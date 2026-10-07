using System.Collections.Immutable;
using System.Numerics;
using InstantEdit.Models;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui;
using InstantEdit.Ui.Timeline;

/// <summary>Bone layers in the timeline editor: their edits, the angle fields, their rows and hit zones, and dragging them.</summary>
internal static class MotionEditsFixture
{
    private const string Hand = "j_te_l";

    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Adding(check);
        Dragging(check);
        Keys(check);
        Stack(check);
        Angles(check);
        Rows(check);
        Gestures(check);
        BoneRows(check);
        Views(check);
    }

    private static void Views(Action<bool, string> check)
    {
        var doc = Layered(Empty());
        check(TimelineWindowViews.LayerLabel(doc.Motion[0]) == "Offset 1" && TimelineWindowViews.LayerLabel(doc.Motion[1]) == "Amplitude 1 · 150%" &&
              TimelineWindowViews.LayerLabel(doc.Motion[0] with { Enabled = false }) == "Offset 1 · off" &&
              TimelineWindowViews.LayerLabel(doc.Motion[0] with { Amount = .5f }) == "Offset 1 · 50%",
            "a layer's bar names it, with its amount when that isn't full strength and off when it is turned off");
        check(TimelineWindowViews.KeyEaseText(doc.Motion[0], doc.Motion[0].Keys[0]) == "To the next key: Smooth (the layer's)" &&
              TimelineWindowViews.KeyEaseText(doc.Motion[0], doc.Motion[0].Keys[0] with { Ease = MotionInterpolation.EaseIn }) == "To the next key: Slow start" &&
              TimelineWindowViews.BoneKeysText(doc.Motion[0], Hand) == "2 keys in this layer" && !TimelineWindowViews.LayerEases.Contains(MotionInterpolation.Custom),
            "a key's tooltip says how it goes to the next key; a layer offers every way but a key's own curve");
        check(TimelineWindowViews.LayerContent(doc.Motion[0]) == "2 keys · 1 bone" && TimelineWindowViews.LayerContent(doc.Motion[1]) == "Every bone" &&
              TimelineWindowViews.LayerContent(doc.Motion[1] with { Bones = [Hand] }) == "1 bone",
            "a layer's tooltip counts its keys and bones");

        // The game's skeleton has the neck; the clip animates it and the hand.
        static BoneTransform Rest() => new(Vector3.Zero, Quaternion.Identity, Vector3.One);
        var game = new SkeletonDescription("game", "game-print", [new("n_root", -1, 0, Rest()), new("j_kubi", 0, 0, Rest()), new(Hand, 0, 0, Rest())], [], [], []);
        var made = new SkeletonDescription("made", "made-print", [.. game.Bones, new("iv_ko_c_l", 2, 0, Rest()), new("j_custom", 2, 0, Rest())], [], [], []);
        var resource = new AnimationResource("chara/a.pap", "a.pap", "hash");
        var candidate = new SkeletonCandidate(new SkeletonSource(SkeletonSourceKind.Game, resource), made, 0, "");
        var clip = new AnimationClip("chara/a.pap", "loop", 0, 0, 1, "s.sklb",
            Resolution: new SkeletonResolution(SkeletonResolutionState.Matched, [candidate], candidate, TrackBones: [1, 2],
                Standards: [new StandardSkeleton(SkeletonStandard.Vanilla, game, "game")]));
        TimelineDocument Keyed(string bone, MotionLayerKind kind = MotionLayerKind.Offset) => Empty() with
        {
            Motion = [new("m1", kind, "L", 0, 100, 0, 0, kind == MotionLayerKind.Amplitude ? 2 : 1, PoseComponents.All, MotionInterpolation.Smooth,
                kind == MotionLayerKind.Amplitude ? [bone] : [],
                kind == MotionLayerKind.Amplitude ? [] : [new MotionKey(0, ImmutableDictionary<string, BoneTransform>.Empty.Add(bone, Turn(10)))])],
        };
        check(TimelineWindowViews.LayerBonesWarning(Keyed("j_custom"), clip) is { } warning && warning.Contains("j_custom", StringComparison.Ordinal),
            "layers moving a bone outside IVCS + YAS that the clip doesn't animate warn that sync plugins can refuse the file");
        check(TimelineWindowViews.LayerBonesWarning(Keyed("iv_ko_c_l"), clip) == null && TimelineWindowViews.LayerBonesWarning(Keyed("j_kubi"), clip) == null &&
              TimelineWindowViews.LayerBonesWarning(Keyed("j_custom", MotionLayerKind.Amplitude), clip) == null &&
              TimelineWindowViews.LayerBonesWarning(Keyed("j_custom") with { Motion = [Keyed("j_custom").Motion[0] with { Enabled = false }] }, clip) == null &&
              TimelineWindowViews.LayerBonesWarning(Keyed("j_custom"), clip with { Resolution = clip.Resolution! with { Standards = [] } }) == null,
            "standard bones, amplitudes, layers turned off and clips without the game's skeleton don't warn");
    }

    private static TimelineDocument Empty(int length = 100) => new(length, length, null, [], 1);

    private static BoneTransform Turn(float degrees) =>
        new(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, degrees * MathF.PI / 180), Vector3.One);

    private static MotionKey Key(int frame, float degrees) =>
        new(frame, ImmutableDictionary<string, BoneTransform>.Empty.Add(Hand, Turn(degrees)));

    /// <summary>An offset from 20 to 60 fading 8 frames each way with keys at 30 and 50, then an amplitude over the whole clip.</summary>
    private static TimelineDocument Layered(TimelineDocument doc) => doc with
    {
        Motion =
        [
            new("m1", MotionLayerKind.Offset, "Offset 1", 20, 60, 8, 8, 1, PoseComponents.All, MotionInterpolation.Smooth, [], [Key(30, 10), Key(50, 20)]),
            new("m2", MotionLayerKind.Amplitude, "Amplitude 1", 0, 100, 0, 0, 1.5f, PoseComponents.Rotation, MotionInterpolation.Smooth),
        ],
        NextMotionKey = 3,
    };

    private static void Adding(Action<bool, string> check)
    {
        var whole = MotionEdits.Add(Empty(), MotionLayerKind.Offset, 0, 100, out var first, out var error);
        var layer = MotionEdits.Find(whole, first)!;
        check(first == "m1" && error == null && layer is { Name: "Offset 1", Start: 0, End: 100, FadeIn: 0, FadeOut: 0, Amount: 1, Components: PoseComponents.All } &&
              whole.NextMotionKey == 2,
            "a layer over the whole clip has no fades, so a loop stays seamless");
        var section = MotionEdits.Add(whole, MotionLayerKind.Offset, 40, 70, out var second, out _);
        check(MotionEdits.Find(section, second) is { Name: "Offset 2", FadeIn: 7, FadeOut: 7 } && section.Motion.Length == 2 && section.Motion[^1].Key == second,
            "a section eases over a quarter of itself, is numbered per kind and applies after the others");
        var longer = MotionEdits.Add(Empty(1000), MotionLayerKind.Offset, 100, 400, out var third, out _);
        check(MotionEdits.Find(longer, third) is { FadeIn: MotionEdits.SectionFade, FadeOut: MotionEdits.SectionFade },
            "a long section's fades stop at a third of a second");
        var amplitude = MotionEdits.Add(Empty(), MotionLayerKind.Amplitude, 0, 100, out var amp, out _);
        var pose = MotionEdits.Add(Empty(), MotionLayerKind.Pose, 0, 100, out var posed, out _);
        check(MotionEdits.Find(amplitude, amp) is { Amount: 1.5f, Components: PoseComponents.Rotation | PoseComponents.Position } &&
              MotionEdits.Find(pose, posed) is { Amount: 1, Components: PoseComponents.Rotation | PoseComponents.Position },
            "an amplitude starts at 150% and a pose at full strength; both turn and move, scale only on request");
        var full = Empty() with { Motion = [.. Enumerable.Range(0, MotionEvaluator.MaxLayers).Select(i => layer with { Key = "m" + i })] };
        var refused = MotionEdits.Add(full, MotionLayerKind.Offset, 0, 10, out _, out var tooMany);
        check(ReferenceEquals(refused, full) && tooMany != null, "a layer past the most an animation may have is refused with a reason");
        check(MotionEdits.Find(MotionEdits.Add(Empty(), MotionLayerKind.Offset, 50, 50, out var thin, out _), thin) is { Start: 50, End: 51 },
            "a layer is always at least a frame long");
    }

    private static void Dragging(Action<bool, string> check)
    {
        var doc = Layered(Empty());
        var moved = MotionEdits.Move(doc, "m1", 5);
        check(MotionEdits.Find(moved, "m1") is { Start: 25, End: 65 } layer && layer.Keys.Select(k => k.Frame).SequenceEqual([35, 55]),
            "moving a layer moves its keys with it");
        check(MotionEdits.Find(MotionEdits.Move(doc, "m1", -50), "m1") is { Start: 0, End: 40 } &&
              MotionEdits.Find(MotionEdits.Move(doc, "m1", 90), "m1") is { Start: 60, End: 100 },
            "a layer moves no further than the start or the end of the clip");
        check(ReferenceEquals(MotionEdits.Move(doc, "m1", 0), doc) && ReferenceEquals(MotionEdits.Move(doc, "missing", 3), doc),
            "a move that changes nothing keeps the same document");
        var shrunk = MotionEdits.Resize(doc, "m1", true, 35);
        check(MotionEdits.Find(shrunk, "m1") is { Start: 55, End: 60, FadeIn: 5, FadeOut: 0 },
            "a resize keeps a layer at least a frame long and shrinks its fades to fit");
        check(MotionEdits.Find(MotionEdits.Resize(doc, "m1", false, 100), "m1") is { End: 100 } &&
              MotionEdits.Find(MotionEdits.Resize(doc, "m1", true, -100), "m1") is { Start: 0 },
            "an edge stops at the clip's ends");
        check(MotionEdits.Find(MotionEdits.Fade(doc, "m1", true, 4), "m1") is { FadeIn: 12 } &&
              MotionEdits.Find(MotionEdits.Fade(doc, "m1", false, 3), "m1") is { FadeOut: 5 } &&
              MotionEdits.Find(MotionEdits.Fade(doc, "m1", true, 100), "m1") is { FadeIn: 32, FadeOut: 8 },
            "a fade's handle drags its fade, and both fades together never outlast the layer");
    }

    private static void Keys(Action<bool, string> check)
    {
        var doc = Layered(Empty());
        var moved = MotionEdits.MoveKey(doc, "m1", 30, 100, out var landed);
        check(landed == 49 && MotionEdits.Find(moved, "m1")!.Keys.Select(k => k.Frame).SequenceEqual([49, 50]),
            "a key moves no further than the frame before its neighbour");
        MotionEdits.MoveKey(doc, "m1", 30, -100, out var lowest);
        check(lowest == 0, "the first key may move to the start of the timeline");
        var added = MotionEdits.SetKey(doc, "m1", 40, Hand, Turn(15));
        check(MotionEdits.Find(added, "m1")!.Keys.Select(k => k.Frame).SequenceEqual([30, 40, 50]), "a value at a frame without a key adds one in order");
        var updated = MotionEdits.SetKey(doc, "m1", 30, Hand, Turn(5));
        check(MotionEdits.Find(updated, "m1")!.Keys[0].Bones[Hand] == Turn(5) && ReferenceEquals(MotionEdits.SetKey(doc, "m1", 30, Hand, Turn(10)), doc),
            "a value at a key's frame replaces that key's value, and the same value changes nothing");
        var emptied = MotionEdits.SetKey(doc, "m1", 30, Hand, null);
        check(MotionEdits.Find(emptied, "m1")!.Keys.Select(k => k.Frame).SequenceEqual([50]), "taking a key's last bone out removes the key");
        var marked = MotionEdits.SetKey(doc, "m1", 30, Hand, Turn(10), moved: true);
        check(MotionEdits.Find(marked, "m1")!.Keys[0].Moved.SequenceEqual([Hand]) &&
              MotionEdits.Find(MotionEdits.SetKey(marked, "m1", 30, Hand, Turn(10), moved: false), "m1")!.Keys[0].Moved.IsEmpty,
            "a pose key marks and unmarks the bones whose position it sets");
        check(MotionEdits.Find(MotionEdits.ClearBone(doc, "m1", Hand), "m1")!.Keys.IsEmpty &&
              MotionEdits.Find(MotionEdits.DeleteKey(doc, "m1", 50), "m1")!.Keys.Select(k => k.Frame).SequenceEqual([30]),
            "a bone can be cleared from every key, and one key deleted");
    }

    private static void Stack(Action<bool, string> check)
    {
        var doc = Layered(Empty());
        var reordered = MotionEdits.Reorder(doc, "m2", -1);
        check(reordered.Motion.Select(l => l.Key).SequenceEqual(["m2", "m1"]) && ReferenceEquals(MotionEdits.Reorder(doc, "m2", 1), doc),
            "a layer moves up and down the stack, never past either end");
        var copied = MotionEdits.Duplicate(doc, "m1", out var copy);
        check(copy == "m3" && copied.Motion.Select(l => l.Key).SequenceEqual(["m1", "m3", "m2"]) &&
              MotionEdits.Find(copied, copy) is { Name: "Offset 1 copy", Start: 20, End: 60 } dup && dup.Keys.Length == 2 && copied.NextMotionKey == 4,
            "a copy goes right after its layer, keys and all, with a new key");
        check(MotionEdits.Find(MotionEdits.SetEnabled(doc, "m1", false), "m1") is { Enabled: false } &&
              MotionEdits.Find(MotionEdits.SetBones(doc, "m2", [Hand, Hand, ""]), "m2")!.Bones.SequenceEqual([Hand]),
            "layers turn off, and a bone limit keeps each named bone once");
        var replaced = MotionEdits.Replace(doc, doc.Motion[1] with { Amount = 9, FadeIn = 80, FadeOut = 80, Name = "  Bigger  ", Kind = MotionLayerKind.Pose });
        check(MotionEdits.Find(replaced, "m2") is { Amount: MotionEvaluator.MaxAmount, FadeIn: 80, FadeOut: 20, Name: "Bigger", Kind: MotionLayerKind.Amplitude },
            "a replacement keeps the layer's kind and an amount, fades and name it can use");
        check(MotionEdits.Find(MotionEdits.Replace(doc, doc.Motion[0] with { Kind = MotionLayerKind.Pose, Amount = 3 }), "m1") is { Kind: MotionLayerKind.Offset, Amount: 3 } &&
              MotionEdits.Find(MotionEdits.Replace(Layered(Empty()) with { Motion = [doc.Motion[0] with { Kind = MotionLayerKind.Pose }] },
                  doc.Motion[0] with { Kind = MotionLayerKind.Pose, Amount = 3 }), "m1") is { Amount: 1 },
            "a pose blends at most fully; every other kind goes to 400%");
        check(ReferenceEquals(MotionEdits.Replace(doc, doc.Motion[0] with { }), doc) && MotionEdits.Same(doc.Motion[0], doc.Motion[0] with { Keys = [Key(30, 10), Key(50, 20)] }),
            "an unchanged replacement keeps the document; layers compare by content");
    }

    private static void Angles(Action<bool, string> check)
    {
        Vector3[] samples = [new(10, 20, 30), new(-45, 60, 170), new(0, 0, 0), new(90, -30, -90), new(179, 1, -179)];
        check(samples.All(angles =>
                  {
                      var back = MotionAngles.ToEuler(MotionAngles.FromEuler(angles));
                      var wrapped = MotionAngles.Wrap(back - angles);
                      return MathF.Abs(wrapped.X) < .01f && MathF.Abs(wrapped.Y) < .01f && MathF.Abs(wrapped.Z) < .01f;
                  }),
            "angles turn into a rotation and back");
        var rotations = new[]
        {
            Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(1, 2, 3)), 1.2f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2),
            Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(-3, .5f, 2)), -2.5f),
        };
        check(rotations.All(q => 1 - MathF.Abs(Quaternion.Dot(MotionAngles.FromEuler(MotionAngles.ToEuler(q)), q)) < 1e-5f),
            "a rotation turns into angles and back, gimbal lock at 90 degrees included");
        check(Vector3.Distance(Vector3.Transform(Vector3.UnitY, MotionAngles.FromEuler(new Vector3(0, 0, 90))), -Vector3.UnitX) < 1e-5f,
            "each angle turns about its own axis");
        check(MotionAngles.Wrap(new Vector3(359, -181, 540)) == new Vector3(-1, 179, 180), "angles wrap into -180 to 180");
    }

    // A 100-frame document with a sound marker; fitted to 440 track pixels: 4 pixels per frame, frame 0 at x 100.
    // Rows: Motion 0, Expression 1, LipSync 2, Sound 3, Effect 4, Prop 5, then the layers in 6 and 7.
    private static readonly TimelineGeometry Geometry = new(Legend: 100, Ruler: 24, Row: 20, Edge: 5, Marker: 9);

    private static Vector2 At(float frame, int row, float dy = 10) => new(100 + frame * 4, 24 + row * 20 + dy);

    private static TimelineEditor Editor()
    {
        var doc = Layered(Empty() with { Events = [new("t1", "C063", TimelineLane.Sound, 50, 0, false, "sound/a.scd", TimelineAccess.Editable, null, false, ImmutableDictionary<string, double>.Empty)] });
        var editor = new TimelineEditor(doc);
        editor.SetWidth(440);
        return editor;
    }

    private static void Rows(Action<bool, string> check)
    {
        var editor = Editor();
        var layout = editor.Layout;
        check(layout.MotionFirstRow == 6 && layout.MotionRows == 2 && layout.LayerIndexAt(6) == 0 && layout.LayerIndexAt(7) == 1 && layout.RowCount == 8,
            "each bone layer gets a row below the event lanes, in the order they apply");
        check(TimelineLayout.Build(Empty() with { Expression = true }).MotionRows == 0, "an expression's own timeline has no bone layer rows");
        TimelineHit Hit(Vector2 point) => editor.HitTest(Geometry, point);
        check(Hit(At(40, 6)) is { Part: TimelinePart.LayerBody, Layer: "m1" } &&
              Hit(At(20, 6)) is { Part: TimelinePart.LayerLeft, Layer: "m1" } &&
              Hit(At(60, 6)) is { Part: TimelinePart.LayerRight, Layer: "m1" },
            "a layer's bar moves it and its ends resize it");
        check(Hit(At(30, 6)) is { Part: TimelinePart.LayerKey, Layer: "m1", KeyFrame: 30 } && Hit(At(50.5f, 6)) is { Part: TimelinePart.LayerKey, KeyFrame: 50 },
            "a key's diamond grabs the key");
        check(Hit(At(28, 6, dy: 3)) is { Part: TimelinePart.LayerFadeIn, Layer: "m1" } && Hit(At(52, 6, dy: 3)) is { Part: TimelinePart.LayerFadeOut, Layer: "m1" } &&
              Hit(At(40, 6, dy: 3)) is { Part: TimelinePart.LayerBody },
            "the fade handles sit on the bar's top edge where the fades end");
        check(Hit(At(80, 6)) is { Part: TimelinePart.MotionLane } && Hit(new Vector2(50, 24 + 7 * 20 + 10)) is { Part: TimelinePart.Legend, Layer: "m2" } &&
              Hit(At(10, 8)) is { Part: TimelinePart.None },
            "empty space in a layer row is the bone lane, its legend names the layer, below the rows is nothing");
        var none = new TimelineEditor(Empty());
        none.SetWidth(440);
        check(none.HitTest(Geometry, At(10, 6)) is { Part: TimelinePart.MotionLane, Layer: null }, "with no layers one empty bone row takes the first");
    }

    private static void BoneRows(Action<bool, string> check)
    {
        const string Arm = "j_ude_a_l";
        // m1: keys at 30 (hand and arm), 40 (arm) and 50 (hand).
        var doc = Layered(Empty());
        doc = MotionEdits.SetKey(doc, "m1", 30, Arm, Turn(5));
        doc = MotionEdits.SetKey(doc, "m1", 40, Arm, Turn(15));
        var order = new Dictionary<string, int>(StringComparer.Ordinal) { [Arm] = 1, [Hand] = 2 };
        check(MotionEdits.KeyBones(doc.Motion[0]).SequenceEqual([Hand, Arm]) && MotionEdits.KeyBones(doc.Motion[0], order).SequenceEqual([Arm, Hand]),
            "a layer's bone rows list its keyed bones in the skeleton's order, by name while that is unknown");

        static int[] Frames(TimelineDocument doc) => [.. doc.Motion[0].Keys.Select(key => key.Frame)];
        static string[] BonesAt(TimelineDocument doc, int frame) => [.. doc.Motion[0].Keys.Single(key => key.Frame == frame).Bones.Keys.Order(StringComparer.Ordinal)];
        var moved = MotionEdits.MoveBoneKey(doc, "m1", Hand, 30, 5, out var landed);
        check(landed == 35 && Frames(moved).SequenceEqual([30, 35, 40, 50]) && BonesAt(moved, 30).SequenceEqual([Arm]) && BonesAt(moved, 35).SequenceEqual([Hand]),
            "moving one bone's key leaves the rest of the key where it was");
        var joined = MotionEdits.MoveBoneKey(doc, "m1", Hand, 30, 10, out landed);
        check(landed == 40 && Frames(joined).SequenceEqual([30, 40, 50]) && BonesAt(joined, 40).SequenceEqual([Hand, Arm]),
            "a bone's key joins the key it lands on");
        MotionEdits.MoveBoneKey(doc, "m1", Hand, 30, 40, out landed);
        check(landed == 49, "a bone's key never passes that bone's next key");
        var held = MotionEdits.SetEase(doc, "m1", 50, MotionInterpolation.Step);
        var alone = MotionEdits.MoveBoneKey(held, "m1", Hand, 50, -5, out landed);
        check(landed == 45 && Frames(alone).SequenceEqual([30, 40, 45]) && alone.Motion[0].Keys[2].Ease == MotionInterpolation.Step,
            "a key left empty goes, and a bone moved to a frame of its own keeps its key's ease");

        var eased = MotionEdits.SetEase(doc, "m1", 30, MotionInterpolation.Custom, new MotionCurve(2, 0, .5f, 3));
        check(eased.Motion[0].Keys[0] is { Ease: MotionInterpolation.Custom, Curve: { X1: 1, Y2: MotionCurve.MaxY } },
            "a key's own curve keeps its handles in range");
        check(ReferenceEquals(MotionEdits.SetEase(eased, "m1", 30, MotionInterpolation.Custom, eased.Motion[0].Keys[0].Curve), eased) &&
              MotionEdits.SetEase(eased, "m1", 30, null).Motion[0].Keys[0] is { Ease: null, Curve: null } &&
              !MotionEdits.Same(eased.Motion[0], doc.Motion[0]),
            "setting the same ease changes nothing, the layer's way drops the curve, and an ease counts as a change");

        var editor = new TimelineEditor(doc);
        editor.SetWidth(440);
        var geometry = Geometry with { Unfold = 16 };
        var legendY = (int row) => 24 + row * 20 + 10f;
        check(!editor.IsUnfolded("m1") && editor.Layout.RowCount == 8, "layers start folded");
        check(editor.HitTest(geometry, new Vector2(92, legendY(6))) is { Part: TimelinePart.LayerUnfold, Layer: "m1" } &&
              editor.HitTest(geometry, new Vector2(92, legendY(7))) is { Part: TimelinePart.Legend, Layer: "m2" } &&
              editor.HitTest(Geometry, new Vector2(92, legendY(6))) is { Part: TimelinePart.Legend },
            "a layer with keys has an arrow at the right of its name; an amplitude has none");
        editor.Press(editor.HitTest(geometry, new Vector2(92, legendY(6))));
        editor.Release();
        var layout = editor.Layout;
        check(editor.IsUnfolded("m1") && layout.RowCount == 10 && layout.BoneAt(7) == (0, Hand) && layout.BoneAt(8) == (0, Arm) &&
              layout.LayerIndexAt(9) == 1 && layout.LayerRow(1) == 9 && layout.BoneRows(0) == 2 && layout.BoneRows(1) == 0 && layout.LayerIndexAt(7) == -1,
            "unfolding adds a row per bone under the layer and moves the next layer down");
        check(editor.History.UndoCount == 0, "folding is no edit");
        editor.BoneOrder = order;
        check(editor.Layout.BoneAt(7) == (0, Arm) && editor.Layout.BoneAt(8) == (0, Hand), "the bone rows follow the skeleton's order once it is known");
        check(editor.HitTest(geometry, At(40, 7)) is { Part: TimelinePart.BoneKey, Layer: "m1", Bone: Arm, KeyFrame: 40 } &&
              editor.HitTest(geometry, At(45, 7)) is { Part: TimelinePart.BoneLane, Layer: "m1", Bone: Arm } &&
              editor.HitTest(geometry, At(40, 8)) is { Part: TimelinePart.BoneLane, Bone: Hand } &&
              editor.HitTest(geometry, new Vector2(50, legendY(8))) is { Part: TimelinePart.Legend, Layer: "m1", Bone: Hand },
            "a bone's row grabs only that bone's keys, and its name names the bone");

        editor.Press(editor.HitTest(geometry, At(30, 8)));
        check(editor is { SelectedLayer: "m1", SelectedKey: 30, SelectedBone: Hand, Gesture: TimelineGesture.BoneKey, Playhead: 30 },
            "pressing a bone's key selects that bone's part of the key and moves the playhead there");
        editor.Drag(36, alt: true);
        editor.Release();
        check(Frames(editor.Document).SequenceEqual([30, 36, 40, 50]) && BonesAt(editor.Document, 36).SequenceEqual([Hand]) &&
              editor.SelectedKey == 36 && editor.SelectedBone == Hand && editor.History.UndoCount == 1,
            "dragging it moves that bone's value alone, as one undo step");
        editor.Key(TimelineKey.Right, alt: true);
        check(editor.SelectedKey == 37 && BonesAt(editor.Document, 37).SequenceEqual([Hand]), "Alt+arrows nudge the bone's key alone");
        editor.Key(TimelineKey.Delete);
        check(Frames(editor.Document).SequenceEqual([30, 40, 50]) && BonesAt(editor.Document, 30).SequenceEqual([Arm]) && editor.SelectedKey == null,
            "Delete takes out only the bone's value");
        editor.Press(editor.HitTest(geometry, At(30, 6)));
        editor.Release();
        check(editor is { SelectedKey: 30, SelectedBone: null }, "pressing the layer's key selects the whole key");
        editor.Undo();
        editor.Undo();
        editor.Undo();
        check(Frames(editor.Document).SequenceEqual([30, 40, 50]) && BonesAt(editor.Document, 30).SequenceEqual([Hand, Arm]), "undo puts the bone's key back");
        editor.SelectLayer("m1");
        editor.Key(TimelineKey.Delete);
        check(!editor.IsUnfolded("m1") && editor.Layout.LayerIndexAt(6) == 0 && editor.Layout.RowCount == 7, "a deleted layer's bone rows go with it");
    }

    private static void Gestures(Action<bool, string> check)
    {
        var editor = Editor();
        var hit = editor.HitTest(Geometry, At(40, 6));
        editor.Press(hit);
        check(editor.SelectedLayer == "m1" && editor.SelectedKey == null && editor.Selected.Count == 0 && editor.Gesture == TimelineGesture.LayerMove,
            "pressing a layer selects it and starts moving it");
        editor.Drag(45, alt: true);
        editor.Release();
        check(editor.SelectedLayerValue is { Start: 25, End: 65 } && editor.History.UndoCount == 1, "dragging a layer moves it as one undo step");
        editor.Undo();
        check(editor.SelectedLayerValue is { Start: 20, End: 60 }, "undo puts it back");

        editor.Press(editor.HitTest(Geometry, At(20, 6)));
        editor.Drag(15, alt: true);
        editor.Release();
        // The fade in now ends at 15 + 8.
        editor.Press(editor.HitTest(Geometry, At(23, 6, dy: 3)));
        editor.Drag(27, alt: true);
        editor.Release();
        check(editor.SelectedLayerValue is { Start: 15, FadeIn: 12 }, "an edge drag resizes the layer and a handle drag sets its fade");

        editor.Press(editor.HitTest(Geometry, At(30, 6)));
        check(editor.SelectedKey == 30 && editor.Playhead == 30 && editor.Gesture == TimelineGesture.LayerKey, "pressing a key selects it and moves the playhead there");
        editor.Drag(33, alt: true);
        check(editor.SelectedKey == 33, "a dragged key stays selected where it lands");
        editor.Key(TimelineKey.Escape);
        check(editor.SelectedKey == 30 && editor.SelectedLayerValue!.Keys[0].Frame == 30, "Escape puts a dragged key back");

        editor.Press(editor.HitTest(Geometry, At(30, 6)));
        editor.Release();
        editor.Key(TimelineKey.Right, alt: true);
        check(editor.SelectedKey == 31 && editor.SelectedLayerValue!.Keys[0].Frame == 31, "Alt+arrows nudge the selected key");
        editor.Key(TimelineKey.Delete);
        check(editor.SelectedLayerValue!.Keys.Length == 1 && editor.SelectedKey == null, "Delete takes out the selected key first");
        editor.Key(TimelineKey.Right, alt: true);
        check(editor.SelectedLayerValue is { Start: 16 }, "with no key selected, Alt+arrows nudge the layer");
        editor.Key(TimelineKey.Duplicate);
        check(editor.SelectedLayer == "m3" && editor.Document.Motion.Length == 3, "Ctrl+D copies the selected layer and selects the copy");
        editor.Key(TimelineKey.Delete);
        check(editor.Document.Motion.Length == 2 && editor.SelectedLayer == null, "Delete with no key selected takes out the layer");

        editor.Press(editor.HitTest(Geometry, At(50, 3)));
        check(editor.Selected.Contains("t1") && editor.SelectedLayer == null, "selecting an event clears the layer selection");
        editor.Release();
        editor.Press(editor.HitTest(Geometry, new Vector2(50, 24 + 6 * 20 + 10)));
        editor.Release();
        check(editor.SelectedLayer == "m1" && editor.Selected.Count == 0, "clicking a layer's name selects it and clears the event selection");

        check(editor.AddLayer(MotionLayerKind.Pose, 0, 100) && editor.SelectedLayerValue is { Name: "Pose 1", Kind: MotionLayerKind.Pose },
            "adding a layer selects it");
        var before = editor.History.UndoCount;
        var layer = editor.SelectedLayerValue!;
        editor.BeginFieldEdit();
        editor.Change(MotionEdits.Replace(editor.Document, layer with { Amount = .5f }));
        editor.Change(MotionEdits.Replace(editor.Document, layer with { Amount = .25f }));
        editor.EndFieldEdit();
        check(editor.SelectedLayerValue is { Amount: .25f } && editor.History.UndoCount == before + 1, "a field's changes until it is left are one undo step");
        editor.Undo();
        editor.Undo();
        check(editor.SelectedLayer == null && editor.Document.Motion.Length == 2, "undoing the add drops the selection of the layer it removed");
    }
}
