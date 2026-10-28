using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace InstantEdit.Studio.Services.Stash;

/// <summary>One model Blender saved into a stash folder.</summary>
/// <param name="File">Relative to the stash folder, with '/'.</param>
/// <param name="Sources">The files it was made from, as full paths, in the order Blender listed them.</param>
internal sealed record BlenderExport(string File, IReadOnlyList<string> Sources);

/// <summary>
/// blender-exports.json: the models Blender's Save to Stash wrote into a stash folder (under
/// Blender/) and the files each was made from. Blender writes it; the stash only reads it.
/// </summary>
internal static class BlenderExports
{
    public const string FileName = "blender-exports.json";

    /// <summary>The subfolder Blender saves its models into.</summary>
    public const string FolderName = "Blender";

    private const string Schema = "instant-edit.blender-exports";
    private const int Version = 1;

    /// <summary>The models the folder's sidecar lists; none when it has none, it can't be read, or it is of another version.</summary>
    public static IReadOnlyList<BlenderExport> Read(string folder)
    {
        try
        {
            var path = Path.Combine(folder, FileName);
            if (!File.Exists(path))
                return Array.Empty<BlenderExport>();
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.String || schema.GetString() != Schema
                || !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var number) || number != Version
                || !root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
                return Array.Empty<BlenderExport>();

            var exports = new List<BlenderExport>();
            foreach (var model in models.EnumerateArray())
            {
                if (model.ValueKind != JsonValueKind.Object || !model.TryGetProperty("file", out var file)
                    || file.ValueKind != JsonValueKind.String || Relative(file.GetString()!) is not { } relative)
                    continue;
                var sources = model.TryGetProperty("sources", out var listed) && listed.ValueKind == JsonValueKind.Array
                    ? listed.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.String).Select(s => s.GetString()!).Where(s => s.Length > 0).ToList()
                    : new List<string>();
                exports.Add(new BlenderExport(relative, sources));
            }
            return exports;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            return Array.Empty<BlenderExport>();
        }
    }

    /// <summary>Lists each model of the folder's sidecar that is there and isn't listed yet, as one from Blender.</summary>
    public static void Merge(StashManifest manifest, string folder)
    {
        foreach (var export in Read(folder))
        {
            if (manifest.Entries.Any(e => string.Equals(e.Path, export.File, StringComparison.OrdinalIgnoreCase))
                || !File.Exists(Path.Combine(folder, export.File)))
                continue;
            manifest.Entries.Add(new StashEntry { Path = export.File, Kind = StashKind.Model, Detail = "from Blender" });
        }
    }

    /// <summary>
    /// Whether <paramref name="file"/> is one Blender wrote into <paramref name="folder"/>: the sidecar,
    /// its .tmp, or anything under Blender/. A re-extraction counts them as the earlier run's, and
    /// <see cref="ExtractionFolder.Finish"/> moves the sidecar and Blender/ into the new run.
    /// </summary>
    public static bool IsBlenderFile(string folder, string file)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(file);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return false;
        var relative = full[root.Length..];
        return relative.Equals(FileName, StringComparison.OrdinalIgnoreCase)
            || relative.Equals(FileName + ".tmp", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith(FolderName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A sidecar's file with '/'; null for one that is rooted or climbs out of the folder.</summary>
    private static string? Relative(string file)
    {
        var path = file.Trim().Replace('\\', '/');
        return path.Length == 0 || path.StartsWith('/') || path.Contains(':') || path.Split('/').Any(s => s.Length == 0 || s is "." or "..")
            ? null
            : path;
    }
}
