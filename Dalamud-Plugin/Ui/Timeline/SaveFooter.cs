using System.Collections.Immutable;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Models;
using InstantEdit.Services;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Services.PenumbraMods;

namespace InstantEdit.Ui.Timeline;

/// <summary>
/// The save footer, like the rebakes': a new mod or in place, the skeleton repair choice, a problem
/// banner, Save with the reason it is unavailable, the running job's status with Cancel, and Undo last
/// save. A save first ends the preview, then reads the timeline again (<paramref name="reload"/>) once it
/// is committed. Bone layers saved into the clip are carried over the save, so they stay editable.
/// </summary>
/// <param name="loading">Whether the timeline is being read.</param>
/// <param name="reload">Reads the open clip again after a save or an undo: true after an undo.</param>
/// <param name="editAgain">Opens a saved edit again with its layers (Edit again).</param>
internal sealed class SaveFooter(TimelineSession session, TimelinePreview timelinePreview, EffectLookEditor effectLooks, SoundFiles soundFiles,
    SoundSaveCheck soundSave, TimelineTexts texts, Func<bool> loading, Action<bool> reload, Action<AnimationEditJournal, AnimationCapture> editAgain)
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
    // The standard skeleton the next save repairs onto; null keeps the clip's own. A rebuild for the
    // character's race lives in the session (TimelineSession.Rebuild).
    private SkeletonStandard? repairTarget;
    // The clip whose automatic rebuild pick has run, so a choice made afterwards stays.
    private string? rebuildDecided;
    // The rebuild's line naming the other races that play the file, for the clip path and race it was made for.
    private (string Path, string Model, string? Note)? borrowNote;
    // Whether the clip's file can be written over in place.
    private bool canReplace;

    /// <summary>Where the next save goes, as the footer's choice says.</summary>
    public AnimationDestination Destination => destination;
    /// <summary>The new mod's name, as typed.</summary>
    public string ModName => modName;
    /// <summary>The standard skeleton the next save repairs or rebuilds onto; null keeps the clip's own.</summary>
    public SkeletonStandard? RepairTarget => session.Rebuild?.Target.Standard ?? repairTarget;
    /// <summary>A save or an undo runs, or waits for the service's result.</summary>
    public bool Working => saveTask != null || pendingSaveId != null || undoPending;
    /// <summary>The document must not change: a save with its sound check or question, or an undo, runs.</summary>
    public bool Busy => Working || soundSave.Checking || soundSave.Asking;

    /// <summary>
    /// A clip was read: another clip starts over with a new mod of its own name; one opened again with a
    /// saved edit's layers (Edit again) saves over that edit's file when it can.
    /// </summary>
    public void Loaded(AnimationTimelineSource loaded, bool sameClip, bool editAgain)
    {
        canReplace = AnimationResources.CanReplace(loaded.Resource);
        if (!sameClip)
        {
            modName = TimelineWindowViews.DefaultModName(AnimationPresentation.DefaultModName(loaded.Capture));
            destination = AnimationDestination.NewMod;
            saveError = null;
            saveMessage = null;
            lastSaveId = null;
            repairTarget = null;
            session.Rebuild = null;
            rebuildDecided = null;
        }
        if (editAgain && loaded.BaseMotion != null)
        {
            // Saving again writes over the edit's file; the new save's journal keeps it for Undo.
            destination = canReplace ? AnimationDestination.InPlace : AnimationDestination.NewMod;
            saveMessage = "The saved layers are back.";
        }
    }

    /// <summary> Edit again, or an undo back to a layered edit: the saved layers apply to the motion from before their edit while this clip stays open. </summary>
    public void KeepBaseOf(AnimationTimelineSource loaded)
    {
        if (loaded.BaseMotion is { } kept && loaded.BaseHash is { } keptHash)
            motionBase = (TimelineSession.ClipKey(loaded), kept, loaded.BaseFingerprint ?? string.Empty, keptHash);
    }

    /// <summary> Another clip, or this one opened afresh: its own file is the motion to edit. </summary>
    public void ForgetMotion()
    {
        motionBase = null;
        motionCarry = null;
    }

    /// <summary> An edit of the open clip is being undone from the Edits page; the clip is read again once it ends. </summary>
    public void UndoingOpenClip()
    {
        jobClip = TimelineSession.ClipKey(session.Source!);
        undoPending = true;
        saveError = null;
        saveMessage = null;
    }

    public void Draw(TimelineEditor current, AnimationTimelineSource loaded)
    {
        var doc = current.Document;
        var clip = session.Fresh(loaded).Clip;
        // Once per clip, when its match is in: a clip borrowed from another race is rebuilt for the character's.
        if (rebuildDecided != TimelineSession.ClipKey(loaded) && !loaded.Standalone && clip.Resolution is { State: SkeletonResolutionState.Matched } resolution)
        {
            rebuildDecided = TimelineSession.ClipKey(loaded);
            if (AnimationBones.DefaultRebuild(clip, loaded.FileModel, doc.ExcludedBones) is { } standard && resolution.ActorModel is { } actor)
            {
                session.Rebuild = new(actor, AnimationBones.Standard(clip, standard, actor)!);
                repairTarget = null;
            }
        }
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
            if (ImGui.InputText("Mod name", ref modName, 128))
                modName = PathRules.PlainDashes(modName);
            if (AnimationBakeRules.CompanionClip(loaded.Capture, loaded.Startup, loaded.Resource, destination) != null)
                Widgets.HintWrapped(AnimationBakeRules.CompanionNote(loaded.Startup));
        }
        // An expression's face keeps its own skeleton and every bone.
        if (!loaded.Standalone)
            DrawRepairChoice(loaded);

        texts.Refresh(doc);
        var repair = loaded.Standalone ? null : RepairTarget;
        var problem = TimelineWindowViews.SaveProblem(loaded.Document != null, loaded.ReadOnlyReason,
                          destination == AnimationDestination.InPlace, canReplace, doc.LengthChanged, clip.Resolution,
                          AnimationBakeRules.ChangesMotion(doc, repair))
                      ?? (loaded.Standalone ? null : TimelineWindowViews.BonesProblem(doc, clip, session.EditingSkeleton(loaded), repair, session.Rebuild?.Model));
        if (problem != null)
            Widgets.Banner("##timeline-save-problem", FeedbackSeverity.Warning, problem);
        else if (saveError != null)
            Widgets.Banner("##timeline-save-error", FeedbackSeverity.Error, saveError);
        else if (saveMessage == AnimationEditService.SavedSkeletonTooSmall)
            Widgets.Banner("##timeline-save-warning", FeedbackSeverity.Warning, saveMessage);
        else if (texts.PastEndWarning != null)
            ImGui.TextColored(Theme.Warning, "⚠ " + texts.PastEndWarning);
        else if (texts.LayerBonesWarning != null)
        {
            using var wrap = ImRaii.TextWrapPos(0f);
            ImGui.TextColored(Theme.Warning, "⚠ " + texts.LayerBonesWarning);
        }

        var missing = SaveMissing(current, problem);
        using (ImRaii.Disabled(missing != null))
        {
            if (ImGui.Button("Save"))
                soundSave.Begin(current, loaded, destination, () => StartSave(current, loaded));
        }
        soundSave.Draw(current, () => StartSave(current, loaded), error => saveError = error);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(missing ?? (destination == AnimationDestination.NewMod
                ? "Save the timeline into a new mod."
                : "Save the timeline into the mod it comes from. Undo last save puts it back."));
        ImGui.SameLine(0, Theme.Scaled(16));
        DrawUndoLastSave(current);

        var service = session.Animations!;
        if (saveTask != null || pendingSaveId != null || undoPending || service.Busy && !timelinePreview.Building)
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
        else if (saveMessage != null && saveMessage != AnimationEditService.SavedSkeletonTooSmall)
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
        if (soundSave.Checking)
            return "Checking the mod.";
        if (soundSave.Asking)
            return "Answer the question about the sound first.";
        if (undoPending)
            return "Undoing the last save.";
        if (loading())
            return "Reading the timeline.";
        if (!current.Dirty && RepairTarget == null)
            return "Nothing changed since the last save.";
        if (current.Gesture != TimelineGesture.None)
            return "Finish the drag first.";
        if (session.Animations!.Busy)
            return "Another edit is running. Wait for it to end.";
        if (destination == AnimationDestination.NewMod && !ModPaths.IsSafeNewModName(modName.Trim()))
            return "Choose a valid mod name.";
        return effectLooks.LookMissing(current.Document) ?? soundFiles.SoundMissing(current.Document) ?? soundFiles.SoundCutMissing(current.Document);
    }

    /// <summary>
    /// Ends the preview (Penumbra must resolve the clip's own file again before a commit checks it),
    /// then hands the document to the service. The editor stays read-only until the job ends.
    /// </summary>
    private void StartSave(TimelineEditor current, AnimationTimelineSource loaded)
    {
        var document = current.Document;
        var motionFrames = current.MotionFrames;
        var target = session.Fresh(loaded);
        var place = destination;
        var name = modName.Trim();
        var repair = target.Standalone ? null : RepairTarget;
        // Effect looks are saved as copies of their effects, packaged like imports. Sound changes and
        // pins go into copies too, or over the mod's own sound files where BeginSave allowed it.
        TimelineDocument saved;
        ImmutableDictionary<string, byte[]> added;
        ImmutableDictionary<string, string> beside;
        ImmutableDictionary<string, Func<byte[], byte[]>> rebuild;
        ImmutableDictionary<string, TimelineSoundReplacement> replaced;
        ImmutableDictionary<string, string> copied;
        ImmutableDictionary<string, string> cutSounds;
        try
        {
            (saved, var used) = effectLooks.WithLooks(document);
            var sounds = soundFiles.WithSoundCuts(soundFiles.WithSounds(saved, soundSave.Route(place)), saved, soundFiles.SoundBytes);
            saved = sounds.Document;
            cutSounds = sounds.CutFrom;
            used.AddRange(sounds.Copies);
            replaced = sounds.InPlace;
            // The sounds copies were made from, as read from the collection (an import's own file isn't there yet).
            copied = sounds.Bases.Where(b => soundFiles.Resource(b.Key) != null)
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
            soundSave.SaveStarted();
        }
        var service = session.Animations!;
        saveError = null;
        saveMessage = null;
        jobClip = TimelineSession.ClipKey(loaded);
        KeepMotionForSave(target, document, repair);
        timelinePreview.Stop();
        var ending = timelinePreview.TakeStopping();
        saveTask = Task.Run(async () =>
        {
            await ending;
            await session.Preview.StopAsync();
            // The edit service starts jobs from the framework thread, where the observer it updates lives.
            return await session.Framework.RunOnFrameworkThread(() =>
            {
                session.Playback.Stop();
                return service.EditTimeline(target, saved, place, name, added, beside, motionFrames, repair, rebuild,
                    replaced.IsEmpty ? null : replaced, copied.IsEmpty ? null : copied, cutSounds);
            });
        });
    }

    public void Poll()
    {
        if (session.Animations is not { } service)
            return;
        if (saveTask is { IsCompleted: true } task)
        {
            saveTask = null;
            if (task.IsFaulted || task.IsCanceled)
            {
                saveError = "The preview could not be ended, so nothing was saved. " + TimelineSession.ErrorText(task.Exception);
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
        var sameClip = session.Source != null && TimelineSession.ClipKey(session.Source) == jobClip;
        if (pendingSaveId is { } job && service.LastResult is { } result && result.Id == job)
        {
            pendingSaveId = null;
            if (!sameClip)
                return;
            if (result.Success)
            {
                session.Editor?.MarkSaved();
                lastSaveId = job;
                // The saved file is on the standard skeleton now; another repair would change nothing. A rebuilt
                // file's header names the character's race, so it offers no rebuild either.
                repairTarget = null;
                session.Rebuild = null;
                saveMessage = result.Message == AnimationEditService.SavedSkeletonTooSmall ? result.Message
                    : destination == AnimationDestination.NewMod ? "Saved into a new mod." : "Saved.";
                // A sound saved in place is another file now; read it again.
                reload(false);
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
            // While this window edits layers on a motion of its own, a file that is again what an earlier
            // layered edit saved brings that edit's layers back as the saved state, so it matches the file.
            reload(motionBase?.Clip == TimelineSession.ClipKey(session.Source!));
        }
    }

    /// <summary>
    /// Before a save: keeps the motion the layers apply to, and the layers themselves, so the clip read
    /// back after the save goes on editing them rather than stacking them on the saved result. Cuts, a
    /// new length, bones left out or a repair read the saved file back as it is instead (see
    /// <see cref="AnimationBakeRules.KeepsLayersOverSave"/>); Edit again brings cuts and layers back.
    /// </summary>
    private void KeepMotionForSave(AnimationTimelineSource loaded, TimelineDocument document, SkeletonStandard? repair)
    {
        var clip = TimelineSession.ClipKey(loaded);
        // A motion from Blender saved without layers is the saved file's own motion: nothing to carry.
        if (!AnimationBakeRules.KeepsLayersOverSave(document, repair) || document.Replacement != null && document.Motion.IsEmpty)
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
    public AnimationTimelineSource WithMotionBase(AnimationTimelineSource loaded)
    {
        if (motionBase is not { } kept || kept.Clip != TimelineSession.ClipKey(loaded) || loaded.Document == null)
            return loaded;
        // Back to the base, as after undoing the first save, the file's own motion is the base again.
        return kept.Hash == loaded.Resource.Hash
            ? loaded with { BaseMotion = null, BaseFingerprint = null, BaseHash = null }
            : loaded with { BaseMotion = kept.Bytes, BaseFingerprint = kept.Fingerprint, BaseHash = kept.Hash };
    }

    /// <summary> The read document with the layers carried over a save or an undo, once; null when none are carried. </summary>
    public (TimelineDocument Document, bool Unsaved)? TakeCarriedLayers(AnimationTimelineSource loaded)
    {
        if (motionCarry is not { } carry || carry.Clip != TimelineSession.ClipKey(loaded) || loaded.Document is not { } doc)
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
        if (session.Animations is not { } service || current.BaseMotion != null)
            return null;
        return AnimationEditService.SavedLayersJournal(service.Recovery, current);
    }

    /// <summary> Offers to bring back the layers the open clip was saved with, which new layers would otherwise go on top of. </summary>
    public void DrawSavedLayers()
    {
        if (session.Source is not { } current || SavedLayers(current) is not { } saved)
            return;
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, "Saved with edits you can open again.");
        ImGui.SameLine(0, Theme.Gap);
        using (ImRaii.Disabled(session.ReadOnly))
        {
            if (ImGui.SmallButton("Edit again"))
                editAgain(saved, current.Capture);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Bring back the bone layers, cuts and length it was saved with, to change them.\nOtherwise they stay baked in, and new edits go on top.");
    }

    /// <summary> Reverts this window's last save through its journal, then reads the timeline again. </summary>
    private void DrawUndoLastSave(TimelineEditor current)
    {
        var service = session.Animations!;
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
            : service.Busy || session.ReadOnly ? "Another edit is running. Wait for it to end."
            : null;
        using (ImRaii.Disabled(blocked != null))
        {
            if (ImGui.Button("Undo last save") && journal != null)
            {
                jobClip = session.Source is { } shown ? TimelineSession.ClipKey(shown) : null;
                // The layers stay, as unsaved edits on the motion they apply to; layers keyed in cut or
                // stretched frames don't fit the file the undo brings back.
                if (session.Source != null && !current.Document.Motion.IsEmpty && AnimationBakeRules.KeepsLayersOverSave(current.Document, RepairTarget))
                    motionCarry = (TimelineSession.ClipKey(session.Source), current.Document.Motion, current.Document.NextMotionKey, true);
                timelinePreview.Stop();
                service.Undo(journal);
                undoPending = true;
                saveError = null;
                saveMessage = null;
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(blocked ?? "Put the files back as they were before the last save.");
    }

    /// <summary>
    /// The save footer's skeleton choice: keep the clip's own, repair it onto a standard skeleton so
    /// every bone sits at the index other players' skeletons and sync plugins expect, or, for a clip
    /// borrowed from another race, rebuild it onto a standard skeleton of the character's race.
    /// </summary>
    private void DrawRepairChoice(AnimationTimelineSource loaded)
    {
        var clip = session.Fresh(loaded).Clip;
        if (repairTarget is { } chosen && AnimationBones.Standard(clip, chosen) is null)
            repairTarget = null;
        var race = AnimationBones.RebuildRace(clip, loaded.FileModel);
        // The match can change under the choice: a skeleton chosen under Source, another character.
        if (session.Rebuild is { } rebuild)
            session.Rebuild = race != rebuild.Model || AnimationBones.Standard(clip, rebuild.Target.Standard, race) is not { } now ? null
                : now == rebuild.Target ? rebuild : rebuild with { Target = now };
        var excluded = session.Editor?.Document.ExcludedBones ?? [];
        // A rebuild is offered only onto the character's standards that hold every kept bone.
        IEnumerable<Choice> Rebuilds() => race == null ? [] : clip.Resolution!.ActorStandards
            .Where(s => AnimationBones.Plan(clip, s.Standard, excluded, race) is { Dropped.IsEmpty: true })
            .Select(s => new Choice(s.Standard, race));
        var current = session.Rebuild is { } shown ? new Choice(shown.Target.Standard, shown.Model) : new Choice(repairTarget, null);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, "Skeleton");
        ImGui.SameLine(0, Theme.Gap);
        ImGui.SetNextItemWidth(Theme.Scaled(race != null ? 300 : 170));
        using (ImRaii.Disabled(session.ReadOnly))
        using (var combo = ImRaii.Combo("##repair", current.Name))
        {
            if (combo.Success)
            {
                if (ImGui.Selectable("Keep its own", current == new Choice(null, null)))
                    Choose(clip, new(null, null));
                foreach (var standard in Enum.GetValues<SkeletonStandard>())
                {
                    var found = AnimationBones.Standard(clip, standard);
                    if (ImGui.Selectable("Repair onto " + AnimationBones.StandardName(standard), current == new Choice(standard, null),
                            found is null ? ImGuiSelectableFlags.Disabled : ImGuiSelectableFlags.None))
                        Choose(clip, new(standard, null));
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                        ImGui.SetTooltip(found is not null
                            ? $"{found.Skeleton.Bones.Length} bones, from {found.Origin}"
                            : standard == SkeletonStandard.Vanilla
                                ? "The game's own skeleton for this model was not found."
                                : $"No {AnimationBones.StandardName(standard)} skeleton for this model is in the skeleton library.");
                }
                foreach (var entry in Rebuilds())
                {
                    if (ImGui.Selectable(entry.Name, current == entry))
                        Choose(clip, entry);
                    if (ImGui.IsItemHovered() && AnimationBones.Standard(clip, entry.Standard!.Value, entry.Model) is { } target)
                        ImGui.SetTooltip($"{target.Skeleton.Bones.Length} bones, from {target.Origin}");
                }
            }
            else if (ComboWheel.Steps() is var steps and not 0)
            {
                // "Keep its own", the standards this model has (the greyed-out ones are skipped), then the rebuilds.
                Choice[] targets = [new(null, null), .. Enum.GetValues<SkeletonStandard>().Where(standard => AnimationBones.Standard(clip, standard) is not null)
                    .Select(standard => new Choice(standard, null)), .. Rebuilds()];
                Choose(clip, ComboWheel.Pick(targets, current, steps));
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Repair moves every bone to the index other players' skeletons use, whatever your skeleton mod adds.\n" +
                             "Only bones the animation moves are written.");
        if (RepairTarget is { } repair && AnimationBones.Plan(clip, repair, excluded, session.Rebuild?.Model) is { } plan)
        {
            ImGui.SameLine(0, Theme.Gap);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Muted, plan.Dropped.IsEmpty
                ? $"Highest index {plan.HighestIndex}"
                : $"Highest index {plan.HighestIndex} · {plan.Dropped.Length} bones left out");
            if (!plan.Dropped.IsEmpty && ImGui.IsItemHovered())
                ImGui.SetTooltip($"The {AnimationBones.StandardName(repair)} skeleton lacks " +
                                 string.Join(", ", plan.Dropped.Take(8).Select(AnimationPresentation.BoneName)) +
                                 (plan.Dropped.Length > 8 ? $" and {plan.Dropped.Length - 8} more." : "."));
        }
        if (AnimationBones.RebuildNote(clip, loaded.FileModel, excluded, session.Rebuild != null) is { } note)
        {
            using var wrap = ImRaii.TextWrapPos(0f);
            ImGui.TextColored(Theme.Muted, note);
        }
        if (session.Rebuild is not { } rebuilding)
            return;
        // The PAP load table is read once per clip and race, not every frame.
        if (borrowNote is not { } borrow || borrow.Path != clip.GamePath || borrow.Model != rebuilding.Model)
            borrowNote = borrow = (clip.GamePath, rebuilding.Model,
                session.Animations?.PapTable is { } table && ushort.TryParse(rebuilding.Model.AsSpan(1), out var actor)
                    ? AnimationBakeRules.BorrowNote(table.PlayedBy(clip.GamePath, Studio.Models.RaceInfo.PlayableCodes), actor)
                    : null);
        if (borrow.Note is { } others)
            Widgets.HintWrapped(others);
    }

    /// <summary>One entry of the skeleton choice: keep (no standard), repair onto a standard, or with a race's model rebuild onto its standard.</summary>
    private sealed record Choice(SkeletonStandard? Standard, string? Model)
    {
        public string Name => this switch
        {
            { Standard: { } standard, Model: { } model } => $"Rebuild for {AnimationBones.RaceName(model)}, {AnimationBones.StandardName(standard)}",
            { Standard: { } standard } => "Repair onto " + AnimationBones.StandardName(standard),
            _ => "Keep its own",
        };
    }

    /// <summary>Takes a skeleton choice: a rebuild goes into the session, a repair or keep clears it.</summary>
    private void Choose(AnimationClip clip, Choice choice)
    {
        session.Rebuild = choice is { Standard: { } standard, Model: { } model } && AnimationBones.Standard(clip, standard, model) is { } target
            ? new(model, target) : null;
        repairTarget = choice.Model == null ? choice.Standard : null;
    }
}
