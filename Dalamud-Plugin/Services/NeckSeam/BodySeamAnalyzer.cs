using System.Numerics;
using System.Text.RegularExpressions;
using InstantEdit.Services.Skeletons;

namespace InstantEdit.Services.NeckSeam;

/// <summary> Where two body parts' skin meets: top and gloves at the wrists, top and legs at the waist, legs (or a long top) and shoes at the ankles. </summary>
internal enum BodySeamKind
{
    Wrists,
    Waist,
    Ankles,
}

/// <summary>
/// One body part's drawn skin in the character's pose: every drawn triangle of its body skin meshes,
/// merged into one surface. Only vertices the drawn triangles use are kept, so a part hidden by an
/// attribute or replaced by a shape key never counts. Each vertex remembers its mesh and index in
/// the file, and the racial deform that moved it, so a fix can be written back.
/// </summary>
internal sealed class BodySeamPart
{
    public required NeckSeamModelInput Input { get; init; }
    /// <summary> "top", "glv", "dwn" or "sho". </summary>
    public required string Slot { get; init; }
    public required SkinModel Model { get; init; }
    public required IReadOnlyList<(SkinMesh Mesh, NeckSeamMaterialInput Material, SkinMaterial Skin)> Meshes { get; init; }
    /// <summary> Per merged vertex: its mesh (an index into <see cref="Meshes"/>) and its vertex in that mesh. </summary>
    public required int[] MeshOf { get; init; }
    public required int[] VertexOf { get; init; }
    public required Vector3[] Positions { get; init; }
    public required Vector3[] Normals { get; init; }
    public required Vector3[] Binormals { get; init; }
    public required float[] Signs { get; init; }
    public required Vector2[] Uv { get; init; }
    public required Vector4[] Colors { get; init; }
    /// <summary> Per merged vertex: the racial deform its weights blend (identity for a model made for the character's race). </summary>
    public required RacialDeformer.Affine[] Deform { get; init; }
    public required int[] Triangles { get; init; }
    public required SeamTopology Topology { get; init; }
    /// <summary> Per welded id: whether the model's clothing continues from this skin vertex, so an edge there is covered rather than open. </summary>
    public required bool[] Covered { get; init; }

    public string Label => BodySeamAnalyzer.PartLabel(Slot);
    public string ModelPath => PathRules.NormalizeGamePath(Input.GamePath);
    public SeamSurface Surface => new(Positions, Normals, Binormals, Signs, Uv, Triangles);

    /// <summary> The drawn surface of the vertices whose mesh uses <paramref name="materialPath"/>. </summary>
    public SeamSurface SurfaceFor(string materialPath)
    {
        var triangles = new List<int>();
        for (var t = 0; t + 2 < Triangles.Length; t += 3)
            if (string.Equals(PathRules.NormalizeGamePath(Meshes[MeshOf[Triangles[t]]].Material.GamePath), materialPath, StringComparison.OrdinalIgnoreCase))
                triangles.AddRange([Triangles[t], Triangles[t + 1], Triangles[t + 2]]);
        return new SeamSurface(Positions, Normals, Binormals, Signs, Uv, triangles.ToArray());
    }

    /// <summary> The normal of a welded vertex as the drawn vertices at its position share it. </summary>
    public Vector3 WeldedNormal(int welded)
    {
        var sum = Vector3.Zero;
        foreach (var vertex in Topology.Members(welded))
            sum += Normals[vertex];
        return NeckSeamAnalyzer.SafeNormalize(sum, Normals[Topology.Representative[welded]]);
    }

    public Dictionary<string, float> Weights(int vertex) => NeckSeamAnalyzer.Weights(Meshes[MeshOf[vertex]].Mesh, VertexOf[vertex]);

    private SeamSpace? _space;
    /// <summary> The drawn skin as a surface to search: what lies nearest a point, and on which triangle. </summary>
    public SeamSpace Space => _space ??= new SeamSpace(Positions, Triangles);

    private Vector3 Blend(Vector3[] values, SurfacePoint point)
    {
        var t = 3 * point.Triangle;
        return values[Triangles[t]] * point.Weights.X + values[Triangles[t + 1]] * point.Weights.Y + values[Triangles[t + 2]] * point.Weights.Z;
    }

    /// <summary> The skin's normal at a point on it, blended from its triangle's corners. </summary>
    public Vector3 NormalAt(SurfacePoint point) => NeckSeamAnalyzer.SafeNormalize(Blend(Normals, point), Normals[Triangles[3 * point.Triangle]]);
    public Vector3 BinormalAt(SurfacePoint point) => Blend(Binormals, point);
    public float SignAt(SurfacePoint point) => Signs[Triangles[3 * point.Triangle]];

    public Vector2 UvAt(SurfacePoint point)
    {
        var t = 3 * point.Triangle;
        return Uv[Triangles[t]] * point.Weights.X + Uv[Triangles[t + 1]] * point.Weights.Y + Uv[Triangles[t + 2]] * point.Weights.Z;
    }

    /// <summary> The corner of the point's triangle nearest to it. </summary>
    public int CornerOf(SurfacePoint point)
    {
        var w = point.Weights;
        var corner = w.X >= w.Y && w.X >= w.Z ? 0 : w.Y >= w.Z ? 1 : 2;
        return Triangles[3 * point.Triangle + corner];
    }
}

/// <summary> A boundary edge of a part: its welded ends and the drawn vertices of the triangle that owns it. </summary>
internal readonly record struct SeamEdge(int WeldedA, int WeldedB, int RawA, int RawB);

/// <summary> The nearest point on a part's boundary edges: its distance, the edge and how far along it. </summary>
internal readonly record struct SeamHit(float Distance, int Edge, float T);

/// <summary> Where an edge vertex lies over another part's skin: that skin's nearest point, and how far above it the vertex is (inside it when negative). </summary>
internal readonly record struct SeamLanding(SurfacePoint Point, float Height);

/// <summary>
/// One stretch of edge where the two parts join, such as the left wrist: either their edges meet, or
/// one part's edge rests on the other's skin (an overlap, like a sleeve over a cuff).
/// </summary>
internal sealed class BodySeamChain
{
    /// <summary> "left", "right" or empty, from the character's point of view. </summary>
    public required string Side { get; init; }
    /// <summary>
    /// The walking edge as welded ids, in order: the first part's where the edges meet, the outer
    /// part's (<see cref="Outer"/>) at an overlap.
    /// </summary>
    public required int[] A { get; init; }
    public required bool Closed { get; init; }
    /// <summary> Where the edges meet: each chain vertex's nearest point on the second part's boundary. </summary>
    public required IReadOnlyDictionary<int, SeamHit> HitsA { get; init; }
    /// <summary> Where the edges meet: the second part's boundary vertices within reach of the chain, with their nearest point on the chain's edges. </summary>
    public required IReadOnlyDictionary<int, SeamHit> HitsB { get; init; }
    /// <summary> The walking part's boundary edges along the chain (drawn vertices), which <see cref="HitsB"/> index. </summary>
    public required IReadOnlyList<SeamEdge> EdgesA { get; init; }
    /// <summary> Null where the edges meet; at an overlap, the part whose edge rests on the other part's skin. </summary>
    public BodySeamPart? Outer { get; init; }
    /// <summary> At an overlap: where each chain vertex lies over the other part's skin. </summary>
    public IReadOnlyDictionary<int, SeamLanding> Landing { get; init; } = new Dictionary<int, SeamLanding>();
    /// <summary> The share of the chain's vertices that clothing closes in around. </summary>
    public float Covered { get; init; }
    public bool Overlap => Outer is not null;
    /// <summary> Samples along the chain: the face fields hold the first part, the body fields the second. </summary>
    public required NeckSeamAnalyzer.Seam Samples { get; init; }
    public required float GapMean { get; init; }
    public required float GapMax { get; init; }
    public required float AngleMean { get; init; }
    public required float AngleMax { get; init; }
    public required float WeightDiff { get; init; }
    public required Vector4 ColorDiff { get; init; }
    /// <summary> The mesh (an index into each part's meshes) whose skin material the chain uses on either side. </summary>
    public required int MeshA { get; init; }
    public required int MeshB { get; init; }
}

/// <summary> A measured body seam: the two parts, where their edges meet, the findings and what the fix can change. </summary>
internal sealed class BodySeam
{
    public required BodySeamKind Kind { get; init; }
    public required BodySeamPart A { get; init; }
    public required BodySeamPart B { get; init; }
    /// <summary> Empty when the edges don't meet (then <see cref="Findings"/> says how far apart they are). </summary>
    public required IReadOnlyList<BodySeamChain> Chains { get; init; }
    public required IReadOnlyList<NeckSeamFinding> Findings { get; init; }
    public required string MaterialPathA { get; init; }
    public required string MaterialPathB { get; init; }
    /// <summary> The skin settings to bring together, when the two parts use different skin materials at the seam. </summary>
    public required NeckSeamMaterialMatch? Material { get; init; }
    /// <summary> Samplers whose textures differ across the seam; both sides are blended towards where they meet. </summary>
    public required IReadOnlySet<uint> TexturesToBlend { get; init; }
    internal required IReadOnlyDictionary<uint, SeamImage> ImagesA { get; init; }
    internal required IReadOnlyDictionary<uint, SeamImage> ImagesB { get; init; }
    /// <summary> The second part's boundary edges, which the chains' <see cref="BodySeamChain.HitsA"/> index. </summary>
    internal IReadOnlyList<SeamEdge> EdgesB { get; init; } = [];
    /// <summary> The skin material each part uses at the seam (null when the edges don't meet). </summary>
    internal (NeckSeamMaterialInput Input, SkinMaterial Skin)? MaterialA { get; init; }
    internal (NeckSeamMaterialInput Input, SkinMaterial Skin)? MaterialB { get; init; }
    public required float GapMax { get; init; }
    public required float NormalMax { get; init; }

    public string Title => BodySeamAnalyzer.Title(Kind);
    public bool CanWeld => Chains.Count > 0 && GapMax > BodySeamAnalyzer.GapFloor;
    public bool NormalsDiffer => Chains.Count > 0 && NormalMax > BodySeamAnalyzer.NormalFloor;
    public bool MaterialDiffers => Material is { Any: true };
    public bool TexturesDiffer => TexturesToBlend.Count > 0;
    public bool AnyFix => CanWeld || NormalsDiffer || MaterialDiffers || TexturesDiffer;
    public NeckSeamSeverity Worst => Findings.Count == 0 ? NeckSeamSeverity.Ok : Findings.Max(f => f.Severity);
}

