using System.Collections.Immutable;
using System.Globalization;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Models;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Ui;

/// <summary> The Timeline tab: a few facts about the selected clip and the button that opens it in the Timeline window. </summary>
public sealed partial class MainWindow
{
    private Action<AnimationCapture, bool>? openTimeline;
    private Task<ImmutableArray<TimelineEffectCheck.Problem>>? effectCheck;
    private CancellationTokenSource? effectCheckCancel;
    private string? effectCheckKey;

    internal void AttachTimeline(Action<AnimationCapture, bool>? open) => openTimeline = open;

    /// <summary>
    /// Checks the selected clip's effects on a task, once per clip and file version, so the tab can
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

    private void DrawAnimationTimeline(AnimationCapture capture)
    {
        var startup = animationStartupSelected && capture.Startup is not null;
        var clip = startup ? capture.Startup! : capture.Clip;
        var file = capture.Sources.FirstOrDefault(s => s.GamePath == clip.GamePath);
        var problems = EffectProblems(capture, startup, clip, file);
        var expression = !startup ? capture.ExpressionTimeline : null;
        Widgets.HintWrapped(expression != null
            ? "Place sounds, effects and faces on the expression, keep its face on and preview it in GPose."
            : "Place sounds, expressions, effects and props on the animation, change its length and preview it in GPose.");
        ImGui.Spacing();
        using (var facts = ImRaii.Table("##timeline-facts", 2, ImGuiTableFlags.SizingFixedFit))
        {
            if (facts.Success)
            {
                SetupFacts();
                Fact(expression != null ? "Face length" : "Length");
                ImGui.TextUnformatted(clip.Duration > 0 ? clip.Duration.ToString("0.##", CultureInfo.InvariantCulture) + "s" : "Unknown");
                // An expression's timeline is a file of its own, often from another mod than its face.
                var timelineFile = expression ?? file;
                Fact("Mod");
                ImGui.TextUnformatted(timelineFile?.ModName ?? timelineFile?.ModDirectory ?? "Game Data");
            }
        }
        if (problems is { IsEmpty: false } found && TimelineWindowViews.EffectLoopWarning(found) is { } warning)
        {
            ImGui.Spacing();
            using var wrap = ImRaii.TextWrapPos(0f);
            var one = found.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1;
            ImGui.TextColored(Theme.Warning, $"⚠ {warning} Open the timeline to keep {(one ? "it" : "them")} looping.");
        }
        ImGui.Spacing();
        using (ImRaii.Disabled(openTimeline is null))
        {
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Film, "Open timeline", null, null, null, null))
                openTimeline?.Invoke(capture, startup);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(openTimeline is null ? "The timeline editor is unavailable." : "Open this animation in the Timeline window.");
    }
}
