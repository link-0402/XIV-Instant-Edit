using System.Collections.Immutable;
using System.Numerics;
using InstantEdit.Models;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Services.Animations.Motion;

/// <summary>
/// How a balance layer keeps the body's weight over the feet: along the character's X (<paramref name="Sideways"/>) and Z
/// (<paramref name="FrontBack"/>), the hips' move averaged over <paramref name="Smooth"/> frames, a foot counting as down as
/// readily as a planted pin at <paramref name="Sensitivity"/>, and the hips moving at most <paramref name="MaxShift"/> metres.
/// </summary>
internal sealed record MotionBalance(bool Sideways = true, bool FrontBack = true, int Smooth = 6, PinSensitivity Sensitivity = PinSensitivity.Normal,
    float MaxShift = 0.15f);

/// <summary>
/// Balance layers: their settings checked, the body's centre of mass, the feet the motion plants, and the hips moved over the
/// clip so the centre stays over them, the planted feet held. Pure managed code: the bake and Live use it alike.
/// </summary>
internal static class MotionBalances
{
    /// <summary>The widest smoothing (frames), and the least and most the hips may move (metres).</summary>
    public const int MaxSmooth = 30;
    public const float MinShift = 0.01f, MaxShiftLimit = 0.5f;

    /// <summary>
    /// The body's segments by bone name: the bone, the bone its segment ends at (null: the bone's own position) and its share of
    /// the body's mass, after Dempster. A segment's centre is halfway along it.
    /// </summary>
    private static readonly (string Bone, string? End, float Share)[] Segments =
    [
        (PinChains.Head, null, .081f),
        ("j_sebo_c", "j_kubi", .150f),
        ("j_sebo_b", "j_sebo_c", .140f),
        (HipSolver.BackBase, "j_sebo_b", .130f),
        (PinChains.Hips, null, .142f),
        ("j_ude_a_l", "j_ude_b_l", .028f), ("j_ude_a_r", "j_ude_b_r", .028f),
        ("j_ude_b_l", "j_te_l", .016f), ("j_ude_b_r", "j_te_r", .016f),
        ("j_te_l", null, .006f), ("j_te_r", null, .006f),
        ("j_asi_a_l", "j_asi_c_l", .100f), ("j_asi_a_r", "j_asi_c_r", .100f),
        ("j_asi_c_l", PinChains.FootL, .0465f), ("j_asi_c_r", PinChains.FootR, .0465f),
        (PinChains.FootL, null, .0145f), (PinChains.FootR, null, .0145f),
    ];

    /// <summary>The bones a balance layer changes: the hips, the back they carry, and each leg's thigh, knee and foot.</summary>
    public static readonly ImmutableArray<string> Moved =
        [PinChains.Hips, HipSolver.BackBase, "j_asi_a_l", "j_asi_b_l", PinChains.FootL, "j_asi_a_r", "j_asi_b_r", PinChains.FootR];

    /// <summary>Throws <see cref="InvalidDataException"/> when a setting is out of range; <paramref name="name"/> names the layer.</summary>
    public static void Validate(MotionBalance balance, string name)
    {
        if (balance.Smooth is < 0 or > MaxSmooth || !float.IsFinite(balance.MaxShift) || balance.MaxShift < MinShift || balance.MaxShift > MaxShiftLimit ||
            !Enum.IsDefined(balance.Sensitivity))
            throw new InvalidDataException($"{name} has a balance out of range.");
    }

    /// <summary>Whether a skeleton has what a balance works on: the hips and both feet.</summary>
    public static bool HasBones(IReadOnlyDictionary<string, int> index) =>
        index.ContainsKey(PinChains.Hips) && index.ContainsKey(PinChains.FootL) && index.ContainsKey(PinChains.FootR);

    /// <summary>
    /// The body's centre of mass in model space: the segments' centres weighed by their shares. A segment whose bones the skeleton
    /// lacks is left out and the rest share its weight. Null without any of them.
    /// </summary>
    public static Vector3? Centre(SkeletonDescription skeleton, ReadOnlySpan<MotionTransform> model, Dictionary<string, int> index)
    {
        var sum = Vector3.Zero;
        var total = 0f;
        foreach (var (bone, endBone, share) in Segments)
        {
            if (!index.TryGetValue(bone, out var b) || b >= model.Length)
                continue;
            var at = model[b].Position;
            if (endBone != null)
            {
                if (!index.TryGetValue(endBone, out var e) || e >= model.Length)
                    continue;
                at = (at + model[e].Position) / 2;
            }
            sum += at * share;
            total += share;
        }
        return total > 0 ? sum / total : null;
    }

