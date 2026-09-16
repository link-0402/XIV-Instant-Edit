using System.Collections.Immutable;
using System.Numerics;
using InstantEdit.Models;

namespace InstantEdit.Services.Animations;

/// <summary>
/// A decoded clip as owned managed data: one pose and one float set per sample on a
/// uniform grid. Built only from <see cref="SkeletonDescription"/> and
/// <see cref="BoneTransform"/>, so unlike a live Havok binding it may cross framework
/// ticks and be transformed off the framework thread.
/// </summary>
internal sealed record AnimationFrames(SkeletonDescription Skeleton,
    ImmutableArray<BoneTransform[]> Poses, ImmutableArray<float[]> Floats, float Duration)
{
    public int Count => Poses.Length;
}

/// <summary>
/// Pure timeline transforms over <see cref="AnimationFrames"/>. Retiming, trimming and
/// looping all reduce to these; the native write path stays in AnimationBakeService.
/// </summary>
internal static class AnimationTimeline
{
    public static void Validate(AnimationFrames frames)
    {
        if (frames.Poses.IsDefault || frames.Floats.IsDefault || frames.Poses.Length < 1 ||
            frames.Poses.Length != frames.Floats.Length)
            throw new InvalidDataException("Animation frames are empty or misaligned.");
        if (!float.IsFinite(frames.Duration) || frames.Duration < 0 || frames.Duration > 3600)
            throw new InvalidDataException("Animation duration is invalid or exceeds one hour.");
        var bones = frames.Skeleton.Bones.Length;
        var floats = frames.Skeleton.FloatNames.Length;
        foreach (var pose in frames.Poses)
            if (pose == null || pose.Length != bones)
                throw new InvalidDataException("An animation frame does not match its skeleton.");
        foreach (var set in frames.Floats)
            if (set == null || set.Length != floats)
                throw new InvalidDataException("An animation float set does not match its skeleton.");
    }

    /// <summary>Sample the uniform grid at a normalized position in [0, 1].</summary>
    public static (BoneTransform[] Pose, float[] Floats) SampleNormalized(AnimationFrames frames, float position)
    {
        Validate(frames);
        if (!float.IsFinite(position)) throw new InvalidDataException("Animation sample position is invalid.");
        position = Math.Clamp(position, 0, 1);
        if (frames.Count == 1) return ((BoneTransform[])frames.Poses[0].Clone(), (float[])frames.Floats[0].Clone());
        var exact = position * (frames.Count - 1);
        var index = Math.Clamp((int)MathF.Floor(exact), 0, frames.Count - 2);
        var fraction = Math.Clamp(exact - index, 0, 1);
        var from = frames.Poses[index];
        var to = frames.Poses[index + 1];
        var pose = new BoneTransform[from.Length];
        for (var i = 0; i < pose.Length; i++) pose[i] = AnimationPoseRules.Interpolate(from[i], to[i], fraction);
        var fromFloats = frames.Floats[index];
        var toFloats = frames.Floats[index + 1];
        var values = new float[fromFloats.Length];
        for (var i = 0; i < values.Length; i++)
            values[i] = AnimationPoseRules.InterpolateFloat(fromFloats[i], toFloats[i], fraction);
        return (pose, values);
    }

    /// <summary>Re-grid the clip onto a new duration, keeping both endpoints.</summary>
    public static AnimationFrames Resample(AnimationFrames frames, float duration)
        => Rebuild(frames, duration, 0, 1);

    /// <summary>
    /// Keep only [start, end] of the clip. The trimmed range becomes the new duration,
    /// so a caller that also owns a TMB must scale its event times by the same factor.
    /// </summary>
    public static AnimationFrames Trim(AnimationFrames frames, float start, float end)
    {
        Validate(frames);
        if (!float.IsFinite(start) || !float.IsFinite(end) || start < 0 || end > frames.Duration || end - start <= 0)
            throw new InvalidDataException("The trimmed range is outside the animation or empty.");
        if (frames.Duration <= 0) throw new InvalidDataException("A zero-length animation cannot be trimmed.");
        return Rebuild(frames, end - start, start / frames.Duration, end / frames.Duration);
    }

