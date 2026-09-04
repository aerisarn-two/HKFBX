using HKFBX.Codec;
using HKFBX.Fbx;
using HKFBX.Hkx;
using HKFBX.Model;
using LeanMeshIO;
using LeanMeshIO.Formats.Fbx;

namespace HKFBX.Tests;

/// <summary>
/// The Havok half, against real files.
/// </summary>
/// <remarks>
/// These need mopper.exe, which the Mopper.Native package copies beside the
/// build output, and Wine when the host is not Windows. They also need real
/// animations, so they skip without a corpus.
/// </remarks>
public class CodecTests
{
    private static readonly MopperAnimationCodec Codec = new();

    [CorpusFact]
    public void DecompressesARealAnimation()
    {
        (string animationPath, _) = Corpus.Chicken;

        (SplineAnimationData spline, _, _) = HkxAnimationFile.ReadAnimation(animationPath);
        SampledAnimation samples = Codec.Decompress(spline);

        Assert.Equal(spline.NumFrames, samples.FrameCount);
        Assert.Equal(spline.TransformTrackCount, samples.TrackCount);
        Assert.Equal(spline.NumFrames * spline.TransformTrackCount, samples.Transforms.Length);

        // Havok's sampler leaves quantization error in the rotations and mopper
        // normalizes it out, so every rotation should be a unit quaternion.
        foreach (BoneTransform t in samples.Transforms)
        {
            Assert.True(float.IsFinite(t.Translation.X));
            Assert.Equal(1f, t.Rotation.Length(), 3);
        }
    }

    [CorpusFact]
    public void SurvivesADecompressCompressDecompressCycle()
    {
        (string animationPath, _) = Corpus.Chicken;

        (SplineAnimationData spline, _, _) = HkxAnimationFile.ReadAnimation(animationPath);

        SampledAnimation first = Codec.Decompress(spline);
        SplineAnimationData packed = Codec.Compress(first);
        SampledAnimation second = Codec.Decompress(packed);

        Assert.Equal(first.FrameCount, second.FrameCount);
        Assert.Equal(first.TrackCount, second.TrackCount);

        double worstTranslation = 0, worstRotation = 0;

        for (int i = 0; i < first.Transforms.Length; i++)
        {
            BoneTransform a = first.Transforms[i];
            BoneTransform b = second.Transforms[i];

            worstTranslation = Math.Max(worstTranslation, (a.Translation - b.Translation).Length());

            // A quaternion and its negation are the same orientation, so compare
            // whichever of the two is closer.
            float same = (a.Rotation - b.Rotation).Length();
            float flipped = (a.Rotation + b.Rotation).Length();
            worstRotation = Math.Max(worstRotation, Math.Min(same, flipped));
        }

        // Lossy by design: the codec quantizes. These bounds are roughly ten
        // times what the corpus actually produces, so they catch a broken round
        // trip without failing on ordinary compression error.
        Assert.True(worstTranslation < 0.05, $"translation drifted {worstTranslation}");
        Assert.True(worstRotation < 0.05, $"rotation drifted {worstRotation}");
    }

    [CorpusFact]
    public void CompressesToASensibleSize()
    {
        (string animationPath, _) = Corpus.Chicken;

        (SplineAnimationData spline, _, _) = HkxAnimationFile.ReadAnimation(animationPath);
        SplineAnimationData packed = Codec.Compress(Codec.Decompress(spline));

        Assert.NotEmpty(packed.Data);
        Assert.Equal(spline.NumFrames, packed.NumFrames);
        Assert.Equal(spline.TransformTrackCount, packed.TransformTrackCount);

        // Bethesda did not compress at the SDK's defaults, so this is not
        // expected to match; it is here to catch the block coming back absurd.
        Assert.True(packed.Data.Length < spline.Data.Length * 10,
            $"recompressed to {packed.Data.Length} bytes from {spline.Data.Length}");
    }
}

/// <summary>
/// The whole pipeline: a real animation and its skeleton, out as an FBX, read
/// back and checked.
/// </summary>
public class ConversionTests
{
    [CorpusFact]
    public void ConvertsARealAnimationToAPlayableFbx()
    {
        (string animationPath, string skeletonPath) = Corpus.Chicken;

        (SplineAnimationData spline, IReadOnlyList<short> trackToBone, _) =
            HkxAnimationFile.ReadAnimation(animationPath);

        Skeleton skeleton = HkxAnimationFile.ReadSkeleton(skeletonPath);
        SampledAnimation sampled = new MopperAnimationCodec().Decompress(spline);

        var animation = new SampledAnimation
        {
            FrameCount = sampled.FrameCount,
            TrackCount = sampled.TrackCount,
            Duration = sampled.Duration,
            FrameDuration = sampled.FrameDuration,
            Transforms = sampled.Transforms,
            TrackToBone = trackToBone,
        };

        FbxDocument document = FbxAnimationWriter.Build(skeleton, animation, "aggrowarning1");

        using var stream = new MemoryStream();
        document.Save(stream);
        stream.Position = 0;
        FbxDocument reloaded = FbxDocument.Load(stream);

        List<FbxNode> objects = reloaded["Objects"]!.Nodes;

        Assert.Equal(skeleton.Count, objects.Count(n => n.Name == "Model"));
        Assert.Single(objects.Where(n => n.Name == "AnimationStack"));
        Assert.Single(objects.Where(n => n.Name == "AnimationLayer"));

        int curveNodes = objects.Count(n => n.Name == "AnimationCurveNode");
        int curves = objects.Count(n => n.Name == "AnimationCurve");

        Assert.True(curveNodes > 0, "no curve nodes were written");
        Assert.Equal(curveNodes * 3, curves);

        // Every track the binding maps onto a real bone gets translation,
        // rotation and scaling.
        int driven = Enumerable.Range(0, animation.TrackCount)
            .Count(t => animation.BoneForTrack(t) >= 0 && animation.BoneForTrack(t) < skeleton.Count);

        Assert.Equal(driven * 3, curveNodes);
    }

    [CorpusFact]
    public void ReadsTheSkeletonHierarchy()
    {
        (_, string skeletonPath) = Corpus.Chicken;

        Skeleton skeleton = HkxAnimationFile.ReadSkeleton(skeletonPath);

        Assert.NotEmpty(skeleton.Bones);
        Assert.Single(skeleton.Roots());
        Assert.All(skeleton.Bones, b => Assert.False(string.IsNullOrWhiteSpace(b.Name)));

        // A parent always precedes its child in Havok's ordering, which is what
        // lets a single pass build the hierarchy.
        for (int i = 0; i < skeleton.Count; i++)
            Assert.True(skeleton.Bones[i].ParentIndex < i);
    }
}
