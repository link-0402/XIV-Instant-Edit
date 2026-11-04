using System.Buffers.Binary;
using System.Text;

namespace InstantEdit.Services.Animations.Timeline;

/// <summary>
/// Lays out a face library's PAP from its entries and the merged Havok data, as <see cref="AnimationPap"/> reads it.
/// The header names the library's race (its folder's model), not a source's: a mod library may advertise a borrowed race.
/// </summary>
internal static class FacePapLayout
{
    /// <summary>One face clip: its name, type, face flag and the whole TMLB it carries.</summary>
    public sealed record Entry(string Name, short Type, int Face, byte[] Timeline);

    private const int HeaderSize = 26;
    private const int EntrySize = 40;
    private const int MaxNameBytes = 31;

    /// <summary>
    /// The PAP holding <paramref name="entries"/> in order over <paramref name="havok"/>, entry i bound to Havok binding i.
    /// Timelines go back to back from the footer, padded to 4 relative to it, the last unpadded.
    /// </summary>
    public static byte[] Build(ushort modelId, byte modelType, IReadOnlyList<Entry> entries, byte[] havok)
    {
        if (modelType > 3) throw new InvalidDataException("Unsupported PAP skeleton type.");
        if (entries.Count == 0) throw new InvalidDataException("A face library needs at least one face.");
        if (entries.Count > 4096) throw new InvalidDataException("A face library holds at most 4096 faces.");
        if (havok.Length < 8 || havok.Length > AnimationPap.MaxFileSize) throw new InvalidDataException("Invalid merged Havok size.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var length = Encoding.UTF8.GetByteCount(entry.Name);
            if (length < 1 || length > MaxNameBytes || entry.Name.Contains('\0'))
                throw new InvalidDataException($"The face name '{entry.Name}' must be 1 to {MaxNameBytes} bytes.");
            if (!names.Add(entry.Name)) throw new InvalidDataException($"The face name '{entry.Name}' is used twice.");
            var tmb = entry.Timeline;
            if (tmb.Length < 12 || !tmb.AsSpan(0, 4).SequenceEqual("TMLB"u8) || AnimationPap.ReadInt(tmb, 4) != tmb.Length)
                throw new InvalidDataException($"The timeline of '{entry.Name}' is not a whole TMLB.");
        }

        var havokOffset = HeaderSize + entries.Count * EntrySize;
        var footer = (long)havokOffset + havok.Length;
        footer += -footer & 3;
        var size = footer;
        for (var i = 0; i < entries.Count; i++)
        {
            if (i > 0) size += (footer - size) & 3;
            size += entries[i].Timeline.Length;
        }
        if (size > AnimationPap.MaxFileSize) throw new InvalidDataException("The face library exceeds the size limit.");

        var result = new byte[size];
        "pap "u8.CopyTo(result);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), 0x00020001);
        BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(8), (short)entries.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(10), modelId);
        result[12] = modelType;
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(14), HeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(18), havokOffset);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(22), (int)footer);
        for (var i = 0; i < entries.Count; i++)
        {
            var start = HeaderSize + i * EntrySize;
            Encoding.UTF8.GetBytes(entries[i].Name, result.AsSpan(start, 32));
            BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(start + 32), entries[i].Type);
            BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(start + 34), (short)i);
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(start + 36), entries[i].Face);
        }
        havok.CopyTo(result, havokOffset);
        var offset = footer;
        for (var i = 0; i < entries.Count; i++)
        {
            if (i > 0) offset += (footer - offset) & 3;
            entries[i].Timeline.CopyTo(result, offset);
            offset += entries[i].Timeline.Length;
        }

        // The Havok span runs to the footer, so it holds the padding too, as ReplaceHavok leaves it.
        var built = new AnimationPap(result);
        if (built.ModelId != modelId || built.ModelType != modelType || !built.Havok.AsSpan(0, havok.Length).SequenceEqual(havok) ||
            built.Entries.Length != entries.Count)
            throw new InvalidDataException("The built face library does not hold the expected header or Havok data.");
        for (var i = 0; i < entries.Count; i++)
            if (built.Entries[i] != new AnimationPap.Entry(entries[i].Name, entries[i].Type, (short)i, entries[i].Face) ||
                !built.TimelineBytes(i).AsSpan().SequenceEqual(entries[i].Timeline))
                throw new InvalidDataException("The built face library does not hold the expected faces.");
        return result;
    }
}
