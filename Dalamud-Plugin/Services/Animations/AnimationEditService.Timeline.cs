using System.Collections.Immutable;
using InstantEdit.Models;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Services.Animations;

/// <summary>
/// One clip's timeline as the Timeline window loaded it: the animation file, which of its
/// entries the clip is, and that entry's timeline. <see cref="Document"/> is null when the
/// timeline can't be edited, and <see cref="ReadOnlyReason"/> says why.
/// </summary>
internal sealed record AnimationTimelineSource(AnimationCapture Capture, bool Startup, AnimationClip Clip,
    AnimationResource Resource, byte[] Pap, int EntryIndex, byte[] Tmb, TimelineDocument? Document, string? ReadOnlyReason)
{
    /// <summary>The motion's length in seconds, as the game plays it.</summary>
    public float Seconds => Clip.Duration;
}

/// <summary>An edited clip, built and checked: the new animation file and its motion's fingerprint.</summary>
internal sealed record AnimationTimelineBuild(byte[] Pap, string BindingFingerprint);

internal sealed partial class AnimationEditService
{
    /// <summary>
    /// Reads the clip's animation file as the player's collection resolves it, and takes its
    /// timeline apart for editing. Nothing in game changes.
    /// </summary>
    public async Task<AnimationTimelineSource> LoadTimelineAsync(AnimationCapture capture, bool startup, CancellationToken token)
    {
        var clip = startup ? capture.Startup ?? throw new InvalidOperationException("No linked startup animation was identified.") : capture.Clip;
        var (resource, bytes) = await resources.ReadAsync(capture.CollectionId, clip.GamePath, token);
        var pap = new AnimationPap(bytes);
        var entry = pap.EntryIndex(clip.Name, clip.BindingIndex);
        var tmb = pap.TimelineBytes(entry);
        try
        {
            return new AnimationTimelineSource(capture, startup, clip, resource, bytes, entry, tmb, TimelineConvert.Read(TmbFile.Parse(tmb)), null);
        }
        catch (InvalidDataException error)
        {
            return new AnimationTimelineSource(capture, startup, clip, resource, bytes, entry, tmb, null, error.Message);
        }
    }

