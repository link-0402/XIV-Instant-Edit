using System.Collections.Immutable;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// The side panel's clip view, shown while nothing is selected: Source (where the clip comes from and the skeleton it was
/// made for), Length (or a face session's Face) and Bones kept, whose Edit opens the bones-kept page in its place.
/// </summary>
/// <param name="open">Opens the timeline of a capture's clip, or of its startup.</param>
internal sealed class ClipView(TimelineSession session, BonePosing posing, TimelinePreview timelinePreview, BlenderMotionPanel blenderMotion,
    Action<AnimationCapture, bool> open)
{
    /// <summary>How long a still face becomes when it starts moving, in frames: 3 seconds.</summary>
    private const int DefaultMovingFrames = 3 * TimelineDocument.Fps;

    private string animatedBoneFilter = string.Empty;
    // The bones-kept page shows in place of the clip view.
    private bool bonesPage;
    private bool lengthActive;
    private float lengthEdit;
    // The Bones kept line's numbers and the skeleton choice and bones left out they were worked out for.
    private (SkeletonResolution? Resolution, ImmutableArray<string> Excluded, ImmutableArray<string> Bones, int Kept, int Highest)? keptCounts;

    /// <summary>Opens Settings, where the skeleton library is rebuilt; null until the main window attaches it.</summary>
    public Action? OpenSettings { get; set; }

    /// <summary>Clears the bones list's filter and goes back from the bones-kept page, for another clip.</summary>
    public void ClearFilter()
    {
        animatedBoneFilter = string.Empty;
        bonesPage = false;
    }

    /// <summary>The Bones kept line: "All 40 kept" or "37 of 40 kept", then the highest index when one is known.</summary>
    public static string KeptText(int kept, int total, int highest) =>
        (kept == total ? $"All {total} kept" : $"{kept} of {total} kept") + (highest < 0 ? "" : $" · highest index {highest}");

    /// <summary>
    /// The clip view for <paramref name="current"/>, the timeline of <paramref name="loaded"/>: Source, Length (Face for a
    /// face session) and Bones kept; or the bones-kept page while it is open.
    /// </summary>
    public void Draw(TimelineEditor current, AnimationTimelineSource loaded)
    {
        // An expression whose face can't be read has no bones to keep.
        if (bonesPage && !loaded.MotionTarget.Standalone)
        {
            DrawBonesPage(current, loaded);
            return;
        }
        bonesPage = false;
        Widgets.SectionHeader("Source");
        DrawClipSource(loaded);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        if (current.Document.Face is { } face)
        {
            Widgets.SectionHeader("Face");
            DrawFaceLength(current, face, session.ReadOnly);
        }
        else
        {
            Widgets.SectionHeader("Length");
            DrawLength(current, session.ReadOnly);
        }
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        Widgets.SectionHeader("Bones kept");
        if (loaded.MotionTarget.Standalone)
            DrawFaceProblem(loaded);
        else
            DrawKept(current, loaded);
    }

    /// <summary>A muted label above a value, for the narrow panel.</summary>
    private static void SideFact(string label)
    {
        ImGui.Spacing();
        ImGui.TextColored(Theme.Muted, label);
    }

    /// <summary>The Source part: the mod and file, the model, the startup, and the skeleton the clip was made for.</summary>
    private void DrawClipSource(AnimationTimelineSource loaded)
    {
        var fresh = session.Fresh(loaded);
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
                OpenSettings != null))
            OpenSettings?.Invoke();
        ImGui.SameLine(0, Theme.Gap);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Hint, "Skeleton library");
    }

    /// <summary>The startup's name, which opens its timeline.</summary>
    private void DrawStartupLink(AnimationCapture capture)
    {
        var name = AnimationPresentation.AnimationName(capture, true);
        using (ImRaii.Disabled(session.ReadOnly))
        {
            if (ImGui.SmallButton("Open##startup"))
                open(capture, true);
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
            using (ImRaii.Disabled(session.Animations!.Busy || session.ReadOnly))
            using (var combo = ImRaii.Combo("##made-for", resolution.Selected is null ? "Choose the skeleton it was made for" : AnimationPresentation.SkeletonLabel(chosen)))
            {
                if (combo.Success)
                {
                    foreach (var candidate in resolution.Candidates)
                    {
                        if (ImGui.Selectable($"{AnimationPresentation.SkeletonLabel(candidate)} · {Path.GetFileName(candidate.Source.Resource.ResolvedPath)}##{AnimationSkeletonIndex.SelectionId(candidate)}",
                                candidate == resolution.Selected))
                            posing.ChooseSkeleton(fresh, candidate);
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip(candidate.Rationale + "\n" + candidate.Source.Resource.ResolvedPath);
                    }
                }
                else if (ComboWheel.Scroll(resolution.Candidates, resolution.Selected!, out var picked))
                    posing.ChooseSkeleton(fresh, picked);
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
    /// The clip's length in seconds, which stretches the motion, and Scale events with length; an expression's timeline keeps
    /// its own.
    /// </summary>
    private void DrawLength(TimelineEditor current, bool readOnly)
    {
        ImGui.SetNextItemWidth(Theme.Scaled(72));
        // While typing, the field shows what was typed; Enter or leaving it applies the length once.
        var seconds = lengthActive ? lengthEdit : current.Document.Length / (float)TimelineDocument.Fps;
        using (ImRaii.Disabled(readOnly || current.FixedLength))
        {
            if (ImGui.InputFloat("##timeline-length", ref seconds, 0, 0, "%.2f"))
                lengthEdit = seconds;
            if (ImGui.IsItemActivated())
                lengthEdit = seconds;
            lengthActive = ImGui.IsItemActive();
            if (ImGui.IsItemDeactivatedAfterEdit() && float.IsFinite(lengthEdit))
                current.SetLength(TimelineWindowViews.Frames(lengthEdit));
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(current.FixedLength
                ? "An expression's timeline keeps its length."
                : "Stretches the motion to this length. Press Enter to apply it. Trim it on the clip row.");
        ImGui.SameLine(0, Theme.Scaled(3));
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, "s");
        if (current.FixedLength)
            return;
        // The same setting as the View menu's.
        var scale = current.ScaleEvents;
        if (ImGui.Checkbox("Scale events with length##timeline-scale-events", ref scale))
            current.ScaleEvents = scale;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(current.ScaleEvents ? "Scale events with length is on. A new length moves and stretches the events too."
                : "Scale events with length is off. A new length keeps the events where they are.");
    }

    /// <summary>
    /// A face's own length: Still (one shape, held) or Moving, then its length in seconds. Turning Moving on or off holds the
    /// face's first frame; a moving face's seconds change its length.
    /// </summary>
    private void DrawFaceLength(TimelineEditor current, TimelineFace face, bool readOnly)
    {
        var unavailable = blenderMotion.FaceUnavailable(current);
        var moves = !face.Still;
        using (ImRaii.Disabled(readOnly || unavailable != null))
        {
            if (ImGui.Checkbox("Moves##timeline-face-moves", ref moves))
                blenderMotion.HoldFace(current, moves ? DefaultMovingFrames : 1);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(unavailable ?? (face.Still ? "Still holds one shape. Tick to make the face move over a length." : "Untick to make the face one shape again."));
        if (face.Still)
            return;
        ImGui.SameLine(0, Theme.Gap);
        ImGui.SetNextItemWidth(Theme.Scaled(72));
        var seconds = lengthActive ? lengthEdit : face.Frames / (float)TimelineDocument.Fps;
        using (ImRaii.Disabled(readOnly))
        {
            if (ImGui.InputFloat("##timeline-face-length", ref seconds, 0, 0, "%.2f"))
                lengthEdit = seconds;
            if (ImGui.IsItemActivated())
                lengthEdit = seconds;
            lengthActive = ImGui.IsItemActive();
            if (ImGui.IsItemDeactivatedAfterEdit() && float.IsFinite(lengthEdit))
                current.SetFaceLength(TimelineWindowViews.Frames(lengthEdit));
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("The face's length. Press Enter to apply it.");
        ImGui.SameLine(0, Theme.Scaled(3));
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, "s");
    }

    /// <summary>
    /// The bones <paramref name="clip"/> animates and the Bones kept line's numbers with <paramref name="excluded"/> left
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

    /// <summary>The Bones kept line, with Edit, which opens the bones-kept page.</summary>
    private void DrawKept(TimelineEditor current, AnimationTimelineSource loaded)
    {
        var clip = session.Fresh(loaded).Clip;
        var counts = KeptCounts(clip, current.Document.ExcludedBones);
        if (counts.Bones.IsEmpty)
        {
            Widgets.HintWrapped("The bones it moves are listed once the skeleton it was made for is known.");
            return;
        }
        ImGui.AlignTextToFramePadding();
        DrawKeptCount(counts);
        ImGui.SameLine(0, Theme.Gap);
        if (ImGui.SmallButton("Edit##bones-kept"))
            bonesPage = true;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(current.Document.HasClip ? "The bones it animates. Untick one to leave it out of the save."
                : "The face's bones. Untick one to leave it out of the save.");
    }

    /// <summary>How many of the bones stay in and the highest index among them.</summary>
    private static void DrawKeptCount((ImmutableArray<string> Bones, int Kept, int Highest) counts)
    {
        ImGui.TextColored(Theme.Muted, KeptText(counts.Kept, counts.Bones.Length, counts.Highest));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Sync plugins refuse animations whose bone indices go past the other player's skeleton.");
    }

    /// <summary>The bones-kept page: the way back to the clip view, then the bones.</summary>
    private void DrawBonesPage(TimelineEditor current, AnimationTimelineSource loaded)
    {
        bool back;
        using (ImRaii.PushId("##bones-kept-back"))
            back = ImGuiComponents.IconButtonWithText(FontAwesomeIcon.ArrowLeft, "Clip", null, null, null, null);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Back to the clip.");
        if (back)
            bonesPage = false;
        ImGui.Spacing();
        DrawAnimatedBones(current, loaded);
    }

    /// <summary>An expression whose face can't be read: why, in place of its bones.</summary>
    private static void DrawFaceProblem(AnimationTimelineSource loaded) =>
        Widgets.HintWrapped(loaded.FaceProblem ?? "This expression's face can't be edited.");

    /// <summary>
    /// The bones-kept page's bones: every bone the clip animates, ticked while it stays in. Unticking leaves the bone
    /// out of the save, for example to give hair, cloth or tail bones back to physics, and shows at once
    /// while Live is on. Each change is one undo step.
    /// </summary>
    private void DrawAnimatedBones(TimelineEditor current, AnimationTimelineSource loaded)
    {
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
