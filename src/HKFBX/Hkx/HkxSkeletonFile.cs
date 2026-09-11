using System.Numerics;
using HKFBX.Model;
using HKX2;

namespace HKFBX.Hkx;

/// <summary>
/// Reads and writes a skeleton packfile: the rig, the ragdoll, and the physics.
/// </summary>
/// <remarks>
/// A skeleton file is a thousand objects, and almost all of them are scaffolding:
/// motion states, collidables, broad phase handles, constraint atoms, memory
/// resource containers. What a converter can meaningfully carry is a small part of
/// that -- names, hierarchies, poses, shapes, limits -- so writing is done by
/// editing a template rather than by building one, the same way an animation is
/// written here.
/// </remarks>
public static class HkxSkeletonFile
{
    /// <summary>Reads a skeleton packfile.</summary>
    public static SkeletonFile Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var root = (hkRootLevelContainer)Util.ReadHKX(path);
        var objects = Flatten(root);

        hkaAnimationContainer container = objects.OfType<hkaAnimationContainer>().FirstOrDefault()
            ?? throw new InvalidDataException($"'{path}' holds no animation container");

        var skeletons = container.m_skeletons;

        if (skeletons.Count == 0)
            throw new InvalidDataException($"'{path}' holds no hkaSkeleton");

        Skeleton rig = ToSkeleton(skeletons[0]);
        Skeleton? ragdollSkeleton = skeletons.Count > 1 ? ToSkeleton(skeletons[1]) : null;

        var instance = objects.OfType<hkaRagdollInstance>().FirstOrDefault();
        var system = objects.OfType<hkpPhysicsData>().FirstOrDefault()?.m_systems.FirstOrDefault();

        // Which rig bone each ragdoll bone stands for. Not derivable from the
        // names: the human prefixes, the bear renumbers, the chicken shifts by one.
        var mappers = objects.OfType<hkaSkeletonMapper>().ToList();
        var rigToRagdoll = MappingsOf(mappers.FirstOrDefault(m => m.m_mapping?.m_skeletonA == skeletons[0]));
        var ragdollToRig = MappingsOf(mappers.FirstOrDefault(m => m.m_mapping?.m_skeletonA != skeletons[0]));

        var rigBoneOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (BoneMapping mapping in rigToRagdoll)
            rigBoneOf[mapping.ToBone] = mapping.FromBone;

        // The ragdoll's own list first and in its own order, because that order is
        // the ragdoll skeleton's bone order and the two have to agree -- the
        // physics system holds the same bodies in the order the file wrote them,
        // which is not the same. The chicken interleaves its neck and its ankles.
        var ragdollBodies = (instance?.m_rigidBodies ?? []).OfType<hkpRigidBody>().ToList();

        var inRagdoll = ragdollBodies
            .Select(body => body.m_name ?? string.Empty)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        List<RagdollBody> bodies = [];

        foreach (hkpRigidBody body in ragdollBodies)
            bodies.Add(ToBody(body, rigBoneOf, inRagdoll));

        // Then whatever else the physics carries: the character bumper, and on a
        // few creatures a body or two more that the ragdoll does not drive.
        foreach (hkpRigidBody body in (system?.m_rigidBodies ?? []).OfType<hkpRigidBody>())
            if (!inRagdoll.Contains(body.m_name ?? string.Empty))
                bodies.Add(ToBody(body, rigBoneOf, inRagdoll));

        // The same joint is in both lists as two objects with one body pair, and
        // they always agree, so one stands for both.
        List<RagdollJoint> joints = [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (hkpConstraintInstance constraint in
                 (instance?.m_constraints ?? []).OfType<hkpConstraintInstance>()
                     .Concat((system?.m_constraints ?? []).OfType<hkpConstraintInstance>()))
        {
            if (ToJoint(constraint) is not { } joint) continue;
            if (!seen.Add($"{joint.BodyA} {joint.BodyB}")) continue;

            joints.Add(joint);
        }

        return new SkeletonFile
        {
            Rig = rig,
            FloatSlots = skeletons[0].m_floatSlots.ToList(),
            Ragdoll = ragdollSkeleton,
            Bodies = bodies,
            Joints = joints,
            RigToRagdoll = rigToRagdoll,
            RagdollToRig = ragdollToRig,
        };
    }

