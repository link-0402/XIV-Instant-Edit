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
/// Sway layers: a five-bone tail pointing back from the hips, 8 cm a segment, swung from its top bone and following with
/// momentum: its drive, follow-through, inertia, hang and bounce, its seams and ends, its validation, journals and edits.
/// </summary>
internal static class SwayLayerFixture
{
    private const string Hips = "j_kosi", A = "n_sippo_a", B = "n_sippo_b", C = "n_sippo_c", D = "n_sippo_d", E = "n_sippo_e";
    private const float Segment = .08f;
    private static readonly string[] Tail = [A, B, C, D, E];

    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Validation(check, reject);
        Journals(check);
        Still(check);
        Drive(check);
        FollowThrough(check);
        Inertia(check);
        Hang(check);
        Bounce(check);
        Loop(check);
        Ends(check);
        Pose(check);
    }

    private static void Validation(Action<bool, string> check, Action<Action, string> reject)
    {
        var sway = Sway(new MotionSway(), Tail);
        MotionEvaluator.Validate(sway);
        MotionEvaluator.Validate(Sway(new MotionSway(1, -180, 0, MotionSways.MinStiffness, 0, 0), A));
        MotionEvaluator.Validate(Sway(new MotionSway(MotionSways.MaxCount, 180, MotionSways.MaxAngle, 1, MotionSways.MaxBounce, 1), A));
        void Refused(MotionLayer layer, string what) => reject(() => MotionEvaluator.Validate(layer), what);
        Refused(Sway(new MotionSway(Count: 0), A), "a sway without a swing is refused");
        Refused(Sway(new MotionSway(Count: 65), A), "a sway of over 64 swings is refused");
        Refused(Sway(new MotionSway(Direction: 180.1f), A), "a sway direction past 180 degrees is refused");
        Refused(Sway(new MotionSway(Angle: -.1f), A), "a negative sway angle is refused");
        Refused(Sway(new MotionSway(Angle: 60.1f), A), "a sway angle over 60 degrees is refused");
        Refused(Sway(new MotionSway(Stiffness: .04f), A), "a stiffness under 5% is refused");
        Refused(Sway(new MotionSway(Stiffness: 1.01f), A), "a stiffness over 100% is refused");
        Refused(Sway(new MotionSway(Bounce: -.01f), A), "a negative sway bounce is refused");
        Refused(Sway(new MotionSway(Bounce: .96f), A), "a sway bounce over 95% is refused");
        Refused(Sway(new MotionSway(Hang: -.01f), A), "a negative hang is refused");
        Refused(Sway(new MotionSway(Hang: 1.01f), A), "a hang over 100% is refused");
        Refused(Sway(new MotionSway(Stiffness: float.NaN), A), "a stiffness that isn't a number is refused");
        Refused(Sway(new MotionSway(Direction: float.PositiveInfinity), A), "a sway direction that isn't finite is refused");
        Refused(new MotionLayer("m1", MotionLayerKind.Offset, "Offset 1", 0, 60, 0, 0, 1, PoseComponents.All, MotionInterpolation.Linear,
            Sway: new MotionSway()), "a sway on an offset layer is refused");
        Refused(sway with { Sway = null }, "a sway layer without its sway is refused");
        Refused(sway with { Keys = [new MotionKey(0, ImmutableDictionary<string, BoneTransform>.Empty.Add(A, T(Vector3.Zero)))] },
            "a sway layer with keys is refused");
        Refused(sway with { Pins = [new MotionPin(A)] }, "a sway layer with pins is refused");
        Refused(sway with { Bones = ["n_root", A] }, "a sway on the root is refused");
        Refused(sway with { Bones = [PinChains.Body] }, "a sway on the body is refused");
        Refused(sway with { Amount = 1.5f }, "a sway past full strength is refused");
        check(MotionEvaluator.BlendsAtMostFully(MotionLayerKind.Sway), "a sway blends at most fully");

        check(MotionEvaluator.Active(sway) && MotionEvaluator.Active(Sway(new MotionSway(Angle: 0, Hang: 0), A)),
            "a sway with bones is active, even without a swing or a hang: it still follows the motion");
        check(!MotionEvaluator.Active(sway with { Bones = [] }), "a sway without bones does nothing");
        check(!MotionEvaluator.Active(sway with { Amount = 0 }), "a sway at 0% does nothing");
        check(!MotionEvaluator.Active(sway with { Components = PoseComponents.Position }), "a sway without Turn does nothing");
        check(MathF.Abs(MotionSways.Frequency(new MotionSway(Stiffness: .05f)) - .566f) < .01f && MathF.Abs(MotionSways.Frequency(new MotionSway()) - 1.354f) < .01f &&
              MathF.Abs(MotionSways.Frequency(new MotionSway(Stiffness: 1)) - 6) < 1e-4f && MathF.Abs(MotionSways.Constants(new MotionSway(Bounce: .3f)).Zeta - .7f) < 1e-6f,
            "a sway swings at 0.5 × 12^Stiffness a second and its damping is 1 - bounce");
    }

    private static void Journals(Action<bool, string> check)
    {
        var temp = Path.Combine(Path.GetTempPath(), "ie-sway-layer-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var resource = new AnimationResource("chara/human/c0801/animation/a0001/bt_common/resident/idle.pap", "idle.pap", "hash");
            var clip = new AnimationClip(resource.GamePath, "idle", 0, 0, 42, "chara/test.sklb");
            var capture = new AnimationCapture("clip", 1, 1, Guid.NewGuid(), "Test", "Idle", clip, null, [resource.GamePath], [resource], DateTime.UtcNow, true);
            var settings = new MotionSway(3, 45, 20, .6f, .5f, .25f);
            var sway = Sway(settings, A, B) with { Weights = [new MotionWeight(10, .5f)] };
            var offset = new MotionLayer("m2", MotionLayerKind.Offset, "Offset 1", 0, 60, 0, 0, 1, PoseComponents.All, MotionInterpolation.Linear);
            var request = new AnimationBakeRequest(Guid.NewGuid(), capture, AnimationDestination.NewMod, "Motion", false,
                AnimationOperation.EditTimeline, Timeline: new AnimationTimelineEdit(true, 0, null, [sway, offset]));
            new AnimationJournalStore(temp).Save(new AnimationEditJournal { Id = request.Id, Request = AnimationCommitService.RecoveryRequest(request) });
            var loaded = new AnimationJournalStore(temp).Load().Single().Request.Timeline!.Motion;
            check(loaded.Length == 2 && MotionEdits.Same(loaded[0], sway) && loaded[0].Sway == settings && loaded[0].Spring == null &&
                  MotionEdits.Same(loaded[1], offset), "a journal keeps a sway's settings");

            var path = Path.Combine(temp, "AnimationEdits", request.Id.ToString("N"), "journal.json");
            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var motion = json["Request"]!["Timeline"]!["Motion"]!;
            check(motion[0]!.AsObject().Remove("Sway"), "a journal stores a layer's sway as Sway");
            motion[1]!.AsObject().Remove("Sway");
            File.WriteAllText(path, json.ToJsonString());
            var older = new AnimationJournalStore(temp).Load().Single().Request.Timeline!.Motion;
            var refused = false;
            try { MotionEvaluator.Validate(older[0]); }
            catch (InvalidDataException) { refused = true; }
            check(older[0].Sway == null && refused && !MotionEvaluator.Active(older[0]), "a sway layer without Sway loads with none and is refused");
            check(MotionEdits.Same(older[1], offset), "a journal written before sways loads its other layers unchanged");
        }
        finally { Directory.Delete(temp, true); }

        var a = Sway(new MotionSway(), A);
        check(MotionEdits.Same(a, a with { Sway = new MotionSway() }) && !MotionEdits.Same(a, a with { Sway = new MotionSway(3) }) &&
              !MotionEdits.Same(a, a with { Sway = new MotionSway(Stiffness: .5f) }) && !MotionEdits.Same(a, a with { Sway = null }),
            "layers differ by their sway's settings");
    }

    private static void Still(Action<bool, string> check)
    {
        // A tail bent down at its second bone, held still: with nothing to swing and no hang, the sway leaves every sample as it was.
        var still = Clip(61, 60, (_, _) => Pose(bend: 20));
        var swayed = Applied(still, Sway(new MotionSway(Angle: 0, Hang: 0), Tail));
        check(Enumerable.Range(0, still.Samples).All(i => Enumerable.Range(0, still.Bones).All(b => swayed.Get(i, b) == still.Get(i, b))),
            "a sway without swing or hang on a still tail changes nothing, not even by rounding");
    }

    private static void Drive(Action<bool, string> check)
    {
        var still = Clip(121, 120, (_, _) => Pose());
        var swayed = Applied(still, Sway(new MotionSway(2, 0, 15, 1, .3f, 0), Tail) with { End = 120 });
        var top = Enumerable.Range(0, 121).Max(i => MathF.Abs(Side(swayed, i, 0)));
        var tip = Enumerable.Range(0, 121).Max(i => MathF.Abs(Side(swayed, i, 4)));
        check(MathF.Abs(top - 15) < 2, $"a stiff sway swings its top bone the set angle each way ({top:0.##} degrees at most)");
        check(tip >= top - .1f, $"the tip of a stiff sway swings at least as far as its top ({tip:0.##} against {top:0.##} degrees)");
        check(Enumerable.Range(0, 121).Max(i => Side(swayed, i, 0)) > 13 && Enumerable.Range(0, 121).Min(i => Side(swayed, i, 0)) < -13,
            "a sway swings both ways, side to side at Direction 0");
        // Only turns are written: every segment keeps its length.
        var stretch = Enumerable.Range(0, 121).Max(i => Enumerable.Range(0, 5).Max(k => MathF.Abs(Length(swayed, i, k) - Length(still, i, k))));
        check(stretch < 1e-4f, $"a sway keeps the motion's segment lengths ({stretch * 1000:0.####} mm off at most)");
        // The top bone alone, its tip a bone it doesn't sway, and two chains with a gap, the lower one riding on the upper one's swing.
        var alone = Applied(still, Sway(new MotionSway(2, 0, 15, 1, .3f, 0), A) with { End = 120 });
        var aloneTop = Enumerable.Range(0, 121).Max(i => MathF.Abs(Side(alone, i, 0)));
        check(MathF.Abs(aloneTop - 15) < 2 && Enumerable.Range(0, 121).All(i => Enumerable.Range(1, 4).All(k => MathF.Abs(Side(alone, i, k) - Side(alone, i, 0)) < .01f)),
            $"a sway on the top bone alone swings it and the bones under it ride along ({aloneTop:0.##} degrees at most)");
        var gap = Applied(still, Sway(new MotionSway(2, 0, 15, 1, .3f, 0), A, C) with { End = 120 });
        var gapLower = Enumerable.Range(0, 121).Max(i => MathF.Abs(Side(gap, i, 2)));
        check(gapLower > 20 && Enumerable.Range(0, 121).All(i => Enumerable.Range(0, gap.Bones).All(b => float.IsFinite(gap.Get(i, b).Rotation.W))),
            $"a second chain under an unswayed bone swings by itself on top of the first's swing ({gapLower:0.##} degrees at most)");
    }

    private static void FollowThrough(Action<bool, string> check)
    {
        // One swing over four seconds: the end of the tail reaches furthest to the side after the top bone's tip does, and later on a softer tail.
        var still = Clip(121, 120, (_, _) => Pose());
        int Peak(MotionClip clip, int k) => Enumerable.Range(0, 121).MaxBy(i => Tip(clip, i, k).X);
        var soft = Applied(still, Sway(new MotionSway(1, 0, 15, .2f, .3f, 0), Tail) with { End = 120 });
        var firm = Applied(still, Sway(new MotionSway(1, 0, 15, .8f, .3f, 0), Tail) with { End = 120 });
        var (softTop, softTip, firmTip) = (Peak(soft, 0), Peak(soft, 4), Peak(firm, 4));
        check(softTip > softTop && softTip > firmTip,
            $"a sway's tip follows through after its top, later when softer (top {softTop}, tip {softTip}, firm tip {firmTip})");
    }

    private static void Inertia(Action<bool, string> check)
    {
        // The hips step 5 cm to the side at frame 20: the tail is left behind, swings the other way first and settles back.
        var step = Clip(121, 120, (_, f) => Pose(shift: f >= 20 ? .05f : 0));
        var swayed = Applied(step, Sway(new MotionSway(Angle: 0, Hang: 0), Tail) with { End = 120 });
        var first = Enumerable.Range(21, 5).Min(i => Side(swayed, i, 0));
        var settled = Enumerable.Range(0, 5).Max(k => MathF.Abs(Side(swayed, 120, k)));
        check(first < -5 && Side(swayed, 19, 0) == 0, $"a tail left behind by the hips swings the other way first ({first:0.#} degrees)");
        check(settled < 1, $"a tail left behind by the hips settles back ({settled:0.###} degrees off at the end)");
    }

    private static void Hang(Action<bool, string> check)
    {
        var still = Clip(61, 60, (_, _) => Pose());
        var level = Applied(still, Sway(new MotionSway(Angle: 0, Hang: 0), Tail));
        var hung = Applied(still, Sway(new MotionSway(Angle: 0, Hang: .5f), Tail));
        var (levelTip, hungTip) = (Tip(level, 60, 4).Y, Tip(hung, 60, 4).Y);
        check(hungTip < levelTip - .01f, $"a hanging tail droops ({(levelTip - hungTip) * 100:0.#} cm lower at its tip)");
    }

    private static void Bounce(Action<bool, string> check)
    {
        var step = Clip(121, 120, (_, f) => Pose(shift: f >= 20 ? .05f : 0));
        var calm = Applied(step, Sway(new MotionSway(Angle: 0, Bounce: 0, Hang: 0), Tail) with { End = 120 });
        var bouncy = Applied(step, Sway(new MotionSway(Angle: 0, Bounce: .8f, Hang: 0), Tail) with { End = 120 });
        var calmBack = Enumerable.Range(20, 101).Max(i => Side(calm, i, 0));
        var bouncyBack = Enumerable.Range(20, 101).Max(i => Side(bouncy, i, 0));
        check(calmBack < .05f, $"a sway without bounce settles after a step without crossing back ({calmBack:0.###} degrees past)");
        check(bouncyBack > .5f, $"a bouncy sway crosses back after a step ({bouncyBack:0.##} degrees past)");
    }

    private static void Loop(Action<bool, string> check)
    {
        // The hips sway a little round a loop, under the default sway: the seam meets itself.
        var loop = Clip(121, 120, (_, f) => Pose(shift: .02f * MathF.Sin(2 * MathF.PI * f / 120)), loops: true);
        var swayed = Applied(loop, Sway(new MotionSway(), Tail));
        var seam = Tail.Max(bone => Degrees(swayed.Get(0, Index(bone)).Rotation, swayed.Get(120, Index(bone)).Rotation));
        check(seam < .1f && Enumerable.Range(0, 121).Max(i => MathF.Abs(Side(swayed, i, 0))) > 5,
            $"a sway round a loop meets itself at the seam ({seam:0.###} degrees apart)");
    }

    private static void Ends(Action<bool, string> check)
    {
        var loop = Clip(121, 120, (_, _) => Pose(), loops: true);
        // One starting later doesn't wrap: it starts on the motion and eases back onto it by the loop's end.
        var late = Applied(loop, Sway(new MotionSway(), Tail) with { Start = 20 });
        var startOff = Tail.Max(bone => Degrees(late.Get(20, Index(bone)).Rotation, loop.Get(20, Index(bone)).Rotation));
        var endOff = Tail.Max(bone => Degrees(late.Get(120, Index(bone)).Rotation, loop.Get(120, Index(bone)).Rotation));
        check(startOff < 1e-3f && endOff < 1e-3f && Enumerable.Range(20, 101).Max(i => MathF.Abs(Side(late, i, 0))) > 5,
            $"a sway that doesn't wrap starts and ends on the motion ({startOff:0.####} and {endOff:0.####} degrees off)");
        // One ending before the clip does is back on the motion from its end.
        var still = Clip(121, 120, (_, _) => Pose());
        var early = Applied(still, Sway(new MotionSway(), Tail) with { End = 80 });
        var after = Enumerable.Range(80, 41).Max(i => Tail.Max(bone => Degrees(early.Get(i, Index(bone)).Rotation, still.Get(i, Index(bone)).Rotation)));
        check(after < 1e-3f && Tail.All(bone => early.Get(0, Index(bone)) == still.Get(0, Index(bone))) &&
              Enumerable.Range(0, 80).Max(i => MathF.Abs(Side(early, i, 0))) > 5,
            $"a sway ending before the clip leaves the motion as it was from its end ({after:0.####} degrees off), and starts on it");
    }

    private static void Pose(Action<bool, string> check)
    {
        var rig = Rig();
        var still = Clip(61, 60, (_, _) => Pose());
        var layer = Sway(new MotionSway(), Tail);
        var local = Enumerable.Range(0, still.Bones).Select(b => still.Get(25, b)).ToArray();
        var at = MotionEvaluator.ApplyAt(rig, local, [layer], 25, 0, 60);
        check(Enumerable.Range(0, still.Bones).All(b => at[b] == local[b]) && BonePosing.TakesWholeClip([layer]),
            "one pose leaves a sway out, so the Bones window starts from the bake");
        var index = MotionEvaluator.Index(rig);
        var physics = ImmutableHashSet.Create(StringComparer.Ordinal, B);
        check(TimelineWindowViews.SpringRefuses(B, rig, index, physics) && !TimelineWindowViews.SpringRefuses(A, rig, index, physics) &&
              BonePosing.StateText(BoneState.Changed, layer) == "This layer sways its bones." && MotionPaths.KeyFrames(layer, A, 0).Count == 0,
            "the sway picker leaves out the bones the game's physics moves, and its bones show as changed, never keyed");
    }

    /// <summary>The test rig: the root, the body, the hips and a five-bone tail pointing back from them, level.</summary>
    private static SkeletonDescription Rig() => new("sway-rig", "sway-rig-fingerprint",
    [
        new SkeletonBone("n_root", -1, 0, T(Vector3.Zero)),
        new SkeletonBone("n_hara", 0, 0, T(new Vector3(0, 1, 0))),
        new SkeletonBone(Hips, 1, 0, T(Vector3.Zero)),
        new SkeletonBone(A, 2, 0, T(new Vector3(0, 0, -Segment))),
        new SkeletonBone(B, 3, 0, T(new Vector3(0, 0, -Segment))),
        new SkeletonBone(C, 4, 0, T(new Vector3(0, 0, -Segment))),
        new SkeletonBone(D, 5, 0, T(new Vector3(0, 0, -Segment))),
        new SkeletonBone(E, 6, 0, T(new Vector3(0, 0, -Segment))),
    ], [], [], []);

    /// <summary>The rig's rest pose, the hips moved <paramref name="shift"/> metres to the side and the tail bent down <paramref name="bend"/> degrees at its second bone.</summary>
    private static BoneTransform[] Pose(float shift = 0, float bend = 0) =>
    [
        .. Rig().Bones.Select(bone => bone.Name switch
        {
            Hips => T(bone.Reference.Position + new Vector3(shift, 0, 0)),
            B => T(bone.Reference.Position, Q(Vector3.UnitX, bend)),
            _ => bone.Reference,
        }),
    ];

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

    private static int Index(string bone) => MotionEvaluator.Index(Rig())[bone];

    private static MotionTransform[] Model(MotionClip clip, int sample) =>
        MotionEvaluator.ModelSpace(Rig(), Enumerable.Range(0, clip.Bones).Select(b => clip.Get(sample, b)).ToArray());

    /// <summary>Where tail segment <paramref name="k"/> (0 the top) ends at <paramref name="sample"/>: the next bone, or past the last as far as the segments are long.</summary>
    private static Vector3 Tip(MotionClip clip, int sample, int k)
    {
        var model = Model(clip, sample);
        if (k < Tail.Length - 1)
            return model[Index(Tail[k + 1])].Position;
        var last = model[Index(Tail[k])];
        return last.Position + Vector3.Transform(new Vector3(0, 0, -Segment), last.Rotation);
    }

    private static Vector3 Head(MotionClip clip, int sample, int k) => Model(clip, sample)[Index(Tail[k])].Position;

    /// <summary>How far tail segment <paramref name="k"/> leans toward the character's left (+X) at <paramref name="sample"/>, in degrees.</summary>
    private static float Side(MotionClip clip, int sample, int k) =>
        MathF.Asin(Math.Clamp(Vector3.Normalize(Tip(clip, sample, k) - Head(clip, sample, k)).X, -1, 1)) * 180 / MathF.PI;

    private static float Length(MotionClip clip, int sample, int k) => Vector3.Distance(Tip(clip, sample, k), Head(clip, sample, k));

    private static Quaternion Q(Vector3 axis, float degrees) => Quaternion.CreateFromAxisAngle(axis, degrees * MathF.PI / 180);

    /// <summary>The angle between two turns in degrees, read from the turn's vector part so small angles stay precise.</summary>
    private static float Degrees(Quaternion a, Quaternion b)
    {
        var turn = Quaternion.Inverse(Quaternion.Normalize(a)) * Quaternion.Normalize(b);
        return 2 * MathF.Atan2(new Vector3(turn.X, turn.Y, turn.Z).Length(), MathF.Abs(turn.W)) * 180 / MathF.PI;
    }

    private static MotionLayer Sway(MotionSway sway, params string[] bones) =>
        new("m1", MotionLayerKind.Sway, "Sway 1", 0, 60, 0, 0, 1, PoseComponents.Rotation, MotionInterpolation.Smooth,
            Bones: [.. bones], Sway: sway);

    private static BoneTransform T(Vector3 position, Quaternion? rotation = null) => new(position, rotation ?? Quaternion.Identity, Vector3.One);
}
