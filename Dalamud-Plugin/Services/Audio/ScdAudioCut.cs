using System.Buffers.Binary;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Services.Audio;

/// <summary>
/// Cuts a sound's audio with the motion it plays to: decodes an SCD audio entry (PCM, MS-ADPCM or
/// Vorbis), takes spans out of it with a short crossfade at each join, and writes the result back as
/// MS-ADPCM in place of the entry, keeping every other setting of the sound. Dalamud-free.
/// </summary>
internal static class ScdAudioCut
{
    /// <summary>How long each join of a cut crossfades, in seconds.</summary>
    public const double FadeSeconds = .010;

    public const string FormatProblem = "This sound's format can't be cut.";
    public const string LoopProblem = "This sound loops in its file, so it can't be cut.";

    /// <summary>The entry's audio as samples; other codecs (HCA, ATRAC, XMA) are refused.</summary>
    public static AudioClip Decode(ScdModelAudio audio)
    {
        if (audio.Null || audio.Data.Length == 0)
            throw new InvalidDataException("The sound has no audio to cut.");
        var channels = audio.Channels;
        var rate = audio.SampleRate;
        var info = CodecInfo(audio);
        switch (audio.Codec)
        {
            case ScdCodec.Pcm:
            {
                var count = audio.Data.Length / 2 / channels * channels;
                var samples = new float[count];
                for (var i = 0; i < count; i++)
                    samples[i] = BinaryPrimitives.ReadInt16LittleEndian(audio.Data.AsSpan(i * 2)) / 32768f;
                return AudioClip.FromInterleaved(samples, rate, channels, default);
            }
            case ScdCodec.MsAdpcm:
            {
                if (info.Length < 14)
                    throw new InvalidDataException(FormatProblem);
                var align = BinaryPrimitives.ReadUInt16LittleEndian(info[12..]);
                var pcm = MsAdpcm.Decode(audio.Data, channels, align);
                var samples = new float[pcm.Length];
                for (var i = 0; i < pcm.Length; i++)
                    samples[i] = pcm[i] / 32768f;
                return AudioClip.FromInterleaved(samples, rate, channels, default);
            }
            case ScdCodec.Vorbis:
                return OggReader.Read(Ogg(audio, info));
            default:
                throw new InvalidDataException(FormatProblem);
        }
    }

    /// <summary>
    /// <paramref name="clip"/> without <paramref name="spans"/>, applied in order, each at its place in
    /// the audio the ones before it left. Each join crossfades over <see cref="FadeSeconds"/> from the
    /// audio that would have played on into what plays now, so the length is exactly what the spans
    /// leave; a span past the end trims the tail and fades it out.
    /// </summary>
    public static AudioClip Cut(AudioClip clip, IReadOnlyList<TimelineAudioSpan> spans)
    {
        var samples = clip.Samples;
        var channels = clip.Channels;
        var rate = clip.SampleRate;
        var fade = Math.Max(1, (int)Math.Round(rate * FadeSeconds));
        foreach (var span in spans)
        {
            var frames = samples.Length / channels;
            var at = (int)Math.Clamp(Math.Round(span.At * rate), 0, frames);
            if (at >= frames || !(span.Seconds > 0))
                continue;
            var end = double.IsPositiveInfinity(span.Seconds) ? frames : (int)Math.Min(frames, at + Math.Round(span.Seconds * rate));
            if (end <= at)
                continue;
            float[] next;
            if (end >= frames)
            {
                next = samples[..(at * channels)];
                var tail = Math.Min(fade, at);
                for (var i = 0; i < tail; i++)
                    for (var c = 0; c < channels; c++)
                        next[(at - tail + i) * channels + c] *= 1 - (i + 1f) / tail;
            }
            else
            {
                next = new float[(frames - (end - at)) * channels];
                samples.AsSpan(0, at * channels).CopyTo(next);
                samples.AsSpan(end * channels).CopyTo(next.AsSpan(at * channels));
                // The audio the cut skipped fades out while the audio after it fades in.
                var blend = Math.Min(fade, frames - end);
                for (var i = 0; i < blend; i++)
                {
                    var t = (i + 1f) / (blend + 1);
                    for (var c = 0; c < channels; c++)
                        next[(at + i) * channels + c] = samples[(at + i) * channels + c] * (1 - t) + samples[(end + i) * channels + c] * t;
                }
            }
            if (next.Length < channels)
                throw new InvalidDataException("Cutting would leave the sound empty.");
            samples = next;
        }
        return ReferenceEquals(samples, clip.Samples) ? clip : new AudioClip(samples, rate, channels);
    }

