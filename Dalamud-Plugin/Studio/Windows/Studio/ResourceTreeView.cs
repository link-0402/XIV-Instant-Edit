using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Models.Resources;
using InstantEdit.Studio.Services;
using InstantEdit.Studio.Services.Project;
using InstantEdit.Studio.Windows.Panels;
using InstantEdit.Studio.Windows.UI;

namespace InstantEdit.Studio.Windows.Studio;

/// <summary>
/// The centre of Edit mode: what is picked on the left, as a tree of tiles — a model, the materials it
/// names, their textures; an animation, the effects and sounds it plays; and so on. An entry by kind
/// lists one top-level resource; an option lists the files it carries; a group lists each of its
/// options with their files, one after the other. Every tile takes a dropped file (or a picked one,
/// through its Browse button) to replace what it stands for, whatever made the mod; several files
/// dropped on one become options at its game path.
/// </summary>
internal sealed class ResourceTreeView
{
    private const string NodeMenuId = "##resourcenodemenu";

    private readonly StudioHost _host;
    private readonly ModEditor _editor;
    private readonly ModSession _session;
    private readonly TextureThumbnailCache _thumbs;
    private readonly TextureSlotEditor _textures;
    private readonly ResourceActions _actions;
    private readonly HandoffActions _handoffs;

    /// <summary>Rows folded away, by their path through the tree (a shared node folds per place it is listed).</summary>
    private readonly HashSet<string> _collapsed = new(StringComparer.OrdinalIgnoreCase);

    private enum RowKind { Node, AddFile, Option, AddOptions }

    private sealed record Row(RowKind Kind, string Key, int Depth, ResourceNode? Node = null, string? Role = null, bool Open = false,
        ResourceRoot? Root = null, OptionRef Option = default);

    /// <summary>What the centre shows: an entry (by kind, or one option's), or every option of a group.</summary>
    private sealed record Scope(string Id, ResourceRoot? Root, ModGroupView? Group, OptionRef? Option);

    // Popups are opened and drawn outside the virtualised child, in one ID scope.
    private string? _openPopup;
    private ResourceNode? _menuNode;

    // The flattened rows, rebuilt only when something they depend on changed: a mod can list tens
    // of thousands of files under one entry.
    private List<Row> _rows = new();
    private List<float> _heights = new();
    private ResourceGraph? _rowsGraph;
    private string? _rowsScope;
    private int _rowsSession = -1;
    private int _rowsFolding = -1;
    private int _rowsMeta = -1;
    private Target? _rowsReveal;
    private int _folding;
    private int _savedFolding;
    private Models.Project.ModProject? _collapsedFor;

    /// <summary>A row the arrow keys moved to, scrolled into view on the next frame.</summary>
    private int _keyboardTo = -1;

    public ResourceTreeView(ModEditor editor, ModSession session, TextureThumbnailCache thumbs,
        TextureSlotEditor textures, ResourceActions actions, HandoffActions handoffs)
    {
        _host   = editor.Host;
        _editor = editor;
        _session  = session;
        _thumbs   = thumbs;
        _textures = textures;
        _actions  = actions;
        _handoffs = handoffs;
    }

    private bool ShowThumbs => _host.Configuration.ShowThumbnails;

    /// <summary>The entry the selection lies in, if it still exists.</summary>
    public ResourceRoot? CurrentRoot()
    {
        return _session.Selection is { Kind: TargetKind.Resource } s ? _session.Graph.FindRoot(s.Root) : null;
    }

    /// <summary>What the selection asks the centre to show, if it still exists.</summary>
    private Scope? CurrentScope()
    {
        if (_session.Selection is not { Kind: TargetKind.Resource } s || _session.Project is not { } project)
            return null;
        if (ResourceGraph.GroupOf(s.Root) is { } groupId)
            return project.Meta.FindGroupView(groupId) is { } group ? new Scope(s.Root, null, group, null) : null;
        if (ResourceGraph.OptionOf(s.Root) is { } option)
            return project.Meta.OptionExists(option) ? new Scope(s.Root, _session.Graph.OptionRoot(option), null, option) : null;
        return _session.Graph.FindRoot(s.Root) is { } root ? new Scope(root.Id, root, null, null) : null;
    }

