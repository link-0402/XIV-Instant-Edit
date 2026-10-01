using System.Text.Json.Nodes;
using InstantEdit.Services.PreviewMods;
using Penumbra.Api.Enums;
using Penumbra.Api.IpcSubscribers;

namespace InstantEdit.Services;

/// <summary> How a preview mod's source file is used inside its mod, for the confirmation before applying. </summary>
internal sealed record ModFileUsage(IReadOnlyList<string> Options, bool SeveralGamePaths);

public sealed partial class PenumbraService
{
    /// <summary> Preview mods, and the mods holding fixes to game files, win over the mods they patch. </summary>
    internal const int PreviewModPriority = 1_000_000;

    /// <summary>
    /// Creates a new Penumbra mod holding <paramref name="files"/> in its default option, enables it
    /// with a high priority in the actor's collection, and redraws the actor. The name gets a " (2)",
    /// " (3)" … suffix when a mod of that name exists; <paramref name="fallbackName"/> replaces a name
    /// a folder can't hold.
    /// </summary>
    internal async Task<PreviewModResult> CreatePreviewModAsync(string name, string description,
        IReadOnlyList<PreviewModEntry> files, int objectIndex, string fallbackName, CancellationToken token)
    {
        if (files.Count == 0)
            throw new InvalidOperationException("There are no files to put in the mod.");
        foreach (var file in files)
        {
            if (!IsSafeRelativeResourcePath(file.RelativePath) || file.Bytes.Length == 0 || file.GamePaths.Count == 0)
                throw new InvalidDataException($"The mod file {file.RelativePath} is invalid.");
            foreach (var gamePath in file.GamePaths)
                if (!IsSafeGameResourcePath(NormalizeGamePath(gamePath), ".mdl", ".mtrl", ".tex"))
                    throw new InvalidDataException($"The mod file {gamePath} is invalid.");
        }
        var collection = await GetCollectionTargetAsync(objectIndex).ConfigureAwait(false)
            ?? throw new IOException("The character's Penumbra collection is unavailable. Refresh and try again.");

        await _exportGate.WaitAsync(token).ConfigureAwait(false);
        string modName;
        string folder;
        try
        {
            var root = await _framework.RunOnFrameworkThread(GetModDirectory).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                throw new IOException("Penumbra's mod directory is unavailable.");
            root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Penumbra's mod directory is an unsupported linked folder.");
            var modList = await _framework.RunOnFrameworkThread(() => TryGetModList(out var mods) ? mods : null).ConfigureAwait(false)
                ?? throw new IOException("Could not retrieve the Penumbra mod list.");
            await LeaveFrameworkThread();

            modName = UniqueModName(name, candidate =>
                modList.Keys.Any(key => string.Equals(key, candidate, StringComparison.OrdinalIgnoreCase)) ||
                modList.Values.Any(value => string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase)) ||
                Directory.Exists(Path.Combine(root, candidate)) || File.Exists(Path.Combine(root, candidate)), fallbackName);
            folder = Path.GetFullPath(Path.Combine(root, modName));
            if (!IsPathWithin(folder, root))
                throw new IOException("The new mod's folder is outside Penumbra's mod directory.");

            var staging = Path.Combine(root, $".instant-edit-preview-{Guid.NewGuid():N}.tmp");
            try
            {
                Directory.CreateDirectory(staging);
                var mappings = new JsonObject();
                foreach (var file in files)
                {
                    WriteBytesAtomic(staging, file.RelativePath, file.Bytes);
                    foreach (var gamePath in file.GamePaths)
                        mappings[NormalizeGamePath(gamePath)] = file.RelativePath;
                }
                WriteJsonAtomic(Path.Combine(staging, "meta.json"), CreateV4ModMetadata(modName, "XIV Instant Edit", description, "",
                    new JsonObject { ["Files"] = mappings, ["FileSwaps"] = new JsonObject(), ["Manipulations"] = new JsonArray() }));
                token.ThrowIfCancellationRequested();
                Directory.Move(staging, folder);
            }
            finally
            {
                if (Directory.Exists(staging))
                    TryDeleteMashupNamespace(root, staging);
            }
        }
        finally
        {
            _exportGate.Release();
        }

