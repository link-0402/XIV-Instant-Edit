using System.Numerics;
using InstantEdit.Models;

namespace InstantEdit.Services.Animations.Motion;

/// <summary>What the Bones window's breakdown row does to the selected bones at the playhead. Never saved.</summary>
internal enum BreakdownMode : byte
{
    /// <summary>Puts the bones between their keys before and after, by the slider.</summary>
    Tween,
    /// <summary>Exaggerates or softens the key at the playhead against what the keys either side give there.</summary>
    Push,
    /// <summary>Takes the key at the playhead toward what the keys either side give there.</summary>
    Relax,
}

/// <summary>
/// A bone's nearest keys in a layer strictly before (<paramref name="From"/>) and after (<paramref name="To"/>) a frame:
/// their values, whether each sets the bone's position (a pose key's Moved) and the earlier key's curve, null for a hold.
/// </summary>
internal readonly record struct BreakdownSpan(int From, MotionTransform Before, bool BeforeMoved, int To, MotionTransform After, bool AfterMoved,
    MotionCurve? Curve);

/// <summary>
/// Breakdowns as Blender makes them, from a layer's own key values (an offset's change, a pose key's local transform),
/// never the final motion. Tween goes from the key before to the key after by its amount, plainly; Push / pull scales
/// the key at the playhead's difference from what the keys either side give there; Relax takes it toward that.
/// Rotations go the short way, positions and scales on a line.
/// </summary>
internal static class MotionBreakdown
{
    /// <summary>The slider's range and starting value for <paramref name="mode"/>, as fractions (1 is 100%).</summary>
    public static (float Min, float Max, float Default) Range(BreakdownMode mode) => mode switch
    {
        BreakdownMode.Push => (0, 2, 1.2f),
        BreakdownMode.Relax => (0, 1, .5f),
        _ => (-.5f, 1.5f, .5f),
    };

    /// <summary><paramref name="bone"/>'s keys either side of <paramref name="frame"/> in <paramref name="layer"/>; null when it misses one. No loop wrap.</summary>
    public static BreakdownSpan? Span(MotionLayer layer, string bone, int frame)
    {
        MotionKey? before = null, after = null;
        foreach (var key in layer.Keys)
        {
            if (!key.Bones.ContainsKey(bone))
                continue;
            if (key.Frame < frame)
                before = key;
            else if (key.Frame > frame)
            {
                after = key;
                break;
            }
        }
        if (before == null || after == null)
            return null;
        return new(before.Frame, Value(before, bone), before.Moved.Contains(bone), after.Frame, Value(after, bone), after.Moved.Contains(bone),
            MotionEvaluator.CurveOf(layer, before));
    }

    /// <summary>What the layer's own easing between the span's keys gives at <paramref name="frame"/>.</summary>
    public static MotionTransform Inter(BreakdownSpan span, float frame) =>
        MotionEvaluator.Sample([span.From, span.To], [span.Before, span.After], [span.Curve], frame);

    /// <summary>
    /// The bone's new value: <paramref name="current"/> (its key at the playhead, else <paramref name="inter"/>) with the
    /// <paramref name="parts"/> broken down by <paramref name="mode"/> at <paramref name="amount"/>; the other parts stay
    /// as they are. Scales stay within what a key may hold.
    /// </summary>
    public static MotionTransform Blend(BreakdownMode mode, float amount, BreakdownSpan span, MotionTransform inter, MotionTransform current,
        PoseComponents parts)
    {
        var target = mode == BreakdownMode.Tween
            ? MotionEvaluator.Lerp(span.Before, span.After, amount)
            : Push(inter, current, mode == BreakdownMode.Push ? amount : 1 - amount);
        return new(
            parts.HasFlag(PoseComponents.Position) ? target.Position : current.Position,
            parts.HasFlag(PoseComponents.Rotation) ? Quaternion.Normalize(target.Rotation) : current.Rotation,
            parts.HasFlag(PoseComponents.Scale)
                ? Vector3.Clamp(target.Scale, new Vector3(MotionEvaluator.MinScale), new Vector3(MotionEvaluator.MaxScale))
                : current.Scale);
    }

    /// <summary><paramref name="current"/>'s difference from <paramref name="inter"/> times <paramref name="factor"/>: 1 is itself, 0 <paramref name="inter"/>.</summary>
    private static MotionTransform Push(MotionTransform inter, MotionTransform current, float factor)
    {
        if (factor == 1)
            return current;
        var turn = Quaternion.Normalize(current.Rotation * Quaternion.Inverse(inter.Rotation));
        return new(inter.Position + (current.Position - inter.Position) * factor,
            Quaternion.Normalize(MotionEvaluator.Pow(turn, factor) * inter.Rotation),
            inter.Scale + (current.Scale - inter.Scale) * factor);
    }

    private static MotionTransform Value(MotionKey key, string bone)
    {
        var value = MotionTransform.From(key.Bones[bone]);
        return value with { Rotation = Quaternion.Normalize(value.Rotation) };
    }
}
