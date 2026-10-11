using System.Buffers.Binary;
using System.Collections.Immutable;

namespace InstantEdit.Services.Audio;

/// <summary>An SCD audio entry's codec, as its format field stores it.</summary>
internal enum ScdCodec { Empty = -1, Pcm = 0x01, Atrac3 = 0x05, Vorbis = 0x06, Xma = 0x0B, MsAdpcm = 0x0C, Atrac3Too = 0x0D, Hca = 0x1A }

/// <summary>
/// One audio entry: a 32-byte header, its sub-info (an optional marker block, then the codec's own
/// header) and the coded data. <see cref="Seconds"/> is 0 when the codec's length is unknown.
/// <see cref="Cut"/> marks an entry whose stated data length ran past the end of the file;
/// <see cref="DataLength"/> is then what the file holds.
/// </summary>
internal sealed record ScdAudio(int Offset, ScdCodec Codec, int Channels, int SampleRate, int DataLength,
    int LoopStart, int LoopEnd, int SubInfoSize, int Flags, double Seconds, bool Cut = false)
{
    public const int HeaderSize = 0x20;
    public const int MarkerFlag = 0x1;
    public bool HasMarker => (Flags & MarkerFlag) != 0;
    public int DataOffset => Offset + HeaderSize + SubInfoSize;
    public int End => DataOffset + DataLength;
}

/// <summary>
/// One sound entry, what a timeline sound event's sound index picks. <see cref="ExtraOffset"/> is
/// the extra block holding the play length in milliseconds, or -1. <see cref="Tracks"/> pairs each
/// track index with the audio entry it plays.
/// </summary>
internal sealed record ScdSound(int Offset, int Type, int Attributes, float Volume, int ExtraOffset,
    ImmutableArray<(int Track, int Audio)> Tracks, double Seconds)
{
    public const int Normal = 1;
    public const int Cycle = 4;
    public const int GroupRandom = 12;
    public const int Empty = 15;
    public const int LoopAttribute = 0x0001;
    /// <summary>The attribute bits that add an optional block to the entry: routing, bus ducking, acceleration, atomos, extra, bypass.</summary>
    public const int BlockAttributes = 0x0100 | 0x0400 | 0x0800 | 0x8000 | 0x2000 | 0x0040;
    public const int VolumeField = 8;
    public bool Loops => (Attributes & LoopAttribute) != 0;
}

/// <summary>What a picker shows for an SCD: each sound entry's length in seconds, by sound index.</summary>
internal sealed record ScdInfo(ImmutableArray<double> SoundSeconds)
{
    public int SoundCount => SoundSeconds.Length;
}

/// <summary>
/// SCD ("SEDBSSCF") reader. Layout follows VFXEditor's ScdFile, ScdReader, ScdHeader,
/// ScdAudioEntry and ScdSoundEntry: a 0x30-byte header, a 0x20-byte table header (sound, track and
/// audio counts, then the track, audio, layout, routing and attribute table offsets), the offset
/// tables, then the entries. Every one of 8,592 vanilla SCDs surveyed on 2026-10-02 is version 3,
/// little endian, and stores layouts, sounds, tracks and attributes before the audio entries.
/// Dalamud-free.
/// </summary>
internal sealed class ScdFile
{
    public const int HeaderSize = 0x30;
    public const int TablesOffset = 0x30;
    public const int SoundTableOffset = 0x50;
    public const int FileSizeField = 0x10;
    public const int MaxEntries = 4096;
    private const int AttributeRouting = 0x0100, AttributeBusDucking = 0x0400, AttributeAcceleration = 0x0800;
    private const int AttributeAtomos = 0x8000, AttributeExtra = 0x2000, AttributeBypass = 0x0040;
    private const int TypeRandom = 2, TypeCycle = 4, TypeGroupRandom = 12, TypeGroupOrder = 13, TypeEmpty = 15;

    public int Version { get; }
    public int DeclaredSize { get; }
    public ImmutableArray<ScdSound> Sounds { get; }
    public ImmutableArray<int> TrackOffsets { get; }
    public ImmutableArray<ScdAudio> Audio { get; }
    public ImmutableArray<int> LayoutOffsets { get; }
    public ImmutableArray<int> AttributeOffsets { get; }
    public int TrackTableOffset { get; }
    public int AudioTableOffset { get; }

    public static bool IsScd(ReadOnlySpan<byte> bytes) => bytes.Length >= 8 && bytes[..8].SequenceEqual("SEDBSSCF"u8);

    public static ScdInfo Info(byte[] bytes) => new([.. Read(bytes).Sounds.Select(s => s.Seconds)]);

