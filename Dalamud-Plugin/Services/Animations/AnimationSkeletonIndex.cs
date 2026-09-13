// Discovery/ranking adapted from VFXEditor's SkeletonMatcher (GPL-3.0).
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dalamud.Plugin.Services;
using InstantEdit.Models;

namespace InstantEdit.Services.Animations;

internal sealed class AnimationSkeletonIndex(PenumbraService penumbra, AnimationResources resources, IFramework framework,
    IPluginLog log, Func<string> configuredCacheRoot)
{
    private readonly Dictionary<string, ImmutableArray<AnimationSkeleton.Variant>> descriptions = [];
    private readonly Dictionary<SkeletonSource, (long Length, DateTime Write, ImmutableArray<SkeletonCandidate> Candidates)> files = [];
    private readonly object libraryLock = new();
    private readonly object cacheLock = new();
    private ImmutableArray<SessionSkeleton> sessionLibrary = [];
    private Task? sessionLibraryBuild;
    private string? cacheDirectory;
    private int revision;
    private int loadedRevision = -1;
    public void Rescan()
    {
        Interlocked.Increment(ref revision);
        lock (libraryLock) { sessionLibrary = []; sessionLibraryBuild = null; }
    }
    public int Revision => Volatile.Read(ref revision);
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true };
    private sealed record CachedSkeleton(int Version, string SourceHash, ImmutableArray<AnimationSkeleton.Variant> Variants);
    private sealed record CachedLibrary(int Version, DateTime BuiltUtc, ImmutableArray<SessionSkeleton> Skeletons);
    internal sealed record SessionSkeleton(SkeletonDescription Skeleton, ImmutableArray<SkeletonSource> Sources)
    {
        public SkeletonCandidate CandidateFor(string expectedPath)
        {
            var source = Sources.OrderByDescending(s => string.Equals(s.Resource.GamePath, expectedPath, StringComparison.Ordinal))
                .ThenByDescending(s => s.Resource.GamePath.Length).ThenBy(s => s.Resource.ResolvedPath, StringComparer.Ordinal).First();
            return new(source, Skeleton, 0, "");
        }
    }
    internal static string SelectionId(SkeletonCandidate candidate) => candidate.Skeleton.Fingerprint + ":" + candidate.Source.Resource.Hash;

    internal static ImmutableArray<SessionSkeleton> DeduplicateLibrary(IEnumerable<SkeletonCandidate> candidates) => candidates
        .GroupBy(candidate => candidate.Skeleton.Fingerprint, StringComparer.Ordinal)
        .Select(group => new SessionSkeleton(group.First().Skeleton,
            group.Select(candidate => candidate.Source).Distinct().OrderBy(source => source.Resource.ResolvedPath, StringComparer.Ordinal).ToImmutableArray()))
        .OrderBy(entry => entry.Skeleton.Fingerprint, StringComparer.Ordinal).ToImmutableArray();

    private string CacheDirectory()
    {
        lock (cacheLock) return cacheDirectory ??= Path.Combine(configuredCacheRoot(), "skeleton-library");
    }

    /// <summary>Starts the once-per-plugin-session Penumbra skeleton library build.</summary>
    public void StartSessionLibrary(CancellationToken lifetimeToken)
    {
        var build = GetSessionLibraryBuild(lifetimeToken);
        _ = build.ContinueWith(task => log.Warning(task.Exception, "Could not build the session skeleton library."),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    private Task GetSessionLibraryBuild(CancellationToken lifetimeToken)
    {
        lock (libraryLock)
        {
            if (sessionLibraryBuild is { IsCanceled: true } or { IsFaulted: true }) sessionLibraryBuild = null;
            return sessionLibraryBuild ??= BuildSessionLibraryAsync(lifetimeToken);
        }
    }

    private async Task BuildSessionLibraryAsync(CancellationToken token)
    {
        var roots = await penumbra.AnimationSkeletonRootsAsync();
        var sources = await Task.Run(() => Scan(roots, token), token);
        var candidates = await ReadCandidatesAsync(sources, source => Describe(Guid.Empty, source, token),
            (source, error) => log.Debug($"Skeleton library candidate {source.Resource.ResolvedPath}: {error.Message}"), token);
        var library = DeduplicateLibrary(candidates);
        lock (libraryLock) sessionLibrary = library;
        await SaveLibraryAsync(library, token);
        log.Debug($"Built session skeleton library with {library.Length} unique skeletons from {sources.Length} registered files.");
    }

    private async Task SaveLibraryAsync(ImmutableArray<SessionSkeleton> library, CancellationToken token)
    {
        try
        {
            var directory = CacheDirectory();
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "session-library.json");
            var temporary = path + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new CachedLibrary(2, DateTime.UtcNow, library), Json), token);
            File.Move(temporary, path, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            log.Debug(e, "Could not cache the session skeleton library.");
        }
    }

    internal static ImmutableArray<SkeletonSource> Scan(IEnumerable<(string Directory, string Root)> roots, CancellationToken token)
    {
        var result = ImmutableArray.CreateBuilder<SkeletonSource>();
        foreach (var (directory, root) in roots)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                TextureFiles.EnsureLocalPath(root);
                var mappings = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                void Visit(JsonNode? node)
                {
                    if (node is JsonObject obj)
                    {
                        if (obj["Files"] is JsonObject files)
                            foreach (var file in files)
                            {
                                if (!file.Key.EndsWith(".sklb", StringComparison.OrdinalIgnoreCase) || !AnimationDependencies.SafeGamePath(file.Key) ||
                                    file.Value is not JsonValue value || !value.TryGetValue<string>(out var relative)) continue;
                                var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
                                if (!PathRules.IsPathWithin(full, root)) continue;
                                if (!mappings.TryGetValue(full, out var paths)) mappings[full] = paths = new(StringComparer.Ordinal);
                                paths.Add(file.Key);
                            }
                        foreach (var property in obj) if (property.Key != "Files") Visit(property.Value);
                    }
                    else if (node is JsonArray array) foreach (var item in array) Visit(item);
                }
                foreach (var json in Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly)
                    .Where(p => Path.GetFileName(p) is "default_mod.json" or "meta.json" || Path.GetFileName(p).StartsWith("group_", StringComparison.OrdinalIgnoreCase)))
                {
                    token.ThrowIfCancellationRequested();
                    try { TextureFiles.EnsureLocalPath(json); if (new FileInfo(json).Length <= 16 * 1024 * 1024) Visit(JsonNode.Parse(File.ReadAllText(json))); }
                    catch (Exception e) when (e is IOException or InvalidDataException or JsonException or ArgumentException or UnauthorizedAccessException) { }
                }
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
                foreach (var path in Directory.EnumerateFiles(root, "*.sklb", options))
                {
                    token.ThrowIfCancellationRequested();
                    var gamePaths = mappings.TryGetValue(path, out var paths) ? paths : new HashSet<string> { "" };
                    foreach (var gamePath in gamePaths)
                        result.Add(new(SkeletonSourceKind.Mod, new(gamePath, path, "", directory, root, Path.GetRelativePath(root, path))));
                }
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException) { }
        }
        return result.ToImmutable();
    }

    public static string[] RelevantPaths(AnimationClip clip, IEnumerable<string> loaded)
    {
        var parts = clip.GamePath.Split('/');
        var paths = loaded.Where(p => p.EndsWith(".sklb", StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.Ordinal);
        if (AnimationDependencies.SafeGamePath(clip.SkeletonPath)) paths.Add(clip.SkeletonPath);
        if (parts.Length > 4 && parts[0] == "chara" && parts[1] == "human")
        {
            var race = parts[2]; var face = parts[4].StartsWith('f'); var variant = face ? parts[4] : "b0001";
            paths.Add($"chara/human/{race}/skeleton/{(face ? "face" : "base")}/{variant}/skl_{race}{variant}.sklb");
        }
        return paths.Order(StringComparer.Ordinal).ToArray();
    }
    private async Task<ImmutableArray<SkeletonCandidate>> Describe(Guid collection, SkeletonSource source, CancellationToken token)
    {
        var length = 0L; var write = DateTime.MinValue;
        if (source.Kind == SkeletonSourceKind.Mod)
        {
            var info = new FileInfo(source.Resource.ResolvedPath);
            length = info.Length; write = info.LastWriteTimeUtc;
        }
        if (source.Kind != SkeletonSourceKind.Collection && files.TryGetValue(source, out var cached) && cached.Length == length && cached.Write == write)
            return cached.Candidates;
        var read = await resources.ReadSkeletonAsync(collection, source, token);
        if (!descriptions.TryGetValue(read.Source.Resource.Hash, out var description))
        {
            var path = Path.Combine(CacheDirectory(), "descriptions", read.Source.Resource.Hash + ".json");
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length < 4 * 1024 * 1024)
                {
                    var disk = JsonSerializer.Deserialize<CachedSkeleton>(await File.ReadAllTextAsync(path, token), Json);
                    if (disk is { Version: 2 } && disk.SourceHash == read.Source.Resource.Hash) description = disk.Variants;
                }
                if (!description.IsDefaultOrEmpty)
                    foreach (var variant in description) AnimationSkeleton.Validate(variant.Skeleton);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or JsonException or ArgumentException or UnauthorizedAccessException) { description = default; }
            if (description.IsDefaultOrEmpty)
            {
                description = await framework.RunOnTick(() => { token.ThrowIfCancellationRequested(); return AnimationSkeleton.InspectSources(read.Bytes); }, delayTicks: 1);
                try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new CachedSkeleton(2, read.Source.Resource.Hash, description), Json), token); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.Debug(e, "Could not cache skeleton description."); }
            }
            if (descriptions.Count >= 512) descriptions.Clear();
            descriptions[read.Source.Resource.Hash] = description;
        }
        var candidates = description.Select(v => new SkeletonCandidate(read.Source with { Variant = v.Name }, v.Skeleton, 0, "")).ToImmutableArray();
        if (files.Count >= 4096) files.Clear();
        files[source] = (length, write, candidates);
        return candidates;
    }

    internal static SkeletonResolution Rank(AnimationChannels channels, IEnumerable<SkeletonCandidate> candidates,
        SkeletonDescription? baseline, string expectedPath, string? selectedIdentity = null)
    {
        var parts = expectedPath.Split('/');
        string Family(string path) => string.Join('/', path.Split('/').Take(5));
        bool Fits(SkeletonCandidate candidate)
        {
            try { return AnimationSkeleton.Fits(channels, candidate.Skeleton); }
            catch (InvalidDataException) { return false; }
        }
        ImmutableArray<SkeletonCandidate> RankCandidates(IEnumerable<SkeletonCandidate> available)
        {
            return available.Select(c =>
            {
                var name = !string.IsNullOrEmpty(channels.OriginalSkeleton) && channels.OriginalSkeleton == c.Skeleton.Name ? 1 : 0;
                var provenance = c.Source.Resource.GamePath == expectedPath ? 2 :
                    parts.Length >= 5 && Family(c.Source.Resource.GamePath) == Family(expectedPath) ? 1 : 0;
                var overlap = baseline == null ? 0 : Enumerable.Range(0, Math.Min(baseline.Bones.Length, c.Skeleton.Bones.Length))
                    .Count(i => baseline.Bones[i].Name == c.Skeleton.Bones[i].Name && baseline.Bones[i].Parent == c.Skeleton.Bones[i].Parent);
                var excess = c.Skeleton.Bones.Length - channels.Bones.Length;
                // A mapped reference pose closest to the original game variant is a
                // better source than another race/era embedded in the same SKLB.
                var referenceOverlap = baseline == null || c.Source.Variant.Length == 0 ? 0 :
                    Enumerable.Range(0, Math.Min(baseline.Bones.Length, c.Skeleton.Bones.Length)).Count(i =>
                        baseline.Bones[i].Name == c.Skeleton.Bones[i].Name && baseline.Bones[i].Parent == c.Skeleton.Bones[i].Parent &&
                        !AnimationRetarget.Meaningful(baseline.Bones[i].Reference, c.Skeleton.Bones[i].Reference));
                // Clips that store their own transforms should continue to prefer
                // the main skeleton over newly discovered mapper reference poses.
                var direct = channels.ReferenceBones == null && c.Source.Variant.Length == 0 ? 1 : 0;
                var rank = name * 1000000000000000L + provenance * 10000000000000L + direct * 1000000000000L +
                    overlap * 10000000L + referenceOverlap * 1000L - excess;
                return c with { Rank = rank,
                    Rationale = $"Skeleton name {(name == 1 ? "matches" : "unconfirmed")}; variant match {provenance}; {overlap} ordered bones/parents match; {referenceOverlap} reference poses match; {excess} unbound bones" };
            }).GroupBy(c => c.Skeleton.Fingerprint).Select(g => g.OrderByDescending(c => c.Rank).ThenBy(c => c.Source.Resource.ResolvedPath, StringComparer.Ordinal).First())
                .OrderByDescending(c => c.Rank).ThenBy(c => c.Source.Resource.ResolvedPath, StringComparer.Ordinal).ToImmutableArray();
        }
        var ranked = RankCandidates(candidates.Where(Fits));
        if (ranked.IsEmpty)
            return new(SkeletonResolutionState.Incompatible, [], Reason: channels.ReferenceBones is { } bones
                ? $"No source skeleton, including embedded mapper skeletons, fits the predictive animation ({bones} reference bones, {channels.ReferenceFloats} reference floats). A compatible source reference pose is required before retargeting."
                : "No source skeleton has valid animation track, float, and partition bindings.");
        var chosen = ranked.FirstOrDefault(c => SelectionId(c) == selectedIdentity);
        if (chosen == null && (ranked.Length == 1 || ranked[0].Rank > ranked[1].Rank)) chosen = ranked[0];
        return chosen == null ? new(SkeletonResolutionState.Ambiguous, ranked, Reason: "Several skeletons fit equally well. Choose the source skeleton.") :
            new(SkeletonResolutionState.Matched, ranked, chosen);
    }
    internal static async Task<List<SkeletonCandidate>> ReadCandidatesAsync(IEnumerable<SkeletonSource> sources,
        Func<SkeletonSource, Task<ImmutableArray<SkeletonCandidate>>> read, Action<SkeletonSource, Exception> rejected, CancellationToken token)
    {
        var candidates = new List<SkeletonCandidate>();
        foreach (var source in sources)
        {
            token.ThrowIfCancellationRequested();
            try { candidates.AddRange(await read(source)); }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            { rejected(source, e); }
        }
        return candidates;
    }
    public async Task<SkeletonResolution> ResolveAsync(Guid collection, AnimationClip clip, byte[] pap,
        IEnumerable<string> loaded, string? manual, CancellationToken token)
    {
        if (loadedRevision != Revision) { files.Clear(); loadedRevision = Revision; }
        await GetSessionLibraryBuild(CancellationToken.None).WaitAsync(token);
        var channels = await framework.RunOnTick(() => { token.ThrowIfCancellationRequested(); return AnimationSkeleton.InspectChannels(pap, clip.BindingIndex); }, delayTicks: 1);
        var relevant = RelevantPaths(clip, loaded);
        var parsed = new AnimationPap(pap);
        var expected = clip.SkeletonPath;
        if (parsed.ModelType == 0 && parsed.ModelId > 0)
        {
            var parts = clip.GamePath.Split('/');
            var face = parts.Length > 4 && parts[4].StartsWith('f');
            var variant = face ? parts[4] : "b0001";
            var race = $"c{parsed.ModelId:D4}";
            expected = $"chara/human/{race}/skeleton/{(face ? "face" : "base")}/{variant}/skl_{race}{variant}.sklb";
            relevant = relevant.Append(expected).Distinct(StringComparer.Ordinal).ToArray();
        }
        var sources = relevant.SelectMany(p => new[] { new SkeletonSource(SkeletonSourceKind.Game, new(p, p, "")), new SkeletonSource(SkeletonSourceKind.Collection, new(p, p, "")) }).ToArray();
        var candidates = await ReadCandidatesAsync(sources, source => Describe(collection, source, token),
            (source, error) => log.Debug($"Skeleton candidate {source.Resource.ResolvedPath}: {error.Message}"), token);
        ImmutableArray<SessionSkeleton> library;
        lock (libraryLock) library = sessionLibrary;
        candidates.AddRange(library.Select(entry => entry.CandidateFor(expected)));
        var baseline = candidates.FirstOrDefault(c => c.Source.Kind == SkeletonSourceKind.Game && c.Source.Resource.GamePath == expected && c.Source.Variant.Length == 0)?.Skeleton;
        return Rank(channels, candidates, baseline, expected, manual);
    }
}
