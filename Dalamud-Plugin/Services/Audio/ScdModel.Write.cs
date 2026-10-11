using System.Buffers.Binary;

namespace InstantEdit.Services.Audio;

internal sealed partial class ScdModel
{
    /// <summary>
    /// Lays the file out as VFXEditor writes it: the header, the table header, the sound, track,
    /// audio, layout and attribute tables each padded to 16, then the sections in the file's own
    /// order (layouts, sounds, tracks, attributes, audio in vanilla files), each padded to 16, with
    /// every audio entry padded to 16. Every entry keeps the bytes that followed it. Counts, offsets
    /// and the file size are rewritten.
    /// </summary>
    public byte[] Write()
    {
        Validate();
        using var stream = new MemoryStream();
        var w = new BinaryWriter(stream);
        w.Write(header, 0, ScdFile.TablesOffset);
        w.Write((short)Sounds.Count);
        w.Write((short)Tracks.Count);
        w.Write((short)Audio.Count);
        w.Write(header, ScdFile.TablesOffset + 6, 2);
        w.Write(new byte[20]);
        w.Write(header, ScdFile.TablesOffset + 0x1C, 4);
        var soundTable = Table(w, Sounds.Count);
        var trackTable = Table(w, Tracks.Count);
        var audioTable = Table(w, Audio.Count);
        var writesLayouts = layoutTable || Layouts.Any(l => l != null);
        var layoutTableAt = writesLayouts ? Table(w, Sounds.Count) : 0;
        var attributeTable = Attributes.Count > 0 ? Table(w, Attributes.Count) : 0;
        var offsets = new List<(int Table, int Index, int Offset)>();
        var padding = 0;
        for (var s = 0; s < sectionOrder.Length; s++)
        {
            // Every section starts on 16 bytes; the end is padded only when the original file was.
            Pad(w);
            var last = s == sectionOrder.Length - 1;
            switch (sectionOrder[s])
            {
                case 0:
                    for (var i = 0; i < Layouts.Count; i++)
                        if (Layouts[i] is { } layout)
                        {
                            offsets.Add((layoutTableAt, i, (int)stream.Position));
                            w.Write(layout);
                        }
                    break;
                case 1:
                    for (var i = 0; i < Sounds.Count; i++)
                    {
                        offsets.Add((soundTable, i, (int)stream.Position));
                        WriteSound(w, Sounds[i]);
                    }
                    break;
                case 2:
                    for (var i = 0; i < Tracks.Count; i++)
                    {
                        offsets.Add((trackTable, i, (int)stream.Position));
                        foreach (var command in Tracks[i].Commands)
                        {
                            w.Write((ushort)command.Type);
                            w.Write(command.Payload);
                        }
                        w.Write(Tracks[i].Trailing);
                    }
                    break;
                case 3:
                    for (var i = 0; i < Attributes.Count; i++)
                    {
                        offsets.Add((attributeTable, i, (int)stream.Position));
                        w.Write(Attributes[i]);
                    }
                    break;
                default:
                    var first = true;
                    for (var i = 0; i < Audio.Count; i++)
                        if (!Audio[i].Null)
                        {
                            // Each audio entry starts on 16 bytes; the padding counts toward the entry before it.
                            if (!first)
                                padding += Pad(w);
                            first = false;
                            offsets.Add((audioTable, i, (int)stream.Position));
                            padding += WriteAudio(w, Audio[i]);
                        }
                    if (!first && (!last || alignedEnd))
                        padding += Pad(w);
                    break;
            }
        }
        if (alignedEnd)
            Pad(w);

        var bytes = stream.ToArray();
        foreach (var (table, index, offset) in offsets)
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(table + index * 4), offset);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(ScdFile.TablesOffset + 8), trackTable);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(ScdFile.TablesOffset + 12), audioTable);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(ScdFile.TablesOffset + 16), layoutTableAt);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(ScdFile.TablesOffset + 24), attributeTable);
        var size = sizeRule switch
        {
            SizeRule.Exact => bytes.Length,
            SizeRule.AudioPadding => bytes.Length - Floor16(padding),
            _ => bytes.Length - sizeSlack,
        };
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(ScdFile.FileSizeField), size);
        return bytes;
    }

    private static void WriteSound(BinaryWriter w, ScdModelSound sound)
    {
        w.Write((byte)sound.Entries.Count);
        w.Write(sound.Head, 1, 15);
        w.Write(sound.Blocks);
        foreach (var entry in sound.Entries)
        {
            w.Write((short)entry.Track);
            w.Write((short)entry.Audio);
            if (sound.Random)
            {
                w.Write(entry.Limit);
                w.Write(entry.Order);
            }
        }
        w.Write(sound.Tail);
        w.Write(sound.Trailing);
    }

    /// <summary>Writes an audio entry and returns the bytes after its data, for the size field.</summary>
    private static int WriteAudio(BinaryWriter w, ScdModelAudio audio)
    {
        var entryHeader = audio.Header.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(entryHeader, audio.Data.Length);
        BinaryPrimitives.WriteInt32LittleEndian(entryHeader.AsSpan(24), audio.SubInfo.Length);
        w.Write(entryHeader);
        w.Write(audio.SubInfo);
        w.Write(audio.Data);
        w.Write(audio.Trailing);
        return audio.Trailing.Length;
    }

    /// <summary>A deep copy: editing it never changes this model.</summary>
    public ScdModel Clone() => new()
    {
        Sounds = [.. Sounds.Select(s => s.Clone())],
        Layouts = [.. Layouts.Select(l => l?.ToArray())],
        Tracks = [.. Tracks.Select(t => t.Clone())],
        Audio = [.. Audio.Select(a => a.Clone())],
        Attributes = [.. Attributes.Select(a => a.ToArray())],
        header = header.ToArray(),
        layoutTable = layoutTable,
        sectionOrder = sectionOrder.ToArray(),
        alignedEnd = alignedEnd,
        sizeRule = sizeRule,
        sizeSlack = sizeSlack,
    };

    private void Validate()
    {
        if (Sounds.Count > short.MaxValue || Tracks.Count > short.MaxValue || Audio.Count > short.MaxValue || Sounds.Count + Tracks.Count + Audio.Count > ScdFile.MaxEntries)
            throw new InvalidDataException("The SCD has too many entries.");
        if (Layouts.Count != Sounds.Count)
            throw new InvalidDataException("Every SCD sound needs its layout slot.");
        foreach (var sound in Sounds)
        {
            if (sound.Entries.Count > byte.MaxValue)
                throw new InvalidDataException("An SCD sound can play at most 255 tracks.");
            foreach (var entry in sound.Entries)
                if (entry.Track < -1 || entry.Track >= Tracks.Count || entry.Audio < -1 || entry.Audio >= Audio.Count)
                    throw new InvalidDataException("An SCD sound entry names a track or audio entry that does not exist.");
        }
    }

    private static int Table(BinaryWriter w, int count)
    {
        var at = (int)w.BaseStream.Position;
        w.Write(new byte[count * 4]);
        Pad(w);
        return at;
    }

    /// <summary>Pads with zeros to the next multiple of 16 and returns how many bytes it added.</summary>
    private static int Pad(BinaryWriter w)
    {
        var added = (int)(-w.BaseStream.Position & 15);
        w.Write(new byte[added]);
        return added;
    }
}
