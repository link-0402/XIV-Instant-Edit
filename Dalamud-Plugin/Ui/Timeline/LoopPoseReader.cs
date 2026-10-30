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

    /// <summary>
    /// Decodes <paramref name="capture"/>'s loop and takes its frames <paramref name="fade"/> and one after: the frame the game
    /// shows as the startup ends, that many frames into its cross-fade (<see cref="MotionFixes.LoopPose"/>). With the
    /// startup's <paramref name="rebuild"/>, the loop is decoded on the same skeleton when <see cref="LoopRebuild"/> allows.
    /// </summary>
    public static async Task<MotionFix> Start(AnimationEditService service, AnimationCapture capture, int fade, RaceRebuild? rebuild, CancellationToken token)
    {
        var loaded = await service.LoadTimelineAsync(capture, false, token).ConfigureAwait(false);
        var clip = await service.DecodeMotionAsync(loaded with { Rebuild = LoopRebuild(loaded, rebuild) }, _ => { }, token).ConfigureAwait(false);
        var pose = MotionFixes.LoopPose(clip, fade);
        return pose.LoopFirst is { IsEmpty: false } ? pose : throw new InvalidDataException("It has no bones.");
    }

    /// <summary>
    /// The startup's <paramref name="rebuild"/> for reading its <paramref name="loop"/>: only when the loop's file is borrowed
    /// the same way and the standard is found for it; a repair (no race) only when the loop's own race has its standard. Null
    /// reads the loop as the game plays it, such as one saved rebuilt already.
    /// </summary>
    internal static RaceRebuild? LoopRebuild(AnimationTimelineSource loop, RaceRebuild? rebuild) =>
        rebuild != null && (rebuild.Model == null || AnimationBones.RebuildRace(loop.Clip, loop.FileModel) == rebuild.Model) &&
        AnimationBones.Standard(loop.Clip, rebuild.Target.Standard, rebuild.Model) != null ? rebuild : null;

    /// <summary>The game fade measured for <paramref name="loaded"/>'s startup into its loop, or null when it never played.</summary>
    public int? MeasuredFade(AnimationTimelineSource loaded) =>
        loaded.Capture.Startup is { } startup ? session.Animations?.Observer.LoopFades.Measured(startup.Timeline, loaded.Capture.Clip.Timeline) : null;

    /// <summary>
    /// Reads <paramref name="loaded"/>'s loop for a new match at its measured fade (else <see cref="LoopFades.Default"/>), or with
    /// <paramref name="layer"/> for that match at <paramref name="fade"/>; nothing while a read runs.
    /// </summary>
    public void Read(AnimationTimelineSource loaded, string? layer, int? fade = null)
    {
        if (session.Animations is { } service)
        {
            var capture = session.WithLoopMatch(loaded);
            var frames = fade ?? MeasuredFade(loaded) ?? LoopFades.Default;
            var rebuild = session.Rebuild;
            Read(loaded, layer, token => Task.Run(() => Start(service, capture, frames, rebuild, token), token));
        }
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
    /// whether it is enabled): Match startup to loop, offered on a startup with a loop, Close loop, offered on a clip the game
    /// loops, Smooth jitter, Hand shape and Reset root turn. A face takes only Smooth jitter.
    /// </summary>
    public void DrawAddItems(TimelineEditor current, bool readOnly, Func<string, bool, bool> item)
    {
        if (current.Document.Face != null)
        {
            if (item(TimelineWindowViews.SmoothJitterName + "###fix-smooth", !readOnly))
                current.AddFix(new MotionFix(MotionFixKind.SmoothJitter));
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(TimelineWindowViews.SmoothJitterHint);
            return;
        }
        var loaded = session.Source;
        var startup = loaded is { Startup: true } && session.Animations != null;
        if (item((Reading ? "Reading the loop" : TimelineWindowViews.MatchLoopName) + "###fix-match", !readOnly && startup && !Reading) && loaded != null)
            Read(loaded, null);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(startup ? TimelineWindowViews.MatchLoopHint : TimelineWindowViews.NoLoop);
        if (item(TimelineWindowViews.CloseLoopName + "###fix-close", !readOnly && current.Loops))
            current.AddFix(new MotionFix(MotionFixKind.CloseLoop));
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(current.Loops ? TimelineWindowViews.CloseLoopHint : TimelineWindowViews.CloseLoopNoLoop);
        if (item(TimelineWindowViews.SmoothJitterName + "###fix-smooth", !readOnly))
            current.AddFix(new MotionFix(MotionFixKind.SmoothJitter));
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(TimelineWindowViews.SmoothJitterHint);
        if (item(TimelineWindowViews.HandShapeName + "###fix-hand", !readOnly))
            current.AddFix(new MotionFix(MotionFixKind.HandShape, Hand: new MotionHand(MotionHandSide.Right, "relaxed")));
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(TimelineWindowViews.HandShapeHint);
        if (item(TimelineWindowViews.ResetRootName + "###fix-reset-root", !readOnly))
            current.AddFix(new MotionFix(MotionFixKind.ResetRoot, Axes: MotionAxes.All));
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(TimelineWindowViews.ResetRootHint);
    }
}
