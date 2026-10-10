using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Models.Resources;
using InstantEdit.Studio.Services;
using InstantEdit.Studio.Services.Project;
using InstantEdit.Studio.Windows.UI;

namespace InstantEdit.Studio.Windows.Studio;

/// <summary>
/// The left pane of Edit mode: the mod itself on top, then everything in it — the way the mod is
/// laid out (its default files, then each group of options, one tile per option), or grouped by what
/// the files are (models, animations, effects, sounds …), one tile per top-level resource. Picking a
/// tile opens what it holds in the centre. Files dragged from Explorer can be dropped anywhere on the
/// pane, or on an option or group to go there.
/// </summary>
internal sealed class ContentsPane
{
    private const string GroupMenuId  = "##contentsgroupmenu";
    private const string OptionMenuId = "##contentsoptionmenu";

    private readonly StudioHost _host;
    private readonly ModEditor _editor;
    private readonly ModSession _session;
    private readonly TextureThumbnailCache _thumbs;
    private readonly ResourceActions _actions;

    private string _filter = string.Empty;
    private readonly HashSet<ResourceSection> _folded = new();
    private readonly HashSet<string> _foldedGroups = new(StringComparer.OrdinalIgnoreCase);

    private enum RowKind { Section, Root, Group, Option, Note }

    private sealed record Row(RowKind Kind, ResourceSection Section = default, ResourceRoot? Root = null, int Count = 0,
        ModGroupView? Group = null, OptionRef Option = default, string? Text = null);

    // The rows, rebuilt when the graph, the mod's groups, the filter or the folding changes (filtering walks every entry).
    private List<Row> _rows = new();
    private ResourceGraph? _rowsGraph;
    private string? _rowsFilter;
    private int _rowsFolding = -1;
    private bool _rowsByOption;
    private PenumbraModMeta? _rowsMeta;
    private int _rowsMetaRevision = -1;
    private int _folding;

    // Popups are opened outside the virtualised child.
    private string? _openMenu;
    private ModGroupView? _menuGroup;
    private OptionRef _menuOption;

    public ContentsPane(ModEditor editor, ModSession session, TextureThumbnailCache thumbs, ResourceActions actions)
    {
        _host  = editor.Host;
        _editor = editor;
        _session = session;
        _thumbs  = thumbs;
        _actions = actions;
    }

    public void Draw()
    {
        var project = _session.Project;
        if (project == null)
            return;

        DrawModTile(project);
        ImGui.Spacing();
        DrawModeSwitch();

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##filter", _session.ByOption ? "Filter by option, name or game path" : "Filter by name or game path", ref _filter, 256);
        ImGui.Spacing();

        float buttonsH = ImGui.GetFrameHeightWithSpacing();
        var graph = _session.Graph;
        if (ImGui.BeginChild("##contents", new Vector2(-1, -buttonsH), false))
        {
            if (_session.ByOption)
            {
                DrawRows(graph, project.Meta);
            }
            else if (graph.Roots.Count == 0)
            {
                Gui.HintWrapped(_editor.Graphs.IsBuilding
                    ? "Reading the mod's files"
                    : "Nothing in this mod yet. Drop files here to add them.");
                if (_editor.Graphs.LastError is { } error)
                    ImGui.TextColored(Theme.Warn, $"Could not read the mod's files: {error}");
            }
            else
            {
                DrawRows(graph, project.Meta);
            }
        }
        ImGui.EndChild();

        // The whole list is a drop area for files from Explorer, and for gear from the item browser.
        if (FileDrop.Target(FileDrop.RejectForAdding, out var dropped))
            _actions.BeginAdd(dropped.Files, null);
        if (SubjectDrop.Target() is { } subject)
            _session.AddItem(subject, _session.AddInto);

        DrawMenus(project.Meta);

        float spacing = ImGui.GetStyle().ItemSpacing.X;
        float w = (ImGui.GetContentRegionAvail().X - spacing) * 0.5f;
        if (Gui.IconTextButton(FontAwesomeIcon.FileImport, "Add files", "Add files of your own (textures, models, sounds, effects) to the mod.", w))
            _actions.BrowseAdd(null);
        ImGui.SameLine();
        if (Gui.IconTextButton(FontAwesomeIcon.FolderPlus, "New group", "Add a group of options to the mod: one the player picks one option of, or any number.", w))
            _actions.BeginNewGroup();
    }

