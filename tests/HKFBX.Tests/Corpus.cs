namespace HKFBX.Tests;

/// <summary>
/// Locates the real .hkx files some tests need.
/// </summary>
/// <remarks>
/// Animations are extracted game data: they cannot live in this repository or
/// reach a runner, so they come from HKFBX_CORPUS. Tests that need them skip
/// when it is unset, and the rest of the suite still runs.
///
/// Set it to the meshes directory, e.g.
///     HKFBX_CORPUS=~/Dev/BSAFileExtractor/extracted/meshes dotnet test
/// </remarks>
public static class Corpus
{
    public const string EnvVar = "HKFBX_CORPUS";

    public static string? Root
    {
        get
        {
            string? value = Environment.GetEnvironmentVariable(EnvVar);
            if (string.IsNullOrWhiteSpace(value)) return null;

            string expanded = value.StartsWith("~/")
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), value[2..])
                : value;

            return Directory.Exists(expanded) ? expanded : null;
        }
    }

    public static bool Available => Root is not null;

    public static string Path_(params string[] parts) =>
        Path.Combine([Root ?? throw new InvalidOperationException("no corpus"), .. parts]);

    /// <summary>An animation and the skeleton it was authored against.</summary>
    public static (string Animation, string Skeleton) Chicken => (
        Path_("actors", "ambient", "chicken", "animations", "aggrowarning1.hkx"),
        Path_("actors", "ambient", "chicken", "character assets", "skeleton.hkx"));
}

/// <summary>Skips a test when there is no corpus to run it against.</summary>
public sealed class CorpusFactAttribute : FactAttribute
{
    public CorpusFactAttribute()
    {
        if (!Corpus.Available)
            Skip = $"set {Corpus.EnvVar} to a directory of .hkx files to run this";
    }
}
