using System.Numerics;

namespace InstantEdit.Services.Physics;

/// <summary> The body part a simulator moves. </summary>
internal enum PhysicsPart
{
    Chest,
    Pecs,
    Butt,
    Belly,
    Thighs,
    Groin,
    Ears,
    Skirt,
    Hair,
    FrontHair,
    Earrings,
    Tail,
}

/// <summary>
/// The starting values of an area added from scratch: a chain per bone from the bone to a tip at
/// <paramref name="Tip"/> (in the bone's space), and a spring pulling the tip back to where it rests.
/// </summary>
internal sealed record PhysicsTemplate(
    Vector3 Tip,
    float Damping,
    float SwingDegrees,
    float TipGravity,
    float FollowAnimation,
    float Firmness,
    float TipRadius = 0f,
    float MaxSpeed = 4f,
    float TipWind = 0.001f);

/// <summary>
/// A body area that skeletons give physics bones to. <paramref name="Body"/> areas are the jiggle the
/// builder is for: they belong in the chest group, which gear can turn off.
/// </summary>
internal sealed record PhysicsAreaKind(
    string Key,
    PhysicsPart Part,
    string Label,
    string Skeleton,
    IReadOnlyList<string> Bones,
    bool Body,
    byte Group,
    PhysicsTemplate? Template = null,
    string? BonePrefix = null)
{
    /// <summary> This area's bone, by exact name or, for bone families like the skirt's, by prefix. </summary>
    public bool Owns(string bone)
        => Bones.Contains(bone, StringComparer.Ordinal) || BonePrefix != null && bone.StartsWith(BonePrefix, StringComparison.Ordinal);
}

/// <summary>
/// What the builder knows about a simulator: its area, or the bones it moves when it has none.
/// <paramref name="Known"/> is false when some chain's bone belongs to no known area.
/// </summary>
internal sealed record PhysicsAreaInfo(string Label, PhysicsPart? Part, IReadOnlyList<PhysicsAreaKind> Kinds, IReadOnlyList<string> Bones, bool Known)
{
    /// <summary> Only body areas: the jiggle that belongs in the chest group. </summary>
    public bool Body => Known && Kinds.Count > 0 && Kinds.All(kind => kind.Body);
}

/// <summary>
/// The body areas the builder knows: vanilla's own simulators (skirt, hair, earrings, tail) and the
/// physics bones IVCS and YAS add. Areas with a template can be added from scratch for any skeleton
/// that has their bones. Starting values follow vanilla's breasts and common presets; they're meant
/// to be tuned in game.
/// </summary>
internal static class PhysicsAreas
{
    /// <summary> The game's group for breasts. Gear that turns off breast physics stops it. </summary>
    public const byte ChestGroup = 7;
    public const byte EarsGroup = 18;

