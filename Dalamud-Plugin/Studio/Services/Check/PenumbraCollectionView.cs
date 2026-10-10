using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using InstantEdit.Studio.Services.Project;

namespace InstantEdit.Studio.Services.Check;

/// <summary>The player's Penumbra collection for a check, captured once per check (Mod Optimizer's PenumbraLiveState).</summary>
internal sealed class PenumbraCollectionView : ICollectionView
{
    private readonly PenumbraIpcService _ipc;
    private readonly string? _modRoot;

    private PenumbraCollectionView(PenumbraIpcService ipc, string? modRoot, CollectionContext context)
    {
        _ipc     = ipc;
        _modRoot = modRoot;
        Context  = context;
    }

    public CollectionContext Context { get; }

    /// <summary>Null when Penumbra is not there or no character is loaded; the check then runs without the collection.</summary>
    public static async Task<ICollectionView?> CaptureAsync(PenumbraIpcService ipc, string modFolderName)
    {
        if (!ipc.IsAvailable)
            return null;
        // The object table and Penumbra's collection lookup belong to the framework thread.
        return await StudioServices.Framework.RunOnFrameworkThread(() =>
        {
            if (ipc.PlayerCollection() is not { } collection)
                return (ICollectionView?)null;
            var state = ipc.ModState(collection.Id, modFolderName);
            return new PenumbraCollectionView(ipc, ipc.GetModDirectory(), new CollectionContext(collection.Name, state?.Enabled ?? false, state?.Priority,
                state is null ? "The mod has no settings in this collection." : null));
        }).ConfigureAwait(false);
    }

    public IReadOnlyDictionary<string, string?> Resolve(IReadOnlyList<string> gamePaths)
    {
        var paths = new string[gamePaths.Count];
        for (var i = 0; i < paths.Length; ++i)
            paths[i] = gamePaths[i];
        var resolved = _ipc.ResolvePlayerPaths(paths).GetAwaiter().GetResult() ?? throw new InvalidOperationException("Penumbra did not resolve the paths.");
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var i = 0; i < paths.Length; ++i)
        {
            var value = i < resolved.Length ? resolved[i] : null;
            result[paths[i]] = string.IsNullOrEmpty(value) || PathNormalizer.SameGamePath(value, paths[i]) ? null : value;
        }
        return result;
    }

    public string? ModOf(string resolvedPath)
    {
        if (_modRoot is null || !Path.IsPathRooted(resolvedPath))
            return null;
        var root = Path.GetFullPath(_modRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(resolvedPath);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return null;
        var relative = full[root.Length..];
        var separator = relative.IndexOf(Path.DirectorySeparatorChar);
        return separator > 0 ? relative[..separator] : relative;
    }
}
