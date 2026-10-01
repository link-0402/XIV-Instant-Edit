using System.Numerics;
using System.Text.RegularExpressions;
using InstantEdit.Services.Skeletons;

namespace InstantEdit.Services.NeckSeam;

/// <summary>
/// What a skin material's textures read at a set of points (where two parts meet, or every texel a
/// part covers): per-channel medians, so a few texels of lip colour or a nail don't move them. Null
/// channels have no texture to read.
/// </summary>
internal readonly record struct SkinToneSample(int Count, Vector3? Colour, float? Influence, float? NormalAlpha, Vector3? Mask, Vector4 VertexColour);

/// <summary>
/// Where one part's skin lies on or within <see cref="SkinToneAnalyzer.Reach"/> of another's: how many
/// of the target's tested vertices do, their share of those tested, and both sides read there.
/// </summary>
internal sealed record SkinToneContact(int Points, float Share, SkinToneSample Target, SkinToneSample Base);

/// <summary>
/// One skin material the character draws (skin.shpk, face or body type): the drawn triangles of every
/// loaded model that uses it, merged and moved into the character's race, and its textures decoded at
/// most <see cref="SkinToneAnalyzer.TextureEdge"/> texels across for measuring.
/// </summary>
internal sealed class SkinTonePart
{
    public required string MaterialPath { get; init; }
    public required NeckSeamMaterialInput Input { get; init; }
    public required SkinMaterial Skin { get; init; }
    /// <summary> The models that draw it, by game path. </summary>
    public required IReadOnlyList<string> Models { get; init; }
    /// <summary> What draws it: "Face", "Tail", "Top, Gloves" … </summary>
    public required string Parts { get; init; }
    /// <summary> The mod the material comes from, or "Game data". </summary>
    public required string Mod { get; init; }
    /// <summary> Whether the face model draws it (its extra skin, like a face mod's ears). </summary>
    public required bool OnFace { get; init; }
    public required Vector3[] Positions { get; init; }
    /// <summary> The first UV set, which the diffuse, normal and mask textures use. </summary>
    public required Vector2[] Uv { get; init; }
    /// <summary> The UV set the detail tile uses: the second on a face skin material, the first on a body one. </summary>
    public required Vector2[] TileUv { get; init; }
    public required Vector4[] Colors { get; init; }
    public required int[] Triangles { get; init; }
    public required float Area { get; init; }
    /// <summary> Detail tile UV units per metre over the whole part (<see cref="TileUv"/>). </summary>
    public required float TileDensity { get; init; }
    public required Vector3 Low { get; init; }
    public required Vector3 High { get; init; }
    internal required IReadOnlyDictionary<uint, SeamImage> Images { get; init; }
    /// <summary> What the textures read over every texel the part covers. </summary>
    internal required SkinToneSample Whole { get; init; }

    public string FileName => MaterialPath[(MaterialPath.LastIndexOf('/') + 1)..];
    /// <summary> The material's own part of its name: mt_c0801f0002_fac_e.mtrl → fac_e. </summary>
    public string Short => SkinToneAnalyzer.ShortName(MaterialPath);
    public string Label => $"{Parts} · {FileName}";
    /// <summary> A findings column heading: "Face (fac_e)". </summary>
    public string Column => $"{Parts} ({Short})";
    public int VertexCount => Positions.Length;

    private SeamSpace? _space;
    /// <summary> The drawn skin as a surface to search. One instance serves one thread; the analysis builds every contact on its own. </summary>
    public SeamSpace Space => _space ??= new SeamSpace(Positions, Triangles);

    public Vector2 UvAt(SurfacePoint point)
    {
        var t = 3 * point.Triangle;
        return Uv[Triangles[t]] * point.Weights.X + Uv[Triangles[t + 1]] * point.Weights.Y + Uv[Triangles[t + 2]] * point.Weights.Z;
    }
}

/// <summary> The drawn skin materials, which pairs of them touch, and the pair the dialog starts with. </summary>
internal sealed class SkinToneReport
{
    private readonly Dictionary<(int, int), SkinToneComparison> _compared = new();
    private readonly object _lock = new();

