using System.Numerics;
using HKFBX.Fbx;
using HKFBX.Model;
using LeanMeshIO;
using LeanMeshIO.Formats.Fbx;
using Xunit;

namespace HKFBX.Tests;

/// <summary>
/// Several clips in one document, which is what exporting a creature needs: the
/// game keeps an actor's animations as hundreds of separate files over one rig,
/// and a single FBX holding all of them is one stack each.
/// </summary>
public sealed class MultipleStackTests
{
    /// <summary>
    /// A second animation over a skeleton the document already has.
    /// </summary>
    private static FbxDocument TwoStacks(out Skeleton skeleton)
    {
        skeleton = Synthetic.Skeleton();

        FbxDocument document = FbxAnimationWriter.Build(skeleton, Synthetic.Animation(), "walk");
        FbxAnimationWriter.AddStack(document, skeleton, Synthetic.Animation(frames: 12), "run");

        return document;
    }

    [Fact]
    public void BothStacksAreInTheDocument()
    {
        FbxDocument document = TwoStacks(out _);

        Assert.Equal(["walk", "run"], FbxAnimationReader.ReadTakeNames(document));
    }

    [Fact]
    public void EveryStackGetsItsOwnTake()
    {
        FbxDocument document = TwoStacks(out _);
        FbxNode takes = Assert.Single(document.Nodes, n => n.Name == "Takes");

        Assert.Equal(
            ["walk", "run"],
            takes.Nodes.Where(n => n.Name == "Take").Select(n => n.Properties[0] as string));

        // Something has to be current, and only one thing can be.
        FbxNode current = Assert.Single(takes.Nodes, n => n.Name == "Current");
        Assert.Equal("walk", current.Properties[0] as string);
    }

    [Fact]
    public void EachStackOwnsItsOwnLayer()
    {
        FbxDocument document = TwoStacks(out _);
        var scene = new FbxScene(document);

        foreach (FbxObject stack in scene.OfClass("AnimationStack"))
            Assert.Single(scene.ChildrenOf(stack.Id), o => o.Class == "AnimationLayer");
    }

    [Fact]
    public void ReadingByNameGetsThatClipAndNotTheOther()
    {
        FbxDocument document = TwoStacks(out Skeleton skeleton);

        SampledAnimation walk = FbxAnimationReader.ReadAnimation(
            document, skeleton, frameCount: 30, frameDuration: 1f / 30f, takeName: "walk");
        SampledAnimation run = FbxAnimationReader.ReadAnimation(
            document, skeleton, frameCount: 12, frameDuration: 1f / 30f, takeName: "run");

        // The synthetic clip slides the first bone to x = 10 over its length, so
        // the two differ everywhere except the ends.
        Assert.Equal(10f, walk[29, 0].Translation.X, 3);
        Assert.Equal(10f, run[11, 0].Translation.X, 3);

        // Halfway through, the shorter clip has travelled further.
        Assert.True(run[6, 0].Translation.X > walk[6, 0].Translation.X);
    }

    /// <summary>
    /// A creature's clips all drive the same root node on the same two
    /// properties, so the travel has to be asked for by clip.
    /// </summary>
    [Fact]
    public void RootMotionIsReadPerClipRatherThanWhicheverComesFirst()
    {
        Skeleton skeleton = Synthetic.Skeleton();

        static RootMotion Travelling(float distance) => new()
        {
            Duration = 1f,
            Translations =
            [
                new TranslationKey(0f, Vector3.Zero),
                new TranslationKey(1f, new Vector3(0f, -distance, 0f)),
            ],
        };

        // 31 frames at 1/30s is exactly one second, so the last frame samples the
        // motion at its end rather than one frame short of it.
        FbxDocument document = FbxAnimationWriter.Build(
            skeleton, Synthetic.Animation(frames: 31) with { RootMotion = Travelling(20f) }, "walk");

        FbxAnimationWriter.AddStack(
            document, skeleton,
            Synthetic.Animation(frames: 31) with { RootMotion = Travelling(200f) }, "run");

        Assert.Equal(-20f,
            FbxAnimationReader.ReadRootMotion(document, skeleton, "walk").Translations[^1].Value.Y, 3);
        Assert.Equal(-200f,
            FbxAnimationReader.ReadRootMotion(document, skeleton, "run").Translations[^1].Value.Y, 3);

        // And a name that names no stack finds no travel, rather than the first
        // clip's.
        Assert.True(FbxAnimationReader.ReadRootMotion(document, skeleton, "crawl").IsEmpty);
    }

    /// <summary>
    /// The same for events, which sit on the same nodes whichever clip they
    /// belong to.
    /// </summary>
    [Fact]
    public void EventsAreReadPerClipRatherThanAllAtOnce()
    {
        Skeleton skeleton = Synthetic.Skeleton();

        static AnnotationTrack Track(params string[] texts) => new()
        {
            Name = "Root",
            Events = [.. texts.Select((text, i) => new AnimationEvent(0.1f * (i + 1), text))],
        };

        FbxDocument document = FbxAnimationWriter.Build(
            skeleton,
            Synthetic.Animation() with { Annotations = [Track("walkStart", "walkStop")] },
            "walk");

        FbxAnimationWriter.AddStack(
            document, skeleton,
            Synthetic.Animation() with { Annotations = [Track("runStart")] },
            "run");

        Assert.Equal(
            ["walkStart", "walkStop"],
            FbxAnimationReader.ReadEvents(document, "walk").SelectMany(t => t.Events).Select(e => e.Text));

        Assert.Equal(
            ["runStart"],
            FbxAnimationReader.ReadEvents(document, "run").SelectMany(t => t.Events).Select(e => e.Text));

        Assert.Empty(FbxAnimationReader.ReadEvents(document, "crawl"));
    }

