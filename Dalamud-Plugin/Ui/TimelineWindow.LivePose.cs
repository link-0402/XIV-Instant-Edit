using System.Collections.Immutable;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Ui.Timeline;

namespace InstantEdit.Ui;

/// <summary>
/// LivePose adjustments as a bone layer: the ones your character has now, read fresh from LivePose, or
/// the ones a .livepose file holds. Either becomes an offset layer over the whole clip that stays
/// editable like any other; LivePose keeps its own until you clear them there, from the note it leaves.
/// </summary>
public sealed partial class TimelineWindow
{
    // A fresh read of LivePose for the open clip, handed back by the service on the framework thread.
    private volatile AnimationCapture? livePoseRead;
    private bool livePoseReading;
    // A .livepose file read on a task.
    private Task<(string Name, ImmutableArray<PoseBone> Bones)>? livePoseFile;
    private string? lastLivePoseFolder;
    // What the last layer from LivePose did, and whether LivePose's own adjustments can be cleared from it.
    private string? livePoseNote;
    private bool livePoseNoteWarns;
    private AnimationCapture? livePoseClearable;

    /// <summary>The Add menu's LivePose items: a layer from your adjustments now, or from a file.</summary>
    private void DrawLivePoseItems(AnimationTimelineSource loaded)
    {
        var capture = Fresh(loaded).Capture;
        var busy = animations is not { Busy: false } || livePoseReading || livePoseFile != null;
        if (ImGui.MenuItem("Offset layer from LivePose", "", false, !busy && capture.PoseUnavailableReason == null))
            ReadLivePose(capture);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(capture.PoseUnavailableReason is { } reason
                ? "LivePose is unavailable: " + reason
                : "Your LivePose adjustments now become a layer over the whole clip.");
        if (ImGui.MenuItem("Offset layer from a .livepose file", "", false, !busy))
            PickLivePoseFile();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("A saved LivePose pose becomes a layer over the whole clip.");
    }

    private void ReadLivePose(AnimationCapture capture)
    {
        livePoseReading = true;
        livePoseNote = null;
        livePoseClearable = null;
        animations!.Refresh(capture, refreshed => livePoseRead = refreshed);
    }

    private void PickLivePoseFile()
    {
        var start = lastLivePoseFolder is { } folder && Directory.Exists(folder) ? folder : null;
        fileDialog.OpenFileDialog("Choose a LivePose file", "LivePose{.livepose}", (success, files) =>
        {
            if (!success || files.Count == 0)
                return;
            var path = files[0];
            lastLivePoseFolder = Path.GetDirectoryName(path);
            livePoseNote = null;
            livePoseClearable = null;
            livePoseFile = Task.Run(async () => (Path.GetFileNameWithoutExtension(path), LivePoseLayer.ReadFile(await File.ReadAllTextAsync(path))));
        }, 1, start, false);
    }

    /// <summary>Every frame: turns a finished read into a layer once the document can change.</summary>
    private void PollLivePose(TimelineEditor current, AnimationTimelineSource loaded)
    {
        if (livePoseReading && animations is { Busy: false } service)
        {
            if (livePoseRead is { } read)
            {
                livePoseReading = false;
                livePoseRead = null;
                if (read.PoseUnavailableReason is { } reason)
                    LivePoseMessage("LivePose is unavailable: " + reason, true);
                else if (AddLivePoseLayer(current, loaded, "LivePose", read.Pose.Bones, read.Clip.Partial))
                    livePoseClearable = read;
            }
            else
            {
                // The read ended without handing anything back.
                livePoseReading = false;
                LivePoseMessage(service.LastFailed ? "LivePose could not be read: " + service.Status : "LivePose could not be read.", true);
            }
        }
        if (livePoseFile is { IsCompleted: true } task && !ReadOnly)
        {
            livePoseFile = null;
            if (task.IsCompletedSuccessfully)
                AddLivePoseLayer(current, loaded, task.Result.Name, task.Result.Bones, null);
            else
                LivePoseMessage("The file could not be read: " + ErrorText(task.Exception), true);
        }
    }

    /// <summary>Adds the offset layer <paramref name="bones"/>' adjustments make, and says what came of it.</summary>
    private bool AddLivePoseLayer(TimelineEditor current, AnimationTimelineSource loaded, string name, ImmutableArray<PoseBone> bones, int? partial)
    {
        if (ReadOnly || current.Gesture != TimelineGesture.None)
        {
            LivePoseMessage("The timeline is busy. Try again.", true);
            return false;
        }
        if (Fresh(loaded).Clip.Resolution?.Selected?.Skeleton is not { } skeleton)
        {
            LivePoseMessage("Choose the skeleton this animation was made for under Source first.", true);
            return false;
        }
        var offsets = LivePoseLayer.Offsets(bones, skeleton, partial);
        if (offsets.Bones.IsEmpty)
        {
            LivePoseMessage(offsets.Adjusted == 0 && offsets.Missing.IsEmpty && offsets.Ik.IsEmpty
                ? "There are no LivePose adjustments to add."
                : "None of the adjusted bones are on this animation's skeleton.", true);
            return false;
        }
        var next = MotionEdits.AddOffsets(current.Document, name, offsets.Bones, out var key, out var error);
        if (error != null || !current.Commit(next))
        {
            LivePoseMessage(error ?? "The layer could not be added.", true);
            return false;
        }
        current.SelectLayer(key, 0);
        sideRequest = SidePage.Properties;
        var text = offsets.Adjusted == 1 ? "Added a layer from 1 adjusted bone." : $"Added a layer from {offsets.Adjusted} adjusted bones.";
        var left = offsets.Missing.Length + offsets.Ik.Length;
        if (left > 0)
            text += left == 1 ? " 1 bone was left out." : $" {left} bones were left out.";
        if (!offsets.TurnsChildren.IsEmpty)
            text += " Some turns now carry the bones below them.";
        LivePoseMessage(text, left > 0 || !offsets.TurnsChildren.IsEmpty);
        return true;
    }

    private void LivePoseMessage(string text, bool warns)
    {
        livePoseNote = text;
        livePoseNoteWarns = warns;
    }

    /// <summary>
    /// The note a LivePose layer leaves, with the button that clears LivePose's own adjustments so they
    /// don't show twice, and the one that puts them back.
    /// </summary>
    private void DrawLivePoseNote()
    {
        if (livePoseReading)
        {
            Widgets.Spinner();
            ImGui.SameLine(0, Theme.Gap);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Muted, "Reading LivePose");
            return;
        }
        if (livePoseNote is not { } note)
            return;
        if (Widgets.GhostIconButton("##livepose-note-dismiss", Dalamud.Interface.FontAwesomeIcon.Times, "Dismiss"))
        {
            livePoseNote = null;
            livePoseClearable = null;
            return;
        }
        ImGui.SameLine(0, Theme.Gap);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(livePoseNoteWarns ? Theme.Warning : Theme.Success, note);
        if (animations is not { } service || livePoseClearable is not { } capture)
            return;
        ImGui.SameLine(0, Theme.Gap);
        using (ImRaii.Disabled(service.Busy))
        {
            if (ImGui.SmallButton("Clear them in LivePose"))
                service.ClearOffsets(capture);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("LivePose still applies them, so they show twice. This clears every LivePose adjustment.");
        ImGui.SameLine(0, Theme.Gap);
        using (ImRaii.Disabled(service.Busy || !service.CanReapplyOffsets))
        {
            if (ImGui.SmallButton("Put them back"))
                service.ReapplyOffsets(capture);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Put back the LivePose adjustments the last clear removed.");
    }
}
