using System;
using System.IO;
using System.Linq;
using InstantEdit.Services.Previews;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Services.Sims;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace InstantEdit.Studio.Services;

/// <summary>
/// A hair normal made from a base normal map and an alpha map: the base's colours, with the alpha
/// map (black where there is no hair, white where there is) as its alpha. The result is a PNG kept
/// next to the converted textures, named after both sources as they are now, so it is made once
/// and converted like any other image; editing either source makes a new one.
/// </summary>
internal static class AlphaNormal
{
    /// <summary>How many made normals are kept; older ones are deleted when a new one is made.</summary>
    private const int Keep = 24;

    /// <summary>Bumped when the way they are made changes, so old results are not reused.</summary>
    private const string Version = "2";   // 1 saved RGB bases without their alpha

    /// <summary>The image a slot is converted from: its own file, or, with an alpha overlay, the combined image (made if needed). Null with why.</summary>
    public static string? SourceOf(TextureSlot tex, string? cacheDirectory, out string error)
    {
        error = string.Empty;
        if (!tex.UseAlphaOverlay)
            return tex.SourcePath;

        if (string.IsNullOrWhiteSpace(tex.SourcePath) || !File.Exists(tex.SourcePath))
        {
            error = $"base normal not found ({tex.SourcePath})";
            return null;
        }
        if (string.IsNullOrWhiteSpace(tex.AlphaSourcePath) || !File.Exists(tex.AlphaSourcePath))
        {
            error = string.IsNullOrWhiteSpace(tex.AlphaSourcePath) ? "no alpha map set" : $"alpha map not found ({tex.AlphaSourcePath})";
            return null;
        }

        var folder = Path.Combine(string.IsNullOrWhiteSpace(cacheDirectory) ? Path.GetTempPath() : cacheDirectory, "alpha-normals");
        var key = TextureCache.KeyForGenerated(TextureCache.KeyFor(tex.SourcePath), TextureCache.KeyFor(tex.AlphaSourcePath), Version);
        var path = Path.Combine(folder, $"{key}.png");
        if (File.Exists(path))
            return path;

        try
        {
            Directory.CreateDirectory(folder);
            using var image = Combine(tex.SourcePath, tex.AlphaSourcePath);
            var temp = path + ".part";
            image.SaveAsPng(temp);
            File.Move(temp, path, overwrite: true);
            Prune(folder);
            return path;
        }
        catch (Exception ex)
        {
            error = $"could not put the alpha map into the normal: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// The base normal with the alpha map as its alpha. The two are brought to the larger size of the
    /// two first. The alpha map's brightness is the alpha; any transparency of its own darkens it, so
    /// an alpha map saved as white on transparent works as well as white on black.
    /// </summary>
    public static Image<Rgba32> Combine(string basePath, string alphaPath)
    {
        var image = Load(basePath);
        try
        {
            using var alpha = Load(alphaPath);
            int width  = Math.Max(image.Width, alpha.Width);
            int height = Math.Max(image.Height, alpha.Height);
            if (image.Width != width || image.Height != height)
                image.Mutate(x => x.Resize(width, height, KnownResamplers.Bicubic));
            if (alpha.Width != width || alpha.Height != height)
                alpha.Mutate(x => x.Resize(width, height, KnownResamplers.Bicubic));

            image.ProcessPixelRows(alpha, (dst, src) =>
            {
                for (int y = 0; y < dst.Height; y++)
                {
                    var d = dst.GetRowSpan(y);
                    var s = src.GetRowSpan(y);
                    for (int x = 0; x < d.Length; x++)
                    {
                        var p = s[x];
                        float luma = (0.299f * p.R + 0.587f * p.G + 0.114f * p.B) * p.A / 255f;
                        d[x].A = (byte)Math.Clamp((int)MathF.Round(luma), 0, 255);
                    }
                }
            });

            // A PNG is saved with the colour type it was read with, and a base normal is usually
            // plain RGB: without this the alpha just put in would be dropped on saving.
            image.Metadata.GetPngMetadata().ColorType = PngColorType.RgbWithAlpha;
            image.Metadata.GetPngMetadata().BitDepth  = PngBitDepth.Bit8;
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    /// <summary>Any image a slot takes, at full size: png, jpeg, tga, dds, or the game's .tex.</summary>
    private static Image<Rgba32> Load(string path)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".tex":
            case ".atex":
            {
                var decoded = TextureDecoder.Decode(File.ReadAllBytes(path), int.MaxValue);
                using var bgra = Image.LoadPixelData<Bgra32>(decoded.Bgra, decoded.Width, decoded.Height);
                return bgra.CloneAs<Rgba32>();
            }
            case ".dds":
                return SimsTextureDecoder.Decode(SimsResourceType.ImgDds, File.ReadAllBytes(path)).ToImage();
            default:
                return Image.Load<Rgba32>(StudioImages.Decoder, path);
        }
    }

    private static void Prune(string folder)
    {
        try
        {
            foreach (var old in new DirectoryInfo(folder).GetFiles("*.png").OrderByDescending(f => f.LastWriteTimeUtc).Skip(Keep))
                old.Delete();
        }
        catch (Exception)
        {
            // Kept a little longer, at worst.
        }
    }
}
