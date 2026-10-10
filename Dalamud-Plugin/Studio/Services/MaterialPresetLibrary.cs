using System.Collections.Generic;
using System.IO;
using System.Linq;
using InstantEdit.Studio.Models;

namespace InstantEdit.Studio.Services;

/// <summary>
/// Looks up a bundled shader-appropriate material preset (sourced from TexTools' own
/// bundled presets — see Resources/MaterialPresets) for a given <see cref="ShaderType"/>,
/// so materials reflect the shader the user actually picked instead of whatever shader
/// the vanilla item happened to use. Shader types with no bundled preset return null;
/// the caller falls back to <see cref="GameDataService.FindVanillaMaterialTemplate"/>.
/// </summary>
public static class MaterialPresetLibrary
{
    private const string Root = "Resources/MaterialPresets";

    /// <summary>
    /// Returns a path relative to the plugin's own directory, or null if no bundled
    /// preset exists for this shader.
    /// </summary>
    public static string? FindPresetPath(ShaderType shaderType, IReadOnlyCollection<TextureType> configuredTypes)
        => shaderType switch
        {
            ShaderType.Character        => Combine("character", "Default Equipment - Mask.mtrl"),
            ShaderType.CharacterLegacy   => Combine("characterlegacy", PickLegacyVariant(configuredTypes)),
            ShaderType.CharacterGlass    => Combine("characterglass", "Glasses.mtrl"),
            ShaderType.CharacterTattoo   => Combine("charactertattoo", "Default Face Tattoo.mtrl"),
            ShaderType.Hair              => Combine("hair", "Default Hair.mtrl"),
            ShaderType.Iris              => Combine("iris", "Default Eye.mtrl"),
            ShaderType.Skin              => Combine("skin", "Default Skin.mtrl"),

            // Not a TexTools preset: the material of Westlaketea's MatCap Library mod.
            ShaderType.CharacterReflection => Combine("characterreflection", "Matcap.mtrl"),

            // No bundled preset yet for these — caller falls back to vanilla-cloning.
            ShaderType.CharacterStockings     => null,
            ShaderType.CharacterScroll        => null,
            ShaderType.CharacterInc           => null,
            ShaderType.CharacterOcclusion     => null,
            ShaderType.CharacterTransparency  => null,
            _                                  => null,
        };

    /// <summary>CharacterLegacy has 3 preset variants; pick the one matching what the user actually configured.</summary>
    private static string PickLegacyVariant(IReadOnlyCollection<TextureType> configuredTypes)
    {
        if (configuredTypes.Contains(TextureType.Specular))
            return "Default Equipment - Diffuse + Specular.mtrl";
        if (configuredTypes.Contains(TextureType.Diffuse))
            return "Default Equipment - Diffuse + Mask.mtrl";
        return "Default Equipment - Mask.mtrl";
    }

    private static string Combine(string shpkFolder, string fileName)
        => Path.Combine(Root, shpkFolder, fileName);
}
