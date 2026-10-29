using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// A hand shape layer's panel: the hand, shape, Curl, Spread and Keep finger motion as fact rows, then Save hand as shape
/// and the fingertip line. Every change is one undo step. The tip check reads the live character on a task and is polled
/// each frame, so drawing never waits on it; no result, whatever the reason, leaves the layer's tips as they are.
/// </summary>
internal sealed class HandShapePanel(TimelineSession session, BonePosing posing)
{
    private const string SavePopup = "##hand-save";
    private static readonly MotionHandSide[] HandSides = [MotionHandSide.Left, MotionHandSide.Right, MotionHandSide.Both];

    // The tip check: the read running, the clip and character it is for, and the last result (null when it gave none).
    private Task<HandTipFacts>? checking;
    private CancellationTokenSource? cancel;
    private string? checkKey, factsKey;
    private HandTipFacts? facts;
    // The layer, hand and result the tips were last written for: an undo of the write stays undone until one of them changes.
    private (string Layer, MotionHandSide Side, HandTipFacts Facts)? written;

    private bool saveRight = true;
    private string saveName = string.Empty;
    private string? saveError;

    private static void Set(TimelineEditor current, MotionLayer layer, MotionHand hand) =>
        current.Commit(MotionEdits.Replace(current.Document, With(layer, hand)));

    private static MotionLayer With(MotionLayer layer, MotionHand hand) => layer with { Fix = layer.Fix! with { Hand = hand } };

    /// <summary>The Hand, Shape, Curl, Spread and Keep finger motion rows, inside the layer's facts table.</summary>
    public void DrawFields(TimelineEditor current, MotionLayer layer, MotionHand hand)
    {
        TimelineUi.Fact("Hand");
        ImGui.SetNextItemWidth(Math.Max(Theme.Scaled(90), ImGui.GetContentRegionAvail().X));
        using (var combo = ImRaii.Combo("##hand-side", hand.Side.ToString()))
        {
            if (combo.Success)
            {
                foreach (var side in HandSides)
                    if (ImGui.Selectable(side.ToString(), side == hand.Side) && side != hand.Side)
                        Set(current, layer, hand with { Side = side });
            }
            else if (ComboWheel.Scroll(HandSides, hand.Side, out var picked))
                Set(current, layer, hand with { Side = picked });
        }
        TimelineUi.Tip("Which hand takes the shape. A shape you saved from one hand works on the other.");

        DrawShape(current, layer, hand);
        Slider(current, layer, "Curl", "##hand-curl", "Close the fingers more, or open them. Ctrl+click to type.", hand.Curl, curl => hand with { Curl = curl });
        Slider(current, layer, "Spread", "##hand-spread", "Spread the fingers apart, or bring them together. Ctrl+click to type.", hand.Spread, spread => hand with { Spread = spread },
            (int)(HandShapes.MinSpread * 100));

        TimelineUi.Fact(string.Empty);
        var keep = hand.KeepMotion;
        if (ImGui.Checkbox("Keep finger motion##hand-keep", ref keep))
            Set(current, layer, hand with { KeepMotion = keep });
        TimelineUi.Tip("Keep the clip's small finger moves on top of the shape.");
    }

