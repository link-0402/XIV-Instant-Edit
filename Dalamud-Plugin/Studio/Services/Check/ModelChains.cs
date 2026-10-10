using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Services.Project;
using InstantEdit.Studio.Services.Resources;

namespace InstantEdit.Studio.Services.Check;

internal sealed partial class ModCheck
{
    private readonly List<ModelWalk> _walks = new();
    private readonly Dictionary<(OptionRef Container, string Path), MaterialCheck> _materials = new();
    private readonly List<Request> _requests = new();
    private readonly HashSet<string> _requested = new(StringComparer.Ordinal);

    private sealed record VariantData(int Variant, ImcEntry Entry, IReadOnlyList<string> Items);

    private sealed class MaterialUse
    {
        public required string Name { get; init; }
        public required int MaterialSet { get; init; }
        public required MaterialRequest Request { get; init; }
        public Request? Resolved { get; init; }
    }

    private sealed record SkinUse(string Name, ushort Body, string Suffix);

    /// <summary>One model the mod redirects (or swaps in), with the variants that load it and the materials it asks for.</summary>
    private sealed class ModelWalk
    {
        public required CheckContainer Container { get; init; }
        public required string Path { get; init; }
        public required PathResolution Source { get; init; }
        public ModelPathInfo? Info { get; init; }
        public ModelLayout? Layout { get; set; }
        public string? Error { get; set; }
        public List<VariantData> Rows { get; } = new();
        public string? RowNote { get; set; }
        public List<MaterialUse> Materials { get; } = new();
        public List<SkinUse> Skins { get; } = new();
        public bool Approximate { get; set; }
        public IEnumerable<int> Folders => Rows.Select(row => (int)row.Entry.MaterialId).Distinct().Order();
    }

    private enum RequestKind
    {
        Material,
        Texture,
        Shader,
    }

    /// <summary>A game path one of the mod's chains asks for, from one container.</summary>
    private sealed class Request
    {
        public required RequestKind Kind { get; init; }
        public required CheckContainer Root { get; init; }
        public required string Path { get; init; }
        public required PathResolution Resolution { get; set; }
        public required string Requester { get; init; }

        /// <summary>Where a repair adds the missing redirection: the container that provides the material or model asking for it.</summary>
        public required CheckContainer Target { get; init; }
        public string? SlotLabel { get; init; }
    }

    private void WalkModels()
    {
        foreach (var mapping in _model.Mappings.Where(m => !m.Shadowed && m.Key.EndsWith(".mdl", StringComparison.Ordinal)).ToArray())
        {
            _token.ThrowIfCancellationRequested();
            WalkModel(mapping.Container, mapping.Key, new PathResolution(ResolutionSource.ModFile, mapping.Key, mapping.RelPath, mapping.Container.Option));
        }
        foreach (var container in _model.Containers)
        foreach (var (key, target) in _model.Swaps[container])
        {
            if (!key.EndsWith(".mdl", StringComparison.Ordinal) || _files[container].ContainsKey(key))
                continue;
            WalkModel(container, key, Resolve(target, container));
        }
    }

    private void WalkModel(CheckContainer container, string path, PathResolution source)
    {
        var walk = new ModelWalk { Container = container, Path = path, Source = source, Info = ModelNaming.Parse(path) };
        _walks.Add(walk);
        if (walk.Info is null)
            walk.Approximate = true;

        try
        {
            if (source.IsModFile)
            {
                var state = State(source.RelPath!);
                if (!state.Exists || state.Kind != FileKind.Mdl)
                {
                    walk.Error = state.Exists ? $"The file is {FileSniffer.Describe(state.Kind)}, not a model." : "The model file is missing.";
                    return;
                }
                walk.Layout = ModelLayoutReader.ReadFile(Full(source.RelPath!));
            }
            else if (source.Source == ResolutionSource.Vanilla && _game.TryReadBytes(source.RequestedPath) is { } bytes)
            {
                walk.Layout = ModelLayoutReader.Read(bytes);
            }
            else
            {
                walk.Error = "The swapped-in model could not be read.";
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            walk.Error = ex.Message;
            return;
        }

        if (!walk.Layout.Structured)
            walk.Approximate = true;
        BuildVariantRows(walk);
        var target = source.Option is { } option ? _model.Find(option) ?? container : container;
        foreach (var name in walk.Layout.Materials.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var folder in walk.Folders)
            {
                var request = walk.Info is { } model
                    ? MaterialNaming.Resolve(model, name, (byte)folder)
                    : name.TrimStart('/').Contains('/')
                        ? new MaterialRequest(MaterialRequestKind.Path, PathNormalizer.GamePath(name))
                        : new MaterialRequest(MaterialRequestKind.Invalid, null,
                            Reason: "The model's game path is not one the check knows, so its material folder is unknown.", Approximate: true);
                if (request.Approximate)
                    walk.Approximate = true;
                if (request.Kind == MaterialRequestKind.Skin)
                {
                    if (!walk.Skins.Any(skin => skin.Name == name))
                        walk.Skins.Add(new SkinUse(name, request.SkinBody, request.SkinSuffix!));
                    break;
                }
                Request? resolved = null;
                if (request.Kind == MaterialRequestKind.Path)
                {
                    resolved = AddRequest(RequestKind.Material, container, request.Path!, target, $"{ShortName(path)}: {name.TrimStart('/')}");
                    if (resolved.Resolution.IsModFile)
                        ValidateMaterial(container, request.Path!, resolved.Resolution);
                }
                walk.Materials.Add(new MaterialUse { Name = name, MaterialSet = folder, Request = request, Resolved = resolved });
            }
        }
    }

