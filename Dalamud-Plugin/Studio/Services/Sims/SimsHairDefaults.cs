using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace InstantEdit.Studio.Services.Sims;

/// <summary>
/// What a hair port needs from a Sims hair package, which carries much more than that.
///
/// Each detail level of a hair lists its meshes in sets that share a shader: the alpha hair
/// cards and the solid cap under them that hides bald spots. Each set holds the full mesh and
/// cut-down copies the game swaps in under hats, with the top of the hair removed. The full
/// mesh is the one with the most vertices, since a hat version is the full one minus the part
/// a hat covers, and a hat version stops short of the full mesh's top (by 2 to 8 cm in the
/// packages checked). A smaller mesh of the shader that reaches the top is another part of the
/// hair, such as bangs beside the back, not a hat version. (An overlap test is no good: a style's
/// hat versions may be shared with another style and only half lie on its full mesh.)
///
/// A hair also ships one diffuse per colour swatch, and a port wants one: the lightest grey,
/// which is turned into the hair's normal texture. Not every creator includes a white or grey
/// swatch, so without a near-grey one the lightest of all is taken. Its alpha map, the first
/// step of that, is written beside it on extraction (<see cref="AlphaMap"/>).
/// </summary>
public static class SimsHairDefaults
{
    /// <summary>Average colour spread (max minus min channel, 0-255) up to which a diffuse counts as grey.</summary>
    public const float GreySaturation = 15f;

    /// <summary>How far below the full mesh's top (in metres, the game's up axis Y) a hat version's top lies at least.</summary>
    public const float HatCut = 0.01f;

    /// <summary>
    /// The hat versions among one detail level's meshes: within each shader, every mesh but the
    /// one with the most vertices (the first of equals) whose <c>Top</c>, its highest point, lies
    /// at least <see cref="HatCut"/> below that one's.
    /// </summary>
    public static HashSet<T> HatVersions<T>(IEnumerable<(T Mesh, uint Shader, int Vertices, float Top)> meshes)
    {
        var hat = new HashSet<T>();
        foreach (var set in meshes.GroupBy(m => m.Shader))
        {
            var full = set.First(m => m.Vertices == set.Max(x => x.Vertices));
            foreach (var (mesh, _, _, top) in set)
                if (!EqualityComparer<T>.Default.Equals(mesh, full.Mesh) && top <= full.Top - HatCut)
                    hat.Add(mesh);
        }
        return hat;
    }

    /// <summary>The index of the diffuse to keep: the lightest grey one, else the lightest. -1 for none.</summary>
    public static int PickDiffuse(IReadOnlyList<(float Lightness, float Saturation)> diffuses)
    {
        int best = -1;
        for (int pass = 0; pass < 2 && best < 0; pass++)
            for (int i = 0; i < diffuses.Count; i++)
            {
                var (lightness, saturation) = diffuses[i];
                if (lightness < 0 || (pass == 0 && saturation > GreySaturation))
                    continue;
                if (best < 0 || lightness > diffuses[best].Lightness)
                    best = i;
            }
        return best;
    }

