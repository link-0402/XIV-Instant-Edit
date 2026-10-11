using System.Collections.Immutable;
using System.Globalization;
using InstantEdit.Services.Audio;

namespace InstantEdit.Ui;

/// <summary>The sound inspector's words: what each option of a sound event and its file does.</summary>
internal static partial class TimelineWindowViews
{
    /// <summary>The C063 Loop value: 1 plays once, -1 keeps the sound going while the animation loops; others are kept as they are.</summary>
    public static string PlaysText(double loop) => loop switch
    {
        1 => "Once",
        -1 => "Keep looping",
        _ => $"Custom ({loop.ToString(CultureInfo.InvariantCulture)})",
    };

    /// <summary>Under "Fades with distance": the file's Point layout ranges, or what fading does when they are unknown.</summary>
    public static string RangeHint(float? fullWithin, float? silentPast) =>
        fullWithin is { } near && silentPast is { } far
            ? string.Create(CultureInfo.InvariantCulture, $"Full volume within {near:0.#}, silent past {far:0.#}.")
            : "Fades as the camera moves away.";

    /// <summary>Which alternative an event plays: any, or the pinned one by its number in the list.</summary>
    public static string WhichText(string? pin, ImmutableArray<string> ids) =>
        pin == null ? $"Random, any of {ids.Length}"
        : ids.IndexOf(pin) is >= 0 and var index ? $"Only #{index + 1}"
        : "A removed sound";

    /// <summary>
    /// Whether sound <paramref name="sound"/> of <paramref name="scd"/> keeps playing: marked to loop,
    /// and every Vorbis or MS-ADPCM audio it plays has loop points (without them it plays once).
    /// </summary>
    public static bool FileLoops(byte[] scd, int sound)
    {
        try
        {
            var file = ScdFile.Read(scd);
            if (sound < 0 || sound >= file.Sounds.Length || !file.Sounds[sound].Loops)
                return false;
            return file.Sounds[sound].Tracks.Select(t => t.Audio).Where(a => a >= 0 && a < file.Audio.Length).Select(a => file.Audio[a])
                .Where(a => a.Codec is ScdCodec.Vorbis or ScdCodec.MsAdpcm && a.DataLength > 0)
                .All(a => a.LoopEnd > a.LoopStart);
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
}
