using System.Globalization;
using System.Numerics;
using InstantEdit.Services.Previews;

namespace InstantEdit.Services.Painter;

/// <summary> One submesh written as its own OBJ object, drawn with the given material (texture set). </summary>
public sealed record ObjGroup(string Name, ModelMeshPart Mesh, ModelSubmesh Submesh, string Material);

/// <summary>
/// Writes meshes as Wavefront OBJ for Substance Painter. Painter makes one texture set per material
/// name. Positions go from game metres to Painter's centimetres, and V flips from the game's top-left
/// UV origin to OBJ's bottom-left one (the add-on's importer flips the same way).
/// </summary>
public static class ObjWriter
{
    public const float Scale = 100f;

    /// <param name="uvScales">
    /// Per material, how often the texture fits its square texture set across and down: UVs shrink
    /// by it into the set's top-left corner.
    /// </param>
    public static void Write(TextWriter obj, string materialLibrary, IReadOnlyList<ObjGroup> groups,
        IReadOnlyDictionary<string, (int U, int V)>? uvScales = null)
    {
        var c = CultureInfo.InvariantCulture;
        obj.WriteLine("# XIV Instant Edit mesh for Substance Painter");
        obj.WriteLine($"mtllib {materialLibrary}");
        var bases = new Dictionary<ModelMeshPart, int>(ReferenceEqualityComparer.Instance);
        var next = 1;
        foreach (var group in groups)
        {
            if (!bases.TryGetValue(group.Mesh, out var first))
            {
                first = next;
                bases[group.Mesh] = first;
                var mesh = group.Mesh;
                for (var v = 0; v < mesh.Positions.Length; v++)
                {
                    var p = mesh.Positions[v] * Scale;
                    obj.Write("v ");
                    obj.Write(p.X.ToString("R", c)); obj.Write(' ');
                    obj.Write(p.Y.ToString("R", c)); obj.Write(' ');
                    obj.WriteLine(p.Z.ToString("R", c));
                }
                var (tileU, tileV) = TileOffset(mesh.Uvs);
                var (scaleU, scaleV) = uvScales is not null && uvScales.TryGetValue(group.Material, out var scale) ? scale : (1, 1);
                for (var v = 0; v < mesh.Uvs.Length; v++)
                {
                    var uv = mesh.Uvs[v];
                    obj.Write("vt ");
                    obj.Write(((uv.X - tileU) / scaleU).ToString("R", c)); obj.Write(' ');
                    obj.WriteLine((1f - (uv.Y - tileV) / scaleV).ToString("R", c));
                }
                for (var v = 0; v < mesh.Normals.Length; v++)
                {
                    var n = mesh.Normals[v];
                    obj.Write("vn ");
                    obj.Write(n.X.ToString("R", c)); obj.Write(' ');
                    obj.Write(n.Y.ToString("R", c)); obj.Write(' ');
                    obj.WriteLine(n.Z.ToString("R", c));
                }
                next += mesh.Positions.Length;
            }

            obj.WriteLine($"o {Clean(group.Name)}");
            obj.WriteLine($"usemtl {group.Material}");
            var indices = group.Submesh.Indices;
            for (var i = 0; i + 2 < indices.Length; i += 3)
            {
                obj.Write('f');
                for (var k = 0; k < 3; k++)
                {
                    var index = (first + indices[i + k]).ToString(c);
                    obj.Write(' ');
                    obj.Write(index); obj.Write('/'); obj.Write(index); obj.Write('/'); obj.Write(index);
                }
                obj.WriteLine();
            }
        }
    }

    /// <summary>
    /// The whole UV tile a mesh sits in. Vanilla bodies, for one, lay their skin out in the U = 1..2
    /// tile; the texture repeats, so moving a mesh by whole tiles into the 0..1 tile Painter paints
    /// changes nothing in game.
    /// </summary>
    public static (float U, float V) TileOffset(IReadOnlyList<System.Numerics.Vector2> uvs)
    {
        if (uvs.Count == 0)
            return (0, 0);
        float minU = float.MaxValue, maxU = float.MinValue, minV = float.MaxValue, maxV = float.MinValue;
        foreach (var uv in uvs)
        {
            minU = Math.Min(minU, uv.X); maxU = Math.Max(maxU, uv.X);
            minV = Math.Min(minV, uv.Y); maxV = Math.Max(maxV, uv.Y);
        }
        return (MathF.Floor((minU + maxU) / 2), MathF.Floor((minV + maxV) / 2));
    }

