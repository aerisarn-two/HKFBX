using System.Numerics;
using HKFBX.Fbx;
using HKFBX.Model;
using HKFBX.Tests.Skyrim;
using LeanMeshIO;

namespace HKFBX.Tests;

/// <summary>
/// Root motion, which lives outside the .hkx and has to be carried in and out
/// separately.
/// </summary>
public class RootMotionTests
{
    private static RootMotion Walking() => new()
    {
        Duration = 1f,
        Translations =
        [
            new TranslationKey(0.00f, Vector3.Zero),
            new TranslationKey(0.25f, new Vector3(0f, -20f, 0f)),
            new TranslationKey(0.50f, new Vector3(0f, -45f, 0f)),
            new TranslationKey(1.00f, new Vector3(0f, -90f, 0f)),
        ],
        Rotations =
        [
            new RotationKey(0.00f, Quaternion.Identity),
            new RotationKey(1.00f, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 4f)),
        ],
    };

    private static FbxDocument SaveAndLoad(FbxDocument document)
    {
        using var stream = new MemoryStream();
        document.Save(stream);
        stream.Position = 0;
        return FbxDocument.Load(stream);
    }

    [Fact]
    public void MotionIsSampledOntoTheRootBone()
    {
        Skeleton skeleton = Synthetic.Skeleton();
        SampledAnimation animation = Synthetic.Animation() with { RootMotion = Walking() };

        FbxDocument document = SaveAndLoad(FbxAnimationWriter.Build(skeleton, animation, "walk"));

        Skeleton read = FbxAnimationReader.ReadSkeleton(document);
        RootMotion motion = FbxAnimationReader.ReadRootMotion(document, read);

        Assert.NotEmpty(motion.Translations);
        Assert.True(motion.HasMovement);

        // Sampled onto the frame grid on the way out, so the keys come back at
        // frame times rather than where they were authored, and the last frame
        // is the animation's end rather than the motion's. What has to survive is
        // the curve they describe, up to where the animation stops.
        RootMotion original = Walking();

        foreach (TranslationKey key in original.Translations)
        {
            if (key.Time > animation.Duration) continue;

            Vector3 expected = original.TranslationAt(key.Time);
            Vector3 actual = motion.TranslationAt(key.Time);

            Assert.True((expected - actual).Length() < 0.5f,
                $"at {key.Time}s expected {expected}, read {actual}");
        }

        // And the far end, at whatever time the animation actually reaches.
        Assert.True(
            (original.TranslationAt(animation.Duration) - motion.TranslationAt(animation.Duration)).Length() < 0.5f);
    }

    [Fact]
    public void MotionReplacesTheRootTrackRatherThanAddingToIt()
    {
        Skeleton skeleton = Synthetic.Skeleton();

        // The synthetic animation slides track 0 along X. The motion travels
        // along Y instead, and is meant to win.
        SampledAnimation animation = Synthetic.Animation() with { RootMotion = Walking() };

        FbxDocument document = SaveAndLoad(FbxAnimationWriter.Build(skeleton, animation, "walk"));

        Skeleton read = FbxAnimationReader.ReadSkeleton(document);
        RootMotion motion = FbxAnimationReader.ReadRootMotion(document, read);

        Vector3 end = motion.TranslationAt(animation.Duration);
        float expected = Walking().TranslationAt(animation.Duration).Y;

        Assert.True(Math.Abs(end.Y - expected) < 1f, $"the motion did not drive the root: {end}");
        Assert.True(Math.Abs(end.X) < 1f, $"the root track is still driving X: {end}");

        // One curve node per property, or a reader treats the second as a blend.
        var scene = new HKFBX.Fbx.FbxScene(document);
        FbxObject root = scene.OfClass("Model").First(m => BoneNames.Unsanitize(m.Name) == "Root");

        int translationNodes = scene.PropertyConnectionsTo(root.Id)
            .Count(c => c.Property == "Lcl Translation");

        Assert.Equal(1, translationNodes);
    }

    [Fact]
    public void AnimationWithoutMotionLeavesTheRootTrackAlone()
    {
        Skeleton skeleton = Synthetic.Skeleton();
        SampledAnimation animation = Synthetic.Animation();

        FbxDocument document = SaveAndLoad(FbxAnimationWriter.Build(skeleton, animation, "idle"));

        Skeleton read = FbxAnimationReader.ReadSkeleton(document);
        SampledAnimation back = FbxAnimationReader.ReadAnimation(
            document, read, animation.FrameCount, animation.FrameDuration);

        // Track 0 slides 0..10 along X, and should still be doing so.
        Assert.True(Math.Abs(back[animation.FrameCount - 1, 0].Translation.X - 10f) < 1e-2f);
    }

    [Fact]
    public void AStationaryMotionIsNotMistakenForMovement()
    {
        // What most clips carry: one key at the end holding nothing.
        var stationary = new RootMotion
        {
            Duration = 1.33333f,
            Translations = [new TranslationKey(1.33333f, Vector3.Zero)],
            Rotations = [new RotationKey(1.33333f, Quaternion.Identity)],
        };

        Assert.False(stationary.IsEmpty);
        Assert.False(stationary.HasMovement);
    }

    [Fact]
    public void SamplesBetweenKeys()
    {
        RootMotion motion = Walking();

        // Half way between the keys at 0.25 and 0.50.
        Vector3 middle = motion.TranslationAt(0.375f);
        Assert.Equal(-32.5f, middle.Y, 3);

        // Flat outside the keyed range rather than extrapolating.
        Assert.Equal(Vector3.Zero, motion.TranslationAt(-1f));
        Assert.Equal(-90f, motion.TranslationAt(99f).Y, 3);
    }
}