    public static readonly IReadOnlyList<PhysicsAreaKind> Kinds =
    [
        new("chest", PhysicsPart.Chest, "Chest", "Vanilla", ["j_mune_l", "j_mune_r"], true, ChestGroup,
            new PhysicsTemplate(new Vector3(0.145f, 0, 0), Damping: 0.1f, SwingDegrees: 8f, TipGravity: 0.1f, FollowAnimation: 0f,
                Firmness: 0.075f, TipRadius: 0.01f, TipWind: 0f)),
        new("pecs-ivcs", PhysicsPart.Pecs, "Pecs", "IVCS", ["iv_kyokin_phys_l", "iv_kyokin_phys_r"], true, ChestGroup,
            new PhysicsTemplate(new Vector3(0.06f, 0, 0), Damping: 0.15f, SwingDegrees: 10f, TipGravity: 0.1f, FollowAnimation: 0.05f,
                Firmness: 0.08f)),
        new("butt-ivcs", PhysicsPart.Butt, "Butt", "IVCS", ["iv_shiri_l", "iv_shiri_r"], true, ChestGroup,
            new PhysicsTemplate(new Vector3(0.106f, 0, 0), Damping: 0.1f, SwingDegrees: 30f, TipGravity: 0.1f, FollowAnimation: 0.05f,
                Firmness: 0.04f)),
        new("butt-yas", PhysicsPart.Butt, "Butt", "YAS", ["ya_shiri_phys_l", "ya_shiri_phys_r"], true, ChestGroup,
            new PhysicsTemplate(new Vector3(0.06f, 0, 0), Damping: 0.05f, SwingDegrees: 30f, TipGravity: 0.1f, FollowAnimation: 0f,
                Firmness: 0.04f)),
        new("belly-ivcs", PhysicsPart.Belly, "Belly", "IVCS", ["iv_fukubu_phys"], true, ChestGroup,
            new PhysicsTemplate(new Vector3(-0.039f, -0.023f, 0), Damping: 0.1f, SwingDegrees: 20f, TipGravity: 0.1f, FollowAnimation: 0.05f,
                Firmness: 0.04f)),
        new("belly-yas", PhysicsPart.Belly, "Belly", "YAS", ["ya_fukubu_phys"], true, ChestGroup,
            new PhysicsTemplate(new Vector3(0.039f, 0.023f, 0), Damping: 0.1f, SwingDegrees: 20f, TipGravity: 0.1f, FollowAnimation: 0.05f,
                Firmness: 0.04f)),
        new("belly-sides-ivcs", PhysicsPart.Belly, "Belly sides", "IVCS", ["iv_fukubu_phys_l", "iv_fukubu_phys_r"], true, ChestGroup),
        new("thighs-ivcs", PhysicsPart.Thighs, "Thighs", "IVCS", ["iv_daitai_phys_l", "iv_daitai_phys_r"], true, ChestGroup,
            new PhysicsTemplate(new Vector3(0.092f, 0, 0), Damping: 0.05f, SwingDegrees: 25f, TipGravity: 0.1f, FollowAnimation: 0.1f,
                Firmness: 0.015f)),
        new("thighs-yas", PhysicsPart.Thighs, "Thighs", "YAS", ["ya_daitai_phys_l", "ya_daitai_phys_r"], true, ChestGroup,
            new PhysicsTemplate(new Vector3(0, 0.072f, 0), Damping: 0.05f, SwingDegrees: 25f, TipGravity: 0.1f, FollowAnimation: 0.1f,
                Firmness: 0.05f)),
        new("groin-ivcs", PhysicsPart.Groin, "Groin", "IVCS",
            ["iv_kougan_l", "iv_kougan_r", "iv_kintama_phys_l", "iv_kintama_phys_r", "iv_kuritto"], true, ChestGroup, BonePrefix: null),
        new("groin-ivcs-shaft", PhysicsPart.Groin, "Groin", "IVCS", [], true, ChestGroup, BonePrefix: "iv_ochinko_"),
        new("groin-ivcs-soft", PhysicsPart.Groin, "Groin", "IVCS", [], true, ChestGroup, BonePrefix: "iv_funyachin_phy_"),
        new("ears", PhysicsPart.Ears, "Ears", "Vanilla", ["j_mimi_l", "j_mimi_r"], false, EarsGroup,
            new PhysicsTemplate(new Vector3(0.1f, 0, 0), Damping: 0.05f, SwingDegrees: 15f, TipGravity: 0.1f, FollowAnimation: 0.1f,
                Firmness: 0.05f)),
        new("skirt", PhysicsPart.Skirt, "Skirt", "Vanilla", [], false, 2, BonePrefix: "j_sk_"),
        new("hair", PhysicsPart.Hair, "Hair", "Vanilla", ["j_kami_a", "j_kami_b"], false, 4),
        new("front-hair", PhysicsPart.FrontHair, "Front hair", "Vanilla", ["j_kami_f_l", "j_kami_f_r"], false, 5),
        new("earrings", PhysicsPart.Earrings, "Earrings", "Vanilla", ["n_ear_a_l", "n_ear_a_r", "n_ear_b_l", "n_ear_b_r"], false, 8),
        new("tail", PhysicsPart.Tail, "Tail", "Vanilla", [], false, 14, BonePrefix: "n_sippo_"),
    ];

    public static PhysicsAreaKind? Find(string key) => Kinds.FirstOrDefault(kind => kind.Key == key);

    /// <summary> The area a chain's bone belongs to, if any. </summary>
    public static PhysicsAreaKind? KindOf(string bone)
        => bone.Length == 0 ? null : Kinds.FirstOrDefault(kind => kind.Owns(bone));

    /// <summary> The bones a simulator's chains start from, in order. </summary>
    public static IReadOnlyList<string> RootBones(PhybSimulator simulator)
        => simulator.Chains.Where(chain => chain.Nodes.Count > 0).Select(chain => chain.Nodes[0].Bone.Text).ToList();

