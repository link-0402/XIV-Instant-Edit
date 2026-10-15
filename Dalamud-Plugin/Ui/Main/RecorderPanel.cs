using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using InstantEdit.Services.Animations;

namespace InstantEdit.Ui.Main;

/// <summary>
/// Record live pose: records a character's live skeleton and sends it to Blender. Drawn in the Timeline
/// window's Record page, and on the Animations tab while the animation services are missing.
/// </summary>
internal sealed class RecorderPanel
{
    private sealed record RecorderMessage(string Text, FeedbackSeverity Severity);

    private readonly Configuration _config;
    private readonly Action _saveConfig;
    private readonly IPluginLog _log;
    private readonly StatusFeed _feed;
    private readonly AnimationDelivery _delivery;
    private readonly CancellationToken _lifetime;
    private RecordingSubject recordingSubject = RecordingSubject.Self;
    private CancellationTokenSource? recordingCancellation;
    private volatile AnimationTake? lastRecording;
    private volatile RecorderMessage? recorderMessage;
    private int recorderSending;

    public RecorderPanel(Configuration config, Action saveConfig, IPluginLog log, StatusFeed feed, AnimationDelivery delivery, CancellationToken lifetime)
    {
        _config = config;
        _saveConfig = saveConfig;
        _log = log;
        _feed = feed;
        _delivery = delivery;
        _lifetime = lifetime;
    }

    /// <summary> The live pose recorder, once the plugin has started it. </summary>
    public AnimationRecorder? Recorder { get; set; }

    /// <summary> The Record live pose tab: records a character's live skeleton and sends it to Blender. </summary>
    public void Draw()
    {
        var active = Recorder;
        if (active == null)
            return;
        Widgets.HintWrapped($"Records a character's live skeleton, including the game's bone physics and LivePose, " +
                            $"and keys it onto \"{_delivery.AnimationArmature}\" in Blender to test clothing against it. " +
                            "Customize+ is paused on the character while it records; MagicFit adds it in Blender.");
        ImGui.Spacing();
        var progress = active.Progress;
        var sending = Volatile.Read(ref recorderSending) != 0;
        using (ImRaii.Disabled(progress != null || sending))
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("Character");
            ImGui.SameLine();
            if (ImGui.RadioButton("You", recordingSubject == RecordingSubject.Self)) recordingSubject = RecordingSubject.Self;
            ImGui.SameLine();
            if (ImGui.RadioButton("Target", recordingSubject == RecordingSubject.Target)) recordingSubject = RecordingSubject.Target;
            ImGui.SameLine();
            Widgets.HelpTip("In GPose, You is your posed copy and Target is the GPose target, " +
                            "such as an actor that Brio plays a walk on.");

            var seconds = _config.RecordingSeconds;
            ImGui.SetNextItemWidth(Theme.Scaled(150));
            if (ImGui.SliderFloat("Length", ref seconds, AnimationRecorder.MinimumSeconds, AnimationRecorder.MaximumSeconds, "%.1f s"))
                _config.RecordingSeconds = Math.Clamp(seconds, AnimationRecorder.MinimumSeconds, AnimationRecorder.MaximumSeconds);
            if (ImGui.IsItemDeactivatedAfterEdit()) _saveConfig();
            // The Timeline window's side panel is narrow: the countdown goes below the length there.
            if (ImGui.GetContentRegionAvail().X > Theme.Scaled(460))
                ImGui.SameLine(0, Theme.Scaled(16));
            var delay = _config.RecordingDelaySeconds;
            ImGui.SetNextItemWidth(Theme.Scaled(150));
            if (ImGui.SliderFloat("Countdown", ref delay, 0, AnimationRecorder.MaximumDelaySeconds, "%.0f s"))
                _config.RecordingDelaySeconds = Math.Clamp(delay, 0, AnimationRecorder.MaximumDelaySeconds);
            if (ImGui.IsItemDeactivatedAfterEdit()) _saveConfig();
            ImGui.SameLine();
            Widgets.HelpTip("Time to start walking before the recording begins, so it captures steady motion.");
        }

