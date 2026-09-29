using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Models;
using InstantEdit.Services.Animations;

namespace InstantEdit.Ui;

/// <summary>
/// The "Send animation to Blender" dialog behind the pen of an animation row: a PAP file from the
/// Mod Browser, whose clip and source skeleton are chosen here, or an animation in the Animations
/// tab, which the listener detected on your character and whose skeleton it already matched.
/// </summary>
public sealed partial class MainWindow
{
    private const string AnimationSendPopup = "Send animation to Blender";

    private sealed class AnimationSendState(CancellationToken lifetime) : IDisposable
    {
        private readonly CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        public CancellationToken Token => cancellation.Token;
        public AnimationFileSource? File { get; init; }
        public string? CaptureId { get; init; }
        public bool Startup { get; init; }
        public Task<AnimationFileInfo>? Inspection { get; set; }
        public int Clip { get; set; }
        public AnimationFileClip? ResolvedClip { get; set; }
        public Task<SkeletonResolution>? Resolution { get; set; }
        public SkeletonCandidate? Chosen { get; set; }
        public bool Sent { get; set; }

        public void Dispose()
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
    }

    private AnimationSendState? animationSend;
    // Rows of the Animations tab and the detected animation each stands for.
    private readonly Dictionary<ResourceView, (string CaptureId, bool Startup)> _animationRows = new(ReferenceEqualityComparer.Instance);

    /// <summary> Why an animation row cannot be sent, or null when it can. </summary>
    private string? AnimationSendBlock(ResourceView node)
    {
        if (animations is null)
            return animationError ?? "Animation integration is unavailable.";
        if (_animationRows.ContainsKey(node))
            return null;
        return node.SourceState == ResourceSourceState.LoadedMod && Path.IsPathRooted(node.ActualPath) &&
               node.ActualPath.EndsWith(".pap", StringComparison.OrdinalIgnoreCase)
            ? null
            : "Only animation files inside a mod folder can be sent from here.";
    }

    private void OpenAnimationSend(ResourceView node)
    {
        if (AnimationSendBlock(node) is { } blocked)
        {
            SetStatus(blocked, FeedbackSeverity.Warning);
            return;
        }
        animationSend?.Dispose();
        if (_animationRows.TryGetValue(node, out var detected))
        {
            animationSend = new AnimationSendState(_lifetimeCts.Token) { CaptureId = detected.CaptureId, Startup = detected.Startup };
            return;
        }
        var source = new AnimationFileSource(node.GamePath, node.ActualPath, Safe(node.DisplayName, Path.GetFileName(node.ActualPath)),
            node.SourceModName.Length > 0 ? node.SourceModName : null, node.SourceModDirectory.Length > 0 ? node.SourceModDirectory : null);
        var state = new AnimationSendState(_lifetimeCts.Token) { File = source };
        state.Inspection = animations!.Files.InspectAsync(source, state.Token);
        animationSend = state;
    }

    private void CloseAnimationSend()
    {
        animationSend?.Dispose();
        animationSend = null;
        ImGui.CloseCurrentPopup();
    }

    private void DrawAnimationSendDialog()
    {
        var state = animationSend;
        if (state is null)
            return;
        if (!ImGui.IsPopupOpen(AnimationSendPopup))
            ImGui.OpenPopup(AnimationSendPopup);
        ImGui.SetNextWindowSizeConstraints(Theme.Scaled(480, 0), Theme.Scaled(760, 600));
        if (!ImGui.BeginPopupModal(AnimationSendPopup, ImGuiWindowFlags.AlwaysAutoResize))
            return;
        try
        {
            var service = animations;
            if (service is null)
            {
                Widgets.Banner("##animation-send-unavailable", FeedbackSeverity.Error, animationError ?? "Animation integration is unavailable.");
                if (ImGui.Button("Close")) CloseAnimationSend();
                return;
            }
            if (state.File is not null)
                DrawFileSend(state, service);
            else
                DrawDetectedSend(state, service);
        }
        finally
        {
            ImGui.EndPopup();
        }
    }

