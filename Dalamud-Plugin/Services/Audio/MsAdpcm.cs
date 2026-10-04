using System.Buffers.Binary;

namespace InstantEdit.Services.Audio;

/// <summary>
/// Standard Microsoft ADPCM: 4-bit nibbles, the seven fixed coefficient pairs, and a block header
/// per channel with its predictor index, step size and two whole samples. The block size matches
/// vanilla SCDs: all 8,874 MS-ADPCM entries in a 2026-10-02 survey of 8,592 vanilla SCDs use 128
/// samples per block (block align 70 mono, 140 stereo), whole blocks only, with the standard
/// coefficients. The encoder tries every predictor and a few starting step sizes for each block
/// and channel and keeps the one with the least error. Dalamud-free.
/// </summary>
internal static class MsAdpcm
{
    public const int SamplesPerBlock = 128;
    public const int FormatTag = 2;
    /// <summary>WAVEFORMATEX (18 bytes) plus samples per block, coefficient count and the 7 pairs.</summary>
    public const int FormatSize = 50;
    private const int HeaderBytes = 7;
    private const int MinDelta = 16;
    private static readonly int[] Coef1 = [256, 512, 0, 192, 240, 460, 392];
    private static readonly int[] Coef2 = [0, -256, 0, 64, 0, -208, -232];
    private static readonly int[] Adaptation = [230, 230, 230, 230, 307, 409, 512, 614, 768, 614, 512, 409, 307, 230, 230, 230];

    public static int BlockAlign(int channels) => channels * (HeaderBytes + (SamplesPerBlock - 2) / 2);

    /// <summary>Samples per channel in a block of the given size, as a decoder derives it.</summary>
    public static int SamplesIn(int blockAlign, int channels) => (blockAlign - HeaderBytes * channels) * 2 / channels + 2;

