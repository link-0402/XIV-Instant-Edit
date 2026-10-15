using System.Text.Json;
using System.Text.Json.Nodes;
using static InstantEdit.Services.PenumbraMods.ModJson;
using static InstantEdit.Services.PenumbraMods.ModPaths;

namespace InstantEdit.Services.PenumbraMods;

/// <summary>
/// Penumbra 1.7's mod metadata (meta.json FileVersion 4): reading and writing it, and the game paths,
/// options and files it maps.
/// </summary>
internal static class ModMetadata
{
    internal static JsonObject LoadV4ModMetadata(string modRoot)
    {
        const string compatibilityError =
            "Unsupported Penumbra mod metadata. Update and reload this mod in Penumbra 1.7.1.0 or newer, then retry.";
        var path = Path.Combine(modRoot, "meta.json");
        if (!File.Exists(path))
            throw new InvalidDataException(compatibilityError);
        JsonObject meta;
        try
        {
            meta = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                ?? throw new InvalidDataException(compatibilityError);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException(compatibilityError, error);
        }
        if (JsonInt(meta["FileVersion"]) != 4)
            throw new InvalidDataException(compatibilityError);
        return meta;
    }

    /// <summary>
    /// Read Penumbra 1.7's stable mod identifier without making metadata validity a
    /// prerequisite for legacy imports. A missing or malformed identifier is treated
    /// as unavailable so callers can retain the pre-1.7 directory-key behavior.
    /// </summary>
    internal static Guid? ReadModStableIdentifier(string modRoot)
    {
        try
        {
            var root = NormalizePhysicalPath(modRoot);
            if (root is null)
                return null;
            if (string.Equals(Path.GetFileName(root), "Files", StringComparison.OrdinalIgnoreCase))
                root = Directory.GetParent(root)?.FullName;
            if (root is null)
                return null;

            var path = Path.Combine(root, "meta.json");
            if (!File.Exists(path))
                return null;
            var meta = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (JsonInt(meta?["FileVersion"]) != 4 ||
                !Guid.TryParse(meta?["Identifier"]?.GetValue<string>(), out var identifier) ||
                identifier == Guid.Empty)
                return null;
            return identifier;
        }
        catch
        {
            return null;
        }
    }

    internal static Guid? ReadModStableIdentifierForRegression(string modRoot)
        => ReadModStableIdentifier(modRoot);

    internal static JsonObject CreateV4ModMetadata(
        string name,
        string author,
        string description,
        string version,
        JsonObject defaultData)
        => new()
        {
            ["FileVersion"] = 4,
            ["Identifier"] = Guid.NewGuid().ToString("D"),
            ["LastWrite"] = DateTime.UtcNow.ToString("O"),
            ["Name"] = name,
            ["Author"] = author,
            ["Description"] = description,
            ["Version"] = version,
            ["DefaultData"] = defaultData,
            ["Groups"] = new JsonArray(),
        };

    internal static void TouchV4ModMetadata(JsonObject meta)
    {
        if (JsonInt(meta["FileVersion"]) != 4)
            throw new InvalidDataException("Only Penumbra FileVersion 4 metadata can be updated.");
        meta["LastWrite"] = DateTime.UtcNow.ToString("O");
    }

    internal sealed record ModMappings(
        IReadOnlyDictionary<string, string?> GamePaths,
        IReadOnlyDictionary<string, string> OptionLabels,
        IReadOnlyDictionary<string, IReadOnlyList<string>> OptionMemberships);

    internal static ModMappings ReadModMappings(string modRoot)
    {
        var meta = LoadV4ModMetadata(modRoot);
        var gamePathsByModPath = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var labelsByPath = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        var membershipsByPath = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);

