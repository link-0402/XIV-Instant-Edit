using System.Collections.Immutable;
using System.Globalization;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Services.Audio;

namespace InstantEdit.Ui;

/// <summary>Where an imported sound goes: added to <paramref name="Key"/>'s set, or in place of the alternative <paramref name="ReplaceId"/>.</summary>
internal sealed record TimelineSoundTarget(TimelineSoundKey Key, string? ReplaceId);

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

    /// <summary>The Sound file section's first line: what kind of sound, how many alternatives, how long they last and where the file comes from.</summary>
    public static string SoundSummary(int type, int count, double shortest, double longest, string origin)
    {
        var kind = type switch
        {
            ScdSound.GroupRandom or 2 => "Random set",
            ScdSound.Normal => "One sound",
            ScdSound.Cycle => "Cycle",
            _ => "Sound",
        };
        var low = shortest.ToString("0.0", CultureInfo.InvariantCulture);
        var high = longest.ToString("0.0", CultureInfo.InvariantCulture);
        return $"{kind} · {count} {(count == 1 ? "sound" : "sounds")} · {(low == high ? low : $"{low} to {high}")} s · {origin}";
    }

    /// <summary>Under the chances: whether they add up to 100%, how much plays nothing, or that they add up to too much.</summary>
    public static string ChanceFooter(int sum) =>
        sum == 100 ? "Adds up to 100%."
        : sum < 100 ? $"{100 - sum}% plays nothing."
        : $"Adds up to {sum}%. Lower some chances.";

    /// <summary>The ends-early mark's tooltip: the track's wait is shorter than its audio.</summary>
    public static string EndsEarlyTip(int waitMs, int audioMs) =>
        $"The track ends {((audioMs - waitMs) / 1000.0).ToString("0.00", CultureInfo.InvariantCulture)} s before the audio. The end may be cut off.";

    /// <summary>The channels (buses) the SCD editing guide names; others by number.</summary>
    public static string ChannelName(int bus) => bus switch
    {
        1 => "BGM",
        2 => "Sound effects",
        5 => "Ambient",
        16 => "Orchestrion",
        17 => "Performance",
        _ => $"Channel {bus}",
    };

    /// <summary>The channels offered in the Channel menu, besides the sound's own.</summary>
    public static readonly ImmutableArray<int> KnownChannels = [1, 2, 5, 16, 17];

    /// <summary>Equal chances for <paramref name="count"/> sounds: 100/n each, the remainder on the last.</summary>
    public static ImmutableArray<int> EvenShares(int count)
    {
        if (count <= 0)
            return [];
        var shares = Enumerable.Repeat(100 / count, count).ToArray();
        shares[^1] += 100 - shares.Sum();
        return [.. shares];
    }

    /// <summary>
    /// The chances after adding <paramref name="count"/> sounds: each new one gets 100/(n+count), the
    /// others shrink to fit what is left (keeping the share that plays nothing), rounded down, and the
    /// remainder goes to the last one. For one sound this is <see cref="ScdEdits.Add"/>'s rule.
    /// </summary>
    public static ImmutableArray<int> AddedShares(ImmutableArray<int> shares, int nothing, int count)
    {
        var each = 100 / (shares.Length + count);
        var rest = Math.Max(0, 100 - Math.Max(0, nothing) - each * count);
        var sum = shares.Sum();
        int[] result = [.. shares.Select(s => sum <= 0 ? 0 : (int)((long)s * rest / sum)), .. Enumerable.Repeat(each, count)];
        result[^1] += rest - result.Take(shares.Length).Sum();
        return [.. result];
    }

    /// <summary>
    /// <paramref name="edit"/> with an imported file placed: in place of the alternative
    /// <paramref name="replaceId"/> (its first sound, keeping the chance), or else each of its
    /// <paramref name="clips"/> sounds added as new alternatives numbered after the others, with even
    /// shares made room for (limit 100 in a set with its own limits). <paramref name="makeRandom"/>
    /// turns a one-shot into a random set first.
    /// </summary>
    public static TimelineSoundEdit Placed(TimelineSoundEdit edit, string? replaceId, string importPath, int clips, bool makeRandom)
    {
        var alternatives = edit.Alternatives.IsDefault ? [] : edit.Alternatives;
        if (replaceId != null)
            return edit with { Alternatives = [.. alternatives.Select(a => a.Id == replaceId ? a with { Import = importPath, Entry = 0 } : a)] };
        var next = alternatives.Select(a => a.Id.StartsWith(TimelineSoundAlternative.AddedPrefix, StringComparison.Ordinal) &&
                                            int.TryParse(a.Id.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0)
            .DefaultIfEmpty(0).Max() + 1;
        var added = Enumerable.Range(0, clips).Select(j => new TimelineSoundAlternative($"{TimelineSoundAlternative.AddedPrefix}{next + j}", j, importPath,
            0, 100, (short)(alternatives.Length + j), null, null)).ToList();
        var all = alternatives.Concat(added).ToImmutableArray();
        if (!edit.Custom)
        {
            var shares = AddedShares([.. alternatives.Select(a => a.Share)], 100 - alternatives.Sum(a => a.Share), clips);
            all = [.. all.Select((a, i) => a with { Share = shares[i] })];
        }
        return edit with { Alternatives = all, Random = edit.Random || makeRandom };
    }

    /// <summary>
    /// How a sound clip is drawn, in frames at 30 fps rounded up: solid to its shortest alternative and
    /// on to its longest; a pinned clip is that one alternative's length.
    /// </summary>
    public static (int Solid, int Total) SoundSpan(ImmutableArray<double> seconds, int? pinned)
    {
        if (seconds.IsDefaultOrEmpty)
            return (0, 0);
        if (pinned is { } index && index >= 0 && index < seconds.Length)
            return (SoundFrames((float)seconds[index]), SoundFrames((float)seconds[index]));
        return (SoundFrames((float)seconds.Min()), SoundFrames((float)seconds.Max()));
    }

    /// <summary>
    /// What the save question says about one sound file other animations also play: <paramref name="users"/>
    /// of the mod, and the game's own when the mod's file replaces a game sound (<paramref name="game"/>).
    /// </summary>
    public static string SoundAsk(string file, int users, bool several, bool game = false) =>
        (users > 0
            ? $"{file} is also played by {users} other {(users == 1 ? "animation" : "animations")} in this mod" + (game ? " and by the game's own." : ".")
            : $"{file} replaces a game sound, so the game's own animations play it too.") +
        (several ? " Only the file your collection uses changes." : "");

    /// <summary>Why an event's pin can't be used: it names a sound the file doesn't have (any more); null when it can.</summary>
    public static string? PinGone(string? pin, ImmutableArray<string> ids, string file) =>
        pin != null && (ids.IsDefault || !ids.Contains(pin)) ? $"{file}: an event always plays a sound that is gone. Pick another in Which sound." : null;

    /// <summary>Why an imported sound can't take the place of <paramref name="replaceId"/>: it was removed meanwhile. Null when it can, or when it is added.</summary>
    public static string? ReplaceGone(TimelineSoundEdit edit, string? replaceId, string name) =>
        replaceId != null && (edit.Alternatives.IsDefault || !edit.Alternatives.Any(a => a.Id == replaceId))
            ? $"{name} was imported, but the sound it was to replace is gone. Add it again."
            : null;

    /// <summary><see cref="FileLoops(byte[], int)"/> for a file already read.</summary>
    public static bool FileLoops(ScdModel model, int sound) =>
        sound >= 0 && sound < model.Sounds.Count && (model.Sounds[sound].Attributes & ScdSound.LoopAttribute) != 0 &&
        model.Sounds[sound].Entries.Select(e => e.Audio).Where(a => a >= 0 && a < model.Audio.Count).Select(a => model.Audio[a])
            .Where(a => !a.Null && a.Codec is ScdCodec.Vorbis or ScdCodec.MsAdpcm && a.Data.Length > 0)
            .All(a => a.LoopEnd > a.LoopStart);

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
