using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Services;
using InstantEdit.Studio.Services.Backups;
using InstantEdit.Studio.Services.Check;
using InstantEdit.Studio.Services.Project;
using InstantEdit.Studio.Windows.UI;

namespace InstantEdit.Studio.Windows.Studio;

/// <summary>
/// The mod window's Check view: why the mod's items might not show up, the repairs offered for
/// it, the item variants and races it covers, and (in later sections) its clean-up and backups.
/// The check itself runs in <see cref="CheckSession"/>; this only draws it and starts things.
/// </summary>
internal sealed partial class CheckView
{
    private readonly StudioHost _host;
    private readonly ModEditor _editor;
    private readonly ModSession _session;
    private readonly CheckInspector _inspector;

    private List<string> _drawnRepairs = new();
    private List<string> _shownRepairs = new();

    /// <summary>The fix last clicked, where a shift-click range starts.</summary>
    private string? _anchorRepair;

    /// <summary>The finding picked in the list, for the inspector.</summary>
    internal string? SelectedFinding { get; private set; }

    public CheckView(StudioHost host, ModEditor editor, ModSession session)
    {
        _host      = host;
        _editor    = editor;
        _session   = session;
        _inspector = new CheckInspector(this);
    }

    internal CheckSession Check => _editor.Check;

