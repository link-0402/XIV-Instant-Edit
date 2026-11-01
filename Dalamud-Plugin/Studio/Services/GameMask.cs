using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Services.Stash;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace InstantEdit.Studio.Services;

/// <summary>
/// A Dawntrail gear mask made from another game's roughness, metallic and occlusion maps. The game's
/// own (402 character.shpk masks, 2026-10-31) hold occlusion in red and blue (light, darker in the
/// creases; the two agree on most) and roughness in green, which the colour row's roughness is
/// multiplied by; alpha is unused. Metalness comes from the colour row, never a texture, so a metallic
/// map only stands in for a missing roughness: metal is made glossy, as the Metal placeholder is.
/// Made once per set of maps, like <see cref="AlphaNormal"/>.
/// </summary>
internal static class GameMask
{
    private const string Version = "1";

    /// <summary>Words of a map that holds smoothness rather than roughness, so it is turned around.</summary>
    private static readonly HashSet<string> GlossWords = new(StringComparer.Ordinal) { "gloss", "glossiness", "smooth", "smoothness" };

    /// <summary>The slot's maps that are set, as (what it is, path).</summary>
    public static IReadOnlyList<(string Role, string Path)> Maps(TextureSlot tex)
    {
        var maps = new List<(string, string)>();
        if (!string.IsNullOrWhiteSpace(tex.SourcePath))
            maps.Add((IsGloss(tex.SourcePath) ? "gloss" : "roughness", tex.SourcePath));
        if (!string.IsNullOrWhiteSpace(tex.MetallicSourcePath))
            maps.Add(("metallic", tex.MetallicSourcePath));
        if (!string.IsNullOrWhiteSpace(tex.OcclusionSourcePath))
            maps.Add(("occlusion", tex.OcclusionSourcePath));
        return maps;
    }

    /// <summary>The made mask for a slot (made if needed), or null with why.</summary>
    public static string? SourceOf(TextureSlot tex, string? cacheDirectory, out string error)
    {
        error = string.Empty;
        var maps = Maps(tex);
        if (maps.Count == 0)
        {
            error = "no maps set to make the mask from";
            return null;
        }
        if (maps.FirstOrDefault(m => !File.Exists(m.Path)) is { Path: { } missing })
        {
            error = $"map not found ({missing})";
            return null;
        }

        var folder = Path.Combine(string.IsNullOrWhiteSpace(cacheDirectory) ? Path.GetTempPath() : cacheDirectory, "made-masks");
        var parts = maps.Select(m => (object)$"{m.Role}={TextureCache.KeyFor(m.Path)}").Append(Version).ToArray();
        var path = Path.Combine(folder, $"{TextureCache.KeyForGenerated(parts)}.png");
        if (File.Exists(path))
            return AlphaNormal.Reused(path);
        try
        {
            Directory.CreateDirectory(folder);
            using var image = Make(tex);
            AlphaNormal.Save(image, path);
            AlphaNormal.Prune(folder);
            return path;
        }
        catch (Exception ex)
        {
            error = $"could not make the mask: {ex.Message}";
            return null;
        }
    }

    /// <summary>What the report says a made mask came from: "ASA_Roughness.png, ASA_Mixed_AO.png".</summary>
    public static string Describe(TextureSlot tex) => string.Join(", ", Maps(tex).Select(m => Path.GetFileName(m.Path)));

    public static Image<Rgba32> Make(TextureSlot tex)
        => Make(OrNull(tex.SourcePath), OrNull(tex.MetallicSourcePath), OrNull(tex.OcclusionSourcePath));

    private static string? OrNull(string path) => string.IsNullOrWhiteSpace(path) ? null : path;

