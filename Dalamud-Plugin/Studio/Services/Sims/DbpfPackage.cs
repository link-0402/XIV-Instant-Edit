using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace InstantEdit.Studio.Services.Sims;

/// <summary>Resource type IDs used by the CAS content this importer cares about.</summary>
public static class SimsResourceType
{
    public const uint Casp       = 0x034AEECB;   // CAS Part — names the textures and LOD meshes
    public const uint Geom       = 0x015A1849;   // Body geometry
    public const uint ImgDds     = 0x00B2D882;   // plain DDS
    public const uint ImgOverlay = 0xB6C8B6A0;   // plain DDS (overlays)
    public const uint Rle2       = 0x3453CF95;   // RLE-compressed DXT5 — diffuse / shadow
    public const uint Rles       = 0xBA856C78;   // RLE-compressed DXT5 — specular
    public const uint RegionMap  = 0xAC16FBEC;   // region map — not a visual texture

    public static bool IsTexture(uint type)
        => type is ImgDds or ImgOverlay or Rle2 or Rles;

    public static string Name(uint type) => type switch
    {
        Casp       => "CASP",
        Geom       => "GEOM",
        ImgDds     => "_IMG",
        ImgOverlay => "_IMG (overlay)",
        Rle2       => "RLE2",
        Rles       => "RLES",
        RegionMap  => "region map",
        _          => $"0x{type:X8}",
    };
}

/// <summary>One resource in a package: its key, where it lives, and how it is packed.</summary>
public sealed class DbpfEntry
{
    public uint  Type;
    public uint  Group;
    public ulong Instance;

    public long   Offset;
    public int    FileSize;          // bytes on disk
    public int    MemSize;           // bytes once decompressed
    public ushort CompressionType;

    /// <summary>Key in the type:group:instance form that s4pe and Sims 4 Studio display.</summary>
    public string KeyString => $"{Type:X8}:{Group:X8}:{Instance:X16}";

    public override string ToString() => $"{SimsResourceType.Name(Type)} {KeyString}";
}

/// <summary>
/// Reader for DBPF 2.1 archives — the .package format The Sims 4 uses.
///
/// Layout: a 96-byte header, then an index at the header's index offset. The index
/// opens with a bitfield saying which parts of the resource key are constant across
/// every entry (and so are stored once, up front, instead of per entry); each entry
/// then carries only its non-constant key parts followed by its position and sizes.
/// The high bit of the file-size field is the "is compressed" flag.
/// </summary>
public sealed class DbpfPackage : IDisposable
{
    private const uint DbpfMagic      = 0x46504244;   // "DBPF"
    private const uint CompressedFlag = 0x80000000;

    /// <summary>
    /// Largest resource this reader will hold in memory, packed or unpacked. The sizes come
    /// from a downloaded file and every byte lands in the game process, so a claim is never
    /// allocated as-is. The biggest real CAS resource is an uncompressed 4096x4096 RGBA DDS
    /// (64 MB); 256 MB leaves room for oversized custom content and still stops a forged
    /// size or a zlib bomb from asking for gigabytes.
    /// </summary>
    internal const int MaxResourceSize = 256 * 1024 * 1024;

    /// <summary>
    /// Most output one packed byte can yield: deflate tops out just above 1032:1, and
    /// RefPack's longest command (4 bytes for 1028 copied bytes) stays far below that. A
    /// declared size past this ratio cannot be real and is refused before allocating.
    /// </summary>
    internal const int MaxCompressionRatio = 1032;

    // Compression types found on index entries.
    private const ushort CompressionNone       = 0x0000;
    private const ushort CompressionZlib       = 0x5A42;
    private const ushort CompressionRefPack    = 0xFFFF;
    private const ushort CompressionStreamable = 0x5A43;

    private readonly FileStream      _stream;
    private readonly BinaryReader    _reader;
    private readonly List<DbpfEntry> _entries = new();

    public IReadOnlyList<DbpfEntry> Entries => _entries;

