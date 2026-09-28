using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InstantEdit.Services.Previews;
using InstantEdit.Services.Skeletons;

namespace InstantEdit.Services.Painter;

/// <summary> A model the actor renders, as On Screen lists it. </summary>
/// <param name="SourcePath">Absolute file for modded models; the game path for vanilla ones.</param>
public sealed record PainterModelRef(string GamePath, string SourcePath, bool Vanilla)
{
    public string FileName => Path.GetFileName(GamePath);
}

/// <summary> Everything needed to open a Painter project for one On Screen model. </summary>
public sealed record PainterRequest(int ObjectIndex, long ActorAddress, string ActorName, PainterModelRef Model,
    IReadOnlyList<PainterModelRef> OtherModels, IReadOnlyCollection<MaterialResourceCandidate> Resources)
{
    /// <summary> What the character draws right now; null when it couldn't be read, and then every part counts. </summary>
    public PainterLiveCharacter? Live { get; init; }

    /// <summary> With racial scaling on, what reshapes other races' models for the character; null when it is off or unavailable. </summary>
    internal RacialScalingSource? RacialScaling { get; init; }

    /// <summary> Why racial scaling is on but <see cref="RacialScaling"/> is missing; null otherwise. </summary>
    internal string? RacialScalingProblem { get; init; }
}

public sealed class PainterDraftTexture
{
    public required TexturePlanTexture Texture { get; init; }
    public bool Editable { get; init; }
    /// <summary> Why the texture stays in Painter only; empty when it can be sent back. </summary>
    public string Reason { get; init; } = "";
    /// <summary> Something to know about sending it back, such as a size change; empty otherwise. </summary>
    public string Note { get; init; } = "";
    public bool Selected { get; set; }
    public string Role => Texture.Usage switch
    {
        "diffuse" => "Base",
        "normal" => "Normal",
        "mask" => "Mask",
        "specular" => "Specular",
        "index" => "Index",
        _ => "Other",
    };
}

/// <summary> One Painter texture set: a material (or several materials using identical textures). </summary>
public sealed class PainterDraftSet
{
    public required string Name { get; init; }
    public required string ShaderPackage { get; init; }
    public required IReadOnlyList<string> MaterialPaths { get; init; }
    public required IReadOnlyList<PainterDraftTexture> Textures { get; init; }
    /// <summary> Why the material couldn't be read; empty when it was. </summary>
    public string Problem { get; init; } = "";
    /// <summary> Why the material's meshes stay out of Painter; empty when they go in. </summary>
    public string SkipReason { get; init; } = "";
    /// <inheritdoc cref="TexturePlanMaterial.Flags"/>
    public uint? Flags { get; init; }
    /// <inheritdoc cref="TexturePlanMaterial.AlphaThreshold"/>
    public float? AlphaThreshold { get; init; }
    public TexturePlanColorSet? ColorSet { get; init; }
    public string Label => string.Join(", ", MaterialPaths.Select(Path.GetFileName));
}

public sealed class PainterDraftModel
{
    public required PainterModelRef Model { get; init; }
    public required ModelMesh Mesh { get; init; }
    /// <summary> Texture set of each of the model's materials, by the material name the model stores. </summary>
    public required IReadOnlyDictionary<string, string> SetByMaterial { get; init; }
    /// <summary> The character's enabled attributes for this model; empty when unknown, and then every part counts. </summary>
    public IReadOnlyList<uint> AttributeMasks { get; init; } = [];
    public bool Selected { get; set; }

    /// <summary> Whether the character draws the submesh. </summary>
    public bool Draws(ModelSubmesh submesh) => PainterVisibility.Draws(AttributeMasks, submesh);
}

