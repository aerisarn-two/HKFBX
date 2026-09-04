using HKFBX.Fbx;
using HKFBX.Model;
using LeanMeshIO;

namespace HKFBX.Tests;

/// <summary>
/// The events an animation announces, which Havok calls annotations.
/// </summary>
/// <remarks>
/// These are what a behaviour graph listens for — a footstep sound, the moment a
/// weapon connects, the end of a clip — so losing one is not cosmetic.
/// </remarks>
public class EventTests
{
    private static AnnotationTrack Footsteps() => new()
    {
        Name = "NPC Root [Root]",
        Events =
        [
            new AnimationEvent(0.333333f, "SoundPlay.NPCChickenScratch"),
            new AnimationEvent(0.600000f, "SoundPlay.NPCChickenScratch"),
            new AnimationEvent(0.900000f, "SoundPlay.NPCChickenScratch"),
            new AnimationEvent(1.333330f, "idleStop"),
        ],
    };

    private static FbxDocument Write(params AnnotationTrack[] tracks)
    {
        SampledAnimation animation = Synthetic.Animation() with { Annotations = tracks };

        FbxDocument document = FbxAnimationWriter.Build(Synthetic.Skeleton(), animation, "take");

        using var stream = new MemoryStream();
        document.Save(stream);
        stream.Position = 0;
        return FbxDocument.Load(stream);
    }

    [Fact]
    public void EventsSurviveTheRoundTrip()
    {
        AnnotationTrack original = Footsteps();

        AnnotationTrack read = Assert.Single(FbxAnimationReader.ReadEvents(Write(original)));

        Assert.Equal(original.Events.Count, read.Events.Count);

        for (int i = 0; i < original.Events.Count; i++)
        {
            Assert.Equal(original.Events[i].Text, read.Events[i].Text);
            Assert.Equal(original.Events[i].Time, read.Events[i].Time, 4);
        }
    }

    [Fact]
    public void RepeatedTextIsStoredOnceAndReadBackEachTime()
    {
        // Three of the four events share a text. It goes into the enum once and
        // each key names it, which is the point of the encoding.
        AnnotationTrack read = Assert.Single(FbxAnimationReader.ReadEvents(Write(Footsteps())));

        Assert.Equal(3, read.Events.Count(e => e.Text == "SoundPlay.NPCChickenScratch"));
        Assert.Single(read.Events, e => e.Text == "idleStop");
    }

    [Fact]
    public void TextIsKeptWhole()
    {
        // ck-cmd splits a name at its last capital and rebuilds it on the way
        // back. Nothing here does, so a name survives however it is spelled.
        var awkward = new AnnotationTrack
        {
            Name = "",
            Events =
            [
                new AnimationEvent(0.1f, "weaponSwing"),
                new AnimationEvent(0.2f, "SoundPlay.WPNSwingUnarmed"),
                new AnimationEvent(0.3f, "aaaa"),
                new AnimationEvent(0.4f, "MCO_WinOpen"),
            ],
        };

        AnnotationTrack read = Assert.Single(FbxAnimationReader.ReadEvents(Write(awkward)));

        Assert.Equal(
            awkward.Events.Select(e => e.Text),
            read.Events.Select(e => e.Text));
    }

    [Fact]
    public void EventTimesAreKeptToTheTick()
    {
        AnnotationTrack read = Assert.Single(FbxAnimationReader.ReadEvents(Write(Footsteps())));

        // FBX counts time in units of 1/46186158000 of a second, so a time
        // survives to far more precision than an event needs.
        Assert.Equal(0.333333f, read.Events[0].Time, 5);
        Assert.Equal(1.33333f, read.Events[^1].Time, 5);
    }

    [Fact]
    public void SeveralTracksStayApart()
    {
        var first = new AnnotationTrack
        {
            Name = "Root",
            Events = [new AnimationEvent(0.5f, "one")],
        };

        var second = new AnnotationTrack
        {
            Name = "Other",
            Events = [new AnimationEvent(0.5f, "two")],
        };

        var read = FbxAnimationReader.ReadEvents(Write(first, second));

        Assert.Equal(2, read.Count);
        Assert.Contains(read, t => t.Events.Single().Text == "one");
        Assert.Contains(read, t => t.Events.Single().Text == "two");
    }

    [Fact]
    public void AnAnimationWithNoEventsWritesNone()
    {
        Assert.Empty(FbxAnimationReader.ReadEvents(Write()));
    }
}
