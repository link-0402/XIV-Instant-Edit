using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// The action bar above the lanes, on one row: undo, redo, the play bar and on the right Curves, the free camera and the Bones
/// window. Also the status line's notes. Live and the time are in the header; layers are added from the lanes.
/// </summary>
internal sealed class TimelineToolbar(TimelineSession session, TimelinePickers pickers, TimelineLoader loader, LivePosePanel livePose,
    TimelineTexts texts, SaveFooter saveFooter, Window bonesWindow, BlenderMotionPanel blenderMotion, PlayBar playBar, FreeCamera freeCamera,
    CurveView curves)
{
    /// <summary>The reason an item waits while the timeline reads, saves or builds.</summary>
    public const string Busy = "Wait for the timeline to finish.";

    // The right end's width last frame and which switches were in it, to right-align it; 0 before it was drawn.
    private float rightWidth;
    private bool rightBones;
    // The right end is on a line of its own, the bar leaving no room for it.
    private bool rightBelow;

    /// <summary> The free camera's tooltip: its keys and its speed now, in metres a second. </summary>
    internal static string FreeCameraTip(float speed) =>
        $"Fly the GPose camera with WASD, Space and Ctrl.\nDrag to look. The wheel sets the speed ({speed:0.#} m/s), Shift goes faster.";

    /// <summary>
    /// Draws the bar for <paramref name="current"/>. Curves, Free camera and Bones keep to
    /// the right and move to a line of their own when the bar leaves no room for them.
    /// </summary>
    public void Draw(TimelineEditor current)
    {
        TimelineUi.DrawUndoRedo("##timeline", current, session.ReadOnly);
        ImGui.SameLine(0, Theme.Scaled(12));
        playBar.Draw(current, "##timeline-play", live: false, time: false);
        DrawRight(current);
    }

    /// <summary>
    /// The bar's right end: Curves and Bones when the document takes bone layers (an expression's own timeline has no bones to
    /// pose; its face does), and Free camera. On the bar's right, or on a line of its own when the bar is full.
    /// </summary>
    private void DrawRight(TimelineEditor current)
    {
        var bones = current.Document.Layered;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var width = rightWidth > 0 && rightBones == bones ? rightWidth
            : ButtonWidth("Free camera") + (bones ? 2 * spacing + ButtonWidth("Curves") + ButtonWidth("Bones") : 0);
        var after = ImGui.GetItemRectMax().X - ImGui.GetWindowPos().X + Theme.Scaled(12);
        var right = ImGui.GetContentRegionMax().X - width;
        // Some room to spare before it comes back up, so the bar doesn't jump back and forth at one width.
        rightBelow = rightBelow ? right < after + Theme.Scaled(24) : right < after;
        if (!rightBelow)
            ImGui.SameLine(right);
        else
            ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), right));
        var start = ImGui.GetCursorScreenPos().X;
        if (bones)
        {
            DrawCurves();
            ImGui.SameLine();
        }
        DrawFreeCamera();
        if (bones)
        {
            ImGui.SameLine();
            DrawBonesButton(current);
        }
        rightWidth = ImGui.GetItemRectMax().X - start;
        rightBones = bones;
    }

    private static float ButtonWidth(string label) => ImGui.CalcTextSize(label).X + ImGui.GetFrameHeight() * 2;

    /// <summary> Curves, icon and label, filled while the curve strip shows under the lanes. </summary>
    private void DrawCurves()
    {
        bool clicked;
        using (ImRaii.PushId("##timeline-curves"))
            clicked = Widgets.TabButton(FontAwesomeIcon.ChartLine, "Curves", curves.Shown);
        TimelineUi.Tip(curves.Shown ? "Hide the curves." : "Show the selected bone's turns and moves over the clip, under the lanes.");
        if (clicked)
            curves.Shown = !curves.Shown;
    }

    /// <summary> Free camera, icon and label, filled while it flies; it comes on only in GPose. </summary>
    private void DrawFreeCamera()
    {
        var blocked = freeCamera.On ? null : freeCamera.Blocked;
        bool clicked;
        using (ImRaii.PushId("##timeline-free-camera"))
        using (ImRaii.Disabled(blocked != null))
            clicked = Widgets.TabButton(FontAwesomeIcon.Camera, "Free camera", freeCamera.On);
        TimelineUi.Tip(blocked ?? FreeCameraTip(freeCamera.Speed));
        if (clicked && blocked == null)
        {
            freeCamera.Toggle();
            // The keys fly it at once: the game gets the keyboard, not this window and its shortcuts.
            if (freeCamera.On)
                ImGuiP.FocusWindow(default(ImGuiWindowPtr));
        }
    }

    /// <summary> Bones, icon and label, filled while the Bones window is open. </summary>
    private void DrawBonesButton(TimelineEditor current)
    {
        bool clicked;
        using (ImRaii.PushId("##timeline-bones"))
            clicked = Widgets.TabButton(FontAwesomeIcon.Child, "Bones", bonesWindow.IsOpen);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(TimelineUi.BonesWindowTip(bonesWindow.IsOpen, current.Document.Face != null));
        if (clicked)
            TimelineUi.Toggle(bonesWindow);
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
