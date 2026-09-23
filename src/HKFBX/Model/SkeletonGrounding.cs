using System.Numerics;

namespace HKFBX.Model;

/// <summary>What grounding a rig changed.</summary>
/// <param name="Rig">The rig as it now stands.</param>
/// <param name="Lift">How far everything was raised, in units.</param>
/// <param name="Lowest">The bone that was lowest, which is what the lift was measured from.</param>
/// <param name="RootAdded">Whether a root bone was put at the origin for it.</param>
/// <param name="Shift">How far it was moved sideways to stand over the origin.</param>
/// <param name="Turn">How far it was turned, in degrees, to face the way the game faces.</param>
public sealed record GroundedSkeleton(
    Skeleton Rig, float Lift, string Lowest, bool RootAdded, Vector2 Shift = default, float Turn = 0f)
{
    public override string ToString()
    {
        string said = $"raised {Lift:F1} units off '{Lowest}'";
        if (Shift != default) said += $", moved ({Shift.X:F1}, {Shift.Y:F1})";
        if (Math.Abs(Turn) > 0.5f) said += $", turned {Turn:F0} degrees";
        return RootAdded ? said + ", under a root of its own" : said;
    }
}

/// <summary>
/// Puts a rig on the ground, under a root that stays there.
/// </summary>
/// <remarks>
/// <para>
/// A Skyrim creature is authored standing on the origin: its root bone is at it and its
/// lowest bone is on it. All 52 of the game's creature skeletons have their lowest bone
/// at or below one unit and not one above it.
/// </para>
/// <para>
/// A rig authored for another engine usually is not. A Biped from 3ds Max is built
/// around its pelvis, so its root <em>is</em> the pelvis and the character hangs below
/// the floor by the length of its legs -- a skeleton bought from a marketplace has its
/// root half a unit above the origin and its toes 37.5 below.
/// </para>
/// <para>
/// Lifting the old root's rest pose does not fix that, because an animation drives the
/// root and overrides its rest. So the lift goes on a bone nothing animates: a root at
/// the origin, which is what root motion moves, and under it a bone holding the lift,
/// and under that the rig as it was. Every clip then plays where it always did, a
/// whole creature's height further up.
/// </para>
/// </remarks>
public static class SkeletonGrounding
{
    /// <summary>The name Skyrim's own creatures give the bone that stays on the ground.</summary>
    public const string RootName = "NPC Root [Root]";

    /// <summary>
    /// How far a rig would have to turn about the vertical to face the way Skyrim's creatures
    /// face, read off one of its feet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A toe is in front of the ankle it hangs from, so the offset between them says which way
    /// a creature looks. Measured on the game's own rigs it comes out the same every time: the
    /// human's left toe is 8.5 units along +Y from its ankle, the draugr's 11.1, and Skyrim
    /// faces +Y. A bought rig need not, and one that does not walks backwards -- the inference
    /// reads its walk travelling at 180 degrees, which is the direction it is looking in.
    /// </para>
    /// <para>
    /// Zero when the two bones are not there or sit on top of each other, since a rig that
    /// cannot be measured is better left alone than turned by a guess.
    /// </para>
    /// </remarks>
    /// <param name="rig">The rig to measure.</param>
    /// <param name="feet">
    /// A bone at the back of a foot and one at the front of the same foot, a pair per foot.
    /// Give both feet: a toe points inwards as well as forwards, and one foot alone reads
    /// that splay as though it were the way the creature is looking. Skyrim's own human has
    /// its left toe 15.7 degrees off straight ahead, and its right toe the same the other
    /// way, so the two together come out straight and either one alone does not.
    /// </param>
    public static float TurnToFaceForward(Skeleton rig, params (string Ankle, string Toe)[] feet)
    {
        ArgumentNullException.ThrowIfNull(rig);
        ArgumentNullException.ThrowIfNull(feet);

        Vector3[] standing = World(rig);
        var looking = Vector2.Zero;

        foreach ((string ankle, string toe) in feet)
        {
            int back = IndexOf(rig, ankle), front = IndexOf(rig, toe);
            if (back < 0 || front < 0) continue;

            Vector2 along = new(standing[front].X - standing[back].X, standing[front].Y - standing[back].Y);

            // Normalised, so a longer foot does not outvote a shorter one.
            if (along.LengthSquared() > 1e-6f) looking += Vector2.Normalize(along);
        }

        if (looking.LengthSquared() < 1e-6f) return 0f;

        // The angle from where it looks round to +Y, which is where the game's creatures look.
        return (float)(Math.Atan2(looking.X, looking.Y) * 180.0 / Math.PI);
    }

