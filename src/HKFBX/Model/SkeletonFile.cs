namespace HKFBX.Model;

/// <summary>
/// Everything a skeleton packfile holds: the rig, the ragdoll over it, and the
/// physics that moves it.
/// </summary>
/// <remarks>
/// A Skyrim <c>skeleton.hkx</c> is not one skeleton. It carries the animation rig,
/// a second and much shorter skeleton for the ragdoll, a rigid body per ragdoll
/// bone, a joint per link between them, and two mappings that say which bone of
/// one stands for which bone of the other.
///
/// The same ragdoll is also in the creature's <c>skeleton.nif</c>, in different
/// units and hung off the rig bones rather than off a skeleton of its own. The
/// two have to agree, so what this model holds is deliberately the part they
/// share: bodies, joints, frames and limits, in Havok's units.
/// </remarks>
public sealed class SkeletonFile
{
    /// <summary>The animation rig.</summary>
    public required Skeleton Rig { get; init; }

    /// <summary>
    /// The rig's float slots, which carry no geometry but are part of it.
    /// </summary>
    /// <remarks>
    /// Eight of them on the human rig -- <c>hkFade:AnimObjectA</c>,
    /// <c>hkVis:Weapon</c> and the like -- driving fades and visibility rather than
    /// bones. An animation binds float tracks to them by index, so dropping them
    /// silently renumbers what is left.
    /// </remarks>
    public IReadOnlyList<string> FloatSlots { get; init; } = [];

    /// <summary>The ragdoll skeleton, when the file has one.</summary>
    public Skeleton? Ragdoll { get; init; }

    /// <summary>The rigid bodies, ragdoll and otherwise.</summary>
    public IReadOnlyList<RagdollBody> Bodies { get; init; } = [];

    /// <summary>The joints between them, one per body pair.</summary>
    public IReadOnlyList<RagdollJoint> Joints { get; init; } = [];

    /// <summary>Rig bone to ragdoll bone.</summary>
    public IReadOnlyList<BoneMapping> RigToRagdoll { get; init; } = [];

    /// <summary>Ragdoll bone back to rig bone.</summary>
    public IReadOnlyList<BoneMapping> RagdollToRig { get; init; } = [];

    /// <summary>Whether there is a ragdoll at all.</summary>
    public bool HasRagdoll => Ragdoll is not null && Bodies.Count > 0;

    /// <inheritdoc />
    public override string ToString() =>
        $"{Rig.Name}: {Rig.Count} bones"
        + (HasRagdoll ? $", ragdoll {Ragdoll!.Count} bones, {Bodies.Count} bodies, {Joints.Count} joints" : ", no ragdoll");
}
