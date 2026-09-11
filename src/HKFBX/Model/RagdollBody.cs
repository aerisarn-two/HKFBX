using System.Numerics;

namespace HKFBX.Model;

/// <summary>
/// A capsule, as Havok stores one: two points and a radius.
/// </summary>
/// <remarks>
/// Every rigid body in every skeleton the game ships is a capsule. Boxes, spheres
/// and convex hulls exist in the format and are not used for ragdolls, so a shape
/// that is not a capsule is carried as the numbers it has rather than converted.
/// </remarks>
public sealed record CapsuleShape(Vector3 VertexA, Vector3 VertexB, float Radius);

/// <summary>
/// One bone of the ragdoll: the body the physics moves, and the bone it drives.
/// </summary>
/// <remarks>
/// A ragdoll is a second skeleton over the first. Each of its bones has a rigid
/// body of the same name, and the constraints between the bodies are the
/// hierarchy -- measured across the game's skeletons, a body per ragdoll bone in
/// every one of them and a constraint for every parent link.
///
/// Which rig bone a body drives cannot be worked out from the names. It holds for
/// the human, whose ragdoll bones are <c>Ragdoll_</c> plus the rig bone's name,
/// and fails for every creature: the bear renumbers as it goes
/// (<c>NPC Spine1</c> to <c>Ragdoll_NPC Spine01</c>) and the chicken shifts by one
/// (<c>Neck0</c> to <c>Ragdoll_Neck01</c>). So <see cref="RigBone"/> is carried,
/// not derived.
/// </remarks>
public sealed record RagdollBody
{
    /// <summary>The body's name, which is also its ragdoll bone's.</summary>
    public required string Name { get; init; }

    /// <summary>The rig bone this body drives, or null when nothing maps to it.</summary>
    public string? RigBone { get; init; }

    /// <summary>Where the body sits, in the rest pose.</summary>
    public BoneTransform Transform { get; init; } = BoneTransform.Identity;

    /// <summary>Its shape.</summary>
    public CapsuleShape? Shape { get; init; }

    /// <summary>
    /// Havok's motion type: 3 is a dynamic box-inertia body, 5 keyframed. The
    /// ragdoll's own bodies are dynamic and the character bumper is not.
    /// </summary>
    public byte MotionType { get; init; }

    /// <summary>The collidable's quality type.</summary>
    public byte QualityType { get; init; }

    /// <summary>
    /// The collision filter word: layer, group and part packed together, which is
    /// what decides whether two bodies of one ragdoll collide with each other.
    /// </summary>
    public uint CollisionFilterInfo { get; init; }

    /// <summary>One over the mass, as Havok stores it. Zero means immovable.</summary>
    public float InverseMass { get; init; }

    /// <summary>One over the diagonal inertia, likewise.</summary>
    public Vector3 InverseInertia { get; init; }

    public float Friction { get; init; }

    public float Restitution { get; init; }

    public float LinearDamping { get; init; }

    public float AngularDamping { get; init; }

    /// <summary>
    /// Whether the ragdoll drives this body.
    /// </summary>
    /// <remarks>
    /// The physics system holds one more body than the ragdoll does: the character
    /// bumper, a keyframed capsule that stops a living actor walking through
    /// things. It is in the file, it is not part of the ragdoll, and a converter
    /// that silently promoted it would give the ragdoll a bone nothing animates.
    /// </remarks>
    public bool InRagdoll { get; init; } = true;

    /// <inheritdoc />
    public override string ToString() => $"{Name} ({(InRagdoll ? "ragdoll" : "physics only")})";
}
