using System.Collections.Immutable;
using System.Text;
using Dalamud.Plugin.Services;
using InstantEdit.Models;

namespace InstantEdit.Services.Animations;

internal sealed class AnimationResourceCache
{
    private const int MaxEntries = 512;
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(5);
    private readonly object sync = new();
    private readonly Dictionary<string, (AnimationResource Resource, byte[] Bytes, DateTime At)> entries = new(StringComparer.Ordinal);

    public bool TryGet(string gamePath, out (AnimationResource Resource, byte[] Bytes) value)
    {
        lock (sync)
        {
            if (entries.TryGetValue(gamePath, out var cached) && DateTime.UtcNow - cached.At < Lifetime)
            {
                value = (cached.Resource, cached.Bytes);
                return true;
            }

            entries.Remove(gamePath);
        }

        value = default;
        return false;
    }

    public void Set(string gamePath, (AnimationResource Resource, byte[] Bytes) value)
    {
        lock (sync)
        {
            if (entries.Count >= MaxEntries && !entries.ContainsKey(gamePath))
                entries.Remove(entries.Keys.First());
            entries[gamePath] = (value.Resource, value.Bytes, DateTime.UtcNow);
        }
    }

    public void Clear()
    {
        lock (sync)
            entries.Clear();
    }
}

internal sealed class AnimationResources(PenumbraService penumbra, IDataManager data, IFramework framework, IPluginLog log)
{
    private readonly ResourceSourceAttributor sources = new(penumbra, log);

    public async Task<(SkeletonSource Source, byte[] Bytes)> ReadSkeletonAsync(Guid collection, SkeletonSource source, CancellationToken token)
    {
        if (source.Kind == SkeletonSourceKind.Collection)
        {
            var result = await ReadAsync(collection, source.Resource.GamePath, token);
            return (source with { Resource = result.Resource }, result.Bytes);
        }
        var resource = source.Resource;
        byte[] bytes;
        if (source.Kind == SkeletonSourceKind.Game)
        {
            if (!AnimationDependencies.SafeGamePath(resource.GamePath)) throw new InvalidDataException("Invalid game skeleton path.");
            bytes = await Task.Run(() => data.GetFile(resource.GamePath)?.Data ?? throw new FileNotFoundException(resource.GamePath), token);
        }
        else
        {
            if (resource.ModRoot == null || !PathRules.IsPathWithin(resource.ResolvedPath, resource.ModRoot))
                throw new InvalidDataException("Skeleton is outside its registered mod root.");
            TextureFiles.EnsureLocalPath(resource.ResolvedPath);
            using var stream = new FileStream(resource.ResolvedPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            if (stream.Length > AnimationPap.MaxFileSize) throw new InvalidDataException("Skeleton exceeds 256 MiB.");
            bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, token);
        }
        if (bytes.Length > AnimationPap.MaxFileSize) throw new InvalidDataException("Skeleton exceeds 256 MiB.");
        return (source with { Resource = resource with { Hash = AnimationPap.Hash(bytes) } }, bytes);
    }

