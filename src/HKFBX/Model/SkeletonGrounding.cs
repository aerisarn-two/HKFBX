using System.Numerics;

namespace HKFBX.Model;

/// <summary>What grounding a rig changed.</summary>
/// <param name="Rig">The rig as it now stands.</param>
/// <param name="Lift">How far everything was raised, in units.</param>
/// <param name="Lowest">The bone that was lowest, which is what the lift was measured from.</param>
/// <param name="RootAdded">Whether a root bone was put at the origin for it.</param>
public sealed record GroundedSkeleton(Skeleton Rig, float Lift, string Lowest, bool RootAdded)
{
    public override string ToString() => RootAdded
        ? $"raised {Lift:F1} units off '{Lowest}', under a root of its own"
        : $"raised {Lift:F1} units off '{Lowest}'";
}

/// <summary>
/// Puts a rig on the ground, under a root that stays there.
/// </summary>
/// <remarks>
/// <para>
/// A Skyrim creature is authored standing on the origin: its root bone is at it and its
/// lowest bone is on it. All 52 of the game's creature skeletons have their lowest bone
/// at or below one unit and not one above it.
/// </para>
/// <para>
/// A rig authored for another engine usually is not. A Biped from 3ds Max is built
/// around its pelvis, so its root <em>is</em> the pelvis and the character hangs below
/// the floor by the length of its legs -- a skeleton bought from a marketplace has its
/// root half a unit above the origin and its toes 37.5 below.
/// </para>
/// <para>
/// Lifting the old root's rest pose does not fix that, because an animation drives the
/// root and overrides its rest. So the lift goes on a bone nothing animates: a root at
/// the origin, which is what root motion moves, and under it a bone holding the lift,
/// and under that the rig as it was. Every clip then plays where it always did, a
/// whole creature's height further up.
/// </para>
/// </remarks>
public static class SkeletonGrounding
{
    /// <summary>The name Skyrim's own creatures give the bone that stays on the ground.</summary>
    public const string RootName = "NPC Root [Root]";

    /// <summary>Raises a rig until its lowest bone rests on the origin.</summary>
    /// <param name="rig">The rig to ground.</param>
    /// <param name="rootName">What to call the root put at the origin.</param>
    /// <param name="liftName">
    /// What to call the bone that holds the lift. It exists because the bone beneath it
    /// is animated and this one must not be.
    /// </param>
    public static GroundedSkeleton Ground(Skeleton rig, string rootName = RootName, string liftName = "Ground")
    {
        ArgumentNullException.ThrowIfNull(rig);
        if (rig.Count == 0) throw new ArgumentException("a rig with no bones cannot be grounded", nameof(rig));

        Vector3[] standing = World(rig);
        int lowest = 0;
        for (int i = 1; i < standing.Length; i++)
            if (standing[i].Z < standing[lowest].Z) lowest = i;

        float lift = -standing[lowest].Z;

        var bones = new List<Bone>
        {
            new(rootName, -1, BoneTransform.Identity),
            new(liftName, 0, new BoneTransform(new Vector3(0, 0, lift), Quaternion.Identity, Vector3.One)),
        };

        // Everything that was a root hangs off the lift; everything else keeps the
        // parent it had, two places further down the list.
        foreach (Bone bone in rig.Bones)
            bones.Add(bone with { ParentIndex = bone.ParentIndex < 0 ? 1 : bone.ParentIndex + 2 });

        return new GroundedSkeleton(
            new Skeleton { Name = rig.Name, Bones = bones },
            lift,
            rig.Bones[lowest].Name,
            RootAdded: true);
    }

    /// <summary>Where every bone stands in the rest pose.</summary>
    private static Vector3[] World(Skeleton rig)
    {
        var world = new Matrix4x4[rig.Count];
        var places = new Vector3[rig.Count];

        for (int i = 0; i < rig.Count; i++)
        {
            BoneTransform rest = rig.Bones[i].ReferencePose;
            Matrix4x4 local = Matrix4x4.CreateScale(rest.Scale)
                * Matrix4x4.CreateFromQuaternion(rest.Rotation)
                * Matrix4x4.CreateTranslation(rest.Translation);

            int parent = rig.Bones[i].ParentIndex;
            world[i] = parent < 0 ? local : local * world[parent];
            places[i] = world[i].Translation;
        }

        return places;
    }
}
