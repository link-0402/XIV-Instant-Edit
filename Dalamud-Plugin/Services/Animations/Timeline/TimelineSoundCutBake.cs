using InstantEdit.Services.Audio;

namespace InstantEdit.Services.Animations.Timeline;

internal static partial class TimelineSoundEdits
{
    /// <summary>
    /// <paramref name="baked"/> with the sounds chosen to be cut with the motion (see
    /// <see cref="TimelineDocument.SoundCuts"/>) cut: each such event plays a copy of the file it plays
    /// after <see cref="Bake"/>, without the spans the cuts take out of its audio
    /// (<see cref="TimelineSoundCuts.Spans"/>), at sound/instant_edit/name_cut_hash.scd. The copies go
    /// with the animation. <paramref name="sound"/> gives a file the bake didn't write, by game path;
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
            updated[i] = ev with { Path = cutPath };
        }
        return baked with
        {
            Document = doc with { Events = updated.ToImmutable(), SoundCuts = doc.SoundCuts.Clear() },
            Copies = [.. baked.Copies, .. added.Values],
        };
    }
}
