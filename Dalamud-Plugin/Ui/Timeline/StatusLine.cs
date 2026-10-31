using System.Collections.Immutable;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace InstantEdit.Ui.Timeline;

/// <summary> What a status note says about the window's state; the status line shows the kinds in this order. </summary>
internal enum StatusKind : byte
{
    /// <summary> Work running, shown with a spinner and never dismissed. </summary>
    Working,
    /// <summary> Something that failed. </summary>
    Error,
    /// <summary> Something the user should know before going on. </summary>
    Warning,
    /// <summary> Something that happened or a state worth knowing. </summary>
    Notice,
}

/// <summary>
/// One note for the status line. <paramref name="Key"/> names its source, so dismissing it hides that source until its
/// <paramref name="Text"/> changes; <paramref name="Button"/> with <paramref name="Act"/> draws a small button beside it,
/// greyed out unless <paramref name="ButtonEnabled"/>, with <paramref name="ButtonTooltip"/> on hover.
/// </summary>
internal sealed record StatusNote(string Key, StatusKind Kind, string Text, string? Button = null, Action? Act = null, string? Tooltip = null,
    string? ButtonTooltip = null, bool ButtonEnabled = true);

/// <summary>
/// The one line under a window's action bar: the first note shows, and "+N notes" opens a popup with all of them.
/// Nothing is drawn when there is no note. Each window keeps its own line, so its dismissed notes stay its own.
/// </summary>
internal sealed class StatusLine
{
    // Dismissed notes by key, with the text they had when dismissed.
    private readonly Dictionary<string, string> dismissed = new();

    /// <summary>
    /// The notes to show, work running first, then errors, warnings and notices, each kind in the order given. A note
    /// whose text is the one it was dismissed with is left out; work running is never left out.
    /// </summary>
    public static ImmutableArray<StatusNote> Shown(IEnumerable<StatusNote> notes, IReadOnlyDictionary<string, string> dismissed) =>
        notes.Where(n => n.Kind == StatusKind.Working || !dismissed.TryGetValue(n.Key, out var text) || text != n.Text)
            .OrderBy(n => n.Kind)
            .ToImmutableArray();

    /// <summary> The text that opens the popup with every note: "+1 note", "+2 notes". </summary>
    public static string MoreText(int more) => more == 1 ? "+1 note" : $"+{more} notes";

    /// <summary> Hides <paramref name="note"/> until its text changes. </summary>
    public void Dismiss(StatusNote note) => dismissed[note.Key] = note.Text;

    /// <summary>
    /// The notes this line shows. A dismissal ends as soon as its note is gone or its text changes, so the same text
    /// coming back later shows again.
    /// </summary>
    public ImmutableArray<StatusNote> Visible(IEnumerable<StatusNote> notes)
    {
        var current = notes as IReadOnlyCollection<StatusNote> ?? notes.ToList();
        if (dismissed.Count > 0)
        {
            foreach (var key in dismissed.Keys.ToList())
            {
                if (!current.Any(n => n.Key == key && n.Text == dismissed[key]))
                    dismissed.Remove(key);
            }
        }
        return Shown(current, dismissed);
    }

    /// <summary>
    /// Draws the line: the first shown note, then "+N notes" when there are more. <paramref name="id"/> starts every
    /// control's id, so two windows can each draw a line.
    /// </summary>
    public void Draw(string id, IEnumerable<StatusNote> notes)
    {
        var shown = Visible(notes);
        if (shown.IsEmpty)
            return;
        using var scope = ImRaii.PushId(id + "-status");
        DrawNote(shown[0]);
        if (shown.Length == 1)
            return;
        ImGui.SameLine();
        var more = MoreText(shown.Length - 1);
        if (ImGui.Selectable(more, false, ImGuiSelectableFlags.None, ImGui.CalcTextSize(more)))
            ImGui.OpenPopup("notes");
        using var popup = ImRaii.Popup("notes");
        if (!popup)
            return;
        foreach (var note in shown)
            DrawNote(note);
    }

    /// <summary> One note on one line: a spinner for work running, its text in its kind's colour, its button and the dismiss button. </summary>
    private void DrawNote(StatusNote note)
    {
        using var scope = ImRaii.PushId(note.Key);
        if (note.Kind == StatusKind.Working)
        {
            Widgets.Spinner();
            ImGui.SameLine();
        }
        ImGui.AlignTextToFramePadding();
        switch (note.Kind)
        {
            case StatusKind.Error or StatusKind.Warning:
                ImGui.TextColored(Theme.Warning, note.Text);
                break;
            case StatusKind.Notice:
                ImGui.TextColored(Theme.Muted, note.Text);
                break;
            default:
                ImGui.TextUnformatted(note.Text);
                break;
        }
        if (note.Tooltip is not null && ImGui.IsItemHovered())
            ImGui.SetTooltip(note.Tooltip);
        if (note.Button is not null)
        {
            ImGui.SameLine();
            using (ImRaii.Disabled(!note.ButtonEnabled))
            {
                if (ImGui.SmallButton(note.Button) && note.ButtonEnabled)
                    note.Act?.Invoke();
            }
            if (note.ButtonTooltip is not null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(note.ButtonTooltip);
        }
        if (note.Kind == StatusKind.Working)
            return;
        ImGui.SameLine();
        if (Widgets.GhostIconButton("dismiss", FontAwesomeIcon.Times, "Hide this note."))
            Dismiss(note);
    }
}