    public required IReadOnlyList<SkinTonePart> Parts { get; init; }
    /// <summary> Contacts by (target, base) index: the target part's vertices that lie within reach of the base part's skin. </summary>
    internal required IReadOnlyDictionary<(int Target, int Base), SkinToneContact> Contacts { get; init; }
    /// <summary> The extra skin that touches a main skin most (ears, a tail), and that main skin; null when none touches. </summary>
    public int? DefaultTarget { get; init; }
    public int? DefaultBase { get; init; }

    public int? IndexOf(string materialPath)
    {
        for (var i = 0; i < Parts.Count; i++)
            if (string.Equals(Parts[i].MaterialPath, PathRules.NormalizeGamePath(materialPath), StringComparison.OrdinalIgnoreCase))
                return i;
        return null;
    }

    /// <summary> The two parts compared where they meet, or over their whole surfaces when they don't touch. </summary>
    public SkinToneComparison Compare(int target, int @base)
    {
        lock (_lock)
        {
            if (_compared.TryGetValue((target, @base), out var cached))
                return cached;
            var comparison = SkinToneComparison.Build(Parts[target], Parts[@base], Contacts.GetValueOrDefault((target, @base)));
            _compared[(target, @base)] = comparison;
            return comparison;
        }
    }
}

/// <summary>
/// Measures the tone of every skin material the character draws, so one part can be matched to
/// another as a whole: a face mod's ears to its face, a tail to the body. skin.shpk draws each as
/// diffuse × g_DiffuseColor × lerp(1, skin colour, normal blue), with specular, roughness and
/// subsurface from the mask's red, green and blue. Where two parts touch, each side is read at the
/// points they share (the target's vertices within <see cref="Reach"/> of the base's skin); parts that
/// don't touch are compared over every texel they cover. Only drawn skin counts, and the human body
/// models (seam connectors, the low-poly body) never do.
/// </summary>
internal static class SkinToneAnalyzer
{
    /// <summary> How close a vertex must lie to the other part's skin to count as touching it: 2 mm. </summary>
    public const float Reach = 0.002f;
    /// <summary> Fewer touching points than this read too little of either side; the whole parts are compared instead. </summary>
    public const int MinContact = 12;
    /// <summary> Medians over whole regions need no more texels than this across, and a smaller mip decodes several times faster. </summary>
    public const int TextureEdge = 512;
    /// <summary> Target vertices tested against the base per pair at most, spread evenly over the part. </summary>
    private const int MaxContactQueries = 8000;
    /// <summary> Covered texels read per part at most, spread evenly, for the whole-part medians. </summary>
    private const int MaxWholeTexels = 250_000;
    private static readonly Regex ModelPrefix = new(@"^c\d{4}[a-z]\d{4}_", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static SkinToneReport Analyze(NeckSeamCaptured captured)
    {
        var input = captured.Input;
        var models = new List<(NeckSeamModelInput Model, bool Face)>();
        if (input.Face is { } face)
            models.Add((face, true));
        models.AddRange(input.Bodies.Where(b => !SeamSurroundings.IsHumanBodyModel(b.GamePath)).Select(b => (b, false)));

        var builders = new Dictionary<string, PartBuilder>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var (model, isFace) in models)
        {
            SkinModel parsed;
            try { parsed = model.Read(); }
            catch (Exception e) when (e is InvalidDataException or NotSupportedException) { continue; }
            var deformer = Deformer(model, input);
            foreach (var mesh in parsed.Meshes)
            {
                if (mesh.Triangles.Length < 3 || NeckSeamAnalyzer.MaterialFor(model, mesh.Material) is not { } material)
                    continue;
                SkinMaterial skin;
                try { skin = SkinMaterial.Read(material.Bytes); }
                catch (InvalidDataException) { continue; }
                if (!skin.IsSkin)
                    continue;
                var path = PathRules.NormalizeGamePath(material.GamePath);
                if (!builders.TryGetValue(path, out var builder))
                {
                    builders[path] = builder = new PartBuilder(path, material, skin);
                    order.Add(path);
                }
                builder.Add(model.GamePath, isFace, mesh, deformer);
            }
        }

        var parts = order.Select(path => builders[path].Build(captured.Source(path))).ToList();
        var contacts = new Dictionary<(int, int), SkinToneContact>();
        for (var t = 0; t < parts.Count; t++)
            for (var b = 0; b < parts.Count; b++)
                if (t != b && Contact(parts[t], parts[b]) is { } contact)
                    contacts[(t, b)] = contact;
        var (target, @base) = Defaults(parts, contacts);
        return new SkinToneReport { Parts = parts, Contacts = contacts, DefaultTarget = target, DefaultBase = @base };
    }