        void AddFileMappings(JsonNode? container, string label, IEnumerable<string> membershipValues)
        {
            if (container is not JsonObject value || value["Files"] is not JsonObject files)
                return;
            var stableMemberships = membershipValues.ToArray();

            foreach (var file in files)
            {
                var relativePath = JsonString(file.Value);
                if (string.IsNullOrWhiteSpace(relativePath))
                    continue;

                var gamePath = NormalizeCanonicalGamePath(file.Key);
                if (gamePath.Length > 0)
                {
                    foreach (var key in ModPathKeys(relativePath))
                    {
                        if (!gamePathsByModPath.TryGetValue(key, out var existing))
                            gamePathsByModPath[key] = gamePath;
                        else if (existing is not null && !SameGamePath(existing, gamePath))
                            gamePathsByModPath[key] = null;
                    }
                }

                foreach (var key in ModPathKeys(relativePath))
                {
                    if (!labelsByPath.TryGetValue(key, out var labels))
                        labelsByPath[key] = labels = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    labels.Add(label);
                    if (!membershipsByPath.TryGetValue(key, out var memberships))
                        membershipsByPath[key] = memberships = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var membership in stableMemberships)
                        memberships.Add(membership);
                }
            }
        }

        void AddGroup(JsonNode? group)
        {
            if (group is not JsonObject value)
                return;

            var groupName = JsonString(value["Name"]);
            if (string.IsNullOrWhiteSpace(groupName))
                groupName = "Unnamed group";
            var groupId = ReadGuid(value["Id"]);

            var options = value["Options"] as JsonArray;
            if (options is not null)
            {
                for (var optionIndex = 0; optionIndex < options.Count; optionIndex++)
                {
                    if (options[optionIndex] is not JsonObject option)
                        continue;
                    var optionName = JsonString(option["Name"]);
                    var label = string.IsNullOrWhiteSpace(optionName)
                        ? groupName
                        : $"{groupName}: {optionName}";
                    var optionId = ReadGuid(option["Id"]);
                    if (groupId is null || optionId is null)
                        continue;
                    AddFileMappings(option, label,
                        [$"option:{groupId.Value:D}:{optionId.Value:D}"]);
                }
            }

            if (value["Containers"] is JsonArray containers)
            {
                for (var containerIndex = 0; containerIndex < containers.Count; containerIndex++)
                {
                    if (containers[containerIndex] is not JsonObject container)
                        continue;
                    if (groupId is null)
                        continue;
                    var selectedOptions = (options ?? [])
                        .Select((option, optionIndex) => (Option: option as JsonObject, Index: optionIndex))
                        .Where(item => item.Option is not null && item.Index < 31 &&
                                       (containerIndex & (1 << item.Index)) != 0)
                        .Select(item => (Name: JsonString(item.Option!["Name"]), Id: ReadGuid(item.Option["Id"])))
                        .Where(item => item.Id is not null)
                        .ToArray();
                    var selectedNames = selectedOptions.Select(item => item.Name)
                        .Where(name => !string.IsNullOrWhiteSpace(name));
                    var labelSuffix = string.Join(" + ", selectedNames);
                    var label = labelSuffix.Length == 0 ? groupName : $"{groupName}: {labelSuffix}";
                    AddFileMappings(container, label, selectedOptions.Select(item =>
                        $"option:{groupId.Value:D}:{item.Id!.Value:D}"));
                }
            }
        }

        AddFileMappings(meta["DefaultData"], "Default", ["default"]);
        if (meta["Groups"] is JsonArray metaGroups)
            foreach (var group in metaGroups)
                AddGroup(group);

        return new ModMappings(
            gamePathsByModPath,
            labelsByPath.ToDictionary(
                pair => pair.Key,
                pair => string.Join(" | ", pair.Value),
                StringComparer.OrdinalIgnoreCase),
            membershipsByPath.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<string>)pair.Value.ToArray(),
                StringComparer.OrdinalIgnoreCase));
    }

    internal static string CanonicalGamePathFor(
        string relativePath,
        IReadOnlyDictionary<string, string?> mappings)
    {
        foreach (var key in ModPathKeys(relativePath))
            if (mappings.TryGetValue(key, out var gamePath) && gamePath is not null)
                return gamePath;

        return NormalizeCanonicalGamePath(relativePath);
    }

    internal static string NormalizeCanonicalGamePath(string path)
    {
        var normalized = path.Replace('\\', '/').Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        normalized = normalized.TrimStart('/');
        if (normalized.StartsWith("Files/", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[6..];
        return normalized;
    }

    internal static string OptionMappingFor(string relativePath, IReadOnlyDictionary<string, string> mappings)
    {
        foreach (var key in ModPathKeys(relativePath))
            if (mappings.TryGetValue(key, out var label))
                return label;
        return "Unmapped";
    }

    internal static IReadOnlyList<string> OptionMembershipsFor(
        string relativePath,
        IReadOnlyDictionary<string, IReadOnlyList<string>> mappings)
    {
        foreach (var key in ModPathKeys(relativePath))
            if (mappings.TryGetValue(key, out var memberships))
                return memberships;
        return Array.Empty<string>();
    }

    internal static IEnumerable<string> ModPathKeys(string path)
    {
        var normalized = path.Replace('\\', '/').Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        normalized = normalized.TrimStart('/');
        if (normalized.Length == 0)
            yield break;

        yield return normalized;
        if (normalized.StartsWith("Files/", StringComparison.OrdinalIgnoreCase))
            yield return normalized[6..];
        else
            yield return "Files/" + normalized;
    }

    /// <summary>
    /// Repoint every mapping of <paramref name="gamePath"/> to <paramref name="oldRelativePath"/>
    /// so it resolves to <paramref name="newRelativePath"/> instead. A mod can offer
    /// the same override from several mutually exclusive options, so every
    /// DefaultData, group, option, and container mapping that matches is updated
    /// together. Returns true when some other game path still maps to the old
    /// physical file, meaning the caller must not delete it.
    /// </summary>
    internal static bool RenameAnimationFileMapping(string modRoot, string gamePath, string oldRelativePath, string newRelativePath)
    {
        var meta = LoadV4ModMetadata(modRoot);
        var oldNormalized = oldRelativePath.Replace('\\', '/').Trim();
        var renamed = false;
        var stillReferenced = false;

        void Visit(JsonObject? container)
        {
            if (container?["Files"] is not JsonObject files) return;
            foreach (var pair in files.ToArray())
            {
                var value = JsonString(pair.Value);
                if (string.IsNullOrWhiteSpace(value) ||
                    !string.Equals(value.Replace('\\', '/').Trim(), oldNormalized, StringComparison.OrdinalIgnoreCase))
                    continue;
                // Written with the separators the mod used, so undoing the rename gives back exactly what was there.
                if (SameGamePath(pair.Key, gamePath)) { files[pair.Key] = SeparatorsLike(value, newRelativePath); renamed = true; }
                else stillReferenced = true;
            }
        }

        Visit(meta["DefaultData"] as JsonObject);
        if (meta["Groups"] is JsonArray groups)
            foreach (var group in groups.OfType<JsonObject>())
            {
                Visit(group);
                if (group["Options"] is JsonArray options)
                    foreach (var option in options.OfType<JsonObject>()) Visit(option);
                if (group["Containers"] is JsonArray containers)
                    foreach (var container in containers.OfType<JsonObject>()) Visit(container);
            }

        if (!renamed) throw new InvalidDataException($"{gamePath} is no longer mapped to {oldRelativePath} in this mod.");
        TouchV4ModMetadata(meta);
        WriteJsonAtomic(Path.Combine(modRoot, "meta.json"), meta);
        return stillReferenced;
    }

    /// <summary><paramref name="path"/> with backslashes when <paramref name="like"/> has them and no forward slashes, else as it is.</summary>
    internal static string SeparatorsLike(string like, string path) =>
        like.Contains('\\') && !like.Contains('/') ? path.Replace('/', '\\') : path;

    /// <summary>Whether a DefaultData, group, option or container of the mod maps <paramref name="gamePath"/> to a file.</summary>
    internal static bool ModMapsGamePath(string modRoot, string gamePath)
    {
        try { return AnimationMappingContainers(LoadV4ModMetadata(modRoot)).Any(files => files.Any(pair => SameGamePath(pair.Key, gamePath))); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException) { return false; }
    }

    /// <summary>
    /// The mod-relative files the DefaultData, groups, options and containers of the mod map
    /// <paramref name="gamePath"/> to, each once, in the order they come.
    /// </summary>
    internal static IReadOnlyList<string> AnimationMappedFiles(string modRoot, string gamePath)
    {
        var result = new List<string>();
        foreach (var files in AnimationMappingContainers(LoadV4ModMetadata(modRoot)))
            foreach (var pair in files)
                if (SameGamePath(pair.Key, gamePath) && NormalizedModFile(JsonString(pair.Value)) is { Length: > 0 } file &&
                    !result.Contains(file, StringComparer.OrdinalIgnoreCase))
                    result.Add(file);
        return result;
    }

    private static string? NormalizedModFile(string? value) => value?.Replace('\\', '/').Trim().TrimStart('/');

    /// <summary>
    /// Map <paramref name="gamePath"/> to <paramref name="relativePath"/> in every DefaultData,
    /// group, option and container that maps <paramref name="ownerGamePath"/>, so a file an
    /// animation edit added (an imported sound) plays wherever that animation does, or a copy
    /// wherever its original is. Refuses when the mod already maps <paramref name="gamePath"/> anywhere.
    /// With <paramref name="ownerFile"/>, only where the owner is that mod-relative file: a copy made
    /// from one of the files a mod's options map its original to. Then only those must not map
    /// <paramref name="gamePath"/> yet, since the other files' own copies take it in the others.
    /// </summary>
    internal static void AddAnimationResourceMapping(string modRoot, string ownerGamePath, string gamePath, string relativePath,
        string? ownerFile = null)
    {
        var meta = LoadV4ModMetadata(modRoot);
        var containers = AnimationMappingContainers(meta).ToArray();
        var owners = containers.Where(files => files.Any(pair => SameGamePath(pair.Key, ownerGamePath) &&
            (ownerFile == null || string.Equals(NormalizedModFile(JsonString(pair.Value)), NormalizedModFile(ownerFile),
                StringComparison.OrdinalIgnoreCase))))
            .ToArray();
        if ((ownerFile == null ? containers : owners).Any(files => files.Any(pair => SameGamePath(pair.Key, gamePath))))
            throw new InvalidDataException($"The mod already maps {gamePath}.");
        if (owners.Length == 0) throw new InvalidDataException($"The mod no longer maps {ownerGamePath}{(ownerFile == null ? "" : $" to {ownerFile}")}.");
        foreach (var files in owners)
            files[gamePath] = relativePath;
        TouchV4ModMetadata(meta);
        WriteJsonAtomic(Path.Combine(modRoot, "meta.json"), meta);
    }

    /// <summary>
    /// Undo <see cref="AddAnimationResourceMapping"/>: drop every mapping of <paramref name="gamePath"/>
    /// to <paramref name="relativePath"/>. Nothing to drop is fine, since a failed commit rolls back
    /// before it mapped anything.
    /// </summary>
    internal static void RemoveAnimationResourceMapping(string modRoot, string gamePath, string relativePath)
    {
        var meta = LoadV4ModMetadata(modRoot);
        var normalized = relativePath.Replace('\\', '/').Trim();
        var removed = false;
        foreach (var files in AnimationMappingContainers(meta))
            foreach (var pair in files.ToArray())
                if (SameGamePath(pair.Key, gamePath) && JsonString(pair.Value) is { } value &&
                    string.Equals(value.Replace('\\', '/').Trim(), normalized, StringComparison.OrdinalIgnoreCase))
                {
                    files.Remove(pair.Key);
                    removed = true;
                }
        if (!removed) return;
        TouchV4ModMetadata(meta);
        WriteJsonAtomic(Path.Combine(modRoot, "meta.json"), meta);
    }

    /// <summary>The Files objects of a mod's DefaultData, groups, options and containers.</summary>
    private static IEnumerable<JsonObject> AnimationMappingContainers(JsonObject meta)
    {
        if (meta["DefaultData"] is JsonObject { } defaults && defaults["Files"] is JsonObject defaultFiles) yield return defaultFiles;
        if (meta["Groups"] is not JsonArray groups) yield break;
        foreach (var group in groups.OfType<JsonObject>())
        {
            if (group["Files"] is JsonObject groupFiles) yield return groupFiles;
            foreach (var key in new[] { "Options", "Containers" })
                if (group[key] is JsonArray entries)
                    foreach (var entry in entries.OfType<JsonObject>())
                        if (entry["Files"] is JsonObject files) yield return files;
        }
    }

    internal static void WriteJsonAtomic(string path, JsonObject value)
    {
        var tempPath = path + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(tempPath, value.ToJsonString(JsonOpts));
            File.Move(tempPath, path, true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
    };

    /// <summary>The mod-relative files the default data and every option of the mod map.</summary>
    internal static HashSet<string> MappedModFiles(JsonObject meta)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(JsonNode? container)
        {
            if (container is JsonObject value && value["Files"] is JsonObject files)
                foreach (var pair in files)
                    if (JsonString(pair.Value) is { } path)
                        used.Add(path.Replace('\\', '/').TrimStart('/'));
        }
        Add(meta["DefaultData"]);
        if (meta["Groups"] is JsonArray groups)
            foreach (var group in groups.OfType<JsonObject>())
                foreach (var key in new[] { "Options", "Containers" })
                    if (group[key] is JsonArray options)
                        foreach (var option in options)
                            Add(option);
        return used;
    }
}
