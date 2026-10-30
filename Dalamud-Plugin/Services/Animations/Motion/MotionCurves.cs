using System.Collections.Immutable;
using System.Numerics;
using InstantEdit.Models;

namespace InstantEdit.Services.Animations.Motion;

/// <summary>What the curve view draws of a bone: its turn about X, Y and Z in degrees (<see cref="MotionAngles"/>), its move in centimetres.</summary>
internal enum CurveChannel : byte
{
    TurnX,
    TurnY,
    TurnZ,
    MoveX,
    MoveY,
    MoveZ,
}

/// <summary>
/// A bone's values at a run of frames, one array per <see cref="CurveChannel"/>: turns unwrapped, so they never jump at
/// ±180 degrees, moves in centimetres. A channel is null when nothing sets it (a pose layer's moves where no key sets the
/// position).
/// </summary>
internal sealed class CurveSamples
{
    private readonly float[] min = new float[MotionCurves.Channels], max = new float[MotionCurves.Channels];

    public CurveSamples(float[] frames, float[]?[] values)
    {
        Frames = frames;
        Values = values;
        for (var c = 0; c < MotionCurves.Channels; c++)
        {
            (min[c], max[c]) = (float.PositiveInfinity, float.NegativeInfinity);
            foreach (var value in values[c] ?? [])
                (min[c], max[c]) = (Math.Min(min[c], value), Math.Max(max[c], value));
        }
    }

    /// <summary>Each sample's timeline frame, ascending.</summary>
    public float[] Frames { get; }

    /// <summary>The values by channel, then sample.</summary>
    public float[]?[] Values { get; }

    /// <summary>The channel's lowest value; +infinity when it has none.</summary>
    public float Min(CurveChannel channel) => min[(int)channel];

    /// <summary>The channel's highest value; -infinity when it has none.</summary>
    public float Max(CurveChannel channel) => max[(int)channel];

    /// <summary>The turn at <paramref name="sample"/> as shown, in degrees.</summary>
    public Vector3 Turn(int sample) => new(Values[0]![sample], Values[1]![sample], Values[2]![sample]);

    /// <summary>The move at <paramref name="sample"/> in centimetres; zero when no move is shown.</summary>
    public Vector3 Move(int sample) => Values[3] is { } x ? new(x[sample], Values[4]![sample], Values[5]![sample]) : Vector3.Zero;
}

/// <summary>A key of a layer on its curve: its frame, the sample showing it, and whether it shows a move (a pose key only where it sets the position).</summary>
internal readonly record struct CurveKey(int Frame, int Sample, bool Moves);

/// <summary>A layer's own values for one bone at every frame, eased between its keys, and the keys on them.</summary>
internal sealed record KeyCurve(CurveSamples Samples, ImmutableArray<CurveKey> Keys);

/// <summary>
/// The values the curve view draws and writes: Euler turns unwrapped along a curve, a layer's keys sampled the way
/// <see cref="MotionEvaluator"/> plays them, one channel written back into a key's value, and the vertical scale.
/// </summary>
internal static class MotionCurves
{
    public const int Channels = 6;

    /// <summary>The most frames a layer's curve samples.</summary>
    private const int MaxFrames = 100_000;

    public static bool IsTurn(CurveChannel channel) => channel <= CurveChannel.TurnZ;

    /// <summary>The other Euler triple of the same rotation: about X a half turn more, Y mirrored about 90 degrees, Z a half turn more.</summary>
    public static Vector3 Twin(Vector3 degrees) => new(degrees.X + 180, 180 - degrees.Y, degrees.Z + 180);

    /// <summary>
    /// The triple turning as <paramref name="degrees"/> does that lies closest to <paramref name="previous"/>: whole turns
    /// added to or taken off each angle, from it or its <see cref="Twin"/>.
    /// </summary>
    public static Vector3 Nearest(Vector3 degrees, Vector3 previous)
    {
        var same = Near(degrees, previous);
        var twin = Near(Twin(degrees), previous);
        return Vector3.DistanceSquared(twin, previous) < Vector3.DistanceSquared(same, previous) ? twin : same;
    }

