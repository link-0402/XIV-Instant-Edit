using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Models.Resources;

namespace InstantEdit.Studio.Services.Resources;

/// <summary>What one file says about itself: its size, what it references, and (textures) its format.</summary>
internal sealed class FileFacts
{
    public long Size { get; init; } = -1;

    /// <summary>
    /// The game paths the file references: a model's material names (as the model has them, often
    /// relative: "/mt_c0101e0001_top_a.mtrl"), a material's textures, the paths an animation,
    /// timeline or effect names.
    /// </summary>
    public IReadOnlyList<string> References { get; init; } = Array.Empty<string>();

    /// <summary>For a material: what each referenced texture is for ("Normal", "Mask" …), by position.</summary>
    public IReadOnlyList<string?> Roles { get; init; } = Array.Empty<string?>();

    /// <summary>For a model: the material each of its material slots names, in slot order (empty where none loads).</summary>
    public IReadOnlyList<string> MaterialSlots { get; init; } = Array.Empty<string>();

    public string? ShaderPackage { get; init; }

    public TexHeaderInfo? Texture { get; init; }

    /// <summary>Why the file could not be read, or null.</summary>
    public string? Error { get; init; }

    /// <summary>The file's last-write time (UTC ticks) when it was read; 0 for the game's own files.</summary>
    public long StampTicks { get; init; }
}

/// <summary>
/// Reads the files of mods for the resource tree: the expensive part of building it. Each file is
/// read once and remembered against its size and timestamp, so rebuilding the tree after an edit
/// only reads what changed. The game's own files never change while it runs and are remembered by
/// path. Textures are only ever read as far as their header.
///
/// Shared by the graph builder's worker threads, so every access is locked; the reading itself
/// happens outside the lock.
/// </summary>
internal sealed class ModFileFactsCache
{
    private const int MaxEntries = 60000;

    /// <summary>How long a file's facts are trusted without looking at its timestamp again.</summary>
    private const long TrustMs = 10_000;

    private sealed class Cached
    {
        public required DateTime Stamp;
        public required long Length;
        public required FileFacts Facts;
        public long CheckedAt;
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, Cached> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileFacts?> _vanilla = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Facts about a file on disk. A file seen in the last few seconds is not even looked at again —
    /// a big mod holds tens of thousands of files, and rebuilding the tree after an edit should not
    /// stat every one of them; <see cref="Invalidate"/> makes the next one look again.
    /// </summary>
    public FileFacts ForFile(string diskPath, ResourceKind kind)
    {
        long now = Environment.TickCount64;
        lock (_lock)
        {
            if (_files.TryGetValue(diskPath, out var recent) && now - recent.CheckedAt < TrustMs)
                return recent.Facts;
        }

        FileInfo info;
        try
        {
            info = new FileInfo(diskPath);
            if (!info.Exists)
                return new FileFacts { Error = "the file is missing from the mod folder" };
        }
        catch (Exception ex)
        {
            return new FileFacts { Error = ex.Message };
        }

        lock (_lock)
        {
            if (_files.TryGetValue(diskPath, out var cached) && cached.Stamp == info.LastWriteTimeUtc && cached.Length == info.Length)
            {
                cached.CheckedAt = now;
                return cached.Facts;
            }
        }

        var facts = Read(diskPath, kind, info.Length, info.LastWriteTimeUtc.Ticks);
        lock (_lock)
        {
            if (_files.Count > MaxEntries)
                _files.Clear();
            _files[diskPath] = new Cached { Stamp = info.LastWriteTimeUtc, Length = info.Length, Facts = facts, CheckedAt = now };
        }
        return facts;
    }

