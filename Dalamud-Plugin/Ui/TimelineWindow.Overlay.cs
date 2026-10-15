using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Bindings.ImGuizmo;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Preview;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui.Pose;
using InstantEdit.Ui.Timeline;

namespace InstantEdit.Ui;

/// <summary>
/// The bones on the posed character, drawn for <see cref="BonesOverlay"/> while the Bones window is open
/// in GPose: a dot where the game shows each of the map's bones (and the selected ones and the ones the
/// layer changes), joined to the nearest one above it and coloured like the map, and the gizmo on the
/// primary bone. A click on a dot selects like the map. With Live on and paused, a gizmo drag turns or
/// moves the bones the edit changes in the selected layer at the playhead, as one undo step. Adapted
/// from Brio's posing overlay (GPL-3.0).
/// </summary>
public sealed partial class TimelineWindow
{
    // ImGuizmo's state is shared by every plugin; this id keeps our drags apart from Brio's gizmo.
    private const int GizmoId = 0x1E5E;
    // ImGuizmo's id when none is set.
    private const int NoGizmoId = -1;
    // How long the gizmo stays where a drag left it while Live applies the change: frames once Live
    // shows it, and frames at most.
    private const int GizmoShownFrames = 2, GizmoHoldFrames = 240;

    private readonly BonesOverlay overlayWindow;
    private readonly ActorPose actorPose = new();
    private bool showOnCharacter = true;
    private bool gizmoMove;
    // The editor a gizmo drag runs in as one undo step.
    private TimelineEditor? gizmoEditor;
    // Whether the mouse was over the gizmo's rings or handles last frame: then the gizmo takes the click, not a dot.
    private bool gizmoOver;
    // Where the last drag left the gizmo on a bone, layer and frame, until Live shows the change there.
    private (string Layer, string Bone, int Frame, Matrix4x4 Matrix, int Since, int Shown)? gizmoHeld;
    // Each bone's dot this frame, by the clip skeleton's bone index.
    private Vector2[] dotAt = [];
    private bool[] dotShown = [];

    internal BonesOverlay Overlay => overlayWindow;

    /// <summary>Whether the bones show on the character now: the Bones window is open on a body clip, in GPose.</summary>
    internal bool OverlayWanted => showOnCharacter && IsOpen && bonesWindow.IsOpen && animations != null &&
                                   source is { Document: not null, Standalone: false } && editor != null && OverlayActor() != null;

    /// <summary>A gizmo drag is running: the overlay takes the mouse.</summary>
    internal bool OverlayDragging => gizmoEditor != null;

    private bool LiveShowing => liveWanted && liveStarted && motionDisplay?.Active == true;

