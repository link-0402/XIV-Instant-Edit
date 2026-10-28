using System.Collections.Immutable;
using System.Numerics;
using InstantEdit.Models;

namespace InstantEdit.Services.Animations.Motion;

/// <summary>A bone a hip drag holds where it is: the chain that bends to keep it there (top first) and that chain's rest normal.</summary>
internal sealed record HipAnchor(int Bone, PinChain Chain, Vector3 RestNormal);

/// <summary>What a hip drag holds and how the body bends around the moved hips. Pure.</summary>
internal static class HipSolver
{
    /// <summary>Held when nothing else is selected with the hips: both feet and both hands.</summary>
    public static readonly ImmutableArray<string> Defaults = [PinChains.FootL, PinChains.FootR, "j_te_l", "j_te_r"];

    /// <summary>The selected bones that may hold: those the skeleton has below the hips, in skeleton order; the hips and bones above them are left out.</summary>
    public static List<int> Candidates(SkeletonDescription skeleton, IEnumerable<string> selected)
    {
        var index = MotionEvaluator.Index(skeleton);
        if (!index.TryGetValue(PinChains.Hips, out var hips))
            return [];
        var candidates = new SortedSet<int>();
        foreach (var name in selected)
            if (index.TryGetValue(name, out var bone) && PinChains.IsBelow(skeleton, bone, hips))
                candidates.Add(bone);
        return [.. candidates];
    }

    /// <summary>
    /// The anchors of a drag: the candidates, or with none the defaults the skeleton has; each with the chain from its
    /// farthest ancestor below the hips down to it. A bone whose parent is the hips has nothing to bend and is left out;
    /// of two on one line only the lower holds. In skeleton order. Empty on a skeleton without the hips.
    /// </summary>
    public static ImmutableArray<HipAnchor> Anchors(SkeletonDescription skeleton, IEnumerable<string> selected)
    {
        var candidates = Candidates(skeleton, selected);
        if (candidates.Count == 0)
            candidates = Candidates(skeleton, Defaults);
        var index = MotionEvaluator.Index(skeleton);
        var held = new List<HipAnchor>();
        foreach (var bone in candidates)
        {
            var name = skeleton.Bones[bone].Name;
            // The farthest ancestor below the hips; none when the bone hangs right from them.
            var top = PinChains.TopChoices(skeleton, name).TakeWhile(choice => choice != PinChains.Hips).LastOrDefault();
            if (top == null)
                continue;
            var chain = PinChains.Chain(skeleton, index, new MotionPin(name, Top: top));
            held.Add(new HipAnchor(bone, chain, PinSolver.RestNormal(skeleton, chain)));
        }
        // The lower of two on one line holds: its chain turns the bones that would hold the higher one.
        return [.. held.Where(anchor => !held.Any(other => PinChains.IsBelow(skeleton, other.Bone, anchor.Bone)))];
    }

    /// <summary>The anchors whose chains share a bone with another's: solved a second time.</summary>
    public static ImmutableArray<HipAnchor> Shared(ReadOnlySpan<HipAnchor> anchors)
    {
        var shared = ImmutableArray.CreateBuilder<HipAnchor>();
        for (var i = 0; i < anchors.Length; i++)
            for (var j = 0; j < anchors.Length; j++)
                if (i != j && anchors[i].Chain.Bones.Intersect(anchors[j].Chain.Bones).Any())
                {
                    shared.Add(anchors[i]);
                    break;
                }
        return shared.ToImmutable();
    }

    /// <summary>Every bone a solve may turn: the anchors' chain bones and the anchors themselves, distinct, ascending.</summary>
    public static ImmutableArray<int> Turned(ReadOnlySpan<HipAnchor> anchors)
    {
        var bones = new SortedSet<int>();
        foreach (var anchor in anchors)
        {
            bones.UnionWith(anchor.Chain.Bones);
            bones.Add(anchor.Bone);
        }
        return [.. bones];
    }

    /// <summary>
    /// Moves the hips (<paramref name="hips"/>) of the pose so their model position is <paramref name="aim"/>, bends every
    /// anchor's chain to put the anchor back at its transform in <paramref name="startModel"/> (the shared ones twice over),
    /// then holds each anchor's turn. <paramref name="local"/> and <paramref name="model"/> are changed in place and kept in step.
    /// </summary>
    public static void Solve(SkeletonDescription skeleton, MotionTransform[] local, MotionTransform[] model, int hips, Vector3 aim,
        ReadOnlySpan<HipAnchor> anchors, ReadOnlySpan<MotionTransform> startModel)
    {
        // The hips' move taken into their parent's space; their own turn stays.
        var parent = skeleton.Bones[hips].Parent;
        var parentRotation = parent >= 0 ? Quaternion.Normalize(model[parent].Rotation) : Quaternion.Identity;
        local[hips] = local[hips] with { Position = local[hips].Position + Vector3.Transform(aim - model[hips].Position, Quaternion.Inverse(parentRotation)) };
        PinSolver.Refresh(skeleton, local, model, hips);
        // Each chain bends on the running pose, in skeleton order; those sharing bones go again, so a lean one needs is seen by the other.
        foreach (var anchor in anchors)
            PinSolver.Reach(skeleton, local, model, anchor.Chain, startModel[anchor.Bone].Position, anchor.RestNormal);
        foreach (var anchor in Shared(anchors))
            PinSolver.Reach(skeleton, local, model, anchor.Chain, startModel[anchor.Bone].Position, anchor.RestNormal);
        // The turns are held after every reach, so a lean doesn't tilt a held hand.
        foreach (var anchor in anchors)
            PinSolver.HoldTurn(skeleton, local, model, anchor.Bone, startModel[anchor.Bone].Rotation);
    }
}
