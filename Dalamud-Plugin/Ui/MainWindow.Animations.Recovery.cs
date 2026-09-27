using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Models;
using InstantEdit.Services.Animations;

namespace InstantEdit.Ui;

public sealed partial class MainWindow
{
    private const int RecoveryRows = 10;

    private static bool NeedsAttention(AnimationEditJournal journal)
        => journal.State is not ("Completed" or "Undone");

    private static bool SameAnimation(AnimationEditJournal journal, AnimationCapture capture)
        => journal.Request.Capture.ActorId == capture.ActorId
        && string.Equals(journal.Request.Capture.Clip.GamePath, capture.Clip.GamePath, StringComparison.Ordinal);

    /// <summary> The newest journal of this animation whose bake cleared live offsets that can still be put back. </summary>
    private AnimationEditJournal? FindOffsetJournal(AnimationCapture capture)
    {
        if (animations is null) return null;
        foreach (var journal in animations.Recovery)
            if (SameAnimation(journal, capture) && (journal.OffsetsCleared || journal.PoseClearOutcome == "Pending"))
                return journal;
        return null;
    }

    /// <summary> The journal of the last successful edit, unless it was already undone. </summary>
    private AnimationEditJournal? FindUndoableLastEdit()
    {
        if (animations is null || animations.LastResult is not { Success: true } last) return null;
        foreach (var journal in animations.Recovery)
            if (journal.Id == last.Id)
                return journal.State == "Undone" ? null : journal;
        return null;
    }

    private static string OperationLabel(AnimationOperation operation)
        => operation switch
        {
            AnimationOperation.RepairSkeleton => "skeleton repair",
            AnimationOperation.ExcludeBones => "bone exclusion",
            _ => "LivePose bake",
        };

    private static string DestinationLabel(AnimationBakeRequest request)
        => request.Destination == AnimationDestination.NewMod ? $"new mod \"{request.ModName}\"" : "in place";

    private static (Vector4 Colour, string Label) JournalState(AnimationEditJournal journal)
        => journal.State switch
        {
            "Completed" => (Theme.Success, "Completed"),
            "Undone" => (Theme.Muted, "Undone"),
            "Prepared" or "ClearingOffsets" => (Theme.Warning, "Interrupted"),
            _ => (Theme.Warning, journal.State),
        };

    /// <summary> How many recorded edits did not finish and need an undo; none while an edit is still running. </summary>
    private int RecoveryAttention()
    {
        if (animations is null || animations.Busy) return 0;
        var attention = 0;
        foreach (var journal in animations.Recovery)
            if (NeedsAttention(journal)) attention++;
        return attention;
    }

    /// <summary> The Recent edits tab: animation edits with undo and offset restoration. </summary>
    private void DrawRecovery()
    {
        if (animations is null) return;
        var journals = animations.Recovery;
        var attention = RecoveryAttention();
        if (attention > 0)
        {
            Widgets.Banner("##animation-recovery-attention", FeedbackSeverity.Warning, attention == 1
                ? "An edit did not finish. Undo it below to restore its files and the live offsets it cleared."
                : $"{attention} edits did not finish. Undo them below to restore their files and the live offsets they cleared.");
            ImGui.Spacing();
        }
        if (journals.IsEmpty)
        {
            Widgets.Hint("No animation edits recorded in the last 7 days.");
            return;
        }

        Widgets.HintWrapped("Every bake, repair and exclusion is journaled. Undo reverts its files and restores the live offsets it cleared; an interrupted edit can be undone here after a restart.");
        using var table = ImRaii.Table("##animation-recovery-table", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp);
        if (!table.Success)
            return;
        ImGui.TableSetupColumn("When", ImGuiTableColumnFlags.WidthFixed, Theme.Scaled(110));
        ImGui.TableSetupColumn("Edit", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.WidthFixed, Theme.Scaled(96));
        ImGui.TableSetupColumn("##actions", ImGuiTableColumnFlags.WidthFixed, Theme.Scaled(58));
        ImGui.TableHeadersRow();

        var shown = 0;
        foreach (var journal in journals)
        {
            if (shown++ >= RecoveryRows)
                break;
            using var id = ImRaii.PushId(journal.Id.ToString("N"));
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Muted, journal.CreatedUtc.ToLocalTime().ToString("g"));

            ImGui.TableSetColumnIndex(1);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(AnimationPresentation.AnimationName(journal.Request.Capture, false));
            ImGui.TextColored(Theme.Muted, $"{OperationLabel(journal.Request.Operation)} · {DestinationLabel(journal.Request)}");

            ImGui.TableSetColumnIndex(2);
            var (colour, state) = JournalState(journal);
            if (Widgets.Badge(state, colour) && journal.Message.Length > 0)
                ImGui.SetTooltip(journal.Message);

            ImGui.TableSetColumnIndex(3);
            var canUndo = journal.State != "Undone";
            using (ImRaii.Disabled(animations.Busy || !canUndo))
            {
                if (Widgets.IconButton("##undo", FontAwesomeIcon.Undo, canUndo ? "Undo this edit: revert its files and restore the live offsets it cleared" : "Already undone"))
                    animations.Undo(journal);
            }
            ImGui.SameLine(0, Theme.Scaled(2));
            var canRestore = journal.OffsetsCleared || journal.PoseClearOutcome == "Pending";
            using (ImRaii.Disabled(animations.Busy || !canRestore))
            {
                if (Widgets.IconButton("##restore-offsets", FontAwesomeIcon.History, canRestore ? "Put back the live offsets captured before this edit, keeping its files" : "This edit cleared no live offsets"))
                    animations.RestoreOffsets(journal);
            }
        }

        if (journals.Length > RecoveryRows)
            ImGui.TextColored(Theme.Hint, $"{journals.Length - RecoveryRows} older edit{(journals.Length - RecoveryRows == 1 ? "" : "s")} not shown.");
    }
}
