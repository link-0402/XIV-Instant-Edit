using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Models;
using InstantEdit.Services;
using FileDrop = InstantEdit.Studio.Windows.UI.FileDrop;

namespace InstantEdit.Ui.Main;

/// <summary> The On Screen tab: Penumbra's resource tree of every on-screen character, with search and kind filters. </summary>
internal sealed class OnScreenTab
{
    // The chips' legend dots; the texture dot is split between the texture roles' colours.
    private static readonly Vector4[] ModelSwatch = [Theme.ModelKind];
    private static readonly Vector4[] MaterialSwatch = [Theme.MaterialKind];
    private static readonly Vector4[] AnimationSwatch = [Theme.AnimationKind];
    private static readonly Vector4[] TextureSwatch = [Theme.BaseTexture, Theme.NormalTexture, Theme.MaskTexture, Theme.IndexTexture];

    private readonly Configuration _config;
    private readonly Action _saveConfig;
    private readonly OnScreenService _onScreen;
    private readonly ResourceRows _rows;
    private readonly ResourceHoverCards _hoverCards;
    private readonly TextureSessionsTab _textureTab;
    private readonly SlotIcons _slotIcons;

    // View-model state: drawing must not rebuild, re-classify, or re-search the resource
    // tree every frame. The views are replaced when the snapshot or the vanilla toggle changes.
    private readonly ResourceKindSelection _kinds = new(ResourceKindChipSet.OnScreen.Listed);
    private readonly ResourceSearch _search = new();
    private readonly ResourceTypeCounter _counter = new();
    private readonly ExpansionState _expansion;
    private string _filter = string.Empty;
    private IReadOnlyList<OnScreenObject>? _actorSnapshot;
    private bool _actorSnapshotIncludesVanilla;
    private List<ActorView> _actors = [];

    public OnScreenTab(Configuration config, Action saveConfig, OnScreenService onScreen, ResourceRows rows, ResourceHoverCards hoverCards,
        TextureSessionsTab textureTab, SlotIcons slotIcons, ExpansionState expansion)
    {
        _config = config;
        _saveConfig = saveConfig;
        _onScreen = onScreen;
        _rows = rows;
        _hoverCards = hoverCards;
        _textureTab = textureTab;
        _slotIcons = slotIcons;
        _expansion = expansion;
    }

    public void Draw()
    {
        // Images dragged in from Explorer can be dropped on texture rows.
        FileDrop.BeginExternalSource();
        ImGui.Spacing();
        var actors = ReadActors();
        DrawFilterBar(actors, ResourceKindChipSet.OnScreen, showVanillaToggle: true);
        ImGui.Spacing();
        DrawResources(actors);
    }

    /// <summary> Search, kind chips and (on screen) the vanilla toggle. </summary>
    private void DrawFilterBar(IReadOnlyList<ActorView> actors, ResourceKindChipSet chips, bool showVanillaToggle)
    {
        Widgets.SearchBox("##resource-filter", ref _filter, "Search names, mods and paths");
        _search.Text = _filter;
        var counts = _counter.Count(actors, chips.Counts);
        if (Widgets.Chip("All", counts[0], _kinds.IsAll))
            _kinds.Clear();
        for (var i = 0; i < chips.Chips.Count; i++)
        {
            ImGui.SameLine(0, Theme.Gap);
            var (kind, label) = chips.Chips[i];
            if (Widgets.Chip(label, counts[i + 1], _kinds.Contains(kind), KindSwatch(kind)))
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
                Widgets.EmptyState(FontAwesomeIcon.Sync, "Refreshing resources", "Collecting the on-screen resource list from Penumbra.");
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
        // On Screen's views always have their character.
        if (actor.Entity is not { } entity)
            return;
        var actorId = ExpansionState.ActorKey(entity);
        using var id = ImRaii.PushId(actorId);
        var flat = _kinds.IsFlat;
        var defaultExpanded = flat || entity.ObjectIndex == 0;
        var expanded = _expansion.IsExpanded(actorId, defaultExpanded, !flat && _search.Active);
        if (DrawActorHeader(actor, expanded))
            _expansion.Toggle(actorId, expanded, defaultExpanded);
        if (!expanded)
            return;

        using var table = ImRaii.Table("##resource-table", 4, ResourceRows.ResourceTableFlags);
        if (!table.Success)
            return;
        SetupColumns();
        DrawSection(actor, ResourceSection.CharacterFeatures, "Character features", ExpansionState.SectionKey(actorId, ResourceSection.CharacterFeatures));
        DrawSection(actor, ResourceSection.Gear, "Gear", ExpansionState.SectionKey(actorId, ResourceSection.Gear));
        DrawSection(actor, ResourceSection.Other, "Other", ExpansionState.SectionKey(actorId, ResourceSection.Other));
    }

    private static bool DrawActorHeader(ActorView actor, bool expanded)
    {
        ResourceRows.DrawOpaqueRow();
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

    private static Vector4[] KindSwatch(ResourceKinds kind)
        => kind switch
        {
            ResourceKinds.Model => ModelSwatch,
            ResourceKinds.Material => MaterialSwatch,
            ResourceKinds.Animation => AnimationSwatch,
            _ => TextureSwatch,
        };

    private void SetupColumns()
    {
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, .34f);
        ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthStretch, .18f);
        ImGui.TableSetupColumn("Path", ImGuiTableColumnFlags.WidthStretch, .48f);
        ImGui.TableSetupColumn("##actions", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoHide, _rows.ActionsColumnWidth());
        ImGui.TableHeadersRow();
    }

