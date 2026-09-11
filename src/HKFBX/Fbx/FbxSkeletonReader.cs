using System.Globalization;
using System.Numerics;
using HKFBX.Model;
using LeanMeshIO;

namespace HKFBX.Fbx;

/// <summary>
/// Reads a rig and its ragdoll back out of an FBX scene.
/// </summary>
/// <remarks>
/// Everything is found by property rather than by name. Names do not survive the
/// journey: Blender caps an object name at 63 characters and replaces the overflow
/// with a hash, which eats the <c>_attach_point</c> suffix off two thirds of a
/// human skeleton's joints. The properties come back untouched, so they are what
/// the reader trusts -- and the two bodies a joint holds are named outright rather
/// than parsed out of the node's name.
///
/// The ragdoll skeleton is not read, because it is not written: a bone per body,
/// named after it, and the joints are the hierarchy. That holds in every skeleton
/// the game ships, so rebuilding it here is the same answer with one fewer thing
/// to keep consistent.
/// </remarks>
public static class FbxSkeletonReader
{
    /// <summary>Reads the scene.</summary>
    public static SkeletonFile Read(FbxDocument document, IReadOnlyList<string>? floatSlots = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        Skeleton rig = FbxAnimationReader.ReadSkeleton(document);

        var scene = new FbxScene(document);
        var nodes = scene.OfClass("Model").ToList();

        List<RagdollBody> bodies = [];
        var nodeOfBody = new Dictionary<string, FbxObject>(StringComparer.OrdinalIgnoreCase);

        foreach (FbxObject node in nodes)
        {
            string name = node.Properties.ValuesOf($"{FbxSkeletonWriter.BodyPrefix}ragdoll_bone")
                .FirstOrDefault() as string ?? string.Empty;

            if (name.Length == 0) continue;

            bodies.Add(ReadBody(node, name));
            nodeOfBody[name] = node;
        }

        List<RagdollJoint> joints = [];

        foreach (FbxObject node in nodes)
        {
            if (node.Properties.ValuesOf(FbxSkeletonWriter.TypeProperty).FirstOrDefault() is not string kind)
                continue;

            if (node.Properties.ValuesOf(FbxSkeletonWriter.FrameProperty).FirstOrDefault() as string == "A")
                continue;

            joints.Add(ReadJoint(scene, node, kind));
        }

        Skeleton? ragdoll = bodies.Count > 0 ? RagdollSkeletonOf(bodies, joints) : null;

        List<BoneMapping> rigToRagdoll = [];
        List<BoneMapping> ragdollToRig = [];

        foreach (RagdollBody body in bodies)
        {
            if (body.RigBone is not { Length: > 0 } bone) continue;

            rigToRagdoll.Add(new BoneMapping(bone, body.Name, BoneTransform.Identity));
            ragdollToRig.Add(new BoneMapping(body.Name, bone, BoneTransform.Identity));
        }

        return new SkeletonFile
        {
            Rig = rig,
            FloatSlots = floatSlots ?? [],
            Ragdoll = ragdoll,
            Bodies = bodies,
            Joints = joints,
            RigToRagdoll = rigToRagdoll,
            RagdollToRig = ragdollToRig,
        };
    }

    private static RagdollBody ReadBody(FbxObject node, string name)
    {
        var properties = node.Properties;

        string Text(string key) =>
            properties.ValuesOf($"{FbxSkeletonWriter.BodyPrefix}{key}").FirstOrDefault() as string ?? string.Empty;

        float Number(string key) =>
            (float)properties.GetDouble($"{FbxSkeletonWriter.BodyPrefix}{key}");

        CapsuleShape? shape = Text("shape") == "capsule"
            ? new CapsuleShape(ParseVector(Text("capsule_a")), ParseVector(Text("capsule_b")), Number("capsule_radius"))
            : null;

        string rigBone = Text("rig_bone");

        return new RagdollBody
        {
            Name = name,
            RigBone = rigBone.Length > 0 ? rigBone : null,
            Transform = FbxAnimationReader.LocalTransformOf(node),
            Shape = shape,
            MotionType = (byte)Number("motion_type"),
            QualityType = (byte)Number("quality_type"),
            CollisionFilterInfo = (uint)properties.GetDouble($"{FbxSkeletonWriter.BodyPrefix}collision_filter"),
            InverseMass = Number("inverse_mass"),
            InverseInertia = ParseVector(Text("inverse_inertia")),
            Friction = Number("friction"),
            Restitution = Number("restitution"),
            LinearDamping = Number("linear_damping"),
            AngularDamping = Number("angular_damping"),
            InRagdoll = Text("in_ragdoll") != "0",
        };
    }

