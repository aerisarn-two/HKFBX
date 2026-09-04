namespace HKFBX.Model;

/// <summary>One bone: its name, its parent, and where it sits in the rest pose.</summary>
public sealed record Bone(string Name, int ParentIndex, BoneTransform ReferencePose);

/// <summary>
/// A skeleton, flattened the way Havok stores it: bones in an array, each naming
/// its parent by index, roots carrying -1.
/// </summary>
/// <remarks>
/// An animation on its own cannot be converted. It has transform *tracks*, and
/// which bone a track drives is decided by the binding's track-to-bone map, while
/// the names those bones have live only in the skeleton file. So both files are
/// needed to produce an FBX anyone can read.
/// </remarks>
public sealed class Skeleton
{
    public required string Name { get; init; }

    public required IReadOnlyList<Bone> Bones { get; init; }

    public int Count => Bones.Count;

    public IEnumerable<int> ChildrenOf(int boneIndex)
    {
        for (var i = 0; i < Bones.Count; i++)
            if (Bones[i].ParentIndex == boneIndex)
                yield return i;
    }

    public IEnumerable<int> Roots()
    {
        for (var i = 0; i < Bones.Count; i++)
            if (Bones[i].ParentIndex < 0)
                yield return i;
    }
}
