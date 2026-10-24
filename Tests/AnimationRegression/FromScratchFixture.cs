using System.Collections.Immutable;
using System.Numerics;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui;
using InstantEdit.Ui.Timeline;

/// <summary>
/// Animations from scratch: a still pose held as the clip's motion, feet stood back on the ground, and
/// idle motion borrowed from another clip (breathing, the weight shift) played over the pose.
/// </summary>
internal static class FromScratchFixture
{
    private const string Body = "n_hara", Hips = "j_kosi", Back = "j_sebo_b", Thigh = "j_asi_a_l", Knee = "j_asi_b_l", Foot = "j_asi_d_l",
        Skirt = "j_sk_f_a_l", Tail = "n_sippo_a";
    private const float Tolerance = .02f;

    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Still(check, reject);
        Ground(check);
        Borrow(check, reject);
        Play(check);
        Document(check, reject);
    }

    private static void Still(Action<bool, string> check, Action<Action, string> reject)
    {
        // A one-second clip where the back turns 30 degrees, with one float track counting up.
        var floats = Enumerable.Range(0, 31).Select(i => (float)i).ToArray();
        var source = Clip(31, 30, (_, f) => Pose(back: Q(Vector3.UnitX, f)), floats, 1);
        var still = MotionHold.Still(source, 12, 90);
        var back = Index(Back);
        check(still.Samples == 91 && Math.Abs(still.Duration - 3) < 1e-6f && still.FloatTracks == 1 &&
              Enumerable.Range(0, still.Samples).All(i => Degrees(still.Get(i, back).Rotation, Q(Vector3.UnitX, 12)) < Tolerance &&
                                                          still.TrackFloats![i] == 12),
            "a still pose holds the frame it was taken from, float tracks too, a sample a frame");
        reject(() => MotionHold.Still(source, 0, 0), "a still pose needs a frame");
    }

    private static void Ground(Action<bool, string> check)
    {
        var rig = Rig();
        var standing = Locals(Pose());
        var bent = Locals(Pose(thigh: Q(Vector3.UnitX, 60)));
        var lifted = MotionHold.Lowest(rig, bent)!.Value - MotionHold.Lowest(rig, standing)!.Value;
        var place = MotionHold.OnGround(rig, bent, standing);
        bent[Index(Body)] = bent[Index(Body)] with { Position = place!.Value };
        check(Math.Abs(lifted - .45f) < 1e-4f && Math.Abs(MotionHold.Lowest(rig, bent)!.Value - MotionHold.Lowest(rig, standing)!.Value) < 1e-5f,
            "a bent leg lifts the foot, and the body comes down until it stands as low as before");

        // A body turned on its side: the drop is still straight down in character space.
        var parent = Locals(Pose());
        parent[0] = parent[0] with { Rotation = Q(Vector3.UnitZ, 90) };
        var turned = (MotionTransform[])parent.Clone();
        turned[Index(Thigh)] = turned[Index(Thigh)] with { Rotation = Q(Vector3.UnitX, 60) };
        turned[Index(Body)] = turned[Index(Body)] with { Position = MotionHold.OnGround(rig, turned, parent)!.Value };
        check(Math.Abs(MotionHold.Lowest(rig, turned)!.Value - MotionHold.Lowest(rig, parent)!.Value) < 1e-5f,
            "under a turned root the body still drops in character space");

        var footless = new SkeletonDescription("footless", "footless", [new("n_root", -1, 0, T(Vector3.Zero)), new(Body, 0, 0, T(Vector3.UnitY))], [], [], []);
        check(MotionHold.OnGround(footless, [MotionTransform.Identity, MotionTransform.Identity], [MotionTransform.Identity, MotionTransform.Identity]) == null,
            "a skeleton without feet can't be grounded");
    }

    /// <summary>
    /// The donor: a 60-frame loop whose back breathes 4 degrees and back, whose body slides 1 cm and back, whose tail swings 20 degrees,
    /// and whose skirt swings, which physics owns, so it isn't borrowed.
    /// </summary>
    private static MotionClip Donor() => Clip(61, 60, (_, f) =>
    {
        var wave = MathF.Sin(f / 60 * 2 * MathF.PI);
        return Pose(back: Q(Vector3.UnitX, 4 * wave), slide: new Vector3(.01f * wave, 0, 0), tail: Q(Vector3.UnitY, 20 * wave), skirt: Q(Vector3.UnitX, 10 * wave));
    }, loops: true);

    private static void Borrow(Action<bool, string> check, Action<Action, string> reject)
    {
        var idle = MotionIdles.Borrow(Donor(), "Idle");
        check(idle.Turns.ContainsKey(Back) && idle.Turns.ContainsKey(Tail) && !idle.Turns.ContainsKey(Skirt) && !idle.Turns.ContainsKey(Thigh) &&
              idle.Slide.Length == idle.Samples * 3 && idle.Samples == 61 && idle.Donor == "Idle",
            "borrowing keeps the bones that move in a group and the body's slide, and leaves physics bones and still bones out");
        check(Math.Abs(idle.Spine - .55f) < 1e-6f && idle.Arms == 0 && idle.Balance == 1 && idle.Tail == 1,
            "a borrowed motion starts at Pose2Idle's Natural shares");
        var still = Clip(2, 60, (_, _) => Pose());
        reject(() => MotionIdles.Borrow(still, "Still"), "a clip that moves nothing has no motion to borrow");

        check(MotionIdles.Group(Body) == IdleGroup.Balance && MotionIdles.Group(Hips) == IdleGroup.Balance && MotionIdles.Group(Knee) == IdleGroup.Balance &&
              MotionIdles.Group("j_kubi") == IdleGroup.Spine && MotionIdles.Group("j_te_r") == IdleGroup.Arms && MotionIdles.Group("j_oya_a_l") == IdleGroup.Arms &&
              MotionIdles.Group(Tail) == IdleGroup.Tail && MotionIdles.Group(Skirt) == IdleGroup.None && MotionIdles.Group("j_kami_a") == IdleGroup.None,
            "the body's slide goes with the hips and legs, as one weight shift");

        var layer = Layer(idle);
        MotionEvaluator.Validate(layer);
        reject(() => MotionEvaluator.Validate(layer with { Idle = idle with { Turns = idle.Turns.SetItem(Back, [1, 0, 0]) } }), "a borrowed turn of the wrong length can't be read");
        reject(() => MotionEvaluator.Validate(layer with { Idle = idle with { Tail = 6 } }), "a share past its most is refused");
        reject(() => MotionEvaluator.Validate(layer with { Kind = MotionLayerKind.Offset }), "only an idle-motion layer holds idle motion");
        reject(() => MotionEvaluator.Validate(layer with { Keys = [new MotionKey(0, ImmutableDictionary<string, BoneTransform>.Empty)] }), "an idle-motion layer has no keys");
        MotionEvaluator.Validate(layer with { Idle = null });
        check(!MotionEvaluator.Active(layer with { Idle = null }) && !MotionEvaluator.Active(layer with { Idle = idle with { Spine = 0, Balance = 0, Tail = 0 } }) &&
              MotionEvaluator.Active(layer),
            "an idle-motion layer does nothing until its motion is read, or with every share at 0");
    }

    private static void Play(Action<bool, string> check)
    {
        var idle = MotionIdles.Borrow(Donor(), "Idle") with { Spine = 1 };
        // A 120-frame still pose whose back already leans 30 degrees: the borrowed breath plays twice, at its own pace.
        var lean = Q(Vector3.UnitX, 30);
        var still = Clip(121, 120, (_, _) => Pose(back: lean));
        var played = Applied(still, Layer(idle, 120));
        var back = Index(Back);
        float Breath(int sample) => Degrees(played.Get(sample, back).Rotation, lean);
        check(Math.Abs(Breath(15) - 4) < Tolerance && Math.Abs(Breath(75) - 4) < Tolerance && Breath(0) < Tolerance && Breath(60) < Tolerance && Breath(120) < Tolerance,
            "the breath plays a whole number of times over the layer, so it starts over seamlessly");
        check(Degrees(played.Get(15, back).Rotation, Q(Vector3.UnitX, 34)) < Tolerance,
            "the borrowed turn adds onto the pose in the parent's space");

        var half = Applied(still, Layer(idle with { Spine = .5f }, 120));
        check(Math.Abs(Degrees(half.Get(15, back).Rotation, lean) - 2) < Tolerance, "a share of 50% plays half the breath");
        var more = Applied(still, Layer(idle with { Tail = 2 }, 120));
        check(Math.Abs(Degrees(more.Get(15, Index(Tail)).Rotation, Quaternion.Identity) - 40) < .1f, "a tail share past 100% swings it further");
        check(Math.Abs(played.Get(15, Index(Body)).Position.X - .01f) < 1e-5f && Math.Abs(played.Get(15, Index(Body)).Position.Y - 1) < 1e-5f,
            "the body slides as the donor's did");
        check(played.Get(15, Index(Skirt)) == still.Get(15, Index(Skirt)) && played.Get(15, back).Scale == Vector3.One,
            "bones it doesn't borrow, and every scale, are left alone");

        // Over 100 frames the 60-frame breath plays twice, a little faster.
        var shorter = Applied(Clip(101, 100, (_, _) => Pose(back: lean)), Layer(idle, 100));
        check(Math.Abs(Degrees(shorter.Get(13, back).Rotation, lean) - 4 * MathF.Sin(13 / 50f * 2 * MathF.PI)) < .05f,
            "the motion stretches a little so a whole number of times fits");

        var at = MotionEvaluator.ApplyAt(still.Skeleton, still.Pose(15), [Layer(idle, 120)], 15);
        check(Degrees(at[back].Rotation, played.Get(15, back).Rotation) < Tolerance && Vector3.Distance(at[Index(Body)].Position, played.Get(15, Index(Body)).Position) < 1e-5f,
            "the Bones window and the gizmo see the idle motion as the save bakes it");

        var limited = Applied(still, Layer(idle, 120) with { Bones = [Tail] });
        check(Degrees(limited.Get(15, back).Rotation, lean) < Tolerance && Degrees(limited.Get(15, Index(Tail)).Rotation, Quaternion.Identity) > 19,
            "a bone limit keeps the motion to its bones");
        check(MotionIdles.Moved(Layer(idle with { Balance = 0 })).OrderBy(name => name, StringComparer.Ordinal).SequenceEqual([Back, Tail]),
            "the bones it moves are the borrowed ones with a share");
    }

    private static void Document(Action<bool, string> check, Action<Action, string> reject)
    {
        var doc = CutEditsFixture.Doc(100);
        var editor = new TimelineEditor(doc);
        var still = new MotionReplacement([1, 2, 3], "still-print", "still-hash", 100, AnimationEditService.StillPoseFrame(12), "", null);
        check(editor.HoldStill(still) && editor.Document.Replacement?.Still == true && editor.Document.Motion is [{ Kind: MotionLayerKind.Pose, Start: 0, End: 100 }] &&
              editor.SelectedLayer == editor.Document.Motion[0].Key,
            "starting from a still pose swaps the motion in and selects a pose layer over the whole clip");
        editor.Undo();
        check(ReferenceEquals(editor.Document, doc), "starting from a still pose is one undo step");
        check(TimelineWindowViews.BlenderMotionLine(still) == "Still pose: frame 12 held for 100 frames.", "the line under the toolbar says which frame is held");

        var face = new TimelineEditor(new TimelineDocument(150, 150, null, [], 1) { Expression = true });
        check(!face.HoldStill(still) && face.Document.Replacement == null, "an expression's timeline has no motion to hold");

        var idle = MotionIdles.Borrow(Donor(), "Idle");
        var layer = Layer(idle);
        check(!MotionEdits.Same(layer, layer with { Idle = idle with { Spine = .6f } }) && MotionEdits.Same(layer, layer with { Idle = idle with { } }),
            "a share's change counts as a change");
        var added = MotionEdits.Add(new TimelineDocument(60, 60, null, [], 1), MotionLayerKind.Idle, 0, 60, out var key, out _);
        check(MotionEdits.SetKey(added, key, 10, Back, T(Vector3.Zero)) == added, "an idle-motion layer takes no keys");
        Journal(check, layer with { Idle = idle with { Spine = .7f } });
    }

    /// <summary>Edit again reads layers back from the journal: the borrowed motion and its shares must come back as saved.</summary>
    private static void Journal(Action<bool, string> check, MotionLayer layer)
    {
        var temp = Path.Combine(Path.GetTempPath(), "ie-from-scratch-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var resource = new AnimationResource("chara/human/c0801/animation/a0001/bt_common/emote/stretch.pap", "stretch.pap", "hash");
            var clip = new AnimationClip(resource.GamePath, "stretch", 0, 0, 42, "chara/test.sklb");
            var capture = new AnimationCapture("clip", 1, 1, Guid.NewGuid(), "Test", "Stretch", clip, null, [resource.GamePath], [resource],
                new PoseSnapshot(0, 0, 0, false, [], DateTime.UtcNow, ""), DateTime.UtcNow, true);
            var plain = layer with { Key = "m2", Idle = null };
            var request = new AnimationBakeRequest(Guid.NewGuid(), capture, AnimationDestination.NewMod, "Motion", false,
                ImmutableHashSet<PoseBoneId>.Empty, PoseComponents.None, AnimationOperation.EditTimeline,
                Timeline: new AnimationTimelineEdit(false, 0, null, [layer, plain]));
            new AnimationJournalStore(temp).Save(new AnimationEditJournal { Id = request.Id, Request = AnimationCommitService.RecoveryRequest(request) });
            var loaded = new AnimationJournalStore(temp).Load().Single().Request.Timeline!.Motion;
            MotionEvaluator.Validate(loaded);
            check(loaded.Length == 2 && MotionEdits.Same(loaded[0], layer) && loaded[0].Idle!.Spine == .7f && loaded[1].Idle == null,
                "a journal keeps an idle-motion layer's borrowed motion and shares, and a layer without one loads with none");
        }
        finally { Directory.Delete(temp, true); }
    }

    // n_root, the body above the hips, the back, a leg down to the foot, a skirt bone and a tail.
    private static SkeletonDescription Rig() => new("scratch-rig", "scratch-rig-fingerprint",
    [
        new("n_root", -1, 0, T(Vector3.Zero)),
        new(Body, 0, 0, T(Vector3.UnitY)),
        new(Hips, 1, 0, T(Vector3.Zero)),
        new(Back, 2, 0, T(new Vector3(0, .2f, 0))),
        new(Thigh, 2, 0, T(new Vector3(.1f, 0, 0))),
        new(Knee, 4, 0, T(new Vector3(0, -.45f, 0))),
        new(Foot, 5, 0, T(new Vector3(0, -.45f, 0))),
        new(Skirt, 2, 0, T(new Vector3(0, -.1f, .1f))),
        new(Tail, 2, 0, T(new Vector3(0, 0, -.1f))),
    ], [], [], []);

    private static int Index(string bone) => MotionEvaluator.Index(Rig())[bone];

    private static BoneTransform[] Pose(Quaternion? back = null, Quaternion? thigh = null, Vector3 slide = default, Quaternion? tail = null, Quaternion? skirt = null)
    {
        var pose = Rig().Bones.Select(bone => bone.Reference).ToArray();
        pose[Index(Body)] = T(Vector3.UnitY + slide);
        pose[Index(Back)] = pose[Index(Back)] with { Rotation = back ?? Quaternion.Identity };
        pose[Index(Thigh)] = pose[Index(Thigh)] with { Rotation = thigh ?? Quaternion.Identity };
        pose[Index(Tail)] = pose[Index(Tail)] with { Rotation = tail ?? Quaternion.Identity };
        pose[Index(Skirt)] = pose[Index(Skirt)] with { Rotation = skirt ?? Quaternion.Identity };
        return pose;
    }

    private static MotionTransform[] Locals(BoneTransform[] pose) => [.. pose.Select(MotionTransform.From)];

    private static MotionLayer Layer(MotionIdle idle, int end = 60) =>
        new("m1", MotionLayerKind.Idle, "Idle motion 1", 0, end, 0, 0, 1, PoseComponents.Rotation | PoseComponents.Position, MotionInterpolation.Smooth, Idle: idle);

    private static MotionClip Clip(int samples, float frames, Func<int, float, BoneTransform[]> pose, float[]? floats = null, int floatTracks = 0, bool loops = false)
    {
        var poses = Enumerable.Range(0, samples).Select(i => pose(i, frames * i / (samples - 1))).ToArray();
        var made = MotionClip.FromPoses(Rig(), frames / TimelineDocument.Fps, poses);
        var clip = new MotionClip(Rig(), made.Duration, made.Samples, floats, floatTracks) { Loops = loops };
        clip.CopyFrom(made);
        return clip;
    }

    private static MotionClip Applied(MotionClip source, params MotionLayer[] layers)
    {
        var clip = source.Clone();
        MotionEvaluator.Apply(clip, layers);
        return clip;
    }

    private static Quaternion Q(Vector3 axis, float degrees) => Quaternion.CreateFromAxisAngle(axis, degrees * MathF.PI / 180);

    private static float Degrees(Quaternion a, Quaternion b) =>
        2 * MathF.Acos(Math.Clamp(Math.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))), 0, 1)) * 180 / MathF.PI;

    private static BoneTransform T(Vector3 position, Quaternion? rotation = null) => new(position, rotation ?? Quaternion.Identity, Vector3.One);
}