/// <summary> The measured wrists, waist and ankles; kinds that couldn't be measured say why in <see cref="Notes"/>. </summary>
internal sealed class BodySeamReport
{
    public required IReadOnlyList<BodySeam> Seams { get; init; }
    public required IReadOnlyDictionary<BodySeamKind, string> Notes { get; init; }
    public bool AnyFix => Seams.Any(s => s.AnyFix);
    public BodySeam? Seam(BodySeamKind kind) => Seams.FirstOrDefault(s => s.Kind == kind);
}

/// <summary>
/// Measures where body parts meet: the drawn skin of the top, gloves, legs and shoes (as the game
/// draws them, with the character's enabled attributes and shape keys), moved into the character's
/// race. Only gear counts as a part: the human body models are the game's seam connectors and its
/// low-poly whole body, which never show as skin. Each open skin edge vertex near the other part
/// either meets that part's edge (within <see cref="WeldLimit"/>), rests on its skin (the parts
/// overlap, like a sleeve over a cuff), is tucked under it, or stays open. Stretches that meet or
/// rest are seams: the gap, the vertex normals, weights and colours are compared along them, and both
/// sides' textures are sampled the way skin.shpk draws a body (first UV set, the tangent frame
/// T = sign · cross(B, N)). Measured on the library, parts made for the same body meet exactly;
/// parts made for different bodies are 6 mm or more apart, which no weld can close, so those are
/// reported without a fix. A seam clothing closes in around doesn't show, and a seam connector the
/// game draws under a seam fills small gaps; both are checked.
/// </summary>
internal static class BodySeamAnalyzer
{
    /// <summary> Edges farther apart than this don't meet and aren't welded (5 mm, like the game's neck snap). </summary>
    public const float WeldLimit = 0.005f;
    /// <summary> Gaps below this are closed already: 0.1 mm, below what shows, and the slivers a weld leaves where the two edges' vertices interleave. </summary>
    public const float GapFloor = 0.0001f;
    /// <summary> Vertex normals within this many degrees shade alike. </summary>
    public const float NormalFloor = 1f;
    /// <summary> Skin edges of similar size this far apart on average are edges that should meet but don't. </summary>
    private const float MissLimit = 0.03f;
    /// <summary> An edge up to this far inside the other part's skin still rests on it (0.5 mm); deeper, it is tucked under it. </summary>
    private const float RestDepth = 0.0005f;
    /// <summary> An edge over the other part's skin overlaps it once it lies this far inside the other's edge (2 mm); closer, the edges just meet. </summary>
    private const float OverlapMin = 0.002f;
    /// <summary> How far under the other part's skin an edge is still found tucked under it. </summary>
    private const float TuckReach = 0.02f;
    /// <summary> A seam counts as hidden when clothing closes in around this share of it. </summary>
    public const float HiddenShare = 0.9f;
    /// <summary> A connector sticking out of the skin by more than this shows (0.3 mm). </summary>
    private const float PokeFloor = 0.0003f;
    private const int SamplesPerEdge = 16;
    private const int TextureEdge = 1024;
    private const float DensityBand = 0.03f;
    /// <summary> Vanilla's own seams change the detail tile's size by up to about 1.5 times, which doesn't show. </summary>
    private const float TileRatioLimit = 2f;
    /// <summary> Normal-mapped normals smoothed over 1 cm differ by up to 2.7° across vanilla's seams. </summary>
    private const float NormalMapLimit = 3f;

