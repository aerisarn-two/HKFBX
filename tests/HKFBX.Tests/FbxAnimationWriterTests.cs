using HKFBX.Fbx;
using HKFBX.Model;
using LeanMeshIO;
using LeanMeshIO.Formats.Fbx;

namespace HKFBX.Tests;

/// <summary>
/// The FBX an animation turns into, checked by reading the document back.
/// </summary>
/// <remarks>
/// Structural rather than visual, because the failure that matters is not a
/// wrong number: it is a curve that reaches nothing. FBX binds animation by
/// connection, so a file can carry every key and still play nothing at all, and
/// that looks identical to a working file until someone presses play.
/// </remarks>
public class FbxAnimationWriterTests
{
    private static FbxDocument Build()
    {
        FbxDocument document = FbxAnimationWriter.Build(Synthetic.Skeleton(), Synthetic.Animation(), "take");

        // Through a real save and load, so the test sees what a reader sees
        // rather than the object graph that was just built.
        using var stream = new MemoryStream();
        document.Save(stream);
        stream.Position = 0;
        return FbxDocument.Load(stream);
    }

    private static IEnumerable<FbxNode> ObjectsOf(FbxDocument document, string className) =>
        document["Objects"]!.Nodes.Where(n => n.Name == className);

    private static List<(long Child, long Parent, string? Property)> Connections(FbxDocument document) =>
        document["Connections"]!.Nodes
            .Where(n => n.Name == "C")
            .Select(n => (
                Child: Convert.ToInt64(n.Properties[1]),
                Parent: Convert.ToInt64(n.Properties[2]),
                Property: n.Properties.Count > 3 ? n.Properties[3] as string : null))
            .ToList();

    [Fact]
    public void WritesTheRecordsEveryReaderExpects()
    {
        FbxDocument document = Build();

        Assert.NotNull(document["FBXHeaderExtension"]);
        Assert.NotNull(document["GlobalSettings"]);
        Assert.NotNull(document["Definitions"]);
        Assert.NotNull(document["Objects"]);
        Assert.NotNull(document["Connections"]);
    }

    [Fact]
    public void WritesOneLimbNodePerBone()
    {
        FbxDocument document = Build();
        Skeleton skeleton = Synthetic.Skeleton();

        var models = ObjectsOf(document, "Model").ToList();

        Assert.Equal(skeleton.Count, models.Count);
        Assert.All(models, m => Assert.Equal("LimbNode", m.Properties[2]));

        var names = models.Select(m => (string)m.Properties[1]!).ToList();
        foreach (Bone bone in skeleton.Bones)
            Assert.Contains($"Model::{bone.Name}", names);
    }

    [Fact]
    public void ParentsBonesAsTheSkeletonSays()
    {
        FbxDocument document = Build();
        Skeleton skeleton = Synthetic.Skeleton();

        var models = ObjectsOf(document, "Model")
            .ToDictionary(m => Convert.ToInt64(m.Properties[0]), m => (string)m.Properties[1]!);

        var edges = Connections(document)
            .Where(c => c.Property is null && models.ContainsKey(c.Child))
            .ToDictionary(c => models[c.Child], c => c.Parent);

        // Root has no parent, so it hangs off the scene root, which FBX calls 0.
        Assert.Equal(0L, edges["Model::Root"]);

        Assert.Equal("Model::Root", models[edges["Model::Spine"]]);
        Assert.Equal("Model::Spine", models[edges["Model::Head"]]);
        Assert.Equal("Model::Root", models[edges["Model::Tail"]]);
    }

