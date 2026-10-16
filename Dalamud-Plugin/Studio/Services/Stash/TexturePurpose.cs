using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using InstantEdit.Studio.Services.Sims;

namespace InstantEdit.Studio.Services.Stash;

/// <summary>What an extracted texture is for, as far as the stash can tell.</summary>
public enum TexturePurpose { Diffuse, Normal, Mask, Occlusion, Emission, Other }

/// <summary>
/// Works out a texture's purpose from what its source says about it: the Sims CAS part's role, the
/// Unity material property that uses it, Unity's normal-map import flag, and last its file name.
/// </summary>
public static class TexturePurposes
{
    public static TexturePurpose? FromSimsRole(SimsTextureRole role) => role switch
    {
        SimsTextureRole.Diffuse  => TexturePurpose.Diffuse,
        SimsTextureRole.Normal   => TexturePurpose.Normal,
        SimsTextureRole.Specular => TexturePurpose.Mask,
        SimsTextureRole.Shadow   => TexturePurpose.Occlusion,
        SimsTextureRole.Emission => TexturePurpose.Emission,
        _                        => null,
    };

    private static readonly Dictionary<string, TexturePurpose> UnityProperties = new(StringComparer.Ordinal)
    {
        ["_MainTex"] = TexturePurpose.Diffuse, ["_BaseMap"] = TexturePurpose.Diffuse, ["_BaseColorMap"] = TexturePurpose.Diffuse,
        ["_Albedo"] = TexturePurpose.Diffuse, ["_MainTexture"] = TexturePurpose.Diffuse,
        ["_BumpMap"] = TexturePurpose.Normal, ["_NormalMap"] = TexturePurpose.Normal, ["_DetailNormalMap"] = TexturePurpose.Normal,
        ["_MetallicGlossMap"] = TexturePurpose.Mask, ["_SpecGlossMap"] = TexturePurpose.Mask, ["_MaskMap"] = TexturePurpose.Mask,
        ["_SmoothnessMap"] = TexturePurpose.Mask, ["_MainColorAdjustMask"] = TexturePurpose.Mask,
        ["_ShadowColorTex"] = TexturePurpose.Mask, ["_MatCapBlendMask"] = TexturePurpose.Mask,
        ["_OcclusionMap"] = TexturePurpose.Occlusion, ["_AOMap"] = TexturePurpose.Occlusion,
        ["_EmissionMap"] = TexturePurpose.Emission, ["_EmissiveMap"] = TexturePurpose.Emission, ["_Emission2ndMap"] = TexturePurpose.Emission,
    };

    public static TexturePurpose? FromUnityProperty(string property)
        => UnityProperties.TryGetValue(property, out var purpose) ? purpose : null;

    private static readonly HashSet<string> NormalTokens    = new(StringComparer.Ordinal) { "n", "nrm", "nml", "norm", "normal", "normals", "bump" };
    private static readonly HashSet<string> OcclusionTokens = new(StringComparer.Ordinal) { "ao", "occlusion" };
    private static readonly HashSet<string> EmissionTokens  = new(StringComparer.Ordinal) { "glow", "emission", "emissive" };
    private static readonly HashSet<string> MaskTokens      = new(StringComparer.Ordinal)
        { "m", "s", "mask", "spec", "specular", "metal", "metallic", "rough", "roughness", "gloss", "smoothness" };
    private static readonly HashSet<string> DiffuseTokens   = new(StringComparer.Ordinal)
        { "d", "c", "col", "color", "colour", "diffuse", "albedo", "basecolor", "base" };

    /// <summary>A guess from the file name alone: its words (split on anything not a letter or digit) and a few telling fragments.</summary>
    public static TexturePurpose FromFileName(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        var words = new string(stem.Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray());
        var tokens = words.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                         .Select(t => t.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9'))
                         .Where(t => t.Length > 0)
                         .ToHashSet(StringComparer.Ordinal);

        if (tokens.Overlaps(NormalTokens) || stem.Contains("normal", StringComparison.Ordinal)) return TexturePurpose.Normal;
        if (tokens.Overlaps(OcclusionTokens) || stem.Contains("occlusion", StringComparison.Ordinal)) return TexturePurpose.Occlusion;
        if (tokens.Overlaps(EmissionTokens) || stem.Contains("emiss", StringComparison.Ordinal)) return TexturePurpose.Emission;
        if (tokens.Overlaps(MaskTokens) || stem.Contains("mask", StringComparison.Ordinal) || stem.Contains("specular", StringComparison.Ordinal))
            return TexturePurpose.Mask;
        if (tokens.Overlaps(DiffuseTokens) || stem.Contains("diffuse", StringComparison.Ordinal) || stem.Contains("albedo", StringComparison.Ordinal)
            || stem.Contains("basecolor", StringComparison.Ordinal) || stem.Contains("base_color", StringComparison.Ordinal))
            return TexturePurpose.Diffuse;
        return TexturePurpose.Other;
    }

    /// <summary>The first that says anything: the source's own role, the material property, Unity's normal-map flag, the file name.</summary>
    public static TexturePurpose Classify(TexturePurpose? fromSource, TexturePurpose? fromProperty, int unityTextureType, string fileName)
        => fromSource ?? fromProperty ?? (unityTextureType == 1 ? TexturePurpose.Normal : FromFileName(fileName));
}