    private static Skeleton ToSkeleton(hkaSkeleton skeleton)
    {
        var bones = new List<Bone>(skeleton.m_bones.Count);

        for (int i = 0; i < skeleton.m_bones.Count; i++)
        {
            short parent = i < skeleton.m_parentIndices.Count ? skeleton.m_parentIndices[i] : (short)-1;

            BoneTransform pose = i < skeleton.m_referencePose.Count
                ? ToBoneTransform(skeleton.m_referencePose[i])
                : BoneTransform.Identity;

            bones.Add(new Bone(skeleton.m_bones[i].m_name ?? $"bone{i}", parent, pose));
        }

        return new Skeleton { Name = skeleton.m_name ?? "skeleton", Bones = bones };
    }

    private static List<BoneMapping> MappingsOf(hkaSkeletonMapper? mapper)
    {
        if (mapper?.m_mapping is not { } mapping) return [];

        var from = mapping.m_skeletonA;
        var to = mapping.m_skeletonB;

        List<BoneMapping> mappings = [];

        foreach (hkaSkeletonMapperDataSimpleMapping simple in mapping.m_simpleMappings)
        {
            if (from is null || to is null) continue;
            if (simple.m_boneA < 0 || simple.m_boneA >= from.m_bones.Count) continue;
            if (simple.m_boneB < 0 || simple.m_boneB >= to.m_bones.Count) continue;

            mappings.Add(new BoneMapping(
                from.m_bones[simple.m_boneA].m_name ?? string.Empty,
                to.m_bones[simple.m_boneB].m_name ?? string.Empty,
                ToBoneTransform(simple.m_aFromBTransform)));
        }

        return mappings;
    }

    private static RagdollBody ToBody(
        hkpRigidBody body, Dictionary<string, string> rigBoneOf, HashSet<string> inRagdoll)
    {
        string name = body.m_name ?? string.Empty;
        var motion = body.m_motion;
        var state = motion?.m_motionState;

        CapsuleShape? shape = body.m_collidable?.m_shape is hkpCapsuleShape capsule
            ? new CapsuleShape(ToVector3(capsule.m_vertexA), ToVector3(capsule.m_vertexB), capsule.m_radius)
            : null;

        return new RagdollBody
        {
            Name = name,
            RigBone = rigBoneOf.GetValueOrDefault(name),
            Transform = state is null
                ? BoneTransform.Identity
                : new BoneTransform(
                    state.m_transform.Translation,
                    Quaternion.CreateFromRotationMatrix(state.m_transform),
                    Vector3.One),
            Shape = shape,
            MotionType = (byte)(motion?.m_type ?? 0),
            QualityType = (byte)(body.m_collidable?.m_broadPhaseHandle?.m_objectQualityType ?? 0),
            CollisionFilterInfo = body.m_collidable?.m_broadPhaseHandle?.m_collisionFilterInfo ?? 0,
            InverseMass = motion?.m_inertiaAndMassInv.W ?? 0,
            InverseInertia = motion is null ? Vector3.Zero : ToVector3(motion.m_inertiaAndMassInv),
            Friction = body.m_material?.m_friction ?? 0,
            Restitution = body.m_material?.m_restitution ?? 0,
            // HKX2 keeps the dampings as Half, which has two implicit conversions
            // from an integer literal and so cannot take a plain zero fallback.
            LinearDamping = state is null ? 0f : (float)state.m_linearDamping,
            AngularDamping = state is null ? 0f : (float)state.m_angularDamping,
            InRagdoll = inRagdoll.Contains(name),
        };
    }

    private static RagdollJoint? ToJoint(hkpConstraintInstance constraint)
    {
        var ends = constraint.m_entities.OfType<hkpRigidBody>().ToList();

        if (ends.Count < 2) return null;

        var joint = new RagdollJoint
        {
            Name = constraint.m_name ?? string.Empty,
            BodyA = ends[0].m_name ?? string.Empty,
            BodyB = ends[1].m_name ?? string.Empty,
        };

        switch (constraint.m_data)
        {
            case hkpRagdollConstraintData ragdoll when ragdoll.m_atoms is { } atoms:
                return joint with
                {
                    Kind = JointKind.Ragdoll,
                    FrameA = atoms.m_transforms?.m_transformA ?? Matrix4x4.Identity,
                    FrameB = atoms.m_transforms?.m_transformB ?? Matrix4x4.Identity,
                    TwistMinAngle = atoms.m_twistLimit?.m_minAngle ?? 0,
                    TwistMaxAngle = atoms.m_twistLimit?.m_maxAngle ?? 0,
                    ConeMaxAngle = atoms.m_coneLimit?.m_maxAngle ?? 0,
                    PlaneMinAngle = atoms.m_planesLimit?.m_minAngle ?? 0,
                    PlaneMaxAngle = atoms.m_planesLimit?.m_maxAngle ?? 0,
                    MaxFrictionTorque = atoms.m_angFriction?.m_maxFrictionTorque ?? 0,
                };

            case hkpLimitedHingeConstraintData hinge when hinge.m_atoms is { } atoms:
                return joint with
                {
                    Kind = JointKind.LimitedHinge,
                    FrameA = atoms.m_transforms?.m_transformA ?? Matrix4x4.Identity,
                    FrameB = atoms.m_transforms?.m_transformB ?? Matrix4x4.Identity,
                    HingeMinAngle = atoms.m_angLimit?.m_minAngle ?? 0,
                    HingeMaxAngle = atoms.m_angLimit?.m_maxAngle ?? 0,
                    MaxFrictionTorque = atoms.m_angFriction?.m_maxFrictionTorque ?? 0,
                };

            default:
                return joint;
        }
    }

