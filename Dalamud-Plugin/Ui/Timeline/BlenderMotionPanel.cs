using Dalamud.Bindings.ImGui;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui.Main;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// The clip's motion to Blender and back: Send motion to Blender sends it as the timeline plays it now
/// (cuts and bone layers applied), and Motion from Blender reads the armature's motion back and puts it
/// in place of the clip's, as one undo step. A still pose takes the clip's place the same way, to start
/// it from scratch, and so do a face's holds (still or moving) and its neutral face; the Animation menu
/// offers them. The status line says which motion plays.
/// </summary>
internal sealed class BlenderMotionPanel(TimelineSession session, MotionDecode decode)
{
    /// <summary>What a running read makes: a motion from Blender, a still pose, a face's hold or its neutral face.</summary>
    private enum Hold : byte { Blender, Still, Face, Neutral }

    private AnimationDelivery? delivery;
    private Task<string>? sending;
    // A read's motion, or null when Blender's changes nothing, and whether Blender's scale was left out.
    private Task<(MotionReplacement? Motion, bool ScaleIgnored)>? reading;
    // The clip a read belongs to; the window may show another one by the time it ends.
    private string? readingClip;
    private CancellationTokenSource? cancel;
    // Cancel was pressed: the read ends at its next check.
    private bool stopping;
    // What the running read makes.
    private Hold hold;
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

