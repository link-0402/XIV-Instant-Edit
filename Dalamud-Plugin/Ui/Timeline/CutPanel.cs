using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// The properties panel for the clip band: a part (its frames, removing frames, removing it), a join
/// (its blend's length and place, the jump it blends away) and the loop seam (its state, the jump,
/// a stall, its blend, its ease and Fix loop).
/// </summary>
internal sealed class CutPanel(TimelineSession session, MotionDecode decode, TimelineClipBand clipBand)
{
    // The Remove frames fields of the selected part, and which part and range they were filled for.
    private int removeFrom, removeTo;
    private string? removeFor;

    public void Draw(TimelineEditor current)
    {
        if (current.SelectedCut is not { } cut)
            return;
        var doc = current.Document;
        var cuts = CutEdits.CutsOf(doc);
        var decoded = session.Source is { Standalone: false } loaded ? decode.DecodedPose(loaded) : null;
        switch (cut.Target)
        {
            case TimelineCutTarget.Part when cut.Index >= 0 && cut.Index < cuts.Parts.Length:
                DrawPartPanel(current, cuts, cut.Index);
                break;
            case TimelineCutTarget.Join when cut.Index >= 0 && cut.Index < cuts.Joins.Length:
                DrawJoinPanel(current, cuts, cut.Index, decoded);
                break;
            case TimelineCutTarget.Seam when current.Loops:
                DrawSeamPanel(current, cuts, decoded);
                break;
            default:
                Widgets.EmptyState(FontAwesomeIcon.MousePointer, "Select an event",
                    "Click an event or a bone layer to edit it. Double-click a lane to add one.");
                break;
        }
    }

