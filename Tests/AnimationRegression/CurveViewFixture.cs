using System.Collections.Immutable;
using System.Numerics;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Ui.Timeline;

/// <summary>
/// The curve view's values (<see cref="MotionCurves"/>): turns unwrapped as Euler angles, a layer's keys sampled the way
/// the evaluator plays them, one channel written back; and the whole-clip bake it draws "after layers" from
/// (<see cref="CurveBake"/>).
/// </summary>
internal static class CurveViewFixture
{
    private const string Root = "n_root", Arm = "j_ude_a_l";

    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Twins(check);
        Unwraps(check);
        OffsetKeys(check);
        PoseKeys(check);
        Writes(check);
        Clips(check);
        Fits(check);
        Bakes(check);
    }

    private static Quaternion Euler(float x, float y, float z) => MotionAngles.FromEuler(new Vector3(x, y, z));

    private static float Degrees(Quaternion a, Quaternion b) =>
        2 * MathF.Acos(Math.Clamp(Math.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))), 0, 1)) * 180 / MathF.PI;

    private static bool Near(Vector3 a, Vector3 b, float tolerance = 1e-2f) => Vector3.Distance(a, b) < tolerance;

    private static bool Near(float a, float b, float tolerance = 1e-2f) => MathF.Abs(a - b) < tolerance;

    private static BoneTransform T(Quaternion rotation, Vector3? position = null) => new(position ?? Vector3.Zero, rotation, Vector3.One);

    private static MotionKey Key(int frame, BoneTransform value, bool moved = false, MotionInterpolation? ease = null) =>
        new(frame, ImmutableDictionary<string, BoneTransform>.Empty.WithComparers(StringComparer.Ordinal).Add(Arm, value), moved ? [Arm] : [], ease);

    private static MotionLayer Layer(MotionLayerKind kind, MotionInterpolation ease, params MotionKey[] keys) =>
        new("m1", kind, "Layer", 0, 40, 0, 0, 1, PoseComponents.All, ease, [], [.. keys]);

    private static void Twins(Action<bool, string> check)
    {
        var e = new Vector3(30, 50, -70);
        var twin = MotionCurves.Twin(e);
        check(Degrees(MotionAngles.FromEuler(e), MotionAngles.FromEuler(twin)) < .01f, "an Euler triple's twin (x+180, 180-y, z+180) is the same rotation");
        check(Near(MotionCurves.Nearest(e, e), e), "the nearest of a triple to itself is itself");
        check(Near(MotionCurves.Nearest(e + new Vector3(360, -360, 720), e), e), "whole turns are taken off each angle");
        var nearTwin = twin + new Vector3(2, -1, 3);
        check(Near(MotionCurves.Nearest(e, nearTwin), twin), "the twin is picked when it lies closer");
    }

    private static void Unwraps(Action<bool, string> check)
    {
        // A turn about Z from 170 to 190 degrees: ToEuler wraps to -170, the curve goes on to 190.
        var rotations = Enumerable.Range(0, 21).Select(i => Euler(0, 0, 170 + i)).ToArray();
        var samples = MotionCurves.Of([.. Enumerable.Range(0, 21).Select(i => (float)i)], [.. rotations.Select(q => new MotionTransform(Vector3.Zero, q, Vector3.One))]);
        var z = samples.Values[(int)CurveChannel.TurnZ]!;
        check(Near(z[0], 170) && Near(z[^1], 190) && z.Zip(z.Skip(1)).All(pair => Math.Abs(pair.Second - pair.First) < 1.5f),
            "a turn crossing 180 degrees runs on without a jump");

        // A spin about X through two whole turns climbs to 720.
        var spin = Enumerable.Range(0, 73).Select(i => new MotionTransform(Vector3.Zero, Euler(i * 10, 0, 0), Vector3.One)).ToArray();
        var x = MotionCurves.Of([.. Enumerable.Range(0, 73).Select(i => (float)i)], spin).Values[(int)CurveChannel.TurnX]!;
        check(Near(x[^1], 720, .1f), "a spin through two whole turns climbs to 720 degrees");

        // A turn about Y over 90 degrees: ToEuler keeps Y within 90 and flips X and Z, the curve keeps X and Z at 0.
        var over = Enumerable.Range(0, 41).Select(i => new MotionTransform(Vector3.Zero, Euler(0, 70 + i, 0), Vector3.One)).ToArray();
        var turned = MotionCurves.Of([.. Enumerable.Range(0, 41).Select(i => (float)i)], over);
        var y = turned.Values[(int)CurveChannel.TurnY]!;
        check(Near(y[^1], 110, .1f) && turned.Values[(int)CurveChannel.TurnX]!.All(v => Math.Abs(v) < .1f) &&
              turned.Values[(int)CurveChannel.TurnZ]!.All(v => Math.Abs(v) < .1f), "a turn about Y past 90 degrees runs on without flipping X and Z");

        // A seed starts the chain in its representation: the after curve starts where the before curve does.
        var seeded = MotionCurves.Of([0f, 1f], [.. rotations.Take(2).Select(q => new MotionTransform(Vector3.Zero, q, Vector3.One))], new Vector3(0, 0, -190));
        check(Near(seeded.Values[(int)CurveChannel.TurnZ]![0], -190), "a seed starts the curve in its turn");
        check(Near(samples.Min(CurveChannel.TurnZ), 170) && Near(samples.Max(CurveChannel.TurnZ), 190), "a curve knows its lowest and highest values");
    }

    private static void OffsetKeys(Action<bool, string> check)
    {
        var layer = Layer(MotionLayerKind.Offset, MotionInterpolation.Smooth,
            Key(0, T(Euler(0, 0, 0), new Vector3(0, .1f, 0))),
            Key(10, T(Euler(40, 0, 20), new Vector3(.2f, 0, 0)), ease: MotionInterpolation.EaseIn),
            Key(30, T(Euler(-20, 10, 0))));
        var curve = MotionCurves.OfKeys(layer, Arm, 0, 40);
        check(curve != null && curve.Samples.Frames.Length == 41 && curve.Keys.Length == 3 && curve.Keys.All(key => key.Moves), "an offset layer's curve covers the frames, a dot per key");
        if (curve == null)
            return;
        var matches = true;
        foreach (var frame in new[] { 0, 3, 7, 10, 13, 22, 30, 35 })
        {
            var expected = MotionEvaluator.KeyValue(layer, Arm, frame)!.Value;
            var shown = curve.Samples.Turn(frame);
            matches &= Degrees(MotionAngles.FromEuler(shown), expected.Rotation) < .05f && Near(curve.Samples.Move(frame), expected.Position * 100);
        }
        check(matches, "the eased in-between values match the evaluator's, key ease and layer ease");
        check(Near(curve.Samples.Turn(10), new Vector3(40, 0, 20)) && Near(curve.Samples.Move(10), new Vector3(20, 0, 0)), "a dot shows its key's turn and move");
        check(MotionCurves.OfKeys(layer, Root, 0, 40) == null, "a bone the layer doesn't key has no curve");
        check(MotionCurves.OfKeys(layer with { Kind = MotionLayerKind.Pin }, Arm, 0, 40) == null, "only offset and pose layers have curves");

        // Wrapping round a loop the layer covers: past the last key the value goes back toward the first.
        var looped = MotionCurves.OfKeys(layer with { End = 40 }, Arm, 0, 40, 40);
        var wrapped = MotionEvaluator.KeyValue(layer with { End = 40 }, Arm, 35, 40)!.Value;
        check(looped != null && Degrees(MotionAngles.FromEuler(looped.Samples.Turn(35)), wrapped.Rotation) < .05f, "a loop the layer covers wraps the curve as the evaluator does");
    }

    private static void PoseKeys(Action<bool, string> check)
    {
        var layer = Layer(MotionLayerKind.Pose, MotionInterpolation.Linear,
            Key(0, T(Euler(10, 0, 0), new Vector3(.1f, 0, 0)), moved: true),
            Key(10, T(Euler(30, 0, 0), new Vector3(.9f, 0, 0))),
            Key(20, T(Euler(50, 0, 0), new Vector3(.3f, 0, 0)), moved: true));
        var curve = MotionCurves.OfKeys(layer, Arm, 0, 20);
        check(curve != null && curve.Keys.Select(key => key.Moves).SequenceEqual([true, false, true]), "a pose key shows a move only where it sets the position");
        if (curve == null)
            return;
        // The evaluator on a bone at rest with the layer at full weight lands exactly on the keys' values.
        var skeleton = Rig();
        var rest = new[] { MotionTransform.Identity, MotionTransform.Identity };
        var matches = true;
        foreach (var frame in new[] { 0, 5, 10, 15, 20 })
        {
            var pose = MotionEvaluator.ApplyAt(skeleton, rest, [layer], frame);
            matches &= Degrees(MotionAngles.FromEuler(curve.Samples.Turn(frame)), pose[1].Rotation) < .05f && Near(curve.Samples.Move(frame), pose[1].Position * 100);
        }
        check(matches, "a pose layer's turns follow every key and its moves only the keys that set them, as the evaluator plays them");
        var turnOnly = Layer(MotionLayerKind.Pose, MotionInterpolation.Linear, Key(0, T(Euler(10, 0, 0))), Key(10, T(Euler(30, 0, 0))));
        check(MotionCurves.OfKeys(turnOnly, Arm, 0, 10) is { } only && only.Samples.Values[(int)CurveChannel.MoveX] == null,
            "a pose layer that never sets the bone's position has no move curves");
    }

    private static void Writes(Action<bool, string> check)
    {
        var value = new MotionTransform(new Vector3(.1f, .2f, .3f), Euler(30, 50, -70), Vector3.One);
        // Shown as the twin, the way a curve that came over Y = 90 shows it.
        var shown = MotionCurves.Twin(new Vector3(30, 50, -70));
        var keeps = true;
        foreach (var channel in new[] { CurveChannel.TurnX, CurveChannel.TurnY, CurveChannel.TurnZ })
        {
            var amount = MotionCurves.Value(value, shown, channel) + 15;
            var written = MotionCurves.WithChannel(value, shown, channel, amount);
            var expected = channel switch
            {
                CurveChannel.TurnX => shown with { X = amount },
                CurveChannel.TurnY => shown with { Y = amount },
                _ => shown with { Z = amount },
            };
            keeps &= Near(MotionCurves.Nearest(MotionAngles.ToEuler(written.Rotation), expected), expected, .05f) &&
                     written.Position == value.Position && written.Scale == value.Scale;
        }
        check(keeps, "writing one turn channel changes that angle and keeps the other two, the move and the scale");
        var moved = MotionCurves.WithChannel(value, shown, CurveChannel.MoveY, 50);
        check(Near(moved.Position, new Vector3(.1f, .5f, .3f), 1e-5f) && moved.Rotation == value.Rotation, "writing a move channel sets that axis in centimetres and keeps the rest");
        check(MotionCurves.Value(value, shown, CurveChannel.MoveZ) is var z && Near(z, 30) && Near(MotionCurves.Value(value, shown, CurveChannel.TurnY), shown.Y),
            "a channel's value reads back in centimetres and shown degrees");
    }

    private static void Clips(Action<bool, string> check)
    {
        var skeleton = Rig();
        var clip = new MotionClip(skeleton, 1, 31);
        for (var i = 0; i < clip.Samples; i++)
        {
            clip.Set(i, 0, MotionTransform.Identity);
            clip.Set(i, 1, new MotionTransform(new Vector3(0, i * .01f, 0), Euler(0, 0, 160 + i * 2), Vector3.One));
        }
        var samples = MotionCurves.OfClip(clip, 1);
        check(samples.Frames.Length == 31 && Near(samples.Frames[^1], 30) && Near(samples.Values[(int)CurveChannel.TurnZ]![^1], 220) &&
              Near(samples.Values[(int)CurveChannel.MoveY]![^1], 30), "a clip's bone reads per sample at its frame, unwrapped and in centimetres");
    }

    private static void Fits(Action<bool, string> check)
    {
        var (low, high) = MotionCurves.Fit(0, 100, 2);
        check(low < 0 && high > 100 && Near(high - low, 120, 1), "the scale fits the values with a margin");
        (low, high) = MotionCurves.Fit(5, 5, 2);
        check(high - low >= 2 && low < 5 && high > 5, "a flat curve still gets a span");
        (low, high) = MotionCurves.Fit(float.PositiveInfinity, float.NegativeInfinity, 2);
        check(float.IsFinite(low) && float.IsFinite(high) && high > low, "no values give a span round zero");
    }

    private static void Bakes(Action<bool, string> check)
    {
        var skeleton = Rig();
        var clip = new MotionClip(skeleton, 1, 31);
        for (var i = 0; i < clip.Samples; i++)
        {
            clip.Set(i, 0, MotionTransform.Identity);
            clip.Set(i, 1, MotionTransform.Identity);
        }
        var turn = Layer(MotionLayerKind.Offset, MotionInterpolation.Linear, Key(0, T(Euler(0, 0, 0))), Key(30, T(Euler(0, 0, 60))));
        ImmutableArray<MotionLayer> layers = [turn];
        var starts = 0;
        var bake = new CurveBake { Start = action => { starts++; action(); return Task.CompletedTask; } };
        var after = bake.After(clip, layers, false, out _, out var failed);
        var expected = MotionEvaluator.KeyValue(turn, Arm, 15)!.Value.Rotation;
        check(after != null && !ReferenceEquals(after, clip) && !failed && Degrees(after.Get(15, 1).Rotation, expected) < .05f && clip.Get(15, 1).Rotation == Quaternion.Identity,
            "the bake applies the layers to a copy, the clip stays as it was");
        check(ReferenceEquals(bake.After(clip, layers, false, out _, out _), after) && starts == 1, "the same clip and layers reuse the bake");
        check(ReferenceEquals(bake.After(clip, [turn with { Enabled = false }], false, out _, out _), clip) && starts == 1, "with no layer changing the motion, after is the clip itself");

        ImmutableArray<MotionLayer> moved = [turn with { Amount = .5f }];
        check(bake.After(clip, moved, true, out var stale, out _) == null && ReferenceEquals(stale, after) && starts == 1,
            "during a drag nothing bakes, and the last bake of the clip stands");
        var other = clip.Clone();
        check(bake.After(other, moved, true, out stale, out _) == null && stale == null, "a bake of another clip never stands for this one");

        var queued = new List<Action>();
        var held = new CurveBake { Start = action => { queued.Add(action); return Task.CompletedTask; } };
        check(held.After(clip, layers, false, out stale, out _) == null && stale == null && queued.Count == 1, "while it builds there is nothing to show yet");
        check(held.After(clip, layers, false, out _, out _) == null && queued.Count == 1, "asking again doesn't start it twice");
        queued[0]();
        check(held.After(clip, layers, false, out _, out _) != null, "a finished build shows");

        var broken = new CurveBake { Start = action => { action(); return Task.CompletedTask; } };
        ImmutableArray<MotionLayer> missing = [Layer(MotionLayerKind.Offset, MotionInterpolation.Linear,
            new MotionKey(0, ImmutableDictionary<string, BoneTransform>.Empty.Add("j_nothing", T(Euler(0, 0, 30)))))];
        check(broken.After(clip, missing, false, out _, out failed) == null && failed, "layers that can't apply say so");
    }

    private static SkeletonDescription Rig() => new("curve-rig", "curve-rig-fingerprint",
        [new(Root, -1, 0, T(Quaternion.Identity)), new(Arm, 0, 0, T(Quaternion.Identity, Vector3.UnitX))], [], [], []);
}
