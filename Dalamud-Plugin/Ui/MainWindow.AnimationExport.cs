using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Models;
using InstantEdit.Services;
using InstantEdit.Services.Animations;

namespace InstantEdit.Ui;

/// <summary>
/// Sends animations to Blender, keyed onto a scene armature as an action: a clip's animation
/// file from the Animations list, or a recording of a character's live skeleton.
/// </summary>
public sealed partial class MainWindow
{
    private sealed record RecorderMessage(string Text, FeedbackSeverity Severity);

    private AnimationRecorder? recorder;
    private RecordingSubject recordingSubject = RecordingSubject.Self;
    private CancellationTokenSource? recordingCancellation;
    private volatile AnimationTake? lastRecording;
    private volatile RecorderMessage? recorderMessage;
    private int recorderSending;

    internal void AttachRecorder(AnimationRecorder? value) => recorder = value;

    private string AnimationArmature =>
        string.IsNullOrWhiteSpace(_config.AnimationArmatureName) ? "Skeleton" : _config.AnimationArmatureName.Trim();

    /// <summary> The Record live pose tab: records a character's live skeleton and sends it to Blender. </summary>
    private void DrawRecorder()
    {
        var active = recorder;
        if (active == null)
            return;
        Widgets.HintWrapped($"Records a character's live skeleton, including the game's bone physics and LivePose, " +
                            $"and keys it onto \"{AnimationArmature}\" in Blender to test clothing against it. " +
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
            if (ImGui.RadioButton("Current target", recordingSubject == RecordingSubject.Target)) recordingSubject = RecordingSubject.Target;
            ImGui.SameLine();
            Widgets.HelpTip("In GPose, You is your posed copy and Current target is the GPose target, " +
                            "such as an actor that Brio plays a walk on.");

            var seconds = _config.RecordingSeconds;
            ImGui.SetNextItemWidth(Theme.Scaled(150));
            if (ImGui.SliderFloat("Length", ref seconds, AnimationRecorder.MinimumSeconds, AnimationRecorder.MaximumSeconds, "%.1f s"))
                _config.RecordingSeconds = Math.Clamp(seconds, AnimationRecorder.MinimumSeconds, AnimationRecorder.MaximumSeconds);
            if (ImGui.IsItemDeactivatedAfterEdit()) _saveConfig();
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
                        _ = Task.Run(() => SendRecordingAsync(previous, _lifetimeCts.Token));
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
        recordingCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
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
            var result = await DeliverAnimationAsync(take, token).ConfigureAwait(false);
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
    private string? RecordingWarning(AnimationTake take)
    {
        if (take.Source.TryGetValue(AnimationRecorder.CustomizePlusSource, out var customizePlus) &&
            customizePlus != AnimationRecorder.CustomizePlusPaused)
            return $"{customizePlus}, so the recording includes its changes.";
        return take.Source.TryGetValue(AnimationRecorder.ScaledBonesSource, out var scaled)
            ? RecordingScale.Warning(scaled, _config.AnimationKeyScale)
            : null;
    }

    /// <summary>Encodes a take and sends it to Blender. Returns what Blender did with it, or throws with a user-facing message.</summary>
    private Task<string> DeliverAnimationAsync(AnimationTake take, CancellationToken token)
        => DeliverTakeAsync(take, null, token);

    /// <summary>
    /// Encodes a take and sends it to Blender, onto the armature of the character send
    /// <paramref name="targetCharacter"/> names, else the configured armature. Returns what Blender
    /// did with it, or throws with a user-facing message.
    /// </summary>
    private async Task<string> DeliverTakeAsync(AnimationTake take, string? targetCharacter, CancellationToken token)
    {
        var port = _config.BlenderPort;
        var status = await CheckBlenderStatusAsync(port, token).ConfigureAwait(false);
        if (!status.Reachable)
            throw new InvalidOperationException("Blender is offline. Start Blender with the XIV Instant Edit add-on, then send the animation again.");
        if (status.Classify(_pluginVersion) != BlenderConnectionState.Online)
            throw new InvalidOperationException(BlenderClient.VersionMismatchMessage(_pluginVersion));
        if (!await _blender.SupportsAnimationImportAsync(port, token).ConfigureAwait(false))
            throw new InvalidOperationException("The XIV Instant Edit add-on is too old to receive animations. Update the add-on and restart Blender.");
        var armature = AnimationArmature;
        var keyScale = _config.AnimationKeyScale;
        var body = await Task.Run(() => AnimationTakeFormat.Write(take, armature, keyScale, _pluginVersion, targetCharacter), token)
            .ConfigureAwait(false);
        try
        {
            return (await _blender.SendAnimationAsync(port, body, token).ConfigureAwait(false)).Describe();
        }
        catch (BlenderBridgeException e)
        {
            throw new InvalidOperationException(e.Failure.UserMessage);
        }
    }

    private void DrawAnimationExportOptions()
    {
        var armature = _config.AnimationArmatureName;
        ImGui.SetNextItemWidth(Math.Max(Theme.Scaled(180), ImGui.GetContentRegionAvail().X * 0.45f));
        if (ImGui.InputText("Blender armature", ref armature, 128))
        {
            _config.AnimationArmatureName = armature;
            _saveConfig();
        }
        Widgets.HintWrapped("Animations become a new action on this armature. Without an object of this name, " +
                            "Blender uses the active armature, or the scene's only one.");
        var keyScale = _config.AnimationKeyScale;
        if (ImGui.Checkbox("Key bone scale", ref keyScale))
        {
            _config.AnimationKeyScale = keyScale;
            _saveConfig();
        }
        Widgets.HintWrapped("On keys bone scale too, and sets bones under an unevenly scaled bone to inherit scale " +
                            "Aligned, the way the game scales them. Recordings leave Customize+ out, since MagicFit " +
                            "adds it. Off leaves bone scaling applied in Blender in place.");
    }
}
