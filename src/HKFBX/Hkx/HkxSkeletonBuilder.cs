using System.Collections;
using System.Numerics;
using System.Reflection;
using HKFBX.Model;
using HKX2;

namespace HKFBX.Hkx;

/// <summary>
/// Writes a skeleton packfile for a rig the template does not have, by cloning
/// the template's objects as prototypes.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="HkxSkeletonFile.Write"/> edits a template by name and stops at its
/// structure: a bone or a body the template lacks is not created, because the
/// counts are what the mappers, the ragdoll instance and every index in the file
/// are built over. A new creature has a new rig, so here the structure is rebuilt
/// -- the two skeletons, the bodies, the joints, the mappers, the ragdoll
/// instance and the resource tree -- and only the scaffolding nobody models is
/// taken from the template: a body's motion state, broad phase handle and
/// material, a joint's atoms, the constants Havok's exporter left in every
/// creature alike. Each is a deep clone of one template object with the modelled
/// values written over it, so what this cannot describe is still what the
/// exporter wrote.
/// </para>
/// <para>
/// The template decides the kind of file: a creature's skeleton.hkx makes a
/// creature's, with the same six named variants in the same order. A joint kind
/// the template has no example of cannot be cloned and is reported by throwing,
/// since a ragdoll missing a joint falls apart rather than degrades.
/// </para>
/// </remarks>
public static class HkxSkeletonBuilder
{
    /// <summary>Writes <paramref name="file"/> as a packfile shaped like the template.</summary>
    public static void Write(string templatePath, SkeletonFile file, string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templatePath);
        ArgumentNullException.ThrowIfNull(file);

        var root = (hkRootLevelContainer)Util.ReadHKX(templatePath);
        Build(root, file);