    private void DrawShape(TimelineEditor current, MotionLayer layer, MotionHand hand)
    {
        var library = session.Animations?.HandShapes;
        var saved = library?.Names ?? [];
        // Built-in shapes first (by id), then the saved ones (by name).
        var entries = HandShapes.Order.Select(id => (Shape: id, Saved: false)).Concat(saved.Select(name => (Shape: name, Saved: true))).ToArray();
        bool Current((string Shape, bool Saved) entry) =>
            entry.Saved == (hand.Turns != null) && string.Equals(entry.Shape, hand.Shape, entry.Saved ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        // A saved shape saved again under the layer's name can be picked again to take the new turns.
        bool Stale((string Shape, bool Saved) entry) =>
            entry.Saved && library?.Get(entry.Shape) is { } turns && hand.Turns is { } kept &&
            !(turns.Count == kept.Count && turns.All(turn => kept.TryGetValue(turn.Key, out var same) && same == turn.Value));
        void Pick((string Shape, bool Saved) entry)
        {
            if (!entry.Saved)
                Set(current, layer, hand with { Shape = entry.Shape, Turns = null });
            else if (library?.Get(entry.Shape) is { } turns)
                Set(current, layer, hand with { Shape = entry.Shape, Turns = turns });
        }

        TimelineUi.Fact("Shape");
        ImGui.SetNextItemWidth(Math.Max(Theme.Scaled(140), ImGui.GetContentRegionAvail().X));
        var preview = hand.Turns == null ? HandShapes.BuiltIn.TryGetValue(hand.Shape, out var recipe) ? recipe.Name : hand.Shape
            : hand.Shape + (library?.Get(hand.Shape) == null ? " (kept in this layer)" : string.Empty);
        using (var combo = ImRaii.Combo("##hand-shape", preview, ImGuiComboFlags.HeightLarge))
        {
            if (combo.Success)
            {
                var separated = false;
                foreach (var entry in entries)
                {
                    if (entry.Saved && !separated)
                    {
                        ImGui.Separator();
                        separated = true;
                    }
                    var chosen = Current(entry);
                    var name = entry.Saved ? entry.Shape : HandShapes.BuiltIn[entry.Shape].Name;
                    if (ImGui.Selectable($"{name}###{(entry.Saved ? "saved-" : "built-in-")}{entry.Shape}", chosen) && (!chosen || Stale(entry)))
                        Pick(entry);
                    if (entry.Saved)
                    {
                        using var menu = ImRaii.ContextPopupItem($"##hand-shape-menu-{entry.Shape}");
                        if (menu.Success && ImGui.MenuItem("Delete shape"))
                            library?.Delete(entry.Shape);
                    }
                }
            }
            else if (ComboWheel.Steps() is var steps and not 0 && ComboWheel.Pick(entries, Current, steps) is var next && !Current(next))
                Pick(next);
        }
        TimelineUi.Tip("The fingers' shape. Your saved shapes come after the built-in ones; right-click one to delete it.");
    }

    /// <summary>A percent slider from <paramref name="min"/>% to 100% for a hand value; a drag is one undo step.</summary>
    private static void Slider(TimelineEditor current, MotionLayer layer, string label, string id, string tooltip, float value, Func<float, MotionHand> with,
        int min = -100)
    {
        TimelineUi.Fact(label);
        var percent = Math.Max((int)MathF.Round(value * 100), min);
        ImGui.SetNextItemWidth(Math.Max(Theme.Scaled(90), ImGui.GetContentRegionAvail().X));
        var changed = ImGui.SliderInt(id, ref percent, min, 100, "%d%%");
        LayerPanel.TrackLayer(current, changed, changed ? With(layer, with(Math.Clamp(percent, min, 100) / 100f)) : null);
        TimelineUi.Tip(tooltip);
    }

    /// <summary>Under the facts: Save hand as shape, then the fingertip line with its Re-check.</summary>
    public void Draw(TimelineEditor current, MotionLayer layer, MotionHand hand, AnimationTimelineSource loaded, SkeletonDescription skeleton, bool readOnly)
    {
        DrawSave(current, hand, loaded, skeleton);
        ImGui.Spacing();
        DrawTips(current, layer, hand, loaded, skeleton, readOnly);
    }

    private void DrawSave(TimelineEditor current, MotionHand hand, AnimationTimelineSource loaded, SkeletonDescription skeleton)
    {
        if (session.Animations?.HandShapes is not { } library)
            return;
        if (ImGui.Button("Save hand as shape###hand-save-open"))
        {
            saveRight = hand.Side != MotionHandSide.Left;
            saveName = string.Empty;
            saveError = null;
            ImGui.OpenPopup(SavePopup);
        }
        TimelineUi.Tip("Keep a hand's fingers as they look at the playhead as a shape of your own.");
        using var popup = ImRaii.Popup(SavePopup);
        if (!popup.Success)
            return;
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, "Hand");
        ImGui.SameLine(0, Theme.Gap);
        if (ImGui.RadioButton("Left##hand-save-left", !saveRight))
            saveRight = false;
        ImGui.SameLine(0, Theme.Gap);
        if (ImGui.RadioButton("Right##hand-save-right", saveRight))
            saveRight = true;
        ImGui.SetNextItemWidth(Theme.Scaled(200));
        ImGui.InputTextWithHint("##hand-save-name", "Name", ref saveName, MotionEvaluator.MaxHandShapeName);
        ImGui.SameLine(0, Theme.Gap);
        if (ImGui.Button("Save##hand-save-go"))
        {
            var side = saveRight ? MotionSide.Right : MotionSide.Left;
            if (HandRig.For(skeleton, side) is not { } rig)
                saveError = "This skeleton has no hand to save.";
            else if (posing.LayeredLocal(loaded, current.Document, current.Playhead) is not { } local || local.Length != skeleton.Bones.Length)
                saveError = "The motion is still being read.";
            else if (library.Save(saveName, HandShapes.Capture(skeleton, local, rig), out var error))
                ImGui.CloseCurrentPopup();
            else
                saveError = error;
        }
        if (saveError != null)
            Widgets.HintWrapped(saveError);
    }

