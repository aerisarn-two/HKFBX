using System.Numerics;
using HKFBX.Model;
using Xunit;

namespace HKFBX.Tests;

/// <summary>
/// Putting an exporter's rig under the names the game looks for.
/// </summary>
public sealed class SkeletonNamingTests
{
    /// <summary>
    /// A 3ds Max biped, as an exporter writes one: the hips at the bottom, a spine of however
    /// many links its author asked for, and the limbs hanging off it.
    /// </summary>
    private static Skeleton Biped(string arms, params string[] chain)
    {
        // A 3ds Max biped does not run its spine through the pelvis: Bip01_Pelvis carries the
        // legs and Bip01_Spine carries everything else, and both hang off Bip01.
        var bones = new List<(string Name, int Parent, float Height)>
        {
            ("Bip01", -1, 60f),
            ("Bip01_Pelvis", 0, 62f),
        };

        int at = 0;
        float height = 70f;
        foreach (string link in chain)
        {
            bones.Add((link, at, height));
            at = bones.Count - 1;
            height += 10f;
        }

        foreach (string side in new[] { "L", "R" })
        {
            bones.Add(($"Bip01_{side}_Thigh", 1, 58f));
            int thigh = bones.Count - 1;
            bones.Add(($"Bip01_{side}_Calf", thigh, 30f));
            int calf = bones.Count - 1;
            bones.Add(($"Bip01_{side}_Foot", calf, 5f));

            int shoulder = bones.FindIndex(b => b.Name == arms);
            Assert.True(shoulder >= 0, $"the fixture has no bone called '{arms}'");
            bones.Add(($"Bip01_{side}_Clavicle", shoulder, 95f));
            int clavicle = bones.Count - 1;
            bones.Add(($"Bip01_{side}_UpperArm", clavicle, 92f));
            int upper = bones.Count - 1;
            bones.Add(($"Bip01_{side}_Forearm", upper, 80f));
            int fore = bones.Count - 1;
            bones.Add(($"Bip01_{side}_Hand", fore, 70f));
            int hand = bones.Count - 1;
            bones.Add(($"Bip01_{side}_Finger0", hand, 68f));
            bones.Add(($"Bip01_{side}_Finger01", bones.Count - 1, 67f));
        }

        return new Skeleton
        {
            Name = "Bip01",
            Bones = [.. bones.Select(b => new Bone(
                b.Name,
                b.Parent,
                new BoneTransform(
                    new Vector3(0f, 0f, b.Parent < 0 ? b.Height : b.Height - bones[b.Parent].Height),
                    Quaternion.Identity,
                    Vector3.One))),
            ],
        };
    }

    /// <summary>A bone takes a new name and stays exactly where it was in the rig.</summary>
    [Fact]
    public void RenamingChangesNamesAndNothingElse()
    {
        Skeleton rig = Biped("Bip01_Spine2",
            "Bip01_Spine", "Bip01_Spine1", "Bip01_Spine2", "Bip01_Neck", "Bip01_Head");

        RenamedSkeleton done = SkeletonNaming.Rename(rig, SkeletonNaming.BipedMap(rig, hasMagicNode: false));

        Assert.Equal(rig.Count, done.Rig.Count);
        for (int i = 0; i < rig.Count; i++)
        {
            Assert.Equal(rig.Bones[i].ParentIndex, done.Rig.Bones[i].ParentIndex);
            Assert.Equal(rig.Bones[i].ReferencePose, done.Rig.Bones[i].ReferencePose);
        }

        var names = done.Rig.Bones.Select(b => b.Name).ToList();
        Assert.Contains("NPC COM [COM ]", names);
        Assert.Contains("NPC Pelvis [Pelv]", names);
        Assert.Contains("NPC L Thigh [LThg]", names);
        Assert.Contains("NPC R Foot [Rft ]", names);
        Assert.Contains("NPC L Finger00 [LF00]", names);
        Assert.Contains("NPC L Finger01 [LF01]", names);
        Assert.Contains("NPC R Hand [RHnd]", names);
    }

