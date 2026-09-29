using System.Collections.Immutable;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using InstantEdit.Models;

namespace InstantEdit.Services.Animations;

internal sealed class AnimationEditService : IDisposable
{
    private readonly IFramework framework;
    private readonly IObjectTable objects;
    private readonly PenumbraService penumbra;
    private readonly LivePoseAdapter poses;
    private readonly AnimationNative native;
    private readonly AnimationBakeService baker;
    private readonly AnimationResources resources;
    private readonly AnimationJournalStore journals;
    private readonly AnimationCommitService commits;
    private readonly AnimationCatalog catalog;
    private readonly AnimationSkeletonIndex skeletons;
    private readonly IPluginLog log;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? cancellation;
    private Task work = Task.CompletedTask;
    private bool disposed;
    private ImmutableArray<AnimationEditJournal> recovery;
    private LivePoseOffsetBackup? offsetBackup;
    public AnimationObserver Observer { get; }
    /// <summary>PAP files no character is playing, such as a mod's animations in the Mod Browser.</summary>
    public AnimationFiles Files { get; }
    public bool Busy => !work.IsCompleted;
    public bool CanCancel { get; private set; }
    public string Status { get; private set; } = "Select an animation and the offsets to bake.";
    public ImmutableArray<AnimationEditJournal> Recovery => recovery;
    public AnimationEditResult? LastResult { get; private set; }
    /// <summary>Whether the most recent operation of any kind ended in an error.</summary>
    public bool LastFailed { get; private set; }
    public bool CanReapplyOffsets => offsetBackup != null;
    public void StartObservation() => Observer.Start();
    public void StopObservation() => Observer.Stop();

    public AnimationEditService(IDalamudPluginInterface pi, IFramework framework, IObjectTable objects,
        IDataManager data, ISigScanner scanner, PenumbraService penumbra, ModelBackupStore backups, Configuration configuration, IPluginLog log)
    {
        this.framework = framework; this.objects = objects; this.penumbra = penumbra; this.log = log;
        native = new AnimationNative(scanner);
        poses = new LivePoseAdapter(pi, objects);
        catalog = new AnimationCatalog(data);
        resources = new AnimationResources(penumbra, data, framework, log);
        // Journals, the LivePose offset backup and the pre-edit file backups are the only
        // copies Undo relies on, so they live in the config folder: temp cleaners empty
        // the default %TEMP% cache (issue #8). Records from older versions move over once.
        var recoveryRoot = pi.ConfigDirectory.FullName;
        try { AnimationJournalStore.ImportLegacy(TextureFiles.CacheRootFor(configuration.TextureCacheDirectory), recoveryRoot); }
        catch (Exception error) { log.Warning(error, "Could not move animation recovery records out of the cache."); }
        journals = new AnimationJournalStore(recoveryRoot, message => log.Warning("{Message}", message));
        recovery = journals.Load().ToImmutableArray();
        offsetBackup = journals.LoadOffsetBackup();
        commits = new AnimationCommitService(penumbra, resources, new ModelBackupStore(recoveryRoot), journals, backups);
        baker = new AnimationBakeService(native, framework);
        skeletons = new AnimationSkeletonIndex(penumbra, resources, framework, log, () => TextureFiles.EnsureCacheRoot(configuration.TextureCacheDirectory));
        Observer = new AnimationObserver(framework, objects, penumbra, native, resources, poses, catalog, skeletons, log);
        Files = new AnimationFiles(framework, objects, penumbra, resources, skeletons);
        Observer.CharacterChanged += Cancel;
    }
    public void Cancel() { if (CanCancel) cancellation?.Cancel(); }

    /// <summary>The skeleton library saved in the cache folder, which source skeletons are matched from.</summary>
    public SkeletonLibraryStatus SkeletonLibrary => skeletons.Status;
    public DateTime? SavedSkeletonLibraryUtc() => skeletons.SavedLibraryUtc();

