using System.Numerics;
using InstantEdit.Services.Skeletons;

namespace InstantEdit.Services.NeckSeam;

/// <summary>
/// Which parts of one body seam's fix to build. <paramref name="Meet"/> is where the two parts meet,
/// as at the neck: 0 keeps the first part as it is (only the second changes), 1 keeps the second,
/// and values between move both.
/// </summary>
internal sealed record BodySeamFixOptions(bool Weld, bool Normals, bool Material, bool Textures, float BandMetres = NeckSeamFixer.DefaultBand, float Meet = 0.5f);

/// <summary> A changed model or material: what it is, the path the game requests and its new bytes. </summary>
internal sealed record SeamFileOutput(string Kind, string GamePath, byte[] Bytes);

/// <summary> A changed skin texture: the path materials request, the original file, the new pixels and the materials that read it. </summary>
internal sealed record SeamTextureOutput(string Kind, uint Sampler, string GamePath, byte[] Original, SeamImage Image, IReadOnlyList<string> Materials);

/// <summary> The fixed body files, what they change, and each seam measured again with them. </summary>
internal sealed class BodySeamFix
{
    public required IReadOnlyList<SeamFileOutput> Models { get; init; }
    /// <summary> Changed skin materials, with any base changes (the neck's body material) already in them. </summary>
    public required IReadOnlyList<SeamFileOutput> Materials { get; init; }
    public required IReadOnlyList<SeamTextureOutput> Textures { get; init; }
    public required IReadOnlyList<string> Changes { get; init; }
    /// <summary> Per seam, the measured values before and after the fix. </summary>
    public required IReadOnlyDictionary<BodySeamKind, IReadOnlyList<string>> Expected { get; init; }
    public bool Empty => Models.Count == 0 && Materials.Count == 0 && Textures.Count == 0;
}

/// <summary>
/// Builds the body seams' fixes from a <see cref="BodySeamReport"/>. The weld moves each part's edge
/// vertices towards the other part's edge by its share of the meeting point, and the vertices behind
/// the edge by a smooth falloff, so no crease forms; normals along the edge meet the same way. Moves
/// are made in the character's pose and taken back through each vertex's racial deform before they
/// are written into the model. Skin settings of two different materials meet as at the neck. Textures
/// are blended on both sides of the seam towards where they meet, fading out over the blend band;
/// where both parts read one texture, both blends go into the same file.
/// </summary>
internal static class BodySeamFixer
{
    /// <summary>
    /// How far along the seam the texture differences are smoothed before they are blended in: 3 mm,
    /// wide enough that single pores aren't copied across, narrow enough that the 1 cm smoothing the
    /// check measures with sees them fully (a 1 cm smoothing here left a third of a tattoo line's step).
    /// </summary>
    private const float DeltaSmoothing = 0.003f;

    private sealed class VertexEdit
    {
        public Vector3? Position;
        public Vector3? Normal;
    }

    /// <summary> An edit for one texel: its distance to the seam, how much of the change it takes, and the change. </summary>
    private readonly record struct TexelEdit(float Distance, float Amount, Vector4 Delta, Vector3 From, Vector3 To, NeckSeamAnalyzer.TangentFrame Frame);

    private sealed class TextureWork
    {
        public required string Path { get; init; }
        public required string Kind { get; init; }
        public required uint Sampler { get; init; }
        public required byte[] Original { get; init; }
        public required SeamImage Image { get; init; }
        public required bool[] Occupied { get; init; }
        public HashSet<string> Materials { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<int, TexelEdit> Edits { get; } = new();
        public HashSet<BodySeamKind> Seams { get; } = [];
    }

    private sealed class MaterialWork
    {
        public required string Path { get; init; }
        public required string Kind { get; init; }
        public required byte[] Base { get; init; }
        public Dictionary<uint, float[]> Constants { get; } = new();
    }

