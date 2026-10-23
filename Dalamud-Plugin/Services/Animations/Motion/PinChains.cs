using System.Collections.Immutable;
using InstantEdit.Models;

namespace InstantEdit.Services.Animations.Motion;

/// <summary>How a pin bends the bones above it: as a two-bone limb, as a chain, or not at all.</summary>
internal enum PinChainShape : byte
{
    /// <summary>The pinned bone sits right under the body: nothing below the body can bend to hold it.</summary>
    None,
    /// <summary>A hand or a foot: the upper and lower limb bend in the plane the motion gives them.</summary>
    Limb,
    /// <summary>Any other bone: the bend spreads over the chain.</summary>
    Chain,
}

/// <summary>
/// The bones a pin turns to hold <paramref name="End"/>, top first. For a limb, <paramref name="Root"/>
/// and <paramref name="Mid"/> are its upper and lower bones, the last two of <paramref name="Bones"/>,
/// and the bones before them sit above the limb; for a chain both are -1. Bones between the lower limb
/// bone and the end (the leg's calf link) keep their own motion and are not in the chain.
/// </summary>
internal sealed record PinChain(int End, PinChainShape Shape, ImmutableArray<int> Bones, int Root, int Mid);

/// <summary>
/// Which bones a pin bends to hold its bone, from the skeleton; resolved when the layer applies,
/// never stored. Nothing at or above the body (<see cref="Body"/>) ever turns: that would move the
/// whole character. Pure.
/// </summary>
internal static class PinChains
{
    public const string FootL = "j_asi_d_l", FootR = "j_asi_d_r", Body = "n_hara";

    /// <summary>The upper and lower bones of the limb a hand or a foot ends, by the end's name; null for other bones.</summary>
    private static (string Upper, string Lower)? LimbOf(string bone) => bone switch
    {
        "j_te_l" => ("j_ude_a_l", "j_ude_b_l"),
        "j_te_r" => ("j_ude_a_r", "j_ude_b_r"),
        FootL => ("j_asi_a_l", "j_asi_b_l"),
        FootR => ("j_asi_a_r", "j_asi_b_r"),
        _ => null,
    };

    /// <summary>
    /// The chain <paramref name="pin"/> bends. Throws <see cref="MotionEvaluator.Missing"/> for a pinned,
    /// top or relative bone <paramref name="skeleton"/> lacks, and for a top that is not above the bone below the body.
    /// </summary>
    public static PinChain Chain(SkeletonDescription skeleton, MotionPin pin) => Chain(skeleton, MotionEvaluator.Index(skeleton), pin, "A bone layer");

    /// <summary><see cref="Chain(SkeletonDescription, MotionPin)"/> with the skeleton's bone index already made (<see cref="MotionEvaluator.Index"/>).</summary>
    public static PinChain Chain(SkeletonDescription skeleton, Dictionary<string, int> index, MotionPin pin) => Chain(skeleton, index, pin, "A bone layer");

    /// <summary>The name of the top <paramref name="bone"/> bends up to by default; null when nothing can bend.</summary>
    public static string? DefaultTop(SkeletonDescription skeleton, string bone)
    {
        var index = MotionEvaluator.Index(skeleton);
        var end = Find(index, bone);
        var top = DefaultTop(skeleton, index, end);
        return top < 0 ? null : skeleton.Bones[top].Name;
    }

    /// <summary>
    /// The bones <paramref name="bone"/> may bend up to, nearest first, stopping below the body (or the root);
    /// with <paramref name="relativeTo"/>, only those it doesn't hang from, as a pin can't ride a bone its solve moves.
    /// </summary>
    public static ImmutableArray<string> TopChoices(SkeletonDescription skeleton, string bone, string? relativeTo = null)
    {
        var index = MotionEvaluator.Index(skeleton);
        var relative = relativeTo == null ? -1 : Find(index, relativeTo);
        return [.. Ancestors(skeleton, Find(index, bone))
            .Where(top => relative < 0 || relative != top && !IsBelow(skeleton, relative, top))
            .Select(b => skeleton.Bones[b].Name)];
    }

    /// <summary>
    /// Throws <see cref="InvalidDataException"/> when a pin of <paramref name="layer"/> names a bone the
    /// skeleton lacks, bends up to a bone that isn't above it below the body, or holds its bone relative
    /// to a bone that moves with its own solve: itself, a bone its chain turns, or one hanging from either
    /// (it would chase itself).
    /// </summary>
    public static void Check(SkeletonDescription skeleton, MotionLayer layer)
    {
        var index = MotionEvaluator.Index(skeleton);
        var name = string.IsNullOrWhiteSpace(layer.Name) ? "A bone layer" : $"The bone layer {layer.Name}";
        foreach (var pin in layer.Pins)
        {
            var chain = Chain(skeleton, index, pin, name);
            if (pin.RelativeTo == null)
                continue;
            // Everything from the chain's top down moves with the solve: the chain, the pinned bone and all that hangs from them.
            var relative = Find(index, pin.RelativeTo);
            var top = chain.Bones.IsEmpty ? chain.End : chain.Bones[0];
            if (relative == top || IsBelow(skeleton, relative, top))
                throw new InvalidDataException(
                    $"{name} holds {AnimationPresentation.BoneName(pin.Bone)} relative to {AnimationPresentation.BoneName(pin.RelativeTo)}, which moves with it.");
        }
    }

