using System.Collections.Immutable;
using System.Numerics;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Preview;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui.Timeline;

namespace InstantEdit.Ui;

/// <summary>
/// Live: the clip's own file plays on the GPose actor and its bone layers show over it, swapped in
/// by <see cref="MotionLiveDisplay"/> whenever the document changes. The clip's motion is decoded on
/// its source skeleton once per file, on a task. Preview stays the exact check: it builds the whole
/// file, sounds, effects and a new length included.
/// </summary>
public sealed partial class TimelineWindow
{
    private bool liveWanted;
    private bool liveStarted;
    private string? liveError;
    // Until when Live waits for the GPose character to be drawn again, as after the redraw that ends a preview.
    private DateTime? liveDrawWait;
    private static readonly TimeSpan LiveDrawWait = TimeSpan.FromSeconds(10);

    /// <summary>Why Live can't start now; null when it can.</summary>
    private string? LiveBlocked(TimelineEditor current, AnimationTimelineSource loaded, bool available, string reason)
    {
        if (motionDisplay == null)
            return "Live edits are unavailable.";
        if (loaded.Standalone)
            return "An expression's face can't take bone layers yet.";
        if (!available)
            return reason;
        if (ReadOnly)
            return "Wait for the save to finish.";
        if (loaded.Clip.Resolution is not { State: SkeletonResolutionState.Matched, Selected: not null })
            return loaded.Clip.Resolution?.Reason ?? "Choose the skeleton this animation was made for under Source first.";
        return null;
    }

    /// <summary>The length Live plays the motion at: the document's when cut or stretched, else 0 for the clip's own.</summary>
    private static int LiveLength(TimelineDocument doc) => doc.Cut || doc.Stretched ? doc.Length : 0;

    private void StartLive(AnimationTimelineSource loaded)
    {
        StopPreview();
        liveWanted = true;
        liveStarted = false;
        liveError = null;
        liveDrawWait = null;
        // Turning Live on reads a clip again whose last read failed.
        decode.ReadAgain();
        decode.Ensure(Fresh(loaded));
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
            motionDisplay?.Stop();
            if (playback.Active && !keepPlaying)
                playback.Stop();
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
        if (!liveWanted || motionDisplay == null)
            return;
        var fresh = Fresh(loaded);
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
            if (liveStarted && !motionDisplay.Active)
            {
                // The display ended itself, such as on a skeleton without a bone the layers move: the clip plays on.
                var ended = motionDisplay.Error;
                StopLive(keepPlaying: true);
                liveError = ended.Length > 0 ? ended : "The live edits stopped.";
                return;
            }
            if (!liveStarted)
            {
                if (playback.ActorIndex() is not { } actor)
                {
                    StopLive();
                    liveError = "Enter GPose to show the edits live.";
                    return;
                }
                var clip = fresh.Clip;
                if (!playback.Play(clip.Timeline, clip.BindingFingerprint, actor))
                {
                    var error = playback.Error;
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
                if (!motionDisplay.Start(actor, clip.BindingFingerprint, [fresh.FileFingerprint, fresh.BaseFingerprint ?? string.Empty],
                        decoded, current.Document.Motion, current.Document.ExcludedBones, current.Document.Cuts, LiveLength(current.Document),
                        // Edited again, the game plays the saved file, as long as the edit made it.
                        fresh.BaseMotion != null ? fresh.Clip.Duration : null))
                {
                    var error = motionDisplay.Error;
                    StopLive();
                    liveError = error;
                    return;
                }
                liveStarted = true;
                return;
            }
            motionDisplay.Show(current.Document.Motion, current.Document.ExcludedBones, current.Document.Cuts, LiveLength(current.Document));
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
        try { actor = playback.ActorIndex(); }
        catch (InvalidOperationException) { return false; }
        if (actor is not { } index || playback.Drawn(index))
        {
            liveDrawWait = null;
            return false;
        }
        return (liveDrawWait ??= DateTime.UtcNow + LiveDrawWait) > DateTime.UtcNow;
    }
}