    /// <param name="materialBase">Material bytes to build on instead of the files read, by game path (the neck's changed body material).</param>
    public static BodySeamFix Build(BodySeamReport report, IReadOnlyDictionary<BodySeamKind, BodySeamFixOptions> options,
        IReadOnlyDictionary<string, byte[]>? materialBase = null)
    {
        var changes = new List<string>();
        var vertexEdits = new Dictionary<BodySeamPart, Dictionary<int, VertexEdit>>();
        var materials = new Dictionary<string, MaterialWork>(StringComparer.OrdinalIgnoreCase);
        var textures = new Dictionary<string, TextureWork>(StringComparer.OrdinalIgnoreCase);

        foreach (var seam in report.Seams)
        {
            if (seam.Chains.Count == 0 || !options.TryGetValue(seam.Kind, out var option))
                continue;
            var meet = Math.Clamp(option.Meet, 0f, 1f);
            var weld = option.Weld && seam.CanWeld;
            var normals = option.Normals && seam.NormalsDiffer;
            if (weld || normals)
                Geometry(seam, weld, normals, meet, vertexEdits, changes);
            if (option.Material && seam.Material is { Any: true } match && seam.MaterialA is { } ma && seam.MaterialB is { } mb)
            {
                var (aChanges, bChanges) = match.Plan(meet);
                Merge(materials, ma.Input, aChanges, materialBase, changes, seam);
                Merge(materials, mb.Input, bChanges, materialBase, changes, seam);
            }
            if (option.Textures && seam.TexturesDiffer)
                Textures(report, seam, meet, Math.Clamp(option.BandMetres, 0.005f, 0.06f), textures);
        }

        // ---- Models ----
        var models = new List<SeamFileOutput>();
        foreach (var (part, edits) in vertexEdits)
        {
            var written = new List<SkinVertexChange>();
            foreach (var (vertex, edit) in edits)
            {
                var (mesh, _, _) = part.Meshes[part.MeshOf[vertex]];
                var index = part.VertexOf[vertex];
                var matrix = part.Deform[vertex];
                Vector3? position = edit.Position is { } p ? Undo(matrix, p, point: true) : null;
                Vector3? normal = null, binormal = null;
                if (edit.Normal is { } n)
                {
                    var modelNormal = NeckSeamAnalyzer.SafeNormalize(Undo(matrix, n, point: false), mesh.Normals[index]);
                    var original = mesh.Binormals[index];
                    normal = modelNormal;
                    binormal = NeckSeamAnalyzer.SafeNormalize(original - modelNormal * Vector3.Dot(original, modelNormal), original);
                }
                written.Add(new SkinVertexChange(mesh.MeshIndex, index, position, normal, binormal));
            }
            models.Add(new SeamFileOutput(part.Label.ToLowerInvariant() + " model", part.ModelPath, part.Model.WithVertexChanges(written)));
        }

        // ---- Materials ----
        var materialFiles = new List<SeamFileOutput>();
        foreach (var work in materials.Values.Where(w => w.Constants.Count > 0))
        {
            materialFiles.Add(new SeamFileOutput(work.Kind, work.Path, SkinMaterial.Read(work.Base).WithConstants(work.Constants)));
            changes.Add($"{Capital(work.Kind)} {FileName(work.Path)}: {NeckSeamFixer.Describe(work.Constants)}");
        }

        // ---- Textures ----
        var textureFiles = new List<SeamTextureOutput>();
        var changed = new Dictionary<string, SeamImage>(StringComparer.OrdinalIgnoreCase);
        foreach (var work in textures.Values.Where(w => w.Edits.Count > 0))
        {
            var image = Apply(work);
            changed[work.Path] = image;
            textureFiles.Add(new SeamTextureOutput(work.Kind, work.Sampler, work.Path, work.Original, image, work.Materials.ToList()));
            changes.Add($"{Capital(work.Kind)} {FileName(work.Path)}: blend {work.Edits.Count:N0} texels at the {string.Join(", ", work.Seams.Select(k => BodySeamAnalyzer.Title(k).ToLowerInvariant()))}");
        }

        return new BodySeamFix
        {
            Models = models, Materials = materialFiles, Textures = textureFiles, Changes = changes,
            Expected = Expected(report, options, vertexEdits, textures, changed),
        };
    }

