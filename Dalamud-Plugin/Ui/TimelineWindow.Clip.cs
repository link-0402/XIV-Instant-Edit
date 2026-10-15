using System.Collections.Immutable;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui.Timeline;

namespace InstantEdit.Ui;

/// <summary>
/// The side panel's pages beside the properties: Source (where the clip comes from and the skeleton it
/// was made for), Bones (the bones it animates, ticked off to leave them out of the save and shown live),
/// Edits (every saved edit with Undo) and Record (a live recording for Blender). The save footer's
/// skeleton repair choice lives here too.
/// </summary>
public sealed partial class TimelineWindow
{
    private enum SidePage : byte { Properties, Source, Bones, Edits, Record }

    private SidePage sidePage = SidePage.Properties;
    // A page the panel switches to on its next draw.
    private SidePage? sideRequest;
    // What was selected when the panel last looked: a new selection brings the properties back.
    private string? sideSelection;
    private string animatedBoneFilter = string.Empty;
    // The standard skeleton the next save repairs onto; null keeps the clip's own.
    private SkeletonStandard? repairTarget;
    private Action? openSettings;
    private Action? drawRecorder;

    /// <summary>
    /// What the Timeline window borrows from the main window: Settings, where the skeleton library is
    /// rebuilt, and the live pose recorder, which sends to Blender through it.
    /// </summary>
    internal void AttachTools(Action? settings, Action? recorderPanel)
    {
        openSettings = settings;
        drawRecorder = recorderPanel;
    }

    /// <summary>The side panel: a tab per page, the properties first.</summary>
    private void DrawSidePanel(TimelineEditor current, AnimationTimelineSource loaded)
    {
        // A look or sound edit ends once its control lets go, even when another page is shown by then.
        effectLooks.EndEdit(current);
        soundInspector.EndEdit(current);
        var selection = $"{current.Primary}|{current.SelectedLayer}|{current.SelectedKey}";
        if (selection != sideSelection)
        {
            if (sideSelection != null && (current.Primary != null || current.SelectedLayer != null))
                sideRequest = SidePage.Properties;
            sideSelection = selection;
        }
        using var tabs = ImRaii.TabBar("##timeline-side", ImGuiTabBarFlags.FittingPolicyScroll);
        if (!tabs.Success)
            return;
        SideTab(SidePage.Properties, "Properties", "The selected event or bone layer.", () => properties.Draw(current));
        SideTab(SidePage.Source, "Source", "Where the animation comes from, and the skeleton it was made for.", () => DrawClipSource(loaded));
        if (!loaded.Standalone)
            SideTab(SidePage.Bones, "Bones", "The bones it animates. Untick one to leave it out of the save.", () => DrawAnimatedBones(current, loaded));
        else
            SideTab(SidePage.Bones, "Face", "The face's bones and LivePose adjustments, saved into the face's own file.", () => DrawFaceTools(loaded));
        DrawEditsTab();
        if (drawRecorder != null)
            SideTab(SidePage.Record, "Record", "Record a character's live pose for Blender.", drawRecorder);
        sideRequest = null;
    }

    private void SideTab(SidePage page, string label, string tooltip, Action draw)
    {
        var flags = sideRequest == page ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        var open = ImGui.BeginTabItem(label, flags);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
        if (!open)
            return;
        sidePage = page;
        DrawPage(draw);
        ImGui.EndTabItem();
    }

