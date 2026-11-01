using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using InstantEdit.Models;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Services.Animations.Motion;

/// <summary>
/// Samples <paramref name="First"/> to <paramref name="Last"/> (inclusive) where a bone is down, and the
/// spot it is held at: its average position and turn over them. On a loop a stretch over the seam has
/// <paramref name="First"/> past <paramref name="Last"/>: it runs from First to the end and on from the start to Last.
/// </summary>
internal sealed record PlantedStretch(int First, int Last, MotionTransform Held);

/// <summary>A foot's sole in the foot's own space: its <paramref name="Up"/> when it stands flat, and the points under its heel and ball.</summary>
internal readonly record struct FootSole(Vector3 Up, Vector3 Heel, Vector3 Ball);

/// <summary>
/// Where a planted pin holds its bone over a clip <paramref name="Samples"/> long: its stretches, and
/// how strongly it holds at each sample, easing over <paramref name="EaseSamples"/> before and after
/// each stretch. A path that <paramref name="Wraps"/> runs round the loop, its last sample the first's moment.
/// Where a narrower layer cuts the clip, at <paramref name="CutFirst"/> and <paramref name="CutLast"/> (-1 for
/// none), the hold eases in from and out to the cut as it does around a stretch, so a stretch the cut
/// shortens doesn't snap on or off.
/// </summary>
internal sealed record PlantedPath(ImmutableArray<PlantedStretch> Stretches, int Samples, bool Wraps, int EaseSamples, int CutFirst = -1, int CutLast = -1)
{
    /// <summary>How strongly the pin holds at <paramref name="sample"/>: 1 inside a stretch, easing to 0 around it and at a cut.</summary>
    public float Hold(int sample)
    {
        var hold = 0f;
        foreach (var stretch in Stretches)
            hold = Math.Max(hold, HoldOf(stretch, sample));
        if (hold > 0 && CutFirst >= 0 && sample - CutFirst < EaseSamples)
            hold *= AnimationPoseRules.SmoothStep(Math.Clamp((float)(sample - CutFirst) / EaseSamples, 0, 1));
        if (hold > 0 && CutLast >= 0 && CutLast - sample < EaseSamples)
            hold *= AnimationPoseRules.SmoothStep(Math.Clamp((float)(CutLast - sample) / EaseSamples, 0, 1));
        return hold;
    }

    /// <summary>
    /// The spot the pin holds its bone at <paramref name="sample"/>, null where it doesn't hold. Where two
    /// stretches' eases overlap it slides between their spots, weighted by each one's hold, so it never jumps.
    /// </summary>
    public MotionTransform? Target(int sample)
    {
        var total = 0f;
        var holding = 0;
        PlantedStretch? last = null;
        var position = Vector3.Zero;
        var sum = Vector4.Zero;
        Quaternion? reference = null;
        foreach (var stretch in Stretches)
        {
            var hold = HoldOf(stretch, sample);
            if (hold <= 0)
                continue;
            holding++;
            last = stretch;
            total += hold;
            position += stretch.Held.Position * hold;
            reference ??= stretch.Held.Rotation;
            var q = MotionEvaluator.Align(stretch.Held.Rotation, reference.Value);
            sum += new Vector4(q.X, q.Y, q.Z, q.W) * hold;
        }
        if (total <= 0)
            return null;
        // One stretch holding fully is its spot exactly, not an average of one.
        if (holding == 1 && total >= 1)
            return last!.Held;
        var rotation = sum.LengthSquared() < 1e-12f ? reference!.Value : Quaternion.Normalize(new Quaternion(sum.X, sum.Y, sum.Z, sum.W));
        return new MotionTransform(position / total, rotation, Vector3.One);
    }

    private float HoldOf(PlantedStretch stretch, int sample)
    {
        var distance = Distance(stretch, sample);
        if (distance == 0)
            return 1;
        return distance >= EaseSamples ? 0 : 1 - AnimationPoseRules.SmoothStep((float)distance / EaseSamples);
    }

