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
/// Look-at layers: the head turns toward keyed spots or a followed bone, the turn shared with the neck and upper back,
/// capped softly by the limit, keeping the head's roll; their validation, journals and edits.
/// </summary>
internal static class LookAtFixture
{
    // A spine, a neck and a head with the game's head rest (its +X along the character's forward, -Y up), a jaw on the head and a right arm.
    private const int Root = 0, Hara = 1, Kosi = 2, SpineA = 3, SpineB = 4, Chest = 5, Neck = 6, Head = 7, Jaw = 8, Clavicle = 9, Upper = 10, Lower = 11, Hand = 12;
    private static readonly Quaternion HeadRest = new(0.70710677f, 0, 0.70710677f, 0);

    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Validation(check, reject);
        Journals(check);
        Axes(check);
        Ahead(check);
        Side(check);
        Limit(check);
        Shares(check);
        Roll(check);
        Behind(check);
        Strength(check);
        Follow(check);
        Keyed(check);
        Edits(check);
    }

    private static void Validation(Action<bool, string> check, Action<Action, string> reject)
    {
        var look = Look(new MotionLook(), Spot(0, new Vector3(0, 1.5f, 1)));
        MotionEvaluator.Validate(look);
        void Refused(MotionLayer layer, string what) => reject(() => MotionEvaluator.Validate(layer), what);
        Refused(Look(new MotionLook(Limit: -1)), "a negative look limit is refused");
        Refused(Look(new MotionLook(Limit: 121)), "a look limit past 120 degrees is refused");
        Refused(Look(new MotionLook(Limit: float.NaN)), "a NaN look limit is refused");
        Refused(Look(new MotionLook(Follow: "")), "a look following a bone without a name is refused");
        Refused(look with { Look = null }, "a look-at layer without its look is refused");
        Refused(new MotionLayer("m1", MotionLayerKind.Offset, "Offset 1", 0, 60, 0, 0, 1, PoseComponents.All, MotionInterpolation.Linear,
            Look: new MotionLook()), "a look on an offset layer is refused");
        Refused(look with { Bones = ["j_kao", "j_te_r"] }, "a look-at layer turning a bone outside the head chain is refused");
        Refused(Look(new MotionLook(Follow: "j_te_r"), Spot(0, Vector3.UnitZ)), "a look-at layer that follows a bone and has keys is refused");
        Refused(look with { Keys = [new MotionKey(0, ImmutableDictionary<string, BoneTransform>.Empty.Add("j_kubi", T(Vector3.UnitZ)))] },
            "a look key for another bone than the head is refused");
        Refused(look with { Pins = [new MotionPin("j_kao")] }, "a look-at layer with pins is refused");
        Refused(look with { Window = 10 }, "a look-at layer with a window is refused");
        Refused(look with { Noise = new MotionNoise() }, "a look-at layer with a noise is refused");
        Refused(look with { Amount = 1.5f }, "a look's strength stops at 100%");
        check(MotionEvaluator.BlendsAtMostFully(MotionLayerKind.LookAt), "a look blends at most fully");
        MotionEvaluator.Validate(Look(new MotionLook(Follow: "j_te_r")));
        MotionEvaluator.Validate(look with { Bones = ["j_kubi"] });

        check(MotionEvaluator.Active(look), "a look with a keyed spot is active");
        check(MotionEvaluator.Active(Look(new MotionLook(Follow: "j_te_r"))), "a look following a bone is active without keys");
        check(!MotionEvaluator.Active(Look(new MotionLook())), "a look with no spot and no bone does nothing");
        check(!MotionEvaluator.Active(look with { Bones = [] }) && !MotionEvaluator.Active(look with { Amount = 0 }) &&
              !MotionEvaluator.Active(look with { Look = new MotionLook(Limit: 0) }),
            "a look with no bones, at 0% or with a 0 limit does nothing");

        var rig = Rig();
        MotionEvaluator.CheckBones([Look(new MotionLook(Follow: "j_te_r"))], rig);
        reject(() => MotionEvaluator.CheckBones([Look(new MotionLook(Follow: "j_ago"))], rig), "following a bone that hangs from the head is refused");
        reject(() => MotionEvaluator.CheckBones([Look(new MotionLook(Follow: "j_kubi"))], rig), "following a bone of the head chain is refused");
        reject(() => MotionEvaluator.CheckBones([Look(new MotionLook(Follow: "j_nope"))], rig), "following a bone the skeleton lacks is refused");
    }

    private static void Journals(Action<bool, string> check)
    {
        var temp = Path.Combine(Path.GetTempPath(), "ie-look-at-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var resource = new AnimationResource("chara/human/c0801/animation/a0001/bt_common/emote/dance01.pap", "dance01.pap", "hash");
            var clip = new AnimationClip(resource.GamePath, "dance", 0, 0, 42, "chara/test.sklb");
            var capture = new AnimationCapture("clip", 1, 1, Guid.NewGuid(), "Test", "Idle", clip, null, [resource.GamePath], [resource], DateTime.UtcNow, true);
            var settings = new MotionLook(Limit: 45);
            var layer = Look(settings, Spot(5, new Vector3(.3f, 1.4f, .8f)), Spot(40, new Vector3(-.3f, 1.6f, .8f))) with { Weights = [new MotionWeight(10, .5f)] };
            var followed = Look(new MotionLook("j_te_r", 100)) with { Key = "m2" };
            var request = new AnimationBakeRequest(Guid.NewGuid(), capture, AnimationDestination.NewMod, "Motion", false,
                AnimationOperation.EditTimeline, Timeline: new AnimationTimelineEdit(true, 0, null, [layer, followed]));
            new AnimationJournalStore(temp).Save(new AnimationEditJournal { Id = request.Id, Request = AnimationCommitService.RecoveryRequest(request) });
            var loaded = new AnimationJournalStore(temp).Load().Single().Request.Timeline!.Motion;
            check(loaded.Length == 2 && MotionEdits.Same(loaded[0], layer) && loaded[0].Look == settings && MotionEdits.Same(loaded[1], followed) &&
                  loaded[1].Look == followed.Look, "a journal keeps a look's spots, limit and followed bone");

            var path = Path.Combine(temp, "AnimationEdits", request.Id.ToString("N"), "journal.json");
            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            check(json["Request"]!["Timeline"]!["Motion"]![0]!.AsObject().Remove("Look"), "a journal stores a layer's look as Look");
            File.WriteAllText(path, json.ToJsonString());
            var older = new AnimationJournalStore(temp).Load().Single().Request.Timeline!.Motion[0];
            var refused = false;
            try { MotionEvaluator.Validate(older); }
            catch (InvalidDataException) { refused = true; }
            check(older.Look == null && refused && !MotionEvaluator.Active(older), "a look-at layer without Look loads with none and is refused");
        }
        finally { Directory.Delete(temp, true); }

        var a = Look(new MotionLook());
        check(MotionEdits.Same(a, a with { Look = new MotionLook() }) && !MotionEdits.Same(a, a with { Look = new MotionLook(Limit: 60) }) &&
              !MotionEdits.Same(a, a with { Look = new MotionLook("j_te_r") }) && !MotionEdits.Same(a, a with { Look = null }),
            "layers differ by their look's limit and followed bone");
    }

    private static void Axes(Action<bool, string> check)
    {
        var (forward, up) = MotionLooks.Axes(Rig(), Head);
        check(Vector3.Distance(forward, Vector3.UnitX) < 1e-5f && Vector3.Distance(up, -Vector3.UnitY) < 1e-5f,
            "the head's forward is measured from its rest: the game's head looks along its +X, -Y up");
        var identity = Rig() with { Bones = Rig().Bones.SetItem(Head, Rig().Bones[Head] with { Reference = T(new Vector3(0, .08f, 0)) }) };
        check(Vector3.Distance(MotionLooks.Axes(identity, Head).Forward, Vector3.UnitZ) < 1e-5f, "a head with no rest turn looks along +Z");
    }

    private static void Ahead(Action<bool, string> check)
    {
        var rest = Rest();
        var target = HeadAt(rest) + new Vector3(0, 0, 2);
        var looked = MotionEvaluator.ApplyAt(Rig(), rest, [Look(new MotionLook(), Spot(0, target))], 10);
        check(Enumerable.Range(0, rest.Length).All(b => looked[b] == rest[b]), "a spot straight ahead changes nothing");
        var clip = Applied(Still(), Look(new MotionLook(), Spot(0, target)));
        check(Enumerable.Range(0, clip.Samples).All(i => Enumerable.Range(0, clip.Bones).All(b => clip.Get(i, b) == Still().Get(i, b))),
            "a spot straight ahead leaves the clip as it was");
    }

    private static void Side(Action<bool, string> check)
    {
        var rest = Rest();
        foreach (var (way, label) in new[] { (new Vector3(1, 0, 1), "left"), (new Vector3(-1, .4f, 1.5f), "up and right"), (new Vector3(.3f, -.6f, 1), "down") })
        {
            var target = HeadAt(rest) + way;
            var looked = MotionEvaluator.ApplyAt(Rig(), rest, [Look(new MotionLook(), Spot(0, target))], 10);
            check(Off(looked, target) < .1f, $"the head ends looking at a spot {label} ({Off(looked, target):0.###} degrees off)");
            check(new[] { Root, Hara, Kosi, SpineA, SpineB, Clavicle, Upper, Lower, Hand, Jaw }.All(b => looked[b] == rest[b]),
                $"looking {label} turns only the head, neck and upper back");
        }
    }

    private static void Limit(Action<bool, string> check)
    {
        var rest = Rest();
        var target = HeadAt(rest) + new Vector3(2, 0, 0);
        var capped = MotionEvaluator.ApplyAt(Rig(), rest, [Look(new MotionLook(Limit: 30), Spot(0, target))], 10);
        var turned = Turned(rest, capped);
        check(turned <= 30.01f && turned > 25, $"the limit caps the turn ({turned:0.##} of at most 30 degrees)");

        var last = 0f;
        var steady = true;
        var within = true;
        for (var degrees = 5; degrees <= 175; degrees += 5)
        {
            var way = new Vector3(MathF.Sin(degrees * MathF.PI / 180), 0, MathF.Cos(degrees * MathF.PI / 180)) * 3;
            var pose = MotionEvaluator.ApplyAt(Rig(), rest, [Look(new MotionLook(), Spot(0, HeadAt(rest) + way))], 10);
            var turn = Turned(rest, pose);
            steady &= turn >= last - .05f;
            within &= turn <= 80.01f && (degrees > 55 || Off(pose, HeadAt(rest) + way) < .15f);
            last = turn;
        }
        check(steady && within && last > 75, $"the turn rises with the spot's angle and eases into the limit, never past it ({last:0.##} at 175 degrees)");
    }

    private static void Shares(Action<bool, string> check)
    {
        var rest = Rest();
        var target = HeadAt(rest) + Way(40) * 20;
        var looked = MotionEvaluator.ApplyAt(Rig(), rest, [Look(new MotionLook(), Spot(0, target))], 10);
        var (back, neck, head) = (Degrees(looked[Chest].Rotation, rest[Chest].Rotation), Degrees(looked[Neck].Rotation, rest[Neck].Rotation),
            Degrees(looked[Head].Rotation, rest[Head].Rotation));
        check(MathF.Abs(back - 6) < .3f && MathF.Abs(neck - 10) < .3f && MathF.Abs(head - 24) < .3f && MathF.Abs(back + neck + head - 40) < .3f,
            $"the turn is shared 15/25/60 from the upper back up and adds up ({back:0.##}, {neck:0.##}, {head:0.##})");
        var two = MotionEvaluator.ApplyAt(Rig(), rest, [Look(new MotionLook(), Spot(0, target)) with { Bones = ["j_kubi", "j_kao"] }], 10);
        var (twoNeck, twoHead) = (Degrees(two[Neck].Rotation, rest[Neck].Rotation), Degrees(two[Head].Rotation, rest[Head].Rotation));
        check(two[Chest] == rest[Chest] && MathF.Abs(twoNeck - 40 * 25 / 85f) < .3f && MathF.Abs(twoHead - 40 * 60 / 85f) < .3f,
            $"the shares are spread over the bones picked ({twoNeck:0.##}, {twoHead:0.##})");
    }

    private static void Roll(Action<bool, string> check)
    {
        var rest = Rest();
        // The motion rolls the head about its own forward.
        rest[Head] = rest[Head] with { Rotation = Quaternion.Normalize(rest[Head].Rotation * Q(Vector3.UnitX, 20)) };
        var target = HeadAt(rest) + Way(40) * 20;
        var looked = MotionEvaluator.ApplyAt(Rig(), rest, [Look(new MotionLook(), Spot(0, target))], 10);
        var (before, after) = (MotionEvaluator.ModelSpace(Rig(), rest)[Head].Rotation, MotionEvaluator.ModelSpace(Rig(), looked)[Head].Rotation);
        var smallest = PinSolver.Between(Vector3.Transform(Vector3.UnitX, before), Vector3.Transform(Vector3.UnitX, after)) * before;
        check(Off(looked, target) < .1f && Degrees(after, smallest) < .3f, $"the head keeps its roll ({Degrees(after, smallest):0.###} degrees off)");
    }

    private static void Behind(Action<bool, string> check)
    {
        foreach (var side in new[] { 1, -1 })
        {
            // The motion already turns the upper back a little to one side.
            var rest = Rest();
            rest[Chest] = rest[Chest] with { Rotation = Q(Vector3.UnitY, 10 * side) };
            var start = Facing(rest);
            var target = HeadAt(rest) - start * 3;
            var looked = MotionEvaluator.ApplyAt(Rig(), rest, [Look(new MotionLook(), Spot(0, target))], 10);
            var facing = Facing(looked);
            var turned = Turned(rest, looked);
            check(turned > 75 && turned <= 80.01f && MathF.Abs(facing.Y) < .01f && MathF.Sign(facing.X) == side && MathF.Abs(facing.X) > .9f,
                $"a spot straight behind turns level to the limit, on the side the motion faces ({turned:0.##} degrees, {(side > 0 ? "left" : "right")})");
        }
        var straight = Rest();
        var turnedBack = MotionEvaluator.ApplyAt(Rig(), straight, [Look(new MotionLook(), Spot(0, HeadAt(straight) - new Vector3(0, 0, 3)))], 10);
        check(MathF.Abs(Facing(turnedBack).Y) < .01f && Turned(straight, turnedBack) > 75, "a spot straight behind a head facing ahead still turns level, never over the top");
    }

    private static void Strength(Action<bool, string> check)
    {
        var rest = Rest();
        var target = HeadAt(rest) + Way(40) * 20;
        var half = MotionEvaluator.ApplyAt(Rig(), rest, [Look(new MotionLook(), Spot(0, target)) with { Amount = .5f }], 10);
        var faded = MotionEvaluator.ApplyAt(Rig(), rest, [Look(new MotionLook(), Spot(0, target)) with { Weights = [new MotionWeight(0, .25f), new MotionWeight(60, .25f)] }], 10);
        check(MathF.Abs(Turned(rest, half) - 20) < .3f && MathF.Abs(Turned(rest, faded) - 10) < .3f,
            $"a look at half strength turns half way, and its weight scales it too ({Turned(rest, half):0.##}, {Turned(rest, faded):0.##})");
    }

    private static void Follow(Action<bool, string> check)
    {
        // The arm swings forward over the clip, the hand passing in front of the chest.
        var clip = Clip(61, 60, f => Rig().Bones.Select((bone, b) => b == Upper ? T(bone.Reference.Position, Q(Vector3.UnitY, 50 + f / 2)) : bone.Reference).ToArray());
        var layer = Look(new MotionLook("j_te_r"));
        var looked = Applied(clip, layer);
        foreach (var sample in new[] { 0, 20, 40, 60 })
        {
            var pose = Pose(looked, sample);
            var hand = MotionEvaluator.ModelSpace(Rig(), pose)[Hand].Position;
            check(Off(pose, hand) < .2f, $"the head follows the hand at frame {sample} ({Off(pose, hand):0.###} degrees off)");
        }
        var at = MotionEvaluator.ApplyAt(Rig(), Pose(clip, 20), [layer], 20, 0, 60);
        check(Enumerable.Range(0, clip.Bones).All(b => Near(at[b], looked.Get(20, b))), "following a bone at a frame is the follow over the clip there");
    }

    private static void Keyed(Action<bool, string> check)
    {
        var head = HeadAt(Rest());
        var layer = Look(new MotionLook(), Spot(0, head + new Vector3(0, 0, 2)), Spot(60, head + new Vector3(2, 0, 2)));
        var looked = Applied(Still(), layer);
        var middle = head + new Vector3(1, 0, 2);
        check(Off(Pose(looked, 30), middle) < .1f, $"keyed spots go from one to the next ({Off(Pose(looked, 30), middle):0.###} degrees off)");
        check(Turned(Rest(), Pose(looked, 0)) < 1e-3f && Off(Pose(looked, 60), head + new Vector3(2, 0, 2)) < .1f, "each key's spot holds at its frame");
        var at = MotionEvaluator.ApplyAt(Rig(), Rest(), [layer], 30, 0, 60);
        check(Enumerable.Range(0, Rest().Length).All(b => Near(at[b], looked.Get(30, b))), "a look at a frame is the look over the clip there");
        var animated = MotionSymmetry.Animated(Rig(), [0], [layer], 1);
        check(animated != null && animated.Contains(Head) && animated.Contains(Neck) && animated.Contains(Chest) && !animated.Contains(Hand),
            "a symmetry after a look counts the head chain as moving");
    }

    private static void Edits(Action<bool, string> check)
    {
        var doc = MotionEdits.Add(new TimelineDocument(100, 100, null, [], 1), MotionLayerKind.LookAt, 0, 100, out var key, out _);
        var layer = MotionEdits.Find(doc, key)!;
        check(layer is { Name: "Look at 1", Amount: 1, Components: PoseComponents.Rotation, Window: 0, Swing: null, Noise: null } &&
              layer.Look == new MotionLook() && layer.Bones.SequenceEqual(["j_kao", "j_kubi", "j_sebo_c"]) && MotionEdits.MaxAmount(MotionLayerKind.LookAt) == 1,
            "a look-at layer starts on the head, neck and upper back with an 80 degree limit, at full strength");
        check(TimelineWindowViews.LayerKinds.SequenceEqual([MotionLayerKind.Offset, MotionLayerKind.Amplitude, MotionLayerKind.Pose, MotionLayerKind.Symmetry,
                  MotionLayerKind.Idle, MotionLayerKind.Swing, MotionLayerKind.Smooth, MotionLayerKind.Noise, MotionLayerKind.Spring, MotionLayerKind.Repeat,
                  MotionLayerKind.LookAt, MotionLayerKind.Link, MotionLayerKind.Pin]) &&
              MotionEdits.KindName(MotionLayerKind.LookAt) == "Look at" && TimelineWindowViews.LayerKindHint(MotionLayerKind.LookAt) == "Turn the head toward a spot." &&
              Enum.GetValues<MotionLayerKind>().Where(kind => kind != MotionLayerKind.LookAt).All(kind =>
                  TimelineWindowViews.LayerColour(kind) != TimelineWindowViews.LayerColour(MotionLayerKind.LookAt) && TimelineUi.LayerIcon(kind) != TimelineUi.LayerIcon(MotionLayerKind.LookAt)),
            "the Add menu offers Look at before Pin, with its own hint, icon and colour");
        check(!TimelineFace.Allows(MotionLayerKind.LookAt, null), "a face takes no look-at layer");

        var spot = new BoneTransform(new Vector3(.2f, 1.5f, 1), Quaternion.Identity, Vector3.One);
        var keyed = MotionEdits.SetLookTarget(doc, key, 10, spot.Position);
        check(MotionEdits.Find(keyed, key)!.Keys is [{ Frame: 10 } k] && k.Bones.Count == 1 && k.Bones["j_kao"] == spot,
            "moving the target keys its spot on the head at the playhead");
        check(ReferenceEquals(MotionEdits.SetKey(doc, key, 10, "j_kubi", spot), doc), "a look-at layer keys only the head's spot");
        var follows = MotionEdits.Replace(keyed, MotionEdits.Find(keyed, key)! with { Look = new MotionLook("j_te_r") });
        check(MotionEdits.Find(follows, key) is { Keys.IsEmpty: true, Look.Follow: "j_te_r" } && ReferenceEquals(MotionEdits.SetLookTarget(follows, key, 20, spot.Position), follows),
            "following a bone drops the keyed spots in the same edit, and takes no new ones");
        var limited = MotionEdits.SetBones(doc, key, ["j_kao", "j_te_r", "j_kubi"]);
        check(MotionEdits.Find(limited, key)!.Bones.SequenceEqual(["j_kao", "j_kubi"]), "a look-at layer takes only the head chain's bones");
        var clamped = MotionEdits.Replace(doc, layer with { Look = new MotionLook(Limit: 500) });
        check(MotionEdits.Find(clamped, key)!.Look!.Limit == MotionLooks.MaxLimit, "a look's limit is kept in range");

        var looking = MotionEdits.Find(keyed, key)!;
        check(TimelineWindowViews.LayerContent(looking) == "1 key · 3 bones" &&
              TimelineWindowViews.LayerContent(looking with { Keys = [], Look = new MotionLook("j_te_r") }) == $"Follows {AnimationPresentation.BoneName("j_te_r")} · 3 bones" &&
              TimelineWindowViews.LayerContent(looking with { Bones = [] }) == "1 key · no bones",
            "a look's tooltip says what it looks at and how many bones turn");
        check(PoseEdits.Changes(looking, "j_kubi") && !PoseEdits.Changes(looking, "j_te_r") &&
              BonePosing.StateOf("j_kao", looking, 10, null) == BoneState.Changed && MotionPaths.KeyFrames(looking, "j_kao", 0).Count == 0,
            "the head chain shows as changed by a look, never as keyed: its keys are spots");
    }

    private static SkeletonDescription Rig() => new("look-rig", "look-rig-fingerprint",
    [
        new SkeletonBone("n_root", -1, 0, T(Vector3.Zero)),
        new SkeletonBone("n_hara", Root, 0, T(new Vector3(0, 1, 0))),
        new SkeletonBone("j_kosi", Hara, 0, T(Vector3.Zero)),
        new SkeletonBone("j_sebo_a", Kosi, 0, T(new Vector3(0, .1f, 0))),
        new SkeletonBone("j_sebo_b", SpineA, 0, T(new Vector3(0, .12f, 0))),
        new SkeletonBone("j_sebo_c", SpineB, 0, T(new Vector3(0, .14f, 0))),
        new SkeletonBone("j_kubi", Chest, 0, T(new Vector3(0, .12f, 0))),
        new SkeletonBone("j_kao", Neck, 0, T(new Vector3(0, .08f, 0), HeadRest)),
        new SkeletonBone("j_ago", Head, 0, T(new Vector3(.06f, .02f, 0))),
        new SkeletonBone("j_sako_r", Chest, 0, T(new Vector3(-.03f, .1f, .02f))),
        new SkeletonBone("j_ude_a_r", Clavicle, 0, T(new Vector3(-.15f, 0, 0))),
        new SkeletonBone("j_ude_b_r", Upper, 0, T(new Vector3(-.25f, 0, 0))),
        new SkeletonBone("j_te_r", Lower, 0, T(new Vector3(-.25f, 0, 0))),
    ], [], [], []);

    private static MotionTransform[] Rest() => [.. Rig().Bones.Select(bone => MotionTransform.From(bone.Reference))];

    private static MotionClip Still() => Clip(61, 60, _ => [.. Rig().Bones.Select(bone => bone.Reference)]);

    private static MotionClip Clip(int samples, float frames, Func<float, BoneTransform[]> pose)
    {
        var poses = Enumerable.Range(0, samples).Select(i => pose(frames * i / (samples - 1))).ToArray();
        var made = MotionClip.FromPoses(Rig(), frames / TimelineDocument.Fps, poses);
        var clip = new MotionClip(Rig(), made.Duration, made.Samples);
        clip.CopyFrom(made);
        return clip;
    }

    private static MotionClip Applied(MotionClip source, params MotionLayer[] layers)
    {
        var clip = source.Clone();
        MotionEvaluator.Apply(clip, layers);
        return clip;
    }

    private static MotionTransform[] Pose(MotionClip clip, int sample) => [.. Enumerable.Range(0, clip.Bones).Select(b => clip.Get(sample, b))];

    private static Vector3 HeadAt(MotionTransform[] local) => MotionEvaluator.ModelSpace(Rig(), local)[Head].Position;

    /// <summary>Where the head looks in character space: its +X, as the game's head rest has it.</summary>
    private static Vector3 Facing(MotionTransform[] local) => Vector3.Normalize(Vector3.Transform(Vector3.UnitX, MotionEvaluator.ModelSpace(Rig(), local)[Head].Rotation));

    /// <summary>How far, in degrees, the head looks off <paramref name="target"/>.</summary>
    private static float Off(MotionTransform[] local, Vector3 target) => Angle(Facing(local), target - HeadAt(local));

    /// <summary>How far, in degrees, the head's forward turned from one pose to the other.</summary>
    private static float Turned(MotionTransform[] from, MotionTransform[] to) => Angle(Facing(from), Facing(to));

    /// <summary>A way in the floor plane <paramref name="degrees"/> toward +X from the character's forward.</summary>
    private static Vector3 Way(float degrees) => new(MathF.Sin(degrees * MathF.PI / 180), 0, MathF.Cos(degrees * MathF.PI / 180));

    private static float Angle(Vector3 a, Vector3 b) =>
        MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b)), -1, 1)) * 180 / MathF.PI;

    private static bool Near(MotionTransform a, MotionTransform b) =>
        Vector3.Distance(a.Position, b.Position) <= 1e-5f && (Quaternion.Dot(a.Rotation, b.Rotation) >= 0 ? a.Rotation - b.Rotation : a.Rotation + b.Rotation).Length() <= 1e-5f;

    private static Quaternion Q(Vector3 axis, float degrees) => Quaternion.CreateFromAxisAngle(axis, degrees * MathF.PI / 180);

    private static float Degrees(Quaternion a, Quaternion b) =>
        2 * MathF.Acos(Math.Clamp(Math.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))), 0, 1)) * 180 / MathF.PI;

    private static MotionLayer Look(MotionLook look, params MotionKey[] keys) =>
        new("m1", MotionLayerKind.LookAt, "Look at 1", 0, 60, 0, 0, 1, PoseComponents.Rotation, MotionInterpolation.Linear,
            Bones: [.. MotionLooks.Chain], Keys: [.. keys], Look: look);

    private static MotionKey Spot(int frame, Vector3 at) => new(frame, ImmutableDictionary<string, BoneTransform>.Empty.Add("j_kao", T(at)));

    private static BoneTransform T(Vector3 position, Quaternion? rotation = null) => new(position, rotation ?? Quaternion.Identity, Vector3.One);
}
