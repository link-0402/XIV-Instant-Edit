namespace InstantEdit.Services.Animations.Timeline;

/// <summary>
/// Whether a face to copy into a library is already there under another name. <see cref="Comparable"/> is the cheap
/// prefilter; the faces it lets through are then sampled frame by frame on the character's face skeleton, which needs
/// the framework thread and is added with the library save.
/// </summary>
internal static class FaceMotionMatch
{
    /// <summary>
    /// Whether two faces can be the same motion: the same whole frames and the same blend hint (an additive face is
    /// never a non-additive one). Track sets are not compared: a library's re-exported copy often tracks other bones.
    /// </summary>
    public static bool Comparable(float duration, sbyte blendHint, float otherDuration, sbyte otherBlendHint) =>
        blendHint == otherBlendHint &&
        TimelineDocument.MotionFrames(duration) is { } frames && TimelineDocument.MotionFrames(otherDuration) == frames;
}
