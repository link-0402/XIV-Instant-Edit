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

    /// <summary>The paths a material may be loaded under: its game path, the file it was opened from and the preview's file, without empty ones.</summary>
    public static IReadOnlyCollection<string> Candidates(string gamePath, string? actualPath, string? previewFile)
        => new[] { gamePath, actualPath, previewFile }.Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>Without Penumbra's "|collection|" prefix, with forward slashes and no leading slash.</summary>
    public static string Normalize(string path)
    {
        path = path.Trim();
        if (path.StartsWith('|') && path.IndexOf('|', 1) is var end and > 0)
            path = path[(end + 1)..];
        return path.Replace('\\', '/').TrimStart('/');
    }
}
