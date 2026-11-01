using System.Numerics;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Services.Animations.Motion;

/// <summary>
/// Cuts a decoded clip by a <see cref="MotionCuts"/>: each output sample plays the source frame the
/// parts map it to, then an offset blend absorbs the jump at every join and, when set, at the loop
/// seam. A blend adds a quintic correction to one side of the join (or half to each side) that
/// starts at zero with zero speed and ends at the gap with the speed difference, so the output is
/// continuous in pose and speed there and frames outside the blend stay as the time map made them.
/// Speeds are compared per output frame, each side at its own part's rate, so a blend into a slowed
/// part also smooths the change of speed. An eased loop seam changes the time map instead: the faster side's frames next to the seam play
/// at the slower side's speed and catch up, so every bone passes the seam gently along its own path.
/// A blend part plays no frames of its own: it moves from the part before's end into the part after's
/// start, matching pose and speed at both, and the joins at its edges blend nothing.
/// Pure managed code: the bake and Live use it alike.
/// </summary>
internal static class MotionCutter
{
    /// <summary>
    /// How many samples a cut of <paramref name="source"/> <paramref name="length"/> frames long has: as
    /// many per frame as the source has, at least one.
    /// </summary>
    public static int OutputSamples(MotionClip source, int length) => OutputSamples(source.Samples, source.Duration, length);

    /// <summary>How many samples a cut <paramref name="length"/> frames long has of a source with <paramref name="samples"/> over <paramref name="duration"/> seconds.</summary>
    public static int OutputSamples(int samples, float duration, int length) => checked(length * Rate(samples, duration) + 1);

    /// <summary>
    /// <paramref name="source"/> cut by <paramref name="cuts"/> into a clip <paramref name="length"/> frames
    /// long: the same skeleton, tracks, blend hint and loop, with float tracks cut the same way.
    /// </summary>
    public static MotionClip Apply(MotionClip source, MotionCuts cuts, int length, CancellationToken token = default)
    {
        cuts.Validate();
        if (length < TimelineDocument.MinLength)
            throw new InvalidDataException("The cuts don't fit the motion.");
        var rate = Rate(source);
        var samples = OutputSamples(source, length);
        var floatTracks = source.TrackFloats == null ? 0 : source.FloatTracks;
        var output = new MotionClip(source.Skeleton, length / (float)TimelineDocument.Fps, samples,
            source.TrackFloats == null ? null : new float[checked(samples * floatTracks)], floatTracks)
        {
            SourceTracks = source.SourceTracks, ReferenceFollowers = source.ReferenceFollowers, BlendHint = source.BlendHint, Loops = source.Loops,
            HipsOnHara = source.HipsOnHara,
        };
        // Source frames each part plays per output frame: a join matches each side's speed at its own rate.
        var rates = new double[cuts.Parts.Length];
        for (var i = 0; i < rates.Length; i++)
            rates[i] = cuts.Rate(i, length);
        var blends = new List<Blend>();
        for (var i = 0; i < cuts.Joins.Length; i++)
        {
            var (before, after) = Window(cuts, i, length);
            if (before <= 0 && after <= 0)
                continue;
            int a = cuts.Parts[i].To, b = cuts.Parts[i + 1].From;
            blends.Add(Gap(source, a, Speed(source, a, 0), b, Speed(source, b, 0), rates[i], rates[i + 1], cuts.PartEnd(i, length), before, after,
                cuts.Joins[i].Placement, seam: false));
        }
        // A blend part leaves the part before it as that one ends and arrives in the part after it as that one starts.
        var fills = new List<Fill>();
        for (var i = 1; i < cuts.Parts.Length - 1; i++)
        {
            if (!cuts.Parts[i].Blend)
                continue;
            int a = cuts.Parts[i - 1].To, b = cuts.Parts[i + 1].From;
            var start = cuts.PartStart(i, length);
            fills.Add(FillOf(source, a, Speed(source, a, -1), b, Speed(source, b, 1), rates[i - 1], rates[i + 1], start,
                cuts.PartEnd(i, length) - start));
        }
        var ease = EaseOf(source, cuts, length);
        if (cuts.Seam is { } seam)
        {
            var (before, after) = Window(cuts, -1, length);
            if (before > 0 || after > 0)
            {
                int a = cuts.Parts[^1].To, b = cuts.Parts[0].From;
                // An eased side plays at the ease's rate at the seam: the blend aims at that speed, not its own.
                var (end, start) = (Speed(source, a, -1), Speed(source, b, 1));
                if (ease.Frames > 0)
                    (end, start) = ease.AtEnd ? (Scaled(end, ease.Rate), start) : (end, Scaled(start, ease.Rate));
                blends.Add(Gap(source, a, end, b, start, rates[^1], rates[0], length, before, after, seam.Placement, seam: true));
            }
        }

        var pose = new MotionTransform[source.Bones];
        var floats = new float[floatTracks];
        for (var k = 0; k < samples; k++)
        {
            if ((k & 63) == 0)
                token.ThrowIfCancellationRequested();
            var o = (double)k / rate;
            // The ease changes when the start's frames play; the blends correct by output time.
            Sample(source, cuts.SourceAt(EasedTime(o, ease, length), length), pose, floats);
            foreach (var blend in blends)
                blend.Correct(o, length, pose, floats);
            foreach (var fill in fills)
                fill.Play(o, pose, floats);
            for (var bone = 0; bone < pose.Length; bone++)
            {
                var value = pose[bone];
                if (k > 0)
                    value = value with { Rotation = MotionEvaluator.Align(value.Rotation, output.Get(k - 1, bone).Rotation) };
                output.Set(k, bone, value);
            }
            if (floatTracks > 0)
                floats.CopyTo(output.TrackFloats.AsSpan(k * floatTracks, floatTracks));
        }
        // A seam closes the loop: the last sample is the first, as the game wraps it.
        if (cuts.Seam != null && blends.Count > 0 && blends[^1].Seam)
        {
            for (var bone = 0; bone < source.Bones; bone++)
            {
                var first = output.Get(0, bone);
                output.Set(samples - 1, bone, first with { Rotation = MotionEvaluator.Align(first.Rotation, output.Get(samples - 2, bone).Rotation) });
            }
            if (floatTracks > 0)
                output.TrackFloats.AsSpan(0, floatTracks).CopyTo(output.TrackFloats.AsSpan((samples - 1) * floatTracks, floatTracks));
        }
        return output;
    }

