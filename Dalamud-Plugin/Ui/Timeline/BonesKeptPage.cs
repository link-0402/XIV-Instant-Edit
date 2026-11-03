using System.Collections.Immutable;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// The Bones window's Kept page: every bone the clip animates, ticked while it stays in. Unticking leaves the bone out of the
/// save, for example to give hair, cloth or tail bones back to physics, and shows at once while Live is on. Each change is
/// one undo step.
/// </summary>
internal sealed class BonesKeptPage(TimelineSession session, TimelinePreview timelinePreview)
{
    private string animatedBoneFilter = string.Empty;
    // The kept line's numbers and the skeleton choice and bones left out they were worked out for.
    private (SkeletonResolution? Resolution, ImmutableArray<string> Excluded, ImmutableArray<string> Bones, int Kept, int Highest)? keptCounts;

    /// <summary>Clears the bones list's filter, for another clip.</summary>
    public void ClearFilter() => animatedBoneFilter = string.Empty;

    /// <summary>The kept line: "All 40 kept" or "37 of 40 kept", then the highest index when one is known.</summary>
    public static string KeptText(int kept, int total, int highest) =>
        (kept == total ? $"All {total} kept" : $"{kept} of {total} kept") + (highest < 0 ? "" : $" · highest index {highest}");

    /// <summary>
    /// The bones <paramref name="clip"/> animates and the kept line's numbers with <paramref name="excluded"/> left
    /// out, worked out again only when the skeleton it was made for or the bones left out change.
    /// </summary>
    private (ImmutableArray<string> Bones, int Kept, int Highest) KeptCounts(AnimationClip clip, ImmutableArray<string> excluded)
    {
        if (keptCounts is { } known && ReferenceEquals(known.Resolution, clip.Resolution) && known.Excluded == excluded)
            return (known.Bones, known.Kept, known.Highest);
        var bones = AnimationBones.Animated(clip);
        var left = excluded.ToHashSet(StringComparer.Ordinal);
        var highest = AnimationBones.HighestIndex(clip, left);
        keptCounts = (clip.Resolution, excluded, bones, bones.Length - left.Count, highest);
        return (bones, bones.Length - left.Count, highest);
    }

    /// <summary>How many of the bones stay in and the highest index among them.</summary>
    private static void DrawKeptCount((ImmutableArray<string> Bones, int Kept, int Highest) counts)
    {
        ImGui.TextColored(Theme.Muted, KeptText(counts.Kept, counts.Bones.Length, counts.Highest));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Sync plugins refuse animations whose bone indices go past the other player's skeleton.");
    }

    /// <summary>
    /// The page: every bone the clip animates with its tick, the filter and, for a body clip, Keep only; an expression whose
    /// face can't be read says why.
    /// </summary>
    public void Draw(TimelineEditor current, AnimationTimelineSource loaded)
    {
        if (loaded.MotionTarget.Standalone)
        {
            Widgets.HintWrapped(loaded.FaceProblem ?? "This expression's face can't be edited.");
            return;
        }
        var clip = session.Fresh(loaded).Clip;
        var doc = current.Document;
        var excluded = doc.ExcludedBones;
        var counts = KeptCounts(clip, excluded);
        if (counts.Bones.IsEmpty)
        {
            Widgets.HintWrapped("The bones it moves are listed once the skeleton it was made for is known.");
            return;
        }
        var readOnly = session.ReadOnly;
        // A face, an expression's or a face session's, keeps only its own bones: no Keep only.
        var body = doc.HasClip;
        Widgets.HintWrapped(body ? "Untick a bone to stop the animation moving it, such as hair or a tail for physics." : "Untick a bone to leave it out of the face.");
        if (!timelinePreview.LiveWanted)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Hint, "Turn on Live to see it in GPose.");
        }
        ImGui.Spacing();
        if (body)
            DrawKeepOnly(current, clip, readOnly);
        DrawBoneTicks(current, counts, excluded, readOnly);
    }

    /// <summary>The Keep only row: one button per standard skeleton, unticking the bones outside it. Body clips only.</summary>
    private static void DrawKeepOnly(TimelineEditor current, AnimationClip clip, bool readOnly)
    {
        var doc = current.Document;
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, "Keep only");
        foreach (var standard in Enum.GetValues<SkeletonStandard>())
        {
            ImGui.SameLine(0, Theme.Scaled(4));
            var outside = AnimationBones.Outside(clip, standard);
            using (ImRaii.Disabled(readOnly || outside is null))
            {
                if (ImGui.SmallButton(AnimationBones.StandardName(standard)) && outside is { } untick)
                    current.Change(MotionEdits.ExcludeOnly(doc, untick));
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(outside is not { } others
                    ? "The game's own skeleton for this model was not found."
                    : $"Keep {PresetGroups(standard)}; untick the other {others.Length}.");
        }
    }

    /// <summary>The filter, Tick all and Untick all, the count kept and a tick per bone.</summary>
    private void DrawBoneTicks(TimelineEditor current, (ImmutableArray<string> Bones, int Kept, int Highest) counts, ImmutableArray<string> excluded, bool readOnly)
    {
        var doc = current.Document;
        var bones = counts.Bones;
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##animated-bone-filter", "Filter bones", ref animatedBoneFilter, 128);
        var filter = animatedBoneFilter.Trim();
        string[] shown = filter.Length == 0 ? [.. bones] : [.. bones.Where(name =>
            AnimationPresentation.BoneName(name).Contains(filter, StringComparison.OrdinalIgnoreCase))];
        using (ImRaii.Disabled(readOnly))
        {
            if (ImGui.SmallButton(filter.Length == 0 ? "Tick all" : "Tick matching"))
                current.Change(MotionEdits.SetExcluded(doc, shown, false));
            ImGui.SameLine();
            if (ImGui.SmallButton(filter.Length == 0 ? "Untick all" : "Untick matching"))
                current.Change(MotionEdits.SetExcluded(doc, shown, true));
        }
        var left = excluded.ToHashSet(StringComparer.Ordinal);
        DrawKeptCount(counts);

        var row = ImGui.GetFrameHeightWithSpacing();
        var height = Math.Max(row * 3, ImGui.GetContentRegionAvail().Y - Theme.Scaled(4));
        using (var list = ImRaii.Child("##animated-bones", new Vector2(0, height), true))
        {
            if (list.Success)
            {
                if (shown.Length == 0)
                    ImGui.TextDisabled("No bone matches.");
                using (ImRaii.Disabled(readOnly))
                    foreach (var name in shown)
                    {
                        using var id = ImRaii.PushId(name);
                        var kept = !left.Contains(name);
                        if (ImGui.Checkbox(AnimationPresentation.BoneName(name), ref kept))
                            current.Change(MotionEdits.SetExcluded(doc, [name], !kept));
                    }
            }
        }
    }

    /// <summary>The bone groups a Keep only preset keeps, for its tooltip.</summary>
    private static string PresetGroups(SkeletonStandard standard) => standard switch
    {
        SkeletonStandard.Ivcs => "the game's bones and IVCS's iv_ bones",
        SkeletonStandard.IvcsYas => "the game's, IVCS's iv_ and YAS's ya_ bones",
        _ => "the game's own bones",
    };
}