/// <summary>
/// The format Skyrim keeps root motion in, which is not the .hkx.
/// </summary>
public class AnimationDataFileTests
{
    private const string OneBlock = """
        13
        1
        1
        1 0 251.9 0
        1
        1 0 0 0 1
        """;

    [Fact]
    public void ReadsAMotionBlock()
    {
        MotionEntry entry = Assert.Single(AnimationDataFile.ParseMotions(OneBlock));

        Assert.Equal(13, entry.ClipId);
        Assert.Equal(1f, entry.Motion.Duration);

        TranslationKey translation = Assert.Single(entry.Motion.Translations);
        Assert.Equal(1f, translation.Time);
        Assert.Equal(new Vector3(0f, 251.9f, 0f), translation.Value);

        RotationKey rotation = Assert.Single(entry.Motion.Rotations);
        Assert.Equal(Quaternion.Identity, rotation.Value);
    }

    [Fact]
    public void MotionBlocksRoundTripThroughTheFormat()
    {
        MotionEntry original = AnimationDataFile.ParseMotions(OneBlock).Single();

        MotionEntry again = AnimationDataFile
            .ParseMotions(AnimationDataFile.WriteMotion(original))
            .Single();

        Assert.Equal(original.ClipId, again.ClipId);
        Assert.Equal(original.Motion.Duration, again.Motion.Duration);
        Assert.Equal(original.Motion.Translations, again.Motion.Translations);
        Assert.Equal(original.Motion.Rotations, again.Motion.Rotations);
    }

    [Fact]
    public void ReadsSeveralKeysAndKeepsTheirOrder()
    {
        const string block = """
            14
            0.833333
            4
            0.333333 2.39327e-006 -8.38587 0
            0.5 5.1638e-006 -23.4908 0
            0.666667 6.87373e-006 -46.8449 0
            0.833333 7.75341e-006 -62.3171 0
            2
            0.0666667 0 0 0.000488281 1
            0.1 0 0 0.0337167 0.999431
            """;

        MotionEntry entry = Assert.Single(AnimationDataFile.ParseMotions(block));

        Assert.Equal(4, entry.Motion.Translations.Count);
        Assert.Equal(2, entry.Motion.Rotations.Count);
        Assert.True(entry.Motion.HasMovement);

        Assert.Equal(-62.3171f, entry.Motion.Translations[^1].Value.Y, 3);

        for (int i = 1; i < entry.Motion.Translations.Count; i++)
        {
            Assert.True(entry.Motion.Translations[i].Time > entry.Motion.Translations[i - 1].Time,
                "keys came back out of order");
        }
    }

    [Fact]
    public void IgnoresLinesThatAreNotMotionBlocks()
    {
        const string noise = """
            MainIdle
            12
            1
            0
            0
            1
            clipEnd:6.65767

            13
            1
            1
            1 0 251.9 0
            1
            1 0 0 0 1
            """;

        MotionEntry entry = Assert.Single(AnimationDataFile.ParseMotions(noise));
        Assert.Equal(13, entry.ClipId);
    }

    [CorpusFact]
    public void ReadsEverythingSkyrimShips()
    {
        string path = Path.Combine(Corpus.Root!, "animationdatasinglefile.txt");

        if (!File.Exists(path)) return;

        // The parse is exact: it either consumes the file or says where it gave
        // up. Nothing here is a tolerance.
        IReadOnlyList<AnimationProject> projects = AnimationDataFile.ReadProjects(path);

        Assert.Equal(429, projects.Count);
        Assert.Equal("ChickenProject.txt", projects[0].Name);
        Assert.Equal(49, projects.Count(p => p.HasCache));

        var motions = projects.SelectMany(p => p.Motions).ToList();

        Assert.Equal(10597, projects.Sum(p => p.Clips.Count));
        Assert.Equal(6725, motions.Count);

        // Every entry carries keys; a clip with nothing to say is simply absent.
        Assert.All(motions, m => Assert.False(m.Motion.IsEmpty));

        Assert.Equal(2632, motions.Count(m => m.Motion.Translations.Any(k => k.Value.LengthSquared() > 1e-8f)));
        Assert.Equal(511, motions.Count(m => m.Motion.Rotations.Any(k => Math.Abs(k.Value.W) < 1f - 1e-6f)));

        Assert.All(motions, m =>
        {
            for (int i = 1; i < m.Motion.Translations.Count; i++)
                Assert.True(m.Motion.Translations[i].Time >= m.Motion.Translations[i - 1].Time);
        });
    }

