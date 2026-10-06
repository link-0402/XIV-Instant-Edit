using System.Collections.Immutable;
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
        Effects is { Count: > 0 } effects ? TimelineEffectCheck.Find(document, effects) : [];

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

    /// <summary>The PAP whose motion the layers apply to: <see cref="BaseMotion"/>, else the file's own.</summary>
    public byte[] MotionSource => BaseMotion ?? Pap;

    /// <summary>The clip as found in <see cref="MotionSource"/>, with that binding's fingerprint.</summary>
    public AnimationClip MotionClip => BaseMotion == null ? Clip : Clip with { BindingFingerprint = BaseFingerprint ?? string.Empty };
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
    /// Builds the edited clip and hands it to <paramref name="show"/>, whose message becomes the
    /// status: the GPose preview. A changed length bakes the motion first. Nothing is saved.
    /// <paramref name="motionFrames"/> are facial motions' own lengths (see <see cref="TimelineConvert.Apply"/>).
    /// </summary>
    public void PreviewTimeline(AnimationTimelineSource source, TimelineDocument document,
        Func<AnimationTimelineBuild, CancellationToken, Task<string>> show, IReadOnlyDictionary<string, int>? motionFrames = null) => Launch(async token =>
    {
        var request = TimelineRequest(source, document, AnimationDestination.NewMod, "");
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
    /// is mapped wherever that file is instead, when the animation's mod is what provides it.
    /// <paramref name="motionFrames"/> are facial motions' own lengths (see <see cref="TimelineConvert.Apply"/>).
    /// </summary>
    /// <returns>The job's id, which <see cref="LastResult"/> carries once it ends; null when another job is running.</returns>
    public Guid? EditTimeline(AnimationTimelineSource source, TimelineDocument document, AnimationDestination destination,
        string modName, IReadOnlyDictionary<string, byte[]> added, IReadOnlyDictionary<string, string>? beside = null,
        IReadOnlyDictionary<string, int>? motionFrames = null)
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
            // The file the timeline lives in: the clip's PAP, or an expression's own TMB.
            var edited = source.Resource.GamePath;
            var manifest = await resources.ManifestAsync(capture, [edited], destination, token);
            if (manifest.Resources.Single().Hash != source.Resource.Hash)
                throw new IOException("The animation file changed while saving. Open the timeline again.");
            var built = await BuildTimelineAsync(request, source, manifest.Files[edited], document, motionFrames,
                journals.DirectoryFor(request.Id), token);
            var (files, reused, owners) = await AddedFilesAsync(capture, destination, source.Resource, added, beside, token);
            var outputs = ImmutableDictionary<string, byte[]>.Empty.Add(built.GamePath, built.Bytes);
            var journal = await commits.PrepareAsync(request, manifest, outputs, token,
                files.Count > 0 ? (edited, files, owners) : null);
            if (reused.Count > 0)
            {
                journal.ReusedFiles = reused;
                journals.Save(journal);
            }
            recovery = recovery.Insert(0, journal);
            await commits.CommitAsync(journal, manifest, async () =>
            {
                await CheckActorAsync(capture, false);
                if (ChangesMotion(document)) await resources.CheckSkeletonsAsync(capture, [source.Clip], token);
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
            journal.Message = document.MotionChanged ? "Animation saved." : "Timeline saved.";
            journals.Save(journal); Status = journal.Message;
        }, request.Id, source.Clip);
        return request.Id;
    }

    private static AnimationBakeRequest TimelineRequest(AnimationTimelineSource source, TimelineDocument document,
        AnimationDestination destination, string modName)
        => new(Guid.NewGuid(), source.Capture, destination, modName, false, ImmutableHashSet<PoseBoneId>.Empty, PoseComponents.None,
            AnimationOperation.EditTimeline,
            Timeline: new AnimationTimelineEdit(source.Startup, source.EntryIndex, RetimeSeconds(document),
                document.MotionChanged ? document.Motion : []));

    /// <summary>
    /// How long the motion becomes, in seconds; null when the length stays. The document's length is
    /// the motion's own (see <see cref="TimelineConvert.Read"/>), not a mod's copied header length.
    /// </summary>
    internal static float? RetimeSeconds(TimelineDocument document) =>
        document.LengthChanged ? document.Length / (float)TimelineDocument.Fps : null;

    /// <summary>Whether saving <paramref name="document"/> bakes the motion: a new length, or bone layers that change it.</summary>
    internal static bool ChangesMotion(TimelineDocument document) => document.LengthChanged || document.MotionChanged;

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
        var baked = ChangesMotion(document);
        if (baked)
        {
            native.EnsureAvailable();
            if (clip.Resolution is not { State: SkeletonResolutionState.Matched, Selected: { } selected })
                throw new InvalidOperationException(clip.Resolution?.Reason ?? (document.LengthChanged
                    ? "Choose the skeleton this animation was made for before changing its length."
                    : "Choose the skeleton this animation was made for before changing its motion."));
            await resources.CheckSkeletonsAsync(request.Capture, [clip], token);
            var skeleton = (await resources.ReadSkeletonAsync(request.Capture.CollectionId, selected.Source, token)).Bytes;
            Directory.CreateDirectory(workDirectory);
            motion = await baker.BakeAsync(source.BaseMotion ?? pap, skeleton, source.BaseMotion == null ? clip : source.MotionClip, request,
                workDirectory, message => Status = message, token, () => CheckIdentityOnFramework(request.Capture, true));
        }
        var result = new AnimationPap(motion).WithTimeline(source.EntryIndex, timeline);
        CheckTimelineOutput(pap, result, source.EntryIndex, timeline, !baked && source.BaseMotion == null);
        var fingerprint = baked || source.BaseMotion != null
            ? await framework.RunOnFrameworkThread(() => AnimationRuntime.InspectPap(result).GetValueOrDefault(clip.BindingIndex, ""))
            : clip.BindingFingerprint;
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
    /// </summary>
    private async Task<(IReadOnlyDictionary<string, byte[]> Files, List<string> Reused, IReadOnlyDictionary<string, string> Owners)>
        AddedFilesAsync(AnimationCapture capture, AnimationDestination destination, AnimationResource edited,
            IReadOnlyDictionary<string, byte[]> added, IReadOnlyDictionary<string, string>? beside, CancellationToken token)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var reused = new List<string>();
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
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
                    edited.ModRoot is { } root && PenumbraService.ModMapsGamePath(root, original))
                {
                    try
                    {
                        var (provider, _) = await resources.ReadAsync(capture.CollectionId, original, token);
                        if (provider.ModDirectory == edited.ModDirectory && provider.ModRoot is { } providerRoot &&
                            PathRules.SamePhysicalPath(providerRoot, root))
                            owners[gamePath] = original;
                    }
                    // Unreadable, the copy is simply mapped with the animation.
                    catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { }
                }
            }
            result[gamePath] = bytes;
        }
        return (result, reused, owners);
    }
}
