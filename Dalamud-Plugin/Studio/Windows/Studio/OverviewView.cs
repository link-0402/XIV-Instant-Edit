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
/// The centre when the mod itself is picked: what it is, and everything in it as a grid of larger
/// tiles — the way the mod is laid out (its default files, then each group with its options), or by
/// kind of file, following the contents pane. A tile opens what it holds.
/// </summary>
internal sealed class OverviewView
{
    private readonly StudioHost _host;
    private readonly ModEditor _editor;
    private readonly ModSession _session;
    private readonly TextureThumbnailCache _thumbs;
    private readonly ResourceActions _actions;

    private enum RowKind { Section, Tiles, Group, Options }
    private sealed record Row(RowKind Kind, ResourceSection Section, IReadOnlyList<ResourceRoot> Roots, int Count,
        ModGroupView? Group = null, IReadOnlyList<OptionRef>? Options = null);

    // The grid's rows, laid out again only when the graph, the mod's groups, the column count or the sizes change.
    private readonly List<Row> _rows = new();
    private List<float> _heights = new();
    private ResourceGraph? _rowsOf;
    private int _rowsColumns;
    private float _rowsTileH, _rowsSectionH;
    private bool _rowsByOption;
    private PenumbraModMeta? _rowsMeta;
    private int _rowsMetaRevision = -1;

    public OverviewView(ModEditor editor, ModSession session, TextureThumbnailCache thumbs, ResourceActions actions)
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
        if (_session.PendingReveal is { Kind: TargetKind.Project } reveal)
            _session.TakeScrollRequest(reveal);

        var meta = project.Meta;
        var imagePath = !string.IsNullOrWhiteSpace(meta.Image) ? PathNormalizer.ToDiskPath(project.FolderPath, meta.Image) : null;
        var description = meta.Description.Replace("\r", string.Empty).Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? string.Empty;
        var header = Tile.Draw(new TileSpec
        {
            Id        = "overview",
            Title     = string.IsNullOrWhiteSpace(meta.Name) ? project.FolderName : meta.Name,
            Subtitle  = string.Join(" · ", new[] { meta.Author, string.IsNullOrWhiteSpace(meta.Version) ? null : $"v{meta.Version}", project.FolderName }
                .Where(s => !string.IsNullOrWhiteSpace(s))),
            Detail    = description,
            DetailColor = Theme.Muted,
            Icon      = FontAwesomeIcon.BoxOpen,
            IconColor = Theme.Accent,
            DrawThumb = imagePath != null && File.Exists(imagePath) ? side => _thumbs.DrawFile(imagePath, side) : null,
            Large     = true,
            Selected  = _session.Selection is { Kind: TargetKind.Project },
            Drop      = new FileDropRule(FileDrop.RejectForAdding, _ => { }),
        }, new Vector2(ImGui.GetContentRegionAvail().X, Tile.Height(large: true)));
        if (header.Clicked)
            _session.Selection = new Target(TargetKind.Project);
        if (header.Dropped is { } dropped)
            _actions.BeginAdd(dropped.Files, null);

        ImGui.Spacing();
        var graph = _session.Graph;
        bool byOption = _session.ByOption;
        if (!byOption && graph.Roots.Count == 0)
        {
            Gui.HintWrapped(_editor.Graphs.IsBuilding
                ? "Reading the mod's files"
                : "This mod is empty. Drop files of your own onto this page to add them.");
            return;
        }

        if (!ImGui.BeginChild("##overview", new Vector2(-1, -1), false))
        {
            ImGui.EndChild();
            return;
        }

        float width = ImGui.GetContentRegionAvail().X;
        float spacing = ImGui.GetStyle().ItemSpacing.X;
        int columns = Math.Max(1, (int)((width + spacing) / (Theme.T(290) + spacing)));
        float tileW = (width - spacing * (columns - 1)) / columns;
        float tileH = Tile.Height(detail: true);

