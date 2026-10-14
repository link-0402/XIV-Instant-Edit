using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui.Timeline;

namespace InstantEdit.Ui;

/// <summary>
/// The properties panel for the clip band: a part (its frames, removing frames, removing it), a join
/// (its blend's length and place, the jump it blends away) and the loop seam (its state, the jump,
/// a stall, its blend and Fix loop). Also the seam's badge at the end of the clip band.
/// </summary>
public sealed partial class TimelineWindow
{
    // The Remove frames fields of the selected part, and which part and range they were filled for.
    private int removeFrom, removeTo;
    private string? removeFor;
    // The loop seam as the motion has it (cached per decode and range), and as the cut output plays it.
    private string? rawSeamKey;
    private SeamReport? rawSeam;
    private MotionCuts? outputSeamCuts;
    private int outputSeamLength;
    private string? outputSeamDecode;
    private SeamReport? outputSeam;
    // Where the seam's badge was drawn last, in screen pixels: a click there selects the seam.
    private (Vector2 Min, Vector2 Max)? seamBadge;

    private void DrawCutPanel(TimelineEditor current)
    {
        if (current.SelectedCut is not { } cut)
            return;
        var doc = current.Document;
        var cuts = CutEdits.CutsOf(doc);
        var decoded = source is { Standalone: false } loaded ? DecodedPose(loaded) : null;
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
        var readOnly = ReadOnly;
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
            DrawDecodeNote();
    }

    private void DrawSeamPanel(TimelineEditor current, MotionCuts cuts, MotionClip? decoded)
    {
        var readOnly = ReadOnly;
        Title(FontAwesomeIcon.SyncAlt, "Loop seam");
        Widgets.HintWrapped("Where the loop's end runs back into its start.");
        ImGui.Spacing();
        if (decoded == null)
        {
            DrawDecodeNote();
            return;
        }
        var (raw, shown) = SeamOf(current, decoded);
        ImGui.TextColored(shown.State == SeamState.Clean ? Theme.Success : Theme.Warning, TimelineWindowViews.SeamBadge(shown.State));
        Widgets.HintWrapped(TimelineWindowViews.GapText(shown.Gap));
        if (raw.StallFrom is { } stall && stall < cuts.Parts[^1].To)
            Widgets.HintWrapped(TimelineWindowViews.StallText(cuts.Parts[^1].To - stall));
        if (cuts.Seam is { } seam)
        {
            ImGui.Spacing();
            DrawBlend(current, seam, MotionCutter.Window(cuts, -1, current.Document.Length), value => current.SetSeam(value));
            if (seam.Placement != JoinPlacement.Before)
                Widgets.HintWrapped(TimelineWindowViews.SeamPlacementNote);
        }
        ImGui.Spacing();
        using (ImRaii.Disabled(readOnly || !current.Loops))
        {
            if (ImGui.Button("Fix loop"))
                current.FixLoop(raw);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(TimelineWindowViews.FixLoopTip);
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
                Fact("Blend");
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
                Fact("Place");
                ImGui.SetNextItemWidth(Theme.Scaled(120));
                using var combo = ImRaii.Combo("##place", TimelineWindowViews.PlacementName(blend.Placement));
                if (combo.Success)
                    foreach (var place in Enum.GetValues<JoinPlacement>())
                        if (ImGui.Selectable(TimelineWindowViews.PlacementName(place), place == blend.Placement) && place != blend.Placement && !readOnly)
                            set(blend with { Placement = place });
            }
        }
        var total = used.Before + used.After;
        if (blend.Frames > 0 && total < blend.Frames - .5)
            Widgets.HintWrapped(TimelineWindowViews.ShortenedText(total));
    }

    /// <summary>While the motion is read, how far along; why it failed when it did.</summary>
    private void DrawDecodeNote()
    {
        if (decodeError != null && source != null && failedDecodeKey == DecodeKey(Fresh(source)))
            ImGui.TextColored(Theme.Warning, "The motion could not be read: " + decodeError);
        else
            ImGui.TextColored(Theme.Muted, decodeStatus);
    }

    private static void Title(FontAwesomeIcon icon, string text)
    {
        Widgets.Icon(icon, TimelineWindowViews.LaneColour(TimelineLane.Motion));
        ImGui.SameLine(0, Theme.Gap);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Label, text);
    }

    /// <summary>
    /// The loop seam as the motion has it between the first part's start and the last part's end (what
    /// a blend absorbs and Fix loop works from), and as the output plays it: with a seam blend, the cut
    /// motion is measured, once per change and never during a drag.
    /// </summary>
    private (SeamReport Raw, SeamReport Shown) SeamOf(TimelineEditor current, MotionClip decoded)
    {
        var doc = current.Document;
        var cuts = CutEdits.CutsOf(doc);
        int from = cuts.Parts[0].From, to = cuts.Parts[^1].To;
        var key = $"{decodedKey}|{from}|{to}";
        if (rawSeam == null || rawSeamKey != key)
        {
            rawSeam = LoopSeam.Analyze(decoded, from, to);
            rawSeamKey = key;
        }
        if (cuts.Seam is not { Frames: > 0 })
            return (rawSeam, rawSeam);
        var stale = !ReferenceEquals(outputSeamCuts, doc.Cuts) || outputSeamLength != doc.Length || outputSeamDecode != decodedKey;
        if (stale && current.Gesture == TimelineGesture.None)
        {
            outputSeam = LoopSeam.Analyze(MotionCutter.Apply(decoded, cuts, doc.Length), 0, doc.Length);
            (outputSeamCuts, outputSeamLength, outputSeamDecode) = (doc.Cuts, doc.Length, decodedKey);
            stale = false;
        }
        return (rawSeam, !stale && outputSeam is { } shown ? shown : rawSeam);
    }

    /// <summary>The seam's badge at the clip band's end, on a loop whose motion is read; a click on it selects the seam.</summary>
    private void DrawSeamBadge(ImDrawListPtr drawList, TimelineEditor current, Vector2 max, float trackLeft, float top, float bottom)
    {
        seamBadge = null;
        if (!current.Loops || source is not { Standalone: false } loaded || DecodedPose(loaded) is not { } decoded)
            return;
        var shown = SeamOf(current, decoded).Shown.State;
        var text = TimelineWindowViews.SeamBadge(shown);
        var size = ImGui.CalcTextSize(text);
        var pad = Theme.Scaled(4);
        var end = trackLeft + current.View.X(current.Document.Length);
        var left = Math.Min(end + Theme.Scaled(6), max.X - size.X - pad * 2 - Theme.Scaled(4));
        var rect = (new Vector2(left, top + Theme.Scaled(3)), new Vector2(left + size.X + pad * 2, bottom - Theme.Scaled(3)));
        var colour = shown == SeamState.Clean ? Theme.Muted : Theme.Warning;
        drawList.AddRectFilled(rect.Item1, rect.Item2, U32(Theme.WithAlpha(colour, .18f)), Theme.Scaled(3));
        drawList.AddRect(rect.Item1, rect.Item2, U32(Theme.WithAlpha(colour, .7f)), Theme.Scaled(3));
        drawList.AddText(new Vector2(left + pad, (top + bottom - size.Y) / 2), U32(colour), text);
        seamBadge = rect;
    }

    /// <summary>Whether a click at <paramref name="mouse"/> (screen pixels) is on the seam's badge.</summary>
    private bool OnSeamBadge(Vector2 mouse) =>
        seamBadge is { } badge && mouse.X >= badge.Min.X && mouse.X <= badge.Max.X && mouse.Y >= badge.Min.Y && mouse.Y <= badge.Max.Y;
}
