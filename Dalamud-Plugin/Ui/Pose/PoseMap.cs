using System.Collections.Immutable;
using System.Numerics;
using System.Text.RegularExpressions;

namespace InstantEdit.Ui.Pose;

/// <summary>A bone's dot on the bone map: where it sits on its picture, in the picture's pixels. A handle draws no lines to other dots.</summary>
internal readonly record struct PoseSpot(string Bone, Vector2 Position, bool Handle = false);

/// <summary>
/// One picture of the bone map: its embedded image, its size in pixels and the bones it shows. A
/// mirrored picture lists the left side's bones; their right-side twins sit mirrored across its middle.
/// </summary>
internal sealed record PoseSection(string Key, string Image, Vector2 Size, bool Mirrored, ImmutableArray<PoseSpot> Spots);

/// <summary>Where a picture is drawn: its top left in the map's area and its pixels' scale.</summary>
internal readonly record struct PosePlacement(PoseSection Section, Vector2 Origin, float Scale);

/// <summary>Bones the Bones window selects in one click, such as a hand's fingers.</summary>
internal sealed record PoseGroup(string Name, Func<string, bool> Contains);

/// <summary>
/// The Bones window's bone map: Brio's posing artwork and bone positions (GPL-3.0, see
/// THIRD-PARTY-NOTICES.md). Only the body's pictures: a body clip never moves face bones.
/// </summary>
internal static class PoseMap
{
    /// <summary>The front of the body. Body sits on the dot between the feet: it carries everything.</summary>
    public static readonly PoseSection Body = new("body", "PoseBody", new Vector2(1270, 2160), true,
    [
        new("n_hara", new(635, 2110), true),
        new("j_kao", new(635, 200)),
        new("j_kubi", new(635, 325)),
        new("j_sebo_c", new(635, 440)),
        new("j_sebo_b", new(635, 620)),
        new("j_sebo_a", new(635, 760)),
        new("j_kosi", new(635, 870)),
        new("iv_shiri_l", new(350, 1160)),
        new("j_asi_a_l", new(530, 1120)),
        new("j_asi_b_l", new(530, 1410)),
        new("j_asi_c_l", new(530, 1530)),
        new("j_asi_d_l", new(530, 1900)),
        new("j_asi_e_l", new(530, 2000)),
        new("j_mune_l", new(520, 545)),
        new("j_sako_l", new(510, 425)),
        new("n_hkata_l", new(425, 420)),
        new("j_ude_a_l", new(423, 485)),
        new("j_ude_b_l", new(280, 730)),
        new("n_hhiji_l", new(355, 740)),
        new("n_hte_l", new(170, 970)),
    ]);

    /// <summary>Hair, skirt, armor and weapon bones on a clothed body.</summary>
    public static readonly PoseSection Armor = new("armor", "PoseArmor", new Vector2(1311, 2148), true,
    [
        new("j_kami_a", new(655.5f, 165)),
        new("j_kami_b", new(655.5f, 280)),
        new("j_kami_f_l", new(560, 165)),
        new("n_kataarmor_l", new(470, 450)),
        new("j_buki_sebo_l", new(550, 450)),
        new("j_buki2_kosi_l", new(477, 772)),
        new("j_buki_kosi_l", new(445, 850)),
        new("j_sk_s_a_l", new(433, 980)),
        new("j_sk_s_b_l", new(381, 1276)),
        new("j_sk_s_c_l", new(304, 1689)),
        new("j_sk_f_a_l", new(540, 980)),
        new("j_sk_f_b_l", new(520, 1300)),
        new("j_sk_f_c_l", new(470, 1750)),
        new("j_sk_b_a_l", new(604, 980)),
        new("j_sk_b_b_l", new(604, 1300)),
        new("j_sk_b_c_l", new(604, 1750)),
        new("n_hizasoubi_l", new(547, 1521)),
    ]);

