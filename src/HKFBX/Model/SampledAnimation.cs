namespace HKFBX.Model;

/// <summary>
/// An animation as one transform per bone per frame.
/// </summary>
/// <remarks>
/// This is the currency of the whole converter. Havok stores animations as
/// spline curves and FBX stores them as per-component keys, and neither maps onto
/// the other directly; both map onto samples. Going through samples also means
/// the Havok codec and the FBX layer never have to know about each other.
/// </remarks>
public sealed class SampledAnimation
{
    public required int FrameCount { get; init; }

    public required int TrackCount { get; init; }

    /// <summary>Seconds the animation runs for.</summary>
    public required float Duration { get; init; }

    /// <summary>Seconds between frames.</summary>
    public required float FrameDuration { get; init; }

    /// <summary>Frame-major: frame * TrackCount + track.</summary>
    public required BoneTransform[] Transforms { get; init; }

    /// <summary>Frame-major float tracks, which most animations do not have.</summary>
    public float[] Floats { get; init; } = [];

    /// <summary>
    /// Which skeleton bone each transform track drives, from the animation
    /// binding. Empty when the file carried no binding, in which case track i is
    /// taken to drive bone i.
    /// </summary>
    public IReadOnlyList<short> TrackToBone { get; init; } = [];

    public BoneTransform this[int frame, int track] => Transforms[frame * TrackCount + track];

    public int BoneForTrack(int track) =>
        TrackToBone.Count > track ? TrackToBone[track] : track;

    public float TimeOf(int frame) => frame * FrameDuration;
}
