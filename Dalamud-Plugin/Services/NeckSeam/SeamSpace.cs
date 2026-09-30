using System.Numerics;

namespace InstantEdit.Services.NeckSeam;

/// <summary> The nearest point of a triangle set: how far away it is, its triangle (an index into the triangle list over three) and its barycentric weights. </summary>
internal readonly record struct SurfacePoint(float Distance, int Triangle, Vector3 Weights, Vector3 Point);

/// <summary>
/// Triangles in the character's pose, bucketed in a 1 cm grid, for the questions the seam checks ask
/// about what lies around a seam: the nearest point of a part's skin (does an edge rest on it, does a
/// connector stay under it) and whether rays from a point hit clothing (does clothing cover it).
/// Queries reuse one scratch array, so one instance serves one thread.
/// </summary>
internal sealed class SeamSpace
{
    private const float Cell = 0.01f;
    /// <summary> Triangles spanning more cells than this along an axis (64 cm) are left out; nothing on a character is that big. </summary>
    private const int MaxSpan = 64;
    private readonly Dictionary<(int X, int Y, int Z), List<int>> _cells = new();
    private readonly int[] _seen;
    private int _stamp;

    public Vector3[] Positions { get; }
    public int[] Triangles { get; }
    public int Count => Triangles.Length / 3;
    public bool Empty => Triangles.Length < 3;

    public SeamSpace(Vector3[] positions, int[] triangles)
    {
        Positions = positions;
        Triangles = triangles;
        _seen = new int[triangles.Length / 3];
        for (var t = 0; t < triangles.Length / 3; t++)
        {
            Vector3 a = positions[triangles[3 * t]], b = positions[triangles[3 * t + 1]], c = positions[triangles[3 * t + 2]];
            var low = Key(Vector3.Min(a, Vector3.Min(b, c)));
            var high = Key(Vector3.Max(a, Vector3.Max(b, c)));
            if (high.X - low.X > MaxSpan || high.Y - low.Y > MaxSpan || high.Z - low.Z > MaxSpan)
                continue;
            for (var x = low.X; x <= high.X; x++)
                for (var y = low.Y; y <= high.Y; y++)
                    for (var z = low.Z; z <= high.Z; z++)
                    {
                        if (!_cells.TryGetValue((x, y, z), out var list))
                            _cells[(x, y, z)] = list = [];
                        list.Add(t);
                    }
        }
    }

    /// <summary> Triangle soup of several surfaces, their positions offset into one array. </summary>
    public static SeamSpace Of(IEnumerable<(Vector3[] Positions, int[] Triangles)> parts)
    {
        var positions = new List<Vector3>();
        var triangles = new List<int>();
        foreach (var (p, t) in parts)
        {
            var offset = positions.Count;
            positions.AddRange(p);
            foreach (var index in t)
                triangles.Add(index + offset);
        }
        return new SeamSpace(positions.ToArray(), triangles.ToArray());
    }

    private static (int X, int Y, int Z) Key(Vector3 p) => ((int)MathF.Floor(p.X / Cell), (int)MathF.Floor(p.Y / Cell), (int)MathF.Floor(p.Z / Cell));

    /// <summary> The nearest point of the triangles within <paramref name="reach"/> of <paramref name="p"/>, or null. </summary>
    public SurfacePoint? Nearest(Vector3 p, float reach)
    {
        var low = Key(p - new Vector3(reach));
        var high = Key(p + new Vector3(reach));
        var stamp = ++_stamp;
        SurfacePoint? best = null;
        for (var x = low.X; x <= high.X; x++)
            for (var y = low.Y; y <= high.Y; y++)
                for (var z = low.Z; z <= high.Z; z++)
                {
                    if (!_cells.TryGetValue((x, y, z), out var list))
                        continue;
                    foreach (var t in list)
                    {
                        if (_seen[t] == stamp)
                            continue;
                        _seen[t] = stamp;
                        var (point, weights) = Closest(p, Positions[Triangles[3 * t]], Positions[Triangles[3 * t + 1]], Positions[Triangles[3 * t + 2]]);
                        var distance = Vector3.Distance(p, point);
                        if (distance <= reach && (best is null || distance < best.Value.Distance))
                            best = new SurfacePoint(distance, t, weights, point);
                    }
                }
        return best;
    }

