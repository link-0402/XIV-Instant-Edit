using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json.Nodes;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;

/// <summary>
/// Bone layers: their fades, keys and the three kinds, the Havok-style space conversions, validation,
/// and that documents and journals carry them. The bake itself needs the game's Havok and is checked
/// in game; everything it applies comes from <see cref="MotionEvaluator"/>, which is checked here.
/// </summary>
internal static class MotionLayerFixture
{
    private const string Root = "n_root", Arm = "j_ude_a_l", Hand = "j_te_l", Tail = "n_sippo_a";

    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Fades(check);
        Offsets(check);
        Keys(check);
        Amplitudes(check);
        Poses(check);
        Order(check);
        Spaces(check);
        Validation(check, reject);
        Documents(check);
        Journals(check);
    }

    private static Quaternion Q(Vector3 axis, float degrees) => Quaternion.CreateFromAxisAngle(axis, degrees * MathF.PI / 180);

    private static float Degrees(Quaternion a, Quaternion b) =>
        2 * MathF.Acos(Math.Clamp(Math.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))), 0, 1)) * 180 / MathF.PI;

    /// <summary>The signed angle of a rotation about Z, in degrees.</summary>
    private static float AboutZ(Quaternion q) => 2 * MathF.Atan2(q.Z, q.W) * 180 / MathF.PI;

    private static BoneTransform T(Vector3 position, Quaternion? rotation = null) => new(position, rotation ?? Quaternion.Identity, Vector3.One);

    private static SkeletonDescription Rig() => new("rig", "rig-fingerprint",
        [new(Root, -1, 0, T(Vector3.Zero)), new(Arm, 0, 0, T(Vector3.UnitX)), new(Hand, 1, 0, T(Vector3.UnitX)), new(Tail, 0, 0, T(-Vector3.UnitY))],
        [], [], []);

    /// <summary>
    /// 31 samples over one second (frames 0 to 30): the arm turns from 0 to 90 degrees about Z and the
    /// root slides from 0 to 1 along Z, while the hand and the tail hold still.
    /// </summary>
    private static MotionClip Clip()
    {
        var poses = Enumerable.Range(0, 31).Select(i => new[]
        {
            T(new Vector3(0, 0, i / 30f)),
            T(Vector3.UnitX, Q(Vector3.UnitZ, 90 * i / 30f)),
            T(Vector3.UnitX),
            T(-Vector3.UnitY),
        }).ToArray();
        return MotionClip.FromPoses(Rig(), 1, poses);
    }

    private static MotionLayer Layer(MotionLayerKind kind, int start = 0, int end = 30, int fadeIn = 0, int fadeOut = 0, float amount = 1,
        PoseComponents components = PoseComponents.All, params MotionKey[] keys) =>
        new("m1", kind, kind.ToString(), start, end, fadeIn, fadeOut, amount, components, MotionInterpolation.Linear, [], [.. keys]);

    private static MotionKey Key(int frame, params (string Bone, BoneTransform Value)[] bones) =>
        new(frame, bones.ToImmutableDictionary(b => b.Bone, b => b.Value, StringComparer.Ordinal));

    private static MotionClip Applied(params MotionLayer[] layers)
    {
        var clip = Clip();
        MotionEvaluator.Apply(clip, layers);
        return clip;
    }

    private static int Index(string bone) => bone switch { Root => 0, Arm => 1, Hand => 2, _ => 3 };

    private static void Fades(Action<bool, string> check)
    {
        var faded = Layer(MotionLayerKind.Offset, 10, 20, 5, 5);
        check(MotionEvaluator.Fade(faded, 9) == 0 && MotionEvaluator.Fade(faded, 10) == 0 && MotionEvaluator.Fade(faded, 15) == 1 &&
              MotionEvaluator.Fade(faded, 20) == 0 && MotionEvaluator.Fade(faded, 21) == 0,
            "a layer is off outside its range and at the ends of its fades, and fully on between them");
        check(Math.Abs(MotionEvaluator.Fade(faded, 12.5f) - .5f) < 1e-6f && Math.Abs(MotionEvaluator.Fade(faded, 17.5f) - .5f) < 1e-6f,
            "fades ease on a smoothstep, halfway at their middle");
        var whole = Layer(MotionLayerKind.Offset);
        check(MotionEvaluator.Fade(whole, 0) == 1 && MotionEvaluator.Fade(whole, 30.0004f) == 1 && MotionEvaluator.Fade(whole, 30.01f) == 0,
            "a layer without fades covers its first and last frame, a rounding error past its end included");
    }

    private static void Offsets(Action<bool, string> check)
    {
        var turn = Key(0, (Hand, T(Vector3.Zero, Q(Vector3.UnitY, 90))));
        var turned = Applied(Layer(MotionLayerKind.Offset, keys: turn));
        var original = Clip();
        check(Enumerable.Range(0, turned.Samples).All(i => Degrees(turned.Get(i, Index(Hand)).Rotation, Q(Vector3.UnitY, 90)) < .01f &&
                                                       turned.Get(i, Index(Arm)) == original.Get(i, Index(Arm))),
            "an offset turns its bone on its own axes over the whole layer and leaves other bones alone");
        var half = Applied(Layer(MotionLayerKind.Offset, amount: .5f, keys: turn));
        check(Degrees(half.Get(10, Index(Hand)).Rotation, Q(Vector3.UnitY, 45)) < .01f, "an offset's amount scales how far it turns");
        var eased = Applied(Layer(MotionLayerKind.Offset, fadeIn: 30, keys: turn));
        check(Degrees(eased.Get(0, Index(Hand)).Rotation, Quaternion.Identity) < .01f &&
              Degrees(eased.Get(15, Index(Hand)).Rotation, Q(Vector3.UnitY, 45)) < .01f &&
              Degrees(eased.Get(30, Index(Hand)).Rotation, Q(Vector3.UnitY, 90)) < .01f,
            "an offset eases in over its fade");
        var section = Applied(Layer(MotionLayerKind.Offset, 10, 20, keys: turn));
        check(Degrees(section.Get(9, Index(Hand)).Rotation, Quaternion.Identity) < .01f &&
              Degrees(section.Get(15, Index(Hand)).Rotation, Q(Vector3.UnitY, 90)) < .01f &&
              Degrees(section.Get(21, Index(Hand)).Rotation, Quaternion.Identity) < .01f,
            "an offset over a section changes only the frames inside it");

        // A move in character space: the hand's model position moves by exactly the offset,
        // however far its parent has turned.
        var lift = Key(0, (Hand, T(Vector3.UnitY)));
        var lifted = Applied(Layer(MotionLayerKind.Offset, components: PoseComponents.Position, keys: lift));
        var moves = Enumerable.Range(0, lifted.Samples).Select(i =>
        {
            var before = MotionEvaluator.ModelSpace(original.Skeleton, Pose(original, i));
            var after = MotionEvaluator.ModelSpace(lifted.Skeleton, Pose(lifted, i));
            return after[Index(Hand)].Position - before[Index(Hand)].Position;
        }).ToArray();
        check(moves.All(move => Vector3.Distance(move, Vector3.UnitY) < 1e-5f) &&
              Vector3.Distance(lifted.Get(30, Index(Hand)).Position, new Vector3(2, 0, 0)) < 1e-5f,
            "an offset's move is in character space and lands in the parent's space turned back by the parent");
        var rotationOnly = Applied(Layer(MotionLayerKind.Offset, components: PoseComponents.Rotation, keys: lift));
        check(Enumerable.Range(0, 31).All(i => rotationOnly.Get(i, Index(Hand)) == original.Get(i, Index(Hand))),
            "an offset changes only the parts of a transform it is set to change");

        var grow = Key(0, (Hand, new BoneTransform(Vector3.Zero, Quaternion.Identity, new Vector3(2, 1, 1))));
        var grown = Applied(Layer(MotionLayerKind.Offset, amount: .5f, keys: grow));
        check(Vector3.Distance(grown.Get(5, Index(Hand)).Scale, new Vector3(1.5f, 1, 1)) < 1e-5f, "an offset's scale is a factor its amount eases toward");
        var shrink = Key(0, (Hand, new BoneTransform(Vector3.Zero, Quaternion.Identity, new Vector3(.5f, 1, 1))));
        var shrunk = Applied(Layer(MotionLayerKind.Offset, amount: 4, keys: shrink));
        check(shrunk.Get(5, Index(Hand)).Scale.X >= MotionEvaluator.MinScale, "a strong shrinking offset never turns a scale negative");

        var disabled = Applied(Layer(MotionLayerKind.Offset, keys: turn) with { Enabled = false });
        check(Enumerable.Range(0, 31).All(i => disabled.Get(i, Index(Hand)) == original.Get(i, Index(Hand))), "a disabled layer changes nothing");
    }

    private static MotionTransform[] Pose(MotionClip clip, int sample) => [.. Enumerable.Range(0, clip.Bones).Select(b => clip.Get(sample, b))];

    private static void Keys(Action<bool, string> check)
    {
        var keys = new[] { Key(0, (Hand, T(Vector3.Zero, Quaternion.Identity)), (Arm, T(Vector3.Zero, Q(Vector3.UnitX, 30)))),
            Key(30, (Hand, T(Vector3.Zero, Q(Vector3.UnitY, 90)))) };
        var linear = Layer(MotionLayerKind.Offset, keys: keys);
        check(Degrees(MotionEvaluator.KeyValue(linear, Hand, 15)!.Value.Rotation, Q(Vector3.UnitY, 45)) < .01f &&
              Degrees(MotionEvaluator.KeyValue(linear, Hand, -5)!.Value.Rotation, Quaternion.Identity) < .01f &&
              Degrees(MotionEvaluator.KeyValue(linear, Hand, 40)!.Value.Rotation, Q(Vector3.UnitY, 90)) < .01f,
            "keys interpolate between each other and hold before the first and after the last");
        check(Degrees(MotionEvaluator.KeyValue(linear, Arm, 20)!.Value.Rotation, Q(Vector3.UnitX, 30)) < .01f &&
              MotionEvaluator.KeyValue(linear, Root, 10) == null,
            "a bone follows only the keys that name it");
        var smooth = linear with { Interpolation = MotionInterpolation.Smooth };
        var expected = 90 * AnimationPoseRules.SmoothStep(1 / 3f);
        check(Math.Abs(Degrees(MotionEvaluator.KeyValue(smooth, Hand, 10)!.Value.Rotation, Quaternion.Identity) - expected) < .01f,
            "smooth keys ease between each other");
        var step = linear with { Interpolation = MotionInterpolation.Step };
        check(Degrees(MotionEvaluator.KeyValue(step, Hand, 29)!.Value.Rotation, Quaternion.Identity) < .01f &&
              Degrees(MotionEvaluator.KeyValue(step, Hand, 30)!.Value.Rotation, Q(Vector3.UnitY, 90)) < .01f,
            "stepped keys hold until the next key");
        var applied = Applied(linear);
        check(Degrees(applied.Get(15, Index(Hand)).Rotation, Q(Vector3.UnitY, 45)) < .01f, "applied keys turn the bone by their value at each sample");
    }

    private static void Amplitudes(Action<bool, string> check)
    {
        var original = Clip();
        var held = Applied(Layer(MotionLayerKind.Amplitude, amount: 0, components: PoseComponents.Rotation | PoseComponents.Position));
        check(Enumerable.Range(0, 31).All(i => Math.Abs(AboutZ(held.Get(i, Index(Arm)).Rotation) - 45) < .05f &&
                                                Vector3.Distance(held.Get(i, Index(Root)).Position, new Vector3(0, 0, .5f)) < 1e-5f),
            "an amplitude of 0 holds every bone at its average pose over the layer");
        var doubled = Applied(Layer(MotionLayerKind.Amplitude, amount: 2, components: PoseComponents.Rotation | PoseComponents.Position));
        check(Math.Abs(AboutZ(doubled.Get(0, Index(Arm)).Rotation) + 45) < .05f && Math.Abs(AboutZ(doubled.Get(30, Index(Arm)).Rotation) - 135) < .05f &&
              Vector3.Distance(doubled.Get(0, Index(Root)).Position, new Vector3(0, 0, -.5f)) < 1e-5f,
            "an amplitude of 2 moves bones twice as far from their average pose");
        check(Enumerable.Range(0, 31).All(i => doubled.Get(i, Index(Tail)) == original.Get(i, Index(Tail)) &&
                                                doubled.Get(i, Index(Hand)) == original.Get(i, Index(Hand))),
            "an amplitude leaves bones that hold still exactly as they were, so the bake adds no tracks for them");
        var turnsOnly = Applied(Layer(MotionLayerKind.Amplitude, amount: 0, components: PoseComponents.Rotation));
        check(turnsOnly.Get(0, Index(Root)) == original.Get(0, Index(Root)), "an amplitude changes only the parts it is set to change");
        var masked = Applied(Layer(MotionLayerKind.Amplitude, amount: 0, components: PoseComponents.Rotation) with { Bones = [Hand] });
        check(masked.Get(0, Index(Arm)) == original.Get(0, Index(Arm)), "an amplitude limited to some bones leaves the others alone");
        var unchanged = Layer(MotionLayerKind.Amplitude, amount: 1);
        check(!MotionEvaluator.Active(unchanged) && MotionEvaluator.Active(unchanged with { Amount = 1.5f }),
            "an amplitude of 1 changes nothing and counts as no edit");
        var section = Applied(Layer(MotionLayerKind.Amplitude, 0, 10, amount: 0, components: PoseComponents.Rotation));
        check(Math.Abs(AboutZ(section.Get(5, Index(Arm)).Rotation) - 15) < .05f &&
              Math.Abs(AboutZ(section.Get(20, Index(Arm)).Rotation) - 60) < .05f,
            "an amplitude over a section holds the section's own average and leaves the rest alone");
    }

    private static void Poses(Action<bool, string> check)
    {
        var pose = Key(0, (Hand, new BoneTransform(new Vector3(3, 0, 0), Q(Vector3.UnitX, 60), Vector3.One)));
        var posed = Applied(Layer(MotionLayerKind.Pose, keys: pose));
        check(Enumerable.Range(0, 31).All(i => Degrees(posed.Get(i, Index(Hand)).Rotation, Q(Vector3.UnitX, 60)) < .01f &&
                                                Vector3.Distance(posed.Get(i, Index(Hand)).Position, Vector3.UnitX) < 1e-6f),
            "a pose turns its bones to the key and leaves their positions unless the key moves them");
        var moved = Applied(Layer(MotionLayerKind.Pose, keys: pose with { Moved = [Hand] }));
        check(Vector3.Distance(moved.Get(3, Index(Hand)).Position, new Vector3(3, 0, 0)) < 1e-6f, "a pose key that moves a bone sets its position too");
        var blended = Applied(Layer(MotionLayerKind.Pose, amount: .5f, keys: pose));
        check(Degrees(blended.Get(3, Index(Hand)).Rotation, Q(Vector3.UnitX, 30)) < .01f, "a pose's amount blends between the motion and the pose");
        var two = Applied(Layer(MotionLayerKind.Pose, keys:
            [Key(0, (Hand, T(Vector3.UnitX))), Key(30, (Hand, T(Vector3.UnitX, Q(Vector3.UnitZ, 90))))]));
        check(Math.Abs(AboutZ(two.Get(15, Index(Hand)).Rotation) - 45) < .05f, "a pose layer blends from one key pose to the next");
    }

    private static void Order(Action<bool, string> check)
    {
        var offset = Layer(MotionLayerKind.Offset, keys: Key(0, (Hand, T(Vector3.Zero, Q(Vector3.UnitY, 90)))));
        var pose = Layer(MotionLayerKind.Pose, keys: Key(0, (Hand, T(Vector3.UnitX)))) with { Key = "m2" };
        var poseLast = Applied(offset, pose);
        var offsetLast = Applied(pose, offset);
        check(Degrees(poseLast.Get(5, Index(Hand)).Rotation, Quaternion.Identity) < .01f &&
              Degrees(offsetLast.Get(5, Index(Hand)).Rotation, Q(Vector3.UnitY, 90)) < .01f,
            "layers apply in order, each to the motion the ones before it made");

        // A turn past the far side of the sphere still leaves every sample on its predecessor's hemisphere.
        var far = Applied(Layer(MotionLayerKind.Offset, keys:
            [Key(0, (Arm, T(Vector3.Zero, Q(Vector3.UnitZ, 0)))), Key(30, (Arm, T(Vector3.Zero, Q(Vector3.UnitZ, 170))))]));
        check(Enumerable.Range(1, 30).All(i => Quaternion.Dot(far.Get(i - 1, Index(Arm)).Rotation, far.Get(i, Index(Arm)).Rotation) >= 0),
            "applied layers keep each rotation on its previous sample's hemisphere");
    }

    private static void Spaces(Action<bool, string> check)
    {
        check(Degrees(MotionEvaluator.Pow(Q(Vector3.UnitZ, 90), .5f), Q(Vector3.UnitZ, 45)) < .01f &&
              Degrees(MotionEvaluator.Pow(Q(Vector3.UnitZ, 90), 2), Q(Vector3.UnitZ, 180)) < .01f &&
              Degrees(MotionEvaluator.Pow(Q(Vector3.UnitZ, 90), 0), Quaternion.Identity) < .01f &&
              Degrees(MotionEvaluator.Pow(-Q(Vector3.UnitZ, 40), 1), Q(Vector3.UnitZ, 40)) < .01f &&
              Degrees(MotionEvaluator.Pow(Q(Vector3.UnitZ, 1e-4f), 3), Q(Vector3.UnitZ, 3e-4f)) < 1e-3f,
            "a rotation's power turns it that many times as far the short way, tiny rotations included");
        var rig = Rig();
        MotionTransform[] local =
        [
            new(new Vector3(.2f, 1, -.3f), Q(Vector3.UnitY, 30), Vector3.One),
            new(Vector3.UnitX, Q(Vector3.Normalize(new Vector3(1, 2, 3)), 50), new Vector3(1, 1.2f, 1)),
            new(new Vector3(1, .1f, 0), Q(Vector3.UnitZ, -20), new Vector3(.9f, 1, 1)),
            new(-Vector3.UnitY, Quaternion.Identity, Vector3.One),
        ];
        var model = MotionEvaluator.ModelSpace(rig, local);
        var back = MotionEvaluator.LocalSpace(rig, model);
        check(local.Zip(back).All(p => Vector3.Distance(p.First.Position, p.Second.Position) < 1e-5f &&
                                        Degrees(p.First.Rotation, p.Second.Rotation) < .01f &&
                                        Vector3.Distance(p.First.Scale, p.Second.Scale) < 1e-5f),
            "model space and local space convert into each other");
        // Havok composes a child's position without its parent's scale.
        check(Vector3.Distance(model[2].Position, model[1].Position + Vector3.Transform(local[2].Position, model[1].Rotation)) < 1e-5f,
            "a child's model position is its parent's plus its own turned by the parent, never scaled by it");
    }

    private static void Validation(Action<bool, string> check, Action<Action, string> reject)
    {
        var turn = Key(0, (Hand, T(Vector3.Zero, Q(Vector3.UnitY, 90))));
        var good = Layer(MotionLayerKind.Offset, keys: turn);
        MotionEvaluator.Validate(good);
        reject(() => MotionEvaluator.Validate(good with { End = 0 }), "a layer that ends where it starts is refused");
        reject(() => MotionEvaluator.Validate(good with { Start = -1 }), "a layer starting before the timeline is refused");
        reject(() => MotionEvaluator.Validate(good with { FadeIn = 20, FadeOut = 20 }), "fades longer than their layer are refused");
        reject(() => MotionEvaluator.Validate(good with { Amount = float.NaN }), "an amount that is not a number is refused");
        reject(() => MotionEvaluator.Validate(good with { Amount = -1 }), "a negative amount is refused");
        reject(() => MotionEvaluator.Validate(Layer(MotionLayerKind.Pose, amount: 1.5f, keys: turn)), "a pose weight above 1 is refused");
        reject(() => MotionEvaluator.Validate(good with { Keys = [Key(10, (Hand, T(Vector3.Zero))), Key(5, (Hand, T(Vector3.Zero)))] }),
            "keys out of order are refused");
        reject(() => MotionEvaluator.Validate(good with { Keys = [Key(5, (Hand, T(Vector3.Zero))), Key(5, (Hand, T(Vector3.Zero)))] }),
            "two keys on one frame are refused");
        reject(() => MotionEvaluator.Validate(good with { Keys = [Key(0, (Hand, T(new Vector3(float.NaN, 0, 0))))] }),
            "a key value that is not a transform is refused");
        reject(() => MotionEvaluator.Validate(good with { Keys = [Key(0, (Hand, new BoneTransform(Vector3.Zero, Quaternion.Identity, Vector3.Zero)))] }),
            "a key that scales a bone to nothing is refused");
        reject(() => MotionEvaluator.Validate(good with { Keys = [turn with { Moved = [Arm] }] }), "a key moving a bone it has no value for is refused");
        reject(() => MotionEvaluator.Validate([good, good]), "two layers with the same key are refused");
        reject(() => MotionEvaluator.Validate([.. Enumerable.Range(0, MotionEvaluator.MaxLayers + 1).Select(i => good with { Key = "m" + i })]),
            "more layers than an animation may have are refused");
        reject(() => MotionEvaluator.Apply(Clip(), [good with { Keys = [Key(0, ("j_missing", T(Vector3.Zero)))] }]),
            "a layer naming a bone the skeleton lacks is refused when applied");
        reject(() => MotionEvaluator.Apply(Clip(), [Layer(MotionLayerKind.Amplitude, amount: 0) with { Bones = ["j_missing"] }]),
            "a layer limited to a bone the skeleton lacks is refused when applied");
        reject(() => MotionEvaluator.CheckBones([good with { Bones = ["j_missing"] }], Rig()), "the bake refuses unknown bones before it samples");
        check(!MotionEvaluator.Active(Layer(MotionLayerKind.Offset)) && !MotionEvaluator.Active(good with { Components = PoseComponents.None }) &&
              !MotionEvaluator.Active(good with { Amount = 0 }) && MotionEvaluator.Active(good),
            "a layer without keys, parts or strength counts as no edit");
        reject(() => _ = new MotionClip(Rig(), 1, (int)(MotionClip.MaxBytes / (4L * 4 * MotionClip.Stride)) + 1),
            "a clip too long to keep in memory is refused");
        var clip = Clip();
        check(clip.Time(clip.Samples - 1) == clip.Duration && Math.Abs(clip.Frame(15) - 15) < 1e-4f, "a clip's samples sit on the bake's grid");
        var copy = clip.Clone();
        copy.Set(0, 0, MotionTransform.Identity with { Position = Vector3.One });
        check(clip.Get(0, 0).Position == Vector3.Zero, "a cloned clip is a copy of its own");
    }

    private static void Documents(Action<bool, string> check)
    {
        var doc = new TimelineDocument(30, 30, null, [], 1);
        var cleared = doc with { Motion = default };
        check(doc.Motion.IsEmpty && cleared.Motion is { IsDefault: false, IsEmpty: true } && doc.NextMotionKey == 1,
            "a document starts without bone layers and never holds a default list");
        var turn = Layer(MotionLayerKind.Offset, keys: Key(0, (Hand, T(Vector3.Zero, Q(Vector3.UnitY, 90)))));
        var edited = doc with { Motion = [turn] };
        check(!doc.MotionChanged && edited.MotionChanged && !(doc with { Motion = [turn with { Enabled = false }] }).MotionChanged,
            "a document's motion changes only with a layer that does something");
        check(AnimationEditService.ChangesMotion(edited) && !AnimationEditService.ChangesMotion(doc) &&
              AnimationEditService.ChangesMotion(doc with { Length = 40 }) && AnimationEditService.RetimeSeconds(edited) == null,
            "saving bakes the motion for bone layers or a new length, and layers alone keep the length");
    }

    private static void Journals(Action<bool, string> check)
    {
        var temp = Path.Combine(Path.GetTempPath(), "ie-motion-layer-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var resource = new AnimationResource("chara/human/c0101/animation/a0001/bt_common/emote/dance.pap", "dance.pap", "hash");
            var clip = new AnimationClip(resource.GamePath, "loop", 0, 0, 42, "chara/test.sklb");
            // Captured from the posed copy of the player in GPose.
            var capture = new AnimationCapture("clip", 1, 1, Guid.NewGuid(), "Test", "Dance", clip, null, [resource.GamePath], [resource],
                new PoseSnapshot(0, 0, 0, false, [], DateTime.UtcNow, ""), DateTime.UtcNow, true, ActorIndex: 201);
            var layer = new MotionLayer("m3", MotionLayerKind.Pose, "Wave", 5, 25, 3, 4, .75f, PoseComponents.Rotation | PoseComponents.Position,
                MotionInterpolation.Smooth, [Hand], [Key(5, (Hand, new BoneTransform(new Vector3(1, 2, 3), Q(Vector3.UnitX, 20), new Vector3(1, 1.1f, 1))))
                    with { Moved = [Hand] }]);
            var request = new AnimationBakeRequest(Guid.NewGuid(), capture, AnimationDestination.NewMod, "Motion", false,
                ImmutableHashSet<PoseBoneId>.Empty, PoseComponents.None, AnimationOperation.EditTimeline,
                Timeline: new AnimationTimelineEdit(false, 0, null, [layer]));
            var store = new AnimationJournalStore(temp);
            store.Save(new AnimationEditJournal { Id = request.Id, Request = AnimationCommitService.RecoveryRequest(request) });
            var reloaded = new AnimationJournalStore(temp).Load().Single().Request;
            check(reloaded.Capture.ActorIndex == 201, "a journal keeps which character the animation was captured from, a GPose actor included");
            var loaded = reloaded.Timeline!.Motion.Single();
            var key = loaded.Keys.Single();
            check(loaded with { Bones = [], Keys = [] } == layer with { Bones = [], Keys = [] } && loaded.Bones.SequenceEqual([Hand]) &&
                  key.Frame == 5 && key.Moved.SequenceEqual([Hand]) && key.Bones[Hand] == layer.Keys[0].Bones[Hand],
                "a timeline edit's journal keeps its bone layers, keys and values across a restart");

            // Journals written before bone layers have no Motion field.
            var path = Path.Combine(temp, "AnimationEdits", request.Id.ToString("N"), "journal.json");
            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            json["Request"]!["Timeline"]!.AsObject().Remove("Motion");
            json["Request"]!["Capture"]!.AsObject().Remove("ActorIndex");
            File.WriteAllText(path, json.ToJsonString());
            var legacyRequest = new AnimationJournalStore(temp).Load().Single().Request;
            check(legacyRequest.Timeline!.Motion is { IsDefault: false, IsEmpty: true }, "an older timeline journal loads with no bone layers");
            check(legacyRequest.Capture.ActorIndex == 0, "an older journal's capture is your own character's");
        }
        finally { Directory.Delete(temp, true); }
    }
}
