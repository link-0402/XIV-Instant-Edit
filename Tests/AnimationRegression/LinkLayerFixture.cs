using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json.Nodes;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui.Timeline;

/// <summary>Link layers: a bone that moves with a partner's bone. Their validation, activity, partner tracks and journals.</summary>
internal static class LinkLayerFixture
{
    private static readonly Guid Partner = new("6f1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d");

    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Validation(check, reject);
        Tracks(check, reject);
        Journals(check);
        Solving(check);
    }

    private static void Validation(Action<bool, string> check, Action<Action, string> reject)
    {
        var body = HipDragFixture.Body();
        var hand = Hand();
        var hips = Hips();
        MotionEvaluator.Validate(Layer(hand));
        MotionEvaluator.Validate(Layer(hips));
        MotionEvaluator.CheckBones([Layer(hand), Layer(hips, key: "m2")], body);
        MotionEvaluator.CheckBones([Layer(hand with { Top = null })], body);

        void Refused(MotionLayer layer, string what) => reject(() => MotionEvaluator.Validate(layer), what);
        Refused(Layer(null), "a link layer without its link is refused");
        Refused(new MotionLayer("m1", MotionLayerKind.Offset, "Offset 1", 0, 30, 0, 0, 1, PoseComponents.All, MotionInterpolation.Linear, Link: hand),
            "a link on an offset layer is refused");
        Refused(Layer(hand with { Bone = "" }), "a link without its bone is refused");
        Refused(Layer(hand with { PartnerBone = "" }), "a link without the partner's bone is refused");
        Refused(Layer(hand) with { Keys = [new MotionKey(0, ImmutableDictionary<string, BoneTransform>.Empty.Add("j_te_r", T(Vector3.Zero)))] },
            "a link layer with keys is refused");
        Refused(Layer(hand with { Holds = [PinChains.FootL] }), "holds on a link that isn't on the hips are refused");
        Refused(Layer(hips with { Holds = [PinChains.FootL, PinChains.FootL] }), "a link holding a bone twice is refused");
        Refused(Layer(hips with { Holds = [""] }), "a link holding a bone without a name is refused");
        Refused(Layer(hand, amount: 1.5f), "a link's pull stops at 100%");
        Refused(Layer(hand with { Bend = float.NaN }), "a link bend that isn't a number is refused");
        Refused(Layer(hand with { Bend = 200 }), "a link bend past a half turn is refused");
        Refused(Layer(hand) with { Pins = [new MotionPin("j_te_r")] }, "a link layer with pins is refused");
        check(MotionEvaluator.BlendsAtMostFully(MotionLayerKind.Link) && MotionEvaluator.Weight(Layer(hand), 15, 30) == 1,
            "a link's pull blends at most fully");

        void Missing(MotionLink link, string what) => reject(() => MotionEvaluator.CheckBones([Layer(link)], body), what);
        Missing(hand with { Bone = "j_nothing" }, "a linked bone the skeleton lacks is refused");
        Missing(hand with { Top = "j_nothing" }, "a link top the skeleton lacks is refused");
        Missing(hand with { Top = "j_asi_a_l" }, "a link top that isn't above the bone is refused");
        Missing(hand with { Top = PinChains.Body }, "the body as a link's top is refused");
        Missing(hand with { Bone = PinChains.Body }, "a link on the body is refused");
        Missing(hand with { Bone = "n_root" }, "a link on the root is refused");
        Missing(hips with { Holds = ["j_nothing"] }, "a held bone the skeleton lacks is refused");
        Missing(hips with { Holds = [PinChains.Hips] }, "a link holding the hips themselves is refused");
        Missing(hips with { Holds = [PinChains.Body] }, "a link holding a bone above the hips is refused");

        var layer = Layer(hand);
        var set = Set(("m1", Still()));
        check(!MotionEvaluator.Active(layer), "a link layer without its partner's track does nothing");
        check(MotionEvaluator.Active(layer, set), "a link layer with its partner's track is active");
        check(!MotionEvaluator.Active(layer, Set(("m9", Still()))) && !MotionEvaluator.Active(layer, MotionLinkSet.Empty),
            "another layer's track doesn't make a link active");
        check(!MotionEvaluator.Active(layer with { Amount = 0 }, set) && !MotionEvaluator.Active(layer with { Enabled = false }, set) &&
              !MotionEvaluator.Active(layer with { Weights = [new MotionWeight(0, 0), new MotionWeight(30, 0)] }, set) &&
              !MotionEvaluator.Active(layer with { Link = null }, set),
            "a link at 0%, off, weighted to 0 all along or without its link does nothing");
        check(MotionLinks.MayChange(layer) && !MotionLinks.MayChange(layer with { Amount = 0 }) && !MotionLinks.MayChange(layer with { Enabled = false }) &&
              !MotionLinks.MayChange(layer with { Weights = [new MotionWeight(0, 0), new MotionWeight(30, 0)] }) &&
              !MotionLinks.MayChange(layer with { Link = null }) &&
              !MotionLinks.MayChange(new MotionLayer("m1", MotionLayerKind.Offset, "Offset 1", 0, 30, 0, 0, 1, PoseComponents.All, MotionInterpolation.Linear)),
            "a link may change the motion when it is on with a pull, without asking for its track");
        var doc = new TimelineDocument(30, 30, null, [], 1) { Motion = [layer] };
        check(!MotionEvaluator.Changes(doc.Motion) && MotionEvaluator.Changes(doc.Motion, set) && !MotionEvaluator.Changes(doc.Motion, null),
            "a document with only a link changes the motion only with its partner's track");
    }

    private static void Tracks(Action<bool, string> check, Action<Action, string> reject)
    {
        MotionTransform[] before = [M(new Vector3(0, 1, 0), 0), M(new Vector3(.3f, 1, 0), 30), M(new Vector3(.3f, 1.6f, 0), 90)];
        MotionTransform[] after = [M(new Vector3(0, 1, .1f), 10), M(new Vector3(.2f, 1, .1f), 40), M(new Vector3(.2f, 1.4f, .1f), 60)];
        var track = new LinkTrack([.. before], [.. after], 2 / 30f);
        check(track.Samples == 3 && Math.Abs(track.Duration - 2 / 30f) < 1e-7f, "a track keeps its samples and duration");
        check(Near(track.At(1), before[1], after[1]), "a track gives its middle sample at frame 1");
        check(Near(track.At(.5f), MotionEvaluator.Lerp(before[0], before[1], .5f), MotionEvaluator.Lerp(after[0], after[1], .5f)),
            "a track goes halfway between its samples at frame 0.5");
        check(Near(track.At(2), before[2], after[2]) && Near(track.At(40), before[2], after[2]), "a track holds its last sample from its end on");
        check(Near(track.At(-1), before[0], after[0]) && Near(track.At(float.NaN), before[0], after[0]), "a track holds its first sample before its start");
        var single = new LinkTrack([before[0]], [after[0]], 0);
        check(Near(single.At(12), before[0], after[0]), "a one-sample track holds it");
        reject(() => _ = new LinkTrack([.. before], [after[0]], 2 / 30f), "a track whose sides differ in length is refused");
        reject(() => _ = new LinkTrack([], [], 0), "a track without samples is refused");

        var set = Set(("m1", track));
        check(MotionLinkSet.Empty.Tracks.IsEmpty && MotionLinkSet.Empty.For("m1") == null && ReferenceEquals(MotionLinkSet.Empty, MotionLinkSet.Empty),
            "the empty set has no tracks");
        check(ReferenceEquals(set.For("m1"), track) && set.For("m2") == null && set.Tracks.Count == 1, "a set finds its tracks by layer key");
    }

    private static void Journals(Action<bool, string> check)
    {
        var temp = Path.Combine(Path.GetTempPath(), "ie-link-layer-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var resource = new AnimationResource("chara/human/c0801/animation/a0001/bt_common/emote/pose06_loop.pap", "pose06_loop.pap", "hash");
            var clip = new AnimationClip(resource.GamePath, "loop", 0, 0, 42, "chara/test.sklb");
            var capture = new AnimationCapture("clip", 1, 1, Guid.NewGuid(), "Test", "Idle", clip, null, [resource.GamePath], [resource], DateTime.UtcNow, true);
            var hips = Layer(Hips() with { HoldTurn = false, Bend = 15 }) with { Weights = [new MotionWeight(10, .5f)], FadeIn = 3 };
            var hand = Layer(Hand() with { Partner = null }, key: "m2") with { Name = "Link 2" };
            var offset = new MotionLayer("m3", MotionLayerKind.Offset, "Offset 1", 0, 30, 0, 0, 1, PoseComponents.All, MotionInterpolation.Linear);
            var request = new AnimationBakeRequest(Guid.NewGuid(), capture, AnimationDestination.NewMod, "Motion", false,
                AnimationOperation.EditTimeline,
                Timeline: new AnimationTimelineEdit(false, 0, null, [hips, hand, offset]));
            new AnimationJournalStore(temp).Save(new AnimationEditJournal { Id = request.Id, Request = AnimationCommitService.RecoveryRequest(request) });
            var loaded = new AnimationJournalStore(temp).Load().Single().Request.Timeline!.Motion;
            check(loaded.Length == 3 && loaded[0].Kind == MotionLayerKind.Link && SameLink(loaded[0].Link, hips.Link) && MotionEdits.Same(loaded[0], hips) &&
                  loaded[1].Kind == MotionLayerKind.Link && SameLink(loaded[1].Link, hand.Link) && loaded[1].Link!.Partner == null &&
                  loaded[0].Link!.Holds.SequenceEqual([PinChains.FootL, PinChains.FootR]) && loaded[2].Link == null,
                "a journal keeps a link layer, its link and its holds");

            var path = Path.Combine(temp, "AnimationEdits", request.Id.ToString("N"), "journal.json");
            var saved = File.ReadAllText(path);
            JsonNode Layers(JsonObject json) => json["Request"]!["Timeline"]!["Motion"]!;
            MotionLayer LoadEdited(Action<JsonObject> edit)
            {
                var json = JsonNode.Parse(saved)!.AsObject();
                edit(json);
                File.WriteAllText(path, json.ToJsonString());
                return new AnimationJournalStore(temp).Load().Single().Request.Timeline!.Motion[0];
            }

            check(Layers(JsonNode.Parse(saved)!.AsObject())[0]!["Kind"]!.GetValue<int>() == 11, "a journal stores a link layer's kind as 11");
            // Journals written before link layers have no Link, and none of their layers is a link.
            var removed = false;
            var older = LoadEdited(json =>
            {
                var layer = Layers(json)[2]!.AsObject();
                removed = layer.Remove("Link");
                Layers(json).AsArray().RemoveAt(1);
                Layers(json).AsArray().RemoveAt(0);
            });
            check(removed && older.Kind == MotionLayerKind.Offset && older.Link == null, "an older journal's offset loads with no link");
            var unlinked = LoadEdited(json => Layers(json)[0]!.AsObject().Remove("Link"));
            var refused = false;
            try { MotionEvaluator.Validate(unlinked); }
            catch (InvalidDataException) { refused = true; }
            check(unlinked.Kind == MotionLayerKind.Link && unlinked.Link == null && refused && !MotionEvaluator.Active(unlinked, Set(("m1", Still()))),
                "a link layer without Link loads with none and is refused");
            // A link written without holds, as a limb link's may be, holds nothing.
            var held = false;
            var unheld = LoadEdited(json => held = Layers(json)[0]!["Link"]!.AsObject().Remove("Holds"));
            check(held && unheld.Link is { Holds: { IsDefault: false, IsEmpty: true } } link && SameLink(link, hips.Link! with { Holds = [] }),
                "a journal's link without Holds loads holding nothing");
        }
        finally { Directory.Delete(temp, true); }
    }

    private static void Solving(Action<bool, string> check)
    {
        // The carry: the partner's move and turn of its bone, applied to this bone as if it were attached to the partner's.
        var spot = new MotionTransform(new Vector3(.2f, 1.1f, -.1f), Q(Vector3.UnitX, 25), Vector3.One);
        var current = new MotionTransform(new Vector3(.4f, 1.1f, -.1f), Q(Vector3.UnitZ, 40), new Vector3(1.1f, 1, 1));
        check(MotionLinks.Aim(current, spot, spot, true) == current && MotionLinks.Aim(current, spot, spot, false) == current,
            "a partner that doesn't move leaves the aim where the bone is");
        var moved = MotionLinks.Aim(current, spot, spot with { Position = spot.Position + new Vector3(.1f, 0, 0) }, true);
        check(Vector3.Distance(moved.Position, current.Position + new Vector3(.1f, 0, 0)) < 1e-6f && Degrees(moved.Rotation, current.Rotation) < .01f &&
              moved.Scale == current.Scale, "a partner moved 10 cm moves the aim 10 cm");
        var turn = Q(Vector3.UnitY, 30);
        var turned = spot with { Rotation = Quaternion.Normalize(turn * spot.Rotation) };
        var held = MotionLinks.Aim(current, spot, turned, true);
        var loose = MotionLinks.Aim(current, spot, turned, false);
        var around = spot.Position + Vector3.Transform(current.Position - spot.Position, turn);
        check(Vector3.Distance(held.Position, around) < 1e-6f && Vector3.Distance(loose.Position, around) < 1e-6f,
            "a partner turned 30° carries the bone round its spot");
        check(Degrees(held.Rotation, Quaternion.Normalize(turn * current.Rotation)) < .01f && Math.Abs(Degrees(held.Rotation, current.Rotation) - 30) < .05f &&
              loose.Rotation == current.Rotation, "holding the turn adds the partner's turn, position only leaves the bone's own");
        check(MotionLinks.AsPin(Hand()) is { Bone: "j_te_r", HoldTurn: true, RelativeTo: null, Top: "j_sako_r", Planted: false, Bend: 30 } pin &&
              pin.Sensitivity == PinSensitivity.Normal, "a link bends its chain as a pin on its bone would");

        var body = HipDragFixture.Body();
        var index = MotionEvaluator.Index(body);
        int hand = index["j_te_r"], forearm = index["j_ude_b_r"], upper = index["j_ude_a_r"];
        var link = new MotionLink("j_te_r", Partner, "j_te_l", "chara/human/c0101/animation/a0001/bt_common/emote/hug.pap", 1);
        var layer = Layer(link);
        var clip = Reaching();
        var chain = PinChains.Chain(body, MotionLinks.AsPin(link));

        var still = Set(("m1", Track(clip, hand, (_, before) => before)));
        check(SameRows(Applied(clip, [layer], still), Applied(clip, [])), "a link whose partner doesn't move leaves every sample exactly as it was");

        // The right arm hangs out along -X, so a move along +X brings the hand in, within the arm's reach.
        var push = new Vector3(.1f, 0, 0);
        var pushed = Set(("m1", Track(clip, hand, (_, before) => before with { Position = before.Position + push })));
        var reached = Applied(clip, [layer], pushed);
        check(All(clip, i => Vector3.Distance(Model(reached, i)[hand].Position, Model(clip, i)[hand].Position + push) < 1e-4f),
            "a partner's hand moved 10 cm moves the hand 10 cm");
        check(All(clip, i => Vector3.Dot(BendNormal(Model(reached, i), chain), BendNormal(Model(clip, i), chain)) > 0), "the elbow keeps its side");
        check(All(clip, i => Math.Abs(Length(Model(reached, i), upper, forearm) - Length(Model(clip, i), upper, forearm)) < 1e-5f &&
                             Math.Abs(Length(Model(reached, i), forearm, hand) - Length(Model(clip, i), forearm, hand)) < 1e-5f),
            "the upper arm and forearm keep their lengths");
        check(All(clip, i => Enumerable.Range(0, clip.Bones).Where(b => b != hand && !chain.Bones.Contains(b)).All(b => reached.Get(i, b) == clip.Get(i, b))),
            "only the chain and the hand are written");
        check(SameRows(Applied(clip, [layer with { Amount = 0 }], pushed), Applied(clip, [])), "a pull of 0 changes nothing");
        var half = Applied(clip, [layer with { Amount = .5f }], pushed);
        check(All(clip, i => Vector3.Distance(Model(half, i)[hand].Position, Model(clip, i)[hand].Position + push * .5f) < 1e-4f),
            "a pull of 50% lands the hand halfway along the straight line");
        var fading = layer with { FadeIn = 10 };
        var faded = Applied(clip, [fading], pushed);
        check(Model(faded, 0)[hand].Position == Model(clip, 0)[hand].Position &&
              All(clip, i => Vector3.Distance(Model(faded, i)[hand].Position, Model(clip, i)[hand].Position + push * MotionEvaluator.Weight(fading, clip.Frame(i), 30)) < 1e-4f) &&
              MotionEvaluator.Weight(fading, 3, 30) is > 0 and < .5f, "a fade-in eases the hand in");

        // The partner turns its hand 20° about Y round a spot 5 cm in front of this hand.
        var swing = Q(Vector3.UnitY, 20);
        var offset = new Vector3(0, 0, .05f);
        var swung = Applied(clip, [layer], Set(("m1", Track(clip, hand,
            (_, before) => before with { Position = before.Position + offset, Rotation = Quaternion.Normalize(swing * before.Rotation) },
            (_, before) => before with { Position = before.Position + offset }))));
        check(All(clip, i =>
        {
            var was = Model(clip, i)[hand];
            var now = Model(swung, i)[hand];
            return Vector3.Distance(now.Position, was.Position + offset + Vector3.Transform(-offset, swing)) < 1e-4f &&
                   Degrees(now.Rotation, Quaternion.Normalize(swing * was.Rotation)) < .01f && Math.Abs(Degrees(now.Rotation, was.Rotation) - 20) < .05f;
        }), "a partner's 20° turn carries the hand round and turns it 20°");

        // Out of reach: on a still arm, the partner's hand goes out along -X up to a metre, over the samples.
        var stand = Reaching(still: true);
        var far = Applied(stand, [layer], Set(("m1", Track(stand, hand, (i, before) => before with { Position = before.Position - new Vector3(i / 30f, 0, 0) }))));
        var reach = Length(Model(stand, 0), upper, forearm) + Length(Model(stand, 0), forearm, hand);
        var shoulder = Model(stand, 0)[upper].Position;
        var smooth = true;
        float? previous = null;
        for (var i = 0; i < stand.Samples; i++)
        {
            var aim = Model(stand, i)[hand].Position - new Vector3(i / 30f, 0, 0);
            var got = Model(far, i)[hand].Position;
            var distance = Vector3.Distance(got, shoulder);
            smooth &= distance <= reach + 1e-5f && (previous == null || distance >= previous - 1e-5f && distance - previous < 1 / 30f + 1e-4f) &&
                      Vector3.Cross(got - shoulder, Vector3.Normalize(aim - shoulder)).Length() < 1e-4f;
            previous = distance;
        }
        check(smooth && previous > .97f * reach, "an aim out of reach straightens the arm and falls short along the line, without a jump");

        // ApplyAt gives Apply's samples: frames 7.5 and 15 are samples 15 and 30 of a clip with two samples a frame.
        var fine = Reaching(61);
        var chest = new MotionLayer("m0", MotionLayerKind.Offset, "Offset 1", 0, 30, 0, 0, 1, PoseComponents.All, MotionInterpolation.Linear,
            Keys: [new MotionKey(0, ImmutableDictionary<string, BoneTransform>.Empty.Add("j_sebo_c", new BoneTransform(Vector3.Zero, Q(Vector3.UnitY, 10), Vector3.One)))]);
        ImmutableArray<MotionLayer> layers = [chest, fading];
        var fineSet = Set(("m1", Track(fine, hand, (_, before) => before with { Position = before.Position + push })));
        var applied = Applied(fine, layers, fineSet);
        check(new[] { 0, 15, 30 }.All(s => MotionEvaluator.ApplyAt(body, Row(fine, s), layers, fine.Frame(s), 0, 30, links: fineSet)
                .Zip(Row(applied, s)).All(pair => Near(pair.First, pair.Second))),
            "ApplyAt gives Apply's samples with the same partner tracks");
        check(Model(applied, 30)[hand].Position != Model(Applied(fine, [chest]), 30)[hand].Position, "the link moves the hand the offset left");

        // No track: the link is left out, the layers round it still apply.
        var after = new MotionLayer("m2", MotionLayerKind.Offset, "Offset 2", 0, 30, 0, 0, 1, PoseComponents.All, MotionInterpolation.Linear,
            Keys: [new MotionKey(0, ImmutableDictionary<string, BoneTransform>.Empty.Add("j_te_r", new BoneTransform(Vector3.Zero, Q(Vector3.UnitX, 15), Vector3.One)))]);
        ImmutableArray<MotionLayer> around3 = [chest, layer, after];
        var without = Applied(fine, [chest, after]);
        check(SameRows(Applied(fine, around3, null), without) && SameRows(Applied(fine, around3, MotionLinkSet.Empty), without) &&
              !SameRows(without, Applied(fine, [])), "a link without its track is left out and the layers round it apply");
        check(new[] { 0f, 7.5f, 15f }.All(frame => MotionEvaluator.ApplyAt(body, fine.Pose(frame), around3, frame, 0, 30)
                  .SequenceEqual(MotionEvaluator.ApplyAt(body, fine.Pose(frame), [chest, after], frame, 0, 30))),
            "ApplyAt without tracks leaves the link out too");

        // A bone right under the body has nothing to bend, as for a pin; the hips follow in their own way (not yet).
        var back = Layer(link with { Bone = "j_sebo_a", PartnerBone = "j_sebo_a" });
        var backSet = Set(("m1", Track(clip, index["j_sebo_a"], (_, before) => before with { Position = before.Position + push })));
        check(SameRows(Applied(clip, [back], backSet), Applied(clip, [])), "a link on a bone right under the body does nothing");
        var hips = Layer(link with { Bone = PinChains.Hips, PartnerBone = PinChains.Hips });
        var hipsSet = Set(("m1", Track(clip, index[PinChains.Hips], (_, before) => before with { Position = before.Position + push })));
        check(SameRows(Applied(clip, [hips], hipsSet), Applied(clip, [])), "a hips link does nothing yet");

        // Symmetry counts what a link may move, with or without its track; paths mark no keys on a link.
        ImmutableArray<short> tracked = [0, 1, 2];
        var animated = MotionSymmetry.Animated(body, tracked, [layer], 1);
        check(animated != null && chain.Bones.All(animated.Contains) && animated.Contains(hand) && animated.Contains(upper),
            "a link's chain and hand count as animated for a later symmetry");
        check(MotionSymmetry.Animated(body, tracked, [layer with { Enabled = false }], 1) is { } off && !off.Contains(hand),
            "a link that is off doesn't count");
        check(MotionLinks.Moved(body, layer).SequenceEqual([upper, forearm, hand]), "a hand link may move the arm and the hand");
        var hipsMoved = MotionLinks.Moved(body, hips with { Link = hips.Link! with { Holds = [PinChains.FootL, PinChains.FootR] } });
        check(hipsMoved.Contains(index[PinChains.Hips]) && hipsMoved.Contains(index[HipSolver.BackBase]) && hipsMoved.Contains(index[PinChains.FootL]) &&
              hipsMoved.Contains(index["j_asi_b_r"]) && !hipsMoved.Contains(index[PinChains.Body]), "a hips link may move the hips, the back and the held legs");
        check(MotionLinks.Moved(body, back).IsEmpty && MotionLinks.Moved(body, chest).IsEmpty, "nothing to bend, or another kind, moves nothing");
        var keyed = layer with { Keys = [new MotionKey(5, ImmutableDictionary<string, BoneTransform>.Empty.Add("j_te_r", T(Vector3.Zero)))] };
        check(MotionPaths.KeyFrames(keyed, "j_te_r", 0).Count == 0, "a link marks no keys on a motion path");
    }

    /// <summary>
    /// <see cref="HipDragFixture.Body"/> over one second in <paramref name="samples"/> samples, the right elbow bent 50° (and with
    /// <paramref name="still"/> off, swinging 10° either way while the chest sways 5°).
    /// </summary>
    private static MotionClip Reaching(int samples = 31, bool still = false)
    {
        var body = HipDragFixture.Body();
        var index = MotionEvaluator.Index(body);
        var poses = Enumerable.Range(0, samples).Select(i =>
        {
            var phase = still ? 0 : 2 * MathF.PI * i / (samples - 1);
            var pose = body.Bones.Select(bone => MotionTransform.From(bone.Reference)).ToArray();
            pose[index["j_sebo_c"]] = pose[index["j_sebo_c"]] with { Rotation = Q(Vector3.UnitY, 5 * MathF.Sin(phase)) };
            pose[index["j_ude_b_r"]] = pose[index["j_ude_b_r"]] with { Rotation = Q(Vector3.UnitY, -50 + 10 * MathF.Sin(phase)) };
            return pose.Select(value => value.ToBone()).ToArray();
        }).ToArray();
        return MotionClip.FromPoses(body, 1, poses);
    }

    /// <summary>
    /// A partner track built from <paramref name="clip"/>'s own <paramref name="bone"/> in model space: Before as the clip has it (or as
    /// <paramref name="from"/> moves it), After as <paramref name="to"/> moves it.
    /// </summary>
    private static LinkTrack Track(MotionClip clip, int bone, Func<int, MotionTransform, MotionTransform> to, Func<int, MotionTransform, MotionTransform>? from = null)
    {
        ImmutableArray<MotionTransform> own = [.. Enumerable.Range(0, clip.Samples).Select(i => Model(clip, i)[bone])];
        return new LinkTrack([.. own.Select((value, i) => from?.Invoke(i, value) ?? value)], [.. own.Select((value, i) => to(i, value))], clip.Duration);
    }

    private static MotionClip Applied(MotionClip clip, ImmutableArray<MotionLayer> layers, MotionLinkSet? links = null)
    {
        var copy = clip.Clone();
        MotionEvaluator.Apply(copy, layers, links: links);
        return copy;
    }

    private static MotionTransform[] Row(MotionClip clip, int sample) => [.. Enumerable.Range(0, clip.Bones).Select(b => clip.Get(sample, b))];

    private static MotionTransform[] Model(MotionClip clip, int sample) => MotionEvaluator.ModelSpace(clip.Skeleton, Row(clip, sample));

    private static bool All(MotionClip clip, Func<int, bool> sample) => Enumerable.Range(0, clip.Samples).All(sample);

    private static bool SameRows(MotionClip a, MotionClip b) => a.Samples == b.Samples && All(a, i => Row(a, i).SequenceEqual(Row(b, i)));

    private static float Length(MotionTransform[] model, int from, int to) => Vector3.Distance(model[from].Position, model[to].Position);

    private static Vector3 BendNormal(MotionTransform[] model, PinChain chain) => Vector3.Normalize(Vector3.Cross(
        model[chain.Mid].Position - model[chain.Root].Position, model[chain.End].Position - model[chain.Root].Position));

    private static float Degrees(Quaternion a, Quaternion b) =>
        2 * MathF.Acos(Math.Min(1, Math.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))))) * 180 / MathF.PI;

    private static MotionLink Hand() => new("j_te_r", Partner, "j_te_l", "chara/human/c0101/animation/a0001/bt_common/emote/hug.pap", 1, Top: "j_sako_r", Bend: 30);

    private static MotionLink Hips() =>
        new(PinChains.Hips, Partner, PinChains.Hips, "chara/human/c0101/animation/a0001/bt_common/emote/hug.pap", 0, Holds: [PinChains.FootL, PinChains.FootR]);

    private static MotionLayer Layer(MotionLink? link, float amount = 1, string key = "m1") =>
        new(key, MotionLayerKind.Link, "Link 1", 0, 30, 0, 0, amount, PoseComponents.None, MotionInterpolation.Linear, Link: link);

    private static MotionLinkSet Set(params (string Key, LinkTrack Track)[] tracks) =>
        new(ImmutableDictionary.CreateRange(StringComparer.Ordinal, tracks.Select(t => KeyValuePair.Create(t.Key, t.Track))));

    /// <summary>A partner that doesn't move: Before and After the same on every sample.</summary>
    private static LinkTrack Still()
    {
        ImmutableArray<MotionTransform> samples = [M(Vector3.UnitY, 0), M(Vector3.UnitY, 0)];
        return new LinkTrack(samples, samples, 1 / 30f);
    }

    /// <summary>The links field by field, holds by sequence (<see cref="MotionEdits.Same"/> doesn't compare links yet).</summary>
    private static bool SameLink(MotionLink? a, MotionLink? b) => a == null || b == null
        ? a == b
        : a.Bone == b.Bone && a.Partner == b.Partner && a.PartnerBone == b.PartnerBone && a.PartnerClip == b.PartnerClip &&
          a.PartnerBinding == b.PartnerBinding && a.HoldTurn == b.HoldTurn && a.Top == b.Top && a.Bend.Equals(b.Bend) && a.Holds.SequenceEqual(b.Holds);

    private static bool Near((MotionTransform Before, MotionTransform After) got, MotionTransform before, MotionTransform after) =>
        Near(got.Before, before) && Near(got.After, after);

    private static bool Near(MotionTransform a, MotionTransform b) =>
        Vector3.Distance(a.Position, b.Position) < 1e-5f && Math.Abs(Quaternion.Dot(Quaternion.Normalize(a.Rotation), Quaternion.Normalize(b.Rotation))) > 1 - 1e-6f &&
        Vector3.Distance(a.Scale, b.Scale) < 1e-5f;

    private static MotionTransform M(Vector3 position, float degrees) => new(position, Q(Vector3.UnitY, degrees), Vector3.One);

    private static Quaternion Q(Vector3 axis, float degrees) => Quaternion.CreateFromAxisAngle(axis, degrees * MathF.PI / 180);

    private static BoneTransform T(Vector3 position) => new(position, Quaternion.Identity, Vector3.One);
}
