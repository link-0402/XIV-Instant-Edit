using System.Numerics;

namespace InstantEdit.Services.NeckSeam;

/// <summary> Which parts of the fix to build, and how far above the seam the face textures are blended. </summary>
/// <param name="Meet">Where the skin settings meet: 0 keeps the face's (only the body material changes), 1 takes the body's (only the face material changes).</param>
internal sealed record NeckSeamFixOptions(bool NeckMorph, bool Material, bool Textures, float BandMetres = NeckSeamFixer.DefaultBand, float Meet = 0.5f);

/// <summary> A changed face texture: the path the face material requests, the original file and the new pixels. </summary>
internal sealed record NeckSeamTextureOutput(uint Sampler, string GamePath, byte[] Original, SeamImage Image);

/// <summary> The fixed face files and the seam measured again with them. </summary>
internal sealed class NeckSeamFix
{
    public byte[]? Model { get; init; }
    /// <summary> The face material with the new constants and its original texture paths. </summary>
    public byte[]? Material { get; init; }
    /// <summary> The body skin material with the new constants, when the settings meet away from the body's. </summary>
    public byte[]? BodyMaterial { get; init; }
    public required IReadOnlyList<NeckSeamTextureOutput> Textures { get; init; }
    public required IReadOnlyList<string> Changes { get; init; }
    public required NeckSeamAnalyzer.TextureComparison Before { get; init; }
    public required NeckSeamAnalyzer.TextureComparison After { get; init; }
    public int TexelsChanged { get; init; }
    public bool Empty => Model is null && Material is null && BodyMaterial is null && Textures.Count == 0;
}

/// <summary>
/// Builds the neck seam fix from a <see cref="NeckSeamReport"/>: connection vertices for the face
/// model, the body's detail tile and skin settings for the face material, and face textures blended
/// into the body at the neck. The blend measures the body-minus-face difference along the seam,
/// smooths it over a centimetre (so the body's pores aren't copied), and adds it to the face texels
/// with a smooth falloff to zero at <see cref="NeckSeamFixOptions.BandMetres"/> above the seam.
/// Normals are rotated in world space with the shader's tangent frame and written back per texel.
/// </summary>
internal static class NeckSeamFixer
{
    public const float DefaultBand = 0.02f;
    /// <summary> How far the correction spreads into the UV padding around the face's islands, so filtering and mips see it. </summary>
    public const int PaddingTexels = 16;

    public static NeckSeamFix Build(NeckSeamReport report, NeckSeamFixOptions options)
    {
        var changes = new List<string>();
        byte[]? model = null;
        if (options.NeckMorph && report.NeckMorphs.Count > 0)
        {
            model = SkinModel.Read(report.FaceModelBytes).WithNeckMorphs(report.NeckMorphs);
            changes.Add($"Face model: add {report.NeckMorphs.Count} neck connection vertices");
        }
        byte[]? material = null, bodyMaterial = null;
        if (options.Material && report.Material.Any)
        {
            var (faceChanges, bodyChanges) = report.Material.Plan(options.Meet);
            if (faceChanges.Count > 0)
            {
                material = SkinMaterial.Read(report.FaceMaterialBytes).WithConstants(faceChanges);
                changes.Add("Face material: " + Describe(faceChanges));
            }
            if (bodyChanges.Count > 0)
            {
                bodyMaterial = SkinMaterial.Read(report.BodyMaterialBytes).WithConstants(bodyChanges);
                changes.Add("Body material: " + Describe(bodyChanges));
            }
        }

        var snapped = report.HasNeckMorphs || (options.NeckMorph && report.NeckMorphs.Count > 0);
        var before = NeckSeamAnalyzer.CompareTextures(report.Seam, report.FaceImages, report.BodyImages, snapped);
        var textures = new List<NeckSeamTextureOutput>();
        var texels = 0;
        var images = report.FaceImages.ToDictionary(p => p.Key, p => p.Value);
        if (options.Textures && report.TexturesDiffer)
        {
            var blended = Blend(report, snapped, Math.Clamp(options.BandMetres, 0.005f, 0.06f), out texels);
            foreach (var (sampler, image) in blended)
            {
                images[sampler] = image;
                var (path, bytes) = report.FaceTextures[sampler];
                textures.Add(new NeckSeamTextureOutput(sampler, path, bytes, image));
                changes.Add($"Face {Label(sampler)} texture: blend {texels:N0} texels into the body at the neck");
            }
        }
        var after = NeckSeamAnalyzer.CompareTextures(report.Seam, images, report.BodyImages, snapped);
        return new NeckSeamFix
        {
            Model = model, Material = material, BodyMaterial = bodyMaterial, Textures = textures, Changes = changes, Before = before, After = after, TexelsChanged = texels,
        };
    }

