using System.Collections.Immutable;
using System.Numerics;
using InstantEdit.Models;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Services.Animations.Motion;

/// <summary>The body parts an idle-motion layer scales on their own.</summary>
internal enum IdleGroup : byte
{
    /// <summary>Not borrowed: roots, physics, gear, the face, anything unknown.</summary>
    None,
    /// <summary>Back, chest, neck and head: the breathing.</summary>
    Spine,
    /// <summary>Shoulders, arms and fingers.</summary>
    Arms,
    /// <summary>The body's slide (n_hara), the hips and the legs: one weight shift, which only means anything as a whole.</summary>
    Balance,
    /// <summary>The tail.</summary>
    Tail,
}

/// <summary>
/// What an idle-motion layer borrows from another clip (Pose2Idle's way of making a still pose live):
/// each bone's turn away from that clip's first frame, sampled <paramref name="Samples"/> times over
/// <paramref name="Duration"/> seconds, as x, y, z, w per sample (<paramref name="Turns"/>), and the body's
/// slide away from where it starts, as x, y, z per sample (<paramref name="Slide"/>, empty when it holds
/// still). Each group's share is how much of its motion applies: 1 as borrowed, 0 none, above 1 more.
/// </summary>
internal sealed record MotionIdle(string Donor, float Duration, int Samples, ImmutableDictionary<string, ImmutableArray<float>> Turns,
    ImmutableArray<float> Slide, float Spine = MotionIdles.DefaultSpine, float Arms = MotionIdles.DefaultArms,
    float Balance = MotionIdles.DefaultBalance, float Tail = MotionIdles.DefaultTail)
{
    public ImmutableArray<float> Slide { get; init; } = Slide.IsDefault ? [] : Slide;
}

/// <summary>
/// Idle motion borrowed from a clip: made from a decoded clip, checked, and played onto a pose. The
/// borrowed motion plays a whole number of times over the layer, each time stretched a little to fit, so
/// a loop the layer covers starts over seamlessly and the motion keeps close to its own pace.
/// </summary>
internal static class MotionIdles
{
    /// <summary>Pose2Idle's "Natural": a gentle breath, still arms, the body's weight shift and a moving tail.</summary>
    public const float DefaultSpine = .55f, DefaultArms = 0, DefaultBalance = 1, DefaultTail = 1;
    /// <summary>The most a group's share may be: twice the motion, five times for a tail, whose swing is the point.</summary>
    public const float MaxShare = 2, MaxTailShare = 5;
    // A bone turning less than this (degrees) over the whole clip isn't borrowed.
    private const float StillAngle = .05f;
    // The body sliding less than this (metres) isn't borrowed.
    private const float StillSlide = 1e-4f;
    /// <summary>The most samples a borrowed motion keeps: two minutes at 30 a second.</summary>
    public const int MaxSamples = 3600;

    /// <summary>Which group <paramref name="bone"/> follows; unknown bones follow none and are left alone.</summary>
    public static IdleGroup Group(string bone)
    {
        if (bone.StartsWith("n_sippo", StringComparison.Ordinal) || bone.StartsWith("j_sippo", StringComparison.Ordinal))
            return IdleGroup.Tail;
        if (bone is MotionHold.Body or "j_kosi" || bone.StartsWith("j_asi", StringComparison.Ordinal))
            return IdleGroup.Balance;
        if (bone.StartsWith("j_sebo", StringComparison.Ordinal) || bone.StartsWith("j_kubi", StringComparison.Ordinal) ||
            bone.StartsWith("j_kao", StringComparison.Ordinal) || bone.StartsWith("j_mune", StringComparison.Ordinal))
            return IdleGroup.Spine;
        if (bone.StartsWith("j_sako", StringComparison.Ordinal) || bone.StartsWith("j_ude", StringComparison.Ordinal) ||
            bone.StartsWith("j_te", StringComparison.Ordinal) || bone.StartsWith("j_hito", StringComparison.Ordinal) ||
            bone.StartsWith("j_ko_", StringComparison.Ordinal) || bone.StartsWith("j_kusu", StringComparison.Ordinal) ||
            bone.StartsWith("j_naka", StringComparison.Ordinal) || bone.StartsWith("j_oya", StringComparison.Ordinal))
            return IdleGroup.Arms;
        return IdleGroup.None;
    }

