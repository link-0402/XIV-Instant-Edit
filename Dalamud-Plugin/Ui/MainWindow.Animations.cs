using System.Collections.Immutable;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using InstantEdit.Models;
using InstantEdit.Services.Animations;

namespace InstantEdit.Ui;

public sealed partial class MainWindow
{
    private AnimationEditService? animations;
    private string? animationError;
    private AnimationCapture? animationSelection;
    private readonly HashSet<PoseBoneId> animationBones = [];
    private PoseComponents animationComponents = PoseComponents.All;
    private AnimationDestination animationDestination = AnimationDestination.NewMod;
    private bool animationStartup;
    private string animationModName = "";

    internal void AttachAnimations(AnimationEditService? service, string? error)
    {
        animations = service;
        animationError = error;
    }
    public override void OnOpen() { }
    public override void OnClose() => animations?.StopObservation();

    private void SelectAnimation(AnimationCapture capture, bool startup = false)
    {
        animationSelection = capture;
        animationBones.Clear();
        animationBones.UnionWith(capture.Pose.Bones.Where(b => b.Id.Partial == capture.Clip.Partial && b.Id.Slot == 0).Select(b => b.Id));
        animationComponents = PoseComponents.All;
        animationStartup = startup && capture.Startup != null;
        animationModName = AnimationPresentation.DefaultModName(capture);
    }

    private void DrawAnimations()
    {
        if (animations == null) { ImGui.TextWrapped(animationError ?? "Animation integration is unavailable."); return; }
        animations.StartObservation();
        ImGui.Spacing();
        var history = animations.Observer.History;
        if (animationSelection is { } previous && history.FirstOrDefault(c => c.Id == previous.Id) is { } currentCapture &&
            currentCapture.Clip.BindingFingerprint == previous.Clip.BindingFingerprint && currentCapture.Clip.SourceContext == previous.Clip.SourceContext &&
            currentCapture.Clip.SkeletonFingerprint == previous.Clip.SkeletonFingerprint)
        {
            // Resolution updates must not recapture offsets or change the user's selected bones.
            animationSelection = previous with { Clip = currentCapture.Clip, Startup = currentCapture.Startup, Playing = currentCapture.Playing };
        }
        if (animationSelection != null && animationSelection.ActorId != animations.Observer.Actor)
        { animationSelection = null; animationBones.Clear(); }
        if (animationSelection is { Playing: false } staleSelection && !AnimationPresentation.Ready(staleSelection))
        { animationSelection = null; animationBones.Clear(); }

        var entries = AnimationPresentation.ListItems(history);
        var width = Math.Max(230, Math.Min(340, ImGui.GetContentRegionAvail().X * 0.34f));
        if (ImGui.BeginChild("##character-animations", new Vector2(width, 330), true))
        {
            ImGui.TextUnformatted("Character Animations");
            ImGui.Separator();
            if (entries.IsEmpty) ImGui.TextWrapped("No ready character animations are available yet.");
            foreach (var entry in entries)
            {
                if (entry.SeparatorBefore) ImGui.Separator();
                var listCapture = entry.Capture;
                var label = AnimationPresentation.AnimationName(listCapture, entry.Startup);
                ImGui.PushID(listCapture.Id + (entry.Startup ? "-startup" : "-live"));
                var active = listCapture.Playing && !entry.Startup;
                if (active) ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(.35f, .9f, .45f, 1));
                if (ImGui.Selectable(label,
                    animationSelection?.Id == listCapture.Id && animationStartup == entry.Startup))
                    SelectAnimation(listCapture, entry.Startup);
                if (active) ImGui.PopStyleColor();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip((entry.Startup ? "Linked startup\n" : "") + (entry.Startup ? listCapture.Startup!.GamePath : listCapture.Clip.GamePath));
                ImGui.PopID();
            }
        }
        ImGui.EndChild(); ImGui.SameLine();
        if (ImGui.BeginChild("##animation-details", new Vector2(0, 330), true))
        {
            ImGui.TextUnformatted("Animation Details");
            ImGui.Separator();
            if (animationSelection is not { } selected)
                ImGui.TextWrapped("Select a ready animation to review its source and LivePose adjustments.");
            else DrawAnimationDetails(selected);
        }
        ImGui.EndChild();

