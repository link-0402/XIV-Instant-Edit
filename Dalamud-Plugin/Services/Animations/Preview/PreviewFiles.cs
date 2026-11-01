namespace InstantEdit.Services.Animations.Preview;

/// <summary>
/// The game paths each Timeline workspace's preview shows, by collection. Penumbra gives a collection one file per game
/// path, so two actors on one collection can't both preview edits of the same file: the second one is refused and told to
/// use Live, which edits each actor's own playing copy. Framework thread only, like the previews.
/// </summary>
internal sealed class PreviewFiles
{
    private readonly Dictionary<string, (string Actor, Guid Collection, HashSet<string> Paths, Func<bool>? Showing)> shown = [];

    /// <summary>The actor of another workspace that shows one of <paramref name="gamePaths"/> on <paramref name="collection"/>; null when none does.</summary>
    public string? Holder(Guid collection, IEnumerable<string> gamePaths, string asking)
    {
        foreach (var (key, preview) in shown)
            if (key != asking && preview.Collection == collection && preview.Showing?.Invoke() != false && gamePaths.Any(preview.Paths.Contains))
                return preview.Actor;
        return null;
    }

    /// <summary>
    /// What workspace <paramref name="key"/>'s preview shows now, on its actor <paramref name="actorName"/>'s collection.
    /// <paramref name="showing"/> says whether that preview is still up or being built; one that failed holds nothing.
    /// </summary>
    public void Set(string key, string actorName, Guid collection, IEnumerable<string> gamePaths, Func<bool>? showing = null) =>
        shown[key] = (actorName, collection, new HashSet<string>(gamePaths, StringComparer.OrdinalIgnoreCase), showing);

    /// <summary>Workspace <paramref name="key"/>'s preview ended.</summary>
    public void Clear(string key) => shown.Remove(key);

    /// <summary>Why a preview is refused: <paramref name="holder"/>, "You" or a partner's name, previews the file on that collection.</summary>
    public static string Refusal(string holder) => holder == "You"
        ? "You preview this file from the same collection. Use Live."
        : $"{holder} previews this file from the same collection. Use Live.";
}
