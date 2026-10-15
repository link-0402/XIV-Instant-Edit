using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InstantEdit.Models;
using static InstantEdit.Services.PenumbraMods.ModJson;
using static InstantEdit.Services.PenumbraMods.ModMetadata;
using static InstantEdit.Services.PenumbraMods.ModPaths;

namespace InstantEdit.Services.PenumbraMods;

/// <summary>
/// A mod's option groups: finding the option an imported model came from, the Single groups an
/// export can extend, and building the groups an export writes.
/// </summary>
internal static class ModOptionGroups
{
    internal const string VariantGroupDescriptionPrefix = "Managed by XIV Instant Edit variant group: ";

    internal sealed record VariantOptionResolution(string? FilePath, string Code, string? Error);

    internal sealed record SourceOptionResolution(
        JsonObject? Option,
        SourceOptionLocator? Locator,
        string? Error,
        bool DefaultMatches = false);

    internal static SourceOptionResolution ResolveSourceOption(
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

    internal static void AddSourceOptionCandidates(
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

    internal static bool IsV4OptionMembership(string? membership)
    {
        var parts = membership?.Split(':');
        return parts is ["option", var group, var option] &&
               Guid.TryParse(group, out _) && Guid.TryParse(option, out _);
    }

    internal static IReadOnlyList<VariantGroupTarget> ReadVariantTargets(
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

    internal static VariantOptionResolution ResolveVariantOptionTarget(
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

    internal static IReadOnlyList<JsonObject> ReadAllVariantGroups(string modFolder)
    {
        var meta = LoadV4ModMetadata(modFolder);
        return (meta["Groups"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
    }

    internal static bool TryParseOptionTargetId(string? value, out Guid groupId, out Guid optionId)
    {
        groupId = Guid.Empty;
        optionId = Guid.Empty;
        var parts = value?.Split(':');
        return parts is ["option", var group, var option] &&
               Guid.TryParse(group, out groupId) && Guid.TryParse(option, out optionId);
    }

    internal static bool TryParseGroupTargetId(string? value, out Guid groupId)
    {
        groupId = Guid.Empty;
        var parts = value?.Split(':');
        return parts is ["group", var group] && Guid.TryParse(group, out groupId);
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

    /// <summary>An option an export writes: its name and the mod-relative model file it maps the game path to.</summary>
    internal sealed record GroupOptionFile(string Name, string RelativePath);

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
        if (!IsSafeVariantName(variantName))
            return "invalid_penumbra_variant";
        return PrepareOptionGroup(modFolder, sourceGamePath, [new GroupOptionFile(variantName, relativeVariantPath)],
            variantGroupName, variantName, true, out prepared, sourceOption, targetId);
    }

    /// <summary>
    /// Prepare an update to a Penumbra group whose options redirect the original model path to
    /// exported models, preserving unrelated v4 metadata. A new group starts with a None option when
    /// <paramref name="addNone"/>; <paramref name="selectedName"/> is the option the group turns on by default.
    /// </summary>
    internal static string? PrepareOptionGroup(
        string modFolder,
        string sourceGamePath,
        IReadOnlyList<GroupOptionFile> optionFiles,
        string groupName,
        string selectedName,
        bool addNone,
        out VariantGroupWrite? prepared,
        JsonObject? sourceOption = null,
        string? targetId = null)
    {
        prepared = null;
        try
        {
            optionFiles = optionFiles
                .Select(file => file with { RelativePath = file.RelativePath.Replace('\\', '/') }).ToArray();
            if (!IsSafeGamePath(sourceGamePath) || optionFiles.Count == 0 ||
                optionFiles.Any(file => !IsSafeRelativeModPath(file.RelativePath) || !IsSafeVariantGroupName(file.Name)) ||
                (targetId is null && !IsSafeVariantGroupName(groupName)))
                return "invalid_penumbra_variant";

            var metaPath = Path.Combine(modFolder, "meta.json");
            var meta = LoadV4ModMetadata(modFolder);
            var groups = meta["Groups"] as JsonArray;
            if (groups is null)
            {
                groups = new JsonArray();
                meta["Groups"] = groups;
            }
            var lookupError = FindOptionGroup(
                groups, sourceGamePath, groupName, targetId, out var existingGroup, out var existingIndex,
                out var highestOtherPriority);
            if (lookupError is not null)
                return lookupError;

            var updatedGroup = BuildOptionGroup(
                existingGroup,
                VariantGroupDescriptionPrefix + groupName + " -> " + sourceGamePath,
                sourceGamePath,
                optionFiles,
                targetId is null ? groupName : JsonString(existingGroup!["Name"])!,
                existingGroup is null ? highestOtherPriority + 1 : JsonInt(existingGroup["Priority"]),
                selectedName,
                addNone,
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

    /// <summary>
    /// Find the group an export extends: the one <paramref name="targetId"/> names, else New Group's
    /// compatible group named <paramref name="groupName"/>; null when New Group makes a new one. Returns
    /// why it can't be used, if so.
    /// </summary>
    internal static string? FindOptionGroup(
        JsonArray groups,
        string sourceGamePath,
        string groupName,
        string? targetId,
        out JsonObject? existingGroup,
        out int existingIndex,
        out int highestOtherPriority)
    {
        existingGroup = null;
        existingIndex = -1;
        highestOtherPriority = 0;
        // Existing targets are resolved by identity; only New Group uses name matching.
        Guid selectedGroupId = default;
        if (targetId is not null && !TryParseGroupTargetId(targetId, out selectedGroupId))
            return "The selected Penumbra group is invalid.";
        var nameConflict = false;
        for (var index = 0; index < groups.Count; ++index)
        {
            if (groups[index] is not JsonObject group)
                continue;
            var sameName = string.Equals(
                JsonString(group["Name"]), groupName, StringComparison.OrdinalIgnoreCase);
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
        return null;
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
        => BuildOptionGroup(existingGroup, marker, sourceGamePath,
            [new GroupOptionFile(variantName, relativeVariantPath)], variantGroupName, priority, variantName, true,
            sourceOption);

    internal static JsonObject BuildOptionGroup(
        JsonObject? existingGroup,
        string marker,
        string sourceGamePath,
        IReadOnlyList<GroupOptionFile> optionFiles,
        string groupName,
        int priority,
        string selectedName,
        bool addNone,
        JsonObject? sourceOption = null)
    {
        var groupId = ReadGuid(existingGroup?["Id"]) ?? Guid.NewGuid();
        var options = existingGroup?["Options"]?.DeepClone() as JsonArray ?? new JsonArray();
        if (existingGroup is null && addNone)
        {
            // A newly created Single group gets a disable entry. When extending
            // an existing group, preserve its options exactly and add only the
            // exported variants.
            options.Insert(0, new JsonObject
            {
                ["Id"] = Guid.NewGuid(),
                ["Name"] = "None",
            });
        }

        foreach (var optionFile in optionFiles)
        {
            var option = FindOption(options, optionFile.Name);
            if (option is null)
            {
                var files = sourceOption?["Files"]?.DeepClone() as JsonObject ?? new JsonObject();
                foreach (var key in files.Select(pair => pair.Key)
                             .Where(key => SameGamePath(key, sourceGamePath)).ToArray())
                    files.Remove(key);
                files[sourceGamePath] = optionFile.RelativePath;
                option = new JsonObject
                {
                    ["Id"] = Guid.NewGuid(),
                    ["Name"] = optionFile.Name,
                    ["Files"] = files,
                };
                if (sourceOption?["FileSwaps"] is JsonNode fileSwaps)
                    option["FileSwaps"] = fileSwaps.DeepClone();
                if (sourceOption?["Manipulations"] is JsonNode manipulations)
                    option["Manipulations"] = manipulations.DeepClone();
                options.Add(option);
            }
            else
            {
                var files = option["Files"]?.DeepClone() as JsonObject ?? new JsonObject();
                foreach (var key in files.Select(pair => pair.Key)
                             .Where(key => SameGamePath(key, sourceGamePath)).ToArray())
                    files.Remove(key);
                files[sourceGamePath] = optionFile.RelativePath;
                option["Files"] = files;
            }
        }
        var selected = FindOption(options, selectedName);
        var selectedIndex = selected is null ? -1 : options.IndexOf(selected);
        if (selectedIndex < 0)
            throw new InvalidOperationException("The exported variant option was not added to its group.");

        var result = existingGroup?.DeepClone() as JsonObject ?? new JsonObject
        {
            ["Type"] = "Single",
            ["Id"] = groupId,
            ["Name"] = groupName,
            ["Description"] = marker,
            ["Priority"] = priority,
        };
        // An existing group keeps its own default; its options keep their places, so the index stays valid.
        if (existingGroup is null)
            result["DefaultSettings"] = selectedIndex;
        result["Options"] = options;
        return result;
    }

    internal static JsonObject? FindOption(JsonArray options, string name)
        => options.OfType<JsonObject>().FirstOrDefault(option =>
            string.Equals(JsonString(option["Name"]), name, StringComparison.OrdinalIgnoreCase));

    internal static bool IsReusableVariantGroup(JsonObject group, string sourceGamePath)
        => string.Equals(JsonString(group["Type"]), "Single", StringComparison.OrdinalIgnoreCase) &&
           GroupHasGamePath(group, sourceGamePath);

    internal static bool GroupHasGamePath(JsonObject group, string sourceGamePath)
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

    /// <summary>Whether the default data or an option not named <paramref name="optionName"/> maps <paramref name="file"/>.</summary>
    internal static bool IsMappedOutsideOption(JsonObject meta, string modRoot, string file, string optionName)
    {
        var relative = Path.GetRelativePath(modRoot, file).Replace('\\', '/');
        bool Maps(JsonNode? container) => container is JsonObject value && value["Files"] is JsonObject files &&
            files.Any(pair => string.Equals(JsonString(pair.Value)?.Replace('\\', '/').TrimStart('/'), relative,
                StringComparison.OrdinalIgnoreCase));
        if (Maps(meta["DefaultData"]))
            return true;
        if (meta["Groups"] is not JsonArray groups)
            return false;
        foreach (var group in groups.OfType<JsonObject>())
            foreach (var key in new[] { "Options", "Containers" })
                if (group[key] is JsonArray options)
                    foreach (var option in options.OfType<JsonObject>())
                        if (Maps(option) &&
                            !string.Equals(JsonString(option["Name"]), optionName, StringComparison.OrdinalIgnoreCase))
                            return true;
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

    internal static JsonArray CaptureEffectiveManipulations(
        string modFolder,
        IReadOnlyDictionary<string, IReadOnlyList<string>> enabledOptions)
    {
        var meta = LoadV4ModMetadata(modFolder);
        var groups = ReadAllVariantGroups(modFolder)
            .Select((group, index) => (Group: group, Index: index))
            .OrderByDescending(item => JsonInt(item.Group["Priority"]))
            .ThenBy(item => item.Index)
            .ToArray();
        var result = new JsonArray();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var serializedBytes = 2;

        static string CanonicalJson(JsonNode? node)
            => node switch
            {
                null => "null",
                JsonObject value => "{" + string.Join(",", value
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => JsonSerializer.Serialize(pair.Key) + ":" + CanonicalJson(pair.Value))) + "}",
                JsonArray value => "[" + string.Join(",", value.Select(CanonicalJson)) + "]",
                _ => node.ToJsonString(),
            };

        static string Identity(JsonObject manipulation)
        {
            var identity = manipulation.DeepClone().AsObject();
            if (identity["Manipulation"] is JsonObject payload)
                payload.Remove("Entry");
            else
                identity.Remove("Entry");
            return CanonicalJson(identity);
        }

        void Append(JsonNode? container)
        {
            if (container?["Manipulations"] is not JsonArray manipulations)
                return;
            foreach (var manipulation in manipulations)
            {
                if (manipulation is not JsonObject entry)
                    throw new InvalidDataException("A source manipulation entry is invalid.");
                var type = JsonString(entry["Type"]);
                if (type is not ("Eqp" or "Eqdp" or "Imc" or "Est" or "Atr"))
                    continue;
                if (!identities.Add(Identity(entry)))
                    continue;
                var clone = entry.DeepClone();
                serializedBytes += Encoding.UTF8.GetByteCount(clone.ToJsonString()) + 1;
                if (result.Count >= 4096 || serializedBytes > 4 * 1024 * 1024)
                    throw new InvalidDataException("The source manipulation snapshot is too large.");
                result.Add(clone);
            }
        }

        foreach (var (group, _index) in groups)
        {
            var groupName = JsonString(group["Name"]);
            var selection = enabledOptions.FirstOrDefault(pair =>
                string.Equals(pair.Key, groupName, StringComparison.OrdinalIgnoreCase));
            var selected = selection.Value;
            if (selected is null)
                continue;

            if (string.Equals(JsonString(group["Type"]), "Combining", StringComparison.OrdinalIgnoreCase))
            {
                if (group["Options"] is not JsonArray combinationOptions ||
                    group["Containers"] is not JsonArray combinationContainers)
                    continue;
                var containerIndex = 0;
                for (var optionIndex = 0; optionIndex < combinationOptions.Count && optionIndex < 31; ++optionIndex)
                    if (combinationOptions[optionIndex] is JsonObject option &&
                        selected.Contains(JsonString(option["Name"]) ?? "", StringComparer.OrdinalIgnoreCase))
                        containerIndex |= 1 << optionIndex;
                if (containerIndex < combinationContainers.Count)
                    Append(combinationContainers[containerIndex]);
                continue;
            }

            if (selected.Count == 0 || group["Options"] is not JsonArray options)
                continue;
            foreach (var selectedName in selected)
            {
                var container = options.OfType<JsonObject>().FirstOrDefault(option =>
                    string.Equals(JsonString(option["Name"]), selectedName, StringComparison.OrdinalIgnoreCase));
                if (container is not null)
                    Append(container);
            }
        }

        Append(meta["DefaultData"]);
        return result;
    }
}
