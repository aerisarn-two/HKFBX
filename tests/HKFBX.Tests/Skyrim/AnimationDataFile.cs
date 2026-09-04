using System.Globalization;
using System.Numerics;
using HKFBX.Model;

namespace HKFBX.Tests.Skyrim;

/// <summary>
/// One clip's entry in Skyrim's animation data.
/// </summary>
public sealed record MotionEntry(int ClipId, RootMotion Motion);

/// <summary>
/// Reads root motion out of Skyrim's <c>animationdatasinglefile.txt</c>.
/// </summary>
/// <remarks>
/// Root motion is not in the .hkx. It lives in the animation data that ships
/// beside the behaviour graphs, in a file that is a sequence of counted blocks:
///
///     429                         how many projects follow
///     ChickenProject.txt          their names
///     ...
///     328                         how many lines the first project's block holds
///     1                           whether asset paths follow
///     3                           how many
///     Behaviors\ChickenBehavior.hkx
///     ...
///     1                           whether an animation cache follows
///     MainIdle                    a clip generator's name
///     12                          its cache index
///     1                           playback speed
///     0                           cropped from the start
///     0                           cropped from the end
///     1                           how many events follow
///     clipEnd:6.65767             an event, and when it fires
///                                 a blank line ends the clip
///
/// A project that has a cache is followed by a second counted block holding its
/// motion, one entry per clip that has any:
///
///     13                          the cache index this belongs to
///     1.0                         how long the clip runs
///     1                           how many translation keys follow
///     1.0 0 251.9 0               time, then x y z
///     1                           how many rotation keys follow
///     1.0 0 0 0 1                 time, then x y z w
///
/// <see cref="ReadProjects"/> reads all of that, and is exact: it consumes the
/// file to its last line or throws saying where it stopped. That matters because
/// a motion entry names only a cache index, so without the clips it points into
/// there is no way to say which animation a motion belongs to.
///
/// Most entries hold a single key at the clip's end carrying the identity, which
/// is how a clip says it does not travel. <see cref="RootMotion.HasMovement"/>
/// tells those apart from the ones that do.
/// </remarks>
public static class AnimationDataFile
{
    /// <summary>Every project in the file, with its clips and their motion.</summary>
    /// <exception cref="InvalidDataException">
    /// The file does not follow the format, naming the line it stopped at.
    /// </exception>
    public static IReadOnlyList<AnimationProject> ReadProjects(string path) =>
        ParseProjects(File.ReadAllText(path));

    /// <summary>Every project in a fragment of the format.</summary>
    /// <inheritdoc cref="ReadProjects" path="/exception"/>
    public static IReadOnlyList<AnimationProject> ParseProjects(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var lines = new Cursor(text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray());

        int count = lines.Int("the project count");
        var names = new string[count];
        for (int i = 0; i < count; i++) names[i] = lines.Line();

        var projects = new List<AnimationProject>(count);

        foreach (string name in names)
        {
            Cursor block = lines.Counted($"the block for {name}");

            bool hasAssets = block.Int("whether asset paths follow") == 1;
            var assets = new List<string>();

            if (hasAssets)
            {
                int assetCount = block.Int("the asset count");
                for (int i = 0; i < assetCount; i++) assets.Add(block.Line());
            }

            bool hasCache = block.Int("whether a cache follows") == 1;
            var clips = new List<ClipEntry>();

            if (hasCache)
            {
                while (!block.Done)
                {
                    clips.Add(ReadClip(block));
                    if (!block.Done) block.Line();   // the blank line ending the clip
                }
            }

            var motions = new List<MotionEntry>();

            if (hasCache)
            {
                Cursor data = lines.Counted($"the motion for {name}");

                while (!data.Done)
                {
                    motions.Add(ReadMotion(data));
                    if (!data.Done) data.Line();
                }
            }

            projects.Add(new AnimationProject(name, assets, hasCache, clips, motions));
        }

        return projects;
    }

    private static ClipEntry ReadClip(Cursor block)
    {
        string name = block.Line();
        int cacheIndex = block.Int("a clip's cache index");
        float speed = block.Float("a clip's playback speed");
        float cropStart = block.Float("a clip's start crop");
        float cropEnd = block.Float("a clip's end crop");

        int eventCount = block.Int("a clip's event count");
        var events = new List<AnimationEvent>(eventCount);

        for (int i = 0; i < eventCount; i++)
        {
            // "SoundPlay.NPCChickenScratch:0.333333" — the text may itself hold a
            // colon, so the time is taken from the last one.
            string line = block.Line();
            int split = line.LastIndexOf(':');

            if (split < 0 || !float.TryParse(line.AsSpan(split + 1), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out float time))
            {
                throw block.Fail($"an event of clip {name}", line);
            }

            events.Add(new AnimationEvent(time, line[..split]));
        }

        return new ClipEntry(name, cacheIndex, speed, cropStart, cropEnd, events);
    }

    private static MotionEntry ReadMotion(Cursor data)
    {
        int cacheIndex = data.Int("a motion's cache index");
        float duration = data.Float("a motion's duration");

        int translationCount = data.Int("a translation key count");
        var translations = new List<TranslationKey>(translationCount);

        for (int i = 0; i < translationCount; i++)
        {
            float[] v = data.Numbers(4, "a translation key");
            translations.Add(new TranslationKey(v[0], new Vector3(v[1], v[2], v[3])));
        }

        int rotationCount = data.Int("a rotation key count");
        var rotations = new List<RotationKey>(rotationCount);

        for (int i = 0; i < rotationCount; i++)
        {
            float[] v = data.Numbers(5, "a rotation key");
            rotations.Add(new RotationKey(v[0], new Quaternion(v[1], v[2], v[3], v[4])));
        }

        return new MotionEntry(cacheIndex, new RootMotion
        {
            Duration = duration,
            Translations = translations,
            Rotations = rotations,
        });
    }

    /// <summary>
    /// A position in the lines, which reports where it gave up rather than
    /// silently returning less than the file holds.
    /// </summary>
    private sealed class Cursor(IReadOnlyList<string> lines, int start = 0, int end = -1)
    {
        private readonly int _end = end < 0 ? lines.Count : end;
        private int _at = start;

        public bool Done => _at >= _end;

        public string Line() =>
            _at < _end ? lines[_at++] : throw Fail("another line", "the end of the block");

        public int Int(string what) =>
            int.TryParse(Peek().Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
                ? Take(v)
                : throw Fail(what, Peek());

        public float Float(string what) =>
            float.TryParse(Peek().Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
                ? Take(v)
                : throw Fail(what, Peek());

        public float[] Numbers(int count, string what)
        {
            string line = Peek();
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length != count) throw Fail(what, line);

            var values = new float[count];
            for (int i = 0; i < count; i++)
            {
                if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                    throw Fail(what, line);
            }

            return Take(values);
        }

        /// <summary>A block of as many lines as its first line says.</summary>
        public Cursor Counted(string what)
        {
            int count = Int(what);
            if (_at + count > _end) throw Fail(what, $"{count} lines, but only {_end - _at} remain");

            var block = new Cursor(lines, _at, _at + count);
            _at += count;

            return block;
        }

        public InvalidDataException Fail(string what, string saw) =>
            new($"line {_at + 1}: expected {what}, saw '{saw}'");

        private string Peek() => _at < _end ? lines[_at] : throw Fail("another line", "the end of the block");

        private T Take<T>(T value)
        {
            _at++;
            return value;
        }
    }

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
