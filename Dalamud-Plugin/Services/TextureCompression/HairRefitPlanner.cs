using System.Numerics;
using InstantEdit.Services.GameFiles;
using InstantEdit.Services.NeckSeam;

namespace InstantEdit.Services.TextureCompression;

/// <summary>
/// Hair whose UVs use only part of its textures, as in hair ported from The Sims 4, whose strands
/// sit in one corner of a texture several times their size: what can be cut, and every file that
/// changes with it. Files are paths inside the mod folder.
/// </summary>
/// <param name="Models">Each model and the material slots whose meshes (in every LOD) move into the window.</param>
internal sealed record RefitPlan(UvWindow Window, IReadOnlyList<string> Textures, IReadOnlyList<string> Materials,
    IReadOnlyDictionary<string, IReadOnlySet<int>> Models);

/// <summary> Hair that uses part of its texture but can't be refit, and why, in words for the card. </summary>
internal sealed record RefitRefusal(string Texture, string Reason);

internal sealed record RefitResult(IReadOnlyList<RefitPlan> Plans, IReadOnlyList<RefitRefusal> Refusals);

/// <summary>
/// Plans hair UV refits inside one mod. A texture is cut only together with everything that reads
/// it: starting from a texture the character wears, every material of the mod that reads one of its
/// game paths, every mesh of the mod's models (any option, any race, every LOD) that uses one of
/// those materials, and those materials' other textures, until nothing more joins. All of them must
/// be hair (hair.shpk reads both UV sets, which move together), the UVs must stay inside the texture,
/// and every member must be the mod's own: a texture no material of the mod reads, a material no
/// model of the mod uses, or a texture a material reads from the game, at a path where the game has
/// a file of its own, means the game's files would pair with the changed ones, so the group is left
/// alone, unless the mod brings its own models for that material's folder. Option combinations that
/// pair the mod's files with the game's (a material on without the model it was made for) aren't
/// looked at either: they show the mod's textures on the game's UVs before any refit.
/// Other mods aren't looked at. Dalamud-free.
/// </summary>
internal sealed class HairRefitPlanner
{
    public const string HairShader = "hair.shpk";

    private readonly ModFileMap _map;
    private readonly Func<string, byte[]?> _readFile;
    private readonly Func<string, IReadOnlyList<string>?> _readMaterialNames;
    private readonly Func<string, bool> _gameFileExists;
    private readonly Dictionary<string, SkinMaterial?> _materials = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, List<string>>? _textureReaders;
    private Dictionary<string, List<(string Model, int Slot)>>? _materialUsers;
    private readonly Dictionary<string, IReadOnlyList<string>?> _modelMaterials = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="readFile">A file of the mod by its path inside the mod folder; null when it is missing.</param>
    /// <param name="readMaterialNames">A model's material names, from its headers; null when it can't be read.</param>
    /// <param name="gameFileExists">Whether the game's own data has a file at a game path.</param>
    public HairRefitPlanner(ModFileMap map, Func<string, byte[]?> readFile, Func<string, IReadOnlyList<string>?> readMaterialNames,
        Func<string, bool> gameFileExists)
    {
        _map = map;
        _readFile = readFile;
        _readMaterialNames = readMaterialNames;
        _gameFileExists = gameFileExists;
    }

    /// <summary> Plans for the groups of <paramref name="textures"/> that can be cut to half their size or less, and why the others can't. </summary>
    public RefitResult Plan(IEnumerable<string> textures)
    {
        var plans = new List<RefitPlan>();
        var refusals = new List<RefitRefusal>();
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // A model that can't be read could use any of the mod's materials.
        var unreadable = _map.Files(".mdl").FirstOrDefault(file => ModelMaterials(file) is null);
        foreach (var seed in textures.Select(ModFileMap.FileKey))
        {
            if (!covered.Add(seed))
                continue;
            if (unreadable is not null)
            {
                refusals.Add(new RefitRefusal(seed, $"the mod's {Name(unreadable)} can't be read, so what it uses is unknown"));
                continue;
            }
            var group = new Group(this);
            group.AddTexture(seed);
            group.Grow();
            covered.UnionWith(group.Textures);
            var outcome = group.Refusal is null ? Fit(group) : (null, group.Refusal);
            if (outcome.Plan is { } plan)
                plans.Add(plan);
            else if (outcome.Refusal is { } reason)
                refusals.Add(new RefitRefusal(seed, reason));
        }
        return new RefitResult(plans, refusals);
    }