    public static string Describe(IReadOnlyDictionary<uint, float[]> changes)
        => string.Join(", ", changes.Select(c =>
            $"{SkinMaterial.Names.GetValueOrDefault(c.Key, $"0x{c.Key:X8}")} {string.Join(", ", c.Value.Select(v => v.ToString("0.###")))}"));

    public static string Label(uint sampler) => sampler switch
    {
        SkinMaterial.DiffuseSampler => "diffuse",
        SkinMaterial.NormalSampler => "normal",
        SkinMaterial.MaskSampler => "mask",
        _ => "other",
    };

    /// <summary> A texel near the seam: its distance to the seam, the nearest seam sample and its shading frame. </summary>
    internal readonly record struct Texel(float Distance, int Seam, NeckSeamAnalyzer.TangentFrame Frame);

    private static Dictionary<uint, SeamImage> Blend(NeckSeamReport report, bool snapped, float band, out int changed)
    {
        var seam = report.Seam;
        var face = report.FaceImages;
        var body = report.BodyImages;
        var result = new Dictionary<uint, SeamImage>();
        changed = 0;

        // Seam differences, smoothed along the seam.
        Vector4[]? Delta(uint sampler)
        {
            if (!face.TryGetValue(sampler, out var f) || !body.TryGetValue(sampler, out var b))
                return null;
            var d = new Vector4[seam.Count];
            for (var i = 0; i < seam.Count; i++)
                d[i] = b.Sample(seam.BodyUv[i]) - f.Sample(seam.FaceUv[i]);
            return NeckSeamAnalyzer.Smooth(seam, d);
        }
        var diffuseDelta = Delta(SkinMaterial.DiffuseSampler);
        var maskDelta = Delta(SkinMaterial.MaskSampler);
        var normalDelta = Delta(SkinMaterial.NormalSampler);
        Vector3[]? source = null, target = null;
        if (face.TryGetValue(SkinMaterial.NormalSampler, out var faceNormal) && body.TryGetValue(SkinMaterial.NormalSampler, out var bodyNormal))
        {
            var src = new Vector4[seam.Count];
            var dst = new Vector4[seam.Count];
            for (var i = 0; i < seam.Count; i++)
            {
                var n = seam.FaceNormal[i];
                src[i] = new Vector4(NeckSeamAnalyzer.ToWorld(faceNormal.Sample(seam.FaceUv[i]), NeckSeamAnalyzer.Frame(n, seam.FaceBinormal[i], seam.FaceSign[i])), 0);
                dst[i] = new Vector4(NeckSeamAnalyzer.ToWorld(bodyNormal.Sample(seam.BodyUv[i]),
                    NeckSeamAnalyzer.Frame(snapped ? n : seam.BodyNormal[i], seam.BodyBinormal[i], seam.BodySign[i])), 0);
            }
            source = NeckSeamAnalyzer.Smooth(seam, src).Select(v => NeckSeamAnalyzer.SafeNormalize(new Vector3(v.X, v.Y, v.Z), Vector3.UnitY)).ToArray();
            target = NeckSeamAnalyzer.Smooth(seam, dst).Select(v => NeckSeamAnalyzer.SafeNormalize(new Vector3(v.X, v.Y, v.Z), Vector3.UnitY)).ToArray();
        }

        var surface = new SeamSurface(report.FaceSide.Positions, report.FaceSide.Normals, report.FaceSide.Binormals, report.FaceSide.Mesh.BinormalSigns,
            report.FaceSide.Mesh.Uv1, report.FaceSide.Mesh.Triangles);
        foreach (var group in face.Where(p => report.TexturesToBlend.Contains(p.Key))
                     .GroupBy(p => (p.Value.Width, p.Value.Height, p.Value.AddressU, p.Value.AddressV)))
        {
            var width = group.Key.Width;
            var field = Field(surface, seam.Positions, group.First().Value, band);
            foreach (var (sampler, original) in group)
            {
                var image = original.Clone();
                var any = false;
                foreach (var (pixel, texel) in field)
                {
                    var w = Falloff(texel.Distance, band);
                    if (w <= 1e-3f)
                        continue;
                    int x = pixel % width, y = pixel / width;
                    var value = image.Texel(x, y);
                    switch (sampler)
                    {
                        case SkinMaterial.DiffuseSampler when diffuseDelta is not null:
                            value += new Vector4(diffuseDelta[texel.Seam].X, diffuseDelta[texel.Seam].Y, diffuseDelta[texel.Seam].Z, 0) * w;
                            break;
                        case SkinMaterial.MaskSampler when maskDelta is not null:
                            value += new Vector4(maskDelta[texel.Seam].X, maskDelta[texel.Seam].Y, maskDelta[texel.Seam].Z, 0) * w;
                            break;
                        case SkinMaterial.NormalSampler when normalDelta is not null && source is not null && target is not null:
                            var world = NeckSeamAnalyzer.ToWorld(value, texel.Frame);
                            var rotated = Rotate(world, source[texel.Seam], target[texel.Seam], w);
                            var rg = NeckSeamAnalyzer.ToTexel(rotated, texel.Frame);
                            value = new Vector4(rg.X, rg.Y, value.Z + normalDelta[texel.Seam].Z * w, value.W);
                            break;
                        default:
                            continue;
                    }
                    image.SetTexel(x, y, Vector4.Clamp(value, Vector4.Zero, Vector4.One));
                    any = true;
                }
                if (any)
                {
                    result[sampler] = image;
                    changed = Math.Max(changed, field.Count);
                }
            }
        }
        return result;
    }

    internal static float Falloff(float distance, float band)
    {
        var x = Math.Clamp(distance / band, 0f, 1f);
        return 1f - x * x * (3f - 2f * x);
    }

    /// <summary> Rotates <paramref name="v"/> by <paramref name="amount"/> of the rotation that takes <paramref name="from"/> to <paramref name="to"/>. </summary>
    internal static Vector3 Rotate(Vector3 v, Vector3 from, Vector3 to, float amount)
    {
        var axis = Vector3.Cross(from, to);
        var sin = axis.Length();
        if (sin < 1e-7f)
            return v;
        var angle = MathF.Atan2(sin, Vector3.Dot(from, to)) * amount;
        var k = axis / sin;
        var c = MathF.Cos(angle);
        return v * c + Vector3.Cross(k, v) * MathF.Sin(angle) + k * Vector3.Dot(k, v) * (1 - c);
    }

    /// <summary>
    /// For each texel of <paramref name="image"/> within <paramref name="band"/> of the seam: its
    /// distance to the seam, the nearest seam sample and its tangent frame. Texels are found by
    /// rasterizing the surface's triangles near the seam in UV space, moved into the image's tile the
    /// way its sampler addresses them; the result then spreads into texels no triangle covers.
    /// </summary>
    /// <param name="occupied">Texels other surfaces sharing the texture cover (see <see cref="Cover"/>), which the spread leaves alone.</param>
    internal static Dictionary<int, Texel> Field(SeamSurface surface, Vector3[] seam, SeamImage image, float band, bool[]? occupied = null)
    {
        int width = image.Width, height = image.Height;
        var triangles = surface.Triangles;
        var positions = surface.Positions;
        occupied ??= new bool[width * height];
        var field = new Dictionary<int, Texel>();
        for (var t = 0; t + 2 < triangles.Length; t += 3)
        {
            int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
            var centroid = (positions[a] + positions[b] + positions[c]) / 3;
            var radius = MathF.Max(Vector3.Distance(centroid, positions[a]),
                MathF.Max(Vector3.Distance(centroid, positions[b]), Vector3.Distance(centroid, positions[c])));
            var candidates = new List<int>();
            for (var s = 0; s < seam.Length; s++)
                if (Vector3.Distance(seam[s], centroid) <= band + radius)
                    candidates.Add(s);
            var near = candidates.Count > 0;
            var (uvA, uvB, uvC) = image.IntoTile(surface.Uv[a], surface.Uv[b], surface.Uv[c]);
            Rasterize(uvA, uvB, uvC, width, height, (pixel, w0, w1, w2) =>
            {
                occupied[pixel] = true;
                if (!near)
                    return;
                var p = positions[a] * w0 + positions[b] * w1 + positions[c] * w2;
                var best = float.MaxValue;
                var nearest = -1;
                foreach (var s in candidates)
                {
                    var d = Vector3.DistanceSquared(p, seam[s]);
                    if (d < best)
                    {
                        best = d;
                        nearest = s;
                    }
                }
                var distance = MathF.Sqrt(best);
                if (nearest < 0 || distance >= band || (field.TryGetValue(pixel, out var existing) && existing.Distance <= distance))
                    return;
                var normal = surface.Normals[a] * w0 + surface.Normals[b] * w1 + surface.Normals[c] * w2;
                var binormal = surface.Binormals[a] * w0 + surface.Binormals[b] * w1 + surface.Binormals[c] * w2;
                field[pixel] = new Texel(distance, nearest, NeckSeamAnalyzer.Frame(normal, binormal, surface.Signs[a]));
            });
        }

        // Spread into the padding around the islands, never into texels another triangle owns.
        var frontier = field.Keys.ToList();
        for (var step = 0; step < PaddingTexels && frontier.Count > 0; step++)
        {
            var next = new List<int>();
            foreach (var pixel in frontier)
            {
                int x = pixel % width, y = pixel / width;
                foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                        continue;
                    var neighbour = ny * width + nx;
                    if (occupied[neighbour] || field.ContainsKey(neighbour))
                        continue;
                    field[neighbour] = field[pixel];
                    next.Add(neighbour);
                }
            }
            frontier = next;
        }
        return field;
    }

    /// <summary> Marks the texels of <paramref name="image"/> that the surface's triangles cover. </summary>
    internal static void Cover(SeamSurface surface, SeamImage image, bool[] occupied)
    {
        var triangles = surface.Triangles;
        for (var t = 0; t + 2 < triangles.Length; t += 3)
        {
            var (uvA, uvB, uvC) = image.IntoTile(surface.Uv[triangles[t]], surface.Uv[triangles[t + 1]], surface.Uv[triangles[t + 2]]);
            Rasterize(uvA, uvB, uvC, image.Width, image.Height, (pixel, _, _, _) => occupied[pixel] = true);
        }
    }

    /// <summary> Calls <paramref name="visit"/> for every texel centre inside the UV triangle, with its barycentric weights. </summary>
    internal static void Rasterize(Vector2 uvA, Vector2 uvB, Vector2 uvC, int width, int height, Action<int, float, float, float> visit)
    {
        var size = new Vector2(width, height);
        Vector2 a = uvA * size, b = uvB * size, c = uvC * size;
        var den = (b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y);
        if (MathF.Abs(den) < 1e-9f)
            return;
        var minX = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))));
        var maxX = Math.Min(width - 1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
        var minY = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))));
        var maxY = Math.Min(height - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
        for (var y = minY; y <= maxY; y++)
            for (var x = minX; x <= maxX; x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f);
                var w1 = ((p.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (p.Y - a.Y)) / den;
                var w2 = ((b.X - a.X) * (p.Y - a.Y) - (p.X - a.X) * (b.Y - a.Y)) / den;
                var w0 = 1 - w1 - w2;
                if (w0 >= -1e-4f && w1 >= -1e-4f && w2 >= -1e-4f)
                    visit(y * width + x, w0, w1, w2);
            }
    }
}
