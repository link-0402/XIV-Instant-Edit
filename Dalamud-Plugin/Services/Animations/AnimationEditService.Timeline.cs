using InstantEdit.Services.PenumbraMods;
﻿using System.Collections.Immutable;
using InstantEdit.Models;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Services.Animations;

/// <summary>
/// One clip's timeline as the Timeline window loaded it: the animation file, which of its
/// entries the clip is, and that entry's timeline. An expression's timeline is a TMB file of its
/// own instead (<see cref="Standalone"/>): <see cref="Resource"/> is that file, <see cref="Pap"/>
/// is empty and <see cref="EntryIndex"/> is -1. <see cref="Document"/> is null when the timeline
/// can't be edited, and <see cref="ReadOnlyReason"/> says why.
/// </summary>
internal sealed record AnimationTimelineSource(AnimationCapture Capture, bool Startup, AnimationClip Clip,
    AnimationResource Resource, byte[] Pap, int EntryIndex, byte[] Tmb, TimelineDocument? Document, string? ReadOnlyReason,
    ImmutableDictionary<string, TimelineEffectInfo>? Effects = null)
{
    /// <summary>The motion's length in seconds, as the game plays it.</summary>
    public float Seconds => Clip.Duration;

    /// <summary>The timeline is an expression's own TMB file, not part of the clip's PAP. Its length can't change.</summary>
    public bool Standalone => EntryIndex < 0;

    /// <summary>
    /// The length in frames of the face an expression plays, by motion name, as the collection
    /// resolves it: a mod's animated face is far longer than the vanilla pose the editor's lists
    /// know. Empty for every other clip.
    /// </summary>
    public ImmutableDictionary<string, int> OwnFrames =>
        Standalone && Clip.Name.Length > 0 && TimelineDocument.MotionFrames(Clip.Duration) is { } frames
            ? ImmutableDictionary<string, int>.Empty.Add(Clip.Name, frames)
            : ImmutableDictionary<string, int>.Empty;

    /// <summary>Effects that stop although the animation goes on, in <paramref name="document"/>; see <see cref="TimelineEffectCheck"/>.</summary>
    public ImmutableArray<TimelineEffectCheck.Problem> EffectProblems(TimelineDocument document) =>
        Effects is { Count: > 0 } effects ? TimelineEffectCheck.Find(document, effects, Clip.IsLoop) : [];

    /// <summary>The fingerprint of the clip's binding in <see cref="Pap"/>, the file as loaded; empty when unknown.</summary>
    public string FileFingerprint { get; init; } = string.Empty;

    /// <summary>
    /// The motion bone layers apply to when it isn't the file's own: after the Timeline window saved
    /// layers into the file, the file as it was before, so the next save bakes the layers onto it again
    /// instead of onto the saved result. Null when the file's own motion is the base.
    /// </summary>
    public byte[]? BaseMotion { get; init; }

    /// <summary>The fingerprint of the clip's binding in <see cref="BaseMotion"/>.</summary>
    public string? BaseFingerprint { get; init; }

    /// <summary>The hash of <see cref="BaseMotion"/>.</summary>
    public string? BaseHash { get; init; }

    /// <summary>The PAP whose motion the layers apply to: <see cref="BaseMotion"/>, else the file's own.</summary>
    public byte[] MotionSource => BaseMotion ?? Pap;

    /// <summary>
    /// The clip as found in <see cref="MotionSource"/>, with that binding's fingerprint. After a save or an undo the
    /// file read is no longer the one captured, so the capture's own fingerprint would refuse it.
    /// </summary>
    public AnimationClip MotionClip => BaseMotion != null ? Clip with { BindingFingerprint = BaseFingerprint ?? string.Empty }
        : FileFingerprint.Length > 0 && FileFingerprint != Clip.BindingFingerprint ? Clip with { BindingFingerprint = FileFingerprint }
        : Clip;

    /// <summary>
    /// This source with <paramref name="replacement"/>, a motion from Blender, as the motion it edits: everything
    /// that reads the base motion (decode, Live, preview, save) then reads it. This source itself without one.
    /// </summary>
    public AnimationTimelineSource WithReplacement(MotionReplacement? replacement) =>
        replacement == null ? this : this with { BaseMotion = replacement.Pap, BaseFingerprint = replacement.Fingerprint, BaseHash = replacement.Hash };
}

/// <summary>
/// An edited clip, built and checked: the file it replaces (the animation, or an expression's
/// timeline), its new bytes and the motion's fingerprint.
/// </summary>
internal sealed record AnimationTimelineBuild(string GamePath, byte[] Bytes, string BindingFingerprint);

internal sealed partial class AnimationEditService
{
    /// <summary>
    /// Reads the clip's animation file as the player's collection resolves it, and takes its
    /// timeline apart for editing. Nothing in game changes.
    /// </summary>
    public async Task<AnimationTimelineSource> LoadTimelineAsync(AnimationCapture capture, bool startup, CancellationToken token)
    {
        var clip = startup ? capture.Startup ?? throw new InvalidOperationException("No linked startup animation was identified.") : capture.Clip;
        if (!startup && capture.ExpressionTimeline is { } expression)
            return await LoadExpressionTimelineAsync(capture, clip, expression.GamePath, token);
        var (resource, bytes) = await resources.ReadAsync(capture.CollectionId, clip.GamePath, token);
        var pap = new AnimationPap(bytes);
        var entry = pap.EntryIndex(clip.Name, clip.BindingIndex);
        var tmb = pap.TimelineBytes(entry);
        TimelineDocument document;
        try
        {
            // The motion's own length: mods often keep a timeline header copied from another clip.
            document = TimelineConvert.Read(TmbFile.Parse(tmb), TimelineDocument.MotionFrames(clip.Duration));
        }
        catch (InvalidDataException error)
        {
            return new AnimationTimelineSource(capture, startup, clip, resource, bytes, entry, tmb, null, error.Message);
        }
        // What the loaded file's clip is called by fingerprint: after a save the game may play either it or the clip as captured.
        var fingerprint = await framework.RunOnFrameworkThread(() => AnimationRuntime.InspectPap(bytes).GetValueOrDefault(clip.BindingIndex, string.Empty));
        return new AnimationTimelineSource(capture, startup, clip, resource, bytes, entry, tmb, document, null,
            await EffectsAsync(capture.CollectionId, document, token)) { FileFingerprint = fingerprint };
    }

