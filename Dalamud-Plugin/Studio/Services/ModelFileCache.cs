using System;
using System.Collections.Generic;
using System.IO;

namespace InstantEdit.Studio.Services;

/// <summary>What the editor needs to know about a local .mdl: the materials its mesh parts reference.</summary>
internal sealed class ModelFileFacts
{
    public IReadOnlyList<string> Materials { get; init; } = Array.Empty<string>();

    /// <summary>Why the file could not be read, or null when it was.</summary>
    public string? Error { get; init; }

    public bool Ok => Error == null;

    public static ModelFileFacts Failed(string error) => new() { Error = error };
}

/// <summary>
/// Reads the material list out of local .mdl files for the editor and the validator, both of which
/// ask on every frame they draw. Cached per path against the file's timestamp and length, so a file
/// re-exported from Blender while the window is open is picked up without the window re-reading
/// megabytes each frame. Render thread only — the build reads its own copies on the worker.
/// </summary>
internal sealed class ModelFileCache
{
    private readonly Dictionary<string, (DateTime Stamp, long Length, ModelFileFacts Facts)> _cache
        = new(StringComparer.OrdinalIgnoreCase);

    public ModelFileFacts Read(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return ModelFileFacts.Failed("no file set");

        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists)
                return ModelFileFacts.Failed("file not found");
        }
        catch (Exception ex)
        {
            return ModelFileFacts.Failed(ex.Message);
        }

        if (_cache.TryGetValue(path, out var cached) && cached.Stamp == info.LastWriteTimeUtc && cached.Length == info.Length)
            return cached.Facts;

        ModelFileFacts facts;
        try
        {
            facts = new ModelFileFacts { Materials = ModelMaterialRewriter.ReadMaterials(File.ReadAllBytes(path)) };
        }
        catch (Exception ex)
        {
            facts = ModelFileFacts.Failed(ex.Message);
        }

        _cache[path] = (info.LastWriteTimeUtc, info.Length, facts);
        return facts;
    }
}
