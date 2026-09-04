using System.Numerics;
using HKFBX.Model;

namespace HKFBX.Tests;

/// <summary>
/// Skeletons and animations built in code, so the tests that check structure
/// rather than game data need nothing on disk.
/// </summary>
internal static class Synthetic
{
    /// <summary>A root with two bones under it, and a grandchild.</summary>
    public static Skeleton Skeleton() => new()
    {
        Name = "test",
        Bones =
        [
            new Bone("Root", -1, BoneTransform.Identity),
            new Bone("Spine", 0, new BoneTransform(new Vector3(0, 0, 10), Quaternion.Identity, Vector3.One)),
            new Bone("Head", 1, new BoneTransform(new Vector3(0, 0, 20), Quaternion.Identity, Vector3.One)),
            new Bone("Tail", 0, new BoneTransform(new Vector3(0, 0, -5), Quaternion.Identity, Vector3.One)),
        ],
    };

    /// <summary>
    /// One track per bone: the first slides along X, the second turns about Z,
    /// the rest hold still. Enough movement that a dropped track is obvious.
    /// </summary>
    public static SampledAnimation Animation(int frames = 30, int tracks = 4)
    {
        const float frameDuration = 1f / 30f;
        var transforms = new BoneTransform[frames * tracks];

        for (int f = 0; f < frames; f++)
        {
            float t = frames > 1 ? f / (float)(frames - 1) : 0f;

            for (int k = 0; k < tracks; k++)
            {
                Vector3 translation = k == 0 ? new Vector3(t * 10f, 0, 0) : Vector3.Zero;
                Quaternion rotation = k == 1
                    ? Quaternion.CreateFromAxisAngle(Vector3.UnitZ, t * MathF.PI / 2f)
                    : Quaternion.Identity;

                transforms[f * tracks + k] = new BoneTransform(translation, rotation, Vector3.One);
            }
        }

        return new SampledAnimation
        {
            FrameCount = frames,
            TrackCount = tracks,
            Duration = frameDuration * (frames - 1),
            FrameDuration = frameDuration,
            Transforms = transforms,
        };
    }
}