    private static AnimationFrames Rebuild(AnimationFrames frames, float duration, float from, float to)
    {
        Validate(frames);
        var count = AnimationPoseRules.SampleCount(duration, frames.Count);
        var poses = ImmutableArray.CreateBuilder<BoneTransform[]>(count);
        var floats = ImmutableArray.CreateBuilder<float[]>(count);
        for (var i = 0; i < count; i++)
        {
            var position = count == 1 ? to : from + (to - from) * i / (count - 1);
            var (pose, values) = SampleNormalized(frames, position);
            poses.Add(pose); floats.Add(values);
        }
        return frames with { Poses = poses.MoveToImmutable(), Floats = floats.MoveToImmutable(), Duration = duration };
    }

    /// <summary>
    /// Ease the tail into the opening pose so the clip wraps without a visible pop.
    /// The final sample becomes exactly the first, which makes the wrap a zero-length
    /// interpolation rather than something a seam fix has to detect afterwards.
    /// </summary>
    public static AnimationFrames CrossfadeLoop(AnimationFrames frames, float fadeSeconds)
    {
        Validate(frames);
        if (!float.IsFinite(fadeSeconds) || fadeSeconds < 0 || fadeSeconds > frames.Duration)
            throw new InvalidDataException("The loop crossfade is negative or longer than the animation.");
        if (frames.Count < 2) throw new InvalidDataException("A single-sample animation cannot be looped.");
        var head = frames.Poses[0];
        var headFloats = frames.Floats[0];
        var start = frames.Duration <= 0 || fadeSeconds <= 0 ? 1 : 1 - fadeSeconds / frames.Duration;
        var poses = ImmutableArray.CreateBuilder<BoneTransform[]>(frames.Count);
        var floats = ImmutableArray.CreateBuilder<float[]>(frames.Count);
        for (var i = 0; i < frames.Count; i++)
        {
            var position = (float)i / (frames.Count - 1);
            // Outside the fade window, and for a zero-length fade, only the final
            // sample is pinned to the opening pose.
            var alpha = i == frames.Count - 1 ? 1
                : position <= start || start >= 1 ? 0
                : (position - start) / (1 - start);
            if (alpha <= 0) { poses.Add((BoneTransform[])frames.Poses[i].Clone()); floats.Add((float[])frames.Floats[i].Clone()); continue; }
            var pose = new BoneTransform[head.Length];
            for (var b = 0; b < pose.Length; b++) pose[b] = AnimationPoseRules.Blend(frames.Poses[i][b], head[b], alpha);
            var values = new float[headFloats.Length];
            for (var f = 0; f < values.Length; f++)
                values[f] = AnimationPoseRules.BlendFloat(frames.Floats[i][f], headFloats[f], alpha);
            poses.Add(pose); floats.Add(values);
        }
        return NormalizeHemisphere(frames with { Poses = poses.MoveToImmutable(), Floats = floats.MoveToImmutable() });
    }

    /// <summary>
    /// Keep every bone's rotation on the same hemisphere as the previous sample, so
    /// spline compression never interpolates the long way around. Session.SampleBatch
    /// does this while decoding; generated frames need it too.
    /// </summary>
    public static AnimationFrames NormalizeHemisphere(AnimationFrames frames)
    {
        Validate(frames);
        var poses = frames.Poses.Select(pose => (BoneTransform[])pose.Clone()).ToArray();
        for (var i = 1; i < poses.Length; i++)
            for (var b = 0; b < poses[i].Length; b++)
                if (Quaternion.Dot(poses[i - 1][b].Rotation, poses[i][b].Rotation) < 0)
                {
                    var r = poses[i][b].Rotation;
                    poses[i][b] = poses[i][b] with { Rotation = new(-r.X, -r.Y, -r.Z, -r.W) };
                }
        return frames with { Poses = [.. poses] };
    }

    /// <summary>
    /// How far the wrap point misses, per bone. Rotation is sign-agnostic because a
    /// quaternion and its negation are the same orientation.
    /// </summary>
    public static ImmutableArray<(string Bone, float Position, float Rotation)> SeamError(AnimationFrames frames)
    {
        Validate(frames);
        var first = frames.Poses[0];
        var last = frames.Poses[^1];
        var result = ImmutableArray.CreateBuilder<(string, float, float)>(first.Length);
        for (var i = 0; i < first.Length; i++)
            result.Add((frames.Skeleton.Bones[i].Name, Vector3.Distance(first[i].Position, last[i].Position),
                1 - Math.Abs(Quaternion.Dot(Quaternion.Normalize(first[i].Rotation), Quaternion.Normalize(last[i].Rotation)))));
        return result.MoveToImmutable();
    }
}
