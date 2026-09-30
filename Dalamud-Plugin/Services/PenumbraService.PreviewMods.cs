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
    /// Writes a preview's files over their files in the source mods. Every destination is checked
    /// before anything is written: the mod must still be registered, the file must still sit at its
    /// relative path and must still hash as it did when it was read. Each file is backed up in the
    /// managed backup store, then replaced; the mods are reloaded afterwards.
    /// </summary>
    /// <param name="recheck">What to do after a conflict, such as "Measure the seam again".</param>
    internal async Task<IReadOnlyList<string>> WritePreviewSourcesAsync(IReadOnlyList<(PreviewSource Source, byte[] Bytes)> writes, int objectIndex,
        string recheck)
    {
        if (_backups is null)
            throw new IOException("Managed backup storage is unavailable, so the source mods can't be changed safely.");
        await _exportGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var targets = new List<(string File, PreviewSource Source, byte[] Bytes)>();
            foreach (var (source, bytes) in writes)
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
                if (!string.Equals(PreviewSource.Hash(await File.ReadAllBytesAsync(file).ConfigureAwait(false)), source.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new TextureConflictException($"{source.Label} changed since it was read. {recheck}.");
                targets.Add((file, source, bytes));
            }

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
        foreach (var mod in writes.Select(w => w.Source.ModDirectory).Distinct(StringComparer.OrdinalIgnoreCase))
            if (await _framework.RunOnFrameworkThread(() => ReloadModOnFramework(mod)).ConfigureAwait(false) is { } reload)
                warnings.Add(reload.Message);
        if (await RedrawPreviewActorAsync(objectIndex).ConfigureAwait(false) is { } redraw)
            warnings.Add(redraw);
        return warnings;
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
