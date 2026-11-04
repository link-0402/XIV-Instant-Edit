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
/// The save row, like the rebakes': a warning line when something blocks the save, then Save to (a new mod or in
/// place), the skeleton repair choice, Save with the reason it is unavailable, Undo last save and the running job's
/// status with Cancel. A save first ends the preview, then reads the timeline again (<paramref name="reload"/>) once it
/// is committed. Bone layers saved into the clip are carried over the save, so they stay editable.
/// </summary>
/// <param name="loading">Whether the timeline is being read.</param>
/// <param name="reload">Reads the open clip again after a save or an undo: true after an undo.</param>
/// <param name="editAgain">Opens a saved edit again with its layers (Edit again).</param>
internal sealed class SaveFooter(TimelineSession session, TimelinePreview timelinePreview, MotionDecode decode, EffectLookEditor effectLooks, SoundFiles soundFiles,
    SoundSaveCheck soundSave, TimelineTexts texts, Func<bool> loading, Action<bool> reload, Action<AnimationEditJournal, AnimationCapture> editAgain)
{
    private AnimationDestination destination = AnimationDestination.NewMod;
    private string modName = string.Empty;
    private Task<SaveStart>? saveTask;
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
    // Whether the last save that baked the layers in did so onto an earlier edit's base, whose layers it carried.
    private bool bakedOnBase;
    // The clip whose automatic rebuild pick has run, so a choice made afterwards stays.
    private string? rebuildDecided;
    // Whether the session's rebuild is the automatic pick, not chosen by hand.
    private bool rebuildAutomatic;
    // Why the automatic rebuild went back to Keep its own: its motion couldn't be read on the target.
    private string? rebuildFailed;
    // The rebuild's line naming the other races that play the file, for the clip path and race it was made for.
    private (string Path, string Model, string? Note)? borrowNote;
    // Whether the edit can be written over in place (see CanReplace).
    private bool canReplace;
    // The widths last frame of Skeleton with its choice and of Save with what follows it, to fit the mod name; 0 before drawn.
    private float skeletonWidth;
    private float saveGroupWidth;
    // Save and what follows are on a line of their own, the row leaving no room for them.
    private bool saveBelow;
    // A save asked for while a link track built: it starts once the track is in (see Poll), so it bakes what Live shows.
    private bool linksWait;
    // The content of the partner tracks the running save bakes the link layers with, for SavedPrints once it succeeds.
    private ImmutableDictionary<string, string> savePrints = MotionLinks.NoPrints;
    // Save to Library (face sessions): your libraries once read, the one chosen by mod folder ("" for New library, null
    // before a choice), a new one's name and the face's name without cfxf_.
    private ImmutableArray<InstantEditLibrary>? libraries;
    private Task<ImmutableArray<InstantEditLibrary>>? librariesTask;
    private string? libraryChoice;
    private string newLibraryName = string.Empty;
    private string faceStem = string.Empty;
    // A new library's name and why it can't be one, for the libraries read: the check looks at every race's face folders.
    private (string Name, ImmutableArray<InstantEditLibrary> Libraries, string? Problem)? newLibraryCheck;
    // The game's faces for the character's face, by the catalog they were read from.
    private (TimelineCatalogData Data, IReadOnlySet<string> Names)? gameFaces;
    // A save into a library: the running one, and one waiting for a yes to replace a face of the same name.
    private Task<FaceCopyOutcome>? libraryTask;
    private FaceLibrarySave? librarySave;
    private (FaceLibrarySave Save, string Text)? libraryAsk;
    // The undo running is a library save's, which leaves the open clip as it is.
    private bool undoLibrary;
    private const string LibraryAskPopup = "##timeline-library-replace";

    /// <summary>Where the next save goes, as the footer's choice says.</summary>
    public AnimationDestination Destination => destination;
    /// <summary>The new mod's name, as typed.</summary>
    public string ModName => modName;
    /// <summary>
    /// The standard skeleton the next save repairs or rebuilds onto; null keeps the clip's own. Both live in the
    /// session (<see cref="TimelineSession.Rebuild"/>, a repair without a race), so the bones are edited on that standard.
    /// </summary>
    public SkeletonStandard? RepairTarget => session.Rebuild?.Target.Standard;
    /// <summary>A save or an undo runs, waits for a link track to build first, or waits for the service's result.</summary>
    public bool Working => saveTask != null || pendingSaveId != null || undoPending || linksWait || libraryTask != null || libraryAsk != null;
    /// <summary>The document must not change: a save with its sound check or question, or an undo, runs.</summary>
    public bool Busy => Working || soundSave.Checking || soundSave.Asking;

    /// <summary>
    /// A clip was read: another clip starts over with a new mod of its own name; one opened again with a
    /// saved edit's layers (Edit again) saves over that edit's file when it can.
    /// </summary>
    public void Loaded(AnimationTimelineSource loaded, bool sameClip, bool editAgain)
    {
        canReplace = CanReplace(loaded, loaded.Document);
        if (!sameClip)
        {
            modName = TimelineWindowViews.DefaultModName(AnimationPresentation.DefaultModName(loaded.Capture));
            destination = AnimationDestination.NewMod;
            saveError = null;
            saveMessage = null;
            lastSaveId = null;
            linksWait = false;
            session.SavedPrints = MotionLinks.NoPrints;
            session.Rebuild = null;
            rebuildDecided = null;
            rebuildAutomatic = false;
            rebuildFailed = null;
            // A face starts with its own name, so a copy matches it where a timeline plays it.
            faceStem = loaded.Document?.Face != null ? LibrarySaveRules.Stem(loaded.MotionTarget.Clip.Name) : string.Empty;
            libraryAsk = null;
        }
        if (editAgain && loaded.MotionTarget.BaseMotion != null)
        {
            // Saving again writes over the edit's file; the new save's journal keeps it for Undo.
            destination = canReplace ? AnimationDestination.InPlace : AnimationDestination.NewMod;
            saveMessage = "The saved layers are back.";
        }
    }

    /// <summary> Edit again, or an undo back to a layered edit: the saved layers apply to the motion from before their edit while this clip stays open. </summary>
    public void KeepBaseOf(AnimationTimelineSource loaded)
    {
        if (loaded.MotionTarget is { BaseMotion: { } kept, BaseHash: { } keptHash } motion)
            motionBase = (TimelineSession.ClipKey(loaded), kept, motion.BaseFingerprint ?? string.Empty, keptHash);
    }

    /// <summary> Another clip, or this one opened afresh: its own file is the motion to edit. </summary>
    public void ForgetMotion()
    {
        motionBase = null;
        motionCarry = null;
        bakedOnBase = false;
        linksWait = false;
        session.SavedPrints = MotionLinks.NoPrints;
    }

    /// <summary> <paramref name="journal"/>, an edit of the open clip, is being undone from the Edits page; the clip is read again once it ends. </summary>
    public void UndoingOpenClip(AnimationEditJournal journal)
    {
        jobClip = TimelineSession.ClipKey(session.Source!);
        // Undoing a rebuild brings the borrowed file back: the rebuild is picked for it again.
        if (journal.Request.RepairModel != null)
            rebuildDecided = null;
        undoPending = true;
        saveError = null;
        saveMessage = null;
    }

    /// <summary>
    /// Draws the save row under the lanes: one warning line when something blocks or concerns the save, then Save to, the
    /// mod name (New mod only), Skeleton (body clips only), and on the right Save, Undo last save and the running job or the
    /// result. The mod name shrinks first; when the row still has no room, the right group moves to a line of its own.
    /// </summary>
    public void Draw(TimelineEditor current, AnimationTimelineSource loaded)
    {
        var doc = current.Document;
        var clip = session.Fresh(loaded).Clip;
        // Only a face goes into a library.
        if (destination == AnimationDestination.Library && doc.Face == null)
            destination = AnimationDestination.NewMod;
        ImGui.Separator();

        texts.Refresh(doc);
        // An expression's own timeline and a face keep their own skeleton: no repair or rebuild.
        var skeleton = !loaded.Standalone && doc.Face == null;
        var repair = skeleton ? RepairTarget : null;
        var inPlace = destination == AnimationDestination.InPlace;
        // A face session writes the face's file, not the expression's timeline it was opened from, unless that changes too.
        canReplace = CanReplace(session.Fresh(loaded), doc);
        var faceInPlace = inPlace ? FaceInPlaceProblem(loaded, doc) : null;
        var problem = faceInPlace ?? TimelineWindowViews.SaveProblem(loaded.Document != null, loaded.ReadOnlyReason,
                          inPlace, canReplace, doc.LengthChanged, clip.Resolution,
                          AnimationBakeRules.ChangesMotion(doc, repair))
                      ?? (loaded.MotionTarget.Standalone ? null : TimelineWindowViews.BonesProblem(doc, clip, session.EditingSkeleton(loaded), repair, session.Rebuild?.Model));
        var (warning, colour) = problem != null ? (problem, Theme.Warning)
            : saveError != null ? (saveError, Theme.Error)
            : saveMessage == AnimationEditService.SavedSkeletonTooSmall ? (saveMessage, Theme.Warning)
            : (texts.PastEndWarning ?? texts.LayerBonesWarning ?? texts.LinkBakeWarning(!doc.Motion.IsEmpty && DropsLayers(doc, repair)) ??
               texts.LinkOffWarning ?? LeftOutWarning(clip, doc, repair), Theme.Warning);
        if (warning != null)
        {
            using var wrap = ImRaii.TextWrapPos(0f);
            ImGui.TextColored(colour, "⚠ " + warning);
        }

        DrawDestination(loaded, doc.Face != null);
        if (destination == AnimationDestination.Library)
            DrawLibrary();
        var rightWidth = saveGroupWidth > 0 ? saveGroupWidth
            : ImGui.CalcTextSize("Save").X + ImGui.CalcTextSize("Undo last save").X + ImGui.GetStyle().FramePadding.X * 4 + Theme.Scaled(16);
        var skeletonPart = skeleton ? Theme.Scaled(16) + skeletonWidth : 0;
        var newMod = destination == AnimationDestination.NewMod;
        // Save and what follows keep to the right, on a line of their own when the row has no room for them with the mod
        // name at its narrowest; some room to spare before they come back up, so a status a few letters longer doesn't
        // make them jump.
        var room = ImGui.GetContentRegionMax().X - (ImGui.GetItemRectMax().X - ImGui.GetWindowPos().X) -
                   (newMod ? Theme.Scaled(16 + 120) : 0) - skeletonPart - Theme.Scaled(16) - rightWidth;
        saveBelow = saveBelow ? room < Theme.Scaled(24) : room < 0;
        if (newMod)
        {
            ImGui.SameLine(0, Theme.Scaled(16));
            var reserved = skeletonPart + (saveBelow ? 0 : Theme.Scaled(16) + rightWidth);
            ImGui.SetNextItemWidth(Math.Max(Theme.Scaled(120), ImGui.GetContentRegionAvail().X - reserved));
            if (ImGui.InputTextWithHint("##timeline-mod-name", "Mod name", ref modName, 128))
                modName = PathRules.PlainDashes(modName);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("The new mod's name.");
        }
        if (skeleton)
        {
            ImGui.SameLine(0, Theme.Scaled(16));
            var start = ImGui.GetCursorScreenPos().X;
            // A rebuild for another race keeps the edits open (AnimationBakeRules.RebuildKeepsEdits): only a repair bakes them in.
            DrawRepairChoice(loaded, AnimationBakeRules.BakeInNote(doc, session.Rebuild?.Model != null ? null : RepairTarget));
            skeletonWidth = ImGui.GetItemRectMax().X - start;
        }

        var right = ImGui.GetContentRegionMax().X - rightWidth;
        if (!saveBelow)
            ImGui.SameLine(Math.Max(right, ImGui.GetItemRectMax().X - ImGui.GetWindowPos().X + Theme.Scaled(16)));
        else
            ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), right));
        var groupStart = ImGui.GetCursorScreenPos().X;
        var missing = SaveMissing(current, problem);
        using (ImRaii.Disabled(missing != null))
        {
            // The save may start frames later (after the mod's check or the question): it takes the clip as it is then.
            if (ImGui.Button("Save"))
            {
                if (destination == AnimationDestination.Library)
                    StartLibrarySave(current, loaded);
                else
                    soundSave.Begin(current, loaded, destination, () =>
                    {
                        if (session.Editor is { } editor && session.Source is { Document: not null } source)
                            StartSave(editor, source);
                    });
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(missing ?? (destination == AnimationDestination.Library ? LibraryTooltip(doc)
                : destination == AnimationDestination.NewMod
                ? doc.Face != null ? "Save the face into a new mod." : "Save the timeline into a new mod."
                : doc.Face != null ? "Save the face into the mod it comes from. Undo last save puts it back."
                : "Save the timeline into the mod it comes from. Undo last save puts it back."));
        soundSave.Draw();
        DrawLibraryAsk();
        ImGui.SameLine(0, Theme.Scaled(16));
        DrawUndoLastSave(current);

        var service = session.Animations!;
        if (saveTask != null || pendingSaveId != null || undoPending || linksWait || libraryTask != null || service.Busy && !timelinePreview.Building)
        {
            ImGui.SameLine(0, Theme.Scaled(16));
            Widgets.Spinner();
            ImGui.SameLine(0, Theme.Gap);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Muted, linksWait ? "Reading the link"
                : saveTask != null || libraryTask != null && !service.Busy ? "Ending the preview" : service.Status);
            if (service.CanCancel && saveTask == null && !linksWait)
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
        saveGroupWidth = ImGui.GetItemRectMax().X - groupStart;
    }

    /// <summary>
    /// "Save to" with its choice, New mod or In place, and Library for a <paramref name="face"/>; its tooltip says when a new
    /// mod takes the loop's startup along, or the startup's loop.
    /// </summary>
    private void DrawDestination(AnimationTimelineSource loaded, bool face)
    {
        var destinations = LibrarySaveRules.Destinations(face);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, "Save to");
        ImGui.SameLine(0, Theme.Gap);
        ImGui.SetNextItemWidth(Theme.Scaled(100));
        using (var combo = ImRaii.Combo("##timeline-save-to", LibrarySaveRules.Name(destination)))
        {
            if (combo.Success)
            {
                foreach (var place in destinations)
                    if (ImGui.Selectable(LibrarySaveRules.Name(place), destination == place))
                        destination = place;
            }
            else if (ComboWheel.Steps() is var steps and not 0)
                destination = ComboWheel.Pick(destinations, destination, steps);
        }
        var choices = face ? "Save into a new mod, into the mod it comes from, or into one of your expression libraries."
            : "Save into a new mod, or into the mod it comes from.";
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(AnimationBakeRules.CompanionClip(loaded.Capture, loaded.Startup, loaded.Resource, destination) != null
                ? choices + "\n" + AnimationBakeRules.CompanionNote(loaded.Startup)
                : choices);
    }

    /// <summary>
    /// Save to Library's own fields: Library (your expression libraries, then New library with a name field) and Face name.
    /// </summary>
    private void DrawLibrary()
    {
        var list = libraries ?? [];
        var chosen = LibrarySaveRules.Choice([.. list.Select(l => l.ModDirectory)], libraryChoice);
        ImGui.SameLine(0, Theme.Scaled(16));
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, "Library");
        ImGui.SameLine(0, Theme.Gap);
        ImGui.SetNextItemWidth(Theme.Scaled(150));
        using (var combo = ImRaii.Combo("##timeline-library", chosen.Length == 0 ? "New library" : list.First(l => l.ModDirectory == chosen).Marker.Name))
        {
            if (combo.Success)
            {
                foreach (var library in list)
                    if (ImGui.Selectable(library.Marker.Name, chosen == library.ModDirectory))
                        libraryChoice = library.ModDirectory;
                if (list.Length > 0)
                    ImGui.Separator();
                if (ImGui.Selectable("New library", chosen.Length == 0))
                    libraryChoice = "";
            }
            else if (ComboWheel.Steps() is var steps and not 0)
                libraryChoice = ComboWheel.Pick([.. list.Select(l => l.ModDirectory), ""], chosen, steps);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(libraries == null ? "Reading your libraries." : "One of your expression libraries, or a new one.");
        if (chosen.Length == 0)
        {
            ImGui.SameLine(0, Theme.Gap);
            ImGui.SetNextItemWidth(Theme.Scaled(130));
            if (ImGui.InputTextWithHint("##timeline-library-name", "Library name", ref newLibraryName, 64))
                newLibraryName = PathRules.PlainDashes(newLibraryName);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("The new library's name.");
        }
        ImGui.SameLine(0, Theme.Scaled(16));
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, "Face name");
        ImGui.SameLine(0, Theme.Gap);
        ImGui.SetNextItemWidth(Theme.Scaled(110));
        ImGui.InputTextWithHint("##timeline-face-name", "Name", ref faceStem, 32);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(LibrarySaveRules.Motion(faceStem) is { } motion ? $"The face's name in the library: {motion}." : "The face's name in the library.");
    }

    /// <summary>The library the next save goes into, or null for a new one; with its name either way.</summary>
    private (InstantEditLibrary? Library, string Name) ChosenLibrary()
    {
        var list = libraries ?? [];
        var chosen = LibrarySaveRules.Choice([.. list.Select(l => l.ModDirectory)], libraryChoice);
        if (chosen.Length == 0)
            return (null, newLibraryName.Trim());
        var library = list.First(l => l.ModDirectory == chosen);
        return (library, library.Marker.Name);
    }

    /// <summary>Save's tooltip for Library: what goes into which library, under which name.</summary>
    private string LibraryTooltip(TimelineDocument doc)
    {
        var (library, name) = ChosenLibrary();
        return LibrarySaveRules.Tooltip(AnimationEditService.FaceChanged(doc), name, library == null, LibrarySaveRules.Motion(faceStem) ?? faceStem);
    }

    /// <summary>
    /// Why Save into a library waits (see <see cref="LibrarySaveRules.Missing"/>), or null. A new library's name is checked
    /// once per name, as the check looks at every race's face folders.
    /// </summary>
    private string? LibraryMissing(TimelineDocument doc, AnimationTimelineSource loaded)
    {
        var service = session.Animations!;
        var (library, name) = ChosenLibrary();
        string? newProblem = null;
        if (library == null && libraries is { } read)
        {
            if (newLibraryCheck is not { } check || check.Name != name || check.Libraries != read)
                newLibraryCheck = check = (name, read, service.NewLibraryProblem(name, read));
            newProblem = check.Problem;
        }
        var edited = AnimationEditService.FaceChanged(doc);
        // A new length is the face's own (baked into its entry); only the expression's events and face set stay behind.
        var opened = session.Fresh(loaded);
        var timelineChanged = opened.Face != null && opened.Document is { } before && (doc.Events != before.Events || doc.FacePack != before.FacePack);
        return LibrarySaveRules.Missing(timelineChanged, libraries != null, library == null, newProblem,
            LibrarySaveRules.FaceNameProblem(faceStem, loaded.MotionTarget.Clip.Name, edited, GameFaceNames()));
    }

    /// <summary>The faces the game has for the character's face, as the catalog lists them; null while it is being read.</summary>
    private IReadOnlySet<string>? GameFaceNames()
    {
        var data = session.Catalog.Data;
        if (data.Expressions.IsDefault || data.LipShapes.IsDefault)
            return null;
        if (gameFaces is not { } faces || faces.Data != data)
            gameFaces = faces = (data, data.Expressions.Concat(data.LipShapes).Select(item => item.Path).ToHashSet(StringComparer.Ordinal));
        return faces.Names;
    }

    /// <summary>
    /// Ends the preview, then saves the face into the chosen library: an edited face under its new name, an unedited one
    /// copied. The open clip stays as it is; only the library's file is written.
    /// </summary>
    private void StartLibrarySave(TimelineEditor current, AnimationTimelineSource loaded)
    {
        var (library, name) = ChosenLibrary();
        if (LibrarySaveRules.Motion(faceStem) is not { } motion)
            return;
        // Save waits for the game's faces when they matter (LibrarySaveRules.FaceNameProblem): an unedited face under its own name needs none.
        var save = new FaceLibrarySave(session.Fresh(loaded), current.Document, new FaceCopyTarget(library, library == null ? name : null), motion,
            GameFaceNames() ?? new HashSet<string>(StringComparer.Ordinal), false);
        saveError = null;
        saveMessage = null;
        timelinePreview.Stop();
        var ending = timelinePreview.TakeStopping();
        var service = session.Animations!;
        librarySave = save;
        libraryTask = Task.Run(async () =>
        {
            await ending;
            await session.Preview.StopAsync();
            Task<FaceCopyOutcome>? started = null;
            // The edit service starts jobs from the framework thread, where the observer it updates lives.
            await session.Framework.RunOnFrameworkThread(() =>
            {
                session.Playback.Stop();
                started = service.SaveFaceToLibraryAsync(save);
            });
            return await started!;
        });
    }

    /// <summary>A save into a library ended: its result, the question to replace a face of the same name, or why it failed.</summary>
    private void LibraryDone(Task<FaceCopyOutcome> task)
    {
        var save = librarySave;
        librarySave = null;
        if (task.IsCanceled)
            return;
        if (task.IsFaulted)
        {
            saveError = TimelineSession.ErrorText(task.Exception);
            return;
        }
        var outcome = task.Result;
        if (!outcome.Asks.IsEmpty && save != null)
            libraryAsk = (save, $"{outcome.LibraryName} has another face named {LibrarySaveRules.Stem(outcome.Asks[0])}. Replace it?");
        else if (outcome.JournalId is { } id)
        {
            lastSaveId = id;
            saveMessage = $"Saved into {outcome.LibraryName}.";
            // A new library is chosen from now on; the list is read again with it.
            if (save?.Target.Library == null && save?.Target.NewName is { } created)
                libraryChoice = created;
            libraries = null;
        }
        else
            saveMessage = $"{outcome.LibraryName} has this face already.";
    }

    /// <summary>The question when the library has another face of the save's name: replace it, or leave the library as it is.</summary>
    private void DrawLibraryAsk()
    {
        if (libraryAsk is not { } asking)
            return;
        if (!ImGui.IsPopupOpen(LibraryAskPopup))
            ImGui.OpenPopup(LibraryAskPopup);
        if (!ImGui.BeginPopupModal(LibraryAskPopup, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar))
            return;
        try
        {
            ImGui.TextUnformatted(asking.Text);
            ImGui.Spacing();
            if (ImGui.Button("Replace"))
            {
                libraryAsk = null;
                ImGui.CloseCurrentPopup();
                librarySave = asking.Save with { Replace = true };
                libraryTask = session.Animations!.SaveFaceToLibraryAsync(librarySave);
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
            {
                libraryAsk = null;
                ImGui.CloseCurrentPopup();
            }
        }
        finally
        {
            ImGui.EndPopup();
        }
    }

    /// <summary>
    /// The warning line's last entry: how many bones the animation moves that the repair or rebuild onto
    /// <paramref name="repair"/> leaves out of the save. Null without one, or when it keeps them all.
    /// </summary>
    private string? LeftOutWarning(AnimationClip clip, TimelineDocument doc, SkeletonStandard? repair)
    {
        if (repair is not { } standard || AnimationBones.Plan(clip, standard, doc.ExcludedBones, session.Rebuild?.Model) is not { Dropped.IsEmpty: false } plan)
            return null;
        var name = session.Rebuild?.Model is { } model ? AnimationBones.RebuildName(model, standard) : "Repair onto " + AnimationBones.StandardName(standard);
        return plan.Dropped.Length == 1 ? $"{name} leaves out 1 bone." : $"{name} leaves out {plan.Dropped.Length} bones.";
    }

    /// <summary>
    /// Why a face's save can't go in place: the face's file, and an expression's timeline when it changes too (<see cref="TimelineChanged"/>), must
    /// come from a mod (see <see cref="AnimationEditService.FaceInPlaceProblem"/>). Null when it can, and when no face is written.
    /// </summary>
    private string? FaceInPlaceProblem(AnimationTimelineSource loaded, TimelineDocument doc) =>
        AnimationEditService.FaceChanged(doc) ? AnimationEditService.FaceInPlaceProblem(session.Fresh(loaded), TimelineChanged(loaded, doc)) : null;

    /// <summary>
    /// Whether saving <paramref name="doc"/> can go in place: a changed face needs the face's file, and an expression's own
    /// timeline too when that changes as well (<see cref="AnimationEditService.FaceInPlaceProblem"/>); anything else needs
    /// the file it edits.
    /// </summary>
    internal static bool CanReplace(AnimationTimelineSource loaded, TimelineDocument? doc) =>
        doc != null && AnimationEditService.FaceChanged(doc)
            ? AnimationEditService.FaceInPlaceProblem(loaded, TimelineChanged(loaded, doc)) == null
            : AnimationResources.CanReplace(loaded.Resource);

    /// <summary>
    /// Whether saving <paramref name="doc"/>'s face writes its expression's own timeline as well: its events or face library
    /// changed, or the face's length, which retimes its face events.
    /// </summary>
    internal static bool TimelineChanged(AnimationTimelineSource loaded, TimelineDocument doc) =>
        loaded.Face != null && loaded.Document is { } opened &&
        (doc.Events != opened.Events || doc.FacePack != opened.FacePack || doc.Face is { LengthChanged: true });

    /// <summary> Why Save is unavailable now, shown on the button; null when it can run. </summary>
    private string? SaveMissing(TimelineEditor current, string? problem)
    {
        var library = destination == AnimationDestination.Library;
        if (problem != null)
            return problem;
        if (saveTask != null || pendingSaveId != null || linksWait || libraryTask != null || libraryAsk != null)
            return "Saving.";
        if (soundSave.Checking)
            return "Checking the mod.";
        if (soundSave.Asking)
            return "Answer the question about the sound first.";
        if (undoPending)
            return "Undoing the last save.";
        if (loading())
            return "Reading the timeline.";
        // An unedited face can go into a library: it is copied there as it is.
        if (!library && !current.Dirty && !session.LinksUnsaved && RepairTarget == null)
            return "Nothing changed since the last save.";
        if (current.Gesture != TimelineGesture.None)
            return "Finish the drag first.";
        if (session.Animations!.Busy)
            return "Another edit is running. Wait for it to end.";
        if (destination == AnimationDestination.NewMod && !ModPaths.IsSafeNewModName(modName.Trim()))
            return "Choose a valid mod name.";
        // A library takes the face's motion only, not the timeline's looks or sounds.
        if (library)
            return session.Source is { } loaded ? LibraryMissing(current.Document, loaded) : "Reading the timeline.";
        return effectLooks.LookMissing(current.Document) ?? soundFiles.SoundMissing(current.Document) ?? soundFiles.SoundCutMissing(current.Document);
    }

    /// <summary>
    /// Ends the preview (Penumbra must resolve the clip's own file again before a commit checks it),
    /// then hands the document to the service. The editor stays read-only until the job ends.
    /// </summary>
    private void StartSave(TimelineEditor current, AnimationTimelineSource loaded)
    {
        // A link track still building: the save waits for it, so the file holds what Live shows (see Poll).
        if (session.LinksBuilding)
        {
            linksWait = true;
            return;
        }
        var document = current.Document;
        // Taken here, on the framework thread: the bake runs on a worker.
        var links = TimelinePreview.LiveLinks(document, session.Links);
        var motionFrames = current.MotionFrames;
        var target = session.Fresh(loaded);
        var place = destination;
        var name = modName.Trim();
        var repair = target.Standalone || document.Face != null ? null : RepairTarget;
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
        savePrints = MotionLinks.Prints(document.Motion, links);
        jobClip = TimelineSession.ClipKey(loaded);
        KeepMotionForSave(target, document, repair);
        timelinePreview.Stop();
        var ending = timelinePreview.TakeStopping();
        Func<string, byte[]?> game = path => session.Data.GetFile(path)?.Data;
        saveTask = Task.Run(async () =>
        {
            // Props from other mods are saved as private copies of their snapshot, packaged like imports. Their files
            // (textures of several MB) are read here, off the framework thread; a file gone stops the save before it starts.
            (ImmutableDictionary<string, byte[]> Files, ImmutableDictionary<string, string> Aliases, string? Error) props;
            try { props = TimelineWindowViews.PropAdded(saved, game); }
            catch (Exception e) when (e is InvalidDataException or ArgumentException)
            {
                props = (ImmutableDictionary<string, byte[]>.Empty, ImmutableDictionary<string, string>.Empty, e.Message);
            }
            await ending;
            await session.Preview.StopAsync();
            if (props.Error != null)
                return new SaveStart(null, props.Error);
            var files = added.SetItems(props.Files);
            var aliases = props.Aliases;
            // The edit service starts jobs from the framework thread, where the observer it updates lives.
            return new SaveStart(await session.Framework.RunOnFrameworkThread(() =>
            {
                session.Playback.Stop();
                return service.EditTimeline(target, saved, place, name, files, beside, motionFrames, repair, rebuild,
                    replaced.IsEmpty ? null : replaced, copied.IsEmpty ? null : copied, cutSounds, aliases.IsEmpty ? null : aliases, links);
            }), null);
        });
    }

    /// <summary>How a save's start ended: the job's id (null when another job was running), or why it didn't start.</summary>
    private sealed record SaveStart(Guid? Id, string? Error);

    /// <summary>
    /// Every frame, also while another workspace shows: the mod's check of a save's sounds, the save's start and its result,
    /// an undo's result and the rebuild picked for you.
    /// </summary>
    public void Poll()
    {
        PickRebuild();
        soundSave.Poll(error => saveError = error);
        if (session.Animations is not { } service)
            return;
        // Your libraries are read while a face session can save into one, and again after a save or an undo into one.
        if (libraries == null && librariesTask == null && session.Editor is { Document.Face: not null })
            librariesTask = service.InstantEditLibrariesAsync();
        if (librariesTask is { IsCompleted: true } read)
        {
            librariesTask = null;
            libraries = read.IsCompletedSuccessfully ? read.Result : [];
        }
        if (libraryTask is { IsCompleted: true } library)
        {
            libraryTask = null;
            LibraryDone(library);
        }
        // A save waiting for a link track starts once the track is in, with the clip as it is then.
        if (linksWait && !session.LinksBuilding)
        {
            linksWait = false;
            if (session.Editor is { } editor && session.Source is { Document: not null } source)
                StartSave(editor, source);
        }
        if (saveTask is { IsCompleted: true } task)
        {
            saveTask = null;
            if (task.IsFaulted || task.IsCanceled)
            {
                saveError = "The preview could not be ended, so nothing was saved. " + TimelineSession.ErrorText(task.Exception);
                motionCarry = null;
            }
            else if (task.Result.Error is { } error)
            {
                saveError = error;
                motionCarry = null;
            }
            else if (task.Result.Id is { } id)
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
            saveError = "The save's result is unknown. Check Save history in the dock.";
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
                // The link layers are saved as the partner was then: a partner that changes since makes the clip unsaved again.
                session.SavedPrints = savePrints;
                lastSaveId = job;
                // A rebuilt file's header names the character's race, so it offers no rebuild. A repaired one is
                // repaired again when it reads back as a smaller skeleton (see PickRebuild).
                session.Rebuild = null;
                rebuildAutomatic = false;
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
            var libraryUndone = undoLibrary;
            undoLibrary = false;
            if (libraryUndone)
                libraries = null;
            if (!sameClip)
                return;
            if (service.LastFailed)
                saveError = service.Status;
            else
            {
                saveMessage = "The last save was undone.";
                // The file is another save's now, whose links aren't known.
                if (!libraryUndone)
                    session.SavedPrints = MotionLinks.NoPrints;
            }
            // A library's undo leaves the open clip as it is.
            if (libraryUndone)
                return;
            // While this window edits layers on a motion of its own, or brings back layers baked onto one, a file that is
            // again what an earlier layered edit saved brings that edit's layers back as the saved state, so it matches the file.
            reload(motionBase?.Clip == TimelineSession.ClipKey(session.Source!) || motionCarry is { Unsaved: true } && bakedOnBase);
        }
    }

    /// <summary>
    /// Every frame, before Live: once per file, when its match is in, a clip borrowed from another race is rebuilt for
    /// the character's, and a file a save repaired onto a standard of its own race is repaired onto it again, as it may
    /// read back as a smaller one (see <see cref="AnimationEditService.SavedRepair"/>). A rebuild picked that way whose
    /// motion can't be read on the target goes back to Keep its own.
    /// </summary>
    private void PickRebuild()
    {
        // A save, an undo or a read runs: the file the decision is for comes once it ends.
        if (session.Source is not { Document: not null, Standalone: false } loaded || Working || loading())
            return;
        var fresh = session.Fresh(loaded);
        // By the file's hash too: a save reads the clip back as another file, decided again.
        var key = TimelineSession.ClipKey(loaded) + "|" + loaded.Resource.Hash;
        if (rebuildDecided != key && fresh.Clip.Resolution is { State: SkeletonResolutionState.Matched } resolution)
        {
            rebuildDecided = key;
            if (AnimationBones.AutomaticRebuild(fresh.Clip, loaded.FileModel, session.Editor?.Document.ExcludedBones ?? [], loaded.HoldsOthers) is { } rebuild)
            {
                session.Rebuild = rebuild;
                rebuildAutomatic = true;
                rebuildFailed = null;
            }
            else if (session.Rebuild == null && session.Animations is { } service && AnimationEditService.SavedRepair(service.Recovery, loaded) is { } saved &&
                     AnimationBones.Standard(fresh.Clip, saved) is { } target && target.Skeleton.Fingerprint != resolution.Selected?.Skeleton.Fingerprint)
                session.Rebuild = new(null, target);
        }
        if (rebuildAutomatic && session.Rebuild is { Model: { } picked } && decode.FailedFor(loaded))
        {
            rebuildFailed = AnimationBones.RebuildFailedNote(picked, decode.Error!);
            session.Rebuild = null;
            rebuildAutomatic = false;
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
        if (DropsLayers(document, repair))
        {
            // An undo of a bake onto an earlier edit's base reads that edit back, so its layers come back with these.
            bakedOnBase = loaded.MotionTarget.BaseMotion != null && document.Replacement == null;
            motionBase = null;
            motionCarry = null;
            return;
        }
        if (document.Motion.IsEmpty && loaded.MotionTarget.BaseMotion == null)
            return;
        if (motionBase?.Clip != clip)
            motionBase = KeptMotion(loaded);
        motionCarry = (clip, document.Motion, document.NextMotionKey, false);
    }

    /// <summary>
    /// Whether saving <paramref name="document"/> with <paramref name="repair"/> reads the saved file back without its layers: they
    /// are baked in (<see cref="AnimationBakeRules.KeepsLayersOverSave"/>), or a motion from Blender is saved without layers, which
    /// is the saved file's own motion. What <see cref="KeepMotionForSave"/> and the footer's bake-in line decide by.
    /// </summary>
    internal static bool DropsLayers(TimelineDocument document, SkeletonStandard? repair) =>
        !AnimationBakeRules.KeepsLayersOverSave(document, repair) || document.Replacement != null && document.Motion.IsEmpty;

    /// <summary> The clip as read, with the base its layers apply to while this window has saved layers into it. </summary>
    public AnimationTimelineSource WithMotionBase(AnimationTimelineSource loaded)
    {
        if (motionBase is not { } kept || kept.Clip != TimelineSession.ClipKey(loaded) || loaded.Document == null)
            return loaded;
        return WithKeptMotion(loaded, kept);
    }

    /// <summary>
    /// The motion <paramref name="loaded"/>'s layers apply to, kept over a save: its base, else its file as read. An
    /// expression's are its face's (<see cref="AnimationTimelineSource.MotionTarget"/>).
    /// </summary>
    internal static (string Clip, byte[] Bytes, string Fingerprint, string Hash) KeptMotion(AnimationTimelineSource loaded)
    {
        var clip = TimelineSession.ClipKey(loaded);
        var motion = loaded.MotionTarget;
        return motion.BaseMotion is { } kept
            ? (clip, kept, motion.BaseFingerprint ?? string.Empty, motion.BaseHash ?? AnimationPap.Hash(kept))
            : (clip, motion.Pap, motion.FileFingerprint, motion.Resource.Hash);
    }

    /// <summary>
    /// <paramref name="loaded"/>, read again after a save, with <paramref name="kept"/> (<see cref="KeptMotion"/>) as the base its
    /// layers apply to; back at that motion, as after undoing the first save, the file's own motion is the base again.
    /// An expression's base goes onto its face.
    /// </summary>
    internal static AnimationTimelineSource WithKeptMotion(AnimationTimelineSource loaded, (string Clip, byte[] Bytes, string Fingerprint, string Hash) kept) =>
        kept.Hash == loaded.MotionTarget.Resource.Hash
            ? loaded.WithMotionTarget(motion => motion with { BaseMotion = null, BaseFingerprint = null, BaseHash = null })
            : loaded.WithMotionTarget(motion => motion with { BaseMotion = kept.Bytes, BaseFingerprint = kept.Fingerprint, BaseHash = kept.Hash });

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
        if (session.Animations is not { } service || current.MotionTarget.BaseMotion != null)
            return null;
        return AnimationEditService.SavedLayersJournal(service.Recovery, current);
    }

    /// <summary>
    /// The status line's notes from the save row: a rebuild picked for you that went back to Keep its own, or why the clip
    /// can't be rebuilt for the character's race; and the offer to bring back the layers the open clip was saved with,
    /// which new layers would otherwise go on top of.
    /// </summary>
    public IEnumerable<StatusNote> Notes()
    {
        if (session.Source is not { } current)
            return [];
        var notes = new List<StatusNote>();
        // Only body clips have the skeleton choice (see Draw).
        if (!current.Standalone && current.Document != null && session.Editor is { Document.Face: null } editor && session.Rebuild?.Model == null)
        {
            if (session.Rebuild == null && rebuildFailed != null)
                notes.Add(new StatusNote("rebuild-failed", StatusKind.Warning, rebuildFailed));
            else if (AnimationBones.RebuildNote(session.Fresh(current).Clip, current.FileModel, editor.Document.ExcludedBones, false, current.HoldsOthers) is { } cannot)
                notes.Add(new StatusNote("rebuild-note", StatusKind.Notice, cannot));
        }
        if (SavedLayers(current) is { } saved)
            notes.Add(new StatusNote("saved-layers", StatusKind.Notice, "Saved with edits you can open again.", "Edit again", () => editAgain(saved, current.Capture),
                ButtonTooltip: "Bring back the bone layers, cuts and length it was saved with, to change them.\nOtherwise they stay baked in, and new edits go on top.",
                ButtonEnabled: !session.ReadOnly));
        return notes;
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
        // A save into a library wrote only the library: the open clip and its edits stay as they are.
        var library = journal?.Request.Destination == AnimationDestination.Library;
        var blocked = journal == null ? "There is no save of this timeline to undo."
            : current.Dirty && !library ? "Save or undo your changes first."
            : service.Busy || session.ReadOnly || libraryTask != null || libraryAsk != null ? "Another edit is running. Wait for it to end."
            : null;
        using (ImRaii.Disabled(blocked != null))
        {
            if (ImGui.Button("Undo last save") && journal != null)
            {
                jobClip = session.Source is { } shown ? TimelineSession.ClipKey(shown) : null;
                if (!library)
                {
                    // The layers stay, as unsaved edits on the motion they apply to; layers keyed in cut or
                    // stretched frames don't fit the file the undo brings back. A save that baked them in keeps them in its journal.
                    if (session.Source != null && AnimationBakeRules.LayersBackAfterUndo(journal.Request) is { IsEmpty: false } baked)
                        motionCarry = (TimelineSession.ClipKey(session.Source), baked, current.Document.NextMotionKey, true);
                    else if (session.Source != null && !current.Document.Motion.IsEmpty && AnimationBakeRules.KeepsLayersOverSave(current.Document, RepairTarget))
                        motionCarry = (TimelineSession.ClipKey(session.Source), current.Document.Motion, current.Document.NextMotionKey, true);
                    // Undoing a rebuild brings the borrowed file back: the rebuild is picked for it again.
                    if (journal.Request.RepairModel != null)
                        rebuildDecided = null;
                    timelinePreview.Stop();
                }
                service.Undo(journal);
                undoPending = true;
                undoLibrary = library;
                saveError = null;
                saveMessage = null;
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(blocked ?? "Put the files back as they were before the last save.");
    }

    /// <summary>
    /// The save row's skeleton choice: keep the clip's own, repair it onto a standard skeleton so
    /// every bone sits at the index other players' skeletons and sync plugins expect, or, for a clip
    /// borrowed from another race, rebuild it onto a standard skeleton of the character's race. Its notes,
    /// and <paramref name="bakeIn"/> when the save bakes the layers in, are its tooltip (<see cref="RepairNotes"/>).
    /// </summary>
    private void DrawRepairChoice(AnimationTimelineSource loaded, string? bakeIn)
    {
        var clip = session.Fresh(loaded).Clip;
        var race = AnimationBones.RebuildRace(clip, loaded.FileModel, loaded.HoldsOthers);
        // The match can change under the choice: a skeleton chosen in the Bones window, another character.
        if (session.Rebuild is { } rebuild)
            session.Rebuild = AnimationBones.CurrentRebuild(clip, loaded.FileModel, rebuild, loaded.HoldsOthers);
        var excluded = session.Editor?.Document.ExcludedBones ?? [];
        // A rebuild is offered only onto the character's standards that hold every kept bone.
        IEnumerable<Choice> Rebuilds() => race == null ? [] : clip.Resolution!.ActorStandards
            .Where(s => AnimationBones.Plan(clip, s.Standard, excluded, race) is { Dropped.IsEmpty: true })
            .Select(s => new Choice(s.Standard, race));
        var current = session.Rebuild is { } shown ? new Choice(shown.Target.Standard, shown.Model) : new Choice(null, null);
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
            ImGui.SetTooltip(string.Join("\n", RepairNotes(clip, loaded, excluded, bakeIn)));
    }

    /// <summary>
    /// The lines of the skeleton choice's tooltip: what a repair does, the chosen standard's highest index and the bones it
    /// lacks, what a rebuild does or why it can't, the other races that play a rebuilt file, and the bake-in hint.
    /// </summary>
    private IEnumerable<string> RepairNotes(AnimationClip clip, AnimationTimelineSource loaded, IReadOnlyCollection<string> excluded, string? bakeIn)
    {
        yield return "Repair moves every bone to the index other players' skeletons use, whatever your skeleton mod adds.";
        yield return "Only bones the animation moves are written.";
        if (RepairTarget is { } repair && AnimationBones.Plan(clip, repair, excluded, session.Rebuild?.Model) is { } plan)
        {
            yield return plan.Dropped.IsEmpty
                ? $"Highest index {plan.HighestIndex}"
                : $"Highest index {plan.HighestIndex} · {plan.Dropped.Length} bones left out";
            if (!plan.Dropped.IsEmpty)
                yield return $"The {AnimationBones.StandardName(repair)} skeleton lacks " +
                             string.Join(", ", plan.Dropped.Take(8).Select(AnimationPresentation.BoneName)) +
                             (plan.Dropped.Length > 8 ? $" and {plan.Dropped.Length - 8} more." : ".");
        }
        if (((session.Rebuild == null ? rebuildFailed : null) ?? AnimationBones.RebuildNote(clip, loaded.FileModel, excluded, session.Rebuild?.Model != null, loaded.HoldsOthers)) is { } note)
            yield return note;
        if (session.Rebuild is { Model: { } rebuilding })
        {
            // The PAP load table is read once per clip and race, not every time the tooltip shows.
            if (borrowNote is not { } borrow || borrow.Path != clip.GamePath || borrow.Model != rebuilding)
                borrowNote = borrow = (clip.GamePath, rebuilding,
                    session.Animations?.PapTable is { } table && ushort.TryParse(rebuilding.AsSpan(1), out var actor)
                        ? AnimationBakeRules.BorrowNote(table.PlayedBy(clip.GamePath, Studio.Models.RaceInfo.PlayableCodes), actor)
                        : null);
            if (borrow.Note is { } others)
                yield return others;
        }
        if (bakeIn != null)
            yield return bakeIn;
    }

    /// <summary>One entry of the skeleton choice: keep (no standard), repair onto a standard, or with a race's model rebuild onto its standard.</summary>
    private sealed record Choice(SkeletonStandard? Standard, string? Model)
    {
        public string Name => this switch
        {
            { Standard: { } standard, Model: { } model } => AnimationBones.RebuildName(model, standard),
            { Standard: { } standard } => "Repair onto " + AnimationBones.StandardName(standard),
            _ => "Keep its own",
        };
    }

    /// <summary>
    /// Takes a skeleton choice into the session: a rebuild, or a repair without a race, so the bones are edited on its
    /// standard; keep clears it.
    /// </summary>
    private void Choose(AnimationClip clip, Choice choice)
    {
        session.Rebuild = choice.Standard is { } standard && AnimationBones.Standard(clip, standard, choice.Model) is { } target
            ? new(choice.Model, target) : null;
        rebuildAutomatic = false;
        rebuildFailed = null;
    }
}
