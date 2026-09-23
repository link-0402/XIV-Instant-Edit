using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using InstantEdit.Models;
using InstantEdit.Services;

namespace InstantEdit.Ui;

public sealed partial class MainWindow
{
    private const ImGuiTableFlags ResourceTableFlags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.BordersOuter |
                                                        ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp;
    private static readonly string[] ResourceTypeFilterGroups = ["Tree Structure", "Models", "Textures"];
    private static readonly ResourceKinds[] ResourceTypeFilterKinds = [ResourceKinds.None, ResourceKinds.Model, ResourceKinds.Texture];

    // View-model state: drawing must not rebuild, re-classify, or re-search the resource
    // tree every frame. The views are replaced when the snapshot or the vanilla toggle changes.
    private readonly ResourceKindSelection _kinds = new();
    private readonly ResourceSearch _search = new();
    private readonly ResourceTypeCounter _counter = new();
    private readonly ExpansionState _expansion = new();
    private readonly ModListFilter _modList = new();
    private IReadOnlyList<OnScreenObject>? _actorSnapshot;
    private bool _actorSnapshotIncludesVanilla;
    private List<ActorView> _actors = [];

    private void DrawResources(IReadOnlyList<ActorView> actors, string? emptyMessage = null)
    {
        actors = actors.Where(ActorMatches).ToList();
        var viewportHeight = Math.Max(1, ImGui.GetContentRegionAvail().Y);
        using var background = ImRaii.PushColor(ImGuiCol.ChildBg, Theme.PanelBg);
        using var browser = ImRaii.Child("##resource-browser", new Vector2(0, viewportHeight), true);
        if (!browser.Success)
            return;
        if (emptyMessage is null && _onScreen.IsRefreshing && actors.Count == 0) ImGui.TextColored(Theme.Muted, "Refreshing resources…");
        else if (actors.Count == 0) ImGui.TextColored(Theme.Muted, emptyMessage ?? "No snapshot loaded. Use Refresh character list to collect on-screen resources.");
        else foreach (var actor in actors) DrawActor(actor);
    }

