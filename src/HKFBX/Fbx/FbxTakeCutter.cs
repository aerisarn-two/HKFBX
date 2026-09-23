using HKFBX.Model;
using LeanMeshIO;

namespace HKFBX.Fbx;

/// <summary>
/// One clip inside a longer take: what it is called, and the frames it spans.
/// </summary>
/// <param name="Name">The stack the cut is written as.</param>
/// <param name="First">The first frame of the clip, counted from zero.</param>
/// <param name="Last">
/// The last frame of the clip, which is part of it. A table where each clip's end is
/// the next one's start -- the shape Unity and Unreal both show -- therefore gives
/// two neighbours one pose in common, which is what a looping clip wants and what a
/// one-shot needs if it is to end on the pose the animator finished on.
/// </param>
public readonly record struct TakeCut(string Name, int First, int Last);

/// <summary>What the cutting made, and what it could not.</summary>
/// <param name="Cuts">The stacks written, in the order they were asked for.</param>
/// <param name="Refused">A cut that names no frames of the take, with the reason.</param>
public sealed record CutReport(
    IReadOnlyList<CutStack> Cuts,
    IReadOnlyDictionary<string, string> Refused);

/// <summary>One clip as it was written.</summary>
/// <param name="Name">The stack's name.</param>
/// <param name="Frames">Frames it holds, both ends included.</param>
/// <param name="Duration">Seconds it runs for.</param>
/// <param name="BoundBones">
/// How many of the rig's bones the stack drives. Zero is a stack over a rig the
/// document has not got, which reads as an animation and animates nothing.
/// </param>
public sealed record CutStack(string Name, int Frames, float Duration, int BoundBones);

/// <summary>
/// Cuts a single animation take into one stack per clip.
/// </summary>
/// <remarks>
/// <para>
/// An animation is one file per clip in this game and one stack per clip in the
/// exchange, but a creature bought off a marketplace is usually one long take with
/// the clips laid end to end, and a picture of an editor's clip list saying where
/// each one starts and stops. A rig used by an engine that cuts takes for itself --
/// Unity, Unreal -- never needs them separated in the file.
/// </para>
/// <para>
/// The cutting goes through samples rather than through the curves. The take is
/// sampled once at its own frame rate, each clip is the frames it spans copied out
/// and rebased to start at zero, and each is written back as a stack of its own.
/// Nothing is interpolated and no key is moved: a clip's frame <c>n</c> holds the
/// pose the take held at frame <c>First + n</c>, exactly.
/// </para>
/// <para>
/// The take the cuts came from is left where it is. It carries none of the naming a
/// clip needs to be imported as an animation, so whatever reads the document next
/// passes over it, and leaving it costs a reader nothing and keeps the source of
/// every cut in the same file as the cuts.
/// </para>
/// </remarks>
public static class FbxTakeCutter
{
    /// <summary>
    /// Adds a stack per cut to <paramref name="document"/>, taken from one of its takes.
    /// </summary>
    /// <param name="document">The scene, modified in place.</param>
    /// <param name="cuts">The clips to make.</param>
    /// <param name="takeName">
    /// The take to cut. Null takes the document's first, which is the whole of it
    /// for a file that holds one long animation.
    /// </param>
    /// <param name="rig">
    /// The skeleton the take is read over. Null reads the document's own.
    /// </param>
    /// <param name="fps">
    /// Frames a second the cut list is counted in. Null uses the take's own sampling,
    /// which is what a list exported beside the file is counted in.
    /// </param>
    public static CutReport Cut(
        FbxDocument document,
        IReadOnlyList<TakeCut> cuts,
        string? takeName = null,
        Skeleton? rig = null,
        float? fps = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(cuts);

        Skeleton skeleton = rig ?? FbxAnimationReader.ReadSkeleton(document);
        string take = takeName ?? FbxAnimationReader.ReadTakeNames(document).FirstOrDefault()
            ?? throw new InvalidOperationException("the document holds no take to cut");

        SampledAnimation whole = FbxAnimationReader.ReadAnimation(
            document, skeleton, frameCount: null, frameDuration: fps is > 0 ? 1f / fps : null, takeName: take);

        var made = new List<CutStack>();
        var refused = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (TakeCut cut in cuts)
        {
            if (string.IsNullOrWhiteSpace(cut.Name))
            {
                refused[$"{cut.First}-{cut.Last}"] = "the cut has no name";
                continue;
            }

            int first = Math.Max(0, cut.First);
            int last = Math.Min(whole.FrameCount - 1, cut.Last);

            if (last < first)
            {
                refused[cut.Name] = $"frames {cut.First} to {cut.Last} are outside the take's {whole.FrameCount}";
                continue;
            }

            SampledAnimation clip = Slice(whole, first, last);
            int bound = FbxAnimationWriter.AddStack(document, skeleton, clip, cut.Name);
            made.Add(new CutStack(cut.Name, clip.FrameCount, clip.Duration, bound));
        }

        return new CutReport(made, refused);
    }

    /// <summary>The frames from <paramref name="first"/> to <paramref name="last"/>, rebased to zero.</summary>
    private static SampledAnimation Slice(SampledAnimation whole, int first, int last)
    {
        int frames = last - first + 1;
        var transforms = new BoneTransform[frames * whole.TrackCount];
        Array.Copy(whole.Transforms, first * whole.TrackCount, transforms, 0, transforms.Length);

        float[] floats = [];
        if (whole.Floats.Length > 0 && whole.FrameCount > 0)
        {
            int perFrame = whole.Floats.Length / whole.FrameCount;
            floats = new float[frames * perFrame];
            Array.Copy(whole.Floats, first * perFrame, floats, 0, floats.Length);
        }

        // An event belongs to the clip whose frames it falls inside, at the time it
        // falls minus where the clip starts.
        float from = first * whole.FrameDuration, to = last * whole.FrameDuration;
        var annotations = whole.Annotations
            .Select(track => new AnnotationTrack
            {
                Name = track.Name,
                Events = [.. track.Events
                    .Where(e => e.Time >= from && e.Time <= to)
                    .Select(e => e with { Time = e.Time - from })],
            })
            .Where(track => track.Events.Count > 0)
            .ToList();

        return new SampledAnimation
        {
            FrameCount = frames,
            TrackCount = whole.TrackCount,
            Duration = (frames - 1) * whole.FrameDuration,
            FrameDuration = whole.FrameDuration,
            Transforms = transforms,
            Floats = floats,
            Annotations = annotations,
            TrackToBone = whole.TrackToBone,
        };
    }
}
