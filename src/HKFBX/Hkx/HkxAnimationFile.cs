using System.Numerics;
using HKFBX.Codec;
using HKFBX.Model;
using HKX2;

namespace HKFBX.Hkx;

/// <summary>
/// Reads and writes the Havok side: the animation file and the skeleton file it
/// needs to make sense.
/// </summary>
public static class HkxAnimationFile
{
    /// <summary>
    /// Pulls the spline animation and its binding out of an animation packfile.
    /// </summary>
    public static (SplineAnimationData Animation, IReadOnlyList<short> TrackToBone, string SkeletonName)
        ReadAnimation(string path) =>
        ReadAnimationWithEvents(path) is var (a, t, s, _) ? (a, t, s) : default;

    /// <summary>
    /// As <see cref="ReadAnimation"/>, and also the events the animation
    /// announces.
    /// </summary>
    public static (SplineAnimationData Animation, IReadOnlyList<short> TrackToBone,
                   string SkeletonName, IReadOnlyList<AnnotationTrack> Annotations)
        ReadAnimationWithEvents(string path)
    {
        var root = (hkRootLevelContainer)Util.ReadHKX(path);
        hkaAnimationContainer container = ContainerOf(root, path);

        hkaAnimation? animation = container.m_animations.FirstOrDefault();

        if (animation is not hkaSplineCompressedAnimation spline)
        {
            throw new NotSupportedException(
                $"'{path}' holds {(animation?.GetType().Name ?? "no animation")}. "
                + "Only hkaSplineCompressedAnimation is supported, which is what "
                + "every animation in the game ships as.");
        }

        hkaAnimationBinding? binding = container.m_bindings.FirstOrDefault();

        var data = new SplineAnimationData
        {
            NumFrames = spline.m_numFrames,
            NumBlocks = spline.m_numBlocks,
            MaxFramesPerBlock = spline.m_maxFramesPerBlock,
            MaskAndQuantizationSize = spline.m_maskAndQuantizationSize,
            BlockDuration = spline.m_blockDuration,
            BlockInverseDuration = spline.m_blockInverseDuration,
            FrameDuration = spline.m_frameDuration,
            Duration = spline.m_duration,
            TransformTrackCount = spline.m_numberOfTransformTracks,
            FloatTrackCount = spline.m_numberOfFloatTracks,
            BlockOffsets = spline.m_blockOffsets.ToArray(),
            FloatBlockOffsets = spline.m_floatBlockOffsets.ToArray(),
            TransformOffsets = spline.m_transformOffsets.ToArray(),
            FloatOffsets = spline.m_floatOffsets.ToArray(),
            Data = spline.m_data.ToArray(),
        };

        return (data,
                binding?.m_transformTrackToBoneIndices.ToArray() ?? [],
                binding?.m_originalSkeletonName ?? string.Empty,
                ReadAnnotations(spline));
    }

    /// <summary>
    /// Writes an animation packfile, reusing the original as the template.
    /// </summary>
    /// <remarks>
    /// Reusing it matters. An animation file carries more than the curves — the
    /// binding, the annotation tracks, the extracted motion — and rebuilding all
    /// of that from nothing would mean inventing values the original already has
    /// correct. Only the compressed animation is replaced.
    /// </remarks>
    public static void WriteAnimation(string templatePath, SplineAnimationData data, string outputPath)
    {
        var root = (hkRootLevelContainer)Util.ReadHKX(templatePath);
        hkaAnimationContainer container = ContainerOf(root, templatePath);

        if (container.m_animations.FirstOrDefault() is not hkaSplineCompressedAnimation spline)
            throw new NotSupportedException($"'{templatePath}' holds no spline animation to replace");

        spline.m_numFrames = data.NumFrames;
        spline.m_numBlocks = data.NumBlocks;
        spline.m_maxFramesPerBlock = data.MaxFramesPerBlock;
        spline.m_maskAndQuantizationSize = data.MaskAndQuantizationSize;
        spline.m_blockDuration = data.BlockDuration;
        spline.m_blockInverseDuration = data.BlockInverseDuration;
        spline.m_frameDuration = data.FrameDuration;
        spline.m_duration = data.Duration;
        spline.m_numberOfTransformTracks = data.TransformTrackCount;
        spline.m_numberOfFloatTracks = data.FloatTrackCount;
        spline.m_blockOffsets = data.BlockOffsets.ToList();
        spline.m_floatBlockOffsets = data.FloatBlockOffsets.ToList();
        spline.m_transformOffsets = data.TransformOffsets.ToList();
        spline.m_floatOffsets = data.FloatOffsets.ToList();
        spline.m_data = data.Data.ToList();

        using FileStream stream = File.Create(outputPath);
        Util.WriteHKX(root, HKXHeader.SkyrimSE(), stream);
    }

