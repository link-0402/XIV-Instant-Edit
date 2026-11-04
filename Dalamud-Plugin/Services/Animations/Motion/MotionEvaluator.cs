using System.Collections.Immutable;
using System.Numerics;
using InstantEdit.Models;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Services.Animations.Motion;

/// <summary>
/// Applies bone layers to a decoded clip. Pure, so the save's bake, the live GPose display and the
/// bone panels all get the same motion: what is shown is what is saved. Transforms compose the
/// Havok way (the game's syncLocalSpace): a child's model rotation is its parent's times its own,
/// and its model position is its parent's plus its own turned by the parent, never scaled by it.
/// </summary>
internal static class MotionEvaluator
{
    /// <summary>The largest offset strength or amplitude factor.</summary>
    public const float MaxAmount = 4;
    public const int MaxLayers = 64;
    public const int MaxKeys = 4096;
    public const int MaxKeyBones = 4096;

    /// <summary>The longest hand shape name and the most bone turns a saved hand shape holds.</summary>
    public const int MaxHandShapeName = 40, MaxHandTurns = 32;
    /// <summary>The range of an offset key's scale factor, and of a pose key's scale.</summary>
    public const float MinScale = 0.001f, MaxScale = 100;
    // Sample frames come from float seconds; a layer reaching the clip's end must still cover its last sample.
    internal const float FrameTolerance = 1e-3f;

    /// <summary>The most weight points a layer holds.</summary>
    public const int MaxWeights = 256;

    /// <summary>
    /// Whether <paramref name="layer"/> changes anything when applied without partner tracks: a link layer never does.
    /// </summary>
    public static bool Active(MotionLayer layer) => Active(layer, null);

    /// <summary>
    /// Whether <paramref name="layer"/> changes anything when applied with <paramref name="links"/>. Weight points holding it at 0
    /// all along leave it out: all at 0, with one on its first frame and one on its last. A link layer, and a look-at layer that
    /// follows a partner, needs its track in <paramref name="links"/>.
    /// </summary>
    public static bool Active(MotionLayer layer, MotionLinkSet? links) => Weighted(layer) && layer.Kind switch
    {
        MotionLayerKind.Amplitude => Math.Abs(layer.Amount - 1) > 1e-6f &&
                                     (layer.Components & (PoseComponents.Rotation | PoseComponents.Position)) != 0,
        MotionLayerKind.Symmetry => layer.Amount > 0 && layer.Components != PoseComponents.None && !layer.Bones.IsEmpty,
        MotionLayerKind.Pin => layer.Amount > 0 && layer.Pins.Any(pin => pin.Planted || layer.Keys.Any(key => key.Bones.ContainsKey(pin.Bone))),
        MotionLayerKind.Fix => layer.Amount > 0 && layer.Fix is { } fix && fix.Kind switch
        {
            MotionFixKind.HandShape => fix.Hand != null && layer.Components.HasFlag(PoseComponents.Rotation),
            MotionFixKind.ResetRoot => fix.Axes != MotionAxes.None && layer.Components.HasFlag(PoseComponents.Rotation),
            _ => (layer.Components & (PoseComponents.Rotation | PoseComponents.Position)) != 0 && (fix.Kind != MotionFixKind.MatchLoop || fix.LoopFirst is { IsEmpty: false }),
        },
        MotionLayerKind.Idle => layer.Amount > 0 && layer.Idle is { } idle && MotionIdles.Moves(idle) &&
                                (layer.Components & (PoseComponents.Rotation | PoseComponents.Position)) != 0,
        MotionLayerKind.Swing => layer.Amount > 0 && layer.Swing is { } swing && !layer.Bones.IsEmpty &&
                                 ((swing.Tilt > 0 || swing.Twist > 0) && layer.Components.HasFlag(PoseComponents.Rotation) ||
                                  swing.Slide > 0 && layer.Components.HasFlag(PoseComponents.Position)),
        MotionLayerKind.Smooth => layer.Amount > 0 && (layer.Components & (PoseComponents.Rotation | PoseComponents.Position)) != 0,
        MotionLayerKind.Noise => layer.Amount > 0 && layer.Noise is { } noise && !layer.Bones.IsEmpty &&
                                 ((noise.Tilt > 0 || noise.Twist > 0) && layer.Components.HasFlag(PoseComponents.Rotation) ||
                                  noise.Slide > 0 && layer.Components.HasFlag(PoseComponents.Position)),
        MotionLayerKind.LookAt => MotionLooks.Turns(layer) && (!MotionLooks.FollowsPartner(layer) || links?.For(layer.Key) != null),
        MotionLayerKind.Link => layer.Amount > 0 && layer.Link != null && links?.For(layer.Key) != null,
        MotionLayerKind.Repeat => layer.Amount > 0 && layer.Repeat is { } repeat && MotionRepeats.Length(repeat) >= 2 &&
                                  (layer.Components & PoseComponents.All) != PoseComponents.None,
        MotionLayerKind.Spring => layer.Amount > 0 && layer.Spring != null && !layer.Bones.IsEmpty && layer.Components.HasFlag(PoseComponents.Rotation),
        MotionLayerKind.Balance => layer.Amount > 0 && layer.Balance is { } balance && (balance.Sideways || balance.FrontBack) &&
                                   layer.Components.HasFlag(PoseComponents.Position),
        MotionLayerKind.Sway => layer.Amount > 0 && layer.Sway != null && !layer.Bones.IsEmpty && layer.Components.HasFlag(PoseComponents.Rotation),
        _ => layer.Amount > 0 && layer.Components != PoseComponents.None && layer.Keys.Any(key => !key.Bones.IsEmpty),
    };

    /// <summary>
    /// Whether <paramref name="layer"/> is on with some weight: weight points holding it at 0 all along (all at 0, with one on its
    /// first frame and one on its last) leave it out. What every kind's <see cref="Active(MotionLayer, MotionLinkSet?)"/> asks first.
    /// </summary>
    internal static bool Weighted(MotionLayer layer) => layer.Enabled && (layer.Weights.IsEmpty || layer.Weights.Any(point => point.Value > 0) ||
                                                                          layer.Weights[0].Frame > layer.Start || layer.Weights[^1].Frame < layer.End);

    /// <summary>Whether any of <paramref name="layers"/> changes the motion without partner tracks.</summary>
    public static bool Changes(ImmutableArray<MotionLayer> layers) => !layers.IsDefaultOrEmpty && layers.Any(Active);

    /// <summary>Whether any of <paramref name="layers"/> changes the motion with <paramref name="links"/>.</summary>
    public static bool Changes(ImmutableArray<MotionLayer> layers, MotionLinkSet? links) =>
        !layers.IsDefaultOrEmpty && layers.Any(layer => Active(layer, links));

    /// <summary>
    /// How much of <paramref name="layer"/> applies at <paramref name="frame"/>, before its amount:
    /// 0 outside its range, rising over its fade in and falling over its fade out on a smoothstep, times its
    /// weight points there (<see cref="WeightAt"/>). Every kind takes its strength from this, through
    /// <see cref="Weight"/> or directly.
    /// Past a layer reaching the end of a motion <paramref name="end"/> frames long (<see cref="ReachesEnd"/>),
    /// its last frame's weight holds.
    /// </summary>
    public static float Fade(MotionLayer layer, float frame, float end = 0)
    {
        if (frame > layer.End && ReachesEnd(layer, end))
            frame = layer.End;
        if (!float.IsFinite(frame) || frame < layer.Start - FrameTolerance || frame > layer.End + FrameTolerance)
            return 0;
        frame = Math.Clamp(frame, layer.Start, layer.End);
        var weight = 1f;
        if (layer.FadeIn > 0 && frame < layer.Start + layer.FadeIn)
            weight = AnimationPoseRules.SmoothStep((frame - layer.Start) / layer.FadeIn);
        if (layer.FadeOut > 0 && frame > layer.End - layer.FadeOut)
            weight = Math.Min(weight, AnimationPoseRules.SmoothStep((layer.End - frame) / layer.FadeOut));
        weight = Math.Clamp(weight, 0, 1);
        return layer.Weights.IsEmpty || weight <= 0 ? weight : weight * WeightAt(layer, frame);
    }

    /// <summary>
    /// The layer's own weight at <paramref name="frame"/> from its points: 1 without any, and between two points a smoothstep
    /// from one value to the next. The line starts at full weight on the layer's first frame and ends at full weight on its last
    /// unless a point is there, so it eases from 1 to the first point and from the last point back to 1: a lone point dips the
    /// weight around itself only.
    /// </summary>
    public static float WeightAt(MotionLayer layer, float frame)
    {
        var points = layer.Weights;
        if (points.IsEmpty)
            return 1;
        if (!(frame > points[0].Frame))
            return points[0].Frame > layer.Start ? Ease(layer.Start, 1, points[0].Frame, points[0].Value, frame) : points[0].Value;
        if (frame >= points[^1].Frame)
            return layer.End > points[^1].Frame ? Ease(points[^1].Frame, points[^1].Value, layer.End, 1, frame) : points[^1].Value;
        // points[low].Frame <= frame < points[high].Frame
        int low = 0, high = points.Length - 1;
        while (high - low > 1)
        {
            var middle = (low + high) >>> 1;
            if (points[middle].Frame <= frame) low = middle;
            else high = middle;
        }
        var (from, to) = (points[low], points[high]);
        return Ease(from.Frame, from.Value, to.Frame, to.Value, frame);
    }

    /// <summary>From <paramref name="a"/> on frame <paramref name="from"/> to <paramref name="b"/> on a later frame <paramref name="to"/>, on a smoothstep; held outside them.</summary>
    private static float Ease(float from, float a, float to, float b, float frame)
    {
        var t = (frame - from) / (to - from);
        t = t > 0 ? Math.Min(t, 1) : 0;
        return Math.Clamp(a + (b - a) * AnimationPoseRules.SmoothStep(t), 0, 1);
    }

    /// <summary>
    /// <paramref name="points"/> as <paramref name="layer"/> may hold them: inside its frames, by frame, the last given for a
    /// frame kept, values within 0 to 1 and at most <see cref="MaxWeights"/>. The layer's own points when they already are.
    /// Edits that change a layer's frames pass its points through this, so a layer never holds points it can't.
    /// </summary>
    public static ImmutableArray<MotionWeight> FitWeights(MotionLayer layer, IEnumerable<MotionWeight> points)
    {
        var fitted = new SortedDictionary<int, float>();
        foreach (var point in points)
            if (point != null && point.Frame >= layer.Start && point.Frame <= layer.End && float.IsFinite(point.Value))
                fitted[point.Frame] = Math.Clamp(point.Value, 0, 1);
        ImmutableArray<MotionWeight> kept = [.. fitted.Take(MaxWeights).Select(pair => new MotionWeight(pair.Key, pair.Value))];
        return kept.SequenceEqual(layer.Weights) ? layer.Weights : kept;
    }

