using System.Text;
using HKFBX.Fbx;
using HKFBX.Model;
using LeanMeshIO;
using Xunit;
namespace HKFBX.Tests;

// What one FBX holds: its takes, its rig, and how long the animation runs.
public sealed class ZzTake
{
    [Fact]
    public void Dump()
    {
        string path = Environment.GetEnvironmentVariable("FBX_PATH") ?? "";
        string outDir = Environment.GetEnvironmentVariable("FBX_OUT") ?? Path.GetTempPath();
        if (!File.Exists(path)) return;

        var document = FbxDocument.Load(path);
        var sb = new StringBuilder();
        var scene = new FbxScene(document);

        sb.AppendLine($"{Path.GetFileName(path)}: {scene.OfClass("Model").Count()} models, "
            + $"{scene.OfClass("AnimationStack").Count()} stacks, {scene.OfClass("AnimationCurve").Count()} curves");
        foreach (string take in FbxAnimationReader.ReadTakeNames(document)) sb.AppendLine($"   take '{take}'");

        Skeleton rig;
        try { rig = FbxAnimationReader.ReadSkeleton(document); }
        catch (Exception e) { sb.AppendLine($"   no rig: {e.Message}"); File.WriteAllText(Path.Combine(outDir, "take.txt"), sb.ToString()); return; }

        sb.AppendLine($"   rig: {rig.Count} bones, root '{rig.Bones[0].Name}'");
        foreach (var bone in rig.Bones.Take(8)) sb.AppendLine($"      {bone.Name} parent {bone.ParentIndex}");

        SampledAnimation a = FbxAnimationReader.ReadAnimation(document, rig);
        sb.AppendLine($"   sampled: {a.FrameCount} frames, {a.TrackCount} tracks, {a.Duration:F3}s, "
            + $"frame {a.FrameDuration:F5}s ({(a.FrameDuration > 0 ? 1f / a.FrameDuration : 0):F1} fps)");
        sb.AppendLine($"   annotations: {a.Annotations.Count}");

        File.WriteAllText(Path.Combine(outDir, "take.txt"), sb.ToString());
    }
}
