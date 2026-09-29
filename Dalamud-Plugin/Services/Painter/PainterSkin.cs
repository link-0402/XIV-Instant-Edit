using InstantEdit.Services.NeckSeam;
using InstantEdit.Services.Previews;

namespace InstantEdit.Services.Painter;

/// <summary> A model the character has loaded, read for a skin project. </summary>
/// <param name="Masks">The character's enabled attributes for the model; empty when every part counts.</param>
internal sealed record PainterSkinCandidate(PainterModelRef Model, ModelMesh Mesh, ModelTexturePlan Plan, IReadOnlyList<uint> Masks);

/// <summary> A model that joins a skin project. </summary>
/// <param name="Candidate">Its index in the candidates.</param>
/// <param name="Face">The face model, which brings the face skin's own texture set.</param>
/// <param name="Materials">Its material names, as the model stores them, that the project paints.</param>
/// <param name="DrawnTriangles">Triangles the character draws with those materials.</param>
internal sealed record PainterSkinModel(int Candidate, bool Face, IReadOnlyList<string> Materials, int DrawnTriangles);

/// <summary> What a skin project paints: its materials (one per game path) and the models drawing them, the main one first. </summary>
internal sealed record PainterSkinSelection(IReadOnlyList<TexturePlanMaterial> Materials, IReadOnlyList<PainterSkinModel> Models);

/// <summary>
/// Picks what a skin project paints: every body skin material (skin.shpk's body types) a model of
/// the character draws, so the parts that meet at the wrists, waist and ankles are painted together,
/// and the face model's face skin, which has its own material and textures. Dalamud-free.
/// </summary>
internal static class PainterSkin
{
    public static bool IsSkin(TexturePlanMaterial material)
        => material.ShaderPackage.Equals(SkinMaterial.SkinShader, StringComparison.OrdinalIgnoreCase);

    // Like SkinMaterial.SkinType: skin.shpk draws a material without the key as face skin.
    private static uint SkinType(TexturePlanMaterial material)
        => material.ShaderKeys.TryGetValue(SkinMaterial.SkinTypeKey, out var value) ? value : SkinMaterial.SkinTypeFace;

    public static bool IsBodySkin(TexturePlanMaterial material)
        => IsSkin(material) && SkinType(material) is SkinMaterial.SkinTypeBody or SkinMaterial.SkinTypeBodyHrothgar;

    public static bool IsFaceSkin(TexturePlanMaterial material)
        => IsSkin(material) && SkinType(material) is SkinMaterial.SkinTypeFace or SkinMaterial.SkinTypeFaceEmissive;

    /// <summary> Triangles the character draws with each of the model's materials, by the material name the model stores. </summary>
    public static Dictionary<string, int> DrawnTriangles(ModelMesh mesh, IReadOnlyList<uint> masks)
    {
        var triangles = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in mesh.Meshes)
        {
            var name = mesh.Materials[part.MaterialIndex];
            var drawn = part.Submeshes.Where(s => s.Indices.Length >= 3 && PainterVisibility.Draws(masks, s)).Sum(s => s.Indices.Length / 3);
            triangles[name] = triangles.GetValueOrDefault(name) + drawn;
        }
        return triangles;
    }

    /// <summary>
    /// The body skin materials the character draws somewhere, and every model using one of them: the
    /// model drawing the most skin leads, and models whose skin is all turned off join with none
    /// drawn. Then the face model, when it draws face skin. No models when no body skin is drawn.
    /// </summary>
    public static PainterSkinSelection Select(IReadOnlyList<PainterSkinCandidate> candidates)
    {
        var drawn = candidates.Select(candidate => DrawnTriangles(candidate.Mesh, candidate.Masks)).ToList();
        var materials = new List<TexturePlanMaterial>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < candidates.Count; i++)
            foreach (var material in candidates[i].Plan.Materials.Where(IsBodySkin))
                if (material.GamePath.Length > 0 && drawn[i].GetValueOrDefault(material.ModelMaterial) > 0 && paths.Add(material.GamePath))
                    materials.Add(material);
        if (materials.Count == 0)
            return new PainterSkinSelection([], []);

        var models = new List<PainterSkinModel>();
        for (var i = 0; i < candidates.Count; i++)
        {
            var names = candidates[i].Plan.Materials.Where(m => IsBodySkin(m) && paths.Contains(m.GamePath)).Select(m => m.ModelMaterial).ToList();
            if (names.Count > 0)
                models.Add(new PainterSkinModel(i, false, names, names.Sum(name => drawn[i].GetValueOrDefault(name))));
        }
        var main = models.MaxBy(model => model.DrawnTriangles)!;
        models.Remove(main);
        models.Insert(0, main);

        for (var i = 0; i < candidates.Count; i++)
        {
            if (!NeckSeamAnalyzer.IsFaceModel(candidates[i].Model.GamePath))
                continue;
            var faces = candidates[i].Plan.Materials
                .Where(m => IsFaceSkin(m) && m.GamePath.Length > 0 && drawn[i].GetValueOrDefault(m.ModelMaterial) > 0 && !paths.Contains(m.GamePath))
                .ToList();
            if (faces.Count == 0)
                continue;
            foreach (var face in faces.Where(face => paths.Add(face.GamePath)))
                materials.Add(face);
            models.Add(new PainterSkinModel(i, true, faces.Select(m => m.ModelMaterial).ToList(),
                faces.Sum(m => drawn[i].GetValueOrDefault(m.ModelMaterial))));
            break;
        }
        return new PainterSkinSelection(materials, models);
    }
}
