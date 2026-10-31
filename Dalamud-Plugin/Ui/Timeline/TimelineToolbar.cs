using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// The action bar above the lanes: undo, redo, Add, the play bar, the length and on the right the Bones window; under it
/// for now snapping, scaling, fit, curves, Blender, load and Follow. Also the status line's notes.
/// </summary>
internal sealed class TimelineToolbar(TimelineSession session, TimelinePickers pickers, TimelineLoader loader, LivePosePanel livePose,
    TimelineTexts texts, SaveFooter saveFooter, Window bonesWindow, BlenderMotionPanel blenderMotion, CurveView curves,
    PlayBar playBar, TimelinePreview preview)
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
    // The Bones button's width last frame, to right-align it; 0 before it was drawn.
    private float bonesWidth;
    // Bones is on a line of its own, the bar leaving no room for it.
    private bool bonesBelow;

    /// <summary>
    /// Draws the bar for <paramref name="current"/>, the timeline of <paramref name="loaded"/>. Bones keeps to the right
    /// and moves to a line of its own when the bar leaves no room for it.
    /// </summary>
    public void Draw(TimelineEditor current, AnimationTimelineSource loaded)
    {
        var readOnly = session.ReadOnly;
        TimelineUi.DrawUndoRedo("##timeline", current, readOnly);

        ImGui.SameLine(0, Theme.Scaled(12));
        bool add;
        using (ImRaii.PushId("##timeline-add"))
        using (ImRaii.Disabled(readOnly))
            add = ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Plus, "Add", null, null, null, null);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Add an expression, lip sync, sound, effect or prop at the playhead, a bone layer, or a .livepose file");
        if (add && !readOnly)
            ImGui.OpenPopup(AddMenu);
        using (var popup = ImRaii.Popup(AddMenu))
        {
            if (popup.Success)
                DrawAddItems(current);
        }

        ImGui.SameLine(0, Theme.Scaled(12));
        playBar.Draw(current, loaded, saveFooter.RepairTarget, "##timeline-play");

        ImGui.SameLine(0, Theme.Scaled(12));
        if (current.Document.Face is { } face)
            DrawFaceLength(current, face, readOnly);
        else
            DrawLength(current, readOnly);

        // An expression's own timeline takes no bone layers, so it has no bones to pose; its face does.
        if (current.Document.Layered)
            DrawBonesButton(current);

        DrawLeftovers(current);
    }

    /// <summary> Bones, icon and label, filled while the Bones window is open: on the bar's right, or on a line of its own when the bar is full. </summary>
    private void DrawBonesButton(TimelineEditor current)
    {
        var width = bonesWidth > 0 ? bonesWidth : ImGui.CalcTextSize("Bones").X + ImGui.GetFrameHeight() * 2;
        var after = ImGui.GetItemRectMax().X - ImGui.GetWindowPos().X + Theme.Scaled(12);
        var right = ImGui.GetContentRegionMax().X - width;
        // Some room to spare before it comes back up, so a playhead time a digit longer doesn't make it jump.
        bonesBelow = bonesBelow ? right < after + Theme.Scaled(24) : right < after;
        if (!bonesBelow)
            ImGui.SameLine(right);
        else
            ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), right));
        bool clicked;
        using (ImRaii.PushId("##timeline-bones"))
            clicked = Widgets.TabButton(FontAwesomeIcon.Child, "Bones", bonesWindow.IsOpen);
        bonesWidth = ImGui.GetItemRectSize().X;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(bonesWindow.IsOpen
                ? "Close the Bones window."
                : current.Document.Face != null ? "Open the Bones window: pick face bones and pose them in the selected bone layer."
                : "Open the Bones window: pick bones on a body map and pose them in the selected bone layer.");
        if (clicked)
            TimelineUi.Toggle(bonesWindow);
    }

    /// <summary> The controls that move into the menus later, on a line under the bar: snapping, scaling, fit, curves, Blender, load, Follow and the help. </summary>
    private void DrawLeftovers(TimelineEditor current)
    {
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
        if (current.Document.Layered)
        {
            ImGui.SameLine(0, Theme.Scaled(2));
            if (TimelineUi.Toggle("##timeline-curves", FontAwesomeIcon.ChartLine, curves.Shown,
                    curves.Shown ? "Hide the curves." : "Show the selected bone's turns and moves over the clip, under the lanes."))
                curves.Shown = !curves.Shown;
            if (current.Document.Face == null)
            {
                ImGui.SameLine(0, Theme.Scaled(2));
                blenderMotion.DrawButton(current);
            }
        }
        ImGui.SameLine(0, Theme.Scaled(2));
        loader.DrawButton();

        ImGui.SameLine(0, Theme.Scaled(12));
        var follow = preview.Follow;
        if (ImGui.Checkbox("Follow", ref follow))
            preview.Follow = follow;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Outside the preview, move the playhead with this clip while your character plays it.");
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

    /// <summary>
    /// The Add menu's items, drawn into the popup or menu that is open: events at the playhead, bone layers, Pin feet, Motion
    /// keys, the loop-pose items and the .livepose file; for a face its layer kinds and Smooth jitter.
    /// </summary>
    public void DrawAddItems(TimelineEditor current)
    {
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
        if (session.Source == null)
            return;
        ImGui.Separator();
        livePose.DrawItems();
    }

    /// <summary>A face's part of the Add menu: the layers a face takes over the face and Smooth jitter.</summary>
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
    }

    /// <summary>
    /// The status line's notes from the editor and the panels: an edit the editor refused or noted, a header length the
    /// motion disagrees with, a pose that plays its events once, an import, the loader, effects that stop, Blender, saved
    /// layers and the .livepose note. Each kind keeps the order the status line shows it in.
    /// </summary>
    public IEnumerable<StatusNote> Notes(TimelineEditor current)
    {
        var notes = new List<StatusNote>();
        if (current.LastError is { } error)
            notes.Add(new StatusNote("edit-error", StatusKind.Error, error));
        if (current.LastNotice is { } notice)
            notes.Add(new StatusNote("edit-notice", StatusKind.Notice, notice));
        if (current.LastRemoval is { } removal && TimelineWindowViews.RemovalStatus(removal) is { } removed)
            notes.Add(new StatusNote("removal", StatusKind.Notice, removed));
        if (texts.HeaderNote != null)
            notes.Add(new StatusNote("header", StatusKind.Notice, texts.HeaderNote));
        if (texts.PoseLoopNote != null)
            notes.Add(new StatusNote("pose-loop", StatusKind.Notice, texts.PoseLoopNote));
        notes.AddRange(pickers.Notes());
        notes.AddRange(loader.Notes());
        if (EffectProblems(current) is { } effects)
            notes.Add(effects);
        notes.AddRange(blenderMotion.Notes(current));
        notes.AddRange(saveFooter.Notes());
        notes.AddRange(livePose.Notes());
        return notes;
    }

    /// <summary> Effects that stop although the animation goes on, with the button that keeps them going; null when none do. </summary>
    private StatusNote? EffectProblems(TimelineEditor current)
    {
        if (texts.EffectWarning is not { } warning || texts.EffectProblems.IsEmpty)
            return null;
        return new StatusNote("effects", StatusKind.Warning, "⚠ " + warning, texts.EffectEvents == 1 ? "Keep it looping" : "Keep them looping", () =>
            {
                // The copies are packaged like imports; the edit's own undo puts the original effects back.
                foreach (var copy in texts.EffectCopies)
                    session.Imports[copy.GamePath] = copy;
                current.Commit(TimelineEffectCheck.Fix(current.Document, texts.EffectProblems));
            },
            string.Join("\n", texts.EffectProblems.Select(p => p.Text)),
            texts.EffectCopies.IsEmpty
                ? "Sets Loop/wait to -1, which loops an effect until the animation ends. Save to keep the change."
                : "Sets Loop/wait to -1 and uses copies of the effects that keep going: no kill of their own, sounds that loop. " +
                  "Save to keep the change.",
            !session.ReadOnly);
    }
}
