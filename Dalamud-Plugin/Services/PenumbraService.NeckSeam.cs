using System.Text.Json.Nodes;
using InstantEdit.Services.NeckSeam;
using Penumbra.Api.Enums;
using Penumbra.Api.IpcSubscribers;

namespace InstantEdit.Services;

/// <summary> A mod the neck seam fix created: where it is and the collection it was enabled in. </summary>
internal sealed record NeckSeamModResult(string ModDirectory, Guid Identifier, Guid CollectionId, string CollectionName, IReadOnlyList<string> Warnings);

public sealed partial class PenumbraService
{
    /// <summary> Neck seam mods win over the face and body mods they patch. </summary>
    private const int NeckSeamModPriority = 1_000_000;

    /// <summary>
    /// Creates a new Penumbra mod holding <paramref name="files"/> (game path → bytes) in its default
    /// option, enables it with a high priority in the actor's collection, and redraws the actor.
    /// The name gets a " (2)", " (3)" … suffix when a mod of that name exists.
    /// </summary>
    internal async Task<NeckSeamModResult> CreateNeckSeamModAsync(string name, string description,
        IReadOnlyList<(string GamePath, byte[] Bytes)> files, int objectIndex, CancellationToken token)
    {
        if (files.Count == 0)
            throw new InvalidOperationException("There are no files to put in the mod.");
        foreach (var (gamePath, bytes) in files)
            if (!IsSafeGameResourcePath(NormalizeGamePath(gamePath), ".mdl", ".mtrl", ".tex") || bytes.Length == 0)
                throw new InvalidDataException($"The mod file {gamePath} is invalid.");
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
                Directory.Exists(Path.Combine(root, candidate)) || File.Exists(Path.Combine(root, candidate)));
            folder = Path.GetFullPath(Path.Combine(root, modName));
            if (!IsPathWithin(folder, root))
                throw new IOException("The new mod's folder is outside Penumbra's mod directory.");

            var staging = Path.Combine(root, $".instant-edit-neck-seam-{Guid.NewGuid():N}.tmp");
            try
            {
                Directory.CreateDirectory(staging);
                var mappings = new JsonObject();
                foreach (var (gamePath, bytes) in files)
                {
                    var normalized = NormalizeGamePath(gamePath);
                    var relative = "Files/" + normalized;
                    WriteBytesAtomic(staging, relative, bytes);
                    mappings[normalized] = relative;
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
                modName, collection.Id, collection.Name, setPriority: true, priority: NeckSeamModPriority, redraw: false)).ConfigureAwait(false);
            if (!configure.Success)
                warnings.Add(configure.Message);
            warnings.AddRange(configure.WarningList);
        }
        if (await RedrawNeckSeamActorAsync(objectIndex).ConfigureAwait(false) is { } redraw)
            warnings.Add(redraw);
        return new NeckSeamModResult(modName, identifier, collection.Id, collection.Name, warnings);
    }

    internal static string UniqueModName(string name, Func<string, bool> taken)
    {
        var baseName = string.Concat(name.Where(c => !char.IsControl(c) && Array.IndexOf(Path.GetInvalidFileNameChars(), c) < 0)).Trim().TrimEnd('.');
        if (baseName.Length > 100)
            baseName = baseName[..100].TrimEnd();
        if (!IsSafeNewModName(baseName))
            baseName = "Neck Seam Fix";
        for (var attempt = 1; attempt < 1000; attempt++)
        {
            var candidate = attempt == 1 ? baseName : $"{baseName} ({attempt})";
            if (IsSafeNewModName(candidate) && !taken(candidate))
                return candidate;
        }
        throw new IOException("Could not find a free mod name.");
    }

    internal Task<string?> NeckSeamModRootAsync() => _framework.RunOnFrameworkThread(GetModDirectory);

    /// <summary>
    /// Deletes a mod this feature created, after checking its identifier still matches. Removed is
    /// true when the mod is gone afterwards or the folder now holds something else (so the preview no
    /// longer exists); false only when Penumbra refused.
    /// </summary>
    internal async Task<(bool Removed, string? Warning)> DeleteNeckSeamModAsync(string modDirectory, Guid identifier, int objectIndex)
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
        return (true, await RedrawNeckSeamActorAsync(objectIndex).ConfigureAwait(false));
    }

    /// <summary>
    /// Writes neck seam fixes over their files in the source mods. Every destination is checked
    /// before anything is written: the mod must still be registered, the file must still sit at
    /// its relative path and must still hash as it did when the seam was measured. Each file is
    /// backed up in the managed backup store, then replaced; the mods are reloaded afterwards.
    /// </summary>
    internal async Task<IReadOnlyList<string>> WriteNeckSeamSourcesAsync(IReadOnlyList<(NeckSeamSource Source, byte[] Bytes)> writes, int objectIndex)
    {
        if (_backups is null)
            throw new IOException("Managed backup storage is unavailable, so the source mods can't be changed safely.");
        await _exportGate.WaitAsync().ConfigureAwait(false);
        var written = new List<string>();
        try
        {
            var targets = new List<(string File, NeckSeamSource Source, byte[] Bytes)>();
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
                    throw new TextureConflictException($"{source.Label} moved or is no longer part of its mod. Measure the seam again.");
                if (!string.Equals(NeckSeamCapture.Hash(await File.ReadAllBytesAsync(file).ConfigureAwait(false)), source.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new TextureConflictException($"{source.Label} changed since the seam was measured. Measure it again.");
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
                written.Add(source.Label);
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
        if (await RedrawNeckSeamActorAsync(objectIndex).ConfigureAwait(false) is { } redraw)
            warnings.Add(redraw);
        return warnings;
    }

    private Task<string?> RedrawNeckSeamActorAsync(int objectIndex)
        => _framework.RunOnFrameworkThread(() =>
        {
            try
            {
                if (_objects?.LocalPlayer?.ObjectIndex != objectIndex)
                    _redrawObject.Invoke(objectIndex);
            }
            catch (Exception e)
            {
                _log.Warning(e, "Could not redraw the character after a neck seam change.");
                return "The character could not be redrawn; redraw it in Penumbra.";
            }
            return RedrawPlayerOwnedEntitiesOnFramework();
        });
}