    /// <summary> The Animation menu's Send motion to Blender and Motion from Blender, each with why it is unavailable as its tooltip. </summary>
    public void DrawMenuItems(TimelineEditor current)
    {
        var problem = Unavailable();
        if (ImGui.MenuItem("Send motion to Blender", "", false, problem == null && sending == null && reading == null))
            Send(current);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(problem ?? "Send the motion as it plays now, with its cuts and bone layers.");
        // ReplaceMotionAsync refuses a rebuilt or repaired clip only after Blender's motion was read.
        var rebuilt = session.Rebuild != null ? "Choose Keep its own under Skeleton first. A motion from Blender can't go onto a rebuilt or repaired clip yet." : null;
        if (ImGui.MenuItem("Motion from Blender", "", false, (problem ?? rebuilt) == null && sending == null && reading == null))
            Read(current);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(problem ?? rebuilt ?? "Use the armature's animation in Blender as this clip's motion.");
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

    /// <summary>Why the clip can't start from a still pose now; null when it can.</summary>
    public string? StillUnavailable(TimelineEditor current)
    {
        if (session.Animations == null || session.Source is not { } loaded || session.ReadOnly)
            return "Wait for the timeline to finish.";
        if (loaded.Standalone || current.FixedLength)
            return "An expression has no motion.";
        if (current.Document.Face != null)
            return "A face starts from Neutral face.";
        if (reading != null)
            return "Wait for the motion being read.";
        if (session.Rebuild != null)
            return "Choose Keep its own under Skeleton first. A still pose can't go onto a rebuilt or repaired clip yet.";
        return decode.DecodedPose(loaded) == null ? "The motion is still being read." : null;
    }

    /// <summary>
    /// Start from a still pose: the frame at the playhead, as the timeline plays it now, held for the clip's
    /// length becomes its motion; a pose layer over it then takes the keys (see <see cref="TimelineEditor.HoldStill"/>).
    /// </summary>
    public void HoldStill(TimelineEditor current)
    {
        var loaded = session.Fresh(session.Source!);
        if (decode.DecodedPose(loaded) is not { } decoded || session.Animations is not { } service)
            return;
        var output = decode.OutputMotion(current, decoded);
        var layers = current.Document.Motion;
        var frame = current.Playhead;
        var frames = current.Document.Length;
        cancel = new CancellationTokenSource();
        var token = cancel.Token;
        stopping = false;
        hold = Hold.Still;
        readingClip = TimelineSession.ClipKey(loaded);
        note = null;
        progress = "Holding the pose";
        reading = Task.Run(async () =>
        {
            var playing = Playing(output, layers, token);
            return ((MotionReplacement?)await service.HoldStillAsync(loaded, playing, frame, frames, message => progress = message, token), false);
        }, token);
    }

    /// <summary>Why the face's length or its neutral face can't be set now; null when it can.</summary>
    public string? FaceUnavailable(TimelineEditor current)
    {
        if (session.Animations == null || session.Source is not { } loaded || session.ReadOnly)
            return "Wait for the timeline to finish.";
        if (current.Document.Face == null || loaded.MotionTarget.Standalone)
            return loaded.FaceProblem ?? "This timeline has no face.";
        if (reading != null)
            return "Wait for the motion being read.";
        return decode.DecodedPose(loaded) == null ? "The face is still being read." : null;
    }

    /// <summary>
    /// A face's motion held for <paramref name="frames"/> frames (1 makes it still): its first frame, without the layers,
    /// which stay (see <see cref="TimelineEditor.HoldFace"/>).
    /// </summary>
    public void HoldFace(TimelineEditor current, int frames) => StartFace(current, Hold.Face, frames, "Holding the face");

    /// <summary>Starts the face from the neutral face, at its length (see <see cref="TimelineEditor.NeutralFace"/>).</summary>
    public void NeutralFace(TimelineEditor current) => StartFace(current, Hold.Neutral, current.Document.Face?.Frames ?? 1, "Setting the neutral face");

    private void StartFace(TimelineEditor current, Hold kind, int frames, string text)
    {
        var loaded = session.Fresh(session.Source!);
        if (decode.DecodedPose(loaded) is not { } decoded || session.Animations is not { } service)
            return;
        // The face's own motion, its first frame and tracks as decoded (not held for the timeline): the layers stay on top of what it becomes.
        var output = decoded;
        cancel = new CancellationTokenSource();
        var token = cancel.Token;
        stopping = false;
        hold = kind;
        readingClip = TimelineSession.ClipKey(loaded);
        note = null;
        progress = text;
        reading = Task.Run(async () => ((MotionReplacement?)(kind == Hold.Neutral
            ? await service.NeutralFaceAsync(loaded, output, frames, message => progress = message, token)
            : await service.HoldStillAsync(loaded, output, 0, frames, message => progress = message, token)), false), token);
    }

    private void Read(TimelineEditor current)
    {
        hold = Hold.Blender;
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

    /// <summary>
    /// Every frame, also while the start page shows: takes a finished send or read; a read's motion goes in as one undo step.
    /// </summary>
    public void Poll(TimelineEditor current)
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
            Message((hold switch { Hold.Blender => "The motion was not read: ", Hold.Neutral => "The neutral face was not set: ", _ => "The pose was not held: " }) +
                    TimelineSession.ErrorText(read.Exception), true);
        else if (session.Source is not { } now || TimelineSession.ClipKey(now) != readingClip)
            Message("The clip changed while reading. Read it again.", true);
        else if (read.Result.Motion is not { } replacement)
            Message(TimelineWindowViews.BlenderUnchangedLine(read.Result.ScaleIgnored), false);
        else if (session.ReadOnly || !(hold switch
                 {
                     Hold.Still => current.HoldStill(replacement),
                     Hold.Face => current.HoldFace(replacement),
                     Hold.Neutral => current.NeutralFace(replacement),
                     _ => current.ReplaceMotion(replacement),
                 }))
            Message(hold == Hold.Blender ? "The timeline is busy. Read it again." : "The timeline is busy. Try again.", true);
        else if (hold == Hold.Still)
            Message("Holding the pose. Key a pose or pose bones in the new pose layer.", false);
        else if (hold == Hold.Neutral)
            Message("Neutral face. Key the new face in the pose layer.", false);
    }

    private void Message(string text, bool warns)
    {
        note = text;
        noteWarns = warns;
    }

    /// <summary>
    /// The status line's notes: a send or read running, how the last one went, and which motion plays. <see cref="Poll"/>
    /// takes a finished one.
    /// </summary>
    public IEnumerable<StatusNote> Notes(TimelineEditor current)
    {
        var notes = new List<StatusNote>(2);
        if (sending != null || reading != null)
            notes.Add(reading != null && !stopping
                ? new StatusNote("blender", StatusKind.Working, progress, "Cancel", Stop)
                : new StatusNote("blender", StatusKind.Working, stopping ? "Stopping" : progress));
        else if (note is { } text)
            notes.Add(new StatusNote("blender", noteWarns ? StatusKind.Warning : StatusKind.Notice, text));
        if (current.Document.Replacement is { } replacement)
            // A face's hold goes back with Ctrl+Z.
            notes.Add(current.Document.Face != null
                ? new StatusNote("blender-motion", StatusKind.Notice, TimelineWindowViews.BlenderMotionLine(replacement))
                : new StatusNote("blender-motion", StatusKind.Notice, TimelineWindowViews.BlenderMotionLine(replacement), "Use the clip's motion",
                    () => current.DropReplacement(), ButtonTooltip: "Go back to the animation's own motion. Ctrl+Z works too.", ButtonEnabled: !session.ReadOnly));
        return notes;
    }
}