    /// <summary>
    /// Reads the clip <paramref name="journal"/>'s edit saved, with that edit's bone layers back on the
    /// motion from before it (Edit again): saving them again replaces the edit's motion instead of stacking
    /// new layers onto it. Refused when the file changed since. Nothing in game changes.
    /// </summary>
    public async Task<AnimationTimelineSource> LoadTimelineAgainAsync(AnimationEditJournal journal, AnimationCapture capture,
        CancellationToken token)
    {
        if (EditAgainProblem(journal) is { } problem) throw new InvalidOperationException(problem);
        var loaded = await LoadTimelineAsync(capture, journal.Request.Timeline!.Startup, token);
        if (loaded.Document == null)
            return loaded;
        return await WithEditAgainAsync(journal, loaded, token);
    }

    /// <summary>
    /// Reads the clip after an undo. When its file is again exactly what an earlier layered edit saved,
    /// that edit's bone layers come back on the motion from before it, as Edit again brings them;
    /// otherwise, or when that motion can't be read, the clip comes as it is.
    /// </summary>
    public async Task<AnimationTimelineSource> LoadTimelineAfterUndoAsync(AnimationCapture capture, bool startup, CancellationToken token)
    {
        var loaded = await LoadTimelineAsync(capture, startup, token);
        if (SavedLayersJournal(Recovery, loaded) is not { } journal)
            return loaded;
        try { return await WithEditAgainAsync(journal, loaded, token); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Warning(e, "Could not bring back the bone layers of the edit the undone save went back to.");
            return loaded;
        }
    }

    /// <summary>
    /// The newest finished edit with bone layers that saved exactly <paramref name="current"/>'s file and
    /// can be edited again; null when there is none.
    /// </summary>
    internal static AnimationEditJournal? SavedLayersJournal(IEnumerable<AnimationEditJournal> recovery, AnimationTimelineSource current)
    {
        if (current.Document == null || current.Standalone)
            return null;
        foreach (var journal in recovery)
            if (journal.Request.Capture.CollectionId == current.Capture.CollectionId && journal.Request.Timeline?.Startup == current.Startup &&
                EditAgainProblem(journal) == null && EditAgainFileProblem(journal, current) == null)
                return journal;
        return null;
    }

    /// <summary><paramref name="loaded"/> with <paramref name="journal"/>'s layers back on its kept motion; throws when they can't come back.</summary>
    private async Task<AnimationTimelineSource> WithEditAgainAsync(AnimationEditJournal journal, AnimationTimelineSource loaded,
        CancellationToken token)
    {
        var edit = journal.Request.Timeline!;
        if (EditAgainFileProblem(journal, loaded) is { } changed) throw new IOException(changed);
        byte[] motion;
        try { motion = await File.ReadAllBytesAsync(Path.Combine(journals.DirectoryFor(journal.Id), MotionBaseName), token); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new IOException("The motion from before this edit is gone.");
        }
        if (AnimationPap.Hash(motion) != edit.MotionBaseHash) throw new IOException("The motion from before this edit is damaged.");
        var fingerprint = await framework.RunOnFrameworkThread(() =>
            AnimationRuntime.InspectPap(motion).GetValueOrDefault(loaded.Clip.BindingIndex, string.Empty));
        return WithSavedEdit(loaded, edit, motion, fingerprint);
    }

    /// <summary>
    /// Reads an expression's timeline (chara/action/facial/pose/smile.tmb), a TMB file of its own.
    /// It has no motion to measure, so its length is its header's, or for an endless header (-1, as
    /// permanent-expression mods write) the vanilla expression length.
    /// </summary>
    private async Task<AnimationTimelineSource> LoadExpressionTimelineAsync(AnimationCapture capture, AnimationClip clip, string gamePath,
        CancellationToken token)
    {
        var (resource, bytes) = await resources.ReadAsync(capture.CollectionId, gamePath, token);
        TimelineDocument document;
        try
        {
            var file = TmbFile.Parse(bytes);
            document = TimelineConvert.Read(file, TimelineDocument.ExpressionFrames(file.Length)) with { Expression = true };
        }
        catch (InvalidDataException error)
        {
            return new AnimationTimelineSource(capture, false, clip, resource, [], -1, bytes, null, error.Message);
        }
        return new AnimationTimelineSource(capture, false, clip, resource, [], -1, bytes, document, null,
            await EffectsAsync(capture.CollectionId, document, token));
    }

