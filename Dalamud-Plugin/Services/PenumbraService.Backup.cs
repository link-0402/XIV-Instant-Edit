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

            await LeaveFrameworkThread();
            if (!string.Equals(Path.GetExtension(originalName), ".mdl", StringComparison.OrdinalIgnoreCase))
                return new ExportResult(false, "invalid_restore", "The backup target is invalid.");
            if (_backups is null || targetRelativePath is null)
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

            var writeError = RestoreModelBackup(targetPath, backupPath, sourceModDirectory, effectiveRelativePath);
            if (writeError is not null)
                return new ExportResult(false, "restore_write_failed", writeError);
            committedTarget = targetPath;

            var warnings = new List<string>();

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

    private string? RestoreModelBackup(string targetFile, string backupFile, string modDirectory, string targetRelativePath)
    {
        try
        {
            if (File.Exists(targetFile))
                _backups?.Create(targetFile, modDirectory, targetRelativePath);
            var temporary = Path.Combine(Path.GetDirectoryName(targetFile)!, $".instant-edit-restore-{Guid.NewGuid():N}.tmp");
            try
            {
                File.Copy(backupFile, temporary, false);
                File.Move(temporary, targetFile, true);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
            return null;
        }
        catch (Exception e)
        {
            return $"Could not restore the model backup: {e.Message}";
        }
    }

    private static bool TryGetBackupOriginal(string? backupName, out string originalName)
    {
        originalName = "";
        if (string.IsNullOrWhiteSpace(backupName) || backupName.Length > 512 ||
            backupName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || backupName.Contains('/') || backupName.Contains('\\'))
            return false;
        var match = Regex.Match(
            backupName,
            @"^(?<original>.+\.mdl)\.\d{8}T\d{6}\.\d{6}Z\.bak$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (match.Success)
            originalName = match.Groups["original"].Value;
        else if (backupName.EndsWith(".mdl.bak", StringComparison.OrdinalIgnoreCase))
            originalName = backupName[..^4];
        else
            return false;
        return originalName.Length <= 255 && originalName is not ".mdl" and not "." and not "..";
    }

    private ExportResult? ReloadModOnFramework(string modName)
    {
        PenumbraApiEc modResult;
        try
        {
            modResult = _reloadMod.Invoke(modName);
        }
        catch (Exception e)
        {
            _log.Error(e, "Penumbra failed while reloading the XIV Instant Edit mod.");
            return new ExportResult(false, $"Penumbra add/reload failed: {e.Message}");
        }

        if (modResult is not (PenumbraApiEc.Success or PenumbraApiEc.NothingChanged))
            return new ExportResult(false, $"Penumbra rejected the mod ({modResult}).");

        return null;
    }

    private async Task<ExportResult?> AddNewModAsync(string modName)
    {
        var added = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventSubscriber<string>? subscription = null;

        try
        {
            subscription = ModAdded.Subscriber(_pi, directory =>
            {
                if (string.Equals(directory, modName, StringComparison.OrdinalIgnoreCase))
                    added.TrySetResult(directory);
            });
            subscription.Enable();

            // AddMod only queues discovery. Do not wait for its event from this
            // callback; the continuation waits on a thread-pool context instead.
            var addResult = await _framework.RunOnFrameworkThread(
                () => AddModOnFramework(modName)).ConfigureAwait(false);
            if (addResult is not (PenumbraApiEc.Success or PenumbraApiEc.NothingChanged))
                return new ExportResult(false, $"Penumbra rejected adding the mod ({addResult}).");

            try
            {
                await added.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return new ExportResult(false, $"Timed out waiting for Penumbra to add '{modName}'.");
            }

            return null;
        }
        catch (Exception e)
        {
            _log.Error(e, "Penumbra failed while adding the XIV Instant Edit mod.");
            return new ExportResult(false, $"Penumbra add failed: {e.Message}");
        }
        finally
        {
            if (subscription is not null)
            {
                try
                {
                    subscription.Disable();
                }
                catch (Exception e)
                {
                    _log.Debug($"Could not disable Penumbra mod-added subscription: {e.Message}");
                }

                try
                {
                    subscription.Dispose();
                }
                catch (Exception e)
                {
                    _log.Debug($"Could not dispose Penumbra mod-added subscription: {e.Message}");
                }
            }
        }
    }

    private PenumbraApiEc AddModOnFramework(string modName)
        => _addMod.Invoke(modName);

    private ExportResult ConfigureModOnFramework(
        string modName,
        int objectIndex,
        bool setPriority = true,
        int priority = int.MaxValue)
    {

        (bool ObjectValid, bool IndividualSet, (Guid Id, string Name) EffectiveCollection) collection;
        try
        {
            collection = _getCollectionForObject.Invoke(objectIndex);
        }
        catch (Exception e)
        {
            _log.Error(e, "Could not retrieve the target Penumbra collection.");
            return new ExportResult(false, $"Could not retrieve the target collection: {e.Message}");
        }

        if (!collection.ObjectValid || collection.EffectiveCollection.Id == Guid.Empty ||
            string.IsNullOrWhiteSpace(collection.EffectiveCollection.Name))
            return new ExportResult(false, "The target object has no valid Penumbra collection.");

        return ConfigureModForCollectionOnFramework(
            modName,
            collection.EffectiveCollection.Id,
            collection.EffectiveCollection.Name,
            setPriority,
            priority);
    }

    private ExportResult ConfigureModForCollectionOnFramework(
        string modName,
        Guid collectionId,
        string collectionName,
        bool setPriority = true,
        int priority = int.MaxValue,
        bool redraw = true)
        => ApplyModToCollection(
            modName,
            collectionName,
            () => _trySetMod.Invoke(collectionId, modName, true, modName),
            setPriority ? () => _trySetModPriority.Invoke(collectionId, modName, priority, modName) : null,
            redraw ? RedrawPlayerOwnedEntitiesOnFramework : null,
            (message, error) => _log.Error(error, message));

    /// <summary>
    /// Enables a mod in a collection, optionally sets its priority, and optionally redraws the
    /// player-owned entities afterwards. The steps are delegates so the sequencing can be
    /// regression-tested without Penumbra: skipping the redraw must never skip the enable.
    /// </summary>
    internal static ExportResult ApplyModToCollection(
        string modName,
        string collectionName,
        Func<PenumbraApiEc> enable,
        Func<PenumbraApiEc>? setPriority,
        Func<string?>? redraw,
        Action<string, Exception>? logError = null)
    {
        PenumbraApiEc enabledResult;
        try
        {
            enabledResult = enable();
        }
        catch (Exception e)
        {
            logError?.Invoke("Could not enable the XIV Instant Edit mod in Penumbra.", e);
            return new ExportResult(false, $"Penumbra enable failed: {e.Message}");
        }

        if (enabledResult is not (PenumbraApiEc.Success or PenumbraApiEc.NothingChanged))
            return new ExportResult(false, $"Penumbra rejected enabling the mod ({enabledResult}).");

        if (setPriority is not null)
        {
            PenumbraApiEc priorityResult;
            try
            {
                priorityResult = setPriority();
            }
            catch (Exception e)
            {
                logError?.Invoke("Could not prioritize the XIV Instant Edit mod in Penumbra.", e);
                return new ExportResult(false, $"Penumbra priority failed: {e.Message}");
            }

            if (priorityResult is not (PenumbraApiEc.Success or PenumbraApiEc.NothingChanged))
                return new ExportResult(false, $"Penumbra rejected the mod priority ({priorityResult}).");
        }

        var applied = $"Applied {modName} to {collectionName}.";
        if (redraw is null)
            return new ExportResult(true, applied);

        try
        {
            var redrawWarning = redraw();
            return redrawWarning is null
                ? new ExportResult(true, applied)
                : new ExportResult(true, "export_applied_with_warnings", applied, [redrawWarning]);
        }
        catch (Exception e)
        {
            logError?.Invoke("Penumbra redraw failed after applying export.", e);
            return new ExportResult(false, $"Penumbra redraw failed: {e.Message}");
        }
    }

    /// <summary> Reads the mod list, distinguishing an IPC failure from an empty list. </summary>
    internal bool TryGetModList(out Dictionary<string, string> modList)
    {
        try
        {
            modList = _getModList.Invoke() ?? new Dictionary<string, string>();
            return true;
        }
        catch (Exception e)
        {
            _log.Error(e, "Could not retrieve the Penumbra mod list.");
            modList = new Dictionary<string, string>();
            return false;
        }
    }

    public async Task<ExportResult> ClearManagedBackupsAsync(
        string sourceModDirectory, string sourceFilePath, string? sourceModRootPath,
        string? targetRelativePath, string sourceGamePath, string backupTargetId,
        Guid? sourceModStableId = null)
    {
        if (_backups is null || targetRelativePath is null)
            return new ExportResult(false, "backup_unavailable", "Managed backup storage is unavailable.");
        await _exportGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var resolved = await _framework.RunOnFrameworkThread(() => ResolveSourceModTargetOnFramework(
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