    private static Vector3 Near(Vector3 degrees, Vector3 previous) =>
        new(Near(degrees.X, previous.X), Near(degrees.Y, previous.Y), Near(degrees.Z, previous.Z));

    private static float Near(float degrees, float previous) => degrees - 360 * MathF.Round((degrees - previous) / 360);

    /// <summary>
    /// The curves of <paramref name="values"/> at <paramref name="frames"/>. Turns start at <paramref name="seed"/>'s
    /// representation when given, so a second curve of the same bone lines up with the first.
    /// </summary>
    public static CurveSamples Of(float[] frames, MotionTransform[] values, Vector3? seed = null) =>
        Of(frames, values, values, seed);

    /// <summary>The curves of a bone of <paramref name="clip"/> at each of its samples.</summary>
    public static CurveSamples OfClip(MotionClip clip, int bone, Vector3? seed = null)
    {
        var frames = new float[clip.Samples];
        var values = new MotionTransform[clip.Samples];
        for (var i = 0; i < clip.Samples; i++)
            (frames[i], values[i]) = (clip.Frame(i), clip.Get(i, bone));
        return Of(frames, values, seed);
    }

    /// <summary>Turns from <paramref name="turns"/>, moves from <paramref name="moves"/> (null for none).</summary>
    private static CurveSamples Of(float[] frames, MotionTransform[] turns, MotionTransform[]? moves, Vector3? seed)
    {
        var values = new float[]?[Channels];
        for (var c = 0; c < 3; c++)
            values[c] = new float[frames.Length];
        var previous = seed;
        for (var i = 0; i < frames.Length; i++)
        {
            var euler = MotionAngles.ToEuler(turns[i].Rotation);
            var shown = previous is { } before ? Nearest(euler, before) : euler;
            (values[0]![i], values[1]![i], values[2]![i]) = (shown.X, shown.Y, shown.Z);
            previous = shown;
        }
        if (moves != null)
        {
            for (var c = 3; c < Channels; c++)
                values[c] = new float[frames.Length];
            for (var i = 0; i < frames.Length; i++)
                (values[3]![i], values[4]![i], values[5]![i]) = (moves[i].Position.X * 100, moves[i].Position.Y * 100, moves[i].Position.Z * 100);
        }
        return new CurveSamples(frames, values);
    }

    /// <summary>
    /// <paramref name="layer"/>'s own values for <paramref name="bone"/> at every frame from <paramref name="from"/> to
    /// <paramref name="to"/> (widened to its keys), eased between the keys as <see cref="MotionEvaluator"/> plays them,
    /// wrapping round a <paramref name="loop"/> the layer covers: an offset key's change, a pose key's local transform,
    /// whose moves follow only the keys that set the position. Null for other kinds, and when no key names the bone.
    /// </summary>
    public static KeyCurve? OfKeys(MotionLayer layer, string bone, int from, int to, float loop = 0)
    {
        if (layer.Kind is not (MotionLayerKind.Offset or MotionLayerKind.Pose) || Track(layer, bone, false) is not { } turns)
            return null;
        var moves = layer.Kind == MotionLayerKind.Pose ? Track(layer, bone, true) : turns;
        var wrap = MotionEvaluator.Wraps(layer, loop) ? loop : 0;
        from = Math.Min(from, turns.Frames[0]);
        to = Math.Max(to, turns.Frames[^1]);
        var count = Math.Clamp(to - from + 1, 1, MaxFrames);
        var frames = new float[count];
        var turned = new MotionTransform[count];
        var moved = moves == null ? null : new MotionTransform[count];
        for (var i = 0; i < count; i++)
        {
            frames[i] = from + i;
            turned[i] = MotionEvaluator.Sample(turns.Frames, turns.Values, turns.Curves, frames[i], wrap);
            if (moved != null)
                moved[i] = MotionEvaluator.Sample(moves!.Frames, moves.Values, moves.Curves, frames[i], wrap);
        }
        var keys = ImmutableArray.CreateBuilder<CurveKey>();
        foreach (var key in layer.Keys)
            if (key.Bones.ContainsKey(bone) && key.Frame - from is var sample and >= 0 && sample < count)
                keys.Add(new CurveKey(key.Frame, sample, layer.Kind != MotionLayerKind.Pose || key.Moved.Contains(bone, StringComparer.Ordinal)));
        return new KeyCurve(Of(frames, turned, moved, null), keys.ToImmutable());
    }