    /// <summary>
    /// Green: the roughness map (a gloss map turned around); without one, a metallic map with a smoothness
    /// alpha (Unity's metallic-gloss map) gives it, or plain metal is glossy and the rest rough; else rough.
    /// Red and blue: the occlusion map, or white without one (or with one that is all but black, which
    /// some exporters write for a part they baked nothing on).
    /// </summary>
    public static Image<Rgba32> Make(string? roughnessPath, string? metallicPath, string? occlusionPath)
    {
        using var roughness = roughnessPath == null ? null : AlphaNormal.Load(roughnessPath);
        using var metallic  = metallicPath == null ? null : AlphaNormal.Load(metallicPath);
        using var occlusion = occlusionPath == null ? null : AlphaNormal.Load(occlusionPath);
        var images = new[] { roughness, metallic, occlusion }.OfType<Image<Rgba32>>().ToList();
        if (images.Count == 0)
            throw new ArgumentException("no maps to make the mask from");

        int width = images.Max(i => i.Width), height = images.Max(i => i.Height);
        foreach (var image in images.Where(i => i.Width != width || i.Height != height))
            image.Mutate(x => x.Resize(width, height, KnownResamplers.Bicubic));

        bool gloss = roughnessPath != null && IsGloss(roughnessPath);
        bool smoothnessInAlpha = roughness == null && metallic != null && AlphaVaries(metallic);
        bool useOcclusion = occlusion != null && !AllButBlack(occlusion);

        var r = Pixels(roughness);
        var m = Pixels(metallic);
        var o = useOcclusion ? Pixels(occlusion) : null;
        var pixels = new Rgba32[width * height];
        for (int i = 0; i < pixels.Length; i++)
        {
            byte green = r != null ? (byte)(gloss ? 255 - Gray(r[i]) : Gray(r[i]))
                       : m != null ? (smoothnessInAlpha ? (byte)(255 - m[i].A) : Gray(m[i]) >= 128 ? (byte)0 : (byte)255)
                       : (byte)255;
            byte ao = o != null ? Gray(o[i]) : (byte)255;
            pixels[i] = new Rgba32(ao, green, ao, 255);
        }
        var mask = Image.LoadPixelData<Rgba32>(pixels, width, height);
        mask.Metadata.GetPngMetadata().ColorType = PngColorType.RgbWithAlpha;
        mask.Metadata.GetPngMetadata().BitDepth  = PngBitDepth.Bit8;
        return mask;
    }

    /// <summary>A map named for gloss or smoothness ("Body_Glossiness", "MetallicSmoothness").</summary>
    public static bool IsGloss(string path) => TexturePurposes.Words(Path.GetFileNameWithoutExtension(path)).Any(GlossWords.Contains);

    private static byte Gray(Rgba32 p) => (byte)Math.Clamp((int)MathF.Round(0.299f * p.R + 0.587f * p.G + 0.114f * p.B), 0, 255);

    private static bool AlphaVaries(Image<Rgba32> image)
    {
        byte min = 255, max = 0;
        Sample(image, p => { min = Math.Min(min, p.A); max = Math.Max(max, p.A); });
        return max - min > 8;
    }

    /// <summary>Whether 99% of an image sits under 16 of 255.</summary>
    private static bool AllButBlack(Image<Rgba32> image)
    {
        long total = 0, dark = 0;
        Sample(image, p => { total++; if (Gray(p) < 16) dark++; });
        return dark * 100 >= total * 99;
    }

    private static Rgba32[]? Pixels(Image<Rgba32>? image)
    {
        if (image == null)
            return null;
        var pixels = new Rgba32[image.Width * image.Height];
        image.CopyPixelDataTo(pixels);
        return pixels;
    }

    /// <summary>About 250,000 of an image's pixels, evenly spread.</summary>
    private static void Sample(Image<Rgba32> image, Action<Rgba32> look)
    {
        int step = Math.Max(1, (int)MathF.Sqrt(image.Width * (float)image.Height / 250_000f));
        image.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < rows.Height; y += step)
            {
                var row = rows.GetRowSpan(y);
                for (int x = 0; x < row.Length; x += step)
                    look(row[x]);
            }
        });
    }
}