    /// <summary>
    /// The spine is laid out along whatever chain the rig has, not looked up by name: a rig
    /// with the game's own three links and a neck takes the game's own names in order.
    /// </summary>
    [Fact]
    public void ASpineTheLengthOfTheGamesTakesTheGamesNamesInOrder()
    {
        Skeleton rig = Biped("Bip01_Spine2",
            "Bip01_Spine", "Bip01_Spine1", "Bip01_Spine2", "Bip01_Neck", "Bip01_Head");
        var map = SkeletonNaming.BipedMap(rig, hasMagicNode: false);

        Assert.Equal("NPC Spine [Spn0]", map["Bip01_Spine"]);
        Assert.Equal("NPC Spine1 [Spn1]", map["Bip01_Spine1"]);
        Assert.Equal("NPC Spine2 [Spn2]", map["Bip01_Spine2"]);
        Assert.Equal("NPC Neck [Neck]", map["Bip01_Neck"]);
        Assert.Equal("NPC Head [Head]", map["Bip01_Head"]);
    }

    /// <summary>
    /// The CGTrader skeleton's own chain: four spine links, a neck and two head bones. The
    /// head is the top whatever it is called, the magic node sits above it, and the surplus
    /// link keeps the name it came with rather than taking one that means something else.
    /// </summary>
    [Fact]
    public void ALongerSpineKeepsItsSurplusLinkRatherThanMisnamingIt()
    {
        // The arms hang off Spine4, as the bought skeleton's do.
        Skeleton rig = Biped("Bip01_Spine4",
            "Bip01_Spine", "Bip01_Spine1", "Bip01_Spine2", "Bip01_Spine4",
            "Bip01_Neck1", "Bip01_Head1", "Bip01_Head2");

        var map = SkeletonNaming.BipedMap(rig);

        // Spine4 is where the arms are, so Spine4 is the game's Spine2 and the three links
        // are counted down from there. The bottom link is the surplus one.
        Assert.Equal("NPC Spine2 [Spn2]", map["Bip01_Spine4"]);
        Assert.Equal("NPC Spine1 [Spn1]", map["Bip01_Spine2"]);
        Assert.Equal("NPC Spine [Spn0]", map["Bip01_Spine1"]);
        Assert.Equal("NPC Neck [Neck]", map["Bip01_Neck1"]);
        Assert.Equal("NPC Head [Head]", map["Bip01_Head1"]);
        Assert.Equal("NPC Head MagicNode [Hmag]", map["Bip01_Head2"]);

        Assert.False(map.ContainsKey("Bip01_Spine"));

        RenamedSkeleton done = SkeletonNaming.Rename(rig, map);
        Assert.Contains("Bip01_Spine", done.Unmapped);
        Assert.Contains("Bip01_Spine", done.Rig.Bones.Select(b => b.Name));

        // Every name in the renamed rig is still its own.
        Assert.Equal(done.Rig.Count, done.Rig.Bones.Select(b => b.Name).Distinct().Count());
    }

    /// <summary>
    /// A map that would put two bones under one name is refused rather than applied: whichever
    /// of them something later looked for, the wrong one might answer.
    /// </summary>
    [Fact]
    public void AMapThatCollidesIsRefused()
    {
        Skeleton rig = Biped("Bip01_Spine", "Bip01_Spine", "Bip01_Neck", "Bip01_Head");

        var clashing = new Dictionary<string, string>
        {
            ["Bip01_Neck"] = "NPC Head [Head]",
            ["Bip01_Head"] = "NPC Head [Head]",
        };

        var thrown = Assert.Throws<ArgumentException>(() => SkeletonNaming.Rename(rig, clashing));
        Assert.Contains("NPC Head [Head]", thrown.Message, StringComparison.Ordinal);
    }
}
