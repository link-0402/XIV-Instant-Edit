using System.Collections.Immutable;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// The layer panel's section for a link layer: whether it follows its partner, Link back when the partner doesn't follow back,
/// the bones it joins and the partner, what it holds, how far up it bends and, on the hips, what stays put. A pick or a tick is
/// one undo step; the Bend slider's changes until it is left are one.
/// </summary>
internal static class LinkFields
{
    // The open bone list's search; only one combo is open at a time.
    private static string filter = string.Empty;
    // The link back last asked for: its layer and the partner's document then, so the button waits while that document stays.
    private static (string Layer, TimelineDocument Partner)? asked;

    /// <summary>The link's state line: hint coloured while it follows the partner, warning coloured when it doesn't; nothing while it is off itself.</summary>
    public static void DrawState(MotionLayer layer, TimelineSession session)
    {
        if (layer.Link is not { } link)
            return;
        var state = session.LinkStates.TryGetValue(layer.Key, out var known) ? known : (LinkState?)null;
        if (TimelineWindowViews.LinkStateText(state, link, session.PartnerName, session.ActorName()) is not { } line)
            return;
        using var wrap = ImRaii.TextWrapPos(0f);
        ImGui.TextColored(state == LinkState.On ? Theme.Hint : Theme.Warning, line);
    }

