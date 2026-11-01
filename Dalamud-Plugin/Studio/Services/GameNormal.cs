using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using InstantEdit.Studio.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace InstantEdit.Studio.Services;

/// <summary>
/// A gear normal's blue is its opacity: character.shpk cuts off what is under g_AlphaThreshold (blends
/// it, with the Translucent flag). Another game's normal keeps the surface's Z there, which drops on
/// every slope, so in game it gets holes. Such a normal is written with a solid blue, or with an
/// opacity map in it; one whose blue already is an opacity (made for the game) is kept as it is.
/// Made images are cached like <see cref="AlphaNormal"/>'s.
/// </summary>
internal static class GameNormal
{
    private const string Version = "1";

    /// <summary>Whether a material of this shader reads its normal's blue as opacity.</summary>
    public static bool ReadsBlueAsOpacity(ShaderType? shader) => shader is ShaderType.Character or ShaderType.CharacterLegacy;

    private static readonly ConcurrentDictionary<string, Task<bool>> Carries = new();
    private static readonly ConcurrentDictionary<string, Task<bool>> Sheer = new();

    /// <summary>Whether a normal file's blue holds an opacity if that is read yet, starting the read in the background if not (for the UI).</summary>
    public static bool? PeekCarriesOpacity(string path)
    {
        var task = Carries.GetOrAdd(TextureCache.KeyFor(path), _ => Task.Run(() => CarriesOpacity(path)));
        return task.IsCompletedSuccessfully ? task.Result : null;
    }

