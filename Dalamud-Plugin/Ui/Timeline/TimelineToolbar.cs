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
/// The action bar above the lanes, on one row: undo, redo, Add, the play bar and on the right the Bones window.
/// Also the Add menu's items and the status line's notes.
/// </summary>
internal sealed class TimelineToolbar(TimelineSession session, TimelinePickers pickers, TimelineLoader loader, LivePosePanel livePose,
    TimelineTexts texts, SaveFooter saveFooter, Window bonesWindow, BlenderMotionPanel blenderMotion, PlayBar playBar)
{
    private const string AddMenu = "##timeline-add-menu";

    /// <summary>The Add button's tooltip, which the Add menu repeats.</summary>
    public const string AddTip = "Add an expression, lip sync, sound, effect or prop at the playhead, a bone layer, or a .livepose file";

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
            ImGui.SetTooltip(AddTip);
        if (add && !readOnly)
            ImGui.OpenPopup(AddMenu);
        using (var popup = ImRaii.Popup(AddMenu))
        {
            if (popup.Success)
                DrawAddItems(current);
        }

        ImGui.SameLine(0, Theme.Scaled(12));
        playBar.Draw(current, loaded, saveFooter.RepairTarget, "##timeline-play");

        // An expression's own timeline takes no bone layers, so it has no bones to pose; its face does.
        if (current.Document.Layered)
            DrawBonesButton(current);
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
            ImGui.SetTooltip(TimelineUi.BonesWindowTip(bonesWindow.IsOpen, current.Document.Face != null));
        if (clicked)
            TimelineUi.Toggle(bonesWindow);
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
