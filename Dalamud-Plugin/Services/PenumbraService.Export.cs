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
    /// <summary>
    /// Write an export back to the resolved file in its original Penumbra mod.
    /// The destination is authoritative while the server-owned import context
    /// is alive; only the source mod registration and destination folder are
    /// checked again before writing.
    /// </summary>
    public async Task<ExportResult> ApplySourceExportAsync(
        string sourceModDirectory,
        string sourceFilePath,
        string? sourceModRootPath,
        string? targetRelativePath,
        string sourceGamePath,
        string exportedFile,
        string? variantName,
        string? variantGroupName,
        string? variantTarget,
        string? variantTargetId,
        bool setupVariantInPenumbra,
        bool backupExisting = false,
        SourceOptionLocator? sourceOption = null,
        string sourceOptionStatus = "unknown",
        Guid? sourceModStableId = null,
        bool createAttributeGroups = false,
        IReadOnlyList<string>? attributeTags = null,
        IReadOnlyDictionary<string, int>? attributeMasks = null,
        string? resolvedGamePath = null,
        JsonArray? sourceManipulations = null)
    {
        resolvedGamePath ??= sourceGamePath;
        if (!IsSafeModName(sourceModDirectory) || !IsSafeGamePath(sourceGamePath) ||
            !IsSafeLocalModelPath(sourceFilePath))
            return new ExportResult(false, "destination_unsafe", "The original Penumbra model destination is invalid.");
        var validationError = ValidateExportRequest(sourceModDirectory, sourceGamePath, exportedFile);
        if (validationError is not null)
            return new ExportResult(false, "invalid_export_file", validationError);
        if (variantName is not null && !IsSafeVariantName(variantName))
            return new ExportResult(false, "invalid_variant_name", "Invalid variant name.");
        if (variantName is not null && string.Equals(
                variantName,
                Path.GetFileNameWithoutExtension(sourceGamePath),
                StringComparison.OrdinalIgnoreCase))
            return new ExportResult(false, "invalid_variant_name", "Variant name must differ from the originally imported model name.");
        if (setupVariantInPenumbra && variantName is null && !string.Equals(variantTarget, "option", StringComparison.Ordinal))
            return new ExportResult(false, "invalid_variant", "Penumbra variant setup requires Save as Variant.");
        if (setupVariantInPenumbra && !string.Equals(variantTarget, "option", StringComparison.Ordinal) &&
            !string.Equals(variantTarget, "group", StringComparison.Ordinal) && !IsSafeVariantGroupName(variantGroupName))
            return new ExportResult(false, "invalid_variant_group", "Penumbra variant setup requires an option group name.");
        if (setupVariantInPenumbra && variantTarget == "group" && string.IsNullOrWhiteSpace(variantTargetId))
            return new ExportResult(false, "stale_variant_target", "The selected Penumbra group is invalid.");

        await _exportGate.WaitAsync().ConfigureAwait(false);
        string? committedTarget = null;
        try
        {
            var resolved = await _framework.RunOnFrameworkThread(
                () => ResolveSourceModTargetOnFramework(
                    sourceModDirectory,
                    sourceFilePath,
                    sourceModRootPath,
                    targetRelativePath,
                    sourceModStableId)).ConfigureAwait(false);
            if (resolved.Target is null)
                return new ExportResult(
                    false,
                    resolved.Code,
                    resolved.Error ?? "The original Penumbra mod is no longer available.");
            _ = LoadV4ModMetadata(resolved.Target.Folder);
            if (createAttributeGroups && attributeTags is { Count: > 0 })
            {
                var attributeError = ValidateAttributeGroups(
                    resolved.Target.Folder, resolvedGamePath, attributeTags, attributeMasks);
                if (attributeError is not null)
                    return new ExportResult(false, AttributeGroupErrorCode(attributeError),
                        AttributeGroupErrorMessage(attributeError));
            }

            var targetFile = variantName is null
                ? resolved.Target.FilePath
                : Path.Combine(Path.GetDirectoryName(resolved.Target.FilePath)!, variantName + ".mdl");
            if (setupVariantInPenumbra && string.Equals(variantTarget, "option", StringComparison.Ordinal))
            {
                var optionTarget = ResolveVariantOptionTarget(
                    resolved.Target.Folder, sourceGamePath, variantTargetId);
                if (optionTarget.Error is not null)
                    return new ExportResult(false, optionTarget.Code, optionTarget.Error);
                targetFile = optionTarget.FilePath!;
            }
            JsonObject? sourceOptionTemplate = null;
            if (setupVariantInPenumbra && !string.Equals(variantTarget, "option", StringComparison.Ordinal))
            {
                if (sourceOptionStatus == "ambiguous")
                    return new ExportResult(false, "ambiguous_source_option",
                        "Multiple Penumbra options provide the imported model. Re-import it from the intended option, then export again.");
                if (sourceOptionStatus != "default")
                {
                    var source = ResolveSourceOption(resolved.Target.Folder, sourceGamePath,
                        resolved.Target.RelativePath, sourceOption);
                    if (source.Error is not null)
                        return new ExportResult(false, "source_option_unavailable", source.Error);
                    sourceOptionTemplate = source.Option;
                }
            }
            VariantGroupWrite? groupWrite = null;
            if (setupVariantInPenumbra && !string.Equals(variantTarget, "option", StringComparison.Ordinal))
            {
                var groupError = PrepareVariantGroup(
                    resolved.Target.Folder, sourceGamePath,
                    Path.GetRelativePath(resolved.Target.Folder, targetFile).Replace('\\', '/'),
                    variantName!, variantGroupName!, out groupWrite, sourceOptionTemplate,
                    string.Equals(variantTarget, "group", StringComparison.Ordinal) ? variantTargetId : null);
                if (groupError is not null)
                    return new ExportResult(false,
                        variantTarget == "group" ? "stale_variant_target" : "invalid_variant_group", groupError);
            }
            var writeError = WriteModelToOriginalLocation(
                resolved.Target.Folder,
                targetFile,
                exportedFile,
                backupExisting,
                resolved.Target.Directory,
                Path.GetRelativePath(resolved.Target.Folder, targetFile).Replace('\\', '/'));
            if (writeError is not null)
                return new ExportResult(false, "write_failed", writeError);
            committedTarget = targetFile;

            var warnings = new List<string>();

            if (groupWrite is not null)
            {
                var groupError = CommitVariantGroup(groupWrite);
                if (groupError is not null)
                    warnings.Add($"Penumbra variant setup failed: {groupError}");
            }

            if (createAttributeGroups && attributeTags is { Count: > 0 })
            {
                var attributeError = WriteAttributeGroups(
                    resolved.Target.Folder, resolvedGamePath, attributeTags, attributeMasks,
                    sourceManipulations);
                if (attributeError is not null)
                    warnings.Add($"Penumbra attribute group setup failed: {AttributeGroupErrorMessage(attributeError)}");
            }

            var reloadError = await _framework.RunOnFrameworkThread(
                () => ReloadModOnFramework(resolved.Target.Directory)).ConfigureAwait(false);
            if (reloadError is not null)
                warnings.Add(reloadError.Message);
            else
            {
                var redrawWarning = await _framework.RunOnFrameworkThread(
                    RedrawPlayerOwnedEntitiesOnFramework).ConfigureAwait(false);
                if (redrawWarning is not null)
                    warnings.Add(redrawWarning);
            }

            var code = warnings.Count == 0 ? "export_applied" : "export_applied_with_warnings";
            var message = warnings.Count == 0
                ? $"Exported to {targetFile} and reloaded {resolved.Target.Directory}."
                : $"Exported to {targetFile}; {warnings.Count} follow-up warning(s).";
            return new ExportResult(true, code, message, warnings, targetFile);
        }
        catch (Exception e)
        {
            _log.Error(e, "Failed to export to the original Penumbra mod.");
            if (committedTarget is not null)
                return new ExportResult(
                    true,
                    "export_applied_with_warnings",
                    $"Exported to {committedTarget}; follow-up processing failed.",
                    [$"Follow-up processing failed: {e.Message}"],
                    committedTarget);
            return new ExportResult(false, "write_failed", $"Failed before the model write could be committed: {e.Message}");
        }
        finally
        {
            _exportGate.Release();
        }
    }

    /// <summary>Read Single groups whose options replace this context's model path.</summary>
    public async Task<VariantTargetsResult> GetVariantTargetsAsync(
        string sourceModDirectory,
        string sourceFilePath,
        string? sourceModRootPath,
        string? targetRelativePath,
        string sourceGamePath,
        Guid? sourceModStableId = null)
    {
        if (!IsSafeModName(sourceModDirectory) || !IsSafeLocalModelPath(sourceFilePath) || !IsSafeGamePath(sourceGamePath))
            return new VariantTargetsResult(false, "destination_unsafe", "The original Penumbra model destination is invalid.", []);

        await _exportGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var resolved = await _framework.RunOnFrameworkThread(
                () => ResolveSourceModTargetOnFramework(
                    sourceModDirectory, sourceFilePath, sourceModRootPath, targetRelativePath, sourceModStableId)).ConfigureAwait(false);
            if (resolved.Target is null)
                return new VariantTargetsResult(false, resolved.Code,
                    resolved.Error ?? "The original Penumbra mod is no longer available.", []);
            return new VariantTargetsResult(true, "variant_targets_loaded", "Compatible Penumbra targets loaded.",
                ReadVariantTargets(resolved.Target.Folder, sourceGamePath, resolved.Target.Directory, _backups));
        }
        catch (Exception e)
        {
            _log.Error(e, "Failed to read Penumbra variant targets.");
            return new VariantTargetsResult(false, "variant_targets_unavailable", "Could not read Penumbra option groups.", []);
        }
        finally
        {
            _exportGate.Release();
        }
    }

    /// <summary>Create the first persistent Penumbra destination for a game-data import.</summary>
    public async Task<NewModelModResult> CreateGameModelModAsync(
        InstantEditImportContext context,
        string exportedFile,
        string modName,
        bool createAttributeGroups = false,
        IReadOnlyList<string>? attributeTags = null,
        IReadOnlyDictionary<string, int>? attributeMasks = null)
    {
        if (context.SourceKind != InstantEditImportContext.GameSource ||
            context.DestinationState != InstantEditImportContext.NewModRequiredDestination)
            return new NewModelModResult(new ExportResult(false, "invalid_destination_state", "This import no longer requires a new Penumbra mod."));
        if (!IsSafeNewModName(modName))
            return new NewModelModResult(new ExportResult(false, "invalid_mod_name", "The Penumbra mod name is invalid."));
        var validationError = ValidateExportRequest(modName, context.GamePath, exportedFile);
        if (validationError is not null)
            return new NewModelModResult(new ExportResult(false, "invalid_export_file", validationError));

        await _exportGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var root = await _framework.RunOnFrameworkThread(GetModDirectory).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                return new NewModelModResult(new ExportResult(false, "penumbra_root_missing", "The Penumbra mod root is unavailable."));
            root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                return new NewModelModResult(new ExportResult(false, "penumbra_root_unsafe", "The Penumbra mod root is an unsupported linked directory."));

            var modList = await _framework.RunOnFrameworkThread(() =>
                TryGetModList(out var mods) ? mods : null).ConfigureAwait(false);
            if (modList is null)
                return new NewModelModResult(new ExportResult(false, "penumbra_unavailable", "Could not retrieve the Penumbra mod list."));
            var finalFolder = Path.GetFullPath(Path.Combine(root, modName));
            if (!IsPathWithin(finalFolder, root) || modList.Keys.Any(key =>
                    string.Equals(key, modName, StringComparison.OrdinalIgnoreCase)) ||
                modList.Values.Any(name => string.Equals(name, modName, StringComparison.OrdinalIgnoreCase)) ||
                Directory.Exists(finalFolder) || File.Exists(finalFolder))
                return new NewModelModResult(new ExportResult(false, "vanilla_mod_exists", "A Penumbra mod or folder with this name already exists."));

            var relativeModel = "Files/" + NormalizeGamePath(context.GamePath);
            var staging = Path.Combine(root, $".instant-edit-vanilla-{Guid.NewGuid():N}.tmp");
            var committed = false;
            try
            {
                Directory.CreateDirectory(staging);
                StageGameModelMod(
                    staging,
                    modName,
                    context.GamePath,
                    await File.ReadAllBytesAsync(exportedFile).ConfigureAwait(false));
                if (createAttributeGroups && attributeTags is { Count: > 0 })
                {
                    var attributeError = WriteAttributeGroups(
                        staging, context.ResolvedGamePath, attributeTags, attributeMasks,
                        context.ResourceManifest?.Manipulations);
                    if (attributeError is not null)
                        throw new InvalidDataException(attributeError);
                }
                Directory.Move(staging, finalFolder);
                committed = true;

                var warnings = new List<string>();
                try
                {
                    var addError = await AddNewModAsync(modName).ConfigureAwait(false);
                    if (addError is not null)
                        warnings.Add(addError.Message);
                    else if (context.TargetCollectionId is { } collectionId && collectionId != Guid.Empty)
                    {
                        var configure = await _framework.RunOnFrameworkThread(() => ConfigureModForCollectionOnFramework(
                            modName,
                            collectionId,
                            context.TargetCollectionName ?? "captured collection",
                            setPriority: true,
                            priority: 0,
                            redraw: false)).ConfigureAwait(false);
                        if (!configure.Success)
                            warnings.Add(configure.Message);
                        else
                            warnings.AddRange(configure.WarningList);
                    }
                    else
                    {
                        warnings.Add("The import's Penumbra collection was unavailable; enable the new mod manually.");
                    }

                    var redrawWarning = await _framework.RunOnFrameworkThread(
                        RedrawPlayerOwnedEntitiesOnFramework).ConfigureAwait(false);
                    if (redrawWarning is not null)
                        warnings.Add(redrawWarning);
                }
                catch (Exception e)
                {
                    warnings.Add($"The model mod was committed, but Penumbra setup failed: {e.Message}");
                }

                var targetFile = Path.Combine(finalFolder, relativeModel.Replace('/', Path.DirectorySeparatorChar));
                var stableId = ReadModStableIdentifier(finalFolder);
                return new NewModelModResult(new ExportResult(
                    true,
                    warnings.Count == 0 ? "vanilla_mod_created" : "vanilla_mod_created_with_warnings",
                    $"Created Penumbra model mod {modName}.",
                    warnings,
                    targetFile,
                    modName), finalFolder, relativeModel, stableId);
            }
            catch (Exception e)
            {
                if (Directory.Exists(staging))
                    TryDeleteMashupNamespace(root, staging);
                if (committed)
                {
                    var targetFile = Path.Combine(finalFolder, relativeModel.Replace('/', Path.DirectorySeparatorChar));
                    var stableId = ReadModStableIdentifier(finalFolder);
                    return new NewModelModResult(new ExportResult(
                        true,
                        "vanilla_mod_created_with_warnings",
                        $"Created Penumbra model mod {modName}.",
                        [$"The model mod was committed, but follow-up processing failed: {e.Message}"],
                        targetFile,
                        modName), finalFolder, relativeModel, stableId);
                }
                return new NewModelModResult(new ExportResult(false, "vanilla_mod_create_failed", e.Message));
            }
        }
        finally
        {
            _exportGate.Release();
        }
    }

    /// <summary>Get resource trees for explicit object-table indices.</summary>
    public ResourceTreeDto?[] GetResourceTrees(ushort[] gameObjectIndices)
    {
        if (gameObjectIndices.Length == 0)
            return Array.Empty<ResourceTreeDto?>();

        try
        {
            if (!Available)
                return Array.Empty<ResourceTreeDto?>();

            return _getObjectTrees.Invoke(withUiData: true, gameObjectIndices) ?? Array.Empty<ResourceTreeDto?>();
        }
        catch (Exception e)
        {
            _log.Debug($"Could not retrieve explicit resource trees: {e.Message}");
            return Array.Empty<ResourceTreeDto?>();
        }
    }

}
