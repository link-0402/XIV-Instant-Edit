// Discovery/ranking adapted from VFXEditor's SkeletonMatcher (GPL-3.0).
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dalamud.Plugin.Services;
using InstantEdit.Models;

namespace InstantEdit.Services.Animations;

internal enum SkeletonLibraryState { NotLoaded, Loading, Building, Ready, Failed }

/// <summary>
/// Where the skeleton library stands, for Settings. Missing and changed count the library's
/// files that were removed or rewritten since it was built; new skeleton mods are only found
/// by a rebuild.
/// </summary>
internal sealed record SkeletonLibraryStatus(SkeletonLibraryState State, int Skeletons = 0, int Files = 0, int Missing = 0,
    int Changed = 0, DateTime? BuiltUtc = null, int Progress = 0, int Total = 0, string? Error = null);

internal sealed class AnimationSkeletonIndex(PenumbraService penumbra, AnimationResources resources, IFramework framework,
    IPluginLog log, Func<string> configuredCacheRoot)
{
    private readonly Dictionary<string, SkeletonDescription> descriptions = [];
    private readonly Dictionary<SkeletonSource, (long Length, DateTime Write, SkeletonCandidate Candidate)> files = [];
    private readonly object libraryLock = new();
    private readonly object cacheLock = new();
    private ImmutableArray<SessionSkeleton> sessionLibrary = [];
    private Task? sessionLibraryBuild;
    private string? cacheDirectory;
    private int revision;
    private int loadedRevision = -1;
    private volatile SkeletonLibraryStatus status = new(SkeletonLibraryState.NotLoaded);
    public int Revision => Volatile.Read(ref revision);
    public SkeletonLibraryStatus Status => status;
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true };
    // Base/body animation skeletons are accepted down this tree. The graph is
    // deliberately limited to the supplied chart; unlisted model IDs retain
    // exact/manual resolution instead of inheriting from an unverified family.
    private static readonly ImmutableDictionary<string, string> BaseParents =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["c1101"] = "c0101", ["c0901"] = "c0101", ["c0701"] = "c0101",
            ["c0501"] = "c0101", ["c1301"] = "c0101", ["c0301"] = "c0101",
            ["c0201"] = "c0101", ["c1201"] = "c1101", ["c1501"] = "c0901",
            ["c0801"] = "c0201", ["c1401"] = "c0201", ["c0601"] = "c0201",
            ["c1801"] = "c0201", ["c1001"] = "c0201", ["c0401"] = "c0201",
        }.ToImmutableDictionary(StringComparer.Ordinal);

    // Descriptions cached as version 2 also held the skeletons that mappers embed in the file,
    // so those files are described again.
    private sealed record CachedSkeleton(int Version, string SourceHash, SkeletonDescription Skeleton);
    private sealed record CachedLibrarySource(SkeletonSource Source, long Length, DateTime Write);
    private sealed record CachedLibraryEntry(SkeletonDescription Skeleton, ImmutableArray<CachedLibrarySource> Sources);
    private sealed record CachedLibrary(int Version, DateTime BuiltUtc, ImmutableArray<CachedLibraryEntry> Skeletons,
        string MappingFingerprint = "");

    internal static string? ModelFromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var parts = path.Replace('\\', '/').Split('/');
        if (parts.Length < 5 || parts[0] != "chara" || parts[1] != "human" ||
            parts[2].Length != 5 || parts[2][0] != 'c' || !parts[2][1..].All(char.IsDigit)) return null;
        var skeletonPath = parts[3] == "skeleton" && (parts[4] is "base" or "face" or "hair" or "met");
        var animationPath = parts[3] == "animation" && parts[4].Length > 0;
        return skeletonPath || animationPath ? parts[2] : null;
    }

    internal static string? ModelCode(ushort modelId)
        => modelId is > 0 and <= 9999 ? $"c{modelId:D4}" : null;

    internal static int ModelDepth(string model)
    {
        var depth = 0;
        var current = model;
        while (BaseParents.TryGetValue(current, out var parent)) { depth++; current = parent; }
        return depth;
    }

    internal static bool IsAncestorOrSelf(string ancestor, string descendant)
    {
        var current = descendant;
        while (true)
        {
            if (current == ancestor) return true;
            if (!BaseParents.TryGetValue(current, out var parent)) return false;
            current = parent;
        }
    }

    internal static ImmutableArray<string> Ancestors(string model)
    {
        var result = ImmutableArray.CreateBuilder<string>();
        var current = model;
        while (true)
        {
            result.Add(current);
            if (!BaseParents.TryGetValue(current, out var parent)) break;
            current = parent;
        }
        return result.ToImmutable();
    }

    internal static ImmutableArray<string> NormalizeAliases(IEnumerable<string>? aliases)
        => (aliases ?? Enumerable.Empty<string>()).Where(AnimationDependencies.SafeGamePath).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToImmutableArray();

    /// <summary>Returns the shallowest model explicitly represented by every mapped base alias.</summary>
    internal static string? CanonicalMappedModel(IEnumerable<string>? aliases)
    {
        var models = NormalizeAliases(aliases).Where(IsChartAlias).Select(ModelFromPath).OfType<string>()
            .Distinct(StringComparer.Ordinal).ToArray();
        return models.Where(candidate => models.All(model => IsAncestorOrSelf(candidate, model)))
            .OrderBy(ModelDepth).ThenBy(model => model, StringComparer.Ordinal).FirstOrDefault();
    }

    private static bool IsChartAlias(string path)
    {
        var parts = path.Replace('\\', '/').Split('/');
        return parts.Length > 4 && ((parts[3] == "skeleton" && parts[4] == "base") ||
            (parts[3] == "animation" && parts[4].StartsWith('a')));
    }

    internal static string MappingFingerprint(IEnumerable<string>? aliases)
    {
        var normalized = NormalizeAliases(aliases);
        return normalized.IsEmpty ? "" : AnimationPap.Hash(Encoding.UTF8.GetBytes(string.Join("\n", normalized)));
    }

    internal static AnimationSourceIdentity SourceIdentity(IEnumerable<string>? aliases, string? papModel)
    {
        var mapped = NormalizeAliases(aliases);
        var mappedModel = CanonicalMappedModel(mapped);
        var normalizedPap = papModel is { Length: 5 } && papModel[0] == 'c' && papModel[1..].All(char.IsDigit) ? papModel : null;
        return new(mapped, mappedModel, normalizedPap, mappedModel ?? normalizedPap, MappingFingerprint(mapped));
    }

    internal static ImmutableArray<string> AliasesFor(IReadOnlyDictionary<string, HashSet<string>>? paths, string resolvedPath)
    {
        if (paths == null || string.IsNullOrWhiteSpace(resolvedPath)) return [];
        return NormalizeAliases(paths.Where(pair => SameResourcePath(pair.Key, resolvedPath)).SelectMany(pair => pair.Value));
    }

    internal static bool SameResourcePath(string left, string right)
        => PathRules.SamePhysicalPath(left, right) ||
           string.Equals(PathRules.NormalizeGamePath(left), PathRules.NormalizeGamePath(right), StringComparison.OrdinalIgnoreCase);

    internal sealed record SessionSkeleton(SkeletonDescription Skeleton, ImmutableArray<SkeletonSource> Sources)
    {
        public SkeletonCandidate CandidateFor(string expectedPath) => CandidateFor(expectedPath, null);

        public SkeletonCandidate CandidateFor(string expectedPath, AnimationSourceIdentity? identity)
        {
            var source = Sources.OrderByDescending(s => identity != null && identity.MappedGamePaths.Any(p =>
                    s.MappedGamePaths.Contains(p, StringComparer.OrdinalIgnoreCase)))
                .ThenByDescending(s => identity?.CanonicalModel != null && s.CanonicalModel == identity.CanonicalModel)
                .ThenBy(s => s.CanonicalModel == null ? int.MaxValue : ModelDepth(s.CanonicalModel))
                .ThenByDescending(s => string.Equals(s.Resource.GamePath, expectedPath, StringComparison.Ordinal))
                .ThenByDescending(s => s.Resource.GamePath.Length).ThenBy(s => s.Resource.ResolvedPath, StringComparer.Ordinal).First();
            return new(source, Skeleton, 0, "");
        }
    }
    internal static string SelectionId(SkeletonCandidate candidate) => candidate.Skeleton.Fingerprint + ":" + candidate.Source.Resource.Hash + ":" + candidate.Source.MappingFingerprint;

    internal static ImmutableArray<SessionSkeleton> DeduplicateLibrary(IEnumerable<SkeletonCandidate> candidates) => candidates
        .GroupBy(candidate => candidate.Skeleton.Fingerprint, StringComparer.Ordinal)
        .Select(group => new SessionSkeleton(group.First().Skeleton,
            group.Select(candidate => candidate.Source).Distinct().OrderBy(source => source.Resource.ResolvedPath, StringComparer.Ordinal).ToImmutableArray()))
        .OrderBy(entry => entry.Skeleton.Fingerprint, StringComparer.Ordinal).ToImmutableArray();

    private string CacheDirectory()
    {
        lock (cacheLock) return cacheDirectory ??= Path.Combine(configuredCacheRoot(), "skeleton-library");
    }

    private string LibraryPath() => Path.Combine(CacheDirectory(), "session-library.json");

    /// <summary>When the saved library was written, or null when none is saved in the cache folder.</summary>
    public DateTime? SavedLibraryUtc()
    {
        try
        {
            var path = LibraryPath();
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Loads the skeleton library saved in the cache folder, or builds and saves it when none was
    /// saved yet. A saved library is used as it is; only <see cref="RebuildAsync"/> builds it again.
    /// </summary>
    public void StartSessionLibrary(CancellationToken lifetimeToken)
    {
        var build = GetSessionLibraryBuild(lifetimeToken);
        _ = build.ContinueWith(task => log.Warning(task.Exception, "Could not load or build the skeleton library."),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    /// <summary>
    /// Builds the library again from every skeleton in the installed mods and saves it, for
    /// example after installing, updating or removing skeleton mods. The current library stays
    /// in use until the new one is complete. While a load or build runs, returns that instead.
    /// </summary>
    public Task RebuildAsync(CancellationToken token)
    {
        lock (libraryLock)
        {
            if (sessionLibraryBuild is { IsCompleted: false } running) return running;
            return sessionLibraryBuild = BuildSessionLibraryAsync(token, true);
        }
    }

    private Task GetSessionLibraryBuild(CancellationToken lifetimeToken)
    {
        lock (libraryLock)
        {
            if (sessionLibraryBuild is { IsCanceled: true } or { IsFaulted: true }) sessionLibraryBuild = null;
            return sessionLibraryBuild ??= LoadOrBuildSessionLibraryAsync(lifetimeToken);
        }
    }

    private async Task LoadOrBuildSessionLibraryAsync(CancellationToken token)
    {
        status = new(SkeletonLibraryState.Loading);
        if (await LoadSessionLibraryAsync(token) is { } loaded)
        {
            lock (libraryLock) sessionLibrary = loaded.Library;
            status = new(SkeletonLibraryState.Ready, loaded.Library.Length, loaded.Library.Sum(entry => entry.Sources.Length),
                loaded.Missing, loaded.Changed, loaded.BuiltUtc);
            log.Debug($"Loaded the saved skeleton library with {loaded.Library.Length} unique skeletons.");
            return;
        }
        // Nothing saved yet, or the saved file is unreadable: build it this once.
        await BuildSessionLibraryAsync(token, false);
    }

    private sealed record LoadedLibrary(ImmutableArray<SessionSkeleton> Library, int Missing, int Changed, DateTime BuiltUtc);

    private async Task<LoadedLibrary?> LoadSessionLibraryAsync(CancellationToken token)
    {
        try
        {
            var path = LibraryPath();
            if (!File.Exists(path) || new FileInfo(path).Length > 64 * 1024 * 1024) return null;
            var json = await File.ReadAllTextAsync(path, token);
            return await Task.Run(() => TryLoadSessionLibraryJson(json, FileState, out var library, out var missing, out var changed, out var built)
                ? new LoadedLibrary(library, missing, changed, built)
                : null, token);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidDataException)
        {
            log.Debug(e, "Could not load the saved skeleton library.");
            return null;
        }
    }

    private static (bool Exists, long Length, DateTime Write) FileState(string resolvedPath)
    {
        if (!File.Exists(resolvedPath)) return (false, 0, DateTime.MinValue);
        var info = new FileInfo(resolvedPath);
        return (true, info.Length, info.LastWriteTimeUtc);
    }

    /// <summary>
    /// Reads a saved library. It stays authoritative until it is rebuilt: files removed since
    /// the build are left out and counted as missing, rewritten files are kept and counted as changed.
    /// </summary>
    internal static bool TryLoadSessionLibraryJson(string json,
        Func<string, (bool Exists, long Length, DateTime Write)> fileState,
        out ImmutableArray<SessionSkeleton> library, out int missing, out int changed, out DateTime builtUtc)
    {
        library = []; missing = 0; changed = 0; builtUtc = DateTime.MinValue;
        try
        {
            var root = JsonNode.Parse(json);
            // Libraries saved before only main skeletons were read also list the skeletons
            // that mappers embed in skeleton files, each source named by its variant. Those
            // are left out, and so is an entry left without sources.
            if (root is JsonObject saved && saved["Skeletons"] is JsonArray entries)
                foreach (var entry in entries)
                    if (entry is JsonObject item && item["Sources"] is JsonArray sources)
                        foreach (var embedded in sources.Where(EmbeddedVariant).ToArray())
                            sources.Remove(embedded);
            var cached = root.Deserialize<CachedLibrary>(Json);
            if (cached is not { Version: 3 } || cached.Skeletons.IsDefault || cached.Skeletons.Length > 4096)
                return false;
            var result = ImmutableArray.CreateBuilder<SessionSkeleton>(cached.Skeletons.Length);
            foreach (var entry in cached.Skeletons)
            {
                AnimationSkeleton.Validate(entry.Skeleton);
                if (entry.Sources.IsDefault || entry.Sources.Length > 128) return false;
                var sources = ImmutableArray.CreateBuilder<SkeletonSource>(entry.Sources.Length);
                foreach (var item in entry.Sources)
                {
                    if (item.Source.Kind != SkeletonSourceKind.Mod) return false;
                    var state = fileState(item.Source.Resource.ResolvedPath);
                    if (!state.Exists) { missing++; continue; }
                    if (state.Length != item.Length || state.Write != item.Write) changed++;
                    sources.Add(item.Source);
                }
                if (sources.Count > 0) result.Add(new(entry.Skeleton, sources.ToImmutable()));
            }
            library = result.ToImmutable();
            builtUtc = cached.BuiltUtc;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidDataException or NotSupportedException or NullReferenceException)
        {
            missing = 0; changed = 0;
            return false;
        }

        static bool EmbeddedVariant(JsonNode? item)
            => item is JsonObject saved && saved["Source"] is JsonObject source && source["Variant"] is JsonValue variant &&
               variant.TryGetValue<string>(out var name) && name.Length > 0;
    }

    /// <summary>Builds and saves the library. A rebuild moves the revision on, so earlier matches are made again.</summary>
    private async Task BuildSessionLibraryAsync(CancellationToken token, bool rebuild)
    {
        var previous = status;
        try
        {
            status = new(SkeletonLibraryState.Building);
            var roots = await penumbra.AnimationSkeletonRootsAsync();
            var sources = await Task.Run(() => Scan(roots, token), token);
            var progress = 0;
            status = new(SkeletonLibraryState.Building, Total: sources.Length);
            // One skeleton at a time, so the progress count is simply incremented.
            var candidates = await ReadCandidatesAsync(sources, async source =>
                {
                    try { return await Describe(Guid.Empty, source, null, token); }
                    finally { status = status with { Progress = ++progress }; }
                },
                (source, error) => log.Debug($"Skeleton library candidate {source.Resource.ResolvedPath}: {error.Message}"), token);
            var library = DeduplicateLibrary(candidates);
            var built = DateTime.UtcNow;
            var saveError = await SaveLibraryAsync(library, built, token);
            lock (libraryLock) sessionLibrary = library;
            if (rebuild) Interlocked.Increment(ref revision);
            status = new(SkeletonLibraryState.Ready, library.Length, library.Sum(entry => entry.Sources.Length), BuiltUtc: built, Error: saveError);
            log.Debug($"Built the skeleton library with {library.Length} unique skeletons from {sources.Length} registered files.");
        }
        catch (OperationCanceledException)
        {
            status = previous;
            throw;
        }
        catch (Exception e)
        {
            status = previous with { State = SkeletonLibraryState.Failed, Error = e.Message, Progress = 0, Total = 0 };
            throw;
        }
    }

    /// <summary>Saves the library to the cache folder. Returns why it could not be saved, or null.</summary>
    private async Task<string?> SaveLibraryAsync(ImmutableArray<SessionSkeleton> library, DateTime built, CancellationToken token)
    {
        try
        {
            var path = LibraryPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            var entries = library.Select(entry => new CachedLibraryEntry(entry.Skeleton,
                entry.Sources.Select(source =>
                {
                    var info = new FileInfo(source.Resource.ResolvedPath);
                    return new CachedLibrarySource(source, info.Exists ? info.Length : -1,
                        info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue);
                }).ToImmutableArray())).ToImmutableArray();
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new CachedLibrary(3, built, entries), Json), token);
            File.Move(temporary, path, true);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            log.Warning(e, "Could not save the skeleton library to the cache folder.");
            return "It could not be saved to the cache folder, so it is built again next session: " + e.Message;
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
                    var gamePaths = mappings.TryGetValue(path, out var paths)
                        ? NormalizeAliases(paths)
                        : [];
                    var canonical = CanonicalMappedModel(gamePaths);
                    var gamePath = gamePaths.FirstOrDefault(p => ModelFromPath(p) == canonical) ?? gamePaths.FirstOrDefault() ?? "";
                    result.Add(new(SkeletonSourceKind.Mod,
                        new(gamePath, path, "", directory, root, Path.GetRelativePath(root, path)),
                        gamePaths, canonical, MappingFingerprint(gamePaths)));
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
        var normalizedSkeleton = clip.SkeletonPath.Replace('\\', '/');
        var targetModel = ModelFromPath(normalizedSkeleton);
        if (targetModel != null && normalizedSkeleton.Contains("/skeleton/base/", StringComparison.OrdinalIgnoreCase) &&
            normalizedSkeleton.Split('/').Length > 5)
        {
            var skeletonParts = normalizedSkeleton.Split('/');
            var variant = skeletonParts[5];
            foreach (var ancestor in Ancestors(targetModel).Skip(1))
                paths.Add($"chara/human/{ancestor}/skeleton/base/{variant}/skl_{ancestor}{variant}.sklb");
        }
        return paths.Order(StringComparer.Ordinal).ToArray();
    }
    /// <summary>A skeleton file's main skeleton, as a candidate from that file.</summary>
    private async Task<SkeletonCandidate> Describe(Guid collection, SkeletonSource source,
        IReadOnlyDictionary<string, HashSet<string>>? paths, CancellationToken token)
    {
        var length = 0L; var write = DateTime.MinValue;
        if (source.Kind == SkeletonSourceKind.Mod)
        {
            var info = new FileInfo(source.Resource.ResolvedPath);
            length = info.Length; write = info.LastWriteTimeUtc;
        }
        if (source.Kind != SkeletonSourceKind.Collection && files.TryGetValue(source, out var cached) && cached.Length == length && cached.Write == write)
            return cached.Candidate;
        var read = await resources.ReadSkeletonAsync(collection, source, token);
        if (!descriptions.TryGetValue(read.Source.Resource.Hash, out var description))
        {
            var path = Path.Combine(CacheDirectory(), "descriptions", read.Source.Resource.Hash + ".json");
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length < 4 * 1024 * 1024)
                {
                    var disk = JsonSerializer.Deserialize<CachedSkeleton>(await File.ReadAllTextAsync(path, token), Json);
                    if (disk is { Version: 3, Skeleton: { } saved } && disk.SourceHash == read.Source.Resource.Hash)
                    {
                        AnimationSkeleton.Validate(saved);
                        description = saved;
                    }
                }
            }
            catch (Exception e) when (e is IOException or InvalidDataException or JsonException or ArgumentException or UnauthorizedAccessException) { description = null; }
            if (description == null)
            {
                description = await framework.RunOnTick(() => { token.ThrowIfCancellationRequested(); return AnimationSkeleton.Inspect(read.Bytes); }, delayTicks: 1);
                try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new CachedSkeleton(3, read.Source.Resource.Hash, description), Json), token); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.Debug(e, "Could not cache skeleton description."); }
            }
            if (descriptions.Count >= 512) descriptions.Clear();
            descriptions[read.Source.Resource.Hash] = description;
        }
        var described = read.Source;
        if (source.Kind == SkeletonSourceKind.Collection)
        {
            var aliases = AliasesFor(paths, described.Resource.ResolvedPath);
            var mappedModel = CanonicalMappedModel(aliases);
            described = described with
            {
                MappedGamePaths = aliases,
                CanonicalModel = mappedModel ?? ModelFromPath(described.Resource.GamePath),
                MappingFingerprint = MappingFingerprint(aliases),
            };
        }
        var candidate = new SkeletonCandidate(described, description, 0, "");
        if (files.Count >= 4096) files.Clear();
        files[source] = (length, write, candidate);
        return candidate;
    }

    internal static SkeletonResolution Rank(AnimationChannels channels, IEnumerable<SkeletonCandidate> candidates,
        SkeletonDescription? baseline, string expectedPath, string? selectedIdentity = null,
        AnimationSourceIdentity? sourceIdentity = null)
    {
        var normalizedExpected = expectedPath.Replace('\\', '/');
        var parts = normalizedExpected.Split('/');
        var targetModel = ModelFromPath(normalizedExpected);
        var mappedModel = sourceIdentity?.MappedModel;
        var papModel = sourceIdentity?.PapModel;
        var exactModel = normalizedExpected.Contains("/skeleton/", StringComparison.OrdinalIgnoreCase) &&
            !normalizedExpected.Contains("/skeleton/base/", StringComparison.OrdinalIgnoreCase);
        var exactReferenceModel = channels.ExactReferenceModel ? papModel ?? targetModel : null;
        string Family(string path) => string.Join('/', path.Split('/').Take(5));
        int ModelScore(SkeletonCandidate candidate)
        {
            var model = candidate.Source.CanonicalModel ?? ModelFromPath(candidate.Source.Resource.GamePath);
            if (model == null) return 0;
            // Quantized animation data is encoded against one complete reference
            // skeleton. Its PAP model is authoritative: chart ancestors are
            // different reference poses.
            if (exactReferenceModel != null) return model == exactReferenceModel ? 600 : 0;
            // Facial and other specialized paths do not inherit through the
            // body chart. Keep their race/variant identity exact.
            if (exactModel)
            {
                if (targetModel != null && model == targetModel) return 500;
                if (targetModel == null && papModel != null && model == papModel) return 400;
                return 0;
            }
            if (mappedModel != null)
            {
                if (model == mappedModel) return 400;
                if (IsAncestorOrSelf(model, mappedModel)) return 300 - ModelDepth(model);
                if (IsAncestorOrSelf(mappedModel, model)) return 100 - ModelDepth(model);
                return 0;
            }
            if (papModel != null)
            {
                if (model == papModel) return 300;
                if (IsAncestorOrSelf(model, papModel)) return 200 - ModelDepth(model);
                if (IsAncestorOrSelf(papModel, model)) return 100 - ModelDepth(model);
                return 0;
            }
            // With no source metadata, choose the broadest compatible chart
            // ancestor of the live target rather than blindly copying its race.
            if (targetModel != null && IsAncestorOrSelf(model, targetModel)) return 200 - ModelDepth(model);
            return 0;
        }
        bool Fits(SkeletonCandidate candidate)
        {
            try { return AnimationSkeleton.Fits(channels, candidate.Skeleton); }
            catch (InvalidDataException) { return false; }
        }
        ImmutableArray<SkeletonCandidate> RankCandidates(IEnumerable<SkeletonCandidate> available)
        {
            return available.Select(c =>
            {
                var model = c.Source.CanonicalModel ?? ModelFromPath(c.Source.Resource.GamePath);
                var modelScore = ModelScore(c);
                var name = !string.IsNullOrEmpty(channels.OriginalSkeleton) && channels.OriginalSkeleton == c.Skeleton.Name ? 1 : 0;
                var candidatePath = c.Source.Resource.GamePath.Replace('\\', '/');
                var provenance = candidatePath == normalizedExpected ? 2 :
                    parts.Length >= 5 && Family(candidatePath) == Family(normalizedExpected) ? 1 : 0;
                var overlap = baseline == null ? 0 : Enumerable.Range(0, Math.Min(baseline.Bones.Length, c.Skeleton.Bones.Length))
                    .Count(i => baseline.Bones[i].Name == c.Skeleton.Bones[i].Name && baseline.Bones[i].Parent == c.Skeleton.Bones[i].Parent);
                var bound = channels.Bones.ToHashSet();
                var referenceOnly = c.Skeleton.Bones.Select((bone, index) => (bone.Name, index))
                    .Where(bone => !bound.Contains((short)bone.index)).Select(bone => bone.Name).ToArray();
                var referenceOnlyText = referenceOnly.Length == 0 ? "0 reference-only bones" : referenceOnly.Length <= 4
                    ? $"{referenceOnly.Length} reference-only bone{(referenceOnly.Length == 1 ? "" : "s")} ({string.Join(", ", referenceOnly)})"
                    : $"{referenceOnly.Length} reference-only bones";
                // Among first bones that fit equally well, those of the character's own skeleton
                // file are the ones the animation plays on.
                var live = c.Source.LeadingBones > 0 && c.Source.Kind == SkeletonSourceKind.Collection && provenance == 2 ? 1 : 0;
                var rank = modelScore * 10000000000000000L + name * 1000000000000000L + provenance * 10000000000000L +
                    overlap * 10000000L + live * 100000L - referenceOnly.Length;
                var leadingText = c.Source.LeadingBones > 0 ? $"; only its first {c.Source.LeadingBones} bones, as no whole skeleton fits" : "";
                return c with { Rank = rank,
                    Rationale = $"Source model {(model ?? "unconfirmed")} (score {modelScore}); skeleton name {(name == 1 ? "matches" : "unconfirmed")}; variant match {provenance}; {overlap} ordered bones/parents match; {referenceOnlyText}{leadingText}" };
            }).GroupBy(c => c.Skeleton.Fingerprint).Select(g => g.OrderByDescending(c => c.Rank).ThenBy(c => c.Source.Resource.ResolvedPath, StringComparer.Ordinal).First())
                .OrderByDescending(c => c.Rank).ThenBy(c => c.Source.Resource.ResolvedPath, StringComparer.Ordinal).ToImmutableArray();
        }
        var fitting = candidates.Where(Fits);
        if (exactReferenceModel != null)
            fitting = fitting.Where(candidate =>
                (candidate.Source.CanonicalModel ?? ModelFromPath(candidate.Source.Resource.GamePath)) == exactReferenceModel);
        var ranked = RankCandidates(fitting);
        // A predictive animation made before a skeleton mod appended bones declares fewer
        // reference bones than the mod's skeletons now have. When no whole skeleton fits,
        // the first bones of a longer one are the skeleton it was made for. Quantized data
        // stays with its exact model's whole skeleton.
        if (ranked.IsEmpty && channels is { ExactReferenceModel: false, ReferenceBones: { } leading })
            ranked = RankCandidates(candidates.Select(c => Leading(c, leading)).OfType<SkeletonCandidate>().Where(Fits));
        if (ranked.IsEmpty)
            return new(SkeletonResolutionState.Incompatible, [], Reason: channels.ReferenceBones is { } bones
                ? channels.ExactReferenceModel
                    ? $"The quantized animation requires the main {exactReferenceModel ?? "PAP"} skeleton ({bones} reference bones, {channels.ReferenceFloats} reference floats); no exact source was found."
                    : $"No source skeleton fits the predictive animation ({bones} reference bones, {channels.ReferenceFloats} reference floats). A compatible source reference pose is required before retargeting."
                : "No source skeleton has valid animation track, float, and partition bindings.");
        var chosen = ranked.FirstOrDefault(c => SelectionId(c) == selectedIdentity);
        if (chosen == null && (ranked.Length == 1 || ranked[0].Rank > ranked[1].Rank)) chosen = ranked[0];
        return chosen == null ? new(SkeletonResolutionState.Ambiguous, ranked, Reason: "Several skeletons fit equally well. Choose the source skeleton.") :
            new(SkeletonResolutionState.Matched, ranked, chosen);
    }

    /// <summary>A whole skeleton's first bones as a source of their own, or null when it has no more bones than that.</summary>
    private static SkeletonCandidate? Leading(SkeletonCandidate candidate, int bones)
    {
        if (candidate.Source.LeadingBones != 0) return null;
        try
        {
            return AnimationSkeleton.Leading(candidate.Skeleton, bones) is { } leading
                ? candidate with { Source = candidate.Source with { LeadingBones = bones }, Skeleton = leading }
                : null;
        }
        catch (InvalidDataException) { return null; } // A malformed skeleton must not stop matching the next.
    }
    internal static async Task<List<SkeletonCandidate>> ReadCandidatesAsync(IEnumerable<SkeletonSource> sources,
        Func<SkeletonSource, Task<SkeletonCandidate>> read, Action<SkeletonSource, Exception> rejected, CancellationToken token)
    {
        var candidates = new List<SkeletonCandidate>();
        foreach (var source in sources)
        {
            token.ThrowIfCancellationRequested();
            try { candidates.Add(await read(source)); }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            { rejected(source, e); }
        }
        return candidates;
    }

    // Compatibility overload for callers and journals created before the
    // Penumbra alias map became part of source resolution.
    public Task<SkeletonResolution> ResolveAsync(Guid collection, AnimationClip clip, byte[] pap,
        IEnumerable<string> loaded, string? manual, CancellationToken token)
        => ResolveAsync(collection, clip, pap, loaded, null, manual, token);

    public async Task<SkeletonResolution> ResolveAsync(Guid collection, AnimationClip clip, byte[] pap,
        IEnumerable<string> loaded, IReadOnlyDictionary<string, HashSet<string>>? pathMap,
        string? manual, CancellationToken token)
    {
        if (loadedRevision != Revision) { files.Clear(); loadedRevision = Revision; }
        await GetSessionLibraryBuild(CancellationToken.None).WaitAsync(token);
        var channels = await framework.RunOnTick(() => { token.ThrowIfCancellationRequested(); return AnimationSkeleton.InspectChannels(pap, clip.BindingIndex); }, delayTicks: 1);
        var relevant = RelevantPaths(clip, loaded);
        var parsed = new AnimationPap(pap);
        var expected = clip.SkeletonPath;
        var papModel = parsed.ModelType == 0 ? ModelCode(parsed.ModelId) : null;
        var sourceIdentity = clip.SourceIdentity ?? SourceIdentity([], papModel);
        if (sourceIdentity.PapModel == null && papModel != null)
            sourceIdentity = sourceIdentity with { PapModel = papModel, CanonicalModel = sourceIdentity.MappedModel ?? papModel };
        // PAP model metadata helps identify a source, but the captured live
        // skeleton remains the retargeting destination. Only use PAP metadata
        // to discover a target when the runtime did not provide one.
        if (string.IsNullOrWhiteSpace(expected) && parsed.ModelType == 0 && parsed.ModelId > 0)
        {
            var parts = clip.GamePath.Split('/');
            var face = parts.Length > 4 && parts[4].StartsWith('f');
            var variant = face ? parts[4] : "b0001";
            var race = $"c{parsed.ModelId:D4}";
            expected = $"chara/human/{race}/skeleton/{(face ? "face" : "base")}/{variant}/skl_{race}{variant}.sklb";
            relevant = relevant.Append(expected).Distinct(StringComparer.Ordinal).ToArray();
        }
        var sources = relevant.SelectMany(p => new[]
        {
            new SkeletonSource(SkeletonSourceKind.Game, new(p, p, ""), [], ModelFromPath(p), ""),
            new SkeletonSource(SkeletonSourceKind.Collection, new(p, p, ""), [], ModelFromPath(p), ""),
        }).ToArray();
        var candidates = await ReadCandidatesAsync(sources, source => Describe(collection, source, pathMap, token),
            (source, error) => log.Debug($"Skeleton candidate {source.Resource.ResolvedPath}: {error.Message}"), token);
        ImmutableArray<SessionSkeleton> library;
        lock (libraryLock) library = sessionLibrary;
        candidates.AddRange(library.Select(entry => entry.CandidateFor(expected, sourceIdentity)));
        var baseline = candidates.FirstOrDefault(c => c.Source.Kind == SkeletonSourceKind.Game && c.Source.Resource.GamePath == expected)?.Skeleton;
        // Repair targets: the live path's own skeleton files, and every library file mapped to it.
        var mapped = candidates.Where(c => c.Source.Kind == SkeletonSourceKind.Collection).Concat(library.SelectMany(entry =>
            entry.Sources.Select(s => new SkeletonCandidate(s, entry.Skeleton, 0, ""))));
        // Keep which bones the clip animates; the selected source names them.
        return Rank(channels, candidates, baseline, expected, manual, sourceIdentity) with
            { TrackBones = channels.Bones, Standards = Standards(baseline, mapped, expected) };
    }

    /// <summary>
    /// The standard skeletons for a model path: the game's own, then the best IVCS and
    /// IVCS + YAS layouts among the main skeletons of files mapped to that path. Among
    /// several, the one keeping the most game bones at the game's indices wins, then the one
    /// with the most game bones, then the one whose game bones rest most like the game's, then
    /// the one the most files share, then the first by file path. None without the game's own
    /// skeleton.
    /// </summary>
    internal static ImmutableArray<StandardSkeleton> Standards(SkeletonDescription? game, IEnumerable<SkeletonCandidate> mapped, string expectedPath)
    {
        if (game == null) return [];
        var reference = game.Bones.ToDictionary(b => b.Name, b => b.Reference, StringComparer.Ordinal);
        int Leading(SkeletonDescription s) => s.Bones.Zip(game.Bones).TakeWhile(pair => pair.First.Name == pair.Second.Name).Count();
        var usable = mapped.Where(c => SameGamePath(c.Source.Resource.GamePath, expectedPath))
            .GroupBy(c => c.Skeleton.Fingerprint, StringComparer.Ordinal)
            .Select(g => (g.First().Skeleton, Sources: g.Select(c => c.Source.Resource).OrderBy(r => r.ResolvedPath, StringComparer.Ordinal).ToArray()))
            .ToArray();
        var result = ImmutableArray.CreateBuilder<StandardSkeleton>();
        result.Add(new(SkeletonStandard.Vanilla, game, "Game data"));
        foreach (var standard in new[] { SkeletonStandard.Ivcs, SkeletonStandard.IvcsYas })
        {
            var best = usable.Where(c => AnimationBones.IsStandardLayout(c.Skeleton, game, standard))
                .OrderByDescending(c => Leading(c.Skeleton))
                .ThenByDescending(c => c.Skeleton.Bones.Count(b => reference.ContainsKey(b.Name)))
                .ThenByDescending(c => c.Skeleton.Bones.Count(b => reference.TryGetValue(b.Name, out var rest) && !AnimationRetarget.Meaningful(b.Reference, rest)))
                .ThenByDescending(c => c.Sources.Length)
                .ThenBy(c => c.Sources[0].ResolvedPath, StringComparer.Ordinal).FirstOrDefault();
            if (best.Skeleton != null)
                result.Add(new(standard, best.Skeleton, Origin(best.Sources)));
        }
        return result.ToImmutable();
    }

    /// <summary>
    /// Names the mods a skeleton comes from, a few at most. Characters downloaded by sync
    /// plugins, whose folders are named Name@World, come after the mods themselves.
    /// </summary>
    private static string Origin(IEnumerable<AnimationResource> sources)
    {
        var names = sources.Select(r => r.ModName ?? r.ModDirectory ?? Path.GetFileName(r.ResolvedPath)).Distinct(StringComparer.Ordinal)
            .OrderBy(name => name.Contains('@')).ThenBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        return names.Length <= 3 ? string.Join(", ", names) : $"{string.Join(", ", names.Take(3))} and {names.Length - 3} more";
    }

    private static bool SameGamePath(string left, string right)
        => string.Equals(left.Replace('\\', '/'), right.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
}