    /// <summary>The Edits tab, marked while an edit did not finish.</summary>
    private void DrawEditsTab()
    {
        var attention = animations is { } service ? RecoveryAttention(service) : 0;
        bool open;
        using (ImRaii.PushColor(ImGuiCol.Text, Theme.Warning, attention > 0))
            open = ImGui.BeginTabItem(attention > 0 ? $"Edits ({attention})###edits" : "Edits###edits",
                sideRequest == SidePage.Edits ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(attention switch
            {
                0 => "Every saved edit, with Undo.",
                1 => "An edit did not finish and needs an undo.",
                _ => $"{attention} edits did not finish and need an undo.",
            });
        if (!open)
            return;
        sidePage = SidePage.Edits;
        DrawPage(DrawRecentEdits);
        ImGui.EndTabItem();
    }

    /// <summary>A page's body, which scrolls below the tabs.</summary>
    private static void DrawPage(Action draw)
    {
        using var body = ImRaii.Child("##side-page", Vector2.Zero, false);
        if (!body.Success)
            return;
        ImGui.Spacing();
        draw();
    }

    /// <summary>A muted label above a value, for the narrow panel.</summary>
    private static void SideFact(string label)
    {
        ImGui.Spacing();
        ImGui.TextColored(Theme.Muted, label);
    }

    /// <summary>The Source page: the mod and file, the model, the startup, and the skeleton the clip was made for.</summary>
    private void DrawClipSource(AnimationTimelineSource loaded)
    {
        var fresh = Fresh(loaded);
        var capture = fresh.Capture;
        var clip = fresh.Clip;
        var file = capture.Sources.FirstOrDefault(s => s.GamePath == clip.GamePath);
        SideFact("Mod");
        ImGui.TextWrapped(file?.ModName ?? file?.ModDirectory ?? "None, it is the game's own file");
        SideFact("File");
        Widgets.PathText(UiText.Safe(file?.RelativePath, clip.GamePath), UiText.Safe(file?.ResolvedPath, clip.GamePath));
        SideFact("Game path");
        Widgets.PathText(clip.GamePath, clip.GamePath);
        if (fresh.Standalone && capture.ExpressionTimeline is { } timeline)
        {
            // An expression's timing lives in a file of its own, which a mod may replace on its own.
            SideFact("Timeline");
            Widgets.PathText(UiText.Safe(timeline.RelativePath, timeline.GamePath), UiText.Safe(timeline.ResolvedPath, timeline.GamePath));
            SideFact("Timeline from");
            ImGui.TextWrapped(timeline.ModName ?? timeline.ModDirectory ?? "Game Data");
        }
        SideFact("Plays on");
        ImGui.TextWrapped(AnimationPresentation.ModelName(clip));
        if (!fresh.Standalone)
        {
            SideFact(fresh.Startup ? "Leads into" : "Startup");
            if (fresh.Startup)
                ImGui.TextWrapped(AnimationPresentation.AnimationName(capture));
            else if (capture.Startup is null)
                ImGui.TextColored(Theme.Hint, "None found");
            else
                DrawStartupLink(capture);
        }
        SideFact("Live skeleton");
        var live = capture.Clip.TargetSkeleton;
        ImGui.TextWrapped(live == null ? "Unavailable" : $"{Path.GetFileName(capture.Clip.SkeletonPath)} · {live.Bones.Length} bones");
        if (ImGui.IsItemHovered() && !string.IsNullOrWhiteSpace(capture.Clip.SkeletonPath))
            ImGui.SetTooltip(capture.Clip.SkeletonPath);
        SideFact("Made for");
        DrawMadeFor(fresh);
        if (!string.IsNullOrWhiteSpace(capture.Clip.LastOperationError))
        {
            ImGui.Spacing();
            Widgets.Banner("##clip-last-error", FeedbackSeverity.Warning, "Last attempt: " + capture.Clip.LastOperationError);
        }
        ImGui.Spacing();
        // The library is only rebuilt on request, from Settings.
        if (Widgets.IconButton("##skeleton-library", FontAwesomeIcon.Cog,
                "Rebuild the skeleton library in Settings when a skeleton is missing, for example after installing skeleton mods",
                openSettings != null))
            openSettings?.Invoke();
        ImGui.SameLine(0, Theme.Gap);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Hint, "Skeleton library");
    }

    /// <summary>The startup's name, which opens its timeline.</summary>
    private void DrawStartupLink(AnimationCapture capture)
    {
        var name = AnimationPresentation.AnimationName(capture, true);
        using (ImRaii.Disabled(ReadOnly))
        {
            if (ImGui.SmallButton("Open##startup"))
                Open(capture, true);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Open the startup's timeline.");
        ImGui.SameLine(0, Theme.Gap);
        ImGui.TextWrapped(name);
    }

    /// <summary>
    /// The skeleton the clip was made for, which it is read, edited and saved on: a choice when several
    /// fit equally well. The choice is shared with the Bones window.
    /// </summary>
    private void DrawMadeFor(AnimationTimelineSource fresh)
    {
        var resolution = fresh.Clip.Resolution;
        var chosen = resolution?.Selected ?? resolution?.Candidates.FirstOrDefault();
        if (resolution is null || chosen is null)
        {
            ImGui.PushTextWrapPos(0);
            ImGui.TextColored(resolution?.State == SkeletonResolutionState.Incompatible ? Theme.Warning : Theme.Hint,
                resolution?.Reason ?? "Finding the skeleton this animation was made for");
            ImGui.PopTextWrapPos();
            return;
        }
        if (resolution.Candidates.Length > 1)
        {
            ImGui.SetNextItemWidth(-1);
            using (ImRaii.Disabled(animations!.Busy || ReadOnly))
            using (var combo = ImRaii.Combo("##made-for", resolution.Selected is null ? "Choose the skeleton it was made for" : AnimationPresentation.SkeletonLabel(chosen)))
            {
                if (combo.Success)
                    foreach (var candidate in resolution.Candidates)
                    {
                        if (ImGui.Selectable($"{AnimationPresentation.SkeletonLabel(candidate)} · {Path.GetFileName(candidate.Source.Resource.ResolvedPath)}##{AnimationSkeletonIndex.SelectionId(candidate)}",
                                candidate == resolution.Selected))
                            posing.ChooseSkeleton(fresh, candidate);
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip(candidate.Rationale + "\n" + candidate.Source.Resource.ResolvedPath);
                    }
            }
            if (resolution.Selected is null)
                Widgets.HintWrapped(resolution.Reason ?? "Several skeletons fit. Choose the one this animation was made for.");
            if (posing.ChoiceError is { } choiceError)
                Widgets.HintWrapped(choiceError);
        }
        else
            ImGui.TextWrapped(AnimationPresentation.SkeletonLabel(chosen));
        Widgets.HelpTip("Why this skeleton: " + chosen.Rationale);
        ImGui.SameLine(0, Theme.Gap);
        Widgets.PathText(chosen.Source.Resource.ResolvedPath, chosen.Source.Resource.ResolvedPath);
        if (chosen.Source.LeadingBones > 0)
            Widgets.HintWrapped($"Uses only its first {chosen.Source.LeadingBones} bones: the animation was likely made before " +
                                "more bones were added to this skeleton.");
    }

    /// <summary>
    /// The Bones page: every bone the clip animates, ticked while it stays in. Unticking leaves the bone
    /// out of the save, for example to give hair, cloth or tail bones back to physics, and shows at once
    /// while Live is on. Each change is one undo step.
    /// </summary>
    private void DrawAnimatedBones(TimelineEditor current, AnimationTimelineSource loaded)
    {
        var clip = Fresh(loaded).Clip;
        var bones = AnimationBones.Animated(clip);
        if (bones.IsEmpty)
        {
            Widgets.HintWrapped("The bones it moves are listed once the skeleton it was made for is known.");
            return;
        }
        var doc = current.Document;
        var excluded = doc.ExcludedBones;
        var readOnly = ReadOnly;
        Widgets.HintWrapped("Untick a bone to stop the animation moving it, such as hair or a tail for physics.");
        if (!liveWanted)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Hint, "Turn on Live to see it in GPose.");
        }
        ImGui.Spacing();
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
        var highest = AnimationBones.HighestIndex(clip, left);
        ImGui.TextColored(Theme.Muted, (left.Count == 0 ? $"All {bones.Length} kept" : $"{bones.Length - left.Count} of {bones.Length} kept") +
                                       (highest < 0 ? "" : $" · highest index {highest}"));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Sync plugins refuse animations whose bone indices go past the other player's skeleton.");

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

