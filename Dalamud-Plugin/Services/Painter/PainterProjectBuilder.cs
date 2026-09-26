using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InstantEdit.Services.Previews;

namespace InstantEdit.Services.Painter;

/// <summary> A model the actor renders, as On Screen lists it. </summary>
/// <param name="SourcePath">Absolute file for modded models; the game path for vanilla ones.</param>
public sealed record PainterModelRef(string GamePath, string SourcePath, bool Vanilla)
{
    public string FileName => Path.GetFileName(GamePath);
}

/// <summary> Everything needed to open a Painter project for one On Screen model. </summary>
public sealed record PainterRequest(int ObjectIndex, long ActorAddress, string ActorName, PainterModelRef Model,
    IReadOnlyList<PainterModelRef> OtherModels, IReadOnlyCollection<MaterialResourceCandidate> Resources);

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
    public string Label => string.Join(", ", MaterialPaths.Select(Path.GetFileName));
}

public sealed class PainterDraftModel
{
    public required PainterModelRef Model { get; init; }
    public required ModelMesh Mesh { get; init; }
    /// <summary> Texture set of each of the model's materials, by the material name the model stores. </summary>
    public required IReadOnlyDictionary<string, string> SetByMaterial { get; init; }
    public bool Selected { get; set; }
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
        var mesh = ModelMeshReader.Read(mainBytes);
        var plan = await _builder.BuildTexturePlanAsync(mainBytes, request.Model.GamePath, request.Resources, token).ConfigureAwait(false);
        warnings.AddRange(plan.Warnings);

        var sets = BuildSets(plan, out var setByMaterial);
        var main = new PainterDraftModel { Model = request.Model, Mesh = mesh, SetByMaterial = setByMaterial, Selected = true };
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
                siblings.Add(new PainterDraftModel { Model = other, Mesh = ModelMeshReader.Read(bytes), SetByMaterial = shared });
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

