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
                    throw new IOException("The source skeleton mod moved or was removed. Rebuild the skeleton library in Settings.");
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
        SafeModRelativePath(source.RelativePath) &&
        source.GamePath.EndsWith(".pap", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A PAP's path inside its mod folder. Mod authors name their files freely
    /// (e.g. "animations/sybian riding groundsit 1 v10.pap"), so this follows the
    /// backup store's file-name rules rather than the stricter game-path ones.
    /// </summary>
    internal static bool SafeModRelativePath(string relativePath) =>
        PenumbraService.IsSafeGameResourcePath(relativePath.Replace('\\', '/'), ".pap");

    /// <summary>
    /// The clips a rebake rewrites, read for the edit and rechecked at commit. A
    /// rebake changes motion data alone and keeps every timeline entry, so the
    /// sounds, expressions, VFX and other files those entries name resolve in-game
    /// exactly as before. None of them is read, packaged or required: an
    /// expression library's PAP that Penumbra's snapshot never lists, or a sound
    /// another mod owns, must not block the edit.
    /// </summary>
    public async Task<AnimationDependencyManifest> ManifestAsync(AnimationCapture capture, IEnumerable<string> clipPaths,
        AnimationDestination destination, CancellationToken token)
    {
        var reads = new List<(AnimationResource Resource, byte[] Bytes)>();
        foreach (var path in clipPaths.Distinct(StringComparer.OrdinalIgnoreCase))
            reads.Add(await ReadAsync(capture.CollectionId, path, token));
        var manifest = new AnimationDependencyManifest(reads.Select(r => r.Resource).ToImmutableArray(),
            reads.ToImmutableDictionary(r => r.Resource.GamePath, r => r.Bytes, StringComparer.OrdinalIgnoreCase));
        if (destination != AnimationDestination.NewMod) return manifest;
        // A new mod carries the collection's metadata that applies to its clips.
        var metadata = AnimationMetadata.Decode(await penumbra.AnimationMetadataAsync(capture.CollectionId));
        return manifest with { ManipulationsJson = AnimationMetadata.Applicable(metadata, manifest.Files.Keys).ToJsonString() };
    }
}
