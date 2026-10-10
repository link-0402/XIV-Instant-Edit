using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Services;
using InstantEdit.Studio.Windows.UI;

namespace InstantEdit.Studio.Windows.Build;

/// <summary>
/// Build mode's Overview: the mod the modpack builds, and every item in it as a grid of larger tiles
/// (a tile opens its set-up). Its inspector is the mod's details — name, description, author, version
/// and website, written into the built mod's meta.json.
/// </summary>
internal sealed class PackOverview
{
    private readonly StudioHost _host;
    private readonly PortSession _session;
    private readonly PackStructureView _structure;

    public PackOverview(StudioHost host, PortSession session, PackStructureView structure)
    {
        _host    = host;
        _session   = session;
        _structure = structure;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Canvas
    // ─────────────────────────────────────────────────────────────────────────

    public void DrawCanvas()
    {
        var cfg  = _host.Configuration;
        var name = string.IsNullOrWhiteSpace(_session.ModName) ? _session.DefaultModName() : _session.ModName.Trim();
        var description = _session.ModDescription.Replace("\r", string.Empty).Split('\n').FirstOrDefault(l => l.Trim().Length > 0);

        Tile.Draw(new TileSpec
        {
            Id          = "overview",
            Title       = name,
            TitleColor  = string.IsNullOrWhiteSpace(_session.ModName) ? Theme.Muted : null,
            Subtitle    = string.Join(" · ", new[] { cfg.ModAuthor, string.IsNullOrWhiteSpace(cfg.ModVersion) ? null : $"v{cfg.ModVersion}",
                              PortSession.SanitizeFolderName(name) }.Where(s => !string.IsNullOrWhiteSpace(s))),
            Detail      = description ?? "No description yet — set one on the right.",
            DetailColor = description != null ? Theme.Muted : Theme.Faint,
            Icon        = FontAwesomeIcon.BoxOpen,
            IconColor   = Theme.Accent,
            Large       = true,
            Selected    = true,
            Tooltip     = "One build makes one Penumbra mod out of every item below.",
        }, new Vector2(ImGui.GetContentRegionAvail().X, Tile.Height(large: true)));
        if (SubjectDrop.Target() is { } dropped)
            _session.AddItem(dropped);

        ImGui.Spacing();
        if (!_session.Loaded)
        {
            Gui.Hint("Reading the game's item list");
            return;
        }

        if (!ImGui.BeginChild("##overview", new Vector2(-1, -1), false))
        {
            ImGui.EndChild();
            return;
        }

        float width   = ImGui.GetContentRegionAvail().X;
        float spacing = ImGui.GetStyle().ItemSpacing.X;
        int columns   = Math.Max(1, (int)((width + spacing) / (Theme.T(290) + spacing)));
        var size      = new Vector2((width - spacing * (columns - 1)) / columns, Tile.Height(detail: true));

        ImGui.Dummy(new Vector2(0, Theme.S(2)));
        Gui.Icon(FontAwesomeIcon.Boxes, Theme.Accent);
        ImGui.SameLine();
        ImGui.TextColored(Theme.Accent, $"In this modpack  ({_session.Items.Count})");

        var items = _session.Items.ToList();
        for (int i = 0; i < items.Count; i++)
        {
            if (i % columns != 0) ImGui.SameLine(0, spacing);
            DrawItemTile(items[i], size);
        }

        if (items.Count % columns != 0) ImGui.SameLine(0, spacing);
        var zone = DropZone.Draw("+item", items.Count == 0 ? "Add gear or a feature from the game" : "Add more from the game",
            FontAwesomeIcon.Plus, size, null);
        if (zone.Clicked)
            _session.GoToStage(StageId.Browse);
        if (SubjectDrop.Target() is { } subject)
            _session.AddItem(subject);

        // The mod's groups of options, a tile each.
        ImGui.Spacing();
        ImGui.Dummy(new Vector2(0, Theme.S(6)));
        Gui.Icon(FontAwesomeIcon.ListUl, Theme.Accent);
        ImGui.SameLine();
        ImGui.TextColored(Theme.Accent, $"Groups of options  ({_session.Groups.Count})");
        Gui.Tooltip("What players pick between in Penumbra. Everything not put into an option is in the default files.");
        var groups = _session.Groups.ToList();
        for (int i = 0; i < groups.Count; i++)
        {
            if (i % columns != 0) ImGui.SameLine(0, spacing);
            DrawGroupTile(groups[i], size);
        }
        if (groups.Count % columns != 0) ImGui.SameLine(0, spacing);
        if (DropZone.Draw("+group", "New group of options", FontAwesomeIcon.FolderPlus, size, null).Clicked)
            _structure.BeginNewGroup();

        ImGui.EndChild();
        if (SubjectDrop.Target() is { } onPage)
            _session.AddItem(onPage);
    }

    private void DrawItemTile(PortItem item, Vector2 size)
    {
        var subject = item.Subject;
        var target  = new Target(TargetKind.Item, item.Key);
        var worst   = PortValidator.WorstUnder(_session.Issues, target);
        uint icon   = GameIcons.Of(subject);
        int textures = item.Materials.Sum(m => m.Textures.Count);

        var badges = new List<Badge>();
        if (!item.HasWork)
            badges.Add(new Badge("not set up", Theme.Faint, "Nothing is set for it yet: a build leaves it out."));

        var result = Tile.Draw(new TileSpec
        {
            Id        = $"item{item.Key}",
            Title     = subject.DisplayName,
            Subtitle  = $"{subject.KindLabel} · {subject.IdDisplay}",
            Detail    = $"{item.Models.Count} race model(s) · {item.Materials.Count} material(s) · {textures} texture(s)",
            Icon      = subject.Kind == SubjectKind.Gear ? FontAwesomeIcon.Tshirt : FontAwesomeIcon.User,
            IconColor = item.HasWork ? Theme.Accent : Theme.Muted,
            DrawThumb = icon != 0 ? side => GameIcons.Draw(icon, side) : null,
            Badges    = badges,
            Issue     = worst,
            IssueText = worst != null ? string.Join("\n", _session.Issues.Where(i => i.Target.IsWithin(target)).Select(i => i.Message)) : null,
            Tooltip   = "Click to set it up.",
        }, size);

        if (result.Clicked)
            _session.Focus(target);
    }

    private void DrawGroupTile(PackGroup group, Vector2 size)
    {
        var result = Tile.Draw(new TileSpec
        {
            Id        = $"group{group.Id}",
            Title     = group.Name,
            Subtitle  = group.Multi ? "Players pick any number" : "Players pick one",
            Detail    = group.Options.Count == 0 ? "no options yet" : string.Join(", ", group.Options.Select(o => o.Name)),
            Icon      = group.Multi ? FontAwesomeIcon.CheckSquare : FontAwesomeIcon.DotCircle,
            IconColor = Theme.Accent,
            Tooltip   = "Click to see its options and what goes into each.",
        }, size);
        if (result.Clicked)
            _session.Focus(Target.ForPackGroup(group.Id));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Inspector — the mod's details
    // ─────────────────────────────────────────────────────────────────────────

    public void DrawInspector()
    {
        var cfg = _host.Configuration;

        Gui.SectionHeader("Mod", "Written into the built mod's meta.json. One build makes one mod out of every item in the modpack.");

        string modName = _session.ModName;
        if (Gui.LabeledInput("Name", "##ModName", ref modName, 128, _session.DefaultModName(),
                "Name of the mod folder created under Penumbra's mod directory."))
        {
            _session.ModName = modName;
            _session.MarkDirty();
        }

        Gui.Label("Description");
        string description = _session.ModDescription;
        if (ImGui.InputTextMultiline("##ModDescription", ref description, 2048,
                new Vector2(-1, ImGui.GetTextLineHeight() * 5 + ImGui.GetStyle().FramePadding.Y * 2)))
        {
            _session.ModDescription = description;
            _session.MarkDirty();
        }
        if (string.IsNullOrEmpty(description))
            Gui.Tooltip($"Empty: \"Port of {_session.ModName}\" is used.");

        string author = cfg.ModAuthor, version = cfg.ModVersion, website = cfg.ModWebsite;
        bool changed = false;
        changed |= Gui.LabeledInput("Author",  "##ModAuthor",  ref author,  128, "", "Shared by every mod you build.");
        changed |= Gui.LabeledInput("Version", "##ModVersion", ref version, 32,  "1.0", "Shared by every mod you build.", Theme.S(120));
        changed |= Gui.LabeledInput("Website", "##ModWebsite", ref website, 256, "https://", "Shared by every mod you build.");
        if (changed)
        {
            cfg.ModAuthor  = author;
            cfg.ModVersion = version;
            cfg.ModWebsite = website;
            _session.MarkDirty();   // saved with the next session flush
        }

        ImGui.Spacing();
        ImGui.Spacing();
        Gui.SectionHeader("What gets built");
        var items = _session.Items;
        int models    = items.Sum(i => i.Models.Count);
        int modelsSet = items.Sum(i => i.Models.Count(m => !string.IsNullOrWhiteSpace(m.SourcePath) || m.UseVariants || m.UseDummy));
        int materials = items.Sum(i => i.Materials.Count);
        int textures  = items.Sum(i => i.Materials.Sum(m => m.Textures.Count));
        Row(FontAwesomeIcon.Boxes,   $"{items.Count} item(s), {items.Count(i => i.HasWork)} set up");
        Row(FontAwesomeIcon.Cube,    $"{modelsSet} of {models} race model(s) set");
        Row(FontAwesomeIcon.Palette, $"{materials} material(s), {textures} texture(s)");
        Row(FontAwesomeIcon.ListUl,  _session.Groups.Count == 0 ? "No groups of options: everything is in the default files" : $"{_session.Groups.Count} group(s) of options, {_session.Groups.Sum(g => g.Options.Count)} option(s)");

        int errors   = _session.Issues.Count(i => i.Severity == Severity.Error);
        int warnings = _session.Issues.Count(i => i.Severity == Severity.Warning);
        if (errors + warnings > 0)
        {
            ImGui.Spacing();
            Gui.IssueBadge(errors, warnings);
            ImGui.SameLine();
            if (ImGui.Button("Pre-flight checklist"))
                _session.GoToStage(StageId.Build);
        }
    }

    private static void Row(FontAwesomeIcon icon, string text)
    {
        Gui.Icon(icon, Theme.Muted);
        ImGui.SameLine(Theme.S(28));
        ImGui.TextUnformatted(text);
    }
}