    /// <summary>The character the tip check is for: the GPose actor, else none.</summary>
    private string CheckKey(AnimationTimelineSource loaded)
    {
        ushort? actor;
        try { actor = session.Playback.ActorIndex(); }
        catch (InvalidOperationException) { actor = null; }
        return $"{TimelineSession.ClipKey(loaded)}|{actor}";
    }

    private void Start(string key)
    {
        cancel?.Cancel();
        cancel?.Dispose();
        cancel = new CancellationTokenSource();
        checkKey = key;
        var token = cancel.Token;
        var read = session.HandTips!;
        // Off the draw thread: from here the framework hop would run inline and the mesh reads after it in this frame.
        checking = Task.Run(() => read(token), token);
    }

    /// <summary>Takes a finished check as the result for the clip and character it was started for; failure or cancellation is no result.</summary>
    private void Poll()
    {
        if (checking is not { IsCompleted: true } task)
            return;
        checking = null;
        // A check cancelled for leaving its clip or character runs again on coming back.
        var dropped = cancel?.IsCancellationRequested == true;
        cancel?.Dispose();
        cancel = null;
        facts = task.IsCompletedSuccessfully ? task.Result : null;
        // A failure is read here so it isn't reported as unobserved.
        _ = task.Exception;
        factsKey = dropped ? null : checkKey;
    }

    private void DrawTips(TimelineEditor current, MotionLayer layer, MotionHand hand, AnimationTimelineSource loaded, SkeletonDescription skeleton, bool readOnly)
    {
        var key = CheckKey(loaded);
        Poll();
        if (checking != null && checkKey != key)
            cancel?.Cancel();
        if (checking == null && factsKey != key && session.HandTips != null)
            Start(key);

        var known = factsKey == key ? facts : null;
        var usable = known is { Problem: null };
        var sides = TimelineWindowViews.HandSides(hand.Side);
        bool Live(MotionSide side) => !usable || (side == MotionSide.Left ? known!.LiveLeft : known!.LiveRight);
        bool Mesh(MotionSide side) => !usable || (side == MotionSide.Left ? known!.MeshLeft : known!.MeshRight);
        string? Model(MotionSide side) => side == MotionSide.Left ? known?.ModelLeft : known?.ModelRight;
        var opened = session.Fresh(loaded);
        var clipSkeleton = HandTipCheck.SkeletonName(opened.Clip.Resolution?.Selected, opened.Rebuild);
        string? line = null;
        foreach (var side in sides)
        {
            line = HandRig.For(skeleton, side) is { } rig ? HandTipCheck.Reason(rig.HasTips, Live(side), Mesh(side), clipSkeleton, Model(side)) : "The clip's skeleton has no finger bones for this hand.";
            if (line != null)
                break;
        }
        if (line == null && known?.Problem is { } problem)
            line = "Fingertips: " + problem;
        if (line != null)
            Widgets.HintWrapped(line);
        else if (checking != null)
            Widgets.MutedWrapped("Checking fingertips");

        // Tips follow the check for every changed side, one undo step; an undone write stays undone until the hand or the check changes.
        // Read again: a field above may have committed this frame, and the write must not undo it.
        if (usable && !readOnly && current.Gesture == TimelineGesture.None && !current.History.InGesture &&
            MotionEdits.Find(current.Document, layer.Key) is { Fix.Hand: { } now } fresh && now.Side == hand.Side)
        {
            var want = sides.All(side => Live(side) && Mesh(side));
            if (want != now.Tips && !(written is { } last && last.Layer == layer.Key && last.Side == hand.Side && ReferenceEquals(last.Facts, known)))
            {
                written = (layer.Key, hand.Side, known!);
                Set(current, fresh, now with { Tips = want });
            }
        }

        if (session.HandTips != null)
        {
            using (ImRaii.Disabled(checking != null))
            {
                if (ImGui.SmallButton("Re-check###hand-recheck"))
                    factsKey = null;
            }
            TimelineUi.Tip("Check your character's fingertips again, after changing your hands or gear.");
        }
    }
}
