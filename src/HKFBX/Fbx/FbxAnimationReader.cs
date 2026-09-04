using System.Numerics;
using HKFBX.Model;
using LeanMeshIO;
using LeanMeshIO.Formats.Fbx;

namespace HKFBX.Fbx;

/// <summary>
/// Reads a skeleton and an animation back out of an FBX document.
/// </summary>
/// <remarks>
/// The inverse of <see cref="FbxAnimationWriter"/>, and it has to work on files
/// this project did not write — an animator's export has been through a tool
/// that knows nothing about track indices or frame counts.
///
/// So nothing is assumed about the keys. Curves are sampled onto a frame grid
/// rather than read off one key per frame: an editor is free to drop keys it
/// considers redundant, move them off frame boundaries, or key each component at
/// different times, and all three are ordinary output.
/// </remarks>
public static class FbxAnimationReader
{
    private static readonly string[] Axes = ["d|X", "d|Y", "d|Z"];

    /// <summary>The skeleton as a node hierarchy, in the order the bones appear.</summary>
    /// <remarks>
    /// Havok requires a parent to precede its children, and FBX guarantees no
    /// such thing, so the bones come back in a depth-first walk from the roots
    /// rather than in file order.
    /// </remarks>
    public static Skeleton ReadSkeleton(FbxDocument document, string name = "skeleton")
    {
        ArgumentNullException.ThrowIfNull(document);

        var scene = new FbxScene(document);
        List<FbxObject> models = SkeletonModels(scene);

        var indexById = new Dictionary<long, int>();
        var ordered = new List<FbxObject>(models.Count);
        var parentOf = new Dictionary<long, long>();

        foreach (FbxObject model in models)
        {
            FbxObject? parent = scene.ParentsOf(model.Id)
                .FirstOrDefault(p => p.Class == "Model");

            if (parent is not null) parentOf[model.Id] = parent.Id;
        }

        var byId = models.ToDictionary(m => m.Id);

        void Visit(FbxObject model)
        {
            if (indexById.ContainsKey(model.Id)) return;

            indexById[model.Id] = ordered.Count;
            ordered.Add(model);

            foreach (FbxObject child in models
                .Where(m => parentOf.TryGetValue(m.Id, out long p) && p == model.Id))
                Visit(child);
        }

        foreach (FbxObject root in models.Where(m => !parentOf.ContainsKey(m.Id))) Visit(root);

        // A cycle, or a parent that is not itself a bone, would leave models
        // unvisited. Take them as roots rather than dropping them.
        foreach (FbxObject orphan in models) Visit(orphan);

        var bones = new List<Bone>(ordered.Count);

        foreach (FbxObject model in ordered)
        {
            int parentIndex = parentOf.TryGetValue(model.Id, out long parentId)
                             && indexById.TryGetValue(parentId, out int index)
                ? index
                : -1;

            // Undone unconditionally: a name with no marker is unchanged, so a
            // file written either way comes back with Havok's spelling.
            bones.Add(new Bone(BoneNames.Unsanitize(model.Name), parentIndex, LocalTransformOf(model)));
        }

        return new Skeleton { Name = name, Bones = bones };
    }

