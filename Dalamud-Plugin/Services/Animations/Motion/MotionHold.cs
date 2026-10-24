using System.Numerics;
using InstantEdit.Models;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Services.Animations.Motion;

/// <summary>
/// Motions made from a single pose, for animations started from scratch: a still pose held for a
/// length, and the body's place moved so posed feet stand on the ground again.
/// </summary>
internal static class MotionHold
{
    /// <summary>The bones whose lowest point is the ground: both feet and both toes.</summary>
    private static readonly string[] Feet = ["j_asi_d_l", "j_asi_d_r", "j_asi_e_l", "j_asi_e_r"];

    /// <summary>The bone that carries the whole body's place.</summary>
    public const string Body = "n_hara";

    /// <summary>
    /// <paramref name="playing"/>'s pose at timeline frame <paramref name="frame"/>, float tracks included,
    /// held still for <paramref name="frames"/> frames: one sample a frame, so later edits find the
    /// usual grid. It keeps the clip's tracks, blend hint and loop flag.
    /// </summary>
    public static MotionClip Still(MotionClip playing, float frame, int frames)
    {
        if (frames < 1)
            throw new InvalidDataException("A still pose needs at least one frame.");
        var samples = frames + 1;
        var pose = playing.Pose(frame);
        var tracks = playing.FloatTracks;
        float[]? floats = null;
        if (playing.TrackFloats is { } source)
        {
            // The float tracks at the sample nearest the frame; they hold too.
            var at = playing.Duration <= 0 ? 0 : (int)MathF.Round(Math.Clamp(frame / TimelineDocument.Fps / playing.Duration, 0, 1) * (playing.Samples - 1));
            floats = new float[(long)samples * tracks];
            for (var k = 0; k < samples; k++)
                Array.Copy(source, (long)at * tracks, floats, (long)k * tracks, tracks);
        }
        var still = new MotionClip(playing.Skeleton, frames / (float)TimelineDocument.Fps, samples, floats, tracks)
        {
            SourceTracks = playing.SourceTracks,
            ReferenceFollowers = playing.ReferenceFollowers,
            BlendHint = playing.BlendHint,
            Loops = playing.Loops,
        };
        for (var k = 0; k < samples; k++)
            for (var b = 0; b < pose.Length; b++)
                still.Set(k, b, pose[b]);
        return still;
    }

    /// <summary>The lowest model-space height of the feet and toes in a local pose; null on a skeleton without them.</summary>
    public static float? Lowest(SkeletonDescription skeleton, ReadOnlySpan<MotionTransform> local)
    {
        var model = MotionEvaluator.ModelSpace(skeleton, local);
        float? lowest = null;
        for (var b = 0; b < skeleton.Bones.Length; b++)
            if (Array.IndexOf(Feet, skeleton.Bones[b].Name) >= 0)
                lowest = Math.Min(lowest ?? float.MaxValue, model[b].Position.Y);
        return lowest;
    }

    /// <summary>
    /// The body's (<see cref="Body"/>) local position in <paramref name="posed"/> moved up or down so its lowest
    /// foot sits as high as <paramref name="ground"/>'s does: posing bends legs and lifts the feet, while the
    /// game keeps the character's root on the floor. Null when the skeleton has no body bone or no feet.
    /// </summary>
    public static Vector3? OnGround(SkeletonDescription skeleton, ReadOnlySpan<MotionTransform> posed, ReadOnlySpan<MotionTransform> ground)
    {
        if (!MotionEvaluator.Index(skeleton).TryGetValue(Body, out var body) || Lowest(skeleton, posed) is not { } feet || Lowest(skeleton, ground) is not { } floor)
            return null;
        var parent = skeleton.Bones[body].Parent;
        var drop = new Vector3(0, floor - feet, 0);
        if (parent >= 0)
        {
            // The drop is in character space; the body's position is in its parent's.
            var model = MotionEvaluator.ModelSpace(skeleton, posed)[parent];
            drop = Vector3.Transform(drop, Quaternion.Inverse(Quaternion.Normalize(model.Rotation)));
        }
        return posed[body].Position + drop;
    }
}
