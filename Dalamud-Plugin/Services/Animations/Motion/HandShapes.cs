using System.Collections.Immutable;
using System.Numerics;
using InstantEdit.Models;

namespace InstantEdit.Services.Animations.Motion;

/// <summary>
/// A built-in hand shape in degrees. <paramref name="Fingers"/> holds the index, middle, ring and little
/// fingers' bends of joints a, b and the tip; <paramref name="Thumb"/> the thumb's bends of a and b and
/// its turn across the palm.
/// </summary>
internal sealed record HandRecipe(string Name, ImmutableArray<Vector3> Fingers, float Spread, Vector3 Thumb);

/// <summary>
/// Hand shapes: the built-in recipes, the local finger rotations a hand layer sets, and the form a
/// saved shape is stored in. A saved shape holds each finger bone's turn from rest relative to the
/// wrist, in the body's rest axes, under the right hand's bone names; the left hand stores its mirror.
/// </summary>
internal static class HandShapes
{
    private const float FingerCurl = 60, ThumbCurl = 30, SpreadTurn = 15, MinBend = -5, MaxBend = 110;

    /// <summary>The lowest Spread that applies: below about -30% the index finger goes through the middle one (seen in game).</summary>
    public const float MinSpread = -.3f;

    /// <summary>The built-in shapes' ids in the order the shape picker lists them.</summary>
    public static readonly ImmutableArray<string> Order = ["relaxed", "flat", "fist", "point", "grip", "cupped", "spread"];

    /// <summary>The built-in shapes by id.</summary>
    public static readonly ImmutableDictionary<string, HandRecipe> BuiltIn = new Dictionary<string, HandRecipe>
    {
        ["relaxed"] = new("Relaxed", [new(10, 15, 8), new(15, 20, 10), new(18, 22, 10), new(20, 25, 12)], 0, new(10, 10, 10)),
        ["flat"] = new("Flat", All(0, 0, 0), 0, Vector3.Zero),
        ["fist"] = new("Fist", All(85, 100, 70), 0, new(30, 50, 45)),
        ["point"] = new("Point", [Vector3.Zero, new(85, 100, 70), new(85, 100, 70), new(85, 100, 70)], 0, new(30, 40, 40)),
        ["grip"] = new("Grip", All(45, 60, 40), 0, new(20, 30, 35)),
        ["cupped"] = new("Cupped", All(25, 30, 20), -3, new(15, 15, 25)),
        ["spread"] = new("Spread", All(0, 0, 0), 12, new(0, 0, -20)),
    }.ToImmutableDictionary();

    private static ImmutableArray<Vector3> All(float a, float b, float tip) => [new(a, b, tip), new(a, b, tip), new(a, b, tip), new(a, b, tip)];

