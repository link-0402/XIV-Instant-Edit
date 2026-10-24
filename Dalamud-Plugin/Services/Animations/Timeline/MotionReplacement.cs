using InstantEdit.Services.Animations.Motion;

namespace InstantEdit.Services.Animations.Timeline;

/// <summary>
/// A motion in place of the clip's own: one brought in from Blender (Motion from Blender), or a still
/// pose the clip starts from scratch with. It holds the clip's PAP with only this clip's motion
/// replaced, its binding's fingerprint, its hash and the motion's length in frames. From Blender, the
/// action and armature it was read from and what Blender changed (<see cref="Report"/>); for a still
/// pose, <see cref="Report"/> is null and <see cref="Action"/> names the frame it was held from. A
/// timeline document holds it, so undo swaps it back; the window makes it the clip's base motion.
/// </summary>
internal sealed record MotionReplacement(byte[] Pap, string Fingerprint, string Hash, int Frames, string Action, string Armature,
    BlenderMotionReport? Report)
{
    /// <summary>Whether this is a still pose rather than a motion from Blender.</summary>
    public bool Still => Report == null;
}
