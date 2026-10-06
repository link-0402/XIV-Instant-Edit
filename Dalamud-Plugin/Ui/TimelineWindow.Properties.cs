using System.Collections.Immutable;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui.Timeline;

namespace InstantEdit.Ui;

/// <summary>
/// The properties panel: the selected event's start, length, file and fields. Each field edit is
/// one undo step (BeginFieldEdit when it activates, a live Replace per change, EndFieldEdit when it
/// is left). Locked and move-only events show their reason and read-only values.
/// </summary>
public sealed partial class TimelineWindow
{
    private void DrawProperties(TimelineEditor current)
    {
        var readOnly = ReadOnly;
        var primary = current.PrimaryEvent;
        var count = current.Selected.Count;
        if (primary == null || count == 0)
        {
            Widgets.EmptyState(FontAwesomeIcon.MousePointer, "Select an event", "Click an event to edit it. Double-click a lane to add one.");
            return;
        }
        if (count > 1 && !OneGroup(current, primary))
        {
            DrawSelection(current, count, readOnly);
            return;
        }
        DrawEventProperties(current, primary, count, readOnly);
    }

    /// <summary> Whether every selected event belongs to the primary's group, such as the parts of one prop. </summary>
    private static bool OneGroup(TimelineEditor current, TimelineEvent primary)
    {
        if (primary.Group is not { } group)
            return false;
        var events = current.Document.Events.IsDefault ? [] : current.Document.Events;
        foreach (var ev in events)
            if (current.IsSelected(ev.Key) && !string.Equals(ev.Group, group, StringComparison.Ordinal))
                return false;
        return true;
    }

    private static void DrawSelection(TimelineEditor current, int count, bool readOnly)
    {
        Widgets.EmptyState(FontAwesomeIcon.LayerGroup, $"{count} selected", "Drag one of them to move them all.");
        ImGui.Spacing();
        var (editable, movable) = SelectionAccess(current);
        using (ImRaii.Disabled(readOnly || !editable))
        {
            if (ImGui.Button("Duplicate"))
                current.DuplicateSelection();
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(readOnly || !movable))
        {
            if (ImGui.Button("Delete"))
                current.DeleteSelection();
        }
    }

    private void DrawEventProperties(TimelineEditor current, TimelineEvent ev, int count, bool readOnly)
    {
        var kind = TmbEntrySchema.Find(ev.Magic);
        var editable = !readOnly && TimelineEdits.CanEdit(ev);
        var movable = !readOnly && TimelineEdits.CanMove(ev);

        Widgets.Icon(LaneIcon(ev.Lane), TimelineWindowViews.LaneColour(ev.Lane));
        ImGui.SameLine(0, Theme.Gap);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Label, LabelOf(current.Document, ev));
        if (ev.Access != TimelineAccess.Editable)
        {
            ImGui.SameLine(0, Theme.Gap);
            if (Widgets.Badge(ev.Access == TimelineAccess.Locked ? "locked" : "move only", Theme.Muted) && ev.LockReason is { } reason)
                ImGui.SetTooltip(reason);
        }
        if (ev.LockReason is { Length: > 0 } why)
            Widgets.HintWrapped(ev.Access == TimelineAccess.Locked ? $"{why}. It is shown but never changed." : $"{why}. Its other values stay as they are.");
        if (count > 1)
            Widgets.HintWrapped($"Part of a group of {count} events. They move together.");
        if (ev.Conditional)
            Widgets.HintWrapped("Plays only under a condition.");
        ImGui.Spacing();

        using (var facts = ImRaii.Table("##timeline-event-facts", 2, ImGuiTableFlags.SizingFixedFit))
        {
            if (facts.Success)
            {
                ImGui.TableSetupColumn("##fact", ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableSetupColumn("##value", ImGuiTableColumnFlags.WidthStretch);
                Fact("Kind");
                ImGui.TextUnformatted(TimelineWindowViews.KindName(ev.Magic));
                Fact("Lane");
                ImGui.TextUnformatted(TimelineWindowViews.LaneName(ev.Lane));
                DrawTiming(current, ev, editable, movable);
                DrawPath(current, ev, kind, editable);
                if (kind != null)
                    foreach (var field in kind.Fields)
                    {
                        Fact(field.Label);
                        using var id = ImRaii.PushId(field.Name);
                        var value = ev.Values?.GetValueOrDefault(field.Name) ?? 0;
                        if (editable)
                            DrawField(current, ev, field, value);
                        else
                            ImGui.TextUnformatted(TimelineWindowViews.FieldText(field, value));
                    }
            }
        }

        ImGui.Spacing();
        using (ImRaii.Disabled(!movable))
        {
            if (ImGui.Button("Move to playhead"))
                current.MoveSelectionTo(current.Playhead);
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(!editable))
        {
            if (ImGui.Button("Duplicate"))
                current.DuplicateSelection();
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(!movable))
        {
            if (ImGui.Button("Delete"))
                current.DeleteSelection();
        }
    }

