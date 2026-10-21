using InstantEdit.Studio.Models;
using InstantEdit.Studio.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// The build window's Add alpha source for hair normals: a base normal with an alpha map put into
/// its alpha, made once per pair of sources and remade when either changes.
/// </summary>
internal static class AlphaNormalScenarios
{
    public static void Run(string testRoot)
    {
        var root = Path.Combine(testRoot, "AlphaNormals");
        Directory.CreateDirectory(root);
        var cache = Path.Combine(root, "cache");

        // A flat 4×4 normal, and an 8×8 alpha map: black left half, white right half.
        var normalPath = Path.Combine(root, "normal.png");
        using (var normal = new Image<Rgba32>(4, 4, new Rgba32(128, 128, 255, 255)))
            normal.SaveAsPng(normalPath);
        var alphaPath = Path.Combine(root, "alpha.png");
        using (var alpha = new Image<Rgba32>(8, 8, new Rgba32(0, 0, 0, 255)))
        {
            for (int y = 0; y < 8; y++)
                for (int x = 4; x < 8; x++)
                    alpha[x, y] = new Rgba32(255, 255, 255, 255);
            alpha.SaveAsPng(alphaPath);
        }

        var tex = new TextureSlot { Type = TextureType.Normal, SourcePath = normalPath };
        Require(AlphaNormal.SourceOf(tex, cache, out _) == normalPath, "alpha normal: a slot without the overlay converts its own file");

        tex.UseAlphaOverlay = true;
        Require(AlphaNormal.SourceOf(tex, cache, out var noAlpha) == null && noAlpha.Contains("no alpha map"),
            "alpha normal: the overlay without an alpha map fails with why");

        tex.AlphaSourcePath = alphaPath;
        var made = AlphaNormal.SourceOf(tex, cache, out var error);
        Require(made != null && File.Exists(made), $"alpha normal: the combined image is made ({error})");
        using (var result = Image.Load<Rgba32>(made!))
        {
            Require(result.Width == 8 && result.Height == 8, "alpha normal: the result takes the larger size of the two");
            Require(result[1, 3].A == 0 && result[6, 3].A == 255, "alpha normal: black is no hair, white is hair");
            Require(Math.Abs(result[6, 3].R - 128) <= 2 && Math.Abs(result[6, 3].G - 128) <= 2 && result[6, 3].B == 255,
                "alpha normal: the base normal's colours are kept");
        }

        var stamp = File.GetLastWriteTimeUtc(made!);
        Require(AlphaNormal.SourceOf(tex, cache, out _) == made && File.GetLastWriteTimeUtc(made!) == stamp,
            "alpha normal: the same sources reuse the image made before, untouched");

        // White on transparent works like white on black.
        using (var alpha = new Image<Rgba32>(8, 8, new Rgba32(255, 255, 255, 0)))
        {
            alpha[2, 2] = new Rgba32(255, 255, 255, 255);
            alpha.SaveAsPng(alphaPath);
        }
        File.SetLastWriteTimeUtc(alphaPath, DateTime.UtcNow.AddMinutes(1));
        var remade = AlphaNormal.SourceOf(tex, cache, out _);
        Require(remade != null && remade != made, "alpha normal: an edited alpha map makes a new image");
        using (var result = Image.Load<Rgba32>(remade!))
            Require(result[2, 2].A == 255 && result[5, 5].A == 0, "alpha normal: an alpha map's own transparency counts as black");
    }
}
