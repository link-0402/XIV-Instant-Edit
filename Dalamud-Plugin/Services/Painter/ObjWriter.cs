using System.Globalization;
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

    public static void Write(TextWriter obj, string materialLibrary, IReadOnlyList<ObjGroup> groups)
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
                for (var v = 0; v < mesh.Uvs.Length; v++)
                {
                    var uv = mesh.Uvs[v];
                    obj.Write("vt ");
                    obj.Write((uv.X - tileU).ToString("R", c)); obj.Write(' ');
                    obj.WriteLine((1f - (uv.Y - tileV)).ToString("R", c));
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