    private string LabelOf(TimelineDocument doc, TimelineEvent ev)
    {
        if (ReferenceEquals(labelDocument, doc) && TimelineEdits.IndexOf(doc, ev.Key) is var index and >= 0 && index < labels.Length)
            return labels[index];
        return TimelineWindowViews.EventLabel(ev);
    }

    private static void DrawTiming(TimelineEditor current, TimelineEvent ev, bool editable, bool movable)
    {
        Fact("Start");
        if (movable)
        {
            var start = ev.Start;
            ImGui.SetNextItemWidth(Theme.Scaled(90));
            var changed = ImGui.InputInt("##start", ref start, 0, 0);
            if (current.Selected.Count > 1)
            {
                // Several selected, such as a prop's parts: they move together, as a drag moves them.
                if (ImGui.IsItemActivated())
                    current.BeginFieldEdit();
                if (changed)
                    current.MoveSelectionStart(Math.Clamp(start, 0, TimelineDocument.MaxFrames));
                if (ImGui.IsItemDeactivated())
                    current.EndFieldEdit();
            }
            else
                TrackField(current, changed, changed ? ev with { Start = Math.Clamp(start, 0, TimelineDocument.MaxFrames) } : null);
            ImGui.SameLine(0, Theme.Gap);
            ImGui.TextColored(Theme.Muted, TimelineText.Seconds(ev.Start));
        }
        else
            ImGui.TextUnformatted(TimelineWindowViews.FrameText(ev.Start));

        if (ev.Duration <= 0 && !ev.Resizable)
            return;
        Fact("Length");
        if (TimelineDocument.Kept(ev))
        {
            var loops = TimelineDocument.Loops(ev);
            ImGui.TextUnformatted(loops ? $"Loops for about {TimelineWindowViews.LoopMinutes(ev)} min"
                : TimelineDocument.Moves(ev, current.MotionFrames) != null ? "Holds still" : "Until replaced");
            ImGui.SameLine(0, Theme.Gap);
            ImGui.TextColored(Theme.Muted, "kept");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip((loops ? TimelineWindowViews.LoopText(ev) : TimelineWindowViews.KeptText) + ".");
        }
        else if (TimelineDocument.Holds(ev))
        {
            // The game ignores the stored length; show how long the face really shows.
            ImGui.TextUnformatted(TimelineWindowViews.DurationText(current.Document.HeldEnd(ev) - ev.Start));
            ImGui.SameLine(0, Theme.Gap);
            ImGui.TextColored(Theme.Muted, "held");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(TimelineWindowViews.HoldText(current.Document, ev) + ". Turn on Time control to set a length.");
        }
        else if (TimelineDocument.OwnPace(ev))
        {
            ImGui.TextUnformatted(TimelineWindowViews.DurationText(current.Document.PlayedFrames(ev, current.MotionFrames)));
            ImGui.SameLine(0, Theme.Gap);
            ImGui.TextColored(Theme.Muted, "own speed");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Plays at its own speed. Turn on Time control to set a length.");
        }
        else if (editable && TimelineEdits.CanResize(ev) && ev.Duration <= TimelineDocument.MaxFrames)
        {
            var duration = ev.Duration;
            ImGui.SetNextItemWidth(Theme.Scaled(90));
            var changed = ImGui.InputInt("##duration", ref duration, 0, 0);
            TrackField(current, changed, changed ? ev with { Duration = Math.Clamp(duration, 1, TimelineDocument.MaxFrames) } : null);
            ImGui.SameLine(0, Theme.Gap);
            ImGui.TextColored(Theme.Muted, TimelineText.Seconds(ev.Duration));
        }
        else
            ImGui.TextUnformatted(TimelineWindowViews.DurationText(ev.Duration));
        if (TimelineDocument.CanKeep(ev))
            DrawKeep(current, ev, editable);
    }

