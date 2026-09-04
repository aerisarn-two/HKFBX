namespace HKFBX.Codec;

/// <summary>
/// The fields of an hkaSplineCompressedAnimation, carried on their own.
/// </summary>
/// <remarks>
/// The compressed block is opaque here on purpose. Havok's spline format is
/// proprietary and only the SDK encodes it credibly, so this project never looks
/// inside <see cref="Data"/> — it reads the fields out of the packfile with HKX2,
/// hands them to the codec, and puts whatever comes back into a new packfile.
/// </remarks>
public sealed class SplineAnimationData
{
    public required int NumFrames { get; init; }
    public required int NumBlocks { get; init; }
    public required int MaxFramesPerBlock { get; init; }
    public required int MaskAndQuantizationSize { get; init; }

    public required float BlockDuration { get; init; }
    public required float BlockInverseDuration { get; init; }
    public required float FrameDuration { get; init; }
    public required float Duration { get; init; }

    public required int TransformTrackCount { get; init; }
    public required int FloatTrackCount { get; init; }

    public required IReadOnlyList<uint> BlockOffsets { get; init; }
    public required IReadOnlyList<uint> FloatBlockOffsets { get; init; }
    public required IReadOnlyList<uint> TransformOffsets { get; init; }
    public required IReadOnlyList<uint> FloatOffsets { get; init; }

    public required byte[] Data { get; init; }
}
