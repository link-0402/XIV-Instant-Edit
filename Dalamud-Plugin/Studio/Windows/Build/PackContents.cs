using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Services;
using InstantEdit.Studio.Windows.UI;

namespace InstantEdit.Studio.Windows.Build;

/// <summary>
/// The left pane of Build mode: the mod the modpack builds on top, then what is in it — laid out the
/// way the mod will be (its default files, then each group with its options, every item under the
/// place it goes into), or item by item. Picking an item opens its set-up in the centre, an option or
/// group what goes into it; the mod's tile opens the overview. Gear dragged out of the item browser
/// can be dropped anywhere on the pane, or on an option to go into it.
/// </summary>
internal sealed class PackContents
{
    private const string GroupMenuId  = "##packgroupmenu";
    private const string OptionMenuId = "##packoptionmenu";

    private readonly StudioHost _host;
    private readonly PortSession _session;
    private readonly PortEditor _editor;
    private readonly PackStructureView _structure;
    private readonly TextureThumbnailCache _thumbs;

    private string _filter = string.Empty;
    private readonly HashSet<Guid> _folded = new();

    private PackGroup? _menuGroup;
    private (PackGroup Group, PackOption Option)? _menuOption;
    private string? _openMenu;

    public PackContents(StudioHost host, PortSession session, PortEditor editor, PackStructureView structure, TextureThumbnailCache thumbs)
    {
        _host    = host;
        _session   = session;
        _editor    = editor;
        _structure = structure;
        _thumbs    = thumbs;
    }

    private bool ByOption => _host.Configuration.PackByOption;

    public void Draw()
    {
        DrawModTile();
        ImGui.Spacing();

        if (!_session.Loaded)
        {
            Gui.Icon(FontAwesomeIcon.Hourglass, Theme.Muted);
            ImGui.SameLine();
            Gui.Hint("Reading the game's item list");
            return;
        }

        DrawModeSwitch();
        if (_session.Items.Count > 6 || ByOption && _session.Groups.Sum(g => g.Options.Count) > 8)
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##filter", ByOption ? "Filter by option, name or id" : "Filter by name or id", ref _filter, 128);
            ImGui.Spacing();
        }

        // The whole list takes drops from the browser, so there is somewhere to aim at even when the
        // pack is still empty.
        if (ImGui.BeginChild("##items", new Vector2(-1, -ImGui.GetFrameHeightWithSpacing()), false))
        {
            if (ByOption)
                DrawStructure();
            else
                DrawItems();
        }
        ImGui.EndChild();
        if (SubjectDrop.Target() is { } dropped)
            _session.AddItem(dropped);
        DrawMenus();

