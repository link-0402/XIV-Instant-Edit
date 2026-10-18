using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace InstantEdit.Studio.Services;

/// <summary>
/// The local files a texture can be made from, for builds and updates alike: images, which are
/// converted, and the game's own format (.tex, .atex), which is taken as it is.
/// </summary>
internal static class TextureSources
{
    /// <summary>Source files a texture slot, variant folder or replacement can be made from.</summary>
    public static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".dds", ".tga", ".tex" };

    /// <summary>Whether a source is already in the game's texture format.</summary>
    public static bool IsTex(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".tex" or ".atex";
    }

    /// <summary>Whether neither converter reads the file itself, so it is decoded to a PNG first (see <see cref="DecodeToPng"/>).</summary>
    public static bool NeedsDecoding(string path)
        => Path.GetExtension(path).Equals(".tga", StringComparison.OrdinalIgnoreCase);

    /// <summary>The images in a variant folder, in name order.</summary>
    public static IEnumerable<string> EnumerateImages(string folder)
        => Directory.EnumerateFiles(folder)
            .Where(f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The images a slot of variants is made from: its picked files, or, for a set-up from before
    /// files could be picked, the images of its folder. Empty when there are none or the folder can't be read.
    /// </summary>
    public static List<string> VariantImages(Models.TextureSlot tex)
    {
        if (tex.VariantPaths.Count > 0)
            return tex.VariantPaths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        try
        {
            return !string.IsNullOrWhiteSpace(tex.SourcePath) && Directory.Exists(tex.SourcePath)
                ? EnumerateImages(tex.SourcePath).ToList()
                : new List<string>();
        }
        catch (Exception)
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// Adds image files to a slot's variants, and the images of any folders among them, skipping
    /// files already there and files named like one already there — each name becomes an option.
    /// A slot still reading a folder gets that folder's images as its list first. Returns how many were added.
    /// </summary>
    public static int AddVariantImages(Models.TextureSlot tex, IEnumerable<string> paths)
    {
        if (tex.VariantPaths.Count == 0 && !string.IsNullOrWhiteSpace(tex.SourcePath) && Directory.Exists(tex.SourcePath))
        {
            tex.VariantPaths.AddRange(VariantImages(tex));
            tex.SourcePath = string.Empty;   // a folder is no file for File mode either
        }

        int added = 0;
        foreach (var path in paths)
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.Exists(path) ? EnumerateImages(path).ToList()
                      : ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()) ? new[] { path }
                      : Array.Empty<string>();
            }
            catch (Exception)
            {
                continue;
            }
            foreach (var file in files)
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (tex.VariantPaths.Any(p => string.Equals(Path.GetFileNameWithoutExtension(p), name, StringComparison.OrdinalIgnoreCase)))
                    continue;
                tex.VariantPaths.Add(file);
                added++;
            }
        }
        return added;
    }

    /// <summary>Decodes an image ImageSharp reads (TGA) into a temporary PNG, or null with why. The caller deletes the file.</summary>
    public static string? DecodeToPng(string sourcePath, out string error)
    {
        try
        {
            using var image = Image.Load<Rgba32>(StudioImages.Decoder, sourcePath);
            var path = Path.Combine(Path.GetTempPath(), $"instant-edit-{Guid.NewGuid():N}.png");
            image.SaveAsPng(path);
            error = string.Empty;
            return path;
        }
        catch (Exception ex)
        {
            error = $"Could not load image: {ex.Message}";
            return null;
        }
    }
}
