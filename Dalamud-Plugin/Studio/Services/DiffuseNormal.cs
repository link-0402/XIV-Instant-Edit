using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using InstantEdit.Studio.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace InstantEdit.Studio.Services;

/// <summary>
/// A gear normal made from a diffuse texture, for ports (Sims 4 above all) that ship a diffuse with
/// its shading baked in but no usable normal. Lighter is higher: the diffuse's brightness, with its
/// broad shading and colour taken out, becomes a height whose slopes are the normal (OpenGL, green
/// up, as the Painter round trip writes them). Blue is the diffuse's alpha, the opacity
/// character.shpk reads from the normal. Made once per diffuse and strength, like
/// <see cref="AlphaNormal"/>.
/// </summary>
internal static class DiffuseNormal
{
    private const string Version = "2";

    /// <summary>The made normal for a slot (made if needed), or null with why.</summary>
    public static string? SourceOf(TextureSlot tex, string? cacheDirectory, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(tex.SourcePath) || !File.Exists(tex.SourcePath))
        {
            error = string.IsNullOrWhiteSpace(tex.SourcePath) ? "no diffuse set" : $"diffuse not found ({tex.SourcePath})";
            return null;
        }

        var folder = Path.Combine(string.IsNullOrWhiteSpace(cacheDirectory) ? Path.GetTempPath() : cacheDirectory, "diffuse-normals");
        var strength = tex.DiffuseNormalStrength.ToString("0.00", CultureInfo.InvariantCulture);
        var path = Path.Combine(folder, $"{TextureCache.KeyForGenerated(TextureCache.KeyFor(tex.SourcePath), strength, Version)}.png");
        if (File.Exists(path))
            return AlphaNormal.Reused(path);