    private void DrawSection(ActorView actor, ResourceSection section, string label, string key)
    {
        var searchActive = actor.Entity is not null && _search.Active;
        var filterBySearch = searchActive && !_search.ActorIdentityMatches(actor);
        var ordered = actor.RootsInSection(section)
            .Where(x => _kinds.AdmitsSubtree(x) && (!filterBySearch || _search.Matches(x)))
            .ToList();
        if (ordered.Count == 0) return;
        using var id = ImRaii.PushId(UiText.SafeId(key));
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        var flat = _kinds.IsFlat;
        // Sections start open, except Other (body connectors and the like are rarely edited);
        // the rows beneath them start collapsed. A search also opens Other in the flat view,
        // so its matches do not hide behind the collapsed header.
        var defaultExpanded = section != ResourceSection.Other;
        var expanded = _expansion.IsExpanded(key, defaultExpanded, _search.Active && (!flat || !defaultExpanded));
        if (Widgets.GhostIconButton("##section-toggle", expanded ? FontAwesomeIcon.CaretDown : FontAwesomeIcon.CaretRight, expanded ? "Collapse" : "Expand"))
            _expansion.Toggle(key, expanded, defaultExpanded);
        ImGui.SameLine(0, Theme.Gap);
        if (ImGui.Selectable($"{label}##section-label", false, ImGuiSelectableFlags.SpanAllColumns, new Vector2(0, ImGui.GetFrameHeight())))
            _expansion.Toggle(key, expanded, defaultExpanded);
        if (!expanded)
            return;
        if (flat)
        {
            var row = 0;
            foreach (var root in ordered)
                foreach (var resource in root.Flattened)
                    if (_kinds.Admits(resource) && (!filterBySearch || _search.Matches(resource)))
                        DrawFlatNode(actor, root, resource, $"{key}:flat:{row++}");
        }
        else
        {
            for (var i = 0; i < ordered.Count; i++)
                DrawNode(actor, ordered[i], $"{key}:{i}", new TreeGuide(Theme.TreeIndent, 0, 0, false), filterBySearch, searchActive);
        }
    }

