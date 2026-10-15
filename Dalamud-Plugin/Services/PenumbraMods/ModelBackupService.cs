using System.Text.Json.Nodes;
using Dalamud.Plugin.Services;
using static InstantEdit.Services.PenumbraMods.FrameworkThread;
using static InstantEdit.Services.PenumbraMods.ModelFileWriter;
using static InstantEdit.Services.PenumbraMods.ModOptionGroups;
using static InstantEdit.Services.PenumbraMods.ModPaths;

namespace InstantEdit.Services.PenumbraMods;

/// <summary>
/// The managed backups of a source model: restoring one over the model, with the EST entries and race
/// export changes it carries, and clearing them.
/// </summary>
internal sealed class ModelBackupService
{
    private readonly IFramework _framework;
    private readonly IPluginLog _log;
    private readonly SemaphoreSlim _exportGate;
    private readonly ModelBackupStore _backups;
    private readonly SourceMods _sources;
    private readonly ModActivation _activation;
    private readonly ModelFileWriter _writer;
    private readonly EstEntryService _est;
    private readonly RaceExportService _race;

    public ModelBackupService(IFramework framework, IPluginLog log, SemaphoreSlim exportGate, ModelBackupStore backups,
        SourceMods sources, ModActivation activation, ModelFileWriter writer, EstEntryService est, RaceExportService race)
    {
        _framework = framework;
        _log = log;
        _exportGate = exportGate;
        _backups = backups;
        _sources = sources;
        _activation = activation;
        _writer = writer;
        _est = est;
        _race = race;
    }

