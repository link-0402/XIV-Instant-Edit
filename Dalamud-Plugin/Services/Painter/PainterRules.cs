namespace InstantEdit.Services.Painter;

/// <summary> Which planned textures a Painter project can send back, and the names it gives files and texture sets. </summary>
internal static class PainterRules
{
    public static readonly HashSet<string> OfferedUsages = new(StringComparer.Ordinal) { "diffuse", "normal", "mask", "specular", "index" };

    /// <summary> Why a planned texture can't be sent back; empty when it can. </summary>
    public static string EditReason(TexturePlanTexture texture)
    {
        if (!OfferedUsages.Contains(texture.Usage))
            return "Not a per-model texture.";
        if (texture.UvSet != 0)
            return "Uses the second UV set, which the Painter mesh doesn't carry.";
        var path = texture.GamePath.TrimStart('-');
        if (path.StartsWith("common/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("chara/common/", StringComparison.OrdinalIgnoreCase))
            return "A shared game texture.";
        if (texture.Locator is null)
            return "Outside Penumbra's mod folder.";
        try { _ = TextureFiles.OutputType(texture.Format); }
        catch (NotSupportedException) { return "Its TEX format can't be saved back."; }
        if (texture.Width is < 1 or > TextureFiles.MaxDimension || texture.Height is < 1 or > TextureFiles.MaxDimension)
            return "Its size is not supported.";
        if (TextureFiles.IsBlockCompressed(texture.Format) && (texture.Width % 4 != 0 || texture.Height % 4 != 0))
            return "Its size isn't a multiple of 4, which its compression needs.";
        return "";
    }

    /// <summary> A file- and Painter-safe name from a file stem, unique within <paramref name="used"/>. </summary>
    public static string UniqueName(string stem, ISet<string> used, string fallback)
    {
        var cleaned = new string(stem.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray()).Trim('_');
        if (cleaned.Length == 0)
            cleaned = fallback;
        if (cleaned.Length > 56)
            cleaned = cleaned[..56];
        var unique = cleaned;
        for (var i = 2; !used.Add(unique); i++)
            unique = $"{cleaned}_{i}";
        return unique;
    }
}
