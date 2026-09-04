using System.Diagnostics;
using System.Runtime.InteropServices;
using HKFBX.Model;

namespace HKFBX.Codec;

/// <summary>
/// Runs Havok's spline codec through mopper as a child process.
/// </summary>
/// <remarks>
/// mopper.exe is a Win32 binary, but it talks in files and exit codes with no GUI
/// and no COM, so it runs unmodified under Wine — which is what makes this work
/// on Linux at all. Out-of-process also sidesteps matching bitness: this is a
/// 32-bit executable and the caller is whatever it is.
///
/// The Mopper.Native package copies mopper.exe beside the build output, so the
/// default probe finds it without configuration.
/// </remarks>
public sealed class MopperAnimationCodec : IAnimationCodec
{
    /// <summary>How long to let mopper run before giving up.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Path to mopper.exe. When null it is looked for beside this assembly, then
    /// on PATH.
    /// </summary>
    public string? MopperPath { get; set; }

    /// <summary>
    /// Launcher used on non-Windows hosts. Empty runs the binary directly.
    /// </summary>
    public string WineCommand { get; set; } = "wine";

    public SampledAnimation Decompress(SplineAnimationData animation)
    {
        ArgumentNullException.ThrowIfNull(animation);

        using var work = new Workspace();

        using (FileStream input = File.Create(work.Input))
            AnimationInterchange.WriteSpline(input, animation);

        Run("-anim-decompress", work.Input, work.Output);

        using FileStream output = File.OpenRead(work.Output);
        SampledAnimation samples = AnimationInterchange.ReadSamples(output);

        // The codec knows nothing about bones, so carry the binding across here.
        return samples;
    }

    public SplineAnimationData Compress(SampledAnimation animation, CompressionSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(animation);

        using var work = new Workspace();

        using (FileStream input = File.Create(work.Input))
            AnimationInterchange.WriteSamples(input, animation);

        var args = new List<string> { "-anim-compress", work.Input, work.Output };

        // Positional and order-dependent: tolerance first, then quantization, so
        // a quantization choice needs a tolerance in front of it.
        if (settings?.Tolerance is { } tolerance || settings?.Rotation is not null)
        {
            args.Add((settings?.Tolerance ?? -1f).ToString("R", System.Globalization.CultureInfo.InvariantCulture));

            if (settings?.Rotation is { } rotation)
                args.Add(((int)rotation).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        Run(args.ToArray());

        using FileStream output = File.OpenRead(work.Output);
        return AnimationInterchange.ReadSpline(output);
    }

    private void Run(params string[] arguments)
    {
        string mopper = Resolve();

        var startInfo = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        bool needsWine = !RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                      && !string.IsNullOrEmpty(WineCommand);

        if (needsWine)
        {
            startInfo.FileName = WineCommand;
            startInfo.ArgumentList.Add(mopper);
        }
        else
        {
            startInfo.FileName = mopper;
        }

        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"could not start {startInfo.FileName}");

        // Read both pipes before waiting: a child that fills one while the parent
        // waits on exit deadlocks.
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw new TimeoutException($"mopper did not finish within {Timeout}");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"mopper {string.Join(' ', arguments)} exited {process.ExitCode}: "
                + stderr.GetAwaiter().GetResult().Trim());
        }
    }

    private string Resolve()
    {
        if (!string.IsNullOrEmpty(MopperPath))
        {
            if (!File.Exists(MopperPath))
                throw new FileNotFoundException("mopper.exe not found", MopperPath);

            return MopperPath;
        }

        string beside = Path.Combine(AppContext.BaseDirectory, "mopper.exe");
        if (File.Exists(beside)) return beside;

        string? onPath = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator)
            .Select(directory => Path.Combine(directory, "mopper.exe"))
            .FirstOrDefault(File.Exists);

        return onPath ?? throw new FileNotFoundException(
            "mopper.exe not found beside this assembly or on PATH. It ships in the "
            + "Mopper.Native package, which copies it to the output directory; set "
            + nameof(MopperPath) + " to point somewhere else.");
    }

    /// <summary>A scratch directory that cleans up after itself.</summary>
    private sealed class Workspace : IDisposable
    {
        private readonly string _directory =
            Directory.CreateTempSubdirectory("hkfbx-").FullName;

        public string Input => Path.Combine(_directory, "in.bin");

        public string Output => Path.Combine(_directory, "out.bin");

        public void Dispose()
        {
            try { Directory.Delete(_directory, recursive: true); }
            catch (IOException) { /* a leftover temp file is not worth failing over */ }
        }
    }
}
