using System.Collections.Immutable;
using System.Numerics;
using InstantEdit.Models;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;

/// <summary>
/// Smooth motion layers: each frame goes toward a bell-weighted average of the frames round it over the layer's window,
/// so sudden moves spread out and steady ones stay; their validation.
/// </summary>
internal static class SmoothMotionFixture
{
    private const string B1 = "j_b1", B2 = "j_b2";

    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Validation(check, reject);
        Step(check);
        Steady(check);
        Strength(check);
        Seam(check);
        Midpoints(check);
        Wide(check);
        Flip(check);
        Partway(check);
        Long(check);
    }

    // Review: a layer over part of the clip must not make a jump of its own at its edges.
    private static void Partway(Action<bool, string> check)
    {
        var step = Stepped(61, 60, 30);
        var smoothed = Applied(step, Smooth(10, 1, 30, 60));
        var edge = Degrees(smoothed.Get(30, Index(B1)).Rotation, smoothed.Get(29, Index(B1)).Rotation);
        var largest = Enumerable.Range(1, 60).Max(i => Degrees(smoothed.Get(i, Index(B1)).Rotation, smoothed.Get(i - 1, Index(B1)).Rotation));
        check(edge < 1e-3f && largest < 30, $"a smooth over part of the clip adds no jump at its edge ({edge:0.##} degrees there, {largest:0.##} at most)");
    }

    // Review: a wide window on a long dance is baked while editing, sometimes during a draw.
    private static void Long(Action<bool, string> check)
    {
        const int bones = 150, samples = 1801;
        var rig = new SkeletonDescription("smooth-long", "smooth-long-fingerprint",
            [.. Enumerable.Range(0, bones).Select(b => new SkeletonBone("j_b" + b, (short)(b - 1), 0, T(Vector3.UnitY)))], [], [], []);
        var poses = Enumerable.Range(0, samples).Select(i => Enumerable.Range(0, bones)
            .Select(b => T(new Vector3(0, 1 + .01f * MathF.Sin(i * .05f + b), 0), Q(Vector3.UnitY, 20 * MathF.Sin(i * .03f + b) + (i % 7 == 0 ? 3 : 0)))).ToArray()).ToArray();
        var made = MotionClip.FromPoses(rig, 900f / TimelineDocument.Fps, poses);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        MotionEvaluator.Apply(made, [Smooth(MotionSmoothing.MaxWindow, 1, 0, 900)]);
        watch.Stop();
        // About 0.2 s on the dev machine; the per-neighbour sums it replaced took 3.5 s, and 1 s even without rotation vectors.
        check(watch.ElapsedMilliseconds < 750, $"a 60-frame window over 150 bones and 30 s takes {watch.ElapsedMilliseconds} ms");
    }

    private static void Step(Action<bool, string> check)
    {
        var step = Stepped(61, 60, 30);
        var smoothed = Applied(step, Smooth(10, 1, 0, 60));
        var largest = Enumerable.Range(1, 60).Max(i => Degrees(smoothed.Get(i, Index(B1)).Rotation, smoothed.Get(i - 1, Index(B1)).Rotation));
        check(largest < 10, $"a 30-degree snap spreads out ({largest:0.##} degrees a frame at most)");
        check(smoothed.Get(0, Index(B1)) == step.Get(0, Index(B1)) && Near(smoothed.Get(60, Index(B1)), step.Get(60, Index(B1)), 1e-4f),
            "a clip that plays once keeps its first and last pose");
        check(Enumerable.Range(0, 61).All(i => smoothed.Get(i, Index(B2)) == step.Get(i, Index(B2))), "a bone that never moves is left alone");
    }

    private static void Steady(Action<bool, string> check)
    {
        var steady = Clip(61, 60, (_, f) => [T(Vector3.Zero), T(new Vector3(.01f * f, 0, 0), Q(Vector3.UnitY, f)), T(Vector3.UnitY)]);
        var smoothed = Applied(steady, Smooth(10, 1, 0, 60));
        check(Enumerable.Range(0, 61).All(i => Near(smoothed.Get(i, Index(B1)), steady.Get(i, Index(B1)), 1e-3f)), "motion at a steady speed stays as it is");
    }

    private static void Strength(Action<bool, string> check)
    {
        var step = Stepped(61, 60, 30);
        var none = Applied(step, Smooth(10, 0, 0, 60));
        check(Enumerable.Range(0, 61).All(i => none.Get(i, Index(B1)) == step.Get(i, Index(B1))), "a smooth at 0% changes nothing");
        var full = Degrees(Applied(step, Smooth(10, 1, 0, 60)).Get(31, Index(B1)).Rotation, step.Get(31, Index(B1)).Rotation);
        var half = Degrees(Applied(step, Smooth(10, .5f, 0, 60)).Get(31, Index(B1)).Rotation, step.Get(31, Index(B1)).Rotation);
        check(full > 5 && MathF.Abs(half - full / 2) < .1f, $"a smooth at 50% goes halfway ({half:0.##} of {full:0.##} degrees)");
    }

    private static void Seam(Action<bool, string> check)
    {
        // A loop at 30 degrees round its seam (samples 56 to 0) and 0 the rest of the way.
        var loop = Clip(61, 60, (i, _) => [T(Vector3.Zero), T(Vector3.UnitX, Q(Vector3.UnitY, i == 0 || i >= 56 ? 30 : 0)), T(Vector3.UnitY)], loops: true);
        var smoothed = Applied(loop, Smooth(10, 1, 0, 60));
        check(Near(smoothed.Get(0, Index(B1)), smoothed.Get(60, Index(B1)), 1e-4f) &&
              Degrees(smoothed.Get(0, Index(B1)).Rotation, loop.Get(0, Index(B1)).Rotation) > 5,
            "a smooth covering a loop averages round the seam and keeps its first and last sample the same");
    }

    // Game clips decode at about twice their key rate, the samples between keys halfway between them.
    private static void Midpoints(Action<bool, string> check)
    {
        static float Sweep(float frame) => 90 * MathF.Sin(MathF.PI * frame / 60);
        var keys = Clip(31, 60, (_, f) => [T(Vector3.Zero), T(Vector3.UnitX, Q(Vector3.UnitY, Sweep(f))), T(Vector3.UnitY)]);
        var halves = Clip(61, 60, (i, f) => [T(Vector3.Zero),
            T(Vector3.UnitX, Q(Vector3.UnitY, i % 2 == 0 ? Sweep(f) : (Sweep(f - 1) + Sweep(f + 1)) / 2)), T(Vector3.UnitY)]);
        var (a, b) = (Applied(keys, Smooth(10, 1, 0, 60)), Applied(halves, Smooth(10, 1, 0, 60)));
        var worst = Enumerable.Range(0, 31).Max(k => Degrees(a.Get(k, Index(B1)).Rotation, b.Get(2 * k, Index(B1)).Rotation));
        check(worst < .25f, $"midpoints between keys add no ripple ({worst:0.###} degrees apart on the keys)");
    }

    // Review: a window wider than the clip.
    private static void Wide(Action<bool, string> check)
    {
        var step = Stepped(21, 20, 10);
        var smoothed = Applied(step, Smooth(MotionSmoothing.MaxWindow, 1, 0, 20));
        check(Enumerable.Range(0, 21).All(i => Finite(smoothed.Get(i, Index(B1)))) && smoothed.Get(0, Index(B1)) == step.Get(0, Index(B1)) &&
              Near(smoothed.Get(20, Index(B1)), step.Get(20, Index(B1)), 1e-4f),
            "a window wider than the clip stays finite and keeps the clip's ends");
    }

    // Review: a turn through 180 degrees flips the rotation's sign in the window.
    private static void Flip(Action<bool, string> check)
    {
        var turning = Clip(61, 60, (_, f) => [T(Vector3.Zero), T(Vector3.UnitX, Q(Vector3.UnitY, 170 + f / 3)), T(Vector3.UnitY)]);
        var smoothed = Applied(turning, Smooth(10, 1, 0, 60));
        var largest = Enumerable.Range(1, 60).Max(i => Degrees(smoothed.Get(i, Index(B1)).Rotation, smoothed.Get(i - 1, Index(B1)).Rotation));
        check(largest < 2, $"a turn through 180 degrees smooths without a flip ({largest:0.##} degrees a frame at most)");
    }

    /// <summary>b1 turns 30 degrees about Y between samples <paramref name="at"/> and the next, b2 holds still.</summary>
    private static MotionClip Stepped(int samples, float frames, int at) =>
        Clip(samples, frames, (i, _) => [T(Vector3.Zero), T(Vector3.UnitX, Q(Vector3.UnitY, i > at ? 30 : 0)), T(Vector3.UnitY)]);

    // A root and two bones under it.
    private static SkeletonDescription Rig() => new("smooth-rig", "smooth-rig-fingerprint",
        [new("n_root", -1, 0, T(Vector3.Zero)), new(B1, 0, 0, T(Vector3.UnitX)), new(B2, 0, 0, T(Vector3.UnitY))], [], [], []);

    private static int Index(string bone) => MotionEvaluator.Index(Rig())[bone];

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

    private static bool Near(MotionTransform a, MotionTransform b, float tolerance) =>
        Vector3.Distance(a.Position, b.Position) <= tolerance &&
        (Quaternion.Dot(a.Rotation, b.Rotation) >= 0 ? a.Rotation - b.Rotation : a.Rotation + b.Rotation).Length() <= tolerance;

    private static bool Finite(MotionTransform value) =>
        float.IsFinite(value.Rotation.X) && float.IsFinite(value.Rotation.Y) && float.IsFinite(value.Rotation.Z) && float.IsFinite(value.Rotation.W) &&
        float.IsFinite(value.Position.X) && float.IsFinite(value.Position.Y) && float.IsFinite(value.Position.Z);

    private static Quaternion Q(Vector3 axis, float degrees) => Quaternion.CreateFromAxisAngle(axis, degrees * MathF.PI / 180);

    private static float Degrees(Quaternion a, Quaternion b) =>
        2 * MathF.Acos(Math.Clamp(Math.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))), 0, 1)) * 180 / MathF.PI;

    private static void Validation(Action<bool, string> check, Action<Action, string> reject)
    {
        var smooth = Smooth(10, 1, 0, 60);
        MotionEvaluator.Validate(smooth);
        MotionEvaluator.Validate(Smooth(MotionSmoothing.MinWindow, 1, 0, 60));
        MotionEvaluator.Validate(Smooth(MotionSmoothing.MaxWindow, 1, 0, 60));
        void Refused(MotionLayer layer, string what) => reject(() => MotionEvaluator.Validate(layer), what);
        Refused(Smooth(MotionSmoothing.MinWindow - 1, 1, 0, 60), "a smooth window under 3 frames is refused");
        Refused(Smooth(MotionSmoothing.MaxWindow + 1, 1, 0, 60), "a smooth window past 60 frames is refused");
        Refused(new MotionLayer("m1", MotionLayerKind.Amplitude, "Amplitude 1", 0, 60, 0, 0, 1.5f, PoseComponents.Rotation, MotionInterpolation.Smooth,
            Window: 10), "a window on an amplitude layer is refused");
        Refused(smooth with { Keys = [new MotionKey(0, ImmutableDictionary<string, BoneTransform>.Empty.Add(B1, T(Vector3.Zero)))] },
            "a smooth layer with keys is refused");
        Refused(smooth with { Amount = 1.5f }, "a smooth above 100% is refused");
        Refused(smooth with { Swing = new MotionSwing() }, "a smooth layer with a swing is refused");
        check(MotionEvaluator.Active(smooth) && !MotionEvaluator.Active(smooth with { Amount = 0 }) &&
              !MotionEvaluator.Active(smooth with { Components = PoseComponents.Scale }),
            "a smooth is active with an amount and a turn or move to change");
    }

    internal static MotionLayer Smooth(int window, float amount, int start, int end) =>
        new("m1", MotionLayerKind.Smooth, "Smooth motion 1", start, end, 0, 0, amount, PoseComponents.Rotation | PoseComponents.Position,
            MotionInterpolation.Smooth, Window: window);

    private static BoneTransform T(Vector3 position, Quaternion? rotation = null) => new(position, rotation ?? Quaternion.Identity, Vector3.One);
}
