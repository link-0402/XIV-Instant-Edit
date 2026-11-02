using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json.Nodes;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Services.Physics;
using InstantEdit.Ui;
using InstantEdit.Ui.Timeline;

/// <summary>
/// Spring layers: bones that lag behind the motion and overshoot it, parents before children, settling at the layer's start,
/// easing back onto the motion at an end that doesn't reach the clip's, round a loop's seam; their validation, journals, the
/// physics bones their picker leaves out and their edits.
/// </summary>
internal static class SpringLayerFixture
{
    private const string Hips = "j_kosi", Waist = "j_sebo_a", Chest = "j_sebo_b";

    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Validation(check, reject);
        Journals(check);
        Still(check);
        Steady(check);
        Step(check);
        Chain(check);
        Loop(check);
        EndEase(check);
        Extreme(check);
        Pose(check);
        Physics(check);
        Views(check);
    }

    private static void Validation(Action<bool, string> check, Action<Action, string> reject)
    {
        var spring = Spring(new MotionSpring(), Waist);
        MotionEvaluator.Validate(spring);
        MotionEvaluator.Validate(Spring(new MotionSpring(MotionSprings.MinLag, 0), Waist));
        MotionEvaluator.Validate(Spring(new MotionSpring(MotionSprings.MaxLag, MotionSprings.MaxBounce), Waist));
        void Refused(MotionLayer layer, string what) => reject(() => MotionEvaluator.Validate(layer), what);
        Refused(Spring(new MotionSpring(.4f), Waist), "a spring lagging under half a frame is refused");
        Refused(Spring(new MotionSpring(30.1f), Waist), "a spring lagging over 30 frames is refused");
        Refused(Spring(new MotionSpring(4, -.01f), Waist), "a negative bounce is refused");
        Refused(Spring(new MotionSpring(4, .96f), Waist), "a bounce over 95% is refused");
        Refused(Spring(new MotionSpring(float.NaN), Waist), "a spring lag that isn't a number is refused");
        Refused(Spring(new MotionSpring(4, float.NaN), Waist), "a spring bounce that isn't a number is refused");
        Refused(new MotionLayer("m1", MotionLayerKind.Offset, "Offset 1", 0, 60, 0, 0, 1, PoseComponents.All, MotionInterpolation.Linear,
            Spring: new MotionSpring()), "a spring on an offset layer is refused");
        Refused(spring with { Spring = null }, "a spring layer without its spring is refused");
        Refused(spring with { Keys = [new MotionKey(0, ImmutableDictionary<string, BoneTransform>.Empty.Add(Waist, T(Vector3.Zero)))] },
            "a spring layer with keys is refused");
        Refused(spring with { Pins = [new MotionPin(Hips)] }, "a spring layer with pins is refused");
        Refused(spring with { Bones = ["n_root", Waist] }, "a spring on the root is refused");
        Refused(spring with { Bones = [PinChains.Body] }, "a spring on the body is refused");
        Refused(spring with { Amount = 1.5f }, "a spring past full strength is refused");
        check(MotionEvaluator.BlendsAtMostFully(MotionLayerKind.Spring), "a spring blends at most fully");

        check(MotionEvaluator.Active(spring), "a spring with bones is active");
        check(!MotionEvaluator.Active(spring with { Bones = [] }), "a spring without bones does nothing");
        check(!MotionEvaluator.Active(spring with { Amount = 0 }), "a spring at 0% does nothing");
        check(!MotionEvaluator.Active(spring with { Components = PoseComponents.Position }), "a spring without Turn does nothing");
        var (omega, zeta) = MotionSprings.Constants(new MotionSpring(4, .3f));
        check(MathF.Abs(omega - 2 / (4f / TimelineDocument.Fps)) < 1e-4f && MathF.Abs(zeta - .7f) < 1e-6f,
            "a spring's rate follows its lag in frames and its damping is 1 - bounce");
    }

    private static void Journals(Action<bool, string> check)
    {
        var temp = Path.Combine(Path.GetTempPath(), "ie-spring-layer-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var resource = new AnimationResource("chara/human/c0801/animation/a0001/bt_common/emote/dance01.pap", "dance01.pap", "hash");
            var clip = new AnimationClip(resource.GamePath, "dance", 0, 0, 42, "chara/test.sklb");
            var capture = new AnimationCapture("clip", 1, 1, Guid.NewGuid(), "Test", "Idle", clip, null, [resource.GamePath], [resource], DateTime.UtcNow, true);
            var settings = new MotionSpring(6.5f, .5f);
            var spring = Spring(settings, Waist, Chest) with { Weights = [new MotionWeight(10, .5f)] };
            var offset = new MotionLayer("m2", MotionLayerKind.Offset, "Offset 1", 0, 60, 0, 0, 1, PoseComponents.All, MotionInterpolation.Linear);
            var request = new AnimationBakeRequest(Guid.NewGuid(), capture, AnimationDestination.NewMod, "Motion", false,
                AnimationOperation.EditTimeline, Timeline: new AnimationTimelineEdit(true, 0, null, [spring, offset]));
            new AnimationJournalStore(temp).Save(new AnimationEditJournal { Id = request.Id, Request = AnimationCommitService.RecoveryRequest(request) });
            var loaded = new AnimationJournalStore(temp).Load().Single().Request.Timeline!.Motion;
            check(loaded.Length == 2 && MotionEdits.Same(loaded[0], spring) && loaded[0].Spring == settings && loaded[0].Repeat == null &&
                  MotionEdits.Same(loaded[1], offset), "a journal keeps a spring's settings");

            var path = Path.Combine(temp, "AnimationEdits", request.Id.ToString("N"), "journal.json");
            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var motion = json["Request"]!["Timeline"]!["Motion"]!;
            check(motion[0]!.AsObject().Remove("Spring"), "a journal stores a layer's spring as Spring");
            motion[1]!.AsObject().Remove("Spring");
            File.WriteAllText(path, json.ToJsonString());
            var older = new AnimationJournalStore(temp).Load().Single().Request.Timeline!.Motion;
            var refused = false;
            try { MotionEvaluator.Validate(older[0]); }
            catch (InvalidDataException) { refused = true; }
            check(older[0].Spring == null && refused && !MotionEvaluator.Active(older[0]), "a spring layer without Spring loads with none and is refused");
            check(MotionEdits.Same(older[1], offset), "a journal written before springs loads its other layers unchanged");
        }
        finally { Directory.Delete(temp, true); }

        var a = Spring(new MotionSpring(), Waist);
        check(MotionEdits.Same(a, a with { Spring = new MotionSpring() }) && !MotionEdits.Same(a, a with { Spring = new MotionSpring(5) }) &&
              !MotionEdits.Same(a, a with { Spring = new MotionSpring(4, .5f) }) && !MotionEdits.Same(a, a with { Spring = null }),
            "layers differ by their spring's settings");
    }

    private static void Still(Action<bool, string> check)
    {
        var still = Clip(61, 60, (_, _) => [.. SwingLayerFixture.Rig().Bones.Select(bone => bone.Name == Waist ? T(bone.Reference.Position, Q(Vector3.UnitX, 20)) : bone.Reference)]);
        var sprung = Applied(still, Spring(new MotionSpring(4, .5f), Hips, Waist, Chest));
        check(Enumerable.Range(0, still.Samples).All(i => Enumerable.Range(0, still.Bones).All(b => Degrees(sprung.Get(i, b).Rotation, still.Get(i, b).Rotation) < 1e-4f)),
            "a spring on a still clip changes nothing");
    }

    private static void Steady(Action<bool, string> check)
    {
        // The hips turn 2 degrees a frame; a spring that doesn't bounce settles about Lag frames behind.
        var ramp = Clip(61, 60, (_, f) => [.. SwingLayerFixture.Rig().Bones.Select(bone => bone.Name == Hips ? T(bone.Reference.Position, Q(Vector3.UnitY, 2 * f)) : bone.Reference)]);
        const float lag = 4;
        var sprung = Applied(ramp, Spring(new MotionSpring(lag, 0), Hips));
        var behind = Enumerable.Range(0, 61).Select(i => Degrees(sprung.Get(i, Index(Hips)).Rotation, ramp.Get(i, Index(Hips)).Rotation)).ToArray();
        var settled = (int)(3 * lag);
        var steady = Enumerable.Range(settled + 1, 60 - settled).All(i => MathF.Abs(behind[i] - behind[i - 1]) < .05f);
        check(steady && behind[settled..].All(d => d >= .5f * lag * 2 && d <= 1.5f * lag * 2),
            $"a spring on a steady turn settles to a steady lag near Lag frames ({behind[settled]:0.##} to {behind[60]:0.##} degrees)");
        check(behind[0] < 1e-3f, "a spring starts settled on the motion");
    }

    private static void Step(Action<bool, string> check)
    {
        var step = StepClip(61, 60, 20, 30);
        const float lag = 4;
        var calm = Applied(step, Spring(new MotionSpring(lag, 0), Waist));
        var bouncy = Applied(step, Spring(new MotionSpring(lag, .5f), Waist));
        var calmMost = Enumerable.Range(0, 61).Max(i => Turn(calm, i, Waist));
        var bouncyMost = Enumerable.Range(0, 61).Max(i => Turn(bouncy, i, Waist));
        check(calmMost <= 30.01f && Turn(calm, 25, Waist) < 29, $"a spring without bounce follows a step without overshooting ({calmMost:0.###} degrees at most)");
        check(bouncyMost > 30.01f && Enumerable.Range(20 + (int)(6 * lag), 61 - 20 - (int)(6 * lag)).All(i => MathF.Abs(Turn(bouncy, i, Waist) - 30) < .5f),
            $"a bouncy spring overshoots a step and settles on it ({bouncyMost:0.##} degrees at most)");
    }

    private static void Chain(Action<bool, string> check)
    {
        var step = StepClip(61, 60, 20, 30);
        var sprung = Applied(step, Spring(new MotionSpring(4, .5f), Waist, Chest));
        int Peak(string bone) => Enumerable.Range(0, 61).MaxBy(i => ModelTurn(sprung, i, bone));
        var (waist, chest) = (Peak(Waist), Peak(Chest));
        check(chest > waist, $"a sprung child follows through after its sprung parent (peaks at frames {waist} and {chest})");
        check(Enumerable.Range(0, 61).Any(i => Degrees(sprung.Get(i, Index(Chest)).Rotation, step.Get(i, Index(Chest)).Rotation) > .5f),
            "a sprung child still in the motion is moved by its parent's spring");
    }

    private static void Loop(Action<bool, string> check)
    {
        var sine = Clip(61, 60, (_, f) => [.. SwingLayerFixture.Rig().Bones.Select(bone => bone.Name == Hips
            ? T(bone.Reference.Position, Q(Vector3.UnitY, 20 * MathF.Sin(2 * MathF.PI * f / 60))) : bone.Reference)], loops: true);
        var sprung = Applied(sine, Spring(new MotionSpring(6, .3f), Hips));
        var seam = Degrees(sprung.Get(0, Index(Hips)).Rotation, sprung.Get(60, Index(Hips)).Rotation);
        check(seam < .1f && Degrees(sprung.Get(15, Index(Hips)).Rotation, sine.Get(15, Index(Hips)).Rotation) > .5f,
            $"a spring round a loop meets itself at the seam ({seam:0.###} degrees apart)");
        // One starting later doesn't wrap: it eases back onto the motion by the loop's end, so the seam doesn't pop.
        var late = Applied(sine, Spring(new MotionSpring(6, .3f), Hips) with { Start = 20 });
        var lastOff = Degrees(late.Get(60, Index(Hips)).Rotation, sine.Get(60, Index(Hips)).Rotation);
        check(lastOff < .01f && Degrees(late.Get(40, Index(Hips)).Rotation, sine.Get(40, Index(Hips)).Rotation) > .5f,
            $"a spring from partway to a loop's end ends on the motion ({lastOff:0.####} degrees off at the last sample)");
    }

    private static void EndEase(Action<bool, string> check)
    {
        // A layer ending at frame 40 of 60 without a fade-out eases back onto the motion before its end.
        var ramp = Clip(61, 60, (_, f) => [.. SwingLayerFixture.Rig().Bones.Select(bone => bone.Name == Hips ? T(bone.Reference.Position, Q(Vector3.UnitY, .5f * f)) : bone.Reference)]);
        var sprung = Applied(ramp, Spring(new MotionSpring(), Hips) with { End = 40 });
        var most = new[] { 39, 40, 41 }.Max(i => Step(sprung, i) - Step(ramp, i));
        check(Degrees(sprung.Get(30, Index(Hips)).Rotation, ramp.Get(30, Index(Hips)).Rotation) > .5f && most <= 1,
            $"a spring ending before the clip doesn't pop where it ends ({most:0.###} degrees more than the motion)");
        check(Enumerable.Range(40, 21).All(i => Degrees(sprung.Get(i, Index(Hips)).Rotation, ramp.Get(i, Index(Hips)).Rotation) < 1e-3f),
            "after a spring ends the motion is as it was");
    }

    private static void Extreme(Action<bool, string> check)
    {
        // The stiffest spring with the most bounce, a 90 degree step on a 30 fps clip: it stays finite and settles.
        var step = StepClip(121, 120, 20, 90);
        var sprung = Applied(step, Spring(new MotionSpring(MotionSprings.MinLag, MotionSprings.MaxBounce), Waist) with { End = 120 });
        var finite = Enumerable.Range(0, sprung.Samples).All(i => Enumerable.Range(0, sprung.Bones).All(b => Finite(sprung.Get(i, b))));
        var last = Turn(sprung, 120, Waist);
        check(finite && MathF.Abs(last - 90) < .5f, $"the stiffest, bounciest spring stays finite and settles ({last:0.##} degrees at the end)");
        // With ζ = 0.05 it swings far past the step before settling.
        var peak = Enumerable.Range(0, sprung.Samples).Max(i => Turn(sprung, i, Waist));
        var after = Degrees(sprung.Get(21, Index(Waist)).Rotation, step.Get(21, Index(Waist)).Rotation);
        check(peak > 105 && after > 1, $"the stiffest, bounciest spring overshoots far ({peak:0.#} degrees at most, {after:0.#} off just after the step)");
    }

    private static void Pose(Action<bool, string> check)
    {
        var rig = SwingLayerFixture.Rig();
        var step = StepClip(61, 60, 20, 30);
        var layer = Spring(new MotionSpring(), Waist);
        var local = Enumerable.Range(0, step.Bones).Select(b => step.Get(25, b)).ToArray();
        var at = MotionEvaluator.ApplyAt(rig, local, [layer], 25, 0, 60);
        check(Enumerable.Range(0, step.Bones).All(b => at[b] == local[b]) && BonePosing.TakesWholeClip([layer]),
            "one pose leaves a spring out, so the Bones window starts from the bake");
        var animated = MotionSymmetry.Animated(rig, [0], [layer with { Bones = [Hips, Waist] }], 1);
        check(animated != null && !animated.Contains(Index(Waist)), "a spring only reshapes motion, so a symmetry after it adds none");
    }

    private static void Physics(Action<bool, string> check)
    {
        static PhybNode Node(string bone) => new() { Bone = PhybName.From(bone) };
        var file = new PhybFile { Simulators = [new PhybSimulator { Chains = [new PhybChain { Nodes = [Node("j_sk_a"), Node("j_sk_b"), Node("")] }] }] };
        var bones = PhysicsDrivenBones.From([file]);
        check(bones.SetEquals(["j_sk_a", "j_sk_b"]), "physics bones are every bone a chain names, its boneless tip left out");
        check(PhysicsDrivenBones.From([]).IsEmpty && PhysicsDrivenBones.From([new PhybFile(), file, file]).Count == 2, "no file names none, and each bone counts once");
    }

    private static void Views(Action<bool, string> check)
    {
        var rig = SwingLayerFixture.Rig();
        var index = MotionEvaluator.Index(rig);
        var physics = ImmutableHashSet.Create(StringComparer.Ordinal, Chest);
        check(TimelineWindowViews.SpringRefuses("n_root", rig, index, null) && TimelineWindowViews.SpringRefuses(PinChains.Body, rig, index, null) &&
              TimelineWindowViews.SpringRefuses(Chest, rig, index, physics) && !TimelineWindowViews.SpringRefuses(Chest, rig, index, null) &&
              !TimelineWindowViews.SpringRefuses(Waist, rig, index, physics),
            "the spring picker leaves out the root, the body and the bones the game's physics moves; unknown physics leaves nothing out");
        var doc = MotionEdits.Add(new TimelineDocument(100, 100, null, [], 1), MotionLayerKind.Spring, 0, 100, out var key, out _);
        var limited = MotionEdits.SetBones(doc, key, ["n_root", PinChains.Body, Waist]);
        check(MotionEdits.Find(limited, key)!.Bones.SequenceEqual([Waist]), "a spring's bones never take the root or the body");
    }

    /// <summary>The waist turned <paramref name="degrees"/> about X from frame <paramref name="at"/> on, still before it.</summary>
    private static MotionClip StepClip(int samples, float frames, float at, float degrees) =>
        Clip(samples, frames, (_, f) => [.. SwingLayerFixture.Rig().Bones.Select(bone => bone.Name == Waist
            ? T(bone.Reference.Position, Q(Vector3.UnitX, f >= at ? degrees : 0)) : bone.Reference)]);

    private static MotionClip Clip(int samples, float frames, Func<int, float, BoneTransform[]> pose, bool loops = false)
    {
        var poses = Enumerable.Range(0, samples).Select(i => pose(i, frames * i / (samples - 1))).ToArray();
        var made = MotionClip.FromPoses(SwingLayerFixture.Rig(), frames / TimelineDocument.Fps, poses);
        var clip = new MotionClip(SwingLayerFixture.Rig(), made.Duration, made.Samples) { Loops = loops };
        clip.CopyFrom(made);
        return clip;
    }

    private static MotionClip Applied(MotionClip source, params MotionLayer[] layers)
    {
        var clip = source.Clone();
        MotionEvaluator.Apply(clip, layers.Select((layer, i) => layer with { Key = "m" + (i + 1) }).ToArray());
        return clip;
    }

    private static int Index(string bone) => MotionEvaluator.Index(SwingLayerFixture.Rig())[bone];

    /// <summary>How far a bone is turned from rest at <paramref name="sample"/>, in degrees.</summary>
    private static float Turn(MotionClip clip, int sample, string bone) => Degrees(clip.Get(sample, Index(bone)).Rotation, Quaternion.Identity);

    /// <summary>How far a bone is turned from rest in model space at <paramref name="sample"/>, in degrees.</summary>
    private static float ModelTurn(MotionClip clip, int sample, string bone) =>
        Degrees(MotionEvaluator.ModelSpace(SwingLayerFixture.Rig(), Enumerable.Range(0, clip.Bones).Select(b => clip.Get(sample, b)).ToArray())[Index(bone)].Rotation,
            Quaternion.Identity);

    /// <summary>How far the hips turn from the sample before <paramref name="sample"/> to it, in degrees.</summary>
    private static float Step(MotionClip clip, int sample) =>
        Degrees(clip.Get(sample - 1, Index(Hips)).Rotation, clip.Get(sample, Index(Hips)).Rotation);

    private static bool Finite(MotionTransform value) =>
        float.IsFinite(value.Rotation.X) && float.IsFinite(value.Rotation.Y) && float.IsFinite(value.Rotation.Z) && float.IsFinite(value.Rotation.W) &&
        float.IsFinite(value.Position.X) && float.IsFinite(value.Position.Y) && float.IsFinite(value.Position.Z);

    private static Quaternion Q(Vector3 axis, float degrees) => Quaternion.CreateFromAxisAngle(axis, degrees * MathF.PI / 180);

    /// <summary>The angle between two turns in degrees, read from the turn's vector part so small angles stay precise.</summary>
    private static float Degrees(Quaternion a, Quaternion b)
    {
        var turn = Quaternion.Inverse(Quaternion.Normalize(a)) * Quaternion.Normalize(b);
        return 2 * MathF.Atan2(new Vector3(turn.X, turn.Y, turn.Z).Length(), MathF.Abs(turn.W)) * 180 / MathF.PI;
    }

    private static MotionLayer Spring(MotionSpring spring, params string[] bones) =>
        new("m1", MotionLayerKind.Spring, "Spring 1", 0, 60, 0, 0, 1, PoseComponents.Rotation, MotionInterpolation.Smooth,
            Bones: [.. bones], Spring: spring);

    private static BoneTransform T(Vector3 position, Quaternion? rotation = null) => new(position, rotation ?? Quaternion.Identity, Vector3.One);
}