    private void DrawFileSend(AnimationSendState state, AnimationEditService service)
    {
        var file = state.File!;
        Widgets.SectionHeader(file.DisplayName, file.ModName);
        Widgets.PathText(file.GamePath, file.FilePath);
        ImGui.Spacing();
        var inspection = state.Inspection!;
        if (!inspection.IsCompleted)
        {
            Waiting("Reading the animation file");
            DrawSendButtons(state, service, null);
            return;
        }
        if (!inspection.IsCompletedSuccessfully)
        {
            Widgets.Banner("##animation-file-error", FeedbackSeverity.Error,
                "This file could not be read as an animation: " + ErrorText(inspection.Exception));
            DrawSendButtons(state, service, null);
            return;
        }

        var clips = inspection.Result.Clips;
        state.Clip = Math.Clamp(state.Clip, 0, clips.Length - 1);
        using (ImRaii.Disabled(state.Sent && service.Busy))
        {
            ImGui.SetNextItemWidth(Math.Max(Theme.Scaled(320), ImGui.GetContentRegionAvail().X * 0.7f));
            using var combo = ImRaii.Combo("Clip", ClipLabel(clips[state.Clip]));
            if (combo.Success)
                for (var i = 0; i < clips.Length; i++)
                    if (ImGui.Selectable($"{ClipLabel(clips[i])}##clip-{i}", i == state.Clip))
                        state.Clip = i;
        }
        if (clips.Length > 1)
            Widgets.HintWrapped("This file holds several clips, such as a loop and its start. Each is sent as its own action.");
        var clip = clips[state.Clip];
        if (!ReferenceEquals(clip, state.ResolvedClip))
        {
            state.ResolvedClip = clip;
            state.Chosen = null;
            state.Resolution = service.Files.ResolveAsync(clip, state.Token);
        }

        ImGui.Spacing();
        SkeletonCandidate? skeleton = null;
        var resolution = state.Resolution!;
        if (!resolution.IsCompleted)
            Waiting("Finding the skeleton this animation was made for");
        else if (!resolution.IsCompletedSuccessfully)
            Widgets.Banner("##animation-file-skeleton-error", FeedbackSeverity.Error,
                "No skeleton could be matched: " + ErrorText(resolution.Exception));
        else
            skeleton = DrawSkeletonChoice(resolution.Result, state.Chosen, candidate => state.Chosen = candidate, state.Sent && service.Busy);

        ImGui.Spacing();
        Widgets.HintWrapped($"Blender keys the clip onto \"{AnimationArmature}\" at its own frame rate. " +
                            "Physics bones keep their rest pose; use Record live pose in the Animations tab to include them.");
        DrawSendButtons(state, service, skeleton is null ? null : () => service.SendFileToBlender(clip, skeleton, DeliverAnimationAsync));
    }

    private void DrawDetectedSend(AnimationSendState state, AnimationEditService service)
    {
        var capture = service.Observer.History.FirstOrDefault(c => c.Id == state.CaptureId);
        var clip = capture is null ? null : state.Startup ? capture.Startup : capture.Clip;
        if (capture is null || clip is null)
        {
            Widgets.Banner("##animation-detected-gone", FeedbackSeverity.Warning,
                "The listener no longer lists this animation, for example after a character change. Play it again.");
            DrawSendButtons(state, service, null);
            return;
        }
        Widgets.SectionHeader(AnimationPresentation.AnimationName(capture, state.Startup),
            capture.Playing && !state.Startup ? "playing" : "recent");
        var source = capture.Sources.FirstOrDefault(s => s.GamePath == clip.GamePath);
        Widgets.PathText(clip.GamePath, source?.ResolvedPath ?? clip.GamePath);
        ImGui.TextColored(Theme.Muted, "Source: " + (source?.ModName ?? source?.ModDirectory ?? "Vanilla game"));
        ImGui.Spacing();

        SkeletonCandidate? skeleton = null;
        switch (clip.Resolution)
        {
            case null or { State: SkeletonResolutionState.Searching }:
                Waiting(clip.Resolution?.Reason ?? "Finding the skeleton this animation was made for");
                break;
            case { State: SkeletonResolutionState.Incompatible } incompatible:
                Widgets.Banner("##animation-detected-incompatible", FeedbackSeverity.Warning,
                    incompatible.Reason ?? "No compatible source skeleton was found. Rebuild the skeleton library in Settings after installing skeleton mods.");
                break;
            case var resolution:
                skeleton = DrawSkeletonChoice(resolution, null, candidate =>
                {
                    try { service.Observer.ChooseSkeleton(clip, candidate); }
                    catch (InvalidOperationException e) { SetStatus(e.Message, FeedbackSeverity.Warning); }
                }, state.Sent && service.Busy);
                break;
        }
        ImGui.Spacing();
        Widgets.HintWrapped($"Blender keys the animation onto \"{AnimationArmature}\" at its own frame rate. " +
                            "Physics bones keep their rest pose; use Record live pose to include them.");
        DrawSendButtons(state, service, skeleton is null ? null : () => service.SendToBlender(capture, state.Startup, DeliverAnimationAsync));
    }

