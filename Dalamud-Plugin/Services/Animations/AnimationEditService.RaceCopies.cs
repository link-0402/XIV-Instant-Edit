using System.Collections.Immutable;
using InstantEdit.Models;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Services.PenumbraMods;
using InstantEdit.Studio.Models;

namespace InstantEdit.Services.Animations;

internal sealed partial class AnimationEditService
{
    /// <summary>What an All races save reports once it ends.</summary>
    internal const string SavedForEveryRace = "Saved for every race.";

    /// <summary>Why <paramref name="edited"/> can't be saved for every race, or null when it can (see <see cref="RaceCopies.Unavailable"/>).</summary>
    internal string? RaceCopiesUnavailable(AnimationResource edited, bool face, bool holdsOthers, bool newMotion) =>
        RaceCopies.Unavailable(edited.GamePath, face, holdsOthers, newMotion, catalog.PapTable, RaceInfo.PlayableCodes);

    /// <summary>
    /// Whether an earlier All races save left copies of <paramref name="edited"/>: the mod maps a file of the copies' folder at
    /// another path the races load, or the file itself is one. Saving such a clip goes on saving for every race, so the copies don't go stale.
    /// </summary>
    internal Task<bool> HasRaceCopiesAsync(AnimationResource edited) => Task.Run(() =>
    {
        if (RaceCopies.IsCopy(edited.RelativePath))
            return true;
        if (edited.ModRoot is not { } root || catalog.PapTable is not { } table || !RaceCopies.InCommonFolder(edited.GamePath))
            return false;
        try
        {
            return table.Files(edited.GamePath, RaceInfo.PlayableCodes)
                .Any(file => file.Path != edited.GamePath && ModMetadata.AnimationMappedFiles(root, file.Path).Any(RaceCopies.IsCopy));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            log.Debug(e, "Could not look for earlier race copies.");
            return false;
        }
    });

    /// <summary>
    /// What an All races save of <paramref name="source"/> does with each file the races load (see <see cref="RaceCopies"/>),
    /// as the collection gives them now. Reads every file, so it runs off the framework thread.
    /// </summary>
    internal Task<RaceCopyPlan> RaceCopyPlanAsync(AnimationTimelineSource source, IReadOnlyCollection<string> excluded, IEnumerable<Motion.MotionLayer> layers,
        SkeletonStandard? repair, CancellationToken token) =>
        RaceCopyPlanAsync(source.Capture.CollectionId, source.Resource, source.FileModel,
            RaceCopies.MovedBones(AnimationBones.Kept(source.Clip, excluded), layers, excluded), repair, token);

    private async Task<RaceCopyPlan> RaceCopyPlanAsync(Guid collection, AnimationResource edited, string? fileModel, IReadOnlyCollection<string> kept,
        SkeletonStandard? repair, CancellationToken token)
    {
        var table = catalog.PapTable ?? throw new InvalidDataException("The game's load table couldn't be read.");
        var files = table.Files(edited.GamePath, RaceInfo.PlayableCodes);
        var sources = new Dictionary<string, RaceFileSource>(StringComparer.Ordinal);
        var standards = new Dictionary<ushort, ImmutableArray<StandardSkeleton>>();
        foreach (var (path, _) in files)
        {
            if (path == edited.GamePath)
                continue;
            AnimationResource? given = null;
            try { given = (await resources.ReadAsync(collection, path, token)).Resource; }
            catch (FileNotFoundException) { }
            sources[path] = new(given, edited.ModRoot is { } root && ModMetadata.ModMapsGamePath(root, path));
        }
        foreach (var race in files.Select(file => file.Path).Append(edited.GamePath).Select(RaceCopies.RaceOf).Distinct())
            standards[race] = await skeletons.StandardsForAsync(collection, race, token);
        return RaceCopies.Plan(files, edited, fileModel, path => sources[path], race => standards[race], kept, repair);
    }

    /// <summary>What an All races save writes besides the edited file: the copies, by the route each takes, and the attach overrides they add.</summary>
    private sealed record RaceCopyBuilds(ImmutableDictionary<string, byte[]> Outputs, ImmutableDictionary<string, byte[]> Added, ImmutableArray<AtchManipulation> Manipulations);

