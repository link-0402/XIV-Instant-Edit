using System.Collections.Immutable;
using System.Numerics;
using InstantEdit.Models;

namespace InstantEdit.Services.Animations.Motion;

/// <summary>
/// A fix layer's passes over a whole clip (<see cref="MotionFixKind"/>), applied by <see cref="MotionEvaluator.Apply"/>.
/// Pure managed code: the bake and Live use it alike.
/// </summary>
internal static class MotionFixes
{
    /// <summary>Smooth jitter's threshold at full strength: a shake below it is smoothed, a move past twice it kept.</summary>
    private const float JitterDegrees = 4, JitterDistance = .01f;

    /// <summary>The low-pass over a sample and two neighbours each side, binomial, summing to 16.</summary>
    private static readonly float[] Window = [1, 4, 6, 4, 1];

    /// <summary>
    /// <paramref name="loop"/>'s timeline frames 0 and 1 as local transforms by bone name: what a match lands on, and the
    /// loop's speed per timeline frame whatever its sample rate. A loop with no frame 1 gives frame 0 twice.
    /// </summary>
    public static MotionFix LoopPose(MotionClip loop)
    {
        var first = loop.Pose(0);
        var second = loop.Pose(Math.Min(1, loop.Frame(loop.Samples - 1)));
        var firsts = ImmutableDictionary.CreateBuilder<string, BoneTransform>(StringComparer.Ordinal);
        var seconds = ImmutableDictionary.CreateBuilder<string, BoneTransform>(StringComparer.Ordinal);
        for (var b = 0; b < loop.Bones; b++)
        {
            var name = loop.Skeleton.Bones[b].Name;
            firsts[name] = first[b].ToBone();
            seconds[name] = second[b].ToBone();
        }
        return new MotionFix(MotionFixKind.MatchLoop, firsts.ToImmutable(), seconds.ToImmutable());
    }

    /// <summary>
    /// The frames a match blends over on a motion <paramref name="end"/> frames long: the layer's length back from the
    /// motion's last frame. Its stored frames only carry that length, so a cut or a length change can't leave the blend mid-clip.
    /// </summary>
    public static (int Start, int End) MatchSpan(MotionLayer layer, float end)
    {
        var last = (int)Math.Round(end, MidpointRounding.AwayFromZero);
        return (Math.Max(0, last - (layer.End - layer.Start)), last);
    }

    /// <summary>
    /// Match startup to loop: a quintic offset, zero with zero speed where the blend starts, that puts each loop bone's
    /// last sample on the loop's frame 0 moving at its frame 0 to 1 speed. Rotations compose on the left like the cut
    /// joins. Gaps are read from the clip as it arrives. Loop bones the skeleton lacks or the layer's limit leaves out are skipped.
    /// </summary>
    internal static void ApplyMatch(MotionClip clip, MotionLayer layer, Dictionary<string, int> index, float end, CancellationToken token)
    {
        if (layer.Fix is not { LoopFirst: { } firsts, LoopSecond: { } seconds })
            return;
        var rotate = layer.Components.HasFlag(PoseComponents.Rotation);
        var move = layer.Components.HasFlag(PoseComponents.Position);
        var last = clip.Samples - 1;
        var lastFrame = clip.Frame(last);
        var start = MatchSpan(layer, end).Start;
        // Measured to the last sample, not the rounded end, so the last sample is exactly s = 1.
        var frames = lastFrame - start;
        var step = last > 0 ? lastFrame - clip.Frame(last - 1) : 0;
        var first = 0;
        while (first < last && clip.Frame(first) < start - MotionEvaluator.FrameTolerance)
            first++;
        foreach (var (name, target) in firsts)
        {
            if (!index.TryGetValue(name, out var b) || !seconds.TryGetValue(name, out var next) ||
                !layer.Bones.IsEmpty && !layer.Bones.Contains(name, StringComparer.Ordinal))
                continue;
            token.ThrowIfCancellationRequested();
            var cur = clip.Get(last, b);
            var curTurn = Quaternion.Normalize(cur.Rotation);
            var aim = Quaternion.Normalize(target.Rotation);
            var rotationGap = MotionCutter.Log(MotionEvaluator.Align(aim, curTurn) * Quaternion.Inverse(curTurn));
            var positionGap = target.Position - cur.Position;
            Vector3 rotationSpeed = Vector3.Zero, positionSpeed = Vector3.Zero;
            if (step > 0)
            {
                // Speed gaps per timeline frame: the loop's frame 0 to 1 change less the clip's last pair's.
                var prev = clip.Get(last - 1, b);
                var prevTurn = Quaternion.Normalize(prev.Rotation);
                rotationSpeed = MotionCutter.Log(MotionEvaluator.Align(Quaternion.Normalize(next.Rotation), aim) * Quaternion.Inverse(aim)) -
                                MotionCutter.Log(MotionEvaluator.Align(curTurn, prevTurn) * Quaternion.Inverse(prevTurn)) / step;
                positionSpeed = next.Position - target.Position - (cur.Position - prev.Position) / step;
            }
            // Every sample from the blend's start through the last, which may sit a fraction past the rounded end.
            for (var i = first; i <= last; i++)
            {
                var s = frames > 0 ? Math.Clamp((clip.Frame(i) - start) / frames, 0, 1) : 1;
                var gap = layer.Amount * MotionCutter.Rise(s);
                var speed = layer.Amount * frames * MotionCutter.RiseSpeed(s);
                var value = clip.Get(i, b);
                var turn = rotationGap * gap + rotationSpeed * speed;
                if (rotate && turn != Vector3.Zero)
                    value = value with { Rotation = Quaternion.Normalize(MotionCutter.Exp(turn) * value.Rotation) };
                if (move)
                    value = value with { Position = value.Position + positionGap * gap + positionSpeed * speed };
                clip.Set(i, b, value);
            }
        }
    }

