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
    private SourceTargetResolution ResolveSourceModTargetOnFramework(
        string sourceModDirectory,
        string sourceFilePath,
        string? sourceModRootPath,
        string? targetRelativePath,
        Guid? sourceModStableId = null)
    {
        if (!TryGetModList(out var modList))
            return new SourceTargetResolution(null, "penumbra_unavailable", "Could not retrieve the Penumbra mod list.");

        if (!TryResolveRegisteredModIdentity(
                modList,
                sourceModDirectory,
                sourceModStableId,
                sourceModRootPath,
                out var registeredDirectory,
                out var identityError))
            return new SourceTargetResolution(null, identityError.Code, identityError.Message);

        try
        {
            var configuredRoot = GetModDirectory();
            return ResolveSourceModTargetFromRoots(
                registeredDirectory,
                sourceFilePath,
                sourceModRootPath,
                targetRelativePath,
                GetRegisteredModPath(registeredDirectory),
                string.IsNullOrWhiteSpace(configuredRoot)
                    ? null
                    : Path.Combine(configuredRoot, registeredDirectory));
        }
        catch (Exception e)
        {
            return new SourceTargetResolution(
                null,
                "destination_unavailable",
                $"Could not validate the original model destination: {e.Message}");
        }
    }

    internal static SourceTargetResolution ResolveSourceModTargetFromRoots(
        string registeredDirectory,
        string sourceFilePath,
        string? sourceModRootPath,
        string? targetRelativePath,
        string? currentRegisteredRoot,
        string? configuredFallbackRoot)
    {
        try
        {
            var roots = new List<(string Root, bool Preferred)>();
            AddExportRoot(roots, currentRegisteredRoot, true, targetRelativePath);
            AddExportRoot(roots, configuredFallbackRoot, false, targetRelativePath);
            AddExportRoot(roots, sourceModRootPath, false, targetRelativePath);

            var relative = NormalizeRelativeModelPath(targetRelativePath);
            if (!string.IsNullOrWhiteSpace(targetRelativePath) && relative is null)
                return new SourceTargetResolution(
                    null,
                    "destination_unsafe",
                    "The authorized target-relative model path is unsafe.");
            if (relative is null && !string.IsNullOrWhiteSpace(sourceModRootPath) &&
                TryRelativeModelPath(sourceFilePath, sourceModRootPath, out var storedRelative))
                relative = storedRelative;
            if (relative is null)
            {
                foreach (var root in roots)
                {
                    if (TryRelativeModelPath(sourceFilePath, root.Root, out var candidateRelative))
                    {
                        relative = candidateRelative;
                        break;
                    }
                }
            }
            if (relative is null)
                return new SourceTargetResolution(
                    null,
                    "destination_unresolvable",
                    "The saved context has no safe model path relative to the registered mod.");

            var valid = new List<(SourceModTarget Target, bool Preferred)>();
            var sawMissingFolder = false;
            var sawUnsafe = false;
            // Once Penumbra resolves the registered directory key to an existing
            // root, that root is authoritative. If the reported root itself has
            // disappeared, retain the configured/captured roots as recovery
            // candidates; otherwise a stale IPC path makes every unchanged import
            // fail with destination_missing. A preferred root that still exists
            // but lacks the destination remains authoritative and cannot fall back.
            var resolutionRoots = roots.Any(candidate => candidate.Preferred && Directory.Exists(candidate.Root))
                ? roots.Where(candidate => candidate.Preferred)
                : roots;
            foreach (var candidate in resolutionRoots)
            {
                if (!Directory.Exists(candidate.Root))
                    continue;
                var target = Path.GetFullPath(Path.Combine(
                    candidate.Root,
                    relative.Replace('/', Path.DirectorySeparatorChar)));
                var parent = Path.GetDirectoryName(target);
                if (parent is null || !IsPathWithin(target, candidate.Root))
                {
                    sawUnsafe = true;
                    continue;
                }
                if (!Directory.Exists(parent))
                {
                    sawMissingFolder = true;
                    continue;
                }
                if (HasReparsePointInPath(candidate.Root, parent) ||
                    (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0))
                {
                    sawUnsafe = true;
                    continue;
                }
                valid.Add((
                    new SourceModTarget(registeredDirectory, candidate.Root, target, relative),
                    candidate.Preferred));
            }

            var preferred = valid.Where(item => item.Preferred).Select(item => item.Target).FirstOrDefault();
            if (preferred is not null)
                return new SourceTargetResolution(preferred, "accepted", null);
            var distinct = valid
                .Select(item => item.Target)
                .DistinctBy(item => item.FilePath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (distinct.Length == 1)
                return new SourceTargetResolution(distinct[0], "accepted", null);
            if (distinct.Length > 1)
                return new SourceTargetResolution(
                    null,
                    "destination_ambiguous",
                    "Multiple registered mod roots contain the authorized destination.");
            if (sawUnsafe)
                return new SourceTargetResolution(
                    null,
                    "destination_unsafe",
                    "The original model path contains an unsupported reparse point or escapes the mod root.");
            return new SourceTargetResolution(
                null,
                "destination_missing",
                sawMissingFolder
                    ? "The original Penumbra destination folder is no longer available."
                    : "The registered Penumbra mod root is no longer available.");
        }
        catch (Exception e)
        {
            return new SourceTargetResolution(
                null,
                "destination_unavailable",
                $"Could not validate the original model destination: {e.Message}");
        }
    }

    /// <summary>Resolve the root currently registered for a Penumbra directory key.</summary>
    public string? GetRegisteredModPath(string modDirectory)
    {
        if (!IsSafeModName(modDirectory))
            return null;
        try
        {
            var result = _getModPath.Invoke(modDirectory, string.Empty);
            if (result.Item1 is not PenumbraApiEc.Success)
                return null;
            var path = NormalizePhysicalPath(result.Item2);
            if (path is null)
                return null;
            return string.Equals(Path.GetFileName(path), "Files", StringComparison.OrdinalIgnoreCase)
                ? Directory.GetParent(path)?.FullName ?? path
                : path;
        }
        catch (Exception e)
        {
            _log.Debug($"Could not retrieve the registered path for {modDirectory}: {e.Message}");
            return null;
        }
    }

    private static void AddExportRoot(
        List<(string Root, bool Preferred)> roots,
        string? value,
        bool preferred,
        string? targetRelativePath)
    {
        var normalized = NormalizePhysicalPath(value);
        if (normalized is null)
            return;

        // Some Penumbra layouts expose the Files directory as the mod path.
        // The durable relative path is rooted at the folder containing mod
        // metadata, so normalize that representation back to the mod root.
        if (string.Equals(Path.GetFileName(normalized), "Files", StringComparison.OrdinalIgnoreCase) &&
            NormalizeRelativeModelPath(targetRelativePath)?.StartsWith("Files/", StringComparison.OrdinalIgnoreCase) == true)
            normalized = Directory.GetParent(normalized)?.FullName ?? normalized;

        var existing = roots.FindIndex(item => string.Equals(item.Root, normalized, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
        {
            if (preferred && !roots[existing].Preferred)
                roots[existing] = (normalized, true);
            return;
        }
        roots.Add((normalized, preferred));
    }

    private static bool TryRelativeModelPath(string filePath, string root, out string relative)
    {
        relative = string.Empty;
        try
        {
            var fullFile = Path.GetFullPath(filePath);
            var fullRoot = Path.GetFullPath(root);
            if (!IsPathWithin(fullFile, fullRoot))
                return false;
            var candidate = Path.GetRelativePath(fullRoot, fullFile).Replace('\\', '/');
            var normalized = NormalizeRelativeModelPath(candidate);
            if (normalized is null)
                return false;
            relative = normalized;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? NormalizeRelativeModelPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var normalized = value.Replace('\\', '/').Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        normalized = normalized.TrimStart('/');
        return IsSafeRelativeModelPath(normalized) ? normalized : null;
    }

    /// <summary>Redraw the local player and the currently spawned entities they own.</summary>
    private string? RedrawPlayerOwnedEntitiesOnFramework()
    {
        if (_objects is null || _objects.LocalPlayer is not { Address: not 0 } localPlayer)
            return "The local player could not be found; player-owned redraw was skipped.";

        var targets = new HashSet<ushort> { localPlayer.ObjectIndex };
        var warnings = new List<string>();
        try
        {
            if (localPlayer.EntityId != 0)
            {
                var localOwnerId = localPlayer.EntityId;
                foreach (var candidate in _objects)
                {
                    if (candidate is null || candidate.Address == nint.Zero ||
                        candidate.ObjectIndex == localPlayer.ObjectIndex ||
                        candidate.OwnerId != localOwnerId)
                        continue;

                    if (candidate.ObjectKind is ObjectKind.Companion or ObjectKind.Mount or ObjectKind.FollowMount ||
                        candidate is IBattleNpc { BattleNpcKind: BattleNpcSubKind.Pet })
                        targets.Add(candidate.ObjectIndex);
                }
            }
            else
            {
                warnings.Add("The local player entity ID was unavailable; only the player was redrawn.");
            }
        }
        catch (Exception e)
        {
            _log.Error(e, "Could not enumerate player-owned entities for redraw.");
            warnings.Add("Could not enumerate all player-owned entities; continuing with the targets found.");
        }

        var failed = 0;
        foreach (var objectIndex in targets)
        {
            try
            {
                _redrawObject.Invoke(objectIndex);
            }
            catch (Exception e)
            {
                failed++;
                _log.Error(e, "Penumbra redraw failed for player-owned object {ObjectIndex}.", objectIndex);
            }
        }

        if (failed > 0)
            warnings.Add($"Penumbra redraw failed for {failed} player-owned object(s).");
        return warnings.Count == 0 ? null : string.Join(" ", warnings);
    }

    private string? WriteModelToOriginalLocation(
        string modFolder,
        string targetFile,
        string exportedFile,
        bool backupExisting = false,
        string? modDirectory = null,
        string? targetRelativePath = null)
    {
        try
        {
            var fullTarget = Path.GetFullPath(targetFile);
            var parent = Path.GetDirectoryName(fullTarget);
            if (parent is null || !IsPathWithin(fullTarget, modFolder) ||
                !string.Equals(Path.GetExtension(fullTarget), ".mdl", StringComparison.OrdinalIgnoreCase) ||
                HasReparsePointInPath(modFolder, parent) ||
                (File.Exists(fullTarget) && (File.GetAttributes(fullTarget) & FileAttributes.ReparsePoint) != 0))
                return "The original mod destination is unsafe.";

            Directory.CreateDirectory(parent);
            if (backupExisting && File.Exists(fullTarget))
            {
                if (_backups is null || modDirectory is null || targetRelativePath is null)
                    return "Managed backup storage is unavailable.";
                _backups.Create(fullTarget, modDirectory, targetRelativePath);
            }
            var temporary = Path.Combine(parent, $".instant-edit-{Guid.NewGuid():N}.tmp");
            try
            {
                File.Copy(exportedFile, temporary, true);
                File.Move(temporary, fullTarget, true);
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
            return $"Could not write the original model file: {e.Message}";
        }
    }

}
