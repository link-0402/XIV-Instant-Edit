using System.Buffers.Binary;

namespace InstantEdit.Services.Audio;

/// <summary>One track command: its 16-bit type and its raw payload.</summary>
internal sealed record ScdCommand(int Type, byte[] Payload);

/// <summary>A track: its commands up to and including the end command, then the bytes before the next entry.</summary>
internal sealed class ScdModelTrack
{
    public List<ScdCommand> Commands { get; set; } = [];
    public byte[] Trailing { get; set; } = [];

    public ScdModelTrack Clone() => new() { Commands = [.. Commands.Select(c => c with { Payload = c.Payload.ToArray() })], Trailing = Trailing.ToArray() };
}

/// <summary>
/// An audio entry: the 32-byte header, the sub-info (marker block and codec header) and the coded
/// data, all raw, then the bytes before the next entry. <see cref="Null"/> is a table slot holding
/// offset 0. <see cref="Seconds"/> is what <see cref="ScdFile"/> computes for the entry.
/// </summary>
internal sealed class ScdModelAudio
{
    public bool Null { get; init; }
    public byte[] Header { get; set; } = new byte[ScdAudio.HeaderSize];
    public byte[] SubInfo { get; set; } = [];
    public byte[] Data { get; set; } = [];
    public byte[] Trailing { get; set; } = [];
    public double Seconds { get; set; }

    public ScdCodec Codec => (ScdCodec)BinaryPrimitives.ReadInt32LittleEndian(Header.AsSpan(12));
    public int Channels => BinaryPrimitives.ReadInt32LittleEndian(Header.AsSpan(4));
    public int SampleRate => BinaryPrimitives.ReadInt32LittleEndian(Header.AsSpan(8));
    public int LoopStart { get => BinaryPrimitives.ReadInt32LittleEndian(Header.AsSpan(16)); set => BinaryPrimitives.WriteInt32LittleEndian(Header.AsSpan(16), value); }
    public int LoopEnd { get => BinaryPrimitives.ReadInt32LittleEndian(Header.AsSpan(20)); set => BinaryPrimitives.WriteInt32LittleEndian(Header.AsSpan(20), value); }

    public ScdModelAudio Clone() => new()
    {
        Null = Null, Header = Header.ToArray(), SubInfo = SubInfo.ToArray(), Data = Data.ToArray(), Trailing = Trailing.ToArray(), Seconds = Seconds,
    };
}

/// <summary>One entry of a sound: the track and audio it plays, and for random types its limit and order.</summary>
internal readonly record struct ScdModelEntry(int Track, int Audio, short Limit = 0, short Order = 0);

/// <summary>
/// A sound entry: the 16-byte head (track count, bus, priority, type, attributes, volume, local
/// number, user id, play history), the optional blocks raw (<see cref="ExtraAt"/> is the extra
/// block's offset within them, or -1), the entries, the Cycle tail, then the bytes before the next
/// entry. The head's track count is rewritten from <see cref="Entries"/> on write.
/// </summary>
internal sealed class ScdModelSound
{
    public byte[] Head { get; set; } = new byte[16];
    public byte[] Blocks { get; set; } = [];
    public int ExtraAt { get; set; } = -1;
    public List<ScdModelEntry> Entries { get; set; } = [];
    public byte[] Tail { get; set; } = [];
    public byte[] Trailing { get; set; } = [];

    public int Bus { get => Head[1]; set => Head[1] = checked((byte)value); }
    public int Priority { get => Head[2]; set => Head[2] = checked((byte)value); }

    /// <summary>The sound type. Changing whether the entry has an empty-loop block is refused.</summary>
    public int Type
    {
        get => Head[3];
        set
        {
            if ((value == ScdSound.Empty) != (Type == ScdSound.Empty) && (Attributes & ScdSound.LoopAttribute) != 0)
                throw new InvalidOperationException("A looping sound can't change to or from the empty type.");
            Head[3] = checked((byte)value);
        }
    }

    /// <summary>The attribute flags. Changing the bits that add or remove an optional block is refused.</summary>
    public int Attributes
    {
        get => BinaryPrimitives.ReadInt32LittleEndian(Head.AsSpan(4));
        set
        {
            if (((value ^ Attributes) & ScdSound.BlockAttributes) != 0 || (Type == ScdSound.Empty && ((value ^ Attributes) & ScdSound.LoopAttribute) != 0))
                throw new InvalidOperationException("A sound's attributes can't add or remove its optional blocks.");
            BinaryPrimitives.WriteInt32LittleEndian(Head.AsSpan(4), value);
        }
    }

