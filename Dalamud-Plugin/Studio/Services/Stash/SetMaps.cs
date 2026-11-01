using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using InstantEdit.Studio.Models;

namespace InstantEdit.Studio.Services.Stash;

/// <summary>
/// The maps of a texture set the game has no slot for, found beside the textures a material was given:
/// another game's opacity, roughness (or gloss), metallic and occlusion maps ("ASA_Opacity" beside
/// "ASA_Base_color", as Substance Painter writes them). They go into what the game reads instead: the
/// opacity into the normal's blue, the rest into a made mask (see <see cref="GameNormal"/>, <see cref="GameMask"/>).
/// </summary>
internal sealed record SetMaps(string? Opacity, string? Roughness, string? Metallic, string? Occlusion)
{
    private static readonly HashSet<string> OpacityWords   = new(StringComparer.Ordinal) { "opacity", "transparency", "alpha" };
    /// <summary>The one opacity word that also names a diffuse's variant with an alpha of its own ("Body_Alpha" beside "Body").</summary>
    private const string AlphaWord = "alpha";
    private static readonly HashSet<string> RoughnessWords = new(StringComparer.Ordinal) { "rough", "roughness", "gloss", "glossiness", "smooth", "smoothness" };
    private static readonly HashSet<string> MetallicWords  = new(StringComparer.Ordinal) { "metal", "metallic", "metalness" };
    private static readonly HashSet<string> OcclusionWords = new(StringComparer.Ordinal) { "ao", "occlusion" };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".tga", ".dds" };

    public bool ForMask => Roughness != null || Metallic != null || Occlusion != null;

    /// <summary>
    /// The maps of the set the given textures belong to: the files of their folder named for the same set
    /// (see <see cref="TexturePurposes.SetNames"/>), by the words their names end in, and the stash's own
    /// mask and occlusion picks. A .tex is the game's own and is left out; so is the diffuse itself
    /// (a "Body_Alpha" diffuse is a variant, not an opacity map).
    /// </summary>
    public static SetMaps Find(IReadOnlyDictionary<TexturePurpose, string> textures)
    {
        string? opacity = null, roughness = null, metallic = null, occlusion = null;
        Dictionary<string, TexturePurpose>? purposes = null;
        void Take(string file)
        {
            if (!ImageExtensions.Contains(Path.GetExtension(file)) || TexturePurposes.IsPreview(file))
                return;
            var words = TexturePurposes.Words(Path.GetFileNameWithoutExtension(file));
            if (words.Any(MetallicWords.Contains)) metallic ??= file;   // "MetallicSmoothness" is Unity's metallic map, smoothness in its alpha
            else if (words.Any(RoughnessWords.Contains)) roughness ??= file;
            else if (words.Any(OcclusionWords.Contains)) occlusion ??= file;
            else if (words.Any(w => OpacityWords.Contains(w) && w != AlphaWord)
                     || words.Contains(AlphaWord) && purposes?.GetValueOrDefault(file) != TexturePurpose.Diffuse)
                opacity ??= file;
        }

        var diffuse = textures.GetValueOrDefault(TexturePurpose.Diffuse);
        var normal  = textures.GetValueOrDefault(TexturePurpose.Normal);
        foreach (var purpose in new[] { TexturePurpose.Mask, TexturePurpose.Occlusion })
            if (textures.TryGetValue(purpose, out var file) && File.Exists(file))
                Take(file);

        var anchor = new[] { diffuse, normal }.FirstOrDefault(f => f != null && File.Exists(f));
        if (anchor != null && Path.GetDirectoryName(anchor) is { } folder)
        {
            List<string> files;
            try
            {
                files = Directory.EnumerateFiles(folder).Where(f => ImageExtensions.Contains(Path.GetExtension(f))).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                files = new List<string>();
            }
            var sets = TexturePurposes.SetNames(files.Append(anchor));
            purposes = TexturePurposes.FromFileNames(files);
            var set = sets.GetValueOrDefault(anchor);
            foreach (var file in files.Where(f => set != null && string.Equals(sets.GetValueOrDefault(f), set, StringComparison.OrdinalIgnoreCase))
                                      .Where(f => !string.Equals(f, diffuse, StringComparison.OrdinalIgnoreCase) && !string.Equals(f, normal, StringComparison.OrdinalIgnoreCase))
                                      .OrderBy(f => Path.GetFileName(f).Length))
                Take(file);
        }
        return new SetMaps(opacity, roughness, metallic, occlusion);
    }

    /// <summary>
    /// Puts the maps into a fitted gear material (character.shpk) the way the game reads them: the opacity into
    /// its normal's blue (Translucent when it is mostly see-through), and a mask made from the others. A mask
    /// the stash gave from another game with nothing to make one from is left white: it would not read right.
    /// </summary>
    public void ApplyTo(MaterialSetup material)
    {
        if (material.ShaderType != ShaderType.Character)
            return;

        var normal = material.Textures.FirstOrDefault(t => t.Type == TextureType.Normal);
        if (Opacity != null && normal is { UseWhiteDummy: false, UseVariants: false } && !TextureSources.IsTex(normal.SourcePath))
        {
            normal.UseAlphaOverlay = true;
            normal.AlphaSourcePath = Opacity;
            material.Translucent = GameNormal.MostlySheer(Opacity);
        }

        var mask = material.Textures.FirstOrDefault(t => t.Type == TextureType.Mask);
        if (mask == null)
            return;
        if (ForMask)
        {
            mask.UseMadeMask         = true;
            mask.UseWhiteDummy       = false;
            mask.UseVariants         = false;
            mask.SourcePath          = Roughness ?? string.Empty;
            mask.MetallicSourcePath  = Metallic ?? string.Empty;
            mask.OcclusionSourcePath = Occlusion ?? string.Empty;
        }
        else if (!mask.UseWhiteDummy && !TextureSources.IsTex(mask.SourcePath))
        {
            mask.SourcePath    = string.Empty;
            mask.UseWhiteDummy = true;
        }
    }
}