    /// <summary>Both hands from the back, with the wrist and the weapon bone.</summary>
    public static readonly PoseSection Hands = new("hands", "PoseHand", new Vector2(2152, 1532), true,
    [
        new("j_oya_a_l", new(830, 1000)),
        new("j_oya_b_l", new(920, 830)),
        new("j_hito_a_l", new(645, 850)),
        new("j_hito_b_l", new(630, 540)),
        new("iv_hito_c_l", new(630, 250)),
        new("j_naka_a_l", new(440, 850)),
        new("j_naka_b_l", new(430, 540)),
        new("iv_naka_c_l", new(430, 250)),
        new("j_kusu_a_l", new(280, 850)),
        new("j_kusu_b_l", new(250, 540)),
        new("iv_kusu_c_l", new(250, 250)),
        new("j_ko_a_l", new(140, 920)),
        new("j_ko_b_l", new(120, 660)),
        new("iv_ko_c_l", new(80, 400)),
        new("n_hte_l", new(455, 1485)),
        new("j_te_l", new(450, 1320)),
        new("n_buki_l", new(450, 1100)),
    ]);

    /// <summary>The toes IVCS adds, from above.</summary>
    public static readonly PoseSection Toes = new("toes", "PoseToes", new Vector2(2152, 773), true,
    [
        new("j_asi_e_l", new(600, 650)),
        new("iv_asi_oya_a_l", new(830, 400)),
        new("iv_asi_oya_b_l", new(830, 175)),
        new("iv_asi_hito_a_l", new(630, 420)),
        new("iv_asi_hito_b_l", new(630, 200)),
        new("iv_asi_naka_a_l", new(480, 460)),
        new("iv_asi_naka_b_l", new(450, 240)),
        new("iv_asi_kusu_a_l", new(330, 540)),
        new("iv_asi_kusu_b_l", new(310, 360)),
        new("iv_asi_ko_a_l", new(180, 620)),
        new("iv_asi_ko_b_l", new(160, 460)),
    ]);

    /// <summary>A tail from the side.</summary>
    public static readonly PoseSection Tail = new("tail", "PoseTail", new Vector2(1892, 1949), false,
    [
        new("n_sippo_a", new(216, 300)),
        new("n_sippo_b", new(570, 462)),
        new("n_sippo_c", new(1046, 885)),
        new("n_sippo_d", new(1177, 1380)),
        new("n_sippo_e", new(1727, 1807)),
    ]);

    public static readonly ImmutableArray<PoseSection> Sections = [Body, Armor, Hands, Toes, Tail];

    /// <summary>Every bone a picture shows, both sides.</summary>
    public static readonly ImmutableHashSet<string> Placed =
        Sections.SelectMany(section => Place(section, false)).Select(spot => spot.Bone).ToImmutableHashSet(StringComparer.Ordinal);

    /// <summary>The embedded image of <paramref name="section"/>, as the plugin's manifest names it.</summary>
    public static string Resource(PoseSection section) => $"InstantEdit.Pose.{section.Image}.png";

    /// <summary>The bone on the other side: _r for _l and back. Null for a bone in the middle.</summary>
    public static string? Mirror(string bone) =>
        bone.Length > 2 && bone[^2] == '_' ? bone[^1] switch { 'l' => bone[..^1] + "r", 'r' => bone[..^1] + "l", _ => null } : null;

    /// <summary>Which side a bone is on: 'l', 'r', or '\0' in the middle.</summary>
    public static char Side(string bone) => Mirror(bone) == null ? '\0' : bone[^1];

    /// <summary>
    /// Where a section's bones sit on its picture, in pixels: every spot and, on a mirrored picture, its
    /// twin across the middle. The left side's bones show on the picture's left, as in a mirror;
    /// <paramref name="swapSides"/> puts them on the right, as seen from the front.
    /// </summary>
    public static IEnumerable<PoseSpot> Place(PoseSection section, bool swapSides)
    {
        foreach (var spot in section.Spots)
        {
            if (!section.Mirrored)
            {
                yield return spot;
                continue;
            }
            var across = spot with { Position = spot.Position with { X = section.Size.X - spot.Position.X } };
            yield return swapSides ? across : spot;
            if (Mirror(spot.Bone) is { } twin)
                yield return (swapSides ? spot : across) with { Bone = twin };
        }
    }

