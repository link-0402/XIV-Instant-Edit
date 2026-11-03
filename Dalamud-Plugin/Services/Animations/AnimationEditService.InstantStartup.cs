using System.Collections.Immutable;
using InstantEdit.Models;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Services.PenumbraMods;
using InstantEdit.Ui.Timeline;

namespace InstantEdit.Services.Animations;

internal sealed partial class AnimationEditService
{
    /// <summary>What an instant startup's motion is: a wind-up from the game's idle into the loop.</summary>
    internal const string InstantStartupAction = "wind-up";

    /// <summary>The shortest and longest wind-up, in frames.</summary>
    public const int MinWindUp = TimelineDocument.MinLength, MostWindUp = 300;

    /// <summary>The wind-up's length until another is typed, in frames.</summary>
    public const int DefaultWindUp = 20;

    /// <summary>
    /// Why <paramref name="capture"/>'s loop can't get an instant startup (see <see cref="MakeInstantStartup"/>);
    /// null when it can.
    /// </summary>
    public static string? InstantStartupBlock(AnimationCapture capture) => capture switch
    {
        { Startup: null } => "This animation has no startup.",
        { ExpressionTimeline: not null } => "An expression has no startup to replace.",
        { Startup.Resolution: not { State: SkeletonResolutionState.Matched } } =>
            capture.Startup.Resolution?.Reason ?? "The startup's skeleton is still being identified.",
        _ => capture.Sources.FirstOrDefault(s => s.GamePath == capture.Clip.GamePath) is { } loop && AnimationResources.CanReplace(loop)
            ? null : "The loop doesn't come from a mod it can be saved in.",
    };

    /// <summary>The game's idle for <paramref name="race"/> (e.g. "c0101") and that race's body skeleton.</summary>
    internal static (string Idle, string Skeleton) VanillaIdlePaths(string race) =>
        ($"chara/human/{race}/animation/a0001/bt_common/resident/idle.pap", $"chara/human/{race}/skeleton/base/b0001/skl_{race}b0001.sklb");

    /// <summary>
    /// The startup's timeline as a wind-up: <paramref name="still"/>'s motion over its frames, the
    /// events the startup starts with and those the editor can't delete, squeezed into that length. The sounds,
    /// effects and faces it plays later never get to play, so they go.
    /// </summary>
    internal static TimelineDocument InstantStartupDocument(TimelineDocument document, MotionReplacement still)
    {
        var kept = document with
        {
            Events = [.. document.Events.Where(e => e.Access == TimelineAccess.Locked || e.Start == 0)],
            Motion = [],
            Cuts = null,
            ExcludedBones = [],
        };
        return TimelineEdits.ReplaceMotion(kept, still, scaleEvents: true);
    }

