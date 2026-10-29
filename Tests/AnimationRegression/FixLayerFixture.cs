using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json.Nodes;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui;
using InstantEdit.Ui.Timeline;

/// <summary>
/// Fix layers: Match startup to loop (lands the last sample on the loop's first frame with its speed) and
/// Smooth jitter (calms small shakes, keeps snaps), their validation, journals and document edits.
/// </summary>
internal static class FixLayerFixture
{
    private const string B1 = "j_b1", B2 = "j_b2";
    private const float RotationTolerance = .05f, PositionTolerance = 1e-4f;

    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Match(check);
        Smooth(check);
        ResetRoot(check);
        Validation(check, reject);
        Journals(check);
        Edits(check);
        Views(check);
        Reader(check);
        Fades(check);
    }

    private static void Fades(Action<bool, string> check)
    {
        // Measured in game 2026-10-24: Red Ranger Pose A's 5.9 s startup had 0.33 s left as its loop started, Ground sit
        // pose 1's 1.33 s one 0.65 s.
        check(LoopFades.FramesLeft(5.9f, 5.57f) == 10 && LoopFades.FramesLeft(4f / 3, .68f) == 20,
            "a measured fade is the startup's frames left as its loop starts");
        check(LoopFades.FramesLeft(5.9f, 5.9f) == null && LoopFades.FramesLeft(5.9f, -1) == null && LoopFades.FramesLeft(float.NaN, 0) == null &&
              LoopFades.FramesLeft(10, 0) == null && LoopFades.FramesLeft(1, .999f) == null,
            "a startup at or past its end, a NaN or a fade past the longest is no measurement");

        var temp = Path.Combine(Path.GetTempPath(), "ie-loop-fades-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var fades = new LoopFades(temp);
            fades.Record(4784, 4785, 10);
            fades.Record(3135, 3136, 20);
            fades.Record(3135, 3136, 0);
            fades.Record(1, 2, LoopFades.Most + 1);
            fades.Saving.Wait();
            var again = new LoopFades(temp);
            check(again.Measured(4784, 4785) == 10 && again.Measured(3135, 3136) == 20 && again.Measured(4785, 4784) == null && again.Measured(1, 2) == null,
                "measured fades are kept by startup and loop, out of range ones dropped");

            // The watch records the row the game played; a capture's startup is the lowest row with that key.
            Func<ushort, string?> keys = id => id switch { 7404 or 7410 => "emote/pose05_start", 7405 => "emote/pose05_loop", _ => null };
            var keyed = new LoopFades(temp) { KeyOf = keys };
            keyed.Record(7410, 7405, 12);
            keyed.Saving.Wait();
            check(keyed.Measured(7404, 7405) == 12 && new LoopFades(temp) { KeyOf = keys }.Measured(7404, 7405) == 12 && keyed.Measured(4784, 4785) == 10,
                "a fade is kept by the timelines' keys, so a sibling startup row finds it; a timeline without one by its id");
            File.WriteAllText(Path.Combine(temp, "LoopFades.json"), "not json");
            string? warned = null;
            check(new LoopFades(temp, message => warned = message).Measured(4784, 4785) == null && warned != null,
                "an unreadable fades file starts empty and says so");
        }
        finally { Directory.Delete(temp, true); }
    }

    private static void Match(Action<bool, string> check)
    {
        var loop = Loop();
        var layer = MatchLayer(loop);
        var plain = Startup(31, 30);
        var clip = Applied(plain, layer);
        var last = clip.Samples - 1;
        check(Lands(clip, loop, B1) && Lands(clip, loop, B2), "match lands: the last sample is the loop's first frame, rotation and position");
        var span = MotionFixes.MatchSpan(layer, clip.Frame(last));
        check(span == (22, 30) && Before(clip, Applied(plain), span.Start), "match lands: samples before the blend are unchanged");

        // At 8 samples per frame the last pair carries almost none of the gap, so its change is the loop's speed.
        var fine = Applied(Startup(241, 30), layer);
        check(Speed(fine, 8, loop, B1, RotationTolerance, PositionTolerance), "match speed: the last pair moves at the loop's speed per frame");

        // A clip still turning 3 degrees a frame about X at its end into a loop 40 degrees on about Y that turns 2 a frame
        // about Z: the clip's speed is carried through the gap's turn, else the seam kinks by about the gap times that speed.
        var turning = Clip(241, 30, (_, f) => [T(Vector3.Zero), T(Vector3.UnitX, Q(Vector3.UnitX, 3 * f)), T(Vector3.UnitY)]);
        var aim = Q(Vector3.UnitY, 40) * Q(Vector3.UnitX, 90);
        var far = new MotionFix(MotionFixKind.MatchLoop, ImmutableDictionary.CreateRange(StringComparer.Ordinal, [KeyValuePair.Create(B1, T(Vector3.UnitX, aim))]),
            ImmutableDictionary.CreateRange(StringComparer.Ordinal, [KeyValuePair.Create(B1, T(Vector3.UnitX, Q(Vector3.UnitZ, 2) * aim))]));
        var bent = Applied(turning, layer with { Fix = far });
        var (end, before) = (bent.Get(bent.Samples - 1, Index(B1)).Rotation, bent.Get(bent.Samples - 2, Index(B1)).Rotation);
        var kink = (MotionCutter.Log(end * Quaternion.Inverse(before)) * 8 - MotionCutter.Log(far.LoopSecond![B1].Rotation * Quaternion.Inverse(aim))).Length() * 180 / MathF.PI;
        check(Lands(bent, far, B1) && kink < .05f, $"match far: a 40-degree gap on a still-turning clip lands at the loop's speed ({kink:0.###} deg per frame off)");

        var half = Applied(plain, layer with { Amount = .5f });
        check(MathF.Abs(Degrees(half.Get(last, Index(B1)).Rotation, loop.LoopFirst![B1].Rotation) - 10) < .1f, "match half: 50% lands halfway");

        var limited = layer with { Bones = [B1], Fix = Loop(ghost: true) };
        var limitedClip = Applied(plain, limited);
        MotionEvaluator.CheckBones([limited], Rig());
        check(Lands(limitedClip, loop, B1) && Track(limitedClip, B2).SequenceEqual(Track(Applied(plain), B2)),
            "match limit: a bone outside the limit is unchanged and a loop bone the skeleton lacks is skipped");

        var anchored = Applied(plain, layer with { Start = 10, End = 18 });
        check(Lands(anchored, loop, B1) && Before(anchored, Applied(plain), 22) && !Same(anchored.Get(23, Index(B1)), Applied(plain).Get(23, Index(B1))),
            "match anchored: a stored 10-18 blends over the clip's last 8 frames");

        var fraction = Applied(Startup(31, 30.4f), layer);
        check(Lands(fraction, loop, B1) && Lands(fraction, loop, B2), "match fraction: a last sample past the rounded end still lands");

        var shortPlain = Startup(5, 4);
        var shortClip = Applied(shortPlain, layer with { Start = 0, End = 8 });
        check(MotionFixes.MatchSpan(layer with { Start = 0, End = 8 }, 4) == (0, 4) && Lands(shortClip, loop, B1) &&
              Same(shortClip.Get(0, Index(B1)), Applied(shortPlain).Get(0, Index(B1))),
            "match short: a clip shorter than the blend starts it at frame 0 and lands");

        var rate = Applied(Startup(61, 30), layer);
        check(Lands(rate, loop, B1) && Speed(rate, 2, loop, B1, .02f * 20, .02f * Vector3.Distance(loop.LoopFirst[B1].Position, new Vector3(.3f, 0, 0))),
            "match rate: two samples per frame land and keep the loop's per-frame speed");

        var flipped = Loop() with { LoopFirst = Loop().LoopFirst!.SetItem(B1, loop.LoopFirst[B1] with { Rotation = -loop.LoopFirst[B1].Rotation }) };
        var flipClip = Applied(plain, layer with { Fix = flipped });
        check(Lands(flipClip, loop, B1) && Enumerable.Range(0, flipClip.Samples).All(i => Finite(flipClip.Get(i, Index(B1)))),
            "match flip: a negated loop rotation lands the same, no NaN");

        var baseline = Applied(plain);
        var identity = Applied(plain, layer with { Fix = Continuing(baseline) });
        check(Enumerable.Range(0, identity.Samples).All(i => Enumerable.Range(0, identity.Bones).All(b => Near(identity.Get(i, b), baseline.Get(i, b), 1e-5f))),
            "match identity: a loop that carries on the startup's motion changes nothing");

        var looping = Clip(31, 30, (_, f) => [T(Vector3.Zero), T(new Vector3(.01f * f, 0, 0), Q(Vector3.UnitY, f)), T(Vector3.UnitY)], loops: true);
        check(Bits(Applied(looping, layer), Applied(looping)), "match loop clip: a match on a looping clip changes nothing");

        var offset = new MotionLayer("m2", MotionLayerKind.Offset, "Offset", 0, 30, 0, 0, 1, PoseComponents.All, MotionInterpolation.Linear, [],
            [new MotionKey(0, ImmutableDictionary.CreateRange(StringComparer.Ordinal, [KeyValuePair.Create(B1, T(new Vector3(.1f, 0, 0), Q(Vector3.UnitX, 10)))]))]);
        check(Lands(Applied(plain, layer, offset), loop, B1), "match last: a match listed before an offset still lands");

        check(MotionFixes.LoopPose(Startup(31, 30)) is { Kind: MotionFixKind.MatchLoop } pose &&
              Near(MotionTransform.From(pose.LoopFirst![B1]), Startup(31, 30).Get(0, Index(B1)), 1e-5f) &&
              Near(MotionTransform.From(pose.LoopSecond![B1]), Startup(31, 30).Get(1, Index(B1)), 1e-5f) && pose.LoopFirst.Count == 3,
            "a loop pose holds the loop's frames 0 and 1 by bone name");

        // The game fades a startup into its loop over its last frames, the loop starting at frame 0: the startup's last
        // frame shows with the loop's frame of that number, which the match lands on.
        var startup = Startup(31, 30);
        check(MotionFixes.LoopPose(startup, 20) is { Fade: 20 } faded &&
              Near(MotionTransform.From(faded.LoopFirst![B1]), startup.Get(20, Index(B1)), 1e-5f) &&
              Near(MotionTransform.From(faded.LoopSecond![B1]), startup.Get(21, Index(B1)), 1e-5f),
            "a loop pose read for a 20-frame game fade holds the loop's frames 20 and 21");
        check(MotionFixes.LoopPose(startup, 35) is var wrapped &&
              Near(MotionTransform.From(wrapped.LoopFirst![B1]), startup.Get(5, Index(B1)), 1e-5f) &&
              Near(MotionTransform.From(wrapped.LoopSecond![B1]), startup.Get(6, Index(B1)), 1e-5f) &&
              Near(MotionTransform.From(MotionFixes.LoopPose(startup, 30).LoopFirst![B1]), startup.Get(30, Index(B1)), 1e-5f),
            "a game fade past the loop's end wraps round it, as the game plays it on; its end frame stays");
        check(MotionFixes.LoopPose(startup, 500).Fade == LoopFades.Most && MotionFixes.LoopPose(startup, -3).Fade == 0,
            "a loop pose's game fade is held to 0 to the longest one");
    }

    private static void Smooth(Action<bool, string> check)
    {
        var noisy = Noisy();
        var smoothed = Applied(noisy, SmoothLayer(1, 0, 60));
        var b1 = Index(B1);
        check(Energy(smoothed, b1) < .1f * Energy(noisy, b1) && PositionEnergy(smoothed, b1) < .1f * PositionEnergy(noisy, b1),
            "smooth calms: the jitter's energy drops below a tenth");
        var b2 = Index(B2);
        check(MathF.Abs(AboutY(smoothed.Get(30, b2).Rotation) - AboutY(smoothed.Get(29, b2).Rotation)) >= 27, "smooth calms: a 30-degree step survives");

        // Steady turning and moving: neighbours past a non-looping clip's ends mirror about them, so the ends stay put.
        var steady = Applied(Startup(31, 30));
        var steadied = Applied(Startup(31, 30), SmoothLayer(1, 0, 30));
        check(new[] { 0, steady.Samples - 1 }.All(i => new[] { B1, B2 }.All(bone =>
                  Vector3.Distance(steadied.Get(i, Index(bone)).Position, steady.Get(i, Index(bone)).Position) < 1e-4f &&
                  MotionCutter.Log(steadied.Get(i, Index(bone)).Rotation * Quaternion.Inverse(steady.Get(i, Index(bone)).Rotation)).Length() * 180 / MathF.PI < .01f)),
            "smooth ends: steady motion keeps the first and last samples");

        // A fast, clean, speeding-up turn keyed every other sample as game clips decode: the keys lie farther off their
        // low-pass than the samples between. Smoothing those apart (only the ones between, at Red Ranger Pose A's hand
        // whirls) turned the steady speed-up into steps that go up and down: a ripple.
        foreach (var speedUp in new[] { 6f, 12f })
        {
            var whirl = Clip(21, 10, (i, _) =>
            {
                float Key(int k) => speedUp * k * k;
                var degrees = i % 2 == 0 ? Key(i / 2) : (Key(i / 2) + Key(i / 2 + 1)) / 2;
                return [T(Vector3.Zero), T(Vector3.Zero, Q(Vector3.UnitY, degrees)), T(Vector3.UnitX)];
            });
            var whirled = Applied(whirl, SmoothLayer(.5f, 0, 10));
            float Step(int i) => Degrees(whirled.Get(i, b1).Rotation, whirled.Get(i - 1, b1).Rotation);
            check(Enumerable.Range(0, whirl.Samples).All(i => Degrees(whirled.Get(i, b1).Rotation, whirl.Get(i, b1).Rotation) < 3) &&
                  Enumerable.Range(2, whirl.Samples - 2).All(i => Step(i) >= Step(i - 1) - .05f),
                $"smooth adds no ripple to a fast clean move ({speedUp} deg per key squared): it moves little and its steps keep growing");
        }

        // Starts, slows, turns back and settles, keyed every other sample (Red Ranger Pose A's shoulder armour): keys sit
        // farther off the low-pass than the samples between, so smoothing both apart left steps going up, down, up.
        float[] keys = [0, 32, 67, 87, 90, 98, 113, 128, 142, 150, 152, 150, 140, 125, 110, 104, 103];
        var swing = Clip(2 * keys.Length - 1, keys.Length - 1, (i, _) =>
            [T(Vector3.Zero), T(Vector3.Zero, Q(Vector3.UnitY, i % 2 == 0 ? keys[i / 2] : (keys[i / 2] + keys[i / 2 + 1]) / 2)), T(Vector3.UnitX)]);
        var swung = Applied(swing, SmoothLayer(.5f, 0, keys.Length - 1));
        int Ripples(MotionClip c)
        {
            float Step(int i) => AboutY(c.Get(i, b1).Rotation) - AboutY(c.Get(i - 1, b1).Rotation);
            return Enumerable.Range(2, c.Samples - 4).Count(i =>
            {
                var (d1, d2, d3) = (Step(i) - Step(i - 1), Step(i + 1) - Step(i), Step(i + 2) - Step(i + 1));
                return MathF.Min(MathF.Abs(d1), MathF.Min(MathF.Abs(d2), MathF.Abs(d3))) > .5f && MathF.Sign(d1) != MathF.Sign(d2) && MathF.Sign(d2) != MathF.Sign(d3);
            });
        }
        check(Enumerable.Range(0, keys.Length - 1).All(k => Degrees(swung.Get(2 * k + 1, b1).Rotation,
                  MotionEvaluator.Slerp(swung.Get(2 * k, b1).Rotation, swung.Get(2 * k + 2, b1).Rotation, .5f)) < .01f) &&
              Ripples(swung) <= Ripples(swing) && !Bits(swung, swing),
            "smooth on a track keyed at half its rate smooths the keys, keeps the samples between halfway and adds no ripple");

        check(Bits(Applied(noisy, SmoothLayer(0, 0, 60)), Applied(noisy)), "smooth zero: strength 0 changes nothing");
        var still = Clip(61, 60, (_, _) => [T(Vector3.Zero), T(new Vector3(.2f, .1f, 0), Q(Vector3.UnitY, 30)), T(Vector3.UnitX)]);
        check(Bits(Applied(still, SmoothLayer(1, 0, 60)), Applied(still)), "smooth zero: a still bone is left as it is");

        // A lone twist: one sample turned off a still bone that comes straight back. The low-pass alone spread it into a
        // bump (Angel Dust's smoking idle: 5.7 degrees on frame 0 became 1.9 over 8 frames), and past twice the limit kept it.
        foreach (var (degrees, amount) in new[] { (6f, 1f), (6f, .5f), (30f, .5f) })
        {
            var twisted = Clip(31, 30, (i, _) => [T(Vector3.Zero), T(Vector3.Zero, Q(Vector3.UnitY, i == 15 ? degrees : 0)), T(Vector3.UnitX)]);
            var untwisted = Applied(twisted, SmoothLayer(amount, 0, 30));
            check(Enumerable.Range(0, twisted.Samples).All(i => MathF.Abs(AboutY(untwisted.Get(i, b1).Rotation)) < .05f),
                $"smooth twist: a lone {degrees}-degree twist at {amount:0%} goes back between its neighbours");
        }
        var pair = Clip(31, 30, (i, _) => [T(Vector3.Zero), T(new Vector3(i is 15 or 16 ? .03f : 0, 0, 0), Q(Vector3.UnitY, i is 15 or 16 ? 10 : 0)), T(Vector3.UnitX)]);
        var unpaired = Applied(pair, SmoothLayer(.5f, 0, 30));
        check(Enumerable.Range(0, pair.Samples).All(i => MathF.Abs(AboutY(unpaired.Get(i, b1).Rotation)) < .05f && unpaired.Get(i, b1).Position.Length() < 1e-4f),
            "smooth twist: a twist and move two samples long goes back too");

        // The seam of a loop whose first sample is the twist and whose last is its second, as the idle decodes.
        // At twice the key rate, as the game decodes, the sample after the twist lies halfway and goes back with it.
        foreach (var samples in new[] { 31, 61 })
        {
            var seamTwist = Clip(samples, 30, (i, _) => [T(Vector3.Zero), T(Vector3.Zero, Q(Vector3.UnitY, i == 0 ? 6 : i == 1 && samples == 61 ? 3.2f : .4f)), T(Vector3.UnitX)], loops: true);
            var seamUntwisted = Applied(seamTwist, SmoothLayer(1, 0, 30));
            check(Enumerable.Range(0, seamTwist.Samples).All(i => MathF.Abs(AboutY(seamUntwisted.Get(i, b1).Rotation) - .4f) < .05f),
                $"smooth twist: a twist on a loop's first sample goes back between the end and the second sample ({samples} samples)");
        }

        // Not a twist: a still bone that starts turning fast, or turns and stops, doesn't come back.
        var starts = Clip(31, 30, (i, _) => [T(Vector3.Zero), T(Vector3.Zero, Q(Vector3.UnitY, 30 * Math.Clamp(i - 15, 0, 3))), T(Vector3.UnitX)]);
        var started = Applied(starts, SmoothLayer(1, 0, 30));
        check(Enumerable.Range(0, starts.Samples).All(i => Degrees(started.Get(i, b1).Rotation, starts.Get(i, b1).Rotation) < .5f),
            "smooth twist: a sudden start or stop isn't flattened");

        // Noise only beside the seam: the first sample sees it only round the loop.
        BoneTransform[] Seam(int i, float frame) => [T(Vector3.Zero), T(Vector3.Zero, Q(Vector3.UnitY, i is >= 24 and <= 29 ? (i % 2 == 0 ? 1 : -1) : 0)), T(Vector3.UnitX)];
        var looped = Applied(Clip(31, 30, Seam, loops: true), SmoothLayer(1, 0, 30));
        var once = Applied(Clip(31, 30, Seam), SmoothLayer(1, 0, 30));
        check(MathF.Abs(AboutY(looped.Get(0, b1).Rotation)) > .1f && MathF.Abs(AboutY(looped.Get(0, b1).Rotation) - AboutY(looped.Get(30, b1).Rotation)) < 1e-3f &&
              MathF.Abs(AboutY(once.Get(0, b1).Rotation)) < 1e-4f,
            "smooth wraps: on a loop the first sample is smoothed with the last ones");
    }

    private static void ResetRoot(Action<bool, string> check)
    {
        // The root faces 90 degrees round and tilts 20 forward; b1 turns a degree a frame.
        var turned = Q(Vector3.UnitY, 90) * Q(Vector3.UnitX, 20);
        var plain = Clip(31, 30, (_, f) => [T(Vector3.Zero, turned), T(Vector3.UnitX, Q(Vector3.UnitY, f)), T(Vector3.UnitY)]);
        var root = Index("n_root");
        var unedited = Applied(plain);
        var all = Applied(plain, ResetLayer(MotionAxes.All, 1, 0, 30));
        check(Enumerable.Range(0, all.Samples).All(i => Degrees(all.Get(i, root).Rotation, Quaternion.Identity) < RotationTolerance) &&
              Track(all, B1).SequenceEqual(Track(unedited, B1)) && Track(all, B2).SequenceEqual(Track(unedited, B2)),
            "reset root: all axes put the root back at rest and leave the other bones as they are");

        var facing = Applied(plain, ResetLayer(MotionAxes.Y, 1, 0, 30)).Get(10, root).Rotation;
        check(Vector3.Distance(Vector3.Transform(Vector3.UnitY, facing), Vector3.Transform(Vector3.UnitY, turned)) < 1e-4f &&
              MathF.Abs(Degrees(facing, Quaternion.Identity) - 20) < RotationTolerance,
            "reset root: Y alone takes out the facing and keeps the tilt");
        var upright = Applied(plain, ResetLayer(MotionAxes.X | MotionAxes.Z, 1, 0, 30)).Get(10, root).Rotation;
        check(Vector3.Distance(Vector3.Transform(Vector3.UnitY, upright), Vector3.UnitY) < 1e-4f && Degrees(upright, Quaternion.Identity) > 80,
            "reset root: X and Z stand it upright and keep the facing");

        var half = Applied(plain, ResetLayer(MotionAxes.All, .5f, 0, 30)).Get(10, root).Rotation;
        var partly = Applied(plain, ResetLayer(MotionAxes.All, 1, 20, 30));
        check(MathF.Abs(Degrees(half, Quaternion.Identity) - Degrees(turned, Quaternion.Identity) / 2) < RotationTolerance &&
              Before(partly, unedited, 20) && Degrees(partly.Get(25, root).Rotation, Quaternion.Identity) < RotationTolerance,
            "reset root: the amount blends it and samples before the layer are unchanged");

        var still = Clip(31, 30, (_, f) => [T(Vector3.Zero), T(Vector3.UnitX, Q(Vector3.UnitY, f)), T(Vector3.UnitY)]);
        check(Bits(Applied(still, ResetLayer(MotionAxes.All, 1, 0, 30)), Applied(still)), "reset root: a root already at rest isn't touched");

        var layers = new[] { ResetLayer(MotionAxes.Y, 1, 0, 30) };
        var pose = Enumerable.Range(0, plain.Bones).Select(b => plain.Get(10, b)).ToArray();
        check(Near(MotionEvaluator.ApplyAt(Rig(), pose, layers, 10)[root], Applied(plain, layers).Get(10, root), 1e-5f),
            "reset root: one pose resets as the clip does, so Live shows what is saved");

        var rest = Q(Vector3.UnitX, -90);
        check(Degrees(MotionFixes.ResetTurn(rest * Q(Vector3.UnitY, 30), rest, MotionAxes.Y), rest) < RotationTolerance &&
              MotionFixes.ResetTurn(turned, rest, MotionAxes.None) == Quaternion.Normalize(turned),
            "reset root: the turn is measured from the root's rest, and no axes change nothing");

        BodyTurn(check);
    }

    /// <summary>
    /// A turn baked under the root, as saved poses have it: the root and n_hara at rest, the hips and spine turned 72 and 74
    /// degrees and swaying 5 degrees against each other, the hips on a tilted rest.
    /// </summary>
    private static void BodyTurn(Action<bool, string> check)
    {
        var rig = new SkeletonDescription("body-rig", "body-rig-fingerprint",
        [
            new("n_root", -1, 0, T(Vector3.Zero)), new("n_hara", 0, 0, T(Vector3.UnitY)),
            new("j_kosi", 1, 0, T(Vector3.Zero, Q(Vector3.UnitX, -90))), new("j_sebo_a", 1, 0, T(Vector3.Zero)),
            new("j_asi_a_l", 2, 0, T(Vector3.UnitX)),
        ], [], [], []);
        var hips = Q(Vector3.UnitX, -90);
        float Sway(float f) => 5 * MathF.Sin(f / 30 * 2 * MathF.PI);
        BoneTransform[] Pose(float turn, float f) =>
        [
            T(Vector3.Zero), T(new Vector3(.1f, 1, 0)),
            T(Vector3.Zero, Q(Vector3.UnitY, turn - 1 + Sway(f)) * hips * Q(Vector3.UnitY, 8)),
            T(Vector3.Zero, Q(Vector3.UnitY, turn + 1 - Sway(f))), T(Vector3.UnitX, Q(Vector3.UnitX, f)),
        ];
        MotionClip Made(float turn)
        {
            var poses = Enumerable.Range(0, 31).Select(i => Pose(turn, i)).ToArray();
            var made = MotionClip.FromPoses(rig, 30f / TimelineDocument.Fps, poses);
            var clip = new MotionClip(rig, made.Duration, made.Samples);
            clip.CopyFrom(made);
            return clip;
        }
        MotionClip Reset(MotionClip source, MotionAxes axes, float amount = 1)
        {
            var clip = source.Clone();
            MotionEvaluator.Apply(clip, [ResetLayer(axes, amount, 0, 30)]);
            return clip;
        }
        // Each bone's turn about Y in character space, from its rest there.
        float Facing(MotionClip clip, int i, int b)
        {
            Quaternion model = Quaternion.Identity, rest = Quaternion.Identity;
            for (var at = b; at >= 0; at = rig.Bones[at].Parent)
            {
                model = clip.Get(i, at).Rotation * model;
                rest = rig.Bones[at].Reference.Rotation * rest;
            }
            var turn = model * Quaternion.Inverse(rest);
            var twist = Quaternion.Normalize(new Quaternion(0, turn.Y, 0, turn.W));
            var degrees = 2 * MathF.Atan2(twist.Y, twist.W) * 180 / MathF.PI;
            return degrees > 180 ? degrees - 360 : degrees < -180 ? degrees + 360 : degrees;
        }

        var turned = Made(72);
        var reset = Reset(turned, MotionAxes.All);
        var facing = Enumerable.Range(0, reset.Samples).Select(i => (Facing(reset, i, 2) + Facing(reset, i, 3)) / 2).ToArray();
        var sway = Enumerable.Range(0, reset.Samples).All(i => MathF.Abs(Facing(reset, i, 2) - Facing(reset, i, 3) - (Facing(turned, i, 2) - Facing(turned, i, 3))) < RotationTolerance);
        check(facing.All(f => MathF.Abs(f) < .5f) && sway,
            "reset root: a turn under the root comes out of the hips and spine together, and their sway stays");
        var hara = reset.Get(10, 1);
        check(Enumerable.Range(0, reset.Samples).All(i => reset.Get(i, 0) == turned.Get(i, 0)) &&
              Enumerable.Range(2, 3).All(b => Enumerable.Range(0, reset.Samples).All(i => reset.Get(i, b) == turned.Get(i, b))) &&
              Vector3.Distance(hara.Position, Vector3.Transform(new Vector3(.1f, 1, 0), Q(Vector3.UnitY, -72))) < 1e-3f,
            "reset root: the body bone turns back, its spot with it, and the root and the bones below are left as they are");
        check(MathF.Abs((Facing(Reset(turned, MotionAxes.Y, .5f), 10, 2) + Facing(Reset(turned, MotionAxes.Y, .5f), 10, 3)) / 2 - 36) < .5f,
            "reset root: the amount blends the body's turn back");
        check(Bits(Reset(turned, MotionAxes.X | MotionAxes.Z), turned) && Bits(Reset(Made(0), MotionAxes.All), Made(0)),
            "reset root: no Y leaves the body's facing, and a body already facing forward isn't touched");
    }

    private static void Validation(Action<bool, string> check, Action<Action, string> reject)
    {
        var match = MatchLayer(Loop());
        MotionEvaluator.Validate(match);
        MotionEvaluator.Validate(SmoothLayer(.5f, 0, 30));
        reject(() => MotionEvaluator.Validate(match with { Fix = null }), "a fix layer without a fix is refused");
        reject(() => MotionEvaluator.Validate(new MotionLayer("m1", MotionLayerKind.Offset, "Offset 1", 0, 30, 0, 0, 1, PoseComponents.All,
            MotionInterpolation.Linear, Fix: new MotionFix(MotionFixKind.SmoothJitter))), "a fix on an offset layer is refused");
        reject(() => MotionEvaluator.Validate(match with { Keys = [new MotionKey(0, Loop().LoopFirst!)] }), "a fix layer with keys is refused");
        reject(() => MotionEvaluator.Validate(match with { Pins = [new MotionPin(B1)] }), "a fix layer with pins is refused");
        reject(() => MotionEvaluator.Validate(match with { Fix = new MotionFix(MotionFixKind.MatchLoop) }), "a match without a loop pose is refused");
        reject(() => MotionEvaluator.Validate(match with { Fix = Loop() with { LoopFirst = ImmutableDictionary<string, BoneTransform>.Empty } }),
            "a match with an empty loop pose is refused");
        reject(() => MotionEvaluator.Validate(match with { Fix = Loop() with { LoopSecond = Loop().LoopSecond!.Remove(B2) } }),
            "a match whose loop frames name different bones is refused");
        reject(() => MotionEvaluator.Validate(match with { Fix = Loop() with { LoopFirst = Loop().LoopFirst!.SetItem(B1, T(new Vector3(float.NaN))) } }),
            "a match whose loop pose isn't a transform is refused");
        reject(() => MotionEvaluator.Validate(match with { Fix = new MotionFix((MotionFixKind)9) }), "an unknown fix is refused");
        reject(() => MotionEvaluator.Validate(match with { Amount = 1.5f }), "a fix above 100% is refused");
        MotionEvaluator.Validate(match with { Fix = Loop() with { Fade = LoopFades.Most } });
        reject(() => MotionEvaluator.Validate(match with { Fix = Loop() with { Fade = -1 } }), "a match with a negative game fade is refused");
        reject(() => MotionEvaluator.Validate(match with { Fix = Loop() with { Fade = LoopFades.Most + 1 } }), "a match with a game fade past the longest is refused");
        reject(() => MotionEvaluator.Validate(SmoothLayer(.5f, 0, 30) with { Fix = new MotionFix(MotionFixKind.SmoothJitter, Fade: 10) }),
            "a smooth with a game fade is refused");
        check(MotionEvaluator.Active(match) && MotionEvaluator.Active(SmoothLayer(.5f, 0, 30)) && !MotionEvaluator.Active(SmoothLayer(0, 0, 30)) &&
              !MotionEvaluator.Active(match with { Fix = null }) && !MotionEvaluator.Active(match with { Components = PoseComponents.Scale }),
            "a fix layer is active with a fix, an amount and a rotation or position to change");
        MotionEvaluator.Validate(ResetLayer(MotionAxes.None, 1, 0, 30));
        reject(() => MotionEvaluator.Validate(SmoothLayer(.5f, 0, 30) with { Fix = new MotionFix(MotionFixKind.SmoothJitter, Axes: MotionAxes.Y) }),
            "a smooth with axes is refused");
        reject(() => MotionEvaluator.Validate(ResetLayer((MotionAxes)8, 1, 0, 30)), "a reset with an unknown axis is refused");
        check(MotionEvaluator.Active(ResetLayer(MotionAxes.Y, 1, 0, 30)) && !MotionEvaluator.Active(ResetLayer(MotionAxes.None, 1, 0, 30)) &&
              !MotionEvaluator.Active(ResetLayer(MotionAxes.Y, 1, 0, 30) with { Components = PoseComponents.Position }),
            "a reset is active with an axis and its turn to change");
    }

    private static void Journals(Action<bool, string> check)
    {
        var temp = Path.Combine(Path.GetTempPath(), "ie-fix-layer-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var resource = new AnimationResource("chara/human/c0801/animation/a0001/bt_common/emote/pose06_start.pap", "pose06_start.pap", "hash");
            var clip = new AnimationClip(resource.GamePath, "start", 0, 0, 42, "chara/test.sklb");
            var capture = new AnimationCapture("clip", 1, 1, Guid.NewGuid(), "Test", "Idle", clip, null, [resource.GamePath], [resource],
                new PoseSnapshot(0, 0, 0, false, [], DateTime.UtcNow, ""), DateTime.UtcNow, true);
            var match = MatchLayer(Loop() with { Fade = 20 });
            var smooth = SmoothLayer(.5f, 0, 30) with { Key = "m2" };
            var reset = ResetLayer(MotionAxes.X | MotionAxes.Z, 1, 0, 30) with { Key = "m3" };
            var request = new AnimationBakeRequest(Guid.NewGuid(), capture, AnimationDestination.NewMod, "Motion", false,
                ImmutableHashSet<PoseBoneId>.Empty, PoseComponents.None, AnimationOperation.EditTimeline,
                Timeline: new AnimationTimelineEdit(true, 0, null, [match, smooth, reset]));
            new AnimationJournalStore(temp).Save(new AnimationEditJournal { Id = request.Id, Request = AnimationCommitService.RecoveryRequest(request) });
            var loaded = new AnimationJournalStore(temp).Load().Single().Request.Timeline!.Motion;
            check(loaded.Length == 3 && MotionEdits.Same(loaded[0], match) && MotionEdits.Same(loaded[1], smooth) && MotionEdits.Same(loaded[2], reset) &&
                  loaded[0].Fix!.LoopFirst![B1] == match.Fix!.LoopFirst![B1] && loaded[0].Fix!.Fade == 20 && loaded[2].Fix!.Axes == (MotionAxes.X | MotionAxes.Z),
                "a journal keeps the fixes, the loop pose, the game fade and a reset's axes");
            var noFade = JsonNode.Parse(File.ReadAllText(Path.Combine(temp, "AnimationEdits", request.Id.ToString("N"), "journal.json")))!.AsObject();
            check(noFade["Request"]!["Timeline"]!["Motion"]![0]!["Fix"]!.AsObject().Remove("Fade"), "a journal stores a match's game fade as Fade");
            File.WriteAllText(Path.Combine(temp, "AnimationEdits", request.Id.ToString("N"), "journal.json"), noFade.ToJsonString());
            check(new AnimationJournalStore(temp).Load().Single().Request.Timeline!.Motion[0].Fix is { Fade: 0, LoopFirst: not null },
                "a match saved before game fades loads with 0, the loop's first frame");

            var path = Path.Combine(temp, "AnimationEdits", request.Id.ToString("N"), "journal.json");
            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            check(json["Request"]!["Timeline"]!["Motion"]![1]!.AsObject().Remove("Fix"), "a journal stores a layer's fix as Fix");
            File.WriteAllText(path, json.ToJsonString());
            var older = new AnimationJournalStore(temp).Load().Single().Request.Timeline!.Motion[1];
            check(older.Fix == null && !MotionEvaluator.Active(older), "a layer without Fix loads with none");
        }
        finally { Directory.Delete(temp, true); }

        var a = MatchLayer(Loop());
        check(MotionEdits.Same(a, a with { Fix = Loop() }) && !MotionEdits.Same(a, a with { Fix = Loop(shifted: true) }) &&
              !MotionEdits.Same(a, a with { Fix = new MotionFix(MotionFixKind.SmoothJitter) }) && !MotionEdits.Same(a, a with { Fix = null }) &&
              !MotionEdits.Same(a, a with { Fix = Loop() with { Fade = 10 } }) &&
              !MotionEdits.Same(ResetLayer(MotionAxes.Y, 1, 0, 30), ResetLayer(MotionAxes.All, 1, 0, 30)),
            "layers differ by their fix's kind, loop pose and game fade, compared by content");
    }

    private static void Edits(Action<bool, string> check)
    {
        var doc = new TimelineDocument(100, 100, null, [], 1);
        var loop = Loop();
        var withMatch = MotionEdits.AddFix(doc, loop, out var matchKey, out var error);
        check(error == null && MotionEdits.Find(withMatch, matchKey) is
              {
                  Kind: MotionLayerKind.Fix, Name: "Match loop", Start: 92, End: 100, FadeIn: 0, FadeOut: 0, Amount: 1,
                  Components: PoseComponents.Rotation | PoseComponents.Position,
              } match && ReferenceEquals(match.Fix, loop),
            "Match loop spans the clip's last 8 frames at full blend");
        var withSmooth = MotionEdits.AddFix(doc, new MotionFix(MotionFixKind.SmoothJitter), out var smoothKey, out error);
        check(error == null && MotionEdits.Find(withSmooth, smoothKey) is
              { Kind: MotionLayerKind.Fix, Name: "Smooth jitter", Start: 0, End: 100, FadeIn: 0, FadeOut: 0, Amount: .5f, Fix.Kind: MotionFixKind.SmoothJitter },
            "Smooth jitter spans the whole clip at half strength");
        check(MotionEdits.Find(MotionEdits.AddFix(doc, new MotionFix(MotionFixKind.ResetRoot, Axes: MotionAxes.All), out var resetKey, out error), resetKey) is
              { Name: "Reset root turn", Start: 0, End: 100, Amount: 1, Components: PoseComponents.Rotation, Fix.Axes: MotionAxes.All } && error == null,
            "Reset root turn spans the whole clip at full blend, turn only");
        check(MotionEdits.Find(MotionEdits.AddFix(doc with { Length = 5 }, Loop(), out var shortKey, out _), shortKey) is { Start: 0, End: 5 },
            "a match on a clip shorter than its blend starts at frame 0");

        var longer = MotionEdits.SetFixLength(withMatch, matchKey, 12);
        check(MotionEdits.Find(longer, matchKey) is { Start: 88, End: 100 } && MotionEdits.Find(MotionEdits.SetFixLength(withMatch, matchKey, 0), matchKey) is { Start: 99 } &&
              MotionEdits.Find(MotionEdits.SetFixLength(withMatch, matchKey, 500), matchKey) is { Start: 0, End: 100 } &&
              ReferenceEquals(MotionEdits.SetFixLength(withSmooth, smoothKey, 12), withSmooth) && ReferenceEquals(MotionEdits.SetFixLength(withMatch, matchKey, 8), withMatch),
            "Blend over sets a match's length back from the clip's end, and only a match's");

        var reread = MotionEdits.SetLoopPose(withMatch, matchKey, Loop(shifted: true));
        check(MotionEdits.Find(reread, matchKey)!.Fix!.LoopFirst![B1] == Loop(shifted: true).LoopFirst![B1] &&
              ReferenceEquals(MotionEdits.SetLoopPose(withSmooth, smoothKey, Loop()), withSmooth),
            "Re-read loop replaces a match's loop pose");
        check(MotionEdits.Find(MotionEdits.AddFix(doc, Loop() with { Fade = 20 }, out var fadedKey, out _), fadedKey) is { Start: 80, End: 100, Fix.Fade: 20 } &&
              MotionEdits.Find(MotionEdits.AddFix(doc, Loop() with { Fade = 5 }, out var shortFadeKey, out _), shortFadeKey) is { Start: 92, End: 100 },
            "a new match blends over its game fade when that is longer than 8 frames");
        var refaded = MotionEdits.SetLoopPose(withMatch, matchKey, Loop(shifted: true) with { Fade = 20 });
        check(MotionEdits.Find(refaded, matchKey) is { Start: 80, End: 100, Fix.Fade: 20 } &&
              MotionEdits.Find(MotionEdits.SetLoopPose(longer, matchKey, Loop() with { Fade = 4 }), matchKey) is { Start: 88, End: 100, Fix.Fade: 4 },
            "a loop read for another game fade takes it, and a blend shorter than it grows to it");
        check(MotionEdits.KindName(MotionLayerKind.Fix) == "Fix" && MotionEdits.MaxAmount(MotionLayerKind.Fix) == 1, "the kind is named Fix and blends at most fully");

        // The canvas: a match's bar and end stay on the clip's last frame, its start sets its length.
        check(ReferenceEquals(MotionEdits.Move(withMatch, matchKey, -5), withMatch) && ReferenceEquals(MotionEdits.Resize(withMatch, matchKey, false, -5), withMatch) &&
              ReferenceEquals(MotionEdits.Fade(withMatch, matchKey, true, 3), withMatch) &&
              MotionEdits.Find(MotionEdits.Resize(withMatch, matchKey, true, -4), matchKey) is { Start: 88, End: 100 },
            "a match's bar and end don't move; dragging its start sets its length");
        var stale = withMatch with { Length = 50 };
        check(MotionEdits.Shown(MotionEdits.Find(stale, matchKey)!, stale.Length) is { Start: 42, End: 50, FadeIn: 0, FadeOut: 0 } &&
              MotionEdits.Find(MotionEdits.Resize(stale, matchKey, true, -2), matchKey) is { Start: 40, End: 50 } &&
              MotionEdits.SnapFrames(stale, null).SequenceEqual([42, 50]) &&
              MotionEdits.Shown(MotionEdits.Find(withSmooth, smoothKey)!, 100) is { Start: 0, End: 100 },
            "a match shows, snaps and drags over the clip's last frames whatever it stores");

        // Cuts: a match stays on the clip's last frame with its length; a smooth inside the removed frames still goes.
        check(CutEdits.RemoveRange(withMatch, 95, 100, out _) is { Length: 95 } tail && MotionEdits.Find(tail, matchKey) is { Start: 87, End: 95 } &&
              CutEdits.RemoveRange(withMatch, 90, 100, out _) is { Length: 90 } over && MotionEdits.Find(over, matchKey) is { Start: 82, End: 90 } &&
              CutEdits.RemoveRange(withMatch, 40, 60, out _) is { Length: 80 } middle && MotionEdits.Find(middle, matchKey) is { Start: 72, End: 80 },
            "removing the clip's last frames keeps a match on the new end with its length");
        check(CutEdits.RemoveRange(withMatch, 5, 100, out _) is { Length: 5 } shortCut && MotionEdits.Find(shortCut, matchKey) is { Start: 0, End: 5 },
            "a cut shorter than the blend starts the match at frame 0");
        check(CutEdits.Ripple(withMatch, 95, 10, out _) is { Length: 110 } inserted && MotionEdits.Find(inserted, matchKey) is { Start: 102, End: 110 },
            "frames inserted inside a match's blend keep its length and end");
        check(CutEdits.StretchPart(withMatch, 0, 20, out _) is { Length: 120 } stretched && MotionEdits.Find(stretched, matchKey) is { Start: 112, End: 120 },
            "stretching the clip keeps a match's length and end");
        var copied = MotionEdits.Find(withMatch, matchKey)!;
        check(MotionEdits.Paste(new TimelineDocument(50, 50, null, [], 1), [copied], 100, out var pasted, out _) is var into &&
              MotionEdits.Find(into, pasted[0]) is { Start: 42, End: 50 } &&
              MotionEdits.Paste(new TimelineDocument(5, 5, null, [], 1), [copied], 100, out var pastedShort, out _) is var intoShort &&
              MotionEdits.Find(intoShort, pastedShort[0]) is { Start: 0, End: 5 },
            "a match pasted into a shorter clip keeps its length and ends on that clip's end, from frame 0 when shorter than the blend");
        var smoothTail = withSmooth with { Motion = [MotionEdits.Find(withSmooth, smoothKey)! with { Start = 92, End = 100 }] };
        check(CutEdits.RemoveRange(smoothTail, 90, 100, out _).Motion.IsEmpty, "a smooth inside the removed frames is still removed");

        var editor = new TimelineEditor(doc);
        check(editor.AddFix(new MotionFix(MotionFixKind.SmoothJitter)) && MotionEdits.Find(editor.Document, editor.SelectedLayer) is { Name: "Smooth jitter" } &&
              editor.Undo() && editor.Document.Motion.IsEmpty, "the editor adds a fix as one undo step and selects it");
        editor.AddFix(loop);
        var key = editor.SelectedLayer!;
        check(editor.SetFixLength(key, 20) && MotionEdits.Find(editor.Document, key) is { Start: 80, End: 100 } && editor.SelectedLayer == key &&
              editor.Undo() && MotionEdits.Find(editor.Document, key) is { Start: 92, End: 100 }, "Blend over is one undo step");
        check(editor.SetLoopPose(key, Loop(shifted: true)) && editor.Undo() && ReferenceEquals(MotionEdits.Find(editor.Document, key)!.Fix, loop),
            "Re-read loop is one undo step");
    }

    private static void Views(Action<bool, string> check)
    {
        var match = MatchLayer(Loop());
        check(!TimelineWindowViews.LayerKinds.Contains(MotionLayerKind.Fix) && TimelineWindowViews.LayerKinds.Last() == MotionLayerKind.Pin &&
              TimelineUi.LayerIcons.Length > (int)MotionLayerKind.Fix && TimelineUi.LayerIcon(MotionLayerKind.Fix) != TimelineUi.LayerIcon(MotionLayerKind.Pin) &&
              TimelineWindowViews.LayerColour(MotionLayerKind.Fix) != TimelineWindowViews.LayerColour(MotionLayerKind.Pose) &&
              TimelineWindowViews.LayerKindHint(MotionLayerKind.Fix) != TimelineWindowViews.LayerKindHint(MotionLayerKind.Pose),
            "Fix has its own icon, colour and hint, and is added only by its own items");
        check(TimelineWindowViews.DragGizmo(match, B1, null) == "This layer doesn't turn bones." && !TimelineLayout.Unfolds(match) &&
              TimelineWindowViews.LayerContent(match) == "Every bone" && TimelineWindowViews.LayerContent(match with { Bones = [B1] }) == "1 bone",
            "a fix layer has no gizmo, no bone rows and lists the bones it is limited to");
        var smooth = SmoothLayer(1, 0, 30) with { Key = "s1" };
        var offset = new MotionLayer("o1", MotionLayerKind.Offset, "Offset", 0, 30, 0, 0, 1, PoseComponents.Rotation, MotionInterpolation.Smooth);
        check(BonePosing.Earlier([smooth, match, offset], 2) is [var kept] && ReferenceEquals(kept, smooth) && BonePosing.TakesWholeClip([smooth]) &&
              BonePosing.Earlier([match, offset], 1) is [] && !BonePosing.TakesWholeClip([offset]),
            "the gizmo's frames bake the whole-clip layers before the posed one, but never a match, which lands after every layer");
    }

    /// <summary>The loop read for Match startup to loop and Re-read loop: one at a time, dropped when the clip or the layer goes.</summary>
    private static void Reader(Action<bool, string> check)
    {
        var source = BlenderMotionFixture.Source() with { Startup = true };
        var session = new TimelineSession(null, null!, null!, null!, null, null!, null!, null!) { Source = source };
        var editor = new TimelineEditor(source.Document!);
        var reader = new LoopPoseReader(session);
        var pending = new TaskCompletionSource<MotionFix>();
        var calls = 0;
        reader.Read(source, null, _ => pending.Task);
        reader.Read(source, null, _ =>
        {
            calls++;
            return pending.Task;
        });
        reader.Poll(editor);
        check(reader.Reading && calls == 0 && editor.Document.Motion.IsEmpty, "one loop read at a time; nothing is added while it runs");
        pending.SetResult(Loop());
        reader.Poll(editor);
        check(!reader.Reading && MotionEdits.Find(editor.Document, editor.SelectedLayer) is { Fix.Kind: MotionFixKind.MatchLoop }, "a finished loop read adds a match and selects it");
        var key = editor.SelectedLayer!;

        reader.Read(source, key, _ => Task.FromResult(Loop(shifted: true)));
        reader.Poll(editor);
        check(MotionEdits.Find(editor.Document, key)!.Fix!.LoopFirst![B1] == Loop(shifted: true).LoopFirst![B1], "a re-read replaces the match's loop pose");

        var before = editor.Document;
        reader.Read(source, key, _ => Task.FromResult(Loop()));
        session.Source = source with { Startup = false };
        reader.Poll(editor);
        session.Source = source;
        check(ReferenceEquals(editor.Document, before) && !reader.Reading, "a read for a clip no longer open is dropped");

        editor.Commit(MotionEdits.Remove(editor.Document, key));
        before = editor.Document;
        reader.Read(source, key, _ => Task.FromResult(Loop()));
        reader.Poll(editor);
        check(ReferenceEquals(editor.Document, before), "a re-read for a deleted layer is dropped");

        reader.Read(source, null, _ => Task.FromException<MotionFix>(new InvalidDataException("No file.")));
        reader.Poll(editor);
        check(editor.LastError == "Couldn't read the loop: No file." && ReferenceEquals(editor.Document, before), "a failed loop read says why");

        CancellationToken seen = default;
        reader.Read(source, null, token =>
        {
            seen = token;
            return new TaskCompletionSource<MotionFix>().Task;
        });
        reader.Drop();
        check(seen.IsCancellationRequested && !reader.Reading, "Drop stops the read");
    }

    // A root and two bones under it.
    private static SkeletonDescription Rig() => new("fix-rig", "fix-rig-fingerprint",
        [new("n_root", -1, 0, T(Vector3.Zero)), new(B1, 0, 0, T(Vector3.UnitX)), new(B2, 0, 0, T(Vector3.UnitY))], [], [], []);

    private static int Index(string bone) => MotionEvaluator.Index(Rig())[bone];

    /// <summary>
    /// A startup <paramref name="frames"/> frames long in <paramref name="samples"/> samples: b1 turns 1 degree per
    /// frame about Y and slides 0.01 a frame along X, b2 turns half a degree per frame about X and rises.
    /// </summary>
    private static MotionClip Startup(int samples, float frames) => Clip(samples, frames, (_, f) =>
    [
        T(Vector3.Zero),
        T(new Vector3(.01f * f, 0, 0), Q(Vector3.UnitY, f)),
        T(new Vector3(0, .005f * f, 0), Q(Vector3.UnitX, .5f * f)),
    ]);

    /// <summary>
    /// A loop whose first frame is 20 degrees on from where <see cref="Startup"/> ends and that moves 2 degrees a
    /// frame; with <paramref name="ghost"/> it names a bone the rig lacks.
    /// </summary>
    internal static MotionFix Loop(bool ghost = false, bool shifted = false)
    {
        var first = new Dictionary<string, BoneTransform>(StringComparer.Ordinal)
        {
            ["n_root"] = T(Vector3.Zero),
            [B1] = T(new Vector3(.5f, .1f, 0), Q(Vector3.UnitY, shifted ? 60 : 50)),
            [B2] = T(new Vector3(0, .2f, 0), Q(Vector3.UnitX, 30)),
        };
        var second = new Dictionary<string, BoneTransform>(StringComparer.Ordinal)
        {
            ["n_root"] = T(Vector3.Zero),
            [B1] = T(new Vector3(.502f, .101f, 0), Q(Vector3.UnitY, shifted ? 62 : 52)),
            [B2] = T(new Vector3(0, .2f, 0), Q(Vector3.UnitX, 31)),
        };
        if (ghost)
            first["j_ghost"] = second["j_ghost"] = T(Vector3.One);
        return new MotionFix(MotionFixKind.MatchLoop, ImmutableDictionary.CreateRange(StringComparer.Ordinal, first),
            ImmutableDictionary.CreateRange(StringComparer.Ordinal, second));
    }

    /// <summary>A loop pose that carries on <paramref name="clip"/>'s motion from its last sample, one sample a frame.</summary>
    private static MotionFix Continuing(MotionClip clip)
    {
        var last = clip.Samples - 1;
        var first = ImmutableDictionary.CreateBuilder<string, BoneTransform>(StringComparer.Ordinal);
        var second = ImmutableDictionary.CreateBuilder<string, BoneTransform>(StringComparer.Ordinal);
        for (var b = 0; b < clip.Bones; b++)
        {
            var (cur, prev) = (clip.Get(last, b), clip.Get(last - 1, b));
            var name = Rig().Bones[b].Name;
            first[name] = cur.ToBone();
            second[name] = new BoneTransform(cur.Position + (cur.Position - prev.Position),
                Quaternion.Normalize(cur.Rotation * Quaternion.Inverse(prev.Rotation) * cur.Rotation), cur.Scale);
        }
        return new MotionFix(MotionFixKind.MatchLoop, first.ToImmutable(), second.ToImmutable());
    }

    private static MotionLayer MatchLayer(MotionFix fix) =>
        new("m1", MotionLayerKind.Fix, "Match loop", 22, 30, 0, 0, 1, PoseComponents.Rotation | PoseComponents.Position, MotionInterpolation.Smooth, Fix: fix);

    private static MotionLayer SmoothLayer(float amount, int start, int end) =>
        new("m1", MotionLayerKind.Fix, "Smooth jitter", start, end, 0, 0, amount, PoseComponents.Rotation | PoseComponents.Position,
            MotionInterpolation.Smooth, Fix: new MotionFix(MotionFixKind.SmoothJitter));

    private static MotionLayer ResetLayer(MotionAxes axes, float amount, int start, int end) =>
        new("m1", MotionLayerKind.Fix, "Reset root turn", start, end, 0, 0, amount, PoseComponents.Rotation,
            MotionInterpolation.Smooth, Fix: new MotionFix(MotionFixKind.ResetRoot, Axes: axes));

    /// <summary>
    /// 61 samples over 60 frames: b1 sweeps 30 degrees about Y and 0.1 along X with a 1-degree, 2 mm shake
    /// each sample; b2 steps 30 degrees at sample 30 with the same shake.
    /// </summary>
    private static MotionClip Noisy() => Clip(61, 60, (i, _) =>
    {
        var shake = i % 2 == 0 ? 1 : -1;
        return
        [
            T(Vector3.Zero),
            T(new Vector3(.1f * i / 60 + .002f * shake, 0, 0), Q(Vector3.UnitY, 30 * AnimationPoseRules.SmoothStep(i / 60f) + shake)),
            T(Vector3.UnitY, Q(Vector3.UnitY, (i >= 30 ? 30 : 0) + shake)),
        ];
    });

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

    private static bool Lands(MotionClip clip, MotionFix fix, string bone)
    {
        var value = clip.Get(clip.Samples - 1, Index(bone));
        var target = fix.LoopFirst![bone];
        return Degrees(value.Rotation, target.Rotation) < RotationTolerance && Vector3.Distance(value.Position, target.Position) < PositionTolerance;
    }

    /// <summary>Whether the last sample pair, times <paramref name="perFrame"/>, moves as the loop's frames 0 to 1 do.</summary>
    private static bool Speed(MotionClip clip, int perFrame, MotionFix fix, string bone, float degrees, float distance)
    {
        var (cur, prev) = (clip.Get(clip.Samples - 1, Index(bone)), clip.Get(clip.Samples - 2, Index(bone)));
        var (first, second) = (fix.LoopFirst![bone], fix.LoopSecond![bone]);
        var turn = (AboutY(cur.Rotation) - AboutY(prev.Rotation)) * perFrame;
        var move = (cur.Position - prev.Position) * perFrame;
        return MathF.Abs(turn - (AboutY(second.Rotation) - AboutY(first.Rotation))) < degrees &&
               Vector3.Distance(move, second.Position - first.Position) < distance;
    }

    private static bool Before(MotionClip a, MotionClip b, int frame) =>
        Enumerable.Range(0, a.Samples).Where(i => a.Frame(i) < frame).All(i => Enumerable.Range(0, a.Bones).All(bone => a.Get(i, bone) == b.Get(i, bone)));

    private static bool Bits(MotionClip a, MotionClip b) =>
        Enumerable.Range(0, a.Samples).All(i => Enumerable.Range(0, a.Bones).All(bone => a.Get(i, bone) == b.Get(i, bone)));

    private static MotionTransform[] Track(MotionClip clip, string bone) => [.. Enumerable.Range(0, clip.Samples).Select(i => clip.Get(i, Index(bone)))];

    /// <summary>The sum of squared second differences of a bone's angle about Y.</summary>
    private static float Energy(MotionClip clip, int bone) => Enumerable.Range(1, clip.Samples - 2).Sum(i =>
        MathF.Pow(AboutY(clip.Get(i + 1, bone).Rotation) - 2 * AboutY(clip.Get(i, bone).Rotation) + AboutY(clip.Get(i - 1, bone).Rotation), 2));

    private static float PositionEnergy(MotionClip clip, int bone) => Enumerable.Range(1, clip.Samples - 2).Sum(i =>
        (clip.Get(i + 1, bone).Position - 2 * clip.Get(i, bone).Position + clip.Get(i - 1, bone).Position).LengthSquared());

    private static bool Same(MotionTransform a, MotionTransform b) => a == b;

    /// <summary>Whether two transforms differ by at most <paramref name="tolerance"/> in any part, a rotation and its negation alike.</summary>
    private static bool Near(MotionTransform a, MotionTransform b, float tolerance) =>
        Vector3.Distance(a.Position, b.Position) <= tolerance && Vector3.Distance(a.Scale, b.Scale) <= tolerance &&
        (Quaternion.Dot(a.Rotation, b.Rotation) >= 0 ? a.Rotation - b.Rotation : a.Rotation + b.Rotation).Length() <= tolerance;

    private static bool Finite(MotionTransform value) =>
        float.IsFinite(value.Rotation.X) && float.IsFinite(value.Rotation.Y) && float.IsFinite(value.Rotation.Z) && float.IsFinite(value.Rotation.W) &&
        float.IsFinite(value.Position.X) && float.IsFinite(value.Position.Y) && float.IsFinite(value.Position.Z);

    /// <summary>The signed angle of a rotation about Y, in degrees.</summary>
    private static float AboutY(Quaternion q) => 2 * MathF.Atan2(q.W < 0 ? -q.Y : q.Y, MathF.Abs(q.W)) * 180 / MathF.PI;

    private static Quaternion Q(Vector3 axis, float degrees) => Quaternion.CreateFromAxisAngle(axis, degrees * MathF.PI / 180);

    private static float Degrees(Quaternion a, Quaternion b) =>
        2 * MathF.Acos(Math.Clamp(Math.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))), 0, 1)) * 180 / MathF.PI;

    private static BoneTransform T(Vector3 position, Quaternion? rotation = null) => new(position, rotation ?? Quaternion.Identity, Vector3.One);
}
