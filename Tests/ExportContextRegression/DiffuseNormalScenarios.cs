using InstantEdit.Studio.Models;
using InstantEdit.Studio.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// The build window's From diffuse source for gear normals: a normal made from a diffuse's shading,
/// green up, with the diffuse's alpha as the opacity in blue.
/// </summary>
internal static class DiffuseNormalScenarios
{
    public static void Run(string testRoot)
    {
        var root = Path.Combine(testRoot, "DiffuseNormals");
        Directory.CreateDirectory(root);
        var cache = Path.Combine(root, "cache");

        // A 256×256 diffuse: transparent on the left quarter, a flat grey island elsewhere with a
        // bright disk in it at (160,128).
        var diffusePath = Path.Combine(root, "diffuse.png");
        using (var diffuse = new Image<Rgba32>(256, 256))
        {
            for (int y = 0; y < 256; y++)
                for (int x = 0; x < 256; x++)
                {
                    bool disk = (x - 160) * (x - 160) + (y - 128) * (y - 128) < 20 * 20;
                    diffuse[x, y] = x < 64 ? new Rgba32(255, 255, 255, 0)
                                  : disk   ? new Rgba32(230, 230, 230, 255)
                                  :          new Rgba32(60, 60, 60, 255);
                }
            diffuse.SaveAsPng(diffusePath);
        }

        var tex = new TextureSlot { Type = TextureType.Normal, SourcePath = diffusePath, UseDiffuseNormal = true };
        var made = AlphaNormal.SourceOf(tex, cache, out var error);
        Require(made != null && made != diffusePath && File.Exists(made), $"diffuse normal: the normal is made ({error})");
        using (var n = Image.Load<Rgba32>(made!))
        {
            Require(n.Width == 256 && n.Height == 256, "diffuse normal: the normal keeps the diffuse's size");
            // A 20-pixel disk on a 256 texture is a broad bump (160 pixels at 2048), so its edges lean only a little.
            Require(n[160, 106].G > 130, $"diffuse normal: the top edge of a bright bump points up, green over 128 ({n[160, 106].G})");
            Require(n[182, 128].R > 130, $"diffuse normal: the right edge of a bright bump points right, red over 128 ({n[182, 128].R})");
            Require(n[160, 150].G < 126 && n[138, 128].R < 126, $"diffuse normal: the bottom and left edges point the other way ({n[160, 150].G},{n[138, 128].R})");
            Require(n[20, 20].B == 0 && n[200, 20].B == 255, "diffuse normal: blue is the diffuse's alpha, the opacity");
            Require(n[20, 20].R == 128 && n[20, 20].G == 128, "diffuse normal: transparent pixels are flat");
            Require(Math.Abs(n[64, 40].R - 128) <= 3 && Math.Abs(n[64, 40].G - 128) <= 3,
                $"diffuse normal: a flat island stays flat at its edge ({n[64, 40].R},{n[64, 40].G})");
        }

        var stamp = File.GetLastWriteTimeUtc(made!);
        Require(AlphaNormal.SourceOf(tex, cache, out _) == made && File.GetLastWriteTimeUtc(made!) == stamp,
            "diffuse normal: the same diffuse and strength reuse the normal made before");

        // Reuse marks the normal as used by its access time, so pruning keeps it although 30 others
        // were written after it, and its write time (which builds key on) stays.
        var folder = Path.GetDirectoryName(made!)!;
        var longAgo = DateTime.UtcNow.AddDays(-2);
        for (int i = 0; i < 30; i++)
        {
            var other = Path.Combine(folder, $"other{i}.png");
            File.WriteAllBytes(other, new byte[] { 1 });
            File.SetLastAccessTimeUtc(other, longAgo);
        }
        File.SetLastAccessTimeUtc(made!, longAgo.AddDays(-1));
        Require(AlphaNormal.SourceOf(tex, cache, out _) == made && File.GetLastAccessTimeUtc(made!) > longAgo,
            "diffuse normal: a reused normal is marked as used");
        AlphaNormal.Prune(folder);
        Require(File.Exists(made!) && File.GetLastWriteTimeUtc(made!) == stamp && Directory.GetFiles(folder, "*.png").Length == 24,
            "diffuse normal: pruning keeps the normal in use, with its write time");
        Require(Directory.GetFiles(folder, "*.part").Length == 0, "diffuse normal: no temp files are left behind");

        tex.DiffuseNormalStrength = 2f;
        var stronger = AlphaNormal.SourceOf(tex, cache, out _);
        Require(stronger != null && stronger != made, "diffuse normal: another strength makes a new normal");
        using (var a = Image.Load<Rgba32>(made!))
        using (var b = Image.Load<Rgba32>(stronger!))
            Require(b[182, 128].R > a[182, 128].R, "diffuse normal: a higher strength makes deeper bumps");

        // The same diffuse at 1024 and at 2048 makes the same bumps: the game reads a normal's directions, not its pixels.
        float Relief(int size)
        {
            var path = Path.Combine(root, $"waves{size}.png");
            using (var waves = new Image<Rgba32>(size, size))
            {
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        var v = (byte)(128 + 60 * Math.Sin(2 * Math.PI * 8 * x / size) * Math.Sin(2 * Math.PI * 8 * y / size));
                        waves[x, y] = new Rgba32(v, v, v, 255);
                    }
                waves.SaveAsPng(path);
            }
            using var normal = DiffuseNormal.Make(path, 1f);
            double sum = 0;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    sum += Math.Abs(normal[x, y].R - 128) + Math.Abs(normal[x, y].G - 128);
            return (float)(sum / (size * size));
        }
        float small = Relief(1024), large = Relief(2048);
        Require(Math.Abs(small / large - 1) < 0.15f, $"diffuse normal: the relief doesn't change with the texture's size ({small:0.0} at 1024, {large:0.0} at 2048)");

        tex.SourcePath = Path.Combine(root, "missing.png");
        Require(AlphaNormal.SourceOf(tex, cache, out var missing) == null && missing.Contains("diffuse not found"),
            "diffuse normal: a missing diffuse fails with why");
    }
}
