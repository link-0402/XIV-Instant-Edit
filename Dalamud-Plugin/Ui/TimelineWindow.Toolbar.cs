using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui.Timeline;

namespace InstantEdit.Ui;

/// <summary> The row above the lanes: undo, redo, add, snapping, fit, the length and the playhead, then short messages. </summary>
public sealed partial class TimelineWindow
{
    private const string AddMenu = "##timeline-add-menu";
    private const string ControlsHelp =
        "Drag an event to move it. Drag its edges to resize it.\n" +
        "Drag the grip at the end of the ruler to change the length.\n" +
        "Click or drag the ruler to move the playhead.\n" +
        "Wheel scrolls. Ctrl+wheel zooms. Shift+wheel scrolls the lanes.\n" +
        "Hold Alt while dragging to place freely.\n" +
        "C cuts the motion at the playhead. Q and W remove the part before or after it.\n" +
        "Drag a part's edges on the clip row to trim it.\n" +
        "Right-click for more. Double-click a lane to add to it.";

    private bool lengthActive;
    private float lengthEdit;
    private int readoutFrame = -1;
    private string readout = string.Empty;

    private void DrawToolbar(TimelineEditor current)
    {
        var readOnly = ReadOnly;
        var history = current.History;
        if (Widgets.IconButton("##timeline-undo", FontAwesomeIcon.Undo, "Undo the last change (Ctrl+Z)", !readOnly && history.CanUndo))
            current.Undo();
        ImGui.SameLine(0, Theme.Scaled(2));
        if (Widgets.IconButton("##timeline-redo", FontAwesomeIcon.Redo, "Redo the change (Ctrl+Y)", !readOnly && history.CanRedo))
            current.Redo();

        ImGui.SameLine(0, Theme.Scaled(12));
        if (Widgets.IconButton("##timeline-add", FontAwesomeIcon.Plus, "Add an expression, lip sync, sound, effect or prop at the playhead, a bone layer, or your LivePose adjustments", !readOnly))
            ImGui.OpenPopup(AddMenu);
        DrawAddMenu(current);
        ImGui.SameLine(0, Theme.Scaled(2));
        if (Toggle("##timeline-snap", FontAwesomeIcon.Magnet, current.Snap,
                current.Snap ? "Snapping is on. Drags stick to starts, ends and the playhead. Hold Alt to place freely." : "Snapping is off."))
            current.Snap = !current.Snap;
        ImGui.SameLine(0, Theme.Scaled(2));
        if (Toggle("##timeline-scale", FontAwesomeIcon.ArrowsAltH, current.ScaleEvents,
                current.ScaleEvents ? "Scale events with length is on. A new length moves and stretches the events too." : "Scale events with length is off. A new length keeps the events where they are."))
            current.ScaleEvents = !current.ScaleEvents;
        ImGui.SameLine(0, Theme.Scaled(2));
        if (Widgets.IconButton("##timeline-fit", FontAwesomeIcon.Expand, "Show the whole timeline (F)"))
            current.Fit();
        // An expression's face takes no bone layers, so it has no bones to pose.
        if (!current.Document.Expression)
        {
            ImGui.SameLine(0, Theme.Scaled(2));
            if (Toggle("##timeline-bones", FontAwesomeIcon.Child, bonesWindow.IsOpen, bonesWindow.IsOpen
                    ? "Close the Bones window."
                    : "Open the Bones window: pick bones on a body map and pose them in the selected bone layer."))
                ToggleBones();
        }
        ImGui.SameLine(0, Theme.Scaled(2));
        DrawLoadButton();

        ImGui.SameLine(0, Theme.Scaled(12));
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, "Length");
        ImGui.SameLine(0, Theme.Gap);
        ImGui.SetNextItemWidth(Theme.Scaled(72));
        // While typing, the field shows what was typed; Enter or leaving it applies the length once.
        var seconds = lengthActive ? lengthEdit : current.Document.Length / (float)TimelineDocument.Fps;
        using (ImRaii.Disabled(readOnly || current.FixedLength))
        {
            if (ImGui.InputFloat("##timeline-length", ref seconds, 0, 0, "%.2f"))
                lengthEdit = seconds;
            if (ImGui.IsItemActivated())
                lengthEdit = seconds;
            lengthActive = ImGui.IsItemActive();
            if (ImGui.IsItemDeactivatedAfterEdit() && float.IsFinite(lengthEdit))
                current.SetLength(TimelineWindowViews.Frames(lengthEdit));
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(current.FixedLength
                ? "An expression's timeline keeps its length."
                : "Stretches the motion to this length. Press Enter to apply it. Trim it on the clip row.");
        ImGui.SameLine(0, Theme.Scaled(3));
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, "s");

