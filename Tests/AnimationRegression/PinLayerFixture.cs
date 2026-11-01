using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json.Nodes;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui;
using InstantEdit.Ui.Pose;
using InstantEdit.Ui.Timeline;

/// <summary>
/// Pin layers: validation, journals, the chains a pin bends, the solver on one pose, planted
/// detection over a path, the evaluator over a clip and at one frame, and the document edits.
/// </summary>
internal static class PinLayerFixture
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Validation(check, reject);
        Journals(check);
        Chains(check, reject);
        Solver(check);
        Limits(check);
        Planting(check);
        Clips(check, reject);
        Edits(check);
        Views(check);
        Drags(check, reject);
        Saving(check);
    }

    /// <summary>
    /// What a save counts as a change a layer made. A pin holds a planted foot with hip turns of a tenth of a degree,
    /// 2.5 mm at the foot; a save that took those for none wrote the unpinned motion back and the foot jittered in game.
    /// </summary>
    private static void Saving(Action<bool, string> check)
    {
        var before = new MotionTransform(new Vector3(0.1f, 0.9f, 0), Quaternion.Normalize(new Quaternion(0.2f, -0.4f, 0.1f, 0.88f)), Vector3.One);
        Quaternion Turned(float degrees) => Quaternion.Normalize(before.Rotation * Quaternion.CreateFromAxisAngle(Vector3.UnitX, degrees * MathF.PI / 180));
        check(!AnimationBakeRules.Changed(before, before) && !AnimationBakeRules.Changed(before, before with { Rotation = -before.Rotation }),
            "a save counts a bone the layers left alone, or only flipped to the other hemisphere, as unchanged");
        check(AnimationBakeRules.Changed(before, before with { Rotation = Turned(0.158f) }) &&
              AnimationBakeRules.Changed(before, before with { Rotation = Turned(0.01f) }),
            "a save keeps a pin's hip turns of a tenth of a degree and less");
        check(AnimationBakeRules.Changed(before, before with { Position = before.Position + new Vector3(0, 0, 1e-5f) }) &&
              AnimationBakeRules.Changed(before, before with { Scale = new Vector3(1.0001f) }),
            "a save keeps a layer's moves and scales of a hundredth of a millimetre");
    }

    /// <summary>The gizmo's IK drag: a solve on the pose the layers leave becomes an offset's or a pose's keys.</summary>
    private static void Drags(Action<bool, string> check, Action<Action, string> reject)
    {
        var body = Body();
        // The arm hangs a little and the elbow bends 60 degrees, so a hand moved 7 cm stays within the arm's reach.
        var before = Rest();
        before[UpperArmL] = before[UpperArmL] with { Rotation = Q(Vector3.UnitZ, -20) };
        before[ForearmL] = before[ForearmL] with { Rotation = Q(Vector3.UnitY, 60) };
        var hand = MotionEvaluator.ModelSpace(body, before)[HandL].Position;
        var near = hand + new Vector3(.05f, 0, .05f);
        TimelineDocument Doc(MotionLayer layer) => new TimelineDocument(100, 100, null, [], 1) with { Motion = [layer] };
        MotionLayer Keyed(TimelineDocument doc) => MotionEdits.Find(doc, "m1")!;
        Vector3 Landed(MotionLayer layer) => MotionEvaluator.ModelSpace(body, MotionEvaluator.ApplyAt(body, before, [layer], 0))[HandL].Position;

        foreach (var kind in new[] { MotionLayerKind.Pose, MotionLayerKind.Offset })
            foreach (var amount in new[] { 1f, .5f })
            {
                var layer = BoneLayer(kind, amount);
                var through = MotionEvaluator.ApplyAt(body, before, [layer], 0);
                var keyed = Keyed(PoseEdits.Reach(Doc(layer), layer, 0, body, before, through, "j_te_l", near, MotionEvaluator.Weight(layer, 0)));
                var bones = keyed.Keys.Single().Bones;
                var name = $"{(kind == MotionLayerKind.Pose ? "a pose" : "an offset")} layer at {(amount < 1 ? "half" : "full")} weight";
                check(Vector3.Distance(Landed(keyed), near) < .001f, $"a drag in {name} lands the hand on the aim");
                check(!bones.ContainsKey("j_te_l") && bones.ContainsKey("j_ude_a_l") && bones.ContainsKey("j_ude_b_l") && bones.Count == 2,
                    $"a drag in {name} keys the upper arm and the forearm, not the hand");
                if (kind == MotionLayerKind.Pose && amount < 1)
                    check(Degrees(bones["j_ude_b_l"].Rotation, MotionEvaluator.ApplyAt(body, before, [keyed], 0)[ForearmL].Rotation) > 1,
                        "a pose at half weight keys past the solved turn, so its blend lands on it");
                if (kind == MotionLayerKind.Offset)
                    check(bones.Values.All(value => value.Position == Vector3.Zero && value.Scale == Vector3.One), $"a drag in {name} keys turns only");
            }

        // A pose key that sets the forearm's position keeps it, and its Moved mark, while its turn changes.
        var moved = new Vector3(.3f, .02f, 0);
        var held = BoneLayer(MotionLayerKind.Pose, 1, [Key(0, ("j_ude_b_l", T(moved, before[ForearmL].Rotation))) with { Moved = ["j_ude_b_l"] }]);
        var heldThrough = MotionEvaluator.ApplyAt(body, before, [held], 0);
        var heldAim = MotionEvaluator.ModelSpace(body, heldThrough)[HandL].Position + new Vector3(.05f, 0, .05f);
        var heldKey = Keyed(PoseEdits.Reach(Doc(held), held, 0, body, before, heldThrough, "j_te_l", heldAim, 1)).Keys.Single();
        check(Vector3.Distance(heldKey.Bones["j_ude_b_l"].Position, moved) < 1e-6f && heldKey.Moved.Contains("j_ude_b_l") &&
              Degrees(heldKey.Bones["j_ude_b_l"].Rotation, before[ForearmL].Rotation) > 1,
            "a drag keeps a pose key's position and Moved mark and changes its turn");

        // A layer at a tenth of its weight can't carry a turn past 18 degrees: its keys stop short of a half turn
        // (which would read back as the opposite way) and the hand still moves toward the aim.
        var chain = PinChains.Chain(body, new("j_te_l"));
        var wide = hand + new Vector3(-.15f, -.1f, .1f);
        var (solvedLocal, solvedModel) = (before.ToArray(), MotionEvaluator.ModelSpace(body, before));
        PinSolver.Reach(body, solvedLocal, solvedModel, chain, wide, PinSolver.RestNormal(body, chain));
        var faint = BoneLayer(MotionLayerKind.Pose, .1f);
        var faintKeyed = Keyed(PoseEdits.Reach(Doc(faint), faint, 0, body, before, before, "j_te_l", wide, MotionEvaluator.Weight(faint, 0)));
        var faintKeys = faintKeyed.Keys.Single().Bones;
        check(chain.Bones.Any(b => Degrees(solvedLocal[b].Rotation, before[b].Rotation) > 18) &&
              faintKeys.All(pair => Quaternion.Dot(pair.Value.Rotation, before[MotionEvaluator.Index(body)[pair.Key]].Rotation) > 0 &&
                  float.IsFinite(pair.Value.Rotation.X) && float.IsFinite(pair.Value.Rotation.Y) && float.IsFinite(pair.Value.Rotation.Z) && float.IsFinite(pair.Value.Rotation.W)) &&
              Vector3.Distance(Landed(faintKeyed), wide) < Vector3.Distance(hand, wide) - .01f,
            "a faint layer's keys stop short of a half turn and the hand still moves toward the aim");

        // Beyond reach: the soft reach applies, the keys stay finite.
        var model = MotionEvaluator.ModelSpace(body, before);
        var shoulder = model[UpperArmL].Position;
        var direction = Vector3.Normalize(new Vector3(1, -.5f, .3f));
        var reach = Vector3.Distance(model[ForearmL].Position, shoulder) + Vector3.Distance(hand, model[ForearmL].Position);
        var pose = BoneLayer(MotionLayerKind.Pose, 1);
        var far = Keyed(PoseEdits.Reach(Doc(pose), pose, 0, body, before, before, "j_te_l", shoulder + direction * 10, 1));
        var soft = shoulder + direction * PinSolver.SoftDistance(10, reach, reach - PinSolver.SoftReach(model, chain));
        check(far.Keys.Single().Bones.Values.All(value =>
                  float.IsFinite(value.Position.X) && float.IsFinite(value.Position.Y) && float.IsFinite(value.Position.Z) &&
                  float.IsFinite(value.Rotation.X) && float.IsFinite(value.Rotation.Y) && float.IsFinite(value.Rotation.Z) && float.IsFinite(value.Rotation.W) &&
                  float.IsFinite(value.Scale.X) && float.IsFinite(value.Scale.Y) && float.IsFinite(value.Scale.Z)) &&
              Vector3.Distance(Landed(far), soft) < .001f,
            "a drag beyond reach keys finite turns and the hand falls short on the line to the aim");

        // A long drag: the hand circles three times and the elbow only bends, the forearm never rolls about its own length.
        // Solved from where the drag began, the arm is back as it was once the hand is, its shoulder unrolled.
        var armLimb = PoseEdits.Limb(body, "j_te_l");
        foreach (var fromStart in new[] { false, true })
        {
            var circledDoc = Doc(BoneLayer(MotionLayerKind.Pose, 1));
            var straightRoll = true;
            MotionTransform[] circledPose = before;
            for (var step = 1; step <= 360; step++)
            {
                var angle = step * 3 * MathF.PI / 180;
                var aim = hand + new Vector3(.12f * (MathF.Cos(angle) - 1), .12f * MathF.Sin(angle), .06f * MathF.Sin(2 * angle));
                var layerNow = Keyed(circledDoc);
                // From its start as the gizmo drags: with the limb it found once.
                circledDoc = PoseEdits.Reach(circledDoc, layerNow, 0, body, before, MotionEvaluator.ApplyAt(body, before, [layerNow], 0), "j_te_l", aim, 1,
                    start: fromStart ? before : null, limb: fromStart ? armLimb : null);
                circledPose = MotionEvaluator.ApplyAt(body, before, [Keyed(circledDoc)], 0);
                straightRoll &= Vector3.Distance(MotionEvaluator.ModelSpace(body, circledPose)[HandL].Position, aim) < .001f &&
                                Math.Abs(Roll(Quaternion.Inverse(before[ForearmL].Rotation) * circledPose[ForearmL].Rotation, Vector3.UnitX)) < 1;
            }
            check(straightRoll, $"a long drag{(fromStart ? " from its start" : "")} lands the hand each frame and never rolls the forearm about its length");
            if (fromStart)
                check(Degrees(circledPose[UpperArmL].Rotation, before[UpperArmL].Rotation) < .1f && Degrees(circledPose[ForearmL].Rotation, before[ForearmL].Rotation) < .1f,
                    "a drag from its start that brings the hand back leaves the arm as it was");
        }

        // Nothing to key.
        var unchanged = Doc(pose);
        check(ReferenceEquals(PoseEdits.Reach(unchanged, pose, 0, body, before, before, "j_te_l", hand, 1), unchanged), "a drag that doesn't move the hand keys nothing");
        check(ReferenceEquals(PoseEdits.Reach(unchanged, pose, 0, body, before, before, "j_te_l", near, .01f), unchanged), "a drag in a layer that barely applies keys nothing");
        check(ReferenceEquals(PoseEdits.Reach(unchanged, pose, 0, body, before, before, "j_kosi", near, 1), unchanged), "a bone right under the body has nothing to bend");
        reject(() => PoseEdits.Reach(unchanged, pose, 0, body, before, before, "j_nothing", near, 1), "a drag on a bone the skeleton lacks is refused");

        // When the gizmo's Drag mode is available, and why not.
        var turnless = BoneLayer(MotionLayerKind.Offset, 1) with { Components = PoseComponents.Position };
        check(TimelineWindowViews.DragGizmo(PinLayer([new("j_te_l")]), "j_te_l", body) == "A pin already holds and bends." &&
              TimelineWindowViews.DragGizmo(turnless, "j_te_l", body) == "This layer doesn't turn bones." &&
              TimelineWindowViews.DragGizmo(BoneLayer(MotionLayerKind.Amplitude, 1), "j_te_l", body) == "This layer doesn't turn bones." &&
              TimelineWindowViews.DragGizmo(pose, null, body) == "Select a bone to drag." &&
              TimelineWindowViews.DragGizmo(pose, "j_te_l", body) == null && TimelineWindowViews.DragGizmo(BoneLayer(MotionLayerKind.Offset, 1), "j_te_l", body) == null &&
              TimelineWindowViews.DragGizmo(pose, "j_te_l", null) == null && TimelineWindowViews.DragGizmo(pose, "j_nothing", body) == null &&
              TimelineWindowViews.DragGizmo(pose, "j_kosi", body) == null && TimelineWindowViews.DragGizmo(pose, "j_kosi", body, MotionEvaluator.Index(body)) == null &&
              TimelineWindowViews.DragGizmo(pose, "j_te_l", body, MotionEvaluator.Index(body)) == null &&
              TimelineWindowViews.DragGizmo(pose, "j_nothing", body, MotionEvaluator.Index(body)) == null,
            "Drag needs an offset or pose layer that turns bones and a selected bone; a limb drags, and so do the hips");
    }

    private static MotionLayer BoneLayer(MotionLayerKind kind, float amount, ImmutableArray<MotionKey> keys = default) =>
        new("m1", kind, "Layer 1", 0, 30, 0, 0, amount, PoseComponents.Rotation | PoseComponents.Position, MotionInterpolation.Linear, [], keys);

    private static void Views(Action<bool, string> check)
    {
        check(TimelineWindowViews.LayerKinds.Last() == MotionLayerKind.Pin && TimelineUi.LayerIcon(MotionLayerKind.Pin) != TimelineUi.LayerIcon(MotionLayerKind.Symmetry) &&
              TimelineWindowViews.LayerColour(MotionLayerKind.Pin) == Theme.ModelKind &&
              TimelineWindowViews.LayerKindHint(MotionLayerKind.Pin) == "Hold bones at a spot while the limb bends to reach it.",
            "Pin is a kind of its own in the menus, glyphs and colours");
        check(TimelineWindowViews.LayerContent(PinLayer([new(PinChains.FootL), new(PinChains.FootR, Planted: true)], [Key(0, (PinChains.FootL, T(Vector3.Zero)))])) == "2 pins · 1 planted · 1 key" &&
              TimelineWindowViews.LayerContent(PinLayer([new(PinChains.FootL, Planted: true)])) == "1 pin · planted" &&
              TimelineWindowViews.LayerContent(PinLayer([new(PinChains.FootL), new(PinChains.FootR)], [Key(0, (PinChains.FootL, T(Vector3.Zero)))])) == "2 pins · 1 key" &&
              TimelineWindowViews.LayerContent(PinLayer([new(PinChains.FootL)])) == "1 pin · no keys" && TimelineWindowViews.LayerContent(PinLayer([])) == "No pins",
            "a pin layer's content line");
        var sets = BonePosing.LayerBones(PinLayer([new(PinChains.FootL)]), Body());
        var (pinnedSet, changedSet) = sets;
        check(pinnedSet.SetEquals([PinChains.FootL]) && changedSet.IsSupersetOf(["j_asi_a_l", "j_asi_b_l"]) && !changedSet.Contains("j_asi_c_l") &&
              BonePosing.LayerBones(PinLayer([new("j_nothing")]), Body()) is { Pinned.Count: 0, Changed.Count: 0 },
            "pinned and bent bones for colouring; a pin the skeleton can't resolve colours nothing");
        var pinLayer = PinLayer([new(PinChains.FootL)]);
        check(BonePosing.StateOf(PinChains.FootL, pinLayer, 0, null, sets) == BoneState.Pinned &&
              BonePosing.StateOf("j_asi_b_l", pinLayer, 0, null, sets) == BoneState.Changed &&
              BonePosing.StateOf(PinChains.FootL, PinLayer([new(PinChains.FootL)], [Key(0, (PinChains.FootL, T(Vector3.Zero)))]), 0, null, sets) == BoneState.Keyed &&
              BonePosing.StateText(BoneState.Pinned, pinLayer) == "Pinned by this layer." && BonePosing.StateText(BoneState.Changed, pinLayer) == "Bent to hold a pin.",
            "pinned bones and chain bones get their states, a key beats both");
        check(PoseEdits.Changes(pinLayer, "j_asi_b_l", changedSet) && !PoseEdits.Changes(pinLayer, "j_kosi", changedSet), "a pin layer changes its chains");
        var doc = new TimelineDocument(100, 100, null, [], 1) with { Motion = [PinLayer([new("j_te_l"), new("j_te_r")], [Key(0, ("j_te_l", T(Vector3.One)))])] };
        check(ReferenceEquals(PoseEdits.Mirror(doc, "m1", [("j_te_l", "j_te_r")], Body(), _ => Rest()), doc), "Mirror leaves pin keys alone: they hold spots, not turns");

        // Pin feet needs both feet.
        var footless = Body() with { Bones = [.. Body().Bones.Take(3)] };
        var refusing = new TimelineEditor(new TimelineDocument(100, 100, null, [], 1));
        check(TimelineWindowViews.CanPinFeet(Body()) && !TimelineWindowViews.CanPinFeet(footless) && !TimelineWindowViews.CanPinFeet(null) &&
              !refusing.AddPinFeet(footless) && refusing.Document.Motion.IsEmpty && refusing.LastError == "This skeleton has no feet to pin." &&
              refusing.AddPinFeet(Body()), "Pin feet needs a skeleton with both feet");
        check(TimelineWindowViews.PinFeetTip(Body()) == TimelineWindowViews.PinFeetHint && TimelineWindowViews.PinFeetTip(footless) == TimelineWindowViews.NoFeet &&
              TimelineWindowViews.PinFeetTip(null).Contains("under Source", StringComparison.Ordinal), "Pin feet says why it waits while the skeleton is unknown");

        // The gizmo on a pin layer: only on a keyed pinned bone; moves, or turns when the pin holds its turn and Move isn't chosen.
        var gizmoLayer = PinLayer([new("j_te_l"), new("j_te_r", HoldTurn: false), new(PinChains.FootL, Planted: true)]);
        check(TimelineWindowViews.PinGizmo(gizmoLayer, "j_kubi", GizmoMode.Turn, out var why) == null && why == "Pin the selected bone to move it with the gizmo." &&
              TimelineWindowViews.PinGizmo(gizmoLayer, PinChains.FootL, GizmoMode.Turn, out why) == null && why == "Planted pins hold where the motion is down." &&
              TimelineWindowViews.PinGizmo(gizmoLayer, "j_te_l", GizmoMode.Turn, out why) == false && why == null &&
              TimelineWindowViews.PinGizmo(gizmoLayer, "j_te_l", GizmoMode.Move, out _) == true && TimelineWindowViews.PinGizmo(gizmoLayer, "j_te_r", GizmoMode.Turn, out _) == true &&
              TimelineWindowViews.PinGizmo(gizmoLayer, "j_te_l", GizmoMode.Drag, out _) == false && TimelineWindowViews.PinGizmo(gizmoLayer, "j_te_r", GizmoMode.Drag, out _) == true,
            "the gizmo turns or moves keyed pins only; Drag turns them as Turn does");

        // The bones' frames for a pin layer turn in the pin's space; its targets are the pinned bones' keys, else where they are.
        var relative = PinLayer([new("j_te_l", RelativeTo: "j_sebo_c"), new(PinChains.FootL), new(PinChains.FootR, Planted: true)], [Key(5, (PinChains.FootL, T(Vector3.One)))]);
        var swayed = Rest();
        swayed[Chest] = swayed[Chest] with { Rotation = Q(Vector3.UnitY, 30) };
        var beforeModel = MotionEvaluator.ModelSpace(Body(), swayed);
        var index = MotionEvaluator.Index(Body());
        var frames = new BonePosing.BoneFrames(Body(), index, swayed, beforeModel, swayed, beforeModel, beforeModel, false, relative);
        check(Degrees(frames.Space("j_te_l"), beforeModel[Chest].Rotation) < .01f && frames.Space(PinChains.FootL) == Quaternion.Identity,
            "a pin's bone turns in its relative bone's space, else the character's");
        check(frames.ThroughPosition("j_te_l") == beforeModel[HandL].Position && frames.ThroughPosition("j_nothing") == null,
            "the frames give a bone's position through the layer, none for a bone they lack");
        var pinTargets = BonePosing.PinTargets(relative, 5, ["j_te_l", PinChains.FootL, PinChains.FootR, "j_kubi"], index, beforeModel, 0, false);
        var spots = BonePosing.PinTargets(relative, 5, ["j_te_l", PinChains.FootL], index, beforeModel, 0, true);
        var handSpot = MotionEvaluator.Relative(beforeModel[Chest], beforeModel[HandL]);
        check(pinTargets.Select(t => t.Bone).SequenceEqual(["j_te_l", PinChains.FootL]) && pinTargets[1].Value == MotionTransform.From(T(Vector3.One)) &&
              Vector3.Distance(pinTargets[0].Value.Position, handSpot.Position) < 1e-5f &&
              Vector3.Distance(spots[1].Value.Position, beforeModel[FootL].Position) < 1e-5f,
            "pin targets are keyed pinned bones, at their key or where they are; Pin here takes where they are");

        // The clip animates every bone of the body; a custom tail hangs off the hips that no standard skeleton has.
        var game = Body();
        var made = game with { Bones = [.. game.Bones, new("j_custom_a", Kosi, 0, T(new Vector3(0, 0, -.1f))), new("j_custom_b", 24, 0, T(new Vector3(0, 0, -.1f))),
            new("j_custom_c", 25, 0, T(new Vector3(0, 0, -.1f)))] };
        var candidate = new SkeletonCandidate(new SkeletonSource(SkeletonSourceKind.Game, new AnimationResource("chara/a.pap", "a.pap", "hash")), made, 0, "");
        var clip = new AnimationClip("chara/a.pap", "loop", 0, 0, 1, "s.sklb",
            Resolution: new SkeletonResolution(SkeletonResolutionState.Matched, [candidate], candidate, TrackBones: [.. Enumerable.Range(0, game.Bones.Length).Select(b => (short)b)],
                Standards: [new StandardSkeleton(SkeletonStandard.Vanilla, game, "game")]));
        var tailPin = new TimelineDocument(100, 100, null, [], 1) with { Motion = [PinLayer([new("j_custom_c", Planted: true)])] };
        check(TimelineWindowViews.PinnedBones(tailPin.Motion[0], clip.Resolution?.Selected?.Skeleton).Order().SequenceEqual(new[] { "j_custom_a", "j_custom_b", "j_custom_c" }) &&
              TimelineWindowViews.LayerBonesWarning(tailPin, clip, clip.Resolution?.Selected?.Skeleton) is { } warning && warning.Contains("j_custom_b", StringComparison.Ordinal),
            "a pin bending bones outside the standard skeletons warns like a keyed layer");
        check(TimelineWindowViews.BonesProblem(tailPin with { ExcludedBones = ["j_custom_b"] }, clip with { Resolution = clip.Resolution! with { TrackBones = [.. Enumerable.Range(0, made.Bones.Length).Select(b => (short)b)] } }, clip.Resolution?.Selected?.Skeleton, null) is { } problem &&
              problem.Contains("j_custom_b", StringComparison.Ordinal), "a pin bending a bone left out of the save refuses to save");
    }

    private static void Edits(Action<bool, string> check)
    {
        var doc = new TimelineDocument(100, 100, null, [], 1);
        var plain = MotionEdits.Add(doc, MotionLayerKind.Pin, 0, 100, out var plainKey, out _);
        check(MotionEdits.Find(plain, plainKey) is { Name: "Pin 1", Components: PoseComponents.Rotation | PoseComponents.Position, Interpolation: MotionInterpolation.Smooth, Pins.IsEmpty: true },
            "a new pin layer is named Pin 1 and has no pins");
        var feet = MotionEdits.AddPinFeet(doc, out var key, out var error);
        var layer = MotionEdits.Find(feet, key)!;
        check(error == null && layer is { Kind: MotionLayerKind.Pin, Name: "Pin feet", Start: 0, End: 100, FadeIn: 0, FadeOut: 0 } &&
              layer.Pins.Select(p => p.Bone).SequenceEqual([PinChains.FootL, PinChains.FootR]) &&
              layer.Pins.All(p => p is { Planted: true, HoldTurn: true, Sensitivity: PinSensitivity.Normal }), "Pin feet plants both feet, turn held");
        var added = MotionEdits.PinBones(feet, key, ["j_te_l", "j_te_l", "", PinChains.FootL]);
        check(MotionEdits.Find(added, key)!.Pins.Select(p => p.Bone).SequenceEqual([PinChains.FootL, PinChains.FootR, "j_te_l"]) &&
              ReferenceEquals(MotionEdits.PinBones(added, key, [PinChains.FootL]), added), "pinning adds new bones once");
        var keyed = MotionEdits.SetKey(added, key, 5, "j_te_l", T(Vector3.One));
        check(MotionEdits.Find(keyed, key)!.Keys.Single().Bones.ContainsKey("j_te_l") && ReferenceEquals(MotionEdits.SetKey(keyed, key, 5, PinChains.FootL, T(Vector3.One)), keyed) &&
              ReferenceEquals(MotionEdits.SetKey(keyed, key, 5, "j_kubi", T(Vector3.One)), keyed), "only keyed pins take keys");
        var plantedHand = MotionEdits.SetPin(keyed, key, new("j_te_l", Planted: true));
        check(MotionEdits.Find(plantedHand, key)!.Keys.IsEmpty && MotionEdits.Find(plantedHand, key)!.Pins[2].Planted, "switching a pin to planted drops its keys");
        var riding = MotionEdits.SetPin(keyed, key, new("j_te_l", RelativeTo: "j_sebo_c"));
        var holding = MotionEdits.SetPin(keyed, key, new("j_te_l", HoldTurn: false));
        check(MotionEdits.Find(riding, key)! is { Keys.IsEmpty: true } rides && rides.Pins[2].RelativeTo == "j_sebo_c" &&
              MotionEdits.Find(holding, key)!.Keys.Single().Bones.ContainsKey("j_te_l"),
            "a pin that rides with another bone drops its keys, whose spots meant somewhere else; other settings keep them");
        var bent = MotionEdits.SetPin(keyed, key, MotionEdits.Find(keyed, key)!.Pins[2] with { Bend = 30 });
        check(MotionEdits.Find(bent, key)! is { Keys: [_] } bentLayer && bentLayer.Pins[2].Bend == 30, "a pin's bend changes without dropping its keys");
        check(MotionEdits.Find(MotionEdits.RemovePin(keyed, key, "j_te_l"), key) is { Keys.IsEmpty: true } removed && removed.Pins.Length == 2,
            "removing a pin drops it and its keys");
        check(ReferenceEquals(MotionEdits.SetPin(keyed, key, new("j_nothing")), keyed) && ReferenceEquals(MotionEdits.SetPin(keyed, key, MotionEdits.Find(keyed, key)!.Pins[2]), keyed),
            "setting a pin that isn't there or hasn't changed changes nothing");
        // Based on `feet`, where `layer` is the stored record, so the pins are the only difference.
        var replaced = MotionEdits.Replace(feet, layer with { Pins = [new(PinChains.FootL, Planted: true, HoldTurn: false), layer.Pins[1]] });
        check(!ReferenceEquals(replaced, feet) && MotionEdits.Find(replaced, key)!.Pins[0].HoldTurn == false, "Replace commits a change to the pins alone");
        var spot = new MotionTransform(new Vector3(1, 2, 3), Quaternion.Identity, Vector3.One);
        check(PoseEdits.Value(layer, PinChains.FootL, 0, spot) == spot && PoseEdits.Value(MotionEdits.Find(keyed, key)!, "j_te_l", 5, spot) == MotionTransform.From(T(Vector3.One)),
            "a pin's value is its key, else the spot it is at");

        var editor = new TimelineEditor(doc);
        check(editor.AddPinFeet() && MotionEdits.Find(editor.Document, editor.SelectedLayer) is { Name: "Pin feet" }, "the editor adds Pin feet and selects it");
        var feetKey = editor.SelectedLayer;
        check(editor.PinSelected(["j_te_l"]) && editor.SelectedLayer == feetKey && MotionEdits.Find(editor.Document, feetKey)!.Pins.Length == 3,
            "Pin selected adds to the selected pin layer");
        var fresh = new TimelineEditor(doc);
        check(fresh.PinSelected(["j_te_r"]) && MotionEdits.Find(fresh.Document, fresh.SelectedLayer) is { Kind: MotionLayerKind.Pin } made &&
              made.Pins.Single().Bone == "j_te_r" && fresh.Document.Motion.Length == 1, "with no pin layer selected, Pin selected adds one");
        fresh.Undo();
        check(fresh.Document.Motion.IsEmpty, "adding a pin layer for the selection is one undo step");
        check(!new TimelineEditor(doc).PinSelected([]), "Pin selected needs a bone");
    }

    /// <summary>
    /// 31 samples over one second (frames 0 to 30) on <see cref="Body"/>: the chest sways 10° about Y, the
    /// left foot turns 3° either way about Y while the hips and the ankle stay put, the tail waves.
    /// </summary>
    private static MotionClip Idle(bool loops = false)
    {
        var poses = Enumerable.Range(0, 31).Select(i =>
        {
            var phase = 2 * MathF.PI * i / 30;
            var pose = Rest();
            pose[Chest] = pose[Chest] with { Rotation = Q(Vector3.UnitY, 10 * MathF.Sin(phase)) };
            pose[FootL] = pose[FootL] with { Rotation = Q(Vector3.UnitY, 3 * MathF.Sin(phase)) };
            pose[TailB] = pose[TailB] with { Rotation = Q(Vector3.UnitY, 15 * MathF.Sin(phase)) };
            return pose.Select(value => value.ToBone()).ToArray();
        }).ToArray();
        var made = MotionClip.FromPoses(Body(), 1, poses);
        var clip = new MotionClip(Body(), 1, made.Samples) { Loops = loops };
        clip.CopyFrom(made);
        return clip;
    }

    /// <summary>
    /// A straight left leg whose hips bob 6 mm up over samples 10-25 (3 s, 31 samples): the foot rises with
    /// them but stays down, so its held spot, an average, is out of the straight leg's reach while they're up.
    /// </summary>
    private static MotionClip StraightBob()
    {
        var poses = Enumerable.Range(0, 31).Select(i =>
        {
            var up = AnimationPoseRules.SmoothStep(Math.Clamp((i - 10) / 5f, 0, 1)) - AnimationPoseRules.SmoothStep(Math.Clamp((i - 20) / 5f, 0, 1));
            var pose = Rest();
            pose[Kosi] = pose[Kosi] with { Position = new Vector3(0, .006f * up, 0) };
            pose[KneeL] = pose[KneeL] with { Rotation = Quaternion.Identity };
            return pose.Select(value => value.ToBone()).ToArray();
        }).ToArray();
        return MotionClip.FromPoses(Body(), 3, poses);
    }

    /// <summary>Standing still for a second (31 samples), the hips <paramref name="drop"/> lower and the left foot tipped <paramref name="tilt"/>° about X.</summary>
    private static MotionClip Sunk(float drop, float tilt)
    {
        var pose = Rest();
        pose[Hara] = pose[Hara] with { Position = pose[Hara].Position - new Vector3(0, drop, 0) };
        pose[FootL] = pose[FootL] with { Rotation = Q(Vector3.UnitX, tilt) };
        var bones = pose.Select(value => value.ToBone()).ToArray();
        return MotionClip.FromPoses(Body(), 1, [.. Enumerable.Repeat(bones, 31)]);
    }

    /// <summary>
    /// Standing still for 3 s (31 samples) on a straight left leg, the hips <paramref name="lift"/> higher, the foot turned as the rest
    /// pose has it and then <paramref name="tilt"/>° toes down.
    /// </summary>
    private static MotionClip StraightAbove(float lift, float tilt = 0)
    {
        var pose = Rest();
        pose[Hara] = pose[Hara] with { Position = pose[Hara].Position + new Vector3(0, lift, 0) };
        pose[KneeL] = pose[KneeL] with { Rotation = Quaternion.Identity };
        pose[FootL] = pose[FootL] with { Rotation = Q(Vector3.UnitX, 20 + tilt) };
        var bones = pose.Select(value => value.ToBone()).ToArray();
        return MotionClip.FromPoses(Body(), 3, [.. Enumerable.Repeat(bones, 31)]);
    }

    private static MotionTransform[] Row(MotionClip clip, int sample) => [.. Enumerable.Range(0, clip.Bones).Select(b => clip.Get(sample, b))];

    private static bool All(MotionClip clip, Func<int, bool> sample) => Enumerable.Range(0, clip.Samples).All(sample);

    /// <summary>Whether every bone of samples <paramref name="first"/> to <paramref name="last"/> (-1: the end) is the same in both clips.</summary>
    private static bool SameRows(MotionClip a, MotionClip b, int first = 0, int last = -1) =>
        Enumerable.Range(first, (last < 0 ? a.Samples - 1 : last) - first + 1).All(i => Row(a, i).SequenceEqual(Row(b, i)));

    private static Vector3 Foot(MotionClip clip, int sample, int bone = FootL) => MotionEvaluator.ModelSpace(Body(), Row(clip, sample))[bone].Position;

    /// <summary>A point 15 cm along the foot, where a sole meets the floor.</summary>
    private static Vector3 SolePoint(MotionClip clip, int sample)
    {
        var foot = MotionEvaluator.ModelSpace(Body(), Row(clip, sample))[FootL];
        return foot.Position + Vector3.Transform(new Vector3(0, 0, .15f), foot.Rotation);
    }

    /// <summary>The left hand's spot at <paramref name="sample"/>, moved <paramref name="outward"/> along the line from the upper arm.</summary>
    private static MotionTransform HandSpot(MotionClip clip, int sample, float outward)
    {
        var model = MotionEvaluator.ModelSpace(Body(), Row(clip, sample));
        return model[HandL] with { Position = model[HandL].Position + Vector3.Normalize(model[HandL].Position - model[UpperArmL].Position) * outward };
    }

    private static MotionTransform RightSpot(MotionClip clip)
    {
        var foot = MotionEvaluator.ModelSpace(Body(), Row(clip, 0))[FootR];
        return foot with { Position = foot.Position + new Vector3(-.02f, 0, .01f) };
    }

    private static void Clips(Action<bool, string> check, Action<Action, string> reject)
    {
        var original = Idle();
        var body = Body();
        var spot = MotionEvaluator.ModelSpace(body, Row(original, 0))[FootL];
        var target = spot with { Position = spot.Position + new Vector3(.03f, 0, .02f) };
        var pinned = Idle();
        MotionEvaluator.Apply(pinned, [PinLayer([new(PinChains.FootL)], [Key(0, (PinChains.FootL, target.ToBone()))])]);
        check(All(pinned, i => Vector3.Distance(MotionEvaluator.ModelSpace(body, Row(pinned, i))[FootL].Position, target.Position) < 1e-4f &&
                               Degrees(MotionEvaluator.ModelSpace(body, Row(pinned, i))[FootL].Rotation, target.Rotation) < .01f),
            "one key holds the foot's spot and turn all through");
        var none = Idle();
        MotionEvaluator.Apply(none, [PinLayer([new(PinChains.FootL)], [Key(0, (PinChains.FootL, target.ToBone()))], amount: 0)]);
        check(SameRows(none, original), "pull 0 leaves the motion");
        var half = Idle();
        MotionEvaluator.Apply(half, [PinLayer([new(PinChains.FootL)], [Key(0, (PinChains.FootL, target.ToBone()))], amount: .5f)]);
        check(Vector3.Distance(Foot(half, 0), Vector3.Lerp(Foot(original, 0), target.Position, .5f)) < 1e-4f, "a half pull puts the end halfway along the straight path");
        var position = Idle();
        MotionEvaluator.Apply(position, [PinLayer([new(PinChains.FootL, HoldTurn: false)], [Key(0, (PinChains.FootL, target.ToBone()))])]);
        check(Vector3.Distance(Foot(position, 10), target.Position) < 1e-4f &&
              Degrees(MotionEvaluator.ModelSpace(body, Row(position, 10))[FootL].Rotation, target.Rotation) > 1, "position only leaves the turn to the motion");
        var faded = Idle();
        MotionEvaluator.Apply(faded, [PinLayer([new(PinChains.FootL)], [Key(10, (PinChains.FootL, target.ToBone()))], start: 10, end: 25, fadeIn: 5, fadeOut: 5)]);
        // A smoothstep fade moves at most 1.5 times its average speed: 3.6 cm over 5 frames is about 1.1 cm a frame at its fastest.
        var fastest = 1.6f * Vector3.Distance(target.Position, spot.Position) / 5;
        check(SameRows(faded, original, 0, 9) && Enumerable.Range(10, 15).All(i => Vector3.Distance(Foot(faded, i), Foot(faded, i - 1)) < fastest),
            "a faded section moves the foot without a pop");
        // Review focus 4: a far pin through a fade moves the end on the straight path from the motion's spot, by the fade's weight.
        var lifted = spot with { Position = spot.Position + new Vector3(0, .1f, .05f) };
        var fadingLayer = PinLayer([new(PinChains.FootL)], [Key(10, (PinChains.FootL, lifted.ToBone()))], start: 10, end: 30, fadeIn: 8);
        var fading = Idle();
        MotionEvaluator.Apply(fading, [fadingLayer]);
        check(Enumerable.Range(10, 10).All(i => Vector3.Distance(Foot(fading, i),
                  Vector3.Lerp(Foot(original, i), lifted.Position, MotionEvaluator.Weight(fadingLayer, fading.Frame(i), 30))) < 1e-4f),
            "a fade slides the end along the straight path from the motion to the pin");
        var two = Idle();
        var far = target with { Position = target.Position + new Vector3(0, 0, .04f) };
        MotionEvaluator.Apply(two, [PinLayer([new(PinChains.FootL)], [Key(0, (PinChains.FootL, target.ToBone())), Key(30, (PinChains.FootL, far.ToBone()))])]);
        check(Vector3.Distance(Foot(two, 15), Vector3.Lerp(target.Position, far.Position, .5f)) < 1e-3f, "two keys move the foot between spots the layer's way");
        var looping = Idle(loops: true);
        MotionEvaluator.Apply(looping, [PinLayer([new(PinChains.FootL)], [Key(5, (PinChains.FootL, target.ToBone())), Key(20, (PinChains.FootL, far.ToBone()))])]);
        check(Vector3.Distance(Foot(looping, 0), Foot(looping, 30)) < 1e-3f, "keys wrap round a loop the layer covers");

        // A planted foot's held spot comes in where the straight leg can't reach it, so the foot holds still all through.
        var bob = StraightBob();
        var plantedFoot = PinLayer([new(PinChains.FootL, Planted: true)], end: 90);
        var heldAt = MotionEvaluator.Planted(bob, plantedFoot)[PinChains.FootL];
        var bobbed = StraightBob();
        MotionEvaluator.Apply(bobbed, [plantedFoot]);
        var feet = Enumerable.Range(0, bobbed.Samples).Select(i => Foot(bobbed, i)).ToArray();
        check(heldAt.Stretches is [{ First: 0, Last: 30 }] && feet.All(foot => Vector3.Distance(foot, heldAt.Stretches[0].Held.Position) < 1e-4f),
            "a planted foot held where the straight leg can't reach comes in until it can, and holds still");
        // The straight leg has no soft zone, so the spot comes in exactly to where the foot is with the hips up.
        check(MathF.Abs(heldAt.Stretches[0].Held.Position.Y - Enumerable.Range(0, bob.Samples).Max(i => Foot(bob, i).Y)) < 1e-5f &&
              MathF.Abs(heldAt.Stretches[0].Held.Position.X - Foot(bob, 0).X) < 1e-5f && MathF.Abs(heldAt.Stretches[0].Held.Position.Z - Foot(bob, 0).Z) < 1e-5f,
            "the held spot comes in only as far as the motion's own feet went");

        // On the floor: a foot the hips sink 2 cm into it, tipped 10°, comes up flat on it as the rest pose stands it.
        var rest = Model()[FootL];
        float Tilt(MotionClip clip, int sample) =>
            MathF.Acos(Math.Clamp(Vector3.Transform(Vector3.Transform(Vector3.UnitY, Quaternion.Inverse(rest.Rotation)), MotionEvaluator.ModelSpace(body, Row(clip, sample))[FootL].Rotation).Y, -1, 1)) * 180 / MathF.PI;
        var floor = PinLayer([new(PinChains.FootL, Planted: true, Floor: true)]);
        var sunk = Sunk(.02f, 10);
        MotionEvaluator.Apply(sunk, [floor]);
        check(All(sunk, i => MathF.Abs(Foot(sunk, i).Y - rest.Position.Y) < 1e-4f && Tilt(sunk, i) < .1f) && MathF.Abs(Tilt(Sunk(.02f, 10), 0) - 10) < .01f,
            "a planted foot set on the floor comes up out of it and lies flat");
        var leftAlone = Sunk(.02f, 10);
        MotionEvaluator.Apply(leftAlone, [PinLayer([new(PinChains.FootL, Planted: true)])]);
        check(All(leftAlone, i => MathF.Abs(Foot(leftAlone, i).Y - (rest.Position.Y - .02f)) < 1e-4f && MathF.Abs(Tilt(leftAlone, i) - 10) < .1f),
            "without it the foot stays where the motion has it");
        // A straight leg whose flat foot the hips lift 2.6 cm over the floor can't reach it: the foot comes down as far as the leg reaches, and holds still.
        var high = StraightAbove(.05f);
        check(Foot(high, 0).Y - rest.Position.Y > .02f, "the straight leg's foot starts over the floor");
        var highHeld = MotionEvaluator.Planted(high, PinLayer([new(PinChains.FootL, Planted: true, Floor: true)], end: 90))[PinChains.FootL].Stretches[0].Held;
        MotionEvaluator.Apply(high, [PinLayer([new(PinChains.FootL, Planted: true, Floor: true)], end: 90)]);
        check(MathF.Abs(highHeld.Position.Y - Foot(StraightAbove(.05f), 0).Y) < 1e-5f && All(high, i => Vector3.Distance(Foot(high, i), highHeld.Position) < 1e-4f),
            "a floor out of the straight leg's reach leaves the foot as low as the leg reaches");
        // A straight leg's foot tipped 5° toes down with its ball on the floor (Idle 6's right foot, XIV Bridge 542): levelled, the
        // ball would lift and the leg couldn't lower the ankle after it, so the foot keeps the tilt that lets it touch.
        var sole = PinPlanting.Sole(body, FootL)!.Value;
        var tippedDown = Model()[FootL].Rotation * Q(Vector3.UnitX, 5);
        var touching = -Math.Min(Vector3.Transform(sole.Heel, tippedDown).Y, Vector3.Transform(sole.Ball, tippedDown).Y) - Foot(StraightAbove(0, 5), 0).Y;
        var toeDown = StraightAbove(touching, 5);
        MotionEvaluator.Apply(toeDown, [PinLayer([new(PinChains.FootL, Planted: true, Floor: true)], end: 90)]);
        float SoleGap(MotionClip clip, int sample)
        {
            var foot = MotionEvaluator.ModelSpace(body, Row(clip, sample))[FootL];
            return foot.Position.Y + Math.Min(Vector3.Transform(sole.Heel, foot.Rotation).Y, Vector3.Transform(sole.Ball, foot.Rotation).Y);
        }
        check(MathF.Abs(SoleGap(StraightAbove(touching, 5), 0)) < 1e-5f && All(toeDown, i => MathF.Abs(SoleGap(toeDown, i)) < 2e-4f && MathF.Abs(Tilt(toeDown, i) - 5) < .5f),
            "a straight leg's toe-down foot keeps the tilt that lets its ball touch the floor, rather than levelling and floating");

        // Relative to the chest: the hand rides with the sway.
        var hand = MotionEvaluator.Relative(MotionEvaluator.ModelSpace(body, Row(original, 0))[Chest], MotionEvaluator.ModelSpace(body, Row(original, 0))[HandL]);
        var riding = Idle();
        MotionEvaluator.Apply(riding, [PinLayer([new("j_te_l", RelativeTo: "j_sebo_c")], [Key(0, ("j_te_l", hand.ToBone()))])]);
        check(All(riding, i =>
            {
                var m = MotionEvaluator.ModelSpace(body, Row(riding, i));
                return Vector3.Distance(m[HandL].Position, MotionEvaluator.Compose(m[Chest], hand).Position) < 1e-4f;
            }), "a hand pinned to the chest rides with it");
        // Above the limb only when needed.
        var near = Idle();
        MotionEvaluator.Apply(near, [PinLayer([new("j_te_l", Top: "j_sako_l")], [Key(0, ("j_te_l", HandSpot(original, 0, -.03f).ToBone()))])]);
        check(All(near, i => Degrees(near.Get(i, ClavicleL).Rotation, original.Get(i, ClavicleL).Rotation) < .01f), "within reach the bones above the limb stay");
        var beyond = Idle();
        MotionEvaluator.Apply(beyond, [PinLayer([new("j_te_l", Top: "j_sako_l")], [Key(0, ("j_te_l", HandSpot(original, 0, .9f).ToBone()))])]);
        check(Degrees(beyond.Get(0, ClavicleL).Rotation, original.Get(0, ClavicleL).Rotation) > .5f, "beyond reach the clavicle helps");
        // Two pins, list order.
        var both = Idle();
        MotionEvaluator.Apply(both, [PinLayer([new(PinChains.FootL), new(PinChains.FootR)],
            [Key(0, (PinChains.FootL, target.ToBone()), (PinChains.FootR, RightSpot(original).ToBone()))])]);
        check(Vector3.Distance(Foot(both, 10), target.Position) < 1e-4f && Vector3.Distance(Foot(both, 10, FootR), RightSpot(original).Position) < 1e-4f,
            "two pins in one layer both hold");
        // Review focus 5: a later pin relative to a bone an earlier pin moves sees where it moved.
        var chestSpot = MotionEvaluator.ModelSpace(body, Row(original, 0))[Chest];
        // Sideways and a little down, so the straight spine below the chest can reach it.
        var movedChest = chestSpot with { Position = chestSpot.Position + new Vector3(.02f, -.01f, 0) };
        var stacked = Idle();
        MotionEvaluator.Apply(stacked, [PinLayer([new("j_sebo_c"), new("j_te_l", RelativeTo: "j_sebo_c")],
            [Key(0, ("j_sebo_c", movedChest.ToBone()), ("j_te_l", hand.ToBone()))])]);
        var stackedModel = MotionEvaluator.ModelSpace(body, Row(stacked, 10));
        check(Vector3.Distance(stackedModel[Chest].Position, movedChest.Position) < 1e-4f &&
              Vector3.Distance(stackedModel[HandL].Position, MotionEvaluator.Compose(stackedModel[Chest], hand).Position) < 1e-4f,
            "a pin relative to a bone an earlier pin moved follows it there");

        // Bones window frame equals the bake.
        var layers = new[] { PinLayer([new(PinChains.FootL)], [Key(0, (PinChains.FootL, target.ToBone()))]) };
        var at = MotionEvaluator.ApplyAt(body, Row(original, 12), layers, 12, 0, 30);
        check(Enumerable.Range(0, at.Length).All(b => Degrees(at[b].Rotation, pinned.Get(12, b).Rotation) < .01f && Vector3.Distance(at[b].Position, pinned.Get(12, b).Position) < 1e-5f),
            "ApplyAt matches Apply for a keyed pin");
        // Planted: the turning foot stands still; ApplyAt skips it.
        var planted = Idle();
        MotionEvaluator.Apply(planted, [PinLayer([new(PinChains.FootL, Planted: true)])]);
        var turns = Enumerable.Range(0, 31).Select(i => MotionEvaluator.ModelSpace(body, Row(planted, i))[FootL].Rotation).ToArray();
        check(turns.Max(q => Degrees(q, turns[0])) < .1f && Enumerable.Range(0, 31).All(i => Vector3.Distance(SolePoint(planted, i), SolePoint(planted, 0)) < 1e-3f),
            "a planted foot no longer turns or drifts");
        check(MotionEvaluator.ApplyAt(body, Row(original, 12), [PinLayer([new(PinChains.FootL, Planted: true)])], 12, 0, 30).SequenceEqual(Row(original, 12)),
            "ApplyAt leaves planted pins to the bake");
        // Review focus 3: a planted pin on a section looks only at the section and fades in with it.
        var section = Idle();
        MotionEvaluator.Apply(section, [PinLayer([new(PinChains.FootL, Planted: true)], start: 10, end: 25, fadeIn: 3, fadeOut: 3)]);
        // The hold eases in and out over 4 frames at the section's cut edges, inside its own fades.
        var held = Enumerable.Range(15, 6).Select(i => MotionEvaluator.ModelSpace(body, Row(section, i))[FootL].Rotation).ToArray();
        check(SameRows(section, original, 0, 10) && SameRows(section, original, 25) && held.Max(q => Degrees(q, held[0])) < .1f &&
              Degrees(MotionEvaluator.ModelSpace(body, Row(section, 11))[FootL].Rotation, MotionEvaluator.ModelSpace(body, Row(original, 11))[FootL].Rotation) < 1.5f,
            "a planted section holds inside its fades and leaves the rest");
        // Review focus 3: without fades, a stretch cut by the section's edges still eases in and out rather than snapping.
        var cut = Idle();
        MotionEvaluator.Apply(cut, [PinLayer([new(PinChains.FootL, Planted: true)], start: 10, end: 25)]);
        Quaternion FootTurn(MotionClip c, int i) => MotionEvaluator.ModelSpace(body, Row(c, i))[FootL].Rotation;
        // A snap would turn the foot from the motion's turn to the held one in one sample.
        var snap = Degrees(FootTurn(cut, 17), FootTurn(original, 10));
        check(Degrees(FootTurn(cut, 10), FootTurn(original, 10)) < .01f && Degrees(FootTurn(cut, 25), FootTurn(original, 25)) < .01f &&
              Enumerable.Range(11, 15).All(i => Degrees(FootTurn(cut, i), FootTurn(cut, i - 1)) < .6f * snap),
            "a planted stretch cut by an unfaded section eases at its edges");
        check(MotionEvaluator.Planted(Idle(), PinLayer([new(PinChains.FootL, Planted: true), new(PinChains.FootR)])) is { Count: 1 } found &&
              found[PinChains.FootL].Stretches is [{ First: 0, Last: 30 }], "the panel finds planted stretches as the bake does");
        var animated = MotionSymmetry.Animated(body, Tracked(), [PinLayer([new("n_sippo_d", Planted: true)])], 1);
        check(animated!.Contains(TailC) && animated.Contains(TailD), "a pin's chain counts as moving for a symmetry above it");
        check(!MotionSymmetry.Animated(body, Tracked(), [PinLayer([new("n_sippo_d"), new(PinChains.FootL, Planted: true)])], 1)!.Contains(TailC),
            "a keyed pin without keys doesn't count its chain as moving");
        var stale = Idle();
        MotionEvaluator.Apply(stale, [PinLayer([new(PinChains.FootL)], [Key(0, (PinChains.FootL, target.ToBone()))]) with { Bones = ["j_nothing"] }]);
        check(Vector3.Distance(Foot(stale, 5), target.Position) < 1e-4f, "a pin layer ignores a bone limit it doesn't use");
        reject(() => MotionEvaluator.Apply(Idle(), [PinLayer([new("j_te_l", Top: "j_sebo_a", RelativeTo: "j_sebo_c")], [Key(0, ("j_te_l", hand.ToBone()))])]),
            "a relative bone inside the chain is refused at apply");
    }

    /// <summary>A bone's path at 60 samples a second, 120 samples, from where it is at each sample.</summary>
    private static MotionTransform[] BonePath(Func<int, Vector3> position, Func<int, Quaternion>? rotation = null) =>
        [.. Enumerable.Range(0, 120).Select(i => new MotionTransform(position(i), rotation?.Invoke(i) ?? Quaternion.Identity, Vector3.One))];

    /// <summary>Still on the floor for samples 0-39; from sample 40 it lifts 5 cm and moves 20 cm; 60-68 jitter 2 cm on landing; still from 69.</summary>
    private static MotionTransform[] Step() => BonePath(i => i switch
    {
        < 40 => Vector3.Zero,
        < 60 => new Vector3(.2f * (i - 40) / 20, .05f * MathF.Sin(MathF.PI * (i - 40) / 20), 0),
        < 69 => new Vector3(.2f, i % 2 == 0 ? .02f : 0, 0),
        _ => new Vector3(.2f, 0, 0),
    });

    /// <summary>Still, but lifted 10 cm for <paramref name="samples"/> samples from sample 60.</summary>
    private static MotionTransform[] Gap(int samples) => BonePath(i => i >= 60 && i < 60 + samples ? new Vector3(0, .1f, 0) : Vector3.Zero);

    /// <summary>Moving at 1 m/s but for <paramref name="samples"/> still samples from sample 60.</summary>
    private static MotionTransform[] Blip(int samples) => BonePath(i => new Vector3(Math.Clamp(i, 0, 60) / 60f + Math.Max(0, i - 60 - samples) / 60f, 0, 0));

    /// <summary>Still at 90-119 and 0-29 of a loop, lifted 10 cm and out 20 cm and back in between.</summary>
    private static MotionTransform[] LoopStep() => BonePath(i => i is >= 30 and < 90
        ? new Vector3(.2f * MathF.Sin(MathF.PI * (i - 30) / 60), .1f * MathF.Sin(MathF.PI * (i - 30) / 60), 0) : Vector3.Zero);

    /// <summary>Gliding along the floor at <paramref name="speed"/> metres a second (at 60 samples a second).</summary>
    private static MotionTransform[] Drift(float speed) => BonePath(i => new Vector3(speed * i / 60, 0, 0));

    private static MotionTransform[] Still() => BonePath(_ => new Vector3(.1f, 0, .2f), _ => Q(Vector3.UnitY, 30));

    private static void Planting(Action<bool, string> check)
    {
        var step = PinPlanting.Find(Step(), 60, 0, 119, false, PinSensitivity.Normal, 6, 8, 8);
        check(step.Stretches.Length == 2 && step.Stretches[0] is { First: 0, Last: 39 } && step.Stretches[1].First >= 60 && step.Stretches[1].Last == 119,
            "a step gives two stretches");
        check(Vector3.Distance(step.Stretches[1].Held.Position, Step()[100].Position) < 1e-4f && step.Stretches[1].Held.Scale == Vector3.One,
            "a stretch holds the average spot");
        var turned = Q(Vector3.UnitY, 30);
        var wide = new PlantedPath([new PlantedStretch(8, 1, new MotionTransform(new Vector3(0, -1.5f, 0), turned, Vector3.One)),
            new PlantedStretch(3, 5, new MotionTransform(new Vector3(0, -.5f, 0), turned, Vector3.One))], 10, true, 1);
        // Reach 1 round the origin, but sample 0 (past the seam in the stretch 8..1) reaches only 0.5 from a root 0.2 lower, and sample 4 only 0.4.
        Vector3[] roots = [new(0, -.2f, 0), .. Enumerable.Repeat(Vector3.Zero, 9)];
        float[] limits = [.5f, 1, 1, 1, .4f, 1, 1, 1, 1, 1];
        var near = PinPlanting.Reachable(wide, roots, limits);
        check(Vector3.Distance(near.Stretches[0].Held.Position, new Vector3(0, -.7f, 0)) < 1e-4f && near.Stretches[0].Held.Rotation == turned &&
              Vector3.Distance(near.Stretches[1].Held.Position, new Vector3(0, -.4f, 0)) < 1e-4f && near.Stretches[0].First == 8 && near.Stretches[0].Last == 1,
            "a held spot comes in to the nearest point every sample of its stretch reaches, round the seam too, its turn kept");
        check(PinPlanting.Reachable(wide, roots, [.. Enumerable.Repeat(2f, 10)]).Stretches.SequenceEqual(wide.Stretches), "a held spot within reach stays");
        check(step.Hold(20) == 1 && step.Hold(39 + 8) == 0 && step.Hold(39 + 4) is > .4f and < .6f && step.Hold(step.Stretches[1].First - 8) == 0,
            "the hold eases out over the ease samples");
        check(step.Target(20) == step.Stretches[0].Held && step.Target(50) == null && step.Target(41) == step.Stretches[0].Held,
            "the target is the held spot wherever the hold applies");
        check(PinPlanting.Find(Gap(3), 60, 0, 119, false, PinSensitivity.Normal, 6, 8, 8).Stretches.Length == 1, "a short gap closes");
        check(PinPlanting.Find(Gap(12), 60, 0, 119, false, PinSensitivity.Normal, 6, 8, 8).Stretches.Length == 2, "a longer gap splits");
        check(PinPlanting.Find(Blip(5), 60, 0, 119, false, PinSensitivity.Normal, 6, 8, 8).Stretches.IsEmpty &&
              PinPlanting.Find(Blip(20), 60, 0, 119, false, PinSensitivity.Normal, 6, 8, 8).Stretches.Length == 1, "a short stretch drops");
        // A joined stretch over the seam stores First > Last: it runs from First to the end and on from 0 to Last.
        var loop = PinPlanting.Find(LoopStep(), 60, 0, 119, true, PinSensitivity.Normal, 6, 8, 8);
        check(loop.Stretches.Length == 1 && loop.Stretches[0] is { First: 90, Last: 29 } && loop.Hold(10) == 1 && loop.Hold(119) == 1 && loop.Hold(60) == 0,
            "a stretch across the seam joins");
        check(PinPlanting.Find(LoopStep(), 60, 0, 119, false, PinSensitivity.Normal, 6, 8, 8).Stretches.Length == 2, "without the wrap the seam splits it");
        // Down from the loop's last sample, which is its first one's moment, on to sample 40; lifted and moving between.
        var seamStart = PinPlanting.Find(BonePath(i => i is > 40 and < 119 ? new Vector3(.2f * MathF.Sin(MathF.PI * (i - 40) / 79), .1f * MathF.Sin(MathF.PI * (i - 40) / 79), 0) : Vector3.Zero),
            60, 0, 119, true, PinSensitivity.Normal, 6, 8, 8);
        check(seamStart.Stretches is [{ First: 0 }] && seamStart.Hold(80) == 0 && seamStart.Target(80) == null, "a stretch starting on the seam doesn't hold round the whole loop");
        // A loop whose ends don't match: down on the last sample but not on sample 0, stepping over 5-29, down 30-49.
        var openSeam = PinPlanting.Find(BonePath(i => i switch
        {
            0 => new Vector3(.1f, 0, 0),
            >= 5 and < 30 => new Vector3(.2f * MathF.Sin(MathF.PI * (i - 4) / 26), .1f * MathF.Sin(MathF.PI * (i - 4) / 26), 0),
            > 50 and < 119 => new Vector3(.2f * MathF.Sin(MathF.PI * (i - 50) / 69), .1f * MathF.Sin(MathF.PI * (i - 50) / 69), 0),
            _ => Vector3.Zero,
        }), 60, 0, 119, true, PinSensitivity.Normal, 6, 8, 8);
        check(openSeam.Stretches is [{ First: 30, Last: 49 }] && openSeam.Hold(15) == 0, "a foot down only on an open loop's last sample isn't held over the step before the first stretch");
        check(PinPlanting.Find(Step(), 60, 0, 0, false, PinSensitivity.Normal, 6, 8, 1).Stretches.IsEmpty &&
              PinPlanting.Find(Step(), 60, 50, 40, false, PinSensitivity.Normal, 6, 8, 8).Stretches.IsEmpty, "a one-sample or empty range finds nothing");
        check(PinPlanting.Find(Gap(3), 60, 0, 119, false, PinSensitivity.Normal, 6, 8, 8).Stretches[0].Held.Position.Y < 1e-6f,
            "a closed gap's lifted samples don't lift the held spot");
        var slow = Drift(.03f);
        check(PinPlanting.Find(slow, 60, 0, 119, false, PinSensitivity.Strict, 6, 8, 8).Stretches.IsEmpty &&
              !PinPlanting.Find(slow, 60, 0, 119, false, PinSensitivity.Normal, 6, 8, 8).Stretches.IsEmpty &&
              !PinPlanting.Find(Drift(.06f), 60, 0, 119, false, PinSensitivity.Loose, 6, 8, 8).Stretches.IsEmpty &&
              PinPlanting.Find(Drift(.06f), 60, 0, 119, false, PinSensitivity.Normal, 6, 8, 8).Stretches.IsEmpty, "strict holds less than normal, loose more");
        // A dance's shuffle: 10 cm/s along the floor isn't down at any sensitivity.
        check(PinPlanting.Find(Drift(.10f), 60, 0, 119, false, PinSensitivity.Loose, 6, 8, 8).Stretches.IsEmpty, "a foot gliding at a dance's pace isn't held");
        // A ported dance's foot (Cheer On: Blue) never stops: 3 cm in and out and 1.5 cm up with each bounce, up to 12 cm/s, but always in one spot.
        var wiggle = BonePath(i => new Vector3(.015f * MathF.Sin(2 * MathF.PI * i / 46), .0075f * (1 + MathF.Cos(2 * MathF.PI * i / 46)), 0));
        check(PinPlanting.Find(wiggle, 60, 0, 119, false, PinSensitivity.Normal, 6, 8, 8).Stretches is [{ First: 0, Last: 119 } held] &&
              Math.Abs(held.Held.Position.X) < .004f && PinPlanting.Find(wiggle, 60, 0, 119, false, PinSensitivity.Strict, 6, 8, 8).Stretches.IsEmpty,
            "a foot wiggling in one spot is held there, at its middle, unless strict");
        // Jitter that cancels over the window (2 cm every other sample) reads still there: the jolt check is what keeps it from being held.
        var shaking = BonePath(i => new Vector3(i % 2 == 0 ? .02f : 0, 0, 0));
        check(PinPlanting.Find(shaking, 60, 0, 119, false, PinSensitivity.Normal, 6, 8, 8).Stretches.IsEmpty, "a foot shaking from sample to sample isn't held");
        // The window is two frames whatever the clip's rate: at 30 samples a second the same speeds give the same answers.
        check(!PinPlanting.Find(BonePath(i => new Vector3(.03f * i / 30, 0, 0)), 30, 0, 119, false, PinSensitivity.Normal, 3, 4, 4).Stretches.IsEmpty &&
              PinPlanting.Find(BonePath(i => new Vector3(.06f * i / 30, 0, 0)), 30, 0, 119, false, PinSensitivity.Normal, 3, 4, 4).Stretches.IsEmpty,
            "speeds read the same at 30 samples a second");
        // A lift that eases off the floor from sample 40: the hold ends within a few samples of it.
        var easedLift = BonePath(i => i < 40 ? Vector3.Zero : new Vector3(.1f * (1 - MathF.Cos(MathF.PI * Math.Min(i - 40, 40) / 40)), 0, 0));
        check(PinPlanting.Find(easedLift, 60, 0, 119, false, PinSensitivity.Normal, 6, 8, 8).Stretches is [{ First: 0, Last: >= 40 and <= 44 }, ..],
            "a hold ends where an eased lift gets going");
        var turning = BonePath(_ => Vector3.Zero, i => Q(Vector3.UnitY, i * .5f));
        check(PinPlanting.Find(turning, 60, 0, 119, false, PinSensitivity.Normal, 6, 8, 8).Stretches.IsEmpty &&
              !PinPlanting.Find(turning, 60, 0, 119, false, PinSensitivity.Loose, 6, 8, 8).Stretches.IsEmpty, "a turning bone isn't planted unless loose allows its turn");
        check(PinPlanting.Find(Step(), 60, 70, 119, false, PinSensitivity.Normal, 6, 8, 8).Stretches is [{ First: 70, Last: 119 }], "only the layer's samples count");
        // A pin attached to a bone gives its heights in model space, as its own space's Y isn't up.
        float[] raised = [.. Enumerable.Range(0, 120).Select(i => i < 60 ? 0f : .1f)];
        check(PinPlanting.Find(Still(), 60, 0, 119, false, PinSensitivity.Normal, 6, 8, 8).Stretches is [{ First: 0, Last: 119 }] &&
              PinPlanting.Find(Still(), 60, 0, 119, false, PinSensitivity.Normal, 6, 8, 8, raised).Stretches is [{ First: 0, Last: 59 }],
            "the height test reads the heights given rather than the path's Y");
        check(PinPlanting.SamplesOf(4, new MotionClip(Body(), 3.97f, 239)) == 8 && PinPlanting.SamplesOf(1, new MotionClip(Body(), 1, 31)) == 1,
            "frames convert at the clip's rate");
        // Facts are timeline frames: sample 90 of 120 over 2 s is frame 45.4, sample 29 is frame 14.6.
        check(PinPlanting.Facts(loop, new MotionClip(Body(), 2, 120) { Loops = true }) == "Held 45-60, 0-15", "a stretch over the seam reads as its two parts");
        check(PinPlanting.Facts(step, new MotionClip(Body(), 119 / 30f, 120)) == $"Held 0-39, {step.Stretches[1].First}-119", "stretches list as frame ranges");
        var whole = PinPlanting.Find(Still(), 30, 0, 119, true, PinSensitivity.Normal, 3, 4, 4);
        check(PinPlanting.Facts(whole, new MotionClip(Body(), 119 / 30f, 120) { Loops = true }) == "Held 0-119 (the whole loop)" &&
              whole.Hold(0) == 1 && whole.Hold(119) == 1 && Degrees(whole.Stretches[0].Held.Rotation, Q(Vector3.UnitY, 30)) < .01f &&
              PinPlanting.Facts(PinPlanting.Find(Drift(1), 30, 0, 119, false, PinSensitivity.Strict, 3, 4, 4), new MotionClip(Body(), 119 / 30f, 120)) == "Not held anywhere",
            "a whole-loop stretch and no stretch say so");
        // Two stretches whose eases overlap: the target slides between their spots rather than jumping.
        var close = PinPlanting.Find(BonePath(i => i < 50 ? Vector3.Zero : i < 56 ? new Vector3(.05f * (i - 49), .1f, 0) : new Vector3(.3f, 0, 0)),
            60, 0, 119, false, PinSensitivity.Normal, 2, 8, 8);
        var smooth = close.Stretches.Length == 2;
        for (var i = 1; i < 120 && smooth; i++)
            smooth = close.Target(i) is { } now && close.Target(i - 1) is { } before && Vector3.Distance(now.Position, before.Position) < .1f;
        check(smooth, "overlapping eases blend the held spots without a jump");

        // On the floor: a sole whose flat foot points up its own Y, heel 8 cm under the ankle, ball 12 cm ahead of it and 7 cm down.
        var sole = new FootSole(Vector3.UnitY, new Vector3(0, -.08f, 0), new Vector3(0, -.07f, .12f));
        PlantedPath Holding(Vector3 position, Quaternion rotation) =>
            new([new PlantedStretch(0, 9, new MotionTransform(position, rotation, Vector3.One)), new PlantedStretch(12, 19, new MotionTransform(new Vector3(.3f, .08f, 0), Quaternion.Identity, Vector3.One))], 20, false, 2);
        var heading = Q(Vector3.UnitY, 40);
        var tipped = Q(Vector3.UnitX, 10) * heading;
        var floated = PinPlanting.OnFloor(Holding(new Vector3(.1f, .11f, .2f), tipped), sole, true);
        var level = floated.Stretches[0].Held;
        check(Vector3.Distance(level.Position, new Vector3(.1f, .08f, .2f)) < 1e-5f && Vector3.Transform(Vector3.UnitY, level.Rotation).Y > MathF.Cos(.1f * MathF.PI / 180) &&
              Vector3.Distance(Vector3.Transform(Vector3.UnitZ, level.Rotation), Vector3.Transform(Vector3.UnitZ, heading)) < 1e-4f &&
              floated.Stretches[1] == Holding(Vector3.Zero, Quaternion.Identity).Stretches[1] && floated.Stretches[0] is { First: 0, Last: 9 },
            "a foot floating 3 cm and tipped 10° is levelled with its heading kept and set on the floor; one already flat on it stays");
        var sunk = PinPlanting.OnFloor(Holding(new Vector3(0, .05f, 0), Quaternion.Identity), sole, true).Stretches[0].Held;
        check(MathF.Abs(sunk.Position.Y - .08f) < 1e-5f && sunk.Rotation == Quaternion.Identity, "a foot sunk into the floor comes up onto it");
        // Toes down.
        var kneel = Q(Vector3.UnitX, 60);
        var knelt = PinPlanting.OnFloor(Holding(new Vector3(0, .2f, 0), kneel), sole, true).Stretches[0].Held;
        var ballY = knelt.Position.Y + Vector3.Transform(sole.Ball, knelt.Rotation).Y;
        check(Degrees(knelt.Rotation, kneel) < .01f && MathF.Abs(ballY) < 1e-5f && knelt.Position.Y + Vector3.Transform(sole.Heel, knelt.Rotation).Y > ballY,
            "a foot tipped 60°, as in a kneel, keeps its angle and lands on its ball");
        var positionOnly = PinPlanting.OnFloor(Holding(new Vector3(0, .11f, 0), tipped), sole, false).Stretches[0].Held;
        check(positionOnly.Rotation == tipped && MathF.Abs(positionOnly.Position.Y + Math.Min(Vector3.Transform(sole.Heel, tipped).Y, Vector3.Transform(sole.Ball, tipped).Y)) < 1e-5f,
            "a pin holding only its position keeps the motion's turn and only sets the height");
        check(PinPlanting.Sole(Body(), FootL) is { } bodySole && PinPlanting.Sole(Body(), HandL) == null &&
              Vector3.Distance(Vector3.Transform(bodySole.Up, Model()[FootL].Rotation), Vector3.UnitY) < 1e-5f &&
              MathF.Abs(Model()[FootL].Position.Y + Vector3.Transform(bodySole.Heel, Model()[FootL].Rotation).Y) < 1e-5f &&
              MathF.Abs(Model()[FootL].Position.Y + Vector3.Transform(bodySole.Ball, Model()[FootL].Rotation).Y) < 1e-5f,
            "a foot's sole is its rest pose's, flat on the floor under the ankle and the toe joint; a hand has none");
    }

    private static MotionTransform[] Rest(SkeletonDescription? skeleton = null) => [.. (skeleton ?? Body()).Bones.Select(bone => MotionTransform.From(bone.Reference))];

    private static MotionTransform[] Model(SkeletonDescription? skeleton = null) => MotionEvaluator.ModelSpace(skeleton ?? Body(), Rest(skeleton));

    /// <summary>The rest pose's local and model transforms, fresh arrays for the solver to change.</summary>
    private static (MotionTransform[] Local, MotionTransform[] Model) Pose(SkeletonDescription? skeleton = null) => (Rest(skeleton), Model(skeleton));

    /// <summary><see cref="Body"/> with the left calf link and ankle on the knee: the shin has no length.</summary>
    private static SkeletonDescription BodyWithZeroCalf()
    {
        var body = Body();
        return body with
        {
            Bones = body.Bones.SetItem(CalfL, body.Bones[CalfL] with { Reference = T(Vector3.Zero) })
                .SetItem(FootL, body.Bones[FootL] with { Reference = T(Vector3.Zero) }),
        };
    }

    private static Vector3 BendNormal(MotionTransform[] model, PinChain chain) => Vector3.Normalize(Vector3.Cross(
        model[chain.Mid].Position - model[chain.Root].Position, model[chain.End].Position - model[chain.Root].Position));

    /// <summary>The unit direction from the root-end line out to the mid joint.</summary>
    private static Vector3 KneeSide(MotionTransform[] model, PinChain chain)
    {
        var a = model[chain.Root].Position;
        var axis = Vector3.Normalize(model[chain.End].Position - a);
        var knee = model[chain.Mid].Position - a;
        return Vector3.Normalize(knee - Vector3.Dot(knee, axis) * axis);
    }

    private static bool Finite(MotionTransform[] pose) => pose.All(t =>
        float.IsFinite(t.Position.X) && float.IsFinite(t.Position.Y) && float.IsFinite(t.Position.Z) &&
        float.IsFinite(t.Rotation.X) && float.IsFinite(t.Rotation.Y) && float.IsFinite(t.Rotation.Z) && float.IsFinite(t.Rotation.W));

    private static void Solver(Action<bool, string> check)
    {
        var body = Body();
        var chain = PinChains.Chain(body, new(PinChains.FootL));
        var normal = PinSolver.RestNormal(body, chain);
        check(normal.Length() > .5f, "a bent rest limb gives a bend normal");
        var (local, model) = Pose();
        var l1 = Vector3.Distance(model[KneeL].Position, model[ThighL].Position);
        var l2 = Vector3.Distance(model[FootL].Position, model[KneeL].Position);
        var aim = model[FootL].Position + new Vector3(.02f, .03f, -.01f);
        PinSolver.Reach(body, local, model, chain, aim, normal);
        check(Vector3.Distance(model[FootL].Position, aim) < 1e-4f, "a target within reach is hit to 0.1 mm");
        check(Math.Abs(Vector3.Distance(model[KneeL].Position, model[ThighL].Position) - l1) < 1e-5f &&
              Math.Abs(Vector3.Distance(model[FootL].Position, model[KneeL].Position) - l2) < 1e-5f, "limb lengths never change");
        check(Vector3.Dot(BendNormal(model, chain), BendNormal(Model(), chain)) > 0, "the knee stays on the motion's side");
        check(local[CalfL] == Rest()[CalfL] && local[ToesL] == Rest()[ToesL], "the calf link and the toes keep their own local motion");
        check(MotionEvaluator.ModelSpace(body, local).Zip(model).All(pair => Vector3.Distance(pair.First.Position, pair.Second.Position) < 1e-5f &&
                  Degrees(pair.First.Rotation, pair.Second.Rotation) < .01f), "the solver keeps model space in step with the local pose");

        // Soft reach: the end falls short along the root-aim line, never further than reach, without a jump.
        var reach = l1 + l2;
        float? previous = null;
        var smooth = true;
        foreach (var d in new[] { reach - .05f, reach - .02f, reach - .01f, reach, reach + .05f, reach + .2f })
        {
            var (l, m) = Pose();
            var target = m[ThighL].Position + Vector3.Normalize(new Vector3(.3f, -1, .2f)) * d;
            PinSolver.Reach(body, l, m, chain, target, normal);
            var got = Vector3.Distance(m[FootL].Position, m[ThighL].Position);
            smooth &= got <= reach + 1e-5f && got <= d + 1e-5f && (previous == null || got >= previous - 1e-5f) &&
                      Vector3.Cross(m[FootL].Position - m[ThighL].Position, Vector3.Normalize(target - m[ThighL].Position)).Length() < 1e-4f;
            previous = got;
        }
        check(smooth, "beyond reach the limb straightens smoothly and the end falls short along the line to the target");
        check(Math.Abs(PinSolver.SoftDistance(.5f, 1) - .5f) < 1e-6f && PinSolver.SoftDistance(2, 1) <= 1 && PinSolver.SoftDistance(1.01f, 1) < 1 &&
              PinSolver.SoftDistance(1, 1) > .98f,
            "soft distance eases only past the soft zone");

        // Degenerate aims: no NaN, the limb left alone.
        var (dl, dm) = Pose();
        PinSolver.Reach(body, dl, dm, chain, dm[ThighL].Position, normal);
        check(Finite(dm) && Vector3.Distance(dm[FootL].Position, Model()[FootL].Position) < 1e-6f, "an aim at the root leaves the limb as it was");
        var flat = BodyWithZeroCalf();
        var (zl, zm) = Pose(flat);
        PinSolver.Reach(flat, zl, zm, PinChains.Chain(flat, new(PinChains.FootL)), zm[FootL].Position + new Vector3(.01f, 0, 0), normal);
        check(Finite(zm) && Finite(zl), "a zero-length segment doesn't break the solve");
        // A straight limb has no bend plane of its own: the rest pose's normal carried by the root decides.
        var (sl, sm) = Pose();
        sl[KneeL] = sl[KneeL] with { Rotation = Quaternion.Identity };
        sm = MotionEvaluator.ModelSpace(body, sl);
        PinSolver.Reach(body, sl, sm, chain, sm[FootL].Position + new Vector3(0, .1f, 0), normal);
        check(Finite(sm) && Vector3.Dot(BendNormal(sm, chain), BendNormal(Model(), chain)) > .99f, "a straight limb bends the rest pose's way");
        var unbent = PinSolver.RestNormal(body with { Bones = body.Bones.SetItem(KneeL, body.Bones[KneeL] with { Reference = T(new Vector3(0, -.4f, 0)) }) }, chain);
        check(unbent == Vector3.Zero, "a straight rest limb has no bend normal");

        // CCD on the tail.
        var tail = PinChains.Chain(body, new("n_sippo_d", Top: "n_sippo_a"));
        var (tl, tm) = Pose();
        // The straight tail reaches 0.3 from its top joint: the tip moves sideways and a little in, so it stays within reach.
        var tip = tm[TailD].Position + new Vector3(.01f, .005f, .002f);
        PinSolver.Reach(body, tl, tm, tail, tip, Vector3.Zero);
        var turns = tail.Bones.Select(b => Degrees(tl[b].Rotation, Rest()[b].Rotation)).ToArray();
        check(Vector3.Distance(tm[TailD].Position, tip) < 1e-4f && turns.Max() < .6f * turns.Sum(), "a three-bone chain reaches and spreads the bend");
        var (ol, om) = Pose();
        var top = om[TailA].Position;
        var away = om[TailD].Position + new Vector3(.01f, .005f, 0);
        PinSolver.Reach(body, ol, om, tail, away, Vector3.Zero);
        check(Finite(om) && Vector3.Distance(om[TailD].Position, top) <= .3f + 1e-5f && Vector3.Distance(om[TailD].Position, away) < 1e-3f &&
              Vector3.Cross(om[TailD].Position - top, Vector3.Normalize(away - top)).Length() < 1e-4f, "a chain aimed beyond reach falls short on the line to the aim");
        var (bl, _) = Pose();
        bl[TailB] = bl[TailB] with { Rotation = Q(Vector3.UnitX, 20) };
        bl[TailC] = bl[TailC] with { Rotation = Q(Vector3.UnitX, 20) };
        var bm = MotionEvaluator.ModelSpace(body, bl);
        var straighter = Model()[TailD].Position + new Vector3(0, .02f, .005f);
        PinSolver.Reach(body, bl, bm, tail, straighter, Vector3.Zero);
        check(Vector3.Distance(bm[TailD].Position, straighter) < 1e-4f, "a bent chain straightens to reach an aim farther off");

        // Sample to sample: a straight tail pulled in 1 cm while its aim crosses the line sideways bulges to one side throughout.
        Vector3? lastBulge = null;
        var steady = true;
        for (var x = -3; x <= 3; x++)
        {
            var (cl, cm) = Pose();
            var crossing = cm[TailD].Position + new Vector3(x * .001f, 0, .01f);
            PinSolver.Reach(body, cl, cm, tail, crossing, Vector3.Zero);
            var bulge = cm[TailC].Position;
            steady &= Vector3.Distance(cm[TailD].Position, crossing) < 1e-4f && (lastBulge == null || Vector3.Distance(bulge, lastBulge.Value) < .005f);
            lastBulge = bulge;
        }
        check(steady, "a straight chain pulled in keeps its bulge on one side as the aim crosses its line");

        // The knee: a leg the motion has nearly straight, or bent a little backwards, still bends forward when the foot is lifted.
        var restSide = KneeSide(Model(), chain);
        Vector3? lastKnee = null;
        var forward = true;
        foreach (var bend in new[] { 3f, 1, .2f, 0, -.2f, -1 })
        {
            var (kl, _) = Pose();
            kl[KneeL] = kl[KneeL] with { Rotation = Q(Vector3.UnitX, bend) };
            var km = MotionEvaluator.ModelSpace(body, kl);
            PinSolver.Reach(body, kl, km, chain, km[FootL].Position + new Vector3(0, .1f, 0), normal);
            forward &= Vector3.Dot(KneeSide(km, chain), restSide) > .9f && (lastKnee == null || Vector3.Distance(km[KneeL].Position, lastKnee.Value) < .01f);
            lastKnee = km[KneeL].Position;
        }
        check(forward, "a lifted foot bends the knee forward from a nearly straight or slightly backward motion, without jumping");
        // With no pull the motion's own knee stays, even bent backwards: the pin phases its plane in with its pull.
        var (wl, _) = Pose();
        wl[KneeL] = wl[KneeL] with { Rotation = Q(Vector3.UnitX, -10) };
        var wm = MotionEvaluator.ModelSpace(body, wl);
        var backKnee = wm[KneeL].Position;
        PinSolver.Reach(body, wl, wm, chain, wm[FootL].Position, normal, 0);
        check(Vector3.Distance(wm[KneeL].Position, backKnee) < 1e-5f, "a pin with no pull leaves a backward knee where the motion has it");
        // As the pull eases in, that knee swings round to the front rather than flipping over in one step.
        Vector3? lastSwing = null;
        var swings = true;
        MotionTransform[] gm = [];
        for (var step = 0; step <= 20; step++)
        {
            var (gl, _) = Pose();
            gl[KneeL] = gl[KneeL] with { Rotation = Q(Vector3.UnitX, -10) };
            gm = MotionEvaluator.ModelSpace(body, gl);
            PinSolver.Reach(body, gl, gm, chain, gm[FootL].Position + new Vector3(0, .1f, 0), normal, step / 20f);
            swings &= lastSwing == null || Vector3.Distance(gm[KneeL].Position, lastSwing.Value) < .05f;
            lastSwing = gm[KneeL].Position;
        }
        check(swings && Vector3.Dot(KneeSide(gm, chain), restSide) > .9f, "a backward knee swings round to the front as the pull eases in");
        // A leg the motion has nearly straight sits inside the soft zone; pinned where it is, it doesn't move.
        var (nl, _) = Pose();
        nl[KneeL] = nl[KneeL] with { Rotation = Q(Vector3.UnitX, 5) };
        var nm = MotionEvaluator.ModelSpace(body, nl);
        var standing = nm[FootL].Position;
        var standingKnee = nm[KneeL].Position;
        PinSolver.Reach(body, nl, nm, chain, standing, normal);
        check(Vector3.Distance(nm[FootL].Position, standing) < 1e-5f && Vector3.Distance(nm[KneeL].Position, standingKnee) < 1e-5f,
            "a nearly straight leg pinned where it stands doesn't move");

        // Hold turn.
        var (hl, hm) = Pose();
        var want = Quaternion.Normalize(Q(Vector3.UnitY, 25) * hm[FootL].Rotation);
        PinSolver.HoldTurn(body, hl, hm, FootL, want);
        check(Degrees(hm[FootL].Rotation, want) < .01f && Degrees(hm[ToesL].Rotation, Quaternion.Normalize(want * Rest()[ToesL].Rotation)) < .01f,
            "the held turn lands in model space and the toes follow");
    }

    /// <summary>Whether the limb's mid joint bends to the rest pose's side, in its upper bone's own space; a straight limb counts as forward.</summary>
    private static bool Forward(MotionTransform[] model, PinChain chain, Vector3 restNormal)
    {
        var upper = model[chain.Mid].Position - model[chain.Root].Position;
        var lower = model[chain.End].Position - model[chain.Mid].Position;
        var hinge = Vector3.Transform(Vector3.Cross(upper, lower), Quaternion.Inverse(Quaternion.Normalize(model[chain.Root].Rotation)));
        return hinge.Length() < .0175f * upper.Length() * lower.Length() || Vector3.Dot(hinge, restNormal) > 0;
    }

    /// <summary>The angle between the limb's upper and lower bones, in degrees: 0 straight.</summary>
    private static float Bent(MotionTransform[] model, PinChain chain)
    {
        var upper = Vector3.Normalize(model[chain.Mid].Position - model[chain.Root].Position);
        var lower = Vector3.Normalize(model[chain.End].Position - model[chain.Mid].Position);
        return MathF.Acos(Math.Clamp(Vector3.Dot(upper, lower), -1, 1)) * 180 / MathF.PI;
    }

    /// <summary>Knee and elbow limits, and the bend angle that turns a limb about the line from its root to its aim.</summary>
    private static void Limits(Action<bool, string> check)
    {
        var body = Body();
        // Aims all round the root, near and far: the knee and the elbow only ever bend forward, and the end still reaches. From the
        // rest pose's bend, and from a limb the motion has straight or nearly so, where the roll eases out and only the mirror helps.
        foreach (var (end, start) in new[] { (PinChains.FootL, 20f), (PinChains.FootL, 0), (PinChains.FootL, 2), ("j_te_l", 20), ("j_te_l", 0), ("j_te_l", 2) })
        {
            var chain = PinChains.Chain(body, new(end));
            (MotionTransform[], MotionTransform[]) Pose()
            {
                var l = Rest();
                if (start != 20)
                    l[chain.Mid] = l[chain.Mid] with { Rotation = Q(end == PinChains.FootL ? Vector3.UnitX : Vector3.UnitY, start) };
                return (l, MotionEvaluator.ModelSpace(body, l));
            }
            var normal = PinSolver.RestNormal(body, chain);
            var rest = Model();
            var reach = Vector3.Distance(rest[chain.Mid].Position, rest[chain.Root].Position) + Vector3.Distance(rest[chain.End].Position, rest[chain.Mid].Position);
            var backwards = new List<string>();
            var reaches = true;
            for (var x = -1; x <= 1; x++)
                for (var y = -1; y <= 1; y++)
                    for (var z = -1; z <= 1; z++)
                    {
                        if (x == 0 && y == 0 && z == 0)
                            continue;
                        foreach (var share in new[] { .5f, .75f, .95f })
                        {
                            var (l, m) = Pose();
                            var aim = m[chain.Root].Position + Vector3.Normalize(new Vector3(x, y, z)) * share * reach;
                            PinSolver.Reach(body, l, m, chain, aim, normal);
                            if (!Forward(m, chain, normal))
                                backwards.Add($"({x},{y},{z})x{share}");
                            reaches &= Vector3.Distance(m[chain.End].Position, aim) < 1e-4f;
                        }
                    }
            check(backwards.Count == 0, $"{end} bent {start} bends forward for aims all round its root{(backwards.Count > 0 ? ": backward at " + string.Join(" ", backwards) : "")}");
            check(reaches, $"{end} bent {start} reaches aims all round its root");
        }

        var leg = PinChains.Chain(body, new(PinChains.FootL));
        var legNormal = PinSolver.RestNormal(body, leg);
        // The foot raised sideways past the hip: the free solver bent the knee backwards here.
        var (sl, sm) = Pose();
        var side = sm[ThighL].Position + Vector3.Normalize(new Vector3(1, 1, 0)) * .6f;
        PinSolver.Reach(body, sl, sm, leg, side, legNormal);
        check(Forward(sm, leg, legNormal) && Vector3.Distance(sm[FootL].Position, side) < 1e-4f && Vector3.Dot(KneeSide(sm, leg), Vector3.UnitZ) > 0,
            "a foot raised sideways past the hip bends the knee forward and still reaches");

        // An aim pulled in toward the hip: the knee folds to MaxBend and no further, the foot falling short along the line, without a jump.
        var most = MathF.Sqrt(.4f * .4f * 2 - 2 * .4f * .4f * MathF.Cos((180 - PinSolver.MaxBend) * MathF.PI / 180));
        var line = Vector3.Normalize(Model()[FootL].Position - Model()[ThighL].Position);
        float? last = null;
        var limited = true;
        var deepest = 0f;
        for (var i = 0; i <= 20; i++)
        {
            var (l, m) = Pose();
            var d = .42f - i * .02f;
            var aim = m[ThighL].Position + line * d;
            PinSolver.Reach(body, l, m, leg, aim, legNormal);
            var got = Vector3.Distance(m[FootL].Position, m[ThighL].Position);
            deepest = Math.Max(deepest, Bent(m, leg));
            limited &= Bent(m, leg) <= PinSolver.MaxBend + .01f && got >= most - 1e-4f && got >= d - 1e-5f &&
                       (last == null || got <= last + 1e-5f && last - got <= .02f + 1e-5f) && Forward(m, leg, legNormal) &&
                       Vector3.Cross(m[FootL].Position - m[ThighL].Position, line).Length() < 1e-4f;
            last = got;
        }
        check(limited && deepest > PinSolver.MaxBend - .5f, "an aim at the hip folds the knee to 150 degrees and no further, the foot falling short smoothly");
        // A knee the motion folds past the limit isn't straightened by a pin where it is, and folds no further.
        var (dl, _) = Pose();
        dl[KneeL] = dl[KneeL] with { Rotation = Q(Vector3.UnitX, 160) };
        var dm = MotionEvaluator.ModelSpace(body, dl);
        var deepKnee = dm[KneeL].Position;
        var deepFoot = dm[FootL].Position;
        PinSolver.Reach(body, dl, dm, leg, deepFoot, legNormal);
        check(Vector3.Distance(dm[KneeL].Position, deepKnee) < 1e-5f && Vector3.Distance(dm[FootL].Position, deepFoot) < 1e-5f,
            "a knee the motion folds past the limit, pinned where it is, doesn't move");
        var (fl, _) = Pose();
        fl[KneeL] = fl[KneeL] with { Rotation = Q(Vector3.UnitX, 160) };
        var fm = MotionEvaluator.ModelSpace(body, fl);
        PinSolver.Reach(body, fl, fm, leg, fm[ThighL].Position + (fm[FootL].Position - fm[ThighL].Position) * .3f, legNormal);
        check(Bent(fm, leg) <= 160.01f, "a knee the motion folds past the limit folds no further");

        // Bend turns the bent limb about the line from its root to its aim; 0 is the solver's own pick.
        var lifted = Model()[FootL].Position + new Vector3(0, .15f, .1f);
        var (bl, bm) = Pose();
        PinSolver.Reach(body, bl, bm, leg, lifted, legNormal);
        var (zl, zm) = Pose();
        PinSolver.Reach(body, zl, zm, leg, lifted, legNormal, 1, 0);
        check(bl.SequenceEqual(zl), "a bend of 0 leaves the solve as it was");
        var axis = Vector3.Normalize(lifted - Model()[ThighL].Position);
        float Turned(MotionTransform[] from, MotionTransform[] to)
        {
            var a = KneeSide(from, leg);
            var b = KneeSide(to, leg);
            return MathF.Atan2(Vector3.Dot(Vector3.Cross(a, b), axis), Vector3.Dot(a, b)) * 180 / MathF.PI;
        }
        var (nl, nm) = Pose();
        PinSolver.Reach(body, nl, nm, leg, lifted, legNormal, 1, 90);
        check(MathF.Abs(Turned(bm, nm) - 90) < .5f && Vector3.Distance(nm[FootL].Position, lifted) < 1e-4f && MathF.Abs(Bent(nm, leg) - Bent(bm, leg)) < .01f &&
              Forward(nm, leg, legNormal), "a bend of 90 turns the knee a quarter turn about the line to the aim and keeps the limb's shape");
        var (hl, hm) = Pose();
        PinSolver.Reach(body, hl, hm, leg, lifted, legNormal, .5f, 90);
        check(MathF.Abs(Turned(bm, hm) - 45) < .5f, "the bend comes in with the pull");
        var (ml, mm) = Pose();
        PinSolver.Reach(body, ml, mm, leg, lifted, legNormal, 1, -90);
        check(MathF.Abs(Turned(bm, mm) + 90) < .5f && Vector3.Distance(mm[FootL].Position, lifted) < 1e-4f, "a negative bend turns the other way");
        // A pin carries its bend to the solver.
        var (pl, pm) = Pose();
        PinSolver.Pin(body, pl, pm, leg, new MotionPin(PinChains.FootL, HoldTurn: false, Bend: 90), -1, new MotionTransform(lifted, Quaternion.Identity, Vector3.One), 1, legNormal);
        check(pl.SequenceEqual(nl), "a pin's bend turns its limb as the solver's does");

        // A drag with a bend lands the hand and turns the elbow about the line to the aim.
        var before = Rest();
        before[UpperArmL] = before[UpperArmL] with { Rotation = Q(Vector3.UnitZ, -20) };
        before[ForearmL] = before[ForearmL] with { Rotation = Q(Vector3.UnitY, 60) };
        var hand = MotionEvaluator.ModelSpace(body, before)[HandL].Position;
        var near = hand + new Vector3(.05f, 0, .05f);
        var layer = BoneLayer(MotionLayerKind.Offset, 1);
        var doc = new TimelineDocument(100, 100, null, [], 1) with { Motion = [layer] };
        var arm = PinChains.Chain(body, new("j_te_l"));
        MotionTransform[] Dragged(float bend) => MotionEvaluator.ModelSpace(body, MotionEvaluator.ApplyAt(body, before,
            [MotionEdits.Find(PoseEdits.Reach(doc, layer, 0, body, before, before, "j_te_l", near, 1, bend: bend), "m1")!], 0));
        var straight = Dragged(0);
        var turned = Dragged(60);
        var elbowAxis = Vector3.Normalize(near - straight[UpperArmL].Position);
        var from = KneeSide(straight, arm);
        var to = KneeSide(turned, arm);
        check(Vector3.Distance(turned[HandL].Position, near) < .001f &&
              MathF.Abs(MathF.Atan2(Vector3.Dot(Vector3.Cross(from, to), elbowAxis), Vector3.Dot(from, to)) * 180 / MathF.PI - 60) < .5f,
            "a drag with a bend lands the hand and turns the elbow about the line to the aim");
    }

    // A small body: both legs from the hips, the spine up to the left arm, and a tail. Each leg's knee
    // is bent 20° at rest and so is the left elbow, so the rest pose has bend normals.
    private const int Root = 0, Hara = 1, Kosi = 2, ThighL = 3, KneeL = 4, CalfL = 5, FootL = 6, ToesL = 7,
        ThighR = 8, KneeR = 9, CalfR = 10, FootR = 11, ToesR = 12, SpineA = 13, SpineB = 14, Chest = 15,
        ClavicleL = 16, UpperArmL = 17, ForearmL = 18, HandL = 19, TailA = 20, TailB = 21, TailC = 22, TailD = 23;

    private static SkeletonDescription Body() => new("body", "body-fingerprint",
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
        new("j_sebo_a", Kosi, 0, T(new Vector3(0, .1f, 0))),
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
    ], [], [], []);

    /// <summary>Every bone of <see cref="Body"/> but n_sippo_c has a track.</summary>
    private static ImmutableArray<short> Tracked() => [.. Enumerable.Range(0, Body().Bones.Length).Where(b => b != TailC).Select(b => (short)b)];

    private static string[] Names(IEnumerable<int> bones) => [.. bones.Select(b => Body().Bones[b].Name)];

    private static void Chains(Action<bool, string> check, Action<Action, string> reject)
    {
        var body = Body();
        var leg = PinChains.Chain(body, new(PinChains.FootL));
        check(leg.Shape == PinChainShape.Limb && Names(leg.Bones).SequenceEqual(["j_asi_a_l", "j_asi_b_l"]) && leg.Root == ThighL && leg.Mid == KneeL && leg.End == FootL,
            "a pinned foot bends the thigh and the knee, the calf link rigid");
        check(Names(PinChains.Chain(body, new("j_te_l", Top: "j_sako_l")).Bones).SequenceEqual(["j_sako_l", "j_ude_a_l", "j_ude_b_l"]), "a top above the limb adds the bones above");
        check(Names(PinChains.Chain(body, new(PinChains.FootL, Top: "j_kosi")).Bones).SequenceEqual(["j_kosi", "j_asi_a_l", "j_asi_b_l"]),
            "a top above the leg adds the hips and still leaves the calf link rigid");
        check(Names(PinChains.Chain(body, new("n_sippo_d")).Bones).SequenceEqual(["n_sippo_b", "n_sippo_c"]) && PinChains.DefaultTop(body, "n_sippo_d") == "n_sippo_b",
            "another bone bends two bones up");
        check(PinChains.DefaultTop(body, "j_te_l") == "j_ude_a_l" && PinChains.Chain(body, new("j_te_l")) is { Shape: PinChainShape.Limb, Root: UpperArmL, Mid: ForearmL },
            "a pinned hand bends the upper arm and the forearm by default");
        check(PinChains.Chain(body, new("j_te_l", Top: "j_ude_b_l")) is { Shape: PinChainShape.Chain, Root: -1, Mid: -1 } chain && Names(chain.Bones).SequenceEqual(["j_ude_b_l"]),
            "a top inside the limb bends only what it allows, as a chain");
        check(Names(PinChains.Chain(body, new("n_sippo_b")).Bones).SequenceEqual(["j_kosi", "n_sippo_a"]), "two up may reach the hips, which sit below the body");
        check(Names(PinChains.Chain(body, new("n_sippo_a")).Bones).SequenceEqual(["j_kosi"]), "fewer ancestors below the body use what there is");
        check(PinChains.Chain(body, new("j_kosi")) is { Shape: PinChainShape.None, Bones.IsEmpty: true } && PinChains.DefaultTop(body, "j_kosi") == null,
            "a bone under the body has nothing to bend");
        check(PinChains.TopChoices(body, "j_te_l").SequenceEqual(["j_ude_b_l", "j_ude_a_l", "j_sako_l", "j_sebo_c", "j_sebo_b", "j_sebo_a", "j_kosi"]), "top choices stop before the body");
        check(PinChains.TopChoices(body, "j_te_l", "j_sebo_b").SequenceEqual(["j_ude_b_l", "j_ude_a_l", "j_sako_l", "j_sebo_c"]) &&
              PinChains.TopChoices(body, "j_te_l", "j_asi_a_l").SequenceEqual(["j_ude_b_l", "j_ude_a_l", "j_sako_l", "j_sebo_c", "j_sebo_b", "j_sebo_a"]),
            "top choices leave out the bone the pin rides and every bone it hangs from");
        check(Names(PinChains.Changed(body, new(PinChains.FootL))).Order().SequenceEqual(new[] { "j_asi_a_l", "j_asi_b_l", "j_asi_d_l" }.Order()),
            "a pin changes its bone and the bones it bends, not the rigid link");
        reject(() => PinChains.Check(body, PinLayer([new("j_nothing")])), "a pinned bone the skeleton lacks is refused");
        reject(() => PinChains.Check(body, PinLayer([new(PinChains.FootL, RelativeTo: "j_nothing")])), "a relative bone the skeleton lacks is refused");
        reject(() => PinChains.Check(body, PinLayer([new(PinChains.FootL, Top: "j_sako_l")])), "a top that is not an ancestor is refused");
        reject(() => PinChains.Check(body, PinLayer([new(PinChains.FootL, Top: "n_hara")])), "the body as top is refused");
        reject(() => PinChains.Check(body, PinLayer([new(PinChains.FootL, Top: PinChains.FootL)])), "the pinned bone as its own top is refused");
        reject(() => PinChains.Check(body, PinLayer([new(PinChains.FootL, RelativeTo: PinChains.FootL)])), "relative to itself is refused");
        reject(() => PinChains.Check(body, PinLayer([new(PinChains.FootL, RelativeTo: "j_asi_e_l")])), "relative to a bone below the pin is refused");
        reject(() => PinChains.Check(body, PinLayer([new("j_te_l", Top: "j_sebo_a", RelativeTo: "j_sebo_c")])), "relative to a bone in the chain is refused");
        reject(() => PinChains.Check(body, PinLayer([new(PinChains.FootL, RelativeTo: "j_asi_c_l")])), "relative to a bone below the knee the pin bends is refused");
        reject(() => PinChains.Check(body, PinLayer([new("j_te_l", Top: "j_kosi", RelativeTo: "j_asi_a_l")])), "relative to a bone hanging from a chain bone is refused");
        check(!PinChains.Changed(body, new("j_kosi")).Any(), "a pin with nothing to bend changes no bone");
        PinChains.Check(body, PinLayer([new("j_te_l", RelativeTo: "j_sebo_c")]));
        check(PinChains.Untracked(body, PinLayer([new("n_sippo_d", Planted: true)]), Tracked()).SequenceEqual(["n_sippo_c"]) &&
              PinChains.Untracked(body, PinLayer([new("n_sippo_d", Planted: true)]), []).IsEmpty, "a chain bone without a track is named");
        check(PinChains.Untracked(body, PinLayer([new("n_sippo_d")]), Tracked()).IsEmpty &&
              PinChains.Untracked(body, PinLayer([new("n_sippo_d")], [Key(0, ("n_sippo_d", T(Vector3.Zero)))]), Tracked()).SequenceEqual(["n_sippo_c"]),
            "a keyed pin without keys bends nothing, so it names no bone");
        reject(() => MotionEvaluator.CheckBones([PinLayer([new(PinChains.FootL, Top: "j_sako_l")])], body), "CheckBones runs the pin rules");
    }

    private static Quaternion Q(Vector3 axis, float degrees) => Quaternion.CreateFromAxisAngle(axis, degrees * MathF.PI / 180);

    /// <summary>The twist of <paramref name="q"/> about the unit <paramref name="axis"/>, in degrees.</summary>
    private static float Roll(Quaternion q, Vector3 axis)
    {
        q = Quaternion.Normalize(q.W < 0 ? -q : q);
        var along = Vector3.Dot(new Vector3(q.X, q.Y, q.Z), axis);
        return 2 * MathF.Atan2(along, q.W) * 180 / MathF.PI;
    }

    private static float Degrees(Quaternion a, Quaternion b) =>
        2 * MathF.Acos(Math.Clamp(Math.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))), 0, 1)) * 180 / MathF.PI;

    private static BoneTransform T(Vector3 position, Quaternion? rotation = null) => new(position, rotation ?? Quaternion.Identity, Vector3.One);

    private static MotionLayer PinLayer(ImmutableArray<MotionPin> pins, ImmutableArray<MotionKey> keys = default, int start = 0, int end = 30,
        int fadeIn = 0, int fadeOut = 0, float amount = 1, MotionInterpolation ease = MotionInterpolation.Linear, string key = "m1") =>
        new(key, MotionLayerKind.Pin, "Pin 1", start, end, fadeIn, fadeOut, amount, PoseComponents.Rotation | PoseComponents.Position, ease, [], keys, Pins: pins);

    private static MotionKey Key(int frame, params (string Bone, BoneTransform Value)[] bones) =>
        new(frame, ImmutableDictionary.CreateRange(StringComparer.Ordinal, bones.Select(b => KeyValuePair.Create(b.Bone, b.Value))));

    private static void Validation(Action<bool, string> check, Action<Action, string> reject)
    {
        var foot = PinChains.FootL;
        reject(() => MotionEvaluator.Validate(PinLayer([new(foot), new(foot)])), "a bone pinned twice is refused");
        reject(() => MotionEvaluator.Validate(PinLayer([new("")])), "a pin without a bone is refused");
        reject(() => MotionEvaluator.Validate(PinLayer([new(foot)], [Key(0, ("j_te_l", T(Vector3.Zero)))])), "a key naming an unpinned bone is refused");
        reject(() => MotionEvaluator.Validate(PinLayer([new(foot, Planted: true)], [Key(0, (foot, T(Vector3.Zero)))])), "a key naming a planted pin is refused");
        reject(() => MotionEvaluator.Validate(PinLayer([new(foot)], amount: 1.5f)), "a pull above 1 is refused");
        reject(() => MotionEvaluator.Validate(PinLayer([new(foot, Sensitivity: (PinSensitivity)7)])), "an unknown sensitivity is refused");
        reject(() => MotionEvaluator.Validate(new MotionLayer("m1", MotionLayerKind.Offset, "Offset 1", 0, 30, 0, 0, 1, PoseComponents.All,
            MotionInterpolation.Linear, Pins: [new(foot)])), "pins on an offset layer are refused");
        MotionEvaluator.Validate(PinLayer([new(foot, Planted: true), new(PinChains.FootR)], [Key(0, (PinChains.FootR, T(Vector3.Zero)))]));
        check(!MotionEvaluator.Active(PinLayer([])) && MotionEvaluator.Active(PinLayer([new(foot, Planted: true)])) &&
              !MotionEvaluator.Active(PinLayer([new(foot)])) && MotionEvaluator.Active(PinLayer([new(foot)], [Key(0, (foot, T(Vector3.Zero)))])) &&
              !MotionEvaluator.Active(PinLayer([new(foot, Planted: true)], amount: 0)), "a pin layer is active with a planted pin or a keyed one, and a pull");
        check(MotionEvaluator.Weight(PinLayer([new(foot)], amount: 1.5f), 15, 30) == 1 && MotionEdits.MaxAmount(MotionLayerKind.Pin) == 1 &&
              MotionEdits.KindName(MotionLayerKind.Pin) == "Pin",
            "a pin's pull caps at 1 and the kind is named Pin");
        var a = PinLayer([new(foot)]);
        check(!MotionEdits.Same(a, a with { Pins = [new(foot, HoldTurn: false)] }) && MotionEdits.Same(a, a with { Pins = [new(foot)] }), "layers differ by their pins");
        check(!MotionEdits.Same(a, a with { Pins = [new(foot, Bend: 30)] }), "layers differ by a pin's bend");
        reject(() => MotionEvaluator.Validate(PinLayer([new(foot, Bend: float.NaN)])), "a bend that isn't a number is refused");
        reject(() => MotionEvaluator.Validate(PinLayer([new(foot, Bend: 200)])), "a bend past a half turn is refused");
        MotionEvaluator.Validate(PinLayer([new(foot, Bend: 180), new(PinChains.FootR, Bend: -180)]));
    }

    private static void Journals(Action<bool, string> check)
    {
        var temp = Path.Combine(Path.GetTempPath(), "ie-pin-layer-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var resource = new AnimationResource("chara/human/c0801/animation/a0001/bt_common/emote/pose06_loop.pap", "pose06_loop.pap", "hash");
            var clip = new AnimationClip(resource.GamePath, "loop", 0, 0, 42, "chara/test.sklb");
            var capture = new AnimationCapture("clip", 1, 1, Guid.NewGuid(), "Test", "Idle", clip, null, [resource.GamePath], [resource], DateTime.UtcNow, true);
            var foot = PinChains.FootL;
            var layer = PinLayer([new(foot, HoldTurn: false, RelativeTo: "j_sebo_c", Top: "j_sako_l", Bend: 45), new(PinChains.FootR, Planted: true, Sensitivity: PinSensitivity.Loose, Floor: true)],
                [Key(3, (foot, T(new Vector3(.1f, .2f, .3f), Q(Vector3.UnitY, 10))))]);
            var request = new AnimationBakeRequest(Guid.NewGuid(), capture, AnimationDestination.NewMod, "Motion", false,
                AnimationOperation.EditTimeline,
                Timeline: new AnimationTimelineEdit(false, 0, null, [layer]));
            new AnimationJournalStore(temp).Save(new AnimationEditJournal { Id = request.Id, Request = AnimationCommitService.RecoveryRequest(request) });
            var loaded = new AnimationJournalStore(temp).Load().Single().Request.Timeline!.Motion.Single();
            check(loaded.Kind == MotionLayerKind.Pin && loaded.Pins.SequenceEqual(layer.Pins) && MotionEdits.Same(loaded, layer),
                "a journal keeps a pin layer, its pins and their settings");

            var path = Path.Combine(temp, "AnimationEdits", request.Id.ToString("N"), "journal.json");
            // Journals written before the bend angle have no Bend: their pins bend as the solver picks.
            var unbent = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            check(unbent["Request"]!["Timeline"]!["Motion"]![0]!["Pins"]![0]!.AsObject().Remove("Bend"), "a journal stores a pin's bend as Bend");
            // And before Floor: their feet stay where the motion has them.
            check(unbent["Request"]!["Timeline"]!["Motion"]![0]!["Pins"]![1]!.AsObject().Remove("Floor"), "a journal stores a pin's floor as Floor");
            File.WriteAllText(path, unbent.ToJsonString());
            var plain = new AnimationJournalStore(temp).Load().Single().Request.Timeline!.Motion.Single();
            check(plain.Pins[0] == layer.Pins[0] with { Bend = 0 } && plain.Pins[1] == layer.Pins[1] with { Floor = false },
                "an older journal's pins load with no bend and the feet off the floor setting");
            // Journals written before pin layers have no Pins.
            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            check(json["Request"]!["Timeline"]!["Motion"]![0]!.AsObject().Remove("Pins"), "a journal stores a layer's pins as Pins");
            File.WriteAllText(path, json.ToJsonString());
            var older = new AnimationJournalStore(temp).Load().Single().Request.Timeline!.Motion.Single();
            check(older.Pins is { IsDefault: false, IsEmpty: true } && !MotionEvaluator.Active(older), "an older journal loads with no pins");
        }
        finally { Directory.Delete(temp, true); }
    }
}
