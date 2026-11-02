using System.Collections.Immutable;
using System.Numerics;
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
    /// <summary>Makes bones copy their twins on the other side, mirrored.</summary>
    Symmetry,
    /// <summary>Holds bones at a spot while the chain above them bends to reach it.</summary>
    Pin,
    /// <summary>Repairs the motion: lands a startup's end on its loop, smooths jitter, shapes a hand or resets the root's turn (<see cref="MotionFix"/>).</summary>
    Fix,
    /// <summary>Plays another clip's idle motion over the bones, such as breathing (<see cref="MotionIdle"/>).</summary>
    Idle,
    /// <summary>Swings bones back and forth over the layer (<see cref="MotionSwing"/>).</summary>
    Swing,
    /// <summary>Evens out sudden moves: each frame goes toward the average of the frames round it over <see cref="MotionLayer.Window"/>.</summary>
    Smooth,
    /// <summary>Wobbles bones at random, smoothly (<see cref="MotionNoise"/>).</summary>
    Noise,
    /// <summary>Turns the head, with some of the neck and upper back, toward keyed spots or a followed bone (<see cref="MotionLook"/>).</summary>
    LookAt,
    /// <summary>Moves a bone with a partner's bone (<see cref="MotionLink"/>).</summary>
    Link,
    /// <summary>Plays a stretch of the motion again and again (<see cref="MotionRepeat"/>).</summary>
    Repeat,
    /// <summary>Makes bones lag behind the motion and overshoot it (<see cref="MotionSpring"/>).</summary>
    Spring,
}

/// <summary>What a fix layer repairs. Saved as numbers.</summary>
internal enum MotionFixKind : byte
{
    /// <summary>Blends a startup's end onto the loop frame the game shows then, pose and speed.</summary>
    MatchLoop,
    /// <summary>Smooths small shakes and keeps sharp moves.</summary>
    SmoothJitter,
    /// <summary>Puts a hand's fingers into a shape (<see cref="MotionHand"/>).</summary>
    HandShape,
    /// <summary>Turns the root bone back to its rest on the picked axes (<see cref="MotionFix.Axes"/>), and with Y a facing turn baked into the body.</summary>
    ResetRoot,
    /// <summary>Blends a looping clip's end into its start after every other layer, pose and speed.</summary>
    CloseLoop,
}

/// <summary>The axes a root reset takes the turn out of, in the root's own space (Y is up). Saved as numbers.</summary>
[Flags]
internal enum MotionAxes : byte
{
    None = 0,
    X = 1,
    Y = 2,
    Z = 4,
    All = X | Y | Z,
}

/// <summary>Which hand a hand shape sets. Saved as numbers.</summary>
internal enum MotionHandSide : byte
{
    Left,
    Right,
    Both,
}

/// <summary>
/// What a hand shape fix sets. <paramref name="Shape"/> is a built-in shape's id or a saved shape's name; a saved
/// shape's right-hand turns by bone name are in <paramref name="Turns"/> (null for a built-in). <paramref name="Curl"/>
/// and <paramref name="Spread"/> run from -1 to 1. <paramref name="Tips"/> says the live skeleton and mesh allow
/// fingertip bends.
/// </summary>
internal sealed record MotionHand(MotionHandSide Side, string Shape, float Curl = 0, float Spread = 0, bool KeepMotion = false,
    bool Tips = false, ImmutableDictionary<string, Quaternion>? Turns = null);

/// <summary>
/// A fix layer's fix. For <see cref="MotionFixKind.MatchLoop"/>, <paramref name="LoopFirst"/> and
/// <paramref name="LoopSecond"/> are the loop's local transforms at timeline frames <paramref name="Fade"/> and one
/// after by bone name, so their difference is the loop's speed per timeline frame there.
/// </summary>
/// <param name="Fade">For a match, the frames the game cross-fades the startup into its loop over: the loop starts
/// that many frames before the startup's end, so the startup's last frame shows with the loop's frame of that number,
/// the one the match lands on. Journals written before it load with 0, the loop's first frame.</param>
/// <param name="Axes">For a root reset, the axes it takes the root's turn out of; none for other fixes.</param>
/// <param name="Placement">For a close, where the blend sits at the seam; other fixes keep the default.</param>
internal sealed record MotionFix(MotionFixKind Kind, ImmutableDictionary<string, BoneTransform>? LoopFirst = null,
    ImmutableDictionary<string, BoneTransform>? LoopSecond = null, int Fade = 0, MotionHand? Hand = null, MotionAxes Axes = MotionAxes.None,
    JoinPlacement Placement = JoinPlacement.Centered);