    /// <summary>The bone's keys in the layer as the evaluator reads them: every key naming it, or with <paramref name="positions"/> those that move it.</summary>
    private static MotionEvaluator.Track? Track(MotionLayer layer, string bone, bool positions)
    {
        var frames = new List<int>();
        var values = new List<MotionTransform>();
        var curves = new List<MotionCurve?>();
        foreach (var key in layer.Keys)
        {
            if (!key.Bones.TryGetValue(bone, out var value) || positions && !key.Moved.Contains(bone, StringComparer.Ordinal))
                continue;
            frames.Add(key.Frame);
            var transform = MotionTransform.From(value);
            values.Add(transform with { Rotation = Quaternion.Normalize(transform.Rotation) });
            curves.Add(MotionEvaluator.CurveOf(layer, key));
        }
        return frames.Count == 0 ? null : new MotionEvaluator.Track([.. frames], [.. values], [.. curves]);
    }

    /// <summary>A channel of <paramref name="value"/>: the angle of <paramref name="shown"/> (its turn as the curve shows it), or the move in centimetres.</summary>
    public static float Value(MotionTransform value, Vector3 shown, CurveChannel channel) => channel switch
    {
        CurveChannel.TurnX => shown.X,
        CurveChannel.TurnY => shown.Y,
        CurveChannel.TurnZ => shown.Z,
        CurveChannel.MoveX => value.Position.X * 100,
        CurveChannel.MoveY => value.Position.Y * 100,
        _ => value.Position.Z * 100,
    };

    /// <summary>
    /// <paramref name="value"/> with one channel set to <paramref name="amount"/>: a turn made again from
    /// <paramref name="shown"/> (the angles the curve shows for it) with that angle changed, or a move in centimetres. The
    /// other channels and the scale stay.
    /// </summary>
    public static MotionTransform WithChannel(MotionTransform value, Vector3 shown, CurveChannel channel, float amount)
    {
        if (!float.IsFinite(amount))
            return value;
        return channel switch
        {
            CurveChannel.TurnX => value with { Rotation = MotionAngles.FromEuler(shown with { X = amount }) },
            CurveChannel.TurnY => value with { Rotation = MotionAngles.FromEuler(shown with { Y = amount }) },
            CurveChannel.TurnZ => value with { Rotation = MotionAngles.FromEuler(shown with { Z = amount }) },
            CurveChannel.MoveX => value with { Position = value.Position with { X = amount / 100 } },
            CurveChannel.MoveY => value with { Position = value.Position with { Y = amount / 100 } },
            _ => value with { Position = value.Position with { Z = amount / 100 } },
        };
    }

    /// <summary>
    /// The vertical scale for values from <paramref name="min"/> to <paramref name="max"/>: a tenth more each side, at least
    /// <paramref name="least"/> tall; round zero when there are none.
    /// </summary>
    public static (float Low, float High) Fit(float min, float max, float least)
    {
        if (!float.IsFinite(min) || !float.IsFinite(max) || max < min)
            (min, max) = (0, 0);
        var middle = (min + max) / 2;
        var half = Math.Max((max - min) / 2 * 1.2f, Math.Max(least, 1e-3f) / 2);
        return (middle - half, middle + half);
    }
}
