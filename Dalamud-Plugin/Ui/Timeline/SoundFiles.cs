using System.Collections.Immutable;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Services.Audio;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// The sound files the timeline's sound events play, read on a task the first time one is needed, and
/// each sound as the document's changes leave it. Saves and previews write those changes, pins and
/// the sounds cut with the motion into files from here (<see cref="TimelineSoundEdits"/>).
/// </summary>
internal sealed class SoundFiles(TimelineSession session)
{
    // Sounds read for the inspector, by game path, as the collection in soundsCollection resolves them.
    private readonly Dictionary<string, SoundRead> soundReads = new(StringComparer.OrdinalIgnoreCase);
    private Guid? soundsCollection;
    // Each sound as its edit leaves it, made again when the edit or the file changes.
    private readonly Dictionary<TimelineSoundKey, SoundShown> soundsShown = new();
    private readonly Dictionary<string, (byte[] Bytes, ScdModel Model)> originalModels = new(StringComparer.OrdinalIgnoreCase);
    // The last document PreviewRest was worked out for: the stale check and the sync ask for the same one.
    private (TimelineDocument For, (TimelineDocument Document, ImmutableDictionary<string, byte[]> Sounds) Result)? previewRestCache;

    public sealed class SoundRead
    {
        public Task<(AnimationResource Resource, byte[] Bytes)>? Task;
        public byte[]? Bytes;
        public AnimationResource? Resource;
        public bool Imported;
        public string? Error;
    }

    /// <summary>
    /// One sound of a file as its edit leaves it: the original file and the edit that change nothing
    /// (<see cref="Initial"/>), the written file, its alternatives' ids in order and its model. When
    /// the edit can't be written (<see cref="Error"/>), the file as it is. Model is null when even the
    /// original can't be read.
    /// </summary>
    public sealed record SoundShown(TimelineSoundEdit? Edit, byte[] Original, TimelineSoundEdit? Initial, byte[] Bytes,
        ImmutableArray<string> Ids, ScdModel? Model, string? Error)
    {
        /// <summary>Each alternative's length in seconds, in order; empty when the file can't be read.</summary>
        public ImmutableArray<double> Seconds { get; init; } = [];
        /// <summary>Some alternative's track ends before its audio.</summary>
        public bool EndsEarly { get; init; }
        /// <summary>The sound keeps playing (<see cref="TimelineWindowViews.FileLoops"/>).</summary>
        public bool Loops { get; init; }
    }

    /// <summary> Forgets the sounds read for another clip. </summary>
    public void Forget()
    {
        soundReads.Clear();
        soundsShown.Clear();
        originalModels.Clear();
        soundsCollection = null;
        previewRestCache = null;
    }

    /// <summary> Whether a read of the sound at <paramref name="path"/> started. </summary>
    public bool Started(string path) => soundReads.ContainsKey(path);

    /// <summary> Where the collection gets the sound at <paramref name="path"/>, once read from it; null for an import or before. </summary>
    public AnimationResource? Resource(string path) => soundReads.TryGetValue(path, out var read) ? read.Resource : null;

    /// <summary> <paramref name="shown"/> with what the canvas draws from it: lengths, an alternative that ends early, whether it loops. </summary>
    private static SoundShown Measured(SoundShown shown, int sound)
    {
        if (shown.Model is not { } model || sound < 0 || sound >= model.Sounds.Count)
            return shown;
        var entries = model.Sounds[sound].Entries;
        return shown with
        {
            Seconds = [.. entries.Select(e => e.Audio >= 0 && e.Audio < model.Audio.Count ? model.Audio[e.Audio].Seconds : 0)],
            EndsEarly = entries.Any(e => ScdEdits.Wait(model, e.Track) is > 0 and var wait && wait < ScdEdits.AudioMilliseconds(model, e.Audio)),
            Loops = TimelineWindowViews.FileLoops(model, sound),
        };
    }

    /// <summary> The sound at <paramref name="gamePath"/> for the inspector: an import's own bytes, else read from the collection on a task. </summary>
    public SoundRead SoundFor(string gamePath, AnimationTimelineSource current)
    {
        if (soundsCollection != current.Capture.CollectionId)
        {
            soundReads.Clear();
            soundsShown.Clear();
            originalModels.Clear();
            soundsCollection = current.Capture.CollectionId;
        }
        if (!soundReads.TryGetValue(gamePath, out var read))
        {
            read = new SoundRead();
            soundReads[gamePath] = read;
            if (session.ImportBytes(gamePath) is { } bytes)
                (read.Bytes, read.Imported) = (bytes, true);
            else if (session.Animations is { } service)
            {
                var collection = current.Capture.CollectionId;
                read.Task = Task.Run(() => service.ReadSoundAsync(collection, gamePath, CancellationToken.None));
            }
            else
                read.Error = "Animation editing is unavailable.";
        }
        if (read.Task is { IsCompleted: true } task)
        {
            read.Task = null;
            if (task.IsCompletedSuccessfully)
                (read.Resource, read.Bytes) = task.Result;
            else
                read.Error = TimelineSession.ErrorText(task.Exception);
        }
        return read;
    }