public sealed class PainterDraft
{
    public required PainterRequest Request { get; init; }
    public required PainterDraftModel Main { get; init; }
    public required IReadOnlyList<PainterDraftModel> Siblings { get; init; }
    public required IReadOnlyList<PainterDraftSet> Sets { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public string NewModName { get; set; } = "";

    public IEnumerable<PainterDraftTexture> SelectedTextures
        => Sets.SelectMany(set => set.Textures).Where(texture => texture.Selected && texture.Editable);

    public bool NeedsModName => SelectedTextures.Any(texture => texture.Texture.IsVanilla);
}

/// <summary> What a written job folder sends back, and where its job.json is. </summary>
internal sealed record PainterProjectFiles(List<PainterTarget> Targets, string ManifestPath, List<string> Warnings);

/// <summary>
/// Turns an On Screen model into a Painter job folder: the texture plan and sibling models for the
/// dialog, then the OBJ, seed images and job.json. It touches no texture sessions or Penumbra
/// state, so the same code runs offline against game data.
/// </summary>
internal sealed class PainterProjectBuilder
{
    public const string ManifestSchema = "instant-edit.painter-job";
    // Version 2 adds square sets (uvScale) and display settings; Painter plugins that only read
    // version 1 would place a square set's seeds wrongly, so they refuse it instead.
    public const int ManifestVersion = 2;
    private readonly MaterialPreviewBundleBuilder _builder;
    private readonly Func<string, CancellationToken, Task<byte[]?>> _readGameFile;
    private readonly Action<Exception, string> _log;

    public PainterProjectBuilder(MaterialPreviewBundleBuilder builder, Func<string, CancellationToken, Task<byte[]?>> readGameFile,
        Action<Exception, string> log)
    {
        _builder = builder;
        _readGameFile = readGameFile;
        _log = log;
    }