    public float Volume { get => BinaryPrimitives.ReadSingleLittleEndian(Head.AsSpan(ScdSound.VolumeField)); set => BinaryPrimitives.WriteSingleLittleEndian(Head.AsSpan(ScdSound.VolumeField), value); }
    public bool Random => ScdFile.IsRandom(Type);

    /// <summary>The extra block's play length in milliseconds, or null when the sound has no extra block.</summary>
    public int? PlayLength
    {
        get => ExtraAt < 0 ? null : BinaryPrimitives.ReadInt32LittleEndian(Blocks.AsSpan(ExtraAt + 4));
        set
        {
            if (ExtraAt < 0 || value == null)
                throw new InvalidOperationException("This sound has no play length to set.");
            BinaryPrimitives.WriteInt32LittleEndian(Blocks.AsSpan(ExtraAt + 4), value.Value);
        }
    }

    public ScdModelSound Clone() => new()
    {
        Head = Head.ToArray(), Blocks = Blocks.ToArray(), ExtraAt = ExtraAt, Entries = [.. Entries], Tail = Tail.ToArray(), Trailing = Trailing.ToArray(),
    };
}

/// <summary>
/// A whole SCD as editable parts: sounds, their layouts (one per sound, raw), tracks, audio and
/// attributes (raw). Every byte the parts don't name is kept, so an unedited model writes the file
/// back byte for byte. Entries whose stated data length runs past the file are normalised to what
/// the file holds, as <see cref="ScdFile"/> reads them. Each section (layouts, sounds, tracks,
/// attributes, audio) is written where the file had it; sections that run into each other are
/// refused. Dalamud-free.
/// </summary>
internal sealed partial class ScdModel
{
    private const string Unsupported = "This SCD's layout is not supported.";

    public List<ScdModelSound> Sounds { get; set; } = [];
    public List<byte[]?> Layouts { get; set; } = [];
    public List<ScdModelTrack> Tracks { get; set; } = [];
    public List<ScdModelAudio> Audio { get; set; } = [];
    public List<byte[]> Attributes { get; set; } = [];

    /// <summary>The file header and the table header; counts, offsets and the file size are rewritten on write.</summary>
    private byte[] header = new byte[ScdFile.SoundTableOffset];
    private bool layoutTable = true;
    /// <summary>The order the sections are written in: 0 layouts, 1 sounds, 2 tracks, 3 attributes, 4 audio.</summary>
    private int[] sectionOrder = [0, 1, 2, 3, 4];
    /// <summary>Whether the file ends on 16 bytes; 136 library files don't, and keep their length.</summary>
    private bool alignedEnd = true;
    private SizeRule sizeRule;
    private int sizeSlack;

    /// <summary>
    /// How the file size field relates to the file: the whole length, the length less the audio
    /// entries' padding rounded down to 16 (VFXEditor's rule), or less a fixed amount.
    /// </summary>
    private enum SizeRule { Exact, AudioPadding, Slack }

    public float? MaxRange(int sound) => PointFloat(sound, 48);
    public float? MinRange(int sound) => PointFloat(sound, 52);

    private float? PointFloat(int sound, int at)
        => sound >= 0 && sound < Layouts.Count && Layouts[sound] is { Length: >= 56 } layout && layout[2] == PointLayout
            ? BinaryPrimitives.ReadSingleLittleEndian(layout.AsSpan(at)) : null;

    internal const int PointLayout = 3;

