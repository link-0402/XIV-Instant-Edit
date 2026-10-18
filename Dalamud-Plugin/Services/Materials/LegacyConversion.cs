using System.Numerics;
using InstantEdit.Services.Previews;

namespace InstantEdit.Services.Materials;

/// <summary>What a characterlegacy.shpk material's mask holds, from its keys (read from the game's shaders).</summary>
public enum LegacyMaskKind
{
    /// <summary>Compatibility with the default shine type: the mask is a shine colour map (rgb, squared).</summary>
    ShineColour,

    /// <summary>Compatibility with the Mask shine type: red is the shine (squared), blue a velvet amount.</summary>
    ShineMask,

    /// <summary>Multi-material: red scales the shine strength, green the shine colour, blue the base colour (all squared).</summary>
    Multi,
}

/// <summary>
/// One row as a legacy shader reads it, whatever the table size. A 16-row table keeps the shine
/// strength in half 3 and the gloss in half 7; characterlegacy.shpk reads a 32-row table with
/// gloss in half 3 and shine strength in half 7, and ignores halves 11 to 23.
/// </summary>
public readonly record struct LegacyLook(
    Vector3 Diffuse, float ShineStrength, Vector3 Specular, float Gloss, Vector3 Emissive,
    float TileIndexValue, float TileAlpha, float TileUU, float TileUV, float TileVU, float TileVV)
{
    public static LegacyLook From(LegacyColorRow row)
        => new(row.Diffuse, row.SpecularMask, row.Specular, row.Gloss, row.Emissive, row.TileIndexValue, 1f, row.TileUU, row.TileUV, row.TileVU, row.TileVV);

    public static LegacyLook FromLegacyShader(DtColorRow row)
        => new(row.Diffuse, row.Scalar7, row.Specular, row.Scalar3, row.Emissive, row.TileIndexValue, row.TileAlpha, row.TileUU, row.TileUV, row.TileVU, row.TileVV);
}

/// <summary>
/// Moves a material from characterlegacy.shpk to character.shpk, keeping its look. The rules come
/// from the game's shaders (G-buffer and composite passes, disassembled 2026-10-18):
/// <list type="bullet">
/// <item>Both packages pick rows the same way (index texture, 32-row table texture).</item>
/// <item>Roughness: legacy writes 2^(-gloss/15) into the G-buffer, character.shpk the row's roughness
/// (mask green 0.5 leaves it as it is). So roughness = 2^(-gloss/15) gives the same G-buffer value.</item>
/// <item>Shine: legacy writes strength (times mask red² on the Mask type and multi-material) to the
/// G-buffer and tints the shine by the shine colour (times its mask's part); character.shpk writes
/// luma(colour × mask red²) and tints by colour × mask red². Scaling the colour by
/// sqrt(strength / luma(colour)) and the mask red as <see cref="MaskTexel"/> does gives the same product.</item>
/// <item>Half 11 on character.shpk scales the base colour (legacy ignores it), so it becomes 1.</item>
/// <item>Legacy has no metalness, reflection map or angle-dependent blend, so those are off.</item>
/// <item>Legacy reflects the room with the shine colour as its base reflectance; character.shpk with 4%. In game
/// (2026-10-18, Leonhart Bottoms in an inn) that left the silhouette darker. A white sheen at 25% of the row's
/// shine with aperture 4 (legacy's Fresnel power) halved that difference; 50% overshot.</item>
/// <item>Legacy writes lighting type 44 to the G-buffer; character.shpk writes 32 + the row's shader id.</item>
/// </list>
/// What can't match: environment reflections (the sheen only stands in for them at the edges),
/// a detail pattern's roughness channel now applies,
/// a shine colour map's tint becomes grey, and the velvet amount of the Mask type is dropped.
/// </summary>
public static class LegacyConversion
{
    public const string LegacyShader = "characterlegacy.shpk";
    public const string CharacterShader = "character.shpk";
    public const uint MaskSampler = 0x8A4E82B6;
    public const uint IndexSampler = 0x565F8FD8;

    /// <summary>The sheen per unit of legacy shine (strength × luma of the shine colour), see the class notes.</summary>
    public const float SheenPerShine = 0.25f;

    /// <summary>The row shader id whose G-buffer lighting type (32 + id) matches legacy's 44.</summary>
    public const int LegacyLightingShaderId = 12;

    private const uint GetValuesKey = 0xB616DC5A;
    private const uint Compatibility = 0x600EF9DF;
    private const uint SpecularTypeKey = 0xC8BD1DEF;
    private const uint SpecularTypeMask = 0xA02F4828;

    /// <summary>Constants character.shpk reads and characterlegacy.shpk doesn't; their defaults draw as legacy does.</summary>
    private static readonly uint[] CharacterOnlyConstants =
    [
        0xB5545FBB, // g_NormalScale (1)
        0xB61D7498, // mask blue's base-colour floor (0)
        0xD07A6A65, // g_AlphaOffset (0)
        0xD62BF368, // g_AlphaAperture (2)
    ];