    /// <summary>
    /// Whether <paramref name="layer"/> reaches the end of a motion <paramref name="end"/> frames long (0 when
    /// unknown). A timeline's length may be a frame off its motion's, from the file's header, or a fraction
    /// short of it (<see cref="TimelineDocument.SameLength"/>), so a layer ending at most a frame before the
    /// motion's length in whole frames reaches it: it goes on over the motion's last samples, and a looping
    /// clip starts over with the layer as it ended rather than popping back to the unedited motion.
    /// </summary>
    public static bool ReachesEnd(MotionLayer layer, float end) =>
        end > 0 && float.IsFinite(end) && layer.End >= Math.Round(end, MidpointRounding.AwayFromZero) - 1;

    /// <summary>
    /// How strongly an offset or pose layer applies its keys at <paramref name="frame"/>: an offset's strength
    /// (its amount, up to <see cref="MaxAmount"/>), a pose's blend toward the keyed pose or a pin's pull, through its fades and
    /// weight points (<see cref="Fade"/>).
    /// </summary>
    public static float Weight(MotionLayer layer, float frame, float end = 0)
    {
        var weight = Fade(layer, frame, end) * layer.Amount;
        return BlendsAtMostFully(layer.Kind) ? Math.Min(1, weight) : weight;
    }

    /// <summary>Whether a kind's amount is a blend of at most 1: a pose, a symmetry, a pin's or a link's pull, a fix, a smooth, a look, a repeat, a spring, a balance or a sway.</summary>
    public static bool BlendsAtMostFully(MotionLayerKind kind) =>
        kind is MotionLayerKind.Pose or MotionLayerKind.Symmetry or MotionLayerKind.Pin or MotionLayerKind.Fix or MotionLayerKind.Smooth or MotionLayerKind.LookAt or
            MotionLayerKind.Link or MotionLayerKind.Repeat or MotionLayerKind.Spring or MotionLayerKind.Balance or MotionLayerKind.Sway;

    /// <summary>Checks one layer; throws <see cref="InvalidDataException"/> naming what is wrong.</summary>
    public static void Validate(MotionLayer layer)
    {
        if (layer == null || string.IsNullOrEmpty(layer.Key) || !Enum.IsDefined(layer.Kind) || !Enum.IsDefined(layer.Interpolation) ||
            layer.Interpolation == MotionInterpolation.Custom || !Enum.IsDefined(layer.From) || layer.Markers is { } m && !Enum.IsDefined(m.Detail) ||
            layer.Fix is { } read && (!Enum.IsDefined(read.Kind) || (read.Axes & ~MotionAxes.All) != 0 || !Enum.IsDefined(read.Placement)))
            throw new InvalidDataException("A bone layer can't be read.");
        var name = string.IsNullOrWhiteSpace(layer.Name) ? "A bone layer" : $"The bone layer {layer.Name}";
        if (layer.Start < 0 || layer.End <= layer.Start || layer.End > TimelineDocument.MaxFrames)
            throw new InvalidDataException($"{name} has no frames on the timeline.");
        if (layer.FadeIn < 0 || layer.FadeOut < 0 || (long)layer.FadeIn + layer.FadeOut > layer.End - layer.Start)
            throw new InvalidDataException($"{name} fades for longer than it lasts.");
        var most = BlendsAtMostFully(layer.Kind) ? 1 : MaxAmount;
        if (!float.IsFinite(layer.Amount) || layer.Amount < 0 || layer.Amount > most)
            throw new InvalidDataException($"{name} has an amount out of range.");
        if ((layer.Components & ~PoseComponents.All) != 0)
            throw new InvalidDataException($"{name} changes an unknown part of its bones.");
        if (layer.Bones.Any(string.IsNullOrEmpty) || layer.Bones.Distinct(StringComparer.Ordinal).Count() != layer.Bones.Length)
            throw new InvalidDataException($"{name} lists a bone twice or a bone without a name.");
        if (layer.Keys.Length > MaxKeys)
            throw new InvalidDataException($"{name} has too many keys.");
        if (layer.Kind is MotionLayerKind.Symmetry or MotionLayerKind.Fix or MotionLayerKind.Idle or MotionLayerKind.Swing or MotionLayerKind.Smooth or
                MotionLayerKind.Noise or MotionLayerKind.Link or MotionLayerKind.Repeat or MotionLayerKind.Spring or MotionLayerKind.Balance or MotionLayerKind.Sway &&
                !layer.Keys.IsEmpty)
            throw new InvalidDataException($"{name} can't have keys.");
        if (layer.Kind != MotionLayerKind.Pin && !layer.Pins.IsEmpty)
            throw new InvalidDataException($"{name} can't have pins.");
        if (layer.Kind == MotionLayerKind.Fix && layer.Fix == null)
            throw new InvalidDataException($"{name} has no fix.");
        if (layer.Kind != MotionLayerKind.Fix && layer.Fix != null)
            throw new InvalidDataException($"{name} can't have a fix.");
        if (layer.Kind != MotionLayerKind.Idle && layer.Idle != null)
            throw new InvalidDataException($"{name} can't have idle motion.");
        if (layer.Kind != MotionLayerKind.Offset && layer.Markers != null)
            throw new InvalidDataException($"{name} can't have markers.");
        if (layer.Markers is { Reach: < 1 or > MotionMarkers.MaxReach })
            throw new InvalidDataException($"{name} has an invalid reach.");
        if (layer.Kind == MotionLayerKind.Swing && layer.Swing == null)
            throw new InvalidDataException($"{name} has no swing.");
        if (layer.Kind != MotionLayerKind.Swing && layer.Swing != null)
            throw new InvalidDataException($"{name} can't have a swing.");
        if (layer.Swing is { } swing)
            MotionSwings.Validate(swing, name);
        if (layer.Kind == MotionLayerKind.Noise && layer.Noise == null)
            throw new InvalidDataException($"{name} has no noise.");
        if (layer.Kind != MotionLayerKind.Noise && layer.Noise != null)
            throw new InvalidDataException($"{name} can't have a noise.");
        if (layer.Noise is { } noise)
            MotionNoises.Validate(noise, name);
        if (layer.Kind == MotionLayerKind.LookAt && layer.Look == null)
            throw new InvalidDataException($"{name} has nothing to look at.");
        if (layer.Kind != MotionLayerKind.LookAt && layer.Look != null)
            throw new InvalidDataException($"{name} can't look at anything.");
        if (layer.Look is { } look)
        {
            MotionLooks.Validate(look, name);
            if (layer.Bones.Any(bone => !MotionLooks.Chain.Contains(bone)))
                throw new InvalidDataException($"{name} turns a bone other than the head, neck and upper back.");
            if (look.Follow != null && !layer.Keys.IsEmpty)
                throw new InvalidDataException($"{name} follows a bone, so it can't have keys.");
        }
        if (layer.Kind == MotionLayerKind.Link && layer.Link == null)
            throw new InvalidDataException($"{name} has no link.");
        if (layer.Kind != MotionLayerKind.Link && layer.Link != null)
            throw new InvalidDataException($"{name} can't have a link.");
        if (layer.Link is { } link)
        {
            if (string.IsNullOrEmpty(link.Bone) || string.IsNullOrEmpty(link.PartnerBone))
                throw new InvalidDataException($"{name} links a bone without a name.");
            if (!float.IsFinite(link.Bend) || link.Bend is < -180 or > 180)
                throw new InvalidDataException($"{name} has a bend out of range.");
            if (!link.Holds.IsEmpty && link.Bone != PinChains.Hips)
                throw new InvalidDataException($"{name} holds bones, but only a link on the hips can.");
            if (link.Holds.Any(string.IsNullOrEmpty) || link.Holds.Distinct(StringComparer.Ordinal).Count() != link.Holds.Length)
                throw new InvalidDataException($"{name} holds a bone twice or a bone without a name.");
        }
        if (layer.Kind == MotionLayerKind.Repeat && layer.Repeat == null)
            throw new InvalidDataException($"{name} has nothing to repeat.");
        if (layer.Kind != MotionLayerKind.Repeat && layer.Repeat != null)
            throw new InvalidDataException($"{name} can't have a repeat.");
        if (layer.Repeat is { } repeat)
            MotionRepeats.Validate(repeat, name);
        if (layer.Kind == MotionLayerKind.Spring && layer.Spring == null)
            throw new InvalidDataException($"{name} has no spring.");
        if (layer.Kind != MotionLayerKind.Spring && layer.Spring != null)
            throw new InvalidDataException($"{name} can't have a spring.");
        if (layer.Spring is { } spring)
        {
            MotionSprings.Validate(spring, name);
            // Springing the root or the body would swing the whole character.
            if (layer.Bones.Any(bone => bone is "n_root" or PinChains.Body))
                throw new InvalidDataException($"{name} springs the root or the body, which would move the whole character.");
        }
        if (layer.Kind == MotionLayerKind.Balance && layer.Balance == null)
            throw new InvalidDataException($"{name} has no balance.");
        if (layer.Kind != MotionLayerKind.Balance && layer.Balance != null)
            throw new InvalidDataException($"{name} can't have a balance.");
        if (layer.Balance is { } balance)
        {
            MotionBalances.Validate(balance, name);
            // A balance always works on the hips and the feet.
            if (!layer.Bones.IsEmpty)
                throw new InvalidDataException($"{name} works on the hips and feet, so it can't list bones.");
        }
        if (layer.Kind == MotionLayerKind.Sway && layer.Sway == null)
            throw new InvalidDataException($"{name} has no sway.");
        if (layer.Kind != MotionLayerKind.Sway && layer.Sway != null)
            throw new InvalidDataException($"{name} can't have a sway.");
        if (layer.Sway is { } sway)
        {
            MotionSways.Validate(sway, name);
            // Swaying the root or the body would swing the whole character.
            if (layer.Bones.Any(bone => bone is "n_root" or PinChains.Body))
                throw new InvalidDataException($"{name} sways the root or the body, which would move the whole character.");
        }
        if (layer.Kind == MotionLayerKind.Smooth && layer.Window is < MotionSmoothing.MinWindow or > MotionSmoothing.MaxWindow)
            throw new InvalidDataException($"{name} has a window out of range.");
        if (layer.Kind != MotionLayerKind.Smooth && layer.Window != 0)
            throw new InvalidDataException($"{name} can't have a window.");
        if (layer.Weights.Length > MaxWeights)
            throw new InvalidDataException($"{name} has too many weight points.");
        // A match or a close loop is anchored to the clip's ends, its stored frames only a length: its points would have no place.
        if (!layer.Weights.IsEmpty && layer.Fix?.Kind is MotionFixKind.MatchLoop or MotionFixKind.CloseLoop)
            throw new InvalidDataException($"{name} can't have weight points.");
        var before = int.MinValue;
        foreach (var point in layer.Weights)
        {
            if (point == null || point.Frame <= before || point.Frame < layer.Start || point.Frame > layer.End)
                throw new InvalidDataException($"{name} has weight points out of order or outside it.");
            before = point.Frame;
            if (!float.IsFinite(point.Value) || point.Value is < 0 or > 1)
                throw new InvalidDataException($"{name} has a weight out of range.");
        }
        if (layer.Idle is { } idle)
            MotionIdles.Validate(idle, name);
        if (layer.Fix is { Kind: MotionFixKind.MatchLoop } match)
        {
            if (match.LoopFirst is not { IsEmpty: false } first || match.LoopSecond is not { } second)
                throw new InvalidDataException($"{name} has no loop pose.");
            if (first.Count > MaxKeyBones || first.Count != second.Count || first.Any(pair => !second.ContainsKey(pair.Key)) ||
                first.Concat(second).Any(pair => string.IsNullOrEmpty(pair.Key) || !IsTransform(pair.Value) || !InScale(pair.Value)))
                throw new InvalidDataException($"{name} has a loop pose that can't be read.");
            if (match.Fade is < 0 or > LoopFades.Most)
                throw new InvalidDataException($"{name} has a game fade out of range.");
        }
        if (layer.Fix is { Kind: not MotionFixKind.MatchLoop, Fade: not 0 })
            throw new InvalidDataException($"{name} can't have a game fade.");
        if (layer.Fix is { Kind: MotionFixKind.HandShape, Hand: null })
            throw new InvalidDataException($"{name} has no hand.");
        if (layer.Fix is { Kind: not MotionFixKind.HandShape, Hand: not null })
            throw new InvalidDataException($"{name} can't have a hand.");
        if (layer.Fix is { Kind: not MotionFixKind.ResetRoot, Axes: not MotionAxes.None })
            throw new InvalidDataException($"{name} can't have axes.");
        if (layer.Fix is { Kind: not MotionFixKind.CloseLoop, Placement: not JoinPlacement.Centered })
            throw new InvalidDataException($"{name} can't have a placement.");
        if (layer.Fix?.Hand is { } hand)
        {
            if (!Enum.IsDefined(hand.Side) || string.IsNullOrEmpty(hand.Shape) || hand.Shape.Length > MaxHandShapeName)
                throw new InvalidDataException($"{name} has a hand whose side or shape can't be read.");
            if (!float.IsFinite(hand.Curl) || !float.IsFinite(hand.Spread) || Math.Abs(hand.Curl) > 1 || Math.Abs(hand.Spread) > 1)
                throw new InvalidDataException($"{name} has a hand curl or spread out of range.");
            if (hand.Turns is { } turns && (turns.Count > MaxHandTurns || turns.Any(pair => string.IsNullOrEmpty(pair.Key) ||
                    !float.IsFinite(pair.Value.LengthSquared()) || pair.Value.LengthSquared() < 1e-6f)))
                throw new InvalidDataException($"{name} has hand turns that can't be read.");
        }
        var pinned = new Dictionary<string, MotionPin>(StringComparer.Ordinal);
        foreach (var pin in layer.Pins)
        {
            if (string.IsNullOrEmpty(pin?.Bone) || !pinned.TryAdd(pin.Bone, pin))
                throw new InvalidDataException($"{name} pins a bone twice or a bone without a name.");
            if (!Enum.IsDefined(pin.Sensitivity))
                throw new InvalidDataException($"{name} has a pin whose sensitivity can't be read.");
            if (!float.IsFinite(pin.Bend) || pin.Bend is < -180 or > 180)
                throw new InvalidDataException($"{name} has a pin whose bend is out of range.");
        }
        var previous = -1;
        foreach (var key in layer.Keys)
        {
            if (key?.Bones == null || key.Frame <= previous || key.Frame > TimelineDocument.MaxFrames)
                throw new InvalidDataException($"{name} has keys out of order.");
            previous = key.Frame;
            if (key.Bones.Count > MaxKeyBones)
                throw new InvalidDataException($"{name} has a key with too many bones.");
            foreach (var (bone, value) in key.Bones)
            {
                if (string.IsNullOrEmpty(bone) || !IsTransform(value))
                    throw new InvalidDataException($"{name} has a key with a value that is not a transform.");
                if (!InScale(value))
                    throw new InvalidDataException($"{name} has a key whose scale is out of range.");
                if (layer.Kind == MotionLayerKind.Pin && (!pinned.TryGetValue(bone, out var pin) || pin.Planted))
                    throw new InvalidDataException($"{name} has a key for a bone it doesn't pin by keys.");
                if (layer.Kind == MotionLayerKind.LookAt && bone != MotionLooks.KeyBone)
                    throw new InvalidDataException($"{name} has a key for a bone other than the head.");
            }
            if (key.Moved.Any(bone => !key.Bones.ContainsKey(bone)))
                throw new InvalidDataException($"{name} moves a bone its key has no value for.");
            if (key.Ease is { } ease && !Enum.IsDefined(ease) || key.Curve is { Valid: false } ||
                key.Ease == MotionInterpolation.Custom && key.Curve == null)
                throw new InvalidDataException($"{name} has a key with an ease that can't be read.");
        }
    }

