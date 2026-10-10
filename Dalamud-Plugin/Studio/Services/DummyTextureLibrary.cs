using System.Collections.Generic;
using InstantEdit.Studio.Models;

namespace InstantEdit.Studio.Services;

/// <summary>
/// Bundled placeholder textures a texture slot can be filled with instead of a local file:
/// either a generated solid colour, sized on demand, or a ready-made .tex copied in as-is.
/// Options differ by texture role — a fully white square passes a mask straight through
/// unmodified, but is the wrong placeholder for a normal map, whose "no bump" colour is a
/// flat (128,128,255), not white — so <see cref="OptionsFor"/> only offers what makes sense
/// for the role, and a role with a single fixed placeholder (normal) offers no size or
/// generated option at all.
/// </summary>
public static class DummyTextureLibrary
{
    /// <summary>One dummy option for a texture role. <see cref="ResourcePath"/> is null for the generated option.</summary>
    public sealed record DummyOption(string Key, string Label, string? ResourcePath)
    {
        /// <summary>True for the built-in generated-white-square option; false for a bundled file copied in as-is.</summary>
        public bool IsGenerated => ResourcePath == null;
    }

    private const string Root = "Resources/DummyTextures";

    public const string GeneratedKey  = "white";
    public const string NullNormalKey = "null_normal";
    public const string MetalKey      = "metal";

    private static readonly DummyOption Generated  = new(GeneratedKey,  "White (generated)", null);
    private static readonly DummyOption NullNormal = new(NullNormalKey, "Flat (no bump)",     $"{Root}/null_normal.tex");
    private static readonly DummyOption Metal      = new(MetalKey,      "Metal",              $"{Root}/metal.tex");

    /// <summary>The dummy options offered for a texture role, in display order. The first is the default.</summary>
    public static IReadOnlyList<DummyOption> OptionsFor(TextureType type) => type switch
    {
        // A plain white square reads as "no bump" for a diffuse/mask/specular map, but a real
        // normal map's neutral colour points straight up — only the bundled flat map is offered.
        TextureType.Normal => new[] { NullNormal },
        TextureType.Mask   => new[] { Generated, Metal },
        _                  => new[] { Generated },
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