        var identifier = ReadModStableIdentifier(folder) ?? Guid.Empty;
        var warnings = new List<string>();
        var added = await AddNewModAsync(modName).ConfigureAwait(false);
        if (added is not null)
            warnings.Add(added.Message);
        else
        {
            var configure = await _framework.RunOnFrameworkThread(() => ConfigureModForCollectionOnFramework(
                modName, collection.Id, collection.Name, setPriority: true, priority: PreviewModPriority, redraw: false)).ConfigureAwait(false);
            if (!configure.Success)
                warnings.Add(configure.Message);
            warnings.AddRange(configure.WarningList);
        }
        if (await RedrawPreviewActorAsync(objectIndex).ConfigureAwait(false) is { } redraw)
            warnings.Add(redraw);
        return new PreviewModResult(modName, identifier, collection.Id, collection.Name, warnings);
    }

    internal static string UniqueModName(string name, Func<string, bool> taken, string fallbackName = "XIV Instant Edit Mod")
    {
        var baseName = string.Concat(name.Where(c => !char.IsControl(c) && Array.IndexOf(Path.GetInvalidFileNameChars(), c) < 0)).Trim().TrimEnd('.');
        if (baseName.Length > 100)
            baseName = baseName[..100].TrimEnd();
        if (!IsSafeNewModName(baseName))
            baseName = fallbackName;
        for (var attempt = 1; attempt < 1000; attempt++)
        {
            var candidate = attempt == 1 ? baseName : $"{baseName} ({attempt})";
            if (IsSafeNewModName(candidate) && !taken(candidate))
                return candidate;
        }
        throw new IOException("Could not find a free mod name.");
    }

    internal Task<string?> PreviewModRootAsync() => _framework.RunOnFrameworkThread(GetModDirectory);

    /// <summary>
    /// Deletes a mod a preview created, after checking its identifier still matches. Removed is true
    /// when the mod is gone afterwards or the folder now holds something else (so the preview no
    /// longer exists); false only when Penumbra refused.
    /// </summary>
    internal async Task<(bool Removed, string? Warning)> DeletePreviewModAsync(string modDirectory, Guid identifier, int objectIndex)
    {
        if (!IsSafeModName(modDirectory))
            return (true, "The preview mod's folder name is invalid.");
        var root = await _framework.RunOnFrameworkThread(GetModDirectory).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(root))
            return (false, "Penumbra's mod directory is unavailable.");
        var folder = Path.Combine(root, modDirectory);
        if (Directory.Exists(folder))
        {
            if (identifier == Guid.Empty || ReadModStableIdentifier(folder) != identifier)
                return (true, $"The mod folder {modDirectory} no longer holds the preview mod, so it was left alone.");
            var result = await _framework.RunOnFrameworkThread(() => new DeleteMod(_pi).Invoke(modDirectory)).ConfigureAwait(false);
            if (result is not (PenumbraApiEc.Success or PenumbraApiEc.NothingChanged or PenumbraApiEc.ModMissing))
                return (false, $"Penumbra could not delete the preview mod ({result}); delete {modDirectory} in Penumbra.");
        }
        return (true, await RedrawPreviewActorAsync(objectIndex).ConfigureAwait(false));
    }

    /// <summary>
    /// Writes a preview's files over their files in the source mods, and adds its new files to the
    /// mods of the materials that read them (see <see cref="PlanFileAdditions"/>). Every destination is
    /// checked before anything is written: the mod must still be registered, a file must still sit at
    /// its relative path and must still hash as it did when it was read, and no option may map a new
    /// file's game path to something else. Each replaced file is backed up in the managed backup store;
    /// the mods are reloaded afterwards.
    /// </summary>
    /// <param name="recheck">What to do after a conflict, such as "Measure the seam again".</param>
    /// <param name="additions">New files, each with the source of the material that reads it at its game path.</param>
    internal async Task<IReadOnlyList<string>> WritePreviewSourcesAsync(IReadOnlyList<(PreviewSource Source, byte[] Bytes)> writes, int objectIndex,
        string recheck, IReadOnlyList<(PreviewSource Material, string GamePath, byte[] Bytes)>? additions = null)
    {
        if (_backups is null)
            throw new IOException("Managed backup storage is unavailable, so the source mods can't be changed safely.");
        additions ??= [];
        await _exportGate.WaitAsync().ConfigureAwait(false);
        try
        {
            async Task<(string File, string Root)> Locate(PreviewSource source)
            {
                if (!source.IsModFile || !IsSafeModName(source.ModDirectory) || !IsSafeRelativeResourcePath(source.RelativePath))
                    throw new IOException($"{source.Label} is not a writable mod file.");
                var scan = await _framework.RunOnFrameworkThread(() => ResolveModScanOnFramework(source.ModDirectory, source.ModStableId)).ConfigureAwait(false)
                    ?? throw new TextureConflictException($"The mod {source.ModName} is no longer registered in Penumbra.");
                var file = Path.GetFullPath(source.ActualPath);
                var root = scan.CandidateRoots.FirstOrDefault(candidate =>
                    string.Equals(Path.GetFullPath(Path.Combine(candidate, source.RelativePath.Replace('/', Path.DirectorySeparatorChar))), file,
                        StringComparison.OrdinalIgnoreCase));
                if (root is null || !IsSafeModResourceFile(root, file) || !File.Exists(file))
                    throw new TextureConflictException($"{source.Label} moved or is no longer part of its mod. {recheck}.");
                return (file, root);
            }

            var targets = new List<(string File, PreviewSource Source, byte[] Bytes)>();
            foreach (var (source, bytes) in writes)
            {
                var (file, _) = await Locate(source).ConfigureAwait(false);
                if (!string.Equals(PreviewSource.Hash(await File.ReadAllBytesAsync(file).ConfigureAwait(false)), source.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new TextureConflictException($"{source.Label} changed since it was read. {recheck}.");
                targets.Add((file, source, bytes));
            }
            var plans = new List<FileAdditionPlan>();
            foreach (var group in additions.GroupBy(a => (a.Material.ModDirectory.ToLowerInvariant(), a.Material.RelativePath.Replace('\\', '/').ToLowerInvariant())))
            {
                var material = group.First().Material;
                var (file, root) = await Locate(material).ConfigureAwait(false);
                // A candidate root can be the mod's Files folder; the metadata sits next to it.
                var modRoot = File.Exists(Path.Combine(root, "meta.json")) || Directory.GetParent(root) is not { } parent ||
                              !File.Exists(Path.Combine(parent.FullName, "meta.json"))
                    ? root
                    : parent.FullName;
                plans.Add(PlanFileAdditions(modRoot, material.ModDirectory, file, material.GamePath, group.Select(a => (a.GamePath, a.Bytes)).ToList(), recheck));
            }

            // New files first, so a material written below never names a texture its mod lacks.
            foreach (var plan in plans)
                CommitFileAdditions(plan, _backups);
            foreach (var (file, source, bytes) in targets)
            {
                _backups.Create(file, source.ModDirectory, source.RelativePath);
                var temporary = file + $".instant-edit-{Guid.NewGuid():N}.tmp";
                try
                {
                    await File.WriteAllBytesAsync(temporary, bytes).ConfigureAwait(false);
                    File.Move(temporary, file, true);
                }
                finally
                {
                    if (File.Exists(temporary))
                        File.Delete(temporary);
                }
            }
        }
        finally
        {
            _exportGate.Release();
        }

        var warnings = new List<string>();
        foreach (var mod in writes.Select(w => w.Source.ModDirectory).Concat(additions.Select(a => a.Material.ModDirectory)).Distinct(StringComparer.OrdinalIgnoreCase))
            if (await _framework.RunOnFrameworkThread(() => ReloadModOnFramework(mod)).ConfigureAwait(false) is { } reload)
                warnings.Add(reload.Message);
        if (await RedrawPreviewActorAsync(objectIndex).ConfigureAwait(false) is { } redraw)
            warnings.Add(redraw);
        return warnings;
    }

    /// <summary> New files for one mod, checked and placed: each file's relative path and full path, and the metadata entries that map the material. </summary>
    internal sealed record FileAdditionPlan(string ModRoot, string ModDirectory, JsonObject Meta, IReadOnlyList<JsonObject> Entries,
        IReadOnlyList<(string GamePath, string RelativePath, string File, byte[] Bytes)> Files);

    /// <summary>
    /// Places new files beside a material in its mod: each goes where the mod keeps its game path (the
    /// material's relative path shows the layout, such as a Files folder or an option folder, or else
    /// next to the material), and will be mapped by every default, option or container entry that maps
    /// the material, so the two always load together. Nothing is written; throws when the metadata
    /// isn't Penumbra 1.7's, when no entry maps the material, or when an entry maps a new game path to
    /// another file.
    /// </summary>
    internal static FileAdditionPlan PlanFileAdditions(string modRoot, string modDirectory, string materialFile, string materialGamePath,
        IReadOnlyList<(string GamePath, byte[] Bytes)> files, string recheck)
    {
        var meta = LoadV4ModMetadata(modRoot);
        var materialRelative = Path.GetRelativePath(modRoot, Path.GetFullPath(materialFile)).Replace('\\', '/');
        var material = NormalizeGamePath(materialGamePath);
        var prefix = materialRelative.EndsWith(material, StringComparison.OrdinalIgnoreCase) &&
                     (materialRelative.Length == material.Length || materialRelative[^(material.Length + 1)] == '/')
            ? materialRelative[..^material.Length]
            : null;
        var folder = materialRelative.Contains('/') ? materialRelative[..(materialRelative.LastIndexOf('/') + 1)] : "";

        bool MapsTo(JsonNode? value, string relative) => ModPathKeys(JsonString(value) ?? "").Any(key => string.Equals(key, relative, StringComparison.OrdinalIgnoreCase));
        var entries = new List<JsonObject>();
        void Visit(JsonNode? node)
        {
            if (node is JsonObject entry && entry["Files"] is JsonObject map && map.Any(pair => MapsTo(pair.Value, materialRelative)))
                entries.Add(entry);
        }
        Visit(meta["DefaultData"]);
        if (meta["Groups"] is JsonArray groups)
            foreach (var group in groups.OfType<JsonObject>())
                foreach (var key in new[] { "Options", "Containers" })
                    if (group[key] is JsonArray list)
                        foreach (var entry in list)
                            Visit(entry);
        if (entries.Count == 0)
            throw new TextureConflictException($"{modDirectory} no longer maps {materialRelative}. {recheck}.");

        var planned = new List<(string, string, string, byte[])>();
        foreach (var (gamePath, bytes) in files)
        {
            var path = NormalizeGamePath(gamePath);
            var relative = prefix is not null ? prefix + path : folder + path[(path.LastIndexOf('/') + 1)..];
            if (!IsSafeGameResourcePath(path, ".tex") || !IsSafeRelativeResourcePath(relative) || bytes.Length == 0)
                throw new InvalidDataException($"The new file {path} can't be added to {modDirectory}.");
            var target = Path.GetFullPath(Path.Combine(modRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsPathWithin(target, modRoot) || HasReparsePointInPath(modRoot, Path.GetDirectoryName(target)!) ||
                File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"The new file {relative} would leave {modDirectory}'s folder.");
            foreach (var entry in entries)
                foreach (var (key, value) in (JsonObject)entry["Files"]!)
                    if (SameGamePath(key, path) && !MapsTo(value, relative))
                        throw new TextureConflictException($"{modDirectory} already maps {path} to another file, so the new texture can't be added. {recheck}.");
            planned.Add((path, relative, target, bytes));
        }
        return new FileAdditionPlan(modRoot, modDirectory, meta, entries, planned);
    }

    /// <summary>
    /// Writes a plan's files and maps them. A file already there (an earlier match's) is backed up and
    /// replaced; files this call created are removed again when the metadata can't be written.
    /// </summary>
    internal static void CommitFileAdditions(FileAdditionPlan plan, ModelBackupStore? backups)
    {
        var created = new List<string>();
        try
        {
            foreach (var (_, relative, target, bytes) in plan.Files)
            {
                var existed = File.Exists(target);
                if (existed)
                    backups?.Create(target, plan.ModDirectory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var temporary = target + $".instant-edit-{Guid.NewGuid():N}.tmp";
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
                if (!existed)
                    created.Add(target);
            }
            foreach (var entry in plan.Entries)
            {
                var map = (JsonObject)entry["Files"]!;
                foreach (var (gamePath, relative, _, _) in plan.Files)
                {
                    foreach (var key in map.Where(pair => SameGamePath(pair.Key, gamePath)).Select(pair => pair.Key).ToArray())
                        map.Remove(key);
                    map[gamePath] = relative.Replace('/', '\\');
                }
            }
            TouchV4ModMetadata(plan.Meta);
            WriteJsonAtomic(Path.Combine(plan.ModRoot, "meta.json"), plan.Meta);
        }
        catch
        {
            foreach (var file in created)
            {
                try { File.Delete(file); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            throw;
        }
    }

    /// <summary> The managed backups kept of a mod file a preview can write, newest first. </summary>
    internal IReadOnlyList<ManagedBackup> ManagedBackupsFor(PreviewSource source)
        => _backups is not null && source.IsModFile ? _backups.List(source.ModDirectory, source.RelativePath) : [];

    /// <summary>
    /// Puts managed backups back over their mod files. Each file must still sit at its place in its
    /// registered mod. The current file is backed up first, so a restore can be undone the same way,
    /// then replaced; the mods are reloaded and the character redrawn.
    /// </summary>
    /// <param name="recheck">What to do after a file moved, such as "Measure the seams again".</param>
    internal async Task<IReadOnlyList<string>> RestorePreviewSourcesAsync(IReadOnlyList<(PreviewSource Source, ManagedBackup Backup)> restores, int objectIndex,
        string recheck)
    {
        if (_backups is null)
            throw new IOException("Managed backup storage is unavailable, so nothing can be restored.");
        await _exportGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var targets = new List<(string File, PreviewSource Source, string Backup)>();
            foreach (var (source, backup) in restores)
            {
                if (!source.IsModFile || !IsSafeModName(source.ModDirectory) || !IsSafeRelativeResourcePath(source.RelativePath))
                    throw new IOException($"{source.Label} is not a writable mod file.");
                if (!string.Equals(_backups.Describe(source.ModDirectory, source.RelativePath).Id, backup.TargetId, StringComparison.Ordinal))
                    throw new IOException($"The backup {backup.Name} doesn't belong to {source.Label}.");
                var scan = await _framework.RunOnFrameworkThread(() => ResolveModScanOnFramework(source.ModDirectory, source.ModStableId)).ConfigureAwait(false)
                    ?? throw new TextureConflictException($"The mod {source.ModName} is no longer registered in Penumbra.");
                var file = Path.GetFullPath(source.ActualPath);
                var root = scan.CandidateRoots.FirstOrDefault(candidate =>
                    string.Equals(Path.GetFullPath(Path.Combine(candidate, source.RelativePath.Replace('/', Path.DirectorySeparatorChar))), file,
                        StringComparison.OrdinalIgnoreCase));
                if (root is null || !IsSafeModResourceFile(root, file) || !File.Exists(file))
                    throw new TextureConflictException($"{source.Label} moved or is no longer part of its mod. {recheck}.");
                var backupPath = _backups.Resolve(backup.TargetId, backup.Name);
                if ((File.GetAttributes(backupPath) & FileAttributes.ReparsePoint) != 0 || new FileInfo(backupPath).Length == 0)
                    throw new IOException($"The backup {backup.Name} can't be read.");
                targets.Add((file, source, backupPath));
            }

            foreach (var (file, source, backupPath) in targets)
            {
                _backups.Create(file, source.ModDirectory, source.RelativePath);
                var temporary = file + $".instant-edit-{Guid.NewGuid():N}.tmp";
                try
                {
                    File.Copy(backupPath, temporary, false);
                    File.Move(temporary, file, true);
                }
                finally
                {
                    if (File.Exists(temporary))
                        File.Delete(temporary);
                }
            }
        }
        finally
        {
            _exportGate.Release();
        }

        var warnings = new List<string>();
        foreach (var mod in restores.Select(r => r.Source.ModDirectory).Distinct(StringComparer.OrdinalIgnoreCase))
            if (await _framework.RunOnFrameworkThread(() => ReloadModOnFramework(mod)).ConfigureAwait(false) is { } reload)
                warnings.Add(reload.Message);
        if (await RedrawPreviewActorAsync(objectIndex).ConfigureAwait(false) is { } redraw)
            warnings.Add(redraw);
        return warnings;
    }

    /// <summary>
    /// For files of one mod: the options that map each one, and whether it is mapped from more than
    /// one game path. Empty when the mod's metadata can't be read.
    /// </summary>
    internal static IReadOnlyDictionary<string, ModFileUsage> DescribeModFileUsages(string modRoot, IEnumerable<string> relativePaths)
    {
        var usages = new Dictionary<string, ModFileUsage>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var mappings = ReadModMappings(modRoot);
            foreach (var relativePath in relativePaths)
            {
                var label = OptionMappingFor(relativePath, mappings.OptionLabels);
                var options = label == "Unmapped" ? [] : label.Split(" | ", StringSplitOptions.RemoveEmptyEntries);
                var several = ModPathKeys(relativePath).Any(key => mappings.GamePaths.TryGetValue(key, out var gamePath) && gamePath is null);
                usages[relativePath] = new ModFileUsage(options, several);
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or
                                        System.Text.Json.JsonException or FormatException)
        {
            // Only a hint shown before applying; writing checks each file again.
        }
        return usages;
    }

    private Task<string?> RedrawPreviewActorAsync(int objectIndex)
        => _framework.RunOnFrameworkThread(() =>
        {
            try
            {
                if (_objects?.LocalPlayer?.ObjectIndex != objectIndex)
                    _redrawObject.Invoke(objectIndex);
            }
            catch (Exception e)
            {
                _log.Warning(e, "Could not redraw the character after a preview mod change.");
                return "The character could not be redrawn; redraw it in Penumbra.";
            }
            return RedrawPlayerOwnedEntitiesOnFramework();
        });
}
