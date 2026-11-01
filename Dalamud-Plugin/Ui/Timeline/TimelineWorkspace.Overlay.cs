using System.Collections.Immutable;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Bindings.ImGuizmo;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Preview;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui.Pose;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// The bones on the posed character, drawn for <see cref="BonesOverlay"/> while the Bones window is open
/// in GPose: a dot where the game shows each of the map's bones (and the selected ones and the ones the
/// layer changes), joined to the nearest one above it and coloured like the map, and the gizmo on the
/// primary bone. A click on a dot selects like the map. With Live on and paused, a gizmo drag turns or
/// moves the bones the edit changes in the selected layer at the playhead, as one undo step. Adapted
/// from Brio's posing overlay (GPL-3.0).
/// </summary>
internal sealed partial class TimelineWorkspace
{
    // ImGuizmo's state is shared by every plugin; this id keeps our drags apart from Brio's gizmo.
    private const int GizmoId = 0x1E5E;
    // ImGuizmo's id when none is set.
    private const int NoGizmoId = -1;
    // How long the gizmo stays where a drag left it while Live applies the change: frames once Live
    // shows it, and frames at most.
    private const int GizmoShownFrames = 2, GizmoHoldFrames = 240;
    // How many frames the "barely applies" tooltip stays after ImGuizmo last had the mouse over its rings.
    private const int GizmoTipFrames = 4;

    private readonly ActorPose actorPose = new();
    private BoneOverlayMode showOnCharacter = BoneOverlayMode.All;
    private GizmoMode gizmoMode;
    // The editor a gizmo drag runs in as one undo step.
    private TimelineEditor? gizmoEditor;
    // Where a Drag aims the bone: kept across the drag so a solve that lands short doesn't move the aim.
    private Vector3? dragAim;
    // The pose a Drag began on, which each of its frames solves from.
    private MotionTransform[]? dragFrom;
    // The limb a Drag bends, found once per drag.
    private (PinChain Chain, Vector3 RestNormal)? dragLimb;
    // The side the limb bends to, carried from each frame of a Drag to the next so the knee or elbow can't swap sides in one.
    private LimbSide? dragSide;
    // How far a Drag on a hand or a foot turns the elbow or knee about the line to the aim, in degrees: the Bones window's Bend field.
    private float dragBend;
    // What a Drag on the hips or the head holds where it is: the Bones window's Hold boxes, and their anchors found once per drag.
    private readonly DragHolds dragHolds = new();
    private ImmutableArray<HipAnchor>? dragAnchors;
    // The back a Drag on the head leans, found once per drag, and the hips' move the drag's last frame keyed.
    private PinChain? dragBack;
    private Vector3 dragHipsMove;
    // Whether the mouse was over the gizmo's rings or handles last frame: then the gizmo takes the click, not a dot.
    private bool gizmoOver;
    // Frames left of the "barely applies" tooltip; it's begun on each of them.
    private int gizmoTip;
    // Where the last drag left the gizmo on a bone, layer and frame, until Live shows the change there.
    private (string Layer, string Bone, int Frame, Matrix4x4 Matrix, int Since, int Shown)? gizmoHeld;
    // Each bone's dot this frame, by the clip skeleton's bone index.
    private Vector2[] dotAt = [];
    private bool[] dotShown = [];
    // An onion skin's dots, reused for the key before the playhead and the one after.
    private Vector2[] ghostAt = [];
    private bool[] ghostShown = [];
    // A motion path's frames, points and which of them show, reused for each selected bone.
    private Vector3[]?[] pathPoses = [];
    private Vector2[] pathAt = [];
    private bool[] pathShown = [];