    /// <summary>Samples from <paramref name="sample"/> to the nearest end of <paramref name="stretch"/>; 0 inside it.</summary>
    private int Distance(PlantedStretch stretch, int sample)
    {
        if (!Wraps)
        {
            if (sample < stretch.First)
                return stretch.First - sample;
            return sample > stretch.Last ? sample - stretch.Last : 0;
        }
        // Round the loop the last sample is the first one's moment.
        var period = Samples - 1;
        int first = stretch.First % period, last = stretch.Last % period;
        sample %= period;
        var inside = stretch.First > stretch.Last || first > last ? sample >= first || sample <= last : sample >= first && sample <= last;
        if (inside || stretch.First == 0 && stretch.Last == Samples - 1)
            return 0;
        return Math.Min(((sample - last) % period + period) % period, ((first - sample) % period + period) % period);
    }
}

/// <summary>Finds where a bone stands still over a clip, for planted pins. Pure.</summary>
internal static class PinPlanting
{
    /// <summary>Clean-up and easing lengths, in timeline frames: gaps this short close, shorter stretches drop, holds ease over this.</summary>
    public const int CloseGapFrames = 3, MinStretchFrames = 4, EaseFrames = 4;

    /// <summary>
    /// Below which a bone counts as down: speed in metres a second and turn speed in degrees a second, both
    /// over <see cref="SpeedWindowFrames"/> each side, and height above its lowest in metres. Measured on real
    /// clips: a planted idle foot moves under 1.1 cm/s, a dance's shuffling feet 4-20 cm/s.
    /// </summary>
    public static (float Speed, float TurnSpeed, float Height) Limits(PinSensitivity sensitivity) => sensitivity switch
    {
        PinSensitivity.Strict => (.02f, 10, .02f),
        PinSensitivity.Loose => (.08f, 40, .08f),
        _ => (.04f, 20, .04f),
    };

    /// <summary>Timeline frames each side of a sample that its speed is read over.</summary>
    public const int SpeedWindowFrames = 2;

    /// <summary>How many times the limits a bone may move from one sample to the next and still be down: jitter a window averages away isn't.</summary>
    private const float JoltFactor = 4;

    /// <summary>
    /// A bone that never stops but stays within the speed limit times this many seconds (and turns within the turn limit times it)
    /// for that long is down too: a ported dance's feet wiggling 3-4 cm with every bounce, at 5-15 cm/s, never read still over the
    /// window, but over this long their net speed is under the limit, which a glide's isn't.
    /// </summary>
    private const float SpotSeconds = 1.5f;

    /// <summary>
    /// <paramref name="path"/> with each stretch's held spot brought in, near where it was, until it lies
    /// within <paramref name="limits"/>[i] of <paramref name="roots"/>[i] (both in the pin's space) at every
    /// sample i of the stretch: a held spot is an average, so a limb the motion has straight may not reach
    /// it where the bone above it is a little farther off, and the bone would rise and fall there instead
    /// of holding still. Only positions change. A spot already in reach stays exactly.
    /// </summary>
    public static PlantedPath Reachable(PlantedPath path, ReadOnlySpan<Vector3> roots, ReadOnlySpan<float> limits)
    {
        var stretches = path.Stretches.ToBuilder();
        for (var s = 0; s < stretches.Count; s++)
            stretches[s] = stretches[s] with { Held = stretches[s].Held with { Position = InReach(stretches[s].Held.Position, stretches[s], path.Samples, roots, limits) } };
        return path with { Stretches = stretches.ToImmutable() };
    }

    private const int ReachPasses = 16;

    /// <summary><paramref name="held"/> brought in until every sample of <paramref name="stretch"/> reaches it, as <see cref="Reachable"/> does.</summary>
    private static Vector3 InReach(Vector3 held, PlantedStretch stretch, int samples, ReadOnlySpan<Vector3> roots, ReadOnlySpan<float> limits)
    {
        // Each sample's reach is a ball round its root; passes of projecting onto each settle on a spot inside all of them.
        for (var pass = 0; pass < ReachPasses; pass++)
        {
            var moved = false;
            foreach (var i in SamplesIn(stretch, samples))
            {
                var offset = held - roots[i];
                var distance = offset.Length();
                if (distance <= limits[i] || distance < 1e-6f)
                    continue;
                held = roots[i] + offset * (limits[i] / distance);
                moved = true;
            }
            if (!moved)
                break;
        }
        return held;
    }