    private static RacialDeformer Deformer(NeckSeamModelInput model, NeckSeamInput input)
    {
        if (NeckSeamAnalyzer.RaceOf(model.GamePath) is not { } modelRace || input.CharacterRace is not { } race || modelRace == race ||
            input.RacialDeformers is not { } pbd)
            return RacialDeformer.None;
        try { return RacialDeformer.Create(pbd, race, modelRace); }
        catch (InvalidDataException) { return RacialDeformer.None; }
    }

    /// <summary> The material's own part of its name: mt_c0801f0002_fac_e.mtrl → fac_e, mt_c0201b0001_bibo.mtrl → bibo. </summary>
    public static string ShortName(string materialPath)
    {
        var name = Path.GetFileNameWithoutExtension(PathRules.NormalizeGamePath(materialPath).Split('/').Last());
        if (name.StartsWith("mt_", StringComparison.OrdinalIgnoreCase))
            name = name[3..];
        var trimmed = ModelPrefix.Replace(name, "");
        return trimmed.Length > 0 ? trimmed : name;
    }

    /// <summary> What a model is on the character: "Face", "Top", "Tail", "Ears" … </summary>
    public static string PartName(string modelPath)
    {
        var path = PathRules.NormalizeGamePath(modelPath).ToLowerInvariant();
        if (BodySeamAnalyzer.SlotOf(path) is { } slot)
            return BodySeamAnalyzer.PartLabel(slot);
        if (path.Contains("/obj/face/"))
            return "Face";
        if (path.Contains("/obj/tail/"))
            return "Tail";
        if (path.Contains("/obj/zear/"))
            return "Ears";
        if (path.Contains("/obj/hair/"))
            return "Hair";
        if (path.Contains("/obj/body/"))
            return "Body";
        return path.StartsWith("chara/equipment/", StringComparison.Ordinal) || path.StartsWith("chara/accessory/", StringComparison.Ordinal) ? "Gear" : "Model";
    }

    // ---- Parts --------------------------------------------------------------------------------------

    private sealed class PartBuilder(string path, NeckSeamMaterialInput input, SkinMaterial skin)
    {
        private readonly List<string> _models = [];
        private readonly List<Vector3> _positions = [];
        private readonly List<Vector2> _uv = [];
        private readonly List<Vector2> _tileUv = [];
        private readonly List<Vector4> _colors = [];
        private readonly List<int> _triangles = [];
        private bool _onFace;

