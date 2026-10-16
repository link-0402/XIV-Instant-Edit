using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui.Main;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// The clip's motion to Blender and back: Send motion to Blender sends it as the timeline plays it now
/// (cuts and bone layers applied), and Motion from Blender reads the armature's motion back and puts it
/// in place of the clip's, as one undo step. The line under the toolbar says which motion plays.
/// </summary>
internal sealed class BlenderMotionPanel(TimelineSession session, MotionDecode decode)
{
    private const string Menu = "##timeline-blender-menu";

    private AnimationDelivery? delivery;
    private Task<string>? sending;
    // A read's motion, or null when Blender's changes nothing, and whether Blender's scale was left out.
    private Task<(MotionReplacement? Motion, bool ScaleIgnored)>? reading;
    // The clip a read belongs to; the window may show another one by the time it ends.
    private string? readingClip;
    private CancellationTokenSource? cancel;
    // Cancel was pressed: the read ends at its next check.
    private bool stopping;
    private volatile string progress = string.Empty;
    private string? note;
    private bool noteWarns;

    /// <summary>Sends and reads through the main window's connection to Blender.</summary>
    public void Attach(AnimationDelivery animationDelivery) => delivery = animationDelivery;

    /// <summary>A read runs: the document must not change until its motion is in.</summary>
    public bool Working => reading != null;

    /// <summary>Stops a read and drops the last message, when the window closes or another clip opens.</summary>
    public void Forget()
    {
        cancel?.Cancel();
        cancel?.Dispose();
        cancel = null;
        reading = null;
        readingClip = null;
        stopping = false;
        note = null;
    }

    public void DrawButton(TimelineEditor current)
    {
        if (Widgets.IconButton("##timeline-blender", FontAwesomeIcon.ExchangeAlt, "Send the motion to Blender, or bring Blender's back."))
            ImGui.OpenPopup(Menu);
        using var popup = ImRaii.Popup(Menu);
        if (!popup.Success)
            return;
        var problem = Unavailable();
        if (ImGui.MenuItem("Send motion to Blender", "", false, problem == null && sending == null && reading == null))
            Send(current);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(problem ?? "Send the motion as it plays now, with its cuts and bone layers.");
        if (ImGui.MenuItem("Motion from Blender", "", false, problem == null && sending == null && reading == null))
            Read(current);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(problem ?? "Use the armature's animation in Blender as this clip's motion.");
    }

    /// <summary>Why the motion can't go to Blender or come back now; null when it can.</summary>
    private string? Unavailable()
    {
        if (delivery == null || session.Animations == null)
            return "Blender isn't available.";
        if (session.Source is not { } loaded || session.ReadOnly)
            return "Wait for the timeline to finish.";
        if (loaded.Standalone)
            return "An expression has no motion.";
        return decode.DecodedPose(loaded) == null ? "The motion is still being read." : null;
    }

    /// <summary>The motion as the timeline plays it now, made on a task: the decode, cut or stretched, with the layers.</summary>
    private static MotionClip Playing(MotionClip output, IReadOnlyList<MotionLayer> layers, CancellationToken token)
    {
        var playing = output.Clone();
        MotionEvaluator.Apply(playing, layers, token);
        return playing;
    }

    private void Send(TimelineEditor current)
    {
        var loaded = session.Fresh(session.Source!);
        if (decode.DecodedPose(loaded) is not { } decoded)
            return;
        var output = decode.OutputMotion(current, decoded);
        var layers = current.Document.Motion;
        var clip = loaded.Clip;
        var name = AnimationPresentation.ExportName(loaded.Capture, loaded.Startup);
        var facts = new Dictionary<string, string>
        {
            ["gamePath"] = clip.GamePath,
            ["clip"] = clip.Name,
            ["skeleton"] = clip.Resolution?.Selected?.Source.Resource.GamePath ?? "",
            ["mod"] = loaded.Resource.ModName ?? loaded.Resource.ModDirectory ?? "",
            ["timeline"] = "1",
        };
        var target = delivery!;
        note = null;
        progress = "Sending the motion to Blender";
        sending = Task.Run(async () =>
        {
            var take = AnimationTakes.FromMotion(Playing(output, layers, CancellationToken.None), name, facts);
            return await target.DeliverAnimationAsync(take, CancellationToken.None);
        });
    }

