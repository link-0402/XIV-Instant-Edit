using InstantEdit.Services.Painter;

namespace InstantEdit.Services.Previews;

/// <summary>
/// The bones a model's skin weights move, by submesh, so the parts the game hides can be left out: a bone
/// counts for a submesh when one of its vertices has weight on it.
/// </summary>
public sealed class ModelBones
{
    private readonly (uint AttributeMask, HashSet<string> Bones)[] submeshes;

    private ModelBones((uint, HashSet<string>)[] submeshes) => this.submeshes = submeshes;

    /// <summary>The bones each submesh of <paramref name="mesh"/>, read with its skin, has weight on.</summary>
    public static ModelBones Of(ModelMesh mesh)
    {
        var parts = new List<(uint, HashSet<string>)>();
        foreach (var part in mesh.Meshes)
            foreach (var submesh in part.Submeshes)
            {
                var bones = new HashSet<string>(StringComparer.Ordinal);
                if (part.Skin is { Influences: > 0 } skin)
                    foreach (var vertex in submesh.Indices)
                        for (var slot = vertex * skin.Influences; slot < (vertex + 1) * skin.Influences && slot < skin.Bones.Length; slot++)
                            if (skin.Bones[slot] is var bone and >= 0 && bone < mesh.Bones.Count && skin.Weights[slot] > 0)
                                bones.Add(mesh.Bones[bone]);
                parts.Add((submesh.AttributeMask, bones));
            }
        return new ModelBones([.. parts]);
    }

    /// <summary>The bones the submeshes drawn with <paramref name="masks"/> have weight on; every submesh's without masks.</summary>
    public IReadOnlySet<string> Weighted(IReadOnlyList<uint>? masks)
    {
        var bones = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (mask, weighted) in submeshes)
            if (PainterVisibility.Draws(masks, new ModelSubmesh(-1, [], mask)))
                bones.UnionWith(weighted);
        return bones;
    }
}
