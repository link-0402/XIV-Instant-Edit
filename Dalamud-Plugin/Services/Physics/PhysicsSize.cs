using System.Numerics;

namespace InstantEdit.Services.Physics;

/// <summary>
/// A body's breast size next to the game's own body: the base shape's reach times how much
/// Customize+ and the bust slider grow the bone (<paramref name="Growth"/>).
/// </summary>
internal sealed record PhysicsSizeMeasure(float Ratio, float BaseRatio, float Growth)
{
    /// <summary> The nearest body size of the presets that tune by size. </summary>
    public string Label => PhysicsSize.Label(Ratio);
}

/// <summary>
/// How big breasts are, and the firmness that suits them. Size-based presets make bigger breasts
/// softer: one popular preset ships firmness 0.045, 0.035, 0.025 and 0.015 for Rue+ Small, Medium and
/// Large and RueXB+. Measured the same way (nine tenths of the skin that follows the breast bone, a
/// quarter of its weight or more, reaches this far along the bone, at rest), those bodies reach 0.99,
/// 1.19, 1.28 and 1.67 times as far as the game's own body. Customize+ and the bust slider scale the
/// bone after physics, so a grown breast moves like a bigger body's: the cube root of the bone's scale
/// (its volume's growth, as a length) multiplies its reach.
/// </summary>
internal static class PhysicsSize
{
    private static readonly (float Ratio, float Firmness)[] Chest = [(0.988f, 0.045f), (1.191f, 0.035f), (1.279f, 0.025f), (1.674f, 0.015f)];

    /// <summary> The size chest levels are tuned at: Rue+ Medium's. </summary>
    public const float MediumRatio = 1.191f;

    /// <summary> The breast bones, left and right. </summary>
    public static readonly string[] ChestBones = ["j_mune_l", "j_mune_r"];

    /// <summary> Only female bodies have breasts that size-based presets tune. </summary>
    public static bool HasBreasts(ushort race) => race / 100 % 2 == 0;

    /// <summary> The firmness size-based presets give breasts of a size, between their sizes in a straight line. </summary>
    public static float ChestFirmness(float ratio)
    {
        if (!float.IsFinite(ratio) || ratio <= Chest[0].Ratio)
            return Chest[0].Firmness;
        for (var i = 1; i < Chest.Length; i++)
            if (ratio <= Chest[i].Ratio)
            {
                var (fromRatio, fromFirmness) = Chest[i - 1];
                var (toRatio, toFirmness) = Chest[i];
                return fromFirmness + (toFirmness - fromFirmness) * (ratio - fromRatio) / (toRatio - fromRatio);
            }
        return Chest[^1].Firmness;
    }

    /// <summary> How much firmer than at medium size breasts of a size should be: 1 at medium, less when bigger. </summary>
    public static float ChestScale(float ratio) => ChestFirmness(ratio) / ChestFirmness(MediumRatio);

    /// <summary> The nearest size name, halfway between the sizes. </summary>
    public static string Label(float ratio) => ratio switch
    {
        < 1.09f => "small",
        < 1.235f => "medium",
        < 1.48f => "large",
        _ => "extra large",
    };

    /// <summary>
    /// How far the skin that follows a bone reaches along it (its X axis): nine tenths of the skin a
    /// quarter of its weight or more follows it, by area. Null without such skin.
    /// </summary>
    public static float? Reach(PhysicsBody body, PhysicsSkeleton skeleton, string bone)
    {
        if (!body.Pose.TryGetValue(bone, out var frame))
            return null;
        var axis = frame.Turn(Vector3.UnitX);
        var bones = PhysicsFit.Region(body, skeleton, [bone]);
        var along = new List<(float Value, float Area)>();
        for (var i = 0; i < body.Points.Length; i++)
            if (body.WeightOn(i, bones) >= 0.25f)
                along.Add((Vector3.Dot(body.Points[i] - frame.Position, axis), body.Areas[i]));
        return PhysicsFit.Percentile(along, 0.9f);
    }

    /// <summary> The mean reach of both breasts, or null when neither has skin. </summary>
    public static float? ChestReach(PhysicsBody body, PhysicsSkeleton skeleton)
    {
        var reaches = ChestBones.Select(bone => Reach(body, skeleton, bone)).OfType<float>().Where(reach => reach > 0).ToList();
        return reaches.Count > 0 ? reaches.Average() : null;
    }

    /// <summary> How much a bone's scale grows what it carries, as a length: the cube root of its volume's growth. </summary>
    public static float Growth(Vector3 scale)
    {
        var volume = MathF.Abs(scale.X * scale.Y * scale.Z);
        return float.IsFinite(volume) && volume > 1e-6f ? MathF.Cbrt(volume) : 1;
    }

    /// <summary>
    /// The size of breasts from the drawn body's base shape (<paramref name="rest"/>, without
    /// Customize+), the game's own body measured the same way, and the live pose's bone scales.
    /// Null when either body has no breast skin.
    /// </summary>
    public static PhysicsSizeMeasure? Measure(PhysicsBody rest, PhysicsBody game, PhysicsSkeleton skeleton, IReadOnlyDictionary<string, PhysicsFrame> live)
    {
        if (ChestReach(rest, skeleton) is not { } own || ChestReach(game, skeleton) is not { } reference || reference < 1e-3f)
            return null;
        var growths = ChestBones.Where(live.ContainsKey).Select(bone => Growth(live[bone].Scale)).ToList();
        var growth = growths.Count > 0 ? growths.Average() : 1;
        var baseRatio = own / reference;
        return new PhysicsSizeMeasure(baseRatio * growth, baseRatio, growth);
    }
}
