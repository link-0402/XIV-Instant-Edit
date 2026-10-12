using System.Collections.Immutable;
using InstantEdit.Services.Audio;

namespace InstantEdit.Services.Animations.Timeline;

/// <summary>
/// The sound an edit changes: a sound file's game path, in any case, and the sound index the events
/// play (C063 "SoundIndex"). Every event that plays that sound of that file shares one edit.
/// </summary>
internal readonly record struct TimelineSoundKey(string Path, int Sound)
{
    public const string SoundIndexField = "SoundIndex";

    public bool Equals(TimelineSoundKey other) => Sound == other.Sound && string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(Path ?? ""), Sound);

    /// <summary>The sound <paramref name="ev"/> plays; null when it is not a sound event with a file.</summary>
    public static TimelineSoundKey? Of(TimelineEvent ev) =>
        ev.Magic == "C063" && !string.IsNullOrEmpty(ev.Path)
            ? new TimelineSoundKey(ev.Path, (int)(ev.Values?.GetValueOrDefault(SoundIndexField) ?? 0))
            : null;
}

/// <summary>One alternative of an edited sound.</summary>
/// <param name="Id">"o" + its entry in the file for one of the file's own, "a" + a number for one added in the editor. Pins name it.</param>
/// <param name="Entry">Which entry of the import's sound 0 it plays when <paramref name="Import"/> is set; else its entry in the file.</param>
/// <param name="Import">The game path of an imported sound file whose audio it plays (added, or replacing the file's own); null for the file's own audio.</param>
/// <param name="Share">Its chance in whole percents, for a set whose limits follow the convention.</param>
/// <param name="Limit">Its raw limit, for a set with custom limits.</param>
/// <param name="Order">Its raw order, for a set with custom limits.</param>
/// <param name="Volume">Its track volume, 0 to 1; null keeps the file's.</param>
/// <param name="WaitMs">How long its track waits after starting the audio; null keeps the file's.</param>
internal sealed record TimelineSoundAlternative(string Id, int Entry, string? Import, int Share, short Limit, short Order, float? Volume, int? WaitMs)
{
    public const string OwnPrefix = "o", AddedPrefix = "a";

    /// <summary>The file's own entry this alternative is (or replaces); -1 for an added one.</summary>
    public int Own => Id.StartsWith(OwnPrefix, StringComparison.Ordinal) && int.TryParse(Id.AsSpan(1), out var entry) ? entry : -1;
}

/// <summary>
/// The changes to one sound of one file: the full list of alternatives in order (one the file has
/// that is missing here is removed) and the file settings, each null to keep the file's. Saving,
/// previewing and listening write it into the file (<see cref="TimelineSoundEdits.Apply"/>).
/// </summary>
/// <param name="Custom">The set's limits don't follow the running-total convention: <see cref="TimelineSoundAlternative.Limit"/> and Order are written as they are.</param>
/// <param name="FullWithin">The Point layout's max range: full volume within it.</param>
/// <param name="SilentPast">The Point layout's min range: silent past it.</param>
/// <param name="Bus">The channel the sound plays on.</param>
/// <param name="Random">Make a one-shot a random set (<see cref="ScdEdits.ToRandom"/>) before adding sounds.</param>
internal sealed record TimelineSoundEdit(ImmutableArray<TimelineSoundAlternative> Alternatives, bool Custom, float? Volume = null,
    float? FullWithin = null, float? SilentPast = null, int? Bus = null, bool? Loop = null, bool Random = false)
{
    public bool Equals(TimelineSoundEdit? other) =>
        other is not null && (ReferenceEquals(this, other) ||
            Custom == other.Custom && Volume == other.Volume && FullWithin == other.FullWithin && SilentPast == other.SilentPast &&
            Bus == other.Bus && Loop == other.Loop && Random == other.Random && List(Alternatives).SequenceEqual(List(other.Alternatives)));

    public override int GetHashCode() => HashCode.Combine(List(Alternatives).Length, Custom, Volume, Bus, Loop, Random);

    private static ImmutableArray<TimelineSoundAlternative> List(ImmutableArray<TimelineSoundAlternative> list) => list.IsDefault ? [] : list;
}

/// <summary>Reading and writing sound edits. Dalamud-free.</summary>
internal static partial class TimelineSoundEdits
{
    /// <summary>The edit that changes nothing: the sound's own alternatives with their chances, or raw limits when they don't follow the convention.</summary>
    public static TimelineSoundEdit Initial(ScdModel m, int sound)
    {
        var target = m.Sounds[sound];
        var shares = ScdEdits.Shares(target);
        var single = target.Entries.Count == 1;
        return new TimelineSoundEdit(
            [.. target.Entries.Select((e, i) => new TimelineSoundAlternative($"{TimelineSoundAlternative.OwnPrefix}{i}", i, null,
                shares?.Shares[i] ?? (single ? 100 : 0), e.Limit, e.Order, null, null))],
            Custom: target.Random && shares == null);
    }

