using System.Collections.Immutable;
using InstantEdit.Models;

namespace InstantEdit.Services.Animations.Motion;

/// <summary>What a bone layer does to the motion under it.</summary>
internal enum MotionLayerKind : byte
{
    /// <summary>Turns, moves or scales bones by the amounts its keys hold.</summary>
    Offset,
    /// <summary>Makes bones move more or less around their average pose over the layer.</summary>
    Amplitude,
    /// <summary>Blends bones toward the poses its keys hold.</summary>
    Pose,
}

/// <summary>How a bone's value goes from one key to the next.</summary>
internal enum MotionInterpolation : byte { Linear, Smooth, Step }

/// <summary>
/// A layer's values at one timeline frame, by bone name. An offset key holds changes: a rotation
/// turned onto the bone's own (so its children follow), a move in character space and a scale
/// factor. A pose key holds whole local transforms; <paramref name="Moved"/> names the bones whose
/// position it sets too, every other bone only turns to it.
/// </summary>
internal sealed record MotionKey(int Frame, ImmutableDictionary<string, BoneTransform> Bones,
    ImmutableArray<string> Moved = default)
{
    public ImmutableArray<string> Moved { get; init; } = Moved.IsDefault ? [] : Moved;
}

/// <summary>
/// One bone layer of an animation. It changes the motion from <paramref name="Start"/> to
/// <paramref name="End"/> (timeline frames), easing in over <paramref name="FadeIn"/> frames and
/// out over <paramref name="FadeOut"/>. <paramref name="Amount"/> is an offset's strength, an
/// amplitude's factor (1 changes nothing, 0 holds the average pose) or a pose's blend weight.
/// <paramref name="Components"/> are the parts of a transform it changes. <paramref name="Bones"/>
/// limits it to these bones; empty means every bone its keys name, or for an amplitude every bone.
/// Layers apply in their document's order, each to the motion the ones before it made.
/// </summary>
/// <param name="Key">"m" + a number, unique in its document.</param>
internal sealed record MotionLayer(
    string Key,
    MotionLayerKind Kind,
    string Name,
    int Start,
    int End,
    int FadeIn,
    int FadeOut,
    float Amount,
    PoseComponents Components,
    MotionInterpolation Interpolation,
    ImmutableArray<string> Bones = default,
    ImmutableArray<MotionKey> Keys = default,
    bool Enabled = true)
{
    public ImmutableArray<string> Bones { get; init; } = Bones.IsDefault ? [] : Bones;
    public ImmutableArray<MotionKey> Keys { get; init; } = Keys.IsDefault ? [] : Keys;
}
