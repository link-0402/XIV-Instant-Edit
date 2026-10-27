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
    /// <paramref name="loop"/>'s timeline frames <paramref name="fade"/> and one after as local transforms by bone name:
    /// the frame the game shows as a startup ends that many frames into its cross-fade, which a match lands on, and the
    /// loop's speed there per timeline frame whatever its sample rate. Past the loop's end the frames wrap round it, as
    /// the game plays it on; a loop with no frame 1 gives frame 0 twice.
    /// </summary>
    public static MotionFix LoopPose(MotionClip loop, int fade = 0)
    {
        var length = loop.Frame(loop.Samples - 1);
        float At(int frame) => length <= 0 ? 0 : frame <= length ? frame : frame % length;
        fade = Math.Clamp(fade, 0, LoopFades.Most);
        var first = loop.Pose(At(fade));
        var second = loop.Pose(length < 1 ? Math.Max(0, length) : At(fade + 1));
        var firsts = ImmutableDictionary.CreateBuilder<string, BoneTransform>(StringComparer.Ordinal);
        var seconds = ImmutableDictionary.CreateBuilder<string, BoneTransform>(StringComparer.Ordinal);
        for (var b = 0; b < loop.Bones; b++)
        {
            var name = loop.Skeleton.Bones[b].Name;
            firsts[name] = first[b].ToBone();
            seconds[name] = second[b].ToBone();
        }
        return new MotionFix(MotionFixKind.MatchLoop, firsts.ToImmutable(), seconds.ToImmutable(), fade);
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
    /// last sample on the loop frame the game shows then (<see cref="MotionFix.Fade"/>) moving at its speed there. Rotations compose on the left like the cut
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
                // Speed gaps per timeline frame: the loop's frame 0 to 1 change less the clip's last pair's, which the
                // gap's turn on the left carries round with it, unbent through that turn (see Unbend).
                var prev = clip.Get(last - 1, b);
                var prevTurn = Quaternion.Normalize(prev.Rotation);
                var landed = rotationGap * layer.Amount;
                rotationSpeed = Unbend(landed, MotionCutter.Log(MotionEvaluator.Align(Quaternion.Normalize(next.Rotation), aim) * Quaternion.Inverse(aim)) -
                                               Vector3.Transform(MotionCutter.Log(MotionEvaluator.Align(curTurn, prevTurn) * Quaternion.Inverse(prevTurn)) / step,
                                                   MotionCutter.Exp(landed)));
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
    /// The frames a close loop changes on a motion <paramref name="end"/> frames long: the end side's and the start side's,
    /// an unused side empty. Its length N is the layer's, at least 1 and at most the motion's. Before puts all of it at the
    /// end, After at the start, Centered the end's ceil(N / 2) frames and the start's floor(N / 2).
    /// </summary>
    public static ((int Start, int End) End, (int Start, int End) Start) CloseSpans(MotionLayer layer, float end)
    {
        var last = Math.Max(0, (int)Math.Round(end, MidpointRounding.AwayFromZero));
        // At most the motion's length, so a motion under a frame long gets two empty sides.
        var length = Math.Min(Math.Max(1, layer.End - layer.Start), last);
        return layer.Fix?.Placement switch
        {
            JoinPlacement.Before => ((last - length, last), (0, 0)),
            JoinPlacement.After => ((last, last), (0, length)),
            _ => ((last - (length + 1) / 2, last), (0, length / 2)),
        };
    }

    /// <summary>
    /// Close loop: blends a looping clip's end into its start, quintic offsets that put the end on the start's pose moving
    /// at its speed (<see cref="CloseSpans"/> for the frames each side changes). Rotations compose on the left like the cut
    /// joins. Gaps and speeds are read from the clip as it arrives; at full amount the last sample is then the first.
    /// </summary>
    internal static void ApplyClose(MotionClip clip, MotionLayer layer, Dictionary<string, int> index, float end, CancellationToken token)
    {
        if (clip.Samples <= 2)
            return;
        var rotate = layer.Components.HasFlag(PoseComponents.Rotation);
        var move = layer.Components.HasFlag(PoseComponents.Position);
        var amount = layer.Amount;
        var (endSpan, startSpan) = CloseSpans(layer, end);
        var last = clip.Samples - 1;
        var lastFrame = clip.Frame(last);
        // The end side is measured to the last sample, not the rounded end, so the last sample is exactly s = 1.
        var endFrames = endSpan.End > endSpan.Start ? lastFrame - endSpan.Start : 0;
        float startFrames = startSpan.End - startSpan.Start;
        // Before gives the end all of the gap, After the start, Centered each half; a side with no frames (a centered
        // blend 1 frame long) leaves it all to the other.
        var endShare = startFrames <= 0 ? 1f : endFrames <= 0 ? 0f : layer.Fix?.Placement switch
        {
            JoinPlacement.Before => 1f,
            JoinPlacement.After => 0f,
            _ => .5f,
        };
        var startShare = 1 - endShare;
        var step = lastFrame - clip.Frame(last - 1);

        // Each sample's share of the gap and of the speed gap: rising to the gap over the end, falling from minus it over the start.
        var weights = new List<(int Sample, float Gap, float Speed)>();
        for (var i = 0; i <= last; i++)
        {
            var frame = clip.Frame(i);
            float gap = 0, speed = 0;
            if (endShare > 0 && endFrames > 0 && frame >= endSpan.Start - MotionEvaluator.FrameTolerance)
            {
                var s = Math.Clamp((frame - endSpan.Start) / endFrames, 0, 1);
                gap += endShare * amount * MotionCutter.Rise(s);
                speed += endShare * amount * endFrames * MotionCutter.RiseSpeed(s);
            }
            if (startShare > 0 && startFrames > 0 && frame < startSpan.End)
            {
                var s = Math.Clamp(frame / startFrames, 0, 1);
                gap -= startShare * amount * MotionCutter.Fall(s);
                speed -= startShare * amount * startFrames * MotionCutter.FallSpeed(s);
            }
            if (gap != 0 || speed != 0)
                weights.Add((i, gap, speed));
        }

        var bones = layer.Bones.IsEmpty ? [.. Enumerable.Range(0, clip.Bones)] : layer.Bones.Where(index.ContainsKey).Select(name => index[name]).ToArray();
        // Every gap and speed gap before any sample is written: the start side changes samples the end side's are read from.
        var gaps = new (Vector3 Turn, Vector3 Move, Vector3 TurnSpeed, Vector3 MoveSpeed)[bones.Length];
        for (var k = 0; k < bones.Length; k++)
        {
            var b = bones[k];
            var (first, second, cur, prev) = (clip.Get(0, b), clip.Get(1, b), clip.Get(last, b), clip.Get(last - 1, b));
            var firstTurn = Quaternion.Normalize(first.Rotation);
            var curTurn = Quaternion.Normalize(cur.Rotation);
            var rotationGap = MotionCutter.Log(MotionEvaluator.Align(firstTurn, curTurn) * Quaternion.Inverse(curTurn));
            Vector3 rotationSpeed = Vector3.Zero, positionSpeed = Vector3.Zero;
            if (step > 0)
            {
                // Speed gaps per timeline frame: the start's first pair's change less the end's last pair's, which the
                // gap's turn on the left carries round with it, unbent through that turn (see Unbend).
                var prevTurn = Quaternion.Normalize(prev.Rotation);
                var landed = rotationGap * amount;
                rotationSpeed = Unbend(landed, (MotionCutter.Log(MotionEvaluator.Align(Quaternion.Normalize(second.Rotation), firstTurn) * Quaternion.Inverse(firstTurn)) -
                                                Vector3.Transform(MotionCutter.Log(MotionEvaluator.Align(curTurn, prevTurn) * Quaternion.Inverse(prevTurn)), MotionCutter.Exp(landed))) / step);
                positionSpeed = (second.Position - first.Position - (cur.Position - prev.Position)) / step;
            }
            gaps[k] = (rotationGap, first.Position - cur.Position, rotationSpeed, positionSpeed);
        }

        for (var k = 0; k < bones.Length; k++)
        {
            token.ThrowIfCancellationRequested();
            var b = bones[k];
            var (rotationGap, positionGap, rotationSpeed, positionSpeed) = gaps[k];
            foreach (var (i, gap, speed) in weights)
            {
                var value = clip.Get(i, b);
                var turn = rotationGap * gap + rotationSpeed * speed;
                if (rotate && turn != Vector3.Zero)
                    value = value with { Rotation = Quaternion.Normalize(MotionCutter.Exp(turn) * value.Rotation) };
                if (move)
                    value = value with { Position = value.Position + positionGap * gap + positionSpeed * speed };
                clip.Set(i, b, value);
            }
            // At full amount the last sample is the first, as the game wraps it, on its neighbour's hemisphere.
            if (amount >= 1)
            {
                var first = clip.Get(0, b);
                var value = clip.Get(last, b);
                if (rotate)
                    value = value with { Rotation = MotionEvaluator.Align(first.Rotation, clip.Get(last - 1, b).Rotation) };
                if (move)
                    value = value with { Position = first.Position };
                clip.Set(last, b, value);
            }
        }
    }

    /// <summary>
    /// The rate of change of a turn vector at <paramref name="turn"/> that turns Exp(turn) at <paramref name="speed"/> (as a
    /// left change): the inverse left Jacobian. Off the turn's axis the two differ by about a third of the speed at 40 degrees.
    /// </summary>
    private static Vector3 Unbend(Vector3 turn, Vector3 speed)
    {
        var angle = turn.Length();
        var cross = Vector3.Cross(turn, speed);
        var k = angle < .05f ? 1f / 12 : 1 / (angle * angle) - 1 / (2 * angle * MathF.Tan(angle / 2));
        return speed - cross / 2 + k * Vector3.Cross(turn, cross);
    }

    /// <summary>
    /// Smooth jitter over the layer's span: each sample of <paramref name="bones"/> moves toward a short low-pass of its
    /// neighbours where the samples round it differ from theirs by little (a shake) and stays where one differs by a lot
    /// (a snap or a fast curve), through the layer's fades. The test and the share it gives are taken over neighbours,
    /// not per sample. Game clips decode at about twice their key rate, the samples between keys halfway between their
    /// neighbours: a track is smoothed on its keys and the samples between are put back on the line between them, since
    /// keys lie farther off the low-pass than the samples between and smoothing both apart left a ripple. Bones that
    /// never move are left alone, so no track is made from rounding.
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
        var averages = new Vector3[clip.Samples];
        var means = new Vector3[clip.Samples];
        var turnOff = new float[clip.Samples];
        var moveOff = new float[clip.Samples];
        var turnShare = new float[clip.Samples];
        var moveShare = new float[clip.Samples];
        var keys = new List<int>(clip.Samples);
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
            // The track's keys: every sample but those halfway between their neighbours, which game clips decoding at
            // twice their key rate put between keys, one at a time after a key so keys stay evenly spaced (a steady
            // stretch, halfway throughout, keeps every other sample). The keys are smoothed as a track of their own (the
            // ends always keys, so a loop still wraps by one less than their count) and the samples between go back on the line.
            keys.Clear();
            for (var i = 0; i < clip.Samples; i++)
                if (i == 0 || i == clip.Samples - 1 || keys[^1] != i - 1 || !Halfway(rotations, positions, i))
                    keys.Add(i);
            var count = keys.Count;
            var keyRotations = new Quaternion[count];
            var keyPositions = new Vector3[count];
            var moved = new bool[count];
            for (var k = 0; k < count; k++)
                (keyRotations[k], keyPositions[k]) = (rotations[keys[k]], positions[keys[k]]);
            // Every key's low-pass and how far it lies off it, the span's neighbours included.
            for (var k = 0; k < count; k++)
            {
                var q = keyRotations[k];
                var back = Quaternion.Inverse(q);
                Vector3 average = Vector3.Zero, mean = Vector3.Zero;
                for (var d = -2; d <= 2; d++)
                {
                    var (rotation, position) = Neighbour(keyRotations, keyPositions, k + d, wraps);
                    average += Window[d + 2] * MotionCutter.Log(MotionEvaluator.Align(rotation, q) * back);
                    mean += Window[d + 2] * position;
                }
                averages[k] = average / 16;
                means[k] = mean / 16;
                turnOff[k] = averages[k].Length();
                moveOff[k] = Vector3.Distance(means[k], keyPositions[k]);
            }
            // A key is a shake only if no neighbour lies far off its low-pass...
            for (var k = 0; k < count; k++)
            {
                float turnMost = 0, moveMost = 0;
                for (var d = -2; d <= 2; d++)
                {
                    var at = Near(k + d, count, wraps);
                    turnMost = MathF.Max(turnMost, turnOff[at]);
                    moveMost = MathF.Max(moveMost, moveOff[at]);
                }
                turnShare[k] = turns ? Smoothing(turnMost, turnLimit) : 0;
                moveShare[k] = moves ? Smoothing(moveMost, moveLimit) : 0;
            }
            for (var k = 0; k < count; k++)
            {
                var i = keys[k];
                var share = i < first || i > last ? 0 : MotionEvaluator.Fade(layer, clip.Frame(i), end);
                if (share <= 0)
                    continue;
                // ...and neighbours move by much the same share, so the hand-over to a kept move adds no ripple.
                float smoothTurn = 0, smoothMove = 0;
                for (var d = -2; d <= 2; d++)
                {
                    var at = Near(k + d, count, wraps);
                    smoothTurn += Window[d + 2] * turnShare[at];
                    smoothMove += Window[d + 2] * moveShare[at];
                }
                smoothTurn *= share / 16;
                smoothMove *= share / 16;
                var value = clip.Get(i, b);
                if (smoothTurn > 0)
                    value = value with { Rotation = MotionEvaluator.Slerp(value.Rotation, Quaternion.Normalize(MotionCutter.Exp(averages[k]) * keyRotations[k]), smoothTurn) };
                if (smoothMove > 0)
                    value = value with { Position = Vector3.Lerp(value.Position, means[k], smoothMove) };
                clip.Set(i, b, value);
                moved[k] = smoothTurn > 0 || smoothMove > 0;
            }
            // Only between keys that moved: elsewhere the samples between stay as they are, not near halfway made exact.
            for (var k = 0; k + 1 < count; k++)
            {
                if (!moved[k] && !moved[k + 1])
                    continue;
                var (from, to) = (keys[k], keys[k + 1]);
                for (var i = Math.Max(from + 1, first); i < to && i <= last; i++)
                {
                    var (before, after, value) = (clip.Get(from, b), clip.Get(to, b), clip.Get(i, b));
                    var t = (float)(i - from) / (to - from);
                    if (rotate)
                        value = value with { Rotation = MotionEvaluator.Slerp(before.Rotation, after.Rotation, t) };
                    if (move)
                        value = value with { Position = Vector3.Lerp(before.Position, after.Position, t) };
                    clip.Set(i, b, value);
                }
            }
        }
    }

    /// <summary>
    /// A hand shape over the clip: each finger bone's local rotation goes to its shape's target by the layer's weight. With
    /// Keep finger motion, the shape takes the finger's average over the layer's samples instead, and each sample keeps its
    /// turn from that average. Sides whose hand the skeleton lacks are skipped; positions, scales and other bones are left alone.
    /// </summary>
    internal static void ApplyHand(MotionClip clip, MotionLayer layer, float end, CancellationToken token)
    {
        if (layer.Fix?.Hand is not { } hand)
            return;
        foreach (var targets in Hands(clip.Skeleton, hand))
        {
            var offsets = hand.KeepMotion ? Offsets(clip, layer, targets, end, token) : [];
            for (var i = 0; i < clip.Samples; i++)
            {
                if ((i & 63) == 0)
                    token.ThrowIfCancellationRequested();
                var weight = MotionEvaluator.Weight(layer, clip.Frame(i), end);
                if (weight <= 0)
                    continue;
                foreach (var (b, target) in targets)
                {
                    var value = clip.Get(i, b);
                    var aim = offsets.TryGetValue(b, out var offset) ? Quaternion.Normalize(offset * Quaternion.Normalize(value.Rotation)) : target;
                    clip.Set(i, b, value with { Rotation = MotionEvaluator.Slerp(value.Rotation, aim, weight) });
                }
            }
        }
    }

    /// <summary>A Replace hand shape on one pose at <paramref name="weight"/>, as <see cref="ApplyHand"/> sets a sample.</summary>
    internal static void ApplyHandAt(SkeletonDescription skeleton, MotionTransform[] pose, MotionLayer layer, float weight)
    {
        if (weight <= 0 || layer.Fix?.Hand is not { KeepMotion: false } hand)
            return;
        foreach (var targets in Hands(skeleton, hand))
            foreach (var (b, target) in targets)
                pose[b] = pose[b] with { Rotation = MotionEvaluator.Slerp(pose[b].Rotation, target, weight) };
    }

    /// <summary>
    /// Reset root turn over the clip: each root bone's rotation goes to <see cref="ResetTurn"/>'s by the layer's weight.
    /// Samples it wouldn't change are left as they are, so a root at rest gets no track from rounding. With Y ticked, a
    /// facing turn baked under the root then comes out of the body too (<see cref="ResetBodyFacing"/>).
    /// </summary>
    internal static void ApplyResetRoot(MotionClip clip, MotionLayer layer, float end, CancellationToken token)
    {
        if (layer.Fix is not { Axes: not MotionAxes.None and var axes })
            return;
        var roots = Roots(clip.Skeleton);
        for (var i = 0; i < clip.Samples; i++)
        {
            if ((i & 63) == 0)
                token.ThrowIfCancellationRequested();
            var weight = MotionEvaluator.Weight(layer, clip.Frame(i), end);
            if (weight <= 0)
                continue;
            foreach (var b in roots)
            {
                var value = clip.Get(i, b);
                if (Reset(value.Rotation, clip.Skeleton.Bones[b].Reference.Rotation, axes, weight) is { } turned)
                    clip.Set(i, b, value with { Rotation = turned });
            }
        }
        if (axes.HasFlag(MotionAxes.Y))
            ResetBodyFacing(clip, layer, end, token);
    }

    // The bones whose turn about Y from their rest, in character space, is where the body faces.
    private static readonly string[] FacingBones = ["j_kosi", "j_sebo_a"];

    /// <summary>
    /// Takes a facing turn out of the body bone (n_hara), for poses saved with the turn under an unanimated root: the
    /// hips' and lower spine's turn about Y from their rest in character space, averaged over the layer's samples, is
    /// turned back on the body bone by the layer's weight, its spot with it, so the motion's own sway stays. A skeleton
    /// without those bones, or a body already facing forward, is left as it is. One pose can't measure it, so Live's
    /// Bones window (<see cref="ApplyResetRootAt"/>) leaves it to the bake.
    /// </summary>
    private static void ResetBodyFacing(MotionClip clip, MotionLayer layer, float end, CancellationToken token)
    {
        var skeleton = clip.Skeleton;
        var index = MotionEvaluator.Index(skeleton);
        if (!index.TryGetValue(MotionHold.Body, out var body))
            return;
        int[] facers = [.. FacingBones.Where(index.ContainsKey).Select(name => index[name]).Where(b => skeleton.Bones[b].Parent == body)];
        if (facers.Length == 0)
            return;
        var restBody = RestModel(skeleton, body);
        var parent = skeleton.Bones[body].Parent;
        var sum = Vector4.Zero;
        Quaternion? first = null;
        for (var i = 0; i < clip.Samples; i++)
        {
            if ((i & 63) == 0)
                token.ThrowIfCancellationRequested();
            if (!MotionEvaluator.Inside(layer, clip.Frame(i), end))
                continue;
            var model = Model(clip, i, body);
            foreach (var b in facers)
            {
                var rest = restBody * skeleton.Bones[b].Reference.Rotation;
                var turn = Twist(Quaternion.Normalize(model * clip.Get(i, b).Rotation * Quaternion.Inverse(rest)), Vector3.UnitY);
                first ??= turn;
                turn = MotionEvaluator.Align(turn, first.Value);
                sum += new Vector4(turn.X, turn.Y, turn.Z, turn.W);
            }
        }
        if (sum.LengthSquared() < 1e-12f)
            return;
        var back = Quaternion.Inverse(Quaternion.Normalize(new Quaternion(sum.X, sum.Y, sum.Z, sum.W)));
        if (new Vector3(back.X, back.Y, back.Z).LengthSquared() <= 1e-10f)
            return;
        for (var i = 0; i < clip.Samples; i++)
        {
            if ((i & 63) == 0)
                token.ThrowIfCancellationRequested();
            var weight = MotionEvaluator.Weight(layer, clip.Frame(i), end);
            if (weight <= 0)
                continue;
            // The turn is about the character's up; in the parent's space it turns with the parent.
            var above = parent >= 0 ? Model(clip, i, parent) : Quaternion.Identity;
            var local = Quaternion.Normalize(Quaternion.Inverse(above) * MotionEvaluator.Slerp(Quaternion.Identity, back, weight) * above);
            var value = clip.Get(i, body);
            clip.Set(i, body, value with
            {
                Rotation = Quaternion.Normalize(local * value.Rotation),
                Position = Vector3.Transform(value.Position, local),
            });
        }
    }

    /// <summary>Bone <paramref name="b"/>'s rotation in character space at sample <paramref name="i"/>.</summary>
    private static Quaternion Model(MotionClip clip, int i, int b)
    {
        var model = Quaternion.Identity;
        for (var at = b; at >= 0; at = clip.Skeleton.Bones[at].Parent)
            model = Quaternion.Normalize(clip.Get(i, at).Rotation) * model;
        return Quaternion.Normalize(model);
    }

    private static Quaternion RestModel(SkeletonDescription skeleton, int b)
    {
        var model = Quaternion.Identity;
        for (var at = b; at >= 0; at = skeleton.Bones[at].Parent)
            model = Quaternion.Normalize(skeleton.Bones[at].Reference.Rotation) * model;
        return Quaternion.Normalize(model);
    }

    /// <summary>A root reset on one pose at <paramref name="weight"/>, as <see cref="ApplyResetRoot"/> sets a sample; the body's facing needs the clip.</summary>
    internal static void ApplyResetRootAt(SkeletonDescription skeleton, MotionTransform[] pose, MotionLayer layer, float weight)
    {
        if (weight <= 0 || layer.Fix is not { Axes: not MotionAxes.None and var axes })
            return;
        foreach (var b in Roots(skeleton))
            if (Reset(pose[b].Rotation, skeleton.Bones[b].Reference.Rotation, axes, weight) is { } turned)
                pose[b] = pose[b] with { Rotation = turned };
    }

    /// <summary><paramref name="rotation"/> turned toward its <see cref="ResetTurn"/> by <paramref name="weight"/>; null when that changes nothing.</summary>
    private static Quaternion? Reset(Quaternion rotation, Quaternion rest, MotionAxes axes, float weight)
    {
        rotation = Quaternion.Normalize(rotation);
        var aim = MotionEvaluator.Align(ResetTurn(rotation, rest, axes), rotation);
        var off = Quaternion.Inverse(rotation) * aim;
        return new Vector3(off.X, off.Y, off.Z).LengthSquared() <= 1e-10f ? null : MotionEvaluator.Slerp(rotation, aim, weight);
    }

    private static int[] Roots(SkeletonDescription skeleton) =>
        [.. Enumerable.Range(0, skeleton.Bones.Length).Where(b => skeleton.Bones[b].Parent < 0)];

    /// <summary>
    /// <paramref name="rotation"/> with its turn from <paramref name="rest"/> taken out on <paramref name="axes"/>, by swing and
    /// twist, so no axis order matters: all three give the rest; two keep only the twist about the third (X and Z keep the facing);
    /// one takes out the twist about it (Y alone keeps the tilt).
    /// </summary>
    internal static Quaternion ResetTurn(Quaternion rotation, Quaternion rest, MotionAxes axes)
    {
        rest = Quaternion.Normalize(rest);
        var turn = Quaternion.Inverse(rest) * Quaternion.Normalize(rotation);
        var picked = BitOperations.PopCount((uint)(axes & MotionAxes.All));
        if (picked == 0)
            return Quaternion.Normalize(rotation);
        if (picked == 3)
            return rest;
        var twist = Twist(turn, Axis(picked == 1 ? axes : MotionAxes.All & ~axes));
        return Quaternion.Normalize(rest * (picked == 1 ? turn * Quaternion.Inverse(twist) : twist));
    }

    private static Vector3 Axis(MotionAxes axis) => axis switch
    {
        MotionAxes.X => Vector3.UnitX,
        MotionAxes.Y => Vector3.UnitY,
        _ => Vector3.UnitZ,
    };

    /// <summary>The part of <paramref name="q"/> that turns about <paramref name="axis"/>; none when q only swings the axis half round.</summary>
    private static Quaternion Twist(Quaternion q, Vector3 axis)
    {
        var along = Vector3.Dot(new Vector3(q.X, q.Y, q.Z), axis) * axis;
        var twist = new Quaternion(along, q.W);
        return twist.LengthSquared() < 1e-12f ? Quaternion.Identity : Quaternion.Normalize(twist);
    }

    /// <summary>Each side's finger targets on <paramref name="skeleton"/>, left first; a side without a usable hand gives none.</summary>
    private static IEnumerable<Dictionary<int, Quaternion>> Hands(SkeletonDescription skeleton, MotionHand hand)
    {
        MotionSide[] sides = hand.Side switch
        {
            MotionHandSide.Both => [MotionSide.Left, MotionSide.Right],
            MotionHandSide.Left => [MotionSide.Left],
            _ => [MotionSide.Right],
        };
        foreach (var side in sides)
            if (HandRig.For(skeleton, side) is { } rig)
                yield return HandShapes.Targets(skeleton, rig, hand, rig.HasTips && hand.Tips);
    }

    /// <summary>
    /// Keep finger motion's change per bone: the one that carries the bone's average over the layer's samples (as an
    /// amplitude's pivot) onto its target. A bone that holds still has none, so it takes its target exactly, as Replace does.
    /// </summary>
    private static Dictionary<int, Quaternion> Offsets(MotionClip clip, MotionLayer layer, Dictionary<int, Quaternion> targets, float end, CancellationToken token)
    {
        var offsets = new Dictionary<int, Quaternion>();
        int first = -1, last = -1;
        for (var i = 0; i < clip.Samples; i++)
            if (MotionEvaluator.Inside(layer, clip.Frame(i), end))
            {
                if (first < 0) first = i;
                last = i;
            }
        if (first < 0)
            return offsets;
        foreach (var (b, target) in targets)
        {
            token.ThrowIfCancellationRequested();
            var reference = Quaternion.Normalize(clip.Get(first, b).Rotation);
            var sum = Vector4.Zero;
            for (var i = first; i <= last; i++)
            {
                var q = MotionEvaluator.Align(Quaternion.Normalize(clip.Get(i, b).Rotation), reference);
                sum += new Vector4(q.X, q.Y, q.Z, q.W);
            }
            var pivot = sum.LengthSquared() < 1e-12f ? reference : Quaternion.Normalize(new Quaternion(sum.X, sum.Y, sum.Z, sum.W));
            var inverse = Quaternion.Inverse(pivot);
            var turns = false;
            for (var i = first; i <= last && !turns; i++)
            {
                var turn = inverse * MotionEvaluator.Align(Quaternion.Normalize(clip.Get(i, b).Rotation), pivot);
                turns = new Vector3(turn.X, turn.Y, turn.Z).LengthSquared() > 1e-10f;
            }
            if (turns)
                offsets[b] = Quaternion.Normalize(target * inverse);
        }
        return offsets;
    }

    /// <summary>Whether sample <paramref name="i"/> lies halfway between its neighbours in turn and place, as a sample between keys does.</summary>
    private static bool Halfway(Quaternion[] rotations, Vector3[] positions, int i)
    {
        var halfway = MotionEvaluator.Slerp(rotations[i - 1], rotations[i + 1], .5f);
        return MathF.Abs(Quaternion.Dot(Quaternion.Normalize(halfway), rotations[i])) >= HalfwayDot &&
               Vector3.DistanceSquared((positions[i - 1] + positions[i + 1]) / 2, positions[i]) <= HalfwayDistance * HalfwayDistance;
    }

    // How near halfway a sample between keys sits: within about a sixth of a degree (as near as a float dot tells), a hundredth of a millimetre.
    private const float HalfwayDot = .999999f, HalfwayDistance = 1e-5f;

    /// <summary>Sample <paramref name="at"/>'s index, round a loop as <see cref="Neighbour"/> wraps, else held at the clip's ends.</summary>
    private static int Near(int at, int samples, bool wraps)
    {
        if (wraps)
        {
            var period = samples - 1;
            return (at % period + period) % period;
        }
        return Math.Clamp(at, 0, samples - 1);
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