    /// <summary>A group's share in <paramref name="idle"/>.</summary>
    public static float Share(MotionIdle idle, IdleGroup group) => group switch
    {
        IdleGroup.Spine => idle.Spine,
        IdleGroup.Arms => idle.Arms,
        IdleGroup.Balance => idle.Balance,
        IdleGroup.Tail => idle.Tail,
        _ => 0,
    };

    /// <summary><paramref name="idle"/> with <paramref name="group"/>'s share set.</summary>
    public static MotionIdle WithShare(MotionIdle idle, IdleGroup group, float share) => group switch
    {
        IdleGroup.Spine => idle with { Spine = share },
        IdleGroup.Arms => idle with { Arms = share },
        IdleGroup.Balance => idle with { Balance = share },
        IdleGroup.Tail => idle with { Tail = share },
        _ => idle,
    };

    /// <summary>
    /// The motion of <paramref name="donor"/> to borrow, called <paramref name="name"/>: every grouped bone that turns,
    /// as its turn away from its first frame in its parent's space, and the body's slide. Throws when nothing moves.
    /// </summary>
    public static MotionIdle Borrow(MotionClip donor, string name)
    {
        // A long clip is sampled down to the most samples kept.
        var samples = Math.Min(donor.Samples, MaxSamples);
        var turns = ImmutableDictionary.CreateBuilder<string, ImmutableArray<float>>(StringComparer.Ordinal);
        var slide = ImmutableArray<float>.Empty;
        var bones = donor.Skeleton.Bones;
        for (var b = 0; b < bones.Length; b++)
        {
            var bone = bones[b].Name;
            if (Group(bone) == IdleGroup.None)
                continue;
            var first = donor.Get(0, b);
            var inverse = Quaternion.Inverse(Quaternion.Normalize(first.Rotation));
            var values = new float[samples * 4];
            var moves = false;
            var slides = false;
            var places = bone == MotionHold.Body ? new float[samples * 3] : null;
            var previous = Quaternion.Identity;
            for (var k = 0; k < samples; k++)
            {
                var value = At(donor, b, k, samples);
                // The turn in the parent's space, as Pose2Idle applies vanilla motion over a pose.
                var turn = MotionEvaluator.Align(Quaternion.Normalize(Quaternion.Normalize(value.Rotation) * inverse), previous);
                previous = turn;
                values[k * 4] = turn.X; values[k * 4 + 1] = turn.Y; values[k * 4 + 2] = turn.Z; values[k * 4 + 3] = turn.W;
                moves |= Degrees(turn) > StillAngle;
                if (places != null)
                {
                    var away = value.Position - first.Position;
                    places[k * 3] = away.X; places[k * 3 + 1] = away.Y; places[k * 3 + 2] = away.Z;
                    slides |= away.Length() > StillSlide;
                }
            }
            if (moves)
                turns[bone] = [.. values];
            if (slides)
                slide = [.. places!];
        }
        if (turns.Count == 0 && slide.IsEmpty)
            throw new InvalidDataException("It doesn't move the back, arms, legs or tail.");
        return new MotionIdle(name, donor.Duration, samples, turns.ToImmutable(), slide);
    }

    /// <summary>A donor bone's local transform at sample <paramref name="k"/> of <paramref name="samples"/> spread over its clip.</summary>
    private static MotionTransform At(MotionClip donor, int bone, int k, int samples)
    {
        if (samples == donor.Samples)
            return donor.Get(k, bone);
        var at = (double)k / (samples - 1) * (donor.Samples - 1);
        var low = (int)at;
        var high = Math.Min(low + 1, donor.Samples - 1);
        return MotionEvaluator.Lerp(donor.Get(low, bone), donor.Get(high, bone), (float)(at - low));
    }

    private static float Degrees(Quaternion q) => 2 * MathF.Atan2(new Vector3(q.X, q.Y, q.Z).Length(), MathF.Abs(q.W)) * 180 / MathF.PI;

    /// <summary>Throws when <paramref name="idle"/> can't be read: its samples, shares or values.</summary>
    public static void Validate(MotionIdle idle, string name)
    {
        if (idle.Donor == null || idle.Turns == null || idle.Samples < 2 || idle.Samples > MaxSamples ||
            !float.IsFinite(idle.Duration) || idle.Duration <= 0 || idle.Duration > 3600 ||
            idle.Turns.Any(pair => string.IsNullOrEmpty(pair.Key) || pair.Value.IsDefault || pair.Value.Length != idle.Samples * 4 || pair.Value.Any(v => !float.IsFinite(v))) ||
            !idle.Slide.IsEmpty && (idle.Slide.Length != idle.Samples * 3 || idle.Slide.Any(v => !float.IsFinite(v))))
            throw new InvalidDataException($"{name} has idle motion that can't be read.");
        if (!InShare(idle.Spine, MaxShare) || !InShare(idle.Arms, MaxShare) || !InShare(idle.Balance, MaxShare) || !InShare(idle.Tail, MaxTailShare))
            throw new InvalidDataException($"{name} has a share out of range.");
    }

