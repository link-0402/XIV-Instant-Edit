using System.Collections.Immutable;
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
}
