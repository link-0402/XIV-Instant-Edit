using System.Collections.Immutable;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Models;
using InstantEdit.Services;
using InstantEdit.Services.Animations;
using InstantEdit.Ui.Timeline;

namespace InstantEdit.Ui;

/// <summary>
/// The save footer, like the rebakes': a new mod or in place, a problem banner, Save with the reason
/// it is unavailable, the running job's status with Cancel, and Undo last save. A save first ends
/// the preview, then reloads the timeline from the game once it is committed.
/// </summary>
public sealed partial class TimelineWindow
{
    private AnimationDestination destination = AnimationDestination.NewMod;
    private string modName = string.Empty;
    private Task<Guid?>? saveTask;
    private Guid? pendingSaveId;
    private Guid? lastSaveId;
    private bool undoPending;
    // The clip a running save or undo belongs to; the window may show another one by the time it ends.
    private string? jobClip;
    private string? saveError;
    private string? saveMessage;

    private void DrawSaveFooter(TimelineEditor current, AnimationTimelineSource loaded)
    {
        ImGui.Separator();
        if (ImGui.RadioButton("Create new mod", destination == AnimationDestination.NewMod))
            destination = AnimationDestination.NewMod;
        ImGui.SameLine();
        if (ImGui.RadioButton("Replace in-place", destination == AnimationDestination.InPlace))
            destination = AnimationDestination.InPlace;
        if (destination == AnimationDestination.NewMod)
        {
            ImGui.SameLine(0, Theme.Scaled(16));
            ImGui.SetNextItemWidth(Math.Min(Theme.Scaled(380), Math.Max(Theme.Scaled(120), ImGui.GetContentRegionAvail().X - Theme.Scaled(90))));
            ImGui.InputText("Mod name", ref modName, 128);
        }

        var doc = current.Document;
        RefreshDocumentTexts(doc);
        var problem = TimelineWindowViews.SaveProblem(loaded.Document != null, loaded.ReadOnlyReason,
            destination == AnimationDestination.InPlace, canReplace, doc.LengthChanged, loaded.Clip.Resolution);
        if (problem != null)
            Widgets.Banner("##timeline-save-problem", FeedbackSeverity.Warning, problem);
        else if (saveError != null)
            Widgets.Banner("##timeline-save-error", FeedbackSeverity.Error, saveError);
        else if (pastEndWarning != null)
            ImGui.TextColored(Theme.Warning, "⚠ " + pastEndWarning);

        var missing = SaveMissing(current, problem);
        using (ImRaii.Disabled(missing != null))
        {
            if (ImGui.Button("Save"))
                StartSave(current, loaded);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(missing ?? (destination == AnimationDestination.NewMod
                ? "Save the timeline into a new mod."
                : "Save the timeline into the mod it comes from. Undo last save puts it back."));
        ImGui.SameLine(0, Theme.Scaled(16));
        DrawUndoLastSave(current);

        var service = animations!;
        if (saveTask != null || pendingSaveId != null || undoPending || service.Busy && !previewJob)
        {
            ImGui.SameLine(0, Theme.Scaled(16));
            Widgets.Spinner();
            ImGui.SameLine(0, Theme.Gap);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Muted, saveTask != null ? "Ending the preview" : service.Status);
            if (service.CanCancel && saveTask == null)
            {
                ImGui.SameLine(0, Theme.Gap);
                if (ImGui.SmallButton("Cancel"))
                    service.Cancel();
            }
        }
        else if (saveMessage != null)
        {
            ImGui.SameLine(0, Theme.Scaled(16));
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Success, saveMessage);
        }
    }

    /// <summary> Why Save is unavailable now, shown on the button; null when it can run. </summary>
    private string? SaveMissing(TimelineEditor current, string? problem)
    {
        if (problem != null)
            return problem;
        if (saveTask != null || pendingSaveId != null)
            return "Saving.";
        if (undoPending)
            return "Undoing the last save.";
        if (loadTask != null)
            return "Reading the timeline.";
        if (!current.Dirty)
            return "Nothing changed since the last save.";
        if (current.Gesture != TimelineGesture.None)
            return "Finish the drag first.";
        if (animations!.Busy)
            return "Another edit is running. Wait for it to end.";
        if (destination == AnimationDestination.NewMod && !PenumbraService.IsSafeNewModName(modName.Trim()))
            return "Choose a valid mod name.";
        return null;
    }

    /// <summary>
    /// Ends the preview (Penumbra must resolve the clip's own file again before a commit checks it),
    /// then hands the document to the service. The editor stays read-only until the job ends.
    /// </summary>
    private void StartSave(TimelineEditor current, AnimationTimelineSource loaded)
    {
        var document = current.Document;
        var target = Fresh(loaded);
        var place = destination;
        var name = modName.Trim();
        ImmutableDictionary<string, byte[]> added;
        try { added = TimelineWindowViews.AddedFiles(document, imports.Values); }
        catch (InvalidDataException e)
        {
            saveError = e.Message;
            return;
        }
        var beside = TimelineWindowViews.AddedBeside(document, imports.Values);
        var service = animations!;
        saveError = null;
        saveMessage = null;
        jobClip = ClipKey(loaded);
        StopPreview();
        var ending = previewStop ?? Task.CompletedTask;
        previewStop = null;
        saveTask = Task.Run(async () =>
        {
            await ending;
            await preview.StopAsync();
            // The edit service starts jobs from the framework thread, where the observer it updates lives.
            return await framework.RunOnFrameworkThread(() =>
            {
                playback.Stop();
                return service.EditTimeline(target, document, place, name, added, beside);
            });
        });
    }

    private void PollSave()
    {
        if (animations is not { } service)
            return;
        if (saveTask is { IsCompleted: true } task)
        {
            saveTask = null;
            if (task.IsFaulted || task.IsCanceled)
                saveError = "The preview could not be ended, so nothing was saved. " + ErrorText(task.Exception);
            else if (task.Result is { } id)
                pendingSaveId = id;
            else
                saveError = "Another edit is running. Save again when it ends.";
        }
        // A save that never started, or whose result a later job replaced while the window was closed.
        if (pendingSaveId is { } stale && !service.Busy && service.LastResult?.Id != stale)
        {
            pendingSaveId = null;
            saveError = "The save's result is unknown. Check Recent edits in the Animations tab.";
        }
        var sameClip = source != null && ClipKey(source) == jobClip;
        if (pendingSaveId is { } job && service.LastResult is { } result && result.Id == job)
        {
            pendingSaveId = null;
            if (!sameClip)
                return;
            if (result.Success)
            {
                editor?.MarkSaved();
                lastSaveId = job;
                saveMessage = destination == AnimationDestination.NewMod ? "Saved into a new mod." : "Saved.";
                Load(source!.Capture, source.Startup, true);
            }
            else
            {
                saveError = result.Message;
            }
        }
        if (undoPending && !service.Busy)
        {
            undoPending = false;
            if (!sameClip)
                return;
            if (service.LastFailed)
                saveError = service.Status;
            else
                saveMessage = "The last save was undone.";
            Load(source!.Capture, source.Startup, true);
        }
    }

    /// <summary> Reverts this window's last save through its journal, then reads the timeline again. </summary>
    private void DrawUndoLastSave(TimelineEditor current)
    {
        var service = animations!;
        AnimationEditJournal? journal = null;
        if (lastSaveId is { } id)
            foreach (var entry in service.Recovery)
                if (entry.Id == id)
                {
                    journal = entry.State == "Undone" ? null : entry;
                    break;
                }
        var blocked = journal == null ? "There is no save of this timeline to undo."
            : current.Dirty ? "Save or undo your changes first."
            : service.Busy || ReadOnly ? "Another edit is running. Wait for it to end."
            : null;
        using (ImRaii.Disabled(blocked != null))
        {
            if (ImGui.Button("Undo last save") && journal != null)
            {
                jobClip = source is { } shown ? ClipKey(shown) : null;
                StopPreview();
                service.Undo(journal);
                undoPending = true;
                saveError = null;
                saveMessage = null;
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(blocked ?? "Put the files back as they were before the last save.");
    }
}
