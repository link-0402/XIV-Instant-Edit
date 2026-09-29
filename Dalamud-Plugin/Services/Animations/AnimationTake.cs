using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using InstantEdit.Models;

namespace InstantEdit.Services.Animations;

/// <summary>A bone of a take: its game name, the index of its parent in the take (-1 for roots) and its reference pose.</summary>
internal sealed record AnimationTakeBone(string Name, int Parent, BoneTransform Reference);

/// <summary>
/// Bone transforms over time, ready for Blender. Every frame holds each bone's transform relative
/// to its parent, in FFXIV's own Y-up space, as <see cref="Stride"/> floats: translation, rotation
/// x/y/z/w and scale. A take is either an animation file sampled on the skeleton it was made for,
/// or a recording of a character's live skeleton.
/// </summary>
internal sealed class AnimationTake
{
    public const string AnimationKind = "animation";
    public const string RecordingKind = "recording";
    public const int Stride = 10;
    public const int MaximumBones = 4096;
    private readonly double[] times;
    private readonly float[] samples;

    public AnimationTake(string kind, string name, ImmutableArray<AnimationTakeBone> bones, double[] times, float[] samples,
        bool loop = false, IReadOnlyDictionary<string, string>? source = null)
    {
        if (kind is not (AnimationKind or RecordingKind)) throw new ArgumentException("Unknown take kind.", nameof(kind));
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("A take needs a name.");
        if (bones.IsDefault || bones.Length is < 1 or > MaximumBones) throw new InvalidDataException("A take needs between 1 and 4096 bones.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < bones.Length; i++)
        {
            var bone = bones[i];
            if (string.IsNullOrEmpty(bone.Name) || !names.Add(bone.Name) || bone.Parent < -1 || bone.Parent >= i)
                throw new InvalidDataException("The take's bone names or hierarchy are ambiguous.");
        }
        if (times.Length == 0 || times.Any(t => !double.IsFinite(t)) || times.Zip(times.Skip(1)).Any(p => p.Second <= p.First))
            throw new InvalidDataException("The take's frame times must be finite and increasing.");
        if (samples.LongLength != (long)times.Length * bones.Length * Stride)
            throw new InvalidDataException("The take's samples do not match its frames and bones.");
        for (var i = 0; i < samples.Length; i += Stride)
            Normalize(samples.AsSpan(i, Stride));
        Kind = kind; Name = name.Trim(); Bones = bones; this.times = times; this.samples = samples; Loop = loop;
        Source = source ?? ImmutableDictionary<string, string>.Empty;
    }

    public string Kind { get; }
    public string Name { get; }
    public ImmutableArray<AnimationTakeBone> Bones { get; }
    public IReadOnlyList<double> Times => times;
    public ReadOnlySpan<float> Samples => samples;
    public bool Loop { get; }
    public IReadOnlyDictionary<string, string> Source { get; }
    public int FrameCount => times.Length;
    public double Duration => times[^1] - times[0];

    /// <summary>Writes a transform into a sample slot.</summary>
    public static void Write(BoneTransform value, Span<float> slot)
    {
        slot[0] = value.Position.X; slot[1] = value.Position.Y; slot[2] = value.Position.Z;
        slot[3] = value.Rotation.X; slot[4] = value.Rotation.Y; slot[5] = value.Rotation.Z; slot[6] = value.Rotation.W;
        slot[7] = value.Scale.X; slot[8] = value.Scale.Y; slot[9] = value.Scale.Z;
    }

    public static BoneTransform Read(ReadOnlySpan<float> slot)
        => new(new Vector3(slot[0], slot[1], slot[2]), new Quaternion(slot[3], slot[4], slot[5], slot[6]), new Vector3(slot[7], slot[8], slot[9]));

    /// <summary>
    /// Rejects values that are not transforms and re-normalizes rotations, which the game keeps
    /// only approximately unit length.
    /// </summary>
    private static void Normalize(Span<float> slot)
    {
        foreach (var value in slot)
            if (!float.IsFinite(value)) throw new InvalidDataException("The take contains a transform that is not finite.");
        var length = MathF.Sqrt(slot[3] * slot[3] + slot[4] * slot[4] + slot[5] * slot[5] + slot[6] * slot[6]);
        if (length is < 0.5f or > 1.5f) throw new InvalidDataException("The take contains a rotation that is not a unit quaternion.");
        for (var i = 3; i < 7; i++) slot[i] /= length;
    }
}

/// <summary>
/// The binary form Blender's <c>/animation</c> endpoint accepts: the magic <c>XIEA</c>, the format
/// version and the header length as little-endian 32-bit integers, a UTF-8 JSON header, zero
/// padding to a four-byte boundary, then the samples as little-endian 32-bit floats, frame by frame.
/// </summary>
internal static class AnimationTakeFormat
{
    public const string Schema = "instant-edit.animation";
    public const int Version = 1;
    // Blender's add-on refuses larger bodies.
    public const long MaximumBytes = 192L * 1024 * 1024;
    private static ReadOnlySpan<byte> Magic => "XIEA"u8;

    private sealed record Bone(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("parent")] int Parent,
        [property: JsonPropertyName("reference")] float[] Reference);