        using FileStream stream = File.Create(outputPath);
        Util.WriteHKX(root, HKXHeader.SkyrimSE(), stream);
    }

    /// <summary>
    /// Rebuilds a template's object graph in place around <paramref name="file"/>.
    /// </summary>
    public static void Build(hkRootLevelContainer template, SkeletonFile file)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(file);

        List<hkReferencedObject> variants = template.m_namedVariants
            .Select(v => v?.m_variant).OfType<hkReferencedObject>().ToList();

        hkaAnimationContainer container = variants.OfType<hkaAnimationContainer>().FirstOrDefault()
            ?? throw new InvalidDataException("the template holds no animation container");

        if (container.m_skeletons.Count == 0)
            throw new InvalidDataException("the template holds no hkaSkeleton");

        // ------------------------------------------------------------ the rig

        hkaSkeleton rig = container.m_skeletons[0];
        List<string> oldRigNames = rig.m_bones.Select(b => b.m_name).ToList();
        List<string> oldRagdollNames = container.m_skeletons.Count > 1 ? container.m_skeletons[1].m_bones.Select(b => b.m_name).ToList() : [];
        FillSkeleton(rig, file.Rig, RigLocks(file.Rig));
        rig.m_name = file.Rig.Name.Length > 0 ? file.Rig.Name : rig.m_name;
        rig.m_floatSlots = file.FloatSlots.ToList();
        rig.m_referenceFloats = rig.m_referenceFloats.Count == file.FloatSlots.Count
            ? rig.m_referenceFloats
            : new float[file.FloatSlots.Count];

        var rigIndex = IndexOf(file.Rig);

        if (!file.HasRagdoll || file.Ragdoll is null)
        {
            // A rig alone: the template's ragdoll, physics and mappers go, since
            // every one of them indexes bones the rig no longer has.
            container.m_skeletons = [rig];
            template.m_namedVariants = template.m_namedVariants
                .Where(v => v?.m_variant is hkaAnimationContainer or hkMemoryResourceContainer).ToList();

            foreach (hkMemoryResourceContainer resources in variants.OfType<hkMemoryResourceContainer>())
                resources.m_children = [RigTree(file.Rig)];

            return;
        }

        // ------------------------------------------------------------ the ragdoll

        hkaSkeleton ragdoll = container.m_skeletons.Count > 1 ? container.m_skeletons[1] : new hkaSkeleton();
        container.m_skeletons = [rig, ragdoll];

        var bodyOf = new Dictionary<string, RagdollBody>(StringComparer.OrdinalIgnoreCase);

        foreach (RagdollBody body in file.Bodies)
            bodyOf[body.Name] = body;

        Skeleton ragdollSkeleton = LocalPoses(file.Ragdoll, bodyOf);
        FillSkeleton(ragdoll, ragdollSkeleton, RootOnly(ragdollSkeleton));
        ragdoll.m_name = ragdollSkeleton.Bones.Count > 0 ? ragdollSkeleton.Bones[0].Name : ragdollSkeleton.Name;
        ragdoll.m_floatSlots = [];
        ragdoll.m_referenceFloats = [];

        var ragdollIndex = IndexOf(ragdollSkeleton);

        // ------------------------------------------------------------ the bodies

        hkpPhysicsData physics = variants.OfType<hkpPhysicsData>().FirstOrDefault()
            ?? throw new InvalidDataException("the template holds no physics data");
        hkpPhysicsSystem system = physics.m_systems.FirstOrDefault()
            ?? throw new InvalidDataException("the template's physics data holds no system");
        hkaRagdollInstance instance = variants.OfType<hkaRagdollInstance>().FirstOrDefault()
            ?? throw new InvalidDataException("the template holds no ragdoll instance");

        List<hkpRigidBody> prototypes = system.m_rigidBodies.OfType<hkpRigidBody>().ToList();

        if (prototypes.Count == 0)
            throw new InvalidDataException("the template's physics system holds no rigid body");

        var built = new Dictionary<string, hkpRigidBody>(StringComparer.OrdinalIgnoreCase);

        foreach (RagdollBody body in file.Bodies)
        {
            // The template's own body of that name where it has one, so a creature
            // rebuilt over itself keeps each body's scaffolding and a shape this
            // cannot model; otherwise the first body that moves the same way.
            hkpRigidBody prototype =
                prototypes.FirstOrDefault(p => string.Equals(p.m_name, body.Name, StringComparison.OrdinalIgnoreCase))
                ?? prototypes.FirstOrDefault(p => p.m_motion.m_type == body.MotionType)
                ?? prototypes[0];
            built[body.Name] = BuildBody(prototype, body);
        }

        // The instance lists the bodies in the ragdoll skeleton's order, which is
        // what boneToRigidBodyMap indexes; the system lists them in the order the
        // exporter walked the scene, depth first, and so does the resource tree.
        List<int> depthFirst = DepthFirst(ragdollSkeleton);

        var instanceBodies = new List<hkpRigidBody>();
        var map = new List<int>();

        for (int i = 0; i < ragdollSkeleton.Bones.Count; i++)
        {
            if (!built.TryGetValue(ragdollSkeleton.Bones[i].Name, out hkpRigidBody? body))
                throw new InvalidDataException($"ragdoll bone '{ragdollSkeleton.Bones[i].Name}' has no rigid body");

            instanceBodies.Add(body);
            map.Add(i);
        }

        var systemBodies = depthFirst.Select(i => built[ragdollSkeleton.Bones[i].Name]).ToList();

        foreach (RagdollBody body in file.Bodies)
            if (!ragdollIndex.ContainsKey(body.Name))
                systemBodies.Add(built[body.Name]);

        // ------------------------------------------------------------ the joints

        // The same joint is two objects, one per list, and the exporter did not
        // write them alike: the chicken's instance copies carry motors its system
        // copies do not. So each list clones from its own.
        List<hkpConstraintInstance> systemPrototypes = system.m_constraints.OfType<hkpConstraintInstance>().ToList();
        List<hkpConstraintInstance> instancePrototypes = instance.m_constraints.OfType<hkpConstraintInstance>().ToList();
        List<hkpConstraintInstance> jointPrototypes = [.. systemPrototypes, .. instancePrototypes];

        // Every body is the child of at most one joint, and a joint is listed
        // where its child is.
        var jointByChild = new Dictionary<string, RagdollJoint>(StringComparer.OrdinalIgnoreCase);

        foreach (RagdollJoint joint in file.Joints)
            jointByChild[joint.BodyA] = joint;

        // A motor is one object the exporter shares between every joint of the
        // ragdoll, so the clones share it too rather than each taking a copy.
        var shared = new List<object>(prototypes);
        shared.AddRange(jointPrototypes.SelectMany(MotorsOf));

        hkpConstraintInstance Joint(RagdollJoint joint, List<hkpConstraintInstance> own)
        {
            // The template's own joint for this child where it has one -- a
            // creature rebuilt over itself keeps each joint's scaffolding -- and
            // otherwise the first joint of the kind, from this list before the other.
            hkpConstraintInstance prototype =
                own.FirstOrDefault(p => KindOf(p) == joint.Kind
                    && string.Equals(p.m_entities.FirstOrDefault()?.m_name, joint.BodyA, StringComparison.OrdinalIgnoreCase))
                ?? own.FirstOrDefault(p => KindOf(p) == joint.Kind)
                ?? jointPrototypes.FirstOrDefault(p => KindOf(p) == joint.Kind)
                ?? throw new InvalidDataException(
                    $"the template has no {joint.Kind} joint to clone for '{joint.BodyA}' -> '{joint.BodyB}'");

            if (!built.TryGetValue(joint.BodyA, out hkpRigidBody? a) || !built.TryGetValue(joint.BodyB, out hkpRigidBody? b))
                throw new InvalidDataException($"joint '{joint.BodyA}' -> '{joint.BodyB}' names a body the file does not have");

            return BuildJoint(prototype, joint, a, b, shared);
        }

        var instanceJoints = new List<hkpConstraintInstance>();

        for (int i = 0; i < ragdollSkeleton.Bones.Count; i++)
            if (jointByChild.TryGetValue(ragdollSkeleton.Bones[i].Name, out RagdollJoint? joint))
                instanceJoints.Add(Joint(joint, instancePrototypes));

        var systemJoints = new List<hkpConstraintInstance>();

        foreach (int i in depthFirst)
            if (jointByChild.TryGetValue(ragdollSkeleton.Bones[i].Name, out RagdollJoint? joint))
                systemJoints.Add(Joint(joint, systemPrototypes));

        foreach (RagdollJoint joint in file.Joints)
            if (!ragdollIndex.ContainsKey(joint.BodyA))
                systemJoints.Add(Joint(joint, systemPrototypes));

        instance.m_rigidBodies = instanceBodies;
        instance.m_constraints = instanceJoints;
        instance.m_boneToRigidBodyMap = map;
        instance.m_skeleton = ragdoll;

        system.m_rigidBodies = systemBodies;
        system.m_constraints = systemJoints;

        // ------------------------------------------------------------ the mappers

        List<hkaSkeletonMapper> mappers = variants.OfType<hkaSkeletonMapper>().ToList();

        hkaSkeletonMapper? toRagdoll = mappers.FirstOrDefault(m => ReferenceEquals(m.m_mapping.m_skeletonA, rig));
        hkaSkeletonMapper? toRig = mappers.FirstOrDefault(m => !ReferenceEquals(m.m_mapping.m_skeletonA, rig));

        if (toRagdoll is not null)
            FillMapper(toRagdoll, rig, ragdoll, file.RigToRagdoll, rigIndex, ragdollIndex, oldRigNames, oldRagdollNames);

        if (toRig is not null)
            FillMapper(toRig, ragdoll, rig, file.RagdollToRig, ragdollIndex, rigIndex, oldRagdollNames, oldRigNames);

        // ------------------------------------------------------------ the resource tree

        foreach (hkMemoryResourceContainer resources in variants.OfType<hkMemoryResourceContainer>())
        {
            hkMemoryResourceContainer? ragdollPrototype = Descendants(resources)
                .FirstOrDefault(c => c.m_resourceHandles.Count > 0);

            resources.m_children =
            [
                RigTree(file.Rig),
                RagdollTree(ragdollSkeleton, systemBodies, ragdollPrototype),
            ];

            // A body the ragdoll does not drive -- the flame atronach's two --
            // hangs off the root of the tree with the same two handles.
            foreach (RagdollBody body in file.Bodies)
                if (!ragdollIndex.ContainsKey(body.Name))
                    resources.m_children.Add(BodyContainer(body.Name, built[body.Name], ragdollPrototype));
        }
    }

    // ---------------------------------------------------------------- skeletons

    private static void FillSkeleton(hkaSkeleton skeleton, Skeleton source, Func<int, bool> unlocked)
    {
        var bones = new List<hkaBone>(source.Count);
        var parents = new List<short>(source.Count);
        var poses = new List<Matrix4x4>(source.Count);

        for (int i = 0; i < source.Count; i++)
        {
            Bone bone = source.Bones[i];

            bones.Add(new hkaBone { m_name = bone.Name, m_lockTranslation = !unlocked(i) });
            parents.Add((short)bone.ParentIndex);
            poses.Add(Pack(bone.ReferencePose));
        }

        // A local frame indexes a bone; with the bones renumbered it points at the
        // wrong one, and nothing here can say which it meant.
        bool sameBones = skeleton.m_bones.Count == source.Count
            && skeleton.m_bones.Select(b => b.m_name).SequenceEqual(source.Bones.Select(b => b.Name), StringComparer.Ordinal);

        if (!sameBones)
            skeleton.m_localFrames = [];

        skeleton.m_bones = bones;
        skeleton.m_parentIndices = parents;
        skeleton.m_referencePose = poses;
    }

    /// <summary>
    /// Havok's exporter locks translation on every bone but the root and the bones
    /// hung directly from it -- the sabre cat's root and pelvis, the human's root
    /// and centre of mass.
    /// </summary>
    private static Func<int, bool> RigLocks(Skeleton rig) =>
        i => rig.Bones[i].ParentIndex < 0 || rig.Bones[rig.Bones[i].ParentIndex].ParentIndex < 0;

    /// <summary>A ragdoll skeleton leaves only its root free.</summary>
    private static Func<int, bool> RootOnly(Skeleton skeleton) =>
        i => skeleton.Bones[i].ParentIndex < 0;

    /// <summary>
    /// The ragdoll skeleton with each bone posed relative to its parent, as the
    /// file stores it, taken from the bodies where they are the model-space truth.
    /// </summary>
    private static Skeleton LocalPoses(Skeleton ragdoll, Dictionary<string, RagdollBody> bodyOf)
    {
        var bones = new List<Bone>(ragdoll.Count);

        for (int i = 0; i < ragdoll.Count; i++)
        {
            Bone bone = ragdoll.Bones[i];

            if (!bodyOf.TryGetValue(bone.Name, out RagdollBody? body))
            {
                bones.Add(bone);
                continue;
            }

            Matrix4x4 world = WorldOf(body.Transform);
            Matrix4x4 local = world;

            if (bone.ParentIndex >= 0 && bodyOf.TryGetValue(ragdoll.Bones[bone.ParentIndex].Name, out RagdollBody? parent)
                && Matrix4x4.Invert(WorldOf(parent.Transform), out Matrix4x4 inverse))
            {
                local = world * inverse;
            }

            bones.Add(bone with
            {
                ReferencePose = new BoneTransform(
                    local.Translation,
                    Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(local)),
                    Vector3.One),
            });
        }

        return new Skeleton { Name = ragdoll.Name, Bones = bones };
    }

    private static Matrix4x4 WorldOf(BoneTransform t) =>
        Matrix4x4.CreateFromQuaternion(t.Rotation) * Matrix4x4.CreateTranslation(t.Translation);

    private static Dictionary<string, int> IndexOf(Skeleton skeleton)
    {
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < skeleton.Count; i++)
            index.TryAdd(skeleton.Bones[i].Name, i);

        return index;
    }

    private static List<int> DepthFirst(Skeleton skeleton)
    {
        var order = new List<int>(skeleton.Count);

        void Visit(int bone)
        {
            order.Add(bone);

            foreach (int child in skeleton.ChildrenOf(bone))
                Visit(child);
        }

        foreach (int root in skeleton.Roots())
            Visit(root);

        return order;
    }

    // ---------------------------------------------------------------- bodies

    private static hkpRigidBody BuildBody(hkpRigidBody prototype, RagdollBody source)
    {
        var body = Clone(prototype, new Dictionary<object, object>(ReferenceEqualityComparer.Instance));

        body.m_name = source.Name;
        body.m_properties = [];
        body.m_localFrame = null;

        hkpMaxSizeMotion motion = body.m_motion;
        motion.m_type = source.MotionType;
        motion.m_inertiaAndMassInv = new Vector4(source.InverseInertia, source.InverseMass);
        motion.m_linearVelocity = Vector4.Zero;
        motion.m_angularVelocity = Vector4.Zero;

        Matrix4x4 world = WorldOf(source.Transform);
        hkMotionState state = motion.m_motionState;
        state.m_transform = world;
        state.m_deltaAngle = Vector4.Zero;
        state.m_linearDamping = (Half)source.LinearDamping;
        state.m_angularDamping = (Half)source.AngularDamping;

        // The swept transform is the body at rest: its centre of mass, which the
        // exporter puts at the middle of the capsule, at both ends of the step.
        Vector3 centre = source.Shape is { } capsule ? (capsule.VertexA + capsule.VertexB) / 2f : Vector3.Zero;
        Vector3 centreWorld = Vector3.Transform(centre, world);
        hkSweptTransform swept = state.m_sweptTransform;
        swept.m_centerOfMassLocal = new Vector4(centre, 0f);
        swept.m_centerOfMass0 = new Vector4(centreWorld, 0f);
        swept.m_centerOfMass1 = new Vector4(centreWorld, 0f);
        swept.m_rotation0 = source.Transform.Rotation;
        swept.m_rotation1 = source.Transform.Rotation;

        // The radius of the sphere around the shape's local bounding box, which is
        // what the exporter wrote for every creature: the sabre cat's thigh, a
        // capsule 20 long and 8.25 wide, carries 21.67.
        if (source.Shape is { } shape)
        {
            Vector3 min = Vector3.Min(shape.VertexA, shape.VertexB) - new Vector3(shape.Radius);
            Vector3 max = Vector3.Max(shape.VertexA, shape.VertexB) + new Vector3(shape.Radius);
            state.m_objectRadius = ((max - min) / 2f).Length();
        }

        hkpLinkedCollidable collidable = body.m_collidable;

        if (source.Shape is { } fitted)
        {
            var capsuleShape = collidable.m_shape is hkpCapsuleShape template
                ? Clone(template, new Dictionary<object, object>(ReferenceEqualityComparer.Instance))
                : new hkpCapsuleShape();

            capsuleShape.m_vertexA = new Vector4(fitted.VertexA, fitted.Radius);
            capsuleShape.m_vertexB = new Vector4(fitted.VertexB, fitted.Radius);
            capsuleShape.m_radius = fitted.Radius;
            collidable.m_shape = capsuleShape;
        }

        collidable.m_broadPhaseHandle.m_objectQualityType = (sbyte)source.QualityType;
        collidable.m_broadPhaseHandle.m_collisionFilterInfo = source.CollisionFilterInfo;
        collidable.m_collisionEntries = [];

        body.m_material.m_friction = source.Friction;
        body.m_material.m_restitution = source.Restitution;

        return body;
    }

    // ---------------------------------------------------------------- joints

    private static JointKind KindOf(hkpConstraintInstance constraint) => constraint.m_data switch
    {
        hkpRagdollConstraintData => JointKind.Ragdoll,
        hkpLimitedHingeConstraintData => JointKind.LimitedHinge,
        _ => JointKind.Other,
    };

    private static IEnumerable<hkpConstraintMotor> MotorsOf(hkpConstraintInstance constraint)
    {
        switch (constraint.m_data)
        {
            case hkpRagdollConstraintData ragdoll:
                foreach (hkpConstraintMotor? ragdollMotor in ragdoll.m_atoms.m_ragdollMotors.m_motors)
                    if (ragdollMotor is not null) yield return ragdollMotor;
                break;
            case hkpLimitedHingeConstraintData hinge:
                if (hinge.m_atoms.m_angMotor.m_motor is { } hingeMotor) yield return hingeMotor;
                break;
        }
    }

    private static hkpConstraintInstance BuildJoint(
        hkpConstraintInstance prototype, RagdollJoint source, hkpRigidBody a, hkpRigidBody b,
        IEnumerable<object> shared)
    {
        // The prototype's bodies are shared rather than cloned along with it, and
        // then replaced: cloning a joint must not clone half the ragdoll.
        var memo = new Dictionary<object, object>(ReferenceEqualityComparer.Instance);

        foreach (object kept in shared)
            memo[kept] = kept;

        var joint = Clone(prototype, memo);

        joint.m_name = source.BodyA;
        joint.m_entities = [a, b];

        switch (joint.m_data)
        {
            case hkpRagdollConstraintData ragdoll:
                ragdoll.m_atoms.m_transforms.m_transformA = source.FrameA;
                ragdoll.m_atoms.m_transforms.m_transformB = source.FrameB;
                ragdoll.m_atoms.m_twistLimit.m_minAngle = source.TwistMinAngle;
                ragdoll.m_atoms.m_twistLimit.m_maxAngle = source.TwistMaxAngle;
                ragdoll.m_atoms.m_coneLimit.m_maxAngle = source.ConeMaxAngle;
                ragdoll.m_atoms.m_planesLimit.m_minAngle = source.PlaneMinAngle;
                ragdoll.m_atoms.m_planesLimit.m_maxAngle = source.PlaneMaxAngle;
                ragdoll.m_atoms.m_angFriction.m_maxFrictionTorque = source.MaxFrictionTorque;
                break;

            case hkpLimitedHingeConstraintData hinge:
                hinge.m_atoms.m_transforms.m_transformA = source.FrameA;
                hinge.m_atoms.m_transforms.m_transformB = source.FrameB;
                hinge.m_atoms.m_angLimit.m_minAngle = source.HingeMinAngle;
                hinge.m_atoms.m_angLimit.m_maxAngle = source.HingeMaxAngle;
                hinge.m_atoms.m_angFriction.m_maxFrictionTorque = source.MaxFrictionTorque;
                break;
        }

        return joint;
    }

    // ---------------------------------------------------------------- mappers

    private static void FillMapper(
        hkaSkeletonMapper mapper, hkaSkeleton a, hkaSkeleton b, IReadOnlyList<BoneMapping> mappings,
        Dictionary<string, int> indexA, Dictionary<string, int> indexB,
        List<string> oldNamesA, List<string> oldNamesB)
    {
        hkaSkeletonMapperData data = mapper.m_mapping;

        // A chain mapping is nothing the model carries -- four creatures have
        // them, the vampire brute's spine among them -- so the template's are
        // kept wherever their four bones are still there, found again by name.
        var chains = new List<hkaSkeletonMapperDataChainMapping>();

        foreach (hkaSkeletonMapperDataChainMapping chain in data.m_chainMappings)
        {
            if (Renamed(chain.m_startBoneA, oldNamesA, indexA) is not { } startA
                || Renamed(chain.m_endBoneA, oldNamesA, indexA) is not { } endA
                || Renamed(chain.m_startBoneB, oldNamesB, indexB) is not { } startB
                || Renamed(chain.m_endBoneB, oldNamesB, indexB) is not { } endB)
                continue;

            chain.m_startBoneA = (short)startA;
            chain.m_endBoneA = (short)endA;
            chain.m_startBoneB = (short)startB;
            chain.m_endBoneB = (short)endB;
            chains.Add(chain);
        }

        data.m_skeletonA = a;
        data.m_skeletonB = b;

        var simple = new List<hkaSkeletonMapperDataSimpleMapping>();
        var mapped = new HashSet<int>();

        foreach (BoneMapping mapping in mappings)
        {
            if (!indexA.TryGetValue(mapping.FromBone, out int from) || !indexB.TryGetValue(mapping.ToBone, out int to))
                continue;

            simple.Add(new hkaSkeletonMapperDataSimpleMapping
            {
                m_boneA = (short)from,
                m_boneB = (short)to,
                m_aFromBTransform = Pack(mapping.Transform),
            });
            mapped.Add(to);
        }

        data.m_simpleMappings = simple;
        data.m_chainMappings = chains;
        data.m_unmappedBones = Enumerable.Range(0, b.m_bones.Count).Where(i => !mapped.Contains(i)).Select(i => (short)i).ToList();
    }

    private static int? Renamed(int oldIndex, List<string> oldNames, Dictionary<string, int> index) =>
        oldIndex >= 0 && oldIndex < oldNames.Count && index.TryGetValue(oldNames[oldIndex], out int found) ? found : null;

    // ---------------------------------------------------------------- resources

    private static hkMemoryResourceContainer RigTree(Skeleton rig)
    {
        var containers = new hkMemoryResourceContainer[rig.Count];

        for (int i = 0; i < rig.Count; i++)
            containers[i] = new hkMemoryResourceContainer { m_name = rig.Bones[i].Name, m_children = [], m_resourceHandles = [] };

        for (int i = 0; i < rig.Count; i++)
            if (rig.Bones[i].ParentIndex >= 0)
                containers[rig.Bones[i].ParentIndex].m_children.Add(containers[i]);

        return containers.Length > 0 ? containers[rig.Roots().First()] : new hkMemoryResourceContainer();
    }

    private static hkMemoryResourceContainer RagdollTree(
        Skeleton ragdoll, IReadOnlyList<hkpRigidBody> bodies, hkMemoryResourceContainer? prototype)
    {
        var bodyOf = bodies.ToDictionary(b => b.m_name, StringComparer.OrdinalIgnoreCase);
        var containers = new hkMemoryResourceContainer[ragdoll.Count];

        for (int i = 0; i < ragdoll.Count; i++)
        {
            string name = ragdoll.Bones[i].Name;

            containers[i] = bodyOf.TryGetValue(name, out hkpRigidBody? body)
                ? BodyContainer(name, body, prototype)
                : new hkMemoryResourceContainer { m_name = name, m_children = [], m_resourceHandles = [] };
        }

        for (int i = 0; i < ragdoll.Count; i++)
            if (ragdoll.Bones[i].ParentIndex >= 0)
                containers[ragdoll.Bones[i].ParentIndex].m_children.Add(containers[i]);

        return containers.Length > 0 ? containers[ragdoll.Roots().First()] : new hkMemoryResourceContainer();
    }

    /// <summary>A body's node in the resource tree: the body and its shape, by handle.</summary>
    private static hkMemoryResourceContainer BodyContainer(string name, hkpRigidBody body, hkMemoryResourceContainer? prototype)
    {
        hkpShapeInfo? shapePrototype = prototype?.m_resourceHandles
            .Select(h => h.m_variant).OfType<hkpShapeInfo>().FirstOrDefault();
        string? prototypeBody = prototype?.m_resourceHandles
            .Select(h => h.m_variant).OfType<hkpRigidBody>().FirstOrDefault()?.m_name;

        var info = shapePrototype is null
            ? new hkpShapeInfo()
            : Clone(shapePrototype, new Dictionary<object, object>(ReferenceEqualityComparer.Instance));

        info.m_shape = body.m_collidable.m_shape;
        info.m_transform = body.m_motion.m_motionState.m_transform;
        info.m_childShapeNames = info.m_childShapeNames
            .Select(n => string.Equals(n, prototypeBody, StringComparison.OrdinalIgnoreCase) ? name : n).ToList();

        return new hkMemoryResourceContainer
        {
            m_name = name,
            m_children = [],
            m_resourceHandles =
            [
                new hkMemoryResourceHandle { m_name = "hkRigidBody", m_variant = body, m_references = [] },
                new hkMemoryResourceHandle { m_name = "hkpShapeInfo", m_variant = info, m_references = [] },
            ],
        };
    }

    private static IEnumerable<hkMemoryResourceContainer> Descendants(hkMemoryResourceContainer container)
    {
        foreach (hkMemoryResourceContainer child in container.m_children)
        {
            yield return child;

            foreach (hkMemoryResourceContainer grandchild in Descendants(child))
                yield return grandchild;
        }
    }

    // ---------------------------------------------------------------- cloning

    /// <summary>
    /// A deep copy of a Havok object: every <c>m_</c> member cloned, references
    /// shared where <paramref name="memo"/> already maps them.
    /// </summary>
    /// <remarks>
    /// Reflection, because the HKX2 classes are generated and plain -- public
    /// settable properties, the odd public field -- and a hand-written copy for
    /// each of the forty types a rigid body and a joint reach would be forty
    /// places to forget one. The memo keeps a shared reference shared: two members
    /// pointing at one object still do after the copy, and an object seeded into
    /// it is left alone.
    /// </remarks>
    internal static T Clone<T>(T source, Dictionary<object, object> memo) where T : class =>
        (T)CloneObject(source, memo)!;

    private static object? CloneObject(object? value, Dictionary<object, object> memo)
    {
        if (value is null) return null;

        System.Type type = value.GetType();

        if (type.IsValueType || value is string) return value;

        if (memo.TryGetValue(value, out object? seen)) return seen;

        if (value is Array array)
        {
            var copy = Array.CreateInstance(type.GetElementType()!, array.Length);
            memo[value] = copy;

            for (int i = 0; i < array.Length; i++)
                copy.SetValue(CloneObject(array.GetValue(i), memo), i);

            return copy;
        }

        if (value is IList list && type.IsGenericType)
        {
            System.Type element = type.GetGenericArguments()[0];
            var copy = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element))!;
            memo[value] = copy;

            foreach (object? item in list)
                copy.Add(CloneObject(item, memo));

            return copy;
        }

        if (value is not IHavokObject)
            return value;

        object clone = Activator.CreateInstance(type)
            ?? throw new InvalidOperationException($"{type.Name} cannot be instantiated");
        memo[value] = clone;

        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length > 0) continue;

            object? member = property.GetValue(value);
            object? copied = CloneObject(member, memo);

            // A generated IList<T> member holds an array by default and a list once
            // read; either is assignable, but an array clone of an array must stay
            // an array only where the member's type says so.
            if (copied is IList && property.PropertyType.IsGenericType && member is Array)
                copied = ToList(property.PropertyType.GetGenericArguments()[0], (IList)copied);

            property.SetValue(clone, copied);
        }

        foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            field.SetValue(clone, CloneObject(field.GetValue(value), memo));

        return clone;
    }

    private static IList ToList(System.Type element, IList items)
    {
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element))!;

        foreach (object? item in items)
            list.Add(item);

        return list;
    }

    // ---------------------------------------------------------------- packing

    /// <summary>
    /// An hkQsTransform as HKX2 keeps it: translation in the first row, rotation
    /// quaternion in the second, scale in the third.
    /// </summary>
    private static Matrix4x4 Pack(BoneTransform t) => new(
        t.Translation.X, t.Translation.Y, t.Translation.Z, 0f,
        t.Rotation.X, t.Rotation.Y, t.Rotation.Z, t.Rotation.W,
        t.Scale.X, t.Scale.Y, t.Scale.Z, 0f,
        0f, 0f, 0f, 0f);
}
