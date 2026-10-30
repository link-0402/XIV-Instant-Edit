using System.Collections.Immutable;
using System.Numerics;
using System.Text.RegularExpressions;
using InstantEdit.Models;
using InstantEdit.Services.Animations.Motion;

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
/// THIRD-PARTY-NOTICES.md). The body's pictures for a clip, a head picture for a face (<see cref="FaceSection"/>):
/// a body clip never moves face bones.
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

    /// <summary>
    /// The groin bones IVCS adds, on the hips from the front. Laid out to be told apart rather than where
    /// they sit: the penis chain runs up the belly, the female bones down between the legs, front to back.
    /// </summary>
    public static readonly PoseSection Groin = new("groin", "PoseGroin", new Vector2(500, 420), true,
    [
        new("iv_ochinko_a", new(250, 235)),
        new("iv_ochinko_b", new(250, 195)),
        new("iv_ochinko_c", new(250, 155)),
        new("iv_ochinko_d", new(250, 115)),
        new("iv_ochinko_e", new(250, 75)),
        new("iv_ochinko_f", new(250, 35)),
        new("iv_kougan_l", new(190, 250)),
        new("iv_kuritto", new(250, 290)),
        new("iv_omanko", new(250, 340)),
        new("iv_inshin_l", new(195, 340)),
        new("iv_koumon", new(250, 390)),
        new("iv_koumon_l", new(195, 390)),
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

    // The head pictures: Brio's (GraphicalBonePosePositions.json at Etheirys/Brio fd01093), the face from the front with the
    // teeth and tongue as handles beside it. Each lists the left side and the middle; the right side mirrors across it.

    /// <summary>A Hyur, Elezen, Lalafell, Roegadyn or Au Ra face, with ears.</summary>
    public static readonly PoseSection HumanHead = new("human-head", "PoseHeadWithEars", new Vector2(917, 776), true,
    [
        new("j_mimi_l", new(102, 330)), new("n_ear_a_l", new(102, 390)), new("n_ear_b_l", new(102, 450)),
        new("j_f_mayu_l", new(250, 160)), new("j_f_mmayu_l", new(326, 175)), new("j_f_miken_01_l", new(380, 197)), new("j_f_miken_02_l", new(407, 211)),
        new("j_f_mabup_01_l", new(310, 200)), new("j_f_eye_l", new(310, 250)), new("j_f_mabdn_01_l", new(310, 300)),
        new("j_f_bero_01", new(750, 540), true), new("j_f_bero_02", new(750, 580), true), new("j_f_bero_03", new(750, 620), true),
        new("j_f_dmemoto_l", new(350, 450)), new("j_f_hoho_l", new(310, 390)), new("j_f_dhoho_l", new(310, 450)), new("j_f_shoho_l", new(310, 510)),
        new("j_f_hagukiup", new(310, 620), true), new("j_f_hagukidn", new(310, 660), true),
        new("j_f_uhana", new(458.5f, 294)), new("j_f_hana_l", new(420.5f, 420)),
        new("j_f_umlip_01_l", new(390, 490)), new("j_f_umlip_02_l", new(390, 520)), new("j_f_ulip_01_l", new(438, 490)), new("j_f_ulip_02_l", new(438, 520)),
        new("j_f_dlip_01_l", new(435, 610)), new("j_f_dlip_02_l", new(435, 580)), new("j_f_dmlip_01_l", new(390, 610)), new("j_f_dmlip_02_l", new(390, 580)),
        new("j_f_dslip_l", new(360, 570)), new("j_f_uslip_l", new(360, 540)),
        new("j_f_ago", new(458.5f, 700)),
    ]);

    /// <summary>A Miqo'te face, ears on top.</summary>
    public static readonly PoseSection MiqoteHead = new("miqote-head", "PoseHeadMiqote", new Vector2(1142, 1106), true,
    [
        new("j_mimi_l", new(142, 113)), new("n_ear_a_l", new(77, 183)), new("n_ear_b_l", new(77, 263)),
        new("j_f_mayu_l", new(311, 427)), new("j_f_mmayu_l", new(400, 457)),
        new("j_f_hagukiup", new(310, 970), true), new("j_f_hagukidn", new(310, 1010), true),
        new("j_f_miken_01_l", new(467, 482)), new("j_f_miken_02_l", new(509, 501)),
        new("j_f_mabup_01_l", new(395, 496)), new("j_f_eye_l", new(395, 548)), new("j_f_mabdn_01_l", new(395, 613)),
        new("j_f_bero_01", new(880, 870), true), new("j_f_bero_02", new(880, 920), true), new("j_f_bero_03", new(880, 970), true),
        new("j_f_dmemoto_l", new(440, 780)), new("j_f_hoho_l", new(395, 700)), new("j_f_dhoho_l", new(395, 780)), new("j_f_shoho_l", new(395, 860)),
        new("j_f_uhana", new(571, 617)), new("j_f_hana_l", new(540, 740)),
        new("j_f_umlip_01_l", new(500, 820)), new("j_f_umlip_02_l", new(500, 860)), new("j_f_ulip_01_l", new(545, 820)), new("j_f_ulip_02_l", new(545, 863)),
        new("j_f_dlip_01_l", new(545, 963)), new("j_f_dlip_02_l", new(545, 920)), new("j_f_dmlip_01_l", new(500, 963)), new("j_f_dmlip_02_l", new(500, 920)),
        new("j_f_ago", new(571, 1043)), new("j_f_uslip_l", new(460, 863)), new("j_f_dslip_l", new(460, 893)),
    ]);

    /// <summary>A Hrothgar face, with its whiskers and outer lips.</summary>
    public static readonly PoseSection HrothgarHead = new("hrothgar-head", "PoseHeadHroth", new Vector2(1393, 1383), true,
    [
        new("n_ear_a_l", new(145, 180)), new("n_ear_b_l", new(145, 280)),
        new("j_f_mayu_l", new(340, 453)), new("j_f_mmayu_l", new(445, 480)), new("j_f_miken_01_l", new(535, 520)), new("j_f_miken_02_l", new(587, 547)),
        new("j_f_mabup_01_l", new(469, 543)),
        new("j_f_hagukiup", new(310, 1230), true), new("j_f_hagukidn", new(310, 1270), true),
        new("j_f_eye_l", new(469, 620)), new("j_f_mabdn_01_l", new(469, 690)), new("j_f_hige_l", new(450, 910)),
        new("j_f_bero_01", new(1100, 1130), true), new("j_f_bero_02", new(1100, 1180), true), new("j_f_bero_03", new(1100, 1230), true),
        new("j_f_dmemoto_l", new(500, 1050)), new("j_f_hoho_l", new(470, 980)), new("j_f_dhoho_l", new(450, 1050)), new("j_f_shoho_l", new(450, 1110)),
        new("n_f_ulip_l", new(480, 1075)), new("n_f_lip_l", new(480, 1160)),
        new("j_f_uhana", new(696.5f, 850)), new("j_f_hana_l", new(656.5f, 920)), new("j_f_uago", new(696.5f, 1040)),
        new("j_f_ulip_01_l", new(670, 1066)), new("j_f_ulip_02_l", new(670, 1116)), new("j_f_umlip_01_l", new(620, 1066)), new("j_f_umlip_02_l", new(620, 1116)),
        new("j_f_dlip_01_l", new(670, 1261)), new("j_f_dlip_02_l", new(670, 1201)), new("j_f_dmlip_01_l", new(620, 1261)), new("j_f_dmlip_02_l", new(620, 1201)),
        new("j_f_uslip_l", new(570, 1126)), new("j_f_dslip_l", new(570, 1176)),
        new("j_f_ago", new(696.5f, 1317)),
    ]);

    /// <summary>A Viera face; its ears' dots come from the skeleton (<see cref="FaceSection"/>).</summary>
    public static readonly PoseSection VieraHead = new("viera-head", "PoseHeadVieraFloppy", new Vector2(1054, 1041), true,
    [
        new("n_ear_a_l", new(234, 93)), new("n_ear_b_l", new(234, 160)),
        new("j_f_mayu_l", new(320, 590)), new("j_f_mmayu_l", new(395, 600)), new("j_f_miken_01_l", new(441, 610)), new("j_f_miken_02_l", new(485, 640)),
        new("j_f_mabup_01_l", new(400, 645)), new("j_f_eye_l", new(400, 683)),
        new("j_f_hagukiup", new(310, 960), true), new("j_f_hagukidn", new(310, 1000), true),
        new("j_f_mabdn_01_l", new(400, 727)),
        new("j_f_bero_01", new(750, 900), true), new("j_f_bero_02", new(750, 930), true), new("j_f_bero_03", new(750, 960), true),
        new("j_f_dmemoto_l", new(435, 850)), new("j_f_hoho_l", new(400, 800)), new("j_f_dhoho_l", new(400, 850)), new("j_f_shoho_l", new(400, 900)),
        new("j_f_uhana", new(527, 714)), new("j_f_hana_l", new(500, 827)),
        new("j_f_ulip_01_l", new(512, 864)), new("j_f_ulip_02_l", new(512, 894)), new("j_f_umlip_01_l", new(472, 864)), new("j_f_umlip_02_l", new(472, 894)),
        new("j_f_dlip_01_l", new(512, 975)), new("j_f_dlip_02_l", new(512, 945)), new("j_f_dmlip_01_l", new(472, 975)), new("j_f_dmlip_02_l", new(472, 945)),
        new("j_f_dslip_l", new(442, 925)), new("j_f_uslip_l", new(442, 885)),
        new("j_f_ago", new(527, 1010)),
    ]);

    /// <summary>A Viera's ear pairs (j_zera to j_zerd); each ear type's pair sits in the same spots on the picture.</summary>
    private static readonly ImmutableArray<char> VieraEarTypes = ['a', 'b', 'c', 'd'];

    public static readonly ImmutableArray<PoseSection> FaceSections = [HumanHead, MiqoteHead, HrothgarHead, VieraHead];

    public static readonly ImmutableArray<PoseSection> Sections = [Body, Armor, Hands, Toes, Groin, Tail];

    /// <summary>Every bone a body picture shows, both sides.</summary>
    public static readonly ImmutableHashSet<string> Placed =
        Sections.SelectMany(section => Place(section, false)).Select(spot => spot.Bone).ToImmutableHashSet(StringComparer.Ordinal);

    /// <summary>Every bone a face picture shows, both sides, a Viera's ears too: the dots a face session draws.</summary>
    public static readonly ImmutableHashSet<string> FacePlaced =
        FaceSections.SelectMany(section => Place(section, false)).Select(spot => spot.Bone)
            .Concat(VieraEarTypes.SelectMany(type => new[] { $"j_zer{type}_a_l", $"j_zer{type}_b_l", $"j_zer{type}_a_r", $"j_zer{type}_b_r" }))
            .ToImmutableHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The picture of a face of <paramref name="race"/> (such as "c0801"): Miqo'te, Hrothgar and Viera have their own,
    /// every other race the human one. A Viera's shows the ear pair its <paramref name="skeleton"/> has.
    /// </summary>
    public static PoseSection FaceSection(string race, SkeletonDescription skeleton)
    {
        switch (race)
        {
            case "c0701" or "c0801":
                return MiqoteHead;
            case "c1501" or "c1601":
                return HrothgarHead;
            case "c1701" or "c1801":
                var names = skeleton.Bones.Select(bone => bone.Name).ToHashSet(StringComparer.Ordinal);
                return VieraEarTypes.FirstOrDefault(type => names.Contains($"j_zer{type}_a_l") && names.Contains($"j_zer{type}_b_l")) is var type and not '\0'
                    ? VieraHead with { Spots = [.. VieraHead.Spots, new($"j_zer{type}_a_l", new(400, 155)), new($"j_zer{type}_b_l", new(90, 190))] }
                    : VieraHead;
            default:
                return HumanHead;
        }
    }

    /// <summary>The embedded image of <paramref name="section"/>, as the plugin's manifest names it.</summary>
    public static string Resource(PoseSection section) => $"InstantEdit.Pose.{section.Image}.png";

    /// <summary>The bone on the other side: _r for _l and back. Null for a bone in the middle.</summary>
    public static string? Mirror(string bone) => MotionMirror.Twin(bone);

    /// <summary>Which side a bone is on: 'l', 'r', or '\0' in the middle.</summary>
    public static char Side(string bone) => MotionMirror.Side(bone);

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

    /// <summary>The bones a picture shows as a handle, which always keep their dot.</summary>
    public static readonly ImmutableHashSet<string> Handles =
        Sections.SelectMany(section => section.Spots).Where(spot => spot.Handle).Select(spot => spot.Bone).ToImmutableHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Whether <paramref name="bone"/>'s dot shows: every dot with no <paramref name="used"/> bones, else a used one, a
    /// <paramref name="kept"/> one (selected, or changed by the selected layer) and a handle.
    /// </summary>
    public static bool Shows(string bone, IReadOnlySet<string>? used, bool kept) =>
        used == null || kept || used.Contains(bone) || Handles.Contains(bone);

    /// <summary>
    /// Which pictures besides the body and the hands <see cref="Layout"/> places: each one with a dot that
    /// <paramref name="shown"/> keeps, on either side. The toes count only for their IVCS bones, as the foot is on the body.
    /// </summary>
    public static (bool Armor, bool Toes, bool Groin, bool Tail) Extras(Func<string, bool> shown)
    {
        bool Any(PoseSection section, Func<string, bool> counts) =>
            Place(section, false).Any(spot => !spot.Handle && counts(spot.Bone) && shown(spot.Bone));
        return (Any(Armor, _ => true), Any(Toes, bone => bone.StartsWith("iv_", StringComparison.Ordinal)), Any(Groin, _ => true), Any(Tail, _ => true));
    }

    /// <summary>
    /// Where each picture goes in an area of <paramref name="area"/> pixels, in columns like Brio's: the
    /// body, the armor picture when the skeleton has its bones, then the hands with the toes, the groin
    /// and the tail below them when it has those. Pictures keep their proportions and sit at the top.
    /// </summary>
    public static List<PosePlacement> Layout(Vector2 area, float gap, bool armor, bool toes, bool groin, bool tail)
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
        if (groin)
            stack.Add(Groin);
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

    /// <summary>Where a face's picture goes in an area of <paramref name="area"/> pixels: as large as fits, at the top.</summary>
    public static List<PosePlacement> FaceLayout(PoseSection face, Vector2 area) => [Fit(face, Vector2.Zero, area)];

    private static PosePlacement Fit(PoseSection section, Vector2 at, Vector2 box)
    {
        var scale = Math.Min(box.X / section.Size.X, box.Y / section.Size.Y);
        return new PosePlacement(section, at + new Vector2((box.X - section.Size.X * scale) / 2, 0), scale);
    }

    private static readonly Regex FingerName = new(@"^(?:j|iv)_(?:oya|hito|naka|kusu|ko)_[abc]_([lr])$", RegexOptions.CultureInvariant);
    private static readonly Regex ArmName = new(@"^(?:j_sako|n_hkata|j_ude_[ab]|n_hhiji|n_hte|j_te|iv_nitoukin)_([lr])$", RegexOptions.CultureInvariant);
    private static readonly Regex LegName = new(@"^(?:j_asi_[a-e]|iv_asi_(?:oya|hito|naka|kusu|ko)_[ab])_([lr])$", RegexOptions.CultureInvariant);

    private static bool Starts(string bone, params string[] prefixes) => prefixes.Any(prefix => bone.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>The groups the Select menu offers on a face, each only when the skeleton has one of its bones.</summary>
    public static readonly ImmutableArray<PoseGroup> FaceGroups =
    [
        new("Brows", bone => Starts(bone, "j_f_mayu", "j_f_mmayu", "j_f_miken", "j_f_dmiken")),
        new("Eyes and lids", bone => Starts(bone, "j_f_eye", "j_f_mab", "j_f_dmab", "j_f_umab", "j_f_irisprm")),
        new("Mouth and lips", bone => bone.Contains("lip", StringComparison.Ordinal) && Starts(bone, "j_f_", "n_f_")),
        new("Jaw and teeth", bone => Starts(bone, "j_f_ago", "j_f_dago", "j_f_uago", "j_f_haguki")),
        new("Tongue", bone => Starts(bone, "j_f_bero")),
        new("Cheeks", bone => Starts(bone, "j_f_hoho", "j_f_dhoho", "j_f_shoho", "j_f_dmemoto")),
        new("Nose", bone => Starts(bone, "j_f_hana", "j_f_uhana", "j_f_memoto")),
    ];

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
        new("Groin", bone => Groin.Spots.Any(spot => spot.Bone == bone || Mirror(spot.Bone) == bone)),
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