/// <summary>How readily a planted pin counts its bone as down. Saved as numbers; Normal first, so a missing value reads as Normal.</summary>
internal enum PinSensitivity : byte
{
    Normal,
    Strict,
    Loose,
}

/// <summary>How many markers the Motion keys layer shows. Saved as numbers; Normal first, so a missing value reads as Normal.</summary>
internal enum MarkerDetail : byte
{
    Normal,
    /// <summary>Only the larger changes in the motion.</summary>
    Fewer,
    /// <summary>Smaller changes too.</summary>
    More,
}

/// <summary>
/// What marks a Motion keys layer: how closely its markers follow the motion and <paramref name="Reach"/>, how many
/// markers an edit fades out over (1 to <see cref="MaxReach"/>).
/// </summary>
internal sealed record MotionMarkers(MarkerDetail Detail = MarkerDetail.Normal, int Reach = 1)
{
    public const int MaxReach = 3;
}

/// <summary>
/// One bone a pin layer holds. <paramref name="HoldTurn"/> holds its turn as well as its position.
/// <paramref name="RelativeTo"/> is the bone it rides with, null for the character. <paramref name="Top"/>
/// is the highest bone the solver may turn, null for the default chain. A <paramref name="Planted"/>
/// pin has no keys: it holds the bone where the motion has it down, as readily as <paramref name="Sensitivity"/> says.
/// <paramref name="Bend"/> turns a limb's bend about the line from its upper bone to the held spot, in degrees (-180 to 180):
/// 0 bends it the way the solver picks. <paramref name="Floor"/> sets a planted foot held relative to the character down on the
/// floor, flat unless the motion tips it far (see <see cref="PinPlanting.OnFloor"/>); other pins ignore it.
/// </summary>
internal sealed record MotionPin(string Bone, bool HoldTurn = true, string? RelativeTo = null, string? Top = null, bool Planted = false,
    PinSensitivity Sensitivity = PinSensitivity.Normal, float Bend = 0, bool Floor = false);

/// <summary>
/// What a link layer joins: <paramref name="Bone"/> of this clip moves the way <paramref name="PartnerBone"/> of the partner's
/// clip moves from its edits. <paramref name="Partner"/> is the partner workspace's preset Id, null for You (the partner's side
/// of a link to you). <paramref name="PartnerClip"/> and <paramref name="PartnerBinding"/> are the clip the link expects open
/// there (<see cref="AnimationClip.GamePath"/>, <see cref="AnimationClip.BindingIndex"/>). <paramref name="HoldTurn"/> carries
/// the partner's turn onto the bone too. <paramref name="Top"/> and <paramref name="Bend"/> are a pin's (<see cref="MotionPin"/>),
/// for limbs and chains. <paramref name="Holds"/> are, for a hips link only, the bones that stay where they are.
/// </summary>
internal sealed record MotionLink(string Bone, Guid? Partner, string PartnerBone, string PartnerClip, int PartnerBinding,
    bool HoldTurn = true, string? Top = null, float Bend = 0, ImmutableArray<string> Holds = default)
{
    public ImmutableArray<string> Holds { get; init; } = Holds.IsDefault ? [] : Holds;
}

/// <summary>A side of the body, for the side a symmetry layer copies. Saved as numbers.</summary>
internal enum MotionSide : byte
{
    Left,
    Right,
}

/// <summary>
/// How a bone's value goes from one key to the next. Saved as numbers, so new ways only ever go at
/// the end. <see cref="Custom"/> is a key's own curve and never a layer's.
/// </summary>
internal enum MotionInterpolation : byte
{
    Linear,
    /// <summary>Slow at both keys.</summary>
    Smooth,
    /// <summary>Holds the value until the next key.</summary>
    Step,
    /// <summary>Slow at the first key, full speed into the next.</summary>
    EaseIn,
    /// <summary>Full speed from the first key, slow into the next.</summary>
    EaseOut,
    /// <summary>The key's own <see cref="MotionCurve"/>.</summary>
    Custom,
}

