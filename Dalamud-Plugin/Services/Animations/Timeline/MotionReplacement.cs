using InstantEdit.Services.Animations.Motion;

namespace InstantEdit.Services.Animations.Timeline;

/// <summary>
/// A motion brought in from Blender in place of the clip's own (Motion from Blender): the clip's PAP
/// with only this clip's motion replaced, its binding's fingerprint, its hash, the motion's length in
/// frames, the action and armature it was read from, and what Blender changed. A timeline document
/// holds it, so undo swaps it back; the window makes it the clip's base motion.
/// </summary>
internal sealed record MotionReplacement(byte[] Pap, string Fingerprint, string Hash, int Frames, string Action, string Armature,
    BlenderMotionReport Report);
