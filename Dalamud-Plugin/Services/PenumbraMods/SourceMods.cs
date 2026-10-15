using System.Text.Json.Nodes;
using Dalamud.Plugin.Services;
using InstantEdit.Models;
using Penumbra.Api.Enums;
using Penumbra.Api.IpcSubscribers;
using static InstantEdit.Services.PenumbraMods.FrameworkThread;
using static InstantEdit.Services.PenumbraMods.ModMetadata;
using static InstantEdit.Services.PenumbraMods.ModOptionGroups;
using static InstantEdit.Services.PenumbraMods.ModPaths;

namespace InstantEdit.Services.PenumbraMods;

/// <summary> An imported model's file in its registered source mod. </summary>
internal sealed record SourceModTarget(string Directory, string Folder, string FilePath, string RelativePath);

internal sealed record SourceTargetResolution(SourceModTarget? Target, string Code, string? Error);

/// <summary> A registered mod and the folders that may hold its files. </summary>
internal sealed record ModScanRequest(PenumbraMod Mod, string[] CandidateRoots);

/// <summary>
/// Finds the registered Penumbra mod an import came from, by its directory key and stable identity,
/// and the model file, option and meta manipulations of that import inside it. The *OnFramework
/// calls must run on the framework thread.
/// </summary>
internal sealed class SourceMods
{
    private readonly PenumbraIpc _ipc;
    private readonly IFramework _framework;
    private readonly IPluginLog _log;
    private readonly GetModPath _getModPath;
    private readonly GetCurrentModSettings _getCurrentModSettings;

    public SourceMods(PenumbraIpc ipc, IFramework framework, IPluginLog log)
    {
        _ipc = ipc;
        _framework = framework;
        _log = log;
        _getModPath = ipc.GetModPath;
        _getCurrentModSettings = ipc.GetCurrentModSettings;
    }

    /// <summary>Capture the effective source-mod manipulations for one collection.</summary>
    public async Task<JsonArray?> CaptureEffectiveManipulationsAsync(
        Guid collectionId,
        string modDirectory,
        string modRoot)
    {
        if (collectionId == Guid.Empty || !IsSafeModName(modDirectory) ||
            string.IsNullOrWhiteSpace(modRoot) || !Directory.Exists(modRoot))
            return null;
        try
        {
            var selections = await _framework.RunOnFrameworkThread(() =>
            {
                var result = _getCurrentModSettings.Invoke(
                    collectionId, modDirectory, string.Empty, false);
                if (result.Item1 is not PenumbraApiEc.Success || result.Item2 is null)
                    return null;
                return result.Item2.Value.Item3.ToDictionary(
                    pair => pair.Key,
                    pair => (IReadOnlyList<string>)pair.Value.ToArray(),
                    StringComparer.OrdinalIgnoreCase);
            }).ConfigureAwait(false);
            await LeaveFrameworkThread();
            return selections is null
                ? null
                : CaptureEffectiveManipulations(modRoot, selections);
        }
        catch (Exception e)
        {
            _log.Warning(e, "Could not snapshot Meta Manipulations for source mod {ModDirectory}.", modDirectory);
            return null;
        }
    }