    /// <summary>
    /// Where each foot is down over the samples inside the layer, in the motion as <paramref name="clip"/> has it, as a planted pin
    /// at the balance's sensitivity finds it. Null without a balance, without the hips or a foot, or with no sample in the layer.
    /// </summary>
    public static (PlantedPath Left, PlantedPath Right)? Support(MotionClip clip, MotionLayer layer, Dictionary<string, int> index)
    {
        if (layer.Balance is not { } balance || !HasBones(index) || clip.Samples < 2)
            return null;
        var end = clip.Frame(clip.Samples - 1);
        if (Span(clip, layer, end) is not ({ } span))
            return null;
        var models = Models(clip, CancellationToken.None);
        return Planted(clip, layer, balance, models, index[PinChains.FootL], index[PinChains.FootR], span.First, span.Last, end);
    }

    /// <summary>
    /// Moves the hips over the samples inside the layer so the body's centre of mass stays over the feet the motion under it
    /// plants: over both soles' middle with both down, over one sole with one down, and nowhere in a jump, the move easing in and
    /// out round it. Each sample's move, along the floor and on the ticked axes only, is found in two passes, each solving the
    /// body with <see cref="HipSolver"/>, the planted feet held and a lifted one riding with the hips, and capped at the balance's
    /// Max shift; the moves are then averaged on a tent over Smooth frames (narrowing at the layer's edges, round the seam on a
    /// loop the layer covers), scaled by the layer's weight, and the body solved once more at them. Every value is read from the
    /// motion before the layer. A skeleton without the hips or a foot is left alone. With <paramref name="hipsOnHara"/>
    /// <see cref="MotionEvaluator.Apply"/> moves the hips' move onto <c>n_hara</c> after this layer, which carries the back too, so the
    /// back keeps its own position.
    /// </summary>
    internal static void Apply(MotionClip clip, MotionLayer layer, Dictionary<string, int> index, float loop, float end, bool hipsOnHara, CancellationToken token)
    {
        if (layer.Balance is not { } balance || clip.Samples < 2 || !HasBones(index) || Span(clip, layer, end) is not ({ } span))
            return;
        var (first, last) = span;
        var weights = new float[clip.Samples];
        var any = false;
        for (var i = first; i <= last; i++)
        {
            weights[i] = MotionEvaluator.Weight(layer, clip.Frame(i), end);
            any |= weights[i] > 0;
        }
        if (!any)
            return;

        var skeleton = clip.Skeleton;
        int hips = index[PinChains.Hips], left = index[PinChains.FootL], right = index[PinChains.FootR];
        var locals = new MotionTransform[clip.Samples][];
        for (var i = 0; i < clip.Samples; i++)
            locals[i] = Local(clip, i);
        var models = Models(clip, token);
        var (leftPath, rightPath) = Planted(clip, layer, balance, models, left, right, first, last, end);
        // A layer narrower than the clip eases its move in and out at its edges (the paths' cuts); a foot down there still holds whole.
        PlantedPath leftHeld = leftPath with { CutFirst = -1, CutLast = -1 }, rightHeld = rightPath with { CutFirst = -1, CutLast = -1 };
        FootSole leftSole = PinPlanting.Sole(skeleton, left)!.Value, rightSole = PinPlanting.Sole(skeleton, right)!.Value;
        var anchors = HipSolver.Anchors(skeleton, [PinChains.FootL, PinChains.FootR]);

        // Each sample's move toward its support, before smoothing; none where no foot is down.
        var moves = new Vector3[clip.Samples];
        for (var i = first; i <= last; i++)
        {
            if ((i & 15) == 0)
                token.ThrowIfCancellationRequested();
            float holdLeft = leftHeld.Hold(i), holdRight = rightHeld.Hold(i);
            var support = Math.Max(leftPath.Hold(i), rightPath.Hold(i));
            if (support <= 0)
                continue;
            var target = (holdLeft * SoleAt(models[i], left, leftSole) + holdRight * SoleAt(models[i], right, rightSole)) / (holdLeft + holdRight);
            var move = Vector3.Zero;
            for (var pass = 0; pass < 2; pass++)
            {
                var model = pass == 0 ? models[i] : Solved(skeleton, locals[i], models[i], hips, move, anchors, left, right, holdLeft, holdRight).Model;
                if (Centre(skeleton, model, index) is not { } centre)
                    break;
                move += Along(target - centre, balance);
            }
            if (move.Length() > balance.MaxShift)
                move *= balance.MaxShift / move.Length();
            moves[i] = move * support;
        }

        var step = clip.Frame(1) - clip.Frame(0);
        var reach = step > 0 ? (int)MathF.Floor(balance.Smooth / 2f / step + 1e-4f) : 0;
        var smoothed = Smoothed(moves, first, last, reach, MotionEvaluator.Wraps(layer, loop));
        var changed = new SortedSet<int>(HipSolver.Turned(anchors.AsSpan())) { hips };
        // On n_hara the hips' move carries the back once more after this layer, so the back keeps its own position.
        if (HipSolver.Carried(skeleton) is var carried and >= 0 && !hipsOnHara)
            changed.Add(carried);
        for (var i = first; i <= last; i++)
        {
            if ((i & 15) == 0)
                token.ThrowIfCancellationRequested();
            var move = smoothed[i] * weights[i];
            if (move.LengthSquared() < 1e-12f)
                continue;
            var local = Solved(skeleton, locals[i], models[i], hips, move, anchors, left, right, leftHeld.Hold(i), rightHeld.Hold(i)).Local;
            foreach (var b in changed)
                if (local[b] != locals[i][b])
                    clip.Set(i, b, local[b]);
        }
    }

