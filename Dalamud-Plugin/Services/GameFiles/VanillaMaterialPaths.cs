using System.Text.RegularExpressions;

namespace InstantEdit.Services.GameFiles;

/// <summary>
/// Where the game finds a model's materials. A model names each material by file name only.
/// Character-part and skin materials live in the folder their own name encodes, whichever model
/// uses them: <c>mt_c0101h0001_hir_a.mtrl</c> is hair 1's, and hair 103 uses it too. Face and ear
/// folders have no variant folder. Gear and weapon materials live in the model's own set folder,
/// under the <c>material/vNNNN</c> folder its IMC variant picks.
/// </summary>
/// <remarks>
/// Checked against the 2026 game data: every one of the 4,116 material references of the vanilla
/// hair, face, tail, ear and body models exists at the name's folder, while 1,617 of them are
/// missing from the model's own folder (hair, tail and body models share materials).
/// </remarks>
internal static partial class VanillaMaterialPaths
{
    [GeneratedRegex(@"^mt_c(?<race>\d{4})(?<letter>[bfhtz])(?<id>\d{4})_", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CharacterMaterial();

    [GeneratedRegex(@"^mt_w(?<weapon>\d{4})b(?<body>\d{4})_", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WeaponMaterial();

    [GeneratedRegex(@"^chara/weapon/w(?<weapon>\d{4})/obj/body/b(?<body>\d{4})/material/(?<variant>v\d{4})/mt_w\k<weapon>b\k<body>_(?<rest>[^/]+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WeaponMaterialPath();

    /// <summary>
    /// For an off-hand weapon material the game has no file for, the main hand's: a few off-hands
    /// (w1851, w3051 and w3151 in the 2026 data) use the material of the weapon 50 IDs below, named
    /// with that weapon's ID. Null for other paths. One vanilla material stays unresolved:
    /// w3054b0001 names w3103's, which only exists under other weapons.
    /// </summary>
    public static string? MainHandMaterial(string materialPath)
    {
        if (WeaponMaterialPath().Match(PathRules.NormalizeGamePath(materialPath)) is not { Success: true } match)
            return null;
        var weapon = int.Parse(match.Groups["weapon"].Value);
        if (weapon % 100 <= 50)
            return null;
        var main = $"w{weapon - 50:D4}";
        var body = match.Groups["body"].Value;
        return $"chara/weapon/{main}/obj/body/b{body}/material/{match.Groups["variant"].Value}/mt_{main}b{body}_{match.Groups["rest"].Value}";
    }

    /// <summary>
    /// The game path of <paramref name="materialName"/> (as the model stores it, usually
    /// <c>/mt_….mtrl</c>) for the model at <paramref name="modelPath"/>, with gear and weapon
    /// materials in material variant <paramref name="variant"/>. Null for unusable names.
    /// </summary>
    public static string? Resolve(string modelPath, string materialName, int variant = 1)
    {
        var name = PathRules.NormalizeGamePath(materialName);
        var file = name[(name.LastIndexOf('/') + 1)..];
        if (file.Length == 0 || !file.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase) || variant is < 0 or > 9999)
            return null;

        if (CharacterMaterial().Match(file) is { Success: true } character)
        {
            var letter = char.ToLowerInvariant(character.Groups["letter"].Value[0]);
            var folder = letter switch
            {
                'h' => "hair",
                'f' => "face",
                't' => "tail",
                'z' => "zear",
                _ => "body",
            };
            var variantFolder = letter is 'f' or 'z' ? "" : "v0001/";
            return $"chara/human/c{character.Groups["race"].Value}/obj/{folder}/{letter}{character.Groups["id"].Value}/material/{variantFolder}{file}";
        }

        // Off-hands such as w0351 use their main hand's materials (mt_w0301…), in their own variant.
        if (WeaponMaterial().Match(file) is { Success: true } weapon)
            return $"chara/weapon/w{weapon.Groups["weapon"].Value}/obj/body/b{weapon.Groups["body"].Value}/material/v{variant:D4}/{file}";

        var model = PathRules.NormalizeGamePath(modelPath);
        var modelDirectory = model[..Math.Max(0, model.LastIndexOf('/'))];
        var assetDirectory = modelDirectory[..Math.Max(0, modelDirectory.LastIndexOf('/'))];
        if (model.Contains("/face/f", StringComparison.OrdinalIgnoreCase) || model.Contains("/zear/z", StringComparison.OrdinalIgnoreCase))
            return $"{assetDirectory}/material/{file}";
        return $"{assetDirectory}/material/v{variant:D4}/{file}";
    }
}
