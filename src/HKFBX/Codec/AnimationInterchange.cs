using System.Numerics;
using HKFBX.Model;

namespace HKFBX.Codec;

/// <summary>
/// The two file formats mopper's animation commands speak.
/// </summary>
/// <remarks>
/// <c>HKANIMSC</c> is a spline animation's fields; <c>HKANIMSM</c> is the same
/// animation sampled once per frame. Both are little-endian binary, and files
/// rather than pipes: the payload is float data that has to survive bit for bit,
/// and a Windows stdout in text mode rewrites every 0x0A in it.
/// </remarks>
internal static class AnimationInterchange
{
    private const int Version = 1;

    private static void WriteMagic(BinaryWriter w, string tag)
    {
        w.Write(System.Text.Encoding.ASCII.GetBytes(tag));
        w.Write(Version);
    }

    private static void ExpectMagic(BinaryReader r, string tag)
    {
        string got = System.Text.Encoding.ASCII.GetString(r.ReadBytes(8));
        if (got != tag)
            throw new InvalidDataException($"expected a {tag} file, got '{got}'");

        int version = r.ReadInt32();
        if (version != Version)
            throw new InvalidDataException($"{tag} version {version}, expected {Version}");
    }

    public static void WriteSpline(Stream stream, SplineAnimationData a)
    {
        using var w = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true);

        WriteMagic(w, "HKANIMSC");
        w.Write(a.NumFrames);
        w.Write(a.NumBlocks);
        w.Write(a.MaxFramesPerBlock);
        w.Write(a.MaskAndQuantizationSize);
        w.Write(a.BlockDuration);
        w.Write(a.BlockInverseDuration);
        w.Write(a.FrameDuration);
        w.Write(a.Duration);
        w.Write(a.TransformTrackCount);
        w.Write(a.FloatTrackCount);

        WriteU32Array(w, a.BlockOffsets);
        WriteU32Array(w, a.FloatBlockOffsets);
        WriteU32Array(w, a.TransformOffsets);
        WriteU32Array(w, a.FloatOffsets);

        w.Write(a.Data.Length);
        w.Write(a.Data);
    }

    public static SplineAnimationData ReadSpline(Stream stream)
    {
        using var r = new BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);

        ExpectMagic(r, "HKANIMSC");

        int numFrames = r.ReadInt32();
        int numBlocks = r.ReadInt32();
        int maxFramesPerBlock = r.ReadInt32();
        int maskAndQuant = r.ReadInt32();
        float blockDuration = r.ReadSingle();
        float blockInverseDuration = r.ReadSingle();
        float frameDuration = r.ReadSingle();
        float duration = r.ReadSingle();
        int transformTracks = r.ReadInt32();
        int floatTracks = r.ReadInt32();

        uint[] blockOffsets = ReadU32Array(r);
        uint[] floatBlockOffsets = ReadU32Array(r);
        uint[] transformOffsets = ReadU32Array(r);
        uint[] floatOffsets = ReadU32Array(r);

        int dataLength = r.ReadInt32();

        return new SplineAnimationData
        {
            NumFrames = numFrames,
            NumBlocks = numBlocks,
            MaxFramesPerBlock = maxFramesPerBlock,
            MaskAndQuantizationSize = maskAndQuant,
            BlockDuration = blockDuration,
            BlockInverseDuration = blockInverseDuration,
            FrameDuration = frameDuration,
            Duration = duration,
            TransformTrackCount = transformTracks,
            FloatTrackCount = floatTracks,
            BlockOffsets = blockOffsets,
            FloatBlockOffsets = floatBlockOffsets,
            TransformOffsets = transformOffsets,
            FloatOffsets = floatOffsets,
            Data = r.ReadBytes(dataLength),
        };
    }

    public static void WriteSamples(Stream stream, SampledAnimation a)
    {
        using var w = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true);

        int floatTracks = a.FrameCount > 0 ? a.Floats.Length / a.FrameCount : 0;

        WriteMagic(w, "HKANIMSM");
        w.Write(a.FrameCount);
        w.Write(a.TrackCount);
        w.Write(floatTracks);
        w.Write(a.Duration);
        w.Write(a.FrameDuration);

        foreach (BoneTransform t in a.Transforms)
        {
            w.Write(t.Translation.X); w.Write(t.Translation.Y); w.Write(t.Translation.Z);
            w.Write(t.Rotation.X); w.Write(t.Rotation.Y); w.Write(t.Rotation.Z); w.Write(t.Rotation.W);
            w.Write(t.Scale.X); w.Write(t.Scale.Y); w.Write(t.Scale.Z);
        }

        foreach (float f in a.Floats) w.Write(f);
    }

    public static SampledAnimation ReadSamples(Stream stream)
    {
        using var r = new BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);

        ExpectMagic(r, "HKANIMSM");

        int frames = r.ReadInt32();
        int tracks = r.ReadInt32();
        int floatTracks = r.ReadInt32();
        float duration = r.ReadSingle();
        float frameDuration = r.ReadSingle();

        var transforms = new BoneTransform[frames * tracks];
        for (int i = 0; i < transforms.Length; i++)
        {
            var translation = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            var rotation = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            var scale = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            transforms[i] = new BoneTransform(translation, rotation, scale);
        }

        var floats = new float[frames * floatTracks];
        for (int i = 0; i < floats.Length; i++) floats[i] = r.ReadSingle();

        return new SampledAnimation
        {
            FrameCount = frames,
            TrackCount = tracks,
            Duration = duration,
            FrameDuration = frameDuration,
            Transforms = transforms,
            Floats = floats,
        };
    }

    private static void WriteU32Array(BinaryWriter w, IReadOnlyList<uint> values)
    {
        w.Write(values.Count);
        foreach (uint v in values) w.Write(v);
    }

    private static uint[] ReadU32Array(BinaryReader r)
    {
        int n = r.ReadInt32();
        var values = new uint[n];
        for (int i = 0; i < n; i++) values[i] = r.ReadUInt32();
        return values;
    }
}