    /// <summary>The first and last samples inside the layer; null for none.</summary>
    private static (int First, int Last)? Span(MotionClip clip, MotionLayer layer, float end)
    {
        int first = -1, last = -1;
        for (var i = 0; i < clip.Samples; i++)
            if (MotionEvaluator.Inside(layer, clip.Frame(i), end))
            {
                if (first < 0) first = i;
                last = i;
            }
        return first < 0 ? null : (first, last);
    }

    private static MotionTransform[] Local(MotionClip clip, int sample)
    {
        var local = new MotionTransform[clip.Bones];
        for (var b = 0; b < local.Length; b++)
            local[b] = clip.Get(sample, b);
        return local;
    }

    /// <summary>Every sample's pose in model space.</summary>
    private static MotionTransform[][] Models(MotionClip clip, CancellationToken token)
    {
        var models = new MotionTransform[clip.Samples][];
        for (var i = 0; i < clip.Samples; i++)
        {
            if ((i & 63) == 0)
                token.ThrowIfCancellationRequested();
            models[i] = MotionEvaluator.ModelSpace(clip.Skeleton, Local(clip, i));
        }
        return models;
    }

    /// <summary>Where each foot is down, read from its model-space path as a planted pin reads it.</summary>
    private static (PlantedPath Left, PlantedPath Right) Planted(MotionClip clip, MotionLayer layer, MotionBalance balance, MotionTransform[][] models,
        int left, int right, int first, int last, float end)
    {
        var rate = clip.Duration > 0 ? (clip.Samples - 1) / clip.Duration : TimelineDocument.Fps;
        var gap = PinPlanting.SamplesOf(PinPlanting.CloseGapFrames, clip);
        var shortest = PinPlanting.SamplesOf(PinPlanting.MinStretchFrames, clip);
        var ease = PinPlanting.SamplesOf(PinPlanting.EaseFrames, clip);
        var wraps = MotionEvaluator.Wraps(layer, clip.Loops ? end : 0);
        PlantedPath Find(int foot)
        {
            var path = new MotionTransform[clip.Samples];
            var heights = new float[clip.Samples];
            for (var i = 0; i < clip.Samples; i++)
            {
                path[i] = models[i][foot];
                heights[i] = models[i][foot].Position.Y;
            }
            return PinPlanting.Find(path, rate, first, last, wraps, balance.Sensitivity, gap, shortest, ease, heights);
        }
        return (Find(left), Find(right));
    }

    /// <summary>The middle of a foot's sole, between its heel and ball, in model space.</summary>
    private static Vector3 SoleAt(MotionTransform[] model, int foot, FootSole sole) =>
        model[foot].Position + Vector3.Transform((sole.Heel + sole.Ball) / 2, Quaternion.Normalize(model[foot].Rotation));

    /// <summary><paramref name="away"/> along the floor, on the ticked axes only.</summary>
    private static Vector3 Along(Vector3 away, MotionBalance balance) =>
        new(balance.Sideways ? away.X : 0, 0, balance.FrontBack ? away.Z : 0);

