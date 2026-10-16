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

/// <summary>Compares a loaded material's file name with a game path.</summary>
public static class MaterialPathMatch
{
    public static bool Matches(string handleFileName, string gamePath)
    {
        var loaded = Normalize(handleFileName);
        return loaded.Length > 0 && string.Equals(loaded, Normalize(gamePath), StringComparison.OrdinalIgnoreCase);
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
