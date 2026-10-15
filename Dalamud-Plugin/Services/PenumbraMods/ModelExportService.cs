using System.Text.Json.Nodes;
using Dalamud.Plugin.Services;
using InstantEdit.Models;
using static InstantEdit.Services.PenumbraMods.AttributeGroupService;
using static InstantEdit.Services.PenumbraMods.FrameworkThread;
using static InstantEdit.Services.PenumbraMods.MashupPlanning;
using static InstantEdit.Services.PenumbraMods.ModFiles;
using static InstantEdit.Services.PenumbraMods.ModMetadata;
using static InstantEdit.Services.PenumbraMods.ModOptionGroups;
using static InstantEdit.Services.PenumbraMods.ModPaths;
using static InstantEdit.Services.PenumbraMods.RaceExportService;

namespace InstantEdit.Services.PenumbraMods;

/// <summary>
/// Writes a Blender export back into Penumbra: over the imported model in its source mod, as a new
/// variant or option of it, or as the first mod of a game model.
/// </summary>
internal sealed class ModelExportService
{
    private readonly PenumbraIpc _ipc;
    private readonly IFramework _framework;
    private readonly IPluginLog _log;
    private readonly SemaphoreSlim _exportGate;
    private readonly ModelBackupStore _backups;
    private readonly SourceMods _sources;
    private readonly ModActivation _activation;
    private readonly ModelFileWriter _writer;
    private readonly AttributeGroupService _attributes;
    private readonly EstEntryService _est;
    private readonly RaceExportService _race;