    [Fact]
    public void WritesOneStackWithOneLayer()
    {
        FbxDocument document = Build();

        FbxNode stack = Assert.Single(ObjectsOf(document, "AnimationStack"));
        FbxNode layer = Assert.Single(ObjectsOf(document, "AnimationLayer"));

        // Autodesk writes the short class alias in the Class::Name prefix, not
        // the record's own name, and readers key off it.
        Assert.Equal("AnimStack::take", stack.Properties[1]);
        Assert.Equal("AnimLayer::Default", layer.Properties[1]);

        long stackId = Convert.ToInt64(stack.Properties[0]);
        long layerId = Convert.ToInt64(layer.Properties[0]);

        Assert.Contains(Connections(document), c => c.Child == layerId && c.Parent == stackId);
    }

    [Fact]
    public void BindsEveryCurveNodeToABoneProperty()
    {
        FbxDocument document = Build();
        Skeleton skeleton = Synthetic.Skeleton();

        var curveNodes = ObjectsOf(document, "AnimationCurveNode").ToList();

        // Translation, rotation and scaling for each bone.
        Assert.Equal(skeleton.Count * 3, curveNodes.Count);

        var modelIds = ObjectsOf(document, "Model")
            .Select(m => Convert.ToInt64(m.Properties[0])).ToHashSet();

        var bound = Connections(document)
            .Where(c => c.Property is "Lcl Translation" or "Lcl Rotation" or "Lcl Scaling")
            .ToList();

        Assert.Equal(curveNodes.Count, bound.Count);
        Assert.All(bound, c => Assert.Contains(c.Parent, modelIds));
    }

    [Fact]
    public void GivesEveryCurveNodeThreeComponentCurves()
    {
        FbxDocument document = Build();

        var curveNodeIds = ObjectsOf(document, "AnimationCurveNode")
            .Select(n => Convert.ToInt64(n.Properties[0])).ToHashSet();

        var curves = ObjectsOf(document, "AnimationCurve").ToList();

        Assert.Equal(curveNodeIds.Count * 3, curves.Count);

        var links = Connections(document)
            .Where(c => c.Property is "d|X" or "d|Y" or "d|Z")
            .ToList();

        Assert.Equal(curves.Count, links.Count);
        Assert.All(links, c => Assert.Contains(c.Parent, curveNodeIds));
    }

    [Fact]
    public void WritesOneKeyPerFrameOnEveryCurve()
    {
        SampledAnimation animation = Synthetic.Animation();
        FbxDocument document = Build();

        foreach (FbxNode curve in ObjectsOf(document, "AnimationCurve"))
        {
            var times = (long[])curve.Nodes.Single(n => n.Name == "KeyTime").Properties[0]!;
            var values = (float[])curve.Nodes.Single(n => n.Name == "KeyValueFloat").Properties[0]!;

            Assert.Equal(animation.FrameCount, times.Length);
            Assert.Equal(animation.FrameCount, values.Length);
        }
    }

    [Fact]
    public void KeyTimesRunFromZeroToTheDuration()
    {
        SampledAnimation animation = Synthetic.Animation();
        FbxDocument document = Build();

        FbxNode curve = ObjectsOf(document, "AnimationCurve").First();
        var times = (long[])curve.Nodes.Single(n => n.Name == "KeyTime").Properties[0]!;

        Assert.Equal(0L, times[0]);
        Assert.Equal(FbxAnimationWriter.ToFbxTime(animation.Duration), times[^1]);

        // Strictly increasing, or a reader will not know which key comes first.
        for (int i = 1; i < times.Length; i++) Assert.True(times[i] > times[i - 1]);
    }

    [Fact]
    public void CarriesTheMovementItWasGiven()
    {
        SampledAnimation animation = Synthetic.Animation();
        FbxDocument document = Build();

        // The first bone slides 0..10 along X, so some translation curve has to
        // end 10 away from where it started.
        bool moved = ObjectsOf(document, "AnimationCurve")
            .Select(c => (float[])c.Nodes.Single(n => n.Name == "KeyValueFloat").Properties[0]!)
            .Any(v => Math.Abs(v[^1] - v[0]) > 9f);

        Assert.True(moved, "no curve carries the translation the animation was built with");
    }
}
