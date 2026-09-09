using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Lumina.Data.Files;
using Penumbra.Api.Enums;
using InstantEdit.Models;

namespace InstantEdit.Services;

internal readonly record struct TextureHeader(uint Format, int Width, int Height, int Mips);

/// <summary>File validation only. All pixel decoding and encoding is delegated to Penumbra.</summary>
internal static class TextureFiles
{
    public const string CacheFolder = "XIV-Instant-Edit";
    private const string CacheSchema = "instant-edit.cache";
    private const int CacheVersion = 1;
    public const int MaxBytes = 512 * 1024 * 1024;
    public static string Hash(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data));
    public static TextureType OutputType(uint format) => (TexFile.TextureFormat)format switch
    {
        TexFile.TextureFormat.BC1 => TextureType.Bc1Tex,
        TexFile.TextureFormat.BC3 => TextureType.Bc3Tex,
        TexFile.TextureFormat.BC4 => TextureType.Bc4Tex,
        TexFile.TextureFormat.BC5 => TextureType.Bc5Tex,
        TexFile.TextureFormat.BC7 => TextureType.Bc7Tex,
        TexFile.TextureFormat.B8G8R8A8 => TextureType.RgbaTex,
        _ => throw new NotSupportedException($"Original TEX format 0x{format:X4} cannot be preserved. Supported: BC1/3/4/5/7 and BGRA32."),
    };

    public static string FormatName(uint format) => OutputType(format) switch
    {
        TextureType.Bc1Tex => "BC1", TextureType.Bc3Tex => "BC3", TextureType.Bc4Tex => "BC4",
        TextureType.Bc5Tex => "BC5", TextureType.Bc7Tex => "BC7", _ => "BGRA32",
    };

    public static string CacheRootFor(string cacheDirectory)
    {
        var baseDirectory = string.IsNullOrWhiteSpace(cacheDirectory)
            ? Path.GetTempPath()
            : cacheDirectory.Trim().Trim('"');
        EnsureLocalPath(baseDirectory);
        return Path.GetFullPath(Path.Combine(baseDirectory, CacheFolder));
    }

    public static string EnsureCacheRoot(string cacheDirectory)
    {
        var root = CacheRootFor(cacheDirectory);
        EnsureManagedCacheRoot(root);
        return root;
    }

    private static void EnsureManagedCacheRoot(string root)
    {
        EnsureLocalPath(root);
        Directory.CreateDirectory(root);
        var marker = Path.Combine(root, ".instant-edit-cache.json");
        if (File.Exists(marker))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(marker));
            if (!document.RootElement.TryGetProperty("schema", out var schema) ||
                schema.GetString() != CacheSchema ||
                !document.RootElement.TryGetProperty("version", out var version) ||
                version.GetInt32() != CacheVersion)
                throw new IOException("The cache ownership marker is invalid.");
        }
        else
        {
            if (Directory.EnumerateFileSystemEntries(root).Any())
                throw new IOException("The cache directory is not empty and is not owned by XIV Instant Edit.");
            File.WriteAllText(marker, "{\"schema\":\"instant-edit.cache\",\"version\":1}");
        }
        foreach (var folder in new[] { "imports", "exports", "backups" })
            Directory.CreateDirectory(Path.Combine(root, folder));
    }

    public static TextureHeader ReadTex(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 80 || bytes.Length > MaxBytes) throw new InvalidDataException("TEX file is truncated or too large.");
        var type = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if ((type & 0x13C00000) != 0x00800000 || BinaryPrimitives.ReadUInt16LittleEndian(bytes[12..]) > 1 || bytes[15] > 1)
            throw new NotSupportedException("Only ordinary 2D textures are supported; arrays, cubes and volumes cannot be edited.");
        var h = new TextureHeader(BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]),
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]), BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..]), bytes[14] & 0x7f);
        _ = OutputType(h.Format);
        if (h.Width is < 1 or > 8192 || h.Height is < 1 or > 8192 || h.Mips is < 1 or > 13 || h.Mips > FullMipCount(h.Width, h.Height))
            throw new InvalidDataException("Unsupported TEX dimensions or mip count (maximum size: 8192 × 8192).");
        long previousEnd = 80;
        for (var mip = 0; mip < h.Mips; mip++)
        {
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(28 + mip * 4)..]);
            var w = Math.Max(1, h.Width >> mip);
            var height = Math.Max(1, h.Height >> mip);
            var size = h.Format == (uint)TexFile.TextureFormat.B8G8R8A8 ? (long)w * height * 4
                : (long)((w + 3) / 4) * ((height + 3) / 4) *
                  (h.Format == (uint)TexFile.TextureFormat.BC1 || h.Format == (uint)TexFile.TextureFormat.BC4 ? 8 : 16);
            if (offset < previousEnd || offset + size > bytes.Length) throw new InvalidDataException("TEX mip data is incomplete or overlaps.");
            previousEnd = offset + size;
        }
        return h;
    }

    public static int FullMipCount(int w, int h) => Math.Min(13, 1 + System.Numerics.BitOperations.Log2((uint)Math.Max(w, h)));

    public static void ValidateOutput(byte[] bytes, TextureEditSession session, bool restoring = false)
    {
        var header = ReadTex(bytes);
        if (header.Format != session.Format || header.Width != session.Width || header.Height != session.Height ||
            (!restoring && header.Mips != (session.MipMaps ? FullMipCount(session.Width, session.Height) : 1)))
            throw new InvalidDataException("Converted TEX format, dimensions or mipmaps do not match the session.");
    }

    public static string PixelHash(byte[] uncompressedTex)
    {
        var h = ReadTex(uncompressedTex);
        if (h.Format != (uint)TexFile.TextureFormat.B8G8R8A8) throw new InvalidDataException("Penumbra did not return BGRA32 pixels.");
        var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(uncompressedTex.AsSpan(28));
        return Hash(uncompressedTex.AsSpan(offset, checked(h.Width * h.Height * 4)));
    }

    public static void ValidateTga(ReadOnlySpan<byte> bytes, int width, int height)
    {
        if (bytes.Length < 18 || bytes.Length > MaxBytes || bytes[1] != 0 || bytes[2] is not (2 or 10) ||
            bytes[16] != 32 || (bytes[17] & 15) != 8 || (bytes[17] & 0xc0) != 0)
            throw new InvalidDataException("Save a 32-bit true-color TGA with an 8-bit alpha channel (uncompressed or RLE).");
        if (BinaryPrimitives.ReadUInt16LittleEndian(bytes[12..]) != width || BinaryPrimitives.ReadUInt16LittleEndian(bytes[14..]) != height)
            throw new InvalidDataException($"Keep the original dimensions: {width} × {height}.");
        var offset = 18 + bytes[0];
        var remaining = checked(width * height);
        if (bytes[2] == 2)
        {
            if ((long)offset + remaining * 4L > bytes.Length) throw new InvalidDataException("TGA save is incomplete.");
            return;
        }
        while (remaining > 0)
        {
            if (offset >= bytes.Length) throw new InvalidDataException("TGA RLE save is incomplete.");
            var packet = bytes[offset++];
            var count = (packet & 127) + 1;
            if (count > remaining) throw new InvalidDataException("TGA RLE packet exceeds the image size.");
            offset += (packet & 128) != 0 ? 4 : count * 4;
            if (offset > bytes.Length) throw new InvalidDataException("TGA RLE save is incomplete.");
            remaining -= count;
        }
    }

    public static void EnsureLocalPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal))
            throw new IOException("Choose a local absolute path.");
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked files and directories are not supported for texture editing.");
            current = Path.GetDirectoryName(current);
        }
    }

    public static string ValidateCache(string root)
    {
        EnsureLocalPath(root);
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, ".instant-edit-cache.json")));
        if (marker.RootElement.GetProperty("schema").GetString() != "instant-edit.cache" || marker.RootElement.GetProperty("version").GetInt32() != 1)
            throw new IOException("The cache ownership marker is invalid. Reconnect Blender to synchronize its cache.");
        return root;
    }

    public static byte[] Read(string path)
    {
        EnsureLocalPath(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > MaxBytes) throw new IOException("Texture file is empty or too large.");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    public static void WriteJson(string path, object value)
    {
        EnsureLocalPath(path);
        var temp = path + ".tmp";
        EnsureLocalPath(temp);
        File.WriteAllText(temp, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, true);
    }

    public static string Replace(string target, string root, string relative, string mod, byte[] bytes,
        string expectedHash, ModelBackupStore backups, Func<bool> current, CancellationToken token)
    {
        EnsureLocalPath(target);
        if (!PenumbraService.IsSafeGameResourcePath(relative, ".tex") || !PathRules.IsPathWithin(target, root) ||
            !string.Equals(Path.GetFullPath(target), Path.GetFullPath(Path.Combine(root, relative)), StringComparison.OrdinalIgnoreCase))
            throw new TextureConflictException("The replacement target is outside the captured texture mod.");
        _ = ReadTex(bytes);
        if (Hash(Read(target)) != expectedHash)
            throw new TextureConflictException("The destination was edited externally. Your TGA is retained; reopen the texture from its new source.");
        var temp = target + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temp, bytes);
            token.ThrowIfCancellationRequested();
            if (!current()) throw new OperationCanceledException("A newer save is pending.");
            var backup = backups.Create(target, mod, relative);
            if (Hash(Read(backup)) != expectedHash)
                throw new TextureConflictException("The destination changed while preparing the save.");
            token.ThrowIfCancellationRequested();
            if (!current()) throw new OperationCanceledException("A newer save is pending.");
            EnsureLocalPath(target);
            if (Hash(Read(target)) != expectedHash)
                throw new TextureConflictException("The destination changed while preparing the save.");
            File.Replace(temp, target, null);
            return backup;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
