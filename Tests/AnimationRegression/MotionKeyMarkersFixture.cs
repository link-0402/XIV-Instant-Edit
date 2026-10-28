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