    /// <summary>The WAVEFORMATEX with the ADPCM extension that an SCD entry's sub-info carries.</summary>
    public static byte[] FormatHeader(int channels, int sampleRate)
    {
        var align = BlockAlign(channels);
        var header = new byte[FormatSize];
        var span = header.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(span, FormatTag);
        BinaryPrimitives.WriteUInt16LittleEndian(span[2..], (ushort)channels);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], (int)((long)sampleRate * align / SamplesPerBlock));
        BinaryPrimitives.WriteUInt16LittleEndian(span[12..], (ushort)align);
        BinaryPrimitives.WriteUInt16LittleEndian(span[14..], 4);
        BinaryPrimitives.WriteUInt16LittleEndian(span[16..], FormatSize - 18);
        BinaryPrimitives.WriteUInt16LittleEndian(span[18..], SamplesPerBlock);
        BinaryPrimitives.WriteUInt16LittleEndian(span[20..], (ushort)Coef1.Length);
        for (var i = 0; i < Coef1.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(span[(22 + i * 4)..], (short)Coef1[i]);
            BinaryPrimitives.WriteInt16LittleEndian(span[(24 + i * 4)..], (short)Coef2[i]);
        }
        return header;
    }

    /// <summary>Float samples to the 16-bit integers the codec works on, rounded and clamped.</summary>
    public static short[] ToPcm16(ReadOnlySpan<float> samples)
    {
        var result = new short[samples.Length];
        for (var i = 0; i < samples.Length; i++)
            result[i] = (short)Math.Clamp(MathF.Round(samples[i] * 32767f), -32768f, 32767f);
        return result;
    }

    /// <summary>
    /// Encodes interleaved 16-bit samples to whole blocks. The last block is padded with silence,
    /// as every vanilla entry is a whole number of blocks.
    /// </summary>
    public static byte[] Encode(ReadOnlySpan<short> interleaved, int channels)
    {
        if (channels is not (1 or 2))
            throw new InvalidDataException("MS-ADPCM encoding takes mono or stereo audio.");
        var frames = interleaved.Length / channels;
        var blocks = Math.Max(1, (frames + SamplesPerBlock - 1) / SamplesPerBlock);
        var align = BlockAlign(channels);
        var output = new byte[(long)blocks * align <= int.MaxValue ? blocks * align : throw new InvalidDataException("The audio is too long to encode.")];
        var source = new int[channels][];
        var codes = new byte[channels][];
        var deltas = new int[channels];
        for (var c = 0; c < channels; c++)
        {
            source[c] = new int[SamplesPerBlock];
            codes[c] = new byte[SamplesPerBlock];
            deltas[c] = MinDelta;
        }
        var trial = new byte[SamplesPerBlock];
        for (var b = 0; b < blocks; b++)
        {
            var block = output.AsSpan(b * align, align);
            for (var c = 0; c < channels; c++)
            {
                var s = source[c];
                for (var i = 0; i < SamplesPerBlock; i++)
                {
                    var frame = b * SamplesPerBlock + i;
                    s[i] = frame < frames ? interleaved[frame * channels + c] : 0;
                }
                // Starting step sizes: the previous block's last one, the floor, and one sized to the opening residual.
                var opening = 0;
                for (var i = 2; i < Math.Min(6, SamplesPerBlock); i++)
                    opening += Math.Abs(s[i] - s[i - 1]);
                Span<int> starts = [Math.Clamp(deltas[c], MinDelta, short.MaxValue), MinDelta, Math.Clamp(opening / 8, MinDelta, short.MaxValue)];
                long best = long.MaxValue;
                int bestPredictor = 0, bestStart = MinDelta, bestEnd = MinDelta;
                for (var p = 0; p < Coef1.Length; p++)
                    foreach (var start in starts)
                    {
                        var error = EncodeChannel(s, p, start, trial, best, out var end);
                        if (error >= best)
                            continue;
                        best = error;
                        bestPredictor = p;
                        bestStart = start;
                        bestEnd = end;
                        trial.AsSpan(0, SamplesPerBlock).CopyTo(codes[c]);
                    }
                block[c] = (byte)bestPredictor;
                BinaryPrimitives.WriteInt16LittleEndian(block[(channels + c * 2)..], (short)bestStart);
                BinaryPrimitives.WriteInt16LittleEndian(block[(channels * 3 + c * 2)..], (short)s[1]);
                BinaryPrimitives.WriteInt16LittleEndian(block[(channels * 5 + c * 2)..], (short)s[0]);
                deltas[c] = bestEnd;
            }
            // Nibbles follow in interleaved sample order, high nibble first.
            var nibble = 0;
            for (var i = 2; i < SamplesPerBlock; i++)
                for (var c = 0; c < channels; c++, nibble++)
                {
                    ref var target = ref block[HeaderBytes * channels + nibble / 2];
                    target = nibble % 2 == 0 ? (byte)(codes[c][i] << 4) : (byte)(target | codes[c][i]);
                }
        }
        return output;
    }

    /// <summary>
    /// One channel of one block with a fixed predictor and starting step; returns the squared error,
    /// or stops early with an error of at least <paramref name="bound"/> once it cannot win.
    /// </summary>
    private static long EncodeChannel(int[] s, int predictor, int delta, byte[] codes, long bound, out int endDelta)
    {
        int c1 = Coef1[predictor], c2 = Coef2[predictor];
        int sample1 = s[1], sample2 = s[0];
        long error = 0;
        for (var i = 2; i < SamplesPerBlock; i++)
        {
            var predicted = (sample1 * c1 + sample2 * c2) / 256;
            var diff = s[i] - predicted;
            // diff / delta rounded half away from zero, in integers.
            var code = Math.Clamp((2 * diff + (diff < 0 ? -delta : delta)) / (2 * delta), -8, 7);
            // Keep the next step size within 16 bits for decoders that store it that way.
            while ((long)delta * Adaptation[code & 0xF] >> 8 > short.MaxValue)
                code -= Math.Sign(code);
            var decoded = Math.Clamp(predicted + code * delta, short.MinValue, short.MaxValue);
            var miss = decoded - s[i];
            error += (long)miss * miss;
            if (error >= bound)
            {
                endDelta = delta;
                return error;
            }
            codes[i] = (byte)(code & 0xF);
            sample2 = sample1;
            sample1 = decoded;
            delta = Math.Max(MinDelta, delta * Adaptation[code & 0xF] >> 8);
        }
        endDelta = delta;
        return error;
    }

    /// <summary>
    /// Decodes whole blocks with the standard coefficients to interleaved 16-bit samples. A trailing
    /// partial block decodes as far as it goes, as decoders do with the end of a stream.
    /// </summary>
    public static short[] Decode(ReadOnlySpan<byte> data, int channels, int blockAlign)
    {
        if (channels is not (1 or 2) || blockAlign < HeaderBytes * channels + channels)
            throw new InvalidDataException("The MS-ADPCM layout is invalid.");
        var perBlock = SamplesIn(blockAlign, channels);
        var output = new List<short>(data.Length / blockAlign * perBlock * channels + perBlock * channels);
        Span<int> delta = stackalloc int[channels], sample1 = stackalloc int[channels], sample2 = stackalloc int[channels];
        Span<int> c1 = stackalloc int[channels], c2 = stackalloc int[channels];
        for (var at = 0; at + HeaderBytes * channels <= data.Length; at += blockAlign)
        {
            var block = data.Slice(at, Math.Min(blockAlign, data.Length - at));
            for (var c = 0; c < channels; c++)
            {
                var predictor = block[c];
                if (predictor >= Coef1.Length)
                    throw new InvalidDataException("An MS-ADPCM block names an unknown predictor.");
                c1[c] = Coef1[predictor];
                c2[c] = Coef2[predictor];
                delta[c] = BinaryPrimitives.ReadInt16LittleEndian(block[(channels + c * 2)..]);
                sample1[c] = BinaryPrimitives.ReadInt16LittleEndian(block[(channels * 3 + c * 2)..]);
                sample2[c] = BinaryPrimitives.ReadInt16LittleEndian(block[(channels * 5 + c * 2)..]);
            }
            for (var c = 0; c < channels; c++)
                output.Add((short)sample2[c]);
            for (var c = 0; c < channels; c++)
                output.Add((short)sample1[c]);
            var nibbles = (block.Length - HeaderBytes * channels) * 2;
            for (var n = 0; n < nibbles; n++)
            {
                var c = n % channels;
                var value = block[HeaderBytes * channels + n / 2];
                var code = n % 2 == 0 ? value >> 4 : value & 0xF;
                var signed = code >= 8 ? code - 16 : code;
                var predicted = (sample1[c] * c1[c] + sample2[c] * c2[c]) / 256;
                var decoded = Math.Clamp(predicted + signed * delta[c], short.MinValue, short.MaxValue);
                sample2[c] = sample1[c];
                sample1[c] = decoded;
                delta[c] = Math.Max(MinDelta, delta[c] * Adaptation[code] >> 8);
                output.Add((short)decoded);
            }
        }
        return [.. output];
    }
}
