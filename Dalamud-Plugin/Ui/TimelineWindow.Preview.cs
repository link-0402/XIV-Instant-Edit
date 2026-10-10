using System.Collections.Immutable;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui.Timeline;

namespace InstantEdit.Ui;

/// <summary>
/// The preview bar: builds the edited clip, shows it through a Penumbra temporary mod on the GPose
/// actor's collection and plays it there, with pause, speed, replay and stop. Paused, the playhead
/// scrubs the pose; playing, it follows the clip. Outside GPose it may follow the clip your
/// character plays. Every playback call runs here, on the framework thread.
/// </summary>
public sealed partial class TimelineWindow
{
    private float speedPercent = 100;
    private bool follow = true;
    private volatile string? previewFingerprint;
    private volatile int previewGeneration;
    private ushort previewTimeline;
    // The previewed timeline is an expression's: it plays on the face only.
    private bool previewFace;
    // The GPose actor whose collection got the preview; playing on another would show the unedited clip.
    private ushort? previewActor;
    private TimelineDocument? previewDocument;
    // A preview redrew the character to load its files, so ending it redraws it again.
    private volatile bool previewRedrawn;
    private bool previewJob;
    private string? previewError;
    private Task? previewStop;
    private int lastSeekFrame = -1;

    private void DrawPreviewBar(TimelineEditor current, AnimationTimelineSource loaded)
    {
        var service = animations!;
        var available = PlaybackAvailable(out var reason);
        var shown = previewFingerprint != null;
        var active = playback.Active;
        var paused = active && playback.Paused;
        var blocked = !available ? reason
            : ReadOnly ? "Wait for the save to finish."
            : previewJob ? "The preview is being built."
            : service.Busy ? "Another edit is running. Wait for it to end."
            : preview.Busy ? "The preview is busy."
            : null;
        using (ImRaii.Disabled(blocked != null))
        {
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Eye, "Preview", null, null, null, null))
                StartPreview(current, loaded);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(blocked ?? "Build the edited clip, redraw your GPose character with it and play it. Nothing is saved.");

        ImGui.SameLine(0, Theme.Scaled(2));
        DrawLiveToggle(current, loaded, "##timeline-live");

        ImGui.SameLine(0, Theme.Scaled(8));
        if (Widgets.IconButton("##timeline-play", active && !paused ? FontAwesomeIcon.Pause : FontAwesomeIcon.Play,
                !shown ? "Press Preview first." : active ? paused ? "Resume" : "Pause" : "Play the preview", shown && available))
            TogglePlay();
        ImGui.SameLine(0, Theme.Scaled(2));
        if (Widgets.IconButton("##timeline-replay", FontAwesomeIcon.Redo, shown ? "Play again from the start" : "Press Preview first.", shown && available))
            Replay(current);
        ImGui.SameLine(0, Theme.Scaled(2));
        if (Widgets.IconButton("##timeline-stop", FontAwesomeIcon.Stop, "Stop the preview and put your character back",
                shown || active || preview.Active || previewJob))
            StopPreview();