    /// <summary>Makes the next tree look at every file's timestamp again (what it read is kept unless the file changed).</summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            foreach (var cached in _files.Values)
                cached.CheckedAt = 0;
        }
    }

    /// <summary>Facts about one of the game's own files, or null when the game does not have it.</summary>
    public FileFacts? ForVanilla(string gamePath, ResourceKind kind, IVanillaFiles vanilla)
    {
        lock (_lock)
        {
            if (_vanilla.TryGetValue(gamePath, out var cached))
                return cached;
        }

        var bytes = vanilla.TryReadBytes(gamePath);
        FileFacts? facts = null;
        if (bytes != null)
        {
            try { facts = FromBytes(bytes, kind); }
            catch (Exception ex) { facts = new FileFacts { Size = bytes.Length, Error = ex.Message }; }
        }

        lock (_lock)
        {
            if (_vanilla.Count > MaxEntries)
                _vanilla.Clear();
            _vanilla[gamePath] = facts;
        }
        return facts;
    }

    public void Clear()
    {
        lock (_lock)
        {
            _files.Clear();
            _vanilla.Clear();
        }
    }

    /// <summary>Whether a kind of file references others worth reading it for.</summary>
    public static bool References(ResourceKind kind) => kind is ResourceKind.Model or ResourceKind.Material
        or ResourceKind.Animation or ResourceKind.Timeline or ResourceKind.Vfx
        or ResourceKind.Skeleton or ResourceKind.SkeletonParams or ResourceKind.Physics;

    private static FileFacts Read(string path, ResourceKind kind, long length, long stamp)
    {
        try
        {
            if (kind is ResourceKind.Texture or ResourceKind.VfxTexture or ResourceKind.Interface
                && Path.GetExtension(path).ToLowerInvariant() is ".tex" or ".atex")
            {
                return new FileFacts { Size = length, StampTicks = stamp, Texture = TexHeader.TryRead(path, out var header) ? header : null };
            }

            if (!References(kind))
                return new FileFacts { Size = length, StampTicks = stamp };

            if (length > PathStringScanner.MaxBytes)
                return new FileFacts { Size = length, StampTicks = stamp, Error = "too large to look into" };

            return FromBytes(File.ReadAllBytes(path), kind, stamp);
        }
        catch (Exception ex)
        {
            return new FileFacts { Size = length, StampTicks = stamp, Error = ex.Message };
        }
    }

    /// <summary>Reads what a file references from its bytes.</summary>
    internal static FileFacts FromBytes(byte[] bytes, ResourceKind kind, long stamp = 0)
    {
        switch (kind)
        {
            case ResourceKind.Model:
            {
                var slots = ModelMaterialRewriter.ReadMaterialsStrict(bytes);
                var names = slots
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return new FileFacts { Size = bytes.Length, StampTicks = stamp, References = names, MaterialSlots = slots };
            }

            case ResourceKind.Material:
            {
                var info = MtrlReader.Read(bytes);
                var paths = new List<string>();
                var roles = new List<string?>();
                for (int i = 0; i < info.TextureOffsets.Length; i++)
                {
                    var path = info.TextureOffsets[i].Path;
                    if (string.IsNullOrWhiteSpace(path))
                        continue;
                    paths.Add(path);
                    roles.Add(MaterialPatcher.TypeOf(info, i) is { } type ? TextureTypeInfo.DisplayName(type) : null);
                }
                return new FileFacts { Size = bytes.Length, StampTicks = stamp, References = paths, Roles = roles, ShaderPackage = info.ShaderPackageName };
            }

            case ResourceKind.Texture or ResourceKind.VfxTexture or ResourceKind.Interface:
                return new FileFacts { Size = bytes.Length, StampTicks = stamp, Texture = TexHeader.TryRead(bytes, out var header) ? header : null };

            case ResourceKind.Animation or ResourceKind.Timeline or ResourceKind.Vfx
              or ResourceKind.Skeleton or ResourceKind.SkeletonParams or ResourceKind.Physics:
                return new FileFacts { Size = bytes.Length, StampTicks = stamp, References = PathStringScanner.Scan(bytes) };

            default:
                return new FileFacts { Size = bytes.Length, StampTicks = stamp };
        }
    }
}

/// <summary>
/// The game's own files, for reading from a worker thread: just existence and bytes, straight from
/// the game's data, with none of the memoising (unlocked) that the rest of the game-data service does.
/// </summary>
internal interface IVanillaFiles
{
    bool Exists(string gamePath);
    byte[]? TryReadBytes(string gamePath);
}
