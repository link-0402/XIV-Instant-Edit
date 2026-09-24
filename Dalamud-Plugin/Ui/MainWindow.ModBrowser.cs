using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Services;

namespace InstantEdit.Ui;

public sealed partial class MainWindow
{
    private readonly ModListFilter _modList = new();
    private float _modListWidth;
    private ActorView[] _modViewActors = [];

    /// <summary> Master/detail: the mod list on the left, the selected mod's resources on the right. </summary>
    private void DrawModsTab()
    {
        ImGui.Spacing();
        var mods = ReadMods();
        if (_modListWidth <= 0)
            _modListWidth = Theme.Scaled(260);
        var height = Math.Max(1, ImGui.GetContentRegionAvail().Y);
        var maxListWidth = Math.Max(Theme.Scaled(160), ImGui.GetContentRegionAvail().X - Theme.Scaled(360));
        _modListWidth = Math.Clamp(_modListWidth, Theme.Scaled(160), Math.Max(Theme.Scaled(160), maxListWidth));

        using (var list = ImRaii.Child("##mod-list", new Vector2(_modListWidth, height), true))
        {
            if (list.Success)
                DrawModList(mods);
        }
        ImGui.SameLine(0, 0);
        Widgets.Splitter("##mod-splitter", ref _modListWidth, Theme.Scaled(160), maxListWidth, height);
        ImGui.SameLine(0, 0);
        using var detail = ImRaii.Child("##mod-detail", new Vector2(0, height), false);
        if (!detail.Success)
            return;

        var selectedMod = mods.FirstOrDefault(mod =>
            string.Equals(mod.Directory, _selectedModDirectory, StringComparison.OrdinalIgnoreCase));
        if (selectedMod is null)
        {
            Widgets.EmptyState(FontAwesomeIcon.FolderOpen, "Select a mod", "Choose a Penumbra mod on the left to browse its models, textures and materials.");
            return;
        }

        DrawModHeader(selectedMod);
        var modView = GetModView(selectedMod);
        if (modView is null)
        {
            bool loading;
            bool failed;
            lock (_stateLock)
            {
                loading = _modLoading;
                failed = _modLoadFailed;
            }
            if (loading)
                Widgets.EmptyState(FontAwesomeIcon.Sync, "Scanning the mod…", "Reading the mod's files and option groups.");
            else if (failed)
                Widgets.EmptyState(FontAwesomeIcon.ExclamationTriangle, "Could not read the selected mod", "Its meta.json may be missing or older than Penumbra 1.7.1. Reload the mod in Penumbra and try again.");
            else
                Widgets.EmptyState(FontAwesomeIcon.FolderOpen, "No supported resources", "This mod has no models, textures or materials.");
            return;
        }

        if (_modViewActors.Length != 1 || !ReferenceEquals(_modViewActors[0], modView))
            _modViewActors = [modView];
        DrawFilterBar(_modViewActors, showVanillaToggle: false);
        ImGui.Spacing();
        Widgets.Banner("##mod-ambiguity", FeedbackSeverity.Info, ModBrowserAmbiguityWarning);
        ImGui.Spacing();
        DrawResources(_modViewActors, "No supported models, textures, or materials found in this mod.");
    }

    private void DrawModList(IReadOnlyList<PenumbraMod> mods)
    {
        Widgets.SearchBox("##mod-filter", ref _modFilter, "Search mods");
        var filtered = _modList.Apply(mods, _modFilter);
        using var rows = ImRaii.Child("##mod-list-rows", new Vector2(0, 0), false);
        if (!rows.Success)
            return;
        if (mods.Count == 0)
        {
            Widgets.EmptyState(FontAwesomeIcon.FolderOpen, "No mods", "Penumbra reported no mods. Is it running?");
            return;
        }
        if (filtered.Count == 0)
        {
            ImGui.TextColored(Theme.Muted, "No matching Penumbra mods.");
            return;
        }
        ImGuiClip.ClippedDraw(filtered, DrawModRow, 1, ImGui.GetTextLineHeightWithSpacing());
    }

