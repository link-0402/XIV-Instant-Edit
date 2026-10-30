using System.Numerics;
using InstantEdit.Models;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Services.Animations.Motion;

/// <summary>
/// How a noise layer wobbles its bones: each leans up to <paramref name="Tilt"/> degrees any way in the floor plane and
/// turns up to <paramref name="Twist"/> degrees about the up axis, and the top bone of each chain moves up to
/// <paramref name="Slide"/> metres in the floor plane, in the character's axes. <paramref name="Speed"/> is about how many
/// changes of direction a second; another <paramref name="Seed"/> gives another wobble.
/// </summary>
internal sealed record MotionNoise(float Tilt = 5, float Twist = 0, float Slide = 0, float Speed = 1, int Seed = 1);

/// <summary>
/// Noise layers: their settings checked, and their wobble played onto a pose or a whole clip. Each bone has five streams
/// (lean about X and Z, twist about Y, slide along X and Z), each a Catmull-Rom curve through random knots spread evenly
/// over the layer. The knots come from a fixed integer hash, so a layer moves the same in every session and in the save.
/// </summary>
internal static class MotionNoises
{
    /// <summary>The largest Tilt or Twist (degrees), the longest Slide (metres), the speed range (per second) and the highest seed.</summary>
    public const float MaxAngle = 90, MaxSlide = .5f, MinSpeed = .1f, MaxSpeed = 10;
    public const int MaxSeed = 9999;

    // The streams: lean about X, lean about Z, twist about Y, slide along X, slide along Z.
    private const int LeanX = 0, LeanZ = 1, Twist = 2, SlideX = 3, SlideZ = 4;

    /// <summary>Throws <see cref="InvalidDataException"/> when a setting is out of range; <paramref name="name"/> names the layer.</summary>
    internal static void Validate(MotionNoise noise, string name)
    {
        if (!In(noise.Tilt, 0, MaxAngle) || !In(noise.Twist, 0, MaxAngle) || !In(noise.Slide, 0, MaxSlide) || !In(noise.Speed, MinSpeed, MaxSpeed) ||
            noise.Seed is < 1 or > MaxSeed)
            throw new InvalidDataException($"{name} has a noise out of range.");
    }

    private static bool In(float value, float min, float max) => float.IsFinite(value) && value >= min && value <= max;

    /// <summary>
    /// A random value from -1 to 1 for knot <paramref name="knot"/> of <paramref name="stream"/> on <paramref name="bone"/>:
    /// FNV-1a over the name's UTF-16 units, then murmur3's finalizer mixing in the seed, the stream and the knot. Never
    /// string.GetHashCode (random per process) or System.Random (its sequence isn't promised across runtimes).
    /// </summary>
    public static float Random(int seed, string bone, int stream, int knot)
    {
        unchecked
        {
            var h = 2166136261u;
            foreach (var c in bone)
            {
                h ^= c;
                h *= 16777619u;
            }
            h = Mix(h ^ (uint)seed * 0x9E3779B9u);
            h = Mix(h ^ (uint)stream * 0x85EBCA6Bu);
            h = Mix(h ^ (uint)knot * 0xC2B2AE35u);
            return (h >> 8) / 16777215f * 2 - 1;
        }
    }

    private static uint Mix(uint h)
    {
        unchecked
        {
            h ^= h >> 16;
            h *= 0x85EBCA6Bu;
            h ^= h >> 13;
            h *= 0xC2B2AE35u;
            h ^= h >> 16;
            return h;
        }
    }

    /// <summary>Another seed than <paramref name="seed"/>, from 1 to <see cref="MaxSeed"/>, for the panel's New button.</summary>
    public static int NewSeed(int seed)
    {
        var next = (int)(Mix(unchecked((uint)seed * 0x9E3779B9u + 1)) % (MaxSeed - 1)) + 1;
        // Skipping the old seed keeps the pick in range and never the same.
        return next >= seed ? next + 1 : next;
    }

