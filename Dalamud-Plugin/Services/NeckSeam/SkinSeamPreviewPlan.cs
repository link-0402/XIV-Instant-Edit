namespace InstantEdit.Services.NeckSeam;

/// <summary> The neck's and the body seams' fixes, which go into one preview mod. </summary>
internal sealed record SkinSeamFix(NeckSeamFix? Neck, BodySeamFix? Body)
{
    public bool Empty => (Neck?.Empty ?? true) && (Body?.Empty ?? true);
    public IReadOnlyList<string> Changes => [.. Neck?.Changes ?? [], .. Body?.Changes ?? []];
}

/// <summary>
/// The files a skin seam preview holds, before its textures are encoded: the changed models, the
/// materials to write (changed ones, and unchanged ones that name a changed texture, so they can point
/// at the preview's copy), and the changed textures with the materials that read them. When the neck
/// and a body seam both change the body's skin material, the body seams' version already holds the
/// neck's change, since it was built on it.
/// </summary>
internal sealed class SkinSeamPreviewPlan
{
    public required IReadOnlyList<SeamFileOutput> Models { get; init; }
    public required IReadOnlyList<SeamFileOutput> Materials { get; init; }
    public required IReadOnlyList<SeamTextureOutput> Textures { get; init; }

    public static SkinSeamPreviewPlan Build(NeckSeamAnalysis analysis, SkinSeamFix fix)
    {
        var models = new List<SeamFileOutput>();
        var materials = new Dictionary<string, SeamFileOutput>(StringComparer.OrdinalIgnoreCase);
        var textures = new List<SeamTextureOutput>();
        if (fix.Neck is { } neck && analysis.Report is { } report)
        {
            foreach (var texture in neck.Textures)
                textures.Add(new SeamTextureOutput("face " + NeckSeamFixer.Label(texture.Sampler) + " texture", texture.Sampler, texture.GamePath,
                    texture.Original, texture.Image, [report.FaceMaterialPath]));
            if (neck.Material is { } faceMaterial)
                materials[report.FaceMaterialPath] = new SeamFileOutput("face material", report.FaceMaterialPath, faceMaterial);
            // The body's skin material is shared by every body part, so its path is kept: the whole body previews the change.
            if (neck.BodyMaterial is { } bodyMaterial)
                materials[report.BodyMaterialPath] = new SeamFileOutput("body material", report.BodyMaterialPath, bodyMaterial);
            if (neck.Model is { } faceModel)
                models.Add(new SeamFileOutput("face model", report.FaceModelPath, faceModel));
        }
        if (fix.Body is { } body)
        {
            models.AddRange(body.Models);
            foreach (var material in body.Materials)
                materials[material.GamePath] = materials.TryGetValue(material.GamePath, out var neckVersion) ? material with { Kind = neckVersion.Kind } : material;
            textures.AddRange(body.Textures);
        }
        foreach (var texture in textures)
            foreach (var owner in texture.Materials)
                if (!materials.ContainsKey(owner) && Original(analysis, owner) is { } original)
                    materials[owner] = new SeamFileOutput(string.Equals(owner, analysis.Report?.FaceMaterialPath, StringComparison.OrdinalIgnoreCase)
                        ? "face material" : "skin material", owner, original);
        return new SkinSeamPreviewPlan { Models = models, Materials = materials.Values.ToList(), Textures = textures };
    }

    /// <summary> A material's bytes as the analysis read them. </summary>
    private static byte[]? Original(NeckSeamAnalysis analysis, string gamePath)
    {
        var input = analysis.Captured.Input;
        IEnumerable<NeckSeamModelInput> models = input.Face is { } face ? [face, .. input.Bodies] : input.Bodies;
        return models.SelectMany(m => m.Materials)
            .FirstOrDefault(m => string.Equals(PathRules.NormalizeGamePath(m.GamePath), gamePath, StringComparison.OrdinalIgnoreCase))?.Bytes;
    }

    /// <summary>
    /// Where the preview's copy of <paramref name="texture"/> goes: the path the game requests for it,
    /// next to the original with the preview's tag, as the first material that reads it stores it.
    /// </summary>
    public string? PreviewPath(SeamTextureOutput texture, string tag)
    {
        foreach (var owner in texture.Materials)
        {
            if (Materials.FirstOrDefault(m => string.Equals(m.GamePath, owner, StringComparison.OrdinalIgnoreCase)) is not { } material)
                continue;
            var parsed = SkinMaterial.Read(material.Bytes);
            for (var i = 0; i < parsed.Textures.Count; i++)
                if (string.Equals(PathRules.Dx11TexturePath(parsed.Textures[i], parsed.TextureFlags[i]), texture.GamePath, StringComparison.OrdinalIgnoreCase))
                    return PathRules.Dx11TexturePath(NeckSeamService.PreviewTexturePath(parsed.Textures[i], tag), parsed.TextureFlags[i]);
        }
        return null;
    }

    /// <summary>
    /// The texture path rewrites that point a material at the preview's copies: each stored path of a
    /// changed texture (by the path the game requests) → the copy's stored path.
    /// </summary>
    public static Dictionary<string, string> Rewrites(byte[] material, IReadOnlySet<string> changed, string tag)
    {
        var rewrites = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var parsed = SkinMaterial.Read(material);
        for (var i = 0; i < parsed.Textures.Count; i++)
            if (changed.Contains(PathRules.Dx11TexturePath(parsed.Textures[i], parsed.TextureFlags[i])))
                rewrites[parsed.Textures[i]] = NeckSeamService.PreviewTexturePath(parsed.Textures[i], tag);
        return rewrites;
    }
}