    /// <summary>Up to this tilt from flat, in degrees, a held foot is levelled; one tipped further (a kneel, tiptoes) keeps its angle.</summary>
    public const float FlatDegrees = 30;

    /// <summary>
    /// The sole of <paramref name="skeleton"/>'s <paramref name="foot"/> as its rest pose stands it on the floor (model Y 0), in the
    /// foot's own space: the way up, and the heel and ball, the floor's points under the ankle and the toe joint. Null for a bone
    /// that isn't a foot. A foot without its toe bone has only the heel.
    /// </summary>
    public static FootSole? Sole(SkeletonDescription skeleton, int foot)
    {
        var name = skeleton.Bones[foot].Name;
        if (name is not (PinChains.FootL or PinChains.FootR))
            return null;
        var rest = MotionEvaluator.ModelSpace(skeleton, [.. skeleton.Bones.Select(bone => MotionTransform.From(bone.Reference))]);
        var inverse = Quaternion.Inverse(Quaternion.Normalize(rest[foot].Rotation));
        var ankle = rest[foot].Position;
        Vector3 Under(Vector3 joint) => Vector3.Transform(new Vector3(joint.X, 0, joint.Z) - ankle, inverse);
        var toeName = "j_asi_e" + name[^2..];
        var heel = Under(ankle);
        var ball = heel;
        for (var b = 0; b < skeleton.Bones.Length; b++)
            if (skeleton.Bones[b].Parent == foot && skeleton.Bones[b].Name == toeName)
                ball = Under(rest[b].Position);
        return new FootSole(Vector3.Normalize(Vector3.Transform(Vector3.UnitY, inverse)), heel, ball);
    }

    /// <summary>Steps from level back to the motion's tilt that <see cref="OnFloor"/> tries for a leg that can't bring a level sole down.</summary>
    private const int LevelSteps = 16;

    /// <summary>How close over the floor, in metres (0.1 mm), a sole counts as on it.</summary>
    private const float Touching = 1e-4f;

    /// <summary>
    /// <paramref name="path"/> with each stretch's held foot (model space) set down on the floor: with <paramref name="turn"/> (the pin
    /// holds its turn) a foot tilted up to <see cref="FlatDegrees"/> from <paramref name="sole"/>'s flat is levelled, keeping its
    /// heading; then it moves up or down until the lower of its heel and ball is at Y 0. A foot the motion tips further lands on its
    /// heel or ball at its own angle. Given the limb's reach (<paramref name="roots"/> and <paramref name="limits"/>, as
    /// <see cref="Reachable"/> takes them), a foot the leg can't lower far enough is levelled only as far as its sole still touches
    /// the floor, or as near as it comes: levelling a toe-down foot on a straight leg would lift the ball off the floor.
    /// </summary>
    public static PlantedPath OnFloor(PlantedPath path, FootSole sole, bool turn, ReadOnlySpan<Vector3> roots = default, ReadOnlySpan<float> limits = default)
    {
        var stretches = path.Stretches.ToBuilder();
        for (var s = 0; s < stretches.Count; s++)
        {
            var stretch = stretches[s];
            var held = stretch.Held;
            var own = Quaternion.Normalize(held.Rotation);
            var rotation = own;
            var up = Vector3.Transform(sole.Up, own);
            if (turn && MathF.Acos(Math.Clamp(up.Y, -1, 1)) * 180 / MathF.PI <= FlatDegrees)
            {
                var level = Quaternion.Normalize(PinSolver.Between(up, Vector3.UnitY) * own);
                rotation = level;
                if (!roots.IsEmpty)
                {
                    // From level toward the motion's own tilt, the first that touches, else the nearest; a tie keeps the more level.
                    var best = float.MaxValue;
                    for (var step = 0; step <= LevelSteps && best > Touching; step++)
                    {
                        var tried = Quaternion.Normalize(Quaternion.Slerp(level, own, (float)step / LevelSteps));
                        var gap = InReach(Floored(held.Position, tried, sole), stretch, path.Samples, roots, limits).Y + Lowest(tried, sole);
                        if (gap < best - Touching)
                            (best, rotation) = (gap, tried);
                    }
                }
            }
            stretches[s] = stretch with { Held = held with { Position = Floored(held.Position, rotation, sole), Rotation = turn ? rotation : held.Rotation } };
        }
        return path with { Stretches = stretches.ToImmutable() };
    }

