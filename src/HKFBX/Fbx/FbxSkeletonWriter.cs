using System.Numerics;
using HKFBX.Model;
using LeanMeshIO;
using LeanMeshIO.Formats.Fbx;

namespace HKFBX.Fbx;

/// <summary>
/// Writes a skeleton packfile's contents into an FBX scene: the rig, the bodies
/// of the ragdoll, and the joints between them.
/// </summary>
/// <remarks>
/// What is native to FBX is written as FBX. A bone is a node, a body is a node
/// under the bone it drives, a joint is a node between two bodies, and every frame
/// is a node's own transform. That is most of a ragdoll, and it survives any tool
/// that can open the file.
///
/// What FBX has no word for is written as user properties. There is no cone, twist
/// or plane limit in the format, and no mass, motion type or collision filter, so
/// those ride as properties under the names ck-cmd's importrig already reads.
/// Constraint objects were considered and rejected: FBX's own are animation
/// constraints -- parent, position, aim -- that cannot express a limit, and
/// Blender drops them on export, so the file would lose its ragdoll on the first
/// round trip through the tool most likely to open it.
///
/// The improvement over ck-cmd is the joint's far side. Both frames are part of a
/// joint and ck-cmd computes both and writes only one, so the A side of every
/// joint is lost on the way out. Here it is a child node, which is a transform
/// rather than a number in a string.
/// </remarks>
public static class FbxSkeletonWriter
{
    /// <summary>Marks a node as a joint, as FBXWrangler names it.</summary>
    public const string AttachPointSuffix = "_attach_point";

    /// <summary>Separates the two body names in a joint's name.</summary>
    public const string NameSeparator = "_con_";

    /// <summary>The child node carrying the joint as the moving body sees it.</summary>
    public const string FrameANodeSuffix = "_frame_a";

    /// <summary>Names which kind of joint this is.</summary>
    public const string TypeProperty = "constraint_type";

    /// <summary>
    /// Marks the child node carrying the joint's far frame.
    /// </summary>
    /// <remarks>
    /// A property rather than the name, because names do not survive. Blender caps
    /// an object name at 63 characters and rewrites the overflow as a hash, which
    /// eats the suffix off 34 of the human skeleton's 53 nodes -- and with it any
    /// convention that depends on reading the end of a name.
    /// </remarks>
    public const string FrameProperty = "constraint_frame";

    /// <summary>Prefix on the properties a rigid body cannot say in FBX.</summary>
    public const string BodyPrefix = "hkb_";

    /// <summary>Builds a scene holding the rig and its ragdoll.</summary>
    public static FbxDocument Build(SkeletonFile file, BoneNaming naming = BoneNaming.Havok)
    {
        ArgumentNullException.ThrowIfNull(file);

        FbxDocument document = FbxAnimationWriter.NewDocument("Take 001", 0);
        var scene = new FbxScene(document);

        FbxObject[] rig = FbxAnimationWriter.AddSkeleton(scene, file.Rig, naming);

        var boneOf = new Dictionary<string, FbxObject>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < file.Rig.Count; i++)
            boneOf.TryAdd(file.Rig.Bones[i].Name, rig[i]);

        var bodyOf = AddBodies(scene, file, boneOf, naming);

        AddJoints(scene, file, bodyOf);

        scene.Flush();

        // Flush appends Definitions, and a reader sizes its tables from it before
        // it reaches Objects, so it has to come first.
        FbxNode? definitions = document["Definitions"];

        if (definitions is not null)
        {
            document.Nodes.Remove(definitions);
            int objects = document.Nodes.FindIndex(n => n.Name == "Objects");
            document.Nodes.Insert(objects < 0 ? document.Nodes.Count : objects, definitions);
        }