    /// <summary>
    /// <paramref name="bone"/>'s <paramref name="stream"/> at <paramref name="frame"/>, about -1 to 1 (the curve may pass the
    /// knots a little; callers bound it). On a loop the layer covers (<see cref="MotionEvaluator.Wraps"/>) the knots go round
    /// the loop, at least two, so the seam is clean; otherwise a 0 knot at each end holds the bone at rest there, and the curve
    /// leaves and reaches it without a jolt. A layer reaching the end of a motion <paramref name="end"/> frames long
    /// (<see cref="MotionEvaluator.ReachesEnd"/>) comes to rest with the motion.
    /// </summary>
    public static float Value(MotionLayer layer, string bone, int stream, float frame, float loop, float end = 0)
    {
        var noise = layer.Noise ?? new MotionNoise();
        if (MotionEvaluator.Wraps(layer, loop))
        {
            var count = Math.Max(2, (int)MathF.Round(noise.Speed * loop / TimelineDocument.Fps));
            var u = Math.Clamp(frame / loop, 0, 1) * count;
            var k = Math.Min((int)MathF.Floor(u), count);
            float Round(int i) => Random(noise.Seed, bone, stream, (i % count + count) % count);
            return CatmullRom(Round(k - 1), Round(k), Round(k + 1), Round(k + 2), u - k);
        }
        var start = layer.Start;
        var stop = MotionEvaluator.ReachesEnd(layer, end) ? Math.Min(layer.End, end) : layer.End;
        var span = stop - start;
        if (frame <= start || frame >= stop || span <= 0)
            return 0;
        var inner = Math.Max(1, (int)MathF.Round(noise.Speed * span / TimelineDocument.Fps));
        var segments = inner + 1;
        var at = (frame - start) / span * segments;
        var knot = Math.Min((int)MathF.Floor(at), segments - 1);
        // Knots 0 and segments are the rests; ghosts past them mirror the knot inside, so the curve is flat at rest.
        float Knot(int i) => i < 0 ? Knot(-i) : i > segments ? Knot(2 * segments - i) : i == 0 || i == segments ? 0 : Random(noise.Seed, bone, stream, i - 1);
        return CatmullRom(Knot(knot - 1), Knot(knot), Knot(knot + 1), Knot(knot + 2), at - knot);
    }

    /// <summary>The uniform Catmull-Rom curve from <paramref name="p1"/> to <paramref name="p2"/> at <paramref name="t"/>.</summary>
    private static float CatmullRom(float p0, float p1, float p2, float p3, float t) =>
        .5f * (2 * p1 + (p2 - p0) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t * t + (3 * p1 - p0 - 3 * p2 + p3) * t * t * t);

    /// <summary>The layer's bones in skeleton order, parents first, and whether each slides (no picked bone above it). Throws for a bone the skeleton lacks.</summary>
    public static NoiseBone[] Plan(SkeletonDescription skeleton, MotionLayer layer, Dictionary<string, int> index)
    {
        var bones = skeleton.Bones;
        var picked = new HashSet<int>();
        foreach (var name in layer.Bones)
            picked.Add(index.TryGetValue(name, out var bone) ? bone : throw MotionEvaluator.Missing(name));
        return
        [
            .. picked.Order().Select(b =>
            {
                var top = true;
                for (var parent = (int)bones[b].Parent; parent >= 0 && top; parent = bones[parent].Parent)
                    top = !picked.Contains(parent);
                return new NoiseBone(b, bones[b].Name, top);
            }),
        ];
    }

