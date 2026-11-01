using System.Buffers.Binary;
using System.Collections.Concurrent;
using InstantEdit.Models;
using InstantEdit.Services.Previews;
using InstantEdit.Services.TextureCompression;

namespace InstantEdit.Services;

/// <summary> What one file costs: bytes in video memory and, for a model, its LOD 0 triangles. </summary>
internal readonly record struct FileCost(long Bytes, long Triangles);

/// <summary> What a character's modded textures and models cost together; <see cref="Unreadable"/> files are left out of the sums. </summary>
internal readonly record struct ResourceCostTotals(long TextureBytes, long ModelBytes, long Triangles, int Textures, int Models, int Unreadable)
{
    public long Vram => TextureBytes + ModelBytes;
}

/// <summary>
/// Totals a character's modded files the way sync plugins do: every texture and model a mod
/// supplies, once each, including gear other gear hides, since the game keeps it loaded.
/// Textures cost their mip chain, models their vertex and index buffers. Dalamud-free.
/// </summary>
internal static class ResourceCost
{
    private const int ModelHeaderSize = 68;
    private const int MaxModelFront = 64 * 1024 * 1024;

    public static ResourceCostTotals Measure(IEnumerable<ResourceNode> roots, Func<string, FileCost?> read)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<ResourceNode>(roots);
        long textureBytes = 0, modelBytes = 0, triangles = 0;
        int textures = 0, models = 0, unreadable = 0;
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            foreach (var child in node.Children)
                pending.Push(child);
            if (node.SourceState != ResourceSourceState.LoadedMod || !Path.IsPathRooted(node.ActualPath))
                continue;
            var texture = node.ActualPath.EndsWith(".tex", StringComparison.OrdinalIgnoreCase);
            if (!texture && !node.ActualPath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!seen.Add(node.ActualPath))
                continue;
            if (read(node.ActualPath) is not { } cost)
            {
                unreadable++;
                continue;
            }
            if (texture)
            {
                textureBytes += cost.Bytes;
                textures++;
            }
            else
            {
                modelBytes += cost.Bytes;
                triangles += cost.Triangles;
                models++;
            }
        }
        return new ResourceCostTotals(textureBytes, modelBytes, triangles, textures, models, unreadable);
    }

    /// <summary> A texture's video memory from its header, or null for an unknown format. </summary>
    public static FileCost? ReadTexture(ReadOnlySpan<byte> head)
        => TextureCost.Read(head)?.Vram is { } bytes ? new FileCost(bytes, 0) : null;

    /// <summary> A model's buffers and LOD 0 triangles from the front of its file (everything before the vertex data), or null when it isn't a readable model. </summary>
    public static FileCost? ReadModel(byte[] front)
    {
        if (front.Length < ModelHeaderSize)
            return null;
        var info = ModelInfoReader.Read(front);
        if (info.Version is not (ModelInfo.V5 or ModelInfo.V6) || info.Partial && info.Lod0Indices == 0)
            return null;
        // The header's vertex and index buffer sizes, three LODs each.
        long buffers = 0;
        for (var at = 40; at < 64; at += 4)
            buffers += BinaryPrimitives.ReadUInt32LittleEndian(front.AsSpan(at, 4));
        return new FileCost(buffers, info.Lod0Triangles);
    }

    /// <summary> Reads a mod's .tex or .mdl from disk, or null when it can't be read. </summary>
    public static FileCost? ReadFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (path.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
            {
                var head = new byte[TextureCost.HeaderSize];
                return stream.ReadAtLeast(head, head.Length, false) == head.Length ? ReadTexture(head) : null;
            }

            var header = new byte[ModelHeaderSize];
            if (stream.ReadAtLeast(header, header.Length, false) < header.Length)
                return null;
            // The tables end after the header's stack and runtime sizes, where the first LOD's vertex data starts.
            var tables = ModelHeaderSize + (long)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4)) + BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));
            var vertexData = Math.Max(tables, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16, 4)));
            var length = (int)Math.Clamp(Math.Min(vertexData, stream.Length), ModelHeaderSize, MaxModelFront);
            var front = new byte[length];
            header.CopyTo(front, 0);
            return stream.ReadAtLeast(front.AsSpan(ModelHeaderSize), length - ModelHeaderSize, false) == length - ModelHeaderSize ? ReadModel(front) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary> <see cref="ResourceCost.ReadFile"/> remembered per file until its size or write time changes. Thread-safe. </summary>
internal sealed class ResourceCostCache
{
    private readonly ConcurrentDictionary<string, (long Length, DateTime Written, FileCost? Cost)> _files = new(StringComparer.OrdinalIgnoreCase);

    public FileCost? Read(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
                return null;
            if (_files.TryGetValue(path, out var known) && known.Length == file.Length && known.Written == file.LastWriteTimeUtc)
                return known.Cost;
            if (_files.Count >= 8192)
                _files.Clear();
            var cost = ResourceCost.ReadFile(path);
            _files[path] = (file.Length, file.LastWriteTimeUtc, cost);
            return cost;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