    /// <summary>Whether the bones show on the character now: the Bones window is open on a clip or a face, in GPose.</summary>
    internal bool OverlayWanted => showOnCharacter != BoneOverlayMode.Off && host.IsOpen && bonesWindow.IsOpen && animations != null &&
                                   source is { Document: not null } open && !open.MotionTarget.Standalone && editor != null && OverlayActor() != null;

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
        if (animations == null || source is not { Document: not null } loaded || loaded.MotionTarget.Standalone || editor is not { } current)
        {
            EndGizmo(true);
            return;
        }
        var clip = Fresh(loaded).Clip;
        // A face's bones live on the face's own part of the skeleton.
        var face = current.Document.Face != null;
        if (session.EditingSkeleton(loaded) is not { } skeleton || OverlayActor() is not { } actor || !ReadActorPose(actor, face) ||
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
        var layerBones = BonesOf(layer, skeleton);

        var count = skeleton.Bones.Length;
        if (dotAt.Length != count)
        {
            dotAt = new Vector2[count];
            dotShown = new bool[count];
        }
        // The map's bones as the map shows them: the ones the character uses or the clip moves. A face's picture shows them all.
        var used = face ? null : posing.MapBones(loaded, skeleton);
        var placed = face ? PoseMap.FacePlaced : PoseMap.Placed;
        for (var b = 0; b < count; b++)
        {
            var name = skeleton.Bones[b].Name;
            dotShown[b] = BoneOverlayModes.Dot(showOnCharacter, selection.Contains(name), placed.Contains(name) && PoseMap.Shows(name, used, false),
                              showOnCharacter == BoneOverlayMode.All && layer != null &&
                              (layerBones?.Pinned.Contains(name) == true || PoseEdits.Changes(layer, name, layerBones?.Changed))) &&
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
        if (posing.Ghosts && layer != null && posing.GhostPose(loaded, current.Document, frame) is { } playhead)
        {
            var (before, after) = BoneOverlayModes.GhostFrames(layer.Keys, frame);
            DrawGhost(skeleton, loaded, current.Document, before, playhead, Theme.Info, toClip, viewport);
            DrawGhost(skeleton, loaded, current.Document, after, playhead, Theme.Important, toClip, viewport);
        }
        if (posing.PathFrames(loaded, current.Document, frame) is { } path)
            DrawPaths(skeleton, index, layer, path.Frames, path.At, path.Cache, BonePosing.LoopFrames(loaded, current.Document), toClip, viewport);
        DrawOverlayBones(skeleton, layer, frame, animated, primary, hover);
        if (layer is { Kind: MotionLayerKind.LookAt })
        {
            DrawLookTarget(loaded, current.Document, skeleton, layer, frame, toClip, viewport);
            DrawLookGizmo(current, loaded, skeleton, layer, frame, modelView, camera.Projection, viewport);
        }
        else
            DrawGizmo(current, loaded, skeleton, layer, frame, modelView, camera.Projection, viewport);

        // The mouse reached the gizmo this frame: it takes the click. A dot's tooltip would hide the gizmo's.
        if (hover < 0 || gizmoOver || gizmoTip > 0)
            return;
        // The game must not take the click as well.
        ImGui.SetNextFrameWantCaptureMouse(true);
        var bone = skeleton.Bones[hover].Name;
        ImGui.SetTooltip($"{AnimationPresentation.BoneName(bone)}\n{StateText(StateOf(bone, layer, frame, animated, layerBones), layer)}");
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            ClickBone(bone, skeleton);
    }

    /// <summary>
    /// An onion skin: faint dots and lines in <paramref name="tint"/> for the pose at a key of the selected layer,
    /// on the bones that have a dot, each joined to the nearest one above it. Drawn before the dots, so under them.
    /// Each sits off its dot by how far the bone is from where it is at the playhead (<paramref name="playhead"/>), so
    /// the character's own scaling and physics carry the ghost too.
    /// </summary>
    private void DrawGhost(SkeletonDescription skeleton, AnimationTimelineSource loaded, TimelineDocument doc, int? frame, MotionTransform[] playhead,
        Vector4 tint, Matrix4x4 toClip, ImGuiViewportPtr viewport)
    {
        if (frame is not { } key || posing.GhostPose(loaded, doc, key) is not { } model || model.Length != dotShown.Length || playhead.Length != model.Length)
            return;
        if (ghostAt.Length != dotShown.Length)
        {
            ghostAt = new Vector2[dotShown.Length];
            ghostShown = new bool[dotShown.Length];
        }
        for (var b = 0; b < dotShown.Length; b++)
            ghostShown[b] = dotShown[b] && actorPose.Index.TryGetValue(skeleton.Bones[b].Name, out var at) &&
                            PoseGizmo.Project(actorPose.Positions[at] + model[b].Position - playhead[b].Position, toClip, viewport.Pos, viewport.Size, out ghostAt[b]);
        var drawList = ImGui.GetWindowDrawList();
        var radius = Theme.Scaled(3.5f);
        var lineColour = ImGui.GetColorU32(Theme.WithAlpha(tint, .3f));
        var dotColour = ImGui.GetColorU32(Theme.WithAlpha(tint, .45f));
        for (var b = 0; b < ghostShown.Length; b++)
        {
            if (!ghostShown[b])
                continue;
            var above = skeleton.Bones[b].Parent;
            while (above >= 0 && !ghostShown[above])
                above = skeleton.Bones[above].Parent;
            if (above >= 0)
                drawList.AddLine(ghostAt[b], ghostAt[above], lineColour, Theme.Scaled(1.5f));
        }
        for (var b = 0; b < ghostShown.Length; b++)
            if (ghostShown[b])
                drawList.AddCircleFilled(ghostAt[b], radius, dotColour);
    }

    /// <summary>
    /// The motion paths: for each selected bone (the first <see cref="MotionPaths.MaxBones"/>, primary first) a line
    /// through where it is at each of <paramref name="frames"/>, blue before the playhead and orange after, with a dot at
    /// each frame, a bigger one where the selected layer keys the bone, and a white one at the playhead. Each point sits
    /// off the bone's dot by how far the bone is from where it is at the playhead, as an onion skin's does. Only frames
    /// already in <paramref name="cache"/> draw; drawn before the dots, so under them.
    /// </summary>
    private void DrawPaths(SkeletonDescription skeleton, Dictionary<string, int> index, MotionLayer? layer, int[] frames, int at, MotionPathCache cache,
        float loop, Matrix4x4 toClip, ImGuiViewportPtr viewport)
    {
        if (pathPoses.Length < frames.Length)
        {
            pathPoses = new Vector3[]?[frames.Length];
            pathAt = new Vector2[frames.Length];
            pathShown = new bool[frames.Length];
        }
        cache.Read(frames, pathPoses);
        if (pathPoses[at] is not { } playhead || playhead.Length != skeleton.Bones.Length)
            return;
        var drawList = ImGui.GetWindowDrawList();
        var small = Theme.Scaled(2);
        var big = Theme.Scaled(4.5f);
        var width = Theme.Scaled(1.5f);
        var shadow = ImGui.GetColorU32(new Vector4(0, 0, 0, .45f));
        var before = ImGui.GetColorU32(Theme.WithAlpha(Theme.Info, .85f));
        var after = ImGui.GetColorU32(Theme.WithAlpha(Theme.Important, .85f));
        var now = ImGui.GetColorU32(new Vector4(1, 1, 1, 1));
        var keyLoop = (int)MathF.Round(loop);
        var drawn = 0;
        foreach (var bone in selection.Primary is { } primary ? selection.Bones.Where(name => name != primary).Prepend(primary) : selection.Bones)
        {
            if (drawn >= MotionPaths.MaxBones)
                break;
            if (!index.TryGetValue(bone, out var b) || !actorPose.Index.TryGetValue(bone, out var on))
                continue;
            drawn++;
            var origin = actorPose.Positions[on] - playhead[b];
            for (var i = 0; i < frames.Length; i++)
                pathShown[i] = pathPoses[i] is { } pose && pose.Length == playhead.Length &&
                               PoseGizmo.Project(origin + pose[b], toClip, viewport.Pos, viewport.Size, out pathAt[i]);
            for (var i = 1; i < frames.Length; i++)
                if (pathShown[i - 1] && pathShown[i])
                    drawList.AddLine(pathAt[i - 1], pathAt[i], i <= at ? before : after, width);
            var keys = MotionPaths.KeyFrames(layer, bone, keyLoop);
            for (var i = 0; i < frames.Length; i++)
            {
                if (!pathShown[i])
                    continue;
                var radius = keys.Contains(frames[i]) ? big : small;
                drawList.AddCircleFilled(pathAt[i], radius + Theme.Scaled(1), shadow);
                drawList.AddCircleFilled(pathAt[i], radius, i == at ? now : i < at ? before : after);
            }
        }
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
        var layerBones = BonesOf(layer, skeleton);
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
            var state = StateOf(name, layer, frame, animated, layerBones);
            var colour = ImGui.GetColorU32(StateColour(state, layer));
            var fill = ImGui.GetColorU32(selection.Contains(name) ? Theme.WithAlpha(Theme.Accent, .9f) : Theme.WithAlpha(Theme.PanelBg, .75f));
            if (state == BoneState.Pinned)
            {
                // A pinned bone is a square.
                var corner = new Vector2(radius);
                drawList.AddRectFilled(at - corner - new Vector2(Theme.Scaled(1.5f)), at + corner + new Vector2(Theme.Scaled(1.5f)), shadow);
                drawList.AddRectFilled(at - corner, at + corner, fill);
                drawList.AddRect(at - corner, at + corner, colour, 0, ImDrawFlags.None, Theme.Scaled(2.5f));
            }
            else
            {
                drawList.AddCircleFilled(at, radius + Theme.Scaled(1.5f), shadow);
                drawList.AddCircleFilled(at, radius, fill);
                drawList.AddCircle(at, radius, colour, 0, Theme.Scaled(state is BoneState.Changed or BoneState.Keyed ? 2.5f : 1.5f));
            }
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
    /// the frame before, in character space; a Drag instead bends the limb above the primary bone to follow
    /// (<see cref="PoseEdits.Reach"/>), or on the hips moves them and bends the body to hold the anchors
    /// (<see cref="PoseEdits.DragHips"/>), and on the head leans the back to it and moves the hips by the rest
    /// (<see cref="PoseEdits.DragHead"/>). While the mouse is over its rings or handles it takes the
    /// click, and the dots under it don't (<see cref="gizmoOver"/>); a click elsewhere goes to a dot.
    /// </summary>
    private void DrawGizmo(TimelineEditor current, AnimationTimelineSource loaded, SkeletonDescription skeleton, MotionLayer? layer, int frame,
        Matrix4x4 modelView, Matrix4x4 projection, ImGuiViewportPtr viewport)
    {
        if (layer == null || GizmoOperation(layer, frame, skeleton, out _) is not { } operation || selection.Primary is not { } bone ||
            !actorPose.Index.TryGetValue(bone, out var at) || Targets(layer, frame, skeleton, loaded, current.Document) is not { Count: > 0 } targets ||
            FramesAt(loaded, current.Document, layer, frame) is not { } frames)
        {
            EndGizmo(true);
            gizmoHeld = null;
            gizmoTip = 0;
            return;
        }
        var turned = targets.Count > 1 ? PoseEdits.Turned(targets, bone, skeleton) : targets;
        var drag = Drags(layer, skeleton);
        var character = drag || characterAxes || turned.Count > 1;
        // A fade or an amount makes the bone move less than its keys: a drag changes them by more, so the
        // bone follows the gizmo. Where the layer barely applies, nothing changes and the gizmo stays on the bone.
        var weight = MotionEvaluator.Weight(layer, frame, MotionEnd(loaded, current.Document));

        // After a drag the gizmo stays where it was left until Live shows the change, rather than jump
        // back to the bone as the game still shows it.
        if (gizmoEditor == null && gizmoHeld is { } held)
        {
            var shown = motionDisplay?.Shows(current.Document.Motion, TimelinePreview.LiveLinks(current.Document, session.Links)) == true ? held.Shown + 1 : 0;
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
        // ImGuizmo can report the mouse over its rings on some frames only, and ImGui hides a tooltip on a frame
        // after one without it while it sizes itself: begun only on those frames it never shows. Held a few
        // frames, it's begun on every frame while the mouse is on the rings.
        gizmoTip = weight >= PoseEdits.MinFollowWeight ? 0 : over ? GizmoTipFrames : Math.Max(0, gizmoTip - 1);
        if (gizmoTip > 0)
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
            dragAim = null;
            dragFrom = null;
            dragLimb = null;
            dragSide = null;
            dragAnchors = null;
            dragBack = null;
            dragHipsMove = Vector3.Zero;
        }
        if (gizmoEditor == null)
            return;
        var (turn, move) = PoseGizmo.Delta(before, matrix);
        if (moved && ReferenceEquals(gizmoEditor, current) && PoseEdits.Follow(turn, move, weight) is { } follow)
        {
            // A drag aims the bone from where the layers through this one have it; Reach scales by the weight itself.
            if (drag)
            {
                if ((dragAim ?? frames.ThroughPosition(bone)) is { } from)
                {
                    dragAim = from + move;
                    dragFrom ??= frames.Through;
                    if (bone == PinChains.Hips)
                    {
                        // The hips: the body bends around them to hold the anchors, then the hips' key moves them as Move does.
                        dragAnchors ??= dragHolds.Anchors(skeleton, false, selection.Bones);
                        var changed = PoseEdits.DragHips(current.Document, layer, frame, skeleton, frames.Before, frames.Through, dragFrom,
                            from + move, dragAnchors.Value.AsSpan(), weight, BonePosing.LoopFrames(loaded, current.Document));
                        var offset = layer.Kind == MotionLayerKind.Offset;
                        var under = offset ? null : frames.Before;
                        var (index, _) = BoneTree(skeleton);
                        var moving = new List<PoseTarget> { targets.First(t => t.Bone == bone) };
                        // The back's base goes with the hips, from its key as the drag has just left it; on a clip whose hips' move goes
                        // onto n_hara, n_hara carries it already.
                        if (!frames.HipsOnHara && HipSolver.Carried(skeleton) is >= 0 and var carried && MotionEdits.Find(changed, layer.Key) is { } edited)
                        {
                            var name = skeleton.Bones[carried].Name;
                            if (PoseEdits.Value(edited, name, frame, offset ? null : frames.Before[carried]) is { } value)
                                moving.Add(new PoseTarget(name, value, PoseEdits.Moved(edited, name, frame)));
                        }
                        current.Change(current.WithReach(PoseEdits.Move(changed, layer.Key, frame, moving, follow.Move, frames.Space, offset,
                            name => under != null && index.TryGetValue(name, out var b) ? under[b].Position : null)));
                    }
                    else if (TimelineWindowViews.HeadDrag(layer, bone, skeleton) && (dragBack ??= HeadSolver.Back(skeleton)) is { } back)
                    {
                        // The head: the back leans to it and the hips move by what the lean can't cover; their keys and the back base's then move as Move does.
                        dragAnchors ??= dragHolds.Anchors(skeleton, true, selection.Bones);
                        var loop = BonePosing.LoopFrames(loaded, current.Document);
                        var (changed, hipsMove) = PoseEdits.DragHead(current.Document, layer, frame, skeleton, frames.Before, frames.Through, dragFrom,
                            from + move, back, dragAnchors.Value.AsSpan(), weight, loop);
                        var offset = layer.Kind == MotionLayerKind.Offset;
                        var under = offset ? null : frames.Before;
                        var (index, _) = BoneTree(skeleton);
                        var moving = new List<PoseTarget>();
                        string[] carriers = !frames.HipsOnHara && HipSolver.Carried(skeleton) is >= 0 and var carried
                            ? [PinChains.Hips, skeleton.Bones[carried].Name]
                            : [PinChains.Hips];
                        if (MotionEdits.Find(changed, layer.Key) is { } edited)
                            foreach (var carrier in carriers)
                                if (PoseEdits.Value(edited, carrier, frame, offset ? null : frames.Before[index[carrier]], loop) is { } value)
                                    moving.Add(new PoseTarget(carrier, value, PoseEdits.Moved(edited, carrier, frame)));
                        // The hips' move since the frame before, followed at the layer's weight as the gizmo's own move is.
                        var step = (hipsMove - dragHipsMove) / weight;
                        dragHipsMove = hipsMove;
                        current.Change(current.WithReach(PoseEdits.Move(changed, layer.Key, frame, moving, step, frames.Space, offset,
                            name => under != null && index.TryGetValue(name, out var b) ? under[b].Position : null)));
                    }
                    else
                    {
                        dragLimb ??= PoseEdits.Limb(skeleton, bone);
                        dragSide ??= new LimbSide();
                        current.Change(current.WithReach(PoseEdits.Reach(current.Document, layer, frame, skeleton, frames.Before, frames.Through, bone,
                            from + move, weight, BonePosing.LoopFrames(loaded, current.Document), dragFrom, dragLimb, dragBend, dragSide)));
                    }
                }
            }
            else if (operation == ImGuizmoOperation.Translate)
            {
                var offset = layer.Kind == MotionLayerKind.Offset;
                // A pin key is a spot in the pin's space: it moves from where it is, never from where the layers before leave the bone.
                var under = offset || layer.Kind == MotionLayerKind.Pin ? null : frames.Before;
                var (index, _) = BoneTree(skeleton);
                current.Change(current.WithReach(PoseEdits.Move(current.Document, layer.Key, frame, turned, follow.Move, frames.Space, offset,
                    name => under != null && index.TryGetValue(name, out var b) ? under[b].Position : null)));
            }
            else
                current.Change(current.WithReach(PoseEdits.Turn(current.Document, layer.Key, frame, turned, follow.Turn, frames.Space)));
        }
        gizmoHeld = weight >= PoseEdits.MinFollowWeight ? (layer.Key, bone, frame, matrix, 0, 0) : null;
        if (!dragging)
            EndGizmo(false);
    }

    /// <summary>
    /// What the gizmo does on the character now, or null when it can't show: it needs the selected offset
    /// or pose layer, or a keyed pin of a pin layer, on with the playhead inside it, and Live showing the
    /// layers on the paused clip.
    /// <paramref name="reason"/> says what to do when that is up to the user.
    /// </summary>
    private ImGuizmoOperation? GizmoOperation(MotionLayer layer, int frame, SkeletonDescription skeleton, out string? reason)
    {
        reason = null;
        // Past this clip's end in the play group it stands on its last frame, which a drag can't key.
        if (editor?.PastEnd == true)
        {
            reason = TimelinePreview.PastEnd;
            return null;
        }
        if (layer.Kind is MotionLayerKind.Amplitude or MotionLayerKind.Symmetry or MotionLayerKind.Fix or MotionLayerKind.Idle or MotionLayerKind.Swing or
                MotionLayerKind.Smooth or MotionLayerKind.Noise || ReadOnly)
            return null;
        if (frame < layer.Start || frame > layer.End)
        {
            reason = "Move the playhead into the layer to pose it with the gizmo.";
            return null;
        }
        if (!layer.Enabled)
        {
            reason = "The layer is off. Tick it to pose with the gizmo.";
            return null;
        }
        // A pin layer's gizmo changes keyed pins' keys only; a look's moves its target, whichever bone is selected.
        bool? pinMoves = null;
        if (layer.Kind == MotionLayerKind.Pin && (pinMoves = TimelineWindowViews.PinGizmo(layer, selection.Primary, gizmoMode, out reason)) == null)
            return null;
        if (layer.Kind == MotionLayerKind.LookAt && (reason = TimelineWindowViews.LookGizmo(layer)) != null)
            return null;
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
        if (pinMoves is { } moves)
            return moves ? ImGuizmoOperation.Translate : ImGuizmoOperation.Rotate;
        if (layer.Kind == MotionLayerKind.LookAt)
            return ImGuizmoOperation.Translate;
        // A drag moves the bone; its limb's turns are what it keys.
        if (Drags(layer, skeleton))
            return ImGuizmoOperation.Translate;
        var turn = layer.Components.HasFlag(PoseComponents.Rotation);
        var move = layer.Components.HasFlag(PoseComponents.Position);
        return move && (gizmoMode == GizmoMode.Move || !turn) ? ImGuizmoOperation.Translate : turn ? ImGuizmoOperation.Rotate : null;
    }

    /// <summary>
    /// Where a look-at layer looks at <paramref name="frame"/>, in character space: its keys' spot there, else a spot ahead of
    /// the head as the layers before leave it; null while that is unknown, and for a look that follows a bone.
    /// </summary>
    private Vector3? LookSpot(AnimationTimelineSource loaded, TimelineDocument doc, SkeletonDescription skeleton, MotionLayer layer, int frame)
    {
        if (layer.Look is not { Follow: null })
            return null;
        if (MotionLooks.KeyedTarget(layer, frame, BonePosing.LoopFrames(loaded, doc)) is { } keyed)
            return keyed;
        return BoneTree(skeleton).Index.TryGetValue(MotionLooks.KeyBone, out var head) && FramesAt(loaded, doc, layer, frame) is { } frames
            ? MotionLooks.DefaultTarget(skeleton, head, frames.BeforeModel)
            : null;
    }

    /// <summary>
    /// A look-at layer's target on the character: a dot at its spot, or the followed bone's dot, with a faint line from the head.
    /// The spot is drawn where it is in character space; the followed bone where the game shows it.
    /// </summary>
    private void DrawLookTarget(AnimationTimelineSource loaded, TimelineDocument doc, SkeletonDescription skeleton, MotionLayer layer, int frame,
        Matrix4x4 toClip, ImGuiViewportPtr viewport)
    {
        if (!actorPose.Index.TryGetValue(MotionLooks.KeyBone, out var head) ||
            !PoseGizmo.Project(actorPose.Positions[head], toClip, viewport.Pos, viewport.Size, out var from))
            return;
        var spot = layer.Look?.Follow is { } follow
            ? actorPose.Index.TryGetValue(follow, out var followed) ? actorPose.Positions[followed] : (Vector3?)null
            : LookSpot(loaded, doc, skeleton, layer, frame);
        if (spot is not { } target || !PoseGizmo.Project(target, toClip, viewport.Pos, viewport.Size, out var to))
            return;
        var drawList = ImGui.GetWindowDrawList();
        var colour = TimelineWindowViews.LayerColour(MotionLayerKind.LookAt);
        drawList.AddLine(from, to, ImGui.GetColorU32(Theme.WithAlpha(colour, .35f)), Theme.Scaled(1.5f));
        if (layer.Look?.Follow != null)
            return;
        var radius = Theme.Scaled(6);
        drawList.AddCircleFilled(to, radius + Theme.Scaled(1.5f), ImGui.GetColorU32(new Vector4(0, 0, 0, .5f)));
        drawList.AddCircleFilled(to, radius, ImGui.GetColorU32(Theme.WithAlpha(colour, .9f)));
    }

    /// <summary>
    /// The gizmo on a look-at layer's spot, Move only, in the character's axes: each frame of a drag keys the spot moved by
    /// what the gizmo did since the frame before at the playhead, as one undo step, as a pin key's spot moves.
    /// </summary>
    private void DrawLookGizmo(TimelineEditor current, AnimationTimelineSource loaded, SkeletonDescription skeleton, MotionLayer layer, int frame,
        Matrix4x4 modelView, Matrix4x4 projection, ImGuiViewportPtr viewport)
    {
        gizmoHeld = null;
        gizmoTip = 0;
        if (GizmoOperation(layer, frame, skeleton, out _) == null || LookSpot(loaded, current.Document, skeleton, layer, frame) is not { } spot)
        {
            EndGizmo(true);
            return;
        }
        var matrix = PoseGizmo.Bone(spot, Quaternion.Identity);
        var before = matrix;
        var view = modelView;
        ImGuizmo.SetID(GizmoId);
        ImGuizmo.BeginFrame();
        ImGuizmo.SetDrawlist();
        ImGuizmo.SetRect(viewport.Pos.X, viewport.Pos.Y, viewport.Size.X, viewport.Size.Y);
        ImGuizmo.SetOrthographic(false);
        ImGuizmo.AllowAxisFlip(false);
        var moved = ImGuizmo.Manipulate(ref view, ref projection, ImGuizmoOperation.Translate, ImGuizmoMode.World, ref matrix);
        var dragging = ImGuizmo.IsUsing();
        var over = ImGuizmo.IsOver();
        gizmoOver = over;
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
        // The spot is where the target is, whatever the layer's weight: it moves with the gizmo as it is.
        var (_, move) = PoseGizmo.Delta(before, matrix);
        if (moved && ReferenceEquals(gizmoEditor, current) && move.LengthSquared() > 0)
            current.Change(MotionEdits.SetLookTarget(current.Document, layer.Key, frame, spot + move));
        if (!dragging)
            EndGizmo(false);
    }

    /// <summary>Whether the gizmo drags the primary bone in <paramref name="layer"/>: Drag chosen and able to; else a kept Drag acts as Turn.</summary>
    private bool Drags(MotionLayer layer, SkeletonDescription skeleton) =>
        gizmoMode == GizmoMode.Drag && TimelineWindowViews.DragGizmo(layer, selection.Primary, skeleton, BoneTree(skeleton).Index) == null;

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
        // A limb drag's Bend shaped the pose it keyed; kept, the next drag would turn that pose by as much again.
        if (dragLimb != null)
            dragBend = 0;
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

    /// <summary>Reads the character's pose for the overlay; with <paramref name="allPartials"/> (a face) its face's bones too.</summary>
    private bool ReadActorPose(ushort actor, bool allPartials = false)
    {
        try { return playback.ReadPose(actor, actorPose, allPartials); }
        catch (InvalidOperationException) { return false; }
    }
}