    /// <summary>Two halves: list the mod by option, or by kind of file.</summary>
    private void DrawModeSwitch()
    {
        var cfg = _host.Configuration;
        float spacing = ImGui.GetStyle().ItemSpacing.X * 0.5f;
        float w = (ImGui.GetContentRegionAvail().X - spacing) * 0.5f;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(spacing, ImGui.GetStyle().ItemSpacing.Y));
        if (Half(FontAwesomeIcon.ListUl, "By option", cfg.ContentsByOption, w,
                "The mod as Penumbra lays it out: its default files, then each group and its options. Drop files on an option to put them there."))
            SetByOption(true);
        ImGui.SameLine();
        if (Half(FontAwesomeIcon.Shapes, "By kind", !cfg.ContentsByOption, w,
                "Everything in the mod grouped by what it is — models, animations, effects, sounds — whichever option carries it."))
            SetByOption(false);
        ImGui.PopStyleVar();
        ImGui.Spacing();

        static bool Half(FontAwesomeIcon icon, string label, bool active, float width, string help)
        {
            ImGui.PushStyleColor(ImGuiCol.Button,        active ? Theme.Accent with { W = 0.35f } : Theme.Band);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.Accent with { W = active ? 0.45f : 0.2f });
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,  Theme.Accent with { W = 0.55f });
            ImGui.PushStyleColor(ImGuiCol.Text,          active ? new Vector4(1, 1, 1, 1) : Theme.Muted);
            bool clicked = Dalamud.Interface.Components.ImGuiComponents.IconButtonWithText(icon, $"{label}##mode", new Vector2(width, 0));
            ImGui.PopStyleColor(4);
            Gui.Tooltip(help);
            return clicked && !active;
        }
    }

    private void SetByOption(bool byOption)
    {
        var cfg = _host.Configuration;
        cfg.ContentsByOption = byOption;
        cfg.Save();

        // What is open follows into the other listing.
        if (_session.Selection is { Kind: TargetKind.Resource } s && !string.IsNullOrEmpty(s.Path) && _session.Graph.Find(s) is { } node)
            _session.Focus(_session.TargetFor(node));
        else if (!byOption && _session.Selection is { Kind: TargetKind.Resource } open
                 && (ResourceGraph.OptionOf(open.Root) != null || ResourceGraph.GroupOf(open.Root) != null))
            _session.Selection = null;
    }

    private void DrawModTile(Models.Project.ModProject project)
    {
        var meta = project.Meta;
        var imagePath = !string.IsNullOrWhiteSpace(meta.Image) ? PathNormalizer.ToDiskPath(project.FolderPath, meta.Image) : null;
        var subtitle = string.Join(" · ", new[] { meta.Author, string.IsNullOrWhiteSpace(meta.Version) ? null : $"v{meta.Version}" }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        int files = _session.Graph.ModFileCount;
        int groups = meta.Groups.Count;

        var badges = new List<Badge>();
        if (groups > 0)
            badges.Add(new Badge($"{groups} group(s)", Theme.Muted, string.Join("\n", meta.Groups.Select(g => $"{g.Name} ({g.Type}, {g.Options.Count} options)"))));
        int replaced = project.Sidecar.Replacements.Count;
        if (replaced > 0)
            badges.Add(new Badge($"{replaced} of yours", Theme.Good, "Files of the mod replaced or added with files of your own; the mod follows when you change them."));
        int conflicts = _editor.Sync.Conflicts.Count;
        if (conflicts > 0)
            badges.Add(new Badge($"{conflicts} changed elsewhere", Theme.Warn, "Files changed outside XIV Instant Edit that your replacements here want to change too — choose which to keep from the status bar."));
        var settings = _editor.Settings;
        if (settings.Enabled is { } enabled)
            badges.Add(new Badge(enabled ? "on" : "off", enabled ? Theme.Good : Theme.Warn,
                $"{(enabled ? "Enabled" : "Disabled")} in {settings.Collection}, the collection you are using."));

        var result = Tile.Draw(new TileSpec
        {
            Id        = "mod",
            Title     = string.IsNullOrWhiteSpace(meta.Name) ? project.FolderName : meta.Name,
            Subtitle  = subtitle.Length > 0 ? subtitle : project.FolderName,
            Detail    = $"{files} file(s) · {project.FolderName}",
            Icon      = FontAwesomeIcon.BoxOpen,
            IconColor = Theme.Accent,
            DrawThumb = imagePath != null && File.Exists(imagePath) && _host.Configuration.ShowThumbnails ? side => _thumbs.DrawFile(imagePath, side) : null,
            Badges    = badges,
            Selected  = _session.Selection is { Kind: TargetKind.Project },
            Tooltip   = project.FolderPath,
            Large     = false,
        }, new Vector2(ImGui.GetContentRegionAvail().X, Tile.Height(detail: true)));

        if (result.Clicked)
            _session.Focus(new Target(TargetKind.Project));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Rows
    // ─────────────────────────────────────────────────────────────────────────

    private void DrawRows(ResourceGraph graph, PenumbraModMeta meta)
    {
        var filter = _filter.Trim();
        bool byOption = _session.ByOption;
        if (_rowsGraph != graph || _rowsFilter != filter || _rowsFolding != _folding || _rowsByOption != byOption
            || _rowsMeta != meta || _rowsMetaRevision != meta.Revision)
        {
            _rows = byOption ? OptionRows(graph, meta, filter) : KindRows(graph, filter);
            _rowsGraph        = graph;
            _rowsFilter       = filter;
            _rowsFolding      = _folding;
            _rowsByOption     = byOption;
            _rowsMeta         = meta;
            _rowsMetaRevision = meta.Revision;
        }
        var rows = _rows;

        if (rows.Count == 0)
        {
            Gui.Hint("Nothing matches the filter.");
            return;
        }

        var selected = byOption ? SelectedOption() : null;
        var selectedGroup = byOption ? SelectedGroup() : null;

        // Bring the selected entry into view when something asked for it ("go to" from an issue).
        int reveal = -1;
        if (_session.PendingReveal is { } wanted)
        {
            var wantedOption = byOption ? OptionOf(wanted) : null;
            var wantedGroup  = byOption ? ResourceGraph.GroupOf(wanted.Root) : null;
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row.Kind == RowKind.Root && Contains(row.Root!, wanted)
                    || row.Kind == RowKind.Option && wantedOption == row.Option && wantedGroup == null
                    || row.Kind == RowKind.Group && wantedGroup != null && string.Equals(row.Group!.Id, wantedGroup, StringComparison.OrdinalIgnoreCase))
                {
                    reveal = i;
                    break;
                }
            }
        }

        bool keepInView = false;
        if (reveal < 0 && _keyboardTo >= 0 && _keyboardTo < rows.Count)
        {
            reveal = _keyboardTo;
            keepInView = true;
        }
        _keyboardTo = -1;

        float width = ImGui.GetContentRegionAvail().X;
        var heights = rows.Select(r => r.Kind switch
        {
            RowKind.Section => ImGui.GetFrameHeight() + Theme.S(4),
            RowKind.Note    => ImGui.GetTextLineHeightWithSpacing(),
            _               => Tile.Height(detail: false),
        }).ToList();
        VirtualRows.Draw(heights, i =>
        {
            var row = rows[i];
            switch (row.Kind)
            {
                case RowKind.Section: DrawSectionHeader(row.Section, row.Count, width); break;
                case RowKind.Root:    DrawRoot(row.Root!, width, heights[i]); break;
                case RowKind.Group:   DrawGroup(row.Group!, meta, width, heights[i], selectedGroup); break;
                case RowKind.Option:  DrawOption(row.Option, row.Group, meta, graph, width, heights[i], selected, selectedGroup); break;
                case RowKind.Note:
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Theme.T(24));
                    ImGui.TextColored(Theme.Faint, row.Text ?? string.Empty);
                    break;
            }
        }, reveal, keepInView);

        // Up and down move between entries while the list has focus.
        if (ImGui.IsWindowFocused() && !ImGui.IsAnyItemActive()
            && (ImGui.IsKeyPressed(ImGuiKey.DownArrow) || ImGui.IsKeyPressed(ImGuiKey.UpArrow)))
        {
            int step = ImGui.IsKeyPressed(ImGuiKey.DownArrow) ? 1 : -1;
            int current = rows.FindIndex(r => IsCurrent(r, selected, selectedGroup));
            for (int i = current < 0 ? (step > 0 ? 0 : rows.Count - 1) : current + step; i >= 0 && i < rows.Count; i += step)
            {
                // Selected without a "go to", so the list only scrolls as far as it must.
                var next = rows[i] switch
                {
                    { Kind: RowKind.Root } r   => r.Root!.Target,
                    { Kind: RowKind.Option } r => Target.ForRoot(ResourceGraph.OptionRootId(r.Option)),
                    { Kind: RowKind.Group } r  => Target.ForRoot(ResourceGraph.GroupRootId(r.Group!.Id)),
                    _                          => (Target?)null,
                };
                if (next is { } target)
                {
                    _session.Selection = target;
                    _session.GoToView(StudioView.Resources);
                    _keyboardTo = i;
                    break;
                }
            }
        }
    }

    private bool IsCurrent(Row row, OptionRef? selected, string? selectedGroup) => row.Kind switch
    {
        RowKind.Root   => _session.Selection is { } s && Contains(row.Root!, s),
        RowKind.Option => selectedGroup == null && selected == row.Option,
        RowKind.Group  => selectedGroup != null && string.Equals(selectedGroup, row.Group!.Id, StringComparison.OrdinalIgnoreCase),
        _              => false,
    };

    /// <summary>A row the arrow keys moved to, scrolled into view on the next frame.</summary>
    private int _keyboardTo = -1;

    // ── By kind ──────────────────────────────────────────────────────────────

    private List<Row> KindRows(ResourceGraph graph, string filter)
    {
        var rows = new List<Row>();
        foreach (var section in graph.Roots.GroupBy(r => r.Section).OrderBy(g => g.Key))
        {
            var roots = section.Where(r => Matches(r, filter)).ToList();
            if (roots.Count == 0)
                continue;
            rows.Add(new Row(RowKind.Section, section.Key, Count: roots.Count));
            if (_folded.Contains(section.Key) && filter.Length == 0)
                continue;
            rows.AddRange(roots.Select(r => new Row(RowKind.Root, section.Key, r)));
        }
        return rows;
    }

    private static bool Matches(ResourceRoot root, string filter)
    {
        if (filter.Length == 0)
            return true;
        if (root.Title.Contains(filter, StringComparison.OrdinalIgnoreCase) || root.Subtitle.Contains(filter, StringComparison.OrdinalIgnoreCase))
            return true;
        var seen = new HashSet<ResourceNode>();
        var stack = new Stack<ResourceNode>(root.Nodes);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (!seen.Add(node))
                continue;
            if (node.GamePath.Contains(filter, StringComparison.OrdinalIgnoreCase))
                return true;
            foreach (var child in node.Children)
                stack.Push(child.Node);
        }
        return false;
    }

    /// <summary>Whether a target lies in an entry: the entry itself, or a resource listed under it.</summary>
    private bool Contains(ResourceRoot root, Target target)
    {
        if (target.Kind != TargetKind.Resource)
            return false;
        if (string.Equals(target.Root, root.Id, StringComparison.OrdinalIgnoreCase))
            return true;
        bool elsewhere = string.IsNullOrEmpty(target.Root) || ResourceGraph.OptionOf(target.Root) != null || ResourceGraph.GroupOf(target.Root) != null;
        return elsewhere && !string.IsNullOrEmpty(target.Path) && _session.Graph.Find(target)?.RootId == root.Id;
    }

    private void DrawSectionHeader(ResourceSection section, int count, float width)
    {
        bool folded = _folded.Contains(section);
        var pos = ImGui.GetCursorScreenPos();
        ImGui.PushID((int)section);
        if (ImGui.InvisibleButton("##section", new Vector2(width, ImGui.GetFrameHeight())))
        {
            if (!_folded.Add(section))
                _folded.Remove(section);
            _folding++;
        }
        bool hot = ImGui.IsItemHovered();
        ImGui.PopID();

        var draw = ImGui.GetWindowDrawList();
        var color = hot ? Theme.Accent : Theme.Muted;
        float h = ImGui.GetFrameHeight();
        Tile.DrawIcon(draw, folded ? FontAwesomeIcon.CaretRight : FontAwesomeIcon.CaretDown, pos, new Vector2(Theme.S(14), h), color);
        Tile.DrawIcon(draw, ResourceStyle.SectionIcon(section), pos + new Vector2(Theme.S(16), 0), new Vector2(Theme.S(18), h), color);
        var label = $"{ResourceKinds.SectionLabel(section)}  ({count})";
        draw.AddText(pos + new Vector2(Theme.S(40), (h - ImGui.GetTextLineHeight()) * 0.5f), ImGui.GetColorU32(color), label);
        float lineX = pos.X + Theme.S(46) + ImGui.CalcTextSize(label).X;
        if (lineX < pos.X + width)
            draw.AddLine(new Vector2(lineX, pos.Y + h * 0.5f), new Vector2(pos.X + width, pos.Y + h * 0.5f), ImGui.GetColorU32(Theme.TileBorder));
    }

    private void DrawRoot(ResourceRoot root, float width, float height)
    {
        var badges = new List<Badge>();
        var nonDefault = root.Options.Where(o => !o.IsDefault).ToList();
        if (nonDefault.Count > 0 && _session.Project is { } project)
            badges.Add(new Badge(nonDefault.Count == 1 ? project.Meta.OptionName(nonDefault[0]) : $"{nonDefault.Count} options", Theme.Muted,
                string.Join("\n", root.Options.Select(o => project.Meta.OptionLabel(o)))));
        if (root.ReplacedCount > 0)
            badges.Add(new Badge($"{root.ReplacedCount} replaced", Theme.Good));
        if (root.MissingCount > 0)
            badges.Add(new Badge($"{root.MissingCount} missing", Theme.Bad));
        if (_session.AddedItemsIn(root) is { Count: > 0 } added)
            badges.Add(new Badge("from the game", Theme.Accent, $"Added to the mod from the game: {string.Join(", ", added.Select(i => i.Name))}."));

        var selection = _session.Selection;
        bool selected = selection is { } s && Contains(root, s);
        var worst = PortValidator.WorstUnder(_session.Issues, root.Target);

        var result = Tile.Draw(new TileSpec
        {
            Id        = root.Id,
            Title     = root.Title,
            Subtitle  = root.Subtitle.Length > 0 ? root.Subtitle : $"{root.FileCount} file(s)",
            Icon      = ResourceStyle.Icon(root.Kind),
            IconColor = Theme.Muted,
            DrawThumb = Thumb(root),
            Badges    = badges,
            Selected  = selected,
            Issue     = worst,
            IssueText = worst != null ? string.Join("\n", _session.Issues.Where(i => i.Target.IsWithin(root.Target)).Select(i => i.Message)) : null,
            Drop      = new FileDropRule(FileDrop.RejectForAdding, _ => { }),
            Tooltip   = $"{root.Title}\n{root.Subtitle}\n{root.FileCount} file(s)",
        }, new Vector2(width, height));

        if (result.Clicked)
            _session.Focus(root.Target);
        if (result.Dropped is { } dropped)
            _actions.BeginAdd(dropped.Files, root);
    }

    private Action<float>? Thumb(ResourceRoot? root)
    {
        if (root == null || !_host.Configuration.ShowThumbnails)
            return null;
        if (root.ThumbDiskPath is { } disk)
            return side => { if (Directory.Exists(disk)) _thumbs.DrawFolder(disk, side); else _thumbs.DrawFile(disk, side); };
        if (root.ThumbGamePath is { } game)
            return side => _thumbs.DrawGame(game, side);
        return null;
    }

    // ── By option ────────────────────────────────────────────────────────────

    private List<Row> OptionRows(ResourceGraph graph, PenumbraModMeta meta, string filter)
    {
        var rows = new List<Row>();
        bool OptionMatches(OptionRef option, string name)
            => filter.Length == 0 || name.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || graph.OptionRoot(option) is { } root && Matches(root, filter);

        if (OptionMatches(OptionRef.Default, "Default files"))
            rows.Add(new Row(RowKind.Option, Option: OptionRef.Default));

        foreach (var group in meta.Groups)
        {
            bool groupMatches = filter.Length > 0 && group.Name.Contains(filter, StringComparison.OrdinalIgnoreCase);
            var options = group.FileSets.Where(o => groupMatches || OptionMatches(o.Ref, o.Name)).ToList();
            if (filter.Length > 0 && !groupMatches && options.Count == 0)
                continue;

            rows.Add(new Row(RowKind.Group, Group: group));
            if (_foldedGroups.Contains(group.Id) && filter.Length == 0)
                continue;
            if (group.Type == ModGroupType.Imc)
                rows.Add(new Row(RowKind.Note, Group: group, Text: $"{group.Options.Count} attribute option(s) — no files"));
            else if (group.FileSets.Count == 0)
                rows.Add(new Row(RowKind.Note, Group: group, Text: "No options yet — drop files on the group to make one each"));
            rows.AddRange(options.Select(o => new Row(RowKind.Option, Group: group, Option: o.Ref)));
        }
        return rows;
    }

    /// <summary>The option the selection lies in: an option's entry, a file listed in one, or a file of the mod picked elsewhere.</summary>
    private OptionRef? SelectedOption() => _session.Selection is { } s ? OptionOf(s) : null;

    private OptionRef? OptionOf(Target target)
    {
        if (target.Kind != TargetKind.Resource)
            return null;
        if (ResourceGraph.OptionOf(target.Root) is { } option)
            return option;
        if (string.IsNullOrEmpty(target.Path))
            return ResourceGraph.GroupOf(target.Root) != null && !string.IsNullOrEmpty(target.Option) ? OptionRef.Parse(target.Option) : null;
        if (_session.Graph.Find(target) is { IsModContent: true } node)
            return node.Option;
        return OptionRef.Parse(target.Option) is { IsDefault: false } named ? named : null;
    }

    /// <summary>The group picked itself (not one of its options or files listed in the centre).</summary>
    private string? SelectedGroup()
        => _session.Selection is { Kind: TargetKind.Resource } s && string.IsNullOrEmpty(s.Path) && string.IsNullOrEmpty(s.Option)
            ? ResourceGraph.GroupOf(s.Root)
            : null;

    private void DrawGroup(ModGroupView group, PenumbraModMeta meta, float width, float height, string? selectedGroup)
    {
        bool folded = _foldedGroups.Contains(group.Id);
        bool takes = PenumbraModMeta.TakesOptions(group);
        var badges = new List<Badge> { new(ResourceStyle.GroupTypeLabel(group.Type), Theme.Muted, ResourceStyle.GroupTypeHelp(group.Type)) };

        var worst = WorstIn(group.FileSets.Select(o => o.Ref));
        var actions = new List<TileAction>
        {
            new(FontAwesomeIcon.Plus, PenumbraModMeta.WhyNoNewOption(group) ?? $"Add an option to {group.Name}",
                () => _actions.BeginNewOption(group.Id), Enabled: takes),
        };

        var result = Tile.Draw(new TileSpec
        {
            Id        = $"grp{group.Id}",
            Expanded  = !folded,
            Title     = group.Name,
            Subtitle  = group.Type == ModGroupType.Combining
                ? $"{group.Options.Count} option(s) · {group.FileSets.Count} combination(s)"
                : $"{group.Options.Count} option(s)",
            Icon      = ResourceStyle.GroupIcon(group.Type),
            IconColor = Theme.Accent,
            Badges    = badges,
            Actions   = actions,
            Selected  = selectedGroup != null && string.Equals(selectedGroup, group.Id, StringComparison.OrdinalIgnoreCase),
            Issue     = worst,
            Drop      = new FileDropRule(p => takes ? FileDrop.RejectForAdding(p) : PenumbraModMeta.WhyNoNewOption(group), _ => { }),
            Tooltip   = $"{group.Name}\n{ResourceStyle.GroupTypeHelp(group.Type)}\nDrop files here to make one new option of each.",
        }, new Vector2(width, height));

        if (result.Toggled)
        {
            if (!_foldedGroups.Add(group.Id))
                _foldedGroups.Remove(group.Id);
            _folding++;
        }
        else if (result.Clicked)
        {
            _session.FocusGroup(group.Id);
        }
        if (result.RightClicked)
        {
            _menuGroup = group;
            _openMenu = GroupMenuId;
        }
        if (result.Dropped is { } dropped)
            _actions.BeginAddToGroup(dropped.Files, group.Id);
    }

    private void DrawOption(OptionRef option, ModGroupView? group, PenumbraModMeta meta, ResourceGraph graph, float width, float height,
        OptionRef? selected, string? selectedGroup)
    {
        var root = graph.OptionRoot(option);
        var settings = _editor.Settings;
        bool? on = settings.IsOn(option);

        var badges = new List<Badge>();
        if (on is { } active)
            badges.Add(new Badge(active ? "in use" : "off", active ? Theme.Good : Theme.Faint,
                active ? $"In use for you right now, in {settings.Collection}."
                       : settings.Enabled == false ? $"The mod is disabled in {settings.Collection}." : $"Not picked in {settings.Collection}."));
        if (root is { ReplacedCount: > 0 })
            badges.Add(new Badge($"{root.ReplacedCount} of yours", Theme.Good, "Files here replaced or added with files of your own."));
        if (root is { MissingCount: > 0 })
            badges.Add(new Badge($"{root.MissingCount} missing", Theme.Bad, "Referenced files that neither the mod nor the game has."));
        if (_session.Project?.Sidecar.AddedItems.Where(i => OptionRef.Parse(i.Option) == option).Select(i => i.Name).ToList() is { Count: > 0 } items)
            badges.Add(new Badge("from the game", Theme.Accent, $"Added here from the game: {string.Join(", ", items)}."));

        var parts = new List<string>();
        int files = root?.FileCount ?? graph.FilesIn(option);
        parts.Add(files == 0 && root == null && _editor.Graphs.IsBuilding ? "reading" : $"{files} file(s)");
        if (root is { SwapCount: > 0 }) parts.Add($"{root.SwapCount} swap(s)");
        if (root is { ManipulationCount: > 0 }) parts.Add($"{root.ManipulationCount} manipulation(s)");

        var title = option.IsDefault ? "Default files" : meta.OptionName(option);
        bool isSelected = selectedGroup == null && selected == option;
        var worst = WorstIn(new[] { option });
        bool canName = !option.IsDefault && group is { Type: ModGroupType.Single or ModGroupType.Multi };

        var result = Tile.Draw(new TileSpec
        {
            Id        = $"opt{option.Serialize()}",
            Indent    = option.IsDefault ? 0 : Theme.T(16),
            Title     = title,
            Subtitle  = option.IsDefault ? $"Always on · {string.Join(" · ", parts)}" : string.Join(" · ", parts),
            Icon      = ResourceStyle.OptionIcon(group?.Type),
            IconColor = on == true ? Theme.Good : Theme.Muted,
            DrawThumb = Thumb(root),
            Badges    = badges,
            Selected  = isSelected,
            Issue     = worst,
            IssueText = worst != null ? string.Join("\n", IssuesIn(new[] { option }).Select(i => i.Message)) : null,
            Drop      = new FileDropRule(FileDrop.RejectForAdding, _ => { }),
            Tooltip   = option.IsDefault
                ? "The mod's default files: always in use while the mod is enabled."
                : $"{meta.OptionLabel(option)}{(meta.OptionDescription(option) is { Length: > 0 } d ? $"\n\n{d}" : string.Empty)}",
        }, new Vector2(width, height));

        if (result.Clicked)
            _session.FocusOption(option);
        if (result.RightClicked && canName)
        {
            _menuOption = option;
            _menuGroup = group;
            _openMenu = OptionMenuId;
        }
        if (result.Dropped is { } dropped)
            _actions.BeginAdd(dropped.Files, root, option);
    }

    private IEnumerable<PortIssue> IssuesIn(IEnumerable<OptionRef> options)
    {
        var keys = options.Select(o => o.Serialize()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _session.Issues.Where(i => i.Target.Kind == TargetKind.Resource
            && (keys.Contains(i.Target.Option ?? string.Empty) && !string.IsNullOrEmpty(i.Target.Path)
                || ResourceGraph.OptionOf(i.Target.Root) is { } o && keys.Contains(o.Serialize())));
    }

    private Severity? WorstIn(IEnumerable<OptionRef> options)
    {
        Severity? worst = null;
        foreach (var issue in IssuesIn(options))
            if (worst == null || issue.Severity > worst.Value)
                worst = issue.Severity;
        return worst;
    }

    // ── Menus ────────────────────────────────────────────────────────────────

    private void DrawMenus(PenumbraModMeta meta)
    {
        if (_openMenu != null)
        {
            ImGui.OpenPopup(_openMenu);
            _openMenu = null;
        }

        if (ImGui.BeginPopup(GroupMenuId))
        {
            if (_menuGroup is { } group)
            {
                ImGui.TextColored(Theme.Muted, group.Name);
                ImGui.Separator();
                bool takes = PenumbraModMeta.TakesOptions(group);
                if (ImGui.MenuItem("Add an option", string.Empty, false, takes))
                    _actions.BeginNewOption(group.Id);
                if (!takes)
                    Gui.Tooltip(PenumbraModMeta.WhyNoNewOption(group));
                if (ImGui.MenuItem("Add files, one option each", string.Empty, false, takes))
                    _actions.BrowseAddToGroup(group.Id);
                if (ImGui.MenuItem("Rename"))
                    _actions.BeginRenameGroup(group.Id);
                ImGui.Separator();
                ImGui.PushStyleColor(ImGuiCol.Text, Theme.Bad);
                if (ImGui.MenuItem("Remove the group"))
                    _actions.AskRemoveGroup(group.Id);
                ImGui.PopStyleColor();
            }
            ImGui.EndPopup();
        }

        if (ImGui.BeginPopup(OptionMenuId))
        {
            var option = _menuOption;
            ImGui.TextColored(Theme.Muted, meta.OptionLabel(option));
            ImGui.Separator();
            if (ImGui.MenuItem("Add files to it"))
                _actions.BrowseAdd(_session.Graph.OptionRoot(option), option);
            if (ImGui.MenuItem("Rename"))
                _actions.BeginRenameOption(option);
            ImGui.Separator();
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Bad);
            if (ImGui.MenuItem("Remove the option"))
                _actions.AskRemoveOption(option);
            ImGui.PopStyleColor();
            ImGui.EndPopup();
        }
    }
}
