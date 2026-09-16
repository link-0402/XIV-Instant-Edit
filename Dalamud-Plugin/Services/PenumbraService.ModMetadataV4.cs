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
    private static JsonObject LoadV4ModMetadata(string modRoot)
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

    private static void TouchV4ModMetadata(JsonObject meta)
    {
        if (JsonInt(meta["FileVersion"]) != 4)
            throw new InvalidDataException("Only Penumbra FileVersion 4 metadata can be updated.");
        meta["LastWrite"] = DateTime.UtcNow.ToString("O");
    }

    private sealed record ModMappings(
        IReadOnlyDictionary<string, string?> GamePaths,
        IReadOnlyDictionary<string, string> OptionLabels,
        IReadOnlyDictionary<string, IReadOnlyList<string>> OptionMemberships);

    private static ModMappings ReadModMappings(string modRoot)
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

    private static string CanonicalGamePathFor(
        string relativePath,
        IReadOnlyDictionary<string, string?> mappings)
    {
        foreach (var key in ModPathKeys(relativePath))
            if (mappings.TryGetValue(key, out var gamePath) && gamePath is not null)
                return gamePath;

        return NormalizeCanonicalGamePath(relativePath);
    }

    private static string NormalizeCanonicalGamePath(string path)
    {
        var normalized = path.Replace('\\', '/').Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        normalized = normalized.TrimStart('/');
        if (normalized.StartsWith("Files/", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[6..];
        return normalized;
    }

    private static string OptionMappingFor(string relativePath, IReadOnlyDictionary<string, string> mappings)
    {
        foreach (var key in ModPathKeys(relativePath))
            if (mappings.TryGetValue(key, out var label))
                return label;
        return "Unmapped";
    }

    private static IReadOnlyList<string> OptionMembershipsFor(
        string relativePath,
        IReadOnlyDictionary<string, IReadOnlyList<string>> mappings)
    {
        foreach (var key in ModPathKeys(relativePath))
            if (mappings.TryGetValue(key, out var memberships))
                return memberships;
        return Array.Empty<string>();
    }

    private static IEnumerable<string> ModPathKeys(string path)
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

    private static void WriteJsonAtomic(string path, JsonObject value)
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

    private sealed record CleanupMapping(
        JsonObject Files,
        string GamePath,
        string OldRelativePath,
        string CanonicalPath);

    private sealed record ModCleanupResult(
        IReadOnlyList<string> Warnings,
        ModPathRemap? PathRemap);

    /// <summary>
    /// Normalize and deduplicate a committed mod without relying on a
    /// Penumbra-version-specific IPC endpoint. The complete directory is
    /// snapshotted before mutation so failures leave the committed mashup
    /// exactly as it was before cleanup started.
    /// </summary>
    private static ModCleanupResult NormalizeAndDeduplicateMod(string modRoot, string modDirectory)
    {
        try
        {
            var root = NormalizePhysicalPath(modRoot)
                ?? throw new InvalidDataException("The Penumbra mod root is invalid.");
            if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The Penumbra mod root is unavailable or unsafe.");

            var mappings = new List<CleanupMapping>();

            void AddContainer(JsonNode? container, string? groupName, string? optionName, string jsonPath)
            {
                if (container is not JsonObject value || value["Files"] is not JsonObject files)
                    return;

                var group = SafeModPathSegment(groupName);
                var option = SafeModPathSegment(optionName);
                foreach (var pair in files.ToArray())
                {
                    var oldRelative = JsonString(pair.Value);
                    if (string.IsNullOrWhiteSpace(oldRelative))
                        continue;
                    if (!TryNormalizeModContentPath(oldRelative, out var normalizedOld))
                        throw new InvalidDataException($"The mapping {jsonPath}:{pair.Key} has an unsafe content path.");

                    var gamePath = NormalizeCanonicalGamePath(pair.Key);
                    if (!TryNormalizeModContentPath(gamePath, out gamePath))
                        throw new InvalidDataException($"The mapping {jsonPath}:{pair.Key} has an unsafe game path.");

                    var canonical = BuildCanonicalModPath(group, option, gamePath);
                    mappings.Add(new CleanupMapping(files, pair.Key, normalizedOld, canonical));
                }
            }

            void AddGroup(JsonNode? group, string jsonPath)
            {
                if (group is not JsonObject value)
                    return;
                var groupName = JsonString(value["Name"]);
                if (string.IsNullOrWhiteSpace(groupName))
                    groupName = "Unnamed group";

                if (value["Files"] is JsonObject)
                    AddContainer(value, groupName, "None", jsonPath);
                if (value["Options"] is JsonArray options)
                    for (var index = 0; index < options.Count; ++index)
                    {
                        var option = options[index] as JsonObject;
                        AddContainer(option, groupName, JsonString(option?["Name"]) ?? $"Option {index + 1}",
                            $"{jsonPath}.Options[{index}]");
                    }
                if (value["Containers"] is JsonArray containers)
                    for (var index = 0; index < containers.Count; ++index)
                    {
                        var container = containers[index] as JsonObject;
                        AddContainer(container, groupName, JsonString(container?["Name"]) ?? $"Container {index + 1}",
                            $"{jsonPath}.Containers[{index}]");
                    }
            }

            var meta = LoadV4ModMetadata(root);
            AddContainer(meta["DefaultData"], null, null, "meta.json.DefaultData");
            if (meta["Groups"] is JsonArray groups)
                for (var index = 0; index < groups.Count; ++index)
                    AddGroup(groups[index], $"meta.json.Groups[{index}]");

            // A mod without any Files mappings cannot be safely normalized: it
            // may use a layout owned by a newer Penumbra schema. Keep it intact.
            if (mappings.Count == 0)
                return new ModCleanupResult([], null);

            var contentByHash = new Dictionary<string, List<(CleanupMapping Mapping, byte[] Bytes)>>(
                StringComparer.OrdinalIgnoreCase);
            var pathHash = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var mapping in mappings)
            {
                var source = SafeContentPath(root, mapping.OldRelativePath);
                if (!File.Exists(source) || !IsSafeModResourceFile(root, source))
                    throw new InvalidDataException($"The referenced content file is missing or unsafe: {mapping.OldRelativePath}");
                var bytes = File.ReadAllBytes(source);
                var hash = Convert.ToHexString(SHA256.HashData(bytes));
                if (pathHash.TryGetValue(mapping.CanonicalPath, out var existingHash) &&
                    !string.Equals(existingHash, hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Different files map to the same canonical path: {mapping.CanonicalPath}");
                pathHash[mapping.CanonicalPath] = hash;
                if (!contentByHash.TryGetValue(hash, out var sameContent))
                    contentByHash[hash] = sameContent = [];
                sameContent.Add((mapping, bytes));
            }

            var keeperByHash = contentByHash.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Select(item => item.Mapping.CanonicalPath)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(path => path, StringComparer.Ordinal)
                    .First(),
                StringComparer.OrdinalIgnoreCase);
            var desiredFiles = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var remappedPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var content in contentByHash)
            {
                var keeper = keeperByHash[content.Key];
                desiredFiles[keeper] = content.Value[0].Bytes;
                foreach (var item in content.Value)
                {
                    item.Mapping.Files[item.Mapping.GamePath] = keeper;
                    AddPathRemap(remappedPaths, item.Mapping.OldRelativePath, keeper);
                }
            }

            var image = JsonString(meta["Image"]);
            if (!string.IsNullOrWhiteSpace(image))
            {
                if (!TryNormalizeModContentPath(image, out var imagePath))
                    throw new InvalidDataException("The linked mod image path is unsafe.");
                var imagePhysical = SafeContentPath(root, imagePath);
                if (File.Exists(imagePhysical) && IsSafeModResourceFile(root, imagePhysical))
                {
                    var imageBytes = File.ReadAllBytes(imagePhysical);
                    if (desiredFiles.TryGetValue(imagePath, out var mappedBytes) &&
                        !mappedBytes.AsSpan().SequenceEqual(imageBytes))
                        throw new InvalidDataException($"The linked mod image collides with mapped content: {imagePath}");
                    desiredFiles[imagePath] = imageBytes;
                }
            }

            EnsureNoReparsePoints(root);
            var snapshot = CreateCleanupSnapshot(root);
            try
            {
                foreach (var file in desiredFiles)
                    WriteModContentAtomic(root, file.Key, file.Value);
                TouchV4ModMetadata(meta);
                WriteJsonAtomic(Path.Combine(root, "meta.json"), meta);

                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToArray())
                {
                    var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                    if (IsModMetadataFile(relative) || desiredFiles.ContainsKey(relative))
                        continue;
                    File.Delete(file);
                }
                DeleteEmptyDirectories(root);
                TryDeleteCleanupSnapshot(snapshot);
            }
            catch
            {
                RestoreCleanupSnapshot(root, snapshot);
                throw;
            }

            return new ModCleanupResult([], new ModPathRemap(modDirectory, root, remappedPaths));
        }
        catch (Exception e)
        {
            return new ModCleanupResult(
                [$"Mashup cleanup failed; the mashup was retained without normalization or deduplication: {e.Message}"],
                null);
        }
    }

    internal static (IReadOnlyList<string> Warnings, ModPathRemap? PathRemap)
        NormalizeAndDeduplicateModForRegression(string modRoot, string modDirectory)
    {
        var result = NormalizeAndDeduplicateMod(modRoot, modDirectory);
        return (result.Warnings, result.PathRemap);
    }

    private static string? SafeModPathSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        if (trimmed is "." or ".." || trimmed.EndsWith(".", StringComparison.Ordinal) ||
            trimmed.EndsWith(" ", StringComparison.Ordinal) || trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            trimmed.Any(char.IsControl) || trimmed.Contains('/') || trimmed.Contains('\\'))
            throw new InvalidDataException($"The Penumbra group or option name is not a safe path segment: {value}");
        return trimmed;
    }

    private static string BuildCanonicalModPath(string? group, string? option, string gamePath)
    {
        var parts = new List<string> { "Files" };
        if (group is not null)
            parts.Add(group);
        if (option is not null)
            parts.Add(option);
        parts.Add(gamePath);
        return string.Join('/', parts);
    }

    private static bool TryNormalizeModContentPath(string? path, out string normalized)
    {
        normalized = (path ?? string.Empty).Replace('\\', '/').Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        normalized = normalized.TrimStart('/');
        if (normalized.Length == 0 || normalized.Length > 4096 || normalized.Contains('\0') ||
            Path.IsPathRooted(normalized))
            return false;
        return normalized.Split('/').All(segment => segment.Length > 0 && segment is not ("." or ".."));
    }

    private static string SafeContentPath(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsPathWithin(path, root) || HasReparsePointInPath(root, path))
            throw new InvalidDataException($"The content path is outside the mod root: {relative}");
        return path;
    }

    private static void AddPathRemap(IDictionary<string, string> paths, string oldPath, string newPath)
    {
        paths[oldPath] = newPath;
        if (oldPath.StartsWith("Files/", StringComparison.OrdinalIgnoreCase))
            paths[oldPath[6..]] = newPath;
        else
            paths["Files/" + oldPath] = newPath;
    }

    private static bool IsModMetadataFile(string relative)
    {
        var normalized = relative.Replace('\\', '/');
        return normalized.IndexOf('/') < 0 &&
               (normalized.Equals(OwnershipMarkerFile, StringComparison.OrdinalIgnoreCase) ||
                normalized.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
    }

    private static void EnsureNoReparsePoints(string root)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"The mod contains an unsupported linked path: {entry}");
    }

    private static string CreateCleanupSnapshot(string root)
    {
        var parent = Path.GetDirectoryName(root)
            ?? throw new InvalidDataException("The mod root has no parent directory.");
        var snapshot = Path.Combine(parent, $".instant-edit-cleanup-{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(snapshot);
            CopyDirectoryContents(root, snapshot);
            return snapshot;
        }
        catch
        {
            TryDeleteCleanupSnapshot(snapshot);
            throw;
        }
    }

    private static void CopyDirectoryContents(string source, string destination)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var target = Path.Combine(destination, Path.GetFileName(entry));
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"The mod contains an unsupported linked path: {entry}");
            if ((attributes & FileAttributes.Directory) != 0)
            {
                Directory.CreateDirectory(target);
                CopyDirectoryContents(entry, target);
            }
            else
            {
                File.Copy(entry, target, false);
            }
        }
    }

    private static void RestoreCleanupSnapshot(string root, string snapshot)
    {
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(root).ToArray())
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    continue;
                if ((attributes & FileAttributes.Directory) != 0)
                    Directory.Delete(entry, true);
                else
                    File.Delete(entry);
            }
            CopyDirectoryContents(snapshot, root);
        }
        finally
        {
            TryDeleteCleanupSnapshot(snapshot);
        }
    }

    private static void TryDeleteCleanupSnapshot(string snapshot)
    {
        try
        {
            if (Directory.Exists(snapshot))
                Directory.Delete(snapshot, true);
        }
        catch
        {
            // A stale snapshot is harmless and can be removed on a later run.
        }
    }

    private static void WriteModContentAtomic(string root, string relative, byte[] bytes)
    {
        if (!TryNormalizeModContentPath(relative, out var normalized))
            throw new InvalidDataException("A normalized content path is unsafe.");
        var target = SafeContentPath(root, normalized);
        var parent = Path.GetDirectoryName(target)
            ?? throw new InvalidDataException("A normalized content path has no parent.");
        Directory.CreateDirectory(parent);
        var temporary = Path.Combine(parent, $".instant-edit-cleanup-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, target, true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static void DeleteEmptyDirectories(string root)
    {
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
    }

}
