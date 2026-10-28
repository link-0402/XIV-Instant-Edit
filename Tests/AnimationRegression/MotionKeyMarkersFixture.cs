using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json.Nodes;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui.Timeline;

/// <summary>
/// The Motion keys layer: a managed offset layer whose keys mark the motion's own key poses. Its data, validation,
/// journal and the edits that add, copy and paste it.
/// </summary>
internal static class MotionKeyMarkersFixture
{
    private const string B1 = "j_b1", B2 = "j_b2";

    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Data(check, reject);
        Detection(check);
        Poses(check);
        Reach(check, reject);
        Search(check);
        Editor(check);
        Layout(check);
    }

    private static void Layout(Action<bool, string> check)
    {
        var turn = T(Vector3.Zero, Q(Vector3.UnitZ, 20));
        var order = new Dictionary<string, int> { [B1] = 0, [B2] = 1 };
        var empty = MotionEdits.AddMotionKeys(Empty(60), out var key, out _);
        var one = MotionEdits.SetKey(empty, key, 10, B1, turn);
        var open = new HashSet<string> { key };
        string[] Rows(TimelineLayout layout) =>
            [.. Enumerable.Range(1, layout.BoneRows(0)).Select(at => layout.BoneAt(layout.LayerRow(0) + at)!.Value.Bone)];

        var rows = TimelineLayout.Build(one, null, null, open, order, [B2, B1]);
        check(Rows(rows).SequenceEqual([B1, B2]), "layout: the key bones first, then the other selected bones");
        var late = TimelineLayout.Build(one, null, null, open, new Dictionary<string, int> { [B2] = 0, [B1] = 1 }, [B2, B1]);
        check(Rows(late).SequenceEqual([B1, B2]), "layout: the key bones stay first whatever the bone order");
        var unknown = TimelineLayout.Build(empty, null, null, open, order, ["x", B2, "a", B1]);
        check(Rows(unknown).SequenceEqual([B1, B2, "x", "a"]), "layout: selected bones the order doesn't know go last, as given");
        var none = TimelineLayout.Build(empty, null, null, open, order, [B2, B1]);
        check(TimelineLayout.Unfolds(empty.Motion[0]) && Rows(none).SequenceEqual([B1, B2]), "layout: with no keys the layer still unfolds into the selected bones");
        check(Rows(TimelineLayout.Build(empty, null, null, open, order)).Length == 0 && Rows(TimelineLayout.Build(empty, null, null, new HashSet<string>(), order, [B1])).Length == 0,
            "layout: no selected bones or a folded layer show no rows");

        var plain = MotionEdits.Add(Empty(60), MotionLayerKind.Offset, 0, 60, out var plainKey, out _);
        plain = MotionEdits.SetKey(plain, plainKey, 10, B1, turn);
        var ordinary = TimelineLayout.Build(plain, null, null, new HashSet<string> { plainKey }, order, [B2, B1]);
        check(Rows(ordinary).SequenceEqual([B1]) && !TimelineLayout.Unfolds(MotionEdits.Add(Empty(60), MotionLayerKind.Offset, 0, 60, out _, out _).Motion[0]),
            "layout: an ordinary offset layer ignores the selected bones");

        var set = new MotionMarkerSet(new Dictionary<string, ImmutableArray<int>> { [B2] = [0, 30, 60] }, [], 60, false, MarkerDetail.Normal);
        var geometry = new TimelineGeometry(Legend: 100, Ruler: 24, Row: 20, Edge: 5, Marker: 9);
        var view = new TimelineView(0, 4, 400);
        TimelineHit Hit(TimelineDocument doc, string layerKey, string bone, float frame, MotionMarkerSet? markers, IReadOnlyList<string> bones)
        {
            var layout = TimelineLayout.Build(doc, null, null, new HashSet<string> { layerKey }, order, bones);
            var row = Enumerable.Range(layout.MotionFirstRow, layout.MotionRows).First(at => layout.BoneAt(at) is { Bone: var name } && name == bone);
            return geometry.HitTest(doc, layout, view, new Vector2(geometry.Legend + view.X(frame), geometry.RowTop(row) + 10), false, markers);
        }
        var marker = Hit(empty, key, B2, 30, set, [B2]);
        check(marker is { Part: TimelinePart.BoneMarker, KeyFrame: 30, Layer: var layerKey, Bone: B2 } && layerKey == key && !marker.OnKey && !marker.OnLayer,
            "hit: a marker on a Motion keys bone row, not a key or a layer part");
        check(Hit(empty, key, B2, 31.25f, set, [B2]) is { Part: TimelinePart.BoneMarker, KeyFrame: 30 } && Hit(empty, key, B2, 31.5f, set, [B2]) is { Part: TimelinePart.BoneLane },
            "hit: the marker's reach is half a marker and a pixel");
        check(Hit(empty, key, B2, 20, set, [B2]) is { Part: TimelinePart.BoneLane }, "hit: between markers is the empty row");
        var keyed = MotionEdits.SetKey(empty, key, 30, B2, turn);
        check(Hit(keyed, key, B2, 30, set, [B2]) is { Part: TimelinePart.BoneKey, KeyFrame: 30 }, "hit: a key wins over the marker under it");
        check(Hit(empty, key, B2, 30, null, [B2]) is { Part: TimelinePart.BoneLane }, "hit: no markers, no marker hit");
        check(Hit(empty, key, B2, 30, new MotionMarkerSet(new Dictionary<string, ImmutableArray<int>> { [B1] = [30] }, [], 60, false, MarkerDetail.Normal), [B2]) is { Part: TimelinePart.BoneLane },
            "hit: another bone's markers don't count");
        var other = Hit(plain, plainKey, B1, 20, new MotionMarkerSet(new Dictionary<string, ImmutableArray<int>> { [B1] = [20] }, [], 60, false, MarkerDetail.Normal), []);
        check(other is { Part: TimelinePart.BoneLane }, "hit: an ordinary layer's rows have no marker hits");

        check(TimelineClipBand.KeyPoseText(new KeyPose(12, 5), false) == "Frame 12: 5 bones key here. Click to edit motion keys." &&
              TimelineClipBand.KeyPoseText(new KeyPose(12, 1), false) == "Frame 12: 1 bone keys here. Click to edit motion keys." &&
              TimelineClipBand.KeyPoseText(new KeyPose(0, 0), false) == "Frame 0. Click to edit motion keys." &&
              TimelineClipBand.KeyPoseText(new KeyPose(12, 5), true) == "Frame 12: 5 bones key here. Click to go there.",
            "key pose tooltip: one bone, no bones, read-only");
    }

    private static void Search(Action<bool, string> check)
    {
        var clip = Clip(61, 60, Swing);
        var doc = Empty(60);
        var starts = 0;
        var inline = new MarkerSearch { Start = action => { starts++; action(); return Task.CompletedTask; } };
        var first = inline.Find("decode", clip, doc, MarkerDetail.Normal, gesture: false);
        check(first is { Detail: MarkerDetail.Normal, Length: 60 } && first.Of(B1).Length > 2 && starts == 1, "search: finds the markers once");
        check(ReferenceEquals(inline.Find("decode", clip, doc, MarkerDetail.Normal, gesture: false), first) && starts == 1, "search: the same key starts nothing");
        var layered = MotionEdits.AddMotionKeys(doc, out _, out _);
        check(ReferenceEquals(inline.Find("decode", clip, layered, MarkerDetail.Normal, gesture: false), first) && starts == 1, "search: layers don't change the key");
        check(inline.Find("decode", clip, doc, MarkerDetail.More, gesture: true) == null && ReferenceEquals(inline.Last("decode"), first) && starts == 1,
            "search: a gesture starts nothing, the last set is only shown");
        check(inline.Find("other", clip, doc, MarkerDetail.More, gesture: true) == null && inline.Last("other") == null, "search: the last set stands only for its decode");

        var queued = new List<Action>();
        var held = new MarkerSearch { Start = action => { queued.Add(action); return Task.CompletedTask; } };
        check(held.Find("decode", clip, doc, MarkerDetail.Normal, gesture: false) == null && queued.Count == 1, "search: none while the first search runs");
        var seen = held.Token;
        check(held.Find("decode", clip, doc, MarkerDetail.More, gesture: false) == null && queued.Count == 2 && seen.IsCancellationRequested,
            "search: a changed detail starts again and cancels the old search");
        queued[0]();
        queued[1]();
        var more = held.Find("decode", clip, doc, MarkerDetail.More, gesture: false);
        check(more is { Detail: MarkerDetail.More } && queued.Count == 2, "search: the cancelled search publishes nothing, the new one its set");
        check(held.Find("decode", clip, doc, MarkerDetail.Normal, gesture: false) == null && queued.Count == 3 && held.Find("decode", clip, doc, MarkerDetail.Normal, gesture: false) == null &&
              ReferenceEquals(held.Last("decode"), more), "search: a changed detail has no markers until its search finishes, the last set is only shown");
        queued[2]();
        var normal = held.Find("decode", clip, doc, MarkerDetail.Normal, gesture: false);
        var cut = doc with { Cuts = MotionCuts.WholeOf(60) };
        check(normal is { Detail: MarkerDetail.Normal } && held.Find("decode", clip, cut, MarkerDetail.Normal, gesture: false) == null && queued.Count == 4 &&
              ReferenceEquals(held.Last("decode"), normal), "search: changed cuts at the same length have no markers until their search finishes, the last set is only shown");
        queued[3]();
        check(held.Find("decode", clip, cut, MarkerDetail.Normal, gesture: false) is { } recut && !ReferenceEquals(recut, normal) && ReferenceEquals(held.Last("decode"), recut),
            "search: the changed cuts' own set once found");

        check(MarkerSearch.KeyOf("decode", doc, MarkerDetail.Normal) != MarkerSearch.KeyOf("decode", cut, MarkerDetail.Normal) &&
              MarkerSearch.KeyOf("decode", cut, MarkerDetail.Normal) != MarkerSearch.KeyOf("decode", cut with { Cuts = MotionCuts.WholeOf(60) }, MarkerDetail.Normal) &&
              MarkerSearch.KeyOf("decode", doc, MarkerDetail.Normal) != MarkerSearch.KeyOf("decode", doc with { Length = 61 }, MarkerDetail.Normal) &&
              MarkerSearch.KeyOf("decode", doc, MarkerDetail.Normal) != MarkerSearch.KeyOf("other", doc, MarkerDetail.Normal) &&
              MarkerSearch.KeyOf("decode", doc, MarkerDetail.Normal) == MarkerSearch.KeyOf("decode", layered, MarkerDetail.Normal),
            "search: the key is the decode, the cuts, the length and the detail");
    }

    private static void Editor(Action<bool, string> check)
    {
        var set = new MotionMarkerSet(new Dictionary<string, ImmutableArray<int>> { [B1] = [0, 10, 20, 30, 40, 60] }, [], 60, false, MarkerDetail.Normal);
        var turn = T(Vector3.Zero, Q(Vector3.UnitZ, 20));
        var turn2 = T(Vector3.Zero, Q(Vector3.UnitZ, 40));
        List<int> Frames(TimelineDocument doc) => [.. MotionEdits.MotionKeysLayer(doc)!.Keys.Where(k => k.Bones.ContainsKey(B1)).Select(k => k.Frame)];

        var editor = new TimelineEditor(Empty(60)) { Markers = set };
        check(ReferenceEquals(editor.History.Base, editor.Document), "history base: the current document outside a gesture");
        check(editor.EditMotionKeys(25) && editor.SelectedLayer == MotionEdits.MotionKeysLayer(editor.Document)!.Key && editor.Playhead == 25 &&
              editor.IsUnfolded(editor.SelectedLayer!) && editor.History.UndoCount == 1, "edit motion keys: adds, selects, moves the playhead");
        var count = editor.Document.Motion.Length;
        check(editor.EditMotionKeys(40) && editor.Document.Motion.Length == count && editor.Playhead == 40 && editor.History.UndoCount == 1, "edit motion keys: reuses the layer, no undo step");
        var key = editor.SelectedLayer!;
        editor.Change(MotionEdits.SetEnabled(editor.Document, key, false));
        check(editor.EditMotionKeys(10) && !MotionEdits.Find(editor.Document, key)!.Enabled, "edit motion keys: a muted layer stays muted");

        var start = editor.Document;
        editor.BeginFieldEdit();
        editor.Change(editor.WithReach(MotionEdits.SetKey(editor.Document, key, 25, B1, turn)));
        check(ReferenceEquals(editor.History.Base, start), "history base: the document the gesture began from");
        editor.Change(editor.WithReach(MotionEdits.SetKey(editor.Document, key, 25, B1, turn2)));
        editor.EndFieldEdit();
        check(Frames(editor.Document).SequenceEqual([20, 25, 30]) && editor.Undo() && ReferenceEquals(editor.Document, start), "drag: zero keys added once and kept through previews, one undo step");
        editor.Redo();
        var rebuilt = editor.Document;
        editor.BeginFieldEdit();
        editor.Change(editor.WithReach(MotionEdits.SetKey(rebuilt, key, 50, B1, turn)));
        editor.Change(editor.WithReach(MotionEdits.SetKey(rebuilt, key, 50, B1, turn2)));
        editor.EndFieldEdit();
        check(Frames(editor.Document).SequenceEqual([20, 25, 30, 40, 50, 60]), "drag: a preview rebuilt from the gesture's start keeps its zero keys");

        editor.Markers = new MotionMarkerSet(set.Bones, [], 50, false, MarkerDetail.Normal);
        var stale = MotionEdits.SetKey(editor.Document, key, 45, B1, turn);
        check(ReferenceEquals(editor.WithReach(stale), stale), "stale markers: no zero keys");
        editor.Markers = null;
        check(ReferenceEquals(editor.WithReach(stale), stale), "no markers: no zero keys");

        var again = new TimelineEditor(Empty(60)) { Markers = set };
        again.EditMotionKeys(0);
        var againKey = again.SelectedLayer!;
        var from = again.Document;
        var zero = MotionTransform.Identity.ToBone();
        void Drag(int frame, BoneTransform value)
        {
            again.BeginFieldEdit();
            again.Change(again.WithReach(MotionEdits.SetKey(again.Document, againKey, frame, B1, value)));
            again.EndFieldEdit();
        }
        Drag(25, turn);
        var firstDrag = Frames(again.Document);
        var undone = again.Undo() && ReferenceEquals(again.Document, from);
        Drag(25, turn);
        var secondDrag = Frames(again.Document);
        undone &= again.Undo() && ReferenceEquals(again.Document, from);
        // The first drags' reach filled frame 20; a later drag from the same start writing a zero key there reaches out on its own.
        Drag(20, zero);
        check(undone && firstDrag.SequenceEqual([20, 25, 30]) && secondDrag.SequenceEqual(firstDrag) && Frames(again.Document).SequenceEqual([10, 20, 30]),
            "drag, undo, drag again from the same start: the zero keys come as on the first drag");

        var retouch = new TimelineEditor(Empty(60)) { Markers = set };
        retouch.EditMotionKeys(0);
        var retouchKey = retouch.SelectedLayer!;
        var trialBase = retouch.Document;
        retouch.Commit(retouch.WithReach(MotionEdits.SetKey(retouch.Document, retouchKey, 25, B1, turn)));
        var keyed = retouch.Document;
        retouch.BeginFieldEdit();
        retouch.Change(retouch.WithReach(MotionEdits.SetKey(retouch.Document, retouchKey, 20, B1, turn)));
        retouch.Change(retouch.WithReach(MotionEdits.SetKey(retouch.Document, retouchKey, 20, B1, turn2)));
        retouch.EndFieldEdit();
        check(Frames(retouch.Document).SequenceEqual([10, 20, 25, 30]) && retouch.Undo() && ReferenceEquals(retouch.Document, keyed),
            "drag on an earlier zero key: it fades out over its own markers, once, one undo step");

        // A pose pick replacing the last one keys onto the document from before it, so it reaches out from there.
        var repick = MotionEdits.SetKey(trialBase, retouchKey, 25, B1, turn);
        check(Frames(retouch.WithReach(trialBase, repick)).SequenceEqual([20, 25, 30]) && Frames(retouch.WithReach(repick)).SequenceEqual([25]),
            "pose pick: reaches out from the replaced pick's base, not the shown document");

        editor.MarkerBones = ["a"];
        var stamp = editor.Layout;
        editor.MarkerBones = new List<string> { "a" };
        check(ReferenceEquals(stamp, editor.Layout), "marker bones: same bones keep the layout");
        editor.MarkerBones = ["a", "b"];
        check(!ReferenceEquals(stamp, editor.Layout) && editor.MarkerBones.SequenceEqual(["a", "b"]), "marker bones: other bones rebuild the layout");
        var live = new List<string> { "c" };
        editor.MarkerBones = live;
        live.Add("d");
        check(editor.MarkerBones.SequenceEqual(["c"]), "marker bones: a copy is kept");

        var expression = new TimelineEditor(Empty(60) with { Expression = true });
        check(!expression.EditMotionKeys(5) && expression.LastError != null && expression.Document.Motion.IsEmpty, "edit motion keys: refused on an expression timeline");
    }

    private static void Reach(Action<bool, string> check, Action<Action, string> reject)
    {
        var set = new MotionMarkerSet(new Dictionary<string, ImmutableArray<int>> { [B1] = [0, 10, 20, 30, 40, 60] }, [], 60, false, MarkerDetail.Normal);
        var before = MotionEdits.AddMotionKeys(Empty(60), out var key, out _);
        var turn = T(Vector3.Zero, Q(Vector3.UnitZ, 20));
        var turn2 = T(Vector3.Zero, Q(Vector3.UnitZ, 40));
        var after = MotionEdits.SetKey(before, key, 25, B1, turn);
        List<int> Frames(TimelineDocument doc, string bone = B1) =>
            [.. MotionEdits.MotionKeysLayer(doc)!.Keys.Where(k => k.Bones.ContainsKey(bone)).Select(k => k.Frame)];
        TimelineDocument WithReachOf(TimelineDocument doc, int n) => MotionEdits.SetMarkers(doc, key, new MotionMarkers(MarkerDetail.Normal, n));

        check(Frames(MotionEdits.WithReach(before, after, set)).SequenceEqual([20, 25, 30]), "reach 1: zero keys at the next markers");
        check(Frames(MotionEdits.WithReach(before, WithReachOf(after, 3), set)).SequenceEqual([0, 25, 60]), "reach 3: three markers each side, ends when short");
        var tweak = MotionEdits.SetKey(MotionEdits.WithReach(before, after, set), key, 25, B1, turn2);
        check(ReferenceEquals(MotionEdits.WithReach(MotionEdits.WithReach(before, after, set), tweak, set), tweak), "reach: a second write at the same key adds nothing");
        var blocked = MotionEdits.SetKey(before, key, 28, B1, turn); // an earlier edit closer in
        check(Frames(MotionEdits.WithReach(blocked, MotionEdits.SetKey(blocked, key, 25, B1, turn), set)).SequenceEqual([20, 25, 28]), "reach: a nearer key bounds the change");
        check(Frames(MotionEdits.WithReach(before, MotionEdits.SetKey(before, key, 0, B1, turn), set)).SequenceEqual([0, 10]), "reach: no zero key before frame 0");
        check(Frames(MotionEdits.WithReach(before, MotionEdits.SetKey(before, key, 60, B1, turn), set)).SequenceEqual([40, 60]), "reach: no zero key after the end");
        var loop = new MotionMarkerSet(new Dictionary<string, ImmutableArray<int>> { [B1] = [0, 10, 50, 60] }, [], 60, true, MarkerDetail.Normal);
        check(Frames(MotionEdits.WithReach(before, MotionEdits.SetKey(before, key, 55, B1, turn), loop)).SequenceEqual([0, 50, 55]), "reach: wraps past the loop's end to frame 0");
        var ends = new MotionMarkerSet(new Dictionary<string, ImmutableArray<int>> { [B1] = [0, 60] }, [], 60, true, MarkerDetail.Normal);
        var sparse = WithReachOf(MotionEdits.SetKey(before, key, 30, B1, turn), 3);
        check(Frames(MotionEdits.WithReach(before, sparse, ends)).SequenceEqual([0, 30]), "reach: a loop with only its ends keys frame 0 once and never the edited frame again");
        check(Frames(MotionEdits.WithReach(before, MotionEdits.SetKey(before, key, 60, B1, turn), loop)).SequenceEqual([10, 50, 60]),
            "reach: an edit at a loop's end fades out as one at frame 0, not to frame 0");
        var trimmed = before with { Motion = before.Motion.SetItem(0, before.Motion[0] with { End = 55 }) };
        check(Frames(MotionEdits.WithReach(trimmed, MotionEdits.SetKey(trimmed, key, 52, B1, turn), loop)).SequenceEqual([50, 52, 55]),
            "reach: a trimmed layer on a loop doesn't wrap, and its zero keys stay inside it");
        var late = before with { Motion = before.Motion.SetItem(0, before.Motion[0] with { Start = 5 }) };
        check(Frames(MotionEdits.WithReach(late, MotionEdits.SetKey(late, key, 8, B1, turn), set)).SequenceEqual([5, 8, 10]), "reach: a zero key past the layer's start goes at its start");
        var first = MotionEdits.WithReach(before, after, set);
        check(Frames(MotionEdits.WithReach(first, MotionEdits.SetKey(first, key, 20, B1, turn), set)).SequenceEqual([10, 20, 25, 30]),
            "reach: editing an earlier zero key fades it out over its own markers");
        check(ReferenceEquals(MotionEdits.WithReach(before, after, new MotionMarkerSet(set.Bones, [], 50, false, MarkerDetail.Normal)), after), "reach: stale markers add nothing");
        check(Frames(MotionEdits.WithReach(before, MotionEdits.SetKey(before, key, 25, "still", turn), set), "still").SequenceEqual([0, 25, 60]), "reach: a still bone fades out at the ends");
        var plain = MotionEdits.Add(Empty(60), MotionLayerKind.Offset, 0, 60, out var plainKey, out _);
        var plainAfter = MotionEdits.SetKey(plain, plainKey, 25, B1, turn);
        check(ReferenceEquals(MotionEdits.WithReach(plain, plainAfter, set), plainAfter), "reach: other offset layers are untouched");
    }

    private static void Detection(Action<bool, string> check)
    {
        var clip = Clip(61, 60, Swing);
        check(MotionKeyMarkers.Tolerance(MarkerDetail.Normal) == (0.5f, 0.001f, 0.005f) && MotionKeyMarkers.Tolerance(MarkerDetail.Fewer) == (2f, 0.005f, 0.02f) &&
            MotionKeyMarkers.Tolerance(MarkerDetail.More) == (0.1f, 0.0002f, 0.001f), "markers: tolerances");
        var normal = MotionKeyMarkers.Find(clip, 60, MarkerDetail.Normal);
        check(!normal.Bones.ContainsKey("n_root") && normal.Of("n_root").IsEmpty, "markers: a still bone has none");
        check(normal.Of(B2).SequenceEqual([0, 60]), "markers: a steady turn keeps only its ends");
        check(Near(normal.Of(B1), 15) && Near(normal.Of(B1), 45) && normal.Of(B1)[0] == 0 && normal.Of(B1)[^1] == 60, "markers: a swing's turning points");
        var fewer = MotionKeyMarkers.Find(clip, 60, MarkerDetail.Fewer);
        var more = MotionKeyMarkers.Find(clip, 60, MarkerDetail.More);
        check(fewer.Of(B1).All(normal.Of(B1).Contains) && normal.Of(B1).All(more.Of(B1).Contains) && more.Of(B1).Length > fewer.Of(B1).Length, "markers: finer keeps coarser");
        // b1 turns and moves together along one line, but its sample 9 turns 2.4 degrees (the worst at Fewer) and its sample 10 moves 5.5 mm
        // (the worst at Normal): picking each level's worst alone would leave Normal without Fewer's marker 9.
        var mixed = Clip(12, 11, (i, _) => [T(Vector3.Zero), T(Vector3.UnitX + new Vector3(.00055f * i, 0, 0), Q(Vector3.UnitZ, i switch { 9 => 2.4f, 11 => 0, _ => .22f * i })), T(Vector3.UnitY)]);
        mixed.Set(11, 1, MotionTransform.From(T(Vector3.UnitX)));
        var (mixedFewer, mixedNormal, mixedMore) = (MotionKeyMarkers.Find(mixed, 11, MarkerDetail.Fewer).Of(B1), MotionKeyMarkers.Find(mixed, 11, MarkerDetail.Normal).Of(B1),
            MotionKeyMarkers.Find(mixed, 11, MarkerDetail.More).Of(B1));
        check(mixedFewer.Contains(9) && mixedFewer.All(mixedNormal.Contains) && mixedNormal.All(mixedMore.Contains), "markers: finer keeps coarser for a bone that turns and moves");
        var stretched =MotionKeyMarkers.Find(Clip(31, 60, Swing), 60, MarkerDetail.More);
        check(stretched.Of(B1).SequenceEqual(stretched.Of(B1).Distinct().Order()) && stretched.Of(B1).All(f => f is >= 0 and <= 60), "markers: whole frames on a stretched clip");
        var cancelled = false;
        try { MotionKeyMarkers.Find(clip, 60, MarkerDetail.Normal, new CancellationToken(true)); } catch (OperationCanceledException) { cancelled = true; }
        check(cancelled, "markers: a cancelled search stops");
        // b2 turns steadily by only 0.15 degrees: the straight line between its ends matches it, so rounding near no turn must not split it.
        var tiny = MotionKeyMarkers.Find(Clip(61, 60, (_, frame) => [T(Vector3.Zero), T(Vector3.UnitX), T(Vector3.UnitY, Q(Vector3.UnitX, .15f * frame / 60))]), 60, MarkerDetail.More);
        check(tiny.Of(B2).SequenceEqual([0, 60]), "markers: a tiny steady turn keeps only its ends at More");
        // The same turn with its stored rotations a few float steps off unit length, as a decode can leave them.
        var drift = MotionKeyMarkers.Find(Clip(61, 60, (_, frame) => [T(Vector3.Zero), T(Vector3.UnitX),
            T(Vector3.UnitY, Q(Vector3.UnitX, .15f * frame / 60) * .999998f)]), 60, MarkerDetail.More);
        check(drift.Of(B2).SequenceEqual([0, 60]), "markers: rotations a hair off unit length don't read as a turn");
        check(normal.Poses.Select(p => p.Frame).First() == 0 && normal.Poses[^1].Frame == 60 && normal.Loops == clip.Loops && normal.Length == 60, "markers: the set's ends, loop and length");
    }

    private static void Poses(Action<bool, string> check)
    {
        var bones = new Dictionary<string, ImmutableArray<int>>
        {
            ["A"] = [0, 10, 30], ["B"] = [0, 10, 30], ["C"] = [0, 10, 30], ["D"] = [0, 11, 30],
            ["E"] = [0, 20, 30], ["F"] = [0, 30], ["G"] = [0, 30], ["H"] = [0, 30],
        };
        var poses = MotionKeyMarkers.Poses(bones, 30);
        check(poses.Select(p => p.Frame).SequenceEqual([0, 10, 30]) && poses[1].Bones == 4, "key poses: quarter threshold, merged within two frames");
        check(MotionKeyMarkers.Poses(new Dictionary<string, ImmutableArray<int>>(), 30).SequenceEqual([new KeyPose(0, 0), new KeyPose(30, 0)]), "key poses: ends only for a still clip");
    }

    /// <summary>n_root stays still, b1 swings 30 degrees about Z once over 60 frames, b2 turns steadily to 90 degrees about X.</summary>
    private static BoneTransform[] Swing(int sample, float frame) =>
    [
        T(Vector3.Zero),
        T(Vector3.UnitX, Q(Vector3.UnitZ, 30 * MathF.Sin(2 * MathF.PI * frame / 60))),
        T(Vector3.UnitY, Q(Vector3.UnitX, 90 * frame / 60)),
    ];

    private static bool Near(ImmutableArray<int> frames, int frame) => frames.Any(f => Math.Abs(f - frame) <= 1);

    private static SkeletonDescription Rig() => new("marker-rig", "marker-rig-fingerprint",
        [new("n_root", -1, 0, T(Vector3.Zero)), new(B1, 0, 0, T(Vector3.UnitX)), new(B2, 0, 0, T(Vector3.UnitY))], [], [], []);

    private static MotionClip Clip(int samples, float frames, Func<int, float, BoneTransform[]> pose, bool loops = false)
    {
        var poses = Enumerable.Range(0, samples).Select(i => pose(i, frames * i / (samples - 1))).ToArray();
        var made = MotionClip.FromPoses(Rig(), frames / TimelineDocument.Fps, poses);
        var clip = new MotionClip(Rig(), made.Duration, made.Samples) { Loops = loops };
        clip.CopyFrom(made);
        return clip;
    }

    private static Quaternion Q(Vector3 axis, float degrees) => Quaternion.CreateFromAxisAngle(axis, degrees * MathF.PI / 180);

    private static BoneTransform T(Vector3 position, Quaternion? rotation = null) => new(position, rotation ?? Quaternion.Identity, Vector3.One);

    private static void Data(Action<bool, string> check, Action<Action, string> reject)
    {
        var doc = MotionEdits.Add(Empty(60), MotionLayerKind.Offset, 0, 60, out var other, out _);
        var added = MotionEdits.AddMotionKeys(doc, out var key, out var error);
        var layer = MotionEdits.MotionKeysLayer(added)!;
        check(error == null && added.Motion[0].Key == key && layer.Key == key, "motion keys: added as the first layer");
        check(layer is { Kind: MotionLayerKind.Offset, Start: 0, End: 60, FadeIn: 0, FadeOut: 0, Amount: 1f, Components: PoseComponents.All,
            Interpolation: MotionInterpolation.Smooth, Name: "Motion keys", Markers: { Detail: MarkerDetail.Normal, Reach: 1 } }, "motion keys: its settings");
        var again = MotionEdits.AddMotionKeys(added, out var key2, out var error2);
        check(ReferenceEquals(again, added) && key2 == key && error2 == null, "motion keys: an existing layer is reused");
        var muted = MotionEdits.SetEnabled(added, key, false);
        check(ReferenceEquals(MotionEdits.AddMotionKeys(muted, out var key3, out _), muted) && key3 == key, "motion keys: a muted layer is reused, still muted");
        MotionEdits.AddMotionKeys(Empty(60) with { Expression = true }, out _, out var exprError);
        check(exprError != null, "motion keys: refused on an expression timeline");
        var dup = MotionEdits.Duplicate(added, key, out var copy);
        check(MotionEdits.Find(dup, copy)!.Markers == null && MotionEdits.MotionKeysLayer(dup)!.Key == key, "motion keys: a duplicate is an ordinary offset");
        var pasted = MotionEdits.Paste(Empty(60), [layer], 60, out var created, out _);
        check(MotionEdits.Find(pasted, created[0])!.Markers == null, "motion keys: a pasted copy is an ordinary offset");
        var set = MotionEdits.SetMarkers(added, key, new MotionMarkers(MarkerDetail.More, 9));
        check(MotionEdits.MotionKeysLayer(set)!.Markers == new MotionMarkers(MarkerDetail.More, 3), "motion keys: reach clamps to 3");
        check(ReferenceEquals(MotionEdits.SetMarkers(added, other, new MotionMarkers()), added), "motion keys: SetMarkers ignores other layers");
        check(ReferenceEquals(MotionEdits.SetMarkers(added, key, new MotionMarkers()), added), "motion keys: SetMarkers with equal settings is unchanged");
        check(!MotionEdits.Same(layer, layer with { Markers = new MotionMarkers(MarkerDetail.More) }) && !MotionEdits.Same(layer, layer with { Markers = null }) &&
              MotionEdits.Same(layer, layer with { Markers = new MotionMarkers() }), "motion keys: layers differ by their markers");
        reject(() => MotionEvaluator.Validate(layer with { Kind = MotionLayerKind.Pose }), "motion keys: markers only on an offset");
        reject(() => MotionEvaluator.Validate(layer with { Markers = new(MarkerDetail.Normal, 0) }), "motion keys: reach below 1");
        reject(() => MotionEvaluator.Validate(layer with { Markers = new(MarkerDetail.Normal, 4) }), "motion keys: reach above 3");
        reject(() => MotionEvaluator.Validate(layer with { Markers = new((MarkerDetail)7, 1) }), "motion keys: unknown detail");
        MotionEvaluator.Validate(layer with { Markers = new(MarkerDetail.More, 3) });

        Journal(check, layer);
    }

    private static void Journal(Action<bool, string> check, MotionLayer motionKeys)
    {
        var temp = Path.Combine(Path.GetTempPath(), "ie-motion-keys-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var resource = new AnimationResource("chara/human/c0801/animation/a0001/bt_common/emote/pose06_start.pap", "pose06_start.pap", "hash");
            var clip = new AnimationClip(resource.GamePath, "start", 0, 0, 42, "chara/test.sklb");
            var capture = new AnimationCapture("clip", 1, 1, Guid.NewGuid(), "Test", "Idle", clip, null, [resource.GamePath], [resource],
                new PoseSnapshot(0, 0, 0, false, [], DateTime.UtcNow, ""), DateTime.UtcNow, true);
            var marked = motionKeys with
            {
                Markers = new MotionMarkers(MarkerDetail.Fewer, 2),
                Keys = [new MotionKey(10, ImmutableDictionary<string, BoneTransform>.Empty.Add(B1,
                    new BoneTransform(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .2f), Vector3.One)))],
            };
            var request = new AnimationBakeRequest(Guid.NewGuid(), capture, AnimationDestination.NewMod, "Motion", false,
                ImmutableHashSet<PoseBoneId>.Empty, PoseComponents.None, AnimationOperation.EditTimeline,
                Timeline: new AnimationTimelineEdit(true, 0, null, [marked]));
            var path = Path.Combine(temp, "AnimationEdits", request.Id.ToString("N"), "journal.json");
            new AnimationJournalStore(temp).Save(new AnimationEditJournal { Id = request.Id, Request = AnimationCommitService.RecoveryRequest(request) });
            var loaded = new AnimationJournalStore(temp).Load().Single().Request.Timeline!.Motion;
            check(loaded.Length == 1 && MotionEdits.Same(loaded[0], marked) && loaded[0].Markers == new MotionMarkers(MarkerDetail.Fewer, 2),
                "motion keys: a journal keeps the layer, its keys and its markers");

            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            check(json["Request"]!["Timeline"]!["Motion"]![0]!["Markers"]!.AsObject().Remove("Detail"), "motion keys: a journal stores the detail as Detail");
            File.WriteAllText(path, json.ToJsonString());
            check(new AnimationJournalStore(temp).Load().Single().Request.Timeline!.Motion[0].Markers is { Detail: MarkerDetail.Normal, Reach: 2 },
                "motion keys: a journal without Detail loads as Normal");

            json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            check(json["Request"]!["Timeline"]!["Motion"]![0]!.AsObject().Remove("Markers"), "motion keys: a journal stores the markers as Markers");
            File.WriteAllText(path, json.ToJsonString());
            check(new AnimationJournalStore(temp).Load().Single().Request.Timeline!.Motion[0].Markers == null,
                "motion keys: a journal without Markers loads an ordinary offset");
        }
        finally { Directory.Delete(temp, true); }
    }

    private static TimelineDocument Empty(int length = 100) => new(length, length, null, [], 1);
}
