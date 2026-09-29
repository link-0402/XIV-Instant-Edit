using System.Numerics;
using System.Text.RegularExpressions;
using InstantEdit.Services.Skeletons;

namespace InstantEdit.Services.NeckSeam;

/// <summary> A material and the textures its samplers read, keyed by the path the game requests. </summary>
internal sealed record NeckSeamMaterialInput(string GamePath, byte[] Bytes, IReadOnlyDictionary<string, byte[]> Textures);

/// <summary> A loaded model and the materials Penumbra resolved for it. </summary>
internal sealed record NeckSeamModelInput(string GamePath, byte[] Bytes, IReadOnlyList<NeckSeamMaterialInput> Materials)
{
    /// <summary> The character's enabled-attribute mask for the model, when the game was read; null counts every part as drawn. </summary>
    public uint? Attributes { get; init; }
    /// <summary> The enabled shape-key mask, such as Penumbra's shpx_ connector shapes; null applies none. </summary>
    public uint? Shapes { get; init; }

    public SkinModel Read() => SkinModel.Read(Bytes, Attributes, Shapes);
}

/// <summary>
/// Everything the analysis reads: the face (null when none is loaded), the models that carry body
/// skin, human.pbd, and the character's race (code like 801) when the game was read.
/// </summary>
internal sealed record NeckSeamInput(NeckSeamModelInput? Face, IReadOnlyList<NeckSeamModelInput> Bodies, byte[]? RacialDeformers)
{
    public int? CharacterRace { get; init; }
}

internal enum NeckSeamSeverity
{
    Ok,
    Info,
    Warning,
    Problem,
}

internal enum NeckSeamFixKind
{
    None,
    NeckMorph,
    Material,
    Textures,
    Weld,
    Normals,
}

/// <summary>
/// One measured seam property: the value on each side (the face and the body at the neck, the first
/// and second part at a body seam), how bad the difference is, and what fixes it.
/// </summary>
internal sealed record NeckSeamFinding(string Title, string Face, string Body, NeckSeamSeverity Severity, string Detail, NeckSeamFixKind Fix);

/// <summary> The measured seam: findings for the dialog and everything the fixer needs. </summary>
internal sealed class NeckSeamReport
{
    public required string FaceModelPath { get; init; }
    public required string FaceMaterialPath { get; init; }
    public required string BodyModelPath { get; init; }
    public required string BodyMaterialPath { get; init; }
    public required IReadOnlyList<NeckSeamFinding> Findings { get; init; }
    public required int RingVertices { get; init; }

    /// <summary> Neck morph entries to add to the face model; empty when it has them or they can't be built. </summary>
    public required IReadOnlyList<NeckMorph> NeckMorphs { get; init; }
    /// <summary> The skin settings that differ between the two materials; <see cref="NeckSeamMaterialMatch.Plan"/> picks the new values. </summary>
    public required NeckSeamMaterialMatch Material { get; init; }
    /// <summary> The face textures whose seam values differ from the body's (diffuse, normal and mask sampler ids). </summary>
    public required IReadOnlySet<uint> TexturesToBlend { get; init; }
    public bool TexturesDiffer => TexturesToBlend.Count > 0;
    public required bool HasNeckMorphs { get; init; }

    internal required NeckSeamAnalyzer.Seam Seam { get; init; }
    internal required NeckSeamAnalyzer.Side FaceSide { get; init; }
    internal required byte[] FaceModelBytes { get; init; }
    internal required byte[] FaceMaterialBytes { get; init; }
    internal required byte[] BodyMaterialBytes { get; init; }
    internal required IReadOnlyDictionary<uint, (string Path, byte[] Bytes)> FaceTextures { get; init; }
    internal required IReadOnlyDictionary<uint, SeamImage> FaceImages { get; init; }
    internal required IReadOnlyDictionary<uint, SeamImage> BodyImages { get; init; }

    public bool AnyFix => NeckMorphs.Count > 0 || Material.Any || TexturesDiffer;
    public NeckSeamSeverity Worst => Findings.Count == 0 ? NeckSeamSeverity.Ok : Findings.Max(f => f.Severity);
}

