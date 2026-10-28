using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Services.Audio;
using InstantEdit.Services.PenumbraMods;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// What can be placed: one picker popup per kind listing the catalog (thousands of rows, so a
/// filter and a list clipper), imports of sound and effect files on a task, and typed game paths.
/// A picker either adds at a frame or chooses a new path or model for an existing event. Sound
/// events take as long as their file plays, so the lengths read for them lay the lanes out again.
/// </summary>
internal sealed class TimelinePickers(TimelineSession session, SoundFiles soundFiles)
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

    // Props from a mod: the mod list, asked again each time the picker opens; the chosen mod's models under its
    // current settings, read on a task; the pick, resolved into a snapshot on a task and placed when it is polled.
    private sealed record ModModels(string Directory, ImmutableArray<PropModelSources.PropCandidate> Models);
    private sealed record PropPick(string Label, int Frame, int Summon, string? Clip, int Generation);
    // A resolved pick: its snapshot and what the search for a free body found outside the timeline (null when its
    // material name is refused, so nothing was searched).
    private sealed record PropPicked(TimelinePropSource Source, PropModelSources.PropBodies? Bodies);
    private bool pickerFromMod;
    private int pickerSummon;
    private (string Directory, string Name, string Root)? pickerMod;
    private string modFilter = string.Empty;
    private ImmutableArray<(string Directory, string Name, string Root)> mods = [];
    private Task<ImmutableArray<(string Directory, string Name, string Root)>>? modsTask;
    private bool modsAsked;
    private string? modsError;
    private ModModels? modModels;
    private Task<ModModels>? modModelsTask;
    private string? modModelsFor;
    private string? modModelsError;
    private Task<PropPicked>? propTask;
    private PropPick? propPick;
    private int pickerShownFrame = -1;
    private string gamePathText = string.Empty;

    // The filtered rows, rebuilt when the list, the filter or the imports change.
    private readonly List<TimelineCatalogItem> pickerRows = [];
    private ImmutableArray<TimelineCatalogItem> pickerRowsSource;
    private TimelineAddKind pickerRowsKind;
    private string pickerRowsFilter = string.Empty;
    private int pickerRowsImports = -1;
    private int pickerRowsInstalled = -1;

    // Faces from the expression libraries that mods on in the collection add, asked each time a face
    // picker opens and again when the list of them is rebuilt.
    private Task<ImmutableArray<FaceLibrary>>? installedTask;
    private (Guid Collection, string Race, string Face, int Revision)? installedKey;
    private ImmutableArray<TimelineCatalogItem> installedFaces = [];
    private ImmutableArray<TimelineCatalogItem> installedLips = [];
    private int installedVersion;

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

    /// <summary> A document was read: imports for the one before can't be placed into it, and its sound lengths start over. </summary>
    public void ClipLoaded()
    {
        loadGeneration++;
        pendingLengths.Clear();
    }

    /// <summary> One line for an import running or done; the done ones can be dismissed. </summary>
    public void DrawImportStatus()
    {
        if (importTask != null && importing is { } work)
        {
            Widgets.Spinner();
            ImGui.SameLine(0, Theme.Gap);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Muted, $"Importing {work.Name}");
        }
        else if (importError != null)
        {
            if (TimelineUi.Note("##import-error-dismiss", Theme.Warning, importError))
                importError = null;
        }
        else if (importMessage != null)
        {
            if (TimelineUi.Note("##import-message-dismiss", Theme.Success, importMessage))
                importMessage = null;
        }
    }

    /// <summary>
    /// Opens the picker on the next draw at the window's own level, so a menu can ask for it.
    /// <paramref name="target"/> is the event whose path or model it replaces; null adds at <paramref name="frame"/>.
    /// </summary>
    public void Request(TimelineAddKind kind, int frame, string? target)
    {
        if (kind != pickerKind)
            pickerFilter = string.Empty;
        pickerKind = kind;
        pickerFrame = Math.Max(0, frame);
        pickerTarget = target;
        pickerError = null;
        pickerWeapon = WeaponAsListed;
        pickerSummon = 0;
        pickerOpenRequested = true;
        // The mods and their settings may have changed since the last look.
        modsAsked = false;
        modModels = null;
        modModelsError = null;
        // The collection may have changed since the last look.
        if (kind is TimelineAddKind.Expression or TimelineAddKind.LipSync)
            installedKey = null;
    }

    /// <summary>
    /// Keeps the installed expression libraries' faces for the open clip's race up to date: asks again
    /// when the clip, the collection or the list of libraries changed, and takes the answer once it is in.
    /// </summary>
    private void PollInstalled()
    {
        if (installedTask is { IsCompleted: true } done)
        {
            var active = done.IsCompletedSuccessfully ? done.Result : [];
            if (done.Exception is { } error)
                session.Log.Debug(error, "Could not tell which expression sets the collection plays.");
            var resident = session.Catalog.Data.ResidentMotions;
            installedFaces = FaceLibraryIndex.Items(active, false, resident);
            installedLips = FaceLibraryIndex.Items(active, true, resident);
            installedVersion++;
            installedTask = null;
        }
        if (installedTask != null || session.Animations is not { } service || session.Source is not { } source || session.CatalogKey is not { } key)
            return;
        service.StartFaceLibraries();
        var wanted = (source.Capture.CollectionId, key.RaceCode, key.Face, service.FaceLibraries.Revision);
        if (installedKey == wanted || service.FaceLibraries.Status.State is not FaceLibraryState.Ready)
            return;
        installedKey = wanted;
        installedTask = Task.Run(() => service.ActiveFaceLibrariesAsync(wanted.CollectionId, wanted.RaceCode, wanted.Face, CancellationToken.None));
    }

    public void Draw(TimelineEditor current)
    {
        if (pickerOpenRequested)
        {
            ImGui.OpenPopup(PickerPopup);
            pickerOpenRequested = false;
        }
        using var popup = ImRaii.Popup(PickerPopup);
        if (!popup.Success)
            return;
        pickerShownFrame = ImGui.GetFrameCount();
        var width = Theme.Scaled(500);
        using var wrap = ImRaii.TextWrapPos(width);
        var name = TimelineWindowViews.AddKindName(pickerKind).ToLowerInvariant();
        ImGui.TextColored(Theme.Label, pickerTarget != null ? $"Choose another {name}" : $"Add {name} at {TimelineText.Seconds(pickerFrame)}");
        ImGui.Separator();
        DrawCatalogState();
        if (pickerKind is TimelineAddKind.Expression or TimelineAddKind.LipSync)
        {
            PollInstalled();
            DrawInstalledState();
        }
        switch (pickerKind)
        {
            case TimelineAddKind.Sound or TimelineAddKind.Effect:
                DrawImportRow(current);
                break;
            case TimelineAddKind.Prop when pickerTarget == null:
                DrawPropSource();
                if (pickerFromMod)
                    DrawModChoice();
                else
                    DrawWeaponChoice();
                break;
        }

        var fromMod = pickerKind == TimelineAddKind.Prop && pickerTarget == null && pickerFromMod;
        Widgets.SearchBox("##timeline-picker-search", ref pickerFilter, fromMod ? "Search by file name or path" : "Search by name, emote or path");
        var picked = false;
        if (fromMod)
        {
            var models = ModRows();
            ImGui.TextColored(Theme.Muted, models.Count == 1 ? "1 item" : models.Count.ToString("N0", CultureInfo.InvariantCulture) + " items");
            using var list = ImRaii.Child("##timeline-picker-mod-list", new Vector2(width, Theme.Scaled(300)), true);
            if (list.Success)
                DrawModRows(current, models);
        }
        else
        {
            var rows = PickerRows();
            ImGui.TextColored(Theme.Muted, rows.Count == 1 ? "1 item" : rows.Count.ToString("N0", CultureInfo.InvariantCulture) + " items");
            using var list = ImRaii.Child("##timeline-picker-list", new Vector2(width, Theme.Scaled(300)), true);
            if (list.Success)
                picked = DrawPickerList(current, rows);
        }
        if (fromMod && propTask != null && propPick is { } reading)
        {
            Widgets.Spinner();
            ImGui.SameLine(0, Theme.Gap);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Muted, $"Reading {reading.Label}");
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
        if (session.CatalogKey == null)
        {
            Widgets.HintWrapped("This animation is not for a player race, so only imports and typed game paths can be placed.");
            return;
        }
        switch (session.Catalog.State)
        {
            case TimelineCatalogState.Ready:
                break;
            case TimelineCatalogState.Failed:
                ImGui.TextColored(Theme.Warning, session.Catalog.Status.Length > 0 ? session.Catalog.Status : "The lists could not be built.");
                if (ImGui.SmallButton("Retry"))
                    session.Catalog.Rebuild();
                break;
            default:
                Widgets.Spinner();
                ImGui.SameLine(0, Theme.Gap);
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(Theme.Muted, session.Catalog.Status.Length > 0 ? session.Catalog.Status : "Finding what can be placed");
                break;
        }
        foreach (var note in session.Catalog.Data.Notes)
            ImGui.TextColored(Theme.Hint, note);
    }

    /// <summary> While the list of the mods' expression sets is built, or why it failed. </summary>
    private void DrawInstalledState()
    {
        if (session.Animations is not { } service || session.CatalogKey == null)
            return;
        var status = service.FaceLibraries.Status;
        if (TimelineWindowViews.FaceLibraryNote(status) is { } note)
        {
            if (status.State is FaceLibraryState.Loading or FaceLibraryState.Building)
            {
                Widgets.Spinner();
                ImGui.SameLine(0, Theme.Gap);
                ImGui.AlignTextToFramePadding();
            }
            ImGui.TextColored(status.State == FaceLibraryState.Failed ? Theme.Warning : Theme.Muted, note);
        }
    }

    /// <summary> Import a file from disk (a sound with its volume), or type a game path the game or a mod has. </summary>
    private void DrawImportRow(TimelineEditor current)
    {
        var sound = pickerKind == TimelineAddKind.Sound;
        var readOnly = session.ReadOnly;
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
                else if (Place(current, pickerKind, pickerFrame, pickerTarget, path, [], null))
                    pickerClose = true;
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Use a file the game or one of your mods already has at this path.");
    }

    /// <summary> For props: the animation the prop plays, as the emote that uses it does, none, or another one. </summary>
    private void DrawWeaponChoice()
    {
        var timelines = session.Catalog.Data.WeaponTimelines;
        ImGui.SetNextItemWidth(Theme.Scaled(320));
        using var combo = ImRaii.Combo("Prop animation", WeaponLabel(timelines));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("What the prop plays after it appears.");
        if (!combo.Success)
        {
            if (ComboWheel.Steps() is var steps and not 0)
                pickerWeapon = ComboWheel.Pick([WeaponAsListed, WeaponNone, .. timelines.Select(timeline => timeline.Id)], pickerWeapon, steps);
            return;
        }
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

    /// <summary> For props: the game's props, or a model from one of the user's mods. </summary>
    private void DrawPropSource()
    {
        if (ImGui.RadioButton("Game props", !pickerFromMod))
            pickerFromMod = false;
        ImGui.SameLine(0, Theme.Scaled(12));
        if (ImGui.RadioButton("From a mod", pickerFromMod))
            pickerFromMod = true;
    }

    /// <summary> From a mod: the mod (Penumbra's list, with a search) and the summon slot the prop goes on. </summary>
    private void DrawModChoice()
    {
        PollMods();
        ImGui.SetNextItemWidth(Theme.Scaled(320));
        using (var combo = ImRaii.Combo("Mod", pickerMod?.Name ?? (modsTask != null ? "Reading the mod list" : "Choose a mod")))
        {
            if (combo.Success)
            {
                if (ImGui.IsWindowAppearing())
                    ImGui.SetKeyboardFocusHere();
                ImGui.SetNextItemWidth(-1);
                ImGui.InputTextWithHint("##prop-mod-filter", "Search mods", ref modFilter, 128);
                var filter = modFilter.Trim();
                var shown = filter.Length == 0 ? mods : [.. mods.Where(mod => mod.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                                                                            mod.Directory.Contains(filter, StringComparison.OrdinalIgnoreCase))];
                var clipper = ImGui.ImGuiListClipper();
                try
                {
                    clipper.Begin(shown.Length, -1);
                    while (clipper.Step())
                        for (var index = clipper.DisplayStart; index < clipper.DisplayEnd; index++)
                        {
                            var mod = shown[index];
                            using var id = ImRaii.PushId(index);
                            if (ImGui.Selectable(mod.Name, pickerMod?.Directory == mod.Directory) && pickerMod?.Directory != mod.Directory)
                            {
                                pickerMod = mod;
                                modModels = null;
                                modModelsError = null;
                            }
                        }
                    clipper.End();
                }
                finally
                {
                    clipper.Destroy();
                }
            }
        }
        if (modsError != null)
            ImGui.TextColored(Theme.Warning, modsError);

        ImGui.SetNextItemWidth(Theme.Scaled(120));
        using (var slot = ImRaii.Combo("Slot", $"Summon {pickerSummon}"))
        {
            if (slot.Success)
                for (var summon = 0; summon <= 1; summon++)
                    if (ImGui.Selectable($"Summon {summon}", pickerSummon == summon))
                        pickerSummon = summon;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Put a second prop on Summon 1 to show both at once.");
    }

    /// <summary> Asks Penumbra for its mods once each time the picker opens in From a mod, and takes the answer once it is in. </summary>
    private void PollMods()
    {
        if (modsTask is { IsCompleted: true } done)
        {
            modsTask = null;
            if (done.IsCompletedSuccessfully)
            {
                mods = done.Result;
                modsError = null;
                // A mod gone from the list can't be picked from.
                if (pickerMod is { } chosen && !mods.Any(mod => mod.Directory == chosen.Directory))
                    pickerMod = null;
            }
            else
                modsError = $"The mod list could not be read. {TimelineSession.ErrorText(done.Exception)}";
        }
        if (modsAsked || modsTask != null || session.Animations is not { } service)
            return;
        modsAsked = true;
        modsTask = service.InstalledModsAsync();
    }

    /// <summary>
    /// The chosen mod's models that can be props, read on a task under its current settings in the clip's collection
    /// (<see cref="ModOptionGroups.Selection"/>: its defaults when the collection has none); filtered by the search.
    /// </summary>
    private List<PropModelSources.PropCandidate> ModRows()
    {
        if (modModelsTask is { IsCompleted: true } done)
        {
            modModelsTask = null;
            if (done.IsCompletedSuccessfully)
                modModels = done.Result;
            // Another mod may have been chosen while this one was read.
            else if (modModelsFor == pickerMod?.Directory)
                modModelsError = $"The mod's files could not be read. {TimelineSession.ErrorText(done.Exception)}";
        }
        if (pickerMod is not { } mod)
            return [];
        if (modModels is { } models && models.Directory == mod.Directory)
        {
            var filter = pickerFilter.Trim();
            return [.. models.Models.Where(model => filter.Length == 0 || model.GamePath.Contains(filter, StringComparison.OrdinalIgnoreCase))];
        }
        if (modModelsTask == null && modModelsError == null && session.Animations is { } service && session.Source is { } source)
        {
            var collection = source.Capture.CollectionId;
            modModelsFor = mod.Directory;
            modModelsTask = Task.Run(async () =>
            {
                var settings = await service.ModSettingsAsync(collection, mod.Directory).ConfigureAwait(false);
                // The settings may come back on the framework thread: the mod's files are read off it.
                return await Task.Run(() => new ModModels(mod.Directory, PropModelSources.ModModels(mod.Root, settings))).ConfigureAwait(false);
            });
        }
        return [];
    }

    /// <summary> The chosen mod's models: a click resolves one into a prop; one that bends with other bones is shown but can't be picked. </summary>
    private void DrawModRows(TimelineEditor current, List<PropModelSources.PropCandidate> models)
    {
        if (pickerMod is not { } mod)
        {
            ImGui.TextColored(Theme.Hint, "Choose a mod.");
            return;
        }
        if (modModelsError != null)
        {
            ImGui.TextColored(Theme.Warning, modModelsError);
            if (ImGui.SmallButton("Retry"))
                modModelsError = null;
            return;
        }
        if (modModelsTask != null || modModels?.Directory != mod.Directory)
        {
            Widgets.Spinner();
            ImGui.SameLine(0, Theme.Gap);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Muted, "Reading the mod's files");
            return;
        }
        if (models.Count == 0)
        {
            ImGui.TextColored(Theme.Hint, "No weapon, monster or demihuman models in this mod.");
            return;
        }
        var clipper = ImGui.ImGuiListClipper();
        try
        {
            clipper.Begin(models.Count, -1);
            while (clipper.Step())
                for (var index = clipper.DisplayStart; index < clipper.DisplayEnd; index++)
                {
                    var model = models[index];
                    using var id = ImRaii.PushId(index);
                    bool clicked;
                    using (ImRaii.Disabled(!model.Qualifies || propTask != null || session.ReadOnly))
                        clicked = ImGui.Selectable(model.Label);
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                        ImGui.SetTooltip(model.Qualifies ? model.GamePath : "Bends with bones a prop doesn't have");
                    ImGui.SameLine(0, Theme.Gap);
                    ImGui.TextColored(Theme.Muted, mod.Name);
                    if (clicked)
                        StartPropPick(current, mod, model);
                }
            clipper.End();
        }
        finally
        {
            clipper.Destroy();
        }
    }

    /// <summary>
    /// Picks <paramref name="model"/> of <paramref name="mod"/>: its private set and a body to resolve it under now, so a
    /// full timeline says so at once, then on a task the mod's current files, the snapshot and the search for a body
    /// free outside the timeline too (<see cref="PropModelSources.FindBody"/>: the bodies the animation's own mod maps
    /// when it can be saved in place, and Penumbra's answer for each model path in the clip's collection);
    /// <see cref="PollPropPick"/> chooses its body and places it.
    /// </summary>
    private void StartPropPick(TimelineEditor current, (string Directory, string Name, string Root) mod, PropModelSources.PropCandidate model)
    {
        pickerError = null;
        if (propTask != null || session.Animations is not { } service || session.Source is not { } source)
            return;
        if (session.ReadOnly)
        {
            pickerError = "Wait for the save to finish.";
            return;
        }
        var set = PropModelSources.TargetSet(model.GamePath);
        if (PropModelSources.BodyFor(current.Document, mod.Directory, model.GamePath, set, session.GameFileExists) is not { } body)
        {
            pickerError = "No free model number left for this prop.";
            return;
        }
        var collection = source.Capture.CollectionId;
        var taken = PropModelSources.Taken(current.Document);
        // The animation's own mod, when a save can write into it: the bodies it maps are taken even with their option off.
        var inPlace = AnimationResources.CanReplace(source.Resource) ? source.Resource.ModRoot : null;
        propPick = new PropPick(model.Label, pickerFrame, pickerSummon, TimelineSession.ClipKey(source), loadGeneration);
        Func<string, byte[]?> game = path => session.Data.GetFile(path)?.Data;
        propTask = Task.Run(async () =>
        {
            var settings = await service.ModSettingsAsync(collection, mod.Directory).ConfigureAwait(false);
            // The settings may come back on the framework thread: the mod's files are read off it.
            var (resolved, mapped) = await Task.Run(() => (
                PropModelSources.Resolve(mod.Directory, mod.Name, model.GamePath, set, body, PropModelSources.ModFiles(mod.Root, settings), game, session.GameFileExists),
                inPlace is null ? new HashSet<(ushort, ushort)>() : PropModelSources.ModBodies(inPlace))).ConfigureAwait(false);
            if (PropModelSources.MaterialUnsavable(resolved))
                return new PropPicked(resolved, null);
            // Each model path is resolved on the framework thread (the service hops there), one body at a time.
            var bodies = await PropModelSources.FindBody(resolved, taken, mapped, session.GameFileExists,
                path => service.ResolvePathAsync(collection, path)).ConfigureAwait(false);
            return new PropPicked(resolved, bodies);
        });
    }

    /// <summary>
    /// Places a resolved prop from a mod once its task is done, into the clip it was picked for, on the body
    /// <see cref="PropModelSources.BodyFor(TimelineDocument, TimelinePropSource, Func{string, bool}, ushort, Func{ushort, bool})"/>
    /// gives it then from the pick's search. Closes the picker when it is still open.
    /// </summary>
    private void PollPropPick()
    {
        if (propTask is not { IsCompleted: true } task)
            return;
        propTask = null;
        var pick = propPick;
        propPick = null;
        var open = pickerShownFrame >= ImGui.GetFrameCount() - 1;
        void Fail(string message)
        {
            if (open)
                pickerError = message;
            else
                importError = message;
        }
        if (pick == null)
            return;
        if (!task.IsCompletedSuccessfully)
        {
            Fail($"{pick.Label} could not be read. {TimelineSession.ErrorText(task.Exception)}");
            return;
        }
        var (source, bodies) = task.Result;
        if (session.Editor is not { } editor || session.Source is not { } loaded || TimelineSession.ClipKey(loaded) != pick.Clip || pick.Generation != loadGeneration)
        {
            Fail("The timeline changed. Pick the prop again.");
            return;
        }
        if (bodies is null)
        {
            Fail(PropModelSources.UnsavableMaterial);
            return;
        }
        // The body is chosen now, from the snapshot and the timeline as they are: the same files share a prop's body,
        // other files (the mod's options changed) get their own, the one the search found free outside the timeline.
        if (PropModelSources.BodyFor(editor.Document, source, session.GameFileExists, bodies.Start, bodies.Outside) is not { } body)
        {
            // None free, or the timeline took the free one meanwhile (the search didn't ask about the others).
            Fail(bodies.Free is null ? "No free model number left for this prop." : "The timeline changed. Pick the prop again.");
            return;
        }
        source = PropModelSources.Rebodied(source, body);
        if (Place(editor, TimelineAddKind.Prop, pick.Frame, null, "", [], new TimelinePropModel(source.Set, source.Body, 1, null, source), summon: pick.Summon))
        {
            if (open)
                pickerClose = true;
        }
        else if (!open)
            importError = pickerError;
    }

    /// <summary> The picker's rows: this kind's imports first, then the catalog's list, then the faces of the mods' expression sets, filtered. </summary>
    private List<TimelineCatalogItem> PickerRows()
    {
        var data = session.Catalog.Data;
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
            installedVersion == pickerRowsInstalled && string.Equals(pickerFilter, pickerRowsFilter, StringComparison.Ordinal))
            return pickerRows;
        pickerRows.Clear();
        var filter = pickerFilter.Trim();
        foreach (var imported in importedItems)
            if (imported.Kind == pickerKind && Matches(imported.Item, filter))
                pickerRows.Add(imported.Item);
        foreach (var item in list)
            if (Matches(item, filter))
                pickerRows.Add(item);
        ImmutableArray<TimelineCatalogItem> installed = pickerKind switch
        {
            TimelineAddKind.Expression => installedFaces,
            TimelineAddKind.LipSync => installedLips,
            _ => [],
        };
        foreach (var item in installed)
            if (Matches(item, filter))
                pickerRows.Add(item);
        pickerRowsSource = list;
        pickerRowsKind = pickerKind;
        pickerRowsImports = importsVersion;
        pickerRowsInstalled = installedVersion;
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
            ImGui.TextColored(Theme.Hint, session.Catalog.State == TimelineCatalogState.Ready || session.CatalogKey == null ? "Nothing to list." : "The lists are still being built.");
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
                    if (ImGui.IsItemHovered() && item.Path.Length > 0)
                        ImGui.SetTooltip(TimelineWindowViews.PackTip(item.Path, item.Packs, current.Document.FacePack, current.ChoosePack(item.Packs, pickerTarget)));
                    if (item.Detail.Length > 0)
                    {
                        ImGui.SameLine(0, Theme.Gap);
                        ImGui.TextColored(Theme.Muted, item.Detail);
                    }
                    if (clicked && !picked)
                        picked = Place(current, pickerKind, pickerFrame, pickerTarget, item.Path, item.Packs, item.Prop, item.Frames ?? 0);
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
    /// A face comes from one of the expression libraries that hold it (<paramref name="packs"/>), switching
    /// the timeline's when another holds all its faces (<see cref="TimelineEditor.ChoosePack"/>).
    /// Lip sync plays at its own speed, so it gets the motion's own <paramref name="frames"/> when known. A prop goes on
    /// summon slot <paramref name="summon"/>; one from a mod stays still.
    /// </summary>
    private bool Place(TimelineEditor current, TimelineAddKind kind, int frame, string? target, string path, ImmutableArray<string> packs,
        TimelinePropModel? prop, int frames = 0, int summon = 0)
    {
        pickerError = null;
        if (session.ReadOnly)
        {
            pickerError = "Wait for the save to finish.";
            return false;
        }
        var choice = current.ChoosePack(packs, target);
        if (choice.Refused)
        {
            pickerError = TimelineEdits.PackBlocked([.. choice.Blockers.Select(FaceLabel)]);
            return false;
        }
        var pack = choice.Pack;
        if (target != null)
            return Replace(current, kind, target, path, choice, prop);
        if (kind == TimelineAddKind.Prop && prop is { Source: null })
            prop = prop with { WeaponTimeline = WeaponChoice(prop) };
        ImmutableArray<TimelineEvent> events;
        try
        {
            events = TimelineTemplates.Create(new TimelineAddRequest(kind, frame, path, kind == TimelineAddKind.LipSync ? frames : 0, pack, prop,
                current.Document.Length, kind == TimelineAddKind.Prop ? summon : 0));
        }
        catch (InvalidDataException e)
        {
            pickerError = e.Message;
            return false;
        }
        if (current.Add(events, pack, choice.Switches))
        {
            current.Reveal(frame);
            return true;
        }
        pickerError = current.LastError ?? "It could not be added.";
        return false;
    }

    /// <summary> The name the picker lists a face or lip motion by, else the motion's own. </summary>
    private string FaceLabel(string motion)
    {
        var data = session.Catalog.Data;
        return data.Expressions.Concat(data.LipShapes).Concat(installedFaces).Concat(installedLips)
            .FirstOrDefault(item => string.Equals(item.Path, motion, StringComparison.Ordinal))?.Label ?? motion;
    }

    private bool Replace(TimelineEditor current, TimelineAddKind kind, string key, string path, TimelinePackChoice choice, TimelinePropModel? prop)
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
        // A face from a library the timeline does not load yet brings the library with it, as one step.
        return Applied(current, current.ReplaceFace(ev with { Path = path }, choice));
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
        var target = session.Editor != null ? TimelineEdits.Find(session.Editor.Document, pickerTarget) : null;
        var job = new ImportJob(kind, pickerFrame, pickerTarget, string.Empty, session.Source is { } current ? TimelineSession.ClipKey(current) : null,
            loadGeneration, target?.Path);
        var volume = importVolume / 100f;
        var sound = kind == TimelineAddKind.Sound;
        session.FileDialog.OpenFileDialog(sound ? "Choose a sound" : "Choose an effect", sound ? TimelineWindowViews.SoundFilter : TimelineWindowViews.EffectFilter,
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

    public void PollImport()
    {
        PollPropPick();
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
            session.Catalog.RememberSeconds(import.GamePath, import.Seconds);
        var name = job?.Name ?? import.GamePath;
        if (job?.Sound is { } sound)
        {
            if (session.Editor == null || session.Source == null || job.Clip != TimelineSession.ClipKey(session.Source) || session.ReadOnly || job.Generation != loadGeneration)
                importMessage = $"{name} was imported, but the timeline changed. Add it again.";
            else
                PlaceSound(session.Editor, sound, import, name);
            return;
        }
        if (job == null || session.Editor == null || session.Source == null || job.Clip != TimelineSession.ClipKey(session.Source) || session.ReadOnly ||
            job.Target != null && (job.Generation != loadGeneration || TimelineEdits.Find(session.Editor.Document, job.Target) is not { } target ||
                                   TimelineWindowViews.PickerFor(target) != job.Kind || target.Path != job.TargetPath))
        {
            importMessage = $"{name} was imported. Pick it from the list to place it.";
            return;
        }
        if (Place(session.Editor, job.Kind, job.Frame, job.Target, import.GamePath, [], null))
            importMessage = $"{name} was imported.";
        else
            importError = $"{name} was imported but could not be placed. {pickerError}";
    }

    /// <summary>
    /// How many frames a sound event visibly takes: once its file is read, as its changes leave it (its
    /// longest alternative, or the one it is pinned to, <see cref="TimelineWindowViews.SoundSpan"/>);
    /// until then the catalog's length of the file.
    /// </summary>
    public int VisibleFrames(TimelineEvent ev)
    {
        if (ev.Magic != "C063" || ev.Path.Length == 0)
            return 0;
        if (session.Editor != null && session.Source is { } loaded && SoundSpanOf(session.Editor.Document, ev, loaded) is { Total: > 0 } span)
            return span.Total;
        var seconds = session.Catalog.Seconds(ev.Path);
        if (seconds == null)
            pendingLengths.Add(ev.Path);
        return TimelineWindowViews.SoundFrames(seconds);
    }

    /// <summary>
    /// The frames a sound event's clip is solid for and lasts (see <see cref="TimelineWindowViews.SoundSpan"/>);
    /// null before its file is read, which this starts. The path waits in pendingLengths until then.
    /// </summary>
    private (int Solid, int Total)? SoundSpanOf(TimelineDocument doc, TimelineEvent ev, AnimationTimelineSource loaded)
    {
        if (TimelineSoundKey.Of(ev) is not { } key || !key.Path.EndsWith(".scd", StringComparison.OrdinalIgnoreCase) ||
            !AnimationDependencies.SafeGamePath(key.Path))
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
    public void PollLengths()
    {
        if (session.Editor == null || pendingLengths.Count == 0 || ImGui.GetFrameCount() % 15 != 0)
            return;
        if (pendingLengths.RemoveWhere(path => session.Catalog.Seconds(path) != null || SoundReadEnded(path)) == 0)
            return;
        // A different delegate drops the editor's cached layout.
        session.Editor.VisibleLength = null;
        session.Editor.VisibleLength = VisibleFrames;
    }

    /// <summary> Whether the inspector's read of the sound at <paramref name="path"/> has ended, so its clip can take its length. </summary>
    private bool SoundReadEnded(string path) =>
        session.Source is { } loaded && soundFiles.Started(path) && soundFiles.SoundFor(path, loaded) is { } read && (read.Bytes != null || read.Error != null);

    /// <summary> Opens the file dialog for a sound to add to <paramref name="target"/>'s set, or to put in place of one of its sounds. </summary>
    public void OpenSoundImport(TimelineSoundTarget target)
    {
        var job = new ImportJob(TimelineAddKind.Sound, 0, null, string.Empty, session.Source is { } current ? TimelineSession.ClipKey(current) : null, loadGeneration, null, target);
        session.FileDialog.OpenFileDialog(target.ReplaceId == null ? "Choose a sound to add" : "Choose a sound to put in its place", TimelineWindowViews.SoundFilter,
            (success, files) =>
            {
                if (success && files.Count > 0)
                    StartImport(job with { Name = Path.GetFileName(files[0]) }, files[0], 1f);
            }, 1, lastImportFolder, false);
    }

    /// <summary> An imported file for the Sound file section, placed into its set (see <see cref="TimelineWindowViews.Placed"/>). </summary>
    private void PlaceSound(TimelineEditor current, TimelineSoundTarget target, TimelineImport import, string name)
    {
        if (session.Source is not { } loaded || soundFiles.ShownSound(current.Document, target.Key, loaded) is not { Model: { } model } shown || SoundFiles.EditOf(shown) is not { } edit)
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
}