    public void Draw()
    {
        var scope = CurrentScope();
        if (scope == null)
        {
            DrawEmpty();
            return;
        }

        if (scope.Group != null)
            DrawGroupHeader(scope.Group);
        else if (scope.Option is { } option)
            DrawOptionHeader(option, scope.Root);
        else
            DrawHeader(scope.Root!);
        ImGui.Spacing();

        // What was folded is remembered per mod.
        if (!ReferenceEquals(_collapsedFor, _session.Project))
        {
            _collapsedFor = _session.Project;
            _collapsed.Clear();
            if (_session.Project != null)
                _collapsed.UnionWith(_session.Project.Sidecar.Ui.Collapsed);
            _folding++;
            _savedFolding = _folding;   // just read; nothing to write back
        }
        if (_folding != _savedFolding && _session.Project is { } project)
        {
            _savedFolding = _folding;
            project.Sidecar.Ui.Collapsed = _collapsed.Take(4000).ToList();
            project.MarkUiStateDirty();
        }

        var reveal = _session.PendingReveal;
        int metaRevision = _session.Project?.Meta.Revision ?? -1;
        if (_rowsGraph != _session.Graph || _rowsScope != scope.Id || _rowsSession != _session.Revision
            || _rowsFolding != _folding || _rowsReveal != reveal || _rowsMeta != metaRevision)
        {
            _rows = scope.Group != null ? FlattenGroup(scope.Group) : Flatten(scope.Root, scope.Option);
            _heights = _rows.Select(r => r.Kind switch
            {
                RowKind.Node or RowKind.Option => Tile.Height(detail: true),
                _                              => Theme.T(34),
            }).ToList();
            _rowsGraph   = _session.Graph;
            _rowsScope   = scope.Id;
            _rowsSession = _session.Revision;
            _rowsFolding = _folding;
            _rowsReveal  = reveal;
            _rowsMeta    = metaRevision;
        }
        var rows = _rows;
        var heights = _heights;
        int revealAt = RevealIndex(rows);
        bool keepInView = false;
        if (revealAt < 0 && _keyboardTo >= 0)
        {
            revealAt = _keyboardTo;
            keepInView = true;
        }
        _keyboardTo = -1;

        if (ImGui.BeginChild("##tree", new Vector2(-1, -1), false))
        {
            float width = ImGui.GetContentRegionAvail().X;
            VirtualRows.Draw(heights, i => DrawRow(rows[i], scope, width, heights[i]), revealAt, keepInView);
            if (ImGui.IsWindowFocused() && !ImGui.IsAnyItemActive())
                HandleKeys(rows, scope);
        }
        ImGui.EndChild();

        DrawPopups();
    }

    /// <summary>
    /// Arrow keys walk the tree (up and down through the rows, right to open, left to fold); Delete
    /// asks to take one of the mod's own files out.
    /// </summary>
    private void HandleKeys(List<Row> rows, Scope scope)
    {
        int current = _session.Selection is { } selected ? rows.FindIndex(r => r.Node is { } n && Names(selected, n)) : -1;
        if (ImGui.IsKeyPressed(ImGuiKey.DownArrow) || ImGui.IsKeyPressed(ImGuiKey.UpArrow))
        {
            int step = ImGui.IsKeyPressed(ImGuiKey.DownArrow) ? 1 : -1;
            for (int i = current < 0 ? (step > 0 ? 0 : rows.Count - 1) : current + step; i >= 0 && i < rows.Count; i += step)
            {
                if (rows[i].Node is { } next)
                {
                    _session.Selection = TargetIn(scope, next);
                    _keyboardTo = i;
                    break;
                }
            }
            return;
        }

        if (current < 0)
            return;
        var row = rows[current];
        if (ImGui.IsKeyPressed(ImGuiKey.RightArrow) && _collapsed.Remove(row.Key))
            _folding++;
        else if (ImGui.IsKeyPressed(ImGuiKey.LeftArrow) && row.Node!.Children.Count > 0 && _collapsed.Add(row.Key))
            _folding++;
        else if (ImGui.IsKeyPressed(ImGuiKey.Delete) && row.Node!.Origin == OriginKind.ModFile)
            _actions.AskRemove(row.Node);
    }

