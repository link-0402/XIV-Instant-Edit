using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using InstantEdit.Studio.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace InstantEdit.Studio.Services;

/// <summary>
/// Which way a normal map's green points. The game's normals point it up (OpenGL; all 66 vanilla
/// gear normals sampled agree), Unreal and some exporters down (DirectX). It is read from the image:
/// with the right direction a normal's slopes fit together into one surface, so the curl of the
/// slope field is near zero, and with the wrong one it is not. Where the image is too flat or too
/// noisy to tell, the file name decides ("_DirectX", "_DX", "_OpenGL", "_GL"), else it is kept.
/// A normal taken as DirectX is written with its green flipped, cached like <see cref="AlphaNormal"/>.
/// </summary>
internal static class NormalGreen
{
    public enum Direction { Unsure, OpenGl, DirectX }

    /// <summary>What was read from an image, how sure (one curl over the other), and from the name.</summary>
    public sealed record Reading(Direction Image, float Margin, Direction Name);

    /// <summary>One direction must fit this much better than the other to be believed; baked maps sit at 1.2 to 2.5.</summary>
    public const float SureMargin = 1.2f;

    private const string Version = "1";

    private static readonly ConcurrentDictionary<string, Task<Reading>> Readings = new();

    /// <summary>
    /// Whether a slot's file is written with its green flipped, and why, in a few words. Only normal
    /// slots in File mode with an image file are looked at; a .tex is used as it is.
    /// </summary>
    public static bool Flips(TextureSlot tex, Reading? reading, out string why)
    {
        why = string.Empty;
        if (!Applies(tex))
            return false;
        switch (tex.GreenDirection)
        {
            case NormalGreenMode.OpenGl:  why = "OpenGL, set by hand";            return false;
            case NormalGreenMode.DirectX: why = "DirectX, set by hand: green flipped"; return true;
        }
        if (reading == null)
        {
            why = "checking";
            return false;
        }
        var (direction, from) = reading.Image != Direction.Unsure ? (reading.Image, $"read from the image, {reading.Margin:0.0}x")
                              : reading.Name  != Direction.Unsure ? (reading.Name, "unclear in the image, from the file name")
                              : (Direction.Unsure, "unclear in the image, kept as it is");
        why = direction switch
        {
            Direction.DirectX => $"DirectX ({from}): green flipped",
            Direction.OpenGl  => $"OpenGL ({from})",
            _                 => from,
        };
        return direction == Direction.DirectX;
    }

    public static bool Applies(TextureSlot tex)
        => tex.Type == TextureType.Normal && !tex.UseVariants && !tex.UseWhiteDummy && !tex.UseAlphaOverlay && !tex.UseDiffuseNormal
           && !string.IsNullOrWhiteSpace(tex.SourcePath) && !TextureSources.IsTex(tex.SourcePath);

    /// <summary>The reading of a file if it is done, starting it in the background if not (for the UI).</summary>
    public static Reading? Peek(string path)
    {
        var task = Readings.GetOrAdd(TextureCache.KeyFor(path), _ => Task.Run(() => Read(path)));
        return task.IsCompletedSuccessfully ? task.Result : null;
    }

    /// <summary>The reading of a file, waiting for it (for builds).</summary>
    public static Reading ReadCached(string path)
        => Readings.GetOrAdd(TextureCache.KeyFor(path), _ => Task.Run(() => Read(path))).GetAwaiter().GetResult();