/// <summary>
/// Measures the seam between a face and the body at the neck, the way skin.shpk draws it. The face's
/// lowest open edge loop is the neck ring; the body mesh whose open loop lies on it (after the racial
/// deform) is its partner. Along the ring both sides are sampled: positions, vertex normals, weights,
/// vertex colours, every texture channel, and the per-pixel normal built with the shader's tangent
/// frame (T = sign · cross(B, N), n = x·T + y·B + z·N). The detail tile is compared in tiles per
/// metre: the face samples it with its second UV set, the body with its first.
/// </summary>
internal static class NeckSeamAnalyzer
{
    /// <summary> The connection vertex snap radius of the vertex shader (0.005 × model scale). </summary>
    public const float SnapRadius = 0.005f;
    public const int SamplesPerEdge = 64;
    public const float SmoothingMetres = 0.010f;
    private const float DensityBand = 0.03f;
    private const int BodyTextureEdge = 1024;
    private static readonly Regex FaceModel = new(@"^chara/human/c(?<race>\d{4})/obj/face/f\d{4}/model/c\d{4}f\d{4}_fac\.mdl$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ModelRace = new(@"(?:^|/)c(?<race>\d{4})[a-z]\d{4}_[a-z]{3}\.mdl$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsFaceModel(string gamePath) => FaceModel.IsMatch(PathRules.NormalizeGamePath(gamePath));

    /// <summary> Race code (such as 801) from a model's file name, or null. </summary>
    public static int? RaceOf(string gamePath)
    {
        var match = ModelRace.Match(PathRules.NormalizeGamePath(gamePath));
        return match.Success ? int.Parse(match.Groups["race"].Value) : null;
    }

    /// <summary> A mesh in a pose: bind-pose data moved through the racial deform, and its topology. </summary>
    internal sealed class Side
    {
        public required SkinMesh Mesh { get; init; }
        public required SkinMaterial Material { get; init; }
        public required Vector3[] Positions { get; init; }
        public required Vector3[] Normals { get; init; }
        public required Vector3[] Binormals { get; init; }
        public required SeamTopology Topology { get; init; }
        public required int[] Ring { get; init; }
    }

    /// <summary> Paired samples along the seam: the face side and the matching body point. </summary>
    internal sealed class Seam
    {
        public required Vector3[] Positions { get; init; }
        public required Vector2[] FaceUv { get; init; }
        public required Vector3[] FaceNormal { get; init; }
        public required Vector3[] FaceBinormal { get; init; }
        public required float[] FaceSign { get; init; }
        public required Vector2[] BodyUv { get; init; }
        public required Vector3[] BodyNormal { get; init; }
        public required Vector3[] BodyBinormal { get; init; }
        public required float[] BodySign { get; init; }
        public required float[] Gap { get; init; }
        public required float[] Arc { get; init; }
        /// <summary> Whether the samples run around a closed loop (the neck) or along an open edge. </summary>
        public bool Closed { get; init; } = true;
        public int Count => Positions.Length;
    }

    public static NeckSeamReport Analyze(NeckSeamInput input)
    {
        var faceInput = input.Face ?? throw new InvalidDataException("This character has no face model loaded.");
        var faceRace = RaceOf(faceInput.GamePath) ?? throw new InvalidDataException("The face model's race could not be read from its path.");
        var faceModel = faceInput.Read();
        var (faceMesh, faceMaterialInput, faceMaterial) = FindSkinMesh(faceInput, faceModel, m => m.IsFaceSkin)
            ?? throw new InvalidDataException("The face model has no mesh with a face skin (skin.shpk) material.");
        var faceTopology = new SeamTopology(faceMesh.Positions, faceMesh.Triangles);
        var faceRing = LowestLoop(faceMesh.Positions, faceTopology)
            ?? throw new InvalidDataException("The face skin mesh has no open edge at the neck.");
        var face = new Side
        {
            Mesh = faceMesh, Material = faceMaterial, Positions = faceMesh.Positions, Normals = faceMesh.Normals, Binormals = faceMesh.Binormals,
            Topology = faceTopology, Ring = faceRing,
        };
        var ringPoints = faceRing.Select(w => faceMesh.Positions[faceTopology.Representative[w]]).ToArray();

        // The body side: of every body skin mesh, the open loop that lies on the face's ring.
        (Side Side, NeckSeamModelInput Model, NeckSeamMaterialInput Material, float Distance)? best = null;
        foreach (var bodyInput in input.Bodies)
        {
            SkinModel model;
            try { model = bodyInput.Read(); }
            catch (Exception e) when (e is InvalidDataException or NotSupportedException) { continue; }
            var bodyRace = RaceOf(bodyInput.GamePath) ?? faceRace;
            var deformer = bodyRace != faceRace && input.RacialDeformers is { } pbd
                ? RacialDeformer.Create(pbd, faceRace, bodyRace)
                : RacialDeformer.None;
            foreach (var mesh in model.Meshes)
            {
                var material = MaterialFor(bodyInput, mesh.Material);
                if (material is null)
                    continue;
                SkinMaterial parsed;
                try { parsed = SkinMaterial.Read(material.Bytes); }
                catch (InvalidDataException) { continue; }
                if (!parsed.IsBodySkin)
                    continue;
                var (positions, normals, binormals) = deformer.Deform(mesh);
                var topology = new SeamTopology(positions, mesh.Triangles);
                foreach (var loop in topology.BoundaryLoops())
                {
                    var points = loop.Select(w => positions[topology.Representative[w]]).ToArray();
                    var distance = ringPoints.Average(p => points.Min(q => Vector3.Distance(p, q)));
                    if (best is null || distance < best.Value.Distance)
                        best = (new Side
                        {
                            Mesh = mesh, Material = parsed, Positions = positions, Normals = normals, Binormals = binormals, Topology = topology, Ring = loop,
                        }, bodyInput, material, distance);
                }
            }
        }
        if (best is not { } body || body.Distance > 0.02f)
            throw new InvalidDataException("No body skin mesh has a neck opening where the face ends. The neck may be covered by gear.");

        var seam = BuildSeam(face, body.Side);
        var findings = new List<NeckSeamFinding>();

        // ---- Geometry ----------------------------------------------------------------------------
        var (vertexGap, vertexAngle, weightDiff, colorDiff) = CompareRings(face, body.Side);
        findings.Add(new NeckSeamFinding("Neck ring fit", $"{face.Ring.Length} vertices", $"{body.Side.Ring.Length} vertices",
            vertexGap.Max > SnapRadius ? NeckSeamSeverity.Problem : vertexGap.Max > 0.0005f ? NeckSeamSeverity.Warning : NeckSeamSeverity.Ok,
            $"The body's neck edge lies {Mm(vertexGap.Mean)} from the face's on average ({Mm(vertexGap.Max)} at most)." +
            (vertexGap.Max > SnapRadius ? " That is beyond the game's 5 mm connection snap, so the edges cannot be joined; the body or face mod reshapes the neck." : ""),
            NeckSeamFixKind.None));

        var hasMorphs = faceModel.NeckMorphs.Count > 0;
        var morphs = hasMorphs ? [] : BuildNeckMorphs(face, faceModel);
        findings.Add(new NeckSeamFinding("Neck connection data", hasMorphs ? $"{faceModel.NeckMorphs.Count} entries" : "Missing", "—",
            hasMorphs ? NeckSeamSeverity.Ok : morphs.Count > 0 ? NeckSeamSeverity.Problem : NeckSeamSeverity.Warning,
            hasMorphs
                ? "The face model carries the connection vertices the game snaps the body's neck edge to."
                : morphs.Count > 0
                    ? "The face model has no neck morph table, so the game cannot snap the body's neck edge onto the face. The fix adds one built from the face's own neck edge."
                    : "The face model has no neck morph table, and one can't be built: its bone table lacks j_kubi or j_sebo_c.",
            morphs.Count > 0 ? NeckSeamFixKind.NeckMorph : NeckSeamFixKind.None));

        findings.Add(new NeckSeamFinding("Vertex normals at the edge", Deg(vertexAngle.Mean) + " avg", Deg(vertexAngle.Max) + " max",
            hasMorphs ? NeckSeamSeverity.Ok : vertexAngle.Max > 1.5f ? NeckSeamSeverity.Warning : NeckSeamSeverity.Info,
            hasMorphs
                ? "The connection snap gives both edges the face's normals in game."
                : "Without connection data each side keeps its own normals along the edge; adding the data makes them identical.",
            hasMorphs ? NeckSeamFixKind.None : NeckSeamFixKind.NeckMorph));

        findings.Add(new NeckSeamFinding("Skin weights at the edge", "", Percent(weightDiff),
            weightDiff > 0.05f ? NeckSeamSeverity.Warning : NeckSeamSeverity.Ok,
            weightDiff > 0.05f
                ? "The face and body weight the neck edge to different bones, so the edges can separate when the head moves. This needs a model edit in Blender."
                : "Both edges follow the same bones, so they stay together when the head moves.",
            NeckSeamFixKind.None));

        if (colorDiff.X > 0.05f || colorDiff.Z > 0.05f || colorDiff.W > 0.05f)
            findings.Add(new NeckSeamFinding("Vertex colour at the edge", "", $"R {colorDiff.X:0.00} · B {colorDiff.Z:0.00} · A {colorDiff.W:0.00}",
                NeckSeamSeverity.Info,
                "Vertex colour scales the skin shader at the edge: red how much muscle tone sharpens the normal map, blue the specular strength, alpha the detail tile. Differences need a model edit in Blender.",
                NeckSeamFixKind.None));

        // ---- Textures: the face's whole, the body's at a smaller mip (it is only sampled at the seam) ----
        var faceTextures = new Dictionary<uint, (string, byte[])>();
        var faceImages = new Dictionary<uint, SeamImage>();
        var bodyImages = new Dictionary<uint, SeamImage>();
        foreach (var sampler in new[] { SkinMaterial.DiffuseSampler, SkinMaterial.NormalSampler, SkinMaterial.MaskSampler })
        {
            if (TextureBytes(faceMaterialInput, faceMaterial, sampler) is { } faceTex)
            {
                faceTextures[sampler] = faceTex;
                faceImages[sampler] = SeamTextures.Decode(faceTex.Bytes, faceMaterial.FlagsFor(sampler));
            }
            // Body samplers wrap, and vanilla body UVs run from 1 to 2.
            if (TextureBytes(body.Material, body.Side.Material, sampler) is { } bodyTex)
                bodyImages[sampler] = SeamTextures.DecodeAtMost(bodyTex.Bytes, BodyTextureEdge, body.Side.Material.FlagsFor(sampler));
        }

        // ---- Material ----------------------------------------------------------------------------
        var faceDensity = UvDensity(face, face.Mesh.HasUv2 ? face.Mesh.Uv2 : face.Mesh.Uv1, up: true);
        var bodyDensity = UvDensity(body.Side, body.Side.Mesh.Uv1, up: false);
        var faceScale = faceMaterial.Constant(SkinMaterial.TileScale);
        var bodyScale = body.Side.Material.Constant(SkinMaterial.TileScale);
        var scaleOff = false;
        if (faceScale.Length == 2 && bodyScale.Length == 2 && faceDensity > 0 && bodyDensity > 0)
        {
            var faceTiles = faceScale.Average() * faceDensity;
            var bodyTiles = bodyScale.Average() * bodyDensity;
            var ratio = faceTiles / bodyTiles;
            // Vanilla faces and bodies differ by up to about 1.3x here, which doesn't show.
            scaleOff = ratio is < 0.7f or > 1.4f;
            findings.Add(new NeckSeamFinding("Skin detail tile size", $"{faceTiles:0} per m", $"{bodyTiles:0} per m",
                scaleOff ? NeckSeamSeverity.Problem : NeckSeamSeverity.Ok,
                $"The skin pore tile repeats {Ratio(ratio)} on the face at the neck (face g_TileScale {Join(faceScale)} over {faceDensity:0.00} UV/m, " +
                $"body {Join(bodyScale)} over {bodyDensity:0.00} UV/m)." + (scaleOff ? " The fix brings both to the same size; choose where they meet." : ""),
                scaleOff ? NeckSeamFixKind.Material : NeckSeamFixKind.None));
        }
        if (!face.Mesh.HasUv2)
            findings.Add(new NeckSeamFinding("Face second UV set", "Missing", "—", NeckSeamSeverity.Warning,
                "The face samples the skin detail tile with its second UV set, which this face model doesn't have.", NeckSeamFixKind.None));

        // Tile strength: TileAlpha × normal.b × vertex alpha on both sides, and × normal.a on the body.
        var bodyNormalA = bodyImages.TryGetValue(SkinMaterial.NormalSampler, out var bodyNormalMap)
            ? seam.BodyUv.Average(uv => bodyNormalMap.Sample(uv).W) : 1f;
        var faceAlpha = faceMaterial.Constant(SkinMaterial.TileAlpha)[0];
        var bodyMaterialAlpha = body.Side.Material.Constant(SkinMaterial.TileAlpha)[0];
        var bodyAlpha = bodyMaterialAlpha * bodyNormalA;
        var alphaOff = MathF.Abs(faceAlpha - bodyAlpha) > 0.05f;
        findings.Add(new NeckSeamFinding("Skin detail tile strength", $"{faceAlpha:0.00}", $"{bodyAlpha:0.00}",
            alphaOff ? NeckSeamSeverity.Warning : NeckSeamSeverity.Ok,
            "How strongly the pore tile shows (g_TileAlpha; on the body also times the normal map's alpha at the neck).",
            alphaOff ? NeckSeamFixKind.Material : NeckSeamFixKind.None));

        var faceIndex = faceMaterial.Constant(SkinMaterial.TileIndex)[0];
        var bodyIndex = body.Side.Material.Constant(SkinMaterial.TileIndex)[0];
        var indexOff = MathF.Abs(faceIndex - bodyIndex) > 0.01f;
        if (indexOff)
            findings.Add(new NeckSeamFinding("Skin detail tile pattern", $"{faceIndex:0}", $"{bodyIndex:0}", NeckSeamSeverity.Problem,
                "The face and body use different pore patterns (g_TileIndex). A pattern can't be blended, so the fix gives both the pattern of the side the settings meet closer to.",
                NeckSeamFixKind.Material));

        var other = new Dictionary<uint, (float[], float[])>();
        foreach (var id in new[]
                 {
                     SkinMaterial.TileMipBiasOffset, SkinMaterial.NormalScale, SkinMaterial.SheenRate, SkinMaterial.SheenTintRate,
                     SkinMaterial.SheenAperture, SkinMaterial.TextureMipBias, SkinMaterial.SsaoMask, SkinMaterial.DiffuseColor,
                 })
        {
            var f = faceMaterial.Constant(id);
            var b = body.Side.Material.Constant(id);
            if (f.Length != b.Length || f.Zip(b).All(p => MathF.Abs(p.First - p.Second) <= 1e-3f))
                continue;
            other[id] = (f, b);
        }
        if (other.Count > 0)
            findings.Add(new NeckSeamFinding("Other skin settings", $"{other.Count} differ", "", NeckSeamSeverity.Warning,
                "The fix brings them together: " + string.Join("; ", other.Select(o => $"{SkinMaterial.Names[o.Key]} face {Join(o.Value.Item1)}, body {Join(o.Value.Item2)}")) + ".",
                NeckSeamFixKind.Material));
        var match = new NeckSeamMaterialMatch
        {
            FaceTileScale = faceScale, BodyTileScale = bodyScale, FaceDensity = faceDensity, BodyDensity = bodyDensity, TileScaleOff = scaleOff,
            FaceTileAlpha = faceAlpha, BodyTileAlpha = bodyMaterialAlpha, BodyNormalAlpha = bodyNormalA, TileAlphaOff = alphaOff,
            FaceTileIndex = faceIndex, BodyTileIndex = bodyIndex, TileIndexOff = indexOff, Other = other,
        };

        // ---- Textures along the seam -------------------------------------------------------------
        var channels = CompareTextures(seam, faceImages, bodyImages, snapped: true);
        var blend = new HashSet<uint>();
        void Channel(string title, uint sampler, float faceValue, float bodyValue, float threshold, string detail, string format = "0.000")
        {
            var off = MathF.Abs(faceValue - bodyValue) > threshold;
            if (off)
                blend.Add(sampler);
            findings.Add(new NeckSeamFinding(title, faceValue.ToString(format), bodyValue.ToString(format),
                off ? NeckSeamSeverity.Problem : NeckSeamSeverity.Ok, detail, off ? NeckSeamFixKind.Textures : NeckSeamFixKind.None));
        }
        if (channels.Diffuse is { } diffuse)
        {
            var off = diffuse.MaxDifference > 0.02f;
            if (off)
                blend.Add(SkinMaterial.DiffuseSampler);
            findings.Add(new NeckSeamFinding("Skin colour at the seam", Rgb(diffuse.Face), Rgb(diffuse.Body),
                off ? NeckSeamSeverity.Problem : NeckSeamSeverity.Ok,
                $"Diffuse colour on both sides of the neck, smoothed over 1 cm; the largest difference along the seam is {diffuse.MaxDifference * 255:0} of 255.",
                off ? NeckSeamFixKind.Textures : NeckSeamFixKind.None));
        }
        if (channels.SkinInfluence is { } influence)
            Channel("Skin tone influence", SkinMaterial.NormalSampler, influence.Face, influence.Body, 0.03f,
                "How much the character's skin colour tints the diffuse (normal map blue). A difference makes the seam show with some skin colours only.");
        if (channels.Specular is { } specular)
            Channel("Specular strength", SkinMaterial.MaskSampler, specular.Face, specular.Body, 0.03f, "Mask red at the seam.");
        if (channels.Roughness is { } roughness)
            Channel("Roughness", SkinMaterial.MaskSampler, roughness.Face, roughness.Body, 0.03f, "Mask green at the seam; a difference changes how sharp and bright highlights are on either side.");
        if (channels.Subsurface is { } subsurface)
            Channel("Subsurface scattering", SkinMaterial.MaskSampler, subsurface.Face, subsurface.Body, 0.03f, "Mask blue at the seam.");
        if (channels.Normal is { } normal)
        {
            var off = normal.Max > 1.5f;
            if (off)
                blend.Add(SkinMaterial.NormalSampler);
            findings.Add(new NeckSeamFinding("Surface normal at the seam", Deg(normal.Mean) + " avg", Deg(normal.Max) + " max",
                off ? NeckSeamSeverity.Problem : NeckSeamSeverity.Ok,
                "The angle between the face's and the body's normal-mapped surface along the seam, as the shader builds it (with the connection snap applied). " +
                $"The normal maps tilt the surface {Deg(normal.FaceTilt)} (face) and {Deg(normal.BodyTilt)} (body) away from the mesh there.",
                off ? NeckSeamFixKind.Textures : NeckSeamFixKind.None));
        }
        if (channels.LipMask > 0.05f)
            findings.Add(new NeckSeamFinding("Lip mask at the neck", $"{channels.LipMask:0.00}", "—", NeckSeamSeverity.Warning,
                "The face's normal map alpha (the lip colour mask) isn't zero at the neck, so lip colour tints the seam.", NeckSeamFixKind.None));

        return new NeckSeamReport
        {
            FaceModelPath = PathRules.NormalizeGamePath(faceInput.GamePath),
            FaceMaterialPath = PathRules.NormalizeGamePath(faceMaterialInput.GamePath),
            BodyModelPath = PathRules.NormalizeGamePath(body.Model.GamePath),
            BodyMaterialPath = PathRules.NormalizeGamePath(body.Material.GamePath),
            Findings = findings,
            RingVertices = face.Ring.Length,
            NeckMorphs = morphs,
            Material = match,
            TexturesToBlend = blend,
            HasNeckMorphs = hasMorphs,
            Seam = seam,
            FaceSide = face,
            FaceModelBytes = faceInput.Bytes,
            FaceMaterialBytes = faceMaterialInput.Bytes,
            BodyMaterialBytes = body.Material.Bytes,
            FaceTextures = faceTextures,
            FaceImages = faceImages,
            BodyImages = bodyImages,
        };
    }

    // ---- Mesh and material lookup ----------------------------------------------------------------

    private static (SkinMesh, NeckSeamMaterialInput, SkinMaterial)? FindSkinMesh(NeckSeamModelInput model, SkinModel parsed, Func<SkinMaterial, bool> wanted)
    {
        foreach (var mesh in parsed.Meshes)
        {
            var material = MaterialFor(model, mesh.Material);
            if (material is null)
                continue;
            try
            {
                var skin = SkinMaterial.Read(material.Bytes);
                if (wanted(skin))
                    return (mesh, material, skin);
            }
            catch (InvalidDataException)
            {
            }
        }
        return null;
    }

    /// <summary> The loaded material for a mesh's material name: by file name, then with the racial remapping the material preview uses. </summary>
    internal static NeckSeamMaterialInput? MaterialFor(NeckSeamModelInput model, string meshMaterial)
    {
        var name = PathRules.NormalizeGamePath(meshMaterial).Split('/').Last();
        var byName = model.Materials.Where(m => string.Equals(PathRules.NormalizeGamePath(m.GamePath).Split('/').Last(), name,
            StringComparison.OrdinalIgnoreCase)).ToList();
        if (byName.Count == 1)
            return byName[0];
        var resources = model.Materials.GroupBy(m => PathRules.NormalizeGamePath(m.GamePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(m => m.GamePath).ToList(), StringComparer.OrdinalIgnoreCase);
        var resolved = MaterialPreviewBundleBuilder.ResolveMaterialPath(model.GamePath, meshMaterial, resources, new List<string>());
        return resolved is null ? null : model.Materials.FirstOrDefault(m => string.Equals(PathRules.NormalizeGamePath(m.GamePath), resolved,
            StringComparison.OrdinalIgnoreCase));
    }

    private static (string Path, byte[] Bytes)? TextureBytes(NeckSeamMaterialInput input, SkinMaterial material, uint sampler)
    {
        var requested = material.RequestedTextureFor(sampler);
        if (requested is null)
            return null;
        foreach (var (path, bytes) in input.Textures)
            if (string.Equals(PathRules.NormalizeGamePath(path), requested, StringComparison.OrdinalIgnoreCase))
                return (requested, bytes);
        return null;
    }

    /// <summary> The open loop reaching lowest: the neck edge of a face. </summary>
    private static int[]? LowestLoop(Vector3[] positions, SeamTopology topology)
    {
        int[]? best = null;
        var bestY = float.MaxValue;
        foreach (var loop in topology.BoundaryLoops())
        {
            if (loop.Length < 4)
                continue;
            var points = loop.Select(w => positions[topology.Representative[w]]).ToArray();
            var minY = points.Min(p => p.Y);
            var height = points.Max(p => p.Y) - minY;
            var width = Math.Max(points.Max(p => p.X) - points.Min(p => p.X), points.Max(p => p.Z) - points.Min(p => p.Z));
            if (width < 0.02f || height > width * 1.5f)
                continue;
            if (minY < bestY)
            {
                bestY = minY;
                best = loop;
            }
        }
        return best;
    }

    // ---- Seam sampling -----------------------------------------------------------------------------

    private static Seam BuildSeam(Side face, Side body)
    {
        var bodyEdges = new List<(Vector3 A, Vector3 B, int RawA, int RawB)>();
        for (var i = 0; i < body.Ring.Length; i++)
        {
            int a = body.Ring[i], b = body.Ring[(i + 1) % body.Ring.Length];
            if (body.Topology.EdgeTriangle(a, b) is { } edge)
                bodyEdges.Add((body.Positions[edge.A], body.Positions[edge.B], edge.A, edge.B));
        }
        if (bodyEdges.Count == 0)
            throw new InvalidDataException("The body's neck edge could not be traced.");

        var positions = new List<Vector3>();
        var faceUv = new List<Vector2>();
        var faceNormal = new List<Vector3>();
        var faceBinormal = new List<Vector3>();
        var faceSign = new List<float>();
        var bodyUv = new List<Vector2>();
        var bodyNormal = new List<Vector3>();
        var bodyBinormal = new List<Vector3>();
        var bodySign = new List<float>();
        var gap = new List<float>();
        for (var i = 0; i < face.Ring.Length; i++)
        {
            if (face.Topology.EdgeTriangle(face.Ring[i], face.Ring[(i + 1) % face.Ring.Length]) is not { } edge)
                continue;
            for (var s = 0; s < SamplesPerEdge; s++)
            {
                var t = s / (float)SamplesPerEdge;
                var p = Vector3.Lerp(face.Positions[edge.A], face.Positions[edge.B], t);
                positions.Add(p);
                faceUv.Add(Vector2.Lerp(face.Mesh.Uv1[edge.A], face.Mesh.Uv1[edge.B], t));
                faceNormal.Add(Vector3.Normalize(Vector3.Lerp(face.Normals[edge.A], face.Normals[edge.B], t)));
                faceBinormal.Add(Vector3.Lerp(face.Binormals[edge.A], face.Binormals[edge.B], t));
                faceSign.Add(face.Mesh.BinormalSigns[edge.A]);

                var bestDistance = float.MaxValue;
                (int A, int B, float T) bestEdge = default;
                foreach (var (a, b, rawA, rawB) in bodyEdges)
                {
                    var ab = b - a;
                    var u = Math.Clamp(Vector3.Dot(p - a, ab) / Math.Max(ab.LengthSquared(), 1e-12f), 0f, 1f);
                    var distance = Vector3.Distance(p, a + ab * u);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestEdge = (rawA, rawB, u);
                    }
                }
                bodyUv.Add(Vector2.Lerp(body.Mesh.Uv1[bestEdge.A], body.Mesh.Uv1[bestEdge.B], bestEdge.T));
                bodyNormal.Add(Vector3.Normalize(Vector3.Lerp(body.Normals[bestEdge.A], body.Normals[bestEdge.B], bestEdge.T)));
                bodyBinormal.Add(Vector3.Lerp(body.Binormals[bestEdge.A], body.Binormals[bestEdge.B], bestEdge.T));
                bodySign.Add(body.Mesh.BinormalSigns[bestEdge.A]);
                gap.Add(bestDistance);
            }
        }
        if (positions.Count == 0)
            throw new InvalidDataException("The face's neck edge could not be traced.");
        var arc = new float[positions.Count];
        for (var i = 1; i < arc.Length; i++)
            arc[i] = arc[i - 1] + Vector3.Distance(positions[i - 1], positions[i]);
        return new Seam
        {
            Positions = positions.ToArray(), FaceUv = faceUv.ToArray(), FaceNormal = faceNormal.ToArray(), FaceBinormal = faceBinormal.ToArray(),
            FaceSign = faceSign.ToArray(), BodyUv = bodyUv.ToArray(), BodyNormal = bodyNormal.ToArray(), BodyBinormal = bodyBinormal.ToArray(),
            BodySign = bodySign.ToArray(), Gap = gap.ToArray(), Arc = arc,
        };
    }

    private readonly record struct Stat(float Mean, float Max);

    private static (Stat Gap, Stat Angle, float Weights, Vector4 Colors) CompareRings(Side face, Side body)
    {
        var gaps = new List<float>();
        var angles = new List<float>();
        var weightDiff = 0f;
        Vector4 faceColor = Vector4.Zero, bodyColor = Vector4.Zero;
        foreach (var welded in face.Ring)
        {
            var fv = face.Topology.Representative[welded];
            var p = face.Positions[fv];
            var bv = body.Ring.Select(w => body.Topology.Representative[w]).MinBy(v => Vector3.Distance(p, body.Positions[v]));
            gaps.Add(Vector3.Distance(p, body.Positions[bv]));
            angles.Add(AngleDegrees(face.Normals[fv], body.Normals[bv]));
            var fw = Weights(face.Mesh, fv);
            var bw = Weights(body.Mesh, bv);
            weightDiff = Math.Max(weightDiff, fw.Keys.Union(bw.Keys).Sum(k => MathF.Abs(fw.GetValueOrDefault(k) - bw.GetValueOrDefault(k))) / 2);
            faceColor += face.Mesh.Colors[fv];
            bodyColor += body.Mesh.Colors[bv];
        }
        var colors = Vector4.Abs(faceColor - bodyColor) / face.Ring.Length;
        return (new Stat(gaps.Average(), gaps.Max()), new Stat(angles.Average(), angles.Max()), weightDiff, colors);
    }

    internal static Dictionary<string, float> Weights(SkinMesh mesh, int vertex)
    {
        var result = new Dictionary<string, float>(StringComparer.Ordinal);
        for (var i = 0; i < mesh.Influences; i++)
        {
            var weight = mesh.BlendWeights[vertex * mesh.Influences + i];
            var bone = mesh.BlendIndices[vertex * mesh.Influences + i];
            if (weight > 0 && bone < mesh.Bones.Length)
                result[mesh.Bones[bone]] = result.GetValueOrDefault(mesh.Bones[bone]) + weight;
        }
        return result;
    }

    /// <summary> Connection vertices from the face's own neck edge: one per welded ring vertex, with its normal. </summary>
    private static IReadOnlyList<NeckMorph> BuildNeckMorphs(Side face, SkinModel model)
    {
        if (model.BoneTables.Count == 0)
            return [];
        var table = model.BoneTables[0];
        int Slot(string bone)
        {
            var index = model.Bones.ToList().FindIndex(b => string.Equals(b, bone, StringComparison.Ordinal));
            return index < 0 ? -1 : Array.IndexOf(table, (ushort)index);
        }
        int kubi = Slot("j_kubi"), sebo = Slot("j_sebo_c");
        if (kubi is < 0 or > byte.MaxValue || sebo is < 0 or > byte.MaxValue || face.Ring.Length > byte.MaxValue)
            return [];
        var morphs = new List<NeckMorph>();
        foreach (var welded in face.Ring)
        {
            var normal = Vector3.Zero;
            for (var v = 0; v < face.Mesh.VertexCount; v++)
                if (face.Topology.Weld[v] == welded)
                    normal += face.Mesh.Normals[v];
            var representative = face.Topology.Representative[welded];
            morphs.Add(new NeckMorph(face.Mesh.Positions[representative],
                normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : face.Mesh.Normals[representative], (byte)kubi, (byte)sebo));
        }
        return morphs;
    }

    /// <summary> UV units per metre over the triangles next to the ring, on the side the mesh owns (above it for the face). </summary>
    private static float UvDensity(Side side, Vector2[] uv, bool up)
    {
        var ringY = side.Ring.Average(w => side.Positions[side.Topology.Representative[w]].Y);
        var centre = side.Ring.Aggregate(Vector3.Zero, (sum, w) => sum + side.Positions[side.Topology.Representative[w]]) / side.Ring.Length;
        double world = 0, texture = 0;
        var triangles = side.Mesh.Triangles;
        for (var t = 0; t + 2 < triangles.Length; t += 3)
        {
            int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
            var centroid = (side.Positions[a] + side.Positions[b] + side.Positions[c]) / 3;
            var dy = (centroid.Y - ringY) * (up ? 1 : -1);
            if (dy < -0.015f || dy > DensityBand || Vector2.Distance(new Vector2(centroid.X, centroid.Z), new Vector2(centre.X, centre.Z)) > 0.07f)
                continue;
            world += 0.5 * Vector3.Cross(side.Positions[b] - side.Positions[a], side.Positions[c] - side.Positions[a]).Length();
            var e1 = uv[b] - uv[a];
            var e2 = uv[c] - uv[a];
            texture += 0.5 * Math.Abs(e1.X * e2.Y - e1.Y * e2.X);
        }
        return world > 1e-9 ? (float)Math.Sqrt(texture / world) : 0f;
    }

    // ---- Texture comparison ------------------------------------------------------------------------

    internal readonly record struct Pair(float Face, float Body);
    internal readonly record struct ColourPair(Vector3 Face, Vector3 Body, float MaxDifference);
    internal readonly record struct NormalStats(float Mean, float Max, float FaceTilt, float BodyTilt);

    internal sealed record TextureComparison(ColourPair? Diffuse, Pair? SkinInfluence, Pair? Specular, Pair? Roughness, Pair? Subsurface,
        NormalStats? Normal, float LipMask);

    internal static TextureComparison CompareTextures(Seam seam, IReadOnlyDictionary<uint, SeamImage> face, IReadOnlyDictionary<uint, SeamImage> body, bool snapped)
    {
        Vector4[]? Samples(IReadOnlyDictionary<uint, SeamImage> images, uint sampler, Vector2[] uv)
            => images.TryGetValue(sampler, out var image) ? uv.Select(image.Sample).ToArray() : null;
        var faceDiffuse = Samples(face, SkinMaterial.DiffuseSampler, seam.FaceUv);
        var bodyDiffuse = Samples(body, SkinMaterial.DiffuseSampler, seam.BodyUv);
        var faceNormal = Samples(face, SkinMaterial.NormalSampler, seam.FaceUv);
        var bodyNormal = Samples(body, SkinMaterial.NormalSampler, seam.BodyUv);
        var faceMask = Samples(face, SkinMaterial.MaskSampler, seam.FaceUv);
        var bodyMask = Samples(body, SkinMaterial.MaskSampler, seam.BodyUv);

        ColourPair? diffuse = null;
        if (faceDiffuse is not null && bodyDiffuse is not null)
        {
            var f = Smooth(seam, faceDiffuse);
            var b = Smooth(seam, bodyDiffuse);
            var max = 0f;
            for (var i = 0; i < f.Length; i++)
                max = Math.Max(max, MaxComponent(Vector4.Abs(f[i] - b[i])));
            diffuse = new ColourPair(Mean3(faceDiffuse), Mean3(bodyDiffuse), max);
        }

        Pair? Channel(Vector4[]? f, Vector4[]? b, Func<Vector4, float> pick)
            => f is null || b is null ? null : new Pair(f.Average(pick), b.Average(pick));

        NormalStats? normal = null;
        if (faceNormal is not null && bodyNormal is not null)
        {
            var faceWorld = new Vector3[seam.Count];
            var bodyWorld = new Vector3[seam.Count];
            float sum = 0, max = 0, faceTilt = 0, bodyTilt = 0;
            for (var i = 0; i < seam.Count; i++)
            {
                var n = seam.FaceNormal[i];
                var fFrame = Frame(n, seam.FaceBinormal[i], seam.FaceSign[i]);
                var bFrame = Frame(snapped ? n : seam.BodyNormal[i], seam.BodyBinormal[i], seam.BodySign[i]);
                faceWorld[i] = ToWorld(faceNormal[i], fFrame);
                bodyWorld[i] = ToWorld(bodyNormal[i], bFrame);
                var angle = AngleDegrees(faceWorld[i], bodyWorld[i]);
                sum += angle;
                max = Math.Max(max, angle);
                faceTilt += AngleDegrees(faceWorld[i], fFrame.N);
                bodyTilt += AngleDegrees(bodyWorld[i], bFrame.N);
            }
            normal = new NormalStats(sum / seam.Count, max, faceTilt / seam.Count, bodyTilt / seam.Count);
        }

        return new TextureComparison(diffuse,
            Channel(faceNormal, bodyNormal, c => c.Z),
            Channel(faceMask, bodyMask, c => c.X),
            Channel(faceMask, bodyMask, c => c.Y),
            Channel(faceMask, bodyMask, c => c.Z),
            normal,
            faceNormal?.Average(c => c.W) ?? 0f);
    }

    // ---- Shader math ---------------------------------------------------------------------------------

    internal readonly record struct TangentFrame(Vector3 T, Vector3 B, Vector3 N);

    /// <summary> skin.shpk's frame: T = sign · cross(B, N), with B made orthogonal to N as the connection snap does. </summary>
    internal static TangentFrame Frame(Vector3 normal, Vector3 binormal, float sign)
    {
        var n = SafeNormalize(normal, Vector3.UnitY);
        var t = SafeNormalize(sign * Vector3.Cross(binormal, n), Vector3.UnitX);
        var b = SafeNormalize(sign * Vector3.Cross(n, t), Vector3.UnitZ);
        return new TangentFrame(t, b, n);
    }

    /// <summary> A normal map texel in world space: (r − ½, g − ½, √(¼ − x² − y²)) in the frame. </summary>
    internal static Vector3 ToWorld(Vector4 texel, TangentFrame frame)
    {
        var x = texel.X - 0.5f;
        var y = texel.Y - 0.5f;
        var z = MathF.Sqrt(MathF.Max(0.25f - x * x - y * y, 1e-6f));
        return SafeNormalize(frame.T * x + frame.B * y + frame.N * z, frame.N);
    }

    /// <summary> The normal map red and green that give <paramref name="world"/> in the frame. </summary>
    internal static Vector2 ToTexel(Vector3 world, TangentFrame frame)
    {
        var local = new Vector3(Vector3.Dot(world, frame.T), Vector3.Dot(world, frame.B), MathF.Max(Vector3.Dot(world, frame.N), 1e-3f));
        local = Vector3.Normalize(local) * 0.5f;
        return new Vector2(local.X + 0.5f, local.Y + 0.5f);
    }

    /// <summary> Gaussian smoothing along the seam by arc length: around a closed seam, or within an open one's ends. </summary>
    internal static Vector4[] Smooth(Seam seam, Vector4[] values, float metres = SmoothingMetres)
    {
        var count = values.Length;
        if (count < 3)
            return (Vector4[])values.Clone();
        var step = Math.Max(seam.Arc[^1] / Math.Max(1, count - 1), 1e-6f);
        var sigma = Math.Max(metres / step, 0.5f);
        var radius = Math.Min((int)MathF.Ceiling(sigma * 3), count / 2);
        var kernel = new float[radius * 2 + 1];
        for (var k = -radius; k <= radius; k++)
            kernel[k + radius] = MathF.Exp(-0.5f * k * k / (sigma * sigma));
        var result = new Vector4[count];
        for (var i = 0; i < count; i++)
        {
            var sum = Vector4.Zero;
            var total = 0f;
            for (var k = -radius; k <= radius; k++)
            {
                var j = i + k;
                if (seam.Closed)
                    j = (j % count + count) % count;
                else if (j < 0 || j >= count)
                    continue;
                sum += values[j] * kernel[k + radius];
                total += kernel[k + radius];
            }
            result[i] = sum / total;
        }
        return result;
    }

    internal static float AngleDegrees(Vector3 a, Vector3 b)
    {
        var dot = Vector3.Dot(SafeNormalize(a, Vector3.UnitY), SafeNormalize(b, Vector3.UnitY));
        return MathF.Acos(Math.Clamp(dot, -1f, 1f)) * 180f / MathF.PI;
    }

    internal static Vector3 SafeNormalize(Vector3 value, Vector3 fallback)
    {
        var length = value.Length();
        return float.IsFinite(length) && length > 1e-8f ? value / length : fallback;
    }

    private static float MaxComponent(Vector4 v) => MathF.Max(v.X, MathF.Max(v.Y, v.Z));
    private static Vector3 Mean3(Vector4[] values) => values.Aggregate(Vector3.Zero, (sum, v) => sum + new Vector3(v.X, v.Y, v.Z)) / values.Length;
    private static string Mm(float metres) => $"{metres * 1000:0.00} mm";
    private static string Deg(float degrees) => $"{degrees:0.0}°";
    private static string Percent(float value) => $"{value * 100:0}% differ";
    private static string Join(float[] values) => string.Join(", ", values.Select(v => v.ToString("0.###")));
    private static string Rgb(Vector3 colour) => $"{colour.X * 255:0} {colour.Y * 255:0} {colour.Z * 255:0}";
    private static string Ratio(float ratio) => ratio >= 1 ? $"{ratio:0.0}× as often" : $"{1 / ratio:0.0}× less often";
}
