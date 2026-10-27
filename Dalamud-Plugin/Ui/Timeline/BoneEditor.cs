using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui.Pose;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// The bones and the gizmo on the GPose character, as the bone editor and the Bones window use them.
/// The Timeline window draws them (TimelineWindow.Overlay.cs) and implements this.
/// </summary>
internal interface IBonesOverlay
{
    /// <summary>Whether the bones show on the character now.</summary>
    bool Shown { get; }

    /// <summary>The Bones window's switch: which bones show on the character in GPose, if any.</summary>
    BoneOverlayMode OnCharacter { get; set; }

    /// <summary>Whether the gizmo turns, moves or drags the bones, for a layer that can.</summary>
    GizmoMode Gizmo { get; set; }

    /// <summary>What to do so the gizmo can pose the character with <paramref name="layer"/> at <paramref name="frame"/>; null when nothing the user does helps.</summary>
    string? GizmoWaits(MotionLayer layer, int frame, SkeletonDescription skeleton);
}

/// <summary>
/// The selected bones' values in a bone layer at the playhead, shared by the properties panel and the
/// Bones window: the turn, move and scale fields, Key, Delete key, Reset, Mirror, pose copy and paste and Clear, the rotation
/// ball and the gizmo's choice, the bone picker and an amplitude's bones. A field's changes until it is
/// left are one undo step.
/// </summary>
internal sealed class BoneEditor(TimelineSession session, MotionDecode decode, BonePosing posing, IBonesOverlay overlay)
{
    private string boneFilter = string.Empty;
    // The angles a turn field shows while it is being dragged: read back from the rotation they make,
    // they could flip to an equivalent set mid-drag.
    private (string Id, Vector3 Degrees)? turnEdit;
    private bool characterAxes;
    // A drag on the rotation ball is running as one undo step.
    private bool ballEditing;
    // Static so a copied pose carries to another clip and between the panel and the Bones window.
    private static PoseCopy? poseClipboard;

    /// <summary>Puts <paramref name="copy"/> on the clipboard Paste pose reads.</summary>
    public static void CopyPose(PoseCopy copy) => poseClipboard = copy;

    /// <summary>Whether the ball and the gizmo use the character's axes rather than the primary bone's own.</summary>
    public bool CharacterAxes => characterAxes;

