using System.Collections.Immutable;
using InstantEdit.Models;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Services.PenumbraMods;
using InstantEdit.Ui.Timeline;

namespace InstantEdit.Services.Animations;

internal sealed partial class AnimationEditService
{
    /// <summary>What an instant startup's motion is: the game's idle, held.</summary>
    internal const string InstantStartupAction = "vanilla idle";

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
    /// The startup's timeline as an instant startup: <paramref name="still"/>'s motion over its few frames, the
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
    /// Gives <paramref name="capture"/>'s loop an instant startup: the startup's file with the game's idle (for the
    /// startup's race) held for <see cref="TimelineDocument.MinLength"/> frames as its motion, so the game fades from
    /// the idle straight into the loop. It is saved in the loop's mod, journaled for Undo: over the startup when that
    /// mod already provides it, else as a new file mapped wherever the mod maps the loop.
    /// </summary>
    /// <returns>The job's id, which <see cref="LastResult"/> carries once it ends; null when another job is running.</returns>
    public Guid? MakeInstantStartup(AnimationCapture capture)
    {
        if (Busy) return null;
        var id = Guid.NewGuid();
        Launch(async token =>
        {
            if (InstantStartupBlock(capture) is { } blocked) throw new InvalidOperationException(blocked);
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

            var playing = await DecodeMotionAsync(source, message => Status = message, token);
            var idle = await DecodeVanillaIdleAsync(source, message => Status = message, token);
            Status = "Holding the idle";
            var still = MotionHold.Still(playing, MotionHold.Borrowed(playing, idle), 0, TimelineDocument.MinLength);
            var replacement = await ReplaceMotionAsync(source, selected, still, InstantStartupAction, "", null, message => Status = message, token);
            var edited = source.WithReplacement(replacement);
            var document = InstantStartupDocument(source.Document, replacement);
            var request = TimelineRequest(edited, document, AnimationDestination.InPlace, "") with { Id = id };

            await resources.CheckAsync(capture.CollectionId, [source.Resource], token);
            var manifest = await resources.ManifestAsync(capture, [loopPath, startupPath], AnimationDestination.InPlace, token);
            if (manifest.Resources.First(r => r.GamePath == startupPath).Hash != source.Resource.Hash ||
                manifest.Resources.First(r => r.GamePath == loopPath).Hash != loop.Hash)
                throw new IOException("The animation changed while saving. Try again.");
            var built = await BuildTimelineAsync(request, edited, manifest.Files[startupPath], document, null, journals.DirectoryFor(request.Id), token);
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
            journal.Message = $"Instant startup saved in {loop.ModName ?? loop.ModDirectory}.";
            journals.Save(journal); Status = journal.Message;
        }, id, capture.Startup);
        return id;
    }

    /// <summary>
    /// Decodes the game's own idle for the race <paramref name="startup"/> was made for (its PAP header's, else its
    /// skeleton's) on that race's game skeleton, whatever mods replace either. Nothing in game changes.
    /// </summary>
    private Task<MotionClip> DecodeVanillaIdleAsync(AnimationTimelineSource startup, Action<string> status, CancellationToken token)
    {
        var race = startup.FileModel ?? AnimationSkeletonIndex.ModelFromPath(startup.Clip.SkeletonPath)
            ?? throw new InvalidOperationException("The startup isn't made for a playable race.");
        return DecodeGameFileAsync(VanillaIdlePaths(race).Idle, StandingIdleEntry, startup.Capture, status, token);
    }
}