    /// <summary>
    /// The groups without triangles that repeat an earlier triangle of the same material, either as
    /// is or with reversed winding. Models draw double-sided cards (brows, lashes, hair) as a front
    /// and a reversed back copy; Painter shows both copies fighting over the same place, while the
    /// texels they paint are the same. Materials that lost back copies are added to
    /// <paramref name="twoSided"/>, to be drawn double-sided instead.
    /// </summary>
    public static IReadOnlyList<ObjGroup> WithoutBackFaceCopies(IReadOnlyList<ObjGroup> groups, ISet<string> twoSided)
    {
        var seen = new Dictionary<string, HashSet<(Vector3, Vector3, Vector3)>>(StringComparer.Ordinal);
        var result = new List<ObjGroup>(groups.Count);
        foreach (var group in groups)
        {
            if (!seen.TryGetValue(group.Material, out var triangles))
                seen[group.Material] = triangles = [];
            var positions = group.Mesh.Positions;
            var indices = group.Submesh.Indices;
            var kept = new List<int>(indices.Length);
            for (var i = 0; i + 2 < indices.Length; i += 3)
            {
                var a = positions[indices[i]];
                var b = positions[indices[i + 1]];
                var c = positions[indices[i + 2]];
                if (triangles.Contains(Canonical(a, c, b)) && !triangles.Contains(Canonical(a, b, c)))
                {
                    twoSided.Add(group.Material);
                    continue;
                }
                if (!triangles.Add(Canonical(a, b, c)))
                    continue;
                kept.Add(indices[i]);
                kept.Add(indices[i + 1]);
                kept.Add(indices[i + 2]);
            }
            if (kept.Count == indices.Length - indices.Length % 3)
                result.Add(group);
            else if (kept.Count > 0)
                result.Add(group with { Submesh = group.Submesh with { Indices = kept.ToArray() } });
        }
        return result;
    }

    // The triangle's corners, starting from the smallest, so rotations of one winding compare equal.
    private static (Vector3, Vector3, Vector3) Canonical(Vector3 a, Vector3 b, Vector3 c)
    {
        var first = (a, b, c);
        var second = (b, c, a);
        var third = (c, a, b);
        var best = Less(second.Item1, first.Item1) ? second : first;
        return Less(third.Item1, best.Item1) ? third : best;
    }

    private static bool Less(Vector3 x, Vector3 y)
        => x.X != y.X ? x.X < y.X : x.Y != y.Y ? x.Y < y.Y : x.Z < y.Z;

    public static void WriteMaterialLibrary(TextWriter mtl, IEnumerable<string> materials)
    {
        foreach (var material in materials.Distinct(StringComparer.Ordinal))
            mtl.WriteLine($"newmtl {material}");
    }

    /// <summary> Object name for a submesh: model, mesh.submesh, and its attributes so Painter's geometry mask can hide variants. </summary>
    public static string GroupName(string model, ModelMeshPart mesh, ModelSubmesh submesh, IReadOnlyList<string> attributes)
    {
        var names = new List<string>();
        for (var bit = 0; bit < 32 && bit < attributes.Count; bit++)
            if ((submesh.AttributeMask & (1u << bit)) != 0)
                names.Add(attributes[bit]);
        var local = Math.Max(0, mesh.Submeshes.ToList().IndexOf(submesh));
        var name = $"{model} {mesh.MeshIndex}.{local.ToString(CultureInfo.InvariantCulture)}";
        return names.Count == 0 ? name : $"{name} [{string.Join(", ", names)}]";
    }

    private static string Clean(string name)
    {
        var chars = name.Select(ch => char.IsControl(ch) ? ' ' : ch).ToArray();
        var cleaned = new string(chars).Trim();
        return cleaned.Length == 0 ? "mesh" : cleaned;
    }
}
