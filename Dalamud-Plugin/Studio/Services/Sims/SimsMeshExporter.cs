using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;

namespace InstantEdit.Studio.Services.Sims;

/// <summary>
/// Writes a parsed GEOM out as a binary glTF (.glb) for Blender.
///
/// Nothing is converted on the way out: positions, normals, winding and UVs all pass
/// through as they are stored. Sims geometry already matches glTF — Y-up, right-handed,
/// counter-clockwise, with the UV origin at the top left — so any "conversion" here
/// would corrupt it rather than fix it.
///
/// Both halves of that were measured rather than assumed, because both mistakes hide
/// well. Negating an axis and flipping the winding to match mirrors the model, and a
/// mirrored body still looks anatomically correct with its normals still facing
/// outward, so a render will not show it; it was caught by comparing the geometric
/// normal implied by each triangle's vertex order against the normals stored beside it
/// in the file, which agree on every mesh tested. Flipping V puts the whole mesh on the
/// transparent half of the sheet; it was caught by sampling the diffuse at every vertex
/// UV, where the stored orientation covers 57% of the artwork and the flipped one 0%.
/// Do not reintroduce either without re-running those checks.
///
/// Skinning is exported as a flat armature rather than the real rig. A GEOM carries
/// only FNV-32 hashes of the bone names it is weighted to, and the actual bind pose
/// lives in a _RIG resource that CAS packages normally reference rather than contain.
/// Since the mesh is already stored in bind pose, emitting one identity joint per bone
/// gives Blender the vertex groups — usable for transferring weights between meshes
/// from the same package — without needing the rig itself. When the hashes could not
/// be read the joints are numbered instead; the names are cosmetic, what matters is
/// that the same bone keeps the same group across meshes.
/// </summary>
public static class SimsMeshExporter
{
    /// <summary>
    /// Writes <paramref name="mesh"/> to <paramref name="path"/> as a .glb. When
    /// <paramref name="crop"/> is set, UV0 is rescaled to match textures that had their
    /// unused half cropped away, so the model and the images stay in step.
    /// </summary>
    public static void ExportGlb(SimsGeomMesh mesh, string path, string meshName, SimsHalfCrop crop = default)
    {
        if (mesh.VertexCount == 0 || mesh.FaceCount == 0)
            throw new InvalidOperationException("Mesh has no geometry to export.");

        var scene = new SceneBuilder();
        var material = new MaterialBuilder(meshName).WithDoubleSide(true);

        if (mesh.HasSkin)
            BuildSkinned(mesh, scene, material, meshName, crop);
        else
            BuildRigid(mesh, scene, material, meshName, crop);

        scene.ToGltf2().SaveGLB(path);
    }

    private static void BuildRigid(SimsGeomMesh mesh, SceneBuilder scene, MaterialBuilder material,
        string meshName, SimsHalfCrop crop)
    {
        var builder = new MeshBuilder<VertexPositionNormal, VertexTexture1>(meshName);
        var prim = builder.UsePrimitive(material);

        AddTriangles(mesh, (a, b, c) => prim.AddTriangle(
            RigidVertex(mesh, a, crop), RigidVertex(mesh, b, crop), RigidVertex(mesh, c, crop)));

        scene.AddRigidMesh(builder, Matrix4x4.Identity);
    }

    private static void BuildSkinned(SimsGeomMesh mesh, SceneBuilder scene, MaterialBuilder material,
        string meshName, SimsHalfCrop crop)
    {
        var builder = new MeshBuilder<VertexPositionNormal, VertexTexture1, VertexJoints4>(meshName);
        var prim = builder.UsePrimitive(material);

        AddTriangles(mesh, (a, b, c) => prim.AddTriangle(
            SkinnedVertex(mesh, a, crop), SkinnedVertex(mesh, b, crop), SkinnedVertex(mesh, c, crop)));

        // One identity joint per bone; the root keeps them together in Blender.
        var root = new NodeBuilder("Sims_Armature");
        var joints = new List<NodeBuilder>(mesh.BoneCount);
        for (int i = 0; i < mesh.BoneCount; i++)
            joints.Add(root.CreateNode(BoneName(mesh, i)));

        scene.AddSkinnedMesh(builder, Matrix4x4.Identity, joints.ToArray());
    }

