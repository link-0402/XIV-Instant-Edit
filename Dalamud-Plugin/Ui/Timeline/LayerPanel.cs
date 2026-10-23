using System.Collections.Immutable;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// The selected bone layer in the properties panel: its timing, amount and the parts of a bone it
/// changes; for offsets and poses the selected bones' values at the playhead, which set the layer's
/// key there (<see cref="BoneEditor"/>), and the selected key's ease; for amplitudes the bones it is
/// limited to. A field's changes until it is left are one undo step, and Live shows each change as it
/// is made.
/// </summary>
internal sealed class LayerPanel(TimelineSession session, BonePosing posing, BoneEditor boneEditor, PoseSourcePanel poseSources, Window bonesWindow)
{
    // The layer the selection last started on: a newly chosen layer with nothing selected starts on its first bone.
    private string? selectionLayer;
    // The ease curve handle being dragged: 0 or 1, -1 for none.
    private int easeHandle = -1;

    public void Draw(TimelineEditor current, MotionLayer layer, AnimationTimelineSource loaded)
    {
        var readOnly = session.ReadOnly;
        var skeleton = session.Fresh(loaded).Clip.Resolution?.Selected?.Skeleton;
        if (skeleton != null)
        {
            posing.KeepSelection(skeleton);
            if (selectionLayer != layer.Key)
            {
                selectionLayer = layer.Key;
                if (posing.Selection.Count == 0 && (layer.Keys.SelectMany(key => key.Bones.Keys).FirstOrDefault() ?? layer.Bones.FirstOrDefault() ?? layer.Pins.FirstOrDefault()?.Bone) is { } first &&
                    posing.BoneTree(skeleton).Index.ContainsKey(first))
                    posing.Selection.Only(first);
            }
        }

        Widgets.Icon(TimelineUi.LayerIcon(layer.Kind), TimelineWindowViews.LayerColour(layer.Kind));
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
        if (layer.Kind == MotionLayerKind.Symmetry)
            using (ImRaii.Disabled(readOnly))
                DrawFrom(current, layer);

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
                    // A pin holds a spot and a turn, whatever parts are set.
                    if (layer.Kind != MotionLayerKind.Pin)
                        DrawComponents(current, layer);
                    if (layer.Kind is not (MotionLayerKind.Amplitude or MotionLayerKind.Symmetry))
                        DrawInterpolation(current, layer);
                }
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        if (skeleton == null)
            Widgets.HintWrapped(session.Fresh(loaded).Clip.Resolution?.Reason ?? "Choose the skeleton this animation was made for under Source to pick bones.");
        else if (layer.Kind == MotionLayerKind.Amplitude)
            boneEditor.DrawLimit(current, layer, skeleton, readOnly, true);
        else if (layer.Kind == MotionLayerKind.Symmetry)
            boneEditor.DrawSymmetryBones(current, layer, skeleton, readOnly, true);
        else if (layer.Kind == MotionLayerKind.Pin)
        {
            DrawPins(current, layer, skeleton, loaded, readOnly);
            if (layer.Pins.Any(pin => !pin.Planted))
            {
                ImGui.Spacing();
                boneEditor.DrawPinButtons(current, layer, skeleton, loaded, readOnly);
                ImGui.Spacing();
                DrawLayerKeys(current, layer);
                if (current.SelectedKey is { } pinKeyFrame && MotionEdits.KeyIndex(layer, pinKeyFrame) is var pinKeyAt and >= 0)
                {
                    ImGui.Spacing();
                    DrawKeyEase(current, layer, layer.Keys[pinKeyAt], readOnly);
                }
            }
        }
        else
        {
            boneEditor.DrawPicker(skeleton, layer);
            boneEditor.Draw(current, layer, skeleton, loaded, readOnly, false);
            ImGui.Spacing();
            DrawLayerKeys(current, layer);
            if (current.SelectedKey is { } keyFrame && MotionEdits.KeyIndex(layer, keyFrame) is var keyAt and >= 0)
            {
                ImGui.Spacing();
                DrawKeyEase(current, layer, layer.Keys[keyAt], readOnly);
            }
            if (layer.Kind == MotionLayerKind.Pose)
            {
                ImGui.Spacing();
                poseSources.Draw(current, layer, skeleton, loaded, readOnly);
            }
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
            TimelineUi.Toggle(bonesWindow);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Pick and pose bones on a body map.");
    }

