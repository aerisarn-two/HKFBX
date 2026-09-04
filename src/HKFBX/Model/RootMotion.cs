using System.Numerics;

namespace HKFBX.Model;

/// <summary>A root translation at a moment in the clip.</summary>
public readonly record struct TranslationKey(float Time, Vector3 Value);

/// <summary>A root rotation at a moment in the clip.</summary>
public readonly record struct RotationKey(float Time, Quaternion Value);

/// <summary>
/// Where a clip carries the character while it plays.
/// </summary>
/// <remarks>
/// An animation's root track and its root motion are different things. The track
/// animates the root bone in place; the motion is the travel across the ground,
/// which Havok keeps apart so the game can apply it to the character controller
/// rather than to the skeleton. Skyrim stores it outside the .hkx altogether, in
/// the animation data that ships alongside the behaviour graphs, which is why it
/// has to be supplied separately here.
///
/// Keys are sparse and need not land on frame boundaries: they are sampled onto
/// the frame grid when written, and read back at whatever times the curves carry.
/// </remarks>
public sealed class RootMotion
{
    public static RootMotion None { get; } = new();

    /// <summary>Seconds the clip runs for, as the motion data states it.</summary>
    public float Duration { get; init; }

    public IReadOnlyList<TranslationKey> Translations { get; init; } = [];

    public IReadOnlyList<RotationKey> Rotations { get; init; } = [];

    public bool IsEmpty => Translations.Count == 0 && Rotations.Count == 0;

    /// <summary>
    /// Whether anything actually moves. A clip commonly carries one key holding
    /// the identity, which is not motion.
    /// </summary>
    public bool HasMovement =>
        Translations.Any(k => k.Value.LengthSquared() > 1e-8f)
        || Rotations.Any(k => Math.Abs(k.Value.W) < 1f - 1e-6f);

    /// <summary>The translation at a time, linearly between keys and flat outside them.</summary>
    public Vector3 TranslationAt(float time)
    {
        if (Translations.Count == 0) return Vector3.Zero;
        if (time <= Translations[0].Time) return Translations[0].Value;
        if (time >= Translations[^1].Time) return Translations[^1].Value;

        for (int i = 1; i < Translations.Count; i++)
        {
            if (Translations[i].Time < time) continue;

            TranslationKey a = Translations[i - 1], b = Translations[i];
            float span = b.Time - a.Time;

            return span <= 0 ? a.Value : Vector3.Lerp(a.Value, b.Value, (time - a.Time) / span);
        }

        return Translations[^1].Value;
    }

    /// <summary>The rotation at a time, along the shortest arc between keys.</summary>
    public Quaternion RotationAt(float time)
    {
        if (Rotations.Count == 0) return Quaternion.Identity;
        if (time <= Rotations[0].Time) return Rotations[0].Value;
        if (time >= Rotations[^1].Time) return Rotations[^1].Value;

        for (int i = 1; i < Rotations.Count; i++)
        {
            if (Rotations[i].Time < time) continue;

            RotationKey a = Rotations[i - 1], b = Rotations[i];
            float span = b.Time - a.Time;

            return span <= 0 ? a.Value : Quaternion.Slerp(a.Value, b.Value, (time - a.Time) / span);
        }

        return Rotations[^1].Value;
    }
}
