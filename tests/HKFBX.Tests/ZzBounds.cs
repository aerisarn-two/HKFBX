using System.Numerics;
using System.Text;
using HKFBX.Fbx;
using HKFBX.Model;
using LeanMeshIO;
using Xunit;
namespace HKFBX.Tests;

// Where a file's rig and its geometry actually stand, which is the first thing to
// know about an asset that was authored for another engine.
public sealed class ZzBounds
{
    [Fact]
    public void Measure()
    {
        string outDir = Environment.GetEnvironmentVariable("FBX_OUT") ?? Path.GetTempPath();
        var sb = new StringBuilder();

        foreach (string path in (Environment.GetEnvironmentVariable("FBX_LIST") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!File.Exists(path)) { sb.AppendLine($"{path}: missing"); continue; }
            var document = FbxDocument.Load(path);
            var scene = new FbxScene(document);
            sb.AppendLine($"== {Path.GetFileName(path)}");

            // the geometry, in the vertices' own space
            var low = new Vector3(float.MaxValue); var high = new Vector3(float.MinValue);
            int points = 0;
            foreach (FbxObject geometry in scene.OfClass("Geometry"))
            {
                if (geometry.Node.Nodes.FirstOrDefault(n => n.Name == "Vertices")?.Properties.FirstOrDefault() is not double[] v) continue;
                for (int i = 0; i + 2 < v.Length; i += 3)
                {
                    var p = new Vector3((float)v[i], (float)v[i + 1], (float)v[i + 2]);
                    low = Vector3.Min(low, p); high = Vector3.Max(high, p); points++;
                }
            }

            if (points > 0)
                sb.AppendLine($"   mesh: {points} vertices, x {low.X:F1}..{high.X:F1}, y {low.Y:F1}..{high.Y:F1}, z {low.Z:F1}..{high.Z:F1} "
                    + $"(size {high.X - low.X:F1} by {high.Y - low.Y:F1} by {high.Z - low.Z:F1})");
            else sb.AppendLine("   mesh: none");

            Skeleton rig;
            try { rig = FbxAnimationReader.ReadSkeleton(document); }
            catch (Exception e) { sb.AppendLine($"   rig: {e.Message}"); continue; }

            var world = new Vector3[rig.Count];
            var lowB = new Vector3(float.MaxValue); var highB = new Vector3(float.MinValue);
            for (int i = 0; i < rig.Count; i++)
            {
                Bone bone = rig.Bones[i];
                Matrix4x4 local = Matrix4x4.CreateScale(bone.ReferencePose.Scale)
                    * Matrix4x4.CreateFromQuaternion(bone.ReferencePose.Rotation)
                    * Matrix4x4.CreateTranslation(bone.ReferencePose.Translation);
                Matrix4x4 at = bone.ParentIndex < 0 ? local : local * _parents[bone.ParentIndex];
                _parents[i] = at;
                world[i] = at.Translation;
                lowB = Vector3.Min(lowB, world[i]); highB = Vector3.Max(highB, world[i]);
            }

            sb.AppendLine($"   rig: {rig.Count} bones, x {lowB.X:F1}..{highB.X:F1}, y {lowB.Y:F1}..{highB.Y:F1}, z {lowB.Z:F1}..{highB.Z:F1}");
            sb.AppendLine($"        root '{rig.Bones[0].Name}' at {world[0]}");
            foreach (var (name, i) in rig.Bones.Select((b, i) => (b.Name, i))
                         .Where(p => p.Name.Contains("Foot", StringComparison.OrdinalIgnoreCase)
                                  || p.Name.Contains("Toe", StringComparison.OrdinalIgnoreCase)
                                  || p.Name.Contains("Head", StringComparison.OrdinalIgnoreCase)))
                sb.AppendLine($"        {name} at {world[i]}");
        }

        File.WriteAllText(Path.Combine(outDir, "bounds.txt"), sb.ToString());
    }

    private readonly Matrix4x4[] _parents = new Matrix4x4[512];
}
