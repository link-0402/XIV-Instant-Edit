using System.Buffers.Binary;

namespace InstantEdit.Services.Audio;

/// <summary>
/// RIFF/WAVE reader for PCM (8, 16, 24 and 32-bit), IEEE float (32 and 64-bit) and
/// WAVE_FORMAT_EXTENSIBLE holding either. Unknown chunks and their odd-size pad bytes are skipped,
/// wider audio is downmixed to stereo, and anything else is refused before decoding. Dalamud-free.
/// </summary>
internal static class WavReader
{
    private const int Pcm = 1;
    private const int Float = 3;
    private const int Extensible = 0xFFFE;
    /// <summary>The KSDATAFORMAT_SUBTYPE GUID tail every PCM and float subformat shares.</summary>
    private static ReadOnlySpan<byte> SubtypeTail => [0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71];

    public static bool IsWav(ReadOnlySpan<byte> bytes)
        => bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WAVE"u8);

    public static AudioClip Read(byte[] bytes)
    {
        if (bytes.Length >= 4 && bytes.AsSpan(0, 4).SequenceEqual("RF64"u8))
            throw new InvalidDataException("64-bit RF64 WAV files are not supported.");
        if (!IsWav(bytes))
            throw new InvalidDataException("This is not a WAV file.");
        if (bytes.Length > AudioImport.MaxFileBytes)
            throw new InvalidDataException("The WAV file is too large.");
        int? format = null, channels = null, rate = null, blockAlign = null, bits = null;
        var mask = 0;
        int dataStart = -1, dataLength = 0;
        var at = 12;
        while (at + 8 <= bytes.Length)
        {
            var id = bytes.AsSpan(at, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 4));
            var body = at + 8;
            var available = bytes.Length - body;
            if (id.SequenceEqual("fmt "u8))
            {
                if (format != null)
                    throw new InvalidDataException("The WAV file has two format chunks.");
                if (size < 16 || size > available)
                    throw new InvalidDataException("The WAV format chunk is malformed.");
                var fmt = bytes.AsSpan(body, (int)size);
                format = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..]);
                rate = BinaryPrimitives.ReadInt32LittleEndian(fmt[4..]);
                blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(fmt[12..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]);
                if (format == Extensible)
                {
                    if (size < 40 || BinaryPrimitives.ReadUInt16LittleEndian(fmt[16..]) < 22)
                        throw new InvalidDataException("The WAV extensible format chunk is malformed.");
                    mask = BinaryPrimitives.ReadInt32LittleEndian(fmt[20..]);
                    var subtype = fmt.Slice(24, 16);
                    if (!subtype[2..].SequenceEqual(SubtypeTail))
                        throw new InvalidDataException("Only PCM and float WAV files are supported.");
                    format = BinaryPrimitives.ReadUInt16LittleEndian(subtype);
                }
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (dataStart >= 0)
                    throw new InvalidDataException("The WAV file has two data chunks.");
                // Streaming writers leave 0 or 0xFFFFFFFF here, and cut files claim more than they hold.
                dataStart = body;
                dataLength = size == 0 || size > available ? available : (int)size;
                if (size == 0 || size > available)
                    break;
            }
            // Chunks are word aligned: an odd-sized chunk is followed by one pad byte.
            var next = body + (long)size + (size & 1);
            if (next > bytes.Length)
                break;
            at = (int)next;
        }
        if (format == null)
            throw new InvalidDataException("The WAV file has no format chunk.");
        if (dataStart < 0)
            throw new InvalidDataException("The WAV file has no audio data.");
        if (format is not (Pcm or Float))
            throw new InvalidDataException($"Only PCM and float WAV files are supported, not format 0x{format:X4}.");
        AudioClip.CheckFormat(rate!.Value, channels!.Value);
        var width = (format, bits) switch
        {
            (Pcm, 8) => 1,
            (Pcm, 16) => 2,
            (Pcm, 24) => 3,
            (Pcm, 32) or (Float, 32) => 4,
            (Float, 64) => 8,
            _ => throw new InvalidDataException($"{bits}-bit {(format == Float ? "float" : "PCM")} WAV files are not supported."),
        };
        if (blockAlign != width * channels)
            throw new InvalidDataException("The WAV block size does not match its channels and bit depth.");
        var frames = dataLength / blockAlign.Value;
        AudioClip.CheckLength(frames, rate.Value, channels.Value);
        var samples = new float[frames * channels.Value];
        var data = bytes.AsSpan(dataStart, frames * blockAlign.Value);
        for (var i = 0; i < samples.Length; i++)
        {
            var s = data.Slice(i * width, width);
            samples[i] = (format, width) switch
            {
                (Pcm, 1) => (s[0] - 128) / 128f,
                (Pcm, 2) => BinaryPrimitives.ReadInt16LittleEndian(s) / 32768f,
                (Pcm, 3) => ((s[0] << 8) | (s[1] << 16) | (s[2] << 24)) / 2147483648f,
                (Pcm, 4) => BinaryPrimitives.ReadInt32LittleEndian(s) / 2147483648f,
                (Float, 4) => Finite(BinaryPrimitives.ReadSingleLittleEndian(s)),
                _ => Finite((float)BinaryPrimitives.ReadDoubleLittleEndian(s)),
            };
        }
        var speakers = AudioClip.SpeakersFromMask(mask != 0 ? mask : AudioClip.DefaultWaveMask(channels.Value), channels.Value);
        return AudioClip.FromInterleaved(samples, rate.Value, channels.Value, speakers);
    }

    /// <summary>NaN and infinity in a float file become silence and full scale rather than poisoning the encoder.</summary>
    private static float Finite(float value) => float.IsNaN(value) ? 0 : Math.Clamp(value, -1f, 1f);
}
