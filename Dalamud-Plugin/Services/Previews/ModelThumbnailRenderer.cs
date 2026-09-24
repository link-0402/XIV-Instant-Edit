using System.Numerics;

namespace InstantEdit.Services.Previews;

/// <summary>
/// A tiny software rasteriser: orthographic three-quarter view, z-buffer, flat Lambert shading,
/// one palette colour per material. Produces BGRA pixels with transparent background.
/// </summary>
public static class ModelThumbnailRenderer
{
    public const int DefaultSize = 256;
    private const float Margin = .08f;
    private const float YawDegrees = 35f;
    private const float PitchDegrees = 20f;
    private const int SupersampleBelowTriangles = 20_000;

    /// <summary> Base colours per material index (R, G, B), shared with the card's material chips. </summary>
    public static readonly (byte R, byte G, byte B)[] Palette =
    [
        (188, 196, 214),
        (222, 176, 118),
        (132, 196, 164),
        (170, 150, 220),
        (226, 140, 140),
        (128, 184, 222),
        (214, 202, 128),
        (160, 212, 210),
    ];

    public static byte[] Render(ModelGeometry geometry, int size = DefaultSize, bool supersample = true)
    {
        if (size < 8) throw new ArgumentOutOfRangeException(nameof(size));
        var scale = supersample && geometry.TriangleCount < SupersampleBelowTriangles ? 2 : 1;
        var n = size * scale;

        // Rotate into view space: yaw around Y, then pitch around X. +Z points at the viewer.
        var rotation = Matrix4x4.CreateRotationY(YawDegrees * MathF.PI / 180f) * Matrix4x4.CreateRotationX(-PitchDegrees * MathF.PI / 180f);
        var view = new Vector3[geometry.Positions.Length];
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (var i = 0; i < view.Length; i++)
        {
            view[i] = Vector3.Transform(geometry.Positions[i], rotation);
            min = Vector3.Min(min, view[i]);
            max = Vector3.Max(max, view[i]);
        }

        var extent = MathF.Max(MathF.Max(max.X - min.X, max.Y - min.Y), 1e-6f);
        var usable = n * (1 - 2 * Margin);
        var pixelsPerUnit = usable / extent;
        var centre = (min + max) / 2;
        var half = n / 2f;

        var depth = new float[n * n];
        Array.Fill(depth, float.MaxValue);
        var colour = new uint[n * n];
        var light = Vector3.Normalize(new Vector3(-.35f, .55f, .8f));

        foreach (var (start, count, materialIndex) in geometry.Meshes)
        {
            var (r, g, b) = Palette[Math.Abs(materialIndex) % Palette.Length];
            for (var t = start; t + 2 < start + count; t += 3)
            {
                var p0 = view[geometry.Indices[t]];
                var p1 = view[geometry.Indices[t + 1]];
                var p2 = view[geometry.Indices[t + 2]];
                var normal = Vector3.Cross(p1 - p0, p2 - p0);
                var length = normal.Length();
                if (length <= 1e-12f)
                    continue;
                normal /= length;
                if (normal.Z < 0)
                    normal = -normal;
                var shade = .25f + .75f * MathF.Max(0, Vector3.Dot(normal, light));
                var packed = Pack((byte)(b * shade), (byte)(g * shade), (byte)(r * shade));

                var (x0, y0) = Project(p0, centre, pixelsPerUnit, half);
                var (x1, y1) = Project(p1, centre, pixelsPerUnit, half);
                var (x2, y2) = Project(p2, centre, pixelsPerUnit, half);
                Rasterise(depth, colour, n, x0, y0, -p0.Z, x1, y1, -p1.Z, x2, y2, -p2.Z, packed);
            }
        }

        return scale == 1 ? ToBgra(colour, n) : Downsample(colour, n, size);
    }

    private static (float X, float Y) Project(Vector3 p, Vector3 centre, float pixelsPerUnit, float half)
        => (half + (p.X - centre.X) * pixelsPerUnit, half - (p.Y - centre.Y) * pixelsPerUnit);

    private static uint Pack(byte b, byte g, byte r) => (uint)b | ((uint)g << 8) | ((uint)r << 16) | 0xFF000000u;

    private static void Rasterise(float[] depth, uint[] colour, int n,
        float x0, float y0, float z0, float x1, float y1, float z1, float x2, float y2, float z2, uint packed)
    {
        var area = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0);
        if (MathF.Abs(area) < 1e-9f)
            return;
        var minX = Math.Max(0, (int)MathF.Floor(MathF.Min(x0, MathF.Min(x1, x2))));
        var maxX = Math.Min(n - 1, (int)MathF.Ceiling(MathF.Max(x0, MathF.Max(x1, x2))));
        var minY = Math.Max(0, (int)MathF.Floor(MathF.Min(y0, MathF.Min(y1, y2))));
        var maxY = Math.Min(n - 1, (int)MathF.Ceiling(MathF.Max(y0, MathF.Max(y1, y2))));
        if (minX > maxX || minY > maxY)
            return;
        var inverseArea = 1f / area;
        for (var y = minY; y <= maxY; y++)
        {
            var py = y + .5f;
            for (var x = minX; x <= maxX; x++)
            {
                var px = x + .5f;
                var w0 = ((x1 - px) * (y2 - py) - (x2 - px) * (y1 - py)) * inverseArea;
                var w1 = ((x2 - px) * (y0 - py) - (x0 - px) * (y2 - py)) * inverseArea;
                var w2 = 1 - w0 - w1;
                if (w0 < 0 || w1 < 0 || w2 < 0)
                    continue;
                var z = w0 * z0 + w1 * z1 + w2 * z2;
                var index = y * n + x;
                if (z >= depth[index])
                    continue;
                depth[index] = z;
                colour[index] = packed;
            }
        }
    }

    private static byte[] ToBgra(uint[] colour, int n)
    {
        var result = new byte[colour.Length * 4];
        for (var i = 0; i < colour.Length; i++)
        {
            var c = colour[i];
            var o = i * 4;
            result[o] = (byte)c;
            result[o + 1] = (byte)(c >> 8);
            result[o + 2] = (byte)(c >> 16);
            result[o + 3] = (byte)(c >> 24);
        }
        return result;
    }

    private static byte[] Downsample(uint[] colour, int n, int size)
    {
        var result = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                int b = 0, g = 0, r = 0, a = 0;
                for (var sy = 0; sy < 2; sy++)
                {
                    for (var sx = 0; sx < 2; sx++)
                    {
                        var c = colour[(y * 2 + sy) * n + x * 2 + sx];
                        if (c == 0)
                            continue;
                        b += (byte)c;
                        g += (byte)(c >> 8);
                        r += (byte)(c >> 16);
                        a += 255;
                    }
                }
                var o = (y * size + x) * 4;
                var covered = a / 255;
                if (covered == 0)
                    continue;
                // Average colour over covered samples; alpha reflects coverage for soft edges.
                result[o] = (byte)(b / covered);
                result[o + 1] = (byte)(g / covered);
                result[o + 2] = (byte)(r / covered);
                result[o + 3] = (byte)(a / 4);
            }
        }
        return result;
    }
}