    internal SourceTargetResolution ResolveSourceModTargetOnFramework(
        string sourceModDirectory,
        string sourceFilePath,
        string? sourceModRootPath,
        string? targetRelativePath,
        Guid? sourceModStableId = null)
    {
        if (!_ipc.TryGetModList(out var modList))
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
            var configuredRoot = _ipc.GetModDirectory();
            return ResolveSourceModTargetFromRoots(
                registeredDirectory,
                sourceFilePath,
                sourceModRootPath,
                targetRelativePath,
                _ipc.GetRegisteredModPath(registeredDirectory),
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

    internal static void AddExportRoot(
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

    internal static bool TryRelativeModelPath(string filePath, string root, out string relative)
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

    internal static string? NormalizeRelativeModelPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var normalized = value.Replace('\\', '/').Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        normalized = normalized.TrimStart('/');
        return IsSafeRelativeModelPath(normalized) ? normalized : null;
    }

    public async Task<SourceOptionCapture> CaptureSourceOptionAsync(
        string sourceModDirectory, string sourceFilePath, string? sourceModRootPath,
        string? targetRelativePath, string sourceGamePath,
        IReadOnlyCollection<string>? preferredMemberships = null,
        Guid? collectionId = null,
        Guid? sourceModStableId = null)
    {
        try
        {
            var resolved = await _framework.RunOnFrameworkThread(() => ResolveSourceModTargetOnFramework(
                sourceModDirectory, sourceFilePath, sourceModRootPath, targetRelativePath, sourceModStableId)).ConfigureAwait(false);
            if (resolved.Target is null)
                return new SourceOptionCapture(null, "unknown", resolved.Error);
            await LeaveFrameworkThread();
            var option = ResolveSourceOption(resolved.Target.Folder, sourceGamePath, resolved.Target.RelativePath);
            if (preferredMemberships is { Count: > 0 })
            {
                var prefersDefault = preferredMemberships.Contains("default", StringComparer.OrdinalIgnoreCase);
                var preferred = preferredMemberships
                    .Where(membership => !string.Equals(membership, "default", StringComparison.OrdinalIgnoreCase))
                    .Select(membership => ResolveSourceOption(resolved.Target.Folder, sourceGamePath,
                        resolved.Target.RelativePath,
                        new SourceOptionLocator
                        {
                            Membership = membership,
                            GroupName = "",
                            OptionName = "",
                        }))
                    .Where(candidate => candidate.Error is null && candidate.Locator is not null)
                    .GroupBy(candidate => candidate.Locator!.Membership, StringComparer.Ordinal)
                    .Select(group => group.First())
                    .ToArray();
                option = (preferred.Length, prefersDefault) switch
                {
                    (0, true) => new SourceOptionResolution(null, null, null, true),
                    (1, false) => preferred[0],
                    (> 0, _) => new SourceOptionResolution(null, null,
                        "Multiple clicked option memberships provide the imported model; the active collection selection is required.",
                        prefersDefault),
                    _ => option,
                };
            }
            if (option.Error is not null && collectionId is Guid activeCollection && activeCollection != Guid.Empty)
            {
                var settings = await _framework.RunOnFrameworkThread(() =>
                {
                    var result = _getCurrentModSettings.Invoke(activeCollection, sourceModDirectory, string.Empty, false);
                    return result.Item1 is PenumbraApiEc.Success && result.Item2 is not null
                        ? result.Item2.Value.Item3.ToDictionary(pair => pair.Key,
                            pair => (IReadOnlyList<string>)pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase)
                        : null;
                }).ConfigureAwait(false);
                await LeaveFrameworkThread();
                if (settings is not null)
                {
                    var selectedOptions = new List<SourceOptionResolution>();
                    foreach (var selection in settings)
                    foreach (var optionName in selection.Value)
                    {
                        var selected = ResolveSourceOption(resolved.Target.Folder, sourceGamePath,
                            resolved.Target.RelativePath, new SourceOptionLocator
                            {
                                Membership = "",
                                GroupName = selection.Key,
                                OptionName = optionName,
                            });
                        if (selected.Error is null && selected.Locator is not null &&
                            selectedOptions.All(candidate => !string.Equals(
                                candidate.Locator!.Membership, selected.Locator.Membership, StringComparison.Ordinal)))
                            selectedOptions.Add(selected);
                    }
                    option = selectedOptions.Count switch
                    {
                        1 => selectedOptions[0],
                        > 1 => new SourceOptionResolution(null, null,
                            "Multiple active Penumbra options provide the imported model. Re-import it from the intended option, then export again.",
                            option.DefaultMatches),
                        _ when option.DefaultMatches => new SourceOptionResolution(null, null, null, true),
                        _ => option,
                    };
                }
            }
            return option.Error is not null
                ? new SourceOptionCapture(null, "ambiguous", option.Error)
                : new SourceOptionCapture(option.Locator, option.Locator is null ? "default" : "ready");
        }
        catch (Exception error)
        {
            return new SourceOptionCapture(null, "unknown", error.Message);
        }
    }

    internal bool TryResolveRegisteredModIdentity(
        IReadOnlyDictionary<string, string> modList,
        string requestedDirectory,
        Guid? expectedStableId,
        string? capturedRootPath,
        out string registeredDirectory,
        out (string Code, string Message) error)
    {
        registeredDirectory = string.Empty;
        error = ("source_mod_missing", "The source mod is no longer registered in Penumbra.");
        if (!IsSafeModName(requestedDirectory))
        {
            error = ("destination_unsafe", "The original Penumbra mod directory is invalid.");
            return false;
        }

        var requested = modList.Keys.FirstOrDefault(directory =>
            string.Equals(directory, requestedDirectory, StringComparison.OrdinalIgnoreCase));
        if (expectedStableId is null)
        {
            if (requested is null)
                return false;
            registeredDirectory = requested;
            return true;
        }

        if (expectedStableId == Guid.Empty)
        {
            error = ("source_mod_identity_unavailable", "The saved Penumbra mod identity is invalid. Re-import the model.");
            return false;
        }

        if (requested is not null)
        {
            var requestedRoots = RegisteredModRootCandidates(requested, capturedRootPath).ToArray();
            if (requestedRoots.Select(ReadModStableIdentifier).Any(identifier => identifier == expectedStableId))
            {
                registeredDirectory = requested;
                return true;
            }
        }

        var matches = new List<string>();
        Guid? requestedObserved = null;
        foreach (var directory in modList.Keys)
        {
            var roots = RegisteredModRootCandidates(
                directory,
                string.Equals(directory, requestedDirectory, StringComparison.OrdinalIgnoreCase)
                    ? capturedRootPath
                    : null).ToArray();
            for (var rootIndex = 0; rootIndex < roots.Length; rootIndex++)
            {
                var observed = ReadModStableIdentifier(roots[rootIndex]);
                if (string.Equals(directory, requestedDirectory, StringComparison.OrdinalIgnoreCase) &&
                    rootIndex == 0 && observed is not null)
                    requestedObserved = observed;
                if (observed == expectedStableId)
                {
                    if (!matches.Contains(directory, StringComparer.OrdinalIgnoreCase))
                        matches.Add(directory);
                    break;
                }
            }
        }

        if (requested is not null && requestedObserved is not null && requestedObserved != expectedStableId)
        {
            error = ("source_mod_changed", "The registered Penumbra mod no longer matches the mod used for this import.");
            return false;
        }
        if (matches.Count == 1)
        {
            registeredDirectory = matches[0];
            return true;
        }
        if (matches.Count > 1)
        {
            error = ("source_mod_ambiguous", "More than one Penumbra mod has the saved stable identity.");
            return false;
        }
        error = (requested is null ? "source_mod_missing" : "source_mod_identity_unavailable",
            requested is null
                ? "The source mod is no longer registered in Penumbra."
                : "The saved Penumbra mod identity could not be verified. Reload the mod in Penumbra, then re-import the model.");
        return false;
    }

    internal IEnumerable<string> RegisteredModRootCandidates(string directory, string? capturedRootPath = null)
    {
        var registered = _ipc.GetRegisteredModPath(directory);
        if (!string.IsNullOrWhiteSpace(registered))
        {
            yield return registered;

            // Penumbra can briefly return the previous search-order path while a
            // mod reload is settling. Only use the configured root as a recovery
            // candidate when that registered path no longer exists; an existing
            // registered root remains authoritative for identity verification.
            if (Directory.Exists(registered))
                yield break;
        }
        var configured = _ipc.GetModDirectory();
        if (!string.IsNullOrWhiteSpace(configured))
            yield return Path.Combine(configured, directory);
        if (!string.IsNullOrWhiteSpace(capturedRootPath))
            yield return capturedRootPath;
    }

    internal ModScanRequest? ResolveModScanOnFramework(string modDirectory, Guid? stableId = null)
    {
        if (stableId is not null && _ipc.TryGetModList(out var modList) &&
            !TryResolveRegisteredModIdentity(modList, modDirectory, stableId, null, out modDirectory, out _))
            return null;
        var mod = _ipc.GetMods().FirstOrDefault(item =>
            string.Equals(item.Directory, modDirectory, StringComparison.OrdinalIgnoreCase));
        if (mod is null)
            return null;

        var candidateRoots = new List<string>();
        var pathResult = _getModPath.Invoke(mod.Directory, string.Empty);
        if (pathResult.Item1 is PenumbraApiEc.Success)
            AddCandidateRoot(candidateRoots, pathResult.Item2);

        // GetModPath can point at a manually configured location. Keep the
        // standard Penumbra root as a fallback for older/API-incompatible installs.
        var modDirectoryRoot = _ipc.GetModDirectory();
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

    internal string[] GetRegisteredManifestRoots(string modDirectory, Guid? stableId = null)
    {
        var modList = _ipc.GetModList();
        if (!TryResolveRegisteredModIdentity(modList, modDirectory, stableId, null, out var registeredDirectory, out _))
            return [];

        var roots = new List<string>();
        AddCandidateRoot(roots, _ipc.GetRegisteredModPath(registeredDirectory));
        var configuredRoot = _ipc.GetModDirectory();
        if (!string.IsNullOrWhiteSpace(configuredRoot))
            AddCandidateRoot(roots, Path.Combine(configuredRoot, registeredDirectory));
        return roots.ToArray();
    }
}