        public void Add(string modelPath, bool isFace, SkinMesh mesh, RacialDeformer deformer)
        {
            var normalized = PathRules.NormalizeGamePath(modelPath);
            if (!_models.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                _models.Add(normalized);
            _onFace |= isFace;
            var matrices = deformer.BoneCount == 0 ? [] : mesh.Bones.Select(deformer.For).ToArray();
            var tileUv = skin.IsFaceSkin && mesh.HasUv2 ? mesh.Uv2 : mesh.Uv1;
            var merged = new Dictionary<int, int>();
            foreach (var vertex in mesh.Triangles)
            {
                if (!merged.TryGetValue(vertex, out var index))
                {
                    index = _positions.Count;
                    merged[vertex] = index;
                    var matrix = matrices.Length == 0
                        ? RacialDeformer.Affine.Identity
                        : RacialDeformer.Blend(matrices, mesh.BlendIndices.AsSpan(vertex * mesh.Influences, mesh.Influences),
                            mesh.BlendWeights.AsSpan(vertex * mesh.Influences, mesh.Influences));
                    _positions.Add(matrix.Point(mesh.Positions[vertex]));
                    _uv.Add(mesh.Uv1[vertex]);
                    _tileUv.Add(tileUv[vertex]);
                    _colors.Add(mesh.Colors[vertex]);
                }
                _triangles.Add(index);
            }
        }

        public SkinTonePart Build(NeckSeamSource? source)
        {
            var positions = _positions.ToArray();
            var triangles = _triangles.ToArray();
            var tileUv = _tileUv.ToArray();
            double area = 0, tileArea = 0;
            for (var t = 0; t + 2 < triangles.Length; t += 3)
            {
                int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
                area += 0.5 * Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]).Length();
                Vector2 e1 = tileUv[b] - tileUv[a], e2 = tileUv[c] - tileUv[a];
                tileArea += 0.5 * Math.Abs(e1.X * e2.Y - e1.Y * e2.X);
            }
            var images = new Dictionary<uint, SeamImage>();
            foreach (var sampler in new[] { SkinMaterial.DiffuseSampler, SkinMaterial.NormalSampler, SkinMaterial.MaskSampler })
            {
                if (skin.RequestedTextureFor(sampler) is not { } requested)
                    continue;
                var bytes = input.Textures.FirstOrDefault(t => string.Equals(PathRules.NormalizeGamePath(t.Key), requested, StringComparison.OrdinalIgnoreCase)).Value;
                if (bytes is null)
                    continue;
                try { images[sampler] = SeamTextures.DecodeAtMost(bytes, TextureEdge, skin.FlagsFor(sampler)); }
                catch (Exception e) when (e is InvalidDataException or NotSupportedException) { }
            }
            var uv = _uv.ToArray();
            var colors = _colors.ToArray();
            return new SkinTonePart
            {
                MaterialPath = path, Input = input, Skin = skin, Models = _models.ToArray(),
                Parts = string.Join(", ", _models.Select(PartName).Distinct(StringComparer.Ordinal)),
                Mod = source is null ? "Unknown" : source.IsModFile ? source.ModName : "Game data", OnFace = _onFace,
                Positions = positions, Uv = uv, TileUv = tileUv, Colors = colors, Triangles = triangles, Area = (float)area,
                TileDensity = area > 1e-9 ? (float)Math.Sqrt(tileArea / area) : 0f,
                Low = positions.Aggregate(new Vector3(float.MaxValue), Vector3.Min), High = positions.Aggregate(new Vector3(float.MinValue), Vector3.Max),
                Images = images, Whole = WholeSample(uv, triangles, colors, images),
            };
        }
    }

    /// <summary> Medians over every texel the part's triangles cover, each texture on its own. </summary>
    private static SkinToneSample WholeSample(Vector2[] uv, int[] triangles, Vector4[] colors, IReadOnlyDictionary<uint, SeamImage> images)
    {
        // Covering texels reads only the UVs and triangles of the surface.
        var surface = new SeamSurface([], [], [], [], uv, triangles);
        List<Vector4> Covered(uint sampler)
        {
            var values = new List<Vector4>();
            if (!images.TryGetValue(sampler, out var image))
                return values;
            var occupied = new bool[image.Width * image.Height];
            NeckSeamFixer.Cover(surface, image, occupied);
            var step = Math.Max(1, occupied.Count(o => o) / MaxWholeTexels);
            var seen = 0;
            for (var i = 0; i < occupied.Length; i++)
                if (occupied[i] && seen++ % step == 0)
                    values.Add(image.Texel(i % image.Width, i / image.Width));
            return values;
        }
        var diffuse = Covered(SkinMaterial.DiffuseSampler);
        var normal = Covered(SkinMaterial.NormalSampler);
        var mask = Covered(SkinMaterial.MaskSampler);
        var colours = colors.Length == 0 ? Vector4.One : colors.Aggregate(Vector4.Zero, (sum, c) => sum + c) / colors.Length;
        return Sample(Math.Max(diffuse.Count, Math.Max(normal.Count, mask.Count)), diffuse, normal, mask, colours);
    }

    private static SkinToneSample Sample(int count, List<Vector4> diffuse, List<Vector4> normal, List<Vector4> mask, Vector4 colours)
        => new(count,
            diffuse.Count > 0 ? new Vector3(Median(diffuse, v => v.X), Median(diffuse, v => v.Y), Median(diffuse, v => v.Z)) : null,
            normal.Count > 0 ? Median(normal, v => v.Z) : null,
            normal.Count > 0 ? Median(normal, v => v.W) : null,
            mask.Count > 0 ? new Vector3(Median(mask, v => v.X), Median(mask, v => v.Y), Median(mask, v => v.Z)) : null,
            colours);

    internal static float Median(IReadOnlyList<Vector4> values, Func<Vector4, float> pick)
    {
        var picked = values.Select(pick).ToArray();
        Array.Sort(picked);
        var middle = picked.Length / 2;
        return picked.Length % 2 == 1 ? picked[middle] : (picked[middle - 1] + picked[middle]) / 2;
    }

    // ---- Contacts -----------------------------------------------------------------------------------

    /// <summary>
    /// The target's drawn vertices on or within <see cref="Reach"/> of the base's skin, each read at its
    /// own UV and the base's texture at the nearest point of its skin; null when fewer than
    /// <see cref="MinContact"/> do.
    /// </summary>
    private static SkinToneContact? Contact(SkinTonePart target, SkinTonePart @base)
    {
        var reach = new Vector3(Reach);
        if (target.Positions.Length == 0 || @base.Positions.Length == 0 || !Overlaps(target.Low, target.High, @base.Low - reach, @base.High + reach))
            return null;
        var targetUv = new List<Vector2>();
        var baseUv = new List<Vector2>();
        var colours = Vector4.Zero;
        var step = Math.Max(1, target.Positions.Length / MaxContactQueries);
        var queried = 0;
        for (var v = 0; v < target.Positions.Length; v += step)
        {
            queried++;
            var p = target.Positions[v];
            if (p.X < @base.Low.X - Reach || p.Y < @base.Low.Y - Reach || p.Z < @base.Low.Z - Reach ||
                p.X > @base.High.X + Reach || p.Y > @base.High.Y + Reach || p.Z > @base.High.Z + Reach)
                continue;
            if (@base.Space.Nearest(p, Reach) is not { } point)
                continue;
            targetUv.Add(target.Uv[v]);
            baseUv.Add(@base.UvAt(point));
            colours += target.Colors[v];
        }
        if (targetUv.Count < MinContact)
            return null;
        List<Vector4> Read(SkinTonePart part, uint sampler, List<Vector2> uv)
            => part.Images.TryGetValue(sampler, out var image) ? uv.Select(image.Sample).ToList() : [];
        var targetSample = Sample(targetUv.Count, Read(target, SkinMaterial.DiffuseSampler, targetUv), Read(target, SkinMaterial.NormalSampler, targetUv),
            Read(target, SkinMaterial.MaskSampler, targetUv), colours / targetUv.Count);
        var baseSample = Sample(baseUv.Count, Read(@base, SkinMaterial.DiffuseSampler, baseUv), Read(@base, SkinMaterial.NormalSampler, baseUv),
            Read(@base, SkinMaterial.MaskSampler, baseUv), @base.Whole.VertexColour);
        return new SkinToneContact(targetUv.Count, targetUv.Count / (float)queried, targetSample, baseSample);
    }

    private static bool Overlaps(Vector3 lowA, Vector3 highA, Vector3 lowB, Vector3 highB)
        => lowA.X <= highB.X && lowB.X <= highA.X && lowA.Y <= highB.Y && lowB.Y <= highA.Y && lowA.Z <= highB.Z && lowB.Z <= highA.Z;

    /// <summary>
    /// The pair the dialog starts with: of the extra skins (anything but the face's own skin and the
    /// biggest body skin, and never another face skin material of the face model, like its teeth), the
    /// one whose vertices touch a main skin most, matched to that main skin.
    /// </summary>
    private static (int? Target, int? Base) Defaults(IReadOnlyList<SkinTonePart> parts, IReadOnlyDictionary<(int, int), SkinToneContact> contacts)
    {
        int? Largest(Func<SkinTonePart, bool> wanted)
        {
            int? best = null;
            for (var i = 0; i < parts.Count; i++)
                if (wanted(parts[i]) && (best is null || parts[i].Area > parts[best.Value].Area))
                    best = i;
            return best;
        }
        var face = Largest(p => p.OnFace && p.Skin.IsFaceSkin);
        var body = Largest(p => p.Skin.IsBodySkin && p.Models.Any(m => BodySeamAnalyzer.SlotOf(m) is not null));
        int[] mains = [.. new[] { face, body }.Where(i => i is not null).Select(i => i!.Value)];
        (int Target, int Base, float Share)? best = null;
        for (var t = 0; t < parts.Count; t++)
        {
            if (mains.Contains(t) || parts[t].OnFace && parts[t].Skin.IsFaceSkin)
                continue;
            foreach (var m in mains)
                if (contacts.TryGetValue((t, m), out var contact) && (best is null || contact.Share > best.Value.Share))
                    best = (t, m, contact.Share);
        }
        return best is { } pick ? (pick.Target, pick.Base) : (null, face ?? body);
    }
}
