using System.Text;
using HKFBX.Fbx;
using LeanMeshIO;
using Xunit;
namespace HKFBX.Tests;

// Cuts a take into the clips a list names, and writes the result beside the source.
public sealed class ZzCutTake
{
    [Fact]
    public void Cut()
    {
        string path = Environment.GetEnvironmentVariable("FBX_PATH") ?? "";
        string list = Environment.GetEnvironmentVariable("CUT_LIST") ?? "";
        string outPath = Environment.GetEnvironmentVariable("FBX_CUT") ?? "";
        if (!File.Exists(path) || !File.Exists(list) || outPath.Length == 0) return;

        var cuts = File.ReadAllLines(list)
            .Select(l => l.Split('\t'))
            .Where(f => f.Length >= 3)
            .Select(f => new TakeCut(f[0].Trim(), (int)float.Parse(f[1]), (int)float.Parse(f[2])))
            .ToList();

        var document = FbxDocument.Load(path);
        CutReport report = FbxTakeCutter.Cut(document, cuts);

        var sb = new StringBuilder();
        foreach (CutStack cut in report.Cuts)
            sb.AppendLine($"{cut.Name,-16} {cut.Frames,4} frames  {cut.Duration,6:F2}s  {cut.BoundBones} bones");
        foreach ((string name, string why) in report.Refused) sb.AppendLine($"refused {name}: {why}");
        sb.AppendLine($"== {report.Cuts.Count} cuts, {report.Refused.Count} refused");

        document.Save(outPath);
        sb.AppendLine($"== written {outPath}, {new FileInfo(outPath).Length / 1024} KB, "
            + $"takes now {FbxAnimationReader.ReadTakeNames(FbxDocument.Load(outPath)).Count}");
        File.WriteAllText(Path.ChangeExtension(outPath, ".txt"), sb.ToString());
    }
}
