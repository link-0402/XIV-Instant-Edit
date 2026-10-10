using System;
using System.Text.RegularExpressions;
using InstantEdit.Studio.Services.Project;
using InstantEdit.Studio.Services.Resources;

namespace InstantEdit.Studio.Models;

public enum MaterialRequestKind
{
    /// <summary>The game requests <see cref="MaterialRequest.Path"/>.</summary>
    Path,

    /// <summary>A skin material: the game puts in the wearer's skin race, so the path depends on who wears the model.</summary>
    Skin,

    /// <summary>The name can't be turned into a game path.</summary>
    Invalid,
}

/// <summary>What the game requests for one material name of a model.</summary>
public sealed record MaterialRequest(MaterialRequestKind Kind, string? Path, ushort SkinBody = 0, string? SkinSuffix = null,
    string? Reason = null, bool Approximate = false);

/// <summary>
/// Material and texture file naming following the TexTools convention (see
/// xivModdingFramework's <c>Mtrl.GetMtrlNameByRootRaceSlotSuffix</c>):
/// <c>mt_{primaryType}{id}_{slot}</c>, e.g. <c>mt_c0101e0164_top</c> for a
/// body-slot item with model 0164.  Gear materials are race-shared, so the
/// canonical base race code <c>c0101</c> is always used.
/// </summary>
public static class MaterialNaming
{
    /// <summary>
    /// The race code a gear material name carries for a gender. The game keeps one material per
    /// gender — male under Midlander M (c0101), female under Midlander F (c0201) — and every other
    /// race looks its material up under its gender's base unless its own Eqdp material bit is set.
    /// </summary>
    public static string BaseRaceCode(PlayerGender gender) => RaceInfo.BaseFor(gender).RaceCode;

    /// <summary>
    /// Default material name for an item, e.g. <c>mt_c0101e0164_top_a</c>.
    /// <paramref name="variant"/> is the 1-based material slot, mapped to the
    /// game's letter-suffix convention (1 → <c>_a</c>, 2 → <c>_b</c>, …) — real
    /// game materials always carry this suffix, even the first one.
    /// </summary>
    public static string DefaultName(EquipSlot slot, ushort modelId, int variant = 1, PlayerGender gender = PlayerGender.Male)
    {
        var itemPrefix = SlotInfo.ItemPrefix(slot);   // "e" for equipment, "a" for accessories
        var slotKey    = SlotInfo.KeyMap[slot];       // e.g. "top"
        var suffix     = (char)('a' + Math.Max(variant, 1) - 1);
        return $"mt_c{BaseRaceCode(gender)}{itemPrefix}{modelId:D4}_{slotKey}_{suffix}";
    }

    /// <summary>
    /// Swaps the race code in a material name ("mt_c0101e0025_top_a" → "mt_c0201e0025_top_a"), which
    /// is how one material set-up is written for each gender, or for each race of a feature port.
    /// A name that carries no race code is left as it is.
    /// </summary>
    public static string WithRaceCode(string name, string raceCode)
    {
        for (int i = 0; i + 5 <= name.Length; i++)
        {
            if (name[i] is not ('c' or 'C'))
                continue;

            bool digits = true;
            for (int d = 1; d <= 4 && digits; d++)
                digits = char.IsAsciiDigit(name[i + d]);

            if (digits)
                return string.Concat(name.AsSpan(0, i + 1), raceCode, name.AsSpan(i + 5));
        }
        return name;
    }

    /// <summary>Default postfix for a texture role, before the user edits it.</summary>
    public static string DefaultPostfix(TextureType type)
        => type switch
        {
            TextureType.Diffuse    => "base",
            TextureType.Normal     => "normal",
            TextureType.Specular   => "specular",
            TextureType.Mask       => "mask",
            TextureType.Index      => "id",
            TextureType.Reflection => "reflection",
            _                      => "other",
        };

    /// <summary>
    /// Combines a material name with a texture's user-editable postfix, e.g.
    /// material "mt_c0201e0025_top_b" + postfix "normal" → "mt_c0201e0025_top_b_normal".
    /// </summary>
    public static string ComposeTextureName(string materialName, string postfix)
        => $"{SanitizeFileName(materialName)}_{SanitizeFileName(postfix)}";

