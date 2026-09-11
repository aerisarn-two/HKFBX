using System.Numerics;
using HKFBX.Fbx;
using HKFBX.Model;
using LeanMeshIO;

namespace HKFBX.Tests;

/// <summary>
/// A rig and its ragdoll, out to FBX and back.
/// </summary>
public class RigRoundTripTests
{
    private static FbxDocument SaveAndLoad(FbxDocument document)
    {
        using var stream = new MemoryStream();
        document.Save(stream);
        stream.Position = 0;
        return FbxDocument.Load(stream);
    }

    /// <summary>
    /// Two rig bones, two bodies hanging off them, and the joint between.
    /// </summary>
    private static SkeletonFile Rig()
    {
        var rig = new Skeleton
        {
            Name = "NPC Root [Root]",
            Bones =
            [
                new Bone("NPC Root [Root]", -1, BoneTransform.Identity),
                new Bone("NPC Thigh", 0, new BoneTransform(new Vector3(0, 0, 60), Quaternion.Identity, Vector3.One)),
                new Bone("NPC Calf", 1, new BoneTransform(new Vector3(0, 0, 30), Quaternion.Identity, Vector3.One)),
            ],
        };

        return new SkeletonFile
        {
            Rig = rig,
            FloatSlots = ["hkVis:Weapon"],
            Bodies =
            [
                new RagdollBody
                {
                    Name = "Ragdoll_Thigh",
                    RigBone = "NPC Thigh",
                    Transform = new BoneTransform(new Vector3(1, 2, 3), Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.4f), Vector3.One),
                    Shape = new CapsuleShape(new Vector3(0, 0, 4), new Vector3(0, 0, 26), 6.5f),
                    MotionType = 3,
                    QualityType = 4,
                    CollisionFilterInfo = 66752,
                    InverseMass = 0.1667f,
                    InverseInertia = new Vector3(0.0017f, 0.0017f, 0.0075f),
                    Friction = 0.3f,
                    Restitution = 0.8f,
                    AngularDamping = 1.324f,
                },
                new RagdollBody
                {
                    Name = "Ragdoll_Calf",
                    RigBone = "NPC Calf",
                    Shape = new CapsuleShape(new Vector3(0, 0, 2), new Vector3(0, 0, 20), 5f),
                    MotionType = 3,
                    InverseMass = 0.25f,
                },
                new RagdollBody { Name = "CharacterBumper", MotionType = 5, InRagdoll = false },
            ],
            Joints =
            [
                new RagdollJoint
                {
                    Name = "Ragdoll_Calf",
                    BodyA = "Ragdoll_Calf",
                    BodyB = "Ragdoll_Thigh",
                    Kind = JointKind.LimitedHinge,
                    FrameA = Matrix4x4.CreateTranslation(0, 0, 1),
                    FrameB = Matrix4x4.CreateFromAxisAngle(Vector3.UnitY, 0.3f) * Matrix4x4.CreateTranslation(0, 0, 22),
                    HingeMinAngle = -1.92f,
                    HingeMaxAngle = 0f,
                    MaxFrictionTorque = 0.25f,
                },
            ],
        };
    }

    private static SkeletonFile Reload(SkeletonFile file) =>
        FbxSkeletonReader.Read(SaveAndLoad(FbxSkeletonWriter.Build(file)), file.FloatSlots);

    [Fact]
    public void TheRigComesBackWithItsBonesAndHierarchy()
    {
        SkeletonFile back = Reload(Rig());

        Assert.Equal(3, back.Rig.Count);
        Assert.Equal(["NPC Root [Root]", "NPC Thigh", "NPC Calf"], back.Rig.Bones.Select(bone => bone.Name));
        Assert.Equal(-1, back.Rig.Bones[0].ParentIndex);
        Assert.Equal(0, back.Rig.Bones[1].ParentIndex);
        Assert.Equal(60f, back.Rig.Bones[1].ReferencePose.Translation.Z, 3);
    }

    /// <summary>
    /// A body is a node under the rig bone it drives, which is where the same
    /// ragdoll sits in the creature's NIF.
    /// </summary>
    [Fact]
    public void EachBodyComesBackOnItsBone()
    {
        SkeletonFile back = Reload(Rig());

        RagdollBody thigh = back.Bodies.Single(body => body.Name == "Ragdoll_Thigh");

        Assert.Equal("NPC Thigh", thigh.RigBone);
        Assert.Equal(new Vector3(1, 2, 3), thigh.Transform.Translation, VectorComparer);
        Assert.Equal(3, thigh.MotionType);
        Assert.Equal(4, thigh.QualityType);
        Assert.Equal(66752u, thigh.CollisionFilterInfo);
        Assert.Equal(0.1667f, thigh.InverseMass, 4);
        Assert.Equal(0.3f, thigh.Friction, 4);
        Assert.Equal(0.8f, thigh.Restitution, 4);
        Assert.Equal(1.324f, thigh.AngularDamping, 3);
    }

    [Fact]
    public void TheCapsuleComesBackExactly()
    {
        CapsuleShape shape = Reload(Rig()).Bodies.Single(body => body.Name == "Ragdoll_Thigh").Shape!;

        Assert.Equal(new Vector3(0, 0, 4), shape.VertexA, VectorComparer);
        Assert.Equal(new Vector3(0, 0, 26), shape.VertexB, VectorComparer);
        Assert.Equal(6.5f, shape.Radius, 4);
    }

    /// <summary>
    /// The bumper is in the physics and not in the ragdoll, and a converter that
    /// promoted it would give the ragdoll a bone nothing animates.
    /// </summary>
    [Fact]
    public void TheBumperStaysOutOfTheRagdoll()
    {
        SkeletonFile back = Reload(Rig());

        Assert.False(back.Bodies.Single(body => body.Name == "CharacterBumper").InRagdoll);
        Assert.DoesNotContain(back.Ragdoll!.Bones, bone => bone.Name == "CharacterBumper");
    }

    [Fact]
    public void TheJointComesBackWithItsLimitsAndBothFrames()
    {
        RagdollJoint joint = Assert.Single(Reload(Rig()).Joints);

        Assert.Equal("Ragdoll_Calf", joint.BodyA);
        Assert.Equal("Ragdoll_Thigh", joint.BodyB);
        Assert.Equal(JointKind.LimitedHinge, joint.Kind);
        Assert.Equal(-1.92f, joint.HingeMinAngle, 4);
        Assert.Equal(0.25f, joint.MaxFrictionTorque, 4);

        // Both sides. ck-cmd computes the far frame and writes only the near one,
        // so a joint exported through it comes back with half of itself.
        Assert.Equal(1f, joint.FrameA.Translation.Z, 3);
        Assert.Equal(22f, joint.FrameB.Translation.Z, 3);
    }

    /// <summary>
    /// The two bodies are named outright rather than left to be parsed out of the
    /// node's name. Blender caps a name at 63 characters and rewrites the
    /// overflow as a hash, which eats the suffix off two thirds of a human
    /// skeleton's joints.
    /// </summary>
    [Fact]
    public void TheJointNamesItsBodiesInProperties()
    {
        FbxDocument document = SaveAndLoad(FbxSkeletonWriter.Build(Rig()));
        var scene = new FbxScene(document);

        FbxObject joint = scene.OfClass("Model")
            .Single(model => model.Properties.ValuesOf(FbxSkeletonWriter.TypeProperty).Count > 0
                          && model.Properties.ValuesOf(FbxSkeletonWriter.FrameProperty).FirstOrDefault() as string != "A");

        Assert.Equal("Ragdoll_Calf", joint.Properties.ValuesOf("constraint_body_a").FirstOrDefault());
        Assert.Equal("Ragdoll_Thigh", joint.Properties.ValuesOf("constraint_body_b").FirstOrDefault());
    }

    /// <summary>
    /// The ragdoll skeleton is not written, because it does not have to be: a bone
    /// per body, and the joints are the hierarchy. That holds in every skeleton
    /// the game ships.
    /// </summary>
    [Fact]
    public void TheRagdollSkeletonIsRebuiltFromTheBodies()
    {
        Skeleton ragdoll = Reload(Rig()).Ragdoll!;

        Assert.Equal(["Ragdoll_Thigh", "Ragdoll_Calf"], ragdoll.Bones.Select(bone => bone.Name));
        Assert.Equal(-1, ragdoll.Bones[0].ParentIndex);
        Assert.Equal(0, ragdoll.Bones[1].ParentIndex);
    }

    /// <summary>
    /// Straight up, the first and last axes of an XYZ decomposition turn about the
    /// same line, and the general formulas lose the rotation between them. 173 of
    /// the 4,132 bone rest poses in Skyrim's skeletons sit exactly there.
    /// </summary>
    [Theory]
    [InlineData(0.7f)]
    [InlineData(-1.2f)]
    [InlineData(2.5f)]
    public void ARotationAtTheGimbalPoleSurvives(float roll)
    {
        Quaternion pole = Quaternion.Concatenate(
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, roll),
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2));

        Vector3 euler = new BoneTransform(Vector3.Zero, pole, Vector3.One).ToEulerXyz();
        Quaternion back = BoneTransform.FromEulerXyz(Vector3.Zero, euler, Vector3.One).Rotation;

        // A fiftieth of a degree is the floor, not the target: a float quaternion
        // lands a ten-millionth off the pole, and there the decomposition's own
        // conditioning costs a few thousandths of a degree whichever way it is
        // taken. Before the pole was handled at all, this same case came back
        // sixty-nine degrees out.
        Assert.True(Drift(pole, back) < 0.02, $"{Drift(pole, back):F4} degrees off");
    }

    /// <summary>The angle between two rotations, measured where acos is not usable.</summary>
    private static double Drift(Quaternion a, Quaternion b)
    {
        a = Quaternion.Normalize(a);
        b = Quaternion.Normalize(b);

        if (Quaternion.Dot(a, b) < 0) b = new Quaternion(-b.X, -b.Y, -b.Z, -b.W);

        double chord = Math.Sqrt(
            Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2) +
            Math.Pow(a.Z - b.Z, 2) + Math.Pow(a.W - b.W, 2));

        return 2.0 * Math.Asin(Math.Clamp(chord / 2.0, 0, 1)) * 180.0 / Math.PI;
    }

    private static readonly VectorEquality VectorComparer = new();

    private sealed class VectorEquality : IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 1e-3f;

        public int GetHashCode(Vector3 v) => 0;
    }
}