    public void DrawCanvas()
    {
        var check = Check;
        if (check.TakeCheckRequest())
            StartCheck();

        DrawTopRow(check);
        ImGui.Spacing();

        if (!ImGui.BeginTabBar("##checktabs"))
            return;
        var report = check.Report;
        var problems = report is null ? "Problems" : $"Problems ({report.Findings.Count(f => f.Severity != Severity.Info)})";
        if (ImGui.BeginTabItem($"{problems}###problems"))
        {
            DrawProblems(report);
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Items###items"))
        {
            DrawItems(report);
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Races###races"))
        {
            DrawRaces(report);
            ImGui.EndTabItem();
        }
        DrawMoreTabs();
        ImGui.EndTabBar();
    }

    public void DrawInspector() => _inspector.Draw();

    /// <summary>Tabs later sections add (clean-up, backups).</summary>
    partial void DrawMoreTabs();

    // ── Top row ──────────────────────────────────────────────────────────────

    private void DrawTopRow(CheckSession check)
    {
        using (Gui.Disabled(check.Busy || _session.Project == null))
        {
            if (Gui.IconTextButton(FontAwesomeIcon.Stethoscope, "Check the mod", "Trace every model, material and texture the mod's options use, and what the game would load."))
                StartCheck();
        }
        ImGui.SameLine();
        var useCollection = check.UseCollection;
        using (Gui.Disabled(!_host.PenumbraIpc.IsAvailable))
        {
            if (ImGui.Checkbox("Use my collection", ref useCollection))
                check.UseCollection = useCollection;
        }
        Gui.Tooltip("Also look at your Penumbra collection: whether the mod is on, which mod provides what this one lacks, and which of its files another mod replaces.");

        ImGui.SameLine(0, Theme.S(18));
        ImGui.AlignTextToFramePadding();
        if (check.Running && check.Progress is { } progress)
        {
            ImGui.TextColored(Theme.Accent, progress.Phase);
            ImGui.SameLine();
            ImGui.ProgressBar(progress.Total > 0 ? (float)progress.Current / progress.Total : 0, new Vector2(Theme.S(120), 0), string.Empty);
            ImGui.SameLine();
            if (Gui.IconButton(FontAwesomeIcon.Times, "cancelcheck", "Stop the check"))
                check.Cancel();
        }
        else if (check.OperationLabel is { } label)
        {
            ImGui.TextColored(Theme.Accent, check.Progress is { Total: > 1 } step ? $"{label} ({step.Current} of {step.Total})" : label);
            if (check.Progress is { Total: > 0 } shown)
            {
                ImGui.SameLine();
                ImGui.ProgressBar((float)shown.Current / shown.Total, new Vector2(Theme.S(120), 0), string.Empty);
            }
            ImGui.SameLine();
            if (Gui.IconButton(FontAwesomeIcon.Times, "canceloperation", "Stop. A batch stops after the file it is on; files already changed stay changed and can be restored."))
                check.Cancel();
        }
        else if (check.Report is { } report)
        {
            ImGui.TextColored(check.IsStale ? Theme.Warn : Theme.Faint,
                check.IsStale ? "The mod changed since it was checked. Check it again." : $"Checked at {report.At.ToLocalTime():HH:mm}");
        }
        else
        {
            Gui.Hint("Not checked yet.");
        }

        if (check.Error is { } error)
            ImGui.TextColored(Theme.Bad, error);
        if (check.LastOperation is { } last)
        {
            var lines = last.Message.Split('\n');
            ImGui.TextColored(last.Ok ? Theme.Good : Theme.Bad, lines[0]);
            if (lines.Length > 1)
                Gui.Tooltip(last.Message);
        }
    }

    private void StartCheck()
    {
        if (_session.Project is not { } project || Check.Busy)
            return;
        var input = CheckInput.From(project, _host.GameData.VanillaFiles, _host.GameData, _host.PlayerRace(), null);
        var ipc = _host.PenumbraIpc;
        var folder = project.FolderName;
        Check.Start(input, Check.UseCollection && ipc.IsAvailable ? () => PenumbraCollectionView.CaptureAsync(ipc, folder) : null);
        SelectedFinding = null;
    }

    // ── Problems ─────────────────────────────────────────────────────────────

    private static readonly (CheckCategory Category, string Label)[] Categories =
    {
        (CheckCategory.Materials,    "Models, materials and textures"),
        (CheckCategory.Variants,     "Item variants"),
        (CheckCategory.Races,        "Races"),
        (CheckCategory.Dependencies, "Other mods"),
        (CheckCategory.Structure,    "The mod's files and meta.json"),
        (CheckCategory.Leftovers,    "Leftovers"),
    };

    private void DrawProblems(ModCheckReport? report)
    {
        if (report is null)
        {
            Gui.HintWrapped("Check the mod to see why any of its items might not show up, and what can be fixed.");
            return;
        }
        if (report.Findings.Count == 0)
        {
            Gui.Icon(FontAwesomeIcon.CheckCircle, Theme.Good);
            ImGui.SameLine();
            ImGui.TextColored(Theme.Good, "Nothing found.");
        }

        // The fix checkboxes in the order drawn last frame, for shift-click ranges.
        (_shownRepairs, _drawnRepairs) = (_drawnRepairs, _shownRepairs);
        _drawnRepairs.Clear();

        var footer = ImGui.GetFrameHeightWithSpacing() * 2 + ImGui.GetStyle().ItemSpacing.Y;
        if (ImGui.BeginChild("##problems", new Vector2(-1, -footer), false))
        {
            foreach (var (category, label) in Categories)
            {
                var findings = report.Findings.Where(f => f.Category == category).ToArray();
                if (findings.Length == 0)
                    continue;
                var errors = findings.Count(f => f.Severity == Severity.Error);
                var warnings = findings.Count(f => f.Severity == Severity.Warning);
                var counts = errors > 0 ? $"{errors} error{(errors == 1 ? string.Empty : "s")}" : warnings > 0 ? $"{warnings} warning{(warnings == 1 ? string.Empty : "s")}" : $"{findings.Length}";
                var flags = category == CheckCategory.Leftovers && errors + warnings == 0 ? ImGuiTreeNodeFlags.None : ImGuiTreeNodeFlags.DefaultOpen;
                var open = ImGui.CollapsingHeader($"{label} ({counts})###cat{(int)category}", flags);
                DrawCategoryMenu(category, findings);
                if (!open)
                    continue;
                foreach (var finding in findings)
                    DrawFinding(report, finding);
            }
            foreach (var note in report.Notes)
                Gui.HintWrapped(note);
        }
        ImGui.EndChild();
        DrawApplyFooter(report);
    }

    private void DrawFinding(ModCheckReport report, CheckFinding finding)
    {
        ImGui.PushID(finding.Key);
        Gui.Icon(SeverityIcon(finding.Severity), Gui.SeverityColor(finding.Severity));
        ImGui.SameLine();
        if (ImGui.Selectable($"{finding.Title}##sel", SelectedFinding == finding.Key, ImGuiSelectableFlags.None,
                new Vector2(ImGui.CalcTextSize(finding.Title).X, 0)))
        {
            SelectedFinding = finding.Key;
            SelectedTexture = null;
        }
        Gui.Tooltip(finding.Detail);
        if (finding.Where is { } where)
        {
            ImGui.SameLine();
            ImGui.TextColored(Theme.Faint, where);
        }
        if (FindNode(finding) is { } node)
        {
            ImGui.SameLine();
            if (Gui.IconButton(FontAwesomeIcon.Sitemap, "showres", "Show in Resources"))
            {
                _session.GoToView(StudioView.Resources);
                _session.Focus(node.Target);
            }
        }

        var selection = Check.Selection;
        foreach (var id in finding.RepairIds)
        {
            if (report.Find(id) is not { } repair || selection is null)
                continue;
            ImGui.Indent(Theme.S(24));
            var ticked = selection.IsTicked(repair.Id);
            using (Gui.Disabled(Check.Busy))
            {
                if (ImGui.Checkbox($"{repair.Title}##{repair.Id}", ref ticked))
                    TickClicked(selection, repair.Id, ticked);
            }
            Gui.Tooltip($"{repair.Explanation}\n\nShift-click to set every fix from the last one you clicked.");
            _drawnRepairs.Add(repair.Id);
            if (repair.Candidates is { Count: > 1 } candidates)
            {
                ImGui.SameLine();
                ImGui.SetNextItemWidth(Theme.S(220));
                var current = selection.Choice(repair.Id) ?? candidates[0].Label;
                if (!Check.Busy && ImGui.BeginCombo($"##cand{repair.Id}", current))
                {
                    foreach (var candidate in candidates)
                    {
                        if (ImGui.Selectable($"{candidate.Label}##{candidate.Label}", candidate.Label == current))
                            selection.Choose(repair.Id, candidate.Label);
                        Gui.Tooltip($"{candidate.Confidence} confidence: {candidate.Reason}");
                    }
                    ImGui.EndCombo();
                }
            }
            ImGui.Unindent(Theme.S(24));
        }
        ImGui.PopID();
    }

    /// <summary>A click on a fix's checkbox; with Shift held, every fix shown between it and the last one clicked gets the same tick.</summary>
    private void TickClicked(RepairSelection selection, string id, bool ticked)
    {
        if (ImGui.GetIO().KeyShift && _anchorRepair is { } anchor)
            selection.SetMany(RepairSelection.Range(_shownRepairs, anchor, id), ticked);
        else
            selection.Set(id, ticked);
        _anchorRepair = id;
    }

    /// <summary>Right-click on a category header: tick or untick all of its fixes.</summary>
    private void DrawCategoryMenu(CheckCategory category, CheckFinding[] findings)
    {
        var ids = findings.SelectMany(f => f.RepairIds).Distinct().ToArray();
        if (ids.Length == 0 || Check.Selection is not { } selection)
            return;
        Gui.Tooltip("Right-click to tick or untick all fixes here.");
        if (!ImGui.BeginPopupContextItem($"##catmenu{(int)category}"))
            return;
        using (Gui.Disabled(Check.Busy))
        {
            if (ImGui.MenuItem(ids.Length == 1 ? "Tick the fix here" : $"Tick all {ids.Length} fixes here"))
                selection.SetMany(ids, true);
            if (ImGui.MenuItem("Untick all fixes here"))
                selection.SetMany(ids, false);
        }
        ImGui.EndPopup();
    }

    private void DrawApplyFooter(ModCheckReport report)
    {
        var selection = Check.Selection;
        if (selection is null)
            return;
        ImGui.Separator();
        var deleteReleased = selection.DeleteReleasedFiles;
        using (Gui.Disabled(Check.Busy))
        {
            if (ImGui.Checkbox("Also delete files the fixes leave unused", ref deleteReleased))
                selection.DeleteReleasedFiles = deleteReleased;
        }
        Gui.Tooltip("Files a removed or changed redirection pointed at, when nothing else uses them. They are backed up first.");

        var count = selection.Count;
        var why = count == 0 ? "Tick the fixes to apply."
            : Check.Busy ? "Wait until the check or the change under way is done."
            : RepairApplier.WhyNot(_session.Project, report, _editor.Sync.Busy, _host.PenumbraIpc.IsAvailable, Check.IsStale);
        using (Gui.Disabled(why != null))
        {
            if (Gui.PrimaryButton(FontAwesomeIcon.Wrench, count == 1 ? "Apply 1 fix" : $"Apply {count} fixes", Theme.S(200),
                    why ?? "Back up what changes, make the fixes, and check the mod again."))
                StartApply(report, selection);
        }
        if (why != null)
            Gui.Tooltip(why);

        using (Gui.Disabled(Check.Busy || report.Repairs.Count == 0))
        {
            ImGui.SameLine();
            if (Gui.IconTextButton(FontAwesomeIcon.CheckSquare, "Tick all", "Tick every fix in the list. Of two fixes that can't both be made, only the first."))
                selection.SetMany(report.Repairs.Select(repair => repair.Id), true);
            ImGui.SameLine();
            if (Gui.IconTextButton(FontAwesomeIcon.Square, "Untick all", "Untick every fix in the list."))
                selection.SetMany(report.Repairs.Select(repair => repair.Id), false);
        }
    }

    private void StartApply(ModCheckReport report, RepairSelection selection)
    {
        if (_session.Project is not { } project)
            return;
        _editor.Flush();
        var host = _host;
        // The ticks as they are now: the view stays drawn while the worker applies them.
        var chosen = selection.Snapshot();
        Check.StartOperation("Applying fixes", async token =>
        {
            if (BackupFolder.TryResolve(() => host.CacheRoot, out var problem) is not { } cacheRoot)
                return (false, problem);
            var result = await RepairApplier.ApplyAsync(project, report, chosen, host.Backups, cacheRoot, host.Reloader, host.Configuration, token,
                OnFramework).ConfigureAwait(false);
            return (result.Ok, result.Message);
        }, checkAfter: true);
    }

    /// <summary>Runs an action on the framework thread, which owns the open mod.</summary>
    internal static Task OnFramework(Action action) => StudioServices.Framework.RunOnFrameworkThread(action);

    // ── Items and races ──────────────────────────────────────────────────────

    private void DrawItems(ModCheckReport? report)
    {
        if (report is null || report.Variants.Count == 0)
        {
            Gui.HintWrapped(report is null ? "Check the mod to see its items' variants." : "The mod has no gear, weapon or monster models with variants.");
            return;
        }
        foreach (var table in report.Variants)
        {
            ImGui.PushID(table.ModelPath + table.Option.Serialize());
            Gui.SectionHeader(FileName(table.ModelPath), $"{table.ModelPath}\n{table.Where}");
            if (table.Note is { } note)
                Gui.HintWrapped(note);
            if (ImGui.BeginTable("##variants", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Variant", ImGuiTableColumnFlags.WidthFixed, Theme.S(56));
                ImGui.TableSetupColumn("Items");
                ImGui.TableSetupColumn("Set", ImGuiTableColumnFlags.WidthFixed, Theme.S(48));
                ImGui.TableSetupColumn("Mask", ImGuiTableColumnFlags.WidthFixed, Theme.S(48));
                ImGui.TableSetupColumn("Status");
                ImGui.TableHeadersRow();
                foreach (var row in table.Rows)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(row.Variant.ToString());
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(row.Items.Count > 0 ? string.Join(", ", row.Items) : "No item");
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"v{row.MaterialSet:D4}");
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"0x{row.AttributeMask:X3}");
                    ImGui.TableNextColumn();
                    ImGui.TextColored(row.Status is { } status ? Gui.SeverityColor(status) : Theme.Good, row.Summary);
                }
                ImGui.EndTable();
            }
            ImGui.PopID();
            ImGui.Spacing();
        }
    }

