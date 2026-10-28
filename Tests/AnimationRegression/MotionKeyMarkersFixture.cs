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
    private const string B1 = "j_b1";

    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Data(check, reject);
    }

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
