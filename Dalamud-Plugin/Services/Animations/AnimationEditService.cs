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
    private readonly IPluginLog log;
    private CancellationTokenSource? cancellation;
    private Task work = Task.CompletedTask;
    private bool disposed;
    private ImmutableArray<AnimationEditJournal> recovery;
    private LivePoseOffsetBackup? offsetBackup;
    public AnimationObserver Observer { get; }
    public bool Busy => !work.IsCompleted;
    public bool CanCancel { get; private set; }
    public string Status { get; private set; } = "Select an animation and the offsets to bake.";
    public ImmutableArray<AnimationEditJournal> Recovery => recovery;
    public AnimationEditResult? LastResult { get; private set; }
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
        journals = new AnimationJournalStore(pi.ConfigDirectory.FullName);
        recovery = journals.Load().ToImmutableArray();
        offsetBackup = journals.LoadOffsetBackup();
        commits = new AnimationCommitService(penumbra, resources, backups, journals);
        baker = new AnimationBakeService(native, framework);
        Observer = new AnimationObserver(framework, objects, penumbra, native, resources, poses, catalog,
            new AnimationSkeletonIndex(penumbra, resources, framework, log, () => TextureFiles.EnsureCacheRoot(configuration.TextureCacheDirectory)), log);
        Observer.CharacterChanged += Cancel;
    }
    public void Cancel() { if (CanCancel) cancellation?.Cancel(); }

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
        var token = cancellation.Token; CanCancel = true;
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
        if (request.Capture.UnavailableReason != null) throw new InvalidOperationException(request.Capture.UnavailableReason);
        if (request.IncludeStartup && request.Capture.Startup == null) throw new InvalidOperationException("No unique startup was identified.");
        await CheckActorAsync(request.Capture);
        if (request.Operation == AnimationOperation.BakeOffsets) await framework.RunOnFrameworkThread(() => poses.ValidateRoundTrip(request.Capture.Pose));
        await resources.CheckAsync(request.Capture.CollectionId, request.Capture.Sources, token);
        Status = "Capturing effective animation sources and dependencies…";
        var clips = request.IncludeStartup ? new[] { request.Capture.Clip, request.Capture.Startup! } : [request.Capture.Clip];
        foreach (var clip in clips)
            if (clip.Resolution is { } resolution &&
                (resolution.Selected == null || resolution.State != SkeletonResolutionState.Matched))
                throw new InvalidOperationException(resolution.Reason ?? "Choose a compatible processing skeleton first.");
        await resources.CheckSkeletonsAsync(request.Capture, clips, token);
        var packagedPaths = clips.Select(clip => clip.GamePath).Distinct(StringComparer.Ordinal).ToArray();
        AnimationDependencyManifest manifest;
        if (request.Destination == AnimationDestination.NewMod)
        {
            if (request.Capture.PackagingError != null) throw new InvalidDataException(request.Capture.PackagingError);
            manifest = await resources.ManifestAsync(request.Capture, catalog, packagedPaths, token);
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
            await CheckActorAsync(request.Capture);
            var source = outputs.TryGetValue(clip.GamePath, out var prior) ? prior : manifest.Files[clip.GamePath];
            var skeletonBytes = clip.Resolution?.Selected is { } selected
                ? (await resources.ReadSkeletonAsync(request.Capture.CollectionId, selected.Source, token)).Bytes
                : manifest.Files[clip.SkeletonPath];
            outputs[clip.GamePath] = await baker.BakeAsync(source, skeletonBytes, clip, request, dir, message => Status = message, token,
                () =>
                {
                    CheckIdentityOnFramework(request.Capture, true);
                    AnimationRuntime.CheckPartial(objects, request.Capture.Clip);
                    if (request.Operation == AnimationOperation.BakeOffsets) poses.CheckModule(request.Capture.Pose);
                });
        }
        var journal = await commits.PrepareAsync(request, manifest, outputs.ToImmutable(), token);
        recovery = recovery.Insert(0, journal);
        await commits.CommitAsync(journal, manifest, async () =>
        {
            await CheckActorAsync(request.Capture);
            await resources.CheckSkeletonsAsync(request.Capture, clips, token);
            await framework.RunOnFrameworkThread(() =>
            {
                CheckIdentityOnFramework(request.Capture, true);
                AnimationRuntime.CheckPartial(objects, request.Capture.Clip);
            });
        }, message =>
        {
            Status = message;
            if (message.StartsWith("Committing", StringComparison.Ordinal)) CanCancel = false;
        }, token);
        if (request.Operation == AnimationOperation.RepairSkeleton)
        {
            journal.State = "Completed"; journal.PoseClearOutcome = "NotApplicable";
            journal.Message = "Repaired animation activated. Live offsets were retained.";
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
            await CheckActorAsync(request.Capture);
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
        disposed = true; cancellation?.Cancel(); Observer.CharacterChanged -= Cancel; Observer.Dispose();
        baker.Dispose();
    }
}
