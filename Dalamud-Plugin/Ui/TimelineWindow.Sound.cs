using System.Collections.Immutable;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Services.Audio;
using InstantEdit.Ui.Timeline;

namespace InstantEdit.Ui;

/// <summary>
/// The sound inspector: a sound event's options in plain words, and under them the Sound file
/// section with the file's alternatives. A sound file is read on a task the first time its event is
/// shown. Changes to the file stay on the document (<see cref="TimelineDocument.SoundEdits"/>, pins
/// on the events) until a save, preview or listen writes them into a file (<see cref="TimelineSoundEdits"/>).
/// </summary>
public sealed partial class TimelineWindow
{
    private const string LoopField = "Loop", PositionField = "PositionFlags", BindField = "BindId";
    private const long FadesBit = 1, StopsBit = 2, BindBit = 4;

    // Sounds read for the inspector, by game path, as the collection in soundsCollection resolves them.
    private readonly Dictionary<string, SoundRead> soundReads = new(StringComparer.OrdinalIgnoreCase);
    private Guid? soundsCollection;
    // Each sound as its edit leaves it, made again when the edit or the file changes.
    private readonly Dictionary<TimelineSoundKey, SoundShown> soundsShown = new();

    private sealed class SoundRead
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
    private sealed record SoundShown(TimelineSoundEdit? Edit, byte[] Original, TimelineSoundEdit? Initial, byte[] Bytes,
        ImmutableArray<string> Ids, ScdModel? Model, string? Error);

    /// <summary> Forgets the sounds read for another clip. </summary>
    private void ForgetSounds()
    {
        soundReads.Clear();
        soundsShown.Clear();
        soundsCollection = null;
    }

    /// <summary> The sound at <paramref name="gamePath"/> for the inspector: an import's own bytes, else read from the collection on a task. </summary>
    private SoundRead SoundFor(string gamePath, AnimationTimelineSource current)
    {
        if (soundsCollection != current.Capture.CollectionId)
        {
            soundReads.Clear();
            soundsShown.Clear();
            soundsCollection = current.Capture.CollectionId;
        }
        if (!soundReads.TryGetValue(gamePath, out var read))
        {
            read = new SoundRead();
            soundReads[gamePath] = read;
            if (ImportBytes(gamePath) is { } bytes)
                (read.Bytes, read.Imported) = (bytes, true);
            else if (animations is { } service)
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
                read.Error = ErrorText(task.Exception);
        }
        return read;
    }

    /// <summary> The bytes of a sound the inspector read, or an import's; null when it wasn't read. </summary>
    private byte[]? SoundBytes(string gamePath) =>
        soundReads.TryGetValue(gamePath, out var read) && read.Bytes != null ? read.Bytes : ImportBytes(gamePath);

    /// <summary> An imported file's own bytes by its game path, such as a sound added to a set. </summary>
    private byte[]? ImportBytes(string gamePath) =>
        imports.TryGetValue(gamePath, out var import) && import.Files.TryGetValue(gamePath, out var bytes) ? bytes : null;

