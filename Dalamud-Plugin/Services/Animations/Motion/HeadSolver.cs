using System.Collections.Immutable;
using System.Numerics;
using InstantEdit.Models;

namespace InstantEdit.Services.Animations.Motion;

/// <summary>
/// What a head drag holds and how the body follows the moved head: the back leans to it, as long as it was, and the hips take
/// what the lean can't cover; the anchors then hold as in a hip drag (<see cref="HipSolver"/>). Pure.
/// </summary>
internal static class HeadSolver
{
    /// <summary>What a head drag holds until the user changes it: both feet. The arms ride with the body.</summary>
    public static readonly ImmutableArray<string> Defaults = [PinChains.FootL, PinChains.FootR];

    private const float Tiny = 1e-6f;

    /// <summary>
    /// The chain a head drag leans: from the back's lowest bone down to the neck, ending at the head. Null when the skeleton lacks the
    /// head or the hips, or when the hips' move doesn't carry the back (its lowest bone neither hangs below them nor is the back they carry).
    /// </summary>
    public static PinChain? Back(SkeletonDescription skeleton)
    {
        var index = MotionEvaluator.Index(skeleton);
        if (!index.ContainsKey(PinChains.Head) || !index.TryGetValue(PinChains.Hips, out var hips))
            return null;
        var top = PinChains.TopChoices(skeleton, PinChains.Head).TakeWhile(choice => choice != PinChains.Hips).LastOrDefault();
        if (top == null)
            return null;
        var bone = index[top];
        return bone == HipSolver.Carried(skeleton) || PinChains.IsBelow(skeleton, bone, hips)
            ? PinChains.Chain(skeleton, index, new MotionPin(PinChains.Head, Top: top))
            : null;
    }

    /// <summary>The bones of <paramref name="selected"/> that may hold: those a hip drag may hold (<see cref="HipSolver.Candidates"/>), less the back that leans, the head and what hangs from it.</summary>
    public static List<int> Candidates(SkeletonDescription skeleton, IEnumerable<string> selected) =>
        Back(skeleton) is { } back ? Candidates(skeleton, back, selected) : [];

    private static List<int> Candidates(SkeletonDescription skeleton, PinChain back, IEnumerable<string> selected) =>
        [.. HipSolver.Candidates(skeleton, selected).Where(bone => bone != back.End && !back.Bones.Contains(bone) && !PinChains.IsBelow(skeleton, bone, back.End))];

    /// <summary>
    /// The anchors of a head drag that holds <paramref name="held"/>: their candidates, each with the chain from its farthest ancestor
    /// below the hips and off the back down to it, so a hand bends its arm and clavicle. In skeleton order; empty without <see cref="Back"/>.
    /// </summary>
    public static ImmutableArray<HipAnchor> Anchors(SkeletonDescription skeleton, IEnumerable<string> held)
    {
        if (Back(skeleton) is not { } back)
            return [];
        var candidates = Candidates(skeleton, back, held);
        var leaning = back.Bones.Select(bone => skeleton.Bones[bone].Name).ToHashSet(StringComparer.Ordinal);
        return HipSolver.Chains(skeleton, candidates, leaning.Contains);
    }

    /// <summary>Every bone a solve may turn: the back, the head, and the anchors' chain bones and the anchors themselves, distinct, ascending.</summary>
    public static ImmutableArray<int> Turned(PinChain back, ReadOnlySpan<HipAnchor> anchors) =>
        [.. HipSolver.Turned(anchors).Concat(back.Bones).Append(back.End).Distinct().Order()];

    /// <summary>
    /// Moves the head (<paramref name="back"/>'s end) of the pose so its model position is <paramref name="aim"/>: the hips
    /// (<paramref name="hips"/>), and the back's lowest bone with them, move by the part of the aim's distance from that bone past the back's
    /// length, and the back leans to the aim, turning whole about that bone. The head then holds its turn from
    /// <paramref name="startModel"/>, and every anchor its spot and turn. Returns the hips' move, in model space.
    /// <paramref name="local"/> and <paramref name="model"/> are changed in place and kept in step.
    /// </summary>
    public static Vector3 Solve(SkeletonDescription skeleton, MotionTransform[] local, MotionTransform[] model, int hips, PinChain back, Vector3 aim,
        ReadOnlySpan<HipAnchor> anchors, ReadOnlySpan<MotionTransform> startModel)
    {
        var head = back.End;
        var foot = back.Bones[0];
        var carried = HipSolver.Carried(skeleton);
        // The least move of the hips that leaves the aim as far from the back's foot as the head was: the back then only leans.
        var toAim = aim - startModel[foot].Position;
        var length = Vector3.Distance(startModel[head].Position, startModel[foot].Position);
        Shift(skeleton, local, model, hips, carried, toAim.Length() > Tiny ? toAim - Vector3.Normalize(toAim) * length : aim - startModel[head].Position);
        // The back leans whole about its foot, keeping its shape: bending it as well would shorten it, and the hips would have to rise to cover that,
        // lifting planted feet off the floor.
        var joint = model[foot].Position;
        var from = model[head].Position - joint;
        if (from.Length() > Tiny && Vector3.Distance(aim, joint) > Tiny)
            PinSolver.HoldTurn(skeleton, local, model, foot, Quaternion.Normalize(PinSolver.Between(from, aim - joint) * Quaternion.Normalize(model[foot].Rotation)));
        // Only float noise is left; the hips take it too.
        Shift(skeleton, local, model, hips, carried, aim - model[head].Position);
        PinSolver.HoldTurn(skeleton, local, model, head, startModel[head].Rotation);
        HipSolver.HoldAnchors(skeleton, local, model, anchors, startModel);
        return model[hips].Position - startModel[hips].Position;
    }

    /// <summary>Moves the hips, and the back they carry (<paramref name="carried"/>, -1 for none), by <paramref name="move"/> in model space; their turns stay.</summary>
    private static void Shift(SkeletonDescription skeleton, MotionTransform[] local, MotionTransform[] model, int hips, int carried, Vector3 move)
    {
        foreach (var bone in carried >= 0 ? [hips, carried] : (ReadOnlySpan<int>)[hips])
        {
            var parent = skeleton.Bones[bone].Parent;
            var parentRotation = parent >= 0 ? Quaternion.Normalize(model[parent].Rotation) : Quaternion.Identity;
            local[bone] = local[bone] with { Position = local[bone].Position + Vector3.Transform(move, Quaternion.Inverse(parentRotation)) };
            PinSolver.Refresh(skeleton, local, model, bone);
        }
    }
}