    /// <summary>The height of the lower of <paramref name="sole"/>'s heel and ball under the foot's ankle, turned <paramref name="rotation"/>.</summary>
    private static float Lowest(Quaternion rotation, FootSole sole) =>
        Math.Min(Vector3.Transform(sole.Heel, rotation).Y, Vector3.Transform(sole.Ball, rotation).Y);

    /// <summary>The ankle at <paramref name="position"/> moved up or down until the foot, turned <paramref name="rotation"/>, stands on the floor.</summary>
    private static Vector3 Floored(Vector3 position, Quaternion rotation, FootSole sole) => position with { Y = -Lowest(rotation, sole) };

    /// <summary>A stretch's samples, round the seam when it runs over it.</summary>
    private static IEnumerable<int> SamplesIn(PlantedStretch stretch, int samples)
    {
        if (stretch.First <= stretch.Last)
        {
            for (var i = stretch.First; i <= stretch.Last; i++)
                yield return i;
            yield break;
        }
        for (var i = stretch.First; i < samples; i++)
            yield return i;
        for (var i = 0; i <= stretch.Last; i++)
            yield return i;
    }

    /// <summary>How many of <paramref name="clip"/>'s samples <paramref name="frames"/> timeline frames take, at least one.</summary>
    public static int SamplesOf(int frames, MotionClip clip)
    {
        if (!(clip.Duration > 0))
            return Math.Max(1, frames);
        var perFrame = (clip.Samples - 1) / clip.Duration / TimelineDocument.Fps;
        return Math.Max(1, (int)Math.Round(frames * perFrame, MidpointRounding.AwayFromZero));
    }