    /// <summary>The character the bones show on: the one Live or the preview plays on, else the GPose actor; null outside GPose.</summary>
    private ushort? OverlayActor()
    {
        try
        {
            if (playback.ActorIndex() is not { } posing)
                return null;
            // Not while Live only waits to start: the actor of an earlier preview may be another one.
            return (liveStarted || playback.Active) && previewActor is { } shown ? shown : posing;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The overlay's whole content; called from its draw.</summary>
    internal void DrawBonesOverlay()
    {
        var overGizmo = gizmoOver;
        gizmoOver = false;
        if (animations == null || source is not { Document: not null, Standalone: false } loaded || editor is not { } current)
        {
            EndGizmo(true);
            return;
        }
        var clip = Fresh(loaded).Clip;
        if (clip.Resolution?.Selected?.Skeleton is not { } skeleton || OverlayActor() is not { } actor || !ReadActorPose(actor) ||
            GameCamera.Read() is not { } camera)
        {
            EndGizmo(true);
            return;
        }
        KeepSelection(skeleton);
        var layer = current.SelectedLayerValue;
        var frame = current.Playhead;
        var viewport = ImGui.GetMainViewport();
        var modelView = PoseGizmo.World(actorPose.Position, actorPose.Rotation, actorPose.Scale) * camera.View;
        var toClip = modelView * camera.Projection;
        var (index, _) = BoneTree(skeleton);
        var animated = AnimatedBones(clip);

        var count = skeleton.Bones.Length;
        if (dotAt.Length != count)
        {
            dotAt = new Vector2[count];
            dotShown = new bool[count];
        }
        for (var b = 0; b < count; b++)
        {
            var name = skeleton.Bones[b].Name;
            dotShown[b] = (PoseMap.Placed.Contains(name) || selection.Contains(name) || layer != null && PoseEdits.Changes(layer, name)) &&
                          actorPose.Index.TryGetValue(name, out var at) &&
                          PoseGizmo.Project(actorPose.Positions[at], toClip, viewport.Pos, viewport.Size, out dotAt[b]);
        }

        // A dot takes the mouse when no window, field, popup, drag or the gizmo has it.
        var hover = -1;
        if (gizmoEditor == null && !overGizmo && !ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow) && !ImGui.IsAnyItemActive() &&
            !ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel) &&
            (!ImGui.IsAnyMouseDown() || ImGui.IsMouseClicked(ImGuiMouseButton.Left)))
        {
            var mouse = ImGui.GetMousePos();
            var best = Theme.Scaled(10);
            for (var b = 0; b < count; b++)
            {
                if (!dotShown[b])
                    continue;
                var distance = Vector2.Distance(dotAt[b], mouse);
                if (distance < best)
                {
                    best = distance;
                    hover = b;
                }
            }
        }

        var primary = selection.Primary is { } chosen && index.TryGetValue(chosen, out var chosenIndex) ? chosenIndex : -1;
        DrawOverlayBones(skeleton, layer, frame, animated, primary, hover);
        DrawGizmo(current, loaded, skeleton, layer, frame, modelView, camera.Projection, viewport);

        // The mouse reached the gizmo this frame: it takes the click.
        if (hover < 0 || gizmoOver)
            return;
        // The game must not take the click as well.
        ImGui.SetNextFrameWantCaptureMouse(true);
        var bone = skeleton.Bones[hover].Name;
        ImGui.SetTooltip($"{AnimationPresentation.BoneName(bone)}\n{StateText(StateOf(bone, layer, frame, animated), layer)}");
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            ClickBone(bone, skeleton);
    }

    /// <summary>
    /// The dots and the lines joining each to the nearest dot above it, drawn like the map's: the lines
    /// below the primary bone lit, the dots coloured by state, the selected ones filled.
    /// </summary>
    private void DrawOverlayBones(SkeletonDescription skeleton, MotionLayer? layer, int frame, HashSet<string>? animated, int primary, int hover)
    {
        var drawList = ImGui.GetWindowDrawList();
        var radius = Theme.Scaled(5);
        var shadow = ImGui.GetColorU32(new Vector4(0, 0, 0, .5f));
        var lineColour = ImGui.GetColorU32(Theme.WithAlpha(Theme.Label, .7f));
        var litColour = ImGui.GetColorU32(Theme.WithAlpha(Theme.Accent, .9f));
        for (var b = 0; b < dotShown.Length; b++)
        {
            if (!dotShown[b])
                continue;
            var above = skeleton.Bones[b].Parent;
            while (above >= 0 && !dotShown[above])
                above = skeleton.Bones[above].Parent;
            if (above < 0)
                continue;
            var along = dotAt[above] - dotAt[b];
            if (along.LengthSquared() <= radius * radius * 4)
                continue;
            var step = Vector2.Normalize(along) * radius;
            // A lit line: the bone moves with the primary bone.
            var lit = IsBelow(skeleton, b, primary);
            drawList.AddLine(dotAt[b] + step, dotAt[above] - step, shadow, Theme.Scaled(lit ? 4 : 3));
            drawList.AddLine(dotAt[b] + step, dotAt[above] - step, lit ? litColour : lineColour, Theme.Scaled(lit ? 2 : 1.5f));
        }
        for (var b = 0; b < dotShown.Length; b++)
        {
            if (!dotShown[b])
                continue;
            var name = skeleton.Bones[b].Name;
            var at = dotAt[b];
            var state = StateOf(name, layer, frame, animated);
            var colour = ImGui.GetColorU32(StateColour(state, layer));
            drawList.AddCircleFilled(at, radius + Theme.Scaled(1.5f), shadow);
            drawList.AddCircleFilled(at, radius, ImGui.GetColorU32(selection.Contains(name)
                ? Theme.WithAlpha(Theme.Accent, .9f)
                : Theme.WithAlpha(Theme.PanelBg, .75f)));
            drawList.AddCircle(at, radius, colour, 0, Theme.Scaled(state is BoneState.Changed or BoneState.Keyed ? 2.5f : 1.5f));
            if (state == BoneState.Keyed)
                drawList.AddCircleFilled(at, radius * .45f, colour);
            if (b == primary)
                drawList.AddCircle(at, radius + Theme.Scaled(3), ImGui.GetColorU32(Theme.Accent), 0, Theme.Scaled(1.5f));
            if (b == hover)
                drawList.AddCircle(at, radius + Theme.Scaled(1), ImGui.GetColorU32(Theme.Text), 0, Theme.Scaled(1.5f));
        }
    }

