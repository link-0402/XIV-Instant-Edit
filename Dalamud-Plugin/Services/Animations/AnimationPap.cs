using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace InstantEdit.Services.Animations;

/// <summary>PAP envelope layout follows VFXEditor PapFile/PapAnimation. Preserve opaque metadata and TMB bytes.</summary>
internal sealed class AnimationPap
{
    public sealed record Entry(string Name, short Type, short Binding, int Face);
    private readonly byte[] bytes;
    public int HavokOffset { get; }
    public int TimelineOffset { get; }
    public ushort ModelId => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(10));
    public byte ModelType => bytes[12];
    public ImmutableArray<Entry> Entries { get; }
    public byte[] Havok => bytes[HavokOffset..TimelineOffset];
    public byte[] Timelines => bytes[TimelineOffset..];
    public const int MaxFileSize = 256 * 1024 * 1024;
    // Packed header: magic/version (8), count (2), model ID (2), type/variant
    // (1 each), then three int32 offsets. There is no alignment gap at byte 14.
    private const int HeaderSize = 26;
    private const int InfoOffsetField = 14;
    private const int HavokOffsetField = 18;
    private const int TimelineOffsetField = 22;
    private const int EntrySize = 40;

    public AnimationPap(byte[] data)
    {
        if (data.Length < HeaderSize || data.Length > MaxFileSize || !data.AsSpan(0, 4).SequenceEqual("pap "u8))
            throw new InvalidDataException("Invalid PAP header or size.");
        if (ReadInt(data, 4) != 0x00020001) throw new InvalidDataException("Unsupported PAP version.");
        var count = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(8));
        var info = ReadInt(data, InfoOffsetField);
        HavokOffset = ReadInt(data, HavokOffsetField);
        TimelineOffset = ReadInt(data, TimelineOffsetField);
        if (count < 1 || count > 4096 || info < HeaderSize || info > HavokOffset ||
            (long)info + count * EntrySize > HavokOffset || HavokOffset > TimelineOffset || TimelineOffset > data.Length)
            throw new InvalidDataException($"PAP offsets or animation count are invalid " +
                $"(count={count}, info={info}, Havok={HavokOffset}, timeline={TimelineOffset}, size={data.Length}).");
        var entries = ImmutableArray.CreateBuilder<Entry>(count);
        for (var i = 0; i < count; i++)
        {
            var start = info + i * EntrySize;
            var name = data.AsSpan(start, 32);
            var end = name.IndexOf((byte)0);
            if (end < 1) throw new InvalidDataException("PAP animation name is missing or unterminated.");
            var binding = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(start + 34));
            if (binding < 0) throw new InvalidDataException("PAP binding index is negative.");
            entries.Add(new Entry(Encoding.UTF8.GetString(name[..end]),
                BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(start + 32)), binding, ReadInt(data, start + 36)));
        }
        Entries = entries.MoveToImmutable();
        bytes = data.ToArray();
    }

    public byte[] ReplaceHavok(byte[] havok)
    {
        if (havok.Length < 8 || havok.Length > MaxFileSize) throw new InvalidDataException("Invalid baked Havok size.");
        // Keep the footer's original alignment, including nonstandard modded PAP padding.
        var padding = (TimelineOffset - (HavokOffset + havok.Length)) & 3;
        var footer = checked(HavokOffset + havok.Length + padding);
        var result = new byte[checked(footer + bytes.Length - TimelineOffset)];
        if (result.Length > MaxFileSize) throw new InvalidDataException("Baked PAP exceeds the size limit.");
        bytes.AsSpan(0, HavokOffset).CopyTo(result);
        havok.CopyTo(result, HavokOffset);
        bytes.AsSpan(TimelineOffset).CopyTo(result.AsSpan(footer));
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(TimelineOffsetField), footer);
        _ = new AnimationPap(result);
        return result;
    }

    public static int ReadInt(byte[] data, int offset)
    {
        if (offset < 0 || offset > data.Length - 4) throw new InvalidDataException("Resource offset is out of bounds.");
        return BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
    }
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public static byte[] SkeletonHavok(byte[] bytes)
    {
        // SKLB magic and version are stored as reversed FourCCs.
        if (bytes.Length < 16 || ReadInt(bytes, 0) != 0x736b6c62)
            throw new InvalidDataException("Invalid SKLB header.");
        var version = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6));
        var offset = version switch
        {
            0x3133 => ReadInt(bytes, 12),
            0x3132 => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(10)),
            _ => throw new InvalidDataException("Unsupported SKLB version."),
        };
        if (offset < 12 || offset >= bytes.Length - 8) throw new InvalidDataException("Invalid SKLB Havok offset.");
        return bytes[offset..];
    }
}