    /// <summary> Gear only: the human body models of the same names (cXXXXbXXXX_top.mdl) are connectors or the low-poly body. </summary>
    private static readonly Regex SlotPattern = new(@"(?:^|/)c\d{4}e\d{4}_(?<slot>top|glv|dwn|sho)\.mdl$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary> The pairs that meet, first part first; the ankles fall back to a long top when the legs have no skin there. </summary>
    private static readonly (BodySeamKind Kind, string A, string B)[] Pairs =
    [
        (BodySeamKind.Wrists, "top", "glv"),
        (BodySeamKind.Waist, "top", "dwn"),
        (BodySeamKind.Ankles, "dwn", "sho"),
        (BodySeamKind.Ankles, "top", "sho"),
    ];

    public static readonly IReadOnlyList<BodySeamKind> Kinds = [BodySeamKind.Wrists, BodySeamKind.Waist, BodySeamKind.Ankles];

    /// <summary> "top", "glv", "dwn" or "sho" for a body part model's path, or null. </summary>
    public static string? SlotOf(string gamePath)
    {
        var match = SlotPattern.Match(PathRules.NormalizeGamePath(gamePath));
        return match.Success ? match.Groups["slot"].Value.ToLowerInvariant() : null;
    }

    public static string PartLabel(string slot) => slot switch
    {
        "top" => "Top",
        "glv" => "Gloves",
        "dwn" => "Legs",
        "sho" => "Shoes",
        _ => slot,
    };

    public static string Title(BodySeamKind kind) => kind switch
    {
        BodySeamKind.Wrists => "Wrists",
        BodySeamKind.Waist => "Waist",
        _ => "Ankles",
    };

    /// <summary> The connector attribute's name for a seam: atr_cn_wrist, atr_cn_waist, atr_cn_ankle. </summary>
    public static string ConnectorKind(BodySeamKind kind) => kind switch
    {
        BodySeamKind.Wrists => "wrist",
        BodySeamKind.Waist => "waist",
        _ => "ankle",
    };

    /// <param name="around">The clothing and seam connectors around the seams, when the caller read them already.</param>
    public static BodySeamReport Analyze(NeckSeamInput input, SeamSurroundings? around = null)
    {
        var parts = new Dictionary<string, BodySeamPart>();
        var failures = new Dictionary<string, string>();
        foreach (var model in input.Bodies)
        {
            if (SlotOf(model.GamePath) is not { } slot || parts.ContainsKey(slot) || failures.ContainsKey(slot))
                continue;
            try
            {
                if (Part(model, slot, input) is { } part)
                    parts[slot] = part;
            }
            catch (Exception e) when (e is InvalidDataException or NotSupportedException)
            {
                failures[slot] = $"The {PartLabel(slot).ToLowerInvariant()} model could not be read: {e.Message}";
            }
        }
        around ??= parts.Count > 0 ? SeamSurroundings.Read(input, input.CharacterRace) : SeamSurroundings.None;

        var images = new Dictionary<(string, uint), SeamImage?>();
        var seams = new List<BodySeam>();
        var notes = new Dictionary<BodySeamKind, string>();
        foreach (var kind in Kinds)
        {
            BodySeam? found = null, miss = null;
            foreach (var (_, a, b) in Pairs.Where(p => p.Kind == kind))
            {
                if (!parts.TryGetValue(a, out var partA) || !parts.TryGetValue(b, out var partB))
                    continue;
                var seam = Measure(kind, partA, partB, images, around);
                if (seam.Chains.Count > 0)
                {
                    found = seam;
                    break;
                }
                miss ??= seam.Findings.Count > 0 ? seam : null;
            }
            if ((found ?? miss) is { } result)
                seams.Add(result);
            else
                notes[kind] = Note(kind, parts, failures);
        }
        return new BodySeamReport { Seams = seams, Notes = notes };
    }

    /// <summary> Why a seam wasn't measured: a part is missing, unreadable or has no skin there. </summary>
    private static string Note(BodySeamKind kind, IReadOnlyDictionary<string, BodySeamPart> parts, IReadOnlyDictionary<string, string> failures)
    {
        var slots = Pairs.Where(p => p.Kind == kind).SelectMany(p => new[] { p.A, p.B }).Distinct().ToList();
        foreach (var slot in slots)
            if (failures.TryGetValue(slot, out var failure))
                return failure;
        var missing = slots.Where(slot => !parts.ContainsKey(slot) && (kind != BodySeamKind.Ankles || slot != "top")).ToList();
        if (missing.Count > 0)
            return $"No skin to compare: the {string.Join(" and ", missing.Select(s => PartLabel(s).ToLowerInvariant()))} show no body skin.";
        return kind switch
        {
            BodySeamKind.Wrists => "No skin meets at the wrists: sleeves or gloves cover them.",
            BodySeamKind.Waist => "No skin meets at the waist: clothing covers it.",
            _ => "No skin meets at the ankles: clothing or shoes cover them.",
        };
    }

    // ---- Parts -------------------------------------------------------------------------------------

    private static BodySeamPart? Part(NeckSeamModelInput input, string slot, NeckSeamInput all)
    {
        var model = input.Read();
        var deformer = RacialDeformer.None;
        if (NeckSeamAnalyzer.RaceOf(input.GamePath) is { } modelRace && all.CharacterRace is { } characterRace && modelRace != characterRace &&
            all.RacialDeformers is { } pbd)
        {
            try { deformer = RacialDeformer.Create(pbd, characterRace, modelRace); }
            catch (InvalidDataException) { deformer = RacialDeformer.None; }
        }

        var meshes = new List<(SkinMesh, NeckSeamMaterialInput, SkinMaterial)>();
        var meshOf = new List<int>();
        var vertexOf = new List<int>();
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var binormals = new List<Vector3>();
        var signs = new List<float>();
        var uv = new List<Vector2>();
        var colors = new List<Vector4>();
        var deform = new List<RacialDeformer.Affine>();
        var triangles = new List<int>();
        // The model's other drawn meshes (clothing, nails): a skin edge they continue from is covered, not open.
        var cloth = new HashSet<(long, long, long)>();
        foreach (var mesh in model.Meshes)
        {
            if (mesh.Triangles.Length == 0)
                continue;
            var material = NeckSeamAnalyzer.MaterialFor(input, mesh.Material);
            SkinMaterial? read = null;
            if (material is not null)
            {
                try { read = SkinMaterial.Read(material.Bytes); }
                catch (InvalidDataException) { read = null; }
            }
            if (material is null || read is not { IsBodySkin: true } skin)
            {
                foreach (var vertex in mesh.Triangles)
                    cloth.Add(Cell(mesh.Positions[vertex]));
                continue;
            }
            var meshIndex = meshes.Count;
            meshes.Add((mesh, material, skin));
            var matrices = deformer.BoneCount == 0 ? Array.Empty<RacialDeformer.Affine>() : mesh.Bones.Select(deformer.For).ToArray();
            var merged = new Dictionary<int, int>();
            foreach (var vertex in mesh.Triangles)
            {
                if (!merged.TryGetValue(vertex, out var index))
                {
                    index = positions.Count;
                    merged[vertex] = index;
                    var matrix = deformer.BoneCount == 0
                        ? RacialDeformer.Affine.Identity
                        : RacialDeformer.Blend(matrices, mesh.BlendIndices.AsSpan(vertex * mesh.Influences, mesh.Influences),
                            mesh.BlendWeights.AsSpan(vertex * mesh.Influences, mesh.Influences));
                    meshOf.Add(meshIndex);
                    vertexOf.Add(vertex);
                    positions.Add(matrix.Point(mesh.Positions[vertex]));
                    normals.Add(NeckSeamAnalyzer.SafeNormalize(matrix.Direction(mesh.Normals[vertex]), mesh.Normals[vertex]));
                    binormals.Add(NeckSeamAnalyzer.SafeNormalize(matrix.Direction(mesh.Binormals[vertex]), mesh.Binormals[vertex]));
                    signs.Add(mesh.BinormalSigns[vertex]);
                    uv.Add(mesh.Uv1[vertex]);
                    colors.Add(mesh.Colors[vertex]);
                    deform.Add(matrix);
                }
                triangles.Add(index);
            }
        }
        if (meshes.Count == 0)
            return null;
        var positionArray = positions.ToArray();
        var triangleArray = triangles.ToArray();
        var topology = new SeamTopology(positionArray, triangleArray);
        var covered = new bool[topology.Representative.Length];
        if (cloth.Count > 0)
            for (var v = 0; v < positionArray.Length; v++)
                if (NearCloth(meshes[meshOf[v]].Item1.Positions[vertexOf[v]], cloth))
                    covered[topology.Weld[v]] = true;
        return new BodySeamPart
        {
            Input = input, Slot = slot, Model = model, Meshes = meshes, MeshOf = meshOf.ToArray(), VertexOf = vertexOf.ToArray(),
            Positions = positionArray, Normals = normals.ToArray(), Binormals = binormals.ToArray(), Signs = signs.ToArray(), Uv = uv.ToArray(),
            Colors = colors.ToArray(), Deform = deform.ToArray(), Triangles = triangleArray, Topology = topology, Covered = covered,
        };
    }

    /// <summary> Model-space positions in 0.1 mm cells, for finding where clothing continues a skin edge. </summary>
    private static (long, long, long) Cell(Vector3 p) => ((long)MathF.Round(p.X * 1e4f), (long)MathF.Round(p.Y * 1e4f), (long)MathF.Round(p.Z * 1e4f));

    private static bool NearCloth(Vector3 p, HashSet<(long, long, long)> cloth)
    {
        var (x, y, z) = Cell(p);
        for (var dx = -1; dx <= 1; dx++)
            for (var dy = -1; dy <= 1; dy++)
                for (var dz = -1; dz <= 1; dz++)
                    if (cloth.Contains((x + dx, y + dy, z + dz)))
                        return true;
        return false;
    }

    internal static List<SeamEdge> BoundaryEdges(BodySeamPart part)
    {
        var edges = new List<SeamEdge>();
        foreach (var (a, b) in part.Topology.BoundaryEdges())
            if (part.Topology.EdgeTriangle(a, b) is { } triangle)
                edges.Add(new SeamEdge(a, b, triangle.A, triangle.B));
        return edges;
    }

    /// <summary> The nearest point to <paramref name="p"/> on the part's <paramref name="edges"/>. </summary>
    internal static SeamHit Nearest(Vector3 p, BodySeamPart part, IReadOnlyList<SeamEdge> edges)
    {
        var best = new SeamHit(float.MaxValue, -1, 0);
        for (var i = 0; i < edges.Count; i++)
        {
            var a = part.Positions[edges[i].RawA];
            var ab = part.Positions[edges[i].RawB] - a;
            var t = Math.Clamp(Vector3.Dot(p - a, ab) / Math.Max(ab.LengthSquared(), 1e-12f), 0f, 1f);
            var distance = Vector3.Distance(p, a + ab * t);
            if (distance < best.Distance)
                best = new SeamHit(distance, i, t);
        }
        return best;
    }

    internal static Vector3 PointOn(BodySeamPart part, SeamEdge edge, float t) => Vector3.Lerp(part.Positions[edge.RawA], part.Positions[edge.RawB], t);

    internal static Vector3 NormalOn(BodySeamPart part, SeamEdge edge, float t)
        => NeckSeamAnalyzer.SafeNormalize(Vector3.Lerp(part.Normals[edge.RawA], part.Normals[edge.RawB], t), part.Normals[edge.RawA]);

    // ---- Seams ----------------------------------------------------------------------------------------

    /// <summary> How one part's open edge vertices relate to the other part: meeting its edge, resting on its skin, or tucked under it. </summary>
    private sealed class EdgeStates
    {
        public Dictionary<int, SeamHit> Meets { get; } = new();
        public Dictionary<int, SeamLanding> Rests { get; } = new();
        public HashSet<int> Tucked { get; } = [];
        public bool Joined(int welded) => Meets.ContainsKey(welded) || Rests.ContainsKey(welded);
        public bool Settled(int welded) => Joined(welded) || Tucked.Contains(welded);
    }

    /// <summary>
    /// Sorts <paramref name="part"/>'s open edge vertices near <paramref name="other"/>. One that lies
    /// over the other's skin at least <see cref="OverlapMin"/> inside the other's edge overlaps it:
    /// within <see cref="WeldLimit"/> above and <see cref="RestDepth"/> inside that skin it rests on
    /// it, deeper it is tucked under it and never shows. Otherwise one within <see cref="WeldLimit"/>
    /// of the other's edge meets it, and the gap is the distance; that includes edges that miss each
    /// other by a fraction of a millimetre either way, which is how half-float positions round.
    /// </summary>
    private static EdgeStates Classify(BodySeamPart part, List<SeamEdge> edges, BodySeamPart other, List<SeamEdge> otherEdges)
    {
        var states = new EdgeStates();
        if (other.Triangles.Length < 3)
            return states;
        var low = other.Positions.Aggregate(new Vector3(float.MaxValue), Vector3.Min) - new Vector3(TuckReach);
        var high = other.Positions.Aggregate(new Vector3(float.MinValue), Vector3.Max) + new Vector3(TuckReach);
        foreach (var welded in edges.SelectMany(e => new[] { e.WeldedA, e.WeldedB }).Distinct())
        {
            var p = part.Positions[part.Topology.Representative[welded]];
            if (p.X < low.X || p.Y < low.Y || p.Z < low.Z || p.X > high.X || p.Y > high.Y || p.Z > high.Z)
                continue;
            if (other.Space.Nearest(p, TuckReach) is not { } surface)
                continue;
            var hit = otherEdges.Count > 0 ? Nearest(p, other, otherEdges) : new SeamHit(float.MaxValue, -1, 0);
            var height = Vector3.Dot(p - surface.Point, other.NormalAt(surface));
            // How far inside the other part's edge the skin under the vertex lies.
            var inside = otherEdges.Count > 0 ? Nearest(surface.Point, other, otherEdges).Distance : float.MaxValue;
            if (inside >= OverlapMin && surface.Distance <= WeldLimit && height >= -RestDepth)
                states.Rests[welded] = new SeamLanding(surface, height);
            else if (inside >= OverlapMin && height < -RestDepth)
                states.Tucked.Add(welded);
            else if (hit.Distance <= WeldLimit)
                states.Meets[welded] = hit;
        }
        return states;
    }

    private static BodySeam Measure(BodySeamKind kind, BodySeamPart a, BodySeamPart b, Dictionary<(string, uint), SeamImage?> images, SeamSurroundings around)
    {
        var edgesA = BoundaryEdges(a);
        var edgesB = BoundaryEdges(b);
        var statesA = Classify(a, edgesA, b, edgesB);
        var statesB = Classify(b, edgesB, a, edgesA);

        var chains = Walk(edgesA, statesA.Meets.Keys.ToHashSet())
            .Select(walk => Chain(a, b, walk.Order, walk.Closed, edgesB, statesA.Meets))
            .Concat(Walk(edgesA, statesA.Rests.Keys.ToHashSet()).Select(walk => RestChain(a, b, true, walk.Order, walk.Closed, statesA.Rests)))
            .Concat(Walk(edgesB, statesB.Rests.Keys.ToHashSet()).Select(walk => RestChain(b, a, false, walk.Order, walk.Closed, statesB.Rests)))
            .Where(c => c.Samples.Count > 0)
            .Select(c => Clothed(c, c.Outer ?? a, around))
            .ToList();
        if (chains.Count == 0)
            return Miss(kind, a, b, statesA, statesB, around);
        if (Apart(a, b, edgesA, edgesB, statesA, statesB) is { } apart)
        {
            var distances = chains.SelectMany(c => c.HitsA.Values.Select(h => h.Distance)).Concat(apart.Select(x => x.Distance)).ToList();
            var covered = around.Covered(apart.Select(x => (x.Part.Positions[x.Part.Topology.Representative[x.Welded]], x.Part.WeldedNormal(x.Welded))).ToList());
            return Unmet(kind, a, b, distances.Average(), distances.Max(),
                $"The {Of(a)} and the {Of(b)} skin edges at the {Title(kind).ToLowerInvariant()} only meet in places: elsewhere they run up to " +
                $"{Mm(distances.Max())} apart, too far to join. The two parts were made for different body shapes or sizes, so the check doesn't weld them. " +
                "Pick matching body options in both mods or fit one part in Blender.", covered, around);
        }
        return Evaluate(kind, a, b, chains, edgesB, images, around);
    }

    /// <summary> The chain with the share of its vertices that clothing closes in around. </summary>
    private static BodySeamChain Clothed(BodySeamChain chain, BodySeamPart walker, SeamSurroundings around)
    {
        if (around.Cloth.Empty)
            return chain;
        var covered = around.Covered(chain.A.Select(w => (walker.Positions[walker.Topology.Representative[w]], walker.WeldedNormal(w))).ToList());
        return new BodySeamChain
        {
            Side = chain.Side, A = chain.A, Closed = chain.Closed, HitsA = chain.HitsA, HitsB = chain.HitsB, EdgesA = chain.EdgesA, Samples = chain.Samples,
            GapMean = chain.GapMean, GapMax = chain.GapMax, AngleMean = chain.AngleMean, AngleMax = chain.AngleMax, WeightDiff = chain.WeightDiff,
            ColorDiff = chain.ColorDiff, MeshA = chain.MeshA, MeshB = chain.MeshB, Outer = chain.Outer, Landing = chain.Landing, Covered = covered,
        };
    }

    /// <summary>
    /// The open boundary vertices, on the loops a seam lies on, that stay within <see cref="MissLimit"/>
    /// of the other part's open edges without meeting them; null when fewer than three do. Parts made
    /// for different body shapes meet like this in places (measured: 6 to 19 mm apart elsewhere), and
    /// welding only those places would pull the edges out of shape. A skin edge that clothing
    /// continues from is covered, and one resting on or tucked under the other part's skin is joined,
    /// so neither counts.
    /// </summary>
    private static List<(BodySeamPart Part, int Welded, float Distance)>? Apart(BodySeamPart a, BodySeamPart b, List<SeamEdge> edgesA, List<SeamEdge> edgesB,
        EdgeStates statesA, EdgeStates statesB)
    {
        var apart = new List<(BodySeamPart, int, float)>();
        void Check(BodySeamPart part, EdgeStates states, BodySeamPart other, List<SeamEdge> otherEdges)
        {
            var open = otherEdges.Where(e => !other.Covered[e.WeldedA] && !other.Covered[e.WeldedB]).ToList();
            if (open.Count == 0)
                return;
            foreach (var loop in part.Topology.BoundaryLoops())
            {
                if (!loop.Any(states.Joined))
                    continue;
                foreach (var welded in loop)
                {
                    if (states.Settled(welded) || part.Covered[welded])
                        continue;
                    var distance = Nearest(part.Positions[part.Topology.Representative[welded]], other, open).Distance;
                    if (distance is > WeldLimit and <= MissLimit)
                        apart.Add((part, welded, distance));
                }
            }
        }
        Check(a, statesA, b, edgesB);
        Check(b, statesB, a, edgesA);
        return apart.Count >= 3 ? apart : null;
    }

    /// <summary> The matched boundary vertices, split into connected stretches along the part's boundary and put in walking order. </summary>
    private static List<(int[] Order, bool Closed)> Walk(IReadOnlyList<SeamEdge> edges, IReadOnlySet<int> hits)
    {
        var adjacency = new Dictionary<int, List<int>>();
        foreach (var edge in edges)
        {
            if (!hits.Contains(edge.WeldedA) || !hits.Contains(edge.WeldedB))
                continue;
            (adjacency.TryGetValue(edge.WeldedA, out var la) ? la : adjacency[edge.WeldedA] = []).Add(edge.WeldedB);
            (adjacency.TryGetValue(edge.WeldedB, out var lb) ? lb : adjacency[edge.WeldedB] = []).Add(edge.WeldedA);
        }
        var seen = new HashSet<int>();
        var result = new List<(int[], bool)>();
        foreach (var first in adjacency.Keys.OrderBy(k => k))
        {
            if (!seen.Add(first))
                continue;
            var component = new List<int>();
            var stack = new Stack<int>([first]);
            while (stack.Count > 0)
            {
                var vertex = stack.Pop();
                component.Add(vertex);
                foreach (var next in adjacency[vertex])
                    if (seen.Add(next))
                        stack.Push(next);
            }
            if (component.Count < 3)
                continue;
            var ends = component.Where(v => adjacency[v].Count == 1).ToList();
            var start = ends.Count > 0 ? ends.Min() : component.Min();
            var order = new List<int> { start };
            var visited = new HashSet<int> { start };
            var current = start;
            while (adjacency[current].FirstOrDefault(n => !visited.Contains(n), -1) is var step and >= 0)
            {
                visited.Add(step);
                order.Add(step);
                current = step;
            }
            var closed = ends.Count == 0 && component.All(v => adjacency[v].Count == 2) && adjacency[current].Contains(start) && order.Count == component.Count;
            if (order.Count >= 3)
                result.Add((order.ToArray(), closed));
        }
        return result;
    }

    private static BodySeamChain Chain(BodySeamPart a, BodySeamPart b, int[] order, bool closed, IReadOnlyList<SeamEdge> edgesB,
        IReadOnlyDictionary<int, SeamHit> hits)
    {
        var edgesA = new List<SeamEdge>();
        for (var i = 0; i < order.Length - (closed ? 0 : 1); i++)
        {
            int w1 = order[i], w2 = order[(i + 1) % order.Length];
            if (a.Topology.EdgeTriangle(w1, w2) is { } triangle)
                edgesA.Add(new SeamEdge(w1, w2, triangle.A, triangle.B));
        }

        // The second part's boundary vertices within reach of this stretch.
        var hitsB = new Dictionary<int, SeamHit>();
        foreach (var welded in edgesB.SelectMany(e => new[] { e.WeldedA, e.WeldedB }).Distinct())
        {
            var hit = Nearest(b.Positions[b.Topology.Representative[welded]], a, edgesA);
            if (hit.Distance <= WeldLimit)
                hitsB[welded] = hit;
        }

        // Samples along the stretch, each with the nearest point of the second part.
        var positions = new List<Vector3>();
        var aUv = new List<Vector2>();
        var aNormal = new List<Vector3>();
        var aBinormal = new List<Vector3>();
        var aSign = new List<float>();
        var bUv = new List<Vector2>();
        var bNormal = new List<Vector3>();
        var bBinormal = new List<Vector3>();
        var bSign = new List<float>();
        var gap = new List<float>();
        for (var e = 0; e < edgesA.Count; e++)
        {
            var edge = edgesA[e];
            var last = !closed && e == edgesA.Count - 1;
            for (var s = 0; s <= (last ? SamplesPerEdge : SamplesPerEdge - 1); s++)
            {
                var t = s / (float)SamplesPerEdge;
                var p = Vector3.Lerp(a.Positions[edge.RawA], a.Positions[edge.RawB], t);
                positions.Add(p);
                aUv.Add(Vector2.Lerp(a.Uv[edge.RawA], a.Uv[edge.RawB], t));
                aNormal.Add(NeckSeamAnalyzer.SafeNormalize(Vector3.Lerp(a.Normals[edge.RawA], a.Normals[edge.RawB], t), a.Normals[edge.RawA]));
                aBinormal.Add(Vector3.Lerp(a.Binormals[edge.RawA], a.Binormals[edge.RawB], t));
                aSign.Add(a.Signs[edge.RawA]);
                var hit = Nearest(p, b, edgesB);
                var other = edgesB[hit.Edge];
                bUv.Add(Vector2.Lerp(b.Uv[other.RawA], b.Uv[other.RawB], hit.T));
                bNormal.Add(NormalOn(b, other, hit.T));
                bBinormal.Add(Vector3.Lerp(b.Binormals[other.RawA], b.Binormals[other.RawB], hit.T));
                bSign.Add(b.Signs[other.RawA]);
                gap.Add(hit.Distance);
            }
        }
        var arc = new float[positions.Count];
        for (var i = 1; i < arc.Length; i++)
            arc[i] = arc[i - 1] + Vector3.Distance(positions[i - 1], positions[i]);
        var samples = new NeckSeamAnalyzer.Seam
        {
            Positions = positions.ToArray(), FaceUv = aUv.ToArray(), FaceNormal = aNormal.ToArray(), FaceBinormal = aBinormal.ToArray(),
            FaceSign = aSign.ToArray(), BodyUv = bUv.ToArray(), BodyNormal = bNormal.ToArray(), BodyBinormal = bBinormal.ToArray(),
            BodySign = bSign.ToArray(), Gap = gap.ToArray(), Arc = arc, Closed = closed,
        };

        // Gaps both ways, normals, weights and colours at the vertices.
        var gaps = order.Select(w => hits[w].Distance).Concat(hitsB.Values.Select(h => h.Distance)).ToList();
        var angles = new List<float>();
        var weightDiff = 0f;
        Vector4 colorA = Vector4.Zero, colorB = Vector4.Zero;
        foreach (var welded in order)
        {
            var hit = hits[welded];
            var other = edgesB[hit.Edge];
            angles.Add(NeckSeamAnalyzer.AngleDegrees(a.WeldedNormal(welded), NormalOn(b, other, hit.T)));
            var vertex = a.Topology.Representative[welded];
            var nearer = hit.T < 0.5f ? other.RawA : other.RawB;
            var wa = a.Weights(vertex);
            var wb = b.Weights(nearer);
            weightDiff = Math.Max(weightDiff, wa.Keys.Union(wb.Keys).Sum(k => MathF.Abs(wa.GetValueOrDefault(k) - wb.GetValueOrDefault(k))) / 2);
            colorA += a.Colors[vertex];
            colorB += b.Colors[nearer];
        }

        var centre = positions.Aggregate(Vector3.Zero, (sum, p) => sum + p) / Math.Max(1, positions.Count);
        return new BodySeamChain
        {
            Side = centre.X > 0.02f ? "left" : centre.X < -0.02f ? "right" : string.Empty,
            A = order, Closed = closed, HitsA = order.ToDictionary(w => w, w => hits[w]), HitsB = hitsB, EdgesA = edgesA, Samples = samples,
            GapMean = gaps.Average(), GapMax = gaps.Max(), AngleMean = angles.Average(), AngleMax = angles.Max(), WeightDiff = weightDiff,
            ColorDiff = Vector4.Abs(colorA - colorB) / order.Length,
            MeshA = Majority(edgesA.SelectMany(e => new[] { a.MeshOf[e.RawA], a.MeshOf[e.RawB] })),
            MeshB = Majority(order.Select(w => edgesB[hits[w].Edge]).SelectMany(e => new[] { b.MeshOf[e.RawA], b.MeshOf[e.RawB] })),
        };
    }

    /// <summary>
    /// A stretch where <paramref name="outer"/>'s edge rests on <paramref name="inner"/>'s skin (the
    /// parts overlap): samples along the outer edge paired with the skin under them. The gap is the
    /// edge's distance from that skin, which shows as a step where it stands off.
    /// </summary>
    private static BodySeamChain RestChain(BodySeamPart outer, BodySeamPart inner, bool outerFirst, int[] order, bool closed,
        IReadOnlyDictionary<int, SeamLanding> landing)
    {
        var edges = new List<SeamEdge>();
        for (var i = 0; i < order.Length - (closed ? 0 : 1); i++)
        {
            int w1 = order[i], w2 = order[(i + 1) % order.Length];
            if (outer.Topology.EdgeTriangle(w1, w2) is { } triangle)
                edges.Add(new SeamEdge(w1, w2, triangle.A, triangle.B));
        }

        var positions = new List<Vector3>();
        var outerUv = new List<Vector2>();
        var outerNormal = new List<Vector3>();
        var outerBinormal = new List<Vector3>();
        var outerSign = new List<float>();
        var innerUv = new List<Vector2>();
        var innerNormal = new List<Vector3>();
        var innerBinormal = new List<Vector3>();
        var innerSign = new List<float>();
        var gap = new List<float>();
        for (var e = 0; e < edges.Count; e++)
        {
            var edge = edges[e];
            var last = !closed && e == edges.Count - 1;
            for (var s = 0; s <= (last ? SamplesPerEdge : SamplesPerEdge - 1); s++)
            {
                var t = s / (float)SamplesPerEdge;
                var p = Vector3.Lerp(outer.Positions[edge.RawA], outer.Positions[edge.RawB], t);
                var under = inner.Space.Nearest(p, TuckReach) ?? landing[t < 0.5f ? edge.WeldedA : edge.WeldedB].Point;
                positions.Add(p);
                outerUv.Add(Vector2.Lerp(outer.Uv[edge.RawA], outer.Uv[edge.RawB], t));
                outerNormal.Add(NeckSeamAnalyzer.SafeNormalize(Vector3.Lerp(outer.Normals[edge.RawA], outer.Normals[edge.RawB], t), outer.Normals[edge.RawA]));
                outerBinormal.Add(Vector3.Lerp(outer.Binormals[edge.RawA], outer.Binormals[edge.RawB], t));
                outerSign.Add(outer.Signs[edge.RawA]);
                innerUv.Add(inner.UvAt(under));
                innerNormal.Add(inner.NormalAt(under));
                innerBinormal.Add(inner.BinormalAt(under));
                innerSign.Add(inner.SignAt(under));
                gap.Add(under.Distance);
            }
        }
        var arc = new float[positions.Count];
        for (var i = 1; i < arc.Length; i++)
            arc[i] = arc[i - 1] + Vector3.Distance(positions[i - 1], positions[i]);
        // The face fields hold the first part, the body fields the second, whichever rests on the other.
        var samples = new NeckSeamAnalyzer.Seam
        {
            Positions = positions.ToArray(),
            FaceUv = (outerFirst ? outerUv : innerUv).ToArray(), FaceNormal = (outerFirst ? outerNormal : innerNormal).ToArray(),
            FaceBinormal = (outerFirst ? outerBinormal : innerBinormal).ToArray(), FaceSign = (outerFirst ? outerSign : innerSign).ToArray(),
            BodyUv = (outerFirst ? innerUv : outerUv).ToArray(), BodyNormal = (outerFirst ? innerNormal : outerNormal).ToArray(),
            BodyBinormal = (outerFirst ? innerBinormal : outerBinormal).ToArray(), BodySign = (outerFirst ? innerSign : outerSign).ToArray(),
            Gap = gap.ToArray(), Arc = arc, Closed = closed,
        };

        var gaps = new List<float>();
        var angles = new List<float>();
        var weightDiff = 0f;
        Vector4 colorOuter = Vector4.Zero, colorInner = Vector4.Zero;
        foreach (var welded in order)
        {
            var point = landing[welded].Point;
            gaps.Add(point.Distance);
            angles.Add(NeckSeamAnalyzer.AngleDegrees(outer.WeldedNormal(welded), inner.NormalAt(point)));
            var vertex = outer.Topology.Representative[welded];
            var corner = inner.CornerOf(point);
            var wo = outer.Weights(vertex);
            var wi = inner.Weights(corner);
            weightDiff = Math.Max(weightDiff, wo.Keys.Union(wi.Keys).Sum(k => MathF.Abs(wo.GetValueOrDefault(k) - wi.GetValueOrDefault(k))) / 2);
            colorOuter += outer.Colors[vertex];
            colorInner += inner.Colors[corner];
        }
        var meshOuter = Majority(edges.SelectMany(e => new[] { outer.MeshOf[e.RawA], outer.MeshOf[e.RawB] }));
        var meshInner = Majority(order.Select(w => inner.MeshOf[inner.CornerOf(landing[w].Point)]));
        var centre = positions.Aggregate(Vector3.Zero, (sum, p) => sum + p) / Math.Max(1, positions.Count);
        return new BodySeamChain
        {
            Side = centre.X > 0.02f ? "left" : centre.X < -0.02f ? "right" : string.Empty,
            A = order, Closed = closed, HitsA = new Dictionary<int, SeamHit>(), HitsB = new Dictionary<int, SeamHit>(), EdgesA = edges, Samples = samples,
            GapMean = gaps.Average(), GapMax = gaps.Max(), AngleMean = angles.Average(), AngleMax = angles.Max(), WeightDiff = weightDiff,
            ColorDiff = Vector4.Abs(colorOuter - colorInner) / order.Length,
            MeshA = outerFirst ? meshOuter : meshInner, MeshB = outerFirst ? meshInner : meshOuter,
            Outer = outer, Landing = order.ToDictionary(w => w, w => landing[w]),
        };
    }

    private static int Majority(IEnumerable<int> values) => values.GroupBy(v => v).MaxBy(g => g.Count())?.Key ?? 0;

    /// <summary>
    /// A note when the part stores the seam's positions as half floats: they round to steps of
    /// 2^(e − 10) for a coordinate of 2^e to 2^(e + 1), about 1 mm at the waist and wrists. Parts saved
    /// that way miss a full-precision part by up to a step, which is where most sub-millimetre gaps come from.
    /// </summary>
    private static string? HalfPrecision(BodySeamPart part, IEnumerable<int> vertices)
    {
        var largest = 0f;
        foreach (var vertex in vertices)
        {
            var mesh = part.Meshes[part.MeshOf[vertex]].Mesh;
            if (!mesh.HalfPositions)
                continue;
            var p = Vector3.Abs(mesh.Positions[part.VertexOf[vertex]]);
            largest = Math.Max(largest, Math.Max(p.X, Math.Max(p.Y, p.Z)));
        }
        if (largest <= 0)
            return null;
        var step = MathF.Pow(2, MathF.Floor(MathF.Log2(largest)) - 10);
        return $" The {Lower(part)} stores its vertex positions as half floats, which round them to {Mm(step)} steps here; the fix puts both edges on a position it can hold.";
    }

    /// <summary>
    /// When no edges meet or rest: whether two open skin edges of similar size lie close enough that
    /// they should have met. Such parts were made for different bodies (or body sizes), which no weld
    /// can join. Loops that are mostly covered by the model's own clothing, or rest on or are tucked
    /// under the other part's skin, don't count.
    /// </summary>
    private static BodySeam Miss(BodySeamKind kind, BodySeamPart a, BodySeamPart b, EdgeStates statesA, EdgeStates statesB, SeamSurroundings around)
    {
        static bool Open(BodySeamPart part, EdgeStates states, int[] loop)
            => loop.Length >= 4 && loop.Count(w => part.Covered[w] || states.Settled(w)) * 2 < loop.Length;
        (float Mean, float Max, int[] LoopA, int[] LoopB)? best = null;
        var loopsB = b.Topology.BoundaryLoops().Where(l => Open(b, statesB, l)).Select(l => (Loop: l, Points: l.Select(w => b.Positions[b.Topology.Representative[w]]).ToArray())).ToList();
        foreach (var loopA in a.Topology.BoundaryLoops().Where(l => Open(a, statesA, l)))
        {
            var pointsA = loopA.Select(w => a.Positions[a.Topology.Representative[w]]).ToArray();
            var lengthA = Perimeter(pointsA);
            foreach (var (loopB, pointsB) in loopsB)
            {
                var ratio = lengthA / Math.Max(Perimeter(pointsB), 1e-6f);
                if (ratio is < 0.75f or > 1.33f)
                    continue;
                var toB = pointsA.Select(p => DistanceToLoop(p, pointsB)).ToList();
                var toA = pointsB.Select(p => DistanceToLoop(p, pointsA)).ToList();
                var mean = Math.Max(toB.Average(), toA.Average());
                if (mean <= MissLimit && (best is null || mean < best.Value.Mean))
                    best = (mean, Math.Max(toB.Max(), toA.Max()), loopA, loopB);
            }
        }
        if (best is not { } miss)
            return Unmet(kind, a, b, 0, 0, null, 0, around);
        var edge = miss.LoopA.Select(w => (a.Positions[a.Topology.Representative[w]], a.WeldedNormal(w)))
            .Concat(miss.LoopB.Select(w => (b.Positions[b.Topology.Representative[w]], b.WeldedNormal(w)))).ToList();
        return Unmet(kind, a, b, miss.Mean, miss.Max,
            $"The {Of(a)} and the {Of(b)} skin edges at the {Title(kind).ToLowerInvariant()} don't meet: they are {Mm(miss.Mean)} apart on average " +
            $"and {Mm(miss.Max)} at most, too far to join here, and neither rests on the other's skin. The two parts were made for different body " +
            $"shapes or sizes (their edges have {miss.LoopA.Length} and {miss.LoopB.Length} vertices). Pick matching body options in both mods or fit one part in Blender.",
            around.Covered(edge), around, edge.Select(p => p.Item1).ToList());
    }

    /// <summary>
    /// A seam whose edges don't meet: one finding saying how far apart they are (none when they aren't
    /// near each other), the seam connector under them, and no fix. Clothing covering them makes it a note.
    /// </summary>
    private static BodySeam Unmet(BodySeamKind kind, BodySeamPart a, BodySeamPart b, float mean, float max, string? detail, float covered,
        SeamSurroundings around, IReadOnlyList<Vector3>? edge = null)
    {
        var findings = new List<NeckSeamFinding>();
        if (detail is not null)
        {
            var hidden = covered >= HiddenShare;
            findings.Add(new NeckSeamFinding("Edges meet", Mm(mean) + " avg", Mm(max) + " max", hidden ? NeckSeamSeverity.Info : NeckSeamSeverity.Problem,
                hidden ? $"Clothing covers these edges, so the gap doesn't show. {detail}" : detail, NeckSeamFixKind.None));
            if (Connector(kind, $"at the {Title(kind).ToLowerInvariant()}", a, b, edge ?? [], SkinMaterialOf(a), gap: true, around) is { Finding: var connector })
                findings.Add(hidden ? Quiet(connector) : connector);
        }
        return new BodySeam
        {
            Kind = kind, A = a, B = b, Chains = [], Findings = findings,
            MaterialPathA = "", MaterialPathB = "", Material = null, TexturesToBlend = new HashSet<uint>(), ImagesA = new Dictionary<uint, SeamImage>(),
            ImagesB = new Dictionary<uint, SeamImage>(), GapMax = max, NormalMax = 0,
        };
    }

    /// <summary> The finding at most as a note. </summary>
    private static NeckSeamFinding Quiet(NeckSeamFinding finding)
        => finding.Severity > NeckSeamSeverity.Info ? finding with { Severity = NeckSeamSeverity.Info } : finding;

    /// <summary> The skin material a part draws most of its skin with. </summary>
    private static string SkinMaterialOf(BodySeamPart part)
        => PathRules.NormalizeGamePath(part.Meshes[Majority(part.MeshOf)].Material.GamePath);

    /// <summary>
    /// What the game's seam connector does at this seam: whether one is drawn, whether it lies under
    /// the seam (so it fills a gap with skin), whether it sticks out of the skin where no clothing
    /// covers it, and which skin material it draws. Null when no connector for this seam is loaded.
    /// </summary>
    /// <param name="seam">Points along the seam.</param>
    /// <param name="gap">Whether the edges leave a gap the connector would show through.</param>
    private static (NeckSeamFinding Finding, bool Fills)? Connector(BodySeamKind kind, string where, BodySeamPart a, BodySeamPart b, IReadOnlyList<Vector3> seam,
        string skinMaterial, bool gap, SeamSurroundings around)
        => ConnectorFinding(ConnectorKind(kind), where, [(a.Space, a.NormalAt), (b.Space, b.NormalAt)], seam, skinMaterial, gap, around);

    /// <summary> The connector reach: a seam point this close to a connector lies over it. </summary>
    private const float ConnectorReach = 0.003f;

    internal static (NeckSeamFinding Finding, bool Fills)? ConnectorFinding(string kind, string where, IReadOnlyList<(SeamSpace Space, Func<SurfacePoint, Vector3> Normal)> skins,
        IReadOnlyList<Vector3> seam, string skinMaterial, bool gap, SeamSurroundings around)
    {
        var bands = around.For(kind).ToList();
        if (bands.Count == 0)
        {
            if (!around.Loaded.Contains(kind))
                return null;
            return (new NeckSeamFinding("Seam connector", "Not drawn", "", gap ? NeckSeamSeverity.Info : NeckSeamSeverity.Ok,
                gap ? $"The game draws no seam connector {where}, so a gap there shows through." : $"The game draws no seam connector {where}; where the edges meet, none is needed.",
                NeckSeamFixKind.None), false);
        }
        var model = FileName(bands[0].ModelPath);
        var material = bands.FirstOrDefault(b => b.MaterialPath.Length > 0)?.MaterialPath ?? string.Empty;
        var under = seam.Count == 0 ? 0 : seam.Count(p => bands.Any(band => band.Space.Nearest(p, ConnectorReach) is not null)) / (float)seam.Count;
        var (poke, poked) = Poke(bands, skins, around);
        // A connector whose material wasn't loaded can't be compared, so it doesn't count as different.
        var same = skinMaterial.Length == 0 || bands.All(band => band.MaterialPath.Length == 0 || string.Equals(band.MaterialPath, skinMaterial, StringComparison.OrdinalIgnoreCase));
        var fills = under >= 0.5f && poked == 0;
        NeckSeamSeverity severity;
        string detail;
        if (poked > 0)
        {
            severity = poke > 0.001f ? NeckSeamSeverity.Problem : NeckSeamSeverity.Warning;
            detail = $"The seam connector ({model}) sticks out of the skin by up to {Mm(poke)} {where} where no clothing covers it, so part of it shows as a band of skin. " +
                     "It was shaped for a different body than these parts; body mods ship connectors shaped for their bodies as a mod option.";
        }
        else if (under < 0.5f)
        {
            severity = gap ? NeckSeamSeverity.Info : NeckSeamSeverity.Ok;
            detail = $"The seam connector ({model}) is drawn but doesn't lie under this seam, so it can't fill a gap here.";
        }
        else if (gap)
        {
            severity = same ? NeckSeamSeverity.Info : NeckSeamSeverity.Warning;
            detail = same
                ? $"The seam connector ({model}) lies just inside the skin under the seam and fills the gap with skin of the same material, so it shows as a crease at most."
                : $"The seam connector ({model}) lies under the seam and fills the gap, but with {FileName(material)} instead of the skin's {FileName(skinMaterial)}, so the gap shows differently coloured skin.";
        }
        else
        {
            severity = NeckSeamSeverity.Ok;
            detail = $"The seam connector ({model}) lies just inside the skin under the seam. It only shows where a gap opens, such as when the edges part in a pose." +
                     (same ? "" : $" It draws {FileName(material)}, not the skin's {FileName(skinMaterial)}.");
        }
        if (!around.DrawStateKnown)
            detail = "Which parts the game draws couldn't be read, so this assumes it draws the connector. " + detail;
        return (new NeckSeamFinding("Seam connector", around.DrawStateKnown ? "Drawn" : "Loaded", material.Length > 0 ? FileName(material) : "—", severity, detail,
            NeckSeamFixKind.None), fills);
    }

    /// <summary> How far a connector reaches out of the skin where no clothing covers it: the largest height, and how many of its vertices do. </summary>
    private static (float Height, int Count) Poke(IEnumerable<SeamConnectorBand> bands, IReadOnlyList<(SeamSpace Space, Func<SurfacePoint, Vector3> Normal)> skins,
        SeamSurroundings around)
    {
        const float reach = 0.03f;
        float highest = 0;
        var count = 0;
        foreach (var band in bands)
            foreach (var vertex in band.Space.Triangles.Distinct())
            {
                var p = band.Space.Positions[vertex];
                var lowest = float.MaxValue;
                foreach (var (space, normal) in skins)
                {
                    if (space.Nearest(p, reach) is not { } point)
                        continue;
                    lowest = Math.Min(lowest, Vector3.Dot(p - point.Point, normal(point)));
                }
                // Under some skin, or away from all of it (then nothing tells where the surface is).
                if (lowest <= PokeFloor || lowest == float.MaxValue || around.Cloth.Encloses(p, band.Normals[vertex]))
                    continue;
                highest = Math.Max(highest, lowest);
                count++;
            }
        return (highest, count);
    }

    private static float Perimeter(Vector3[] loop)
    {
        var length = 0f;
        for (var i = 0; i < loop.Length; i++)
            length += Vector3.Distance(loop[i], loop[(i + 1) % loop.Length]);
        return length;
    }

    private static float DistanceToLoop(Vector3 p, Vector3[] loop)
    {
        var best = float.MaxValue;
        for (var i = 0; i < loop.Length; i++)
        {
            var a = loop[i];
            var ab = loop[(i + 1) % loop.Length] - a;
            var t = Math.Clamp(Vector3.Dot(p - a, ab) / Math.Max(ab.LengthSquared(), 1e-12f), 0f, 1f);
            best = Math.Min(best, Vector3.Distance(p, a + ab * t));
        }
        return best;
    }

    // ---- Findings -------------------------------------------------------------------------------------

    private static BodySeam Evaluate(BodySeamKind kind, BodySeamPart a, BodySeamPart b, List<BodySeamChain> chains, List<SeamEdge> edgesB,
        Dictionary<(string, uint), SeamImage?> cache, SeamSurroundings around)
    {
        string la = Lower(a), lb = Lower(b);
        var where = Where(kind, chains);
        var findings = new List<NeckSeamFinding>();

        // ---- Geometry: edges that meet leave a gap; an edge resting on the other part's skin can stand off it as a step ----
        var gapMax = chains.Max(c => c.GapMax);
        var gapMean = chains.Sum(c => c.GapMean * c.A.Length) / chains.Sum(c => c.A.Length);
        var meets = chains.Where(c => !c.Overlap).ToList();
        var rests = chains.Where(c => c.Overlap).ToList();
        var meetMax = meets.Count > 0 ? meets.Max(c => c.GapMax) : 0;
        var restMax = rests.Count > 0 ? rests.Max(c => c.GapMax) : 0;
        var connector = Connector(kind, where, a, b, chains.SelectMany(c => c.Samples.Positions).ToList(), a.Meshes.Count > 0
            ? PathRules.NormalizeGamePath(a.Meshes[chains.MaxBy(c => c.A.Length)!.MeshA].Material.GamePath) : "", gap: meetMax > GapFloor, around);
        var filled = connector is { Fills: true };
        var gapSeverity = meetMax <= GapFloor ? NeckSeamSeverity.Ok
            : filled ? (meetMax <= 0.0005f ? NeckSeamSeverity.Ok : NeckSeamSeverity.Warning)
            : meetMax <= 0.0005f ? NeckSeamSeverity.Warning : NeckSeamSeverity.Problem;
        var stepSeverity = restMax <= GapFloor ? NeckSeamSeverity.Ok : restMax <= 0.0005f ? NeckSeamSeverity.Warning : NeckSeamSeverity.Problem;
        var fit = new List<string>();
        if (meets.Count > 0)
        {
            var meetMean = meets.Sum(c => c.GapMean * c.A.Length) / meets.Sum(c => c.A.Length);
            fit.Add($"The {Of(a)} and the {Of(b)} skin edges lie {Mm(meetMean)} apart on average ({Mm(meetMax)} at most) {Where(kind, meets)}." +
                    (meetMax <= GapFloor ? " They are joined."
                        : (filled ? " The game's seam connector under them fills the gap with skin, so it shows as a crease at most." : " Light shows through the gap.") +
                          " The fix moves the edges onto each other; the slider picks which part moves." +
                          (HalfPrecision(a, meets.SelectMany(c => c.A.Select(w => a.Topology.Representative[w])))
                           ?? HalfPrecision(b, meets.SelectMany(c => c.HitsB.Keys.Select(w => b.Topology.Representative[w]))))));
        }
        foreach (var group in rests.GroupBy(c => c.Outer!))
        {
            var outer = group.Key;
            var inner = outer == a ? b : a;
            var max = group.Max(c => c.GapMax);
            fit.Add($"The {Of(Lower(outer))} skin edge rests on the {Of(Lower(inner))} skin {Where(kind, group.ToList())}: the parts overlap, so no gap can show. " +
                    $"It lies {Mm(group.Sum(c => c.GapMean * c.A.Length) / group.Sum(c => c.A.Length))} from that skin on average ({Mm(max)} at most)." +
                    (max <= GapFloor ? ""
                        : $" Where it stands off, a step shows; the fix lays the edge onto the skin under it, moving only the {Lower(outer)}." +
                          HalfPrecision(outer, group.SelectMany(c => c.A.Select(w => outer.Topology.Representative[w])))));
        }
        findings.Add(new NeckSeamFinding("Edge fit", Mm(gapMean) + " avg", Mm(gapMax) + " max", (NeckSeamSeverity)Math.Max((int)gapSeverity, (int)stepSeverity),
            string.Join(" ", fit), gapMax > GapFloor ? NeckSeamFixKind.Weld : NeckSeamFixKind.None));

        var angleMax = chains.Max(c => c.AngleMax);
        var angleMean = chains.Sum(c => c.AngleMean * c.A.Length) / chains.Sum(c => c.A.Length);
        findings.Add(new NeckSeamFinding("Vertex normals at the edge", Deg(angleMean) + " avg", Deg(angleMax) + " max",
            angleMax <= NormalFloor ? NeckSeamSeverity.Ok : angleMax <= 4f ? NeckSeamSeverity.Warning : NeckSeamSeverity.Problem,
            angleMax <= NormalFloor
                ? "Both edges shade with the same vertex normals."
                : "Each part lights its edge with its own vertex normals, so the shading changes abruptly along the seam. The fix gives both edges the same normals.",
            angleMax > NormalFloor ? NeckSeamFixKind.Normals : NeckSeamFixKind.None));

        var weightDiff = chains.Max(c => c.WeightDiff);
        findings.Add(new NeckSeamFinding("Skin weights at the edge", "", Percent(weightDiff),
            weightDiff > 0.05f ? NeckSeamSeverity.Warning : NeckSeamSeverity.Ok,
            weightDiff > 0.05f
                ? "The two parts weight their edges to different bones, so the edges can separate when the character moves. This needs a model edit in Blender."
                : "Both edges follow the same bones, so they stay together when the character moves.",
            NeckSeamFixKind.None));

        var colorDiff = chains.Aggregate(Vector4.Zero, (max, c) => Vector4.Max(max, c.ColorDiff));
        if (colorDiff.X > 0.05f || colorDiff.Z > 0.05f || colorDiff.W > 0.05f)
            findings.Add(new NeckSeamFinding("Vertex colour at the edge", "", $"R {colorDiff.X:0.00} · B {colorDiff.Z:0.00} · A {colorDiff.W:0.00}",
                NeckSeamSeverity.Info,
                "Vertex colour scales the skin shader at the edge: red how much muscle tone sharpens the normal map, blue the specular strength, alpha the detail tile. Differences need a model edit in Blender.",
                NeckSeamFixKind.None));

        // ---- Material ----
        var main = chains.MaxBy(c => c.A.Length)!;
        var (_, materialA, skinA) = a.Meshes[main.MeshA];
        var (_, materialB, skinB) = b.Meshes[main.MeshB];
        var pathA = PathRules.NormalizeGamePath(materialA.GamePath);
        var pathB = PathRules.NormalizeGamePath(materialB.GamePath);
        var sameMaterial = string.Equals(pathA, pathB, StringComparison.OrdinalIgnoreCase);
        var imagesA = Images(materialA, skinA, cache);
        var imagesB = sameMaterial ? imagesA : Images(materialB, skinB, cache);
        findings.Add(new NeckSeamFinding("Skin material", FileName(pathA), FileName(pathB), sameMaterial ? NeckSeamSeverity.Ok : NeckSeamSeverity.Info,
            sameMaterial
                ? $"Both parts use {pathA}, so their skin settings and textures are the same files."
                : $"The {la} uses {pathA} and the {lb} {pathB}. Different skin materials can bring different settings, textures and UV layouts.",
            NeckSeamFixKind.None));

        var normalAlphaA = NormalAlpha(imagesA, chains, first: true);
        var normalAlphaB = NormalAlpha(imagesB, chains, first: false);
        NeckSeamMaterialMatch? match = null;
        if (!sameMaterial)
            match = MaterialMatch(findings, a, b, chains, skinA, skinB, normalAlphaA, normalAlphaB, la, lb);

        // ---- Textures along the seam ----
        var comparisons = chains.Select(c => NeckSeamAnalyzer.CompareTextures(c.Samples, imagesA, imagesB, snapped: false)).ToList();
        var blend = new HashSet<uint>();
        TextureFindings(findings, chains, comparisons, imagesA, imagesB, blend, la, lb);
        if (sameMaterial && imagesA.ContainsKey(SkinMaterial.NormalSampler))
        {
            var tileAlpha = skinA.Constant(SkinMaterial.TileAlpha)[0];
            var strengthA = tileAlpha * normalAlphaA;
            var strengthB = tileAlpha * normalAlphaB;
            var off = MathF.Abs(strengthA - strengthB) > 0.05f;
            if (off)
                blend.Add(SkinMaterial.NormalSampler);
            findings.Add(new NeckSeamFinding("Skin detail tile strength", $"{strengthA:0.00}", $"{strengthB:0.00}", off ? NeckSeamSeverity.Problem : NeckSeamSeverity.Ok,
                "How strongly the pore tile shows: g_TileAlpha times the normal map's alpha at the seam." +
                (off ? " Both parts share the material, so the difference is in the normal map's alpha on either side; the fix blends it." : ""),
                off ? NeckSeamFixKind.Textures : NeckSeamFixKind.None));
        }

        if (connector is { Finding: var connectorFinding })
            findings.Add(connectorFinding);
        Clothing(findings, chains.Sum(c => c.Covered * c.A.Length) / chains.Sum(c => c.A.Length), where);

        return new BodySeam
        {
            Kind = kind, A = a, B = b, Chains = chains, Findings = findings, MaterialPathA = pathA, MaterialPathB = pathB, Material = match,
            TexturesToBlend = blend, ImagesA = imagesA, ImagesB = imagesB, EdgesB = edgesB, MaterialA = (materialA, skinA), MaterialB = (materialB, skinB),
            GapMax = gapMax, NormalMax = angleMax,
        };
    }

    /// <summary>
    /// Leads the findings with how much of the seam clothing covers: all of it (then every finding is
    /// only a note, since nothing shows with this outfit), or a share worth knowing.
    /// </summary>
    public const string CoveredTitle = "Covered by clothing";

    internal static void Clothing(List<NeckSeamFinding> findings, float covered, string where)
    {
        if (covered >= HiddenShare)
        {
            for (var i = 0; i < findings.Count; i++)
                findings[i] = Quiet(findings[i]);
            findings.Insert(0, new NeckSeamFinding(CoveredTitle, $"{covered * 100:0}%", "", NeckSeamSeverity.Info,
                $"Clothing closes in around the seam {where}, so it doesn't show with this outfit. The fixes still work for outfits that leave it bare.",
                NeckSeamFixKind.None));
        }
        else if (covered >= 0.25f)
            findings.Insert(0, new NeckSeamFinding("Partly covered by clothing", $"{covered * 100:0}%", "", NeckSeamSeverity.Info,
                $"Clothing closes in around {covered * 100:0}% of the seam {where}; the rest shows.", NeckSeamFixKind.None));
    }

    /// <summary> Skin settings of two different skin materials, compared as the neck does, with the detail tile size in tiles per metre. </summary>
    private static NeckSeamMaterialMatch MaterialMatch(List<NeckSeamFinding> findings, BodySeamPart a, BodySeamPart b, List<BodySeamChain> chains,
        SkinMaterial skinA, SkinMaterial skinB, float normalAlphaA, float normalAlphaB, string la, string lb)
    {
        var seamPoints = chains.SelectMany(c => c.Samples.Positions).ToArray();
        var densityA = UvDensity(a, seamPoints);
        var densityB = UvDensity(b, seamPoints);
        var scaleA = skinA.Constant(SkinMaterial.TileScale);
        var scaleB = skinB.Constant(SkinMaterial.TileScale);
        var scaleOff = false;
        if (scaleA.Length == 2 && scaleB.Length == 2 && densityA > 0 && densityB > 0)
        {
            var tilesA = scaleA.Average() * densityA;
            var tilesB = scaleB.Average() * densityB;
            var ratio = tilesA / tilesB;
            scaleOff = ratio > TileRatioLimit || ratio < 1 / TileRatioLimit;
            findings.Add(new NeckSeamFinding("Skin detail tile size", $"{tilesA:0} per m", $"{tilesB:0} per m",
                scaleOff ? NeckSeamSeverity.Problem : NeckSeamSeverity.Ok,
                $"The skin pore tile repeats {Ratio(ratio)} on the {la} at the seam ({la} g_TileScale {Join(scaleA)} over {densityA:0.00} UV/m, " +
                $"{lb} {Join(scaleB)} over {densityB:0.00} UV/m). Vanilla's own seams differ by up to about 1.5 times." +
                (scaleOff ? " The fix brings both to the same size; choose where they meet." : ""),
                scaleOff ? NeckSeamFixKind.Material : NeckSeamFixKind.None));
        }

        var alphaA = skinA.Constant(SkinMaterial.TileAlpha)[0];
        var alphaB = skinB.Constant(SkinMaterial.TileAlpha)[0];
        var alphaOff = MathF.Abs(alphaA * normalAlphaA - alphaB * normalAlphaB) > 0.05f;
        findings.Add(new NeckSeamFinding("Skin detail tile strength", $"{alphaA * normalAlphaA:0.00}", $"{alphaB * normalAlphaB:0.00}",
            alphaOff ? NeckSeamSeverity.Warning : NeckSeamSeverity.Ok,
            "How strongly the pore tile shows: g_TileAlpha times the normal map's alpha at the seam.",
            alphaOff ? NeckSeamFixKind.Material : NeckSeamFixKind.None));

        var indexA = skinA.Constant(SkinMaterial.TileIndex)[0];
        var indexB = skinB.Constant(SkinMaterial.TileIndex)[0];
        var indexOff = MathF.Abs(indexA - indexB) > 0.01f;
        if (indexOff)
            findings.Add(new NeckSeamFinding("Skin detail tile pattern", $"{indexA:0}", $"{indexB:0}", NeckSeamSeverity.Problem,
                "The two parts use different pore patterns (g_TileIndex). A pattern can't be blended, so the fix gives both the pattern of the side the settings meet closer to.",
                NeckSeamFixKind.Material));

        var other = new Dictionary<uint, (float[], float[])>();
        foreach (var id in new[]
                 {
                     SkinMaterial.TileMipBiasOffset, SkinMaterial.NormalScale, SkinMaterial.SheenRate, SkinMaterial.SheenTintRate,
                     SkinMaterial.SheenAperture, SkinMaterial.TextureMipBias, SkinMaterial.SsaoMask, SkinMaterial.DiffuseColor,
                 })
        {
            var va = skinA.Constant(id);
            var vb = skinB.Constant(id);
            if (va.Length != vb.Length || va.Zip(vb).All(p => MathF.Abs(p.First - p.Second) <= 1e-3f))
                continue;
            other[id] = (va, vb);
        }
        if (other.Count > 0)
            findings.Add(new NeckSeamFinding("Other skin settings", $"{other.Count} differ", "", NeckSeamSeverity.Warning,
                "The fix brings them together: " + string.Join("; ", other.Select(o => $"{SkinMaterial.Names[o.Key]} {la} {Join(o.Value.Item1)}, {lb} {Join(o.Value.Item2)}")) + ".",
                NeckSeamFixKind.Material));
        return new NeckSeamMaterialMatch
        {
            FaceTileScale = scaleA, BodyTileScale = scaleB, FaceDensity = densityA, BodyDensity = densityB, TileScaleOff = scaleOff,
            FaceTileAlpha = alphaA, BodyTileAlpha = alphaB, FaceNormalAlpha = normalAlphaA, BodyNormalAlpha = normalAlphaB, TileAlphaOff = alphaOff,
            FaceTileIndex = indexA, BodyTileIndex = indexB, TileIndexOff = indexOff, Other = other,
        };
    }

    private static void TextureFindings(List<NeckSeamFinding> findings, List<BodySeamChain> chains, List<NeckSeamAnalyzer.TextureComparison> comparisons,
        IReadOnlyDictionary<uint, SeamImage> imagesA, IReadOnlyDictionary<uint, SeamImage> imagesB, HashSet<uint> blend, string la, string lb)
    {
        var weights = chains.Select(c => (float)c.Samples.Count).ToList();
        float Average(Func<NeckSeamAnalyzer.TextureComparison, float?> pick)
        {
            float sum = 0, total = 0;
            for (var i = 0; i < comparisons.Count; i++)
                if (pick(comparisons[i]) is { } value)
                {
                    sum += value * weights[i];
                    total += weights[i];
                }
            return total > 0 ? sum / total : 0;
        }

        if (comparisons.All(c => c.Diffuse is not null))
        {
            var colourA = new Vector3(Average(c => c.Diffuse!.Value.Face.X), Average(c => c.Diffuse!.Value.Face.Y), Average(c => c.Diffuse!.Value.Face.Z));
            var colourB = new Vector3(Average(c => c.Diffuse!.Value.Body.X), Average(c => c.Diffuse!.Value.Body.Y), Average(c => c.Diffuse!.Value.Body.Z));
            var max = comparisons.Max(c => c.Diffuse!.Value.MaxDifference);
            var off = max > 0.02f;
            if (off)
                blend.Add(SkinMaterial.DiffuseSampler);
            findings.Add(new NeckSeamFinding("Skin colour at the seam", Rgb(colourA), Rgb(colourB), max > 0.04f ? NeckSeamSeverity.Problem : off ? NeckSeamSeverity.Warning : NeckSeamSeverity.Ok,
                $"Diffuse colour on both sides of the seam, smoothed over 1 cm; the largest difference along it is {max * 255:0} of 255. " +
                "A tattoo or line that stops at the seam on purpose shows here too.",
                off ? NeckSeamFixKind.Textures : NeckSeamFixKind.None));
        }

        void Channel(string title, uint sampler, Func<NeckSeamAnalyzer.TextureComparison, NeckSeamAnalyzer.Pair?> pick, string detail)
        {
            if (!comparisons.All(c => pick(c) is not null))
                return;
            var valueA = Average(c => pick(c)!.Value.Face);
            var valueB = Average(c => pick(c)!.Value.Body);
            var off = MathF.Abs(valueA - valueB) > 0.03f;
            if (off)
                blend.Add(sampler);
            findings.Add(new NeckSeamFinding(title, valueA.ToString("0.000"), valueB.ToString("0.000"), off ? NeckSeamSeverity.Problem : NeckSeamSeverity.Ok,
                detail, off ? NeckSeamFixKind.Textures : NeckSeamFixKind.None));
        }
        Channel("Skin tone influence", SkinMaterial.NormalSampler, c => c.SkinInfluence,
            "How much the character's skin colour tints the diffuse (normal map blue). A difference makes the seam show with some skin colours only.");
        Channel("Specular strength", SkinMaterial.MaskSampler, c => c.Specular, "Mask red at the seam.");
        Channel("Roughness", SkinMaterial.MaskSampler, c => c.Roughness, "Mask green at the seam; a difference changes how sharp and bright highlights are on either side.");
        Channel("Subsurface scattering", SkinMaterial.MaskSampler, c => c.Subsurface, "Mask blue at the seam.");

        if (imagesA.TryGetValue(SkinMaterial.NormalSampler, out var normalA) && imagesB.TryGetValue(SkinMaterial.NormalSampler, out var normalB))
        {
            float mean = 0, max = 0;
            var count = 0;
            foreach (var chain in chains)
            {
                var (worldA, worldB) = SmoothedNormals(chain.Samples, normalA, normalB);
                for (var i = 0; i < worldA.Length; i++)
                {
                    var angle = NeckSeamAnalyzer.AngleDegrees(worldA[i], worldB[i]);
                    mean += angle;
                    max = Math.Max(max, angle);
                    count++;
                }
            }
            mean /= Math.Max(1, count);
            var off = max > NormalMapLimit;
            if (off)
                blend.Add(SkinMaterial.NormalSampler);
            findings.Add(new NeckSeamFinding("Surface normal at the seam", Deg(mean) + " avg", Deg(max) + " max", max > 2 * NormalMapLimit ? NeckSeamSeverity.Problem : off ? NeckSeamSeverity.Warning : NeckSeamSeverity.Ok,
                $"The angle between the {Of(la)} and the {Of(lb)} normal-mapped surface along the seam, as the shader builds it, smoothed over 1 cm so single pores don't count. " +
                $"Vanilla's own seams stay under {NormalMapLimit:0}°.",
                off ? NeckSeamFixKind.Textures : NeckSeamFixKind.None));
        }
    }

    /// <summary>
    /// Both sides' normal-mapped world normals along the seam, smoothed over <paramref name="metres"/>
    /// and normalized. Both frames are built on the same vertex normal (the two sides' average), so
    /// only the normal maps are compared: the vertex normals' own difference is a finding (and a fix)
    /// of its own and would otherwise count twice.
    /// </summary>
    internal static (Vector3[] A, Vector3[] B) SmoothedNormals(NeckSeamAnalyzer.Seam seam, SeamImage normalA, SeamImage normalB,
        float metres = NeckSeamAnalyzer.SmoothingMetres)
    {
        var a = new Vector4[seam.Count];
        var b = new Vector4[seam.Count];
        for (var i = 0; i < seam.Count; i++)
        {
            var shared = NeckSeamAnalyzer.SafeNormalize(seam.FaceNormal[i] + seam.BodyNormal[i], seam.FaceNormal[i]);
            a[i] = new Vector4(NeckSeamAnalyzer.ToWorld(normalA.Sample(seam.FaceUv[i]), NeckSeamAnalyzer.Frame(shared, seam.FaceBinormal[i], seam.FaceSign[i])), 0);
            b[i] = new Vector4(NeckSeamAnalyzer.ToWorld(normalB.Sample(seam.BodyUv[i]), NeckSeamAnalyzer.Frame(shared, seam.BodyBinormal[i], seam.BodySign[i])), 0);
        }
        static Vector3[] Unit(Vector4[] values) => values.Select(v => NeckSeamAnalyzer.SafeNormalize(new Vector3(v.X, v.Y, v.Z), Vector3.UnitY)).ToArray();
        return (Unit(NeckSeamAnalyzer.Smooth(seam, a, metres)), Unit(NeckSeamAnalyzer.Smooth(seam, b, metres)));
    }

    /// <summary> The normal map's alpha along the seam on one side, which scales the pore tile's strength on a body. </summary>
    private static float NormalAlpha(IReadOnlyDictionary<uint, SeamImage> images, List<BodySeamChain> chains, bool first)
    {
        if (!images.TryGetValue(SkinMaterial.NormalSampler, out var normal))
            return 1f;
        var values = chains.SelectMany(c => (first ? c.Samples.FaceUv : c.Samples.BodyUv).Select(uv => normal.Sample(uv).W)).ToList();
        return values.Count > 0 ? values.Average() : 1f;
    }

    /// <summary> A material's diffuse, normal and mask textures, decoded at up to 1024 texels (they are only sampled along the seam). </summary>
    private static Dictionary<uint, SeamImage> Images(NeckSeamMaterialInput material, SkinMaterial skin, Dictionary<(string, uint), SeamImage?> cache)
    {
        var images = new Dictionary<uint, SeamImage>();
        foreach (var sampler in new[] { SkinMaterial.DiffuseSampler, SkinMaterial.NormalSampler, SkinMaterial.MaskSampler })
        {
            if (skin.RequestedTextureFor(sampler) is not { } requested)
                continue;
            var flags = skin.FlagsFor(sampler);
            var key = (requested + "\n" + flags, sampler);
            if (!cache.TryGetValue(key, out var image))
            {
                image = null;
                var bytes = material.Textures.FirstOrDefault(t => string.Equals(PathRules.NormalizeGamePath(t.Key), requested, StringComparison.OrdinalIgnoreCase)).Value;
                if (bytes is not null)
                {
                    try { image = SeamTextures.DecodeAtMost(bytes, TextureEdge, flags); }
                    catch (Exception e) when (e is InvalidDataException or NotSupportedException) { image = null; }
                }
                cache[key] = image;
            }
            if (image is not null)
                images[sampler] = image;
        }
        return images;
    }

    /// <summary> UV units per metre over the part's triangles within a few centimetres of the seam. </summary>
    private static float UvDensity(BodySeamPart part, Vector3[] seam)
    {
        double world = 0, texture = 0;
        var triangles = part.Triangles;
        for (var t = 0; t + 2 < triangles.Length; t += 3)
        {
            int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
            var centroid = (part.Positions[a] + part.Positions[b] + part.Positions[c]) / 3;
            var near = false;
            foreach (var point in seam)
                if (Vector3.DistanceSquared(point, centroid) <= DensityBand * DensityBand)
                {
                    near = true;
                    break;
                }
            if (!near)
                continue;
            world += 0.5 * Vector3.Cross(part.Positions[b] - part.Positions[a], part.Positions[c] - part.Positions[a]).Length();
            var e1 = part.Uv[b] - part.Uv[a];
            var e2 = part.Uv[c] - part.Uv[a];
            texture += 0.5 * Math.Abs(e1.X * e2.Y - e1.Y * e2.X);
        }
        return world > 1e-9 ? (float)Math.Sqrt(texture / world) : 0f;
    }

    /// <summary> "at both wrists", "at the left ankle", "along the waist". </summary>
    private static string Where(BodySeamKind kind, IReadOnlyList<BodySeamChain> chains)
    {
        if (kind == BodySeamKind.Waist)
            return "along the waist";
        var sides = chains.Select(c => c.Side).Where(s => s.Length > 0).Distinct().ToList();
        var noun = kind == BodySeamKind.Wrists ? "wrist" : "ankle";
        return sides.Count switch
        {
            >= 2 => $"at both {noun}s",
            1 => $"at the {sides[0]} {noun}",
            _ => $"at the {noun}s",
        };
    }

    private static string Lower(BodySeamPart part) => part.Label.ToLowerInvariant();

    /// <summary> A part's lower-case name as a possessive: "top's", "gloves'". </summary>
    internal static string Of(string lower) => lower.EndsWith('s') ? lower + "'" : lower + "'s";
    private static string Of(BodySeamPart part) => Of(Lower(part));
    private static string FileName(string path) => path[(path.LastIndexOf('/') + 1)..];
    internal static string Mm(float metres) => $"{metres * 1000:0.00} mm";
    internal static string Deg(float degrees) => $"{degrees:0.0}°";
    private static string Percent(float value) => $"{value * 100:0}% differ";
    private static string Join(float[] values) => string.Join(", ", values.Select(v => v.ToString("0.###")));
    private static string Rgb(Vector3 colour) => $"{colour.X * 255:0} {colour.Y * 255:0} {colour.Z * 255:0}";
    private static string Ratio(float ratio) => ratio >= 1 ? $"{ratio:0.0}× as often" : $"{1 / ratio:0.0}× less often";
}