    /// <summary> The window the group's UVs fit in, when it saves at least half; a refusal when the files don't allow one. </summary>
    private (RefitPlan? Plan, string? Refusal) Fit(Group group)
    {
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);
        foreach (var (file, slots) in group.Meshes)
        {
            RefitModel model;
            try
            {
                model = RefitModel.Read(_readFile(file) ?? throw new FileNotFoundException("The model is missing."));
            }
            catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException)
            {
                return (null, $"{Name(file)} can't be refit: {e.Message}");
            }
            if (model.MeshesWithoutUvs.Any(slots.Contains))
                return (null, $"{Name(file)} has a part without UVs");
            foreach (var mesh in model.Meshes.Where(mesh => slots.Contains(mesh.MaterialIndex)))
            {
                if (!mesh.FourComponents)
                    return (null, $"{Name(file)} has a part with one UV set, where the hair shader reads two");
                // Both sets move the same way, so the window must hold both; usually they are the same.
                foreach (var uv in mesh.Uvs)
                {
                    min = Vector2.Min(min, Vector2.Min(new Vector2(uv.X, uv.Y), new Vector2(uv.Z, uv.W)));
                    max = Vector2.Max(max, Vector2.Max(new Vector2(uv.X, uv.Y), new Vector2(uv.Z, uv.W)));
                }
            }
        }
        if (min.X > max.X)
            return (null, null);

        int shiftU = 12, shiftV = 12;
        foreach (var file in group.Textures)
        {
            var bytes = _readFile(file);
            if (bytes is null || TextureCost.Read(bytes) is not { Is2D: true } info || TextureCost.BitsPerPixel(info.Format) is null)
                return (null, $"{Name(file)} can't be read as an ordinary texture");
            var blocks = TextureCost.IsBlockCompressed(info.Format);
            shiftU = Math.Min(shiftU, TextureCrop.MaxShift(info.Width, blocks));
            shiftV = Math.Min(shiftV, TextureCrop.MaxShift(info.Height, blocks));
        }
        // Hair spread over more than half the texture each way has nothing to cut, wherever its UVs lie.
        if (max.X - min.X > 0.5f && max.Y - min.Y > 0.5f)
            return (null, null);
        if (TextureCrop.Fit(min, max, shiftU, shiftV) is not { } window)
            return (null, "its UVs reach outside the texture, where they wrap around");
        if (window.Fraction > 0.5)
            return (null, null);
        return (new RefitPlan(window, group.Textures.ToList(), group.Materials.ToList(),
            group.Meshes.ToDictionary(pair => pair.Key, pair => (IReadOnlySet<int>)pair.Value, StringComparer.OrdinalIgnoreCase)), null);
    }

    private SkinMaterial? Material(string file)
    {
        if (!_materials.TryGetValue(file, out var material))
        {
            try
            {
                material = _readFile(file) is { } bytes ? SkinMaterial.Read(bytes) : null;
            }
            catch (Exception e) when (e is InvalidDataException or ArgumentException or IndexOutOfRangeException)
            {
                material = null;
            }
            _materials[file] = material;
        }
        return material;
    }

    private IReadOnlyList<string>? ModelMaterials(string file)
    {
        if (!_modelMaterials.TryGetValue(file, out var names))
            _modelMaterials[file] = names = _readMaterialNames(file);
        return names;
    }

    /// <summary> The game paths a material reads textures from: each texture as stored and as the game requests it. </summary>
    private static IEnumerable<string> TexturePaths(SkinMaterial material)
    {
        for (var i = 0; i < material.Textures.Count; i++)
        {
            yield return material.Textures[i];
            yield return PathRules.Dx11TexturePath(material.Textures[i], material.TextureFlags[i]);
        }
    }

    /// <summary> Texture game path → the mod's materials that read it. </summary>
    private Dictionary<string, List<string>> TextureReaders()
    {
        if (_textureReaders is { } built)
            return built;
        built = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in _map.Files(".mtrl"))
            if (Material(file) is { } material)
                foreach (var path in TexturePaths(material).Select(ModFileMap.GamePathKey).Distinct())
                {
                    if (!built.TryGetValue(path, out var readers))
                        built[path] = readers = [];
                    readers.Add(file);
                }
        return _textureReaders = built;
    }

    /// <summary> Material game path → the mod's model slots that use it, in any option. </summary>
    private Dictionary<string, List<(string Model, int Slot)>> MaterialUsers()
    {
        if (_materialUsers is { } built)
            return built;
        built = new Dictionary<string, List<(string, int)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in _map.Files(".mdl"))
        {
            if (ModelMaterials(file) is not { } names)
                continue;
            foreach (var mapping in _map.ForFile(file))
                for (var slot = 0; slot < names.Count; slot++)
                    if (VanillaMaterialPaths.Resolve(mapping.GamePath, names[slot]) is { } path)
                    {
                        var key = ModFileMap.GamePathKey(path);
                        if (!built.TryGetValue(key, out var users))
                            built[key] = users = [];
                        users.Add((file, slot));
                    }
        }
        return _materialUsers = built;
    }

    private static string Name(string file) => Path.GetFileName(file);

    /// <summary> Everything that has to change together with one texture. </summary>
    private sealed class Group(HairRefitPlanner planner)
    {
        private readonly Queue<Action> _work = new();
        public SortedSet<string> Textures { get; } = new(StringComparer.OrdinalIgnoreCase);
        public SortedSet<string> Materials { get; } = new(StringComparer.OrdinalIgnoreCase);
        public SortedDictionary<string, HashSet<int>> Meshes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? Refusal { get; private set; }

        private ModFileMap Map => planner._map;
        private bool Game(string gamePath) => planner._gameFileExists(gamePath);

        private void Refuse(string reason) => Refusal ??= reason;

        public void Grow()
        {
            while (Refusal is null && _work.TryDequeue(out var next))
                next();
        }

        public void AddTexture(string file)
        {
            if (Textures.Add(file))
                _work.Enqueue(() => VisitTexture(file));
        }

        private void AddMaterial(string file)
        {
            if (Materials.Add(file))
                _work.Enqueue(() => VisitMaterial(file));
        }

        private void AddSlot(string model, int slot)
        {
            if (!Meshes.TryGetValue(model, out var slots))
                Meshes[model] = slots = [];
            if (slots.Add(slot))
                _work.Enqueue(() => VisitSlot(model, slot));
        }

        private void VisitTexture(string file)
        {
            var readers = planner.TextureReaders();
            foreach (var mapping in Map.ForFile(file))
            {
                var materials = readers.GetValueOrDefault(mapping.GamePath) ?? [];
                foreach (var material in materials)
                    AddMaterial(material);
                if (materials.Count == 0 && Game(mapping.GamePath))
                    Refuse($"the game's own materials can read it as {mapping.GamePath}");
            }
        }

        private void VisitMaterial(string file)
        {
            if (planner.Material(file) is not { } material)
            {
                Refuse($"{Name(file)}, which reads it, can't be read");
                return;
            }
            if (!string.Equals(material.ShaderPackage, HairShader, StringComparison.OrdinalIgnoreCase))
            {
                Refuse($"{Name(file)}, which reads it too, uses {material.ShaderPackage}, not the hair shader");
                return;
            }
            var paths = TexturePaths(material).Select(ModFileMap.GamePathKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var path in paths)
                foreach (var texture in Map.ForGamePath(path))
                    AddTexture(texture.File);
            for (var i = 0; i < material.Textures.Count; i++)
            {
                string stored = ModFileMap.GamePathKey(material.Textures[i]),
                    requested = ModFileMap.GamePathKey(PathRules.Dx11TexturePath(material.Textures[i], material.TextureFlags[i]));
                if (!Map.ForGamePath(stored).Any() && !Map.ForGamePath(requested).Any() && (Game(stored) || Game(requested)))
                    Refuse($"{Name(file)} reads the game's own {Path.GetFileName(requested)}, which wouldn't be cut with it");
            }
            var users = planner.MaterialUsers();
            foreach (var mapping in Map.ForFile(file))
            {
                var slots = users.GetValueOrDefault(mapping.GamePath) ?? [];
                foreach (var (model, slot) in slots)
                    AddSlot(model, slot);
                if (slots.Count == 0 && Game(mapping.GamePath) && !ReplacesModels(mapping.GamePath))
                    Refuse($"the game's own models can use {Name(file)} as {mapping.GamePath}");
            }
        }

        /// <summary>
        /// Whether the mod brings its own models in the folder a material belongs to, such as
        /// chara/human/c0201/obj/hair/h0178/model for that hair's materials: the game's model that would
        /// use a material the mod's models don't is then the mod's too.
        /// </summary>
        private bool ReplacesModels(string materialPath)
        {
            var folder = materialPath.IndexOf("/material/", StringComparison.OrdinalIgnoreCase);
            if (folder <= 0)
                return false;
            var models = materialPath[..folder] + "/model/";
            return Map.Mappings.Any(m => m.GamePath.StartsWith(models, StringComparison.OrdinalIgnoreCase) && m.GamePath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase));
        }

        private void VisitSlot(string model, int slot)
        {
            if (planner.ModelMaterials(model) is not { } names || slot >= names.Count)
            {
                Refuse($"{Name(model)}, which uses it, can't be read");
                return;
            }
            foreach (var mapping in Map.ForFile(model))
            {
                if (VanillaMaterialPaths.Resolve(mapping.GamePath, names[slot]) is not { } resolved)
                {
                    Refuse($"{Name(model)} names a material the game can't find");
                    continue;
                }
                foreach (var material in Map.ForGamePath(ModFileMap.GamePathKey(resolved)))
                    AddMaterial(material.File);
            }
        }
    }
}