    /// <summary>
    /// <paramref name="original"/> with <paramref name="edit"/> written into its sound
    /// <paramref name="sound"/>, and the alternatives' ids in the written entry order. In order: a
    /// one-shot becomes a random set, replaced and added sounds come in (their files from
    /// <paramref name="import"/> by game path), missing ones go, then chances, each alternative's
    /// volume and wait, the file settings and the play length; what nothing plays is dropped. An
    /// edit that changes nothing returns <paramref name="original"/> itself.
    /// </summary>
    public static (byte[] Bytes, ImmutableArray<string> Ids) Apply(byte[] original, int sound, TimelineSoundEdit edit, Func<string, byte[]?> import)
    {
        var (bytes, ids, _) = ApplyModel(original, sound, edit, import);
        return (bytes, ids);
    }

    /// <summary><see cref="Apply"/>, also giving the written file's model, so a caller needn't read the file again.</summary>
    public static (byte[] Bytes, ImmutableArray<string> Ids, ScdModel Model) ApplyModel(byte[] original, int sound, TimelineSoundEdit edit,
        Func<string, byte[]?> import)
    {
        var m = ScdModel.Read(original);
        if (sound < 0 || sound >= m.Sounds.Count)
            throw new InvalidDataException("The sound file has no such sound.");
        var initial = Initial(m, sound);
        if (edit.Equals(initial))
            return (original, [.. initial.Alternatives.Select(a => a.Id)], m);
        var target = m.Sounds[sound];
        var wanted = edit.Alternatives.IsDefault ? [] : edit.Alternatives;
        if (wanted.IsEmpty)
            throw new InvalidDataException("A sound needs at least one alternative.");
        if (edit.Random && target.Type == ScdSound.Normal)
            ScdEdits.ToRandom(target);
        var clips = new Dictionary<string, ImmutableArray<ScdClip>>(StringComparer.OrdinalIgnoreCase);
        ScdClip Clip(string path, int entry)
        {
            if (!clips.TryGetValue(path, out var list))
                clips[path] = list = ScdEdits.Clips(import(path) ?? throw new InvalidDataException($"{Path.GetFileName(path)}, a sound added to this file, is missing."));
            return entry >= 0 && entry < list.Length ? list[entry] : throw new InvalidDataException($"{Path.GetFileName(path)} has no such sound.");
        }
        var ids = Enumerable.Range(0, target.Entries.Count).Select(i => $"{TimelineSoundAlternative.OwnPrefix}{i}").ToList();
        foreach (var alternative in wanted.Where(a => a.Own >= 0 && a.Import != null))
            ScdEdits.Replace(m, sound, IndexOf(ids, alternative.Id), Clip(alternative.Import!, alternative.Entry));
        foreach (var alternative in wanted.Where(a => a.Own < 0))
        {
            ScdEdits.Add(m, sound, Clip(alternative.Import ?? throw new InvalidDataException("An added sound has no file."), alternative.Entry));
            ids.Add(alternative.Id);
        }
        var keep = wanted.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        for (var i = ids.Count - 1; i >= 0; i--)
            if (!keep.Contains(ids[i]))
            {
                ScdEdits.Remove(m, sound, i);
                ids.RemoveAt(i);
            }
        var byId = wanted.ToDictionary(a => a.Id, StringComparer.Ordinal);
        // Only a random set has chances; other types keep their entries as they are.
        if (target.Type == ScdSound.GroupRandom && edit.Custom)
            ScdEdits.SetLimits(target, [.. ids.Select(id => (byId[id].Limit, byId[id].Order))]);
        else if (target.Type == ScdSound.GroupRandom)
            ScdEdits.SetShares(target, [.. ids.Select(id => byId[id].Share)]);
        for (var i = 0; i < ids.Count; i++)
        {
            if (byId[ids[i]].Volume is { } volume)
                ScdEdits.SetTrackVolume(m, sound, i, volume);
            if (byId[ids[i]].WaitMs is { } wait)
                ScdEdits.SetWait(m, sound, i, wait);
        }
        if (edit.Volume is { } fileVolume)
            ScdEdits.SetVolume(target, fileVolume);
        if (edit.FullWithin != null || edit.SilentPast != null)
            ScdEdits.SetRange(m, sound, edit.FullWithin ?? m.MaxRange(sound) ?? 0, edit.SilentPast ?? m.MinRange(sound) ?? 0);
        if (edit.Bus is { } bus)
            ScdEdits.SetBus(target, bus);
        if (edit.Loop is { } loop)
            ScdEdits.SetLoop(m, sound, loop);
        ScdEdits.UpdatePlayLength(m, sound);
        ScdEdits.Compact(m);
        var bytes = m.Write();
        if (ScdFile.Read(bytes).Sounds[sound].Tracks.Length != ids.Count)
            throw new InvalidDataException("The changed sound file doesn't read back.");
        return (bytes, [.. ids], m);
    }

    /// <summary>Whether two documents' sound edits are the same, by content; keys ignore the path's case.</summary>
    public static bool Same(IReadOnlyDictionary<TimelineSoundKey, TimelineSoundEdit> a, IReadOnlyDictionary<TimelineSoundKey, TimelineSoundEdit> b)
    {
        if (ReferenceEquals(a, b))
            return true;
        if (a.Count != b.Count)
            return false;
        foreach (var (key, edit) in a)
            if (!b.TryGetValue(key, out var other) || !edit.Equals(other))
                return false;
        return true;
    }

    private static int IndexOf(List<string> ids, string id)
    {
        var index = ids.IndexOf(id);
        return index >= 0 ? index : throw new InvalidDataException("The sound file doesn't have that sound any more.");
    }
}