    [Fact]
    public void NamingNoStackReadsNoCurves()
    {
        FbxDocument document = TwoStacks(out Skeleton skeleton);

        SampledAnimation none = FbxAnimationReader.ReadAnimation(
            document, skeleton, frameCount: 2, frameDuration: 1f / 30f, takeName: "crawl");

        // Every bone at its rest pose, which is what a document with no curves for
        // it leaves behind.
        for (int bone = 0; bone < skeleton.Count; bone++)
            Assert.Equal(skeleton.Bones[bone].ReferencePose.Translation, none[0, bone].Translation);
    }

    [Fact]
    public void ASceneWhoseNodesAreNamedDifferentlyStillBinds()
    {
        // What a scene converted from a NIF looks like: the bone names escaped, so
        // no node is called what the rig calls it.
        var skeleton = new Skeleton
        {
            Name = "escaped",
            Bones =
            [
                new Bone("NPC Root [Root]", -1, BoneTransform.Identity),
                new Bone("NPC Spine [Spn0]", 0, BoneTransform.Identity),
            ],
        };

        static string Escape(string name) =>
            name.Replace(" ", "_s_").Replace("[", "_ob_").Replace("]", "_cb_");

        // The document's nodes carry the escaped spelling; the rig carries the real
        // one, and the map is the only thing joining them.
        var escaped = new Skeleton
        {
            Name = skeleton.Name,
            Bones = [.. skeleton.Bones.Select(b => b with { Name = Escape(b.Name) })],
        };

        FbxDocument document = FbxAnimationWriter.Build(escaped, Synthetic.Animation(tracks: 2), "rest");

        var renamed = skeleton.Bones.ToDictionary(b => b.Name, b => Escape(b.Name), StringComparer.Ordinal);

        int bound = FbxAnimationWriter.AddStack(
            document, skeleton, Synthetic.Animation(tracks: 2), "walk", renamed);

        Assert.Equal(2, bound);

        // And without the map it binds nothing, which is the point of having one.
        Assert.Equal(0, FbxAnimationWriter.AddStack(document, skeleton, Synthetic.Animation(tracks: 2), "jog"));
    }

    [Fact]
    public void ABoneTheDocumentDoesNotHaveIsReportedRatherThanThrown()
    {
        Skeleton skeleton = Synthetic.Skeleton();
        FbxDocument document = FbxAnimationWriter.Build(skeleton, Synthetic.Animation(), "walk");

        var wider = new Skeleton
        {
            Name = skeleton.Name,
            Bones = [.. skeleton.Bones, new Bone("Wing", 0, BoneTransform.Identity)],
        };

        // A clip authored against a fuller rig is still mostly playable.
        Assert.Equal(4, FbxAnimationWriter.AddStack(document, wider, Synthetic.Animation(tracks: 5), "fly"));
    }

    [Fact]
    public void AddingAStackMarksTheNodesAsAnimated()
    {
        Skeleton skeleton = Synthetic.Skeleton();
        FbxDocument document = FbxAnimationWriter.Build(skeleton, Synthetic.Animation(), "walk");

        // Strip the flags the way a converter that never expected animation would
        // have written them, then add a stack over it.
        var scene = new FbxScene(document);

        foreach (FbxObject model in scene.OfClass("Model"))
            foreach (FbxProperty70 property in model.Properties.All)
                if (property.Name is "Lcl Translation" or "Lcl Rotation" or "Lcl Scaling")
                    property.SetFlags(string.Empty);

        scene.Flush();
        FbxAnimationWriter.AddStack(document, skeleton, Synthetic.Animation(), "run");

        foreach (FbxObject model in new FbxScene(document).OfClass("Model"))
            foreach (FbxProperty70 property in model.Properties.All)
                if (property.Name is "Lcl Translation" or "Lcl Rotation" or "Lcl Scaling")
                    Assert.Equal("A+", property.Flags);
    }

    [Fact]
    public void ManyStacksSurviveBeingWrittenAndReadBack()
    {
        Skeleton skeleton = Synthetic.Skeleton();
        FbxDocument document = FbxAnimationWriter.Build(skeleton, Synthetic.Animation(), "clip00");

        for (int i = 1; i < 20; i++)
            FbxAnimationWriter.AddStack(document, skeleton, Synthetic.Animation(frames: 10 + i), $"clip{i:00}");

        string path = Path.Combine(Path.GetTempPath(), $"hkfbx-stacks-{Guid.NewGuid():N}.fbx");

        try
        {
            document.Save(path);
            FbxDocument reopened = FbxDocument.Load(path);

            Assert.Equal(20, FbxAnimationReader.ReadTakeNames(reopened).Count);
            Assert.Equal("clip19", FbxAnimationReader.ReadTakeNames(reopened)[19]);

            // And each still reads back as its own length.
            SampledAnimation last = FbxAnimationReader.ReadAnimation(
                reopened, skeleton, frameCount: 29, frameDuration: 1f / 30f, takeName: "clip19");

            Assert.Equal(10f, last[28, 0].Translation.X, 3);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