    /// <summary>The image a File normal slot converts from: its file, or its file with the green flipped (made if needed).</summary>
    public static string? SourceOf(TextureSlot tex, string? cacheDirectory, out string error)
    {
        error = string.Empty;
        if (!Applies(tex) || !File.Exists(tex.SourcePath))
            return tex.SourcePath;
        Reading? reading = null;
        if (tex.GreenDirection == NormalGreenMode.Auto)
            reading = ReadCached(tex.SourcePath);
        if (!Flips(tex, reading, out _))
            return tex.SourcePath;

        var folder = Path.Combine(string.IsNullOrWhiteSpace(cacheDirectory) ? Path.GetTempPath() : cacheDirectory, "flipped-normals");
        var path = Path.Combine(folder, $"{TextureCache.KeyForGenerated(TextureCache.KeyFor(tex.SourcePath), Version)}.png");
        if (File.Exists(path))
            return AlphaNormal.Reused(path);
        try
        {
            Directory.CreateDirectory(folder);
            using var image = AlphaNormal.Load(tex.SourcePath);
            image.ProcessPixelRows(rows =>
            {
                for (int y = 0; y < rows.Height; y++)
                    foreach (ref var p in rows.GetRowSpan(y))
                        p.G = (byte)(255 - p.G);
            });
            image.Metadata.GetPngMetadata().ColorType = SixLabors.ImageSharp.Formats.Png.PngColorType.RgbWithAlpha;
            image.Metadata.GetPngMetadata().BitDepth  = SixLabors.ImageSharp.Formats.Png.PngBitDepth.Bit8;
            AlphaNormal.Save(image, path);
            AlphaNormal.Prune(folder);
            return path;
        }
        catch (Exception ex)
        {
            error = $"could not flip the normal's green: {ex.Message}";
            return null;
        }
    }

    /// <summary>Never throws: an image that cannot be read is unclear, and its name still counts.</summary>
    public static Reading Read(string path)
    {
        try
        {
            using var image = AlphaNormal.Load(path);
            return new Reading(Detect(image, out var margin), margin, FromName(path));
        }
        catch (Exception)
        {
            return new Reading(Direction.Unsure, 1, FromName(path));
        }
    }

    /// <summary>The direction an image's slopes fit, with how much better they fit it than the other.</summary>
    public static Direction Detect(Image<Rgba32> image, out float margin)
    {
        int w = image.Width, h = image.Height;
        var pixels = new Rgba32[w * h];
        image.CopyPixelDataTo(pixels);

        // Slope along x, and down the rows if green points up.
        float P(int i) { float nx = pixels[i].R / 127.5f - 1, ny = pixels[i].G / 127.5f - 1; return -nx / Nz(nx, ny); }
        float Q(int i) { float nx = pixels[i].R / 127.5f - 1, ny = pixels[i].G / 127.5f - 1; return ny / Nz(nx, ny); }

        // Every pixel's neighbours count, but about a million pixels are enough to judge by.
        int step = Math.Max(1, (int)MathF.Sqrt(w * (float)h / 1_000_000f));
        var up = new System.Collections.Generic.List<float>();
        var down = new System.Collections.Generic.List<float>();
        for (int y = 1; y < h - 1; y += step)
            for (int x = 1; x < w - 1; x += step)
            {
                float dpdy = P((y + 1) * w + x) - P((y - 1) * w + x);
                float dqdx = Q(y * w + x + 1) - Q(y * w + x - 1);
                if (MathF.Abs(dpdy) + MathF.Abs(dqdx) <= 0.02f)
                    continue;   // flat
                up.Add(MathF.Abs(dpdy - dqdx));
                down.Add(MathF.Abs(dpdy + dqdx));
            }

        margin = 1;
        if (up.Count < 1000)
            return Direction.Unsure;
        float gl = TrimmedMean(up), dx = TrimmedMean(down);
        margin = Math.Max(gl, dx) / Math.Max(Math.Min(gl, dx), 1e-9f);
        return margin < SureMargin ? Direction.Unsure : gl < dx ? Direction.OpenGl : Direction.DirectX;
    }

    private static float Nz(float nx, float ny) => MathF.Sqrt(Math.Clamp(1 - nx * nx - ny * ny, 0.0025f, 1));

    /// <summary>The mean without the worst 5 %, which are UV seams.</summary>
    private static float TrimmedMean(System.Collections.Generic.List<float> values)
    {
        values.Sort();
        int keep = Math.Max(1, (int)(values.Count * 0.95));
        double sum = 0;
        for (int i = 0; i < keep; i++)
            sum += values[i];
        return (float)(sum / keep);
    }

    public static Direction FromName(string path)
    {
        var words = Path.GetFileNameWithoutExtension(path).ToLowerInvariant()
            .Split(new[] { '_', '-', ' ', '.', '(', ')', '[', ']' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Any(w => w is "directx" or "dx" or "d3d"))
            return Direction.DirectX;
        if (words.Any(w => w is "opengl" or "gl" or "ogl"))
            return Direction.OpenGl;
        return Direction.Unsure;
    }
}
