using System.Numerics;
using InstantEdit.Models;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Services.Animations.Motion;

/// <summary>
/// What a repeat layer plays again and again: the motion from timeline frame <paramref name="From"/> up to
/// <paramref name="To"/>, which isn't played (<paramref name="From"/> plays again there). The last <paramref name="Blend"/>
/// frames of each repeat cross-fade into the next one's start.
/// </summary>
internal sealed record MotionRepeat(int From, int To, int Blend = MotionRepeats.DefaultBlend);

/// <summary>
/// Repeat layers: their settings checked, and a stretch of the motion played over the layer's bones again and again, read
/// from the motion before the layer. Pure managed code: the bake and Live use it alike.
/// </summary>
internal static class MotionRepeats
{
    /// <summary>A new repeat's cross-fade, in frames.</summary>
    public const int DefaultBlend = 4;

    /// <summary>Throws <see cref="InvalidDataException"/> when a setting is out of range; <paramref name="name"/> names the layer.</summary>
    public static void Validate(MotionRepeat repeat, string name)
    {
        if (repeat.From < 0 || repeat.To <= repeat.From + 1 || repeat.To > TimelineDocument.MaxFrames ||
            repeat.Blend < 0 || repeat.Blend > (repeat.To - repeat.From) / 2)
            throw new InvalidDataException($"{name} has a repeat out of range.");
    }

    /// <summary>
    /// A repeat from <paramref name="from"/> to <paramref name="to"/> as a layer may hold it: on the timeline, its source two frames
    /// long or more and its blend within half of it.
    /// </summary>
    public static MotionRepeat Fit(int from, int to, int blend)
    {
        from = Math.Clamp(from, 0, TimelineDocument.MaxFrames - 2);
        to = Math.Clamp(to, from + 2, TimelineDocument.MaxFrames);
        return new MotionRepeat(from, to, Math.Clamp(blend, 0, (to - from) / 2));
    }

    /// <summary>
    /// <paramref name="layer"/> with its repeat's source moved as the timeline's frames moved: <paramref name="map"/> takes a frame and
    /// whether it is the source's end, and gives where it is now. The layer itself when it has no repeat or it stays as it was.
    /// </summary>
    public static MotionLayer Moved(MotionLayer layer, Func<int, bool, int> map)
    {
        if (layer.Repeat is not { } repeat)
            return layer;
        var moved = Fit(map(repeat.From, false), map(repeat.To, true), repeat.Blend);
        return moved == repeat ? layer : layer with { Repeat = moved };
    }

    /// <summary>How many frames one repeat lasts.</summary>
    public static int Length(MotionRepeat r) => r.To - r.From;

    /// <summary>How many times the layer plays its source, for the panel: 3.5 is three and a half repeats. 0 without a repeat.</summary>
    public static float Count(MotionLayer layer) =>
        layer.Repeat is { } repeat && Length(repeat) > 0 ? (float)(layer.End - layer.Start) / Length(repeat) : 0;

    /// <summary>
    /// Plays the source over <paramref name="bones"/> inside the layer: at frame f the bone takes the source at
    /// <c>From + p</c>, <c>p = (f - Start) mod (To - From)</c>, and in the last Blend frames of each repeat it goes on a
    /// smoothstep toward the source a repeat earlier (the motion leading into From, held at frame 0), so each wrap arrives on
    /// From without a jump. Every value is read from the motion before the layer, so the source may overlap the layer. A
    /// fractional source frame is read between its two samples; one past the clip's end holds its last sample. The layer's
    /// weight blends the motion toward it, on its ticked parts only. Bones that never move are left alone, so no track is
    /// made from rounding.
    /// </summary>
    internal static void Apply(MotionClip clip, MotionLayer layer, IReadOnlyCollection<int> bones, float end, CancellationToken token)
    {
        if (layer.Repeat is not { } repeat || Length(repeat) < 2)
            return;
        var rotate = layer.Components.HasFlag(PoseComponents.Rotation);
        var move = layer.Components.HasFlag(PoseComponents.Position);
        var scale = layer.Components.HasFlag(PoseComponents.Scale);
        // What each sample plays, and how far it goes there; samples the layer leaves alone are skipped.
        var weights = new float[clip.Samples];
        var any = false;
        for (var i = 0; i < clip.Samples; i++)
        {
            weights[i] = MotionEvaluator.Inside(layer, clip.Frame(i), end) ? MotionEvaluator.Weight(layer, clip.Frame(i), end) : 0;
            any |= weights[i] > 0;
        }
        if (!any)
            return;
        var source = clip.Clone();
        float length = Length(repeat);
        var blend = repeat.Blend;
        foreach (var b in bones)
        {
            token.ThrowIfCancellationRequested();
            if (!Moves(source, b))
                continue;
            for (var i = 0; i < clip.Samples; i++)
            {
                var weight = weights[i];
                if (weight <= 0)
                    continue;
                var p = (clip.Frame(i) - layer.Start) % length;
                if (p < 0)
                    p += length;
                // A frame a hair short of a wrap is the wrap: From plays again.
                if (p >= length - MotionEvaluator.FrameTolerance)
                    p = 0;
                var value = At(source, b, repeat.From + p);
                if (blend > 0 && p > length - blend)
                {
                    var t = AnimationPoseRules.SmoothStep((p - (length - blend)) / blend);
                    value = MotionEvaluator.Lerp(value, At(source, b, Math.Max(0, repeat.From + p - length)), t);
                }
                var under = clip.Get(i, b);
                if (rotate)
                    under = under with { Rotation = MotionEvaluator.Slerp(under.Rotation, value.Rotation, weight) };
                if (move)
                    under = under with { Position = Vector3.Lerp(under.Position, value.Position, weight) };
                if (scale)
                    under = under with { Scale = Vector3.Lerp(under.Scale, value.Scale, weight) };
                clip.Set(i, b, under);
            }
        }
    }

    /// <summary>A bone's local transform at timeline frame <paramref name="frame"/>, between the two samples around it; past the clip's ends, its first or last sample.</summary>
    private static MotionTransform At(MotionClip clip, int bone, float frame)
    {
        var at = clip.Duration <= 0 || !float.IsFinite(frame) ? 0 : Math.Clamp(frame / TimelineDocument.Fps / clip.Duration, 0, 1) * (clip.Samples - 1);
        var low = Math.Min((int)at, clip.Samples - 1);
        var high = Math.Min(low + 1, clip.Samples - 1);
        var t = at - low;
        return high == low || t <= 0 ? clip.Get(low, bone) : MotionEvaluator.Lerp(clip.Get(low, bone), clip.Get(high, bone), t);
    }

    /// <summary>Whether a bone turns, moves or scales at all over the clip.</summary>
    private static bool Moves(MotionClip clip, int bone)
    {
        var first = clip.Get(0, bone);
        var rotation = Quaternion.Normalize(first.Rotation);
        var inverse = Quaternion.Inverse(rotation);
        for (var i = 1; i < clip.Samples; i++)
        {
            var value = clip.Get(i, bone);
            var turn = inverse * MotionEvaluator.Align(Quaternion.Normalize(value.Rotation), rotation);
            if (new Vector3(turn.X, turn.Y, turn.Z).LengthSquared() > 1e-10f || Vector3.DistanceSquared(value.Position, first.Position) > 1e-12f ||
                Vector3.DistanceSquared(value.Scale, first.Scale) > 1e-12f)
                return true;
        }
        return false;
    }
}
