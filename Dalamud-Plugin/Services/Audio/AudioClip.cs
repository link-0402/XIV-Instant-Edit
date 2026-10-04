namespace InstantEdit.Services.Audio;

/// <summary>
/// Decoded audio as interleaved float samples, nominally in [-1, 1], one or two channels. Readers
/// downmix wider audio to stereo first, so the encoder only ever sees mono or stereo. Dalamud-free.
/// </summary>
internal sealed class AudioClip
{
    /// <summary>The longest clip an import accepts, the timeline's own 10-minute limit.</summary>
    public const int MaxSeconds = 600;
    public const int MinRate = 8000;
    /// <summary>Higher rates would let a 10-minute clip take gigabytes once decoded.</summary>
    public const int MaxRate = 96000;
    /// <summary>The widest source an import downmixes. Wider files are refused as malformed.</summary>
    public const int MaxSourceChannels = 32;

    public float[] Samples { get; }
    public int SampleRate { get; }
    public int Channels { get; }
    public int Frames => Samples.Length / Channels;
    public double Seconds => (double)Frames / SampleRate;

    public AudioClip(float[] samples, int sampleRate, int channels)
    {
        if (channels is not (1 or 2))
            throw new InvalidDataException("Audio must be mono or stereo.");
        CheckFormat(sampleRate, channels);
        if (samples.Length == 0 || samples.Length % channels != 0)
            throw new InvalidDataException("The audio has no whole sample frames.");
        CheckLength(samples.Length / channels, sampleRate, channels);
        Samples = samples;
        SampleRate = sampleRate;
        Channels = channels;
    }

    /// <summary>Refuses a source format before any of it is decoded.</summary>
    public static void CheckFormat(int sampleRate, int channels)
    {
        if (channels < 1 || channels > MaxSourceChannels)
            throw new InvalidDataException($"Audio with {channels} channels is not supported.");
        if (sampleRate < MinRate || sampleRate > MaxRate)
            throw new InvalidDataException($"Sample rates from {MinRate} to {MaxRate} Hz are supported, not {sampleRate} Hz.");
    }

    /// <summary>The most samples a source may decode to: 10 minutes of stereo at the highest rate (440 MiB).</summary>
    public const long MaxDecodedSamples = 2L * MaxRate * MaxSeconds;

    /// <summary>Refuses a source by its declared length, so a long file is never decoded.</summary>
    public static void CheckLength(long frames, int sampleRate, int channels = 1)
    {
        if (frames <= 0)
            throw new InvalidDataException("The audio is empty.");
        if (frames > (long)MaxSeconds * sampleRate)
            throw new InvalidDataException("Audio longer than 10 minutes is not supported.");
        if (frames * channels > MaxDecodedSamples)
            throw new InvalidDataException("The audio has too many channels for its length.");
    }

    /// <summary>
    /// A clip from decoded interleaved samples, downmixed to stereo when there are more than two
    /// channels. <paramref name="speakers"/> gives each source channel's WAVE speaker bit (front
    /// left 0x1, front right 0x2, center 0x4, LFE 0x8, back left 0x10, ...), 0 when unknown.
    /// </summary>
    public static AudioClip FromInterleaved(float[] samples, int sampleRate, int channels, ReadOnlySpan<int> speakers)
    {
        CheckFormat(sampleRate, channels);
        if (channels <= 2)
            return new AudioClip(samples, sampleRate, channels);
        var frames = samples.Length / channels;
        Span<float> left = stackalloc float[channels], right = stackalloc float[channels];
        float leftSum = 0, rightSum = 0;
        for (var c = 0; c < channels; c++)
        {
            (left[c], right[c]) = Weight(c < speakers.Length ? speakers[c] : 0, c);
            leftSum += left[c];
            rightSum += right[c];
        }
        // Normalize each side by its total weight so the downmix can never clip.
        for (var c = 0; c < channels; c++)
        {
            left[c] = leftSum > 0 ? left[c] / leftSum : 0;
            right[c] = rightSum > 0 ? right[c] / rightSum : 0;
        }
        var result = new float[frames * 2];
        for (var f = 0; f < frames; f++)
        {
            float l = 0, r = 0;
            var source = samples.AsSpan(f * channels, channels);
            for (var c = 0; c < channels; c++)
            {
                l += source[c] * left[c];
                r += source[c] * right[c];
            }
            result[f * 2] = l;
            result[f * 2 + 1] = r;
        }
        return new AudioClip(result, sampleRate, 2);
    }

    /// <summary>The WAVE speaker bits a file without a channel mask implies, by channel count.</summary>
    public static int DefaultWaveMask(int channels) => channels switch
    {
        1 => 0x4,
        2 => 0x3,
        3 => 0x7,
        4 => 0x33,
        5 => 0x37,
        6 => 0x3F,
        7 => 0x70F,
        8 => 0x63F,
        _ => 0,
    };

    /// <summary>Each channel's speaker bit from a WAVE channel mask: set bits in ascending order, then unknown.</summary>
    public static int[] SpeakersFromMask(int mask, int channels)
    {
        var speakers = new int[channels];
        var next = 0;
        for (var bit = 0; bit < 32 && next < channels; bit++)
            if ((mask & (1 << bit)) != 0)
                speakers[next++] = 1 << bit;
        return speakers;
    }

    private const float Side = 0.7071f;

    private static (float Left, float Right) Weight(int speaker, int index) => speaker switch
    {
        0x1 or 0x40 => (1, 0),
        0x2 or 0x80 => (0, 1),
        0x4 or 0x100 or 0x800 or 0x2000 or 0x10000 => (Side, Side),
        0x8 => (0, 0),
        0x10 or 0x200 or 0x1000 or 0x8000 => (Side, 0),
        0x20 or 0x400 or 0x4000 or 0x20000 => (0, Side),
        _ => index % 2 == 0 ? (Side, 0) : (0, Side),
    };
}