    /// <summary>Emits every triangle in its original winding; see the note on the class.</summary>
    private static void AddTriangles(SimsGeomMesh mesh, Action<int, int, int> emit)
    {
        for (int i = 0; i + 2 < mesh.Indices.Length; i += 3)
        {
            int a = mesh.Indices[i], b = mesh.Indices[i + 1], c = mesh.Indices[i + 2];
            if (a >= mesh.VertexCount || b >= mesh.VertexCount || c >= mesh.VertexCount)
                continue;
            emit(a, b, c);
        }
    }

    private static (VertexPositionNormal, VertexTexture1) RigidVertex(SimsGeomMesh mesh, int i, SimsHalfCrop crop)
        => (Geometry(mesh, i), Uv(mesh, i, crop));

    private static (VertexPositionNormal, VertexTexture1, VertexJoints4) SkinnedVertex(SimsGeomMesh mesh, int i, SimsHalfCrop crop)
        => (Geometry(mesh, i), Uv(mesh, i, crop), Joints(mesh, i));

    private static VertexPositionNormal Geometry(SimsGeomMesh mesh, int i)
    {
        var position = mesh.Positions[i];

        if (mesh.Normals.Length == 0)
            return new VertexPositionNormal(position, Vector3.UnitY);

        var normal = mesh.Normals[i];
        if (normal.LengthSquared() < 1e-8f)
            normal = Vector3.UnitY;

        return new VertexPositionNormal(position, Vector3.Normalize(normal));
    }

    /// <summary>
    /// UV0, rescaled when the textures were cropped. Only channel 0 is exported: the Sims
    /// stores a second channel whose U runs to -1, so it is not a texture coordinate.
    /// </summary>
    private static VertexTexture1 Uv(SimsGeomMesh mesh, int i, SimsHalfCrop crop)
    {
        if (mesh.UvChannels.Count == 0 || mesh.UvChannels[0].Length <= i)
            return new VertexTexture1(Vector2.Zero);

        var uv = mesh.UvChannels[0][i];
        return crop.Crops
            ? new VertexTexture1(new Vector2(uv.X, crop.RemapV(uv.Y)))
            : new VertexTexture1(uv);
    }

    private static VertexJoints4 Joints(SimsGeomMesh mesh, int i)
    {
        var indices = mesh.BoneIndices!;
        var weights = mesh.BoneWeights!;

        var bindings = new List<(int, float)>(4);
        for (int k = 0; k < 4; k++)
        {
            int at = i * 4 + k;
            if (at >= indices.Length || at >= weights.Length)
                break;

            float weight = weights[at];
            int bone = indices[at];
            if (weight <= 0f || bone >= mesh.BoneCount)
                continue;

            bindings.Add((bone, weight));
        }

        // A vertex with no usable weights would vanish from every group, so pin it to
        // the first bone rather than leaving it unbound.
        if (bindings.Count == 0)
            bindings.Add((0, 1f));

        return new VertexJoints4(bindings.ToArray());
    }

    /// <summary>
    /// Names a joint after its bone. GEOM stores only the FNV-32 hash of the name and
    /// the table mapping those back to names is not in the package, so the hash itself
    /// serves as the name; when the hash list could not be read the index does instead.
    /// Either way the same bone gets the same name across every mesh in the package,
    /// which is what makes the groups useful for transferring weights.
    /// </summary>
    private static string BoneName(SimsGeomMesh mesh, int index)
        => index < mesh.BoneHashes.Length ? $"bone_{mesh.BoneHashes[index]:X8}" : $"bone_{index:D2}";
}