    /// <summary>
    /// Under the layer's facts: the state line, Link back, then the settings as fact rows, why the bone can't bend, and the hips' holds.
    /// <paramref name="index"/> is <paramref name="skeleton"/>'s bone index.
    /// </summary>
    public static void Draw(TimelineEditor current, MotionLayer layer, SkeletonDescription skeleton, Dictionary<string, int> index, TimelineSession session,
        bool readOnly)
    {
        if (layer.Link is not { } link)
            return;
        DrawState(layer, session);
        var picker = session.LinkPicker;
        // The partner's view is ready on the clip the link names only while the link is on; the link reads it, so it isn't asked for.
        var view = picker != null && session.LinkStates.GetValueOrDefault(layer.Key, LinkState.NotOpen) == LinkState.On ? picker.Linked(link.Partner) : null;
        // The partner's document changed since the click (the link back came, or an undo took it away): the button may show again.
        if (view != null && asked is { } waiting && waiting.Layer == layer.Key && !ReferenceEquals(waiting.Partner, view.Document))
            asked = null;
        if (picker != null && view != null && MotionLinks.LinkBack(view.Document.Motion, link, picker.Key.Preset) == null && asked?.Layer != layer.Key)
        {
            using (ImRaii.Disabled(view.ReadOnly))
            {
                if (ImGui.Button("Link back##link-back"))
                {
                    picker.AddLinkBack(view, link);
                    asked = (layer.Key, view.Document);
                }
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(view.ReadOnly ? TimelineWindowViews.LinkReadOnly(view.Name) : TimelineWindowViews.LinkBackHover(view.Name));
        }

        PinChain? chain = null;
        string? problem = null;
        if (link.Bone != PinChains.Hips)
            try
            {
                chain = PinChains.Chain(skeleton, index, MotionLinks.AsPin(link));
            }
            catch (InvalidDataException e)
            {
                problem = e.Message;
            }
        using (ImRaii.Disabled(readOnly))
        using (var table = ImRaii.Table("##link-settings", 2, ImGuiTableFlags.SizingFixedFit))
        {
            if (table.Success)
            {
                ImGui.TableSetupColumn("##fact", ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableSetupColumn("##value", ImGuiTableColumnFlags.WidthStretch);
                DrawSettings(current, layer, link, skeleton, session, picker, chain);
            }
        }
        if (problem != null)
            Widgets.HintWrapped(problem);
        else if (chain is { Shape: PinChainShape.None })
            Widgets.HintWrapped(LinkPicker.Refused(link.Bone));
        if (link.Bone == PinChains.Hips)
            DrawHolds(current, layer, link, skeleton, index, readOnly);
    }

    /// <summary>The fact rows: Bone, Partner, Partner bone, Holds, and for a limb or chain Bends up to, and Bend for a limb.</summary>
    private static void DrawSettings(TimelineEditor current, MotionLayer layer, MotionLink link, SkeletonDescription skeleton, TimelineSession session,
        LinkPicker? picker, PinChain? chain)
    {
        TimelineUi.Fact("Bone");
        if (BoneCombo("##link-bone", link.Bone, () => skeleton, LinkPicker.Linkable(skeleton), null, out var bone))
            // A hips link starts out holding both feet, as a new one does.
            Commit(current, layer, link with { Bone = bone, Holds = bone == PinChains.Hips ? [PinChains.FootL, PinChains.FootR] : [] }, skeleton);

        TimelineUi.Fact("Partner");
        ImGui.SetNextItemWidth(Math.Max(Theme.Scaled(140), ImGui.GetContentRegionAvail().X));
        using (var combo = ImRaii.Combo("##link-partner", session.PartnerName(link.Partner) ?? "No partner"))
        {
            // Listed only while open: asking for the partners reads their motion.
            if (combo.Success && picker != null)
            {
                var views = picker.Partners();
                if (views.Count == 0)
                {
                    using (ImRaii.Disabled())
                        ImGui.Selectable(picker.Available ? LinkPicker.Waiting : TimelineWindowViews.LinkNoPartner);
                }
                foreach (var view in views)
                {
                    var chosen = view.Key.Preset == link.Partner;
                    if (ImGui.Selectable($"{view.Name}##{view.Key.Id}", chosen) && !chosen)
                        Commit(current, layer, Retarget(link, view), skeleton);
                }
            }
            else if (!combo.Success && picker != null && ComboWheel.Steps() is var steps and not 0 && picker.Partners() is { Count: > 0 } views)
            {
                var next = ComboWheel.Pick(views, view => view.Key.Preset == link.Partner, steps);
                if (next.Key.Preset != link.Partner)
                    Commit(current, layer, Retarget(link, next), skeleton);
            }
        }

        TimelineUi.Fact("Partner bone");
        if (BoneCombo("##link-partner-bone", link.PartnerBone, () => picker?.Partner(link.Partner)?.Played.Skeleton, null,
                picker?.Available == true ? LinkPicker.Waiting : TimelineWindowViews.LinkNoPartner, out var partnerBone))
            Commit(current, layer, link with { PartnerBone = partnerBone }, skeleton);

        Choice<bool>(current, layer, skeleton, "Holds", "##link-holds", "Follow the partner's move, or its turn as well.",
            link.HoldTurn ? "Position and turn" : "Position", [(false, "Position"), (true, "Position and turn")], link.HoldTurn,
            holds => link with { HoldTurn = holds });

        if (chain is not { Shape: not PinChainShape.None })
            return;
        string fallback;
        ImmutableArray<string> choices;
        try
        {
            fallback = PinChains.DefaultTop(skeleton, link.Bone) is { } usual ? $"Default ({AnimationPresentation.BoneName(usual)})" : "Default (nothing bends)";
            choices = PinChains.TopChoices(skeleton, link.Bone);
        }
        catch (InvalidDataException)
        {
            fallback = "Default";
            choices = [];
        }
        Choice<string?>(current, layer, skeleton, "Bends up to", "##link-top", "The highest bone that may turn to follow.",
            link.Top is { } highest ? AnimationPresentation.BoneName(highest) : fallback,
            [(null, fallback), .. choices.Select(name => ((string?)name, AnimationPresentation.BoneName(name)))], link.Top,
            top => link with { Top = top });

        // A limb's knee or elbow bends where the solver picks; Bend turns it about the line to the followed spot. One undo step per drag.
        if (chain.Shape == PinChainShape.Limb)
        {
            TimelineUi.Fact("Bend");
            var bend = link.Bend;
            ImGui.SetNextItemWidth(Math.Max(Theme.Scaled(140), ImGui.GetContentRegionAvail().X));
            var changed = ImGui.SliderFloat("##link-bend", ref bend, -180, 180, "%.0f°", ImGuiSliderFlags.AlwaysClamp);
            LayerPanel.TrackLayer(current, changed, changed ? layer with { Link = link with { Bend = Math.Clamp(bend, -180, 180) } } : null);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Turns the knee or elbow around the line to the held spot. Ctrl+click to type.");
        }
    }

    /// <summary>A hips link's Hold boxes: each foot and hand the skeleton has that can stay put while the hips move.</summary>
    private static void DrawHolds(TimelineEditor current, MotionLayer layer, MotionLink link, SkeletonDescription skeleton, Dictionary<string, int> index,
        bool readOnly)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, "Hold");
        using (ImRaii.Disabled(readOnly))
            foreach (var (bone, label) in DragHolds.Ends)
            {
                if (!index.ContainsKey(bone) || HipSolver.Candidates(skeleton, [bone]).Count == 0)
                    continue;
                ImGui.SameLine(0, Theme.Scaled(10));
                var on = link.Holds.Contains(bone);
                if (ImGui.Checkbox(label + "##link-hold-" + bone, ref on))
                    Commit(current, layer, link with { Holds = [.. DragHolds.Ends.Select(end => end.Bone).Where(b => b == bone ? on : link.Holds.Contains(b))] }, skeleton);
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip("Stays where it is while the hips move.");
            }
    }

