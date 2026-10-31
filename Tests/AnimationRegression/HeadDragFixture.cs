using System.Collections.Immutable;
using System.Numerics;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui;
using InstantEdit.Ui.Pose;
using InstantEdit.Ui.Timeline;

/// <summary>The head drag: the back it leans, the bones it holds, the solve that moves the head with the body following, and the keys it becomes.</summary>
internal static class HeadDragFixture
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Anchors(check);
        Solving(check);
        Keys(check, reject);
        Views(check);
    }

    private static void Anchors(Action<bool, string> check)
    {
        var body = Body();
        check(HeadSolver.Back(body) is { Shape: PinChainShape.Chain, End: Head } leaning && Names(leaning.Bones).SequenceEqual(["j_sebo_a", "j_sebo_b", "j_sebo_c", "j_kubi"]),
            "a head drag leans the back from its lowest bone to the neck");
        var back = HeadSolver.Back(body)!;
        int[] Held(string[] selected) => [.. HeadSolver.Anchors(body, selected).Select(a => a.Bone)];

        foreach (var selected in new[] { [.. HeadSolver.Defaults], new[] { "j_kao", "j_asi_d_r", "j_asi_d_l" } })
        {
            var name = selected.Length == 2 ? "the defaults" : "the head and the defaults in another order";
            var anchors = HeadSolver.Anchors(body, selected);
            check(anchors.Select(a => a.Bone).SequenceEqual([FootL, FootR]), $"with {name} a head drag holds both feet, not the hands");
            check(anchors.All(a => a.Chain.Shape == PinChainShape.Limb && a.Chain.Bones.Length == 2), $"with {name} a foot bends the thigh and the knee");
            check(HeadSolver.Turned(back, anchors.AsSpan()).SequenceEqual([ThighL, KneeL, FootL, ThighR, KneeR, FootR, SpineA, SpineB, Chest, Neck, Head]),
                $"with {name} a solve may turn the legs, the back, the neck and the head, each once, ascending");
        }
        check(HeadSolver.Anchors(body, ["j_te_l", "j_kao"]) is [{ Bone: HandL, Chain.Shape: PinChainShape.Limb } hand] &&
              Names(hand.Chain.Bones).SequenceEqual(["j_sako_l", "j_ude_a_l", "j_ude_b_l"]),
            "a hand alone holds alone and bends its arm and clavicle, not the back");
        foreach (var bone in new[] { "j_sebo_a", "j_sebo_b", "j_kubi", "j_ago", "j_kosi", "n_hara" })
            check(HeadSolver.Candidates(body, [bone, "j_kao"]).Count == 0 && Held([bone, "j_kao"]).Length == 0,
                $"{bone}, on the head's line or at or above the hips, can't hold, and the feet don't in its place");
        check(Held(["j_sako_l", "j_kao"]).Length == 0 && HeadSolver.Candidates(body, ["j_sako_l", "j_kao"]).SequenceEqual([ClavicleL]),
            "a clavicle hangs from the back that leans, so it has nothing to bend and holds nothing");

        var headless = body with { Bones = body.Bones.SetItem(Head, body.Bones[Head] with { Name = "j_kao_x" }) };
        var hipless = body with { Bones = body.Bones.SetItem(Kosi, body.Bones[Kosi] with { Name = "j_kosi_x" }) };
        check(HeadSolver.Back(headless) == null && HeadSolver.Back(hipless) == null && HeadSolver.Anchors(hipless, HeadSolver.Defaults).IsEmpty && HeadSolver.Candidates(hipless, ["j_te_l"]).Count == 0,
            "a skeleton without the head or the hips has no head drag");
        // The back hangs from the body, but under another name than the one the hips carry: their move wouldn't take it along.
        var stray = body with { Bones = body.Bones.SetItem(SpineA, body.Bones[SpineA] with { Name = "j_sebo_x" }) };
        check(HeadSolver.Back(stray) == null, "a back the hips don't carry has no head drag");
    }

    private static void Solving(Action<bool, string> check)
    {
        var body = Body();
        var back = HeadSolver.Back(body)!;
        var feet = HeadSolver.Anchors(body, HeadSolver.Defaults);
        var start = Rest();
        var startModel = MotionEvaluator.ModelSpace(body, start);
        var head = startModel[Head].Position;
        var foot = startModel[SpineA].Position;
        int[] spine = [SpineA, SpineB, Chest, Neck];
        bool Held(MotionTransform[] pose, int bone) =>
            Vector3.Distance(pose[bone].Position, startModel[bone].Position) < .001f && Degrees(pose[bone].Rotation, startModel[bone].Rotation) < .1f;
        bool Lands(MotionTransform[] model, Vector3 aim) => Vector3.Distance(model[Head].Position, aim) < 1e-4f && Degrees(model[Head].Rotation, startModel[Head].Rotation) < .01f;

        // Forward and down along the circle about the back's foot: the back only leans.
        var lean = foot + Vector3.Transform(head - foot, Q(Vector3.UnitX, 20));
        var (ll, lm, lMove) = Solve(body, start, lean, feet);
        check(Lands(lm, lean), "a lean lands the head on the aim, facing as it was");
        check(Math.Abs(Degrees(ll[SpineA].Rotation, start[SpineA].Rotation) - 20) < .01f && spine.Skip(1).All(b => ll[b] == start[b]),
            "the back leans whole about its lowest joint, keeping its shape");
        check(lMove.Length() < 1e-4f, "a lean leaves the hips where they were");
        check(feet.All(a => Held(lm, a.Bone)), "both feet hold their spot and turn");
        check(Vector3.Distance(lMove, lm[Kosi].Position - startModel[Kosi].Position) < 1e-6f &&
              Vector3.Distance(lm[SpineA].Position - startModel[SpineA].Position, lMove) < 1e-5f && ll[Kosi].Rotation == start[Kosi].Rotation,
            "the solve returns the hips' move; the back's foot moves with them and the hips never turn");
        check(MotionEvaluator.ModelSpace(body, ll).Zip(lm).All(pair => Vector3.Distance(pair.First.Position, pair.Second.Position) < 1e-5f &&
                  Degrees(pair.First.Rotation, pair.Second.Rotation) < .01f), "the solve keeps model space in step with the local pose");
        check(Degrees(lm[HandL].Rotation, startModel[HandL].Rotation) > 1 && ll[HandL] == start[HandL] && ll[ClavicleL] == start[ClavicleL],
            "the hands, not held, ride with the leaning back");

        // Straight down 20 cm: the hips take it all and the back stays upright.
        var down = head + new Vector3(0, -.2f, 0);
        var (dl, dm, dMove) = Solve(body, start, down, feet);
        check(Lands(dm, down) && Vector3.Distance(dMove, new Vector3(0, -.2f, 0)) < 1e-4f, "a pull straight down lowers the hips by as much");
        check(spine.All(b => Degrees(dl[b].Rotation, start[b].Rotation) < .01f), "a pull straight down leaves the back as it was");
        check(feet.All(a => Held(dm, a.Bone)) && Degrees(dl[KneeL].Rotation, start[KneeL].Rotation) > 10, "the feet hold and the knees bend: a crouch");

        // Up 1 m: the head still lands; the legs ease short of their feet.
        var up = head + new Vector3(0, 1, 0);
        var (ul, um, _) = Solve(body, start, up, feet);
        check(Finite(ul) && Finite(um) && Lands(um, up) && feet.All(a => um[a.Bone].Position.Y > startModel[a.Bone].Position.Y + .3f),
            "a pull far up lands the head, and the feet lift once the legs are straight");

        // A held hand: the clavicle takes its share, the arm bends, the hand holds.
        var bent = start.ToArray();
        bent[ForearmL] = bent[ForearmL] with { Rotation = Q(Vector3.UnitY, 80) };
        var bentModel = MotionEvaluator.ModelSpace(body, bent);
        var hand = HeadSolver.Anchors(body, ["j_te_l", "j_kao"]);
        var (hl, hm, _) = Solve(body, bent, bentModel[Head].Position + new Vector3(0, -.05f, .05f), hand);
        check(Vector3.Distance(hm[HandL].Position, bentModel[HandL].Position) < .001f && Degrees(hm[HandL].Rotation, bentModel[HandL].Rotation) < .1f &&
              Degrees(hl[ClavicleL].Rotation, bent[ClavicleL].Rotation) > .5f, "a held hand holds while the back leans, its clavicle following");
        check(Degrees(hm[FootL].Rotation, bentModel[FootL].Rotation) > .1f || Vector3.Distance(hm[FootL].Position, bentModel[FootL].Position) > .001f,
            "with a hand held, the feet ride with the body");

        // The head's own spot, the same aim twice, and nothing held.
        var (sl, _, sMove) = Solve(body, start, head, feet);
        check(sMove.Length() < 1e-5f && sl.Zip(start).All(pair => Vector3.Distance(pair.First.Position, pair.Second.Position) < 1e-5f && Degrees(pair.First.Rotation, pair.Second.Rotation) < .01f),
            "an aim at the head's own spot leaves the pose as it was");
        check(Solve(body, start, lean, feet).Local.SequenceEqual(ll), "the same aim from the same start gives the same pose");
        var (nl, nm, _) = Solve(body, start, down, []);
        check(Lands(nm, down) && Degrees(nm[FootL].Rotation, startModel[FootL].Rotation) < .01f && nm[FootL].Position.Y < startModel[FootL].Position.Y - .19f &&
              nl[KneeL] == start[KneeL], "with nothing held the whole body follows the head");
    }

    /// <summary>The solve becomes an offset's or a pose's keys, beside the hips' and the back base's position keys that Move makes.</summary>
    private static void Keys(Action<bool, string> check, Action<Action, string> reject)
    {
        var body = Body();
        var index = MotionEvaluator.Index(body);
        var back = HeadSolver.Back(body)!;
        var feet = HeadSolver.Anchors(body, HeadSolver.Defaults);
        var before = Rest();
        var beforeModel = MotionEvaluator.ModelSpace(body, before);
        TimelineDocument Doc(MotionLayer layer) => new TimelineDocument(100, 100, null, [], 1) with { Motion = [layer] };
        MotionLayer Keyed(TimelineDocument doc) => MotionEdits.Find(doc, "m1")!;
        MotionTransform[] Landed(TimelineDocument doc) => MotionEvaluator.ModelSpace(body, MotionEvaluator.ApplyAt(body, before, [Keyed(doc)], 0));
        Quaternion Space(string bone, bool offset)
        {
            var at = index[bone];
            var parent = body.Bones[at].Parent;
            var above = parent >= 0 ? beforeModel[parent].Rotation : Quaternion.Identity;
            return Quaternion.Normalize(offset ? above * before[at].Rotation : above);
        }
        // The hips' and the back base's position keys as the overlay composes them with the drag, by a move already followed at the weight.
        TimelineDocument MoveCarriers(TimelineDocument doc, Vector3 step)
        {
            var layer = Keyed(doc);
            var offset = layer.Kind == MotionLayerKind.Offset;
            var targets = new[] { PinChains.Hips, HipSolver.BackBase }
                .Select(name => new PoseTarget(name, PoseEdits.Value(layer, name, 0, offset ? null : before[index[name]])!.Value, PoseEdits.Moved(layer, name, 0)))
                .ToList();
            return PoseEdits.Move(doc, "m1", 0, targets, step, bone => Space(bone, offset), offset, name => before[index[name]].Position);
        }

        var lean = beforeModel[SpineA].Position + Vector3.Transform(beforeModel[Head].Position - beforeModel[SpineA].Position, Q(Vector3.UnitX, 20)) +
                   new Vector3(0, -.05f, 0);
        var allowed = Names(HeadSolver.Turned(back, feet.AsSpan())).Append(PinChains.Hips).ToHashSet(StringComparer.Ordinal);
        foreach (var kind in new[] { MotionLayerKind.Pose, MotionLayerKind.Offset })
            foreach (var amount in new[] { 1f, .5f })
            {
                var layer = BoneLayer(kind, amount);
                var weight = MotionEvaluator.Weight(layer, 0);
                var (doc, move) = PoseEdits.DragHead(Doc(layer), layer, 0, body, before, before, before, lean, back, feet.AsSpan(), weight);
                doc = MoveCarriers(doc, move / weight);
                var bones = Keyed(doc).Keys.Single().Bones;
                var landed = Landed(doc);
                var name = $"{(kind == MotionLayerKind.Pose ? "a pose" : "an offset")} layer at {(amount < 1 ? "half" : "full")} weight";
                check(Vector3.Distance(landed[Head].Position, lean) < .001f && Degrees(landed[Head].Rotation, beforeModel[Head].Rotation) < .1f,
                    $"a head drag in {name} lands the head on the aim, facing as it was");
                check(feet.All(a => Vector3.Distance(landed[a.Bone].Position, beforeModel[a.Bone].Position) < .001f && Degrees(landed[a.Bone].Rotation, beforeModel[a.Bone].Rotation) < .1f),
                    $"a head drag in {name} holds both feet where they were");
                check(new[] { "j_kosi", "j_sebo_a", "j_kao", "j_asi_a_l", "j_asi_b_l", "j_asi_a_r", "j_asi_b_r" }.All(bones.ContainsKey) &&
                      !new[] { "j_sebo_b", "j_sebo_c", "j_kubi" }.Any(bones.ContainsKey) && bones.Keys.All(allowed.Contains),
                    $"a head drag in {name} keys the hips, the back's base, the head and the legs; the rest of the back keeps its shape");
            }

        // There and back: 20 drags down 1 cm each, 20 back up, each solved from where the drag began.
        foreach (var kind in new[] { MotionLayerKind.Pose, MotionLayerKind.Offset })
        {
            var doc = Doc(BoneLayer(kind, 1));
            var last = Vector3.Zero;
            for (var step = 1; step <= 40; step++)
            {
                var aim = lean + new Vector3(0, -.01f * (step <= 20 ? step : 40 - step), 0);
                var layer = Keyed(doc);
                var through = MotionEvaluator.ApplyAt(body, before, [layer], 0);
                (doc, var move) = PoseEdits.DragHead(doc, layer, 0, body, before, through, before, aim, back, feet.AsSpan(), 1);
                doc = MoveCarriers(doc, move - last);
                last = move;
                check(step != 20 || Vector3.Distance(Landed(doc)[Head].Position, aim) < .001f, $"{kind} head drags land the head on each frame's aim");
            }
            check(Vector3.Distance(Landed(doc)[Head].Position, lean) < .001f, $"{kind} head drags down and back put the head where the first frame did");
        }

        // Nothing to key.
        var still = BoneLayer(MotionLayerKind.Pose, 1);
        var stillDoc = Doc(still);
        var (same, sameMove) = PoseEdits.DragHead(stillDoc, still, 0, body, before, before, before, beforeModel[Head].Position, back, feet.AsSpan(), 1);
        check(ReferenceEquals(same, stillDoc) && sameMove.Length() < 1e-5f, "a head drag that doesn't move the head keys nothing");
        var faint = BoneLayer(MotionLayerKind.Pose, .01f);
        var faintDoc = Doc(faint);
        var (faded, fadedMove) = PoseEdits.DragHead(faintDoc, faint, 0, body, before, before, before, lean, back, feet.AsSpan(), MotionEvaluator.Weight(faint, 0));
        check(ReferenceEquals(faded, faintDoc) && fadedMove == Vector3.Zero, "a head drag in a layer that barely applies keys nothing and moves nothing");
        var hipless = body with { Bones = body.Bones.SetItem(Kosi, body.Bones[Kosi] with { Name = "j_kosi_x" }) };
        reject(() => PoseEdits.DragHead(stillDoc, still, 0, hipless, before, before, before, lean, back, feet.AsSpan(), 1),
            "a head drag on a skeleton without the hips is refused");
    }

    private static void Views(Action<bool, string> check)
    {
        var body = Body();
        var pose = BoneLayer(MotionLayerKind.Pose, 1);
        check(TimelineWindowViews.HeadDrag(pose, PinChains.Head, body) && TimelineWindowViews.HeadDrag(BoneLayer(MotionLayerKind.Offset, 1), PinChains.Head, body) &&
              TimelineWindowViews.DragGizmo(pose, PinChains.Head, body) == null,
            "Drag on the head of an offset or pose layer moves it with the body following");
        var turnOnly = pose with { Components = PoseComponents.Rotation };
        check(!TimelineWindowViews.HeadDrag(turnOnly, PinChains.Head, body) && TimelineWindowViews.DragGizmo(turnOnly, PinChains.Head, body) == null,
            "on a layer that doesn't move bones the head drags as other bones do, bending the neck");
        var hipless = body with { Bones = body.Bones.SetItem(Kosi, body.Bones[Kosi] with { Name = "j_kosi_x" }) };
        check(!TimelineWindowViews.HeadDrag(pose, "j_kubi", body) && !TimelineWindowViews.HeadDrag(pose, PinChains.Head, null) && !TimelineWindowViews.HeadDrag(pose, PinChains.Head, hipless),
            "other bones, an unknown skeleton and one without the hips have no head drag");

        // The Hold boxes: the feet and hands, the hands unticked at first; a selected bone on the head's line or a clavicle adds none.
        var holds = new DragHolds();
        int[] Holding(params string[] selected) => [.. holds.Anchors(body, true, selected).Select(a => a.Bone)];
        check(holds.Bones(body, true, [PinChains.Head, "j_sebo_b", "j_sako_l"]).SequenceEqual(["j_asi_d_l", "j_asi_d_r", "j_te_l", "j_te_r"]) &&
              holds.Holds(true, "j_asi_d_l") && !holds.Holds(true, "j_te_l") && Holding(PinChains.Head).SequenceEqual([FootL, FootR]),
            "the head offers both feet, ticked, and both hands, unticked; the back and a clavicle add no box");
        holds.Hold(true, "j_te_l", true);
        holds.Hold(true, "j_asi_d_l", false);
        holds.Hold(true, "j_asi_d_r", false);
        check(Holding(PinChains.Head).SequenceEqual([HandL]) && holds.Holds(false, "j_asi_d_l"), "ticking the left hand and unticking the feet holds the hand alone; the hip drag's boxes stay");
    }

    /// <summary>A head drag from <paramref name="start"/> to <paramref name="aim"/> on fresh copies, as each drag frame solves.</summary>
    private static (MotionTransform[] Local, MotionTransform[] Model, Vector3 Move) Solve(SkeletonDescription skeleton, MotionTransform[] start, Vector3 aim,
        ImmutableArray<HipAnchor> anchors)
    {
        var startModel = MotionEvaluator.ModelSpace(skeleton, start);
        var (local, model) = (start.ToArray(), startModel.ToArray());
        var move = HeadSolver.Solve(skeleton, local, model, Kosi, HeadSolver.Back(skeleton)!, aim, anchors.AsSpan(), startModel);
        return (local, model, move);
    }

    // HipDragFixture's body with a neck and a head on the chest, and a jaw on the head.
    private const int Kosi = 2, ThighL = 3, KneeL = 4, FootL = 6, ThighR = 8, KneeR = 9, FootR = 11, SpineA = 13, SpineB = 14, Chest = 15,
        ClavicleL = 16, ForearmL = 18, HandL = 19, Neck = 28, Head = 29;

    private static SkeletonDescription Body()
    {
        var body = HipDragFixture.Body();
        return body with
        {
            Bones = body.Bones.AddRange(
                new SkeletonBone("j_kubi", Chest, 0, T(new Vector3(0, .15f, 0))),
                new SkeletonBone("j_kao", Neck, 0, T(new Vector3(0, .1f, 0))),
                new SkeletonBone("j_ago", Head, 0, T(new Vector3(0, -.02f, .08f)))),
        };
    }

    private static string[] Names(IEnumerable<int> bones) => [.. bones.Select(b => Body().Bones[b].Name)];

    private static MotionTransform[] Rest() => [.. Body().Bones.Select(bone => MotionTransform.From(bone.Reference))];

    private static bool Finite(MotionTransform[] pose) => pose.All(t =>
        float.IsFinite(t.Position.X) && float.IsFinite(t.Position.Y) && float.IsFinite(t.Position.Z) &&
        float.IsFinite(t.Rotation.X) && float.IsFinite(t.Rotation.Y) && float.IsFinite(t.Rotation.Z) && float.IsFinite(t.Rotation.W));

    private static Quaternion Q(Vector3 axis, float degrees) => Quaternion.CreateFromAxisAngle(axis, degrees * MathF.PI / 180);

    private static float Degrees(Quaternion a, Quaternion b) =>
        2 * MathF.Acos(Math.Clamp(Math.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))), 0, 1)) * 180 / MathF.PI;

    private static BoneTransform T(Vector3 position, Quaternion? rotation = null) => new(position, rotation ?? Quaternion.Identity, Vector3.One);

    private static MotionLayer BoneLayer(MotionLayerKind kind, float amount) =>
        new("m1", kind, "Layer 1", 0, 30, 0, 0, amount, PoseComponents.Rotation | PoseComponents.Position, MotionInterpolation.Linear, [], default);
}