    private static bool InShare(float share, float most) => float.IsFinite(share) && share >= 0 && share <= most;

    /// <summary>Whether <paramref name="idle"/> moves anything: a bone or the slide in a group with a share.</summary>
    public static bool Moves(MotionIdle idle) =>
        idle.Turns.Keys.Any(bone => Share(idle, Group(bone)) > 0) || !idle.Slide.IsEmpty && idle.Balance > 0;

    /// <summary>The bones <paramref name="layer"/>'s idle motion moves: the borrowed ones with a share, within its bone limit.</summary>
    public static IEnumerable<string> Moved(MotionLayer layer)
    {
        if (layer.Idle is not { } idle)
            yield break;
        foreach (var bone in idle.Turns.Keys.Append(MotionHold.Body).Distinct(StringComparer.Ordinal))
            if ((idle.Turns.ContainsKey(bone) || bone == MotionHold.Body && !idle.Slide.IsEmpty) && Share(idle, Group(bone)) > 0 &&
                (layer.Bones.IsEmpty || layer.Bones.Contains(bone)))
                yield return bone;
    }

    /// <summary>
    /// Where in the borrowed samples <paramref name="frame"/> falls: the motion plays a whole number of times over the layer,
    /// as near its own pace as that allows.
    /// </summary>
    public static float SampleAt(MotionIdle idle, MotionLayer layer, float frame)
    {
        var span = (float)(layer.End - layer.Start);
        var own = idle.Duration * TimelineDocument.Fps;
        var times = Math.Max(1, MathF.Round(span / own));
        var through = Math.Clamp((frame - layer.Start) / span, 0, 1) * times;
        var cycle = through - MathF.Floor(through);
        // The layer's very end is the end of its last time, not the start of another.
        if (through >= times)
            cycle = 1;
        return cycle * (idle.Samples - 1);
    }

    /// <summary>
    /// Plays <paramref name="layer"/>'s idle motion onto one pose at <paramref name="frame"/>, at <paramref name="weight"/>
    /// (its amount through its fades), for the bones in <paramref name="mask"/> (null for all): each turn in the
    /// parent's space before the bone's own, the slide added to the body's place. Bones the skeleton lacks are skipped.
    /// </summary>
    public static void Apply(Span<MotionTransform> pose, MotionLayer layer, Dictionary<string, int> index, HashSet<int>? mask, float frame, float weight)
    {
        if (layer.Idle is not { } idle || weight <= 0)
            return;
        var at = SampleAt(idle, layer, frame);
        var low = Math.Min((int)at, idle.Samples - 1);
        var high = Math.Min(low + 1, idle.Samples - 1);
        var t = at - low;
        var rotate = layer.Components.HasFlag(PoseComponents.Rotation);
        var move = layer.Components.HasFlag(PoseComponents.Position);
        if (rotate)
            foreach (var (bone, turns) in idle.Turns)
            {
                var share = Share(idle, Group(bone)) * weight;
                if (share <= 0 || !index.TryGetValue(bone, out var b) || mask != null && !mask.Contains(b))
                    continue;
                var turn = MotionEvaluator.Slerp(Turn(turns, low), Turn(turns, high), t);
                pose[b] = pose[b] with { Rotation = Quaternion.Normalize(MotionEvaluator.Pow(turn, share) * pose[b].Rotation) };
            }
        if (move && !idle.Slide.IsEmpty && idle.Balance > 0 && index.TryGetValue(MotionHold.Body, out var body) && (mask == null || mask.Contains(body)))
        {
            var slide = Vector3.Lerp(Place(idle.Slide, low), Place(idle.Slide, high), t);
            pose[body] = pose[body] with { Position = pose[body].Position + slide * idle.Balance * weight };
        }
    }

    private static Quaternion Turn(ImmutableArray<float> turns, int k) => new(turns[k * 4], turns[k * 4 + 1], turns[k * 4 + 2], turns[k * 4 + 3]);

    private static Vector3 Place(ImmutableArray<float> slide, int k) => new(slide[k * 3], slide[k * 3 + 1], slide[k * 3 + 2]);
}
