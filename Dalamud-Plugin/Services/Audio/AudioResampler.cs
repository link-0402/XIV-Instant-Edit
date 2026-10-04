namespace InstantEdit.Services.Audio;

/// <summary>
/// Picks the sample rate an SCD gets and converts to it with a windowed-sinc filter. A 2026-10-02
/// survey of 8,592 vanilla SCDs found every MS-ADPCM entry between 43,736 and 48,095 Hz (game
/// files carry odd rates like 44,116), so sources in that range keep their rate and everything
/// else moves to 44,100 or 48,000 Hz. Dalamud-free.
/// </summary>
internal static class AudioResampler
{
    public const int MinKeptRate = 43736;
    public const int MaxKeptRate = 48095;
    /// <summary>Kernel half-width, in samples of the lower rate; with the Blackman window the stopband starts near that rate's Nyquist.</summary>
    private const int Zeros = 16;
    /// <summary>The passband edge as a fraction of the lower of the two Nyquist rates, leaving the window room to roll off.</summary>
    private const double Cutoff = 0.92;
    private const int TableSteps = 512;

    public static int ScdRate(int rate) => rate is >= MinKeptRate and <= MaxKeptRate ? rate : rate > MaxKeptRate ? 48000 : 44100;

    /// <summary>The clip at the rate an SCD gets, unchanged when it already has it.</summary>
    public static AudioClip ForScd(AudioClip clip)
    {
        var rate = ScdRate(clip.SampleRate);
        return rate == clip.SampleRate ? clip : Resample(clip, rate);
    }

    public static AudioClip Resample(AudioClip clip, int rate)
    {
        AudioClip.CheckFormat(rate, clip.Channels);
        if (rate == clip.SampleRate)
            return clip;
        var source = clip.SampleRate;
        var channels = clip.Channels;
        var input = clip.Samples;
        var frames = clip.Frames;
        // Downsampling lowers the cutoff to the new Nyquist and widens the kernel to match.
        var scale = Math.Min(1.0, (double)rate / source);
        var half = (int)Math.Ceiling(Zeros / scale);
        var table = Kernel(scale, half);
        var outFrames = (int)Math.Max(1, ((long)frames * rate + source - 1) / source);
        var output = new float[outFrames * channels];
        var taps = 2 * half;
        // Output positions fall on rate / gcd distinct source phases (one for 96 to 48 kHz, 441 for
        // 32 to 44.1 kHz): compute each phase's taps once when that table stays small.
        var step = Gcd(source, rate);
        var phases = rate / step;
        var cache = (long)phases * taps <= 1 << 20 ? new float[phases * taps] : null;
        if (cache != null)
            for (var p = 0; p < phases; p++)
                Weights(table, (double)p * step / rate, cache.AsSpan(p * taps, taps));
        var scratch = cache == null ? new float[taps] : [];
        for (var n = 0; n < outFrames; n++)
        {
            // Exact rational position, so long clips do not drift.
            var position = (long)n * source;
            var center = (int)(position / rate);
            var remainder = (int)(position % rate);
            ReadOnlySpan<float> weights;
            if (cache != null)
                weights = cache.AsSpan(remainder / step * taps, taps);
            else
            {
                Weights(table, (double)remainder / rate, scratch);
                weights = scratch;
            }
            var first = center - half + 1;
            for (var c = 0; c < channels; c++)
            {
                float sum = 0;
                for (var k = 0; k < weights.Length; k++)
                {
                    var at = first + k;
                    if (at >= 0 && at < frames)
                        sum += input[at * channels + c] * weights[k];
                }
                output[n * channels + c] = sum;
            }
        }
        return new AudioClip(output, rate, channels);
    }

    /// <summary>
    /// The taps for an output that lies <paramref name="fraction"/> of a source sample past its
    /// center, normalized so the gain is exactly one at every fractional position.
    /// </summary>
    private static void Weights(double[] table, double fraction, Span<float> weights)
    {
        var half = weights.Length / 2;
        float total = 0;
        for (var k = 0; k < weights.Length; k++)
        {
            var x = Math.Abs(k - half + 1 - fraction) * TableSteps;
            var i = (int)x;
            var w = i + 1 < table.Length ? (float)(table[i] + (table[i + 1] - table[i]) * (x - i)) : 0f;
            weights[k] = w;
            total += w;
        }
        var gain = total != 0 ? 1 / total : 0;
        for (var k = 0; k < weights.Length; k++)
            weights[k] *= gain;
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0)
            (a, b) = (b, a % b);
        return a;
    }

    /// <summary>The Blackman-windowed sinc, sampled every 1/<see cref="TableSteps"/> of a source sample.</summary>
    private static double[] Kernel(double scale, int half)
    {
        var table = new double[half * TableSteps + 2];
        for (var i = 0; i < table.Length; i++)
        {
            var x = (double)i / TableSteps;
            if (x >= half)
                continue;
            var t = Cutoff * scale * x * Math.PI;
            var sinc = t == 0 ? 1 : Math.Sin(t) / t;
            var phase = Math.PI * (x / half + 1);
            var window = 0.42 - 0.5 * Math.Cos(phase) + 0.08 * Math.Cos(2 * phase);
            table[i] = sinc * window;
        }
        return table;
    }
}
