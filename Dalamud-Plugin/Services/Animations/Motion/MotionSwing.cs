using System.Numerics;
using InstantEdit.Models;

namespace InstantEdit.Services.Animations.Motion;

/// <summary>
/// How a swing layer swings its bones: <paramref name="Count"/> full swings back and forth over the layer, toward
/// <paramref name="Direction"/> degrees in the floor plane (0 side to side, 90 front to back) and back the other way.
/// <paramref name="Tilt"/> leans each bone toward the direction, <paramref name="Twist"/> turns it about the up axis
/// (both in degrees) and <paramref name="Slide"/> moves the top bone of each chain along it (metres), in the character's
/// axes. A bone under other swung bones trails them by <paramref name="Delay"/> frames each; <paramref name="Shape"/>
/// from 0 (a sine) to 1 lingers longer at each end. With <paramref name="TwinsOpposite"/>, the right bone of a swung
/// left and right pair swings half a swing behind; with <paramref name="Spread"/>, a chain shares Tilt and Twist out.
/// </summary>
internal sealed record MotionSwing(int Count = 2, float Direction = 0, float Tilt = 10, float Twist = 0, float Slide = 0,
    float Delay = 3, float Shape = 0, bool TwinsOpposite = true, bool Spread = true);

/// <summary>Swing layers: their settings checked, and their motion played onto a pose or a whole clip.</summary>
internal static class MotionSwings
{
    /// <summary>The most swings, the largest Tilt or Twist (degrees), the longest Slide (metres) and Delay (frames).</summary>
    public const int MaxCount = 64;
    public const float MaxAngle = 90, MaxSlide = .5f, MaxDelay = 30;

    /// <summary>Throws <see cref="InvalidDataException"/> when a setting is out of range; <paramref name="name"/> names the layer.</summary>
    internal static void Validate(MotionSwing swing, string name)
    {
        if (swing.Count is < 1 or > MaxCount || !In(swing.Direction, -180, 180) || !In(swing.Tilt, 0, MaxAngle) || !In(swing.Twist, 0, MaxAngle) ||
            !In(swing.Slide, 0, MaxSlide) || !In(swing.Delay, 0, MaxDelay) || !In(swing.Shape, 0, 1))
            throw new InvalidDataException($"{name} has a swing out of range.");
    }

    private static bool In(float value, float min, float max) => float.IsFinite(value) && value >= min && value <= max;

    /// <summary>
    /// The layer's bones in skeleton order, parents first: how many swung bones are above each (<see cref="SwingBone.Depth"/>),
    /// its share of Tilt and Twist, whether it slides and which way it swings. Throws for a bone the skeleton lacks.
    /// </summary>
    public static SwingBone[] Plan(SkeletonDescription skeleton, MotionLayer layer, Dictionary<string, int> index)
    {
        var swing = layer.Swing ?? new MotionSwing();
        var bones = skeleton.Bones;
        var picked = new HashSet<int>();
        foreach (var name in layer.Bones)
            picked.Add(index.TryGetValue(name, out var bone) ? bone : throw MotionEvaluator.Missing(name));
        // Each swung bone's nearest swung bone above it, and how many swung bones lie above and, at most, below it.
        var above = new Dictionary<int, int>();
        var up = new Dictionary<int, int>();
        var down = picked.ToDictionary(b => b, _ => 0);
        foreach (var b in picked)
        {
            var count = 0;
            for (var parent = (int)bones[b].Parent; parent >= 0; parent = bones[parent].Parent)
                if (picked.Contains(parent))
                {
                    if (count++ == 0)
                        above[b] = parent;
                }
            up[b] = count;
        }
        // Children come after their parents, so the deepest runs reach the top last to first.
        foreach (var b in picked.OrderByDescending(b => b))
            if (above.TryGetValue(b, out var parent))
                down[parent] = Math.Max(down[parent], down[b] + 1);
        return
        [
            .. picked.Order().Select(b =>
            {
                var name = bones[b].Name;
                var twin = MotionMirror.Twin(name);
                var opposite = swing.TwinsOpposite && MotionMirror.Side(name) == 'r' && twin != null && index.TryGetValue(twin, out var other) && picked.Contains(other);
                return new SwingBone(b, up[b], swing.Spread ? 1f / (1 + up[b] + down[b]) : 1, up[b] == 0, opposite ? -1 : 1);
            }),
        ];
    }

    /// <summary>
    /// The wave at <paramref name="phase"/> swings in: a sine from 0 up to 1, back through 0 to -1 and home, its ends held
    /// longer as <paramref name="shape"/> rises to 1. The shaped curve leaves 0 at most 1 + 3 × shape times as fast as the
    /// sine, so it never snaps out of rest. Whole swings are taken off first, so a phase of a whole number is exactly 0.
    /// </summary>
    public static float Wave(float phase, float shape)
    {
        var x = MathF.Sin(2 * MathF.PI * (phase - MathF.Floor(phase)));
        return shape <= 0 ? x : MathF.Sign(x) * (1 - MathF.Pow(1 - MathF.Abs(x), 1 + 3 * shape));
    }