    /// <summary>
    /// Where each picture goes in an area of <paramref name="area"/> pixels, in columns like Brio's: the
    /// body, the armor picture when the skeleton has its bones, then the hands with the toes and the
    /// tail below them when it has those. Pictures keep their proportions and sit at the top.
    /// </summary>
    public static List<PosePlacement> Layout(Vector2 area, float gap, bool armor, bool toes, bool tail)
    {
        var columns = armor ? 3 : 2;
        var width = Math.Max(1, (area.X - gap * (columns - 1)) / columns);
        var height = Math.Max(1, area.Y);
        var placements = new List<PosePlacement> { Fit(Body, new Vector2(0, 0), new Vector2(width, height)) };
        var x = width + gap;
        if (armor)
        {
            placements.Add(Fit(Armor, new Vector2(x, 0), new Vector2(width, height)));
            x += width + gap;
        }
        List<PoseSection> stack = [Hands];
        if (toes)
            stack.Add(Toes);
        if (tail)
            stack.Add(Tail);
        // As wide as the column, or all of them smaller together when they would be too tall.
        var gaps = gap * (stack.Count - 1);
        var tall = stack.Sum(section => section.Size.Y * width / section.Size.X);
        var shrink = Math.Min(1, Math.Max(1, height - gaps) / Math.Max(1, tall));
        var y = 0f;
        foreach (var section in stack)
        {
            var scale = width / section.Size.X * shrink;
            var size = section.Size * scale;
            placements.Add(new PosePlacement(section, new Vector2(x + (width - size.X) / 2, y), scale));
            y += size.Y + gap;
        }
        return placements;
    }

    private static PosePlacement Fit(PoseSection section, Vector2 at, Vector2 box)
    {
        var scale = Math.Min(box.X / section.Size.X, box.Y / section.Size.Y);
        return new PosePlacement(section, at + new Vector2((box.X - section.Size.X * scale) / 2, 0), scale);
    }

    private static readonly Regex FingerName = new(@"^(?:j|iv)_(?:oya|hito|naka|kusu|ko)_[abc]_([lr])$", RegexOptions.CultureInvariant);
    private static readonly Regex ArmName = new(@"^(?:j_sako|n_hkata|j_ude_[ab]|n_hhiji|n_hte|j_te|iv_nitoukin)_([lr])$", RegexOptions.CultureInvariant);
    private static readonly Regex LegName = new(@"^(?:j_asi_[a-e]|iv_asi_(?:oya|hito|naka|kusu|ko)_[ab])_([lr])$", RegexOptions.CultureInvariant);

    /// <summary>The groups the Select menu offers, each only when the skeleton has one of its bones.</summary>
    public static readonly ImmutableArray<PoseGroup> Groups =
    [
        new("Spine", bone => bone is "j_kosi" or "j_sebo_a" or "j_sebo_b" or "j_sebo_c"),
        new("Neck and head", bone => bone is "j_kubi" or "j_kao"),
        new("Left arm", bone => Sided(ArmName, bone, 'l')),
        new("Right arm", bone => Sided(ArmName, bone, 'r')),
        new("Left fingers", bone => Sided(FingerName, bone, 'l')),
        new("Right fingers", bone => Sided(FingerName, bone, 'r')),
        new("Left leg", bone => Sided(LegName, bone, 'l')),
        new("Right leg", bone => Sided(LegName, bone, 'r')),
        new("Tail", bone => bone.StartsWith("n_sippo_", StringComparison.Ordinal)),
        new("Hair", bone => bone.StartsWith("j_kami_", StringComparison.Ordinal) || bone.StartsWith("j_ex_h", StringComparison.Ordinal)),
        new("Skirt", bone => bone.StartsWith("j_sk_", StringComparison.Ordinal)),
        new("Armor and weapons", bone => bone.StartsWith("n_kataarmor_", StringComparison.Ordinal) ||
                                          bone.StartsWith("n_hizasoubi_", StringComparison.Ordinal) ||
                                          bone.StartsWith("n_hijisoubi_", StringComparison.Ordinal) ||
                                          bone.StartsWith("j_buki", StringComparison.Ordinal) ||
                                          bone.StartsWith("n_buki", StringComparison.Ordinal)),
    ];

    private static bool Sided(Regex pattern, string bone, char side) =>
        pattern.Match(bone) is { Success: true } match && match.Groups[1].Value[0] == side;
}
