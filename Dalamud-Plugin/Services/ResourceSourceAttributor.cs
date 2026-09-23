using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using InstantEdit.Models;
using Dalamud.Plugin.Services;

namespace InstantEdit.Services;

/// <summary>
/// Attributes physical resolved paths to registered Penumbra mod directories. This
/// identifies only the directory containing a file; it never infers option, priority,
/// or which collection decision produced the resolved path.
/// </summary>
/// <remarks>
/// Penumbra loads every mod from a direct child of its mod directory, so the index is
/// derived from the mod list alone and never touches the disk. Since Penumbra 1.7 a
/// mod's meta.json also carries all of its option data (on the order of 100 MiB across
/// a few thousand installed mods), so stable identifiers are read lazily, only for mods
/// a resolved path actually belongs to, and cached until that meta.json changes.
/// </remarks>
public sealed class ResourceSourceAttributor
{
    // A rebuild is two IPC calls and string work only, so a short interval keeps newly
    // installed or removed mods current at no measurable cost.
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);

    private readonly Func<ModListSnapshot?> _readModList;
    private readonly ModStableIdCache _stableIds;
    private readonly Action<string>? _logDebug;
    private readonly object _lock = new();
    private DateTime _lastRefreshUtc = DateTime.MinValue;
    private ModRootIndex _index = ModRootIndex.Empty;
    private long _generation;

    public ResourceSourceAttributor(PenumbraService penumbra, IPluginLog log)
        : this(() => ReadModList(penumbra), PenumbraService.ReadModStableIdentifier, message => log.Debug(message))
    {
    }

    // Logging stays a delegate so regression tests can use this type without Dalamud loaded.
    private ResourceSourceAttributor(Func<ModListSnapshot?> readModList, Func<string, Guid?> readStableId, Action<string>? logDebug)
    {
        _readModList = readModList;
        _stableIds = new ModStableIdCache(readStableId);
        _logDebug = logDebug;
    }

    /// <summary> Regression seam: supplies the Penumbra mod list and the stable-identifier reader. </summary>
    internal static ResourceSourceAttributor CreateForRegression(
        Func<ModListSnapshot?> readModList,
        Func<string, Guid?> readStableId)
        => new(readModList, readStableId, null);

    public ResourceSource AttributionFor(string? actualPath)
    {
        if (string.IsNullOrWhiteSpace(actualPath))
            return new ResourceSource(ResourceSourceState.SourceUnavailable, "Source unavailable", null, null, null, null);

        if (!Path.IsPathRooted(actualPath))
            return new ResourceSource(ResourceSourceState.GameData, "Game data", null, null, null, actualPath);

        var physicalPath = NormalizePhysicalPath(actualPath);
        if (physicalPath is null)
            return new ResourceSource(ResourceSourceState.SourceUnavailable, "Source unavailable", null, null, null, null);

        var (index, generation) = GetModRootIndex();
        if (index.TryFind(physicalPath, out var mod))
            return BuildLoadedModSource(mod, physicalPath, _stableIds.Get(mod.Path, generation));

        return new ResourceSource(ResourceSourceState.ExternalResolvedFile, "External resolved file", null, null, null, physicalPath);
    }

    /// <summary> Makes the next attribution rebuild the mod index and revalidate identifiers. </summary>
    internal void Invalidate()
    {
        lock (_lock)
            _lastRefreshUtc = DateTime.MinValue;
    }

    private static ResourceSource BuildLoadedModSource(ModRoot mod, string physicalPath, Guid? stableId)
    {
        var relativePath = Path.GetRelativePath(mod.Path, physicalPath).Replace('\\', '/');
        return new ResourceSource(
            ResourceSourceState.LoadedMod,
            $"Loaded from: {mod.Name}",
            mod.Name,
            mod.Directory,
            mod.Path,
            relativePath,
            stableId);
    }

    private (ModRootIndex Index, long Generation) GetModRootIndex()
    {
        lock (_lock)
        {
            if (DateTime.UtcNow - _lastRefreshUtc < RefreshInterval)
                return (_index, _generation);

            // A failed read keeps the previous index and is retried after the interval,
            // rather than attributing every modded file as external until then.
            _lastRefreshUtc = DateTime.UtcNow;
            try
            {
                if (_readModList() is { } snapshot)
                {
                    _index = ModRootIndex.Build(snapshot);
                    _generation++;
                }
            }
            catch (Exception e)
            {
                _logDebug?.Invoke($"Could not build Penumbra mod source map: {e.Message}");
            }

            return (_index, _generation);
        }
    }

    private static ModListSnapshot? ReadModList(PenumbraService penumbra)
    {
        var modDirectory = penumbra.GetModDirectory();
        if (modDirectory is null)
            return null;
        return penumbra.TryGetModList(out var mods) ? new ModListSnapshot(modDirectory, mods) : null;
    }

    private static string? NormalizePhysicalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
                ? fullPath
                : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private sealed record ModRoot(string Path, string Directory, string Name);

    /// <summary> Mods keyed by their folder name directly below Penumbra's mod directory. </summary>
    private sealed class ModRootIndex
    {
        public static readonly ModRootIndex Empty = new(null, new Dictionary<string, ModRoot>(StringComparer.OrdinalIgnoreCase));

        private readonly string? _modDirectory;
        private readonly IReadOnlyDictionary<string, ModRoot> _byFolder;

        private ModRootIndex(string? modDirectory, IReadOnlyDictionary<string, ModRoot> byFolder)
        {
            _modDirectory = modDirectory;
            _byFolder = byFolder;
        }

        public static ModRootIndex Build(ModListSnapshot snapshot)
        {
            var modDirectory = NormalizePhysicalPath(snapshot.ModDirectory);
            if (modDirectory is null)
                return Empty;

            var byFolder = new Dictionary<string, ModRoot>(snapshot.Mods.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var (directory, modName) in snapshot.Mods)
            {
                if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(modName))
                    continue;

                // A directory key that is not a single folder name below the mod directory
                // (for example one containing "..") cannot be a Penumbra mod and is ignored.
                var path = NormalizePhysicalPath(System.IO.Path.Combine(modDirectory, directory));
                if (path is null ||
                    !string.Equals(System.IO.Path.GetDirectoryName(path), modDirectory, StringComparison.OrdinalIgnoreCase))
                    continue;

                byFolder[System.IO.Path.GetFileName(path)] = new ModRoot(path, directory, modName);
            }

            return new ModRootIndex(modDirectory, byFolder);
        }

        public bool TryFind(string physicalPath, [NotNullWhen(true)] out ModRoot? mod)
        {
            mod = null;
            return _modDirectory is not null &&
                   FolderBelow(physicalPath, _modDirectory) is { } folder &&
                   _byFolder.TryGetValue(folder, out mod);
        }

        private static string? FolderBelow(string physicalPath, string modDirectory)
        {
            var separated = System.IO.Path.EndsInDirectorySeparator(modDirectory);
            var prefixLength = separated ? modDirectory.Length : modDirectory.Length + 1;
            if (physicalPath.Length <= prefixLength ||
                !physicalPath.StartsWith(modDirectory, StringComparison.OrdinalIgnoreCase) ||
                (!separated && physicalPath[modDirectory.Length] is not ('\\' or '/')))
                return null;

            var rest = physicalPath.AsSpan(prefixLength);
            var separator = rest.IndexOfAny('\\', '/');
            var folder = separator < 0 ? rest : rest[..separator];
            return folder.IsEmpty ? null : folder.ToString();
        }
    }

    /// <summary>
    /// Stable identifiers per mod root. An identifier is re-read only when its meta.json
    /// changes, and that is checked at most once per index generation.
    /// </summary>
    private sealed class ModStableIdCache(Func<string, Guid?> readStableId)
    {
        private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

        public Guid? Get(string modRoot, long generation)
        {
            if (_entries.TryGetValue(modRoot, out var cached) && cached.Generation == generation)
                return cached.StableId;

            var stamp = ReadStamp(modRoot);
            if (cached is not null && cached.Stamp == stamp)
            {
                _entries[modRoot] = cached with { Generation = generation };
                return cached.StableId;
            }

            var stableId = readStableId(modRoot);
            _entries[modRoot] = new Entry(stableId, stamp, generation);
            return stableId;
        }

        private static MetaStamp? ReadStamp(string modRoot)
        {
            try
            {
                var meta = new FileInfo(System.IO.Path.Combine(modRoot, "meta.json"));
                return meta.Exists ? new MetaStamp(meta.LastWriteTimeUtc, meta.Length) : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return null;
            }
        }

        private readonly record struct MetaStamp(DateTime LastWriteUtc, long Length);

        private sealed record Entry(Guid? StableId, MetaStamp? Stamp, long Generation);
    }
}

/// <summary> Penumbra's mod directory and its mod list, keyed by mod directory name. </summary>
internal sealed record ModListSnapshot(string? ModDirectory, IReadOnlyDictionary<string, string> Mods);

public sealed record ResourceSource(
    ResourceSourceState State,
    string Label,
    string? ModName,
    string? ModDirectory,
    string? ModRootPath,
    string? RelativePath,
    Guid? ModStableId = null);
