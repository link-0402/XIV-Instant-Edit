using System.Collections.Immutable;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Models;
using InstantEdit.Services.Animations;

namespace InstantEdit.Ui;

/// <summary>
/// The Face page of an expression's timeline. The timeline saves only the expression's own file, so
/// what changes the face's motion runs as an edit of the face's file of its own, with the save
/// footer's new mod or in place: leaving bones out, and baking your LivePose face adjustments in.
/// </summary>
public sealed partial class TimelineWindow
{
    // The face's bones left out of the next face save, by name; another clip starts over.
    private readonly HashSet<string> faceExcluded = new(StringComparer.Ordinal);
    private string? faceClip;
    // The face edit started here, whose result the page shows.
    private Guid? faceJob;

    private void DrawFaceTools(AnimationTimelineSource loaded)
    {
        if (animations is not { } service)
            return;
        var fresh = Fresh(loaded);
        var capture = fresh.Capture;
        var clip = fresh.Clip;
        if (faceClip != ClipKey(fresh))
        {
            faceClip = ClipKey(fresh);
            faceExcluded.Clear();
            faceJob = null;
        }
        Widgets.HintWrapped("These change the face's own file, " +
                            (destination == AnimationDestination.NewMod ? "into a new mod" : "in place") + ", as the save footer says.");
        var bones = AnimationBones.Animated(clip);
        faceExcluded.IntersectWith(bones);
        var problem = FaceProblem(capture, clip);
        ImGui.Spacing();
        ImGui.TextColored(Theme.Muted, "LivePose");
        var adjusted = capture.Pose.Bones.Where(b => b.Id.Slot == 0 && b.Id.Partial == clip.Partial).ToImmutableArray();
        var bakeMissing = problem ?? capture.PoseUnavailableReason ?? (adjusted.IsEmpty ? "Your face has no LivePose adjustments." : null)
            ?? AnimationBones.OffsetConflict(faceExcluded, adjusted.Select(b => b.Id.Name));
        using (ImRaii.Disabled(service.Busy || ReadOnly || bakeMissing != null))
        {
            if (ImGui.Button("Bake LivePose into the face"))
                faceJob = StartFaceEdit(service, new AnimationBakeRequest(Guid.NewGuid(), capture, destination, modName.Trim(), false,
                    [.. adjusted.Select(b => b.Id)], PoseComponents.All, ExcludedBones: [.. faceExcluded.Order(StringComparer.Ordinal)]));
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(bakeMissing ?? "Bake your LivePose face adjustments into the face, and clear them from LivePose.");

        ImGui.Spacing();
        ImGui.TextColored(Theme.Muted, "Animated bones");
        if (bones.IsEmpty)
        {
            Widgets.HintWrapped("The bones it moves are listed once the skeleton it was made for is known.");
            DrawFaceResult(service);
            return;
        }
        var excludeMissing = problem ?? (faceExcluded.Count == 0 ? "Untick at least one bone." : AnimationBones.Problem(clip, faceExcluded));
        using (ImRaii.Disabled(service.Busy || ReadOnly || excludeMissing != null))
        {
            if (ImGui.Button("Save the face without them"))
                faceJob = StartFaceEdit(service, new AnimationBakeRequest(Guid.NewGuid(), capture, destination, modName.Trim(), false,
                    ImmutableHashSet<PoseBoneId>.Empty, PoseComponents.None, AnimationOperation.ExcludeBones,
                    ExcludedBones: [.. faceExcluded.Order(StringComparer.Ordinal)]));
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(excludeMissing ?? "Write the face without the unticked bones.");
        DrawFaceResult(service);
        var row = ImGui.GetFrameHeightWithSpacing();
        using var list = ImRaii.Child("##face-bones", new Vector2(0, Math.Max(row * 3, ImGui.GetContentRegionAvail().Y - Theme.Scaled(4))), true);
        if (!list.Success)
            return;
        using (ImRaii.Disabled(service.Busy))
            foreach (var name in bones)
            {
                using var id = ImRaii.PushId(name);
                var kept = !faceExcluded.Contains(name);
                if (ImGui.Checkbox(AnimationPresentation.BoneName(name), ref kept))
                {
                    if (kept) faceExcluded.Remove(name);
                    else faceExcluded.Add(name);
                }
            }
    }

    /// <summary>What stops any face edit: an unknown skeleton, an unwritable file in place, a bad mod name.</summary>
    private string? FaceProblem(AnimationCapture capture, AnimationClip clip)
    {
        if (capture.UnavailableReason is { } unavailable)
            return unavailable;
        if (clip.Resolution is not { State: SkeletonResolutionState.Matched, Selected: not null })
            return clip.Resolution?.Reason ?? "Choose the skeleton the face was made for under Source first.";
        if (destination == AnimationDestination.InPlace && !capture.Sources.Any(s => s.GamePath == clip.GamePath && AnimationResources.CanReplace(s)))
            return "The face has no writable Penumbra source. Choose Create new mod.";
        if (destination == AnimationDestination.NewMod && !Services.PenumbraService.IsSafeNewModName(modName.Trim()))
            return "Choose a valid mod name.";
        return null;
    }

    private Guid StartFaceEdit(AnimationEditService service, AnimationBakeRequest request)
    {
        StopPreview();
        service.Edit(request);
        return request.Id;
    }

    /// <summary>The running face edit's status, or how the last one ended.</summary>
    private void DrawFaceResult(AnimationEditService service)
    {
        if (faceJob == null)
            return;
        if (service.Busy)
        {
            Widgets.Spinner();
            ImGui.SameLine(0, Theme.Gap);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Muted, service.Status);
            return;
        }
        // A rebake reports only through the service's status, which stays until the next job.
        using var wrap = ImRaii.TextWrapPos(0f);
        ImGui.TextColored(service.LastFailed ? Theme.Warning : Theme.Success, service.Status);
    }
}
