using System.Collections.Immutable;
using System.Numerics;
using InstantEdit.Models;

namespace InstantEdit.Services.Animations.Motion;

/// <summary>A hand's fingers, thumb first.</summary>
internal enum HandFinger : byte { Thumb, Index, Middle, Ring, Little }

/// <summary>
/// One finger's bones, root first (<c>a</c>, <c>b</c>, then the tip when the skeleton has one). Turning
/// bone i by a positive angle about <paramref name="BendAxes"/>[i], in that bone's own frame, curls the
/// finger toward the palm. Turning the <c>a</c> bone about <paramref name="SpreadAxis"/> (in its frame)
/// times <paramref name="SpreadSign"/> spreads the hand: the index and thumb toward the index side, the
/// little finger away, the ring finger part of the way, the middle finger not at all.
/// </summary>
internal sealed record HandChain(HandFinger Finger, ImmutableArray<int> Bones, ImmutableArray<Vector3> BendAxes, Vector3 SpreadAxis, float SpreadSign);

/// <summary>
/// One hand's finger chains and how they turn, worked out from the skeleton's rest pose, so it holds
/// for any body whose fingers are straight at rest. <paramref name="AcrossAxis"/> turns the thumb across
/// the palm, a positive turn toward it, in <c>j_oya_a</c>'s frame; zero without a thumb.
/// <paramref name="HasTips"/> is true when all four fingers have their <c>iv_*_c</c> tip bone.
/// </summary>
internal sealed record HandRig(MotionSide Side, int Wrist, ImmutableArray<HandChain> Chains, bool HasTips, Vector3 AcrossAxis)
{
    private static readonly (HandFinger Finger, string Name, float Spread)[] Fingers =
        [(HandFinger.Thumb, "oya", 1), (HandFinger.Index, "hito", 1), (HandFinger.Middle, "naka", 0), (HandFinger.Ring, "kusu", -.6f), (HandFinger.Little, "ko", -1)];

    /// <summary>
    /// The <paramref name="side"/> hand's rig on <paramref name="skeleton"/>. A finger whose <c>a</c> or
    /// <c>b</c> bone is missing, or whose axes can't be worked out (a bone of no length), is left out.
    /// Null without the wrist, with fewer than two of the four fingers, which the palm's facing is
    /// worked out from, or when those fingers give no palm.
    /// </summary>
    public static HandRig? For(SkeletonDescription skeleton, MotionSide side)
    {
        var index = MotionEvaluator.Index(skeleton);
        var suffix = side == MotionSide.Left ? "_l" : "_r";
        if (!index.TryGetValue("j_te" + suffix, out var wrist))
            return null;
        var bones = skeleton.Bones;
        var found = new List<(HandFinger Finger, float Spread, int[] Bones)>(Fingers.Length);
        foreach (var (finger, name, spread) in Fingers)
        {
            if (!index.TryGetValue($"j_{name}_a{suffix}", out var a) || !index.TryGetValue($"j_{name}_b{suffix}", out var b) || bones[b].Parent != a)
                continue;
            found.Add((finger, spread, index.TryGetValue($"iv_{name}_c{suffix}", out var tip) && bones[tip].Parent == b ? [a, b, tip] : [a, b]));
        }
        var fingers = found.Where(item => item.Finger != HandFinger.Thumb).ToArray();
        if (fingers.Length < 2)
            return null;

        var rest = MotionSymmetry.RestModel(skeleton);
        // Toward the bone's child; the last bone points the way its parent does.
        Vector3 Direction(int[] chain, int at) =>
            at + 1 < chain.Length ? Vector3.Normalize(rest[chain[at + 1]].Position - rest[chain[at]].Position) : Direction(chain, at - 1);
        Vector3 Local(Vector3 model, int bone) => Vector3.Transform(model, Quaternion.Inverse(rest[bone].Rotation));

        var across = Vector3.Normalize(rest[fingers[0].Bones[0]].Position - rest[fingers[^1].Bones[0]].Position);
        var middle = Direction(fingers.FirstOrDefault(item => item.Finger == HandFinger.Middle).Bones ?? fingers[0].Bones, 0);
        var palm = Vector3.Normalize(Vector3.Cross(across, middle));
        // Zero-length bones or fingers at one spot give no palm; MathF.Sign would throw on the NaN.
        if (!Finite(palm))
            return null;
        if (palm.X * rest[wrist].Position.X > 0)
            palm = -palm;

        var chains = ImmutableArray.CreateBuilder<HandChain>(found.Count);
        var acrossAxis = Vector3.Zero;
        foreach (var (finger, spread, chain) in found)
        {
            var axes = ImmutableArray.CreateBuilder<Vector3>(chain.Length);
            for (var i = 0; i < chain.Length; i++)
                axes.Add(Local(Vector3.Normalize(Vector3.Cross(Direction(chain, i), palm)), chain[i]));
            var along = Direction(chain, 0);
            if (!Finite(along) || !axes.All(Finite))
                continue;
            chains.Add(new HandChain(finger, [.. chain], axes.MoveToImmutable(), Local(palm, chain[0]),
                spread * MathF.Sign(Vector3.Dot(Vector3.Cross(palm, along), across))));
            if (finger == HandFinger.Thumb)
                acrossAxis = Local(middle * MathF.Sign(Vector3.Dot(Vector3.Cross(middle, along), palm)), chain[0]);
        }
        if (chains.Count == 0)
            return null;
        var hasTips = chains.Count(chain => chain.Finger != HandFinger.Thumb && chain.Bones.Length == 3) == 4;
        return new HandRig(side, wrist, chains.ToImmutable(), hasTips, acrossAxis);
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