    // ---- Geometry ----------------------------------------------------------------------------------

    /// <summary>
    /// Moves both parts' edges (and normals) towards where they meet, the first part by
    /// <paramref name="meet"/>, the second by the rest. The weld works in two passes: one part's edge
    /// moves to the meeting points, then the other part's edge lands exactly on that edge's new line,
    /// which closes the gap even where the two edges have different vertex counts. A part that stores
    /// half floats goes first, onto positions it can hold, since only full-precision positions can
    /// follow another edge exactly.
    /// </summary>
    private static void Geometry(BodySeam seam, bool weld, bool normals, float meet, Dictionary<BodySeamPart, Dictionary<int, VertexEdit>> edits,
        List<string> changes)
    {
        BodySeamPart a = seam.A, b = seam.B;
        var movedA = new Dictionary<int, Vector3>();
        var movedB = new Dictionary<int, Vector3>();
        var normalsA = 0;
        var normalsB = 0;
        foreach (var chain in seam.Chains)
        {
            if (weld)
            {
                var halfA = chain.A.Any(w => Half(a, a.Topology.Representative[w]));
                var halfB = chain.HitsB.Keys.Any(w => Half(b, b.Topology.Representative[w]));
                if (halfA || !halfB)
                {
                    // The first part's edge moves to the meeting points; the second's lands on its new line.
                    var final = new Dictionary<int, Vector3>();
                    foreach (var welded in chain.A)
                    {
                        var hit = chain.HitsA[welded];
                        var vertex = a.Topology.Representative[welded];
                        final[welded] = Held(a, vertex, Vector3.Lerp(a.Positions[vertex], BodySeamAnalyzer.PointOn(b, seam.EdgesB[hit.Edge], hit.T), meet));
                        movedA[welded] = final[welded] - a.Positions[vertex];
                    }
                    foreach (var (welded, hit) in chain.HitsB)
                    {
                        var edge = chain.EdgesA[hit.Edge];
                        var vertex = b.Topology.Representative[welded];
                        var on = Vector3.Lerp(final.GetValueOrDefault(edge.WeldedA, a.Positions[edge.RawA]), final.GetValueOrDefault(edge.WeldedB, a.Positions[edge.RawB]), hit.T);
                        movedB[welded] = Held(b, vertex, on) - b.Positions[vertex];
                    }
                }
                else
                {
                    // Only the second part stores half floats: it goes first, the first part's edge lands on it.
                    var final = new Dictionary<int, Vector3>();
                    foreach (var (welded, hit) in chain.HitsB)
                    {
                        var vertex = b.Topology.Representative[welded];
                        final[welded] = Held(b, vertex, Vector3.Lerp(BodySeamAnalyzer.PointOn(a, chain.EdgesA[hit.Edge], hit.T), b.Positions[vertex], meet));
                        movedB[welded] = final[welded] - b.Positions[vertex];
                    }
                    foreach (var welded in chain.A)
                    {
                        var hit = chain.HitsA[welded];
                        var edge = seam.EdgesB[hit.Edge];
                        var vertex = a.Topology.Representative[welded];
                        var on = Vector3.Lerp(final.GetValueOrDefault(edge.WeldedA, b.Positions[edge.RawA]), final.GetValueOrDefault(edge.WeldedB, b.Positions[edge.RawB]), hit.T);
                        movedA[welded] = on - a.Positions[vertex];
                    }
                }
            }
            if (!normals)
                continue;
            foreach (var welded in chain.A)
            {
                if (meet <= 0)
                    break;
                var hit = chain.HitsA[welded];
                Set(edits, a, welded, null, Meet(a.WeldedNormal(welded), BodySeamAnalyzer.NormalOn(b, seam.EdgesB[hit.Edge], hit.T), meet));
                normalsA++;
            }
            foreach (var (welded, hit) in chain.HitsB)
            {
                if (meet >= 1)
                    break;
                Set(edits, b, welded, null, Meet(BodySeamAnalyzer.NormalOn(a, chain.EdgesA[hit.Edge], hit.T), b.WeldedNormal(welded), meet));
                normalsB++;
            }
        }
        var where = BodySeamAnalyzer.Title(seam.Kind).ToLowerInvariant();
        if (Spread(a, movedA, edits) is ({ } edgeA and > 0, var behindA))
            changes.Add($"{a.Label} model: move {edgeA} edge vertices at the {where} up to {BodySeamAnalyzer.Mm(movedA.Values.Max(v => v.Length()))} " +
                        $"towards the {b.Label.ToLowerInvariant()}, and {behindA} behind them less");
        if (Spread(b, movedB, edits) is ({ } edgeB and > 0, var behindB))
            changes.Add($"{b.Label} model: move {edgeB} edge vertices at the {where} up to {BodySeamAnalyzer.Mm(movedB.Values.Max(v => v.Length()))} " +
                        $"towards the {a.Label.ToLowerInvariant()}, and {behindB} behind them less");
        if (normalsA > 0)
            changes.Add($"{a.Label} model: give {normalsA} edge vertices at the {where} the normals both parts meet at");
        if (normalsB > 0)
            changes.Add($"{b.Label} model: give {normalsB} edge vertices at the {where} the normals both parts meet at");
    }

