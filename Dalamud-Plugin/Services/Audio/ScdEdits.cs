using System.Buffers.Binary;
using System.Collections.Immutable;

namespace InstantEdit.Services.Audio;

/// <summary>Each alternative's chance in whole percents, read as running-total limits, and the share that plays nothing.</summary>
internal sealed record ScdShares(ImmutableArray<int> Shares, int Nothing);

/// <summary>
/// Edits on one sound of an <see cref="ScdModel"/>, in place. A random set's limits are read as
/// running totals with order = index: 33/66/100 is three equal chances (76 of 126 vanilla sets
/// count up this way; the reading is not yet confirmed in game). A track several entries share is
/// split before one entry's volume or wait changes, so no other alternative moves. Pitch,
/// modulation, routing, attributes other than Loop, and markers are never touched. Dalamud-free.
/// </summary>
internal static partial class ScdEdits
{
    public const float MaxFileVolume = 2f;
    public const float MaxTrackVolume = 1f;

    /// <summary>A GroupRandom set's chances, or null when its limits don't count up from order 0 to at most 100.</summary>
    public static ScdShares? Shares(ScdModelSound sound)
    {
        if (sound.Type != ScdSound.GroupRandom || sound.Entries.Count == 0)
            return null;
        var shares = ImmutableArray.CreateBuilder<int>(sound.Entries.Count);
        var previous = 0;
        for (var i = 0; i < sound.Entries.Count; i++)
        {
            var entry = sound.Entries[i];
            if (entry.Order != i || entry.Limit < previous || entry.Limit > 100)
                return null;
            shares.Add(entry.Limit - previous);
            previous = entry.Limit;
        }
        return new ScdShares(shares.MoveToImmutable(), 100 - previous);
    }

    /// <summary>Writes whole-percent chances as running-total limits with order = index; what is left of 100 plays nothing.</summary>
    public static void SetShares(ScdModelSound sound, IReadOnlyList<int> shares)
    {
        RequireRandomSet(sound);
        if (shares.Count != sound.Entries.Count)
            throw new ArgumentException("Every sound needs a chance.", nameof(shares));
        if (shares.Any(s => s < 0) || shares.Sum() > 100)
            throw new ArgumentOutOfRangeException(nameof(shares), "The chances must be 0% or more and add up to at most 100%.");
        var total = 0;
        for (var i = 0; i < shares.Count; i++)
        {
            total += shares[i];
            sound.Entries[i] = sound.Entries[i] with { Limit = (short)total, Order = (short)i };
        }
    }

    /// <summary>Writes raw limit and order pairs, for sets that follow their own rule.</summary>
    public static void SetLimits(ScdModelSound sound, IReadOnlyList<(short Limit, short Order)> raw)
    {
        RequireRandomSet(sound);
        if (raw.Count != sound.Entries.Count)
            throw new ArgumentException("Every sound needs a limit.", nameof(raw));
        for (var i = 0; i < raw.Count; i++)
            sound.Entries[i] = sound.Entries[i] with { Limit = raw[i].Limit, Order = raw[i].Order };
    }

    /// <summary>The track's first Volume command's value, or 1 when it has none.</summary>
    public static float TrackVolume(ScdModel m, int track)
        => track >= 0 && track < m.Tracks.Count && m.Tracks[track].Commands.Find(c => c.Type == ScdTrack.Volume) is { } volume
            ? BinaryPrimitives.ReadSingleLittleEndian(volume.Payload) : 1f;

