using System.Collections.Immutable;
using Dalamud.Bindings.ImGui;
using InstantEdit.Models;
using InstantEdit.Services.Animations;

namespace InstantEdit.Ui;

public sealed partial class MainWindow
{
    // Destination slot index -> source slot index. Absent means "leave alone".
    private readonly Dictionary<int, int> animationSlotMap = [];
    private string? animationSlotGroup;

    private void DrawSlotSwapAction(AnimationCapture capture)
    {
        if (animationStartupSelected) return;
        if (AnimationSlots.Describe(capture.Clip.GamePath) is not { } current) return;

        ImGui.Separator();
        ImGui.TextUnformatted("Swap animation slots");
        ImGui.TextWrapped("Put a different animation into each slot of this group. Every mapping becomes an option " +
            "in one Penumbra group, so you can switch between them without running this again.");

        // The discovered set belongs to whichever group was probed last; a different
        // selection must not inherit another group's slots.
        var discovered = animations!.SlotGroup == current.Group ? animations.Slots : [];
        if (animationSlotGroup != current.Group) { animationSlotGroup = current.Group; animationSlotMap.Clear(); }

        ImGui.BeginDisabled(animations.Busy);
        if (ImGui.Button(discovered.IsEmpty ? "Find animations in this group" : "Search again"))
        { animationSlotMap.Clear(); animations.DiscoverSlots(capture, current); }
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Reads each candidate file in this group. Penumbra cannot list a character's animation packs, so they have to be probed.");

        if (animations.SlotError != null && animations.SlotGroup == current.Group)
        { ImGui.TextWrapped(animations.SlotError); return; }
        if (discovered.IsEmpty)
        { ImGui.TextDisabled("Search this group to choose what goes into each slot."); return; }

        var loops = discovered.Where(slot => !slot.Startup).OrderBy(slot => slot.Index).ToImmutableArray();
        ImGui.Spacing();
        foreach (var destination in loops)
        {
            ImGui.PushID(destination.Index);
            var source = animationSlotMap.GetValueOrDefault(destination.Index, destination.Index);
            ImGui.SetNextItemWidth(260);
            if (ImGui.BeginCombo(AnimationPresentation.SlotName(destination, capture.Clip.Timeline, "Loop"),
                    source == destination.Index
                        ? "Unchanged"
                        : AnimationPresentation.SlotName(destination.At(source), capture.Clip.Timeline, "Loop")))
            {
                if (ImGui.Selectable("Unchanged", source == destination.Index))
                    animationSlotMap.Remove(destination.Index);
                foreach (var candidate in loops)
                    if (ImGui.Selectable(AnimationPresentation.SlotName(candidate, capture.Clip.Timeline, "Loop"),
                            candidate.Index == source && candidate.Index != destination.Index))
                        animationSlotMap[destination.Index] = candidate.Index;
                ImGui.EndCombo();
            }
            ImGui.PopID();
        }

        ImmutableArray<AnimationSlotSwap> swaps = [];
        string? blocked = capture.UnavailableReason ?? capture.PackagingError;
        try
        {
            swaps = AnimationSlots.Plan(discovered,
                loops.Select(slot => (slot.Index, animationSlotMap.GetValueOrDefault(slot.Index, slot.Index))),
                slot => AnimationPresentation.SlotName(slot, capture.Clip.Timeline, "Loop"));
        }
        catch (InvalidDataException e) { blocked ??= e.Message; }

        ImGui.Spacing();
        ImGui.SetNextItemWidth(Math.Min(400, ImGui.GetContentRegionAvail().X));
        ImGui.InputText("Slot variant mod name", ref animationModName, 128);
        ImGui.TextDisabled("Slot variants are always packaged as a new mod.");
        if (blocked != null) ImGui.TextWrapped(blocked);
        else ImGui.TextDisabled($"{swaps.Count(swap => !swap.Destination.Startup)} slot(s) will be replaced.");

        ImGui.BeginDisabled(animations.Busy || blocked != null);
        if (ImGui.Button("Create slot variants"))
            animations.SwapSlots(new AnimationBakeRequest(Guid.NewGuid(), capture, AnimationDestination.NewMod,
                animationModName.Trim(), false, ImmutableHashSet<PoseBoneId>.Empty, PoseComponents.None,
                AnimationOperation.SwapSlots, SlotSwaps: swaps));
        ImGui.EndDisabled();
    }
}