    /// <summary>Builds the skeleton library again, then matches every listed animation to a skeleton again.</summary>
    public void RebuildSkeletonLibrary() => _ = RebuildSkeletonLibraryAsync();

    private async Task RebuildSkeletonLibraryAsync()
    {
        try
        {
            await skeletons.RebuildAsync(lifetime.Token);
            if (!disposed) await framework.RunOnFrameworkThread(Observer.RefreshSkeletonMatches);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { log.Warning(e, "Could not rebuild the skeleton library."); }
    }

    public void Refresh(AnimationCapture capture, Action<AnimationCapture> accept) => Launch(async token =>
    {
        await CheckActorAsync(capture);
        await resources.CheckAsync(capture.CollectionId, capture.Sources, token);
        if (capture.PoseUnavailableReason != null)
        {
            await framework.RunOnFrameworkThread(() => accept(capture));
            Status = "Animation capture refreshed. " + capture.PoseUnavailableReason;
            return;
        }
        var snapshot = await framework.RunOnFrameworkThread(() => { token.ThrowIfCancellationRequested(); return poses.ReadMatching(capture.Pose); });
        await framework.RunOnFrameworkThread(() =>
        {
            token.ThrowIfCancellationRequested();
            CheckIdentityOnFramework(capture, true);
            accept(capture with { Pose = snapshot, CapturedUtc = snapshot.CapturedUtc });
        });
        Status = "Captured offsets refreshed for the selected timeline.";
    });
    public void ClearOffsets(AnimationCapture capture) => Launch(async token =>
    {
        if (capture.PoseUnavailableReason != null) throw new InvalidOperationException(capture.PoseUnavailableReason);
        await CheckActorAsync(capture, false);
        var backup = await framework.RunOnFrameworkThread(() =>
        {
            token.ThrowIfCancellationRequested();
            CheckIdentityOnFramework(capture, false);
            var before = poses.ReadMatching(capture.Pose);
            poses.ValidateRoundTrip(before);
            return new LivePoseOffsetBackup(capture.ActorId, capture.CollectionId, before,
                AnimationPoseRules.ClearAll(before), DateTime.UtcNow);
        });
        // Persist before touching LivePose so Reapply remains available after an interruption or restart.
        var previousBackup = offsetBackup;
        journals.SaveOffsetBackup(backup);
        var cleared = await framework.RunOnFrameworkThread(() =>
        {
            token.ThrowIfCancellationRequested();
            CheckIdentityOnFramework(capture, false);
            return poses.CompareAndSet(backup.Before, backup.Cleared);
        });
        if (!cleared)
        {
            RestoreOffsetBackup(previousBackup);
            throw new IOException("LivePose changed before offsets could be cleared. Nothing was changed.");
        }
        offsetBackup = backup;
        Status = "All current LivePose offsets were cleared. Reapply offsets is available.";
    });
    public void ReapplyOffsets(AnimationCapture capture) => Launch(async token =>
    {
        var backup = offsetBackup ?? throw new InvalidOperationException("There are no cleared LivePose offsets to reapply.");
        if (backup.ActorId != capture.ActorId || backup.CollectionId != capture.CollectionId)
            throw new InvalidOperationException("The saved offsets belong to a different player or collection.");
        await CheckActorAsync(capture, false);
        var outcome = await framework.RunOnFrameworkThread(() =>
        {
            token.ThrowIfCancellationRequested();
            CheckIdentityOnFramework(capture, false);
            var current = poses.ReadMatching(backup.Cleared);
            if (AnimationPoseRules.Same(current, backup.Before)) return 1;
            return poses.CompareAndSet(backup.Cleared, backup.Before) ? 2 : 0;
        });
        if (outcome == 0)
            throw new IOException("LivePose changed after the clear. Saved offsets were not reapplied.");
        journals.ClearOffsetBackup();
        offsetBackup = null;
        Status = outcome == 1 ? "LivePose offsets were already applied." : "Saved LivePose offsets were reapplied.";
    });
    private void RestoreOffsetBackup(LivePoseOffsetBackup? backup)
    {
        if (backup == null) journals.ClearOffsetBackup(); else journals.SaveOffsetBackup(backup);
        offsetBackup = backup;
    }
    private void Launch(Func<CancellationToken, Task> action, Guid? jobId = null, AnimationClip? clip = null)
    {
        if (Busy || disposed) return;
        if (clip != null) Observer.ReportOperationError(clip, null);
        cancellation?.Dispose(); cancellation = new CancellationTokenSource();
        var token = cancellation.Token; CanCancel = true; LastFailed = false;
        work = Task.Run(async () =>
        {
            try
            {
                await action(token);
                if (jobId is { } id) LastResult = new AnimationEditResult(id, true, Status, recovery.FirstOrDefault(r => r.Id == id)?.ModDirectory);
            }
            catch (OperationCanceledException)
            {
                Status = "Cancelled before commit. Live offsets are unchanged.";
                if (jobId is { } id) LastResult = new AnimationEditResult(id, false, Status);
            }
            catch (Exception e)
            {
                LastFailed = true;
                Status = e.InnerException?.Message ?? e.Message; log.Warning(e, "Animation edit operation failed.");
                if (clip != null) await framework.RunOnFrameworkThread(() => Observer.ReportOperationError(clip, Status));
                if (jobId is { } id) LastResult = new AnimationEditResult(id, false, Status);
            }
            finally { CanCancel = false; }
        });
    }

    public void Edit(AnimationBakeRequest request) => Launch(async token =>
    {
        native.EnsureAvailable();
        if (request.Destination == AnimationDestination.NewMod && !PenumbraService.IsSafeNewModName(request.ModName))
            throw new InvalidDataException("Choose a valid mod name before baking.");
        if (request.Operation == AnimationOperation.BakeOffsets)
        {
            if (request.Capture.PoseUnavailableReason != null) throw new InvalidOperationException(request.Capture.PoseUnavailableReason);
            if (request.SelectedBones.Count == 0 || request.Components == PoseComponents.None)
                throw new InvalidDataException("Select at least one bone and transform component.");
            if (request.SelectedBones.Any(b => b.Partial != request.Capture.Clip.Partial || b.Slot != 0 || !request.Capture.Pose.Bones.Any(p => p.Id == b)))
                throw new InvalidDataException("A selected offset belongs to a different skeleton.");
            if (!request.Capture.Pose.Bones.Where(b => request.SelectedBones.Contains(b.Id)).SelectMany(b => b.Stacks)
                    .Any(s => !AnimationPoseRules.Empty(AnimationPoseRules.Filter(s, request.Components))))
                throw new InvalidDataException("The selected components contain no applied adjustments.");
        }
        if (request.Operation == AnimationOperation.ExcludeBones && request.ExcludedBones.IsEmpty)
            throw new InvalidDataException("Untick at least one animated bone first.");
        if (AnimationBones.Problem(request.Capture.Clip, request.ExcludedBones) is { } exclusion)
            throw new InvalidDataException(exclusion);
        if (request.Operation == AnimationOperation.BakeOffsets &&
            AnimationBones.OffsetConflict(request.ExcludedBones, request.SelectedBones.Select(b => b.Name)) is { } conflict)
            throw new InvalidDataException(conflict);
        if (request.Capture.UnavailableReason != null) throw new InvalidOperationException(request.Capture.UnavailableReason);
        if (request.IncludeStartup && request.Capture.Startup == null) throw new InvalidOperationException("No unique startup was identified.");
        // Rebakes use only the selected source skeleton and, on repair, a standard
        // skeleton; none of them touches the current rig.
        await CheckActorAsync(request.Capture, false);
        if (request.Operation == AnimationOperation.BakeOffsets) await framework.RunOnFrameworkThread(() => poses.ValidateRoundTrip(request.Capture.Pose));
        await resources.CheckAsync(request.Capture.CollectionId, request.Capture.Sources, token);
        Status = "Capturing effective animation sources and dependencies";
        var clips = request.IncludeStartup ? new[] { request.Capture.Clip, request.Capture.Startup! } : [request.Capture.Clip];
        foreach (var clip in clips)
            if (clip.Resolution is { } resolution &&
                (resolution.Selected == null || resolution.State != SkeletonResolutionState.Matched))
                throw new InvalidOperationException(resolution.Reason ?? "Choose a compatible processing skeleton first.");
        if (request.Operation == AnimationOperation.RepairSkeleton &&
            clips.FirstOrDefault(clip => request.RepairTarget is not { } target || AnimationBones.Standard(clip, target) == null) is { } unrepairable)
            throw new InvalidOperationException(request.RepairTarget is { } missing
                ? $"No {AnimationBones.StandardName(missing)} skeleton was found for {unrepairable.GamePath}."
                : "Choose the skeleton to repair onto first.");
        await resources.CheckSkeletonsAsync(request.Capture, clips, token);
        var packagedPaths = clips.Select(clip => clip.GamePath).Distinct(StringComparer.Ordinal).ToArray();
        AnimationDependencyManifest manifest;
        if (request.Destination == AnimationDestination.NewMod)
        {
            if (request.Capture.PackagingError != null) throw new InvalidDataException(request.Capture.PackagingError);
            manifest = await resources.ManifestAsync(request.Capture, catalog,
                clips.Select(clip => clip.GamePath), packagedPaths, token);
        }
        else
        {
            var values = new List<(AnimationResource Resource, byte[] Bytes)>();
            foreach (var path in request.Capture.Sources.Select(s => s.GamePath).Distinct())
                values.Add(await resources.ReadAsync(request.Capture.CollectionId, path, token));
            manifest = new AnimationDependencyManifest(values.Select(v => v.Resource).ToImmutableArray(),
                values.ToImmutableDictionary(v => v.Resource.GamePath, v => v.Bytes));
        }
        var outputs = ImmutableDictionary.CreateBuilder<string, byte[]>();
        var dir = journals.DirectoryFor(request.Id); Directory.CreateDirectory(dir);
        foreach (var clip in clips.Distinct())
        {
            await CheckActorAsync(request.Capture, false);
            var source = outputs.TryGetValue(clip.GamePath, out var prior) ? prior : manifest.Files[clip.GamePath];
            var skeletonBytes = clip.Resolution?.Selected is { } selected
                ? (await resources.ReadSkeletonAsync(request.Capture.CollectionId, selected.Source, token)).Bytes
                : manifest.Files[clip.SkeletonPath];
            outputs[clip.GamePath] = await baker.BakeAsync(source, skeletonBytes, clip, request, dir, message => Status = message, token,
                () =>
                {
                    CheckIdentityOnFramework(request.Capture, true);
                    if (request.Operation == AnimationOperation.BakeOffsets) poses.CheckModule(request.Capture.Pose);
                });
        }
        var journal = await commits.PrepareAsync(request, manifest, outputs.ToImmutable(), token);
        recovery = recovery.Insert(0, journal);
        await commits.CommitAsync(journal, manifest, async () =>
        {
            await CheckActorAsync(request.Capture, false);
            await resources.CheckSkeletonsAsync(request.Capture, clips, token);
            await framework.RunOnFrameworkThread(() => CheckIdentityOnFramework(request.Capture, true));
        }, message =>
        {
            Status = message;
            if (message.StartsWith("Committing", StringComparison.Ordinal)) CanCancel = false;
        }, token);
        // An in-place commit just changed the hash of its own captured sources -
        // and, for a rebake that had to land at a fresh path, their resolved
        // location too. The game may not reload the file, so the observer will
        // never revisit this clip to notice on its own; update it now so a retry
        // (or Refresh) does not immediately fail CheckAsync against this same edit.
        if (request.Destination == AnimationDestination.InPlace)
        {
            var updatedSources = request.Capture.Sources.Select(s =>
                journal.Files.FirstOrDefault(f => f.GamePath == s.GamePath) is { } file
                    ? s with
                    {
                        Hash = file.AfterHash, ResolvedPath = file.Target,
                        ModDirectory = file.ModDirectory, ModRoot = file.ModRoot, RelativePath = file.RelativePath,
                    }
                    : s).ToImmutableArray();
            Observer.UpdateSources(request.Capture.Id, updatedSources);
        }
        if (request.Operation != AnimationOperation.BakeOffsets)
        {
            journal.State = "Completed"; journal.PoseClearOutcome = "NotApplicable";
            journal.Message = request.Operation == AnimationOperation.ExcludeBones
                ? "Animation activated without the unticked bones. Live offsets were retained."
                : "Repaired animation activated. Live offsets were retained.";
            journals.Save(journal); Status = journal.Message; return;
        }
        // Record clearing intent first. A restart can compare live state against both sides of this exact scope.
        journal.PoseAfter = AnimationPoseRules.RemoveBaked(request.Capture.Pose, request);
        journal.State = "ClearingOffsets"; journal.PoseClearOutcome = "Pending"; journals.Save(journal);
        var bakedBackup = new LivePoseOffsetBackup(request.Capture.ActorId, request.Capture.CollectionId,
            journal.PoseBefore!, journal.PoseAfter, DateTime.UtcNow);
        var previousBackup = offsetBackup;
        journals.SaveOffsetBackup(bakedBackup);
        try
        {
            await CheckActorAsync(request.Capture, false);
            journal.OffsetsCleared = await framework.RunOnFrameworkThread(() =>
            {
                if (disposed) throw new OperationCanceledException();
                CheckIdentityOnFramework(request.Capture, true);
                return poses.CompareAndSet(journal.PoseBefore!, journal.PoseAfter);
            });
            journal.Message = journal.OffsetsCleared ? "Animation activated; baked live offsets cleared." :
                "Animation activated. Live offsets changed while baking, so automatic clearing was skipped.";
            journal.PoseClearOutcome = journal.OffsetsCleared ? "Cleared" : "Skipped";
            if (journal.OffsetsCleared) offsetBackup = bakedBackup;
            else RestoreOffsetBackup(previousBackup);
        }
        catch (Exception e) { RestoreOffsetBackup(previousBackup); journal.Message = "Animation activated; live offsets were retained: " + e.Message; }
        journal.State = "Completed"; journals.Save(journal); Status = journal.Message;
    }, request.Id, request.Capture.Clip);

    /// <summary>
    /// Samples the clip's animation file on the skeleton it was made for and hands the take to
    /// <paramref name="deliver"/>, whose message becomes the status. Nothing in game changes.
    /// </summary>
    public void SendToBlender(AnimationCapture capture, bool startup, Func<AnimationTake, CancellationToken, Task<string>> deliver) => Launch(async token =>
    {
        var clip = startup ? capture.Startup ?? throw new InvalidOperationException("No linked startup animation was identified.") : capture.Clip;
        if (clip.Resolution is not { State: SkeletonResolutionState.Matched, Selected: { } source })
            throw new InvalidOperationException(clip.Resolution?.Reason ?? "Choose the skeleton this animation was made for first.");
        Status = "Reading the animation file";
        var (resource, pap) = await resources.ReadAsync(capture.CollectionId, clip.GamePath, token);
        var (_, skeleton) = await resources.ReadSkeletonAsync(capture.CollectionId, source.Source, token);
        var name = AnimationPresentation.ExportName(capture, startup);
        var facts = new Dictionary<string, string>
        {
            ["gamePath"] = clip.GamePath,
            ["clip"] = clip.Name,
            ["skeleton"] = source.Source.Resource.GamePath,
            ["mod"] = resource.ModName ?? resource.ModDirectory ?? "",
        };
        var take = await AnimationClipExport.SampleAsync(framework, pap, skeleton, clip, source, name, facts, message => Status = message, token);
        Status = "Sending the animation to Blender";
        Status = await deliver(take, token);
    });

    /// <summary>Samples a clip of a PAP file on the chosen skeleton and hands the take to <paramref name="deliver"/>.</summary>
    public void SendFileToBlender(AnimationFileClip clip, SkeletonCandidate skeleton, Func<AnimationTake, CancellationToken, Task<string>> deliver) => Launch(async token =>
    {
        Status = $"Reading {clip.Source.DisplayName}";
        var take = await Files.SampleAsync(clip, skeleton, message => Status = message, token);
        Status = "Sending the animation to Blender";
        Status = await deliver(take, token);
    });

    public void RestoreOffsets(AnimationEditJournal journal) => Launch(async _ =>
    {
        await CheckActorAsync(journal.Request.Capture, false);
        await RestoreOffsetsAsync(journal);
        Status = journal.Message;
    });
    public void Undo(AnimationEditJournal journal) => Launch(async _ =>
    {
        await CheckActorAsync(journal.Request.Capture, false);
        CanCancel = false;
        await commits.UndoFilesAsync(journal);
        await RestoreOffsetsAsync(journal);
        journal.State = "Undone"; journals.Save(journal); Status = journal.Message;
    });
    private async Task RestoreOffsetsAsync(AnimationEditJournal journal)
    {
        if (journal.PoseBefore == null || journal.PoseAfter == null || !journal.OffsetsCleared && journal.PoseClearOutcome is not "Pending")
        { journal.Message = "Files recovered; live offsets were not cleared."; journals.Save(journal); return; }
        var restored = await framework.RunOnFrameworkThread(() =>
        {
            CheckIdentityOnFramework(journal.Request.Capture, false);
            var current = poses.ReadMatching(journal.PoseBefore);
            if (AnimationPoseRules.Same(current, journal.PoseBefore)) return true;
            return poses.CompareAndSet(journal.PoseAfter, journal.PoseBefore);
        });
        if (!restored) throw new IOException("Live offsets changed after this edit. They were retained; automatic restoration was skipped.");
        journal.OffsetsCleared = false; journal.PoseClearOutcome = "Restored"; journal.Message = "Captured live offsets restored."; journals.Save(journal);
    }
    private async Task CheckActorAsync(AnimationCapture capture, bool skeleton = true)
    {
        if (disposed) throw new OperationCanceledException();
        var player = await framework.RunOnFrameworkThread(() => AnimationRuntime.Capture(objects));
        if (player == null || player.ActorId != capture.ActorId || skeleton && player.Address != capture.ActorAddress)
            throw new InvalidOperationException("The captured player is unavailable or changed. Refresh the animation capture.");
        var collection = await penumbra.GetCollectionTargetAsync(player.ObjectIndex);
        if (collection?.Id != capture.CollectionId) throw new InvalidOperationException("The player's collection changed. Refresh the animation capture.");
        if (skeleton && !player.Bindings.Any(b => b.Partial == capture.Clip.Partial && b.SkeletonFingerprint == capture.Clip.SkeletonFingerprint))
            throw new InvalidOperationException("The player's partial skeleton changed. Refresh the animation capture.");
    }
    private void CheckIdentityOnFramework(AnimationCapture capture, bool address)
    {
        if (disposed) throw new OperationCanceledException();
        var identity = AnimationRuntime.Identity(objects);
        if (identity == null || identity.Value.ActorId != capture.ActorId || address && identity.Value.Address != capture.ActorAddress)
            throw new InvalidOperationException("The captured player changed. Live offsets were retained.");
        penumbra.CheckAnimationCollectionOnFramework(identity.Value.Index, capture.CollectionId);
    }
    public void Dispose()
    {
        disposed = true; cancellation?.Cancel(); lifetime.Cancel(); Observer.CharacterChanged -= Cancel; Observer.Dispose();
        baker.Dispose();
    }
}
