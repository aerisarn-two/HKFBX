using HKFBX.Model;

namespace HKFBX.Hkx;

/// <summary>
/// A clip generator: a named way of playing one animation, which is what a
/// behaviour graph actually refers to.
/// </summary>
/// <remarks>
/// The name is the behaviour's, not the animation's, and the two need not agree:
/// several clips commonly play the same animation at different speeds or cropped
/// differently. <see cref="CacheIndex"/> is what a <see cref="MotionEntry"/>
/// points at, and is how a clip finds its root motion.
/// </remarks>
public sealed record ClipEntry(
    string Name,
    int CacheIndex,
    float PlaybackSpeed,
    float CropStart,
    float CropEnd,
    IReadOnlyList<AnimationEvent> Events);

/// <summary>
/// One project's entry in the animation data: its clips, and the root motion
/// belonging to them.
/// </summary>
/// <remarks>
/// A project is a creature or a piece of equipment — <c>ChickenProject.txt</c>,
/// <c>BowProject.txt</c> — and owns the behaviour graph, character and skeleton
/// named in <see cref="Assets"/>. Only some carry an animation cache; the rest
/// state their clips and no motion at all.
/// </remarks>
public sealed record AnimationProject(
    string Name,
    IReadOnlyList<string> Assets,
    bool HasCache,
    IReadOnlyList<ClipEntry> Clips,
    IReadOnlyList<MotionEntry> Motions)
{
    /// <summary>The root motion a named clip carries, or none if it carries any.</summary>
    /// <remarks>
    /// Clips are matched without regard to case, since the names in the animation
    /// data and the names of the files on disk disagree about it freely.
    /// </remarks>
    public RootMotion MotionFor(string clipName)
    {
        ClipEntry? clip = Clip(clipName);
        if (clip is null) return RootMotion.None;

        foreach (MotionEntry entry in Motions)
            if (entry.ClipId == clip.CacheIndex) return entry.Motion;

        return RootMotion.None;
    }

    /// <summary>The named clip, or null.</summary>
    public ClipEntry? Clip(string clipName) =>
        Clips.FirstOrDefault(c => string.Equals(c.Name, clipName, StringComparison.OrdinalIgnoreCase));
}