    /// <summary>
    /// The local rotation <paramref name="hand"/> sets on each finger bone <paramref name="rig"/> has: its
    /// saved <see cref="MotionHand.Turns"/>, or else the built-in shape it names, plus Curl and Spread.
    /// With <paramref name="tips"/> false a finger's tip bend goes into its b bone, up to that joint's
    /// limit, and the tip stays at rest. Empty for a shape that is neither saved nor built in.
    /// </summary>
    public static Dictionary<int, Quaternion> Targets(SkeletonDescription skeleton, HandRig rig, MotionHand hand, bool tips)
    {
        var targets = new Dictionary<int, Quaternion>();
        var recipe = hand.Turns == null ? BuiltIn.GetValueOrDefault(hand.Shape) : null;
        if (recipe == null && hand.Turns == null)
            return targets;
        var bones = skeleton.Bones;
        var rest = recipe == null ? MotionSymmetry.RestModel(skeleton) : null;
        // A layer saved before the cap keeps its value, but applies no further than it.
        var spreadBy = Math.Max(hand.Spread, MinSpread) * SpreadTurn;
        foreach (var chain in rig.Chains)
        {
            var thumb = chain.Finger == HandFinger.Thumb;
            var fold = !thumb && !(tips && chain.Bones.Length == 3);
            var curl = hand.Curl * (thumb ? ThumbCurl : FingerCurl);
            var local = chain.Bones.Select(bone => bones[bone].Reference.Rotation).ToArray();
            float spread;
            if (recipe != null)
            {
                var shape = thumb ? recipe.Thumb : recipe.Fingers[(int)chain.Finger - 1];
                float[] bend = [Math.Clamp(shape.X + curl, MinBend, MaxBend), Math.Clamp(shape.Y + curl, MinBend, MaxBend), thumb ? 0 : Math.Clamp(shape.Z + curl, MinBend, MaxBend)];
                // The total only holds below the clamp: past 110 degrees one vanilla joint folds the finger through the palm.
                if (fold)
                    (bend[1], bend[2]) = (Math.Clamp(bend[1] + bend[2], MinBend, MaxBend), 0);
                if (thumb)
                    local[0] = Turn(local[0], rig.AcrossAxis, recipe.Thumb.Z);
                for (var i = 0; i < local.Length && i < bend.Length; i++)
                    local[i] = Turn(local[i], chain.BendAxes[i], bend[i]);
                spread = recipe.Spread + spreadBy;
            }
            else
            {
                Saved(skeleton, rig, chain, rest!, hand.Turns!, local, out var tipTurn);
                // Bends past the limits stay as saved; the fold and Curl only never push a joint further past them.
                var joints = thumb ? Math.Min(local.Length, 2) : local.Length;
                var bent = new float[joints];
                for (var i = 0; i < joints; i++)
                    bent[i] = Twist(Quaternion.Inverse(bones[chain.Bones[i]].Reference.Rotation) * local[i], chain.BendAxes[i]);
                if (fold)
                {
                    local[1] *= tipTurn;
                    if (local.Length == 3)
                        local[2] = bones[chain.Bones[2]].Reference.Rotation;
                    var folded = bent[1] + Twist(tipTurn, chain.BendAxes[1]);
                    var limited = Math.Clamp(folded, Math.Min(MinBend, bent[1]), Math.Max(MaxBend, bent[1]));
                    local[1] = Turn(local[1], chain.BendAxes[1], limited - folded);
                    bent[1] = limited;
                }
                for (var i = 0; i < joints && (!fold || i < 2); i++)
                {
                    // A folded b bends for its tip too, so it takes the tip's Curl as well.
                    var add = curl * (fold && i == 1 ? 2 : 1);
                    local[i] = Turn(local[i], chain.BendAxes[i], add > 0 ? Math.Clamp(MaxBend - bent[i], 0, add) : add < 0 ? -Math.Clamp(bent[i] - MinBend, 0, -add) : 0);
                }
                spread = spreadBy;
            }
            // After the bends, in the bent frame: on a curled a bone, which points along the palm's normal, a turn about that normal would only roll it.
            local[0] = Turn(local[0], chain.SpreadAxis, spread * chain.SpreadSign);
            for (var i = 0; i < local.Length; i++)
                targets[chain.Bones[i]] = Quaternion.Normalize(local[i]);
        }
        return targets;
    }

    /// <summary>
    /// The finger bones' turns in <paramref name="local"/> in saved form, under the right hand's bone
    /// names, mirrored when <paramref name="rig"/> is the left hand.
    /// </summary>
    public static ImmutableDictionary<string, Quaternion> Capture(SkeletonDescription skeleton, ReadOnlySpan<MotionTransform> local, HandRig rig)
    {
        var model = MotionEvaluator.ModelSpace(skeleton, local);
        var rest = MotionSymmetry.RestModel(skeleton);
        var rest0 = rest[rig.Wrist].Rotation;
        var wrist = Quaternion.Inverse(model[rig.Wrist].Rotation);
        var turns = ImmutableDictionary.CreateBuilder<string, Quaternion>();
        foreach (var chain in rig.Chains)
            foreach (var bone in chain.Bones)
            {
                var turn = Quaternion.Normalize(rest0 * (wrist * model[bone].Rotation) *
                    Quaternion.Inverse(Quaternion.Inverse(rest0) * rest[bone].Rotation) * Quaternion.Inverse(rest0));
                turns[RightName(skeleton.Bones[bone].Name, rig)] = rig.Side == MotionSide.Left ? MotionMirror.Rotation(turn) : turn;
            }
        return turns.ToImmutable();
    }

