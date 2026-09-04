using HKFBX.Model;

namespace HKFBX.Tests.Skyrim;

/// <summary>
/// An animation that travels, and the data saying how.
/// </summary>
public sealed record MotionSample(
    string Project,
    ClipEntry Clip,
    RootMotion Motion,
    string AnimationPath,
    string SkeletonPath)
{
    /// <summary>A filename that keeps the pair legible.</summary>
    public string Name
    {
        get
        {
            string name = $"{Project}-{Clip.Name}".Replace(' ', '_');
            foreach (char bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');

            return name;
        }
    }
}

/// <summary>
/// Finds animations in the corpus that carry real root motion, so the conversion
/// can be tested against motion Bethesda authored rather than motion invented
/// here.
/// </summary>
/// <remarks>
/// The link is by name. A clip generator names the animation it plays only
/// implicitly, but a great many clips are named after their animation exactly —
/// <c>TurnCannedL180</c> plays <c>turncannedl180.hkx</c> — and where that holds
/// the pairing is certain enough to test against.
///
/// Which folder to look in comes from the project file: <c>HorseProject.txt</c>
/// sits beside its animations as <c>horseproject.hkx</c>. Matching only within
/// that folder keeps a chicken's clip from pairing with a mudcrab's animation,
/// which a corpus-wide match by name does constantly.
/// </remarks>
public static class MotionSamples
{
    /// <summary>
    /// The clips that travel and name their own animation, best first within each
    /// project, at most <paramref name="perProject"/> from any one of them.
    /// </summary>
    public static IReadOnlyList<MotionSample> Find(string corpus, int perProject = 1)
    {
        IReadOnlyList<AnimationProject> projects =
            AnimationDataFile.ReadProjects(Path.Combine(corpus, "animationdatasinglefile.txt"));

        var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (string file in Directory.EnumerateFiles(corpus, "*.hkx", SearchOption.AllDirectories))
        {
            string stem = Path.GetFileNameWithoutExtension(file);
            if (!index.TryGetValue(stem, out List<string>? all)) index[stem] = all = [];
            all.Add(file);
        }

        var samples = new List<MotionSample>();

        foreach (AnimationProject project in projects.Where(p => p.HasCache))
        {
            string stem = Path.GetFileNameWithoutExtension(project.Name);
            if (!index.TryGetValue(stem, out List<string>? projectFiles)) continue;

            string folder = Path.GetDirectoryName(projectFiles[0])!;

            string? skeleton = Directory
                .EnumerateFiles(folder, "skeleton.hkx", SearchOption.AllDirectories)
                .FirstOrDefault(p => p.Contains("character assets", StringComparison.OrdinalIgnoreCase));

            if (skeleton is null) continue;

            samples.AddRange(project.Clips
                .Select(clip => (Clip: clip, Motion: project.MotionFor(clip.Name)))
                .Where(c => c.Motion.HasMovement)
                .Select(c => (c.Clip, c.Motion, File: Animation(index, folder, c.Clip.Name)))
                .Where(c => c.File is not null)
                .GroupBy(c => c.File, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                // Most travelled first, weighting a turn so that clips which spin
                // in place are not crowded out by ones that merely walk far.
                .OrderByDescending(c => Travel(c.Motion) + 100f * Turn(c.Motion))
                .Take(perProject)
                .Select(c => new MotionSample(stem, c.Clip, c.Motion, c.File!, skeleton)));
        }

        return samples;
    }

    private static string? Animation(Dictionary<string, List<string>> index, string folder, string clip) =>
        index.TryGetValue(clip, out List<string>? all)
            ? all.FirstOrDefault(p =>
                p.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && p.Contains($"{Path.DirectorySeparatorChar}animations{Path.DirectorySeparatorChar}",
                              StringComparison.OrdinalIgnoreCase))
            : null;

    /// <summary>How far the clip carries the character, in game units.</summary>
    public static float Travel(RootMotion motion) =>
        motion.Translations.Count == 0
            ? 0f
            : (motion.Translations[^1].Value - motion.Translations[0].Value).Length();

    /// <summary>How far it turns, in radians.</summary>
    public static float Turn(RootMotion motion)
    {
        if (motion.Rotations.Count == 0) return 0f;

        System.Numerics.Quaternion a = motion.Rotations[0].Value, b = motion.Rotations[^1].Value;
        float dot = Math.Clamp(Math.Abs(System.Numerics.Quaternion.Dot(a, b)), 0f, 1f);

        return 2f * MathF.Acos(dot);
    }
}
