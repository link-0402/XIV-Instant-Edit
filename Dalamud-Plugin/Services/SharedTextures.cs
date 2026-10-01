namespace InstantEdit.Services;

/// <summary>
/// Game textures that many materials point at without any mod shipping them, such as the blank
/// white.tex. Editing one changes every material that uses it, so none is opened for editing or written.
/// </summary>
internal static class SharedTextures
{
    /// <summary> The game's solid fill textures (32×32 or 4×4), found in the game data and referenced by mod materials. </summary>
    private static readonly HashSet<string> Blank = new(StringComparer.Ordinal)
    {
        "chara/common/texture/white.tex",
        "chara/common/texture/black.tex",
        "chara/common/texture/red.tex",
        "chara/common/texture/green.tex",
        "chara/common/texture/blue.tex",
        "chara/common/texture/transparent.tex",
        "chara/common/texture/null_normal.tex",
        "chara/common/texture/id_16.tex",
        "chara/common/texture/skin_mask.tex",
        "common/graphics/texture/dummy.tex",
        "bgcommon/texture/dummy_d.tex",
        "bgcommon/texture/dummy_n.tex",
        "bgcommon/texture/dummy_s.tex",
    };

    /// <summary> Folders of game textures shared between unrelated models. Mods also ship their own files under chara/common. </summary>
    private static readonly string[] SharedFolders = ["chara/common/", "common/", "bgcommon/", "vfx/common/"];

    /// <summary> Materials may write game paths with a leading "--"; the game reads the same file. </summary>
    private static string Normalize(string gamePath) => gamePath.Trim().Replace('\\', '/').TrimStart('-', '/').ToLowerInvariant();

    public static bool IsBlank(string gamePath) => Blank.Contains(Normalize(gamePath));

    public static bool InSharedFolder(string gamePath)
    {
        var path = Normalize(gamePath);
        return SharedFolders.Any(folder => path.StartsWith(folder, StringComparison.Ordinal));
    }

    /// <summary> Why the texture can't be edited; empty when it can. </summary>
    /// <param name="vanilla">
    /// Whether the game's own file shows: editing it makes a mod that replaces the game path for
    /// everything drawn in the collection. A mod's own file in a shared folder (an eye mod) stays editable.
    /// </param>
    public static string EditBlock(string gamePath, bool vanilla)
    {
        if (IsBlank(gamePath))
            return $"{Path.GetFileName(Normalize(gamePath))} is a blank texture the game shares between many materials; editing it would change all of them.";
        if (vanilla && InSharedFolder(gamePath))
            return "A game texture shared between many materials; a mod replacing it would change all of them.";
        return "";
    }
}