    private void DrawEmpty()
    {
        var graph = _session.Graph;
        Gui.Icon(FontAwesomeIcon.ArrowLeft, Theme.Muted);
        ImGui.SameLine();
        if (graph.Roots.Count == 0 && _editor.Graphs.IsBuilding)
            Gui.HintWrapped("Reading the mod's files");
        else if (graph.Roots.Count == 0 && !_session.ByOption)
            Gui.HintWrapped("This mod has no files yet. Drop files onto the list on the left to add them.");
        else
            Gui.HintWrapped("Pick something on the left to see what it is made of — and to replace any of it, drop a file onto its tile.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Headers
    // ─────────────────────────────────────────────────────────────────────────

    private void DrawHeader(ResourceRoot root)
    {
        var badges = new List<Badge>();
        if (root.ReplacedCount > 0)
            badges.Add(new Badge($"{root.ReplacedCount} replaced", Theme.Good));
        if (root.MissingCount > 0)
            badges.Add(new Badge($"{root.MissingCount} missing", Theme.Bad, "Referenced files that neither the mod nor the game has."));

        var actions = new List<TileAction>
        {
            new(FontAwesomeIcon.Plus, "Add files under this entry", () => _actions.BrowseAdd(root)),
        };

        var worst = PortValidator.WorstUnder(_session.Issues, root.Target);
        var result = Tile.Draw(new TileSpec
        {
            Id        = "header",
            Title     = root.Title,
            Subtitle  = root.Subtitle,
            Detail    = $"{ResourceKinds.SectionLabel(root.Section)} · {root.FileCount} file(s)"
                      + (root.Options.Count > 1 || root.Options.Any(o => !o.IsDefault) ? $" · {root.Options.Count} option(s)" : string.Empty),
            Icon      = ResourceStyle.SectionIcon(root.Section),
            IconColor = Theme.Accent,
            DrawThumb = Thumb(root),
            Badges    = badges,
            Actions   = actions,
            Large     = true,
            Selected  = _session.Selection == root.Target,
            Issue     = worst,
            IssueText = worst != null ? IssuesText(i => i.Target.IsWithin(root.Target)) : null,
            Drop      = new FileDropRule(FileDrop.RejectForAdding, p => _actions.BeginAdd(p.Files, root)),
        }, new Vector2(ImGui.GetContentRegionAvail().X, Tile.Height(large: true)));

        if (result.Clicked)
            _session.Selection = root.Target;
        if (result.Dropped is { } dropped)
            _actions.BeginAdd(dropped.Files, root);
    }

    /// <summary>The header of an option's entry: which group it is in, how it is picked, what it carries.</summary>
    private void DrawOptionHeader(OptionRef option, ResourceRoot? root)
    {
        var meta = _session.Project!.Meta;
        var group = option.IsDefault ? null : meta.FindGroupView(option.Group);
        var settings = _editor.Settings;

        var badges = new List<Badge>();
        if (settings.IsOn(option) is { } on)
            badges.Add(new Badge(on ? "in use" : "off", on ? Theme.Good : Theme.Faint,
                on ? $"In use for you right now, in {settings.Collection}." : $"Not in use for you right now, in {settings.Collection}."));
        if (root is { ReplacedCount: > 0 })
            badges.Add(new Badge($"{root.ReplacedCount} of yours", Theme.Good));
        if (root is { MissingCount: > 0 })
            badges.Add(new Badge($"{root.MissingCount} missing", Theme.Bad, "Referenced files that neither the mod nor the game has."));

        bool named = group is { Type: ModGroupType.Single or ModGroupType.Multi };
        var actions = new List<TileAction>
        {
            new(FontAwesomeIcon.Plus, "Add files to this option", () => _actions.BrowseAdd(root, option)),
        };
        if (named)
        {
            actions.Add(new TileAction(FontAwesomeIcon.Pen, "Rename the option", () => _actions.BeginRenameOption(option)));
            actions.Add(new TileAction(FontAwesomeIcon.Trash, "Take the option out of the mod", () => _actions.AskRemoveOption(option), Theme.Bad));
        }

        var parts = new List<string> { $"{root?.FileCount ?? 0} file(s)" };
        if (root is { SwapCount: > 0 }) parts.Add($"{root.SwapCount} file swap(s)");
        if (root is { ManipulationCount: > 0 }) parts.Add($"{root.ManipulationCount} manipulation(s)");

        var target = Target.ForRoot(ResourceGraph.OptionRootId(option));
        var result = Tile.Draw(new TileSpec
        {
            Id        = "optionheader",
            Title     = option.IsDefault ? "Default files" : meta.OptionName(option),
            Subtitle  = option.IsDefault ? "Always in use while the mod is enabled" : $"{group?.Name} · {ResourceStyle.GroupTypeLabel(group?.Type ?? ModGroupType.Unknown)}",
            Detail    = string.Join(" · ", parts),
            Icon      = ResourceStyle.OptionIcon(group?.Type),
            IconColor = Theme.Accent,
            DrawThumb = Thumb(root),
            Badges    = badges,
            Actions   = actions,
            Large     = true,
            Selected  = _session.Selection == target,
            Drop      = new FileDropRule(FileDrop.RejectForAdding, _ => { }),
            Tooltip   = meta.OptionLabel(option),
        }, new Vector2(ImGui.GetContentRegionAvail().X, Tile.Height(large: true)));

        if (result.Clicked)
            _session.Selection = target;
        if (result.Dropped is { } dropped)
            _actions.BeginAdd(dropped.Files, root, option);
    }

    /// <summary>The header of a group: its name and kind; files dropped on it become options of it.</summary>
    private void DrawGroupHeader(ModGroupView group)
    {
        bool takes = PenumbraModMeta.TakesOptions(group);
        var actions = new List<TileAction>
        {
            new(FontAwesomeIcon.Plus, PenumbraModMeta.WhyNoNewOption(group) ?? "Add an option", () => _actions.BeginNewOption(group.Id), Enabled: takes),
            new(FontAwesomeIcon.FileImport, takes ? "Add files, one new option each" : PenumbraModMeta.WhyNoNewOption(group) ?? string.Empty,
                () => _actions.BrowseAddToGroup(group.Id), Enabled: takes),
            new(FontAwesomeIcon.Pen, "Rename the group", () => _actions.BeginRenameGroup(group.Id)),
            new(FontAwesomeIcon.Trash, "Take the group out of the mod", () => _actions.AskRemoveGroup(group.Id), Theme.Bad),
        };

        var target = Target.ForRoot(ResourceGraph.GroupRootId(group.Id));
        var result = Tile.Draw(new TileSpec
        {
            Id        = "groupheader",
            Title     = group.Name,
            Subtitle  = ResourceStyle.GroupTypeHelp(group.Type),
            Detail    = group.Type == ModGroupType.Combining
                ? $"{group.Options.Count} option(s) · {group.FileSets.Count} combination(s)"
                : $"{group.Options.Count} option(s)",
            Icon      = ResourceStyle.GroupIcon(group.Type),
            IconColor = Theme.Accent,
            Actions   = actions,
            Large     = true,
            Selected  = _session.Selection == target,
            Drop      = new FileDropRule(p => takes ? FileDrop.RejectForAdding(p) : PenumbraModMeta.WhyNoNewOption(group), _ => { }),
            Tooltip   = "Drop files here to make one new option of each.",
        }, new Vector2(ImGui.GetContentRegionAvail().X, Tile.Height(large: true)));

        if (result.Clicked)
            _session.Selection = target;
        if (result.Dropped is { } dropped)
            _actions.BeginAddToGroup(dropped.Files, group.Id);
    }

    private Action<float>? Thumb(ResourceRoot? root)
    {
        if (!ShowThumbs || root == null)
            return null;
        if (root.ThumbDiskPath is { } disk)
            return side => DrawDiskThumb(disk, side);
        if (root.ThumbGamePath is { } game)
            return side => _thumbs.DrawGame(game, side);
        return null;
    }

    private void DrawDiskThumb(string path, float side)
    {
        if (Directory.Exists(path)) _thumbs.DrawFolder(path, side);
        else _thumbs.DrawFile(path, side);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Rows
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>The visible rows of one entry: the tree walked depth-first, skipping what is folded.</summary>
    private List<Row> Flatten(ResourceRoot? root, OptionRef? option)
    {
        var rows = new List<Row>();
        if (root != null)
            AddTree(rows, root, 0);
        if (root?.Section != ResourceSection.Metadata)
            rows.Add(new Row(RowKind.AddFile, $"{root?.Id ?? "none"}/+file", 0, Root: root, Option: option ?? default));
        return rows;
    }

    /// <summary>The rows of a group: each option as a header, its files under it, then a place to drop files for new options.</summary>
    private List<Row> FlattenGroup(ModGroupView group)
    {
        var rows = new List<Row>();
        var groupKey = ResourceGraph.GroupRootId(group.Id);
        foreach (var set in group.FileSets)
        {
            var root = _session.Graph.OptionRoot(set.Ref);
            var key = $"{groupKey}/{set.Ref.Serialize()}";
            bool open = root is { Nodes.Count: > 0 } && !_collapsed.Contains(key);
            rows.Add(new Row(RowKind.Option, key, 0, Open: open, Root: root, Option: set.Ref));
            if (open)
                AddTree(rows, root!, 1);
        }
        if (group.Type is ModGroupType.Single or ModGroupType.Multi)
            rows.Add(new Row(RowKind.AddOptions, $"{groupKey}/+options", 0));
        return rows;
    }

    private void AddTree(List<Row> rows, ResourceRoot root, int baseDepth)
    {
        var force = ForcedOpen(root);

        void Visit(ResourceNode node, string? role, int depth, string parentKey, HashSet<ResourceNode> ancestors)
        {
            var key = $"{parentKey}/{node.Id}";
            bool hasChildren = node.Children.Count > 0;
            bool open = hasChildren && (!_collapsed.Contains(key) || force.Contains(node));
            if (force.Contains(node) && _collapsed.Remove(key))
                _folding++;

            rows.Add(new Row(RowKind.Node, key, depth, node, role, Open: open, Root: root));
            if (!open || depth > 12 + baseDepth || ancestors.Contains(node))
                return;

            ancestors.Add(node);
            foreach (var child in node.Children)
                Visit(child.Node, child.Role, depth + 1, key, ancestors);
            ancestors.Remove(node);
        }

        var seen = new HashSet<ResourceNode>();
        foreach (var top in root.Nodes)
            Visit(top, null, baseDepth, root.Id, seen);
    }

    /// <summary>The nodes on the way to a pending "go to", which open even if folded.</summary>
    private HashSet<ResourceNode> ForcedOpen(ResourceRoot root)
    {
        var set = new HashSet<ResourceNode>();
        if (_session.PendingReveal is not { } reveal)
            return set;
        var target = _session.Graph.Find(reveal);
        if (target == null || !ResourceGraph.Reaches(root, target))
            return set;
        foreach (var node in _session.Graph.PathTo(target, root))
            if (node != target)
                set.Add(node);
        return set;
    }

    /// <summary>
    /// The selection target of a node as listed in what the centre shows. A file several entries share
    /// (a texture two sets use) is selected where it is being looked at, so picking it does not jump to
    /// whichever entry the graph listed it under first.
    /// </summary>
    private static Target TargetIn(Scope scope, ResourceNode node)
        => Target.ForResource(scope.Id, node.GamePath, node.Option.Serialize());

    /// <summary>Whether a target names this node: the same file in the same option.</summary>
    private static bool Names(Target target, ResourceNode node)
    {
        return target.Kind == TargetKind.Resource
            && string.Equals(target.Path, node.GamePath, StringComparison.OrdinalIgnoreCase)
            && OptionRef.Parse(target.Option) == node.Option;
    }

    private int RevealIndex(List<Row> rows)
    {
        if (_session.PendingReveal is not { } reveal)
            return -1;
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Node is { } node && Names(reveal, node) && _session.TakeScrollRequest(reveal))
                return i;
        }
        // A whole entry was asked for: it is shown by being open; nothing to scroll to.
        if (reveal.IsResourceRoot)
            _session.TakeScrollRequest(reveal);
        return -1;
    }

    private void DrawRow(Row row, Scope scope, float width, float height)
    {
        float indent = row.Depth * Theme.T(22);
        switch (row.Kind)
        {
            case RowKind.Node:
                DrawNode(row, scope, width, height);
                break;

            case RowKind.Option:
                DrawOptionRow(row, scope, width, height);
                break;

            case RowKind.AddFile:
            {
                var hint = row.Root?.Option is { } into
                    ? $"Drop files here to add them to {(into.IsDefault ? "the default files" : _session.Project?.Meta.OptionName(into))}, or click to pick them"
                    : "Drop files here to add them to this entry, or click to pick them";
                var zone = DropZone.Draw(row.Key, hint, FontAwesomeIcon.Plus,
                    new Vector2(width, height), new FileDropRule(FileDrop.RejectForAdding, _ => { }), indent: indent);
                var option = scope.Option;
                if (zone.Dropped is { } dropped)
                    _actions.BeginAdd(dropped.Files, row.Root, option);
                else if (zone.Clicked)
                    _actions.BrowseAdd(row.Root, option);
                break;
            }

            case RowKind.AddOptions when scope.Group is { } group:
            {
                var why = PenumbraModMeta.WhyNoNewOption(group);
                var zone = DropZone.Draw(row.Key, why ?? "Drop files here to make one new option of each, or click to pick them", FontAwesomeIcon.Plus,
                    new Vector2(width, height), new FileDropRule(p => why ?? FileDrop.RejectForAdding(p), _ => { }), indent: indent);
                if (zone.Dropped is { } dropped)
                    _actions.BeginAddToGroup(dropped.Files, group.Id);
                else if (zone.Clicked && why == null)
                    _actions.BrowseAddToGroup(group.Id);
                break;
            }
        }
    }

    /// <summary>One option of a group listed in the centre: picking it shows it in the inspector; double-clicking opens it alone.</summary>
    private void DrawOptionRow(Row row, Scope scope, float width, float height)
    {
        var meta = _session.Project!.Meta;
        var option = row.Option;
        var root = row.Root;
        var settings = _editor.Settings;
        var group = scope.Group;

        var badges = new List<Badge>();
        if (settings.IsOn(option) is { } on)
            badges.Add(new Badge(on ? "in use" : "off", on ? Theme.Good : Theme.Faint));
        if (root is { ReplacedCount: > 0 })
            badges.Add(new Badge($"{root.ReplacedCount} of yours", Theme.Good));
        if (root is { MissingCount: > 0 })
            badges.Add(new Badge($"{root.MissingCount} missing", Theme.Bad));

        bool named = group is { Type: ModGroupType.Single or ModGroupType.Multi };
        var actions = new List<TileAction>
        {
            new(FontAwesomeIcon.Plus, "Add files to this option", () => _actions.BrowseAdd(root, option)),
        };
        if (named)
            actions.Add(new TileAction(FontAwesomeIcon.Pen, "Rename the option", () => _actions.BeginRenameOption(option)));

        var target = Target.ForResource(scope.Id, string.Empty, option.Serialize());
        var result = Tile.Draw(new TileSpec
        {
            Id        = row.Key,
            Expanded  = root is { Nodes.Count: > 0 } ? row.Open : null,
            Title     = meta.OptionName(option),
            Subtitle  = $"{root?.FileCount ?? 0} file(s)" + (root is { SwapCount: > 0 } ? $" · {root.SwapCount} swap(s)" : string.Empty),
            Detail    = meta.OptionDescription(option) is { Length: > 0 } description ? description.Replace('\n', ' ') : null,
            Icon      = ResourceStyle.OptionIcon(group?.Type),
            IconColor = settings.IsOn(option) == true ? Theme.Good : Theme.Muted,
            DrawThumb = Thumb(root),
            Badges    = badges,
            Actions   = actions,
            Selected  = _session.Selection == target,
            Drop      = new FileDropRule(FileDrop.RejectForAdding, _ => { }),
            Tooltip   = $"{meta.OptionLabel(option)}\nDouble-click to open it on its own.",
        }, new Vector2(width, height));

        if (result.Toggled)
        {
            if (!_collapsed.Add(row.Key))
                _collapsed.Remove(row.Key);
            _folding++;
        }
        else if (result.DoubleClicked)
        {
            _session.FocusOption(option);
        }
        else if (result.Clicked)
        {
            _session.Selection = target;
        }
        if (result.Dropped is { } dropped)
            _actions.BeginAdd(dropped.Files, root, option);
    }

    private void DrawNode(Row row, Scope scope, float width, float height)
    {
        var node = row.Node!;
        var target = TargetIn(scope, node);
        var replacement = node.Replacement;

        var badges = new List<Badge>();
        if (row.Role != null)
            badges.Add(new Badge(row.Role, Theme.Accent));
        if (node.Origin != OriginKind.ModFile)
            badges.Add(new Badge(ResourceStyle.OriginLabel(node.Origin), ResourceStyle.OriginColor(node.Origin), ResourceStyle.OriginHelp(node.Origin)));
        if (node.IsModContent && !node.Option.IsDefault)
            badges.Add(new Badge(OptionBadgeText(node), Theme.Muted, _session.Project?.Meta.OptionLabel(node.Option)));
        if (node.IsModContent && _editor.Settings.IsOn(node.Option) == false)
            badges.Add(new Badge("off", Theme.Faint,
                $"Not in use for you right now — in {_editor.Settings.Collection}, " +
                (_editor.Settings.Enabled == false ? "the mod is disabled." : "this option is not picked.")));
        bool fromFile = replacement is { Authored: null } && !string.IsNullOrWhiteSpace(replacement.SourcePath);
        if (replacement?.Authored != null)
            badges.Add(new Badge(node.Origin == OriginKind.ModFile ? "edited here" : "made here", Theme.Good,
                "Made here: its shader and texture slots are set up in the inspector, and it is written into the mod as you change them."));
        else if (fromFile && node.Origin != OriginKind.Added)
            badges.Add(new Badge(node.Origin is OriginKind.Vanilla or OriginKind.Missing ? "provided" : "replaced", Theme.Good,
                $"Made from {replacement!.SourcePath}."));
        if (replacement is { ReassignsMaterials: true })
            badges.Add(new Badge("materials reassigned", Theme.Good,
                string.Join("\n", replacement.MaterialNames.Select((n, i) => (n, i)).Where(s => !string.IsNullOrWhiteSpace(s.n)).Select(s => $"Slot {s.i + 1} → {s.n}"))));
        if (node.Pinned)
            badges.Add(new Badge("from the game", Theme.Accent, "Listed because its item was added to the mod from the game. Drop a file on it to put one of your own here."));

        var conflict = ConflictOf(node);
        if (conflict != null)
            badges.Add(new Badge("changed elsewhere", Theme.Warn,
                $"{conflict.Reason}.\n\nUse the buttons on the right: write what is set up here over it, or keep the changed file."));
        else if (node.EditedElsewhere)
            badges.Add(new Badge("edited elsewhere", Theme.Accent, EditedElsewhereHelp(node)));

        var actions = new List<TileAction>();
        if (conflict != null)
        {
            actions.Add(new TileAction(FontAwesomeIcon.Undo, "Keep the file as it was changed elsewhere",
                () => _editor.Sync.Resolve(conflict, ConflictChoice.KeepTheirs), Theme.Warn));
            actions.Add(new TileAction(FontAwesomeIcon.Check, "Write what is set up here over it",
                () => _editor.Sync.Resolve(conflict, ConflictChoice.UseMine)));
        }
        // Instant Edit's hand-off first: a model or animation to Blender, a texture to the image editor.
        if (_handoffs.For(node) is { } handoff)
            actions.Add(new TileAction(handoff.Icon, handoff.Block is { } why ? $"{handoff.Label}. {why}" : handoff.Label, handoff.Run,
                Enabled: handoff.Block == null));
        FileDropRule? drop = null;
        if (node.AuthoredSlot is { } slot)
        {
            // A texture of a material made here: dropping or picking sets that slot's image.
            drop = _textures.DropRule(slot);
            actions.Add(new TileAction(FontAwesomeIcon.FolderOpen, "Pick the image this texture is made from", () => BrowseSlot(slot)));
        }
        else if (ResourceActions.CanReplace(node))
        {
            drop = _actions.DropRuleFor(node);
            if (replacement is { OnlyReassigns: true })
                actions.Add(new TileAction(FontAwesomeIcon.Undo, "Give every slot the material its file has", () => _session.ResetMaterials(node), Theme.Warn));
            else if (replacement != null)
                actions.Add(new TileAction(FontAwesomeIcon.Undo, node.Origin == OriginKind.Added ? "Don't add this file" : "Keep the mod's own file",
                    () => _actions.ClearReplacement(node), Theme.Warn));
            actions.Add(new TileAction(FontAwesomeIcon.FolderOpen, $"Replace {node.FileName} with a file of your own", () => _actions.BrowseReplacement(node)));
        }

        var worst = WorstFor(node);
        var issueText = worst == null ? null : IssuesText(i => Names(i.Target, node));
        if (CheckMark(node) is { } mark)
        {
            if (worst == null || mark.Severity > worst.Value)
                worst = mark.Severity;
            issueText = issueText == null ? $"Check: {mark.Title}" : $"{issueText}\nCheck: {mark.Title}";
        }
        var result = Tile.Draw(new TileSpec
        {
            Id          = row.Key,
            Indent      = row.Depth * Theme.T(22),
            Expanded    = node.Children.Count > 0 ? row.Open : null,
            Title       = node.FileName,
            TitleColor  = node.Origin == OriginKind.Missing ? Theme.Bad : null,
            Subtitle    = Subtitle(node),
            SubtitleColor = node.Error != null ? Theme.Warn : null,
            Detail      = fromFile ? $"← {replacement!.SourcePath}" : node.GamePath,
            DetailColor = fromFile ? Theme.Good : null,
            Icon        = ResourceStyle.Icon(node.Kind),
            IconColor   = ResourceStyle.OriginColor(node.Origin),
            DrawThumb   = NodeThumb(node),
            Badges      = badges,
            Actions     = actions,
            Selected    = _session.Selection == target,
            Dimmed      = node.Origin == OriginKind.Vanilla && replacement == null,
            Issue       = worst,
            IssueText   = issueText,
            Drop        = drop,
            Tooltip     = node.GamePath,
        }, new Vector2(width, height));

        if (result.Toggled || result.DoubleClicked && node.Children.Count > 0)
        {
            if (!_collapsed.Add(row.Key))
                _collapsed.Remove(row.Key);
            _folding++;
        }
        else if (result.Clicked)
        {
            _session.Selection = target;
        }
        if (result.RightClicked)
        {
            _session.Selection = target;
            _menuNode = node;
            _openPopup = NodeMenuId;
        }
        if (result.Dropped is { } dropped && drop != null)
        {
            drop.OnDrop(dropped);
            _session.Selection = target;
        }
    }

    /// <summary>A file changed both here and elsewhere, waiting for a choice.</summary>
    private WriteConflict? ConflictOf(ResourceNode node)
        => node.IsModContent ? _editor.Sync.ConflictFor(node.Option, node.GamePath) : null;

    internal static string EditedElsewhereHelp(ResourceNode node)
        => node.Kind == ResourceKind.Material
            ? "Changed outside XIV Instant Edit since it was written here (in Penumbra's material editor, say). It is kept as it is: " +
              "only a change to its replacement here touches it again, and then only its textures are carried over onto it."
            : "Changed outside XIV Instant Edit since it was written here. It is kept as it is: only a change to its replacement here " +
              "touches it again, and then you are asked which to keep.";

    private string OptionBadgeText(ResourceNode node)
    {
        var meta = _session.Project?.Meta;
        if (meta == null)
            return "option";
        var name = meta.OptionName(node.Option);
        return name.Length > 24 ? name[..22] : name;
    }

    /// <summary>The line under a tile's title: what the file is, its format, and what is notable about it.</summary>
    private string Subtitle(ResourceNode node)
    {
        var parts = new List<string> { ResourceKinds.Label(node.Kind) };
        if (node.Texture is { } tex)
            parts.Add($"{tex.FormatName} {tex.Size}");
        if (node.Size > 0 && node.Origin == OriginKind.ModFile)
            parts.Add(FormatSize(node.Size));
        if (node.Origin == OriginKind.Swap)
            parts.Add($"served by {node.SwapTarget}");
        if (node.Note != null)
            parts.Add(node.Note);
        if (node.Error != null)
            parts.Add($"could not be read: {node.Error}");
        if (node.Origin is OriginKind.ModFile or OriginKind.Added && node.Children.Count > 0)
            parts.Add($"{node.Children.Count} reference(s)");
        return string.Join(" · ", parts);
    }

    internal static string FormatSize(long bytes) => bytes switch
    {
        < 1024        => $"{bytes} B",
        < 1024 * 1024        => $"{bytes / 1024.0:0.#} KiB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):0.#} MiB",
        _                    => $"{bytes / (1024.0 * 1024.0 * 1024.0):0.##} GiB",
    };

    private Action<float>? NodeThumb(ResourceNode node)
    {
        if (!ShowThumbs || node.Kind is not (ResourceKind.Texture or ResourceKind.VfxTexture or ResourceKind.Interface))
            return null;

        if (node.AuthoredSlot is { } slot)
            return side => _thumbs.Draw(slot, side);

        if (node.Replacement is { } replacement && File.Exists(replacement.SourcePath))
            return side => _thumbs.DrawFile(replacement.SourcePath, side);

        switch (node.Origin)
        {
            case OriginKind.ModFile:
                return side => _thumbs.DrawFile(node.DiskPath, side);
            case OriginKind.Vanilla:
                return side => _thumbs.DrawGame(node.GamePath, side);
            default:
                return null;
        }
    }

    private void BrowseSlot(TextureSlot slot)
    {
        Gui.Dialogs.OpenFileDialog("Texture image", "Image files{.png,.jpg,.jpeg,.tga,.dds,.tex}", (ok, path) =>
        {
            if (!ok) return;
            slot.SourcePath = path;
            slot.UseVariants = slot.UseWhiteDummy = false;
            _session.TouchReplacements();
        });
    }

    /// <summary>The worst finding of the mod's last check about this file, while that check still matches the mod.</summary>
    private (Severity Severity, string Title)? CheckMark(ResourceNode node)
    {
        var check = _editor.Check;
        if (check.Report is null || check.IsStale)
            return null;
        return check.Markers(_session.Graph).TryGetValue(node.Id, out var mark) ? mark : null;
    }

    private string IssuesText(Func<PortIssue, bool> about)
        => string.Join("\n", _session.Issues.Where(about).Select(i => i.Message));

    /// <summary>
    /// The worst issue about a file of the mod. Matched by the file, not by the entry it is listed
    /// under, so a file shared by several entries shows its issues in each of them.
    /// </summary>
    private Severity? WorstFor(ResourceNode node)
    {
        Severity? worst = null;
        foreach (var issue in _session.Issues)
            if (Names(issue.Target, node) && (worst == null || issue.Severity > worst.Value))
                worst = issue.Severity;
        return worst;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Popups
    // ─────────────────────────────────────────────────────────────────────────

    private void DrawPopups()
    {
        if (_openPopup != null)
        {
            ImGui.OpenPopup(_openPopup);
            _openPopup = null;
        }

        if (!ImGui.BeginPopup(NodeMenuId))
            return;

        var node = _menuNode;
        if (node == null)
        {
            ImGui.EndPopup();
            return;
        }

        ImGui.TextColored(Theme.Muted, node.FileName);
        ImGui.Separator();
        if (ImGui.MenuItem("Copy game path"))
            ImGui.SetClipboardText(node.GamePath);

        if (_handoffs.For(node) is { } handoff)
        {
            ImGui.Separator();
            if (ImGui.MenuItem(handoff.Label, string.Empty, false, handoff.Block == null))
                handoff.Run();
            if (handoff.Block != null)
                Gui.Tooltip(handoff.Block);
        }

        if (ConflictOf(node) is { } conflict)
        {
            ImGui.Separator();
            if (ImGui.MenuItem("Use what is set up here"))
                _editor.Sync.Resolve(conflict, ConflictChoice.UseMine);
            Gui.Tooltip("Writes what is set up here over the change made elsewhere.");
            if (ImGui.MenuItem("Keep the file as it was changed elsewhere"))
                _editor.Sync.Resolve(conflict, ConflictChoice.KeepTheirs);
        }

        if (node.Origin == OriginKind.ModFile && File.Exists(node.DiskPath) && ImGui.MenuItem("Show in Explorer"))
            ShowInExplorer(node.DiskPath);

        if (ResourceActions.CanReplace(node) && node.AuthoredSlot == null)
        {
            ImGui.Separator();
            if (ImGui.MenuItem("Replace with a file of your own"))
                _actions.BrowseReplacement(node);
            if (node.Replacement is { OnlyReassigns: false } && ImGui.MenuItem(node.Origin == OriginKind.Added ? "Don't add this file" : "Keep the mod's own file"))
                _actions.ClearReplacement(node);
        }

        var own = _session.FindReplacement(node.GamePath, _session.OptionFor(node));
        if (node.Kind == ResourceKind.Model && node.MaterialSlots.Count > 0)
        {
            ImGui.Separator();
            if (ImGui.MenuItem("New material for it"))
                _actions.BeginNewMaterial(node, -1);
            Gui.Tooltip("Make a material of the mod's own for this model, from a preset or as a copy of one it uses. Its slots are pointed at materials in the inspector.");
            if (own is { ReassignsMaterials: true } && ImGui.MenuItem("Every slot as the file has it"))
                _session.ResetMaterials(node);
        }
        if (node.Kind == ResourceKind.Material && node.Origin is OriginKind.ModFile or OriginKind.Vanilla && own == null)
        {
            ImGui.Separator();
            if (ImGui.MenuItem("Edit this material here"))
            {
                _session.EditMaterial(node);
                _session.Selection = node.Target;
            }
            Gui.Tooltip("Starts from this material — its shader, colour table and textures — so you only swap what you want.");
        }

        if (node.Origin == OriginKind.ModFile)
        {
            ImGui.Separator();
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Bad);
            if (ImGui.MenuItem("Remove from the mod"))
                _actions.AskRemove(node);
            ImGui.PopStyleColor();
        }
        ImGui.EndPopup();
    }

    internal static void ShowInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"/select,\"{path}\"", UseShellExecute = true });
        }
        catch (Exception)
        {
            // Nothing useful to do; the path is in the tooltip.
        }
    }
}
