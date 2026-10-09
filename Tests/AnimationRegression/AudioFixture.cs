using System.Buffers.Binary;
using System.Text;
using InstantEdit.Services.Audio;

/// <summary>Sound import: WAV and Ogg reading, resampling, MS-ADPCM, SCD reading and building on a synthetic template.</summary>
internal static class AudioFixture
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Wav(check, reject);
        Ogg(check, reject);
        Resampling(check);
        Adpcm(check, reject);
        Scd(check, reject);
        Import(check, reject);
    }

    private static void Wav(Action<bool, string> check, Action<Action, string> reject)
    {
        var sine = Sine(44100, 441, 0.5f, 1, 2000);
        foreach (var (format, bits, tolerance) in new[] { (1, 8, 1 / 64f), (1, 16, 1 / 16000f), (1, 24, 1e-6f), (1, 32, 1e-6f), (3, 32, 1e-7f), (3, 64, 1e-7f) })
        {
            var clip = WavReader.Read(WavBytes(sine, 44100, 1, format, bits));
            check(clip.Channels == 1 && clip.SampleRate == 44100 && clip.Frames == 2000 && MaxError(clip.Samples, sine) <= tolerance,
                $"{bits}-bit {(format == 3 ? "float" : "PCM")} WAV decodes to the written samples");
        }
        var stereo = Interleave(Sine(48000, 300, 0.4f, 1, 1500), Sine(48000, 700, -0.3f, 1, 1500));
        var extensible = WavReader.Read(WavBytes(stereo, 48000, 2, 1, 16, extensible: true, mask: 0x3));
        check(extensible.Channels == 2 && extensible.Frames == 1500 && MaxError(extensible.Samples, stereo) < 1 / 16000f,
            "WAVE_FORMAT_EXTENSIBLE stereo PCM keeps its channel order");
        var floatExtensible = WavReader.Read(WavBytes(stereo, 48000, 2, 3, 32, extensible: true, mask: 0x3));
        check(MaxError(floatExtensible.Samples, stereo) < 1e-7f, "WAVE_FORMAT_EXTENSIBLE float decodes");
        var odd = WavReader.Read(WavBytes(sine, 44100, 1, 1, 16, junk: true));
        check(odd.Frames == 2000 && MaxError(odd.Samples, sine) < 1 / 16000f, "unknown chunks with an odd size and their pad byte are skipped");
        var streamed = WavBytes(sine, 44100, 1, 1, 16);
        BinaryPrimitives.WriteUInt32LittleEndian(streamed.AsSpan(DataSizeField(streamed)), 0xFFFFFFFF);
        check(WavReader.Read(streamed).Frames == 2000, "a streamed WAV's placeholder data size reads to the end of the file");
        // 5.1: front left, front right, center, LFE, back left, back right.
        var surround = new float[6 * 100];
        for (var f = 0; f < 100; f++)
        {
            surround[f * 6] = 0.6f;
            surround[f * 6 + 3] = 1f;
            surround[f * 6 + 5] = -0.4f;
        }
        var downmix = WavReader.Read(WavBytes(surround, 44100, 6, 3, 32));
        check(downmix.Channels == 2 && downmix.Samples[0] > 0.2f && downmix.Samples[1] < -0.05f &&
              downmix.Samples.All(s => Math.Abs(s) <= 1), "5.1 WAV downmixes to stereo by speaker position and drops the LFE");
        reject(() => WavReader.Read(Encoding.ASCII.GetBytes("RIFF\0\0\0\0WAVX")), "a RIFF file that is not WAVE is refused");
        reject(() => WavReader.Read(Strip(WavBytes(sine, 44100, 1, 1, 16), "fmt ")), "a WAV without a format chunk is refused");
        reject(() => WavReader.Read(WavBytes([], 44100, 1, 1, 16)), "a WAV without samples is refused");
        reject(() => WavReader.Read(WavBytes(sine, 44100, 1, 0x55, 16)), "compressed (MP3) WAV formats are refused");
        reject(() => WavReader.Read(WavBytes(sine, 44100, 1, 1, 12)), "12-bit PCM is refused");
        reject(() => WavReader.Read(WavBytes(sine, 4000, 1, 1, 16)), "a 4 kHz WAV is refused");
        var badAlign = WavBytes(sine, 44100, 1, 1, 16);
        BinaryPrimitives.WriteUInt16LittleEndian(badAlign.AsSpan(32), 3);
        reject(() => WavReader.Read(badAlign), "a block size that disagrees with the bit depth is refused");
        var rf64 = WavBytes(sine, 44100, 1, 1, 16);
        "RF64"u8.CopyTo(rf64);
        reject(() => WavReader.Read(rf64), "RF64 WAV files are refused");
        reject(() => WavReader.Read(WavBytes(new float[600 * 8000 + 8], 8000, 1, 1, 8)), "WAV audio longer than 10 minutes is refused");
    }

    private static void Ogg(Action<bool, string> check, Action<Action, string> reject)
    {
        var mono = OggReader.Read(SilentVorbis(1, 22050, 40));
        check(mono.Channels == 1 && mono.SampleRate == 22050 && mono.Frames == 39 * 128 && mono.Samples.All(s => s == 0),
            "a synthetic Ogg Vorbis stream decodes through NVorbis to its granule length");
        var wide = OggReader.Read(SilentVorbis(6, 48000, 10));
        check(wide.Channels == 2 && wide.Frames == 9 * 128, "6-channel Ogg Vorbis downmixes to stereo");
        reject(() => OggReader.Read(Encoding.ASCII.GetBytes("OggS but not really a stream")), "a broken Ogg stream is refused");
        reject(() => OggReader.Read(SilentVorbis(1, 8000, 4, 601L * 8000)), "an Ogg stream declaring more than 10 minutes is refused before decoding");
        reject(() => OggReader.Read(WavBytes(Sine(44100, 441, 0.5f, 1, 100), 44100, 1, 1, 16)), "a WAV is not read as Ogg");
    }

    private static void Resampling(Action<bool, string> check)
    {
        check(AudioResampler.ScdRate(44100) == 44100 && AudioResampler.ScdRate(48000) == 48000 && AudioResampler.ScdRate(44116) == 44116 &&
              AudioResampler.ScdRate(22050) == 44100 && AudioResampler.ScdRate(32000) == 44100 && AudioResampler.ScdRate(96000) == 48000,
            "SCD rates keep the vanilla range and move others to 44.1 or 48 kHz");
        var up = AudioResampler.Resample(new AudioClip(Sine(22050, 1000, 0.5f, 1, 22050), 22050, 1), 44100);
        var expected = Sine(44100, 1000, 0.5f, 1, 44100);
        check(up.SampleRate == 44100 && up.Frames == 44100 && Snr(expected.AsSpan(64, 44000), up.Samples.AsSpan(64, 44000)) > 60,
            "upsampling 22.05 to 44.1 kHz reproduces a 1 kHz sine (SNR above 60 dB)");
        var down = AudioResampler.Resample(new AudioClip(Sine(96000, 1000, 0.5f, 1, 96000), 96000, 1), 48000);
        check(down.Frames == 48000 && Snr(Sine(48000, 1000, 0.5f, 1, 48000).AsSpan(64, 47800), down.Samples.AsSpan(64, 47800)) > 60,
            "downsampling 96 to 48 kHz keeps a 1 kHz sine");
        var alias = AudioResampler.Resample(new AudioClip(Sine(96000, 30000, 0.5f, 1, 96000), 96000, 1), 48000);
        check(Rms(alias.Samples.AsSpan(64, 47800)) < 0.01, "a 30 kHz tone is filtered out instead of aliasing to 18 kHz");
        var stereo = AudioResampler.Resample(new AudioClip(Interleave(Sine(32000, 500, 0.5f, 1, 3200), new float[3200]), 32000, 2), 44100);
        check(stereo.Channels == 2 && stereo.Frames == 4410 && Enumerable.Range(0, stereo.Frames).All(f => stereo.Samples[f * 2 + 1] == 0),
            "resampling keeps stereo channels apart");
    }

    private static void Adpcm(Action<bool, string> check, Action<Action, string> reject)
    {
        var header = MsAdpcm.FormatHeader(1, 44049);
        check(header.Length == 50 && Convert.ToHexString(header) ==
              "0200010011AC0000195E000046000400200080000700" + "00010000000200FF00000000C0004000F0000000CC0130FF880118FF",
            "the ADPCM format header matches vanilla SCD sub-info byte for byte (mono, 44,049 Hz)");
        check(MsAdpcm.BlockAlign(1) == 70 && MsAdpcm.BlockAlign(2) == 140 && MsAdpcm.SamplesIn(70, 1) == 128 && MsAdpcm.SamplesIn(140, 2) == 128,
            "blocks hold 128 samples in 70 bytes per channel, as vanilla entries do");
        // White noise has nothing to predict, so 4 bits per sample leave it the least headroom.
        foreach (var (name, samples, channels, floor) in new[]
        {
            ("a 440 Hz sine", Sine(44100, 440, 0.8f, 1, 44100), 1, 40),
            ("stereo sines", Interleave(Sine(44100, 220, 0.6f, 1, 22050), Sine(44100, 1760, 0.3f, 1, 22050)), 2, 40),
            ("a chirp", Chirp(48000), 1, 20),
            ("white noise", Noise(44100, 0.3f), 1, 15),
        })
        {
            var pcm = MsAdpcm.ToPcm16(samples);
            var data = MsAdpcm.Encode(pcm, channels);
            var decoded = MsAdpcm.Decode(data, channels, MsAdpcm.BlockAlign(channels));
            var frames = pcm.Length / channels;
            var blocks = (frames + 127) / 128;
            var snr = Snr(pcm.Select(s => (float)s).ToArray(), decoded.Take(pcm.Length).Select(s => (float)s).ToArray());
            check(data.Length == blocks * MsAdpcm.BlockAlign(channels) && decoded.Length == blocks * 128 * channels && snr > floor,
                $"MS-ADPCM round-trips {name} in whole blocks (SNR {snr:F0} dB, above {floor})");
        }
        // One hand-made mono block: predictor 0 (coefficients 256, 0), step 16, samples 100 then 200, nibbles +1 and -1.
        var block = new byte[70];
        BinaryPrimitives.WriteInt16LittleEndian(block.AsSpan(1), 16);
        BinaryPrimitives.WriteInt16LittleEndian(block.AsSpan(3), 200);
        BinaryPrimitives.WriteInt16LittleEndian(block.AsSpan(5), 100);
        block[7] = 0x1F;
        var known = MsAdpcm.Decode(block, 1, 70);
        check(known.Length == 128 && known[0] == 100 && known[1] == 200 && known[2] == 216 && known[3] == 200,
            "the decoder emits the older header sample first and steps from the newer one");
        var silence = MsAdpcm.Encode(new short[10], 2);
        check(silence.Length == 140 && MsAdpcm.Decode(silence, 2, 140).All(s => s == 0), "a short clip pads its last block with silence");
        var square = MsAdpcm.Encode(Enumerable.Range(0, 4096).Select(i => (short)(i / 64 % 2 == 0 ? 32767 : -32768)).ToArray(), 1);
        check(Enumerable.Range(0, square.Length / 70).All(b => BinaryPrimitives.ReadInt16LittleEndian(square.AsSpan(b * 70 + 1)) >= 16),
            "full-scale square waves keep every block's step size within 16 bits");
        reject(() => MsAdpcm.Encode(new short[30], 3), "MS-ADPCM encoding refuses more than two channels");
        reject(() => MsAdpcm.Decode(block, 1, 5), "MS-ADPCM decoding refuses a block smaller than its header");
    }

    private static void Scd(Action<bool, string> check, Action<Action, string> reject)
    {
        var template = Template();
        var parsed = ScdFile.Read(template);
        check(parsed.Sounds.Length == 1 && parsed.Audio.Length == 1 && parsed.TrackOffsets.Length == 1 && parsed.LayoutOffsets.Length == 1 &&
              parsed.AttributeOffsets.Length == 1 && parsed.Sounds[0].Loops && parsed.Sounds[0].Volume == 0.5f &&
              parsed.Audio[0] is { Codec: ScdCodec.MsAdpcm, Channels: 1, SampleRate: 44100, DataLength: 140 } &&
              Math.Abs(parsed.Sounds[0].Seconds - 256 / 44100.0) < 1e-9,
            "the synthetic vanilla-shaped template parses: entry tables, sound volume and loop, ADPCM length");
        var clip = new AudioClip(Sine(44100, 440, 0.7f, 1, 44100), 44100, 1);
        var built = ScdBuilder.Build(template, clip, 0.8f);
        var scd = ScdFile.Read(built);
        var audio = scd.Audio[0];
        var sound = scd.Sounds[0];
        var blocks = (44100 + 127) / 128;
        var milliseconds = (int)Math.Ceiling(blocks * 128 * 1000.0 / 44100);
        check(built.Length % 16 == 0 && BinaryPrimitives.ReadInt32LittleEndian(built.AsSpan(0x10)) == built.Length &&
              audio.Offset == parsed.Audio[0].Offset && audio.End <= built.Length && built.Length - audio.End < 16,
            "the built SCD keeps the audio entry's offset, pads to 16 bytes and declares its own size");
        check(audio is { Codec: ScdCodec.MsAdpcm, Channels: 1, SampleRate: 44100, LoopStart: 0, LoopEnd: 0, SubInfoSize: 50, Flags: 0x01000000 } &&
              audio.DataLength == blocks * 70 && Math.Abs(sound.Seconds - blocks * 128 / 44100.0) < 1e-9,
            "the new audio entry is mono MS-ADPCM without loop or marker, in whole 70-byte blocks");
        check(!sound.Loops && sound.Volume == 0.8f && BinaryPrimitives.ReadInt32LittleEndian(built.AsSpan(sound.ExtraOffset + 4)) == milliseconds &&
              BinaryPrimitives.ReadInt32LittleEndian(built.AsSpan(IntervalAt)) == milliseconds,
            "the sound plays once at the chosen volume, and the track wait and extra play length hold the new length in ms");
        var unchanged = Enumerable.Range(0, parsed.Audio[0].Offset).Where(i => built[i] != template[i]).ToArray();
        var allowed = new HashSet<int>(Enumerable.Range(0x10, 4).Concat(Enumerable.Range(SoundAt + 4, 8))
            .Concat(Enumerable.Range(sound.ExtraOffset + 4, 4)).Concat(Enumerable.Range(IntervalAt, 4)));
        check(unchanged.All(allowed.Contains), "nothing before the audio entry changes except size, loop flag, volume and the two lengths");
        var decoded = MsAdpcm.Decode(built.AsSpan(audio.DataOffset, audio.DataLength), 1, 70);
        var input = MsAdpcm.ToPcm16(clip.Samples);
        var snr = Snr(input.Select(s => (float)s).ToArray(), decoded.Take(input.Length).Select(s => (float)s).ToArray());
        check(snr > 20, $"the built SCD's audio decodes back to the encoder input (SNR {snr:F0} dB)");
        var stereo = ScdFile.Read(ScdBuilder.Build(template, new AudioClip(Interleave(Sine(22050, 300, 0.5f, 1, 11025), Sine(22050, 600, 0.5f, 1, 11025)), 22050, 2), 1f));
        check(stereo.Audio[0] is { Channels: 2, SampleRate: 44100, DataLength: var length } && length % 140 == 0 && Math.Abs(stereo.Sounds[0].Seconds - 0.5) < 0.01,
            "a 22.05 kHz stereo clip builds as 44.1 kHz stereo blocks of 140 bytes");
        var info = ScdFile.Info(built);
        check(info.SoundCount == 1 && Math.Abs(info.SoundSeconds[0] - 1.0) < 0.005, "Info reports one sound of about one second");
        check(ScdFile.TrySeconds(built) is { } lookup && Math.Abs(lookup - info.SoundSeconds[0]) < 1e-6 && ScdFile.TrySeconds(built, 1) == null &&
              ScdFile.TrySeconds(template[..0x200]) == null && ScdFile.TrySeconds(WavBytes(Sine(44100, 441, 0.5f, 1, 100), 44100, 1, 1, 16)) == null,
            "TrySeconds gives the sound's length, and null for a missing index, a cut SCD or another file type");
        // A marker block in the template's sub-info is skipped when reading and dropped when building.
        var marked = Template(marker: true);
        check(ScdFile.Read(marked).Audio[0] is { HasMarker: true, SubInfoSize: 82 } && Math.Abs(ScdFile.Info(marked).SoundSeconds[0] - 256 / 44100.0) < 1e-9,
            "a marker block before the ADPCM header is skipped");
        check(ScdFile.Read(ScdBuilder.Build(marked, clip, 1f)).Audio[0] is { HasMarker: false, SubInfoSize: 50 }, "building drops the template's marker");
        // Vorbis entries report the last Ogg page's granule; mode 0x2003 hides it under the XOR table.
        check(Math.Abs(ScdFile.Info(VorbisScd(0x0000, 96000)).SoundSeconds[0] - 2.0) < 1e-9 &&
              Math.Abs(ScdFile.Info(VorbisScd(0x2003, 144000)).SoundSeconds[0] - 3.0) < 1e-9,
            "Vorbis entries report the last Ogg page's granule, also under the 0x2003 XOR table");
        // Some mod tools state the data length from the entry's start; the reader keeps what the file holds.
        var overstated = template.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(overstated.AsSpan(AudioAt), parsed.Audio[0].DataLength + 0x20 + 50);
        check(ScdFile.Read(overstated).Audio[0] is { Cut: true } cut && cut.End == overstated.Length && !parsed.Audio[0].Cut &&
              Math.Abs(ScdFile.Info(overstated).SoundSeconds[0] - 256 / 44100.0) < 1e-9,
            "an audio entry whose data length runs past the file reads as far as the file goes");
        reject(() => ScdBuilder.Build(template, clip, 1.5f), "volumes above 1 are refused");
        reject(() => ScdBuilder.Build(template, clip, float.NaN), "a NaN volume is refused");
        reject(() => ScdBuilder.Build(Template(twoAudio: true), clip, 1f), "a template with two audio entries is refused");
        reject(() => ScdBuilder.Build(Template(looping: true), clip, 1f), "a template whose track loops is refused");
        reject(() => ScdBuilder.Build(WavBytes(Sine(44100, 441, 0.5f, 1, 100), 44100, 1, 1, 16), clip, 1f), "a template that is not an SCD is refused");
        reject(() => ScdFile.Read(template[..0x200]), "a cut SCD is refused");
        var bigEndian = template.ToArray();
        bigEndian[12] = 1;
        reject(() => ScdFile.Read(bigEndian), "big-endian SCDs are refused");
        var badAudio = template.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(badAudio.AsSpan(AudioTableAt), 0x7FFFFFF0);
        reject(() => ScdFile.Read(badAudio), "an audio offset outside the file is refused");
        var badSound = template.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(badSound.AsSpan(ScdFile.SoundTableOffset), 0x7FFFFFF0);
        reject(() => ScdFile.Read(badSound), "a sound offset outside the file is refused");
        check(ScdFile.TrySeconds(badSound) == null, "TrySeconds gives null for a sound offset near int.MaxValue");
        var badTrack = template.ToArray();
        BinaryPrimitives.WriteInt16LittleEndian(badTrack.AsSpan(SoundAt + 32 + 2), 5);
        reject(() => ScdFile.Read(badTrack), "a sound naming a missing audio entry is refused");
    }

    private static void Import(Action<bool, string> check, Action<Action, string> reject)
    {
        var template = Template();
        var wav = WavBytes(Sine(44100, 440, 0.5f, 1, 44100), 44100, 1, 1, 16);
        var (scd, seconds) = AudioImport.FromBytes(".WAV", wav, template, 1f);
        check(ScdFile.IsScd(scd) && Math.Abs(seconds - 1f) < 0.005f, "a WAV imports as an SCD with its length");
        var (ogg, oggSeconds) = AudioImport.FromBytes(".ogg", SilentVorbis(2, 44100, 50), template, 0.5f);
        check(ScdFile.Read(ogg).Audio[0].Channels == 2 && Math.Abs(oggSeconds - 49 * 128 / 44100f) < 0.005f, "an Ogg imports as a stereo SCD");
        var (same, sameSeconds) = AudioImport.FromBytes(".scd", scd, template, 0.2f);
        check(ReferenceEquals(same, scd) && sameSeconds == seconds && ScdFile.Read(same).Sounds[0].Volume == 1f,
            "an SCD passes through unchanged, keeping its own volume");
        reject(() => AudioImport.FromBytes(".mp3", wav, template, 1f), "other file types are refused");
        reject(() => AudioImport.FromBytes(".scd", wav, template, 1f), "an .scd that is not an SCD is refused");
        reject(() => AudioImport.FromBytes(".wav", SilentVorbis(1, 44100, 4), template, 1f), "an .wav holding Ogg data is refused");
        reject(() => AudioImport.FromFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".wav"), template, 1f), "a missing file is refused");
    }

    // ---- WAV writing -------------------------------------------------------------------------

    private static byte[] WavBytes(float[] samples, int rate, int channels, int format, int bits, bool extensible = false, int mask = 0, bool junk = false)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var width = bits / 8;
        writer.Write("RIFF"u8);
        writer.Write(0);
        writer.Write("WAVE"u8);
        if (junk)
        {
            writer.Write("LIST"u8);
            writer.Write(3);
            writer.Write(new byte[] { 1, 2, 3, 0 });
        }
        writer.Write("fmt "u8);
        writer.Write(extensible ? 40 : 16);
        writer.Write((ushort)(extensible ? 0xFFFE : format));
        writer.Write((ushort)channels);
        writer.Write(rate);
        writer.Write(rate * channels * Math.Max(1, width));
        writer.Write((ushort)(channels * Math.Max(1, width)));
        writer.Write((ushort)bits);
        if (extensible)
        {
            writer.Write((ushort)22);
            writer.Write((ushort)bits);
            writer.Write(mask);
            writer.Write((ushort)format);
            writer.Write(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71 });
        }
        if (junk)
        {
            writer.Write("junk"u8);
            writer.Write(5);
            writer.Write(new byte[6]);
        }
        writer.Write("data"u8);
        writer.Write(samples.Length * Math.Max(1, width));
        foreach (var s in samples)
        {
            switch (format, bits)
            {
                case (1, 8): writer.Write((byte)Math.Clamp(Math.Round(s * 128 + 128), 0, 255)); break;
                case (1, 16) or (0x55, 16): writer.Write((short)Math.Clamp(Math.Round(s * 32768), -32768, 32767)); break;
                case (1, 24):
                    var v = (int)Math.Clamp(Math.Round(s * 8388608.0), -8388608, 8388607);
                    writer.Write((byte)v); writer.Write((byte)(v >> 8)); writer.Write((byte)(v >> 16));
                    break;
                case (1, 32): writer.Write((int)Math.Clamp(Math.Round(s * 2147483648.0), int.MinValue, int.MaxValue)); break;
                case (3, 32): writer.Write(s); break;
                case (3, 64): writer.Write((double)s); break;
                default: writer.Write((short)0); break;
            }
        }
        var bytes = stream.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), bytes.Length - 8);
        return bytes;
    }

    private static int DataSizeField(byte[] wav) => wav.AsSpan().IndexOf("data"u8) + 4;

    private static byte[] Strip(byte[] wav, string chunk)
    {
        var at = wav.AsSpan().IndexOf(Encoding.ASCII.GetBytes(chunk));
        var size = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(at + 4)) + 8;
        return [.. wav[..at], .. wav[(at + size)..]];
    }

    // ---- Synthetic SCD template --------------------------------------------------------------

    private const int TrackTableAt = 0x60, AudioTableAt = 0x70, LayoutAt = 0xA0, SoundAt = 0x120, TrackAt = 0x150, AttributeAt = 0x1B0, AudioAt = 0x230;
    private const int IntervalAt = 0x19E + 2;

    /// <summary>
    /// A template shaped like vanilla one-shots: tables, a 0x80-byte point layout, a sound with an
    /// extra block, the canonical track (KeyOn, Interval, KeyOff, End), one attribute entry and a
    /// two-block MS-ADPCM audio entry last. The bytes are invented; only the structure is vanilla's.
    /// </summary>
    internal static byte[] Template(bool marker = false, bool twoAudio = false, bool looping = false)
    {
        var markerSize = marker ? 32 : 0;
        var data = MsAdpcm.Encode(MsAdpcm.ToPcm16(Sine(44100, 1000, 0.5f, 1, 256)), 1);
        var audioEnd = AudioAt + 0x20 + markerSize + 50 + data.Length;
        var bytes = new byte[(audioEnd + 15) & ~15];
        var s = bytes.AsSpan();
        "SEDBSSCF"u8.CopyTo(s);
        BinaryPrimitives.WriteInt32LittleEndian(s[8..], 3);
        s[13] = 4;
        BinaryPrimitives.WriteInt16LittleEndian(s[14..], 0x30);
        BinaryPrimitives.WriteInt32LittleEndian(s[0x10..], bytes.Length);
        BinaryPrimitives.WriteInt16LittleEndian(s[0x30..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(s[0x32..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(s[0x34..], (short)(twoAudio ? 2 : 1));
        BinaryPrimitives.WriteInt16LittleEndian(s[0x36..], 4999);
        BinaryPrimitives.WriteInt32LittleEndian(s[0x38..], TrackTableAt);
        BinaryPrimitives.WriteInt32LittleEndian(s[0x3C..], AudioTableAt);
        BinaryPrimitives.WriteInt32LittleEndian(s[0x40..], 0x80);
        BinaryPrimitives.WriteInt32LittleEndian(s[0x48..], 0x90);
        BinaryPrimitives.WriteInt32LittleEndian(s[0x50..], SoundAt);
        BinaryPrimitives.WriteInt32LittleEndian(s[TrackTableAt..], TrackAt);
        BinaryPrimitives.WriteInt32LittleEndian(s[AudioTableAt..], AudioAt);
        if (twoAudio)
            BinaryPrimitives.WriteInt32LittleEndian(s[(AudioTableAt + 4)..], AudioAt);
        BinaryPrimitives.WriteInt32LittleEndian(s[0x80..], LayoutAt);
        BinaryPrimitives.WriteInt32LittleEndian(s[0x90..], AttributeAt);
        BinaryPrimitives.WriteUInt16LittleEndian(s[LayoutAt..], 0x80);
        s[LayoutAt + 2] = 3;
        s[LayoutAt + 3] = 1;
        s[LayoutAt + 4] = 4;
        // Sound: one track, bus 2, priority 99, normal, extra block + loop, volume 0.5, then the extra block and track 0 / audio 0.
        s[SoundAt] = 1;
        s[SoundAt + 1] = 2;
        s[SoundAt + 2] = 99;
        s[SoundAt + 3] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(s[(SoundAt + 4)..], 0x2001);
        BinaryPrimitives.WriteSingleLittleEndian(s[(SoundAt + 8)..], 0.5f);
        s[SoundAt + 15] = 0xFF;
        BinaryPrimitives.WriteUInt16LittleEndian(s[(SoundAt + 18)..], 16);
        BinaryPrimitives.WriteInt32LittleEndian(s[(SoundAt + 20)..], 6);
        BinaryPrimitives.WriteInt32LittleEndian(s[(SoundAt + 24)..], 2);
        // Track: Version 11, ReleaseRate 0, ModulationOff x3, Volume 1, Pitch 1, Panning, FrPanning, ChannelVolumeZeroOne (no channels), KeyOn, Interval 6, KeyOff, End.
        Convert.FromHexString(
            "2F000B00" + "050000000000" + "1D0003000000" + "1D0002000000" + "1D0001000000" + "01000000803F00000000" + "02000000803F00000000" +
            "06000000000000000000" + "21000000000000000000" + "3F0000000600" + "0000" + "0700" + "030006000000" + (looping ? "0E00" : "0C00") + "0000")
            .CopyTo(s[TrackAt..]);
        s[AttributeAt] = 2;
        // Audio: header, optional marker, the ADPCM format header, two blocks of a 1 kHz sine.
        BinaryPrimitives.WriteInt32LittleEndian(s[AudioAt..], data.Length);
        BinaryPrimitives.WriteInt32LittleEndian(s[(AudioAt + 4)..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(s[(AudioAt + 8)..], 44100);
        BinaryPrimitives.WriteInt32LittleEndian(s[(AudioAt + 12)..], 0x0C);
        BinaryPrimitives.WriteInt32LittleEndian(s[(AudioAt + 24)..], 50 + markerSize);
        BinaryPrimitives.WriteInt32LittleEndian(s[(AudioAt + 28)..], 0x01000000 | (marker ? 1 : 0));
        if (marker)
        {
            "MARK"u8.CopyTo(s[(AudioAt + 0x20)..]);
            BinaryPrimitives.WriteInt32LittleEndian(s[(AudioAt + 0x24)..], markerSize);
        }
        MsAdpcm.FormatHeader(1, 44100).CopyTo(s[(AudioAt + 0x20 + markerSize)..]);
        data.CopyTo(s[(AudioAt + 0x20 + markerSize + 50)..]);
        return bytes;
    }

    /// <summary>A one-sound SCD with a Vorbis entry whose data holds two fake Ogg pages; the last one's granule is the length.</summary>
    private static byte[] VorbisScd(short mode, long granule)
    {
        var bytes = Template();
        var header = new byte[40];
        var data = new byte[300];
        "OggS"u8.CopyTo(data.AsSpan(10));
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(16), granule / 2);
        "OggS"u8.CopyTo(data.AsSpan(200));
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(206), granule);
        var plain = header.Concat(data).ToArray();
        if (mode == 0x2003)
        {
            var key = data.Length & 0x7F;
            for (var i = 0; i < plain.Length; i++)
                plain[i] ^= (byte)(VorbisTable[((key & 0x3F) + i) & 0xFF] ^ key);
        }
        var info = new byte[0x20];
        BinaryPrimitives.WriteInt16LittleEndian(info, mode);
        BinaryPrimitives.WriteInt32LittleEndian(info.AsSpan(20), header.Length);
        var entry = new byte[0x20];
        BinaryPrimitives.WriteInt32LittleEndian(entry, data.Length);
        BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(4), 2);
        BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(8), 48000);
        BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(12), 0x06);
        BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(24), info.Length + header.Length);
        byte[] result = [.. bytes[..AudioAt], .. entry, .. info, .. plain];
        Array.Resize(ref result, (result.Length + 15) & ~15);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(0x10), result.Length);
        return result;
    }

    /// <summary>The 0x2003 XOR table's first bytes, enough for the fake Vorbis entry (VFXEditor ScdUtils.XORTABLE).</summary>
    private static readonly byte[] VorbisTable = Convert.FromHexString(
        "3a323232037e12f7b2e2a267323222323252161b3ca1547b1b97a6931a4baaa67a7b1b97a6f702bbaaa6bbf72a51be03f42a51be03f42a51be1206562732323632" +
        "b21a3bbc91d47b58fc0b552a15bc40920b5b7c0a951235b863d20b3bf0c714515c948694595cfc1b173a3f6b3732323032727a13b726607a13b72650ba13b42a50" +
        "ba13b52e40fa1395ae4038189a92b03800fa12b17e00db96a17c08db9a91bc08d81a86e270391f86e0787e03e764519c8f346f4e41fc0bd5ae41fc0bd5ae41fc3b" +
        "70716433321232323670342b5622703a13b72660ba1b94aa403800fab2e2a26732321232b23232323275a3267b8326f9832effe3167dc01e632107e301");

    // ---- Synthetic Ogg Vorbis ------------------------------------------------------------------

    /// <summary>
    /// A valid Ogg Vorbis stream of silence: identification, comment and a minimal setup header (one
    /// two-entry codebook, a floor 1 without partitions, an empty residue, one mapping and one short-block
    /// mode), then audio packets whose floors are all unused. Each packet after the first adds 128 frames.
    /// </summary>
    private static byte[] SilentVorbis(int channels, int rate, int packets, long? granule = null)
    {
        var identification = new byte[30];
        identification[0] = 1;
        "vorbis"u8.CopyTo(identification.AsSpan(1));
        identification[11] = (byte)channels;
        BinaryPrimitives.WriteInt32LittleEndian(identification.AsSpan(12), rate);
        identification[28] = 0x88; // block sizes 256 and 256
        identification[29] = 1;
        var vendor = "fixture"u8.ToArray();
        byte[] comment = [3, .. "vorbis"u8, .. BitConverter.GetBytes(vendor.Length), .. vendor, 0, 0, 0, 0, 1];
        var bits = new BitWriter();
        foreach (var b in new byte[] { 5, (byte)'v', (byte)'o', (byte)'r', (byte)'b', (byte)'i', (byte)'s' })
            bits.Write(b, 8);
        bits.Write(0, 8);              // one codebook
        bits.Write(0x564342, 24);
        bits.Write(1, 16);             // dimensions
        bits.Write(2, 24);             // entries
        bits.Write(0, 1);              // not ordered
        bits.Write(0, 1);              // not sparse
        bits.Write(0, 5);              // length 1
        bits.Write(0, 5);              // length 1
        bits.Write(0, 4);              // no lookup
        bits.Write(0, 6);              // one time-domain placeholder
        bits.Write(0, 16);
        bits.Write(0, 6);              // one floor
        bits.Write(1, 16);             // floor type 1
        bits.Write(0, 5);              // no partitions
        bits.Write(1, 2);              // multiplier 2
        bits.Write(7, 4);              // range bits: x runs to 128
        bits.Write(0, 6);              // one residue
        bits.Write(0, 16);             // residue type 0
        bits.Write(0, 24);             // begin
        bits.Write(0, 24);             // end
        bits.Write(0, 24);             // partition size 1
        bits.Write(0, 6);              // one classification
        bits.Write(0, 8);              // classbook 0
        bits.Write(0, 3);              // no cascade
        bits.Write(0, 1);
        bits.Write(0, 6);              // one mapping
        bits.Write(0, 16);             // mapping type 0
        bits.Write(0, 1);              // one submap
        bits.Write(0, 1);              // no coupling
        bits.Write(0, 2);
        bits.Write(0, 8);              // submap: time, floor 0, residue 0
        bits.Write(0, 8);
        bits.Write(0, 8);
        bits.Write(0, 6);              // one mode
        bits.Write(0, 1);              // short blocks
        bits.Write(0, 16);
        bits.Write(0, 16);
        bits.Write(0, 8);
        bits.Write(1, 1);              // framing
        var setup = bits.ToArray();
        using var stream = new MemoryStream();
        var sequence = 0;
        OggPage(stream, 2, 0, sequence++, [identification]);
        OggPage(stream, 0, 0, sequence++, [comment, setup]);
        // Audio packet: type 0, then one "floor unused" bit per channel.
        var audio = new byte[] { 0 };
        OggPage(stream, 4, granule ?? (packets - 1) * 128L, sequence, Enumerable.Repeat(audio, packets).ToArray());
        return stream.ToArray();
    }

    private static void OggPage(Stream stream, byte flags, long granule, int sequence, byte[][] packets)
    {
        var lacing = new List<byte>();
        foreach (var packet in packets)
        {
            var left = packet.Length;
            while (left >= 255)
            {
                lacing.Add(255);
                left -= 255;
            }
            lacing.Add((byte)left);
        }
        var page = new List<byte>();
        page.AddRange("OggS"u8.ToArray());
        page.Add(0);
        page.Add(flags);
        page.AddRange(BitConverter.GetBytes(granule));
        page.AddRange(BitConverter.GetBytes(0x1234));
        page.AddRange(BitConverter.GetBytes(sequence));
        page.AddRange(new byte[4]);
        page.Add((byte)lacing.Count);
        page.AddRange(lacing);
        foreach (var packet in packets)
            page.AddRange(packet);
        var bytes = page.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(22), OggCrc(bytes));
        stream.Write(bytes);
    }

    private static uint OggCrc(byte[] bytes)
    {
        uint crc = 0;
        foreach (var b in bytes)
        {
            crc ^= (uint)b << 24;
            for (var i = 0; i < 8; i++)
                crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7 : crc << 1;
        }
        return crc;
    }

    private sealed class BitWriter
    {
        private readonly List<byte> bytes = [];
        private int used;

        public void Write(long value, int count)
        {
            for (var i = 0; i < count; i++)
            {
                if (used == 0)
                    bytes.Add(0);
                if (((value >> i) & 1) != 0)
                    bytes[^1] |= (byte)(1 << used);
                used = (used + 1) % 8;
            }
        }

        public byte[] ToArray() => [.. bytes];
    }

    // ---- Signals -------------------------------------------------------------------------------

    private static float[] Sine(int rate, double frequency, float amplitude, int channels, int frames)
    {
        var samples = new float[frames * channels];
        for (var f = 0; f < frames; f++)
            for (var c = 0; c < channels; c++)
                samples[f * channels + c] = amplitude * (float)Math.Sin(2 * Math.PI * frequency * f / rate);
        return samples;
    }

    private static float[] Noise(int frames, float amplitude)
    {
        var random = new Random(1234);
        return Enumerable.Range(0, frames).Select(_ => amplitude * (float)(random.NextDouble() * 2 - 1)).ToArray();
    }

    private static float[] Chirp(int rate)
    {
        var samples = new float[rate];
        for (var i = 0; i < samples.Length; i++)
        {
            var t = (double)i / rate;
            samples[i] = 0.6f * (float)Math.Sin(2 * Math.PI * (100 * t + 4000 * t * t));
        }
        return samples;
    }

    private static float[] Interleave(float[] left, float[] right)
    {
        var result = new float[left.Length * 2];
        for (var i = 0; i < left.Length; i++)
        {
            result[i * 2] = left[i];
            result[i * 2 + 1] = right[i];
        }
        return result;
    }

    private static float MaxError(float[] a, float[] b) => a.Length != b.Length ? float.MaxValue : a.Zip(b, (x, y) => Math.Abs(x - y)).DefaultIfEmpty(0).Max();

    private static double Rms(ReadOnlySpan<float> samples)
    {
        double sum = 0;
        foreach (var s in samples)
            sum += s * s;
        return Math.Sqrt(sum / samples.Length);
    }

    private static double Snr(ReadOnlySpan<float> reference, ReadOnlySpan<float> test)
    {
        double signal = 0, noise = 0;
        for (var i = 0; i < reference.Length; i++)
        {
            signal += reference[i] * (double)reference[i];
            var d = reference[i] - (double)test[i];
            noise += d * d;
        }
        return 10 * Math.Log10(signal / Math.Max(noise, 1e-12));
    }
}
