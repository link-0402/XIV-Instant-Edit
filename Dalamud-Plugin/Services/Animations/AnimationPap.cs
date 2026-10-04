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

    /// <summary>
    /// Rewrite the declared source model so a cross-race output does not keep
    /// advertising the race it was authored for. Every offset is unchanged.
    /// </summary>
    public byte[] WithModel(ushort modelId, byte modelType)
    {
        if (modelType > 3) throw new InvalidDataException("Unsupported PAP skeleton type.");
        var result = bytes.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(10), modelId);
        result[12] = modelType;
        _ = new AnimationPap(result);
        return result;
    }

    /// <summary>The index of the entry named <paramref name="name"/> with Havok binding <paramref name="binding"/>.</summary>
    public int EntryIndex(string name, int binding)
    {
        var matches = Entries.Select((entry, index) => (entry, index)).Where(e => e.entry.Name == name && e.entry.Binding == binding).ToArray();
        return matches.Length switch
        {
            1 => matches[0].index,
            0 => throw new InvalidDataException($"The PAP has no animation '{name}' with binding {binding}."),
            _ => throw new InvalidDataException($"The PAP has {matches.Length} animations '{name}' with binding {binding}."),
        };
    }

    /// <summary>The embedded timeline (a whole TMLB) of entry <paramref name="index"/>.</summary>
    public byte[] TimelineBytes(int index)
    {
        if (index < 0 || index >= Entries.Length) throw new InvalidDataException($"PAP entry {index} does not exist.");
        var (start, length) = TimelineSpans()[index];
        return bytes[start..(start + length)];
    }

    /// <summary>
    /// Replaces entry <paramref name="index"/>'s timeline. The footer is rebuilt in entry order with
    /// timelines padded to 4 relative to <see cref="TimelineOffset"/> (how the game and VFXEditor walk
    /// it, including the odd offsets of modded PAPs), the last unpadded. Header, Havok and everything
    /// before the replaced timeline stay byte for byte, as do any bytes after the last timeline.
    /// </summary>
    public byte[] WithTimeline(int index, byte[] tmb)
    {
        var spans = TimelineSpans();
        if (index < 0 || index >= spans.Length) throw new InvalidDataException($"PAP entry {index} does not exist.");
        if (tmb.Length < 12 || !tmb.AsSpan(0, 4).SequenceEqual("TMLB"u8) || ReadInt(tmb, 4) != tmb.Length)
            throw new InvalidDataException("The replacement timeline is not a whole TMLB.");
        var (lastStart, lastLength) = spans[^1];
        var footer = new MemoryStream();
        footer.Write(bytes, 0, spans[index].Start);
        for (var i = index; i < spans.Length; i++)
        {
            if (i > index) footer.Write(new byte[(TimelineOffset - (int)footer.Length) & 3]);
            if (i == index) footer.Write(tmb);
            else footer.Write(bytes, spans[i].Start, spans[i].Length);
        }
        footer.Write(bytes, lastStart + lastLength, bytes.Length - lastStart - lastLength);
        if (footer.Length > MaxFileSize) throw new InvalidDataException("The rebuilt PAP exceeds the size limit.");
        var result = footer.ToArray();
        var rebuilt = new AnimationPap(result);
        var walked = rebuilt.TimelineSpans();
        for (var i = 0; i < spans.Length; i++)
            if (!result.AsSpan(walked[i].Start, walked[i].Length).SequenceEqual(i == index ? tmb : bytes.AsSpan(spans[i].Start, spans[i].Length)))
                throw new InvalidDataException("The rebuilt PAP does not hold the expected timelines.");
        return result;
    }

    /// <summary>Where each entry's TMLB lives: back to back from the footer, padded to 4 relative to it, the last unpadded.</summary>
    private (int Start, int Length)[] TimelineSpans()
    {
        var spans = new (int Start, int Length)[Entries.Length];
        var offset = TimelineOffset;
        for (var i = 0; i < spans.Length; i++)
        {
            if (i > 0) offset += (TimelineOffset - offset) & 3;
            if (offset > bytes.Length - 12 || !bytes.AsSpan(offset, 4).SequenceEqual("TMLB"u8))
                throw new InvalidDataException($"The PAP's timeline {i} is missing.");
            var length = ReadInt(bytes, offset + 4);
            if (length < 12 || length > bytes.Length - offset) throw new InvalidDataException($"The PAP's timeline {i} has an invalid size.");
            spans[i] = (offset, length);
            offset += length;
        }
        return spans;
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
