using System.Text.Json.Nodes;
using InstantEdit.Services.Animations;
using InstantEdit.Services.NeckSeam;
using InstantEdit.Services.Previews;

namespace InstantEdit.Services.Painter;

/// <summary> What a skin lookup asks Penumbra; delegates, so the lookup runs without the game too. </summary>
/// <param name="Collection">The collection a game object's files come from; null when Penumbra can't tell.</param>
/// <param name="Meta">The object's collection's metadata manipulations as Penumbra's IPC encodes them; null when unavailable.</param>
/// <param name="Resolve">The files a collection loads for game paths (<see cref="PenumbraService.ResolveCollectionPathsAsync"/>).</param>
internal sealed record PainterSkinPenumbra(Func<int, Task<Guid?>> Collection, Func<int, Task<string?>> Meta,
    Func<Guid, IReadOnlyList<string>, Task<string?[]>> Resolve);

/// <summary>
/// Looks up what a skin project paints on a character: in each body slot the smallclothes model
/// (set e0000) the character would wear with nothing equipped there, as its Penumbra collection
/// resolves it (the race by EQDP, the file by the collection's mods), with the parts and connector
/// shapes the game would draw; the skin materials and textures those models load from mods; and
/// the face the character draws.
/// </summary>
internal sealed class PainterSkinResolver(PainterSkinPenumbra penumbra, Func<string, CancellationToken, Task<byte[]?>> readGameFile,
    Action<Exception, string> log)
{
    /// <summary> The request with its <see cref="PainterRequest.Skin"/> parts and the resources they need. </summary>
    public async Task<PainterRequest> ResolveAsync(PainterRequest request, CancellationToken token)
    {
        var live = request.Live;
        var race = live is { Race: > 0 } ? live.Race : request.RacialScaling?.CharacterRace ?? NeckSeamAnalyzer.RaceOf(request.Model.GamePath) ?? 0;
        if (race <= 0)
            throw new InvalidDataException("Your character's race couldn't be read.");
        var collection = await penumbra.Collection(request.ObjectIndex).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Penumbra's collection for your character is unavailable. Is Penumbra running?");

        // The collection's EQDP and IMC manipulations change which smallclothes load and which of their parts show.
        IReadOnlyList<AnimationMetadata.EqdpManipulation> eqdp = [];
        JsonArray meta = [];
        if (await penumbra.Meta(request.ObjectIndex).ConfigureAwait(false) is { } encoded)
        {
            try
            {
                eqdp = AnimationMetadata.DecodeEqdp(encoded);
                meta = AnimationMetadata.Decode(encoded);
            }
            catch (Exception error) when (error is InvalidDataException or FormatException or System.Text.Json.JsonException)
            {
                log(error, "Could not read the collection's metadata; the game's own tables decide the smallclothes.");
            }
        }
        var entries = new Dictionary<int, ushort>();
        foreach (var candidate in new[] { race, PainterSmallclothes.Fallback(race) }.Distinct())
        {
            var file = await readGameFile(PainterSmallclothes.EqdpPath(candidate), token).ConfigureAwait(false);
            var entry = file is null ? (ushort)0 : PainterSmallclothes.EqdpEntry(file, 0);
            foreach (var slot in PainterBodySlot.All)
                foreach (var manipulation in eqdp.Where(m => m.SetId == 0 && m.GenderRace == candidate && m.Slot == slot.EquipSlot))
                    entry = PainterSmallclothes.ApplyEqdp(entry, slot, manipulation.Entry);
            entries[candidate] = entry;
        }

        var gamePaths = PainterBodySlot.All
            .Select(slot => slot.ModelPath(PainterSmallclothes.ModelRace(race, slot, r => entries.GetValueOrDefault(r))))
            .ToList();
        var files = await penumbra.Resolve(collection, gamePaths).ConfigureAwait(false);
        var models = new List<(PainterBodySlot Slot, PainterModelRef Model, ModelMesh Mesh)>();
        for (var i = 0; i < gamePaths.Count; i++)
        {
            var slot = PainterBodySlot.All[i];
            var model = ModelRef(gamePaths[i], files[i]);
            var bytes = await ReadAsync(model.SourcePath, token).ConfigureAwait(false)
                ?? throw new InvalidDataException($"The {slot.Part.ToLowerInvariant()} smallclothes model {model.GamePath} couldn't be read.");
            models.Add((slot, model, ModelMeshReader.Read(bytes, shapes: true)));
        }
        // Wearing nothing in any body slot, the character draws these very models: their parts and shapes
        // are taken as the game draws them, with all Penumbra's metadata turns on or off. Otherwise its
        // gear changes what the smallclothes it wears show, so they follow the rules instead.
        var undressed = live is not null && models.All(m => live.MasksFor(m.Model).Count > 0);
        var shapes = PainterSmallclothes.ConnectorShapes(models.Select(m => (IReadOnlyList<string>)m.Mesh.Shapes.Select(s => s.Name).ToList()).ToList());
        var imc = await readGameFile(PainterSmallclothes.ImcPath, token).ConfigureAwait(false);

        // Body materials load from the character's own skin folder, which the models it wears now show.
        var folder = PainterSmallclothes.SkinFolder(request.Resources.Select(r => r.GamePath))
            ?? models.SelectMany(m => m.Mesh.Materials).Select(PainterSmallclothes.FolderOfName).FirstOrDefault(f => f is not null);
        var parts = new List<PainterSkinPart>();
        var materials = new List<string>();
        for (var i = 0; i < models.Count; i++)
        {
            var (slot, model, mesh) = models[i];
            var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (folder is not null)
                foreach (var name in mesh.Materials)
                    if (PainterSmallclothes.SkinMaterialPath(name, folder) is { } path)
                        paths[name] = path;
            materials.AddRange(paths.Values);
            parts.Add(undressed
                ? new PainterSkinPart(slot.Part, model, live!.MasksFor(model), live.ShapesFor(model), paths)
                : new PainterSkinPart(slot.Part, model, [PainterSmallclothes.EnabledAttributes(mesh.Attributes, slot, ImcAttributes(meta, imc, slot))],
                    shapes[i], paths));
        }
        var head = new PainterSkinPart("Head", request.Model, live?.MasksFor(request.Model) ?? [], live?.ShapesFor(request.Model) ?? 0,
            new Dictionary<string, string>());

        var resources = await ModResourcesAsync(collection, request.Resources, materials, token).ConfigureAwait(false);
        return request with
        {
            Skin = new PainterSkinSources(parts, head) { AsDrawn = undressed }, Resources = request.Resources.Concat(resources).ToList(),
        };
    }

    private static PainterModelRef ModelRef(string gamePath, string? file)
        => file is not null && Path.IsPathRooted(file)
            ? new PainterModelRef(gamePath, file, false)
            : new PainterModelRef(gamePath, PathRules.NormalizeGamePath(file ?? gamePath), true);

    private async Task<byte[]?> ReadAsync(string source, CancellationToken token)
    {
        try
        {
            if (!Path.IsPathRooted(source))
                return await readGameFile(source, token).ConfigureAwait(false);
            return File.Exists(source) ? await File.ReadAllBytesAsync(source, token).ConfigureAwait(false) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            log(error, $"Could not read {source}.");
            return null;
        }
    }

    /// <summary> The IMC attribute mask of the smallclothes in a slot: the collection's manipulation, else the game's e0000 table, else all parts. </summary>
    private static ushort ImcAttributes(JsonArray meta, byte[]? imc, PainterBodySlot slot)
    {
        foreach (var item in meta)
        {
            if (item?["Type"]?.GetValue<string>() != "Imc" || item["Manipulation"] is not JsonObject manipulation)
                continue;
            if (manipulation["ObjectType"]?.GetValue<string>() == "Equipment" && manipulation["PrimaryId"]?.GetValue<int>() == 0 &&
                manipulation["Variant"]?.GetValue<int>() == 0 && manipulation["EquipSlot"]?.GetValue<string>() == slot.EquipSlotName &&
                manipulation["Entry"]?["AttributeMask"]?.GetValue<int>() is { } mask)
                return (ushort)(mask & 0x3FF);
        }
        if (imc is not null)
        {
            try { return PainterSmallclothes.ImcAttributes(imc, 0, slot.Index); }
            catch (InvalidDataException) { }
        }
        return 0x3FF;
    }

    /// <summary>
    /// The mod files the collection loads for the smallclothes' body materials and their textures,
    /// where the character doesn't load them now: the texture plan reads everything else from game data.
    /// </summary>
    private async Task<List<MaterialResourceCandidate>> ModResourcesAsync(Guid collection, IReadOnlyCollection<MaterialResourceCandidate> loaded,
        IEnumerable<string> materials, CancellationToken token)
    {
        var known = loaded.Select(r => PathRules.NormalizeGamePath(r.GamePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<MaterialResourceCandidate>();
        async Task<List<(string GamePath, string Source)>> Resolve(IEnumerable<string> gamePaths)
        {
            var wanted = gamePaths.Select(PathRules.NormalizeGamePath).Where(path => known.Add(path)).ToList();
            var files = await penumbra.Resolve(collection, wanted).ConfigureAwait(false);
            var sources = new List<(string, string)>();
            for (var i = 0; i < wanted.Count; i++)
            {
                var source = files[i] ?? wanted[i];
                // Only redirected paths need a candidate; the plan reads unchanged ones from game data itself.
                if (Path.IsPathRooted(source) || !string.Equals(PathRules.NormalizeGamePath(source), wanted[i], StringComparison.OrdinalIgnoreCase))
                    result.Add(new MaterialResourceCandidate(wanted[i], source));
                sources.Add((wanted[i], source));
            }
            return sources;
        }

        var textures = new List<string>();
        foreach (var (_, source) in await Resolve(materials).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            if (await ReadAsync(source, token).ConfigureAwait(false) is not { } bytes)
                continue;
            try
            {
                var material = SkinMaterial.Read(bytes);
                for (var i = 0; i < material.Textures.Count; i++)
                {
                    textures.Add(PathRules.Dx11TexturePath(material.Textures[i], material.TextureFlags[i]));
                    textures.Add(material.Textures[i]);
                }
            }
            catch (InvalidDataException error)
            {
                log(error, $"Could not read the textures of {source}.");
            }
        }
        await Resolve(textures.Where(path => path.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)).ConfigureAwait(false);
        return result;
    }
}
