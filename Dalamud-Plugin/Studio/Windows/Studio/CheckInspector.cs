using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using InstantEdit.Studio.Windows.UI;

namespace InstantEdit.Studio.Windows.Studio;

/// <summary>The Check view's inspector: the picked finding in full, what to do about it, and its repairs with their reasons.</summary>
internal sealed class CheckInspector
{
    private readonly CheckView _view;

    public CheckInspector(CheckView view) => _view = view;

    public void Draw()
    {
        var report = _view.Check.Report;
        if (report is null)
        {
            Gui.HintWrapped("Check the mod, then pick a problem to see it here.");
            return;
        }
        if (_view.SelectedFinding is not { } key || report.Findings.FirstOrDefault(f => f.Key == key) is not { } finding)
        {
            DrawSummary(report);
            return;
        }

        Gui.Icon(CheckView.SeverityIcon(finding.Severity), Gui.SeverityColor(finding.Severity));
        ImGui.SameLine();
        using (Fonts.Title())
            ImGui.TextWrapped(finding.Title);
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextWrapped(finding.Detail);
        ImGui.Spacing();
        if (finding.Where is { } where)
            Gui.LabeledValue("Where", where);
        if (finding.GamePath is { } path)
        {
            Gui.Label("Game path");
            ImGui.TextColored(Theme.Muted, path);
            if (ImGui.IsItemClicked())
                ImGui.SetClipboardText(path);
            Gui.Tooltip("Click to copy it.");
        }
        if (finding.Manual is { } manual)
        {
            ImGui.Spacing();
            Gui.SectionHeader("What to do");
            ImGui.TextWrapped(manual);
        }

        var repairs = finding.RepairIds.Select(report.Find).OfType<Services.Check.Repair>().ToArray();
        if (repairs.Length == 0)
            return;
        ImGui.Spacing();
        Gui.SectionHeader("Fixes", "Fixes only change the mod's meta.json (and delete files nothing uses). Model and material files are never rewritten.");
        foreach (var repair in repairs)
        {
            ImGui.PushID(repair.Id);
            Gui.Icon(repair.Preselected ? FontAwesomeIcon.CheckCircle : FontAwesomeIcon.Circle, repair.Preselected ? Theme.Good : Theme.Muted);
            ImGui.SameLine();
            ImGui.TextWrapped(repair.Title);
            ImGui.TextColored(Theme.Faint, $"{repair.Confidence} confidence{(repair.Preselected ? ", ticked on its own" : string.Empty)}");
            ImGui.TextWrapped(repair.Explanation);
            foreach (var edit in repair.EditsFor(_view.Check.Selection?.Choice(repair.Id)))
                ImGui.BulletText(edit.Describe());
            foreach (var deletion in repair.Deletions)
                ImGui.BulletText($"Delete {deletion.RelPath}");
            if (repair.Candidates is { Count: > 1 } candidates)
            {
                Gui.Label("Candidates");
                foreach (var candidate in candidates.Take(12))
                    ImGui.BulletText($"{candidate.Label} ({candidate.Confidence}): {candidate.Reason}");
            }
            ImGui.PopID();
            ImGui.Spacing();
        }
    }

    private static void DrawSummary(Services.Check.ModCheckReport report)
    {
        using (Fonts.Title())
            ImGui.TextUnformatted("The check");
        ImGui.Separator();
        ImGui.Spacing();
        Gui.LabeledValue("Errors", report.Count(Services.Severity.Error).ToString());
        Gui.LabeledValue("Warnings", report.Count(Services.Severity.Warning).ToString());
        Gui.LabeledValue("Notes", report.Count(Services.Severity.Info).ToString());
        Gui.LabeledValue("Fixes offered", report.Repairs.Count.ToString());
        if (report.Collection is { } collection)
            Gui.LabeledValue("Collection", collection.ModEnabled == false ? $"{collection.Name} (the mod is off)" : collection.Name);
        ImGui.Spacing();
        Gui.HintWrapped("Pick a problem to see it in full.");
    }
}
