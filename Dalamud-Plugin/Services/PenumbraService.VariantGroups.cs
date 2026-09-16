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
    private sealed record VariantOptionResolution(string? FilePath, string Code, string? Error);
    private sealed record SourceOptionResolution(
        JsonObject? Option,
        SourceOptionLocator? Locator,
        string? Error,
        bool DefaultMatches = false);

    private static SourceOptionResolution ResolveSourceOption(
        string modFolder,
        string sourceGamePath,
        string sourceRelativePath,
        SourceOptionLocator? locator = null)
    {
        sourceRelativePath = sourceRelativePath.Replace('\\', '/');
        var candidates = new List<(JsonObject Option, SourceOptionLocator Locator)>();
        var meta = LoadV4ModMetadata(modFolder);
        var groups = meta["Groups"] as JsonArray ?? [];
        foreach (var group in groups.OfType<JsonObject>())
            AddSourceOptionCandidates(group, sourceGamePath, sourceRelativePath, candidates);

        if (locator is not null)
        {
            var exactMembership = candidates.Where(candidate =>
                string.Equals(candidate.Locator.Membership, locator.Membership, StringComparison.Ordinal)).ToArray();
            if (exactMembership.Length == 1)
                return new SourceOptionResolution(exactMembership[0].Option, exactMembership[0].Locator, null);
            if (IsV4OptionMembership(locator.Membership))
                return new SourceOptionResolution(null, null,
                    "The saved Penumbra option no longer exists. Re-import the model from the intended option.");

            // Pre-v4 persisted locators used array/file indexes. They can only be recovered by an
            // unambiguous name pair; an index must never silently select a different v4 option.
            var nameMatches = candidates.Where(candidate =>
                !string.IsNullOrWhiteSpace(locator.GroupName) &&
                !string.IsNullOrWhiteSpace(locator.OptionName) &&
                string.Equals(candidate.Locator.GroupName, locator.GroupName, StringComparison.Ordinal) &&
                string.Equals(candidate.Locator.OptionName, locator.OptionName, StringComparison.Ordinal)).ToArray();
            if (nameMatches.Length == 1)
                return new SourceOptionResolution(nameMatches[0].Option, nameMatches[0].Locator, null);
            if (!string.IsNullOrWhiteSpace(locator.Membership))
                return new SourceOptionResolution(null, null,
                    "The saved legacy Penumbra option could not be matched uniquely. Re-import the model from the intended option.");
        }
        var defaultData = meta["DefaultData"] as JsonObject;
        var defaultMapping = defaultData?["Files"] is JsonObject defaultFiles
            ? defaultFiles.FirstOrDefault(pair => SameGamePath(pair.Key, sourceGamePath)).Value
            : null;
        var mapsFromDefault = TryNormalizeRelativeModPath(JsonString(defaultMapping), out var defaultPath) &&
                              string.Equals(defaultPath, sourceRelativePath, StringComparison.OrdinalIgnoreCase);
        return (candidates.Count, mapsFromDefault) switch
        {
            (0, true) => new SourceOptionResolution(null, null, null, true),
            (0, false) => new SourceOptionResolution(null, null,
                "The source Penumbra option could not be rediscovered safely. Re-import the model, then export again."),
            (1, false) => new SourceOptionResolution(candidates[0].Option, candidates[0].Locator, null),
            _ => new SourceOptionResolution(null, null,
                "Multiple Penumbra options or Default provide the imported model. Re-import it from the intended option, then export again.",
                mapsFromDefault),
        };
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

    private static void AddSourceOptionCandidates(
        JsonObject? group, string gamePath, string relativePath,
        ICollection<(JsonObject Option, SourceOptionLocator Locator)> candidates)
    {
        var groupId = ReadGuid(group?["Id"]);
        if (groupId is null || group?["Options"] is not JsonArray options)
            return;
        foreach (var option in options.OfType<JsonObject>())
        {
            var optionId = ReadGuid(option["Id"]);
            if (optionId is null || option["Files"] is not JsonObject files)
                continue;
            var mapping = files.FirstOrDefault(pair => SameGamePath(pair.Key, gamePath));
            if (!TryNormalizeRelativeModPath(JsonString(mapping.Value), out var mapped) ||
                !string.Equals(mapped, relativePath, StringComparison.OrdinalIgnoreCase))
                continue;
            candidates.Add((option, new SourceOptionLocator
            {
                Membership = $"option:{groupId:D}:{optionId:D}",
                GroupName = JsonString(group["Name"]) ?? "",
                OptionName = JsonString(option["Name"]) ?? "",
            }));
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

    private static bool IsV4OptionMembership(string? membership)
    {
        var parts = membership?.Split(':');
        return parts is ["option", var group, var option] &&
               Guid.TryParse(group, out _) && Guid.TryParse(option, out _);
    }

    private static IReadOnlyList<VariantGroupTarget> ReadVariantTargets(
        string modFolder, string sourceGamePath, string? modDirectory = null,
        ModelBackupStore? backups = null)
    {
        var meta = LoadV4ModMetadata(modFolder);
        var groups = (meta["Groups"] as JsonArray ?? []).OfType<JsonObject>();

        var result = new List<VariantGroupTarget>();
        foreach (var group in groups)
        {
            if (!string.Equals(JsonString(group["Type"]), "Single", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(JsonString(group["Name"])) ||
                group["Options"] is not JsonArray options)
                continue;
            var groupId = ReadGuid(group["Id"]);
            if (groupId is null)
                continue;
            var targets = new List<VariantOptionTarget>();
            for (var optionIndex = 0; optionIndex < options.Count; ++optionIndex)
            {
                if (options[optionIndex] is not JsonObject option || option["Files"] is not JsonObject files)
                    continue;
                var mapping = files.FirstOrDefault(pair => SameGamePath(pair.Key, sourceGamePath));
                if (!TryNormalizeRelativeModPath(JsonString(mapping.Value), out var modelPath))
                    continue;
                var optionId = ReadGuid(option["Id"]);
                if (optionId is null)
                    continue;
                var selector = $"option:{groupId.Value:D}:{optionId.Value:D}";
                var backupTarget = backups is not null && modDirectory is not null
                    ? backups.Describe(modDirectory, modelPath) : null;
                targets.Add(new VariantOptionTarget(selector,
                    JsonString(option["Name"]) ?? "Unnamed Option", modelPath,
                    backupTarget?.Id, backupTarget?.Directory));
            }
            if (targets.Count > 0)
            {
                var selector = $"group:{groupId.Value:D}";
                result.Add(new VariantGroupTarget(selector, JsonString(group["Name"])!, targets));
            }
        }
        return result;
    }

    private static VariantOptionResolution ResolveVariantOptionTarget(
        string modFolder, string sourceGamePath, string? targetId)
    {
        try
        {
            if (!TryParseOptionTargetId(targetId, out var groupId, out var optionId))
                return new VariantOptionResolution(null, "invalid_variant_target", "The selected Penumbra option is invalid.");
            var group = ReadAllVariantGroups(modFolder).FirstOrDefault(candidate => ReadGuid(candidate["Id"]) == groupId);
            var option = group?["Options"] is JsonArray options
                ? options.OfType<JsonObject>().FirstOrDefault(candidate => ReadGuid(candidate["Id"]) == optionId)
                : null;
            if (option?["Files"] is not JsonObject files)
                return new VariantOptionResolution(null, "stale_variant_target", "The selected Penumbra option no longer exists.");
            var mapping = files.FirstOrDefault(pair => SameGamePath(pair.Key, sourceGamePath));
            if (!TryNormalizeRelativeModPath(JsonString(mapping.Value), out var relative))
                return new VariantOptionResolution(null, "stale_variant_target", "The selected option no longer replaces this model.");
            var path = Path.GetFullPath(Path.Combine(modFolder, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsPathWithin(path, modFolder))
                return new VariantOptionResolution(null, "destination_unsafe", "The selected option has an unsafe model path.");
            return new VariantOptionResolution(path, "accepted", null);
        }
        catch (Exception e)
        {
            return new VariantOptionResolution(null, "variant_target_unavailable", $"Could not read the selected Penumbra option: {e.Message}");
        }
    }

    private static IReadOnlyList<JsonObject> ReadAllVariantGroups(string modFolder)
    {
        var meta = LoadV4ModMetadata(modFolder);
        return (meta["Groups"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
    }

    private static bool TryParseOptionTargetId(string? value, out Guid groupId, out Guid optionId)
    {
        groupId = Guid.Empty;
        optionId = Guid.Empty;
        var parts = value?.Split(':');
        return parts is ["option", var group, var option] &&
               Guid.TryParse(group, out groupId) && Guid.TryParse(option, out optionId);
    }

    private static bool TryParseGroupTargetId(string? value, out Guid groupId)
    {
        groupId = Guid.Empty;
        var parts = value?.Split(':');
        return parts is ["group", var group] && Guid.TryParse(group, out groupId);
    }

    private static bool TryNormalizeRelativeModPath(string? value, out string path)
    {
        path = (value ?? "").Replace('\\', '/');
        return IsSafeRelativeModPath(path);
    }

    internal sealed record VariantGroupWrite(string Path, JsonObject Document);

    internal static string? CommitVariantGroup(VariantGroupWrite prepared)
    {
        try
        {
            WriteJsonAtomic(prepared.Path, prepared.Document);
            return null;
        }
        catch (Exception error)
        {
            return $"penumbra_group_write_failed: {error.Message}";
        }
    }

    /// <summary>
    /// Prepare an update to a Penumbra group that redirects the original model path
    /// to the newly exported sibling while preserving unrelated v4 metadata.
    /// </summary>
    internal static string? PrepareVariantGroup(
        string modFolder,
        string sourceGamePath,
        string relativeVariantPath,
        string variantName,
        string variantGroupName,
        out VariantGroupWrite? prepared,
        JsonObject? sourceOption = null,
        string? targetId = null)
    {
        prepared = null;
        try
        {
            relativeVariantPath = relativeVariantPath.Replace('\\', '/');
            if (!IsSafeGamePath(sourceGamePath) || !IsSafeRelativeModPath(relativeVariantPath) ||
                !IsSafeVariantName(variantName) || (targetId is null && !IsSafeVariantGroupName(variantGroupName)))
                return "invalid_penumbra_variant";

            // Existing targets are resolved by identity; only New Group uses name matching.
            var marker = VariantGroupDescriptionPrefix + variantGroupName + " -> " + sourceGamePath;
            var metaPath = Path.Combine(modFolder, "meta.json");
            var meta = LoadV4ModMetadata(modFolder);
            var groups = meta["Groups"] as JsonArray;
            if (groups is null)
            {
                groups = new JsonArray();
                meta["Groups"] = groups;
            }
            Guid selectedGroupId = default;
            if (targetId is not null && !TryParseGroupTargetId(targetId, out selectedGroupId))
                return "The selected Penumbra group is invalid.";
            JsonObject? existingGroup = null;
            var existingIndex = -1;
            var nameConflict = false;
            var highestOtherPriority = 0;
            for (var index = 0; index < groups.Count; ++index)
            {
                if (groups[index] is not JsonObject group)
                    continue;
                var sameName = string.Equals(
                    JsonString(group["Name"]), variantGroupName, StringComparison.OrdinalIgnoreCase);
                var selected = targetId is null ? sameName : ReadGuid(group["Id"]) == selectedGroupId;
                if (selected && IsReusableVariantGroup(group, sourceGamePath))
                {
                    existingGroup = group;
                    existingIndex = index;
                    continue;
                }
                if (sameName)
                    nameConflict = true;
                highestOtherPriority = Math.Max(highestOtherPriority, JsonInt(group["Priority"]));
            }

            if (targetId is not null && existingGroup is null)
                return "The selected Penumbra group no longer contains a replacement for this model.";
            if (existingGroup is null && nameConflict)
                return "An existing Penumbra group with this name is not a compatible Single group for this model.";
            if (existingGroup is null && highestOtherPriority == int.MaxValue)
                return "penumbra_group_priority_exhausted";

            var updatedGroup = BuildVariantGroup(
                existingGroup,
                marker,
                sourceGamePath,
                relativeVariantPath,
                variantName,
                targetId is null ? variantGroupName : JsonString(existingGroup!["Name"])!,
                existingGroup is null ? highestOtherPriority + 1 : JsonInt(existingGroup["Priority"]),
                sourceOption);
            if (existingIndex >= 0)
                groups[existingIndex] = updatedGroup;
            else
                groups.Add(updatedGroup);
            TouchV4ModMetadata(meta);
            prepared = new VariantGroupWrite(metaPath, meta);
            return null;
        }
        catch (Exception e)
        {
            return $"penumbra_group_write_failed: {e.Message}";
        }
    }

    internal static JsonObject BuildVariantGroup(
        JsonObject? existingGroup,
        string marker,
        string sourceGamePath,
        string relativeVariantPath,
        string variantName,
        string variantGroupName,
        int priority,
        JsonObject? sourceOption = null)
    {
        var groupId = ReadGuid(existingGroup?["Id"]) ?? Guid.NewGuid();
        var options = existingGroup?["Options"]?.DeepClone() as JsonArray ?? new JsonArray();
        if (existingGroup is null)
        {
            // A newly created Single group gets a disable entry. When extending
            // an existing group, preserve its options exactly and add only the
            // exported variant.
            options.Insert(0, new JsonObject
            {
                ["Id"] = Guid.NewGuid(),
                ["Name"] = "None",
            });
        }

        var variantOption = options
            .OfType<JsonObject>()
            .FirstOrDefault(option =>
                string.Equals(JsonString(option["Name"]), variantName, StringComparison.OrdinalIgnoreCase));
        if (variantOption is null)
        {
            var files = sourceOption?["Files"]?.DeepClone() as JsonObject ?? new JsonObject();
            foreach (var key in files.Select(pair => pair.Key)
                         .Where(key => SameGamePath(key, sourceGamePath)).ToArray())
                files.Remove(key);
            files[sourceGamePath] = relativeVariantPath;
            variantOption = new JsonObject
            {
                ["Id"] = Guid.NewGuid(),
                ["Name"] = variantName,
                ["Files"] = files,
            };
            if (sourceOption?["FileSwaps"] is JsonNode fileSwaps)
                variantOption["FileSwaps"] = fileSwaps.DeepClone();
            if (sourceOption?["Manipulations"] is JsonNode manipulations)
                variantOption["Manipulations"] = manipulations.DeepClone();
            options.Add(variantOption);
        }
        else
        {
            var files = variantOption["Files"]?.DeepClone() as JsonObject ?? new JsonObject();
            foreach (var key in files.Select(pair => pair.Key)
                         .Where(key => SameGamePath(key, sourceGamePath)).ToArray())
                files.Remove(key);
            files[sourceGamePath] = relativeVariantPath;
            variantOption["Files"] = files;
        }
        var selectedIndex = options.IndexOf(variantOption);
        if (selectedIndex < 0)
            throw new InvalidOperationException("The exported variant option was not added to its group.");

        var result = existingGroup?.DeepClone() as JsonObject ?? new JsonObject
        {
            ["Type"] = "Single",
            ["Id"] = groupId,
            ["Name"] = variantGroupName,
            ["Description"] = marker,
            ["Priority"] = priority,
        };
        result["DefaultSettings"] = selectedIndex;
        result["Options"] = options;
        return result;
    }

    private static bool IsReusableVariantGroup(JsonObject group, string sourceGamePath)
        => string.Equals(JsonString(group["Type"]), "Single", StringComparison.OrdinalIgnoreCase) &&
           GroupHasGamePath(group, sourceGamePath);

    private static bool GroupHasGamePath(JsonObject group, string sourceGamePath)
    {
        if (group["Options"] is not JsonArray options)
            return false;

        foreach (var option in options.OfType<JsonObject>())
        {
            if (option["Files"] is not JsonObject files)
                continue;

            if (files.Any(file => SameGamePath(file.Key, sourceGamePath)))
                return true;
        }

        return false;
    }

    private static bool SameGamePath(string left, string right)
        => string.Equals(left.Replace('\\', '/'), right.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static string? JsonString(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static int JsonInt(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<int>(out var number) ? number : 0;

    private static Guid? ReadGuid(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) && Guid.TryParse(text, out var guid)
            ? guid
            : null;

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

    private static bool HasReparsePointInPath(string root, string path)
    {
        var current = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        while (current.Length >= fullRoot.Length)
        {
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                return true;
            if (string.Equals(current, fullRoot, StringComparison.OrdinalIgnoreCase))
                break;
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal))
                break;
            current = parent;
        }

        return false;
    }
    internal static JsonObject BuildVariantOptionForRegression(
        JsonObject sourceOption, string sourceGamePath, string relativeVariantPath)
    {
        var group = BuildVariantGroup(null, "test", sourceGamePath, relativeVariantPath,
            "Variant", "Variants", 1, sourceOption);
        return (group["Options"] as JsonArray)![1]!.AsObject();
    }

    internal static IReadOnlyList<VariantGroupTarget> ReadVariantTargetsForRegression(
        string modFolder, string sourceGamePath)
        => ReadVariantTargets(modFolder, sourceGamePath);

    internal static string? ResolveVariantOptionPathForRegression(
        string modFolder, string sourceGamePath, string selector)
        => ResolveVariantOptionTarget(modFolder, sourceGamePath, selector).FilePath;

    internal static IReadOnlyList<string> ReadOptionMembershipsForRegression(
        string modFolder, string relativePath)
    {
        var mappings = ReadModMappings(modFolder);
        return OptionMembershipsFor(relativePath, mappings.OptionMemberships);
    }

    internal static (string? Membership, string? Error) ResolveSourceOptionForRegression(
        string modFolder, string sourceGamePath, string sourceRelativePath, SourceOptionLocator locator)
    {
        var resolution = ResolveSourceOption(modFolder, sourceGamePath, sourceRelativePath, locator);
        return (resolution.Locator?.Membership, resolution.Error);
    }

}
