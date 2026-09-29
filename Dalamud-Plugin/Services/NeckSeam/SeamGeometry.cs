using System.Numerics;

namespace InstantEdit.Services.NeckSeam;

/// <summary> Mesh topology helpers: position welding, open-edge loops and the triangle behind each edge. </summary>
internal sealed class SeamTopology
{
    private const float WeldTolerance = 1e-5f;
    private readonly Dictionary<long, int> _edgeTriangle = new();
    private readonly Dictionary<long, int> _edgeUses = new();

    /// <summary> Welded id of each vertex: vertices at the same position (UV and normal splits) share one. </summary>
    public int[] Weld { get; }
    /// <summary> One vertex for each welded id. </summary>
    public int[] Representative { get; }
    public int[] Triangles { get; }

    public SeamTopology(Vector3[] positions, int[] triangles)
    {
        Triangles = triangles;
        Weld = new int[positions.Length];
        var ids = new Dictionary<(int, int, int), int>();
        var representatives = new List<int>();
        for (var v = 0; v < positions.Length; v++)
        {
            var p = positions[v] / WeldTolerance;
            var key = ((int)MathF.Round(p.X), (int)MathF.Round(p.Y), (int)MathF.Round(p.Z));
            if (!ids.TryGetValue(key, out var id))
            {
                id = representatives.Count;
                ids[key] = id;
                representatives.Add(v);
            }
            Weld[v] = id;
        }
        Representative = representatives.ToArray();
        for (var t = 0; t + 2 < triangles.Length; t += 3)
        {
            int a = Weld[triangles[t]], b = Weld[triangles[t + 1]], c = Weld[triangles[t + 2]];
            if (a == b || b == c || a == c)
                continue;
            foreach (var (x, y) in new[] { (a, b), (b, c), (c, a) })
            {
                var key = EdgeKey(x, y);
                _edgeUses[key] = _edgeUses.GetValueOrDefault(key) + 1;
                _edgeTriangle.TryAdd(key, t / 3);
            }
        }
    }

    public static long EdgeKey(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

    /// <summary>
    /// Closed loops of edges used by a single triangle, as welded ids in walking order. Open chains
    /// (where the boundary is not a simple loop) are left out.
    /// </summary>
    public List<int[]> BoundaryLoops()
    {
        var adjacency = new Dictionary<int, List<int>>();
        foreach (var (key, uses) in _edgeUses)
        {
            if (uses != 1)
                continue;
            int a = (int)(key >> 32), b = (int)(key & 0xFFFFFFFF);
            (adjacency.TryGetValue(a, out var la) ? la : adjacency[a] = []).Add(b);
            (adjacency.TryGetValue(b, out var lb) ? lb : adjacency[b] = []).Add(a);
        }
        var seen = new HashSet<int>();
        var loops = new List<int[]>();
        foreach (var start in adjacency.Keys.OrderBy(k => k))
        {
            if (!seen.Add(start))
                continue;
            var loop = new List<int> { start };
            int current = start, previous = -1;
            while (true)
            {
                var next = -1;
                foreach (var candidate in adjacency[current])
                    if (candidate != previous && !seen.Contains(candidate))
                    {
                        next = candidate;
                        break;
                    }
                if (next < 0)
                    break;
                seen.Add(next);
                loop.Add(next);
                previous = current;
                current = next;
            }
            if (loop.Count >= 3 && adjacency[current].Contains(start))
                loops.Add(loop.ToArray());
        }
        return loops;
    }

    /// <summary> The triangle holding the open edge between welded ids, as (vertex on a, vertex on b, opposite vertex). </summary>
    public (int A, int B, int Opposite)? EdgeTriangle(int a, int b)
    {
        if (!_edgeTriangle.TryGetValue(EdgeKey(a, b), out var triangle))
            return null;
        var t = triangle * 3;
        int ia = -1, ib = -1;
        for (var i = 0; i < 3; i++)
        {
            var w = Weld[Triangles[t + i]];
            if (w == a && ia < 0) ia = i;
            else if (w == b && ib < 0) ib = i;
        }
        if (ia < 0 || ib < 0)
            return null;
        return (Triangles[t + ia], Triangles[t + ib], Triangles[t + 3 - ia - ib]);
    }
}

/// <summary> An 8-bit RGBA image (rows top-down) sampled with the game's UV convention (v down). </summary>
internal sealed class SeamImage
{
    public SeamImage(int width, int height, byte[] rgba)
    {
        if (rgba.Length != checked(width * height * 4))
            throw new ArgumentException("The pixel buffer does not match the image size.", nameof(rgba));
        Width = width;
        Height = height;
        Rgba = rgba;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Rgba { get; }

    public SeamImage Clone() => new(Width, Height, (byte[])Rgba.Clone());

    public static SeamImage FromBgra(byte[] bgra, int width, int height)
    {
        var rgba = new byte[bgra.Length];
        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            rgba[i] = bgra[i + 2];
            rgba[i + 1] = bgra[i + 1];
            rgba[i + 2] = bgra[i];
            rgba[i + 3] = bgra[i + 3];
        }
        return new SeamImage(width, height, rgba);
    }

