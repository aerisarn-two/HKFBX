using HKFBX.Model;

namespace HKFBX.Codec;

/// <summary>
/// Converts between Havok's spline-compressed animations and plain samples.
/// </summary>
/// <remarks>
/// Behind an interface because the only implementation today runs a native
/// helper. Havok's spline format is proprietary: decoding it is documented well
/// enough to reimplement, but encoding is curve fitting with quantization
/// heuristics, and reproducing Havok's choices well enough for a game to behave
/// is a research project. So the encoder is Havok's own, and a managed decoder
/// can be dropped in later for read-only work without the rest of the converter
/// noticing.
/// </remarks>
public interface IAnimationCodec
{
    SampledAnimation Decompress(SplineAnimationData animation);

    SplineAnimationData Compress(SampledAnimation animation, CompressionSettings? settings = null);
}

/// <summary>
/// What to trade size against accuracy. Null members leave the SDK's defaults.
/// </summary>
/// <remarks>
/// Worth setting: Bethesda did not compress at the defaults, and recompressing a
/// vanilla animation at them can double the block.
/// </remarks>
public sealed record CompressionSettings(float? Tolerance = null, RotationQuantization? Rotation = null);

/// <summary>How rotations are quantized, in the SDK's own numbering.</summary>
public enum RotationQuantization
{
    Polar32 = 0,
    ThreeComp40 = 1,
    ThreeComp48 = 2,
    ThreeComp24 = 3,
    Straight16 = 4,
    Uncompressed = 5,
}
