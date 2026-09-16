using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using InstantEdit.Models;
using Penumbra.Api.Enums;
using Penumbra.Api.Helpers;
using Penumbra.Api.IpcSubscribers;
using Lumina.Data;
using Lumina.Data.Files;

namespace InstantEdit.Services;

public sealed partial class PenumbraService
{
    public IReadOnlyList<PenumbraMod> GetMods()
        => GetModList()
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => new PenumbraMod(pair.Key, pair.Value))
            .OrderBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(mod => mod.Directory, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private sealed record ModScanRequest(PenumbraMod Mod, string[] CandidateRoots);

    /// <summary>Resolve Penumbra state on the framework thread, then scan files on a worker.</summary>
    public async Task<PenumbraModSnapshot?> GetModResourcesAsync(
        string modDirectory,
        CancellationToken cancellationToken = default,
        Guid? stableId = null)
    {
        if (!IsSafeModName(modDirectory))
            return null;

        try
        {
            var request = await _framework.RunOnFrameworkThread(
                () => ResolveModScanOnFramework(modDirectory, stableId)).ConfigureAwait(false);
            if (request is null)
                return null;
            return await Task.Run(() => ScanModResources(request, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception e)
        {
            _log.Debug($"Could not read Penumbra mod resources: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Compare captured non-active material and texture dependencies with the current
    /// contents of the active output mod. The scan is authoritative because it
    /// reads the registered output mod and verifies each matching file's bytes.
    /// </summary>
    public async Task<MaterialCoverageResult> GetMaterialCoverageAsync(
        InstantEditImportContext activeContext,
        IReadOnlyList<MashupContributor> contributors,
        CancellationToken cancellationToken = default)
    {
        if (activeContext.DestinationState != InstantEditImportContext.ReadyDestination ||
            !IsSafeModName(activeContext.SourceModDirectory))
            return MaterialCoverageUnavailable();

        var snapshot = await GetModResourcesAsync(activeContext.SourceModDirectory!, cancellationToken, activeContext.SourceModStableId)
            .ConfigureAwait(false);
        if (snapshot is null)
            return MaterialCoverageUnavailable();

        return await EvaluateMaterialCoverageAsync(activeContext, contributors, snapshot.Resources, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Evaluate captured dependencies against already-scanned output resources.</summary>
    internal static async Task<MaterialCoverageResult> EvaluateMaterialCoverageAsync(
        InstantEditImportContext activeContext,
        IReadOnlyList<MashupContributor> contributors,
        IReadOnlyList<PenumbraModResource> outputResources,
        CancellationToken cancellationToken = default)
    {
        if (activeContext.DestinationState != InstantEditImportContext.ReadyDestination ||
            !IsSafeModName(activeContext.SourceModDirectory) || contributors.Count is < 1 or > 16)
            return MaterialCoverageUnavailable();

        var required = new List<(
            MashupContributor Contributor,
            string ResourceType,
            string ModelMaterial,
            string GamePath,
            SourceResourceLocator Locator)>();
        var requiredKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var requestedByContext = new HashSet<string>(StringComparer.Ordinal);
        foreach (var contributor in contributors)
        {
            if (!requestedByContext.Add(contributor.Context.ContextId) ||
                contributor.Context.ResourceManifest?.Version != ResourceDependencyManifest.CurrentVersion)
                return MaterialCoverageUnavailable();

            var seenMaterials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var requestedMaterial in contributor.Materials)
            {
                var normalizedMaterial = NormalizeModelMaterial(requestedMaterial);
                if (!seenMaterials.Add(normalizedMaterial))
                    continue;
                var dependency = contributor.Context.ResourceManifest.Materials.FirstOrDefault(material =>
                    string.Equals(NormalizeModelMaterial(material.ModelMaterial), normalizedMaterial,
                        StringComparison.OrdinalIgnoreCase));
                if (dependency is null)
                    return MaterialCoverageUnavailable();

                if (!IsValidMashupLocator(dependency.Resource, ".mtrl"))
                    return MaterialCoverageUnavailable();

                if (string.Equals(dependency.Resource.Kind, InstantEditImportContext.ModSource,
                        StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(dependency.Resource.SourceModDirectory, activeContext.SourceModDirectory,
                        StringComparison.OrdinalIgnoreCase))
                {
                    var gamePath = NormalizeGamePath(dependency.Resource.GamePath);
                    var key = $"{contributor.Context.ContextId}\0material\0{gamePath}\0{dependency.Resource.Sha256}";
                    if (requiredKeys.Add(key))
                        required.Add((contributor, "material", normalizedMaterial, gamePath, dependency.Resource));
                }

                foreach (var texture in dependency.Textures)
                {
                    if (!IsValidMashupLocator(texture.Resource, ".tex"))
                        return MaterialCoverageUnavailable();
                    if (!string.Equals(texture.Resource.Kind, InstantEditImportContext.ModSource,
                            StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(texture.Resource.SourceModDirectory, activeContext.SourceModDirectory,
                            StringComparison.OrdinalIgnoreCase))
                        continue;
                    var gamePath = NormalizeGamePath(texture.Resource.GamePath);
                    var key = $"{contributor.Context.ContextId}\0texture\0{gamePath}\0{texture.Resource.Sha256}";
                    if (requiredKeys.Add(key))
                        required.Add((contributor, "texture", normalizedMaterial, gamePath, texture.Resource));
                }
            }
        }

        var outputByGamePath = outputResources
            .Where(resource => resource.GamePath.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase) ||
                               resource.GamePath.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
            .GroupBy(resource => NormalizeGamePath(resource.GamePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var missing = new List<MaterialCoverageMissing>();
        var hashCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in required)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var locator = item.Locator;
            var gamePath = item.GamePath;
            var sourceName = string.Equals(
                    locator.SourceModDirectory,
                    item.Contributor.Context.SourceModDirectory,
                    StringComparison.OrdinalIgnoreCase)
                ? item.Contributor.Context.SourceModName ?? locator.SourceModDirectory!
                : locator.SourceModDirectory!;
            if (!outputByGamePath.TryGetValue(gamePath, out var candidates))
            {
                missing.Add(new MaterialCoverageMissing(
                    item.Contributor.Context.ContextId,
                    sourceName,
                    item.ModelMaterial,
                    gamePath,
                    item.ResourceType));
                continue;
            }

            var matched = false;
            foreach (var candidate in candidates)
            {
                if (!hashCache.TryGetValue(candidate.ActualPath, out var actualHash))
                {
                    try
                    {
                        await using var stream = new FileStream(
                            candidate.ActualPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                            bufferSize: 64 * 1024, useAsync: true);
                        actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)
                            .ConfigureAwait(false));
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch
                    {
                        return MaterialCoverageUnavailable();
                    }
                    hashCache[candidate.ActualPath] = actualHash;
                }

                if (string.Equals(actualHash, locator.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    matched = true;
                    break;
                }
            }

            if (!matched)
                missing.Add(new MaterialCoverageMissing(
                    item.Contributor.Context.ContextId,
                    sourceName,
                    item.ModelMaterial,
                    gamePath,
                    item.ResourceType));
        }

        return missing.Count == 0
            ? new MaterialCoverageResult(true, true, "material_coverage_complete",
                "The output mod contains all captured non-active material and texture dependencies.", missing)
            : new MaterialCoverageResult(true, false, "material_coverage_missing",
                "The output mod is missing one or more captured non-active material or texture dependencies.", missing);
    }

    private static MaterialCoverageResult MaterialCoverageUnavailable()
        => new(false, false, "material_coverage_unavailable",
            "Material coverage could not be verified.", Array.Empty<MaterialCoverageMissing>());

    private ModScanRequest? ResolveModScanOnFramework(string modDirectory, Guid? stableId = null)
    {
        if (stableId is not null && TryGetModList(out var modList) &&
            !TryResolveRegisteredModIdentity(modList, modDirectory, stableId, null, out modDirectory, out _))
            return null;
        var mod = GetMods().FirstOrDefault(item =>
            string.Equals(item.Directory, modDirectory, StringComparison.OrdinalIgnoreCase));
        if (mod is null)
            return null;

        var candidateRoots = new List<string>();
        var pathResult = _getModPath.Invoke(mod.Directory, string.Empty);
        if (pathResult.Item1 is PenumbraApiEc.Success)
            AddCandidateRoot(candidateRoots, pathResult.Item2);

        // GetModPath can point at a manually configured location. Keep the
        // standard Penumbra root as a fallback for older/API-incompatible installs.
        var modDirectoryRoot = GetModDirectory();
        if (!string.IsNullOrWhiteSpace(modDirectoryRoot))
            AddCandidateRoot(candidateRoots, Path.Combine(modDirectoryRoot, mod.Directory));
        mod = mod with
        {
            StableId = candidateRoots
                .Select(ReadModStableIdentifier)
                .FirstOrDefault(identifier => identifier.HasValue),
        };
        return new ModScanRequest(mod, candidateRoots.ToArray());
    }

    private PenumbraModSnapshot? ScanModResources(ModScanRequest request, CancellationToken cancellationToken)
    {
        var mod = request.Mod;
        var scannedRoot = false;
        try
        {
            foreach (var root in request.CandidateRoots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                    continue;

                var standardFilesRoot = Path.Combine(root, "Files");
                string scanRoot;
                string sourceRoot;
                string relativePrefix;
                if (string.Equals(Path.GetFileName(root), "Files", StringComparison.OrdinalIgnoreCase))
                {
                    // Penumbra exposes the Files directory itself; treat its parent as the
                    // durable mod root and prefix every relative path with "Files/".
                    sourceRoot = Directory.GetParent(root)?.FullName ?? root;
                    scanRoot = root;
                    relativePrefix = "Files/";
                }
                else if (Directory.Exists(standardFilesRoot))
                {
                    // Standard Penumbra layout: the mod root has a Files/ subdirectory that
                    // contains every game resource. The relative path must mirror that
                    // prefix so the registry can reproduce sourceModRootPath + relativePath
                    // == targetFilePath when a Mod Browser import becomes a Quick Export.
                    sourceRoot = root;
                    scanRoot = standardFilesRoot;
                    relativePrefix = "Files/";
                }
                else
                {
                    sourceRoot = root;
                    scanRoot = root;
                    relativePrefix = string.Empty;
                }

                if (!Directory.Exists(scanRoot) || HasReparsePointInPath(sourceRoot, scanRoot))
                    continue;

                scannedRoot = true;
                var mappings = ReadModMappings(sourceRoot);
                var resources = new List<PenumbraModResource>();
                var enumeration = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                };
                foreach (var file in Directory.EnumerateFiles(scanRoot, "*", enumeration))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsSafeModResourceFile(sourceRoot, file))
                        continue;

                    var relativePath = Path.GetRelativePath(scanRoot, file).Replace('\\', '/');
                    var modPath = relativePrefix + relativePath;
                    var gamePath = CanonicalGamePathFor(modPath, mappings.GamePaths);
                    var extension = Path.GetExtension(relativePath);
                    if (!extension.Equals(".mdl", StringComparison.OrdinalIgnoreCase) &&
                        !extension.Equals(".tex", StringComparison.OrdinalIgnoreCase) &&
                        !extension.Equals(".atex", StringComparison.OrdinalIgnoreCase) &&
                        !extension.Equals(".mtrl", StringComparison.OrdinalIgnoreCase))
                        continue;

                    resources.Add(new PenumbraModResource(
                        gamePath,
                        file,
                        modPath,
                        OptionMappingFor(modPath, mappings.OptionLabels),
                        OptionMembershipsFor(modPath, mappings.OptionMemberships)));
                }

                if (resources.Count > 0)
                    return new PenumbraModSnapshot(
                        mod.Directory,
                        mod.Name,
                        sourceRoot,
                        resources.OrderBy(resource => resource.GamePath, StringComparer.OrdinalIgnoreCase).ToArray(),
                        mod.StableId);
            }

            return scannedRoot
                ? new PenumbraModSnapshot(mod.Directory, mod.Name, request.CandidateRoots.FirstOrDefault() ?? string.Empty, Array.Empty<PenumbraModResource>(), mod.StableId)
                : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            _log.Debug($"Could not read Penumbra mod resources: {e.Message}");
            return null;
        }
    }

}

public sealed record PenumbraMod(string Directory, string Name, Guid? StableId = null);

public sealed record PenumbraModResource(
    string GamePath,
    string ActualPath,
    string RelativePath,
    string OptionMapping,
    IReadOnlyList<string> OptionMemberships);

public sealed record PenumbraModSnapshot(
    string Directory,
    string Name,
    string RootPath,
    IReadOnlyList<PenumbraModResource> Resources,
    Guid? StableId = null);