    public async Task CheckSkeletonsAsync(AnimationCapture capture, IEnumerable<AnimationClip> clips, CancellationToken token)
    {
        foreach (var source in clips.Select(c => c.Resolution?.Selected?.Source).OfType<SkeletonSource>().Distinct())
        {
            if (source.Kind == SkeletonSourceKind.Mod)
            {
                var roots = await penumbra.AnimationSkeletonRootsAsync();
                if (!roots.Any(r => r.Directory == source.Resource.ModDirectory && string.Equals(r.Root, source.Resource.ModRoot, StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("The source skeleton mod moved or was removed. Rescan skeletons.");
            }
            if ((await ReadSkeletonAsync(capture.CollectionId, source, token)).Source != source)
                throw new IOException("The processing skeleton changed. Refresh the animation capture.");
        }
    }

    public Task<(AnimationResource Resource, byte[] Bytes)> ReadAsync(Guid collection, string gamePath, CancellationToken token)
        => ReadAsync(collection, gamePath, token, null);

    public async Task<(AnimationResource Resource, byte[] Bytes)> ReadAsync(Guid collection, string gamePath,
        CancellationToken token, AnimationResourceCache? cache)
    {
        token.ThrowIfCancellationRequested();
        if (cache?.TryGet(gamePath, out var cached) == true) return cached;
        var resolved = await penumbra.ResolveAnimationPathAsync(collection, gamePath);
        byte[] bytes;
        if (Path.IsPathRooted(resolved))
        {
            TextureFiles.EnsureLocalPath(resolved);
            using var stream = new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            if (stream.Length > AnimationPap.MaxFileSize) throw new InvalidDataException($"{gamePath} exceeds 256 MiB.");
            bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, token);
        }
        else
        {
            if (!AnimationDependencies.SafeGamePath(resolved)) throw new InvalidDataException($"Invalid file swap for {gamePath}.");
            bytes = await Task.Run(() => data.GetFile(resolved)?.Data ?? throw new FileNotFoundException($"Missing game resource: {resolved}"), token);
        }
        var source = await framework.RunOnFrameworkThread(() => sources.AttributionFor(resolved));
        var result = (new AnimationResource(gamePath, resolved, AnimationPap.Hash(bytes), source.ModDirectory,
            source.ModRootPath, source.RelativePath, source.ModName), bytes);
        cache?.Set(gamePath, result);
        return result;
    }

    internal static string ResourceMapKey(Dictionary<string, HashSet<string>> paths)
    {
        var builder = new StringBuilder();
        foreach (var pair in paths.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            builder.Append(pair.Key).Append('\0');
            foreach (var gamePath in pair.Value.Order(StringComparer.Ordinal)) builder.Append(gamePath).Append('\0');
        }
        return AnimationPap.Hash(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    public async Task CheckAsync(Guid collection, IEnumerable<AnimationResource> expected, CancellationToken token)
    {
        foreach (var resource in expected)
        {
            var current = (await ReadAsync(collection, resource.GamePath, token)).Resource;
            if (current != resource) throw new IOException($"The source or mapping of {resource.GamePath} changed. Refresh the capture and retry.");
        }
    }

    public static bool CanReplace(AnimationResource source) => source.ModDirectory != null && source.ModRoot != null &&
        source.RelativePath != null && Path.IsPathFullyQualified(source.ResolvedPath) &&
        PathRules.IsPathWithin(source.ResolvedPath, source.ModRoot) &&
        AnimationDependencies.SafeGamePath(source.RelativePath.Replace('\\', '/')) &&
        source.GamePath.EndsWith(".pap", StringComparison.OrdinalIgnoreCase);

    public async Task<AnimationDependencyManifest> ManifestAsync(AnimationCapture capture, AnimationCatalog catalog,
        IEnumerable<string> packagedPaths, CancellationToken token)
    {
        var known = capture.FamilyPaths.Concat(capture.Sources.Select(s => s.GamePath)).ToImmutableArray();
        var loaded = capture.LoadedResourcePaths.IsDefault ? known : known.Concat(capture.LoadedResourcePaths).Distinct().ToImmutableArray();
        var reads = new Dictionary<string, (AnimationResource Resource, byte[] Bytes)>(StringComparer.Ordinal);
        async Task<(AnimationResource Resource, byte[] Bytes)> Read(string path)
        {
            token.ThrowIfCancellationRequested();
            if (!reads.TryGetValue(path, out var source)) reads[path] = source = await ReadAsync(capture.CollectionId, path, token);
            return source;
        }
        var metadata = AnimationMetadata.Decode(await penumbra.AnimationMetadataAsync(capture.CollectionId));
        var manifest = await AnimationDependencies.BuildAsync(known,
            Read,
            async (parent, reference) =>
            {
                var path = reference.Path;
                if (reference.Kind == "timeline" && !path.EndsWith(".tmb", StringComparison.OrdinalIgnoreCase)) path = $"chara/action/{path}.tmb";
                if (reference.Kind == "animation" && !path.EndsWith(".pap", StringComparison.OrdinalIgnoreCase))
                {
                    return await ResolveMotionAsync(path, parent, capture.Clip.GamePath, loaded, catalog,
                        async candidate => (await Read(candidate)).Bytes, token);
                }
                if (reference.Kind == "material" && path.StartsWith('/'))
                {
                    var material = path.TrimStart('/');
                    if (material.Contains('/') && AnimationDependencies.SafeGamePath(material)) return new[] { material };
                    var candidates = loaded.Where(p => p.EndsWith('/' + material, StringComparison.OrdinalIgnoreCase)).Distinct().ToArray();
                    if (candidates.Length == 1) return candidates;
                    throw new InvalidDataException($"Material '{path}' in {parent} requires an unambiguous loaded player IMC variant. Its dependencies could not be established.");
                }
                return new[] { path };
            }, token);
        return manifest with { ManipulationsJson = AnimationMetadata.Applicable(metadata, packagedPaths).ToJsonString() };
    }

    internal static async Task<IReadOnlyList<string>> ResolveMotionAsync(string motion, string parent, string selectedPap,
        IEnumerable<string> loaded, AnimationCatalog catalog, Func<string, Task<byte[]>> read, CancellationToken token)
    {
        async Task<bool> Contains(string path)
        {
            token.ThrowIfCancellationRequested();
            return new AnimationPap(await read(path)).Entries.Any(e => e.Name == motion);
        }
        // A timeline can refer to its own PAP entry without an external dependency.
        if (parent.EndsWith(".pap", StringComparison.OrdinalIgnoreCase) && await Contains(parent)) return [];
        var paths = loaded.Where(AnimationDependencies.SafeGamePath).Distinct(StringComparer.Ordinal).ToArray();
        var checkedPaths = new HashSet<string>(StringComparer.Ordinal) { parent };
        var containing = new List<string>();
        foreach (var candidate in paths.Where(p => p.EndsWith(".pap", StringComparison.OrdinalIgnoreCase)))
            if (checkedPaths.Add(candidate) && await Contains(candidate)) containing.Add(candidate);
        if (containing.Count == 0)
            foreach (var candidate in catalog.ResolveMotion(motion, selectedPap, paths, token))
            {
                if (!checkedPaths.Add(candidate)) continue;
                try { if (await Contains(candidate)) containing.Add(candidate); }
                catch (FileNotFoundException) { } // An inferred game variant may not exist.
            }
        if (containing.Count != 1)
            throw new InvalidDataException(containing.Count == 0
                ? $"Cannot identify the current player's PAP for motion '{motion}' in {parent}."
                : $"Motion '{motion}' in {parent} is present in several player PAPs; the binding cannot be resolved uniquely.");
        var papParts = containing[0].Split('/');
        // Include the captured facial skeleton when this is an external facial PAP.
        if (papParts.Length >= 7 && papParts[0] == "chara" && papParts[1] == "human" && papParts[3] == "animation" && papParts[4].StartsWith('f'))
            containing.AddRange(paths.Where(p => p.StartsWith($"chara/human/{papParts[2]}/skeleton/face/{papParts[4]}/", StringComparison.Ordinal) &&
                p.EndsWith(".sklb", StringComparison.Ordinal)));
        return containing;
    }
}