    /// <summary>
    /// The normal at <paramref name="path"/> with a solid blue, made if needed, or the path itself when its
    /// blue already holds an opacity. <paramref name="changed"/> says which. Null with why when it can't be read.
    /// </summary>
    public static string? Solid(string path, string? cacheDirectory, out bool changed, out string error)
    {
        changed = false;
        error = string.Empty;
        try
        {
            if (Carries.GetOrAdd(TextureCache.KeyFor(path), _ => Task.Run(() => CarriesOpacity(path))).GetAwaiter().GetResult())
                return path;

            var folder = Path.Combine(string.IsNullOrWhiteSpace(cacheDirectory) ? Path.GetTempPath() : cacheDirectory, "game-normals");
            var made = Path.Combine(folder, $"{TextureCache.KeyForGenerated(TextureCache.KeyFor(path), "solid", Version)}.png");
            changed = true;
            if (File.Exists(made))
                return AlphaNormal.Reused(made);
            Directory.CreateDirectory(folder);
            using var image = AlphaNormal.Load(path);
            image.ProcessPixelRows(rows =>
            {
                for (int y = 0; y < rows.Height; y++)
                    foreach (ref var p in rows.GetRowSpan(y))
                        p.B = 255;
            });
            KeepAlpha(image);
            AlphaNormal.Save(image, made);
            AlphaNormal.Prune(folder);
            return made;
        }
        catch (Exception ex)
        {
            error = $"could not make the normal's blue solid: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// The normal with an opacity map in its blue, made if needed: the map's brightness (darkened by any
    /// transparency of its own) times <paramref name="scale"/>, which is the alpha cutoff for a Translucent
    /// material (it divides the blue by it) and 1 otherwise. Null with why.
    /// </summary>
    public static string? WithOpacity(string normalPath, string opacityPath, float scale, string? cacheDirectory, out string error)
    {
        error = string.Empty;
        var folder = Path.Combine(string.IsNullOrWhiteSpace(cacheDirectory) ? Path.GetTempPath() : cacheDirectory, "game-normals");
        var made = Path.Combine(folder, $"{TextureCache.KeyForGenerated(TextureCache.KeyFor(normalPath), TextureCache.KeyFor(opacityPath), $"{scale:R}", Version)}.png");
        if (File.Exists(made))
            return AlphaNormal.Reused(made);
        try
        {
            Directory.CreateDirectory(folder);
            using var image = Combine(normalPath, opacityPath, scale);
            AlphaNormal.Save(image, made);
            AlphaNormal.Prune(folder);
            return made;
        }
        catch (Exception ex)
        {
            error = $"could not put the opacity map into the normal: {ex.Message}";
            return null;
        }
    }

    /// <summary>The normal with the opacity map in its blue, both at the larger size of the two.</summary>
    public static Image<Rgba32> Combine(string normalPath, string opacityPath, float scale)
    {
        var image = AlphaNormal.Load(normalPath);
        try
        {
            using var opacity = AlphaNormal.Load(opacityPath);
            int width  = Math.Max(image.Width, opacity.Width);
            int height = Math.Max(image.Height, opacity.Height);
            if (image.Width != width || image.Height != height)
                image.Mutate(x => x.Resize(width, height, KnownResamplers.Bicubic));
            if (opacity.Width != width || opacity.Height != height)
                opacity.Mutate(x => x.Resize(width, height, KnownResamplers.Bicubic));

            image.ProcessPixelRows(opacity, (dst, src) =>
            {
                for (int y = 0; y < dst.Height; y++)
                {
                    var d = dst.GetRowSpan(y);
                    var s = src.GetRowSpan(y);
                    for (int x = 0; x < d.Length; x++)
                        d[x].B = (byte)Math.Clamp((int)MathF.Round(Luma(s[x]) * scale), 0, 255);
                }
            });
            KeepAlpha(image);
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Whether a normal's blue holds an opacity rather than the surface's Z. A normal saved by another
    /// game keeps blue at the Z its red and green give (within a few levels); a game normal is solid
    /// (255) where it is opaque, above that Z on every slope, and dark where it is see-through. A blue
    /// that is one dark value is an empty channel (a two-channel normal), not an opacity.
    /// </summary>
    public static bool CarriesOpacity(string path)
    {
        using var image = AlphaNormal.Load(path);
        return CarriesOpacity(image);
    }

    public static bool CarriesOpacity(Image<Rgba32> image)
    {
        int w = image.Width, h = image.Height;
        int step = Math.Max(1, (int)MathF.Sqrt(w * (float)h / 1_000_000f));
        var histogram = new int[256];
        long total = 0, sloped = 0, saturated = 0, below = 0, holes = 0;
        image.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < h; y += step)
            {
                var row = rows.GetRowSpan(y);
                for (int x = 0; x < w; x += step)
                {
                    var p = row[x];
                    total++;
                    histogram[p.B]++;
                    if (p.B < 32)
                        holes++;
                    float nx = p.R / 127.5f - 1, ny = p.G / 127.5f - 1;
                    float z = MathF.Sqrt(Math.Max(0, 1 - nx * nx - ny * ny)) * 127.5f + 127.5f;
                    if (p.B <= z - 48)
                        below++;   // far under the surface's Z: see-through, or nothing a plain normal holds
                    else if (z < 240)
                    {
                        sloped++;
                        if (p.B >= z + 12)
                            saturated++;
                    }
                }
            }
        });

        int low = Percentile(histogram, total, 0.01), high = Percentile(histogram, total, 0.99);
        if (high - low <= 8 && high < 128)
            return false;   // an empty blue
        // A plain normal's blue all but never drops under 32 (a thousandth of a percent of a Substance
        // export's); a game normal's cut-outs sit at 0, and older ones keep Z everywhere else (2026-10-31).
        if (below > total / 100 || holes > total / 2000)
            return true;
        if (sloped < 500)
            return below > total / 10000;   // too flat to read the slopes: only holes tell
        return saturated > sloped / 2;
    }

    /// <summary>
    /// Whether most of what an opacity map shows is see-through: more than half of the pixels that are
    /// not fully gone sit under 50%. Such a part vanishes at the preset's cutoff, so it is written Translucent.
    /// </summary>
    public static bool MostlySheer(string opacityPath)
        => Sheer.GetOrAdd(TextureCache.KeyFor(opacityPath), _ => Task.Run(() =>
        {
            try
            {
                using var image = AlphaNormal.Load(opacityPath);
                int w = image.Width, h = image.Height;
                int step = Math.Max(1, (int)MathF.Sqrt(w * (float)h / 250_000f));
                long shown = 0, sheer = 0;
                image.ProcessPixelRows(rows =>
                {
                    for (int y = 0; y < h; y += step)
                    {
                        var row = rows.GetRowSpan(y);
                        for (int x = 0; x < w; x += step)
                        {
                            var o = Luma(row[x]);
                            if (o <= 5) continue;
                            shown++;
                            if (o < 128) sheer++;
                        }
                    }
                });
                return shown > 0 && sheer * 2 > shown;
            }
            catch (Exception)
            {
                return false;   // an opacity map that can't be read is cut off, as the preset does
            }
        })).GetAwaiter().GetResult();

    /// <summary>A pixel's brightness, darkened by its own transparency (white on transparent reads as white on black).</summary>
    private static float Luma(Rgba32 p) => (0.299f * p.R + 0.587f * p.G + 0.114f * p.B) * p.A / 255f;

    private static int Percentile(int[] histogram, long total, double q)
    {
        long limit = (long)(q * total), seen = 0;
        for (int v = 0; v < 256; v++)
        {
            seen += histogram[v];
            if (seen > limit)
                return v;
        }
        return 255;
    }

    /// <summary>A PNG is saved with the colour type it was read with: without this, the alpha of an RGB normal would be dropped.</summary>
    private static void KeepAlpha(Image<Rgba32> image)
    {
        image.Metadata.GetPngMetadata().ColorType = PngColorType.RgbWithAlpha;
        image.Metadata.GetPngMetadata().BitDepth  = PngBitDepth.Bit8;
    }
}
