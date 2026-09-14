using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Diagnostics;
using Dalamud.Plugin.Services;
using InstantEdit.Models;

namespace InstantEdit.Services.Animations;

internal sealed class AnimationObserver : IDisposable
{
    private readonly IFramework framework;
    private readonly IObjectTable objects;
    private readonly PenumbraService penumbra;
    private readonly AnimationNative native;
    private readonly AnimationResources resources;
    private readonly LivePoseAdapter poses;
    private readonly AnimationCatalog catalog;
    private readonly AnimationSkeletonIndex skeletons;
    private readonly IPluginLog log;
    private readonly ConcurrentDictionary<string, (DateTime At, SkeletonResolution Result)> resolutions = [];
    private readonly ConcurrentDictionary<string, string> manualChoices = [];
    private readonly ConcurrentDictionary<string, string> operationErrors = [];
    private Task matching = Task.CompletedTask;
    private readonly CancellationTokenSource lifetime = new();
    private readonly RuntimeCaptureCache runtimeCache = new();
    private readonly AnimationResourceCache resourceCache = new();
    private readonly ConcurrentDictionary<string, AnimationPap> parsedPaps = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ImmutableHashSet<string>> motionCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ImmutableDictionary<int, float>> durations = new(StringComparer.Ordinal);
    private CancellationTokenSource? listening;
    private CancellationTokenSource? observation;
    private volatile bool listeningEnabled;
    private readonly ConcurrentDictionary<string, ImmutableDictionary<int, string>> prints = new(StringComparer.Ordinal);
    private RuntimeChangeStamp? lastStamp;
    private DateTime nextFallback;
    private Guid resourceCollection;
    private string? resourceMapKey;
    private Task pending = Task.CompletedTask;
    private ulong actor;
    private int generation;
    private ImmutableArray<AnimationCapture> history = [];
    public ImmutableArray<AnimationCapture> History => history;
    public ulong Actor => actor;
    public string Status { get; private set; } = "Waiting for the local player…";
    public event Action? CharacterChanged;