    /// <summary>
    /// The stretches where a bone whose transform at each sample is <paramref name="path"/> (in its pin's
    /// space, the whole clip at <paramref name="sampleRate"/> samples a second) is down, looking only at
    /// samples <paramref name="first"/> to <paramref name="last"/>. Gaps of up to <paramref name="gapSamples"/>
    /// close, stretches shorter than <paramref name="minSamples"/> drop, and with <paramref name="wraps"/> a
    /// stretch running off the end joins one starting at the beginning. <paramref name="heights"/> are the bone's
    /// heights in model space, for a pin attached to a bone, whose space's Y isn't up; none reads the path's Y.
    /// </summary>
    public static PlantedPath Find(ReadOnlySpan<MotionTransform> path, float sampleRate, int first, int last, bool wraps, PinSensitivity sensitivity,
        int gapSamples, int minSamples, int easeSamples, ReadOnlySpan<float> heights = default)
    {
        var count = path.Length;
        first = Math.Max(first, 0);
        last = Math.Min(last, count - 1);
        // A range of one sample has no speed to read.
        if (last <= first)
            return new PlantedPath([], count, false, easeSamples);
        wraps &= first == 0 && last == count - 1 && count > 2;
        var (speedLimit, turnLimit, heightLimit) = Limits(sensitivity);
        var lowest = float.MaxValue;
        for (var i = first; i <= last; i++)
            lowest = Math.Min(lowest, heights.IsEmpty ? path[i].Position.Y : heights[i]);

        // Speeds over SpeedWindowFrames before and after each sample, the stiller side counting: a sample's noise
        // averages out but a slide doesn't, and a bone that has just landed or is about to lift is still down.
        // At the range's ends a side is cut short; round a loop it goes on over the seam (the last sample is the first's moment).
        var window = Math.Max(1, (int)Math.Round(SpeedWindowFrames * sampleRate / TimelineDocument.Fps, MidpointRounding.AwayFromZero));
        // Round a very short loop the window never reaches back to its own sample.
        if (wraps)
            window = Math.Max(1, Math.Min(window, (count - 2) / 2));

        var spots = Spots(path, first, last, (int)Math.Ceiling(SpotSeconds * sampleRate), speedLimit * SpotSeconds, turnLimit * SpotSeconds);

        // Runs of samples that are down.
        var down = new bool[count];
        var runs = new List<(int First, int Last)>();
        var start = -1;
        for (var i = first; i <= last; i++)
        {
            var (backSpeed, backTurn) = Side(path, i, i - window, first, last, wraps, sampleRate);
            var (aheadSpeed, aheadTurn) = Side(path, i, i + window, first, last, wraps, sampleRate);
            var still = backSpeed < speedLimit && backTurn < turnLimit || aheadSpeed < speedLimit && aheadTurn < turnLimit || spots[i];
            // A jolt to the next sample (the last: from the one before, or round the loop to sample 1) that the window would average away.
            var (jolt, joltTurn) = Side(path, i, i < last || wraps ? i + 1 : i - 1, first, last, wraps, sampleRate);
            down[i] = still && jolt < JoltFactor * speedLimit && joltTurn < JoltFactor * turnLimit && (heights.IsEmpty ? path[i].Position.Y : heights[i]) - lowest < heightLimit;
            if (down[i] && start < 0)
                start = i;
            if (!down[i] && start >= 0)
            {
                runs.Add((start, i - 1));
                start = -1;
            }
        }
        if (start >= 0)
            runs.Add((start, last));

        for (var r = runs.Count - 1; r > 0; r--)
            if (runs[r].First - runs[r - 1].Last - 1 <= gapSamples)
            {
                runs[r - 1] = (runs[r - 1].First, runs[r].Last);
                runs.RemoveAt(r);
            }
        var joined = false;
        if (wraps && runs.Count > 1 && runs[^1].First == last && runs[0].First == first)
        {
            // Down only on the last sample, which is the first one's moment, and the first run starts there too: one run.
            runs.RemoveAt(runs.Count - 1);
        }
        else if (wraps && runs.Count > 1 && runs[0].First - first + (last - runs[^1].Last) - 1 <= gapSamples)
        {
            // Round the loop the last run goes on into the first: one stretch over the seam.
            runs[0] = (runs[^1].First, runs[0].Last);
            runs.RemoveAt(runs.Count - 1);
            joined = true;
        }

        var stretches = ImmutableArray.CreateBuilder<PlantedStretch>();
        for (var r = 0; r < runs.Count; r++)
        {
            var (from, to) = runs[r];
            var across = joined && r == 0;
            var length = across ? last - from + 1 + to - first : to - from + 1;
            if (length < minSamples)
                continue;
            // The spot is the average of the samples that are down, the seam's moment counted once.
            var held = across ? Average(path, down, from, last - 1, first, to) : Average(path, down, from, wraps && from == first && to == last ? to - 1 : to, 0, -1);
            stretches.Add(new PlantedStretch(from, to, held));
        }
        // A layer narrower than the clip cuts it: the hold eases at the cut, as it does around a stretch.
        return new PlantedPath(stretches.ToImmutable(), count, wraps, easeSamples, wraps || first == 0 ? -1 : first, wraps || last == count - 1 ? -1 : last);
    }

    /// <summary>"Held 0-40, 72-119" in timeline frames; "Held 0-119 (the whole loop)"; "Not held anywhere".</summary>
    public static string Facts(PlantedPath path, MotionClip clip)
    {
        if (path.Stretches.IsEmpty)
            return "Not held anywhere";
        int Frame(int sample) => (int)Math.Round(clip.Frame(Math.Clamp(sample, 0, clip.Samples - 1)), MidpointRounding.AwayFromZero);
        if (clip.Loops && path.Stretches is [{ First: 0 } whole] && whole.Last == clip.Samples - 1)
            return $"Held 0-{Frame(whole.Last).ToString(CultureInfo.InvariantCulture)} (the whole loop)";
        string Range(int first, int last) => $"{Frame(first).ToString(CultureInfo.InvariantCulture)}-{Frame(last).ToString(CultureInfo.InvariantCulture)}";
        // A stretch over the loop's seam reads as its two parts, the end of the loop first.
        return "Held " + string.Join(", ", path.Stretches.Select(stretch =>
            stretch.First > stretch.Last ? $"{Range(stretch.First, clip.Samples - 1)}, {Range(0, stretch.Last)}" : Range(stretch.First, stretch.Last)));
    }