    /// <summary>
    /// The output frames a join's blend really uses before and after it, its windows clipped to the
    /// parts they lie on. <paramref name="join"/> -1 is the loop seam, whose before side is the end of
    /// the last part and whose after side is the start of the first. A join at a blend part's edge uses
    /// none: the blend part lines the motion up there.
    /// </summary>
    public static (double Before, double After) Window(MotionCuts cuts, int join, int length)
    {
        var blend = join < 0 ? cuts.Seam : cuts.Joins[join];
        if (blend == null || blend.Frames <= 0 || join >= 0 && cuts.BesideBlend(join))
            return (0, 0);
        var left = join < 0 ? cuts.Parts.Length - 1 : join;
        var right = join < 0 ? 0 : join + 1;
        var leftFrames = cuts.PartEnd(left, length) - cuts.PartStart(left, length);
        var rightFrames = cuts.PartEnd(right, length) - cuts.PartStart(right, length);
        double n = blend.Frames;
        return blend.Placement switch
        {
            JoinPlacement.Before => (Math.Min(n, leftFrames), 0),
            JoinPlacement.After => (0, Math.Min(n, rightFrames)),
            _ => (Math.Min(n / 2, leftFrames), Math.Min(n / 2, rightFrames)),
        };
    }

    /// <summary>
    /// The fastest an eased start may begin, as a share of normal speed: above 4 its time map would run
    /// backwards before it catches up, and at 2.5 it still slows to half speed on the way.
    /// </summary>
    public const float MaxEaseRate = 2.5f;

    /// <summary>
    /// Output frame <paramref name="output"/> of a start eased over <paramref name="frames"/> frames, as
    /// the time it plays: a cubic that leaves 0 at <paramref name="rate"/> times normal speed and reaches
    /// <paramref name="frames"/> at normal speed, rising all the way for rates up to 4. Frames outside
    /// the ease play as they are.
    /// </summary>
    public static double EaseTime(double output, double frames, double rate)
    {
        if (frames <= 0 || output <= 0 || output >= frames)
            return output;
        var s = output / frames;
        return frames * (rate * s * (1 - s) * (1 - s) + s * s * (2 - s));
    }