    public Vector4 Texel(int x, int y)
    {
        var i = (y * Width + x) * 4;
        return new Vector4(Rgba[i], Rgba[i + 1], Rgba[i + 2], Rgba[i + 3]) / 255f;
    }

    public void SetTexel(int x, int y, Vector4 value)
    {
        var i = (y * Width + x) * 4;
        Rgba[i] = ToByte(value.X);
        Rgba[i + 1] = ToByte(value.Y);
        Rgba[i + 2] = ToByte(value.Z);
        Rgba[i + 3] = ToByte(value.W);
    }

    private static byte ToByte(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);

    /// <summary> Bilinear sample at a UV, clamped to the image. </summary>
    public Vector4 Sample(Vector2 uv)
    {
        var x = Math.Clamp(uv.X * Width - 0.5f, 0f, Width - 1.001f);
        var y = Math.Clamp(uv.Y * Height - 0.5f, 0f, Height - 1.001f);
        int x0 = (int)x, y0 = (int)y;
        int x1 = Math.Min(x0 + 1, Width - 1), y1 = Math.Min(y0 + 1, Height - 1);
        float fx = x - x0, fy = y - y0;
        return Texel(x0, y0) * (1 - fx) * (1 - fy) + Texel(x1, y0) * fx * (1 - fy) + Texel(x0, y1) * (1 - fx) * fy + Texel(x1, y1) * fx * fy;
    }
}

/// <summary> Decoding of .tex files into <see cref="SeamImage"/>s, whole or at a smaller mip level. </summary>
internal static class SeamTextures
{
    private const int HeaderSize = 80;

    public static SeamImage Decode(byte[] tex)
    {
        var decoded = Previews.TextureDecoder.Decode(tex, int.MaxValue);
        return SeamImage.FromBgra(decoded.Bgra, decoded.Width, decoded.Height);
    }

    /// <summary>
    /// Decodes the largest mip level whose longest edge is at most <paramref name="maxEdge"/> (mip 0
    /// when the file has no smaller level that fits), avoiding a full decode of a 4K texture that is
    /// only sampled along a seam.
    /// </summary>
    public static SeamImage DecodeAtMost(byte[] tex, int maxEdge)
    {
        var bytes = TextureFiles.NormalizeMipOffsets(tex);
        if (bytes.Length < HeaderSize)
            return Decode(tex);
        var format = BitConverter.ToUInt32(bytes, 4);
        int width = BitConverter.ToUInt16(bytes, 8), height = BitConverter.ToUInt16(bytes, 10);
        var mips = Math.Clamp(bytes[14] & 0x7F, 1, 13);
        var mip = 0;
        while (mip + 1 < mips && Math.Max(width >> mip, height >> mip) > maxEdge)
            mip++;
        if (mip == 0)
            return Decode(bytes);
        var bitsPerPixel = 1L << (int)((format >> 4) & 0xF);
        var blockCompressed = ((format >> 12) & 0xF) is 3 or 6;
        long w = Math.Max(1, width >> mip), h = Math.Max(1, height >> mip);
        var size = blockCompressed ? Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * bitsPerPixel * 2 : w * h * bitsPerPixel / 8;
        long offset = BitConverter.ToUInt32(bytes, 28 + mip * 4);
        if (offset < HeaderSize || offset + size > bytes.Length)
            return Decode(bytes);
        var single = new byte[HeaderSize + size];
        bytes.AsSpan(0, HeaderSize).CopyTo(single);
        BitConverter.TryWriteBytes(single.AsSpan(8), (ushort)w);
        BitConverter.TryWriteBytes(single.AsSpan(10), (ushort)h);
        single[14] = (byte)((bytes[14] & 0x80) | 1);
        single.AsSpan(16, 64).Clear();
        BitConverter.TryWriteBytes(single.AsSpan(28), (uint)HeaderSize);
        bytes.AsSpan((int)offset, (int)size).CopyTo(single.AsSpan(HeaderSize));
        return Decode(single);
    }
}
