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
    public const string CacheFolder = "XIV Instant Edit";
    public const string LegacyCacheFolder = "XIV-Instant-Edit";
    private const string CacheSchema = "instant-edit.cache";
    private const int CacheVersion = 1;
    // Top-level entries the plugin or the Blender add-on write into the cache.
    // Keep in sync with OWNED_ROOT_DIRECTORIES/OWNED_ROOT_FILES in Blender-Addon/instant_edit/cache.py.
    private static readonly HashSet<string> OwnedRootDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "imports", "exports", "backups", "texture-edits", "AnimationEdits", "skeleton-library", "Contexts", "painter", "game-exports",
        "texture-backups",
    };
    private static readonly HashSet<string> OwnedRootFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "TextureSessions.json", "TextureSessions.json.tmp", "pending-context-revocations.json",
    };
    private static readonly System.Text.RegularExpressions.Regex OwnedRootTemporary = new(
        @"^\.pending-context-revocations\.json\.[0-9a-f]{32}\.tmp$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    public const int MaxBytes = 512 * 1024 * 1024;
    public const int MaxDimension = 8192;
    private const uint Uncompressed = (uint)TexFile.TextureFormat.B8G8R8A8;
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

    public static bool IsBlockCompressed(uint format) => OutputType(format) != TextureType.RgbaTex;

    /// <summary>Encoding for the next save: the captured format, or uncompressed when recompression is off.</summary>
    public static uint SaveFormat(TextureEditSession session, bool recompress) => recompress ? session.Format : Uncompressed;

    /// <summary>A session's destination holds either its captured format or an uncompressed save.</summary>
    public static bool IsSessionFormat(uint format, TextureEditSession session) => format == session.Format || format == Uncompressed;

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
            // Temp cleaners delete the marker once it is old, while newer cache files
            // survive. A folder holding only entries this cache creates is still ours.
            if (Directory.EnumerateFileSystemEntries(root).Any(entry => !IsOwnedRootEntry(entry)))
                throw new IOException("The cache directory is not empty and is not owned by XIV Instant Edit.");
            File.WriteAllText(marker, "{\"schema\":\"instant-edit.cache\",\"version\":1}");
        }
        foreach (var folder in new[] { "imports", "exports", "backups" })
            Directory.CreateDirectory(Path.Combine(root, folder));
    }

    private static bool IsOwnedRootEntry(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            return false;
        var name = Path.GetFileName(path);
        return (attributes & FileAttributes.Directory) != 0
            ? OwnedRootDirectories.Contains(name)
            : OwnedRootFiles.Contains(name) || OwnedRootTemporary.IsMatch(name);
    }

    /// <summary>
    /// Reads a TEX the plugin didn't write, a mod's or the game's. Its mip offsets are normalized
    /// first (<see cref="NormalizeMipOffsets"/>), so textures the game draws normally are accepted.
    /// Returns those bytes, whose header describes their data, to keep and write back instead of the
    /// input. Files the plugin writes are checked with <see cref="ReadTex"/> as they are.
    /// </summary>
    public static (byte[] Bytes, TextureHeader Header) ReadOriginal(byte[] bytes)
    {
        var normalized = NormalizeMipOffsets(bytes);
        return (normalized, ReadTex(normalized));
    }

    /// <summary>
    /// Some texture tools write TEX headers whose mip offset table does not describe the data, such
    /// as uncompressed-size offsets for BC7 mips or small bogus values, or claim a last mip the file
    /// doesn't hold. Penumbra reads the mips contiguously after the header and the game draws them,
    /// but Lumina and <see cref="ReadTex"/> locate each mip from that table. When the stored
    /// table cannot hold the mips, rebuild it for contiguous 2D mips after the first surface,
    /// keeping only the levels that fit in the file, and keep the LOD mip indices within them as
    /// Penumbra's import does. Consistent files are returned as they are.
    /// </summary>
    public static byte[] NormalizeMipOffsets(byte[] bytes)
    {
        const int headerSize = 80;
        const int lodTable = 16;
        const int offsetTable = 28;
        const uint textureTypeMask = 0x13C00000;
        const uint textureType2D = 0x00800000;
        if (bytes.Length < headerSize ||
            (BinaryPrimitives.ReadUInt32LittleEndian(bytes) & textureTypeMask) != textureType2D)
            return bytes;

        // Mirrors Lumina's TexFile.SliceSize: format bits 4-7 hold log2(bits
        // per pixel) and bits 12-15 the type, where 3 and 6 are BC formats.
        var format = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
        int width = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8));
        int height = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(10));
        var mipCount = Math.Clamp(bytes[14] & 0x7F, 1, 13);
        var bitsPerPixel = 1L << (int)((format >> 4) & 0xF);
        var blockCompressed = ((format >> 12) & 0xF) is 3 or 6;
        long SurfaceSize(int mip)
        {
            long w = Math.Max(1, width >> mip), h = Math.Max(1, height >> mip);
            return blockCompressed
                ? Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * bitsPerPixel * 2
                : w * h * bitsPerPixel / 8;
        }

        var offsets = new long[mipCount];
        for (var mip = 0; mip < mipCount; mip++)
            offsets[mip] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offsetTable + mip * 4));
        var consistent = offsets[0] >= headerSize;
        for (var mip = 0; consistent && mip < mipCount; mip++)
        {
            var end = offsets[mip] + SurfaceSize(mip);
            consistent = end <= bytes.Length && (mip == mipCount - 1 || offsets[mip + 1] >= end);
        }
        if (consistent)
            return bytes;

        var start = offsets[0] >= headerSize && offsets[0] + SurfaceSize(0) <= bytes.Length ? offsets[0] : headerSize;
        if (start + SurfaceSize(0) > bytes.Length)
            return bytes;
        var normalized = (byte[])bytes.Clone();
        var kept = 0;
        for (var offset = start; kept < mipCount && offset + SurfaceSize(kept) <= bytes.Length; kept++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(normalized.AsSpan(offsetTable + kept * 4), (uint)offset);
            offset += SurfaceSize(kept);
        }
        normalized.AsSpan(offsetTable + kept * 4, (13 - kept) * 4).Clear();
        normalized[14] = (byte)((bytes[14] & 0x80) | kept);
        // The same limits as Penumbra's TexFileParser.FixMipOffsets.
        var lowest = BinaryPrimitives.ReadUInt32LittleEndian(normalized.AsSpan(lodTable + 8));
        if (lowest >= kept)
            BinaryPrimitives.WriteUInt32LittleEndian(normalized.AsSpan(lodTable + 8), (uint)(kept - 1));
        var middle = BinaryPrimitives.ReadUInt32LittleEndian(normalized.AsSpan(lodTable + 4));
        if (middle >= kept)
            BinaryPrimitives.WriteUInt32LittleEndian(normalized.AsSpan(lodTable + 4), (uint)(kept > 2 ? kept - 2 : kept - 1));
        return normalized;
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
        if (h.Width is < 1 or > MaxDimension || h.Height is < 1 or > MaxDimension || h.Mips is < 1 or > 13 || h.Mips > FullMipCount(h.Width, h.Height))
            throw new InvalidDataException($"Unsupported TEX dimensions or mip count (maximum size: {MaxDimension} × {MaxDimension}).");
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

    /// <summary>
    /// Puts the original's data back wherever an edit didn't reach. For each mip level both files
    /// hold, every 4 × 4 block (every pixel of uncompressed data) whose footprint in the full-size
    /// image, with one pixel of that level around it for the downsampling filter, holds no changed
    /// pixel keeps the original's bytes. A re-encode then changes only what the edit changed, instead
    /// of adding its own compression error to the whole texture each time it is saved. Returns
    /// <paramref name="encoded"/> unchanged unless both files have the same format and size.
    /// </summary>
    /// <param name="changed">Per full-size pixel, row by row, whether the edit changed it.</param>
    public static byte[] KeepUnchanged(byte[] original, byte[] encoded, bool[] changed)
    {
        var (source, from) = ReadOriginal(original);
        var to = ReadTex(encoded);
        if (from.Format != to.Format || from.Width != to.Width || from.Height != to.Height || changed.Length != from.Width * from.Height)
            return encoded;
        int width = from.Width, height = from.Height;
        // Summed-area table of changed pixels, so any footprint is counted at once.
        var sums = new int[(width + 1) * (height + 1)];
        for (var y = 0; y < height; y++)
        {
            var row = 0;
            for (var x = 0; x < width; x++)
            {
                row += changed[y * width + x] ? 1 : 0;
                sums[(y + 1) * (width + 1) + x + 1] = sums[y * (width + 1) + x + 1] + row;
            }
        }
        int Count(int x0, int y0, int x1, int y1)
        {
            x0 = Math.Clamp(x0, 0, width);
            x1 = Math.Clamp(x1, 0, width);
            y0 = Math.Clamp(y0, 0, height);
            y1 = Math.Clamp(y1, 0, height);
            if (x1 <= x0 || y1 <= y0)
                return 0;
            return sums[y1 * (width + 1) + x1] - sums[y0 * (width + 1) + x1] - sums[y1 * (width + 1) + x0] + sums[y0 * (width + 1) + x0];
        }

        var uncompressed = from.Format == Uncompressed;
        var blockBytes = uncompressed ? 4 : from.Format is (uint)TexFile.TextureFormat.BC1 or (uint)TexFile.TextureFormat.BC4 ? 8 : 16;
        var blockPixels = uncompressed ? 1 : 4;
        var result = (byte[])encoded.Clone();
        for (var mip = 0; mip < Math.Min(from.Mips, to.Mips); mip++)
        {
            var scale = 1 << mip;
            int levelWidth = Math.Max(1, width >> mip), levelHeight = Math.Max(1, height >> mip);
            int blocksX = (levelWidth + blockPixels - 1) / blockPixels, blocksY = (levelHeight + blockPixels - 1) / blockPixels;
            var sourceOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(28 + mip * 4));
            var targetOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(28 + mip * 4));
            for (var by = 0; by < blocksY; by++)
                for (var bx = 0; bx < blocksX; bx++)
                {
                    var x0 = bx * blockPixels * scale - scale;
                    var y0 = by * blockPixels * scale - scale;
                    if (Count(x0, y0, x0 + (blockPixels + 2) * scale, y0 + (blockPixels + 2) * scale) != 0)
                        continue;
                    var at = (by * blocksX + bx) * blockBytes;
                    source.AsSpan(sourceOffset + at, blockBytes).CopyTo(result.AsSpan(targetOffset + at, blockBytes));
                }
        }
        return result;
    }

    public static void ValidateOutput(byte[] bytes, uint format, int width, int height, bool mipMaps)
    {
        var header = ReadTex(bytes);
        if (header.Format != format || header.Width != width || header.Height != height ||
            header.Mips != (mipMaps ? FullMipCount(width, height) : 1))
            throw new InvalidDataException("Converted TEX format, dimensions or mipmaps do not match the saved image.");
    }

    /// <summary>Commit-time check: any supported size, stored in the session's captured format or uncompressed.</summary>
    public static TextureHeader ValidateCommit(byte[] bytes, TextureEditSession session)
    {
        var header = ReadTex(bytes);
        if (!IsSessionFormat(header.Format, session))
            throw new InvalidDataException($"A {FormatName(session.Format)} session cannot store {FormatName(header.Format)} data.");
        return header;
    }

    /// <summary>Block-compressed formats encode whole 4 × 4 tiles; Direct3D requires this of the top level.</summary>
    public static void ValidateEncodable(uint format, int width, int height)
    {
        if (IsBlockCompressed(format) && (width % 4 != 0 || height % 4 != 0))
            throw new InvalidDataException($"{FormatName(format)} needs a width and height divisible by 4; this save is {width} × {height}. " +
                "Resize it, or turn off texture recompression in Options to save uncompressed.");
    }

    /// <summary>Identity of the decoded image. The size participates so a same-area resize still counts as a change.</summary>
    public static string PixelHash(byte[] uncompressedTex)
    {
        var h = ReadTex(uncompressedTex);
        if (h.Format != Uncompressed) throw new InvalidDataException("Penumbra did not return BGRA32 pixels.");
        var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(uncompressedTex.AsSpan(28));
        Span<byte> size = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(size, h.Width);
        BinaryPrimitives.WriteInt32LittleEndian(size[4..], h.Height);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(size);
        hash.AppendData(uncompressedTex.AsSpan(offset, checked(h.Width * h.Height * 4)));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static void ValidateTga(ReadOnlySpan<byte> bytes, int width, int height)
    {
        var size = ValidateTga(bytes);
        if (size != (width, height))
            throw new InvalidDataException($"The TGA is {size.Width} × {size.Height}; expected {width} × {height}.");
    }

    /// <summary>Validates a 32-bit TGA save and returns its dimensions.</summary>
    public static (int Width, int Height) ValidateTga(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 18 || bytes.Length > MaxBytes || bytes[1] != 0 || bytes[2] is not (2 or 10) ||
            bytes[16] != 32 || (bytes[17] & 15) != 8 || (bytes[17] & 0xc0) != 0)
            throw new InvalidDataException("Save a 32-bit true-color TGA with an 8-bit alpha channel (uncompressed or RLE).");
        int width = BinaryPrimitives.ReadUInt16LittleEndian(bytes[12..]), height = BinaryPrimitives.ReadUInt16LittleEndian(bytes[14..]);
        if (width is < 1 or > MaxDimension || height is < 1 or > MaxDimension)
            throw new InvalidDataException($"Texture size must be 1 to {MaxDimension} pixels per side; this save is {width} × {height}.");
        var offset = 18 + bytes[0];
        var remaining = checked(width * height);
        if (bytes[2] == 2)
        {
            if ((long)offset + remaining * 4L > bytes.Length) throw new InvalidDataException("TGA save is incomplete.");
            return (width, height);
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
        return (width, height);
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
