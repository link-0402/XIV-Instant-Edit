using System.Collections.Immutable;
using Dalamud.Bindings.ImGui;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// The adjustments a .livepose file holds as a bone layer: an offset layer over the whole clip that
/// stays editable like any other.
/// </summary>
internal sealed class LivePosePanel(TimelineSession session)
{
    // A .livepose file read on a task.
    private Task<(string Name, ImmutableArray<PoseBone> Bones)>? livePoseFile;
    private string? lastLivePoseFolder;
    // What the last layer from a file did.
    private string? livePoseNote;
    private bool livePoseNoteWarns;

    /// <summary> Drops the last layer's note, which belongs to the clip it was added to. </summary>
    public void ForgetNote() => livePoseNote = null;

    /// <summary>The Add menu's LivePose item: a layer from a .livepose file.</summary>
    public void DrawItems()
    {
        var busy = session.Animations is not { Busy: false } || livePoseFile != null;
        if (ImGui.MenuItem("Offset layer from a .livepose file", "", false, !busy))
            PickLivePoseFile();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("A saved LivePose pose becomes a layer over the whole clip.");
    }

    private void PickLivePoseFile()
    {
        var start = lastLivePoseFolder is { } folder && Directory.Exists(folder) ? folder : null;
        session.FileDialog.OpenFileDialog("Choose a LivePose file", "LivePose{.livepose}", (success, files) =>
        {
            if (!success || files.Count == 0)
                return;
            var path = files[0];
            lastLivePoseFolder = Path.GetDirectoryName(path);
            livePoseNote = null;
            livePoseFile = Task.Run(async () => (Path.GetFileNameWithoutExtension(path), LivePoseLayer.ReadFile(await File.ReadAllTextAsync(path))));
        }, 1, start, false);
    }

    /// <summary>Every frame: turns a finished read into a layer once the document can change.</summary>
    public void Poll(TimelineEditor current, AnimationTimelineSource loaded)
    {
        if (livePoseFile is { IsCompleted: true } task && !session.ReadOnly)
        {
            livePoseFile = null;
            if (task.IsCompletedSuccessfully)
                AddLivePoseLayer(current, loaded, task.Result.Name, task.Result.Bones);
            else
                LivePoseMessage("The file could not be read: " + TimelineSession.ErrorText(task.Exception), true);
        }
    }

    /// <summary>Adds the offset layer <paramref name="bones"/>' adjustments make, and says what came of it.</summary>
    private void AddLivePoseLayer(TimelineEditor current, AnimationTimelineSource loaded, string name, ImmutableArray<PoseBone> bones)
    {
        if (session.ReadOnly || current.Gesture != TimelineGesture.None)
        {
            LivePoseMessage("The timeline is busy. Try again.", true);
            return;
        }
        if (session.EditingSkeleton(loaded) is not { } skeleton)
        {
            LivePoseMessage("Choose the skeleton this animation was made for under Source first.", true);
            return;
        }
        var offsets = LivePoseLayer.Offsets(bones, skeleton);
        if (offsets.Bones.IsEmpty)
        {
            LivePoseMessage(offsets.Adjusted == 0 && offsets.Missing.IsEmpty && offsets.Ik.IsEmpty
                ? "There are no LivePose adjustments to add."
                : "None of the adjusted bones are on this animation's skeleton.", true);
            return;
        }
        var next = MotionEdits.AddOffsets(current.Document, name, offsets.Bones, out var key, out var error);
        if (error != null || !current.Commit(next))
        {
            LivePoseMessage(error ?? "The layer could not be added.", true);
            return;
        }
        current.SelectLayer(key, 0);
        var text = offsets.Adjusted == 1 ? "Added a layer from 1 adjusted bone." : $"Added a layer from {offsets.Adjusted} adjusted bones.";
        var left = offsets.Missing.Length + offsets.Ik.Length;
        if (left > 0)
            text += left == 1 ? " 1 bone was left out." : $" {left} bones were left out.";
        if (!offsets.TurnsChildren.IsEmpty)
            text += " Some turns now carry the bones below them.";
        LivePoseMessage(text, left > 0 || !offsets.TurnsChildren.IsEmpty);
    }

    private void LivePoseMessage(string text, bool warns)
    {
        livePoseNote = text;
        livePoseNoteWarns = warns;
    }

    /// <summary>The status line's note a layer from a file leaves.</summary>
    public IEnumerable<StatusNote> Notes() =>
        livePoseNote is { } note ? [new StatusNote("livepose", livePoseNoteWarns ? StatusKind.Warning : StatusKind.Notice, note)] : [];
}
