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
        Apply(check);
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

    private static void Apply(Action<bool, string> check)
    {
        var plain = LoopClip(61, 30);
        var b1 = Index(B1);
        check(plain.Loops && plain.Samples == 61 && LoopSeam.Frames(plain) == 30 &&
              Enumerable.Range(0, plain.Bones).All(b => plain.Get(60, b) == plain.Get(0, b)) &&
              MathF.Abs(Degrees(plain.Get(2, b1).Rotation, plain.Get(0, b1).Rotation) - 12) < .01f,
            "the test loop: 61 samples over 30 frames, b1 turning 12 degrees a frame, its last sample its first");

        check(MotionFixes.CloseSpans(CloseLayer(JoinPlacement.Centered, 9), 30) == ((25, 30), (0, 4)) &&
              MotionFixes.CloseSpans(CloseLayer(JoinPlacement.After, 8), 30) == ((30, 30), (0, 8)) &&
              MotionFixes.CloseSpans(CloseLayer(JoinPlacement.Before, 8), 30) == ((22, 30), (0, 0)),
            "close spans: centered splits the length, the end taking the odd frame; after changes only the start, before only the end");

        // The offset ramps b1 20 degrees about X over frames 10 to 30: the end pops and turns on X, the start doesn't.
        var turned = Applied(plain, Turn());
        var closed = Applied(plain, Turn(), CloseLayer(JoinPlacement.Centered, 8));
        var (open, shut) = (Gap(turned), Gap(closed));
        var typical = LoopSeam.Analyze(closed, 0, LoopSeam.Frames(closed)).TypicalSpeed;
        check(open.MeanAngle >= LoopSeam.PopDegrees && shut.MeanAngle < .01f && MathF.Abs(shut.SpeedOut - shut.SpeedIn) < .05f * typical,
            $"close loop: a popping seam closes ({open.MeanAngle:0.###} to {shut.MeanAngle:0.####} degrees, speed jump {MathF.Abs(shut.SpeedOut - shut.SpeedIn):0.####} of {typical:0.###})");
        var (openKink, shutKink) = (Kink(turned, b1), Kink(closed, b1));
        check(openKink > .5f && shutKink < .05f * 12,
            $"close loop speed: b1 leaves the end at the speed it leaves the start ({openKink:0.###} to {shutKink:0.####} degrees a frame apart)");

        // A longer blend bends less near the seam, so what's left of the jump there is the speed gap's own.
        var longKinks = Enum.GetValues<JoinPlacement>().Select(place => Kink(Applied(plain, Turn(), CloseLayer(place, 24)), b1)).ToArray();
        check(longKinks.All(kink => kink < .1f),
            $"close loop speed, long: every placement matches the speeds ({string.Join(", ", longKinks.Select(kink => kink.ToString("0.####")))} degrees a frame apart)");

        foreach (var place in Enum.GetValues<JoinPlacement>())
        {
            var layer = CloseLayer(place, 8);
            var (endSpan, startSpan) = MotionFixes.CloseSpans(layer, 30);
            var placed = Applied(plain, Turn(), layer);
            var outside = Enumerable.Range(0, placed.Samples - 1).Where(i => !Inside(placed.Frame(i), endSpan) && !Inside(placed.Frame(i), startSpan)).ToArray();
            var inside = Enumerable.Range(0, placed.Samples - 1).Except(outside);
            check(outside.Length > 30 && outside.All(i => Enumerable.Range(0, placed.Bones).All(b => placed.Get(i, b) == turned.Get(i, b))) &&
                  inside.Any(i => placed.Get(i, b1) != turned.Get(i, b1)) && Gap(placed).MeanAngle < .01f,
                $"close loop {place}: it closes and changes only the samples in its windows");
        }

        var half = Gap(Applied(plain, Turn(), CloseLayer(JoinPlacement.Centered, 8, .5f)));
        check(MathF.Abs(half.MeanAngle - open.MeanAngle / 2) < .05f * open.MeanAngle / 2,
            $"close loop half: 50% leaves half the gap ({half.MeanAngle:0.###} of {open.MeanAngle:0.###} degrees)");
        var single = Gap(Applied(plain, Turn(), CloseLayer(JoinPlacement.Centered, 1, .5f)));
        check(MotionFixes.CloseSpans(CloseLayer(JoinPlacement.Centered, 1), 30) == ((29, 30), (0, 0)) &&
              MathF.Abs(single.MeanAngle - open.MeanAngle / 2) < .05f * open.MeanAngle / 2,
            $"close loop one frame: a centered blend with no start frames gives the end all of it ({single.MeanAngle:0.###} degrees left)");

        var once = Once(plain);
        check(Bits(Applied(once, Turn(), CloseLayer(JoinPlacement.Centered, 8)), Applied(once, Turn())),
            "close loop plays once: on a clip the game doesn't loop it changes nothing");
        var loop = FixLayerFixture.Loop();
        var match = new MotionLayer("m1", MotionLayerKind.Fix, "Match loop", 22, 30, 0, 0, 1, PoseComponents.Rotation | PoseComponents.Position,
            MotionInterpolation.Smooth, Fix: loop);
        var matched = Applied(once, match, CloseLayer(JoinPlacement.Centered, 8));
        var landed = matched.Get(matched.Samples - 1, b1);
        check(Degrees(landed.Rotation, loop.LoopFirst![B1].Rotation) < .01f && Vector3.Distance(landed.Position, loop.LoopFirst[B1].Position) < 1e-4f &&
              Bits(matched, Applied(once, match)),
            "close loop on a startup: the match lands and the close loop changes nothing");

        // Review focus 1: a blend longer than the clip.
        var tiny = LoopClip(2, 1);
        var shortClip = LoopClip(5, 4);
        var fits = Enum.GetValues<JoinPlacement>().All(place =>
        {
            var layer = CloseLayer(place, 8) with { Start = 0, End = 500 };
            Applied(tiny, Turn(), layer);
            var shortClosed = Applied(shortClip, layer);
            return Within(MotionFixes.CloseSpans(layer, 1), 1) && Within(MotionFixes.CloseSpans(layer, 4), 4) &&
                   Within(MotionFixes.CloseSpans(layer, 0), 0) && Gap(shortClosed).MeanAngle < .01f;
        });
        check(fits, "close loop long: a blend longer than the clip keeps its windows on the clip, and a 2-sample loop doesn't throw");

        // Review focus 2: a loop already closed moves nowhere.
        var still = Applied(plain, CloseLayer(JoinPlacement.Centered, 8));
        var reference = Applied(plain);
        check(Enumerable.Range(0, still.Samples).All(i => Enumerable.Range(0, still.Bones).All(b =>
                  Degrees(still.Get(i, b).Rotation, reference.Get(i, b).Rotation) < .01f &&
                  Vector3.Distance(still.Get(i, b).Position, reference.Get(i, b).Position) < 1e-5f)),
            "close loop closed: a loop whose end already runs into its start is left as it is");

        // Review focus 4: an end 170 degrees round from the start closes the short way.
        var wide = Clip([.. Enumerable.Range(0, 31).Select(i => new[] { T(Vector3.Zero), T(Vector3.UnitX, Q(Vector3.UnitY, 170 * i / 30f)), T(Vector3.UnitY) })], 30);
        var unfixed = Applied(wide);
        foreach (var place in Enum.GetValues<JoinPlacement>())
        {
            var wideClosed = Applied(wide, CloseLayer(place, 8));
            var most = Enumerable.Range(0, wide.Samples).Max(i => Degrees(wideClosed.Get(i, b1).Rotation, unfixed.Get(i, b1).Rotation));
            check(Gap(wideClosed).MeanAngle < .01f && most <= 175,
                $"close loop wide {place}: a 170-degree gap closes the short way (turns {most:0.#} degrees at most)");
        }
    }

    /// <summary>An offset ramping b1 from no turn on frame 10 to 20 degrees about X on frame 30, without fades.</summary>
    private static MotionLayer Turn() =>
        new("o1", MotionLayerKind.Offset, "Offset", 10, 30, 0, 0, 1, PoseComponents.Rotation, MotionInterpolation.Linear, [],
        [
            new MotionKey(10, ImmutableDictionary.CreateRange(StringComparer.Ordinal, [KeyValuePair.Create(B1, T(Vector3.Zero))])),
            new MotionKey(30, ImmutableDictionary.CreateRange(StringComparer.Ordinal, [KeyValuePair.Create(B1, T(Vector3.Zero, Q(Vector3.UnitX, 20)))])),
        ]);

    /// <summary>The seam's jump from the clip's last frame into its first.</summary>
    private static JoinGap Gap(MotionClip clip) => LoopSeam.Gap(clip, LoopSeam.Frames(clip), 0);

    /// <summary>
    /// How far apart, in degrees a frame, <paramref name="bone"/>'s turning speed leaving the first sample and arriving at the
    /// last are: the speed jump at the seam as a vector, not just its size. Each speed is taken from the two sample pairs
    /// beside the seam (a second-order one-sided difference), so the motion's own curving there doesn't count.
    /// </summary>
    private static float Kink(MotionClip clip, int bone)
    {
        Vector3 Spin(int from) => MotionCutter.Log(clip.Get(from + 1, bone).Rotation * Quaternion.Inverse(clip.Get(from, bone).Rotation)) /
                                  (clip.Frame(from + 1) - clip.Frame(from));
        var last = clip.Samples - 2;
        return (1.5f * Spin(0) - .5f * Spin(1) - (1.5f * Spin(last) - .5f * Spin(last - 1))).Length() * 180 / MathF.PI;
    }

    /// <summary>Whether a frame lies on a span that changes any frames.</summary>
    private static bool Inside(float frame, (int Start, int End) span) =>
        span.End > span.Start && frame >= span.Start - MotionEvaluator.FrameTolerance && frame <= span.End + MotionEvaluator.FrameTolerance;

    /// <summary>Whether both of a close's spans run forward and lie on a clip <paramref name="frames"/> frames long.</summary>
    private static bool Within(((int Start, int End) End, (int Start, int End) Start) spans, int frames) =>
        spans.End.Start >= 0 && spans.End.Start <= spans.End.End && spans.End.End <= frames &&
        spans.Start.Start >= 0 && spans.Start.Start <= spans.Start.End && spans.Start.End <= frames;

    private static MotionClip Applied(MotionClip source, params MotionLayer[] layers)
    {
        var clip = source.Clone();
        MotionEvaluator.Apply(clip, layers.Select((layer, i) => layer with { Key = "m" + (i + 1) }).ToArray());
        return clip;
    }

    /// <summary>A copy of <paramref name="clip"/> the game plays once.</summary>
    private static MotionClip Once(MotionClip clip)
    {
        var once = new MotionClip(clip.Skeleton, clip.Duration, clip.Samples) { Loops = false };
        once.CopyFrom(clip);
        return once;
    }

    private static bool Bits(MotionClip a, MotionClip b) => a.Samples == b.Samples &&
        Enumerable.Range(0, a.Samples).All(i => Enumerable.Range(0, a.Bones).All(bone => a.Get(i, bone) == b.Get(i, bone)));

    private static int Index(string bone) => MotionEvaluator.Index(Rig())[bone];

    private static Quaternion Q(Vector3 axis, float degrees) => Quaternion.CreateFromAxisAngle(axis, degrees * MathF.PI / 180);

    private static float Degrees(Quaternion a, Quaternion b) =>
        2 * MathF.Acos(Math.Clamp(Math.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))), 0, 1)) * 180 / MathF.PI;

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
        return Clip(poses, frames);
    }

    /// <summary>A looping clip of <paramref name="poses"/> over <paramref name="frames"/> frames.</summary>
    private static MotionClip Clip(BoneTransform[][] poses, float frames)
    {
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
