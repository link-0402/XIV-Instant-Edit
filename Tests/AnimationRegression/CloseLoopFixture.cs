using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json.Nodes;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui.Timeline;

/// <summary>
/// The Close loop fix layer: blends a looping clip's end into its start after every other layer. Its validation and
/// journals here; the test clip, rig and layer helpers are shared by the later sections.
/// </summary>
internal static class CloseLoopFixture
{
    private const string B1 = "j_b1", B2 = "j_b2";

    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Validation(check, reject);
        Journals(check);
    }

    private static void Validation(Action<bool, string> check, Action<Action, string> reject)
    {
        MotionEvaluator.Validate(CloseLayer(JoinPlacement.After, 8));
        MotionEvaluator.Validate(CloseLayer(JoinPlacement.Before, 8));
        reject(() => MotionEvaluator.Validate(new MotionLayer("m1", MotionLayerKind.Fix, "Smooth jitter", 0, 30, 0, 0, .5f,
            PoseComponents.Rotation | PoseComponents.Position, MotionInterpolation.Smooth,
            Fix: new MotionFix(MotionFixKind.SmoothJitter, Placement: JoinPlacement.After))), "a placement on another fix is refused");
        reject(() => MotionEvaluator.Validate(CloseLayer(JoinPlacement.After, 8) with { Fix = new MotionFix(MotionFixKind.CloseLoop, Placement: (JoinPlacement)7) }),
            "an unknown placement is refused");
        reject(() => MotionEvaluator.Validate(CloseLayer(JoinPlacement.After, 8) with { Fix = new MotionFix(MotionFixKind.CloseLoop, Fade: 4) }),
            "a close loop with a game fade is refused");
        reject(() => MotionEvaluator.Validate(CloseLayer(JoinPlacement.After, 8) with { Fix = new MotionFix(MotionFixKind.CloseLoop, Axes: MotionAxes.Y) }),
            "a close loop with axes is refused");
        check(MotionEvaluator.Active(CloseLayer(JoinPlacement.Centered, 8)) && !MotionEvaluator.Active(CloseLayer(JoinPlacement.Centered, 8) with { Amount = 0 }) &&
              !MotionEvaluator.Active(CloseLayer(JoinPlacement.Centered, 8) with { Components = PoseComponents.Scale }),
            "a close loop is active with an amount and a rotation or position to change");
        check(!MotionEdits.Same(CloseLayer(JoinPlacement.Before, 8), CloseLayer(JoinPlacement.After, 8)) &&
              MotionEdits.Same(CloseLayer(JoinPlacement.After, 8), CloseLayer(JoinPlacement.After, 8)),
            "close loop layers differ by their placement");
    }

    private static void Journals(Action<bool, string> check)
    {
        var temp = Path.Combine(Path.GetTempPath(), "ie-close-loop-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var resource = new AnimationResource("chara/human/c0801/animation/a0001/bt_common/emote/pose06_loop.pap", "pose06_loop.pap", "hash");
            var clip = new AnimationClip(resource.GamePath, "loop", 0, 0, 42, "chara/test.sklb");
            var capture = new AnimationCapture("clip", 1, 1, Guid.NewGuid(), "Test", "Idle", clip, null, [resource.GamePath], [resource],
                new PoseSnapshot(0, 0, 0, false, [], DateTime.UtcNow, ""), DateTime.UtcNow, true);
            var close = CloseLayer(JoinPlacement.After, 12);
            var request = new AnimationBakeRequest(Guid.NewGuid(), capture, AnimationDestination.NewMod, "Motion", false,
                ImmutableHashSet<PoseBoneId>.Empty, PoseComponents.None, AnimationOperation.EditTimeline,
                Timeline: new AnimationTimelineEdit(true, 0, null, [close]));
            new AnimationJournalStore(temp).Save(new AnimationEditJournal { Id = request.Id, Request = AnimationCommitService.RecoveryRequest(request) });
            var loaded = new AnimationJournalStore(temp).Load().Single().Request.Timeline!.Motion;
            check(loaded.Length == 1 && MotionEdits.Same(loaded[0], close) && loaded[0].Fix is { Kind: MotionFixKind.CloseLoop, Placement: JoinPlacement.After } &&
                  loaded[0].End - loaded[0].Start == 12,
                "a journal keeps a close loop's kind, placement and length");

            var path = Path.Combine(temp, "AnimationEdits", request.Id.ToString("N"), "journal.json");
            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            check(json["Request"]!["Timeline"]!["Motion"]![0]!["Fix"]!.AsObject().Remove("Placement"), "a journal stores a fix's placement as Placement");
            File.WriteAllText(path, json.ToJsonString());
            check(new AnimationJournalStore(temp).Load().Single().Request.Timeline!.Motion[0].Fix is { Kind: MotionFixKind.CloseLoop, Placement: JoinPlacement.Centered },
                "a fix saved without a placement loads centered");
        }
        finally { Directory.Delete(temp, true); }
    }

    // A root and two bones under it.
    internal static SkeletonDescription Rig() => new("close-rig", "close-rig-fingerprint",
        [new("n_root", -1, 0, T(Vector3.Zero)), new(B1, 0, 0, T(Vector3.UnitX)), new(B2, 0, 0, T(Vector3.UnitY))], [], [], []);

    /// <summary>
    /// A looping clip of <paramref name="samples"/> samples over <paramref name="frames"/> frames whose last sample equals
    /// its first: b1 turns 360 / <paramref name="frames"/> degrees a frame about Y, b2 sways along Y and back.
    /// </summary>
    internal static MotionClip LoopClip(int samples, float frames)
    {
        var poses = Enumerable.Range(0, samples).Select(i =>
        {
            var f = frames * i / (samples - 1);
            var turn = 360 / frames * f;
            return new[]
            {
                T(Vector3.Zero),
                T(Vector3.UnitX, Quaternion.CreateFromAxisAngle(Vector3.UnitY, turn * MathF.PI / 180)),
                T(new Vector3(0, 1 + .1f * MathF.Sin(2 * MathF.PI * f / frames), 0)),
            };
        }).ToArray();
        // The last sample is the first, exactly, as a looping clip has it.
        poses[^1] = poses[0];
        var made = MotionClip.FromPoses(Rig(), frames / TimelineDocument.Fps, poses);
        var clip = new MotionClip(Rig(), made.Duration, made.Samples) { Loops = true };
        clip.CopyFrom(made);
        return clip;
    }

    /// <summary>A Close loop layer of <paramref name="frames"/> frames ending on frame 30.</summary>
    internal static MotionLayer CloseLayer(JoinPlacement place, int frames, float amount = 1) =>
        new("c1", MotionLayerKind.Fix, "Close loop", 30 - frames, 30, 0, 0, amount, PoseComponents.Rotation | PoseComponents.Position,
            MotionInterpolation.Smooth, Fix: new MotionFix(MotionFixKind.CloseLoop, Placement: place));

    private static BoneTransform T(Vector3 position, Quaternion? rotation = null) => new(position, rotation ?? Quaternion.Identity, Vector3.One);
}
