using System.Collections.Immutable;
using System.Numerics;
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
    /// The width the first note's text gets on the line: all of it (<paramref name="text"/>) when it fits in
    /// <paramref name="available"/> beside what follows it on the line (<paramref name="after"/>), else what is left, never
    /// below 0. Less than <paramref name="text"/> means the text is cut off and shows whole in its tooltip.
    /// </summary>
    public static float TextRoom(float available, float text, float after) => Math.Clamp(available - after, 0, text);

    /// <summary>
    /// Draws the line: the first shown note, then "+N notes" at the right end when there are more. The note's text is cut
    /// to the room left, so its button, its dismiss button and "+N notes" always stay in view. <paramref name="id"/> starts
    /// every control's id, so two windows can each draw a line.
    /// </summary>
    public void Draw(string id, IEnumerable<StatusNote> notes)
    {
        var shown = Visible(notes);
        if (shown.IsEmpty)
            return;
        using var scope = ImRaii.PushId(id + "-status");
        var more = shown.Length > 1 ? MoreText(shown.Length - 1) : null;
        var first = shown[0];
        var style = ImGui.GetStyle();
        var spacing = style.ItemSpacing.X;
        var square = ImGui.GetFrameHeight();
        var moreWidth = more != null ? ImGui.CalcTextSize(more).X : 0;
        // The spinner before work running, or the dismiss button after any other note: one frame-height square either way.
        var after = square + spacing
                    + (first.Button is { } button ? ImGui.CalcTextSize(button).X + style.FramePadding.X * 2 + spacing : 0)
                    + (more != null ? moreWidth + spacing : 0);
        DrawNote(first, TextRoom(ImGui.GetContentRegionAvail().X, ImGui.CalcTextSize(first.Text).X, after));
        if (more == null)
            return;
        ImGui.SameLine();
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), ImGui.GetContentRegionMax().X - moreWidth));
        if (ImGui.Selectable(more, false, ImGuiSelectableFlags.None, ImGui.CalcTextSize(more)))
            ImGui.OpenPopup("notes");
        using var popup = ImRaii.Popup("notes");
        if (!popup)
            return;
        foreach (var note in shown)
            DrawNote(note, null);
    }

    /// <summary>
    /// One note on one line: a spinner for work running, its text in its kind's colour, its button and the dismiss button.
    /// With <paramref name="room"/> the text is cut to that width, and shows whole in its tooltip when cut.
    /// </summary>
    private void DrawNote(StatusNote note, float? room)
    {
        using var scope = ImRaii.PushId(note.Key);
        if (note.Kind == StatusKind.Working)
        {
            Widgets.Spinner();
            ImGui.SameLine();
        }
        var colour = note.Kind switch
        {
            StatusKind.Error or StatusKind.Warning => Theme.Warning,
            StatusKind.Notice => Theme.Muted,
            _ => ImGui.GetStyle().Colors[(int)ImGuiCol.Text],
        };
        var cut = room is { } width && width < ImGui.CalcTextSize(note.Text).X;
        if (cut)
        {
            // The text drawn into a box as wide as the room, clipped at its right edge.
            var at = ImGui.GetCursorScreenPos();
            var size = new Vector2(room!.Value, ImGui.GetFrameHeight());
            ImGui.Dummy(size);
            var drawList = ImGui.GetWindowDrawList();
            drawList.PushClipRect(at, at + size, true);
            drawList.AddText(at + new Vector2(0, ImGui.GetStyle().FramePadding.Y), ImGui.GetColorU32(colour), note.Text);
            drawList.PopClipRect();
        }
        else
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(colour, note.Text);
        }
        var tooltip = cut ? note.Tooltip is { } own ? note.Text + "\n" + own : note.Text : note.Tooltip;
        if (tooltip is not null && ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
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
        if (Widgets.GhostIconButton("dismiss", FontAwesomeIcon.Times, "Hide this note.", new Vector2(ImGui.GetFrameHeight())))
            Dismiss(note);
    }
}
