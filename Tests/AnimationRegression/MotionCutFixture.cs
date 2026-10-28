using System.Numerics;
using System.Text.Json;
using InstantEdit.Models;
using InstantEdit.Services.Animations.Motion;

/// <summary>
/// Cutting a clip's motion: the cut list and its time map, the offset blends at joins and the loop
/// seam (<see cref="MotionCutter"/>), and the seam check (<see cref="LoopSeam"/>).
/// </summary>
internal static class MotionCutFixture
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Model(check, reject);
        Cutter(check);
        Seams(check);
    }

    private static void Seams(Action<bool, string> check)
    {
        var clean = LoopSeam.Analyze(Spin(101, f => 3.6f * f), 0, 100);   // ends at 360 = start
        check(clean is { State: SeamState.Clean, StallFrom: null } && clean.Gap.MeanAngle < .01f, "a loop that meets itself is clean");
        var pops = LoopSeam.Analyze(Spin(101, f => 3f * f), 0, 100);       // ends at 300
        check(pops.State == SeamState.Pops && Math.Abs(pops.Gap.MeanAngle - 30) < .5f, "a pose gap pops");   // mean over 2 bones: 60 and 0
        // One arm a layer turns 25 degrees among a hundred tracks: a 0.26 degree mean.
        check(LoopSeam.Pops(new JoinGap(.26f, 25, "j_ude_a_r", 1, 1)) && !LoopSeam.Pops(new JoinGap(.5f, 5, "j_ude_a_r", 1, 1)),
            "one bone's big gap pops though the mean is low; small gaps don't");
        // A swing that, from its turn at frame 88, eases into frame 0's pose and slows to a stop there, then
        // leaves at full speed: the stop is the motion arriving, so there is nothing to trim (the TikTok dance).
        var stall = LoopSeam.Analyze(EaseIntoStart(101), 0, 100);
        check(stall is { State: SeamState.Stalls, StallFrom: null }, "a motion easing into frame 0's pose stalls, with nothing to trim");
        // A full turn by frame 85, then held on frame 0's pose to the end.
        var hold = LoopSeam.Analyze(Spin(101, f => f <= 85 ? 360f * f / 85 : 360), 0, 100);
        check(hold is { State: SeamState.Stalls, StallFrom: >= 84 and <= 86 }, "an end holding frame 0's pose stalls from where the hold starts");
        check(LoopSeam.Speeds(Spin(101, f => 3f * f))[10] is > 1.4f and < 1.6f, "speeds average the tracked bones");  // 3 and 0
        // Idles and poses barely move: their noise is no stall, though a real gap still pops.
        var still = LoopSeam.Analyze(Spin(101, f => f <= 90 ? .2f * f : 18), 0, 100);
        check(still.State == SeamState.Pops && still.StallFrom == null, "a loop that barely moves never stalls");
        // Out from frame 0 and back in, slowing at both ends: it wraps smoothly, and nothing is trimmed.
        var swing = LoopSeam.Analyze(Spin(101, f => 90 * MathF.Pow(MathF.Sin(MathF.PI * f / 100), 2)), 0, 100);
        check(swing is { State: SeamState.Clean, StallFrom: null }, "a loop easing out and back in is clean, not a stall to trim");
    }

    private static float Smooth(float t) => t * t * (3 - 2 * t);

    /// <summary>The 60-degree swing that eases into frame 0's pose from frame 88, <paramref name="samples"/> samples over 100 frames.</summary>
    private static MotionClip EaseIntoStart(int samples) =>
        SpinAt(samples, 100, f => 60 * MathF.Sin(2 * MathF.PI * f / 50) * (1 - Smooth(Math.Clamp((f - 88) / 12f, 0, 1))));

    private static void Cutter(Action<bool, string> check)
    {
        var source = Spin(101, f => 3f * f);                                // 100 frames, 3 deg per frame
        var cut = MotionCutter.Apply(source, new MotionCuts(100, [new(0, 40), new(60, 100)], [MotionJoin.ForCut]), 80);
        check(cut.Samples == 81 && Math.Abs(cut.Duration - 80 / 30f) < 1e-6, "80 output frames, one sample each");
        check(Near(Angle(cut, 10), 30) && Near(Angle(cut, 50), 210) && Near(Angle(cut, 33), 99) && Near(Angle(cut, 47), 201),
            "frames outside the 12-frame window are the time map's");
        check(Math.Abs(Angle(cut, 40) - (Angle(cut, 39) + Angle(cut, 41)) / 2) < 1 && Near(Angle(cut, 34) - Angle(cut, 33), 3, .05f) &&
              Near(Angle(cut, 47) - Angle(cut, 46), 3, .05f), "the join has no pop and leaves its window at the source's speed");
        // Speed matching is checked on a clip sampled 10 times per frame, so finite differences measure speed
        // and not the curvature a 100-degree gap adds over one whole frame.
        var accel = SpinAt(1001, 100, f => .05f * f * f);
        var joined = MotionCutter.Apply(accel, new MotionCuts(100, [new(0, 40), new(60, 100)], [new MotionJoin(12, JoinPlacement.After)]), 80);
        check(joined.Samples == 801 && Math.Abs((AngleS(joined, 400) - AngleS(joined, 399)) - (AngleS(joined, 401) - AngleS(joined, 400))) < .01f,
            "After: the speed into the join matches the speed out of it");
        check(Near(AngleS(joined, 390), AngleS(accel, 390)), "After leaves the left part as it was");
        var fine = SpinAt(1001, 100, f => 3f * f);
        var seam = MotionCutter.Apply(fine, new MotionCuts(100, [new(0, 100)], [], MotionJoin.ForSeam), 100);
        check(Degrees(seam.Get(1000, 1).Rotation, fine.Get(0, 1).Rotation) < 1e-3f && Near(AngleS(seam, 500), 150),
            "a seam's last sample equals the first, and samples before its window stay");
        check(Near(AngleS(seam, 1000) - AngleS(seam, 999), .3f, .01f), "the seam arrives at frame 0's speed (0.3 degrees per tenth of a frame)");
        // The 4-frame part takes 54 degrees of correction from both joins, so even with pose and speed matched
        // at each join its whole-frame second difference is 2.3 degrees; a pop would be 24 to 30.
        var tight = MotionCutter.Apply(source, new MotionCuts(100, [new(0, 40), new(60, 64), new(80, 100)], [MotionJoin.ForCut, MotionJoin.ForCut]), 64);
        check(Math.Abs(Angle(tight, 40) - (Angle(tight, 39) + Angle(tight, 41)) / 2) < 4 &&
              Math.Abs(Angle(tight, 44) - (Angle(tight, 43) + Angle(tight, 45)) / 2) < 4, "a 4-frame part between two joins stays continuous");
        var tightCuts = new MotionCuts(100, [new(0, 40), new(60, 64), new(80, 100)], [MotionJoin.ForCut, MotionJoin.ForCut]);
        check(MotionCutter.Window(tightCuts, 0, 64) == (6, 4), "a centered 12-frame blend keeps 6 frames before and only 4 on the short part");
        var fast = SpinAt(201, 100, f => 3f * f);                           // 100 frames at 2 samples per frame
        check(MotionCutter.OutputSamples(fast, 80) == 161, "a 60 fps clip keeps its rate");
        var longer = MotionCutter.Apply(source, new MotionCuts(101, [new(0, 101)], []), 101);
        check(Near(Angle(longer, 101), Angle(source, 100)), "a part one frame past the motion samples its last frame");
        var floats = MotionCutter.Apply(WithFloats(source, f => f), new MotionCuts(100, [new(0, 40), new(60, 100)], [new MotionJoin(0, JoinPlacement.Centered)]), 80);
        check(floats.TrackFloats![50] == 70, "float tracks are cut with the motion");
        check(MotionCutter.Rise(1) == 1 && MotionCutter.RiseSpeed(1) == 0 && MotionCutter.Fall(0) == 1 && MotionCutter.FallSpeed(1) == 0,
            "the quintic bases end where they should");
        Ease(check);
        Blend(check);
        Stretch(check);
        // Edit again of a cut edit: the game plays the saved, shorter file while Live decoded the motion from before.
        check(InstantEdit.Services.Animations.Preview.MotionLiveDisplay.LengthFits(100 / 30f, 100 / 30f, null) &&
              InstantEdit.Services.Animations.Preview.MotionLiveDisplay.LengthFits(80 / 30f, 100 / 30f, 80 / 30f) &&
              !InstantEdit.Services.Animations.Preview.MotionLiveDisplay.LengthFits(80 / 30f, 100 / 30f, null),
            "Live takes a playing file of the decoded motion's length, or of the saved edit's");
    }

    private static void Blend(Action<bool, string> check)
    {
        // Speeding up from 0 (0.1 degrees per frame more each frame), with a 30-degree bump over frames 40 to 60
        // to blend away, at 10 samples per frame.
        var source = SpinAt(1001, 100, f => .05f * f * f + (f is > 40 and < 60 ? 30 * MathF.Pow(MathF.Sin(MathF.PI * (f - 40) / 20), 2) : 0));
        var cuts = new MotionCuts(100, [new(0, 40), new(40, 60, Blend: true), new(60, 100)], [MotionJoin.ForCut, MotionJoin.ForCut]);
        var blended = MotionCutter.Apply(source, cuts, 100);
        check(blended.Samples == 1001 && Near(AngleS(blended, 399), AngleS(source, 399)) && Near(AngleS(blended, 600), AngleS(source, 600)) &&
              Near(AngleS(blended, 395), AngleS(source, 395)) && Near(AngleS(blended, 605), AngleS(source, 605)),
            "a blend part keeps the length, and the parts on both sides play as they are");
        check(Near(AngleS(blended, 400), 80) && Math.Abs(AngleS(blended, 500) - AngleS(source, 500)) > 20 &&
              AngleS(blended, 500) is > 80 and < 180, "the blend leaves the part before's end pose and drops the bump in between");
        float Step(int sample) => AngleS(blended, sample + 1) - AngleS(blended, sample);
        check(Math.Abs(Step(399) - Step(400)) < .02f && Math.Abs(Step(598) - Step(600)) < .02f,
            "the blend leaves and arrives at the speeds of the parts on both sides");
        check(MotionCutter.Apply(source, cuts with { Parts = [new(0, 40), new(40, 60), new(60, 100)] }, 100) is var plain &&
              Near(AngleS(plain, 500), AngleS(source, 500)), "without the blend the part plays its own motion");
        // A 40-frame blend over the 20 frames it replaced: longer, and the part after plays from frame 60 still.
        var longer = MotionCutter.Apply(source, cuts with { Parts = [new(0, 40), new(40, 80, Blend: true), new(60, 100)] }, 120);
        check(longer.Samples == 1201 && Near(AngleS(longer, 400), 80) && Near(AngleS(longer, 800), AngleS(source, 600)) &&
              Math.Abs(AngleS(longer, 802) - AngleS(longer, 801) - (AngleS(longer, 799) - AngleS(longer, 798))) < .02f,
            "a blend longer than the frames it replaced plays over its own length and arrives on the part after");
    }

    private static void Stretch(Action<bool, string> check)
    {
        // 3 degrees per frame at 10 samples per frame; the middle part plays at a third of the speed.
        var source = SpinAt(1001, 100, f => 3f * f);
        var slow = new MotionCuts(100, [new(0, 40), new(40, 60, Stretch: 3), new(60, 100)], [new MotionJoin(0, JoinPlacement.Centered), new MotionJoin(0, JoinPlacement.Centered)]);
        var played = MotionCutter.Apply(source, slow, 140);
        check(played.Samples == 1401 && Near(AngleS(played, 300), AngleS(source, 300)) && Near(AngleS(played, 1100), AngleS(source, 700)),
            "parts around a slowed part play as before, the later one shifted by 40 frames");
        check(Near(AngleS(played, 701) - AngleS(played, 700), .1f, .005f), "the slowed part turns a third as fast");
        var blended = MotionCutter.Apply(source, slow with { Joins = [MotionJoin.ForCut, MotionJoin.ForCut] }, 140);
        float Step(int s) => AngleS(blended, s + 1) - AngleS(blended, s);
        check(Near(Step(339), .3f, .01f) && Near(Step(459), .1f, .01f), "a centred blend into a slowed part leaves at full speed and arrives at its speed");
        check(Math.Abs(Step(399) - Step(400)) < .01f && Near(Step(400), .2f, .01f), "both sides meet the join at one speed, between the two parts'");
        var tiny = new MotionCuts(100, [new(0, 40), new(40, 42, Stretch: 3), new(42, 100)], [MotionJoin.ForCut, MotionJoin.ForCut]);
        var tight = MotionCutter.Apply(source, tiny, 104);
        check(MotionCutter.Window(tiny, 0, 104).After <= 6 && Math.Abs(AngleS(tight, 401) - 2 * AngleS(tight, 400) + AngleS(tight, 399)) < .1f,
            "a blend into a 2-frame slowed part stays inside it and doesn't pop");
    }

    private static void Ease(Action<bool, string> check)
    {
        // The time map: starts at the rate given, ends on time at the rate 1, never runs backwards.
        var rising = true;
        foreach (var rate in new[] { 0, .12, 1, 2.5 })
            for (var i = 1; i <= 300; i++)
                rising &= MotionCutter.EaseTime(i / 10.0, 30, rate) > MotionCutter.EaseTime((i - 1) / 10.0, 30, rate);
        check(MotionCutter.EaseTime(0, 30, .12) == 0 && Near((float)MotionCutter.EaseTime(30, 30, .12), 30) &&
              Near((float)(MotionCutter.EaseTime(.001, 30, .12) / .001), .12f, .005f) &&
              Near((float)((30 - MotionCutter.EaseTime(29.999, 30, .12)) / .001), 1, .005f) && rising,
            "an ease's time map starts at its rate, ends on time at normal speed and never runs backwards");
        check(MotionCutter.EaseTime(45, 30, .12) == 45, "the time map leaves frames after the ease alone");

        // The swing easing into frame 0 at 10 samples per frame: it arrives at a near stop and leaves at 7.5
        // degrees per frame. Eased over 30 frames, the start leaves at the end's speed and catches up.
        var source = EaseIntoStart(1001);
        var eased = MotionCutter.Apply(source, new MotionCuts(100, [new(0, 100)], [], null, 30), 100);
        check(eased.Samples == 1001 && Near(AngleS(eased, 0), AngleS(source, 0)), "an eased loop keeps its length and still starts on frame 0's pose");
        check(Math.Abs(AngleS(eased, 1) - AngleS(eased, 0)) < .1f * Math.Abs(AngleS(source, 1) - AngleS(source, 0)),
            "an eased start leaves at the speed the end arrives at");
        check(Near(AngleS(eased, 300), AngleS(source, 300)) && Near(AngleS(eased, 600), AngleS(source, 600)),
            "from the ease's last frame on the motion is the source's");

        // Its mirror: eased out of frame 0's pose, arriving back at full speed. The end eases instead.
        var fastEnd = SpinAt(1001, 100, f => 60 * MathF.Sin(2 * MathF.PI * f / 50) * Smooth(Math.Clamp(f / 12f, 0, 1)));
        var slowed = MotionCutter.Apply(fastEnd, new MotionCuts(100, [new(0, 100)], [], null, 30, EaseEnd: true), 100);
        check(Math.Abs(AngleS(slowed, 1000) - AngleS(slowed, 999)) < .1f * Math.Abs(AngleS(fastEnd, 1000) - AngleS(fastEnd, 999)) &&
              Near(AngleS(slowed, 1000), AngleS(fastEnd, 1000)), "an eased end arrives at the start's speed, on frame 0's pose");
        check(Near(AngleS(slowed, 700), AngleS(fastEnd, 700)) && Near(AngleS(slowed, 300), AngleS(fastEnd, 300)),
            "before an end's ease the motion is the source's");

        // Slowing into a 10-degree gap and leaving fast, with a seam blend and an eased start: the blend
        // aims at the start's eased speed, so the wrap keeps its speed either way the blend is placed.
        var gapped = SpinAt(1001, 100, f => 60 * MathF.Sin(2 * MathF.PI * f / 50) * (1 - Smooth(Math.Clamp((f - 88) / 12f, 0, 1))) +
                                             10 * Smooth(Math.Clamp((f - 88) / 12f, 0, 1)));
        foreach (var place in new[] { JoinPlacement.Before, JoinPlacement.After })
        {
            var both = MotionCutter.Apply(gapped, new MotionCuts(100, [new(0, 100)], [], new MotionJoin(12, place), 30), 100);
            float into = AngleS(both, 1000) - AngleS(both, 999), outOf = AngleS(both, 1) - AngleS(both, 0);
            check(Math.Abs(into - outOf) < .05f && outOf > -.01f, $"a seam blend placed {place} and an eased start keep the wrap's speed");
        }
    }

    private static SkeletonDescription Rig() => new("cut-rig", "cut-rig-fingerprint",
        [new("n_root", -1, 0, new BoneTransform(Vector3.Zero, Quaternion.Identity, Vector3.One)),
         new("j_ude_a_l", 0, 0, new BoneTransform(Vector3.UnitX, Quaternion.Identity, Vector3.One))],
        [], [], []);

    /// <summary><paramref name="samples"/> samples over that many frames less one, at 30 fps.</summary>
    internal static MotionClip Spin(int samples, Func<float, float> degrees) => SpinAt(samples, samples - 1, degrees);

    /// <summary>
    /// <paramref name="samples"/> samples over <paramref name="frames"/> timeline frames: the root slides 0.01
    /// along X per frame and the arm turns about Z by <paramref name="degrees"/> of the frame. Both bones are tracked.
    /// </summary>
    internal static MotionClip SpinAt(int samples, int frames, Func<float, float> degrees)
    {
        var poses = Enumerable.Range(0, samples).Select(i =>
        {
            var f = (float)((double)i * frames / (samples - 1));
            return new[]
            {
                new BoneTransform(new Vector3(.01f * f, 0, 0), Quaternion.Identity, Vector3.One),
                new BoneTransform(Vector3.UnitX, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, degrees(f) * MathF.PI / 180), Vector3.One),
            };
        }).ToArray();
        return CopyOf(MotionClip.FromPoses(Rig(), frames / 30f, poses), null, 0);
    }

    /// <summary>The clip with one float track holding <paramref name="value"/> of each sample's frame.</summary>
    private static MotionClip WithFloats(MotionClip clip, Func<float, float> value) =>
        CopyOf(clip, [.. Enumerable.Range(0, clip.Samples).Select(i => value(clip.Frame(i)))], 1);

    private static MotionClip CopyOf(MotionClip clip, float[]? floats, int floatTracks)
    {
        var copy = new MotionClip(clip.Skeleton, clip.Duration, clip.Samples, floats, floatTracks) { SourceTracks = [0, 1], Loops = true };
        copy.CopyFrom(clip);
        return copy;
    }

    /// <summary>The arm's angle about Z at a sample, unwrapped from sample 0 so each is within 180 of the one before.</summary>
    internal static float AngleS(MotionClip clip, int sample)
    {
        var angle = AboutZ(clip.Get(0, 1).Rotation);
        for (var i = 1; i <= sample; i++)
        {
            var next = AboutZ(clip.Get(i, 1).Rotation);
            next += 360 * MathF.Round((angle - next) / 360);
            angle = next;
        }
        return angle;
    }

    /// <summary>The arm's angle at a timeline frame.</summary>
    private static float Angle(MotionClip clip, float frame) => AngleS(clip, (int)MathF.Round(frame * (clip.Samples - 1) / (clip.Duration * 30)));

    private static float AboutZ(Quaternion q) => 2 * MathF.Atan2(q.Z, q.W) * 180 / MathF.PI;

    private static bool Near(float a, float b, float tolerance = .01f) => Math.Abs(a - b) < tolerance;

    private static float Degrees(Quaternion a, Quaternion b) =>
        2 * MathF.Acos(Math.Clamp(Math.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))), 0, 1)) * 180 / MathF.PI;

    private static void Model(Action<bool, string> check, Action<Action, string> reject)
    {
        var cuts = new MotionCuts(100, [new(0, 40), new(60, 100)], [MotionJoin.ForCut]);
        check(cuts.Kept == 80 && !cuts.Whole && MotionCuts.WholeOf(100).Whole, "kept frames and whole");
        check(cuts.SourceAt(0, 80) == 0 && cuts.SourceAt(39.5, 80) == 39.5 && cuts.SourceAt(40, 80) == 60 && cuts.SourceAt(80, 80) == 100,
            "output frames map through the parts; a boundary is the right part's start");
        check(cuts.SourceAt(80, 160) == 60 && cuts.PartAt(80, 160) == 1 && cuts.PartStart(1, 160) == 80, "a stretch scales the map");
        check(cuts.PartAt(40, 80) == 1 && cuts.PartEnd(0, 80) == 40 && cuts.PartAt(80, 80) == 1, "parts by output frame");
        reject(() => new MotionCuts(100, [new(0, 50), new(40, 100)], [MotionJoin.ForCut]).Validate(), "overlapping parts are refused");
        reject(() => new MotionCuts(100, [new(0, 50)], [MotionJoin.ForCut]).Validate(), "a join count that doesn't match is refused");
        reject(() => new MotionCuts(100, [new(0, 100)], [], new MotionJoin(61, JoinPlacement.Before)).Validate(), "a blend over 60 frames is refused");
        reject(() => new MotionCuts(100, [new(0, 100)], [], null, 61).Validate(), "an ease over 60 frames is refused");
        check(!new MotionCuts(100, [new(0, 100)], [], null, MotionCuts.DefaultEase).Whole && MotionCuts.DefaultEase == 30, "an eased start alone changes the motion");
        reject(() => new MotionCuts(100, [new(0, 40, Blend: true), new(40, 100)], [MotionJoin.ForCut]).Validate(), "a first blend part is refused");
        reject(() => new MotionCuts(100, [new(0, 40), new(40, 100, Blend: true)], [MotionJoin.ForCut]).Validate(), "a last blend part is refused");
        reject(() => new MotionCuts(100, [new(0, 40), new(40, 50, Blend: true), new(50, 60, Blend: true), new(60, 100)],
            [MotionJoin.ForCut, MotionJoin.ForCut, MotionJoin.ForCut]).Validate(), "blend parts next to each other are refused");
        new MotionCuts(100, [new(0, 40), new(40, 130, Blend: true), new(60, 100)], [MotionJoin.ForCut, MotionJoin.ForCut]).Validate();
        reject(() => new MotionCuts(100, [new(0, 40), new(40, 130), new(60, 100)], [MotionJoin.ForCut, MotionJoin.ForCut]).Validate(),
            "only a blend part may run past the source or over the next part's frames");
        var blendJson = JsonSerializer.Serialize(new MotionCuts(100, [new(0, 40), new(40, 60, Blend: true), new(60, 100)], [MotionJoin.ForCut, MotionJoin.ForCut]));
        check(JsonSerializer.Deserialize<MotionCuts>(blendJson) is { Parts: [{ Blend: false }, { Blend: true }, { Blend: false }] }, "blend parts survive a journal's JSON");
        var json = JsonSerializer.Serialize(cuts with { Seam = MotionJoin.ForSeam, Ease = 30, EaseEnd = true }, new JsonSerializerOptions { IncludeFields = true });
        check(JsonSerializer.Deserialize<MotionCuts>(json, new JsonSerializerOptions { IncludeFields = true }) is
              { Source: 100, Parts.Length: 2, Seam.Placement: JoinPlacement.Before, Ease: 30, EaseEnd: true } back && back.Parts[1] == new MotionPart(60, 100) &&
              JsonSerializer.Deserialize<MotionCuts>("""{"Source":100,"Parts":[{"From":0,"To":100}],"Joins":[]}""") is { Ease: 0, Seam: null },
            "cuts survive a journal's JSON, and older ones load without an ease");
        var slow = new MotionCuts(100, [new(0, 40), new(40, 60, Stretch: 3), new(60, 100)], [MotionJoin.ForCut, MotionJoin.ForCut]);
        check(slow.Span == 140 && slow.PartStart(1, 140) == 40 && slow.PartEnd(1, 140) == 100 && slow.PartStart(2, 140) == 100 &&
              slow.SourceAt(70, 140) == 50 && slow.SourceAt(100, 140) == 60 && slow.PartAt(99.9, 140) == 1 && slow.PartAt(100, 140) == 2,
            "a part at a third of the speed plays its 20 frames over 60");
        check(Math.Abs(slow.Rate(1, 140) - 1 / 3.0) < 1e-9 && slow.Rate(0, 140) == 1 && slow.Rate(1, 280) == 1 / 6.0,
            "each part's rate, with the clip's even stretch on top");
        check(slow.OutputOf(50, 140) == 70 && slow.CutOf(70, 140) == 50 && slow.CutOf(slow.OutputOf(63.5, 280), 280) == 63.5,
            "cut and output frames map both ways through the stretch");
        check(cuts.OutputOf(40, 80) == 40 && cuts.CutOf(20, 160) == 10 && cuts.Span == 80, "with no stretch the map is today's");
        check(!new MotionCuts(100, [new(0, 100, Stretch: 2)], []).Whole, "a stretched part is a cut");
        reject(() => new MotionCuts(100, [new(0, 50, Stretch: .2), new(50, 100)], [MotionJoin.ForCut]).Validate(), "a stretch under 0.25 is refused");
        reject(() => new MotionCuts(100, [new(0, 50, Stretch: 11), new(50, 100)], [MotionJoin.ForCut]).Validate(), "a stretch over 10 is refused");
        reject(() => new MotionCuts(100, [new(0, 40), new(40, 60, true, 2), new(60, 100)], [MotionJoin.ForCut, MotionJoin.ForCut]).Validate(),
            "a stretched blend part is refused");
        check(JsonSerializer.Deserialize<MotionCuts>(JsonSerializer.Serialize(slow))!.Parts[1].Stretch == 3 &&
              JsonSerializer.Deserialize<MotionCuts>("""{"Source":100,"Parts":[{"From":0,"To":100}],"Joins":[]}""")!.Parts[0].Stretch == 1,
            "the stretch survives a journal, and older ones load at 1");
    }
}
