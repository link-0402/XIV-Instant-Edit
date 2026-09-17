using InstantEdit.Models;
using Dalamud.Plugin.Services;

namespace InstantEdit.Services;

/// <summary>
/// Attributes physical resolved paths to registered Penumbra mod directories. This
/// identifies only the directory containing a file; it never infers option, priority,
/// or which collection decision produced the resolved path.
/// </summary>
public sealed class ResourceSourceAttributor
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    private readonly PenumbraService _penumbra;
    private readonly IPluginLog _log;
    private readonly object _lock = new();
    private DateTime _lastRefreshUtc = DateTime.MinValue;
    private ModRootIndex _index = ModRootIndex.Empty;

    public ResourceSourceAttributor(PenumbraService penumbra, IPluginLog log)
    {
        _penumbra = penumbra;
        _log = log;
    }

    public ResourceSource AttributionFor(string? actualPath)
    {
        if (string.IsNullOrWhiteSpace(actualPath))
            return new ResourceSource(ResourceSourceState.SourceUnavailable, "Source unavailable", null, null, null, null);

        if (!Path.IsPathRooted(actualPath))
            return new ResourceSource(ResourceSourceState.GameData, "Game data", null, null, null, actualPath);

        var physicalPath = NormalizePhysicalPath(actualPath);
        if (physicalPath is null)
            return new ResourceSource(ResourceSourceState.SourceUnavailable, "Source unavailable", null, null, null, null);

        var index = GetModRootIndex();

        // Fast path: almost every resolved file lives directly under Penumbra's
        // standard mod root as "<root>/<mod directory>/...", so the immediate child
        // segment is an O(1) dictionary lookup instead of scanning every installed
        // mod's path. With a few thousand mods installed, that scan alone was the
        // dominant cost of every on-screen refresh.
        if (index.StandardRootDirectory is { } standardRoot)
        {
            var candidate = StandardCandidatePath(physicalPath, standardRoot);
            if (candidate is not null && index.ByStandardPath.TryGetValue(candidate, out var directMod))
                return BuildLoadedModSource(directMod, physicalPath);
        }

        // Fallback: mods relocated outside the standard root (or any case the fast
        // path above didn't resolve) still get a correct answer via the full scan.
        foreach (var mod in index.All)
        {
            if (!IsPathWithin(physicalPath, mod.Path))
                continue;
            return BuildLoadedModSource(mod, physicalPath);
        }

        return new ResourceSource(ResourceSourceState.ExternalResolvedFile, "External resolved file", null, null, null, physicalPath);
    }

    private static ResourceSource BuildLoadedModSource(ModRoot mod, string physicalPath)
    {
        var relativePath = Path.GetRelativePath(mod.Path, physicalPath).Replace('\\', '/');
        return new ResourceSource(
            ResourceSourceState.LoadedMod,
            $"Loaded from: {mod.Name}",
            mod.Name,
            mod.Directory,
            mod.Path,
            relativePath,
            mod.StableId);
    }

    /// <summary> The candidate standard-layout mod path for a file under <paramref name="standardRoot"/>, i.e. "&lt;root&gt;/&lt;first segment&gt;". </summary>
    private static string? StandardCandidatePath(string physicalPath, string standardRoot)
    {
        if (!physicalPath.StartsWith(standardRoot, StringComparison.OrdinalIgnoreCase) ||
            physicalPath.Length <= standardRoot.Length + 1)
            return null;

        var afterRoot = physicalPath[(standardRoot.Length + 1)..];
        var separatorIndex = afterRoot.IndexOfAny(['\\', '/']);
        var firstSegment = separatorIndex < 0 ? afterRoot : afterRoot[..separatorIndex];
        return firstSegment.Length == 0 ? null : Path.Combine(standardRoot, firstSegment);
    }

    private ModRootIndex GetModRootIndex()
    {
        lock (_lock)
        {
            if (DateTime.UtcNow - _lastRefreshUtc < RefreshInterval)
                return _index;

            _lastRefreshUtc = DateTime.UtcNow;
            _index = BuildModRootIndex();
            return _index;
        }
    }

    private ModRootIndex BuildModRootIndex()
    {
        try
        {
            var modRoot = NormalizePhysicalPath(_penumbra.GetModDirectory());

            var roots = new List<ModRoot>();
            var byStandardPath = new Dictionary<string, ModRoot>(StringComparer.OrdinalIgnoreCase);
            foreach (var (directory, modName) in _penumbra.GetModList())
            {
                if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(modName))
                    continue;

                // The standard Penumbra layout is just a string concatenation; it does
                // not touch the filesystem or any IPC and stays cheap even when the
                // Penumbra mod folder holds thousands of directories. Use it for every
                // mod before consulting the per-mod registered path so the on-screen and
                // search tabs still populate under that scale.
                string? path = null;
                bool fromRegistered = false;
                bool isStandardPath = false;
                if (modRoot is not null)
                {
                    var standardPath = NormalizePhysicalPath(Path.Combine(modRoot, directory));
                    if (standardPath is not null && IsPathWithin(standardPath, modRoot))
                    {
                        path = standardPath;
                        isStandardPath = true;
                    }
                }

                // A mod stored outside Penumbra's global root is only discoverable via
                // its Penumbra IPC entry. Also retry when the conventional directory
                // is absent: GetModDirectory is only the default root, while Penumbra
                // permits an individual mod to have a manually configured location.
                // This keeps the common path cheap without attributing relocated mods
                // to a non-existent default directory.
                if (path is null || !Directory.Exists(path))
                {
                    var registeredPath = _penumbra.GetRegisteredModPath(directory);
                    path = NormalizePhysicalPath(registeredPath);
                    fromRegistered = registeredPath is not null;
                    isStandardPath = false;
                }

                if (path is not null && (modRoot is null ||
                    fromRegistered || IsPathWithin(path, modRoot)))
                {
                    var root = new ModRoot(path, directory, modName, PenumbraService.ReadModStableIdentifier(path));
                    roots.Add(root);
                    if (isStandardPath)
                        byStandardPath[path] = root;
                }
            }

            return new ModRootIndex(
                roots.OrderByDescending(root => root.Path.Length).ToArray(),
                byStandardPath,
                modRoot);
        }
        catch (Exception e)
        {
            _log.Debug($"Could not build Penumbra mod source map: {e.Message}");
            return ModRootIndex.Empty;
        }
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

    private static bool IsPathWithin(string path, string root)
        => PathRules.IsPathWithin(path, root);

    private sealed record ModRoot(string Path, string Directory, string Name, Guid? StableId);

    /// <summary>
    /// <paramref name="All"/> keeps the longest-path-first order the linear fallback
    /// scan relies on; <paramref name="ByStandardPath"/> indexes only the mods that sit
    /// directly under <paramref name="StandardRootDirectory"/> (the common case) for
    /// O(1) lookups instead of scanning every installed mod per resource.
    /// </summary>
    private sealed record ModRootIndex(
        IReadOnlyList<ModRoot> All,
        IReadOnlyDictionary<string, ModRoot> ByStandardPath,
        string? StandardRootDirectory)
    {
        public static readonly ModRootIndex Empty = new(
            Array.Empty<ModRoot>(), new Dictionary<string, ModRoot>(StringComparer.OrdinalIgnoreCase), null);
    }
}

public sealed record ResourceSource(
    ResourceSourceState State,
    string Label,
    string? ModName,
    string? ModDirectory,
    string? ModRootPath,
    string? RelativePath,
    Guid? ModStableId = null);
