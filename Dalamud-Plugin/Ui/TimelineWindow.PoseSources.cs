using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Preview;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Ui.Pose;
using InstantEdit.Ui.Timeline;

namespace InstantEdit.Ui;

/// <summary>
/// Key pose sources for pose layers: a .pose file from Anamnesis, Ktisis or Brio, the character's pose as
/// the game shows it now, and a frame of an animation (a startup's loop among them). A source is read on
/// a task and waits in the panel, where it is keyed at the playhead for the whole body or the selected
/// bones: rotations always, positions for the bones it moves when it was made on a body of the same
/// proportions. Shared by the properties panel and the Bones window.
/// </summary>
public sealed partial class TimelineWindow
{
    private enum PoseSourceKind : byte { File, Character, Clip }

    /// <summary>A pose waiting to be keyed: a pose file, a character's pose or a clip's motion, once read.</summary>
    private sealed class PoseSource
    {
        public required PoseSourceKind Kind { get; init; }
        public required string Name { get; set; }
        public Task<object>? Reading { get; set; }
        public volatile string Status = "Reading the pose";
        public string? Error { get; set; }
        public PoseFile? File { get; set; }
        public AnimationTake? Take { get; set; }
        public MotionClip? Clip { get; set; }
        /// <summary>The clip's frame the pose is taken from.</summary>
        public int ClipFrame { get; set; }
        /// <summary>Keys at the clip's end rather than at the playhead: a startup's loop's first frame.</summary>
        public bool AtEnd { get; init; }
    }

    // A whole-body pose leaves these groups out unless they are ticked: physics and gear move them on their own.
    private static readonly string[] PoseGroupsLeftOut = ["Hair", "Skirt", "Armor and weapons"];

    private readonly AnimationRecorder recorder;
    private PoseSource? poseSource;
    private CancellationTokenSource? poseCancel;
    private bool poseSelectedOnly;
    // Left-out groups ticked back in.
    private readonly HashSet<string> poseGroupsKept = new(StringComparer.Ordinal);
    // Whether positions are keyed; null until chosen for the waiting pose, which then decides.
    private bool? posePositions;
    private string? poseKeyed;
    private string? lastPoseFolder;
    // The waiting pose made local on one skeleton, for a key frame and a frame of its clip.
    private (PoseSource Source, string Skeleton, int Frame, int ClipFrame, PoseImport Import)? poseImport;