    private static Vector3 Meet(Vector3 first, Vector3 second, float meet) => NeckSeamAnalyzer.SafeNormalize(Vector3.Lerp(first, second, meet), first);

    private static bool Half(BodySeamPart part, int vertex) => part.Meshes[part.MeshOf[vertex]].Mesh.HalfPositions;

    /// <summary>
    /// The position in the pose a vertex ends up at when moved to <paramref name="target"/>: the target
    /// itself at full precision, the nearest position the file can hold when it stores half floats
    /// (about 1 mm steps a metre from the origin). That can move a part the meeting point keeps by less
    /// than a step.
    /// </summary>
    private static Vector3 Held(BodySeamPart part, int vertex, Vector3 target)
    {
        if (!Half(part, vertex))
            return target;
        var model = Undo(part.Deform[vertex], target, point: true);
        return part.Deform[vertex].Point(new Vector3((float)(Half)model.X, (float)(Half)model.Y, (float)(Half)model.Z));
    }

    private static void Set(Dictionary<BodySeamPart, Dictionary<int, VertexEdit>> edits, BodySeamPart part, int welded, Vector3? position, Vector3? normal)
    {
        if (!edits.TryGetValue(part, out var partEdits))
            edits[part] = partEdits = new Dictionary<int, VertexEdit>();
        foreach (var vertex in part.Topology.Members(welded))
        {
            if (!partEdits.TryGetValue(vertex, out var edit))
                partEdits[vertex] = edit = new VertexEdit();
            if (position is not null)
                edit.Position = position;
            if (normal is not null)
                edit.Normal = normal;
        }
    }

