using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Services;
using InstantEdit.Studio.Windows.Panels;
using InstantEdit.Studio.Windows.UI;

namespace InstantEdit.Studio.Windows.Build;

/// <summary>
/// Build mode's Build view: where the mod goes, the build's progress, and then the report — every
/// file it wrote or could not write, as tiles that jump to the set-up that produced them. The
/// inspector beside it is the pre-flight checklist.
/// </summary>
internal sealed class BuildView
{
    private readonly StudioHost _host;
    private readonly PortSession _session;
    private readonly BuildController _builds;
    private bool _problemsOnly;

    public BuildView(StudioHost host, PortSession session, BuildController builds)
    {
        _host  = host;
        _session = session;
        _builds  = builds;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Canvas
    // ─────────────────────────────────────────────────────────────────────────

    public void DrawCanvas()
    {
        if (_session.Items.Count == 0)
        {
            Gui.Icon(FontAwesomeIcon.ArrowLeft, Theme.Muted);
            ImGui.SameLine();
            Gui.HintWrapped("Add at least one item to the modpack first — find one with \"Add from the game\".");
            return;
        }

        DrawTarget();
        ImGui.Spacing();
        DrawControls();
        ImGui.Spacing();
        ImGui.Spacing();

        var report = _builds.LastReport;
        if (report == null)
        {
            Gui.HintWrapped("No build yet. Building converts every texture, writes the materials and models for every item in the modpack, " +
                           "and adds the new mod to Penumbra.");
            ImGui.Spacing();
            Gui.HintWrapped("The mod it makes is a plain Penumbra mod: none of this set-up goes into it. A build only makes new mods. " +
                           "To change one later, open it from the library.");
            return;
        }

        DrawReport(report);
    }

    /// <summary>The mod the build makes, as a tile: its name and the folder it is written to.</summary>
    private void DrawTarget()
    {
        var name = PortSession.SanitizeFolderName(string.IsNullOrWhiteSpace(_session.ModName)
            ? _session.DefaultModName() : _session.ModName.Trim());
        var root = _host.PenumbraIpc.IsAvailable ? _host.PenumbraIpc.GetModDirectory() : null;
        bool known = !string.IsNullOrEmpty(root);

        var result = Tile.Draw(new TileSpec
        {
            Id          = "target",
            Title       = name,
            Subtitle    = $"{_session.Items.Count} item(s): {string.Join(", ", _session.Items.Select(i => i.Subject.DisplayName))}",
            Detail      = known ? Path.Combine(root!, name) : "Penumbra not available — the mod folder cannot be resolved.",
            DetailColor = known ? null : Theme.Bad,
            Icon        = FontAwesomeIcon.BoxOpen,
            IconColor   = Theme.Accent,
            Actions     = new[] { new TileAction(FontAwesomeIcon.Pen, "Change the mod's name and details", () => _session.GoToStage(StageId.ModInfo)) },
            Tooltip     = "Every item in the modpack is written into this one mod folder.",
        }, new Vector2(ImGui.GetContentRegionAvail().X, Tile.Height(detail: true)));
        if (result.Clicked)
            _session.GoToStage(StageId.ModInfo);
    }

    private void DrawControls()
    {
        var job = _builds.Running;
        if (job != null)
        {
            var p = job.Progress;
            ImGui.ProgressBar(p.Fraction, new Vector2(-(Theme.S(90) + ImGui.GetStyle().ItemSpacing.X), 0),
                p.Total > 0 ? $"{p.Done}/{p.Total}" : "");
            ImGui.SameLine();
            if (Gui.IconTextButton(FontAwesomeIcon.Stop, "Cancel", "Stops after the current file.", Theme.S(90)))
                job.Cancel();
            ImGui.TextColored(Theme.Muted, p.Current);
            return;
        }

        bool can = _builds.CanBuild(out var reason);
        using (Gui.Disabled(!can))
        {
            if (Gui.PrimaryButton(FontAwesomeIcon.Hammer, "Build mod", Theme.S(160)))
                _builds.Start();
        }
        if (!can)
        {
            // A reason can be a sentence long (the folder this one would build into is already there).
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Warn);
            ImGui.TextWrapped(reason);
            ImGui.PopStyleColor();
            return;
        }

        int errors = _session.Issues.Count(i => i.Severity == Severity.Error);
        if (errors > 0)
        {
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Bad, $"{errors} problem(s) will make parts of the build fail — see the checklist.");
        }
    }

    private void DrawReport(BuildReport report)
    {
        var color = report.FatalError != null || report.Failed > 0 ? Theme.Bad
                  : report.Cancelled || report.Skipped > 0         ? Theme.Warn
                  : Theme.Good;
        var icon  = color == Theme.Good ? FontAwesomeIcon.CheckCircle
                  : color == Theme.Warn ? FontAwesomeIcon.ExclamationTriangle
                  : FontAwesomeIcon.TimesCircle;

        Gui.Icon(icon, color);
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextWrapped(report.Summary());
        ImGui.PopStyleColor();

        if (report.AddModResult != null)
        {
            if (report.RegisteredWithPenumbra)
                ImGui.TextColored(Theme.Muted, "Registered with Penumbra — enable it in your collection and redraw.");
            else
                ImGui.TextColored(Theme.Warn, $"Penumbra did not pick it up automatically ({report.AddModResult}) — run a mod rediscovery.");
        }

        ImGui.Spacing();
        if (Gui.IconTextButton(FontAwesomeIcon.Clipboard, "Copy report"))
        {
            ImGui.SetClipboardText(report.ToClipboardText());
            _session.Notify("Build report copied to the clipboard.", Severity.Info);
        }
        ImGui.SameLine();
        using (Gui.Disabled(!Directory.Exists(report.ModPath)))
        {
            if (Gui.IconTextButton(FontAwesomeIcon.FolderOpen, "Open mod folder"))
                OpenFolder(report.ModPath);
        }
        ImGui.SameLine();
        using (Gui.Disabled(report.FatalError != null || report.Cancelled || !Directory.Exists(report.ModPath)))
        {
            if (Gui.IconTextButton(FontAwesomeIcon.Edit, "Edit the built mod",
                    "Opens the mod as it is now in Penumbra's folder in a window of its own, to change any of its files. The modpack stays here."))
            {
                if (!_host.OpenMod(report.ModPath, out var error))
                    _session.Notify($"It could not be opened: {error}", Severity.Error);
            }
        }
        ImGui.SameLine();
        Gui.AlignRight(ImGui.GetFrameHeight() + ImGui.CalcTextSize("Problems only").X + ImGui.GetStyle().ItemInnerSpacing.X);
        ImGui.Checkbox("Problems only", ref _problemsOnly);

        ImGui.Spacing();
        var entries = _problemsOnly ? report.Entries.Where(e => e.Outcome != BuildOutcome.Ok).ToList() : report.Entries;
        if (entries.Count == 0)
        {
            Gui.Hint(_problemsOnly ? "Nothing was skipped or failed." : "The build produced no files.");
            return;
        }

        // Headings between items and categories, then one compact tile per file.
        var rows = new List<(BuildEntry? Entry, string? Heading, bool Item)>();
        uint? lastKey = null;
        BuildCategory? lastCategory = null;
        foreach (var e in entries)
        {
            if (lastKey != e.SubjectKey)
            {
                lastKey = e.SubjectKey;
                lastCategory = null;
                if (!string.IsNullOrEmpty(e.ItemName))
                    rows.Add((null, e.ItemName, true));
            }
            if (e.Category != lastCategory)
            {
                lastCategory = e.Category;
                rows.Add((null, CategoryTitle(e.Category), false));
            }
            rows.Add((e, null, false));
        }

        if (!ImGui.BeginChild("##report", new Vector2(-1, -1), false))
        {
            ImGui.EndChild();
            return;
        }

        float width = ImGui.GetContentRegionAvail().X;
        float tileH = Tile.Height();
        var heights = rows.Select(r => r.Entry != null ? tileH : ImGui.GetTextLineHeightWithSpacing() + Theme.S(4)).ToList();
        VirtualRows.Draw(heights, i =>
        {
            var row = rows[i];
            if (row.Entry == null)
            {
                ImGui.Dummy(new Vector2(0, Theme.S(4)));
                ImGui.TextColored(row.Item ? Theme.Accent : Theme.Muted, row.Heading);
                return;
            }

            var e = row.Entry;
            var (glyph, tint) = e.Outcome switch
            {
                BuildOutcome.Ok      => (FontAwesomeIcon.Check, Theme.Good),
                BuildOutcome.Skipped => (FontAwesomeIcon.Minus, Theme.Warn),
                _                    => (FontAwesomeIcon.Times, Theme.Bad),
            };
            var result = Tile.Draw(new TileSpec
            {
                Id            = $"entry{i}",
                Title         = e.Label,
                Subtitle      = string.IsNullOrEmpty(e.Detail) ? e.GamePath : e.Detail,
                SubtitleColor = e.Outcome == BuildOutcome.Failed ? Theme.Bad : null,
                Icon          = glyph,
                IconColor     = tint,
                Tooltip       = (e.GamePath ?? e.Label) + (e.Source != null ? "\n\nClick to open its set-up." : string.Empty),
            }, new Vector2(width, tileH));
            if (result.Clicked)
            {
                if (e.Source != null)
                    _session.Focus(e.Source.Value);
                else if (e.SubjectKey != 0)
                    _session.Focus(new Target(TargetKind.Item, e.SubjectKey));
            }
        });
        ImGui.EndChild();
    }

    private static string CategoryTitle(BuildCategory category) => category switch
    {
        BuildCategory.Model    => "Models",
        BuildCategory.Texture  => "Textures",
        BuildCategory.Variant  => "Variants",
        BuildCategory.Material => "Materials",
        _                      => "Mod metadata",
    };

    private void OpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _session.Notify($"Could not open the folder: {ex.Message}", Severity.Error);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Inspector — pre-flight checklist
    // ─────────────────────────────────────────────────────────────────────────

    public void DrawInspector()
    {
        Gui.SectionHeader("Pre-flight", "Checked continuously against every item's set-up and the files on disk. Click a line to go to it.");

        int errors   = _session.Issues.Count(i => i.Severity == Severity.Error);
        int warnings = _session.Issues.Count(i => i.Severity == Severity.Warning);
        if (errors + warnings > 0)
        {
            ImGui.TextColored(errors > 0 ? Theme.Bad : Theme.Warn, $"{errors} error(s), {warnings} warning(s)");
            ImGui.Spacing();
        }

        IssueList.Draw(_session.Issues, _session.Focus);

        ImGui.Spacing();
        ImGui.Spacing();
        Gui.SectionHeader("What gets built");
        var items = _session.Items;
        int models    = items.Sum(i => i.Models.Count);
        int materials = items.Sum(i => i.Materials.Count);
        int mtrls     = items.Sum(i => i.Materials.Count * i.Subject.MaterialRaces(i.Models).Count);
        int textures  = items.Sum(i => i.Materials.Sum(m => m.Textures.Count));
        int variants  = items.Sum(i => i.Materials.Sum(m => m.Textures.Count(t => t.UseVariants && !t.UseWhiteDummy)));
        Row(FontAwesomeIcon.Boxes,   $"{items.Count} item(s)");
        Row(FontAwesomeIcon.Cube,    $"{models} race model(s)");
        Row(FontAwesomeIcon.Palette, $"{mtrls} material file(s) from {materials} material(s)");
        Row(FontAwesomeIcon.Image,   $"{textures} texture slot(s){(variants > 0 ? $", {variants} with variants" : "")}");
        var groups = _session.Groups.Where(g => g.Options.Count > 0).ToList();
        Row(FontAwesomeIcon.ListUl,  groups.Count == 0
            ? "Everything in the default files — no groups of options"
            : $"{groups.Count} group(s) with {groups.Sum(g => g.Options.Count)} option(s): {string.Join(", ", groups.Select(g => g.Name))}");
    }

    private static void Row(FontAwesomeIcon icon, string text)
    {
        Gui.Icon(icon, Theme.Muted);
        ImGui.SameLine(Theme.S(28));
        ImGui.TextUnformatted(text);
    }
}
