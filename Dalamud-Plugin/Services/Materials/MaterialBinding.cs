namespace InstantEdit.Services.Materials;

/// <summary>Which draw object of a character a material sits on.</summary>
public enum DrawObjectKind
{
    Character,
    MainHand,
    OffHand,
}

/// <summary>
/// Where a loaded material sits: the object, its draw object, the model slot and the material slot.
/// Only indices are kept; pointers are found again before every write.
/// </summary>
public readonly record struct MaterialBinding(int ObjectIndex, DrawObjectKind Kind, int ModelSlot, int MaterialSlot);

/// <summary>
/// What each bound material held when it was first bound, by material address and resource handle.
/// A snapshot outlives its binding: after a zone change the game can hand the same material, still
/// holding live edits, to a new draw object, and only the first snapshot has its own values.
/// </summary>
public sealed class MaterialSnapshots<T>
{
    public const int RetiredLimit = 32;

    private readonly Dictionary<nint, (nint Handle, T Snapshot)> bound = [];
    private readonly List<(nint Material, nint Handle, T Snapshot)> retired = [];

    public IReadOnlyCollection<nint> Bound => bound.Keys;

    public IEnumerable<T> Values => bound.Values.Select(entry => entry.Snapshot);

    public int RetiredCount => retired.Count;

    public bool TryGetBound(nint material, out T snapshot)
    {
        var found = bound.TryGetValue(material, out var entry);
        snapshot = entry.Snapshot;
        return found;
    }

    /// <summary>The material's snapshot: the bound one, the retired one for the same material and file, or a new <paramref name="read"/>.</summary>
    public T Take(nint material, nint handle, Func<T> read)
    {
        if (bound.TryGetValue(material, out var entry) && entry.Handle == handle)
            return entry.Snapshot;
        var index = retired.FindIndex(old => old.Material == material && old.Handle == handle);
        T snapshot;
        if (index >= 0)
        {
            snapshot = retired[index].Snapshot;
            retired.RemoveAt(index);
        }
        else
            snapshot = read();
        bound[material] = (handle, snapshot);
        return snapshot;
    }

    /// <summary>Moves the snapshots of materials not in <paramref name="keep"/> aside, newest last, dropping the oldest past the cap.</summary>
    public void Retire(IReadOnlyCollection<nint> keep)
    {
        foreach (var material in bound.Keys.Where(material => !keep.Contains(material)).ToList())
        {
            var (handle, snapshot) = bound[material];
            bound.Remove(material);
            retired.RemoveAll(old => old.Material == material);
            retired.Add((material, handle, snapshot));
        }
        if (retired.Count > RetiredLimit)
            retired.RemoveRange(0, retired.Count - RetiredLimit);
    }

    public void Clear()
    {
        bound.Clear();
        retired.Clear();
    }
}

/// <summary>
/// Compares a loaded material's file name with the paths it may have been loaded under. A game
/// file's handle holds the game path, but a mod's holds its file on disk (and the structural
/// preview's, its file in the preview folder).
/// </summary>
public static class MaterialPathMatch
{
    public static bool Matches(string handleFileName, string gamePath)
    {
        var loaded = Normalize(handleFileName);
        return loaded.Length > 0 && string.Equals(loaded, Normalize(gamePath), StringComparison.OrdinalIgnoreCase);
    }

    public static bool MatchesAny(string handleFileName, IReadOnlyCollection<string> paths)
        => paths.Any(path => Matches(handleFileName, path));

    /// <summary>
    /// The paths a material may be loaded under, without empty ones: the mod file it was opened from
    /// (or, for a game file, its game path) and the preview's file. A mod file leaves the game path out,
    /// so a character wearing the vanilla material while the mod is off isn't edited.
    /// </summary>
    public static IReadOnlyCollection<string> Candidates(string gamePath, string? actualPath, string? previewFile)
    {
        var opened = !string.IsNullOrWhiteSpace(actualPath) && Path.IsPathRooted(actualPath) ? actualPath : gamePath;
        return new[] { opened, previewFile }.Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Without Penumbra's "|collection|" prefix, with forward slashes and no leading slash.</summary>
    public static string Normalize(string path)
    {
        path = path.Trim();
        if (path.StartsWith('|') && path.IndexOf('|', 1) is var end and > 0)
            path = path[(end + 1)..];
        return path.Replace('\\', '/').TrimStart('/');
    }
}