    /// <summary>
    /// The gizmo on the primary bone, in the character's model space: its own axes are the bone's and its
    /// world axes the character's, forced with several bones as for the ball. Each frame of a drag turns or
    /// moves every bone the edit changes (see <see cref="PoseEdits.Turned"/>) by what the gizmo did since
    /// the frame before, in character space. While the mouse is over its rings or handles it takes the
    /// click, and the dots under it don't (<see cref="gizmoOver"/>); a click elsewhere goes to a dot.
    /// </summary>
    private void DrawGizmo(TimelineEditor current, AnimationTimelineSource loaded, SkeletonDescription skeleton, MotionLayer? layer, int frame,
        Matrix4x4 modelView, Matrix4x4 projection, ImGuiViewportPtr viewport)
    {
        if (layer == null || GizmoOperation(layer, frame, out _) is not { } operation || selection.Primary is not { } bone ||
            !actorPose.Index.TryGetValue(bone, out var at) || Targets(layer, frame, skeleton, loaded, current.Document) is not { Count: > 0 } targets ||
            FramesAt(loaded, current.Document, layer, frame) is not { } frames)
        {
            EndGizmo(true);
            gizmoHeld = null;
            return;
        }
        var turned = targets.Count > 1 ? PoseEdits.Turned(targets, bone, skeleton) : targets;
        var character = characterAxes || turned.Count > 1;
        // A fade or an amount makes the bone move less than its keys: a drag changes them by more, so the
        // bone follows the gizmo. Where the layer barely applies, nothing changes and the gizmo stays on the bone.
        var weight = MotionEvaluator.Weight(layer, frame, MotionEnd(loaded, current.Document));

        // After a drag the gizmo stays where it was left until Live shows the change, rather than jump
        // back to the bone as the game still shows it.
        if (gizmoEditor == null && gizmoHeld is { } held)
        {
            var shown = motionDisplay?.Shows(current.Document.Motion) == true ? held.Shown + 1 : 0;
            gizmoHeld = held.Layer != layer.Key || held.Bone != bone || held.Frame != frame || shown > GizmoShownFrames ||
                        held.Since >= GizmoHoldFrames || !LiveShowing
                ? null
                : held with { Since = held.Since + 1, Shown = shown };
        }
        var matrix = gizmoHeld is { } kept && kept.Layer == layer.Key && kept.Bone == bone && kept.Frame == frame
            ? kept.Matrix
            : PoseGizmo.Bone(actorPose.Positions[at], actorPose.Rotations[at]);
        var before = matrix;
        var view = modelView;

        ImGuizmo.SetID(GizmoId);
        ImGuizmo.BeginFrame();
        ImGuizmo.SetDrawlist();
        ImGuizmo.SetRect(viewport.Pos.X, viewport.Pos.Y, viewport.Size.X, viewport.Size.Y);
        ImGuizmo.SetOrthographic(false);
        ImGuizmo.AllowAxisFlip(false);
        var moved = ImGuizmo.Manipulate(ref view, ref projection, operation, character ? ImGuizmoMode.World : ImGuizmoMode.Local, ref matrix);
        var dragging = ImGuizmo.IsUsing();
        var over = ImGuizmo.IsOver();
        gizmoOver = over;
        if (over && weight < PoseEdits.MinFollowWeight)
            ImGui.SetTooltip("The layer barely applies here.");
        ImGuizmo.Enable(true);
        ImGuizmo.AllowAxisFlip(true);
        ImGuizmo.SetID(NoGizmoId);

        // The game must not turn its camera with the drag.
        if (dragging || over)
            ImGui.SetNextFrameWantCaptureMouse(true);
        if (dragging && gizmoEditor == null)
        {
            current.BeginFieldEdit();
            gizmoEditor = current;
        }
        if (gizmoEditor == null)
            return;
        var (turn, move) = PoseGizmo.Delta(before, matrix);
        if (moved && ReferenceEquals(gizmoEditor, current) && PoseEdits.Follow(turn, move, weight) is { } follow)
        {
            if (operation == ImGuizmoOperation.Translate)
            {
                var offset = layer.Kind == MotionLayerKind.Offset;
                var motion = offset ? null : MotionPose(loaded, frame);
                var (index, _) = BoneTree(skeleton);
                current.Change(PoseEdits.Move(current.Document, layer.Key, frame, turned, follow.Move, frames.Space, offset,
                    name => motion is { } own && index.TryGetValue(name, out var b) ? own.Local[b].Position : null));
            }
            else
                current.Change(PoseEdits.Turn(current.Document, layer.Key, frame, turned, follow.Turn, frames.Space));
        }
        gizmoHeld = weight >= PoseEdits.MinFollowWeight ? (layer.Key, bone, frame, matrix, 0, 0) : null;
        if (!dragging)
            EndGizmo(false);
    }