    public ModelExportService(PenumbraIpc ipc, IFramework framework, IPluginLog log, SemaphoreSlim exportGate,
        ModelBackupStore backups, SourceMods sources, ModActivation activation, ModelFileWriter writer,
        AttributeGroupService attributes, EstEntryService est, RaceExportService race)
    {
        _ipc = ipc;
        _framework = framework;
        _log = log;
        _exportGate = exportGate;
        _backups = backups;
        _sources = sources;
        _activation = activation;
        _writer = writer;
        _attributes = attributes;
        _est = est;
        _race = race;
    }

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
        JsonArray? sourceManipulations = null,
        IReadOnlyList<EstEntryRequest>? estEntries = null)
    {
        resolvedGamePath ??= sourceGamePath;
        if (!IsSafeLocalModelPath(sourceFilePath))
            return new ExportResult(false, ResultCode.DestinationUnsafe, "The original Penumbra model destination is invalid.");
        // ValidateExportRequest also covers sourceModDirectory/sourceGamePath, so they aren't re-checked here.
        var validationError = ValidateExportRequest(sourceModDirectory, sourceGamePath, exportedFile);
        if (validationError is not null)
            return new ExportResult(false, ResultCode.InvalidExportFile, validationError);
        if (variantName is not null && !IsSafeVariantName(variantName))
            return new ExportResult(false, ResultCode.InvalidVariantName, "Invalid variant name.");
        if (variantName is not null && string.Equals(
                variantName,
                Path.GetFileNameWithoutExtension(sourceGamePath),
                StringComparison.OrdinalIgnoreCase))
            return new ExportResult(false, ResultCode.InvalidVariantName, "Variant name must differ from the originally imported model name.");
        if (setupVariantInPenumbra && variantName is null && !string.Equals(variantTarget, "option", StringComparison.Ordinal))
            return new ExportResult(false, ResultCode.InvalidVariant, "Penumbra variant setup requires Save as Variant.");
        if (setupVariantInPenumbra && !string.Equals(variantTarget, "option", StringComparison.Ordinal) &&
            !string.Equals(variantTarget, "group", StringComparison.Ordinal) && !IsSafeVariantGroupName(variantGroupName))
            return new ExportResult(false, ResultCode.InvalidVariantGroup, "Penumbra variant setup requires an option group name.");
        if (setupVariantInPenumbra && variantTarget == "group" && string.IsNullOrWhiteSpace(variantTargetId))
            return new ExportResult(false, ResultCode.StaleVariantTarget, "The selected Penumbra group is invalid.");

        await _exportGate.WaitAsync().ConfigureAwait(false);
        string? committedTarget = null;
        try
        {
            var resolved = await _framework.RunOnFrameworkThread(
                () => _sources.ResolveSourceModTargetOnFramework(
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
            await LeaveFrameworkThread();
            var meta = LoadV4ModMetadata(resolved.Target.Folder);
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
            // A new variant gets its own file; never overwrite a model the mod already uses.
            else if (variantName is not null && File.Exists(targetFile) &&
                     (PathRules.SamePhysicalPath(targetFile, resolved.Target.FilePath) ||
                      IsMappedOutsideOption(meta, resolved.Target.Folder, targetFile, variantName)))
                return new ExportResult(false, ResultCode.InvalidVariantName,
                    $"The mod already uses {variantName}.mdl. Choose a different variant name.");
            JsonObject? sourceOptionTemplate = null;
            if (setupVariantInPenumbra && !string.Equals(variantTarget, "option", StringComparison.Ordinal))
            {
                if (sourceOptionStatus == "ambiguous")
                    return new ExportResult(false, ResultCode.AmbiguousSourceOption,
                        "Multiple Penumbra options provide the imported model. Re-import it from the intended option, then export again.");
                if (sourceOptionStatus != "default")
                {
                    var source = ResolveSourceOption(resolved.Target.Folder, sourceGamePath,
                        resolved.Target.RelativePath, sourceOption);
                    if (source.Error is not null)
                        return new ExportResult(false, ResultCode.SourceOptionUnavailable, source.Error);
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
                        variantTarget == "group" ? ResultCode.StaleVariantTarget : ResultCode.InvalidVariantGroup, groupError);
            }
            var targetRelative = Path.GetRelativePath(resolved.Target.Folder, targetFile).Replace('\\', '/');
            // The backup keeps the model's EST entries too, so restoring it undoes this export's EST change.
            var estState = backupExisting && File.Exists(targetFile)
                ? _est.CaptureEstState(resolved.Target.Folder, resolved.Target.Directory, targetRelative)
                : null;
            var writeError = _writer.WriteModelToOriginalLocation(
                resolved.Target.Folder,
                targetFile,
                exportedFile,
                backupExisting,
                resolved.Target.Directory,
                targetRelative,
                estState);
            if (writeError is not null)
                return new ExportResult(false, ResultCode.WriteFailed, writeError);
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
                var attributeError = _attributes.WriteAttributeGroups(
                    resolved.Target.Folder, resolvedGamePath, attributeTags, attributeMasks,
                    sourceManipulations);
                if (attributeError is not null)
                    warnings.Add($"Penumbra attribute group setup failed: {AttributeGroupErrorMessage(attributeError)}");
            }

            warnings.AddRange(_est.TryUpdateEstEntries(
                resolved.Target.Folder, resolved.Target.Directory, targetRelative, estEntries));

            var reloadError = await _framework.RunOnFrameworkThread(
                () => _activation.ReloadModOnFramework(resolved.Target.Directory)).ConfigureAwait(false);
            if (reloadError is not null)
                warnings.Add(reloadError.Message);
            else
            {
                var redrawWarning = await _framework.RunOnFrameworkThread(
                    _activation.RedrawPlayerOwnedEntitiesOnFramework).ConfigureAwait(false);
                if (redrawWarning is not null)
                    warnings.Add(redrawWarning);
            }

            var code = warnings.Count == 0 ? ResultCode.ExportApplied : ResultCode.ExportAppliedWithWarnings;
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
                    ResultCode.ExportAppliedWithWarnings,
                    $"Exported to {committedTarget}; follow-up processing failed.",
                    [$"Follow-up processing failed: {e.Message}"],
                    committedTarget);
            return new ExportResult(false, ResultCode.WriteFailed, $"Failed before the model write could be committed: {e.Message}");
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
            return new VariantTargetsResult(false, ResultCode.DestinationUnsafe, "The original Penumbra model destination is invalid.", []);

        await _exportGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var resolved = await _framework.RunOnFrameworkThread(
                () => _sources.ResolveSourceModTargetOnFramework(
                    sourceModDirectory, sourceFilePath, sourceModRootPath, targetRelativePath, sourceModStableId)).ConfigureAwait(false);
            if (resolved.Target is null)
                return new VariantTargetsResult(false, resolved.Code,
                    resolved.Error ?? "The original Penumbra mod is no longer available.", []);
            await LeaveFrameworkThread();
            return new VariantTargetsResult(true, ResultCode.VariantTargetsLoaded, "Compatible Penumbra targets loaded.",
                ReadVariantTargets(resolved.Target.Folder, sourceGamePath, resolved.Target.Directory, _backups));
        }
        catch (Exception e)
        {
            _log.Error(e, "Failed to read Penumbra variant targets.");
            return new VariantTargetsResult(false, ResultCode.VariantTargetsUnavailable, "Could not read Penumbra option groups.", []);
        }
        finally
        {
            _exportGate.Release();
        }
    }

    /// <summary>
    /// Create the first persistent Penumbra destination for a game-data import. With <paramref name="race"/>
    /// the model is that race's own copy (see RaceModelGamePath), with the EQDP entry that makes the race load it.
    /// </summary>
    public async Task<NewModelModResult> CreateGameModelModAsync(
        InstantEditImportContext context,
        string exportedFile,
        string modName,
        bool createAttributeGroups = false,
        IReadOnlyList<string>? attributeTags = null,
        IReadOnlyDictionary<string, int>? attributeMasks = null,
        IReadOnlyList<EstEntryRequest>? estEntries = null,
        int? race = null)
    {
        if (context.SourceKind != InstantEditImportContext.GameSource ||
            context.DestinationState != InstantEditImportContext.NewModRequiredDestination)
            return new NewModelModResult(new ExportResult(false, ResultCode.InvalidDestinationState, "This import no longer requires a new Penumbra mod."));
        if (!IsSafeNewModName(modName))
            return new NewModelModResult(new ExportResult(false, ResultCode.InvalidModName, "The Penumbra mod name is invalid."));
        var validationError = ValidateExportRequest(modName, context.GamePath, exportedFile);
        if (validationError is not null)
            return new NewModelModResult(new ExportResult(false, ResultCode.InvalidExportFile, validationError));
        var modelGamePath = context.GamePath;
        if (race is { } raceCode)
        {
            if (RaceModelGamePath(context.GamePath, raceCode) is not { } racePath)
                return new NewModelModResult(new ExportResult(false, ResultCode.RaceModelUnavailable,
                    "Only gear and accessory models can be saved for another race."));
            modelGamePath = racePath;
        }

        await _exportGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var root = await _framework.RunOnFrameworkThread(_ipc.GetModDirectory).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                return new NewModelModResult(new ExportResult(false, ResultCode.PenumbraRootMissing, "The Penumbra mod root is unavailable."));
            root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                return new NewModelModResult(new ExportResult(false, ResultCode.PenumbraRootUnsafe, "The Penumbra mod root is an unsupported linked directory."));

            var modList = await _framework.RunOnFrameworkThread(() =>
                _ipc.TryGetModList(out var mods) ? mods : null).ConfigureAwait(false);
            if (modList is null)
                return new NewModelModResult(new ExportResult(false, ResultCode.PenumbraUnavailable, "Could not retrieve the Penumbra mod list."));
            await LeaveFrameworkThread();
            var finalFolder = Path.GetFullPath(Path.Combine(root, modName));
            if (!IsPathWithin(finalFolder, root) || modList.Keys.Any(key =>
                    string.Equals(key, modName, StringComparison.OrdinalIgnoreCase)) ||
                modList.Values.Any(name => string.Equals(name, modName, StringComparison.OrdinalIgnoreCase)) ||
                Directory.Exists(finalFolder) || File.Exists(finalFolder))
                return new NewModelModResult(new ExportResult(false, ResultCode.VanillaModExists, "A Penumbra mod or folder with this name already exists."));

            var relativeModel = "Files/" + NormalizeGamePath(modelGamePath);
            var staging = Path.Combine(root, $".instant-edit-vanilla-{Guid.NewGuid():N}.tmp");
            var committed = false;
            try
            {
                Directory.CreateDirectory(staging);
                StageGameModelMod(
                    staging,
                    modName,
                    modelGamePath,
                    await File.ReadAllBytesAsync(exportedFile).ConfigureAwait(false));
                if (race is not null)
                    _race.AddRaceModelEqdp(staging, modelGamePath, race.Value);
                if (createAttributeGroups && attributeTags is { Count: > 0 })
                {
                    // A new mod of a game model has no variant settings of its own to keep.
                    var attributeError = _attributes.WriteAttributeGroups(
                        staging, context.ResolvedGamePath, attributeTags, attributeMasks,
                        KnownManipulations(context));
                    if (attributeError is not null)
                        throw new InvalidDataException(attributeError);
                }
                // The staged meta.json already has the mod's identifier, which keys the EST records.
                var estWarnings = _est.TryUpdateEstEntries(staging, modName, relativeModel, estEntries);
                Directory.Move(staging, finalFolder);
                committed = true;

                var warnings = new List<string>(estWarnings);
                try
                {
                    var addError = await _activation.AddNewModAsync(modName).ConfigureAwait(false);
                    if (addError is not null)
                        warnings.Add(addError.Message);
                    else if (context.TargetCollectionId is { } collectionId && collectionId != Guid.Empty)
                    {
                        var configure = await _framework.RunOnFrameworkThread(() => _activation.ConfigureModForCollectionOnFramework(
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
                        _activation.RedrawPlayerOwnedEntitiesOnFramework).ConfigureAwait(false);
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
                    warnings.Count == 0 ? ResultCode.VanillaModCreated : ResultCode.VanillaModCreatedWithWarnings,
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
                        ResultCode.VanillaModCreatedWithWarnings,
                        $"Created Penumbra model mod {modName}.",
                        [$"The model mod was committed, but follow-up processing failed: {e.Message}"],
                        targetFile,
                        modName), finalFolder, relativeModel, stableId);
                }
                return new NewModelModResult(new ExportResult(false, ResultCode.VanillaModCreateFailed, e.Message));
            }
        }
        finally
        {
            _exportGate.Release();
        }
    }

}
