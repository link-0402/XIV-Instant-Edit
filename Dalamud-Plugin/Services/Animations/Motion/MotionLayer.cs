using System.Collections.Immutable;
using InstantEdit.Models;

namespace InstantEdit.Services.Animations.Motion;

/// <summary>What a bone layer does to the motion under it.</summary>
internal enum MotionLayerKind : byte
{
    /// <summary>Turns, moves or scales bones by the amounts its keys hold.</summary>
    Offset,
    /// <summary>Makes bones move more or less around their average pose over the layer.</summary>
    Amplitude,
    /// <summary>Blends bones toward the poses its keys hold.</summary>
    Pose,
}

/// <summary>
/// How a bone's value goes from one key to the next. Saved as numbers, so new ways only ever go at
/// the end. <see cref="Custom"/> is a key's own curve and never a layer's.
/// </summary>
internal enum MotionInterpolation : byte
{
    Linear,
    /// <summary>Slow at both keys.</summary>
    Smooth,
    /// <summary>Holds the value until the next key.</summary>
    Step,
    /// <summary>Slow at the first key, full speed into the next.</summary>
    EaseIn,
    /// <summary>Full speed from the first key, slow into the next.</summary>
    EaseOut,
    /// <summary>The key's own <see cref="MotionCurve"/>.</summary>
    Custom,
}

/// <summary>
/// An easing curve from (0, 0) to (1, 1) with two handles, as CSS's cubic-bezier: X is the time
/// between the keys, Y how far the value has come. Handle X stays within 0 to 1 so the curve never
/// runs back in time; Y may leave it to overshoot or wind up.
/// </summary>
internal sealed record MotionCurve(float X1, float Y1, float X2, float Y2)
{
    public const float MinY = -1, MaxY = 2;

    public static readonly MotionCurve Linear = new(1 / 3f, 1 / 3f, 2 / 3f, 2 / 3f);
    /// <summary>Exactly the smoothstep <see cref="MotionInterpolation.Smooth"/> has always used.</summary>
    public static readonly MotionCurve Smooth = new(1 / 3f, 0, 2 / 3f, 1);
    public static readonly MotionCurve EaseIn = new(1 / 3f, 0, 2 / 3f, 2 / 3f);
    public static readonly MotionCurve EaseOut = new(1 / 3f, 1 / 3f, 2 / 3f, 1);

    public bool Valid => float.IsFinite(X1) && float.IsFinite(Y1) && float.IsFinite(X2) && float.IsFinite(Y2) &&
                         X1 is >= 0 and <= 1 && X2 is >= 0 and <= 1 && Y1 is >= MinY and <= MaxY && Y2 is >= MinY and <= MaxY;

    /// <summary>The curve with its handles moved back into range.</summary>
    public MotionCurve Clamped() => new(Clamp(X1, 0, 1), Clamp(Y1, MinY, MaxY), Clamp(X2, 0, 1), Clamp(Y2, MinY, MaxY));

    /// <summary>How far the value has come at <paramref name="t"/> of the way between the keys.</summary>
    public float Ease(float t)
    {
        if (!(t > 0))
            return 0;
        if (t >= 1)
            return 1;
        return Bezier(SolveX(t), Y1, Y2);
    }

    private float SolveX(float x)
    {
        // Handles a third and two thirds along make X the time itself, as every preset does.
        if (MathF.Abs(X1 - 1 / 3f) < 1e-6f && MathF.Abs(X2 - 2 / 3f) < 1e-6f)
            return x;
        var s = x;
        for (var i = 0; i < 8; i++)
        {
            var error = Bezier(s, X1, X2) - x;
            if (MathF.Abs(error) < 1e-6f)
                return s;
            var slope = Slope(s, X1, X2);
            if (MathF.Abs(slope) < 1e-6f)
                break;
            s = Math.Clamp(s - error / slope, 0, 1);
        }
        // Flat stretches stall Newton; the curve's X only ever rises, so halving always finds it.
        float low = 0, high = 1;
        for (var i = 0; i < 40; i++)
        {
            s = (low + high) / 2;
            if (Bezier(s, X1, X2) < x) low = s;
            else high = s;
        }
        return (low + high) / 2;
    }

    private static float Bezier(float s, float p1, float p2)
    {
        var u = 1 - s;
        return 3 * u * u * s * p1 + 3 * u * s * s * p2 + s * s * s;
    }

    private static float Slope(float s, float p1, float p2)
    {
        var u = 1 - s;
        return 3 * u * u * p1 + 6 * u * s * (p2 - p1) + 3 * s * s * (1 - p2);
    }

    private static float Clamp(float value, float min, float max) => float.IsFinite(value) ? Math.Clamp(value, min, max) : min;
}

/// <summary>
/// A layer's values at one timeline frame, by bone name. An offset key holds changes: a rotation
/// turned onto the bone's own (so its children follow), a move in character space and a scale
/// factor. A pose key holds whole local transforms; <paramref name="Moved"/> names the bones whose
/// position it sets too, every other bone only turns to it. <paramref name="Ease"/> is how every
/// bone goes from this key to its next one, null for the layer's way; <paramref name="Curve"/> is
/// the curve <see cref="MotionInterpolation.Custom"/> uses. Journals written before keys had their
/// own ease load with none.
/// </summary>
internal sealed record MotionKey(int Frame, ImmutableDictionary<string, BoneTransform> Bones,
    ImmutableArray<string> Moved = default, MotionInterpolation? Ease = null, MotionCurve? Curve = null)
{
    public ImmutableArray<string> Moved { get; init; } = Moved.IsDefault ? [] : Moved;
}

/// <summary>
/// One bone layer of an animation. It changes the motion from <paramref name="Start"/> to
/// <paramref name="End"/> (timeline frames), easing in over <paramref name="FadeIn"/> frames and
/// out over <paramref name="FadeOut"/>. <paramref name="Amount"/> is an offset's strength, an
/// amplitude's factor (1 changes nothing, 0 holds the average pose) or a pose's blend weight.
/// <paramref name="Components"/> are the parts of a transform it changes. <paramref name="Bones"/>
/// limits it to these bones; empty means every bone its keys name, or for an amplitude every bone.
/// <paramref name="Interpolation"/> is how bones go from a key without an ease of its own to the next.
/// Layers apply in their document's order, each to the motion the ones before it made.
/// </summary>
/// <param name="Key">"m" + a number, unique in its document.</param>
internal sealed record MotionLayer(
    string Key,
    MotionLayerKind Kind,
    string Name,
    int Start,
    int End,
    int FadeIn,
    int FadeOut,
    float Amount,
    PoseComponents Components,
    MotionInterpolation Interpolation,
    ImmutableArray<string> Bones = default,
    ImmutableArray<MotionKey> Keys = default,
    bool Enabled = true)
{
    public ImmutableArray<string> Bones { get; init; } = Bones.IsDefault ? [] : Bones;
    public ImmutableArray<MotionKey> Keys { get; init; } = Keys.IsDefault ? [] : Keys;
}
