using System.Numerics;
using HKFBX.Fbx;
using HKFBX.Model;
using Xunit;

namespace HKFBX.Tests;

/// <summary>
/// Renaming a scene's bones where they are, so that whatever converts it next sees them.
/// </summary>
public sealed class FbxBoneRenamingTests
{
    private static Skeleton Rig() => new()
    {
        Name = "Bip01",
        Bones =
        [
            new Bone("Bip01", -1, Rest(0, 0, 40)),
            new Bone("Bip01_Pelvis", 0, Rest(0, 0, 0)),
            new Bone("Bip01_L_Hand", 1, Rest(10, 0, 0)),
        ],
    };

    private static BoneTransform Rest(float x, float y, float z) =>
        new(new Vector3(x, y, z), Quaternion.Identity, Vector3.One);

    private static LeanMeshIO.FbxDocument Scene(Skeleton rig) =>
        FbxSkeletonWriter.Build(new SkeletonFile { Rig = rig }, BoneNaming.Havok);

    /// <summary>
    /// The scene itself is changed, so a rig read back out of it comes back under the new
    /// names -- which is the whole point, since what reads the scene next is a converter
    /// nobody is passing a map to.
    /// </summary>
    [Fact]
    public void ARenamedSceneReadsBackUnderTheNewNames()
    {
        Skeleton rig = Rig();
        var document = Scene(rig);

        var map = new Dictionary<string, string>
        {
            ["Bip01"] = "NPC COM [COM ]",
            ["Bip01_L_Hand"] = "NPC L Hand [LHnd]",
        };

        RenamedBones done = FbxBoneRenaming.Rename(document, map);
        Assert.Equal(2, done.Count);

        Skeleton back = FbxAnimationReader.ReadSkeleton(document);
        var names = back.Bones.Select(b => b.Name).ToList();

        Assert.Contains("NPC COM [COM ]", names);
        Assert.Contains("NPC L Hand [LHnd]", names);

        // A bone the map says nothing about keeps the name it had.
        Assert.Contains("Bip01_Pelvis", names);
        Assert.Equal(rig.Count, back.Count);
    }

    /// <summary>
    /// Nothing moves. The bones are where they were, because a skinned mesh's corrections are
    /// carried by its skeleton and only the names have to change here.
    /// </summary>
    [Fact]
    public void RenamingMovesNothing()
    {
        Skeleton rig = Rig();
        var document = Scene(rig);

        Skeleton before = FbxAnimationReader.ReadSkeleton(document);
        FbxBoneRenaming.Rename(document, new Dictionary<string, string> { ["Bip01"] = "NPC COM [COM ]" });
        Skeleton after = FbxAnimationReader.ReadSkeleton(document);

        Assert.Equal(before.Count, after.Count);
        for (int i = 0; i < before.Count; i++)
        {
            Assert.Equal(before.Bones[i].ParentIndex, after.Bones[i].ParentIndex);
            Assert.Equal(before.Bones[i].ReferencePose.Translation, after.Bones[i].ReferencePose.Translation);
        }
    }

    /// <summary>The names go back, for a document something else still wants as it was.</summary>
    [Fact]
    public void TheNamesCanBePutBack()
    {
        var document = Scene(Rig());
        var map = new Dictionary<string, string> { ["Bip01"] = "NPC COM [COM ]", ["Bip01_Pelvis"] = "NPC Pelvis [Pelv]" };

        RenamedBones done = FbxBoneRenaming.Rename(document, map);
        Assert.Contains("NPC Pelvis [Pelv]", FbxAnimationReader.ReadSkeleton(document).Bones.Select(b => b.Name));

        FbxBoneRenaming.Restore(document, done);

        var names = FbxAnimationReader.ReadSkeleton(document).Bones.Select(b => b.Name).ToList();
        Assert.Contains("Bip01", names);
        Assert.Contains("Bip01_Pelvis", names);
        Assert.DoesNotContain("NPC Pelvis [Pelv]", names);
    }

    /// <summary>A map naming nothing in the scene changes nothing and says so.</summary>
    [Fact]
    public void AMapNamingNothingInTheSceneChangesNothing()
    {
        var document = Scene(Rig());

        RenamedBones done = FbxBoneRenaming.Rename(
            document, new Dictionary<string, string> { ["SomeOtherCreature_Hand"] = "NPC L Hand [LHnd]" });

        Assert.Equal(0, done.Count);
        Assert.Contains("Bip01", FbxAnimationReader.ReadSkeleton(document).Bones.Select(b => b.Name));
    }
}
