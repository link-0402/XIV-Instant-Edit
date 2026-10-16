using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using InstantEdit.Models;
using InstantEdit.Services.Animations.Motion;

namespace InstantEdit.Services.Animations;

/// <summary>
/// What the Timeline asks Blender for when it reads a motion back: samples per second, the clip
/// (so Blender's reply can say whether its action was made from it) and the clip's skeleton.
/// </summary>
internal sealed record AnimationReadRequest(double Rate, string GamePath, string ClipName, SkeletonDescription Skeleton);

/// <summary>A bone Blender read: its index in the request's skeleton, and the index of the nearest ancestor it read too (-1 for none).</summary>
internal sealed record AnimationReadBone(int Index, int Ancestor);

/// <summary>
/// The motion Blender read from its armature: for each read bone and each time, the bone's game
/// transform relative to <see cref="AnimationReadBone.Ancestor"/> (model space without one), in
/// the take layout. Times are uniform from 0 to <see cref="Duration"/>. <see cref="ScaleIgnored"/>: the
/// armature has a scale the motion was sent without, which the plugin leaves out.
/// </summary>
internal sealed record AnimationRead(string Action, string Armature, int FrameStart, int FrameEnd, double SceneRate, float Duration,
    bool KeyedScale, string Evaluated, IReadOnlyDictionary<string, string> Source, ImmutableArray<AnimationReadBone> Bones,
    double[] Times, float[] Samples, int MissingBoneCount, ImmutableArray<string> MissingBones, bool ScaleIgnored = false)
{
    public MotionTransform Sample(int time, int bone)
    {
        var s = Samples.AsSpan((time * Bones.Length + bone) * AnimationTake.Stride, AnimationTake.Stride);
        return new(new Vector3(s[0], s[1], s[2]), new Quaternion(s[3], s[4], s[5], s[6]), new Vector3(s[7], s[8], s[9]));
    }
}

/// <summary>
/// The read request the plugin posts to Blender's <c>/animation/read</c> (JSON), and the reply
/// Blender sends back: the <c>XIEA</c> container of <see cref="AnimationTakeFormat"/> with the
/// schema <see cref="Schema"/>.
/// </summary>
internal static class AnimationReadFormat
{
    public const string RequestSchema = "instant-edit.animation-read-request";
    public const string Schema = "instant-edit.animation-read";
    public const int Version = 1;
    public const long MaximumBytes = 192L * 1024 * 1024;
    private const string Unreadable = "Blender's reply can't be read. Update the add-on and restart Blender.";
    private const string Unfit = "Blender's reply doesn't fit this animation.";
    private static ReadOnlySpan<byte> Magic => "XIEA"u8;

