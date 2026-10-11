using System.Buffers.Binary;
using InstantEdit.Services.Audio;

/// <summary>The structural SCD model: byte-for-byte round trips, typed reads and refusals.</summary>
internal static class ScdModelFixture
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        RoundTrip(check, reject);
    }

    private static void RoundTrip(Action<bool, string> check, Action<Action, string> reject)
    {
        foreach (var (name, bytes) in new[] { ("one-shot", AudioFixture.Template()), ("marker", AudioFixture.Template(marker: true)),
                     ("random", RandomSet(3)), ("shared track", RandomSet(4, sharedTrack: true)), ("vorbis", RandomSet(3, vorbisLast: true)),
                     ("cycle", RandomSet(2, cycle: true)), ("every sound block", RandomSet(2, blocks: true)),
                     ("sounds and tracks before layouts", RandomSet(3, toolOrder: true)) })
            check(ScdModel.Read(bytes).Write().AsSpan().SequenceEqual(bytes), $"{name}: ScdModel writes the file back byte for byte");
        // 136 library files end without padding to 16; they come back as they were.
        var unaligned = RandomSet(2);
        unaligned = unaligned[..(ScdFile.Read(unaligned).Audio[^1].End)];
        BinaryPrimitives.WriteInt32LittleEndian(unaligned.AsSpan(ScdFile.FileSizeField), unaligned.Length);
        check(ScdModel.Read(unaligned).Write().AsSpan().SequenceEqual(unaligned), "a file that doesn't end on 16 bytes is written back byte for byte");
        // Some tools state a data length that runs past the file: it is written as what the file holds.
        var overstated = AudioFixture.Template();
        var audioAt = ScdFile.Read(overstated).Audio[0].Offset;
        BinaryPrimitives.WriteInt32LittleEndian(overstated.AsSpan(audioAt), ScdFile.Read(overstated).Audio[0].DataLength + 0x20 + 50);
        var normalised = ScdModel.Read(overstated).Write();
        check(ScdFile.Read(normalised).Audio[0] is { Cut: false } fixedAudio && fixedAudio.End == normalised.Length && normalised.Length == overstated.Length &&
              Enumerable.Range(0, normalised.Length).Where(i => normalised[i] != overstated[i]).All(i => i >= audioAt && i < audioAt + 4),
            "a data length that runs past the file is written as what the file holds, and nothing else changes");
        var model = ScdModel.Read(RandomSet(3));
        check(model.Sounds[0] is { Type: 12, Entries.Count: 3 } s && s.Entries[2] == new ScdModelEntry(2, 3, 100, 2) && s.PlayLength != null,
            "a random set reads its entries, limits, order and play length");
        check(model.Audio[1] is { Null: false, Codec: ScdCodec.Empty } && model.Audio[0].Seconds > 0, "empty audio slots read as empty");
        check(model.MaxRange(0) == MaxRange && model.MinRange(0) == MinRange, "a Point layout's ranges read");
        check(ScdModel.Read(RandomSet(2, cycle: true)).Sounds[0] is { Type: 4, Random: true, Tail.Length: 8 }, "a Cycle sound keeps its tail");
        reject(() => ScdModel.Read(AudioFixture.Template()[..0x200]), "a cut file is refused");
        var bad = RandomSet(2);
        BinaryPrimitives.WriteInt16LittleEndian(bad.AsSpan(ScdFile.Read(bad).Sounds[0].Offset + 32 + 8 + 2), 99);
        reject(() => ScdModel.Read(bad), "an entry naming a missing audio entry is refused");
        reject(() => ScdModel.Read(Interleaved()), "a file whose sections are interleaved is refused");
        var tool = ScdModel.Read(RandomSet(3, toolOrder: true));
        tool.Tracks[0].Commands.Insert(tool.Tracks[0].Commands.Count - 1, new ScdCommand(ScdTrack.Volume, new byte[8]));
        var toolWritten = ScdFile.Read(tool.Write());
        check(toolWritten.Sounds[0].Offset < toolWritten.TrackOffsets[0] && toolWritten.TrackOffsets[^1] < toolWritten.LayoutOffsets[0] &&
              ScdModel.Read(tool.Write()).MaxRange(0) == MaxRange, "a file keeps its own section order when written");
        var clone = model.Clone();
        clone.Sounds[0].Volume = 0.25f;
        clone.Tracks[0].Commands.RemoveAt(0);
        check(model.Sounds[0].Volume != 0.25f && model.Tracks[0].Commands.Count == clone.Tracks[0].Commands.Count + 1, "Clone is deep");
        var broken = ScdModel.Read(RandomSet(2));
        broken.Sounds[0].Entries[1] = broken.Sounds[0].Entries[1] with { Audio = 99 };
        reject(() => broken.Write(), "Write refuses an entry naming a missing audio entry");
        // A track that grows moves everything after it; the tables, counts and size follow.
        var grown = ScdModel.Read(RandomSet(3));
        var before = ScdFile.Read(RandomSet(3));
        grown.Tracks[0].Commands.Insert(grown.Tracks[0].Commands.Count - 1, new ScdCommand(ScdTrack.Volume, new byte[8]));
        var written = grown.Write();
        var after = ScdFile.Read(written);
        check(after.TrackOffsets[1] > before.TrackOffsets[1] && after.Audio.Select(a => a.Seconds).SequenceEqual(before.Audio.Select(a => a.Seconds)) &&
              BinaryPrimitives.ReadInt32LittleEndian(written.AsSpan(ScdFile.FileSizeField)) == written.Length && written.Length % 16 == 0 &&
              ScdModel.Read(written).Tracks[0].Commands.Count == ScdModel.Read(RandomSet(3)).Tracks[0].Commands.Count + 1,
            "a grown track moves the entries after it, and the tables and file size follow");
    }

    // ---- Synthetic random sets ------------------------------------------------------------------

    internal const float MaxRange = 10, MinRange = 30;

    /// <summary>
    /// One GroupRandom sound (or Cycle with its 8-byte tail) of <paramref name="alternatives"/>
    /// entries with running-total limits 100*(i+1)/n and order i, an extra block, a Point layout
    /// (max range 10, min range 30), one canonical track per alternative (the last two share one
    /// with <paramref name="sharedTrack"/>) and one MS-ADPCM entry per alternative, each a different
    /// length (Vorbis last with <paramref name="vorbisLast"/>). An empty audio slot sits at index 1,
    /// so the entries play audio 0, 2, 3 and so on. <paramref name="blocks"/> adds every optional
    /// sound block. Laid out as VFXEditor writes: tables padded to 16, then layouts, sounds, tracks
    /// and attributes packed with each section padded to 16, then audio entries each padded to 16.
    /// <paramref name="toolOrder"/> writes sounds and tracks before the layouts, as some mod tools do.
    /// </summary>
    internal static byte[] RandomSet(int alternatives, bool sharedTrack = false, bool vorbisLast = false, bool cycle = false, bool blocks = false, bool toolOrder = false)
    {
        var n = alternatives;
        var real = Enumerable.Range(0, n).Select(i => vorbisLast && i == n - 1 ? VorbisAudio(48000L * (i + 2)) : AdpcmAudio(1500 * (i + 1) + 300)).ToList();
        var audio = real.ToList();
        audio.Insert(Math.Min(1, audio.Count), EmptyAudio());
        var audioOf = Enumerable.Range(0, n).Select(i => i == 0 ? 0 : i + 1).ToArray();
        var milliseconds = audioOf.Select(a => (int)Math.Ceiling(ScdFile.Read(Single(audio[a])).Audio[0].Seconds * 1000)).ToArray();
        var trackOf = Enumerable.Range(0, n).Select(i => sharedTrack && n > 1 && i == n - 1 ? n - 2 : i).ToArray();
        var tracks = Enumerable.Range(0, trackOf.Max() + 1).Select(t => TemplateTrack(milliseconds[Array.IndexOf(trackOf, t)])).ToList();

        var sound = new MemoryStream();
        var sw = new BinaryWriter(sound);
        var attributes = 0x2000 | (blocks ? 0x0100 | 0x0400 | 0x0800 | 0x8000 | 0x0040 : 0);
        sw.Write((byte)n);
        sw.Write((byte)2);
        sw.Write((byte)99);
        sw.Write((byte)(cycle ? 4 : 12));
        sw.Write(attributes);
        sw.Write(0.5f);
        sw.Write((short)0);
        sw.Write((byte)0);
        sw.Write((byte)0xFF);
        if (blocks)
        {
            sw.Write(Block16(b => BinaryPrimitives.WriteInt32LittleEndian(b, 16)));   // routing: 32-bit size
            sw.Write(Block16(b => b[0] = 16));                                        // bus ducking: size in the first byte
            sw.Write(Block16(b => b[1] = 16));                                        // acceleration: size in the second byte
            sw.Write(Block16(b => BinaryPrimitives.WriteInt16LittleEndian(b[2..], 16))); // atomos: 16-bit size at +2
        }
        sw.Write(Block16(b =>
        {
            BinaryPrimitives.WriteInt16LittleEndian(b[2..], 16);
            BinaryPrimitives.WriteInt32LittleEndian(b[4..], milliseconds.Max());
            BinaryPrimitives.WriteInt32LittleEndian(b[8..], 2);
        }));
        if (blocks)
            sw.Write(Enumerable.Range(0, 32).Select(i => (byte)(i + 1)).ToArray());
        for (var i = 0; i < n; i++)
        {
            sw.Write((short)trackOf[i]);
            sw.Write((short)audioOf[i]);
            sw.Write((short)(100 * (i + 1) / n));
            sw.Write((short)i);
        }
        if (cycle)
        {
            sw.Write(500);
            sw.Write((short)1);
            sw.Write((short)1);
        }

        var layout = new byte[0x80];
        BinaryPrimitives.WriteUInt16LittleEndian(layout, 0x80);
        layout[2] = 3;
        layout[3] = 1;
        layout[4] = 4;
        BinaryPrimitives.WriteSingleLittleEndian(layout.AsSpan(48), MaxRange);
        BinaryPrimitives.WriteSingleLittleEndian(layout.AsSpan(52), MinRange);
        var attribute = new byte[0x20];
        attribute[0] = 2;

        var file = new MemoryStream();
        var w = new BinaryWriter(file);
        w.Write("SEDBSSCF"u8);
        w.Write(3);
        w.Write((byte)0);
        w.Write((byte)4);
        w.Write((short)0x30);
        w.Write(0);
        w.Write(new byte[0x30 - 0x14]);
        w.Write((short)1);
        w.Write((short)tracks.Count);
        w.Write((short)audio.Count);
        w.Write((short)4999);
        w.Write(new byte[0x20 - 8]);
        var soundTable = Table(w, 1);
        var trackTable = Table(w, tracks.Count);
        var audioTable = Table(w, audio.Count);
        var layoutTable = Table(w, 1);
        var attributeTable = Table(w, 1);
        var bytes = new List<(int Table, int Index, int Offset)>();
        void Layouts()
        {
            bytes.Add((layoutTable, 0, (int)file.Position));
            w.Write(layout);
            Pad(w);
        }
        void Sounds()
        {
            bytes.Add((soundTable, 0, (int)file.Position));
            w.Write(sound.ToArray());
            Pad(w);
        }
        void Tracks()
        {
            for (var t = 0; t < tracks.Count; t++)
            {
                bytes.Add((trackTable, t, (int)file.Position));
                w.Write(tracks[t]);
            }
            Pad(w);
        }
        // Some mod tools write sounds and tracks before the layouts.
        foreach (var section in toolOrder ? new Action[] { Sounds, Tracks, Layouts } : [Layouts, Sounds, Tracks])
            section();
        bytes.Add((attributeTable, 0, (int)file.Position));
        w.Write(attribute);
        Pad(w);
        for (var a = 0; a < audio.Count; a++)
        {
            bytes.Add((audioTable, a, (int)file.Position));
            w.Write(audio[a]);
            Pad(w);
        }
        var result = file.ToArray();
        foreach (var (table, index, offset) in bytes)
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(table + index * 4), offset);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(0x38), trackTable);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(0x3C), audioTable);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(0x40), layoutTable);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(0x48), attributeTable);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(ScdFile.FileSizeField), result.Length);
        return result;
    }

    /// <summary>
    /// RandomSet(1) with its layout and then its empty audio slot appended, so the layout sits
    /// between the two audio entries: readable by ScdFile, refused by ScdModel.
    /// </summary>
    private static byte[] Interleaved()
    {
        var bytes = RandomSet(1);
        var layoutTable = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0x40));
        var audioTable = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0x3C));
        var layout = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(layoutTable));
        byte[] moved = [.. bytes, .. bytes.AsSpan(layout, 0x80), .. EmptyAudio()];
        BinaryPrimitives.WriteInt32LittleEndian(moved.AsSpan(layoutTable), bytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(moved.AsSpan(audioTable + 4), bytes.Length + 0x80);
        BinaryPrimitives.WriteInt32LittleEndian(moved.AsSpan(ScdFile.FileSizeField), moved.Length);
        return moved;
    }

    private static int Table(BinaryWriter w, int count)
    {
        var at = (int)w.BaseStream.Position;
        w.Write(new byte[count * 4]);
        Pad(w);
        return at;
    }

    private static void Pad(BinaryWriter w)
    {
        while (w.BaseStream.Position % 16 != 0)
            w.Write((byte)0);
    }

    private static byte[] Block16(SpanAction fill)
    {
        var block = new byte[16];
        fill(block);
        return block;
    }

    private delegate void SpanAction(Span<byte> bytes);

    /// <summary>The template's canonical track (KeyOn, Interval, KeyOff, End) with its wait set.</summary>
    private static byte[] TemplateTrack(int intervalMs)
    {
        var template = AudioFixture.Template();
        var at = ScdFile.Read(template).TrackOffsets[0];
        var commands = ScdTrack.Read(template, at);
        var track = template[at..commands[^1].Next];
        BinaryPrimitives.WriteInt32LittleEndian(track.AsSpan(commands.First(c => c.Type == ScdTrack.Interval).Data - at), intervalMs);
        return track;
    }

    private static byte[] AdpcmAudio(int frames)
    {
        var samples = Enumerable.Range(0, frames).Select(f => 0.3f * MathF.Sin(f * 0.05f)).ToArray();
        var data = MsAdpcm.Encode(MsAdpcm.ToPcm16(samples), 1);
        var header = AudioHeader(data.Length, 1, 44100, ScdCodec.MsAdpcm, 50, 0x01000000);
        return [.. header, .. MsAdpcm.FormatHeader(1, 44100), .. data];
    }

    private static byte[] EmptyAudio() => AudioHeader(0, 0, 0, ScdCodec.Empty, 0, 0);

    /// <summary>A Vorbis entry (mode 0) whose data holds two fake Ogg pages; the last one's granule is the length.</summary>
    private static byte[] VorbisAudio(long granule)
    {
        var data = new byte[300];
        "OggS"u8.CopyTo(data.AsSpan(10));
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(16), granule / 2);
        "OggS"u8.CopyTo(data.AsSpan(200));
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(206), granule);
        var info = new byte[0x20];
        BinaryPrimitives.WriteInt32LittleEndian(info.AsSpan(20), 40);
        return [.. AudioHeader(data.Length, 2, 48000, ScdCodec.Vorbis, info.Length + 40, 0), .. info, .. new byte[40], .. data];
    }

    private static byte[] AudioHeader(int dataLength, int channels, int rate, ScdCodec codec, int subInfo, int flags)
    {
        var header = new byte[0x20];
        BinaryPrimitives.WriteInt32LittleEndian(header, dataLength);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), channels);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), rate);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), (int)codec);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24), subInfo);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28), flags);
        return header;
    }

    /// <summary>One audio entry wrapped in a minimal SCD, to read its length through ScdFile.</summary>
    private static byte[] Single(byte[] entry)
    {
        var template = AudioFixture.Template();
        var at = ScdFile.Read(template).Audio[0].Offset;
        byte[] bytes = [.. template[..at], .. entry];
        Array.Resize(ref bytes, (bytes.Length + 15) & ~15);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(ScdFile.FileSizeField), bytes.Length);
        return bytes;
    }
}
