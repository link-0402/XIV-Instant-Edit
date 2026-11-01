using System.Collections.Immutable;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Preview;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Services.PenumbraMods;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// What plays on the GPose actor for the open clip. The play bar's Preview builds the edited clip, shows it
/// through a Penumbra temporary mod on the actor's collection and plays it there, with pause, speed,
/// replay and stop: paused, the playhead scrubs the pose; playing, it follows the clip. Live plays the
/// clip's own file instead and shows its bone layers over it, swapped in by <see cref="MotionLiveDisplay"/>
/// whenever the document changes. While either runs, effect looks and sound changes reach the game
/// without building again. Outside GPose the playhead may follow the clip your character plays. Every
/// playback call runs here, on the framework thread.
/// </summary>
internal sealed class TimelinePreview(TimelineSession session, MotionDecode decode, EffectLookEditor effectLooks, SoundFiles soundFiles)
{
    private float speedPercent = 100;
    private volatile string? previewFingerprint;
    private volatile int previewGeneration;
    private ushort previewTimeline;
    // The previewed timeline is an expression's: it plays on the face only.
    private bool previewFace;
    // The previewed clip loops on its own; one that doesn't is played again at its end (RepeatAtEnd).
    private bool previewLoop;
    // The clip was seen playing before its end since it last started, so its end may start it again.
    private bool repeatArmed;
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
    // The decoded motion the display was started on; a motion from Blender put in or taken out decodes another.
    private MotionClip? liveDecoded;
    private string? liveError;
    // Until when Live waits for the GPose character to be drawn again, as after the redraw that ends a preview.
    private DateTime? liveDrawWait;
    private static readonly TimeSpan LiveDrawWait = TimeSpan.FromSeconds(10);
    // Live turned on over a built preview: its layers go onto the file the preview built, which the game plays as it is
    // (a rebuild already in the character's race, so every position shows), and turning Live off goes back to that preview.
    private BuiltPreview? livePreview;
    // Preview was pressed while Live was on: Live comes back on over the new build.
    private bool liveAfterPreview;
    // What the shown preview was built with besides its document: the skeleton choice, as rebuild model and standard.
    private (string? Model, SkeletonStandard? Standard, SkeletonStandard? Repair)? previewSkeleton;
    // The document the preview was built from. previewDocument follows it to later documents the preview shows as well
    // (other looks, and under Live other bone layers); Live turned off goes back to this one.
    private TimelineDocument? builtDocument;

    /// <summary>A built preview Live runs over: the binding it plays, its timeline and actor, and its length when the document changed it.</summary>
    private sealed record BuiltPreview(string Fingerprint, ushort Timeline, ushort Actor, float? Duration);

    // Looks shown in game while Preview or Live runs: each effect's own path points at an edited copy in
    // the preview's temporary mod (see TimelineEffectLooks.Redirectable). A playing timeline loads its effects
    // and sounds once, when its resources load, and keeps them through every loop (in game 2026-10-29: a tint
    // changed while the preview played never showed), so each update redraws the character and starts the
    // clip again (ShowAndReload), without building it again.
    // The preview's files without the looks, and the previewed document without the looks it redirects.
    private ImmutableDictionary<string, byte[]>? previewBaseFiles;
    // The placed props' attach overrides the preview's mod carries, encoded; a look update keeps them.
    private string previewManipulations = string.Empty;
    private TimelineDocument? previewRest;
    private ImmutableDictionary<string, TimelineEffectEdit> shownLooks = ImmutableDictionary<string, TimelineEffectEdit>.Empty;
    // The same for sound changes: each one-sound file's own path points at the edited file (TimelineSoundEdits.Redirectable).
    private ImmutableDictionary<string, byte[]> shownSounds = ImmutableDictionary<string, byte[]>.Empty;
    // Live's temporary mod with looks or sounds is up (Live itself plays the clip's own file and needs none).
    private bool liveLooks;
    private Task? lookSync;
    private (ImmutableDictionary<string, TimelineEffectEdit> Looks, ImmutableDictionary<string, byte[]> Sounds, bool Live, int Generation)? lookSyncing;
    private int lookGeneration;
    // A look edit waits until the document stays the same this long, so dragging a colour doesn't redraw every frame.
    private TimelineDocument? lookPending;
    private long lookPendingAt;
    private const long LookSettleMs = 400;
    // The frame a paused preview held when a look update started it again; it pauses there again once it plays.
    private int? reloadHold;
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

