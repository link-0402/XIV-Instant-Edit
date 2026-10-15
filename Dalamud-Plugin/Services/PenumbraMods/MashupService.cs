using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Dalamud.Plugin.Services;
using InstantEdit.Models;
using Lumina.Data;
using static InstantEdit.Services.PenumbraMods.AttributeGroupService;
using static InstantEdit.Services.PenumbraMods.FrameworkThread;
using static InstantEdit.Services.PenumbraMods.MashupPlanning;
using static InstantEdit.Services.PenumbraMods.ModCleanup;
using static InstantEdit.Services.PenumbraMods.ModFiles;
using static InstantEdit.Services.PenumbraMods.ModJson;
using static InstantEdit.Services.PenumbraMods.ModMetadata;
using static InstantEdit.Services.PenumbraMods.ModPaths;

namespace InstantEdit.Services.PenumbraMods;

/// <summary>
/// Builds a mashup: the exported model with the materials and textures each contributor's import
/// captured, as a group in the active mod or as a new mod.
/// </summary>
internal sealed class MashupService
{
    private readonly PenumbraIpc _ipc;
    private readonly IFramework _framework;
    private readonly IPluginLog _log;
    private readonly SemaphoreSlim _exportGate;
    private readonly IDataManager _data;
    private readonly SourceMods _sources;
    private readonly ModActivation _activation;
    private readonly AttributeGroupService _attributes;
    private readonly EstEntryService _est;

    public MashupService(PenumbraIpc ipc, IFramework framework, IPluginLog log, SemaphoreSlim exportGate, IDataManager data,
        SourceMods sources, ModActivation activation, AttributeGroupService attributes, EstEntryService est)
    {
        _ipc = ipc;
        _framework = framework;
        _log = log;
        _exportGate = exportGate;
        _data = data;
        _sources = sources;
        _activation = activation;
        _attributes = attributes;
        _est = est;
    }

    public async Task<ExportResult> ApplyMashupAsync(
        InstantEditImportContext activeContext,
        IReadOnlyList<MashupContributor> contributors,
        MashupPlanResult plan,
        string exportedFile,
        string exportId,
        string destination,
        string name,
        bool bundleExternalDependencies = false,
        bool createAttributeGroups = false,
        IReadOnlyList<string>? attributeTags = null,
        IReadOnlyDictionary<string, int>? attributeMasks = null,
        IReadOnlyList<EstEntryRequest>? estEntries = null)
    {
        if (!plan.Success || string.IsNullOrWhiteSpace(plan.Fingerprint) ||
            !IsSafeVariantGroupName(name) || exportId.Length is < 8 or > 128 ||
            destination is not ("active_mod" or "new_mod") ||
            !IsValidMashupContributorCount(destination, contributors.Count))
            return new ExportResult(false, ResultCode.InvalidMashup, "The mashup request is invalid.");
        if (contributors.All(item => item.Context.ContextId != activeContext.ContextId))
            return new ExportResult(false, ResultCode.InvalidMashup, "The active Context must contribute.");
        if (contributors.Any(item => item.Context.ResourceManifest?.Version != ResourceDependencyManifest.CurrentVersion))
            return new ExportResult(false, ResultCode.MashupReimportRequired, "Re-import every contributing Context before creating a mashup.");
        if (destination == "active_mod" &&
            (activeContext.DestinationState != InstantEditImportContext.ReadyDestination ||
             string.IsNullOrWhiteSpace(activeContext.SourceModDirectory) ||
             string.IsNullOrWhiteSpace(activeContext.TargetFilePath)))
            return new ExportResult(false, ResultCode.DestinationNotReady, "The active Context has no Penumbra mod destination.");
        if (destination == "new_mod" &&
            activeContext.DestinationState is not (InstantEditImportContext.ReadyDestination or
                InstantEditImportContext.NewModRequiredDestination))
            return new ExportResult(false, ResultCode.DestinationNotReady, "The active Context cannot create a Penumbra mashup mod.");
        if (contributors.Any(item => item.Context.DestinationState is not (
                InstantEditImportContext.ReadyDestination or InstantEditImportContext.NewModRequiredDestination)))
            return new ExportResult(false, ResultCode.DestinationNotReady, "A contributing Context is not ready for mashup export.");

        await _exportGate.WaitAsync().ConfigureAwait(false);
        try
        {
            SourceModTarget? activeTarget = null;
            if (destination == "active_mod")
            {
                var resolution = await _framework.RunOnFrameworkThread(() => _sources.ResolveSourceModTargetOnFramework(
                    activeContext.SourceModDirectory!,
                    activeContext.TargetFilePath!,
                    activeContext.SourceModRootPath,
                    activeContext.TargetRelativePath,
                    activeContext.SourceModStableId)).ConfigureAwait(false);
                if (resolution.Target is null)
                    return new ExportResult(false, resolution.Code,
                        resolution.Error ?? "The active Penumbra mod is no longer available.");
                activeTarget = resolution.Target;
                await LeaveFrameworkThread();
            }

            var modelBytes = await File.ReadAllBytesAsync(exportedFile).ConfigureAwait(false);
            var prepared = await PrepareMashupAsync(
                activeContext, contributors, plan, modelBytes, exportId, bundleExternalDependencies)
                .ConfigureAwait(false);
            if (prepared.Error is not null)
                return new ExportResult(false, prepared.Code, prepared.Error);

            if (createAttributeGroups && attributeTags is { Count: > 0 })
            {
                var attributeRoot = activeTarget?.Folder;
                var attributeError = attributeRoot is not null
                    ? ValidateAttributeGroups(attributeRoot, activeContext.ResolvedGamePath,
                        attributeTags, attributeMasks)
                    : ValidateAttributeGroupRequest(activeContext.ResolvedGamePath,
                        attributeTags, attributeMasks);
                if (attributeError is not null)
                    return new ExportResult(false, AttributeGroupErrorCode(attributeError),
                        AttributeGroupErrorMessage(attributeError));
            }

            var description = FormatMashupDescription(contributors, prepared.RequiredExternalMods);

            return destination == "active_mod"
                ? await CommitMashupToActiveModAsync(activeTarget!, activeContext, prepared, exportId, name, description,
                    createAttributeGroups, attributeTags, attributeMasks, estEntries)
                    .ConfigureAwait(false)
                : await CommitMashupToNewModAsync(activeContext, prepared, exportId, name, description,
                    createAttributeGroups, attributeTags, attributeMasks, estEntries)
                    .ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _log.Error(e, "Failed to create Penumbra mashup.");
            return new ExportResult(false, ResultCode.MashupFailed, e.Message);
        }
        finally
        {
            _exportGate.Release();
        }
    }