    /// <summary> The bytes of a sound the inspector read, or an import's; null when it wasn't read. </summary>
    public byte[]? SoundBytes(string gamePath) =>
        soundReads.TryGetValue(gamePath, out var read) && read.Bytes != null ? read.Bytes : session.ImportBytes(gamePath);

    /// <summary> <paramref name="key"/>'s sound as the document's edit leaves it; null while its file is read. </summary>
    public SoundShown? ShownSound(TimelineDocument doc, TimelineSoundKey key, AnimationTimelineSource loaded)
    {
        if (SoundFor(key.Path, loaded).Bytes is not { } original)
            return null;
        var edit = doc.SoundEdits.GetValueOrDefault(key);
        if (soundsShown.TryGetValue(key, out var shown) && ReferenceEquals(shown.Edit, edit) && ReferenceEquals(shown.Original, original))
            return shown;
        ScdModel? model = null;
        TimelineSoundEdit? initial = null;
        try
        {
            model = OriginalModel(key.Path, original);
            if (key.Sound < 0 || key.Sound >= model.Sounds.Count)
                throw new InvalidDataException("The file has no sound at this sound entry.");
            initial = TimelineSoundEdits.Initial(model, key.Sound);
            // A control held down changes the edit every frame: read the file once, and keep the model the edit wrote.
            var (bytes, ids, written) = TimelineSoundEdits.ApplyModel(original, key.Sound, edit ?? initial, session.ImportBytes);
            shown = new SoundShown(edit, original, initial, bytes, ids, written, null);
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or InvalidOperationException)
        {
            shown = Unwritable(edit, original, initial, model, key, e.Message);
        }
        shown = Measured(shown, key.Sound);
        soundsShown[key] = shown;
        return shown;
    }

    /// <summary> The model of a sound file as read, kept while those bytes are the file's (it is never edited). </summary>
    private ScdModel OriginalModel(string path, byte[] original)
    {
        if (originalModels.TryGetValue(path, out var kept) && ReferenceEquals(kept.Bytes, original))
            return kept.Model;
        var model = ScdModel.Read(original);
        originalModels[path] = (original, model);
        return model;
    }

    /// <summary> The inspector's written file for an edit of a file, when it already wrote it; else writes it. For preview. </summary>
    private (byte[] Bytes, ImmutableArray<string> Ids) AppliedSound(byte[] original, int sound, TimelineSoundEdit edit)
    {
        foreach (var (key, shown) in soundsShown)
            if (key.Sound == sound && shown.Error == null && ReferenceEquals(shown.Original, original) && ReferenceEquals(shown.Edit, edit))
                return (shown.Bytes, shown.Ids);
        return TimelineSoundEdits.Apply(original, sound, edit, session.ImportBytes);
    }

    /// <summary> <paramref name="key"/>'s sound as edited once its file is read; null before, without starting a read. </summary>
    public SoundShown? ShownIfRead(TimelineDocument doc, TimelineSoundKey key) =>
        session.Source is { } loaded && soundReads.TryGetValue(key.Path, out var read) && read.Bytes != null ? ShownSound(doc, key, loaded) : null;

    /// <summary>
    /// What to show for an edit that can't be written: when only its chances are wrong (more than 100%),
    /// the file with those chances left out, so every row stays to fix them; else the file as it is.
    /// </summary>
    private SoundShown Unwritable(TimelineSoundEdit? edit, byte[] original, TimelineSoundEdit? initial, ScdModel? model, TimelineSoundKey key, string error)
    {
        if (model == null || initial == null)
            return new SoundShown(edit, original, initial, original, [], null, error);
        if (edit is { Custom: false })
            try
            {
                var (bytes, ids, written) = TimelineSoundEdits.ApplyModel(original, key.Sound,
                    edit with { Alternatives = [.. edit.Alternatives.Select(a => a with { Share = 0 })] }, session.ImportBytes);
                return new SoundShown(edit, original, initial, bytes, ids, written, error);
            }
            catch (Exception e) when (e is InvalidDataException or ArgumentException or InvalidOperationException)
            {
            }
        return new SoundShown(edit, original, initial, original, [.. initial.Alternatives.Select(a => a.Id)], model, error);
    }

    /// <summary> The edit to change for <paramref name="shown"/>: the document's, else the one that changes nothing. </summary>
    public static TimelineSoundEdit? EditOf(SoundShown shown) => shown.Edit ?? shown.Initial;

