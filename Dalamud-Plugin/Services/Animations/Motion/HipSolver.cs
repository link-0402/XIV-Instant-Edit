using System.Collections.Immutable;
using System.Numerics;
using InstantEdit.Models;

namespace InstantEdit.Services.Animations.Motion;

/// <summary>A bone a hip drag holds where it is: the chain that bends to keep it there (top first) and that chain's rest normal.</summary>
internal sealed record HipAnchor(int Bone, PinChain Chain, Vector3 RestNormal);

/// <summary>What a hip drag holds and how the body bends around the moved hips. Pure.</summary>
internal static class HipSolver
{
    /// <summary>What a hip drag holds until the user unticks it: both feet and both hands.</summary>
    public static readonly ImmutableArray<string> Defaults = [PinChains.FootL, PinChains.FootR, "j_te_l", "j_te_r"];

    /// <summary>The bone the upper body hangs from, which the hips' move carries along: the game's lowest back bone, a sibling of the hips.</summary>
    public const string BackBase = "j_sebo_a";

    /// <summary>The back's lowest bone when it is not under the hips (the game's skeleton); -1 when the skeleton lacks it or has it under the hips.</summary>
    public static int Carried(SkeletonDescription skeleton)
    {
        var index = MotionEvaluator.Index(skeleton);
        return index.TryGetValue(BackBase, out var bone) && index.TryGetValue(PinChains.Hips, out var hips) && !PinChains.IsBelow(skeleton, bone, hips) &&
               skeleton.Bones[bone].Parent != hips
            ? bone
            : -1;
    }

    /// <summary>The bones of <paramref name="selected"/> that may hold: those the skeleton has below the hips or at or below the carried back, in skeleton order; the hips and bones above them are left out.</summary>
    public static List<int> Candidates(SkeletonDescription skeleton, IEnumerable<string> selected)
    {
        var index = MotionEvaluator.Index(skeleton);
        if (!index.TryGetValue(PinChains.Hips, out var hips))
            return [];
        var carried = Carried(skeleton);
        var candidates = new SortedSet<int>();
        foreach (var name in selected)
            if (index.TryGetValue(name, out var bone) && (PinChains.IsBelow(skeleton, bone, hips) || carried >= 0 && (bone == carried || PinChains.IsBelow(skeleton, bone, carried))))
                candidates.Add(bone);
        return [.. candidates];
    }

    /// <summary>
    /// The anchors of a drag that holds <paramref name="held"/>: their candidates, each with the chain from its farthest
    /// ancestor below the hips down to it. A bone whose parent is the hips has nothing to bend and is left out; of two on
    /// one line only the lower holds. In skeleton order. Empty on a skeleton without the hips.
    /// </summary>
    public static ImmutableArray<HipAnchor> Anchors(SkeletonDescription skeleton, IEnumerable<string> held) =>
        Chains(skeleton, Candidates(skeleton, held), _ => false);