    private static int IndexOf(Skeleton rig, string name)
    {
        for (int i = 0; i < rig.Count; i++)
            if (string.Equals(rig.Bones[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>Raises a rig until its lowest bone rests on the origin.</summary>
    /// <param name="rig">The rig to ground.</param>
    /// <param name="rootName">What to call the root put at the origin.</param>
    /// <param name="liftName">
    /// What to call the bone that holds the lift. It exists because the bone beneath it
    /// is animated and this one must not be.
    /// </param>
    /// <param name="centre">
    /// Whether to bring the rig over the origin sideways as well as vertically. A creature is
    /// authored standing on its own root, and one built somewhere else in its scene stands
    /// that far from wherever the game puts it.
    /// </param>
    /// <param name="turnDegrees">
    /// How far to turn the rig about the vertical, which <see cref="TurnToFaceForward"/>
    /// measures. Skyrim's creatures face +Y and one that does not walks backwards.
    /// </param>
    public static GroundedSkeleton Ground(
        Skeleton rig,
        string rootName = RootName,
        string liftName = "Ground",
        bool centre = false,
        float turnDegrees = 0f)
    {
        ArgumentNullException.ThrowIfNull(rig);
        if (rig.Count == 0) throw new ArgumentException("a rig with no bones cannot be grounded", nameof(rig));

        Vector3[] standing = World(rig);
        int lowest = 0;
        for (int i = 1; i < standing.Length; i++)
            if (standing[i].Z < standing[lowest].Z) lowest = i;

        float lift = -standing[lowest].Z;

        // The rig stands over whatever its own root stands over, which need not be the origin.
        Vector2 shift = centre && standing.Length > 0
            ? new Vector2(-standing[0].X, -standing[0].Y)
            : Vector2.Zero;

        // Everything the correction does goes on the one bone nothing animates: the sideways
        // move, then the turn, then the lift. Folded into a single transform, since moving
        // before turning is the same as turning and then moving the moved distance round with
        // it -- which is what a bone's transform can say, where three steps in a row cannot.
        Quaternion turn = Quaternion.CreateFromAxisAngle(
            Vector3.UnitZ, turnDegrees * (float)Math.PI / 180f);

        Vector3 moved = Vector3.Transform(new Vector3(shift.X, shift.Y, 0f), turn);
        var correction = new BoneTransform(
            new Vector3(moved.X, moved.Y, moved.Z + lift), turn, Vector3.One);

        var bones = new List<Bone>
        {
            new(rootName, -1, BoneTransform.Identity),
            new(liftName, 0, correction),
        };

        // Everything that was a root hangs off the lift; everything else keeps the
        // parent it had, two places further down the list.
        foreach (Bone bone in rig.Bones)
            bones.Add(bone with { ParentIndex = bone.ParentIndex < 0 ? 1 : bone.ParentIndex + 2 });

        return new GroundedSkeleton(
            new Skeleton { Name = rig.Name, Bones = bones },
            lift,
            rig.Bones[lowest].Name,
            RootAdded: true,
            shift,
            turnDegrees);
    }

    /// <summary>Where every bone stands in the rest pose.</summary>
    private static Vector3[] World(Skeleton rig)
    {
        var world = new Matrix4x4[rig.Count];
        var places = new Vector3[rig.Count];

        for (int i = 0; i < rig.Count; i++)
        {
            BoneTransform rest = rig.Bones[i].ReferencePose;
            Matrix4x4 local = Matrix4x4.CreateScale(rest.Scale)
                * Matrix4x4.CreateFromQuaternion(rest.Rotation)
                * Matrix4x4.CreateTranslation(rest.Translation);

            int parent = rig.Bones[i].ParentIndex;
            world[i] = parent < 0 ? local : local * world[parent];
            places[i] = world[i].Translation;
        }

        return places;
    }
}
