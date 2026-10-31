using System.Collections.Immutable;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// What a Drag on the hips or the head holds where it is, as the Bones window's Hold boxes tick it: both feet and both hands
/// the skeleton has, then the other selected bones that can hold. Each is ticked unless the user unticked it; a head drag's hands
/// start unticked. Kept for each drag while the plugin runs; not saved.
/// </summary>
internal sealed class DragHolds
{
    /// <summary>The bones the Hold boxes always offer, when the skeleton has them and they can hold, with their labels.</summary>
    public static readonly ImmutableArray<(string Bone, string Label)> Ends =
        [(PinChains.FootL, "Left foot"), (PinChains.FootR, "Right foot"), ("j_te_l", "Left hand"), ("j_te_r", "Right hand")];

    // The bones unticked, for the hip drag and the head drag: at first the ends the drag's defaults leave out.
    private readonly HashSet<string> hipsOff = Unticked(HipSolver.Defaults), headOff = Unticked(HeadSolver.Defaults);
    // The last boxes found, for the skeleton, drag and selection they were found for: asked every frame.
    private (SkeletonDescription Skeleton, bool Head, string[] Selected, ImmutableArray<string> Bones)? boxes;

    /// <summary>
    /// The bones the Hold boxes offer for a drag on the head (<paramref name="head"/>) or the hips: the ends, then the other bones of
    /// <paramref name="selected"/>, each only when it holds on its own (a thigh, with nothing to bend, never shows).
    /// </summary>
    public static ImmutableArray<string> Offered(SkeletonDescription skeleton, bool head, IEnumerable<string> selected)
    {
        var offered = ImmutableArray.CreateBuilder<string>();
        foreach (var bone in Ends.Select(end => end.Bone).Concat(selected))
            if (!offered.Contains(bone) && Anchors(skeleton, head, [bone]).Length == 1)
                offered.Add(bone);
        return offered.ToImmutable();
    }

    /// <summary>The boxes now, as <see cref="Offered"/> finds them.</summary>
    public ImmutableArray<string> Bones(SkeletonDescription skeleton, bool head, IReadOnlyList<string> selected)
    {
        if (boxes is { } kept && ReferenceEquals(kept.Skeleton, skeleton) && kept.Head == head && kept.Selected.SequenceEqual(selected))
            return kept.Bones;
        var bones = Offered(skeleton, head, selected);
        boxes = (skeleton, head, [.. selected], bones);
        return bones;
    }

    /// <summary>Whether <paramref name="bone"/>'s box is ticked.</summary>
    public bool Holds(bool head, string bone) => !(head ? headOff : hipsOff).Contains(bone);

    public void Hold(bool head, string bone, bool held)
    {
        var off = head ? headOff : hipsOff;
        if (held)
            off.Remove(bone);
        else
            off.Add(bone);
    }

    /// <summary>What the drag holds: the ticked boxes' bones as anchors.</summary>
    public ImmutableArray<HipAnchor> Anchors(SkeletonDescription skeleton, bool head, IReadOnlyList<string> selected) =>
        Anchors(skeleton, head, Bones(skeleton, head, selected).Where(bone => Holds(head, bone)));

    /// <summary>A box's label: the end's short name, else the bone's.</summary>
    public static string Label(string bone) =>
        Ends.FirstOrDefault(end => end.Bone == bone) is { Label: { } label } ? label : AnimationPresentation.BoneName(bone);

    private static HashSet<string> Unticked(ImmutableArray<string> defaults) =>
        new(Ends.Select(end => end.Bone).Except(defaults), StringComparer.Ordinal);

    private static ImmutableArray<HipAnchor> Anchors(SkeletonDescription skeleton, bool head, IEnumerable<string> held) =>
        head ? HeadSolver.Anchors(skeleton, held) : HipSolver.Anchors(skeleton, held);
}