    /// <summary>
    /// <paramref name="candidates"/> as anchors, each with the chain from its farthest ancestor below the hips and below every bone
    /// <paramref name="fixedBone"/> names (one another solve turns) down to it; one with nothing to bend is left out, and of two on one line only the lower holds.
    /// </summary>
    internal static ImmutableArray<HipAnchor> Chains(SkeletonDescription skeleton, IEnumerable<int> candidates, Func<string, bool> fixedBone)
    {
        var index = MotionEvaluator.Index(skeleton);
        var held = new List<HipAnchor>();
        foreach (var bone in candidates)
        {
            var name = skeleton.Bones[bone].Name;
            // The farthest ancestor below the hips; none when the bone hangs right from them.
            var top = PinChains.TopChoices(skeleton, name).TakeWhile(choice => choice != PinChains.Hips && !fixedBone(choice)).LastOrDefault();
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

    /// <summary>
    /// The back the arms' chains share: the bones above each arm's clavicle, from the lowest to the one under the clavicles
    /// (the common start of every limb's). Fewer than two when no held limb has a back above it.
    /// </summary>
    public static ImmutableArray<int> Back(ReadOnlySpan<HipAnchor> anchors)
    {
        var back = ImmutableArray<int>.Empty;
        var first = true;
        foreach (var anchor in anchors)
        {
            if (anchor.Chain.Shape != PinChainShape.Limb || anchor.Chain.Bones.Length < 4)
                continue;
            // The bones above the limb, less the clavicle, the last of them.
            var above = anchor.Chain.Bones.AsSpan(0, anchor.Chain.Bones.Length - 3).ToArray();
            back = first ? [.. above] : [.. back.TakeWhile((bone, i) => i < above.Length && above[i] == bone)];
            first = false;
        }
        return back.Length < 2 ? ImmutableArray<int>.Empty : back;
    }

    /// <summary>
    /// Settles the back before the arms bend: the back leans, as long as it was, until the shoulders are within the arms'
    /// reach of their held hands (the arms' soft reach as the pose had them), and no further; it takes the hips' move whole when
    /// they are. The lean spreads over the back, and the chest turns with the last bone's share.
    /// </summary>
    private static void SettleBack(SkeletonDescription skeleton, MotionTransform[] local, MotionTransform[] model, ReadOnlySpan<HipAnchor> anchors,
        ReadOnlySpan<MotionTransform> startModel)
    {
        var back = Back(anchors);
        if (back.IsEmpty)
            return;
        var chest = back[^1];
        var last = back[^2];
        var chain = new PinChain(chest, PinChainShape.Chain, [.. back.Take(back.Length - 1)], -1, -1);
        var arms = anchors.ToArray().Where(a => a.Chain.Shape == PinChainShape.Limb && a.Chain.Bones.Length >= 4).ToArray();
        var start = startModel.ToArray();
        var reaches = arms.Select(arm => PinSolver.SoftReach(start, arm.Chain)).ToArray();
        for (var round = 0; round < 8; round++)
        {
            var follow = model[chest].Position;
            var target = follow;
            // Each arm that is over its reach pulls the chest toward its hand; together they pull by their mean, so the pulls of two arms
            // on opposite sides cancel sideways instead of swinging the chest from one to the other.
            for (var pass = 0; pass < 12; pass++)
            {
                var pull = Vector3.Zero;
                var over = 0;
                for (var i = 0; i < arms.Length; i++)
                {
                    var away = target + (model[arms[i].Chain.Root].Position - follow) - start[arms[i].Bone].Position;
                    var length = away.Length();
                    if (length > reaches[i] && length > 1e-6f)
                    {
                        pull -= away / length * (length - reaches[i]);
                        over++;
                    }
                }
                if (over == 0)
                    break;
                target += pull / over;
            }
            if (Vector3.Distance(target, follow) < 2e-4f)
                return;
            // The chest stays as far from the back's foot as it is: the back leans rather than shortens.
            var joint = model[back[0]].Position;
            var aim = joint + Vector3.Normalize(target - joint) * Vector3.Distance(follow, joint);
            var before = Quaternion.Normalize(local[last].Rotation);
            PinSolver.Reach(skeleton, local, model, chain, aim, Vector3.Zero);
            // The chest already turns with the whole lean below it; it turns again by the last bone's own turn, in model space.
            var parent = skeleton.Bones[last].Parent;
            var frame = parent >= 0 ? Quaternion.Normalize(model[parent].Rotation) : Quaternion.Identity;
            var share = Quaternion.Normalize(frame * Quaternion.Normalize(local[last].Rotation) * Quaternion.Inverse(before) * Quaternion.Inverse(frame));
            PinSolver.HoldTurn(skeleton, local, model, chest, Quaternion.Normalize(share * Quaternion.Normalize(model[chest].Rotation)));
        }
    }

    /// <summary>The share of an arm's swing its clavicle takes, as the shoulder blade takes about a third of an arm's raise.</summary>
    public const float ClavicleShare = 1f / 3;

    /// <summary>
    /// Turns each held arm's clavicle by <see cref="ClavicleShare"/> of the arm's swing: the turn, in the clavicle's parent's space,
    /// from the line from the clavicle to the held hand as the pose had it to that line now the body has moved. The shoulder
    /// then follows the hand a little, and the arm bends the rest.
    /// </summary>
    private static void FollowClavicles(SkeletonDescription skeleton, MotionTransform[] local, MotionTransform[] model, ReadOnlySpan<HipAnchor> anchors,
        ReadOnlySpan<MotionTransform> startModel)
    {
        foreach (var anchor in anchors)
        {
            // A head drag's arm starts at its clavicle; a hip drag's at the back.
            if (anchor.Chain.Shape != PinChainShape.Limb || anchor.Chain.Bones.Length < 3)
                continue;
            var clavicle = anchor.Chain.Bones[^3];
            var parent = skeleton.Bones[clavicle].Parent;
            if (parent < 0)
                continue;
            var was = Vector3.Transform(startModel[anchor.Bone].Position - startModel[clavicle].Position, Quaternion.Inverse(Quaternion.Normalize(startModel[parent].Rotation)));
            var now = Vector3.Transform(startModel[anchor.Bone].Position - model[clavicle].Position, Quaternion.Inverse(Quaternion.Normalize(model[parent].Rotation)));
            if (was.Length() < 1e-6f || now.Length() < 1e-6f)
                continue;
            var swing = MotionEvaluator.Pow(PinSolver.Between(was, now), ClavicleShare);
            local[clavicle] = local[clavicle] with { Rotation = Quaternion.Normalize(swing * Quaternion.Normalize(local[clavicle].Rotation)) };
            PinSolver.Refresh(skeleton, local, model, clavicle);
        }
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
        // The back goes with the hips; the back's lean, the clavicles and the arms then settle around the hands.
        var carried = Carried(skeleton);
        if (carried >= 0)
        {
            var carriedParent = skeleton.Bones[carried].Parent;
            var carriedRotation = carriedParent >= 0 ? Quaternion.Normalize(model[carriedParent].Rotation) : Quaternion.Identity;
            local[carried] = local[carried] with { Position = local[carried].Position + Vector3.Transform(aim - startModel[hips].Position, Quaternion.Inverse(carriedRotation)) };
            PinSolver.Refresh(skeleton, local, model, carried);
        }
        SettleBack(skeleton, local, model, anchors, startModel);
        HoldAnchors(skeleton, local, model, anchors, startModel);
    }

    /// <summary>
    /// Puts every anchor back at its transform in <paramref name="startModel"/> on the moved body: the clavicles follow their arms, every
    /// chain bends (the shared ones twice over), then each anchor holds its turn.
    /// </summary>
    internal static void HoldAnchors(SkeletonDescription skeleton, MotionTransform[] local, MotionTransform[] model, ReadOnlySpan<HipAnchor> anchors,
        ReadOnlySpan<MotionTransform> startModel)
    {
        FollowClavicles(skeleton, local, model, anchors, startModel);
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
