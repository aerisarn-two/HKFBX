using System.Numerics;
using HKFBX.Model;
using LeanMeshIO;
using LeanMeshIO.Formats.Fbx;

namespace HKFBX.Fbx;

/// <summary>
/// Writes a skeleton and one animation into an FBX document.
/// </summary>
/// <remarks>
/// FBX splits an animation four ways. An <c>AnimationStack</c> is the take; an
/// <c>AnimationLayer</c> under it holds the tracks; an <c>AnimationCurveNode</c>
/// binds one vector-valued property of one model — its translation, rotation or
/// scaling — and an <c>AnimationCurve</c> under that holds the keys for a single
/// component.
///
/// The binding is by connection, not by containment: the curve node reaches its
/// model through an object-to-property edge naming <c>Lcl Translation</c> and the
/// like, and each curve reaches the curve node through another naming <c>d|X</c>.
/// Miss either and the file loads with the animation present but attached to
/// nothing, which looks like a working file until you press play.
/// </remarks>
public static class FbxAnimationWriter
{
    /// <summary>
    /// FBX time units per second. One unit is 1/46186158000 of a second, a number
    /// chosen to divide exactly by every frame rate in use, so no frame time is
    /// ever rounded.
    /// </summary>
    public const long TimeUnitsPerSecond = 46186158000L;

    /// <summary>The version stamped on a curve's key arrays.</summary>
    private const int KeyVersion = 4009;

    private const int LinearKey = 0x00000004;

    private static readonly string[] Axes = ["d|X", "d|Y", "d|Z"];

    /// <summary>
    /// The name an object's <c>Class::Name</c> prefix uses, which is not always
    /// the record's own name. Autodesk writes <c>AnimStack::</c> over an
    /// <c>AnimationStack</c> record, and readers key off the prefix, so getting
    /// this wrong produces a file whose animation is present and inert.
    /// </summary>
    private static readonly Dictionary<string, string> ClassAliases = new()
    {
        ["AnimationStack"] = "AnimStack",
        ["AnimationLayer"] = "AnimLayer",
        ["AnimationCurveNode"] = "AnimCurveNode",
        ["AnimationCurve"] = "AnimCurve",
    };

    private static FbxObject Add(FbxScene scene, string className, string name, string subClass)
    {
        FbxObject o = scene.AddObject(className, name, subClass);

        if (ClassAliases.TryGetValue(className, out string? alias))
            o.QualifiedName = $"{alias}::{name}";

        return o;
    }

    public static long ToFbxTime(float seconds) => (long)MathF.Round(seconds * TimeUnitsPerSecond);

    /// <summary>
    /// Builds a document holding the skeleton as a node hierarchy and the
    /// animation as one stack of curves over it.
    /// </summary>
    public static FbxDocument Build(Skeleton skeleton, SampledAnimation animation, string takeName)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(animation);

        FbxDocument document = NewDocument(takeName, ToFbxTime(animation.Duration));
        var scene = new FbxScene(document);

        FbxObject[] models = AddSkeleton(scene, skeleton);
        AddAnimation(scene, skeleton, models, animation, takeName);

        // Writes the objects and connections into the document and refreshes the
        // Definitions counts.
        scene.Flush();

        // Flush appends Definitions, but a reader sizes its tables from it before
        // it reaches Objects, so it has to come first.
        FbxNode? definitions = document["Definitions"];
        if (definitions is not null)
        {
            document.Nodes.Remove(definitions);
            int objects = document.Nodes.FindIndex(n => n.Name == "Objects");
            document.Nodes.Insert(objects < 0 ? document.Nodes.Count : objects, definitions);
        }

        AddTakes(document, takeName, ToFbxTime(animation.Duration));