    /// <summary>A gizmo drag runs: Live builds every few frames, which the status doesn't flash for.</summary>
    public bool Dragging { get; set; }

    /// <summary>Outside the preview, the playhead moves with this clip while your character plays it.</summary>
    public bool Follow { get; set; } = true;

    /// <summary>The preview's playback speed in percent, 10 to 200. Setting it while the preview plays changes the speed now.</summary>
    public float SpeedPercent
    {
        get => speedPercent;
        set
        {
            speedPercent = Math.Clamp(value, 10, 200);
            if (session.Playback.Active)
                PlaybackCall(() => session.Playback.SetSpeed(speedPercent / 100f));
        }
    }

    /// <summary>Why Preview can't start now; null when it can.</summary>
    internal string? PreviewBlocked()
    {
        var available = PlaybackAvailable(out var reason);
        return !available ? reason
            : session.ReadOnly ? "Wait for the save to finish."
            : previewJob ? "The preview is being built."
            : session.Animations!.Busy ? "Another edit is running. Wait for it to end."
            : session.Preview.Busy ? "The preview is busy."
            : null;
    }

    /// <summary> Every frame: the playhead and the pose together, Live's layers, and the looks and sounds shown. </summary>
    public void Update(TimelineEditor current, AnimationTimelineSource loaded)
    {
        if (liveAfterPreview && !previewJob)
        {
            liveAfterPreview = false;
            if (OnBuiltPreview() != null)
                StartLive(loaded);
        }
        SyncPlayback(current, loaded);
        UpdateLive(current, loaded);
        SyncLooks(current);
    }

    /// <summary>
    /// The status line's notes for the preview and Live: work running, else an error, else the playback status or why
    /// playback is unavailable; then bones Live can't show, the rebuilt-positions note, the shared collection and what the
    /// preview still needs. <paramref name="repair"/> is the standard skeleton a preview repairs the clip onto.
    /// </summary>
    internal IEnumerable<StatusNote> Notes(TimelineEditor current, AnimationTimelineSource loaded, SkeletonStandard? repair)
    {
        var notes = new List<StatusNote>();
        var available = PlaybackAvailable(out var reason);
        var shown = previewFingerprint != null;
        var active = session.Playback.Active;

        // One of these at a time, as on the preview bar before: an error waits for the work, a status for the error.
        if (previewJob)
            notes.Add(new StatusNote("preview-work", StatusKind.Working, session.Animations!.Status));
        else if (liveWanted && (decode.Reading || session.MotionDisplay?.Building == true && !Dragging || liveDrawWait != null))
            notes.Add(new StatusNote("live-work", StatusKind.Working,
                decode.Reading ? decode.Status : liveDrawWait != null ? "Waiting for your character" : "Applying the bone layers"));
        else if (FirstText(liveError, liveWanted ? session.MotionDisplay?.Error : null, previewError, session.Preview.Error, session.Playback.Error) is { } error)
            notes.Add(new StatusNote("preview-error", StatusKind.Error, error));
        else if (FirstText(active ? session.Playback.Status : null, session.Preview.Active ? session.Preview.Status : null, available ? null : reason) is { } status)
            notes.Add(new StatusNote("playback", StatusKind.Notice, status));

        var needed = PreviewNeeded(current, loaded, repair);
        if (liveWanted)
        {
            if (session.MotionDisplay?.Unshown is > 0 and var unshown)
                notes.Add(new StatusNote("live-unshown", StatusKind.Warning, TimelineWindowViews.LiveUnshown(unshown)));
            if (livePreview == null && session.Fresh(loaded).Rebuild is { Model: not null } && TimelineWindowViews.LiveRebuiltPositions(current.Document))
                notes.Add(new StatusNote("rebuilt-positions", StatusKind.Notice, TimelineWindowViews.LiveRebuiltPositionsNote));
            // Live without a preview shows the bone layers itself, so nothing waits (PreviewNeeded is null then).
            if (livePreview != null && needed != null)
                notes.Add(new StatusNote("preview-needs", StatusKind.Notice, needed));
            return notes;
        }
        if (shown || session.Preview.Active)
        {
            if (session.Preview.CollectionShared)
                notes.Add(new StatusNote("collection-shared", StatusKind.Warning, "⚠ Others using this collection see the preview too."));
        }
        if (needed != null)
            notes.Add(new StatusNote("preview-needs", StatusKind.Notice, needed));
        return notes;
    }

