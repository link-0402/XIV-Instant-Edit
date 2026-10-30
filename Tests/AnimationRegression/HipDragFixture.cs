using System.Collections.Immutable;
using System.Numerics;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui;
using InstantEdit.Ui.Pose;
using InstantEdit.Ui.Timeline;

/// <summary>The hip drag: the bones a drag holds, the solve that bends the body around the moved hips, and the keys it becomes.</summary>
internal static class HipDragFixture
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
        int[] Held(SkeletonDescription skeleton, string[] selected) => [.. HipSolver.Anchors(skeleton, selected).Select(a => a.Bone)];
        int[] defaults = [FootL, FootR, HandL, HandR];

        foreach (var selected in new[] { new[] { "j_kosi" }, [] })
        {
            var name = selected.Length == 0 ? "nothing selected" : "the hips alone";
            var anchors = HipSolver.Anchors(body, selected);
            check(anchors.Select(a => a.Bone).SequenceEqual(defaults), $"with {name} a drag holds both feet, then both hands");
            check(anchors[0].Chain.Shape == PinChainShape.Limb && Names(anchors[0].Chain.Bones).SequenceEqual(["j_asi_a_l", "j_asi_b_l"]) &&
                  anchors[1].Chain.Shape == PinChainShape.Limb && Names(anchors[1].Chain.Bones).SequenceEqual(["j_asi_a_r", "j_asi_b_r"]),
                $"with {name} a foot bends the thigh and the knee");
            check(anchors[2].Chain.Shape == PinChainShape.Limb &&
                  Names(anchors[2].Chain.Bones).SequenceEqual(["j_sebo_a", "j_sebo_b", "j_sebo_c", "j_sako_l", "j_ude_a_l", "j_ude_b_l"]) &&
                  anchors[3].Chain.Shape == PinChainShape.Limb &&
                  Names(anchors[3].Chain.Bones).SequenceEqual(["j_sebo_a", "j_sebo_b", "j_sebo_c", "j_sako_r", "j_ude_a_r", "j_ude_b_r"]),
                $"with {name} a hand bends the arm, the clavicle and the back");
            check(anchors.All(a => a.RestNormal == PinSolver.RestNormal(body, a.Chain)), $"with {name} each anchor carries its chain's rest normal");
            check(HipSolver.Shared(anchors.AsSpan()).Select(a => a.Bone).SequenceEqual([HandL, HandR]), $"with {name} the two hands share the back and are solved twice");
            check(HipSolver.Turned(anchors.AsSpan()).SequenceEqual([ThighL, KneeL, FootL, ThighR, KneeR, FootR, SpineA, SpineB, Chest,
                      ClavicleL, UpperArmL, ForearmL, HandL, ClavicleR, UpperArmR, ForearmR, HandR]),
                $"with {name} a solve may turn the 17 chain bones and anchors, each once, ascending");
        }

        check(Held(body, ["j_kosi", "n_hara", "n_root"]).SequenceEqual(defaults) && HipSolver.Candidates(body, ["j_kosi", "n_hara", "n_root"]).Count == 0,
            "the hips and the bones above them are no candidates, so the defaults hold");
        check(Held(body, ["j_asi_d_l", "j_kosi"]).SequenceEqual([FootL]), "a selected foot holds alone");
        check(Held(body, ["j_kosi", "j_te_l", "j_ude_b_l"]).SequenceEqual([HandL]), "of a hand and its forearm only the lower, the hand, holds");
        check(Held(body, ["j_kosi", "j_sebo_a"]).Length == 0 && HipSolver.Candidates(body, ["j_kosi", "j_sebo_a"]).SequenceEqual([SpineA]),
            "a bone right under the hips has nothing to bend and holds nothing, and the defaults stay off");
        check(Held(body, ["j_kosi", "j_asi_a_l"]).Length == 0 && HipSolver.Candidates(body, ["j_kosi", "j_asi_a_l"]).SequenceEqual([ThighL]),
            "a thigh has nothing to bend and holds nothing");
        check(HipSolver.Anchors(body, ["j_kosi", "n_sippo_b"]) is [{ Bone: TailB, Chain.Shape: PinChainShape.Chain } tail] &&
              Names(tail.Chain.Bones).SequenceEqual(["n_sippo_a"]),
            "another bone bends the chain from below the hips down to it");
        check(Held(body, ["j_kosi", "j_nothing"]).SequenceEqual(defaults), "a bone the skeleton lacks is no candidate");

        SkeletonDescription spine = new("spine", "spine-fingerprint",
        [
            new("n_root", -1, 0, T(Vector3.Zero)),
            new("n_hara", Root, 0, T(new Vector3(0, 1, 0))),
            new("j_kosi", Hara, 0, T(Vector3.Zero)),
            new("j_sebo_a", Hara, 0, T(new Vector3(0, .1f, 0))),
        ], [], [], []);
        check(HipSolver.Anchors(spine, []).IsEmpty, "a skeleton without the default bones has no anchors");
        var hipless = body with { Bones = body.Bones.SetItem(Kosi, body.Bones[Kosi] with { Name = "j_kosi_x" }) };
        check(HipSolver.Anchors(hipless, []).IsEmpty && HipSolver.Anchors(hipless, ["j_te_l"]).IsEmpty, "a skeleton without the hips has no anchors");
    }

    private static void Solving(Action<bool, string> check)
    {
        var body = Body();
        var anchors = HipSolver.Anchors(body, []);
        var legs = new[] { anchors[0].Chain, anchors[1].Chain };
        int[] back = [SpineA, SpineB, Chest];
        // Both elbows bent 80 degrees: the hands sit well within the arms' reach, so the back needn't lean to hold them.
        var start = Rest();
        start[ForearmL] = start[ForearmL] with { Rotation = Q(Vector3.UnitY, 80) };
        start[ForearmR] = start[ForearmR] with { Rotation = Q(Vector3.UnitY, -80) };
        var startModel = MotionEvaluator.ModelSpace(body, start);
        bool Held(MotionTransform[] pose, MotionTransform[] from, int bone) =>
            Vector3.Distance(pose[bone].Position, from[bone].Position) < .001f && Degrees(pose[bone].Rotation, from[bone].Rotation) < .1f;
        bool BackTurned(MotionTransform[] local, MotionTransform[] from, float degrees) => back.Any(b => Degrees(local[b].Rotation, from[b].Rotation) > degrees);

        // Down 10 cm.
        var down = startModel[Kosi].Position + new Vector3(0, -.1f, 0);
        var (dl, dm) = Solve(body, start, down, anchors);
        check(Vector3.Distance(dm[Kosi].Position, down) < 1e-5f, "the hips land on the aim");
        check(anchors.All(a => Held(dm, startModel, a.Bone)), "both feet and both hands hold their spot and turn");
        check(legs.All(leg => Vector3.Dot(BendNormal(dm, leg), BendNormal(startModel, leg)) > 0), "the knees bend on the motion's side");
        check(!BackTurned(dl, start, .01f), "hands within reach leave the back alone");
        check(new[] { (ClavicleL, HandL), (ClavicleR, HandR) }.All(arm =>
            {
                // The line from the clavicle to the hand, in the chest's frame, before and after.
                Vector3 Line(MotionTransform[] model) => Vector3.Transform(startModel[arm.Item2].Position - model[arm.Item1].Position,
                    Quaternion.Inverse(model[Chest].Rotation));
                var swing = Degrees(PinSolver.Between(Line(startModel), Line(dm)), Quaternion.Identity);
                return swing > 1 && Math.Abs(Degrees(dl[arm.Item1].Rotation, start[arm.Item1].Rotation) - swing * HipSolver.ClavicleShare) < .01f;
            }),
            "each clavicle turns by its share of its arm's swing");
        check(dl[Kosi].Rotation == start[Kosi].Rotation, "the hips never turn");
        check(MotionEvaluator.ModelSpace(body, dl).Zip(dm).All(pair => Vector3.Distance(pair.First.Position, pair.Second.Position) < 1e-5f &&
                  Degrees(pair.First.Rotation, pair.Second.Rotation) < .01f), "the solve keeps model space in step with the local pose");

        // A straight arm behind the back: the hips forward 5 cm put the hand out of the arm's reach, so the back leans.
        var forward = startModel[Kosi].Position + new Vector3(0, 0, .05f);
        var behind = start.ToArray();
        behind[UpperArmL] = behind[UpperArmL] with { Rotation = Q(Vector3.UnitY, 90) };
        behind[ForearmL] = behind[ForearmL] with { Rotation = Quaternion.Identity };
        var behindModel = MotionEvaluator.ModelSpace(body, behind);
        var (bl, bm) = Solve(body, behind, forward, anchors);
        check(Vector3.Distance(bm[HandL].Position, behindModel[HandL].Position) < .001f && BackTurned(bl, behind, .5f),
            "a hand out of the arm's reach leans the back to hold it");
        var bentBehind = behind.ToArray();
        bentBehind[ForearmL] = bentBehind[ForearmL] with { Rotation = Q(Vector3.UnitY, 80) };
        var bentModel = MotionEvaluator.ModelSpace(body, bentBehind);
        var (el, em) = Solve(body, bentBehind, forward, anchors);
        check(Vector3.Distance(em[HandL].Position, bentModel[HandL].Position) < .001f && !BackTurned(el, bentBehind, .01f),
            "a bent elbow takes the move alone and the back stays");
        var bothBehind = behind.ToArray();
        bothBehind[UpperArmR] = bothBehind[UpperArmR] with { Rotation = Q(Vector3.UnitY, -90) };
        bothBehind[ForearmR] = bothBehind[ForearmR] with { Rotation = Quaternion.Identity };
        var bothModel = MotionEvaluator.ModelSpace(body, bothBehind);
        var (wl, wm) = Solve(body, bothBehind, forward, anchors);
        check(Vector3.Distance(wm[HandL].Position, bothModel[HandL].Position) < .001f && Vector3.Distance(wm[HandR].Position, bothModel[HandR].Position) < .001f &&
              BackTurned(wl, bothBehind, .5f),
            "two hands out of reach both hold through one leaning back");

        // Up 1 m: the legs ease short of their feet on the line from the thigh, as far as the soft reach puts them.
        var up = startModel[Kosi].Position + new Vector3(0, 1, 0);
        var (ul, um) = Solve(body, start, up, anchors);
        var soft = Finite(ul) && Finite(um);
        foreach (var leg in legs)
        {
            var reach = Vector3.Distance(startModel[leg.Mid].Position, startModel[leg.Root].Position) +
                        Vector3.Distance(startModel[leg.End].Position, startModel[leg.Mid].Position);
            var thigh = um[leg.Root].Position;
            var spot = startModel[leg.End].Position;
            var want = PinSolver.SoftDistance(Vector3.Distance(thigh, spot), reach, reach - PinSolver.SoftReach(startModel, leg));
            soft &= Math.Abs(Vector3.Distance(um[leg.End].Position, thigh) - want) < 1e-4f &&
                    Vector3.Cross(um[leg.End].Position - thigh, Vector3.Normalize(spot - thigh)).Length() < 1e-4f;
        }
        check(soft, "feet out of reach fall short on the line from the thigh, by the soft reach");
        var (_, hm) = Solve(body, start, up + new Vector3(0, .001f, 0), anchors);
        check(legs.All(leg => Vector3.Distance(hm[leg.End].Position, um[leg.End].Position) < .002f), "out of reach, a millimetre more moves the feet less than two");

        // The hips' own spot, the same aim twice, and no anchors.
        var (sl, _) = Solve(body, start, startModel[Kosi].Position, anchors);
        check(sl.Zip(start).All(pair => Vector3.Distance(pair.First.Position, pair.Second.Position) < 1e-5f && Degrees(pair.First.Rotation, pair.Second.Rotation) < .01f),
            "an aim at the hips' own spot leaves the pose as it was");
        check(Solve(body, start, down, anchors).Local.SequenceEqual(dl), "the same aim from the same start gives the same pose");
        var (nl, nm) = (start.ToArray(), startModel.ToArray());
        HipSolver.Solve(body, nl, nm, Kosi, down, ReadOnlySpan<HipAnchor>.Empty, startModel);
        check(Vector3.Distance(nm[Kosi].Position, down) < 1e-5f && Vector3.Distance(nm[SpineA].Position, startModel[SpineA].Position + down - startModel[Kosi].Position) < 1e-5f &&
              Enumerable.Range(0, nl.Length).Where(b => b != Kosi && b != SpineA).All(b => nl[b] == start[b]),
            "with no anchors the hips move, and the back's base with them, and nothing turns");
    }

    /// <summary>The solve becomes an offset's or a pose's keys, beside the hips' position key that Move makes.</summary>
    private static void Keys(Action<bool, string> check, Action<Action, string> reject)
    {
        var body = Body();
        var index = MotionEvaluator.Index(body);
        var anchors = HipSolver.Anchors(body, []);
        // Both elbows bent 80 degrees, as Solving starts: the hands stay within the arms' reach.
        var before = Rest();
        before[ForearmL] = before[ForearmL] with { Rotation = Q(Vector3.UnitY, 80) };
        before[ForearmR] = before[ForearmR] with { Rotation = Q(Vector3.UnitY, -80) };
        var beforeModel = MotionEvaluator.ModelSpace(body, before);
        var down = new Vector3(0, -.1f, 0);
        TimelineDocument Doc(MotionLayer layer) => new TimelineDocument(100, 100, null, [], 1) with { Motion = [layer] };
        MotionLayer Keyed(TimelineDocument doc) => MotionEdits.Find(doc, "m1")!;
        MotionTransform[] Landed(TimelineDocument doc) => MotionEvaluator.ModelSpace(body, MotionEvaluator.ApplyAt(body, before, [Keyed(doc)], 0));
        // As BoneFrames.Space gives it for an offset or pose layer: the parent's model rotation, times the bone's own for an offset.
        Quaternion Space(string bone, bool offset)
        {
            var at = index[bone];
            var parent = body.Bones[at].Parent;
            var above = parent >= 0 ? beforeModel[parent].Rotation : Quaternion.Identity;
            return Quaternion.Normalize(offset ? above * before[at].Rotation : above);
        }
        // The hips' position key as the overlay composes it with the drag, read from the layer as the document holds it now.
        TimelineDocument MoveHips(TimelineDocument doc, Vector3 move, float weight)
        {
            var layer = Keyed(doc);
            var offset = layer.Kind == MotionLayerKind.Offset;
            var hips = new PoseTarget(PinChains.Hips, PoseEdits.Value(layer, PinChains.Hips, 0, offset ? null : before[Kosi])!.Value,
                PoseEdits.Moved(layer, PinChains.Hips, 0));
            // The back's base goes with the hips, from its key as the document holds it now.
            var back = Keyed(doc);
            var base_ = new PoseTarget(HipSolver.BackBase, PoseEdits.Value(back, HipSolver.BackBase, 0, offset ? null : before[SpineA])!.Value,
                PoseEdits.Moved(back, HipSolver.BackBase, 0));
            return PoseEdits.Move(doc, "m1", 0, [hips, base_], move / weight, bone => Space(bone, offset), offset, name => before[index[name]].Position);
        }
        // A drag of the hips by move in a fresh layer, as the overlay makes it.
        (TimelineDocument Doc, float Weight) Dragged(MotionLayerKind kind, float amount, Vector3 move, ImmutableArray<MotionKey> keys = default)
        {
            var layer = BoneLayer(kind, amount, keys);
            var through = MotionEvaluator.ApplyAt(body, before, [layer], 0);
            var aim = MotionEvaluator.ModelSpace(body, through)[Kosi].Position + move;
            var weight = MotionEvaluator.Weight(layer, 0);
            var doc = PoseEdits.DragHips(Doc(layer), layer, 0, body, before, through, through, aim, anchors.AsSpan(), weight);
            return (MoveHips(doc, move, weight), weight);
        }

        string[] named = ["j_kosi", "j_asi_a_l", "j_asi_b_l", "j_asi_d_l", "j_asi_a_r", "j_asi_b_r", "j_asi_d_r",
            "j_sako_l", "j_ude_a_l", "j_ude_b_l", "j_sako_r", "j_ude_a_r", "j_ude_b_r"];
        var allowed = Names(HipSolver.Turned(anchors.AsSpan())).Append(PinChains.Hips).ToHashSet(StringComparer.Ordinal);
        string[] back = Names([SpineB, Chest]);
        foreach (var kind in new[] { MotionLayerKind.Pose, MotionLayerKind.Offset })
            foreach (var amount in new[] { 1f, .5f })
            {
                var (doc, _) = Dragged(kind, amount, down);
                var bones = Keyed(doc).Keys.Single().Bones;
                var landed = Landed(doc);
                var name = $"{(kind == MotionLayerKind.Pose ? "a pose" : "an offset")} layer at {(amount < 1 ? "half" : "full")} weight";
                check(Vector3.Distance(landed[Kosi].Position, beforeModel[Kosi].Position + down) < .001f, $"a hip drag in {name} lands the hips on the aim");
                check(anchors.All(a => Vector3.Distance(landed[a.Bone].Position, beforeModel[a.Bone].Position) < .001f &&
                          Degrees(landed[a.Bone].Rotation, beforeModel[a.Bone].Rotation) < .1f),
                    $"a hip drag in {name} holds both feet and both hands where they were");
                check(named.All(bones.ContainsKey), $"a hip drag in {name} keys the hips, the thighs, knees and feet and the clavicles, upper arms and forearms");
                check(bones.Keys.All(allowed.Contains), $"a hip drag in {name} keys only the hips and the bones its solve may turn");
                check(!back.Any(bones.ContainsKey) && bones.ContainsKey(HipSolver.BackBase), $"a hip drag in {name} with the hands within reach keys the back's base, which goes with the hips, and no other back bone");
                if (kind == MotionLayerKind.Offset)
                    check(bones.All(pair => pair.Key == PinChains.Hips || pair.Key == HipSolver.BackBase || pair.Value.Position == Vector3.Zero), $"a hip drag in {name} moves only the hips and the back's base");
                if (kind == MotionLayerKind.Offset && amount < 1)
                    check(Vector3.Distance(bones[PinChains.Hips].Position, down * 2) < 1e-5f, "an offset at half weight keys the hips twice the move");
                if (kind == MotionLayerKind.Pose && amount < 1)
                    check(Degrees(bones["j_asi_b_l"].Rotation, MotionEvaluator.ApplyAt(body, before, [Keyed(doc)], 0)[KneeL].Rotation) > 1,
                        "a pose at half weight keys the knee past the solved turn, so its blend lands on it");
            }

        // A pose key that sets the hips' and the knee's positions: the hips' adds the move, the knee's stays, both keep their Moved marks.
        var p = before[Kosi].Position + new Vector3(0, .02f, .01f);
        var q = before[KneeL].Position + new Vector3(0, .01f, 0);
        var set = Key(0, (PinChains.Hips, T(p, before[Kosi].Rotation)), ("j_asi_b_l", T(q, before[KneeL].Rotation))) with { Moved = [PinChains.Hips, "j_asi_b_l"] };
        var (setDoc, setWeight) = Dragged(MotionLayerKind.Pose, 1, down, [set]);
        var setKey = Keyed(setDoc).Keys.Single();
        check(Vector3.Distance(setKey.Bones[PinChains.Hips].Position, p + Vector3.Transform(down / setWeight, Quaternion.Inverse(Space(PinChains.Hips, false)))) < 1e-5f &&
              setKey.Moved.Contains(PinChains.Hips) && setKey.Moved.Contains("j_asi_b_l") &&
              Vector3.Distance(setKey.Bones["j_asi_b_l"].Position, q) < 1e-6f && Degrees(setKey.Bones["j_asi_b_l"].Rotation, before[KneeL].Rotation) > 1,
            "a hip drag adds the move to a pose key's hips position, keeps the knee's position and both Moved marks, and turns the knee");

        // There and back: 20 drags up 5 cm each to a metre, 20 back down, each solved from where the drag began.
        foreach (var kind in new[] { MotionLayerKind.Pose, MotionLayerKind.Offset })
            foreach (var amount in new[] { 1f, .5f })
            {
                var tripDoc = Doc(BoneLayer(kind, amount));
                var weight = MotionEvaluator.Weight(Keyed(tripDoc), 0);
                var last = beforeModel[Kosi].Position;
                for (var step = 1; step <= 40; step++)
                {
                    var aim = beforeModel[Kosi].Position + new Vector3(0, .05f * (step <= 20 ? step : 40 - step), 0);
                    var layer = Keyed(tripDoc);
                    var through = MotionEvaluator.ApplyAt(body, before, [layer], 0);
                    tripDoc = PoseEdits.DragHips(tripDoc, layer, 0, body, before, through, before, aim, anchors.AsSpan(), weight);
                    tripDoc = MoveHips(tripDoc, aim - last, weight);
                    last = aim;
                }
                var returned = MotionEvaluator.ApplyAt(body, before, [Keyed(tripDoc)], 0);
                var hipsKey = Keyed(tripDoc).Keys.Single().Bones[PinChains.Hips].Position;
                var name = $"{(kind == MotionLayerKind.Pose ? "a pose" : "an offset")} layer at {(amount < 1 ? "half" : "full")} weight";
                check(returned.Zip(before).All(pair => Vector3.Distance(pair.First.Position, pair.Second.Position) < .001f && Degrees(pair.First.Rotation, pair.Second.Rotation) < .1f),
                    $"hip drags in {name} up a metre and back leave every bone as it was");
                check(Vector3.Distance(hipsKey, kind == MotionLayerKind.Offset ? Vector3.Zero : before[Kosi].Position) < .001f,
                    $"hip drags in {name} up a metre and back add up to no move of the hips' key");
            }

        // Nothing to key.
        var still = BoneLayer(MotionLayerKind.Pose, 1);
        var stillDoc = Doc(still);
        var lowered = beforeModel[Kosi].Position + down;
        check(ReferenceEquals(PoseEdits.DragHips(stillDoc, still, 0, body, before, before, before, beforeModel[Kosi].Position, anchors.AsSpan(), 1), stillDoc),
            "a hip drag that doesn't move the hips keys nothing");
        var faint = BoneLayer(MotionLayerKind.Pose, .01f);
        var faintDoc = Doc(faint);
        check(ReferenceEquals(PoseEdits.DragHips(faintDoc, faint, 0, body, before, before, before, lowered, anchors.AsSpan(), MotionEvaluator.Weight(faint, 0)), faintDoc),
            "a hip drag in a layer that barely applies keys nothing");
        var alone = PoseEdits.DragHips(stillDoc, still, 0, body, before, before, before, lowered, ReadOnlySpan<HipAnchor>.Empty, 1);
        check(ReferenceEquals(alone, stillDoc) && Keyed(MoveHips(alone, down, 1)).Keys.Single().Bones.Keys.OrderBy(k => k).SequenceEqual(new[] { PinChains.Hips, HipSolver.BackBase }.OrderBy(k => k)),
            "with nothing to hold a hip drag turns no bone, so only the hips' and the back base's moves are keyed");
        var hipless = body with { Bones = body.Bones.SetItem(Kosi, body.Bones[Kosi] with { Name = "j_kosi_x" }) };
        reject(() => PoseEdits.DragHips(stillDoc, still, 0, hipless, before, before, before, lowered, anchors.AsSpan(), 1),
            "a hip drag on a skeleton without the hips is refused");
        var refusal = "";
        try { PoseEdits.DragHips(stillDoc, still, 0, hipless, before, before, before, lowered, anchors.AsSpan(), 1); }
        catch (InvalidDataException e) { refusal = e.Message; }
        check(refusal == MotionEvaluator.Missing(PinChains.Hips).Message, "a hip drag on a skeleton without the hips says the hips are missing");
    }

    /// <summary>A hip drag from <paramref name="start"/> to <paramref name="aim"/> on fresh copies, as each drag frame solves.</summary>
    private static (MotionTransform[] Local, MotionTransform[] Model) Solve(SkeletonDescription skeleton, MotionTransform[] start, Vector3 aim,
        ImmutableArray<HipAnchor> anchors)
    {
        var startModel = MotionEvaluator.ModelSpace(skeleton, start);
        var (local, model) = (start.ToArray(), startModel.ToArray());
        HipSolver.Solve(skeleton, local, model, MotionEvaluator.Index(skeleton)[PinChains.Hips], aim, anchors.AsSpan(), startModel);
        return (local, model);
    }

    private static void Views(Action<bool, string> check)
    {
        var body = Body();
        var pose = BoneLayer(MotionLayerKind.Pose, 1);
        check(TimelineWindowViews.DragGizmo(pose, PinChains.Hips, body) == null && TimelineWindowViews.DragGizmo(pose, PinChains.Hips, body, MotionEvaluator.Index(body)) == null &&
              TimelineWindowViews.DragGizmo(BoneLayer(MotionLayerKind.Offset, 1), PinChains.Hips, body) == null,
            "Drag is available on the hips in an offset or pose layer");
        check(TimelineWindowViews.DragGizmo(pose with { Components = PoseComponents.Rotation }, PinChains.Hips, body) == "This layer doesn't move bones.",
            "the hips need a layer that moves bones to drag them");
        check(TimelineWindowViews.DragGizmo(pose, PinChains.Body, body) == "The body moves the whole character. Select the hips to drag them.",
            "the body sends the drag to the hips");
        var turnless = BoneLayer(MotionLayerKind.Offset, 1) with { Components = PoseComponents.Position };
        check(TimelineWindowViews.DragGizmo(pose, "j_te_l", body) == null &&
              TimelineWindowViews.DragGizmo(BoneLayer(MotionLayerKind.Pin, 1), PinChains.Hips, body) == "A pin already holds and bends." &&
              TimelineWindowViews.DragGizmo(turnless, PinChains.Hips, body) == "This layer doesn't turn bones." &&
              TimelineWindowViews.DragGizmo(pose, null, body) == "Select a bone to drag.",
            "other bones drag as before, and the first rules still come first");
        const string defaults = "The feet and hands hold where they are. To hold other bones instead, select them first, then Ctrl+click the hips.";
        check(TimelineWindowViews.HipDragHint(body, [PinChains.Hips]) == defaults && TimelineWindowViews.HipDragHint(body, [PinChains.Hips, PinChains.Body]) == defaults,
            "with nothing else that can hold selected, the hint names the feet and hands and says how to choose others");
        check(TimelineWindowViews.HipDragHint(body, ["j_asi_d_l", "j_te_r", PinChains.Hips]) == "Holds where they are: Left Foot (j_asi_d_l), Right Hand (j_te_r).",
            "the hint names the selected bones that hold");
        check(TimelineWindowViews.HipDragHint(body, [PinChains.Hips, "j_sebo_a"]) == "Nothing holds; the hips move alone.",
            "a selection none of which can hold holds nothing, not the defaults");

        // The hips and a spine, and a bone right under the body: no feet or hands to hold.
        SkeletonDescription spine = new("spine", "spine-fingerprint",
        [
            new("n_root", -1, 0, T(Vector3.Zero)),
            new("n_hara", Root, 0, T(new Vector3(0, 1, 0))),
            new("j_kosi", Hara, 0, T(Vector3.Zero)),
            new("j_sebo_a", Hara, 0, T(new Vector3(0, .1f, 0))),
            new("n_throw", Hara, 0, T(new Vector3(0, .1f, .1f))),
        ], [], [], []);
        var underBody = $"{AnimationPresentation.BoneName("n_throw")} sits right under the body, so nothing can bend to drag it.";
        check(TimelineWindowViews.DragGizmo(pose, "n_throw", spine) == underBody && TimelineWindowViews.DragGizmo(pose, "n_throw", spine, MotionEvaluator.Index(spine)) == underBody,
            "a bone right under the body has nothing to bend to drag it");
        check(TimelineWindowViews.HipDragHint(spine, [PinChains.Hips]) == "Nothing holds; the hips move alone.",
            "without the feet and hands on the skeleton, the hint says nothing holds, not the defaults");
    }

    // PinLayerFixture's small body with a mirrored right clavicle and arm: both legs from the hips, the spine
    // up to both arms, and a tail. Each knee and elbow is bent 20° at rest, so the rest pose has bend normals.
    private const int Root = 0, Hara = 1, Kosi = 2, ThighL = 3, KneeL = 4, CalfL = 5, FootL = 6, ToesL = 7,
        ThighR = 8, KneeR = 9, CalfR = 10, FootR = 11, ToesR = 12, SpineA = 13, SpineB = 14, Chest = 15,
        ClavicleL = 16, UpperArmL = 17, ForearmL = 18, HandL = 19, TailA = 20, TailB = 21, TailC = 22, TailD = 23,
        ClavicleR = 24, UpperArmR = 25, ForearmR = 26, HandR = 27;

    internal static SkeletonDescription Body() => new("body", "body-fingerprint",
    [
        new("n_root", -1, 0, T(Vector3.Zero)),
        new("n_hara", Root, 0, T(new Vector3(0, 1, 0))),
        new("j_kosi", Hara, 0, T(Vector3.Zero)),
        new("j_asi_a_l", Kosi, 0, T(new Vector3(.1f, -.1f, 0))),
        new("j_asi_b_l", ThighL, 0, T(new Vector3(0, -.4f, 0), Q(Vector3.UnitX, 20))),
        new("j_asi_c_l", KneeL, 0, T(new Vector3(0, -.2f, 0))),
        new("j_asi_d_l", CalfL, 0, T(new Vector3(0, -.2f, 0))),
        new("j_asi_e_l", FootL, 0, T(new Vector3(0, -.05f, .1f))),
        new("j_asi_a_r", Kosi, 0, T(new Vector3(-.1f, -.1f, 0))),
        new("j_asi_b_r", ThighR, 0, T(new Vector3(0, -.4f, 0), Q(Vector3.UnitX, 20))),
        new("j_asi_c_r", KneeR, 0, T(new Vector3(0, -.2f, 0))),
        new("j_asi_d_r", CalfR, 0, T(new Vector3(0, -.2f, 0))),
        new("j_asi_e_r", FootR, 0, T(new Vector3(0, -.05f, .1f))),
        new("j_sebo_a", Hara, 0, T(new Vector3(0, .1f, 0))),
        new("j_sebo_b", SpineA, 0, T(new Vector3(0, .15f, 0))),
        new("j_sebo_c", SpineB, 0, T(new Vector3(0, .15f, 0))),
        new("j_sako_l", Chest, 0, T(new Vector3(.05f, .1f, 0))),
        new("j_ude_a_l", ClavicleL, 0, T(new Vector3(.1f, 0, 0))),
        new("j_ude_b_l", UpperArmL, 0, T(new Vector3(.25f, 0, 0), Q(Vector3.UnitY, 20))),
        new("j_te_l", ForearmL, 0, T(new Vector3(.25f, 0, 0))),
        new("n_sippo_a", Kosi, 0, T(new Vector3(0, 0, -.1f))),
        new("n_sippo_b", TailA, 0, T(new Vector3(0, 0, -.1f))),
        new("n_sippo_c", TailB, 0, T(new Vector3(0, 0, -.1f))),
        new("n_sippo_d", TailC, 0, T(new Vector3(0, 0, -.1f))),
        new("j_sako_r", Chest, 0, T(new Vector3(-.05f, .1f, 0))),
        new("j_ude_a_r", ClavicleR, 0, T(new Vector3(-.1f, 0, 0))),
        new("j_ude_b_r", UpperArmR, 0, T(new Vector3(-.25f, 0, 0), Q(Vector3.UnitY, -20))),
        new("j_te_r", ForearmR, 0, T(new Vector3(-.25f, 0, 0))),
    ], [], [], []);

    private static string[] Names(IEnumerable<int> bones) => [.. bones.Select(b => Body().Bones[b].Name)];

    private static MotionTransform[] Rest(SkeletonDescription? skeleton = null) => [.. (skeleton ?? Body()).Bones.Select(bone => MotionTransform.From(bone.Reference))];

    private static Vector3 BendNormal(MotionTransform[] model, PinChain chain) => Vector3.Normalize(Vector3.Cross(
        model[chain.Mid].Position - model[chain.Root].Position, model[chain.End].Position - model[chain.Root].Position));

    private static bool Finite(MotionTransform[] pose) => pose.All(t =>
        float.IsFinite(t.Position.X) && float.IsFinite(t.Position.Y) && float.IsFinite(t.Position.Z) &&
        float.IsFinite(t.Rotation.X) && float.IsFinite(t.Rotation.Y) && float.IsFinite(t.Rotation.Z) && float.IsFinite(t.Rotation.W));

    private static Quaternion Q(Vector3 axis, float degrees) => Quaternion.CreateFromAxisAngle(axis, degrees * MathF.PI / 180);

    private static float Degrees(Quaternion a, Quaternion b) =>
        2 * MathF.Acos(Math.Clamp(Math.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))), 0, 1)) * 180 / MathF.PI;

    private static BoneTransform T(Vector3 position, Quaternion? rotation = null) => new(position, rotation ?? Quaternion.Identity, Vector3.One);

    private static MotionLayer BoneLayer(MotionLayerKind kind, float amount, ImmutableArray<MotionKey> keys = default) =>
        new("m1", kind, "Layer 1", 0, 30, 0, 0, amount, PoseComponents.Rotation | PoseComponents.Position, MotionInterpolation.Linear, [], keys);

    private static MotionKey Key(int frame, params (string Bone, BoneTransform Value)[] bones) =>
        new(frame, ImmutableDictionary.CreateRange(StringComparer.Ordinal, bones.Select(b => KeyValuePair.Create(b.Bone, b.Value))));
}
