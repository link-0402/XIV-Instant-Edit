using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using InstantEdit.Studio.Services.Sims;

namespace InstantEdit.Studio.Services.Stash;

/// <summary>What an extracted texture is for, as far as the stash can tell.</summary>
public enum TexturePurpose { Diffuse, Normal, Mask, Occlusion, Emission, Matcap, Other }

/// <summary>
/// Works out a texture's purpose from what its source says about it: the Sims CAS part's role, the
/// Unity material property that uses it, Unity's normal-map import flag, and last its file name and
/// the files beside it.
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

    // Layer and matcap blend masks (lilToon's _Main2ndBlendMask and the like) are left out on purpose:
    // a material's Mask goes into the game's mask slot, and those are not that. Their file names still
    // list them as masks.
    private static readonly Dictionary<string, TexturePurpose> UnityProperties = new(StringComparer.Ordinal)
    {
        ["_MainTex"] = TexturePurpose.Diffuse, ["_BaseMap"] = TexturePurpose.Diffuse, ["_BaseColorMap"] = TexturePurpose.Diffuse,
        ["_Albedo"] = TexturePurpose.Diffuse, ["_MainTexture"] = TexturePurpose.Diffuse,
        ["_BumpMap"] = TexturePurpose.Normal, ["_NormalMap"] = TexturePurpose.Normal, ["_DetailNormalMap"] = TexturePurpose.Normal,
        ["_MetallicGlossMap"] = TexturePurpose.Mask, ["_SpecGlossMap"] = TexturePurpose.Mask, ["_MaskMap"] = TexturePurpose.Mask,
        ["_SmoothnessMap"] = TexturePurpose.Mask, ["_MainColorAdjustMask"] = TexturePurpose.Mask,
        ["_ShadowColorTex"] = TexturePurpose.Mask, ["_MatCapBlendMask"] = TexturePurpose.Mask,
        ["_OcclusionMap"] = TexturePurpose.Occlusion, ["_AOMap"] = TexturePurpose.Occlusion,
        ["_ShadowBorderMask"] = TexturePurpose.Occlusion,   // lilToon's "AO map"
        ["_EmissionMap"] = TexturePurpose.Emission, ["_EmissiveMap"] = TexturePurpose.Emission, ["_Emission2ndMap"] = TexturePurpose.Emission,
        // lilToon, Poiyomi, UnityChan Toon Shader and MMD-style sphere maps.
        ["_MatCapTex"] = TexturePurpose.Matcap, ["_MatCap2ndTex"] = TexturePurpose.Matcap,
        ["_Matcap"] = TexturePurpose.Matcap, ["_Matcap2"] = TexturePurpose.Matcap, ["_Matcap3"] = TexturePurpose.Matcap, ["_Matcap4"] = TexturePurpose.Matcap,
        ["_MatCap_Sampler"] = TexturePurpose.Matcap, ["_SphereAddTex"] = TexturePurpose.Matcap, ["_SphereMulTex"] = TexturePurpose.Matcap,
    };

    public static TexturePurpose? FromUnityProperty(string property)
        => UnityProperties.TryGetValue(property, out var purpose) ? purpose : null;

    /// <summary>
    /// The purpose most of a texture's uses give it, or null when none says anything. A diffuse a
    /// few materials also stack as a decal layer stays a diffuse.
    /// </summary>
    public static TexturePurpose? MostCommon(IEnumerable<TexturePurpose?> purposes)
        => purposes.OfType<TexturePurpose>()
                   .GroupBy(p => p)
                   .OrderByDescending(g => g.Count())
                   .ThenBy(g => g.Key)
                   .Select(g => (TexturePurpose?)g.Key)
                   .FirstOrDefault();

    private static readonly HashSet<string> NormalTokens    = new(StringComparer.Ordinal) { "n", "nrm", "nml", "norm", "normal", "normals", "bump" };
    private static readonly HashSet<string> OcclusionTokens = new(StringComparer.Ordinal) { "ao", "occlusion" };
    private static readonly HashSet<string> EmissionTokens  = new(StringComparer.Ordinal) { "glow", "emission", "emissive" };
    private static readonly HashSet<string> MaskTokens      = new(StringComparer.Ordinal)
        { "m", "s", "mask", "spec", "specular", "metal", "metallic", "rough", "roughness", "gloss", "smoothness" };
    private static readonly HashSet<string> DiffuseTokens   = new(StringComparer.Ordinal)
        { "d", "c", "col", "color", "colour", "diffuse", "albedo", "basecolor", "base" };
    private static readonly HashSet<string> MatcapTokens    = new(StringComparer.Ordinal) { "matcap", "matcaps", "spheremap" };

    /// <summary>Store pictures and screenshots that come with a download, not textures of the model.</summary>
    private static readonly HashSet<string> PreviewTokens = new(StringComparer.Ordinal)
        { "preview", "previews", "thumb", "thumbnail", "thumbnails", "screenshot", "screenshots", "resized", "promo", "banner", "showcase" };

    /// <summary>
    /// Words that name a look rather than a map. Alone (no texture set around them, as in "Gloss 1",
    /// "Metal 2", "Hologram") they are matcaps; beside a diffuse ("Body_Gloss") they keep their role.
    /// </summary>
    private static readonly HashSet<string> LookTokens = new(StringComparer.Ordinal)
    {
        "gloss", "glossy", "metal", "chrome", "latex", "glow", "light", "hologram", "holo", "shine", "shiny", "rim",
        "reflect", "reflection", "gold", "silver", "pearl", "iridescent", "rainbow",
    };

    /// <summary>A name a download site gave the file (Booth's "&lt;guid&gt;_base_resized.jpg"), not one the artist did.</summary>
    private static readonly Regex GuidName = new(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.CultureInvariant);

    /// <summary>The words of a file stem, lower case, split on anything not a letter or digit, with trailing digits dropped.</summary>
    internal static List<string> Words(string stem)
        => new string(stem.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9'))
            .Where(t => t.Length > 0)
            .ToList();

    /// <summary>A store picture or screenshot that came with a download (by its name or folder), not a texture of the model.</summary>
    public static bool IsPreview(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        return GuidName.IsMatch(stem) || Words(stem).Any(PreviewTokens.Contains) || ParentFolders(fileName).SelectMany(Words).Any(PreviewTokens.Contains);
    }

    /// <summary>The names of the two folders a file sits in, lower case, for "Matcap/" or "Preview/" hints.</summary>
    private static IEnumerable<string> ParentFolders(string fileName)
    {
        var dir = Path.GetDirectoryName(fileName);
        for (int i = 0; i < 2 && !string.IsNullOrEmpty(dir); i++)
        {
            var name = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(name))
                yield break;
            yield return name.ToLowerInvariant();
            dir = Path.GetDirectoryName(dir);
        }
    }

    /// <summary>A guess from the file name alone: its words (split on anything not a letter or digit), a few telling fragments, and the folders it sits in.</summary>
    public static TexturePurpose FromFileName(string fileName)
    {
        if (IsPreview(fileName))
            return TexturePurpose.Other;
        var stem = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        var guess = FromWords(stem, Words(stem).ToHashSet(StringComparer.Ordinal));
        if (guess != TexturePurpose.Normal && ParentFolders(fileName).Any(f => f.Contains("matcap", StringComparison.Ordinal)))
            return TexturePurpose.Matcap;
        return guess;
    }

    private static TexturePurpose FromWords(string stem, HashSet<string> tokens)
    {
        if (tokens.Overlaps(NormalTokens) || stem.Contains("normal", StringComparison.Ordinal)) return TexturePurpose.Normal;
        if (tokens.Overlaps(MatcapTokens) || stem.Contains("matcap", StringComparison.Ordinal)) return TexturePurpose.Matcap;
        if (tokens.Overlaps(OcclusionTokens) || stem.Contains("occlusion", StringComparison.Ordinal)) return TexturePurpose.Occlusion;
        if (tokens.Overlaps(EmissionTokens) || stem.Contains("emiss", StringComparison.Ordinal)) return TexturePurpose.Emission;
        if (tokens.Overlaps(MaskTokens) || stem.Contains("mask", StringComparison.Ordinal) || stem.Contains("specular", StringComparison.Ordinal))
            return TexturePurpose.Mask;
        if (tokens.Overlaps(DiffuseTokens) || stem.Contains("diffuse", StringComparison.Ordinal) || stem.Contains("albedo", StringComparison.Ordinal)
            || stem.Contains("basecolor", StringComparison.Ordinal) || stem.Contains("base_color", StringComparison.Ordinal))
            return TexturePurpose.Diffuse;
        return TexturePurpose.Other;
    }

    /// <summary>
    /// File-name guesses for a set of textures, reading each name next to the others in its folder:
    /// a texture without a role word whose name others extend with one (Body beside Body_Normal and
    /// Body_AO) is their diffuse, and so is a variant of a diffuse without a role word of its own
    /// (Body_Alpha); a look word with no texture set around it (Gloss 1, Metal 2, Hologram) is a
    /// matcap. Keyed by the paths given.
    /// </summary>
    public static Dictionary<string, TexturePurpose> FromFileNames(IEnumerable<string> files)
    {
        var result = new Dictionary<string, TexturePurpose>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in files.Distinct(StringComparer.OrdinalIgnoreCase)
                                    .GroupBy(f => Path.GetDirectoryName(f) ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            // Shortest first, so a variant's base is settled before the variant.
            var ordered = folder.OrderBy(f => Path.GetFileNameWithoutExtension(f).Length).ToList();
            var byStem = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in ordered)
            {
                byStem.TryAdd(Path.GetFileNameWithoutExtension(file), file);
                result[file] = FromFileName(file);
            }

            // Every name that extends another ("Body" → "Body_AO", "Body_Alpha_Normal").
            var extensions = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var parent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in ordered)
            {
                var stem = Path.GetFileNameWithoutExtension(file);
                for (int i = stem.Length - 1; i > 0; i--)
                {
                    if (char.IsLetterOrDigit(stem[i]) || !byStem.TryGetValue(stem[..i], out var shorter) || shorter == file)
                        continue;
                    parent.TryAdd(file, shorter);   // the longest, found first
                    if (!extensions.TryGetValue(shorter, out var list))
                        extensions[shorter] = list = new();
                    list.Add(file);
                }
            }

            foreach (var file in ordered)
            {
                if (result[file] != TexturePurpose.Other || IsPreview(file))
                    continue;
                bool baseOfASet = extensions.TryGetValue(file, out var longer)
                    && longer.Any(l => result[l] is TexturePurpose.Normal or TexturePurpose.Mask or TexturePurpose.Occlusion or TexturePurpose.Emission);
                bool variantOfADiffuse = parent.TryGetValue(file, out var p) && result[p] == TexturePurpose.Diffuse;
                if (baseOfASet || variantOfADiffuse)
                    result[file] = TexturePurpose.Diffuse;
            }

            // What a name is about once its role and look words are gone ("LKVK_Metal 1" → "lkvk"); a
            // look name is a matcap only while nothing about the same thing is a diffuse or occlusion map.
            string Subject(string file) => string.Join(' ', Words(Path.GetFileNameWithoutExtension(file))
                .Where(w => !LookTokens.Contains(w) && !NormalTokens.Contains(w) && !OcclusionTokens.Contains(w) && !EmissionTokens.Contains(w)
                         && !MaskTokens.Contains(w) && !DiffuseTokens.Contains(w)));
            var textureSets = ordered.Where(f => result[f] is TexturePurpose.Diffuse or TexturePurpose.Occlusion)
                                     .Select(Subject)
                                     .ToHashSet(StringComparer.Ordinal);
            foreach (var file in ordered)
            {
                if (result[file] is not (TexturePurpose.Mask or TexturePurpose.Emission or TexturePurpose.Other)
                    || IsPreview(file) || parent.ContainsKey(file) || extensions.ContainsKey(file))
                    continue;
                var stem = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                var words = Words(stem);
                if (!words.Any(LookTokens.Contains) || textureSets.Contains(Subject(file)))
                    continue;
                // Only the look words may speak for it: "Gloss_Mask" stays a mask.
                var rest = words.Where(w => !LookTokens.Contains(w)).ToList();
                if (FromWords(string.Join(' ', rest), rest.ToHashSet(StringComparer.Ordinal)) == TexturePurpose.Other)
                    result[file] = TexturePurpose.Matcap;
            }
        }
        return result;
    }

    /// <summary>Words a texture set's names end in after the name of the thing they belong to ("Body_Normal", "top_id", "Shirt_Alpha").</summary>
    private static readonly HashSet<string> SuffixWords = new(
        NormalTokens.Concat(OcclusionTokens).Concat(EmissionTokens).Concat(MaskTokens).Concat(DiffuseTokens)
                    .Concat(new[] { "id", "index", "alpha", "opacity", "transparency", "height", "disp", "displacement" }),
        StringComparer.Ordinal);

    /// <summary><paramref name="stem"/> without the role words it ends in ("Body_Normal" → "Body", "top_m" → "top"); itself when it has none.</summary>
    private static string WithoutRoleSuffix(string stem)
    {
        while (true)
        {
            int cut = stem.Length - 1;
            while (cut > 0 && char.IsLetterOrDigit(stem[cut]))
                cut--;
            if (cut <= 0)
                return stem;
            var word = stem[(cut + 1)..].ToLowerInvariant().TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
            if (!SuffixWords.Contains(word))
                return stem;
            var shorter = stem[..cut].TrimEnd(' ', '_', '-', '.');
            if (shorter.Length == 0)
                return stem;
            stem = shorter;
        }
    }

    /// <summary>
    /// The texture set each file belongs to, read from the names in its folder: what its name extends
    /// ("Body_Alpha_Normal" → "Body", when there is a Body), or else its name without the role words it
    /// ends in ("top_id", "top_m" → "top"). Keyed by the paths given; the value is the set's name, and
    /// files of one set in one folder share it. A file in no set keys to its own name.
    /// </summary>
    public static Dictionary<string, string> SetNames(IEnumerable<string> files)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in files.Distinct(StringComparer.OrdinalIgnoreCase)
                                    .GroupBy(f => Path.GetDirectoryName(f) ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            var byStem = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in folder.OrderBy(f => Path.GetFileNameWithoutExtension(f).Length))
                byStem.TryAdd(Path.GetFileNameWithoutExtension(file), file);

            // The shortest file name this one extends, down the chain ("Body_Alpha_Normal" → "Body").
            string Root(string stem)
            {
                for (int i = 1; i < stem.Length; i++)
                    if (!char.IsLetterOrDigit(stem[i]) && byStem.ContainsKey(stem[..i]))
                        return Path.GetFileNameWithoutExtension(byStem[stem[..i]]);
                return stem;
            }

            foreach (var file in folder)
            {
                var stem = Path.GetFileNameWithoutExtension(file);
                var root = Root(stem);
                if (root == stem)
                {
                    var bare = WithoutRoleSuffix(stem);
                    root = byStem.ContainsKey(bare) ? Root(Path.GetFileNameWithoutExtension(byStem[bare])) : bare;
                }
                result[file] = root;
            }
        }
        return result;
    }

    /// <summary>The first that says anything: the source's own role, the material property, Unity's normal-map flag, the file name.</summary>
    public static TexturePurpose Classify(TexturePurpose? fromSource, TexturePurpose? fromProperty, int unityTextureType, TexturePurpose fromName)
        => fromSource ?? fromProperty ?? (unityTextureType == 1 ? TexturePurpose.Normal : fromName);

    public static TexturePurpose Classify(TexturePurpose? fromSource, TexturePurpose? fromProperty, int unityTextureType, string fileName)
        => Classify(fromSource, fromProperty, unityTextureType, FromFileName(fileName));
}