    /// <summary>
    /// Moves the edge vertices by their offsets and the part's other vertices near them by the nearest
    /// edge vertex's offset, fading out smoothly over a centimetre (or four times the largest move), so
    /// the surface behind the edge follows it. Edge vertices that stay (offset zero) still anchor their
    /// neighbourhood. Returns how many edge and other positions moved.
    /// </summary>
    private static (int Edge, int Behind) Spread(BodySeamPart part, Dictionary<int, Vector3> moved, Dictionary<BodySeamPart, Dictionary<int, VertexEdit>> edits)
    {
        const float still = 1e-14f;
        if (moved.Count == 0 || moved.Values.All(v => v.LengthSquared() <= still))
            return (0, 0);
        var radius = Math.Max(0.01f, 4 * moved.Values.Max(v => v.Length()));
        var anchors = moved.Select(p => (Position: part.Positions[part.Topology.Representative[p.Key]], Offset: p.Value)).ToList();
        var edge = 0;
        foreach (var (welded, offset) in moved)
            if (offset.LengthSquared() > still)
            {
                Set(edits, part, welded, part.Positions[part.Topology.Representative[welded]] + offset, null);
                edge++;
            }
        var low = anchors.Aggregate(new Vector3(float.MaxValue), (m, a) => Vector3.Min(m, a.Position)) - new Vector3(radius);
        var high = anchors.Aggregate(new Vector3(float.MinValue), (m, a) => Vector3.Max(m, a.Position)) + new Vector3(radius);
        var behind = 0;
        for (var welded = 0; welded < part.Topology.Representative.Length; welded++)
        {
            if (moved.ContainsKey(welded))
                continue;
            var p = part.Positions[part.Topology.Representative[welded]];
            if (p.X < low.X || p.Y < low.Y || p.Z < low.Z || p.X > high.X || p.Y > high.Y || p.Z > high.Z)
                continue;
            var best = float.MaxValue;
            var offset = Vector3.Zero;
            foreach (var anchor in anchors)
            {
                var d = Vector3.DistanceSquared(p, anchor.Position);
                if (d < best)
                {
                    best = d;
                    offset = anchor.Offset;
                }
            }
            var weight = NeckSeamFixer.Falloff(MathF.Sqrt(best), radius);
            if (weight <= 1e-3f || offset.LengthSquared() * weight * weight < 1e-14f)
                continue;
            Set(edits, part, welded, p + offset * weight, null);
            behind++;
        }
        return (edge, behind);
    }

    /// <summary> Takes a point or direction in the character's pose back through a vertex's racial deform (y = A x + t). </summary>
    internal static Vector3 Undo(RacialDeformer.Affine m, Vector3 value, bool point)
    {
        float a = m.X.X, b = m.X.Y, c = m.X.Z, d = m.Y.X, e = m.Y.Y, f = m.Y.Z, g = m.Z.X, h = m.Z.Y, i = m.Z.Z;
        var det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
        if (MathF.Abs(det) < 1e-12f)
            return value;
        var y = point ? value - new Vector3(m.X.W, m.Y.W, m.Z.W) : value;
        return new Vector3(
            (e * i - f * h) * y.X + (c * h - b * i) * y.Y + (b * f - c * e) * y.Z,
            (f * g - d * i) * y.X + (a * i - c * g) * y.Y + (c * d - a * f) * y.Z,
            (d * h - e * g) * y.X + (b * g - a * h) * y.Y + (a * e - b * d) * y.Z) / det;
    }

    // ---- Materials -----------------------------------------------------------------------------------

    private static void Merge(Dictionary<string, MaterialWork> materials, NeckSeamMaterialInput input, Dictionary<uint, float[]> constants,
        IReadOnlyDictionary<string, byte[]>? materialBase, List<string> changes, BodySeam seam)
    {
        if (constants.Count == 0)
            return;
        var path = PathRules.NormalizeGamePath(input.GamePath);
        if (!materials.TryGetValue(path, out var work))
            materials[path] = work = new MaterialWork
            {
                Path = path, Kind = "skin material", Base = materialBase?.GetValueOrDefault(path) ?? input.Bytes,
            };
        foreach (var (id, values) in constants)
        {
            if (work.Constants.TryGetValue(id, out var existing) && !existing.SequenceEqual(values))
            {
                changes.Add($"The {BodySeamAnalyzer.Title(seam.Kind).ToLowerInvariant()} would set {SkinMaterial.Names.GetValueOrDefault(id, $"0x{id:X8}")} of " +
                            $"{FileName(path)} differently from another seam; the first value was kept");
                continue;
            }
            work.Constants[id] = values;
        }
    }

    // ---- Textures ------------------------------------------------------------------------------------

