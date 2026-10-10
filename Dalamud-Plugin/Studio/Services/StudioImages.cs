using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Tga;
using SixLabors.ImageSharp.Formats.Webp;

namespace InstantEdit.Studio.Services;

/// <summary>
/// How the Mod Browser reads images: your own files and the pictures of downloaded mods. Only the
/// formats it offers are decoded (no TIFF), and their metadata, ICC profiles included, is skipped,
/// so the decoder paths of ImageSharp 3.1's known advisories are never reached (see the csproj).
/// </summary>
internal static class StudioImages
{
    public static readonly DecoderOptions Decoder = new()
    {
        Configuration = new SixLabors.ImageSharp.Configuration(
            new PngConfigurationModule(),
            new JpegConfigurationModule(),
            new TgaConfigurationModule(),
            new BmpConfigurationModule(),
            new GifConfigurationModule(),
            new WebpConfigurationModule()),
        SkipMetadata = true,
    };
}
