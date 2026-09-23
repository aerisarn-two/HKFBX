using HKFBX.Fbx;
using HKFBX.Model;
using LeanMeshIO;
using Xunit;

namespace HKFBX.Tests;

/// <summary>
/// One long take cut into the clips it is made of, which is how a creature bought
/// off a marketplace arrives: every animation laid end to end in a single take, and
/// a picture of an editor's clip list saying where each one starts and stops.
/// </summary>
public sealed class TakeCutterTests
{
    private static FbxDocument OneTake(out Skeleton skeleton, int frames = 30)
    {
        skeleton = Synthetic.Skeleton();
        return FbxAnimationWriter.Build(skeleton, Synthetic.Animation(frames), "Take 001");
    }

    [Fact]
    public void EachCutBecomesAStackOfItsOwn()
    {
        FbxDocument document = OneTake(out Skeleton skeleton);

        CutReport report = FbxTakeCutter.Cut(document,
            [new TakeCut("walk", 0, 9), new TakeCut("run", 10, 19), new TakeCut("stop", 20, 29)], rig: skeleton);

        Assert.Empty(report.Refused);
        Assert.Equal(["walk", "run", "stop"], report.Cuts.Select(c => c.Name));
        Assert.All(report.Cuts, c => Assert.Equal(skeleton.Count, c.BoundBones));

        // The take is left where it was, and the cuts stand beside it.
        Assert.Equal(["Take 001", "walk", "run", "stop"], FbxAnimationReader.ReadTakeNames(document));
    }

    /// <summary>
    /// A cut holds the take's own poses and nothing interpolated: its frame n is the
    /// take's frame first + n, to the bit.
    /// </summary>
    [Fact]
    public void ACutHoldsThePosesTheTakeHeld()
    {
        FbxDocument document = OneTake(out Skeleton skeleton);
        SampledAnimation whole = FbxAnimationReader.ReadAnimation(document, skeleton, takeName: "Take 001");

        FbxTakeCutter.Cut(document, [new TakeCut("middle", 10, 19)], rig: skeleton);
        SampledAnimation cut = FbxAnimationReader.ReadAnimation(document, skeleton, takeName: "middle");

        Assert.Equal(10, cut.FrameCount);
        Assert.Equal(whole.FrameDuration, cut.FrameDuration, 5);
        Assert.Equal(9 * whole.FrameDuration, cut.Duration, 4);

        for (int frame = 0; frame < cut.FrameCount; frame++)
            for (int track = 0; track < cut.TrackCount; track++)
            {
                BoneTransform was = whole[10 + frame, track], now = cut[frame, track];
                Assert.Equal(was.Translation.X, now.Translation.X, 3);
                Assert.Equal(was.Translation.Y, now.Translation.Y, 3);
                Assert.Equal(was.Translation.Z, now.Translation.Z, 3);
            }
    }

    /// <summary>
    /// Two neighbours can be made to share the pose they meet on, which is what a take
    /// that runs continuously through the boundary wants. It has to be asked for: on a
    /// list whose clips touch, the frame they meet on belongs to the second by default,
    /// because a take with the clips merely laid end to end has a different animation
    /// starting there and the first would finish on a pose out of it.
    /// </summary>
    [Fact]
    public void NeighboursCanBeMadeToShareTheFrameTheyMeetOn()
    {
        FbxDocument document = OneTake(out Skeleton skeleton);
        SampledAnimation whole = FbxAnimationReader.ReadAnimation(document, skeleton, takeName: "Take 001");

        FbxTakeCutter.Cut(
            document, [new TakeCut("first", 0, 10), new TakeCut("second", 10, 20)],
            rig: skeleton, endIsInside: true);

        SampledAnimation first = FbxAnimationReader.ReadAnimation(document, skeleton, takeName: "first");
        SampledAnimation second = FbxAnimationReader.ReadAnimation(document, skeleton, takeName: "second");

        Assert.Equal(11, first.FrameCount);
        Assert.Equal(11, second.FrameCount);
        Assert.Equal(whole[10, 0].Translation.X, first[first.FrameCount - 1, 0].Translation.X, 3);
        Assert.Equal(whole[10, 0].Translation.X, second[0, 0].Translation.X, 3);
    }