    private static RagdollJoint ReadJoint(FbxScene scene, FbxObject node, string kind)
    {
        var properties = node.Properties;

        string bodyA = properties.ValuesOf("constraint_body_a").FirstOrDefault() as string ?? string.Empty;
        string bodyB = properties.ValuesOf("constraint_body_b").FirstOrDefault() as string ?? string.Empty;

        // The far frame rides on a child node marked as such, which is the half
        // ck-cmd's export drops.
        FbxObject? frameA = scene.ChildrenOf(node.Id)
            .FirstOrDefault(child =>
                child.Properties.ValuesOf(FbxSkeletonWriter.FrameProperty).FirstOrDefault() as string == "A");

        var joint = new RagdollJoint
        {
            Name = bodyA,
            BodyA = bodyA,
            BodyB = bodyB,
            Kind = kind switch
            {
                "Ragdoll" => JointKind.Ragdoll,
                "LimitedHinge" => JointKind.LimitedHinge,
                _ => JointKind.Other,
            },
            FrameB = ToMatrix(FbxAnimationReader.LocalTransformOf(node)),
            FrameA = frameA is null ? Matrix4x4.Identity : ToMatrix(FbxAnimationReader.LocalTransformOf(frameA)),
            MaxFrictionTorque = (float)properties.GetDouble("maxFriction"),
        };

        return joint.Kind switch
        {
            JointKind.Ragdoll => joint with
            {
                ConeMaxAngle = (float)properties.GetDouble("coneMaxAngle"),
                PlaneMinAngle = (float)properties.GetDouble("planeMinAngle"),
                PlaneMaxAngle = (float)properties.GetDouble("planeMaxAngle"),
                TwistMinAngle = (float)properties.GetDouble("twistMinAngle"),
                TwistMaxAngle = (float)properties.GetDouble("twistMaxAngle"),
            },
            JointKind.LimitedHinge => joint with
            {
                HingeMinAngle = (float)properties.GetDouble("minAngle"),
                HingeMaxAngle = (float)properties.GetDouble("maxAngle"),
            },
            _ => joint,
        };
    }

    /// <summary>
    /// The ragdoll skeleton, rebuilt from the bodies and what joins them.
    /// </summary>
    /// <remarks>
    /// A bone per body in the order the bodies were written, and a joint's far body
    /// is the near body's parent. Measured across the game's skeletons this is
    /// exactly what the file itself holds, in every one of them.
    /// </remarks>
    private static Skeleton RagdollSkeletonOf(List<RagdollBody> bodies, List<RagdollJoint> joints)
    {
        var inRagdoll = bodies.Where(body => body.InRagdoll).ToList();
        var indexOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < inRagdoll.Count; i++)
            indexOf.TryAdd(inRagdoll[i].Name, i);

        var parents = new int[inRagdoll.Count];
        Array.Fill(parents, -1);

        foreach (RagdollJoint joint in joints)
        {
            if (!indexOf.TryGetValue(joint.BodyA, out int child)) continue;
            if (!indexOf.TryGetValue(joint.BodyB, out int parent)) continue;

            parents[child] = parent;
        }

        var bones = new List<Bone>(inRagdoll.Count);

        for (int i = 0; i < inRagdoll.Count; i++)
            bones.Add(new Bone(inRagdoll[i].Name, parents[i], inRagdoll[i].Transform));

        return new Skeleton
        {
            Name = inRagdoll.Count > 0 ? inRagdoll[0].Name : "ragdoll",
            Bones = bones,
        };
    }

    private static Matrix4x4 ToMatrix(BoneTransform transform) =>
        Matrix4x4.CreateScale(transform.Scale)
        * Matrix4x4.CreateFromQuaternion(transform.Rotation)
        * Matrix4x4.CreateTranslation(transform.Translation);

    private static Vector3 ParseVector(string text)
    {
        string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 3) return Vector3.Zero;

        return new Vector3(Parse(parts[0]), Parse(parts[1]), Parse(parts[2]));

        static float Parse(string value) =>
            float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) ? parsed : 0;
    }
}