    /// <summary>
    /// A sound's length for drawing its clip (the catalog's seconds lookup), or null when the bytes
    /// are not a readable SCD, the index does not exist or the codec's length is unknown.
    /// </summary>
    public static float? TrySeconds(byte[] bytes, int soundIndex = 0)
    {
        try
        {
            var sounds = Read(bytes).Sounds;
            return soundIndex >= 0 && soundIndex < sounds.Length && sounds[soundIndex].Seconds > 0 ? (float)sounds[soundIndex].Seconds : null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    public static ScdFile Read(byte[] bytes) => new(bytes);

    private ScdFile(byte[] bytes)
    {
        if (!IsScd(bytes))
            throw new InvalidDataException("This is not an SCD sound file.");
        if (bytes.Length < SoundTableOffset)
            throw new InvalidDataException("The SCD file is cut short.");
        if (bytes[12] != 0)
            throw new InvalidDataException("Big-endian SCD files are not supported.");
        if (I16(bytes, 14) != HeaderSize)
            throw new InvalidDataException("The SCD header size is not 0x30.");
        Version = I32(bytes, 8);
        DeclaredSize = I32(bytes, FileSizeField);
        int soundCount = I16(bytes, TablesOffset), trackCount = I16(bytes, TablesOffset + 2), audioCount = I16(bytes, TablesOffset + 4);
        if (soundCount < 0 || trackCount < 0 || audioCount < 0 || soundCount + trackCount + audioCount > MaxEntries)
            throw new InvalidDataException("The SCD entry counts are invalid.");
        TrackTableOffset = I32(bytes, TablesOffset + 8);
        AudioTableOffset = I32(bytes, TablesOffset + 12);
        var layoutTable = I32(bytes, TablesOffset + 16);
        var attributeTable = I32(bytes, TablesOffset + 24);
        var soundOffsets = Table(bytes, SoundTableOffset, soundCount);
        TrackOffsets = Table(bytes, TrackTableOffset, trackCount);
        var audioOffsets = Table(bytes, AudioTableOffset, audioCount);
        LayoutOffsets = layoutTable != 0 ? Table(bytes, layoutTable, soundCount) : [];
        // The attribute table has no count: it runs up to the first entry, zero-padded to 16 bytes.
        var first = soundOffsets.Concat(TrackOffsets).Concat(audioOffsets).Concat(LayoutOffsets).Where(o => o > 0).DefaultIfEmpty(bytes.Length).Min();
        AttributeOffsets = attributeTable > 0 && attributeTable < first ? [.. Table(bytes, attributeTable, (first - attributeTable) / 4).Where(o => o != 0)] : [];
        foreach (var offset in TrackOffsets.Concat(LayoutOffsets).Concat(AttributeOffsets))
            if (offset < 0 || offset >= bytes.Length)
                throw new InvalidDataException("An SCD entry offset points outside the file.");
        Audio = [.. audioOffsets.Select(o => ReadAudio(bytes, o))];
        Sounds = [.. soundOffsets.Select(o => ReadSound(bytes, o, trackCount, Audio))];
    }

    private static ImmutableArray<int> Table(byte[] bytes, int offset, int count)
    {
        if (count == 0)
            return [];
        if (offset < SoundTableOffset || (long)offset + count * 4L > bytes.Length)
            throw new InvalidDataException("An SCD offset table lies outside the file.");
        var table = ImmutableArray.CreateBuilder<int>(count);
        for (var i = 0; i < count; i++)
            table.Add(I32(bytes, offset + i * 4));
        return table.MoveToImmutable();
    }

    private static ScdAudio ReadAudio(byte[] bytes, int offset)
    {
        if (offset == 0)
            return new ScdAudio(0, ScdCodec.Empty, 0, 0, 0, 0, 0, 0, 0, 0);
        if (offset < SoundTableOffset || (long)offset + ScdAudio.HeaderSize > bytes.Length)
            throw new InvalidDataException("An SCD audio entry lies outside the file.");
        var audio = new ScdAudio(offset, (ScdCodec)I32(bytes, offset + 12), I32(bytes, offset + 4), I32(bytes, offset + 8), I32(bytes, offset),
            I32(bytes, offset + 16), I32(bytes, offset + 20), I32(bytes, offset + 24), I32(bytes, offset + 28), 0);
        if (audio.DataLength < 0 || audio.SubInfoSize < 0 || (long)offset + ScdAudio.HeaderSize + audio.SubInfoSize > bytes.Length)
            throw new InvalidDataException("An SCD audio entry runs past the end of the file.");
        // 795 of 2,791 mod-library SCDs (2026-10-03, all one-entry MS-ADPCM from some tool) state a data
        // length that runs past the file, often by exactly the entry's header and sub-info. Mods ship
        // them, so read what is there instead of refusing; no vanilla SCD does this.
        if (audio.DataLength > bytes.Length - audio.DataOffset)
            audio = audio with { DataLength = bytes.Length - audio.DataOffset, Cut = true };
        if (audio.DataLength == 0)
            return audio;
        if (audio.Channels < 1 || audio.Channels > 8 || audio.SampleRate < 1000 || audio.SampleRate > 192000)
            throw new InvalidDataException("An SCD audio entry has an invalid channel count or sample rate.");
        // The sub-info starts with the marker block when the entry has one; its second field is its size.
        var codecInfo = offset + ScdAudio.HeaderSize;
        if (audio.HasMarker)
        {
            var marker = I32(bytes, codecInfo + 4);
            if (marker < 8 || marker > audio.SubInfoSize)
                throw new InvalidDataException("An SCD audio entry's marker block is malformed.");
            codecInfo += marker;
        }
        var codecInfoSize = audio.DataOffset - codecInfo;
        var frames = audio.Codec switch
        {
            ScdCodec.MsAdpcm => AdpcmFrames(bytes, codecInfo, codecInfoSize, audio),
            ScdCodec.Pcm => audio.DataLength / (2L * audio.Channels),
            ScdCodec.Vorbis => VorbisFrames(bytes, codecInfo, codecInfoSize, audio),
            // HCA: 1024 samples per block of the size the sub-info states.
            ScdCodec.Hca when codecInfoSize >= 6 && I16(bytes, codecInfo + 4) > 0 => (long)audio.DataLength / I16(bytes, codecInfo + 4) * 1024,
            _ => 0,
        };
        return audio with { Seconds = (double)frames / audio.SampleRate };
    }

    private static long AdpcmFrames(byte[] bytes, int at, int size, ScdAudio audio)
    {
        if (size < 16 || I16(bytes, at) != MsAdpcm.FormatTag || I16(bytes, at + 2) != audio.Channels)
            throw new InvalidDataException("An SCD MS-ADPCM entry has an invalid format header.");
        var align = (int)BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at + 12));
        if (align <= 7 * audio.Channels)
            throw new InvalidDataException("An SCD MS-ADPCM entry has an invalid block size.");
        var perBlock = MsAdpcm.SamplesIn(align, audio.Channels);
        var rest = audio.DataLength % align;
        return (long)(audio.DataLength / align) * perBlock + (rest > 7 * audio.Channels ? MsAdpcm.SamplesIn(rest, audio.Channels) : 0);
    }

