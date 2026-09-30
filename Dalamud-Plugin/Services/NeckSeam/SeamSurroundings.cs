using System.Numerics;
using System.Text.RegularExpressions;
using InstantEdit.Services.Skeletons;

namespace InstantEdit.Services.NeckSeam;

/// <summary>
/// One of the game's seam connectors as drawn: a band of skin (submeshes behind atr_cn_neck,
/// atr_cn_wrist, atr_cn_waist or atr_cn_ankle, in the human body b0002 models) that sits just inside
/// the body under a seam, touching the skin only along the seam line, so a gap there shows skin
/// instead of a hole. Positions are in the character's pose.
/// </summary>
internal sealed record SeamConnectorBand(string Kind, string ModelPath, string MaterialPath, SeamSpace Space, Vector3[] Normals);

/// <summary>
/// What lies around the seams besides the skin being compared: the clothing every loaded model draws,
/// and the seam connectors, all moved into the character's race. The seam checks use it to tell a
/// seam clothing covers, and to check the connector under a seam.
/// </summary>
internal sealed class SeamSurroundings
{
    private static readonly Regex HumanBody = new(@"^chara/human/c\d{4}/obj/body/b\d{4}/", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary> Every drawn triangle that isn't skin: clothing, nails, jewellery. </summary>
    public required SeamSpace Cloth { get; init; }
    public required IReadOnlyList<SeamConnectorBand> Connectors { get; init; }
    /// <summary> Connector kinds whose model is loaded, drawn or not. </summary>
    public required IReadOnlySet<string> Loaded { get; init; }
    /// <summary> Whether the game's draw state was read; without it every connector counts as drawn. </summary>
    public required bool DrawStateKnown { get; init; }

    public static readonly SeamSurroundings None = new()
    {
        Cloth = new SeamSpace([], []), Connectors = [], Loaded = new HashSet<string>(), DrawStateKnown = false,
    };

    /// <summary> A human body model (chara/human/cXXXX/obj/body/bXXXX): the game's seam connectors and its low-poly whole-body model, never a gear part. </summary>
    public static bool IsHumanBodyModel(string gamePath) => HumanBody.IsMatch(PathRules.NormalizeGamePath(gamePath));

    public IEnumerable<SeamConnectorBand> For(string kind) => Connectors.Where(c => string.Equals(c.Kind, kind, StringComparison.Ordinal));

    /// <summary>
    /// The share of <paramref name="points"/> clothing closes in around (see <see cref="SeamSpace.Encloses"/>),
    /// from 0 (none, or nothing drawn is clothing) to 1.
    /// </summary>
    public float Covered(IReadOnlyCollection<(Vector3 Point, Vector3 Normal)> points)
    {
        if (Cloth.Empty || points.Count == 0)
            return 0;
        return points.Count(p => Cloth.Encloses(p.Point, p.Normal)) / (float)points.Count;
    }

    /// <summary> Reads the clothing and the connectors of every model the analysis got, in the character's race (null: the models' own). </summary>
    public static SeamSurroundings Read(NeckSeamInput input, int? characterRace)
    {
        var cloth = new List<(Vector3[], int[])>();
        var connectors = new List<SeamConnectorBand>();
        var loaded = new HashSet<string>(StringComparer.Ordinal);
        var known = true;
        foreach (var model in input.Bodies.Concat(input.Clothing))
        {
            SkinModel parsed;
            try { parsed = model.Read(); }
            catch (Exception e) when (e is InvalidDataException or NotSupportedException) { continue; }
            known &= model.Attributes is not null;
            foreach (var name in parsed.Attributes)
                if (name.StartsWith(SkinModel.ConnectorPrefix, StringComparison.OrdinalIgnoreCase))
                    loaded.Add(name[SkinModel.ConnectorPrefix.Length..].ToLowerInvariant());
            var deformer = RacialDeformer.None;
            if (NeckSeamAnalyzer.RaceOf(model.GamePath) is { } modelRace && characterRace is { } race && modelRace != race && input.RacialDeformers is { } pbd)
            {
                try { deformer = RacialDeformer.Create(pbd, race, modelRace); }
                catch (InvalidDataException) { deformer = RacialDeformer.None; }
            }
            foreach (var mesh in parsed.Meshes)
            {
                var material = NeckSeamAnalyzer.MaterialFor(model, mesh.Material);
                var skin = false;
                if (material is not null)
                {
                    try { skin = SkinMaterial.Read(material.Bytes) is { IsBodySkin: true } or { IsFaceSkin: true }; }
                    catch (InvalidDataException) { skin = false; }
                }
                if (mesh.Connectors.Count == 0 && (skin || mesh.Triangles.Length == 0))
                    continue;
                var (positions, normals, _) = deformer.Deform(mesh);
                if (!skin && mesh.Triangles.Length > 0)
                    cloth.Add((positions, mesh.Triangles));
                foreach (var (kind, triangles) in mesh.Connectors)
                    if (triangles.Length >= 3)
                        connectors.Add(new SeamConnectorBand(kind, PathRules.NormalizeGamePath(model.GamePath),
                            material is null ? string.Empty : PathRules.NormalizeGamePath(material.GamePath), new SeamSpace(positions, triangles), normals));
            }
        }
        return new SeamSurroundings { Cloth = SeamSpace.Of(cloth), Connectors = connectors, Loaded = loaded, DrawStateKnown = known };
    }
}