    /// <summary>Sets one entry's track volume (0 to 1), adding a Volume command just before KeyOn when the track has none.</summary>
    public static void SetTrackVolume(ScdModel m, int sound, int entry, float volume)
    {
        if (!(volume >= 0 && volume <= MaxTrackVolume))
            throw new ArgumentOutOfRangeException(nameof(volume), "A sound's volume must be between 0% and 100%.");
        var commands = m.Tracks[OwnTrack(m, sound, entry)].Commands;
        var payload = new byte[8];
        BinaryPrimitives.WriteSingleLittleEndian(payload, volume);
        var at = commands.FindIndex(c => c.Type == ScdTrack.Volume);
        if (at >= 0)
        {
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), BinaryPrimitives.ReadInt32LittleEndian(commands[at].Payload.AsSpan(4)));
            commands[at] = commands[at] with { Payload = payload };
            return;
        }
        RequireNoJumps(commands);
        var keyOn = commands.FindIndex(c => c.Type == ScdTrack.KeyOn);
        commands.Insert(keyOn >= 0 ? keyOn : commands.Count - 1, new ScdCommand(ScdTrack.Volume, payload));
    }

    /// <summary>How long the track waits after KeyOn, in milliseconds: the sum of its Interval commands after it.</summary>
    public static int Wait(ScdModel m, int track)
    {
        if (track < 0 || track >= m.Tracks.Count)
            return 0;
        var commands = m.Tracks[track].Commands;
        var keyOn = commands.FindIndex(c => c.Type == ScdTrack.KeyOn);
        return keyOn < 0 ? 0 : commands.Skip(keyOn).Where(c => c.Type == ScdTrack.Interval).Sum(c => BinaryPrimitives.ReadInt32LittleEndian(c.Payload));
    }

    /// <summary>Sets how long one entry's track waits after KeyOn, by changing its last Interval (or adding one after KeyOn).</summary>
    public static void SetWait(ScdModel m, int sound, int entry, int milliseconds)
    {
        CheckWait(m.Tracks[ExistingTrack(m, sound, entry)].Commands, milliseconds);
        WriteWait(m.Tracks[OwnTrack(m, sound, entry)].Commands, milliseconds);
    }

    /// <summary>Refuses a wait the track can't take: negative, a track without KeyOn, one that loops or jumps, or less than its earlier pauses.</summary>
    private static void CheckWait(List<ScdCommand> track, int milliseconds)
    {
        if (milliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(milliseconds), "A wait can't be negative.");
        var keyOn = track.FindIndex(c => c.Type == ScdTrack.KeyOn);
        if (keyOn < 0)
            throw new InvalidDataException("This sound's track never starts its audio, so its wait can't be set.");
        if (track.Any(c => c.Type is ScdTrack.LoopStart or ScdTrack.LoopEnd || Jumps(c)))
            throw new InvalidDataException("This sound's track loops, so its wait can't be set.");
        if (milliseconds < Intervals(track, keyOn).SkipLast(1).Sum(i => BinaryPrimitives.ReadInt32LittleEndian(track[i].Payload)))
            throw new ArgumentOutOfRangeException(nameof(milliseconds), "This track already waits longer than that before its last pause.");
    }

    /// <summary>Sets the last Interval after KeyOn so the waits after KeyOn add up to <paramref name="milliseconds"/>, adding one after KeyOn when there is none.</summary>
    private static void WriteWait(List<ScdCommand> track, int milliseconds)
    {
        CheckWait(track, milliseconds);
        var keyOn = track.FindIndex(c => c.Type == ScdTrack.KeyOn);
        var intervals = Intervals(track, keyOn);
        var payload = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(payload, milliseconds - intervals.SkipLast(1).Sum(i => BinaryPrimitives.ReadInt32LittleEndian(track[i].Payload)));
        if (intervals.Length == 0)
            track.Insert(keyOn + 1, new ScdCommand(ScdTrack.Interval, payload));
        else
            track[intervals[^1]] = track[intervals[^1]] with { Payload = payload };
    }

    private static int[] Intervals(List<ScdCommand> track, int keyOn)
        => [.. Enumerable.Range(keyOn, track.Count - keyOn).Where(i => track[i].Type == ScdTrack.Interval)];

    /// <summary>Jump and ZeroOneJump move by byte offsets inside the track, so nothing may be added before them.</summary>
    private static bool Jumps(ScdCommand command) => command.Type is ScdTrack.Jump or ZeroOneJump;

    private const int ZeroOneJump = 0x3E;

    /// <summary>An audio entry's length in whole milliseconds, rounded up; 0 for no audio.</summary>
    public static int AudioMilliseconds(ScdModel m, int audio)
        => audio >= 0 && audio < m.Audio.Count ? (int)Math.Ceiling(m.Audio[audio].Seconds * 1000) : 0;

    /// <summary>The whole sound's volume, 0 to 2 (above 1 may clip).</summary>
    public static void SetVolume(ScdModelSound sound, float volume)
    {
        if (!(volume >= 0 && volume <= MaxFileVolume))
            throw new ArgumentOutOfRangeException(nameof(volume), "The volume must be between 0% and 200%.");
        sound.Volume = volume;
    }

    /// <summary>A Point layout's hearing range: full volume within <paramref name="fullWithin"/> (max range), silent past <paramref name="silentPast"/> (min range).</summary>
    public static void SetRange(ScdModel m, int sound, float fullWithin, float silentPast)
    {
        if (!(fullWithin > 0 && fullWithin <= silentPast && float.IsFinite(silentPast)))
            throw new ArgumentOutOfRangeException(nameof(fullWithin), "Full volume must reach past 0 and end before the sound goes silent.");
        if (m.MaxRange(sound) == null)
            throw new InvalidDataException("This sound's hearing range can't be changed.");
        var layout = m.Layouts[sound]!;
        BinaryPrimitives.WriteSingleLittleEndian(layout.AsSpan(48), fullWithin);
        BinaryPrimitives.WriteSingleLittleEndian(layout.AsSpan(52), silentPast);
    }

    /// <summary>The channel (bus) the sound plays on.</summary>
    public static void SetBus(ScdModelSound sound, int bus)
    {
        if (bus is < 0 or > byte.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(bus), "The channel must be between 0 and 255.");
        sound.Bus = bus;
    }

    /// <summary>
    /// The Loop attribute. Turning it on also gives every Vorbis or MS-ADPCM audio the sound plays
    /// that has no loop points a loop over all of it, in the codec's units (Vorbis data bytes,
    /// MS-ADPCM samples), as <c>TimelineEffectFiles.WithLoopPoints</c> does.
    /// </summary>
    public static void SetLoop(ScdModel m, int sound, bool on)
    {
        var target = RequireSound(m, sound);
        if (target.Type == ScdSound.Empty)
            throw new InvalidDataException("This sound can't loop.");
        target.Attributes = on ? target.Attributes | ScdSound.LoopAttribute : target.Attributes & ~ScdSound.LoopAttribute;
        if (!on)
            return;
        foreach (var index in target.Entries.Select(e => e.Audio).Where(a => a >= 0 && a < m.Audio.Count).Distinct())
        {
            var audio = m.Audio[index];
            if (audio.Codec is not (ScdCodec.Vorbis or ScdCodec.MsAdpcm) || audio.Data.Length == 0 || audio.LoopEnd > audio.LoopStart)
                continue;
            audio.LoopStart = 0;
            audio.LoopEnd = audio.Codec == ScdCodec.Vorbis ? audio.Data.Length : checked((int)Math.Round(audio.Seconds * audio.SampleRate));
        }
    }

    /// <summary>Sets the extra block's play length to the longest audio the sound plays, when it has one.</summary>
    public static void UpdatePlayLength(ScdModel m, int sound)
    {
        var target = RequireSound(m, sound);
        if (target.ExtraAt >= 0 && target.Entries.Count > 0)
            target.PlayLength = target.Entries.Max(e => AudioMilliseconds(m, e.Audio));
    }

    private static void RequireRandomSet(ScdModelSound sound)
    {
        if (sound.Type != ScdSound.GroupRandom)
            throw new InvalidDataException("Only a random set has chances.");
    }

    private static void RequireNoJumps(List<ScdCommand> commands)
    {
        if (commands.Any(Jumps))
            throw new InvalidDataException("This sound's track jumps, so nothing can be added to it.");
    }

    private static int ExistingTrack(ScdModel m, int sound, int entry)
    {
        var track = RequireEntry(m, sound, entry).Track;
        return track >= 0 && track < m.Tracks.Count ? track : throw new InvalidDataException("This sound plays no track.");
    }

    private static ScdModelSound RequireSound(ScdModel m, int sound)
        => sound >= 0 && sound < m.Sounds.Count ? m.Sounds[sound] : throw new InvalidDataException("The sound file has no such sound.");

    private static ScdModelEntry RequireEntry(ScdModel m, int sound, int entry)
    {
        var entries = RequireSound(m, sound).Entries;
        return entry >= 0 && entry < entries.Count ? entries[entry] : throw new InvalidDataException("This sound has no such alternative.");
    }

    /// <summary>The entry's track, copied first when any other entry of any sound plays it too.</summary>
    private static int OwnTrack(ScdModel m, int sound, int entry)
    {
        var track = ExistingTrack(m, sound, entry);
        var shared = m.Sounds.Select((s, si) => s.Entries.Where((e, ei) => e.Track == track && (si != sound || ei != entry)).Any()).Any(x => x);
        if (!shared)
            return track;
        var copy = m.Tracks[track].Clone();
        copy.Trailing = [];
        m.Tracks.Add(copy);
        m.Sounds[sound].Entries[entry] = m.Sounds[sound].Entries[entry] with { Track = m.Tracks.Count - 1 };
        return m.Tracks.Count - 1;
    }
}
