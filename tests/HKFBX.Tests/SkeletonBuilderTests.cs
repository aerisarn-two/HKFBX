using System.Numerics;
using HKFBX.Hkx;
using HKFBX.Model;
using HKX2;

namespace HKFBX.Tests;

/// <summary>
/// A skeleton rebuilt from prototypes reads back as the skeleton it was built from.
/// </summary>
public sealed class SkeletonBuilderTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "hkfbx-builder-" + Guid.NewGuid().ToString("N"));

    public SkeletonBuilderTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        if (Directory.Exists(_temp)) Directory.Delete(_temp, recursive: true);
    }

    /// <summary>
    /// Every creature's own skeleton, rebuilt over itself, comes back the same: the
    /// same bones in the same order with the same poses, the same bodies with the
    /// same shapes and the same joints with the same limits -- and the same
    /// objects, counted by type, but for the resource tree, which the exporter
    /// pruned and this writes whole.
    /// </summary>
    [CorpusFact]
    public void ACreatureRebuiltOverItselfReadsBackTheSame()
    {
        int rebuilt = 0;

        foreach (string path in Directory.EnumerateFiles(Corpus.Root!, "skeleton*.hkx", SearchOption.AllDirectories).Order())
        {
            SkeletonFile original = HkxSkeletonFile.Read(path);
            if (!original.HasRagdoll) continue;

            string output = Path.Combine(_temp, $"{rebuilt}.hkx");
            HkxSkeletonBuilder.Write(path, original, output);
            SkeletonFile again = HkxSkeletonFile.Read(output);

            AssertSameSkeleton(original.Rig, again.Rig, path);
            Assert.Equal(original.FloatSlots, again.FloatSlots);
            Assert.Equal(original.Bodies.Count, again.Bodies.Count);

            foreach ((RagdollBody was, RagdollBody now) in original.Bodies.Zip(again.Bodies))
            {
                Assert.Equal(was.Name, now.Name);
                Assert.Equal(was.RigBone, now.RigBone);
                Assert.Equal(was.InRagdoll, now.InRagdoll);
                Assert.Equal(was.MotionType, now.MotionType);
                Assert.Equal(was.QualityType, now.QualityType);
                Assert.Equal(was.CollisionFilterInfo, now.CollisionFilterInfo);
                Assert.Equal(was.InverseMass, now.InverseMass, 1e-5f);
                Assert.Equal(was.Friction, now.Friction);
                Assert.Equal(was.Restitution, now.Restitution);
                Assert.Equal(was.AngularDamping, now.AngularDamping, 1e-3f);
                AssertClose(was.Transform.Translation, now.Transform.Translation, 1e-3f, $"{path}: {was.Name}");
                Assert.True(MathF.Abs(Quaternion.Dot(was.Transform.Rotation, now.Transform.Rotation)) > 1f - 1e-4f, $"{path}: {was.Name} rotation");
                Assert.Equal(was.Shape is null, now.Shape is null);

                if (was.Shape is { } shape && now.Shape is { } rebuiltShape)
                {
                    AssertClose(shape.VertexA, rebuiltShape.VertexA, 1e-4f, $"{path}: {was.Name} capsule A");
                    AssertClose(shape.VertexB, rebuiltShape.VertexB, 1e-4f, $"{path}: {was.Name} capsule B");
                    Assert.Equal(shape.Radius, rebuiltShape.Radius, 1e-5f);
                }
            }

            Assert.Equal(original.Joints.Count, again.Joints.Count);

            foreach ((RagdollJoint was, RagdollJoint now) in original.Joints.Zip(again.Joints))
            {
                Assert.Equal(was.BodyA, now.BodyA);
                Assert.Equal(was.BodyB, now.BodyB);
                Assert.Equal(was.Kind, now.Kind);
                Assert.Equal(was.FrameA, now.FrameA);
                Assert.Equal(was.FrameB, now.FrameB);
                Assert.Equal(was.ConeMaxAngle, now.ConeMaxAngle);
                Assert.Equal(was.PlaneMinAngle, now.PlaneMinAngle);
                Assert.Equal(was.PlaneMaxAngle, now.PlaneMaxAngle);
                Assert.Equal(was.TwistMinAngle, now.TwistMinAngle);
                Assert.Equal(was.TwistMaxAngle, now.TwistMaxAngle);
                Assert.Equal(was.HingeMinAngle, now.HingeMinAngle);
                Assert.Equal(was.HingeMaxAngle, now.HingeMaxAngle);
                Assert.Equal(was.MaxFrictionTorque, now.MaxFrictionTorque);
            }

            Assert.Equal(original.RigToRagdoll.Count, again.RigToRagdoll.Count);
            Assert.Equal(original.RagdollToRig.Count, again.RagdollToRig.Count);

            foreach ((BoneMapping was, BoneMapping now) in original.RigToRagdoll.Zip(again.RigToRagdoll))
            {
                Assert.Equal(was.FromBone, now.FromBone);
                Assert.Equal(was.ToBone, now.ToBone);
                Assert.Equal(was.Transform, now.Transform);
            }

            // The ragdoll skeleton's poses are taken from the bodies, which is
            // where the exporter took them from too.
            Assert.NotNull(again.Ragdoll);
            Assert.Equal(original.Ragdoll!.Bones.Select(b => b.Name), again.Ragdoll!.Bones.Select(b => b.Name));
            Assert.Equal(original.Ragdoll.Bones.Select(b => b.ParentIndex), again.Ragdoll.Bones.Select(b => b.ParentIndex));

            foreach ((Bone was, Bone now) in original.Ragdoll.Bones.Zip(again.Ragdoll.Bones))
            {
                AssertClose(was.ReferencePose.Translation, now.ReferencePose.Translation, 1e-2f, $"{path}: ragdoll {was.Name}");
                Assert.True(MathF.Abs(Quaternion.Dot(was.ReferencePose.Rotation, now.ReferencePose.Rotation)) > 1f - 1e-3f, $"{path}: ragdoll {was.Name} rotation");
            }

            AssertSameObjectCensus(path, output);
            rebuilt++;
        }

        Assert.True(rebuilt >= 40, $"only {rebuilt} skeletons carried a ragdoll");
    }

    /// <summary>
    /// A rig the template never had: more bones, fewer bodies, a joint of each kind.
    /// The file the builder writes holds exactly that, indexed consistently.
    /// </summary>
    [CorpusFact]
    public void ANewRigIsWrittenOverAnOldTemplate()
    {
        // The sabre cat has a joint of each kind to clone.
        string template = Corpus.Path_("actors", "sabrecat", "character assets", "skeleton.hkx");

        var rig = new Skeleton
        {
            Name = "NPC Root [Root]",
            Bones =
            [
                new Bone("NPC Root [Root]", -1, BoneTransform.Identity),
                new Bone("Pelvis", 0, new BoneTransform(new Vector3(0, 0, 40), Quaternion.Identity, Vector3.One)),
                new Bone("Spine", 1, new BoneTransform(new Vector3(0, 10, 0), Quaternion.Identity, Vector3.One)),
                new Bone("Thigh", 1, new BoneTransform(new Vector3(5, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.5f), Vector3.One)),
                new Bone("Calf", 3, new BoneTransform(new Vector3(0, 0, -20), Quaternion.Identity, Vector3.One)),
                new Bone("Tail", 1, new BoneTransform(new Vector3(0, -10, 0), Quaternion.Identity, Vector3.One)),
                new Bone("TailTip", 5, new BoneTransform(new Vector3(0, -10, 0), Quaternion.Identity, Vector3.One)),
            ],
        };

        RagdollBody Body(string name, string bone, Vector3 at, byte motion = 3) => new()
        {
            Name = name, RigBone = bone, MotionType = motion, QualityType = 4, CollisionFilterInfo = 0x104c0,
            InverseMass = 0.5f, InverseInertia = new Vector3(0.01f, 0.02f, 0.03f), Friction = 0.3f, Restitution = 0.8f,
            AngularDamping = 1.324f, Transform = new BoneTransform(at, Quaternion.Identity, Vector3.One),
            Shape = new CapsuleShape(new Vector3(0, 0, 2), new Vector3(0, 0, 18), 5f),
        };

        var file = new SkeletonFile
        {
            Rig = rig,
            Ragdoll = new Skeleton
            {
                Name = "Ragdoll_Pelvis",
                Bones =
                [
                    new Bone("Ragdoll_Pelvis", -1, BoneTransform.Identity),
                    new Bone("Ragdoll_Thigh", 0, BoneTransform.Identity),
                    new Bone("Ragdoll_Calf", 1, BoneTransform.Identity),
                ],
            },
            Bodies =
            [
                Body("Ragdoll_Pelvis", "Pelvis", new Vector3(0, 0, 40), 2),
                Body("Ragdoll_Thigh", "Thigh", new Vector3(5, 0, 40)),
                Body("Ragdoll_Calf", "Calf", new Vector3(5, 0, 20)),
            ],
            Joints =
            [
                new RagdollJoint
                {
                    Name = "Ragdoll_Thigh", BodyA = "Ragdoll_Thigh", BodyB = "Ragdoll_Pelvis", Kind = JointKind.Ragdoll,
                    FrameA = Matrix4x4.Identity, FrameB = Matrix4x4.CreateTranslation(5, 0, 0),
                    ConeMaxAngle = 0.7f, PlaneMinAngle = -0.5f, PlaneMaxAngle = 0.5f, TwistMinAngle = -0.2f, TwistMaxAngle = 0.2f,
                },
                new RagdollJoint
                {
                    Name = "Ragdoll_Calf", BodyA = "Ragdoll_Calf", BodyB = "Ragdoll_Thigh", Kind = JointKind.LimitedHinge,
                    FrameA = Matrix4x4.Identity, FrameB = Matrix4x4.CreateTranslation(0, 0, -20),
                    HingeMinAngle = -1.4f, HingeMaxAngle = 0.1f,
                },
            ],
            RigToRagdoll =
            [
                new BoneMapping("Pelvis", "Ragdoll_Pelvis", BoneTransform.Identity),
                new BoneMapping("Thigh", "Ragdoll_Thigh", BoneTransform.Identity),
                new BoneMapping("Calf", "Ragdoll_Calf", BoneTransform.Identity),
            ],
            RagdollToRig =
            [
                new BoneMapping("Ragdoll_Pelvis", "Pelvis", BoneTransform.Identity),
                new BoneMapping("Ragdoll_Thigh", "Thigh", BoneTransform.Identity),
                new BoneMapping("Ragdoll_Calf", "Calf", BoneTransform.Identity),
            ],
        };

        string output = Path.Combine(_temp, "new.hkx");
        HkxSkeletonBuilder.Write(template, file, output);
        SkeletonFile again = HkxSkeletonFile.Read(output);

        Assert.Equal(7, again.Rig.Count);
        Assert.Equal(rig.Bones.Select(b => b.Name), again.Rig.Bones.Select(b => b.Name));
        Assert.Equal(3, again.Bodies.Count);
        Assert.Equal(2, again.Joints.Count);
        Assert.Equal(JointKind.Ragdoll, again.Joints[0].Kind);
        Assert.Equal(JointKind.LimitedHinge, again.Joints[1].Kind);
        Assert.Equal(-1.4f, again.Joints[1].HingeMinAngle);
        Assert.Equal(3, again.RigToRagdoll.Count);
        Assert.Equal("Thigh", again.Bodies[1].RigBone);

        // The ragdoll's local poses follow the bodies: the calf sits 20 below the thigh.
        Assert.Equal(new Vector3(0, 0, -20), again.Ragdoll!.Bones[2].ReferencePose.Translation);

        // The indices the file carries all fit the rig and the ragdoll it holds.
        var root = (hkRootLevelContainer)Util.ReadHKX(output);
        var variants = root.m_namedVariants.Select(v => v?.m_variant).ToList();
        hkaRagdollInstance instance = Assert.Single(variants.OfType<hkaRagdollInstance>());
        Assert.Equal([0, 1, 2], instance.m_boneToRigidBodyMap);
        Assert.Equal(3, instance.m_rigidBodies.Count);
        Assert.Equal(2, instance.m_constraints.Count);

        foreach (hkaSkeletonMapper mapper in variants.OfType<hkaSkeletonMapper>())
        {
            Assert.All(mapper.m_mapping.m_simpleMappings, m => Assert.InRange(m.m_boneA, 0, mapper.m_mapping.m_skeletonA!.m_bones.Count - 1));
            Assert.All(mapper.m_mapping.m_simpleMappings, m => Assert.InRange(m.m_boneB, 0, mapper.m_mapping.m_skeletonB!.m_bones.Count - 1));
            Assert.All(mapper.m_mapping.m_unmappedBones, b => Assert.InRange(b, 0, mapper.m_mapping.m_skeletonB!.m_bones.Count - 1));
        }

        hkaSkeletonMapper toRig = variants.OfType<hkaSkeletonMapper>().Single(m => m.m_mapping.m_skeletonB!.m_bones.Count == 7);
        Assert.Equal([0, 2, 5, 6], toRig.m_mapping.m_unmappedBones);

        // The resource tree names every rig bone and every body.
        hkMemoryResourceContainer resources = Assert.Single(variants.OfType<hkMemoryResourceContainer>());
        Assert.Equal(2, resources.m_children.Count);
        Assert.Equal("NPC Root [Root]", resources.m_children[0].m_name);
        Assert.Equal("Ragdoll_Pelvis", resources.m_children[1].m_name);
        Assert.Equal(2, resources.m_children[1].m_resourceHandles.Count);
    }

    private static void AssertSameSkeleton(Skeleton was, Skeleton now, string path)
    {
        Assert.Equal(was.Name, now.Name);
        Assert.Equal(was.Bones.Select(b => b.Name), now.Bones.Select(b => b.Name));
        Assert.Equal(was.Bones.Select(b => b.ParentIndex), now.Bones.Select(b => b.ParentIndex));

        foreach ((Bone a, Bone b) in was.Bones.Zip(now.Bones))
        {
            Assert.Equal(a.ReferencePose.Translation, b.ReferencePose.Translation);
            Assert.Equal(a.ReferencePose.Rotation, b.ReferencePose.Rotation);
            Assert.Equal(a.ReferencePose.Scale, b.ReferencePose.Scale);
        }
    }

    private static void AssertClose(Vector3 expected, Vector3 actual, float tolerance, string what) =>
        Assert.True(Vector3.Distance(expected, actual) <= tolerance, $"{what}: expected {expected}, got {actual}");

    /// <summary>
    /// The same objects, counted by type: a rebuilt file reaches the same
    /// structure the exporter wrote, except in the resource tree, where the
    /// exporter listed only the bones it had scene nodes for.
    /// </summary>
    private static void AssertSameObjectCensus(string original, string rebuilt)
    {
        static Dictionary<string, int> Census(string path)
        {
            var seen = new HashSet<IHavokObject>(ReferenceEqualityComparer.Instance);
            var counts = new Dictionary<string, int>();

            void Visit(object? value)
            {
                switch (value)
                {
                    case null or string:
                        return;
                    case IHavokObject obj when seen.Add(obj):
                        counts[obj.GetType().Name] = counts.GetValueOrDefault(obj.GetType().Name) + 1;
                        foreach (var property in obj.GetType().GetProperties())
                            if (property.GetIndexParameters().Length == 0 && !property.PropertyType.IsValueType)
                                Visit(property.GetValue(obj));
                        foreach (var field in obj.GetType().GetFields())
                            Visit(field.GetValue(obj));
                        return;
                    case System.Collections.IEnumerable list:
                        foreach (object? item in list) Visit(item);
                        return;
                }
            }

            Visit(Util.ReadHKX(path));
            return counts;
        }

        Dictionary<string, int> was = Census(original);
        Dictionary<string, int> now = Census(rebuilt);

        foreach ((string type, int count) in was)
        {
            if (type is nameof(hkMemoryResourceContainer)) continue;
            Assert.True(now.GetValueOrDefault(type) == count, $"{original}: {type} was {count}, rebuilt {now.GetValueOrDefault(type)}");
        }

        foreach (string type in now.Keys)
            Assert.True(was.ContainsKey(type), $"{original}: rebuilt holds {type}, which the original did not");
    }
}
