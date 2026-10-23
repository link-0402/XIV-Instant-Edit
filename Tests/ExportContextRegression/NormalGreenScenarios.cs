using InstantEdit.Studio.Models;
using InstantEdit.Studio.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// The green direction of File normal slots: read from the image, from the file name when the image
/// is unclear, or set by hand; a DirectX normal is written with its green flipped.
/// </summary>
internal static class NormalGreenScenarios
{
    public static void Run(string testRoot)
    {
        var root = Path.Combine(testRoot, "NormalGreen");
        Directory.CreateDirectory(root);
        var cache = Path.Combine(root, "cache");

        // An OpenGL normal of a bumpy surface, h = sin(x/7)·cos(y/5), and the same with green flipped.
        var openGl = Path.Combine(root, "bumps.png");
        var directX = Path.Combine(root, "bumps_flipped.png");
        using (var gl = new Image<Rgba32>(256, 256))
        using (var dx = new Image<Rgba32>(256, 256))
        {
            for (int y = 0; y < 256; y++)
                for (int x = 0; x < 256; x++)
                {
                    float dhdx = MathF.Cos(x / 7f) / 7f * MathF.Cos(y / 5f);
                    float dhdrow = -MathF.Sin(x / 7f) * MathF.Sin(y / 5f) / 5f;
                    float nx = -dhdx, ny = dhdrow, len = MathF.Sqrt(nx * nx + ny * ny + 1);
                    byte r = (byte)MathF.Round(nx / len * 127.5f + 127.5f), g = (byte)MathF.Round(ny / len * 127.5f + 127.5f);
                    gl[x, y] = new Rgba32(r, g, 255, 255);
                    dx[x, y] = new Rgba32(r, (byte)(255 - g), 255, 255);
                }
            gl.SaveAsPng(openGl);
            dx.SaveAsPng(directX);
        }

        var glReading = NormalGreen.Read(openGl);
        var dxReading = NormalGreen.Read(directX);
        Require(glReading.Image == NormalGreen.Direction.OpenGl && glReading.Margin >= NormalGreen.SureMargin,
            $"normal green: green up reads as OpenGL ({glReading.Image}, {glReading.Margin:0.00})");
        Require(dxReading.Image == NormalGreen.Direction.DirectX, $"normal green: green down reads as DirectX ({dxReading.Image})");

        var tex = new TextureSlot { Type = TextureType.Normal, SourcePath = openGl };
        Require(AlphaNormal.SourceOf(tex, cache, out _) == openGl, "normal green: an OpenGL normal is converted as it is");

        tex.SourcePath = directX;
        var flipped = AlphaNormal.SourceOf(tex, cache, out var error);
        Require(flipped != null && flipped != directX && File.Exists(flipped), $"normal green: a DirectX normal is flipped ({error})");
        using (var a = Image.Load<Rgba32>(directX))
        using (var b = Image.Load<Rgba32>(flipped!))
            Require(b[40, 70].G == 255 - a[40, 70].G && b[40, 70].R == a[40, 70].R && b[40, 70].B == a[40, 70].B,
                "normal green: only the green is flipped");
        Require(NormalGreen.Read(flipped!).Image == NormalGreen.Direction.OpenGl, "normal green: the flipped normal reads as OpenGL");

        tex.GreenDirection = NormalGreenMode.OpenGl;
        Require(AlphaNormal.SourceOf(tex, cache, out _) == directX, "normal green: OpenGL set by hand keeps the file");
        tex.SourcePath = openGl;
        tex.GreenDirection = NormalGreenMode.DirectX;
        Require(AlphaNormal.SourceOf(tex, cache, out _) != openGl, "normal green: DirectX set by hand flips even a map read as OpenGL");

        // A flat normal says nothing; its name decides.
        var flatDx = Path.Combine(root, "cloth_Normal_DirectX.png");
        var flat = Path.Combine(root, "cloth_normal.png");
        using (var image = new Image<Rgba32>(64, 64, new Rgba32(128, 128, 255, 255)))
        {
            image.SaveAsPng(flatDx);
            image.SaveAsPng(flat);
        }
        tex.GreenDirection = NormalGreenMode.Auto;
        tex.SourcePath = flatDx;
        Require(NormalGreen.Read(flatDx).Image == NormalGreen.Direction.Unsure, "normal green: a flat normal is unclear");
        Require(AlphaNormal.SourceOf(tex, cache, out _) != flatDx, "normal green: an unclear normal named DirectX is flipped");
        tex.SourcePath = flat;
        Require(AlphaNormal.SourceOf(tex, cache, out _) == flat, "normal green: an unclear normal with no hint is kept");

        Require(NormalGreen.FromName(@"C:\x\Boots_Normal_OpenGL.png") == NormalGreen.Direction.OpenGl
             && NormalGreen.FromName(@"C:\x\T_Body_N_DX.tga") == NormalGreen.Direction.DirectX
             && NormalGreen.FromName(@"C:\x\dxt5_normal.png") == NormalGreen.Direction.Unsure,
            "normal green: file name hints are whole words");

        // A diffuse slot is never touched.
        var diffuse = new TextureSlot { Type = TextureType.Diffuse, SourcePath = directX };
        Require(AlphaNormal.SourceOf(diffuse, cache, out _) == directX, "normal green: only normal slots are looked at");
    }
}
