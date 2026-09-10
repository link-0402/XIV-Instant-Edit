using System.Collections.Immutable;
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
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? observation;
    private readonly Dictionary<string, ImmutableDictionary<int, string>> prints = [];
    private DateTime next;
    private Task pending = Task.CompletedTask;
    private ulong actor;
    private int generation;
    private ImmutableArray<AnimationCapture> history = [];
    public ImmutableArray<AnimationCapture> History => history;
    public ulong Actor => actor;
    public string Status { get; private set; } = "Waiting for the local player…";
    public event Action? CharacterChanged;

    public AnimationObserver(IFramework framework, IObjectTable objects, PenumbraService penumbra,
        AnimationNative native, AnimationResources resources, LivePoseAdapter poses, AnimationCatalog catalog)
    {
        this.framework = framework; this.objects = objects; this.penumbra = penumbra;
        this.native = native; this.resources = resources; this.poses = poses; this.catalog = catalog;
        framework.Update += Update;
    }
    private unsafe ulong ActorId() => objects.LocalPlayer is { } p ?
        ((FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)p.Address)->ContentId : 0;
    private void Update(IFramework _)
    {
        var current = ActorId();
        if (current != actor)
        {
            observation?.Cancel();
            actor = current; generation++; history = []; CharacterChanged?.Invoke();
        }
        if (current == 0) { Status = "Log in to observe player emotes and idles."; return; }
        if (!pending.IsCompleted || DateTime.UtcNow < next) return;
        next = DateTime.UtcNow.AddMilliseconds(500);
        observation?.Dispose();
        observation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        pending = ObserveAsync(generation, observation.Token);
    }

    private async Task ObserveAsync(int expectedGeneration, CancellationToken token)
    {
        try
        {
            native.EnsureAvailable();
            var pair = await framework.RunOnFrameworkThread(() =>
            {
                token.ThrowIfCancellationRequested();
                return (Runtime: AnimationRuntime.Capture(objects), Pose: poses.Capture());
            });
            if (pair.Runtime is not { } runtime) { MarkNotPlaying(); Status = "Waiting for an eligible player animation."; return; }
            if (!pair.Pose.Global && (runtime.Timelines[0] != pair.Pose.MainTimeline || runtime.Timelines[1] != pair.Pose.UpperTimeline || runtime.Timelines[2] != pair.Pose.FaceTimeline))
                throw new InvalidOperationException("Waiting for LivePose to capture the current timeline transition.");
            var active = runtime.Timelines.Distinct().Select(catalog.Find).OfType<AnimationCatalog.Timeline>().ToArray();
            if (active.Length == 0) { MarkNotPlaying(); Status = "No eligible emote or idle is playing."; return; }
            var collection = await penumbra.GetCollectionTargetAsync(runtime.ObjectIndex) ?? throw new IOException("The player collection is unavailable.");
            var paths = await penumbra.GetResourcePathsAsync(runtime.ObjectIndex) ?? throw new IOException("Penumbra's player resource snapshot is unavailable.");
            var gamePaths = paths.Values.SelectMany(p => p).Distinct(StringComparer.Ordinal).ToArray();
            var motionReferences = new Dictionary<ushort, ImmutableHashSet<string>>();
            async Task<ImmutableHashSet<string>> TimelineMotions(AnimationCatalog.Timeline timeline)
            {
                if (motionReferences.TryGetValue(timeline.Id, out var found)) return found;
                var names = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
                var pendingPaths = new Queue<string>(); pendingPaths.Enqueue($"chara/action/{timeline.Key}.tmb");
                var visited = new HashSet<string>(StringComparer.Ordinal);
                while (pendingPaths.TryDequeue(out var path))
                {
                    if (!visited.Add(path)) continue;
                    if (visited.Count > 128) throw new InvalidDataException("The active timeline graph exceeds the observation limit.");
                    (AnimationResource Resource, byte[] Bytes) source;
                    try { source = await resources.ReadAsync(collection.Id, path, token); }
                    catch (FileNotFoundException) when (visited.Count == 1) { break; }
                    foreach (var reference in AnimationDependencies.Read(path, source.Bytes).References)
                        if (reference.Kind == "animation") names.Add(reference.Path);
                        else if (reference.Kind == "timeline") pendingPaths.Enqueue(reference.Path.EndsWith(".tmb", StringComparison.Ordinal) ? reference.Path : $"chara/action/{reference.Path}.tmb");
                }
                return motionReferences[timeline.Id] = names.ToImmutable();
            }
            foreach (var timeline in active) await TimelineMotions(timeline);
            var candidates = new List<(AnimationResource Resource, AnimationPap Pap, ImmutableDictionary<int, string> Prints)>();
            foreach (var path in gamePaths.Where(p => p.EndsWith(".pap", StringComparison.OrdinalIgnoreCase)))
            {
                token.ThrowIfCancellationRequested();
                var source = await resources.ReadAsync(collection.Id, path, token);
                if (!prints.TryGetValue(source.Resource.Hash, out var bindings))
                {
                    bindings = await framework.RunOnTick(() => { token.ThrowIfCancellationRequested(); return AnimationRuntime.InspectPap(source.Bytes); }, delayTicks: 1);
                    if (prints.Count >= 128) prints.Clear();
                    prints[source.Resource.Hash] = bindings;
                }
                candidates.Add((source.Resource, new AnimationPap(source.Bytes), bindings));
            }
            var captures = new List<AnimationCapture>();
            foreach (var binding in runtime.Bindings)
            {
                var matches = candidates.SelectMany(p => p.Pap.Entries.Where(e => p.Prints.GetValueOrDefault(e.Binding) == binding.Fingerprint)
                    .Select(e => (p.Resource, Entry: e))).Distinct().ToArray();
                // Duplicate byte-identical providers/entries are ambiguous, even if the last control looks plausible.
                if (matches.Length != 1) continue;
                var match = matches[0];
                var timelines = active.Where(t => catalog.Matches(t, match.Resource.GamePath, match.Entry.Name) ||
                    motionReferences[t.Id].Contains(match.Entry.Name) || motionReferences[t.Id].Contains(match.Resource.GamePath)).ToArray();
                if (timelines.Length != 1) continue;
                var timeline = timelines[0];
                var skeletonPaths = paths.Where(p => string.Equals(p.Key, binding.SkeletonResource, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(p => p.Value).Where(p => p.EndsWith(".sklb", StringComparison.OrdinalIgnoreCase)).Distinct().ToArray();
                if (skeletonPaths.Length == 0 && AnimationDependencies.SafeGamePath(binding.SkeletonResource)) skeletonPaths = [binding.SkeletonResource];
                if (skeletonPaths.Length == 0)
                {
                    // Penumbra may give a resource handle a collection-qualified virtual filename.
                    // Resolve it through the actual skeleton graph instead of stripping an assumed filename prefix.
                    var matching = new List<string>();
                    foreach (var path in gamePaths.Where(p => p.EndsWith(".sklb", StringComparison.OrdinalIgnoreCase)))
                    {
                        var candidate = await resources.ReadAsync(collection.Id, path, token);
                        var fingerprint = await framework.RunOnTick(() =>
                        { token.ThrowIfCancellationRequested(); return AnimationRuntime.InspectSkeleton(candidate.Bytes); }, delayTicks: 1);
                        if (fingerprint == binding.SkeletonFingerprint) matching.Add(path);
                    }
                    skeletonPaths = matching.ToArray();
                }
                if (skeletonPaths.Length != 1) continue;
                var sklb = await resources.ReadAsync(collection.Id, skeletonPaths[0], token);
                await framework.RunOnTick(() => { token.ThrowIfCancellationRequested(); AnimationRuntime.CheckSkeleton(sklb.Bytes, binding.SkeletonFingerprint); }, delayTicks: 1);
                var clip = new AnimationClip(match.Resource.GamePath, match.Entry.Name, match.Entry.Binding,
                    binding.Partial, timeline.Id, skeletonPaths[0], binding.SkeletonFingerprint);
                var family = new HashSet<string>(StringComparer.Ordinal) { clip.GamePath, clip.SkeletonPath };
                var skeletonIdentity = string.Join('/', clip.SkeletonPath.Split('/').Take(3)) + "/";
                family.UnionWith(gamePaths.Where(p => p.StartsWith(skeletonIdentity, StringComparison.Ordinal) &&
                    Path.GetExtension(p) is ".skp" or ".phyb" or ".eid"));
                string? packagingError = null;
                var startupPaths = new List<(AnimationCatalog.Timeline Timeline, string Path)>();
                foreach (var id in timeline.Family)
                {
                    var relative = catalog.Find(id);
                    if (relative == null) { packagingError = $"Family timeline {id} is unavailable."; continue; }
                    var pap = AnimationCatalog.PapPath(relative, clip.GamePath, gamePaths);
                    if (pap == null) { packagingError = $"Cannot determine the player's variant for {relative.Key}."; continue; }
                    family.Add(pap);
                    // Only existing external timelines are used. Some emotes contain their timeline exclusively inside PAP.
                    var tmb = $"chara/action/{relative.Key}.tmb";
                    try { _ = await resources.ReadAsync(collection.Id, tmb, token); family.Add(tmb); }
                    catch (FileNotFoundException) { }
                    if (timeline.Startups.Contains(id)) startupPaths.Add((relative, pap));
                }
                var sources = new List<AnimationResource> { match.Resource, sklb.Resource };
                AnimationClip? startup = null;
                if (startupPaths.Count == 1)
                {
                    var item = startupPaths[0];
                    var startupSource = await resources.ReadAsync(collection.Id, item.Path, token);
                    var startupMotions = await TimelineMotions(item.Timeline);
                    var papEntries = new AnimationPap(startupSource.Bytes).Entries;
                    var entries = papEntries.Where(e => startupMotions.Contains(e.Name)).ToArray();
                    if (entries.Length == 0 && papEntries.Length == 1) entries = papEntries.ToArray();
                    if (entries.Length == 1)
                    {
                        startup = new AnimationClip(item.Path, entries[0].Name, entries[0].Binding, clip.Partial,
                            item.Timeline.Id, clip.SkeletonPath, clip.SkeletonFingerprint);
                        sources.Add(startupSource.Resource);
                    }
                }
                var idString = $"{runtime.ActorId}:{timeline.Id}:{clip.GamePath}:{clip.BindingIndex}:{clip.Partial}";
                captures.Add(new AnimationCapture(idString, runtime.ActorId, runtime.Address, collection.Id, collection.Name,
                    timeline.Name, clip, startup, family.ToImmutableArray(), sources.Distinct().ToImmutableArray(), pair.Pose,
                    pair.Pose.CapturedUtc, true, PackagingError: packagingError, LoadedResourcePaths: gamePaths.ToImmutableArray()));
            }
            token.ThrowIfCancellationRequested();
            await framework.RunOnFrameworkThread(() =>
            {
                if (expectedGeneration != generation) return;
                var currentIds = captures.Select(c => c.Id).ToHashSet();
                history = captures.DistinctBy(c => c.Id).Concat(history.Where(c => !currentIds.Contains(c.Id)).Select(c => c with { Playing = false }))
                    .Take(50).ToImmutableArray();
                Status = captures.Count > 0 ? $"Listening · {captures.Count} playing · {history.Length} recent" :
                    "No unique PAP binding matched the active timelines. Wait for the transition to finish.";
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { MarkNotPlaying(); Status = e.InnerException?.Message ?? e.Message; }
    }
    private void MarkNotPlaying() => history = history.Select(c => c with { Playing = false }).ToImmutableArray();
    public void Dispose() { framework.Update -= Update; lifetime.Cancel(); }
}
