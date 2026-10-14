using InstantEdit.Services.Audio;

namespace InstantEdit.Services.Animations.Timeline;

internal static partial class TimelineSoundEdits
{
    /// <summary>
    /// <paramref name="baked"/> with the sounds chosen to be cut with the motion (see
    /// <see cref="TimelineDocument.SoundCuts"/>) cut: each such event plays a copy of the file it plays
    /// after <see cref="Bake"/>, without the spans the cuts take out of its audio
    /// (<see cref="TimelineSoundCuts.Spans"/>), at sound/instant_edit/name_cut_hash.scd. The copies go
    /// with the animation, and <see cref="TimelineSoundBake.CutFrom"/> says what each was cut from.
    /// <paramref name="sound"/> gives a file the bake didn't write, by game path;
    /// <paramref name="template"/> is the SCD imports are built on; <paramref name="audioSeconds"/> how
    /// long an event's sound plays. The choices are cleared, as they are written now.
    /// </summary>
    public static TimelineSoundBake BakeCuts(TimelineSoundBake baked, Func<string, byte[]?> sound, byte[] template, Func<TimelineEvent, double> audioSeconds)
    {
        var doc = baked.Document;
        if (doc.SoundCuts.IsEmpty)
            return baked;
        var events = doc.Events.IsDefault ? [] : doc.Events;
        var updated = events.ToBuilder();
        var written = baked.Copies.ToDictionary(c => c.GamePath, StringComparer.OrdinalIgnoreCase);
        var added = new Dictionary<string, TimelineImport>(StringComparer.OrdinalIgnoreCase);
        var cutFrom = baked.CutFrom.ToBuilder();
        for (var i = 0; i < events.Length; i++)
        {
            var ev = events[i];
            if (!doc.SoundCuts.TryGetValue(ev.Key, out var cut) || !cut || TimelineSoundKey.Of(ev) is not { } key)
                continue;
            var spans = TimelineSoundCuts.Spans(doc, ev.Start, audioSeconds(ev));
            if (spans.IsEmpty)
                continue;
            var path = ev.Path;
            // The file as the bake left it: a copy it made, a mod file it writes over, else the file itself.
            var bytes = (written.TryGetValue(path, out var copy) && copy.Files.TryGetValue(path, out var copied) ? copied : null)
                ?? (baked.InPlace.TryGetValue(path, out var replaced) ? replaced.Bytes : null)
                ?? sound(path) ?? throw Unreadable(path);
            byte[] cutBytes;
            try { cutBytes = ScdAudioCut.Apply(bytes, key.Sound, spans, template); }
            catch (InvalidDataException e) { throw new InvalidDataException($"{Path.GetFileName(path)}: {e.Message}", e); }
            var cutPath = CopyPath($"{BaseName(path)}_cut", cutBytes);
            if (!written.ContainsKey(cutPath))
                added.TryAdd(cutPath, Copy(cutPath, cutBytes, null, null));
            cutFrom[cutPath] = path;
            updated[i] = ev with { Path = cutPath };
        }
        return baked with
        {
            Document = doc with { Events = updated.ToImmutable(), SoundCuts = doc.SoundCuts.Clear() },
            Copies = [.. baked.Copies, .. added.Values],
            CutFrom = cutFrom.ToImmutable(),
        };
    }

    /// <summary>
    /// <paramref name="doc"/> with every event that plays a copy a save cut (<paramref name="cutFrom"/>, by
    /// game path) playing the file it was cut from again, chosen to be cut with the motion: the next save
    /// cuts that file once, by the cuts as they are then, instead of cutting the cut copy again.
    /// </summary>
    public static TimelineDocument Uncut(TimelineDocument doc, IReadOnlyDictionary<string, string>? cutFrom)
    {
        if (cutFrom is not { Count: > 0 } || doc.Events.IsDefaultOrEmpty)
            return doc;
        var sources = cutFrom.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var events = doc.Events.ToBuilder();
        var choices = doc.SoundCuts;
        for (var i = 0; i < events.Count; i++)
            if (sources.TryGetValue(events[i].Path, out var whole))
            {
                choices = choices.SetItem(events[i].Key, true);
                events[i] = events[i] with { Path = whole };
            }
        return ReferenceEquals(choices, doc.SoundCuts) ? doc : doc with { Events = events.ToImmutable(), SoundCuts = choices };
    }
}
