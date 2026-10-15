using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Services.Audio;
using InstantEdit.Ui.Timeline;

namespace InstantEdit.Ui;

/// <summary>
/// What can be placed: one picker popup per kind listing the catalog (thousands of rows, so a
/// filter and a list clipper), imports of sound and effect files on a task, and typed game paths.
/// A picker either adds at a frame or chooses a new path or model for an existing event.
/// </summary>
public sealed partial class TimelineWindow
{
    private const string PickerPopup = "##timeline-picker";
    private const int WeaponAsListed = -1;
    private const int WeaponNone = -2;

    private sealed record ImportedItem(TimelineAddKind Kind, TimelineCatalogItem Item);
    // Generation is the document load the import began on, TargetPath the target's path then: a reload
    // renumbers loaded entries' keys, and an undo can give an added one's key to another event.
    // Sound is set for a file the Sound file section adds to a set or puts in place of one of its sounds.
    private sealed record ImportJob(TimelineAddKind Kind, int Frame, string? Target, string Name, string? Clip,
        int Generation, string? TargetPath, TimelineSoundTarget? Sound = null);

    private TimelineAddKind pickerKind;
    private int pickerFrame;
    private string? pickerTarget;
    private bool pickerOpenRequested;
    private bool pickerClose;
    private string pickerFilter = string.Empty;
    private string? pickerError;
    private int pickerWeapon = WeaponAsListed;
    private float importVolume = 100;
    private string gamePathText = string.Empty;

    // The filtered rows, rebuilt when the list, the filter or the imports change.
    private readonly List<TimelineCatalogItem> pickerRows = [];
    private ImmutableArray<TimelineCatalogItem> pickerRowsSource;
    private TimelineAddKind pickerRowsKind;
    private string pickerRowsFilter = string.Empty;
    private int pickerRowsImports = -1;

    private readonly List<ImportedItem> importedItems = [];
    private int importsVersion;
    // Counts document loads (see ImportJob).
    private int loadGeneration;
    private Task<TimelineImport>? importTask;
    private ImportJob? importing;
    private string? importError;
    private string? importMessage;
    private string? lastImportFolder;

    // Sounds draw as long as their file plays; lengths arrive in the background.
    private readonly HashSet<string> pendingLengths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Opens the picker on the next draw at the window's own level, so a menu can ask for it.
    /// <paramref name="target"/> is the event whose path or model it replaces; null adds at <paramref name="frame"/>.
    /// </summary>
    private void RequestPicker(TimelineAddKind kind, int frame, string? target)
    {
        if (kind != pickerKind)
            pickerFilter = string.Empty;
        pickerKind = kind;
        pickerFrame = Math.Max(0, frame);
        pickerTarget = target;
        pickerError = null;
        pickerWeapon = WeaponAsListed;
        pickerOpenRequested = true;
    }

    private void DrawPicker(TimelineEditor current)
    {
        if (pickerOpenRequested)
        {
            ImGui.OpenPopup(PickerPopup);
            pickerOpenRequested = false;
        }
        using var popup = ImRaii.Popup(PickerPopup);
        if (!popup.Success)
            return;
        var width = Theme.Scaled(500);
        using var wrap = ImRaii.TextWrapPos(width);
        var name = TimelineWindowViews.AddKindName(pickerKind).ToLowerInvariant();
        ImGui.TextColored(Theme.Label, pickerTarget != null ? $"Choose another {name}" : $"Add {name} at {TimelineText.Seconds(pickerFrame)}");
        ImGui.Separator();
        DrawCatalogState();
        switch (pickerKind)
        {
            case TimelineAddKind.Sound or TimelineAddKind.Effect:
                DrawImportRow(current);
                break;
            case TimelineAddKind.Prop when pickerTarget == null:
                DrawWeaponChoice();
                break;
        }

        Widgets.SearchBox("##timeline-picker-search", ref pickerFilter, "Search by name, emote or path");
        var rows = PickerRows();
        ImGui.TextColored(Theme.Muted, rows.Count == 1 ? "1 item" : rows.Count.ToString("N0", CultureInfo.InvariantCulture) + " items");
        var picked = false;
        using (var list = ImRaii.Child("##timeline-picker-list", new Vector2(width, Theme.Scaled(300)), true))
        {
            if (list.Success)
                picked = DrawPickerList(current, rows);
        }
        if (pickerError != null)
            ImGui.TextColored(Theme.Warning, pickerError);
        if (picked || pickerClose)
        {
            pickerClose = false;
            ImGui.CloseCurrentPopup();
        }
    }