    // The shaders' luma weights.
    private static readonly Vector3 Luma = new(0.29891f, 0.58661f, 0.11448f);

    public static bool IsLegacyShader(string package)
        => package[(package.LastIndexOfAny(['/', '\\']) + 1)..].Equals(LegacyShader, StringComparison.OrdinalIgnoreCase);

    /// <summary>The material's mask kind: its GetValues and shine-type keys (missing keys count as the package defaults).</summary>
    public static LegacyMaskKind MaskKind(MaterialDocument doc)
        => doc.KeyValue(GetValuesKey) != Compatibility ? LegacyMaskKind.Multi
            : doc.KeyValue(SpecularTypeKey) == SpecularTypeMask ? LegacyMaskKind.ShineMask : LegacyMaskKind.ShineColour;

    /// <summary>A legacy row in character.shpk's layout, looking the same with the mask <see cref="MaskTexel"/> makes.</summary>
    public static DtColorRow ToCharacter(LegacyLook look)
    {
        var row = DtColorRow.Default;
        row.Diffuse = look.Diffuse;
        row.Specular = ShineColour(look.Specular, look.ShineStrength);
        row.Emissive = look.Emissive;
        row.Exposure = 1f;
        row.SheenRate = SheenPerShine * Math.Clamp(look.ShineStrength * Vector3.Dot(look.Specular, Luma), 0f, 1f);
        row.SheenTint = 0f;
        row.SheenAperture = 4f;
        row.Roughness = Math.Clamp(float.Exp2(-look.Gloss / 15f), 0f, 1f);
        row.Metalness = 0f;
        row.Anisotropy = 0f;
        row.SphereMapMask = 0f;
        row.ShaderId = LegacyLightingShaderId;
        row.TileIndexValue = look.TileIndexValue;
        row.TileAlpha = look.TileAlpha;
        row.TileUU = look.TileUU;
        row.TileUV = look.TileUV;
        row.TileVU = look.TileVU;
        row.TileVV = look.TileVV;
        return row;
    }

    /// <summary>
    /// A character.shpk row as characterlegacy.shpk reads it (pasting between the two): gloss from the
    /// roughness, the shine colour as it is with its luma as the strength.
    /// </summary>
    public static DtColorRow ToLegacyShader(DtColorRow row)
    {
        var legacy = row;
        legacy.Scalar3 = row.Roughness <= 0f ? 100f : Math.Clamp(-15f * float.Log2(row.Roughness), 0f, 100f);
        legacy.Scalar7 = Vector3.Dot(row.Specular, Luma);
        return legacy;
    }

    /// <summary>A copied row as the target's shader reads rows: converted between characterlegacy.shpk and the others.</summary>
    public static DtColorRow RowForShader(DtColorRow copied, bool fromLegacy, bool toLegacy)
        => fromLegacy == toLegacy ? copied
            : fromLegacy ? ToCharacter(LegacyLook.FromLegacyShader(copied))
            : ToLegacyShader(copied);

    /// <summary>The shine colour times sqrt(strength / luma), so luma(colour) × colour comes out as strength × the old colour.</summary>
    private static Vector3 ShineColour(Vector3 specular, float strength)
    {
        var luma = Vector3.Dot(specular, Luma);
        if (strength <= 0f || luma <= 1e-4f)
            return Vector3.Zero;
        return specular * MathF.Sqrt(strength / luma);
    }

    /// <summary>
    /// A legacy dye row for character.shpk: base, shine and glow stay, gloss (bit 3) becomes roughness
    /// (bit 5), shine strength (bit 4) has no field and is dropped. Template t of the legacy file is
    /// t + 1000 in stainingtemplate_gud.stm (all 43 line up, with the same colours).
    /// </summary>
    public static DtDyeRow DyeToCharacter(int flags, int template, int channel)
    {
        var converted = (ushort)((flags & 0b111) | ((flags & 0b1000) != 0 ? 1 << 5 : 0));
        var dye = new DtDyeRow(0).WithFlags(converted).WithChannel(channel);
        return dye.WithTemplate(template is > 0 and < 1000 ? template + 1000 : template);
    }

    /// <summary>A 32-row table as characterlegacy.shpk reads it, in character.shpk's layout.</summary>
    public static ColorTable TableToCharacter(ColorTable table)
    {
        if (table.Kind != ColorTableKind.Dawntrail)
            throw new InvalidOperationException("Only a 32-row table is read this way.");
        var copy = table.Clone();
        for (var i = 0; i < copy.Rows.Length; i++)
            copy.Rows[i] = ToCharacter(LegacyLook.FromLegacyShader(copy.Rows[i]));
        if (copy.Dye != null)
            for (var i = 0; i < copy.Dye.Length; i++)
                copy.Dye[i] = copy.Dye[i].Flags == 0 && copy.Dye[i].Template == 0
                    ? copy.Dye[i]
                    : DyeToCharacter(copy.Dye[i].Flags, copy.Dye[i].Template, copy.Dye[i].Channel);
        return copy;
    }

