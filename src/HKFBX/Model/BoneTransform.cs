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

        Vector3 general = Extract(q, fold: false);

        // Straight up or straight down, the first and last axes turn about the
        // same line and the pair is no longer separable: the general formulas ask
        // atan2(0, 0) for both and the rotation between them is lost. 173 of the
        // 4,132 bone rest poses in Skyrim's skeletons sit there -- weapon and
        // shield mounts, mostly -- and a shield exported through the general case
        // came back turned by a right angle.
        //
        // Which decomposition is right is not worth a threshold to guess at. Near
        // the pole the general one loses the angle and the folded one is exact;
        // away from it the folded one is an approximation and the general one is
        // exact; so both are rebuilt and the closer one wins. It costs two
        // quaternion multiplications and it cannot be wrong by more than the
        // better of the two.
        // A hair, not a tolerance: 1e-6 of a unit quaternion's dot product is
        // already a sixth of a degree, which is the error being hunted here.
        if (Closeness(q, general) > 1e-12f)
        {
            Vector3 folded = Extract(q, fold: true);

            if (Closeness(q, folded) < Closeness(q, general)) return folded;
        }

        return general;
    }

    /// <summary>
    /// The extraction itself, either splitting the turn between the first and last
    /// axes or folding all of it into the last.
    /// </summary>
    /// <remarks>
    /// In double throughout: asin is ill-conditioned where its argument approaches
    /// one, which is exactly where a skeleton's mount points sit, and the digits a
    /// float has left there are the ones that decide the angle.
    /// </remarks>
    private static Vector3 Extract(Quaternion q, bool fold)
    {
        double x = q.X, y = q.Y, z = q.Z, w = q.W;

        double sinPitch = Math.Clamp(2.0 * ((w * y) - (z * x)), -1.0, 1.0);
        double pitch = Math.Asin(sinPitch);

        if (fold)
        {
            double folded = Math.Atan2(
                -2.0 * ((x * y) - (w * z)),
                1.0 - (2.0 * ((x * x) + (z * z))));

            return new Vector3(0f, (float)pitch, (float)folded);
        }

        double roll = Math.Atan2(
            2.0 * ((w * x) + (y * z)),
            1.0 - (2.0 * ((x * x) + (y * y))));

        double yaw = Math.Atan2(
            2.0 * ((w * z) + (x * y)),
            1.0 - (2.0 * ((y * y) + (z * z))));

        return new Vector3((float)roll, (float)pitch, (float)yaw);
    }

    /// <summary>How far a decomposition lands from the rotation it came from.</summary>
    private static float Closeness(Quaternion q, Vector3 euler)
    {
        Quaternion rebuilt = FromEulerXyz(Vector3.Zero, euler, Vector3.One).Rotation;

        return 1f - MathF.Abs(Quaternion.Dot(q, Quaternion.Normalize(rebuilt)));
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
