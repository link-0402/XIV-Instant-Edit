using System.Collections.Immutable;
using System.Numerics;
using InstantEdit.Models;

namespace InstantEdit.Services.Animations.Motion;

/// <summary>
/// What a look-at layer looks at: <paramref name="Follow"/>, a bone followed each frame, or null for the spots its keys
/// hold in character space. <paramref name="Limit"/> is the most, in degrees, the chain turns the head from the motion's.
/// </summary>
internal sealed record MotionLook(string? Follow = null, float Limit = 80);

/// <summary>
/// Look-at layers: their settings checked, and the head turned toward the target on a pose or a whole clip. The head's
/// forward is measured from the skeleton's rest: the game's head (j_kao, rest model rotation (0.7071, 0, 0.7071, 0) on
/// c0101, c0201, c0401, c0601 and c0901) looks along its own +X, -Y up, while the character faces +Z. The turn is the
/// smallest one taking that forward toward the target, so the head keeps its tilt and roll, eased into the limit and
/// shared over the picked bones from the upper back up, each turned in character space about its own joint.
/// </summary>
internal static class MotionLooks
{
    /// <summary>The largest limit (degrees) and the longest followed bone name.</summary>
    public const float MaxLimit = 120;
    public const int MaxFollowName = 64;
    /// <summary>The bone a look's keys hold their spots under: the head.</summary>
    public const string KeyBone = PinChains.Head;
    /// <summary>How far ahead of the head (metres) a look with no keys yet starts its target.</summary>
    public const float Ahead = 1;

    /// <summary>The bones a look may turn, head first: head, neck and upper back.</summary>
    public static readonly ImmutableArray<string> Chain = [KeyBone, "j_kubi", "j_sebo_c"];
    private static readonly float[] Shares = [.60f, .25f, .15f];

    // The share of the limit over which the turn eases into it, and how often a followed bone riding the turn is read again.
    private const float SoftShare = .25f;
    private const int FollowPasses = 3;
    private const float Tiny = 1e-6f, Degree = MathF.PI / 180;

    /// <summary>Throws <see cref="InvalidDataException"/> when a setting is out of range; <paramref name="name"/> names the layer.</summary>
    internal static void Validate(MotionLook look, string name)
    {
        if (!float.IsFinite(look.Limit) || look.Limit is < 0 or > MaxLimit)
            throw new InvalidDataException($"{name} has a look limit out of range.");
        if (look.Follow is { } follow && (follow.Length == 0 || follow.Length > MaxFollowName))
            throw new InvalidDataException($"{name} follows a bone that can't be read.");
    }

    /// <summary>A bone's share of the turn before the picked bones' shares are made to add up to 1; 0 off the chain.</summary>
    public static float Share(string bone) => Chain.IndexOf(bone) is var at and >= 0 ? Shares[at] : 0;

