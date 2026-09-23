using System.Text;
using HKFBX.Fbx;
using HKFBX.Model;
using LeanMeshIO;
using Xunit;
namespace HKFBX.Tests;

// What a take's feet say each of its clips should travel at.
public sealed class ZzInferMotion
{
    [Fact]
    public void Measure()
    {
        string path = Environment.GetEnvironmentVariable("FBX_PATH") ?? "";
        string list = Environment.GetEnvironmentVariable("CUT_LIST") ?? "";
        string feet = Environment.GetEnvironmentVariable("FEET") ?? "";
        string outDir = Environment.GetEnvironmentVariable("FBX_OUT") ?? Path.GetTempPath();
        if (!File.Exists(path) || !File.Exists(list) || feet.Length == 0) return;

        var document = FbxDocument.Load(path);
        Skeleton rig = FbxAnimationReader.ReadSkeleton(document);
        SampledAnimation whole = FbxAnimationReader.ReadAnimation(document, rig);
        string[] names = feet.Split(';', StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        sb.AppendLine($"{Path.GetFileName(path)}: feet {string.Join(", ", names)}");

        foreach (string row in File.ReadAllLines(list))
        {
            string[] f = row.Split('\t');
            if (f.Length < 3) continue;
            int first = (int)float.Parse(f[1]), last = Math.Min(whole.FrameCount - 1, (int)float.Parse(f[2]));
            if (last <= first) continue;

            int frames = last - first + 1;
            var transforms = new BoneTransform[frames * whole.TrackCount];
            Array.Copy(whole.Transforms, first * whole.TrackCount, transforms, 0, transforms.Length);
            var clip = new SampledAnimation
            {
                FrameCount = frames,
                TrackCount = whole.TrackCount,
                Duration = (frames - 1) * whole.FrameDuration,
                FrameDuration = whole.FrameDuration,
                Transforms = transforms,
                TrackToBone = whole.TrackToBone,
            };

            InferredMotion? inferred = FootMotion.Infer(clip, rig, names);
            sb.AppendLine(inferred is null
                ? $"{f[0],-16} no foot ever planted"
                : $"{f[0],-16} {inferred.Speed,7:F1} units/s at {inferred.Heading,7:F0} deg, "
                  + $"confidence {inferred.Confidence:P0}");
        }

        File.WriteAllText(Path.Combine(outDir, "inferred.txt"), sb.ToString());
    }
}
