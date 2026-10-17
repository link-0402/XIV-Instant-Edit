using System.Collections.Immutable;
using System.Numerics;
using InstantEdit.Models;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Services.Animations.Motion;

/// <summary>A bone's transform as a value: what the evaluator works with, so its loops never allocate.</summary>
internal readonly record struct MotionTransform(Vector3 Position, Quaternion Rotation, Vector3 Scale)
{
    public static readonly MotionTransform Identity = new(Vector3.Zero, Quaternion.Identity, Vector3.One);

    public static MotionTransform From(BoneTransform value) => new(value.Position, value.Rotation, value.Scale);

    public BoneTransform ToBone() => new(Position, Rotation, Scale);
}

/// <summary>
/// A decoded clip: every bone's local transform at each sample, on the skeleton the clip is edited
/// on, packed as <see cref="Stride"/> floats per bone like <see cref="AnimationTake"/> (position,
/// rotation x/y/z/w, scale). Samples sit on a uniform grid from 0 to <see cref="Duration"/>
/// seconds, the bake's own grid. <see cref="TrackFloats"/> holds the clip's float tracks per sample
/// when a decode kept them (<see cref="FloatTracks"/> values each); layers never change them.
/// </summary>
internal sealed class MotionClip
{
    public const int Stride = AnimationTake.Stride;

    /// <summary>The most memory a decoded clip may take: about three minutes of a 455-bone skeleton.</summary>
    public const long MaxBytes = 192L * 1024 * 1024;

    private readonly float[] data;

    public MotionClip(SkeletonDescription skeleton, float duration, int samples, float[]? trackFloats = null, int floatTracks = 0)
    {
        AnimationSkeleton.Validate(skeleton);
        if (!float.IsFinite(duration) || duration < 0 || duration > 3600)
            throw new InvalidDataException("The animation's length is invalid.");
        if (samples < 2)
            throw new InvalidDataException("An animation needs at least two samples to edit.");
        var values = (long)samples * skeleton.Bones.Length * Stride;
        if (values * sizeof(float) > MaxBytes)
            throw new InvalidDataException("This animation is too long to edit its motion.");
        if (floatTracks < 0 || trackFloats != null && trackFloats.LongLength != (long)samples * floatTracks)
            throw new InvalidDataException("The animation's float tracks do not match its samples.");
        Skeleton = skeleton;
        Duration = duration;
        Samples = samples;
        TrackFloats = trackFloats;
        FloatTracks = trackFloats == null ? 0 : floatTracks;
        data = new float[values];
    }

    public SkeletonDescription Skeleton { get; }
    public float Duration { get; }
    public int Samples { get; }
    public int Bones => Skeleton.Bones.Length;
    public float[]? TrackFloats { get; }
    public int FloatTracks { get; }

    /// <summary>The bone each of the decoded clip's transform tracks drives, in track order; empty unless decoded.</summary>
    public ImmutableArray<short> SourceTracks { get; init; } = [];

    /// <summary>
    /// Tracked bones whose channels the decoded clip left to the skeleton's reference pose (see
    /// <see cref="AnimationReferenceChannels"/>): a save drops their tracks while nothing moved them, and so does Live.
    /// </summary>
    public ImmutableArray<short> ReferenceFollowers { get; init; } = [];

    /// <summary>The decoded clip's blend hint: 0 normal, 1 or 2 additive (see <see cref="AnimationRetarget.Encode(BoneTransform, BoneTransform, sbyte)"/>).</summary>
    public sbyte BlendHint { get; init; }

    /// <summary>Whether the game plays the clip as a loop, its last sample running back into its first.</summary>
    public bool Loops { get; init; }

    /// <summary>A sample's time in seconds; the last is exactly the duration, as the bake samples it.</summary>
    public float Time(int sample) => sample == Samples - 1 ? Duration : Duration * sample / (Samples - 1);

    /// <summary>A sample's timeline frame, at <see cref="TimelineDocument.Fps"/>.</summary>
    public float Frame(int sample) => Time(sample) * TimelineDocument.Fps;

    public MotionTransform Get(int sample, int bone)
    {
        var s = data.AsSpan(Offset(sample, bone), Stride);
        return new(new Vector3(s[0], s[1], s[2]), new Quaternion(s[3], s[4], s[5], s[6]), new Vector3(s[7], s[8], s[9]));
    }

    public void Set(int sample, int bone, MotionTransform value)
    {
        var s = data.AsSpan(Offset(sample, bone), Stride);
        s[0] = value.Position.X; s[1] = value.Position.Y; s[2] = value.Position.Z;
        s[3] = value.Rotation.X; s[4] = value.Rotation.Y; s[5] = value.Rotation.Z; s[6] = value.Rotation.W;
        s[7] = value.Scale.X; s[8] = value.Scale.Y; s[9] = value.Scale.Z;
    }

    /// <summary>Every bone's local transform at timeline frame <paramref name="frame"/>, between the two samples around it.</summary>
    public MotionTransform[] Pose(float frame)
    {
        var at = Duration <= 0 || !float.IsFinite(frame) ? 0 : Math.Clamp(frame / TimelineDocument.Fps / Duration, 0, 1) * (Samples - 1);
        var low = Math.Min((int)at, Samples - 1);
        var high = Math.Min(low + 1, Samples - 1);
        var t = at - low;
        var pose = new MotionTransform[Bones];
        for (var b = 0; b < Bones; b++)
            pose[b] = high == low || t <= 0 ? Get(low, b) : MotionEvaluator.Lerp(Get(low, b), Get(high, b), t);
        return pose;
    }

    /// <summary>A copy to apply layers to, so this decode stays the unedited motion.</summary>
    public MotionClip Clone()
    {
        var copy = new MotionClip(Skeleton, Duration, Samples, TrackFloats, FloatTracks) { SourceTracks = SourceTracks, ReferenceFollowers = ReferenceFollowers, BlendHint = BlendHint, Loops = Loops };
        data.CopyTo(copy.data, 0);
        return copy;
    }

    /// <summary>Copies another clip's transforms over this one's; both must be the same shape.</summary>
    public void CopyFrom(MotionClip source)
    {
        if (source.Samples != Samples || !ReferenceEquals(source.Skeleton, Skeleton) && source.Skeleton.Fingerprint != Skeleton.Fingerprint)
            throw new InvalidDataException("The decoded clips do not match.");
        source.data.CopyTo(data, 0);
    }

    /// <summary>A clip from poses in sample order, each with one transform per skeleton bone.</summary>
    public static MotionClip FromPoses(SkeletonDescription skeleton, float duration, IReadOnlyList<BoneTransform[]> poses)
    {
        var clip = new MotionClip(skeleton, duration, poses.Count);
        for (var i = 0; i < poses.Count; i++)
        {
            if (poses[i] is not { } pose || pose.Length != skeleton.Bones.Length)
                throw new InvalidDataException("A pose does not match the skeleton.");
            for (var b = 0; b < pose.Length; b++)
                clip.Set(i, b, MotionTransform.From(pose[b]));
        }
        return clip;
    }

    private int Offset(int sample, int bone)
    {
        if ((uint)sample >= (uint)Samples || (uint)bone >= (uint)Bones)
            throw new ArgumentOutOfRangeException(nameof(sample), "The sample or bone is outside the clip.");
        return (sample * Bones + bone) * Stride;
    }
}