        ImGui.SameLine(0, Theme.Scaled(12));
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, "Speed");
        ImGui.SameLine(0, Theme.Gap);
        ImGui.SetNextItemWidth(Theme.Scaled(110));
        if (ImGui.SliderFloat("##timeline-speed", ref speedPercent, 10, 200, "%.0f%%"))
        {
            speedPercent = Math.Clamp(speedPercent, 10, 200);
            if (playback.Active)
                PlaybackCall(() => playback.SetSpeed(speedPercent / 100f));
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Playback speed of the preview.");
        ImGui.SameLine(0, Theme.Scaled(12));
        ImGui.Checkbox("Follow", ref follow);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Outside the preview, move the playhead with this clip while your character plays it.");

        // A status only follows on this line when there is one, so nothing else lands beside the bar.
        if (previewJob)
        {
            ImGui.SameLine(0, Theme.Scaled(12));
            Widgets.Spinner();
            ImGui.SameLine(0, Theme.Gap);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Muted, service.Status);
        }
        else if (liveWanted && (decodeTask != null || motionDisplay?.Building == true || liveDrawWait != null))
        {
            ImGui.SameLine(0, Theme.Scaled(12));
            Widgets.Spinner();
            ImGui.SameLine(0, Theme.Gap);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Muted, decodeTask != null ? decodeStatus : liveDrawWait != null ? "Waiting for your character" : "Applying the bone layers");
        }
        else if (FirstText(liveError, liveWanted ? motionDisplay?.Error : null, previewError, preview.Error, playback.Error) is { } error)
        {
            ImGui.SameLine(0, Theme.Scaled(12));
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Warning, error);
        }
        else if (FirstText(active ? playback.Status : null, preview.Active ? preview.Status : null, available ? null : reason) is { } status)
        {
            ImGui.SameLine(0, Theme.Scaled(12));
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Muted, status);
        }

        if (liveWanted)
        {
            ImGui.TextColored(Theme.Hint, "Live shows bone layers and effect looks. Preview shows sounds, effects, a new length and looks that differ per event too.");
            return;
        }
        if (!shown && !preview.Active)
            return;
        if (preview.CollectionShared)
            ImGui.TextColored(Theme.Warning, "⚠ Others using this collection see the preview too.");
        ImGui.TextColored(Theme.Hint, previewDocument != null && !ReferenceEquals(previewDocument, current.Document)
            ? "Changed since the preview. Press Preview again."
            : !shownLooks.IsEmpty ? "Effect looks change when the effect starts again."
            : "Sounds and effects play only while the preview plays.");
    }

    /// <summary> The Live toggle, in the preview bar and in the Bones window. </summary>
    private void DrawLiveToggle(TimelineEditor current, AnimationTimelineSource loaded, string id)
    {
        var liveBlocked = liveWanted ? null : LiveBlocked(current, Fresh(loaded), PlaybackAvailable(out var reason), reason);
        using (ImRaii.PushColor(ImGuiCol.Button, Theme.Selection, liveWanted)
                   .Push(ImGuiCol.ButtonHovered, Theme.WithAlpha(Theme.Selection, .85f), liveWanted))
        {
            if (Widgets.IconButton(id, FontAwesomeIcon.Bolt, liveWanted
                    ? "Live is on: your bone layers show on the GPose character as you edit. Click to turn it off."
                    : liveBlocked ?? "Show your bone layers on the GPose character as you edit them. Nothing is saved.",
                    liveWanted || liveBlocked == null))
            {
                if (liveWanted)
                    StopPreview();
                else
                    StartLive(loaded);
            }
        }
    }

    private static string? FirstText(params string?[] texts)
    {
        foreach (var text in texts)
            if (!string.IsNullOrEmpty(text))
                return text;
        return null;
    }

    private bool PlaybackAvailable(out string reason)
    {
        try { return playback.Available(out reason); }
        catch (InvalidOperationException e)
        {
            reason = e.Message;
            return false;
        }
    }

    /// <summary> A playback call that reports failure in the bar rather than throwing into the draw. </summary>
    private void PlaybackCall(Func<bool> call)
    {
        try
        {
            if (call())
                previewError = null;
        }
        catch (InvalidOperationException e)
        {
            previewError = e.Message;
        }
    }

    /// <summary>
    /// Builds the edited clip in the service, writes it with the imports it uses into a temporary
    /// mod on the GPose actor's collection, then plays the clip's timeline on that actor.
    /// </summary>
    private void StartPreview(TimelineEditor current, AnimationTimelineSource loaded)
    {
        // The preview plays the built file through a temporary mod; Live's swapped motion must go first.
        StopLive();
        ushort? actor;
        try { actor = playback.ActorIndex(); }
        catch (InvalidOperationException e)
        {
            previewError = e.Message;
            return;
        }
        if (actor is not { } index)
        {
            previewError = "Enter GPose to play the preview.";
            return;
        }
        var document = current.Document;
        var target = Fresh(loaded);
        var clip = target.Clip;
        TimelineDocument rest, shown;
        ImmutableDictionary<string, byte[]> added, lookFiles;
        ImmutableDictionary<string, TimelineEffectEdit> looks;
        try
        {
            if (LookMissing(document) is { } missing)
                throw new InvalidDataException(missing);
            // Effect looks point the effect's own path at an edited copy: the game may keep playing the clip's
            // loaded file, which names that path, and later look changes then show without building again.
            (rest, looks) = TimelineEffectLooks.Redirectable(document);
            lookFiles = TimelineEffectLooks.Redirected(looks, EffectBytes);
            // Looks that differ between events of one effect play from copies, as a save writes them.
            (shown, var used) = WithLooks(rest);
            added = TimelineWindowViews.AddedFiles(shown, used);
        }
        catch (InvalidDataException e)
        {
            previewError = e.Message;
            return;
        }
        var speed = speedPercent / 100f;
        var generation = ++previewGeneration;
        ForgetPreviewLooks();
        previewActor = index;
        previewError = null;
        previewJob = true;
        previewDocument = document;
        previewRest = rest;
        lastSeekFrame = -1;
        animations!.PreviewTimeline(target, shown, async (built, token) =>
        {
            var baseFiles = added.SetItem(built.GamePath, built.Bytes);
            await preview.ShowAsync(index, baseFiles.SetItems(lookFiles), token);
            if (generation != previewGeneration)
            {
                // Stopped or replaced while it was being built: take it down again.
                await preview.StopAsync();
                return "Preview ended.";
            }
            // The character keeps the clip's loaded file until it is drawn again, as after an in-place edit;
            // a stop redraws it once more to put its own file back.
            previewRedrawn = true;
            var redraw = await preview.RedrawAsync(index, token);
            var played = await framework.RunOnFrameworkThread(() =>
            {
                if (generation != previewGeneration)
                    return false;
                previewBaseFiles = baseFiles;
                shownLooks = looks;
                previewFingerprint = built.BindingFingerprint;
                previewTimeline = clip.Timeline;
                previewFace = target.Standalone;
                if (!playback.Play(clip.Timeline, built.BindingFingerprint, index, previewFace))
                    return false;
                if (Math.Abs(speed - 1) > .001f)
                    playback.SetSpeed(speed);
                return true;
            });
            return played ? redraw == null ? "Preview playing." : "Preview playing. " + redraw : "Preview on. " + playback.Error;
        }, current.MotionFrames, target.Standalone ? null : repairTarget);
    }

    private void TogglePlay()
    {
        if (!playback.Active)
        {
            if (previewFingerprint is { } fingerprint)
                PlaybackCall(() => playback.Play(previewTimeline, fingerprint, previewActor, previewFace));
        }
        else if (playback.Paused)
            PlaybackCall(playback.Resume);
        else
            PlaybackCall(playback.Pause);
    }

    private void Replay(TimelineEditor current)
    {
        if (previewFingerprint is not { } fingerprint)
            return;
        PlaybackCall(() => playback.Play(previewTimeline, fingerprint, previewActor, previewFace));
        current.Playhead = 0;
        lastSeekFrame = 0;
    }

    /// <summary>
    /// Ends the preview: the character is put back now, the temporary mod is removed on a task.
    /// Runs when the window closes, the clip changes, before a save and from Stop.
    /// </summary>
    private void StopPreview()
    {
        StopLive();
        previewGeneration++;
        ForgetPreviewLooks();
        try
        {
            if (playback.Active)
                playback.Stop();
        }
        catch (InvalidOperationException e)
        {
            previewError = e.Message;
        }
        previewFingerprint = null;
        previewDocument = null;
        lastSeekFrame = -1;
        var redraw = previewRedrawn ? previewActor : null;
        previewRedrawn = false;
        StopTemporaryMod(redraw);
    }

    /// <summary>
    /// Removes the preview's temporary mod on a task, then redraws <paramref name="redraw"/>, the character
    /// a preview redrew, so it drops the previewed files and loads its own again.
    /// </summary>
    private void StopTemporaryMod(ushort? redraw = null)
    {
        if (!preview.Active && !preview.Busy)
            return;
        var stop = redraw is { } index ? StopAndRedraw(index) : preview.StopAsync();
        // The window may close before this ends; a failure still reaches the log.
        _ = stop.ContinueWith(task => log.Warning(task.Exception!, "Could not end the animation preview."),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        previewStop = stop;
    }

    private async Task StopAndRedraw(ushort index)
    {
        await preview.StopAsync();
        if (await preview.RedrawAsync(index, CancellationToken.None) is { } error)
            log.Warning("The character was not redrawn after the preview: {Error}", error);
    }

    private void PollPreview()
    {
        if (previewJob && animations is { Busy: false } service)
        {
            previewJob = false;
            if (service.LastFailed)
                previewError = service.Status;
        }
        if (previewStop is { IsCompleted: true } stop)
        {
            previewStop = null;
            if (stop.IsFaulted)
                previewError = "The preview could not be removed. " + ErrorText(stop.Exception);
        }
        // The service ends the preview on its own when GPose ends or Penumbra restarts. Live plays the
        // clip's own file and needs no temporary mod.
        if (previewFingerprint != null && !previewJob && !preview.Active && !preview.Busy && !liveWanted)
        {
            previewFingerprint = null;
            previewDocument = null;
            lastSeekFrame = -1;
            ForgetPreviewLooks();
        }
    }

    /// <summary>
    /// Keeps the playhead and the GPose pose together: paused (or while scrubbing) the pose follows
    /// the playhead; playing, the playhead follows the clip. Without a preview, Follow reads the
    /// clip your character plays, read only.
    /// </summary>
    private void SyncPlayback(TimelineEditor current, AnimationTimelineSource loaded)
    {
        try
        {
            if (playback.Active && previewFingerprint is { } fingerprint)
            {
                if (playback.Paused || current.Gesture == TimelineGesture.Scrub)
                {
                    if (current.Playhead != lastSeekFrame)
                    {
                        playback.Seek(current.Playhead / (float)TimelineDocument.Fps, fingerprint);
                        lastSeekFrame = current.Playhead;
                    }
                }
                else if (playback.CurrentTime(fingerprint) is { } seconds)
                {
                    current.Playhead = TimelineWindowViews.Frames(seconds);
                    lastSeekFrame = current.Playhead;
                    current.Reveal(current.Playhead);
                }
                return;
            }
            // After a save the game plays the clip as captured until it loads the saved file, then that file.
            if (follow && !playback.Active && current.Gesture == TimelineGesture.None && loaded.Clip.BindingFingerprint.Length > 0 &&
                (playback.CurrentTime(loaded.Clip.BindingFingerprint) ??
                 (loaded.FileFingerprint.Length > 0 && loaded.FileFingerprint != loaded.Clip.BindingFingerprint
                     ? playback.CurrentTime(loaded.FileFingerprint) : null)) is { } live)
            {
                current.Playhead = TimelineWindowViews.Frames(live);
                lastSeekFrame = current.Playhead;
            }
        }
        catch (InvalidOperationException e)
        {
            previewError = e.Message;
        }
    }
}