    private sealed record Bone(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("parent")] int Parent,
        [property: JsonPropertyName("reference")] float[] Reference);

    private sealed record Clip(
        [property: JsonPropertyName("gamePath")] string GamePath,
        [property: JsonPropertyName("clip")] string Name);

    private sealed record Request(
        [property: JsonPropertyName("schema")] string Schema,
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("pluginVersion")] string PluginVersion,
        [property: JsonPropertyName("targetObject")] string TargetObject,
        [property: JsonPropertyName("rate")] double Rate,
        [property: JsonPropertyName("clip")] Clip Clip,
        [property: JsonPropertyName("bones")] Bone[] Bones,
        [property: JsonPropertyName("targetCharacter"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? TargetCharacter = null);

    /// <summary>The JSON body of a read of <paramref name="targetObject"/>, or of the armature of the character send <paramref name="targetCharacter"/>.</summary>
    public static byte[] WriteRequest(AnimationReadRequest request, string targetObject, string? targetCharacter, string pluginVersion)
    {
        var bones = request.Skeleton.Bones.Select(b =>
        {
            var reference = new float[AnimationTake.Stride];
            AnimationTake.Write(b.Reference, reference);
            if (reference.Any(v => !float.IsFinite(v))) throw new InvalidDataException($"Bone {b.Name} has a reference pose that is not finite.");
            return new Bone(b.Name, b.Parent, reference);
        }).ToArray();
        return JsonSerializer.SerializeToUtf8Bytes(new Request(RequestSchema, Version, pluginVersion, targetObject.Trim(), request.Rate,
            new Clip(request.GamePath, request.ClipName), bones, targetCharacter));
    }

    /// <summary>Reads Blender's reply to <paramref name="request"/>; throws <see cref="InvalidDataException"/> when it doesn't fit.</summary>
    public static AnimationRead Parse(ReadOnlySpan<byte> body, AnimationReadRequest request)
    {
        if (body.Length > MaximumBytes || body.Length < 12 || !body[..4].SequenceEqual(Magic) ||
            BinaryPrimitives.ReadInt32LittleEndian(body[4..]) != Version)
            throw new InvalidDataException(Unreadable);
        var headerLength = BinaryPrimitives.ReadInt32LittleEndian(body[8..]);
        if (headerLength <= 0 || headerLength > body.Length - 12)
            throw new InvalidDataException(Unreadable);
        JsonElement header;
        try
        {
            using var document = JsonDocument.Parse(body.Slice(12, headerLength).ToArray());
            header = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new InvalidDataException(Unreadable);
        }
        if (header.ValueKind != JsonValueKind.Object || Text(header, "schema") != Schema || Number(header, "version") != Version)
            throw new InvalidDataException(Unreadable);

        var skeleton = request.Skeleton.Bones;
        var bones = ImmutableArray.CreateBuilder<AnimationReadBone>();
        if (!header.TryGetProperty("bones", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
            throw new InvalidDataException(Unfit);
        foreach (var item in list.EnumerateArray())
        {
            var index = item.ValueKind == JsonValueKind.Object ? Number(item, "index") : null;
            var ancestor = item.ValueKind == JsonValueKind.Object ? Number(item, "ancestor") : null;
            if (index is not { } i || ancestor is not { } a || i < 0 || i >= skeleton.Length ||
                bones.Count > 0 && i <= bones[^1].Index || a != -1 && !IsAncestor(skeleton, a, i))
                throw new InvalidDataException(Unfit);
            bones.Add(new AnimationReadBone(i, a));
        }

        if (!header.TryGetProperty("times", out var timeList) || timeList.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException(Unfit);
        var times = timeList.EnumerateArray().Select(t => t.ValueKind == JsonValueKind.Number ? t.GetDouble() : double.NaN).ToArray();
        var duration = header.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : double.NaN;
        // MotionClip samples on a uniform grid from 0 to its duration, so every time must sit on that grid.
        if (times.Length < 2 || times[0] != 0 || !double.IsFinite(duration) || duration <= 0 ||
            times.Any(t => !double.IsFinite(t)) || times.Zip(times.Skip(1)).Any(p => p.Second <= p.First) ||
            times.Select((t, i) => Math.Abs(t - duration * i / (times.Length - 1))).Any(off => off > 1e-6 * Math.Max(1, duration)))
            throw new InvalidDataException(Unfit);

        var start = 12 + headerLength;
        start += (4 - start % 4) % 4;
        var count = (long)times.Length * bones.Count * AnimationTake.Stride;
        if (body.Length - start != count * sizeof(float))
            throw new InvalidDataException(Unfit);
        var samples = new float[count];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = BinaryPrimitives.ReadSingleLittleEndian(body[(start + i * sizeof(float))..]);
        for (var i = 0; i < samples.Length; i += AnimationTake.Stride)
            Normalize(samples.AsSpan(i, AnimationTake.Stride));

        var source = new Dictionary<string, string>(StringComparer.Ordinal);
        if (header.TryGetProperty("source", out var facts) && facts.ValueKind == JsonValueKind.Object)
            foreach (var fact in facts.EnumerateObject())
                if (fact.Value.ValueKind == JsonValueKind.String)
                    source[fact.Name] = fact.Value.GetString()!;
        var missing = header.TryGetProperty("missingBones", out var names) && names.ValueKind == JsonValueKind.Array
            ? names.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.String).Select(n => n.GetString()!).ToImmutableArray()
            : [];
        return new AnimationRead(Text(header, "action") ?? "", Text(header, "armature") ?? "", Number(header, "frameStart") ?? 0,
            Number(header, "frameEnd") ?? 0, header.TryGetProperty("sceneRate", out var rate) && rate.ValueKind == JsonValueKind.Number ? rate.GetDouble() : 0,
            (float)duration, header.TryGetProperty("keyedScale", out var scale) && scale.ValueKind == JsonValueKind.True,
            Text(header, "evaluated") ?? "", source, bones.ToImmutable(), times, samples, Number(header, "missingBoneCount") ?? missing.Length, missing,
            header.TryGetProperty("scaleIgnored", out var ignored) && ignored.ValueKind == JsonValueKind.True);
    }

    private static bool IsAncestor(ImmutableArray<SkeletonBone> skeleton, int ancestor, int bone)
    {
        for (var parent = (int)skeleton[bone].Parent; parent >= 0; parent = skeleton[parent].Parent)
            if (parent == ancestor)
                return true;
        return false;
    }

    /// <summary>Refuses values that are not transforms and re-normalizes rotations, as <see cref="AnimationTake"/> does.</summary>
    private static void Normalize(Span<float> slot)
    {
        foreach (var value in slot)
            if (!float.IsFinite(value)) throw new InvalidDataException(Unfit);
        var length = MathF.Sqrt(slot[3] * slot[3] + slot[4] * slot[4] + slot[5] * slot[5] + slot[6] * slot[6]);
        if (length is < 0.5f or > 1.5f) throw new InvalidDataException(Unfit);
        for (var i = 3; i < 7; i++) slot[i] /= length;
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
}