    /// <summary>The head's forward and up in its own space: the directions that point along the character's forward (+Z) and up (+Y) at rest.</summary>
    public static (Vector3 Forward, Vector3 Up) Axes(SkeletonDescription skeleton, int head)
    {
        var rest = Quaternion.Identity;
        for (var at = head; at >= 0; at = skeleton.Bones[at].Parent)
            rest = Quaternion.Normalize(skeleton.Bones[at].Reference.Rotation) * rest;
        var inverse = Quaternion.Inverse(Quaternion.Normalize(rest));
        return (Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, inverse)), Vector3.Normalize(Vector3.Transform(Vector3.UnitY, inverse)));
    }

    /// <summary>
    /// The layer resolved on <paramref name="skeleton"/>: its picked bones the head hangs from (or the head), bottom first,
    /// with their shares made to add up to 1, and its followed bone. Null when the skeleton has no head or no picked bone
    /// turns it. Throws for a bone the skeleton lacks, and for a followed bone that turns with the head: one of the chain's,
    /// or one hanging from the head.
    /// </summary>
    public static LookPlan? Plan(SkeletonDescription skeleton, MotionLayer layer, Dictionary<string, int> index)
    {
        var picked = layer.Bones.Select(name => index.TryGetValue(name, out var at) ? at : throw MotionEvaluator.Missing(name)).ToArray();
        var follow = -1;
        if (layer.Look?.Follow is { } followed)
            follow = index.TryGetValue(followed, out var at) ? at : throw MotionEvaluator.Missing(followed);
        if (!index.TryGetValue(KeyBone, out var head))
            return null;
        if (follow >= 0 && !CanFollow(skeleton, follow, head))
        {
            var name = string.IsNullOrWhiteSpace(layer.Name) ? "A bone layer" : $"The bone layer {layer.Name}";
            throw new InvalidDataException($"{name} follows {AnimationPresentation.BoneName(skeleton.Bones[follow].Name)}, which turns with the head.");
        }
        int[] chain = [.. picked.Where(b => b == head || PinChains.IsBelow(skeleton, head, b)).OrderBy(b => Depth(skeleton, b))];
        var total = chain.Sum(b => Share(skeleton.Bones[b].Name));
        if (chain.Length == 0 || total <= 0)
            return null;
        var (forward, up) = Axes(skeleton, head);
        return new LookPlan(head, forward, up, [.. chain.Select(b => new LookBone(b, Share(skeleton.Bones[b].Name) / total))], follow,
            follow >= 0 && chain.Any(b => PinChains.IsBelow(skeleton, follow, b)));
    }

    /// <summary>Whether a look may follow <paramref name="bone"/>: not one of the chain's, nor one hanging from the head (<paramref name="head"/>), which turn with it.</summary>
    public static bool CanFollow(SkeletonDescription skeleton, int bone, int head) =>
        !Chain.Contains(skeleton.Bones[bone].Name) && bone != head && (head < 0 || !PinChains.IsBelow(skeleton, bone, head));

    private static int Depth(SkeletonDescription skeleton, int bone)
    {
        var depth = 0;
        for (var at = (int)skeleton.Bones[bone].Parent; at >= 0; at = skeleton.Bones[at].Parent)
            depth++;
        return depth;
    }

    /// <summary>Where the head looks in character space on a pose's model transforms.</summary>
    public static Vector3 Facing(LookPlan plan, ReadOnlySpan<MotionTransform> model) =>
        Vector3.Normalize(Vector3.Transform(plan.Forward, Quaternion.Normalize(model[plan.Head].Rotation)));

    /// <summary>Where a look with no keys yet starts its target: <see cref="Ahead"/> in front of the head (<paramref name="head"/>) on a pose's model transforms.</summary>
    public static Vector3 DefaultTarget(SkeletonDescription skeleton, int head, ReadOnlySpan<MotionTransform> model) =>
        model[head].Position + Vector3.Normalize(Vector3.Transform(Axes(skeleton, head).Forward, Quaternion.Normalize(model[head].Rotation))) * Ahead;

    /// <summary>The spot the layer's keys hold at <paramref name="frame"/>, in character space; null without keys.</summary>
    public static Vector3? KeyedTarget(MotionLayer layer, float frame, float loop = 0) => MotionEvaluator.KeyValue(layer, KeyBone, frame, loop)?.Position;

    /// <summary>
    /// Turns the plan's bones of a pose so the head looks toward <paramref name="target"/> (character space): the smallest
    /// turn of its forward, eased into <paramref name="limit"/> degrees, times <paramref name="weight"/>. Each bone, bottom
    /// first, takes its share of what is left from where the ones below it leave the head; the last one closes to the angle
    /// wanted, never turning the head past the limit. A target straight behind turns about the character's up, toward the
    /// side the head already faces. <paramref name="local"/> and <paramref name="model"/> are changed in place and kept in step.
    /// </summary>
    public static void Turn(SkeletonDescription skeleton, MotionTransform[] local, MotionTransform[] model, LookPlan plan, Vector3 target, float limit,
        float weight)
    {
        if (!(weight > 0) || !(limit > 0) || plan.Bones.IsEmpty || !float.IsFinite(target.X) || !float.IsFinite(target.Y) || !float.IsFinite(target.Z))
            return;
        weight = Math.Min(weight, 1);
        limit *= Degree;
        var start = Facing(plan, model);
        var to = target - model[plan.Head].Position;
        if (to.Length() < Tiny)
            return;
        var angle = Angle(start, to);
        var wanted = PinSolver.SoftDistance(angle, limit, limit * SoftShare) * weight;
        if (wanted < Tiny)
            return;
        // The angle to the target once turned.
        var goal = angle - wanted;
        var side = Side(start, plan, model);
        var left = 1f;
        for (var i = 0; i < plan.Bones.Length; i++)
        {
            var bone = plan.Bones[i];
            var facing = Facing(plan, model);
            var toward = target - model[plan.Head].Position;
            var step = Angle(facing, toward) - goal;
            if (i < plan.Bones.Length - 1)
                step *= bone.Share / left;
            else
                step = Math.Min(step, limit - Angle(start, facing));
            left -= bone.Share;
            if (!(step > Tiny))
                continue;
            var axis = Vector3.Cross(facing, toward);
            axis = axis.Length() > 1e-4f * toward.Length() ? Vector3.Normalize(axis) : side;
            PinSolver.HoldTurn(skeleton, local, model, bone.Bone,
                Quaternion.Normalize(Quaternion.CreateFromAxisAngle(axis, step) * Quaternion.Normalize(model[bone.Bone].Rotation)));
        }
    }

    /// <summary>
    /// The axis a target straight behind turns about: the character's up, across the head's forward, turning it further
    /// toward the side it already faces (+X when it faces straight ahead). The head's own up when it looks straight up or down.
    /// </summary>
    private static Vector3 Side(Vector3 facing, LookPlan plan, ReadOnlySpan<MotionTransform> model)
    {
        var up = Vector3.UnitY - Vector3.Dot(Vector3.UnitY, facing) * facing;
        if (up.Length() < 1e-3f)
        {
            up = Vector3.Transform(plan.Up, Quaternion.Normalize(model[plan.Head].Rotation));
            up -= Vector3.Dot(up, facing) * facing;
        }
        up = Vector3.Normalize(up);
        // A turn about +Y takes +Z toward +X.
        return facing.X < 0 ? -up : up;
    }

    private static float Angle(Vector3 a, Vector3 b) => MathF.Atan2(Vector3.Cross(a, b).Length(), Vector3.Dot(a, b));

    /// <summary>
    /// A look on one pose (local transforms of the whole skeleton) at <paramref name="frame"/>, at <paramref name="weight"/>
    /// (the layer's amount through its fades and weight): toward its followed bone where the pose has it, else toward its
    /// keys' spot (<paramref name="track"/>, round a loop <paramref name="wrap"/> frames long when the layer wraps it).
    /// </summary>
    public static void ApplyAt(SkeletonDescription skeleton, MotionTransform[] pose, MotionLayer layer, LookPlan? plan, MotionEvaluator.Track? track,
        float frame, float wrap, float weight)
    {
        if (plan == null || weight <= 0 || plan.Follow < 0 && track == null)
            return;
        Look(skeleton, pose, MotionEvaluator.ModelSpace(skeleton, pose), layer, plan, track, frame, wrap, weight);
    }

    /// <summary>A look over the whole clip, sample by sample; only the bones it turns are written.</summary>
    public static void Apply(MotionClip clip, MotionLayer layer, Dictionary<string, int> index, MotionEvaluator.Track? track, float wrap, float end,
        CancellationToken token)
    {
        if (Plan(clip.Skeleton, layer, index) is not { } plan || plan.Follow < 0 && track == null)
            return;
        var local = new MotionTransform[clip.Bones];
        for (var i = 0; i < clip.Samples; i++)
        {
            if ((i & 63) == 0)
                token.ThrowIfCancellationRequested();
            var frame = clip.Frame(i);
            var weight = MotionEvaluator.Weight(layer, frame, end);
            if (weight <= 0)
                continue;
            for (var b = 0; b < local.Length; b++)
                local[b] = clip.Get(i, b);
            Look(clip.Skeleton, local, MotionEvaluator.ModelSpace(clip.Skeleton, local), layer, plan, track, frame, wrap, weight);
            foreach (var bone in plan.Bones)
                clip.Set(i, bone.Bone, local[bone.Bone]);
        }
    }

    /// <summary>
    /// Turns the pose toward the layer's target. A followed bone that hangs from a bone the turn moves (a hand under the upper
    /// back) moves with it: the pose is turned again from where it was, toward where the last turn left that bone.
    /// </summary>
    private static void Look(SkeletonDescription skeleton, MotionTransform[] local, MotionTransform[] model, MotionLayer layer, LookPlan plan,
        MotionEvaluator.Track? track, float frame, float wrap, float weight)
    {
        var limit = layer.Look?.Limit ?? 0;
        if (plan.Follow < 0)
        {
            Turn(skeleton, local, model, plan, MotionEvaluator.Sample(track!.Frames, track.Values, track.Curves, frame, wrap).Position, limit, weight);
            return;
        }
        var target = model[plan.Follow].Position;
        if (!plan.Rides)
        {
            Turn(skeleton, local, model, plan, target, limit, weight);
            return;
        }
        var startLocal = local.ToArray();
        var startModel = model.ToArray();
        for (var pass = 0; pass < FollowPasses; pass++)
        {
            if (pass > 0)
            {
                startLocal.CopyTo(local, 0);
                startModel.CopyTo(model, 0);
            }
            Turn(skeleton, local, model, plan, target, limit, weight);
            target = model[plan.Follow].Position;
        }
    }
}

/// <summary>
/// A look-at layer resolved on a skeleton: the head, its forward and up in its own space, the bones that turn bottom first
/// with their shares, the followed bone (-1 for keyed spots) and whether that bone hangs from a bone that turns.
/// </summary>
internal sealed record LookPlan(int Head, Vector3 Forward, Vector3 Up, ImmutableArray<LookBone> Bones, int Follow, bool Rides);

/// <summary>One bone a look turns and its share of the turn.</summary>
internal readonly record struct LookBone(int Bone, float Share);