    /// <summary>
    /// The Vorbis sub-info: encode mode and byte, XOR offset and size, seek step, seek table size,
    /// Vorbis header size and two unknowns (0x20 bytes), the seek table, then the Ogg headers. Mode
    /// 0x2002 XORs only those headers with one byte; mode 0x2003 XORs headers and data with a table.
    /// The length is the last Ogg page's granule position.
    /// </summary>
    private static long VorbisFrames(byte[] bytes, int at, int size, ScdAudio audio)
    {
        if (size < 0x20)
            return 0;
        var mode = I16(bytes, at);
        var headerSize = I32(bytes, at + 20);
        var tableMode = mode == 0x2003;
        // Old VFXEditor imports store a plain Ogg stream after a legacy header: search the whole entry.
        var start = tableMode ? audio.DataOffset - headerSize : at + 0x20;
        if (start < at || start > audio.End)
            return 0;
        var key = audio.DataLength & 0x7F;
        var region = bytes.AsSpan(start, audio.End - start);
        // An Ogg page is at most 65,307 bytes, so the last one starts within that of the end.
        var from = Math.Max(0, region.Length - 65307 - 27);
        var tail = region[from..].ToArray();
        if (tableMode)
            for (var i = 0; i < tail.Length; i++)
                tail[i] ^= (byte)(VorbisXor[((key & 0x3F) + from + i) & 0xFF] ^ key);
        for (var i = tail.Length - 27; i >= 0; i--)
            if (tail.AsSpan(i, 4).SequenceEqual("OggS"u8) && tail[i + 4] == 0)
                return Math.Max(0, BinaryPrimitives.ReadInt64LittleEndian(tail.AsSpan(i + 6)));
        return 0;
    }