    /// <summary> Whether a ray from <paramref name="origin"/> along the unit <paramref name="direction"/> hits a triangle within <paramref name="length"/>. </summary>
    public bool Hits(Vector3 origin, Vector3 direction, float length)
    {
        if (Empty)
            return false;
        var (x, y, z) = Key(origin);
        int stepX = Math.Sign(direction.X), stepY = Math.Sign(direction.Y), stepZ = Math.Sign(direction.Z);
        static float First(float o, int cell, int step, float d) => step == 0 ? float.MaxValue : ((cell + (step > 0 ? 1 : 0)) * Cell - o) / d;
        static float Delta(float d) => d == 0 ? float.MaxValue : Cell / MathF.Abs(d);
        float tx = First(origin.X, x, stepX, direction.X), ty = First(origin.Y, y, stepY, direction.Y), tz = First(origin.Z, z, stepZ, direction.Z);
        float dx = Delta(direction.X), dy = Delta(direction.Y), dz = Delta(direction.Z);
        var stamp = ++_stamp;
        while (true)
        {
            if (_cells.TryGetValue((x, y, z), out var list))
                foreach (var t in list)
                {
                    if (_seen[t] == stamp)
                        continue;
                    _seen[t] = stamp;
                    if (Ray(origin, direction, Positions[Triangles[3 * t]], Positions[Triangles[3 * t + 1]], Positions[Triangles[3 * t + 2]]) is { } hit &&
                        hit > 1e-5f && hit < length)
                        return true;
                }
            if (tx <= ty && tx <= tz)
            {
                if (tx > length)
                    return false;
                x += stepX;
                tx += dx;
            }
            else if (ty <= tz)
            {
                if (ty > length)
                    return false;
                y += stepY;
                ty += dy;
            }
            else
            {
                if (tz > length)
                    return false;
                z += stepZ;
                tz += dz;
            }
        }
    }

    /// <summary>
    /// Whether the triangles (clothing) close in around a point of skin: every ray from just above it,
    /// along its normal and in two rings tilted 35° and 65° from it, hits them within 15 cm. A seam
    /// point that passes can't be seen from any side the clothing leaves open.
    /// </summary>
    public bool Encloses(Vector3 point, Vector3 normal)
    {
        if (Empty)
            return false;
        var n = NeckSeamAnalyzer.SafeNormalize(normal, Vector3.UnitY);
        var origin = point + n * 0.0005f;
        var side = MathF.Abs(n.Y) < 0.9f ? Vector3.Cross(n, Vector3.UnitY) : Vector3.Cross(n, Vector3.UnitX);
        var t = Vector3.Normalize(side);
        var b = Vector3.Cross(n, t);
        if (!Hits(origin, n, EncloseReach))
            return false;
        foreach (var tilt in EncloseTilts)
            for (var i = 0; i < EncloseRays; i++)
            {
                var phi = 2 * MathF.PI * i / EncloseRays;
                var direction = MathF.Cos(tilt) * n + MathF.Sin(tilt) * (MathF.Cos(phi) * t + MathF.Sin(phi) * b);
                if (!Hits(origin, direction, EncloseReach))
                    return false;
            }
        return true;
    }

    private const float EncloseReach = 0.15f;
    private const int EncloseRays = 6;
    private static readonly float[] EncloseTilts = [35f * MathF.PI / 180f, 65f * MathF.PI / 180f];

    /// <summary> The point of triangle abc nearest to p and its barycentric weights (Ericson, Real-Time Collision Detection 5.1.5). </summary>
    internal static (Vector3 Point, Vector3 Weights) Closest(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        var ab = b - a;
        var ac = c - a;
        var ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0)
            return (a, new Vector3(1, 0, 0));
        var bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3)
            return (b, new Vector3(0, 1, 0));
        var vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0)
        {
            var v = d1 / (d1 - d3);
            return (a + v * ab, new Vector3(1 - v, v, 0));
        }
        var cp = p - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6)
            return (c, new Vector3(0, 0, 1));
        var vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0)
        {
            var w = d2 / (d2 - d6);
            return (a + w * ac, new Vector3(1 - w, 0, w));
        }
        var va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
        {
            var w = (d4 - d3) / (d4 - d3 + (d5 - d6));
            return (b + w * (c - b), new Vector3(0, 1 - w, w));
        }
        var denominator = va + vb + vc;
        if (MathF.Abs(denominator) < 1e-30f)
            return (a, new Vector3(1, 0, 0));
        var sv = vb / denominator;
        var sw = vc / denominator;
        return (a + ab * sv + ac * sw, new Vector3(1 - sv - sw, sv, sw));
    }

    /// <summary> Where a ray meets triangle abc, as a distance along it (Möller–Trumbore), or null. </summary>
    private static float? Ray(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c)
    {
        var e1 = b - a;
        var e2 = c - a;
        var p = Vector3.Cross(direction, e2);
        var det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-14f)
            return null;
        var inverse = 1 / det;
        var s = origin - a;
        var u = Vector3.Dot(s, p) * inverse;
        if (u < 0 || u > 1)
            return null;
        var q = Vector3.Cross(s, e1);
        var v = Vector3.Dot(direction, q) * inverse;
        if (v < 0 || u + v > 1)
            return null;
        return Vector3.Dot(e2, q) * inverse;
    }
}
