using System.Numerics;
using HKFBX.Codec;
using HKFBX.Model;

namespace HKFBX.Tests;

/// <summary>
/// The formats mopper reads and writes. These are the seam between managed code
/// and the native codec, so a field written in the wrong order here shows up as
/// a nonsensical animation rather than as an error.
/// </summary>
public class InterchangeTests
{
    [Fact]
    public void SplineFieldsSurviveARoundTrip()
    {
        var original = new SplineAnimationData
        {
            NumFrames = 41,
            NumBlocks = 2,
            MaxFramesPerBlock = 256,
            MaskAndQuantizationSize = 132,
            BlockDuration = 8.5f,
            BlockInverseDuration = 0.11764706f,
            FrameDuration = 1f / 30f,
            Duration = 1.3333334f,
            TransformTrackCount = 33,
            FloatTrackCount = 2,
            BlockOffsets = [0u, 4096u],
            FloatBlockOffsets = [7u],
            TransformOffsets = [1u, 2u, 3u],
            FloatOffsets = [],
            Data = [1, 2, 3, 250, 251, 252],
        };

        using var stream = new MemoryStream();
        AnimationInterchange.WriteSpline(stream, original);
        stream.Position = 0;
        SplineAnimationData read = AnimationInterchange.ReadSpline(stream);

        Assert.Equal(original.NumFrames, read.NumFrames);
        Assert.Equal(original.NumBlocks, read.NumBlocks);
        Assert.Equal(original.MaxFramesPerBlock, read.MaxFramesPerBlock);
        Assert.Equal(original.MaskAndQuantizationSize, read.MaskAndQuantizationSize);
        Assert.Equal(original.BlockDuration, read.BlockDuration);
        Assert.Equal(original.BlockInverseDuration, read.BlockInverseDuration);
        Assert.Equal(original.FrameDuration, read.FrameDuration);
        Assert.Equal(original.Duration, read.Duration);
        Assert.Equal(original.TransformTrackCount, read.TransformTrackCount);
        Assert.Equal(original.FloatTrackCount, read.FloatTrackCount);
        Assert.Equal(original.BlockOffsets, read.BlockOffsets);
        Assert.Equal(original.FloatBlockOffsets, read.FloatBlockOffsets);
        Assert.Equal(original.TransformOffsets, read.TransformOffsets);
        Assert.Equal(original.FloatOffsets, read.FloatOffsets);
        Assert.Equal(original.Data, read.Data);
    }

    [Fact]
    public void SamplesSurviveARoundTripBitExactly()
    {
        SampledAnimation original = Synthetic.Animation(frames: 7, tracks: 3);

        using var stream = new MemoryStream();
        AnimationInterchange.WriteSamples(stream, original);
        stream.Position = 0;
        SampledAnimation read = AnimationInterchange.ReadSamples(stream);

        Assert.Equal(original.FrameCount, read.FrameCount);
        Assert.Equal(original.TrackCount, read.TrackCount);
        Assert.Equal(original.Duration, read.Duration);
        Assert.Equal(original.FrameDuration, read.FrameDuration);

        // Bit exact, not approximate: this is a binary format precisely so that
        // nothing is lost on the way to the codec and back.
        for (int i = 0; i < original.Transforms.Length; i++)
        {
            Assert.Equal(original.Transforms[i].Translation, read.Transforms[i].Translation);
            Assert.Equal(original.Transforms[i].Rotation, read.Transforms[i].Rotation);
            Assert.Equal(original.Transforms[i].Scale, read.Transforms[i].Scale);
        }
    }

    [Fact]
    public void RefusesAFileThatIsNotItsOwn()
    {
        using var stream = new MemoryStream("not an animation at all"u8.ToArray());

        Assert.Throws<InvalidDataException>(() => AnimationInterchange.ReadSpline(stream));
    }
}

public class BoneTransformTests
{
    [Theory]
    [InlineData(0f, 0f, 0f)]
    [InlineData(0.3f, 0f, 0f)]
    [InlineData(0f, 0.4f, 0f)]
    [InlineData(0f, 0f, 0.5f)]
    [InlineData(0.3f, -0.4f, 0.5f)]
    public void EulerAnglesRoundTripThroughAQuaternion(float x, float y, float z)
    {
        var euler = new Vector3(x, y, z);
        BoneTransform transform = BoneTransform.FromEulerXyz(Vector3.Zero, euler, Vector3.One);

        Vector3 back = transform.ToEulerXyz();

        // Through a quaternion and out again, so exactness is not on offer; the
        // point is that FBX's per-axis curves describe the same orientation.
        Assert.Equal(euler.X, back.X, 4);
        Assert.Equal(euler.Y, back.Y, 4);
        Assert.Equal(euler.Z, back.Z, 4);
    }
}
