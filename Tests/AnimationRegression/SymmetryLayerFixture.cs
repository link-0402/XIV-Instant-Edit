using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json.Nodes;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;

/// <summary>
/// Symmetry layers: which bones they pair, the mirror of one pose across a turning body, how the
/// evaluator applies them over a clip and at one frame, validation, and that journals carry them.
/// </summary>
internal static class SymmetryLayerFixture
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Validation(check, reject);
        Journals(check);
        Pairs(check, reject);
        Poses(check);
        Clips(check);
    }

    /// <summary>
    /// 31 samples over one second (frames 0 to 30) that loop: the chest sways about Y, the left
    /// shoulder rises and falls with its forearm twisted, the left breast is turned; the right side
    /// holds its rest pose.
    /// </summary>
    private static MotionClip Swing(bool loops = false, ImmutableArray<short> tracks = default)
    {
        var poses = Enumerable.Range(0, 31).Select(i =>
        {
            var phase = 2 * MathF.PI * i / 30;
            var pose = RestPose();
            pose[Chest] = pose[Chest] with { Rotation = Q(Vector3.UnitY, 10 * MathF.Sin(phase)) };
            pose[ShoulderL] = pose[ShoulderL] with { Rotation = Q(Vector3.UnitZ, 30 * (1 - MathF.Cos(phase))) };
            pose[ArmL] = pose[ArmL] with { Rotation = Q(Vector3.UnitX, 40) };
            pose[BreastL] = pose[BreastL] with { Rotation = Q(Vector3.UnitX, 15) };
            return pose.Select(value => value.ToBone()).ToArray();
        }).ToArray();
        var made = MotionClip.FromPoses(Body(), 1, poses);
        var clip = new MotionClip(Body(), 1, made.Samples) { Loops = loops, SourceTracks = tracks.IsDefault ? [] : tracks };
        clip.CopyFrom(made);
        return clip;
    }

    private static MotionTransform[] Row(MotionClip clip, int sample) => [.. Enumerable.Range(0, clip.Bones).Select(b => clip.Get(sample, b))];

    private static MotionLayer Offset(string key, string bone, Quaternion turn) =>
        new(key, MotionLayerKind.Offset, key, 0, 30, 0, 0, 1, PoseComponents.All, MotionInterpolation.Linear, [],
            [new MotionKey(0, ImmutableDictionary<string, BoneTransform>.Empty.Add(bone, T(Vector3.Zero, turn)))]);

    private static void Clips(Action<bool, string> check)
    {
        var body = Body();
        var original = Swing();
        check(MirrorError(MotionEvaluator.ModelSpace(body, Row(original, 10)), HelperL, HelperR) > .05f, "the swinging arms start out of mirror");
        var clip = Swing();
        MotionEvaluator.Apply(clip, [Sym(bones: "j_sako_l")]);
        check(Enumerable.Range(0, clip.Samples).All(i => MirrorError(MotionEvaluator.ModelSpace(body, Row(clip, i)), HelperL, HelperR) < 1e-4f),
            "every frame of the right arm mirrors the left");

        var section = Swing();
        MotionEvaluator.Apply(section, [Sym(start: 10, end: 20, fadeIn: 5, bones: "j_sako_l")]);
        check(Row(section, 5).SequenceEqual(Row(original, 5)) && MirrorError(MotionEvaluator.ModelSpace(body, Row(section, 15)), HelperL, HelperR) < 1e-4f,
            "a section changes only its frames");

        var at = MotionEvaluator.ApplyAt(body, Row(original, 12), [Sym(bones: "j_sako_l")], 12, 0, 30);
        check(Enumerable.Range(0, at.Length).All(b => Degrees(at[b].Rotation, clip.Get(12, b).Rotation) < .01f &&
                                                      Vector3.Distance(at[b].Position, clip.Get(12, b).Position) < 1e-5f),
            "the Bones window's pose matches the bake");

        var below = Swing();
        MotionEvaluator.Apply(below, [Offset("m0", "j_ude_a_l", Q(Vector3.UnitY, 20)), Sym(bones: "j_sako_l")]);
        check(Enumerable.Range(0, below.Samples).All(i => MirrorError(MotionEvaluator.ModelSpace(body, Row(below, i)), HelperL, HelperR) < 1e-4f),
            "an offset below a symmetry layer is copied to the other side");
        var above = Swing();
        MotionEvaluator.Apply(above, [Sym(bones: "j_sako_l"), Offset("m2", "j_ude_a_r", Q(Vector3.UnitY, 20))]);
        var aboveModel = MotionEvaluator.ModelSpace(body, Row(above, 10));
        check(MirrorError(aboveModel, ArmL, ArmR) < 1e-4f && MirrorError(aboveModel, HelperL, HelperR) > .01f,
            "an offset above a symmetry layer adjusts the copy");

        var looping = Swing(loops: true);
        MotionEvaluator.Apply(looping, [Sym(bones: "j_sako_l")]);
        check(Degrees(looping.Get(0, ArmR).Rotation, looping.Get(looping.Samples - 1, ArmR).Rotation) < .01f &&
              Degrees(looping.Get(0, ShoulderR).Rotation, looping.Get(looping.Samples - 1, ShoulderR).Rotation) < .01f,
            "a looping clip's copy starts and ends the same, so it doesn't pop");

        ImmutableArray<short> tracked = [.. Enumerable.Range(0, body.Bones.Length).Where(b => b != BreastL && b != BreastR).Select(b => (short)b)];
        var physics = Swing(tracks: tracked);
        MotionEvaluator.Apply(physics, [Sym(bones: "j_sebo_c")]);
        check(Enumerable.Range(0, physics.Samples).All(i => physics.Get(i, BreastR) == original.Get(i, BreastR)) &&
              MirrorError(MotionEvaluator.ModelSpace(body, Row(physics, 10)), HelperL, HelperR) < 1e-4f,
            "a pair without tracks is left to physics while the tracked arms are copied");
        // The save applies layers to a clip it builds from its own samples; it must know the tracks too.
        var saved = AnimationBakeRules.LayerClip(body, 1, original.Samples, false, tracked);
        saved.CopyFrom(original);
        MotionEvaluator.Apply(saved, [Sym(bones: "j_sebo_c")]);
        check(Enumerable.Range(0, saved.Samples).All(i => saved.Get(i, BreastR) == original.Get(i, BreastR)) && saved.SourceTracks.SequenceEqual(tracked),
            "the save's clip carries the clip's tracks, so it leaves physics pairs alone as Live does");
        // The Bones window's frames know the tracks too, so they show what Live and the save do.
        var framed = MotionEvaluator.ApplyAt(body, Row(original, 10), [Sym(bones: "j_sebo_c")], 10, 0, 30, tracked);
        check(framed[BreastR] == original.Get(10, BreastR) && Enumerable.Range(0, framed.Length).All(b =>
                  Degrees(framed[b].Rotation, physics.Get(10, b).Rotation) < .01f && Vector3.Distance(framed[b].Position, physics.Get(10, b).Position) < 1e-5f),
            "with the clip's tracks the Bones window's pose matches the save, physics pairs left alone");

        // A bone an earlier layer moves has motion to copy, even when the clip doesn't track it.
        var offsetBreast = Offset("m0", "j_mune_l", Q(Vector3.UnitY, 20));
        var layered = Swing(tracks: tracked);
        MotionEvaluator.Apply(layered, [offsetBreast, Sym(bones: "j_sebo_c")]);
        check(MirrorError(MotionEvaluator.ModelSpace(body, Row(layered, 10)), BreastL, BreastR) < 1e-4f &&
              Degrees(layered.Get(10, BreastR).Rotation, original.Get(10, BreastR).Rotation) > 1,
            "an offset below a symmetry layer is copied across even onto bones the clip doesn't track");
        var animated = MotionSymmetry.Animated(body, tracked, [offsetBreast, Sym(bones: "j_sebo_c")], 1);
        check(animated != null && animated.Contains(BreastL) && !animated.Contains(BreastR) &&
              MotionSymmetry.Animated(body, null, [offsetBreast], 1) == null &&
              MotionSymmetry.Animated(body, tracked, [offsetBreast with { Enabled = false }, Sym(bones: "j_sebo_c")], 1)!.Contains(BreastL) == false,
            "the bones a symmetry layer counts as moving are the clip's tracks and what earlier layers that are on move");
        var untracked = Swing();
        MotionEvaluator.Apply(untracked, [Sym(bones: "j_sebo_c")]);
        check(Degrees(untracked.Get(10, BreastR).Rotation, original.Get(10, BreastR).Rotation) > 1,
            "without known tracks the same pick copies the breasts too");
    }

    // The arms of a small body whose right side has flipped local axes, as FFXIV's does: the right
    // shoulder is turned 180° about Y, so its children's local +X points to the character's -X. The
    // elbow helpers sit off the bone's axis, so a twist moves them.
    private const int Chest = 1, ShoulderL = 2, ArmL = 3, HelperL = 4, ShoulderR = 5, ArmR = 6, HelperR = 7, OnlyR = 8, Neck = 9, BreastL = 10, BreastR = 11;

    private static SkeletonDescription Body() => new("body", "body-fingerprint",
    [
        new("n_root", -1, 0, T(Vector3.Zero)),
        new("j_sebo_c", 0, 0, T(new Vector3(0, 1, 0))),
        new("j_sako_l", Chest, 0, T(new Vector3(.1f, 0, 0))),
        new("j_ude_a_l", ShoulderL, 0, T(new Vector3(.2f, 0, 0))),
        new("n_hhiji_l", ArmL, 0, T(new Vector3(.1f, .05f, 0))),
        new("j_sako_r", Chest, 0, T(new Vector3(-.1f, 0, 0), Q(Vector3.UnitY, 180))),
        new("j_ude_a_r", ShoulderR, 0, T(new Vector3(.2f, 0, 0))),
        new("n_hhiji_r", ArmR, 0, T(new Vector3(.1f, .05f, 0))),
        new("j_only_r", ArmR, 0, T(new Vector3(0, .1f, 0))),
        new("j_kubi", Chest, 0, T(new Vector3(0, .2f, 0))),
        new("j_mune_l", Chest, 0, T(new Vector3(.05f, -.1f, .05f))),
        new("j_mune_r", Chest, 0, T(new Vector3(-.05f, -.1f, .05f))),
    ], [], [], []);

    /// <summary>A direction mirrored across the chest's left-right plane, written out here rather than taken from the plugin.</summary>
    private static Vector3 Flip(Vector3 v) => new(-v.X, v.Y, v.Z);

    private static MotionTransform[] RestPose() => [.. Body().Bones.Select(bone => MotionTransform.From(bone.Reference))];

    /// <summary>The chest twisted and leaning, the left arm raised with its forearm twisted, the right arm at rest.</summary>
    private static MotionTransform[] Posed()
    {
        var pose = RestPose();
        pose[Chest] = pose[Chest] with { Rotation = Q(Vector3.UnitY, 25) * Q(Vector3.UnitX, 10) };
        pose[ShoulderL] = pose[ShoulderL] with { Rotation = Q(Vector3.UnitZ, 40) };
        pose[ArmL] = pose[ArmL] with { Rotation = Q(Vector3.UnitX, 50) * Q(Vector3.UnitY, 30) };
        return pose;
    }

    /// <summary>
    /// How far a right bone is from the mirror of its left twin, both seen from the chest (whose rest
    /// turn is none): the chest's left-right plane is the body's middle.
    /// </summary>
    private static float MirrorError(MotionTransform[] model, int left, int right)
    {
        var inverse = Quaternion.Inverse(Quaternion.Normalize(model[Chest].Rotation));
        var l = Vector3.Transform(model[left].Position - model[Chest].Position, inverse);
        var r = Vector3.Transform(model[right].Position - model[Chest].Position, inverse);
        return Vector3.Distance(r, new Vector3(-l.X, l.Y, l.Z));
    }

    private static void Pairs(Action<bool, string> check, Action<Action, string> reject)
    {
        var plan = MotionSymmetry.Plan(Body(), Sym(bones: "j_sako_l"));
        check(plan.Pairs.Select(p => (p.Source, p.Target)).SequenceEqual([(ShoulderL, ShoulderR), (ArmL, ArmR), (HelperL, HelperR)]),
            "picking the left shoulder pairs the whole arm, parents first");
        check(plan.Unpaired.SequenceEqual(["j_only_r"]), "a target bone without a twin is listed, not changed");
        check(plan.Pairs.All(p => p.Middle == Chest), "the arms mirror across the chest");
        check(MotionSymmetry.Plan(Body(), Sym(bones: "j_sako_r")).Pairs.SequenceEqual(plan.Pairs), "picking either side of a pair means the same pair");
        check(MotionSymmetry.Plan(Body(), Sym(bones: ["j_sako_l", "j_sako_r"])).Pairs.SequenceEqual(plan.Pairs), "picking both sides changes each target once");
        var right = MotionSymmetry.Plan(Body(), Sym(MotionSide.Right, bones: "j_sako_l"));
        check(right.Pairs.Select(p => (p.Source, p.Target)).SequenceEqual([(ShoulderR, ShoulderL), (ArmR, ArmL), (HelperR, HelperL)]) && right.Unpaired.IsEmpty,
            "From Right copies the right arm onto the left");
        var chest = MotionSymmetry.Plan(Body(), Sym(bones: "j_sebo_c"));
        check(chest.Pairs.Any(p => p.Source == BreastL && p.Target == BreastR) && chest.Pairs.All(p => p.Target != Neck && p.Target != Chest),
            "a middle pick mirrors its sides and leaves middle bones alone");
        check(MotionSymmetry.Plan(Body(), Sym(bones: "j_sebo_c"), new HashSet<int> { 0, 1, 2, 3, 4, 5, 6, 7, 9 }).Pairs.All(p => p.Target != BreastR),
            "a pair with no track on either bone is left to physics");
        reject(() => MotionSymmetry.Plan(Body(), Sym(bones: "j_missing_l")), "a picked bone the skeleton lacks is refused");
        check(MotionSymmetry.DefaultName(MotionSide.Left) == "Symmetry (Left → Right)" && MotionSymmetry.DefaultName(MotionSide.Right) == "Symmetry (Right → Left)",
            "a symmetry layer's default name says which way it copies");
    }

    private static void Poses(Action<bool, string> check)
    {
        var body = Body();
        var plan = MotionSymmetry.Plan(body, Sym(bones: "j_sako_l"));
        var rest = MotionSymmetry.RestModel(body);
        var before = MotionEvaluator.ModelSpace(body, Posed());
        check(MirrorError(before, HelperL, HelperR) > .05f, "the posed arms start out of mirror");

        var local = Posed();
        MotionSymmetry.Mirror(body, local, plan, rest, 1, PoseComponents.Rotation);
        var model = MotionEvaluator.ModelSpace(body, local);
        check(MirrorError(model, ArmL, ArmR) < 1e-4f && MirrorError(model, HelperL, HelperR) < 1e-4f,
            "the right arm mirrors the left across a twisted chest, its twist included");
        // A leaf's own frame, twist included: each local axis of the left helper, seen from the chest,
        // mirrors onto the matching axis of the right one. At rest the matching axis is the one the right
        // helper's rest frame takes to the mirror of the left's.
        var restModel = MotionEvaluator.ModelSpace(body, RestPose());
        var axesMirrored = new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ }.All(axis =>
        {
            var match = Vector3.Transform(Flip(Vector3.Transform(axis, restModel[HelperL].Rotation)), Quaternion.Inverse(restModel[HelperR].Rotation));
            var chestInverse = Quaternion.Inverse(model[Chest].Rotation);
            var left = Vector3.Transform(Vector3.Transform(axis, model[HelperL].Rotation), chestInverse);
            var right = Vector3.Transform(Vector3.Transform(match, model[HelperR].Rotation), chestInverse);
            return Vector3.Distance(right, Flip(left)) < 1e-4f;
        });
        check(axesMirrored, "a leaf bone's own turn mirrors its twin's, about every axis");
        check(local[ShoulderR].Position == RestPose()[ShoulderR].Position && local[ArmR].Position == RestPose()[ArmR].Position,
            "turns only keep the right arm's own bone lengths");
        check(local[Neck] == Posed()[Neck] && local[ShoulderL] == Posed()[ShoulderL] && local[OnlyR] == Posed()[OnlyR],
            "the middle, the copied side and bones without a twin keep their own values");

        var half = Posed();
        MotionSymmetry.Mirror(body, half, plan, rest, .5f, PoseComponents.Rotation);
        check(Degrees(half[ShoulderR].Rotation, MotionEvaluator.Slerp(Posed()[ShoulderR].Rotation, local[ShoulderR].Rotation, .5f)) < .01f,
            "a half blend lands halfway in local space");
        // A joint the same on both sides stays that angle through a fade, rather than making up for its parent's blend.
        check(Degrees(half[ArmR].Rotation, MotionEvaluator.Slerp(Posed()[ArmR].Rotation, local[ArmR].Rotation, .5f)) < .01f &&
              Degrees(half[HelperR].Rotation, MotionEvaluator.Slerp(Posed()[HelperR].Rotation, local[HelperR].Rotation, .5f)) < .01f,
            "a half blend lands halfway in local space down the whole arm");
        var none = Posed();
        MotionSymmetry.Mirror(body, none, plan, rest, 0, PoseComponents.Rotation);
        check(none.SequenceEqual(Posed()), "a zero blend changes nothing");

        var still = RestPose();
        still[Chest] = still[Chest] with { Rotation = Q(Vector3.UnitY, 25) };
        var turned = still.ToArray();
        MotionSymmetry.Mirror(body, still, plan, rest, 1, PoseComponents.Rotation);
        check(Enumerable.Range(0, still.Length).All(b => Degrees(still[b].Rotation, turned[b].Rotation) < 1e-3f),
            "a symmetric pose under a turned chest mirrors to itself");

        var shifted = Posed();
        shifted[ShoulderL] = shifted[ShoulderL] with { Position = new Vector3(.1f, .05f, .02f) };
        var moved = shifted.ToArray();
        MotionSymmetry.Mirror(body, moved, plan, rest, 1, PoseComponents.Rotation | PoseComponents.Position);
        check(MirrorError(MotionEvaluator.ModelSpace(body, moved), ShoulderL, ShoulderR) < 1e-4f &&
              MirrorError(MotionEvaluator.ModelSpace(body, moved), HelperL, HelperR) < 1e-4f,
            "with Move the right shoulder takes the left one's place, mirrored");

        var scaled = Posed();
        scaled[ArmL] = scaled[ArmL] with { Scale = new Vector3(1.2f, 1, 1) };
        MotionSymmetry.Mirror(body, scaled, plan, rest, 1, PoseComponents.Rotation | PoseComponents.Scale);
        check(scaled[ArmR].Scale == new Vector3(1.2f, 1, 1), "with Scale a bone takes its twin's scale");
        var unscaled = Posed();
        unscaled[ArmL] = unscaled[ArmL] with { Scale = new Vector3(1.2f, 1, 1) };
        MotionSymmetry.Mirror(body, unscaled, plan, rest, 1, PoseComponents.Rotation);
        check(unscaled[ArmR].Scale == Vector3.One, "without Scale a bone keeps its own scale");
    }

    private static Quaternion Q(Vector3 axis, float degrees) => Quaternion.CreateFromAxisAngle(axis, degrees * MathF.PI / 180);

    private static float Degrees(Quaternion a, Quaternion b) =>
        2 * MathF.Acos(Math.Clamp(Math.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))), 0, 1)) * 180 / MathF.PI;

    private static BoneTransform T(Vector3 position, Quaternion? rotation = null) => new(position, rotation ?? Quaternion.Identity, Vector3.One);

    private static MotionLayer Sym(MotionSide from = MotionSide.Left, float amount = 1, PoseComponents components = PoseComponents.Rotation,
        int start = 0, int end = 30, int fadeIn = 0, int fadeOut = 0, params string[] bones) =>
        new("m1", MotionLayerKind.Symmetry, "Symmetry", start, end, fadeIn, fadeOut, amount, components, MotionInterpolation.Linear,
            [.. bones], [], true, from);

    private static void Validation(Action<bool, string> check, Action<Action, string> reject)
    {
        var good = Sym(bones: "j_sako_l");
        MotionEvaluator.Validate(good);
        check(MotionEvaluator.Active(good), "a symmetry layer with bones is active");
        check(!MotionEvaluator.Active(good with { Bones = [] }), "a symmetry layer without bones changes nothing");
        check(!MotionEvaluator.Active(good with { Components = PoseComponents.None }), "a symmetry layer copying no part changes nothing");
        check(!MotionEvaluator.Active(good with { Amount = 0 }), "a symmetry layer at no blend changes nothing");
        check(MotionEvaluator.Weight(good, 10) == 1, "a symmetry layer blends at most fully");
        reject(() => MotionEvaluator.Validate(good with { Amount = 1.5f }), "a symmetry blend above 1 is refused");
        reject(() => MotionEvaluator.Validate(good with { Keys = [new MotionKey(0, ImmutableDictionary<string, BoneTransform>.Empty.Add("j_sako_l", T(Vector3.Zero)))] }),
            "a symmetry layer with keys is refused");
        reject(() => MotionEvaluator.Validate(good with { From = (MotionSide)7 }), "an unknown side is refused");
    }

    private static void Journals(Action<bool, string> check)
    {
        var temp = Path.Combine(Path.GetTempPath(), "ie-symmetry-layer-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var resource = new AnimationResource("chara/human/c0801/animation/a0001/bt_common/emote/pose04_loop.pap", "pose04_loop.pap", "hash");
            var clip = new AnimationClip(resource.GamePath, "loop", 0, 0, 42, "chara/test.sklb");
            var capture = new AnimationCapture("clip", 1, 1, Guid.NewGuid(), "Test", "Idle", clip, null, [resource.GamePath], [resource],
                new PoseSnapshot(0, 0, 0, false, [], DateTime.UtcNow, ""), DateTime.UtcNow, true);
            var layer = Sym(MotionSide.Right, .5f, PoseComponents.Rotation | PoseComponents.Scale, bones: "j_sako_l");
            var request = new AnimationBakeRequest(Guid.NewGuid(), capture, AnimationDestination.NewMod, "Motion", false,
                ImmutableHashSet<PoseBoneId>.Empty, PoseComponents.None, AnimationOperation.EditTimeline,
                Timeline: new AnimationTimelineEdit(false, 0, null, [layer]));
            new AnimationJournalStore(temp).Save(new AnimationEditJournal { Id = request.Id, Request = AnimationCommitService.RecoveryRequest(request) });
            var loaded = new AnimationJournalStore(temp).Load().Single().Request.Timeline!.Motion.Single();
            check(loaded with { Bones = [], Keys = [] } == layer with { Bones = [], Keys = [] } && loaded.Bones.SequenceEqual(["j_sako_l"]) &&
                  loaded.From == MotionSide.Right,
                "a journal keeps a symmetry layer, its side and its bones");

            // Journals written before layers had a side have no From.
            var path = Path.Combine(temp, "AnimationEdits", request.Id.ToString("N"), "journal.json");
            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            check(json["Request"]!["Timeline"]!["Motion"]![0]!.AsObject().Remove("From"), "a journal stores a layer's side as From");
            File.WriteAllText(path, json.ToJsonString());
            check(new AnimationJournalStore(temp).Load().Single().Request.Timeline!.Motion.Single().From == MotionSide.Left,
                "a journal written before layers had a side loads as Left");
        }
        finally { Directory.Delete(temp, true); }
    }
}