    /// <summary> The fixed hints the Preview button's tooltip adds to what it does. </summary>
    internal const string PreviewHints = "Sounds and effects play only while the preview plays.\nChanging an effect look redraws your character.";

    /// <summary> The Live toggle, icon and label, filled while on: in the play bar and in the Bones window. </summary>
    public void DrawLiveToggle(TimelineEditor current, AnimationTimelineSource loaded, string id)
    {
        var liveBlocked = liveWanted ? null : LiveBlocked(current, session.Fresh(loaded), PlaybackAvailable(out var reason), reason);
        var enabled = liveWanted || liveBlocked == null;
        bool clicked;
        using (ImRaii.PushId(id))
        using (ImRaii.Disabled(!enabled))
        using (ImRaii.PushColor(ImGuiCol.Button, Theme.Selection, liveWanted)
                   .Push(ImGuiCol.ButtonHovered, Theme.WithAlpha(Theme.Selection, .85f), liveWanted))
            clicked = ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Bolt, "Live", null, null, null, null);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip((liveWanted
                ? "Live is on: your bone layers show on the GPose character as you edit. Click to turn it off."
                : liveBlocked ?? (OnBuiltPreview() != null
                    ? "Show your bone layers over the preview as you edit them. Nothing is saved."
                    : "Show your bone layers on the GPose character as you edit them. Nothing is saved.")) + "\n" +
                (livePreview != null ? TimelineWindowViews.LiveOnPreviewHint : TimelineWindowViews.LiveHint(current.Document.Cut || current.Document.Stretched)));
        if (!clicked || !enabled)
            return;
        if (!liveWanted && Carried(session.Fresh(loaded)) && OnBuiltPreview() == null)
            // A face opened on its own plays only through the carrier: Live starts over its preview.
            StartPreview(current, loaded, null, live: true);
        else if (!liveWanted)
            StartLive(loaded);
        else if (livePreview != null)
            // Back to the preview Live ran over, as built.
            StopLive(keepPlaying: true);
        else
            Stop();
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
    internal void StartPreview(TimelineEditor current, AnimationTimelineSource loaded, SkeletonStandard? repair, bool live = false)
    {
        // The preview plays the built file through a temporary mod; Live's swapped motion must go first, and comes back over the build.
        // With live (a face opened on its own) Live starts over it once it plays.
        var relive = liveWanted || live;
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
        string manipulations;
        var types = session.Animations!.PropTypes;
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
            // Placed props show in place: their attach overrides for the actor's race ride in the preview's mod. A prop
            // whose attach point is still unknown is left out; this preview learns it (see LearnProps below).
            // Places as loaded are the mod's own overrides, which the collection applies already.
            var opened = session.Source?.Document;
            var placed = document.Events.Concat(opened?.Events ?? []).Any(e => e.Place != null) && session.Playback.ActorRace(index) is { } race
                ? TimelinePropPlaces.Manipulations(document, [race],
                    r => session.AttachTable(r) ?? throw new InvalidDataException($"The game has no attach table for c{r:D4}."), types, skipUnlearned: true,
                    opened, session.Source?.ModAttachOverrides)
                : [];
            manipulations = placed.IsEmpty ? string.Empty : PenumbraMetaEncoder.Encode(placed);
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException)
        {
            previewError = e.Message;
            return;
        }
        // Penumbra gives a collection one file per game path: another actor previewing this file there keeps it.
        string[] gamePaths = [clip.GamePath, .. added.Keys, .. lookFiles.Keys, .. soundRedirects.Keys];
        if (session.Files?.Holder(target.Capture.CollectionId, gamePaths, session.WorkspaceId) is { } holder)
        {
            previewError = PreviewFiles.Refusal(holder);
            return;
        }
        session.Files?.Set(session.WorkspaceId, session.ActorName(), target.Capture.CollectionId, gamePaths, () => previewJob || session.Preview.Active);
        var speed = speedPercent / 100f;
        var generation = ++previewGeneration;
        ForgetPreviewLooks();
        previewActor = index;
        previewError = null;
        previewJob = true;
        liveAfterPreview = relive;
        previewDocument = builtDocument = document;
        previewSkeleton = SkeletonChoice(target, repair);
        previewRest = rest;
        lastSeekFrame = -1;
        var service = session.Animations!;
        Func<string, byte[]?> game = path => session.Data.GetFile(path)?.Data;
        service.PreviewTimeline(target, shown, async (built, token) =>
        {
            // A clip moving bones the actor lacks (an edit made for a bigger skeleton) is never handed to the game.
            if (!target.Standalone &&
                await session.Framework.RunOnFrameworkThread(() => service.PlayProblem(index, clip.Partial, built.Bytes, built.BindingFingerprint)) is { } problem)
                throw new InvalidOperationException(problem);
            // Props from other mods play from their private copy, its files (textures of several MB) read here off the
            // framework thread; a file gone fails the preview with the copy's text. The preview's mod has no alias to
            // follow, so each alias holds its target's bytes.
            var props = await Task.Run(() => TimelineWindowViews.PropAdded(shown, game), token);
            if (props.Error != null)
                throw new InvalidDataException(props.Error);
            // An expression's face and a carried face's carrier go with the built file.
            var baseFiles = added.SetItems(PropCopies.Flatten(new PropCopy(props.Files, props.Aliases, []))).SetItem(built.GamePath, built.Bytes)
                .SetItems(built.Also);
            await session.Preview.ShowAsync(index, baseFiles.SetItems(lookFiles).SetItems(soundRedirects), manipulations, token);
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
                previewManipulations = manipulations;
                shownLooks = looks;
                shownSounds = soundRedirects;
                previewFingerprint = built.BindingFingerprint;
                // A face opened on its own has no expression of its own: the carrier plays it.
                previewTimeline = Carried(target) ? FaceCarrier.TimelineId(session.Data) ?? 0 : clip.Timeline;
                previewFace = target.Standalone || shown.Face != null;
                previewLoop = clip.IsLoop;
                repeatArmed = false;
                if (!session.Playback.Play(previewTimeline, built.BindingFingerprint, index, previewFace))
                    return false;
                if (Math.Abs(speed - 1) > .001f)
                    session.Playback.SetSpeed(speed);
                // The props whose attach point is unknown are watched as they appear, to 2 s after the last one does;
                // none ends the last preview's watch.
                var starts = TimelinePropPlaces.PropStarts(document);
                var unknown = starts.Keys.Where(model => types.CodeOf(model) == null).ToArray();
                session.Playback.LearnProps(index, unknown, unknown.Length == 0 ? TimeSpan.Zero
                    : TimeSpan.FromSeconds(unknown.Max(model => starts[model]) / (double)TimelineDocument.Fps / Math.Max(speed, .1f) + 2), types.Learn);
                return true;
            });
            return played ? redraw == null ? "Preview playing." : "Preview playing. " + redraw : "Preview on. " + session.Playback.Error;
        }, current.MotionFrames, target.Standalone ? null : repair);
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
            {
                repeatArmed = false;
                PlaybackCall(() => session.Playback.Play(previewTimeline, fingerprint, previewActor, previewFace));
            }
        }
        else if (session.Playback.Paused)
            PlaybackCall(session.Playback.Resume);
        else
            PlaybackCall(session.Playback.Pause);
    }

    /// <summary>
    /// A playhead moved by a key pauses a playing preview: while it plays the playhead follows the clip,
    /// which would undo the step the next frame (with GPose's motion paused, the clip's time stands still).
    /// Paused, the pose follows the playhead instead.
    /// </summary>
    public void PauseForStep()
    {
        if (session.Playback.Active && !session.Playback.Paused)
            PlaybackCall(session.Playback.Pause);
    }

    /// <summary>Plays the shown preview again from frame 0, the playhead with it.</summary>
    internal void Replay(TimelineEditor current)
    {
        if (previewFingerprint is not { } fingerprint)
            return;
        repeatArmed = false;
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
        liveAfterPreview = false;
        previewSkeleton = null;
        builtDocument = null;
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
        session.Files?.Clear(session.WorkspaceId);
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
        session.PlayheadFollows = false;
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
                else
                {
                    if (reloadHold is { } hold && session.Playback.CurrentTime(fingerprint) != null)
                    {
                        // Paused before a look update started the clip again: paused again once it plays, at the same frame.
                        reloadHold = null;
                        PlaybackCall(session.Playback.Pause);
                        current.Playhead = hold;
                        lastSeekFrame = -1;
                        return;
                    }
                    if (session.Playback.CurrentTime(fingerprint) is { } seconds)
                    {
                        current.Playhead = TimelineWindowViews.Frames(seconds);
                        lastSeekFrame = current.Playhead;
                        current.Reveal(current.Playhead);
                        session.PlayheadFollows = true;
                    }
                    RepeatAtEnd(current, fingerprint);
                }
                return;
            }
            // After a save the game plays the clip as captured until it loads the saved file, then that file.
            if (Follow && !session.Playback.Active && current.Gesture == TimelineGesture.None && loaded.Clip.BindingFingerprint.Length > 0 &&
                (session.Playback.CurrentTime(loaded.Clip.BindingFingerprint) ??
                 (loaded.FileFingerprint.Length > 0 && loaded.FileFingerprint != loaded.Clip.BindingFingerprint
                     ? session.Playback.CurrentTime(loaded.FileFingerprint) : null)) is { } live)
            {
                current.Playhead = TimelineWindowViews.Frames(live);
                lastSeekFrame = current.Playhead;
                session.PlayheadFollows = true;
            }
        }
        catch (InvalidOperationException e)
        {
            previewError = e.Message;
        }
    }

    /// <summary>
    /// A clip that doesn't loop, such as a startup, plays again from the start once it ends, as GPose repeats it. The game
    /// holds such a clip on its last frame or drops it; either counts only after the clip was seen playing before its end
    /// since the last start, so a clip that isn't on the character yet, or a native GPose freeze, never restarts it every frame.
    /// Live plays the clip's own file under swapped bone layers and is left alone.
    /// </summary>
    private void RepeatAtEnd(TimelineEditor current, string fingerprint)
    {
        if (previewLoop || liveWanted)
            return;
        switch (session.Playback.AtEnd(fingerprint))
        {
            case false:
                repeatArmed = true;
                break;
            case true or null when repeatArmed:
                Replay(current);
                break;
        }
    }

    /// <summary>Why Live can't start now; null when it can.</summary>
    private string? LiveBlocked(TimelineEditor current, AnimationTimelineSource loaded, bool available, string reason)
    {
        if (session.MotionDisplay == null)
            return "Live edits are unavailable.";
        if (loaded.MotionTarget.Standalone)
            return loaded.FaceProblem ?? "This expression's face can't be edited.";
        if (!available)
            return reason;
        if (session.ReadOnly)
            return "Wait for the save to finish.";
        if (loaded.Clip.Resolution is not { State: SkeletonResolutionState.Matched, Selected: not null })
            return loaded.Clip.Resolution?.Reason ?? "Choose the skeleton this animation was made for under Source first.";
        return null;
    }

    /// <summary>
    /// The length Live plays the motion at: the document's when cut or stretched, else 0 for the clip's own. A face plays
    /// its own length once it changed.
    /// </summary>
    internal static int LiveLength(TimelineDocument doc) =>
        doc.Face is { } face ? face.LengthChanged || doc.Replacement != null ? face.Frames : 0
        : doc.Cut || doc.Stretched ? doc.Length : 0;

    /// <summary>The layers Live shows: a face's in the face's own frames, a clip's as they are.</summary>
    internal static ImmutableArray<MotionLayer> LiveLayers(TimelineDocument doc) =>
        doc.Face is { } face ? TimelineFace.ToFace(doc.Motion, face.Anchor) : doc.Motion;

    /// <summary>Whether <paramref name="loaded"/> is a face opened on its own, which plays through the carrier (<see cref="FaceCarrier"/>).</summary>
    private static bool Carried(AnimationTimelineSource loaded) => !loaded.Standalone && loaded.Document?.Face != null;

    /// <summary>The skeleton choice a preview of <paramref name="source"/> builds with: the rebuild's race and standard, and the repair.</summary>
    private static (string? Model, SkeletonStandard? Standard, SkeletonStandard? Repair) SkeletonChoice(AnimationTimelineSource source, SkeletonStandard? repair) =>
        (source.Rebuild?.Model, source.Rebuild?.Target.Standard, repair);

    /// <summary>
    /// Why the character doesn't show the document as it is: changed since the preview was built, the skeleton chosen
    /// otherwise, or with no preview, edits or a rebuild (the one picked for your race too) that only a build shows. Null when
    /// nothing waits. Live without a preview shows the bone layers itself; its own lines say what it can't.
    /// </summary>
    private string? PreviewNeeded(TimelineEditor current, AnimationTimelineSource loaded, SkeletonStandard? repair)
    {
        var fresh = session.Fresh(loaded);
        if (previewFingerprint != null && previewDocument != null && session.Preview.Active)
            return ReferenceEquals(previewDocument, current.Document) && previewSkeleton == SkeletonChoice(fresh, repair) ? null : TimelineWindowViews.PreviewStale;
        if (liveWanted && livePreview == null)
            return null;
        // After a save the game plays the saved file, rebuilt or repaired as it was saved.
        return current.Dirty || fresh.Rebuild != null && fresh.BaseMotion == null ? TimelineWindowViews.PreviewNeeded : null;
    }

    /// <summary>The preview built and playing now, which Live can run over; null when there is none.</summary>
    private BuiltPreview? OnBuiltPreview() =>
        // An expression's own timeline has no motion to run over; a face's preview has its face.
        !liveWanted && !previewJob && (!previewFace || previewDocument?.Face != null) && previewFingerprint is { } fingerprint && session.Preview.Active &&
        previewBaseFiles != null && previewActor is { } actor && previewDocument is { } doc
            ? new BuiltPreview(fingerprint, previewTimeline, actor, LiveLength(doc) is > 0 and var frames ? frames / (float)TimelineDocument.Fps : null)
            : null;

    private void StartLive(AnimationTimelineSource loaded)
    {
        // Over a built preview Live keeps it, its files and its playback; otherwise the character plays the clip's own file.
        var built = OnBuiltPreview();
        if (built == null)
            Stop();
        livePreview = built;
        staleChecked = null;
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
    /// <paramref name="keepPlaying"/> leaves the clip playing, plain, as the loader leaves it; over a built preview, that preview.
    /// </summary>
    private void StopLive(bool keepPlaying = false)
    {
        if (!liveWanted)
            return;
        var onPreview = livePreview != null;
        liveWanted = false;
        liveStarted = false;
        liveDecoded = null;
        liveDrawWait = null;
        livePreview = null;
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
        if (!onPreview || !keepPlaying)
            previewFingerprint = null;
        else
        {
            // The preview shows the bone layers it was built with again: its document is checked anew.
            previewDocument = builtDocument;
            staleChecked = null;
        }
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
        // Back in GPose the game's replay is observed again and matched for a moment: Live waits for that match.
        if (fresh.Clip.Resolution is { State: SkeletonResolutionState.Searching })
            return;
        // A read that waited for that match starts now.
        if (decode.DecodedKey == null)
            decode.Ensure(fresh);
        if (LiveBlocked(current, fresh, PlaybackAvailable(out var reason), reason) is { } blocked)
        {
            StopLive();
            liveError = blocked;
            return;
        }
        if (decode.DecodedKey is { } decodedKey && decodedKey != MotionDecode.KeyOf(fresh))
        {
            // Only the motion changed (a motion from Blender put in or taken out, another skeleton choice): it is read, and the display starts again on it.
            if (MotionDecode.ClipPart(decodedKey) == MotionDecode.ClipPart(MotionDecode.KeyOf(fresh)))
                decode.Ensure(fresh);
            else
            {
                StopLive();
                liveError = "The clip changed. Turn Live on again.";
                return;
            }
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
                if (livePreview is { } built)
                {
                    // The preview plays on: Live swaps its copy in where the built file plays.
                    if (!session.Playback.Active && !session.Playback.Play(built.Timeline, built.Fingerprint, built.Actor))
                    {
                        var error = session.Playback.Error;
                        StopLive();
                        liveError = error;
                        return;
                    }
                    if (!StartDisplay(current, fresh, decoded, built.Actor))
                        return;
                    liveStarted = true;
                    return;
                }
                if (session.Playback.ActorIndex() is not { } actor)
                {
                    StopLive();
                    liveError = "Enter GPose to show the edits live.";
                    return;
                }
                var clip = fresh.Clip;
                // A face plays in the face slot and leaves the body as it is.
                var face = current.Document.Face != null;
                if (!session.Playback.Play(clip.Timeline, fresh.MotionTarget.Clip.BindingFingerprint, actor, face))
                {
                    var error = session.Playback.Error;
                    StopLive();
                    liveError = error;
                    return;
                }
                previewFingerprint = fresh.MotionTarget.Clip.BindingFingerprint;
                previewTimeline = clip.Timeline;
                previewActor = actor;
                previewFace = face;
                previewDocument = null;
                previewSkeleton = null;
                lastSeekFrame = -1;
                if (!StartDisplay(current, fresh, decoded, actor))
                    return;
                liveStarted = true;
                return;
            }
            if (!ReferenceEquals(decoded, liveDecoded))
            {
                // The display keeps the motion it started on: it starts again on the new one, the clip playing on.
                if ((livePreview?.Fingerprint ?? fresh.MotionTarget.Clip.BindingFingerprint) != previewFingerprint || previewActor is not { } shown)
                {
                    StopLive();
                    liveError = "The clip changed. Turn Live on again.";
                }
                else
                    StartDisplay(current, fresh, decoded, shown);
                return;
            }
            session.MotionDisplay.Show(LiveLayers(current.Document), current.Document.ExcludedBones, current.Document.Cuts, LiveLength(current.Document));
        }
        catch (InvalidOperationException e)
        {
            StopLive();
            liveError = e.Message;
        }
    }

    /// <summary>Starts the display of <paramref name="decoded"/> with the document's edits on the actor; false, with Live off and its error, when it can't.</summary>
    private bool StartDisplay(TimelineEditor current, AnimationTimelineSource fresh, MotionClip decoded, ushort actor)
    {
        // Over a built preview the game plays the built file, on the skeleton the motion was decoded for, with nothing to
        // convert. That file holds the layers as they were built, so a copy always shows: the motion with the layers as they are now.
        // An expression's motion is its face's.
        var motion = fresh.MotionTarget;
        if (livePreview is { } built
                ? session.MotionDisplay!.Start(actor, built.Fingerprint, [], decoded, LiveLayers(current.Document), current.Document.ExcludedBones,
                    current.Document.Cuts, LiveLength(current.Document), built.Duration, baseDiffers: true)
                // After a save the game plays either file until it reloads the clip: the one saved or the one before.
                : session.MotionDisplay!.Start(actor, motion.Clip.BindingFingerprint, [motion.FileFingerprint, motion.BaseFingerprint ?? string.Empty],
                decoded, LiveLayers(current.Document), current.Document.ExcludedBones, current.Document.Cuts, LiveLength(current.Document),
                // Edited again, the game plays the saved file, as long as the edit made it.
                motion.BaseMotion != null ? motion.Clip.Duration : null,
                // The game plays the clip's own file; a motion from Blender or a rebuild shows only in a copy.
                baseDiffers: current.Document.Replacement != null || fresh.Rebuild != null,
                // A repair's copy goes onto the character's own skeleton by name, so bones the clip's skeleton lacks show too;
                // a rebuild's onto the clip's own, which the game converts for the character's race.
                playedOn: fresh.Rebuild is { Model: null } ? session.Playback.ActorSkeleton(actor) ?? fresh.Clip.Resolution!.Selected!.Skeleton
                    : fresh.Rebuild != null ? fresh.Clip.Resolution!.Selected!.Skeleton : null,
                playedTracks: fresh.Rebuild != null ? fresh.Clip.Resolution!.TrackBones : default,
                sameRace: fresh.Rebuild is { Model: null }))
        {
            liveDecoded = decoded;
            return true;
        }
        var error = session.MotionDisplay.Error;
        StopLive();
        liveError = error;
        return false;
    }

    /// <summary>
    /// Before Live starts: whether to wait for the character. An ending preview redraws it with its own file first,
    /// and Penumbra reports that redraw done before the character has its skeleton again, so Live waits a while.
    /// </summary>
    private bool WaitingForDraw()
    {
        // A look update redraws the character too (ShowAndReload): Live starts again once it ends.
        if (previewStop is { IsCompleted: false } || lookSync is { IsCompleted: false })
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
        previewManipulations = string.Empty;
        previewRest = null;
        shownLooks = ImmutableDictionary<string, TimelineEffectEdit>.Empty;
        shownSounds = ImmutableDictionary<string, byte[]>.Empty;
        liveLooks = false;
        staleChecked = null;
        lookChecked = null;
        lookPending = null;
        reloadHold = null;
        previewStale = false;
    }

    /// <summary>
    /// Ends Live's temporary mod with looks, when it has one. A look update redrew the character to load them (previewRedrawn),
    /// so it is redrawn again for the effects' own files, however Live ends.
    /// </summary>
    private void StopLiveLooks()
    {
        if (!liveLooks && lookSyncing is not { Live: true })
            return;
        ForgetPreviewLooks();
        var redraw = previewRedrawn ? previewActor : null;
        previewRedrawn = false;
        StopTemporaryMod(redraw);
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
        // Live over a built preview keeps the preview's files: looks and sounds go in beside them, as for the preview.
        var live = liveWanted && liveStarted && livePreview == null;
        var previewing = (!liveWanted || livePreview != null) && previewFingerprint != null && !previewJob && previewBaseFiles != null && previewRest != null;
        if (!live && !previewing)
            return;
        var doc = current.Document;
        if (previewing && !ReferenceEquals(doc, staleChecked) && soundFiles.SoundMissing(doc) == null)
        {
            staleChecked = doc;
            // Only looks or redirected sound changes changed: the preview shows the document once they are in. Live over
            // the preview shows the bone layers and the bones left out too.
            try
            {
                var shownRest = soundFiles.PreviewRest(doc).Document;
                if (livePreview != null)
                    shownRest = shownRest with { Motion = previewRest!.Motion, ExcludedBones = previewRest.ExcludedBones };
                previewStale = !TimelineEdits.Same(shownRest, previewRest!);
            }
            catch (Exception e) when (e is InvalidDataException or ArgumentException) { previewStale = true; }
            if (!previewStale)
                previewDocument = doc;
        }
        if (previewing && previewStale || ReferenceEquals(doc, lookChecked) || previewStop is { IsCompleted: false } || session.Preview.Busy ||
            previewActor is not { } actor || effectLooks.LookMissing(doc) != null || soundFiles.SoundMissing(doc) != null)
            return;
        // Each update redraws the character: an edit is shown once it has settled.
        var now = Environment.TickCount64;
        if (!ReferenceEquals(doc, lookPending))
        {
            lookPending = doc;
            lookPendingAt = now;
            return;
        }
        if (now - lookPendingAt < LookSettleMs)
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
        lookSync = ShowAndReload(actor, files, previewing ? previewManipulations : string.Empty, lookGeneration);
    }

    /// <summary>
    /// Puts <paramref name="files"/> into the temporary mod (Live without looks needs none: it is taken down), then redraws
    /// the character and starts the clip again, as Preview does: the playing timeline keeps the effects and sounds it loaded
    /// when it started, through every loop. Live goes back to the clip's own motion for the redraw and starts its display
    /// again after it (UpdateLive). A stop or a new preview meanwhile (another <paramref name="generation"/>) leaves the character be.
    /// </summary>
    private async Task ShowAndReload(ushort actor, ImmutableDictionary<string, byte[]> files, string manipulations, int generation)
    {
        if (files.Count == 0)
            await session.Preview.StopAsync();
        else
            await session.Preview.ShowAsync(actor, files, manipulations, CancellationToken.None);
        if (!await session.Framework.RunOnFrameworkThread(() => BeforeReload(generation)))
            return;
        if (await session.Preview.RedrawAsync(actor, CancellationToken.None) is { } error)
            session.Log.Warning("The character was not redrawn for the effect looks: {Error}", error);
        await WaitUntilShown(actor, CancellationToken.None);
        await session.Framework.RunOnFrameworkThread(() => AfterReload(generation));
    }

    /// <summary>On the framework thread, before a look update's redraw: false when the preview or Live it was for has ended.</summary>
    private bool BeforeReload(int generation)
    {
        if (generation != lookGeneration)
            return false;
        // A stop redraws the character again to put its own files back.
        previewRedrawn = true;
        // Mid-redraw the clip has no control, which RepeatAtEnd would take for its end.
        repeatArmed = false;
        if (liveWanted)
        {
            // The redraw frees the controls holding Live's copy: they get the clip's own motion back first.
            session.MotionDisplay?.Stop();
            liveStarted = false;
            liveDecoded = null;
        }
        return true;
    }

    /// <summary>On the framework thread, once the redrawn character shows: the preview plays again from the start.</summary>
    private void AfterReload(int generation)
    {
        // Live without a built preview plays the clip's own file again itself, once the character is drawn.
        if (generation != lookGeneration || liveWanted && livePreview == null || previewFingerprint is not { } fingerprint)
            return;
        reloadHold = session.Playback.Active && session.Playback.Paused && lastSeekFrame >= 0 ? lastSeekFrame : null;
        repeatArmed = false;
        PlaybackCall(() => session.Playback.Play(previewTimeline, fingerprint, previewActor, previewFace));
        if (Math.Abs(speedPercent - 100) > .1f && session.Playback.Active)
            PlaybackCall(() => session.Playback.SetSpeed(speedPercent / 100f));
    }

    /// <summary> Whether two sets of files by game path hold the same bytes. </summary>
    private static bool SameFiles(IReadOnlyDictionary<string, byte[]> a, IReadOnlyDictionary<string, byte[]> b) =>
        a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var other) && (ReferenceEquals(pair.Value, other) || pair.Value.AsSpan().SequenceEqual(other)));
}
