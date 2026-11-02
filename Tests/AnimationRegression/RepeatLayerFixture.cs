using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json.Nodes;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui.Timeline;

/// <summary>
/// Repeat layers: a stretch of the motion played again and again over the layer, read from the motion before it, cross-faded
/// into each next repeat, limited to its bones and blended by its amount; their validation and journals.
/// </summary>
internal static class RepeatLayerFixture
{
    private const string Hips = "j_kosi", Waist = "j_sebo_a";

    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Validation(check, reject);
        Journals(check);
        Plays(check);
        NoJump(check);
        Limit(check);
        Half(check);
        Overlap(check);
        PastEnd(check);
        Pose(check);
    }

    private static void Validation(Action<bool, string> check, Action<Action, string> reject)
    {
        var repeat = Repeat(new MotionRepeat(0, 10));
        MotionEvaluator.Validate(repeat);
        void Refused(MotionLayer layer, string what) => reject(() => MotionEvaluator.Validate(layer), what);
        Refused(Repeat(new MotionRepeat(-1, 10)), "a repeat starting before frame 0 is refused");
        Refused(Repeat(new MotionRepeat(10, 11)), "a repeat one frame long is refused");
        Refused(Repeat(new MotionRepeat(0, TimelineDocument.MaxFrames + 1)), "a repeat past the most frames is refused");
        Refused(Repeat(new MotionRepeat(0, 10, Blend: 6)), "a repeat blending over more than half its source is refused");
        Refused(Repeat(new MotionRepeat(0, 10, Blend: -1)), "a negative repeat blend is refused");
        Refused(new MotionLayer("m1", MotionLayerKind.Offset, "Offset 1", 0, 60, 0, 0, 1, PoseComponents.All, MotionInterpolation.Linear,
            Repeat: new MotionRepeat(0, 10)), "a repeat on an offset layer is refused");
        Refused(repeat with { Repeat = null }, "a repeat layer without its repeat is refused");
        Refused(repeat with { Keys = [new MotionKey(0, ImmutableDictionary<string, BoneTransform>.Empty.Add(Hips, T(Vector3.Zero)))] },
            "a repeat layer with keys is refused");
        Refused(repeat with { Pins = [new MotionPin(Hips)] }, "a repeat layer with pins is refused");
        Refused(repeat with { Amount = 1.5f }, "a repeat past full strength is refused");
        MotionEvaluator.Validate(Repeat(new MotionRepeat(0, 10, Blend: 5)));
        check(MotionEvaluator.BlendsAtMostFully(MotionLayerKind.Repeat), "a repeat blends at most fully");

        check(MotionEvaluator.Active(repeat) && MotionEvaluator.Active(repeat with { Bones = [Hips] }), "a repeat with or without bones is active");
        check(!MotionEvaluator.Active(repeat with { Amount = 0 }), "a repeat at 0% does nothing");
        check(!MotionEvaluator.Active(repeat with { Components = PoseComponents.None }), "a repeat with no part ticked does nothing");
        check(MathF.Abs(MotionRepeats.Count(repeat with { Start = 20, End = 55 }) - 3.5f) < 1e-5f && MotionRepeats.Length(new MotionRepeat(5, 25)) == 20,
            "a 35-frame layer repeating a 10-frame source plays it 3.5 times");
    }

    private static void Journals(Action<bool, string> check)
    {
        var temp = Path.Combine(Path.GetTempPath(), "ie-repeat-layer-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var resource = new AnimationResource("chara/human/c0801/animation/a0001/bt_common/emote/dance01.pap", "dance01.pap", "hash");
            var clip = new AnimationClip(resource.GamePath, "dance", 0, 0, 42, "chara/test.sklb");
            var capture = new AnimationCapture("clip", 1, 1, Guid.NewGuid(), "Test", "Idle", clip, null, [resource.GamePath], [resource], DateTime.UtcNow, true);
            var settings = new MotionRepeat(12, 40, 6);
            var repeat = Repeat(settings, Hips, Waist) with { Weights = [new MotionWeight(10, .5f)] };
            var offset = new MotionLayer("m2", MotionLayerKind.Offset, "Offset 1", 0, 60, 0, 0, 1, PoseComponents.All, MotionInterpolation.Linear);
            var request = new AnimationBakeRequest(Guid.NewGuid(), capture, AnimationDestination.NewMod, "Motion", false,
                AnimationOperation.EditTimeline, Timeline: new AnimationTimelineEdit(true, 0, null, [repeat, offset]));
            new AnimationJournalStore(temp).Save(new AnimationEditJournal { Id = request.Id, Request = AnimationCommitService.RecoveryRequest(request) });
            var loaded = new AnimationJournalStore(temp).Load().Single().Request.Timeline!.Motion;
            check(loaded.Length == 2 && MotionEdits.Same(loaded[0], repeat) && loaded[0].Repeat == settings && loaded[0].Noise == null &&
                  MotionEdits.Same(loaded[1], offset), "a journal keeps a repeat's settings");

            var path = Path.Combine(temp, "AnimationEdits", request.Id.ToString("N"), "journal.json");
            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var motion = json["Request"]!["Timeline"]!["Motion"]!;
            check(motion[0]!.AsObject().Remove("Repeat"), "a journal stores a layer's repeat as Repeat");
            motion[1]!.AsObject().Remove("Repeat");
            File.WriteAllText(path, json.ToJsonString());
            var older = new AnimationJournalStore(temp).Load().Single().Request.Timeline!.Motion;
            var refused = false;
            try { MotionEvaluator.Validate(older[0]); }
            catch (InvalidDataException) { refused = true; }
            check(older[0].Repeat == null && refused && !MotionEvaluator.Active(older[0]), "a repeat layer without Repeat loads with none and is refused");
            check(MotionEdits.Same(older[1], offset), "a journal written before repeats loads its other layers unchanged");
        }
        finally { Directory.Delete(temp, true); }

        var a = Repeat(new MotionRepeat(0, 10));
        check(MotionEdits.Same(a, a with { Repeat = new MotionRepeat(0, 10) }) && !MotionEdits.Same(a, a with { Repeat = new MotionRepeat(0, 10, 2) }) &&
              !MotionEdits.Same(a, a with { Repeat = new MotionRepeat(1, 10) }) && !MotionEdits.Same(a, a with { Repeat = null }),
            "layers differ by their repeat's settings");
    }

    private static void Plays(Action<bool, string> check)
    {
        var ramp = Ramp();
        var played = Applied(ramp, Repeat(new MotionRepeat(0, 10, Blend: 0)) with { Start = 20, End = 55 });
        check(new[] { 20, 30, 40, 50 }.All(f => Degrees(played.Get(f, Index(Hips)).Rotation, ramp.Get(0, Index(Hips)).Rotation) < .01f) &&
              Degrees(played.Get(25, Index(Hips)).Rotation, ramp.Get(5, Index(Hips)).Rotation) < .01f,
            "a repeat plays its source from its first frame each time round");
        check(Near(played, ramp, 19) && Near(played, ramp, 56), "a repeat leaves the motion outside it alone");
    }

    private static void NoJump(Action<bool, string> check)
    {
        // A small wave on a steady turn: a 10-frame stretch of it doesn't end where it starts.
        var drift = Clip(61, 60, (_, f) => [.. SwingLayerFixture.Rig().Bones.Select(bone => bone.Name == Hips
            ? T(bone.Reference.Position, Q(Vector3.UnitY, 2 * MathF.Sin(2 * MathF.PI * f / 10) + .5f * f)) : bone.Reference)]);
        var most = Enumerable.Range(1, 60).Max(f => Step(drift, f));
        var cut = Applied(drift, Repeat(new MotionRepeat(10, 20, Blend: 0)) with { Start = 20, End = 55 });
        var blended = Applied(drift, Repeat(new MotionRepeat(10, 20, Blend: 4)) with { Start = 20, End = 55 });
        var (jump, smooth) = (Enumerable.Range(21, 35).Max(f => Step(cut, f)), Enumerable.Range(21, 35).Max(f => Step(blended, f)));
        check(jump > most + .5f && smooth <= most + .5f,
            $"a blend takes the jump out of each wrap ({jump:0.##} degrees cut, {smooth:0.##} blended, {most:0.##} in the motion)");
        check(Degrees(blended.Get(30, Index(Hips)).Rotation, drift.Get(10, Index(Hips)).Rotation) < .01f, "a blended repeat still lands on its first frame");
    }

    private static void Limit(Action<bool, string> check)
    {
        var ramp = Ramp();
        var limited = Applied(ramp, Repeat(new MotionRepeat(0, 10, Blend: 0), Hips) with { Start = 20, End = 55 });
        check(Enumerable.Range(0, 61).All(i => limited.Get(i, Index(Waist)) == ramp.Get(i, Index(Waist))) && !Near(limited, ramp, 25),
            "a repeat limited to the hips leaves the waist's own motion");
    }

    private static void Half(Action<bool, string> check)
    {
        var ramp = Ramp();
        var half = Applied(ramp, Repeat(new MotionRepeat(0, 10, Blend: 0)) with { Start = 20, End = 55, Amount = .5f });
        var expected = MotionEvaluator.Slerp(ramp.Get(25, Index(Hips)).Rotation, ramp.Get(5, Index(Hips)).Rotation, .5f);
        check(Degrees(half.Get(25, Index(Hips)).Rotation, expected) < .01f, "a repeat at 50% lands halfway between the motion and the repeat");
    }

    private static void Overlap(Action<bool, string> check)
    {
        var ramp = Ramp();
        var overlap = Applied(ramp, Repeat(new MotionRepeat(25, 35, Blend: 0)) with { Start = 20, End = 55 });
        check(Enumerable.Range(20, 36).All(f => Degrees(overlap.Get(f, Index(Hips)).Rotation, ramp.Get(25 + (f - 20) % 10, Index(Hips)).Rotation) < .01f),
            "a source inside the layer reads the motion before the layer");
    }

    private static void PastEnd(Action<bool, string> check)
    {
        var ramp = Ramp();
        MotionClip? past = null;
        try { past = Applied(ramp, Repeat(new MotionRepeat(50, 90))); }
        catch (Exception) { }
        check(past != null && Enumerable.Range(10, 26).All(f => Degrees(past.Get(f, Index(Hips)).Rotation, ramp.Get(60, Index(Hips)).Rotation) < .01f),
            "a source past the clip's end holds its last sample");
        var tiny = Clip(3, 2, (_, f) => [.. SwingLayerFixture.Rig().Bones.Select(bone => bone.Name == Hips ? T(bone.Reference.Position, Q(Vector3.UnitY, f)) : bone.Reference)]);
        MotionClip? shortened = null;
        try { shortened = Applied(tiny, Repeat(new MotionRepeat(0, 30)) with { End = 2 }); }
        catch (Exception) { }
        check(shortened != null && Enumerable.Range(0, 3).All(i => Finite(shortened.Get(i, Index(Hips)))), "a repeat on a clip shorter than its source stays finite");
    }

    private static void Pose(Action<bool, string> check)
    {
        var rig = SwingLayerFixture.Rig();
        var ramp = Ramp();
        var layer = Repeat(new MotionRepeat(0, 10)) with { Start = 20, End = 55 };
        var local = Enumerable.Range(0, ramp.Bones).Select(b => ramp.Get(25, b)).ToArray();
        var at = MotionEvaluator.ApplyAt(rig, local, [layer], 25, 0, 60);
        check(Enumerable.Range(0, ramp.Bones).All(b => at[b] == local[b]) && BonePosing.TakesWholeClip([layer]),
            "one pose leaves a repeat out, so the Bones window starts from the bake");
        var animated = MotionSymmetry.Animated(rig, [0], [layer with { Bones = [Hips, Waist] }], 1);
        check(animated != null && animated.Contains(Index(Hips)) && animated.Contains(Index(Waist)), "a symmetry after a repeat counts its bones as moving");
    }

    /// <summary>The hips turn 1 degree a frame and the waist half a degree about another axis, over 60 frames.</summary>
    private static MotionClip Ramp() => Clip(61, 60, (_, f) => [.. SwingLayerFixture.Rig().Bones.Select(bone => bone.Name switch
    {
        Hips => T(bone.Reference.Position, Q(Vector3.UnitY, f)),
        Waist => T(bone.Reference.Position, Q(Vector3.UnitX, f / 2)),
        _ => bone.Reference,
    })]);

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

    /// <summary>How far the hips turn from the sample before <paramref name="sample"/> to it, in degrees.</summary>
    private static float Step(MotionClip clip, int sample) =>
        Degrees(clip.Get(sample - 1, Index(Hips)).Rotation, clip.Get(sample, Index(Hips)).Rotation);

    private static bool Near(MotionClip a, MotionClip b, int sample) =>
        Enumerable.Range(0, a.Bones).All(bone => NearValue(a.Get(sample, bone), b.Get(sample, bone), 1e-4f));

    private static bool NearValue(MotionTransform a, MotionTransform b, float tolerance) =>
        Vector3.Distance(a.Position, b.Position) <= tolerance &&
        (Quaternion.Dot(a.Rotation, b.Rotation) >= 0 ? a.Rotation - b.Rotation : a.Rotation + b.Rotation).Length() <= tolerance;

    private static bool Finite(MotionTransform value) =>
        float.IsFinite(value.Rotation.X) && float.IsFinite(value.Rotation.Y) && float.IsFinite(value.Rotation.Z) && float.IsFinite(value.Rotation.W) &&
        float.IsFinite(value.Position.X) && float.IsFinite(value.Position.Y) && float.IsFinite(value.Position.Z);

    private static Quaternion Q(Vector3 axis, float degrees) => Quaternion.CreateFromAxisAngle(axis, degrees * MathF.PI / 180);

    private static float Degrees(Quaternion a, Quaternion b) =>
        2 * MathF.Acos(Math.Clamp(Math.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))), 0, 1)) * 180 / MathF.PI;

    private static MotionLayer Repeat(MotionRepeat repeat, params string[] bones) =>
        new("m1", MotionLayerKind.Repeat, "Repeat 1", 0, 60, 0, 0, 1, PoseComponents.Rotation | PoseComponents.Position, MotionInterpolation.Smooth,
            Bones: [.. bones], Repeat: repeat);

    private static BoneTransform T(Vector3 position, Quaternion? rotation = null) => new(position, rotation ?? Quaternion.Identity, Vector3.One);
}