    /// <summary> <paramref name="key"/>'s sound as the document's edit leaves it; null while its file is read. </summary>
    private SoundShown? ShownSound(TimelineDocument doc, TimelineSoundKey key, AnimationTimelineSource loaded)
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
            model = ScdModel.Read(original);
            if (key.Sound < 0 || key.Sound >= model.Sounds.Count)
                throw new InvalidDataException("The file has no sound at this sound entry.");
            initial = TimelineSoundEdits.Initial(model, key.Sound);
            var (bytes, ids) = TimelineSoundEdits.Apply(original, key.Sound, edit ?? initial, ImportBytes);
            shown = new SoundShown(edit, original, initial, bytes, ids, ReferenceEquals(bytes, original) ? model : ScdModel.Read(bytes), null);
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or InvalidOperationException)
        {
            var ok = model != null && key.Sound >= 0 && key.Sound < model.Sounds.Count;
            shown = new SoundShown(edit, original, initial, original,
                ok ? [.. initial!.Alternatives.Select(a => a.Id)] : [], ok ? model : null, e.Message);
        }
        soundsShown[key] = shown;
        return shown;
    }

    /// <summary> The edit to change for <paramref name="shown"/>: the document's, else the one that changes nothing. </summary>
    private static TimelineSoundEdit? EditOf(SoundShown shown) => shown.Edit ?? shown.Initial;

    /// <summary>
    /// A sound event's options, as rows of the properties table: how it plays, which alternative,
    /// whether it fades with distance, stops with the animation and where it comes from, and the
    /// sound entry for a file with several.
    /// </summary>
    private void DrawSoundEvent(TimelineEditor current, TimelineEvent ev, bool editable)
    {
        var values = ev.Values ?? ImmutableDictionary<string, double>.Empty;
        var key = TimelineSoundKey.Of(ev);
        var shown = key is { } k && source is { } loaded ? ShownSound(current.Document, k, loaded) : null;
        var sound = key?.Sound ?? 0;

        Fact("Plays");
        var loop = values.GetValueOrDefault(LoopField, 1);
        using (ImRaii.Disabled(!editable))
        {
            ImGui.SetNextItemWidth(Theme.Scaled(170));
            using var combo = ImRaii.Combo("##sound-plays", TimelineWindowViews.PlaysText(loop));
            if (combo.Success)
                foreach (var option in new[] { 1.0, -1.0 })
                    if (ImGui.Selectable(TimelineWindowViews.PlaysText(option), option == loop) && option != loop)
                        current.Replace(With(ev, LoopField, option));
        }
        Tip("Once plays the sound once. Keep looping keeps it going while the animation loops.");
        if (loop == -1 && shown is { Model: not null } && !TimelineWindowViews.FileLoops(shown.Bytes, sound))
        {
            Widgets.HintWrapped("The file has no loop points, so it plays once.");
            using (ImRaii.Disabled(!editable || EditOf(shown) == null))
            {
                if (ImGui.SmallButton("Add loop points") && EditOf(shown) is { } edit)
                    current.SetSound(key!.Value, edit with { Loop = true });
            }
            Tip("Marks the sound to loop and loops each of its audio files from start to end.");
        }

        if (shown is { Model: { } model, Ids.Length: > 1 } && sound < model.Sounds.Count && model.Sounds[sound].Type == ScdSound.GroupRandom)
        {
            Fact("Which sound");
            using (ImRaii.Disabled(!editable))
            {
                ImGui.SetNextItemWidth(Theme.Scaled(170));
                using var combo = ImRaii.Combo("##sound-which", TimelineWindowViews.WhichText(ev.Pin, shown.Ids));
                if (combo.Success)
                {
                    if (ImGui.Selectable(TimelineWindowViews.WhichText(null, shown.Ids), ev.Pin == null) && ev.Pin != null)
                        current.Replace(ev with { Pin = null });
                    for (var i = 0; i < shown.Ids.Length; i++)
                        if (ImGui.Selectable(TimelineWindowViews.WhichText(shown.Ids[i], shown.Ids), ev.Pin == shown.Ids[i]) && ev.Pin != shown.Ids[i])
                            current.Replace(ev with { Pin = shown.Ids[i] });
                }
            }
            Tip("Random plays one of the file's sounds by its chance. Only plays the same one every time, from a copy of the file.");
        }

        var flags = (long)values.GetValueOrDefault(PositionField);
        Fact("Distance");
        Flag(current, ev, editable, "Fades with distance", flags, FadesBit, "Off, it plays at the same volume everywhere.");
        Widgets.HintWrapped(TimelineWindowViews.RangeHint(shown?.Model?.MaxRange(sound), shown?.Model?.MinRange(sound)));

        Fact("Ends");
        Flag(current, ev, editable, "Stops with the animation", flags, StopsBit, "Off, it plays to its end.");
        Widgets.HintWrapped("Cut off when the emote ends.");

        Fact("Comes from");
        Flag(current, ev, editable, "Bind point", flags, BindBit, "A point on the character, such as a hand, by its number.");
        if ((flags & BindBit) != 0)
        {
            ImGui.SameLine(0, Theme.Gap);
            var bind = (int)values.GetValueOrDefault(BindField);
            using (ImRaii.Disabled(!editable))
            {
                ImGui.SetNextItemWidth(Theme.Scaled(70));
                var changed = ImGui.InputInt("##sound-bind", ref bind, 0, 0);
                TrackField(current, changed, changed ? With(ev, BindField, Math.Clamp(bind, 0, byte.MaxValue)) : null);
            }
        }
        else
            Widgets.HintWrapped("The character's position.");

        if (shown?.Model is { Sounds.Count: > 1 } several)
        {
            Fact("Sound entry");
            var index = (int)values.GetValueOrDefault(TimelineSoundKey.SoundIndexField);
            using (ImRaii.Disabled(!editable))
            {
                ImGui.SetNextItemWidth(Theme.Scaled(70));
                var changed = ImGui.InputInt("##sound-index", ref index, 0, 0);
                TrackField(current, changed, changed ? With(ev, TimelineSoundKey.SoundIndexField, Math.Clamp(index, 0, several.Sounds.Count - 1)) : null);
            }
            Tip($"The file has {several.Sounds.Count} sounds. This event plays the one with this number, from 0.");
        }
    }

    /// <summary> One bit of the position flags as a checkbox. </summary>
    private static void Flag(TimelineEditor current, TimelineEvent ev, bool editable, string label, long flags, long bit, string tooltip)
    {
        var set = (flags & bit) != 0;
        using (ImRaii.Disabled(!editable))
        {
            var changed = ImGui.Checkbox(label, ref set);
            TrackField(current, changed, changed ? With(ev, PositionField, set ? flags | bit : flags & ~bit) : null);
        }
        Tip(tooltip);
    }

    private static TimelineEvent With(TimelineEvent ev, string field, double value) =>
        ev with { Values = (ev.Values ?? ImmutableDictionary<string, double>.Empty).SetItem(field, value) };
}