    public DbpfPackage(string path)
    {
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        _reader = new BinaryReader(_stream);
        try
        {
            ReadHeaderAndIndex();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void ReadHeaderAndIndex()
    {
        if (_stream.Length < 96)
            throw new InvalidDataException("File is too small to be a .package archive.");

        if (_reader.ReadUInt32() != DbpfMagic)
            throw new InvalidDataException("Not a DBPF archive — the file does not begin with DBPF.");

        uint major = _reader.ReadUInt32();
        uint minor = _reader.ReadUInt32();
        if (major != 2)
            throw new InvalidDataException(
                $"Unsupported DBPF version {major}.{minor} — only 2.x (The Sims 4) is supported.");

        _stream.Position = 32;                       // skip user version / flags / timestamps
        _reader.ReadUInt32();                        // index major version
        uint entryCount = _reader.ReadUInt32();
        _reader.ReadUInt32();                        // 32-bit index position, unused in 2.x
        _reader.ReadUInt32();                        // index size

        _stream.Position = 60;
        _reader.ReadUInt32();                        // index minor version
        uint indexOffset = _reader.ReadUInt32();

        if (entryCount == 0)
            return;
        if (indexOffset == 0 || indexOffset >= _stream.Length)
            throw new InvalidDataException("Package index is missing or points outside the file.");

        _stream.Position = indexOffset;
        uint indexType = _reader.ReadUInt32();

        // Bits 0-2 mark key parts that are constant for every entry and stored once here.
        uint constType     = (indexType & 0x01) != 0 ? _reader.ReadUInt32() : 0;
        uint constGroup    = (indexType & 0x02) != 0 ? _reader.ReadUInt32() : 0;
        uint constInstHigh = (indexType & 0x04) != 0 ? _reader.ReadUInt32() : 0;

        for (uint i = 0; i < entryCount; i++)
        {
            var entry = new DbpfEntry
            {
                Type  = (indexType & 0x01) != 0 ? constType  : _reader.ReadUInt32(),
                Group = (indexType & 0x02) != 0 ? constGroup : _reader.ReadUInt32(),
            };

            uint instHigh = (indexType & 0x04) != 0 ? constInstHigh : _reader.ReadUInt32();
            uint instLow  = _reader.ReadUInt32();
            entry.Instance = ((ulong)instHigh << 32) | instLow;

            entry.Offset = _reader.ReadUInt32();
            uint fileSize = _reader.ReadUInt32();
            entry.FileSize        = (int)(fileSize & ~CompressedFlag);
            // Bit 31 makes this negative; Read refuses it rather than allocating for it.
            entry.MemSize         = (int)_reader.ReadUInt32();
            entry.CompressionType = _reader.ReadUInt16();
            _reader.ReadUInt16();                    // committed flag

            // Entries without the compressed flag are stored raw, whatever the type field says.
            if ((fileSize & CompressedFlag) == 0)
                entry.CompressionType = CompressionNone;

            _entries.Add(entry);
        }
    }

    /// <summary>Reads and decompresses one resource.</summary>
    public byte[] Read(DbpfEntry entry)
    {
        if (entry.Offset < 0 || entry.FileSize < 0 || entry.Offset + entry.FileSize > _stream.Length)
            throw new InvalidDataException($"Resource {entry.KeyString} points outside the file.");
        if (entry.FileSize > MaxResourceSize)
            throw new InvalidDataException($"Resource {entry.KeyString} is too large to read ({entry.FileSize:N0} bytes).");

        bool packed = entry.CompressionType != CompressionNone;
        if (packed)
            CheckDeclaredSize(entry.MemSize, entry.FileSize, $"Resource {entry.KeyString}");

        _stream.Position = entry.Offset;
        var raw = _reader.ReadBytes(entry.FileSize);

        return entry.CompressionType switch
        {
            CompressionNone       => raw,
            CompressionZlib       => Inflate(raw, entry.MemSize, MaxResourceSize),
            CompressionRefPack    => RefPack.Decompress(raw, entry.MemSize),
            CompressionStreamable => throw new NotSupportedException(
                $"Resource {entry.KeyString} uses streamable compression (0x5A43), which this importer cannot read."),
            _ => throw new NotSupportedException(
                $"Resource {entry.KeyString} uses unknown compression 0x{entry.CompressionType:X4}."),
        };
    }

    /// <summary>Reads a resource, returning null instead of throwing when it cannot be decoded.</summary>
    public byte[]? TryRead(DbpfEntry entry, out string error)
    {
        try
        {
            error = string.Empty;
            return Read(entry);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Rejects an unpacked size that is negative (bit 31 set in the index), above
    /// <see cref="MaxResourceSize"/>, or more than <paramref name="packedSize"/> could ever
    /// expand to. Zero means "not stated" and is allowed; the unpacker then caps the output.
    /// </summary>
    internal static void CheckDeclaredSize(long declared, long packedSize, string what)
    {
        if (declared < 0 || declared > MaxResourceSize)
            throw new InvalidDataException($"{what} declares an impossible unpacked size ({declared:N0} bytes).");
        if (declared > packedSize * MaxCompressionRatio + 1024)
            throw new InvalidDataException(
                $"{what} claims {declared:N0} bytes from {packedSize:N0} packed bytes, more than any compression yields.");
    }

    /// <summary>
    /// Inflates a zlib resource. A stated size is allocated once (the caller has checked it);
    /// without one the output grows only up to <paramref name="maxSize"/> or what the packed
    /// size could yield, whichever is smaller, and a stream that keeps going past that fails.
    /// </summary>
    internal static byte[] Inflate(byte[] source, int expectedSize, int maxSize)
    {
        using var input = new MemoryStream(source, writable: false);
        using var zlib  = new ZLibStream(input, CompressionMode.Decompress);

        if (expectedSize > 0)
        {
            if (expectedSize > maxSize)
                throw new InvalidDataException($"Packed resource is too large to unpack ({expectedSize:N0} bytes).");
            var buffer = new byte[expectedSize];
            try
            {
                zlib.ReadExactly(buffer, 0, expectedSize);
            }
            catch (EndOfStreamException)
            {
                throw new InvalidDataException("Packed resource ends before its stated size.");
            }
            return buffer;
        }

        long limit = Math.Min(maxSize, (long)source.Length * MaxCompressionRatio + 1024);
        using var output = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = zlib.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (output.Length + read > limit)
                throw new InvalidDataException($"Packed resource unpacks to more than {limit:N0} bytes.");
            output.Write(chunk, 0, read);
        }
        return output.ToArray();
    }

    public void Dispose()
    {
        _reader.Dispose();
        _stream.Dispose();
    }
}
