using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json.Nodes;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui;
using InstantEdit.Ui.Timeline;

/// <summary>
/// Layer weight over time: weight points ease a layer's strength up and down over it, times its fades; their validation,
/// journals, how every kind takes them, and how moving, resizing, copying and cutting carry or drop them.
/// </summary>
internal static class LayerWeightFixture
{
    private const string Hips = "j_kosi";

    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Curve(check);
        Validation(check, reject);
        Journals(check);
        Apply(check);
        Edits(check);
        Cuts(check);
        Canvas(check);
    }

    private static void Canvas(Action<bool, string> check)
    {
        var geometry = new TimelineGeometry(Legend: 100, Ruler: 24, Row: 20, Edge: 5, Marker: 9);
        var editor = new TimelineEditor(Doc(Offset() with { Start = 10, End = 50, Weights = [new(10, 0), new(50, 1)] }));
        editor.SetWidth(440);
        var row = editor.Layout.LayerRow(0);
        Vector2 At(float frame, float weight) => new(100 + editor.View.X(frame), geometry.WeightY(row, weight));
        TimelineHit Hit(float frame, float weight) => editor.HitTest(geometry, At(frame, weight));

        check(Hit(30, .5f) is { Part: TimelinePart.LayerBody } && editor.WeightRow == null, "with its weight not edited a layer's bar moves it");
        editor.SetWeightEditing("m1", true);
        check(editor.WeightLayer == "m1" && editor.WeightRow == row && Hit(30, .25f) is { Part: TimelinePart.WeightLane, Layer: "m1", Weight: .25f },
            "while its weight is edited the bar takes the weight at the pointer's height");
        check(Hit(50, 1) is { Part: TimelinePart.WeightPoint, KeyFrame: 50 } && Hit(10, 0) is { Part: TimelinePart.WeightPoint, KeyFrame: 10 } &&
              editor.HitTest(geometry, At(10, 1) - new Vector2(4, 0)) is { Part: TimelinePart.LayerLeft },
            "a point's diamond grabs the point, and just outside the bar its end still resizes it");
        check(geometry.WeightY(row, 1) < geometry.WeightY(row, 0) && geometry.WeightValue(row, geometry.WeightY(row, .6f)) == .6f,
            "the line runs from 0 at the row's bottom to 100% at its top");

        var steps = editor.History.UndoCount;
        editor.Press(Hit(30, .25f));
        check(editor.Document.Motion[0].Weights.Contains(new MotionWeight(30, .25f)), "a press on the bar adds a point there at once");
        editor.Drag(34, weight: .5f);
        editor.Release();
        check(editor.Document.Motion[0].Weights.SequenceEqual([new(10, 0), new(34, .5f), new(50, 1)]) && editor.History.UndoCount == steps + 1,
            "the drag after it moves the new point, the add and the move one undo step");
        editor.Undo();
        check(editor.Document.Motion[0].Weights.SequenceEqual([new(10, 0), new(50, 1)]), "undo takes the added point out");
        editor.Redo();

        editor.Press(Hit(50, 1));
        editor.Drag(20, weight: .8f);
        editor.Release();
        check(editor.Document.Motion[0].Weights.SequenceEqual([new(10, 0), new(34, .5f), new(35, .8f)]),
            "dragging a point moves it as far as its neighbour and sets its height");
        check(editor.DeleteWeight(Hit(35, .8f)) && editor.Document.Motion[0].Weights.SequenceEqual([new(10, 0), new(34, .5f)]),
            "a right-click on a point deletes it");
        check(TimelineWindowViews.WeightText(new MotionWeight(12, .6f)) == "Frame 12, 60 %", "a point's tooltip says its frame and weight");

        editor.Commit(MotionEdits.Remove(editor.Document, "m1"));
        check(editor.WeightLayer == null, "deleting the layer stops editing its weight");
    }

    private static void Curve(Action<bool, string> check)
    {
        var plain = Offset();
        check(MotionEvaluator.WeightAt(plain, 30) == 1 && MotionEvaluator.Fade(plain, 30) == 1, "a layer without weight points is at full weight");
        var weighted = plain with { Weights = [new(10, .2f), new(30, 1)] };
        check(MotionEvaluator.WeightAt(weighted, 0) == .2f && MotionEvaluator.WeightAt(weighted, 10) == .2f && MotionEvaluator.WeightAt(weighted, 50) == 1,
            "before the first point its value holds, after the last point its value");
        check(MathF.Abs(MotionEvaluator.WeightAt(weighted, 20) - .6f) < 1e-6f && MathF.Abs(MotionEvaluator.WeightAt(weighted, 15) - (.2f + .8f * .15625f)) < 1e-6f,
            "between two points the weight goes on a smoothstep, halfway at the middle");
        var faded = weighted with { FadeIn = 10 };
        check(MathF.Abs(MotionEvaluator.Fade(faded, 5) - .5f * .2f) < 1e-6f && MathF.Abs(MotionEvaluator.Weight(faded with { Amount = 2 }, 20) - 1.2f) < 1e-6f,
            "the weight multiplies the fade, and the amount on top");
        check(MotionEvaluator.Fade(weighted, 61) == 0 && MotionEvaluator.Fade(weighted, 61, 61.4f) == 1,
            "outside the layer nothing applies, and past a layer reaching the end its last weight holds");
        check(!MotionEvaluator.Active(plain with { Weights = [new(10, 0), new(40, 0)] }) && MotionEvaluator.Active(plain with { Weights = [new(10, 0), new(40, .1f)] }),
            "a layer whose points are all 0 isn't active");
    }

    private static void Validation(Action<bool, string> check, Action<Action, string> reject)
    {
        var good = Offset() with { Weights = [new(0, 0), new(30, 1), new(60, .5f)] };
        MotionEvaluator.Validate(good);
        void Refused(MotionLayer layer, string what) => reject(() => MotionEvaluator.Validate(layer), what);
        Refused(good with { Weights = [new(30, 1), new(10, .5f)] }, "weight points out of order are refused");
        Refused(good with { Weights = [new(10, .5f), new(10, 1)] }, "two weight points on one frame are refused");
        Refused(good with { Weights = [new(61, 1)] }, "a weight point after the layer's end is refused");
        Refused(good with { Start = 5, Weights = [new(4, 1)] }, "a weight point before the layer's start is refused");
        Refused(good with { Weights = [new(10, 1.5f)] }, "a weight above 100% is refused");
        Refused(good with { Weights = [new(10, -.1f)] }, "a negative weight is refused");
        Refused(good with { Weights = [new(10, float.NaN)] }, "a NaN weight is refused");
        Refused(good with { End = 1000, Weights = [.. Enumerable.Range(0, MotionEvaluator.MaxWeights + 1).Select(f => new MotionWeight(f, 1))] },
            "more than 256 weight points are refused");
        var close = new MotionLayer("m2", MotionLayerKind.Fix, "Close loop", 52, 60, 0, 0, 1, PoseComponents.Rotation | PoseComponents.Position,
            MotionInterpolation.Smooth, Fix: new MotionFix(MotionFixKind.CloseLoop));
        MotionEvaluator.Validate(close);
        Refused(close with { Weights = [new(55, .5f)] }, "a close loop, anchored to the clip's end, can't have weight points");
        var swing = new MotionLayer("m3", MotionLayerKind.Swing, "Swing 1", 0, 60, 0, 0, 1, PoseComponents.Rotation, MotionInterpolation.Smooth,
            Bones: [Hips], Swing: new MotionSwing(), Weights: [new(20, .5f)]);
        MotionEvaluator.Validate(swing);
        check(MotionEdits.TakesWeights(swing) && !MotionEdits.TakesWeights(close), "any layer kind takes weight points but an anchored one");
    }

    private static void Journals(Action<bool, string> check)
    {
        var temp = Path.Combine(Path.GetTempPath(), "ie-layer-weight-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var resource = new AnimationResource("chara/human/c0801/animation/a0001/bt_common/emote/dance01.pap", "dance01.pap", "hash");
            var clip = new AnimationClip(resource.GamePath, "dance", 0, 0, 42, "chara/test.sklb");
            var capture = new AnimationCapture("clip", 1, 1, Guid.NewGuid(), "Test", "Idle", clip, null, [resource.GamePath], [resource], DateTime.UtcNow, true);
            var weighted = Offset() with { Weights = [new(10, .25f), new(40, 1)] };
            var request = new AnimationBakeRequest(Guid.NewGuid(), capture, AnimationDestination.NewMod, "Motion", false,
                AnimationOperation.EditTimeline, Timeline: new AnimationTimelineEdit(true, 0, null, [weighted]));
            new AnimationJournalStore(temp).Save(new AnimationEditJournal { Id = request.Id, Request = AnimationCommitService.RecoveryRequest(request) });
            var loaded = new AnimationJournalStore(temp).Load().Single().Request.Timeline!.Motion.Single();
            check(MotionEdits.Same(loaded, weighted) && loaded.Weights.SequenceEqual(weighted.Weights), "a journal keeps a layer's weight points");

            var path = Path.Combine(temp, "AnimationEdits", request.Id.ToString("N"), "journal.json");
            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            check(json["Request"]!["Timeline"]!["Motion"]![0]!.AsObject().Remove("Weights"), "a journal stores a layer's points as Weights");
            File.WriteAllText(path, json.ToJsonString());
            var older = new AnimationJournalStore(temp).Load().Single().Request.Timeline!.Motion.Single();
            MotionEvaluator.Validate(older);
            check(older.Weights.IsEmpty && !older.Weights.IsDefault && MotionEdits.Same(older, Offset()) && MotionEvaluator.Fade(older, 10) == 1,
                "a journal written before weight points loads with none and applies as before");
        }
        finally { Directory.Delete(temp, true); }

        var a = Offset() with { Weights = [new(10, .5f)] };
        check(MotionEdits.Same(a, a with { Weights = [new(10, .5f)] }) && !MotionEdits.Same(a, a with { Weights = [new(10, .6f)] }) &&
              !MotionEdits.Same(a, a with { Weights = [new(11, .5f)] }) && !MotionEdits.Same(a, a with { Weights = [] }),
            "layers differ by their weight points");
    }

    private static void Apply(Action<bool, string> check)
    {
        var still = Clip(61, 60, _ => 0);
        var half = Applied(still, Offset() with { Weights = [new(0, .5f)] });
        var full = Applied(still, Offset());
        check(MathF.Abs(Angle(full, 30) - 20) < .01f && MathF.Abs(Angle(half, 30) - 10) < .01f, "an offset at weight 0.5 gives half its change");
        var eased = Applied(still, Offset() with { Weights = [new(10, 0), new(50, 1)] });
        check(Angle(eased, 5) < 1e-3f && MathF.Abs(Angle(eased, 30) - 10) < .01f && MathF.Abs(Angle(eased, 55) - 20) < .01f,
            "an offset eases in along its weight points");

        // The hips sway 0 to 30 degrees and back: an amplitude doubles it where its weight is 1 and leaves it alone where it is 0.
        var sway = Clip(61, 60, f => 30 * MathF.Sin(f / 60 * MathF.PI));
        var amplitude = new MotionLayer("m1", MotionLayerKind.Amplitude, "Amplitude 1", 0, 60, 0, 0, 2, PoseComponents.Rotation, MotionInterpolation.Smooth,
            Weights: [new(0, 0), new(40, 0), new(50, 1)]);
        var swayed = Applied(sway, amplitude);
        var doubled = Applied(sway, amplitude with { Weights = [] });
        check(Enumerable.Range(0, 41).All(i => Near(swayed.Get(i, Index(Hips)), sway.Get(i, Index(Hips)))) &&
              Near(swayed.Get(55, Index(Hips)), doubled.Get(55, Index(Hips))) && !Near(swayed.Get(55, Index(Hips)), sway.Get(55, Index(Hips))),
            "an amplitude at weight 0 changes nothing, at weight 1 its full factor");

        var local = Enumerable.Range(0, still.Bones).Select(b => still.Get(30, b)).ToArray();
        var at = MotionEvaluator.ApplyAt(SwingLayerFixture.Rig(), local, [Offset() with { Weights = [new(0, .5f)] }], 30, 0, 60);
        check(Near(at[Index(Hips)], half.Get(30, Index(Hips))), "one pose takes the weight as the clip does");
    }

    private static void Edits(Action<bool, string> check)
    {
        var doc = Doc(Offset() with { Start = 10, End = 50, Weights = [new(10, 0), new(30, 1), new(50, .5f)] });
        static int[] Frames(TimelineDocument doc) => [.. doc.Motion[0].Weights.Select(point => point.Frame)];

        var moved = MotionEdits.Move(doc, "m1", 5);
        check(moved.Motion[0] is { Start: 15, End: 55 } && Frames(moved).SequenceEqual([15, 35, 55]), "moving a layer moves its weight points with it");
        var shorter = MotionEdits.Resize(doc, "m1", false, -15);
        check(shorter.Motion[0].End == 35 && Frames(shorter).SequenceEqual([10, 30]), "pulling a layer's end in drops the points left outside");
        var later = MotionEdits.Replace(doc, doc.Motion[0] with { Start = 20 });
        check(Frames(later).SequenceEqual([30, 50]), "a later start drops the points before it in the same change");
        MotionEvaluator.Validate(later.Motion[0]);

        var copied = MotionEdits.Duplicate(doc, "m1", out var created);
        check(MotionEdits.Find(copied, created)!.Weights.SequenceEqual(doc.Motion[0].Weights), "a duplicate carries the weight points");
        var pasted = MotionEdits.Paste(new TimelineDocument(40, 40, null, [], 1), [doc.Motion[0]], 100, out var pastedKeys, out _);
        check(MotionEdits.Find(pasted, pastedKeys[0]) is { End: 40 } layer && layer.Weights.Select(point => point.Frame).SequenceEqual([10, 30]),
            "pasting into a shorter animation carries the points that still fit");

        var added = MotionEdits.SetWeight(doc, "m1", 20, .25f);
        check(Frames(added).SequenceEqual([10, 20, 30, 50]) && added.Motion[0].Weights[1].Value == .25f, "a point goes in by frame");
        check(MotionEdits.SetWeight(doc, "m1", 30, 1.5f).Motion[0].Weights[1].Value == 1 && Frames(MotionEdits.SetWeight(doc, "m1", 70, .5f)).SequenceEqual([10, 30, 50]) &&
              ReferenceEquals(MotionEdits.SetWeight(doc, "m1", 30, 1), doc),
            "a point on a point's frame changes it, values stay within 0 to 1, frames inside the layer");
        var dragged = MotionEdits.MoveWeight(doc, "m1", 30, 40, .75f, out var landed);
        check(landed == 49 && dragged.Motion[0].Weights[1] == new MotionWeight(49, .75f), "a dragged point stops before its neighbour and takes the new value");
        MotionEdits.MoveWeight(doc, "m1", 10, -5, null, out landed);
        check(landed == 10, "a point stays inside the layer");
        var cleared = MotionEdits.ClearWeights(doc, "m1");
        check(Frames(MotionEdits.DeleteWeight(doc, "m1", 30)).SequenceEqual([10, 50]) && cleared.Motion[0].Weights.IsEmpty &&
              ReferenceEquals(MotionEdits.ClearWeights(cleared, "m1"), cleared),
            "a point can be deleted, and Clear weight deletes them all");
        var full = Doc(Offset() with { End = 300 });
        for (var f = 0; f < MotionEvaluator.MaxWeights; f++)
            full = MotionEdits.SetWeight(full, "m1", f, 1);
        check(full.Motion[0].Weights.Length == MotionEvaluator.MaxWeights && ReferenceEquals(MotionEdits.SetWeight(full, "m1", 280, 1), full),
            "a layer takes at most 256 points");

        var face = TimelineFace.ToFace([Offset() with { Weights = [new(5, .2f), new(20, 1)] }], 10).Single();
        check(face is { Start: 0, End: 50 } && face.Weights.Length == 2 && face.Weights[0].Frame == 0 &&
              MathF.Abs(face.Weights[0].Value - MotionEvaluator.WeightAt(Offset() with { Weights = [new(5, .2f), new(20, 1)] }, 10)) < 1e-6f &&
              face.Weights[1] == new MotionWeight(10, 1),
            "on a face starting later the points move back, and those before it become one point at its first frame");
    }

    private static void Cuts(Action<bool, string> check)
    {
        // An offset from 20 to 90 (CutEditsFixture.Doc) with points at 20, 45, 60 and 90.
        var doc = CutEditsFixture.Doc(100);
        doc = doc with { Motion = [doc.Motion[0] with { Weights = [new(20, 0), new(45, 1), new(60, .5f), new(90, 1)] }] };
        var cut = CutEdits.RemoveRange(doc, 40, 60, out _);
        check(cut.Motion[0] is { Start: 20, End: 70 } && cut.Motion[0].Weights.SequenceEqual([new(20, 0), new(40, .5f), new(70, 1)]),
            "removing frames drops the points inside them and moves the later ones back");
        var inserted = CutEdits.Ripple(doc, 50, 10, out _);
        check(inserted.Motion[0] is { End: 100 } && inserted.Motion[0].Weights.Select(point => point.Frame).SequenceEqual([20, 45, 70, 100]),
            "inserting frames moves the points after them");
        var split = CutEdits.CutAt(doc, 50);
        var stretched = CutEdits.StretchPart(split, 0, 50, out _);
        check(stretched.Motion[0].Weights.Select(point => point.Frame).SequenceEqual([40, 90, 110, 140]) && stretched.Motion[0] is { Start: 40, End: 140 },
            "a part played slower spreads its points with its keys");
        foreach (var layer in new[] { cut, inserted, stretched }.SelectMany(d => d.Motion))
            MotionEvaluator.Validate(layer);
    }

    private static MotionLayer Offset() =>
        new("m1", MotionLayerKind.Offset, "Offset 1", 0, 60, 0, 0, 1, PoseComponents.Rotation, MotionInterpolation.Linear,
            Keys: [new MotionKey(0, ImmutableDictionary<string, BoneTransform>.Empty.Add(Hips, new(Vector3.Zero, Q(20), Vector3.One)))]);

    private static TimelineDocument Doc(params MotionLayer[] layers) => new TimelineDocument(100, 100, null, [], 1) { Motion = [.. layers], NextMotionKey = 2 };

    /// <summary>The swing rig with its hips turned <paramref name="degrees"/> of the frame about Z.</summary>
    private static MotionClip Clip(int samples, float frames, Func<float, float> degrees)
    {
        var rig = SwingLayerFixture.Rig();
        var poses = Enumerable.Range(0, samples).Select(i => rig.Bones.Select(bone => bone.Name == Hips
            ? new BoneTransform(bone.Reference.Position, Q(degrees(frames * i / (samples - 1))), Vector3.One) : bone.Reference).ToArray()).ToArray();
        var made = MotionClip.FromPoses(rig, frames / TimelineDocument.Fps, poses);
        var clip = new MotionClip(rig, made.Duration, made.Samples);
        clip.CopyFrom(made);
        return clip;
    }

    private static MotionClip Applied(MotionClip source, params MotionLayer[] layers)
    {
        var clip = source.Clone();
        MotionEvaluator.Apply(clip, layers);
        return clip;
    }

    private static int Index(string bone) => MotionEvaluator.Index(SwingLayerFixture.Rig())[bone];

    /// <summary>How far the hips turn at a sample, in degrees.</summary>
    private static float Angle(MotionClip clip, int sample) =>
        2 * MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Normalize(clip.Get(sample, Index(Hips)).Rotation).W), 0, 1)) * 180 / MathF.PI;

    private static bool Near(MotionTransform a, MotionTransform b) =>
        Vector3.Distance(a.Position, b.Position) <= 1e-4f && 1 - MathF.Abs(Quaternion.Dot(Quaternion.Normalize(a.Rotation), Quaternion.Normalize(b.Rotation))) < 1e-6f;

    private static Quaternion Q(float degrees) => Quaternion.CreateFromAxisAngle(Vector3.UnitZ, degrees * MathF.PI / 180);
}
