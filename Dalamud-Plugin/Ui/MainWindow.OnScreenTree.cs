using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using InstantEdit.Models;
using InstantEdit.Services;
using Lumina.Data;

namespace InstantEdit.Ui;

public sealed partial class MainWindow
{
    private static void Status(string name, bool good, string value)
        => Status(name, good ? BlenderConnectionState.Online : BlenderConnectionState.Offline, value);

    private static void Status(string name, BlenderConnectionState state, string value)
    {
        var color = state switch
        {
            BlenderConnectionState.Online => new Vector4(.3f, .78f, .5f, 1),
            BlenderConnectionState.VersionMismatch => new Vector4(1f, .65f, .1f, 1),
            _ => new Vector4(.9f, .45f, .32f, 1),
        };
        ImGui.TextColored(color, "●"); ImGui.SameLine(0, 3); ImGui.TextColored(new Vector4(.7f, .72f, .78f, 1), $"{name}: {value}");
    }

    private void DrawResources(IReadOnlyList<ActorView> actors, string? emptyMessage = null)
    {
        actors = actors.Where(ActorMatches).ToList();
        var viewportHeight = Math.Max(1, ImGui.GetContentRegionAvail().Y);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(.075f, .085f, .105f, 1));
        if (!ImGui.BeginChild("##resource-browser", new Vector2(0, viewportHeight), true)) { ImGui.EndChild(); ImGui.PopStyleColor(); return; }
        if (emptyMessage is null && _onScreen.IsRefreshing && actors.Count == 0) ImGui.TextColored(new Vector4(.65f, .68f, .75f, 1), "Refreshing resources…");
        else if (actors.Count == 0) ImGui.TextColored(new Vector4(.65f, .68f, .75f, 1), emptyMessage ?? "No snapshot loaded. Use Refresh character list to collect on-screen resources.");
        else foreach (var actor in actors) DrawActor(actor);
        ImGui.EndChild();
        ImGui.PopStyleColor();
    }

    private void DrawActor(ActorView actor)
    {
        if (actor.Entity is null)
        {
            DrawModResourceRows(actor);
            return;
        }

        var actorId = SafeId($"actor:{actor.Entity.Address:X}:{actor.Entity.ObjectIndex}");
        ImGui.PushID(actorId); DrawOpaqueRow();
        var filteredView = IsFilteredResourceView;
        var expanded = filteredView
            ? !_collapsedFiltered.Contains(actorId)
            : SearchActive || _expanded.Contains(actorId);
        if (ImGui.Button(expanded ? "▼##actor-toggle" : "▶##actor-toggle", new Vector2(22, ImGui.GetFrameHeight()))) ToggleExpanded(actorId, expanded, filteredView);
        ImGui.SameLine(0, 4); var header = Safe($"{actor.Category}{(string.IsNullOrWhiteSpace(actor.Name) ? string.Empty : $"  ·  {actor.Name}")}", "Player");
        var actorLabelWidth = Math.Max(1, ImGui.GetContentRegionAvail().X);
        if (ImGui.Selectable($"{header}##actor-label", false, ImGuiSelectableFlags.None, new Vector2(actorLabelWidth, ImGui.GetFrameHeight()))) ToggleExpanded(actorId, expanded, filteredView);
        if (expanded)
        {
            var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.BordersOuter |
                        ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp;
            if (ImGui.BeginTable("##resource-table", 3, flags))
            {
                ImGui.TableSetupColumn("Slot / Item", ImGuiTableColumnFlags.WidthStretch, .36f);
                ImGui.TableSetupColumn("Mod / Resource Path", ImGuiTableColumnFlags.WidthStretch, .64f);
                ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 58);
                ImGui.TableHeadersRow();
                DrawSection(actor, ResourceSection.CharacterFeatures, "Character features", actorId + ":features");
                DrawSection(actor, ResourceSection.Gear, "Gear", actorId + ":gear");
                DrawSection(actor, ResourceSection.Other, "Other", actorId + ":other");
                ImGui.EndTable();
            }
        }
        ImGui.PopID();
    }

    private void DrawModResourceRows(ActorView actor)
    {
        var resources = actor.Roots
            .Where(HasResourceTypeMatch)
            .OrderBy(resource => resource.GamePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.BordersOuter |
                    ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("##mod-resource-table", 4, flags))
            return;

        ImGui.TableSetupColumn("Resource", ImGuiTableColumnFlags.WidthStretch, .36f);
        ImGui.TableSetupColumn("Mod / Resource Path", ImGuiTableColumnFlags.WidthStretch, .42f);
        ImGui.TableSetupColumn("Mod Options", ImGuiTableColumnFlags.WidthStretch, .22f);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 58);
        ImGui.TableHeadersRow();

        if (IsFilteredResourceView)
        {
            var row = 0;
            foreach (var root in resources)
                foreach (var resource in Flatten(root).Where(MatchesSelectedResourceType))
                    DrawFlatNode(actor, root, resource, $"mod:flat:{row++}", true);
        }
        else
        {
            for (var i = 0; i < resources.Count; i++)
                DrawNode(actor, resources[i], $"mod:{i}", 0, false, false, true);
        }

        ImGui.EndTable();
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
        ImGui.PushID(SafeId(key));
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        var filteredView = IsFilteredResourceView;
        var expanded = filteredView
            ? !_collapsedFiltered.Contains(key)
            : SearchActive || _expanded.Contains(key);
        if (ImGui.Button(expanded ? "▼##section-toggle" : "▶##section-toggle", new Vector2(22, ImGui.GetFrameHeight()))) ToggleExpanded(key, expanded, filteredView);
        ImGui.SameLine(0, 4);
        if (ImGui.Selectable($"{label}##section-label", false, ImGuiSelectableFlags.SpanAllColumns, new Vector2(0, ImGui.GetFrameHeight()))) ToggleExpanded(key, expanded, filteredView);
        if (expanded)
        {
            if (filteredView)
                DrawFlatSection(actor, ordered, key, filterBySearch);
            else
                for (var i = 0; i < ordered.Count; i++) DrawNode(actor, ordered[i], $"{key}:{i}", 3, filterBySearch, searchActive);
        }
        ImGui.PopID();
    }

    private void DrawFlatSection(ActorView actor, IReadOnlyList<ResourceView> roots, string key, bool filterBySearch)
    {
        var row = 0;
        foreach (var root in roots)
        {
            foreach (var resource in Flatten(root).Where(x => MatchesSelectedResourceType(x) && (!filterBySearch || Matches(x))))
                DrawFlatNode(actor, root, resource, $"{key}:flat:{row++}");
        }
    }

    private void DrawFlatNode(ActorView actor, ResourceView item, ResourceView resource, string scope, bool showOptionMapping = false)
    {
        ImGui.PushID(SafeId(scope));
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 18);
        DrawSlotIcon(Safe(item.Slot, KindLabel(item.Type)), item.Icon, item.Section);
        ImGui.SameLine(0, 6);
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
        ImGui.PopID();
    }

    private void DrawNode(ActorView actor, ResourceView node, string scope, int depth, bool filterBySearch, bool autoExpandSearch, bool showOptionMapping = false)
    {
        var type = Safe(node.Type, "Resource");
        var name = Safe(node.Name, "Unnamed resource");
        var gamePath = Safe(node.GamePath);
        var actualPath = Safe(node.ActualPath);
        var source = Safe(node.SourceLabel, "Source unavailable");
        var key = SafeId($"{scope}:{type}:{name}:{gamePath}:{actualPath}");
        ImGui.PushID(key);
        var presentation = Safe(node.Slot, KindLabel(type));
        var children = node.Children;
        var hasChildren = children.Count > 0;
        var model = IsModel(node);
        var expanded = autoExpandSearch || _expanded.Contains(key);
        var arrow = hasChildren ? (expanded ? "▼" : "▶") : "  ";

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        // ImGui's persistent Indent state is reset while changing table rows/cells.
        // Offset this cell's cursor directly so every tree level moves right.
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, depth - 2) * 18);
        if (ImGui.Button(Safe($"{arrow}##expand:{key}", "  ##expand"), new Vector2(22, ImGui.GetFrameHeight())))
            if (hasChildren) { if (expanded) _expanded.Remove(key); else _expanded.Add(key); }
        if (ImGui.IsItemHovered() && hasChildren) ImGui.SetTooltip(expanded ? "Collapse" : "Expand");
        ImGui.SameLine(0, 4);
        DrawSlotIcon(presentation, node.Icon, node.Section);
        ImGui.SameLine(0, 6);
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
            for (var i = 0; i < children.Count; i++)
                if (!filterBySearch || Matches(children[i]))
                    DrawNode(actor, children[i], $"{scope}:{i}", depth + 1, filterBySearch, autoExpandSearch, showOptionMapping);
        }
        ImGui.PopID();
    }

    private void DrawResolvedPath(ResourceView node, string source, string actualPath, string gamePath)
    {
        if (!string.IsNullOrWhiteSpace(node.SourceModName))
        {
            ImGui.TextColored(new Vector4(.3f, .9f, .35f, 1), $"[{node.SourceModName}]");
            ImGui.SameLine(0, 5);
        }
        else if (node.SourceState == ResourceSourceState.GameData)
        {
            ImGui.TextColored(new Vector4(.45f, .7f, .95f, 1), "[Game Data]");
            ImGui.SameLine(0, 5);
        }
        else if (!string.IsNullOrWhiteSpace(source))
        {
            ImGui.TextColored(new Vector4(.55f, .57f, .63f, 1), $"[{source}]");
            ImGui.SameLine(0, 5);
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
            ImGui.Dummy(new Vector2(ImGui.GetFrameHeight()));
            return;
        }

        var size = new Vector2(ImGui.GetFrameHeight());
        ImGui.Image(icon.Handle, size);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(slot);
    }

    private void DrawResourceTypeFilters(IReadOnlyList<ActorView> actors)
    {
        var groups = new[] { "Tree Structure", "Models", "Textures" };
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(.58f, .61f, .69f, 1), "Filter"); ImGui.SameLine(0, 8);
        foreach (var group in groups)
        {
            var filter = group == "Tree Structure" ? string.Empty : group;
            var count = actors.SelectMany(x => x.Roots).SelectMany(Flatten)
                .Count(node => MatchesResourceType(node, filter));
            ImGui.PushID($"resource-type-filter:{group}");
            var selected = _resourceTypeFilter == filter;
            if (selected) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(.45f, .34f, .16f, 1));
            if (ImGui.SmallButton($"{group}  {count}")) _resourceTypeFilter = filter;
            if (selected) ImGui.PopStyleColor();
            if (group != groups[^1]) ImGui.SameLine(0, 6);
            ImGui.PopID();
        }
        ImGui.NewLine();
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

    private static IEnumerable<ResourceView> Flatten(ResourceView root)
    { yield return root; foreach (var child in root.Children) foreach (var item in Flatten(child)) yield return item; }

    private List<ActorView> ReadActors()
    {
        var result = new List<ActorView>();
        foreach (var entity in _onScreen.Items)
        {
            var parsed = OnScreenService.ProjectVisibleResourceNodes(
                    entity.ResourceRoots,
                    _config.IncludeVanillaResources)
                .Select(ReadNode)
                .ToList();
            result.Add(new ActorView(entity, entity.PresentationCategory.ToString(), Safe(entity.Name), parsed, entity.ObjectIndex));
        }
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
        => MatchesSelectedResourceType(node) || node.Children.Any(HasResourceTypeMatch);

    private bool MatchesSelectedResourceType(ResourceView node)
        => MatchesResourceType(node, _resourceTypeFilter);

    private static bool MatchesResourceType(ResourceView node, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return true;

        var type = Safe(node.Type).ToLowerInvariant();
        var gamePath = Safe(node.GamePath).ToLowerInvariant();
        var actualPath = Safe(node.ActualPath).ToLowerInvariant();
        return filter switch
        {
            "Models" => type.Contains("model") || gamePath.EndsWith(".mdl") || actualPath.EndsWith(".mdl"),
            "Textures" => type.Contains("texture") || gamePath.EndsWith(".tex") || gamePath.EndsWith(".atex") || actualPath.EndsWith(".tex") || actualPath.EndsWith(".atex"),
            "Materials" => type.Contains("material") || gamePath.EndsWith(".mtrl") || actualPath.EndsWith(".mtrl"),
            _ => true,
        };
    }

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
    private bool Matches(ResourceView node)
    {
        var filter = Safe(_filter);
        var children = node.Children;
        return string.IsNullOrWhiteSpace(filter)
            || Safe(node.Name).Contains(filter, StringComparison.OrdinalIgnoreCase)
            || Safe(node.Type).Contains(filter, StringComparison.OrdinalIgnoreCase)
            || Safe(node.SourceLabel).Contains(filter, StringComparison.OrdinalIgnoreCase)
            || Safe(node.SourceModName).Contains(filter, StringComparison.OrdinalIgnoreCase)
            || Safe(node.SourceRelativePath).Contains(filter, StringComparison.OrdinalIgnoreCase)
            || Safe(node.GamePath).Contains(filter, StringComparison.OrdinalIgnoreCase)
            || Safe(node.ActualPath).Contains(filter, StringComparison.OrdinalIgnoreCase)
            || children.Any(Matches);
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
    { var p = ImGui.GetCursorScreenPos(); ImGui.GetWindowDrawList().AddRectFilled(p, p + new Vector2(ImGui.GetContentRegionAvail().X, ImGui.GetFrameHeight()), ImGui.GetColorU32(new Vector4(.11f, .12f, .15f, 1)), 2); }
    private static void ShowPathTooltip(bool hovered, string path)
    {
        if (!hovered) return;
        var safePath = Safe(path);
        ImGui.SetTooltip(string.IsNullOrWhiteSpace(safePath) ? "No resolved path" : $"{safePath}\nRight-click to copy");
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) ImGui.SetClipboardText(safePath);
    }
}
