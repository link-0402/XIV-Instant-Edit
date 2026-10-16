using System;
using System.Collections.Generic;
using System.Linq;

namespace InstantEdit.Studio.Models;

/// <summary>
/// The shader packs available for FFXIV materials, following the list TexTools
/// exposes (its <c>EShaderPack</c> enum in xivModdingFramework).  The shader
/// pack identifier is the filename written into a .mtrl file's shader pack
/// field, e.g. "character.shpk".
/// </summary>
public enum ShaderType
{
    Character,
    CharacterLegacy,
    CharacterGlass,
    CharacterStockings,
    CharacterTattoo,
    CharacterScroll,
    CharacterInc,
    CharacterOcclusion,
    CharacterReflection,
    CharacterTransparency,
    Skin,
    Hair,
    Iris,
}

/// <summary>One shader definition: display name plus the game shader pack identifier.</summary>
public sealed record ShaderDef(ShaderType Type, string DisplayName, string ShaderPackName);

/// <summary>Static metadata about the supported shader types.</summary>
public static class ShaderInfo
{
    public static readonly IReadOnlyList<ShaderDef> All = new List<ShaderDef>
    {
        new(ShaderType.Character,             "Character",                "character.shpk"),
        new(ShaderType.CharacterLegacy,       "Character (Legacy)",       "characterlegacy.shpk"),
        new(ShaderType.CharacterGlass,        "Character (Glass)",        "characterglass.shpk"),
        new(ShaderType.CharacterStockings,    "Character (Stockings)",    "characterstockings.shpk"),
        new(ShaderType.CharacterTattoo,       "Character (Tattoo)",       "charactertattoo.shpk"),
        new(ShaderType.CharacterScroll,       "Character (Scroll)",       "characterscroll.shpk"),
        new(ShaderType.CharacterInc,          "Character (Incandescence)","characterinc.shpk"),
        new(ShaderType.CharacterOcclusion,    "Character (Occlusion)",    "characterocclusion.shpk"),
        new(ShaderType.CharacterReflection,   "Character (Reflection)",   "characterreflection.shpk"),
        new(ShaderType.CharacterTransparency, "Character (Transparency)", "charactertransparency.shpk"),
        new(ShaderType.Skin,                  "Skin",                     "skin.shpk"),
        new(ShaderType.Hair,                  "Hair",                     "hair.shpk"),
        new(ShaderType.Iris,                  "Iris",                     "iris.shpk"),
    };

    /// <summary>Display names in enum order, for ImGui combo boxes.</summary>
    public static readonly string[] Labels = All.Select(d => d.DisplayName).ToArray();

    public static ShaderDef Get(ShaderType type)
    {
        foreach (var def in All)
            if (def.Type == type)
                return def;
        return All[0];
    }

    public static string DisplayName(ShaderType type) => Get(type).DisplayName;

    public static string ShaderPackName(ShaderType type) => Get(type).ShaderPackName;

    // Dawntrail (7.0+) character shaders have no specular map — its role moved into the mask.
    // Only the pre-7.0 legacy shader still samples one.
    private static readonly TextureType[] CharacterTextures =
        { TextureType.Diffuse, TextureType.Normal, TextureType.Mask, TextureType.Index };

    /// <summary>
    /// The texture roles a shader samples — what "add texture slot" offers for a material using it.
    /// Follows the TexTools preset materials in Resources/MaterialPresets; shaders without one use
    /// the Dawntrail character set.
    /// </summary>
    public static IReadOnlyList<TextureType> SupportedTextures(ShaderType type) => type switch
    {
        ShaderType.CharacterLegacy
            => new[] { TextureType.Diffuse, TextureType.Normal, TextureType.Specular, TextureType.Mask, TextureType.Index },
        ShaderType.CharacterGlass  => new[] { TextureType.Normal, TextureType.Mask, TextureType.Index },
        ShaderType.CharacterTattoo => new[] { TextureType.Normal },
        ShaderType.Skin            => new[] { TextureType.Diffuse, TextureType.Normal, TextureType.Mask },
        ShaderType.Iris            => new[] { TextureType.Diffuse, TextureType.Normal, TextureType.Mask },
        ShaderType.Hair            => new[] { TextureType.Normal, TextureType.Mask },
        ShaderType.CharacterReflection
            => new[] { TextureType.Diffuse, TextureType.Normal, TextureType.Mask, TextureType.Index, TextureType.Reflection },
        _ => CharacterTextures,
    };

    /// <summary>The shader type a .mtrl's shader pack name ("hair.shpk") stands for, or null if unknown.</summary>
    public static ShaderType? FromShaderPack(string shaderPackName)
    {
        foreach (var def in All)
            if (string.Equals(def.ShaderPackName, shaderPackName, StringComparison.OrdinalIgnoreCase))
                return def.Type;
        return null;
    }
}

/// <summary>
/// The role a texture plays inside a material, matching the game's texture
/// usage types (and the _d/_n/_s/_m/_id/_r file suffixes TexTools uses).
/// </summary>
/// <summary>
/// What a texture is compressed to in the mod. BC7 is what the game itself uses for
/// modern textures and keeps the most detail, but compressing one is slow — a 4096×4096 image takes
/// the better part of a minute even across every core. BC3 is the older block format the game also
/// reads: around twenty times quicker to compress, softer on sharp gradients. BC5 only stores two
/// channels (red and green) at full precision each, which is what the game's own index textures use —
/// their blue and alpha channels go unused, so BC5 holds the two that matter (row and blend) more
/// precisely than BC7 or BC3 would, for about the same compression cost as BC3. None writes the raw
/// pixels, which costs nothing to convert and four times the memory in game. Auto lets the write
/// decide: BC5 for an index map, BC7 otherwise, unless the check Optimize textures uses finds that
/// compressing would change how the texture looks, which leaves it uncompressed.
/// </summary>
public enum TextureCompression
{
    None,
    Bc3,
    Bc7,
    Bc5,
    Auto,
}

/// <summary>The texture formats as the format pickers list them.</summary>
public static class TextureCompressionInfo
{
    public static readonly TextureCompression[] Order =
        { TextureCompression.Auto, TextureCompression.Bc7, TextureCompression.Bc5, TextureCompression.Bc3, TextureCompression.None };

    public static readonly string[] Labels = { "Auto", "BC7", "BC5", "BC3", "None" };

    public static int IndexOf(TextureCompression compression) => Math.Max(0, Array.IndexOf(Order, compression));

    /// <summary>A generated placeholder is one colour, which BC7 keeps exactly, so Auto writes it as BC7.</summary>
    public static TextureCompression ForPlaceholder(TextureCompression compression)
        => compression == TextureCompression.Auto ? TextureCompression.Bc7 : compression;
}

public enum TextureType
{
    Diffuse,
    Normal,
    Specular,
    Mask,
    Index,
    Reflection,
}

public static class TextureTypeInfo
{
    public static readonly string[] Labels =
    {
        "Diffuse",
        "Normal",
        "Specular",
        "Mask / Multiply",
        "Color Set / Index",
        "Reflection / Matcap",
    };

    public static string DisplayName(TextureType type) => Labels[(int)type];

    public static TextureType FromIndex(int index)
        => index >= 0 && index < Labels.Length ? (TextureType)index : TextureType.Diffuse;
}