    /// <summary>The link on <paramref name="view"/>'s workspace and its open clip, the bones kept.</summary>
    private static MotionLink Retarget(MotionLink link, LinkView view) =>
        link with { Partner = view.Key.Preset, PartnerClip = view.GamePath, PartnerBinding = view.Binding };

    private static void Commit(TimelineEditor current, MotionLayer layer, MotionLink link, SkeletonDescription skeleton) =>
        current.Commit(MotionEdits.SetLink(current.Document, layer.Key, link, skeleton));

    /// <summary>A fact row with a combo of <paramref name="options"/>; a pick, or the wheel over it, commits the changed link.</summary>
    private static void Choice<T>(TimelineEditor current, MotionLayer layer, SkeletonDescription skeleton, string label, string id, string tooltip,
        string preview, IReadOnlyList<(T Value, string Name)> options, T value, Func<T, MotionLink> with)
    {
        TimelineUi.Fact(label);
        ImGui.SetNextItemWidth(Math.Max(Theme.Scaled(140), ImGui.GetContentRegionAvail().X));
        using (var combo = ImRaii.Combo(id, preview, ImGuiComboFlags.HeightLarge))
        {
            if (combo.Success)
            {
                var at = 0;
                foreach (var (option, name) in options)
                {
                    at++;
                    var chosen = EqualityComparer<T>.Default.Equals(option, value);
                    if (ImGui.Selectable($"{name}##{at}", chosen) && !chosen)
                        Commit(current, layer, with(option), skeleton);
                }
            }
            else if (ComboWheel.Steps() is var steps and not 0)
            {
                var next = ComboWheel.Pick(options, option => EqualityComparer<T>.Default.Equals(option.Value, value), steps).Value;
                if (!EqualityComparer<T>.Default.Equals(next, value))
                    Commit(current, layer, with(next), skeleton);
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(tooltip);
    }

    /// <summary>
    /// A combo of <paramref name="skeleton"/>'s bones with a search field, read and listed only while open or turned by the wheel; with
    /// <paramref name="linkable"/>, the bones it marks false are greyed with why. Without a skeleton, the list shows <paramref name="empty"/>.
    /// True with the bone <paramref name="picked"/>.
    /// </summary>
    internal static bool BoneCombo(string id, string? current, Func<SkeletonDescription?> skeleton, bool[]? linkable, string? empty, out string picked)
    {
        picked = string.Empty;
        var chose = false;
        ImGui.SetNextItemWidth(Math.Max(Theme.Scaled(140), ImGui.GetContentRegionAvail().X));
        using (var combo = ImRaii.Combo(id, current is { } shown ? AnimationPresentation.BoneName(shown) : "Choose a bone", ImGuiComboFlags.HeightLarge))
        {
            if (combo.Success)
            {
                ImGui.SetNextItemWidth(-1);
                if (ImGui.IsWindowAppearing())
                {
                    filter = string.Empty;
                    ImGui.SetKeyboardFocusHere();
                }
                ImGui.InputTextWithHint("##link-search", "Search bones", ref filter, 64);
                if (skeleton() is not { } bones)
                {
                    if (empty != null)
                        using (ImRaii.Disabled())
                            ImGui.Selectable(empty);
                }
                else
                    for (var b = 0; b < bones.Bones.Length; b++)
                    {
                        var bone = bones.Bones[b].Name;
                        var name = AnimationPresentation.BoneName(bone);
                        if (filter.Length > 0 && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                            continue;
                        var refused = linkable != null && b < linkable.Length && !linkable[b];
                        var chosen = bone == current;
                        using (ImRaii.Disabled(refused))
                        {
                            if (ImGui.Selectable($"{name}##{b}", chosen) && !chosen)
                            {
                                picked = bone;
                                chose = true;
                            }
                        }
                        if (refused && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                            ImGui.SetTooltip(LinkPicker.Refused(bone));
                    }
            }
            else if (ComboWheel.Steps() is var steps and not 0 && skeleton() is { } bones)
            {
                string[] names = [.. Enumerable.Range(0, bones.Bones.Length).Where(b => linkable == null || b < linkable.Length && linkable[b]).Select(b => bones.Bones[b].Name)];
                if (names.Length > 0 && ComboWheel.Pick(names, current ?? string.Empty, steps) is var next && next != current)
                {
                    picked = next;
                    chose = true;
                }
            }
        }
        return chose;
    }
}