    /// <summary>
    /// Why the sound changes can't be saved or previewed yet: a file with changes or a pin still being
    /// read, one that couldn't be, or changes that can't be written.
    /// </summary>
    public string? SoundMissing(TimelineDocument document)
    {
        if (session.Source is not { } loaded || document.Events.IsDefault)
            return null;
        foreach (var ev in document.Events)
        {
            if (TimelineSoundKey.Of(ev) is not { } key || !document.SoundEdits.ContainsKey(key) && ev.Pin == null)
                continue;
            var file = Path.GetFileName(key.Path);
            var read = SoundFor(key.Path, loaded);
            if (read.Bytes == null)
                return read.Error != null ? $"{file} could not be read, so its changes can't be used." : "Reading the sound files.";
            if (ShownSound(document, key, loaded) is not { } shown)
                continue;
            if (shown.Error is { } error)
                return $"{file}: {error}";
            if (TimelineWindowViews.PinGone(ev.Pin, shown.Ids, file) is { } gone)
                return gone;
        }
        return null;
    }

    /// <summary>
    /// <paramref name="document"/> with its sound changes and pins written into files (see
    /// <see cref="TimelineSoundEdits.Bake"/>): the copies to package and the mod files written over in place.
    /// </summary>
    public TimelineSoundBake WithSounds(TimelineDocument document, Func<TimelineSoundKey, TimelineSoundRoute> route, Func<string, byte[]?>? sound = null) =>
        TimelineSoundEdits.Bake(document, sound ?? SoundBytes, session.ImportBytes, route);

    /// <summary>
    /// The document a preview builds once looks and sound changes it shows by redirecting are taken
    /// off, with the sound files that redirect (see <see cref="TimelineSoundEdits.Redirectable"/>).
    /// </summary>
    public (TimelineDocument Document, ImmutableDictionary<string, byte[]> Sounds) PreviewRest(TimelineDocument document)
    {
        if (previewRestCache is { } cached && ReferenceEquals(cached.For, document))
            return cached.Result;
        var rest = TimelineSoundEdits.Redirectable(TimelineEffectLooks.Redirectable(document).Document, SoundBytes, session.ImportBytes, AppliedSound);
        previewRestCache = (document, rest);
        return rest;
    }

    /// <summary>
    /// How long sound <paramref name="ev"/> plays: its longest alternative. Null while its file is unread,
    /// or when it can't be read; with <paramref name="read"/> a read starts.
    /// </summary>
    private double? AudioSeconds(TimelineDocument doc, TimelineEvent ev, bool read)
    {
        if (ev.Magic != "C063" || TimelineSoundKey.Of(ev) is not { } key)
            return null;
        var shown = read ? session.Source is { } loaded ? ShownSound(doc, key, loaded) : null : ShownIfRead(doc, key);
        return shown is { Seconds.IsEmpty: false } ? shown.Seconds.Max() : null;
    }

    /// <summary>Whether sound <paramref name="ev"/> plays across a cut, once its file is read.</summary>
    public bool CrossesCut(TimelineDocument doc, TimelineEvent ev, bool read) =>
        doc.Cut && AudioSeconds(doc, ev, read) is { } seconds && TimelineSoundCuts.Crosses(doc, ev, seconds);

    /// <summary>
    /// Why saving or previewing waits for the sounds across a cut: one still being read, or ones without
    /// a choice. A sound whose file can't be read plays whole.
    /// </summary>
    public string? SoundCutMissing(TimelineDocument document)
    {
        if (!document.Cut || session.Source is not { } loaded || document.Events.IsDefault)
            return null;
        // Only a sound that starts before the last cut can play across one.
        var cuts = CutEdits.CutsOf(document);
        var last = cuts.Source > cuts.Parts[^1].To ? document.Length
            : cuts.Joins.Length > 0 ? cuts.PartEnd(cuts.Joins.Length - 1, document.Length) : -1;
        var undecided = 0;
        foreach (var ev in document.Events)
        {
            if (ev.Magic != "C063" || ev.Start >= last || document.SoundCuts.ContainsKey(ev.Key) || TimelineSoundKey.Of(ev) is not { } key)
                continue;
            var read = SoundFor(key.Path, loaded);
            if (read.Bytes == null)
            {
                if (read.Error == null)
                    return "Reading the sound files.";
                continue;
            }
            if (CrossesCut(document, ev, true))
                undecided++;
        }
        return undecided == 0 ? null : TimelineWindowViews.SoundCutsFooter(undecided);
    }

    /// <summary>
    /// <paramref name="sounds"/> with the sounds chosen to be cut with the motion cut into copies (see
    /// <see cref="TimelineSoundEdits.BakeCuts"/>), their lengths as <paramref name="before"/> plays them.
    /// </summary>
    public TimelineSoundBake WithSoundCuts(TimelineSoundBake sounds, TimelineDocument before, Func<string, byte[]?> sound)
    {
        if (!sounds.Document.SoundCuts.ContainsValue(true))
            return sounds;
        return TimelineSoundEdits.BakeCuts(sounds, sound, session.ScdTemplate(),
            ev => TimelineEdits.Find(before, ev.Key) is { } original ? AudioSeconds(before, original, true) ?? 0 : 0);
    }
}