/// <summary>
/// An easing curve from (0, 0) to (1, 1) with two handles, as CSS's cubic-bezier: X is the time
/// between the keys, Y how far the value has come. Handle X stays within 0 to 1 so the curve never
/// runs back in time; Y may leave it to overshoot or wind up.
/// </summary>
internal sealed record MotionCurve(float X1, float Y1, float X2, float Y2)
{
    public const float MinY = -1, MaxY = 2;

    public static readonly MotionCurve Linear = new(1 / 3f, 1 / 3f, 2 / 3f, 2 / 3f);
    /// <summary>Exactly the smoothstep <see cref="MotionInterpolation.Smooth"/> has always used.</summary>
    public static readonly MotionCurve Smooth = new(1 / 3f, 0, 2 / 3f, 1);
    public static readonly MotionCurve EaseIn = new(1 / 3f, 0, 2 / 3f, 2 / 3f);
    public static readonly MotionCurve EaseOut = new(1 / 3f, 1 / 3f, 2 / 3f, 1);

    public bool Valid => float.IsFinite(X1) && float.IsFinite(Y1) && float.IsFinite(X2) && float.IsFinite(Y2) &&
                         X1 is >= 0 and <= 1 && X2 is >= 0 and <= 1 && Y1 is >= MinY and <= MaxY && Y2 is >= MinY and <= MaxY;

    /// <summary>The curve with its handles moved back into range.</summary>
    public MotionCurve Clamped() => new(Clamp(X1, 0, 1), Clamp(Y1, MinY, MaxY), Clamp(X2, 0, 1), Clamp(Y2, MinY, MaxY));

    /// <summary>How far the value has come at <paramref name="t"/> of the way between the keys.</summary>
    public float Ease(float t)
    {
        if (!(t > 0))
            return 0;
        if (t >= 1)
            return 1;
        return Bezier(SolveX(t), Y1, Y2);
    }

    private float SolveX(float x)
    {
        // Handles a third and two thirds along make X the time itself, as every preset does.
        if (MathF.Abs(X1 - 1 / 3f) < 1e-6f && MathF.Abs(X2 - 2 / 3f) < 1e-6f)
            return x;
        var s = x;
        for (var i = 0; i < 8; i++)
        {
            var error = Bezier(s, X1, X2) - x;
            if (MathF.Abs(error) < 1e-6f)
                return s;
            var slope = Slope(s, X1, X2);
            if (MathF.Abs(slope) < 1e-6f)
                break;
            s = Math.Clamp(s - error / slope, 0, 1);
        }
        // Flat stretches stall Newton; the curve's X only ever rises, so halving always finds it.
        float low = 0, high = 1;
        for (var i = 0; i < 40; i++)
        {
            s = (low + high) / 2;
            if (Bezier(s, X1, X2) < x) low = s;
            else high = s;
        }
        return (low + high) / 2;
    }

    private static float Bezier(float s, float p1, float p2)
    {
        var u = 1 - s;
        return 3 * u * u * s * p1 + 3 * u * s * s * p2 + s * s * s;
    }

    private static float Slope(float s, float p1, float p2)
    {
        var u = 1 - s;
        return 3 * u * u * p1 + 6 * u * s * (p2 - p1) + 3 * s * s * (1 - p2);
    }

    private static float Clamp(float value, float min, float max) => float.IsFinite(value) ? Math.Clamp(value, min, max) : min;
}

/// <summary>
/// A layer's values at one timeline frame, by bone name. An offset key holds changes: a rotation
/// turned onto the bone's own (so its children follow), a move in character space and a scale
/// factor. A pose key holds whole local transforms; <paramref name="Moved"/> names the bones whose
/// position it sets too, every other bone only turns to it. <paramref name="Ease"/> is how every
/// bone goes from this key to its next one, null for the layer's way; <paramref name="Curve"/> is
/// the curve <see cref="MotionInterpolation.Custom"/> uses. Journals written before keys had their
/// own ease load with none.
/// </summary>
internal sealed record MotionKey(int Frame, ImmutableDictionary<string, BoneTransform> Bones,
    ImmutableArray<string> Moved = default, MotionInterpolation? Ease = null, MotionCurve? Curve = null)
{
    public ImmutableArray<string> Moved { get; init; } = Moved.IsDefault ? [] : Moved;
}

