using System.Numerics;

namespace InstantEdit.Services.Painter;

/// <summary>
/// Which texels of a texture the project's UV islands cover. Painter computes fill layers across the
/// whole texture, so for a texture that other meshes also use, only the covered texels (plus a seam
/// margin) are taken from Painter and the rest keeps the texture's current content.
/// </summary>
internal sealed class UvCoverage
{
    private readonly bool[] _covered;

    private UvCoverage(int width, int height, bool[] covered)
    {
        Width = width;
        Height = height;
        _covered = covered;
    }

    public int Width { get; }
    public int Height { get; }

    public bool IsCovered(int x, int y) => _covered[y * Width + x];

    public int CoveredCount => _covered.Count(c => c);

    /// <summary> Seam margin in texels: 8 at 2048, scaled with the texture and never below 2. </summary>
    public static int MarginFor(int width, int height) => Math.Max(2, Math.Max(width, height) / 256);

    /// <summary>
    /// Rasterizes triangles given in game UV space (origin top-left, one tile = 0..1) at the given
    /// size; UVs outside the tile wrap, as the texture repeats. Each triangle also marks the texels
    /// under its corners, so slivers thinner than a texel still count.
    /// </summary>
    public static UvCoverage Rasterize(int width, int height, IEnumerable<(Vector2 A, Vector2 B, Vector2 C)> triangles, int margin)
    {
        var covered = new bool[checked(width * height)];
        foreach (var (a, b, c) in triangles)
        {
            var pa = new Vector2(a.X * width, a.Y * height);
            var pb = new Vector2(b.X * width, b.Y * height);
            var pc = new Vector2(c.X * width, c.Y * height);
            if (!Finite(pa) || !Finite(pb) || !Finite(pc))
                continue;
            Mark(covered, width, height, pa);
            Mark(covered, width, height, pb);
            Mark(covered, width, height, pc);
            var minX = (int)Math.Floor(Math.Min(pa.X, Math.Min(pb.X, pc.X)));
            var maxX = (int)Math.Ceiling(Math.Max(pa.X, Math.Max(pb.X, pc.X)));
            var minY = (int)Math.Floor(Math.Min(pa.Y, Math.Min(pb.Y, pc.Y)));
            var maxY = (int)Math.Ceiling(Math.Max(pa.Y, Math.Max(pb.Y, pc.Y)));
            // A triangle larger than a few tiles is corrupt data; the corners above still count.
            if (maxX - minX > 4 * width || maxY - minY > 4 * height)
                continue;
            var area = Edge(pa, pb, pc);
            if (Math.Abs(area) < 1e-12f)
                continue;
            for (var y = minY; y < maxY; y++)
            {
                for (var x = minX; x < maxX; x++)
                {
                    var p = new Vector2(x + 0.5f, y + 0.5f);
                    var w0 = Edge(pb, pc, p) / area;
                    var w1 = Edge(pc, pa, p) / area;
                    var w2 = Edge(pa, pb, p) / area;
                    const float epsilon = -1e-4f;
                    if (w0 >= epsilon && w1 >= epsilon && w2 >= epsilon)
                        covered[Wrap(y, height) * width + Wrap(x, width)] = true;
                }
            }
        }
        if (margin > 0)
            Dilate(covered, width, height, margin);
        return new UvCoverage(width, height, covered);
    }

    /// <summary> Covered texels from Painter's image, the rest from the current texture. Both must match this size. </summary>
    public RgbaImage Merge(RgbaImage painter, RgbaImage current)
    {
        if (painter.Width != Width || painter.Height != Height || current.Width != Width || current.Height != Height)
            throw new InvalidDataException("The images and the coverage mask differ in size.");
        var result = new RgbaImage(Width, Height, (byte[])current.Pixels.Clone());
        for (var i = 0; i < _covered.Length; i++)
        {
            if (!_covered[i])
                continue;
            Buffer.BlockCopy(painter.Pixels, i * 4, result.Pixels, i * 4, 4);
        }
        return result;
    }

    private static float Edge(Vector2 a, Vector2 b, Vector2 p) => (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);

    private static bool Finite(Vector2 v) => float.IsFinite(v.X) && float.IsFinite(v.Y);

    private static int Wrap(int value, int size) => ((value % size) + size) % size;

    private static void Mark(bool[] covered, int width, int height, Vector2 p)
    {
        if (Math.Abs(p.X) > 1e7f || Math.Abs(p.Y) > 1e7f)
            return;
        covered[Wrap((int)Math.Floor(p.Y), height) * width + Wrap((int)Math.Floor(p.X), width)] = true;
    }

    /// <summary> Square dilation by the margin, as a horizontal then a vertical sliding-window pass. </summary>
    private static void Dilate(bool[] covered, int width, int height, int margin)
    {
        var temp = new bool[covered.Length];
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            var count = 0;
            for (var x = 0; x < Math.Min(margin, width); x++)
                if (covered[row + x]) count++;
            for (var x = 0; x < width; x++)
            {
                var add = x + margin;
                if (add < width && covered[row + add]) count++;
                var remove = x - margin - 1;
                if (remove >= 0 && covered[row + remove]) count--;
                temp[row + x] = count > 0;
            }
        }
        for (var x = 0; x < width; x++)
        {
            var count = 0;
            for (var y = 0; y < Math.Min(margin, height); y++)
                if (temp[y * width + x]) count++;
            for (var y = 0; y < height; y++)
            {
                var add = y + margin;
                if (add < height && temp[add * width + x]) count++;
                var remove = y - margin - 1;
                if (remove >= 0 && temp[remove * width + x]) count--;
                covered[y * width + x] = count > 0;
            }
        }
    }
}
