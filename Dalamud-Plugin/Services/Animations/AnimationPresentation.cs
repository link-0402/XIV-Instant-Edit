using System.Collections.Immutable;
using InstantEdit.Models;

namespace InstantEdit.Services.Animations;

/// <summary>Turns runtime animation and skeleton identifiers into stable UI language.</summary>
internal static class AnimationPresentation
{
    internal sealed record ListItem(AnimationCapture Capture, bool Startup, bool SeparatorBefore);

    private static readonly ImmutableDictionary<string, string> BoneLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["n_root"] = "Root", ["j_kosi"] = "Hips", ["j_sebo_a"] = "Lower Spine", ["j_sebo_b"] = "Upper Spine",
        ["j_mune"] = "Chest", ["j_kubi"] = "Neck", ["j_kao"] = "Head", ["j_ude_l"] = "Left Upper Arm",
        ["j_ude_r"] = "Right Upper Arm", ["j_hiji_l"] = "Left Elbow", ["j_hiji_r"] = "Right Elbow",
        ["j_te_l"] = "Left Hand", ["j_te_r"] = "Right Hand", ["j_asi_l"] = "Left Upper Leg",
        ["j_asi_r"] = "Right Upper Leg", ["j_hiza_l"] = "Left Knee", ["j_hiza_r"] = "Right Knee",
        ["j_asi_b_l"] = "Left Foot", ["j_asi_b_r"] = "Right Foot", ["j_toe_l"] = "Left Toes",
        ["j_toe_r"] = "Right Toes",
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

    public static ImmutableArray<ListItem> ListItems(IEnumerable<AnimationCapture> history)
    {
        var current = history.Where(c => c.Playing).ToArray();
        var recent = history.Where(c => !c.Playing && Ready(c)).ToArray();
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
        if (!string.Equals(capture.DisplayName, "Idle", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(capture.DisplayName, "Walk", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(capture.DisplayName))
            return startup ? capture.DisplayName + " - Startup" : capture.DisplayName;
        if (clip == null) return capture.DisplayName;
        if (AnimationCatalog.IsExplicitWalk(clip.Timeline)) return "Movement - Running";
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
        AnimationCatalog.IsExplicitIdle(capture.Clip.Timeline) || AnimationCatalog.IsExplicitWalk(capture.Clip.Timeline) ? "Loop" : "Emote";

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

    private static string ModelLabel(string? model, string fallback)
    {
        if (model == null) return fallback;
        return ModelLabels.TryGetValue(model, out var label) ? $"{label} ({model})" : model;
    }

    public static string BoneName(string raw)
    {
        if (BoneLabels.TryGetValue(raw, out var label)) return $"{label} ({raw})";
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
