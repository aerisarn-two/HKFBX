using HKFBX.Fbx;
using LeanMeshIO;
using LeanMeshIO.Formats.Fbx;

namespace HKFBX.Tests;

/// <summary>
/// Format conventions that readers depend on and that nothing else would catch.
/// </summary>
/// <remarks>
/// Every one of these was wrong in the first version of the writer, and the
/// result imported as a correct skeleton that would not move — the file parses,
/// the hierarchy is right, the curves are all present, and nothing plays. They
/// are checked against "Shoved Reaction With Spin.fbx", an Autodesk-written file
/// that works, field by field.
/// </remarks>
public class FbxConventionTests
{
    private static FbxDocument Build()
    {
        FbxDocument document = FbxAnimationWriter.Build(
            Synthetic.Skeleton(), Synthetic.Animation(), "take");

        using var stream = new MemoryStream();
        document.Save(stream);
        stream.Position = 0;
        return FbxDocument.Load(stream);
    }

    [Fact]
    public void DefinitionsComeBeforeObjects()
    {
        FbxDocument document = Build();

        int definitions = document.Nodes.FindIndex(n => n.Name == "Definitions");
        int objects = document.Nodes.FindIndex(n => n.Name == "Objects");

        Assert.True(definitions >= 0, "no Definitions");
        Assert.True(definitions < objects,
            "a reader sizes its tables from Definitions before it reaches Objects");
    }

    [Theory]
    [InlineData("AnimationStack", "AnimStack::")]
    [InlineData("AnimationLayer", "AnimLayer::")]
    [InlineData("AnimationCurveNode", "AnimCurveNode::")]
    [InlineData("AnimationCurve", "AnimCurve::")]
    [InlineData("Model", "Model::")]
    public void ObjectsCarryTheClassAliasAutodeskWrites(string className, string prefix)
    {
        FbxDocument document = Build();

        var names = document["Objects"]!.Nodes
            .Where(n => n.Name == className)
            .Select(n => n.Properties[1] as string ?? "")
            .ToList();

        Assert.NotEmpty(names);
        Assert.All(names, n => Assert.StartsWith(prefix, n, StringComparison.Ordinal));
    }

    [Fact]
    public void SceneNamesTheStackItShouldPlay()
    {
        FbxDocument document = Build();

        FbxNode? scene = document["Documents"]?.Nodes.FirstOrDefault(n => n.Name == "Document");
        FbxNode? properties = scene?.Nodes.FirstOrDefault(n => n.Name == "Properties70");

        Assert.NotNull(properties);

        // A viewer reads this to decide what to play; without it a file can hold
        // a perfectly good take and open showing nothing moving.
        Assert.Contains(properties!.Nodes,
            p => p.Properties.Count > 0 && (p.Properties[0] as string) == "ActiveAnimStackName");
    }

    [Fact]
    public void TimeModeIsThirtyFramesASecond()
    {
        FbxDocument document = Build();

        FbxNode properties = document["GlobalSettings"]!.Nodes.Single(n => n.Name == "Properties70");
        FbxNode timeMode = properties.Nodes.Single(
            p => p.Properties.Count > 0 && (p.Properties[0] as string) == "TimeMode");

        // 6 is eFrames30. The enum is not a frame rate: 11 is eFrames24, which is
        // what this used to say, and everything played slow.
        Assert.Equal(6, Convert.ToInt32(timeMode.Properties[^1]));
    }

    [Fact]
    public void ModelsCarryTheirVersionAndAnimatedFlags()
    {
        FbxDocument document = Build();

        foreach (FbxNode model in document["Objects"]!.Nodes.Where(n => n.Name == "Model"))
        {
            Assert.Contains(model.Nodes, n => n.Name == "Version");

            FbxNode properties = model.Nodes.Single(n => n.Name == "Properties70");

            foreach (string local in new[] { "Lcl Translation", "Lcl Rotation", "Lcl Scaling" })
            {
                FbxNode property = properties.Nodes.Single(
                    p => p.Properties.Count > 0 && (p.Properties[0] as string) == local);

                // "A+" is animatable and animated. "A" alone lets a reader ignore
                // the curve.
                Assert.Equal("A+", property.Properties[3] as string);
            }
        }
    }

    [Fact]
    public void WritesATakeList()
    {
        FbxDocument document = Build();

        FbxNode? takes = document["Takes"];

        Assert.NotNull(takes);
        Assert.Contains(takes!.Nodes, n => n.Name == "Current");
        Assert.Contains(takes.Nodes, n => n.Name == "Take");
    }

    /// <summary>
    /// Proves the reader is not merely the writer's mirror: it also reads a file
    /// this project had no hand in producing.
    /// </summary>
    [ReferenceFbxFact]
    public void ReadsAFileWrittenByAutodesk()
    {
        FbxDocument document = FbxDocument.Load(ReferenceFbxFactAttribute.Path_);

        Model.Skeleton skeleton = FbxAnimationReader.ReadSkeleton(document);
        Model.SampledAnimation animation = FbxAnimationReader.ReadAnimation(document, skeleton);

        Assert.NotEmpty(skeleton.Bones);
        Assert.True(animation.FrameCount > 1);
        Assert.Equal("mixamo.com", FbxAnimationReader.ReadTakeName(document));
    }
}