    /// <summary>
    /// Collects the texel edits of one seam: both parts' texels near each chain move towards where the
    /// parts meet. The differences come from the full-size textures the edits go into: the analysis's
    /// smaller mip averages a detailed normal map differently, and rotations taken from it miss.
    /// </summary>
    private static void Textures(BodySeamReport report, BodySeam seam, float meet, float band, Dictionary<string, TextureWork> textures)
    {
        if (seam.MaterialA is not { } ma || seam.MaterialB is not { } mb)
            return;
        foreach (var sampler in seam.TexturesToBlend)
        {
            if (Work(report, ma, sampler, textures) is not { Image: var imageA } || Work(report, mb, sampler, textures) is not { Image: var imageB })
                continue;
            foreach (var chain in seam.Chains)
            {
                var samples = chain.Samples;
                // Seam differences (second minus first), smoothed along the seam so single pores aren't copied.
                var delta = new Vector4[samples.Count];
                for (var i = 0; i < samples.Count; i++)
                    delta[i] = imageB.Sample(samples.BodyUv[i]) - imageA.Sample(samples.FaceUv[i]);
                delta = NeckSeamAnalyzer.Smooth(samples, delta, DeltaSmoothing);
                Vector3[] worldA = [], worldB = [];
                if (sampler == SkinMaterial.NormalSampler)
                    (worldA, worldB) = BodySeamAnalyzer.SmoothedNormals(samples, imageA, imageB, DeltaSmoothing);

                if (meet > 0)
                    Side(report, seam, chain, seam.A, ma, sampler, band, meet, i => sampler == SkinMaterial.NormalSampler
                        ? (new Vector4(0, 0, delta[i].Z, delta[i].W), worldA[i], worldB[i])
                        : (delta[i], Vector3.Zero, Vector3.Zero), textures);
                if (meet < 1)
                    Side(report, seam, chain, seam.B, mb, sampler, band, 1 - meet, i => sampler == SkinMaterial.NormalSampler
                        ? (new Vector4(0, 0, -delta[i].Z, -delta[i].W), worldB[i], worldA[i])
                        : (-delta[i], Vector3.Zero, Vector3.Zero), textures);
            }
        }
    }

    private static void Side(BodySeamReport report, BodySeam seam, BodySeamChain chain, BodySeamPart part, (NeckSeamMaterialInput Input, SkinMaterial Skin) material,
        uint sampler, float band, float share, Func<int, (Vector4 Delta, Vector3 From, Vector3 To)> change, Dictionary<string, TextureWork> textures)
    {
        if (Work(report, material, sampler, textures) is not { } work)
            return;
        work.Materials.Add(PathRules.NormalizeGamePath(material.Input.GamePath));
        work.Seams.Add(seam.Kind);
        var surface = part.SurfaceFor(PathRules.NormalizeGamePath(material.Input.GamePath));
        var field = NeckSeamFixer.Field(surface, chain.Samples.Positions, work.Image, band, work.Occupied);
        foreach (var (pixel, texel) in field)
        {
            var amount = NeckSeamFixer.Falloff(texel.Distance, band) * share;
            if (amount <= 1e-3f)
                continue;
            if (work.Edits.TryGetValue(pixel, out var existing) && existing.Distance <= texel.Distance)
                continue;
            var (delta, from, to) = change(texel.Seam);
            work.Edits[pixel] = new TexelEdit(texel.Distance, amount, delta, from, to, texel.Frame);
        }
    }

    /// <summary>
    /// The full-size texture a material's sampler reads, decoded once, with the texels every part
    /// reading it covers marked, so a blend spreads only into the UV padding and never into another island.
    /// </summary>
    private static TextureWork? Work(BodySeamReport report, (NeckSeamMaterialInput Input, SkinMaterial Skin) material, uint sampler,
        Dictionary<string, TextureWork> textures)
    {
        if (material.Skin.RequestedTextureFor(sampler) is not { } path)
            return null;
        if (textures.TryGetValue(path, out var work))
            return work;
        var bytes = material.Input.Textures.FirstOrDefault(t => string.Equals(PathRules.NormalizeGamePath(t.Key), path, StringComparison.OrdinalIgnoreCase)).Value;
        if (bytes is null)
            return null;
        SeamImage image;
        try { image = SeamTextures.Decode(bytes, material.Skin.FlagsFor(sampler)); }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException) { return null; }
        var occupied = new bool[image.Width * image.Height];
        foreach (var part in report.Seams.SelectMany(s => new[] { s.A, s.B }).Distinct())
            foreach (var (_, input, skin) in part.Meshes.DistinctBy(m => PathRules.NormalizeGamePath(m.Material.GamePath)))
                if (skin.Samplers.Keys.Any(s => string.Equals(skin.RequestedTextureFor(s), path, StringComparison.OrdinalIgnoreCase)))
                    NeckSeamFixer.Cover(part.SurfaceFor(PathRules.NormalizeGamePath(input.GamePath)), image, occupied);
        work = new TextureWork
        {
            Path = path, Kind = $"skin {NeckSeamFixer.Label(sampler)} texture", Sampler = sampler, Original = bytes, Image = image, Occupied = occupied,
        };
        textures[path] = work;
        return work;
    }