    /// <summary> The Keep switch of a face: on, the game keeps showing it after the timeline ends. </summary>
    private static void DrawKeep(TimelineEditor current, TimelineEvent ev, bool editable)
    {
        Fact("Keep");
        // A kept moving face that could loop but holds still (as a Static Infinite option writes)
        // shows unticked, so ticking it makes it loop.
        var loops = TimelineEdits.CanLoop(current.Document, ev, current.MotionFrames);
        var kept = TimelineDocument.Kept(ev) && (ev.Loop || !loops);
        using (ImRaii.Disabled(!editable))
        {
            if (ImGui.Checkbox("##keep", ref kept))
                current.Keep(ev.Key, kept);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(TimelineWindowViews.KeepTip(ev, TimelineDocument.Moves(ev, current.MotionFrames) != null, loops));
    }

    /// <summary> The event's file or motion, or a prop's model, with Choose for one that can take another. </summary>
    private void DrawPath(TimelineEditor current, TimelineEvent ev, TmbEntryKind? kind, bool editable)
    {
        var picker = TimelineWindowViews.PickerFor(ev);
        var hasPath = ev.Path.Length > 0 || kind is { StringOffset: >= 0 };
        if (!hasPath && picker != TimelineAddKind.Prop)
            return;
        Fact(picker == TimelineAddKind.Prop ? "Model" : ev.Lane is TimelineLane.Motion or TimelineLane.Expression or TimelineLane.LipSync ? "Motion" : "File");
        if (editable && picker is { } choose)
        {
            if (ImGui.SmallButton("Choose"))
                RequestPicker(choose, current.Playhead, ev.Key);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(choose == TimelineAddKind.Prop ? "Pick another prop model." : $"Pick another {TimelineWindowViews.AddKindName(choose).ToLowerInvariant()}.");
            ImGui.SameLine(0, Theme.Gap);
        }
        if (picker == TimelineAddKind.Prop && !hasPath)
        {
            ImGui.TextUnformatted(LabelOf(current.Document, ev));
            return;
        }
        if (ev.Path.Length == 0)
            ImGui.TextColored(Theme.Hint, "None");
        else
            Widgets.PathText(LabelOf(current.Document, ev), ev.Path);
    }

    /// <summary> One field's control; its edits until it is left make one undo step. </summary>
    private static void DrawField(TimelineEditor current, TimelineEvent ev, TimelineField field, double value)
    {
        switch (field.Kind)
        {
            case TimelineFieldKind.Int:
            {
                var number = (int)Math.Clamp(Math.Round(value), int.MinValue, int.MaxValue);
                ImGui.SetNextItemWidth(Theme.Scaled(110));
                var changed = ImGui.InputInt("##value", ref number, 0, 0);
                TrackField(current, changed, changed ? With(ev, field, Math.Clamp(number, field.Min, field.Max)) : null);
                break;
            }
            case TimelineFieldKind.Float:
            {
                var number = (float)value;
                ImGui.SetNextItemWidth(Theme.Scaled(110));
                var changed = ImGui.InputFloat("##value", ref number, 0, 0, "%.3f") && float.IsFinite(number);
                TrackField(current, changed, changed ? With(ev, field, Math.Clamp(number, field.Min, field.Max)) : null);
                break;
            }
            case TimelineFieldKind.Bool:
            {
                var on = value != 0;
                var changed = ImGui.Checkbox("##value", ref on);
                TrackField(current, changed, changed ? With(ev, field, on ? 1 : 0) : null);
                break;
            }
            case TimelineFieldKind.Choice:
            {
                ImGui.SetNextItemWidth(Theme.Scaled(170));
                using var combo = ImRaii.Combo("##value", TimelineWindowViews.FieldText(field, value));
                if (!combo.Success)
                    break;
                foreach (var (label, option) in field.Options)
                    if (ImGui.Selectable(label, option == value) && option != value)
                        current.Replace(With(ev, field, option));
                break;
            }
            case TimelineFieldKind.Flags:
            {
                var bits = (long)value;
                foreach (var (label, option) in field.Options)
                {
                    var bit = (long)option;
                    var set = (bits & bit) != 0;
                    var changed = ImGui.Checkbox(label, ref set);
                    TrackField(current, changed, changed ? With(ev, field, set ? bits | bit : bits & ~bit) : null);
                }
                break;
            }
        }
    }

    private static TimelineEvent With(TimelineEvent ev, TimelineField field, double value) =>
        ev with { Values = (ev.Values ?? ImmutableDictionary<string, double>.Empty).SetItem(field.Name, value) };

    /// <summary> Makes the last control's changes one undo step: begun when it activates, ended when it is left. </summary>
    private static void TrackField(TimelineEditor current, bool changed, TimelineEvent? next)
    {
        if (ImGui.IsItemActivated())
            current.BeginFieldEdit();
        if (changed && next != null)
            current.Replace(next);
        if (ImGui.IsItemDeactivated())
            current.EndFieldEdit();
    }

    /// <summary> Starts a fact row: its muted label, then the value's cell. </summary>
    private static void Fact(string label)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, label);
        ImGui.TableSetColumnIndex(1);
        ImGui.AlignTextToFramePadding();
    }
}