/// <summary>
/// A point of a layer's weight over time: at <paramref name="Frame"/> (timeline frames) the layer applies
/// <paramref name="Value"/> (0 to 1) of its strength, easing on a smoothstep to the next point.
/// </summary>
internal sealed record MotionWeight(int Frame, float Value);

/// <summary>
/// One bone layer of an animation. It changes the motion from <paramref name="Start"/> to
/// <paramref name="End"/> (timeline frames), easing in over <paramref name="FadeIn"/> frames and
/// out over <paramref name="FadeOut"/>. <paramref name="Amount"/> is an offset's strength, an
/// amplitude's factor (1 changes nothing, 0 holds the average pose), a pose's blend weight or a
/// symmetry's blend toward the other side's motion.
/// <paramref name="Components"/> are the parts of a transform it changes. <paramref name="Bones"/>
/// limits it to these bones; empty means every bone its keys name, or for an amplitude every bone.
/// <paramref name="Interpolation"/> is how bones go from a key without an ease of its own to the next.
/// Layers apply in their document's order, each to the motion the ones before it made.
/// </summary>
/// <param name="From">For a symmetry layer, the side whose motion is copied; other kinds ignore it.</param>
/// <param name="Pins">For a pin layer, the bones it holds; its keys hold their spots in each pin's space
/// and its amount is the pull toward them. Other kinds have none.</param>
/// <param name="Fix">For a fix layer, what it repairs; its amount is a match's blend or a smooth's strength.
/// Other kinds have none, and journals written before fix layers load with none.</param>
/// <param name="Idle">For an idle-motion layer, the motion it borrows, null until one is read; its amount is that
/// motion's strength. Other kinds have none.</param>
/// <param name="Markers">For the Motion keys layer, an offset whose keys mark the motion's own key poses; null for every
/// other layer, and for journals written before it.</param>
/// <param name="Swing">For a swing layer, how its bones swing; its amount is the swing's strength. Other kinds have none.</param>
/// <param name="Window">For a smooth motion layer, the frames each frame is averaged over; its amount is the blend toward
/// that average. 0 on every other kind, and for journals written before it.</param>
/// <param name="Weights">Its weight over time, by frame inside the layer (<see cref="MotionEvaluator.WeightAt"/>), times its
/// fades; empty is full weight, as for journals written before it.</param>
/// <param name="Noise">For a noise layer, how its bones wobble; its amount is the wobble's strength. Other kinds have none.</param>
/// <param name="Look">For a look-at layer, what it looks at and how far it turns; its keys hold spots in character space under the
/// head's name and its amount is the turn's blend. Other kinds have none.</param>
/// <param name="Link">For a link layer, the bones it joins; its amount is the pull toward the partner's move and it has no keys.
/// Other kinds have none, and journals written before it load with none.</param>
/// <param name="Repeat">For a repeat layer, the stretch of motion it plays again and again; its amount is the blend toward it. Other
/// kinds have none, and journals written before it load with none.</param>
/// <param name="Spring">For a spring layer, how its bones lag and overshoot; its amount is the blend toward the sprung motion. Other
/// kinds have none, and journals written before it load with none.</param>
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
    bool Enabled = true,
    MotionSide From = MotionSide.Left,
    ImmutableArray<MotionPin> Pins = default,
    MotionFix? Fix = null,
    MotionIdle? Idle = null,
    MotionMarkers? Markers = null,
    MotionSwing? Swing = null,
    int Window = 0,
    ImmutableArray<MotionWeight> Weights = default,
    MotionNoise? Noise = null,
    MotionLook? Look = null,
    MotionLink? Link = null,
    MotionRepeat? Repeat = null,
    MotionSpring? Spring = null)
{
    public ImmutableArray<string> Bones { get; init; } = Bones.IsDefault ? [] : Bones;
    public ImmutableArray<MotionKey> Keys { get; init; } = Keys.IsDefault ? [] : Keys;
    public ImmutableArray<MotionPin> Pins { get; init; } = Pins.IsDefault ? [] : Pins;
    public ImmutableArray<MotionWeight> Weights { get; init; } = Weights.IsDefault ? [] : Weights;
}
