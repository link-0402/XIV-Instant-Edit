using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// The open clip's motion, decoded on its source skeleton once per file on a task: what Live shows
/// the layers over, what the bone editor, the clip band and the cut panels measure, and the line each
/// of them shows while it is read or why it failed.
/// </summary>
internal sealed class MotionDecode(TimelineSession session)
{
    private Task<MotionClip>? task;
    private CancellationTokenSource? cancel;
    private volatile string status = "Reading the motion";
    // Why the last read of this version of the clip failed; it is not read again until Live asks or the clip changes.
    private string? failedKey;
    // The decoded motion as the document last cut or stretched it (see OutputMotion).
    private (MotionClip Decoded, MotionCuts? Cuts, int Length, MotionClip Output)? outputMotion;

    /// <summary>The decoded motion; null until read.</summary>
    public MotionClip? Decoded { get; private set; }

    /// <summary>The <see cref="KeyOf"/> of the motion decoded or being decoded.</summary>
    public string? DecodedKey { get; private set; }

    /// <summary>How far the read is.</summary>
    public string Status => status;

    /// <summary>Why the last read failed.</summary>
    public string? Error { get; private set; }

    /// <summary>A read is running.</summary>
    public bool Reading => task != null;

    /// <summary>Identifies a decode: the clip, its file version, the skeleton it is read on and the motion layers apply to.</summary>
    public static string KeyOf(AnimationTimelineSource loaded) =>
        $"{TimelineSession.ClipKey(loaded)}|{loaded.Resource.Hash}|{loaded.Clip.BindingFingerprint}|{loaded.Clip.Resolution?.Selected?.Skeleton.Fingerprint}|{loaded.BaseHash}";

    /// <summary>Whether the last read failed for <paramref name="loaded"/> as it is now.</summary>
    public bool FailedFor(AnimationTimelineSource loaded) => Error != null && failedKey == KeyOf(session.Fresh(loaded));

    /// <summary>Starts decoding the clip's motion unless this version of it is decoded or being decoded.</summary>
    public void Ensure(AnimationTimelineSource loaded)
    {
        if (session.Animations is not { } service)
            return;
        var key = KeyOf(loaded);
        if (key == DecodedKey && (Decoded != null || task != null) || key == failedKey)
            return;
        Forget();
        DecodedKey = key;
        status = "Reading the motion";
        cancel = new CancellationTokenSource();
        var token = cancel.Token;
        task = Task.Run(() => service.DecodeMotionAsync(loaded, message => status = message, token), token);
    }

    /// <summary>Lets a clip whose last read failed be read again.</summary>
    public void ReadAgain()
    {
        failedKey = null;
        Error = null;
    }

    /// <summary>Every frame: takes a finished read. True when it failed just now.</summary>
    public bool Poll()
    {
        if (task is not { IsCompleted: true } done)
            return false;
        task = null;
        if (done.IsCanceled)
            return false;
        if (done.IsFaulted)
        {
            // Remembered so the panels that need the motion say why instead of reading it again every frame.
            failedKey = DecodedKey;
            Error = TimelineSession.ErrorText(done.Exception);
            DecodedKey = null;
            return true;
        }
        Decoded = done.Result;
        return false;
    }

    /// <summary>Drops the decoded motion and any decode still running, when the window closes or the clip goes.</summary>
    public void Forget()
    {
        cancel?.Cancel();
        cancel?.Dispose();
        cancel = null;
        task = null;
        Decoded = null;
        DecodedKey = null;
        failedKey = null;
        Error = null;
    }

    /// <summary>The decoded motion when it is this clip's current one; null while it is read, which this starts.</summary>
    public MotionClip? DecodedPose(AnimationTimelineSource loaded)
    {
        var fresh = session.Fresh(loaded);
        if (Decoded != null && DecodedKey == KeyOf(fresh))
            return Decoded;
        Ensure(fresh);
        return null;
    }

    /// <summary>
    /// <paramref name="clip"/> as the document's cuts and length play it, as Live and a save cut it before the
    /// bone layers; the clip itself when neither changes it. During a drag the last cut stands until it ends.
    /// </summary>
    public MotionClip OutputMotion(TimelineEditor current, MotionClip clip)
    {
        var doc = current.Document;
        if (!doc.Cut && !doc.Stretched)
            return clip;
        if (outputMotion is { } cached && ReferenceEquals(cached.Decoded, clip) &&
            (ReferenceEquals(cached.Cuts, doc.Cuts) && cached.Length == doc.Length || current.Gesture != TimelineGesture.None))
            return cached.Output;
        try
        {
            var whole = (int)Math.Round(clip.Duration * TimelineDocument.Fps);
            var output = MotionCutter.Apply(clip, doc.Cuts ?? MotionCuts.WholeOf(whole), doc.Length);
            outputMotion = (clip, doc.Cuts, doc.Length, output);
            return output;
        }
        catch (InvalidDataException)
        {
            return clip;
        }
    }

    /// <summary> In place of what needs the clip's motion: the reading's progress, or why it failed. </summary>
    public void DrawWait()
    {
        if (Error is { } error)
        {
            using var wrap = ImRaii.TextWrapPos(0f);
            ImGui.TextColored(Theme.Warning, "The motion could not be read: " + error);
            return;
        }
        Widgets.Spinner();
        ImGui.SameLine(0, Theme.Gap);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, status);
    }

    /// <summary>While the motion is read, how far along; why it failed when it did.</summary>
    public void DrawNote()
    {
        if (session.Source is { } loaded && FailedFor(loaded))
            ImGui.TextColored(Theme.Warning, "The motion could not be read: " + Error);
        else
            ImGui.TextColored(Theme.Muted, status);
    }
}
