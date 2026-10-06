using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui.Pose;
using InstantEdit.Ui.Timeline;

namespace InstantEdit.Ui;

/// <summary>
/// The selected bone layer in the properties panel: its timing, amount and the parts of a bone it
/// changes; for offsets and poses the selected bones' values at the playhead, which set the layer's
/// key there; for amplitudes the bones it is limited to. The bone editor is shared with the Bones
/// window, and so is the selection. A field's changes until it is left are one undo step, and Live
/// shows each change as it is made.
/// </summary>
public sealed partial class TimelineWindow
{
    private string boneFilter = string.Empty;
    // The angles a turn field shows while it is being dragged: read back from the rotation they make,
    // they could flip to an equivalent set mid-drag.
    private (string Id, Vector3 Degrees)? turnEdit;
    // The layer the selection last started on: a newly chosen layer with nothing selected starts on its first bone.
    private string? selectionLayer;

    private void DrawLayerProperties(TimelineEditor current, MotionLayer layer, AnimationTimelineSource loaded)
    {
        var readOnly = ReadOnly;
        var skeleton = Fresh(loaded).Clip.Resolution?.Selected?.Skeleton;
        if (skeleton != null)
        {
            KeepSelection(skeleton);
            if (selectionLayer != layer.Key)
            {
                selectionLayer = layer.Key;
                if (selection.Count == 0 && (layer.Keys.SelectMany(key => key.Bones.Keys).FirstOrDefault() ?? layer.Bones.FirstOrDefault()) is { } first &&
                    BoneTree(skeleton).Index.ContainsKey(first))
                    selection.Only(first);
            }
        }

        Widgets.Icon(LayerIcon(layer.Kind), TimelineWindowViews.LayerColour(layer.Kind));
        ImGui.SameLine(0, Theme.Gap);
        using (ImRaii.Disabled(readOnly))
        {
            var name = layer.Name;
            ImGui.SetNextItemWidth(Math.Max(Theme.Scaled(80), ImGui.GetContentRegionAvail().X - Theme.Scaled(48)));
            var renamed = ImGui.InputText("##layer-name", ref name, MotionEdits.MaxName);
            TrackLayer(current, renamed, renamed ? layer with { Name = name } : null);
            ImGui.SameLine();
            var on = layer.Enabled;
            if (ImGui.Checkbox("##layer-on", ref on))
                current.Commit(MotionEdits.SetEnabled(current.Document, layer.Key, on));
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(layer.Enabled ? "On. Untick to see and save the motion without it." : "Off. Tick to apply it again.");
        Widgets.HintWrapped(TimelineWindowViews.LayerKindHint(layer.Kind));
        ImGui.Spacing();

        using (var facts = ImRaii.Table("##layer-facts", 2, ImGuiTableFlags.SizingFixedFit))
        {
            if (facts.Success)
            {
                ImGui.TableSetupColumn("##fact", ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableSetupColumn("##value", ImGuiTableColumnFlags.WidthStretch);
                using (ImRaii.Disabled(readOnly))
                {
                    FrameField(current, layer, "Starts", "##layer-start", layer.Start, start => layer with { Start = start });
                    FrameField(current, layer, "Ends", "##layer-end", layer.End, end => layer with { End = end });
                    FrameField(current, layer, "Fade in", "##layer-fade-in", layer.FadeIn, fade => layer with { FadeIn = fade });
                    FrameField(current, layer, "Fade out", "##layer-fade-out", layer.FadeOut, fade => layer with { FadeOut = fade });
                    DrawAmount(current, layer);
                    DrawComponents(current, layer);
                    if (layer.Kind != MotionLayerKind.Amplitude)
                        DrawInterpolation(current, layer);
                }
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        if (skeleton == null)
            Widgets.HintWrapped(Fresh(loaded).Clip.Resolution?.Reason ?? "Choose the skeleton this animation was made for in the Source tab to pick bones.");
        else if (layer.Kind == MotionLayerKind.Amplitude)
            DrawLayerLimit(current, layer, skeleton, readOnly, true);
        else
        {
            DrawBonePicker(skeleton, layer);
            DrawBoneEditor(current, layer, skeleton, loaded, readOnly, false);
            ImGui.Spacing();
            DrawLayerKeys(current, layer);
        }

        ImGui.Spacing();
        using (ImRaii.Disabled(readOnly))
        {
            if (ImGui.Button("Duplicate"))
                current.DuplicateLayer();
            ImGui.SameLine();
            if (ImGui.Button("Delete layer"))
                current.Commit(MotionEdits.Remove(current.Document, layer.Key));
        }
        ImGui.SameLine();
        if (ImGui.Button("Bones"))
            ToggleBones();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Pick and pose bones on a body map.");
    }

    /// <summary> A frame number field with its time beside it. </summary>
    private static void FrameField(TimelineEditor current, MotionLayer layer, string label, string id, int value, Func<int, MotionLayer> with)
    {
        Fact(label);
        var number = value;
        ImGui.SetNextItemWidth(Theme.Scaled(90));
        var changed = ImGui.InputInt(id, ref number, 0, 0);
        TrackLayer(current, changed, changed ? with(Math.Clamp(number, 0, TimelineDocument.MaxFrames)) : null);
        ImGui.SameLine(0, Theme.Gap);
        ImGui.TextColored(Theme.Muted, TimelineText.Seconds(value));
    }

    private static void DrawAmount(TimelineEditor current, MotionLayer layer)
    {
        Fact(layer.Kind switch { MotionLayerKind.Offset => "Strength", MotionLayerKind.Amplitude => "Movement", _ => "Blend" });
        var percent = layer.Amount * 100;
        ImGui.SetNextItemWidth(Math.Max(Theme.Scaled(90), ImGui.GetContentRegionAvail().X));
        var changed = ImGui.SliderFloat("##layer-amount", ref percent, 0, MotionEdits.MaxAmount(layer.Kind) * 100, "%.0f%%");
        TrackLayer(current, changed, changed ? layer with { Amount = percent / 100 } : null);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(layer.Kind switch
            {
                MotionLayerKind.Offset => "How much of the keyed offsets applies. Ctrl+click to type.",
                MotionLayerKind.Amplitude => "100% keeps the motion. Less calms it toward the average pose, more exaggerates it. Ctrl+click to type.",
                _ => "How far the bones blend toward the keyed poses. Ctrl+click to type.",
            });
    }

    private static void DrawComponents(TimelineEditor current, MotionLayer layer)
    {
        Fact("Changes");
        PoseComponents[] parts = layer.Kind == MotionLayerKind.Amplitude
            ? [PoseComponents.Rotation, PoseComponents.Position]
            : [PoseComponents.Rotation, PoseComponents.Position, PoseComponents.Scale];
        var first = true;
        foreach (var part in parts)
        {
            if (!first)
                ImGui.SameLine(0, Theme.Scaled(10));
            first = false;
            var on = layer.Components.HasFlag(part);
            if (ImGui.Checkbox(part switch { PoseComponents.Rotation => "Turn", PoseComponents.Position => "Move", _ => "Scale" } + "##part-" + part, ref on))
                current.Commit(MotionEdits.Replace(current.Document, layer with { Components = on ? layer.Components | part : layer.Components & ~part }));
        }
    }

    private static void DrawInterpolation(TimelineEditor current, MotionLayer layer)
    {
        Fact("Between keys");
        ImGui.SetNextItemWidth(Theme.Scaled(120));
        using var combo = ImRaii.Combo("##layer-interpolation", InterpolationName(layer.Interpolation));
        if (!combo.Success)
            return;
        foreach (var mode in Enum.GetValues<MotionInterpolation>())
            if (ImGui.Selectable(InterpolationName(mode), mode == layer.Interpolation) && mode != layer.Interpolation)
                current.Commit(MotionEdits.Replace(current.Document, layer with { Interpolation = mode }));
    }

    private static string InterpolationName(MotionInterpolation mode) => mode switch
    {
        MotionInterpolation.Linear => "Linear",
        MotionInterpolation.Smooth => "Smooth",
        _ => "Hold",
    };

    /// <summary>
    /// The selected bones' values at the playhead, for an offset or a pose layer: the primary bone's turn,
    /// move and scale, each change keyed there and made to every other selected bone too; then Key,
    /// Delete key, Reset, Mirror and Clear for all of them. With <paramref name="ball"/> (the Bones
    /// window) the rotation ball comes first.
    /// </summary>
    private void DrawBoneEditor(TimelineEditor current, MotionLayer layer, SkeletonDescription skeleton, AnimationTimelineSource loaded,
        bool readOnly, bool ball)
    {
        var (index, _) = BoneTree(skeleton);
        if (selection.Primary is not { } bone || !index.ContainsKey(bone))
        {
            Widgets.HintWrapped(ball ? "Click a bone on the map to set its value at the playhead." : "Choose a bone to set its value at the playhead.");
            return;
        }
        var frame = current.Playhead;
        var inside = frame >= layer.Start && frame <= layer.End;
        var pose = layer.Kind == MotionLayerKind.Pose;
        if (Targets(layer, frame, skeleton, loaded) is not { Count: > 0 } targets)
        {
            DrawMotionWait();
            return;
        }
        var primary = targets[0];
        // Several bones change the way the primary one does, as the character sees it, which needs
        // their frames from the motion; so does the ball. One bone alone changes in its own space.
        var turned = targets.Count > 1 ? PoseEdits.Turned(targets, bone, skeleton) : targets;
        var frames = ball || turned.Count > 1 ? FramesAt(loaded, current.Document, layer, frame) : null;
        if (turned.Count > 1 && frames == null)
        {
            DrawMotionWait();
            return;
        }
        Func<string, Quaternion> space = frames != null ? frames.Space : _ => Quaternion.Identity;
        List<PoseTarget> others = [.. turned.Where(target => target.Bone != bone)];

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
                DrawBall(current, layer, frames, turned, primary, frame);
            using var table = ImRaii.Table("##bone-value", 2, ImGuiTableFlags.SizingFixedFit);
            if (table.Success)
            {
                ImGui.TableSetupColumn("##fact", ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableSetupColumn("##value", ImGuiTableColumnFlags.WidthStretch);
                if (layer.Components.HasFlag(PoseComponents.Rotation))
                    DrawTurn(current, layer, primary, others, space, frame, pose);
                if (layer.Components.HasFlag(PoseComponents.Position))
                    DrawMove(current, layer, primary, targets, others, space, frame, pose);
                if (layer.Components.HasFlag(PoseComponents.Scale))
                    DrawScale(current, layer, primary, others, space, frame, pose);
            }
        }
        DrawBoneButtons(current, layer, skeleton, loaded, targets, frame, readOnly, inside);
    }

    /// <summary> Key, Delete key, Reset, Mirror and Clear, each for every selected bone at once. </summary>
    private void DrawBoneButtons(TimelineEditor current, MotionLayer layer, SkeletonDescription skeleton, AnimationTimelineSource loaded,
        List<PoseTarget> targets, int frame, bool readOnly, bool inside)
    {
        var pose = layer.Kind == MotionLayerKind.Pose;
        var (index, _) = BoneTree(skeleton);
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
                var motion = pose ? MotionPose(loaded, frame) : null;
                current.Commit(PoseEdits.Reset(current.Document, layer.Key, frame, bones, pose,
                    bone => motion is { } known && index.TryGetValue(bone, out var at) ? known.Local[at] : null));
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(pose ? "Key the clip's own pose at the playhead." : "Key no change at the playhead, so the clip's own motion shows there.");

        var pairs = PoseEdits.MirrorPairs(selection.Bones, selection.Primary, index.ContainsKey)
            .Where(pair => layer.Keys.Any(key => key.Bones.ContainsKey(pair.From))).ToList();
        var mirrorReady = pairs.Count == 0 || DecodedPose(loaded) != null;
        FlowLine("Mirror");
        using (ImRaii.Disabled(readOnly || pairs.Count == 0 || !mirrorReady))
        {
            if (ImGui.Button("Mirror") && decoded is { } clip &&
                PoseEdits.Mirror(current.Document, layer.Key, pairs, skeleton, at => clip.Pose(at)) is { } mirrored)
                current.Commit(mirrored);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(pairs.Count == 0 ? "Select a bone with keys in this layer and a twin on the other side."
                : !mirrorReady ? decodeStatus
                : "Give the other side the same edits, mirrored. Replaces what the other side has in this layer.");

        FlowLine("Clear");
        using (ImRaii.Disabled(readOnly || !bones.Any(bone => layer.Keys.Any(key => key.Bones.ContainsKey(bone)))))
        {
            if (ImGui.Button("Clear"))
                current.Commit(PoseEdits.Clear(current.Document, layer.Key, bones));
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(several ? "Take the selected bones out of every key of this layer." : "Take the bone out of every key of this layer.");
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
    /// The selected bones the skeleton has, primary first, each with its value in the layer at
    /// <paramref name="frame"/>; null while a pose layer waits for the clip's own motion.
    /// </summary>
    private List<PoseTarget>? Targets(MotionLayer layer, int frame, SkeletonDescription skeleton, AnimationTimelineSource loaded)
    {
        var (index, _) = BoneTree(skeleton);
        (MotionTransform[] Local, MotionTransform[] Model)? motion = null;
        if (layer.Kind == MotionLayerKind.Pose && (motion = MotionPose(loaded, frame)) == null)
            return null;
        var targets = new List<PoseTarget>();
        foreach (var bone in selection.Primary is { } primary ? selection.Bones.Where(name => name != primary).Prepend(primary) : selection.Bones)
        {
            if (!index.TryGetValue(bone, out var at) || PoseEdits.Value(layer, bone, frame, motion?.Local[at]) is not { } value)
                continue;
            targets.Add(new PoseTarget(bone, value, PoseEdits.Moved(layer, bone, frame)));
        }
        return targets;
    }

    /// <summary>The decoded motion when it is this clip's current one; null while it is read, which this starts.</summary>
    private MotionClip? DecodedPose(AnimationTimelineSource loaded)
    {
        var fresh = Fresh(loaded);
        if (decoded != null && decodedKey == DecodeKey(fresh))
            return decoded;
        EnsureDecode(fresh);
        return null;
    }

    /// <summary>
    /// Turn: an offset's extra rotation about the bone's own axes, or a pose's whole local rotation, in
    /// degrees. <paramref name="others"/> turn the same way, as the character sees it.
    /// </summary>
    private void DrawTurn(TimelineEditor current, MotionLayer layer, PoseTarget primary, IReadOnlyList<PoseTarget> others,
        Func<string, Quaternion> space, int frame, bool pose)
    {
        Fact("Turn");
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
    /// <paramref name="others"/> move the same way; Set position marks every selected bone.
    /// </summary>
    private static void DrawMove(TimelineEditor current, MotionLayer layer, PoseTarget primary, IReadOnlyList<PoseTarget> targets,
        IReadOnlyList<PoseTarget> others, Func<string, Quaternion> space, int frame, bool pose)
    {
        Fact("Move");
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
                space, !pose, pose ? true : null));
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
        Fact("Scale");
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
    private void DrawBonePicker(SkeletonDescription skeleton, MotionLayer layer)
    {
        var primary = selection.Primary;
        var preview = primary == null ? "Choose a bone"
            : selection.Count > 1 ? $"{AnimationPresentation.BoneName(primary)} and {selection.Count - 1} more"
            : AnimationPresentation.BoneName(primary);
        ImGui.SetNextItemWidth(-1);
        using var combo = ImRaii.Combo("##layer-bone", preview, ImGuiComboFlags.HeightLarge);
        if (!combo.Success)
            return;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();
        ImGui.InputTextWithHint("##bone-search", "Search bones", ref boneFilter, 64);
        var own = layer.Keys.SelectMany(key => key.Bones.Keys).Concat(layer.Bones).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        foreach (var bone in own.Concat(skeleton.Bones.Select(b => b.Name).Where(name => !own.Contains(name))))
        {
            var label = AnimationPresentation.BoneName(bone);
            if (boneFilter.Length > 0 && !label.Contains(boneFilter, StringComparison.OrdinalIgnoreCase))
                continue;
            if (ImGui.Selectable($"{label}##{bone}", selection.Contains(bone)))
            {
                selection.Only(bone);
                boneFilter = string.Empty;
            }
            if (own.Contains(bone) && ImGui.IsItemHovered())
                ImGui.SetTooltip("This layer already changes it.");
        }
    }

    /// <summary> The layer's keys: click one to move the playhead there and select it. </summary>
    private static void DrawLayerKeys(TimelineEditor current, MotionLayer layer)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, "Keys");
        if (layer.Keys.IsEmpty)
        {
            ImGui.SameLine(0, Theme.Gap);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Hint, "None yet. Set a bone's value to add one.");
            return;
        }
        foreach (var key in layer.Keys)
        {
            ImGui.SameLine(0, Theme.Scaled(4));
            if (ImGui.GetContentRegionAvail().X < Theme.Scaled(48))
                ImGui.NewLine();
            var chosen = current.SelectedKey == key.Frame;
            using var colour = ImRaii.PushColor(ImGuiCol.Button, Theme.Selection, chosen);
            if (ImGui.SmallButton($"{TimelineText.Seconds(key.Frame)}##key-{key.Frame}"))
            {
                current.Playhead = key.Frame;
                current.SelectLayer(layer.Key, key.Frame);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"{TimelineWindowViews.FrameText(key.Frame)} · {(key.Bones.Count == 1 ? "1 bone" : $"{key.Bones.Count} bones")}");
        }
    }

    /// <summary>
    /// An amplitude's bones: every bone, or the ones it is limited to. The selected bones join the
    /// limit; with <paramref name="picker"/> (the properties panel) a bone picker chooses them.
    /// </summary>
    private void DrawLayerLimit(TimelineEditor current, MotionLayer layer, SkeletonDescription skeleton, bool readOnly, bool picker)
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
            DrawBonePicker(skeleton, layer);
        var index = BoneTree(skeleton).Index;
        string[] chosen = [.. selection.Bones.Where(index.ContainsKey)];
        using (ImRaii.Disabled(readOnly || chosen.Length == 0 || chosen.All(layer.Bones.Contains)))
        {
            if (ImGui.Button(chosen.Length > 1 ? "Limit to selected" : "Limit to this bone"))
                current.Commit(MotionEdits.SetBones(current.Document, layer.Key, layer.Bones.Concat(chosen)));
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Add the selected bones to the ones this layer scales. With none listed, it scales every bone.");
        ImGui.SameLine();
        using (ImRaii.Disabled(readOnly || layer.Bones.IsEmpty))
        {
            if (ImGui.Button("Every bone"))
                current.Commit(MotionEdits.SetBones(current.Document, layer.Key, []));
        }
    }

    /// <summary> A field edit of the layer's own values: begun when the field activates, one undo step when it is left. </summary>
    private static void TrackLayer(TimelineEditor current, bool changed, MotionLayer? next)
    {
        if (ImGui.IsItemActivated())
            current.BeginFieldEdit();
        if (changed && next != null)
            current.Change(MotionEdits.Replace(current.Document, next));
        if (ImGui.IsItemDeactivated())
            current.EndFieldEdit();
    }
}
