using HKFBX.Codec;
using HKFBX.Fbx;
using HKFBX.Hkx;
using HKFBX.Model;
using HKFBX.Tests.Skyrim;
using LeanMeshIO;

namespace HKFBX.Tests;

/// <summary>
/// The conversion, run against animations that actually travel.
/// </summary>
/// <remarks>
/// Everything else about root motion is tested against keys written here, which
/// proves the plumbing and nothing about the shape of real data. These take
/// motion Bethesda authored — clips that turn a full 180, that sprint 2,000
/// units, that spin without travelling at all — put them through the writer, and
/// read the file back off the disk.
///
/// Set HKFBX_SAMPLES to a directory to keep the results and to widen the run to
/// one clip from every project that has any; otherwise a handful go to a
/// temporary directory that is removed afterwards. That is the whole of what
/// producing sample files takes: there is no separate tool.
/// </remarks>
public class CorpusMotionTests
{
    private const string SamplesVar = "HKFBX_SAMPLES";

    private static string? SampleDir
    {
        get
        {
            string? value = Environment.GetEnvironmentVariable(SamplesVar);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }

    [CorpusFact]
    public void TheCorpusOffersClipsThatTravelAndNameTheirAnimation()
    {
        // Cheap: the join only, no decompression.
        IReadOnlyList<MotionSample> samples = MotionSamples.Find(Corpus.Root!, int.MaxValue);

        Assert.True(samples.Count > 1000, $"only {samples.Count} clips pair with an animation");
        Assert.True(samples.Select(s => s.Project).Distinct().Count() > 35,
            "the pairing found too few projects to be believable");

        // Both kinds have to be present, or the conversion is only ever asked
        // the easy question.
        Assert.Contains(samples, s => MotionSamples.Travel(s.Motion) > 500f);
        Assert.Contains(samples, s => MotionSamples.Turn(s.Motion) > 3f);

        // A clip that turns in place travels nowhere, and is the case a writer
        // that quietly drops rotation still passes without.
        Assert.Contains(samples, s => MotionSamples.Travel(s.Motion) < 1f
                                      && MotionSamples.Turn(s.Motion) > 1f);
    }

    [CorpusFact]
    public void MotionAndEventsSurviveRealAnimations()
    {
        string? keep = SampleDir;
        string outDir = keep ?? Path.Combine(Path.GetTempPath(), $"hkfbx-motion-{Guid.NewGuid():N}");

        Directory.CreateDirectory(outDir);

        // One per project when the results are being kept, otherwise enough to
        // cover both kinds of motion without paying for 42 decompressions.
        IReadOnlyList<MotionSample> found = MotionSamples.Find(Corpus.Root!);
        IReadOnlyList<MotionSample> samples = keep is not null ? found : Spread(found);

        // Not merely non-empty: a Spread that quietly collapsed to one sample
        // would still pass every assertion below it.
        Assert.True(samples.Count >= 6, $"only {samples.Count} samples to convert");

        var codec = new MopperAnimationCodec();
        var written = new List<string>();

        try
        {
            foreach (MotionSample sample in samples)
            {
                (SplineAnimationData spline, IReadOnlyList<short> trackToBone, _) =
                    HkxAnimationFile.ReadAnimation(sample.AnimationPath);

                Skeleton skeleton = HkxAnimationFile.ReadSkeleton(sample.SkeletonPath);
                SampledAnimation decompressed = codec.Decompress(spline);

                var animation = new SampledAnimation
                {
                    FrameCount = decompressed.FrameCount,
                    TrackCount = decompressed.TrackCount,
                    Duration = decompressed.Duration,
                    FrameDuration = decompressed.FrameDuration,
                    Transforms = decompressed.Transforms,
                    TrackToBone = trackToBone,
                    RootMotion = sample.Motion,
                    Annotations = sample.Clip.Events.Count == 0
                        ? []
                        : [new AnnotationTrack { Name = "", Events = sample.Clip.Events }],
                };

                string output = Path.Combine(outDir, sample.Name + ".fbx");
                FbxAnimationWriter.Build(skeleton, animation, sample.Clip.Name).Save(output);
                written.Add(output);

                // Off the disk, not out of memory: the bytes are what a viewer
                // will be given.
                using FileStream stream = File.OpenRead(output);
                FbxDocument document = FbxDocument.Load(stream);

                Skeleton readSkeleton = FbxAnimationReader.ReadSkeleton(document);
                RootMotion motion = FbxAnimationReader.ReadRootMotion(document, readSkeleton);

                Assert.True(motion.HasMovement,
                    $"{sample.Name}: the motion did not survive at all");

                // Compared only where both are defined: the motion is sampled onto
                // the animation's frame grid, and the clip may outrun the animation.
                float at = Math.Min(animation.Duration, sample.Motion.Duration);

                float drift = (sample.Motion.TranslationAt(at) - motion.TranslationAt(at)).Length();
                Assert.True(drift < 1f, $"{sample.Name}: root drifted {drift:F2} units by {at:F2}s");

                // A turn has to arrive as a turn, not as a translation that
                // happens to end in the right place.
                float turned = MotionSamples.Turn(sample.Motion);
                if (turned > 0.5f)
                {
                    float back = MotionSamples.Turn(motion);
                    Assert.True(Math.Abs(turned - back) < 0.2f,
                        $"{sample.Name}: turned {turned:F2} rad, read back {back:F2}");
                }

                IReadOnlyList<AnnotationTrack> events = FbxAnimationReader.ReadEvents(document);
                var texts = events.SelectMany(t => t.Events).ToList();

                Assert.Equal(sample.Clip.Events.Count, texts.Count);

                foreach (AnimationEvent expected in sample.Clip.Events)
                {
                    Assert.Contains(texts, e => e.Text == expected.Text
                                                && Math.Abs(e.Time - expected.Time) < 0.05f);
                }
            }
        }
        finally
        {
            if (keep is null && Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        }

        Assert.Equal(samples.Count, written.Count);
    }

    /// <summary>
    /// A few samples covering the ways a clip can move, so the short run still
    /// asks every question the long one does.
    /// </summary>
    private static IReadOnlyList<MotionSample> Spread(IReadOnlyList<MotionSample> all)
    {
        var picked = new List<MotionSample>();

        void Take(Func<MotionSample, bool> want, int count)
        {
            picked.AddRange(all
                .Where(want)
                .Where(s => !picked.Contains(s))
                .OrderByDescending(s => s.Clip.Events.Count)
                .Take(count));
        }

        Take(s => MotionSamples.Travel(s.Motion) < 1f && MotionSamples.Turn(s.Motion) > 1f, 2);
        Take(s => MotionSamples.Turn(s.Motion) > 3f, 2);
        Take(s => MotionSamples.Travel(s.Motion) > 500f, 2);
        Take(_ => true, 2);

        return picked;
    }
}
