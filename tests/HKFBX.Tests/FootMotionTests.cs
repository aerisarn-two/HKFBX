using System.Numerics;
using HKFBX.Model;
using Xunit;

namespace HKFBX.Tests;

/// <summary>
/// Reading a clip's travel off its feet, which is the only place it is written when
/// the animation was made for an engine that moves the actor in code.
/// </summary>
public sealed class FootMotionTests
{
    private const float FrameDuration = 1f / 30f;

    /// <summary>A root and two feet under it, which is the least a walk needs.</summary>
    private static Skeleton Biped() => new()
    {
        Name = "biped",
        Bones =
        [
            new Bone("Root", -1, BoneTransform.Identity),
            new Bone("LeftFoot", 0, new BoneTransform(new Vector3(-5, 0, 0), Quaternion.Identity, Vector3.One)),
            new Bone("RightFoot", 0, new BoneTransform(new Vector3(5, 0, 0), Quaternion.Identity, Vector3.One)),
        ],
    };

    /// <summary>
    /// A walk in place: each foot in turn stays on the ground and slides backwards by
    /// <paramref name="perFrame"/>, while the other lifts and swings.
    /// </summary>
    private static SampledAnimation InPlaceWalk(int frames, float perFrame, float lift = 6f, int stride = 5)
    {
        var transforms = new BoneTransform[frames * 3];

        for (int f = 0; f < frames; f++)
        {
            bool leftDown = f / stride % 2 == 0;
            int since = f % stride;

            transforms[f * 3] = BoneTransform.Identity;
            transforms[f * 3 + 1] = Foot(-5f, leftDown, since);
            transforms[f * 3 + 2] = Foot(5f, !leftDown, since);
        }

        return new SampledAnimation
        {
            FrameCount = frames,
            TrackCount = 3,
            Duration = (frames - 1) * FrameDuration,
            FrameDuration = FrameDuration,
            Transforms = transforms,
        };

        BoneTransform Foot(float x, bool down, int since) => new(
            new Vector3(x, down ? -since * perFrame : since * perFrame, down ? 0f : lift),
            Quaternion.Identity, Vector3.One);
    }

    /// <summary>
    /// The planted foot slides backwards at the speed the creature should go forwards,
    /// so the travel is the mirror of the slide.
    /// </summary>
    [Fact]
    public void ThePlantedFootsSlideIsTheTravel()
    {
        const float perFrame = 3f;
        InferredMotion motion = Assert.IsType<InferredMotion>(
            FootMotion.Infer(InPlaceWalk(31, perFrame), Biped(), ["LeftFoot", "RightFoot"]));

        Assert.Equal(perFrame / FrameDuration, motion.Speed, 0);
        Assert.Equal(0f, motion.Heading, 0);
        Assert.True(motion.Confidence >= 0.8f, $"a walk has a foot down throughout, and this read {motion.Confidence:P0}");
    }

    /// <summary>A creature standing still is read as standing still.</summary>
    [Fact]
    public void AnIdleTravelsNowhere()
    {
        InferredMotion motion = Assert.IsType<InferredMotion>(
            FootMotion.Infer(InPlaceWalk(31, perFrame: 0f, lift: 0f), Biped(), ["LeftFoot", "RightFoot"]));

        Assert.Equal(0f, motion.Speed, 3);
    }

    /// <summary>Faster feet, faster creature, in proportion.</summary>
    [Fact]
    public void TwiceTheSlideIsTwiceTheSpeed()
    {
        Skeleton rig = Biped();
        float slow = FootMotion.Infer(InPlaceWalk(31, 2f), rig, ["LeftFoot", "RightFoot"])!.Speed;
        float fast = FootMotion.Infer(InPlaceWalk(31, 4f), rig, ["LeftFoot", "RightFoot"])!.Speed;

        Assert.Equal(2f * slow, fast, 1);
    }

    /// <summary>
    /// A clip that leaves the ground -- a jump, a swim -- reads at a confidence that
    /// says not to trust it, rather than returning a number that looks like the others.
    /// </summary>
    [Fact]
    public void AClipThatLeavesTheGroundReadsAtLowConfidence()
    {
        Skeleton rig = Biped();
        var transforms = new BoneTransform[10 * 3];

        for (int f = 0; f < 10; f++)
        {
            transforms[f * 3] = BoneTransform.Identity;
            // Both feet rise together: nothing is ever near the ground for long.
            var up = new BoneTransform(new Vector3(0, 0, f * 10f), Quaternion.Identity, Vector3.One);
            transforms[f * 3 + 1] = up;
            transforms[f * 3 + 2] = up;
        }

        InferredMotion? motion = FootMotion.Infer(
            new SampledAnimation
            {
                FrameCount = 10, TrackCount = 3, Duration = 9 * FrameDuration,
                FrameDuration = FrameDuration, Transforms = transforms,
            },
            rig, ["LeftFoot", "RightFoot"]);

        Assert.True(motion is null || motion.Confidence < 0.5f,
            $"a clip mostly in the air should not read as planted, and this read {motion?.Confidence:P0}");
    }

    [Fact]
    public void AFootTheRigHasNotGotIsNoAnswerAtAll() =>
        Assert.Null(FootMotion.Infer(InPlaceWalk(31, 3f), Biped(), ["NoSuchBone"]));
}
