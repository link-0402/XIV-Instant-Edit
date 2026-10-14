using System.Collections.Immutable;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Models;
using InstantEdit.Services;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;
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
    // The clip's motion before this window first saved bone layers into it, kept while the clip stays
    // open: later saves bake the layers onto it again instead of onto the saved result.
    private (string Clip, byte[] Bytes, string Fingerprint, string Hash)? motionBase;
    // Layers to put back once the clip is read again: after a save as saved, after an undo as unsaved edits.
    private (string Clip, ImmutableArray<MotionLayer> Layers, int NextKey, bool Unsaved)? motionCarry;

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
        // An expression's face keeps its own skeleton and every bone.
        if (!loaded.Standalone)
            DrawRepairChoice(loaded);

        var doc = current.Document;
        RefreshDocumentTexts(doc);
        var clip = Fresh(loaded).Clip;
        var repair = loaded.Standalone ? null : repairTarget;
        var problem = TimelineWindowViews.SaveProblem(loaded.Document != null, loaded.ReadOnlyReason,
                          destination == AnimationDestination.InPlace, canReplace, doc.LengthChanged, clip.Resolution,
                          AnimationBakeRules.ChangesMotion(doc, repair))
                      ?? (loaded.Standalone ? null : TimelineWindowViews.BonesProblem(doc, clip, repair));
        if (problem != null)
            Widgets.Banner("##timeline-save-problem", FeedbackSeverity.Warning, problem);
        else if (saveError != null)
            Widgets.Banner("##timeline-save-error", FeedbackSeverity.Error, saveError);
        else if (pastEndWarning != null)
            ImGui.TextColored(Theme.Warning, "⚠ " + pastEndWarning);
        else if (layerBonesWarning != null)
        {
            using var wrap = ImRaii.TextWrapPos(0f);
            ImGui.TextColored(Theme.Warning, "⚠ " + layerBonesWarning);
        }

        var missing = SaveMissing(current, problem);
        using (ImRaii.Disabled(missing != null))
        {
            if (ImGui.Button("Save"))
                BeginSave(current, loaded);
        }
        DrawSoundAsk(current, loaded);
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
        if (soundCheck != null)
            return "Checking the mod.";
        if (soundAsk != null)
            return "Answer the question about the sound first.";
        if (undoPending)
            return "Undoing the last save.";
        if (loadTask != null)
            return "Reading the timeline.";
        if (!current.Dirty && repairTarget == null)
            return "Nothing changed since the last save.";
        if (current.Gesture != TimelineGesture.None)
            return "Finish the drag first.";
        if (animations!.Busy)
            return "Another edit is running. Wait for it to end.";
        if (destination == AnimationDestination.NewMod && !PenumbraService.IsSafeNewModName(modName.Trim()))
            return "Choose a valid mod name.";
        return LookMissing(current.Document) ?? SoundMissing(current.Document) ?? SoundCutMissing(current.Document);
    }

    /// <summary>
    /// Ends the preview (Penumbra must resolve the clip's own file again before a commit checks it),
    /// then hands the document to the service. The editor stays read-only until the job ends.
    /// </summary>
    private void StartSave(TimelineEditor current, AnimationTimelineSource loaded)
    {
        var document = current.Document;
        var motionFrames = current.MotionFrames;
        var target = Fresh(loaded);
        var place = destination;
        var name = modName.Trim();
        var repair = target.Standalone ? null : repairTarget;
        // Effect looks are saved as copies of their effects, packaged like imports. Sound changes and
        // pins go into copies too, or over the mod's own sound files where BeginSave allowed it.
        TimelineDocument saved;
        ImmutableDictionary<string, byte[]> added;
        ImmutableDictionary<string, string> beside;
        ImmutableDictionary<string, Func<byte[], byte[]>> rebuild;
        ImmutableDictionary<string, TimelineSoundReplacement> replaced;
        ImmutableDictionary<string, string> copied;
        try
        {
            (saved, var used) = WithLooks(document);
            var sounds = WithSounds(saved, SoundRoute(place));
            saved = sounds.Document;
            used.AddRange(sounds.Copies);
            replaced = sounds.InPlace;
            // The sounds copies were made from, as read from the collection (an import's own file isn't there yet).
            copied = sounds.Bases.Where(b => soundReads.TryGetValue(b.Key, out var read) && read is { Resource: not null, Imported: false })
                .ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);
            added = TimelineWindowViews.AddedFiles(saved, used);
            beside = TimelineWindowViews.AddedBeside(saved, used);
            rebuild = TimelineWindowViews.AddedRebuild(saved, used);
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException)
        {
            saveError = e.Message;
            return;
        }
        finally
        {
            soundInPlace = null;
        }
        var service = animations!;
        saveError = null;
        saveMessage = null;
        jobClip = ClipKey(loaded);
        KeepMotionForSave(target, document, repair);
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
                return service.EditTimeline(target, saved, place, name, added, beside, motionFrames, repair, rebuild,
                    replaced.IsEmpty ? null : replaced, copied.IsEmpty ? null : copied);
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
            {
                saveError = "The preview could not be ended, so nothing was saved. " + ErrorText(task.Exception);
                motionCarry = null;
            }
            else if (task.Result is { } id)
                pendingSaveId = id;
            else
            {
                saveError = "Another edit is running. Save again when it ends.";
                motionCarry = null;
            }
        }
        // A save that never started, or whose result a later job replaced while the window was closed.
        if (pendingSaveId is { } stale && !service.Busy && service.LastResult?.Id != stale)
        {
            pendingSaveId = null;
            saveError = "The save's result is unknown. Check Edits in the side panel.";
            motionCarry = null;
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
                // The saved file is on the standard skeleton now; another repair would change nothing.
                repairTarget = null;
                saveMessage = destination == AnimationDestination.NewMod ? "Saved into a new mod." : "Saved.";
                // A sound saved in place is another file now; read it again.
                ForgetSounds();
                Load(source!.Capture, source.Startup, true);
            }
            else
            {
                saveError = result.Message;
                motionCarry = null;
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
            ForgetSounds();
            // While this window edits layers on a motion of its own, a file that is again what an earlier
            // layered edit saved brings that edit's layers back as the saved state, so it matches the file.
            Load(source!.Capture, source.Startup, true, afterUndo: motionBase?.Clip == ClipKey(source));
        }
    }

    /// <summary>
    /// Before a save: keeps the motion the layers apply to, and the layers themselves, so the clip read
    /// back after the save goes on editing them rather than stacking them on the saved result. A new
    /// length, bones left out or a repair bake them in for good.
    /// </summary>
    private void KeepMotionForSave(AnimationTimelineSource loaded, TimelineDocument document, SkeletonStandard? repair)
    {
        var clip = ClipKey(loaded);
        if (AnimationBakeRules.BakesLayersIn(document, repair))
        {
            motionBase = null;
            motionCarry = null;
            return;
        }
        if (document.Motion.IsEmpty && loaded.BaseMotion == null)
            return;
        if (motionBase?.Clip != clip)
            motionBase = loaded.BaseMotion is { } kept
                ? (clip, kept, loaded.BaseFingerprint ?? string.Empty, loaded.BaseHash ?? AnimationPap.Hash(kept))
                : (clip, loaded.Pap, loaded.FileFingerprint, loaded.Resource.Hash);
        motionCarry = (clip, document.Motion, document.NextMotionKey, false);
    }

    /// <summary> The clip as read, with the base its layers apply to while this window has saved layers into it. </summary>
    private AnimationTimelineSource WithMotionBase(AnimationTimelineSource loaded)
    {
        if (motionBase is not { } kept || kept.Clip != ClipKey(loaded) || loaded.Document == null)
            return loaded;
        // Back to the base, as after undoing the first save, the file's own motion is the base again.
        return kept.Hash == loaded.Resource.Hash
            ? loaded with { BaseMotion = null, BaseFingerprint = null, BaseHash = null }
            : loaded with { BaseMotion = kept.Bytes, BaseFingerprint = kept.Fingerprint, BaseHash = kept.Hash };
    }

    /// <summary> The read document with the layers carried over a save or an undo, once; null when none are carried. </summary>
    private (TimelineDocument Document, bool Unsaved)? TakeCarriedLayers(AnimationTimelineSource loaded)
    {
        if (motionCarry is not { } carry || carry.Clip != ClipKey(loaded) || loaded.Document is not { } doc)
            return null;
        motionCarry = null;
        return carry.Layers.IsEmpty ? null
            : (doc with { Motion = carry.Layers, NextMotionKey = Math.Max(doc.NextMotionKey, carry.NextKey) }, carry.Unsaved);
    }

    /// <summary>
    /// The saved edit whose bone layers the open clip's file holds baked in, while this window doesn't edit
    /// them: the newest finished edit with layers that saved exactly this file. Null when there is none.
    /// </summary>
    private AnimationEditJournal? SavedLayers(AnimationTimelineSource current)
    {
        if (animations is not { } service || current.BaseMotion != null)
            return null;
        return AnimationEditService.SavedLayersJournal(service.Recovery, current);
    }

    /// <summary> Offers to bring back the layers the open clip was saved with, which new layers would otherwise go on top of. </summary>
    private void DrawSavedLayers()
    {
        if (source is not { } current || SavedLayers(current) is not { } saved)
            return;
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, "Saved with bone layers.");
        ImGui.SameLine(0, Theme.Gap);
        using (ImRaii.Disabled(ReadOnly))
        {
            if (ImGui.SmallButton("Edit again"))
                EditAgain(saved, current.Capture);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Bring back the bone layers it was saved with, to change them.\nOtherwise they stay baked in, and new layers go on top.");
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
                // The layers stay, as unsaved edits on the motion they apply to.
                if (source != null && !current.Document.Motion.IsEmpty)
                    motionCarry = (ClipKey(source), current.Document.Motion, current.Document.NextMotionKey, true);
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