    private static List<PainterDraftSet> BuildSets(ModelTexturePlan plan, out Dictionary<string, string> setByMaterial)
    {
        setByMaterial = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sets = new List<(PainterDraftSet Set, string Signature)>();
        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var material in plan.Materials)
        {
            var textures = material.Textures.Where(t => t.Problem.Length == 0).DistinctBy(t => t.SourcePath, StringComparer.OrdinalIgnoreCase).ToList();
            // Materials drawing with identical textures share one texture set, so painting either paints both.
            var signature = material.ShaderPackage + "|" + string.Join("|", textures.Select(t => t.SourcePath.ToLowerInvariant()).Order());
            var existing = material.GamePath.Length > 0 && textures.Count > 0 ? sets.FirstOrDefault(s => s.Signature == signature).Set : null;
            if (existing is not null)
            {
                ((List<string>)existing.MaterialPaths).Add(material.GamePath);
                setByMaterial[material.ModelMaterial] = existing.Name;
                continue;
            }
            var name = PainterRules.UniqueName(Path.GetFileNameWithoutExtension(material.GamePath.Length > 0 ? material.GamePath : material.ModelMaterial), names, "material");
            var draftTextures = new List<PainterDraftTexture>();
            foreach (var texture in material.Textures)
            {
                var reason = texture.Problem.Length > 0 ? texture.Problem : PainterRules.EditReason(texture);
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
            var set = new PainterDraftSet
            {
                Name = name,
                ShaderPackage = material.ShaderPackage,
                MaterialPaths = new List<string> { material.GamePath.Length > 0 ? material.GamePath : material.ModelMaterial },
                Textures = draftTextures,
                Problem = material.Problem,
            };
            sets.Add((set, signature));
            setByMaterial[material.ModelMaterial] = name;
        }
        return sets.Select(s => s.Set).ToList();
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

        // Channel layout for each texture set.
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var layouts = new List<(PainterDraftSet Set, PainterSetLayout Layout, Dictionary<PainterTextureInput, PainterDraftTexture> Sources)>();
        var seedIndex = 0;
        foreach (var set in draft.Sets)
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

        // Seed images, one decode per texture.
        var seedFiles = new Dictionary<string, List<JsonObject>>(StringComparer.Ordinal);
        foreach (var (set, layout, sources) in layouts)
        {
            var list = new List<JsonObject>();
            seedFiles[set.Name] = list;
            foreach (var group in PainterChannelMap.Seeds(layout).GroupBy(seed => seed.Texture, ReferenceEqualityComparer.Instance))
            {
                token.ThrowIfCancellationRequested();
                var texture = sources[(PainterTextureInput)group.Key!];
                RgbaImage image;
                try { image = await _builder.DecodeTextureAsync(texture.Texture, token).ConfigureAwait(false); }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    warnings.Add($"{Path.GetFileName(texture.Texture.GamePath)} could not be decoded: {error.Message}");
                    continue;
                }
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
        }

        // Mesh: the model, plus the selected siblings' meshes that use one of its texture sets.
        var modelStem = PainterRules.UniqueName(Path.GetFileNameWithoutExtension(draft.Request.Model.GamePath), new HashSet<string>(), "model");
        var setNames = draft.Sets.Select(set => set.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var groups = new List<ObjGroup>();
        var coverage = new Dictionary<string, List<PainterCoverageModel>>(StringComparer.Ordinal);
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
                foreach (var submesh in part.Submeshes)
                    groups.Add(new ObjGroup(ObjWriter.GroupName(stem, part, submesh, model.Mesh.Attributes), part, submesh, setName));
                if (!meshesBySet.TryGetValue(setName, out var meshes))
                    meshesBySet[setName] = meshes = [];
                meshes.Add(part.MeshIndex);
            }
            foreach (var (setName, meshes) in meshesBySet)
            {
                if (!coverage.TryGetValue(setName, out var models))
                    coverage[setName] = models = [];
                models.Add(new PainterCoverageModel(model.Model.SourcePath, model.Model.Vanilla, meshes));
            }
        }
        var meshFile = Path.Combine(jobDir, "mesh", modelStem + ".obj");
        await using (var obj = new StreamWriter(meshFile, false, new UTF8Encoding(false)))
            ObjWriter.Write(obj, modelStem + ".mtl", groups);
        await using (var mtl = new StreamWriter(Path.Combine(jobDir, "mesh", modelStem + ".mtl"), false, new UTF8Encoding(false)))
            ObjWriter.WriteMaterialLibrary(mtl, groups.Select(g => g.Material));
        // Painter only creates texture sets for materials the mesh draws with.
        var drawn = groups.Select(g => g.Material).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in layouts.Where(entry => !drawn.Contains(entry.Set.Name) && entry.Layout.Textures.Any(t => t.Exported)))
            warnings.Add($"{entry.Set.Label} has no triangles at full detail, so its textures stay out of Painter.");
        layouts.RemoveAll(entry => !drawn.Contains(entry.Set.Name));

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
                var protect = unselectedShared.Contains(set.Name) || usersBySource.GetValueOrDefault(draftTexture.Texture.SourcePath) > 1;
                targets.Add(new PainterTarget
                {
                    Key = texture.Texture.Key,
                    TextureSet = set.Name,
                    GamePath = draftTexture.Texture.GamePath,
                    SessionId = sessions[draftTexture],
                    Width = PainterChannelMap.NextPowerOfTwo(texture.Texture.Width),
                    Height = PainterChannelMap.NextPowerOfTwo(texture.Texture.Height),
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

        var manifest = new JsonObject
        {
            ["schema"] = ManifestSchema,
            ["version"] = 1,
            ["jobId"] = jobId.ToString("N"),
            ["capability"] = capability,
            ["callbackPort"] = callbackPort,
            ["pluginVersion"] = pluginVersion,
            ["displayName"] = displayName,
            ["mesh"] = "mesh/" + modelStem + ".obj",
            ["textureSets"] = new JsonArray(layouts
                .Where(entry => seedFiles[entry.Set.Name].Count > 0 || entry.Layout.Textures.Any(t => t.Exported))
                .Select(entry => (JsonNode)new JsonObject
                {
                    ["name"] = entry.Set.Name,
                    ["label"] = entry.Set.Label,
                    ["width"] = entry.Layout.Width,
                    ["height"] = entry.Layout.Height,
                    ["removeChannels"] = new JsonArray(entry.Layout.RemoveChannels.Select(c => (JsonNode)JsonValue.Create(c)!).ToArray()),
                    ["channels"] = new JsonArray(entry.Layout.AddChannels.Select(c => (JsonNode)new JsonObject
                    {
                        ["type"] = c.Type, ["format"] = c.Format, ["label"] = c.Label,
                    }).ToArray()),
                    ["seeds"] = new JsonArray(seedFiles[entry.Set.Name].Select(s => (JsonNode)s).ToArray()),
                }).ToArray()),
            ["targets"] = manifestTargets,
            ["export"] = PainterChannelMap.ExportConfig(layouts.Select(entry => entry.Layout)),
        };
        var manifestPath = Path.Combine(jobDir, "job.json");
        if (targets.Count > 0)
            await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false), token).ConfigureAwait(false);
        return new PainterProjectFiles(targets, manifestPath, warnings);
    }
}