    /// <summary>
    /// The loop seam's ease for a cut <paramref name="length"/> frames long: its frames, clipped to the
    /// part it lies on, the rate the eased side plays at the seam (the other side's mean bone speed over
    /// its own, each per output frame at its part's rate, 1 when the eased side doesn't move, at most
    /// <see cref="MaxEaseRate"/>) and whether it is the end.
    /// </summary>
    public static (double Frames, double Rate, bool AtEnd) EaseOf(MotionClip source, MotionCuts cuts, int length)
    {
        if (cuts.Ease <= 0)
            return (0, 1, false);
        var (from, to) = cuts.EaseSpan(length);
        var frames = to - from;
        var gap = LoopSeam.Gap(source, cuts.Parts[^1].To, cuts.Parts[0].From);
        double first = cuts.Rate(0, length), last = cuts.Rate(cuts.Parts.Length - 1, length);
        var (eased, other, easedRate, otherRate) = cuts.EaseEnd ? (gap.SpeedIn, gap.SpeedOut, last, first) : (gap.SpeedOut, gap.SpeedIn, first, last);
        // Source speeds times their parts' rates; with both rates equal the ratio is the source speeds'.
        var rate = eased > 1e-3f ? Math.Clamp(other / eased * (otherRate / easedRate), 0, MaxEaseRate) : 1;
        return (frames, rate, cuts.EaseEnd);
    }

    /// <summary>
    /// The output time output frame <paramref name="output"/> plays at under <paramref name="ease"/>: the
    /// start's time map (see <see cref="EaseTime"/>), or the end's, its mirror, in a cut <paramref name="length"/> frames long.
    /// </summary>
    public static double EasedTime(double output, (double Frames, double Rate, bool AtEnd) ease, int length) =>
        ease.AtEnd ? length - EaseTime(length - output, ease.Frames, ease.Rate) : EaseTime(output, ease.Frames, ease.Rate);

    /// <summary>Rises from 0 to 1 with zero speed and acceleration at both ends.</summary>
    internal static float Rise(float s) => s * s * s * (10 + s * (-15 + s * 6));

    /// <summary>0 at both ends, its slope 0 at the start and 1 at the end: carries a speed into the end.</summary>
    internal static float RiseSpeed(float s) => s * s * s * (-4 + s * (7 - s * 3));

    /// <summary>Falls from 1 to 0 with zero speed and acceleration at both ends.</summary>
    internal static float Fall(float s) => 1 - Rise(s);

    /// <summary>0 at both ends, its slope 1 at the start and 0 at the end: carries a speed out of the start.</summary>
    internal static float FallSpeed(float s) => s + s * s * s * (-6 + s * (8 - s * 3));

    /// <summary>Samples per timeline frame: the source's, at least one.</summary>
    private static int Rate(MotionClip source) => Rate(source.Samples, source.Duration);

