using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// The layer panel's rows for a look-at layer: what it looks at, its limit, and the head chain's bones it turns. A slider's
/// changes until it is left are one undo step; a pick or a tick is one.
/// </summary>
internal static class LookFields
{
    // The open follow list's search; only one combo is open at a time.
    private static string filter = string.Empty;

    /// <summary> Drawn in the facts table: Looks at (Spot, a bone to follow, or a partner's bone) and Limit. </summary>
    public static void Draw(TimelineEditor current, MotionLayer layer, SkeletonDescription? skeleton, TimelineSession session)
    {
        if (layer.Look is not { } look)
            return;
        TimelineUi.Fact("Looks at");
        ImGui.SetNextItemWidth(Math.Max(Theme.Scaled(140), ImGui.GetContentRegionAvail().X));
        var shown = look.Follow is not { } follow ? "Spot"
            : look.Partner is { } partner ? $"{session.PartnerName(partner.Workspace) ?? "A partner"}: {AnimationPresentation.BoneName(follow)}"
            : AnimationPresentation.BoneName(follow);
        using (var combo = ImRaii.Combo("##look-follow", shown, ImGuiComboFlags.HeightLarge))
        {
            if (combo.Success)
            {
                ImGui.SetNextItemWidth(-1);
                if (ImGui.IsWindowAppearing())
                {
                    filter = string.Empty;
                    ImGui.SetKeyboardFocusHere();
                }
                ImGui.InputTextWithHint("##look-search", "Search bones", ref filter, 64);
                if (ImGui.Selectable("Spot##look-spot", look.Follow == null) && look.Follow != null)
                    current.Commit(MotionEdits.Replace(current.Document, layer with { Look = look with { Follow = null, Partner = null } }));
                // Listed only while open: the bones the head may follow, not its own chain nor what hangs from the head.
                if (skeleton != null)
                {
                    var head = MotionEvaluator.Index(skeleton).GetValueOrDefault(MotionLooks.KeyBone, -1);
                    for (var b = 0; b < skeleton.Bones.Length; b++)
                    {
                        var bone = skeleton.Bones[b].Name;
                        if (!MotionLooks.CanFollow(skeleton, b, head))
                            continue;
                        var name = AnimationPresentation.BoneName(bone);
                        if (filter.Length > 0 && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                            continue;
                        var chosen = look.Partner == null && look.Follow == bone;
                        // Following a bone drops the keyed spots in the same step (MotionEdits.Replace).
                        if (ImGui.Selectable($"{name}##look-{b}", chosen) && !chosen)
                            current.Commit(MotionEdits.Replace(current.Document, layer with { Look = look with { Follow = bone, Partner = null } }));
                    }
                }
                // Then each open partner's bones, from its candidate: listing them reads none of its motion.
                if (session.LinkPicker is { } picker)
                    foreach (var candidate in picker.Candidates())
                    {
                        if (candidate.Skeleton is not { } theirs)
                            continue;
                        for (var b = 0; b < theirs.Bones.Length; b++)
                        {
                            var bone = theirs.Bones[b].Name;
                            var name = $"{candidate.Name}: {AnimationPresentation.BoneName(bone)}";
                            if (filter.Length > 0 && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                                continue;
                            var chosen = look.Partner is { } known && known.Workspace == candidate.Key.Preset && look.Follow == bone;
                            if (ImGui.Selectable($"{name}##look-{candidate.Key.Id}-{b}", chosen) && !chosen)
                                current.Commit(MotionEdits.Replace(current.Document, layer with
                                {
                                    Look = look with { Follow = bone, Partner = new MotionLookPartner(candidate.Key.Preset, candidate.GamePath, candidate.Binding) },
                                }));
                        }
                    }
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(look.Follow == null ? "Move the target with the gizmo. Picking a bone drops the keyed spots." : "Pick Spot to key spots again.");

        TimelineUi.Fact("Limit");
        var limit = look.Limit;
        ImGui.SetNextItemWidth(Math.Max(Theme.Scaled(90), ImGui.GetContentRegionAvail().X));
        var changed = ImGui.SliderFloat("##look-limit", ref limit, 0, MotionLooks.MaxLimit, "%.0f°");
        LayerPanel.TrackLayer(current, changed, changed ? layer with { Look = look with { Limit = Math.Clamp(limit, 0, MotionLooks.MaxLimit) } } : null);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("The most the head turns. Ctrl+click to type.");
    }

    /// <summary> A look at a partner's line, in the warning colour, while it can't follow the partner: why, as a link's line says it. </summary>
    public static void DrawState(MotionLayer layer, TimelineSession session)
    {
        if (!MotionLooks.MayChange(layer) || !session.LinkStates.TryGetValue(layer.Key, out var state) || state == LinkState.On ||
            TimelineWindowViews.LinkStateText(state, MotionLooks.AsLink(layer), session.PartnerName, session.ActorName()) is not { } line)
            return;
        using var wrap = ImRaii.TextWrapPos(0f);
        ImGui.TextColored(Theme.Warning, line);
    }

    /// <summary> Under the facts: a tick for each bone of the head chain the turn is shared over, and what to do with none. </summary>
    public static void DrawBones(TimelineEditor current, MotionLayer layer, bool readOnly)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, "Turns");
        using (ImRaii.Disabled(readOnly))
            foreach (var bone in MotionLooks.Chain)
            {
                ImGui.SameLine(0, Theme.Scaled(10));
                var on = layer.Bones.Contains(bone);
                if (ImGui.Checkbox(Label(bone) + "##look-bone-" + bone, ref on))
                    current.Commit(MotionEdits.SetBones(current.Document, layer.Key, MotionLooks.Chain.Where(b => b == bone ? on : layer.Bones.Contains(b))));
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip($"Takes {MotionLooks.Share(bone) * 100:0}% of the turn, shared over the ticked bones.");
            }
        if (layer.Bones.IsEmpty)
            Widgets.HintWrapped("Tick a bone to turn.");
    }

    private static string Label(string bone) => bone switch
    {
        MotionLooks.KeyBone => "Head",
        "j_kubi" => "Neck",
        _ => "Upper back",
    };
}