    /// <summary>
    /// Samples the animation onto a frame grid, one track per bone of
    /// <paramref name="skeleton"/>.
    /// </summary>
    /// <remarks>
    /// Tracks line up with the skeleton's bones, so the result needs no binding
    /// to be understood. A bone with no curves holds its rest pose, which is
    /// what an exporter that keys only the bones it moved leaves behind.
    /// </remarks>
    public static SampledAnimation ReadAnimation(
        FbxDocument document, Skeleton skeleton, int? frameCount = null, float? frameDuration = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(skeleton);

        var scene = new FbxScene(document);

        var models = SkeletonModels(scene)
            .GroupBy(m => BoneNames.Unsanitize(m.Name))
            .ToDictionary(g => g.Key, g => g.First());

        // Curve nodes reach their model through an object-to-property edge, so
        // the binding is read the same way it was written.
        var channels = new Dictionary<(string Bone, string Property), Curve[]>();
        float latest = 0f;

        foreach (FbxObject model in models.Values)
        {
            foreach ((FbxObject source, string property) in scene.PropertyConnectionsTo(model.Id))
            {
                if (source.Class != "AnimationCurveNode") continue;
                if (property is not ("Lcl Translation" or "Lcl Rotation" or "Lcl Scaling")) continue;

                var curves = new Curve[3];

                for (int axis = 0; axis < 3; axis++)
                {
                    FbxObject? curve = scene.PropertyConnectionsTo(source.Id)
                        .Where(c => c.Property == Axes[axis] && c.Source.Class == "AnimationCurve")
                        .Select(c => c.Source)
                        .FirstOrDefault();

                    curves[axis] = Curve.From(curve, source.Properties.GetDouble(Axes[axis]));
                    latest = MathF.Max(latest, curves[axis].End);
                }

                channels[(model.Name, property)] = curves;
            }
        }

        float step = frameDuration ?? 1f / 30f;
        int frames = frameCount ?? Math.Max(1, (int)MathF.Round(latest / step) + 1);

        var transforms = new BoneTransform[frames * skeleton.Count];

        for (int frame = 0; frame < frames; frame++)
        {
            float time = frame * step;

            for (int bone = 0; bone < skeleton.Count; bone++)
            {
                Bone rest = skeleton.Bones[bone];
                string boneName = rest.Name;

                Vector3 translation = Sample(channels, boneName, "Lcl Translation", time,
                    rest.ReferencePose.Translation);

                Vector3 euler = Sample(channels, boneName, "Lcl Rotation", time,
                    ToDegrees(rest.ReferencePose.ToEulerXyz()));

                Vector3 scale = Sample(channels, boneName, "Lcl Scaling", time,
                    rest.ReferencePose.Scale);

                transforms[frame * skeleton.Count + bone] =
                    BoneTransform.FromEulerXyz(translation, ToRadians(euler), scale);
            }
        }

        return new SampledAnimation
        {
            FrameCount = frames,
            TrackCount = skeleton.Count,
            Duration = step * (frames - 1),
            FrameDuration = step,
            Transforms = transforms,
        };
    }

    /// <summary>
    /// The travel the root bone carries, read back as sparse keys.
    /// </summary>
    /// <remarks>
    /// Only meaningful for a document written with root motion, or one an
    /// animator has moved the root in. A root that merely sits at its rest pose
    /// comes back empty rather than as a run of identical keys.
    /// </remarks>
    public static RootMotion ReadRootMotion(FbxDocument document, Skeleton skeleton)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(skeleton);

        int root = skeleton.Roots().FirstOrDefault(-1);
        if (root < 0) return RootMotion.None;

        var scene = new FbxScene(document);

        FbxObject? model = SkeletonModels(scene)
            .FirstOrDefault(m => BoneNames.Unsanitize(m.Name) == skeleton.Bones[root].Name);

        if (model is null) return RootMotion.None;

        Curve[]? translation = ChannelOf(scene, model, "Lcl Translation");
        Curve[]? rotation = ChannelOf(scene, model, "Lcl Rotation");

        if (translation is null && rotation is null) return RootMotion.None;

        var translations = new List<TranslationKey>();
        var rotations = new List<RotationKey>();

        foreach (float time in KeyTimes(translation))
        {
            translations.Add(new TranslationKey(time, new Vector3(
                translation![0].At(time, 0), translation[1].At(time, 0), translation[2].At(time, 0))));
        }

        foreach (float time in KeyTimes(rotation))
        {
            var euler = new Vector3(
                rotation![0].At(time, 0), rotation[1].At(time, 0), rotation[2].At(time, 0));

            rotations.Add(new RotationKey(time,
                BoneTransform.FromEulerXyz(Vector3.Zero, ToRadians(euler), Vector3.One).Rotation));
        }

        float duration = Math.Max(
            translations.Count > 0 ? translations[^1].Time : 0f,
            rotations.Count > 0 ? rotations[^1].Time : 0f);