    /// <summary>The pins of <paramref name="layer"/> that act: planted ones, and keyed ones with a key.</summary>
    public static IEnumerable<MotionPin> Acting(MotionLayer layer) =>
        layer.Pins.Where(pin => pin.Planted || layer.Keys.Any(key => key.Bones.ContainsKey(pin.Bone)));

    /// <summary>The bones <paramref name="pin"/> may change: its own and the ones it bends; none when nothing can bend.</summary>
    public static IEnumerable<int> Changed(SkeletonDescription skeleton, MotionPin pin)
    {
        var chain = Chain(skeleton, pin);
        return chain.Shape == PinChainShape.None ? [] : chain.Bones.Append(chain.End);
    }

    /// <summary>
    /// The names of bones <paramref name="layer"/>'s acting pins change that <paramref name="tracks"/> lacks, once
    /// each: saving gives them tracks, so physics stops moving them. Empty when the tracks are unknown.
    /// </summary>
    public static ImmutableArray<string> Untracked(SkeletonDescription skeleton, MotionLayer layer, IReadOnlyCollection<short> tracks)
    {
        if (tracks.Count == 0)
            return [];
        var tracked = tracks.Select(b => (int)b).ToHashSet();
        var names = new List<string>();
        foreach (var pin in Acting(layer))
            foreach (var bone in Changed(skeleton, pin))
                if (!tracked.Contains(bone) && !names.Contains(skeleton.Bones[bone].Name, StringComparer.Ordinal))
                    names.Add(skeleton.Bones[bone].Name);
        return [.. names];
    }

    private static PinChain Chain(SkeletonDescription skeleton, Dictionary<string, int> index, MotionPin pin, string name)
    {
        var end = Find(index, pin.Bone);
        if (pin.RelativeTo != null)
            Find(index, pin.RelativeTo);
        var ancestors = Ancestors(skeleton, end);
        int top;
        if (pin.Top == null)
            top = DefaultTop(skeleton, index, end);
        else
        {
            top = Find(index, pin.Top);
            if (!ancestors.Contains(top))
                throw new InvalidDataException(
                    $"{name} lets {AnimationPresentation.BoneName(pin.Bone)} bend up to {AnimationPresentation.BoneName(pin.Top)}, which isn't above it below the body.");
        }
        if (top < 0)
            return new PinChain(end, PinChainShape.None, [], -1, -1);
        // The path from the top down to the pinned bone's parent, top first.
        var path = ancestors.TakeWhile(b => b != top).Append(top).Reverse().ToList();
        if (Limb(skeleton, index, end) is var (root, mid) && path.Contains(root))
        {
            // The calf link between the knee and the ankle keeps its own motion.
            path = path.TakeWhile(b => b != mid).Append(mid).ToList();
            return new PinChain(end, PinChainShape.Limb, [.. path], root, mid);
        }
        return new PinChain(end, PinChainShape.Chain, [.. path], -1, -1);
    }

    /// <summary>The default top: a limb's upper bone, else two bones up but never the body or above; -1 when nothing can bend.</summary>
    private static int DefaultTop(SkeletonDescription skeleton, Dictionary<string, int> index, int end)
    {
        if (Limb(skeleton, index, end) is var (root, _))
            return root;
        var ancestors = Ancestors(skeleton, end);
        return ancestors.Count == 0 ? -1 : ancestors[Math.Min(1, ancestors.Count - 1)];
    }

    /// <summary>A hand's or a foot's upper and lower limb bones, when both are above it, the lower one nearer.</summary>
    private static (int Root, int Mid)? Limb(SkeletonDescription skeleton, Dictionary<string, int> index, int end)
    {
        if (LimbOf(skeleton.Bones[end].Name) is not var (upper, lower) ||
            !index.TryGetValue(upper, out var root) || !index.TryGetValue(lower, out var mid))
            return null;
        var ancestors = Ancestors(skeleton, end);
        var at = ancestors.IndexOf(mid);
        return at >= 0 && ancestors.IndexOf(root) > at ? (root, mid) : null;
    }

    /// <summary><paramref name="bone"/>'s ancestors, nearest first, stopping before the body or the root.</summary>
    private static List<int> Ancestors(SkeletonDescription skeleton, int bone)
    {
        var ancestors = new List<int>();
        for (var b = (int)skeleton.Bones[bone].Parent; b >= 0 && skeleton.Bones[b].Parent >= 0 && skeleton.Bones[b].Name != Body; b = skeleton.Bones[b].Parent)
            ancestors.Add(b);
        return ancestors;
    }

    /// <summary>Whether <paramref name="bone"/> hangs below <paramref name="above"/>.</summary>
    private static bool IsBelow(SkeletonDescription skeleton, int bone, int above)
    {
        for (var b = (int)skeleton.Bones[bone].Parent; b >= 0; b = skeleton.Bones[b].Parent)
            if (b == above)
                return true;
        return false;
    }

    private static int Find(Dictionary<string, int> index, string bone) => index.TryGetValue(bone, out var b) ? b : throw MotionEvaluator.Missing(bone);
}
