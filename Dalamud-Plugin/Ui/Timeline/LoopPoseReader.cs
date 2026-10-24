using Dalamud.Bindings.ImGui;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// Match startup to loop and Re-read loop: the open startup's loop decoded on a task, one read at a time, then
/// added as a match or put in the match it was read for. A result for a clip no longer open, or a layer gone,
/// is dropped. Also the Add menus' two fix items.
/// </summary>
internal sealed class LoopPoseReader(TimelineSession session)
{
    private Task<MotionFix>? reading;
    private CancellationTokenSource? cancel;
    // The startup the read is for, and the match it re-reads (null adds one).
    private string? clipKey;
    private string? layerKey;

    /// <summary>Whether a loop is being read.</summary>
    public bool Reading => reading != null;

    /// <summary>Decodes <paramref name="capture"/>'s loop and takes its frames 0 and 1, as the Pose source "Loop's first frame" reads it.</summary>
    public static async Task<MotionFix> Start(AnimationEditService service, AnimationCapture capture, CancellationToken token)
    {
        var loaded = await service.LoadTimelineAsync(capture, false, token).ConfigureAwait(false);
        var clip = await service.DecodeMotionAsync(loaded, _ => { }, token).ConfigureAwait(false);
        var pose = MotionFixes.LoopPose(clip);
        return pose.LoopFirst is { IsEmpty: false } ? pose : throw new InvalidDataException("It has no bones.");
    }

    /// <summary>Reads <paramref name="loaded"/>'s loop for a new match, or with <paramref name="layer"/> for that match; nothing while a read runs.</summary>
    public void Read(AnimationTimelineSource loaded, string? layer)
    {
        if (session.Animations is { } service)
            Read(loaded, layer, token => Task.Run(() => Start(service, loaded.Capture, token), token));
    }

    internal void Read(AnimationTimelineSource loaded, string? layer, Func<CancellationToken, Task<MotionFix>> read)
    {
        if (reading != null)
            return;
        cancel = new CancellationTokenSource();
        clipKey = TimelineSession.ClipKey(loaded);
        layerKey = layer;
        reading = read(cancel.Token);
    }

    /// <summary>
    /// Every frame: a finished read becomes a match in <paramref name="editor"/>, or says why it couldn't. It waits while the
    /// document can't change or a drag or field edit runs, so the result isn't lost or folded into that edit.
    /// </summary>
    public void Poll(TimelineEditor editor)
    {
        if (reading is not { IsCompleted: true } task || session.ReadOnly || editor.Gesture != TimelineGesture.None || editor.History.InGesture)
            return;
        var (clip, layer) = (clipKey, layerKey);
        Drop();
        if (task.IsCanceled || session.Source is not { } open || TimelineSession.ClipKey(open) != clip)
            return;
        if (task.IsFaulted)
            editor.Refuse("Couldn't read the loop: " + TimelineSession.ErrorText(task.Exception));
        else if (layer == null)
            editor.AddFix(task.Result);
        else
            editor.SetLoopPose(layer, task.Result);
    }

    /// <summary>Stops the read and forgets it: the clip changed or the window closed.</summary>
    public void Drop()
    {
        cancel?.Cancel();
        cancel?.Dispose();
        cancel = null;
        reading = null;
    }

    /// <summary>
    /// The Add menus' fix items after Pin feet, drawn by <paramref name="item"/> (a menu item or a button, given its label and
    /// whether it is enabled): Match startup to loop, offered on a startup with a loop, and Smooth jitter.
    /// </summary>
    public void DrawAddItems(TimelineEditor current, bool readOnly, Func<string, bool, bool> item)
    {
        var loaded = session.Source;
        var startup = loaded is { Startup: true } && session.Animations != null;
        if (item((Reading ? "Reading the loop" : TimelineWindowViews.MatchLoopName) + "###fix-match", !readOnly && startup && !Reading) && loaded != null)
            Read(loaded, null);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(startup ? TimelineWindowViews.MatchLoopHint : TimelineWindowViews.NoLoop);
        if (item(TimelineWindowViews.SmoothJitterName + "###fix-smooth", !readOnly))
            current.AddFix(new MotionFix(MotionFixKind.SmoothJitter));
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(TimelineWindowViews.SmoothJitterHint);
    }
}
