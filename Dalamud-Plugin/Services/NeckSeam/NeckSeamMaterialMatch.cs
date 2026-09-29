namespace InstantEdit.Services.NeckSeam;

/// <summary>
/// The skin settings that differ between the face and body materials, and where to make them meet.
/// A meeting point of 0 keeps the face's values (only the body material changes), 1 takes the
/// body's (only the face material changes), and values between move both. The detail tile size
/// meets in tiles per metre on a log scale, since the face and body map the tile over different
/// UV densities; the tile pattern index can't be blended and goes to the nearer side. Body seams
/// use it too, with their first part in the face's place and the second in the body's.
/// </summary>
internal sealed class NeckSeamMaterialMatch
{
    public required float[] FaceTileScale { get; init; }
    public required float[] BodyTileScale { get; init; }
    /// <summary> UV units per metre next to the seam: the face's second UV set, the body's first. </summary>
    public required float FaceDensity { get; init; }
    public required float BodyDensity { get; init; }
    public required bool TileScaleOff { get; init; }
    public required float FaceTileAlpha { get; init; }
    public required float BodyTileAlpha { get; init; }
    /// <summary> The body normal map's alpha at the seam, which scales the body's tile strength. </summary>
    public required float BodyNormalAlpha { get; init; }
    /// <summary> The same for the face side: 1 for a face (whose normal alpha is the lip mask), the normal map's alpha for a body part. </summary>
    public float FaceNormalAlpha { get; init; } = 1f;
    public required bool TileAlphaOff { get; init; }
    public required float FaceTileIndex { get; init; }
    public required float BodyTileIndex { get; init; }
    public required bool TileIndexOff { get; init; }
    /// <summary> Other skin constants that differ, as (face, body) values. </summary>
    public required IReadOnlyDictionary<uint, (float[] Face, float[] Body)> Other { get; init; }

    public bool Any => TileScaleOff || TileAlphaOff || TileIndexOff || Other.Count > 0;
    public float FaceTiles => FaceTileScale.Length == 0 ? 0 : FaceTileScale.Average() * FaceDensity;
    public float BodyTiles => BodyTileScale.Length == 0 ? 0 : BodyTileScale.Average() * BodyDensity;
    /// <summary> The body's tile strength as the shader applies it at the seam. </summary>
    public float BodyTileStrength => BodyTileAlpha * BodyNormalAlpha;
    public float FaceTileStrength => FaceTileAlpha * FaceNormalAlpha;

    /// <summary> Constant changes for each material to meet at <paramref name="meet"/> (0 = face's values, 1 = body's). </summary>
    public (Dictionary<uint, float[]> Face, Dictionary<uint, float[]> Body) Plan(float meet)
    {
        meet = Math.Clamp(meet, 0f, 1f);
        var face = new Dictionary<uint, float[]>();
        var body = new Dictionary<uint, float[]>();

        if (TileScaleOff && FaceTiles > 0 && BodyTiles > 0)
        {
            var target = TargetTiles(meet);
            if (MathF.Abs(target / FaceTiles - 1) > 0.005f)
                face[SkinMaterial.TileScale] = FaceTileScale.Select(s => MathF.Round(s * target / FaceTiles, 2)).ToArray();
            if (MathF.Abs(target / BodyTiles - 1) > 0.005f)
                body[SkinMaterial.TileScale] = BodyTileScale.Select(s => MathF.Round(s * target / BodyTiles, 2)).ToArray();
        }

        if (TileAlphaOff)
        {
            var strength = FaceTileStrength + (BodyTileStrength - FaceTileStrength) * meet;
            var faceAlpha = FaceNormalAlpha > 0.05f ? strength / FaceNormalAlpha : strength;
            if (MathF.Abs(faceAlpha - FaceTileAlpha) > 0.001f)
                face[SkinMaterial.TileAlpha] = [MathF.Round(faceAlpha, 3)];
            var bodyAlpha = BodyNormalAlpha > 0.05f ? strength / BodyNormalAlpha : strength;
            if (MathF.Abs(bodyAlpha - BodyTileAlpha) > 0.001f)
                body[SkinMaterial.TileAlpha] = [MathF.Round(bodyAlpha, 3)];
        }

        if (TileIndexOff)
        {
            if (meet >= 0.5f)
                face[SkinMaterial.TileIndex] = [BodyTileIndex];
            else
                body[SkinMaterial.TileIndex] = [FaceTileIndex];
        }

        foreach (var (id, (faceValues, bodyValues)) in Other)
        {
            var values = faceValues.Zip(bodyValues, (f, b) => MathF.Round(f + (b - f) * meet, 4)).ToArray();
            if (values.Zip(faceValues).Any(p => MathF.Abs(p.First - p.Second) > 1e-4f))
                face[id] = values;
            if (values.Zip(bodyValues).Any(p => MathF.Abs(p.First - p.Second) > 1e-4f))
                body[id] = values;
        }
        return (face, body);
    }

    /// <summary> The detail tile repeats per metre where both meet: a log-scale blend of the face's and the body's. </summary>
    public float TargetTiles(float meet)
        => FaceTiles > 0 && BodyTiles > 0 ? MathF.Exp(MathF.Log(FaceTiles) + (MathF.Log(BodyTiles) - MathF.Log(FaceTiles)) * Math.Clamp(meet, 0f, 1f)) : 0;
}
