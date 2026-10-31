using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using InstantEdit.Models;
using InstantEdit.Services.Animations;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// The Timeline window's menu bar while a clip is open: Animation, Edit, Add, View and Help. Disabled items give their reason
/// as a tooltip; items that don't apply to the open document stay hidden.
/// </summary>
/// <param name="showStart">Shows the start page, on an entry or with null on the last one shown.</param>
/// <param name="reload">Reads the open clip again, asking first when it has unsaved edits.</param>
/// <param name="open">Opens the timeline of a capture's clip, or of its startup.</param>
internal sealed class TimelineMenus(TimelineSession session, TimelineCanvas canvas, TimelineToolbar toolbar, BlenderMotionPanel blenderMotion,
    CurveView curves, Window bonesWindow, TimelinePreview preview, Action<StartEntry?> showStart, Action reload,
    Action<AnimationCapture, bool> open)
{
    private const string HelpPopup = "##timeline-help";
    private const string Busy = "Wait for the timeline to finish.";
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

    /// <summary>
    /// Draws the menu bar for <paramref name="current"/>, the timeline of <paramref name="loaded"/>; the window must have
    /// <see cref="ImGuiWindowFlags.MenuBar"/> set. Then the help popup, when Mouse and keys opened it.
    /// </summary>
    public void Draw(TimelineEditor current, AnimationTimelineSource loaded)
    {
        var help = false;
        using (var bar = ImRaii.MenuBar())
        {
            if (bar.Success)
            {
                DrawAnimation(current, loaded);
                DrawEdit(current);
                DrawAdd(current);
                DrawView(current);
                help = DrawHelp();
            }
        }
        // Opened here, outside the menu, so the popup's id is the window's.
        if (help)
            ImGui.OpenPopup(HelpPopup);
        using var popup = ImRaii.Popup(HelpPopup);
        if (popup.Success)
            ImGui.TextUnformatted(ControlsHelp);
    }

    /// <summary>
    /// Animation: the start page, another clip, Reload, the startup or loop; then Blender's motion and Still pose, or a
    /// face's Neutral face.
    /// </summary>
    private void DrawAnimation(TimelineEditor current, AnimationTimelineSource loaded)
    {
        using var menu = ImRaii.Menu("Animation");
        if (!menu.Success)
            return;
        var readOnly = session.ReadOnly;
        if (ImGui.MenuItem("Start page"))
            showStart(null);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Go to the start page. This clip stays open.");
        if (ImGui.MenuItem("Load another"))
            showStart(StartEntry.Game);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Pick another animation on the start page.");
        var doc = current.Document;
        // A face opened with Edit face belongs to its clip's capture: reading that again would open the clip, not the face.
        if (doc.Face == null)
        {
            if (ImGui.MenuItem("Reload", "", false, !readOnly))
                reload();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(readOnly ? Busy : "Read this clip again from your mods.");
        }
        var fresh = session.Fresh(loaded);
        if (doc.Face == null && !fresh.Standalone && (fresh.Startup || fresh.Capture.Startup != null))
        {
            if (ImGui.MenuItem(fresh.Startup ? "Open loop" : "Open startup", "", false, !readOnly))
                open(fresh.Capture, !fresh.Startup);
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(readOnly ? Busy : fresh.Startup
                    ? $"Open {AnimationPresentation.AnimationName(fresh.Capture)}, which this startup leads into."
                    : $"Open the startup's timeline, {AnimationPresentation.AnimationName(fresh.Capture, true)}.");
        }

        // A face takes neither: it has no body motion to send, nor one to hold still.
        if (doc.Layered && doc.Face == null)
        {
            ImGui.Separator();
            blenderMotion.DrawMenuItems(current);
        }
        if (doc.Face != null)
        {
            ImGui.Separator();
            var unavailable = blenderMotion.FaceUnavailable(current);
            if (ImGui.MenuItem("Neutral face", "", false, unavailable == null))
                blenderMotion.NeutralFace(current);
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(unavailable ?? "Start from the face at rest, and add a pose layer to key the new face.");
            return;
        }
        // An expression's own timeline has no motion to hold.
        if (doc.Expression)
            return;
        ImGui.Separator();
        var still = blenderMotion.StillUnavailable(current);
        if (ImGui.MenuItem("Still pose (start from scratch)", "", false, still == null))
            blenderMotion.HoldStill(current);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(still ?? "Hold the pose at the playhead for the whole clip, in place of its motion, and add a pose layer to animate it. " +
                "Use the clip's motion goes back.");
        // The way back from a still pose, or from Blender's motion: the status line offers it too.
        if (doc.Replacement == null)
            return;
        if (ImGui.MenuItem("Use the clip's motion", "", false, !readOnly))
            current.DropReplacement();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(readOnly ? Busy : "Go back to the animation's own motion. Ctrl+Z works too.");
    }

    /// <summary> Edit: Undo and Redo, then the selection's items as its right-click menu has them. </summary>
    private void DrawEdit(TimelineEditor current)
    {
        using var menu = ImRaii.Menu("Edit");
        if (!menu.Success)
            return;
        var readOnly = session.ReadOnly;
        var history = current.History;
        if (ImGui.MenuItem("Undo", "Ctrl+Z", false, !readOnly && history.CanUndo))
            current.Undo();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(readOnly ? Busy : history.CanUndo ? "Undo the last change." : "Nothing to undo.");
        if (ImGui.MenuItem("Redo", "Ctrl+Y", false, !readOnly && history.CanRedo))
            current.Redo();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(readOnly ? Busy : history.CanRedo ? "Redo the change." : "Nothing to redo.");
        ImGui.Separator();
        canvas.DrawSelectionItems(current, readOnly);
    }

    /// <summary> Add: the action bar's Add menu. </summary>
    private void DrawAdd(TimelineEditor current)
    {
        var readOnly = session.ReadOnly;
        using var menu = ImRaii.Menu("Add", !readOnly);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && !menu.Success)
            ImGui.SetTooltip(readOnly ? Busy : TimelineToolbar.AddTip);
        if (menu.Success)
            toolbar.DrawAddItems(current);
    }

    /// <summary> View: Fit; Snap, Scale events with length and Follow; for a document with bone layers Curves and the Bones window. </summary>
    private void DrawView(TimelineEditor current)
    {
        using var menu = ImRaii.Menu("View");
        if (!menu.Success)
            return;
        if (ImGui.MenuItem("Fit", "F"))
            current.Fit();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Show the whole timeline.");
        ImGui.Separator();
        if (ImGui.MenuItem("Snap", "", current.Snap))
            current.Snap = !current.Snap;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(current.Snap ? "Snapping is on. Drags stick to starts, ends and the playhead. Hold Alt to place freely." : "Snapping is off.");
        if (ImGui.MenuItem("Scale events with length", "", current.ScaleEvents))
            current.ScaleEvents = !current.ScaleEvents;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(current.ScaleEvents ? "Scale events with length is on. A new length moves and stretches the events too."
                : "Scale events with length is off. A new length keeps the events where they are.");
        if (ImGui.MenuItem("Follow", "", preview.Follow))
            preview.Follow = !preview.Follow;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Outside the preview, move the playhead with this clip while your character plays it.");
        // An expression's own timeline takes no bone layers, so it has no curves and no bones to pose; its face does.
        if (!current.Document.Layered)
            return;
        ImGui.Separator();
        if (ImGui.MenuItem("Curves", "", curves.Shown))
            curves.Shown = !curves.Shown;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(curves.Shown ? "Hide the curves." : "Show the selected bone's turns and moves over the clip, under the lanes.");
        if (ImGui.MenuItem("Bones window", "", bonesWindow.IsOpen))
            TimelineUi.Toggle(bonesWindow);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(bonesWindow.IsOpen
                ? "Close the Bones window."
                : current.Document.Face != null ? "Open the Bones window: pick face bones and pose them in the selected bone layer."
                : "Open the Bones window: pick bones on a body map and pose them in the selected bone layer.");
    }

    /// <summary> Help: Mouse and keys; true when it was clicked, so the help popup opens. </summary>
    private static bool DrawHelp()
    {
        using var menu = ImRaii.Menu("Help");
        if (!menu.Success)
            return false;
        var clicked = ImGui.MenuItem("Mouse and keys");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("How to edit on the lanes with the mouse and keys.");
        return clicked;
    }
}