    private static SeamImage Apply(TextureWork work)
    {
        var image = work.Image.Clone();
        foreach (var (pixel, edit) in work.Edits)
        {
            int x = pixel % image.Width, y = pixel / image.Width;
            var value = image.Texel(x, y);
            if (work.Sampler == SkinMaterial.NormalSampler)
            {
                var world = NeckSeamAnalyzer.ToWorld(value, edit.Frame);
                var rotated = NeckSeamFixer.Rotate(world, edit.From, edit.To, edit.Amount);
                var rg = NeckSeamAnalyzer.ToTexel(rotated, edit.Frame);
                value = new Vector4(rg.X, rg.Y, value.Z + edit.Delta.Z * edit.Amount, value.W + edit.Delta.W * edit.Amount);
            }
            else
                value += new Vector4(edit.Delta.X, edit.Delta.Y, edit.Delta.Z, 0) * edit.Amount;
            image.SetTexel(x, y, Vector4.Clamp(value, Vector4.Zero, Vector4.One));
        }
        return image;
    }

    // ---- Measured again --------------------------------------------------------------------------------

    /// <summary> Per seam: the edge gap, vertex normals and textures before and after the fix, measured along the same samples. </summary>
    private static Dictionary<BodySeamKind, IReadOnlyList<string>> Expected(BodySeamReport report, IReadOnlyDictionary<BodySeamKind, BodySeamFixOptions> options,
        Dictionary<BodySeamPart, Dictionary<int, VertexEdit>> edits, Dictionary<string, TextureWork> textures, Dictionary<string, SeamImage> changed)
    {
        var result = new Dictionary<BodySeamKind, IReadOnlyList<string>>();
        foreach (var seam in report.Seams)
        {
            if (seam.Chains.Count == 0 || !options.ContainsKey(seam.Kind))
                continue;
            var lines = new List<string>();
            var (gap, angle) = Geometry(seam, edits);
            if (MathF.Abs(gap - seam.GapMax) > 1e-6f)
                lines.Add($"Edge gap: {BodySeamAnalyzer.Mm(seam.GapMax)} → {BodySeamAnalyzer.Mm(gap)} at most");
            if (MathF.Abs(angle - seam.NormalMax) > 0.01f)
                lines.Add($"Vertex normals at the edge: {BodySeamAnalyzer.Deg(seam.NormalMax)} → {BodySeamAnalyzer.Deg(angle)} at most");

            // Where the fix decoded a texture at full size, both measurements use it, so they compare like with like.
            (Dictionary<uint, SeamImage> Before, Dictionary<uint, SeamImage> After) Images(IReadOnlyDictionary<uint, SeamImage> analysis,
                (NeckSeamMaterialInput Input, SkinMaterial Skin)? material)
            {
                var before = analysis.ToDictionary(p => p.Key, p => p.Value);
                var after = analysis.ToDictionary(p => p.Key, p => p.Value);
                if (material is { } m)
                    foreach (var sampler in analysis.Keys)
                        if (m.Skin.RequestedTextureFor(sampler) is { } path && textures.TryGetValue(path, out var work))
                        {
                            before[sampler] = work.Image;
                            after[sampler] = changed.GetValueOrDefault(path, work.Image);
                        }
                return (before, after);
            }
            var (beforeA, afterA) = Images(seam.ImagesA, seam.MaterialA);
            var (beforeB, afterB) = Images(seam.ImagesB, seam.MaterialB);
            float colourBefore = 0, colourAfter = 0, normalBefore = 0, normalAfter = 0;
            foreach (var chain in seam.Chains)
            {
                colourBefore = Math.Max(colourBefore, NeckSeamAnalyzer.CompareTextures(chain.Samples, beforeA, beforeB, snapped: false).Diffuse?.MaxDifference ?? 0);
                colourAfter = Math.Max(colourAfter, NeckSeamAnalyzer.CompareTextures(chain.Samples, afterA, afterB, snapped: false).Diffuse?.MaxDifference ?? 0);
                normalBefore = Math.Max(normalBefore, MaxNormal(chain, beforeA, beforeB));
                normalAfter = Math.Max(normalAfter, MaxNormal(chain, afterA, afterB));
            }
            if (colourBefore - colourAfter > 0.002f)
                lines.Add($"Largest colour difference along the seam: {colourBefore * 255:0} → {colourAfter * 255:0} of 255");
            if (normalBefore - normalAfter > 0.05f)
                lines.Add($"Surface normal at the seam: {BodySeamAnalyzer.Deg(normalBefore)} → {BodySeamAnalyzer.Deg(normalAfter)} at most");
            result[seam.Kind] = lines;
        }
        return result;
    }