        float sectionH = ImGui.GetFrameHeightWithSpacing() + Theme.S(6);
        if (_rowsOf != graph || _rowsColumns != columns || _rowsTileH != tileH || _rowsSectionH != sectionH
            || _rowsByOption != byOption || _rowsMeta != meta || _rowsMetaRevision != meta.Revision)
        {
            _rowsOf           = graph;
            _rowsColumns      = columns;
            _rowsTileH        = tileH;
            _rowsSectionH     = sectionH;
            _rowsByOption     = byOption;
            _rowsMeta         = meta;
            _rowsMetaRevision = meta.Revision;
            _rows.Clear();
            if (byOption)
                LayOutOptions(meta, columns);
            else
                LayOutKinds(graph, columns);
            _heights = _rows.Select(r => r.Kind is RowKind.Section or RowKind.Group ? sectionH : tileH).ToList();
        }

        var rows = _rows;
        VirtualRows.Draw(_heights, i =>
        {
            var row = rows[i];
            switch (row.Kind)
            {
                case RowKind.Section:
                    ImGui.Dummy(new Vector2(0, Theme.S(6)));
                    Gui.Icon(ResourceStyle.SectionIcon(row.Section), Theme.Accent);
                    ImGui.SameLine();
                    ImGui.TextColored(Theme.Accent, $"{ResourceKinds.SectionLabel(row.Section)}  ({row.Count})");
                    break;

                case RowKind.Group:
                    DrawGroupHeading(row.Group);
                    break;

                case RowKind.Tiles:
                    for (int c = 0; c < row.Roots.Count; c++)
                    {
                        if (c > 0) ImGui.SameLine(0, spacing);
                        DrawRootTile(row.Roots[c], new Vector2(tileW, tileH));
                    }
                    break;

                case RowKind.Options:
                    for (int c = 0; c < row.Options!.Count; c++)
                    {
                        if (c > 0) ImGui.SameLine(0, spacing);
                        DrawOptionTile(row.Options[c], row.Group, meta, graph, new Vector2(tileW, tileH));
                    }
                    break;
            }
        });
        ImGui.EndChild();
    }

    private void LayOutKinds(ResourceGraph graph, int columns)
    {
        foreach (var section in graph.Roots.GroupBy(r => r.Section).OrderBy(g => g.Key))
        {
            var roots = section.ToList();
            _rows.Add(new Row(RowKind.Section, section.Key, Array.Empty<ResourceRoot>(), roots.Count));
            for (int i = 0; i < roots.Count; i += columns)
                _rows.Add(new Row(RowKind.Tiles, section.Key, roots.GetRange(i, Math.Min(columns, roots.Count - i)), 0));
        }
    }

    private void LayOutOptions(PenumbraModMeta meta, int columns)
    {
        _rows.Add(new Row(RowKind.Group, default, Array.Empty<ResourceRoot>(), 1));
        _rows.Add(new Row(RowKind.Options, default, Array.Empty<ResourceRoot>(), 0, Options: new[] { OptionRef.Default }));
        foreach (var group in meta.Groups)
        {
            _rows.Add(new Row(RowKind.Group, default, Array.Empty<ResourceRoot>(), group.FileSets.Count, group));
            var sets = group.FileSets.Select(o => o.Ref).ToList();
            for (int i = 0; i < sets.Count; i += columns)
                _rows.Add(new Row(RowKind.Options, default, Array.Empty<ResourceRoot>(), 0, group, sets.GetRange(i, Math.Min(columns, sets.Count - i))));
        }
    }

    /// <summary>A group's heading (or the default files' when <paramref name="group"/> is null): clicking it opens the group.</summary>
    private void DrawGroupHeading(ModGroupView? group)
    {
        ImGui.Dummy(new Vector2(0, Theme.S(6)));
        Gui.Icon(group != null ? ResourceStyle.GroupIcon(group.Type) : FontAwesomeIcon.CheckCircle, Theme.Accent);
        ImGui.SameLine();
        if (group == null)
        {
            ImGui.TextColored(Theme.Accent, "Default files");
            ImGui.SameLine();
            ImGui.TextColored(Theme.Faint, "always in use");
            return;
        }

        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Accent);
        bool open = ImGui.Selectable($"{group.Name}  ({group.Options.Count})##heading{group.Id}", false, ImGuiSelectableFlags.None,
            new Vector2(ImGui.CalcTextSize($"{group.Name}  ({group.Options.Count})").X, 0));
        ImGui.PopStyleColor();
        Gui.Tooltip($"{ResourceStyle.GroupTypeHelp(group.Type)}\nClick to see its options with their files. Drop files here to make one option of each.");
        if (FileDrop.Target(p => PenumbraModMeta.WhyNoNewOption(group) ?? FileDrop.RejectForAdding(p), out var dropped))
            _actions.BeginAddToGroup(dropped.Files, group.Id);
        if (open)
            _session.FocusGroup(group.Id);
        ImGui.SameLine();
        ImGui.TextColored(Theme.Faint, ResourceStyle.GroupTypeLabel(group.Type));
    }

    private void DrawRootTile(ResourceRoot root, Vector2 size)
    {
        var badges = new List<Badge>();
        if (root.ReplacedCount > 0) badges.Add(new Badge($"{root.ReplacedCount} replaced", Theme.Good));
        if (root.MissingCount > 0) badges.Add(new Badge($"{root.MissingCount} missing", Theme.Bad));
        var worst = PortValidator.WorstUnder(_session.Issues, root.Target);

        var result = Tile.Draw(new TileSpec
        {
            Id        = root.Id,
            Title     = root.Title,
            Subtitle  = root.Subtitle,
            Detail    = $"{root.FileCount} file(s)" + (root.Options.Count > 1 ? $" · {root.Options.Count} options" : string.Empty),
            Icon      = ResourceStyle.Icon(root.Kind),
            IconColor = Theme.Muted,
            DrawThumb = Thumb(root),
            Badges    = badges,
            Issue     = worst,
            Drop      = new FileDropRule(FileDrop.RejectForAdding, _ => { }),
        }, size);

        if (result.Clicked)
            _session.Focus(root.Target);
        if (result.Dropped is { } dropped)
            _actions.BeginAdd(dropped.Files, root);
    }

    private void DrawOptionTile(OptionRef option, ModGroupView? group, PenumbraModMeta meta, ResourceGraph graph, Vector2 size)
    {
        var root = graph.OptionRoot(option);
        var settings = _editor.Settings;
        bool? on = settings.IsOn(option);

        var badges = new List<Badge>();
        if (on is { } active)
            badges.Add(new Badge(active ? "in use" : "off", active ? Theme.Good : Theme.Faint));
        if (root is { ReplacedCount: > 0 }) badges.Add(new Badge($"{root.ReplacedCount} of yours", Theme.Good));
        if (root is { MissingCount: > 0 }) badges.Add(new Badge($"{root.MissingCount} missing", Theme.Bad));

        var parts = new List<string> { $"{root?.FileCount ?? 0} file(s)" };
        if (root is { SwapCount: > 0 }) parts.Add($"{root.SwapCount} swap(s)");
        if (root is { ManipulationCount: > 0 }) parts.Add($"{root.ManipulationCount} manipulation(s)");

        var result = Tile.Draw(new TileSpec
        {
            Id        = $"opt{option.Serialize()}",
            Title     = option.IsDefault ? "Default files" : meta.OptionName(option),
            Subtitle  = string.Join(" · ", parts),
            Detail    = option.IsDefault ? "Always in use while the mod is enabled" : meta.OptionDescription(option).Replace('\n', ' '),
            Icon      = ResourceStyle.OptionIcon(group?.Type),
            IconColor = on == true ? Theme.Good : Theme.Muted,
            DrawThumb = Thumb(root),
            Badges    = badges,
            Drop      = new FileDropRule(FileDrop.RejectForAdding, _ => { }),
            Tooltip   = meta.OptionLabel(option),
        }, size);

        if (result.Clicked)
            _session.FocusOption(option);
        if (result.Dropped is { } dropped)
            _actions.BeginAdd(dropped.Files, root, option);
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
}