    public static ScdModel Read(byte[] bytes)
    {
        var file = ScdFile.Read(bytes);
        var model = new ScdModel { header = bytes[..ScdFile.SoundTableOffset], alignedEnd = bytes.Length % 16 == 0 };
        var layoutTableOffset = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(ScdFile.TablesOffset + 16));
        model.layoutTable = layoutTableOffset != 0;
        if (file.TrackOffsets.Any(o => o == 0))
            throw new InvalidDataException(Unsupported);
        var sections = new[]
        {
            file.LayoutOffsets.Where(o => o != 0).ToArray(),
            file.Sounds.Select(s => s.Offset).ToArray(),
            file.TrackOffsets.ToArray(),
            file.AttributeOffsets.ToArray(),
            file.Audio.Where(a => a.Offset != 0).Select(a => a.Offset).ToArray(),
        };
        var starts = sections.SelectMany(s => s).Order().ToArray();
        if (starts.Distinct().Count() != starts.Length)
            throw new InvalidDataException(Unsupported);
        // Vanilla files and VFXEditor write layouts first; some mod tools write sounds and tracks first.
        // Either is kept, but a section may not run into another one.
        model.sectionOrder = [.. Enumerable.Range(0, sections.Length).OrderBy(s => sections[s].Length > 0 ? sections[s].Min() : int.MaxValue).ThenBy(s => s)];
        var present = model.sectionOrder.Select(s => sections[s]).Where(s => s.Length > 0).ToArray();
        for (var i = 1; i < present.Length; i++)
            if (present[i - 1].Max() > present[i].Min())
                throw new InvalidDataException(Unsupported);
        int Next(int offset)
        {
            var i = Array.BinarySearch(starts, offset);
            return i + 1 < starts.Length ? starts[i + 1] : bytes.Length;
        }
        byte[] Rest(int end, int offset) => end <= Next(offset) ? bytes[end..Next(offset)] : throw new InvalidDataException(Unsupported);

        model.Layouts = model.layoutTable ? [.. file.LayoutOffsets.Select(o => o == 0 ? null : bytes[o..Next(o)])] : [.. file.Sounds.Select(_ => (byte[]?)null)];
        model.Attributes = [.. file.AttributeOffsets.Select(o => bytes[o..Next(o)])];
        foreach (var offset in file.TrackOffsets)
        {
            var commands = ScdTrack.Read(bytes, offset);
            model.Tracks.Add(new ScdModelTrack
            {
                Commands = [.. commands.Select(c => new ScdCommand(c.Type, bytes[c.Data..c.Next]))],
                Trailing = Rest(commands[^1].Next, offset),
            });
        }
        foreach (var audio in file.Audio)
        {
            if (audio.Offset == 0)
            {
                model.Audio.Add(new ScdModelAudio { Null = true });
                continue;
            }
            var entryHeader = bytes[audio.Offset..(audio.Offset + ScdAudio.HeaderSize)];
            BinaryPrimitives.WriteInt32LittleEndian(entryHeader, audio.DataLength);
            model.Audio.Add(new ScdModelAudio
            {
                Header = entryHeader,
                SubInfo = bytes[(audio.Offset + ScdAudio.HeaderSize)..audio.DataOffset],
                Data = bytes[audio.DataOffset..audio.End],
                Trailing = Rest(audio.End, audio.Offset),
                Seconds = audio.Seconds,
            });
        }
        foreach (var sound in file.Sounds)
        {
            var head = bytes[sound.Offset..(sound.Offset + 16)];
            var (entries, extra) = ScdFile.SoundBlocks(bytes, sound.Offset + 16, sound.Attributes, sound.Type);
            var stride = ScdFile.IsRandom(sound.Type) ? 8 : 4;
            var list = new List<ScdModelEntry>(sound.Tracks.Length);
            for (var i = 0; i < sound.Tracks.Length; i++)
            {
                var at = entries + i * stride;
                list.Add(stride == 8
                    ? new ScdModelEntry(sound.Tracks[i].Track, sound.Tracks[i].Audio, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(at + 4)), BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(at + 6)))
                    : new ScdModelEntry(sound.Tracks[i].Track, sound.Tracks[i].Audio));
            }
            var tail = entries + sound.Tracks.Length * stride;
            var end = tail + (sound.Type == ScdSound.Cycle ? 8 : 0);
            if (end > bytes.Length)
                throw new InvalidDataException("An SCD sound entry runs past the end of the file.");
            model.Sounds.Add(new ScdModelSound
            {
                Head = head,
                Blocks = bytes[(sound.Offset + 16)..entries],
                ExtraAt = extra < 0 ? -1 : extra - sound.Offset - 16,
                Entries = list,
                Tail = bytes[tail..end],
                Trailing = Rest(end, sound.Offset),
            });
        }
        var declared = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(ScdFile.FileSizeField));
        if (declared == bytes.Length)
            model.sizeRule = SizeRule.Exact;
        else if (declared == bytes.Length - Floor16(model.Audio.Sum(a => a.Trailing.Length)))
            model.sizeRule = SizeRule.AudioPadding;
        else
        {
            model.sizeRule = SizeRule.Slack;
            model.sizeSlack = bytes.Length - declared;
        }
        return model;
    }

    private static int Floor16(int value) => value - value % 16;
}