    private static int Rate(int samples, float duration)
    {
        var frames = duration * TimelineDocument.Fps;
        return frames <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling((samples - 1) / frames - 1e-4));
    }

    /// <summary>
    /// Every bone and float track at source frame <paramref name="frame"/>, between the two samples
    /// around it; a frame on a sample takes it exactly, and frames past the clip take its last.
    /// </summary>
    private static void Sample(MotionClip source, double frame, MotionTransform[] pose, float[] floats)
    {
        var at = source.Duration <= 0 ? 0 : Math.Clamp(frame / TimelineDocument.Fps / source.Duration, 0, 1) * (source.Samples - 1);
        if (Math.Abs(at - Math.Round(at)) < 1e-4)
            at = Math.Round(at);
        var low = Math.Min((int)at, source.Samples - 1);
        var high = Math.Min(low + 1, source.Samples - 1);
        var t = (float)(at - low);
        for (var b = 0; b < pose.Length; b++)
            pose[b] = high == low || t <= 0 ? source.Get(low, b) : MotionEvaluator.Lerp(source.Get(low, b), source.Get(high, b), t);
        if (source.TrackFloats is not { } values)
            return;
        var count = floats.Length;
        for (var f = 0; f < count; f++)
        {
            var a = values[low * count + f];
            floats[f] = high == low || t <= 0 ? a : a + (values[high * count + f] - a) * t;
        }
    }

    /// <summary>
    /// Every bone's and float's speed at source frame <paramref name="frame"/>, per source frame:
    /// <paramref name="side"/> 0 is a central difference (one-sided at the clip's ends), -1 backward, 1 forward.
    /// </summary>
    private static (Vector3[] Position, Vector3[] Rotation, Vector3[] Scale, float[] Floats) Speed(MotionClip source, int frame, int side)
    {
        var end = source.Duration * TimelineDocument.Fps;
        var from = side > 0 || frame - 1 < 0 ? frame : frame - 1;
        var to = side < 0 || frame + 1 > end ? frame : frame + 1;
        if (from == to)
            (from, to) = frame - 1 < 0 ? (frame, frame + 1) : (frame - 1, frame);
        var span = to - from;
        var floatCount = source.TrackFloats == null ? 0 : source.FloatTracks;
        var (a, af) = PoseAt(source, from, floatCount);
        var (b, bf) = PoseAt(source, to, floatCount);
        var position = new Vector3[a.Length];
        var rotation = new Vector3[a.Length];
        var scale = new Vector3[a.Length];
        for (var i = 0; i < a.Length; i++)
        {
            position[i] = (b[i].Position - a[i].Position) / span;
            rotation[i] = Log(b[i].Rotation * Quaternion.Conjugate(Quaternion.Normalize(a[i].Rotation))) / span;
            scale[i] = (b[i].Scale - a[i].Scale) / span;
        }
        var floats = new float[floatCount];
        for (var f = 0; f < floatCount; f++)
            floats[f] = (bf[f] - af[f]) / span;
        return (position, rotation, scale, floats);
    }

    /// <summary>Speeds <paramref name="factor"/> times as fast.</summary>
    private static (Vector3[] Position, Vector3[] Rotation, Vector3[] Scale, float[] Floats) Scaled(
        (Vector3[] Position, Vector3[] Rotation, Vector3[] Scale, float[] Floats) speed, double factor)
    {
        var k = (float)factor;
        return ([.. speed.Position.Select(v => v * k)], [.. speed.Rotation.Select(v => v * k)], [.. speed.Scale.Select(v => v * k)],
            [.. speed.Floats.Select(v => v * k)]);
    }

    private static (MotionTransform[] Pose, float[] Floats) PoseAt(MotionClip source, double frame, int floatCount)
    {
        var pose = new MotionTransform[source.Bones];
        var floats = new float[floatCount];
        Sample(source, frame, pose, floats);
        return (pose, floats);
    }

    /// <summary>
    /// The blend of a join from source frame <paramref name="left"/> to <paramref name="right"/>, met at output
    /// frame <paramref name="at"/>: speeds per source frame, each side playing <paramref name="leftRate"/> or
    /// <paramref name="rightRate"/> source frames per output frame.
    /// </summary>
    private static Blend Gap(MotionClip source, int left, (Vector3[] Position, Vector3[] Rotation, Vector3[] Scale, float[] Floats) leftSpeed,
        int right, (Vector3[] Position, Vector3[] Rotation, Vector3[] Scale, float[] Floats) rightSpeed, double leftRate, double rightRate,
        double at, double before, double after, JoinPlacement placement, bool seam)
    {
        var floatCount = leftSpeed.Floats.Length;
        var (l, lf) = PoseAt(source, left, floatCount);
        var (r, rf) = PoseAt(source, right, floatCount);
        var bones = l.Length;
        var blend = new Blend(at, before, after, placement == JoinPlacement.Centered ? .5f : 1, seam, bones, floatCount);
        float kl = (float)leftRate, kr = (float)rightRate;
        for (var i = 0; i < bones; i++)
        {
            blend.Position[i] = r[i].Position - l[i].Position;
            blend.Rotation[i] = Log(r[i].Rotation * Quaternion.Conjugate(Quaternion.Normalize(l[i].Rotation)));
            blend.Scale[i] = r[i].Scale - l[i].Scale;
            blend.PositionSpeed[i] = rightSpeed.Position[i] * kr - leftSpeed.Position[i] * kl;
            blend.RotationSpeed[i] = rightSpeed.Rotation[i] * kr - leftSpeed.Rotation[i] * kl;
            blend.ScaleSpeed[i] = rightSpeed.Scale[i] * kr - leftSpeed.Scale[i] * kl;
        }
        for (var f = 0; f < floatCount; f++)
        {
            blend.Floats[f] = rf[f] - lf[f];
            blend.FloatSpeed[f] = rightSpeed.Floats[f] * kr - leftSpeed.Floats[f] * kl;
        }
        return blend;
    }

    /// <summary>
    /// A blend part <paramref name="frames"/> output frames long from output frame <paramref name="at"/>: from
    /// source frame <paramref name="left"/> moving at <paramref name="leftSpeed"/> to <paramref name="right"/>
    /// moving at <paramref name="rightSpeed"/>, speeds per source frame, the part before playing
    /// <paramref name="leftRate"/> and the part after <paramref name="rightRate"/> source frames per output frame.
    /// </summary>
    private static Fill FillOf(MotionClip source, int left, (Vector3[] Position, Vector3[] Rotation, Vector3[] Scale, float[] Floats) leftSpeed,
        int right, (Vector3[] Position, Vector3[] Rotation, Vector3[] Scale, float[] Floats) rightSpeed, double leftRate, double rightRate,
        double at, double frames)
    {
        var floatCount = leftSpeed.Floats.Length;
        var (l, lf) = PoseAt(source, left, floatCount);
        var (r, rf) = PoseAt(source, right, floatCount);
        var fill = new Fill(at, frames, l, lf);
        float kl = (float)leftRate, kr = (float)rightRate;
        for (var i = 0; i < l.Length; i++)
        {
            fill.Position[i] = r[i].Position - l[i].Position;
            fill.Rotation[i] = Log(r[i].Rotation * Quaternion.Conjugate(Quaternion.Normalize(l[i].Rotation)));
            fill.Scale[i] = r[i].Scale - l[i].Scale;
            fill.PositionIn[i] = leftSpeed.Position[i] * kl;
            fill.RotationIn[i] = leftSpeed.Rotation[i] * kl;
            fill.ScaleIn[i] = leftSpeed.Scale[i] * kl;
            fill.PositionOut[i] = rightSpeed.Position[i] * kr;
            fill.RotationOut[i] = rightSpeed.Rotation[i] * kr;
            fill.ScaleOut[i] = rightSpeed.Scale[i] * kr;
        }
        for (var f = 0; f < floatCount; f++)
        {
            fill.Floats[f] = rf[f] - lf[f];
            fill.FloatIn[f] = leftSpeed.Floats[f] * kl;
            fill.FloatOut[f] = rightSpeed.Floats[f] * kr;
        }
        return fill;
    }

    /// <summary>The rotation vector of a rotation, the short way round: its axis scaled by its angle in radians.</summary>
    internal static Vector3 Log(Quaternion q)
    {
        q = Quaternion.Normalize(q);
        if (q.W < 0)
            q = -q;
        var sin = MathF.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z);
        if (sin < 1e-7f)
            return new Vector3(q.X, q.Y, q.Z) * 2;
        var angle = 2 * MathF.Atan2(sin, q.W);
        return new Vector3(q.X, q.Y, q.Z) * (angle / sin);
    }

    /// <summary>The rotation of a rotation vector: the inverse of <see cref="Log"/>.</summary>
    internal static Quaternion Exp(Vector3 v)
    {
        var angle = v.Length();
        if (angle < 1e-7f)
            return Quaternion.Normalize(new Quaternion(v * .5f, 1));
        var half = angle / 2;
        return new Quaternion(v * (MathF.Sin(half) / angle), MathF.Cos(half));
    }

    /// <summary>One join's correction: the gap and speed gap per bone and float, and the windows it spreads over.</summary>
    private sealed class Blend(double at, double before, double after, float share, bool seam, int bones, int floats)
    {
        public bool Seam { get; } = seam;
        public readonly Vector3[] Position = new Vector3[bones], Rotation = new Vector3[bones], Scale = new Vector3[bones];
        public readonly Vector3[] PositionSpeed = new Vector3[bones], RotationSpeed = new Vector3[bones], ScaleSpeed = new Vector3[bones];
        public readonly float[] Floats = new float[floats], FloatSpeed = new float[floats];

        /// <summary>Adds the correction for output frame <paramref name="o"/>, if it lies in one of the windows.</summary>
        public void Correct(double o, int length, MotionTransform[] pose, float[] floats)
        {
            // Before: over the frames up to the join (a seam's last sample included), rising to the gap.
            if (before > 0 && o >= at - before && (o < at || Seam && o <= at))
            {
                var s = (float)((o - (at - before)) / before);
                Add(pose, floats, share * Rise(s), share * (float)before * RiseSpeed(s));
            }
            // After: over the frames from the join (a seam's from the loop's start), falling from minus the gap.
            var from = Seam ? 0 : at;
            if (after > 0 && o >= from && o < from + after)
            {
                var s = (float)((o - from) / after);
                Add(pose, floats, -share * Fall(s), -share * (float)after * FallSpeed(s));
            }
        }

        private void Add(MotionTransform[] pose, float[] floats, float gap, float speed)
        {
            for (var i = 0; i < pose.Length; i++)
            {
                var value = pose[i];
                var turn = Rotation[i] * gap + RotationSpeed[i] * speed;
                pose[i] = new MotionTransform(
                    value.Position + Position[i] * gap + PositionSpeed[i] * speed,
                    turn == Vector3.Zero ? value.Rotation : Quaternion.Normalize(Exp(turn) * value.Rotation),
                    value.Scale + Scale[i] * gap + ScaleSpeed[i] * speed);
            }
            for (var f = 0; f < floats.Length; f++)
                floats[f] += Floats[f] * gap + FloatSpeed[f] * speed;
        }
    }

    /// <summary>
    /// One blend part's motion: the start pose, the gap to the end pose, and the speeds it leaves and
    /// arrives with, per bone and float. It plays the start, plus the gap as it rises, plus each speed
    /// carried out of the start and into the end, so pose and speed match the parts on both sides.
    /// </summary>
    private sealed class Fill(double at, double frames, MotionTransform[] start, float[] startFloats)
    {
        public readonly Vector3[] Position = new Vector3[start.Length], Rotation = new Vector3[start.Length], Scale = new Vector3[start.Length];
        public readonly Vector3[] PositionIn = new Vector3[start.Length], RotationIn = new Vector3[start.Length], ScaleIn = new Vector3[start.Length];
        public readonly Vector3[] PositionOut = new Vector3[start.Length], RotationOut = new Vector3[start.Length], ScaleOut = new Vector3[start.Length];
        public readonly float[] Floats = new float[startFloats.Length], FloatIn = new float[startFloats.Length], FloatOut = new float[startFloats.Length];

        /// <summary>Puts the blend's pose for output frame <paramref name="o"/> in place of the motion's, if the part plays it.</summary>
        public void Play(double o, MotionTransform[] pose, float[] floats)
        {
            if (frames <= 0 || o < at || o >= at + frames)
                return;
            var s = (float)((o - at) / frames);
            float gap = Rise(s), leave = (float)frames * FallSpeed(s), arrive = (float)frames * RiseSpeed(s);
            for (var i = 0; i < pose.Length; i++)
            {
                var from = start[i];
                var turn = Rotation[i] * gap + RotationIn[i] * leave + RotationOut[i] * arrive;
                pose[i] = new MotionTransform(
                    from.Position + Position[i] * gap + PositionIn[i] * leave + PositionOut[i] * arrive,
                    turn == Vector3.Zero ? from.Rotation : Quaternion.Normalize(Exp(turn) * from.Rotation),
                    from.Scale + Scale[i] * gap + ScaleIn[i] * leave + ScaleOut[i] * arrive);
            }
            for (var f = 0; f < floats.Length; f++)
                floats[f] = startFloats[f] + Floats[f] * gap + FloatIn[f] * leave + FloatOut[f] * arrive;
        }
    }
}