    private static ScdSound ReadSound(byte[] bytes, int offset, int trackCount, ImmutableArray<ScdAudio> audio)
    {
        if (offset < SoundTableOffset || offset + 16L > bytes.Length)
            throw new InvalidDataException("An SCD sound entry lies outside the file.");
        var tracks = bytes[offset];
        var type = bytes[offset + 3];
        var attributes = I32(bytes, offset + 4);
        var volume = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset + ScdSound.VolumeField));
        var (at, extra) = SoundBlocks(bytes, offset + 16, attributes, type);
        var stride = IsRandom(type) ? 8 : 4;
        if ((long)at + tracks * stride > bytes.Length)
            throw new InvalidDataException("An SCD sound entry runs past the end of the file.");
        var pairs = ImmutableArray.CreateBuilder<(int, int)>(tracks);
        double seconds = 0;
        for (var i = 0; i < tracks; i++)
        {
            int track = I16(bytes, at + i * stride), index = I16(bytes, at + i * stride + 2);
            if (track < -1 || track >= trackCount || index < -1 || index >= audio.Length)
                throw new InvalidDataException("An SCD sound entry names a track or audio entry that does not exist.");
            pairs.Add((track, index));
            if (index >= 0)
                seconds = Math.Max(seconds, audio[index].Seconds);
        }
        return new ScdSound(offset, type, attributes, volume, extra, pairs.MoveToImmutable(), seconds);
    }

    /// <summary>Sound types whose entries are 8 bytes (track, audio, limit, order) instead of 4.</summary>
    internal static bool IsRandom(int type) => type is TypeRandom or TypeCycle or TypeGroupRandom or TypeGroupOrder;

    /// <summary>
    /// Walks a sound entry's optional blocks from <paramref name="at"/>, just after its 16-byte head,
    /// and returns where its track entries start and where its extra block starts (or -1).
    /// </summary>
    internal static (int Entries, int Extra) SoundBlocks(byte[] bytes, int at, int attributes, int type)
    {
        // Optional blocks in this order, each sized by its own size field (routing: 32-bit total; bus ducking: first byte; acceleration: second byte; atomos and extra: 16-bit at +2).
        if ((attributes & AttributeRouting) != 0)
            at += Block(bytes, at, I32(bytes, at), 16);
        if ((attributes & AttributeBusDucking) != 0)
            at += Block(bytes, at, U8(bytes, at), 16);
        if ((attributes & AttributeAcceleration) != 0)
            at += Block(bytes, at, U8(bytes, at + 1), 16);
        if ((attributes & AttributeAtomos) != 0)
            at += Block(bytes, at, I16(bytes, at + 2), 16);
        var extra = -1;
        if ((attributes & AttributeExtra) != 0)
        {
            extra = at;
            at += Block(bytes, at, I16(bytes, at + 2), 16);
        }
        if ((attributes & AttributeBypass) != 0)
            at += 32;
        if (type == TypeEmpty && (attributes & ScdSound.LoopAttribute) != 0)
            at += 8;
        return (at, extra);
    }

    private static int Block(byte[] bytes, int at, int size, int minimum)
        => size >= minimum && (long)at + size <= bytes.Length ? size : throw new InvalidDataException("An SCD sound entry has a malformed block.");

    private static int I32(byte[] bytes, int at)
        => at >= 0 && at + 4 <= bytes.Length ? BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at)) : throw new InvalidDataException("The SCD file is cut short.");

    private static short I16(byte[] bytes, int at)
        => at >= 0 && at + 2 <= bytes.Length ? BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(at)) : throw new InvalidDataException("The SCD file is cut short.");

    private static byte U8(byte[] bytes, int at)
        => at >= 0 && at < bytes.Length ? bytes[at] : throw new InvalidDataException("The SCD file is cut short.");

    /// <summary>The table Vorbis mode 0x2003 XORs with (VFXEditor ScdUtils.XORTABLE).</summary>
    private static readonly byte[] VorbisXor = Convert.FromHexString(
        "3a323232037e12f7b2e2a267323222323252161b3ca1547b1b97a6931a4baaa67a7b1b97a6f702bbaaa6bbf72a51be03f42a51be03f42a51be1206562732323632" +
        "b21a3bbc91d47b58fc0b552a15bc40920b5b7c0a951235b863d20b3bf0c714515c948694595cfc1b173a3f6b3732323032727a13b726607a13b72650ba13b42a50" +
        "ba13b52e40fa1395ae4038189a92b03800fa12b17e00db96a17c08db9a91bc08d81a86e270391f86e0787e03e764519c8f346f4e41fc0bd5ae41fc0bd5ae41fc3b" +
        "70716433321232323670342b5622703a13b72660ba1b94aa403800fab2e2a26732321232b23232323275a3267b8326f9832effe3167dc01e632107e301");
}