        try
        {
            Directory.CreateDirectory(folder);
            using var image = Make(tex.SourcePath, tex.DiffuseNormalStrength);
            AlphaNormal.Save(image, path);
            AlphaNormal.Prune(folder);
            return path;
        }
        catch (Exception ex)
        {
            error = $"could not make a normal from the diffuse: {ex.Message}";
            return null;
        }
    }

    public static Image<Rgba32> Make(string diffusePath, float strength)
    {
        using var diffuse = AlphaNormal.Load(diffusePath);
        int w = diffuse.Width, h = diffuse.Height, n = w * h;
        var height = new float[n];
        var opaque = new float[n];
        var alpha  = new byte[n];
        diffuse.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < h; y++)
            {
                var row = rows.GetRowSpan(y);
                for (int x = 0; x < w; x++)
                {
                    var p = row[x];
                    int i = y * w + x;
                    height[i] = (0.299f * p.R + 0.587f * p.G + 0.114f * p.B) / 255f;
                    opaque[i] = p.A >= 128 ? 1f : 0f;
                    alpha[i]  = p.A;
                }
            }
        });

        // Sizes are tuned on 2048 textures and scale with the image.
        float k = Math.Max(w, h) / 2048f;

        // Transparent pixels take the opaque ones' average around them, so UV island edges stay flat:
        // the nearest first, wider for those further out, the overall average for any left.
        var filled = (float[])opaque.Clone();
        foreach (var sigma in new[] { 2f * k, 8f * k, 32f * k, 128f * k })
        {
            var (fill, cover) = MaskedBlur(height, opaque, w, h, sigma);
            for (int i = 0; i < n; i++)
                if (filled[i] == 0 && cover[i] > 0.05f)
                {
                    height[i] = fill[i];
                    filled[i] = 1;
                }
        }
        double opaqueSum = 0, opaqueCount = 0;
        for (int i = 0; i < n; i++)
            if (opaque[i] != 0) { opaqueSum += height[i]; opaqueCount++; }
        for (int i = 0; i < n; i++)
            if (filled[i] == 0) height[i] = opaqueCount > 0 ? (float)(opaqueSum / opaqueCount) : 0f;

        // Broad shading and colour differences are not bumps.
        var (broad, _) = MaskedBlur(height, opaque, w, h, 48 * k);
        double sum = 0, sumSq = 0, count = 0;
        for (int i = 0; i < n; i++)
        {
            height[i] -= broad[i];
            if (opaque[i] == 0) continue;
            sum += height[i]; sumSq += height[i] * height[i]; count++;
        }
        // The same relief whatever the colour or contrast of the swatch.
        double mean = count > 0 ? sum / count : 0;
        float spread = (float)Math.Sqrt(Math.Max(count > 0 ? sumSq / count - mean * mean : 0, 1e-8));

        // Fine detail and folds, half each.
        var gx = new float[n];
        var gy = new float[n];
        foreach (var sigma in new[] { 1f * k, 4f * k })
        {
            var smooth = Blur(height, w, h, sigma);
            for (int y = 0; y < h; y++)
            {
                int up = Math.Max(y - 1, 0) * w, down = Math.Min(y + 1, h - 1) * w;
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    gx[i] += 0.25f * (smooth[y * w + Math.Min(x + 1, w - 1)] - smooth[y * w + Math.Max(x - 1, 0)]);
                    gy[i] += 0.25f * (smooth[down + x] - smooth[up + x]);
                }
            }
        }

        // A bump spans k times the pixels of a 2048's, so its slope per pixel is 1/k as steep: times k, the same relief at any size.
        float scale = strength * 0.75f * k / spread;
        var image = new Image<Rgba32>(w, h);
        image.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < h; y++)
            {
                var row = rows.GetRowSpan(y);
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    float nx = -gx[i] * scale, ny = gy[i] * scale;   // image rows run down, green points up
                    float len = MathF.Sqrt(nx * nx + ny * ny + 1);
                    row[x] = opaque[i] == 0
                        ? new Rgba32(128, 128, alpha[i], 255)
                        : new Rgba32(ToByte(nx / len), ToByte(ny / len), alpha[i], 255);
                }
            }
        });
        image.Metadata.GetPngMetadata().ColorType = PngColorType.RgbWithAlpha;
        image.Metadata.GetPngMetadata().BitDepth  = PngBitDepth.Bit8;
        return image;
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 127.5f + 127.5f), 0, 255);

    /// <summary>Blur over the opaque pixels only, and how much of each pixel's neighbourhood is opaque.</summary>
    private static (float[] Values, float[] Cover) MaskedBlur(float[] values, float[] mask, int w, int h, float sigma)
    {
        var weighted = new float[values.Length];
        for (int i = 0; i < values.Length; i++)
            weighted[i] = values[i] * mask[i];
        var num = Blur(weighted, w, h, sigma);
        var den = Blur(mask, w, h, sigma);
        for (int i = 0; i < num.Length; i++)
            num[i] = den[i] > 1e-4f ? num[i] / den[i] : 0f;
        return (num, den);
    }

    /// <summary>A Gaussian blur, as three box blurs each way.</summary>
    private static float[] Blur(float[] src, int w, int h, float sigma)
    {
        int radius = Math.Max(1, (int)MathF.Round((MathF.Sqrt(4 * sigma * sigma + 1) - 1) / 2));
        var a = (float[])src.Clone();
        var b = new float[src.Length];
        for (int pass = 0; pass < 3; pass++)
        {
            BoxRows(a, b, w, h, radius);
            BoxColumns(b, a, w, h, radius);
        }
        return a;
    }

    private static void BoxRows(float[] src, float[] dst, int w, int h, int r)
    {
        float inv = 1f / (2 * r + 1);
        Parallel.For(0, h, y =>
        {
            int o = y * w;
            float acc = 0;
            for (int x = -r - 1; x < r; x++)
                acc += src[o + Math.Clamp(x, 0, w - 1)];
            for (int x = 0; x < w; x++)
            {
                acc += src[o + Math.Min(x + r, w - 1)] - src[o + Math.Max(x - r - 1, 0)];
                dst[o + x] = acc * inv;
            }
        });
    }

    private static void BoxColumns(float[] src, float[] dst, int w, int h, int r)
    {
        float inv = 1f / (2 * r + 1);
        Parallel.For(0, w, x =>
        {
            float acc = 0;
            for (int y = -r - 1; y < r; y++)
                acc += src[Math.Clamp(y, 0, h - 1) * w + x];
            for (int y = 0; y < h; y++)
            {
                acc += src[Math.Min(y + r, h - 1) * w + x] - src[Math.Max(y - r - 1, 0) * w + x];
                dst[y * w + x] = acc * inv;
            }
        });
    }
}
