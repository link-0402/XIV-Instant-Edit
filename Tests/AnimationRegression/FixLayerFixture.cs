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
/// Fix layers: Match startup to loop (lands the last sample on the loop's first frame with its speed) and
/// Smooth jitter (calms small shakes, keeps snaps), their validation, journals and document edits.
/// </summary>
internal static class FixLayerFixture
{
    private const string B1 = "j_b1", B2 = "j_b2";
    private const float RotationTolerance = .05f, PositionTolerance = 1e-4f;

    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Match(check);
        Smooth(check);
        Validation(check, reject);
        Journals(check);
        Edits(check);
        Views(check);
        Reader(check);
    }

    private static void Match(Action<bool, string> check)
    {
        var loop = Loop();
        var layer = MatchLayer(loop);
        var plain = Startup(31, 30);
        var clip = Applied(plain, layer);
        var last = clip.Samples - 1;
        check(Lands(clip, loop, B1) && Lands(clip, loop, B2), "match lands: the last sample is the loop's first frame, rotation and position");
        var span = MotionFixes.MatchSpan(layer, clip.Frame(last));
        check(span == (22, 30) && Before(clip, Applied(plain), span.Start), "match lands: samples before the blend are unchanged");

        // At 8 samples per frame the last pair carries almost none of the gap, so its change is the loop's speed.
        var fine = Applied(Startup(241, 30), layer);
        check(Speed(fine, 8, loop, B1, RotationTolerance, PositionTolerance), "match speed: the last pair moves at the loop's speed per frame");

        var half = Applied(plain, layer with { Amount = .5f });
        check(MathF.Abs(Degrees(half.Get(last, Index(B1)).Rotation, loop.LoopFirst![B1].Rotation) - 10) < .1f, "match half: 50% lands halfway");

        var limited = layer with { Bones = [B1], Fix = Loop(ghost: true) };
        var limitedClip = Applied(plain, limited);
        MotionEvaluator.CheckBones([limited], Rig());
        check(Lands(limitedClip, loop, B1) && Track(limitedClip, B2).SequenceEqual(Track(Applied(plain), B2)),
            "match limit: a bone outside the limit is unchanged and a loop bone the skeleton lacks is skipped");

        var anchored = Applied(plain, layer with { Start = 10, End = 18 });
        check(Lands(anchored, loop, B1) && Before(anchored, Applied(plain), 22) && !Same(anchored.Get(23, Index(B1)), Applied(plain).Get(23, Index(B1))),
            "match anchored: a stored 10-18 blends over the clip's last 8 frames");

        var fraction = Applied(Startup(31, 30.4f), layer);
        check(Lands(fraction, loop, B1) && Lands(fraction, loop, B2), "match fraction: a last sample past the rounded end still lands");

        var shortPlain = Startup(5, 4);
        var shortClip = Applied(shortPlain, layer with { Start = 0, End = 8 });
        check(MotionFixes.MatchSpan(layer with { Start = 0, End = 8 }, 4) == (0, 4) && Lands(shortClip, loop, B1) &&
              Same(shortClip.Get(0, Index(B1)), Applied(shortPlain).Get(0, Index(B1))),
            "match short: a clip shorter than the blend starts it at frame 0 and lands");

        var rate = Applied(Startup(61, 30), layer);
        check(Lands(rate, loop, B1) && Speed(rate, 2, loop, B1, .02f * 20, .02f * Vector3.Distance(loop.LoopFirst[B1].Position, new Vector3(.3f, 0, 0))),
            "match rate: two samples per frame land and keep the loop's per-frame speed");

        var flipped = Loop() with { LoopFirst = Loop().LoopFirst!.SetItem(B1, loop.LoopFirst[B1] with { Rotation = -loop.LoopFirst[B1].Rotation }) };
        var flipClip = Applied(plain, layer with { Fix = flipped });
        check(Lands(flipClip, loop, B1) && Enumerable.Range(0, flipClip.Samples).All(i => Finite(flipClip.Get(i, Index(B1)))),
            "match flip: a negated loop rotation lands the same, no NaN");

        var baseline = Applied(plain);
        var identity = Applied(plain, layer with { Fix = Continuing(baseline) });
        check(Enumerable.Range(0, identity.Samples).All(i => Enumerable.Range(0, identity.Bones).All(b => Near(identity.Get(i, b), baseline.Get(i, b), 1e-5f))),
            "match identity: a loop that carries on the startup's motion changes nothing");

        var looping = Clip(31, 30, (_, f) => [T(Vector3.Zero), T(new Vector3(.01f * f, 0, 0), Q(Vector3.UnitY, f)), T(Vector3.UnitY)], loops: true);
        check(Bits(Applied(looping, layer), Applied(looping)), "match loop clip: a match on a looping clip changes nothing");

        var offset = new MotionLayer("m2", MotionLayerKind.Offset, "Offset", 0, 30, 0, 0, 1, PoseComponents.All, MotionInterpolation.Linear, [],
            [new MotionKey(0, ImmutableDictionary.CreateRange(StringComparer.Ordinal, [KeyValuePair.Create(B1, T(new Vector3(.1f, 0, 0), Q(Vector3.UnitX, 10)))]))]);
        check(Lands(Applied(plain, layer, offset), loop, B1), "match last: a match listed before an offset still lands");

        check(MotionFixes.LoopPose(Startup(31, 30)) is { Kind: MotionFixKind.MatchLoop } pose &&
              Near(MotionTransform.From(pose.LoopFirst![B1]), Startup(31, 30).Get(0, Index(B1)), 1e-5f) &&
              Near(MotionTransform.From(pose.LoopSecond![B1]), Startup(31, 30).Get(1, Index(B1)), 1e-5f) && pose.LoopFirst.Count == 3,
            "a loop pose holds the loop's frames 0 and 1 by bone name");
    }

    private static void Smooth(Action<bool, string> check)
    {
        var noisy = Noisy();
        var smoothed = Applied(noisy, SmoothLayer(1, 0, 60));
        var b1 = Index(B1);
        check(Energy(smoothed, b1) < .1f * Energy(noisy, b1) && PositionEnergy(smoothed, b1) < .1f * PositionEnergy(noisy, b1),
            "smooth calms: the jitter's energy drops below a tenth");
        var b2 = Index(B2);
        check(MathF.Abs(AboutY(smoothed.Get(30, b2).Rotation) - AboutY(smoothed.Get(29, b2).Rotation)) >= 27, "smooth calms: a 30-degree step survives");

        // Steady turning and moving: neighbours past a non-looping clip's ends mirror about them, so the ends stay put.
        var steady = Applied(Startup(31, 30));
        var steadied = Applied(Startup(31, 30), SmoothLayer(1, 0, 30));
        check(new[] { 0, steady.Samples - 1 }.All(i => new[] { B1, B2 }.All(bone =>
                  Vector3.Distance(steadied.Get(i, Index(bone)).Position, steady.Get(i, Index(bone)).Position) < 1e-4f &&
                  MotionCutter.Log(steadied.Get(i, Index(bone)).Rotation * Quaternion.Inverse(steady.Get(i, Index(bone)).Rotation)).Length() * 180 / MathF.PI < .01f)),
            "smooth ends: steady motion keeps the first and last samples");

        check(Bits(Applied(noisy, SmoothLayer(0, 0, 60)), Applied(noisy)), "smooth zero: strength 0 changes nothing");
        var still = Clip(61, 60, (_, _) => [T(Vector3.Zero), T(new Vector3(.2f, .1f, 0), Q(Vector3.UnitY, 30)), T(Vector3.UnitX)]);
        check(Bits(Applied(still, SmoothLayer(1, 0, 60)), Applied(still)), "smooth zero: a still bone is left as it is");

        // Noise only beside the seam: the first sample sees it only round the loop.
        BoneTransform[] Seam(int i, float frame) => [T(Vector3.Zero), T(Vector3.Zero, Q(Vector3.UnitY, i == 29 ? 2 : 0)), T(Vector3.UnitX)];
        var looped = Applied(Clip(31, 30, Seam, loops: true), SmoothLayer(1, 0, 30));
        var once = Applied(Clip(31, 30, Seam), SmoothLayer(1, 0, 30));
        check(AboutY(looped.Get(0, b1).Rotation) > .1f && MathF.Abs(AboutY(looped.Get(0, b1).Rotation) - AboutY(looped.Get(30, b1).Rotation)) < 1e-3f &&
              MathF.Abs(AboutY(once.Get(0, b1).Rotation)) < 1e-4f,
            "smooth wraps: on a loop the first sample is smoothed with the last ones");
    }

    private static void Validation(Action<bool, string> check, Action<Action, string> reject)
    {
        var match = MatchLayer(Loop());
        MotionEvaluator.Validate(match);
        MotionEvaluator.Validate(SmoothLayer(.5f, 0, 30));
        reject(() => MotionEvaluator.Validate(match with { Fix = null }), "a fix layer without a fix is refused");
        reject(() => MotionEvaluator.Validate(new MotionLayer("m1", MotionLayerKind.Offset, "Offset 1", 0, 30, 0, 0, 1, PoseComponents.All,
            MotionInterpolation.Linear, Fix: new MotionFix(MotionFixKind.SmoothJitter))), "a fix on an offset layer is refused");
        reject(() => MotionEvaluator.Validate(match with { Keys = [new MotionKey(0, Loop().LoopFirst!)] }), "a fix layer with keys is refused");
        reject(() => MotionEvaluator.Validate(match with { Pins = [new MotionPin(B1)] }), "a fix layer with pins is refused");
        reject(() => MotionEvaluator.Validate(match with { Fix = new MotionFix(MotionFixKind.MatchLoop) }), "a match without a loop pose is refused");
        reject(() => MotionEvaluator.Validate(match with { Fix = Loop() with { LoopFirst = ImmutableDictionary<string, BoneTransform>.Empty } }),
            "a match with an empty loop pose is refused");
        reject(() => MotionEvaluator.Validate(match with { Fix = Loop() with { LoopSecond = Loop().LoopSecond!.Remove(B2) } }),
            "a match whose loop frames name different bones is refused");
        reject(() => MotionEvaluator.Validate(match with { Fix = Loop() with { LoopFirst = Loop().LoopFirst!.SetItem(B1, T(new Vector3(float.NaN))) } }),
            "a match whose loop pose isn't a transform is refused");
        reject(() => MotionEvaluator.Validate(match with { Fix = new MotionFix((MotionFixKind)9) }), "an unknown fix is refused");
        reject(() => MotionEvaluator.Validate(match with { Amount = 1.5f }), "a fix above 100% is refused");
        check(MotionEvaluator.Active(match) && MotionEvaluator.Active(SmoothLayer(.5f, 0, 30)) && !MotionEvaluator.Active(SmoothLayer(0, 0, 30)) &&
              !MotionEvaluator.Active(match with { Fix = null }) && !MotionEvaluator.Active(match with { Components = PoseComponents.Scale }),
            "a fix layer is active with a fix, an amount and a rotation or position to change");
    }

    private static void Journals(Action<bool, string> check)
    {
        var temp = Path.Combine(Path.GetTempPath(), "ie-fix-layer-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var resource = new AnimationResource("chara/human/c0801/animation/a0001/bt_common/emote/pose06_start.pap", "pose06_start.pap", "hash");
            var clip = new AnimationClip(resource.GamePath, "start", 0, 0, 42, "chara/test.sklb");
            var capture = new AnimationCapture("clip", 1, 1, Guid.NewGuid(), "Test", "Idle", clip, null, [resource.GamePath], [resource],
                new PoseSnapshot(0, 0, 0, false, [], DateTime.UtcNow, ""), DateTime.UtcNow, true);
            var match = MatchLayer(Loop());
            var smooth = SmoothLayer(.5f, 0, 30) with { Key = "m2" };
            var request = new AnimationBakeRequest(Guid.NewGuid(), capture, AnimationDestination.NewMod, "Motion", false,
                ImmutableHashSet<PoseBoneId>.Empty, PoseComponents.None, AnimationOperation.EditTimeline,
                Timeline: new AnimationTimelineEdit(true, 0, null, [match, smooth]));
            new AnimationJournalStore(temp).Save(new AnimationEditJournal { Id = request.Id, Request = AnimationCommitService.RecoveryRequest(request) });
            var loaded = new AnimationJournalStore(temp).Load().Single().Request.Timeline!.Motion;
            check(loaded.Length == 2 && MotionEdits.Same(loaded[0], match) && MotionEdits.Same(loaded[1], smooth) &&
                  loaded[0].Fix!.LoopFirst![B1] == match.Fix!.LoopFirst![B1], "a journal keeps both fixes and the loop pose");

            var path = Path.Combine(temp, "AnimationEdits", request.Id.ToString("N"), "journal.json");
            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            check(json["Request"]!["Timeline"]!["Motion"]![1]!.AsObject().Remove("Fix"), "a journal stores a layer's fix as Fix");
            File.WriteAllText(path, json.ToJsonString());
            var older = new AnimationJournalStore(temp).Load().Single().Request.Timeline!.Motion[1];
            check(older.Fix == null && !MotionEvaluator.Active(older), "a layer without Fix loads with none");
        }
        finally { Directory.Delete(temp, true); }

        var a = MatchLayer(Loop());
        check(MotionEdits.Same(a, a with { Fix = Loop() }) && !MotionEdits.Same(a, a with { Fix = Loop(shifted: true) }) &&
              !MotionEdits.Same(a, a with { Fix = new MotionFix(MotionFixKind.SmoothJitter) }) && !MotionEdits.Same(a, a with { Fix = null }),
            "layers differ by their fix's kind and loop pose, compared by content");
    }

    private static void Edits(Action<bool, string> check)
    {
        var doc = new TimelineDocument(100, 100, null, [], 1);
        var loop = Loop();
        var withMatch = MotionEdits.AddFix(doc, loop, out var matchKey, out var error);
        check(error == null && MotionEdits.Find(withMatch, matchKey) is
              {
                  Kind: MotionLayerKind.Fix, Name: "Match loop", Start: 92, End: 100, FadeIn: 0, FadeOut: 0, Amount: 1,
                  Components: PoseComponents.Rotation | PoseComponents.Position,
              } match && ReferenceEquals(match.Fix, loop),
            "Match loop spans the clip's last 8 frames at full blend");
        var withSmooth = MotionEdits.AddFix(doc, new MotionFix(MotionFixKind.SmoothJitter), out var smoothKey, out error);
        check(error == null && MotionEdits.Find(withSmooth, smoothKey) is
              { Kind: MotionLayerKind.Fix, Name: "Smooth jitter", Start: 0, End: 100, FadeIn: 0, FadeOut: 0, Amount: .5f, Fix.Kind: MotionFixKind.SmoothJitter },
            "Smooth jitter spans the whole clip at half strength");
        check(MotionEdits.Find(MotionEdits.AddFix(doc with { Length = 5 }, Loop(), out var shortKey, out _), shortKey) is { Start: 0, End: 5 },
            "a match on a clip shorter than its blend starts at frame 0");

        var longer = MotionEdits.SetFixLength(withMatch, matchKey, 12);
        check(MotionEdits.Find(longer, matchKey) is { Start: 88, End: 100 } && MotionEdits.Find(MotionEdits.SetFixLength(withMatch, matchKey, 0), matchKey) is { Start: 99 } &&
              MotionEdits.Find(MotionEdits.SetFixLength(withMatch, matchKey, 500), matchKey) is { Start: 0, End: 100 } &&
              ReferenceEquals(MotionEdits.SetFixLength(withSmooth, smoothKey, 12), withSmooth) && ReferenceEquals(MotionEdits.SetFixLength(withMatch, matchKey, 8), withMatch),
            "Blend over sets a match's length back from the clip's end, and only a match's");

        var reread = MotionEdits.SetLoopPose(withMatch, matchKey, Loop(shifted: true));
        check(MotionEdits.Find(reread, matchKey)!.Fix!.LoopFirst![B1] == Loop(shifted: true).LoopFirst![B1] &&
              ReferenceEquals(MotionEdits.SetLoopPose(withSmooth, smoothKey, Loop()), withSmooth),
            "Re-read loop replaces a match's loop pose");
        check(MotionEdits.KindName(MotionLayerKind.Fix) == "Fix" && MotionEdits.MaxAmount(MotionLayerKind.Fix) == 1, "the kind is named Fix and blends at most fully");

        // The canvas: a match's bar and end stay on the clip's last frame, its start sets its length.
        check(ReferenceEquals(MotionEdits.Move(withMatch, matchKey, -5), withMatch) && ReferenceEquals(MotionEdits.Resize(withMatch, matchKey, false, -5), withMatch) &&
              ReferenceEquals(MotionEdits.Fade(withMatch, matchKey, true, 3), withMatch) &&
              MotionEdits.Find(MotionEdits.Resize(withMatch, matchKey, true, -4), matchKey) is { Start: 88, End: 100 },
            "a match's bar and end don't move; dragging its start sets its length");
        var stale = withMatch with { Length = 50 };
        check(MotionEdits.Shown(MotionEdits.Find(stale, matchKey)!, stale.Length) is { Start: 42, End: 50, FadeIn: 0, FadeOut: 0 } &&
              MotionEdits.Find(MotionEdits.Resize(stale, matchKey, true, -2), matchKey) is { Start: 40, End: 50 } &&
              MotionEdits.SnapFrames(stale, null).SequenceEqual([42, 50]) &&
              MotionEdits.Shown(MotionEdits.Find(withSmooth, smoothKey)!, 100) is { Start: 0, End: 100 },
            "a match shows, snaps and drags over the clip's last frames whatever it stores");

        // Cuts: a match stays on the clip's last frame with its length; a smooth inside the removed frames still goes.
        check(CutEdits.RemoveRange(withMatch, 95, 100, out _) is { Length: 95 } tail && MotionEdits.Find(tail, matchKey) is { Start: 87, End: 95 } &&
              CutEdits.RemoveRange(withMatch, 90, 100, out _) is { Length: 90 } over && MotionEdits.Find(over, matchKey) is { Start: 82, End: 90 } &&
              CutEdits.RemoveRange(withMatch, 40, 60, out _) is { Length: 80 } middle && MotionEdits.Find(middle, matchKey) is { Start: 72, End: 80 },
            "removing the clip's last frames keeps a match on the new end with its length");
        check(CutEdits.RemoveRange(withMatch, 5, 100, out _) is { Length: 5 } shortCut && MotionEdits.Find(shortCut, matchKey) is { Start: 0, End: 5 },
            "a cut shorter than the blend starts the match at frame 0");
        check(CutEdits.Ripple(withMatch, 95, 10, out _) is { Length: 110 } inserted && MotionEdits.Find(inserted, matchKey) is { Start: 102, End: 110 },
            "frames inserted inside a match's blend keep its length and end");
        check(CutEdits.StretchPart(withMatch, 0, 20, out _) is { Length: 120 } stretched && MotionEdits.Find(stretched, matchKey) is { Start: 112, End: 120 },
            "stretching the clip keeps a match's length and end");
        var copied = MotionEdits.Find(withMatch, matchKey)!;
        check(MotionEdits.Paste(new TimelineDocument(50, 50, null, [], 1), [copied], 100, out var pasted, out _) is var into &&
              MotionEdits.Find(into, pasted[0]) is { Start: 42, End: 50 } &&
              MotionEdits.Paste(new TimelineDocument(5, 5, null, [], 1), [copied], 100, out var pastedShort, out _) is var intoShort &&
              MotionEdits.Find(intoShort, pastedShort[0]) is { Start: 0, End: 5 },
            "a match pasted into a shorter clip keeps its length and ends on that clip's end, from frame 0 when shorter than the blend");
        var smoothTail = withSmooth with { Motion = [MotionEdits.Find(withSmooth, smoothKey)! with { Start = 92, End = 100 }] };
        check(CutEdits.RemoveRange(smoothTail, 90, 100, out _).Motion.IsEmpty, "a smooth inside the removed frames is still removed");

        var editor = new TimelineEditor(doc);
        check(editor.AddFix(new MotionFix(MotionFixKind.SmoothJitter)) && MotionEdits.Find(editor.Document, editor.SelectedLayer) is { Name: "Smooth jitter" } &&
              editor.Undo() && editor.Document.Motion.IsEmpty, "the editor adds a fix as one undo step and selects it");
        editor.AddFix(loop);
        var key = editor.SelectedLayer!;
        check(editor.SetFixLength(key, 20) && MotionEdits.Find(editor.Document, key) is { Start: 80, End: 100 } && editor.SelectedLayer == key &&
              editor.Undo() && MotionEdits.Find(editor.Document, key) is { Start: 92, End: 100 }, "Blend over is one undo step");
        check(editor.SetLoopPose(key, Loop(shifted: true)) && editor.Undo() && ReferenceEquals(MotionEdits.Find(editor.Document, key)!.Fix, loop),
            "Re-read loop is one undo step");
    }

    private static void Views(Action<bool, string> check)
    {
        var match = MatchLayer(Loop());
        check(!TimelineWindowViews.LayerKinds.Contains(MotionLayerKind.Fix) && TimelineWindowViews.LayerKinds.Last() == MotionLayerKind.Pin &&
              TimelineUi.LayerIcons.Length > (int)MotionLayerKind.Fix && TimelineUi.LayerIcon(MotionLayerKind.Fix) != TimelineUi.LayerIcon(MotionLayerKind.Pin) &&
              TimelineWindowViews.LayerColour(MotionLayerKind.Fix) != TimelineWindowViews.LayerColour(MotionLayerKind.Pose) &&
              TimelineWindowViews.LayerKindHint(MotionLayerKind.Fix) != TimelineWindowViews.LayerKindHint(MotionLayerKind.Pose),
            "Fix has its own icon, colour and hint, and is added only by its own items");
        check(TimelineWindowViews.DragGizmo(match, B1, null) == "This layer doesn't turn bones." && !TimelineLayout.Unfolds(match) &&
              TimelineWindowViews.LayerContent(match) == "Every bone" && TimelineWindowViews.LayerContent(match with { Bones = [B1] }) == "1 bone",
            "a fix layer has no gizmo, no bone rows and lists the bones it is limited to");
    }

    /// <summary>The loop read for Match startup to loop and Re-read loop: one at a time, dropped when the clip or the layer goes.</summary>
    private static void Reader(Action<bool, string> check)
    {
        var source = BlenderMotionFixture.Source() with { Startup = true };
        var session = new TimelineSession(null, null!, null!, null!, null, null!, null!, null!) { Source = source };
        var editor = new TimelineEditor(source.Document!);
        var reader = new LoopPoseReader(session);
        var pending = new TaskCompletionSource<MotionFix>();
        var calls = 0;
        reader.Read(source, null, _ => pending.Task);
        reader.Read(source, null, _ =>
        {
            calls++;
            return pending.Task;
        });
        reader.Poll(editor);
        check(reader.Reading && calls == 0 && editor.Document.Motion.IsEmpty, "one loop read at a time; nothing is added while it runs");
        pending.SetResult(Loop());
        reader.Poll(editor);
        check(!reader.Reading && MotionEdits.Find(editor.Document, editor.SelectedLayer) is { Fix.Kind: MotionFixKind.MatchLoop }, "a finished loop read adds a match and selects it");
        var key = editor.SelectedLayer!;

        reader.Read(source, key, _ => Task.FromResult(Loop(shifted: true)));
        reader.Poll(editor);
        check(MotionEdits.Find(editor.Document, key)!.Fix!.LoopFirst![B1] == Loop(shifted: true).LoopFirst![B1], "a re-read replaces the match's loop pose");

        var before = editor.Document;
        reader.Read(source, key, _ => Task.FromResult(Loop()));
        session.Source = source with { Startup = false };
        reader.Poll(editor);
        session.Source = source;
        check(ReferenceEquals(editor.Document, before) && !reader.Reading, "a read for a clip no longer open is dropped");

        editor.Commit(MotionEdits.Remove(editor.Document, key));
        before = editor.Document;
        reader.Read(source, key, _ => Task.FromResult(Loop()));
        reader.Poll(editor);
        check(ReferenceEquals(editor.Document, before), "a re-read for a deleted layer is dropped");

        reader.Read(source, null, _ => Task.FromException<MotionFix>(new InvalidDataException("No file.")));
        reader.Poll(editor);
        check(editor.LastError == "Couldn't read the loop: No file." && ReferenceEquals(editor.Document, before), "a failed loop read says why");

        CancellationToken seen = default;
        reader.Read(source, null, token =>
        {
            seen = token;
            return new TaskCompletionSource<MotionFix>().Task;
        });
        reader.Drop();
        check(seen.IsCancellationRequested && !reader.Reading, "Drop stops the read");
    }

    // A root and two bones under it.
    private static SkeletonDescription Rig() => new("fix-rig", "fix-rig-fingerprint",
        [new("n_root", -1, 0, T(Vector3.Zero)), new(B1, 0, 0, T(Vector3.UnitX)), new(B2, 0, 0, T(Vector3.UnitY))], [], [], []);

    private static int Index(string bone) => MotionEvaluator.Index(Rig())[bone];

    /// <summary>
    /// A startup <paramref name="frames"/> frames long in <paramref name="samples"/> samples: b1 turns 1 degree per
    /// frame about Y and slides 0.01 a frame along X, b2 turns half a degree per frame about X and rises.
    /// </summary>
    private static MotionClip Startup(int samples, float frames) => Clip(samples, frames, (_, f) =>
    [
        T(Vector3.Zero),
        T(new Vector3(.01f * f, 0, 0), Q(Vector3.UnitY, f)),
        T(new Vector3(0, .005f * f, 0), Q(Vector3.UnitX, .5f * f)),
    ]);

    /// <summary>
    /// A loop whose first frame is 20 degrees on from where <see cref="Startup"/> ends and that moves 2 degrees a
    /// frame; with <paramref name="ghost"/> it names a bone the rig lacks.
    /// </summary>
    private static MotionFix Loop(bool ghost = false, bool shifted = false)
    {
        var first = new Dictionary<string, BoneTransform>(StringComparer.Ordinal)
        {
            ["n_root"] = T(Vector3.Zero),
            [B1] = T(new Vector3(.5f, .1f, 0), Q(Vector3.UnitY, shifted ? 60 : 50)),
            [B2] = T(new Vector3(0, .2f, 0), Q(Vector3.UnitX, 30)),
        };
        var second = new Dictionary<string, BoneTransform>(StringComparer.Ordinal)
        {
            ["n_root"] = T(Vector3.Zero),
            [B1] = T(new Vector3(.502f, .101f, 0), Q(Vector3.UnitY, shifted ? 62 : 52)),
            [B2] = T(new Vector3(0, .2f, 0), Q(Vector3.UnitX, 31)),
        };
        if (ghost)
            first["j_ghost"] = second["j_ghost"] = T(Vector3.One);
        return new MotionFix(MotionFixKind.MatchLoop, ImmutableDictionary.CreateRange(StringComparer.Ordinal, first),
            ImmutableDictionary.CreateRange(StringComparer.Ordinal, second));
    }

    /// <summary>A loop pose that carries on <paramref name="clip"/>'s motion from its last sample, one sample a frame.</summary>
    private static MotionFix Continuing(MotionClip clip)
    {
        var last = clip.Samples - 1;
        var first = ImmutableDictionary.CreateBuilder<string, BoneTransform>(StringComparer.Ordinal);
        var second = ImmutableDictionary.CreateBuilder<string, BoneTransform>(StringComparer.Ordinal);
        for (var b = 0; b < clip.Bones; b++)
        {
            var (cur, prev) = (clip.Get(last, b), clip.Get(last - 1, b));
            var name = Rig().Bones[b].Name;
            first[name] = cur.ToBone();
            second[name] = new BoneTransform(cur.Position + (cur.Position - prev.Position),
                Quaternion.Normalize(cur.Rotation * Quaternion.Inverse(prev.Rotation) * cur.Rotation), cur.Scale);
        }
        return new MotionFix(MotionFixKind.MatchLoop, first.ToImmutable(), second.ToImmutable());
    }

    private static MotionLayer MatchLayer(MotionFix fix) =>
        new("m1", MotionLayerKind.Fix, "Match loop", 22, 30, 0, 0, 1, PoseComponents.Rotation | PoseComponents.Position, MotionInterpolation.Smooth, Fix: fix);

    private static MotionLayer SmoothLayer(float amount, int start, int end) =>
        new("m1", MotionLayerKind.Fix, "Smooth jitter", start, end, 0, 0, amount, PoseComponents.Rotation | PoseComponents.Position,
            MotionInterpolation.Smooth, Fix: new MotionFix(MotionFixKind.SmoothJitter));

    /// <summary>
    /// 61 samples over 60 frames: b1 sweeps 30 degrees about Y and 0.1 along X with a 1-degree, 2 mm shake
    /// each sample; b2 steps 30 degrees at sample 30 with the same shake.
    /// </summary>
    private static MotionClip Noisy() => Clip(61, 60, (i, _) =>
    {
        var shake = i % 2 == 0 ? 1 : -1;
        return
        [
            T(Vector3.Zero),
            T(new Vector3(.1f * i / 60 + .002f * shake, 0, 0), Q(Vector3.UnitY, 30 * AnimationPoseRules.SmoothStep(i / 60f) + shake)),
            T(Vector3.UnitY, Q(Vector3.UnitY, (i >= 30 ? 30 : 0) + shake)),
        ];
    });

    private static MotionClip Clip(int samples, float frames, Func<int, float, BoneTransform[]> pose, bool loops = false)
    {
        var poses = Enumerable.Range(0, samples).Select(i => pose(i, frames * i / (samples - 1))).ToArray();
        var made = MotionClip.FromPoses(Rig(), frames / TimelineDocument.Fps, poses);
        var clip = new MotionClip(Rig(), made.Duration, made.Samples) { Loops = loops };
        clip.CopyFrom(made);
        return clip;
    }

    private static MotionClip Applied(MotionClip source, params MotionLayer[] layers)
    {
        var clip = source.Clone();
        MotionEvaluator.Apply(clip, layers.Select((layer, i) => layer with { Key = "m" + (i + 1) }).ToArray());
        return clip;
    }

    private static bool Lands(MotionClip clip, MotionFix fix, string bone)
    {
        var value = clip.Get(clip.Samples - 1, Index(bone));
        var target = fix.LoopFirst![bone];
        return Degrees(value.Rotation, target.Rotation) < RotationTolerance && Vector3.Distance(value.Position, target.Position) < PositionTolerance;
    }

    /// <summary>Whether the last sample pair, times <paramref name="perFrame"/>, moves as the loop's frames 0 to 1 do.</summary>
    private static bool Speed(MotionClip clip, int perFrame, MotionFix fix, string bone, float degrees, float distance)
    {
        var (cur, prev) = (clip.Get(clip.Samples - 1, Index(bone)), clip.Get(clip.Samples - 2, Index(bone)));
        var (first, second) = (fix.LoopFirst![bone], fix.LoopSecond![bone]);
        var turn = (AboutY(cur.Rotation) - AboutY(prev.Rotation)) * perFrame;
        var move = (cur.Position - prev.Position) * perFrame;
        return MathF.Abs(turn - (AboutY(second.Rotation) - AboutY(first.Rotation))) < degrees &&
               Vector3.Distance(move, second.Position - first.Position) < distance;
    }

    private static bool Before(MotionClip a, MotionClip b, int frame) =>
        Enumerable.Range(0, a.Samples).Where(i => a.Frame(i) < frame).All(i => Enumerable.Range(0, a.Bones).All(bone => a.Get(i, bone) == b.Get(i, bone)));

    private static bool Bits(MotionClip a, MotionClip b) =>
        Enumerable.Range(0, a.Samples).All(i => Enumerable.Range(0, a.Bones).All(bone => a.Get(i, bone) == b.Get(i, bone)));

    private static MotionTransform[] Track(MotionClip clip, string bone) => [.. Enumerable.Range(0, clip.Samples).Select(i => clip.Get(i, Index(bone)))];

    /// <summary>The sum of squared second differences of a bone's angle about Y.</summary>
    private static float Energy(MotionClip clip, int bone) => Enumerable.Range(1, clip.Samples - 2).Sum(i =>
        MathF.Pow(AboutY(clip.Get(i + 1, bone).Rotation) - 2 * AboutY(clip.Get(i, bone).Rotation) + AboutY(clip.Get(i - 1, bone).Rotation), 2));

    private static float PositionEnergy(MotionClip clip, int bone) => Enumerable.Range(1, clip.Samples - 2).Sum(i =>
        (clip.Get(i + 1, bone).Position - 2 * clip.Get(i, bone).Position + clip.Get(i - 1, bone).Position).LengthSquared());

    private static bool Same(MotionTransform a, MotionTransform b) => a == b;

    /// <summary>Whether two transforms differ by at most <paramref name="tolerance"/> in any part, a rotation and its negation alike.</summary>
    private static bool Near(MotionTransform a, MotionTransform b, float tolerance) =>
        Vector3.Distance(a.Position, b.Position) <= tolerance && Vector3.Distance(a.Scale, b.Scale) <= tolerance &&
        (Quaternion.Dot(a.Rotation, b.Rotation) >= 0 ? a.Rotation - b.Rotation : a.Rotation + b.Rotation).Length() <= tolerance;

    private static bool Finite(MotionTransform value) =>
        float.IsFinite(value.Rotation.X) && float.IsFinite(value.Rotation.Y) && float.IsFinite(value.Rotation.Z) && float.IsFinite(value.Rotation.W) &&
        float.IsFinite(value.Position.X) && float.IsFinite(value.Position.Y) && float.IsFinite(value.Position.Z);

    /// <summary>The signed angle of a rotation about Y, in degrees.</summary>
    private static float AboutY(Quaternion q) => 2 * MathF.Atan2(q.W < 0 ? -q.Y : q.Y, MathF.Abs(q.W)) * 180 / MathF.PI;

    private static Quaternion Q(Vector3 axis, float degrees) => Quaternion.CreateFromAxisAngle(axis, degrees * MathF.PI / 180);

    private static float Degrees(Quaternion a, Quaternion b) =>
        2 * MathF.Acos(Math.Clamp(Math.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))), 0, 1)) * 180 / MathF.PI;

    private static BoneTransform T(Vector3 position, Quaternion? rotation = null) => new(position, rotation ?? Quaternion.Identity, Vector3.One);
}