    /// <summary>
    /// The SCD <paramref name="scd"/> with every alternative of sound <paramref name="sound"/> cut by
    /// <paramref name="spans"/> and written as MS-ADPCM, built on <paramref name="template"/> as imports
    /// are; the sound's tracks, chances and volume stay. A sound with loop points is refused.
    /// </summary>
    public static byte[] Apply(byte[] scd, int sound, IReadOnlyList<TimelineAudioSpan> spans, byte[] template)
    {
        var model = ScdModel.Read(scd);
        if (sound < 0 || sound >= model.Sounds.Count)
            throw new InvalidDataException("The sound file has no such sound.");
        var entries = model.Sounds[sound].Entries;
        bool Plays(ScdModelEntry e) => e.Audio >= 0 && e.Audio < model.Audio.Count;
        if (entries.Any(e => Plays(e) && model.Audio[e.Audio].LoopEnd > 0))
            throw new InvalidDataException(LoopProblem);
        for (var i = 0; i < entries.Count; i++)
        {
            if (!Plays(entries[i]))
                continue;
            var cut = Cut(Decode(model.Audio[entries[i].Audio]), spans);
            ScdEdits.Replace(model, sound, i, ScdEdits.Clips(ScdBuilder.Build(template, cut, 1f))[0]);
        }
        ScdEdits.Compact(model);
        return model.Write();
    }

    /// <summary>The codec's part of the entry's sub-info: after the marker block when it has one.</summary>
    private static ReadOnlySpan<byte> CodecInfo(ScdModelAudio audio)
    {
        var info = audio.SubInfo.AsSpan();
        var flags = BinaryPrimitives.ReadInt32LittleEndian(audio.Header.AsSpan(28));
        if ((flags & ScdAudio.MarkerFlag) == 0)
            return info;
        var marker = info.Length >= 8 ? BinaryPrimitives.ReadInt32LittleEndian(info[4..]) : -1;
        if (marker < 8 || marker > info.Length)
            throw new InvalidDataException(FormatProblem);
        return info[marker..];
    }

    /// <summary>
    /// The Ogg stream of a Vorbis entry: the header pages from the sub-info (after its 0x20-byte block and
    /// the seek table) and the audio pages as data. Mode 0x2002 XORs the headers with one byte, 0x2003
    /// headers and data with the table, keyed by the data's length.
    /// </summary>
    private static byte[] Ogg(ScdModelAudio audio, ReadOnlySpan<byte> info)
    {
        if (info.Length < 0x20)
            throw new InvalidDataException(FormatProblem);
        var mode = BinaryPrimitives.ReadInt16LittleEndian(info);
        var encodeByte = (byte)BinaryPrimitives.ReadInt16LittleEndian(info[2..]);
        var seekTable = BinaryPrimitives.ReadInt32LittleEndian(info[16..]);
        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(info[20..]);
        // Old VFXEditor imports store a whole Ogg stream behind a header of their own.
        if (seekTable < 0 || headerSize < 0 || 0x20L + seekTable + headerSize > info.Length)
            throw new InvalidDataException(FormatProblem);
        byte[] ogg = [.. info.Slice(0x20 + seekTable, headerSize), .. audio.Data];
        if (mode == 0x2002 && encodeByte != 0)
            for (var i = 0; i < headerSize; i++)
                ogg[i] ^= encodeByte;
        else if (mode == 0x2003)
        {
            var key = (audio.CutShort?.Declared ?? audio.Data.Length) & 0x7F;
            for (var i = 0; i < ogg.Length; i++)
                ogg[i] ^= (byte)(ScdFile.VorbisXor[((key & 0x3F) + i) & 0xFF] ^ key);
        }
        if (!OggReader.IsOgg(ogg))
            throw new InvalidDataException(FormatProblem);
        return ogg;
    }
}
