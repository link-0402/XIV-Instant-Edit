using System.Numerics;
using InstantEdit.Models;

namespace InstantEdit.Services.Animations.Motion;

/// <summary>
/// Smooth motion layers: each frame of the layer's bones goes toward a bell-weighted average of the frames round it,
/// so sudden moves spread out and steady ones stay. Pure managed code: the bake and Live use it alike.
/// </summary>
internal static class MotionSmoothing
{
    /// <summary>The shortest and longest window, and a new layer's, in timeline frames.</summary>
    public const int MinWindow = 3, MaxWindow = 60, DefaultWindow = 10;

    // A rotation's four parts and a position's three, summed per sample.
    private const int Parts = 7;

    /// <summary>
    /// Smooths <paramref name="bones"/> over the layer's span: each sample goes, by the layer's weight there, toward the
    /// average of the samples within half a window each side, weighted on a tent (highest at the sample, none past half a
    /// window), so the motion isn't delayed. The tent's sums come from running sums, so a wide window costs no more than a
    /// narrow one. Turns are summed on one hemisphere and normalized, positions as they are. Every value is read from the
    /// motion before the layer. Near the layer's ends the window narrows to what lies inside it on both sides, so it adds no
    /// jump at its edges and a clip that plays once keeps its first and last pose; on a loop the layer covers it reads round
    /// the seam, by <c>Samples - 1</c> (the last sample is the first one's moment). Bones that never move inside the layer are
    /// left alone, so no track is made from rounding.
    /// </summary>
    internal static void Apply(MotionClip clip, MotionLayer layer, IReadOnlyCollection<int> bones, float loop, float end, CancellationToken token)
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
        if (first < 0 || clip.Samples < 2)
            return;
        var step = clip.Frame(1) - clip.Frame(0);
        if (!(step > 0))
            return;
        var reach = (int)MathF.Floor(layer.Window / 2f / step + 1e-4f);
        var wraps = MotionEvaluator.Wraps(layer, loop);
        var period = clip.Samples - 1;
        // Round a loop the samples carry on past both ends of the clip, so every window there is whole.
        var pad = wraps ? reach : 0;
        var count = clip.Samples + 2 * pad;
        var rotations = new Quaternion[clip.Samples];
        var positions = new Vector3[clip.Samples];
        // Running sums of each part and of those sums, in doubles: a tent's sum is three of the second.
        var once = new double[(count + 1) * Parts];
        var twice = new double[(count + 2) * Parts];
        Span<double> sum = stackalloc double[Parts];
        foreach (var b in bones)
        {
            token.ThrowIfCancellationRequested();
            for (var i = 0; i < clip.Samples; i++)
            {
                var value = clip.Get(i, b);
                rotations[i] = Quaternion.Normalize(value.Rotation);
                positions[i] = value.Position;
            }
            if (!Moves(rotations, positions, first, last, rotate, move, out var turns, out var moves))
                continue;
            // Each rotation on its neighbour's hemisphere, so summing them averages them.
            var previous = rotations[Sample(0, pad, period, wraps)];
            for (var e = 0; e < count; e++)
            {
                var s = Sample(e, pad, period, wraps);
                var q = MotionEvaluator.Align(rotations[s], previous);
                previous = q;
                var p = positions[s];
                ReadOnlySpan<double> parts = [q.X, q.Y, q.Z, q.W, p.X, p.Y, p.Z];
                for (var c = 0; c < Parts; c++)
                {
                    once[(e + 1) * Parts + c] = once[e * Parts + c] + parts[c];
                    twice[(e + 1) * Parts + c] = twice[e * Parts + c] + once[e * Parts + c];
                }
            }
            for (var c = 0; c < Parts; c++)
                twice[(count + 1) * Parts + c] = twice[count * Parts + c] + once[count * Parts + c];
            for (var i = first; i <= last; i++)
            {
                var weight = MotionEvaluator.Weight(layer, clip.Frame(i), end);
                // Off a loop the window takes as many samples each side, so it narrows toward the layer's ends: the samples
                // just inside an edge stay close to the unsmoothed ones just outside it, and a clip keeps its first and last pose.
                var r = wraps ? reach : Math.Min(reach, Math.Min(i - first, last - i));
                if (weight <= 0 || r == 0)
                    continue;
                // The tent of half-width r round e0 weighs sample e0 + k by r + 1 - |k|, (r + 1)² in all.
                var e0 = i + pad;
                double total = (r + 1) * (r + 1);
                for (var c = 0; c < Parts; c++)
                    sum[c] = (twice[(e0 + r + 2) * Parts + c] - 2 * twice[(e0 + 1) * Parts + c] + twice[(e0 - r) * Parts + c]) / total;
                var value = clip.Get(i, b);
                if (turns)
                {
                    var average = Quaternion.Normalize(new Quaternion((float)sum[0], (float)sum[1], (float)sum[2], (float)sum[3]));
                    value = value with { Rotation = MotionEvaluator.Slerp(rotations[i], average, weight) };
                }
                if (moves)
                    value = value with { Position = Vector3.Lerp(positions[i], new Vector3((float)sum[4], (float)sum[5], (float)sum[6]), weight) };
                clip.Set(i, b, value);
            }
        }
    }

    /// <summary>The clip sample at place <paramref name="e"/> of the padded run: round a loop it wraps by <paramref name="period"/>.</summary>
    private static int Sample(int e, int pad, int period, bool wraps) => wraps ? ((e - pad) % period + period) % period : e;

    /// <summary>Whether a bone turns or moves at all over samples <paramref name="first"/> to <paramref name="last"/>, as the parts it may change.</summary>
    private static bool Moves(Quaternion[] rotations, Vector3[] positions, int first, int last, bool rotate, bool move, out bool turns, out bool moves)
    {
        var inverse = Quaternion.Inverse(rotations[first]);
        turns = moves = false;
        for (var i = first + 1; i <= last && !(turns && moves); i++)
        {
            var turn = inverse * MotionEvaluator.Align(rotations[i], rotations[first]);
            turns |= rotate && new Vector3(turn.X, turn.Y, turn.Z).LengthSquared() > 1e-10f;
            moves |= move && Vector3.DistanceSquared(positions[i], positions[first]) > 1e-12f;
        }
        return turns || moves;
    }
}