    /// <summary> Whether the lists are ready, why they failed (with Retry) and the catalog's notes. </summary>
    private void DrawCatalogState()
    {
        if (catalogKey == null)
        {
            Widgets.HintWrapped("This animation is not for a player race, so only imports and typed game paths can be placed.");
            return;
        }
        switch (catalog.State)
        {
            case TimelineCatalogState.Ready:
                break;
            case TimelineCatalogState.Failed:
                ImGui.TextColored(Theme.Warning, catalog.Status.Length > 0 ? catalog.Status : "The lists could not be built.");
                if (ImGui.SmallButton("Retry"))
                    catalog.Rebuild();
                break;
            default:
                Widgets.Spinner();
                ImGui.SameLine(0, Theme.Gap);
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(Theme.Muted, catalog.Status.Length > 0 ? catalog.Status : "Finding what can be placed");
                break;
        }
        foreach (var note in catalog.Data.Notes)
            ImGui.TextColored(Theme.Hint, note);
    }

    /// <summary> Import a file from disk (a sound with its volume), or type a game path the game or a mod has. </summary>
    private void DrawImportRow(TimelineEditor current)
    {
        var sound = pickerKind == TimelineAddKind.Sound;
        var readOnly = ReadOnly;
        using (ImRaii.Disabled(importTask != null || readOnly))
        {
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.FileImport, "Import file", null, null, null, null))
                OpenImportDialog(pickerKind);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(importTask != null ? "Another file is still importing."
                : sound ? "Import a .wav, .ogg or .scd file. It is saved with the mod."
                : "Import an .avfx file. The textures and sounds it needs are found next to it and saved with the mod.");
        if (sound)
        {
            ImGui.SameLine(0, Theme.Scaled(12));
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Muted, "Volume");
            ImGui.SameLine(0, Theme.Gap);
            ImGui.SetNextItemWidth(Theme.Scaled(130));
            ImGui.SliderFloat("##timeline-import-volume", ref importVolume, 0, 100, "%.0f%%");
            importVolume = Math.Clamp(importVolume, 0, 100);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("For .wav and .ogg files. An .scd keeps its own volume.");
        }

        ImGui.SetNextItemWidth(Theme.Scaled(400));
        ImGui.InputTextWithHint("##timeline-game-path", sound ? "Game path, such as sound/foo/bar.scd" : "Game path, such as vfx/foo/bar.avfx",
            ref gamePathText, 256);
        ImGui.SameLine(0, Theme.Gap);
        using (ImRaii.Disabled(readOnly))
        {
            if (ImGui.Button("Use"))
            {
                var path = TimelineWindowViews.GamePath(gamePathText, pickerKind, out var error);
                if (path == null)
                    pickerError = error;
                else if (Place(current, pickerKind, pickerFrame, pickerTarget, path, null, null))
                    pickerClose = true;
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Use a file the game or one of your mods already has at this path.");
    }

    /// <summary> For props: the animation the prop plays, as the emote that uses it does, none, or another one. </summary>
    private void DrawWeaponChoice()
    {
        var timelines = catalog.Data.WeaponTimelines;
        ImGui.SetNextItemWidth(Theme.Scaled(320));
        using var combo = ImRaii.Combo("Prop animation", WeaponLabel(timelines));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("What the prop plays after it appears.");
        if (!combo.Success)
            return;
        if (ImGui.Selectable("As its emote plays it", pickerWeapon == WeaponAsListed))
            pickerWeapon = WeaponAsListed;
        if (ImGui.Selectable("None, the prop stays still", pickerWeapon == WeaponNone))
            pickerWeapon = WeaponNone;
        var clipper = ImGui.ImGuiListClipper();
        try
        {
            clipper.Begin(timelines.Length, -1);
            while (clipper.Step())
                for (var index = clipper.DisplayStart; index < clipper.DisplayEnd; index++)
                {
                    var timeline = timelines[index];
                    using var id = ImRaii.PushId(index);
                    if (ImGui.Selectable(timeline.Key, pickerWeapon == timeline.Id))
                        pickerWeapon = timeline.Id;
                    ImGui.SameLine(0, Theme.Gap);
                    ImGui.TextColored(Theme.Muted, timeline.Id.ToString(CultureInfo.InvariantCulture));
                }
            clipper.End();
        }
        finally
        {
            clipper.Destroy();
        }
    }

    private string WeaponLabel(ImmutableArray<TimelineWeaponTimeline> timelines)
    {
        switch (pickerWeapon)
        {
            case WeaponAsListed: return "As its emote plays it";
            case WeaponNone: return "None, the prop stays still";
        }
        foreach (var timeline in timelines)
            if (timeline.Id == pickerWeapon)
                return timeline.Key;
        return "Animation " + pickerWeapon.ToString(CultureInfo.InvariantCulture);
    }

    private ushort? WeaponChoice(TimelinePropModel prop) => pickerWeapon switch
    {
        WeaponAsListed => prop.WeaponTimeline,
        WeaponNone => null,
        var id => (ushort)id,
    };

    /// <summary> The picker's rows: this kind's imports first, then the catalog's list, filtered. </summary>
    private List<TimelineCatalogItem> PickerRows()
    {
        var data = catalog.Data;
        var list = pickerKind switch
        {
            TimelineAddKind.Expression => data.Expressions,
            TimelineAddKind.LipSync => data.LipShapes,
            TimelineAddKind.Sound => data.Sounds,
            TimelineAddKind.Effect => data.Effects,
            _ => data.Props,
        };
        if (list.IsDefault)
            list = [];
        if (list == pickerRowsSource && pickerKind == pickerRowsKind && importsVersion == pickerRowsImports &&
            string.Equals(pickerFilter, pickerRowsFilter, StringComparison.Ordinal))
            return pickerRows;
        pickerRows.Clear();
        var filter = pickerFilter.Trim();
        foreach (var imported in importedItems)
            if (imported.Kind == pickerKind && Matches(imported.Item, filter))
                pickerRows.Add(imported.Item);
        foreach (var item in list)
            if (Matches(item, filter))
                pickerRows.Add(item);
        pickerRowsSource = list;
        pickerRowsKind = pickerKind;
        pickerRowsImports = importsVersion;
        pickerRowsFilter = pickerFilter;
        return pickerRows;
    }

    private static bool Matches(TimelineCatalogItem item, string filter) =>
        filter.Length == 0 ||
        item.Label.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        item.Sources.Any(source => source.Contains(filter, StringComparison.OrdinalIgnoreCase)) ||
        item.Path.Contains(filter, StringComparison.OrdinalIgnoreCase);

    /// <summary> The rows in view only; returns true when one was picked and placed. </summary>
    private bool DrawPickerList(TimelineEditor current, List<TimelineCatalogItem> rows)
    {
        if (rows.Count == 0)
        {
            ImGui.TextColored(Theme.Hint, catalog.State == TimelineCatalogState.Ready || catalogKey == null ? "Nothing to list." : "The lists are still being built.");
            return false;
        }
        var picked = false;
        var clipper = ImGui.ImGuiListClipper();
        try
        {
            clipper.Begin(rows.Count, -1);
            while (clipper.Step())
                for (var index = clipper.DisplayStart; index < clipper.DisplayEnd; index++)
                {
                    var item = rows[index];
                    using var id = ImRaii.PushId(index);
                    var clicked = ImGui.Selectable(item.Label);
                    // Of the libraries that hold a face, the timeline's own when it is one of them.
                    var pack = TimelineWindowViews.PackFor(item.Packs, current.Document.FacePack);
                    if (ImGui.IsItemHovered() && item.Path.Length > 0)
                        ImGui.SetTooltip(pack == null ? item.Path
                            : string.Equals(pack, current.Document.FacePack, StringComparison.OrdinalIgnoreCase) ? $"{item.Path}\nFrom this timeline's {pack} expressions."
                            : $"{item.Path}\nNeeds the {pack} expressions.");
                    if (item.Detail.Length > 0)
                    {
                        ImGui.SameLine(0, Theme.Gap);
                        ImGui.TextColored(Theme.Muted, item.Detail);
                    }
                    if (clicked && !picked)
                        picked = Place(current, pickerKind, pickerFrame, pickerTarget, item.Path, pack, item.Prop, item.Frames ?? 0);
                }
            clipper.End();
        }
        finally
        {
            clipper.Destroy();
        }
        return picked;
    }

    /// <summary>
    /// Adds an event of <paramref name="kind"/> at <paramref name="frame"/>, or gives the event
    /// <paramref name="target"/> a new path or prop model. Sets <see cref="pickerError"/> when refused.
    /// Lip sync plays at its own speed, so it gets the motion's own <paramref name="frames"/> when known.
    /// </summary>
    private bool Place(TimelineEditor current, TimelineAddKind kind, int frame, string? target, string path, string? pack,
        TimelinePropModel? prop, int frames = 0)
    {
        pickerError = null;
        if (ReadOnly)
        {
            pickerError = "Wait for the save to finish.";
            return false;
        }
        if (target != null)
            return Replace(current, kind, target, path, pack, prop);
        if (kind == TimelineAddKind.Prop && prop != null)
            prop = prop with { WeaponTimeline = WeaponChoice(prop) };
        ImmutableArray<TimelineEvent> events;
        try { events = TimelineTemplates.Create(new TimelineAddRequest(kind, frame, path, kind == TimelineAddKind.LipSync ? frames : 0, pack, prop)); }
        catch (InvalidDataException e)
        {
            pickerError = e.Message;
            return false;
        }
        if (current.Add(events, pack))
        {
            current.Reveal(frame);
            return true;
        }
        pickerError = current.LastError ?? "It could not be added.";
        return false;
    }

    private bool Replace(TimelineEditor current, TimelineAddKind kind, string key, string path, string? pack, TimelinePropModel? prop)
    {
        var doc = current.Document;
        if (TimelineEdits.Find(doc, key) is not { } ev)
        {
            pickerError = "The event is gone.";
            return false;
        }
        if (kind == TimelineAddKind.Prop)
        {
            if (prop == null)
            {
                pickerError = "Pick a prop model.";
                return false;
            }
            var values = (ev.Values ?? ImmutableDictionary<string, double>.Empty)
                .SetItem("Model", prop.Model).SetItem("Body", prop.Body).SetItem("Variant", prop.Variant);
            return Applied(current, current.Replace(ev with { Values = values }));
        }
        // The face being swapped out no longer holds the library.
        var facePack = TimelineWindowViews.PackAfter(doc.FacePack, pack, out var error,
            TimelineEdits.UsesPack(doc, current.ResidentMotions, except: key));
        if (error != null)
        {
            pickerError = error;
            return false;
        }
        var updated = ev with { Path = path };
        // A face from a library the timeline does not load yet brings the library with it, as one step.
        return Applied(current, !string.Equals(facePack, doc.FacePack, StringComparison.Ordinal)
            ? current.Commit(TimelineEdits.Replace(doc with { FacePack = facePack }, updated))
            : current.Replace(updated));
    }

    /// <summary>
    /// Whether a change of the event went in. The editor refuses one while a drag runs; one that
    /// changed nothing (the event already had it) counts as done.
    /// </summary>
    private bool Applied(TimelineEditor current, bool changed)
    {
        if (changed || current.Gesture == TimelineGesture.None)
            return true;
        pickerError = "Finish the drag first.";
        return false;
    }

    private void OpenImportDialog(TimelineAddKind kind)
    {
        var target = editor != null ? TimelineEdits.Find(editor.Document, pickerTarget) : null;
        var job = new ImportJob(kind, pickerFrame, pickerTarget, string.Empty, source is { } current ? TimelineSession.ClipKey(current) : null,
            loadGeneration, target?.Path);
        var volume = importVolume / 100f;
        var sound = kind == TimelineAddKind.Sound;
        fileDialog.OpenFileDialog(sound ? "Choose a sound" : "Choose an effect", sound ? TimelineWindowViews.SoundFilter : TimelineWindowViews.EffectFilter,
            (success, files) =>
            {
                if (success && files.Count > 0)
                    StartImport(job with { Name = Path.GetFileName(files[0]) }, files[0], volume);
            }, 1, lastImportFolder, false);
    }

    /// <summary> Converts and checks the file on a task; the result is placed when the task is polled. </summary>
    private void StartImport(ImportJob job, string file, float volume)
    {
        if (importTask != null)
        {
            importError = "Another file is still importing.";
            return;
        }
        importError = null;
        importMessage = null;
        importing = job;
        lastImportFolder = Path.GetDirectoryName(file);
        importTask = job.Kind == TimelineAddKind.Sound
            ? Task.Run(() => TimelineImports.Sound(file, volume, (path, level) => AudioImport.FromFile(path, session.ScdTemplate(), level),
                bytes => ScdFile.TrySeconds(bytes), bytes =>
                {
                    // Throws for tables that don't parse.
                    if (ScdFile.Info(bytes).SoundCount == 0)
                        throw new InvalidDataException("This SCD file has no sounds.");
                }))
            : Task.Run(() => TimelineImports.Effect(file, session.GameFileExists));
    }

    private void PollImport()
    {
        if (importTask is not { IsCompleted: true } task)
            return;
        importTask = null;
        var job = importing;
        importing = null;
        if (task.IsFaulted || task.IsCanceled)
        {
            importError = $"{job?.Name ?? "The file"} could not be imported. {TimelineSession.ErrorText(task.Exception)}";
            return;
        }
        var import = task.Result;
        // The game path holds the content's hash, so the same path is the same file.
        var known = session.Imports.ContainsKey(import.GamePath);
        session.Imports[import.GamePath] = import;
        if (!known && job != null)
        {
            importedItems.Insert(0, new ImportedItem(job.Kind, new TimelineCatalogItem(
                Path.GetFileNameWithoutExtension(job.Name), import.GamePath, TimelineCatalogOrigin.Mod, "Imported")));
            importsVersion++;
        }
        if (import.Seconds > 0)
            catalog.RememberSeconds(import.GamePath, import.Seconds);
        var name = job?.Name ?? import.GamePath;
        if (job?.Sound is { } sound)
        {
            if (editor == null || source == null || job.Clip != TimelineSession.ClipKey(source) || ReadOnly || job.Generation != loadGeneration)
                importMessage = $"{name} was imported, but the timeline changed. Add it again.";
            else
                PlaceSound(editor, sound, import, name);
            return;
        }
        if (job == null || editor == null || source == null || job.Clip != TimelineSession.ClipKey(source) || ReadOnly ||
            job.Target != null && (job.Generation != loadGeneration || TimelineEdits.Find(editor.Document, job.Target) is not { } target ||
                                   TimelineWindowViews.PickerFor(target) != job.Kind || target.Path != job.TargetPath))
        {
            importMessage = $"{name} was imported. Pick it from the list to place it.";
            return;
        }
        if (Place(editor, job.Kind, job.Frame, job.Target, import.GamePath, null, null))
            importMessage = $"{name} was imported.";
        else
            importError = $"{name} was imported but could not be placed. {pickerError}";
    }

    /// <summary>
    /// How many frames a sound event visibly takes: once its file is read, as its changes leave it (its
    /// longest alternative, or the one it is pinned to, <see cref="TimelineWindowViews.SoundSpan"/>);
    /// until then the catalog's length of the file.
    /// </summary>
    private int VisibleFrames(TimelineEvent ev)
    {
        if (ev.Magic != "C063" || ev.Path.Length == 0)
            return 0;
        if (editor != null && source is { } loaded && SoundSpanOf(editor.Document, ev, loaded) is { Total: > 0 } span)
            return span.Total;
        var seconds = catalog.Seconds(ev.Path);
        if (seconds == null)
            pendingLengths.Add(ev.Path);
        return TimelineWindowViews.SoundFrames(seconds);
    }

    /// <summary>
    /// The frames a sound event's clip is solid for and lasts (see <see cref="TimelineWindowViews.SoundSpan"/>);
    /// null before its file is read, which this starts. The path waits in pendingLengths until then.
    /// </summary>
    private (int Solid, int Total)? SoundSpanOf(TimelineDocument doc, TimelineEvent ev, Services.Animations.AnimationTimelineSource loaded)
    {
        if (TimelineSoundKey.Of(ev) is not { } key || !key.Path.EndsWith(".scd", StringComparison.OrdinalIgnoreCase) ||
            !Services.Animations.AnimationDependencies.SafeGamePath(key.Path))
            return null;
        var read = soundFiles.SoundFor(key.Path, loaded);
        if (read.Bytes == null)
        {
            if (read.Error == null)
                pendingLengths.Add(ev.Path);
            return null;
        }
        if (soundFiles.ShownSound(doc, key, loaded) is not { Seconds.IsEmpty: false } shown)
            return null;
        return TimelineWindowViews.SoundSpan(shown.Seconds, ev.Pin is { } pin && shown.Ids.IndexOf(pin) is >= 0 and var index ? index : null);
    }

    /// <summary> Lays the lanes out again once sound lengths that were being read are known. </summary>
    private void PollLengths()
    {
        if (editor == null || pendingLengths.Count == 0 || ImGui.GetFrameCount() % 15 != 0)
            return;
        if (pendingLengths.RemoveWhere(path => catalog.Seconds(path) != null || SoundReadEnded(path)) == 0)
            return;
        // A different delegate drops the editor's cached layout.
        editor.VisibleLength = null;
        editor.VisibleLength = VisibleFrames;
    }

    /// <summary> Whether the inspector's read of the sound at <paramref name="path"/> has ended, so its clip can take its length. </summary>
    private bool SoundReadEnded(string path) =>
        source is { } loaded && soundFiles.Started(path) && soundFiles.SoundFor(path, loaded) is { } read && (read.Bytes != null || read.Error != null);
}
