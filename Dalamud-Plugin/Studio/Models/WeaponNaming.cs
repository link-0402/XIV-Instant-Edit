namespace InstantEdit.Studio.Models;

/// <summary>
/// One weapon model of an item: the item's row, name and icon, its class category, and the model
/// the main or off hand shows (weapon id, body, variant). An item with both a main-hand and an
/// off-hand model (dual wield, shields drawn with the weapon) is listed once per hand.
/// </summary>
internal sealed record WeaponModel(uint RowId, string Name, uint Icon, string Category, ushort Id, ushort Body, ushort Variant, bool OffHand)
{
    public string IdDisplay => $"w{Id:D4}b{Body:D4}";
}

/// <summary>Game paths of weapon files, e.g. "chara/weapon/w0201/obj/body/b0001/model/w0201b0001.mdl".</summary>
internal static class WeaponNaming
{
    public static string Folder(ushort id, ushort body) => $"chara/weapon/w{id:D4}/obj/body/b{body:D4}";

    public static string ModelGamePath(ushort id, ushort body) => $"{Folder(id, body)}/model/w{id:D4}b{body:D4}.mdl";

    /// <summary>The build always writes into v0001; IMC overrides point every variant at it, as for gear.</summary>
    public static string MaterialFolder(ushort id, ushort body) => $"{Folder(id, body)}/material/v0001";

    public static string MaterialGamePath(ushort id, ushort body, string name)
        => $"{MaterialFolder(id, body)}/{MaterialNaming.SanitizeFileName(name)}.mtrl";

    /// <summary>Where the game keeps a variant's own vanilla material.</summary>
    public static string VanillaMaterialGamePath(ushort id, ushort body, ushort variant, string name)
        => $"{Folder(id, body)}/material/v{variant:D4}/{MaterialNaming.SanitizeFileName(name)}.mtrl";

    /// <summary>
    /// Where to look for a vanilla material: the variant's own folder, then v0001. Which folder a
    /// variant reads is its IMC entry's material id, and most variants share v0001, so the variant's
    /// folder often doesn't exist. A variant of 0 counts as 1.
    /// </summary>
    public static System.Collections.Generic.IReadOnlyList<string> VanillaMaterialCandidates(ushort id, ushort body, ushort variant, string name)
    {
        var own = VanillaMaterialGamePath(id, body, System.Math.Max(variant, (ushort)1), name);
        var shared = VanillaMaterialGamePath(id, body, 1, name);
        return own == shared ? new[] { own } : new[] { own, shared };
    }

    public static string TextureFolder(ushort id, ushort body) => $"{Folder(id, body)}/texture";

    public static string TextureGamePath(ushort id, ushort body, string name)
        => $"{TextureFolder(id, body)}/{MaterialNaming.SanitizeFileName(name)}.tex";

    public static string DefaultMaterialName(ushort id, ushort body, int letter)
        => $"mt_w{id:D4}b{body:D4}_{(char)('a' + System.Math.Max(letter, 1) - 1)}";

    public static string ImcGamePath(ushort id, ushort body) => $"{Folder(id, body)}/b{body:D4}.imc";

    /// <summary>
    /// The weapon id whose IMC file the game reads for <paramref name="id"/>. Off hands of the dual-wield
    /// classes (ids 51-99 within the 03xx, 16xx, 18xx, 26xx, 30xx and 31xx ranges) read their main hand's
    /// (id - 50); Penumbra refuses IMC entries for the off-hand ids themselves (ItemData.AdaptOffhandImc).
    /// </summary>
    public static ushort ImcOwnerId(ushort id)
    {
        var category = id / 100;
        return category is 3 or 16 or 18 or 26 or 30 or 31 && id - category * 100 > 50 ? (ushort)(id - 50) : id;
    }
}