    /// <summary>
    /// Wobbles <paramref name="pose"/>'s planned bones (local transforms of the whole skeleton) as the layer has them at
    /// <paramref name="frame"/>, at <paramref name="weight"/> (the layer's amount through its fades and weight), in a motion
    /// <paramref name="end"/> frames long (0 when unknown).
    /// </summary>
    public static void ApplyAt(SkeletonDescription skeleton, MotionTransform[] pose, MotionLayer layer, ReadOnlySpan<NoiseBone> plan, float frame,
        float loop, float weight, float end = 0)
    {
        if (plan.IsEmpty || weight <= 0)
            return;
        Wobble(skeleton, pose, new Quaternion[plan[^1].Bone + 1], layer, plan, frame, loop, end, weight);
    }

    /// <summary>A noise layer over the whole clip, sample by sample; only the bones it wobbles are written.</summary>
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
            Wobble(clip.Skeleton, pose, model, layer, plan, frame, loop, end, weight);
            foreach (var bone in plan)
                clip.Set(i, bone.Bone, pose[bone.Bone]);
        }
    }

    /// <summary>
    /// Turns each planned bone in the character's axes, so its children follow, and slides the top bones in character
    /// space. Model rotations are kept in <paramref name="model"/> up to the last planned bone, parents before children.
    /// </summary>
    private static void Wobble(SkeletonDescription skeleton, MotionTransform[] pose, Quaternion[] model, MotionLayer layer, ReadOnlySpan<NoiseBone> plan,
        float frame, float loop, float end, float weight)
    {
        var noise = layer.Noise!;
        var rotate = layer.Components.HasFlag(PoseComponents.Rotation) && (noise.Tilt > 0 || noise.Twist > 0);
        var move = layer.Components.HasFlag(PoseComponents.Position) && noise.Slide > 0;
        var next = 0;
        for (var b = 0; b < model.Length; b++)
        {
            var parent = skeleton.Bones[b].Parent;
            var parentModel = parent >= 0 ? model[parent] : Quaternion.Identity;
            if (next < plan.Length && plan[next].Bone == b)
            {
                var bone = plan[next++];
                var local = pose[b];
                if (rotate)
                {
                    // The lean is a way in the floor plane, at most 1 long, so it never passes Tilt whichever way it goes.
                    var lean = noise.Tilt > 0 ? Disc(Value(layer, bone.Name, LeanX, frame, loop, end), Value(layer, bone.Name, LeanZ, frame, loop, end)) : Vector2.Zero;
                    var twist = noise.Twist > 0 ? Math.Clamp(Value(layer, bone.Name, Twist, frame, loop, end), -1, 1) : 0;
                    var size = lean.Length();
                    var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, twist * noise.Twist * weight * MathF.PI / 180);
                    if (size > 1e-7f)
                        turn *= Quaternion.CreateFromAxisAngle(new Vector3(lean.X / size, 0, lean.Y / size), size * noise.Tilt * weight * MathF.PI / 180);
                    local = local with { Rotation = Quaternion.Normalize(Quaternion.Inverse(parentModel) * turn * parentModel * local.Rotation) };
                }
                // The move is in character space; in the parent's space it turns with the parent.
                if (move && bone.Slides)
                {
                    var slide = Disc(Value(layer, bone.Name, SlideX, frame, loop, end), Value(layer, bone.Name, SlideZ, frame, loop, end)) * (noise.Slide * weight);
                    local = local with { Position = local.Position + Vector3.Transform(new Vector3(slide.X, 0, slide.Y), Quaternion.Inverse(parentModel)) };
                }
                pose[b] = local;
            }
            model[b] = parent >= 0 ? Quaternion.Normalize(parentModel * pose[b].Rotation) : Quaternion.Normalize(pose[b].Rotation);
        }
    }

    /// <summary>Two streams as a way in the floor plane, no longer than 1.</summary>
    private static Vector2 Disc(float x, float z)
    {
        var v = new Vector2(x, z);
        var length = v.Length();
        return length > 1 ? v / length : v;
    }
}

/// <summary>One bone a noise layer wobbles: its index, its name (the hash's input) and whether it slides (the top bone of its chain).</summary>
internal readonly record struct NoiseBone(int Bone, string Name, bool Slides);