    private sealed record Header(
        [property: JsonPropertyName("schema")] string Schema,
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("pluginVersion")] string PluginVersion,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("targetObject")] string TargetObject,
        [property: JsonPropertyName("keyScale")] bool KeyScale,
        [property: JsonPropertyName("loop")] bool Loop,
        [property: JsonPropertyName("source")] IReadOnlyDictionary<string, string> Source,
        [property: JsonPropertyName("bones")] Bone[] Bones,
        [property: JsonPropertyName("times")] IReadOnlyList<double> Times,
        [property: JsonPropertyName("targetCharacter"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? TargetCharacter = null);

    /// <param name="targetCharacter">
    /// The character send whose armature the take belongs on (see CharacterImportEntry): Blender
    /// keys it there whatever <paramref name="targetObject"/> names. Null for the named armature.
    /// </param>
    public static byte[] Write(AnimationTake take, string targetObject, bool keyScale, string pluginVersion,
        string? targetCharacter = null)
    {
        var bones = take.Bones.Select(b =>
        {
            var reference = new float[AnimationTake.Stride];
            AnimationTake.Write(b.Reference, reference);
            if (reference.Any(v => !float.IsFinite(v))) throw new InvalidDataException($"Bone {b.Name} has a reference pose that is not finite.");
            return new Bone(b.Name, b.Parent, reference);
        }).ToArray();
        var header = JsonSerializer.SerializeToUtf8Bytes(new Header(Schema, Version, pluginVersion, take.Kind, take.Name,
            targetObject.Trim(), keyScale, take.Loop, take.Source, bones, take.Times, targetCharacter));
        var start = 12 + header.Length;
        var padding = (4 - start % 4) % 4;
        var size = (long)start + padding + (long)take.Samples.Length * sizeof(float);
        if (size > MaximumBytes)
            throw new InvalidDataException($"The take needs {size / (1024 * 1024)} MiB, more than Blender accepts. Record a shorter take.");
        var bytes = new byte[size];
        Magic.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), Version);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), header.Length);
        header.CopyTo(bytes, 12);
        var data = bytes.AsSpan(start + padding);
        if (BitConverter.IsLittleEndian)
            MemoryMarshal.AsBytes(take.Samples).CopyTo(data);
        else
            for (var i = 0; i < take.Samples.Length; i++)
                BinaryPrimitives.WriteSingleLittleEndian(data[(i * sizeof(float))..], take.Samples[i]);
        return bytes;
    }
}

/// <summary>Turns a recording's uneven frame times into samples at a fixed rate.</summary>
internal static class AnimationTakeTiming
{
    /// <summary>
    /// Samples at <paramref name="rate"/> per second over the recording, interpolated from the
    /// recorded frames: translation and scale linearly, rotation along the shorter arc. Times of
    /// the result start at zero.
    /// </summary>
    public static (double[] Times, float[] Samples) Resample(IReadOnlyList<double> times, IReadOnlyList<float[]> frames,
        int boneCount, double rate)
    {
        if (times.Count == 0 || times.Count != frames.Count) throw new InvalidDataException("A recording needs at least one frame.");
        if (!double.IsFinite(rate) || rate <= 0) throw new ArgumentOutOfRangeException(nameof(rate));
        var width = boneCount * AnimationTake.Stride;
        if (frames.Any(f => f.Length != width)) throw new InvalidDataException("A recorded frame does not match the skeleton.");
        for (var i = 1; i < times.Count; i++)
            if (!(times[i] > times[i - 1])) throw new InvalidDataException("Recorded frame times must increase.");
        var count = (int)Math.Floor((times[^1] - times[0]) * rate + 1e-6) + 1;
        var resultTimes = new double[count];
        var samples = new float[(long)count * width];
        var segment = 0;
        for (var k = 0; k < count; k++)
        {
            resultTimes[k] = k / rate;
            var wanted = times[0] + resultTimes[k];
            while (segment < times.Count - 2 && times[segment + 1] <= wanted) segment++;
            var next = Math.Min(segment + 1, times.Count - 1);
            var span = times[next] - times[segment];
            var alpha = span > 0 ? (float)Math.Clamp((wanted - times[segment]) / span, 0, 1) : 0f;
            Interpolate(frames[segment], frames[next], alpha, samples.AsSpan(k * width, width));
        }
        return (resultTimes, samples);
    }

    internal static void Interpolate(ReadOnlySpan<float> from, ReadOnlySpan<float> to, float alpha, Span<float> result)
    {
        for (var bone = 0; bone < result.Length; bone += AnimationTake.Stride)
        {
            var a = from.Slice(bone, AnimationTake.Stride);
            var b = to.Slice(bone, AnimationTake.Stride);
            var r = result.Slice(bone, AnimationTake.Stride);
            for (var i = 0; i < 3; i++) r[i] = a[i] + (b[i] - a[i]) * alpha;
            for (var i = 7; i < 10; i++) r[i] = a[i] + (b[i] - a[i]) * alpha;
            var sign = a[3] * b[3] + a[4] * b[4] + a[5] * b[5] + a[6] * b[6] < 0 ? -1f : 1f;
            var length = 0f;
            for (var i = 3; i < 7; i++)
            {
                r[i] = a[i] + (sign * b[i] - a[i]) * alpha;
                length += r[i] * r[i];
            }
            length = MathF.Sqrt(length);
            if (length > 1e-6f)
                for (var i = 3; i < 7; i++) r[i] /= length;
            else
                a.Slice(3, 4).CopyTo(r.Slice(3, 4));
        }
    }
}
