using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json.Nodes;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui.Timeline;

/// <summary>
/// Balance layers: the hips moved so the body's centre of mass stays over the feet the motion plants, the planted feet held, a
/// lifted foot riding along; nothing in a jump, the move capped and kept to the ticked axes; their validation and journals, and a
/// skeleton without the hips or a foot left alone.
/// </summary>
internal static class BalanceLayerFixture
{
    private const string Waist = "j_sebo_a";

    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Validation(check, reject);
        Journals(check);
        Centre(check);
        Lean(check);
        OneFoot(check);
        Jump(check);
        Cap(check);
        Axes(check);
        MissingBones(check);
        OnHara(check);
        Narrow(check);
        OnePose(check);
    }

    private static void Validation(Action<bool, string> check, Action<Action, string> reject)
    {
        var balance = Balance(new MotionBalance());
        MotionEvaluator.Validate(balance);
        MotionEvaluator.Validate(Balance(new MotionBalance(Smooth: 0, MaxShift: MotionBalances.MinShift)));
        MotionEvaluator.Validate(Balance(new MotionBalance(Smooth: MotionBalances.MaxSmooth, MaxShift: MotionBalances.MaxShiftLimit)));
        void Refused(MotionLayer layer, string what) => reject(() => MotionEvaluator.Validate(layer), what);
        Refused(Balance(new MotionBalance(Smooth: -1)), "a negative balance smoothing is refused");
        Refused(Balance(new MotionBalance(Smooth: 31)), "a balance smoothing over 30 frames is refused");
        Refused(Balance(new MotionBalance(MaxShift: .009f)), "a balance shift under 1 cm is refused");
        Refused(Balance(new MotionBalance(MaxShift: .51f)), "a balance shift over 50 cm is refused");
        Refused(Balance(new MotionBalance(MaxShift: float.NaN)), "a balance shift that isn't a number is refused");
        Refused(Balance(new MotionBalance(Sensitivity: (PinSensitivity)9)), "a balance sensitivity that can't be read is refused");
        Refused(new MotionLayer("m1", MotionLayerKind.Offset, "Offset 1", 0, 60, 0, 0, 1, PoseComponents.All, MotionInterpolation.Linear,
            Balance: new MotionBalance()), "a balance on an offset layer is refused");
        Refused(balance with { Balance = null }, "a balance layer without its balance is refused");
        Refused(balance with { Keys = [new MotionKey(0, ImmutableDictionary<string, BoneTransform>.Empty.Add(PinChains.Hips, T(Vector3.Zero)))] },
            "a balance layer with keys is refused");
        Refused(balance with { Pins = [new MotionPin(PinChains.Hips)] }, "a balance layer with pins is refused");
        Refused(balance with { Bones = [PinChains.Hips] }, "a balance layer listing bones is refused");
        Refused(balance with { Amount = 1.5f }, "a balance past full strength is refused");
        check(MotionEvaluator.BlendsAtMostFully(MotionLayerKind.Balance), "a balance blends at most fully");

        check(MotionEvaluator.Active(balance), "a balance layer is active");
        check(!MotionEvaluator.Active(balance with { Amount = 0 }), "a balance at 0% does nothing");
        check(!MotionEvaluator.Active(Balance(new MotionBalance(false, false))), "a balance with neither axis does nothing");
        check(MotionEvaluator.Active(Balance(new MotionBalance(Sideways: false))), "a balance front to back only is active");
        check(!MotionEvaluator.Active(balance with { Components = PoseComponents.Rotation }), "a balance without Move does nothing");
    }

    private static void Journals(Action<bool, string> check)
    {
        var temp = Path.Combine(Path.GetTempPath(), "ie-balance-layer-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var resource = new AnimationResource("chara/human/c0801/animation/a0001/bt_common/emote/dance01.pap", "dance01.pap", "hash");
            var clip = new AnimationClip(resource.GamePath, "dance", 0, 0, 42, "chara/test.sklb");
            var capture = new AnimationCapture("clip", 1, 1, Guid.NewGuid(), "Test", "Idle", clip, null, [resource.GamePath], [resource], DateTime.UtcNow, true);
            var settings = new MotionBalance(false, true, 12, PinSensitivity.Loose, .25f);
            var balance = Balance(settings) with { Weights = [new MotionWeight(10, .5f)] };
            var offset = new MotionLayer("m2", MotionLayerKind.Offset, "Offset 1", 0, 60, 0, 0, 1, PoseComponents.All, MotionInterpolation.Linear);
            var request = new AnimationBakeRequest(Guid.NewGuid(), capture, AnimationDestination.NewMod, "Motion", false,
                AnimationOperation.EditTimeline, Timeline: new AnimationTimelineEdit(true, 0, null, [balance, offset]));
            new AnimationJournalStore(temp).Save(new AnimationEditJournal { Id = request.Id, Request = AnimationCommitService.RecoveryRequest(request) });
            var loaded = new AnimationJournalStore(temp).Load().Single().Request.Timeline!.Motion;
            check(loaded.Length == 2 && MotionEdits.Same(loaded[0], balance) && loaded[0].Balance == settings && loaded[0].Spring == null &&
                  MotionEdits.Same(loaded[1], offset), "a journal keeps a balance's settings");

            var path = Path.Combine(temp, "AnimationEdits", request.Id.ToString("N"), "journal.json");
            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var motion = json["Request"]!["Timeline"]!["Motion"]!;
            check(motion[0]!.AsObject().Remove("Balance"), "a journal stores a layer's balance as Balance");
            motion[1]!.AsObject().Remove("Balance");
            File.WriteAllText(path, json.ToJsonString());
            var older = new AnimationJournalStore(temp).Load().Single().Request.Timeline!.Motion;
            var refused = false;
            try { MotionEvaluator.Validate(older[0]); }
            catch (InvalidDataException) { refused = true; }
            check(older[0].Balance == null && refused && !MotionEvaluator.Active(older[0]), "a balance layer without Balance loads with none and is refused");
            check(MotionEdits.Same(older[1], offset), "a journal written before balances loads its other layers unchanged");
        }
        finally { Directory.Delete(temp, true); }

        var a = Balance(new MotionBalance());
        check(MotionEdits.Same(a, a with { Balance = new MotionBalance() }) && !MotionEdits.Same(a, a with { Balance = new MotionBalance(Smooth: 3) }) &&
              !MotionEdits.Same(a, a with { Balance = new MotionBalance(Sideways: false) }) && !MotionEdits.Same(a, a with { Balance = null }),
            "layers differ by their balance's settings");
    }

    private static void Centre(Action<bool, string> check)
    {
        var rig = Rig();
        var model = MotionEvaluator.ModelSpace(rig, [.. rig.Bones.Select(bone => MotionTransform.From(bone.Reference))]);
        var index = MotionEvaluator.Index(rig);
        var centre = MotionBalances.Centre(rig, model, index);
        var hips = model[index[PinChains.Hips]].Position.Y;
        check(centre is { } c && MathF.Abs(c.X) < .02f && c.Y > .8f * hips,
            $"the rest pose's centre of mass lies between the feet, about the hips' height ({centre?.X:0.###}, {centre?.Y:0.###} m)");
        var bare = new SkeletonDescription("bare", "bare-fingerprint", [new("n_root", -1, 0, T(Vector3.Zero)), new("j_other", 0, 0, T(Vector3.UnitY))], [], [], []);
        check(MotionBalances.Centre(bare, MotionEvaluator.ModelSpace(bare, [MotionTransform.Identity, MotionTransform.Identity]), MotionEvaluator.Index(bare)) == null,
            "a skeleton without any of the body's bones has no centre");
    }

    private static void Lean(Action<bool, string> check)
    {
        // Both feet still, the upper body leaning 20 degrees to the side.
        var lean = Clip(Rig(), 61, 60, (_, _) => Pose(Rig(), lean: 20));
        var balanced = Applied(lean, Balance(new MotionBalance()));
        var most = Enumerable.Range(0, 61).Max(i => Over(balanced, i, PinChains.FootL, PinChains.FootR));
        var before = Over(lean, 30, PinChains.FootL, PinChains.FootR);
        check(before > .02f && most < .01f, $"a lean's weight comes back over the feet ({before * 100:0.#} cm off before, {most * 100:0.##} cm at most after)");
        var drift = Enumerable.Range(0, 61).Max(i => Math.Max(Drift(lean, balanced, i, PinChains.FootL), Drift(lean, balanced, i, PinChains.FootR)));
        check(drift < .002f, $"the planted feet stay where they were ({drift * 1000:0.##} mm at most)");
    }

    private static void OneFoot(Action<bool, string> check)
    {
        // The right foot lifted 15 cm over frames 20 to 40.
        var step = Clip(Rig(), 61, 60, (_, f) => Pose(Rig(), liftRight: f is >= 20 and <= 40));
        var balanced = Applied(step, Balance(new MotionBalance()));
        var most = Enumerable.Range(25, 11).Max(i => Over(balanced, i, PinChains.FootL));
        check(most < .02f, $"with one foot lifted the weight goes over the other ({most * 100:0.##} cm from the left sole at most)");
        var support = MotionBalances.Support(step, Balance(new MotionBalance()), MotionEvaluator.Index(Rig()));
        check(support is { } s && s.Left.Stretches.Length == 1 && s.Right.Stretches.Length == 2, "the support reads the left foot down throughout, the right twice");
    }

    private static void Jump(Action<bool, string> check)
    {
        // Both feet off the floor over frames 20 to 40, leaning, so the hips move round it.
        var jump = Clip(Rig(), 61, 60, (_, f) => Pose(Rig(), lean: 20, liftLeft: f is >= 20 and <= 40, liftRight: f is >= 20 and <= 40));
        var balanced = Applied(jump, Balance(new MotionBalance()));
        var still = Enumerable.Range(27, 7).All(i => Enumerable.Range(0, jump.Bones).All(b => Near(jump.Get(i, b), balanced.Get(i, b), 1e-3f)));
        var moved = Vector3.Distance(Model(jump, 5, PinChains.Hips).Position, Model(balanced, 5, PinChains.Hips).Position);
        check(still && moved > .01f, $"in the middle of a jump nothing changes, while the hips move round it ({moved * 100:0.#} cm at frame 5)");
    }

    private static void Cap(Action<bool, string> check)
    {
        var lean = Clip(Rig(), 61, 60, (_, _) => Pose(Rig(), lean: 60));
        var free = Shift(lean, Applied(lean, Balance(new MotionBalance(MaxShift: MotionBalances.MaxShiftLimit))), 30);
        var capped = Enumerable.Range(0, 61).Max(i => Shift(lean, Applied(lean, Balance(new MotionBalance(MaxShift: .1f))), i));
        check(free > .15f && capped <= .1f + 1e-4f, $"Max shift caps the hips' move ({free * 100:0.#} cm uncapped, {capped * 100:0.##} cm capped at 10)");
    }

    private static void Axes(Action<bool, string> check)
    {
        var lean = Clip(Rig(), 61, 60, (_, _) => Pose(Rig(), lean: 20, forward: 15));
        var balanced = Applied(lean, Balance(new MotionBalance(Sideways: false)));
        var sideways = Enumerable.Range(0, 61).Max(i => MathF.Abs(Model(balanced, i, PinChains.Hips).Position.X - Model(lean, i, PinChains.Hips).Position.X));
        var forward = MathF.Abs(Model(balanced, 30, PinChains.Hips).Position.Z - Model(lean, 30, PinChains.Hips).Position.Z);
        check(sideways < 1e-5f && forward > .005f, $"with Sideways off the hips keep their X ({sideways * 1000:0.###} mm) and still move front to back");
    }

    private static void MissingBones(Action<bool, string> check)
    {
        var rig = Rig();
        foreach (var (name, gone) in new[] { ("a foot", PinChains.FootR), ("the hips", PinChains.Hips) })
        {
            var at = rig.Bones.IndexOf(rig.Bones.First(bone => bone.Name == gone));
            var lacking = rig with { Bones = rig.Bones.SetItem(at, rig.Bones[at] with { Name = gone + "_x" }) };
            var lean = Clip(lacking, 61, 60, (_, _) => Pose(lacking, lean: 20));
            var unchanged = false;
            try
            {
                var balanced = Applied(lean, Balance(new MotionBalance()));
                unchanged = Enumerable.Range(0, 61).All(i => Enumerable.Range(0, lean.Bones).All(b => lean.Get(i, b) == balanced.Get(i, b)));
            }
            catch (Exception) { }
            check(unchanged && MotionBalances.Support(lean, Balance(new MotionBalance()), MotionEvaluator.Index(lacking)) == null,
                $"a skeleton without {name} is left alone, without an error");
        }
    }

    private static void OnHara(Action<bool, string> check)
    {
        // A clip whose hips' move goes onto n_hara: the back hangs from n_hara and moves with it once, as far as the hips.
        var lean = Clip(Rig(), 61, 60, (_, _) => Pose(Rig(), lean: 20), hipsOnHara: true);
        var balanced = Applied(lean, Balance(new MotionBalance()));
        var hips = Shift(lean, balanced, 30);
        var back = Vector3.Distance(Model(lean, 30, Waist).Position, Model(balanced, 30, Waist).Position);
        var over = Over(balanced, 30, PinChains.FootL, PinChains.FootR);
        check(hips > .02f && MathF.Abs(back - hips) < 1e-3f && over < .01f,
            $"on a clip whose hips move n_hara the back moves as far as the hips ({back * 100:0.##} and {hips * 100:0.##} cm), over the feet ({over * 100:0.##} cm off)");
    }

    private static void Narrow(Action<bool, string> check)
    {
        // A layer from frame 10 to 50 of a still lean: the planted feet hold at its edges too.
        var lean = Clip(Rig(), 61, 60, (_, _) => Pose(Rig(), lean: 20));
        var balanced = Applied(lean, Balance(new MotionBalance()) with { Start = 10, End = 50 });
        var drift = Enumerable.Range(0, 61).Max(i => Math.Max(Drift(lean, balanced, i, PinChains.FootL), Drift(lean, balanced, i, PinChains.FootR)));
        check(drift < .002f && Shift(lean, balanced, 30) > .02f, $"a layer narrower than the clip keeps the planted feet still at its edges ({drift * 1000:0.##} mm at most)");
    }

    private static void OnePose(Action<bool, string> check)
    {
        var rig = Rig();
        var layer = Balance(new MotionBalance());
        var local = Pose(rig, lean: 20).Select(value => MotionTransform.From(value)).ToArray();
        var at = MotionEvaluator.ApplyAt(rig, local, [layer], 25, 0, 60);
        check(Enumerable.Range(0, local.Length).All(b => at[b] == local[b]) && BonePosing.TakesWholeClip([layer]),
            "one pose leaves a balance out, so the Bones window starts from the bake");
    }

    /// <summary>
    /// HipDragFixture's body with a neck and head on the chest: the standard bones of the weight table, but the
    /// game's calf and toe twists.
    /// </summary>
    internal static SkeletonDescription Rig()
    {
        var body = HipDragFixture.Body();
        var chest = (short)body.Bones.IndexOf(body.Bones.First(bone => bone.Name == "j_sebo_c"));
        var neck = (short)body.Bones.Length;
        return body with
        {
            Bones = body.Bones.Add(new("j_kubi", chest, 0, T(new Vector3(0, .12f, 0)))).Add(new("j_kao", neck, 0, T(new Vector3(0, .1f, 0)))),
        };
    }

    /// <summary>The rig's rest pose, the waist leaning <paramref name="lean"/> degrees to the side and <paramref name="forward"/> forward, a foot lifted 15 cm.</summary>
    private static BoneTransform[] Pose(SkeletonDescription rig, float lean = 0, float forward = 0, bool liftLeft = false, bool liftRight = false) =>
    [
        .. rig.Bones.Select(bone => bone.Name switch
        {
            Waist => T(bone.Reference.Position, Quaternion.Normalize(Q(Vector3.UnitZ, lean) * Q(Vector3.UnitX, forward))),
            PinChains.FootL when liftLeft => T(bone.Reference.Position + Lift, bone.Reference.Rotation),
            PinChains.FootR when liftRight => T(bone.Reference.Position + Lift, bone.Reference.Rotation),
            _ => bone.Reference,
        }),
    ];

    // In the calf's space, which the knee tilts 20 degrees: 15 cm up in the character's.
    private static readonly Vector3 Lift = Vector3.Transform(new Vector3(0, .15f, 0), Quaternion.Inverse(Q(Vector3.UnitX, 20)));

    private static MotionClip Clip(SkeletonDescription rig, int samples, float frames, Func<int, float, BoneTransform[]> pose, bool hipsOnHara = false)
    {
        var poses = Enumerable.Range(0, samples).Select(i => pose(i, frames * i / (samples - 1))).ToArray();
        var made = MotionClip.FromPoses(rig, frames / TimelineDocument.Fps, poses);
        var clip = new MotionClip(rig, made.Duration, made.Samples) { HipsOnHara = hipsOnHara };
        clip.CopyFrom(made);
        return clip;
    }

    private static MotionClip Applied(MotionClip source, params MotionLayer[] layers)
    {
        var clip = source.Clone();
        MotionEvaluator.Apply(clip, layers.Select((layer, i) => layer with { Key = "m" + (i + 1) }).ToArray());
        return clip;
    }

    private static MotionTransform[] Models(MotionClip clip, int sample) =>
        MotionEvaluator.ModelSpace(clip.Skeleton, Enumerable.Range(0, clip.Bones).Select(b => clip.Get(sample, b)).ToArray());

    private static MotionTransform Model(MotionClip clip, int sample, string bone) => Models(clip, sample)[MotionEvaluator.Index(clip.Skeleton)[bone]];

    /// <summary>How far, along the floor, the centre of mass is from the middle of the named feet's soles at <paramref name="sample"/>.</summary>
    private static float Over(MotionClip clip, int sample, params string[] feet)
    {
        var model = Models(clip, sample);
        var index = MotionEvaluator.Index(clip.Skeleton);
        var centre = MotionBalances.Centre(clip.Skeleton, model, index)!.Value;
        var soles = feet.Select(foot =>
        {
            var b = index[foot];
            var sole = PinPlanting.Sole(clip.Skeleton, b)!.Value;
            return model[b].Position + Vector3.Transform((sole.Heel + sole.Ball) / 2, model[b].Rotation);
        }).Aggregate(Vector3.Zero, (sum, at) => sum + at) / feet.Length;
        var away = centre - soles;
        return new Vector2(away.X, away.Z).Length();
    }

    /// <summary>How far a bone moved in model space at <paramref name="sample"/>.</summary>
    private static float Drift(MotionClip a, MotionClip b, int sample, string bone) => Vector3.Distance(Model(a, sample, bone).Position, Model(b, sample, bone).Position);

    /// <summary>How far the hips moved along the floor at <paramref name="sample"/>.</summary>
    private static float Shift(MotionClip a, MotionClip b, int sample)
    {
        var away = Model(b, sample, PinChains.Hips).Position - Model(a, sample, PinChains.Hips).Position;
        return new Vector2(away.X, away.Z).Length();
    }

    private static bool Near(MotionTransform a, MotionTransform b, float tolerance) =>
        Vector3.Distance(a.Position, b.Position) <= tolerance &&
        (Quaternion.Dot(a.Rotation, b.Rotation) >= 0 ? a.Rotation - b.Rotation : a.Rotation + b.Rotation).Length() <= tolerance;

    private static Quaternion Q(Vector3 axis, float degrees) => Quaternion.CreateFromAxisAngle(axis, degrees * MathF.PI / 180);

    private static MotionLayer Balance(MotionBalance balance) =>
        new("m1", MotionLayerKind.Balance, "Balance 1", 0, 60, 0, 0, 1, PoseComponents.Rotation | PoseComponents.Position, MotionInterpolation.Smooth,
            Balance: balance);

    private static BoneTransform T(Vector3 position, Quaternion? rotation = null) => new(position, rotation ?? Quaternion.Identity, Vector3.One);
}
