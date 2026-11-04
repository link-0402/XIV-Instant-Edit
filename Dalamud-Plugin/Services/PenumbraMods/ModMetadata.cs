using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Timeline;
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

    /// <summary>The author of new mods when Settings names none.</summary>
    internal const string DefaultAuthor = "XIV Instant Edit";

    /// <summary>The author written into a new mod: <paramref name="author"/> from Settings, else <see cref="DefaultAuthor"/>.</summary>
    internal static string AuthorOrDefault(string? author) => string.IsNullOrWhiteSpace(author) ? DefaultAuthor : author.Trim();

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

        // Read, changed and written under the gate every writer of a mod's meta.json takes, so a Studio save isn't lost.
        lock (Studio.Services.Project.ModFiles.Gate)
        {
            var meta = LoadV4ModMetadata(modRoot);
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
        }
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

    /// <summary>
    /// The mod-relative file the mod maps <paramref name="gamePath"/> to beside <paramref name="ownerFile"/>:
    /// from a container that maps <paramref name="ownerGamePath"/> to that file, else the first mapping
    /// (default files come first); null when the mod doesn't map it or can't be read.
    /// </summary>
    internal static string? MappedFileBeside(string modRoot, string ownerGamePath, string ownerFile, string gamePath)
    {
        try
        {
            var containers = AnimationMappingContainers(LoadV4ModMetadata(modRoot)).ToList();
            var owner = NormalizedModFile(ownerFile);
            string? In(JsonObject files) => files.Where(pair => SameGamePath(pair.Key, gamePath))
                .Select(pair => NormalizedModFile(JsonString(pair.Value))).FirstOrDefault(file => file is { Length: > 0 });
            foreach (var files in containers)
                if (files.Any(pair => SameGamePath(pair.Key, ownerGamePath) && string.Equals(NormalizedModFile(JsonString(pair.Value)), owner, StringComparison.OrdinalIgnoreCase))
                    && In(files) is { } beside)
                    return beside;
            return containers.Select(In).FirstOrDefault(file => file != null);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string? NormalizedModFile(string? value) => value?.Replace('\\', '/').Trim().TrimStart('/');

    /// <summary>A game path as <see cref="SameGamePath"/> compares it, for a dictionary that ignores case.</summary>
    private static string GamePathKey(string gamePath) => gamePath.Replace('\\', '/');

    /// <summary>
    /// Map <paramref name="gamePath"/> to <paramref name="relativePath"/> in every DefaultData,
    /// group, option and container that maps <paramref name="ownerGamePath"/>, so a file an
    /// animation edit added (an imported sound) plays wherever that animation does, or a copy
    /// wherever its original is. Refuses when the mod already maps <paramref name="gamePath"/> anywhere
    /// to another file; a mapping to this same file is kept as it is (a save made again).
    /// With <paramref name="ownerFile"/>, only where the owner is that mod-relative file: a copy made
    /// from one of the files a mod's options map its original to. Then only those must not map
    /// <paramref name="gamePath"/> yet, since the other files' own copies take it in the others.
    /// </summary>
    internal static void AddAnimationResourceMapping(string modRoot, string ownerGamePath, string gamePath, string relativePath,
        string? ownerFile = null) => AddAnimationResourceMappings(modRoot, ownerGamePath, [(gamePath, relativePath)], ownerFile);

    /// <summary>
    /// <see cref="AddAnimationResourceMapping"/> for several game paths at once, in one write of meta.json: a file
    /// and the game paths that share it (a prop material's 256 version folders). Nothing is written when one is refused.
    /// </summary>
    internal static void AddAnimationResourceMappings(string modRoot, string ownerGamePath,
        IReadOnlyList<(string GamePath, string RelativePath)> mappings, string? ownerFile = null)
    {
        lock (Studio.Services.Project.ModFiles.Gate)
        {
            var meta = LoadV4ModMetadata(modRoot);
            var containers = AnimationMappingContainers(meta).ToArray();
            var owners = containers.Where(files => files.Any(pair => SameGamePath(pair.Key, ownerGamePath) &&
                (ownerFile == null || string.Equals(NormalizedModFile(JsonString(pair.Value)), NormalizedModFile(ownerFile),
                    StringComparison.OrdinalIgnoreCase))))
                .ToArray();
            // By game path as SameGamePath compares them, so a mod with thousands of files is read once per container.
            var wanted = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (gamePath, relativePath) in mappings)
                if (!wanted.TryAdd(GamePathKey(gamePath), NormalizedModFile(relativePath)))
                    throw new InvalidDataException($"{gamePath} is mapped twice.");
            foreach (var files in ownerFile == null ? containers : owners)
                foreach (var pair in files)
                    if (wanted.TryGetValue(GamePathKey(pair.Key), out var file) &&
                        !string.Equals(NormalizedModFile(JsonString(pair.Value)), file, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"The mod already maps {pair.Key}.");
            if (owners.Length == 0) throw new InvalidDataException($"The mod no longer maps {ownerGamePath}{(ownerFile == null ? "" : $" to {ownerFile}")}.");
            foreach (var files in owners)
            {
                var mapped = files.Select(pair => GamePathKey(pair.Key)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var (gamePath, relativePath) in mappings)
                    if (!mapped.Contains(GamePathKey(gamePath)))
                        files[gamePath] = relativePath;
            }
            TouchV4ModMetadata(meta);
            WriteJsonAtomic(Path.Combine(modRoot, "meta.json"), meta);
        }
    }

    /// <summary>
    /// Undo <see cref="AddAnimationResourceMapping"/>: drop every mapping of <paramref name="gamePath"/>
    /// to <paramref name="relativePath"/>. Nothing to drop is fine, since a failed commit rolls back
    /// before it mapped anything.
    /// </summary>
    internal static void RemoveAnimationResourceMapping(string modRoot, string gamePath, string relativePath)
        => RemoveAnimationResourceMappings(modRoot, [(gamePath, relativePath)]);

    /// <summary><see cref="RemoveAnimationResourceMapping"/> for several game paths at once, in one write of meta.json.</summary>
    internal static void RemoveAnimationResourceMappings(string modRoot, IReadOnlyList<(string GamePath, string RelativePath)> mappings)
    {
        var dropped = new HashSet<(string, string)>(mappings.Select(m => (GamePathKey(m.GamePath).ToUpperInvariant(),
            m.RelativePath.Replace('\\', '/').Trim().ToUpperInvariant())));
        lock (Studio.Services.Project.ModFiles.Gate)
        {
            var meta = LoadV4ModMetadata(modRoot);
            var removed = false;
            foreach (var files in AnimationMappingContainers(meta))
                foreach (var pair in files.ToArray())
                    if (JsonString(pair.Value) is { } value &&
                        dropped.Contains((GamePathKey(pair.Key).ToUpperInvariant(), value.Replace('\\', '/').Trim().ToUpperInvariant())))
                    {
                        files.Remove(pair.Key);
                        removed = true;
                    }
            if (!removed) return;
            TouchV4ModMetadata(meta);
            WriteJsonAtomic(Path.Combine(modRoot, "meta.json"), meta);
        }
    }

    /// <summary>
    /// Maps <paramref name="gamePath"/> to <paramref name="relativePath"/> in the mod's default data, in one write of
    /// meta.json: a library's file for a face folder it had none for. Refuses when the mod maps that game path anywhere.
    /// </summary>
    internal static void AddDefaultFileMapping(string modRoot, string gamePath, string relativePath)
    {
        lock (Studio.Services.Project.ModFiles.Gate)
        {
            var meta = LoadV4ModMetadata(modRoot);
            if (AnimationMappingContainers(meta).Any(files => files.Any(pair => SameGamePath(pair.Key, gamePath))))
                throw new InvalidDataException($"The mod already maps {gamePath}.");
            if (meta["DefaultData"] is not JsonObject defaults) meta["DefaultData"] = defaults = new JsonObject();
            if (defaults["Files"] is not JsonObject files) defaults["Files"] = files = new JsonObject();
            files[gamePath] = relativePath;
            TouchV4ModMetadata(meta);
            WriteJsonAtomic(Path.Combine(modRoot, "meta.json"), meta);
        }
    }

    /// <summary>
    /// Adds <paramref name="manipulations"/> (Penumbra JSON) to every DefaultData, group, option and container that
    /// maps <paramref name="ownerGamePath"/>, as <see cref="AddAnimationResourceMapping"/> maps files: one there with
    /// the same identifier (its type and every field but the entry) is replaced in its place. One there with the
    /// identifier of one of <paramref name="removed"/> is taken out. <paramref name="record"/> gets the changes before
    /// meta.json is written, so a journal holds them if writing fails.
    /// </summary>
    internal static IReadOnlyList<AnimationManipulationChange> AddAnimationManipulations(string modRoot, string ownerGamePath,
        IReadOnlyList<string> manipulations, Action<IReadOnlyList<AnimationManipulationChange>>? record = null,
        IReadOnlyList<string>? removed = null)
    {
        lock (Studio.Services.Project.ModFiles.Gate)
        {
            var meta = LoadV4ModMetadata(modRoot);
            var owners = ManipulationContainers(meta)
                .Where(c => c.Container["Files"] is JsonObject files && files.Any(pair => SameGamePath(pair.Key, ownerGamePath))).ToArray();
            if (owners.Length == 0) throw new InvalidDataException($"The mod no longer maps {ownerGamePath}.");
            var changes = new List<AnimationManipulationChange>();
            foreach (var (locator, container) in owners)
            {
                if (container["Manipulations"] is not JsonArray list)
                    container["Manipulations"] = list = new JsonArray();
                foreach (var text in manipulations)
                {
                    var added = JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException("A manipulation is not a JSON object.");
                    var at = IndexOfManipulation(list, added);
                    changes.Add(new AnimationManipulationChange(locator, added.ToJsonString(), at < 0 ? null : list[at]!.ToJsonString()));
                    if (at < 0) list.Add(added);
                    else list[at] = added;
                }
                foreach (var text in removed ?? [])
                {
                    var at = IndexOfManipulation(list, JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException("A manipulation is not a JSON object."));
                    if (at < 0)
                        continue;
                    changes.Add(new AnimationManipulationChange(locator, null, list[at]!.ToJsonString()));
                    list.RemoveAt(at);
                }
            }
            record?.Invoke(changes);
            if (changes.Count == 0)
                return changes;
            TouchV4ModMetadata(meta);
            WriteJsonAtomic(Path.Combine(modRoot, "meta.json"), meta);
            return changes;
        }
    }

    /// <summary>
    /// Before undoing <paramref name="changes"/>: each option must still hold what the edit wrote, or what it replaced
    /// (none when it replaced none), which is what undone looks like. Anything else is newer work.
    /// </summary>
    internal static void CheckAnimationManipulations(string modRoot, IReadOnlyList<AnimationManipulationChange> changes)
    {
        lock (Studio.Services.Project.ModFiles.Gate)
            CheckAnimationManipulations(LoadV4ModMetadata(modRoot), changes);
    }

    private static void CheckAnimationManipulations(JsonObject meta, IReadOnlyList<AnimationManipulationChange> changes)
    {
        foreach (var change in changes)
        {
            var now = CurrentManipulation(meta, change);
            if (now != Canonical(change.Written) && now != Canonical(change.Replaced))
                throw new IOException("A manipulation this edit wrote into the mod was changed since. Undo will not overwrite it.");
        }
    }

    /// <summary>
    /// Undo <see cref="AddAnimationManipulations"/>, last change first: where an option still holds what was
    /// written, the one it replaced goes back in its place, or it is removed; one the edit took out comes back where
    /// the option holds none there. Nothing to undo is fine.
    /// </summary>
    internal static void RemoveAnimationManipulations(string modRoot, IReadOnlyList<AnimationManipulationChange> changes)
    {
        lock (Studio.Services.Project.ModFiles.Gate)
        {
            var meta = LoadV4ModMetadata(modRoot);
            var containers = ManipulationContainers(meta).ToDictionary(c => c.Locator, c => c.Container, StringComparer.Ordinal);
            var changed = false;
            foreach (var change in changes.Reverse())
            {
                if (!containers.TryGetValue(change.Container, out var container))
                    continue;
                if (change.Written == null)
                {
                    if (change.Replaced is not { } taken)
                        continue;
                    if (container["Manipulations"] is not JsonArray into)
                        container["Manipulations"] = into = new JsonArray();
                    var back = JsonNode.Parse(taken) as JsonObject ?? throw new InvalidDataException("A manipulation is not a JSON object.");
                    if (IndexOfManipulation(into, back) >= 0)
                        continue;
                    into.Add(back);
                    changed = true;
                    continue;
                }
                if (container["Manipulations"] is not JsonArray list)
                    continue;
                var written = JsonNode.Parse(change.Written) as JsonObject ?? throw new InvalidDataException("A manipulation is not a JSON object.");
                var at = IndexOfManipulation(list, written);
                if (at < 0 || Canonical(list[at]) != Canonical(written))
                    continue;
                if (change.Replaced == null) list.RemoveAt(at);
                else list[at] = JsonNode.Parse(change.Replaced);
                changed = true;
            }
            if (!changed) return;
            TouchV4ModMetadata(meta);
            WriteJsonAtomic(Path.Combine(modRoot, "meta.json"), meta);
        }
    }

    /// <summary>What the option <paramref name="change"/> names holds at its manipulation's identifier now, <see cref="Canonical(JsonNode?)"/>; null for none.</summary>
    private static string? CurrentManipulation(JsonObject meta, AnimationManipulationChange change)
    {
        var container = ManipulationContainers(meta).FirstOrDefault(c => c.Locator == change.Container).Container;
        if (container?["Manipulations"] is not JsonArray list)
            return null;
        var at = IndexOfManipulation(list, JsonNode.Parse(change.Written ?? change.Replaced ?? "null") as JsonObject
            ?? throw new InvalidDataException("A manipulation is not a JSON object."));
        return at < 0 ? null : Canonical(list[at]);
    }

    /// <summary>
    /// The attach overrides (Penumbra <c>Atch</c> manipulations) of the mod at <paramref name="modRoot"/> for the races in
    /// <paramref name="genderRaces"/>, in the options that map <paramref name="ownerGamePath"/>: the first per race,
    /// point and state.
    /// </summary>
    internal static ImmutableArray<AtchManipulation> AttachOverrides(string modRoot, string ownerGamePath, IReadOnlyList<ushort> genderRaces)
    {
        static float Number(JsonNode? node) => node is JsonValue value && value.TryGetValue<double>(out var number) ? (float)number : 0;
        var names = genderRaces.ToDictionary(race => AnimationMetadata.GenderRaceNames(race), race => race);
        var result = ImmutableArray.CreateBuilder<AtchManipulation>();
        JsonObject meta;
        lock (Studio.Services.Project.ModFiles.Gate)
            meta = LoadV4ModMetadata(modRoot);
        var owners = ManipulationContainers(meta)
            .Where(c => c.Container["Files"] is JsonObject files && files.Any(pair => SameGamePath(pair.Key, ownerGamePath)));
        foreach (var item in owners.SelectMany(c => (c.Container["Manipulations"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()))
        {
            if (JsonString(item["Type"]) != "Atch" || item["Manipulation"] is not JsonObject fields || fields["Entry"] is not JsonObject entry ||
                JsonString(fields["Type"]) is not { Length: 3 } code || JsonString(entry["Bone"]) is not { } bone ||
                !names.TryGetValue((JsonString(fields["Gender"]) ?? "", JsonString(fields["Race"]) ?? ""), out var race))
                continue;
            var state = (int)Number(fields["Index"]);
            if (result.Any(m => m.GenderRace == race && m.Code == code && m.State == state))
                continue;
            result.Add(new AtchManipulation(race, code, state, new AtchEntry(bone, Number(entry["Scale"]),
                new Vector3(Number(entry["OffsetX"]), Number(entry["OffsetY"]), Number(entry["OffsetZ"])),
                new Vector3(Number(entry["RotationX"]), Number(entry["RotationY"]), Number(entry["RotationZ"])))));
        }
        return result.ToImmutable();
    }

    private static string? Canonical(string? json) => json == null ? null : Canonical(JsonNode.Parse(json));

    /// <summary>
    /// JSON by meaning, for telling whether a manipulation is still the one written: keys in order and numbers by value,
    /// since Penumbra saving the mod again writes 1.0 where this writes 1.
    /// </summary>
    private static string Canonical(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject item => "{" + string.Join(",", item.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => JsonSerializer.Serialize(p.Key) + ":" + Canonical(p.Value))) + "}",
        JsonArray list => "[" + string.Join(",", list.Select(Canonical)) + "]",
        JsonValue value when value.GetValueKind() == JsonValueKind.Number =>
            value.GetValue<double>().ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        _ => node.ToJsonString(),
    };

    /// <summary>Where <paramref name="list"/> holds a manipulation with <paramref name="manipulation"/>'s identifier: its type and every field but the entry.</summary>
    internal static int IndexOfManipulation(JsonArray list, JsonObject manipulation)
    {
        static string Identity(JsonNode? node) => node is JsonObject { } item && item["Manipulation"] is JsonObject fields
            ? JsonString(item["Type"]) + "|" + string.Join("|", fields.Where(f => f.Key != "Entry").OrderBy(f => f.Key, StringComparer.Ordinal)
                .Select(f => f.Key + "=" + Canonical(f.Value)))
            : "";
        var identity = Identity(manipulation);
        for (var i = 0; i < list.Count; i++)
            if (Identity(list[i]) == identity)
                return i;
        return -1;
    }

    /// <summary>
    /// The DefaultData, groups, options and containers of a mod that carry files, each with a name a journal can
    /// find it by again: "DefaultData", or the group and its option or container by their Id, else by position.
    /// </summary>
    private static IEnumerable<(string Locator, JsonObject Container)> ManipulationContainers(JsonObject meta)
    {
        static string Name(JsonObject item, int index) => JsonString(item["Id"]) is { Length: > 0 } id ? id : "#" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (meta["DefaultData"] is JsonObject defaults) yield return ("DefaultData", defaults);
        if (meta["Groups"] is not JsonArray groups) yield break;
        for (var g = 0; g < groups.Count; g++)
        {
            if (groups[g] is not JsonObject group) continue;
            var name = "Groups/" + Name(group, g);
            if (group["Files"] is JsonObject) yield return (name, group);
            foreach (var key in new[] { "Options", "Containers" })
                if (group[key] is JsonArray entries)
                    for (var o = 0; o < entries.Count; o++)
                        if (entries[o] is JsonObject entry)
                            yield return ($"{name}/{key}/{Name(entry, o)}", entry);
        }
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
            DurableFile.WriteAllText(tempPath, value.ToJsonString(JsonOpts));
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