        if (progress is { } running)
        {
            var width = Math.Min(Theme.Scaled(360), ImGui.GetContentRegionAvail().X);
            if (running.Waiting)
                // Past the countdown it still waits a few frames for Customize+ to pause.
                ImGui.ProgressBar(0, new Vector2(width, 0), $"Recording starts in {Math.Max(0, Math.Ceiling(-running.Elapsed)):0} s");
            else
                ImGui.ProgressBar((float)Math.Clamp(running.Elapsed / running.Duration, 0, 1), new Vector2(width, 0),
                    $"Recording {running.Character}: {running.Elapsed:0.0} / {running.Duration:0.0} s");
            ImGui.SameLine();
            if (ImGui.Button("Stop")) active.Stop();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("End the recording now and send what it has.");
            ImGui.SameLine();
            if (ImGui.Button("Cancel##recording")) recordingCancellation?.Cancel();
        }
        else
        {
            using (ImRaii.Disabled(sending))
            {
                if (ImGui.Button($"Record {_config.RecordingSeconds:0.#} s")) StartRecording(active);
            }
            if (lastRecording is { } previous)
            {
                ImGui.SameLine();
                using (ImRaii.Disabled(sending))
                {
                    if (ImGui.Button("Send last recording again"))
                        _ = Task.Run(() => SendRecordingAsync(previous, _lifetime));
                }
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip($"{previous.Name}: {previous.FrameCount} frames of {previous.Bones.Length} bones.");
            }
        }

        if (recorderMessage is { } message)
        {
            var (accent, _, icon) = Theme.Severity(message.Severity);
            ImGui.TextColored(accent, icon);
            ImGui.SameLine(0, Theme.Gap);
            using var wrap = ImRaii.TextWrapPos(0f);
            ImGui.TextUnformatted(message.Text);
        }
    }

    private void StartRecording(AnimationRecorder active)
    {
        if (active.Active) return;
        recordingCancellation?.Dispose();
        recordingCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        var token = recordingCancellation.Token;
        var subject = recordingSubject;
        var seconds = _config.RecordingSeconds;
        var delay = _config.RecordingDelaySeconds;
        recorderMessage = null;
        _ = Task.Run(async () =>
        {
            AnimationTake take;
            try
            {
                take = await active.RecordAsync(subject, seconds, delay, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                ReportRecorder("Recording cancelled.", FeedbackSeverity.Warning);
                return;
            }
            catch (Exception e)
            {
                _log.Warning(e, "Live pose recording failed.");
                ReportRecorder("Recording failed: " + e.Message, FeedbackSeverity.Error);
                return;
            }
            lastRecording = take;
            await SendRecordingAsync(take, token).ConfigureAwait(false);
        });
    }

    private async Task SendRecordingAsync(AnimationTake take, CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref recorderSending, 1, 0) != 0) return;
        try
        {
            recorderMessage = new RecorderMessage($"Sending {take.FrameCount} frames of {take.Bones.Length} bones to Blender", FeedbackSeverity.Info);
            var result = await _delivery.DeliverAnimationAsync(take, token).ConfigureAwait(false);
            var warning = RecordingWarning(take);
            ReportRecorder(warning == null ? result : $"{result} {warning}",
                warning == null ? FeedbackSeverity.Success : FeedbackSeverity.Warning);
        }
        catch (OperationCanceledException)
        {
            ReportRecorder("Sending the recording was cancelled.", FeedbackSeverity.Warning);
        }
        catch (Exception e)
        {
            ReportRecorder(e.Message, FeedbackSeverity.Error);
        }
        finally
        {
            Volatile.Write(ref recorderSending, 0);
        }
    }

    private void ReportRecorder(string text, FeedbackSeverity severity)
    {
        recorderMessage = new RecorderMessage(text, severity);
        _feed.Report(StatusChannel.Animations, severity, text);
    }

    /// <summary>
    /// What a recording carries that the user should know: Customize+ could not be paused, or
    /// something else scaled the body while it recorded. Null when neither.
    /// </summary>
    public string? RecordingWarning(AnimationTake take)
    {
        if (take.Source.TryGetValue(AnimationRecorder.CustomizePlusSource, out var customizePlus) &&
            customizePlus != AnimationRecorder.CustomizePlusPaused)
            return $"{customizePlus}, so the recording includes its changes.";
        return take.Source.TryGetValue(AnimationRecorder.ScaledBonesSource, out var scaled)
            ? RecordingScale.Warning(scaled, _config.AnimationKeyScale)
            : null;
    }
}