    private static float MaxNormal(BodySeamChain chain, IReadOnlyDictionary<uint, SeamImage> a, IReadOnlyDictionary<uint, SeamImage> b)
    {
        if (!a.TryGetValue(SkinMaterial.NormalSampler, out var normalA) || !b.TryGetValue(SkinMaterial.NormalSampler, out var normalB))
            return 0;
        var (worldA, worldB) = BodySeamAnalyzer.SmoothedNormals(chain.Samples, normalA, normalB);
        var max = 0f;
        for (var i = 0; i < worldA.Length; i++)
            max = Math.Max(max, NeckSeamAnalyzer.AngleDegrees(worldA[i], worldB[i]));
        return max;
    }

    /// <summary> The largest edge gap and vertex normal difference along the seam with the edits applied. </summary>
    private static (float Gap, float Angle) Geometry(BodySeam seam, Dictionary<BodySeamPart, Dictionary<int, VertexEdit>> edits)
    {
        Vector3 Position(BodySeamPart part, int vertex)
            => edits.TryGetValue(part, out var e) && e.TryGetValue(vertex, out var edit) && edit.Position is { } p ? p : part.Positions[vertex];
        Vector3 Normal(BodySeamPart part, int vertex)
            => edits.TryGetValue(part, out var e) && e.TryGetValue(vertex, out var edit) && edit.Normal is { } n ? n : part.Normals[vertex];
        float gap = 0, angle = 0;
        foreach (var chain in seam.Chains)
        {
            foreach (var welded in chain.A)
            {
                var vertex = seam.A.Topology.Representative[welded];
                var p = Position(seam.A, vertex);
                var best = float.MaxValue;
                var normal = Vector3.UnitY;
                foreach (var edge in seam.EdgesB)
                {
                    var q = Position(seam.B, edge.RawA);
                    var qb = Position(seam.B, edge.RawB);
                    var ab = qb - q;
                    var t = Math.Clamp(Vector3.Dot(p - q, ab) / Math.Max(ab.LengthSquared(), 1e-12f), 0f, 1f);
                    var d = Vector3.Distance(p, q + ab * t);
                    if (d < best)
                    {
                        best = d;
                        normal = Vector3.Lerp(Normal(seam.B, edge.RawA), Normal(seam.B, edge.RawB), t);
                    }
                }
                var own = Vector3.Zero;
                foreach (var member in seam.A.Topology.Members(welded))
                    own += Normal(seam.A, member);
                gap = Math.Max(gap, best);
                angle = Math.Max(angle, NeckSeamAnalyzer.AngleDegrees(own, normal));
            }
        }
        return (gap, angle);
    }

    private static string FileName(string path) => path[(path.LastIndexOf('/') + 1)..];
    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
