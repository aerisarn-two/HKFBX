using System.Numerics;

namespace HKFBX.Model;

/// <summary>What kind of joint holds two ragdoll bodies together.</summary>
public enum JointKind
{
    /// <summary>Anything else the format allows and Skyrim's skeletons do not use.</summary>
    Other = 0,

    /// <summary>
    /// A ball joint with a twist limit, a cone around the twist axis and a plane
    /// limit across it. Shoulders, hips, the spine.
    /// </summary>
    Ragdoll,

    /// <summary>A hinge with one angular limit. Knees, elbows.</summary>
    LimitedHinge,
}

/// <summary>
/// A joint between two ragdoll bodies.
/// </summary>
/// <remarks>
/// The joint's frames are what a converter has to get right and what the tools
/// before this one dropped: each body sees the joint through its own frame, and
/// ck-cmd's export computes both and writes only B, so the A side of every joint
/// is lost on the way out.
///
/// A skeleton file holds each joint twice -- once in the ragdoll instance and once
/// in the physics system, as different objects with the same body pair. Measured
/// across the game, the two copies always agree, so one joint here stands for
/// both and a write puts the values back into each.
/// </remarks>
public sealed record RagdollJoint
{
    /// <summary>The constraint's name, which is usually the child body's.</summary>
    public required string Name { get; init; }

    /// <summary>The body the joint moves.</summary>
    public required string BodyA { get; init; }

    /// <summary>The body it hangs from.</summary>
    public required string BodyB { get; init; }

    public JointKind Kind { get; init; }

    /// <summary>The joint as the moving body sees it.</summary>
    public Matrix4x4 FrameA { get; init; } = Matrix4x4.Identity;

    /// <summary>The joint as the body it hangs from sees it.</summary>
    public Matrix4x4 FrameB { get; init; } = Matrix4x4.Identity;

    /// <summary>How far the twist axis may turn, for a ragdoll joint.</summary>
    public float TwistMinAngle { get; init; }

    public float TwistMaxAngle { get; init; }

    /// <summary>The cone the twist axis may lean within.</summary>
    public float ConeMaxAngle { get; init; }

    /// <summary>The limit across the cone.</summary>
    public float PlaneMinAngle { get; init; }

    public float PlaneMaxAngle { get; init; }

    /// <summary>The one angle a hinge admits.</summary>
    public float HingeMinAngle { get; init; }

    public float HingeMaxAngle { get; init; }

    /// <summary>What it takes to turn the joint at all.</summary>
    public float MaxFrictionTorque { get; init; }

    /// <inheritdoc />
    public override string ToString() => $"{BodyA} -> {BodyB} ({Kind})";
}

/// <summary>
/// One bone of the rig standing for one bone of the ragdoll.
/// </summary>
/// <param name="FromBone">The bone in the skeleton the mapping starts from.</param>
/// <param name="ToBone">The bone it maps to.</param>
/// <param name="Transform">Where the second sits relative to the first.</param>
public sealed record BoneMapping(string FromBone, string ToBone, BoneTransform Transform);
