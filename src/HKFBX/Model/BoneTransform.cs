using System.Numerics;

namespace HKFBX.Model;

/// <summary>
/// A bone's placement for one frame: where it sits, how it is turned, how it is
/// scaled. Havok's hkQsTransform in the terms this project needs, with the two
/// padding lanes it never uses left out.
/// </summary>
public readonly record struct BoneTransform(Vector3 Translation, Quaternion Rotation, Vector3 Scale)
{
    public static BoneTransform Identity { get; } =
        new(Vector3.Zero, Quaternion.Identity, Vector3.One);

    /// <summary>
    /// Euler angles in radians, XYZ order, which is what FBX rotation curves carry.
    /// </summary>
    /// <remarks>
    /// FBX stores rotation as three per-axis curves, not as quaternions, so the
    /// conversion has to happen somewhere. Doing it here keeps the winding and
    /// gimbal handling in one place rather than in the curve writer.
    /// </remarks>
    public Vector3 ToEulerXyz()
    {
        Quaternion q = Quaternion.Normalize(Rotation);

        // Standard XYZ extraction, with the pitch clamped so that a value nudged
        // just past 1 by rounding does not turn into NaN at the poles.
        float sinPitch = 2f * (q.W * q.Y - q.Z * q.X);
        sinPitch = Math.Clamp(sinPitch, -1f, 1f);

        float roll = MathF.Atan2(2f * (q.W * q.X + q.Y * q.Z),
                                 1f - 2f * (q.X * q.X + q.Y * q.Y));
        float pitch = MathF.Asin(sinPitch);
        float yaw = MathF.Atan2(2f * (q.W * q.Z + q.X * q.Y),
                                1f - 2f * (q.Y * q.Y + q.Z * q.Z));

        return new Vector3(roll, pitch, yaw);
    }

    /// <summary>
    /// The inverse of <see cref="ToEulerXyz"/>, and of FBX's default rotation
    /// order: rotate about X, then Y, then Z.
    /// </summary>
    /// <remarks>
    /// Composed from axis rotations rather than through CreateFromYawPitchRoll,
    /// whose arguments are named for aircraft axes rather than for X, Y and Z and
    /// so invite exactly the mix-up that composing explicitly cannot make.
    /// </remarks>
    public static BoneTransform FromEulerXyz(Vector3 translation, Vector3 euler, Vector3 scale)
    {
        Quaternion x = Quaternion.CreateFromAxisAngle(Vector3.UnitX, euler.X);
        Quaternion y = Quaternion.CreateFromAxisAngle(Vector3.UnitY, euler.Y);
        Quaternion z = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, euler.Z);

        // Right to left is order of application, so X happens first.
        return new BoneTransform(translation, z * y * x, scale);
    }
}