    /// <summary>
    /// Fills <paramref name="local"/> with the chain's local rotations from saved <paramref name="turns"/>;
    /// a bone without a turn keeps its rest rotation. <paramref name="tipTurn"/> is the tip's turn from
    /// rest as a turn of the b bone, for folding it in: the saved tip's even when this skeleton has none.
    /// </summary>
    private static void Saved(SkeletonDescription skeleton, HandRig rig, HandChain chain, MotionTransform[] rest,
        ImmutableDictionary<string, Quaternion> turns, Quaternion[] local, out Quaternion tipTurn)
    {
        var bones = skeleton.Bones;
        var wrist = rest[rig.Wrist].Rotation;
        var unwrist = Quaternion.Inverse(wrist);
        // H: a bone's rotation relative to the wrist.
        Quaternion? Held(string name, Quaternion restHeld) =>
            turns.TryGetValue(RightName(name, rig), out var turn) ? unwrist * (rig.Side == MotionSide.Left ? MotionMirror.Rotation(turn) : turn) * wrist * restHeld : null;
        var parent = bones[chain.Bones[0]].Parent;
        var above = parent == rig.Wrist ? Quaternion.Identity : unwrist * rest[parent].Rotation;
        for (var i = 0; i < chain.Bones.Length; i++)
        {
            var bone = chain.Bones[i];
            var held = Held(bones[bone].Name, unwrist * rest[bone].Rotation);
            local[i] = held is { } h ? Quaternion.Inverse(above) * h : bones[bone].Reference.Rotation;
            above = held ?? above * local[i];
        }
        tipTurn = Quaternion.Identity;
        if (chain.Finger == HandFinger.Thumb)
            return;
        if (chain.Bones.Length == 3)
        {
            var tipRest = bones[chain.Bones[2]].Reference.Rotation;
            tipTurn = local[2] * Quaternion.Inverse(tipRest);
            return;
        }
        // A two-joint skeleton has no tip bone: a straight tip's rest is its b bone's.
        var b = bones[chain.Bones[1]].Name;
        if (Held("iv_" + b[2..^3] + "c" + b[^2..], unwrist * rest[chain.Bones[1]].Rotation) is { } tip)
            tipTurn = Quaternion.Inverse(above) * tip;
    }

    private static string RightName(string name, HandRig rig) => rig.Side == MotionSide.Left ? MotionMirror.Twin(name) ?? name : name;

    /// <summary><paramref name="rotation"/> turned further by <paramref name="degrees"/> about <paramref name="axis"/> in its own frame; unchanged for no axis.</summary>
    private static Quaternion Turn(Quaternion rotation, Vector3 axis, float degrees) =>
        degrees == 0 || !(axis.LengthSquared() > 1e-12f) ? rotation : rotation * Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), degrees * MathF.PI / 180);

    /// <summary>The signed angle in degrees, -180 to 180, that <paramref name="turn"/> turns about <paramref name="axis"/>.</summary>
    private static float Twist(Quaternion turn, Vector3 axis)
    {
        if (turn.W < 0)
            turn = -turn;
        return 2 * MathF.Atan2(Vector3.Dot(new Vector3(turn.X, turn.Y, turn.Z), Vector3.Normalize(axis)), turn.W) * 180 / MathF.PI;
    }
}