    /// <summary>
    /// The selected bones' values at the playhead, for an offset or a pose layer: the primary bone's turn,
    /// move and scale, each change keyed there and made to every other selected bone too; then Key,
    /// Delete key, Reset, Mirror and Clear for all of them. With <paramref name="ball"/> (the Bones
    /// window) the rotation ball comes first.
    /// </summary>
    public void Draw(TimelineEditor current, MotionLayer layer, SkeletonDescription skeleton, AnimationTimelineSource loaded,
        bool readOnly, bool ball)
    {
        var (index, _) = posing.BoneTree(skeleton);
        if (posing.Selection.Primary is not { } bone || !index.ContainsKey(bone))
        {
            Widgets.HintWrapped(ball ? "Click a bone on the map to set its value at the playhead." : "Choose a bone to set its value at the playhead.");
            return;
        }
        var frame = current.Playhead;
        var inside = frame >= layer.Start && frame <= layer.End;
        var pose = layer.Kind == MotionLayerKind.Pose;
        if (posing.Targets(layer, frame, skeleton, loaded, current.Document) is not { Count: > 0 } targets)
        {
            decode.DrawWait();
            return;
        }
        var primary = targets[0];
        // Several bones change the way the primary one does, as the character sees it, which needs
        // their frames from the motion; so does the ball. One bone alone changes in its own space.
        var turned = targets.Count > 1 ? PoseEdits.Turned(targets, bone, skeleton) : targets;
        var frames = ball || turned.Count > 1 ? posing.FramesAt(loaded, current.Document, layer, frame) : null;
        if (turned.Count > 1 && frames == null)
        {
            decode.DrawWait();
            return;
        }
        Func<string, Quaternion> space = frames != null ? frames.Space : _ => Quaternion.Identity;
        List<PoseTarget> others = [.. turned.Where(target => target.Bone != bone)];
        // A pose bone whose key only turned it starts a move from where the layers before this one leave it.
        var before = pose ? (frames ?? posing.FramesAt(loaded, current.Document, layer, frame))?.Before : null;
        Func<string, Vector3?> own = name => before != null && index.TryGetValue(name, out var at) ? before[at].Position : null;

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, $"At {TimelineWindowViews.FrameText(frame)}");
        if (PoseEdits.Keyed(layer, bone, frame))
        {
            ImGui.SameLine(0, Theme.Gap);
            Widgets.Badge("key", Theme.Accent);
        }
        if (targets.Count > 1)
        {
            ImGui.SameLine(0, Theme.Gap);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Muted, $"· {targets.Count} bones");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"The values are {AnimationPresentation.BoneName(bone)}'s. The other selected bones turn and move the same way, " +
                                 "as the character sees it; a bone below another selected one follows it.");
        }
        if (!inside)
            Widgets.HintWrapped("The playhead is outside this layer. Move it into the layer to set values.");

        using (ImRaii.Disabled(readOnly || !inside))
        {
            if (ball)
                DrawBall(current, layer, skeleton, frames, turned, primary, frame);
            using var table = ImRaii.Table("##bone-value", 2, ImGuiTableFlags.SizingFixedFit);
            if (table.Success)
            {
                ImGui.TableSetupColumn("##fact", ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableSetupColumn("##value", ImGuiTableColumnFlags.WidthStretch);
                if (layer.Components.HasFlag(PoseComponents.Rotation))
                    DrawTurn(current, layer, primary, others, space, frame, pose);
                if (layer.Components.HasFlag(PoseComponents.Position))
                    DrawMove(current, layer, primary, targets, others, space, own, frame, pose);
                if (layer.Components.HasFlag(PoseComponents.Scale))
                    DrawScale(current, layer, primary, others, space, frame, pose);
            }
        }
        DrawBoneButtons(current, layer, skeleton, loaded, targets, frame, readOnly, inside);
    }

    /// <summary> Keys a copied pose at the playhead as one undo step, saying how many bones the skeleton lacks. </summary>
    private static void Paste(TimelineEditor current, string layer, SkeletonDescription skeleton, PoseCopy copy, int frame, bool mirrored,
        MotionTransform[]? local)
    {
        if (PoseEdits.Paste(current.Document, layer, frame, copy, mirrored, skeleton, local, out var skipped) is not { } pasted)
            return;
        current.Commit(pasted);
        if (skipped > 0)
            current.Note(skipped == 1 ? "Skipped 1 bone this skeleton lacks." : $"Skipped {skipped} bones this skeleton lacks.");
    }

    /// <summary> Key, Delete key, Reset, Mirror, Copy pose, Paste pose, Paste mirrored and Clear, each for every selected bone at once. </summary>
    private void DrawBoneButtons(TimelineEditor current, MotionLayer layer, SkeletonDescription skeleton, AnimationTimelineSource loaded,
        List<PoseTarget> targets, int frame, bool readOnly, bool inside)
    {
        var pose = layer.Kind == MotionLayerKind.Pose;
        var (index, _) = posing.BoneTree(skeleton);
        string[] bones = [.. targets.Select(target => target.Bone)];
        var several = bones.Length > 1;
        using (ImRaii.Disabled(readOnly || !inside))
        {
            if (ImGui.Button("Key"))
                current.Commit(PoseEdits.Key(current.Document, layer.Key, frame, targets, pose));
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(several ? "Key the selected bones as they are now, at the playhead." : "Key the bone as it is now, at the playhead.");

        FlowLine("Delete key");
        using (ImRaii.Disabled(readOnly || !bones.Any(bone => PoseEdits.Keyed(layer, bone, frame))))
        {
            if (ImGui.Button("Delete key"))
                current.Commit(PoseEdits.Unkey(current.Document, layer.Key, frame, bones));
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(several ? "Take the selected bones out of the key at the playhead." : "Take the bone out of the key at the playhead.");

        FlowLine("Reset");
        using (ImRaii.Disabled(readOnly || !inside))
        {
            if (ImGui.Button("Reset"))
            {
                var before = pose ? posing.FramesAt(loaded, current.Document, layer, frame)?.Before : null;
                current.Commit(PoseEdits.Reset(current.Document, layer.Key, frame, bones, pose,
                    bone => before != null && index.TryGetValue(bone, out var at) ? before[at] : null));
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(pose ? "Key the pose the layers above leave, at the playhead." : "Key no change at the playhead, so the layers above show there.");

        var pairs = PoseEdits.MirrorPairs(posing.Selection.Bones, posing.Selection.Primary, index.ContainsKey)
            .Where(pair => layer.Keys.Any(key => key.Bones.ContainsKey(pair.From))).ToList();
        var mirrorReady = pairs.Count == 0 || decode.DecodedPose(loaded) != null;
        FlowLine("Mirror");
        using (ImRaii.Disabled(readOnly || pairs.Count == 0 || !mirrorReady))
        {
            // The motion as the layers before this one leave it, as Paste mirrored reads it.
            if (ImGui.Button("Mirror") && PoseEdits.Mirror(current.Document, layer.Key, pairs, skeleton,
                    at => posing.FramesAt(loaded, current.Document, layer, at)?.Before) is { } mirrored)
                current.Commit(mirrored);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(pairs.Count == 0 ? "Select a bone with keys in this layer and a twin on the other side."
                : !mirrorReady ? decode.Status
                : "Give the other side the same edits, mirrored. Replaces what the other side has in this layer.");

        FlowLine("Copy pose");
        if (ImGui.Button("Copy pose"))
        {
            poseClipboard = new PoseCopy(layer.Kind, [.. targets]);
            current.Note(targets.Count == 1 ? "Copied 1 bone." : $"Copied {targets.Count} bones.");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(several ? "Copy the selected bones' values at the playhead, to paste in any clip." : "Copy the bone's value at the playhead, to paste in any clip.");

        var copied = poseClipboard is { } copy && copy.Kind == layer.Kind ? copy : null;
        var noPaste = poseClipboard == null ? "Copy a pose first."
            : copied == null ? $"The copy is from {(poseClipboard.Kind == MotionLayerKind.Pose ? "a pose" : "an offset")} layer. Paste it into one."
            : !inside ? "Move the playhead into the layer."
            : null;
        FlowLine("Paste pose");
        using (ImRaii.Disabled(readOnly || noPaste != null))
        {
            if (ImGui.Button("Paste pose") && copied != null)
                Paste(current, layer.Key, skeleton, copied, frame, false, null);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(noPaste ?? "Key the copied bones at the playhead.");

        // Mirroring reads the pose the layers before this one leave here, which may still be read.
        var local = noPaste == null ? posing.FramesAt(loaded, current.Document, layer, frame)?.Before : null;
        FlowLine("Paste mirrored");
        using (ImRaii.Disabled(readOnly || noPaste != null || local == null))
        {
            if (ImGui.Button("Paste mirrored") && copied != null && local != null)
                Paste(current, layer.Key, skeleton, copied, frame, true, local);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(noPaste ?? (local == null ? decode.Status : "Key the copied bones on the other side at the playhead, mirrored."));

        FlowLine("Clear");
        using (ImRaii.Disabled(readOnly || !bones.Any(bone => layer.Keys.Any(key => key.Bones.ContainsKey(bone)))))
        {
            if (ImGui.Button("Clear"))
                current.Commit(PoseEdits.Clear(current.Document, layer.Key, bones));
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(several ? "Take the selected bones out of every key of this layer." : "Take the bone out of every key of this layer.");
    }

    /// <summary>
    /// A pin layer's buttons for the selected keyed pins: Pin here keys them where they are at the playhead,
    /// as the layers before this one leave them; Delete key and Clear as for other layers; and with the bones
    /// on the character, whether the gizmo turns or moves them. Unpinned and planted bones have no keys.
    /// </summary>
    public void DrawPinButtons(TimelineEditor current, MotionLayer layer, SkeletonDescription skeleton, AnimationTimelineSource loaded, bool readOnly)
    {
        var frame = current.Playhead;
        var inside = frame >= layer.Start && frame <= layer.End;
        var index = posing.BoneTree(skeleton).Index;
        string[] bones = [.. posing.Selection.Bones.Where(bone =>
            index.ContainsKey(bone) && MotionEdits.PinIndex(layer, bone) is var at and >= 0 && !layer.Pins[at].Planted)];
        var several = bones.Length > 1;
        if (bones.Length == 0)
            Widgets.HintWrapped("Select a keyed pin's bone to key it here.");
        else if (!inside)
            Widgets.HintWrapped("The playhead is outside this layer. Move it into the layer to set values.");
        var spots = bones.Length > 0 && inside ? posing.Targets(layer, frame, skeleton, loaded, current.Document, spots: true) : [];
        if (spots == null)
            decode.DrawWait();
        using (ImRaii.Disabled(readOnly || spots is not { Count: > 0 }))
        {
            if (ImGui.Button("Pin here") && spots is { Count: > 0 })
                current.Commit(PoseEdits.Key(current.Document, layer.Key, frame, spots, false));
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Key the selected pinned bones where they are now, at the playhead.");

        FlowLine("Delete key");
        using (ImRaii.Disabled(readOnly || !bones.Any(bone => PoseEdits.Keyed(layer, bone, frame))))
        {
            if (ImGui.Button("Delete key"))
                current.Commit(PoseEdits.Unkey(current.Document, layer.Key, frame, bones));
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(several ? "Take the selected bones out of the key at the playhead." : "Take the bone out of the key at the playhead.");

        FlowLine("Clear");
        using (ImRaii.Disabled(readOnly || !bones.Any(bone => layer.Keys.Any(key => key.Bones.ContainsKey(bone)))))
        {
            if (ImGui.Button("Clear"))
                current.Commit(PoseEdits.Clear(current.Document, layer.Key, bones));
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(several ? "Take the selected bones out of every key of this layer." : "Take the bone out of every key of this layer.");

        if (overlay.Shown)
        {
            DrawAxes(several);
            DrawGizmoChoice(layer, frame, skeleton);
        }
    }

    /// <summary> The next button on this line, or on a new one when it would not fit. </summary>
    private static void FlowLine(string label)
    {
        var width = ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2;
        ImGui.SameLine();
        if (ImGui.GetContentRegionAvail().X < width)
            ImGui.NewLine();
    }

    /// <summary>
    /// Turn: an offset's extra rotation about the bone's own axes, or a pose's whole local rotation, in
    /// degrees. <paramref name="others"/> turn the same way, as the character sees it.
    /// </summary>
    private void DrawTurn(TimelineEditor current, MotionLayer layer, PoseTarget primary, IReadOnlyList<PoseTarget> others,
        Func<string, Quaternion> space, int frame, bool pose)
    {
        TimelineUi.Fact("Turn");
        var id = $"{layer.Key}|{primary.Bone}|{frame}";
        var degrees = turnEdit is { } editing && editing.Id == id ? editing.Degrees : MotionAngles.Wrap(MotionAngles.ToEuler(primary.Value.Rotation));
        ImGui.SetNextItemWidth(Math.Max(Theme.Scaled(150), ImGui.GetContentRegionAvail().X));
        var changed = ImGui.DragFloat3("##turn", ref degrees, .25f, -360, 360, "%.1f");
        if (ImGui.IsItemActivated())
        {
            turnEdit = (id, degrees);
            current.BeginFieldEdit();
        }
        if (changed)
        {
            turnEdit = (id, degrees);
            current.Change(PoseEdits.Set(current.Document, layer.Key, frame, primary,
                primary.Value with { Rotation = MotionAngles.FromEuler(degrees) }, others, space, !pose));
        }
        if (ImGui.IsItemDeactivated())
        {
            turnEdit = null;
            current.EndFieldEdit();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(pose
                ? "The bone's rotation from its parent, in degrees about X, Y and Z. Drag, or Ctrl+click to type."
                : "Extra rotation about the bone's own X, Y and Z axes, in degrees. Drag, or Ctrl+click to type.");
    }

    /// <summary>
    /// Move: an offset's move in character space, or a pose's local position, in centimetres.
    /// <paramref name="others"/> move the same way, a pose's from the clip's own position (<paramref name="own"/>)
    /// where their keys only turned them; Set position marks every selected bone.
    /// </summary>
    private static void DrawMove(TimelineEditor current, MotionLayer layer, PoseTarget primary, IReadOnlyList<PoseTarget> targets,
        IReadOnlyList<PoseTarget> others, Func<string, Quaternion> space, Func<string, Vector3?> own, int frame, bool pose)
    {
        TimelineUi.Fact("Move");
        if (pose)
        {
            // A pose key leaves a bone's position to the motion unless it sets it.
            var set = primary.Moved;
            if (ImGui.Checkbox("##set-position", ref set))
                current.Commit(PoseEdits.SetMoved(current.Document, layer.Key, frame, targets, set));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Set the bone's position too. Unticked, the pose only turns it.");
            ImGui.SameLine(0, Theme.Gap);
            if (!primary.Moved)
            {
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(Theme.Hint, "Turn only");
                return;
            }
        }
        var centimetres = primary.Value.Position * 100;
        ImGui.SetNextItemWidth(Math.Max(Theme.Scaled(150), ImGui.GetContentRegionAvail().X));
        var changed = ImGui.DragFloat3("##move", ref centimetres, .1f, -500, 500, "%.1f cm");
        if (ImGui.IsItemActivated())
            current.BeginFieldEdit();
        if (changed)
            current.Change(PoseEdits.Set(current.Document, layer.Key, frame, primary, primary.Value with { Position = centimetres / 100 }, others,
                space, !pose, pose ? true : null, own));
        if (ImGui.IsItemDeactivated())
            current.EndFieldEdit();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(pose
                ? "The bone's position from its parent, in centimetres."
                : "How far the bone moves in character space, in centimetres: X to the side, Y up, Z forward.");
    }

    /// <summary> Scale: an offset's scale factor, or a pose's local scale, in percent. <paramref name="others"/> scale as much. </summary>
    private static void DrawScale(TimelineEditor current, MotionLayer layer, PoseTarget primary, IReadOnlyList<PoseTarget> others,
        Func<string, Quaternion> space, int frame, bool pose)
    {
        TimelineUi.Fact("Scale");
        var percent = primary.Value.Scale * 100;
        ImGui.SetNextItemWidth(Math.Max(Theme.Scaled(150), ImGui.GetContentRegionAvail().X));
        var changed = ImGui.DragFloat3("##scale", ref percent, .5f, MotionEvaluator.MinScale * 100, MotionEvaluator.MaxScale * 100, "%.0f%%");
        if (ImGui.IsItemActivated())
            current.BeginFieldEdit();
        if (changed)
            current.Change(PoseEdits.Set(current.Document, layer.Key, frame, primary,
                primary.Value with { Scale = Vector3.Clamp(percent / 100, new Vector3(MotionEvaluator.MinScale), new Vector3(MotionEvaluator.MaxScale)) },
                others, space, !pose));
        if (ImGui.IsItemDeactivated())
            current.EndFieldEdit();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(pose ? "The bone's scale, in percent." : "How much the bone scales, in percent: 100% changes nothing.");
    }

    /// <summary> The bone the value fields edit: the layer's own bones first, then the whole skeleton, searchable. Picking one selects only it. </summary>
    public void DrawPicker(SkeletonDescription skeleton, MotionLayer layer)
    {
        var primary = posing.Selection.Primary;
        var preview = primary == null ? "Choose a bone"
            : posing.Selection.Count > 1 ? $"{AnimationPresentation.BoneName(primary)} and {posing.Selection.Count - 1} more"
            : AnimationPresentation.BoneName(primary);
        ImGui.SetNextItemWidth(-1);
        using var combo = ImRaii.Combo("##layer-bone", preview, ImGuiComboFlags.HeightLarge);
        if (!combo.Success)
        {
            if (ComboWheel.Steps() is var steps and not 0 && PickerBones(skeleton, layer, out _) is { Count: > 0 } bones)
                posing.Selection.Only(ComboWheel.Pick(bones, primary!, steps));
            return;
        }
        ImGui.SetNextItemWidth(-1);
        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();
        ImGui.InputTextWithHint("##bone-search", "Search bones", ref boneFilter, 64);
        foreach (var bone in PickerBones(skeleton, layer, out var own))
        {
            var label = AnimationPresentation.BoneName(bone);
            if (boneFilter.Length > 0 && !label.Contains(boneFilter, StringComparison.OrdinalIgnoreCase))
                continue;
            if (ImGui.Selectable($"{label}##{bone}", posing.Selection.Contains(bone)))
            {
                posing.Selection.Only(bone);
                boneFilter = string.Empty;
            }
            if (own.Contains(bone) && ImGui.IsItemHovered())
                ImGui.SetTooltip("This layer already changes it.");
        }
    }

    /// <summary> The picker's bones in order: the layer's own (<paramref name="own"/>), then the rest of the skeleton. </summary>
    private static List<string> PickerBones(SkeletonDescription skeleton, MotionLayer layer, out HashSet<string> own)
    {
        var mine = layer.Keys.SelectMany(key => key.Bones.Keys).Concat(layer.Bones).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        own = mine;
        return [.. mine, .. skeleton.Bones.Select(b => b.Name).Where(name => !mine.Contains(name))];
    }

    /// <summary>
    /// An amplitude's bones: every bone, or the ones it is limited to. The selected bones join the
    /// limit; with <paramref name="picker"/> (the properties panel) a bone picker chooses them.
    /// </summary>
    public void DrawLimit(TimelineEditor current, MotionLayer layer, SkeletonDescription skeleton, bool readOnly, bool picker)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, "Bones");
        ImGui.SameLine(0, Theme.Gap);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(layer.Bones.IsEmpty ? "Every bone" : layer.Bones.Length == 1 ? "1 bone" : $"{layer.Bones.Length} bones");
        foreach (var bone in layer.Bones)
        {
            using var id = ImRaii.PushId(bone);
            using (ImRaii.Disabled(readOnly))
            {
                if (Widgets.GhostIconButton("##remove", Dalamud.Interface.FontAwesomeIcon.Times, "Stop limiting the layer to this bone"))
                    current.Commit(MotionEdits.SetBones(current.Document, layer.Key, layer.Bones.Remove(bone)));
            }
            ImGui.SameLine(0, Theme.Gap);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(AnimationPresentation.BoneName(bone));
        }
        if (picker)
            DrawPicker(skeleton, layer);
        var index = posing.BoneTree(skeleton).Index;
        string[] chosen = [.. posing.Selection.Bones.Where(index.ContainsKey)];
        using (ImRaii.Disabled(readOnly || chosen.Length == 0 || chosen.All(layer.Bones.Contains)))
        {
            if (ImGui.Button(chosen.Length > 1 ? "Limit to selected" : "Limit to this bone"))
                current.Commit(MotionEdits.SetBones(current.Document, layer.Key, layer.Bones.Concat(chosen)));
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(layer.Kind == MotionLayerKind.Fix
                ? "Add the selected bones to the ones this layer fixes. With none listed, it fixes every bone."
                : "Add the selected bones to the ones this layer scales. With none listed, it scales every bone.");
        ImGui.SameLine();
        using (ImRaii.Disabled(readOnly || layer.Bones.IsEmpty))
        {
            if (ImGui.Button("Every bone"))
                current.Commit(MotionEdits.SetBones(current.Document, layer.Key, []));
        }
    }

    /// <summary>
    /// A symmetry layer's bones: the picked ones, each bringing everything below it, how many bones that
    /// copies, and the ones on the changed side without a twin. The selected bones can be added; with
    /// <paramref name="picker"/> (the properties panel) a bone picker chooses them.
    /// </summary>
    public void DrawSymmetryBones(TimelineEditor current, MotionLayer layer, SkeletonDescription skeleton, bool readOnly, bool picker)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, "Bones");
        ImGui.SameLine(0, Theme.Gap);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(layer.Bones.IsEmpty ? "None yet" : layer.Bones.Length == 1 ? "1 bone" : $"{layer.Bones.Length} bones");
        foreach (var bone in layer.Bones)
        {
            using var id = ImRaii.PushId(bone);
            using (ImRaii.Disabled(readOnly))
            {
                if (Widgets.GhostIconButton("##remove", Dalamud.Interface.FontAwesomeIcon.Times, "Stop copying this bone's branch"))
                    current.Commit(MotionEdits.SetBones(current.Document, layer.Key, layer.Bones.Remove(bone)));
            }
            ImGui.SameLine(0, Theme.Gap);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(AnimationPresentation.BoneName(bone));
        }
        if (picker)
        {
            DrawPicker(skeleton, layer);
            Widgets.HintWrapped("Each bone brings everything below it.");
        }
        var index = posing.BoneTree(skeleton).Index;
        string[] chosen = [.. posing.Selection.Bones.Where(index.ContainsKey)];
        using (ImRaii.Disabled(readOnly || chosen.Length == 0 || chosen.All(layer.Bones.Contains)))
        {
            if (ImGui.Button("Add selected"))
                current.Commit(MotionEdits.SetBones(current.Document, layer.Key, layer.Bones.Concat(chosen)));
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Copy the selected bones and everything below them.");

        var (plan, problem, _) = posing.Symmetry(layer, skeleton);
        if (problem != null)
        {
            Widgets.HintWrapped(problem);
            return;
        }
        var from = layer.From == MotionSide.Left ? "left" : "right";
        Widgets.MutedWrapped(plan.Pairs.Length == 1 ? $"Copies 1 bone from the {from} side." : $"Copies {plan.Pairs.Length} bones from the {from} side.");
        if (!plan.Unpaired.IsEmpty)
        {
            var names = string.Join(", ", plan.Unpaired.Take(5).Select(AnimationPresentation.BoneName));
            Widgets.HintWrapped($"No twin, left as is: {names}" + (plan.Unpaired.Length > 5 ? $" and {plan.Unpaired.Length - 5} more." : "."));
        }
    }

    /// <summary>
    /// The rotation ball over the primary bone: its rings show the bone's own axes as the camera sees
    /// it, or the character's. A drag is one undo step and turns every bone the edit changes (see
    /// <see cref="PoseEdits.Turned"/>). With several of them the rings are the character's: each bone's
    /// own axes point elsewhere, so only those turn them all the same way.
    /// </summary>
    private void DrawBall(TimelineEditor current, MotionLayer layer, SkeletonDescription skeleton, BonePosing.BoneFrames? frames,
        IReadOnlyList<PoseTarget> turned, PoseTarget primary, int frame)
    {
        if (CameraRotation() is not { } view)
            return;
        var actor = ActorRotation() ?? Quaternion.Identity;
        var several = turned.Count > 1;
        // A drag forces the character's axes, so the ball shows them too, as the axes line says.
        var drag = overlay.Shown && overlay.Gizmo == GizmoMode.Drag &&
                   TimelineWindowViews.DragGizmo(layer, posing.Selection.Primary, skeleton, posing.BoneTree(skeleton).Index) == null;
        var character = characterAxes || several || drag;
        // The primary bone as the layers turn it, in character space.
        Quaternion? bone = frames is { } known ? Quaternion.Normalize(known.Space(primary.Bone) * Quaternion.Normalize(primary.Value.Rotation)) : null;
        var ring = character || bone is not { } own ? actor : actor * own;
        var size = Math.Min(ImGui.GetContentRegionAvail().X, Theme.Scaled(220));
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, (ImGui.GetContentRegionAvail().X - size) / 2));
        var result = RotationBall.Draw("##bone-ball", size, ring, view);
        // Turns need the bones' frames from the motion, which may still be read.
        if (result.Started && frames != null)
        {
            current.BeginFieldEdit();
            ballEditing = true;
        }
        if (result.Turn is { } turn && frames != null && bone is { } axes)
            current.Change(PoseEdits.Turn(current.Document, layer.Key, frame, turned, character ? turn : PoseEdits.Around(axes, turn), frames.Space));
        if (result.Ended && ballEditing)
        {
            current.EndFieldEdit();
            ballEditing = false;
        }
        if (frames == null)
            decode.DrawWait();
        DrawAxes(several, drag);
        if (overlay.Shown)
            DrawGizmoChoice(layer, frame, skeleton);
    }

    /// <summary>
    /// Whether the ball and the gizmo use the bone's own axes or the character's; several bones and a drag
    /// (<paramref name="drag"/>) always use the character's.
    /// </summary>
    private void DrawAxes(bool several, bool drag = false)
    {
        var forced = several || drag;
        using (ImRaii.Disabled(forced))
        {
            if (ImGui.RadioButton("Bone axes", !(characterAxes || forced)))
                characterAxes = false;
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(several
                ? "Several bones turn about the character's axes: their own axes point different ways."
                : drag ? "A drag moves the bone along the character's axes."
                : "The ball and the gizmo use the bone's own axes.");
        ImGui.SameLine();
        if (ImGui.RadioButton("Character axes", characterAxes || forced))
            characterAxes = true;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("The ball and the gizmo use the character's axes: up, forward and to the side.");
    }

    /// <summary> Whether the gizmo on the character turns, moves or drags, and what it waits for. </summary>
    private void DrawGizmoChoice(MotionLayer layer, int frame, SkeletonDescription skeleton)
    {
        // A pin moves its key, and turns it when it holds its turn.
        var (turns, moves) = layer.Kind == MotionLayerKind.Pin
            ? (posing.Selection.Primary is { } primary && MotionEdits.PinIndex(layer, primary) is var at and >= 0 && layer.Pins[at].HoldTurn, true)
            : (layer.Components.HasFlag(PoseComponents.Rotation), layer.Components.HasFlag(PoseComponents.Position));
        var dragHint = TimelineWindowViews.DragGizmo(layer, posing.Selection.Primary, skeleton, posing.BoneTree(skeleton).Index);
        // Drag kept from a layer where it could drag reads as Turn here.
        var drag = overlay.Gizmo == GizmoMode.Drag && dragHint == null;
        var move = !drag && moves && (overlay.Gizmo == GizmoMode.Move || !turns);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, "Gizmo");
        ImGui.SameLine(0, Theme.Gap);
        using (ImRaii.Disabled(!turns))
        {
            if (ImGui.RadioButton("Turn##gizmo-turn", !move && !drag))
                overlay.Gizmo = GizmoMode.Turn;
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(turns ? "The gizmo on the character turns the bones."
                : layer.Kind == MotionLayerKind.Pin ? "This pin holds only its position." : "This layer doesn't turn bones.");
        ImGui.SameLine();
        using (ImRaii.Disabled(!moves))
        {
            if (ImGui.RadioButton("Move##gizmo-move", move))
                overlay.Gizmo = GizmoMode.Move;
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(moves ? "The gizmo on the character moves the bones." : "This layer doesn't move bones.");
        ImGui.SameLine();
        using (ImRaii.Disabled(dragHint != null))
        {
            if (ImGui.RadioButton("Drag##gizmo-drag", drag))
                overlay.Gizmo = GizmoMode.Drag;
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(dragHint ?? "The gizmo drags the bone; the limb above bends to follow.");
        if (overlay.GizmoWaits(layer, frame, skeleton) is { } reason)
            Widgets.HintWrapped(reason);
    }

    /// <summary>The rotation of the game camera's view, for the ball; null without a camera.</summary>
    private static unsafe Quaternion? CameraRotation()
    {
        var manager = CameraManager.Instance();
        if (manager == null)
            return null;
        var camera = manager->GetActiveCamera();
        if (camera == null)
            return null;
        Matrix4x4 view = camera->CameraBase.SceneCamera.ViewMatrix;
        view.M44 = 1;
        return Matrix4x4.Decompose(view, out _, out var rotation, out _) ? Quaternion.Normalize(rotation) : null;
    }

    private Quaternion? ActorRotation()
    {
        try { return session.Playback.ActorRotation(); }
        catch (InvalidOperationException) { return null; }
    }
}
