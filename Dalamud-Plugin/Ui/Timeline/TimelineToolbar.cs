using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Ui.Timeline;

/// <summary> The row above the lanes: undo, redo, add, snapping, fit, the Bones window, load, the length and the playhead, then short messages. </summary>
internal sealed class TimelineToolbar(TimelineSession session, TimelinePickers pickers, TimelineLoader loader, LivePosePanel livePose,
    TimelineTexts texts, SaveFooter saveFooter, Window bonesWindow, BlenderMotionPanel blenderMotion)
{
    private const string AddMenu = "##timeline-add-menu";
    private const string ControlsHelp =
        "Drag an event to move it. Drag its edges to resize it.\n" +
        "Drag the grip at the end of the ruler to change the length.\n" +
        "Click or drag the ruler to move the playhead.\n" +
        "Wheel scrolls. Ctrl+wheel zooms. Shift+wheel scrolls the lanes.\n" +
        "Hold Alt while dragging to place freely.\n" +
        "Right-click the clip row to cut the motion or remove the part before or after a frame.\n" +
        "Drag a part's edges on the clip row to trim it. B plays a blend in the selected part's place.\n" +
        "Shift+drag a part's end to play it slower or faster.\n" +
        "Right-click for more. Double-click a lane to add to it.";

    private bool lengthActive;
    private float lengthEdit;
    private int readoutFrame = -1;
    private string readout = string.Empty;

    public void Draw(TimelineEditor current)
    {
        var readOnly = session.ReadOnly;
        var history = current.History;
        if (Widgets.IconButton("##timeline-undo", FontAwesomeIcon.Undo, "Undo the last change (Ctrl+Z)", !readOnly && history.CanUndo))
            current.Undo();
        ImGui.SameLine(0, Theme.Scaled(2));
        if (Widgets.IconButton("##timeline-redo", FontAwesomeIcon.Redo, "Redo the change (Ctrl+Y)", !readOnly && history.CanRedo))
            current.Redo();

        ImGui.SameLine(0, Theme.Scaled(12));
        if (Widgets.IconButton("##timeline-add", FontAwesomeIcon.Plus, "Add an expression, lip sync, sound, effect or prop at the playhead, a bone layer, or a .livepose file", !readOnly))
            ImGui.OpenPopup(AddMenu);
        DrawAddMenu(current);
        ImGui.SameLine(0, Theme.Scaled(2));
        if (TimelineUi.Toggle("##timeline-snap", FontAwesomeIcon.Magnet, current.Snap,
                current.Snap ? "Snapping is on. Drags stick to starts, ends and the playhead. Hold Alt to place freely." : "Snapping is off."))
            current.Snap = !current.Snap;
        ImGui.SameLine(0, Theme.Scaled(2));
        if (TimelineUi.Toggle("##timeline-scale", FontAwesomeIcon.ArrowsAltH, current.ScaleEvents,
                current.ScaleEvents ? "Scale events with length is on. A new length moves and stretches the events too." : "Scale events with length is off. A new length keeps the events where they are."))
            current.ScaleEvents = !current.ScaleEvents;
        ImGui.SameLine(0, Theme.Scaled(2));
        if (Widgets.IconButton("##timeline-fit", FontAwesomeIcon.Expand, "Show the whole timeline (F)"))
            current.Fit();
        // An expression's own timeline takes no bone layers, so it has no bones to pose; its face does.
        if (current.Document.Layered)
        {
            ImGui.SameLine(0, Theme.Scaled(2));
            if (TimelineUi.Toggle("##timeline-bones", FontAwesomeIcon.Child, bonesWindow.IsOpen, bonesWindow.IsOpen
                    ? "Close the Bones window."
                    : current.Document.Face != null ? "Open the Bones window: pick face bones and pose them in the selected bone layer."
                    : "Open the Bones window: pick bones on a body map and pose them in the selected bone layer."))
                TimelineUi.Toggle(bonesWindow);
            if (current.Document.Face == null)
            {
                ImGui.SameLine(0, Theme.Scaled(2));
                blenderMotion.DrawButton(current);
            }
        }
        ImGui.SameLine(0, Theme.Scaled(2));
        loader.DrawButton();

        ImGui.SameLine(0, Theme.Scaled(12));
        if (current.Document.Face is { } face)
            DrawFaceLength(current, face, readOnly);
        else
            DrawLength(current, readOnly);

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

    /// <summary>
    /// A face's own length: Still (one shape, held) or Moving, then its length in seconds. Turning Moving on or off holds the
    /// face's first frame; a moving face's seconds change its length.
    /// </summary>
    private void DrawFaceLength(TimelineEditor current, TimelineFace face, bool readOnly)
    {
        var unavailable = blenderMotion.FaceUnavailable(current);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, "Face");
        ImGui.SameLine(0, Theme.Gap);
        var moves = !face.Still;
        using (ImRaii.Disabled(readOnly || unavailable != null))
        {
            if (ImGui.Checkbox("Moves##timeline-face-moves", ref moves))
                blenderMotion.HoldFace(current, moves ? DefaultMovingFrames : 1);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(unavailable ?? (face.Still ? "Still holds one shape. Tick to make the face move over a length." : "Untick to make the face one shape again."));
        if (face.Still)
            return;
        ImGui.SameLine(0, Theme.Gap);
        ImGui.SetNextItemWidth(Theme.Scaled(72));
        var seconds = lengthActive ? lengthEdit : face.Frames / (float)TimelineDocument.Fps;
        using (ImRaii.Disabled(readOnly))
        {
            if (ImGui.InputFloat("##timeline-face-length", ref seconds, 0, 0, "%.2f"))
                lengthEdit = seconds;
            if (ImGui.IsItemActivated())
                lengthEdit = seconds;
            lengthActive = ImGui.IsItemActive();
            if (ImGui.IsItemDeactivatedAfterEdit() && float.IsFinite(lengthEdit))
                current.SetFaceLength(TimelineWindowViews.Frames(lengthEdit));
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("The face's length. Press Enter to apply it.");
        ImGui.SameLine(0, Theme.Scaled(3));
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, "s");
    }

    /// <summary>How long a still face becomes when it starts moving, in frames: 3 seconds.</summary>
    private const int DefaultMovingFrames = 3 * TimelineDocument.Fps;

    /// <summary>The clip's length in seconds, which stretches the motion; an expression's timeline keeps its own.</summary>
    private void DrawLength(TimelineEditor current, bool readOnly)
    {
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
    }

    private void DrawAddMenu(TimelineEditor current)
    {
        using var popup = ImRaii.Popup(AddMenu);
        if (!popup.Success)
            return;
        // A face opened on its own plays no events of its own; an expression's timeline does.
        if (current.Document.HasClip || current.Document.Expression)
            foreach (var kind in TimelineWindowViews.AddKinds)
                if (ImGui.MenuItem(TimelineWindowViews.AddKindName(kind)))
                    pickers.Request(kind, current.Playhead, null);
        if (current.Document.Face is { } face)
        {
            DrawFaceAddItems(current, face);
            return;
        }
        // An expression's own timeline takes no bone layers.
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
        var skeleton = session.EditingSkeleton();
        var feet = TimelineWindowViews.CanPinFeet(skeleton);
        if (ImGui.MenuItem(TimelineWindowViews.PinFeetName, "", false, feet))
            current.AddPinFeet(skeleton);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(TimelineWindowViews.PinFeetTip(skeleton));
        // As the clip row's Edit motion keys: without markers the layer would have nothing to fade out over.
        var markers = current.ShownMarkers != null;
        if (ImGui.MenuItem(MotionEdits.MotionKeysName, "", false, !session.ReadOnly && markers))
            current.EditMotionKeys(current.Playhead);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(TimelineWindowViews.MotionKeysTip(markers));
        session.LoopPose.DrawAddItems(current, session.ReadOnly, (label, enabled) => ImGui.MenuItem(label, "", false, enabled));
        ImGui.Separator();
        var still = blenderMotion.StillUnavailable(current);
        if (ImGui.MenuItem("Still pose (start from scratch)", "", false, still == null))
            blenderMotion.HoldStill(current);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(still ?? "Hold the pose at the playhead for the whole clip, in place of its motion, and add a pose layer to animate it. " +
                "Use the clip's motion goes back.");
        if (session.Source == null)
            return;
        ImGui.Separator();
        livePose.DrawItems();
    }

    /// <summary>A face's part of the Add menu: the layers a face takes over the face, Smooth jitter and Neutral face.</summary>
    private void DrawFaceAddItems(TimelineEditor current, TimelineFace face)
    {
        ImGui.Separator();
        var end = face.Anchor + Math.Max(TimelineDocument.MinLength, face.Frames);
        foreach (var kind in TimelineWindowViews.FaceLayerKinds)
        {
            if (ImGui.MenuItem($"{MotionEdits.KindName(kind)} layer"))
                current.AddLayer(kind, face.Anchor, end);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(TimelineWindowViews.LayerKindHint(kind) + " Covers the whole face.");
        }
        if (ImGui.MenuItem("Smooth jitter", "", false, !session.ReadOnly))
            current.AddFix(new MotionFix(MotionFixKind.SmoothJitter));
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(TimelineWindowViews.SmoothJitterHint);
        ImGui.Separator();
        var unavailable = blenderMotion.FaceUnavailable(current);
        if (ImGui.MenuItem("Neutral face", "", false, unavailable == null))
            blenderMotion.NeutralFace(current);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(unavailable ?? "Start from the face at rest, and add a pose layer to key the new face.");
    }

    /// <summary>
    /// One line each for an import running or done, an edit the editor refused or noted, a header length the motion
    /// disagrees with and a pose that plays its events once.
    /// </summary>
    public void DrawMessages(TimelineEditor current)
    {
        if (texts.HeaderNote != null)
            TimelineUi.Note(null, Theme.Muted, texts.HeaderNote);
        if (texts.PoseLoopNote != null)
            TimelineUi.Note(null, Theme.Muted, texts.PoseLoopNote);
        pickers.DrawImportStatus();
        if (current.LastError is { } error)
            TimelineUi.Note(null, Theme.Warning, error);
        if (current.LastNotice is { } notice)
            TimelineUi.Note(null, Theme.Muted, notice);
        if (current.LastRemoval is { } removal && TimelineWindowViews.RemovalStatus(removal) is { } removed)
            TimelineUi.Note(null, Theme.Muted, removed);
        loader.DrawStatus();
        livePose.DrawNote();
        DrawEffectProblems(current);
        blenderMotion.DrawStatus(current);
        saveFooter.DrawSavedLayers();
    }

    /// <summary> Effects that stop although the animation goes on, and the button that keeps them going. </summary>
    private void DrawEffectProblems(TimelineEditor current)
    {
        if (texts.EffectWarning is not { } warning || texts.EffectProblems.IsEmpty)
            return;
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Warning, "⚠ " + warning);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(string.Join("\n", texts.EffectProblems.Select(p => p.Text)));
        ImGui.SameLine(0, Theme.Gap);
        using (ImRaii.Disabled(session.ReadOnly))
        {
            if (ImGui.SmallButton(texts.EffectEvents == 1 ? "Keep it looping" : "Keep them looping"))
            {
                // The copies are packaged like imports; the edit's own undo puts the original effects back.
                foreach (var copy in texts.EffectCopies)
                    session.Imports[copy.GamePath] = copy;
                current.Commit(TimelineEffectCheck.Fix(current.Document, texts.EffectProblems));
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(texts.EffectCopies.IsEmpty
                ? "Sets Loop/wait to -1, which loops an effect until the animation ends. Save to keep the change."
                : "Sets Loop/wait to -1 and uses copies of the effects that keep going: no kill of their own, sounds that loop. " +
                  "Save to keep the change.");
    }
}
