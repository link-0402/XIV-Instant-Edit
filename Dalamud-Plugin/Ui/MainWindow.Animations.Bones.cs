using System.Numerics;
using Dalamud.Bindings.ImGui;
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

    private void DrawAnimatedBones(AnimationCapture capture)
    {
        // The rebake buttons bake the loop (and an included startup), so the list is
        // the loop's; the startup row has nothing of its own to untick.
        if (animationStartupSelected) return;
        var bones = AnimationBones.Animated(capture.Clip);
        // Another source skeleton can name the same tracks differently; only bones the
        // animation still drives can be left out.
        if (!bones.IsEmpty) animationExcludedBones.IntersectWith(bones);
        if (!ImGui.CollapsingHeader("Animated Bones")) return;
        if (bones.IsEmpty)
        {
            ImGui.TextDisabled("The bones this animation moves are listed once its source skeleton is identified.");
            return;
        }
        ImGui.TextWrapped("Untick a bone to stop this animation from moving it, for example to give hair, cloth " +
            "or tail bones back to physics. Rebaking removes unticked bones from the animation.");

        ImGui.SetNextItemWidth(Math.Min(260, ImGui.GetContentRegionAvail().X));
        ImGui.InputTextWithHint("##animated-bone-filter", "Filter bones", ref animationBoneFilter, 128);
        var filter = animationBoneFilter.Trim();
        string[] shown = filter.Length == 0 ? [.. bones] : [.. bones.Where(name =>
            AnimationPresentation.BoneName(name).Contains(filter, StringComparison.OrdinalIgnoreCase))];
        ImGui.SameLine();
        if (ImGui.SmallButton(filter.Length == 0 ? "Tick all" : "Tick matching")) animationExcludedBones.ExceptWith(shown);
        ImGui.SameLine();
        if (ImGui.SmallButton(filter.Length == 0 ? "Untick all" : "Untick matching")) animationExcludedBones.UnionWith(shown);

        var row = ImGui.GetFrameHeightWithSpacing();
        if (ImGui.BeginChild("##animated-bones", new Vector2(0, Math.Clamp(shown.Length * row + 8, row + 8, 240)), true))
        {
            if (shown.Length == 0) ImGui.TextDisabled("No animated bone matches the filter.");
            foreach (var name in shown)
            {
                ImGui.PushID(name);
                var kept = !animationExcludedBones.Contains(name);
                if (ImGui.Checkbox(AnimationPresentation.BoneName(name), ref kept))
                { if (kept) animationExcludedBones.Remove(name); else animationExcludedBones.Add(name); }
                ImGui.PopID();
            }
        }
        ImGui.EndChild();
        ImGui.TextDisabled(animationExcludedBones.Count == 0
            ? $"All {bones.Length} bones stay animated."
            : $"{bones.Length - animationExcludedBones.Count} of {bones.Length} bones stay animated; " +
              $"{animationExcludedBones.Count} will be left out.");
    }
}
