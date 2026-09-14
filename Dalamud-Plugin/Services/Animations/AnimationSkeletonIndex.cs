// Discovery/ranking adapted from VFXEditor's SkeletonMatcher (GPL-3.0).
using System.Collections.Immutable;
using System.Text;
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

    private sealed record CachedSkeleton(int Version, string SourceHash, ImmutableArray<AnimationSkeleton.Variant> Variants);
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
            return sessionLibraryBuild ??= LoadOrBuildSessionLibraryAsync(lifetimeToken);
        }
    }

    private async Task LoadOrBuildSessionLibraryAsync(CancellationToken token)
    {
        var roots = await penumbra.AnimationSkeletonRootsAsync();
        var mappingFingerprint = await Task.Run(() => ModMappingFingerprint(roots, token), token);
        var loaded = await LoadSessionLibraryAsync(token, mappingFingerprint);
        if (loaded is { } library)
        {
            lock (libraryLock) sessionLibrary = library;
            log.Debug($"Loaded cached session skeleton library with {library.Length} unique skeletons.");
            return;
        }
        await BuildSessionLibraryAsync(roots, mappingFingerprint, token);
    }

    private async Task<ImmutableArray<SessionSkeleton>?> LoadSessionLibraryAsync(CancellationToken token, string mappingFingerprint)
    {
        try
        {
            var path = Path.Combine(CacheDirectory(), "session-library.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 64 * 1024 * 1024) return null;
            return TryLoadSessionLibraryJson(await File.ReadAllTextAsync(path, token), resolvedPath =>
            {
                if (!File.Exists(resolvedPath)) return (false, 0, DateTime.MinValue);
                var info = new FileInfo(resolvedPath);
                return (true, info.Length, info.LastWriteTimeUtc);
            }, out var library, mappingFingerprint) ? library : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidDataException)
        {
            log.Debug(e, "Could not load the cached session skeleton library.");
            return null;
        }
    }

    internal static bool TryLoadSessionLibraryJson(string json,
        Func<string, (bool Exists, long Length, DateTime Write)> fileState,
        out ImmutableArray<SessionSkeleton> library, string? mappingFingerprint = null)
    {
        library = [];
        try
        {
            var cached = JsonSerializer.Deserialize<CachedLibrary>(json, Json);
            if (cached is not { Version: 3 } || cached.Skeletons.IsDefault || cached.Skeletons.Length > 4096)
                return false;
            if (mappingFingerprint != null && !string.Equals(cached.MappingFingerprint, mappingFingerprint, StringComparison.Ordinal))
                return false;
            var result = ImmutableArray.CreateBuilder<SessionSkeleton>(cached.Skeletons.Length);
            foreach (var entry in cached.Skeletons)
            {
                AnimationSkeleton.Validate(entry.Skeleton);
                if (entry.Sources.IsDefaultOrEmpty || entry.Sources.Length > 128) return false;
                var sources = ImmutableArray.CreateBuilder<SkeletonSource>(entry.Sources.Length);
                foreach (var item in entry.Sources)
                {
                    var state = fileState(item.Source.Resource.ResolvedPath);
                    if (item.Source.Kind != SkeletonSourceKind.Mod || !state.Exists || state.Length != item.Length || state.Write != item.Write)
                        return false;
                    sources.Add(item.Source);
                }
                result.Add(new(entry.Skeleton, sources.ToImmutable()));
            }
            library = result.ToImmutable();
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidDataException or NotSupportedException or NullReferenceException)
        {
            return false;
        }
    }

    private async Task BuildSessionLibraryAsync(IEnumerable<(string Directory, string Root)> roots,
        string mappingFingerprint, CancellationToken token)
    {
        var sources = await Task.Run(() => Scan(roots, token), token);
        var candidates = await ReadCandidatesAsync(sources, source => Describe(Guid.Empty, source, null, token),
            (source, error) => log.Debug($"Skeleton library candidate {source.Resource.ResolvedPath}: {error.Message}"), token);
        var library = DeduplicateLibrary(candidates);
        lock (libraryLock) sessionLibrary = library;
        await SaveLibraryAsync(library, mappingFingerprint, token);
        log.Debug($"Built session skeleton library with {library.Length} unique skeletons from {sources.Length} registered files.");
    }

    private async Task SaveLibraryAsync(ImmutableArray<SessionSkeleton> library, string mappingFingerprint, CancellationToken token)
    {
        try
        {
            var directory = CacheDirectory();
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "session-library.json");
            var temporary = path + ".tmp";
            var entries = library.Select(entry => new CachedLibraryEntry(entry.Skeleton,
                entry.Sources.Select(source =>
                {
                    var info = new FileInfo(source.Resource.ResolvedPath);
                    return new CachedLibrarySource(source, info.Exists ? info.Length : -1,
                        info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue);
                }).ToImmutableArray())).ToImmutableArray();
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new CachedLibrary(3, DateTime.UtcNow, entries, mappingFingerprint), Json), token);
            File.Move(temporary, path, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            log.Debug(e, "Could not cache the session skeleton library.");
        }
    }

    internal static string ModMappingFingerprint(IEnumerable<(string Directory, string Root)> roots, CancellationToken token)
    {
        var builder = new StringBuilder();
        foreach (var (directory, root) in roots.OrderBy(item => item.Directory, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.Root, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (var json in Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly)
                             .Where(p => Path.GetFileName(p) is "default_mod.json" or "meta.json" ||
                                         Path.GetFileName(p).StartsWith("group_", StringComparison.OrdinalIgnoreCase))
                             .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    token.ThrowIfCancellationRequested();
                    var info = new FileInfo(json);
                    builder.Append(directory).Append('\0').Append(Path.GetFileName(json)).Append('\0')
                        .Append(info.Length).Append('\0').Append(info.LastWriteTimeUtc.Ticks).Append('\0');
                    if (info.Length <= 16 * 1024 * 1024)
                        builder.Append(AnimationPap.Hash(File.ReadAllBytes(json)));
                    builder.Append('\0');
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return AnimationPap.Hash(Encoding.UTF8.GetBytes(builder.ToString()));
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
                        new(gamePath, path, "", directory, root, Path.GetRelativePath(root, path)), "",
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
    private async Task<ImmutableArray<SkeletonCandidate>> Describe(Guid collection, SkeletonSource source,
        IReadOnlyDictionary<string, HashSet<string>>? paths, CancellationToken token)
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
        var candidates = description.Select(v => new SkeletonCandidate(described with { Variant = v.Name }, v.Skeleton, 0, "")).ToImmutableArray();
        if (files.Count >= 4096) files.Clear();
        files[source] = (length, write, candidates);
        return candidates;
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
        string Family(string path) => string.Join('/', path.Split('/').Take(5));
        int ModelScore(SkeletonCandidate candidate)
        {
            var model = candidate.Source.CanonicalModel ?? ModelFromPath(candidate.Source.Resource.GamePath);
            if (model == null) return 0;
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
                var rank = modelScore * 10000000000000000L + name * 1000000000000000L + provenance * 10000000000000L + direct * 1000000000000L +
                    overlap * 10000000L + referenceOverlap * 1000L - excess;
                return c with { Rank = rank,
                    Rationale = $"Source model {(model ?? "unconfirmed")} (score {modelScore}); skeleton name {(name == 1 ? "matches" : "unconfirmed")}; variant match {provenance}; {overlap} ordered bones/parents match; {referenceOverlap} reference poses match; {excess} unbound bones" };
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
            new SkeletonSource(SkeletonSourceKind.Game, new(p, p, ""), "", [], ModelFromPath(p), ""),
            new SkeletonSource(SkeletonSourceKind.Collection, new(p, p, ""), "", [], ModelFromPath(p), ""),
        }).ToArray();
        var candidates = await ReadCandidatesAsync(sources, source => Describe(collection, source, pathMap, token),
            (source, error) => log.Debug($"Skeleton candidate {source.Resource.ResolvedPath}: {error.Message}"), token);
        ImmutableArray<SessionSkeleton> library;
        lock (libraryLock) library = sessionLibrary;
        candidates.AddRange(library.Select(entry => entry.CandidateFor(expected, sourceIdentity)));
        var baseline = candidates.FirstOrDefault(c => c.Source.Kind == SkeletonSourceKind.Game && c.Source.Resource.GamePath == expected && c.Source.Variant.Length == 0)?.Skeleton;
        return Rank(channels, candidates, baseline, expected, manual, sourceIdentity);
    }
}
