using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Models;
using InstantEdit.Services.Animations;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// The Save history page: every animation edit of the days kept (Settings > Cache), newest first, each under the mod it
/// saved into, with Undo and Edit again for a save with bone layers.
/// </summary>
/// <param name="loading">Whether the timeline is being read.</param>
/// <param name="editAgain">Opens a saved edit again with its layers (Edit again).</param>
internal sealed class RecentEditsPage(TimelineSession session, SaveFooter saveFooter, TimelinePreview timelinePreview, Func<bool> loading,
    Action<AnimationEditJournal> editAgain)
{
    private const int RecoveryRows = 20;

    // A save that was refused, or failed and was rolled back, left no files to put back.
    private static bool NeedsAttention(AnimationEditJournal journal) => journal.State is not ("Completed" or "Undone" or "FilesUndone");

    /// <summary>How many recorded edits did not finish and need an undo; none while an edit is still running.</summary>
    public static int RecoveryAttention(AnimationEditService service)
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
        AnimationOperation.CopyFaces => "faces copied",
        AnimationOperation.EditTimeline when request.Timeline is { RaceCopies.IsEmpty: false } => "timeline edit, for every race",
        AnimationOperation.EditTimeline when request is { RepairTarget: { } target, RepairModel: { } model } =>
            $"timeline edit, rebuilt for {AnimationBones.RaceName(model)} on {AnimationBones.StandardName(target)}",
        AnimationOperation.EditTimeline => request.RepairTarget is { } repaired
            ? $"timeline edit, repaired onto {AnimationBones.StandardName(repaired)}"
            : "timeline edit",
        _ => "LivePose bake",
    };

    /// <summary>
    /// The mod the edit saved into: the new mod's name, else the name of the mod the edited file came from, else the mod
    /// folder the edit wrote.
    /// </summary>
    internal static string ModLabel(AnimationEditJournal journal)
    {
        var request = journal.Request;
        if (request.Destination == AnimationDestination.NewMod && request.ModName.Length > 0)
            return request.ModName;
        if (request.Destination == AnimationDestination.Library && journal.Library is { Name.Length: > 0 } library)
            return library.Name;
        var clip = request.Timeline?.Startup == true && request.Capture.Startup is { } startup ? startup : request.Capture.Clip;
        if (request.Capture.Sources.FirstOrDefault(s => s.GamePath == clip.GamePath)?.ModName is { Length: > 0 } name)
            return name;
        return journal.ModDirectory.Length > 0 ? Path.GetFileName(journal.ModDirectory.TrimEnd('/', '\\')) : "Unknown mod";
    }

    private static string DestinationLabel(AnimationBakeRequest request) =>
        request.Destination switch { AnimationDestination.NewMod => "new mod", AnimationDestination.Library => "library", _ => "in place" };

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

    public void Draw()
    {
        if (session.Animations is not { } service)
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
            Widgets.HintWrapped($"No animation edits in the last {service.RecordDays} days.");
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

    /// <summary>One edit: the mod it saved into and its animation, its state and when, what it changed, and its buttons.</summary>
    private void DrawRecentEdit(AnimationEditService service, AnimationEditJournal journal)
    {
        var request = journal.Request;
        var startup = request.Timeline?.Startup == true;
        var buttons = Theme.Scaled(56);
        var mod = ModLabel(journal);
        ImGui.AlignTextToFramePadding();
        // Cut at the buttons, whole in the tooltip.
        var at = ImGui.GetCursorScreenPos();
        var room = Math.Max(1, ImGui.GetContentRegionAvail().X - buttons - Theme.Gap);
        ImGui.PushClipRect(at, at + new Vector2(room, ImGui.GetFrameHeight()), true);
        ImGui.TextColored(Theme.Label, mod);
        ImGui.PopClipRect();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(mod);
        ImGui.SameLine(Math.Max(ImGui.GetCursorPosX() + Theme.Gap, ImGui.GetContentRegionMax().X - buttons));
        // Opening another clip waits for a running read, save or undo; with no clip open nothing does.
        var working = loading() || saveFooter.Working;
        var againProblem = AnimationEditService.EditAgainProblem(journal) ?? (working ? "Wait for the timeline to finish." : null);
        if (Widgets.IconButton("##edit-again", FontAwesomeIcon.Edit, againProblem ?? "Edit again: bring back its bone layers, cuts and length", againProblem is null))
            editAgain(journal);
        ImGui.SameLine(0, Theme.Scaled(2));
        var canUndo = journal.State != "Undone";
        // Undoing the open clip's edit reads it again, which unsaved changes would not survive.
        var open = session.Source is { } shown && EditsFile(journal, shown);
        var blocked = !canUndo ? "Already undone"
            : open && session.Editor is { Dirty: true } ? "Save or undo your changes to this timeline first."
            : open && session.ReadOnly ? "Wait for the save to finish."
            : null;
        using (ImRaii.Disabled(service.Busy || blocked != null))
        {
            if (Widgets.IconButton("##undo", FontAwesomeIcon.Undo, blocked ?? "Undo this edit: put its files back"))
            {
                if (open)
                {
                    timelinePreview.Stop();
                    saveFooter.UndoingOpenClip(journal);
                }
                service.Undo(journal);
            }
        }
        ImGui.TextUnformatted(AnimationPresentation.AnimationName(request.Capture, startup));
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