    private void DrawPartPanel(TimelineEditor current, MotionCuts cuts, int index)
    {
        var doc = current.Document;
        var readOnly = session.ReadOnly;
        var part = cuts.Parts[index];
        var start = cuts.PartStart(index, doc.Length);
        var end = cuts.PartEnd(index, doc.Length);
        Title(FontAwesomeIcon.Film, cuts.Parts.Length == 1 ? "The clip's motion" : $"Part {index + 1} of {cuts.Parts.Length}");
        Widgets.HintWrapped(TimelineWindowViews.PartRange(part, start, end));
        ImGui.Spacing();
        var key = $"{index}|{TimelineSnap.Round(start)}|{TimelineSnap.Round(end)}";
        if (removeFor != key)
        {
            removeFor = key;
            (removeFrom, removeTo) = (TimelineSnap.Round(start), TimelineSnap.Round(end));
        }
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, "Remove frames");
        ImGui.SameLine(0, Theme.Gap);
        ImGui.SetNextItemWidth(Theme.Scaled(70));
        ImGui.InputInt("##remove-from", ref removeFrom, 0, 0);
        ImGui.SameLine(0, Theme.Gap);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, "to");
        ImGui.SameLine(0, Theme.Gap);
        ImGui.SetNextItemWidth(Theme.Scaled(70));
        ImGui.InputInt("##remove-to", ref removeTo, 0, 0);
        (removeFrom, removeTo) = (Math.Clamp(removeFrom, 0, doc.Length), Math.Clamp(removeTo, 0, doc.Length));
        ImGui.SameLine(0, Theme.Gap);
        using (ImRaii.Disabled(readOnly || removeTo <= removeFrom))
        {
            if (ImGui.Button("Remove"))
                current.RemoveRange(removeFrom, removeTo);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Removes these frames. What plays after them moves up.");
        ImGui.Spacing();
        using (ImRaii.Disabled(readOnly || cuts.Parts.Length < 2))
        {
            if (ImGui.Button("Remove part"))
                current.RemoveSelectedPart();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(cuts.Parts.Length < 2 ? "Cut the motion first (C at the playhead)." : "Removes this part. What plays after it moves up.");
    }

    private void DrawJoinPanel(TimelineEditor current, MotionCuts cuts, int index, MotionClip? decoded)
    {
        var doc = current.Document;
        Title(FontAwesomeIcon.Link, $"Join {index + 1}");
        Widgets.HintWrapped("The blend where two parts meet, so the motion doesn't jump there.");
        ImGui.Spacing();
        var join = cuts.Joins[index];
        DrawBlend(current, join, MotionCutter.Window(cuts, index, doc.Length), value => current.SetJoin(index, value));
        if (decoded != null)
            Widgets.HintWrapped(TimelineWindowViews.GapText(LoopSeam.Gap(decoded, cuts.Parts[index].To, cuts.Parts[index + 1].From)));
        else
            decode.DrawNote();
    }

    private void DrawSeamPanel(TimelineEditor current, MotionCuts cuts, MotionClip? decoded)
    {
        var readOnly = session.ReadOnly;
        Title(FontAwesomeIcon.SyncAlt, "Loop seam");
        Widgets.HintWrapped("Where the loop's end runs back into its start.");
        ImGui.Spacing();
        if (decoded == null)
        {
            decode.DrawNote();
            return;
        }
        var (raw, shown) = clipBand.SeamOf(current, decoded);
        ImGui.TextColored(shown.State == SeamState.Clean ? Theme.Success : Theme.Warning, TimelineWindowViews.SeamBadge(shown.State));
        Widgets.HintWrapped(TimelineWindowViews.GapText(shown.Gap));
        // The hold is the source's, in its frames; what still jumps is the output's.
        if (shown.State != SeamState.Clean && raw.StallFrom is { } stall && stall < cuts.Parts[^1].To)
            Widgets.HintWrapped(TimelineWindowViews.StallText(cuts.Parts[^1].To - stall));
        else if (shown.State == SeamState.Stalls)
            Widgets.HintWrapped(TimelineWindowViews.JumpText(shown.Gap.SpeedIn, shown.Gap.SpeedOut));
        if (cuts.Seam is { } seam)
        {
            ImGui.Spacing();
            DrawBlend(current, seam, MotionCutter.Window(cuts, -1, current.Document.Length), value => current.SetSeam(value));
            if (seam.Placement != JoinPlacement.Before)
                Widgets.HintWrapped(TimelineWindowViews.SeamPlacementNote);
        }
        ImGui.Spacing();
        DrawEase(current, cuts);
        ImGui.Spacing();
        var clean = shown.State == SeamState.Clean;
        using (ImRaii.Disabled(readOnly || !current.Loops || clean))
        {
            if (ImGui.Button("Fix loop"))
                current.FixLoop(raw);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(clean ? TimelineWindowViews.LoopCleanTip : TimelineWindowViews.FixLoopTip);
        if (cuts.Seam != null)
        {
            ImGui.SameLine(0, Theme.Gap);
            using (ImRaii.Disabled(readOnly))
            {
                if (ImGui.Button("Remove blend"))
                    current.SetSeam(null);
            }
        }
    }

    /// <summary>A blend's length and place, each change one undo step, and the frames it really uses when the parts are too short.</summary>
    private static void DrawBlend(TimelineEditor current, MotionJoin blend, (double Before, double After) used, Func<MotionJoin, bool> set)
    {
        var readOnly = current.Gesture != TimelineGesture.None;
        using (var facts = ImRaii.Table("##cut-blend", 2, ImGuiTableFlags.SizingFixedFit))
        {
            if (facts.Success)
            {
                ImGui.TableSetupColumn("##fact", ImGuiTableColumnFlags.WidthFixed);
                ImGui.TableSetupColumn("##value", ImGuiTableColumnFlags.WidthStretch);
                TimelineUi.Fact("Blend");
                var frames = blend.Frames;
                ImGui.SetNextItemWidth(Theme.Scaled(90));
                using (ImRaii.Disabled(readOnly))
                {
                    var changed = ImGui.InputInt("##blend", ref frames, 1, 6);
                    if (ImGui.IsItemActivated())
                        current.BeginFieldEdit();
                    if (changed)
                        set(blend with { Frames = Math.Clamp(frames, 0, MotionJoin.MaxFrames) });
                    if (ImGui.IsItemDeactivated())
                        current.EndFieldEdit();
                }
                ImGui.SameLine(0, Theme.Gap);
                ImGui.TextColored(Theme.Muted, "frames");
                TimelineUi.Fact("Place");
                ImGui.SetNextItemWidth(Theme.Scaled(120));
                using var combo = ImRaii.Combo("##place", TimelineWindowViews.PlacementName(blend.Placement));
                if (combo.Success)
                {
                    foreach (var place in Enum.GetValues<JoinPlacement>())
                        if (ImGui.Selectable(TimelineWindowViews.PlacementName(place), place == blend.Placement) && place != blend.Placement && !readOnly)
                            set(blend with { Placement = place });
                }
                else if (!readOnly && ComboWheel.Scroll(Enum.GetValues<JoinPlacement>(), blend.Placement, out var picked))
                    set(blend with { Placement = picked });
            }
        }
        var total = used.Before + used.After;
        if (blend.Frames > 0 && total < blend.Frames - .5)
            Widgets.HintWrapped(TimelineWindowViews.ShortenedText(total));
    }

    /// <summary>The loop seam's ease: over how many frames (0 for none) and on which side, each change one undo step.</summary>
    private static void DrawEase(TimelineEditor current, MotionCuts cuts)
    {
        var readOnly = current.Gesture != TimelineGesture.None;
        using var facts = ImRaii.Table("##seam-ease", 2, ImGuiTableFlags.SizingFixedFit);
        if (!facts.Success)
            return;
        ImGui.TableSetupColumn("##fact", ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("##value", ImGuiTableColumnFlags.WidthStretch);
        TimelineUi.Fact(TimelineWindowViews.EaseLegend);
        var frames = cuts.Ease;
        ImGui.SetNextItemWidth(Theme.Scaled(90));
        using (ImRaii.Disabled(readOnly))
        {
            var changed = ImGui.InputInt("##ease", ref frames, 1, 6);
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(TimelineWindowViews.EaseTip);
            if (ImGui.IsItemActivated())
                current.BeginFieldEdit();
            if (changed)
                current.SetEase(frames);
            if (ImGui.IsItemDeactivated())
                current.EndFieldEdit();
        }
        ImGui.SameLine(0, Theme.Gap);
        ImGui.TextColored(Theme.Muted, "frames");
        if (cuts.Ease <= 0)
            return;
        TimelineUi.Fact("Side");
        var sides = TimelineWindowViews.EaseSides;
        ImGui.SetNextItemWidth(Theme.Scaled(120));
        using var combo = ImRaii.Combo("##ease-side", sides[cuts.EaseEnd ? 1 : 0]);
        if (combo.Success)
        {
            for (var i = 0; i < sides.Length; i++)
                if (ImGui.Selectable(sides[i], (i == 1) == cuts.EaseEnd) && (i == 1) != cuts.EaseEnd && !readOnly)
                    current.SetEase(cuts.Ease, atEnd: i == 1);
        }
        else
        {
            var side = cuts.EaseEnd ? 1 : 0;
            if (!readOnly && ComboWheel.Scroll(ref side, sides.Length))
                current.SetEase(cuts.Ease, atEnd: side == 1);
        }
    }

    private static void Title(FontAwesomeIcon icon, string text)
    {
        Widgets.Icon(icon, TimelineWindowViews.LaneColour(TimelineLane.Motion));
        ImGui.SameLine(0, Theme.Gap);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Label, text);
    }
}