    /// <summary>
    /// Writes a skeleton packfile, editing the original rather than building one.
    /// </summary>
    /// <remarks>
    /// A skeleton file is a thousand objects and almost all of them are
    /// scaffolding -- motion states, collidables, broad phase handles, constraint
    /// atoms, the memory resource tree. Rebuilding that from nothing would mean
    /// inventing values the original already has right, so the template supplies
    /// everything and this replaces what a converter can actually carry: the rest
    /// pose, the body transforms and shapes, and the joint frames and limits.
    ///
    /// Everything is matched by name. A bone the template does not have is not
    /// created, and a body it does not have is not either -- the bone count and
    /// the body count are structure, and changing them means rewriting the mapper,
    /// the ragdoll instance and every index that points into them. What comes back
    /// with a changed count is reported rather than half-applied.
    /// </remarks>
    /// <returns>The names in the file that the template had no place for.</returns>
    public static IReadOnlyList<string> Write(string templatePath, SkeletonFile file, string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templatePath);
        ArgumentNullException.ThrowIfNull(file);

        var root = (hkRootLevelContainer)Util.ReadHKX(templatePath);
        var objects = Flatten(root);

        hkaAnimationContainer container = objects.OfType<hkaAnimationContainer>().FirstOrDefault()
            ?? throw new InvalidDataException($"'{templatePath}' holds no animation container");

        List<string> unmatched = [];

        if (container.m_skeletons.Count > 0)
            WriteSkeleton(container.m_skeletons[0], file.Rig, unmatched);

        var system = objects.OfType<hkpPhysicsData>().FirstOrDefault()?.m_systems.FirstOrDefault();

        var bodyOf = file.Bodies.ToDictionary(body => body.Name, StringComparer.OrdinalIgnoreCase);

        foreach (hkpRigidBody body in (system?.m_rigidBodies ?? []).OfType<hkpRigidBody>())
        {
            if (!bodyOf.TryGetValue(body.m_name ?? string.Empty, out RagdollBody? source))
            {
                unmatched.Add($"body {body.m_name}");
                continue;
            }

            WriteBody(body, source);
        }

        // The same joint is in the ragdoll instance and in the physics system as
        // two objects, so both copies take the values.
        var instance = objects.OfType<hkaRagdollInstance>().FirstOrDefault();
        var jointOf = new Dictionary<string, RagdollJoint>(StringComparer.OrdinalIgnoreCase);

        foreach (RagdollJoint joint in file.Joints)
            jointOf[$"{joint.BodyA} {joint.BodyB}"] = joint;

        foreach (hkpConstraintInstance constraint in
                 (instance?.m_constraints ?? []).OfType<hkpConstraintInstance>()
                     .Concat((system?.m_constraints ?? []).OfType<hkpConstraintInstance>()))
        {
            var ends = constraint.m_entities.OfType<hkpRigidBody>().ToList();

            if (ends.Count < 2) continue;

            if (!jointOf.TryGetValue($"{ends[0].m_name} {ends[1].m_name}", out RagdollJoint? joint))
            {
                unmatched.Add($"joint {ends[0].m_name} -> {ends[1].m_name}");
                continue;
            }

            WriteJoint(constraint, joint);
        }

        using (FileStream stream = File.Create(outputPath))
            Util.WriteHKX(root, HKXHeader.SkyrimSE(), stream);