    private void Read(TimelineEditor current)
    {
        var loaded = session.Fresh(session.Source!);
        if (decode.DecodedPose(loaded) is not { } decoded || session.Animations is not { } service)
            return;
        var output = decode.OutputMotion(current, decoded);
        var document = current.Document;
        var layers = document.Motion;
        var target = delivery!;
        cancel = new CancellationTokenSource();
        var token = cancel.Token;
        stopping = false;
        readingClip = TimelineSession.ClipKey(loaded);
        note = null;
        progress = "Reading the motion from Blender";
        reading = Task.Run(async () =>
        {
            var playing = Playing(output, layers, token);
            if (playing.Duration <= 0)
                throw new InvalidOperationException("This animation has no length to replace.");
            var rate = (playing.Samples - 1) / (double)playing.Duration;
            var read = await target.ReadMotionAsync(new AnimationReadRequest(rate, loaded.Clip.GamePath, loaded.Clip.Name, playing.Skeleton), token);
            return (await service.ReplaceMotionAsync(loaded, playing, read, document, message => progress = message, token), read.ScaleIgnored);
        }, token);
    }

    /// <summary>Cancel on the progress line: the read stops at its next check, and its motion, if it gets one, is dropped.</summary>
    private void Stop()
    {
        stopping = true;
        cancel?.Cancel();
    }

    /// <summary>Every frame: takes a finished send or read; a read's motion goes in as one undo step.</summary>
    private void Poll(TimelineEditor current)
    {
        if (sending is { IsCompleted: true } sent)
        {
            sending = null;
            if (sent.IsCompletedSuccessfully)
                Message(sent.Result, false);
            else
                Message("The motion was not sent: " + TimelineSession.ErrorText(sent.Exception), true);
        }
        if (reading is not { IsCompleted: true } read)
            return;
        reading = null;
        cancel?.Dispose();
        cancel = null;
        var stopped = stopping;
        stopping = false;
        if (stopped || read.IsCanceled)
            Message("Reading stopped.", false);
        else if (!read.IsCompletedSuccessfully)
            Message("The motion was not read: " + TimelineSession.ErrorText(read.Exception), true);
        else if (session.Source is not { } now || TimelineSession.ClipKey(now) != readingClip)
            Message("The clip changed while reading. Read it again.", true);
        else if (read.Result.Motion is not { } replacement)
            Message(TimelineWindowViews.BlenderUnchangedLine(read.Result.ScaleIgnored), false);
        else if (session.ReadOnly || !current.ReplaceMotion(replacement))
            Message("The timeline is busy. Read it again.", true);
    }

    private void Message(string text, bool warns)
    {
        note = text;
        noteWarns = warns;
    }

    /// <summary>The line under the toolbar: a send or read running, how the last one went, and which motion plays.</summary>
    public void DrawStatus(TimelineEditor current)
    {
        Poll(current);
        if (sending != null || reading != null)
        {
            Widgets.Spinner();
            ImGui.SameLine(0, Theme.Gap);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Muted, stopping ? "Stopping" : progress);
            if (reading != null && !stopping)
            {
                ImGui.SameLine(0, Theme.Gap);
                if (ImGui.SmallButton("Cancel##timeline-blender-cancel"))
                    Stop();
            }
        }
        else if (note is { } text && TimelineUi.Note("##timeline-blender-note", noteWarns ? Theme.Warning : Theme.Muted, text))
            note = null;
        if (current.Document.Replacement is not { } replacement)
            return;
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Label, TimelineWindowViews.BlenderMotionLine(replacement));
        ImGui.SameLine(0, Theme.Gap);
        using (ImRaii.Disabled(session.ReadOnly))
        {
            if (ImGui.SmallButton("Use the clip's motion"))
                current.DropReplacement();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Go back to the animation's own motion. Ctrl+Z works too.");
    }
}
