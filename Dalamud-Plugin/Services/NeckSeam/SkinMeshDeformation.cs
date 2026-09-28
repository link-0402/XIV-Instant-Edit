using System.Numerics;
using InstantEdit.Services.Skeletons;

namespace InstantEdit.Services.NeckSeam;

internal static class SkinMeshDeformation
{
    /// <summary>
    /// Skins a mesh's bind pose through the deformer: positions, normals and binormals blended by the
    /// vertex weights, as the game's vertex shader does with its joint matrices (directions use the
    /// same matrices and are renormalized).
    /// </summary>
    public static (Vector3[] Positions, Vector3[] Normals, Vector3[] Binormals) Deform(this RacialDeformer deformer, SkinMesh mesh)
    {
        var count = mesh.VertexCount;
        if (deformer.BoneCount == 0)
            return (mesh.Positions, mesh.Normals, mesh.Binormals);
        var positions = new Vector3[count];
        var normals = new Vector3[count];
        var binormals = new Vector3[count];
        var matrices = mesh.Bones.Select(deformer.For).ToArray();
        for (var v = 0; v < count; v++)
        {
            var at = v * mesh.Influences;
            var matrix = RacialDeformer.Blend(matrices, mesh.BlendIndices.AsSpan(at, mesh.Influences), mesh.BlendWeights.AsSpan(at, mesh.Influences));
            positions[v] = matrix.Point(mesh.Positions[v]);
            var normal = matrix.Direction(mesh.Normals[v]);
            var binormal = matrix.Direction(mesh.Binormals[v]);
            normals[v] = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : mesh.Normals[v];
            binormals[v] = binormal.LengthSquared() > 1e-12f ? Vector3.Normalize(binormal) : mesh.Binormals[v];
        }
        return (positions, normals, binormals);
    }
}
