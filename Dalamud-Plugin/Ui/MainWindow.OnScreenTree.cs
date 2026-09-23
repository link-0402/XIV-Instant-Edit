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

    // View-model caches: drawing must not rebuild, re-classify, or re-search the resource
    // tree every frame. They are replaced when the snapshot, filter, or search changes.
    private IReadOnlyList<OnScreenObject>? _actorSnapshot;
    private bool _actorSnapshotIncludesVanilla;
    private List<ActorView> _actors = [];
    private ActorView[] _resourceTypeCountActors = [];
    private readonly int[] _resourceTypeCounts = new int[ResourceTypeFilterGroups.Length];
    private readonly Dictionary<ResourceView, bool> _searchMatches = new(ReferenceEqualityComparer.Instance);
    private string _searchMatchesFilter = string.Empty;

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

        var actorId = SafeId($"actor:{actor.Entity.Address:X}:{actor.Entity.ObjectIndex}");
        using var id = ImRaii.PushId(actorId);
        DrawOpaqueRow();
        var filteredView = IsFilteredResourceView;
        var expanded = filteredView
            ? !_collapsedFiltered.Contains(actorId)
            : SearchActive || _expanded.Contains(actorId);
        if (ImGui.Button(expanded ? "▼##actor-toggle" : "▶##actor-toggle", new Vector2(Theme.ArrowWidth, ImGui.GetFrameHeight()))) ToggleExpanded(actorId, expanded, filteredView);
        ImGui.SameLine(0, Theme.Scaled(4)); var header = Safe($"{actor.Category}{(string.IsNullOrWhiteSpace(actor.Name) ? string.Empty : $"  ·  {actor.Name}")}", "Player");
        var actorLabelWidth = Math.Max(1, ImGui.GetContentRegionAvail().X);
        if (ImGui.Selectable($"{header}##actor-label", false, ImGuiSelectableFlags.None, new Vector2(actorLabelWidth, ImGui.GetFrameHeight()))) ToggleExpanded(actorId, expanded, filteredView);
        if (!expanded)
            return;

        using var table = ImRaii.Table("##resource-table", 3, ResourceTableFlags);
        if (!table.Success)
            return;
        ImGui.TableSetupColumn("Slot / Item", ImGuiTableColumnFlags.WidthStretch, .36f);
        ImGui.TableSetupColumn("Mod / Resource Path", ImGuiTableColumnFlags.WidthStretch, .64f);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, Theme.Scaled(58));
        ImGui.TableHeadersRow();
        DrawSection(actor, ResourceSection.CharacterFeatures, "Character features", actorId + ":features");
        DrawSection(actor, ResourceSection.Gear, "Gear", actorId + ":gear");
        DrawSection(actor, ResourceSection.Other, "Other", actorId + ":other");
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

        if (IsFilteredResourceView)
        {
            var row = 0;
            foreach (var root in actor.Roots)
            {
                if (!HasResourceTypeMatch(root))
                    continue;
                foreach (var resource in root.Flattened)
                    if (MatchesSelectedResourceType(resource))
                        DrawFlatNode(actor, root, resource, $"mod:flat:{row++}", true);
            }
        }
        else
        {
            var index = 0;
            foreach (var root in actor.Roots)
                if (HasResourceTypeMatch(root))
                    DrawNode(actor, root, $"mod:{index++}", 0, false, false, true);
        }
    }

    private void DrawSection(ActorView actor, ResourceSection section, string label, string key)
    {
        var sectionValue = section.ToString();
        var searchActive = actor.Entity is not null && SearchActive;
        var filterBySearch = searchActive && !ActorIdentityMatches(actor);
        var nodes = actor.Roots.Where(x => string.Equals(Safe(x.Section), sectionValue, StringComparison.OrdinalIgnoreCase) && HasResourceTypeMatch(x) && (!filterBySearch || Matches(x)));
        nodes = nodes.OrderBy(x => x.Order);
        var ordered = nodes.ToList();
        if (ordered.Count == 0) return;
        using var id = ImRaii.PushId(SafeId(key));
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        var filteredView = IsFilteredResourceView;
        var expanded = filteredView
            ? !_collapsedFiltered.Contains(key)
            : SearchActive || _expanded.Contains(key);
        if (ImGui.Button(expanded ? "▼##section-toggle" : "▶##section-toggle", new Vector2(Theme.ArrowWidth, ImGui.GetFrameHeight()))) ToggleExpanded(key, expanded, filteredView);
        ImGui.SameLine(0, Theme.Scaled(4));
        if (ImGui.Selectable($"{label}##section-label", false, ImGuiSelectableFlags.SpanAllColumns, new Vector2(0, ImGui.GetFrameHeight()))) ToggleExpanded(key, expanded, filteredView);
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
                if (MatchesSelectedResourceType(resource) && (!filterBySearch || Matches(resource)))
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
        DrawSlotIcon(Safe(item.Slot, KindLabel(item.Type)), item.Icon, item.Section);
        ImGui.SameLine(0, Theme.Scaled(6));
        var itemName = Safe(DisplayName(item.Name, item.ActualPath), "Unnamed resource");
        ImGui.TextUnformatted(itemName);
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
        if (IsModel(resource) && IsSafeModel(resource))
        {
            if (ImGui.SmallButton("Edit##flat-node-action")) TryEditNode(resource, actor);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Edit this model in Blender");
        }
        else DrawTextureAction(resource, actor);
    }

    private void DrawNode(ActorView actor, ResourceView node, string scope, int depth, bool filterBySearch, bool autoExpandSearch, bool showOptionMapping = false)
    {
        var type = Safe(node.Type, "Resource");
        var name = Safe(node.Name, "Unnamed resource");
        var gamePath = Safe(node.GamePath);
        var actualPath = Safe(node.ActualPath);
        var source = Safe(node.SourceLabel, "Source unavailable");
        var key = SafeId($"{scope}:{type}:{name}:{gamePath}:{actualPath}");
        using var id = ImRaii.PushId(key);
        var presentation = Safe(node.Slot, KindLabel(type));
        // Only descend into children IE can actually edit (or that themselves
        // contain one) - resources like animations, skeletons, or VFX have
        // nothing to open here, so they're dropped from the tree entirely.
        var hasChildren = HasChildResourceTypeMatch(node);
        var model = IsModel(node);
        var expanded = autoExpandSearch || _expanded.Contains(key);
        var arrow = hasChildren ? (expanded ? "▼" : "▶") : "  ";

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        // ImGui's persistent Indent state is reset while changing table rows/cells.
        // Offset this cell's cursor directly so every tree level moves right.
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, depth - 2) * Theme.TreeIndent);
        if (ImGui.Button(Safe($"{arrow}##expand:{key}", "  ##expand"), new Vector2(Theme.ArrowWidth, ImGui.GetFrameHeight())))
            if (hasChildren) { if (expanded) _expanded.Remove(key); else _expanded.Add(key); }
        if (ImGui.IsItemHovered() && hasChildren) ImGui.SetTooltip(expanded ? "Collapse" : "Expand");
        ImGui.SameLine(0, Theme.Scaled(4));
        DrawSlotIcon(presentation, node.Icon, node.Section);
        ImGui.SameLine(0, Theme.Scaled(6));
        var itemName = Safe(DisplayName(name, actualPath), "Unnamed resource");
        var hovered = ImGui.Selectable($"{itemName}##label:{key}", false, ImGuiSelectableFlags.None, new Vector2(0, ImGui.GetFrameHeight()));
        if (hovered && hasChildren) { if (expanded) _expanded.Remove(key); else _expanded.Add(key); }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{presentation}\nGame path: {(gamePath.Length == 0 ? "(none)" : gamePath)}");
        ImGui.TableSetColumnIndex(1);
        DrawResolvedPath(node, source, actualPath, gamePath);

        if (showOptionMapping)
        {
            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(Safe(node.OptionMapping, "Unmapped"));
        }

        ImGui.TableSetColumnIndex(showOptionMapping ? 3 : 2);
        if (model && IsSafeModel(node))
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
                if (!HasResourceTypeMatch(child))
                    continue;
                var i = childIndex++;
                if (!filterBySearch || Matches(child))
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
            : NormalizeSlotIcon(slot, resourceIcon);
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
        var counts = ResourceTypeCounts(actors);
        ImGui.Spacing();
        ImGui.TextColored(Theme.Muted, "Filter"); ImGui.SameLine(0, Theme.Scaled(8));
        for (var i = 0; i < ResourceTypeFilterGroups.Length; i++)
        {
            var group = ResourceTypeFilterGroups[i];
            var filter = ResourceTypeFilterFor(group);
            using var id = ImRaii.PushId($"resource-type-filter:{group}");
            var selected = _resourceTypeFilter == filter;
            using var colour = ImRaii.PushColor(ImGuiCol.Button, Theme.Selection, selected);
            if (ImGui.SmallButton($"{group}  {counts[i]}")) _resourceTypeFilter = filter;
            if (i < ResourceTypeFilterGroups.Length - 1) ImGui.SameLine(0, Theme.Scaled(6));
        }
        ImGui.NewLine();
    }

    private static string ResourceTypeFilterFor(string group)
        => group == "Tree Structure" ? string.Empty : group;

    /// <summary> Filter-button counts, recomputed only when the displayed actors change. </summary>
    private int[] ResourceTypeCounts(IReadOnlyList<ActorView> actors)
    {
        var unchanged = actors.Count == _resourceTypeCountActors.Length;
        for (var i = 0; unchanged && i < actors.Count; i++)
            unchanged = ReferenceEquals(actors[i], _resourceTypeCountActors[i]);
        if (unchanged)
            return _resourceTypeCounts;

        for (var i = 0; i < ResourceTypeFilterGroups.Length; i++)
        {
            var filter = ResourceTypeFilterFor(ResourceTypeFilterGroups[i]);
            _resourceTypeCounts[i] = actors.SelectMany(x => x.Roots).SelectMany(root => root.Flattened)
                .Count(node => MatchesResourceType(node, filter));
        }

        _resourceTypeCountActors = actors.ToArray();
        return _resourceTypeCounts;
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

    private bool ModMatches(PenumbraMod mod)
        => string.IsNullOrWhiteSpace(_modFilter)
            || mod.Name.Contains(_modFilter, StringComparison.OrdinalIgnoreCase)
            || mod.Directory.Contains(_modFilter, StringComparison.OrdinalIgnoreCase);

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
            view = BuildModView(snapshot, importObjectIndex);

        lock (_stateLock)
        {
            if (!ReferenceEquals(_modLoadCts, owner))
                return;
            _loadedModView = view;
            _modLoading = false;
            _modLoadFailed = snapshot is null && !owner.IsCancellationRequested;
        }
    }

    private static ActorView BuildModView(PenumbraModSnapshot snapshot, int importObjectIndex)
    {
        var roots = snapshot.Resources
            .Select(resource => new ResourceView(
                ResourceType(resource.GamePath),
                string.Empty,
                Path.GetFileName(resource.GamePath),
                resource.GamePath,
                resource.ActualPath,
                $"Loaded from: {snapshot.Name}",
                snapshot.Name,
                snapshot.Directory,
                snapshot.RootPath,
                resource.RelativePath,
                snapshot.StableId,
                ResourceSourceState.LoadedMod,
                ResourceSection.Other.ToString(),
                ResourceType(resource.GamePath),
                int.MaxValue,
                resource.OptionMapping,
                resource.OptionMemberships,
                new List<ResourceView>()))
            .OrderBy(resource => resource.GamePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ActorView(
            null,
            "Mod",
            snapshot.Name,
            roots,
            importObjectIndex);
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

    private static string ResourceType(string gamePath)
        => Path.GetExtension(gamePath).ToLowerInvariant() switch
        {
            ".mdl" => "Model",
            ".tex" or ".atex" => "Texture",
            ".mtrl" => "Material",
            _ => "Resource",
        };

    /// <summary> The root followed by its descendants, depth first. </summary>
    private static IEnumerable<ResourceView> Flatten(ResourceView root)
    {
        var pending = new Stack<ResourceView>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            yield return node;
            for (var i = node.Children.Count - 1; i >= 0; i--)
                pending.Push(node.Children[i]);
        }
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
                .Select(ReadNode)
                .ToList();
            result.Add(new ActorView(entity, entity.PresentationCategory.ToString(), Safe(entity.Name), parsed, entity.ObjectIndex));
        }

        _actorSnapshot = items;
        _actorSnapshotIncludesVanilla = includeVanilla;
        _actors = result;
        _searchMatches.Clear();
        return result;
    }

    private bool SearchActive => !string.IsNullOrWhiteSpace(_filter);

    private bool ActorIdentityMatches(ActorView actor)
        => actor.Category.Contains(_filter, StringComparison.OrdinalIgnoreCase) || actor.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase);

    private bool ActorMatches(ActorView actor)
        => actor.Roots.Any(HasResourceTypeMatch)
        && (actor.Entity is null || !SearchActive || ActorIdentityMatches(actor) || actor.Roots.Any(Matches));

    private bool IsFilteredResourceView => !string.IsNullOrWhiteSpace(_resourceTypeFilter);

    private bool HasResourceTypeMatch(ResourceView node)
        => ResourceKindClassifier.ForFilter(_resourceTypeFilter) is not { } kinds || (node.SubtreeKinds & kinds) != 0;

    private bool HasChildResourceTypeMatch(ResourceView node)
        => ResourceKindClassifier.ForFilter(_resourceTypeFilter) is { } kinds
            ? (node.DescendantKinds & kinds) != 0
            : node.Children.Count > 0;

    private bool MatchesSelectedResourceType(ResourceView node)
        => MatchesResourceType(node, _resourceTypeFilter);

    private static bool MatchesResourceType(ResourceView node, string filter)
        => ResourceKindClassifier.ForFilter(filter) is not { } kinds || (node.Kinds & kinds) != 0;

    private void ToggleExpanded(string key, bool expanded, bool defaultExpanded)
    {
        var set = defaultExpanded ? _collapsedFiltered : _expanded;
        if (expanded)
        {
            if (defaultExpanded) set.Add(key); else set.Remove(key);
        }
        else
        {
            if (defaultExpanded) set.Remove(key); else set.Add(key);
        }
    }

    private static ResourceView ReadNode(ResourceNode node)
    {
        var children = node.Children
            .Select(ReadNode)
            .ToList();
        return new ResourceView(
            node.Type,
            node.Icon,
            node.Name,
            node.GamePath,
            node.ActualPath,
            node.SourceLabel,
            node.SourceModName ?? string.Empty,
            node.SourceModDirectory ?? string.Empty,
            node.SourceModRootPath ?? string.Empty,
            node.SourceRelativePath ?? string.Empty,
            node.SourceModStableId,
            node.SourceState,
            node.ResourceSection.ToString(),
            node.SlotLabel,
            node.SortOrder,
            string.Empty,
            Array.Empty<string>(),
            children);
    }

    private static string KindLabel(string type) => string.IsNullOrWhiteSpace(type) ? "Resource" : type;
    private static string DisplayName(string name, string actualPath)
    {
        if (!string.IsNullOrWhiteSpace(name)) return name;
        return Safe(Path.GetFileName(actualPath), "Unnamed resource");
    }
    private static bool IsModel(ResourceView node) => node.Type.Contains("model", StringComparison.OrdinalIgnoreCase) || node.GamePath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase) || node.ActualPath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase);
    private static bool IsSafeModel(ResourceView node)
    {
        if (!IsModel(node) || !PenumbraService.IsSafeGamePath(node.GamePath) ||
            !node.GamePath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase) ||
            !node.ActualPath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
            return false;
        return node.SourceState switch
        {
            ResourceSourceState.LoadedMod => Path.IsPathRooted(node.ActualPath) &&
                                             !string.IsNullOrWhiteSpace(node.SourceModDirectory),
            ResourceSourceState.GameData => !Path.IsPathRooted(node.ActualPath) &&
                                            PenumbraService.IsSafeGamePath(node.ActualPath),
            _ => false,
        };
    }
    /// <summary> Search match for a node or any descendant, memoized per search text and snapshot. </summary>
    private bool Matches(ResourceView node)
    {
        var filter = Safe(_filter);
        if (string.IsNullOrWhiteSpace(filter))
            return true;
        if (!string.Equals(filter, _searchMatchesFilter, StringComparison.Ordinal))
        {
            _searchMatches.Clear();
            _searchMatchesFilter = filter;
        }

        if (_searchMatches.TryGetValue(node, out var matches))
            return matches;

        matches = Safe(node.Name).Contains(filter, StringComparison.OrdinalIgnoreCase)
            || Safe(node.Type).Contains(filter, StringComparison.OrdinalIgnoreCase)
            || Safe(node.SourceLabel).Contains(filter, StringComparison.OrdinalIgnoreCase)
            || Safe(node.SourceModName).Contains(filter, StringComparison.OrdinalIgnoreCase)
            || Safe(node.SourceRelativePath).Contains(filter, StringComparison.OrdinalIgnoreCase)
            || Safe(node.GamePath).Contains(filter, StringComparison.OrdinalIgnoreCase)
            || Safe(node.ActualPath).Contains(filter, StringComparison.OrdinalIgnoreCase)
            || node.Children.Any(Matches);
        _searchMatches[node] = matches;
        return matches;
    }
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

    private static string NormalizeSlotIcon(string slot, string resourceIcon)
    {
        var value = $"{slot} {resourceIcon}".ToLowerInvariant();
        if (value.Contains("mainhand") || value.Contains("weapon")) return "Mainhand";
        if (value.Contains("offhand")) return "Offhand";
        if (value.Contains("head")) return "Head";
        if (value.Contains("body")) return "Body";
        if (value.Contains("hand")) return "Hands";
        if (value.Contains("leg")) return "Legs";
        if (value.Contains("feet") || value.Contains("foot")) return "Feet";
        if (value.Contains("earring") || value.Contains("ears")) return "Ears";
        if (value.Contains("neck")) return "Neck";
        if (value.Contains("bracelet") || value.Contains("wrist")) return "Wrists";
        if (value.Contains("ring") || value.Contains("finger")) return "Finger";
        return string.Empty;
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