    /// <summary>Restore a timestamped MDL backup beside an authorized source model.</summary>
    public async Task<ExportResult> RestoreSourceBackupAsync(
        string sourceModDirectory,
        string sourceFilePath,
        string? sourceModRootPath,
        string? targetRelativePath,
        string sourceGamePath,
        string backupName,
        string backupTargetId,
        Guid? sourceModStableId = null)
    {
        if (!IsSafeModName(sourceModDirectory) || !IsSafeGamePath(sourceGamePath) ||
            !IsSafeLocalModelPath(sourceFilePath) || !TryGetBackupOriginal(backupName, out var originalName))
            return new ExportResult(false, "invalid_restore", "The backup restore request is invalid.");

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
            if (!string.Equals(Path.GetExtension(originalName), ".mdl", StringComparison.OrdinalIgnoreCase))
                return new ExportResult(false, "invalid_restore", "The backup target is invalid.");
            if (targetRelativePath is null)
                return new ExportResult(false, "backup_unavailable", "Managed backup storage is unavailable.");
            var effectiveRelativePath = targetRelativePath;
            var targetPath = resolved.Target.FilePath;
            var expectedTarget = _backups.Describe(sourceModDirectory, effectiveRelativePath);
            if (!string.Equals(expectedTarget.Id, backupTargetId, StringComparison.Ordinal))
            {
                var option = ReadVariantTargets(resolved.Target.Folder, sourceGamePath, sourceModDirectory, _backups)
                    .SelectMany(group => group.Options)
                    // Options that share one model file share its backups, so any of them will do.
                    .FirstOrDefault(candidate => string.Equals(candidate.BackupTargetId, backupTargetId, StringComparison.Ordinal));
                if (option is null)
                    return new ExportResult(false, "invalid_backup_target", "The backup does not belong to this export context.");
                effectiveRelativePath = option.ModelPath;
                targetPath = Path.GetFullPath(Path.Combine(resolved.Target.Folder,
                    effectiveRelativePath.Replace('/', Path.DirectorySeparatorChar)));
                expectedTarget = _backups.Describe(sourceModDirectory, effectiveRelativePath);
            }
            var parent = Path.GetDirectoryName(targetPath)!;
            var backupPath = _backups.Resolve(backupTargetId, backupName);
            if (!string.Equals(Path.GetFileName(targetPath), originalName, StringComparison.OrdinalIgnoreCase) ||
                new FileInfo(backupPath).Length == 0)
                return new ExportResult(false, "backup_missing", "The managed backup does not match the authorized model.");
            if (HasReparsePointInPath(resolved.Target.Folder, parent) ||
                (File.Exists(backupPath) && (File.GetAttributes(backupPath) & FileAttributes.ReparsePoint) != 0) ||
                (File.Exists(targetPath) && (File.GetAttributes(targetPath) & FileAttributes.ReparsePoint) != 0))
                return new ExportResult(false, "destination_unsafe", "The backup target contains an unsupported reparse point.");

            // The hair's EST entries go with the model both ways: the backup taken of the
            // current file keeps today's, and the restored backup's own are put back.
            JsonObject? currentEst = null;
            try
            {
                currentEst = _est.CaptureEstState(resolved.Target.Folder, resolved.Target.Directory, effectiveRelativePath);
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                _log.Warning(e, "Could not read the EST entries to keep with the pre-restore backup.");
            }
            var restoredEst = _backups.ReadEstState(backupPath);
            // A race export's restore point also undoes what the export changed in the mod's options.
            var raceState = _backups.ReadRaceState(backupPath);
            var writeError = _writer.RestoreModelBackup(targetPath, backupPath, sourceModDirectory, effectiveRelativePath, currentEst);
            if (writeError is not null)
                return new ExportResult(false, "restore_write_failed", writeError);
            committedTarget = targetPath;

            var warnings = new List<string>();
            if (restoredEst is not null)
            {
                try
                {
                    warnings.AddRange(_est.RestoreEstState(resolved.Target.Folder, resolved.Target.Directory, restoredEst));
                }
                catch (Exception e)
                {
                    _log.Error(e, "Could not restore the backup's EST entries.");
                    warnings.Add($"The backup's EST entries could not be restored: {e.Message}");
                }
            }
            if (raceState is not null)
            {
                try
                {
                    warnings.AddRange(_race.UndoRaceExport(resolved.Target.Folder, resolved.Target.Directory, raceState));
                }
                catch (Exception e)
                {
                    _log.Error(e, "Could not undo the race export's option changes.");
                    warnings.Add($"The race model's options could not be restored: {e.Message}");
                }
            }

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

            var code = warnings.Count == 0 ? "backup_restored" : "backup_restored_with_warnings";
            var message = warnings.Count == 0
                ? $"Restored {originalName} and reloaded {resolved.Target.Directory}."
                : $"Restored {originalName}; {warnings.Count} follow-up warning(s).";
            return new ExportResult(true, code, message, warnings, targetPath);
        }
        catch (Exception e)
        {
            _log.Error(e, "Failed to restore a model backup.");
            if (committedTarget is not null)
                return new ExportResult(
                    true,
                    "backup_restored_with_warnings",
                    $"Restored {originalName}; follow-up processing failed.",
                    [$"Follow-up processing failed: {e.Message}"],
                    committedTarget);
            return new ExportResult(false, "restore_failed", $"Failed to restore the model backup: {e.Message}");
        }
        finally
        {
            _exportGate.Release();
        }
    }

    public async Task<ExportResult> ClearManagedBackupsAsync(
        string sourceModDirectory, string sourceFilePath, string? sourceModRootPath,
        string? targetRelativePath, string sourceGamePath, string backupTargetId,
        Guid? sourceModStableId = null)
    {
        if (targetRelativePath is null)
            return new ExportResult(false, "backup_unavailable", "Managed backup storage is unavailable.");
        await _exportGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var resolved = await _framework.RunOnFrameworkThread(() => _sources.ResolveSourceModTargetOnFramework(
                sourceModDirectory, sourceFilePath, sourceModRootPath, targetRelativePath, sourceModStableId)).ConfigureAwait(false);
            if (resolved.Target is null)
                return new ExportResult(false, resolved.Code, resolved.Error ?? "The source mod is unavailable.");
            await LeaveFrameworkThread();
            var validIds = ReadVariantTargets(resolved.Target.Folder, sourceGamePath, sourceModDirectory, _backups)
                .SelectMany(group => group.Options)
                .Select(option => option.BackupTargetId)
                .Where(id => id is not null)
                .Append(_backups.Describe(sourceModDirectory, targetRelativePath).Id);
            if (!validIds.Contains(backupTargetId, StringComparer.Ordinal))
                return new ExportResult(false, "invalid_backup_target", "The backup target does not belong to this context.");
            _backups.Clear(backupTargetId);
            return new ExportResult(true, "backups_cleared", "Managed backups cleared.");
        }
        catch (Exception error)
        {
            return new ExportResult(false, "backup_clear_failed", error.Message);
        }
        finally
        {
            _exportGate.Release();
        }
    }

}