    private static string PresetGroups(SkeletonStandard standard) => standard switch
    {
        SkeletonStandard.Ivcs => "the game's bones and IVCS's iv_ bones",
        SkeletonStandard.IvcsYas => "the game's, IVCS's iv_ and YAS's ya_ bones",
        _ => "the game's own bones",
    };

    /// <summary>
    /// The save footer's skeleton choice: keep the clip's own, or repair it onto a standard skeleton so
    /// every bone sits at the index other players' skeletons and sync plugins expect.
    /// </summary>
    private void DrawRepairChoice(AnimationTimelineSource loaded)
    {
        var clip = Fresh(loaded).Clip;
        if (repairTarget is { } chosen && AnimationBones.Standard(clip, chosen) is null)
            repairTarget = null;
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, "Skeleton");
        ImGui.SameLine(0, Theme.Gap);
        ImGui.SetNextItemWidth(Theme.Scaled(170));
        using (ImRaii.Disabled(ReadOnly))
        using (var combo = ImRaii.Combo("##repair", repairTarget is { } target ? "Repair onto " + AnimationBones.StandardName(target) : "Keep its own"))
        {
            if (combo.Success)
            {
                if (ImGui.Selectable("Keep its own", repairTarget == null))
                    repairTarget = null;
                foreach (var standard in Enum.GetValues<SkeletonStandard>())
                {
                    var found = AnimationBones.Standard(clip, standard);
                    if (ImGui.Selectable("Repair onto " + AnimationBones.StandardName(standard), repairTarget == standard,
                            found is null ? ImGuiSelectableFlags.Disabled : ImGuiSelectableFlags.None))
                        repairTarget = standard;
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                        ImGui.SetTooltip(found is not null
                            ? $"{found.Skeleton.Bones.Length} bones, from {found.Origin}"
                            : standard == SkeletonStandard.Vanilla
                                ? "The game's own skeleton for this model was not found."
                                : $"No {AnimationBones.StandardName(standard)} skeleton for this model is in the skeleton library.");
                }
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Repair moves every bone to the index other players' skeletons use, whatever your skeleton mod adds.\n" +
                             "Only bones the animation moves are written.");
        if (repairTarget is { } repair && AnimationBones.Plan(clip, repair, editor?.Document.ExcludedBones ?? []) is { } plan)
        {
            ImGui.SameLine(0, Theme.Gap);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Muted, plan.Dropped.IsEmpty
                ? $"Highest index {plan.HighestIndex}"
                : $"Highest index {plan.HighestIndex} · {plan.Dropped.Length} bones left out");
            if (!plan.Dropped.IsEmpty && ImGui.IsItemHovered())
                ImGui.SetTooltip($"The {AnimationBones.StandardName(repair)} skeleton lacks " +
                                 string.Join(", ", plan.Dropped.Take(8).Select(AnimationPresentation.BoneName)) +
                                 (plan.Dropped.Length > 8 ? $" and {plan.Dropped.Length - 8} more." : "."));
        }
    }

}
