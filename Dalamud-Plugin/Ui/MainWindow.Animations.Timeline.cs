using System.Collections.Immutable;
using System.Globalization;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Ui;

/// <summary>
/// The Animations tab's timeline section: the selected clip, a few facts about it and the button that
/// opens it in the Timeline window, where every tool works. With nothing selected it opens the window's
/// loader, which plays any animation on the GPose character.
/// </summary>
public sealed partial class MainWindow
{
    private Action<AnimationCapture, bool>? openTimeline;
    // Opens the Timeline window as it is, with its loader when no clip is open.
    private Action? showTimeline;
    // Whether the Timeline window keeps the listener running.
    private Func<bool>? timelineListens;
    private Task<ImmutableArray<TimelineEffectCheck.Problem>>? effectCheck;
    private CancellationTokenSource? effectCheckCancel;
    private string? effectCheckKey;

    internal void AttachTimeline(Action<AnimationCapture, bool>? open, Action? show = null, Func<bool>? listens = null)
    {
        openTimeline = open;
        showTimeline = show;
        timelineListens = listens;
    }

    /// <summary>Whether this window keeps the listener running: its Animations tab is shown, or Send my character waits for it.</summary>
    internal bool ListensForAnimations => IsOpen && (_activeTab == MainTab.Animations || _activeTab == MainTab.QuickActions && CharacterSendListens);

    /// <summary>
    /// Checks the selected clip's effects on a task, once per clip and file version, so the section can
    /// say when a looping effect stops after its first loop before the timeline is opened.
    /// </summary>
    private ImmutableArray<TimelineEffectCheck.Problem>? EffectProblems(AnimationCapture capture, bool startup, AnimationClip clip,
        AnimationResource? file)
    {
        if (animations is not { } service)
            return [];
        var key = $"{capture.Id}|{startup}|{clip.GamePath}|{file?.Hash}|{capture.ExpressionTimeline?.Hash}";
        if (key != effectCheckKey)
        {
            effectCheckCancel?.Cancel();
            effectCheckCancel?.Dispose();
            effectCheckCancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            var token = effectCheckCancel.Token;
            effectCheckKey = key;
            effectCheck = Task.Run(async () =>
            {
                var source = await service.LoadTimelineAsync(capture, startup, token);
                return source.Document is { } doc ? source.EffectProblems(doc) : [];
            }, token);
        }
        return effectCheck is { IsCompletedSuccessfully: true } done ? done.Result : null;
    }

    /// <summary>How tall the section below the list is drawn, so the list leaves it its room.</summary>
    private static float TimelineSectionHeight() => ImGui.GetFrameHeightWithSpacing() * 6 + ImGui.GetTextLineHeightWithSpacing() * 2;

    private void DrawAnimationTimeline()
    {
        if (animations is { } service && TimelineWindow.RecoveryAttention(service) is var attention and > 0)
        {
            Widgets.Banner("##animation-recovery", FeedbackSeverity.Warning, attention == 1
                ? "An animation edit did not finish. Undo it under Edits in the Timeline window."
                : $"{attention} animation edits did not finish. Undo them under Edits in the Timeline window.");
            ImGui.Spacing();
        }
        if (animationSelection is not { } capture)
        {
            Widgets.EmptyState(FontAwesomeIcon.Film, "Select an animation",
                "Choose one above to edit it in the timeline, or open the timeline to load any animation in GPose.");
            ImGui.Spacing();
            DrawOpenTimeline(null, false);
            return;
        }
        var startup = animationStartupSelected && capture.Startup is not null;
        var clip = startup ? capture.Startup! : capture.Clip;
        var file = capture.Sources.FirstOrDefault(s => s.GamePath == clip.GamePath);
        var expression = !startup ? capture.ExpressionTimeline : null;
        DrawSelectedAnimationTitle(capture);
        // An expression's timeline is a file of its own, often from another mod than its face.
        var timelineFile = expression ?? file;
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, (expression != null ? "Face length " : "Length ") +
                                       (clip.Duration > 0 ? clip.Duration.ToString("0.##", CultureInfo.InvariantCulture) + "s" : "unknown") +
                                       " · " + (timelineFile?.ModName ?? timelineFile?.ModDirectory ?? "Game Data"));
        if (SkeletonBlock(clip) is { } problem)
        {
            using var wrap = ImRaii.TextWrapPos(0f);
            ImGui.TextColored(Theme.Hint, clip.Resolution?.State == SkeletonResolutionState.Ambiguous
                ? "Several skeletons fit. Choose the one it was made for under Source in the timeline."
                : problem);
        }
        if (EffectProblems(capture, startup, clip, file) is { IsEmpty: false } found && TimelineWindowViews.EffectLoopWarning(found) is { } warning)
        {
            using var wrap = ImRaii.TextWrapPos(0f);
            var one = found.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1;
            ImGui.TextColored(Theme.Warning, $"⚠ {warning} Open the timeline to keep {(one ? "it" : "them")} looping.");
        }
        ImGui.Spacing();
        DrawOpenTimeline(capture, startup);
        ImGui.SameLine(0, Theme.Gap);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Hint, expression != null
            ? "Sounds, effects and faces, a kept face, and a preview in GPose."
            : "Events, length, bone layers, LivePose, animated bones, skeleton repair and a preview in GPose.");
    }

    /// <summary>Names the animation the section is about.</summary>
    private void DrawSelectedAnimationTitle(AnimationCapture capture)
    {
        var playing = capture.Playing && !animationStartupSelected ? " · playing" : "";
        Widgets.Icon(AnimationIcon(capture), Theme.AnimationKind);
        ImGui.SameLine(0, Theme.Gap);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Label, AnimationPresentation.AnimationName(capture, animationStartupSelected));
        ImGui.SameLine(0, Theme.Gap);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, AnimationPresentation.AnimationState(capture, animationStartupSelected) + playing);
    }

    /// <summary>Open timeline: the selected clip's, or with none the Timeline window and its loader.</summary>
    private void DrawOpenTimeline(AnimationCapture? capture, bool startup)
    {
        var available = capture is null ? showTimeline is not null : openTimeline is not null;
        using (ImRaii.Disabled(!available))
        {
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Film, "Open timeline", null, null, null, null))
            {
                if (capture is null)
                    showTimeline?.Invoke();
                else
                    openTimeline?.Invoke(capture, startup);
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(!available ? "The timeline editor is unavailable."
                : capture is null ? "Open the Timeline window to load an animation."
                : "Open this animation in the Timeline window.");
    }
}
