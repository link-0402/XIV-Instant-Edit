using System;
using System.Collections.Generic;
using System.Linq;

namespace InstantEdit.Studio.Models;

/// <summary>
/// A pre-made material type: a shader pack plus the texture slots that are
/// pre-configured when the preset is applied, mirroring the texture sets the
/// game uses for each shader pack.
/// </summary>
public sealed record MaterialPreset(string Name, ShaderType Shader, IReadOnlyList<TextureType> Textures);

/// <summary>Static list of pre-made material presets.</summary>
public static class MaterialPresets
{
    /// <remarks>
    /// Texture sets follow the TexTools preset materials bundled under Resources/MaterialPresets
    /// (each preset's .mtrl lists exactly these textures). Since 7.0 the specular map is folded
    /// into the mask everywhere except the legacy shader's "Diffuse + Specular" layout.
    /// Matcap is characterreflection.shpk with the matcap image in its catchlight sampler; left
    /// empty, that slot reads the MatCap Library mod's shared texture (see MaterialPatcher).
    /// </remarks>
    public static readonly IReadOnlyList<MaterialPreset> All = new List<MaterialPreset>
    {
        new("Gear",                                ShaderType.Character,       new[] { TextureType.Normal, TextureType.Mask, TextureType.Index }),
        new("Gear with Diffuse",                   ShaderType.Character,       new[] { TextureType.Diffuse, TextureType.Normal, TextureType.Mask, TextureType.Index }),
        new("Gear Legacy (Diffuse + Mask)",        ShaderType.CharacterLegacy, new[] { TextureType.Diffuse, TextureType.Normal, TextureType.Mask, TextureType.Index }),
        new("Gear Legacy (Diffuse + Specular)",    ShaderType.CharacterLegacy, new[] { TextureType.Diffuse, TextureType.Normal, TextureType.Specular, TextureType.Index }),
        new("Glass",                               ShaderType.CharacterGlass,  new[] { TextureType.Normal, TextureType.Mask, TextureType.Index }),
        new("Scroll",                              ShaderType.CharacterScroll, new[] { TextureType.Normal, TextureType.Mask, TextureType.Index }),
        new("Skin",                                ShaderType.Skin,            new[] { TextureType.Diffuse, TextureType.Normal, TextureType.Mask }),
        new("Hair",                                ShaderType.Hair,            new[] { TextureType.Normal, TextureType.Mask }),
        new("Iris",                                ShaderType.Iris,            new[] { TextureType.Diffuse, TextureType.Normal, TextureType.Mask }),
        new("Face Tattoo",                         ShaderType.CharacterTattoo, new[] { TextureType.Normal }),
        new("Matcap",                              ShaderType.CharacterReflection, new[] { TextureType.Normal, TextureType.Mask, TextureType.Reflection }),
    };

    /// <summary>Preset names in list order, for ImGui combo boxes.</summary>
    public static readonly string[] Names = All.Select(p => p.Name).ToArray();
}
