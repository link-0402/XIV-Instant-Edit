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
