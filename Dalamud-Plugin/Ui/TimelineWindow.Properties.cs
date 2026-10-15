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
    // The File field of a sound or effect: its text while typed in, its last refusal, and where
    // the collection gets each path (by collection and path, forgotten with the sounds).
    private string? pathFieldKey;
    private string pathFieldText = string.Empty;
    private bool pathFieldActive;
    private string? pathFieldError;
    private readonly Dictionary<(Guid Collection, string Path), Task<(TimelinePathOrigin Origin, string? ModName)>> pathOrigins = [];

    private void DrawProperties(TimelineEditor current)
    {
        effectLooks.EndEdit(current);
        soundInspector.EndEdit(current);
        if (current.SelectedCut != null)
        {
            cutPanel.Draw(current);
            return;
        }
        var readOnly = ReadOnly;
        if (current.SelectedLayerValue is { } layer && source != null)
        {
            layerPanel.Draw(current, layer, source);
            return;
        }
        var primary = current.PrimaryEvent;
        var count = current.Selected.Count;
        if (primary == null || count == 0)
        {
            Widgets.EmptyState(FontAwesomeIcon.MousePointer, "Select an event",
                "Click an event or a bone layer to edit it. Double-click a lane to add one.");
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
        var (editable, movable) = TimelineCanvas.SelectionAccess(current);
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

        Widgets.Icon(TimelineUi.LaneIcon(ev.Lane), TimelineWindowViews.LaneColour(ev.Lane));
        ImGui.SameLine(0, Theme.Gap);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Label, texts.LabelOf(current.Document, ev));
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
                TimelineUi.Fact("Kind");
                ImGui.TextUnformatted(TimelineWindowViews.KindName(ev.Magic));
                TimelineUi.Fact("Lane");
                ImGui.TextUnformatted(TimelineWindowViews.LaneName(ev.Lane));
                if (TimelineCanvas.FromPack(current, ev))
                {
                    TimelineUi.Fact("Set");
                    ImGui.TextUnformatted(current.Document.FacePack);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("This face comes from the timeline's expression set. A timeline uses one set.\nIt goes when its last face does.");
                }
                DrawTiming(current, ev, editable, movable);
                DrawPath(current, ev, kind, editable);
                // A sound's options say what they do instead of showing its raw fields.
                if (ev.Magic == "C063")
                    soundInspector.DrawEvent(current, ev, editable);
                else if (kind != null)
                    foreach (var field in kind.Fields)
                    {
                        TimelineUi.Fact(field.Label);
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
        effectLooks.Draw(current, ev, editable);
        soundInspector.DrawFile(current, ev, editable);
    }

    private static void DrawTiming(TimelineEditor current, TimelineEvent ev, bool editable, bool movable)
    {
        TimelineUi.Fact("Start");
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
                TimelineUi.TrackField(current, changed, changed ? ev with { Start = Math.Clamp(start, 0, TimelineDocument.MaxFrames) } : null);
            ImGui.SameLine(0, Theme.Gap);
            ImGui.TextColored(Theme.Muted, TimelineText.Seconds(ev.Start));
        }
        else
            ImGui.TextUnformatted(TimelineWindowViews.FrameText(ev.Start));

        if (ev.Duration <= 0 && !ev.Resizable)
            return;
        TimelineUi.Fact("Length");
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
        else if (TimelineDocument.Holds(ev) && TimelineDocument.Moves(ev, current.MotionFrames) == null)
        {
            // The game ignores the stored length; show how long the pose really shows. A moving face plays its own length.
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
            TimelineUi.TrackField(current, changed, changed ? ev with { Duration = Math.Clamp(duration, 1, TimelineDocument.MaxFrames) } : null);
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
        TimelineUi.Fact("Keep");
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
            ImGui.SetTooltip(TimelineWindowViews.KeepTip(ev, TimelineDocument.Moves(ev, current.MotionFrames) != null, loops,
                TimelineEdits.TooShortToLoop(current.Document, ev, current.MotionFrames)));
    }

    /// <summary> The event's file or motion, or a prop's model, with Choose for one that can take another. </summary>
    private void DrawPath(TimelineEditor current, TimelineEvent ev, TmbEntryKind? kind, bool editable)
    {
        var picker = TimelineWindowViews.PickerFor(ev);
        var hasPath = ev.Path.Length > 0 || kind is { StringOffset: >= 0 };
        if (!hasPath && picker != TimelineAddKind.Prop)
            return;
        TimelineUi.Fact(picker == TimelineAddKind.Prop ? "Model" : ev.Lane is TimelineLane.Motion or TimelineLane.Expression or TimelineLane.LipSync ? "Motion" : "File");
        if (editable && picker is { } choose)
        {
            if (ImGui.SmallButton("Choose"))
                pickers.Request(choose, current.Playhead, ev.Key);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(choose == TimelineAddKind.Prop ? "Pick another prop model." : $"Pick another {TimelineWindowViews.AddKindName(choose).ToLowerInvariant()}.");
            ImGui.SameLine(0, Theme.Gap);
        }
        if (picker == TimelineAddKind.Prop && !hasPath)
        {
            ImGui.TextUnformatted(texts.LabelOf(current.Document, ev));
            return;
        }
        if (editable && picker is TimelineAddKind.Sound or TimelineAddKind.Effect)
        {
            DrawPathField(current, ev);
            return;
        }
        if (ev.Path.Length == 0)
            ImGui.TextColored(Theme.Hint, "None");
        else
            Widgets.PathText(texts.LabelOf(current.Document, ev), ev.Path);
    }

    /// <summary>
    /// A sound's or effect's game path as a text field: Enter or leaving it applies the typed path as
    /// one undo step. Under it, the friendly name and where the collection gets the file.
    /// </summary>
    private void DrawPathField(TimelineEditor current, TimelineEvent ev)
    {
        // The buffer follows the event until the field is being typed in.
        if (pathFieldKey != ev.Key)
        {
            pathFieldKey = ev.Key;
            pathFieldActive = false;
            pathFieldError = null;
        }
        if (!pathFieldActive)
            pathFieldText = ev.Path;
        ImGui.SetNextItemWidth(Math.Min(Theme.Scaled(300), ImGui.GetContentRegionAvail().X));
        bool entered;
        using (ImRaii.PushFont(UiBuilder.MonoFont))
            entered = ImGui.InputText("##path", ref pathFieldText, 256, ImGuiInputTextFlags.EnterReturnsTrue);
        pathFieldActive = ImGui.IsItemActive();
        if (ImGui.IsItemActivated())
            pathFieldError = null;
        if (entered || ImGui.IsItemDeactivatedAfterEdit())
        {
            var next = TimelineWindowViews.WithPath(ev, pathFieldText, out pathFieldError);
            if (next != null && !ReferenceEquals(next, ev))
            {
                current.BeginFieldEdit();
                current.Replace(next);
                current.EndFieldEdit();
            }
            pathFieldText = next?.Path ?? pathFieldText;
        }
        if (pathFieldError != null)
        {
            using var wrap = ImRaii.TextWrapPos(0f);
            ImGui.TextColored(Theme.Warning, pathFieldError);
        }
        ImGui.TextColored(Theme.Hint, texts.LabelOf(current.Document, ev));
        if (PathOrigin(ev.Path) is { } origin)
            Widgets.MutedWrapped(origin);
    }

    /// <summary> Where the collection gets <paramref name="gamePath"/>, read once on a task; null until read. </summary>
    private string? PathOrigin(string gamePath)
    {
        if (source == null || gamePath.Length == 0 || animations is not { } service)
            return null;
        var key = (source.Capture.CollectionId, gamePath.ToLowerInvariant());
        if (!pathOrigins.TryGetValue(key, out var read))
        {
            var collection = source.Capture.CollectionId;
            read = Task.Run(() => service.PathOriginAsync(collection, gamePath, CancellationToken.None));
            pathOrigins[key] = read;
        }
        if (!read.IsCompleted)
            return null;
        return read.IsCompletedSuccessfully ? TimelineWindowViews.PathOriginText(read.Result.Origin, read.Result.ModName) : TimelineSession.ErrorText(read.Exception);
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
                TimelineUi.TrackField(current, changed, changed ? TimelineUi.With(ev, field, Math.Clamp(number, field.Min, field.Max)) : null);
                break;
            }
            case TimelineFieldKind.Float:
            {
                var number = (float)value;
                ImGui.SetNextItemWidth(Theme.Scaled(110));
                var changed = ImGui.InputFloat("##value", ref number, 0, 0, "%.3f") && float.IsFinite(number);
                TimelineUi.TrackField(current, changed, changed ? TimelineUi.With(ev, field, Math.Clamp(number, field.Min, field.Max)) : null);
                break;
            }
            case TimelineFieldKind.Bool:
            {
                var on = value != 0;
                var changed = ImGui.Checkbox("##value", ref on);
                TimelineUi.TrackField(current, changed, changed ? TimelineUi.With(ev, field, on ? 1 : 0) : null);
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
                        current.Replace(TimelineUi.With(ev, field, option));
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
                    TimelineUi.TrackField(current, changed, changed ? TimelineUi.With(ev, field, set ? bits | bit : bits & ~bit) : null);
                }
                break;
            }
        }
    }

}
