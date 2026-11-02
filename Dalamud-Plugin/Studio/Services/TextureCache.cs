using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using InstantEdit.Services.TextureCompression;

namespace InstantEdit.Studio.Services;

/// <summary>
/// Keeps the .tex files converted for the mod, so the same images are not compressed again.
/// Compressing is by far the slowest part of updating a mod — BC7 on a 4096×4096 image takes the
/// better part of a minute even across every core — and a port is updated many times over while
/// its materials and metadata are worked out, from the same handful of source images.
///
/// An entry is keyed by everything that decides the bytes: the source file (its path, size and
/// timestamp) and the settings used. Editing the image changes its timestamp, so the next update
/// converts it again; nothing has to be invalidated by hand. Entries live in the plugin's own
/// config folder and are pruned oldest-first once they pass <see cref="MaxBytes"/>.
///
/// An entry is written to a ".part" file and renamed into place, and one whose length falls short of
/// what its own header describes is dropped when it is read, so an update that was cut short (a full
/// drive, a crash) never hands a truncated texture to a mod.
///
/// Used from the update worker, which is the only thread that touches it while it runs.
/// </summary>
internal sealed class TextureCache
{
    /// <summary>How much converted texture data to keep before pruning the least recently used.</summary>
    private const long MaxBytes = 1024L * 1024 * 1024;

    private readonly string? _directory;

    public TextureCache(string? directory)
    {
        _directory = string.IsNullOrWhiteSpace(directory) ? null : directory;
    }

    /// <summary>Identifies one conversion: the source as it was, and everything that shaped the result.</summary>
    public static string KeyFor(string sourcePath, params object[] settings)
    {
        string stamp;
        try
        {
            var info = new FileInfo(sourcePath);
            stamp = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch (Exception)
        {
            stamp = sourcePath;
        }

        return Hash($"{stamp}|{string.Join('|', settings)}");
    }

    /// <summary>Identifies a conversion with no source file of its own, such as a generated placeholder.</summary>
    public static string KeyForGenerated(params object[] settings) => Hash($"generated|{string.Join('|', settings)}");

    /// <summary>The bytes this conversion produced last time, or null when it has to be done again.</summary>
    public byte[]? Get(string key)
    {
        var path = PathFor(key);
        if (path == null || !File.Exists(path))
            return null;

        try
        {
            var data = File.ReadAllBytes(path);
            if (!IsComplete(data, data.Length))
            {
                Discard(path);
                return null;
            }
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);   // keeps what is in use from being pruned
            return data;
        }
        catch (Exception)
        {
            return null;   // a cache that cannot be read is simply a miss
        }
    }

    /// <summary>The file holding what this conversion produced last time, or null when it has to be done again.</summary>
    public string? PathOf(string key)
    {
        var path = PathFor(key);
        if (path == null || !File.Exists(path))
            return null;

        if (!IsComplete(path))
        {
            Discard(path);
            return null;
        }

        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);   // keeps what is in use from being pruned
        }
        catch (Exception)
        {
            // Pruned a little early, at worst.
        }
        return path;
    }

    /// <summary>Keeps a conversion's bytes; returns the file they are kept in, or null when there is no cache to keep them in.</summary>
    public string? Store(string key, byte[] data)
    {
        var path = PathFor(key);
        if (path == null)
            return null;

        var part = PartFor(path);
        try
        {
            Directory.CreateDirectory(_directory!);
            File.WriteAllBytes(part, data);
            File.Move(part, path, overwrite: true);
            return path;
        }
        catch (Exception)
        {
            Discard(part);   // never leave half an entry under a name that is read back as a hit
            return null;     // nothing to do about it: the caller has the bytes either way
        }
    }

    /// <summary>Moves a converted file into the cache; returns where it is kept now, or null when it could not be moved.</summary>
    public string? Adopt(string key, string file)
    {
        var path = PathFor(key);
        if (path == null)
            return null;

        var part = PartFor(path);
        try
        {
            Directory.CreateDirectory(_directory!);
            // Copied beside the entry and renamed into place: a move from another drive is a copy and a
            // delete, which can be cut short at any point, and the caller still needs the file if this fails.
            File.Copy(file, part, overwrite: true);
            File.Move(part, path, overwrite: true);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (Exception)
        {
            Discard(part);
            return null;
        }

        Discard(file);
        return path;
    }

    /// <summary>Drops the oldest entries once the folder grows past its limit. Called after each update.</summary>
    public void Prune() => Prune(MaxBytes);

    internal void Prune(long maxBytes)
    {
        if (_directory == null || !Directory.Exists(_directory))
            return;

        try
        {
            // Left behind by a write the process did not survive.
            foreach (var stale in new DirectoryInfo(_directory).GetFiles("*.part"))
                if (stale.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1))
                    Discard(stale.FullName);

            var files = new DirectoryInfo(_directory).GetFiles("*.tex");
            long total = files.Sum(f => f.Length);
            if (total <= maxBytes)
                return;

            foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc))
            {
                // The length first: once deleted, FileInfo.Length throws for the file that is gone.
                long size = file.Length;
                try
                {
                    file.Delete();
                }
                catch (Exception)
                {
                    continue;   // in use, say; the next one frees the space instead
                }
                total -= size;
                if (total <= maxBytes)
                    return;
            }
        }
        catch (Exception)
        {
            // Pruning is housekeeping; a failure here must not fail an update.
        }
    }

    private string? PathFor(string key)
        => _directory == null ? null : Path.Combine(_directory, $"{key}.tex");

    private static string PartFor(string path) => $"{path}.{Guid.NewGuid():N}.part";

    private static void Discard(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // In use, say; pruning or the next write gets it.
        }
    }

    /// <summary>
    /// False for a texture shorter than its own header says: 80 bytes of header and every mip level of
    /// the format. Longer is fine (the converter may pad), and anything this cannot size is let through.
    /// </summary>
    private static bool IsComplete(ReadOnlySpan<byte> head, long length)
    {
        if (length < TextureCost.HeaderSize || head.Length < TextureCost.HeaderSize)
            return false;
        return TextureCost.Read(head) is { } info && (info.Vram is not { } vram || length >= TextureCost.HeaderSize + vram);
    }

    private static bool IsComplete(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> head = stackalloc byte[TextureCost.HeaderSize];
            return stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) == head.Length &&
                   IsComplete(head, stream.Length);
        }
        catch (Exception)
        {
            return false;   // a cache that cannot be read is simply a miss
        }
    }

    /// <summary>
    /// Part of every key, raised whenever the converter's output changes for the same input, so
    /// entries written by an older converter are never handed out again (they age out by pruning).
    /// 2: BC4/BC5/BC6H were written with format codes the game does not know, crashing it.
    /// </summary>
    private const int Revision = 2;

    private static string Hash(string value)
        => System.Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"r{Revision}|{value}")))[..32].ToLowerInvariant();
}
