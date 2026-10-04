using NVorbis;

namespace InstantEdit.Services.Audio;

/// <summary>
/// Ogg Vorbis to float PCM through NVorbis (MIT, fully managed). Only the first logical stream is
/// read; Opus and other Ogg codecs are refused. Dalamud-free.
/// </summary>
internal static class OggReader
{
    /// <summary>Vorbis channel order (I.4.3.9 of the spec) as WAVE speaker bits, for the downmix.</summary>
    private static readonly int[][] VorbisSpeakers =
    [
        [], [0x4], [0x1, 0x2], [0x1, 0x4, 0x2], [0x1, 0x2, 0x10, 0x20], [0x1, 0x4, 0x2, 0x10, 0x20],
        [0x1, 0x4, 0x2, 0x10, 0x20, 0x8], [0x1, 0x4, 0x2, 0x200, 0x400, 0x100, 0x8],
        [0x1, 0x4, 0x2, 0x200, 0x400, 0x10, 0x20, 0x8],
    ];

    public static bool IsOgg(ReadOnlySpan<byte> bytes) => bytes.Length >= 4 && bytes[..4].SequenceEqual("OggS"u8);

    public static AudioClip Read(byte[] bytes)
    {
        if (!IsOgg(bytes))
            throw new InvalidDataException("This is not an Ogg file.");
        if (bytes.Length > AudioImport.MaxFileBytes)
            throw new InvalidDataException("The Ogg file is too large.");
        float[] samples;
        int channels, rate;
        try
        {
            using var reader = new VorbisReader(new MemoryStream(bytes, false), true);
            channels = reader.Channels;
            rate = reader.SampleRate;
            AudioClip.CheckFormat(rate, channels);
            // TotalSamples comes from the last page's granule position; decoding still stops at the limit.
            var limit = Math.Min((long)AudioClip.MaxSeconds * rate, AudioClip.MaxDecodedSamples / channels);
            var declared = reader.TotalSamples;
            if (declared > limit)
                AudioClip.CheckLength(declared, rate, channels);
            var buffer = new float[Math.Clamp(declared, 1, limit) * channels];
            var count = 0;
            while (true)
            {
                if (count == buffer.Length)
                {
                    if (buffer.Length / channels >= limit)
                    {
                        if (reader.ReadSamples(new float[channels], 0, channels) > 0)
                            AudioClip.CheckLength(limit + 1, rate, channels);
                        break;
                    }
                    Array.Resize(ref buffer, (int)Math.Min((long)buffer.Length * 2, limit * channels));
                }
                var read = reader.ReadSamples(buffer, count, buffer.Length - count);
                if (read <= 0)
                    break;
                count += read;
            }
            samples = count == buffer.Length ? buffer : buffer[..(count - count % channels)];
        }
        catch (Exception e) when (e is not InvalidDataException)
        {
            throw new InvalidDataException("This Ogg file could not be read as Ogg Vorbis: " + e.Message, e);
        }
        AudioClip.CheckLength(samples.Length / channels, rate, channels);
        var speakers = channels < VorbisSpeakers.Length ? VorbisSpeakers[channels] : [];
        return AudioClip.FromInterleaved(samples, rate, channels, speakers);
    }
}
