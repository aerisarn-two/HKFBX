using HKFBX.Hkx;
using HKFBX.Model;

namespace HKFBX.Tests;

/// <summary>
/// A skeleton read and written back must be the file it started as, byte for byte.
/// </summary>
/// <remarks>
/// The stricter test than <see cref="RigCorpusTests"/>, which compares the values
/// a file parses to. Both matter and they are not the same claim: every rig in the
/// game once round tripped with its bones, bodies, joints and limits intact while
/// the bytes differed by hundreds per file, because the writer was rebuilding
/// fields the caller had never touched.
///
/// Three did it. <c>FromBoneTransform</c> rebuilt an <c>hkQsTransform</c> and
/// hard-coded zeros into the padding beside the translation and the scale and into
/// the whole fourth row -- six floats a transform, and a shipped file does not keep
/// zeros there. A motion state's transform went matrix to quaternion and back,
/// which is not bit-exact in the rotation block and drops the fourth column. And a
/// capsule wrote its radius into the fourth component of its vertices, which is not
/// what that component is.
///
/// The rule that fixed all three: compare against what the template holds and write
/// only what actually differs. So this test is really about that rule, and it will
/// fail the next time a field is written unconditionally.
/// </remarks>
public class RigByteExactTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("hkfbx-bytes-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); } catch { /* a temp folder */ }
    }

    /// <summary>
    /// The narrowest claim, and the one that says the rest is achievable: the
    /// packfile reader and writer lose nothing between them.
    /// </summary>
    [CorpusFact]
    public void ThePackfileItselfSurvivesUntouched()
    {
        var offenders = new List<string>();
        int checked_ = 0;

        foreach (string path in Skeletons())
        {
            byte[] original = File.ReadAllBytes(path);
            string output = Path.Combine(_temp, "plain.hkx");

            try
            {
                var root = (HKX2.hkRootLevelContainer)HKX2.Util.ReadHKX(path);
                using (FileStream stream = File.Create(output))
                    HKX2.Util.WriteHKX(root, HKX2.HKXHeader.SkyrimSE(), stream);
            }
            catch
            {
                continue;
            }

            checked_++;

            if (!original.AsSpan().SequenceEqual(File.ReadAllBytes(output)))
                offenders.Add(Path.GetFileName(Path.GetDirectoryName(path)) + "/" + Path.GetFileName(path));
        }

        Assert.True(checked_ > 0, "no skeleton could be read at all");
        Assert.Empty(offenders);
    }

    /// <summary>
    /// And the claim that was false until the writer stopped touching what it had
    /// not been asked to change.
    /// </summary>
    [CorpusFact]
    public void AReadAndWrittenSkeletonIsTheSameFile()
    {
        var offenders = new List<string>();
        int checked_ = 0;

        foreach (string path in Skeletons())
        {
            SkeletonFile file;

            try { file = HkxSkeletonFile.Read(path); }
            catch { continue; }

            if (!file.HasRagdoll) continue;

            byte[] original = File.ReadAllBytes(path);
            string output = Path.Combine(_temp, "written.hkx");

            HkxSkeletonFile.Write(path, file, output);
            checked_++;

            byte[] written = File.ReadAllBytes(output);

            if (original.Length != written.Length)
            {
                offenders.Add($"{Name(path)}: {original.Length} bytes became {written.Length}");
                continue;
            }

            int differing = 0;
            int firstAt = -1;

            for (int i = 0; i < original.Length; i++)
            {
                if (original[i] == written[i]) continue;
                differing++;
                if (firstAt < 0) firstAt = i;
            }

            if (differing > 0)
                offenders.Add($"{Name(path)}: {differing} bytes differ, first at {firstAt}");
        }

        Assert.True(checked_ > 0, "no skeleton with a ragdoll was found");
        Assert.Empty(offenders);
    }

    private static string Name(string path) =>
        $"{Path.GetFileName(Path.GetDirectoryName(path))}/{Path.GetFileName(path)}";

    private static IEnumerable<string> Skeletons() =>
        Directory.EnumerateFiles(Corpus.Root!, "skeleton*.hkx", SearchOption.AllDirectories).Order();
}
