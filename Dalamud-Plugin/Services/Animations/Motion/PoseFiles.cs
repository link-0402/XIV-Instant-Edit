using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace InstantEdit.Services.Animations.Motion;

/// <summary>A pose a posing tool saved: each bone's transform in the character's model space, by name.</summary>
/// <param name="Tool">The tool that wrote it (Anamnesis, Ktisis, Brio), or empty when the file doesn't say.</param>
internal sealed record PoseFile(string Tool, ImmutableDictionary<string, MotionTransform> Bones);

/// <summary>
/// Reads .pose files as Anamnesis, Ktisis and Brio write them: JSON whose Bones object holds each
/// bone's Position, Rotation and Scale as "x, y, z" or "x, y, z, w" strings in the character's model
/// space. Weapons, props and the character's place in the world are left out. A bone whose values
/// can't be read is skipped; rotations are normalised, and one of no length counts as none.
/// </summary>
internal static class PoseFiles
{
    /// <summary>Larger files are refused: a pose with its preview picture stays far below this.</summary>
    public const int MaximumBytes = 32 * 1024 * 1024;

    public static PoseFile Parse(byte[] bytes)
    {
        if (bytes.Length > MaximumBytes)
            throw new InvalidDataException("The file is too large for a pose.");
        // Some tools write a byte order mark, which the JSON reader doesn't skip.
        var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes.AsMemory(start),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException)
        {
            throw new InvalidDataException("The file is not a pose file.");
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Bones", out var bones) || bones.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("The file has no bones. Is it a pose file?");
            var result = ImmutableDictionary.CreateBuilder<string, MotionTransform>(StringComparer.Ordinal);
            foreach (var bone in bones.EnumerateObject())
                if (!string.IsNullOrWhiteSpace(bone.Name) && Bone(bone.Value) is { } value)
                    result[bone.Name] = value;
            if (result.Count == 0)
                throw new InvalidDataException("The file has no bones. Is it a pose file?");
            var tool = root.TryGetProperty("TypeName", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString() ?? "" : "";
            if (tool.EndsWith(" Pose", StringComparison.OrdinalIgnoreCase))
                tool = tool[..^5];
            return new PoseFile(tool.Trim(), result.ToImmutable());
        }
    }

    /// <summary>One bone's transform; null when its position or rotation can't be read.</summary>
    private static MotionTransform? Bone(JsonElement bone)
    {
        if (bone.ValueKind != JsonValueKind.Object || Values(bone, "Position", 3) is not { } position || Values(bone, "Rotation", 4) is not { } rotation)
            return null;
        var scale = Values(bone, "Scale", 3) is { } read ? new Vector3(read[0], read[1], read[2]) : Vector3.One;
        var turn = new Quaternion(rotation[0], rotation[1], rotation[2], rotation[3]);
        turn = turn.LengthSquared() < 1e-12f ? Quaternion.Identity : Quaternion.Normalize(turn);
        return new MotionTransform(new Vector3(position[0], position[1], position[2]), turn, scale);
    }

    /// <summary>
    /// A value's <paramref name="count"/> numbers: written as "x, y, z" (every tool here), or as an object of
    /// X, Y, Z and W. Null when missing, short or not finite.
    /// </summary>
    private static float[]? Values(JsonElement bone, string name, int count)
    {
        if (!bone.TryGetProperty(name, out var value))
            return null;
        var result = new float[count];
        if (value.ValueKind == JsonValueKind.String)
        {
            var parts = (value.GetString() ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < count)
                return null;
            for (var i = 0; i < count; i++)
                if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out result[i]) || !float.IsFinite(result[i]))
                    return null;
            return result;
        }
        if (value.ValueKind != JsonValueKind.Object)
            return null;
        string[] axes = ["X", "Y", "Z", "W"];
        for (var i = 0; i < count; i++)
            if (!value.TryGetProperty(axes[i], out var axis) || axis.ValueKind != JsonValueKind.Number || !axis.TryGetSingle(out result[i]) ||
                !float.IsFinite(result[i]))
                return null;
        return result;
    }

    // ── Writing ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A .pose file of <paramref name="bones"/> in model space, with the header Anamnesis writes (most of the poses
    /// people share have it, and every tool here reads it): the character's place left at the origin, no version.
    /// Numbers are written the shortest way that reads back the same, whatever the system's language.
    /// </summary>
    public static byte[] Write(IEnumerable<(string Name, MotionTransform Model)> bones)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteString("FileExtension", PoseLibrary.Extension);
            json.WriteString("TypeName", "Anamnesis Pose");
            json.WriteString("Position", "0, 0, 0");
            json.WriteString("Rotation", "0, 0, 0, 1");
            json.WriteString("Scale", "1, 1, 1");
            json.WriteStartObject("Bones");
            var count = 0;
            foreach (var (name, model) in bones)
            {
                json.WriteStartObject(name);
                json.WriteString("Position", Text(model.Position.X, model.Position.Y, model.Position.Z));
                var turn = model.Rotation.LengthSquared() < 1e-12f ? Quaternion.Identity : Quaternion.Normalize(model.Rotation);
                json.WriteString("Rotation", Text(turn.X, turn.Y, turn.Z, turn.W));
                json.WriteString("Scale", Text(model.Scale.X, model.Scale.Y, model.Scale.Z));
                json.WriteEndObject();
                count++;
            }
            if (count == 0)
                throw new InvalidDataException("The pose has no bones.");
            json.WriteEndObject();
            json.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static string Text(params float[] values) => string.Join(", ", values.Select(value => (value == 0 ? 0f : value).ToString(CultureInfo.InvariantCulture)));

    private static readonly string[] DeviceNames = ["CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        .. Enumerable.Range(1, 9).SelectMany(i => new[] { $"COM{i}", $"LPT{i}" })];

    /// <summary>Why <paramref name="name"/> can't name a pose file, or null when it can. Spaces around it are dropped.</summary>
    public static string? NameProblem(string name)
    {
        name = name.Trim();
        if (name.Length == 0)
            return "Type a name.";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return "A name can't have \\ / : * ? \" < > |.";
        if (name.EndsWith('.'))
            return "A name can't end in a dot.";
        if (DeviceNames.Contains(name.Split('.')[0].TrimEnd(), StringComparer.OrdinalIgnoreCase))
            return "Windows keeps that name.";
        return null;
    }

    /// <summary>Where a pose named <paramref name="name"/> goes in <paramref name="folder"/>.</summary>
    public static string PathFor(string folder, string name) => Path.Combine(folder, name.Trim() + PoseLibrary.Extension);

    /// <summary>
    /// Writes <paramref name="bytes"/> to <paramref name="path"/> whole: to a .tmp file beside it first (the browser
    /// lists only .pose files), then moved over it, so a pose is never left half written. The .tmp file goes again
    /// when anything fails.
    /// </summary>
    public static void Save(string path, byte[] bytes)
    {
        var folder = Path.GetDirectoryName(path) ?? throw new IOException("The pose has no folder.");
        var temp = Path.Combine(folder, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temp, path, true);
        }
        finally
        {
            try { File.Delete(temp); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