    /// <summary>
    /// Item folder, e.g. "chara/equipment/e0164" or "chara/accessory/a0101".
    /// </summary>
    public static string ItemFolder(EquipSlot slot, ushort modelId)
        => $"chara/{(SlotInfo.IsAccessory(slot) ? "accessory" : "equipment")}/{SlotInfo.ItemPrefix(slot)}{modelId:D4}";

    /// <summary>
    /// Material folder, e.g. "chara/equipment/e0164/material/v0001". Materials live here —
    /// textures do not. Always the v0001 slot: this tool's created mods always write there,
    /// and pair it with an Imc override forcing every dye/recolor variant back to it (see
    /// <see cref="GameDataService.GetImcOverridesForcingV1"/>) rather than exposing a variant
    /// choice that would just be wrong for anyone not on that exact variant.
    /// </summary>
    public static string MaterialFolder(EquipSlot slot, ushort modelId)
        => $"{ItemFolder(slot, modelId)}/material/v0001";

    /// <summary>
    /// Full game path of a material, e.g. "chara/equipment/e0164/material/v0001/mt_c0101e0164_top_a.mtrl".
    /// </summary>
    public static string MaterialGamePath(EquipSlot slot, ushort modelId, string materialName)
        => $"{MaterialFolder(slot, modelId)}/{SanitizeFileName(materialName)}.mtrl";

    /// <summary>
    /// Texture folder, e.g. "chara/equipment/e0164/texture". Flat per item — not nested under a
    /// material variant, since multiple material variants commonly share the same textures.
    /// </summary>
    public static string TextureFolder(EquipSlot slot, ushort modelId)
        => $"{ItemFolder(slot, modelId)}/texture";

    /// <summary>
    /// Full game path of a texture, e.g. "chara/equipment/e0164/texture/mt_c0101e0164_top_a_d.tex".
    /// </summary>
    public static string TextureGamePath(EquipSlot slot, ushort modelId, string textureName)
        => $"{TextureFolder(slot, modelId)}/{SanitizeFileName(textureName)}.tex";

    /// <summary>
    /// Full game path of an item's IMC file, e.g. "chara/equipment/e0164/e0164.imc".
    /// </summary>
    public static string ImcGamePath(EquipSlot slot, ushort modelId)
        => $"{ItemFolder(slot, modelId)}/{SlotInfo.ItemPrefix(slot)}{modelId:D4}.imc";