        return document;
    }

    /// <summary>
    /// The records every reader expects before it will look at the rest: a header
    /// with a timestamp, the axis convention, and the tick rate.
    /// </summary>
    private static FbxDocument NewDocument(string takeName, long stop)
    {
        var document = new FbxDocument { Version = FbxVersion.v7400 };

        var header = new FbxNode("FBXHeaderExtension");
        header.Nodes.Add(new FbxNode("FBXHeaderVersion", 1003));
        header.Nodes.Add(new FbxNode("FBXVersion", (int)FbxVersion.v7400));
        header.Nodes.Add(new FbxNode("EncryptionType", 0));

        // Not decoration: readers reject a header without a timestamp.
        DateTime now = DateTime.Now;
        var stamp = new FbxNode("CreationTimeStamp");
        stamp.Nodes.Add(new FbxNode("Version", 1000));
        stamp.Nodes.Add(new FbxNode("Year", now.Year));
        stamp.Nodes.Add(new FbxNode("Month", now.Month));
        stamp.Nodes.Add(new FbxNode("Day", now.Day));
        stamp.Nodes.Add(new FbxNode("Hour", now.Hour));
        stamp.Nodes.Add(new FbxNode("Minute", now.Minute));
        stamp.Nodes.Add(new FbxNode("Second", now.Second));
        stamp.Nodes.Add(new FbxNode("Millisecond", now.Millisecond));
        header.Nodes.Add(stamp);

        header.Nodes.Add(new FbxNode("Creator", "HKFBX"));
        document.Nodes.Add(header);

        document.Nodes.Add(new FbxNode("CreationTime", now.ToString("yyyy-MM-dd HH:mm:ss:fff")));
        document.Nodes.Add(new FbxNode("Creator", "HKFBX"));

        //
        // The scene document. A viewer reads ActiveAnimStackName to decide which
        // take to play, so a file without one can hold a perfectly good animation
        // and still open showing nothing moving.
        //
        var documents = new FbxNode("Documents");
        documents.Nodes.Add(new FbxNode("Count", 1));

        var scene = new FbxNode("Document");
        scene.Properties.Add(1L);
        scene.Properties.Add("");
        scene.Properties.Add("Scene");

        var sceneProperties = new FbxNode("Properties70");
        scene.Nodes.Add(sceneProperties);
        var sceneSettings = new FbxProperties(sceneProperties);
        sceneSettings.Set("SourceObject", "object", "", "");
        sceneSettings.Set("ActiveAnimStackName", "KString", "", "", takeName);

        scene.Nodes.Add(new FbxNode("RootNode", 0L));
        documents.Nodes.Add(scene);
        document.Nodes.Add(documents);

        document.Nodes.Add(new FbxNode("References"));

        var settings = new FbxNode("GlobalSettings");
        settings.Nodes.Add(new FbxNode("Version", 1000));

        var properties70 = new FbxNode("Properties70");
        settings.Nodes.Add(properties70);

        var globals = new FbxProperties(properties70);

        // Z-up, right-handed, which is Havok's convention as well as Max's, so
        // the numbers go across unchanged.
        globals.Set("UpAxis", "int", "Integer", "", 2);
        globals.Set("UpAxisSign", "int", "Integer", "", 1);
        globals.Set("FrontAxis", "int", "Integer", "", 1);
        globals.Set("FrontAxisSign", "int", "Integer", "", -1);
        globals.Set("CoordAxis", "int", "Integer", "", 0);
        globals.Set("CoordAxisSign", "int", "Integer", "", 1);
        globals.Set("OriginalUpAxis", "int", "Integer", "", 2);
        globals.Set("OriginalUpAxisSign", "int", "Integer", "", 1);
        globals.Set("UnitScaleFactor", "double", "Number", "", 1.0);
        globals.Set("OriginalUnitScaleFactor", "double", "Number", "", 1.0);

        // 6 is FbxTime::eFrames30, which is what Havok animations are authored
        // at. 11 is eFrames24 -- the enum is not a frame rate, and reading it as
        // one plays everything at the wrong speed.
        globals.Set("TimeMode", "enum", "", "", 6);
        globals.Set("TimeProtocol", "enum", "", "", 2);

        // The timeline a viewer opens on. Without it the take exists but the
        // scrubber has nothing to scrub.
        globals.Set("TimeSpanStart", "KTime", "Time", "", 0L);
        globals.Set("TimeSpanStop", "KTime", "Time", "", stop);

        document.Nodes.Add(settings);

        return document;
    }

    /// <summary>
    /// One Model per bone, parented as the skeleton says, each carrying its rest
    /// pose as its local transform.
    /// </summary>
    private static FbxObject[] AddSkeleton(FbxScene scene, Skeleton skeleton)
    {
        var models = new FbxObject[skeleton.Count];

        for (int i = 0; i < skeleton.Count; i++)
        {
            Bone bone = skeleton.Bones[i];

            FbxObject model = scene.AddObject("Model", bone.Name, "LimbNode");

            // 232 is the Model record version every 7.x writer stamps. Readers
            // check it before trusting the properties that follow.
            model.Node.Nodes.Add(new FbxNode("Version", 232));

            var properties = new FbxProperties(EnsureProperties70(model.Node));

            Vector3 euler = ToDegrees(bone.ReferencePose.ToEulerXyz());

            properties.Set("RotationActive", "bool", "", "", 1);
            properties.Set("InheritType", "enum", "", "", 1);
            properties.Set("ScalingMax", "Vector3D", "Vector", "", 0.0, 0.0, 0.0);
            properties.Set("DefaultAttributeIndex", "int", "Integer", "", 0);

            // "A+" means animatable and animated. "A" alone says a curve may
            // exist; a reader is entitled to ignore one when the property does
            // not admit to having it.
            properties.Set("Lcl Translation", "Lcl Translation", "", "A+",
                (double)bone.ReferencePose.Translation.X,
                (double)bone.ReferencePose.Translation.Y,
                (double)bone.ReferencePose.Translation.Z);
            properties.Set("Lcl Rotation", "Lcl Rotation", "", "A+",
                (double)euler.X, (double)euler.Y, (double)euler.Z);
            properties.Set("Lcl Scaling", "Lcl Scaling", "", "A+",
                (double)bone.ReferencePose.Scale.X,
                (double)bone.ReferencePose.Scale.Y,
                (double)bone.ReferencePose.Scale.Z);

            // FBX's boolean-as-char type, written as 'Y'. A real bool is not a
            // property type the format has.
            model.Node.Nodes.Add(new FbxNode("Shading", 'Y'));
            model.Node.Nodes.Add(new FbxNode("Culling", "CullingOff"));

            // Without a skeleton attribute an importer treats these as plain
            // nulls, and no armature comes out the other side. The name is left
            // empty, as Autodesk writes it -- the model carries the name.
            FbxObject attribute = scene.AddObject("NodeAttribute", string.Empty, "LimbNode");
            attribute.Node.Nodes.Add(new FbxNode("TypeFlags", "Skeleton"));
            scene.Connect(attribute, model);

            models[i] = model;
        }

        for (int i = 0; i < skeleton.Count; i++)
        {
            int parent = skeleton.Bones[i].ParentIndex;

            if (parent >= 0 && parent < models.Length) scene.Connect(models[i], models[parent]);
            else scene.ConnectToRoot(models[i]);
        }

        return models;
    }

    private static void AddAnimation(
        FbxScene scene, Skeleton skeleton, FbxObject[] models,
        SampledAnimation animation, string takeName)
    {
        FbxObject stack = Add(scene, "AnimationStack", takeName, string.Empty);
        var stackProperties = new FbxProperties(EnsureProperties70(stack.Node));

        long stop = ToFbxTime(animation.Duration);
        stackProperties.Set("LocalStart", "KTime", "Time", "", 0L);
        stackProperties.Set("LocalStop", "KTime", "Time", "", stop);
        stackProperties.Set("ReferenceStart", "KTime", "Time", "", 0L);
        stackProperties.Set("ReferenceStop", "KTime", "Time", "", stop);

        scene.ConnectToRoot(stack);

        FbxObject layer = Add(scene, "AnimationLayer", "Default", string.Empty);
        scene.Connect(layer, stack);

        var times = new long[animation.FrameCount];
        for (int f = 0; f < animation.FrameCount; f++) times[f] = ToFbxTime(animation.TimeOf(f));

        for (int track = 0; track < animation.TrackCount; track++)
        {
            int bone = animation.BoneForTrack(track);

            // A binding can name a bone the skeleton does not have, usually
            // because the animation was authored against a different rig. Those
            // tracks have nothing to drive.
            if (bone < 0 || bone >= models.Length) continue;

            var translation = new float[3][];
            var rotation = new float[3][];
            var scale = new float[3][];

            for (int axis = 0; axis < 3; axis++)
            {
                translation[axis] = new float[animation.FrameCount];
                rotation[axis] = new float[animation.FrameCount];
                scale[axis] = new float[animation.FrameCount];
            }

            for (int f = 0; f < animation.FrameCount; f++)
            {
                BoneTransform t = animation[f, track];
                Vector3 euler = ToDegrees(t.ToEulerXyz());

                translation[0][f] = t.Translation.X;
                translation[1][f] = t.Translation.Y;
                translation[2][f] = t.Translation.Z;

                rotation[0][f] = euler.X;
                rotation[1][f] = euler.Y;
                rotation[2][f] = euler.Z;

                scale[0][f] = t.Scale.X;
                scale[1][f] = t.Scale.Y;
                scale[2][f] = t.Scale.Z;
            }

            AddChannel(scene, layer, models[bone], "Lcl Translation", "T", times, translation);
            AddChannel(scene, layer, models[bone], "Lcl Rotation", "R", times, rotation);
            AddChannel(scene, layer, models[bone], "Lcl Scaling", "S", times, scale);
        }
    }

    /// <summary>
    /// One curve node for a property, with a curve under it per component.
    /// </summary>
    private static void AddChannel(
        FbxScene scene, FbxObject layer, FbxObject model,
        string property, string channel, long[] times, float[][] values)
    {
        FbxObject node = Add(scene, "AnimationCurveNode", channel, string.Empty);
        var properties = new FbxProperties(EnsureProperties70(node.Node));

        for (int axis = 0; axis < 3; axis++)
        {
            // The default is the first key, so the curve node and its curve agree
            // at time zero even for a reader that ignores one of them.
            double first = values[axis].Length > 0 ? values[axis][0] : 0.0;
            properties.Set(Axes[axis], "Number", string.Empty, "A", first);
        }

        scene.Connect(node, layer);
        scene.ConnectToProperty(node, model, property);

        for (int axis = 0; axis < 3; axis++)
        {
            FbxObject curve = AddCurve(scene, times, values[axis]);
            scene.ConnectToProperty(curve, node, Axes[axis]);
        }
    }

    /// <summary>Writes one component's keys as an <c>AnimationCurve</c>.</summary>
    /// <remarks>
    /// The keys are parallel arrays rather than records. The attribute arrays are
    /// the awkward part: they are run-length encoded, with <c>KeyAttrRefCount</c>
    /// saying how many consecutive keys share an entry. Every key here
    /// interpolates the same way, so there is exactly one run.
    /// </remarks>
    private static FbxObject AddCurve(FbxScene scene, long[] times, float[] values)
    {
        FbxObject curve = Add(scene, "AnimationCurve", string.Empty, string.Empty);
        FbxNode node = curve.Node;

        node.Nodes.Add(new FbxNode("Default", values.Length > 0 ? (double)values[0] : 0.0));
        node.Nodes.Add(new FbxNode("KeyVer", KeyVersion));
        node.Nodes.Add(new FbxNode("KeyTime", times));
        node.Nodes.Add(new FbxNode("KeyValueFloat", values));

        // Samples, not authored keys: straight lines between them are what the
        // animation already is, so there are no tangents to describe.
        node.Nodes.Add(new FbxNode("KeyAttrFlags", new[] { LinearKey }));
        node.Nodes.Add(new FbxNode("KeyAttrDataFloat", new[] { 0f, 0f, 0f, 0f }));
        node.Nodes.Add(new FbxNode("KeyAttrRefCount", new[] { values.Length }));

        return curve;
    }

    /// <summary>
    /// The take list, which predates AnimationStack and which some readers still
    /// consult to find the timeline.
    /// </summary>
    private static void AddTakes(FbxDocument document, string takeName, long stop)
    {
        var takes = new FbxNode("Takes");
        takes.Nodes.Add(new FbxNode("Current", takeName));

        var take = new FbxNode("Take", takeName);
        take.Nodes.Add(new FbxNode("FileName", takeName + ".tak"));

        var local = new FbxNode("LocalTime");
        local.Properties.Add(0L);
        local.Properties.Add(stop);
        take.Nodes.Add(local);

        var reference = new FbxNode("ReferenceTime");
        reference.Properties.Add(0L);
        reference.Properties.Add(stop);
        take.Nodes.Add(reference);

        takes.Nodes.Add(take);
        document.Nodes.Add(takes);
    }

    private static FbxNode EnsureProperties70(FbxNode node)
    {
        FbxNode? existing = node.Nodes.FirstOrDefault(n => n.Name == "Properties70");
        if (existing is not null) return existing;

        var properties = new FbxNode("Properties70");
        node.Nodes.Add(properties);
        return properties;
    }

    private static Vector3 ToDegrees(Vector3 radians) =>
        new(radians.X * 180f / MathF.PI, radians.Y * 180f / MathF.PI, radians.Z * 180f / MathF.PI);
}