    /// <summary>
    /// Builds the edited clip and hands it to <paramref name="show"/>, whose message becomes the
    /// status: the GPose preview. A changed length bakes the motion first. Nothing is saved.
    /// </summary>
    public void PreviewTimeline(AnimationTimelineSource source, TimelineDocument document,
        Func<AnimationTimelineBuild, CancellationToken, Task<string>> show) => Launch(async token =>
    {
        var request = TimelineRequest(source, document, AnimationDestination.NewMod, "");
        // A preview is never journaled, so a length change bakes in a folder of its own.
        var work = Path.Combine(Path.GetTempPath(), "XIV Instant Edit timeline preview", request.Id.ToString("N"));
        try
        {
            var built = await BuildTimelineAsync(request, source, source.Pap, document, work, token);
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
    /// imported sounds, by game path; they are packaged next to the animation.
    /// </summary>
    /// <returns>The job's id, which <see cref="LastResult"/> carries once it ends; null when another job is running.</returns>
    public Guid? EditTimeline(AnimationTimelineSource source, TimelineDocument document, AnimationDestination destination,
        string modName, IReadOnlyDictionary<string, byte[]> added)
    {
        if (Busy) return null;
        var request = TimelineRequest(source, document, destination, modName.Trim());
        Launch(async token =>
        {
            if (destination == AnimationDestination.NewMod && !PenumbraService.IsSafeNewModName(request.ModName))
                throw new InvalidDataException("Choose a valid mod name before saving.");
            if (source.Document == null) throw new InvalidOperationException(source.ReadOnlyReason ?? "This timeline can't be edited.");
            var capture = request.Capture;
            await CheckActorAsync(capture, false);
            // The file must still be the one the editor loaded, or the edit would drop newer work.
            await resources.CheckAsync(capture.CollectionId, [source.Resource], token);
            var manifest = await resources.ManifestAsync(capture, [source.Clip.GamePath], destination, token);
            if (manifest.Resources.Single().Hash != source.Resource.Hash)
                throw new IOException("The animation file changed while saving. Open the timeline again.");
            var built = await BuildTimelineAsync(request, source, manifest.Files[source.Clip.GamePath], document,
                journals.DirectoryFor(request.Id), token);
            var (files, reused) = await AddedFilesAsync(capture, destination, source.Resource, added, token);
            var outputs = ImmutableDictionary<string, byte[]>.Empty.Add(source.Clip.GamePath, built.Pap);
            var journal = await commits.PrepareAsync(request, manifest, outputs, token,
                files.Count > 0 ? (source.Clip.GamePath, files) : null);
            if (reused.Count > 0)
            {
                journal.ReusedFiles = reused;
                journals.Save(journal);
            }
            recovery = recovery.Insert(0, journal);
            await commits.CommitAsync(journal, manifest, async () =>
            {
                await CheckActorAsync(capture, false);
                if (document.LengthChanged) await resources.CheckSkeletonsAsync(capture, [source.Clip], token);
                await framework.RunOnFrameworkThread(() => CheckIdentityOnFramework(capture, false));
            }, message =>
            {
                Status = message;
                if (message.StartsWith("Committing", StringComparison.Ordinal)) CanCancel = false;
            }, token);
            if (destination == AnimationDestination.InPlace)
            {
                var updated = capture.Sources.Select(s =>
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
            journal.Message = "Timeline saved.";
            journals.Save(journal); Status = journal.Message;
        }, request.Id, source.Clip);
        return request.Id;
    }

    private static AnimationBakeRequest TimelineRequest(AnimationTimelineSource source, TimelineDocument document,
        AnimationDestination destination, string modName)
        => new(Guid.NewGuid(), source.Capture, destination, modName, false, ImmutableHashSet<PoseBoneId>.Empty, PoseComponents.None,
            AnimationOperation.EditTimeline,
            Timeline: new AnimationTimelineEdit(source.Startup, source.EntryIndex,
                document.LengthChanged ? document.Length / (float)TimelineDocument.Fps : null));

    /// <summary>
    /// Writes the edited timeline into the clip's file. A changed length first resamples the
    /// motion, which needs the skeleton the clip was made for. Everything else in the file must
    /// come out exactly as it went in.
    /// </summary>
    private async Task<AnimationTimelineBuild> BuildTimelineAsync(AnimationBakeRequest request, AnimationTimelineSource source,
        byte[] pap, TimelineDocument document, string workDirectory, CancellationToken token)
    {
        if (source.Document == null) throw new InvalidOperationException(source.ReadOnlyReason ?? "This timeline can't be edited.");
        var clip = source.Clip;
        var timeline = TimelineConvert.Apply(source.Tmb, document);
        var motion = pap;
        if (document.LengthChanged)
        {
            native.EnsureAvailable();
            if (clip.Resolution is not { State: SkeletonResolutionState.Matched, Selected: { } selected })
                throw new InvalidOperationException(clip.Resolution?.Reason ?? "Choose the skeleton this animation was made for before changing its length.");
            await resources.CheckSkeletonsAsync(request.Capture, [clip], token);
            var skeleton = (await resources.ReadSkeletonAsync(request.Capture.CollectionId, selected.Source, token)).Bytes;
            Directory.CreateDirectory(workDirectory);
            motion = await baker.BakeAsync(pap, skeleton, clip, request, workDirectory, message => Status = message, token,
                () => CheckIdentityOnFramework(request.Capture, true));
        }
        var result = new AnimationPap(motion).WithTimeline(source.EntryIndex, timeline);
        CheckTimelineOutput(pap, result, source.EntryIndex, timeline, !document.LengthChanged);
        var fingerprint = document.LengthChanged
            ? await framework.RunOnFrameworkThread(() => AnimationRuntime.InspectPap(result).GetValueOrDefault(clip.BindingIndex, ""))
            : clip.BindingFingerprint;
        return new AnimationTimelineBuild(result, fingerprint);
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
    /// </summary>
    private async Task<(IReadOnlyDictionary<string, byte[]> Files, List<string> Reused)> AddedFilesAsync(AnimationCapture capture,
        AnimationDestination destination, AnimationResource edited, IReadOnlyDictionary<string, byte[]> added, CancellationToken token)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var reused = new List<string>();
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
            }
            result[gamePath] = bytes;
        }
        return (result, reused);
    }
}