    /// <summary>Checks a document's layers: each one, how many there are, and that their keys are unique.</summary>
    public static void Validate(IReadOnlyList<MotionLayer> layers)
    {
        if (layers.Count > MaxLayers)
            throw new InvalidDataException($"An animation can have at most {MaxLayers} bone layers.");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var layer in layers)
        {
            Validate(layer);
            if (!keys.Add(layer.Key))
                throw new InvalidDataException("Two bone layers share a key.");
        }
    }

    /// <summary>
    /// Applies the active layers to <paramref name="clip"/> in place, in order, each to the motion the
    /// ones before it made, then keeps every bone's rotation on its previous sample's hemisphere.
    /// Match startup to loop fixes apply after every other layer, in their own order, and never to a looping clip. Close loop
    /// fixes apply after those, in their own order, and only to a looping clip. On a looping clip,
    /// layers covering it wrap their keys round (<see cref="Wraps"/>), and layers reaching its end cover its
    /// last samples (<see cref="ReachesEnd"/>). On a clip whose hips' move goes onto <c>n_hara</c> (<see cref="MotionClip.HipsOnHara"/>),
    /// what each layer moves the hips by moves <c>n_hara</c> instead, and the clip's own hips stay as they were.
    /// Link layers, and look-at layers that follow a partner, apply with their partner tracks in <paramref name="links"/>; without
    /// one such a layer is left out. Throws for a layer naming a bone the clip's skeleton does not have.
    /// </summary>
    public static void Apply(MotionClip clip, IReadOnlyList<MotionLayer> layers, CancellationToken token = default, MotionLinkSet? links = null)
    {
        Validate(layers);
        var index = Index(clip.Skeleton);
        var end = clip.Frame(clip.Samples - 1);
        var loop = clip.Loops ? end : 0;
        // On a clip whose hips' move goes onto n_hara, each layer's move of the hips does, before the next layer reads the pose.
        (int Hara, int Hips) hips = clip.HipsOnHara ? HaraAndHips(clip.Skeleton, index) : (-1, -1);
        var hipsBefore = hips.Hips >= 0 ? new Vector3[clip.Samples] : null;
        for (var at = 0; at < layers.Count; at++)
        {
            var layer = layers[at];
            token.ThrowIfCancellationRequested();
            if (!Active(layer, links))
                continue;
            // A pin or link layer keeps a bone limit it never uses.
            var mask = layer.Kind is MotionLayerKind.Pin or MotionLayerKind.Link ? null : Mask(layer, index);
            if (layer.Fix?.Kind is MotionFixKind.MatchLoop or MotionFixKind.CloseLoop)
                continue;
            if (hipsBefore != null)
                HoldHips(clip, hips.Hips, hipsBefore);
            switch (layer.Kind)
            {
                case MotionLayerKind.Offset:
                    ApplyOffset(clip, layer, Tracks(layer, index, mask, false), WrapOf(layer, loop), end, token);
                    break;
                case MotionLayerKind.Amplitude:
                    ApplyAmplitude(clip, layer, (IReadOnlyCollection<int>?)mask ?? Enumerable.Range(0, clip.Bones).ToArray(), end, token);
                    break;
                case MotionLayerKind.Pose:
                    ApplyPose(clip, layer, Tracks(layer, index, mask, false), Tracks(layer, index, mask, true), WrapOf(layer, loop), end, token);
                    break;
                case MotionLayerKind.Symmetry:
                    ApplySymmetry(clip, layers, at, end, token);
                    break;
                case MotionLayerKind.Pin:
                    ApplyPin(clip, layer, index, WrapOf(layer, loop), end, token);
                    break;
                case MotionLayerKind.Fix when layer.Fix!.Kind == MotionFixKind.HandShape:
                    MotionFixes.ApplyHand(clip, layer, end, token);
                    break;
                case MotionLayerKind.Fix when layer.Fix!.Kind == MotionFixKind.ResetRoot:
                    MotionFixes.ApplyResetRoot(clip, layer, end, token);
                    break;
                case MotionLayerKind.Fix:
                    MotionFixes.ApplySmooth(clip, layer, (IReadOnlyCollection<int>?)mask ?? Enumerable.Range(0, clip.Bones).ToArray(), loop, end, token);
                    break;
                case MotionLayerKind.Idle:
                    ApplyIdle(clip, layer, index, mask, end, token);
                    break;
                case MotionLayerKind.Swing:
                    MotionSwings.Apply(clip, layer, index, loop, end, token);
                    break;
                case MotionLayerKind.Noise:
                    MotionNoises.Apply(clip, layer, index, loop, end, token);
                    break;
                case MotionLayerKind.LookAt:
                    MotionLooks.Apply(clip, layer, index, LookTrack(layer, index), links?.For(layer.Key), WrapOf(layer, loop), end, token);
                    break;
                case MotionLayerKind.Smooth:
                    MotionSmoothing.Apply(clip, layer, (IReadOnlyCollection<int>?)mask ?? Enumerable.Range(0, clip.Bones).ToArray(), loop, end, token);
                    break;
                case MotionLayerKind.Link:
                    ApplyLink(clip, layer, index, links!.For(layer.Key)!, end, hipsBefore != null, token);
                    break;
                case MotionLayerKind.Repeat:
                    MotionRepeats.Apply(clip, layer, (IReadOnlyCollection<int>?)mask ?? Enumerable.Range(0, clip.Bones).ToArray(), end, token);
                    break;
                case MotionLayerKind.Spring:
                    MotionSprings.Apply(clip, layer, index, loop, end, token);
                    break;
                case MotionLayerKind.Balance:
                    MotionBalances.Apply(clip, layer, index, loop, end, hipsBefore != null, token);
                    break;
                case MotionLayerKind.Sway:
                    MotionSways.Apply(clip, layer, index, loop, end, token);
                    break;
            }
            if (hipsBefore != null)
                CarryHips(clip, hips, hipsBefore);
        }
        // Matches land last, so no layer after them in the list can undo the landing. A looping clip has no loop to land on.
        foreach (var layer in layers)
            if (!clip.Loops && layer.Fix?.Kind == MotionFixKind.MatchLoop && Active(layer))
            {
                if (hipsBefore != null)
                    HoldHips(clip, hips.Hips, hipsBefore);
                MotionFixes.ApplyMatch(clip, layer, index, end, token);
                if (hipsBefore != null)
                    CarryHips(clip, hips, hipsBefore);
            }
        // Close loops then join the loop's end to its start, on whatever every other layer left there. A clip the game
        // plays once has no seam to close.
        foreach (var layer in layers)
            if (clip.Loops && layer.Fix?.Kind == MotionFixKind.CloseLoop && Active(layer))
            {
                if (hipsBefore != null)
                    HoldHips(clip, hips.Hips, hipsBefore);
                MotionFixes.ApplyClose(clip, layer, index, end, token);
                if (hipsBefore != null)
                    CarryHips(clip, hips, hipsBefore);
            }
        Continuity(clip);
    }

    /// <summary><c>n_hara</c> and the hips under it, for <see cref="MotionClip.HipsOnHara"/>; -1 for both when the skeleton lacks either or hangs the hips elsewhere.</summary>
    private static (int Hara, int Hips) HaraAndHips(SkeletonDescription skeleton, Dictionary<string, int> index) =>
        index.TryGetValue(PinChains.Body, out var hara) && index.TryGetValue(PinChains.Hips, out var hips) && skeleton.Bones[hips].Parent == hara
            ? (hara, hips)
            : (-1, -1);

    private static void HoldHips(MotionClip clip, int hips, Vector3[] before)
    {
        for (var s = 0; s < clip.Samples; s++)
            before[s] = clip.Get(s, hips).Position;
    }

    private static void CarryHips(MotionClip clip, (int Hara, int Hips) bones, Vector3[] before)
    {
        for (var s = 0; s < clip.Samples; s++)
        {
            var (hara, hips) = CarryHips(clip.Get(s, bones.Hara), clip.Get(s, bones.Hips), before[s]);
            clip.Set(s, bones.Hara, hara);
            clip.Set(s, bones.Hips, hips);
        }
    }

    /// <summary>
    /// The hips' move from <paramref name="before"/> taken onto <c>n_hara</c>: the hips go back there and <c>n_hara</c> moves as far,
    /// turned by its own rotation, so the hips and every bone below them stay where they are in model space. <c>n_hara</c>'s other
    /// children (the game's back) then go along, as they do when the game plays <c>n_hara</c>'s translation.
    /// </summary>
    private static (MotionTransform Hara, MotionTransform Hips) CarryHips(MotionTransform hara, MotionTransform hips, Vector3 before)
    {
        var move = hips.Position - before;
        if (move == Vector3.Zero)
            return (hara, hips);
        return (hara with { Position = hara.Position + Vector3.Transform(move, Quaternion.Normalize(hara.Rotation)) }, hips with { Position = before });
    }

    /// <summary>Throws when a layer names a bone <paramref name="skeleton"/> does not have.</summary>
    public static void CheckBones(IReadOnlyList<MotionLayer> layers, SkeletonDescription skeleton)
    {
        var index = Index(skeleton);
        foreach (var layer in layers)
        {
            foreach (var name in layer.Bones)
                if (!index.ContainsKey(name))
                    throw Missing(name);
            foreach (var key in layer.Keys)
                foreach (var name in key.Bones.Keys)
                    if (!index.ContainsKey(name))
                        throw Missing(name);
            if (layer.Kind == MotionLayerKind.Pin)
                PinChains.Check(skeleton, layer);
            if (layer.Kind == MotionLayerKind.LookAt)
                MotionLooks.Plan(skeleton, layer, index);
            if (layer is { Kind: MotionLayerKind.Link, Link: { } link })
                CheckLink(skeleton, index, layer, link);
        }
    }

    /// <summary>
    /// Throws when <paramref name="link"/> names a bone, top or held bone the skeleton lacks, bends up to a bone that isn't above
    /// its bone below the body, links the body or a bone above it, or holds a bone that doesn't hang below the hips.
    /// </summary>
    private static void CheckLink(SkeletonDescription skeleton, Dictionary<string, int> index, MotionLayer layer, MotionLink link)
    {
        var name = string.IsNullOrWhiteSpace(layer.Name) ? "A bone layer" : $"The bone layer {layer.Name}";
        var bone = index.TryGetValue(link.Bone, out var found) ? found : throw Missing(link.Bone);
        if (skeleton.Bones[bone].Parent < 0 || link.Bone == PinChains.Body ||
            index.TryGetValue(PinChains.Body, out var body) && PinChains.IsBelow(skeleton, body, bone))
            throw new InvalidDataException($"{name} links {AnimationPresentation.BoneName(link.Bone)}, which would move the whole character.");
        // The chain bends as a pin on the bone would: the same top and bend rules.
        PinChains.Check(skeleton, layer with { Pins = [MotionLinks.AsPin(link)] });
        foreach (var hold in link.Holds)
        {
            if (!index.ContainsKey(hold))
                throw Missing(hold);
            if (HipSolver.Candidates(skeleton, [hold]).Count == 0)
                throw new InvalidDataException($"{name} holds {AnimationPresentation.BoneName(hold)}, which doesn't hang below the hips.");
        }
    }

    /// <summary>A look-at layer's keyed spots on the head, read with no bone limit; null when it follows a bone or has none.</summary>
    private static Track? LookTrack(MotionLayer layer, Dictionary<string, int> index) =>
        layer.Look is { Follow: null } && index.TryGetValue(MotionLooks.KeyBone, out var head) ? Tracks(layer, index, null, false).GetValueOrDefault(head) : null;

    /// <summary>Bone indices by name.</summary>
    public static Dictionary<string, int> Index(SkeletonDescription skeleton)
    {
        var index = new Dictionary<string, int>(skeleton.Bones.Length, StringComparer.Ordinal);
        for (var i = 0; i < skeleton.Bones.Length; i++)
            index[skeleton.Bones[i].Name] = i;
        return index;
    }

    /// <summary>
    /// Whether <paramref name="layer"/>'s keys wrap around a loop <paramref name="loop"/> frames long (0 for
    /// a clip that plays once): the layer covers the whole loop, so after its last key each bone goes back
    /// toward its first key, which comes round again one loop later. The loop's last frame then matches its
    /// first, and the motion doesn't jump each time it starts over.
    /// </summary>
    public static bool Wraps(MotionLayer layer, float loop) => loop > 0 && layer.Start < 1 && ReachesEnd(layer, loop);

    /// <summary>The loop length <see cref="Sample"/> wraps <paramref name="layer"/>'s keys around: <paramref name="loop"/> when it wraps, else 0.</summary>
    private static float WrapOf(MotionLayer layer, float loop) => Wraps(layer, loop) ? loop : 0;

    /// <summary>
    /// A bone's keyed value at <paramref name="frame"/>: between two keys it goes the earlier key's way
    /// (<see cref="KeyEase"/>), before the first and after the last it holds, or on a loop the layer
    /// covers (<see cref="Wraps"/>) goes the last key's way round to the first. Null when no key has the bone.
    /// </summary>
    public static MotionTransform? KeyValue(MotionLayer layer, string bone, float frame, float loop = 0)
    {
        var frames = new List<int>();
        var values = new List<MotionTransform>();
        var curves = new List<MotionCurve?>();
        foreach (var key in layer.Keys)
            if (key.Bones.TryGetValue(bone, out var value))
            {
                frames.Add(key.Frame);
                values.Add(Normalized(MotionTransform.From(value)));
                curves.Add(CurveOf(layer, key));
            }
        return frames.Count == 0 ? null : Sample(frames.ToArray(), values.ToArray(), curves.ToArray(), frame, WrapOf(layer, loop));
    }

    /// <summary>How the bones go from <paramref name="key"/> to their next keys: its own way, else the layer's.</summary>
    public static MotionInterpolation KeyEase(MotionLayer layer, MotionKey key) => key.Ease ?? layer.Interpolation;

    /// <summary>The curve from <paramref name="key"/> to the next one; null holds the value until then.</summary>
    public static MotionCurve? CurveOf(MotionLayer layer, MotionKey key) => CurveOf(KeyEase(layer, key), key.Curve);

    /// <summary>The curve of <paramref name="mode"/>, with <paramref name="custom"/> for a key's own; null for a hold.</summary>
    public static MotionCurve? CurveOf(MotionInterpolation mode, MotionCurve? custom) => mode switch
    {
        MotionInterpolation.Step => null,
        MotionInterpolation.Smooth => MotionCurve.Smooth,
        MotionInterpolation.EaseIn => MotionCurve.EaseIn,
        MotionInterpolation.EaseOut => MotionCurve.EaseOut,
        MotionInterpolation.Custom when custom is { Valid: true } => custom,
        _ => MotionCurve.Linear,
    };

    /// <summary>
    /// The value at <paramref name="frame"/> on a bone's keys (frames ascending, at least one), each
    /// stretch between two keys following the earlier key's curve (null holds it). With a
    /// <paramref name="loop"/> length, the stretch past the last key runs on its curve to the first key
    /// one loop later, and frames before the first key are the end of that stretch.
    /// </summary>
    internal static MotionTransform Sample(int[] frames, MotionTransform[] values, MotionCurve?[] curves, float frame, float loop = 0)
    {
        if (frames.Length > 1 && loop > 0 && frames[^1] - frames[0] < loop && (frame < frames[0] || frame > frames[^1]))
        {
            var since = frame > frames[^1] ? frame - frames[^1] : frame + loop - frames[^1];
            var across = curves[^1] is { } wrap ? wrap.Ease(since / (frames[0] + loop - frames[^1])) : 0;
            return Eased(values[^1], values[0], across);
        }
        if (frames.Length == 1 || frame <= frames[0])
            return values[0];
        if (frame >= frames[^1])
            return values[^1];
        // frames[low] <= frame < frames[high]
        int low = 0, high = frames.Length - 1;
        while (high - low > 1)
        {
            var middle = (low + high) >>> 1;
            if (frames[middle] <= frame) low = middle;
            else high = middle;
        }
        var t = (frame - frames[low]) / (frames[high] - frames[low]);
        t = curves[low] is { } curve ? curve.Ease(t) : 0;
        return Eased(values[low], values[high], t);
    }

    /// <summary>
    /// <see cref="Lerp"/> along a key's curve. A curve that overshoots or winds up leaves 0 to 1, which
    /// could carry a scale past zero; it stays within the range a key may hold.
    /// </summary>
    private static MotionTransform Eased(MotionTransform from, MotionTransform to, float t)
    {
        var value = Lerp(from, to, t);
        return t is >= 0 and <= 1 ? value : value with { Scale = Vector3.Clamp(value.Scale, new Vector3(MinScale), new Vector3(MaxScale)) };
    }

    /// <summary>Uniform interpolation: positions and scales on a line, rotations the short way round.</summary>
    public static MotionTransform Lerp(MotionTransform from, MotionTransform to, float t) =>
        new(Vector3.Lerp(from.Position, to.Position, t), Slerp(from.Rotation, to.Rotation, t), Vector3.Lerp(from.Scale, to.Scale, t));

    public static Quaternion Slerp(Quaternion from, Quaternion to, float t)
    {
        from = Quaternion.Normalize(from);
        return Quaternion.Normalize(Quaternion.Slerp(from, Align(Quaternion.Normalize(to), from), t));
    }

    /// <summary><paramref name="q"/>, or its negation when that is closer to <paramref name="reference"/>: the same orientation.</summary>
    public static Quaternion Align(Quaternion q, Quaternion reference) => Quaternion.Dot(q, reference) < 0 ? -q : q;

    /// <summary>The rotation turned <paramref name="k"/> times as far, the short way: 0 is none, 1 itself, 2 twice as far.</summary>
    public static Quaternion Pow(Quaternion q, float k)
    {
        q = Quaternion.Normalize(q);
        if (q.W < 0)
            q = -q;
        var sin = MathF.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z);
        if (sin < 1e-6f)
            return Quaternion.Normalize(new Quaternion(q.X * k, q.Y * k, q.Z * k, 1));
        var angle = MathF.Atan2(sin, q.W) * k;
        var scale = MathF.Sin(angle) / sin;
        return new Quaternion(q.X * scale, q.Y * scale, q.Z * scale, MathF.Cos(angle));
    }

    /// <summary>Every bone's model-space transform from its local one.</summary>
    public static MotionTransform[] ModelSpace(SkeletonDescription skeleton, ReadOnlySpan<MotionTransform> local)
    {
        if (local.Length != skeleton.Bones.Length)
            throw new InvalidDataException("The pose does not match the skeleton.");
        var model = new MotionTransform[local.Length];
        for (var b = 0; b < local.Length; b++)
        {
            var parent = skeleton.Bones[b].Parent;
            model[b] = parent < 0 ? local[b] : Compose(model[parent], local[b]);
        }
        return model;
    }

    /// <summary>Every bone's local transform from its model-space one: the inverse of <see cref="ModelSpace"/>.</summary>
    public static MotionTransform[] LocalSpace(SkeletonDescription skeleton, ReadOnlySpan<MotionTransform> model)
    {
        if (model.Length != skeleton.Bones.Length)
            throw new InvalidDataException("The pose does not match the skeleton.");
        var local = new MotionTransform[model.Length];
        for (var b = 0; b < model.Length; b++)
        {
            var parent = skeleton.Bones[b].Parent;
            local[b] = parent < 0 ? model[b] : Relative(model[parent], model[b]);
        }
        return local;
    }

    /// <summary>A child's model transform from its parent's and its own local one.</summary>
    public static MotionTransform Compose(MotionTransform parent, MotionTransform local) =>
        new(parent.Position + Vector3.Transform(local.Position, parent.Rotation),
            Quaternion.Normalize(parent.Rotation * local.Rotation), parent.Scale * local.Scale);

    /// <summary>A child's local transform from its parent's and its own model one.</summary>
    public static MotionTransform Relative(MotionTransform parent, MotionTransform model)
    {
        var inverse = Quaternion.Inverse(Quaternion.Normalize(parent.Rotation));
        return new(Vector3.Transform(model.Position - parent.Position, inverse),
            Quaternion.Normalize(inverse * model.Rotation), Divide(model.Scale, parent.Scale));
    }

    /// <summary>Flips rotations onto the hemisphere of the bone's previous sample, so compression never goes the long way.</summary>
    public static void Continuity(MotionClip clip)
    {
        for (var b = 0; b < clip.Bones; b++)
        {
            var previous = clip.Get(0, b).Rotation;
            for (var i = 1; i < clip.Samples; i++)
            {
                var value = clip.Get(i, b);
                if (Quaternion.Dot(previous, value.Rotation) < 0)
                {
                    value = value with { Rotation = -value.Rotation };
                    clip.Set(i, b, value);
                }
                previous = value.Rotation;
            }
        }
    }

    private static void ApplyOffset(MotionClip clip, MotionLayer layer, Dictionary<int, Track> tracks, float loop, float end, CancellationToken token)
    {
        if (tracks.Count == 0)
            return;
        var rotate = layer.Components.HasFlag(PoseComponents.Rotation);
        var move = layer.Components.HasFlag(PoseComponents.Position);
        var scale = layer.Components.HasFlag(PoseComponents.Scale);
        // Model rotations are only needed up to the last edited bone: parents come before children.
        var last = tracks.Keys.Max();
        var model = new Quaternion[last + 1];
        var bones = clip.Skeleton.Bones;
        for (var i = 0; i < clip.Samples; i++)
        {
            if ((i & 63) == 0)
                token.ThrowIfCancellationRequested();
            var frame = clip.Frame(i);
            var strength = Weight(layer, frame, end);
            if (strength <= 0)
                continue;
            for (var b = 0; b <= last; b++)
            {
                var local = clip.Get(i, b);
                var parent = bones[b].Parent;
                var parentModel = parent >= 0 ? model[parent] : Quaternion.Identity;
                if (tracks.TryGetValue(b, out var track))
                {
                    local = OffsetBone(local, parentModel, Sample(track.Frames, track.Values, track.Curves, frame, loop), strength, rotate, move, scale);
                    clip.Set(i, b, local);
                }
                model[b] = parent >= 0 ? Quaternion.Normalize(parentModel * local.Rotation) : Quaternion.Normalize(local.Rotation);
            }
        }
    }

    /// <summary>
    /// An offset's change to one bone at <paramref name="strength"/>: its turn after the bone's own, its
    /// move in character space under a parent turned <paramref name="parentModel"/>, its scale factor.
    /// </summary>
    private static MotionTransform OffsetBone(MotionTransform local, Quaternion parentModel, MotionTransform delta, float strength,
        bool rotate, bool move, bool scale)
    {
        if (rotate)
            local = local with { Rotation = Quaternion.Normalize(local.Rotation * Pow(delta.Rotation, strength)) };
        // The move is in character space; in the parent's space it turns with the parent.
        if (move && delta.Position != Vector3.Zero)
            local = local with { Position = local.Position + Vector3.Transform(delta.Position * strength, Quaternion.Inverse(parentModel)) };
        if (scale && delta.Scale != Vector3.One)
            local = local with { Scale = local.Scale * Vector3.Max(new Vector3(MinScale), Vector3.Lerp(Vector3.One, delta.Scale, strength)) };
        return local;
    }

    /// <summary>A pose key's pull on one bone at <paramref name="weight"/>: its rotation and scale blended toward the key's.</summary>
    private static MotionTransform PoseBone(MotionTransform value, MotionTransform target, float weight, bool rotate, bool scale)
    {
        if (rotate)
            value = value with { Rotation = Slerp(value.Rotation, target.Rotation, weight) };
        if (scale)
            value = value with { Scale = Vector3.Lerp(value.Scale, target.Scale, weight) };
        return value;
    }

    /// <summary>
    /// One pose with the offset, pose, symmetry, keyed pin, Replace hand shape, root reset, idle, swing, noise and look-at layers applied at <paramref name="frame"/>, in order, the way
    /// <see cref="Apply"/> applies them to a sample there. Amplitudes are left out: they scale motion
    /// around its average over their section, which one pose does not have, and so are smooth motions, which average the frames
    /// round it, repeats, which play other frames of the motion under them, springs and sways, which follow the motion up to the frame, balances,
    /// which find the planted feet and average the hips' move over the frames round it, and a hand shape keeping finger motion; planted pins
    /// and the other fixes too, as finding where a bone is down or what its neighbours do takes the whole clip. The Bones window turns
    /// bones in the space the layers leave the bones above them in. <paramref name="loop"/> is the
    /// clip's length in frames when it loops, else 0; <paramref name="end"/> its length in frames, 0 when
    /// unknown. <paramref name="trackedBones"/> are the bones the clip has tracks for, as a decode gives them
    /// (<see cref="MotionClip.SourceTracks"/>), so symmetry layers skip the same pairs they do over the clip.
    /// <paramref name="hipsOnHara"/> is the clip's <see cref="MotionClip.HipsOnHara"/>. Link layers, and look-at layers that follow a
    /// partner, apply with their partner tracks in <paramref name="links"/>, as <see cref="Apply"/> applies them; without one such a
    /// layer is left out.
    /// Throws for a layer naming a bone the skeleton does not have.
    /// </summary>
    public static MotionTransform[] ApplyAt(SkeletonDescription skeleton, ReadOnlySpan<MotionTransform> local, IReadOnlyList<MotionLayer> layers,
        float frame, float loop = 0, float end = 0, IReadOnlyCollection<short>? trackedBones = null, bool hipsOnHara = false, MotionLinkSet? links = null)
    {
        if (local.Length != skeleton.Bones.Length)
            throw new InvalidDataException("The pose does not match the skeleton.");
        var pose = local.ToArray();
        var index = Index(skeleton);
        var model = new Quaternion[pose.Length];
        MotionTransform[]? rest = null;
        (int Hara, int Hips) hips = hipsOnHara ? HaraAndHips(skeleton, index) : (-1, -1);
        var hipsBefore = hips.Hips >= 0 ? pose[hips.Hips].Position : Vector3.Zero;
        for (var at = 0; at < layers.Count; at++)
        {
            // The layer before's move of the hips goes onto n_hara before this one reads the pose.
            if (hips.Hips >= 0)
            {
                (pose[hips.Hara], pose[hips.Hips]) = CarryHips(pose[hips.Hara], pose[hips.Hips], hipsBefore);
                hipsBefore = pose[hips.Hips].Position;
            }
            var layer = layers[at];
            if (!Active(layer, links))
                continue;
            if (layer.Fix?.Kind == MotionFixKind.HandShape)
            {
                MotionFixes.ApplyHandAt(skeleton, pose, layer, Weight(layer, frame, end));
                continue;
            }
            if (layer.Fix?.Kind == MotionFixKind.ResetRoot)
            {
                MotionFixes.ApplyResetRootAt(skeleton, pose, layer, Weight(layer, frame, end));
                continue;
            }
            if (layer.Kind == MotionLayerKind.Swing)
            {
                MotionSwings.ApplyAt(skeleton, pose, layer, MotionSwings.Plan(skeleton, layer, index), frame, loop, Weight(layer, frame, end), end);
                continue;
            }
            if (layer.Kind == MotionLayerKind.Noise)
            {
                MotionNoises.ApplyAt(skeleton, pose, layer, MotionNoises.Plan(skeleton, layer, index), frame, loop, Weight(layer, frame, end), end);
                continue;
            }
            if (layer.Kind == MotionLayerKind.LookAt)
            {
                MotionLooks.ApplyAt(skeleton, pose, layer, MotionLooks.Plan(skeleton, layer, index), LookTrack(layer, index), links?.For(layer.Key), frame,
                    WrapOf(layer, loop), Weight(layer, frame, end));
                continue;
            }
            if (layer.Kind is MotionLayerKind.Amplitude or MotionLayerKind.Fix or MotionLayerKind.Smooth or MotionLayerKind.Repeat or MotionLayerKind.Spring or
                MotionLayerKind.Balance or MotionLayerKind.Sway)
                continue;
            if (layer.Kind == MotionLayerKind.Symmetry)
            {
                var copy = Weight(layer, frame, end);
                if (copy > 0)
                {
                    rest ??= MotionSymmetry.RestModel(skeleton);
                    var plan = MotionSymmetry.Plan(skeleton, layer, MotionSymmetry.Animated(skeleton, trackedBones, layers, at));
                    MotionSymmetry.Mirror(skeleton, pose, plan, rest, copy, layer.Components);
                }
                continue;
            }
            if (layer.Kind == MotionLayerKind.Pin)
            {
                // Keyed pins only: planted ones need the whole clip to find where the bone is down, so the bake does them.
                var pull = Weight(layer, frame, end);
                if (pull <= 0)
                    continue;
                var keyed = Tracks(layer, index, null, false);
                var space = ModelSpace(skeleton, pose);
                var around = WrapOf(layer, loop);
                foreach (var resolved in ResolvePins(skeleton, layer, index))
                    if (!resolved.Pin.Planted && keyed.TryGetValue(resolved.Chain.End, out var pinTrack))
                        PinSolver.Pin(skeleton, pose, space, resolved.Chain, resolved.Pin, resolved.RelativeTo,
                            Sample(pinTrack.Frames, pinTrack.Values, pinTrack.Curves, frame, around), pull, resolved.RestNormal);
                continue;
            }
            if (layer.Kind == MotionLayerKind.Link)
            {
                // A hips link's move of the hips goes onto n_hara with the next layer's carry, as every layer's does.
                var follow = Weight(layer, frame, end);
                if (follow <= 0 || links?.For(layer.Key) is not { } linkTrack)
                    continue;
                if (layer.Link!.Bone == PinChains.Hips)
                    HipsLinkAt(skeleton, pose, ModelSpace(skeleton, pose), ResolveHipsLink(skeleton, layer.Link, index), linkTrack, frame, follow, hips.Hips >= 0);
                else if (ResolveLink(skeleton, layer.Link, index) is { } resolvedLink)
                    LinkAt(skeleton, pose, ModelSpace(skeleton, pose), resolvedLink, linkTrack, frame, follow);
                continue;
            }
            if (layer.Kind == MotionLayerKind.Idle)
            {
                MotionIdles.Apply(pose, layer, index, Mask(layer, index), frame, Weight(layer, frame, end));
                continue;
            }
            var rotate = layer.Components.HasFlag(PoseComponents.Rotation);
            var move = layer.Components.HasFlag(PoseComponents.Position);
            var scale = layer.Components.HasFlag(PoseComponents.Scale);
            var mask = Mask(layer, index);
            var wrap = WrapOf(layer, loop);
            if (layer.Kind == MotionLayerKind.Offset)
            {
                var strength = Weight(layer, frame, end);
                var tracks = Tracks(layer, index, mask, false);
                if (strength <= 0 || tracks.Count == 0)
                    continue;
                var last = tracks.Keys.Max();
                for (var b = 0; b <= last; b++)
                {
                    var parent = skeleton.Bones[b].Parent;
                    var parentModel = parent >= 0 ? model[parent] : Quaternion.Identity;
                    if (tracks.TryGetValue(b, out var track))
                        pose[b] = OffsetBone(pose[b], parentModel, Sample(track.Frames, track.Values, track.Curves, frame, wrap), strength, rotate, move, scale);
                    model[b] = parent >= 0 ? Quaternion.Normalize(parentModel * pose[b].Rotation) : Quaternion.Normalize(pose[b].Rotation);
                }
                continue;
            }
            var weight = Weight(layer, frame, end);
            if (weight <= 0)
                continue;
            if (rotate || scale)
                foreach (var (b, track) in Tracks(layer, index, mask, false))
                    pose[b] = PoseBone(pose[b], Sample(track.Frames, track.Values, track.Curves, frame, wrap), weight, rotate, scale);
            if (move)
                foreach (var (b, track) in Tracks(layer, index, mask, true))
                    pose[b] = pose[b] with { Position = Vector3.Lerp(pose[b].Position, Sample(track.Frames, track.Values, track.Curves, frame, wrap).Position, weight) };
        }
        if (hips.Hips >= 0)
            (pose[hips.Hara], pose[hips.Hips]) = CarryHips(pose[hips.Hara], pose[hips.Hips], hipsBefore);
        return pose;
    }

    /// <summary>An idle-motion layer over the clip, sample by sample (see <see cref="MotionIdles.Apply"/>); only the bones it moves are read and written.</summary>
    private static void ApplyIdle(MotionClip clip, MotionLayer layer, Dictionary<string, int> index, HashSet<int>? mask, float end, CancellationToken token)
    {
        var moved = MotionIdles.Moved(layer).Where(index.ContainsKey).Select(bone => index[bone]).ToArray();
        if (moved.Length == 0)
            return;
        var pose = new MotionTransform[clip.Bones];
        for (var i = 0; i < clip.Samples; i++)
        {
            if ((i & 63) == 0)
                token.ThrowIfCancellationRequested();
            var frame = clip.Frame(i);
            var weight = Weight(layer, frame, end);
            if (weight <= 0)
                continue;
            foreach (var b in moved)
                pose[b] = clip.Get(i, b);
            MotionIdles.Apply(pose, layer, index, mask, frame, weight);
            foreach (var b in moved)
                clip.Set(i, b, pose[b]);
        }
    }

    private static void ApplyAmplitude(MotionClip clip, MotionLayer layer, IReadOnlyCollection<int> bones, float end, CancellationToken token)
    {
        var rotate = layer.Components.HasFlag(PoseComponents.Rotation);
        var move = layer.Components.HasFlag(PoseComponents.Position);
        int first = -1, last = -1;
        for (var i = 0; i < clip.Samples; i++)
            if (Inside(layer, clip.Frame(i), end))
            {
                if (first < 0) first = i;
                last = i;
            }
        if (first < 0)
            return;
        var count = last - first + 1;
        foreach (var b in bones)
        {
            token.ThrowIfCancellationRequested();
            // The average pose over the layer: rotations on one hemisphere, summed and normalized.
            var reference = Quaternion.Normalize(clip.Get(first, b).Rotation);
            var sum = Vector4.Zero;
            var center = Vector3.Zero;
            for (var i = first; i <= last; i++)
            {
                var value = clip.Get(i, b);
                var q = Align(Quaternion.Normalize(value.Rotation), reference);
                sum += new Vector4(q.X, q.Y, q.Z, q.W);
                center += value.Position;
            }
            center /= count;
            var pivot = sum.LengthSquared() < 1e-12f ? reference : Quaternion.Normalize(new Quaternion(sum.X, sum.Y, sum.Z, sum.W));
            var inverse = Quaternion.Inverse(pivot);
            // A bone that holds still has nothing to scale; leaving it alone keeps the bake from
            // giving every still bone of the skeleton a track for rounding noise. A turn is measured
            // by its quaternion's vector part (half the angle's sine), which floats keep precise.
            var turns = false;
            var moves = false;
            for (var i = first; i <= last && !(turns && moves); i++)
            {
                var value = clip.Get(i, b);
                var turn = inverse * Align(Quaternion.Normalize(value.Rotation), pivot);
                turns |= rotate && new Vector3(turn.X, turn.Y, turn.Z).LengthSquared() > 1e-10f;
                moves |= move && Vector3.DistanceSquared(value.Position, center) > 1e-12f;
            }
            if (!turns && !moves)
                continue;
            for (var i = first; i <= last; i++)
            {
                var factor = 1 + (layer.Amount - 1) * Fade(layer, clip.Frame(i), end);
                if (Math.Abs(factor - 1) < 1e-6f)
                    continue;
                var value = clip.Get(i, b);
                if (turns)
                    value = value with { Rotation = Quaternion.Normalize(pivot * Pow(inverse * Align(Quaternion.Normalize(value.Rotation), pivot), factor)) };
                if (moves)
                    value = value with { Position = center + (value.Position - center) * factor };
                clip.Set(i, b, value);
            }
        }
    }

    private static void ApplyPose(MotionClip clip, MotionLayer layer, Dictionary<int, Track> tracks, Dictionary<int, Track> positions,
        float loop, float end, CancellationToken token)
    {
        var rotate = layer.Components.HasFlag(PoseComponents.Rotation);
        var move = layer.Components.HasFlag(PoseComponents.Position);
        var scale = layer.Components.HasFlag(PoseComponents.Scale);
        for (var i = 0; i < clip.Samples; i++)
        {
            if ((i & 63) == 0)
                token.ThrowIfCancellationRequested();
            var frame = clip.Frame(i);
            var weight = Weight(layer, frame, end);
            if (weight <= 0)
                continue;
            if (rotate || scale)
                foreach (var (b, track) in tracks)
                    clip.Set(i, b, PoseBone(clip.Get(i, b), Sample(track.Frames, track.Values, track.Curves, frame, loop), weight, rotate, scale));
            if (move)
                foreach (var (b, track) in positions)
                {
                    var target = Sample(track.Frames, track.Values, track.Curves, frame, loop);
                    var value = clip.Get(i, b);
                    clip.Set(i, b, value with { Position = Vector3.Lerp(value.Position, target.Position, weight) });
                }
        }
    }

    /// <summary>
    /// A symmetry layer over the clip, sample by sample. Bone pairs the clip has no track for on either
    /// side are left alone, so physics still moves them in game.
    /// </summary>
    private static void ApplySymmetry(MotionClip clip, IReadOnlyList<MotionLayer> layers, int at, float end, CancellationToken token)
    {
        var layer = layers[at];
        var plan = MotionSymmetry.Plan(clip.Skeleton, layer, MotionSymmetry.Animated(clip.Skeleton, clip.SourceTracks, layers, at));
        if (plan.Pairs.IsEmpty)
            return;
        var rest = MotionSymmetry.RestModel(clip.Skeleton);
        var pose = new MotionTransform[clip.Bones];
        var scratch = new SymmetryScratch(plan, clip.Bones);
        for (var i = 0; i < clip.Samples; i++)
        {
            if ((i & 63) == 0)
                token.ThrowIfCancellationRequested();
            var weight = Weight(layer, clip.Frame(i), end);
            if (weight <= 0)
                continue;
            for (var b = 0; b < pose.Length; b++)
                pose[b] = clip.Get(i, b);
            MotionSymmetry.Mirror(clip.Skeleton, pose, plan, rest, weight, layer.Components, scratch);
            foreach (var pair in plan.Pairs)
                clip.Set(i, pair.Target, pose[pair.Target]);
        }
    }

    /// <summary>A pin of a layer resolved on the skeleton: the chain it bends, its relative bone (-1 for the character) and its limb's rest bend.</summary>
    private sealed record ResolvedPin(MotionPin Pin, PinChain Chain, int RelativeTo, Vector3 RestNormal);

    /// <summary>
    /// <paramref name="layer"/>'s pins resolved on <paramref name="skeleton"/>, after the checks
    /// <see cref="CheckBones"/> makes (<see cref="Apply"/> doesn't call it, so they run here).
    /// </summary>
    private static List<ResolvedPin> ResolvePins(SkeletonDescription skeleton, MotionLayer layer, Dictionary<string, int> index)
    {
        PinChains.Check(skeleton, layer);
        var pins = new List<ResolvedPin>(layer.Pins.Length);
        foreach (var pin in layer.Pins)
        {
            var chain = PinChains.Chain(skeleton, index, pin);
            pins.Add(new(pin, chain, pin.RelativeTo == null ? -1 : index[pin.RelativeTo], PinSolver.RestNormal(skeleton, chain)));
        }
        return pins;
    }

    /// <summary>
    /// A pin layer over the clip, sample by sample, its pins in list order, each seeing what the ones before
    /// it did. Keyed pins aim at their keys; planted pins first find where their bone is down over the
    /// layer's samples, on the clip as the layers before this one leave it, and hold it there. Only the
    /// bones the pins change are written back, so the rest keep their values exactly.
    /// </summary>
    private static void ApplyPin(MotionClip clip, MotionLayer layer, Dictionary<string, int> index, float wrap, float end, CancellationToken token)
    {
        var skeleton = clip.Skeleton;
        var pins = ResolvePins(skeleton, layer, index);
        var tracks = Tracks(layer, index, null, false);
        var keyed = new Track?[pins.Count];
        var planted = new PlantedPath?[pins.Count];
        var changed = new SortedSet<int>();
        for (var p = 0; p < pins.Count; p++)
        {
            if (pins[p].Chain.Shape == PinChainShape.None)
                continue;
            changed.UnionWith(pins[p].Chain.Bones);
            changed.Add(pins[p].Chain.End);
            if (!pins[p].Pin.Planted)
                keyed[p] = tracks.GetValueOrDefault(pins[p].Chain.End);
        }
        if (pins.Any(pin => pin.Pin.Planted && pin.Chain.Shape != PinChainShape.None))
            FindPlanted(clip, layer, pins, planted, wrap > 0, end, token);
        if (changed.Count == 0)
            return;

        var local = new MotionTransform[clip.Bones];
        for (var i = 0; i < clip.Samples; i++)
        {
            if ((i & 63) == 0)
                token.ThrowIfCancellationRequested();
            var frame = clip.Frame(i);
            var weight = Weight(layer, frame, end);
            if (weight <= 0)
                continue;
            for (var b = 0; b < local.Length; b++)
                local[b] = clip.Get(i, b);
            var model = ModelSpace(skeleton, local);
            for (var p = 0; p < pins.Count; p++)
            {
                var pin = pins[p];
                MotionTransform target;
                var pull = weight;
                if (planted[p] is { } path)
                {
                    if (path.Target(i) is not { } held)
                        continue;
                    target = held;
                    pull *= path.Hold(i);
                }
                else if (keyed[p] is { } track)
                    target = Sample(track.Frames, track.Values, track.Curves, frame, wrap);
                else
                    continue;
                PinSolver.Pin(skeleton, local, model, pin.Chain, pin.Pin, pin.RelativeTo, target, pull, pin.RestNormal);
            }
            foreach (var b in changed)
                clip.Set(i, b, local[b]);
        }
    }

    /// <summary>A link layer's bone resolved on the skeleton: the pin its chain bends as (<see cref="MotionLinks.AsPin"/>), the chain and its limb's rest bend.</summary>
    private sealed record ResolvedLink(MotionPin Pin, PinChain Chain, Vector3 RestNormal);

    /// <summary>
    /// <paramref name="link"/> resolved on <paramref name="skeleton"/>, for a bone other than the hips (<see cref="ResolveHipsLink"/>);
    /// null when it has nothing to bend: a bone right under the body, as for a pin. Throws for a bone or top the skeleton can't resolve.
    /// </summary>
    private static ResolvedLink? ResolveLink(SkeletonDescription skeleton, MotionLink link, Dictionary<string, int> index)
    {
        var pin = MotionLinks.AsPin(link);
        var chain = PinChains.Chain(skeleton, index, pin);
        return chain.Shape == PinChainShape.None ? null : new(pin, chain, PinSolver.RestNormal(skeleton, chain));
    }

    /// <summary>
    /// One link on one pose (<paramref name="local"/> and its <paramref name="model"/> space, kept in step) at timeline
    /// <paramref name="frame"/>: aims the bone where the partner's edit takes it (<see cref="MotionLinks.Aim"/>) and bends the chain
    /// toward that by <paramref name="weight"/>, which <see cref="PinSolver.Pin"/> eases itself. False, the pose untouched, when the
    /// partner's edit leaves the bone where it is.
    /// </summary>
    private static bool LinkAt(SkeletonDescription skeleton, MotionTransform[] local, MotionTransform[] model, ResolvedLink link, LinkTrack track,
        float frame, float weight)
    {
        var (before, after) = track.At(frame);
        var current = model[link.Chain.End];
        var aim = MotionLinks.Aim(current, before, after, link.Pin.HoldTurn);
        if (aim == current)
            return false;
        PinSolver.Pin(skeleton, local, model, link.Chain, link.Pin, -1, aim, weight, link.RestNormal);
        return true;
    }

    /// <summary>A hips link resolved on the skeleton: the hips, the back their move carries (-1 for none), what it holds and whether it turns them.</summary>
    private sealed record ResolvedHipsLink(int Hips, int Carried, ImmutableArray<HipAnchor> Anchors, bool HoldTurn);

    /// <summary><paramref name="link"/> on the hips resolved on <paramref name="skeleton"/>. Throws for a skeleton without the hips.</summary>
    private static ResolvedHipsLink ResolveHipsLink(SkeletonDescription skeleton, MotionLink link, Dictionary<string, int> index) =>
        new(index.TryGetValue(PinChains.Hips, out var hips) ? hips : throw Missing(PinChains.Hips), HipSolver.Carried(skeleton),
            HipSolver.Anchors(skeleton, link.Holds), link.HoldTurn);

    /// <summary>
    /// A hips link on one pose (<paramref name="local"/> and its <paramref name="model"/> space, kept in step) at timeline
    /// <paramref name="frame"/>: eases the hips toward where the partner's edit takes them by <paramref name="weight"/> (the hip solve
    /// takes no weight), turns them first when the link holds the turn, then moves them there with the held bones planted where the
    /// pose had them, so the replant also undoes what the turn did to them. With <paramref name="hipsOnHara"/> the caller moves the
    /// hips' move onto <c>n_hara</c>, which carries the back too, so the back keeps its own spot here. False, the pose untouched,
    /// when the partner's edit leaves the hips where they are.
    /// </summary>
    private static bool HipsLinkAt(SkeletonDescription skeleton, MotionTransform[] local, MotionTransform[] model, ResolvedHipsLink link, LinkTrack track,
        float frame, float weight, bool hipsOnHara)
    {
        var (before, after) = track.At(frame);
        var current = model[link.Hips];
        var aim = MotionLinks.Aim(current, before, after, link.HoldTurn);
        if (aim == current)
            return false;
        var start = model.ToArray();
        var back = link.Carried >= 0 ? local[link.Carried].Position : Vector3.Zero;
        var eased = Lerp(current, aim, weight);
        if (link.HoldTurn)
            PinSolver.HoldTurn(skeleton, local, model, link.Hips, eased.Rotation);
        HipSolver.Solve(skeleton, local, model, link.Hips, eased.Position, link.Anchors.AsSpan(), start);
        if (hipsOnHara && link.Carried >= 0)
        {
            local[link.Carried] = local[link.Carried] with { Position = back };
            PinSolver.Refresh(skeleton, local, model, link.Carried);
        }
        return true;
    }

    /// <summary>
    /// A link layer over the clip, sample by sample: its bone follows the partner's edit in <paramref name="track"/>, its chain bending
    /// as a pin's. Only the chain and the bone are written back, so the rest keep their values exactly; a hips link writes every bone,
    /// as the hip solve turns the back and every held chain and leaves the rest exact copies. <paramref name="hipsOnHara"/> says
    /// <see cref="Apply"/> moves the hips' move onto <c>n_hara</c> after this layer.
    /// </summary>
    private static void ApplyLink(MotionClip clip, MotionLayer layer, Dictionary<string, int> index, LinkTrack track, float end, bool hipsOnHara,
        CancellationToken token)
    {
        var skeleton = clip.Skeleton;
        if (layer.Link!.Bone == PinChains.Hips)
        {
            var hips = ResolveHipsLink(skeleton, layer.Link, index);
            var pose = new MotionTransform[clip.Bones];
            for (var i = 0; i < clip.Samples; i++)
            {
                if ((i & 63) == 0)
                    token.ThrowIfCancellationRequested();
                var frame = clip.Frame(i);
                var weight = Weight(layer, frame, end);
                if (weight <= 0)
                    continue;
                for (var b = 0; b < pose.Length; b++)
                    pose[b] = clip.Get(i, b);
                if (!HipsLinkAt(skeleton, pose, ModelSpace(skeleton, pose), hips, track, frame, weight, hipsOnHara))
                    continue;
                for (var b = 0; b < pose.Length; b++)
                    clip.Set(i, b, pose[b]);
            }
            return;
        }
        if (ResolveLink(skeleton, layer.Link, index) is not { } link)
            return;
        var changed = link.Chain.Bones.Append(link.Chain.End).ToArray();
        var local = new MotionTransform[clip.Bones];
        for (var i = 0; i < clip.Samples; i++)
        {
            if ((i & 63) == 0)
                token.ThrowIfCancellationRequested();
            var frame = clip.Frame(i);
            var weight = Weight(layer, frame, end);
            if (weight <= 0)
                continue;
            for (var b = 0; b < local.Length; b++)
                local[b] = clip.Get(i, b);
            if (!LinkAt(skeleton, local, ModelSpace(skeleton, local), link, track, frame, weight))
                continue;
            foreach (var b in changed)
                clip.Set(i, b, local[b]);
        }
    }

    /// <summary>
    /// Where <paramref name="layer"/>'s planted pins hold their bones on <paramref name="clip"/> as it stands,
    /// found as <see cref="Apply"/> finds them, by pinned bone; for the panel's facts. Pins with nothing to bend
    /// are left out. Throws for a pin the skeleton can't resolve.
    /// </summary>
    public static Dictionary<string, PlantedPath> Planted(MotionClip clip, MotionLayer layer)
    {
        var pins = ResolvePins(clip.Skeleton, layer, Index(clip.Skeleton));
        var planted = new PlantedPath?[pins.Count];
        var end = clip.Frame(clip.Samples - 1);
        FindPlanted(clip, layer, pins, planted, Wraps(layer, clip.Loops ? end : 0), end, CancellationToken.None);
        var found = new Dictionary<string, PlantedPath>(StringComparer.Ordinal);
        for (var p = 0; p < pins.Count; p++)
            if (planted[p] is { } path)
                found[pins[p].Pin.Bone] = path;
        return found;
    }

    /// <summary>
    /// Where each planted pin's bone is down: its path in the pin's space over the whole clip as it stands,
    /// looked at over the samples inside the layer, wrapping when the layer covers a loop.
    /// </summary>
    private static void FindPlanted(MotionClip clip, MotionLayer layer, List<ResolvedPin> pins, PlantedPath?[] planted, bool wraps, float end, CancellationToken token)
    {
        int first = -1, last = -1;
        for (var i = 0; i < clip.Samples; i++)
            if (Inside(layer, clip.Frame(i), end))
            {
                if (first < 0) first = i;
                last = i;
            }
        if (first < 0)
            return;
        var paths = new Dictionary<int, MotionTransform[]>();
        // Heights in model space: a bone's space has its own Y, which isn't up.
        var heights = new Dictionary<int, float[]>();
        // A limb that only bends itself can't reach past its soft reach: where its upper bone is and how far that goes, per sample.
        // Read before the layer runs, so an earlier pin of this layer that bends the same limb (a chain through the thigh) isn't seen.
        var reaches = new Dictionary<int, (Vector3[] Roots, float[] Limits)>();
        for (var p = 0; p < pins.Count; p++)
            if (pins[p].Pin.Planted && pins[p].Chain.Shape != PinChainShape.None)
            {
                paths[p] = new MotionTransform[clip.Samples];
                heights[p] = new float[clip.Samples];
                if (pins[p].Chain is { Shape: PinChainShape.Limb } limb && limb.Bones[0] == limb.Root)
                    reaches[p] = (new Vector3[clip.Samples], new float[clip.Samples]);
            }
        var local = new MotionTransform[clip.Bones];
        for (var i = 0; i < clip.Samples; i++)
        {
            if ((i & 63) == 0)
                token.ThrowIfCancellationRequested();
            for (var b = 0; b < local.Length; b++)
                local[b] = clip.Get(i, b);
            var model = ModelSpace(clip.Skeleton, local);
            foreach (var (p, path) in paths)
            {
                path[i] = PinSolver.PinSpace(model, pins[p].Chain.End, pins[p].RelativeTo);
                heights[p][i] = model[pins[p].Chain.End].Position.Y;
            }
            foreach (var (p, (roots, limits)) in reaches)
            {
                roots[i] = PinSolver.PinSpace(model, pins[p].Chain.Root, pins[p].RelativeTo).Position;
                limits[i] = PinSolver.SoftReach(model, pins[p].Chain);
            }
        }
        var rate = clip.Duration > 0 ? (clip.Samples - 1) / clip.Duration : TimelineDocument.Fps;
        var gap = PinPlanting.SamplesOf(PinPlanting.CloseGapFrames, clip);
        var shortest = PinPlanting.SamplesOf(PinPlanting.MinStretchFrames, clip);
        var ease = PinPlanting.SamplesOf(PinPlanting.EaseFrames, clip);
        foreach (var (p, path) in paths)
        {
            var found = PinPlanting.Find(path, rate, first, last, wraps, pins[p].Pin.Sensitivity, gap, shortest, ease, heights[p]);
            // A foot set on the floor first, so a floor the leg can't reach still comes in to where it can.
            if (pins[p].Pin is { Floor: true, RelativeTo: null } && PinPlanting.Sole(clip.Skeleton, pins[p].Chain.End) is { } sole)
                found = reaches.TryGetValue(p, out var limb) ? PinPlanting.OnFloor(found, sole, pins[p].Pin.HoldTurn, limb.Roots, limb.Limits)
                    : PinPlanting.OnFloor(found, sole, pins[p].Pin.HoldTurn);
            planted[p] = reaches.TryGetValue(p, out var reach) ? PinPlanting.Reachable(found, reach.Roots, reach.Limits) : found;
        }
    }

    internal static bool Inside(MotionLayer layer, float frame, float end) =>
        frame >= layer.Start - FrameTolerance && (frame <= layer.End + FrameTolerance || ReachesEnd(layer, end));

    /// <summary>The bones a layer is limited to, by index; null when it is not limited.</summary>
    private static HashSet<int>? Mask(MotionLayer layer, Dictionary<string, int> index)
    {
        if (layer.Bones.IsEmpty)
            return null;
        var mask = new HashSet<int>();
        foreach (var name in layer.Bones)
            mask.Add(index.TryGetValue(name, out var bone) ? bone : throw Missing(name));
        return mask;
    }

    /// <summary>
    /// Each keyed bone's keys, by bone index: every key that names the bone, or with
    /// <paramref name="positions"/> only those that move it.
    /// </summary>
    private static Dictionary<int, Track> Tracks(MotionLayer layer, Dictionary<string, int> index, HashSet<int>? mask, bool positions)
    {
        var frames = new Dictionary<int, List<int>>();
        var values = new Dictionary<int, List<MotionTransform>>();
        var curves = new Dictionary<int, List<MotionCurve?>>();
        foreach (var key in layer.Keys)
        {
            var curve = CurveOf(layer, key);
            foreach (var (name, value) in key.Bones)
            {
                if (!index.TryGetValue(name, out var bone))
                    throw Missing(name);
                if (mask != null && !mask.Contains(bone) || positions && !key.Moved.Contains(name, StringComparer.Ordinal))
                    continue;
                if (!frames.TryGetValue(bone, out var list))
                {
                    frames[bone] = list = [];
                    values[bone] = [];
                    curves[bone] = [];
                }
                list.Add(key.Frame);
                values[bone].Add(Normalized(MotionTransform.From(value)));
                curves[bone].Add(curve);
            }
        }
        return frames.ToDictionary(pair => pair.Key, pair => new Track(pair.Value.ToArray(), values[pair.Key].ToArray(), curves[pair.Key].ToArray()));
    }

    private static MotionTransform Normalized(MotionTransform value) => value with { Rotation = Quaternion.Normalize(value.Rotation) };

    internal static InvalidDataException Missing(string bone) =>
        new($"This animation's skeleton has no bone {AnimationPresentation.BoneName(bone)}.");

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    /// <summary>Whether a stored value is a transform: finite, its rotation near unit length.</summary>
    private static bool IsTransform(BoneTransform? value) =>
        value != null && Finite(value.Position) && Finite(value.Scale) &&
        float.IsFinite(value.Rotation.LengthSquared()) && value.Rotation.LengthSquared() is >= .25f and <= 4;

    private static bool InScale(BoneTransform value) =>
        value.Scale.X is >= MinScale and <= MaxScale && value.Scale.Y is >= MinScale and <= MaxScale && value.Scale.Z is >= MinScale and <= MaxScale;

    private static Vector3 Divide(Vector3 value, Vector3 by) => new(
        Math.Abs(by.X) < 1e-12f ? value.X : value.X / by.X,
        Math.Abs(by.Y) < 1e-12f ? value.Y : value.Y / by.Y,
        Math.Abs(by.Z) < 1e-12f ? value.Z : value.Z / by.Z);

    internal sealed record Track(int[] Frames, MotionTransform[] Values, MotionCurve?[] Curves);
}