    /// <summary> One row of the flattened (kind-filtered) view: the resource itself, with its parent item as context. </summary>
    private void DrawFlatNode(ActorView actor, ResourceView item, ResourceView resource, string scope)
    {
        using var id = ImRaii.PushId(UiText.SafeId(scope));
        ImGui.TableNextRow();
        ResourceRows.TintRow(resource);
        ImGui.TableSetColumnIndex(0);
        var rowLeft = ImGui.GetCursorScreenPos().X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Theme.TreeIndent);
        _slotIcons.Draw(UiText.Safe(item.Slot, ResourceViews.KindLabel(item.Type)), item.Icon, item.Section);
        ImGui.SameLine(0, Theme.Gap);
        ResourceRows.DrawRowGlyph(resource);
        ImGui.SameLine(0, Theme.Gap);
        var name = UiText.Safe(resource.DisplayName, "Unnamed resource");
        var context = ReferenceEquals(item, resource) ? string.Empty : UiText.Safe(item.DisplayName);
        var labelWidth = context.Length == 0 ? 0 : ImGui.CalcTextSize(name).X + Theme.Gap;
        ImGui.Selectable($"{name}##label", false, ImGuiSelectableFlags.None, new Vector2(labelWidth, ImGui.GetFrameHeight()));
        var hovered = ImGui.IsItemHovered();
        ImGui.OpenPopupOnItemClick(ResourceRows.RowMenuPopup, ImGuiPopupFlags.MouseButtonRight);
        if (hovered)
            _hoverCards.DrawRowHover(resource, UiText.Safe(item.Slot, item.Type), scope);
        if (context.Length > 0)
        {
            ImGui.SameLine(0, Theme.Gap);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Hint, context);
        }
        DrawRowCells(actor, resource, rowLeft);
        _rows.DrawRowMenu(actor, resource);
    }

    /// <summary>
    /// A row's arrow, relative to the item row's arrow. Item rows carry a slot icon after their
    /// arrow; below them, each row's arrow sits under its parent's kind glyph.
    /// </summary>
    private static float TreeArrowOffset(int level) => level == 0 ? 0 : (level + 1) * ResourceRows.TreeStep;

    /// <summary> The row's kind glyph, relative to the item row's arrow. </summary>
    private static float TreeGlyphOffset(int level) => TreeArrowOffset(level) + (level == 0 ? 2 : 1) * ResourceRows.TreeStep;

    /// <summary>
    /// Draws a row's guide lines: the lines of ancestors that have rows still to come, and the
    /// connector from the parent's line to this row, ending at its arrow or, for a row without
    /// one, running through the empty arrow slot to its glyph. A parent's line hangs from the
    /// centre of the slot beside its glyph: the slot icon of an item, the arrow of anything below.
    /// Must be called at the start of the row's first cell.
    /// </summary>
    private static void DrawTreeGuides(TreeGuide guide, bool hasArrow)
    {
        if (guide.Level == 0)
            return;
        var origin = ImGui.GetCursorScreenPos();
        var frame = ImGui.GetFrameHeight();
        var padding = ImGui.GetStyle().CellPadding.Y;
        var top = origin.Y - padding;
        var bottom = origin.Y + frame + padding;
        var middle = MathF.Floor(origin.Y + frame / 2) + .5f;
        var drawList = ImGui.GetWindowDrawList();
        var colour = ImGui.GetColorU32(Theme.TreeLine);
        var thickness = Math.Max(1, MathF.Floor(Theme.Scale));
        float LineX(int column) => MathF.Floor(origin.X + guide.Indent + (column + 1) * ResourceRows.TreeStep + frame / 2) + .5f;

        for (var column = 0; column < guide.Level - 1; column++)
            if (guide.LineContinues(column))
                drawList.AddLine(new Vector2(LineX(column), top), new Vector2(LineX(column), bottom), colour, thickness);

        var x = LineX(guide.Level - 1);
        drawList.AddLine(new Vector2(x, top), new Vector2(x, guide.Last ? middle : bottom), colour, thickness);
        var end = origin.X + guide.Indent + (hasArrow ? TreeArrowOffset(guide.Level) : TreeGlyphOffset(guide.Level)) - Theme.Scaled(3);
        drawList.AddLine(new Vector2(x, middle), new Vector2(end, middle), colour, thickness);
    }

    /// <summary> One row of the tree view, followed by its admitted children when expanded. </summary>
    private void DrawNode(ActorView actor, ResourceView node, string scope, TreeGuide guide, bool filterBySearch, bool autoExpandSearch)
    {
        var key = ExpansionState.NodeKey(scope, node);
        using var id = ImRaii.PushId(key);
        var presentation = UiText.Safe(node.Slot, ResourceViews.KindLabel(UiText.Safe(node.Type, "Resource")));
        // Only descend into children IE can actually edit (or that themselves
        // contain one) - resources like animations, skeletons, or VFX have
        // nothing to open here, so they're dropped from the tree entirely.
        var hasChildren = _kinds.AdmitsChild(node);
        var expanded = _expansion.IsExpanded(key, false, autoExpandSearch);

        ImGui.TableNextRow();
        ResourceRows.TintRow(node);
        ImGui.TableSetColumnIndex(0);
        var rowLeft = ImGui.GetCursorScreenPos().X;
        DrawTreeGuides(guide, hasChildren);
        // ImGui's persistent Indent state is reset while changing table rows/cells, so each
        // element is placed at its column's offset from the cell start.
        var start = ImGui.GetCursorPosX() + guide.Indent;
        ImGui.SetCursorPosX(start + TreeArrowOffset(guide.Level));
        if (hasChildren)
        {
            // A square arrow keeps both carets centred on the guide line that hangs from it.
            if (Widgets.GhostIconButton("##expand", expanded ? FontAwesomeIcon.CaretDown : FontAwesomeIcon.CaretRight, expanded ? "Collapse" : "Expand",
                    new Vector2(ImGui.GetFrameHeight())))
                _expansion.Toggle(key, expanded, false);
        }
        else
        {
            ImGui.Dummy(new Vector2(ImGui.GetFrameHeight()));
        }
        // Only the item row shows its slot; the rows below it belong to that slot.
        if (guide.Level == 0)
        {
            ImGui.SameLine(0, 0);
            ImGui.SetCursorPosX(start + ResourceRows.TreeStep);
            _slotIcons.Draw(presentation, node.Icon, node.Section);
        }
        ImGui.SameLine(0, 0);
        ImGui.SetCursorPosX(start + TreeGlyphOffset(guide.Level));
        ResourceRows.DrawRowGlyph(node);
        ImGui.SameLine(0, Theme.Gap);
        if (ImGui.Selectable($"{UiText.Safe(node.DisplayName, "Unnamed resource")}##label", false, ImGuiSelectableFlags.None, new Vector2(0, ImGui.GetFrameHeight())) && hasChildren)
            _expansion.Toggle(key, expanded, false);
        var hovered = ImGui.IsItemHovered();
        ImGui.OpenPopupOnItemClick(ResourceRows.RowMenuPopup, ImGuiPopupFlags.MouseButtonRight);
        if (hovered)
            _hoverCards.DrawRowHover(node, presentation, key);
        DrawRowCells(actor, node, rowLeft);
        _rows.DrawRowMenu(actor, node);

        if (!expanded || !hasChildren)
            return;
        // The last visible child ends its parent's guide line, so count the visible ones first.
        var visible = 0;
        foreach (var child in node.Children)
            if (_kinds.AdmitsSubtree(child) && (!filterBySearch || _search.Matches(child)))
                visible++;
        // Child IDs count every type-matching child, including ones the search hides.
        var childIndex = 0;
        var drawn = 0;
        foreach (var child in node.Children)
        {
            if (!_kinds.AdmitsSubtree(child))
                continue;
            var i = childIndex++;
            if (!filterBySearch || _search.Matches(child))
                DrawNode(actor, child, $"{scope}:{i}", guide.Child(++drawn == visible), filterBySearch, autoExpandSearch);
        }
    }

    /// <summary> The row's other cells; <paramref name="rowLeft"/> is where its first cell starts on screen. </summary>
    private void DrawRowCells(ActorView actor, ResourceView node, float rowLeft)
    {
        ImGui.TableSetColumnIndex(1);
        ResourceRows.DrawSourceBadge(node);

        ImGui.TableSetColumnIndex(2);
        Widgets.PathText(UiText.Safe(node.SourceRelativePath, UiText.Safe(node.GamePath, node.ActualPath)), UiText.Safe(node.ActualPath));

        ImGui.TableSetColumnIndex(3);
        if (TextureSessionsTab.IsTextureRow(node))
            _textureTab.DrawTextureDrop(actor, node, rowLeft);
        _rows.DrawRowActions(actor, node);
    }

    /// <summary> The On Screen view model: Penumbra's resources, rebuilt only when the snapshot or the vanilla toggle changes. </summary>
    private List<ActorView> ReadActors()
    {
        var items = _onScreen.Items;
        var includeVanilla = _config.IncludeVanillaResources;
        if (ReferenceEquals(items, _actorSnapshot) && includeVanilla == _actorSnapshotIncludesVanilla)
            return _actors;

        var result = new List<ActorView>(items.Count);
        foreach (var entity in items)
        {
            var roots = OnScreenService.ProjectVisibleResourceNodes(entity.ResourceRoots, includeVanilla)
                .Select(ResourceViews.FromNode)
                .ToList();
            result.Add(new ActorView(entity, entity.PresentationCategory.ToString(), UiText.Safe(entity.Name), roots, entity.ObjectIndex));
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
}
