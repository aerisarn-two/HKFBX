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
    /// A clip list where each end is the next start gives two neighbours one pose in
    /// common, which is what a looping clip wants and what a one-shot needs to finish
    /// on the pose it was drawn finishing on.
    /// </summary>
    [Fact]
    public void NeighboursShareTheFrameTheyMeetOn()
    {
        FbxDocument document = OneTake(out Skeleton skeleton);
        SampledAnimation whole = FbxAnimationReader.ReadAnimation(document, skeleton, takeName: "Take 001");

        FbxTakeCutter.Cut(document, [new TakeCut("first", 0, 10), new TakeCut("second", 10, 20)], rig: skeleton);

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
}
