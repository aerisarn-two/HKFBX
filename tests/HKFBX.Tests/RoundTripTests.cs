using System.Numerics;
using HKFBX.Codec;
using HKFBX.Fbx;
using HKFBX.Hkx;
using HKFBX.Model;
using LeanMeshIO;

namespace HKFBX.Tests;

/// <summary>
/// Out to FBX and back again.
/// </summary>
public class RoundTripTests
{
    private static FbxDocument SaveAndLoad(FbxDocument document)
    {
        using var stream = new MemoryStream();
        document.Save(stream);
        stream.Position = 0;
        return FbxDocument.Load(stream);
    }

    [Fact]
    public void SkeletonSurvivesTheRoundTrip()
    {
        Skeleton original = Synthetic.Skeleton();

        FbxDocument document = SaveAndLoad(
            FbxAnimationWriter.Build(original, Synthetic.Animation(), "take"));

        Skeleton read = FbxAnimationReader.ReadSkeleton(document);

        Assert.Equal(original.Count, read.Count);

        // Names and hierarchy, which is what the FBX has to carry for the
        // animation to mean anything.
        for (int i = 0; i < original.Count; i++)
        {
            Assert.Equal(original.Bones[i].Name, read.Bones[i].Name);
            Assert.Equal(original.Bones[i].ParentIndex, read.Bones[i].ParentIndex);
        }
    }

    [Fact]
    public void RestPoseSurvivesTheRoundTrip()
    {
        Skeleton original = Synthetic.Skeleton();

        FbxDocument document = SaveAndLoad(
            FbxAnimationWriter.Build(original, Synthetic.Animation(), "take"));

        Skeleton read = FbxAnimationReader.ReadSkeleton(document);

        for (int i = 0; i < original.Count; i++)
        {
            BoneTransform a = original.Bones[i].ReferencePose;
            BoneTransform b = read.Bones[i].ReferencePose;

            Assert.True((a.Translation - b.Translation).Length() < 1e-4f,
                $"bone {i} translation {a.Translation} became {b.Translation}");
            Assert.True((a.Scale - b.Scale).Length() < 1e-4f,
                $"bone {i} scale {a.Scale} became {b.Scale}");
        }
    }

    [Fact]
    public void AnimationSurvivesTheRoundTrip()
    {
        Skeleton skeleton = Synthetic.Skeleton();
        SampledAnimation original = Synthetic.Animation();

        FbxDocument document = SaveAndLoad(
            FbxAnimationWriter.Build(skeleton, original, "take"));

        Skeleton readSkeleton = FbxAnimationReader.ReadSkeleton(document);
        SampledAnimation read = FbxAnimationReader.ReadAnimation(
            document, readSkeleton, original.FrameCount, original.FrameDuration);

        Assert.Equal(original.FrameCount, read.FrameCount);
        Assert.Equal(skeleton.Count, read.TrackCount);

        double worstTranslation = 0, worstRotation = 0, worstScale = 0;

        for (int frame = 0; frame < original.FrameCount; frame++)
        for (int track = 0; track < original.TrackCount; track++)
        {
            BoneTransform a = original[frame, track];
            BoneTransform b = read[frame, track];

            worstTranslation = Math.Max(worstTranslation, (a.Translation - b.Translation).Length());
            worstScale = Math.Max(worstScale, (a.Scale - b.Scale).Length());

            // Rotation goes out as three Euler curves and comes back as a
            // quaternion, so compare orientations rather than components.
            float same = (a.Rotation - b.Rotation).Length();
            float flipped = (a.Rotation + b.Rotation).Length();
            worstRotation = Math.Max(worstRotation, Math.Min(same, flipped));
        }

        // The keys are floats and the angles go through degrees, so this is not
        // exact; it is well inside anything an animator would notice.
        Assert.True(worstTranslation < 1e-3, $"translation drifted {worstTranslation}");
        Assert.True(worstRotation < 1e-3, $"rotation drifted {worstRotation}");
        Assert.True(worstScale < 1e-3, $"scale drifted {worstScale}");
    }

    [Fact]
    public void TakeNameSurvivesTheRoundTrip()
    {
        FbxDocument document = SaveAndLoad(
            FbxAnimationWriter.Build(Synthetic.Skeleton(), Synthetic.Animation(), "mt_idle"));

        Assert.Equal("mt_idle", FbxAnimationReader.ReadTakeName(document));
    }

    [Fact]
    public void BonesWithNoCurvesHoldTheirRestPose()
    {
        Skeleton skeleton = Synthetic.Skeleton();

        // Two tracks for four bones, so the last two are never keyed.
        SampledAnimation partial = Synthetic.Animation(frames: 10, tracks: 2);

        FbxDocument document = SaveAndLoad(
            FbxAnimationWriter.Build(skeleton, partial, "partial"));

        Skeleton readSkeleton = FbxAnimationReader.ReadSkeleton(document);
        SampledAnimation read = FbxAnimationReader.ReadAnimation(
            document, readSkeleton, partial.FrameCount, partial.FrameDuration);

        // An unkeyed bone still needs a sensible transform, or the skeleton
        // collapses to the origin when the animation is applied.
        for (int frame = 0; frame < read.FrameCount; frame++)
        {
            BoneTransform head = read[frame, 2];
            Assert.True((head.Translation - skeleton.Bones[2].ReferencePose.Translation).Length() < 1e-4f);
        }
    }