    /// <summary>
    /// What the gizmo does on the character now, or null when it can't show: it needs the selected offset
    /// or pose layer on with the playhead inside it, and Live showing the layers on the paused clip.
    /// <paramref name="reason"/> says what to do when that is up to the user.
    /// </summary>
    private ImGuizmoOperation? GizmoOperation(MotionLayer layer, int frame, out string? reason)
    {
        reason = null;
        if (layer.Kind == MotionLayerKind.Amplitude || frame < layer.Start || frame > layer.End || ReadOnly)
            return null;
        if (!layer.Enabled)
        {
            reason = "The layer is off. Tick it to pose with the gizmo.";
            return null;
        }
        if (!LiveShowing)
        {
            reason = "Turn on Live to pose the character with the gizmo.";
            return null;
        }
        if (!playback.Paused)
        {
            reason = "Pause to pose the character with the gizmo.";
            return null;
        }
        var turn = layer.Components.HasFlag(PoseComponents.Rotation);
        var move = layer.Components.HasFlag(PoseComponents.Position);
        return move && (gizmoMove || !turn) ? ImGuizmoOperation.Translate : turn ? ImGuizmoOperation.Rotate : null;
    }

    /// <summary>
    /// Ends a gizmo drag: its changes become one undo step in the editor it began in. An abandoned drag,
    /// whose gizmo went away under the mouse, also ends ImGuizmo's so it can't go on later.
    /// </summary>
    private void EndGizmo(bool abandoned)
    {
        if (gizmoEditor is not { } began)
            return;
        gizmoEditor = null;
        if (ReferenceEquals(began, editor))
            began.EndFieldEdit();
        if (!abandoned)
            return;
        gizmoHeld = null;
        ImGuizmo.SetID(GizmoId);
        ImGuizmo.Enable(false);
        ImGuizmo.Enable(true);
        ImGuizmo.SetID(NoGizmoId);
    }

    /// <summary>
    /// Ends a drag whose overlay stopped drawing: GPose ended or the Bones window closed under it. The
    /// overlay checks every frame, so this holds while the Timeline window is collapsed too.
    /// </summary>
    internal void CheckGizmo()
    {
        if (gizmoEditor != null && !OverlayWanted)
            EndGizmo(true);
    }

    private bool ReadActorPose(ushort actor)
    {
        try { return playback.ReadPose(actor, actorPose); }
        catch (InvalidOperationException) { return false; }
    }
}