    private void DrawModRow(PenumbraMod mod)
    {
        var selected = string.Equals(_selectedModDirectory, mod.Directory, StringComparison.OrdinalIgnoreCase);
        if (!ImGui.Selectable($"{mod.Name}##mod:{SafeId(mod.Directory)}", selected))
            return;
        if (selected)
            return;
        CancelModLoad();
        _selectedModDirectory = mod.Directory;
        _loadedModDirectory = null;
        _loadedModView = null;
    }

    private void DrawModHeader(PenumbraMod mod)
    {
        var buttonWidth = ImGui.GetFrameHeight();
        using (ImRaii.Group())
        {
            ImGui.TextColored(Theme.Label, mod.Name);
            Widgets.PathText(mod.Directory, mod.Directory);
        }
        ImGui.SameLine(ImGui.GetContentRegionMax().X - buttonWidth);
        if (Widgets.IconButton("##open-in-penumbra", FontAwesomeIcon.ExternalLinkAlt, "Open this mod in Penumbra"))
        {
            try
            {
                _penumbra.OpenModInPenumbra(mod.Directory, mod.Name);
            }
            catch (Exception e)
            {
                _log.Warning(e, "Could not open the mod in Penumbra.");
                SetStatus($"Could not open the mod in Penumbra: {e.Message}", FeedbackSeverity.Warning);
            }
        }
        ImGui.Separator();
    }

    private IReadOnlyList<PenumbraMod> ReadMods()
    {
        if (DateTime.UtcNow - _lastModListRefresh > TimeSpan.FromSeconds(2))
        {
            _mods = _penumbra.GetMods();
            _lastModListRefresh = DateTime.UtcNow;
            if (_selectedModDirectory is not null && !_mods.Any(mod =>
                    string.Equals(mod.Directory, _selectedModDirectory, StringComparison.OrdinalIgnoreCase)))
            {
                CancelModLoad();
                _selectedModDirectory = null;
                _loadedModDirectory = null;
                _loadedModView = null;
            }
        }

        return _mods;
    }

    private ActorView? GetModView(PenumbraMod mod)
    {
        lock (_stateLock)
        {
            if (string.Equals(_loadedModDirectory, mod.Directory, StringComparison.OrdinalIgnoreCase))
                return _loadedModView;
        }

        StartModLoad(mod);
        return null;
    }

    private void StartModLoad(PenumbraMod mod)
    {
        CancelModLoad();
        var cts = new CancellationTokenSource();
        var importObjectIndex = _onScreen.Items.FirstOrDefault()?.ObjectIndex ?? 0;
        lock (_stateLock)
        {
            _modLoadCts = cts;
            _loadedModDirectory = mod.Directory;
            _loadedModView = null;
            _modLoading = true;
            _modLoadFailed = false;
        }

        _ = LoadModViewAsync(mod, importObjectIndex, cts);
    }

    private async Task LoadModViewAsync(PenumbraMod mod, int importObjectIndex, CancellationTokenSource owner)
    {
        PenumbraModSnapshot? snapshot = null;
        try
        {
            snapshot = await _penumbra.GetModResourcesAsync(mod.Directory, owner.Token).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _log.Debug($"Could not load Penumbra mod resources: {e.Message}");
        }

        ActorView? view = null;
        if (snapshot is not null)
            view = ResourceViews.BuildModView(snapshot, importObjectIndex);

        lock (_stateLock)
        {
            if (!ReferenceEquals(_modLoadCts, owner))
                return;
            _loadedModView = view;
            _modLoading = false;
            _modLoadFailed = snapshot is null && !owner.IsCancellationRequested;
        }
    }

    private void CancelModLoad()
    {
        CancellationTokenSource? cts;
        lock (_stateLock)
        {
            cts = _modLoadCts;
            _modLoadCts = null;
            _modLoading = false;
        }
        cts?.Cancel();
        cts?.Dispose();
    }
}