    public AnimationObserver(IFramework framework, IObjectTable objects, PenumbraService penumbra,
        AnimationNative native, AnimationResources resources, LivePoseAdapter poses, AnimationCatalog catalog, AnimationSkeletonIndex skeletons, IPluginLog log)
    {
        this.framework = framework; this.objects = objects; this.penumbra = penumbra;
        this.native = native; this.resources = resources; this.poses = poses; this.catalog = catalog;
        this.skeletons = skeletons;
        this.log = log;
    }
    public void Start()
    {
        if (listeningEnabled || lifetime.IsCancellationRequested) return;
        listeningEnabled = true;
        skeletons.StartSessionLibrary(lifetime.Token);
        listening = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        lastStamp = null;
        nextFallback = DateTime.MinValue;
        framework.Update += Update;
    }
    public void Stop()
    {
        if (!listeningEnabled) return;
        listeningEnabled = false;
        framework.Update -= Update;
        observation?.Cancel();
        observation?.Dispose();
        observation = null;
        listening?.Cancel();
        listening?.Dispose();
        listening = null;
        lastStamp = null;
        runtimeCache.Clear();
        resourceCache.Clear();
        parsedPaps.Clear();
        motionCache.Clear();
        durations.Clear();
        generation++;
        MarkNotPlaying();
        Status = "Open Instant Edit to observe player animations.";
    }
    private void Update(IFramework _)
    {
        var listeningToken = listening;
        if (!listeningEnabled || listeningToken is null || listeningToken.IsCancellationRequested) return;
        RuntimeChangeStamp? stamp;
        try { stamp = AnimationRuntime.CaptureChangeStamp(objects); }
        catch (Exception e) { log.Debug(e, "Could not capture the animation change stamp."); return; }
        var current = stamp?.ActorId ?? 0;
        if (current != actor)
        {
            observation?.Cancel();
            actor = current; generation++; history = []; CharacterChanged?.Invoke();
            resolutions.Clear(); manualChoices.Clear(); operationErrors.Clear();
            runtimeCache.Clear(); resourceCache.Clear(); parsedPaps.Clear(); motionCache.Clear(); durations.Clear();
            resourceCollection = Guid.Empty; resourceMapKey = null;
        }
        if (current == 0) { Status = "Log in to observe player emotes, idles, and walk cycles."; return; }
        var changed = !lastStamp.HasValue || stamp is null || !lastStamp.Value.SameAs(stamp.Value);
        var now = DateTime.UtcNow;
        // Leave the last scheduled stamp untouched while work is in flight. If
        // the player changes during a capture, the next framework tick after
        // completion will immediately schedule a fresh capture instead of
        // accidentally treating the change as already observed.
        if (!pending.IsCompleted) return;
        if (!changed && now < nextFallback) return;
        lastStamp = stamp;
        nextFallback = now.AddMilliseconds(1500);
        observation?.Dispose();
        observation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, listeningToken.Token);
        pending = ObserveAsync(generation, observation.Token);
    }

    private async Task ObserveAsync(int expectedGeneration, CancellationToken token)
    {
        var observationStarted = Stopwatch.GetTimestamp();
        try
        {
            var frameworkStarted = Stopwatch.GetTimestamp();
            var pair = await framework.RunOnFrameworkThread(() =>
            {
                token.ThrowIfCancellationRequested();
                var runtime = AnimationRuntime.Capture(objects, runtimeCache);
                string? error = null;
                PoseSnapshot pose;
                try
                {
                    pose = poses.Capture();
                    if (runtime != null && !pose.Global && (runtime.Timelines[0] != pose.MainTimeline || runtime.Timelines[1] != pose.UpperTimeline || runtime.Timelines[2] != pose.FaceTimeline))
                        throw new InvalidOperationException("Waiting for LivePose to capture the current timeline transition.");
                }
                catch (Exception e)
                {
                    error = e.Message;
                    pose = new(0, 0, 0, false, [], DateTime.UtcNow, "");
                }
                return (Runtime: runtime, Pose: pose, PoseError: error);
            });
            var frameworkMillis = Stopwatch.GetElapsedTime(frameworkStarted).TotalMilliseconds;
            if (frameworkMillis >= 2)
                log.Debug($"Animation observation framework capture took {frameworkMillis:F2} ms.");
            token.ThrowIfCancellationRequested();
            if (pair.Runtime is not { } runtime) { MarkNotPlaying(); Status = "Waiting for an eligible player animation."; return; }
            var active = runtime.Timelines.Distinct().Select(catalog.Find).OfType<AnimationCatalog.Timeline>().ToArray();
            if (active.Length == 0) { MarkNotPlaying(); Status = "No eligible emote, idle, or walk cycle is playing."; return; }
            var collection = await penumbra.GetCollectionTargetAsync(runtime.ObjectIndex) ?? throw new IOException("The player collection is unavailable.");
            token.ThrowIfCancellationRequested();
            var paths = await penumbra.GetResourcePathsAsync(runtime.ObjectIndex) ?? throw new IOException("Penumbra's player resource snapshot is unavailable.");
            token.ThrowIfCancellationRequested();
            var mapKey = AnimationResources.ResourceMapKey(paths);
            var aliases = ImmutableDictionary.CreateBuilder<string, ImmutableArray<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var pathMap in paths)
                aliases[pathMap.Key] = AnimationSkeletonIndex.NormalizeAliases(pathMap.Value);
            var resourceAliases = aliases.ToImmutable();
            if (resourceCollection != collection.Id || resourceMapKey != mapKey)
            {
                resourceCache.Clear();
                motionCache.Clear();
                durations.Clear();
                resourceCollection = collection.Id;
                resourceMapKey = mapKey;
            }
            var gamePaths = paths.Values.SelectMany(p => p).Distinct(StringComparer.Ordinal).ToArray();
            var motionReferences = new Dictionary<ushort, ImmutableHashSet<string>>();
            async Task<ImmutableHashSet<string>> TimelineMotions(AnimationCatalog.Timeline timeline)
            {
                if (motionReferences.TryGetValue(timeline.Id, out var found)) return found;
                var motionKey = $"{collection.Id:N}:{mapKey}:{timeline.Id}";
                if (motionCache.TryGetValue(motionKey, out found)) return motionReferences[timeline.Id] = found;
                var names = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
                var pendingPaths = new Queue<string>(); pendingPaths.Enqueue($"chara/action/{timeline.Key}.tmb");
                var visited = new HashSet<string>(StringComparer.Ordinal);
                while (pendingPaths.TryDequeue(out var path))
                {
                    if (!visited.Add(path)) continue;
                    if (visited.Count > 128) throw new InvalidDataException("The active timeline graph exceeds the observation limit.");
                    (AnimationResource Resource, byte[] Bytes) source;
                    try { source = await resources.ReadAsync(collection.Id, path, token, resourceCache); }
                    catch (FileNotFoundException) when (visited.Count == 1) { break; }
                    foreach (var reference in AnimationDependencies.Read(path, source.Bytes).References)
                        if (reference.Kind == "animation") names.Add(reference.Path);
                        else if (reference.Kind == "timeline") pendingPaths.Enqueue(reference.Path.EndsWith(".tmb", StringComparison.Ordinal) ? reference.Path : $"chara/action/{reference.Path}.tmb");
                }
                found = names.ToImmutable();
                if (motionCache.Count >= 128) motionCache.TryRemove(motionCache.Keys.First(), out _);
                motionCache[motionKey] = found;
                return motionReferences[timeline.Id] = found;
            }
            foreach (var timeline in active)
                try { await TimelineMotions(timeline); }
                catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { motionReferences[timeline.Id] = ImmutableHashSet<string>.Empty; }
            var inferredPaths = active.SelectMany(t => AnimationCatalog.CandidatePapPaths(t, gamePaths)).ToHashSet(StringComparer.Ordinal);
            var candidatePaths = gamePaths.Where(p => p.EndsWith(".pap", StringComparison.OrdinalIgnoreCase))
                .Concat(inferredPaths).Distinct(StringComparer.Ordinal).ToArray();
            var candidates = new List<AnimationPapCandidate>();
            var missingPaths = new List<string>();
            foreach (var path in candidatePaths)
            {
                try
                {
                token.ThrowIfCancellationRequested();
                (AnimationResource Resource, byte[] Bytes) source;
                try { source = await resources.ReadAsync(collection.Id, path, token, resourceCache); }
                catch (FileNotFoundException) when (inferredPaths.Contains(path)) { missingPaths.Add(path); continue; }
                if (!prints.TryGetValue(source.Resource.Hash, out var bindings))
                {
                    bindings = await framework.RunOnTick(() => { token.ThrowIfCancellationRequested(); return AnimationRuntime.InspectPap(source.Bytes); }, delayTicks: 1);
                    if (prints.Count >= 128) prints.Clear();
                    prints[source.Resource.Hash] = bindings;
                }
                if (!durations.TryGetValue(source.Resource.Hash, out var bindingDurations))
                {
                    bindingDurations = await framework.RunOnTick(() => { token.ThrowIfCancellationRequested(); return AnimationRuntime.InspectPapDurations(source.Bytes); }, delayTicks: 1);
                    if (durations.Count >= 128) durations.TryRemove(durations.Keys.First(), out _);
                    durations[source.Resource.Hash] = bindingDurations;
                }
                if (!parsedPaps.TryGetValue(source.Resource.Hash, out var parsed))
                {
                    parsed = new AnimationPap(source.Bytes);
                    if (parsedPaps.Count >= 128) parsedPaps.TryRemove(parsedPaps.Keys.First(), out _);
                    parsedPaps[source.Resource.Hash] = parsed;
                }
                candidates.Add(new AnimationPapCandidate(source.Resource, parsed, bindings, bindingDurations));
                }
                catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException) { missingPaths.Add(path); }
            }
            gamePaths = gamePaths.Concat(candidates.Select(c => c.Resource.GamePath)).Distinct(StringComparer.Ordinal).ToArray();
            var captures = new List<AnimationCapture>();
            var matchedBindings = 0; var ambiguousBindings = 0;
            foreach (var binding in runtime.Bindings)
            {
                // Scope by the active timeline before testing uniqueness. An unrelated
                // PAP with identical motion data does not make the current idle ambiguous.
                var matches = AnimationMatching.Find(binding.Fingerprint, candidates, active, motionReferences);
                if (matches.Length == 0) continue;
                matchedBindings++;
                if (matches.Length != 1) { ambiguousBindings++; continue; }
                var match = matches[0];
                var timeline = match.Timeline;
                var pap = candidates.First(candidate => candidate.Resource == match.Resource).Pap;
                var sourceIdentity = AnimationSkeletonIndex.SourceIdentity(
                    AnimationSkeletonIndex.AliasesFor(paths, match.Resource.ResolvedPath),
                    pap.ModelType == 0 ? AnimationSkeletonIndex.ModelCode(pap.ModelId) : null);
                var skeletonPaths = paths.Where(p => string.Equals(p.Key, binding.SkeletonResource, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(p => p.Value).Where(p => p.EndsWith(".sklb", StringComparison.OrdinalIgnoreCase)).Distinct().ToArray();
                if (skeletonPaths.Length == 0 && AnimationDependencies.SafeGamePath(binding.SkeletonResource)) skeletonPaths = [binding.SkeletonResource];
                var clip = new AnimationClip(match.Resource.GamePath, match.Entry.Name, match.Entry.Binding,
                    binding.Partial, timeline.Id, skeletonPaths.FirstOrDefault() ?? "", binding.SkeletonFingerprint,
                    binding.Skeleton, new(SkeletonResolutionState.Searching, [], Reason: "Finding a compatible source skeleton…"), binding.Fingerprint,
                    $"{collection.Id}:{match.Resource.Hash}:{match.Resource.ResolvedPath}:{sourceIdentity.MappingFingerprint}:{sourceIdentity.CanonicalModel}",
                    SourceIdentity: sourceIdentity,
                    Duration: candidates.First(candidate => candidate.Resource == match.Resource).Durations?.GetValueOrDefault(match.Entry.Binding) ?? 0,
                    IsLoop: timeline.Loop);
                if (clip.SkeletonPath.Length == 0)
                    clip = clip with { SkeletonPath = AnimationSkeletonIndex.RelevantPaths(clip, []).FirstOrDefault() ?? "" };
                var family = new HashSet<string>(StringComparer.Ordinal) { clip.GamePath };
                var skeletonIdentity = string.Join('/', clip.SkeletonPath.Split('/').Take(3)) + "/";
                family.UnionWith(gamePaths.Where(p => p.StartsWith(skeletonIdentity, StringComparison.Ordinal) &&
                    Path.GetExtension(p) is ".skp" or ".phyb" or ".eid"));
                string? packagingError = null;
                var startupPaths = new List<(AnimationCatalog.Timeline? Timeline, string Path)>();
                foreach (var id in timeline.Family)
                {
                    var relative = catalog.Find(id);
                    if (relative == null) { packagingError = $"Family timeline {id} is unavailable."; continue; }
                    var familyPap = AnimationCatalog.PapPath(relative, clip.GamePath, gamePaths);
                    if (familyPap == null) { packagingError = $"Cannot determine the player's variant for {relative.Key}."; continue; }
                    family.Add(familyPap);
                    // Only existing external timelines are used. Some emotes contain their timeline exclusively inside PAP.
                    var tmb = $"chara/action/{relative.Key}.tmb";
                    try { _ = await resources.ReadAsync(collection.Id, tmb, token, resourceCache); family.Add(tmb); }
                    catch (FileNotFoundException) { }
                    catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { packagingError = e.Message; }
                    if (timeline.Startups.Contains(id)) startupPaths.Add((relative, familyPap));
                }
                // Some idle loops, including pose01_loop, have a valid sibling
                // startup but no Emote-sheet relationship. Discover it through the
                // captured game path, then still validate its effective PAP below.
                if (startupPaths.Count == 0 && AnimationCatalog.SiblingStartupPath(clip.GamePath) is { } sibling)
                {
                    family.Add(sibling);
                    startupPaths.Add((null, sibling));
                }
                var sources = new List<AnimationResource> { match.Resource };
                AnimationClip? startup = null;
                if (startupPaths.Count == 1)
                {
                    try
                    {
                    var item = startupPaths[0];
                    var startupSource = await resources.ReadAsync(collection.Id, item.Path, token, resourceCache);
                    var startupPap = GetPap(startupSource);
                    var startupIdentity = AnimationSkeletonIndex.SourceIdentity(
                        AnimationSkeletonIndex.AliasesFor(paths, startupSource.Resource.ResolvedPath),
                        startupPap.ModelType == 0 ? AnimationSkeletonIndex.ModelCode(startupPap.ModelId) : null);
                    var papEntries = startupPap.Entries;
                    var startupMotions = item.Timeline == null ? null : await TimelineMotions(item.Timeline);
                    var entries = startupMotions == null ? papEntries.ToArray() : papEntries.Where(e => startupMotions.Contains(e.Name)).ToArray();
                    if (entries.Length == 0 && papEntries.Length == 1) entries = papEntries.ToArray();
                    if (entries.Length == 1)
                    {
                        if (!prints.TryGetValue(startupSource.Resource.Hash, out var startupPrints))
                        {
                            startupPrints = await framework.RunOnTick(() => AnimationRuntime.InspectPap(startupSource.Bytes), delayTicks: 1);
                            if (prints.Count >= 128) prints.Clear();
                            prints[startupSource.Resource.Hash] = startupPrints;
                        }
                        if (!durations.TryGetValue(startupSource.Resource.Hash, out var startupDurations))
                        {
                            startupDurations = await framework.RunOnTick(() => AnimationRuntime.InspectPapDurations(startupSource.Bytes), delayTicks: 1);
                            if (durations.Count >= 128) durations.TryRemove(durations.Keys.First(), out _);
                            durations[startupSource.Resource.Hash] = startupDurations;
                        }
                        startup = new AnimationClip(item.Path, entries[0].Name, entries[0].Binding, clip.Partial,
                            item.Timeline?.Id ?? timeline.Id, clip.SkeletonPath, clip.SkeletonFingerprint, binding.Skeleton,
                            new(SkeletonResolutionState.Searching, []), startupPrints.GetValueOrDefault(entries[0].Binding, ""),
                            $"{collection.Id}:{startupSource.Resource.Hash}:{startupSource.Resource.ResolvedPath}:{startupIdentity.MappingFingerprint}:{startupIdentity.CanonicalModel}",
                            SourceIdentity: startupIdentity,
                            Duration: startupDurations.GetValueOrDefault(entries[0].Binding),
                            IsLoop: item.Timeline?.Loop ?? false);
                        sources.Add(startupSource.Resource);
                    }
                    }
                    catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException) { packagingError = e.Message; }
                }
                var idString = $"{runtime.ActorId}:{timeline.Id}:{clip.GamePath}:{clip.BindingIndex}:{clip.Partial}";
                captures.Add(new AnimationCapture(idString, runtime.ActorId, runtime.Address, collection.Id, collection.Name,
                    timeline.Name, clip, startup, family.ToImmutableArray(), sources.Distinct().ToImmutableArray(), pair.Pose,
                    pair.Pose.CapturedUtc, true, PackagingError: packagingError, LoadedResourcePaths: gamePaths.ToImmutableArray(),
                    PoseUnavailableReason: pair.PoseError, ResourceAliases: resourceAliases));
            }
            // Publish identified clips before any native source-skeleton search.
            await PublishAsync(captures, expectedGeneration, token);
            if (matching.IsCompleted) matching = MatchAsync(captures.ToArray(), expectedGeneration, token);
            token.ThrowIfCancellationRequested();
            await framework.RunOnFrameworkThread(() =>
            {
                if (expectedGeneration != generation) return;
                Status = captures.Count > 0 ? $"Listening · {captures.Count} playing · {history.Length} recent" :
                    $"No editable animation matched. Active: {string.Join(", ", active.Select(t => $"{t.Id} ({t.Key})"))}. " +
                    $"Playing bindings: {runtime.Bindings.Length}; PAPs checked: {candidates.Count}; " +
                    $"timeline matches: {matchedBindings}; ambiguous: {ambiguousBindings}." +
                    (missingPaths.Count == 0 ? "" : $" Missing PAPs: {string.Join(", ", missingPaths)}.");
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            if (token.IsCancellationRequested || expectedGeneration != generation) return;
            MarkNotPlaying(); Status = e.InnerException?.Message ?? e.Message;
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(observationStarted).TotalMilliseconds;
            if (elapsed >= 2)
                log.Debug($"Animation observation pipeline took {elapsed:F2} ms.");
        }
    }
    private async Task MatchAsync(AnimationCapture[] captures, int expectedGeneration, CancellationToken token)
    {
        var expectedRevision = skeletons.Revision;
        try
        {
            foreach (var capture in captures)
            {
                if (expectedGeneration != generation || expectedRevision != skeletons.Revision) return;
                var resolved = await ResolveClip(capture, capture.Clip, token);
                var startup = capture.Startup == null ? null : await ResolveClip(capture, capture.Startup, token);
                await framework.RunOnFrameworkThread(() =>
                {
                    if (expectedGeneration != generation || expectedRevision != skeletons.Revision) return;
                    history = history.Select(c => c.Id == capture.Id && ChoiceKey(c.Clip) == ChoiceKey(capture.Clip)
                        ? c with { Clip = resolved, Startup = startup } : c).ToImmutableArray();
                });
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            if (token.IsCancellationRequested || expectedGeneration != generation || expectedRevision != skeletons.Revision) return;
            log.Warning(e, "Animation skeleton matching failed.");
            await framework.RunOnFrameworkThread(() =>
            {
                if (expectedGeneration != generation || expectedRevision != skeletons.Revision) return;
                foreach (var capture in captures)
                {
                    var result = new SkeletonResolution(SkeletonResolutionState.Incompatible, [], Reason: e.GetBaseException().Message);
                    resolutions[ChoiceKey(capture.Clip)] = (DateTime.UtcNow, result);
                }
                history = history.Select(c => resolutions.TryGetValue(ChoiceKey(c.Clip), out var result)
                    ? c with { Clip = c.Clip with { Resolution = result.Result } } : c).ToImmutableArray();
            });
        }
    }
    private Task PublishAsync(IEnumerable<AnimationCapture> captures, int expectedGeneration, CancellationToken token) => framework.RunOnFrameworkThread(() =>
    {
        token.ThrowIfCancellationRequested();
        if (expectedGeneration != generation) return;
        AnimationClip Cached(AnimationClip clip) => clip with
        {
            Resolution = resolutions.TryGetValue(ChoiceKey(clip), out var cached) ? cached.Result : clip.Resolution,
            LastOperationError = operationErrors.GetValueOrDefault(ChoiceKey(clip)),
        };
        var current = captures.DistinctBy(c => c.Id).Select(c => c with { Clip = Cached(c.Clip), Startup = c.Startup == null ? null : Cached(c.Startup) }).ToArray();
        history = MergeHistory(current, history);
        Status = $"Listening · {current.Length} playing · {history.Length} recent";
    });
    internal static ImmutableArray<AnimationCapture> MergeHistory(IEnumerable<AnimationCapture> captures, ImmutableArray<AnimationCapture> previous)
    {
        var current = captures.DistinctBy(c => c.Id).ToArray();
        var ids = current.Select(c => c.Id).ToHashSet();
        return current.Concat(previous.Where(c => !ids.Contains(c.Id)).Select(c => c with { Playing = false })).Take(50).ToImmutableArray();
    }
    private string ChoiceKey(AnimationClip clip) => $"{actor}:{clip.SourceContext}:{clip.GamePath}:{clip.BindingIndex}:{clip.BindingFingerprint}:{clip.SkeletonFingerprint}:{clip.SourceIdentity?.MappingFingerprint}:{clip.SourceIdentity?.CanonicalModel}:{skeletons.Revision}";
    private async Task<AnimationClip> ResolveClip(AnimationCapture capture, AnimationClip clip, CancellationToken token)
    {
        var key = ChoiceKey(clip);
        if (resolutions.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.At < TimeSpan.FromSeconds(30)) return clip with { Resolution = cached.Result };
        SkeletonResolution result;
        try
        {
            var source = await resources.ReadAsync(capture.CollectionId, clip.GamePath, token, resourceCache);
            var pathMap = capture.ResourceAliases.ToDictionary(pair => pair.Key,
                pair => pair.Value.ToHashSet(StringComparer.Ordinal), StringComparer.OrdinalIgnoreCase);
            result = await skeletons.ResolveAsync(capture.CollectionId, clip, source.Bytes, capture.LoadedResourcePaths,
                pathMap, manualChoices.GetValueOrDefault(key), token);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            log.Warning(e, $"Skeleton matching failed for {clip.GamePath}, binding {clip.BindingIndex}.");
            result = new(SkeletonResolutionState.Incompatible, [], Reason: e.GetBaseException().Message);
        }
        if (resolutions.Count >= 128) resolutions.Clear();
        if (manualChoices.TryGetValue(key, out var latestChoice) && result.Candidates.FirstOrDefault(c => AnimationSkeletonIndex.SelectionId(c) == latestChoice) is { } selected)
            result = result with { State = SkeletonResolutionState.Matched, Selected = selected, Reason = null };
        resolutions[key] = (DateTime.UtcNow, result);
        return clip with { Resolution = result };
    }
    private AnimationPap GetPap((AnimationResource Resource, byte[] Bytes) source)
    {
        if (parsedPaps.TryGetValue(source.Resource.Hash, out var pap)) return pap;
        pap = new AnimationPap(source.Bytes);
        if (parsedPaps.Count >= 128) parsedPaps.TryRemove(parsedPaps.Keys.First(), out _);
        parsedPaps[source.Resource.Hash] = pap;
        return pap;
    }

    public void RescanSkeletons()
    {
        skeletons.Rescan(); resolutions.Clear(); manualChoices.Clear(); operationErrors.Clear();
        lastStamp = null; nextFallback = DateTime.MinValue; resourceCache.Clear(); motionCache.Clear(); parsedPaps.Clear(); durations.Clear();
    }
    public void ReportOperationError(AnimationClip clip, string? error)
    {
        var key = ChoiceKey(clip);
        if (error == null) operationErrors.TryRemove(key, out _); else operationErrors[key] = error;
        history = history.Select(c => ChoiceKey(c.Clip) == key ? c with { Clip = c.Clip with { LastOperationError = error } } : c).ToImmutableArray();
    }
    public AnimationClip ChooseSkeleton(AnimationClip clip, SkeletonCandidate candidate)
    {
        if (clip.Resolution == null || !clip.Resolution.Candidates.Contains(candidate)) throw new InvalidOperationException("Skeleton candidate changed. Refresh the capture.");
        if (clip.Resolution.State == SkeletonResolutionState.Incompatible)
            throw new InvalidOperationException("A compatible source reference pose is required before retargeting.");
        var key = ChoiceKey(clip); manualChoices[key] = AnimationSkeletonIndex.SelectionId(candidate);
        operationErrors.TryRemove(key, out _);
        var resolution = clip.Resolution with { State = SkeletonResolutionState.Matched, Selected = candidate, Reason = null };
        resolutions[key] = (DateTime.UtcNow, resolution);
        history = history.Select(c => c with
        {
            Clip = ChoiceKey(c.Clip) == key ? c.Clip with { Resolution = resolution, LastOperationError = null } : c.Clip,
            Startup = c.Startup != null && ChoiceKey(c.Startup) == key ? c.Startup with { Resolution = resolution } : c.Startup,
        }).ToImmutableArray();
        return clip with { Resolution = resolution, LastOperationError = null };
    }
    private void MarkNotPlaying() => history = history.Select(c => c with { Playing = false }).ToImmutableArray();
    public void Dispose()
    {
        Stop();
        lifetime.Cancel();
        lifetime.Dispose();
    }
}
