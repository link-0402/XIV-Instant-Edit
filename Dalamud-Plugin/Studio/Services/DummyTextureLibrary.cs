using System.Collections.Generic;
using InstantEdit.Studio.Models;

namespace InstantEdit.Studio.Services;

/// <summary>
/// Placeholder textures a texture slot can be filled with instead of a local file: each one a
/// generated <see cref="Size"/> × <see cref="Size"/> square of one colour. Options differ by
/// texture role — a fully white square passes a mask straight through unmodified, but is the
/// wrong placeholder for a normal map, whose "no bump" colour is a flat (128,128,255), not
/// white — so <see cref="OptionsFor"/> only offers what makes sense for the role.
/// </summary>
public static class DummyTextureLibrary
{
    /// <summary>One placeholder option for a texture role: a solid colour, as 0–255 RGBA.</summary>
    public sealed record DummyOption(string Key, string Label, byte R, byte G, byte B, byte A = 255);

    /// <summary>Side length of every generated placeholder. One colour reads the same at any size.</summary>
    public const int Size = 16;

    public const string GeneratedKey  = "white";
    public const string NullNormalKey = "null_normal";
    public const string MetalKey      = "metal";

    private static readonly DummyOption White      = new(GeneratedKey,  "White",          255, 255, 255);
    private static readonly DummyOption NullNormal = new(NullNormalKey, "Flat (no bump)", 128, 128, 255);
    private static readonly DummyOption Metal      = new(MetalKey,      "Metal",          185,   5, 185);

    /// <summary>The placeholder options offered for a texture role, in display order. The first is the default.</summary>
    public static IReadOnlyList<DummyOption> OptionsFor(TextureType type) => type switch
    {
        // A plain white square reads as "no bump" for a diffuse/mask/specular map, but a real
        // normal map's neutral colour points straight up — only the flat map is offered.
        TextureType.Normal => new[] { NullNormal },
        TextureType.Mask   => new[] { White, Metal },
        _                  => new[] { White },
    };

    /// <summary>Resolves a saved key to its option, falling back to the role's default (first option) when unset or unknown.</summary>
    public static DummyOption Resolve(TextureType type, string? key)
    {
        var options = OptionsFor(type);
        if (!string.IsNullOrEmpty(key))
            foreach (var option in options)
                if (option.Key == key)
                    return option;
        return options[0];
    }
}