        return document;
    }

    /// <summary>
    /// A node per rigid body, under the rig bone it drives.
    /// </summary>
    /// <remarks>
    /// Under the bone, not under a ragdoll skeleton of its own, because that is
    /// where the same ragdoll sits in the creature's NIF and the two have to agree.
    /// The ragdoll's own bone name is kept as a property: it cannot be worked out
    /// from the rig bone's, which holds for the human and fails for every creature.
    /// </remarks>
    private static Dictionary<string, FbxObject> AddBodies(
        FbxScene scene, SkeletonFile file, Dictionary<string, FbxObject> boneOf, BoneNaming naming)
    {
        var bodyOf = new Dictionary<string, FbxObject>(StringComparer.OrdinalIgnoreCase);

        foreach (RagdollBody body in file.Bodies)
        {
            FbxObject node = AddNode(scene, BoneNames.Apply(body.Name, naming), body.Transform);

            if (body.RigBone is { } bone && boneOf.TryGetValue(bone, out FbxObject? parent))
                scene.Connect(node, parent);
            else
                scene.ConnectToRoot(node);

            var properties = node.Properties;

            properties.SetUserString($"{BodyPrefix}ragdoll_bone", body.Name);
            properties.SetUserString($"{BodyPrefix}rig_bone", body.RigBone ?? string.Empty);
            properties.SetUserString($"{BodyPrefix}in_ragdoll", body.InRagdoll ? "1" : "0");
            properties.SetUserFloat($"{BodyPrefix}motion_type", body.MotionType);
            properties.SetUserFloat($"{BodyPrefix}quality_type", body.QualityType);
            properties.SetUserFloat($"{BodyPrefix}collision_filter", body.CollisionFilterInfo);
            properties.SetUserFloat($"{BodyPrefix}inverse_mass", body.InverseMass);
            properties.SetUserString($"{BodyPrefix}inverse_inertia", Vector(body.InverseInertia));
            properties.SetUserFloat($"{BodyPrefix}friction", body.Friction);
            properties.SetUserFloat($"{BodyPrefix}restitution", body.Restitution);
            properties.SetUserFloat($"{BodyPrefix}linear_damping", body.LinearDamping);
            properties.SetUserFloat($"{BodyPrefix}angular_damping", body.AngularDamping);

            if (body.Shape is { } shape)
            {
                properties.SetUserString($"{BodyPrefix}shape", "capsule");
                properties.SetUserString($"{BodyPrefix}capsule_a", Vector(shape.VertexA));
                properties.SetUserString($"{BodyPrefix}capsule_b", Vector(shape.VertexB));
                properties.SetUserFloat($"{BodyPrefix}capsule_radius", shape.Radius);
            }

            bodyOf[body.Name] = node;
        }

        return bodyOf;
    }

    /// <summary>
    /// A node per joint, under the body it hangs from.
    /// </summary>
    private static void AddJoints(FbxScene scene, SkeletonFile file, Dictionary<string, FbxObject> bodyOf)
    {
        foreach (RagdollJoint joint in file.Joints)
        {
            if (!bodyOf.TryGetValue(joint.BodyB, out FbxObject? far)
                && !bodyOf.TryGetValue(joint.BodyA, out far))
            {
                continue;
            }

            // Far body first, then the body it holds: reading it back, the parent
            // gives one end and the name's second half the other.
            string name = $"{joint.BodyB}{NameSeparator}{joint.BodyA}{AttachPointSuffix}";

            FbxObject node = AddNode(scene, name, ToTransform(joint.FrameB));
            scene.Connect(node, far);

            var properties = node.Properties;

            properties.SetUserString(FrameProperty, "B");
            properties.SetUserString(TypeProperty, joint.Kind switch
            {
                JointKind.Ragdoll => "Ragdoll",
                JointKind.LimitedHinge => "LimitedHinge",
                _ => "Unknown",
            });

            // Named outright rather than left to be parsed out of the node's name,
            // which a rename in any editor breaks.
            properties.SetUserString("constraint_body_a", joint.BodyA);
            properties.SetUserString("constraint_body_b", joint.BodyB);

            switch (joint.Kind)
            {
                case JointKind.Ragdoll:
                    properties.SetUserFloat("coneMaxAngle", joint.ConeMaxAngle);
                    properties.SetUserFloat("planeMinAngle", joint.PlaneMinAngle);
                    properties.SetUserFloat("planeMaxAngle", joint.PlaneMaxAngle);
                    properties.SetUserFloat("twistMinAngle", joint.TwistMinAngle);
                    properties.SetUserFloat("twistMaxAngle", joint.TwistMaxAngle);
                    break;

                case JointKind.LimitedHinge:
                    properties.SetUserFloat("minAngle", joint.HingeMinAngle);
                    properties.SetUserFloat("maxAngle", joint.HingeMaxAngle);
                    break;
            }

            properties.SetUserFloat("maxFriction", joint.MaxFrictionTorque);

            // The far side of the joint, which ck-cmd computes and throws away.
            FbxObject frameA = AddNode(scene, name + FrameANodeSuffix, ToTransform(joint.FrameA));
            frameA.Properties.SetUserString(FrameProperty, "A");
            scene.Connect(frameA, node);
        }
    }

    private static FbxObject AddNode(FbxScene scene, string name, BoneTransform transform)
    {
        FbxObject node = scene.AddObject("Model", name, "Null");
        node.Node.Nodes.Add(new FbxNode("Version", 232));

        var properties = new FbxProperties(FbxAnimationWriter.EnsureProperties70(node.Node));

        Vector3 euler = ToDegrees(transform.ToEulerXyz());

        properties.Set("RotationActive", "bool", "", "", 1);
        properties.Set("InheritType", "enum", "", "", 1);
        properties.Set("Lcl Translation", "Lcl Translation", "", "A+",
            (double)transform.Translation.X, (double)transform.Translation.Y, (double)transform.Translation.Z);
        properties.Set("Lcl Rotation", "Lcl Rotation", "", "A+",
            (double)euler.X, (double)euler.Y, (double)euler.Z);
        properties.Set("Lcl Scaling", "Lcl Scaling", "", "A+",
            (double)transform.Scale.X, (double)transform.Scale.Y, (double)transform.Scale.Z);

        node.Node.Nodes.Add(new FbxNode("Shading", 'Y'));
        node.Node.Nodes.Add(new FbxNode("Culling", "CullingOff"));

        return node;
    }

    /// <summary>A Havok constraint frame, as a transform.</summary>
    private static BoneTransform ToTransform(Matrix4x4 frame)
    {
        if (!Matrix4x4.Decompose(frame, out Vector3 scale, out Quaternion rotation, out Vector3 translation))
            return new BoneTransform(frame.Translation, Quaternion.Identity, Vector3.One);

        return new BoneTransform(translation, rotation, scale);
    }

    private static string Vector(Vector3 v) =>
        $"{v.X.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
        $"{v.Y.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
        $"{v.Z.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    private static Vector3 ToDegrees(Vector3 radians) =>
        new(radians.X * 180f / MathF.PI, radians.Y * 180f / MathF.PI, radians.Z * 180f / MathF.PI);
}
