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

    private void ResetAnimatedBones()
    {
        animationExcludedBones.Clear();
        animationBoneFilter = "";
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
            ImGui.TextDisabled("The bones this animation moves are listed once its source skeleton is identified.");
        else
            DrawAnimatedBoneList(bones);
        DrawRebake(capture, AnimationOperation.ExcludeBones, "Rebake without unticked bones",
            "Write the animation without tracks for the unticked bones, handing them back to physics.");
    }

    private void DrawAnimatedBoneList(ImmutableArray<string> bones)
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
        ImGui.TextDisabled(animationExcludedBones.Count == 0
            ? $"All {bones.Length} bones stay animated."
            : $"{bones.Length - animationExcludedBones.Count} of {bones.Length} bones stay animated; " +
              $"{animationExcludedBones.Count} will be left out.");
    }
}
