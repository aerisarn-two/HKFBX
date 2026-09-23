using System.Numerics;

namespace HKFBX.Model;

/// <summary>What a clip's feet say the creature is doing, when its root says nothing.</summary>
/// <param name="Speed">Units a second the planted feet ask the creature to travel at.</param>
/// <param name="Heading">
/// Degrees clockwise from straight ahead: 0 forward, 180 back, ±90 the two sides.
/// </param>
/// <param name="PlantedFrames">Frames a foot was found planted, out of the clip's.</param>
/// <param name="Confidence">
/// How much of the clip had a foot planted, from 0 to 1. A walk should be near 1, since
/// something is always down; a jump or a swim is near 0 and its answer means nothing.
/// </param>
public sealed record InferredMotion(
    float Speed, float Heading, int PlantedFrames, float Confidence)
{
    public override string ToString() =>
        $"{Speed:F1} units a second at {Heading:F0} degrees, from {PlantedFrames} planted frames";
}

/// <summary>
/// Reads the travel of an in-place animation off its feet.
/// </summary>
/// <remarks>
/// <para>
/// An animation made for an engine that moves the actor in code carries no root
/// motion: a skeleton bought from a marketplace walks 0.33 units over its whole walk
/// cycle, which is hip sway. Skyrim moves an actor from the cache's motion block
/// alone, so such a clip is a creature that slides along without walking.
/// </para>
/// <para>
/// The animation does say how fast it should go, though, just not where anyone thought
/// to look. A foot on the ground does not move; the world moves past it. So in an
/// in-place cycle the planted foot slides <em>backwards</em> relative to the root at
/// exactly the speed the creature should be travelling forwards, and the mirror of
/// that slide is the travel the clip wants.
/// </para>
/// <para>
/// Which foot is planted is read the same way: the lowest one, and only while it is
/// near the lowest that foot gets in this clip. A walk has one down at every instant
/// and reads at full confidence; a run has a flight phase and reads a little under; a
/// jump has almost nothing down and says so rather than answering wrongly.
/// </para>
/// <para>
/// Only the travel is inferred, not the turn. One planted foot cannot tell a creature
/// walking forward from one turning about a distant centre -- both slide the foot the
/// same way -- and a straight walk read for turn comes back with tens of degrees a
/// second of nonsense, because a foot passing from front to back at a lateral offset
/// sweeps an angle about the root. A turn is authored instead
/// (<c>HKSK.Assembly.SyntheticMotion</c>).
/// </para>
/// </remarks>
public static class FootMotion
{
    /// <summary>
    /// What the feet say, or null where no foot is ever planted long enough to say it.
    /// </summary>
    /// <param name="animation">The clip, sampled.</param>
    /// <param name="skeleton">The rig it is over.</param>
    /// <param name="feet">
    /// The bones that touch the ground, by name. Two for a biped, four for a quadruped;
    /// the toes read better than the ankles where a rig has both.
    /// </param>
    /// <param name="plantedWithin">
    /// How far above the lowest a foot gets it may be and still count as planted, as a
    /// fraction of how far that foot rises over the clip. A foot lifts and falls by the
    /// same amount every step, so a fraction reads the same on a mouse and a mammoth
    /// where a distance in units does not.
    /// </param>
    public static InferredMotion? Infer(
        SampledAnimation animation, Skeleton skeleton, IReadOnlyList<string> feet, float plantedWithin = 0.35f)
    {
        ArgumentNullException.ThrowIfNull(animation);
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(feet);

        if (animation.FrameCount < 2 || feet.Count == 0) return null;

        var tracked = feet
            .Select(name => skeleton.Bones.Select((b, i) => (b.Name, Index: i))
                .FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)).Index)
            .Where(i => i > 0)
            .Distinct()
            .ToList();

        if (tracked.Count == 0) return null;

        // Where every tracked foot is on every frame, in the root's frame of reference,
        // which is where "the world slides past it" can be seen.
        var paths = tracked.ToDictionary(i => i, i => new Vector3[animation.FrameCount]);
        for (int frame = 0; frame < animation.FrameCount; frame++)
        {
            Matrix4x4[] world = Pose(animation, skeleton, frame);
            foreach (int bone in tracked) paths[bone][frame] = world[bone].Translation;
        }

        // Each foot against its own rise and fall, so the reading does not depend on the
        // creature's size or on one foot being shorter than the other.
        var ceiling = tracked.ToDictionary(bone => bone, bone =>
        {
            float low = paths[bone].Min(p => p.Z), high = paths[bone].Max(p => p.Z);
            return low + MathF.Max(high - low, 0f) * plantedWithin;
        });

        var slides = new List<Vector3>();
        int planted = 0, previous = -1;

        for (int frame = 1; frame < animation.FrameCount; frame++)
        {
            // The foot that is down is the lowest one, and it only counts while it is
            // near the lowest the clip ever puts a foot.
            int? down = null;
            float lowest = float.MaxValue;
            foreach (int bone in tracked)
            {
                float height = (paths[bone][frame].Z + paths[bone][frame - 1].Z) / 2f;
                if (height >= lowest) continue;
                lowest = height;
                down = bone;
            }

            if (down is not { } foot || lowest > ceiling[foot]) { previous = -1; continue; }

            planted++;

            // The frame a creature changes feet is not a slide: the foot that has just
            // come down was in the air a frame ago, and the distance between the two is
            // the step it took, not the ground going past. Counted as planted, since it
            // is, and left out of the travel, which it is not part of.
            if (foot != previous) { previous = foot; continue; }

            Vector3 slide = paths[foot][frame] - paths[foot][frame - 1];
            slide.Z = 0f;
            slides.Add(-slide);
        }

        if (planted == 0 || slides.Count == 0) return null;

        Vector3 travel = slides.Aggregate(Vector3.Zero, (a, b) => a + b);

        // The travel was measured over the frames a foot stayed down, which are a part
        // of the clip, so the speed is over that part and not over the whole.
        float seen = slides.Count * animation.FrameDuration;
        float speed = seen > 0 ? travel.Length() / seen : 0f;
        float heading = travel.LengthSquared() > 0 ? MathF.Atan2(travel.X, travel.Y) * 180f / MathF.PI : 0f;

        return new InferredMotion(speed, heading, planted, planted / (float)(animation.FrameCount - 1));
    }

    /// <summary>Every bone's place in the world on one frame.</summary>
    private static Matrix4x4[] Pose(SampledAnimation animation, Skeleton skeleton, int frame)
    {
        var world = new Matrix4x4[skeleton.Count];

        for (int bone = 0; bone < skeleton.Count; bone++)
        {
            int track = Array.IndexOf(TrackOf(animation, skeleton), bone);
            BoneTransform local = track >= 0 && track < animation.TrackCount
                ? animation[frame, track]
                : skeleton.Bones[bone].ReferencePose;

            Matrix4x4 at = Matrix4x4.CreateScale(local.Scale)
                * Matrix4x4.CreateFromQuaternion(local.Rotation)
                * Matrix4x4.CreateTranslation(local.Translation);

            int parent = skeleton.Bones[bone].ParentIndex;
            world[bone] = parent < 0 ? at : at * world[parent];
        }

        return world;
    }

    private static int[] TrackOf(SampledAnimation animation, Skeleton skeleton)
    {
        var bones = new int[animation.TrackCount];
        for (int track = 0; track < animation.TrackCount; track++) bones[track] = animation.BoneForTrack(track);
        return bones;
    }
}
