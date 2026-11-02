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
/// The selected bone layer in the side panel: its timing, amount and the parts of a bone it changes, its keys and the
/// selected key's ease, a match's length and loop, and a line on the bones it works on with Pose in Bones window, where
/// its bones are picked and posed. A field's changes until it is left are one undo step, and Live shows each change as it
/// is made.
/// </summary>
internal sealed class LayerPanel(TimelineSession session, MotionDecode decode, BonePosing posing, Window bonesWindow)
{
    // The layer the selection last started on: a newly chosen layer with nothing selected starts on its first bone.
    private string? selectionLayer;
    // The ease curve handle being dragged: 0 or 1, -1 for none.
    private int easeHandle = -1;
    // The open pin bone list's search; only one combo is open at a time.
    private static string pinFilter = string.Empty;
    private readonly HandShapePanel hands = new(session, posing);
    // The summary line of the layer last drawn, kept while its record is the same.
    private (MotionLayer? Layer, string? Text) summaryShown;

    public void Draw(TimelineEditor current, MotionLayer layer, AnimationTimelineSource loaded)
    {
        var readOnly = session.ReadOnly;
        var skeleton = session.EditingSkeleton(loaded);
        if (skeleton != null)
        {
            posing.KeepSelection(skeleton);
            if (selectionLayer != layer.Key)
            {
                selectionLayer = layer.Key;
                if (posing.Selection.Count == 0 && (layer.Keys.SelectMany(key => key.Bones.Keys).FirstOrDefault() ?? layer.Bones.FirstOrDefault() ?? layer.Pins.FirstOrDefault()?.Bone ?? layer.Link?.Bone) is { } first &&
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
        Widgets.HintWrapped(TimelineWindowViews.LayerHint(layer));
        ImGui.Spacing();
        if (layer.Kind == MotionLayerKind.Symmetry)
            using (ImRaii.Disabled(readOnly))
                DrawFrom(current, layer);

        // A match ends on the clip's last frame and its blend is its fade: only its length is set. A close loop is anchored the
        // same way, with a place besides, and has no game fade.
        var match = MotionEdits.IsMatch(layer);
        var close = layer.Fix?.Kind == MotionFixKind.CloseLoop;
        var hand = layer.Fix is { Kind: MotionFixKind.HandShape, Hand: { } shape } ? shape : null;
        var reset = layer.Fix?.Kind == MotionFixKind.ResetRoot;
        using (var facts = ImRaii.Table("##layer-facts", 2, ImGuiTableFlags.SizingFixedFit))
        {
            if (facts.Success)
            {
                ImGui.TableSetupColumn("##fact", ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableSetupColumn("##value", ImGuiTableColumnFlags.WidthStretch);
                using (ImRaii.Disabled(readOnly))
                {
                    if (layer.Markers is { } markers)
                        DrawMarkers(current, layer, markers, loaded);
                    if (match)
                    {
                        DrawBlendOver(current, layer);
                        DrawGameFade(layer, loaded);
                    }
                    else if (close)
                    {
                        DrawBlendOver(current, layer);
                        DrawPlace(current, layer, readOnly);
                    }
                    else if (current.Document.Face is { Still: true })
                        // A still face is one shape: its layers have no span or fades, only how much they change it.
                        Widgets.MutedWrapped("A still face is one shape: keys go on its first frame.");
                    else
                    {
                        FrameField(current, layer, "Starts", "##layer-start", layer.Start, start => layer with { Start = start });
                        FrameField(current, layer, "Ends", "##layer-end", layer.End, end => layer with { End = end });
                        FrameField(current, layer, "Fade in", "##layer-fade-in", layer.FadeIn, fade => layer with { FadeIn = fade });
                        FrameField(current, layer, "Fade out", "##layer-fade-out", layer.FadeOut, fade => layer with { FadeOut = fade });
                    }
                    DrawAmount(current, layer);
                    // A match or a close loop is anchored to the clip's ends, and a still face has no span to weigh over.
                    if (MotionEdits.TakesWeights(layer) && current.Document.Face is not { Still: true })
                        DrawWeight(current, layer);
                    // A hand shape turns finger bones only, a root reset the root; a pin holds a spot and a turn, whatever parts are set, and a look only turns.
                    if (hand != null)
                        hands.DrawFields(current, layer, hand);
                    else if (reset)
                        DrawAxes(current, layer);
                    else if (layer.Kind is not (MotionLayerKind.Pin or MotionLayerKind.LookAt or MotionLayerKind.Link or MotionLayerKind.Spring))
                        DrawComponents(current, layer);
                    if (layer.Kind == MotionLayerKind.Swing)
                        SwingFields.Draw(current, layer);
                    else if (layer.Kind == MotionLayerKind.Smooth)
                        SwingFields.DrawWindow(current, layer);
                    else if (layer.Kind == MotionLayerKind.Noise)
                        SwingFields.DrawNoise(current, layer);
                    else if (layer.Kind == MotionLayerKind.LookAt)
                        LookFields.Draw(current, layer, skeleton);
                    else if (layer.Kind == MotionLayerKind.Repeat)
                        RepeatFields.Draw(current, layer);
                    else if (layer.Kind == MotionLayerKind.Spring)
                        SpringFields.Draw(current, layer);
                    // A look that follows a bone has no keys to go between.
                    if (layer.Kind is not (MotionLayerKind.Amplitude or MotionLayerKind.Symmetry or MotionLayerKind.Fix or MotionLayerKind.Idle or MotionLayerKind.Swing or
                        MotionLayerKind.Smooth or MotionLayerKind.Noise or MotionLayerKind.Link or MotionLayerKind.Repeat or MotionLayerKind.Spring) && layer.Look?.Follow == null)
                        DrawInterpolation(current, layer);
                }
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        if (match)
        {
            DrawReread(layer, loaded, readOnly);
            ImGui.Spacing();
        }
        else if (close)
        {
            // The evaluator skips a close loop on a clip the game doesn't loop. The pin note is in the layer's hint above.
            if (!current.Loops)
            {
                Widgets.HintWrapped("This clip doesn't loop, so this layer does nothing.");
                ImGui.Spacing();
            }
        }
        PollIdle(current);
        // Bones are picked and posed in the Bones window: here a line on them and the button that opens it.
        if (skeleton == null)
        {
            Widgets.HintWrapped(session.Fresh(loaded).Clip.Resolution?.Reason ?? "Choose the skeleton this animation was made for under Source to pick bones.");
            DrawPosing(layer);
        }
        else if (layer.Kind == MotionLayerKind.Idle)
        {
            DrawIdle(current, layer, loaded, skeleton, readOnly);
            ImGui.Spacing();
            DrawPosing(layer);
        }
        else if (hand != null)
        {
            hands.Draw(current, layer, hand, loaded, skeleton, readOnly);
            ImGui.Spacing();
            DrawPosing(layer);
        }
        else if (reset)
        {
            Widgets.MutedWrapped("Changes the root bone only, so the whole body turns with it.");
            DrawPosing(layer);
        }
        else if (layer.Kind is MotionLayerKind.Amplitude or MotionLayerKind.Fix or MotionLayerKind.Smooth or MotionLayerKind.Symmetry or MotionLayerKind.Repeat)
            DrawPosing(layer);
        else if (layer.Kind is MotionLayerKind.Swing or MotionLayerKind.Noise)
        {
            DrawPosing(layer);
            SwingFields.DrawNotes(layer);
        }
        else if (layer.Kind == MotionLayerKind.Spring)
        {
            DrawPosing(layer);
            SpringFields.DrawNotes(layer);
        }
        else if (layer.Kind == MotionLayerKind.LookAt)
        {
            DrawPosing(layer);
            if (layer.Look?.Follow == null)
            {
                ImGui.Spacing();
                DrawLayerKeys(current, layer);
                using (ImRaii.Disabled(readOnly))
                    DrawSpread(current, layer);
                if (current.SelectedKey is { } lookKeyFrame && MotionEdits.KeyIndex(layer, lookKeyFrame) is var lookKeyAt and >= 0)
                {
                    ImGui.Spacing();
                    DrawKeyEase(current, layer, layer.Keys[lookKeyAt], readOnly);
                }
            }
        }
        else if (layer.Kind == MotionLayerKind.Link)
        {
            LinkFields.Draw(current, layer, skeleton, posing.BoneTree(skeleton).Index, session, readOnly);
            ImGui.Spacing();
            DrawPosing(layer);
        }
        else if (layer.Kind == MotionLayerKind.Pin)
        {
            DrawPins(current, layer, skeleton, loaded, readOnly);
            ImGui.Spacing();
            DrawPosing(layer);
            if (layer.Pins.Any(pin => !pin.Planted))
            {
                ImGui.Spacing();
                DrawLayerKeys(current, layer);
                using (ImRaii.Disabled(readOnly))
                    DrawSpread(current, layer);
                if (current.SelectedKey is { } pinKeyFrame && MotionEdits.KeyIndex(layer, pinKeyFrame) is var pinKeyAt and >= 0)
                {
                    ImGui.Spacing();
                    DrawKeyEase(current, layer, layer.Keys[pinKeyAt], readOnly);
                }
            }
        }
        else
        {
            DrawPosing(layer);
            ImGui.Spacing();
            DrawLayerKeys(current, layer);
            using (ImRaii.Disabled(readOnly))
                DrawSpread(current, layer);
            if (current.SelectedKey is { } keyFrame && MotionEdits.KeyIndex(layer, keyFrame) is var keyAt and >= 0)
            {
                ImGui.Spacing();
                DrawKeyEase(current, layer, layer.Keys[keyAt], readOnly);
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
    }

    /// <summary>
    /// One line on the bones the layer works on: how many it is limited to, keys, copies, turns, swings, wobbles, smooths, repeats or springs,
    /// or how many pins it has; for a link the bones it joins, its partner named by <paramref name="nameOf"/>. Null for a hand shape or
    /// a root reset, whose bones are set by what they do.
    /// </summary>
    public static string? Summary(MotionLayer layer, Func<Guid?, string?>? nameOf = null)
    {
        if (layer.Fix?.Kind is MotionFixKind.HandShape or MotionFixKind.ResetRoot)
            return null;
        var bones = layer.Bones.IsDefaultOrEmpty ? 0 : layer.Bones.Distinct().Count();
        return layer.Kind switch
        {
            MotionLayerKind.Amplitude or MotionLayerKind.Fix or MotionLayerKind.Idle => bones == 0 ? "Every bone" : $"Limited to {Count(bones, "bone")}",
            MotionLayerKind.Offset or MotionLayerKind.Pose => Keyed(layer) is var keyed and > 0 ? $"{Count(keyed, "bone")} keyed" : "No bones keyed",
            MotionLayerKind.Symmetry => $"Copies {Count(bones, "bone")}",
            MotionLayerKind.Pin => layer.Pins.IsDefaultOrEmpty ? "No pins" : Count(layer.Pins.Length, "pin"),
            MotionLayerKind.LookAt => $"Turns {Count(bones, "bone")}",
            MotionLayerKind.Noise => $"Wobbles {Count(bones, "bone")}",
            MotionLayerKind.Swing => $"Swings {Count(bones, "bone")}",
            MotionLayerKind.Smooth => bones == 0 ? "Smooths every bone" : $"Smooths {Count(bones, "bone")}",
            MotionLayerKind.Repeat => bones == 0 ? "Repeats every bone" : $"Repeats {Count(bones, "bone")}",
            MotionLayerKind.Spring => $"Springs {Count(bones, "bone")}",
            MotionLayerKind.Link => layer.Link is { } link ? TimelineWindowViews.LinkSummary(link, nameOf ?? (_ => null)) : null,
            _ => null,
        };
    }

    /// <summary> How many bones the layer's keys set, each counted once. </summary>
    private static int Keyed(MotionLayer layer) =>
        layer.Keys.IsDefaultOrEmpty ? 0 : layer.Keys.SelectMany(key => key.Bones.Keys).Distinct().Count();

    /// <summary> "1 bone", "3 bones" or "no bones" for <paramref name="thing"/> "bone". </summary>
    private static string Count(int count, string thing) => count switch
    {
        0 => $"no {thing}s",
        1 => $"1 {thing}",
        _ => $"{count} {thing}s",
    };

    /// <summary> The summary line in muted text, when the layer has one, and Pose in Bones window, which opens the Bones window or brings it to front. </summary>
    private void DrawPosing(MotionLayer layer)
    {
        // A layer's record changes with every edit, so the same one keeps its line.
        if (!ReferenceEquals(summaryShown.Layer, layer))
            summaryShown = (layer, Summary(layer, session.PartnerName));
        if (summaryShown.Text is { } summary)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Muted, summary);
        }
        if (ImGui.Button("Pose in Bones window"))
        {
            bonesWindow.IsOpen = true;
            bonesWindow.BringToFront();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(session.Editor?.Document.Face != null ? "Pick and pose this layer's face bones." : "Pick and pose this layer's bones on a body map.");
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

    /// <summary>
    /// One pin's settings, as fact rows: Holds, Attached to, Bends up to, Bend for a hand or a foot, When, Sensitivity when planted,
    /// and Floor for a planted foot held relative to the character.
    /// </summary>
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
            pin.RelativeTo, relative => pin with { RelativeTo = relative }, true);

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
            pin.Top, highest => pin with { Top = highest }, true);

        // A limb's knee or elbow bends where the solver picks; Bend turns it about the line to the held spot. One undo step per drag.
        if (chain is { Shape: PinChainShape.Limb })
        {
            TimelineUi.Fact("Bend");
            var bend = pin.Bend;
            ImGui.SetNextItemWidth(Math.Max(Theme.Scaled(140), ImGui.GetContentRegionAvail().X));
            var changed = ImGui.SliderFloat("##pin-bend", ref bend, -180, 180, "%.0f°", ImGuiSliderFlags.AlwaysClamp);
            if (ImGui.IsItemActivated())
                current.BeginFieldEdit();
            if (changed)
                current.Change(MotionEdits.SetPin(current.Document, layer.Key, pin with { Bend = Math.Clamp(bend, -180, 180) }));
            if (ImGui.IsItemDeactivated())
                current.EndFieldEdit();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Turns the knee or elbow around the line to the held spot. Ctrl+click to type.");
        }

        PinChoice(current, layer, "When", "##pin-when", "At keys holds the spots you key, such as a hand on a knee. When planted holds the bone wherever the motion rests it, such as feet on the floor.",
            pin.Planted ? "When planted" : "At keys", () => [(false, "At keys"), (true, "When planted")], pin.Planted,
            planted => pin with { Planted = planted });
        if (pin.Planted)
            PinChoice(current, layer, "Sensitivity", "##pin-sensitivity", "How readily the bone counts as down. Strict holds less, Loose more.",
                pin.Sensitivity.ToString(), () => [(PinSensitivity.Normal, "Normal"), (PinSensitivity.Strict, "Strict"), (PinSensitivity.Loose, "Loose")],
                pin.Sensitivity, sensitivity => pin with { Sensitivity = sensitivity });
        if (pin is { Planted: true, RelativeTo: null, Bone: PinChains.FootL or PinChains.FootR })
            PinChoice(current, layer, "Floor", "##pin-floor",
                "On the floor sets the held foot down on the floor and levels it. A foot tipped far, as in a kneel, keeps its angle. With Holds: Position only its height changes.",
                pin.Floor ? "On the floor" : "As the motion has it", () => [(false, "As the motion has it"), (true, "On the floor")], pin.Floor,
                floor => pin with { Floor = floor });
    }

    /// <summary>
    /// A fact row with a combo of <paramref name="options"/>, listed only while open; a pick commits the changed pin.
    /// With <paramref name="search"/> (bone lists) a field above the list filters it.
    /// </summary>
    private static void PinChoice<T>(TimelineEditor current, MotionLayer layer, string label, string id, string tooltip, string preview,
        Func<IEnumerable<(T Value, string Name)>> options, T value, Func<T, MotionPin> with, bool search = false)
    {
        TimelineUi.Fact(label);
        ImGui.SetNextItemWidth(Math.Max(Theme.Scaled(140), ImGui.GetContentRegionAvail().X));
        using (var combo = ImRaii.Combo(id, preview, ImGuiComboFlags.HeightLarge))
        {
            if (combo.Success)
            {
                if (search)
                {
                    ImGui.SetNextItemWidth(-1);
                    if (ImGui.IsWindowAppearing())
                    {
                        pinFilter = string.Empty;
                        ImGui.SetKeyboardFocusHere();
                    }
                    ImGui.InputTextWithHint("##pin-search", "Search bones", ref pinFilter, 64);
                }
                var at = 0;
                foreach (var (option, name) in options())
                {
                    at++;
                    if (search && pinFilter.Length > 0 && !name.Contains(pinFilter, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var chosen = EqualityComparer<T>.Default.Equals(option, value);
                    if (ImGui.Selectable($"{name}##{at}", chosen) && !chosen)
                        current.Commit(MotionEdits.SetPin(current.Document, layer.Key, with(option)));
                }
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(tooltip);
    }

    /// <summary>
    /// The layer's weight over time: a tick that edits it on the layer's row in the timeline (a view setting), and Clear
    /// weight, which deletes every point as one undo step.
    /// </summary>
    private static void DrawWeight(TimelineEditor current, MotionLayer layer)
    {
        TimelineUi.Fact("Weight");
        var on = current.WeightLayer == layer.Key;
        if (ImGui.Checkbox("##layer-weight", ref on))
            current.SetWeightEditing(layer.Key, on);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Edit this layer's weight on its row.");
        ImGui.SameLine(0, Theme.Scaled(10));
        using (ImRaii.Disabled(layer.Weights.IsEmpty))
            if (ImGui.Button("Clear weight"))
                current.Commit(MotionEdits.ClearWeights(current.Document, layer.Key));
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

    /// <summary>
    /// An anchored layer's length: a match's how many frames before the clip's end it starts blending, a close loop's how many
    /// frames it blends over. One undo step per drag.
    /// </summary>
    private static void DrawBlendOver(TimelineEditor current, MotionLayer layer)
    {
        TimelineUi.Fact("Blend over");
        // The stored length, which cuts, stretches and pastes keep as far as the clip allows.
        var frames = layer.End - layer.Start;
        ImGui.SetNextItemWidth(Math.Max(Theme.Scaled(90), ImGui.GetContentRegionAvail().X));
        var changed = ImGui.SliderInt("##fix-length", ref frames, 1, Math.Max(1, current.Document.Length), "%d frames");
        if (ImGui.IsItemActivated())
            current.BeginFieldEdit();
        if (changed)
            current.SetFixLength(layer.Key, frames);
        if (ImGui.IsItemDeactivated())
            current.EndFieldEdit();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(layer.Fix?.Kind == MotionFixKind.CloseLoop ? "How many frames the blend runs over. Ctrl+click to type."
                : "How many frames before the end the blend starts. Ctrl+click to type.");
    }

    /// <summary> A close loop's place: whether it changes the frames before the seam, around it or after it. Each pick is one undo step. </summary>
    private static void DrawPlace(TimelineEditor current, MotionLayer layer, bool readOnly)
    {
        var place = layer.Fix!.Placement;
        TimelineUi.Fact("Place");
        ImGui.SetNextItemWidth(Theme.Scaled(120));
        using (var combo = ImRaii.Combo("##fix-place", TimelineWindowViews.PlacementName(place)))
        {
            if (combo.Success)
            {
                foreach (var option in Enum.GetValues<JoinPlacement>())
                    if (ImGui.Selectable(TimelineWindowViews.PlacementName(option), option == place) && option != place)
                        current.SetFixPlacement(layer.Key, option);
            }
            else if (!readOnly && ComboWheel.Scroll(Enum.GetValues<JoinPlacement>(), place, out var picked))
                current.SetFixPlacement(layer.Key, picked);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Before changes the frames before the loop's end, After the frames after its start. Centered does both.");
    }

    // The game fade being dragged, or read after a drag, for the match it belongs to; the layer has the old one until the read lands.
    private (string Layer, int Frames)? fadeEdit;

    /// <summary>
    /// A match's game fade: how many frames before the startup's end the game starts the loop and fades into it. A new
    /// fade reads the loop again at that frame, one undo step. Disabled where Re-read loop is.
    /// </summary>
    private void DrawGameFade(MotionLayer layer, AnimationTimelineSource loaded)
    {
        var reader = session.LoopPose;
        if (fadeEdit is { } stale && (stale.Layer != layer.Key || !reader.Reading && !ImGui.IsAnyItemActive()))
            fadeEdit = null;
        TimelineUi.Fact("Game fade");
        var frames = fadeEdit?.Frames ?? layer.Fix!.Fade;
        ImGui.SetNextItemWidth(Math.Max(Theme.Scaled(90), ImGui.GetContentRegionAvail().X));
        using (ImRaii.Disabled(reader.Reading || !loaded.Startup || session.Animations == null))
        {
            if (ImGui.SliderInt("##fix-fade", ref frames, 0, LoopFades.Most, "%d frames"))
                fadeEdit = (layer.Key, Math.Clamp(frames, 0, LoopFades.Most));
            if (ImGui.IsItemDeactivatedAfterEdit() && fadeEdit is { } done && done.Frames != layer.Fix!.Fade)
                reader.Read(loaded, layer.Key, done.Frames);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("How many frames before the end the game starts the loop and fades into it. The blend lands on the loop frame shown at the end. Ctrl+click to type.");
    }

    /// <summary> A match's Re-read loop button, whether its game fade was measured, and that it applies last. </summary>
    private void DrawReread(MotionLayer layer, AnimationTimelineSource loaded, bool readOnly)
    {
        Widgets.MutedWrapped("Applies after every other layer.");
        // The evaluator skips a match on a looping clip.
        if (loaded.Clip.IsLoop)
            Widgets.HintWrapped("Only works on a startup that leads into a loop.");
        var reader = session.LoopPose;
        var startup = loaded.Startup && session.Animations != null;
        if (startup)
        {
            if (reader.MeasuredFade(loaded) is not { } measured)
                Widgets.HintWrapped($"Game fade not measured yet, so {LoopFades.Default} frames. Play this emote in game once with this window open.");
            else if (measured == layer.Fix!.Fade)
                Widgets.MutedWrapped("Game fade measured in game.");
            else
            {
                Widgets.HintWrapped($"Measured in game: {measured} frames.");
                using (ImRaii.Disabled(readOnly || reader.Reading))
                {
                    if (ImGui.Button("Use it###fix-measured"))
                        reader.Read(loaded, layer.Key, measured);
                }
            }
        }
        using (ImRaii.Disabled(readOnly || reader.Reading || !startup))
        {
            if (ImGui.Button((reader.Reading ? "Reading the loop" : "Re-read loop") + "###fix-reread"))
                reader.Read(loaded, layer.Key, layer.Fix!.Fade);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(startup ? "Read the loop again, after the loop changed." : TimelineWindowViews.NoLoop);
    }

    private static void DrawAmount(TimelineEditor current, MotionLayer layer)
    {
        var smooth = layer.Fix?.Kind == MotionFixKind.SmoothJitter;
        TimelineUi.Fact(smooth ? "Strength" : layer.Kind switch
        {
            MotionLayerKind.Link => TimelineWindowViews.LinkPullLabel,
            MotionLayerKind.Offset or MotionLayerKind.Idle or MotionLayerKind.Swing or MotionLayerKind.Smooth or MotionLayerKind.Noise or
                MotionLayerKind.LookAt or MotionLayerKind.Repeat or MotionLayerKind.Spring => "Strength",
            MotionLayerKind.Amplitude => "Movement",
            MotionLayerKind.Pin => "Pull",
            _ => "Blend",
        });
        var percent = layer.Amount * 100;
        ImGui.SetNextItemWidth(Math.Max(Theme.Scaled(90), ImGui.GetContentRegionAvail().X));
        var changed = ImGui.SliderFloat("##layer-amount", ref percent, 0, MotionEdits.MaxAmount(layer.Kind) * 100, "%.0f%%");
        TrackLayer(current, changed, changed ? layer with { Amount = percent / 100 } : null);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(layer.Kind switch
            {
                MotionLayerKind.Link => TimelineWindowViews.LinkPullTip,
                MotionLayerKind.Offset => "How much of the keyed offsets applies. Ctrl+click to type.",
                MotionLayerKind.Amplitude => "100% keeps the motion. Less calms it toward the average pose, more exaggerates it. Ctrl+click to type.",
                MotionLayerKind.Symmetry => "How far the bones blend toward the other side's motion. Ctrl+click to type.",
                MotionLayerKind.Pin => "How far the bones go to their pins. Ctrl+click to type.",
                MotionLayerKind.Fix when smooth => "How big a shake it calms. Ctrl+click to type.",
                MotionLayerKind.Fix when layer.Fix?.Kind == MotionFixKind.HandShape => "How far the fingers go to the shape. Ctrl+click to type.",
                MotionLayerKind.Fix when layer.Fix?.Kind == MotionFixKind.ResetRoot => "How far the root turns back. Ctrl+click to type.",
                MotionLayerKind.Fix => "How far the end goes to the loop's first frame. Ctrl+click to type.",
                MotionLayerKind.Idle => "How much of the borrowed motion applies, over the shares below. Ctrl+click to type.",
                MotionLayerKind.Swing => "How much of the swing applies. Ctrl+click to type.",
                MotionLayerKind.Noise => "How much of the wobble applies. Ctrl+click to type.",
                MotionLayerKind.LookAt => "How far the head turns toward the target. Ctrl+click to type.",
                MotionLayerKind.Smooth => "How far the bones go to the smoothed motion. Ctrl+click to type.",
                MotionLayerKind.Repeat => "How far the bones go to the repeated motion. Ctrl+click to type.",
                MotionLayerKind.Spring => "How far the bones go to the sprung motion. Ctrl+click to type.",
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

    /// <summary> A root reset's axes, one checkbox each; a click is one undo step. </summary>
    private static void DrawAxes(TimelineEditor current, MotionLayer layer)
    {
        TimelineUi.Fact("Axes");
        var fix = layer.Fix!;
        var first = true;
        foreach (var (axis, label, tip) in TimelineWindowViews.ResetAxes)
        {
            if (!first)
                ImGui.SameLine(0, Theme.Scaled(10));
            first = false;
            var on = fix.Axes.HasFlag(axis);
            if (ImGui.Checkbox(label + "##axis-" + label, ref on))
                current.Commit(MotionEdits.Replace(current.Document, layer with { Fix = fix with { Axes = on ? fix.Axes | axis : fix.Axes & ~axis } }));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(tip);
        }
    }

    private static void DrawComponents(TimelineEditor current, MotionLayer layer)
    {
        TimelineUi.Fact("Changes");
        PoseComponents[] parts = layer.Kind is MotionLayerKind.Amplitude or MotionLayerKind.Fix or MotionLayerKind.Idle or MotionLayerKind.Swing or MotionLayerKind.Smooth or
                MotionLayerKind.Noise
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

    // The Detail choices in the order they are listed, which is not the enum's.
    private static readonly MarkerDetail[] Details = [MarkerDetail.Fewer, MarkerDetail.Normal, MarkerDetail.More];

    /// <summary>
    /// The Motion keys layer's markers: how closely they follow the motion and how many an edit fades out over. They
    /// are found on the decoded motion, so until it is read the rows give way to its progress.
    /// </summary>
    private void DrawMarkers(TimelineEditor current, MotionLayer layer, MotionMarkers markers, AnimationTimelineSource loaded)
    {
        if (decode.DecodedPose(loaded) == null)
        {
            TimelineUi.Fact("Markers");
            decode.DrawWait();
            return;
        }
        TimelineUi.Fact("Detail");
        ImGui.SetNextItemWidth(Theme.Scaled(120));
        using (var combo = ImRaii.Combo("##marker-detail", markers.Detail.ToString()))
        {
            if (combo.Success)
            {
                foreach (var detail in Details)
                    if (ImGui.Selectable(detail.ToString(), detail == markers.Detail) && detail != markers.Detail)
                        current.Commit(MotionEdits.SetMarkers(current.Document, layer.Key, markers with { Detail = detail }));
            }
            else if (ComboWheel.Scroll(Details, markers.Detail, out var picked))
                current.Commit(MotionEdits.SetMarkers(current.Document, layer.Key, markers with { Detail = picked }));
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("How closely markers follow the motion.");

        TimelineUi.Fact("Reach");
        var reach = markers.Reach;
        ImGui.SetNextItemWidth(Math.Max(Theme.Scaled(90), ImGui.GetContentRegionAvail().X));
        var changed = ImGui.SliderInt("##marker-reach", ref reach, 1, MotionMarkers.MaxReach);
        if (ImGui.IsItemActivated())
            current.BeginFieldEdit();
        if (changed)
            current.Change(MotionEdits.SetMarkers(current.Document, layer.Key, markers with { Reach = reach }));
        if (ImGui.IsItemDeactivated())
            current.EndFieldEdit();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("How many markers an edit fades out over.");
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

    /// <summary>
    /// With keys picked on two frames or more: how many, and Spread over, the frames from the first picked key to the last.
    /// Fewer play them faster, more slower. One undo step per field edit.
    /// </summary>
    private static void DrawSpread(TimelineEditor current, MotionLayer layer)
    {
        var (picks, picked) = current.PickedKeys;
        if (picked.Count < 2)
            return;
        ImGui.Spacing();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, $"{picked.Count} picked · Spread over");
        ImGui.SameLine(0, Theme.Gap);
        var frames = picks[^1].Frame - picks[0].Frame;
        ImGui.SetNextItemWidth(Theme.Scaled(90));
        var changed = ImGui.InputInt("##spread-keys", ref frames, 0, 0);
        if (ImGui.IsItemActivated())
            current.BeginFieldEdit();
        if (changed)
            current.SpreadKeys(Math.Max(1, frames));
        if (ImGui.IsItemDeactivated())
            current.EndFieldEdit();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Frames from the first picked key to the last. Fewer play them faster, more slower; the first stays put.");
        ImGui.SameLine(0, Theme.Gap);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, "frames");
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
            ImGui.TextColored(Theme.Hint, layer.Kind switch
            {
                MotionLayerKind.Pin => "None yet. Pin here in the Bones window adds one.",
                MotionLayerKind.LookAt => "None yet. Move the target with the gizmo.",
                _ => "None yet. Set a bone's value in the Bones window to add one.",
            });
            return;
        }
        var picked = current.PickedKeys.Frames;
        foreach (var key in layer.Keys)
        {
            ImGui.SameLine(0, Theme.Scaled(4));
            if (ImGui.GetContentRegionAvail().X < Theme.Scaled(48))
                ImGui.NewLine();
            var chosen = picked.Contains(key.Frame);
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
    /// An idle-motion layer's motion: the animation it is borrowed from (a seen one, read on a task), then each body
    /// part's share of it, and Pin feet while the weight shift moves the body over them.
    /// </summary>
    private void DrawIdle(TimelineEditor current, MotionLayer layer, AnimationTimelineSource loaded, SkeletonDescription skeleton, bool readOnly)
    {
        var idle = layer.Idle;
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, "Motion from");
        ImGui.SameLine(0, Theme.Gap);
        ImGui.SetNextItemWidth(Math.Max(Theme.Scaled(140), ImGui.GetContentRegionAvail().X));
        using (ImRaii.Disabled(readOnly || idleReading != null))
        using (var combo = ImRaii.Combo("##idle-donor", idleReading != null ? "Reading the motion" : idle?.Donor ?? "Choose an animation", ImGuiComboFlags.HeightLarge))
        {
            if (combo.Success)
            {
                var any = false;
                foreach (var (capture, startup) in PoseSourcePanel.SeenClips(session))
                {
                    any = true;
                    var name = AnimationPresentation.AnimationName(capture, startup);
                    if (ImGui.Selectable($"{name}##{capture.Id}-{startup}"))
                        ReadIdle(loaded, layer.Key, name, async (service, token) =>
                            await service.DecodeMotionAsync(await service.LoadTimelineAsync(capture, startup, token).ConfigureAwait(false), _ => { }, token)
                                .ConfigureAwait(false));
                }
                if (!any)
                {
                    using (ImRaii.Disabled())
                        ImGui.Selectable("No animations seen yet");
                }
                ImGui.Separator();
                ImGui.TextColored(Theme.Muted, "From the game");
                var idles = GameIdles(loaded);
                if (idles is not { IsCompletedSuccessfully: true })
                {
                    using (ImRaii.Disabled())
                        ImGui.Selectable(idles?.IsFaulted == true ? "Couldn't read the game's idles" : idles == null ? "No idles for this race" : "Reading the game's idles");
                }
                else
                {
                    foreach (var game in idles.Result)
                        if (ImGui.Selectable($"{game.Name}##game-{game.GamePath}"))
                            ReadIdle(loaded, layer.Key, game.Name, (service, token) => service.DecodeGameIdleAsync(loaded, game, _ => { }, token));
                }
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("The animation whose motion is borrowed: one played since you started, or one of the game's idles for your race.");
        if (idleError is { } error)
            Widgets.HintWrapped(error);
        if (idle == null)
            return;

        using (ImRaii.Disabled(readOnly))
        using (var table = ImRaii.Table("##idle-shares", 2, ImGuiTableFlags.SizingFixedFit))
        {
            if (table.Success)
            {
                ImGui.TableSetupColumn("##fact", ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableSetupColumn("##value", ImGuiTableColumnFlags.WidthStretch);
                IdleShare(current, layer, idle, IdleGroup.Spine, "Breathing", "The back, chest, neck and head.");
                IdleShare(current, layer, idle, IdleGroup.Arms, "Arms", "Shoulders, arms and fingers. Hands drift with them, so keep it low when they touch something.");
                IdleShare(current, layer, idle, IdleGroup.Balance, "Balance", "The body's weight shift: its slide with the hips and legs, which only works as a whole.");
                if (idle.Turns.Keys.Any(bone => MotionIdles.Group(bone) == IdleGroup.Tail))
                    IdleShare(current, layer, idle, IdleGroup.Tail, "Tail", "The tail's swing. Above 100% swings it further.");
            }
        }
        if (idle.Balance > 0)
        {
            Widgets.HintWrapped("Balance moves the body over the feet. Pin feet after this layer to keep them planted.");
            var feet = TimelineWindowViews.CanPinFeet(skeleton);
            using (ImRaii.Disabled(readOnly || !feet))
            {
                if (ImGui.Button(TimelineWindowViews.PinFeetName + "##idle-pin-feet"))
                    current.AddPinFeet(skeleton);
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(TimelineWindowViews.PinFeetTip(skeleton));
        }
    }

    /// <summary> One body part's share of the idle motion, as a percent slider; a drag is one undo step. </summary>
    private static void IdleShare(TimelineEditor current, MotionLayer layer, MotionIdle idle, IdleGroup group, string label, string tooltip)
    {
        TimelineUi.Fact(label);
        var percent = MotionIdles.Share(idle, group) * 100;
        var most = (group == IdleGroup.Tail ? MotionIdles.MaxTailShare : MotionIdles.MaxShare) * 100;
        ImGui.SetNextItemWidth(Math.Max(Theme.Scaled(90), ImGui.GetContentRegionAvail().X));
        var changed = ImGui.SliderFloat($"##idle-{group}", ref percent, 0, most, "%.0f%%");
        TrackLayer(current, changed, changed ? layer with { Idle = MotionIdles.WithShare(idle, group, Math.Clamp(percent, 0, most) / 100) } : null);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip + " 100% is the motion as borrowed. Ctrl+click to type.");
    }

    // shortcut: the read waits for this panel to be drawn again before it lands; move it to the session if users lose reads by switching tabs.
    private Task<MotionIdle>? idleReading;
    private CancellationTokenSource? idleCancel;
    private string? idleLayer, idleClip, idleError;
    // The game's idles for the race they were listed for; null while the race has none.
    private Task<ImmutableArray<GameIdle>>? gameIdles;
    private string? gameIdlesRace;

    /// <summary>The game's idles for the race of the character playing <paramref name="loaded"/>, read once per race; null for no race.</summary>
    private Task<ImmutableArray<GameIdle>>? GameIdles(AnimationTimelineSource loaded)
    {
        var race = AnimationEditService.CharacterRace(loaded);
        if (race != gameIdlesRace)
        {
            gameIdlesRace = race;
            gameIdles = race != null && session.Animations is { } service ? service.GameIdlesAsync(race, CancellationToken.None) : null;
        }
        return gameIdles;
    }

    /// <summary>Decodes a donor (<paramref name="read"/>) on a task and borrows its motion for <paramref name="layerKey"/>; one read at a time.</summary>
    private void ReadIdle(AnimationTimelineSource loaded, string layerKey, string name, Func<AnimationEditService, CancellationToken, Task<MotionClip>> read)
    {
        if (idleReading != null || session.Animations is not { } service)
            return;
        idleCancel = new CancellationTokenSource();
        var token = idleCancel.Token;
        idleLayer = layerKey;
        idleClip = TimelineSession.ClipKey(loaded);
        idleError = null;
        idleReading = Task.Run(async () =>
        {
            var clip = await read(service, token).ConfigureAwait(false);
            return MotionIdles.Borrow(clip, name);
        }, token);
    }

    /// <summary>A finished read becomes the layer's motion, keeping its shares; it waits while a drag or field edit runs.</summary>
    private void PollIdle(TimelineEditor current)
    {
        if (idleReading is not { IsCompleted: true } task || session.ReadOnly || current.Gesture != TimelineGesture.None || current.History.InGesture)
            return;
        idleReading = null;
        idleCancel?.Dispose();
        idleCancel = null;
        if (task.IsCanceled || session.Source is not { } open || TimelineSession.ClipKey(open) != idleClip ||
            MotionEdits.Find(current.Document, idleLayer!) is not { Kind: MotionLayerKind.Idle } layer)
            return;
        if (task.IsFaulted)
        {
            idleError = "Couldn't borrow its motion: " + TimelineSession.ErrorText(task.Exception);
            return;
        }
        var read = task.Result;
        var kept = layer.Idle is { } old ? read with { Spine = old.Spine, Arms = old.Arms, Balance = old.Balance, Tail = old.Tail } : read;
        current.Commit(MotionEdits.Replace(current.Document, layer with { Idle = kept }));
    }

    /// <summary> A field edit of the layer's own values: begun when the field activates, one undo step when it is left. </summary>
    internal static void TrackLayer(TimelineEditor current, bool changed, MotionLayer? next)
    {
        if (ImGui.IsItemActivated())
            current.BeginFieldEdit();
        if (changed && next != null)
            current.Change(MotionEdits.Replace(current.Document, next));
        if (ImGui.IsItemDeactivated())
            current.EndFieldEdit();
    }
}