    private void DrawRaces(ModCheckReport? report)
    {
        if (report is null || report.Races.Count == 0)
        {
            Gui.HintWrapped(report is null ? "Check the mod to see which races wear its gear." : "The mod has no gear models made for a race.");
            return;
        }
        var selection = Check.Selection;
        foreach (var table in report.Races)
        {
            ImGui.PushID(table.Label);
            Gui.SectionHeader(table.Label, $"The mod ships models for {string.Join(", ", table.Shipped.Select(RaceInfo.NameOf))}.");
            if (ImGui.BeginTable("##races", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Race");
                ImGui.TableSetupColumn("Wears");
                ImGui.TableSetupColumn("This mod's model", ImGuiTableColumnFlags.WidthFixed, Theme.S(110));
                ImGui.TableSetupColumn("Skin");
                ImGui.TableHeadersRow();
                foreach (var row in table.Rows)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    var name = RaceInfo.NameOf(row.Race);
                    if (row.IsPlayer)
                        ImGui.TextColored(Theme.Accent, $"{name} (you)");
                    else
                        ImGui.TextUnformatted(name);
                    ImGui.TableNextColumn();
                    ImGui.TextColored(row.ViaFallback ? Theme.Muted : Theme.Faint, row.ViaFallback ? $"the {RaceInfo.NameOf(row.Loaded)} model" : "its own model");
                    ImGui.TableNextColumn();
                    if (row.RepairId is { } repairId && selection is not null)
                    {
                        var ticked = selection.IsTicked(repairId);
                        using (Gui.Disabled(Check.Busy))
                        {
                            if (ImGui.Checkbox($"##{repairId}", ref ticked))
                                selection.Set(repairId, ticked);
                        }
                        Gui.Tooltip(report.Find(repairId)?.Explanation);
                        ImGui.SameLine();
                    }
                    ImGui.TextColored(row.UsesModModel ? Theme.Good : Theme.Muted, row.UsesModModel ? "Yes" : "No");
                    ImGui.TableNextColumn();
                    ImGui.TextColored(Theme.Faint, row.SkinNote ?? string.Empty);
                }
                ImGui.EndTable();
            }
            ImGui.PopID();
            ImGui.Spacing();
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    internal Models.Resources.ResourceNode? FindNode(CheckFinding finding)
        => string.IsNullOrEmpty(finding.GamePath) ? null : _session.Graph.Find(finding.GamePath, finding.Option ?? OptionRef.Default);

    internal static FontAwesomeIcon SeverityIcon(Severity severity) => severity switch
    {
        Severity.Error   => FontAwesomeIcon.TimesCircle,
        Severity.Warning => FontAwesomeIcon.ExclamationTriangle,
        _                => FontAwesomeIcon.InfoCircle,
    };

    private static string FileName(string gamePath) => gamePath[(gamePath.LastIndexOf('/') + 1)..];
}
