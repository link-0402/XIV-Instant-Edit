using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Services.Animations.Motion;

/// <summary>
/// Samples <paramref name="First"/> to <paramref name="Last"/> (inclusive) where a bone is down, and the
/// spot it is held at: its average position and turn over them. On a loop a stretch over the seam has
/// <paramref name="First"/> past <paramref name="Last"/>: it runs from First to the end and on from the start to Last.
/// </summary>
internal sealed record PlantedStretch(int First, int Last, MotionTransform Held);

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
        var position = Vector3.Zero;
        var sum = Vector4.Zero;
        Quaternion? reference = null;
        foreach (var stretch in Stretches)
        {
            var hold = HoldOf(stretch, sample);
            if (hold <= 0)
                continue;
            if (hold >= 1 && total == 0 && Stretches.Count(other => HoldOf(other, sample) > 0) == 1)
                return stretch.Held;
            total += hold;
            position += stretch.Held.Position * hold;
            reference ??= stretch.Held.Rotation;
            var q = MotionEvaluator.Align(stretch.Held.Rotation, reference.Value);
            sum += new Vector4(q.X, q.Y, q.Z, q.W) * hold;
        }
        if (total <= 0)
            return null;
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

    /// <summary>Below which a bone counts as down: speed in metres a second, turn speed in degrees a second, height above its lowest in metres.</summary>
    public static (float Speed, float TurnSpeed, float Height) Limits(PinSensitivity sensitivity) => sensitivity switch
    {
        PinSensitivity.Strict => (.05f, 20, .02f),
        PinSensitivity.Loose => (.30f, 90, .08f),
        _ => (.15f, 45, .04f),
    };

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
        {
            var stretch = stretches[s];
            var held = stretch.Held.Position;
            // Each sample's reach is a ball round its root; passes of projecting onto each settle on a spot inside all of them.
            for (var pass = 0; pass < ReachPasses; pass++)
            {
                var moved = false;
                foreach (var i in SamplesIn(stretch, path.Samples))
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
            stretches[s] = stretch with { Held = stretch.Held with { Position = held } };
        }
        return path with { Stretches = stretches.ToImmutable() };
    }

    private const int ReachPasses = 16;

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
    /// stretch running off the end joins one starting at the beginning.
    /// </summary>
    public static PlantedPath Find(ReadOnlySpan<MotionTransform> path, float sampleRate, int first, int last, bool wraps, PinSensitivity sensitivity,
        int gapSamples, int minSamples, int easeSamples)
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
            lowest = Math.Min(lowest, path[i].Position.Y);

        // Runs of samples that are down.
        var down = new bool[count];
        var runs = new List<(int First, int Last)>();
        var start = -1;
        for (var i = first; i <= last; i++)
        {
            // Speeds from the next sample; the last from the one before, or round the loop from sample 1.
            var next = i < last ? i + 1 : wraps ? 1 : i - 1;
            var speed = Vector3.Distance(path[next].Position, path[i].Position) * sampleRate;
            var turn = Degrees(path[next].Rotation, path[i].Rotation) * sampleRate;
            down[i] = speed < speedLimit && turn < turnLimit && path[i].Position.Y - lowest < heightLimit;
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
        if (wraps && runs.Count > 1 && runs[^1].First == last)
        {
            // Down only on the last sample, which is the first one's moment: the first run already starts there.
            runs[0] = (first, runs[0].Last);
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

    private static float Degrees(Quaternion a, Quaternion b) =>
        2 * MathF.Acos(Math.Clamp(Math.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))), 0, 1)) * 180 / MathF.PI;
}