        return unmatched;
    }

    private static void WriteSkeleton(hkaSkeleton skeleton, Skeleton source, List<string> unmatched)
    {
        var poseOf = new Dictionary<string, BoneTransform>(StringComparer.OrdinalIgnoreCase);

        foreach (Bone bone in source.Bones)
            poseOf[bone.Name] = bone.ReferencePose;

        for (int i = 0; i < skeleton.m_bones.Count && i < skeleton.m_referencePose.Count; i++)
        {
            string name = skeleton.m_bones[i].m_name ?? string.Empty;

            if (!poseOf.TryGetValue(name, out BoneTransform pose))
            {
                unmatched.Add($"bone {name}");
                continue;
            }

            skeleton.m_referencePose[i] = FromBoneTransform(pose);
        }
    }

    private static void WriteBody(hkpRigidBody body, RagdollBody source)
    {
        if (body.m_collidable?.m_shape is hkpCapsuleShape capsule && source.Shape is { } shape)
        {
            capsule.m_vertexA = new Vector4(shape.VertexA, shape.Radius);
            capsule.m_vertexB = new Vector4(shape.VertexB, shape.Radius);
            capsule.m_radius = shape.Radius;
        }

        if (body.m_material is { } material)
        {
            material.m_friction = source.Friction;
            material.m_restitution = source.Restitution;
        }

        if (body.m_collidable?.m_broadPhaseHandle is { } handle)
            handle.m_collisionFilterInfo = source.CollisionFilterInfo;

        if (body.m_motion?.m_motionState is { } state)
        {
            state.m_transform = Matrix4x4.CreateFromQuaternion(source.Transform.Rotation)
                * Matrix4x4.CreateTranslation(source.Transform.Translation);

            state.m_linearDamping = (Half)source.LinearDamping;
            state.m_angularDamping = (Half)source.AngularDamping;
        }
    }

    private static void WriteJoint(hkpConstraintInstance constraint, RagdollJoint source)
    {
        switch (constraint.m_data)
        {
            case hkpRagdollConstraintData ragdoll when ragdoll.m_atoms is { } atoms:
                if (atoms.m_transforms is { } ragdollFrames)
                {
                    ragdollFrames.m_transformA = source.FrameA;
                    ragdollFrames.m_transformB = source.FrameB;
                }

                if (atoms.m_twistLimit is { } twist)
                {
                    twist.m_minAngle = source.TwistMinAngle;
                    twist.m_maxAngle = source.TwistMaxAngle;
                }

                if (atoms.m_coneLimit is { } cone) cone.m_maxAngle = source.ConeMaxAngle;

                if (atoms.m_planesLimit is { } planes)
                {
                    planes.m_minAngle = source.PlaneMinAngle;
                    planes.m_maxAngle = source.PlaneMaxAngle;
                }

                if (atoms.m_angFriction is { } friction)
                    friction.m_maxFrictionTorque = source.MaxFrictionTorque;

                break;

            case hkpLimitedHingeConstraintData hinge when hinge.m_atoms is { } atoms:
                if (atoms.m_transforms is { } hingeFrames)
                {
                    hingeFrames.m_transformA = source.FrameA;
                    hingeFrames.m_transformB = source.FrameB;
                }

                if (atoms.m_angLimit is { } limit)
                {
                    limit.m_minAngle = source.HingeMinAngle;
                    limit.m_maxAngle = source.HingeMaxAngle;
                }

                if (atoms.m_angFriction is { } hingeFriction)
                    hingeFriction.m_maxFrictionTorque = source.MaxFrictionTorque;

                break;
        }
    }

    /// <summary>The inverse of <see cref="ToBoneTransform"/>.</summary>
    private static Matrix4x4 FromBoneTransform(BoneTransform t) =>
        new(t.Translation.X, t.Translation.Y, t.Translation.Z, 0,
            t.Rotation.X, t.Rotation.Y, t.Rotation.Z, t.Rotation.W,
            t.Scale.X, t.Scale.Y, t.Scale.Z, 0,
            0, 0, 0, 0);

    private static IReadOnlyList<IHavokObject> Flatten(hkRootLevelContainer root)
    {
        List<IHavokObject> objects = [];

        foreach (hkRootLevelContainerNamedVariant variant in root.m_namedVariants)
            if (variant.m_variant is { } value)
                objects.Add(value);

        return objects;
    }

    /// <summary>
    /// HKX2 keeps an hkQsTransform in a Matrix4x4: translation in the first row,
    /// rotation quaternion in the second, scale in the third.
    /// </summary>
    private static BoneTransform ToBoneTransform(Matrix4x4 m) =>
        new(new Vector3(m.M11, m.M12, m.M13),
            new Quaternion(m.M21, m.M22, m.M23, m.M24),
            new Vector3(m.M31, m.M32, m.M33));

    private static Vector3 ToVector3(Vector4 v) => new(v.X, v.Y, v.Z);
}