    [CorpusFact]
    public void FindsAClipsMotionByName()
    {
        string path = Path.Combine(Corpus.Root!, "animationdatasinglefile.txt");

        if (!File.Exists(path)) return;

        AnimationProject chicken = AnimationDataFile.ReadProjects(path)
            .First(p => p.Name == "ChickenProject.txt");

        Assert.Contains("Behaviors\\ChickenBehavior.hkx", chicken.Assets);

        ClipEntry idle = chicken.Clip("Idle Fulbody2[mirror]")!;

        Assert.Equal(4, idle.CacheIndex);
        Assert.Equal(1f, idle.PlaybackSpeed);
        Assert.Equal(4, idle.Events.Count);
        Assert.Equal("SoundPlay.NPCChickenScratch", idle.Events[0].Text);
        Assert.Equal(0.333333f, idle.Events[0].Time, 5);
        Assert.Equal("idleStop", idle.Events[^1].Text);

        // A chicken pecking about does not travel.
        Assert.False(chicken.MotionFor("Idle Fulbody2[mirror]").HasMovement);

        // Naming a clip that is not there is not an error; it has no motion.
        Assert.True(chicken.MotionFor("NoSuchClip").IsEmpty);
    }
}

/// <summary>
/// The counted-block structure the animation data is written in.
/// </summary>
public class AnimationProjectTests
{
    // One project: three asset paths, two clips, and motion for the second.
    private const string OneProject = """
        1
        DemoProject.txt
        20
        1
        3
        Behaviors\DemoBehavior.hkx
        Characters\DemoCharacter.hkx
        Character Assets\skeleton.HKX
        1
        MainIdle
        12
        1
        0
        0
        1
        clipEnd:6.65767

        WalkForward
        13
        1
        0
        0
        0
        6
        13
        1
        1
        1 0 251.9 0
        1
        1 0 0 0 1
        """;

    [Fact]
    public void ReadsAProjectWithItsClipsAndMotion()
    {
        AnimationProject project = Assert.Single(AnimationDataFile.ParseProjects(OneProject));

        Assert.Equal("DemoProject.txt", project.Name);
        Assert.True(project.HasCache);
        Assert.Equal(3, project.Assets.Count);
        Assert.Equal("Characters\\DemoCharacter.hkx", project.Assets[1]);

        Assert.Equal(2, project.Clips.Count);
        Assert.Equal("MainIdle", project.Clips[0].Name);
        Assert.Equal(12, project.Clips[0].CacheIndex);

        // The motion belongs to the clip that shares its cache index.
        Assert.True(project.MotionFor("WalkForward").HasMovement);
        Assert.False(project.MotionFor("MainIdle").HasMovement);

        Assert.Equal(251.9f, project.MotionFor("WalkForward").TranslationAt(1f).Y, 3);
    }

    [Fact]
    public void AProjectWithoutACacheHasNoMotionBlock()
    {
        const string text = """
            1
            BowProject.txt
            2
            0
            0
            """;

        AnimationProject project = Assert.Single(AnimationDataFile.ParseProjects(text));

        Assert.False(project.HasCache);
        Assert.Empty(project.Clips);
        Assert.Empty(project.Motions);
        Assert.Empty(project.Assets);
    }

    [Fact]
    public void EventTextsKeepAColonOfTheirOwn()
    {
        // The time is taken from the last colon, so a text carrying one survives.
        const string text = """
            1
            P.txt
            9
            0
            1
            Clip
            1
            1
            0
            0
            1
            Sound:Play.Thing:0.25
            5
            1
            1
            1
            1 0 1 0
            0
            """;

        ClipEntry clip = Assert.Single(AnimationDataFile.ParseProjects(text)[0].Clips);

        Assert.Equal("Sound:Play.Thing", clip.Events[0].Text);
        Assert.Equal(0.25f, clip.Events[0].Time, 5);
    }

    [Fact]
    public void SaysWhereItGaveUp()
    {
        const string broken = """
            1
            P.txt
            3
            1
            2
            only one path
            """;

        var error = Assert.Throws<InvalidDataException>(() => AnimationDataFile.ParseProjects(broken));

        Assert.Contains("line", error.Message, StringComparison.Ordinal);
    }
}
