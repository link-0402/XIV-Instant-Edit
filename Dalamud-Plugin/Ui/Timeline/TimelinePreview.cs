using System.Collections.Immutable;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Preview;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// What plays on the GPose actor for the open clip. The preview bar builds the edited clip, shows it
/// through a Penumbra temporary mod on the actor's collection and plays it there, with pause, speed,
/// replay and stop: paused, the playhead scrubs the pose; playing, it follows the clip. Live plays the
/// clip's own file instead and shows its bone layers over it, swapped in by <see cref="MotionLiveDisplay"/>
/// whenever the document changes. While either runs, effect looks and sound changes reach the game
/// without building again. Outside GPose the playhead may follow the clip your character plays. Every
/// playback call runs here, on the framework thread.
/// </summary>
internal sealed class TimelinePreview(TimelineSession session, MotionDecode decode, EffectLookEditor effectLooks, SoundFiles soundFiles,
    Func<SkeletonStandard?> repairTarget)
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
    // A redrawn character shows 65 to 120 ms after Penumbra's redraw event (in game, 2026-10-10).
    private static readonly TimeSpan ShowWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ShowPoll = TimeSpan.FromMilliseconds(15);

    private bool liveWanted;
    private bool liveStarted;
    private string? liveError;
    // Until when Live waits for the GPose character to be drawn again, as after the redraw that ends a preview.
    private DateTime? liveDrawWait;
    private static readonly TimeSpan LiveDrawWait = TimeSpan.FromSeconds(10);

    // Looks shown in game while Preview or Live runs: each effect's own path points at an edited copy in
    // the preview's temporary mod (see TimelineEffectLooks.Redirectable). The game asks for the path again
    // whenever the effect starts, so a changed look shows then, without building the clip again.
    // The preview's files without the looks, and the previewed document without the looks it redirects.
    private ImmutableDictionary<string, byte[]>? previewBaseFiles;
    private TimelineDocument? previewRest;
    private ImmutableDictionary<string, TimelineEffectEdit> shownLooks = ImmutableDictionary<string, TimelineEffectEdit>.Empty;
    // The same for sound changes: each one-sound file's own path points at the edited file (TimelineSoundEdits.Redirectable).
    private ImmutableDictionary<string, byte[]> shownSounds = ImmutableDictionary<string, byte[]>.Empty;
    // Live's temporary mod with looks or sounds is up (Live itself plays the clip's own file and needs none).
    private bool liveLooks;
    private Task? lookSync;
    private (ImmutableDictionary<string, TimelineEffectEdit> Looks, ImmutableDictionary<string, byte[]> Sounds, bool Live, int Generation)? lookSyncing;
    private int lookGeneration;
    // The document last compared with the preview, and the one whose looks were last shown or found unshowable.
    private TimelineDocument? staleChecked;
    private TimelineDocument? lookChecked;
    private bool previewStale;

    /// <summary>Live is on, or waits to start.</summary>
    public bool LiveWanted => liveWanted;
    /// <summary>Live plays on the character.</summary>
    public bool LiveStarted => liveStarted;
    /// <summary>The GPose actor the preview or Live plays on.</summary>
    public ushort? Actor => previewActor;
    /// <summary>The binding fingerprint of what the preview or Live plays; null while neither does.</summary>
    public string? Fingerprint => previewFingerprint;
    /// <summary>A preview is being built.</summary>
    public bool Building => previewJob;
    /// <summary>The removal of the preview's temporary mod, while it runs.</summary>
    public Task? Stopping => previewStop;

    /// <summary>The removal of the temporary mod still running, which the caller waits for instead from now on.</summary>
    public Task TakeStopping()
    {
        var stop = previewStop ?? Task.CompletedTask;
        previewStop = null;
        return stop;
    }

    /// <summary> Every frame: the playhead and the pose together, Live's layers, and the looks and sounds shown. </summary>
    public void Update(TimelineEditor current, AnimationTimelineSource loaded)
    {
        SyncPlayback(current, loaded);
        UpdateLive(current, loaded);
        SyncLooks(current);
    }

    public void DrawBar(TimelineEditor current, AnimationTimelineSource loaded)
    {
        var service = session.Animations!;
        var available = PlaybackAvailable(out var reason);
        var shown = previewFingerprint != null;
        var active = session.Playback.Active;
        var paused = active && session.Playback.Paused;
        var blocked = !available ? reason
            : session.ReadOnly ? "Wait for the save to finish."
            : previewJob ? "The preview is being built."
            : service.Busy ? "Another edit is running. Wait for it to end."
            : session.Preview.Busy ? "The preview is busy."
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
                shown || active || session.Preview.Active || previewJob))
            Stop();

        ImGui.SameLine(0, Theme.Scaled(12));
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, "Speed");
        ImGui.SameLine(0, Theme.Gap);
        ImGui.SetNextItemWidth(Theme.Scaled(110));
        if (ImGui.SliderFloat("##timeline-speed", ref speedPercent, 10, 200, "%.0f%%"))
        {
            speedPercent = Math.Clamp(speedPercent, 10, 200);
            if (session.Playback.Active)
                PlaybackCall(() => session.Playback.SetSpeed(speedPercent / 100f));
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
        else if (liveWanted && (decode.Reading || session.MotionDisplay?.Building == true || liveDrawWait != null))
        {
            ImGui.SameLine(0, Theme.Scaled(12));
            Widgets.Spinner();
            ImGui.SameLine(0, Theme.Gap);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Muted, decode.Reading ? decode.Status : liveDrawWait != null ? "Waiting for your character" : "Applying the bone layers");
        }
        else if (FirstText(liveError, liveWanted ? session.MotionDisplay?.Error : null, previewError, session.Preview.Error, session.Playback.Error) is { } error)
        {
            ImGui.SameLine(0, Theme.Scaled(12));
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Warning, error);
        }
        else if (FirstText(active ? session.Playback.Status : null, session.Preview.Active ? session.Preview.Status : null, available ? null : reason) is { } status)
        {
            ImGui.SameLine(0, Theme.Scaled(12));
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Muted, status);
        }

        if (liveWanted)
        {
            ImGui.TextColored(Theme.Hint, TimelineWindowViews.LiveHint(current.Document.Cut || current.Document.Stretched));
            return;
        }
        if (!shown && !session.Preview.Active)
            return;
        if (session.Preview.CollectionShared)
            ImGui.TextColored(Theme.Warning, "⚠ Others using this collection see the preview too.");
        ImGui.TextColored(Theme.Hint, previewDocument != null && !ReferenceEquals(previewDocument, current.Document)
            ? "Changed since the preview. Press Preview again."
            : !shownLooks.IsEmpty ? "Effect looks change when the effect starts again."
            : "Sounds and effects play only while the preview plays.");
    }

    /// <summary> The Live toggle, in the preview bar and in the Bones window. </summary>
    public void DrawLiveToggle(TimelineEditor current, AnimationTimelineSource loaded, string id)
    {
        var liveBlocked = liveWanted ? null : LiveBlocked(current, session.Fresh(loaded), PlaybackAvailable(out var reason), reason);
        using (ImRaii.PushColor(ImGuiCol.Button, Theme.Selection, liveWanted)
                   .Push(ImGuiCol.ButtonHovered, Theme.WithAlpha(Theme.Selection, .85f), liveWanted))
        {
            if (Widgets.IconButton(id, FontAwesomeIcon.Bolt, liveWanted
                    ? "Live is on: your bone layers show on the GPose character as you edit. Click to turn it off."
                    : liveBlocked ?? "Show your bone layers on the GPose character as you edit them. Nothing is saved.",
                    liveWanted || liveBlocked == null))
            {
                if (liveWanted)
                    Stop();
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

    public bool PlaybackAvailable(out string reason)
    {
        try { return session.Playback.Available(out reason); }
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
        try { actor = session.Playback.ActorIndex(); }
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
        var target = session.Fresh(loaded);
        var clip = target.Clip;
        TimelineDocument rest, shown;
        ImmutableDictionary<string, byte[]> added, lookFiles, soundRedirects;
        ImmutableDictionary<string, TimelineEffectEdit> looks;
        try
        {
            if ((effectLooks.LookMissing(document) ?? soundFiles.SoundMissing(document) ?? soundFiles.SoundCutMissing(document)) is { } missing)
                throw new InvalidDataException(missing);
            // Effect looks point the effect's own path at an edited copy: the game may keep playing the clip's
            // loaded file, which names that path, and later look changes then show without building again.
            (rest, looks) = TimelineEffectLooks.Redirectable(document);
            lookFiles = TimelineEffectLooks.Redirected(looks, effectLooks.EffectBytes);
            // So do changes to a sound file with one sound; pins and other sound changes play from copies.
            (rest, soundRedirects) = TimelineSoundEdits.Redirectable(rest, soundFiles.SoundBytes, session.ImportBytes);
            // Looks that differ between events of one effect play from copies, as a save writes them.
            (shown, var used) = effectLooks.WithLooks(rest);
            var sounds = soundFiles.WithSoundCuts(soundFiles.WithSounds(shown, _ => TimelineSoundRoute.Copy, path => soundRedirects.GetValueOrDefault(path) ?? soundFiles.SoundBytes(path)),
                shown, path => soundRedirects.GetValueOrDefault(path) ?? soundFiles.SoundBytes(path));
            shown = sounds.Document;
            used.AddRange(sounds.Copies);
            added = TimelineWindowViews.AddedFiles(shown, used);
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException)
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
        session.Animations!.PreviewTimeline(target, shown, async (built, token) =>
        {
            var baseFiles = added.SetItem(built.GamePath, built.Bytes);
            await session.Preview.ShowAsync(index, baseFiles.SetItems(lookFiles).SetItems(soundRedirects), token);
            if (generation != previewGeneration)
            {
                // Stopped or replaced while it was being built: take it down again.
                await session.Preview.StopAsync();
                return "Preview ended.";
            }
            // The character keeps the clip's loaded file until it is drawn again, as after an in-place edit;
            // a stop redraws it once more to put its own file back.
            previewRedrawn = true;
            var redraw = await session.Preview.RedrawAsync(index, token);
            await WaitUntilShown(index, token);
            var played = await session.Framework.RunOnFrameworkThread(() =>
            {
                if (generation != previewGeneration)
                    return false;
                previewBaseFiles = baseFiles;
                shownLooks = looks;
                shownSounds = soundRedirects;
                previewFingerprint = built.BindingFingerprint;
                previewTimeline = clip.Timeline;
                previewFace = target.Standalone;
                if (!session.Playback.Play(clip.Timeline, built.BindingFingerprint, index, previewFace))
                    return false;
                if (Math.Abs(speed - 1) > .001f)
                    session.Playback.SetSpeed(speed);
                return true;
            });
            return played ? redraw == null ? "Preview playing." : "Preview playing. " + redraw : "Preview on. " + session.Playback.Error;
        }, current.MotionFrames, target.Standalone ? null : repairTarget());
    }

    /// <summary>
    /// Waits until the redrawn character at <paramref name="index"/> shows, at most <see cref="ShowWait"/> (a character hidden
    /// in GPose never does): it shows a few frames after Penumbra's redraw event, and a clip started before that plays without
    /// its sounds.
    /// </summary>
    private async Task WaitUntilShown(ushort index, CancellationToken token)
    {
        var until = DateTime.UtcNow + ShowWait;
        while (DateTime.UtcNow < until && !await session.Framework.RunOnFrameworkThread(() => session.Playback.Shown(index)))
            await Task.Delay(ShowPoll, token);
    }

    public void TogglePlay()
    {
        if (!session.Playback.Active)
        {
            if (previewFingerprint is { } fingerprint)
                PlaybackCall(() => session.Playback.Play(previewTimeline, fingerprint, previewActor, previewFace));
        }
        else if (session.Playback.Paused)
            PlaybackCall(session.Playback.Resume);
        else
            PlaybackCall(session.Playback.Pause);
    }

    private void Replay(TimelineEditor current)
    {
        if (previewFingerprint is not { } fingerprint)
            return;
        PlaybackCall(() => session.Playback.Play(previewTimeline, fingerprint, previewActor, previewFace));
        current.Playhead = 0;
        lastSeekFrame = 0;
    }

    /// <summary>
    /// Ends the preview: the character is put back now, the temporary mod is removed on a task.
    /// Runs when the window closes, the clip changes, before a save and from Stop.
    /// </summary>
    public void Stop()
    {
        StopLive();
        previewGeneration++;
        ForgetPreviewLooks();
        try
        {
            if (session.Playback.Active)
                session.Playback.Stop();
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
    public void StopTemporaryMod(ushort? redraw = null)
    {
        if (!session.Preview.Active && !session.Preview.Busy)
            return;
        var stop = redraw is { } index ? StopAndRedraw(index) : session.Preview.StopAsync();
        // The window may close before this ends; a failure still reaches the log.
        _ = stop.ContinueWith(task => session.Log.Warning(task.Exception!, "Could not end the animation preview."),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        previewStop = stop;
    }

    private async Task StopAndRedraw(ushort index)
    {
        await session.Preview.StopAsync();
        if (await session.Preview.RedrawAsync(index, CancellationToken.None) is { } error)
            session.Log.Warning("The character was not redrawn after the preview: {Error}", error);
    }

    public void Poll()
    {
        if (previewJob && session.Animations is { Busy: false } service)
        {
            previewJob = false;
            if (service.LastFailed)
                previewError = service.Status;
        }
        if (previewStop is { IsCompleted: true } stop)
        {
            previewStop = null;
            if (stop.IsFaulted)
                previewError = "The preview could not be removed. " + TimelineSession.ErrorText(stop.Exception);
        }
        // The service ends the preview on its own when GPose ends or Penumbra restarts. Live plays the
        // clip's own file and needs no temporary mod.
        if (previewFingerprint != null && !previewJob && !session.Preview.Active && !session.Preview.Busy && !liveWanted)
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
            if (session.Playback.Active && previewFingerprint is { } fingerprint)
            {
                if (session.Playback.Paused || current.Gesture == TimelineGesture.Scrub)
                {
                    if (current.Playhead != lastSeekFrame)
                    {
                        session.Playback.Seek(current.Playhead / (float)TimelineDocument.Fps, fingerprint);
                        lastSeekFrame = current.Playhead;
                    }
                }
                else if (session.Playback.CurrentTime(fingerprint) is { } seconds)
                {
                    current.Playhead = TimelineWindowViews.Frames(seconds);
                    lastSeekFrame = current.Playhead;
                    current.Reveal(current.Playhead);
                }
                return;
            }
            // After a save the game plays the clip as captured until it loads the saved file, then that file.
            if (follow && !session.Playback.Active && current.Gesture == TimelineGesture.None && loaded.Clip.BindingFingerprint.Length > 0 &&
                (session.Playback.CurrentTime(loaded.Clip.BindingFingerprint) ??
                 (loaded.FileFingerprint.Length > 0 && loaded.FileFingerprint != loaded.Clip.BindingFingerprint
                     ? session.Playback.CurrentTime(loaded.FileFingerprint) : null)) is { } live)
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

    /// <summary>Why Live can't start now; null when it can.</summary>
    private string? LiveBlocked(TimelineEditor current, AnimationTimelineSource loaded, bool available, string reason)
    {
        if (session.MotionDisplay == null)
            return "Live edits are unavailable.";
        if (loaded.Standalone)
            return "An expression's face can't take bone layers yet.";
        if (!available)
            return reason;
        if (session.ReadOnly)
            return "Wait for the save to finish.";
        if (loaded.Clip.Resolution is not { State: SkeletonResolutionState.Matched, Selected: not null })
            return loaded.Clip.Resolution?.Reason ?? "Choose the skeleton this animation was made for under Source first.";
        return null;
    }

    /// <summary>The length Live plays the motion at: the document's when cut or stretched, else 0 for the clip's own.</summary>
    private static int LiveLength(TimelineDocument doc) => doc.Cut || doc.Stretched ? doc.Length : 0;

    private void StartLive(AnimationTimelineSource loaded)
    {
        Stop();
        liveWanted = true;
        liveStarted = false;
        liveError = null;
        liveDrawWait = null;
        // Turning Live on reads a clip again whose last read failed.
        decode.ReadAgain();
        decode.Ensure(session.Fresh(loaded));
    }

    /// <summary>
    /// Puts the clip's own motion back and ends its playback. Stop, every reload and every save end Live here.
    /// <paramref name="keepPlaying"/> leaves the clip playing, plain, as the loader leaves it.
    /// </summary>
    private void StopLive(bool keepPlaying = false)
    {
        if (!liveWanted)
            return;
        liveWanted = false;
        liveStarted = false;
        liveDrawWait = null;
        StopLiveLooks();
        try
        {
            session.MotionDisplay?.Stop();
            if (session.Playback.Active && !keepPlaying)
                session.Playback.Stop();
        }
        catch (InvalidOperationException e)
        {
            liveError = e.Message;
        }
        previewFingerprint = null;
        lastSeekFrame = -1;
    }

    /// <summary>Every frame: starts the display once the motion is decoded, then hands it the document's layers.</summary>
    private void UpdateLive(TimelineEditor current, AnimationTimelineSource loaded)
    {
        if (decode.Poll() && liveWanted)
        {
            StopLive();
            liveError = "The motion could not be read: " + decode.Error;
        }
        if (!liveWanted || session.MotionDisplay == null)
            return;
        var fresh = session.Fresh(loaded);
        if (!liveStarted && WaitingForDraw())
            return;
        if (LiveBlocked(current, fresh, PlaybackAvailable(out var reason), reason) is { } blocked)
        {
            StopLive();
            liveError = blocked;
            return;
        }
        if (decode.DecodedKey != null && decode.DecodedKey != MotionDecode.KeyOf(fresh))
        {
            StopLive();
            liveError = "The clip changed. Turn Live on again.";
            return;
        }
        if (decode.Decoded is not { } decoded)
            return;
        try
        {
            if (liveStarted && !session.MotionDisplay.Active)
            {
                // The display ended itself, such as on a skeleton without a bone the layers move: the clip plays on.
                var ended = session.MotionDisplay.Error;
                StopLive(keepPlaying: true);
                liveError = ended.Length > 0 ? ended : "The live edits stopped.";
                return;
            }
            if (!liveStarted)
            {
                if (session.Playback.ActorIndex() is not { } actor)
                {
                    StopLive();
                    liveError = "Enter GPose to show the edits live.";
                    return;
                }
                var clip = fresh.Clip;
                if (!session.Playback.Play(clip.Timeline, clip.BindingFingerprint, actor))
                {
                    var error = session.Playback.Error;
                    StopLive();
                    liveError = error;
                    return;
                }
                previewFingerprint = clip.BindingFingerprint;
                previewTimeline = clip.Timeline;
                previewActor = actor;
                previewFace = false;
                previewDocument = null;
                lastSeekFrame = -1;
                // After a save the game plays either file until it reloads the clip: the one saved or the one before.
                if (!session.MotionDisplay.Start(actor, clip.BindingFingerprint, [fresh.FileFingerprint, fresh.BaseFingerprint ?? string.Empty],
                        decoded, current.Document.Motion, current.Document.ExcludedBones, current.Document.Cuts, LiveLength(current.Document),
                        // Edited again, the game plays the saved file, as long as the edit made it.
                        fresh.BaseMotion != null ? fresh.Clip.Duration : null))
                {
                    var error = session.MotionDisplay.Error;
                    StopLive();
                    liveError = error;
                    return;
                }
                liveStarted = true;
                return;
            }
            session.MotionDisplay.Show(current.Document.Motion, current.Document.ExcludedBones, current.Document.Cuts, LiveLength(current.Document));
        }
        catch (InvalidOperationException e)
        {
            StopLive();
            liveError = e.Message;
        }
    }

    /// <summary>
    /// Before Live starts: whether to wait for the character. An ending preview redraws it with its own file first,
    /// and Penumbra reports that redraw done before the character has its skeleton again, so Live waits a while.
    /// </summary>
    private bool WaitingForDraw()
    {
        if (previewStop is { IsCompleted: false })
            return true;
        ushort? actor;
        try { actor = session.Playback.ActorIndex(); }
        catch (InvalidOperationException) { return false; }
        if (actor is not { } index || session.Playback.Drawn(index))
        {
            liveDrawWait = null;
            return false;
        }
        return (liveDrawWait ??= DateTime.UtcNow + LiveDrawWait) > DateTime.UtcNow;
    }

    /// <summary> Forgets the looks shown with a preview that ended or is replaced; a running update is ignored. </summary>
    private void ForgetPreviewLooks()
    {
        lookGeneration++;
        previewBaseFiles = null;
        previewRest = null;
        shownLooks = ImmutableDictionary<string, TimelineEffectEdit>.Empty;
        shownSounds = ImmutableDictionary<string, byte[]>.Empty;
        liveLooks = false;
        staleChecked = null;
        lookChecked = null;
        previewStale = false;
    }

    /// <summary> Ends Live's temporary mod with looks, when it has one. </summary>
    private void StopLiveLooks()
    {
        if (!liveLooks && lookSyncing is not { Live: true })
            return;
        ForgetPreviewLooks();
        StopTemporaryMod();
    }

    /// <summary>
    /// Every frame: while Preview or Live runs, puts the document's effect looks into the temporary mod
    /// when they changed, one update at a time. A preview whose document changed otherwise is stale
    /// and needs Preview again; Live shows looks over the clip's own file.
    /// </summary>
    private void SyncLooks(TimelineEditor current)
    {
        if (lookSync != null)
        {
            if (!lookSync.IsCompleted)
                return;
            var task = lookSync;
            lookSync = null;
            if (lookSyncing is { } done && done.Generation == lookGeneration)
            {
                if (task.IsCompletedSuccessfully)
                {
                    shownLooks = done.Looks;
                    shownSounds = done.Sounds;
                    liveLooks = done.Live && (done.Looks.Count > 0 || done.Sounds.Count > 0);
                }
                else if (!task.IsCanceled)
                    previewError = "The effect looks and sound changes could not be shown. " + TimelineSession.ErrorText(task.Exception);
            }
            lookSyncing = null;
            lookChecked = null;
        }
        var live = liveWanted && liveStarted;
        var previewing = !liveWanted && previewFingerprint != null && !previewJob && previewBaseFiles != null && previewRest != null;
        if (!live && !previewing)
            return;
        var doc = current.Document;
        if (previewing && !ReferenceEquals(doc, staleChecked) && soundFiles.SoundMissing(doc) == null)
        {
            staleChecked = doc;
            // Only looks or redirected sound changes changed: the preview shows the document once they are in.
            try { previewStale = !TimelineEdits.Same(soundFiles.PreviewRest(doc).Document, previewRest!); }
            catch (Exception e) when (e is InvalidDataException or ArgumentException) { previewStale = true; }
            if (!previewStale)
                previewDocument = doc;
        }
        if (previewing && previewStale || ReferenceEquals(doc, lookChecked) || previewStop is { IsCompleted: false } || session.Preview.Busy ||
            previewActor is not { } actor || effectLooks.LookMissing(doc) != null || soundFiles.SoundMissing(doc) != null)
            return;
        lookChecked = doc;
        var looks = TimelineEffectLooks.Redirectable(doc).Looks;
        ImmutableDictionary<string, byte[]> sounds;
        try { sounds = soundFiles.PreviewRest(doc).Sounds; }
        catch (Exception e) when (e is InvalidDataException or ArgumentException)
        {
            previewError = e.Message;
            return;
        }
        if (TimelineEffectLooks.SameLooks(looks, shownLooks) && SameFiles(sounds, shownSounds))
            return;
        ImmutableDictionary<string, byte[]> files;
        try
        {
            files = (previewing ? previewBaseFiles! : ImmutableDictionary<string, byte[]>.Empty)
                .SetItems(TimelineEffectLooks.Redirected(looks, effectLooks.EffectBytes)).SetItems(sounds);
        }
        catch (InvalidDataException e)
        {
            previewError = e.Message;
            return;
        }
        lookSyncing = (looks, sounds, live, lookGeneration);
        // Live without looks needs no temporary mod.
        lookSync = files.Count == 0 ? session.Preview.StopAsync() : session.Preview.ShowAsync(actor, files, CancellationToken.None);
    }

    /// <summary> Whether two sets of files by game path hold the same bytes. </summary>
    private static bool SameFiles(IReadOnlyDictionary<string, byte[]> a, IReadOnlyDictionary<string, byte[]> b) =>
        a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var other) && (ReferenceEquals(pair.Value, other) || pair.Value.AsSpan().SequenceEqual(other)));
}
