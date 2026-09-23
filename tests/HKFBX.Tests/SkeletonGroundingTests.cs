using System.Numerics;
using HKFBX.Model;
using Xunit;

namespace HKFBX.Tests;

/// <summary>
/// Putting a rig on the ground, which a rig authored for another engine usually is not.
/// </summary>
public sealed class SkeletonGroundingTests
{
    /// <summary>
    /// A Biped from 3ds Max: the root is the pelvis, so the legs hang below the floor.
    /// </summary>
    private static Skeleton Hanging() => new()
    {
        Name = "biped",
        Bones =
        [
            new Bone("Bip01", -1, new BoneTransform(new Vector3(0, 0, 0.5f), Quaternion.Identity, Vector3.One)),
            new Bone("Bip01_Spine", 0, new BoneTransform(new Vector3(0, 0, 10), Quaternion.Identity, Vector3.One)),
            new Bone("Bip01_L_Foot", 0, new BoneTransform(new Vector3(-6, 0, -38), Quaternion.Identity, Vector3.One)),
            new Bone("Bip01_R_Foot", 0, new BoneTransform(new Vector3(6, 0, -38), Quaternion.Identity, Vector3.One)),
        ],
    };

    private static Vector3 Standing(Skeleton rig, string bone)
    {
        var world = new Matrix4x4[rig.Count];
        for (int i = 0; i < rig.Count; i++)
        {
            BoneTransform rest = rig.Bones[i].ReferencePose;
            Matrix4x4 local = Matrix4x4.CreateScale(rest.Scale)
                * Matrix4x4.CreateFromQuaternion(rest.Rotation)
                * Matrix4x4.CreateTranslation(rest.Translation);
            int parent = rig.Bones[i].ParentIndex;
            world[i] = parent < 0 ? local : local * world[parent];
        }

        return world[rig.Bones.Select((b, i) => (b.Name, i)).First(b => b.Name == bone).i].Translation;
    }

    [Fact]
    public void TheLowestBoneEndsUpOnTheGround()
    {
        GroundedSkeleton grounded = SkeletonGrounding.Ground(Hanging());

        Assert.Equal(37.5f, grounded.Lift, 3);
        Assert.Contains("Foot", grounded.Lowest, StringComparison.Ordinal);
        Assert.Equal(0f, Standing(grounded.Rig, "Bip01_L_Foot").Z, 3);
    }

    /// <summary>
    /// The lift goes on a bone nothing animates, because an animation drives the old
    /// root and overrides whatever its rest pose says.
    /// </summary>
    [Fact]
    public void TheRootStaysOnTheOriginAndSomethingElseCarriesTheLift()
    {
        GroundedSkeleton grounded = SkeletonGrounding.Ground(Hanging());

        Assert.Equal(SkeletonGrounding.RootName, grounded.Rig.Bones[0].Name);
        Assert.Equal(-1, grounded.Rig.Bones[0].ParentIndex);
        Assert.Equal(Vector3.Zero, Standing(grounded.Rig, SkeletonGrounding.RootName));

        // The bone under it holds the whole lift, and the old root hangs off that.
        Assert.Equal(37.5f, Standing(grounded.Rig, "Ground").Z, 3);
        Assert.Equal("Ground", grounded.Rig.Bones[grounded.Rig.Bones.First(b => b.Name == "Bip01").ParentIndex].Name);
    }

    /// <summary>Everything keeps the parent it had, and its place relative to it.</summary>
    [Fact]
    public void TheRigItselfIsUnchangedApartFromBeingHigher()
    {
        Skeleton was = Hanging();
        GroundedSkeleton grounded = SkeletonGrounding.Ground(was);

        Assert.Equal(was.Count + 2, grounded.Rig.Count);

        foreach (Bone bone in was.Bones)
        {
            Bone now = grounded.Rig.Bones.First(b => b.Name == bone.Name);
            Assert.Equal(bone.ReferencePose.Translation, now.ReferencePose.Translation);
            Assert.Equal(
                Standing(was, bone.Name).Z + grounded.Lift,
                Standing(grounded.Rig, bone.Name).Z, 3);
        }
    }

    /// <summary>A rig already standing on the ground is not moved.</summary>
    [Fact]
    public void ARigAlreadyOnTheGroundIsLeftWhereItIs()
    {
        Skeleton standing = new()
        {
            Name = "standing",
            Bones =
            [
                new Bone("Root", -1, BoneTransform.Identity),
                new Bone("Head", 0, new BoneTransform(new Vector3(0, 0, 120), Quaternion.Identity, Vector3.One)),
            ],
        };

        Assert.Equal(0f, SkeletonGrounding.Ground(standing).Lift, 3);
    }
}
