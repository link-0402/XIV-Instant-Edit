using System.Collections.Immutable;
using Dalamud.Plugin.Services;
using InstantEdit.Models;

namespace InstantEdit.Services.Animations;

internal sealed class AnimationResources(PenumbraService penumbra, IDataManager data, IFramework framework, IPluginLog log)
{
    private readonly ResourceSourceAttributor sources = new(penumbra, log);

    public async Task<(AnimationResource Resource, byte[] Bytes)> ReadAsync(Guid collection, string gamePath, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
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
        return (new AnimationResource(gamePath, resolved, AnimationPap.Hash(bytes), source.ModDirectory,
            source.ModRootPath, source.RelativePath), bytes);
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

    public async Task<AnimationDependencyManifest> ManifestAsync(AnimationCapture capture, AnimationCatalog catalog, CancellationToken token)
    {
        var known = capture.FamilyPaths.Concat(capture.Sources.Select(s => s.GamePath)).ToImmutableArray();
        var loaded = capture.LoadedResourcePaths.IsDefault ? known : capture.LoadedResourcePaths;
        var metadata = AnimationMetadata.Decode(await penumbra.AnimationMetadataAsync(capture.CollectionId));
        var manifest = await AnimationDependencies.BuildAsync(known,
            path => ReadAsync(capture.CollectionId, path, token),
            async (parent, reference) =>
            {
                var path = reference.Path;
                if (reference.Kind == "timeline" && !path.EndsWith(".tmb", StringComparison.OrdinalIgnoreCase)) path = $"chara/action/{path}.tmb";
                if (reference.Kind == "animation" && !path.EndsWith(".pap", StringComparison.OrdinalIgnoreCase))
                {
                    // PAP timelines refer to a contained motion by name. It may already be in the same PAP.
                    if (parent.EndsWith(".pap", StringComparison.OrdinalIgnoreCase))
                    {
                        var pap = new AnimationPap((await ReadAsync(capture.CollectionId, parent, token)).Bytes);
                        if (pap.Entries.Any(e => e.Name == path)) return Array.Empty<string>();
                    }
                    var containing = new List<string>();
                    foreach (var candidate in known.Where(p => p.EndsWith(".pap", StringComparison.OrdinalIgnoreCase)).Distinct())
                    {
                        var pap = new AnimationPap((await ReadAsync(capture.CollectionId, candidate, token)).Bytes);
                        if (pap.Entries.Any(e => e.Name == path)) containing.Add(candidate);
                    }
                    if (containing.Count == 1) return containing;
                    if (containing.Count > 1) throw new InvalidDataException($"Motion '{path}' in {parent} is present in several family PAPs; the binding cannot be resolved uniquely.");
                    var candidates = catalog.ResolveMotion(path, capture.Clip.GamePath, known);
                    if (candidates.Count != 1) throw new InvalidDataException($"Cannot identify the current player's PAP for motion '{path}' in {parent}.");
                    return candidates;
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
        return manifest with { ManipulationsJson = AnimationMetadata.Applicable(metadata, manifest.Files.Keys).ToJsonString() };
    }
}
