using System.Globalization;
using System.Numerics;
using HKFBX.Model;

namespace HKFBX.Hkx;

/// <summary>
/// One clip's entry in Skyrim's animation data.
/// </summary>
public sealed record MotionEntry(int ClipId, RootMotion Motion);

/// <summary>
/// Reads root motion out of Skyrim's <c>animationdatasinglefile.txt</c>.
/// </summary>
/// <remarks>
/// Root motion is not in the .hkx. It lives in the animation data that ships
/// beside the behaviour graphs, as a block per clip:
///
///     13              the clip's index within the project
///     1.0             how long the clip runs
///     1               how many translation keys follow
///     1.0 0 251.9 0   time, then x y z
///     1               how many rotation keys follow
///     1.0 0 0 0 1     time, then x y z w
///
/// The file opens with a count and that many project names, then the projects
/// themselves, each holding clip definitions and then its motion blocks. Rather
/// than model all of that, this scans for blocks that parse as the shape above,
/// which is unambiguous enough to pick them out and leaves the reader unbothered
/// by the parts of the format it has no use for.
///
/// Most blocks hold a single key at the clip's end carrying nothing, which is how
/// a clip says it does not travel. <see cref="RootMotion.HasMovement"/> tells
/// those apart from the ones that do.
/// </remarks>
public static class AnimationDataFile
{
    /// <summary>Every motion block in the file, in the order they appear.</summary>
    public static IReadOnlyList<MotionEntry> ReadMotions(string path) =>
        ParseMotions(File.ReadAllLines(path));

    /// <summary>Every motion block in a fragment of the format.</summary>
    public static IReadOnlyList<MotionEntry> ParseMotions(string text) =>
        ParseMotions(text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray());

    private static IReadOnlyList<MotionEntry> ParseMotions(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var motions = new List<MotionEntry>();
        int at = 0;

        while (at < lines.Count)
        {
            MotionEntry? entry = TryReadBlock(lines, ref at);
            if (entry is not null) motions.Add(entry);
            else at++;
        }

        return motions;
    }

    /// <summary>
    /// Reads a block if one starts here, leaving <paramref name="at"/> past it.
    /// </summary>
    /// <remarks>
    /// Everything is checked before anything is consumed, so a line that merely
    /// looks like the start of a block does not swallow the lines after it.
    /// </remarks>
    private static MotionEntry? TryReadBlock(IReadOnlyList<string> lines, ref int at)
    {
        int cursor = at;

        if (!TryInt(lines, cursor++, out int clipId) || clipId < 0) return null;
        if (!TryFloat(lines, cursor++, out float duration)) return null;
        if (!TryInt(lines, cursor++, out int translationCount) || translationCount < 0) return null;

        var translations = new List<TranslationKey>(translationCount);
        for (int i = 0; i < translationCount; i++)
        {
            if (!TryNumbers(lines, cursor++, 4, out float[] v)) return null;
            translations.Add(new TranslationKey(v[0], new Vector3(v[1], v[2], v[3])));
        }

        if (!TryInt(lines, cursor++, out int rotationCount) || rotationCount < 0) return null;

        var rotations = new List<RotationKey>(rotationCount);
        for (int i = 0; i < rotationCount; i++)
        {
            if (!TryNumbers(lines, cursor++, 5, out float[] v)) return null;
            rotations.Add(new RotationKey(v[0], new Quaternion(v[1], v[2], v[3], v[4])));
        }

        // A block of nothing at all is every pair of integers in the file; it
        // carries no motion and claiming it would consume unrelated lines.
        if (translationCount == 0 && rotationCount == 0) return null;

        at = cursor;

        return new MotionEntry(clipId, new RootMotion
        {
            Duration = duration,
            Translations = translations,
            Rotations = rotations,
        });
    }

    private static bool TryInt(IReadOnlyList<string> lines, int index, out int value)
    {
        value = 0;
        return index < lines.Count
            && int.TryParse(lines[index].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryFloat(IReadOnlyList<string> lines, int index, out float value)
    {
        value = 0;
        return index < lines.Count
            && float.TryParse(lines[index].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryNumbers(IReadOnlyList<string> lines, int index, int count, out float[] values)
    {
        values = [];
        if (index >= lines.Count) return false;

        string[] parts = lines[index].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != count) return false;

        var parsed = new float[count];
        for (int i = 0; i < count; i++)
        {
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out parsed[i]))
                return false;
        }

        values = parsed;
        return true;
    }

    /// <summary>Writes a motion block in the same format, which the tests round trip.</summary>
    public static string WriteMotion(MotionEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var text = new System.Text.StringBuilder();
        var culture = CultureInfo.InvariantCulture;

        text.Append(entry.ClipId.ToString(culture)).Append('\n');
        text.Append(entry.Motion.Duration.ToString("R", culture)).Append('\n');

        text.Append(entry.Motion.Translations.Count.ToString(culture)).Append('\n');
        foreach (TranslationKey k in entry.Motion.Translations)
        {
            text.Append(k.Time.ToString("R", culture)).Append(' ')
                .Append(k.Value.X.ToString("R", culture)).Append(' ')
                .Append(k.Value.Y.ToString("R", culture)).Append(' ')
                .Append(k.Value.Z.ToString("R", culture)).Append('\n');
        }

        text.Append(entry.Motion.Rotations.Count.ToString(culture)).Append('\n');
        foreach (RotationKey k in entry.Motion.Rotations)
        {
            text.Append(k.Time.ToString("R", culture)).Append(' ')
                .Append(k.Value.X.ToString("R", culture)).Append(' ')
                .Append(k.Value.Y.ToString("R", culture)).Append(' ')
                .Append(k.Value.Z.ToString("R", culture)).Append(' ')
                .Append(k.Value.W.ToString("R", culture)).Append('\n');
        }

        return text.ToString();
    }
}