    /// <summary>
    /// Where a bone <paramref name="depth"/> swung bones down is in its swing at <paramref name="frame"/>, from -1 to 1. On a
    /// loop the layer covers (<see cref="MotionEvaluator.Wraps"/>) the swings fit the loop and a trailing bone runs behind by its
    /// delay, round the seam; otherwise it starts that much later and fits its swings in before the layer ends, or before a
    /// motion <paramref name="end"/> frames long ends when the layer reaches it (<see cref="MotionEvaluator.ReachesEnd"/>), so
    /// every bone starts and ends at rest. A delay never takes more than half the layer.
    /// </summary>
    public static float Value(MotionLayer layer, int depth, float frame, float loop, float end = 0)
    {
        var swing = layer.Swing ?? new MotionSwing();
        var shift = Math.Min(depth * swing.Delay, (layer.End - layer.Start) / 2f);
        if (MotionEvaluator.Wraps(layer, loop))
            return Wave(swing.Count * (frame - shift) / loop, swing.Shape);
        var start = layer.Start + shift;
        // A layer may end a frame or a fraction past the motion it reaches; its last swing then ends with the motion.
        var stop = MotionEvaluator.ReachesEnd(layer, end) ? Math.Min(layer.End, end) : layer.End;
        var span = stop - start;
        if (frame <= start || span <= 0)
            return 0;
        return Wave(swing.Count * Math.Min(frame - start, span) / span, swing.Shape);
    }

    /// <summary>
    /// Swings <paramref name="pose"/>'s planned bones (local transforms of the whole skeleton) as the layer has them at
    /// <paramref name="frame"/>, at <paramref name="weight"/> (the layer's amount through its fades), in a motion
    /// <paramref name="end"/> frames long (0 when unknown).
    /// </summary>
    public static void ApplyAt(SkeletonDescription skeleton, MotionTransform[] pose, MotionLayer layer, ReadOnlySpan<SwingBone> plan, float frame,
        float loop, float weight, float end = 0)
    {
        if (plan.IsEmpty || weight <= 0)
            return;
        Swing(skeleton, pose, new Quaternion[plan[^1].Bone + 1], layer, plan, frame, loop, end, weight);
    }

    /// <summary>A swing layer over the whole clip, sample by sample; only the bones it swings are written.</summary>
    public static void Apply(MotionClip clip, MotionLayer layer, Dictionary<string, int> index, float loop, float end, CancellationToken token)
    {
        var plan = Plan(clip.Skeleton, layer, index);
        if (plan.Length == 0)
            return;
        var last = plan[^1].Bone;
        var pose = new MotionTransform[last + 1];
        var model = new Quaternion[last + 1];
        for (var i = 0; i < clip.Samples; i++)
        {
            if ((i & 63) == 0)
                token.ThrowIfCancellationRequested();
            var frame = clip.Frame(i);
            var weight = MotionEvaluator.Weight(layer, frame, end);
            if (weight <= 0)
                continue;
            for (var b = 0; b <= last; b++)
                pose[b] = clip.Get(i, b);
            Swing(clip.Skeleton, pose, model, layer, plan, frame, loop, end, weight);
            foreach (var bone in plan)
                clip.Set(i, bone.Bone, pose[bone.Bone]);
        }
    }

    /// <summary>
    /// Turns each planned bone in the character's axes, so its children follow, and slides the top bones in character
    /// space. Model rotations are kept in <paramref name="model"/> up to the last planned bone, parents before children.
    /// </summary>
    private static void Swing(SkeletonDescription skeleton, MotionTransform[] pose, Quaternion[] model, MotionLayer layer, ReadOnlySpan<SwingBone> plan,
        float frame, float loop, float end, float weight)
    {
        var swing = layer.Swing!;
        var rotate = layer.Components.HasFlag(PoseComponents.Rotation);
        var move = layer.Components.HasFlag(PoseComponents.Position);
        var radians = swing.Direction * MathF.PI / 180;
        var direction = new Vector3(MathF.Cos(radians), 0, MathF.Sin(radians));
        // Leaning toward the direction turns about the floor axis across it.
        var across = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, direction));
        var next = 0;
        for (var b = 0; b < model.Length; b++)
        {
            var parent = skeleton.Bones[b].Parent;
            var parentModel = parent >= 0 ? model[parent] : Quaternion.Identity;
            if (next < plan.Length && plan[next].Bone == b)
            {
                var bone = plan[next++];
                var w = Value(layer, bone.Depth, frame, loop, end) * bone.Sign * weight;
                var local = pose[b];
                var tilt = w * swing.Tilt * bone.Share * MathF.PI / 180;
                var twist = w * swing.Twist * bone.Share * MathF.PI / 180;
                if (rotate && (tilt != 0 || twist != 0))
                {
                    var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, twist) * Quaternion.CreateFromAxisAngle(across, tilt);
                    local = local with { Rotation = Quaternion.Normalize(Quaternion.Inverse(parentModel) * turn * parentModel * local.Rotation) };
                }
                // The move is in character space; in the parent's space it turns with the parent.
                if (move && bone.Slides && swing.Slide > 0 && w != 0)
                    local = local with { Position = local.Position + Vector3.Transform(direction * (swing.Slide * w), Quaternion.Inverse(parentModel)) };
                pose[b] = local;
            }
            model[b] = parent >= 0 ? Quaternion.Normalize(parentModel * pose[b].Rotation) : Quaternion.Normalize(pose[b].Rotation);
        }
    }
}

/// <summary>
/// One bone a swing layer swings: its index, how many swung bones are above it, its share of Tilt and Twist, whether it
/// slides (the top bone of its chain) and its way, -1 for the right twin of a pair swinging opposite.
/// </summary>
internal readonly record struct SwingBone(int Bone, int Depth, float Share, bool Slides, float Sign);