        return new RootMotion
        {
            Duration = duration,
            Translations = translations,
            Rotations = rotations,
        };
    }

    /// <summary>
    /// The events the document carries, from the enum channels the writer puts
    /// them on.
    /// </summary>
    public static IReadOnlyList<AnnotationTrack> ReadEvents(FbxDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var scene = new FbxScene(document);
        var tracks = new List<AnnotationTrack>();

        foreach (FbxObject model in SkeletonModels(scene))
        {
            foreach ((FbxObject source, string property) in scene.PropertyConnectionsTo(model.Id))
            {
                if (source.Class != "AnimationCurveNode") continue;
                if (!property.StartsWith("hkEvents", StringComparison.Ordinal)) continue;

                // The texts live in the enum's value list, which follows the
                // current index rather than replacing it.
                string list = model.Properties.GetValues(property)
                    .OfType<string>()
                    .LastOrDefault() ?? string.Empty;

                string[] texts = list.Split('~', StringSplitOptions.RemoveEmptyEntries);

                FbxObject? curve = scene.PropertyConnectionsTo(source.Id)
                    .Where(c => c.Property == "d|" + property && c.Source.Class == "AnimationCurve")
                    .Select(c => c.Source)
                    .FirstOrDefault();

                if (curve is null) continue;

                var events = new List<AnimationEvent>();

                foreach ((float time, float value) in KeysOf(curve))
                {
                    int index = (int)MathF.Round(value);
                    events.Add(new AnimationEvent(time,
                        index >= 0 && index < texts.Length ? texts[index] : string.Empty));
                }

                tracks.Add(new AnnotationTrack
                {
                    Name = property.Length > "hkEvents".Length ? property["hkEvents".Length..] : string.Empty,
                    Events = events,
                });
            }
        }

        return tracks;
    }

    private static Curve[]? ChannelOf(FbxScene scene, FbxObject model, string property)
    {
        FbxObject? node = scene.PropertyConnectionsTo(model.Id)
            .Where(c => c.Property == property && c.Source.Class == "AnimationCurveNode")
            .Select(c => c.Source)
            .FirstOrDefault();

        if (node is null) return null;

        var curves = new Curve[3];
        for (int axis = 0; axis < 3; axis++)
        {
            FbxObject? curve = scene.PropertyConnectionsTo(node.Id)
                .Where(c => c.Property == Axes[axis] && c.Source.Class == "AnimationCurve")
                .Select(c => c.Source)
                .FirstOrDefault();

            curves[axis] = Curve.From(curve, node.Properties.GetDouble(Axes[axis]));
        }

        return curves;
    }

    /// <summary>The times any component of a channel is keyed at, in order.</summary>
    private static IEnumerable<float> KeyTimes(Curve[]? channel)
    {
        if (channel is null) return [];

        return channel.SelectMany(c => c.Times)
            .Distinct()
            .OrderBy(t => t);
    }

    private static IEnumerable<(float Time, float Value)> KeysOf(FbxObject curve)
    {
        long[] times = curve.Node.Nodes
            .FirstOrDefault(n => n.Name == "KeyTime")?.Properties[0] as long[] ?? [];

        float[] values = curve.Node.Nodes
            .FirstOrDefault(n => n.Name == "KeyValueFloat")?.Properties[0] as float[] ?? [];

        for (int i = 0; i < Math.Min(times.Length, values.Length); i++)
            yield return ((float)((double)times[i] / FbxAnimationWriter.TimeUnitsPerSecond), values[i]);
    }

    /// <summary>The take's name, or empty when the document holds no stack.</summary>
    public static string ReadTakeName(FbxDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return new FbxScene(document).OfClass("AnimationStack").FirstOrDefault()?.Name ?? string.Empty;
    }

    /// <summary>
    /// The models that are bones. A skeleton attribute is what separates a bone
    /// from a mesh or a null, and an exporter that omits it leaves nothing to go
    /// on, so those models are taken as bones too when nothing else qualifies.
    /// </summary>
    private static List<FbxObject> SkeletonModels(FbxScene scene)
    {
        var limbs = scene.OfClass("Model")
            .Where(m => m.SubClass is "LimbNode" or "Limb")
            .ToList();

        return limbs.Count > 0 ? limbs : scene.OfClass("Model").ToList();
    }

    private static Vector3 Sample(
        Dictionary<(string, string), Curve[]> channels,
        string bone, string property, float time, Vector3 fallback)
    {
        if (!channels.TryGetValue((bone, property), out Curve[]? curves)) return fallback;

        return new Vector3(
            curves[0].At(time, fallback.X),
            curves[1].At(time, fallback.Y),
            curves[2].At(time, fallback.Z));
    }

    private static BoneTransform LocalTransformOf(FbxObject model)
    {
        FbxProperties properties = model.Properties;

        (double tx, double ty, double tz) = properties.GetVector3("Lcl Translation");
        (double rx, double ry, double rz) = properties.GetVector3("Lcl Rotation");
        (double sx, double sy, double sz) = properties.GetVector3("Lcl Scaling", 1);

        return BoneTransform.FromEulerXyz(
            new Vector3((float)tx, (float)ty, (float)tz),
            ToRadians(new Vector3((float)rx, (float)ry, (float)rz)),
            new Vector3((float)sx, (float)sy, (float)sz));
    }

    private static Vector3 ToDegrees(Vector3 radians) =>
        new(radians.X * 180f / MathF.PI, radians.Y * 180f / MathF.PI, radians.Z * 180f / MathF.PI);

    private static Vector3 ToRadians(Vector3 degrees) =>
        new(degrees.X * MathF.PI / 180f, degrees.Y * MathF.PI / 180f, degrees.Z * MathF.PI / 180f);

    /// <summary>
    /// One component's keys, sampled by time.
    /// </summary>
    /// <remarks>
    /// Linear between keys and flat outside them. The keys carry an
    /// interpolation flag and may carry tangents, and honouring cubic ones would
    /// reproduce an editor's curve more faithfully — but Havok stores samples
    /// anyway, so the error is bounded by the frame rate either way, and getting
    /// the shape roughly right beats getting the tangents subtly wrong.
    /// </remarks>
    private readonly struct Curve
    {
        private readonly long[] _times;
        private readonly float[] _values;

        private Curve(long[] times, float[] values)
        {
            _times = times;
            _values = values;
        }

        /// <summary>The times this curve is keyed at, in seconds.</summary>
        public IEnumerable<float> Times =>
            _times.Select(t => (float)((double)t / FbxAnimationWriter.TimeUnitsPerSecond));

        public float End => _times.Length == 0
            ? 0f
            : (float)((double)_times[^1] / FbxAnimationWriter.TimeUnitsPerSecond);

        public static Curve From(FbxObject? curve, double fallback)
        {
            if (curve is null) return new Curve([], [(float)fallback]);

            long[] times = curve.Node.Nodes
                .FirstOrDefault(n => n.Name == "KeyTime")?.Properties[0] as long[] ?? [];

            float[] values = curve.Node.Nodes
                .FirstOrDefault(n => n.Name == "KeyValueFloat")?.Properties[0] as float[] ?? [];

            // A curve whose arrays disagree is malformed; take what both cover
            // rather than reading off the end of one of them.
            int usable = Math.Min(times.Length, values.Length);

            return usable == 0
                ? new Curve([], [(float)fallback])
                : new Curve(times[..usable], values[..usable]);
        }

        public float At(float seconds, float fallback)
        {
            if (_times.Length == 0) return _values.Length > 0 ? _values[0] : fallback;
            if (_times.Length == 1) return _values[0];

            long time = (long)MathF.Round(seconds * FbxAnimationWriter.TimeUnitsPerSecond);

            if (time <= _times[0]) return _values[0];
            if (time >= _times[^1]) return _values[^1];

            int high = Array.BinarySearch(_times, time);
            if (high >= 0) return _values[high];

            high = ~high;
            int low = high - 1;

            long span = _times[high] - _times[low];
            if (span <= 0) return _values[low];

            float t = (float)((double)(time - _times[low]) / span);
            return _values[low] + (_values[high] - _values[low]) * t;
        }
    }
}