        float spacing = ImGui.GetStyle().ItemSpacing.X;
        float w = (ImGui.GetContentRegionAvail().X - spacing) * 0.5f;
        if (Gui.IconTextButton(FontAwesomeIcon.Tshirt, "Add from the game", "Find gear or a character feature to port onto, and add it to the modpack.", w))
            _session.GoToStage(StageId.Browse);
        ImGui.SameLine();
        if (Gui.IconTextButton(FontAwesomeIcon.FolderPlus, "New group", "Add a group of options to the mod: players pick one of its options, or any number.", w))
            _structure.BeginNewGroup();
    }

    /// <summary>Two halves: list the modpack the way the mod will be laid out, or item by item.</summary>
    private void DrawModeSwitch()
    {
        var cfg = _host.Configuration;
        float spacing = ImGui.GetStyle().ItemSpacing.X * 0.5f;
        float w = (ImGui.GetContentRegionAvail().X - spacing) * 0.5f;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(spacing, ImGui.GetStyle().ItemSpacing.Y));
        if (Half(FontAwesomeIcon.ListUl, "By option", cfg.PackByOption, w,
                "The mod as it will be laid out: its default files, then each group and its options, with every item under the place it goes into."))
        {
            cfg.PackByOption = true;
            cfg.Save();
        }
        ImGui.SameLine();
        if (Half(FontAwesomeIcon.Tshirt, "By item", !cfg.PackByOption, w, "Every item of the modpack, one tile each."))
        {
            cfg.PackByOption = false;
            cfg.Save();
        }
        ImGui.PopStyleVar();
        ImGui.Spacing();

        static bool Half(FontAwesomeIcon icon, string label, bool active, float width, string help)
        {
            ImGui.PushStyleColor(ImGuiCol.Button,        active ? Theme.Accent with { W = 0.35f } : Theme.Band);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.Accent with { W = active ? 0.45f : 0.2f });
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,  Theme.Accent with { W = 0.55f });
            ImGui.PushStyleColor(ImGuiCol.Text,          active ? new Vector4(1, 1, 1, 1) : Theme.Muted);
            bool clicked = Dalamud.Interface.Components.ImGuiComponents.IconButtonWithText(icon, $"{label}##packmode", new Vector2(width, 0));
            ImGui.PopStyleColor(4);
            Gui.Tooltip(help);
            return clicked && !active;
        }
    }

    private void DrawModTile()
    {
        var cfg  = _host.Configuration;
        var name = string.IsNullOrWhiteSpace(_session.ModName) ? _session.DefaultModName() : _session.ModName.Trim();
        var subtitle = string.Join(" · ", new[] { cfg.ModAuthor, string.IsNullOrWhiteSpace(cfg.ModVersion) ? null : $"v{cfg.ModVersion}" }
            .Where(s => !string.IsNullOrWhiteSpace(s)));

        var badges = new List<Badge>();
        if (_host.Builds.LastReport is { } report)
        {
            bool ok = report.FatalError == null && !report.Cancelled && report.Failed == 0;
            badges.Add(new Badge(ok ? "built" : "last build failed", ok ? Theme.Good : Theme.Bad, report.Summary()));
        }
        if (_session.Groups.Count > 0)
            badges.Add(new Badge($"{_session.Groups.Count} group(s)", Theme.Muted, string.Join("\n", _session.Groups.Select(g => $"{g.Name} ({g.Options.Count} options)"))));

        int errors   = _session.Issues.Count(i => i.Severity == Severity.Error);
        int warnings = _session.Issues.Count(i => i.Severity == Severity.Warning);
        var result = Tile.Draw(new TileSpec
        {
            Id        = "mod",
            Title     = name,
            TitleColor = string.IsNullOrWhiteSpace(_session.ModName) ? Theme.Muted : null,
            Subtitle  = subtitle.Length > 0 ? subtitle : "New mod",
            Detail    = $"{_session.Items.Count} item(s) · builds {PortSession.SanitizeFolderName(name)}",
            Icon      = FontAwesomeIcon.BoxOpen,
            IconColor = Theme.Accent,
            Badges    = badges,
            Selected  = _session.ActiveStage == StageId.ModInfo,
            Issue     = errors > 0 ? Severity.Error : warnings > 0 ? Severity.Warning : null,
            IssueText = errors + warnings > 0 ? $"{errors} error(s), {warnings} warning(s) in the modpack" : null,
            Tooltip   = "The mod this modpack builds: its name, description and everything in it.",
        }, new Vector2(ImGui.GetContentRegionAvail().X, Tile.Height(detail: true)));

        if (result.Clicked)
            _session.GoToStage(StageId.ModInfo);
        if (SubjectDrop.Target() is { } dropped)
            _session.AddItem(dropped);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Item by item
    // ─────────────────────────────────────────────────────────────────────────

    private void DrawItems()
    {
        if (_session.Items.Count == 0)
        {
            Gui.HintWrapped(SubjectDrop.IsDragging
                ? "Drop it here to add it to the modpack."
                : "Nothing here yet. Add gear or a character feature from the game.");
            return;
        }

        var filter = _filter.Trim();
        float width = ImGui.GetContentRegionAvail().X;
        float height = Tile.Height(detail: true);
        bool any = false;
        foreach (var item in _session.Items.ToList())
        {
            if (filter.Length > 0 && !Matches(item.Subject, filter))
                continue;
            any = true;
            DrawItem(item, new Vector2(width, height), 0);
        }
        if (!any)
            Gui.Hint("Nothing matches the filter.");

        // Up and down move between items while the list has focus.
        if (ImGui.IsWindowFocused() && !ImGui.IsAnyItemActive()
            && (ImGui.IsKeyPressed(ImGuiKey.DownArrow) || ImGui.IsKeyPressed(ImGuiKey.UpArrow)))
        {
            var items = _session.Items.Where(i => filter.Length == 0 || Matches(i.Subject, filter)).ToList();
            int step = ImGui.IsKeyPressed(ImGuiKey.DownArrow) ? 1 : -1;
            int current = _session.CurrentItem is { } open ? items.IndexOf(open) : -1;
            int next = current < 0 ? (step > 0 ? 0 : items.Count - 1) : current + step;
            if (next >= 0 && next < items.Count)
                _session.Focus(new Target(TargetKind.Item, items[next].Key));
        }
    }

    private static bool Matches(PortSubject subject, string filter)
        => subject.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || subject.IdDisplay.Contains(filter, StringComparison.OrdinalIgnoreCase);

    private void DrawItem(PortItem item, Vector2 size, float indent)
    {
        var subject = item.Subject;
        var target  = new Target(TargetKind.Item, item.Key);
        var worst   = PortValidator.WorstUnder(_session.Issues, target);
        uint icon   = GameIcons.Of(subject);

        var badges = new List<Badge>();
        if (!item.HasWork)
            badges.Add(new Badge("not set up", Theme.Faint, "Nothing is set for it yet: a build leaves it out."));
        if (!ByOption)
            badges.With(PackPlaceUi.PlaceBadge(_session, _session.PlaceOf(item), PackPlace.Default));

        bool selected = _session.ActiveStage != StageId.ModInfo && !_structure.Shows(_session.Selection)
                     && _session.Selection is { } s && s.Item == item.Key;
        if (selected && _session.TakeScrollRequest(target))
            ImGui.SetScrollHereY(0.3f);

        var result = Tile.Draw(new TileSpec
        {
            Id        = $"item{item.Key}",
            Indent    = indent,
            Title     = subject.DisplayName,
            Subtitle  = $"{subject.KindLabel} · {subject.IdDisplay}",
            Detail    = $"{item.Models.Count} race model(s) · {item.Materials.Count} material(s)",
            Icon      = subject.Kind == SubjectKind.Gear ? FontAwesomeIcon.Tshirt : FontAwesomeIcon.User,
            IconColor = item.HasWork ? Theme.Accent : Theme.Muted,
            DrawThumb = icon != 0 ? side => GameIcons.Draw(icon, side) : null,
            Badges    = badges,
            Actions   = new[] { new TileAction(FontAwesomeIcon.Trash, "Remove from the modpack (its set-up is kept)", () => _editor.AskRemoveItem(item), Theme.Bad) },
            Selected  = selected,
            Issue     = worst,
            IssueText = worst != null ? string.Join("\n", _session.Issues.Where(i => i.Target.IsWithin(target)).Select(i => i.Message)) : null,
        }, size);

        if (result.Clicked)
            _session.Focus(target);
        if (SubjectDrop.Target() is { } dropped)
            _session.AddItem(dropped);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // By option
    // ─────────────────────────────────────────────────────────────────────────

    private void DrawStructure()
    {
        var filter = _filter.Trim();
        float width = ImGui.GetContentRegionAvail().X;
        float itemH = Tile.Height(detail: true);
        float tileH = Tile.Height(detail: false);
        float step  = Theme.T(16);

        bool ItemMatches(PortItem item) => filter.Length == 0 || Matches(item.Subject, filter);
        var byPlace = _session.Items.GroupBy(i => _session.PlaceOf(i)).ToDictionary(g => g.Key, g => g.ToList());

        // ── Default files ────────────────────────────────────────────────
        DrawPlaceTile(PackPlace.Default, null, null, new Vector2(width, tileH), 0);
        foreach (var item in byPlace.GetValueOrDefault(PackPlace.Default, new List<PortItem>()).Where(ItemMatches))
            DrawItem(item, new Vector2(width, itemH), step);
        if (_session.Items.Count == 0)
            Gui.HintWrapped(SubjectDrop.IsDragging ? "Drop it here to add it to the modpack." : "Nothing here yet. Add gear or a character feature from the game.");

        // ── Groups ───────────────────────────────────────────────────────
        foreach (var group in _session.Groups.ToList())
        {
            bool groupMatches = filter.Length == 0 || group.Name.Contains(filter, StringComparison.OrdinalIgnoreCase);
            var options = group.Options.Where(o => groupMatches || o.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || byPlace.GetValueOrDefault(PackPlace.Of(group.Id, o.Id))?.Any(ItemMatches) == true)
                .ToList();
            if (!groupMatches && options.Count == 0)
                continue;

            ImGui.Spacing();
            DrawGroupTile(group, new Vector2(width, tileH));
            if (_folded.Contains(group.Id) && filter.Length == 0)
                continue;
            if (group.Options.Count == 0)
            {
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + step);
                ImGui.TextColored(Theme.Faint, "No options yet — add one with +");
            }
            foreach (var option in options)
            {
                var place = PackPlace.Of(group.Id, option.Id);
                DrawPlaceTile(place, group, option, new Vector2(width, tileH), step);
                foreach (var item in byPlace.GetValueOrDefault(place, new List<PortItem>()).Where(ItemMatches))
                    DrawItem(item, new Vector2(width, itemH), step * 2);
            }
        }

        if (_session.Groups.Count == 0 && _session.Items.Count > 0)
        {
            ImGui.Spacing();
            Gui.HintWrapped("Everything goes into the mod's default files. Make a group (below) to give players options to pick between; " +
                           "a slot of variants gets a group of its own.");
        }
    }

    private void DrawGroupTile(PackGroup group, Vector2 size)
    {
        bool folded = _folded.Contains(group.Id);
        bool takes = !group.Multi || group.Options.Count < PortSession.MaxMultiOptions;
        var badges = new List<Badge> { new(group.Multi ? "pick any" : "pick one", Theme.Muted,
            group.Multi ? "Players pick any number of its options." : "Players pick one of its options.") };
        if (_session.VariantSlotsOf(group) is { Count: > 0 } slots)
            badges.Add(new Badge("variants", Theme.Accent, $"Filled by {string.Join(", ", slots.Select(s => s.Label))}."));

        var result = Tile.Draw(new TileSpec
        {
            Id        = $"grp{group.Id}",
            Expanded  = !folded,
            Title     = group.Name,
            Subtitle  = $"{group.Options.Count} option(s)",
            Icon      = group.Multi ? FontAwesomeIcon.CheckSquare : FontAwesomeIcon.DotCircle,
            IconColor = Theme.Accent,
            Badges    = badges,
            Actions   = new[] { new TileAction(FontAwesomeIcon.Plus, takes ? $"Add an option to {group.Name}" : $"At most {PortSession.MaxMultiOptions} options",
                () => _structure.BeginNewOption(group), Enabled: takes) },
            Selected  = _session.Selection is { Kind: TargetKind.Group } s && s.Root == group.Id.ToString("D"),
        }, size);

        if (result.Toggled)
        {
            if (!_folded.Add(group.Id))
                _folded.Remove(group.Id);
        }
        else if (result.Clicked)
        {
            _session.Focus(Target.ForPackGroup(group.Id));
        }
        if (result.RightClicked)
        {
            _menuGroup = group;
            _openMenu = GroupMenuId;
        }
    }

    /// <summary>The default files (no group), or one option: what goes into it, as a tile that takes gear dropped from the browser.</summary>
    private void DrawPlaceTile(string place, PackGroup? group, PackOption? option, Vector2 size, float indent)
    {
        var contents = _session.ContentsOf(place);
        int items = contents.Count(c => c.Kind == PlacedKind.Item);
        int own = contents.Count(c => c.Kind is PlacedKind.Model or PlacedKind.Material or PlacedKind.Texture);
        int variants = contents.Count(c => c.Kind == PlacedKind.Variant);
        var parts = new List<string>();
        if (items > 0) parts.Add($"{items} item(s)");
        if (own > 0) parts.Add($"{own} file(s) on their own");
        if (variants > 0) parts.Add($"{variants} variant file(s)");
        if (parts.Count == 0) parts.Add(option == null ? "nothing yet" : "nothing goes into it yet");

        var image = contents.FirstOrDefault(c => c.File != null && FileDrop.IsImage(c.File))?.File;
        var target = Target.ForPackOption(place);
        var result = Tile.Draw(new TileSpec
        {
            Id        = $"place{place}",
            Indent    = indent,
            Title     = option?.Name ?? "Default files",
            Subtitle  = option == null ? $"Always on · {string.Join(" · ", parts)}" : string.Join(" · ", parts),
            SubtitleColor = contents.Count == 0 && option != null ? Theme.Faint : null,
            Icon      = group == null ? FontAwesomeIcon.CheckCircle : group.Multi ? FontAwesomeIcon.CheckSquare : FontAwesomeIcon.DotCircle,
            IconColor = Theme.Muted,
            DrawThumb = image != null && _host.Configuration.ShowThumbnails ? side => _thumbs.DrawFile(image, side) : null,
            Selected  = _session.Selection == target,
            Tooltip   = option == null
                ? "The mod's default files: always in use while the mod is enabled. Drop gear from the item browser here to add it."
                : $"{group!.Name} › {option.Name}\nDrop gear from the item browser here to put it into this option.",
        }, size);

        if (result.Clicked)
            _session.Focus(target);
        if (result.RightClicked && group != null && option != null)
        {
            _menuOption = (group, option);
            _openMenu = OptionMenuId;
        }
        if (SubjectDrop.Target() is { } dropped)
            _structure.DropItemInto(dropped, place);
    }

    private void DrawMenus()
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
                if (ImGui.MenuItem("Add an option"))
                    _structure.BeginNewOption(group);
                if (ImGui.MenuItem("Move up"))
                    _session.MoveGroup(group, -1);
                if (ImGui.MenuItem("Move down"))
                    _session.MoveGroup(group, +1);
                ImGui.Separator();
                ImGui.PushStyleColor(ImGuiCol.Text, Theme.Bad);
                if (ImGui.MenuItem("Take the group out"))
                    _structure.AskRemove(group);
                ImGui.PopStyleColor();
            }
            ImGui.EndPopup();
        }

        if (ImGui.BeginPopup(OptionMenuId))
        {
            if (_menuOption is { } m)
            {
                ImGui.TextColored(Theme.Muted, $"{m.Group.Name} › {m.Option.Name}");
                ImGui.Separator();
                int index = m.Group.Options.IndexOf(m.Option);
                if (ImGui.MenuItem("Move up", string.Empty, false, index > 0))
                    _session.MoveOption(m.Group, index, -1);
                if (ImGui.MenuItem("Move down", string.Empty, false, index < m.Group.Options.Count - 1))
                    _session.MoveOption(m.Group, index, +1);
                ImGui.Separator();
                ImGui.PushStyleColor(ImGuiCol.Text, Theme.Bad);
                if (ImGui.MenuItem("Take the option out"))
                    _structure.AskRemove(m.Group, m.Option);
                ImGui.PopStyleColor();
            }
            ImGui.EndPopup();
        }
    }
}