    /// <summary>
    /// A curve keyed off the frame grid, as an editor is free to produce.
    /// </summary>
    [Fact]
    public void SamplesCurvesThatDoNotLandOnFrames()
    {
        Skeleton skeleton = Synthetic.Skeleton();

        // Two keys a second apart. Sampling at 30fps has to interpolate.
        var transforms = new BoneTransform[2 * skeleton.Count];
        for (int i = 0; i < transforms.Length; i++) transforms[i] = BoneTransform.Identity;

        transforms[0] = new BoneTransform(Vector3.Zero, Quaternion.Identity, Vector3.One);
        transforms[skeleton.Count] =
            new BoneTransform(new Vector3(10, 0, 0), Quaternion.Identity, Vector3.One);

        var sparse = new SampledAnimation
        {
            FrameCount = 2,
            TrackCount = skeleton.Count,
            Duration = 1f,
            FrameDuration = 1f,
            Transforms = transforms,
        };

        FbxDocument document = SaveAndLoad(FbxAnimationWriter.Build(skeleton, sparse, "sparse"));

        Skeleton readSkeleton = FbxAnimationReader.ReadSkeleton(document);
        SampledAnimation read = FbxAnimationReader.ReadAnimation(
            document, readSkeleton, frameCount: 31, frameDuration: 1f / 30f);

        Assert.Equal(31, read.FrameCount);

        // Half a second in, half way along.
        Assert.Equal(5f, read[15, 0].Translation.X, 2);
        Assert.Equal(10f, read[30, 0].Translation.X, 2);
    }
}

/// <summary>
/// The whole loop against real data: hkx to FBX and back to hkx.
/// </summary>
public class HkxRoundTripTests
{
    [CorpusFact]
    public void AnimationSurvivesHkxToFbxToHkx()
    {
        (string animationPath, string skeletonPath) = Corpus.Chicken;

        (SplineAnimationData spline, IReadOnlyList<short> trackToBone, _) =
            HkxAnimationFile.ReadAnimation(animationPath);

        Skeleton skeleton = HkxAnimationFile.ReadSkeleton(skeletonPath);
        var codec = new MopperAnimationCodec();

        SampledAnimation fromHkx = codec.Decompress(spline);

        var animation = new SampledAnimation
        {
            FrameCount = fromHkx.FrameCount,
            TrackCount = fromHkx.TrackCount,
            Duration = fromHkx.Duration,
            FrameDuration = fromHkx.FrameDuration,
            Transforms = fromHkx.Transforms,
            TrackToBone = trackToBone,
        };

        // Out to FBX...
        FbxDocument document = FbxAnimationWriter.Build(skeleton, animation, "roundtrip");

        using var stream = new MemoryStream();
        document.Save(stream);
        stream.Position = 0;

        // ...and back.
        FbxDocument reloaded = FbxDocument.Load(stream);
        Skeleton readSkeleton = FbxAnimationReader.ReadSkeleton(reloaded);
        SampledAnimation fromFbx = FbxAnimationReader.ReadAnimation(
            reloaded, readSkeleton, animation.FrameCount, animation.FrameDuration);

        Assert.Equal(skeleton.Count, readSkeleton.Count);
        Assert.Equal(animation.FrameCount, fromFbx.FrameCount);

        // The reader walks the hierarchy depth-first so that a parent always
        // precedes its children, which Havok requires and FBX does not promise.
        // That is its own order, so bones are matched up by name.
        var indexByName = readSkeleton.Bones
            .Select((bone, index) => (bone.Name, index))
            .ToDictionary(x => x.Name, x => x.index);

        double worstTranslation = 0, worstRotation = 0;

        for (int frame = 0; frame < animation.FrameCount; frame++)
        for (int track = 0; track < animation.TrackCount; track++)
        {
            int bone = animation.BoneForTrack(track);
            if (bone < 0 || bone >= skeleton.Count) continue;

            if (!indexByName.TryGetValue(skeleton.Bones[bone].Name, out int readBone)) continue;

            BoneTransform a = animation[frame, track];
            BoneTransform b = fromFbx[frame, readBone];

            worstTranslation = Math.Max(worstTranslation, (a.Translation - b.Translation).Length());

            float same = (a.Rotation - b.Rotation).Length();
            float flipped = (a.Rotation + b.Rotation).Length();
            worstRotation = Math.Max(worstRotation, Math.Min(same, flipped));
        }

        Assert.True(worstTranslation < 1e-2, $"translation drifted {worstTranslation}");
        Assert.True(worstRotation < 1e-2, $"rotation drifted {worstRotation}");
    }

    [CorpusFact]
    public void WritesAnHkxThatReadsBackAsTheSameAnimation()
    {
        (string animationPath, _) = Corpus.Chicken;

        (SplineAnimationData spline, _, _) = HkxAnimationFile.ReadAnimation(animationPath);
        var codec = new MopperAnimationCodec();

        SampledAnimation original = codec.Decompress(spline);
        SplineAnimationData recompressed = codec.Compress(original);

        string output = Path.Combine(Path.GetTempPath(), $"hkfbx-{Guid.NewGuid():N}.hkx");

        try
        {
            HkxAnimationFile.WriteAnimation(animationPath, recompressed, output);

            (SplineAnimationData readBack, _, _) = HkxAnimationFile.ReadAnimation(output);

            Assert.Equal(recompressed.NumFrames, readBack.NumFrames);
            Assert.Equal(recompressed.NumBlocks, readBack.NumBlocks);
            Assert.Equal(recompressed.TransformTrackCount, readBack.TransformTrackCount);
            Assert.Equal(recompressed.Data, readBack.Data);

            // The point of writing it: the animation still samples the same.
            SampledAnimation again = codec.Decompress(readBack);

            double worst = 0;
            for (int i = 0; i < original.Transforms.Length; i++)
            {
                worst = Math.Max(worst,
                    (original.Transforms[i].Translation - again.Transforms[i].Translation).Length());
            }

            Assert.True(worst < 0.05, $"translation drifted {worst} through the file");
        }
        finally
        {
            File.Delete(output);
        }
    }
}