    /// <summary>
    /// The alpha map of a hair diffuse, the first step of turning it into the port's normal
    /// texture: opaque straight RGBA8 whose grey is the diffuse's alpha, as Photoshop's Layer
    /// Mask > From Transparency makes it. Solid strands are white, the background black, and
    /// the soft strand edges keep their grey. Laid over a base normal, it keeps the normal
    /// only where the strands are.
    /// </summary>
    public static byte[] AlphaMap(byte[] rgba)
    {
        var map = new byte[rgba.Length];
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            map[i] = map[i + 1] = map[i + 2] = rgba[i + 3];
            map[i + 3] = 255;
        }
        return map;
    }

    /// <summary>
    /// The part of a Sims texture sheet a hair uses. A Sims CAS sheet (1024×2048 for a hair) holds
    /// every body part in its own place, and a hair only takes a square near the top left; a port
    /// wants that square as its whole texture, so it fits the full-size base normal and mask.
    ///
    /// Found from the meshes' UVs measured in pixels of the sheet: the smallest power-of-two square
    /// on the sheet's grid of that size holding every UV. Null when there is nothing to cut: no UVs,
    /// UVs off the sheet, or a square as large as the sheet.
    /// </summary>
    public static UvRegion? HairRegion(IEnumerable<Vector2> uvs, int sheetWidth, int sheetHeight)
    {
        const float Slack = 0.5f;   // half a pixel past an edge still counts as inside
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var uv in uvs)
        {
            minX = Math.Min(minX, uv.X * sheetWidth);
            minY = Math.Min(minY, uv.Y * sheetHeight);
            maxX = Math.Max(maxX, uv.X * sheetWidth);
            maxY = Math.Max(maxY, uv.Y * sheetHeight);
        }
        if (minX > maxX || sheetWidth <= 0 || sheetHeight <= 0 ||
            minX < -Slack || minY < -Slack || maxX > sheetWidth + Slack || maxY > sheetHeight + Slack)
            return null;

        minX = Math.Max(0, minX);
        minY = Math.Max(0, minY);
        int side = 64;
        while (side < Math.Max(maxX - minX, maxY - minY))
            side *= 2;
        for (; side < Math.Max(sheetWidth, sheetHeight); side *= 2)
        {
            int x0 = (int)(minX / side) * side, y0 = (int)(minY / side) * side;
            int w = Math.Min(side, sheetWidth - x0), h = Math.Min(side, sheetHeight - y0);
            if (maxX <= x0 + w + Slack && maxY <= y0 + h + Slack)
                return new UvRegion((float)x0 / sheetWidth, (float)y0 / sheetHeight, (float)w / sheetWidth, (float)h / sheetHeight);
        }
        return null;
    }

    /// <summary>
    /// Mean lightness (0-255) and colour spread of the opaque pixels of straight RGBA8 data,
    /// sampled every few pixels. (-1, 0) when none is opaque.
    /// </summary>
    public static (float Lightness, float Saturation) Measure(byte[] rgba)
    {
        const int Step = 4 * 7;   // every 7th pixel; an odd step avoids sampling one column
        double lightness = 0, saturation = 0;
        long count = 0;
        for (int i = 0; i + 3 < rgba.Length; i += Step)
        {
            if (rgba[i + 3] < 128)
                continue;
            int r = rgba[i], g = rgba[i + 1], b = rgba[i + 2];
            lightness += (r + g + b) / 3.0;
            saturation += Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
            count++;
        }
        return count == 0 ? (-1f, 0f) : ((float)(lightness / count), (float)(saturation / count));
    }
}

/// <summary>A rectangle of a texture sheet in UV units (origin top left), and how UVs and images are cut down to it.</summary>
public readonly record struct UvRegion(float U, float V, float Width, float Height)
{
    /// <summary>A UV of the sheet as a UV of the cut-out.</summary>
    public Vector2 Map(Vector2 uv) => new((uv.X - U) / Width, (uv.Y - V) / Height);

    /// <summary>The region in pixels of an image of the given size, which may differ from the sheet the region was measured on.</summary>
    public (int X, int Y, int Width, int Height) Pixels(int width, int height)
    {
        int x = Math.Clamp((int)MathF.Round(U * width), 0, width - 1);
        int y = Math.Clamp((int)MathF.Round(V * height), 0, height - 1);
        return (x, y, Math.Clamp((int)MathF.Round(Width * width), 1, width - x), Math.Clamp((int)MathF.Round(Height * height), 1, height - y));
    }

    /// <summary>Straight RGBA8 pixels of an image of the given size, cut down to the region.</summary>
    public byte[] Crop(byte[] rgba, int width, int height, out int croppedWidth, out int croppedHeight)
    {
        var (x, y, w, h) = Pixels(width, height);
        var cropped = new byte[w * h * 4];
        for (int row = 0; row < h; row++)
            Buffer.BlockCopy(rgba, ((y + row) * width + x) * 4, cropped, row * w * 4, w * 4);
        croppedWidth = w;
        croppedHeight = h;
        return cropped;
    }
}