    private sealed record PreparedMashup(
        byte[] ModelBytes,
        Dictionary<string, byte[]> Files,
        Dictionary<string, string> Mappings,
        Dictionary<string, string> FileSwaps,
        IReadOnlyList<string> RequiredExternalMods,
        ResultCode Code = ResultCode.Accepted,
        string? Error = null,
        IReadOnlyList<PreparedMashupEntry>? Entries = null);

    private sealed record PreparedMashupEntry(
        MashupContributor Contributor,
        MaterialDependency Material,
        MashupMaterialAssignment Assignment,
        bool BundleExternalDependencies,
        string? RelativeMaterialPath,
        IReadOnlyDictionary<string, string> TextureRewrites);

    private static ResourceDependencyManifest? BuildMashupResourceManifest(
        PreparedMashup prepared,
        string outputModDirectory,
        string outputModRoot,
        ModPathRemap? pathRemap,
        JsonArray? manipulations)
    {
        if (prepared.Entries is not { Count: > 0 })
            return null;

        var materials = new List<MaterialDependency>(prepared.Entries.Count);
        foreach (var entry in prepared.Entries)
        {
            SourceResourceLocator materialLocator;
            string materialGamePath;
            if (entry.RelativeMaterialPath is { } relativeMaterial)
            {
                if (!prepared.Files.TryGetValue(relativeMaterial, out var materialBytes))
                    return null;
                relativeMaterial = RemapOutputRelativePath(relativeMaterial, pathRemap);
                materialGamePath = NormalizeGamePath(entry.Assignment.GamePath);
                materialLocator = OutputResourceLocator(
                    materialGamePath, outputModDirectory, outputModRoot, relativeMaterial, materialBytes);
            }
            else
            {
                materialGamePath = NormalizeGamePath(entry.Material.GamePath);
                materialLocator = entry.Material.Resource;
            }

            var textures = new List<TextureDependency>(entry.Material.Textures.Count);
            foreach (var texture in entry.Material.Textures)
            {
                var storedGamePath = entry.TextureRewrites.TryGetValue(
                    NormalizeGamePath(texture.StoredGamePath), out var rewrittenStoredPath)
                    ? NormalizeGamePath(rewrittenStoredPath)
                    : NormalizeGamePath(texture.StoredGamePath);
                var effectiveGamePath = Dx11TexturePath(storedGamePath, texture.Flags);
                var textureLocator = texture.Resource;

                if (TryGetGeneratedTexture(
                        prepared, texture, effectiveGamePath, pathRemap,
                        out var generatedGamePath, out var generatedRelativePath, out var generatedBytes))
                {
                    textureLocator = OutputResourceLocator(
                        generatedGamePath, outputModDirectory, outputModRoot, generatedRelativePath, generatedBytes);
                }
                else if (entry.TextureRewrites.ContainsKey(NormalizeGamePath(texture.StoredGamePath)))
                {
                    // A rewritten material without its corresponding generated
                    // texture would make the manifest appear valid while leaving
                    // the next mashup unable to read the resource.
                    return null;
                }

                textures.Add(texture with
                {
                    StoredGamePath = storedGamePath,
                    EffectiveGamePath = effectiveGamePath,
                    Resource = textureLocator,
                });
            }

            materials.Add(new MaterialDependency
            {
                ModelMaterial = NormalizeModelMaterial(entry.Assignment.Alias),
                GamePath = materialGamePath,
                Resource = materialLocator,
                Textures = textures,
            });
        }

        return new ResourceDependencyManifest
        {
            Materials = materials,
            Manipulations = CloneManipulations(manipulations),
        };
    }

    private static bool TryGetGeneratedTexture(
        PreparedMashup prepared,
        TextureDependency texture,
        string effectiveGamePath,
        ModPathRemap? pathRemap,
        out string generatedGamePath,
        out string generatedRelativePath,
        out byte[] generatedBytes)
    {
        var expectedHash = texture.Resource.Sha256;
        var possiblePaths = new[]
        {
            NormalizeGamePath(effectiveGamePath),
            NormalizeGamePath(texture.Resource.GamePath),
        };
        foreach (var gamePath in possiblePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!prepared.Mappings.TryGetValue(gamePath, out var relative) ||
                !prepared.Files.TryGetValue(relative, out var bytes) ||
                !relative.EndsWith(".tex", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(TextureFiles.Hash(bytes), expectedHash, StringComparison.OrdinalIgnoreCase))
                continue;

            generatedGamePath = gamePath;
            generatedRelativePath = RemapOutputRelativePath(relative, pathRemap);
            generatedBytes = bytes;
            return true;
        }

        generatedGamePath = string.Empty;
        generatedRelativePath = string.Empty;
        generatedBytes = Array.Empty<byte>();
        return false;
    }

