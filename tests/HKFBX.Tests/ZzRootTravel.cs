using System.Numerics;
using System.Text;
using HKFBX.Fbx;
using HKFBX.Model;
using LeanMeshIO;
using Xunit;
namespace HKFBX.Tests;

// Whether a take's root actually goes anywhere, range by range: an animation made
// for an engine that moves the actor itself usually does not.
public sealed class ZzRootTravel
{
    [Fact]
    public void Measure()
    {
        string path = Environment.GetEnvironmentVariable("FBX_PATH") ?? "";
        string list = Environment.GetEnvironmentVariable("CUT_LIST") ?? "";
        string outDir = Environment.GetEnvironmentVariable("FBX_OUT") ?? Path.GetTempPath();
        if (!File.Exists(path) || !File.Exists(list)) return;

        var document = FbxDocument.Load(path);
        Skeleton rig = FbxAnimationReader.ReadSkeleton(document);
        SampledAnimation whole = FbxAnimationReader.ReadAnimation(document, rig);
        var sb = new StringBuilder();
        sb.AppendLine($"{Path.GetFileName(path)}: {whole.FrameCount} frames, root '{rig.Bones[0].Name}'");

        foreach (string row in File.ReadAllLines(list))
        {
            string[] f = row.Split('\t');
            if (f.Length < 3) continue;
            int first = (int)float.Parse(f[1]), last = Math.Min(whole.FrameCount - 1, (int)float.Parse(f[2]));
            if (last <= first) continue;

            Vector3 from = whole[first, 0].Translation, to = whole[last, 0].Translation;
            var lowest = new Vector3(float.MaxValue); var highest = new Vector3(float.MinValue);
            for (int i = first; i <= last; i++)
            {
                Vector3 p = whole[i, 0].Translation;
                lowest = Vector3.Min(lowest, p); highest = Vector3.Max(highest, p);
            }

            Quaternion turn = Quaternion.Conjugate(whole[first, 0].Rotation) * whole[last, 0].Rotation;
            float yaw = MathF.Atan2(2 * (turn.W * turn.Z + turn.X * turn.Y), 1 - 2 * (turn.Y * turn.Y + turn.Z * turn.Z)) * 180f / MathF.PI;

            sb.AppendLine($"{f[0],-16} {last - first + 1,4} frames  travel {(to - from).Length(),7:F2}  "
                + $"wander {(highest - lowest).Length(),7:F2}  turn {yaw,7:F1} deg");
        }

        File.WriteAllText(Path.Combine(outDir, "root_travel.txt"), sb.ToString());
    }
}