    /// <summary>
    /// Builds <paramref name="plan"/>'s copies one after another, each for its race on its own skeleton, from the edited file: in place, a copy of a path the
    /// mod maps goes through the rename route and any other is added beside the edited file; in a new mod every copy is added, and the mod's own other files come along.
    /// One that fails stops the save before anything is written.
    /// </summary>
    private async Task<RaceCopyBuilds> BuildRaceCopiesAsync(AnimationBakeRequest request, AnimationTimelineSource source, TimelineDocument document,
        IReadOnlyDictionary<string, int>? motionFrames, RaceCopyPlan plan, AnimationDependencyManifest manifest, byte[] edited, CancellationToken token)
    {
        var outputs = ImmutableDictionary<string, byte[]>.Empty;
        var added = ImmutableDictionary<string, byte[]>.Empty;
        var manipulations = ImmutableArray.CreateBuilder<AtchManipulation>();
        foreach (var file in plan.Files)
        {
            if (file.Action == RaceFileAction.Kept && file.InEditedMod && request.Destination == AnimationDestination.NewMod)
                outputs = outputs.SetItem(file.GamePath, manifest.Files[file.GamePath]);
            if (file.Action != RaceFileAction.Rebuild)
                continue;
            token.ThrowIfCancellationRequested();
            var model = $"c{file.Race:D4}";
            Status = "Rebuilding for " + AnimationBones.RaceName(model);
            var copy = RaceCopies.CopyRequest(request, file);
            var work = Path.Combine(journals.DirectoryFor(request.Id), "race-" + model);
            try
            {
                // The copy's own path gives its attach overrides the races that play it; its own layers are the ones its skeleton holds.
                var built = await BuildTimelineAsync(copy, source with { Resource = source.Resource with { GamePath = file.GamePath } }, edited,
                    document with { Motion = copy.Timeline!.Motion }, motionFrames, work, token, placeProps: true, status: _ => { });
                if (request.Destination == AnimationDestination.InPlace && file.InEditedMod)
                    outputs = outputs.SetItem(file.GamePath, built.Bytes);
                else
                    added = added.SetItem(file.GamePath, built.Bytes);
                manipulations.AddRange(built.Manipulations);
            }
            catch (Exception e) when (e is InvalidDataException or InvalidOperationException or IOException or ArgumentException)
            {
                throw new InvalidDataException(AnimationBones.RebuildFailedNote(model, e.Message), e);
            }
            finally
            {
                try { if (Directory.Exists(work)) Directory.Delete(work, true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.Debug(e, "Could not remove a race copy's work folder."); }
            }
        }
        return new(outputs, added, [.. manipulations.Distinct()]);
    }

    /// <summary>
    /// The other half of a loop and its startup for an All races save, when the edited mod provides it from a file of its own: a plan over its own path,
    /// since its borrowing can differ, and each copy a plain rebuild with no layers. Nothing for a clip with no other half, one in the same file, one outside
    /// <c>bt_common</c>, or one whose file holds other animations, which a new header race would warp: that half stays as it is.
    /// </summary>
    private async Task<(RaceCopyPlan? Plan, RaceCopyBuilds? Builds)> OtherHalfCopiesAsync(AnimationBakeRequest request, AnimationTimelineSource source, CancellationToken token)
    {
        var capture = request.Capture;
        if ((source.Startup ? capture.Clip : capture.Startup) is not { } half || half.GamePath == source.Resource.GamePath || source.Resource.ModDirectory == null ||
            !RaceCopies.InCommonFolder(half.GamePath))
            return (null, null);
        var (resource, bytes) = await resources.ReadAsync(capture.CollectionId, half.GamePath, token);
        if (resource.ModDirectory != source.Resource.ModDirectory || HoldsOthers(new AnimationPap(bytes), half.BindingIndex))
            return (null, null);
        // The file as it is now: a save since the capture changed its binding, which the bake compares with the clip's.
        var fingerprint = await framework.RunOnFrameworkThread(() => AnimationRuntime.InspectPap(bytes).GetValueOrDefault(half.BindingIndex, string.Empty));
        var clip = (half.Resolution is { State: SkeletonResolutionState.Matched } ? half
            : half with { Resolution = await skeletons.ResolveAsync(capture.CollectionId, half, bytes, capture.LoadedResourcePaths, null, null, token) }) with { BindingFingerprint = fingerprint };
        if (clip.Resolution is not { State: SkeletonResolutionState.Matched, Selected: { } selected })
            throw new InvalidDataException("The other half of this animation has no matching skeleton.");
        var pap = new AnimationPap(bytes);
        var fileModel = pap.ModelType == 0 ? AnimationSkeletonIndex.ModelCode(pap.ModelId) : null;
        var plan = await RaceCopyPlanAsync(capture.CollectionId, resource, fileModel, AnimationBones.Kept(clip, request.ExcludedBones), request.RepairModel == null ? request.RepairTarget : null, token);
        var outputs = ImmutableDictionary<string, byte[]>.Empty;
        var added = ImmutableDictionary<string, byte[]>.Empty;
        var skeleton = (await resources.ReadSkeletonAsync(capture.CollectionId, selected.Source, token)).Bytes;
        var plain = request with
        {
            ExcludedBones = [],
            Timeline = request.Timeline! with { DurationSeconds = null, Motion = [], Cuts = null, CutSounds = null, Links = null },
        };
        if (request.Destination == AnimationDestination.NewMod)
            foreach (var file in plan.Files.Where(file => file.Action == RaceFileAction.Kept && file.InEditedMod))
                outputs = outputs.SetItem(file.GamePath, (await resources.ReadAsync(capture.CollectionId, file.GamePath, token)).Bytes);
        foreach (var file in plan.Files.Where(file => file.Action == RaceFileAction.Rebuild))
        {
            token.ThrowIfCancellationRequested();
            var model = $"c{file.Race:D4}";
            Status = "Rebuilding for " + AnimationBones.RaceName(model);
            var work = Path.Combine(journals.DirectoryFor(request.Id), "race-half-" + model);
            byte[] built = bytes;
            try
            {
                Directory.CreateDirectory(work);
                if (file.Target != null)
                    built = await baker.BakeAsync(bytes, skeleton, clip, RaceCopies.CopyRequest(plain, file), work, _ => { }, token,
                        () => CheckIdentityOnFramework(capture, true));
            }
            catch (Exception e) when (e is InvalidDataException or InvalidOperationException or IOException or ArgumentException)
            {
                throw new InvalidDataException(AnimationBones.RebuildFailedNote(model, e.Message), e);
            }
            finally
            {
                try { if (Directory.Exists(work)) Directory.Delete(work, true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.Debug(e, "Could not remove a race copy's work folder."); }
            }
            if (request.Destination == AnimationDestination.InPlace && file.InEditedMod)
                outputs = outputs.SetItem(file.GamePath, built);
            else
                added = added.SetItem(file.GamePath, built);
        }
        return (plan, new(outputs, added, []));
    }
}