    /// <summary>
    /// The pose with the hips moved by <paramref name="move"/>, the feet held as far as they are down: a foot that isn't rides
    /// with the hips, one easing in or out of a hold goes part of the way.
    /// </summary>
    private static (MotionTransform[] Local, MotionTransform[] Model) Solved(SkeletonDescription skeleton, MotionTransform[] local, MotionTransform[] model,
        int hips, Vector3 move, ImmutableArray<HipAnchor> anchors, int left, int right, float holdLeft, float holdRight)
    {
        var solvedLocal = (MotionTransform[])local.Clone();
        var solvedModel = (MotionTransform[])model.Clone();
        var start = (MotionTransform[])model.Clone();
        start[left] = start[left] with { Position = model[left].Position + (1 - holdLeft) * move };
        start[right] = start[right] with { Position = model[right].Position + (1 - holdRight) * move };
        HipSolver.Solve(skeleton, solvedLocal, solvedModel, hips, model[hips].Position + move, anchors.AsSpan(), start);
        return (solvedLocal, solvedModel);
    }

    /// <summary>
    /// <paramref name="moves"/> averaged on a tent <paramref name="reach"/> samples each side, from running sums as Smooth motion
    /// averages; off a loop the tent narrows to what lies inside samples <paramref name="first"/> to <paramref name="last"/> on both
    /// sides, round a loop it reads over the seam by <c>Samples - 1</c> (the last sample is the first one's moment).
    /// </summary>
    private static Vector3[] Smoothed(Vector3[] moves, int first, int last, int reach, bool wraps)
    {
        var smoothed = (Vector3[])moves.Clone();
        if (reach <= 0)
            return smoothed;
        var samples = moves.Length;
        var period = samples - 1;
        var pad = wraps ? reach : 0;
        var count = samples + 2 * pad;
        // Running sums of X and Z, and of those sums: a tent's sum is three of the second.
        var once = new double[(count + 1) * 2];
        var twice = new double[(count + 2) * 2];
        for (var e = 0; e < count; e++)
        {
            var value = moves[wraps ? ((e - pad) % period + period) % period : e];
            once[(e + 1) * 2] = once[e * 2] + value.X;
            once[(e + 1) * 2 + 1] = once[e * 2 + 1] + value.Z;
            for (var c = 0; c < 2; c++)
                twice[(e + 1) * 2 + c] = twice[e * 2 + c] + once[e * 2 + c];
        }
        for (var c = 0; c < 2; c++)
            twice[(count + 1) * 2 + c] = twice[count * 2 + c] + once[count * 2 + c];
        for (var i = first; i <= last; i++)
        {
            var r = wraps ? reach : Math.Min(reach, Math.Min(i - first, last - i));
            if (r == 0)
                continue;
            // The tent of half-width r round e0 weighs sample e0 + k by r + 1 - |k|, (r + 1)² in all.
            var e0 = i + pad;
            double total = (r + 1) * (r + 1);
            double Sum(int c) => (twice[(e0 + r + 2) * 2 + c] - 2 * twice[(e0 + 1) * 2 + c] + twice[(e0 - r) * 2 + c]) / total;
            smoothed[i] = new Vector3((float)Sum(0), 0, (float)Sum(1));
        }
        return smoothed;
    }

    /// <summary>
    /// The panel's facts on each foot, "Left held 0-40, 72-119", on <paramref name="clip"/> with <paramref name="before"/> (the
    /// layers before the balance) baked over it, link layers following <paramref name="links"/>; why there are none when a layer
    /// can't be baked. Reads the clip only; for a worker.
    /// </summary>
    public static IReadOnlyList<string> Facts(MotionClip clip, IReadOnlyList<MotionLayer> before, MotionLayer layer, MotionLinkSet? links, CancellationToken token)
    {
        try
        {
            var baked = new MotionClip(clip.Skeleton, clip.Duration, clip.Samples) { Loops = clip.Loops, SourceTracks = clip.SourceTracks, HipsOnHara = clip.HipsOnHara };
            baked.CopyFrom(clip);
            MotionEvaluator.Apply(baked, before, token, links);
            if (Support(baked, layer, MotionEvaluator.Index(baked.Skeleton)) is not { } support)
                return [];
            static string Lower(string text) => text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];
            return ["Left " + Lower(PinPlanting.Facts(support.Left, baked)), "Right " + Lower(PinPlanting.Facts(support.Right, baked))];
        }
        catch (InvalidDataException e)
        {
            return [e.Message];
        }
    }
}
