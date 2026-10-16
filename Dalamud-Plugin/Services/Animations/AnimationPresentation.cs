using System.Collections.Immutable;
using InstantEdit.Models;

namespace InstantEdit.Services.Animations;

/// <summary>Turns runtime animation and skeleton identifiers into stable UI language.</summary>
internal static class AnimationPresentation
{
    internal sealed record ListItem(AnimationCapture Capture, bool Startup, bool SeparatorBefore);

    // The body's bones by their game names, after Brio's names for them (see THIRD-PARTY-NOTICES.md).
    private static readonly ImmutableDictionary<string, string> BoneLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["n_root"] = "Root", ["n_hara"] = "Body", ["n_throw"] = "Throw", ["j_kosi"] = "Hips",
        ["j_sebo_a"] = "Lower Spine", ["j_sebo_b"] = "Middle Spine", ["j_sebo_c"] = "Upper Spine",
        ["j_kubi"] = "Neck", ["j_kao"] = "Head", ["j_kami_a"] = "Hair A", ["j_kami_b"] = "Hair B",
        ["n_sippo_a"] = "Tail A", ["n_sippo_b"] = "Tail B", ["n_sippo_c"] = "Tail C", ["n_sippo_d"] = "Tail D", ["n_sippo_e"] = "Tail E",
        ["iv_ochinko_a"] = "Penis A", ["iv_ochinko_b"] = "Penis B", ["iv_ochinko_c"] = "Penis C",
        ["iv_ochinko_d"] = "Penis D", ["iv_ochinko_e"] = "Penis E", ["iv_ochinko_f"] = "Penis F",
        ["iv_kuritto"] = "Clitoris", ["iv_omanko"] = "Vagina", ["iv_koumon"] = "Anus",
        ["iv_funyachin_phy_a"] = "Soft Penis A", ["iv_funyachin_phy_b"] = "Soft Penis B",
        ["iv_funyachin_phy_c"] = "Soft Penis C", ["iv_funyachin_phy_d"] = "Soft Penis D",
        ["iv_fukubu_phys"] = "Upper Belly", ["ya_fukubu_phys"] = "Lower Belly",
        // Face bones, after the XIV Modding asset reference's bone list. Dawntrail heads first, then older ones.
        ["j_f_ago"] = "Jaw", ["j_f_dago"] = "Chin", ["j_f_hagukiup"] = "Upper Teeth", ["j_f_hagukidn"] = "Lower Teeth",
        ["j_f_bero_01"] = "Tongue 1", ["j_f_bero_02"] = "Tongue 2", ["j_f_bero_03"] = "Tongue 3", ["j_f_uhana"] = "Nose Bridge",
        ["j_ago"] = "Jaw", ["j_f_face"] = "Face", ["j_f_hana"] = "Nose", ["j_f_memoto"] = "Nose Bridge",
        ["j_f_uago"] = "Upper Lip A", ["j_f_ulip"] = "Upper Lip B", ["j_f_ulip_a"] = "Upper Lip A", ["j_f_ulip_b"] = "Upper Lip B",
        ["j_f_dlip"] = "Lower Lip", ["j_f_dlip_a"] = "Lower Lip A", ["j_f_dlip_b"] = "Lower Lip B",
        // Gear and weapon bones.
        ["mh_n_root"] = "Main Hand Root", ["mh_n_hara"] = "Main Hand Center", ["oh_n_root"] = "Off Hand Root", ["oh_n_hara"] = "Off Hand Center",
        ["j_ex_met_va"] = "Visor A", ["j_ex_met_vb"] = "Visor B", ["j_ex_met_a"] = "Hat A", ["j_ex_met_b"] = "Hat B",
        ["j_ex_top_a"] = "Coat Tail A", ["j_ex_top_b"] = "Coat Tail B", ["j_ex_top_c"] = "Coat Tail C", ["j_ex_top_d"] = "Coat Tail D",
    }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);

    // Bones with a left and a right twin, by their name before the _l or _r.
    private static readonly ImmutableDictionary<string, string> SidedLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["j_sako"] = "Clavicle", ["n_hkata"] = "Shoulder", ["j_ude_a"] = "Upper Arm", ["j_ude_b"] = "Forearm",
        ["n_hhiji"] = "Elbow", ["n_hte"] = "Wrist", ["j_te"] = "Hand", ["j_mune"] = "Breast",
        ["j_asi_a"] = "Thigh", ["j_asi_b"] = "Knee", ["j_asi_c"] = "Calf", ["j_asi_d"] = "Foot", ["j_asi_e"] = "Toes",
        ["j_oya_a"] = "Thumb A", ["j_oya_b"] = "Thumb B",
        ["j_hito_a"] = "Index A", ["j_hito_b"] = "Index B", ["iv_hito_c"] = "Index C",
        ["j_naka_a"] = "Middle A", ["j_naka_b"] = "Middle B", ["iv_naka_c"] = "Middle C",
        ["j_kusu_a"] = "Ring A", ["j_kusu_b"] = "Ring B", ["iv_kusu_c"] = "Ring C",
        ["j_ko_a"] = "Pinky A", ["j_ko_b"] = "Pinky B", ["iv_ko_c"] = "Pinky C",
        ["iv_asi_oya_a"] = "Big Toe A", ["iv_asi_oya_b"] = "Big Toe B", ["iv_asi_hito_a"] = "Second Toe A", ["iv_asi_hito_b"] = "Second Toe B",
        ["iv_asi_naka_a"] = "Middle Toe A", ["iv_asi_naka_b"] = "Middle Toe B", ["iv_asi_kusu_a"] = "Fourth Toe A", ["iv_asi_kusu_b"] = "Fourth Toe B",
        ["iv_asi_ko_a"] = "Little Toe A", ["iv_asi_ko_b"] = "Little Toe B", ["iv_shiri"] = "Buttock", ["iv_nitoukin"] = "Bicep",
        ["iv_kougan"] = "Testicle", ["iv_inshin"] = "Labia", ["iv_koumon"] = "Anus",
        ["iv_c_mune"] = "Breast B", ["iv_kyokin_phys"] = "Pectoral", ["iv_kintama_phys"] = "Testicle Physics", ["iv_fukubu_phys"] = "Upper Belly",
        ["iv_daitai_phys"] = "Back Thigh", ["ya_daitai_phys"] = "Front Thigh", ["ya_shiri_phys"] = "Lower Buttock",
        ["j_zera_a"] = "Ear A Base", ["j_zera_b"] = "Ear A Tip", ["j_zerb_a"] = "Ear B Base", ["j_zerb_b"] = "Ear B Tip",
        ["j_zerc_a"] = "Ear C Base", ["j_zerc_b"] = "Ear C Tip", ["j_zerd_a"] = "Ear D Base", ["j_zerd_b"] = "Ear D Tip",
        ["j_ex_top_a"] = "Sleeve A", ["j_ex_top_b"] = "Sleeve B",
        // Face bones, after the XIV Modding asset reference's bone list. Dawntrail heads first, then older ones.
        ["j_f_umlip_01"] = "Upper Outer Lip", ["j_f_umlip_02"] = "Upper Outer Lip Inner",
        ["j_f_ulip_01"] = "Upper Lip", ["j_f_ulip_02"] = "Upper Lip Inner", ["j_f_uslip"] = "Upper Lip Corner",
        ["j_f_dmlip_01"] = "Lower Outer Lip", ["j_f_dmlip_02"] = "Lower Outer Lip Inner",
        ["j_f_dlip_01"] = "Lower Lip", ["j_f_dlip_02"] = "Lower Lip Inner", ["j_f_dslip"] = "Lower Lip Corner", ["j_f_hige"] = "Whisker",
        ["j_f_mabup_01"] = "Upper Eyelid", ["j_f_mabup_02out"] = "Upper Eyelid Outer", ["j_f_mabup_03in"] = "Upper Eyelid Inner",
        ["j_f_mabdn_01"] = "Lower Eyelid", ["j_f_mabdn_02out"] = "Lower Eyelid Outer", ["j_f_mabdn_03in"] = "Lower Eyelid Inner",
        ["j_f_mayu"] = "Outer Eyebrow", ["j_f_mmayu"] = "Inner Eyebrow", ["j_f_miken_01"] = "Brow Ridge", ["j_f_miken_02"] = "Inner Brow Ridge",
        ["j_f_eye"] = "Eye", ["j_f_mab"] = "Eye Socket", ["j_f_eyepuru"] = "Eyeball", ["j_f_irisprm"] = "Iris",
        ["j_f_eyeprm_01"] = "Iris B", ["j_f_eyeprm_02"] = "Iris C",
        ["j_f_hana"] = "Nostril", ["j_f_dmiken"] = "Between the Eyes",
        ["j_f_hoho"] = "Cheek", ["j_f_dhoho"] = "Outer Cheek", ["j_f_shoho"] = "Middle Cheek", ["j_f_dmemoto"] = "Inner Cheek",
        ["j_f_dmab"] = "Lower Eyelid", ["j_f_umab"] = "Upper Eyelid", ["j_f_miken"] = "Brow",
        ["j_f_lip"] = "Lip", ["n_f_lip"] = "Lip", ["n_f_ulip"] = "Upper Lip",
        ["j_kami_f"] = "Front Hair", ["j_mimi"] = "Ear", ["n_ear_a"] = "Earring A", ["n_ear_b"] = "Earring B",
        ["n_buki"] = "Weapon", ["n_buki_tate"] = "Shield", ["j_buki_sebo"] = "Scabbard", ["j_buki_kosi"] = "Sheath", ["j_buki2_kosi"] = "Holster",
        ["n_kataarmor"] = "Pauldron", ["n_hizasoubi"] = "Knee Guard", ["n_hijisoubi"] = "Elbow Guard",
        ["j_sk_f_a"] = "Skirt Front A", ["j_sk_f_b"] = "Skirt Front B", ["j_sk_f_c"] = "Skirt Front C",
        ["j_sk_b_a"] = "Skirt Back A", ["j_sk_b_b"] = "Skirt Back B", ["j_sk_b_c"] = "Skirt Back C",
        ["j_sk_s_a"] = "Skirt Side A", ["j_sk_s_b"] = "Skirt Side B", ["j_sk_s_c"] = "Skirt Side C",
    }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly ImmutableDictionary<string, string> ModelLabels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["c0101"] = "Male Midlander", ["c0201"] = "Female Midlander",
        ["c0301"] = "Male Highlander", ["c0401"] = "Female Highlander",
        ["c0501"] = "Male Elezen", ["c0601"] = "Female Elezen",
        ["c0701"] = "Male Miqo'te", ["c0801"] = "Female Miqo'te",
        ["c0901"] = "Male Roegadyn", ["c1001"] = "Female Roegadyn",
        ["c1101"] = "Male Lalafell", ["c1201"] = "Female Lalafell",
        ["c1301"] = "Male Au Ra", ["c1401"] = "Female Au Ra",
        ["c1501"] = "Male Hrothgar", ["c1601"] = "Female Hrothgar",
        ["c1701"] = "Male Viera", ["c1801"] = "Female Viera",
    }.ToImmutableDictionary(StringComparer.Ordinal);

    public static bool Ready(AnimationCapture capture) => capture.Clip.Resolution is { State: SkeletonResolutionState.Matched, Selected: not null };

    /// <summary>
    /// Whether an animation stays listed after it stops playing: once its skeleton is matched, or
    /// while several fit equally well, so the source skeleton can still be chosen afterwards.
    /// </summary>
    public static bool Listed(AnimationCapture capture) => Ready(capture) || capture.Clip.Resolution?.State == SkeletonResolutionState.Ambiguous;

    public static ImmutableArray<ListItem> ListItems(IEnumerable<AnimationCapture> history)
    {
        var current = history.Where(c => c.Playing).ToArray();
        var recent = history.Where(c => !c.Playing && Listed(c)).ToArray();
        var result = ImmutableArray.CreateBuilder<ListItem>();
        // The current animation is useful immediately. Skeleton matching stays in
        // the background and never leaks Searching/Incompatible state into the list.
        foreach (var capture in current)
        {
            result.Add(new(capture, false, false));
            if (capture.Startup != null)
                result.Add(new(capture, true, false));
        }
        var separator = result.Count > 0;
        foreach (var capture in recent)
        {
            result.Add(new(capture, false, separator));
            separator = false;
        }
        return result.ToImmutable();
    }

    public static string AnimationName(AnimationCapture capture, bool startup = false)
    {
        var clip = startup ? capture.Startup : capture.Clip;
        if (clip != null && TryIdleName(clip, startup, out var idleName)) return idleName;
        if (clip != null && AnimationCatalog.MovementName(clip.Timeline) is { } movement) return "Movement - " + movement;
        if (!string.Equals(capture.DisplayName, "Idle", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(capture.DisplayName))
            return startup ? capture.DisplayName + " - Startup" : capture.DisplayName;
        if (clip == null) return capture.DisplayName;
        if (AnimationCatalog.IsExplicitIdle(clip.Timeline)) return "Standing Idle - " + (startup ? "Startup" : "Loop");
        return string.IsNullOrWhiteSpace(capture.DisplayName) ? "Animation" : capture.DisplayName;
    }

    private static bool TryIdleName(AnimationClip clip, bool startup, out string name)
    {
        if (AnimationSlots.Describe(clip.GamePath) is not { } slot)
        {
            if (clip.GamePath.EndsWith("/resident/idle.pap", StringComparison.OrdinalIgnoreCase))
            { name = "Standing Idle - Loop 1"; return true; }
            name = ""; return false;
        }
        name = SlotName(slot, clip.Timeline, startup || slot.Startup ? "Startup" : "Loop");
        return true;
    }

    /// <summary>
    /// Label a numbered slot. The family comes from the filename prefix, with the
    /// timeline disambiguating sitting rows that report their transition instead.
    /// </summary>
    internal static string SlotName(AnimationSlot slot, ushort timeline, string state)
    {
        var label = AnimationSlots.Label(slot.Family, timeline);
        return AnimationSlots.Resolve(slot.Family, timeline) == "standing"
            ? $"{label} - {state} {slot.Index}"
            : $"{label} {slot.Index} - {state}";
    }

    public static string AnimationState(AnimationCapture capture, bool startup) => startup ? "Startup" :
        IsExpression(capture) ? "Expression" :
        AnimationCatalog.IsMovement(capture.Clip.Timeline) ? "Movement" :
        AnimationCatalog.IsExplicitIdle(capture.Clip.Timeline) ? "Loop" : "Emote";

    /// <summary>An expression emote (/smile): a face on the face skeleton, timed by a TMB file of its own.</summary>
    public static bool IsExpression(AnimationCapture capture) => capture.ExpressionTimeline != null;

    /// <summary>The Blender action name of a clip: its display name, followed by its name inside the PAP.</summary>
    public static string ExportName(AnimationCapture capture, bool startup = false)
    {
        var name = AnimationName(capture, startup);
        var clip = startup ? capture.Startup : capture.Clip;
        return clip == null || string.IsNullOrWhiteSpace(clip.Name) || name.Contains(clip.Name, StringComparison.OrdinalIgnoreCase)
            ? name
            : $"{name} ({clip.Name})";
    }

    public static string ModelName(AnimationClip clip)
    {
        var model = new[] { clip.SkeletonPath, clip.GamePath, clip.Resolution?.Selected?.Source.Resource.GamePath }
            .OfType<string>().SelectMany(p => p.Replace('\\', '/').Split('/'))
            .FirstOrDefault(p => p.Length == 5 && p[0] == 'c' && p[1..].All(char.IsDigit));
        return ModelLabel(model, "Unknown model");
    }

    internal static string SourceModelName(SkeletonCandidate candidate)
    {
        var model = candidate.Source.CanonicalModel ?? AnimationSkeletonIndex.ModelFromPath(candidate.Source.Resource.GamePath);
        return ModelLabel(model, "Unknown source model");
    }

    /// <summary>A source skeleton's race, the mod or game it comes from and its bone count.</summary>
    internal static string SkeletonLabel(SkeletonCandidate candidate)
    {
        var provider = candidate.Source.Resource.ModName ?? candidate.Source.Resource.ModDirectory ??
                       (candidate.Source.Kind == SkeletonSourceKind.Game ? "Vanilla game" : "Current collection");
        var bones = candidate.Source.LeadingBones > 0 ? $"its first {candidate.Skeleton.Bones.Length} bones" : $"{candidate.Skeleton.Bones.Length} bones";
        return $"{SourceModelName(candidate)} · {provider} · {bones}";
    }

    private static string ModelLabel(string? model, string fallback)
    {
        if (model == null) return fallback;
        return ModelLabels.TryGetValue(model, out var label) ? $"{label} ({model})" : model;
    }

    public static string BoneName(string raw)
    {
        if (BoneLabels.TryGetValue(raw, out var label)) return $"{label} ({raw})";
        if (raw.Length > 2 && raw[^2] == '_' && char.ToLowerInvariant(raw[^1]) is 'l' or 'r' && SidedLabels.TryGetValue(raw[..^2], out var sided))
            return $"{(char.ToLowerInvariant(raw[^1]) == 'l' ? "Left" : "Right")} {sided} ({raw})";
        var words = raw.Split('_', StringSplitOptions.RemoveEmptyEntries).Where(w => w is not "j" and not "n")
            .Select(w => w.Equals("l", StringComparison.OrdinalIgnoreCase) ? "Left" : w.Equals("r", StringComparison.OrdinalIgnoreCase) ? "Right" :
                char.ToUpperInvariant(w[0]) + w[1..]);
        var fallback = string.Join(" ", words);
        return string.IsNullOrWhiteSpace(fallback) ? raw : $"{fallback} ({raw})";
    }

    public static string DefaultModName(AnimationCapture capture)
    {
        var source = capture.Sources.FirstOrDefault(s => s.GamePath == capture.Clip.GamePath);
        var prefix = !string.IsNullOrWhiteSpace(source?.ModName) ? source.ModName! : AnimationName(capture);
        const string suffix = " - Instant Edit";
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var clean = new string(prefix.Trim().Select(c => char.IsControl(c) || invalid.Contains(c) || c is '/' or '\\' ? '-' : c).ToArray()).Trim(' ', '.');
        if (string.IsNullOrWhiteSpace(clean) || clean is "." or "..") clean = "Animation";
        return clean[..Math.Min(clean.Length, 128 - suffix.Length)] + suffix;
    }
}