    /// <summary>
    /// What decides how long the effects the timeline's Async VFX entries start keep going, as the
    /// collection resolves them: their loops, a kill their own schedule sets, sounds that should
    /// loop but can't, and the copy that keeps them going. An effect that can't be read is left
    /// out, so it is never judged.
    /// </summary>
    private async Task<ImmutableDictionary<string, TimelineEffectInfo>> EffectsAsync(Guid collection,
        TimelineDocument document, CancellationToken token)
    {
        var effects = ImmutableDictionary.CreateBuilder<string, TimelineEffectInfo>(StringComparer.OrdinalIgnoreCase);
        // A sound counts as background music when it lasts at least one play of the motion.
        var motionSeconds = document.Length / (double)TimelineDocument.Fps;
        foreach (var path in document.Events.Where(e => e.Magic == "C173").Select(e => e.Path).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!path.EndsWith(".avfx", StringComparison.OrdinalIgnoreCase) || !AnimationDependencies.SafeGamePath(path))
                continue;
            // The check is a hint: an effect it can't read must never keep the timeline from opening.
            try
            {
                var avfx = (await resources.ReadAsync(collection, path, token)).Bytes;
                var loops = AnimationDependencies.ReadVfxLoops(avfx);
                var endsAt = TimelineEffectFiles.EndsAt(avfx);
                double? soundSeconds = null;
                var sounds = new List<(string Path, byte[] Scd)>();
                foreach (var sound in AnimationDependencies.ReadVfxPaths(avfx).Sounds)
                {
                    if (!sound.EndsWith(".scd", StringComparison.OrdinalIgnoreCase) || !AnimationDependencies.SafeGamePath(sound))
                        continue;
                    try
                    {
                        var scd = (await resources.ReadAsync(collection, sound, token)).Bytes;
                        if (TimelineEffectFiles.UnloopedSeconds(scd, motionSeconds) is not { } seconds)
                            continue;
                        soundSeconds = Math.Max(soundSeconds ?? 0, seconds);
                        sounds.Add((sound, scd));
                    }
                    catch (Exception e) when (e is not OperationCanceledException) { log.Debug(e, $"Could not read {sound}, which {path} plays."); }
                }
                TimelineImport? looping = null;
                if (endsAt != null || sounds.Count > 0)
                    try { looping = TimelineEffectFiles.LoopingCopy(path, avfx, endsAt != null, sounds, motionSeconds); }
                    catch (InvalidDataException e) { log.Debug(e, $"Could not make a copy of {path} that keeps going."); }
                effects[path] = new TimelineEffectInfo(loops, endsAt, soundSeconds, looping);
            }
            catch (Exception e) when (e is not OperationCanceledException) { log.Debug(e, $"Could not read the effect {path}."); }
        }
        return effects.ToImmutable();
    }

    /// <summary>
    /// Reads the effect <paramref name="gamePath"/> as the collection resolves it, for the Timeline's
    /// effect editor (see <see cref="TimelineEffectLooks"/>). Nothing in game changes.
    /// </summary>
    public async Task<byte[]> ReadEffectAsync(Guid collection, string gamePath, CancellationToken token)
    {
        if (!gamePath.EndsWith(".avfx", StringComparison.OrdinalIgnoreCase) || !AnimationDependencies.SafeGamePath(gamePath))
            throw new InvalidDataException("This is not an effect the game can load.");
        return (await resources.ReadAsync(collection, gamePath, token)).Bytes;
    }

    /// <summary>
    /// Reads the sound <paramref name="gamePath"/> as the collection resolves it, for the Timeline's
    /// sound inspector (see <see cref="TimelineSoundEdits"/>): where it comes from and its bytes.
    /// Nothing in game changes.
    /// </summary>
    public async Task<(AnimationResource Resource, byte[] Bytes)> ReadSoundAsync(Guid collection, string gamePath, CancellationToken token)
    {
        if (!gamePath.EndsWith(".scd", StringComparison.OrdinalIgnoreCase) || !AnimationDependencies.SafeGamePath(gamePath))
            throw new InvalidDataException("This is not a sound the game can load.");
        return await resources.ReadAsync(collection, gamePath, token);
    }

    /// <summary>
    /// Where the collection gets a sound's or effect's game path: a mod (by name), the game, or
    /// nowhere, as a music library's sound/lolo.scd is for people without that mod. Nothing in game changes.
    /// </summary>
    public async Task<(TimelinePathOrigin Origin, string? ModName)> PathOriginAsync(Guid collection, string gamePath, CancellationToken token)
    {
        try
        {
            var (resource, _) = await resources.ReadAsync(collection, gamePath, token);
            return (resource.ModName ?? resource.ModDirectory) is { } mod ? (TimelinePathOrigin.Mod, mod) : (TimelinePathOrigin.Game, null);
        }
        catch (FileNotFoundException)
        {
            return (TimelinePathOrigin.Missing, null);
        }
    }

    /// <summary>The native animation runtime, which the live motion display builds its uncompressed copies with.</summary>
    internal AnimationNative Native => native;

    /// <summary>
    /// Decodes the motion of the clip <paramref name="source"/> holds on the skeleton it was made for:
    /// the unedited motion bone layers apply to, for the live GPose display and the bone panels.
    /// Nothing in game changes.
    /// </summary>
    public async Task<MotionClip> DecodeMotionAsync(AnimationTimelineSource source, Action<string> status, CancellationToken token)
    {
        if (source.Standalone) throw new InvalidOperationException("An expression's face can't take bone layers yet.");
        var clip = source.Clip;
        if (clip.Resolution is not { State: SkeletonResolutionState.Matched, Selected: { } selected })
            throw new InvalidOperationException(clip.Resolution?.Reason ?? "Choose the skeleton this animation was made for first.");
        var skeleton = (await resources.ReadSkeletonAsync(source.Capture.CollectionId, selected.Source, token)).Bytes;
        var work = Path.Combine(Path.GetTempPath(), "XIV Instant Edit motion", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try { return await baker.DecodeAsync(source.MotionSource, skeleton, source.MotionClip, source.Capture, work, status, token, () => { }); }
        finally
        {
            try { Directory.Delete(work, true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Builds a motion from Blender (Motion from Blender) into the clip: <paramref name="read"/>, what Blender read,
    /// goes onto <paramref name="playing"/>, the motion as the timeline plays it now (see <see cref="BlenderMotion"/>),
    /// and is written in place of the clip's motion in <paramref name="source"/>'s motion source. Returns null, with
    /// nothing built, when that changes nothing for <paramref name="document"/>, the timeline as it was when the read
    /// began (see <see cref="BlenderMotion.ChangesNothing"/>). Nothing is saved and nothing in game changes.
    /// </summary>
    public async Task<MotionReplacement?> ReplaceMotionAsync(AnimationTimelineSource source, MotionClip playing, AnimationRead read,
        TimelineDocument document, Action<string> status, CancellationToken token)
    {
        if (source.Standalone) throw new InvalidOperationException("An expression's face has no motion to replace.");
        var clip = source.Clip;
        if (clip.Resolution is not { State: SkeletonResolutionState.Matched, Selected: { } selected })
            throw new InvalidOperationException(clip.Resolution?.Reason ?? "Choose the skeleton this animation was made for first.");
        status("Putting the motion together");
        var (motion, report) = await Task.Run(() => BlenderMotion.Build(BlenderMotion.Fill(playing, read), read, clip.GamePath, clip.Name), token);
        if (BlenderMotion.ChangesNothing(report, motion.Duration, playing.Duration, document))
            return null;
        if (TimelineDocument.MotionFrames(motion.Duration) is not { } frames || frames < TimelineDocument.MinLength || frames > TimelineDocument.MaxLength)
            throw new InvalidDataException("The motion from Blender is too long or too short.");
        var skeleton = (await resources.ReadSkeletonAsync(source.Capture.CollectionId, selected.Source, token)).Bytes;
        var work = Path.Combine(Path.GetTempPath(), "XIV Instant Edit motion", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        byte[] pap;
        try { pap = await baker.ReplaceAsync(source.MotionSource, skeleton, source.MotionClip, source.Capture, motion, work, status, token); }
        finally
        {
            try { Directory.Delete(work, true); } catch (IOException) { }
        }
        var fingerprint = await framework.RunOnFrameworkThread(() => AnimationRuntime.InspectPap(pap).GetValueOrDefault(clip.BindingIndex, string.Empty));
        return new MotionReplacement(pap, fingerprint, AnimationPap.Hash(pap), frames, read.Action, read.Armature, report);
    }

    /// <summary>
    /// Builds the edited clip and hands it to <paramref name="show"/>, whose message becomes the
    /// status: the GPose preview. A changed length bakes the motion first. Nothing is saved.
    /// <paramref name="motionFrames"/> are facial motions' own lengths (see <see cref="TimelineConvert.Apply"/>).
    /// </summary>
    public void PreviewTimeline(AnimationTimelineSource source, TimelineDocument document,
        Func<AnimationTimelineBuild, CancellationToken, Task<string>> show, IReadOnlyDictionary<string, int>? motionFrames = null,
        SkeletonStandard? repair = null) => Launch(async token =>
    {
        var request = TimelineRequest(source, document, AnimationDestination.NewMod, "", repair);
        // A preview is never journaled, so a length change bakes in a folder of its own.
        var work = Path.Combine(Path.GetTempPath(), "XIV Instant Edit timeline preview", request.Id.ToString("N"));
        try
        {
            var built = await BuildTimelineAsync(request, source, source.Standalone ? source.Tmb : source.Pap, document, motionFrames, work, token);
            Status = "Showing the preview";
            Status = await show(built, token);
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (IOException) { }
        }
    });

    /// <summary>
    /// Saves a timeline edit like a rebake: into a new mod or in place, journaled for Undo.
    /// <paramref name="added"/> holds the files the timeline needs that no mod has yet, such as
    /// imported sounds, by game path; they are packaged next to the animation. A file
    /// <paramref name="beside"/> names as a copy of another (see <see cref="TimelineImport.Beside"/>)
    /// is mapped wherever that file is instead, when the animation's mod is what provides it; when the
    /// mod maps that file to different files in different options, each gets its own copy, made by
    /// <paramref name="rebuild"/> (see <see cref="TimelineImport.Rebuild"/>).
    /// <paramref name="motionFrames"/> are facial motions' own lengths (see <see cref="TimelineConvert.Apply"/>).
    /// The document's excluded bones lose their tracks, and with <paramref name="repair"/> the motion is
    /// retargeted onto that standard skeleton, as the old rebakes did, in the same bake.
    /// In place, <paramref name="replaced"/> are the mod's own sound files the edit writes over (see
    /// <see cref="TimelineSoundEdits.Bake"/>): they are read, checked and journaled with the animation,
    /// so Undo restores them too. <paramref name="copied"/> are the sounds copies were made from, by
    /// game path, with the hash of the bytes they were made from (see <see cref="TimelineSoundBake.Bases"/>):
    /// each must still be what the collection gives.
    /// </summary>
    /// <returns>The job's id, which <see cref="LastResult"/> carries once it ends; null when another job is running.</returns>
    public Guid? EditTimeline(AnimationTimelineSource source, TimelineDocument document, AnimationDestination destination,
        string modName, IReadOnlyDictionary<string, byte[]> added, IReadOnlyDictionary<string, string>? beside = null,
        IReadOnlyDictionary<string, int>? motionFrames = null, SkeletonStandard? repair = null,
        IReadOnlyDictionary<string, Func<byte[], byte[]>>? rebuild = null,
        IReadOnlyDictionary<string, TimelineSoundReplacement>? replaced = null, IReadOnlyDictionary<string, string>? copied = null,
        IReadOnlyDictionary<string, string>? cutSounds = null)
    {
        if (Busy) return null;
        var request = TimelineRequest(source, document, destination, modName.Trim(), repair, cutSounds);
        Launch(async token =>
        {
            if (destination == AnimationDestination.NewMod && !ModPaths.IsSafeNewModName(request.ModName))
                throw new InvalidDataException("Choose a valid mod name before saving.");
            if (source.Document == null) throw new InvalidOperationException(source.ReadOnlyReason ?? "This timeline can't be edited.");
            if (replaced is { Count: > 0 } && destination != AnimationDestination.InPlace)
                throw new InvalidDataException("A mod's sound can only be changed when saving in place.");
            var capture = request.Capture;
            CheckBones(request, source);
            await CheckActorAsync(capture, false);
            // The file must still be the one the editor loaded, or the edit would drop newer work.
            await resources.CheckAsync(capture.CollectionId, [source.Resource], token);
            // The file the timeline lives in: the clip's PAP, or an expression's own TMB.
            var edited = source.Resource.GamePath;
            var manifest = await resources.ManifestAsync(capture, [edited, .. replaced?.Keys ?? []], destination, token);
            if (manifest.Resources.First(r => string.Equals(r.GamePath, edited, StringComparison.OrdinalIgnoreCase)).Hash != source.Resource.Hash)
                throw new IOException("The animation file changed while saving. Open the timeline again.");
            // A sound copy made from bytes the collection no longer gives would drop the newer sound.
            foreach (var (path, hash) in copied ?? ImmutableDictionary<string, string>.Empty)
            {
                var (now, _) = await resources.ReadAsync(capture.CollectionId, path, token);
                TimelineSoundUsage.CheckCopied(path, hash, now.Hash);
            }
            var built = await BuildTimelineAsync(request, source, manifest.Files[edited], document, motionFrames,
                journals.DirectoryFor(request.Id), token);
            var (files, reused, owners, copies) = await AddedFilesAsync(capture, destination, source.Resource, added, beside, rebuild, token);
            var outputs = ImmutableDictionary<string, byte[]>.Empty.Add(built.GamePath, built.Bytes);
            // The mod's own sounds the edit changes are written over like the animation, at a fresh path.
            if (replaced is { Count: > 0 })
                outputs = TimelineSoundUsage.ReplacedOutputs(outputs, manifest.Resources, source.Resource, replaced);
            // The motion the layers were baked onto goes with the journal, so the edit can be opened again.
            var motionBase = MotionBase(source, document, manifest.Files[edited], request.RepairTarget);
            var journaled = motionBase == null ? request
                : request with { Timeline = request.Timeline! with { MotionBaseHash = AnimationPap.Hash(motionBase) } };
            var journal = await commits.PrepareAsync(journaled, manifest, outputs, token,
                files.Count > 0 ? (edited, files, owners, copies) : null);
            if (motionBase != null)
                KeepMotionBase(journal.Id, motionBase);
            if (reused.Count > 0)
            {
                journal.ReusedFiles = reused;
                journals.Save(journal);
            }
            recovery = recovery.Insert(0, journal);
            await commits.CommitAsync(journal, manifest, async () =>
            {
                await CheckActorAsync(capture, false);
                if (AnimationBakeRules.NewMotion(document, request.RepairTarget)) await resources.CheckSkeletonsAsync(capture, [source.Clip], token);
                await framework.RunOnFrameworkThread(() => CheckIdentityOnFramework(capture, false));
            }, message =>
            {
                Status = message;
                if (message.StartsWith("Committing", StringComparison.Ordinal)) CanCancel = false;
            }, token);
            if (destination == AnimationDestination.InPlace)
            {
                var updated = capture.Sources.Concat(capture.ExpressionTimeline is { } expression ? [expression] : []).Select(s =>
                    journal.Files.FirstOrDefault(f => f.AddedFor == null && f.GamePath == s.GamePath) is { } file
                        ? s with
                        {
                            Hash = file.AfterHash, ResolvedPath = file.Target,
                            ModDirectory = file.ModDirectory, ModRoot = file.ModRoot, RelativePath = file.RelativePath,
                        }
                        : s).ToImmutableArray();
                await framework.RunOnFrameworkThread(() => Observer.UpdateSources(capture.Id, updated));
            }
            journal.State = "Completed"; journal.PoseClearOutcome = "NotApplicable";
            journal.Message = AnimationBakeRules.NewMotion(document, request.RepairTarget) ? "Animation saved." : "Timeline saved.";
            journals.Save(journal); Status = journal.Message;
        }, request.Id, source.Clip);
        return request.Id;
    }

    /// <summary>
    /// The request saving <paramref name="document"/> makes. <paramref name="cutSounds"/> are the copies its
    /// sounds were cut into with the motion and what each was cut from, which Edit again plays again.
    /// </summary>
    internal static AnimationBakeRequest TimelineRequest(AnimationTimelineSource source, TimelineDocument document,
        AnimationDestination destination, string modName, SkeletonStandard? repair = null, IReadOnlyDictionary<string, string>? cutSounds = null)
        => new(Guid.NewGuid(), source.Capture, destination, modName, false, ImmutableHashSet<PoseBoneId>.Empty, PoseComponents.None,
            AnimationOperation.EditTimeline,
            ExcludedBones: source.Standalone ? [] : document.ExcludedBones,
            RepairTarget: source.Standalone ? null : repair,
            Timeline: new AnimationTimelineEdit(source.Startup, source.EntryIndex, RetimeSeconds(document),
                document.MotionChanged ? document.Motion : [], Cuts: SavedCuts(document),
                CutSounds: cutSounds is { Count: > 0 } ? cutSounds.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase) : null));

    /// <summary>
    /// The cut list a save records: the document's when it cuts, and for a stretch a whole one of the
    /// motion it stretched, so Edit again knows that motion's length.
    /// </summary>
    internal static MotionCuts? SavedCuts(TimelineDocument document) =>
        document.Cut ? document.Cuts : document.Stretched ? MotionCuts.WholeOf(document.MotionLength) : null;

    /// <summary>
    /// Refuses bones left out or a repair the clip can't take: as the old rebakes refused them, before
    /// anything is read.
    /// </summary>
    private static void CheckBones(AnimationBakeRequest request, AnimationTimelineSource source)
    {
        if (request.ExcludedBones.IsEmpty && request.RepairTarget == null)
            return;
        if (source.Standalone)
            throw new InvalidOperationException("An expression's face keeps every bone.");
        var clip = source.Clip;
        if (AnimationBones.Problem(clip, request.ExcludedBones) is { } exclusion)
            throw new InvalidDataException(exclusion);
        if (request.RepairTarget is { } target && AnimationBones.Standard(clip, target) == null)
            throw new InvalidOperationException($"No {AnimationBones.StandardName(target)} skeleton was found for this animation.");
    }

    /// <summary>
    /// How long the motion becomes, in seconds; null when it stays as it is. Cuts always carry it, even
    /// when the length stays (a seam blend alone). The document's length is the motion's own (see
    /// <see cref="TimelineConvert.Read"/>), not a mod's copied header length.
    /// </summary>
    internal static float? RetimeSeconds(TimelineDocument document) =>
        document.Stretched || document.Cut ? document.Length / (float)TimelineDocument.Fps : null;

    /// <summary>Whether saving <paramref name="document"/> bakes the motion: a new length, bone layers that change it, or bones left out.</summary>
    internal static bool ChangesMotion(TimelineDocument document) => AnimationBakeRules.ChangesMotion(document);

    /// <summary>The file beside a journal that holds the motion its edit's bone layers were baked onto.</summary>
    internal const string MotionBaseName = "motion-base.pap";

    /// <summary>
    /// The file whose motion saving <paramref name="document"/> bakes its edits onto (layers, cuts, a new
    /// length): the base the window keeps, else <paramref name="file"/>, the clip's file as it is. Null when
    /// the save bakes none of them, or leaves out bones or repairs too, which bakes them in for good.
    /// </summary>
    internal static byte[]? MotionBase(AnimationTimelineSource source, TimelineDocument document, byte[] file, SkeletonStandard? repair = null) =>
        source.Standalone || !(document.MotionChanged || document.Cut || document.Stretched) || AnimationBakeRules.BakesLayersIn(document, repair)
            ? null : source.BaseMotion ?? file;

    /// <summary>Keeps the motion beside the journal. Without it the edit still saves; it just can't be opened again.</summary>
    private void KeepMotionBase(Guid id, byte[] motion)
    {
        try { AnimationJournalStore.WriteAtomic(Path.Combine(journals.DirectoryFor(id), MotionBaseName), motion); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Warning(e, "Could not keep the motion from before a timeline edit, so it can't be edited again.");
        }
    }

    /// <summary>
    /// Why <paramref name="journal"/>'s edit can't be opened again with its bone layers, cuts and length;
    /// null when it can: a finished timeline edit that made some of them and kept the motion from before it.
    /// </summary>
    internal static string? EditAgainProblem(AnimationEditJournal journal) => journal switch
    {
        { Request: not { Operation: AnimationOperation.EditTimeline, Timeline: not null } } or { Request.Timeline: { Motion.IsEmpty: true, Cuts: null } } =>
            "Only edits with bone layers, cuts or a new length can be edited again.",
        { State: "Undone" } => "This edit was undone.",
        { State: "FilesUndone" } => "This edit was not saved.",
        { State: not "Completed" } => "This edit did not finish.",
        { Request.ExcludedBones.IsEmpty: false } or { Request.RepairTarget: not null } =>
            "This edit left out bones or repaired the skeleton, which can't be opened again.",
        { Request.Timeline.MotionBaseHash: null } => "This edit kept no copy of the motion from before it.",
        _ => null,
    };

    /// <summary>
    /// The capture to open <paramref name="journal"/>'s clip again with: the newest the listener saw of the
    /// same clip in the same collection, the same character's first; else the one the edit was made from.
    /// </summary>
    internal static AnimationCapture EditAgainCapture(AnimationEditJournal journal, IEnumerable<AnimationCapture> history)
    {
        var saved = journal.Request.Capture;
        var startup = journal.Request.Timeline?.Startup == true;
        if ((startup ? saved.Startup : saved.Clip) is not { } clip)
            return saved;
        var same = history.Where(capture => capture.CollectionId == saved.CollectionId && (startup || capture.ExpressionTimeline == null) &&
            (startup ? capture.Startup : capture.Clip) is { } other && other.GamePath == clip.GamePath && other.Name == clip.Name &&
            other.BindingIndex == clip.BindingIndex).ToArray();
        return same.FirstOrDefault(capture => capture.ActorId == saved.ActorId) ?? same.FirstOrDefault() ?? saved;
    }

    /// <summary>
    /// Why <paramref name="loaded"/>, the clip as it is now, can't take <paramref name="journal"/>'s layers
    /// back: its file must still be the one that edit saved. Null when it can.
    /// </summary>
    internal static string? EditAgainFileProblem(AnimationEditJournal journal, AnimationTimelineSource loaded) =>
        journal.Request.Timeline is not { } edit || loaded.Standalone || loaded.EntryIndex != edit.EntryIndex ||
        !journal.Files.Any(f => f.AddedFor == null && f.GamePath == loaded.Resource.GamePath && f.AfterHash == loaded.Resource.Hash)
            ? "The animation was changed after this edit."
            : null;

    /// <summary>
    /// <paramref name="loaded"/> with <paramref name="edit"/>'s layers and cuts back, on the motion from before
    /// it. The saved timeline's events are already in the edit's output frames, and its length is the
    /// output's, so a stretch comes back as one. Sounds the edit cut with the motion play their whole
    /// files again, chosen to be cut, so saving again cuts them once.
    /// </summary>
    internal static AnimationTimelineSource WithSavedEdit(AnimationTimelineSource loaded, AnimationTimelineEdit edit, byte[] motion,
        string fingerprint) =>
        loaded.Document is not { } document ? loaded : loaded with
        {
            Document = TimelineSoundEdits.Uncut(document with { Motion = edit.Motion, Cuts = edit.Cuts }, edit.CutSounds),
            BaseMotion = motion,
            BaseFingerprint = fingerprint,
            BaseHash = edit.MotionBaseHash,
        };

    /// <summary>
    /// Writes the edited timeline into the clip's file. A changed length resamples the motion and
    /// bone layers change it, both in a bake that needs the skeleton the clip was made for.
    /// Everything else in the file must come out exactly as it went in.
    /// </summary>
    private async Task<AnimationTimelineBuild> BuildTimelineAsync(AnimationBakeRequest request, AnimationTimelineSource source,
        byte[] pap, TimelineDocument document, IReadOnlyDictionary<string, int>? motionFrames, string workDirectory, CancellationToken token)
    {
        if (source.Document == null) throw new InvalidOperationException(source.ReadOnlyReason ?? "This timeline can't be edited.");
        var clip = source.Clip;
        var timeline = TimelineConvert.Apply(source.Tmb, document, motionFrames);
        if (source.Standalone)
        {
            // An expression's timeline is the whole file; the face it plays stays as it is.
            if (document.LengthChanged) throw new InvalidOperationException("An expression's timeline keeps its length.");
            if (document.MotionChanged) throw new InvalidOperationException("An expression's face can't take bone layers yet.");
            if (!pap.AsSpan().SequenceEqual(source.Tmb)) throw new IOException("The timeline file changed while saving. Open the timeline again.");
            _ = TmbFile.Parse(timeline);
            return new AnimationTimelineBuild(source.Resource.GamePath, timeline, clip.BindingFingerprint);
        }
        // With a base (the file before the window saved layers into it) the motion comes from the base:
        // a bake applies the layers to it again, and with nothing to bake its own motion comes back.
        var motion = source.BaseMotion ?? pap;
        var baked = AnimationBakeRules.ChangesMotion(document, request.RepairTarget);
        if (baked)
        {
            native.EnsureAvailable();
            if (clip.Resolution is not { State: SkeletonResolutionState.Matched, Selected: { } selected })
                throw new InvalidOperationException(clip.Resolution?.Reason ?? (document.LengthChanged
                    ? "Choose the skeleton this animation was made for before changing its length."
                    : "Choose the skeleton this animation was made for before changing its motion."));
            // Layers on bones the standard lacks would be lost without a word.
            if (document.MotionChanged && request.RepairTarget is { } target && AnimationBones.Standard(clip, target) is { } standard)
                MotionEvaluator.CheckBones(document.Motion, standard.Skeleton);
            await resources.CheckSkeletonsAsync(request.Capture, [clip], token);
            var skeleton = (await resources.ReadSkeletonAsync(request.Capture.CollectionId, selected.Source, token)).Bytes;
            Directory.CreateDirectory(workDirectory);
            motion = await baker.BakeAsync(source.BaseMotion ?? pap, skeleton, source.MotionClip, request,
                workDirectory, message => Status = message, token, () => CheckIdentityOnFramework(request.Capture, true));
        }
        var result = new AnimationPap(motion).WithTimeline(source.EntryIndex, timeline);
        CheckTimelineOutput(pap, result, source.EntryIndex, timeline, !baked && source.BaseMotion == null);
        // Unbaked, the motion is the file's own: what the game plays once it loads the file.
        var fingerprint = baked || source.BaseMotion != null
            ? await framework.RunOnFrameworkThread(() => AnimationRuntime.InspectPap(result).GetValueOrDefault(clip.BindingIndex, ""))
            : source.MotionClip.BindingFingerprint;
        return new AnimationTimelineBuild(source.Resource.GamePath, result, fingerprint);
    }

    /// <summary>
    /// The edited file must differ from the original only in the edited timeline, and in the
    /// motion when the length changed: same entries, every other timeline byte for byte, and a
    /// timeline that reads back cleanly.
    /// </summary>
    internal static void CheckTimelineOutput(byte[] original, byte[] edited, int entry, byte[] timeline, bool sameMotion)
    {
        var before = new AnimationPap(original);
        var after = new AnimationPap(edited);
        if (!before.Entries.SequenceEqual(after.Entries) || before.ModelId != after.ModelId || before.ModelType != after.ModelType)
            throw new InvalidDataException("Saving the timeline changed the animation's entries.");
        if (sameMotion && !before.Havok.AsSpan().SequenceEqual(after.Havok))
            throw new InvalidDataException("Saving the timeline changed the motion.");
        for (var i = 0; i < before.Entries.Length; i++)
        {
            var expected = i == entry ? timeline : before.TimelineBytes(i);
            if (!after.TimelineBytes(i).AsSpan().SequenceEqual(expected))
                throw new InvalidDataException("Saving the timeline changed another of the file's timelines.");
        }
        _ = TmbFile.Parse(after.TimelineBytes(entry));
    }

    /// <summary>
    /// The added files to package. In place, a file the collection already resolves to the same
    /// bytes (an import saved before) is left as it is rather than packaged twice; when a mod
    /// provides it, it is listed as reused so undoing the edit that added it waits for this one.
    /// <c>Owners</c> holds the copies mapped beside their original: those whose original the
    /// edited animation's mod maps itself. Every other file is mapped with the animation.
    /// <c>Copies</c> holds those whose original the mod maps to several files (see <see cref="OwnCopies"/>).
    /// </summary>
    private async Task<(IReadOnlyDictionary<string, byte[]> Files, List<string> Reused, IReadOnlyDictionary<string, string> Owners,
            IReadOnlyDictionary<string, AnimationCommitService.AddedCopies> Copies)>
        AddedFilesAsync(AnimationCapture capture, AnimationDestination destination, AnimationResource edited,
            IReadOnlyDictionary<string, byte[]> added, IReadOnlyDictionary<string, string>? beside,
            IReadOnlyDictionary<string, Func<byte[], byte[]>>? rebuild, CancellationToken token)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var reused = new List<string>();
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        var copies = new Dictionary<string, AnimationCommitService.AddedCopies>(StringComparer.Ordinal);
        foreach (var (gamePath, bytes) in added)
        {
            if (!AnimationCommitService.AddedGamePath(gamePath)) throw new InvalidDataException($"{gamePath} can't be added to the mod.");
            if (destination == AnimationDestination.InPlace)
            {
                try
                {
                    var (resource, _) = await resources.ReadAsync(capture.CollectionId, gamePath, token);
                    if (resource.Hash == AnimationPap.Hash(bytes))
                    {
                        if (resource.ModDirectory != null) reused.Add(gamePath);
                        continue;
                    }
                    if (resource.ModDirectory != null)
                        throw new IOException(resource.ModDirectory == edited.ModDirectory
                            ? $"This mod already has a different {gamePath}. Choose Create new mod."
                            : $"Another mod already provides {gamePath}. Choose Create new mod.");
                }
                catch (FileNotFoundException) { }
                // A copy follows its original's options (a toggle that turns the original off turns it off too).
                if (beside?.GetValueOrDefault(gamePath) is { } original && AnimationDependencies.SafeGamePath(original) &&
                    edited.ModRoot is { } root && ModMetadata.ModMapsGamePath(root, original))
                {
                    try
                    {
                        var (provider, _) = await resources.ReadAsync(capture.CollectionId, original, token);
                        if (provider.ModDirectory == edited.ModDirectory && provider.ModRoot is { } providerRoot &&
                            PathRules.SamePhysicalPath(providerRoot, root))
                        {
                            var own = OwnCopies(root, original, provider.ResolvedPath, rebuild?.GetValueOrDefault(gamePath),
                                problem => log.Warning($"{gamePath}: {problem}"));
                            owners[gamePath] = original;
                            if (own != null)
                                copies[gamePath] = own;
                        }
                    }
                    // Unreadable, the copy is simply mapped with the animation.
                    catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { }
                }
            }
            result[gamePath] = bytes;
        }
        return (result, reused, owners, copies);
    }

    /// <summary>
    /// For a copy made from <paramref name="resolved"/>, the file the mod maps <paramref name="original"/> to
    /// in the options in use: when the mod maps it to other files in other options too (a "Gold" and a
    /// "Silver" effect), the copy is mapped only where the original is that file, and each other file gets
    /// a copy of its own made by <paramref name="rebuild"/>, at the same game path. A file no copy can be
    /// made from gets none: there the timeline's copy is missing, never another option's. Options that
    /// don't map the original get no copy, as before. Null when the mod maps the original to one file,
    /// or the one in use isn't among its files.
    /// </summary>
    internal static AnimationCommitService.AddedCopies? OwnCopies(string root, string original, string resolved,
        Func<byte[], byte[]>? rebuild, Action<string> skipped)
    {
        var mapped = ModMetadata.AnimationMappedFiles(root, original);
        if (mapped.Count < 2 || mapped.FirstOrDefault(file => PathRules.SamePhysicalPath(Path.Combine(root, file), resolved)) is not { } from)
            return null;
        var others = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in mapped.Where(file => !string.Equals(file, from, StringComparison.OrdinalIgnoreCase)))
        {
            if (rebuild == null)
            {
                skipped($"no copy can be made from {file}, so the options using it don't get one.");
                continue;
            }
            try
            {
                var path = Path.GetFullPath(Path.Combine(root, file));
                var info = new FileInfo(path);
                if (!PathRules.IsPathWithin(path, root) || !info.Exists || info.Length > AnimationPap.MaxFileSize)
                    throw new IOException($"{file} is missing or can't be read.");
                others[file] = rebuild(File.ReadAllBytes(path));
            }
            // Another option's file may be anything; one that can't be copied must not keep the edit from saving.
            catch (Exception e) when (e is not OperationCanceledException)
            {
                skipped($"no copy could be made from {file} ({e.Message}), so the options using it don't get one.");
            }
        }
        return new AnimationCommitService.AddedCopies(from, others);
    }
}