        ImGui.SameLine(0, Theme.Scaled(16));
        if (current.Playhead != readoutFrame)
        {
            readoutFrame = current.Playhead;
            readout = "Playhead " + TimelineWindowViews.FrameText(readoutFrame);
        }
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Label, readout);
        ImGui.SameLine(0, Theme.Gap);
        Widgets.HelpTip(ControlsHelp);
    }

    /// <summary> An icon button that stays filled while its setting is on. </summary>
    private static bool Toggle(string id, FontAwesomeIcon icon, bool on, string tooltip)
    {
        using var colour = ImRaii.PushColor(ImGuiCol.Button, Theme.Selection, on)
            .Push(ImGuiCol.ButtonHovered, Theme.WithAlpha(Theme.Selection, .85f), on);
        return Widgets.IconButton(id, icon, tooltip);
    }

    private void DrawAddMenu(TimelineEditor current)
    {
        using var popup = ImRaii.Popup(AddMenu);
        if (!popup.Success)
            return;
        foreach (var kind in TimelineWindowViews.AddKinds)
            if (ImGui.MenuItem(TimelineWindowViews.AddKindName(kind)))
                RequestPicker(kind, current.Playhead, null);
        // An expression's face takes no bone layers.
        if (current.Document.Expression)
            return;
        ImGui.Separator();
        foreach (var kind in TimelineWindowViews.LayerKinds)
        {
            if (ImGui.MenuItem($"{MotionEdits.KindName(kind)} layer"))
                current.AddLayer(kind, 0, Math.Max(TimelineDocument.MinLength, current.Document.Length));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(TimelineWindowViews.LayerKindHint(kind) + " Covers the whole clip; drag its ends to narrow it.");
        }
        if (source == null)
            return;
        ImGui.Separator();
        DrawLivePoseItems(source);
    }

    /// <summary> One line each for an import running or done, an edit the editor refused and a header length the motion disagrees with. </summary>
    private void DrawMessages(TimelineEditor current)
    {
        if (headerNote != null)
            Note(null, Theme.Muted, headerNote);
        if (importTask != null && importing is { } work)
        {
            Widgets.Spinner();
            ImGui.SameLine(0, Theme.Gap);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Muted, $"Importing {work.Name}");
        }
        else if (importError != null)
        {
            if (Note("##import-error-dismiss", Theme.Warning, importError))
                importError = null;
        }
        else if (importMessage != null)
        {
            if (Note("##import-message-dismiss", Theme.Success, importMessage))
                importMessage = null;
        }
        if (current.LastError is { } error)
            Note(null, Theme.Warning, error);
        DrawLoaderStatus();
        DrawLivePoseNote();
        DrawEffectProblems(current);
        DrawSavedLayers();
    }

    /// <summary> Effects that stop although the animation goes on, and the button that keeps them going. </summary>
    private void DrawEffectProblems(TimelineEditor current)
    {
        if (effectWarning is not { } warning || effectProblems.IsEmpty)
            return;
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Warning, "⚠ " + warning);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(string.Join("\n", effectProblems.Select(p => p.Text)));
        ImGui.SameLine(0, Theme.Gap);
        using (ImRaii.Disabled(ReadOnly))
        {
            if (ImGui.SmallButton(effectEvents == 1 ? "Keep it looping" : "Keep them looping"))
            {
                // The copies are packaged like imports; the edit's own undo puts the original effects back.
                foreach (var copy in effectCopies)
                    imports[copy.GamePath] = copy;
                current.Commit(TimelineEffectCheck.Fix(current.Document, effectProblems));
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(effectCopies.IsEmpty
                ? "Sets Loop/wait to -1, which loops an effect until the animation ends. Save to keep the change."
                : "Sets Loop/wait to -1 and uses copies of the effects that keep going: no kill of their own, sounds that loop. " +
                  "Save to keep the change.");
    }

    /// <summary> A short coloured message with an optional dismiss button; true when dismissed. </summary>
    private static bool Note(string? dismissId, Vector4 colour, string text)
    {
        var dismissed = false;
        if (dismissId != null)
        {
            dismissed = Widgets.GhostIconButton(dismissId, FontAwesomeIcon.Times, "Dismiss");
            ImGui.SameLine(0, Theme.Gap);
        }
        ImGui.AlignTextToFramePadding();
        using var wrap = ImRaii.TextWrapPos(0f);
        ImGui.TextColored(colour, text);
        return dismissed;
    }
}
