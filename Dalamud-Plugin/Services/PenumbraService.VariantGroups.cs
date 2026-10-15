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
using InstantEdit.Services.PenumbraMods;
using static InstantEdit.Services.PenumbraMods.FrameworkThread;
using static InstantEdit.Services.PenumbraMods.ModCleanup;
using static InstantEdit.Services.PenumbraMods.ModJson;
using static InstantEdit.Services.PenumbraMods.ModMetadata;
using static InstantEdit.Services.PenumbraMods.ModOptionGroups;
using static InstantEdit.Services.PenumbraMods.ModPaths;

namespace InstantEdit.Services;

public sealed partial class PenumbraService
{
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

    private bool TryResolveRegisteredModIdentity(
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

    private IEnumerable<string> RegisteredModRootCandidates(string directory, string? capturedRootPath = null)
    {
        var registered = GetRegisteredModPath(directory);
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
        var configured = GetModDirectory();
        if (!string.IsNullOrWhiteSpace(configured))
            yield return Path.Combine(configured, directory);
        if (!string.IsNullOrWhiteSpace(capturedRootPath))
            yield return capturedRootPath;
    }

    internal static string FormatMashupDescription(
        IReadOnlyList<MashupContributor> contributors,
        IReadOnlyList<string>? requiredExternalMods = null)
    {
        var names = contributors
            .Select(item => item.Context.SourceModName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!.Replace("\"", "'", StringComparison.Ordinal))
            .ToArray();
        var description = $"Mashup created by XIV Instant Edit from {string.Join(" and ", names.Select(name => $"\"{name}\""))}.";
        return requiredExternalMods is { Count: > 0 }
            ? $"{description}\n\nRequires external mods: {string.Join(", ", requiredExternalMods)}."
            : description;
    }

    private static IReadOnlyList<string> ExternalMashupWarnings(IReadOnlyList<string> requiredExternalMods)
        => requiredExternalMods.Count == 0
            ? Array.Empty<string>()
            : [$"Keep these external mods enabled in the active collection: {string.Join(", ", requiredExternalMods)}."];

}