    /// <summary>
    /// The average transform of the samples that are <paramref name="down"/> from <paramref name="from"/> to
    /// <paramref name="to"/> and then <paramref name="from2"/> to <paramref name="to2"/>: a closed gap's lifted samples don't count.
    /// </summary>
    private static MotionTransform Average(ReadOnlySpan<MotionTransform> path, bool[] down, int from, int to, int from2, int to2)
    {
        var reference = Quaternion.Normalize(path[from].Rotation);
        var position = Vector3.Zero;
        var sum = Vector4.Zero;
        var count = 0;
        for (var pass = 0; pass < 2; pass++)
            for (var i = pass == 0 ? from : from2; i <= (pass == 0 ? to : to2); i++)
            {
                if (!down[i])
                    continue;
                position += path[i].Position;
                var q = MotionEvaluator.Align(Quaternion.Normalize(path[i].Rotation), reference);
                sum += new Vector4(q.X, q.Y, q.Z, q.W);
                count++;
            }
        var rotation = sum.LengthSquared() < 1e-12f ? reference : Quaternion.Normalize(new Quaternion(sum.X, sum.Y, sum.Z, sum.W));
        return new MotionTransform(position / count, rotation, Vector3.One);
    }

    /// <summary>
    /// The samples from <paramref name="first"/> to <paramref name="last"/> in runs of at least <paramref name="length"/> where the bone
    /// stays in a box <paramref name="size"/> across (corner to corner) and within <paramref name="turn"/> degrees of the run's first turn.
    /// Greedy, left to right. shortcut: a spot over a loop's seam counts only where each side is long enough on its own; unroll the loop if that bites.
    /// </summary>
    private static bool[] Spots(ReadOnlySpan<MotionTransform> path, int first, int last, int length, float size, float turn)
    {
        var spots = new bool[path.Length];
        var start = first;
        while (start + length - 1 <= last)
        {
            var low = path[start].Position;
            var high = low;
            var end = start;
            while (end < last)
            {
                var next = path[end + 1];
                var nextLow = Vector3.Min(low, next.Position);
                var nextHigh = Vector3.Max(high, next.Position);
                if ((nextHigh - nextLow).Length() > size || Degrees(next.Rotation, path[start].Rotation) > turn)
                    break;
                (low, high) = (nextLow, nextHigh);
                end++;
            }
            if (end - start + 1 < length)
            {
                start++;
                continue;
            }
            Array.Fill(spots, true, start, end - start + 1);
            start = end + 1;
        }
        return spots;
    }

    /// <summary>Speed and turn speed from sample <paramref name="i"/> to <paramref name="to"/>, cut at the range's ends or round the loop; none to read gives the most.</summary>
    private static (float Speed, float Turn) Side(ReadOnlySpan<MotionTransform> path, int i, int to, int first, int last, bool wraps, float sampleRate)
    {
        var reach = wraps ? to : Math.Clamp(to, first, last);
        var span = Math.Abs(reach - i);
        if (span == 0)
            return (float.MaxValue, float.MaxValue);
        var period = path.Length - 1;
        var other = wraps ? (reach % period + period) % period : reach;
        return (Vector3.Distance(path[other].Position, path[i].Position) * sampleRate / span, Degrees(path[other].Rotation, path[i].Rotation) * sampleRate / span);
    }

    private static float Degrees(Quaternion a, Quaternion b) =>
        2 * MathF.Acos(Math.Clamp(Math.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))), 0, 1)) * 180 / MathF.PI;
}