    private static SourceResourceLocator OutputResourceLocator(
        string gamePath,
        string modDirectory,
        string modRoot,
        string relativePath,
        byte[] bytes)
        => new()
        {
            Kind = InstantEditImportContext.ModSource,
            GamePath = NormalizeGamePath(gamePath),
            SourceModDirectory = modDirectory,
            SourceModStableId = ReadModStableIdentifier(modRoot),
            SourceModRootPath = modRoot,
            SourceRelativePath = relativePath,
            Sha256 = TextureFiles.Hash(bytes).ToLowerInvariant(),
        };

    private static string RemapOutputRelativePath(string relativePath, ModPathRemap? pathRemap)
        => pathRemap?.RelativePaths.TryGetValue(relativePath, out var remapped) == true
            ? remapped
            : relativePath;

    private async Task<PreparedMashup> PrepareMashupAsync(
        InstantEditImportContext activeContext,
        IReadOnlyList<MashupContributor> contributors,
        MashupPlanResult plan,
        byte[] modelBytes,
        string exportId,
        bool bundleExternalDependencies)
    {
        var expectedAliases = plan.Assignments
            .Select(item => NormalizeModelMaterial(item.Alias))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var fileSwaps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var participantDirectories = contributors
            .Select(item => item.Context.SourceModDirectory)
            .Where(IsSafeModName)
            .Select(item => item!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var externalDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var materialEntries = new List<(
            MashupContributor Contributor,
            MaterialDependency Material,
            MashupMaterialAssignment Assignment,
            bool BundleExternalDependencies)>();
        var preparedEntries = new List<PreparedMashupEntry>();

        foreach (var assignment in plan.Assignments)
        {
            var contributor = contributors.FirstOrDefault(item =>
                string.Equals(item.Context.ContextId, assignment.ContextId, StringComparison.Ordinal));
            if (contributor is null)
                return new PreparedMashup(modelBytes, files, mappings, fileSwaps, [], ResultCode.MashupPlanMismatch,
                    "The material plan references an unknown Context.");
            var manifest = contributor.Context.ResourceManifest!;
            var dependency = manifest.Materials.FirstOrDefault(material =>
                string.Equals(NormalizeModelMaterial(material.ModelMaterial), assignment.ModelMaterial,
                    StringComparison.OrdinalIgnoreCase));
            if (dependency is null)
                return new PreparedMashup(modelBytes, files, mappings, fileSwaps, [], ResultCode.MashupReimportRequired,
                    $"Material {assignment.ModelMaterial} is absent from the captured dependency manifest; re-import that Context.");
            if (!IsValidMashupLocator(dependency.Resource, ".mtrl") ||
                !string.Equals(NormalizeGamePath(dependency.GamePath),
                    NormalizeGamePath(dependency.Resource.GamePath), StringComparison.OrdinalIgnoreCase))
                return new PreparedMashup(modelBytes, files, mappings, fileSwaps, [], ResultCode.MashupReimportRequired,
                    $"Material {assignment.ModelMaterial} has invalid captured source metadata; re-import that Context.");
            var bundleMaterialExternalDependencies = CanBundleExternalMashupDependencies(
                dependency, bundleExternalDependencies);
            foreach (var texture in dependency.Textures)
            {
                if (!IsValidMashupLocator(texture.Resource, ".tex"))
                    return new PreparedMashup(modelBytes, files, mappings, fileSwaps, [], ResultCode.MashupReimportRequired,
                        $"Texture {texture.EffectiveGamePath} has invalid captured source metadata; re-import that Context.");
                if (!ShouldBundleMashupDependency(
                        participantDirectories, texture.Resource, bundleMaterialExternalDependencies))
                    AddExternalMashupDirectory(texture.Resource, participantDirectories, externalDirectories);
            }
            if (!ShouldBundleMashupDependency(
                    participantDirectories, dependency.Resource, bundleMaterialExternalDependencies))
                AddExternalMashupDirectory(dependency.Resource, participantDirectories, externalDirectories);
            materialEntries.Add((contributor, dependency, assignment, bundleMaterialExternalDependencies));
        }

        var actualAliases = MaterialPreviewBundleBuilder.ReadUsedModelMaterials(modelBytes)
            .Select(NormalizeModelMaterial)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!expectedAliases.SetEquals(actualAliases))
            return new PreparedMashup(modelBytes, files, mappings, fileSwaps, [], ResultCode.MashupModelMismatch,
                "The exported MDL material aliases do not match the authenticated mashup plan.");

        var texturePhysicalByHash = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var textureHashByGamePath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Unbundled textures stay at their game paths (vanilla or external files the
        // materials keep loading), so a retargeted texture must not claim those paths.
        foreach (var entry in materialEntries)
            foreach (var texture in entry.Material.Textures)
                if (!ShouldBundleMashupDependency(
                        participantDirectories, texture.Resource, entry.BundleExternalDependencies))
                    textureHashByGamePath.TryAdd(
                        NormalizeGamePath(texture.EffectiveGamePath), texture.Resource.Sha256.ToLowerInvariant());

        async Task<string?> BundleTextureAtOriginalPathAsync(TextureDependency texture)
        {
            var textureBytes = await ReadManifestResourceAsync(texture.Resource).ConfigureAwait(false);
            if (textureBytes is null)
                return $"Texture source changed or disappeared: {texture.Resource.GamePath}. Re-import the Context.";
            var hash = texture.Resource.Sha256.ToLowerInvariant();
            if (!texturePhysicalByHash.TryGetValue(hash, out var relativeTexture))
            {
                relativeTexture = $"Files/xiv-instant-edit/mashups/{exportId[..12]}/textures/{hash[..24]}.tex";
                texturePhysicalByHash[hash] = relativeTexture;
                files[relativeTexture] = textureBytes;
            }
            var gamePath = NormalizeGamePath(texture.Resource.GamePath);
            if (textureHashByGamePath.TryGetValue(gamePath, out var existingHash) &&
                !string.Equals(existingHash, hash, StringComparison.OrdinalIgnoreCase))
                return $"Different bundled textures target {gamePath}.";
            if (fileSwaps.ContainsKey(gamePath))
                return $"A bundled texture conflicts with a pass-through resource at {gamePath}.";
            textureHashByGamePath[gamePath] = hash;
            mappings[gamePath] = relativeTexture;
            return null;
        }

        foreach (var entry in materialEntries)
        {
            var bundleMaterial = ShouldBundleMashupDependency(
                participantDirectories, entry.Material.Resource, entry.BundleExternalDependencies);
            if (!bundleMaterial)
            {
                var target = NormalizeGamePath(entry.Assignment.GamePath);
                var source = NormalizeGamePath(entry.Material.GamePath);
                if (!string.Equals(target, source, StringComparison.OrdinalIgnoreCase))
                {
                    if (mappings.ContainsKey(target) ||
                        (fileSwaps.TryGetValue(target, out var existingSwap) &&
                         !string.Equals(existingSwap, source, StringComparison.OrdinalIgnoreCase)))
                        return new PreparedMashup(modelBytes, files, mappings, fileSwaps, [],
                            ResultCode.MashupMaterialPathConflict, $"A pass-through material conflicts at {target}.");
                    fileSwaps[target] = source;
                }

                foreach (var texture in entry.Material.Textures.Where(texture =>
                             ShouldBundleMashupDependency(
                                 participantDirectories, texture.Resource, entry.BundleExternalDependencies)))
                {
                    var textureError = await BundleTextureAtOriginalPathAsync(texture).ConfigureAwait(false);
                    if (textureError is not null)
                        return new PreparedMashup(modelBytes, files, mappings, fileSwaps, [],
                            ResultCode.MashupTextureConflict, textureError);
                }
                preparedEntries.Add(new PreparedMashupEntry(
                    entry.Contributor,
                    entry.Material,
                    entry.Assignment,
                    entry.BundleExternalDependencies,
                    null,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
                continue;
            }

            var materialBytes = await ReadManifestResourceAsync(entry.Material.Resource).ConfigureAwait(false);
            if (materialBytes is null)
                return new PreparedMashup(modelBytes, files, mappings, fileSwaps, [], ResultCode.MashupSourceChanged,
                    $"Material source changed or disappeared: {entry.Material.GamePath}. Re-import the Context.");

            var rewrites = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var usages = MaterialPreviewBundleBuilder.ReadTextureUsages(materialBytes);
            var indexedTextures = entry.Material.Textures
                .Select((texture, index) => (Texture: texture, Index: index))
                .GroupBy(item => NormalizeGamePath(item.Texture.StoredGamePath), StringComparer.OrdinalIgnoreCase);
            foreach (var storedGroup in indexedTextures)
            {
                var owned = storedGroup
                    .Where(item => ShouldBundleMashupDependency(
                        participantDirectories, item.Texture.Resource, entry.BundleExternalDependencies))
                    .ToArray();
                if (owned.Length != storedGroup.Count())
                {
                    foreach (var item in owned)
                    {
                        var textureError = await BundleTextureAtOriginalPathAsync(item.Texture).ConfigureAwait(false);
                        if (textureError is not null)
                            return new PreparedMashup(modelBytes, files, mappings, fileSwaps, [],
                                ResultCode.MashupTextureConflict, textureError);
                    }
                    continue;
                }

                var captured = new List<(TextureDependency Texture, int Index, string Hash, string Relative)>();
                foreach (var item in owned)
                {
                    var textureBytes = await ReadManifestResourceAsync(item.Texture.Resource).ConfigureAwait(false);
                    if (textureBytes is null)
                        return new PreparedMashup(modelBytes, files, mappings, fileSwaps, [], ResultCode.MashupSourceChanged,
                            $"Texture source changed or disappeared: {item.Texture.EffectiveGamePath}. Re-import the Context.");
                    var hash = item.Texture.Resource.Sha256.ToLowerInvariant();
                    if (!texturePhysicalByHash.TryGetValue(hash, out var relativeTexture))
                    {
                        relativeTexture = $"Files/xiv-instant-edit/mashups/{exportId[..12]}/textures/{hash[..24]}.tex";
                        texturePhysicalByHash[hash] = relativeTexture;
                        files[relativeTexture] = textureBytes;
                    }
                    captured.Add((item.Texture, item.Index, hash, relativeTexture));
                }

                string storedAlias;
                try
                {
                    var usage = captured
                        .Select(item => item.Index < usages.Count ? usages[item.Index] : "other")
                        .FirstOrDefault(item => item != "other") ?? "other";
                    storedAlias = PlanMashupTexturePath(
                        activeContext.GamePath,
                        entry.Contributor.Context.GamePath,
                        storedGroup.Key,
                        entry.Assignment.Slot,
                        usage,
                        captured[0].Index,
                        captured.Select(item => (item.Texture.Flags, item.Hash)).ToArray(),
                        textureHashByGamePath,
                        mappings);
                    rewrites[storedGroup.Key] = storedAlias;
                }
                catch (Exception e)
                {
                    return new PreparedMashup(modelBytes, files, mappings, fileSwaps, [], ResultCode.MashupTextureConflict,
                        $"Could not retarget texture {storedGroup.Key}: {e.Message}");
                }

                foreach (var item in captured)
                {
                    var effective = Dx11TexturePath(storedAlias, item.Texture.Flags);
                    if (textureHashByGamePath.TryGetValue(effective, out var existingHash) &&
                        !string.Equals(existingHash, item.Hash, StringComparison.OrdinalIgnoreCase))
                        return new PreparedMashup(modelBytes, files, mappings, fileSwaps, [], ResultCode.MashupTextureConflict,
                            $"Generated texture path still conflicts: {effective}.");
                    textureHashByGamePath[effective] = item.Hash;
                    mappings[effective] = item.Relative;
                }
            }

            byte[] rewritten;
            try
            {
                rewritten = RewriteMaterialTexturePaths(materialBytes, rewrites);
            }
            catch (Exception e)
            {
                return new PreparedMashup(modelBytes, files, mappings, fileSwaps, [], ResultCode.MashupMaterialRewriteFailed,
                    $"Could not rewrite {entry.Material.GamePath}: {e.Message}");
            }
            var relativeMaterial = ContentAddressedMashupMaterialPath(exportId, rewritten);
            if (files.TryGetValue(relativeMaterial, out var existingMaterial) &&
                !existingMaterial.AsSpan().SequenceEqual(rewritten))
                return new PreparedMashup(modelBytes, files, mappings, fileSwaps, [], ResultCode.MashupMaterialHashConflict,
                    "Two rewritten materials produced the same content address with different bytes.");
            files[relativeMaterial] = rewritten;
            if (mappings.TryGetValue(entry.Assignment.GamePath, out var existingMapping) &&
                !string.Equals(existingMapping, relativeMaterial, StringComparison.Ordinal))
                return new PreparedMashup(modelBytes, files, mappings, fileSwaps, [], ResultCode.MashupMaterialPathConflict,
                    $"More than one rewritten material targets {entry.Assignment.GamePath}.");
            mappings[entry.Assignment.GamePath] = relativeMaterial;
            preparedEntries.Add(new PreparedMashupEntry(
                entry.Contributor,
                entry.Material,
                entry.Assignment,
                entry.BundleExternalDependencies,
                relativeMaterial,
                rewrites));
        }

        var relativeModel = $"Files/xiv-instant-edit/mashups/{exportId[..12]}/model.mdl";
        files[relativeModel] = modelBytes;
        mappings[NormalizeGamePath(activeContext.GamePath)] = relativeModel;
        if (mappings.Keys.Any(fileSwaps.ContainsKey))
            return new PreparedMashup(modelBytes, files, mappings, fileSwaps, [], ResultCode.MashupMappingConflict,
                "A generated file mapping conflicts with a pass-through FileSwap.");
        var requiredExternalMods = await ResolveExternalMashupModNamesAsync(externalDirectories).ConfigureAwait(false);
        return new PreparedMashup(modelBytes, files, mappings, fileSwaps, requiredExternalMods,
            Entries: preparedEntries);
    }

    private async Task<IReadOnlyList<string>> ResolveExternalMashupModNamesAsync(
        IReadOnlyCollection<string> directories)
    {
        if (directories.Count == 0)
            return Array.Empty<string>();
        var mods = await _framework.RunOnFrameworkThread(_ipc.GetMods).ConfigureAwait(false);
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories)
        {
            var value = mods.FirstOrDefault(mod => string.Equals(
                mod.Directory, directory, StringComparison.OrdinalIgnoreCase))?.Name ?? directory;
            value = new string(value.Where(character => !char.IsControl(character)).ToArray())
                .Replace('"', '\'').Trim();
            if (!string.IsNullOrWhiteSpace(value))
                names.Add(value[..Math.Min(value.Length, 160)]);
        }
        return names.ToArray();
    }

    private static void AddExternalMashupDirectory(
        SourceResourceLocator locator,
        IReadOnlySet<string> participantDirectories,
        ISet<string> externalDirectories)
    {
        if (string.Equals(locator.Kind, InstantEditImportContext.ModSource, StringComparison.OrdinalIgnoreCase) &&
            IsSafeModName(locator.SourceModDirectory) &&
            !participantDirectories.Contains(locator.SourceModDirectory!))
            externalDirectories.Add(locator.SourceModDirectory!);
    }

    internal static bool ShouldBundleMashupDependency(
        IReadOnlySet<string> participantDirectories,
        SourceResourceLocator locator,
        bool bundleExternalDependencies)
        => string.Equals(locator.Kind, InstantEditImportContext.ModSource, StringComparison.OrdinalIgnoreCase) &&
           IsSafeModName(locator.SourceModDirectory) &&
           IsSafeRelativeResourcePath(locator.SourceRelativePath) &&
           (participantDirectories.Contains(locator.SourceModDirectory!) || bundleExternalDependencies);

    internal static bool CanBundleExternalMashupDependencies(
        MaterialDependency material,
        bool bundleExternalDependencies)
        => bundleExternalDependencies &&
           !MaterialPreviewBundleBuilder.IsBodyOrGeneralMaterial(material.ModelMaterial) &&
           !MaterialPreviewBundleBuilder.IsBodyOrGeneralMaterial(material.GamePath) &&
           !MaterialPreviewBundleBuilder.IsBodyOrGeneralMaterial(material.Resource.GamePath);

    internal static string ContentAddressedMashupMaterialPath(string exportId, byte[] materialBytes)
    {
        if (exportId.Length < 12 || materialBytes.Length == 0)
            throw new InvalidDataException("The mashup material content address is invalid.");
        var hash = Convert.ToHexString(SHA256.HashData(materialBytes)).ToLowerInvariant();
        return $"Files/xiv-instant-edit/mashups/{exportId[..12]}/materials/{hash}.mtrl";
    }

    private async Task<byte[]?> ReadManifestResourceAsync(SourceResourceLocator locator)
    {
        byte[]? bytes = null;
        if (locator.Kind == "game")
        {
            try
            {
                bytes = (await _data.GetFileAsync<FileResource>(locator.GamePath, CancellationToken.None)
                    .ConfigureAwait(false))?.Data;
            }
            catch (Exception e)
            {
                _log.Debug(e, "Could not read mashup game resource {GamePath}.", locator.GamePath);
            }
        }
        else if (locator.Kind == "mod" && IsSafeModName(locator.SourceModDirectory) &&
                 IsSafeRelativeResourcePath(locator.SourceRelativePath))
        {
            var roots = await _framework.RunOnFrameworkThread(
                () => _sources.GetRegisteredManifestRoots(locator.SourceModDirectory!, locator.SourceModStableId)).ConfigureAwait(false);
            if (roots.Length == 0)
                return null;
            await LeaveFrameworkThread();
            return await ReadVerifiedModManifestResourceAsync(locator, roots).ConfigureAwait(false);
        }

        if (bytes is not { Length: > 0 })
            return null;
        var actualHash = Convert.ToHexString(SHA256.HashData(bytes));
        return string.Equals(actualHash, locator.Sha256, StringComparison.OrdinalIgnoreCase) ? bytes : null;
    }

    internal static async Task<byte[]?> ReadVerifiedModManifestResourceAsync(
        SourceResourceLocator locator,
        IEnumerable<string> authorizedRoots)
    {
        if (locator.Kind != "mod" || !IsSafeModName(locator.SourceModDirectory) ||
            !IsSafeRelativeResourcePath(locator.SourceRelativePath) ||
            locator.Sha256.Length != 64 || locator.Sha256.Any(character => !Uri.IsHexDigit(character)))
            return null;

        foreach (var candidate in authorizedRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var root = NormalizePhysicalPath(candidate);
            if (root is null || !Directory.Exists(root) ||
                (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                continue;

            var file = Path.GetFullPath(Path.Combine(
                root, locator.SourceRelativePath!.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsPathWithin(file, root) || !File.Exists(file) ||
                HasReparsePointInPath(root, file) ||
                (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                continue;

            var bytes = await File.ReadAllBytesAsync(file).ConfigureAwait(false);
            var actualHash = Convert.ToHexString(SHA256.HashData(bytes));
            if (string.Equals(actualHash, locator.Sha256, StringComparison.OrdinalIgnoreCase))
                return bytes;
        }

        return null;
    }

    private async Task<ExportResult> CommitMashupToActiveModAsync(
        SourceModTarget target,
        InstantEditImportContext activeContext,
        PreparedMashup prepared,
        string exportId,
        string requestedName,
        string description,
        bool createAttributeGroups,
        IReadOnlyList<string>? attributeTags,
        IReadOnlyDictionary<string, int>? attributeMasks,
        IReadOnlyList<EstEntryRequest>? estEntries)
    {
        _ = LoadV4ModMetadata(target.Folder);
        var outputManipulations = createAttributeGroups && attributeTags is { Count: > 0 }
            ? ManipulationsWithAtrDefaults(
                activeContext.ResourceManifest?.Manipulations,
                activeContext.ResolvedGamePath,
                attributeTags)
            : CloneManipulations(activeContext.ResourceManifest?.Manipulations);
        var namespaceRelative = $"Files/xiv-instant-edit/mashups/{exportId[..12]}";
        var namespaceFolder = Path.Combine(target.Folder, namespaceRelative.Replace('/', Path.DirectorySeparatorChar));
        if (Directory.Exists(namespaceFolder))
            return new ExportResult(false, ResultCode.MashupDestinationExists, "The mashup namespace already exists.");
        var committed = false;
        var actualName = requestedName;
        try
        {
            foreach (var file in prepared.Files)
                WriteBytesAtomic(target.Folder, file.Key, file.Value);
            actualName = UniqueMashupGroupName(target.Folder, requestedName);
            var groupError = WriteMashupGroup(
                target.Folder,
                actualName,
                prepared.Mappings,
                description,
                activeContext.ResourceManifest?.Manipulations,
                prepared.FileSwaps);
            if (groupError is not null)
                throw new InvalidDataException(groupError);
            committed = true;

            var attributeWarnings = createAttributeGroups && attributeTags is { Count: > 0 }
                ? _attributes.WriteAttributeGroups(target.Folder, activeContext.ResolvedGamePath, attributeTags,
                    attributeMasks, KnownManipulations(activeContext))
                : null;

            var warnings = ExternalMashupWarnings(prepared.RequiredExternalMods).ToList();
            if (attributeWarnings is not null)
                warnings.Add($"Penumbra attribute group setup failed: {AttributeGroupErrorMessage(attributeWarnings)}");
            var cleanup = NormalizeAndDeduplicateMod(target.Folder, target.Directory);
            warnings.AddRange(cleanup.Warnings);
            var mashupModel = prepared.Mappings[NormalizeGamePath(activeContext.GamePath)];
            if (cleanup.PathRemap?.RelativePaths.TryGetValue(mashupModel, out var remappedMashupModel) == true)
                mashupModel = remappedMashupModel;
            warnings.AddRange(_est.TryUpdateEstEntries(target.Folder, target.Directory, mashupModel, estEntries));
            try
            {
                var reloadError = await _framework.RunOnFrameworkThread(
                    () => _activation.ReloadModOnFramework(target.Directory)).ConfigureAwait(false);
                if (reloadError is not null)
                    warnings.Add(reloadError.Message);
                else
                {
                    var redraw = await _framework.RunOnFrameworkThread(
                        _activation.RedrawPlayerOwnedEntitiesOnFramework).ConfigureAwait(false);
                    if (redraw is not null)
                        warnings.Add(redraw);
                }
            }
            catch (Exception e)
            {
                warnings.Add($"The mashup was committed, but Penumbra refresh failed: {e.Message}");
            }
            var modelRelative = prepared.Mappings[NormalizeGamePath(activeContext.GamePath)];
            if (cleanup.PathRemap?.RelativePaths.TryGetValue(modelRelative, out var remappedModel) == true)
                modelRelative = remappedModel;
            var modelPath = Path.Combine(target.Folder, modelRelative.Replace('/', Path.DirectorySeparatorChar));
            return new ExportResult(true,
                warnings.Count == 0 ? ResultCode.MashupApplied : ResultCode.MashupAppliedWithWarnings,
                $"Created mashup group {actualName} in {target.Directory}.", warnings, modelPath, actualName,
                cleanup.PathRemap, prepared.RequiredExternalMods,
                OutputModDirectory: target.Directory,
                OutputModRootPath: target.Folder,
                OutputTargetRelativePath: modelRelative,
                OutputResourceManifest: BuildMashupResourceManifest(
                    prepared, target.Directory, target.Folder, cleanup.PathRemap,
                    outputManipulations));
        }
        catch (Exception e)
        {
            if (!committed)
            {
                TryDeleteMashupNamespace(target.Folder, namespaceFolder);
                return new ExportResult(false, ResultCode.MashupWriteFailed, e.Message);
            }
            var modelRelative = prepared.Mappings[NormalizeGamePath(activeContext.GamePath)];
            var modelPath = Path.Combine(target.Folder, modelRelative.Replace('/', Path.DirectorySeparatorChar));
            var warnings = ExternalMashupWarnings(prepared.RequiredExternalMods)
                .Append($"The mashup was committed, but Penumbra refresh failed: {e.Message}")
                .ToArray();
            return new ExportResult(true, ResultCode.MashupAppliedWithWarnings,
                $"Created mashup group {actualName} in {target.Directory}.",
                warnings, modelPath, actualName, RequiredExternalMods: prepared.RequiredExternalMods,
                OutputModDirectory: target.Directory,
                OutputModRootPath: target.Folder,
                OutputTargetRelativePath: modelRelative,
                OutputResourceManifest: BuildMashupResourceManifest(
                    prepared, target.Directory, target.Folder, null,
                    outputManipulations));
        }
    }

    private async Task<ExportResult> CommitMashupToNewModAsync(
        InstantEditImportContext activeContext,
        PreparedMashup prepared,
        string exportId,
        string modName,
        string description,
        bool createAttributeGroups,
        IReadOnlyList<string>? attributeTags,
        IReadOnlyDictionary<string, int>? attributeMasks,
        IReadOnlyList<EstEntryRequest>? estEntries)
    {
        if (!IsSafeNewModName(modName))
            return new ExportResult(false, ResultCode.InvalidModName, "The Penumbra mod name is invalid.");
        var root = await _framework.RunOnFrameworkThread(_ipc.GetModDirectory).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return new ExportResult(false, ResultCode.PenumbraRootMissing, "The Penumbra mod root is unavailable.");
        var modList = await _framework.RunOnFrameworkThread(() =>
            _ipc.TryGetModList(out var mods) ? mods : null).ConfigureAwait(false);
        if (modList is null)
            return new ExportResult(false, ResultCode.PenumbraUnavailable, "Could not retrieve the Penumbra mod list.");
        await LeaveFrameworkThread();
        var conflicts = modList.Keys.Any(key =>
            string.Equals(key, modName, StringComparison.OrdinalIgnoreCase));
        var finalFolder = Path.Combine(root, modName);
        if (conflicts || Directory.Exists(finalFolder) || File.Exists(finalFolder))
            return new ExportResult(false, ResultCode.MashupModExists, "A Penumbra mod or folder with this name already exists.");

        var outputManipulations = createAttributeGroups && attributeTags is { Count: > 0 }
            ? ManipulationsWithAtrDefaults(
                activeContext.ResourceManifest?.Manipulations,
                activeContext.ResolvedGamePath,
                attributeTags)
            : CloneManipulations(activeContext.ResourceManifest?.Manipulations);
        var staging = Path.Combine(root, $".instant-edit-mashup-{Guid.NewGuid():N}.tmp");
        var committed = false;
        try
        {
            Directory.CreateDirectory(staging);
            foreach (var file in prepared.Files)
                WriteBytesAtomic(staging, file.Key, file.Value);
            WriteJsonAtomic(Path.Combine(staging, "meta.json"), CreateV4ModMetadata(
                modName,
                "XIV Instant Edit",
                description,
                "",
                CreateMashupDefaultData(
                    prepared.Mappings,
                    activeContext.ResourceManifest?.Manipulations,
                    prepared.FileSwaps)));
            if (createAttributeGroups && attributeTags is { Count: > 0 })
            {
                var attributeError = _attributes.WriteAttributeGroups(
                    staging, activeContext.ResolvedGamePath, attributeTags, attributeMasks,
                    KnownManipulations(activeContext));
                if (attributeError is not null)
                    throw new InvalidDataException(attributeError);
            }
            ValidateStagedMashupMod(
                staging,
                modName,
                prepared.Mappings,
                outputManipulations,
                prepared.FileSwaps);
            Directory.Move(staging, finalFolder);
            committed = true;

            var warnings = ExternalMashupWarnings(prepared.RequiredExternalMods).ToList();
            var cleanup = NormalizeAndDeduplicateMod(finalFolder, modName);
            warnings.AddRange(cleanup.Warnings);
            var mashupModel = prepared.Mappings[NormalizeGamePath(activeContext.GamePath)];
            if (cleanup.PathRemap?.RelativePaths.TryGetValue(mashupModel, out var remappedMashupModel) == true)
                mashupModel = remappedMashupModel;
            warnings.AddRange(_est.TryUpdateEstEntries(finalFolder, modName, mashupModel, estEntries));
            try
            {
                var addError = await _activation.AddNewModAsync(modName).ConfigureAwait(false);
                if (addError is not null)
                    warnings.Add(addError.Message);
                else
                {
                    // Enable it in the collection captured with the Context, like a new-mod export:
                    // the object index may belong to another actor, or none, by now.
                    var configure = activeContext.TargetCollectionId is { } collectionId && collectionId != Guid.Empty
                        ? await _framework.RunOnFrameworkThread(() => _activation.ConfigureModForCollectionOnFramework(
                            modName,
                            collectionId,
                            activeContext.TargetCollectionName ?? "captured collection",
                            setPriority: true,
                            priority: 0)).ConfigureAwait(false)
                        : await _framework.RunOnFrameworkThread(
                            () => _activation.ConfigureModOnFramework(
                                modName,
                                activeContext.ObjectIndex,
                                setPriority: true,
                                priority: 0)).ConfigureAwait(false);
                    if (!configure.Success)
                        warnings.Add(configure.Message);
                    else
                        warnings.AddRange(configure.WarningList);
                }
            }
            catch (Exception e)
            {
                warnings.Add($"The mashup mod was committed, but Penumbra setup failed: {e.Message}");
            }
            var modelRelative = prepared.Mappings[NormalizeGamePath(activeContext.GamePath)];
            if (cleanup.PathRemap?.RelativePaths.TryGetValue(modelRelative, out var remappedModel) == true)
                modelRelative = remappedModel;
            var modelPath = Path.Combine(finalFolder, modelRelative.Replace('/', Path.DirectorySeparatorChar));
            var stableId = ReadModStableIdentifier(finalFolder);
            return new ExportResult(true,
                warnings.Count == 0 ? ResultCode.MashupModCreated : ResultCode.MashupModCreatedWithWarnings,
                $"Created Penumbra mashup mod {modName}.", warnings, modelPath, modName,
                RequiredExternalMods: prepared.RequiredExternalMods,
                OutputModDirectory: modName,
                OutputModRootPath: finalFolder,
                OutputTargetRelativePath: modelRelative,
                OutputResourceManifest: BuildMashupResourceManifest(
                    prepared, modName, finalFolder, cleanup.PathRemap,
                    outputManipulations),
                OutputModStableId: stableId);
        }
        catch (Exception e)
        {
            if (Directory.Exists(staging))
                TryDeleteMashupNamespace(root, staging);
            if (committed)
            {
                var modelRelative = prepared.Mappings[NormalizeGamePath(activeContext.GamePath)];
                var modelPath = Path.Combine(finalFolder, modelRelative.Replace('/', Path.DirectorySeparatorChar));
                var stableId = ReadModStableIdentifier(finalFolder);
                var warnings = ExternalMashupWarnings(prepared.RequiredExternalMods)
                    .Append($"The mashup mod was committed, but Penumbra setup failed: {e.Message}")
                    .ToArray();
                return new ExportResult(true, ResultCode.MashupModCreatedWithWarnings,
                    $"Created Penumbra mashup mod {modName}.",
                    warnings, modelPath, modName, RequiredExternalMods: prepared.RequiredExternalMods,
                    OutputModDirectory: modName,
                    OutputModRootPath: finalFolder,
                    OutputTargetRelativePath: modelRelative,
                    OutputResourceManifest: BuildMashupResourceManifest(
                        prepared, modName, finalFolder, null,
                        outputManipulations),
                    OutputModStableId: stableId);
            }
            return new ExportResult(false, ResultCode.MashupModCreateFailed, e.Message);
        }
    }

}