        if (animationSelection is { } capture)
        {
            ImGui.Spacing();
            DrawLivePoseAdjustments(capture);
            ImGui.Spacing();
            DrawAnimationActions(capture);
        }
        if (animations.Busy)
        {
            ImGui.SameLine(); ImGui.BeginDisabled(!animations.CanCancel);
            if (ImGui.Button("Cancel")) animations.Cancel();
            ImGui.EndDisabled();
        }
        if (!string.IsNullOrWhiteSpace(animations.Status)) ImGui.TextWrapped(animations.Status);
    }

    private void DrawAnimationDetails(AnimationCapture capture)
    {
        var selectedClip = animationStartup && capture.Startup is { } startup ? startup : capture.Clip;
        ImGui.TextUnformatted(AnimationPresentation.AnimationName(capture, animationStartup));
        ImGui.TextDisabled(AnimationPresentation.AnimationState(capture, animationStartup));
        ImGui.TextUnformatted("Animation model: " + AnimationPresentation.ModelName(selectedClip));
        DrawAnimationFileSource(capture, selectedClip);
        DrawLiveSkeleton(capture.Clip);
        DrawAnimationSource(capture, capture.Clip, false);

        ImGui.Separator();
        if (capture.Startup == null)
            ImGui.TextDisabled("Startup animation: no linked startup could be identified.");
        else
        {
            ImGui.TextUnformatted("Startup animation: " + AnimationPresentation.AnimationName(capture, true));
            if (animationStartup || capture.Startup.Resolution?.State == SkeletonResolutionState.Ambiguous)
                DrawAnimationSource(capture, capture.Startup, true);
        }

        if (!string.IsNullOrWhiteSpace(capture.Clip.LastOperationError))
            ImGui.TextWrapped("Last attempt: " + capture.Clip.LastOperationError);
    }

    private static void DrawAnimationFileSource(AnimationCapture capture, AnimationClip clip)
    {
        var file = capture.Sources.FirstOrDefault(s => s.GamePath == clip.GamePath);
        if (file == null) return;
        ImGui.TextUnformatted("Animation file");
        ImGui.Indent();
        var mod = file.ModName ?? file.ModDirectory ?? "Original game";
        ImGui.TextUnformatted("Source mod: " + mod);
        ImGui.TextUnformatted("File: " + (file.RelativePath ?? file.GamePath));
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(file.ResolvedPath);
        ImGui.Unindent();
    }

    private static void DrawLiveSkeleton(AnimationClip clip)
    {
        ImGui.TextUnformatted("Your character");
        ImGui.Indent();
        var skeleton = clip.TargetSkeleton;
        var label = skeleton == null ? "Live skeleton unavailable" : $"{Path.GetFileName(clip.SkeletonPath)} · {skeleton.Bones.Length} bones";
        ImGui.TextUnformatted(label);
        if (ImGui.IsItemHovered() && !string.IsNullOrWhiteSpace(clip.SkeletonPath)) ImGui.SetTooltip(clip.SkeletonPath);
        ImGui.Unindent();
    }

    private void DrawAnimationSource(AnimationCapture capture, AnimationClip clip, bool startup)
    {
        var resolution = clip.Resolution;
        var source = resolution?.Selected ?? resolution?.Candidates.FirstOrDefault();
        if (source == null)
        {
            if (resolution != null) ImGui.TextDisabled(resolution.Reason ?? "Preparing the animation skeleton…");
            return;
        }
        ImGui.TextUnformatted(startup ? "Startup animation source" : "Animation source");
        ImGui.Indent();
        var provider = source.Source.Resource.ModName ?? source.Source.Resource.ModDirectory ??
            (source.Source.Kind == SkeletonSourceKind.Game ? "Original game" : "Current collection");
        ImGui.TextUnformatted($"{Path.GetFileName(source.Source.Resource.ResolvedPath)} · {source.Skeleton.Bones.Length} bones");
        ImGui.TextDisabled("Provided by " + provider);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(source.Source.Resource.ResolvedPath + "\n" + source.Rationale);
        if (source.Source.Variant.Length > 0) ImGui.TextDisabled(source.Source.Variant);
        if (resolution!.Candidates.Length > 1)
        {
            ImGui.TextDisabled("Choose the skeleton this animation was made for.");
            ImGui.BeginDisabled(animations!.Busy);
            if (ImGui.BeginCombo(startup ? "Startup animation skeleton" : "Animation skeleton",
                resolution.Selected == null ? "Choose source skeleton" : Path.GetFileName(source.Source.Resource.ResolvedPath)))
            {
                foreach (var candidate in resolution.Candidates)
                {
                    var candidateProvider = candidate.Source.Resource.ModName ?? candidate.Source.Resource.ModDirectory ??
                        (candidate.Source.Kind == SkeletonSourceKind.Game ? "Original game" : "Current collection");
                    var variant = candidate.Source.Variant.Length == 0 ? "Main skeleton" : candidate.Source.Variant;
                    var label = $"{candidateProvider} · {Path.GetFileName(candidate.Source.Resource.ResolvedPath)} · {variant} · {candidate.Skeleton.Bones.Length} bones##{AnimationSkeletonIndex.SelectionId(candidate)}";
                    if (ImGui.Selectable(label, candidate == resolution.Selected))
                    {
                        var chosen = animations.Observer.ChooseSkeleton(clip, candidate);
                        animationSelection = startup ? capture with { Startup = chosen } : capture with { Clip = chosen };
                    }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(candidate.Rationale + "\n" + candidate.Source.Resource.ResolvedPath);
                }
                ImGui.EndCombo();
            }
            ImGui.EndDisabled();
        }
        ImGui.Unindent();
    }

    private void DrawLivePoseAdjustments(AnimationCapture capture)
    {
        if (!ImGui.CollapsingHeader("Current LivePose Adjustments")) return;
        foreach (var component in new[] { PoseComponents.Position, PoseComponents.Rotation, PoseComponents.Scale })
        {
            var enabled = animationComponents.HasFlag(component);
            if (ImGui.Checkbox(component.ToString(), ref enabled))
                animationComponents = enabled ? animationComponents | component : animationComponents & ~component;
            ImGui.SameLine();
        }
        ImGui.NewLine();
        var poseUnavailable = capture.PoseUnavailableReason != null;
        ImGui.BeginDisabled(animations!.Busy || poseUnavailable);
        if (ImGui.Button("Clear all offsets")) animations.ClearOffsets(capture);
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(animations.Busy || !animations.CanReapplyOffsets || poseUnavailable);
        if (ImGui.Button("Reapply offsets")) animations.ReapplyOffsets(capture);
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(animations.Busy || poseUnavailable);
        if (ImGui.Button("Refresh offsets"))
        {
            var selected = capture;
            animations.Refresh(selected, refreshed =>
            {
                if (animationSelection?.Id != selected.Id) return;
                animationSelection = refreshed;
                animationBones.IntersectWith(refreshed.Pose.Bones.Select(b => b.Id));
            });
        }
        ImGui.EndDisabled();
        if (poseUnavailable) ImGui.TextWrapped("LivePose adjustments are unavailable: " + capture.PoseUnavailableReason);
        ImGui.Separator();
        foreach (var bone in capture.Pose.Bones.Where(b => b.Id.Slot == 0 && b.Id.Partial == capture.Clip.Partial))
        {
            ImGui.PushID(bone.Id.Name);
            var enabled = animationBones.Contains(bone.Id);
            if (ImGui.Checkbox(AnimationPresentation.BoneName(bone.Id.Name), ref enabled))
            { if (enabled) animationBones.Add(bone.Id); else animationBones.Remove(bone.Id); }
            ImGui.SameLine(); ImGui.TextDisabled($"{bone.Stacks.Length} adjustment(s)");
            ImGui.PopID();
        }
    }

    private void DrawAnimationActions(AnimationCapture capture)
    {
        if (capture.Startup is { Resolution: { State: SkeletonResolutionState.Matched, Selected: not null } })
        {
            ImGui.Checkbox("Include startup in rebake", ref animationStartup);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(AnimationPresentation.AnimationName(capture, true));
        }
        else if (capture.Startup != null)
            ImGui.TextDisabled("Startup animation is not ready to include.");

        if (ImGui.RadioButton("Create new mod", animationDestination == AnimationDestination.NewMod)) animationDestination = AnimationDestination.NewMod;
        ImGui.SameLine();
        if (ImGui.RadioButton("Replace in-place", animationDestination == AnimationDestination.InPlace)) animationDestination = AnimationDestination.InPlace;
        var clips = animationStartup && capture.Startup is { } startupClip ? new[] { capture.Clip, startupClip } : new[] { capture.Clip };
        string? blocked = capture.UnavailableReason ?? clips.Select(SkeletonBlock).FirstOrDefault(reason => reason != null);
        if (animationDestination == AnimationDestination.NewMod)
        {
            ImGui.SetNextItemWidth(Math.Min(400, ImGui.GetContentRegionAvail().X));
            ImGui.InputText("Mod name", ref animationModName, 128);
            blocked ??= capture.PackagingError;
        }
        else
        {
            var paths = new[] { capture.Clip.GamePath, animationStartup ? capture.Startup?.GamePath : null }.OfType<string>();
            if (paths.Any(p => !capture.Sources.Any(s => s.GamePath == p && AnimationResources.CanReplace(s))))
                blocked ??= "This animation has no writable Penumbra source. Choose Create new mod.";
        }
        if (blocked != null) ImGui.TextWrapped(blocked);
        var poseUnavailable = capture.PoseUnavailableReason != null;
        ImGui.BeginDisabled(animations!.Busy || blocked != null || poseUnavailable || animationBones.Count == 0 || animationComponents == PoseComponents.None);
        if (ImGui.Button("Rebake with LivePose")) animations.Edit(new AnimationBakeRequest(Guid.NewGuid(), capture, animationDestination,
            animationModName.Trim(), animationStartup, animationBones.ToImmutableHashSet(), animationComponents));
        ImGui.EndDisabled();
        ImGui.SameLine();
        string? repairBlocked = capture.UnavailableReason ?? clips.Select(SkeletonBlock).FirstOrDefault(reason => reason != null);
        if (animationDestination == AnimationDestination.NewMod) repairBlocked ??= capture.PackagingError;
        else
        {
            var paths = clips.Select(clip => clip.GamePath);
            if (paths.Any(p => !capture.Sources.Any(s => s.GamePath == p && AnimationResources.CanReplace(s))))
                repairBlocked ??= "This animation has no writable Penumbra source. Choose Create new mod.";
        }
        ImGui.BeginDisabled(animations.Busy || repairBlocked != null);
        if (ImGui.Button("Repair skeleton")) animations.Edit(new AnimationBakeRequest(Guid.NewGuid(), capture, animationDestination,
            animationModName.Trim(), animationStartup, ImmutableHashSet<PoseBoneId>.Empty, PoseComponents.None, AnimationOperation.RepairSkeleton));
        ImGui.EndDisabled();
    }

    private static string? SkeletonBlock(AnimationClip clip) => clip.Resolution is { State: not SkeletonResolutionState.Matched } resolution
        ? resolution.Reason ?? "A compatible animation source is still being identified." : null;

}
