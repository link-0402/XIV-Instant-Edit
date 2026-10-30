using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json.Nodes;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui.Timeline;

/// <summary>
/// Swing layers: a whole number of swings over the layer in the character's axes, follow-through down chains, twins
/// half a swing apart, Tilt and Twist shared along a chain and Slide only on its top bone; their validation and journals.
/// </summary>
internal static class SwingLayerFixture
{
    private const string Hips = "j_kosi", Waist = "j_sebo_a", Chest = "j_sebo_b", ArmL = "j_ude_a_l", ArmR = "j_ude_a_r";

    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Validation(check, reject);
        Journals(check);
        Rest(check);
        Sign(check);
        Seam(check);
        Twins(check);
        Spread(check);
        Slide(check);
        Delay(check);
        Gaps(check);
        Dense(check);
        Shape(check);
        Live(check);
        Animated(check);
        Shaped(check);
    }

    // Review: a shaped wave must still end at rest, past the clip's end and round a loop, and leave rest without a snap.
    private static void Shaped(Action<bool, string> check)
    {
        var still = Still(false);
        var past = Applied(still, Swing(new MotionSwing(Shape: 1), Hips) with { End = 61 });
        check(Near(past, still, 60), "a shaped swing reaching a frame past the clip's end is at rest on its last sample");
        var plain = Applied(still, Swing(new MotionSwing(Count: 1), Hips) with { End = 61 });
        check(Near(plain, still, 60), "a plain swing reaching a frame past the clip's end is at rest on its last sample");
        var loop = Still(true);
        var looped = Applied(loop, Swing(new MotionSwing(Count: 4, Shape: 1, Delay: 0), Hips));
        check(Enumerable.Range(0, looped.Bones).All(b => NearValue(looped.Get(0, b), looped.Get(60, b), 1e-4f)),
            "a shaped swing covering a loop has the same first and last sample");
        var fine = Clip(241, 60, (_, _) => [.. Rig().Bones.Select(bone => bone.Reference)]);
        var (sharp, sine) = (Applied(fine, Swing(new MotionSwing(Count: 1, Shape: 1), Hips)), Applied(fine, Swing(new MotionSwing(Count: 1), Hips)));
        var ratio = Turn(sharp, fine, 1, Hips) / Turn(sine, fine, 1, Hips);
        check(ratio < 4.01f, $"a shaped swing leaves rest at most 4 times as fast as a sine ({ratio:0.##}x)");
    }

    private static void Rest(Action<bool, string> check)
    {
        var still = Still(false);
        var swung = Applied(still, Swing(new MotionSwing(Count: 1, Delay: 0), Hips));
        check(Near(swung, still, 0) && Near(swung, still, 60), "a swing that doesn't wrap starts and ends at rest");
        check(MathF.Abs(Turn(swung, still, 15, Hips) - 10) < .05f && MathF.Abs(Turn(swung, still, 45, Hips) - 10) < .05f &&
              Model(swung, 15, Waist).Position.X > 0 && Model(swung, 45, Waist).Position.X < 0,
            "one swing peaks a quarter of the way in at the full tilt and the other way three quarters in");

        // Review: a layer partway through a looping clip doesn't wrap, so it too starts and ends at rest.
        var loop = Still(true);
        var partway = Applied(loop, Swing(new MotionSwing(Count: 3), Hips, Waist) with { Start = 10, End = 50 });
        check(Near(partway, loop, 10) && Near(partway, loop, 50) && !Near(partway, loop, 20), "a swing partway through a loop starts and ends at rest");
    }

    private static void Sign(Action<bool, string> check)
    {
        var still = Still(false);
        var side = Applied(still, Swing(new MotionSwing(Count: 1, Delay: 0), Hips));
        var front = Applied(still, Swing(new MotionSwing(Count: 1, Direction: 90, Delay: 0), Hips));
        check(Model(side, 15, Waist).Position.X > .01f && MathF.Abs(Model(side, 15, Waist).Position.Z) < 1e-4f &&
              Model(front, 15, Waist).Position.Z > .01f && MathF.Abs(Model(front, 15, Waist).Position.X) < 1e-4f,
            "a tilt leans the bone's children toward the direction first: 0 degrees +X, 90 degrees +Z");
        var slid = Applied(still, Swing(new MotionSwing(Count: 1, Tilt: 0, Slide: .05f, Delay: 0), Hips));
        check(MathF.Abs(Model(slid, 15, Hips).Position.X - .05f) < 1e-4f && MathF.Abs(Model(slid, 15, Hips).Position.Y - 1) < 1e-4f,
            "a slide moves the bone along the direction by its size");
    }

    private static void Seam(Action<bool, string> check)
    {
        var loop = Still(true);
        var swung = Applied(loop, Swing(new MotionSwing(Delay: 5, Twist: 6, Slide: .02f), Hips, Waist, Chest));
        check(Enumerable.Range(0, swung.Bones).All(b => NearValue(swung.Get(0, b), swung.Get(60, b), 1e-4f)) && !Near(swung, loop, 7),
            "a swing covering a loop has the same first and last sample, trailing bones included");
    }

    private static void Twins(Action<bool, string> check)
    {
        var still = Still(false);
        var opposite = Applied(still, Swing(new MotionSwing(Count: 1, Tilt: 0, Twist: 20), ArmL, ArmR));
        var (l, r) = (AboutY(Model(opposite, 15, ArmL).Rotation), AboutY(Model(opposite, 15, ArmR).Rotation));
        check(MathF.Abs(l + r) < .05f && MathF.Abs(MathF.Abs(l) - 20) < .05f, $"twins swing opposite ways ({l:0.##}, {r:0.##})");
        var together = Applied(still, Swing(new MotionSwing(Count: 1, Tilt: 0, Twist: 20, TwinsOpposite: false), ArmL, ArmR));
        check(MathF.Abs(AboutY(Model(together, 15, ArmL).Rotation) - AboutY(Model(together, 15, ArmR).Rotation)) < .05f,
            "with Twins opposite off they swing together");
        var alone = Applied(still, Swing(new MotionSwing(Count: 1, Tilt: 0, Twist: 20), ArmR));
        check(MathF.Abs(AboutY(Model(alone, 15, ArmR).Rotation) - l) < .05f, "a right bone without its twin swings as set");
    }

    private static void Spread(Action<bool, string> check)
    {
        var still = Still(false);
        var shared = Applied(still, Swing(new MotionSwing(Count: 1, Tilt: 30, Delay: 0), Hips, Waist, Chest));
        check(MathF.Abs(Turn(shared, still, 15, Chest) - 30) < .05f && MathF.Abs(Turn(shared, still, 15, Hips) - 10) < .05f,
            "a chain shares the tilt out, so its last bone turns the set angle in all");
        var each = Applied(still, Swing(new MotionSwing(Count: 1, Tilt: 30, Delay: 0, Spread: false), Hips, Waist, Chest));
        check(MathF.Abs(Turn(each, still, 15, Chest) - 90) < .05f, "with Spread off every bone turns the full angle");
        // Review: the chest with a left arm and forearm and a right arm alone: each branch ends at the set angle.
        var branched = Applied(still, Swing(new MotionSwing(Count: 1, Tilt: 30, Delay: 0, TwinsOpposite: false), Chest, ArmL, "j_ude_b_l", ArmR));
        var (left, right) = (Turn(branched, still, 15, "j_ude_b_l"), Turn(branched, still, 15, ArmR));
        check(MathF.Abs(left - 30) < .05f && MathF.Abs(right - 30) < .05f && MathF.Abs(Turn(branched, still, 15, Chest) - 10) < .05f,
            $"branches of a spread swing each turn the set angle in all ({left:0.##} and {right:0.##} degrees)");
    }

    private static void Slide(Action<bool, string> check)
    {
        var still = Still(false);
        var slid = Applied(still, Swing(new MotionSwing(Count: 1, Tilt: 0, Slide: .05f, Delay: 0), Hips, Waist, Chest));
        check(slid.Get(15, Index(Hips)).Position != still.Get(15, Index(Hips)).Position &&
              slid.Get(15, Index(Waist)) == still.Get(15, Index(Waist)) && slid.Get(15, Index(Chest)) == still.Get(15, Index(Chest)),
            "only the top bone of a chain slides");
    }

    private static void Delay(Action<bool, string> check)
    {
        var loop = Still(true);
        var wrapped = Applied(loop, Swing(new MotionSwing(Count: 1, Delay: 6), Hips, Waist));
        // The first half swing's peak; the second half's is as far the other way.
        var peak = Enumerable.Range(0, 36).MaxBy(i => Local(wrapped, loop, i, Waist));
        check(peak == 21, $"on a loop a trailing bone peaks Delay frames later (at {peak})");
        var still = Still(false);
        var once = Applied(still, Swing(new MotionSwing(Count: 1, Delay: 6), Hips, Waist));
        check(Local(once, still, 5, Waist) < 1e-3f && Local(once, still, 7, Waist) > .01f && Local(once, still, 5, Hips) > .01f,
            "on a layer that doesn't wrap a trailing bone starts Delay frames late");
    }

    // Review: a selection with gaps counts only the selected bones.
    private static void Gaps(Action<bool, string> check)
    {
        var plan = MotionSwings.Plan(Rig(), Swing(new MotionSwing(Tilt: 20, Delay: 4), Hips, Chest), MotionEvaluator.Index(Rig()));
        check(plan.Length == 2 && plan[0] == new SwingBone(Index(Hips), 0, .5f, true, 1) && plan[1] == new SwingBone(Index(Chest), 1, .5f, false, 1),
            "the hips and the chest with the waist between unselected: one Delay step and half the tilt each, the hips slide");
    }

    // Review: more swings than the layer has samples for.
    private static void Dense(Action<bool, string> check)
    {
        var still = Still(false);
        var dense = Applied(still, Swing(new MotionSwing(Count: MotionSwings.MaxCount), Hips) with { End = 4 });
        check(Enumerable.Range(0, 61).All(i => Finite(dense.Get(i, Index(Hips)))) && Near(dense, still, 0) && Near(dense, still, 4),
            "64 swings over 4 frames stay finite and end at rest");
    }

    private static void Shape(Action<bool, string> check)
    {
        check(MathF.Abs(MotionSwings.Wave(.25f, 0) - 1) < 1e-5f && MathF.Abs(MotionSwings.Wave(.125f, 0) - .70711f) < 1e-4f &&
              MotionSwings.Wave(.125f, 1) > .9f && MathF.Abs(MotionSwings.Wave(.75f, .5f) + 1) < 1e-5f && MotionSwings.Wave(0, 1) == 0,
            "the wave is a sine at shape 0 and lingers near its ends at higher shapes");
    }

    private static void Live(Action<bool, string> check)
    {
        var clip = Clip(61, 60, (_, f) => [.. Rig().Bones.Select(bone => bone.Name == Hips ? T(bone.Reference.Position, Q(Vector3.UnitY, f)) : bone.Reference)]);
        var layer = Swing(new MotionSwing(Twist: 5, Slide: .02f), Hips, Waist, Chest);
        var applied = Applied(clip, layer);
        var local = Enumerable.Range(0, clip.Bones).Select(b => clip.Get(20, b)).ToArray();
        var at = MotionEvaluator.ApplyAt(Rig(), local, [layer], 20, 0, 60);
        check(Enumerable.Range(0, clip.Bones).All(b => NearValue(at[b], applied.Get(20, b), 1e-5f)), "a swing at one frame is the swing over the clip there");
    }

    private static void Animated(Action<bool, string> check)
    {
        var animated = MotionSymmetry.Animated(Rig(), [0], [Swing(new MotionSwing(), Hips, Waist)], 1);
        check(animated != null && animated.Contains(Index(Hips)) && animated.Contains(Index(Waist)) && !animated.Contains(Index(Chest)),
            "a symmetry after a swing counts the swung bones as moving");
    }

    private static MotionClip Still(bool loops) => Clip(61, 60, (_, _) => [.. Rig().Bones.Select(bone => bone.Reference)], loops);

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

    private static MotionTransform Model(MotionClip clip, int sample, string bone) =>
        MotionEvaluator.ModelSpace(Rig(), Enumerable.Range(0, clip.Bones).Select(b => clip.Get(sample, b)).ToArray())[Index(bone)];

    /// <summary>How far a bone's model rotation at a sample is from the unswung clip's, in degrees.</summary>
    private static float Turn(MotionClip swung, MotionClip still, int sample, string bone) =>
        Degrees(Model(swung, sample, bone).Rotation, Model(still, sample, bone).Rotation);

    /// <summary>How far a bone's own (local) rotation at a sample is from the unswung clip's, in degrees.</summary>
    private static float Local(MotionClip swung, MotionClip still, int sample, string bone) =>
        Degrees(swung.Get(sample, Index(bone)).Rotation, still.Get(sample, Index(bone)).Rotation);

    private static bool Near(MotionClip a, MotionClip b, int sample) =>
        Enumerable.Range(0, a.Bones).All(bone => NearValue(a.Get(sample, bone), b.Get(sample, bone), 1e-4f));

    private static bool NearValue(MotionTransform a, MotionTransform b, float tolerance) =>
        Vector3.Distance(a.Position, b.Position) <= tolerance &&
        (Quaternion.Dot(a.Rotation, b.Rotation) >= 0 ? a.Rotation - b.Rotation : a.Rotation + b.Rotation).Length() <= tolerance;

    private static bool Finite(MotionTransform value) =>
        float.IsFinite(value.Rotation.X) && float.IsFinite(value.Rotation.Y) && float.IsFinite(value.Rotation.Z) && float.IsFinite(value.Rotation.W) &&
        float.IsFinite(value.Position.X) && float.IsFinite(value.Position.Y) && float.IsFinite(value.Position.Z);

    /// <summary>The signed angle of a rotation about Y, in degrees.</summary>
    private static float AboutY(Quaternion q) => 2 * MathF.Atan2(q.W < 0 ? -q.Y : q.Y, MathF.Abs(q.W)) * 180 / MathF.PI;

    private static Quaternion Q(Vector3 axis, float degrees) => Quaternion.CreateFromAxisAngle(axis, degrees * MathF.PI / 180);

    private static float Degrees(Quaternion a, Quaternion b) =>
        2 * MathF.Acos(Math.Clamp(Math.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))), 0, 1)) * 180 / MathF.PI;

    private static void Validation(Action<bool, string> check, Action<Action, string> reject)
    {
        var swing = Swing(new MotionSwing(), Hips);
        MotionEvaluator.Validate(swing);
        void Refused(MotionLayer layer, string what) => reject(() => MotionEvaluator.Validate(layer), what);
        Refused(Swing(new MotionSwing(Count: 0), Hips), "a swing with no swings is refused");
        Refused(Swing(new MotionSwing(Count: MotionSwings.MaxCount + 1), Hips), "a swing with too many swings is refused");
        Refused(Swing(new MotionSwing(Direction: 181), Hips), "a swing direction past 180 degrees is refused");
        Refused(Swing(new MotionSwing(Tilt: -1), Hips), "a negative tilt is refused");
        Refused(Swing(new MotionSwing(Tilt: 91), Hips), "a tilt past 90 degrees is refused");
        Refused(Swing(new MotionSwing(Twist: 91), Hips), "a twist past 90 degrees is refused");
        Refused(Swing(new MotionSwing(Slide: .51f), Hips), "a slide past 50 cm is refused");
        Refused(Swing(new MotionSwing(Delay: 31), Hips), "a delay past 30 frames is refused");
        Refused(Swing(new MotionSwing(Shape: 1.1f), Hips), "a shape past 100% is refused");
        Refused(Swing(new MotionSwing(Tilt: float.NaN), Hips), "a swing with a NaN tilt is refused");
        Refused(swing with { Swing = null }, "a swing layer without its swing is refused");
        Refused(new MotionLayer("m1", MotionLayerKind.Offset, "Offset 1", 0, 60, 0, 0, 1, PoseComponents.All, MotionInterpolation.Linear,
            Swing: new MotionSwing()), "a swing on an offset layer is refused");
        Refused(swing with { Keys = [new MotionKey(0, ImmutableDictionary<string, BoneTransform>.Empty.Add(Hips, T(Vector3.Zero)))] },
            "a swing layer with keys is refused");
        Refused(swing with { Pins = [new MotionPin(Hips)] }, "a swing layer with pins is refused");
        Refused(swing with { Window = 10 }, "a swing layer with a window is refused");
        MotionEvaluator.Validate(swing with { Amount = MotionEvaluator.MaxAmount });

        check(MotionEvaluator.Active(swing), "a swing with bones and a tilt is active");
        check(!MotionEvaluator.Active(swing with { Bones = [] }), "a swing with no bones does nothing");
        check(!MotionEvaluator.Active(swing with { Amount = 0 }), "a swing at 0% does nothing");
        check(!MotionEvaluator.Active(Swing(new MotionSwing(Tilt: 0), Hips)), "a swing with every size 0 does nothing");
        check(!MotionEvaluator.Active(swing with { Components = PoseComponents.Position }), "a tilt with Turn unticked does nothing");
        check(!MotionEvaluator.Active(Swing(new MotionSwing(Tilt: 0, Slide: .05f), Hips) with { Components = PoseComponents.Rotation }),
            "a slide with Move unticked does nothing");
        check(MotionEvaluator.Active(Swing(new MotionSwing(Tilt: 0, Slide: .05f), Hips) with { Components = PoseComponents.Position }),
            "a slide with Move ticked is active");
    }

    private static void Journals(Action<bool, string> check)
    {
        var temp = Path.Combine(Path.GetTempPath(), "ie-swing-layer-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var resource = new AnimationResource("chara/human/c0801/animation/a0001/bt_common/emote/dance01.pap", "dance01.pap", "hash");
            var clip = new AnimationClip(resource.GamePath, "dance", 0, 0, 42, "chara/test.sklb");
            var capture = new AnimationCapture("clip", 1, 1, Guid.NewGuid(), "Test", "Idle", clip, null, [resource.GamePath], [resource], DateTime.UtcNow, true);
            var settings = new MotionSwing(5, -45, 12, 7, .03f, 4, .4f, false, false);
            var swing = Swing(settings, Hips, Waist);
            var smooth = SmoothMotionFixture.Smooth(25, .5f, 0, 60) with { Key = "m2" };
            var request = new AnimationBakeRequest(Guid.NewGuid(), capture, AnimationDestination.NewMod, "Motion", false,
                AnimationOperation.EditTimeline, Timeline: new AnimationTimelineEdit(true, 0, null, [swing, smooth]));
            new AnimationJournalStore(temp).Save(new AnimationEditJournal { Id = request.Id, Request = AnimationCommitService.RecoveryRequest(request) });
            var loaded = new AnimationJournalStore(temp).Load().Single().Request.Timeline!.Motion;
            check(loaded.Length == 2 && MotionEdits.Same(loaded[0], swing) && MotionEdits.Same(loaded[1], smooth) && loaded[0].Swing == settings &&
                  loaded[1].Window == 25 && loaded[0].Window == 0 && loaded[1].Swing == null,
                "a journal keeps a swing's settings and a smooth's window");

            var path = Path.Combine(temp, "AnimationEdits", request.Id.ToString("N"), "journal.json");
            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            check(json["Request"]!["Timeline"]!["Motion"]![0]!.AsObject().Remove("Swing"), "a journal stores a layer's swing as Swing");
            File.WriteAllText(path, json.ToJsonString());
            var older = new AnimationJournalStore(temp).Load().Single().Request.Timeline!.Motion[0];
            var refused = false;
            try { MotionEvaluator.Validate(older); }
            catch (InvalidDataException) { refused = true; }
            check(older.Swing == null && refused && !MotionEvaluator.Active(older), "a swing layer without Swing loads with none and is refused");
        }
        finally { Directory.Delete(temp, true); }

        var a = Swing(new MotionSwing(), Hips);
        check(MotionEdits.Same(a, a with { Swing = new MotionSwing() }) && !MotionEdits.Same(a, a with { Swing = new MotionSwing(Count: 3) }) &&
              !MotionEdits.Same(a, a with { Swing = new MotionSwing(Direction: 90) }) && !MotionEdits.Same(a, a with { Swing = new MotionSwing(TwinsOpposite: false) }) &&
              !MotionEdits.Same(a, a with { Swing = null }) &&
              !MotionEdits.Same(SmoothMotionFixture.Smooth(10, 1, 0, 60), SmoothMotionFixture.Smooth(11, 1, 0, 60)),
            "layers differ by their swing's settings and a smooth's window");
    }

    /// <summary>
    /// The root, the hips at 1 m, two spine bones above them, and on the chest an arm each side with its forearm above it,
    /// so every child sits at +Y from its parent.
    /// </summary>
    internal static SkeletonDescription Rig() => new("swing-rig", "swing-rig-fingerprint",
    [
        new("n_root", -1, 0, T(Vector3.Zero)), new(Hips, 0, 0, T(Vector3.UnitY)), new(Waist, 1, 0, T(new Vector3(0, .1f, 0))),
        new(Chest, 2, 0, T(new Vector3(0, .1f, 0))), new(ArmL, 3, 0, T(new Vector3(.1f, .1f, 0))), new(ArmR, 3, 0, T(new Vector3(-.1f, .1f, 0))),
        new("j_ude_b_l", 4, 0, T(new Vector3(0, .2f, 0))), new("j_ude_b_r", 5, 0, T(new Vector3(0, .2f, 0))),
    ], [], [], []);

    private static MotionLayer Swing(MotionSwing swing, params string[] bones) =>
        new("m1", MotionLayerKind.Swing, "Swing 1", 0, 60, 0, 0, 1, PoseComponents.Rotation | PoseComponents.Position, MotionInterpolation.Smooth,
            Bones: [.. bones], Swing: swing);

    private static BoneTransform T(Vector3 position, Quaternion? rotation = null) => new(position, rotation ?? Quaternion.Identity, Vector3.One);
}