    /// <summary>Reads the bone names, hierarchy and rest pose from a skeleton packfile.</summary>
    public static Skeleton ReadSkeleton(string path)
    {
        var root = (hkRootLevelContainer)Util.ReadHKX(path);

        hkaAnimationContainer container = ContainerOf(root, path);
        hkaSkeleton skeleton = container.m_skeletons.FirstOrDefault()
            ?? throw new InvalidDataException($"'{path}' holds no hkaSkeleton");

        var bones = new List<Bone>(skeleton.m_bones.Count);

        for (int i = 0; i < skeleton.m_bones.Count; i++)
        {
            short parent = i < skeleton.m_parentIndices.Count ? skeleton.m_parentIndices[i] : (short)-1;

            BoneTransform pose = i < skeleton.m_referencePose.Count
                ? ToBoneTransform(skeleton.m_referencePose[i])
                : BoneTransform.Identity;

            bones.Add(new Bone(skeleton.m_bones[i].m_name ?? $"bone{i}", parent, pose));
        }

        return new Skeleton { Name = skeleton.m_name ?? Path.GetFileNameWithoutExtension(path), Bones = bones };
    }

    /// <summary>
    /// The animation's annotation tracks, with the empty ones left out.
    /// </summary>
    /// <remarks>
    /// Havok gives an animation one track per transform track, so most are empty
    /// padding; only the first usually carries anything.
    /// </remarks>
    private static IReadOnlyList<AnnotationTrack> ReadAnnotations(hkaAnimation animation)
    {
        var tracks = new List<AnnotationTrack>();

        foreach (hkaAnnotationTrack track in animation.m_annotationTracks)
        {
            if (track.m_annotations.Count == 0) continue;

            tracks.Add(new AnnotationTrack
            {
                Name = track.m_trackName ?? string.Empty,
                Events = track.m_annotations
                    .Select(a => new AnimationEvent(a.m_time, a.m_text ?? string.Empty))
                    .ToArray(),
            });
        }

        return tracks;
    }

    /// <summary>
    /// Replaces the animation's annotation tracks with these events.
    /// </summary>
    /// <remarks>
    /// The track count is left as Havok wants it -- one per transform track --
    /// and the events go into the tracks by name, or into the first track when
    /// they carry none.
    /// </remarks>
    public static void WriteAnnotations(string templatePath, IReadOnlyList<AnnotationTrack> tracks, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(tracks);

        var root = (hkRootLevelContainer)Util.ReadHKX(templatePath);
        hkaAnimationContainer container = ContainerOf(root, templatePath);

        hkaAnimation animation = container.m_animations.FirstOrDefault()
            ?? throw new InvalidDataException($"'{templatePath}' holds no animation");

        foreach (hkaAnnotationTrack track in animation.m_annotationTracks)
            track.m_annotations = [];

        foreach (AnnotationTrack track in tracks)
        {
            hkaAnnotationTrack? target =
                animation.m_annotationTracks.FirstOrDefault(t => t.m_trackName == track.Name)
                ?? animation.m_annotationTracks.FirstOrDefault();

            if (target is null) break;

            target.m_annotations = track.Events
                .Select(e => new hkaAnnotationTrackAnnotation { m_time = e.Time, m_text = e.Text })
                .ToList();
        }

        using FileStream stream = File.Create(outputPath);
        Util.WriteHKX(root, HKXHeader.SkyrimSE(), stream);
    }

    private static hkaAnimationContainer ContainerOf(hkRootLevelContainer root, string path) =>
        root.m_namedVariants
            .Select(variant => variant?.m_variant)
            .OfType<hkaAnimationContainer>()
            .FirstOrDefault()
        ?? throw new InvalidDataException($"'{path}' holds no hkaAnimationContainer");

    /// <summary>
    /// HKX2 keeps an hkQsTransform in a Matrix4x4: translation in the first row,
    /// rotation quaternion in the second, scale in the third.
    /// </summary>
    private static BoneTransform ToBoneTransform(Matrix4x4 m) =>
        new(new Vector3(m.M11, m.M12, m.M13),
            new Quaternion(m.M21, m.M22, m.M23, m.M24),
            new Vector3(m.M31, m.M32, m.M33));
}
