using System.Collections.Immutable;

namespace InstantEdit.Services.Audio;

/// <summary>A sound to add or swap in: an audio entry and the track that played it, from another SCD.</summary>
internal sealed record ScdClip(ScdModelAudio Audio, ScdModelTrack? Track);

internal static partial class ScdEdits
{
    // Track commands that name audio entries or sounds of their own bank by index.
    private const int ExternalAudio = 0x0F, MidiExternalAudio = 0x2C, PlayInnerSound = 0x3C;

    /// <summary>The audio each entry of <paramref name="sound"/> plays, with its track; refused when an entry plays no audio.</summary>
    public static ImmutableArray<ScdClip> Clips(byte[] scd, int sound = 0)
    {
        var m = ScdModel.Read(scd);
        if (sound < 0 || sound >= m.Sounds.Count)
            throw new InvalidDataException("The sound file has no such sound.");
        var entries = m.Sounds[sound].Entries;
        if (entries.Count == 0 || entries.Any(e => e.Audio < 0 || e.Audio >= m.Audio.Count || m.Audio[e.Audio] is { Null: true } or { Data.Length: 0 }))
            throw new InvalidDataException("Every sound in the file must play audio.");
        return [.. entries.Select(e => new ScdClip(Fresh(m.Audio[e.Audio]), e.Track >= 0 && e.Track < m.Tracks.Count ? Fresh(m.Tracks[e.Track]) : null))];
    }

    /// <summary>
    /// Removes one alternative of a random set. Chances that follow the convention are spread over
    /// the rest again: each scaled to what played before, rounded down, the remainder on the last.
    /// Tracks and audio stay until <see cref="Compact"/>.
    /// </summary>
    public static void Remove(ScdModel m, int sound, int entry)
    {
        RequireEntry(m, sound, entry);
        var target = m.Sounds[sound];
        RequireRandomSet(target);
        if (target.Entries.Count <= 1)
            throw new InvalidDataException("A random set needs at least one sound.");
        var shares = Shares(target);
        target.Entries.RemoveAt(entry);
        if (shares == null)
            return;
        var playing = 100 - shares.Nothing;
        var scaled = Scale(shares.Shares.RemoveAt(entry), playing);
        scaled[^1] += playing - scaled.Sum();
        SetShares(target, scaled);
    }

    /// <summary>
    /// Adds an alternative to a random set: a copy of the set's first track (or the clip's own when
    /// the set has none) waiting the clip's length, and the clip's audio. Chances that follow the
    /// convention make room for an even share: the new one gets 100/(n+1), the others shrink to fit,
    /// rounded down, and the remainder goes to the new one ([50, 50] becomes [33, 33, 34]).
    /// </summary>
    public static void Add(ScdModel m, int sound, ScdClip clip)
    {
        var target = RequireSound(m, sound);
        RequireRandomSet(target);
        if (target.Entries.Count >= byte.MaxValue)
            throw new InvalidDataException("A random set can hold at most 255 sounds.");
        var shares = Shares(target);
        var first = target.Entries.FindIndex(e => e.Track >= 0 && e.Track < m.Tracks.Count);
        var track = first >= 0 ? Fresh(m.Tracks[target.Entries[first].Track])
            : clip.Track is { } own ? Fresh(own) : throw new InvalidDataException("The new sound has no track to play it.");
        MatchWait(track.Commands, Milliseconds(clip.Audio));
        m.Tracks.Add(track);
        m.Audio.Add(Fresh(clip.Audio));
        target.Entries.Add(new ScdModelEntry(m.Tracks.Count - 1, m.Audio.Count - 1, 100, (short)target.Entries.Count));
        if (shares == null)
            return;
        var added = 100 / (shares.Shares.Length + 1);
        var others = Scale(shares.Shares, 100 - added - shares.Nothing);
        SetShares(target, [.. others, 100 - shares.Nothing - others.Sum()]);
    }

    /// <summary>Swaps one alternative's audio for the clip's, keeping its track, chance and volume, and matches the wait to the new audio.</summary>
    public static void Replace(ScdModel m, int sound, int entry, ScdClip clip)
    {
        RequireEntry(m, sound, entry);
        var target = m.Sounds[sound];
        if (target.Type is not (ScdSound.Normal or ScdSound.GroupRandom))
            throw new InvalidDataException("Only a one-shot or a random set can change its sounds.");
        m.Audio.Add(Fresh(clip.Audio));
        target.Entries[entry] = target.Entries[entry] with { Audio = m.Audio.Count - 1 };
        if (target.Entries[entry].Track >= 0 && target.Entries[entry].Track < m.Tracks.Count)
            MatchWait(m.Tracks[OwnTrack(m, sound, entry)].Commands, Milliseconds(clip.Audio));
    }

