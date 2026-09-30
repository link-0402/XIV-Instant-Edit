using Penumbra.Api.Enums;
using Penumbra.Api.IpcSubscribers;

namespace InstantEdit.Services;

public sealed partial class PenumbraService
{
    /// <summary>
    /// The files a collection loads for game paths: a mod's file as a full path, or the game path
    /// (or the one a file swap names) when no mod replaces it; null where Penumbra couldn't resolve.
    /// </summary>
    internal Task<string?[]> ResolveCollectionPathsAsync(Guid collection, IReadOnlyList<string> gamePaths) => _framework.RunOnFrameworkThread(() =>
    {
        var resolve = new ResolvePath(_pi);
        return gamePaths.Select(gamePath =>
        {
            try
            {
                return resolve.Invoke(collection, gamePath, out var resolved) == PenumbraApiEc.Success && !string.IsNullOrWhiteSpace(resolved)
                    ? resolved
                    : null;
            }
            catch (Exception e)
            {
                _log.Debug(e, "Could not resolve {GamePath} for collection {Collection}.", gamePath, collection);
                return null;
            }
        }).ToArray();
    });
}