    private Request AddRequest(RequestKind kind, CheckContainer root, string path, CheckContainer target, string requester, string? slot = null)
    {
        path = PathNormalizer.GamePath(path);
        var existing = _requests.FirstOrDefault(r => r.Kind == kind && r.Path == path && ReferenceEquals(r.Root, root));
        if (existing is not null)
            return existing;
        var request = new Request
        {
            Kind = kind, Root = root, Path = path, Resolution = Resolve(path, root), Requester = requester, Target = target, SlotLabel = slot,
        };
        _requests.Add(request);
        _requested.Add(path);
        return request;
    }

    // ── Variants ─────────────────────────────────────────────────────────────

    private void BuildVariantRows(ModelWalk walk)
    {
        if (walk.Info is not { UsesImc: true } model)
        {
            walk.Rows.Add(new VariantData(0, new ImcEntry(1, 0, 0x3FF, 0, 0, 0), Array.Empty<string>()));
            return;
        }

        var imcPath = ModelNaming.ImcPath(model)!;
        var part = ModelNaming.ImcPart(model);
        var imcSource = Resolve(imcPath, walk.Container);
        byte[]? bytes = null;
        try
        {
            bytes = imcSource.IsModFile ? File.ReadAllBytes(Full(imcSource.RelPath!)) : _game.TryReadBytes(imcPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        var rows = new SortedDictionary<int, ImcEntry>();
        if (bytes is not null && ImcReader.Parse(bytes) is { } table)
        {
            for (var variant = 0; variant <= table.VariantCount; ++variant)
                if (table.Entry(part, variant) is { } entry)
                    rows[variant] = entry;
            if (imcSource.IsModFile)
                walk.RowNote = $"Uses the IMC file this mod ships ({imcSource.RelPath}).";
        }
        if (rows.Count == 0)
        {
            rows[1] = new ImcEntry(1, 0, 0x3FF, 0, 0, 0);
            walk.RowNote = $"No IMC data for {imcPath}; material set 1 is assumed.";
        }

        // The mod's own IMC manipulations and IMC groups; the asking option wins over the default files.
        foreach (var container in Active(walk.Container).Reverse())
        foreach (var manipulation in _model.Manipulations.Where(m => ReferenceEquals(m.Container, container) && m.Parsed.Imc is { } imc && MetaManipulations.Targets(imc, model)))
            rows[manipulation.Parsed.Imc!.Variant] = manipulation.Parsed.Imc.Entry;
        foreach (var group in _model.ImcGroups.Where(group => !group.DisabledByDefault && MetaManipulations.Targets(group.Identifier, model)).OrderBy(group => group.Priority))
        {
            var mask = group.DefaultMask();
            foreach (var variant in rows.Keys.ToArray())
            {
                if (!group.AllVariants && variant != group.Identifier.Variant)
                    continue;
                rows[variant] = group.OnlyAttributes ? rows[variant] with { AttributeMask = mask } : group.Identifier.Entry with { AttributeMask = mask };
            }
            if (!group.AllVariants && !rows.ContainsKey(group.Identifier.Variant))
                rows[group.Identifier.Variant] = group.Identifier.Entry with { AttributeMask = mask };
        }

        foreach (var (variant, entry) in rows)
        {
            var items = _input.Items?.Names(model.Kind, model.PrimaryId, model.SecondaryId, model.Slot, variant) ?? Array.Empty<string>();
            walk.Rows.Add(new VariantData(variant, entry, items));
        }
    }

    // ── Materials ────────────────────────────────────────────────────────────

    private sealed class MaterialCheck
    {
        public required string Path { get; init; }
        public required CheckContainer Root { get; init; }
        public required PathResolution Source { get; init; }
        public MtrlInfo? Mtrl { get; set; }
        public string? Error { get; set; }
        public List<Request> Textures { get; } = new();
        public Request? Shader { get; set; }
        public List<(string Code, Severity Severity, string Message)> Problems { get; } = new();
    }

    private MaterialCheck ValidateMaterial(CheckContainer root, string path, PathResolution source)
    {
        if (_materials.TryGetValue((root.Option, path), out var check))
            return check;
        check = new MaterialCheck { Path = path, Root = root, Source = source };
        _materials[(root.Option, path)] = check;

        var state = State(source.RelPath!);
        if (!state.Exists || state.Kind != FileKind.Mtrl)
        {
            check.Error = state.Exists ? $"The file is {FileSniffer.Describe(state.Kind)}, not a material." : "The material file is missing.";
            return check;
        }
        try
        {
            check.Mtrl = MtrlReader.Read(File.ReadAllBytes(Full(source.RelPath!)));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentOutOfRangeException or EndOfStreamException)
        {
            check.Error = $"The material could not be read: {ex.Message}";
            return check;
        }

        var target = source.Option is { } option ? _model.Find(option) ?? root : root;
        var mtrl = check.Mtrl;
        check.Shader = AddRequest(RequestKind.Shader, root, ModelNaming.ShaderPath(mtrl.ShaderPackageName), target, ShortName(path));
        if (mtrl.LegacyColorTable)
            check.Problems.Add(("C009", Severity.Info, "Uses a colour table from before Dawntrail with character.shpk. It may look wrong until it is updated in Penumbra's material editor."));
        for (var index = 0; index < mtrl.TextureOffsets.Length; ++index)
        {
            var texture = mtrl.TextureOffsets[index];
            var slot = SamplerName(mtrl, index);
            if (!ResourceNaming.HasValidRoot(texture.Path))
            {
                check.Problems.Add(("C003", Severity.Error, $"Texture slot {slot} holds '{texture.Path}', which is not a game path."));
                continue;
            }
            check.Textures.Add(AddRequest(RequestKind.Texture, root, MaterialNaming.TexturePath(texture.Path, texture.Dx11), target,
                $"{ShortName(path)} ({slot})", slot));
        }
        if (mtrl.ShaderDataParsed)
            foreach (var sampler in mtrl.Samplers.Where(sampler => sampler.HasTexture && sampler.TextureIndex >= mtrl.TextureOffsets.Length))
                check.Problems.Add(("C004", Severity.Error,
                    $"Sampler 0x{sampler.Id:X8} uses texture slot {sampler.TextureIndex}, but the material only has {mtrl.TextureOffsets.Length} textures."));
        return check;
    }

    // ── Findings ─────────────────────────────────────────────────────────────

    private void ModelFindings()
    {
        foreach (var walk in _walks)
        {
            if (walk.Error is not null)
                AddFinding($"F002:{walk.Container.Option.Serialize()}:{walk.Path}", "F002", CheckCategory.Materials, Severity.Error, "Model can't be read",
                    $"{walk.Path}: {walk.Error}", walk.Path, walk.Container, "Export the model again, or replace the file.");
            else if (walk.Layout is { MeshesWithInvalidMaterial.Count: > 0 } layout)
                AddFinding($"C008:{walk.Path}", "C008", CheckCategory.Materials, Severity.Error, "A mesh uses a material the model doesn't list",
                    $"{walk.Path}: mesh {string.Join(", ", layout.MeshesWithInvalidMaterial)} uses a material slot past the model's {layout.Materials.Count} materials.",
                    walk.Path, walk.Container, "Give every mesh a material that exists in your model editor, and export it again.");
            if (walk.Info is null)
                _notes.Add($"{walk.Path} is not a model path the check knows, so its material folders were not checked.");
            else if (walk.Layout is { Structured: false } unstructured)
                _notes.Add($"{walk.Path}: the model's layout could not be followed ({unstructured.StructureError}); its material names were read from its string table.");
            if (walk.Info?.Kind == GameModelKind.Weapon)
                _notes.Add($"{walk.Path}: off-hand weapon mirroring is not followed, so its material paths are approximate and nothing in it is offered for removal.");
        }
    }

    private void MaterialProblemFindings()
    {
        foreach (var check in _materials.Values.DistinctBy(check => check.Path))
        {
            if (check.Error is not null && check.Source.RelPath is { } relPath && State(relPath) is { Exists: true, Kind: FileKind.Mtrl })
                AddFinding($"F002:{check.Path}", "F002", CheckCategory.Materials, Severity.Error, "Material can't be read",
                    $"{check.Path}: {check.Error}", check.Path, check.Root, "Export the material again, or replace the file.");
            foreach (var (code, severity, message) in check.Problems)
                AddFinding($"{code}:{check.Path}:{message}", code, CheckCategory.Materials, severity,
                    code switch { "C003" => "Material has a texture path that isn't one", "C004" => "Material sampler has no texture", _ => "Material is from before Dawntrail" },
                    $"{check.Path}: {message}", check.Path, check.Root,
                    code == "C009" ? null : "Fix the material in Penumbra's material editor (Advanced Editing, Materials).");
        }
    }

    private static readonly Dictionary<uint, string> SamplerNames = new()
    {
        [0x115306BE] = "diffuse",
        [0x0C5EC1F1] = "normal",
        [0x8A4E82B6] = "mask",
        [0x2B99E025] = "specular",
        [0x565F8FD8] = "index",
        [0xFEA0F3D2] = "catchlight",
    };

    private static string SamplerName(MtrlInfo mtrl, int textureIndex)
    {
        var sampler = mtrl.Samplers.FirstOrDefault(s => s.TextureIndex == textureIndex);
        return sampler is not null && SamplerNames.TryGetValue(sampler.Id, out var name) ? name : $"texture {textureIndex + 1}";
    }
}
