using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Ui.Timeline;

namespace InstantEdit.Ui;

/// <summary>
/// The Edits page: every animation edit of the last week, newest first, with Undo, Edit again for a
/// save with bone layers, and putting back the LivePose offsets an old LivePose bake cleared.
/// </summary>
public sealed partial class TimelineWindow
{
    private const int RecoveryRows = 20;

    // A save that was refused, or failed and was rolled back, left no files to put back.
    private static bool NeedsAttention(AnimationEditJournal journal) => journal.State is not ("Completed" or "Undone" or "FilesUndone");

    /// <summary>How many recorded edits did not finish and need an undo; none while an edit is still running.</summary>
    internal static int RecoveryAttention(AnimationEditService service)
    {
        if (service.Busy)
            return 0;
        var attention = 0;
        foreach (var journal in service.Recovery)
            if (NeedsAttention(journal))
                attention++;
        return attention;
    }

    private static string OperationLabel(AnimationBakeRequest request) => request.Operation switch
    {
        AnimationOperation.RepairSkeleton => request.RepairTarget is { } target
            ? $"skeleton repair onto {AnimationBones.StandardName(target)}"
            : "skeleton repair",
        AnimationOperation.ExcludeBones => "bone exclusion",
        AnimationOperation.EditTimeline => request.RepairTarget is { } repaired
            ? $"timeline edit, repaired onto {AnimationBones.StandardName(repaired)}"
            : "timeline edit",
        _ => "LivePose bake",
    };

    private static string DestinationLabel(AnimationBakeRequest request) =>
        request.Destination == AnimationDestination.NewMod ? $"new mod \"{request.ModName}\"" : "in place";

    private static (Vector4 Colour, string Label) JournalState(AnimationEditJournal journal) => journal.State switch
    {
        "Completed" => (Theme.Success, "Completed"),
        "Undone" => (Theme.Muted, "Undone"),
        "FilesUndone" => (Theme.Muted, "Not saved"),
        "Prepared" or "ClearingOffsets" => (Theme.Warning, "Interrupted"),
        _ => (Theme.Warning, journal.State),
    };

    /// <summary>Whether <paramref name="journal"/>'s edit wrote the file the open timeline lives in.</summary>
    private static bool EditsFile(AnimationEditJournal journal, AnimationTimelineSource open) =>
        journal.Request.Capture.CollectionId == open.Capture.CollectionId &&
        journal.Files.Any(f => f.AddedFor == null && string.Equals(f.GamePath, open.Resource.GamePath, StringComparison.Ordinal));

    private void DrawRecentEdits()
    {
        if (animations is not { } service)
            return;
        var journals = service.Recovery;
        var attention = RecoveryAttention(service);
        if (attention > 0)
        {
            Widgets.Banner("##animation-recovery-attention", FeedbackSeverity.Warning, attention == 1
                ? "An edit did not finish. Undo it to restore its files."
                : $"{attention} edits did not finish. Undo them to restore their files.");
            ImGui.Spacing();
        }
        if (journals.IsEmpty)
        {
            Widgets.HintWrapped("No animation edits in the last 7 days.");
            return;
        }
        Widgets.HintWrapped("Undo puts an edit's files back, even after a restart.");
        ImGui.Spacing();
        var shown = 0;
        foreach (var journal in journals)
        {
            if (shown++ >= RecoveryRows)
                break;
            using var id = ImRaii.PushId(journal.Id.ToString("N"));
            DrawRecentEdit(service, journal);
            ImGui.Separator();
        }
        if (journals.Length > RecoveryRows)
            ImGui.TextColored(Theme.Hint, $"{journals.Length - RecoveryRows} older edit{(journals.Length - RecoveryRows == 1 ? "" : "s")} not shown.");
    }

    /// <summary>One edit: what it changed and when, its state, and its buttons.</summary>
    private void DrawRecentEdit(AnimationEditService service, AnimationEditJournal journal)
    {
        var request = journal.Request;
        var startup = request.Timeline?.Startup == true;
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(AnimationPresentation.AnimationName(request.Capture, startup));
        var buttons = Theme.Scaled(84);
        ImGui.SameLine(Math.Max(ImGui.GetCursorPosX() + Theme.Gap, ImGui.GetContentRegionMax().X - buttons));
        // Opening another clip waits for a running read, save or undo; with no clip open nothing does.
        var working = loadTask != null || saveFooter.Working;
        var againProblem = AnimationEditService.EditAgainProblem(journal) ?? (working ? "Wait for the timeline to finish." : null);
        if (Widgets.IconButton("##edit-again", FontAwesomeIcon.Edit, againProblem ?? "Edit again: bring back its bone layers, cuts and length", againProblem is null))
            EditAgain(journal);
        ImGui.SameLine(0, Theme.Scaled(2));
        var canUndo = journal.State != "Undone";
        // Undoing the open clip's edit reads it again, which unsaved changes would not survive.
        var open = source is { } shown && EditsFile(journal, shown);
        var blocked = !canUndo ? "Already undone"
            : open && editor is { Dirty: true } ? "Save or undo your changes to this timeline first."
            : open && ReadOnly ? "Wait for the save to finish."
            : null;
        using (ImRaii.Disabled(service.Busy || blocked != null))
        {
            if (Widgets.IconButton("##undo", FontAwesomeIcon.Undo, blocked ?? "Undo this edit: put its files back"))
            {
                if (open)
                {
                    timelinePreview.Stop();
                    saveFooter.UndoingOpenClip();
                }
                service.Undo(journal);
            }
        }
        ImGui.SameLine(0, Theme.Scaled(2));
        var canRestore = journal.OffsetsCleared || journal.PoseClearOutcome == "Pending";
        using (ImRaii.Disabled(service.Busy || !canRestore))
        {
            if (Widgets.IconButton("##restore-offsets", FontAwesomeIcon.History,
                    canRestore ? "Put back the LivePose offsets this edit cleared, keeping its files" : "This edit cleared no LivePose offsets"))
                service.RestoreOffsets(journal);
        }
        var (colour, state) = JournalState(journal);
        if (Widgets.Badge(state, colour) && journal.Message.Length > 0)
            ImGui.SetTooltip(journal.Message);
        ImGui.SameLine(0, Theme.Gap);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, journal.CreatedUtc.ToLocalTime().ToString("g"));
        using var wrap = ImRaii.TextWrapPos(0f);
        ImGui.TextColored(Theme.Hint, $"{OperationLabel(request)} · {DestinationLabel(request)}");
    }
}
