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
    private Task<MotionClip>? decodeTask;
    private CancellationTokenSource? decodeCancel;
    private MotionClip? decoded;
    private string? decodedKey;
    private volatile string decodeStatus = "Reading the motion";
    // Why the last read of this version of the clip failed; it is not read again until Live asks or the clip changes.
    private string? decodeError;
    private string? failedDecodeKey;

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
        if (current.Document.LengthChanged)
            return "Live can't show a new length. Use Preview.";
        if (loaded.Clip.Resolution is not { State: SkeletonResolutionState.Matched, Selected: not null })
            return loaded.Clip.Resolution?.Reason ?? "Choose the skeleton this animation was made for under Source first.";
        return null;
    }

    private void StartLive(AnimationTimelineSource loaded)
    {
        StopPreview();
        liveWanted = true;
        liveStarted = false;
        liveError = null;
        // Turning Live on reads a clip again whose last read failed.
        failedDecodeKey = null;
        decodeError = null;
        EnsureDecode(Fresh(loaded));
    }

    /// <summary>Puts the clip's own motion back and ends its playback. Stop, every reload and every save end Live here.</summary>
    private void StopLive()
    {
        if (!liveWanted)
            return;
        liveWanted = false;
        liveStarted = false;
        StopLiveLooks();
        try
        {
            motionDisplay?.Stop();
            if (playback.Active)
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
        PollDecode();
        if (!liveWanted || motionDisplay == null)
            return;
        var fresh = Fresh(loaded);
        if (LiveBlocked(current, fresh, PlaybackAvailable(out var reason), reason) is { } blocked)
        {
            StopLive();
            liveError = blocked;
            return;
        }
        if (decodedKey != null && decodedKey != DecodeKey(fresh))
        {
            StopLive();
            liveError = "The clip changed. Turn Live on again.";
            return;
        }
        if (decoded == null)
            return;
        try
        {
            if (liveStarted && !motionDisplay.Active)
            {
                var ended = motionDisplay.Error;
                StopLive();
                liveError = ended.Length > 0 ? ended : "The live edits stopped.";
                return;
            }
            if (!liveStarted)
            {
                // An ending preview redraws the character with its own file first.
                if (previewStop is { IsCompleted: false })
                    return;
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
                        decoded, current.Document.Motion, current.Document.ExcludedBones))
                {
                    var error = motionDisplay.Error;
                    StopLive();
                    liveError = error;
                    return;
                }
                liveStarted = true;
                return;
            }
            motionDisplay.Show(current.Document.Motion, current.Document.ExcludedBones);
        }
        catch (InvalidOperationException e)
        {
            StopLive();
            liveError = e.Message;
        }
    }

    /// <summary>Identifies a decode: the clip, its file version, the skeleton it is read on and the motion layers apply to.</summary>
    private static string DecodeKey(AnimationTimelineSource loaded) =>
        $"{ClipKey(loaded)}|{loaded.Resource.Hash}|{loaded.Clip.BindingFingerprint}|{loaded.Clip.Resolution?.Selected?.Skeleton.Fingerprint}|{loaded.BaseHash}";

    /// <summary>Starts decoding the clip's motion unless this version of it is decoded or being decoded.</summary>
    private void EnsureDecode(AnimationTimelineSource loaded)
    {
        if (animations is not { } service)
            return;
        var key = DecodeKey(loaded);
        if (key == decodedKey && (decoded != null || decodeTask != null) || key == failedDecodeKey)
            return;
        ForgetDecode();
        decodedKey = key;
        decodeStatus = "Reading the motion";
        decodeCancel = new CancellationTokenSource();
        var token = decodeCancel.Token;
        decodeTask = Task.Run(() => service.DecodeMotionAsync(loaded, message => decodeStatus = message, token), token);
    }

    private void PollDecode()
    {
        if (decodeTask is not { IsCompleted: true } task)
            return;
        decodeTask = null;
        if (task.IsCanceled)
            return;
        if (task.IsFaulted)
        {
            // Remembered so the panels that need the motion say why instead of reading it again every frame.
            failedDecodeKey = decodedKey;
            decodeError = ErrorText(task.Exception);
            decodedKey = null;
            if (liveWanted)
            {
                StopLive();
                liveError = "The motion could not be read: " + decodeError;
            }
            return;
        }
        decoded = task.Result;
    }

    /// <summary>Drops the decoded motion and any decode still running, when the window closes or the clip goes.</summary>
    private void ForgetDecode()
    {
        decodeCancel?.Cancel();
        decodeCancel?.Dispose();
        decodeCancel = null;
        decodeTask = null;
        decoded = null;
        decodedKey = null;
        failedDecodeKey = null;
        decodeError = null;
    }
}
