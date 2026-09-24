using System.Diagnostics;
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
                                                        ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Hideable;
    private const string RowMenuPopup = "##row-menu";

    private static readonly (ResourceKinds Kind, string Label)[] KindChips =
    [
        (ResourceKinds.Model, "Models"),
        (ResourceKinds.Texture, "Textures"),
        (ResourceKinds.Material, "Materials"),
    ];

    /// <summary> The kinds each chip counts; index 0 is the "All" chip, which counts every editable row. </summary>
    private static readonly ResourceKinds[] KindChipCounts = [ResourceKinds.Editable, ResourceKinds.Model, ResourceKinds.Texture, ResourceKinds.Material];

    // View-model state: drawing must not rebuild, re-classify, or re-search the resource
    // tree every frame. The views are replaced when the snapshot or the vanilla toggle changes.
    private readonly ResourceKindSelection _kinds = new();
    private readonly ResourceSearch _search = new();
    private readonly ResourceTypeCounter _counter = new();
    private readonly ExpansionState _expansion = new();
    private IReadOnlyList<OnScreenObject>? _actorSnapshot;
    private bool _actorSnapshotIncludesVanilla;
    private List<ActorView> _actors = [];

    private enum RowLayout
    {
        OnScreen,
        Mod,
    }

    private void DrawOnScreenTab()
    {
        ImGui.Spacing();
        var actors = ReadActors();
        DrawFilterBar(actors, showVanillaToggle: true);
        ImGui.Spacing();
        DrawResources(actors);
    }

    /// <summary> Search, kind chips and (on screen) the vanilla toggle. </summary>
    private void DrawFilterBar(IReadOnlyList<ActorView> actors, bool showVanillaToggle)
    {
        Widgets.SearchBox("##resource-filter", ref _filter, "Search names, mods and paths");
        _search.Text = _filter;
        var counts = _counter.Count(actors, KindChipCounts);
        if (Widgets.Chip("All", counts[0], _kinds.IsAll))
            _kinds.Clear();
        for (var i = 0; i < KindChips.Length; i++)
        {
            ImGui.SameLine(0, Theme.Gap);
            var (kind, label) = KindChips[i];
            if (Widgets.Chip(label, counts[i + 1], _kinds.Contains(kind)))
                _kinds.Toggle(kind);
        }
        ImGui.SameLine(0, Theme.Gap);
        Widgets.HelpTip("All shows the resource tree. Pick one or more kinds to list just those rows.");

        if (!showVanillaToggle)
            return;
        ImGui.SameLine(0, Theme.Scaled(16));
        var includeVanilla = _config.IncludeVanillaResources;
        if (ImGui.Checkbox("Include vanilla", ref includeVanilla))
        {
            _config.IncludeVanillaResources = includeVanilla;
            _saveConfig();
        }
        ImGui.SameLine(0, Theme.Gap);
        Widgets.HelpTip("Show resources loaded directly from game data alongside Penumbra-modified resources. Needed to edit vanilla models and textures.");
    }

    private void DrawResources(IReadOnlyList<ActorView> actors, string? emptyMessage = null)
    {
        actors = actors.Where(ActorMatches).ToList();
        var viewportHeight = Math.Max(1, ImGui.GetContentRegionAvail().Y);
        using var background = ImRaii.PushColor(ImGuiCol.ChildBg, Theme.PanelBg);
        using var browser = ImRaii.Child("##resource-browser", new Vector2(0, viewportHeight), true);
        if (!browser.Success)
            return;
        if (actors.Count == 0)
        {
            if (emptyMessage is null && _onScreen.IsRefreshing)
                Widgets.EmptyState(FontAwesomeIcon.Sync, "Refreshing resources…", "Collecting the on-screen resource list from Penumbra.");
            else if (emptyMessage is not null)
                Widgets.EmptyState(FontAwesomeIcon.FolderOpen, "Nothing to show", emptyMessage);
            else if (_search.Active || _kinds.IsFlat)
                Widgets.EmptyState(FontAwesomeIcon.Search, "No matching resources", "Clear the search or choose other kinds.");
            else
                Widgets.EmptyState(FontAwesomeIcon.Eye, "No snapshot loaded", "Use the refresh button in the toolbar to collect on-screen resources.");
            return;
        }

        using var align = ImRaii.PushStyle(ImGuiStyleVar.SelectableTextAlign, new Vector2(0, .5f));
        foreach (var actor in actors)
            DrawActor(actor);
    }

    private void DrawActor(ActorView actor)
    {
        if (actor.Entity is null)
        {
            DrawModTable(actor);
            return;
        }

        var actorId = ExpansionState.ActorKey(actor.Entity);
        using var id = ImRaii.PushId(actorId);
        var flat = _kinds.IsFlat;
        var defaultExpanded = flat || actor.Entity.ObjectIndex == 0;
        var expanded = _expansion.IsExpanded(actorId, defaultExpanded, !flat && _search.Active);
        if (DrawActorHeader(actor, expanded))
            _expansion.Toggle(actorId, expanded, defaultExpanded);
        if (!expanded)
            return;

        using var table = ImRaii.Table("##resource-table", 4, ResourceTableFlags);
        if (!table.Success)
            return;
        SetupColumns(RowLayout.OnScreen);
        DrawSection(actor, ResourceSection.CharacterFeatures, "Character features", ExpansionState.SectionKey(actorId, ResourceSection.CharacterFeatures));
        DrawSection(actor, ResourceSection.Gear, "Gear", ExpansionState.SectionKey(actorId, ResourceSection.Gear));
        DrawSection(actor, ResourceSection.Other, "Other", ExpansionState.SectionKey(actorId, ResourceSection.Other));
    }

    private bool DrawActorHeader(ActorView actor, bool expanded)
    {
        DrawOpaqueRow();
        Widgets.Icon(expanded ? FontAwesomeIcon.CaretDown : FontAwesomeIcon.CaretRight, Theme.Muted);
        ImGui.SameLine(0, Theme.Gap);
        Widgets.Icon(ActorIcon(actor.Entity!.PresentationCategory), Theme.Label);
        ImGui.SameLine(0, Theme.Gap);
        var clicked = ImGui.Selectable($"{actor.Header}##actor-label", false, ImGuiSelectableFlags.None, new Vector2(0, ImGui.GetFrameHeight()));
        var summary = actor.Summary;
        if (summary.Length > 0)
        {
            var width = ImGui.CalcTextSize(summary).X;
            ImGui.SameLine(ImGui.GetContentRegionMax().X - width - Theme.Gap);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Muted, summary);
        }
        return clicked;
    }

    private static FontAwesomeIcon ActorIcon(ActorPresentationCategory category)
        => category switch
        {
            ActorPresentationCategory.Minion => FontAwesomeIcon.Cat,
            ActorPresentationCategory.Mount => FontAwesomeIcon.Horse,
            ActorPresentationCategory.Summon => FontAwesomeIcon.Ghost,
            _ => FontAwesomeIcon.User,
        };

    private static FontAwesomeIcon KindGlyph(ResourceKinds kinds)
    {
        if ((kinds & ResourceKinds.Model) != 0) return FontAwesomeIcon.Cube;
        if ((kinds & ResourceKinds.Texture) != 0) return FontAwesomeIcon.Image;
        if ((kinds & ResourceKinds.Material) != 0) return FontAwesomeIcon.Palette;
        return FontAwesomeIcon.File;
    }

    private static void SetupColumns(RowLayout layout)
    {
        if (layout == RowLayout.OnScreen)
        {
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, .34f);
            ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthStretch, .18f);
            ImGui.TableSetupColumn("Path", ImGuiTableColumnFlags.WidthStretch, .48f);
        }
        else
        {
            ImGui.TableSetupColumn("Resource", ImGuiTableColumnFlags.WidthStretch, .36f);
            ImGui.TableSetupColumn("Path", ImGuiTableColumnFlags.WidthStretch, .42f);
            ImGui.TableSetupColumn("Option", ImGuiTableColumnFlags.WidthStretch, .22f);
        }
        ImGui.TableSetupColumn("##actions", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoHide, Theme.Scaled(58));
        ImGui.TableHeadersRow();
    }

    private void DrawModTable(ActorView actor)
    {
        // BuildModView sorts the roots by game path once, so this only filters them.
        using var table = ImRaii.Table("##mod-resource-table", 4, ResourceTableFlags);
        if (!table.Success)
            return;
        SetupColumns(RowLayout.Mod);

        if (_kinds.IsFlat)
        {
            var row = 0;
            foreach (var root in actor.Roots)
            {
                if (!_kinds.AdmitsSubtree(root))
                    continue;
                foreach (var resource in root.Flattened)
                    if (_kinds.Admits(resource))
                        DrawFlatNode(actor, root, resource, $"mod:flat:{row++}", RowLayout.Mod);
            }
        }
        else
        {
            var index = 0;
            foreach (var root in actor.Roots)
                if (_kinds.AdmitsSubtree(root))
                    DrawNode(actor, root, $"mod:{index++}", 0, false, false, RowLayout.Mod);
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
        var flat = _kinds.IsFlat;
        var expanded = _expansion.IsExpanded(key, flat, !flat && _search.Active);
        if (Widgets.GhostIconButton("##section-toggle", expanded ? FontAwesomeIcon.CaretDown : FontAwesomeIcon.CaretRight, expanded ? "Collapse" : "Expand"))
            _expansion.Toggle(key, expanded, flat);
        ImGui.SameLine(0, Theme.Gap);
        if (ImGui.Selectable($"{label}##section-label", false, ImGuiSelectableFlags.SpanAllColumns, new Vector2(0, ImGui.GetFrameHeight())))
            _expansion.Toggle(key, expanded, flat);
        if (!expanded)
            return;
        if (flat)
        {
            var row = 0;
            foreach (var root in ordered)
                foreach (var resource in root.Flattened)
                    if (_kinds.Admits(resource) && (!filterBySearch || _search.Matches(resource)))
                        DrawFlatNode(actor, root, resource, $"{key}:flat:{row++}", RowLayout.OnScreen);
        }
        else
        {
            for (var i = 0; i < ordered.Count; i++)
                DrawNode(actor, ordered[i], $"{key}:{i}", 3, filterBySearch, searchActive, RowLayout.OnScreen);
        }
    }

    /// <summary> One row of the flattened (kind-filtered) view: the resource itself, with its parent item as context. </summary>
    private void DrawFlatNode(ActorView actor, ResourceView item, ResourceView resource, string scope, RowLayout layout)
    {
        using var id = ImRaii.PushId(SafeId(scope));
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Theme.TreeIndent);
        DrawSlotIcon(Safe(item.Slot, ResourceViews.KindLabel(item.Type)), item.Icon, item.Section);
        ImGui.SameLine(0, Theme.Gap);
        Widgets.Icon(KindGlyph(resource.Kinds), Theme.Muted);
        ImGui.SameLine(0, Theme.Gap);
        var name = Safe(resource.DisplayName, "Unnamed resource");
        var context = ReferenceEquals(item, resource) ? string.Empty : Safe(item.DisplayName);
        var labelWidth = context.Length == 0 ? 0 : ImGui.CalcTextSize(name).X + Theme.Gap;
        ImGui.Selectable($"{name}##label", false, ImGuiSelectableFlags.None, new Vector2(labelWidth, ImGui.GetFrameHeight()));
        var hovered = ImGui.IsItemHovered();
        ImGui.OpenPopupOnItemClick(RowMenuPopup, ImGuiPopupFlags.MouseButtonRight);
        if (hovered)
            DrawRowHover(resource, Safe(item.Slot, item.Type), scope);
        if (context.Length > 0)
        {
            ImGui.SameLine(0, Theme.Gap);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Hint, context);
        }
        DrawRowCells(actor, resource, layout);
        DrawRowMenu(actor, resource);
    }

    /// <summary> One row of the tree view, followed by its admitted children when expanded. </summary>
    private void DrawNode(ActorView actor, ResourceView node, string scope, int depth, bool filterBySearch, bool autoExpandSearch, RowLayout layout)
    {
        var key = ExpansionState.NodeKey(scope, node);
        using var id = ImRaii.PushId(key);
        var presentation = Safe(node.Slot, ResourceViews.KindLabel(Safe(node.Type, "Resource")));
        // Only descend into children IE can actually edit (or that themselves
        // contain one) - resources like animations, skeletons, or VFX have
        // nothing to open here, so they're dropped from the tree entirely.
        var hasChildren = _kinds.AdmitsChild(node);
        var expanded = _expansion.IsExpanded(key, false, autoExpandSearch);

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        // ImGui's persistent Indent state is reset while changing table rows/cells.
        // Offset this cell's cursor directly so every tree level moves right.
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, depth - 2) * Theme.TreeIndent);
        if (hasChildren)
        {
            if (Widgets.GhostIconButton("##expand", expanded ? FontAwesomeIcon.CaretDown : FontAwesomeIcon.CaretRight, expanded ? "Collapse" : "Expand"))
                _expansion.Toggle(key, expanded, false);
        }
        else
        {
            ImGui.Dummy(new Vector2(ImGui.GetFrameHeight()));
        }
        ImGui.SameLine(0, Theme.Gap);
        DrawSlotIcon(presentation, node.Icon, node.Section);
        ImGui.SameLine(0, Theme.Gap);
        Widgets.Icon(KindGlyph(node.Kinds), Theme.Muted);
        ImGui.SameLine(0, Theme.Gap);
        if (ImGui.Selectable($"{Safe(node.DisplayName, "Unnamed resource")}##label", false, ImGuiSelectableFlags.None, new Vector2(0, ImGui.GetFrameHeight())) && hasChildren)
            _expansion.Toggle(key, expanded, false);
        var hovered = ImGui.IsItemHovered();
        ImGui.OpenPopupOnItemClick(RowMenuPopup, ImGuiPopupFlags.MouseButtonRight);
        if (hovered)
            DrawRowHover(node, presentation, key);
        DrawRowCells(actor, node, layout);
        DrawRowMenu(actor, node);

        if (!expanded || !hasChildren)
            return;
        // Child IDs count every type-matching child, including ones the search hides.
        var childIndex = 0;
        foreach (var child in node.Children)
        {
            if (!_kinds.AdmitsSubtree(child))
                continue;
            var i = childIndex++;
            if (!filterBySearch || _search.Matches(child))
                DrawNode(actor, child, $"{scope}:{i}", depth + 1, filterBySearch, autoExpandSearch, layout);
        }
    }

    private void DrawRowCells(ActorView actor, ResourceView node, RowLayout layout)
    {
        var column = 1;
        if (layout == RowLayout.OnScreen)
        {
            ImGui.TableSetColumnIndex(column++);
            DrawSourceBadge(node);
        }

        ImGui.TableSetColumnIndex(column++);
        Widgets.PathText(Safe(node.SourceRelativePath, Safe(node.GamePath, node.ActualPath)), Safe(node.ActualPath));

        if (layout == RowLayout.Mod)
        {
            ImGui.TableSetColumnIndex(column++);
            ImGui.AlignTextToFramePadding();
            if (node.OptionMapping.Length > 0)
                ImGui.TextUnformatted(node.OptionMapping);
            else
                ImGui.TextColored(Theme.Hint, "Unmapped");
        }

        ImGui.TableSetColumnIndex(column);
        DrawRowActions(actor, node);
    }

    private void DrawSourceBadge(ResourceView node)
    {
        if (node.SourceModName.Length > 0)
        {
            if (Widgets.Badge(node.SourceModName, Theme.ModSource))
                ImGui.SetTooltip(node.OptionMapping.Length > 0
                    ? $"Mod: {node.SourceModName}\nDirectory: {node.SourceModDirectory}\nOption: {node.OptionMapping}"
                    : $"Mod: {node.SourceModName}\nDirectory: {node.SourceModDirectory}");
        }
        else if (node.SourceState == ResourceSourceState.GameData)
        {
            if (Widgets.Badge("Game Data", Theme.GameSource))
                ImGui.SetTooltip("Loaded from the game's own files (no mod replaces it).");
        }
        else
        {
            if (Widgets.Badge(Safe(node.SourceLabel, "Unavailable"), Theme.Hint))
                ImGui.SetTooltip("The file could not be attributed to a mod; it cannot be edited here.");
        }
    }

    private void DrawRowActions(ActorView actor, ResourceView node)
    {
        if (node.IsModel && ResourceViews.IsSafeModel(node))
        {
            if (Widgets.IconButton("##edit-model", FontAwesomeIcon.Pen, "Edit this model in Blender"))
                TryEditNode(node, actor);
            ImGui.SameLine(0, Theme.Scaled(2));
        }
        else if (IsTextureRow(node))
        {
            var available = TextureEditAvailable(node);
            if (Widgets.IconButton("##edit-texture", FontAwesomeIcon.PaintBrush,
                    available
                        ? "Open as a 32-bit TGA in your configured editor. Shared references to this texture will also change."
                        : "This texture has no verified writable mod source.",
                    available && Volatile.Read(ref _textureBusy) == 0))
                StartTextureEdit(node, actor);
            ImGui.SameLine(0, Theme.Scaled(2));
        }

        if (Widgets.GhostIconButton("##more", FontAwesomeIcon.EllipsisH, "More actions"))
            ImGui.OpenPopup(RowMenuPopup);
    }

    /// <summary> The row's context menu; opened by right-click on the name or the ⋯ button. </summary>
    private void DrawRowMenu(ActorView actor, ResourceView node)
    {
        using var popup = ImRaii.Popup(RowMenuPopup);
        if (!popup.Success)
            return;

        var safeModel = node.IsModel && ResourceViews.IsSafeModel(node);
        var texture = IsTextureRow(node);
        if (safeModel && ImGui.MenuItem("Edit model in Blender"))
            TryEditNode(node, actor);
        if (texture)
        {
            using var disabled = ImRaii.Disabled(!TextureEditAvailable(node) || Volatile.Read(ref _textureBusy) != 0);
            if (ImGui.MenuItem("Edit texture"))
                StartTextureEdit(node, actor);
        }
        if (safeModel || texture)
            ImGui.Separator();

        using (ImRaii.Disabled(node.GamePath.Length == 0))
            if (ImGui.MenuItem("Copy game path"))
                ImGui.SetClipboardText(node.GamePath);
        using (ImRaii.Disabled(node.ActualPath.Length == 0))
            if (ImGui.MenuItem("Copy resolved path"))
                ImGui.SetClipboardText(node.ActualPath);
        var folder = ContainingFolder(node);
        using (ImRaii.Disabled(folder is null))
            if (ImGui.MenuItem("Open containing folder") && folder is not null)
                OpenFolder(folder);

        if (node.SourceModDirectory.Length == 0)
            return;
        ImGui.Separator();
        if (ImGui.MenuItem("Open in Penumbra"))
            OpenInPenumbra(node);
        if (actor.Entity is not null && ImGui.MenuItem("Show in Mod Browser"))
            ShowInModBrowser(node.SourceModDirectory);
    }

    private static string? ContainingFolder(ResourceView node)
    {
        if (node.ActualPath.Length == 0 || !Path.IsPathRooted(node.ActualPath))
            return null;
        try
        {
            var directory = Path.GetDirectoryName(node.ActualPath);
            return directory is not null && Directory.Exists(directory) ? directory : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void OpenFolder(string directory)
    {
        try
        {
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            _log.Warning(e, "Could not open a mod folder.");
            SetStatus($"Could not open the folder: {e.Message}", FeedbackSeverity.Warning);
        }
    }

    private void OpenInPenumbra(ResourceView node)
    {
        try
        {
            _penumbra.OpenModInPenumbra(node.SourceModDirectory, node.SourceModName);
        }
        catch (Exception e)
        {
            _log.Warning(e, "Could not open the mod in Penumbra.");
            SetStatus($"Could not open the mod in Penumbra: {e.Message}", FeedbackSeverity.Warning);
        }
    }

    private void ShowInModBrowser(string modDirectory)
    {
        _modFilter = string.Empty;
        CancelModLoad();
        _selectedModDirectory = modDirectory;
        _loadedModDirectory = null;
        _loadedModView = null;
        _pendingTab = MainTab.ModBrowser;
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
}
