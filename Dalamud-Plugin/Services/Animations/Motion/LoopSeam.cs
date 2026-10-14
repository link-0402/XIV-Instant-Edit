using System.Numerics;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Services.Animations.Motion;

/// <summary>How a loop runs from its end into its start: cleanly, with a pose gap, or slowing to a stop or jumping in speed.</summary>
internal enum SeamState : byte { Clean, Pops, Stalls }

/// <summary>
/// The jump where one frame runs into another, such as across a cut or a loop's seam: the mean and
/// worst bone angle between the two poses in degrees, and the mean bone speed into the first and out
/// of the second, in degrees per frame.
/// </summary>
internal sealed record JoinGap(float MeanAngle, float WorstAngle, string WorstBone, float SpeedIn, float SpeedOut);

/// <summary>
/// A loop's seam: its gap, the clip's typical speed (the median per-frame mean bone speed), the frame
/// a hold of the start's pose begins at when the loop ends standing still on it, and its state.
/// </summary>
internal sealed record SeamReport(JoinGap Gap, float TypicalSpeed, int? StallFrom, SeamState State);

/// <summary>
/// Measures a loop's seam on a decoded clip. Means are over the clip's own tracks (every bone when it
/// has none recorded), angles in degrees, at whole timeline frames.
/// </summary>
internal static class LoopSeam
{
    /// <summary>A speed jump at the seam over this share of the typical speed counts as a stall.</summary>
    public const float JumpShare = .5f;

    /// <summary>
    /// An end holds the start's pose while every bone stays this close to it, in degrees: tight enough
    /// that a motion only slowing into the pose, as most loops end, doesn't count.
    /// </summary>
    public const float HoldDegrees = .25f;

    /// <summary>The shortest hold that stalls, in frames.</summary>
    public const int HoldFrames = 3;

    /// <summary>A mean pose gap of this many degrees or more pops.</summary>
    public const float PopDegrees = 1f;

    /// <summary>
    /// A loop whose typical speed is under this many degrees per frame barely moves, as idles and poses
    /// do: its seam can pop but never stalls, since a change of speed that small doesn't show.
    /// </summary>
    public const float MinSpeed = .3f;

    /// <summary>For each frame f from 0 to the clip's last but one, the mean bone angle from frame f to f + 1.</summary>
    public static float[] Speeds(MotionClip clip)
    {
        var bones = Tracked(clip);
        var frames = Frames(clip);
        var speeds = new float[frames];
        var previous = Rotations(clip, 0, bones);
        for (var f = 0; f < frames; f++)
        {
            var next = Rotations(clip, f + 1, bones);
            speeds[f] = Mean(previous, next);
            previous = next;
        }
        return speeds;
    }

    /// <summary>The jump from frame <paramref name="left"/> into frame <paramref name="right"/>.</summary>
    public static JoinGap Gap(MotionClip clip, int left, int right)
    {
        var bones = Tracked(clip);
        var a = Rotations(clip, left, bones);
        var b = Rotations(clip, right, bones);
        float worst = 0;
        var worstBone = string.Empty;
        for (var i = 0; i < bones.Length; i++)
            if (Angle(a[i], b[i]) is var angle && angle > worst)
                (worst, worstBone) = (angle, clip.Skeleton.Bones[bones[i]].Name);
        var speedIn = Mean(Rotations(clip, Math.Max(0, left - 1), bones), a);
        var speedOut = Mean(b, Rotations(clip, Math.Min(Frames(clip), right + 1), bones));
        return new JoinGap(Mean(a, b), worst, worstBone, speedIn, speedOut);
    }

    /// <summary>The seam of a loop playing frames <paramref name="from"/> to <paramref name="to"/>, its end running back into its start.</summary>
    public static SeamReport Analyze(MotionClip clip, int from, int to)
    {
        var frames = Frames(clip);
        to = Math.Clamp(to, 1, frames);
        from = Math.Clamp(from, 0, to - 1);
        var speeds = Speeds(clip);
        var span = speeds.AsSpan(from, to - from).ToArray();
        Array.Sort(span);
        var typical = span.Length % 2 == 1 ? span[span.Length / 2] : (span[span.Length / 2 - 1] + span[span.Length / 2]) / 2;
        var gap = Gap(clip, to, from);

        // Walk back from the end while every bone stays on the start's pose: those frames hold still on
        // it. A motion slowing into the pose leaves it too soon to count, and is left to the ease.
        var bones = Tracked(clip);
        var start = Rotations(clip, from, bones);
        var f = to + 1;
        while (f - 1 > from && Worst(Rotations(clip, f - 1, bones), start) <= HoldDegrees)
            f--;
        var moves = typical >= MinSpeed;
        int? stall = moves && f <= to - HoldFrames ? f : null;
        var state = stall != null || moves && Math.Abs(gap.SpeedOut - gap.SpeedIn) > JumpShare * typical ? SeamState.Stalls
            : gap.MeanAngle >= PopDegrees ? SeamState.Pops : SeamState.Clean;
        return new SeamReport(gap, typical, stall, state);
    }

    /// <summary>The clip's length in whole timeline frames.</summary>
    private static int Frames(MotionClip clip) => Math.Max(1, (int)Math.Round(clip.Duration * TimelineDocument.Fps));

    private static int[] Tracked(MotionClip clip) =>
        clip.SourceTracks.IsDefaultOrEmpty ? [.. Enumerable.Range(0, clip.Bones)] : [.. clip.SourceTracks.Select(t => (int)t).Where(t => t < clip.Bones)];

    /// <summary>The <paramref name="bones"/>' rotations at a whole timeline frame, between the samples around it.</summary>
    private static Quaternion[] Rotations(MotionClip clip, int frame, int[] bones)
    {
        var at = clip.Duration <= 0 ? 0 : Math.Clamp(frame / (double)TimelineDocument.Fps / clip.Duration, 0, 1) * (clip.Samples - 1);
        if (Math.Abs(at - Math.Round(at)) < 1e-4)
            at = Math.Round(at);
        var low = Math.Min((int)at, clip.Samples - 1);
        var high = Math.Min(low + 1, clip.Samples - 1);
        var t = (float)(at - low);
        var rotations = new Quaternion[bones.Length];
        for (var i = 0; i < bones.Length; i++)
            rotations[i] = high == low || t <= 0 ? clip.Get(low, bones[i]).Rotation
                : MotionEvaluator.Slerp(clip.Get(low, bones[i]).Rotation, clip.Get(high, bones[i]).Rotation, t);
        return rotations;
    }

    private static float Worst(Quaternion[] a, Quaternion[] b)
    {
        float worst = 0;
        for (var i = 0; i < a.Length; i++)
            worst = Math.Max(worst, Angle(a[i], b[i]));
        return worst;
    }

    private static float Mean(Quaternion[] a, Quaternion[] b)
    {
        if (a.Length == 0)
            return 0;
        double sum = 0;
        for (var i = 0; i < a.Length; i++)
            sum += Angle(a[i], b[i]);
        return (float)(sum / a.Length);
    }

    private static float Angle(Quaternion a, Quaternion b) =>
        2 * MathF.Acos(Math.Clamp(Math.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))), 0, 1)) * 180 / MathF.PI;
}
