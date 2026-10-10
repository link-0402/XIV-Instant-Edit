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
