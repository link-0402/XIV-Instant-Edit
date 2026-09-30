using InstantEdit.Services.NeckSeam;
using InstantEdit.Services.Previews;

namespace InstantEdit.Services.Painter;

/// <summary> A part of a skin project as read: its model, mesh (shapes applied), texture plan and enabled attributes. </summary>
/// <param name="Part">Torso, Hands, Legs, Feet or Head.</param>
/// <param name="Masks">The enabled attributes; empty when every part counts.</param>
internal sealed record PainterSkinCandidate(string Part, PainterModelRef Model, ModelMesh Mesh, ModelTexturePlan Plan, IReadOnlyList<uint> Masks);

/// <summary>
/// What a skin project paints of its parts: the body skin material the smallclothes of all four
/// body slots share, and the face's own skin. Nothing else goes to Painter. Dalamud-free.
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

    /// <summary> A part's drawn body skin materials, by game path, with the triangles drawn with each. </summary>
    private static Dictionary<string, int> BodySkins(PainterSkinCandidate part)
    {
        var drawn = DrawnTriangles(part.Mesh, part.Masks);
        var skins = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var material in part.Plan.Materials.Where(m => IsBodySkin(m) && m.GamePath.Length > 0))
            if (drawn.GetValueOrDefault(material.ModelMaterial) is > 0 and var triangles)
                skins[material.GamePath] = skins.GetValueOrDefault(material.GamePath) + triangles;
        return skins;
    }

    /// <summary>
    /// The body skin material every body part draws, so paint can cross from one to the next: the
    /// one drawing the most triangles when they share several. Null, with the reason, when a part
    /// draws no body skin or the parts don't share one.
    /// </summary>
    public static TexturePlanMaterial? SharedBodyMaterial(IReadOnlyList<PainterSkinCandidate> parts, out string problem)
    {
        problem = "";
        var skins = parts.Select(BodySkins).ToList();
        for (var i = 0; i < parts.Count; i++)
            if (skins[i].Count == 0)
            {
                problem = $"The {parts[i].Part.ToLowerInvariant()} smallclothes model ({parts[i].Model.FileName}) draws no body skin, " +
                          "so the body parts don't share a skin material to paint across.";
                return null;
            }
        var shared = skins.Skip(1).Aggregate(skins[0].Keys.ToHashSet(StringComparer.OrdinalIgnoreCase), (common, next) =>
        {
            common.IntersectWith(next.Keys);
            return common;
        });
        if (shared.Count == 0)
        {
            var uses = parts.Select((part, i) => $"{part.Part.ToLowerInvariant()}: {string.Join(" and ", skins[i].Keys.Select(Path.GetFileName))}");
            problem = "Your character's body parts don't share one skin material, so paint couldn't cross from one to the next (" +
                      string.Join(", ", uses) + "). Pick body options that use the same skin material in Penumbra.";
            return null;
        }
        var path = shared.MaxBy(p => skins.Sum(s => s.GetValueOrDefault(p)))!;
        return parts.SelectMany(part => part.Plan.Materials).First(m => string.Equals(m.GamePath, path, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The face's own skin: the _fac_a material every vanilla face draws its skin with, or else the
    /// face skin of the face's first mesh. Other face skin materials (horns, ears, teeth) stay out.
    /// </summary>
    public static TexturePlanMaterial? HeadMaterial(PainterSkinCandidate face)
    {
        var drawn = DrawnTriangles(face.Mesh, face.Masks);
        var skins = face.Plan.Materials.Where(m => IsFaceSkin(m) && m.GamePath.Length > 0 && drawn.GetValueOrDefault(m.ModelMaterial) > 0).ToList();
        return skins.FirstOrDefault(m => m.ModelMaterial.EndsWith("_fac_a.mtrl", StringComparison.OrdinalIgnoreCase))
               ?? face.Mesh.Meshes.Select(part => face.Mesh.Materials[part.MaterialIndex])
                   .Select(name => skins.FirstOrDefault(m => string.Equals(m.ModelMaterial, name, StringComparison.OrdinalIgnoreCase)))
                   .FirstOrDefault(m => m is not null);
    }
}
