using System.Collections.Immutable;
using System.Numerics;
using InstantEdit.Models;
using InstantEdit.Services.Animations;

internal static class AnimationFramesFixture
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        static BoneTransform T(float x, float degrees = 0, float scale = 1) =>
            new(new(x, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, degrees * MathF.PI / 180), new(scale, scale, scale));
        static SkeletonDescription Rig() => new("rig", "rig-fingerprint",
            [new("root", -1, 0, T(0)), new("hand", 0, 0, T(1))], ["blink"], [0f], []);
        static AnimationFrames Frames(float duration, params (float X, float Degrees)[] hand) => new(Rig(),
            [.. hand.Select(h => new[] { T(0), T(h.X, h.Degrees) })],
            [.. hand.Select((h, i) => new[] { (float)i })], duration);

        var linear = Frames(1, (0, 0), (1, 0), (2, 0));

        // Resampling must interpolate uniformly. Easing belongs to transitions only;
        // applying it here would bend every retimed clip toward its keyframes.
        var (midPose, midFloats) = AnimationTimeline.SampleNormalized(linear, 0.25f);
        check(Math.Abs(midPose[1].Position.X - 0.5f) < 1e-5 && Math.Abs(midFloats[0] - 0.5f) < 1e-5,
            "sampling a clip interpolates uniformly rather than easing toward keyframes");
        check(AnimationTimeline.SampleNormalized(linear, 0).Pose[1].Position.X == 0 &&
              Math.Abs(AnimationTimeline.SampleNormalized(linear, 1).Pose[1].Position.X - 2) < 1e-5,
            "sampling a clip reproduces both endpoints exactly");
        check(AnimationPoseRules.Blend(T(0), T(1), 0.25f).Position.X == AnimationPoseRules.Interpolate(T(0), T(1), AnimationPoseRules.SmoothStep(0.25f)).Position.X,
            "eased blending stays defined as uniform interpolation over the smoothstep curve");

        var stretched = AnimationTimeline.Resample(linear, 2);
        check(stretched.Duration == 2 && stretched.Count == AnimationPoseRules.SampleCount(2, linear.Count) &&
              stretched.Poses[0][1].Position.X == 0 && Math.Abs(stretched.Poses[^1][1].Position.X - 2) < 1e-5,
            "retiming re-grids onto the new duration while keeping both endpoints");
        check(AnimationTimeline.Resample(linear, 1).Count == AnimationPoseRules.SampleCount(1, linear.Count),
            "retiming to the same duration still lands on the uniform sample grid");
        reject(() => AnimationTimeline.Resample(linear, float.NaN), "a non-finite retime duration is rejected");
        reject(() => AnimationTimeline.Resample(linear, -1), "a negative retime duration is rejected");

        var trimmed = AnimationTimeline.Trim(linear, 0.5f, 1);
        check(Math.Abs(trimmed.Duration - 0.5f) < 1e-5 && Math.Abs(trimmed.Poses[0][1].Position.X - 1) < 1e-5 &&
              Math.Abs(trimmed.Poses[^1][1].Position.X - 2) < 1e-5,
            "trimming keeps the selected range and rebases it to a new duration");
        reject(() => AnimationTimeline.Trim(linear, 0.5f, 0.5f), "an empty trim range is rejected");
        reject(() => AnimationTimeline.Trim(linear, -0.1f, 1), "a trim range starting before the animation is rejected");
        reject(() => AnimationTimeline.Trim(linear, 0, 1.5f), "a trim range running past the animation is rejected");

        // A clip that ends somewhere other than where it started pops on every wrap.
        var popping = Frames(1, (0, 0), (1, 0), (2, 0));
        var seam = AnimationTimeline.SeamError(popping);
        check(seam.Length == 2 && seam.Single(b => b.Bone == "hand").Position > 1.9f,
            "seam error reports how far each bone misses the wrap point");

        var looped = AnimationTimeline.CrossfadeLoop(popping, 0.5f);
        var loopedSeam = AnimationTimeline.SeamError(looped);
        check(loopedSeam.All(b => b.Position < 1e-5 && b.Rotation < 1e-5),
            "a crossfaded loop closes its seam exactly for every bone");
        check(looped.Count == popping.Count && looped.Duration == popping.Duration &&
              looped.Poses[0][1].Position.X == popping.Poses[0][1].Position.X,
            "crossfading a loop changes neither the sample grid, the duration, nor the opening pose");
        check(AnimationTimeline.SeamError(AnimationTimeline.CrossfadeLoop(popping, 0)).All(b => b.Position < 1e-5),
            "a zero-length crossfade still pins the final sample to the opening pose");
        reject(() => AnimationTimeline.CrossfadeLoop(popping, 2), "a crossfade longer than the animation is rejected");
        reject(() => AnimationTimeline.CrossfadeLoop(popping, -0.1f), "a negative crossfade is rejected");

        // Havok interpolates rotations along the shorter arc, so a sign flip between
        // adjacent samples would compress into a visible spin.
        var spinning = Frames(1, (0, 0), (0, 200), (0, 350));
        var normalized = AnimationTimeline.NormalizeHemisphere(spinning);
        var adjacent = Enumerable.Range(1, normalized.Count - 1).SelectMany(i =>
            Enumerable.Range(0, 2).Select(b => Quaternion.Dot(normalized.Poses[i - 1][b].Rotation, normalized.Poses[i][b].Rotation)));
        check(adjacent.All(dot => dot >= 0), "hemisphere normalization leaves no reversed rotation between adjacent samples");
        check(Enumerable.Range(0, normalized.Count).All(i => Enumerable.Range(0, 2).All(b =>
                Math.Abs(Math.Abs(Quaternion.Dot(normalized.Poses[i][b].Rotation, spinning.Poses[i][b].Rotation)) - 1) < 1e-5)),
            "hemisphere normalization only changes quaternion signs, never orientations");
        check(AnimationTimeline.SeamError(AnimationTimeline.CrossfadeLoop(spinning, 0.5f)).All(b => b.Rotation < 1e-5),
            "a crossfaded loop closes its rotation seam even when the source crosses hemispheres");

        var rig = Rig();
        reject(() => AnimationTimeline.Validate(new AnimationFrames(rig, [], [], 1)), "an empty frame list is rejected");
        reject(() => AnimationTimeline.Validate(new AnimationFrames(rig, [[T(0)]], [[0f]], 1)),
            "a frame that does not match its skeleton's bone count is rejected");
        reject(() => AnimationTimeline.Validate(new AnimationFrames(rig, [[T(0), T(1)]], [[0f, 1f]], 1)),
            "a float set that does not match its skeleton's channel count is rejected");
        reject(() => AnimationTimeline.Validate(new AnimationFrames(rig, [[T(0), T(1)]], [], 1)),
            "misaligned pose and float frame counts are rejected");
        reject(() => AnimationTimeline.Validate(linear with { Duration = 3601 }), "a frame set longer than an hour is rejected");
    }
}
