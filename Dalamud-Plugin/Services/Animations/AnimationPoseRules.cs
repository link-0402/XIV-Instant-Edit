using System.Numerics;
using InstantEdit.Models;

namespace InstantEdit.Services.Animations;

internal static class AnimationPoseRules
{
    public static float SmoothStep(float t) => t * t * (3 - 2 * t);

    public static float BlendFloat(float from, float to, float t) => InterpolateFloat(from, to, SmoothStep(t));

    public static BoneTransform Blend(BoneTransform from, BoneTransform to, float t) => Interpolate(from, to, SmoothStep(t));

    /// <summary>Uniform interpolation. Resampling a clip must not ease; only transitions do.</summary>
    public static float InterpolateFloat(float from, float to, float t) => from + (to - from) * t;

    public static BoneTransform Interpolate(BoneTransform from, BoneTransform to, float t)
    {
        var rotation = to.Rotation;
        if (Quaternion.Dot(from.Rotation, rotation) < 0)
            rotation = new(-rotation.X, -rotation.Y, -rotation.Z, -rotation.W);
        return new(Vector3.Lerp(from.Position, to.Position, t),
            Quaternion.Normalize(Quaternion.Slerp(Quaternion.Normalize(from.Rotation), Quaternion.Normalize(rotation), t)),
            Vector3.Lerp(from.Scale, to.Scale, t));
    }

    /// <summary>A length a timeline edit may give a clip: from one 30 fps frame to ten minutes.</summary>
    public static bool ValidDuration(float seconds) => float.IsFinite(seconds) && seconds >= 1 / 30f - 1e-6f && seconds <= 600;

    public static int SampleCount(float duration, int sourceFrames)
    {
        if (!float.IsFinite(duration) || duration < 0 || duration > 3600)
            throw new InvalidDataException("Animation duration is invalid or exceeds one hour.");
        // An integer subdivision of source intervals includes every original sample
        // while retaining uniform spacing for the interleaved Havok output.
        var intervals = Math.Max(1, sourceFrames - 1);
        var subdivision = Math.Max(1, checked((int)Math.Ceiling(duration * 30d / intervals)));
        var count = checked(intervals * subdivision + 1);
        if (count > 216001) throw new InvalidDataException("Animation sampling exceeds the frame limit.");
        return count;
    }
}