    /// <summary>
    /// Gives <paramref name="capture"/>'s loop a wind-up startup: the startup's file with a move of <paramref name="frames"/>
    /// frames from the game's idle (for the loop's race) into the loop, on the loop's skeleton, then the loop's first frames for the game's
    /// measured fade (<see cref="MotionWindUp.Build"/>), so the fade shows the same pose on both sides. It is saved in the
    /// loop's mod, journaled for Undo: over the startup when that mod already provides it, else as a new file mapped
    /// wherever the mod maps the loop.
    /// </summary>
    /// <returns>The job's id, which <see cref="LastResult"/> carries once it ends; null when another job is running.</returns>
    public Guid? MakeInstantStartup(AnimationCapture capture, int frames)
    {
        if (Busy) return null;
        var id = Guid.NewGuid();
        Launch(async token =>
        {
            if (InstantStartupBlock(capture) is { } blocked) throw new InvalidOperationException(blocked);
            if (frames is < MinWindUp or > MostWindUp)
                throw new InvalidOperationException($"The wind-up must be {MinWindUp} to {MostWindUp} frames.");
            native.EnsureAvailable();
            await CheckActorAsync(capture, false);
            var loopPath = capture.Clip.GamePath;
            var (loop, _) = await resources.ReadAsync(capture.CollectionId, loopPath, token);
            if (!AnimationResources.CanReplace(loop)) throw new InvalidOperationException("The loop doesn't come from a mod it can be saved in.");
            Status = "Reading the startup";
            var source = await LoadTimelineAsync(capture, true, token);
            if (source.Document == null) throw new InvalidOperationException(source.ReadOnlyReason ?? "The startup's timeline can't be edited.");
            if (source.Clip.Resolution is not { State: SkeletonResolutionState.Matched, Selected: { } selected })
                throw new InvalidOperationException(source.Clip.Resolution?.Reason ?? "Choose the skeleton the startup was made for first.");
            var startupPath = source.Resource.GamePath;
            // The loop's mod provides the startup itself: it is written over, at a fresh path. Otherwise the mod gets one.
            var own = source.Resource.ModDirectory == loop.ModDirectory && source.Resource.ModRoot is { } startupRoot &&
                      PathRules.SamePhysicalPath(startupRoot, loop.ModRoot!);
            if (!own && ModMetadata.ModMapsGamePath(loop.ModRoot!, startupPath))
                throw new InvalidOperationException($"{loop.ModName ?? loop.ModDirectory} already has this startup in an option that is off.");

            // The wind-up is written on the loop's skeleton and for its race, so it fits the loop whatever the startup was made for.
            // The loop itself is only read: as it is, or on the standard a save repaired it onto, as the Timeline opens it.
            Status = "Reading the loop";
            var loopSource = await LoadTimelineAsync(capture, false, token);
            var repaired = SavedRepair(recovery, loopSource) is { } saved ? AnimationBones.Standard(loopSource.Clip, saved) : null;
            var rebuild = WindUpRebuild(source, loopSource, repaired);
            var rebuilt = source with { Rebuild = rebuild };
            var playing = await DecodeMotionAsync(rebuilt, message => Status = message, token);
            var idle = await DecodeVanillaIdleAsync(rebuild?.Model ?? StartupRace(source), capture, message => Status = message, token);
            var looping = await DecodeMotionAsync(
                repaired != null && repaired.Skeleton.Fingerprint != loopSource.Clip.Resolution?.Selected?.Skeleton.Fingerprint
                    ? loopSource with { Rebuild = new(null, repaired) } : loopSource,
                message => Status = message, token);
            Status = "Building the wind-up";
            var fade = Math.Clamp(Observer.LoopFades.Measured(capture.Startup!.Timeline, capture.Clip.Timeline) ?? LoopFades.Default, 0, LoopFades.Most);
            var windUp = MotionWindUp.Build(playing, MotionHold.Borrowed(playing, idle), looping, frames, fade);
            var replacement = await ReplaceMotionAsync(rebuilt, selected, windUp, InstantStartupAction, "", null, message => Status = message, token);
            var edited = source.WithReplacement(replacement);
            var document = InstantStartupDocument(source.Document, replacement);
            var request = TimelineRequest(edited, document, AnimationDestination.InPlace, "") with { Id = id };

            await resources.CheckAsync(capture.CollectionId, [source.Resource], token);
            var manifest = await resources.ManifestAsync(capture, [loopPath, startupPath], AnimationDestination.InPlace, token);
            if (manifest.Resources.First(r => r.GamePath == startupPath).Hash != source.Resource.Hash ||
                manifest.Resources.First(r => r.GamePath == loopPath).Hash != loop.Hash)
                throw new IOException("The animation changed while saving. Try again.");
            var built = await BuildTimelineAsync(request, edited, manifest.Files[startupPath], document, null, journals.DirectoryFor(request.Id), token);
            // Journaled as repaired onto the loop's standard, so the startup reopens on it too (SavedRepair). Only here: the build
            // would bake the motion, already on that standard, onto it again.
            if (repaired is { Standard: not SkeletonStandard.Vanilla })
                request = request with { RepairTarget = repaired.Standard };
            var bytes = ImmutableDictionary<string, byte[]>.Empty.Add(startupPath, built.Bytes);
            var journal = own
                ? await commits.PrepareAsync(request, manifest, bytes, token)
                : await commits.PrepareAsync(request, manifest, ImmutableDictionary<string, byte[]>.Empty, token, (loopPath, bytes, null, null, null));
            recovery = recovery.Insert(0, journal);
            await commits.CommitAsync(journal, manifest, async () =>
            {
                await CheckActorAsync(capture, false);
                await resources.CheckSkeletonsAsync(capture, [source.Clip], token);
                await framework.RunOnFrameworkThread(() => CheckIdentityOnFramework(capture, false));
            }, message =>
            {
                Status = message;
                if (message.StartsWith("Committing", StringComparison.Ordinal)) CanCancel = false;
            }, token);
            var file = journal.Files.First(f => f.GamePath == startupPath);
            var updated = capture.Sources.Select(s => s.GamePath == startupPath
                ? s with
                {
                    Hash = file.AfterHash, ResolvedPath = file.Target, ModDirectory = file.ModDirectory, ModRoot = file.ModRoot,
                    RelativePath = file.RelativePath, ModName = loop.ModName,
                }
                : s).ToImmutableArray();
            await framework.RunOnFrameworkThread(() => Observer.UpdateSources(capture.Id, updated));
            journal.State = "Completed";
            journal.Message = $"Wind-up saved in {loop.ModName ?? loop.ModDirectory}.";
            journals.Save(journal); Status = journal.Message;
        }, id, capture.Startup);
        return id;
    }

    /// <summary>
    /// Decodes the game's own idle for <paramref name="race"/> on that race's game skeleton, whatever mods replace either.
    /// Nothing in game changes.
    /// </summary>
    private Task<MotionClip> DecodeVanillaIdleAsync(string race, AnimationCapture capture, Action<string> status, CancellationToken token) =>
        DecodeGameFileAsync(VanillaIdlePaths(race).Idle, StandingIdleEntry, capture, status, token);

    /// <summary>The race <paramref name="startup"/> was made for: its PAP header's, else its skeleton's.</summary>
    private static string StartupRace(AnimationTimelineSource startup) =>
        startup.FileModel ?? AnimationSkeletonIndex.ModelFromPath(startup.Clip.SkeletonPath)
        ?? throw new InvalidOperationException("The startup isn't made for a playable race.");

    /// <summary>
    /// The rebuild that writes the wind-up on <paramref name="loop"/>'s skeleton and names its race (its PAP header's) in the
    /// startup's header, so the startup is a sibling of the loop: the game converts both the same way, and the loop's own bones
    /// (a skeleton mod's) are the ones the wind-up moves. A loop a save <paramref name="repaired"/> onto a standard reads back as
    /// a smaller skeleton, so the wind-up goes on that standard instead. Null when the startup is on that skeleton for that race
    /// already, or while the loop's skeleton isn't matched, which reading the loop then reports.
    /// </summary>
    internal static RaceRebuild? WindUpRebuild(AnimationTimelineSource startup, AnimationTimelineSource loop, StandardSkeleton? repaired = null)
    {
        if (loop.Clip.Resolution is not { State: SkeletonResolutionState.Matched, Selected: { } own })
            return null;
        var target = repaired ?? new StandardSkeleton(SkeletonStandard.Vanilla, own.Skeleton, "the loop's");
        if (startup.Clip.Resolution?.Selected?.Skeleton.Fingerprint == target.Skeleton.Fingerprint && loop.FileModel == startup.FileModel)
            return null;
        return new RaceRebuild(loop.FileModel, target);
    }
}