    /// <summary> What a simulator moves: one area's name, the areas it mixes, or its bones. </summary>
    public static PhysicsAreaInfo Classify(PhybSimulator simulator)
    {
        var bones = RootBones(simulator);
        var kinds = bones.Select(KindOf).ToList();
        var known = kinds.OfType<PhysicsAreaKind>().Distinct().ToList();
        if (bones.Count == 0)
            return new PhysicsAreaInfo("Empty", null, [], bones, true);
        if (kinds.Any(kind => kind is null))
        {
            var names = bones.Where(bone => KindOf(bone) is null).Distinct().ToList();
            var shown = string.Join(", ", names.Take(2)) + (names.Count > 2 ? $" and {names.Count - 2} more" : "");
            return new PhysicsAreaInfo(known.Count == 0 ? shown : $"{Label(known)}, {shown}", null, known, bones, false);
        }
        var parts = known.Select(kind => kind.Part).Distinct().ToList();
        // Simulators that move one side only, like the game's front hair, are told apart by it.
        var side = bones.All(bone => bone.EndsWith("_l", StringComparison.Ordinal)) ? ", left"
            : bones.All(bone => bone.EndsWith("_r", StringComparison.Ordinal)) ? ", right"
            : string.Empty;
        return new PhysicsAreaInfo(Label(known) + side, parts.Count == 1 ? parts[0] : null, known, bones, true);
    }

    /// <summary> "Butt (IVCS)", "Butt (IVCS, YAS)" or "Butt (IVCS), Thighs (YAS)". </summary>
    private static string Label(IReadOnlyList<PhysicsAreaKind> kinds)
        => string.Join(", ", kinds.GroupBy(kind => kind.Label).Select(group =>
        {
            var skeletons = group.Select(kind => kind.Skeleton).Where(skeleton => skeleton != "Vanilla").Distinct().ToList();
            return skeletons.Count == 0 ? group.Key : $"{group.Key} ({string.Join(", ", skeletons)})";
        }));

    /// <summary> The mirrored bone of a left or right one ("j_mune_l" and "j_mune_r"), or null. </summary>
    public static string? Mirror(string bone)
    {
        if (bone.EndsWith("_l", StringComparison.Ordinal))
            return bone[..^1] + "r";
        if (bone.EndsWith("_r", StringComparison.Ordinal))
            return bone[..^1] + "l";
        return null;
    }

    /// <summary> The chain whose bone mirrors this chain's, anywhere in the file, or null. </summary>
    public static (int Simulator, int Chain)? MirrorChain(PhybFile file, int simulator, int chain)
    {
        var chains = file.Simulators[simulator].Chains;
        if (chain < 0 || chain >= chains.Count || chains[chain].Nodes.Count == 0 || Mirror(chains[chain].Nodes[0].Bone.Text) is not { } mirror)
            return null;
        // The same simulator first: most files keep both sides together.
        for (var c = 0; c < chains.Count; c++)
            if (c != chain && chains[c].Nodes.Count > 0 && chains[c].Nodes[0].Bone.Text == mirror)
                return (simulator, c);
        for (var s = 0; s < file.Simulators.Count; s++)
            for (var c = 0; c < file.Simulators[s].Chains.Count; c++)
                if (s != simulator && file.Simulators[s].Chains[c].Nodes.Count > 0 && file.Simulators[s].Chains[c].Nodes[0].Bone.Text == mirror)
                    return (s, c);
        return null;
    }
}

/// <summary> The groups of the game's PhysicsGroup sheet the builder offers, with what each does. </summary>
internal static class PhysicsGroups
{
    /// <summary> Hair groups: hats that turn off hair physics stop them all together. </summary>
    public static bool IsHair(byte group) => group is 3 or 4 or 5 or 6 or 23 or 24 or 25;

    public static readonly IReadOnlyList<(byte Group, string Label, string Detail)> Choices =
    [
        (PhysicsAreas.ChestGroup, "Chest", "What the game's breasts use. Tops that turn off breast physics stop it."),
        (1, "Not tied to gear", "No gear turns it off."),
        (2, "Skirt", "What the game's skirt bones use."),
        (4, "Hair", "Hats that turn off hair physics stop it."),
        (8, "Earrings", "What the game's earrings use."),
        (14, "Tail", "What the game's tails use."),
        (PhysicsAreas.EarsGroup, "Ears", "What Viera ears use."),
    ];

    public static string Label(byte group)
    {
        foreach (var choice in Choices)
            if (choice.Group == group)
                return $"{choice.Label} ({group})";
        return IsHair(group) ? $"Hair ({group})" : $"Group {group}";
    }
}
