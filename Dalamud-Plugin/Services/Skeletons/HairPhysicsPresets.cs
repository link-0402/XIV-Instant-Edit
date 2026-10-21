namespace InstantEdit.Services.Skeletons;

/// <summary>One node's physics: radius in metres, attract-by-animation, wind, gravity and the cone in degrees.</summary>
internal sealed record HairNodePreset(float Radius, float AttractByAnimation, float WindScale, float GravityScale, float ConeDegrees);

/// <summary>
/// A chain kind's physics: its dampening, the length its cones are measured for, and the root (first
/// bone), middle (other bones) and tip (the extra end node) presets.
/// </summary>
internal sealed record HairPhysicsPreset(float Dampening, float ReferenceLength, HairNodePreset Root, HairNodePreset Middle, HairNodePreset Tip);

/// <summary>
/// Chain presets from the medians of comparable vanilla hair chains, with the PhysicsGroup rows and
/// collision shapes vanilla hair uses per kind. Shapes come from the race's h0182 physics, which every
/// player race has and which carries all of them.
/// </summary>
internal static class HairPhysicsPresets
{
    public const int DonorSkeletonId = 182;

    // Every vanilla hair chain has these.
    public const float MaxSpeed = 4;
    public const float Friction = 0;
    public const float CollisionDampening = 0;
    public const float RepulsionStrength = 1;

    private static readonly HairPhysicsPreset Bangs = new(0.01f, 0.12f,
        new(0.01f, 0, 0.01f, 1, 20), new(0.01f, 0, 0.01f, 1, 10), new(0.01f, 0.02f, 0.003f, 0.7f, 180));

    private static readonly HairPhysicsPreset Side = new(0.1f, 0.25f,
        new(0.01f, 0, 0.01f, 1, 30), new(0.01f, 0.05f, 0.005f, 1, 180), new(0.01f, 0.005f, 0.005f, 0.825f, 180));

    private static readonly HairPhysicsPreset Back = new(0.05f, 0.30f,
        new(0.01f, 0, 0.01f, 1, 40), new(0.02f, 0.01f, 0.005f, 0.5f, 40), new(0.02f, 0, 0.005f, 0.5f, 180));

    private static readonly HairPhysicsPreset Ponytail = new(0.04f, 0.40f,
        new(0.02f, 0, 0.01f, 1, 40), new(0.02f, 0, 0.004f, 0.8f, 40), new(0.02f, 0, 0.004f, 0.65f, 180));

    private static readonly string[] BackShapes = ["backhair_nplane", "hair_capsule_sako_l", "hair_capsule_sako_r"];
    private static readonly string[] LeftShapes = ["sidehair_nplane", "hair_sphere_kao_l", "sidehair_capsule_sako_l"];
    private static readonly string[] RightShapes = ["sidehair_nplane", "hair_sphere_kao_r", "sidehair_capsule_sako_r"];

    public static HairPhysicsPreset For(string kind) => kind switch
    {
        "bangs" => Bangs,
        "side" => Side,
        "back" => Back,
        "ponytail" => Ponytail,
        _ => throw Unknown(kind),
    };

    /// <summary>The PhysicsGroup row: 3 for front hair, 23 for back hair, 24 and 25 for left and right side hair.</summary>
    public static byte Group(string kind, bool left) => kind switch
    {
        "bangs" => 3,
        "back" or "ponytail" => 23,
        "side" => left ? (byte)24 : (byte)25,
        _ => throw Unknown(kind),
    };

    /// <summary>The donor shapes a kind collides with, in vanilla's order; front hair collides with none.</summary>
    public static IReadOnlyList<string> Shapes(string kind, bool left) => kind switch
    {
        "bangs" => [],
        "back" or "ponytail" => BackShapes,
        "side" => left ? LeftShapes : RightShapes,
        _ => throw Unknown(kind),
    };

    public static string DonorPath(int genderRace) => HairPhysicsBuilder.PhysicsPath(genderRace, DonorSkeletonId);

    private static ArgumentException Unknown(string kind) => new($"\"{kind}\" isn't a hair chain kind.", nameof(kind));
}