    /// <summary>
    /// One texel of the new character.shpk mask from a legacy mask texel (0-1 per channel): red so that
    /// red⁴ matches what legacy multiplies the shine by, green 0.5 (roughness unchanged), blue the base-colour
    /// factor (squared by the shader).
    /// </summary>
    public static (float Red, float Green, float Blue) MaskTexel(LegacyMaskKind kind, float red, float green, float blue) => kind switch
    {
        // Shine colour × map rgb²: the map's luma² stands in for its tint.
        LegacyMaskKind.ShineColour => (MathF.Pow(Vector3.Dot(new Vector3(red * red, green * green, blue * blue), Luma), 0.25f), 0.5f, 1f),
        LegacyMaskKind.ShineMask => (red, 0.5f, 1f),
        // Strength × red², colour × green², base × blue².
        _ => (MathF.Sqrt(red * green), 0.5f, blue),
    };

    /// <summary>The new mask for a legacy mask (null: none, read as white), BGRA with mipmaps.</summary>
    public static byte[] MaskTexture(LegacyMaskKind kind, DecodedTexture? mask)
    {
        if (mask == null)
        {
            var (r, g, b) = MaskTexel(kind, 1f, 1f, 1f);
            var flat = new byte[4 * 4 * 4];
            for (var i = 0; i < 16; i++)
                Store(flat, i, r, g, b);
            return TextureFiles.BgraTexWithMips(flat, 4, 4);
        }
        var texels = mask.Width * mask.Height;
        var bgra = new byte[texels * 4];
        var source = mask.Bgra;
        for (var i = 0; i < texels; i++)
        {
            var (r, g, b) = MaskTexel(kind, source[i * 4 + 2] / 255f, source[i * 4 + 1] / 255f, source[i * 4] / 255f);
            Store(bgra, i, r, g, b);
        }
        return TextureFiles.BgraTexWithMips(bgra, mask.Width, mask.Height);
    }

    private static void Store(byte[] bgra, int texel, float r, float g, float b)
    {
        static byte Byte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
        bgra[texel * 4] = Byte(b);
        bgra[texel * 4 + 1] = Byte(g);
        bgra[texel * 4 + 2] = Byte(r);
        bgra[texel * 4 + 3] = 255;
    }

    /// <summary>
    /// The material on character.shpk with the same look: rows converted (a 16-row table first fills
    /// pairs as <see cref="ColorTable.ToDawntrail(bool)"/> does, blending into the next row when the
    /// index is made from the normal map), keys and slots fitted, character-only constants at their
    /// defaults, and the mask slot pointing at <paramref name="maskPath"/> (the texture
    /// <see cref="MaskTexture"/> makes). The notes say what else changed.
    /// </summary>
    public static (MaterialDocument Doc, IReadOnlyList<string> Notes) ToCharacter(MaterialDocument doc, string? maskPath, string? madeIndexPath = null)
    {
        var table = doc.Table switch
        {
            { Kind: ColorTableKind.Dawntrail } dt => TableToCharacter(dt),
            { Kind: ColorTableKind.Legacy } legacy => legacy.ToDawntrail(blendNext: madeIndexPath != null),
            { Kind: ColorTableKind.Opaque } => throw new InvalidOperationException("The colour table has a layout the editor doesn't know."),
            _ => null,
        };
        if (table != null)
            doc = doc.WithTable(table);
        foreach (var id in CharacterOnlyConstants)
            if (doc.Constant(id) != null)
                doc = doc.WithoutConstant(id);
        var (switched, notes) = MaterialSession.Reconcile(doc.WithShaderPackage(CharacterShader), ShaderPackages.Get(CharacterShader)!);
        doc = switched;
        var kept = notes.Where(note => !note.Contains("Make that texture", StringComparison.Ordinal)
                                       && !note.Contains("needs an Index texture", StringComparison.Ordinal)
                                       && !note.StartsWith("Removed Shine from", StringComparison.Ordinal)).ToList();
        if (maskPath != null)
            doc = WithSlotPath(doc, MaskSampler, maskPath);
        if (madeIndexPath != null)
            doc = WithSlotPath(doc, IndexSampler, madeIndexPath);
        return (doc, kept);
    }

    private static MaterialDocument WithSlotPath(MaterialDocument doc, uint sampler, string path)
    {
        var slot = doc.Slots.ToList().FindIndex(s => s.SamplerId == sampler);
        return slot < 0 ? doc.WithAddedSlot(sampler, path) : doc.WithSlot(slot, doc.Slots[slot] with { Path = path });
    }
}
