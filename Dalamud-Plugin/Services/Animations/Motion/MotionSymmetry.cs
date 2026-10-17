using System.Collections.Immutable;
using System.Numerics;
using InstantEdit.Models;

namespace InstantEdit.Services.Animations.Motion;

/// <summary>A bone a symmetry layer changes (<paramref name="Target"/>), the twin it copies and the bone above both.</summary>
internal sealed record SymmetryPair(int Source, int Target, int Middle);

/// <summary>
/// What a symmetry layer changes on one skeleton: its pairs, targets in skeleton order (parents
/// first), and the bones on the changed side it leaves alone because they have no twin.
/// </summary>
internal sealed record SymmetryPlan(ImmutableArray<SymmetryPair> Pairs, ImmutableArray<string> Unpaired)
{
    public static readonly SymmetryPlan Empty = new([], []);
}

/// <summary>
/// <see cref="MotionSymmetry.Mirror"/>'s working buffers for one plan on a skeleton of
/// <paramref name="bones"/> bones, kept across the samples of a clip so each pose needs no new arrays.
/// </summary>
internal sealed class SymmetryScratch
{
    public SymmetryScratch(SymmetryPlan plan, int bones)
    {
        Plan = plan;
        Snapshot = new MotionTransform[bones];
        Full = new MotionTransform[bones];
        Original = new MotionTransform[bones];
        PairAt = new int[bones];
        Array.Fill(PairAt, -1);
        for (var i = 0; i < plan.Pairs.Length; i++)
            PairAt[plan.Pairs[i].Target] = i;
    }

    public SymmetryPlan Plan { get; }
    /// <summary>The pose in model space before any copy, which the twins are read from.</summary>
    public MotionTransform[] Snapshot { get; }
    /// <summary>The pose in model space at the full copy.</summary>
    public MotionTransform[] Full { get; }
    /// <summary>The local pose before any copy.</summary>
    public MotionTransform[] Original { get; }
    /// <summary>For each bone, its pair's index in <see cref="Plan"/> when it is a target, else -1.</summary>
    public int[] PairAt { get; }
}

/// <summary>
/// Symmetry layers: which bones copy which, and one pose with the copies made. Each picked bone
/// brings everything below it, and so does its twin, so either side of a pair means the pair. A
/// target's change from its rest pose is its twin's change, mirrored across the body's middle, which
/// turns with the nearest bone above both. Mirroring the change rather than the rotation itself
/// absorbs the flipped local axes of the game's right-side bones.
/// </summary>
internal static class MotionSymmetry
{
    public static string DefaultName(MotionSide from) =>
        from == MotionSide.Left ? "Symmetry (Left → Right)" : "Symmetry (Right → Left)";

    /// <summary>
    /// The pairs <paramref name="layer"/> changes on <paramref name="skeleton"/>. With
    /// <paramref name="animated"/> (the bones the clip has tracks for), a pair whose two bones both have
    /// none is left out, so bones the game's physics drives stay trackless. Throws for a picked bone the
    /// skeleton does not have.
    /// </summary>
    public static SymmetryPlan Plan(SkeletonDescription skeleton, MotionLayer layer, IReadOnlySet<int>? animated = null)
    {
        var index = MotionEvaluator.Index(skeleton);
        var bones = skeleton.Bones;
        var picked = new HashSet<int>();
        foreach (var name in layer.Bones)
        {
            picked.Add(index.TryGetValue(name, out var bone) ? bone : throw MotionEvaluator.Missing(name));
            if (MotionMirror.Twin(name) is { } twin && index.TryGetValue(twin, out var other))
                picked.Add(other);
        }
        if (picked.Count == 0)
            return SymmetryPlan.Empty;
        // Parents come before their children, so one pass finds every bone below a picked one.
        var inside = new bool[bones.Length];
        for (var b = 0; b < bones.Length; b++)
            inside[b] = picked.Contains(b) || bones[b].Parent >= 0 && inside[bones[b].Parent];
        var side = layer.From == MotionSide.Left ? 'r' : 'l';
        var pairs = ImmutableArray.CreateBuilder<SymmetryPair>();
        var unpaired = ImmutableArray.CreateBuilder<string>();
        for (var b = 0; b < bones.Length; b++)
        {
            if (!inside[b] || MotionMirror.Side(bones[b].Name) != side)
                continue;
            if (!index.TryGetValue(MotionMirror.Twin(bones[b].Name)!, out var source))
            {
                unpaired.Add(bones[b].Name);
                continue;
            }
            if (animated != null && !animated.Contains(source) && !animated.Contains(b))
                continue;
            pairs.Add(new SymmetryPair(source, b, MotionMirror.CommonAncestor(skeleton, source, b)));
        }
        return new SymmetryPlan(pairs.ToImmutable(), unpaired.ToImmutable());
    }

