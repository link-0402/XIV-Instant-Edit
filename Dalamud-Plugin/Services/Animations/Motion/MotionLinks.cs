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
        Print = Fingerprint(before, after, Duration);
    }

    public ImmutableArray<MotionTransform> Before { get; }
    public ImmutableArray<MotionTransform> After { get; }
    public float Duration { get; }
    public int Samples => Before.Length;

    /// <summary>
    /// The track's content as 16 hex digits: the same for two tracks with the same samples and duration, bit for bit, whatever their
    /// instances. What a save remembers of a link, so a partner rebuilt to the same content doesn't make the clip unsaved.
    /// </summary>
    public string Print { get; }

    // FNV-1a over the float bits, 64 bits wide: stable across runs and machines, so a journal can keep it.
    private static string Fingerprint(ImmutableArray<MotionTransform> before, ImmutableArray<MotionTransform> after, float duration)
    {
        var hash = 14695981039346656037UL;
        void Add(float value)
        {
            var bits = BitConverter.SingleToUInt32Bits(value);
            for (var i = 0; i < 4; i++, bits >>= 8)
                hash = (hash ^ (bits & 0xFF)) * 1099511628211UL;
        }
        void AddAll(ImmutableArray<MotionTransform> samples)
        {
            foreach (var t in samples)
            {
                Add(t.Position.X); Add(t.Position.Y); Add(t.Position.Z);
                Add(t.Rotation.X); Add(t.Rotation.Y); Add(t.Rotation.Z); Add(t.Rotation.W);
                Add(t.Scale.X); Add(t.Scale.Y); Add(t.Scale.Z);
            }
        }
        Add(duration);
        Add(before.Length);
        AddAll(before);
        AddAll(after);
        return hash.ToString("x16");
    }

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

    /// <summary>No prints: the one empty instance.</summary>
    public static readonly ImmutableDictionary<string, string> NoPrints = ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);

    /// <summary>
    /// The <see cref="LinkTrack.Print"/> of each link layer of <paramref name="motion"/> that is active with <paramref name="links"/>, by
    /// layer key: what a save or a preview bakes the links as. Links that are off have none.
    /// </summary>
    public static ImmutableDictionary<string, string> Prints(IReadOnlyList<MotionLayer> motion, MotionLinkSet? links)
    {
        if (links == null || links.Tracks.IsEmpty)
            return NoPrints;
        var prints = NoPrints.ToBuilder();
        foreach (var layer in motion)
            if (layer.Kind == MotionLayerKind.Link && MotionEvaluator.Active(layer, links))
                prints[layer.Key] = links.For(layer.Key)!.Print;
        return prints.Count == 0 ? NoPrints : prints.ToImmutable();
    }

    /// <summary>Whether two sets of prints hold the same links with the same content.</summary>
    public static bool SamePrints(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        ReferenceEquals(a, b) || a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var print) && print == pair.Value);

    /// <summary>
    /// Whether an active link layer of <paramref name="motion"/> follows a track in <paramref name="links"/> whose content isn't the
    /// print <paramref name="saved"/> holds for it, or has no saved print (it was off when saved): the partner changed since. A link that
    /// is off now counts for nothing.
    /// </summary>
    public static bool Unsaved(IReadOnlyList<MotionLayer> motion, MotionLinkSet links, IReadOnlyDictionary<string, string> saved)
    {
        if (links.Tracks.IsEmpty)
            return false;
        foreach (var layer in motion)
            if (layer.Kind == MotionLayerKind.Link && MotionEvaluator.Active(layer, links) &&
                (!saved.TryGetValue(layer.Key, out var print) || print != links.For(layer.Key)!.Print))
                return true;
        return false;
    }

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

    /// <summary>
    /// The partner's link back to <paramref name="link"/>: the first of <paramref name="partnerLayers"/> that links from the workspace
    /// <paramref name="self"/> (null for You), with this link's bones the other way round. Null when the partner doesn't link back.
    /// </summary>
    public static MotionLayer? LinkBack(IReadOnlyList<MotionLayer> partnerLayers, MotionLink link, Guid? self)
    {
        foreach (var layer in partnerLayers)
            if (layer is { Kind: MotionLayerKind.Link, Link: { } back } && back.Partner == self &&
                back.Bone == link.PartnerBone && back.PartnerBone == link.Bone)
                return layer;
        return null;
    }

    /// <summary>
    /// The partner's own edits, what a link carries: its layers above <paramref name="linkBack"/> (all of them when it is null), in
    /// order, without links. Links never carry links, so two links between the same clips can't feed each other.
    /// </summary>
    public static ImmutableArray<MotionLayer> Own(IReadOnlyList<MotionLayer> partnerLayers, MotionLayer? linkBack)
    {
        var own = ImmutableArray.CreateBuilder<MotionLayer>();
        foreach (var layer in partnerLayers)
        {
            if (linkBack != null && ReferenceEquals(layer, linkBack))
                break;
            if (layer.Kind != MotionLayerKind.Link)
                own.Add(layer);
        }
        return own.ToImmutable();
    }

    /// <summary>
    /// The track of a link to <paramref name="partnerBone"/> on the partner's clip as <paramref name="played"/>: the bone in model space
    /// as played and with the partner's <paramref name="own"/> edits, positions times <paramref name="ratio"/> (partner size over this
    /// character's). Leaves <paramref name="played"/> as it was. Throws <see cref="MotionEvaluator.Missing"/> for a bone the partner's
    /// skeleton lacks.
    /// </summary>
    public static LinkTrack Track(MotionClip played, IReadOnlyList<MotionLayer> own, string partnerBone, float ratio, CancellationToken token)
    {
        if (!float.IsFinite(ratio) || ratio <= 0)
            throw new ArgumentOutOfRangeException(nameof(ratio), "The partners' sizes don't give a ratio.");
        var bone = MotionEvaluator.Index(played.Skeleton).TryGetValue(partnerBone, out var found) ? found : throw MotionEvaluator.Missing(partnerBone);
        var edited = played.Clone();
        MotionEvaluator.Apply(edited, own, token);
        return new LinkTrack(Sampled(played, bone, ratio, token), Sampled(edited, bone, ratio, token), played.Duration);
    }

    /// <summary><paramref name="clip"/>'s <paramref name="bone"/> in model space at each sample, as <see cref="MotionEvaluator.ModelSpace"/> gives it, positions times <paramref name="ratio"/>.</summary>
    private static ImmutableArray<MotionTransform> Sampled(MotionClip clip, int bone, float ratio, CancellationToken token)
    {
        // The bone and its parents, root first: composing just them gives what ModelSpace does for the bone.
        var chain = new List<int>();
        for (var b = bone; b >= 0; b = clip.Skeleton.Bones[b].Parent)
            chain.Add(b);
        chain.Reverse();
        var samples = ImmutableArray.CreateBuilder<MotionTransform>(clip.Samples);
        for (var i = 0; i < clip.Samples; i++)
        {
            if ((i & 63) == 0)
                token.ThrowIfCancellationRequested();
            var model = clip.Get(i, chain[0]);
            for (var c = 1; c < chain.Count; c++)
                model = MotionEvaluator.Compose(model, clip.Get(i, chain[c]));
            samples.Add(model with { Position = model.Position * ratio });
        }
        return samples.MoveToImmutable();
    }
}