    /// <summary>
    /// A pin layer's pins, one block each: the bone with a button to unpin it, what it holds, what it rides
    /// with, how far up it bends and when it holds; for a planted pin how readily it counts as down and where
    /// the motion has it down. Every change is one undo step. Then why a pin can't do what it should.
    /// </summary>
    private void DrawPins(TimelineEditor current, MotionLayer layer, SkeletonDescription skeleton, AnimationTimelineSource loaded, bool readOnly)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, "Pins");
        if (layer.Pins.IsEmpty)
        {
            ImGui.SameLine(0, Theme.Gap);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Hint, "None yet. Select bones and choose Pin selected in the Bones window.");
            return;
        }
        var facts = layer.Pins.Any(pin => pin.Planted) ? posing.PlantedFacts(loaded, current.Document, layer) : null;
        var index = posing.BoneTree(skeleton).Index;
        foreach (var pin in layer.Pins)
        {
            using var id = ImRaii.PushId(pin.Bone);
            ImGui.Spacing();
            using (ImRaii.Disabled(readOnly))
            {
                if (Widgets.GhostIconButton("##unpin", Dalamud.Interface.FontAwesomeIcon.Times, "Unpin this bone"))
                    current.Commit(MotionEdits.RemovePin(current.Document, layer.Key, pin.Bone));
            }
            ImGui.SameLine(0, Theme.Gap);
            // A click selects the bone, for Pin here and the gizmo.
            if (ImGui.Selectable(AnimationPresentation.BoneName(pin.Bone), posing.Selection.Contains(pin.Bone)) && index.ContainsKey(pin.Bone))
                posing.Selection.Only(pin.Bone);
            PinChain? chain = null;
            string? problem = null;
            try
            {
                chain = PinChains.Chain(skeleton, index, pin);
                // What Live and the save would refuse, such as riding a bone the solve moves.
                PinChains.Check(skeleton, layer with { Pins = [pin] });
            }
            catch (InvalidDataException e)
            {
                problem = e.Message;
            }
            using (ImRaii.Disabled(readOnly))
            using (var table = ImRaii.Table("##pin-settings", 2, ImGuiTableFlags.SizingFixedFit))
            {
                if (table.Success)
                {
                    ImGui.TableSetupColumn("##fact", ImGuiTableColumnFlags.WidthFixed);
                    ImGui.TableSetupColumn("##value", ImGuiTableColumnFlags.WidthStretch);
                    DrawPinSettings(current, layer, pin, skeleton, index, chain);
                }
            }
            if (problem != null)
                Widgets.HintWrapped(problem);
            else if (chain is { Shape: PinChainShape.None })
                Widgets.HintWrapped($"{AnimationPresentation.BoneName(pin.Bone)} sits right under the body, so nothing can bend to hold it.");
            if (pin.Planted && problem == null && chain is not { Shape: PinChainShape.None })
                Widgets.MutedWrapped(facts == null ? "Reading the motion"
                    : facts.TryGetValue(pin.Bone, out var line) ? line
                    : facts.GetValueOrDefault(string.Empty) ?? "Not held anywhere");
        }
        string[] untracked;
        try
        {
            untracked = [.. PinChains.Untracked(skeleton, layer, posing.DecodedTracks(skeleton))];
        }
        catch (InvalidDataException)
        {
            untracked = [];
        }
        if (untracked.Length > 0)
        {
            var names = string.Join(", ", untracked.Take(3).Select(AnimationPresentation.BoneName)) + (untracked.Length > 3 ? $" and {untracked.Length - 3} more" : "");
            Widgets.HintWrapped($"This layer bends {names}, which the clip doesn't move. Saving gives them tracks, so physics stops moving them.");
        }
    }

    /// <summary> One pin's settings, as fact rows: Holds, Attached to, Bends up to, When, and Sensitivity when planted. </summary>
    private static void DrawPinSettings(TimelineEditor current, MotionLayer layer, MotionPin pin, SkeletonDescription skeleton, Dictionary<string, int> index,
        PinChain? chain)
    {
        PinChoice(current, layer, "Holds", "##pin-holds", "Hold the bone's position, or its turn as well.",
            pin.HoldTurn ? "Position and turn" : "Position", () => [(false, "Position"), (true, "Position and turn")], pin.HoldTurn,
            holds => pin with { HoldTurn = holds });

        // A bone the pin's own solve moves would chase itself: everything from the chain's top down.
        var top = chain is null ? (index.TryGetValue(pin.Bone, out var own) ? own : -1) : chain.Bones.IsEmpty ? chain.End : chain.Bones[0];
        PinChoice(current, layer, "Attached to", "##pin-relative", "What the held spot moves with. Character keeps it still; a bone, such as a knee, carries it along. Changing it takes out the pin's keys.",
            pin.RelativeTo is { } riding ? AnimationPresentation.BoneName(riding) : "Character",
            () => [(null, "Character"), .. Enumerable.Range(0, skeleton.Bones.Length)
                .Where(b => b != top && !BonePosing.IsBelow(skeleton, b, top))
                .Select(b => ((string?)skeleton.Bones[b].Name, AnimationPresentation.BoneName(skeleton.Bones[b].Name)))],
            pin.RelativeTo, relative => pin with { RelativeTo = relative });

        string fallback;
        ImmutableArray<string> choices;
        // Default is offered unless the pin rides a bone the default top would bend.
        var offerDefault = true;
        try
        {
            var usual = PinChains.DefaultTop(skeleton, pin.Bone);
            fallback = usual != null ? $"Default ({AnimationPresentation.BoneName(usual)})" : "Default (nothing bends)";
            choices = PinChains.TopChoices(skeleton, pin.Bone, pin.RelativeTo);
            offerDefault = usual == null || pin.Top == null || choices.Contains(usual);
        }
        catch (InvalidDataException)
        {
            fallback = "Default";
            choices = [];
        }
        PinChoice(current, layer, "Bends up to", "##pin-top", "The highest bone that may turn to hold the pin.",
            pin.Top is { } highest ? AnimationPresentation.BoneName(highest) : fallback,
            () => [.. offerDefault ? new[] { ((string?)null, fallback) } : Array.Empty<(string?, string)>(), .. choices.Select(name => ((string?)name, AnimationPresentation.BoneName(name)))],
            pin.Top, highest => pin with { Top = highest });

        PinChoice(current, layer, "When", "##pin-when", "At keys holds the spots you key, such as a hand on a knee. When planted holds the bone wherever the motion rests it, such as feet on the floor.",
            pin.Planted ? "When planted" : "At keys", () => [(false, "At keys"), (true, "When planted")], pin.Planted,
            planted => pin with { Planted = planted });
        if (pin.Planted)
            PinChoice(current, layer, "Sensitivity", "##pin-sensitivity", "How readily the bone counts as down. Strict holds less, Loose more.",
                pin.Sensitivity.ToString(), () => [(PinSensitivity.Normal, "Normal"), (PinSensitivity.Strict, "Strict"), (PinSensitivity.Loose, "Loose")],
                pin.Sensitivity, sensitivity => pin with { Sensitivity = sensitivity });
    }

    /// <summary> A fact row with a combo of <paramref name="options"/>, listed only while open; a pick commits the changed pin. </summary>
    private static void PinChoice<T>(TimelineEditor current, MotionLayer layer, string label, string id, string tooltip, string preview,
        Func<IEnumerable<(T Value, string Name)>> options, T value, Func<T, MotionPin> with)
    {
        TimelineUi.Fact(label);
        ImGui.SetNextItemWidth(Math.Max(Theme.Scaled(140), ImGui.GetContentRegionAvail().X));
        using (var combo = ImRaii.Combo(id, preview, ImGuiComboFlags.HeightLarge))
        {
            if (combo.Success)
            {
                var at = 0;
                foreach (var (option, name) in options())
                {
                    var chosen = EqualityComparer<T>.Default.Equals(option, value);
                    if (ImGui.Selectable($"{name}##{at++}", chosen) && !chosen)
                        current.Commit(MotionEdits.SetPin(current.Document, layer.Key, with(option)));
                }
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(tooltip);
    }

    /// <summary> A frame number field with its time beside it. </summary>
    private static void FrameField(TimelineEditor current, MotionLayer layer, string label, string id, int value, Func<int, MotionLayer> with)
    {
        TimelineUi.Fact(label);
        var number = value;
        ImGui.SetNextItemWidth(Theme.Scaled(90));
        var changed = ImGui.InputInt(id, ref number, 0, 0);
        TrackLayer(current, changed, changed ? with(Math.Clamp(number, 0, TimelineDocument.MaxFrames)) : null);
        ImGui.SameLine(0, Theme.Gap);
        ImGui.TextColored(Theme.Muted, TimelineText.Seconds(value));
    }

    private static void DrawAmount(TimelineEditor current, MotionLayer layer)
    {
        TimelineUi.Fact(layer.Kind switch { MotionLayerKind.Offset => "Strength", MotionLayerKind.Amplitude => "Movement", MotionLayerKind.Pin => "Pull", _ => "Blend" });
        var percent = layer.Amount * 100;
        ImGui.SetNextItemWidth(Math.Max(Theme.Scaled(90), ImGui.GetContentRegionAvail().X));
        var changed = ImGui.SliderFloat("##layer-amount", ref percent, 0, MotionEdits.MaxAmount(layer.Kind) * 100, "%.0f%%");
        TrackLayer(current, changed, changed ? layer with { Amount = percent / 100 } : null);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(layer.Kind switch
            {
                MotionLayerKind.Offset => "How much of the keyed offsets applies. Ctrl+click to type.",
                MotionLayerKind.Amplitude => "100% keeps the motion. Less calms it toward the average pose, more exaggerates it. Ctrl+click to type.",
                MotionLayerKind.Symmetry => "How far the bones blend toward the other side's motion. Ctrl+click to type.",
                MotionLayerKind.Pin => "How far the bones go to their pins. Ctrl+click to type.",
                _ => "How far the bones blend toward the keyed poses. Ctrl+click to type.",
            });
    }

    /// <summary> A symmetry layer's side: the one whose motion the other side copies. </summary>
    private static void DrawFrom(TimelineEditor current, MotionLayer layer)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, "Copy from");
        foreach (var side in new[] { MotionSide.Left, MotionSide.Right })
        {
            ImGui.SameLine(0, Theme.Gap);
            if (ImGui.RadioButton(side == MotionSide.Left ? "Left##from-left" : "Right##from-right", layer.From == side))
                current.Commit(MotionEdits.SetFrom(current.Document, layer.Key, side));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("The side whose motion is copied.");
        }
        ImGui.Spacing();
    }

    private static void DrawComponents(TimelineEditor current, MotionLayer layer)
    {
        TimelineUi.Fact("Changes");
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
        TimelineUi.Fact("Between keys");
        ImGui.SetNextItemWidth(Theme.Scaled(120));
        using (var combo = ImRaii.Combo("##layer-interpolation", TimelineWindowViews.EaseName(layer.Interpolation)))
        {
            if (combo.Success)
            {
                foreach (var mode in TimelineWindowViews.LayerEases)
                {
                    if (ImGui.Selectable(TimelineWindowViews.EaseName(mode), mode == layer.Interpolation) && mode != layer.Interpolation)
                        current.Commit(MotionEdits.Replace(current.Document, layer with { Interpolation = mode }));
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(TimelineWindowViews.EaseHint(mode));
                }
            }
            else if (ComboWheel.Scroll(TimelineWindowViews.LayerEases, layer.Interpolation, out var picked))
                current.Commit(MotionEdits.Replace(current.Document, layer with { Interpolation = picked }));
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("How bones go from one key to the next. A key can have its own way.");
    }

    /// <summary>
    /// The selected key's way to the next key: the layer's, a preset, or its own curve, drawn below with
    /// two handles to drag. Dragging a handle makes the curve the key's own.
    /// </summary>
    private void DrawKeyEase(TimelineEditor current, MotionLayer layer, MotionKey key, bool readOnly)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, current.SelectedBone is { } bone
            ? $"{AnimationPresentation.BoneName(bone)}'s key at {TimelineWindowViews.FrameText(key.Frame)}"
            : $"Key at {TimelineWindowViews.FrameText(key.Frame)}");
        using (ImRaii.Disabled(readOnly))
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("To the next key");
            ImGui.SameLine(0, Theme.Gap);
            ImGui.SetNextItemWidth(Math.Max(Theme.Scaled(120), ImGui.GetContentRegionAvail().X));
            var preview = key.Ease is { } own ? TimelineWindowViews.EaseName(own) : $"The layer's ({TimelineWindowViews.EaseName(layer.Interpolation)})";
            using (var combo = ImRaii.Combo("##key-ease", preview))
            {
                if (combo.Success)
                {
                    if (ImGui.Selectable($"The layer's ({TimelineWindowViews.EaseName(layer.Interpolation)})", key.Ease == null) && key.Ease != null)
                        current.Commit(MotionEdits.SetEase(current.Document, layer.Key, key.Frame, null));
                    foreach (var mode in TimelineWindowViews.LayerEases.Add(MotionInterpolation.Custom))
                    {
                        if (ImGui.Selectable(TimelineWindowViews.EaseName(mode), key.Ease == mode) && key.Ease != mode)
                            current.Commit(MotionEdits.SetEase(current.Document, layer.Key, key.Frame, mode,
                                mode == MotionInterpolation.Custom ? MotionEvaluator.CurveOf(layer, key) ?? MotionCurve.Linear : null));
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip(TimelineWindowViews.EaseHint(mode));
                    }
                }
                else if (ComboWheel.Steps() is var steps and not 0)
                {
                    // The layer's way (null) comes first, as in the list.
                    MotionInterpolation?[] modes = [null, .. TimelineWindowViews.LayerEases, MotionInterpolation.Custom];
                    if (ComboWheel.Pick(modes, key.Ease, steps) is var mode && mode != key.Ease)
                        current.Commit(MotionEdits.SetEase(current.Document, layer.Key, key.Frame, mode,
                            mode == MotionInterpolation.Custom ? MotionEvaluator.CurveOf(layer, key) ?? MotionCurve.Linear : null));
                }
            }
            DrawEaseCurve(current, layer, key, readOnly);
        }
        if (key.Bones.Count > 1 && current.SelectedBone != null)
            Widgets.HintWrapped($"Shared by the {key.Bones.Count} bones in this key. Drag this bone's key to a frame of its own to ease it alone.");
        // The ease shapes the way to each bone's next key; with none after it, it changes nothing.
        var last = !layer.Keys.Any(later => later.Frame > key.Frame && later.Bones.Keys.Any(key.Bones.ContainsKey));
        if (last)
            Widgets.HintWrapped("No key follows for these bones, so this has no effect yet.");
    }

    /// <summary>
    /// The key's curve from its value (bottom left) to the next key's (top right). Space above and below
    /// shows overshoot. A hold draws its jump and has no handles.
    /// </summary>
    private void DrawEaseCurve(TimelineEditor current, MotionLayer layer, MotionKey key, bool readOnly)
    {
        var curve = MotionEvaluator.CurveOf(layer, key);
        var width = Math.Max(Theme.Scaled(120), ImGui.GetContentRegionAvail().X);
        var height = Theme.Scaled(110);
        var origin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##ease-curve", new Vector2(width, height));
        var drawList = ImGui.GetWindowDrawList();
        // Y from -0.25 to 1.25 fills the box, so the curve's 0 to 1 sits in its middle two thirds.
        const float low = -.25f, high = 1.25f;
        var margin = Theme.Scaled(10);
        var plotMin = origin + new Vector2(margin, Theme.Scaled(4));
        var plotMax = origin + new Vector2(width - margin, height - Theme.Scaled(4));
        var plot = plotMax - plotMin;
        Vector2 Screen(float x, float y) => new(plotMin.X + x * plot.X, plotMax.Y - (y - low) / (high - low) * plot.Y);
        Vector2 Curve(Vector2 screen) => new(Math.Clamp((screen.X - plotMin.X) / plot.X, 0, 1),
            Math.Clamp(low + (plotMax.Y - screen.Y) / plot.Y * (high - low), low, high));

        drawList.AddRectFilled(origin, origin + new Vector2(width, height), TimelineUi.U32(Theme.RowAlt), Theme.Scaled(3));
        var guide = TimelineUi.U32(Theme.WithAlpha(Theme.TreeLine, .6f));
        drawList.AddLine(Screen(0, 0), Screen(1, 0), guide);
        drawList.AddLine(Screen(0, 1), Screen(1, 1), guide);
        drawList.AddLine(Screen(0, low), Screen(0, high), guide);
        drawList.AddLine(Screen(1, low), Screen(1, high), guide);
        var stroke = TimelineUi.U32(Theme.Accent);
        var thickness = Theme.Scaled(2);
        if (curve == null)
        {
            drawList.AddLine(Screen(0, 0), Screen(1, 0), stroke, thickness);
            drawList.AddLine(Screen(1, 0), Screen(1, 1), stroke, thickness);
            return;
        }
        var previous = Screen(0, 0);
        for (var step = 1; step <= 48; step++)
        {
            var t = step / 48f;
            var next = Screen(t, curve.Ease(t));
            drawList.AddLine(previous, next, stroke, thickness);
            previous = next;
        }
        var handles = new[] { Screen(curve.X1, curve.Y1), Screen(curve.X2, curve.Y2) };
        var handleLine = TimelineUi.U32(Theme.WithAlpha(Theme.Text, .5f));
        drawList.AddLine(Screen(0, 0), handles[0], handleLine);
        drawList.AddLine(Screen(1, 1), handles[1], handleLine);
        var mouse = ImGui.GetIO().MousePos;
        var reach = Theme.Scaled(9);
        if (ImGui.IsItemActivated() && !readOnly)
        {
            easeHandle = Vector2.Distance(mouse, handles[0]) <= reach ? 0 : Vector2.Distance(mouse, handles[1]) <= reach ? 1 : -1;
            if (easeHandle >= 0)
                current.BeginFieldEdit();
        }
        if (easeHandle >= 0 && ImGui.IsItemActive())
        {
            var point = Curve(mouse);
            var moved = easeHandle == 0 ? curve with { X1 = point.X, Y1 = point.Y } : curve with { X2 = point.X, Y2 = point.Y };
            current.Change(MotionEdits.SetEase(current.Document, layer.Key, key.Frame, MotionInterpolation.Custom, moved));
        }
        if (ImGui.IsItemDeactivated() && easeHandle >= 0)
        {
            easeHandle = -1;
            current.EndFieldEdit();
        }
        for (var index = 0; index < 2; index++)
        {
            var hot = easeHandle == index || easeHandle < 0 && ImGui.IsItemHovered() && Vector2.Distance(mouse, handles[index]) <= reach;
            drawList.AddCircleFilled(handles[index], Theme.Scaled(hot ? 6 : 5), TimelineUi.U32(hot ? Theme.Accent : Theme.Text));
        }
        if (ImGui.IsItemHovered() && easeHandle < 0 && !readOnly)
            ImGui.SetTooltip("Drag a handle to shape how the bones get to the next key.");
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
            ImGui.TextColored(Theme.Hint, layer.Kind == MotionLayerKind.Pin ? "None yet. Pin here adds one." : "None yet. Set a bone's value to add one.");
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