    public async Task<PainterDraft> PrepareAsync(PainterRequest request, CancellationToken token = default)
    {
        var warnings = new List<string>();
        var mainBytes = await ReadModelAsync(request.Model, token).ConfigureAwait(false);
        if (request.RacialScalingProblem is { } problem && ModelSkeletonPaths.Parse(request.Model.GamePath) is { Human: true })
            warnings.Add($"Racial scaling is on but can't apply: {problem.TrimEnd('.')}. The models keep their own race's shape.");
        var mesh = ModelMeshReader.Read(Scaled(request, request.Model, mainBytes, warnings));
        var plan = await _builder.BuildTexturePlanAsync(mainBytes, request.Model.GamePath, request.Resources, token).ConfigureAwait(false);
        warnings.AddRange(plan.Warnings);

        var masks = VisibleMasks(request.Live, request.Model, mesh);
        if (masks.Count == 0)
            warnings.Add("Instant Edit couldn't tell which parts of the model the character shows, so Painter gets all of them.");
        bool Draws(ModelSubmesh submesh) => submesh.Indices.Length >= 3 && PainterVisibility.Draws(masks, submesh);
        var hidden = mesh.Meshes.Sum(part => part.Submeshes.Count(s => s.Indices.Length >= 3 && !Draws(s)));
        if (hidden > 0)
            warnings.Add(hidden == 1
                ? "1 part of the model is turned off on this character (by its gear, customization or mod options) and stays out of Painter."
                : $"{hidden} parts of the model are turned off on this character (by its gear, customization or mod options) and stay out of Painter.");
        var drawnMaterials = mesh.Meshes.Where(part => part.Submeshes.Any(Draws))
            .Select(part => mesh.Materials[part.MaterialIndex])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var sets = BuildSets(plan, drawnMaterials, out var setByMaterial);
        var main = new PainterDraftModel { Model = request.Model, Mesh = mesh, SetByMaterial = setByMaterial, AttributeMasks = masks, Selected = true };
        var setByMaterialPath = sets.SelectMany(set => set.MaterialPaths.Select(path => (path, set.Name)))
            .GroupBy(pair => pair.path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Name, StringComparer.OrdinalIgnoreCase);

        var siblings = new List<PainterDraftModel>();
        foreach (var other in request.OtherModels.DistinctBy(m => m.SourcePath, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            if (string.Equals(other.SourcePath, request.Model.SourcePath, StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                var bytes = await ReadModelAsync(other, token).ConfigureAwait(false);
                var otherPlan = await _builder.BuildTexturePlanAsync(bytes, other.GamePath, request.Resources, token).ConfigureAwait(false);
                var shared = otherPlan.Materials
                    .Where(material => material.GamePath.Length > 0 && setByMaterialPath.ContainsKey(material.GamePath))
                    .ToDictionary(material => material.ModelMaterial, material => setByMaterialPath[material.GamePath], StringComparer.OrdinalIgnoreCase);
                if (shared.Count == 0)
                    continue;
                var otherMesh = ModelMeshReader.Read(Scaled(request, other, bytes, warnings));
                siblings.Add(new PainterDraftModel
                {
                    Model = other, Mesh = otherMesh, SetByMaterial = shared,
                    AttributeMasks = VisibleMasks(request.Live, other, otherMesh),
                });
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // A sibling that can't be read simply isn't offered.
                _log(error, $"Could not check {other.FileName} for shared materials.");
            }
        }

        var modName = "Painter " + Path.GetFileNameWithoutExtension(request.Model.GamePath);
        return new PainterDraft
        {
            Request = request, Main = main, Siblings = siblings, Sets = sets, Warnings = warnings,
            NewModName = PenumbraService.IsSafeNewModName(modName) ? modName : "Painter Edit",
        };
    }

    /// <summary>
    /// A model of another race reshaped for the character when racial scaling is on, as the game
    /// shows it on the character, so it lines up with the character's own face, hair and body in
    /// Painter; otherwise, or when it can't be scaled, as it is, with a note.
    /// </summary>
    private static byte[] Scaled(PainterRequest request, PainterModelRef model, byte[] bytes, List<string> notes)
    {
        if (request.RacialScaling is not { } source)
            return bytes;
        try
        {
            if (source.For(model.GamePath) is not { } scaling)
                return bytes;
            var scaled = RacialScalingModel.Apply(bytes, scaling.Deformer);
            notes.Add($"{model.FileName} is scaled from {scaling.Description}, as the character wears it.");
            return scaled;
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException)
        {
            notes.Add($"{model.FileName} keeps its own race's shape: {error.Message}");
            return bytes;
        }
    }

    /// <summary>
    /// The character's attribute masks for a model, or none (every part counts) when they're unknown
    /// or would hide the whole model, as right after a gear change, before the game sets them.
    /// </summary>
    private static IReadOnlyList<uint> VisibleMasks(PainterLiveCharacter? live, PainterModelRef model, ModelMesh mesh)
    {
        var masks = live?.MasksFor(model) ?? [];
        return masks.Count > 0 && mesh.Meshes.Any(part => part.Submeshes.Any(s => s.Indices.Length >= 3 && PainterVisibility.Draws(masks, s)))
            ? masks
            : [];
    }

    /// <param name="drawnMaterials">Model material names the character draws; textures of the others can't be painted.</param>
    private static List<PainterDraftSet> BuildSets(ModelTexturePlan plan, ISet<string> drawnMaterials, out Dictionary<string, string> setByMaterial)
    {
        setByMaterial = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Materials drawing with identical textures share one texture set, so painting either paints both.
        var groups = new List<(string Signature, List<TexturePlanMaterial> Materials)>();
        foreach (var material in plan.Materials)
        {
            var textures = material.Textures.Where(t => t.Problem.Length == 0).DistinctBy(t => t.SourcePath, StringComparer.OrdinalIgnoreCase).ToList();
            var signature = material.ShaderPackage + "|" + string.Join("|", textures.Select(t => t.SourcePath.ToLowerInvariant()).Order());
            var existing = material.GamePath.Length > 0 && textures.Count > 0 ? groups.FindIndex(g => g.Signature == signature) : -1;
            if (existing >= 0)
                groups[existing].Materials.Add(material);
            else
                groups.Add((signature, [material]));
        }

        var sets = new List<PainterDraftSet>();
        foreach (var (_, materials) in groups)
        {
            var material = materials[0];
            var skip = PainterRules.SkippedShaderReason(material.ShaderPackage);
            var drawn = materials.Any(m => drawnMaterials.Contains(m.ModelMaterial));
            var name = PainterRules.UniqueName(Path.GetFileNameWithoutExtension(material.GamePath.Length > 0 ? material.GamePath : material.ModelMaterial), names, "material");
            var draftTextures = new List<PainterDraftTexture>();
            foreach (var texture in material.Textures)
            {
                var reason = texture.Problem.Length > 0 ? texture.Problem
                    : skip.Length > 0 ? skip
                    : !drawn ? "Not drawn on this character."
                    : PainterRules.EditReason(texture);
                if (reason.Length == 0 && owners.TryGetValue(texture.SourcePath, out var owner))
                    reason = $"Also used by {owner}; it is sent back from there.";
                else if (reason.Length == 0)
                    owners[texture.SourcePath] = name;
                var width = PainterChannelMap.NextPowerOfTwo(texture.Width);
                var height = PainterChannelMap.NextPowerOfTwo(texture.Height);
                draftTextures.Add(new PainterDraftTexture
                {
                    Texture = texture,
                    Editable = reason.Length == 0,
                    Reason = reason,
                    Note = reason.Length == 0 && (width != texture.Width || height != texture.Height)
                        ? $"Painter works in powers of two, so it comes back as {width}×{height}."
                        : "",
                    Selected = reason.Length == 0 && texture.Usage != "index",
                });
            }
            sets.Add(new PainterDraftSet
            {
                Name = name,
                ShaderPackage = material.ShaderPackage,
                MaterialPaths = materials.Select(m => m.GamePath.Length > 0 ? m.GamePath : m.ModelMaterial).ToList(),
                Textures = draftTextures,
                Problem = material.Problem,
                SkipReason = skip,
                Flags = material.Flags,
                AlphaThreshold = material.AlphaThreshold,
                ColorSet = material.ColorSet,
            });
            foreach (var member in materials)
                setByMaterial[member.ModelMaterial] = name;
        }
        return sets;
    }

    public async Task<byte[]> ReadModelAsync(PainterModelRef model, CancellationToken token)
    {
        if (!model.Vanilla)
        {
            if (!Path.IsPathRooted(model.SourcePath))
                throw new IOException($"{model.FileName} has no file on disk.");
            return await File.ReadAllBytesAsync(model.SourcePath, token).ConfigureAwait(false);
        }
        return await _readGameFile(model.SourcePath, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException($"Game file not found: {model.SourcePath}");
    }

    /// <summary>
    /// Writes the job folder: channel layouts, seed images, the OBJ and job.json. Only textures with
    /// a session in <paramref name="sessions"/> are sent back; the rest are seeded for reference.
    /// </summary>
    public async Task<PainterProjectFiles> WriteJobAsync(PainterDraft draft, IReadOnlyDictionary<PainterDraftTexture, Guid> sessions,
        string jobDir, Guid jobId, string capability, int callbackPort, string pluginVersion, string displayName, CancellationToken token)
    {
        var warnings = new List<string>();
        TextureFiles.EnsureLocalPath(jobDir);
        Directory.CreateDirectory(Path.Combine(jobDir, "mesh"));
        Directory.CreateDirectory(Path.Combine(jobDir, "seeds"));

        // Channel layout for each texture set whose meshes go to Painter.
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var layouts = new List<(PainterDraftSet Set, PainterSetLayout Layout, Dictionary<PainterTextureInput, PainterDraftTexture> Sources)>();
        var seedIndex = 0;
        foreach (var set in draft.Sets.Where(set => set.SkipReason.Length == 0))
        {
            var sources = new Dictionary<PainterTextureInput, PainterDraftTexture>(ReferenceEqualityComparer.Instance);
            var inputs = new List<PainterTextureInput>();
            foreach (var texture in set.Textures.Where(t => t.Texture.Problem.Length == 0 && PainterRules.OfferedUsages.Contains(t.Texture.Usage) && t.Texture.UvSet == 0))
            {
                var key = sessions.ContainsKey(texture) ? PainterRules.UniqueName(Path.GetFileNameWithoutExtension(texture.Texture.GamePath), keys, "texture") : "";
                var input = new PainterTextureInput(key, texture.Texture.Usage, texture.Texture.Format, texture.Texture.Width,
                    texture.Texture.Height, $"t{seedIndex++:D3}");
                inputs.Add(input);
                sources[input] = texture;
            }
            var layout = PainterChannelMap.Layout(new PainterTextureSetInput(set.Name, set.ShaderPackage, inputs));
            foreach (var problem in layout.Textures.Where(t => t.Texture.Exported && t.Problem.Length > 0))
                warnings.Add($"{Path.GetFileName(sources[problem.Texture].Texture.GamePath)}: {problem.Problem}");
            layouts.Add((set, layout, sources));
        }

        // Mesh: what the character draws of the model, and of the selected siblings the meshes that
        // use one of its texture sets. Parts it has turned off stay out, as do skipped materials.
        var modelStem = PainterRules.UniqueName(Path.GetFileNameWithoutExtension(draft.Request.Model.GamePath), new HashSet<string>(), "model");
        var setNames = draft.Sets.Select(set => set.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var skipped = draft.Sets.Where(set => set.SkipReason.Length > 0).Select(set => set.Name).ToHashSet(StringComparer.Ordinal);
        var groups = new List<ObjGroup>();
        var coverage = new Dictionary<string, List<PainterCoverageModel>>(StringComparer.Ordinal);
        var partlyHidden = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in new[] { draft.Main }.Concat(draft.Siblings.Where(s => s.Selected)))
        {
            var stem = Path.GetFileNameWithoutExtension(model.Model.GamePath);
            var meshesBySet = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            foreach (var part in model.Mesh.Meshes)
            {
                var materialName = model.Mesh.Materials[part.MaterialIndex];
                var setName = model.SetByMaterial.TryGetValue(materialName, out var mapped) ? mapped : null;
                if (setName is null)
                {
                    if (model != draft.Main)
                        continue;
                    // A material the plan couldn't resolve still gets a texture set, so the mesh stays visible.
                    setName = PainterRules.UniqueName(Path.GetFileNameWithoutExtension(materialName), setNames, "unmapped");
                    ((Dictionary<string, string>)model.SetByMaterial)[materialName] = setName;
                }
                if (skipped.Contains(setName))
                    continue;
                var drawn = false;
                foreach (var submesh in part.Submeshes)
                {
                    if (!model.Draws(submesh))
                    {
                        // Painter can't paint its texels, so they keep what the texture has.
                        partlyHidden.Add(setName);
                        continue;
                    }
                    groups.Add(new ObjGroup(ObjWriter.GroupName(stem, part, submesh, model.Mesh.Attributes), part, submesh, setName));
                    drawn = true;
                }
                if (!drawn)
                    continue;
                if (!meshesBySet.TryGetValue(setName, out var meshes))
                    meshesBySet[setName] = meshes = [];
                meshes.Add(part.MeshIndex);
            }
            foreach (var (setName, meshes) in meshesBySet)
            {
                if (!coverage.TryGetValue(setName, out var models))
                    coverage[setName] = models = [];
                models.Add(new PainterCoverageModel(model.Model.SourcePath, model.Model.Vanilla, meshes, model.AttributeMasks));
            }
        }
        var twoSided = new HashSet<string>(StringComparer.Ordinal);
        var meshGroups = ObjWriter.WithoutBackFaceCopies(groups, twoSided);
        // Painter only creates texture sets for materials the mesh draws with.
        var drawnSets = meshGroups.Select(g => g.Material).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in layouts.Where(entry => !drawnSets.Contains(entry.Set.Name) && entry.Layout.Textures.Any(t => t.Exported)))
            warnings.Add($"{entry.Set.Label} isn't drawn on this character at full detail, so its textures stay out of Painter.");
        layouts.RemoveAll(entry => !drawnSets.Contains(entry.Set.Name));
        var uvScales = layouts.ToDictionary(entry => entry.Set.Name, entry => (entry.Layout.ScaleU, entry.Layout.ScaleV), StringComparer.Ordinal);
        var meshFile = Path.Combine(jobDir, "mesh", modelStem + ".obj");
        await using (var obj = new StreamWriter(meshFile, false, new UTF8Encoding(false)))
            ObjWriter.Write(obj, modelStem + ".mtl", meshGroups, uvScales);
        await using (var mtl = new StreamWriter(Path.Combine(jobDir, "mesh", modelStem + ".mtl"), false, new UTF8Encoding(false)))
            ObjWriter.WriteMaterialLibrary(mtl, meshGroups.Select(g => g.Material));

        // Seed images, one decode per texture, and a base color preview for sets whose color the
        // game doesn't take from a texture.
        var seedFiles = new Dictionary<string, List<JsonObject>>(StringComparer.Ordinal);
        var previewed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (set, layout, sources) in layouts)
        {
            var list = new List<JsonObject>();
            seedFiles[set.Name] = list;
            var decoded = new Dictionary<PainterTextureInput, RgbaImage?>(ReferenceEqualityComparer.Instance);
            async Task<RgbaImage?> Decode(PainterTextureInput input)
            {
                if (decoded.TryGetValue(input, out var cached))
                    return cached;
                var texture = sources[input];
                try { cached = await _builder.DecodeTextureAsync(texture.Texture, token).ConfigureAwait(false); }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    warnings.Add($"{Path.GetFileName(texture.Texture.GamePath)} could not be decoded: {error.Message}");
                    cached = null;
                }
                decoded[input] = cached;
                return cached;
            }

            foreach (var group in PainterChannelMap.Seeds(layout).GroupBy(seed => seed.Texture, ReferenceEqualityComparer.Instance))
            {
                token.ThrowIfCancellationRequested();
                if (await Decode((PainterTextureInput)group.Key!).ConfigureAwait(false) is not { } image)
                    continue;
                foreach (var seed in group)
                {
                    var bytes = seed.Kind switch
                    {
                        PainterSeedKind.Color => TgaImage.WriteRgb24(image),
                        PainterSeedKind.Normal => TgaImage.WriteRgb24(PainterChannelMap.NormalSeed(image)),
                        _ => TgaImage.WriteChannel(image, seed.Component),
                    };
                    await File.WriteAllBytesAsync(Path.Combine(jobDir, "seeds", seed.FileName), bytes, token).ConfigureAwait(false);
                    list.Add(new JsonObject { ["channel"] = seed.Channel, ["file"] = "seeds/" + seed.FileName, ["colorSpace"] = seed.ColorSpace });
                }
            }

            if (await PreviewAsync(set, layout, Decode, draft.Request.Live?.Colors).ConfigureAwait(false) is { } preview)
            {
                var file = $"p{previewed.Count:D3}.rgb.tga";
                await File.WriteAllBytesAsync(Path.Combine(jobDir, "seeds", file), TgaImage.WriteRgb24(preview), token).ConfigureAwait(false);
                list.Add(new JsonObject { ["channel"] = "BaseColor", ["file"] = "seeds/" + file, ["colorSpace"] = "color" });
                previewed.Add(set.Name);
            }
        }

        // Targets, with the texture sets whose meshes decide which texels Painter may change.
        var unselectedShared = draft.Siblings.Where(s => !s.Selected).SelectMany(s => s.SetByMaterial.Values).ToHashSet(StringComparer.Ordinal);
        var usersBySource = draft.Sets.SelectMany(set => set.Textures.Select(t => (t.Texture.SourcePath, set.Name)))
            .GroupBy(pair => pair.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(pair => pair.Name).Distinct().Count(), StringComparer.OrdinalIgnoreCase);
        var targets = new List<PainterTarget>();
        var manifestTargets = new JsonArray();
        foreach (var (set, layout, sources) in layouts)
        {
            foreach (var texture in layout.Textures.Where(t => t.Exported))
            {
                var draftTexture = sources[texture.Texture];
                var protect = unselectedShared.Contains(set.Name) || partlyHidden.Contains(set.Name)
                    || usersBySource.GetValueOrDefault(draftTexture.Texture.SourcePath) > 1;
                var width = PainterChannelMap.NextPowerOfTwo(texture.Texture.Width);
                var height = PainterChannelMap.NextPowerOfTwo(texture.Texture.Height);
                targets.Add(new PainterTarget
                {
                    Key = texture.Texture.Key,
                    TextureSet = set.Name,
                    GamePath = draftTexture.Texture.GamePath,
                    SessionId = sessions[draftTexture],
                    Width = width,
                    Height = height,
                    ExportWidth = width * layout.ScaleU,
                    ExportHeight = height * layout.ScaleV,
                    Protect = protect,
                    Coverage = protect ? coverage.GetValueOrDefault(set.Name) ?? [] : [],
                });
                manifestTargets.Add(new JsonObject
                {
                    ["key"] = texture.Texture.Key,
                    ["textureSet"] = set.Name,
                    ["label"] = $"{draftTexture.Role}: {Path.GetFileName(draftTexture.Texture.GamePath)}",
                });
            }
        }

        var textureSets = new JsonArray();
        foreach (var (set, layout, _) in layouts)
        {
            var display = PainterDisplay.For(set.Flags, set.AlphaThreshold, layout.Uses("Opacity"), twoSided.Contains(set.Name));
            if (seedFiles[set.Name].Count == 0 && !layout.Textures.Any(t => t.Exported) && display == PainterDisplay.Default)
                continue;
            var channels = layout.AddChannels.ToList();
            if (previewed.Contains(set.Name) && channels.All(c => c.Type != "BaseColor"))
                channels.Insert(0, new PainterChannelSpec("BaseColor", "sRGB8", ""));
            var entry = new JsonObject
            {
                ["name"] = set.Name,
                ["label"] = set.Label,
                ["width"] = layout.Size,
                ["height"] = layout.Size,
                ["removeChannels"] = new JsonArray(layout.RemoveChannels.Select(c => (JsonNode)JsonValue.Create(c)!).ToArray()),
                ["channels"] = new JsonArray(channels.Select(c => (JsonNode)new JsonObject
                {
                    ["type"] = c.Type, ["format"] = c.Format, ["label"] = c.Label,
                }).ToArray()),
                ["seeds"] = new JsonArray(seedFiles[set.Name].Select(s => (JsonNode)s).ToArray()),
            };
            if (layout.ScaleU != 1 || layout.ScaleV != 1)
                entry["uvScale"] = new JsonArray(layout.ScaleU, layout.ScaleV);
            if (display != PainterDisplay.Default)
                entry["display"] = new JsonObject
                {
                    ["alpha"] = display.Alpha,
                    ["threshold"] = Math.Round(display.Threshold, 4),
                    ["doubleSided"] = display.DoubleSided,
                };
            textureSets.Add(entry);
        }

        var manifest = new JsonObject
        {
            ["schema"] = ManifestSchema,
            ["version"] = ManifestVersion,
            ["jobId"] = jobId.ToString("N"),
            ["capability"] = capability,
            ["callbackPort"] = callbackPort,
            ["pluginVersion"] = pluginVersion,
            ["displayName"] = displayName,
            ["mesh"] = "mesh/" + modelStem + ".obj",
            ["textureSets"] = textureSets,
            ["targets"] = manifestTargets,
            ["export"] = PainterChannelMap.ExportConfig(layouts.Select(entry => entry.Layout)),
        };
        var manifestPath = Path.Combine(jobDir, "job.json");
        if (targets.Count > 0)
            await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false), token).ConfigureAwait(false);
        return new PainterProjectFiles(targets, manifestPath, warnings);
    }

    /// <summary>
    /// A base color image for a set whose color doesn't come from a texture, or null: hair.shpk
    /// takes the character's hair colors, and the character shaders their colorset through the
    /// index texture. Painter only shows it; nothing sends it back.
    /// </summary>
    private static async Task<RgbaImage?> PreviewAsync(PainterDraftSet set, PainterSetLayout layout,
        Func<PainterTextureInput, Task<RgbaImage?>> decode, PainterCharacterColors? colors)
    {
        if (layout.Uses("BaseColor"))
            return null;
        if (set.ShaderPackage.Equals("hair.shpk", StringComparison.OrdinalIgnoreCase))
        {
            var normal = layout.Textures.Select(t => t.Texture).FirstOrDefault(t => t.Usage == "normal");
            return normal is not null && await decode(normal).ConfigureAwait(false) is { } image
                ? PainterPreviews.Hair(image, colors ?? PainterPreviews.DefaultColors)
                : null;
        }
        if (set.ColorSet is { Rows: > 0 } colorSet && PainterChannelMap.IsCharacterShader(set.ShaderPackage))
        {
            var index = layout.Textures.Select(t => t.Texture).FirstOrDefault(t => t.Usage == "index");
            return index is not null && await decode(index).ConfigureAwait(false) is { } image
                ? PainterPreviews.ColorSet(image, colorSet)
                : null;
        }
        return null;
    }
}