    private void DrawActor(ActorView actor)
    {
        if (actor.Entity is null)
        {
            DrawModResourceRows(actor);
            return;
        }

        var actorId = ExpansionState.ActorKey(actor.Entity);
        using var id = ImRaii.PushId(actorId);
        DrawOpaqueRow();
        var filteredView = _kinds.IsFlat;
        var expanded = _expansion.IsExpanded(actorId, filteredView, !filteredView && _search.Active);
        if (ImGui.Button(expanded ? "▼##actor-toggle" : "▶##actor-toggle", new Vector2(Theme.ArrowWidth, ImGui.GetFrameHeight()))) _expansion.Toggle(actorId, expanded, filteredView);
        ImGui.SameLine(0, Theme.Scaled(4)); var header = Safe($"{actor.Category}{(string.IsNullOrWhiteSpace(actor.Name) ? string.Empty : $"  ·  {actor.Name}")}", "Player");
        var actorLabelWidth = Math.Max(1, ImGui.GetContentRegionAvail().X);
        if (ImGui.Selectable($"{header}##actor-label", false, ImGuiSelectableFlags.None, new Vector2(actorLabelWidth, ImGui.GetFrameHeight()))) _expansion.Toggle(actorId, expanded, filteredView);
        if (!expanded)
            return;

        using var table = ImRaii.Table("##resource-table", 3, ResourceTableFlags);
        if (!table.Success)
            return;
        ImGui.TableSetupColumn("Slot / Item", ImGuiTableColumnFlags.WidthStretch, .36f);
        ImGui.TableSetupColumn("Mod / Resource Path", ImGuiTableColumnFlags.WidthStretch, .64f);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, Theme.Scaled(58));
        ImGui.TableHeadersRow();
        DrawSection(actor, ResourceSection.CharacterFeatures, "Character features", ExpansionState.SectionKey(actorId, ResourceSection.CharacterFeatures));
        DrawSection(actor, ResourceSection.Gear, "Gear", ExpansionState.SectionKey(actorId, ResourceSection.Gear));
        DrawSection(actor, ResourceSection.Other, "Other", ExpansionState.SectionKey(actorId, ResourceSection.Other));
    }

    private void DrawModResourceRows(ActorView actor)
    {
        // BuildModView sorts the roots by game path once, so this only filters them.
        using var table = ImRaii.Table("##mod-resource-table", 4, ResourceTableFlags);
        if (!table.Success)
            return;

        ImGui.TableSetupColumn("Resource", ImGuiTableColumnFlags.WidthStretch, .36f);
        ImGui.TableSetupColumn("Mod / Resource Path", ImGuiTableColumnFlags.WidthStretch, .42f);
        ImGui.TableSetupColumn("Mod Options", ImGuiTableColumnFlags.WidthStretch, .22f);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, Theme.Scaled(58));
        ImGui.TableHeadersRow();

        if (_kinds.IsFlat)
        {
            var row = 0;
            foreach (var root in actor.Roots)
            {
                if (!_kinds.AdmitsSubtree(root))
                    continue;
                foreach (var resource in root.Flattened)
                    if (_kinds.Admits(resource))
                        DrawFlatNode(actor, root, resource, $"mod:flat:{row++}", true);
            }
        }
        else
        {
            var index = 0;
            foreach (var root in actor.Roots)
                if (_kinds.AdmitsSubtree(root))
                    DrawNode(actor, root, $"mod:{index++}", 0, false, false, true);
        }
    }

    private void DrawSection(ActorView actor, ResourceSection section, string label, string key)
    {
        var searchActive = actor.Entity is not null && _search.Active;
        var filterBySearch = searchActive && !_search.ActorIdentityMatches(actor);
        var ordered = actor.RootsInSection(section)
            .Where(x => _kinds.AdmitsSubtree(x) && (!filterBySearch || _search.Matches(x)))
            .ToList();
        if (ordered.Count == 0) return;
        using var id = ImRaii.PushId(SafeId(key));
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        var filteredView = _kinds.IsFlat;
        var expanded = _expansion.IsExpanded(key, filteredView, !filteredView && _search.Active);
        if (ImGui.Button(expanded ? "▼##section-toggle" : "▶##section-toggle", new Vector2(Theme.ArrowWidth, ImGui.GetFrameHeight()))) _expansion.Toggle(key, expanded, filteredView);
        ImGui.SameLine(0, Theme.Scaled(4));
        if (ImGui.Selectable($"{label}##section-label", false, ImGuiSelectableFlags.SpanAllColumns, new Vector2(0, ImGui.GetFrameHeight()))) _expansion.Toggle(key, expanded, filteredView);
        if (expanded)
        {
            if (filteredView)
                DrawFlatSection(actor, ordered, key, filterBySearch);
            else
                for (var i = 0; i < ordered.Count; i++) DrawNode(actor, ordered[i], $"{key}:{i}", 3, filterBySearch, searchActive);
        }
    }

    private void DrawFlatSection(ActorView actor, IReadOnlyList<ResourceView> roots, string key, bool filterBySearch)
    {
        var row = 0;
        foreach (var root in roots)
        {
            foreach (var resource in root.Flattened)
            {
                if (_kinds.Admits(resource) && (!filterBySearch || _search.Matches(resource)))
                    DrawFlatNode(actor, root, resource, $"{key}:flat:{row++}");
            }
        }
    }

    private void DrawFlatNode(ActorView actor, ResourceView item, ResourceView resource, string scope, bool showOptionMapping = false)
    {
        using var id = ImRaii.PushId(SafeId(scope));
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Theme.TreeIndent);
        DrawSlotIcon(Safe(item.Slot, ResourceViews.KindLabel(item.Type)), item.Icon, item.Section);
        ImGui.SameLine(0, Theme.Scaled(6));
        ImGui.TextUnformatted(Safe(item.DisplayName, "Unnamed resource"));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Safe(item.Slot, item.Type));

        ImGui.TableSetColumnIndex(1);
        DrawResolvedPath(resource, Safe(resource.SourceLabel), Safe(resource.ActualPath), Safe(resource.GamePath));

        if (showOptionMapping)
        {
            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(Safe(resource.OptionMapping, "Unmapped"));
        }

        ImGui.TableSetColumnIndex(showOptionMapping ? 3 : 2);
        if (resource.IsModel && ResourceViews.IsSafeModel(resource))
        {
            if (ImGui.SmallButton("Edit##flat-node-action")) TryEditNode(resource, actor);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Edit this model in Blender");
        }
        else DrawTextureAction(resource, actor);
    }

    private void DrawNode(ActorView actor, ResourceView node, string scope, int depth, bool filterBySearch, bool autoExpandSearch, bool showOptionMapping = false)
    {
        var type = Safe(node.Type, "Resource");
        var gamePath = Safe(node.GamePath);
        var actualPath = Safe(node.ActualPath);
        var source = Safe(node.SourceLabel, "Source unavailable");
        var key = ExpansionState.NodeKey(scope, node);
        using var id = ImRaii.PushId(key);
        var presentation = Safe(node.Slot, ResourceViews.KindLabel(type));
        // Only descend into children IE can actually edit (or that themselves
        // contain one) - resources like animations, skeletons, or VFX have
        // nothing to open here, so they're dropped from the tree entirely.
        var hasChildren = _kinds.AdmitsChild(node);
        var expanded = _expansion.IsExpanded(key, false, autoExpandSearch);
        var arrow = hasChildren ? (expanded ? "▼" : "▶") : "  ";

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        // ImGui's persistent Indent state is reset while changing table rows/cells.
        // Offset this cell's cursor directly so every tree level moves right.
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, depth - 2) * Theme.TreeIndent);
        if (ImGui.Button(Safe($"{arrow}##expand:{key}", "  ##expand"), new Vector2(Theme.ArrowWidth, ImGui.GetFrameHeight())))
            if (hasChildren) _expansion.Toggle(key, expanded, false);
        if (ImGui.IsItemHovered() && hasChildren) ImGui.SetTooltip(expanded ? "Collapse" : "Expand");
        ImGui.SameLine(0, Theme.Scaled(4));
        DrawSlotIcon(presentation, node.Icon, node.Section);
        ImGui.SameLine(0, Theme.Scaled(6));
        var itemName = Safe(node.DisplayName, "Unnamed resource");
        var hovered = ImGui.Selectable($"{itemName}##label:{key}", false, ImGuiSelectableFlags.None, new Vector2(0, ImGui.GetFrameHeight()));
        if (hovered && hasChildren) _expansion.Toggle(key, expanded, false);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{presentation}\nGame path: {(gamePath.Length == 0 ? "(none)" : gamePath)}");
        ImGui.TableSetColumnIndex(1);
        DrawResolvedPath(node, source, actualPath, gamePath);

        if (showOptionMapping)
        {
            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(Safe(node.OptionMapping, "Unmapped"));
        }

        ImGui.TableSetColumnIndex(showOptionMapping ? 3 : 2);
        if (node.IsModel && ResourceViews.IsSafeModel(node))
        {
            if (ImGui.SmallButton("Edit##node-action")) TryEditNode(node, actor);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Edit this model in Blender");
        }
        else DrawTextureAction(node, actor);
        if (expanded && hasChildren)
        {
            // Child IDs count every type-matching child, including ones the search hides.
            var childIndex = 0;
            foreach (var child in node.Children)
            {
                if (!_kinds.AdmitsSubtree(child))
                    continue;
                var i = childIndex++;
                if (!filterBySearch || _search.Matches(child))
                    DrawNode(actor, child, $"{scope}:{i}", depth + 1, filterBySearch, autoExpandSearch, showOptionMapping);
            }
        }
    }

    private void DrawResolvedPath(ResourceView node, string source, string actualPath, string gamePath)
    {
        if (!string.IsNullOrWhiteSpace(node.SourceModName))
        {
            ImGui.TextColored(Theme.ModSource, $"[{node.SourceModName}]");
            ImGui.SameLine(0, Theme.Scaled(5));
        }
        else if (node.SourceState == ResourceSourceState.GameData)
        {
            ImGui.TextColored(Theme.GameSource, "[Game Data]");
            ImGui.SameLine(0, Theme.Scaled(5));
        }
        else if (!string.IsNullOrWhiteSpace(source))
        {
            ImGui.TextColored(Theme.Hint, $"[{source}]");
            ImGui.SameLine(0, Theme.Scaled(5));
        }

        var displayPath = Safe(node.SourceRelativePath, Safe(gamePath, actualPath));
        ImGui.TextUnformatted(displayPath);
        ShowPathTooltip(ImGui.IsItemHovered(), actualPath);
    }

    private void DrawSlotIcon(string slot, string resourceIcon, string section)
    {
        IDalamudTextureWrap? icon;
        var key = string.Equals(section, ResourceSection.CharacterFeatures.ToString(), StringComparison.OrdinalIgnoreCase)
            ? "Unknown"
            : ResourceViews.NormalizeSlotIcon(slot, resourceIcon);
        lock (_stateLock)
            _slotIcons.TryGetValue(key, out icon);

        if (icon is null)
        {
            ImGui.Dummy(new Vector2(Theme.IconSize));
            return;
        }

        ImGui.Image(icon.Handle, new Vector2(Theme.IconSize));
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(slot);
    }

    private void DrawResourceTypeFilters(IReadOnlyList<ActorView> actors)
    {
        var counts = _counter.Count(actors, ResourceTypeFilterCountKinds);
        ImGui.Spacing();
        ImGui.TextColored(Theme.Muted, "Filter"); ImGui.SameLine(0, Theme.Scaled(8));
        for (var i = 0; i < ResourceTypeFilterGroups.Length; i++)
        {
            var group = ResourceTypeFilterGroups[i];
            var kind = ResourceTypeFilterKinds[i];
            using var id = ImRaii.PushId($"resource-type-filter:{group}");
            var selected = _kinds.Selected == kind;
            using var colour = ImRaii.PushColor(ImGuiCol.Button, Theme.Selection, selected);
            if (ImGui.SmallButton($"{group}  {counts[i]}")) _kinds.Set(kind);
            if (i < ResourceTypeFilterGroups.Length - 1) ImGui.SameLine(0, Theme.Scaled(6));
        }
        ImGui.NewLine();
    }

    /// <summary> The kinds each filter button counts: the tree view counts every editable row. </summary>
    private static readonly ResourceKinds[] ResourceTypeFilterCountKinds = [ResourceKinds.Editable, ResourceKinds.Model, ResourceKinds.Texture];

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

    /// <summary> The On Screen view model, rebuilt only when the snapshot or the vanilla toggle changes. </summary>
    private List<ActorView> ReadActors()
    {
        var items = _onScreen.Items;
        var includeVanilla = _config.IncludeVanillaResources;
        if (ReferenceEquals(items, _actorSnapshot) && includeVanilla == _actorSnapshotIncludesVanilla)
            return _actors;

        var result = new List<ActorView>(items.Count);
        foreach (var entity in items)
        {
            var parsed = OnScreenService.ProjectVisibleResourceNodes(
                    entity.ResourceRoots,
                    includeVanilla)
                .Select(ResourceViews.FromNode)
                .ToList();
            result.Add(new ActorView(entity, entity.PresentationCategory.ToString(), Safe(entity.Name), parsed, entity.ObjectIndex));
        }

        _actorSnapshot = items;
        _actorSnapshotIncludesVanilla = includeVanilla;
        _actors = result;
        _search.Invalidate();
        return result;
    }

    private bool ActorMatches(ActorView actor)
        => actor.Roots.Any(_kinds.AdmitsSubtree)
        && (actor.Entity is null || !_search.Active || _search.ActorIdentityMatches(actor) || actor.Roots.Any(_search.Matches));

    private bool LoadSlotIcons(IUiBuilder uiBuilder, ITextureProvider textureProvider)
    {
        using var armoury = uiBuilder.LoadUld("ui/uld/ArmouryBoard.uld");
        if (!armoury.Valid)
            return false;

        var icons = new Dictionary<string, IDalamudTextureWrap>(StringComparer.OrdinalIgnoreCase);
        try
        {
            void Add(string slot, int part)
            {
                var texture = armoury.LoadTexturePart("ui/uld/ArmouryBoard_hr1.tex", part);
                if (texture is not null)
                    icons.Add(slot, texture);
            }

            Add("Mainhand", 0);
            Add("Head", 1);
            Add("Body", 2);
            Add("Hands", 3);
            Add("Legs", 5);
            Add("Feet", 6);
            Add("Offhand", 7);
            Add("Ears", 8);
            Add("Neck", 9);
            Add("Wrists", 10);
            Add("Finger", 11);

            var unknown = LoadUnknownSlotIcon(textureProvider);
            if (unknown is not null)
                icons.Add("Unknown", unknown);

            lock (_stateLock)
                _slotIcons = icons;
            return true;
        }
        catch (Exception e)
        {
            foreach (var icon in icons.Values)
                icon.Dispose();
            _log.Debug($"Could not load armoury slot icons: {e.Message}");
            return false;
        }
    }

    private IDalamudTextureWrap? LoadUnknownSlotIcon(ITextureProvider textureProvider)
    {
        var texture = _data.GetFile<Lumina.Data.Files.TexFile>("ui/uld/levelup2_hr1.tex");
        if (texture is null)
            return null;

        // This is the same square crop Penumbra uses for its unknown-slot '?' icon.
        var source = texture.GetRgbaImageData();
        var size = texture.Header.Height;
        var bytes = new byte[size * size * 4];
        var horizontalOffset = 2 * (texture.Header.Height - texture.Header.Width);
        for (var y = 0; y < size; ++y)
            source.AsSpan(4 * y * texture.Header.Width, 4 * texture.Header.Width)
                .CopyTo(bytes.AsSpan(4 * y * size + horizontalOffset));

        return textureProvider.CreateFromRaw(RawImageSpecification.Rgba32(size, size), bytes, "InstantEdit.UnknownSlotIcon");
    }

    private void DrawOpaqueRow()
    {
        var p = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddRectFilled(p, p + new Vector2(ImGui.GetContentRegionAvail().X, ImGui.GetFrameHeight()), ImGui.GetColorU32(Theme.RowAlt), Theme.Scaled(2));
    }

    private static void ShowPathTooltip(bool hovered, string path)
    {
        if (!hovered) return;
        var safePath = Safe(path);
        ImGui.SetTooltip(string.IsNullOrWhiteSpace(safePath) ? "No resolved path" : $"{safePath}\nRight-click to copy");
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) ImGui.SetClipboardText(safePath);
    }
}
