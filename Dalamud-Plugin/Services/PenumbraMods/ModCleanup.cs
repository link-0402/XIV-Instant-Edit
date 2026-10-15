using System.Security.Cryptography;
using System.Text.Json.Nodes;
using static InstantEdit.Services.PenumbraMods.ModJson;
using static InstantEdit.Services.PenumbraMods.ModMetadata;
using static InstantEdit.Services.PenumbraMods.ModPaths;

namespace InstantEdit.Services.PenumbraMods;

/// <summary> Normalizes a committed mod's file layout and removes duplicate files, all or nothing. </summary>
internal static class ModCleanup
{
    internal const string OwnershipMarkerFile = ".instant-edit-owner.json";

    internal sealed record CleanupMapping(
        JsonObject Files,
        string GamePath,
        string OldRelativePath,
        string CanonicalPath);

    internal sealed record ModCleanupResult(
        IReadOnlyList<string> Warnings,
        ModPathRemap? PathRemap);

    /// <summary>
    /// Normalize and deduplicate a committed mod without relying on a
    /// Penumbra-version-specific IPC endpoint. The complete directory is
    /// snapshotted before mutation so failures leave the committed mashup
    /// exactly as it was before cleanup started.
    /// </summary>
    internal static ModCleanupResult NormalizeAndDeduplicateMod(string modRoot, string modDirectory)
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

                // Only files an old mapping pointed at can have become redundant; readmes,
                // images and unmapped extras belong to the mod and stay.
                var previouslyMapped = mappings.Select(mapping => mapping.OldRelativePath)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToArray())
                {
                    var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                    if (IsModMetadataFile(relative) || desiredFiles.ContainsKey(relative) ||
                        !previouslyMapped.Contains(relative))
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

    internal static string? SafeModPathSegment(string? value)
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

    internal static string BuildCanonicalModPath(string? group, string? option, string gamePath)
    {
        var parts = new List<string> { "Files" };
        if (group is not null)
            parts.Add(group);
        if (option is not null)
            parts.Add(option);
        parts.Add(gamePath);
        return string.Join('/', parts);
    }

    internal static bool TryNormalizeModContentPath(string? path, out string normalized)
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

    internal static string SafeContentPath(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsPathWithin(path, root) || HasReparsePointInPath(root, path))
            throw new InvalidDataException($"The content path is outside the mod root: {relative}");
        return path;
    }

    internal static void AddPathRemap(IDictionary<string, string> paths, string oldPath, string newPath)
    {
        paths[oldPath] = newPath;
        if (oldPath.StartsWith("Files/", StringComparison.OrdinalIgnoreCase))
            paths[oldPath[6..]] = newPath;
        else
            paths["Files/" + oldPath] = newPath;
    }

    internal static bool IsModMetadataFile(string relative)
    {
        var normalized = relative.Replace('\\', '/');
        return normalized.IndexOf('/') < 0 &&
               (normalized.Equals(OwnershipMarkerFile, StringComparison.OrdinalIgnoreCase) ||
                normalized.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
    }

    internal static void EnsureNoReparsePoints(string root)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"The mod contains an unsupported linked path: {entry}");
    }

    internal static string CreateCleanupSnapshot(string root)
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

    internal static void CopyDirectoryContents(string source, string destination)
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

    internal static void RestoreCleanupSnapshot(string root, string snapshot)
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
        catch (Exception e)
        {
            // The snapshot is now the only intact copy of the mod, so it stays.
            throw new IOException($"Could not restore the mod from its cleanup snapshot, kept at {snapshot}: {e.Message}", e);
        }
        TryDeleteCleanupSnapshot(snapshot);
    }

    internal static void TryDeleteCleanupSnapshot(string snapshot)
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

    internal static void WriteModContentAtomic(string root, string relative, byte[] bytes)
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

    internal static void DeleteEmptyDirectories(string root)
    {
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
    }
}