    /// <summary>
    /// "Key a pose": the menu of pose sources, then the pose waiting to be keyed into <paramref name="layer"/>,
    /// a pose layer.
    /// </summary>
    private void DrawPoseSources(TimelineEditor current, MotionLayer layer, SkeletonDescription skeleton, AnimationTimelineSource loaded,
        bool readOnly)
    {
        PollPoseSource();
        using (ImRaii.Disabled(readOnly))
        {
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.FileImport, "Key a pose", null, null, null, null))
                ImGui.OpenPopup("##pose-sources");
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Key a whole pose at the playhead: from a pose file, the character as posed now, or a frame of an animation.");
        DrawPoseSourceMenu(loaded);
        if (poseSource is { } staged)
            DrawWaitingPose(current, layer, skeleton, loaded, staged, readOnly);
    }

    private void DrawPoseSourceMenu(AnimationTimelineSource loaded)
    {
        using var popup = ImRaii.Popup("##pose-sources");
        if (!popup.Success)
            return;
        if (ImGui.MenuItem("Pose file"))
            OpenPoseFile();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("A .pose file from Anamnesis, Ktisis or Brio.");
        if (ImGui.MenuItem("Character pose"))
            CapturePose();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("The pose your character shows now. In GPose, the GPose character's, posing tools included.");
        using (var menu = ImRaii.Menu("Frame of an animation"))
        {
            if (menu.Success)
            {
                var any = false;
                foreach (var (capture, startup) in PoseClips())
                {
                    any = true;
                    var mine = TimelineSession.ClipKey(loaded) == TimelineSession.ClipKey(capture, startup);
                    if (ImGui.MenuItem($"{AnimationPresentation.AnimationName(capture, startup)}{(mine ? " (this one)" : "")}##{capture.Id}-{startup}"))
                        ReadClipPose(capture, startup, false);
                }
                if (!any)
                {
                    using (ImRaii.Disabled())
                        ImGui.MenuItem("No animations seen yet");
                }
            }
        }
        if (loaded.Startup && ImGui.MenuItem("Loop's first frame, at the end"))
            ReadClipPose(loaded.Capture, false, true);
        if (loaded.Startup && ImGui.IsItemHovered())
            ImGui.SetTooltip("Key the loop's first pose at this startup's end, so the startup ends where the loop begins.");
    }

    /// <summary>The animations the listener has seen, loops and startups, without expressions: a frame of any of them can be keyed.</summary>
    private IEnumerable<(AnimationCapture Capture, bool Startup)> PoseClips()
    {
        if (animations is not { } service)
            yield break;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var capture in service.Observer.History)
        {
            if (capture.ExpressionTimeline != null)
                continue;
            if (seen.Add(TimelineSession.ClipKey(capture, false)))
                yield return (capture, false);
            if (capture.Startup != null && seen.Add(TimelineSession.ClipKey(capture, true)))
                yield return (capture, true);
        }
    }

    private void OpenPoseFile()
    {
        var folder = lastPoseFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        fileDialog.OpenFileDialog("Choose a pose", "Poses{.pose}", (success, files) =>
        {
            if (!success || files.Count == 0)
                return;
            var path = files[0];
            lastPoseFolder = Path.GetDirectoryName(path);
            Stage(new PoseSource { Kind = PoseSourceKind.File, Name = Path.GetFileName(path) }, _ => Task.Run<object>(() =>
            {
                if (new FileInfo(path).Length > PoseFiles.MaximumBytes)
                    throw new InvalidDataException("The file is too large for a pose.");
                return PoseFiles.Parse(System.IO.File.ReadAllBytes(path));
            }));
        }, 1, folder, false);
    }

    /// <summary>Reads the character's pose: in GPose the GPose target, else the posed player; outside GPose your character.</summary>
    private void CapturePose()
    {
        var subject = RecordingSubject.Self;
        try
        {
            if (playback.ActorIndex() is { } index && index != AnimationPlayback.GPosePlayerIndex)
                subject = RecordingSubject.Target;
        }
        catch (InvalidOperationException)
        {
        }
        Stage(new PoseSource { Kind = PoseSourceKind.Character, Name = "Character pose" },
            token => Task.Run<object>(async () => await recorder.CapturePoseAsync(subject, token).ConfigureAwait(false), token));
    }

    /// <summary>Reads a clip's motion to take a frame from; this window's own clip is read already.</summary>
    private void ReadClipPose(AnimationCapture capture, bool startup, bool atEnd)
    {
        var staged = new PoseSource { Kind = PoseSourceKind.Clip, Name = AnimationPresentation.AnimationName(capture, startup), AtEnd = atEnd };
        if (source is { } current && TimelineSession.ClipKey(current) == TimelineSession.ClipKey(capture, startup) && decode.DecodedPose(current) is { } own)
        {
            staged.Clip = own;
            Stage(staged, null);
            return;
        }
        if (animations is not { } service)
            return;
        Stage(staged, token => Task.Run<object>(async () =>
        {
            var loaded = await service.LoadTimelineAsync(capture, startup, token).ConfigureAwait(false);
            return await service.DecodeMotionAsync(loaded, status => staged.Status = status, token).ConfigureAwait(false);
        }, token));
    }

    /// <summary>Makes <paramref name="staged"/> the waiting pose, starting its reading; the one before is dropped.</summary>
    private void Stage(PoseSource staged, Func<CancellationToken, Task<object>>? read)
    {
        DropPose();
        poseCancel = new CancellationTokenSource();
        poseSource = staged;
        posePositions = null;
        poseKeyed = null;
        if (read != null)
            staged.Reading = read(poseCancel.Token);
    }

    /// <summary>Puts the waiting pose away and stops its reading.</summary>
    private void DropPose()
    {
        poseCancel?.Cancel();
        poseCancel?.Dispose();
        poseCancel = null;
        poseSource = null;
        poseImport = null;
    }

    private void PollPoseSource()
    {
        if (poseSource is not { Reading: { IsCompleted: true } task } staged)
            return;
        staged.Reading = null;
        if (task.IsCanceled)
        {
            staged.Error = "The reading was stopped.";
            return;
        }
        if (task.IsFaulted)
        {
            staged.Error = TimelineSession.ErrorText(task.Exception);
            return;
        }
        switch (task.Result)
        {
            case PoseFile file:
                staged.File = file;
                if (file.Tool.Length > 0)
                    staged.Name += $" ({file.Tool})";
                break;
            case AnimationTake take:
                staged.Take = take;
                if (take.Source.TryGetValue("character", out var character) && character.Length > 0)
                    staged.Name = $"{character}'s pose";
                break;
            case MotionClip clip:
                staged.Clip = clip;
                break;
        }
    }

    /// <summary>
    /// The waiting pose: what it is, the clip frame it is taken from, which bones it keys and whether their
    /// positions go with them, and the Key button for the playhead (or the clip's end).
    /// </summary>
    private void DrawWaitingPose(TimelineEditor current, MotionLayer layer, SkeletonDescription skeleton, AnimationTimelineSource loaded,
        PoseSource staged, bool readOnly)
    {
        ImGui.Spacing();
        Widgets.Icon(staged.Kind switch
        {
            PoseSourceKind.File => FontAwesomeIcon.FileAlt,
            PoseSourceKind.Character => FontAwesomeIcon.User,
            _ => FontAwesomeIcon.Film,
        }, Theme.Label);
        ImGui.SameLine(0, Theme.Gap);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Label, staged.Name);
        ImGui.SameLine(0, Theme.Gap);
        if (Widgets.GhostIconButton("##pose-drop", FontAwesomeIcon.Times, "Put the pose away"))
        {
            DropPose();
            return;
        }
        if (staged.Reading != null)
        {
            Widgets.Spinner();
            ImGui.SameLine(0, Theme.Gap);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Muted, staged.Status);
            return;
        }
        if (staged.Error is { } error)
        {
            using var wrap = ImRaii.TextWrapPos(0f);
            ImGui.TextColored(Theme.Warning, error);
            return;
        }
        if (staged.Clip is { } clip)
        {
            var last = Math.Max(0, (int)MathF.Round(clip.Duration * TimelineDocument.Fps));
            var at = Math.Clamp(staged.ClipFrame, 0, last);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Muted, "Frame");
            ImGui.SameLine(0, Theme.Gap);
            ImGui.SetNextItemWidth(Math.Max(Theme.Scaled(120), ImGui.GetContentRegionAvail().X - Theme.Scaled(60)));
            if (ImGui.SliderInt("##pose-clip-frame", ref at, 0, last))
                staged.ClipFrame = Math.Clamp(at, 0, last);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("The frame of the animation the pose is taken from. Ctrl+click to type.");
            ImGui.SameLine(0, Theme.Gap);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Muted, TimelineText.Seconds(staged.ClipFrame));
        }

        // A startup's end is its last frame, where its loop takes over.
        var frame = staged.AtEnd ? current.Document.Length : current.Playhead;
        PoseImport? import;
        try { import = PoseImportFor(staged, skeleton, loaded, frame); }
        catch (InvalidDataException e)
        {
            using var wrap = ImRaii.TextWrapPos(0f);
            ImGui.TextColored(Theme.Warning, e.Message);
            return;
        }
        if (import == null)
        {
            decode.DrawWait();
            return;
        }

        // Which bones: the whole body (but the left-out groups), or the selected ones.
        List<string> available = [.. skeleton.Bones.Select(bone => bone.Name).Where(import.Local.ContainsKey)];
        if (ImGui.RadioButton("Whole body###pose-whole", !poseSelectedOnly))
            poseSelectedOnly = false;
        ImGui.SameLine();
        using (ImRaii.Disabled(posing.Selection.Count == 0))
        {
            if (ImGui.RadioButton($"Selected bones ({selection.Count})###pose-selected", poseSelectedOnly && posing.Selection.Count > 0))
                poseSelectedOnly = true;
        }
        var selectedOnly = poseSelectedOnly && posing.Selection.Count > 0;
        if (!selectedOnly)
        {
            var groups = PoseMap.Groups.Where(group => PoseGroupsLeftOut.Contains(group.Name) && available.Any(group.Contains)).ToList();
            if (groups.Count > 0)
            {
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(Theme.Muted, "Also");
                foreach (var group in groups)
                {
                    ImGui.SameLine(0, Theme.Gap);
                    var kept = poseGroupsKept.Contains(group.Name);
                    if (ImGui.Checkbox($"{group.Name}##pose-also", ref kept))
                    {
                        if (kept)
                            poseGroupsKept.Add(group.Name);
                        else
                            poseGroupsKept.Remove(group.Name);
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Physics and gear move these on their own. Tick to key them too.");
                }
            }
        }
        var leftOut = PoseMap.Groups.Where(group => PoseGroupsLeftOut.Contains(group.Name) && !poseGroupsKept.Contains(group.Name));
        var bones = PoseEdits.PoseBones(available, selectedOnly ? posing.Selection.Bones : null, leftOut);

        // Positions: off for a pose made on a body of other proportions, unless asked for.
        var moving = bones.Count(import.Moved.Contains);
        var positions = posePositions ?? !import.OtherProportions;
        using (ImRaii.Disabled(moving == 0))
        {
            if (ImGui.Checkbox((moving == 1 ? "Move 1 bone" : $"Move {moving} bones") + "###pose-positions", ref positions))
                posePositions = positions;
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(moving == 0
                ? "The pose moves none of these bones; it only turns them."
                : "Key the positions of the bones the pose moves, like where the body is. The others only turn.");
        if (import.OtherProportions)
            Widgets.HintWrapped("The pose was made on a body with other proportions. Moving bones would reshape this one.");

        var inside = frame >= layer.Start && frame <= layer.End;
        var own = posing.MotionPose(loaded, frame);
        var blocked = readOnly ? "Wait for the save to finish."
            : !inside ? staged.AtEnd ? "The layer must reach the clip's end." : "Move the playhead into the layer."
            : bones.Count == 0 ? selectedOnly ? "The pose has none of the selected bones." : "The pose has none of this skeleton's bones."
            : own == null ? decode.Status
            : null;
        using (ImRaii.Disabled(blocked != null))
        {
            if (ImGui.Button((staged.AtEnd ? $"Key at the end ({TimelineWindowViews.FrameText(frame)})" : $"Key at {TimelineWindowViews.FrameText(frame)}") + "###pose-key") &&
                own is { } clipPose)
            {
                var index = posing.BoneTree(skeleton).Index;
                current.Commit(PoseEdits.KeyPose(current.Document, layer.Key, frame, import, bones, positions,
                    bone => index.TryGetValue(bone, out var at) ? clipPose.Local[at] : null));
                current.SelectLayer(layer.Key, frame);
                if (staged.AtEnd)
                    current.Playhead = frame;
                poseKeyed = bones.Count == 1 ? $"Keyed 1 bone at {TimelineWindowViews.FrameText(frame)}." : $"Keyed {bones.Count} bones at {TimelineWindowViews.FrameText(frame)}.";
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(blocked ?? (bones.Count == 1 ? "Key 1 bone of the pose in this layer." : $"Key {bones.Count} bones of the pose in this layer."));
        if (poseKeyed is { } keyed)
            Widgets.MutedWrapped(keyed);
    }

    /// <summary>
    /// The waiting pose on the clip's skeleton, kept while the pose, the skeleton and the frames stay. A pose
    /// file's missing bones take the clip's own pose at <paramref name="frame"/>, so null while that is read.
    /// </summary>
    private PoseImport? PoseImportFor(PoseSource staged, SkeletonDescription skeleton, AnimationTimelineSource loaded, int frame)
    {
        if (poseImport is { } cached && ReferenceEquals(cached.Source, staged) && cached.Skeleton == skeleton.Fingerprint &&
            cached.Frame == frame && cached.ClipFrame == staged.ClipFrame)
            return cached.Import;
        PoseImport import;
        if (staged.File is { } file)
        {
            if (posing.MotionPose(loaded, frame) is not { } own)
                return null;
            import = PoseImports.FromModel(skeleton, file.Bones, own.Local);
        }
        else if (staged.Take is { } take)
            import = PoseImports.FromLocal(skeleton, TakeBones(take));
        else if (staged.Clip is { } clip)
        {
            var pose = clip.Pose(staged.ClipFrame);
            import = PoseImports.FromLocal(skeleton, clip.Skeleton.Bones.Select((bone, i) => (bone.Name, pose[i], bone.Reference.Position)));
        }
        else
            return null;
        poseImport = (staged, skeleton.Fingerprint, frame, staged.ClipFrame, import);
        return import;
    }

    /// <summary>A one-frame take's bones: each one's local transform and its offset from its parent at rest.</summary>
    private static List<(string Name, MotionTransform Local, System.Numerics.Vector3 RestOffset)> TakeBones(AnimationTake take)
    {
        var bones = new List<(string, MotionTransform, System.Numerics.Vector3)>(take.Bones.Length);
        var samples = take.Samples;
        for (var b = 0; b < take.Bones.Length && (b + 1) * AnimationTake.Stride <= samples.Length; b++)
            bones.Add((take.Bones[b].Name, MotionTransform.From(AnimationTake.Read(samples.Slice(b * AnimationTake.Stride, AnimationTake.Stride))),
                take.Bones[b].Reference.Position));
        return bones;
    }
}
