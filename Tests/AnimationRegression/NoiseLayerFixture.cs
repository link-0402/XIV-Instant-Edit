using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json.Nodes;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui.Timeline;

/// <summary>
/// Noise layers: smooth random wobble from a fixed hash, the same every run, seamless round a loop and at rest at both
/// ends otherwise, never past its sizes, sliding only the top bone of a chain; their validation and journals.
/// </summary>
internal static class NoiseLayerFixture
{
    private const string Hips = "j_kosi", Waist = "j_sebo_a", Chest = "j_sebo_b";

    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Validation(check, reject);
        Journals(check);
        Hash(check);
        Same(check);
        Seam(check);
        Rest(check);
        Peak(check);
        Speed(check);
        Slide(check);
        Live(check);
        Animated(check);
    }

    private static void Validation(Action<bool, string> check, Action<Action, string> reject)
    {
        var noise = Noise(new MotionNoise(), Hips);
        MotionEvaluator.Validate(noise);
        void Refused(MotionLayer layer, string what) => reject(() => MotionEvaluator.Validate(layer), what);
        Refused(Noise(new MotionNoise(Tilt: -1), Hips), "a negative noise tilt is refused");
        Refused(Noise(new MotionNoise(Tilt: 91), Hips), "a noise tilt past 90 degrees is refused");
        Refused(Noise(new MotionNoise(Twist: 91), Hips), "a noise twist past 90 degrees is refused");
        Refused(Noise(new MotionNoise(Slide: .51f), Hips), "a noise slide past 50 cm is refused");
        Refused(Noise(new MotionNoise(Speed: .09f), Hips), "a noise speed under 0.1 is refused");
        Refused(Noise(new MotionNoise(Speed: 10.1f), Hips), "a noise speed past 10 is refused");
        Refused(Noise(new MotionNoise(Seed: 0), Hips), "a seed of 0 is refused");
        Refused(Noise(new MotionNoise(Seed: MotionNoises.MaxSeed + 1), Hips), "a seed past 9999 is refused");
        Refused(Noise(new MotionNoise(Twist: float.NaN), Hips), "a noise with a NaN twist is refused");
        Refused(noise with { Noise = null }, "a noise layer without its noise is refused");
        Refused(new MotionLayer("m1", MotionLayerKind.Offset, "Offset 1", 0, 60, 0, 0, 1, PoseComponents.All, MotionInterpolation.Linear,
            Noise: new MotionNoise()), "noise on an offset layer is refused");
        Refused(noise with { Keys = [new MotionKey(0, ImmutableDictionary<string, BoneTransform>.Empty.Add(Hips, T(Vector3.Zero)))] },
            "a noise layer with keys is refused");
        Refused(noise with { Pins = [new MotionPin(Hips)] }, "a noise layer with pins is refused");
        Refused(noise with { Window = 10 }, "a noise layer with a window is refused");
        Refused(noise with { Swing = new MotionSwing() }, "a noise layer with a swing is refused");
        MotionEvaluator.Validate(noise with { Amount = MotionEvaluator.MaxAmount });
        check(!MotionEvaluator.BlendsAtMostFully(MotionLayerKind.Noise), "a noise's strength goes past 100%");

        check(MotionEvaluator.Active(noise), "a noise with bones and a tilt is active");
        check(!MotionEvaluator.Active(noise with { Bones = [] }), "a noise with no bones does nothing");
        check(!MotionEvaluator.Active(noise with { Amount = 0 }), "a noise at 0% does nothing");
        check(!MotionEvaluator.Active(Noise(new MotionNoise(Tilt: 0), Hips)), "a noise with every size 0 does nothing");
        check(!MotionEvaluator.Active(noise with { Components = PoseComponents.Position }), "a noise tilt with Turn unticked does nothing");
        check(MotionEvaluator.Active(Noise(new MotionNoise(Tilt: 0, Slide: .05f), Hips) with { Components = PoseComponents.Position }) &&
              !MotionEvaluator.Active(Noise(new MotionNoise(Tilt: 0, Slide: .05f), Hips) with { Components = PoseComponents.Rotation }),
            "a noise slide needs Move ticked");
    }

    private static void Journals(Action<bool, string> check)
    {
        var temp = Path.Combine(Path.GetTempPath(), "ie-noise-layer-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var resource = new AnimationResource("chara/human/c0801/animation/a0001/bt_common/emote/dance01.pap", "dance01.pap", "hash");
            var clip = new AnimationClip(resource.GamePath, "dance", 0, 0, 42, "chara/test.sklb");
            var capture = new AnimationCapture("clip", 1, 1, Guid.NewGuid(), "Test", "Idle", clip, null, [resource.GamePath], [resource], DateTime.UtcNow, true);
            var settings = new MotionNoise(12, 7, .03f, 2.5f, 4321);
            var noise = Noise(settings, Hips, Waist) with { Weights = [new MotionWeight(10, .5f)] };
            var request = new AnimationBakeRequest(Guid.NewGuid(), capture, AnimationDestination.NewMod, "Motion", false,
                AnimationOperation.EditTimeline, Timeline: new AnimationTimelineEdit(true, 0, null, [noise]));
            new AnimationJournalStore(temp).Save(new AnimationEditJournal { Id = request.Id, Request = AnimationCommitService.RecoveryRequest(request) });
            var loaded = new AnimationJournalStore(temp).Load().Single().Request.Timeline!.Motion;
            check(loaded.Length == 1 && MotionEdits.Same(loaded[0], noise) && loaded[0].Noise == settings && loaded[0].Swing == null,
                "a journal keeps a noise's settings");

            var path = Path.Combine(temp, "AnimationEdits", request.Id.ToString("N"), "journal.json");
            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            check(json["Request"]!["Timeline"]!["Motion"]![0]!.AsObject().Remove("Noise"), "a journal stores a layer's noise as Noise");
            File.WriteAllText(path, json.ToJsonString());
            var older = new AnimationJournalStore(temp).Load().Single().Request.Timeline!.Motion[0];
            var refused = false;
            try { MotionEvaluator.Validate(older); }
            catch (InvalidDataException) { refused = true; }
            check(older.Noise == null && refused && !MotionEvaluator.Active(older), "a noise layer without Noise loads with none and is refused");
        }
        finally { Directory.Delete(temp, true); }

        var a = Noise(new MotionNoise(), Hips);
        check(MotionEdits.Same(a, a with { Noise = new MotionNoise() }) && !MotionEdits.Same(a, a with { Noise = new MotionNoise(Seed: 2) }) &&
              !MotionEdits.Same(a, a with { Noise = new MotionNoise(Speed: 2) }) && !MotionEdits.Same(a, a with { Noise = null }),
            "layers differ by their noise's settings");
    }

    private static void Hash(Action<bool, string> check)
    {
        // Pinned: the wobble must be the same in every session and on every runtime.
        var value = MotionNoises.Random(1, Hips, 0, 0);
        check(MathF.Abs(value - PinnedRandom) < 1e-6f, $"the noise hash is fixed ({value:R})");
        var values = Enumerable.Range(0, 2000).Select(i => MotionNoises.Random(7, Waist, i % 5, i / 5)).ToArray();
        check(values.All(v => v is >= -1 and <= 1) && values.Min() < -.95f && values.Max() > .95f && MathF.Abs(values.Average()) < .05f,
            "noise values spread over -1 to 1");
        check(MotionNoises.Random(1, Hips, 0, 0) != MotionNoises.Random(2, Hips, 0, 0) && MotionNoises.Random(1, Hips, 0, 0) != MotionNoises.Random(1, Waist, 0, 0) &&
              MotionNoises.Random(1, Hips, 0, 0) != MotionNoises.Random(1, Hips, 1, 0) && MotionNoises.Random(1, Hips, 0, 0) != MotionNoises.Random(1, Hips, 0, 1),
            "the seed, bone, stream and knot each change the value");
        var seeds = new[] { 1, 2, 500, MotionNoises.MaxSeed };
        check(seeds.All(s => MotionNoises.NewSeed(s) is >= 1 and <= MotionNoises.MaxSeed && MotionNoises.NewSeed(s) != s && MotionNoises.NewSeed(s) == MotionNoises.NewSeed(s)),
            "New picks another seed in range");
    }

    // MotionNoises.Random(1, "j_kosi", 0, 0) as first written; a change here changes every saved noise layer's motion.
    private const float PinnedRandom = -0.08270985f;

    private static void Same(Action<bool, string> check)
    {
        var still = Still(false);
        var layer = Noise(new MotionNoise(Tilt: 10, Twist: 5, Slide: .02f, Speed: 2), Hips, Waist, Chest);
        var (first, second) = (Applied(still, layer), Applied(still, layer));
        check(Enumerable.Range(0, still.Samples).All(i => Enumerable.Range(0, still.Bones).All(b => first.Get(i, b) == second.Get(i, b))),
            "the same noise gives the same motion twice");
        var other = Applied(still, layer with { Noise = layer.Noise! with { Seed = 2 } });
        check(!Near(first, other, 20) && !Near(first, still, 20), "another seed gives another wobble");
    }

    private static void Seam(Action<bool, string> check)
    {
        var loop = Still(true);
        var noisy = Applied(loop, Noise(new MotionNoise(Tilt: 15, Twist: 10, Slide: .03f, Speed: 3), Hips, Waist, Chest));
        check(Enumerable.Range(0, noisy.Bones).All(b => NearValue(noisy.Get(0, b), noisy.Get(60, b), 1e-4f)) && !Near(noisy, loop, 0) && !Near(noisy, loop, 30),
            "a noise covering a loop has the same first and last sample, away from rest");
        // A slow noise on a short loop still wobbles: at least two knots round it.
        var slow = Applied(loop, Noise(new MotionNoise(Tilt: 15, Speed: .1f), Hips));
        check(Enumerable.Range(0, 61).Select(i => Turn(slow, loop, i, Hips)).Max() - Enumerable.Range(0, 61).Select(i => Turn(slow, loop, i, Hips)).Min() > 1,
            "a slow noise round a short loop still moves");
    }

    private static void Rest(Action<bool, string> check)
    {
        var still = Still(false);
        var layer = Noise(new MotionNoise(Tilt: 15, Twist: 10, Slide: .03f, Speed: 3), Hips, Waist);
        var noisy = Applied(still, layer);
        check(Near(noisy, still, 0) && Near(noisy, still, 60) && !Near(noisy, still, 30), "a noise that doesn't wrap starts and ends at rest");
        var past = Applied(still, layer with { End = 61 });
        check(Near(past, still, 60), "a noise reaching a frame past the clip's end is at rest on its last sample");
        var slow = Applied(still, Noise(new MotionNoise(Tilt: 15, Speed: .1f), Hips));
        check(!Near(slow, still, 30), "a slow noise over a short layer still wobbles once");
        var partway = Applied(Still(true), layer with { Start = 10, End = 50 });
        check(Near(partway, Still(true), 10) && Near(partway, Still(true), 50), "a noise partway through a loop starts and ends at rest");
    }

    private static void Peak(Action<bool, string> check)
    {
        var still = Clip(301, 300, (_, _) => [.. SwingLayerFixture.Rig().Bones.Select(bone => bone.Reference)]);
        var tilted = Applied(still, Noise(new MotionNoise(Tilt: 20, Speed: 4), Hips) with { End = 300, Amount = 1.5f });
        var tilt = Enumerable.Range(0, 301).Max(i => Turn(tilted, still, i, Hips));
        check(tilt <= 30.01f && tilt > 15, $"a noise never leans past Tilt times its strength ({tilt:0.##} of 30 degrees)");
        var twisted = Applied(still, Noise(new MotionNoise(Tilt: 0, Twist: 12, Speed: 4), Hips) with { End = 300 });
        var twist = Enumerable.Range(0, 301).Max(i => MathF.Abs(AboutY(Model(twisted, i, Hips).Rotation)));
        check(twist <= 12.01f && twist > 6 && Enumerable.Range(0, 301).All(i => Turn(twisted, still, i, Hips) - MathF.Abs(AboutY(Model(twisted, i, Hips).Rotation)) < .01f),
            $"a twist only turns about the up axis, at most Twist ({twist:0.##} of 12 degrees)");
        var slid = Applied(still, Noise(new MotionNoise(Tilt: 0, Slide: .05f, Speed: 4), Hips) with { End = 300 });
        var moves = Enumerable.Range(0, 301).Select(i => Model(slid, i, Hips).Position - Model(still, i, Hips).Position).ToArray();
        check(moves.Max(m => m.Length()) <= .0501f && moves.Max(m => m.Length()) > .025f && moves.All(m => MathF.Abs(m.Y) < 1e-5f),
            $"a slide stays in the floor plane, at most Slide ({moves.Max(m => m.Length()) * 100:0.##} of 5 cm)");
    }

    private static void Speed(Action<bool, string> check)
    {
        var layer = Noise(new MotionNoise(Speed: 1), Hips) with { End = 900 };
        int Crossings(float speed, int stream) =>
            Enumerable.Range(1, 899).Count(f => MathF.Sign(MotionNoises.Value(layer with { Noise = new MotionNoise(Speed: speed) }, Hips, stream, f - 1, 0)) *
                                                 MathF.Sign(MotionNoises.Value(layer with { Noise = new MotionNoise(Speed: speed) }, Hips, stream, f, 0)) < 0);
        var (slow, fast) = (Enumerable.Range(0, 5).Sum(s => Crossings(1, s)), Enumerable.Range(0, 5).Sum(s => Crossings(4, s)));
        // 30 seconds, 5 streams: about half the knots change sign from the one before.
        check(fast > 2 * slow && slow is > 30 and < 150 && fast is > 150 && fast < 600, $"Speed sets about how often it changes ({slow} and {fast} crossings)");
    }

    private static void Slide(Action<bool, string> check)
    {
        var still = Still(false);
        var slid = Applied(still, Noise(new MotionNoise(Tilt: 0, Slide: .05f, Speed: 3), Hips, Waist, Chest));
        check(slid.Get(30, Index(Hips)).Position != still.Get(30, Index(Hips)).Position &&
              Enumerable.Range(0, 61).All(i => slid.Get(i, Index(Waist)) == still.Get(i, Index(Waist)) && slid.Get(i, Index(Chest)) == still.Get(i, Index(Chest))),
            "only the top bone of a chain slides");
    }

    private static void Live(Action<bool, string> check)
    {
        var rig = SwingLayerFixture.Rig();
        var clip = Clip(61, 60, (_, f) => [.. rig.Bones.Select(bone => bone.Name == Hips ? T(bone.Reference.Position, Q(Vector3.UnitY, f)) : bone.Reference)]);
        var layer = Noise(new MotionNoise(Tilt: 8, Twist: 5, Slide: .02f, Speed: 2), Hips, Waist, Chest) with { Weights = [new MotionWeight(0, .2f), new MotionWeight(60, 1)] };
        var applied = Applied(clip, layer);
        foreach (var sample in new[] { 20, 45 })
        {
            var local = Enumerable.Range(0, clip.Bones).Select(b => clip.Get(sample, b)).ToArray();
            var at = MotionEvaluator.ApplyAt(rig, local, [layer], sample, 0, 60);
            check(Enumerable.Range(0, clip.Bones).All(b => NearValue(at[b], applied.Get(sample, b), 1e-5f)), $"a noise at frame {sample} is the noise over the clip there");
        }
    }

    private static void Animated(Action<bool, string> check)
    {
        var animated = MotionSymmetry.Animated(SwingLayerFixture.Rig(), [0], [Noise(new MotionNoise(), Hips, Waist)], 1);
        check(animated != null && animated.Contains(Index(Hips)) && animated.Contains(Index(Waist)) && !animated.Contains(Index(Chest)),
            "a symmetry after a noise counts the wobbled bones as moving");
    }

    private static MotionClip Still(bool loops) => Clip(61, 60, (_, _) => [.. SwingLayerFixture.Rig().Bones.Select(bone => bone.Reference)], loops);

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

    private static MotionTransform Model(MotionClip clip, int sample, string bone) =>
        MotionEvaluator.ModelSpace(SwingLayerFixture.Rig(), Enumerable.Range(0, clip.Bones).Select(b => clip.Get(sample, b)).ToArray())[Index(bone)];

    /// <summary>How far a bone's model rotation at a sample is from the still clip's, in degrees.</summary>
    private static float Turn(MotionClip noisy, MotionClip still, int sample, string bone) =>
        Degrees(Model(noisy, sample, bone).Rotation, Model(still, sample, bone).Rotation);

    private static bool Near(MotionClip a, MotionClip b, int sample) =>
        Enumerable.Range(0, a.Bones).All(bone => NearValue(a.Get(sample, bone), b.Get(sample, bone), 1e-4f));

    private static bool NearValue(MotionTransform a, MotionTransform b, float tolerance) =>
        Vector3.Distance(a.Position, b.Position) <= tolerance &&
        (Quaternion.Dot(a.Rotation, b.Rotation) >= 0 ? a.Rotation - b.Rotation : a.Rotation + b.Rotation).Length() <= tolerance;

    /// <summary>The signed angle of a rotation about Y, in degrees.</summary>
    private static float AboutY(Quaternion q) => 2 * MathF.Atan2(q.W < 0 ? -q.Y : q.Y, MathF.Abs(q.W)) * 180 / MathF.PI;

    private static Quaternion Q(Vector3 axis, float degrees) => Quaternion.CreateFromAxisAngle(axis, degrees * MathF.PI / 180);

    private static float Degrees(Quaternion a, Quaternion b) =>
        2 * MathF.Acos(Math.Clamp(Math.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))), 0, 1)) * 180 / MathF.PI;

    private static MotionLayer Noise(MotionNoise noise, params string[] bones) =>
        new("m1", MotionLayerKind.Noise, "Noise 1", 0, 60, 0, 0, 1, PoseComponents.Rotation | PoseComponents.Position, MotionInterpolation.Smooth,
            Bones: [.. bones], Noise: noise);

    private static BoneTransform T(Vector3 position, Quaternion? rotation = null) => new(position, rotation ?? Quaternion.Identity, Vector3.One);
}
