using System.Collections.Immutable;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Models;
using InstantEdit.Services.Animations;

namespace InstantEdit.Ui;

public sealed partial class MainWindow
{
    // Animated bones the next rebake leaves out, by name. Every bone the animation
    // drives starts ticked, and choosing another animation starts over.
    private readonly HashSet<string> animationExcludedBones = new(StringComparer.Ordinal);
    private string animationBoneFilter = "";
    // The standard skeleton the Skeleton repair tab retargets onto. Null picks the smallest
    // one that has every kept bone.
    private SkeletonStandard? animationRepairTarget;

    private void ResetAnimatedBones()
    {
        animationExcludedBones.Clear();
        animationBoneFilter = "";
        animationRepairTarget = null;
    }

    /// <summary>
    /// The Animated bones tab. Rebakes change the animation (and an included startup), so the list
    /// is the animation's even while its startup row is selected.
    /// </summary>
    private void DrawAnimatedBones(AnimationCapture capture)
    {
        Widgets.HintWrapped("Untick a bone to stop this animation from moving it, for example to give hair, cloth " +
                            "or tail bones back to physics. Rebaking removes unticked bones from the animation.");
        ImGui.Spacing();
        var bones = AnimationBones.Animated(capture.Clip);
        if (bones.IsEmpty)
        {
            ImGui.TextDisabled("The bones this animation moves are listed once its source skeleton is identified.");
        }
        else
        {
            DrawBonePresets(capture.Clip);
            DrawAnimatedBoneList(capture.Clip, bones);
        }
        DrawRebake(capture, AnimationOperation.ExcludeBones, "Rebake without unticked bones",
            "Write the animation without tracks for the unticked bones, handing them back to physics.");
    }

    /// <summary> Presets that tick one standard's bone groups and untick every other animated bone. </summary>
    private void DrawBonePresets(AnimationClip clip)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, "Keep only");
        foreach (var standard in Enum.GetValues<SkeletonStandard>())
        {
            ImGui.SameLine();
            var outside = AnimationBones.Outside(clip, standard);
            using (ImRaii.Disabled(outside is null))
            {
                if (ImGui.SmallButton(AnimationBones.StandardName(standard)) && outside is { } untick)
                {
                    animationExcludedBones.Clear();
                    animationExcludedBones.UnionWith(untick);
                }
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(outside is not { } others
                    ? "The game's own skeleton for this animation's model was not found, so its bones are unknown."
                    : $"Tick {PresetGroups(standard)} and untick the other {others.Length} animated bones.");
        }
    }

    private static string PresetGroups(SkeletonStandard standard) => standard switch
    {
        SkeletonStandard.Ivcs => "the game's own bones and IVCS's iv_ bones",
        SkeletonStandard.IvcsYas => "the game's own bones, IVCS's iv_ bones and YAS's ya_ bones",
        _ => "the game's own bones",
    };

    private void DrawAnimatedBoneList(AnimationClip clip, ImmutableArray<string> bones)
    {
        ImGui.SetNextItemWidth(Math.Min(Theme.Scaled(260), ImGui.GetContentRegionAvail().X));
        ImGui.InputTextWithHint("##animated-bone-filter", "Filter bones", ref animationBoneFilter, 128);
        var filter = animationBoneFilter.Trim();
        string[] shown = filter.Length == 0 ? [.. bones] : [.. bones.Where(name =>
            AnimationPresentation.BoneName(name).Contains(filter, StringComparison.OrdinalIgnoreCase))];
        ImGui.SameLine();
        if (ImGui.SmallButton(filter.Length == 0 ? "Tick all" : "Tick matching")) animationExcludedBones.ExceptWith(shown);
        ImGui.SameLine();
        if (ImGui.SmallButton(filter.Length == 0 ? "Untick all" : "Untick matching")) animationExcludedBones.UnionWith(shown);

        var row = ImGui.GetFrameHeightWithSpacing();
        var listHeight = Math.Clamp(shown.Length * row + Theme.Scaled(8), row + Theme.Scaled(8), Theme.Scaled(240));
        using (var list = ImRaii.Child("##animated-bones", new Vector2(0, listHeight), true))
        {
            if (list.Success)
            {
                if (shown.Length == 0) ImGui.TextDisabled("No animated bone matches the filter.");
                foreach (var name in shown)
                {
                    using var id = ImRaii.PushId(name);
                    var kept = !animationExcludedBones.Contains(name);
                    if (ImGui.Checkbox(AnimationPresentation.BoneName(name), ref kept))
                    { if (kept) animationExcludedBones.Remove(name); else animationExcludedBones.Add(name); }
                }
            }
        }
        // Sync plugins refuse animations that bind bone indices beyond the receiving skeleton.
        var highest = AnimationBones.HighestIndex(clip, animationExcludedBones);
        ImGui.TextDisabled((animationExcludedBones.Count == 0
            ? $"All {bones.Length} bones stay animated"
            : $"{bones.Length - animationExcludedBones.Count} of {bones.Length} bones stay animated; " +
              $"{animationExcludedBones.Count} will be left out") + (highest < 0 ? "." : $" · highest bone index {highest}."));
        // A rebake here keeps the source's own bone order; only repair moves bones onto standard indices.
        if (clip.Resolution is { Selected: { } source, Standards: { IsEmpty: false } standards } &&
            standards.All(s => s.Skeleton.Fingerprint != source.Skeleton.Fingerprint))
            Widgets.HintWrapped("This animation was made for a skeleton that is not one of the standard layouts, and rebaking " +
                                "here keeps its bone indices. Skeleton repair moves the kept bones onto standard indices.");
    }
}