    [Fact]
    public void ACutOutsideTheTakeIsRefusedAndNamed()
    {
        FbxDocument document = OneTake(out Skeleton skeleton);

        CutReport report = FbxTakeCutter.Cut(document,
            [new TakeCut("good", 0, 5), new TakeCut("past the end", 40, 50), new TakeCut("", 1, 2)], rig: skeleton);

        Assert.Equal("good", Assert.Single(report.Cuts).Name);
        Assert.Contains("past the end", report.Refused.Keys);
        Assert.Contains("outside the take", report.Refused["past the end"], StringComparison.Ordinal);
        Assert.Contains(report.Refused, r => r.Value.Contains("no name", StringComparison.Ordinal));
    }

    /// <summary>A cut reaching past the last frame is trimmed to it rather than refused.</summary>
    [Fact]
    public void ACutThatOverrunsTheEndIsTrimmed()
    {
        FbxDocument document = OneTake(out Skeleton skeleton, frames: 20);

        CutReport report = FbxTakeCutter.Cut(document, [new TakeCut("tail", 15, 99)], rig: skeleton);

        Assert.Equal(5, Assert.Single(report.Cuts).Frames);
    }

    /// <summary>
    /// A cut list whose clips touch is half-open, and reading it as inclusive gives every
    /// clip one frame belonging to the animation that follows it.
    /// </summary>
    [Fact]
    public void ACutListWhoseClipsTouchIsReadAsHalfOpen()
    {
        Assert.True(FbxTakeCutter.Touching(
            [new TakeCut("A", 0, 10), new TakeCut("B", 10, 20), new TakeCut("C", 20, 30)]));

        // One apart is the other convention, where the end frame is this clip's last.
        Assert.False(FbxTakeCutter.Touching(
            [new TakeCut("A", 0, 9), new TakeCut("B", 10, 19), new TakeCut("C", 20, 29)]));

        Assert.False(FbxTakeCutter.Touching([new TakeCut("A", 0, 10)]));
    }

    /// <summary>
    /// The frames themselves: a touching list gives each clip the frames up to but not
    /// including its end, so no two clips share a pose.
    /// </summary>
    [Fact]
    public void ATouchingListGivesNoTwoClipsAPoseInCommon()
    {
        FbxDocument document = OneTake(out Skeleton skeleton, frames: 30);

        CutReport report = FbxTakeCutter.Cut(
            document,
            [new TakeCut("A", 0, 10), new TakeCut("B", 10, 20), new TakeCut("C", 20, 30)],
            rig: skeleton);

        Assert.False(report.EndIsInside);
        Assert.Equal([10, 10, 10], report.Cuts.Select(c => c.Frames));
        Assert.Empty(report.Refused);

        // Read as inclusive instead, each clip takes one frame more -- the one the next
        // clip starts on.
        CutReport inclusive = FbxTakeCutter.Cut(
            document,
            [new TakeCut("A", 0, 10), new TakeCut("B", 10, 20), new TakeCut("C", 20, 30)],
            rig: skeleton,
            endIsInside: true);

        Assert.True(inclusive.EndIsInside);
        Assert.Equal([11, 11, 10], inclusive.Cuts.Select(c => c.Frames));
    }

    /// <summary>
    /// A list whose clips do not touch keeps its end frames, which is what that convention
    /// means and what a single cut out of the middle of a take needs.
    /// </summary>
    [Fact]
    public void AListWhoseClipsDoNotTouchKeepsItsEndFrames()
    {
        FbxDocument document = OneTake(out Skeleton skeleton, frames: 30);

        CutReport report = FbxTakeCutter.Cut(
            document, [new TakeCut("A", 0, 9), new TakeCut("B", 10, 19)], rig: skeleton);

        Assert.True(report.EndIsInside);
        Assert.Equal([10, 10], report.Cuts.Select(c => c.Frames));
    }
}
