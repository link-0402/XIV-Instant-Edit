using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Services;
using InstantEdit.Studio.Windows.UI;

namespace InstantEdit.Studio.Windows.Panels;

/// <summary>
/// Draws validation issues as clickable rows; clicking one opens the stage or view that shows the
/// offending object and selects it. Used by the status-bar popups of both modes and the Build stage.
/// </summary>
internal static class IssueList
{
    public const string PopupId = "##StudioIssues";

    /// <summary>Returns true when a row was clicked (so a popup can close itself).</summary>
    public static bool Draw(IReadOnlyList<PortIssue> issues, Action<Target> focus, bool includeInfo = true)
    {
        var shown = includeInfo ? issues : issues.Where(i => i.Severity != Severity.Info).ToList();
        if (shown.Count == 0)
        {
            Gui.Icon(FontAwesomeIcon.CheckCircle, Theme.Good);
            ImGui.SameLine();
            ImGui.TextColored(Theme.Good, "No problems found.");
            return false;
        }

        bool clicked = false;
        for (int i = 0; i < shown.Count; i++)
        {
            var issue = shown[i];
            ImGui.PushID(i);

            Gui.Icon(IconFor(issue.Severity), Gui.SeverityColor(issue.Severity));
            ImGui.SameLine();

            if (ImGui.Selectable($"{issue.Message}##issue", false, ImGuiSelectableFlags.None))
            {
                focus(issue.Target);
                clicked = true;
            }
            Gui.Tooltip("Go to it");

            ImGui.PopID();
        }
        return clicked;
    }

    /// <summary>The popup opened from the status bar's issue badge.</summary>
    public static void DrawPopup(IReadOnlyList<PortIssue> issues, Action<Target> focus)
    {
        ImGui.SetNextWindowSizeConstraints(new System.Numerics.Vector2(Theme.S(360), 0),
            new System.Numerics.Vector2(Theme.S(640), Theme.S(420)));
        if (!ImGui.BeginPopup(PopupId))
            return;

        Gui.SectionHeader("Problems", "Checked continuously against your set-up and the files on disk.");
        if (Draw(issues, focus))
            ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    public static FontAwesomeIcon IconFor(Severity severity) => severity switch
    {
        Severity.Error   => FontAwesomeIcon.TimesCircle,
        Severity.Warning => FontAwesomeIcon.ExclamationTriangle,
        _                => FontAwesomeIcon.InfoCircle,
    };

    /// <summary>
    /// Draws a severity marker for the given target, if it has an issue; otherwise an
    /// invisible spacer of the same width so rows stay aligned.
    /// </summary>
    public static void Marker(IReadOnlyList<PortIssue> issues, Target target)
    {
        var worst = PortValidator.Worst(issues, target);
        if (worst == null)
        {
            ImGui.PushFont(UiBuilder.IconFont);
            ImGui.Dummy(ImGui.CalcTextSize(FontAwesomeIcon.TimesCircle.ToIconString()));
            ImGui.PopFont();
            return;
        }

        Gui.Icon(IconFor(worst.Value.Severity), Gui.SeverityColor(worst.Value.Severity));
        var all = issues.Where(i => i.Target == target).Select(i => i.Message);
        Gui.Tooltip(string.Join("\n", all));
    }
}