    /// <summary>A one-shot (a Normal sound with one entry) becomes a GroupRandom set of that one sound at 100%.</summary>
    public static void ToRandom(ScdModelSound sound)
    {
        if (sound.Type != ScdSound.Normal || sound.Entries.Count != 1)
            throw new InvalidDataException("Only a one-shot can become a random set.");
        sound.Type = ScdSound.GroupRandom;
        sound.Entries[0] = sound.Entries[0] with { Limit = 100, Order = 0 };
    }

    /// <summary>A copy holding only <paramref name="sound"/>, as sound 0, with all its entries.</summary>
    public static ScdModel KeepSound(ScdModel m, int sound)
    {
        RequireSound(m, sound);
        var copy = m.Clone();
        var target = copy.Sounds[sound];
        if (target.Entries.Any(e => e.Track >= 0 && e.Track < copy.Tracks.Count && copy.Tracks[e.Track].Commands.Any(c => c.Type == PlayInnerSound)))
            throw new InvalidDataException("This sound plays other sounds, so it can't be cut out.");
        copy.Sounds = [target];
        copy.Layouts = [copy.Layouts[sound]];
        Compact(copy);
        return copy;
    }

    /// <summary>A copy holding only <paramref name="sound"/>, as sound 0, playing only <paramref name="entry"/> (at 100% in a random set), with its layout.</summary>
    public static ScdModel KeepOnly(ScdModel m, int sound, int entry)
    {
        RequireEntry(m, sound, entry);
        var copy = KeepSound(m, sound);
        var target = copy.Sounds[0];
        var only = target.Entries[entry];
        target.Entries = [target.Random ? only with { Limit = 100, Order = 0 } : only];
        Compact(copy);
        UpdatePlayLength(copy, 0);
        return copy;
    }

    /// <summary>
    /// Drops tracks and audio entries no sound plays (empty slots included) and renumbers every
    /// entry. Audio stays as it is when a track names audio entries by index itself.
    /// </summary>
    public static void Compact(ScdModel m)
    {
        var entries = m.Sounds.SelectMany(s => s.Entries).ToArray();
        var tracks = Renumber(entries.Select(e => e.Track), m.Tracks.Count);
        m.Tracks = [.. tracks.Kept.Select(i => m.Tracks[i])];
        var keepAudio = m.Tracks.Any(t => t.Commands.Any(c => c.Type is ExternalAudio or MidiExternalAudio));
        var audio = keepAudio ? (Map: Enumerable.Range(0, m.Audio.Count).ToArray(), Kept: Enumerable.Range(0, m.Audio.Count).ToArray())
            : Renumber(entries.Select(e => e.Audio), m.Audio.Count);
        m.Audio = [.. audio.Kept.Select(i => m.Audio[i])];
        foreach (var sound in m.Sounds)
            sound.Entries = [.. sound.Entries.Select(e => e with { Track = Moved(tracks.Map, e.Track), Audio = Moved(audio.Map, e.Audio) })];
    }

    private static (int[] Map, int[] Kept) Renumber(IEnumerable<int> used, int count)
    {
        var kept = used.Where(i => i >= 0 && i < count).Distinct().Order().ToArray();
        var map = Enumerable.Repeat(-1, count).ToArray();
        for (var i = 0; i < kept.Length; i++)
            map[kept[i]] = i;
        return (map, kept);
    }

    private static int Moved(int[] map, int index) => index >= 0 && index < map.Length ? map[index] : index;

    /// <summary>Whole-percent shares scaled to add up to at most <paramref name="total"/>, each rounded down.</summary>
    private static int[] Scale(ImmutableArray<int> shares, int total)
    {
        var sum = shares.Sum();
        return [.. shares.Select(s => sum == 0 ? 0 : (int)((long)s * total / sum))];
    }

    /// <summary>Sets the track's wait to the audio's length when the track can take it; a looping or jumping track keeps its own.</summary>
    private static void MatchWait(List<ScdCommand> track, int milliseconds)
    {
        try
        {
            WriteWait(track, milliseconds);
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentOutOfRangeException)
        {
        }
    }

    private static int Milliseconds(ScdModelAudio audio) => (int)Math.Ceiling(audio.Seconds * 1000);

    private static ScdModelAudio Fresh(ScdModelAudio audio)
    {
        var copy = audio.Clone();
        copy.Trailing = [];
        return copy;
    }

    private static ScdModelTrack Fresh(ScdModelTrack track)
    {
        var copy = track.Clone();
        copy.Trailing = [];
        return copy;
    }
}