    private static readonly Regex SkinName = new(@"^mt_c\d{4}b(?<body>\d{4})_(?<suffix>.+)\.mtrl$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex HairName = new(@"c(?<race>\d{4})h(?<id>\d{4})", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TailId = new(@"t(?<id>\d{4})", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The game path the game requests for a material name stored in a model, with the IMC material
    /// set it picked: gear, weapons, monsters and demihumans look in the model's material/vNNNN folder
    /// (as Penumbra's resource tree resolves them), hair in the shared roots the game hard-codes,
    /// faces and Viera ears in a folder without variants. Skin materials on gear depend on the wearer.
    /// </summary>
    public static MaterialRequest Resolve(ModelPathInfo model, string materialName, byte materialSet)
    {
        var name = materialName.Replace('\\', '/').Trim().TrimStart('/');
        if (name.Length == 0 || !name.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase))
            return new MaterialRequest(MaterialRequestKind.Invalid, null, Reason: $"'{materialName}' is not a material name.");
        if (name.Contains('/'))
            return ResourceNaming.HasValidRoot(name)
                ? new MaterialRequest(MaterialRequestKind.Path, PathNormalizer.GamePath(name))
                : new MaterialRequest(MaterialRequestKind.Invalid, null, Reason: $"'{materialName}' is not a valid game path.");

        var skin = SkinName.Match(name);
        if (skin.Success && model.Kind is GameModelKind.Equipment or GameModelKind.Body)
        {
            var body = ushort.Parse(skin.Groups["body"].Value);
            var suffix = skin.Groups["suffix"].Value.ToLowerInvariant();
            // A body model is the wearer, so its skin materials resolve for the model's own skin race;
            // gear hands skin materials to whoever wears it.
            return model.Kind == GameModelKind.Body
                ? new MaterialRequest(MaterialRequestKind.Path, SkinPath(model.Race, body, suffix))
                : new MaterialRequest(MaterialRequestKind.Skin, null, body, suffix);
        }

        name = name.ToLowerInvariant();
        switch (model.Kind)
        {
            case GameModelKind.Equipment:
            case GameModelKind.Accessory:
            case GameModelKind.Demihuman:
            case GameModelKind.Monster:
                return new MaterialRequest(MaterialRequestKind.Path, $"{model.Directory}/material/v{materialSet:D4}/{name}");
            case GameModelKind.Weapon:
                // Gunbreaker and machinist style 20xx sets share one "c" material; off-hand mirroring isn't reproduced.
                if (model.PrimaryId / 100 == 20 && name.Length > 14 && name[14] == 'c')
                    return new MaterialRequest(MaterialRequestKind.Path, "chara/weapon/w2001/obj/body/b0001/material/v0001/mt_w2001b0001_c.mtrl");
                return new MaterialRequest(MaterialRequestKind.Path, $"{model.Directory}/material/v{materialSet:D4}/{name}", Approximate: true);
            case GameModelKind.Hair:
            {
                var root = HairMaterialRace(model.Race, model.PrimaryId);
                var rebased = HairName.Replace(name, match =>
                    ushort.Parse(match.Groups["race"].Value) == model.Race && ushort.Parse(match.Groups["id"].Value) == model.PrimaryId
                        ? $"c{root:D4}h{model.PrimaryId:D4}"
                        : match.Value);
                return new MaterialRequest(MaterialRequestKind.Path, $"chara/human/c{root:D4}/obj/hair/h{model.PrimaryId:D4}/material/v0001/{rebased}");
            }
            case GameModelKind.Face:
            case GameModelKind.Ear:
                return new MaterialRequest(MaterialRequestKind.Path, $"{model.Directory}/material/{name}");
            case GameModelKind.Tail:
            {
                // A tail model may name another tail's materials; Hrothgar tails share t0001 with several variants.
                var directory = model.Directory;
                var tail = TailId.Match(name[(name.IndexOf('_') + 1)..]);
                if (tail.Success && ushort.Parse(tail.Groups["id"].Value) != model.PrimaryId)
                    directory = directory[..^4] + tail.Groups["id"].Value;
                return new MaterialRequest(MaterialRequestKind.Path, $"{directory}/material/v0001/{name}", Approximate: model.Race is 1501 or 1601);
            }
            default:
                return new MaterialRequest(MaterialRequestKind.Path, $"{model.Directory}/material/v0001/{name}");
        }
    }

    /// <summary>The body material a skin material resolves to for a wearer of race <paramref name="wearerRace"/>.</summary>
    public static string SkinPath(ushort wearerRace, ushort body, string suffix)
    {
        var race = RaceInfo.SkinRace(wearerRace);
        return $"chara/human/c{race:D4}/obj/body/b{body:D4}/material/v0001/mt_c{race:D4}b{body:D4}_{suffix}.mtrl";
    }

    /// <summary>
    /// The race whose folder a hairstyle's materials come from: styles 101-115 use the Midlander
    /// ones except for Miqo'te, 116-200 always do, Hrothgar never share.
    /// </summary>
    public static ushort HairMaterialRace(ushort race, ushort hairId)
    {
        if (race is 1501 or 1601 || hairId < 101 || hairId >= 201) return race;
        if (hairId < 116 && race is 701 or 801) return race;
        return RaceInfo.IsFemaleCode(race) ? (ushort)201 : (ushort)101;
    }

    /// <summary>The texture path the game requests for a material's texture entry: DX11 ones carry the -- prefix.</summary>
    public static string TexturePath(string path, bool dx11)
    {
        path = PathNormalizer.GamePath(path);
        if (!dx11) return path;
        var slash = path.LastIndexOf('/');
        return slash < 0 ? "--" + path : $"{path[..(slash + 1)]}--{path[(slash + 1)..]}";
    }

    /// <summary>
    /// Lower-cases a file name, strips an extension if present, and removes
    /// characters that are invalid in game paths.
    /// </summary>
    public static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "texture";
        name = name.Trim().ToLowerInvariant();

        var ext = System.IO.Path.GetExtension(name);
        if (!string.IsNullOrEmpty(ext)) name = name[..^ext.Length];

        var chars = new char[name.Length];
        int len = 0;
        foreach (var c in name)
        {
            bool ok = c is >= 'a' and <= 'z' or >= '0' and <= '9' || c is '_' or '-';
            if (ok) chars[len++] = c;
        }
        return len == 0 ? "texture" : new string(chars, 0, len);
    }
}