    /// <summary>
    /// Smooth jitter over the layer's span: each sample of <paramref name="bones"/> moves toward a short low-pass of its
    /// neighbours where it differs from it by little (a shake) and stays where it differs by a lot (a snap), through the
    /// layer's fades. Bones that never move are left alone, so no track is made from rounding.
    /// </summary>
    internal static void ApplySmooth(MotionClip clip, MotionLayer layer, IReadOnlyCollection<int> bones, float loop, float end, CancellationToken token)
    {
        var rotate = layer.Components.HasFlag(PoseComponents.Rotation);
        var move = layer.Components.HasFlag(PoseComponents.Position);
        int first = -1, last = -1;
        for (var i = 0; i < clip.Samples; i++)
            if (MotionEvaluator.Inside(layer, clip.Frame(i), end))
            {
                if (first < 0) first = i;
                last = i;
            }
        if (first < 0)
            return;
        // A looping clip's last sample is its first one's moment (MotionCutter writes the first over it at a seam,
        // PinPlanting wraps by Samples - 1), so round the seam neighbours wrap by Samples - 1.
        var wraps = MotionEvaluator.Wraps(layer, loop);
        var turnLimit = layer.Amount * JitterDegrees * MathF.PI / 180;
        var moveLimit = layer.Amount * JitterDistance;
        var rotations = new Quaternion[clip.Samples];
        var positions = new Vector3[clip.Samples];
        foreach (var b in bones)
        {
            token.ThrowIfCancellationRequested();
            for (var i = 0; i < clip.Samples; i++)
            {
                var value = clip.Get(i, b);
                rotations[i] = Quaternion.Normalize(value.Rotation);
                positions[i] = value.Position;
            }
            var inverse = Quaternion.Inverse(rotations[0]);
            bool turns = false, moves = false;
            for (var i = 1; i < clip.Samples && !(turns && moves); i++)
            {
                var turn = inverse * MotionEvaluator.Align(rotations[i], rotations[0]);
                turns |= rotate && new Vector3(turn.X, turn.Y, turn.Z).LengthSquared() > 1e-10f;
                moves |= move && Vector3.DistanceSquared(positions[i], positions[0]) > 1e-12f;
            }
            if (!turns && !moves)
                continue;
            for (var i = first; i <= last; i++)
            {
                var share = MotionEvaluator.Fade(layer, clip.Frame(i), end);
                if (share <= 0)
                    continue;
                var q = rotations[i];
                var back = Quaternion.Inverse(q);
                Vector3 average = Vector3.Zero, mean = Vector3.Zero;
                for (var d = -2; d <= 2; d++)
                {
                    var (rotation, position) = Neighbour(rotations, positions, i + d, wraps);
                    average += Window[d + 2] * MotionCutter.Log(MotionEvaluator.Align(rotation, q) * back);
                    mean += Window[d + 2] * position;
                }
                average /= 16;
                mean /= 16;
                var value = clip.Get(i, b);
                var smoothTurn = turns ? Smoothing(average.Length(), turnLimit) * share : 0;
                var smoothMove = moves ? Smoothing(Vector3.Distance(mean, positions[i]), moveLimit) * share : 0;
                if (smoothTurn > 0)
                    value = value with { Rotation = MotionEvaluator.Slerp(value.Rotation, Quaternion.Normalize(MotionCutter.Exp(average) * q), smoothTurn) };
                if (smoothMove > 0)
                    value = value with { Position = Vector3.Lerp(value.Position, mean, smoothMove) };
                clip.Set(i, b, value);
            }
        }
    }

    /// <summary>How far a sample <paramref name="residual"/> off its low-pass moves to it: fully below <paramref name="limit"/>, not at all past twice it.</summary>
    private static float Smoothing(float residual, float limit) =>
        limit > 0 ? 1 - AnimationPoseRules.SmoothStep(Math.Clamp((residual - limit) / limit, 0, 1)) : 0;

    /// <summary>
    /// Sample <paramref name="at"/>, round a loop; past a clip's ends, the sample as far in mirrored through the end one
    /// (its turn from the end undone, its move reversed), so steady motion there reads as steady and isn't pulled in.
    /// </summary>
    private static (Quaternion Rotation, Vector3 Position) Neighbour(Quaternion[] rotations, Vector3[] positions, int at, bool wraps)
    {
        var samples = rotations.Length;
        if (wraps)
        {
            var period = samples - 1;
            at = (at % period + period) % period;
        }
        if (at >= 0 && at < samples)
            return (rotations[at], positions[at]);
        var end = at < 0 ? 0 : samples - 1;
        var mirror = Math.Clamp(2 * end - at, 0, samples - 1);
        var turn = MotionEvaluator.Align(rotations[mirror], rotations[end]) * Quaternion.Inverse(rotations[end]);
        return (MotionCutter.Exp(-MotionCutter.Log(turn)) * rotations[end], 2 * positions[end] - positions[mirror]);
    }
}