    /// <summary>
    /// The matched skeleton, or a choice among equally good candidates. Returns the skeleton to
    /// sample on, or null while there is none.
    /// </summary>
    private static SkeletonCandidate? DrawSkeletonChoice(SkeletonResolution resolution, SkeletonCandidate? chosen,
        Action<SkeletonCandidate> choose, bool locked)
    {
        if (resolution.State == SkeletonResolutionState.Incompatible || resolution.Candidates.IsDefaultOrEmpty)
        {
            Widgets.Banner("##animation-skeleton-incompatible", FeedbackSeverity.Warning,
                resolution.Reason ?? "No installed skeleton fits this animation.");
            return null;
        }
        var selected = chosen ?? resolution.Selected;
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Skeleton");
        ImGui.SameLine(0, Theme.Gap);
        if (resolution.Candidates.Length == 1)
        {
            var only = resolution.Candidates[0];
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Label, SkeletonLabel(only));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(only.Rationale + "\n" + only.Source.Resource.ResolvedPath);
            return only;
        }
        using (ImRaii.Disabled(locked))
        {
            ImGui.SetNextItemWidth(Math.Max(Theme.Scaled(320), ImGui.GetContentRegionAvail().X));
            using var combo = ImRaii.Combo("##animation-skeleton", selected is null ? "Choose the skeleton it was made for" : SkeletonLabel(selected));
            if (combo.Success)
                foreach (var candidate in resolution.Candidates)
                {
                    if (ImGui.Selectable($"{SkeletonLabel(candidate)}##{AnimationSkeletonIndex.SelectionId(candidate)}", candidate == selected))
                        choose(candidate);
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(candidate.Rationale + "\n" + candidate.Source.Resource.ResolvedPath);
                }
        }
        if (selected is null)
            Widgets.HintWrapped(resolution.Reason ?? "Several skeletons fit. Choose the one this animation was made for.");
        return selected;
    }

    private static string SkeletonLabel(SkeletonCandidate candidate)
    {
        var provider = candidate.Source.Resource.ModName ?? candidate.Source.Resource.ModDirectory ??
                       (candidate.Source.Kind == SkeletonSourceKind.Game ? "Vanilla game" : "Current collection");
        var bones = candidate.Source.LeadingBones > 0 ? $"its first {candidate.Skeleton.Bones.Length} bones" : $"{candidate.Skeleton.Bones.Length} bones";
        return $"{AnimationPresentation.SourceModelName(candidate)} · {provider} · {bones}";
    }

    private static string ClipLabel(AnimationFileClip clip)
        => $"{clip.Name} · {clip.Duration:0.##} s{(clip.Face ? " · face" : "")}";

    private void DrawSendButtons(AnimationSendState state, AnimationEditService service, Action? send)
    {
        ImGui.Spacing();
        if (state.Sent)
        {
            if (service.Busy)
            {
                Widgets.Spinner();
                ImGui.SameLine();
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(service.Status);
            }
            else
            {
                var (accent, _, icon) = Theme.Severity(service.LastFailed ? FeedbackSeverity.Error : FeedbackSeverity.Success);
                ImGui.TextColored(accent, icon);
                ImGui.SameLine(0, Theme.Gap);
                using var wrap = ImRaii.TextWrapPos(ImGui.GetCursorPosX() + Theme.Scaled(560));
                ImGui.TextUnformatted(service.Status);
            }
            ImGui.Spacing();
        }
        var blocked = send is null || service.Busy;
        using (ImRaii.Disabled(blocked))
        {
            if (ImGui.Button(state.Sent ? "Send again" : "Send to Blender") && send is not null)
            {
                state.Sent = true;
                send();
            }
        }
        if (service.Busy && !state.Sent && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Another animation task is running.");
        ImGui.SameLine();
        if (ImGui.Button("Close"))
            CloseAnimationSend();
    }

    private static void Waiting(string text)
    {
        Widgets.Spinner();
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, text);
    }

    private static string ErrorText(AggregateException? error)
        => error?.GetBaseException().Message ?? "Unknown error.";
}
