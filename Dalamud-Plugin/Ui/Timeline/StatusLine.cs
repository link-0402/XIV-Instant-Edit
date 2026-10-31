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
/// A work-running note's timing on the status line: since when its key is there (<paramref name="Since"/>), since when it
/// is gone (<paramref name="Gone"/>, null while there), whether it shows yet, and the note as it last was.
/// </summary>
internal sealed record WorkTiming(double Since, double? Gone, bool Shown, StatusNote Last);

/// <summary>
/// The one line under a window's action bar: the first note shows, and "+N notes" opens a popup with all of them.
/// Nothing is drawn when there is no note. Each window keeps its own line, so its dismissed notes stay its own.
/// </summary>
internal sealed class StatusLine
{
    // Dismissed notes by key, with the text they had when dismissed.
    private readonly Dictionary<string, string> dismissed = new();
    // Work-running notes' timings by key (see Debounced).
    private readonly Dictionary<string, WorkTiming> timings = new();

    /// <summary>
    /// The notes to show, work running first, then errors, warnings and notices, each kind in the order given. A note
    /// whose text is the one it was dismissed with is left out; work running is never left out.
    /// </summary>
    public static ImmutableArray<StatusNote> Shown(IEnumerable<StatusNote> notes, IReadOnlyDictionary<string, string> dismissed) =>
        notes.Where(n => n.Kind == StatusKind.Working || !dismissed.TryGetValue(n.Key, out var text) || text != n.Text)
            .OrderBy(n => n.Kind)
            .ToImmutableArray();

    /// <summary> Seconds a work-running note must be there before it shows, so a few frames of work don't move the lanes. </summary>
    public const double WorkShowAfter = .3;

    /// <summary> Seconds a work-running note keeps showing after it went, so work that stops and starts again stays on. </summary>
    public const double WorkKeepFor = .5;

    /// <summary>
    /// The notes as the line shows them at <paramref name="now"/> seconds. A work-running note shows once its key has been
    /// there for <see cref="WorkShowAfter"/>; once shown, it stays with its last text until its key has been gone for
    /// <see cref="WorkKeepFor"/>, without its button then, or until another note with its key takes its place. Other notes
    /// show at once. <paramref name="timings"/> holds each work note's timing by key and is updated here.
    /// </summary>
    public static ImmutableArray<StatusNote> Debounced(IEnumerable<StatusNote> notes, Dictionary<string, WorkTiming> timings, double now)
    {
        var shown = ImmutableArray.CreateBuilder<StatusNote>();
        var working = new HashSet<string>();
        var others = new HashSet<string>();
        foreach (var note in notes)
        {
            if (note.Kind != StatusKind.Working)
            {
                others.Add(note.Key);
                shown.Add(note);
                continue;
            }
            working.Add(note.Key);
            var timing = timings.TryGetValue(note.Key, out var known) ? known with { Gone = null, Last = note } : new WorkTiming(now, null, false, note);
            if (!timing.Shown && now - timing.Since >= WorkShowAfter)
                timing = timing with { Shown = true };
            timings[note.Key] = timing;
            if (timing.Shown)
                shown.Add(note);
        }
        foreach (var (key, timing) in timings.ToList())
        {
            if (working.Contains(key))
                continue;
            // Work that never showed, or whose result now shows in its place, goes at once.
            var gone = timing.Gone ?? now;
            if (!timing.Shown || others.Contains(key) || now - gone >= WorkKeepFor)
            {
                timings.Remove(key);
                continue;
            }
            timings[key] = timing with { Gone = gone };
            // Its task has ended: no button stays to click.
            shown.Add(timing.Last with { Button = null, Act = null, ButtonTooltip = null });
        }
        return shown.ToImmutable();
    }

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
    /// Draws the line: the first shown note, then "+N notes" at the right end when there are more. Work running comes and
    /// goes on it steadily (<see cref="Debounced"/>). The note's text is cut to the room left, so its button, its dismiss
    /// button and "+N notes" always stay in view. <paramref name="id"/> starts every control's id, so two windows can each
    /// draw a line.
    /// </summary>
    public void Draw(string id, IEnumerable<StatusNote> notes)
    {
        var shown = Visible(Debounced(notes, timings, ImGui.GetTime()));
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