    /// <summary>
    /// The bones the layer at <paramref name="at"/> in <paramref name="layers"/> counts as moving, for
    /// <see cref="Plan"/>: the ones the clip has tracks for and every bone an earlier layer that is on
    /// moves (an offset's or pose's keyed bones, a symmetry's targets), since those have motion to copy
    /// and get a track when saved. Null when the clip's tracks are unknown, which counts every bone.
    /// Bones the skeleton lacks are left out; applying the layers reports them.
    /// </summary>
    public static HashSet<int>? Animated(SkeletonDescription skeleton, IReadOnlyCollection<short>? tracks, IReadOnlyList<MotionLayer> layers, int at)
    {
        if (tracks == null || tracks.Count == 0)
            return null;
        var animated = tracks.Select(track => (int)track).ToHashSet();
        var index = MotionEvaluator.Index(skeleton);
        for (var i = 0; i < Math.Min(at, layers.Count); i++)
        {
            var layer = layers[i];
            if (!MotionEvaluator.Active(layer))
                continue;
            switch (layer.Kind)
            {
                case MotionLayerKind.Offset or MotionLayerKind.Pose:
                    foreach (var key in layer.Keys)
                        foreach (var name in key.Bones.Keys)
                            if ((layer.Bones.IsEmpty || layer.Bones.Contains(name)) && index.TryGetValue(name, out var bone))
                                animated.Add(bone);
                    break;
                case MotionLayerKind.Symmetry:
                    try
                    {
                        foreach (var pair in Plan(skeleton, layer, animated).Pairs)
                            animated.Add(pair.Target);
                    }
                    catch (InvalidDataException)
                    {
                        // Applying the layers reports the missing bone.
                    }
                    break;
            }
        }
        return animated;
    }

    /// <summary>Every bone's model-space transform in the skeleton's rest pose.</summary>
    public static MotionTransform[] RestModel(SkeletonDescription skeleton) =>
        MotionEvaluator.ModelSpace(skeleton, skeleton.Bones.Select(bone => MotionTransform.From(bone.Reference)).ToArray());

    /// <summary>
    /// Makes <paramref name="plan"/>'s targets in <paramref name="local"/> (one pose, local transforms)
    /// copy their twins, mirrored, blended in by <paramref name="weight"/>: rotations along the shortest
    /// way, positions and scales in a straight line. <paramref name="components"/> are the parts copied;
    /// the others keep the target's own. The twins are read as the pose was before any change.
    /// <paramref name="scratch"/> holds the working buffers between calls with the same plan (one per
    /// sample over a clip); without one, each call makes its own.
    /// </summary>
    public static void Mirror(SkeletonDescription skeleton, MotionTransform[] local, SymmetryPlan plan, MotionTransform[] restModel,
        float weight, PoseComponents components, SymmetryScratch? scratch = null)
    {
        if (!(weight > 0) || plan.Pairs.IsEmpty)
            return;
        weight = Math.Min(1, weight);
        var rotate = components.HasFlag(PoseComponents.Rotation);
        var move = components.HasFlag(PoseComponents.Position);
        var scale = components.HasFlag(PoseComponents.Scale);
        var bones = skeleton.Bones;
        if (scratch == null || !ReferenceEquals(scratch.Plan, plan) || scratch.Snapshot.Length != bones.Length)
            scratch = new SymmetryScratch(plan, bones.Length);
        var snapshot = scratch.Snapshot;
        var full = scratch.Full;
        var original = scratch.Original;
        Array.Copy(local, original, bones.Length);
        for (var b = 0; b < bones.Length; b++)
            snapshot[b] = bones[b].Parent < 0 ? local[b] : MotionEvaluator.Compose(snapshot[bones[b].Parent], local[b]);
        // The pose at the full copy: each target's copy is taken against its parent's full copy and only
        // then blended, so at part weight every joint is part of the way to its own copy. Against the
        // blended parent instead, a joint the same on both sides would bend to make up for the blend.
        Array.Copy(snapshot, full, bones.Length);
        for (var b = plan.Pairs[0].Target; b < bones.Length; b++)
        {
            var parentIndex = bones[b].Parent;
            var parent = parentIndex >= 0 ? full[parentIndex] : MotionTransform.Identity;
            var copied = original[b];
            if (scratch.PairAt[b] is var at and >= 0)
            {
                var pair = plan.Pairs[at];
                var s = pair.Source;
                var m = pair.Middle;
                var body = m < 0 ? Quaternion.Identity
                    : Quaternion.Normalize(Quaternion.Normalize(snapshot[m].Rotation) * Quaternion.Inverse(Quaternion.Normalize(restModel[m].Rotation)));
                var change = Quaternion.Inverse(body) * Quaternion.Normalize(snapshot[s].Rotation) * Quaternion.Inverse(Quaternion.Normalize(restModel[s].Rotation));
                var rotation = Quaternion.Normalize(body * MotionMirror.Rotation(change) * Quaternion.Normalize(restModel[b].Rotation));
                var own = original[b];
                var next = own;
                if (rotate)
                    next = next with { Rotation = Quaternion.Normalize(Quaternion.Inverse(Quaternion.Normalize(parent.Rotation)) * rotation) };
                if (move)
                {
                    var position = m < 0
                        ? MotionMirror.Position(snapshot[s].Position)
                        : snapshot[m].Position + Vector3.Transform(MotionMirror.Position(Vector3.Transform(snapshot[s].Position - snapshot[m].Position,
                            Quaternion.Inverse(body))), body);
                    next = next with { Position = MotionEvaluator.Relative(parent, new MotionTransform(position, rotation, parent.Scale * own.Scale)).Position };
                }
                if (scale)
                    next = next with { Scale = original[s].Scale };
                copied = next;
                local[b] = new MotionTransform(Vector3.Lerp(own.Position, next.Position, weight),
                    MotionEvaluator.Slerp(own.Rotation, next.Rotation, weight), Vector3.Lerp(own.Scale, next.Scale, weight));
            }
            full[b] = parentIndex < 0 ? copied : MotionEvaluator.Compose(parent, copied);
        }
    }
}
