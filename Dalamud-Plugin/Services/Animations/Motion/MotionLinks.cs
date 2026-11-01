using System.Collections.Immutable;
using System.Numerics;
using InstantEdit.Models;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Services.Animations.Motion;

/// <summary>
/// A link's partner input: the partner bone's model transforms at the partner clip's samples, <see cref="Before"/> as the clip
/// plays and <see cref="After"/> with the partner's own edits, positions already in this character's size. The samples sit on a
/// uniform grid from 0 to <see cref="Duration"/> seconds, as a <see cref="MotionClip"/>'s. Immutable.
/// </summary>
internal sealed class LinkTrack
{
    public LinkTrack(ImmutableArray<MotionTransform> before, ImmutableArray<MotionTransform> after, float duration)
    {
        if (before.IsDefaultOrEmpty || after.IsDefault || before.Length != after.Length)
            throw new ArgumentException("A link track needs as many samples after the partner's edits as before them, at least one.");
        Before = before;
        After = after;
        Duration = float.IsFinite(duration) && duration > 0 ? duration : 0;
    }

    public ImmutableArray<MotionTransform> Before { get; }
    public ImmutableArray<MotionTransform> After { get; }
    public float Duration { get; }
    public int Samples => Before.Length;

    /// <summary>
    /// The partner bone at timeline <paramref name="frame"/>, between the partner's samples there: its first sample before
    /// the partner's start and its last from the partner's end on, so a shorter partner holds where it ended.
    /// </summary>
    public (MotionTransform Before, MotionTransform After) At(float frame)
    {
        var at = Duration <= 0 || !float.IsFinite(frame) ? 0 : Math.Clamp(frame / TimelineDocument.Fps / Duration, 0, 1) * (Samples - 1);
        var low = Math.Min((int)at, Samples - 1);
        var high = Math.Min(low + 1, Samples - 1);
        var t = at - low;
        return (MotionEvaluator.Lerp(Before[low], Before[high], t), MotionEvaluator.Lerp(After[low], After[high], t));
    }
}

/// <summary>
/// The partner tracks of a document's link layers, by layer key. A layer without one stays off. Callers compare sets by
/// reference, so a set is replaced only when a track was added, replaced or removed.
/// </summary>
internal sealed class MotionLinkSet
{
    public static readonly MotionLinkSet Empty = new(ImmutableDictionary<string, LinkTrack>.Empty.WithComparers(StringComparer.Ordinal));

    public MotionLinkSet(ImmutableDictionary<string, LinkTrack> tracks) => Tracks = tracks;

    public ImmutableDictionary<string, LinkTrack> Tracks { get; }

    /// <summary>The track of the link layer keyed <paramref name="layerKey"/>; null when it has none.</summary>
    public LinkTrack? For(string layerKey) => Tracks.GetValueOrDefault(layerKey);
}

/// <summary>What link layers do with their partner tracks. Pure.</summary>
internal static class MotionLinks
{
    /// <summary>
    /// Whether link layer <paramref name="layer"/> may change the motion once its partner's track is there: on, with some
    /// weight and a pull, and with its link. What code that has no tracks asks; false for other kinds.
    /// </summary>
    public static bool MayChange(MotionLayer layer) =>
        layer.Kind == MotionLayerKind.Link && layer.Amount > 0 && layer.Link != null && MotionEvaluator.Weighted(layer);

    /// <summary>
    /// Where the partner's edit takes this bone, <paramref name="current"/> in model space, as if it were attached to the partner's bone:
    /// the partner's move from <paramref name="before"/> to <paramref name="after"/>, and its turn about the partner bone's spot. With
    /// <paramref name="holdTurn"/> the bone turns with it too, else it keeps its own turn. <paramref name="current"/> itself when the
    /// partner didn't move.
    /// </summary>
    public static MotionTransform Aim(MotionTransform current, MotionTransform before, MotionTransform after, bool holdTurn)
    {
        if (before == after)
            return current;
        var turn = Quaternion.Normalize(Quaternion.Normalize(after.Rotation) * Quaternion.Inverse(Quaternion.Normalize(before.Rotation)));
        return current with
        {
            Position = after.Position + Vector3.Transform(current.Position - before.Position, turn),
            Rotation = holdTurn ? Quaternion.Normalize(turn * current.Rotation) : current.Rotation,
        };
    }

    /// <summary>The pin on <paramref name="link"/>'s bone whose chain it bends: its top, bend and turn, relative to the character.</summary>
    public static MotionPin AsPin(MotionLink link) => new(link.Bone, link.HoldTurn, null, link.Top, false, PinSensitivity.Normal, link.Bend);

    /// <summary>
    /// The bones link layer <paramref name="layer"/> may write, by index, ascending: on the hips, the hips, the back they carry and
    /// what bends round the held bones; on another bone, the chain it bends and the bone. None when nothing can bend or for other
    /// kinds. For the views and <see cref="MotionSymmetry.Animated"/>. Throws for a linked bone or top the skeleton lacks; held bones
    /// it lacks are left out.
    /// </summary>
    public static ImmutableArray<int> Moved(SkeletonDescription skeleton, MotionLayer layer)
    {
        if (layer is not { Kind: MotionLayerKind.Link, Link: { } link })
            return [];
        if (link.Bone != PinChains.Hips)
            return [.. PinChains.Changed(skeleton, AsPin(link)).Order()];
        var index = MotionEvaluator.Index(skeleton);
        var bones = new SortedSet<int> { index.TryGetValue(PinChains.Hips, out var hips) ? hips : throw MotionEvaluator.Missing(PinChains.Hips) };
        if (HipSolver.Carried(skeleton) is >= 0 and var carried)
            bones.Add(carried);
        var anchors = HipSolver.Anchors(skeleton, link.Holds).AsSpan();
        bones.UnionWith(HipSolver.Back(anchors));
        bones.UnionWith(HipSolver.Turned(anchors));
        return [.. bones];
    }
}
