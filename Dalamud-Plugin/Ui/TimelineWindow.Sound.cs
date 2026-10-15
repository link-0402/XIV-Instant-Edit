using System.Collections.Immutable;
using System.Globalization;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
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
        ImmutableArray<string> Ids, ScdModel? Model, string? Error)
    {
        /// <summary>Each alternative's length in seconds, in order; empty when the file can't be read.</summary>
        public ImmutableArray<double> Seconds { get; init; } = [];
        /// <summary>Some alternative's track ends before its audio.</summary>
        public bool EndsEarly { get; init; }
        /// <summary>The sound keeps playing (<see cref="TimelineWindowViews.FileLoops"/>).</summary>
        public bool Loops { get; init; }
    }

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

    /// <summary> Forgets the sounds read for another clip. </summary>
    private void ForgetSounds()
    {
        soundReads.Clear();
        soundsShown.Clear();
        originalModels.Clear();
        soundsCollection = null;
        pathOrigins.Clear();
        previewRestCache = null;
        // A save's check of the mod, or its question, belongs to the clip it was for.
        soundCheck = null;
        soundCheckDocument = null;
        soundAsk = null;
        soundInPlace = null;
        // So is a listen's error.
        ForgetListen(null);
    }

    /// <summary> Drops the last listen's error and the play still running, which belong to another event or clip. </summary>
    private void ForgetListen(string? eventKey)
    {
        listenEvent = eventKey;
        listenError = null;
        listening = null;
    }

    /// <summary> The sound at <paramref name="gamePath"/> for the inspector: an import's own bytes, else read from the collection on a task. </summary>
    private SoundRead SoundFor(string gamePath, AnimationTimelineSource current)
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
                read.Error = TimelineSession.ErrorText(task.Exception);
        }
        return read;
    }

    /// <summary> The bytes of a sound the inspector read, or an import's; null when it wasn't read. </summary>
    private byte[]? SoundBytes(string gamePath) =>
        soundReads.TryGetValue(gamePath, out var read) && read.Bytes != null ? read.Bytes : session.ImportBytes(gamePath);

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

    private readonly Dictionary<string, (byte[] Bytes, ScdModel Model)> originalModels = new(StringComparer.OrdinalIgnoreCase);

    /// <summary> The inspector's written file for an edit of a file, when it already wrote it; else writes it. For preview. </summary>
    private (byte[] Bytes, ImmutableArray<string> Ids) AppliedSound(byte[] original, int sound, TimelineSoundEdit edit)
    {
        foreach (var (key, shown) in soundsShown)
            if (key.Sound == sound && shown.Error == null && ReferenceEquals(shown.Original, original) && ReferenceEquals(shown.Edit, edit))
                return (shown.Bytes, shown.Ids);
        return TimelineSoundEdits.Apply(original, sound, edit, session.ImportBytes);
    }

    /// <summary> <paramref name="key"/>'s sound as edited once its file is read; null before, without starting a read. </summary>
    private SoundShown? ShownIfRead(TimelineDocument doc, TimelineSoundKey key) =>
        source is { } loaded && soundReads.TryGetValue(key.Path, out var read) && read.Bytes != null ? ShownSound(doc, key, loaded) : null;

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

        TimelineUi.Fact("Plays");
        var loop = values.GetValueOrDefault(LoopField, 1);
        using (ImRaii.Disabled(!editable))
        {
            ImGui.SetNextItemWidth(Theme.Scaled(170));
            using var combo = ImRaii.Combo("##sound-plays", TimelineWindowViews.PlaysText(loop));
            if (combo.Success)
                foreach (var option in new[] { 1.0, -1.0 })
                    if (ImGui.Selectable(TimelineWindowViews.PlaysText(option), option == loop) && option != loop)
                        current.Replace(TimelineUi.With(ev, LoopField, option));
        }
        TimelineUi.Tip("Once plays the sound once. Keep looping keeps it going while the animation loops.");
        DrawSoundCutChoice(current, ev, editable);
        if (loop == -1 && shown is { Model: not null } && !TimelineWindowViews.FileLoops(shown.Bytes, sound))
        {
            Widgets.HintWrapped("The file has no loop points, so it plays once.");
            using (ImRaii.Disabled(!editable || EditOf(shown) == null))
            {
                if (ImGui.SmallButton("Add loop points") && EditOf(shown) is { } edit)
                    current.SetSound(key!.Value, edit with { Loop = true });
            }
            TimelineUi.Tip("Marks the sound to loop and loops each of its audio files from start to end.");
        }

        // A pin is always shown, so one left on a single sound (or a gone one) can be cleared.
        if (shown is { Model: { } model } && sound < model.Sounds.Count &&
            (ev.Pin != null || shown.Ids.Length > 1 && model.Sounds[sound].Type == ScdSound.GroupRandom))
        {
            TimelineUi.Fact("Which sound");
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
            TimelineUi.Tip("Random plays one of the file's sounds by its chance. Only plays the same one every time, from a copy of the file.");
        }

        var flags = (long)values.GetValueOrDefault(PositionField);
        TimelineUi.Fact("Distance");
        Flag(current, ev, editable, "Fades with distance", flags, FadesBit, "Off, it plays at the same volume everywhere.");
        Widgets.HintWrapped(TimelineWindowViews.RangeHint(shown?.Model?.MaxRange(sound), shown?.Model?.MinRange(sound)));

        TimelineUi.Fact("Ends");
        Flag(current, ev, editable, "Stops with the animation", flags, StopsBit, "Off, it plays to its end.");
        Widgets.HintWrapped("Cut off when the emote ends.");

        TimelineUi.Fact("Comes from");
        Flag(current, ev, editable, "Bind point", flags, BindBit, "A point on the character, such as a hand, by its number.");
        if ((flags & BindBit) != 0)
        {
            ImGui.SameLine(0, Theme.Gap);
            var bind = (int)values.GetValueOrDefault(BindField);
            using (ImRaii.Disabled(!editable))
            {
                ImGui.SetNextItemWidth(Theme.Scaled(70));
                var changed = ImGui.InputInt("##sound-bind", ref bind, 0, 0);
                TimelineUi.TrackField(current, changed, changed ? TimelineUi.With(ev, BindField, Math.Clamp(bind, 0, byte.MaxValue)) : null);
            }
        }
        else
            Widgets.HintWrapped("The character's position.");

        if (shown?.Model is { Sounds.Count: > 1 } several)
        {
            TimelineUi.Fact("Sound entry");
            var index = (int)values.GetValueOrDefault(TimelineSoundKey.SoundIndexField);
            using (ImRaii.Disabled(!editable))
            {
                ImGui.SetNextItemWidth(Theme.Scaled(70));
                var changed = ImGui.InputInt("##sound-index", ref index, 0, 0);
                var picked = Math.Clamp(index, 0, several.Sounds.Count - 1);
                // A pin names an alternative of the sound it was set on, never of another.
                TimelineUi.TrackField(current, changed, changed
                    ? TimelineUi.With(picked != sound ? ev with { Pin = null } : ev, TimelineSoundKey.SoundIndexField, picked)
                    : null);
            }
            TimelineUi.Tip($"The file has {several.Sounds.Count} sounds. This event plays the one with this number, from 0.");
        }
    }

    /// <summary> One bit of the position flags as a checkbox. </summary>
    private static void Flag(TimelineEditor current, TimelineEvent ev, bool editable, string label, long flags, long bit, string tooltip)
    {
        var set = (flags & bit) != 0;
        using (ImRaii.Disabled(!editable))
        {
            var changed = ImGui.Checkbox(label, ref set);
            TimelineUi.TrackField(current, changed, changed ? TimelineUi.With(ev, PositionField, set ? flags | bit : flags & ~bit) : null);
        }
        TimelineUi.Tip(tooltip);
    }

    // A Sound file control's changes until no control is active make one undo step.
    private bool soundEditing;
    // The sound playing in game for a listen button, why the last one couldn't, and the event they are for.
    private Task? listening;
    private string? listenError;
    private string? listenEvent;

    /// <summary> Ends the running sound edit once no control is active any more. </summary>
    private void EndSoundEdit(TimelineEditor current)
    {
        if (!soundEditing || ImGui.IsAnyItemActive())
            return;
        soundEditing = false;
        current.EndFieldEdit();
    }

    private void SetSoundEdit(TimelineEditor current, TimelineSoundKey key, TimelineSoundEdit edit)
    {
        if (!soundEditing)
        {
            current.BeginFieldEdit();
            soundEditing = true;
        }
        current.SetSound(key, edit);
    }

    /// <summary>
    /// The Sound file section under a sound event: what the file is, its volume, its alternatives
    /// with their chance, volume and length, listen buttons, adding, replacing and removing sounds, and
    /// More with the hearing range, channel and loop. A file whose sound isn't a one-shot or a random
    /// set, a locked event and a read-only timeline show it without controls.
    /// </summary>
    private void DrawSoundFile(TimelineEditor current, TimelineEvent ev, bool editable)
    {
        if (listenEvent != ev.Key)
            ForgetListen(ev.Key);
        PollListen();
        if (source is not { } loaded || TimelineSoundKey.Of(ev) is not { } key || !key.Path.EndsWith(".scd", StringComparison.OrdinalIgnoreCase) ||
            !AnimationDependencies.SafeGamePath(key.Path))
            return;
        ImGui.Spacing();
        if (!ImGui.CollapsingHeader("Sound file##sound-file", ImGuiTreeNodeFlags.DefaultOpen))
            return;
        var read = SoundFor(key.Path, loaded);
        var shown = ShownSound(current.Document, key, loaded);
        if (shown == null)
        {
            if (read.Error != null)
                Widgets.HintWrapped("The sound file could not be read. " + read.Error);
            else
            {
                Widgets.Spinner();
                ImGui.SameLine(0, Theme.Gap);
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(Theme.Muted, "Reading the sound");
            }
            return;
        }
        if (shown.Model is not { } model || EditOf(shown) is not { } edit)
        {
            Widgets.HintWrapped("The sound file could not be read. " + shown.Error);
            return;
        }
        var target = model.Sounds[key.Sound];
        var random = target.Type == ScdSound.GroupRandom;
        var oneShot = target.Type == ScdSound.Normal && target.Entries.Count == 1;
        var changes = editable && (random || target.Type == ScdSound.Normal);
        var problem = soundPlayer == null ? "Listening isn't available." : soundPlayer.Problem;

        var seconds = target.Entries.Select(e => e.Audio >= 0 && e.Audio < model.Audio.Count ? model.Audio[e.Audio].Seconds : 0).DefaultIfEmpty(0).ToArray();
        var origin = read.Imported ? "imported" : read.Resource?.ModDirectory != null ? "the mod's own" : "vanilla";
        ListenButton("##sound-listen-all", shown.Error == null ? "Listen in game to the whole sound, with your changes." : shown.Error, problem,
            shown.Error == null, () => TimelineListen.Whole(shown.Bytes, key.Sound));
        ImGui.SameLine(0, Theme.Gap);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, TimelineWindowViews.SoundSummary(target.Type, target.Entries.Count, seconds.Min(), seconds.Max(), origin));
        Widgets.HintWrapped(origin == "the mod's own" ? "Saving in place changes the mod's file. A new mod gets a copy." : "Changes go into a copy of the file.");
        if (shown.Error != null)
            Wrapped(Theme.Error, shown.Error);
        if (listenError != null)
            Wrapped(Theme.Error, listenError);

        using (var settings = ImRaii.Table("##sound-file-volume", 2, ImGuiTableFlags.SizingFixedFit))
        {
            if (settings.Success)
            {
                ImGui.TableSetupColumn("##sound-name", ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableSetupColumn("##sound-value", ImGuiTableColumnFlags.WidthStretch);
                TimelineUi.Fact("Volume");
                var volume = (edit.Volume ?? target.Volume) * 100;
                using (ImRaii.Disabled(!editable))
                {
                    ImGui.SetNextItemWidth(-1);
                    if (ImGui.SliderFloat("##sound-volume", ref volume, 0, ScdEdits.MaxFileVolume * 100, "%.0f%%", ImGuiSliderFlags.AlwaysClamp))
                        SetSoundEdit(current, key, edit with { Volume = MathF.Round(volume) / 100 });
                }
                TimelineUi.Tip("The whole sound's volume. Ctrl+click to type.");
                if (volume > 100.5f)
                    Widgets.HintWrapped("Above 100% may clip.");
            }
        }

        DrawAlternatives(current, key, shown, model, edit, changes, random, problem);

        if (random && !edit.Custom)
        {
            var sum = shown.Ids.Sum(id => edit.Alternatives.FirstOrDefault(a => a.Id == id)?.Share ?? 0);
            Wrapped(sum > 100 ? Theme.Error : Theme.Muted, TimelineWindowViews.ChanceFooter(sum));
        }
        else if (random)
            Widgets.HintWrapped("These chances follow the file's own rule. Even chances rewrites them.");
        if (changes)
        {
            using (ImRaii.Disabled(!random && !oneShot || target.Entries.Count >= byte.MaxValue))
            {
                if (ImGui.Button("Add sound"))
                    OpenSoundImport(new TimelineSoundTarget(key, null));
            }
            TimelineUi.Tip(oneShot ? "Turns this into a random set." : "Adds a .wav, .ogg or .scd file as another sound.");
            if (random)
            {
                ImGui.SameLine();
                if (ImGui.Button("Even chances"))
                {
                    var even = TimelineWindowViews.EvenShares(shown.Ids.Length);
                    SetSoundEdit(current, key, edit with
                    {
                        Custom = false,
                        Alternatives = [.. edit.Alternatives.Select(a => a with { Share = shown.Ids.IndexOf(a.Id) is >= 0 and var at ? even[at] : 0 })],
                    });
                }
                TimelineUi.Tip("Gives every sound the same chance.");
            }
            ImGui.SameLine();
            if (ImGui.Button("Match lengths"))
                SetSoundEdit(current, key, edit with
                {
                    Alternatives = [.. edit.Alternatives.Select(a => shown.Ids.IndexOf(a.Id) is >= 0 and var at
                        ? a with { WaitMs = ScdEdits.AudioMilliseconds(model, target.Entries[at].Audio) } : a)],
                });
            TimelineUi.Tip("Lets every sound's track run as long as its audio.");
        }
        DrawSoundMore(current, key, model, target, edit, editable);
    }

    /// <summary> The alternatives table: listen, number, length, chance (or raw limits), volume, an ends-early mark and a row menu. </summary>
    private void DrawAlternatives(TimelineEditor current, TimelineSoundKey key, SoundShown shown, ScdModel model, TimelineSoundEdit edit,
        bool changes, bool random, string? problem)
    {
        var target = model.Sounds[key.Sound];
        var columns = edit.Custom && random ? 8 : 7;
        using var table = ImRaii.Table("##sound-alternatives", columns, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg);
        if (!table.Success)
            return;
        ImGui.TableSetupColumn("##listen", ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("Length", ImGuiTableColumnFlags.WidthFixed);
        if (edit.Custom && random)
        {
            ImGui.TableSetupColumn("Limit", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn("Order", ImGuiTableColumnFlags.WidthFixed);
        }
        else
            ImGui.TableSetupColumn("Chance", ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("Volume", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("##ends", ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("##menu", ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableHeadersRow();
        for (var i = 0; i < shown.Ids.Length && i < target.Entries.Count; i++)
        {
            using var id = ImRaii.PushId(i);
            var entry = target.Entries[i];
            var alternative = edit.Alternatives.FirstOrDefault(a => a.Id == shown.Ids[i]);
            var row = i;
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ListenButton("##listen", "Listen in game to this sound alone.", problem, true, () => TimelineListen.Row(shown.Bytes, key.Sound, row));
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted((i + 1).ToString(CultureInfo.InvariantCulture));
            ImGui.TableNextColumn();
            var audioMs = ScdEdits.AudioMilliseconds(model, entry.Audio);
            ImGui.TextUnformatted(string.Create(CultureInfo.InvariantCulture, $"{audioMs / 1000.0:0.00} s"));
            if (edit.Custom && random)
            {
                ImGui.TableNextColumn();
                var limit = (int)(alternative?.Limit ?? entry.Limit);
                using (ImRaii.Disabled(!changes || alternative == null))
                {
                    ImGui.SetNextItemWidth(Theme.Scaled(55));
                    if (ImGui.InputInt("##limit", ref limit, 0, 0) && alternative != null)
                        SetSoundEdit(current, key, Changed(edit, alternative with { Limit = (short)Math.Clamp(limit, short.MinValue, short.MaxValue) }));
                }
                ImGui.TableNextColumn();
                var order = (int)(alternative?.Order ?? entry.Order);
                using (ImRaii.Disabled(!changes || alternative == null))
                {
                    ImGui.SetNextItemWidth(Theme.Scaled(45));
                    if (ImGui.InputInt("##order", ref order, 0, 0) && alternative != null)
                        SetSoundEdit(current, key, Changed(edit, alternative with { Order = (short)Math.Clamp(order, short.MinValue, short.MaxValue) }));
                }
            }
            else
            {
                ImGui.TableNextColumn();
                if (random)
                {
                    var share = alternative?.Share ?? 0;
                    using (ImRaii.Disabled(!changes || alternative == null))
                    {
                        ImGui.SetNextItemWidth(Theme.Scaled(70));
                        if (ImGui.InputInt("##chance", ref share, 0, 0, default, ImGuiInputTextFlags.CharsDecimal) && alternative != null)
                            SetSoundEdit(current, key, Changed(edit, alternative with { Share = Math.Clamp(share, 0, 100) }));
                    }
                    TimelineUi.Tip("How often this sound plays, in percent.");
                }
                else
                    ImGui.TextColored(Theme.Muted, "Always");
            }
            ImGui.TableNextColumn();
            var volume = (int)MathF.Round(ScdEdits.TrackVolume(model, entry.Track) * 100);
            using (ImRaii.Disabled(!changes || alternative == null || entry.Track < 0))
            {
                ImGui.SetNextItemWidth(-1);
                if (ImGui.SliderInt("##volume", ref volume, 0, 100, "%d%%", ImGuiSliderFlags.AlwaysClamp) && alternative != null)
                    SetSoundEdit(current, key, Changed(edit, alternative with { Volume = volume / 100f }));
            }
            TimelineUi.Tip("This sound's own volume. Ctrl+click to type.");
            ImGui.TableNextColumn();
            var wait = ScdEdits.Wait(model, entry.Track);
            if (wait > 0 && wait < audioMs)
            {
                ImGui.AlignTextToFramePadding();
                Widgets.Icon(FontAwesomeIcon.ExclamationTriangle, Theme.Warning);
                TimelineUi.Tip(TimelineWindowViews.EndsEarlyTip(wait, audioMs));
            }
            ImGui.TableNextColumn();
            if (!changes || alternative == null)
                continue;
            if (Widgets.GhostIconButton("##row-menu", FontAwesomeIcon.EllipsisV, "More for this sound"))
                ImGui.OpenPopup("##sound-row");
            using var popup = ImRaii.Popup("##sound-row");
            if (!popup.Success)
                continue;
            if (ImGui.MenuItem("Replace with audio file"))
                OpenSoundImport(new TimelineSoundTarget(key, alternative.Id));
            if (ImGui.MenuItem("Match length", "", false, entry.Track >= 0))
                SetSoundEdit(current, key, Changed(edit, alternative with { WaitMs = audioMs }));
            // SetSound unpins the events that always played it, in the same undo step.
            if (ImGui.MenuItem("Remove", "", false, random && shown.Ids.Length > 1))
                SetSoundEdit(current, key, edit with { Alternatives = [.. edit.Alternatives.Where(a => a.Id != alternative.Id)] });
        }
    }

    /// <summary> More: the hearing range of a Point layout, the channel and the loop switch, for any sound. </summary>
    private void DrawSoundMore(TimelineEditor current, TimelineSoundKey key, ScdModel model, ScdModelSound target, TimelineSoundEdit edit, bool editable)
    {
        if (!ImGui.CollapsingHeader("More##sound-more"))
            return;
        using var table = ImRaii.Table("##sound-more", 2, ImGuiTableFlags.SizingFixedFit);
        if (!table.Success)
            return;
        ImGui.TableSetupColumn("##more-name", ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("##more-value", ImGuiTableColumnFlags.WidthStretch);
        using var disabled = ImRaii.Disabled(!editable);
        if (model.MaxRange(key.Sound) is { } near && model.MinRange(key.Sound) is { } far)
        {
            TimelineUi.Fact("Full volume within");
            ImGui.SetNextItemWidth(Theme.Scaled(110));
            if (ImGui.InputFloat("##sound-near", ref near, 0, 0, "%.1f") && float.IsFinite(near))
                SetSoundEdit(current, key, edit with { FullWithin = Math.Clamp(near, .1f, Math.Max(.1f, far)), SilentPast = far });
            TimelineUi.Tip("How far from the camera the sound stays at full volume.");
            TimelineUi.Fact("Silent past");
            ImGui.SetNextItemWidth(Theme.Scaled(110));
            if (ImGui.InputFloat("##sound-far", ref far, 0, 0, "%.1f") && float.IsFinite(far))
                SetSoundEdit(current, key, edit with { FullWithin = near, SilentPast = Math.Max(far, near) });
            TimelineUi.Tip("How far from the camera the sound fades out completely.");
        }
        TimelineUi.Fact("Channel");
        var bus = edit.Bus ?? target.Bus;
        ImGui.SetNextItemWidth(Theme.Scaled(170));
        using (var combo = ImRaii.Combo("##sound-channel", TimelineWindowViews.ChannelName(bus)))
        {
            if (combo.Success)
                foreach (var option in TimelineWindowViews.KnownChannels.Contains(bus) ? TimelineWindowViews.KnownChannels : [bus, .. TimelineWindowViews.KnownChannels])
                    if (ImGui.Selectable(TimelineWindowViews.ChannelName(option), option == bus) && option != bus)
                        SetSoundEdit(current, key, edit with { Bus = option });
        }
        TimelineUi.Tip("Which of the game's volume sliders the sound follows.");
        TimelineUi.Fact("Loop");
        var loops = edit.Loop ?? (target.Attributes & ScdSound.LoopAttribute) != 0;
        using (ImRaii.Disabled(target.Type == ScdSound.Empty))
        {
            if (ImGui.Checkbox("##sound-loop", ref loops))
                SetSoundEdit(current, key, edit with { Loop = loops });
        }
        TimelineUi.Tip("Marks the sound to loop. Turning it on also loops each audio file from start to end.");
    }

    /// <summary> <paramref name="edit"/> with <paramref name="alternative"/> in place of the one with its id. </summary>
    private static TimelineSoundEdit Changed(TimelineSoundEdit edit, TimelineSoundAlternative alternative) =>
        edit with { Alternatives = [.. edit.Alternatives.Select(a => a.Id == alternative.Id ? alternative : a)] };

    /// <summary> A listen button: plays <paramref name="bytes"/>' sound 0 in game; disabled with the reason when it can't. </summary>
    private void ListenButton(string id, string tooltip, string? problem, bool allowed, Func<byte[]> bytes)
    {
        var busy = listening is { IsCompleted: false };
        if (Widgets.IconButton(id, FontAwesomeIcon.VolumeUp, problem ?? tooltip, problem == null && allowed && !busy) && soundPlayer is { } player)
        {
            listenError = null;
            try { listening = player.PlayAsync(bytes(), CancellationToken.None); }
            catch (Exception e) when (e is InvalidDataException or InvalidOperationException or ArgumentException)
            {
                listenError = e.Message;
            }
        }
    }

    private void PollListen()
    {
        if (listening is not { IsCompleted: true } task)
            return;
        listening = null;
        if (task.IsFaulted)
            listenError = "The sound could not be played. " + TimelineSession.ErrorText(task.Exception);
    }

    /// <summary> Opens the file dialog for a sound to add to <paramref name="target"/>'s set, or to put in place of one of its sounds. </summary>
    private void OpenSoundImport(TimelineSoundTarget target)
    {
        var job = new ImportJob(TimelineAddKind.Sound, 0, null, string.Empty, source is { } current ? TimelineSession.ClipKey(current) : null, loadGeneration, null, target);
        fileDialog.OpenFileDialog(target.ReplaceId == null ? "Choose a sound to add" : "Choose a sound to put in its place", TimelineWindowViews.SoundFilter,
            (success, files) =>
            {
                if (success && files.Count > 0)
                    StartImport(job with { Name = Path.GetFileName(files[0]) }, files[0], 1f);
            }, 1, lastImportFolder, false);
    }

    /// <summary> An imported file for the Sound file section, placed into its set (see <see cref="TimelineWindowViews.Placed"/>). </summary>
    private void PlaceSound(TimelineEditor current, TimelineSoundTarget target, TimelineImport import, string name)
    {
        if (source is not { } loaded || ShownSound(current.Document, target.Key, loaded) is not { Model: { } model } shown || EditOf(shown) is not { } edit)
        {
            importError = $"{name} was imported, but its sound file isn't read.";
            return;
        }
        int clips;
        try { clips = ScdEdits.Clips(import.Files[import.GamePath]).Length; }
        catch (InvalidDataException e)
        {
            importError = $"{name} can't be used. {e.Message}";
            return;
        }
        var sound = model.Sounds[target.Key.Sound];
        var oneShot = sound.Type == ScdSound.Normal && sound.Entries.Count == 1;
        if (target.ReplaceId == null && !oneShot && sound.Type != ScdSound.GroupRandom)
        {
            importError = $"{name} can't be added: only a random set or a one-shot takes more sounds.";
            return;
        }
        // The sound may have been removed (or undone) while the file was converted.
        if (TimelineWindowViews.ReplaceGone(edit, target.ReplaceId, name) is { } gone)
        {
            importError = gone;
            return;
        }
        var placed = TimelineWindowViews.Placed(edit, target.ReplaceId, import.GamePath, clips, target.ReplaceId == null && oneShot);
        if (ReferenceEquals(TimelineEdits.SetSound(current.Document, target.Key, placed), current.Document))
            importMessage = $"{name} is already in place.";
        else if (current.SetSound(target.Key, placed))
            importMessage = target.ReplaceId == null ? $"{name} was added." : $"{name} took the sound's place.";
        else
            importError = $"{name} was imported but could not be placed. Finish the drag first.";
    }

    private static void Wrapped(System.Numerics.Vector4 colour, string text)
    {
        using var wrap = ImRaii.TextWrapPos(0);
        ImGui.TextColored(colour, text);
    }
}
