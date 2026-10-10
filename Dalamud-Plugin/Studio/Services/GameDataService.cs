using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Lumina.Data.Files;
using Lumina.Excel.Sheets;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Services.Resources;

namespace InstantEdit.Studio.Services;

/// <summary>
/// Provides slot + item browsing over the game's Item sheet so the user can
/// pick which piece of gear the imported outfit should replace.
/// </summary>
public sealed class GameDataService : IItemNames
{
    private readonly IDataManager _data;
    private readonly IPluginLog   _log;

    /// <summary>Searchable equipment/accessory items (model-backed only), by slot.</summary>
    private volatile Dictionary<EquipSlot, List<GameItem>>? _itemsBySlot;
    private Dictionary<uint, GameItem>?            _itemsByRowId;
    private Dictionary<(bool Accessory, ushort SetId), List<(string Name, EquipSlot Slot)>>? _itemsBySet;
    private int                                    _cacheCount;
    private readonly object                        _cacheLock = new();

    /// <summary>The game's files for worker threads: existence and bytes only, nothing memoised.</summary>
    internal IVanillaFiles VanillaFiles { get; }

    /// <summary>
    /// The gear that uses a model set. Read-only once the item cache is built (it is published whole),
    /// so it is safe from the resource graph's worker; empty until then.
    /// </summary>
    IReadOnlyList<(string Name, EquipSlot Slot)> IItemNames.ForSet(bool accessory, ushort setId)
    {
        if (_itemsBySlot == null)
            return Array.Empty<(string, EquipSlot)>();
        return _itemsBySet != null && _itemsBySet.TryGetValue((accessory, setId), out var list)
            ? list
            : Array.Empty<(string, EquipSlot)>();
    }

    private sealed class GameFiles : IVanillaFiles
    {
        private readonly IDataManager _data;

        public GameFiles(IDataManager data) => _data = data;

        public bool Exists(string gamePath)
        {
            try { return _data.FileExists(gamePath); }
            catch (Exception) { return false; }
        }

        public byte[]? TryReadBytes(string gamePath)
        {
            try { return _data.GetFile(gamePath)?.Data; }
            catch (Exception) { return null; }
        }
    }

    // Per-item probes the validator asks about every tick; the answers never change
    // while the game is running, so each is computed once.
    private readonly Dictionary<(EquipSlot, ushort), HashSet<RaceGender>> _nativeRaces = new();
    private readonly Dictionary<(EquipSlot, ushort), bool>                _hasVanillaTemplate = new();

    public GameDataService(IDataManager data, IPluginLog log)
    {
        _data = data;
        _log  = log;
        VanillaFiles = new GameFiles(data);
    }

    /// <summary>A searchable game item from the Item excel sheet.</summary>
    public sealed class GameItem
    {
        public uint      RowId        { get; init; }
        public string    Name         { get; init; } = string.Empty;
        public ushort    ModelId      { get; init; }
        public ushort    Variant      { get; init; }
        public EquipSlot Slot         { get; init; }
        public bool      IsAccessory  { get; init; }

        /// <summary>The item's icon id, for the browser's tiles.</summary>
        public uint      Icon         { get; init; }

        public string ModelIdPadded  => ModelId.ToString("D4");
        public string ModelIdDisplay => $"{ModelIdPadded}-{Variant}";
    }

    /// <summary>All items for a slot, sorted by name. Triggers a cache build if needed.</summary>
    public List<GameItem> GetAllItemsForSlot(EquipSlot slot)
        => GetItemCache().TryGetValue(slot, out var list) ? list : new();

    /// <summary>Looks an item up by row ID (used to restore the last selection).</summary>
    public GameItem? GetItemByRowId(uint rowId)
    {
        GetItemCache();
        return _itemsByRowId != null && _itemsByRowId.TryGetValue(rowId, out var item)
            ? item
            : null;
    }

    /// <summary>Total number of cached items. Builds the cache if needed.</summary>
    public int CacheCount
    {
        get
        {
            GetItemCache();
            return _cacheCount;
        }
    }

    /// <summary>True once the item cache exists, so callers can avoid triggering a build on the render thread.</summary>
    public bool IsItemCacheReady => _itemsBySlot != null;

    /// <summary>Builds the item cache on a worker thread, so the first window draw does not stall the game.</summary>
    public System.Threading.Tasks.Task WarmUpAsync()
        => System.Threading.Tasks.Task.Run(() => GetItemCache());

    /// <summary>Whether this item has a vanilla material to clone. Cached per item.</summary>
    public bool HasVanillaMaterialTemplate(EquipSlot slot, ushort modelId)
    {
        lock (_hasVanillaTemplate)
        {
            if (_hasVanillaTemplate.TryGetValue((slot, modelId), out var known))
                return known;
        }

        bool found = false;
        for (int letter = 1; letter <= 26 && !found; letter++)
        {
            var path = MaterialNaming.MaterialGamePath(slot, modelId, MaterialNaming.DefaultName(slot, modelId, letter));
            try { found = _data.FileExists(path); }
            catch (Exception ex) { _log.Debug(ex, "[Studio] FileExists check failed for {0}", path); }
        }

        lock (_hasVanillaTemplate)
            _hasVanillaTemplate[(slot, modelId)] = found;
        return found;
    }

    /// <summary>
    /// Returns the race/gender combinations that already have their own model file
    /// for this item by default (as opposed to falling back to another race's model).
    /// </summary>
    public HashSet<RaceGender> GetNativeModelRaces(EquipSlot slot, ushort modelId)
    {
        lock (_nativeRaces)
        {
            if (_nativeRaces.TryGetValue((slot, modelId), out var cached))
                return cached;
        }

        var result = ProbeNativeModelRaces(slot, modelId);
        lock (_nativeRaces)
            _nativeRaces[(slot, modelId)] = result;
        return result;
    }

    private HashSet<RaceGender> ProbeNativeModelRaces(EquipSlot slot, ushort modelId)
    {
        var result = new HashSet<RaceGender>();
        foreach (var rg in RaceInfo.AllRaces)
        {
            var path = ModelNaming.ModelGamePath(slot, modelId, rg.RaceCode);
            try
            {
                if (_data.FileExists(path))
                    result.Add(rg);
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "[Studio] FileExists check failed for {0}", path);
            }
        }
        return result;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Port subjects (gear and character features)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Highest feature id probed. Hair, face, tail and ear ids all stay well below this.</summary>
    private const int MaxFeatureId = 999;

    private readonly Dictionary<SubjectKind, System.Threading.Tasks.Task<IReadOnlyDictionary<RaceGender, IReadOnlyList<ushort>>>> _featureScans = new();
    private readonly Dictionary<(SubjectKind, ushort), HashSet<RaceGender>> _featureNativeRaces = new();
    private readonly Dictionary<string, IReadOnlyList<string>> _vanillaMaterialNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Every id of a feature kind that exists in the game, per race — found by probing the
    /// model path of each candidate id. Scanned once per kind on a worker; returns null until
    /// that scan has finished (the browser shows a wait state). Races with no ids are left out.
    /// </summary>
    internal IReadOnlyDictionary<RaceGender, IReadOnlyList<ushort>>? GetFeatureIds(SubjectKind kind)
    {
        System.Threading.Tasks.Task<IReadOnlyDictionary<RaceGender, IReadOnlyList<ushort>>> scan;
        lock (_featureScans)
        {
            if (!_featureScans.TryGetValue(kind, out scan!))
                _featureScans[kind] = scan = System.Threading.Tasks.Task.Run(() => ScanFeatureIds(kind));
        }
        return scan.IsCompletedSuccessfully ? scan.Result : null;
    }

    private IReadOnlyDictionary<RaceGender, IReadOnlyList<ushort>> ScanFeatureIds(SubjectKind kind)
    {
        var result = new Dictionary<RaceGender, IReadOnlyList<ushort>>();
        foreach (var rg in FeatureNaming.CandidateRaces(kind))
        {
            var ids = new List<ushort>();
            for (ushort id = 1; id <= MaxFeatureId; id++)
                if (Exists(FeatureNaming.ModelGamePath(kind, rg.RaceCode, id)))
                    ids.Add(id);
            if (ids.Count > 0)
                result[rg] = ids;
        }
        _log.Information("[Studio] {0} scan: {1} id(s) across {2} race(s)", kind, result.Values.Sum(l => l.Count), result.Count);
        return result;
    }

    /// <summary>
    /// Races that ship their own vanilla model for this subject. Gear: races with their own
    /// model for the item. Hair and other features: races that have this id at all.
    /// </summary>
    internal HashSet<RaceGender> GetNativeRaces(PortSubject subject)
    {
        if (subject is GearSubject gear)
            return GetNativeModelRaces(gear.Item.Slot, gear.Item.ModelId);

        var feature = (FeatureSubject)subject;
        var key = (feature.Kind, feature.Id);
        lock (_featureNativeRaces)
        {
            if (_featureNativeRaces.TryGetValue(key, out var cached))
                return cached;
        }

        var result = RaceInfo.AllRaces.Where(rg => Exists(feature.ModelGamePath(rg))).ToHashSet();
        lock (_featureNativeRaces)
            _featureNativeRaces[key] = result;
        return result;
    }

    /// <summary>
    /// The material names the vanilla model of <paramref name="subject"/> for <paramref name="race"/>
    /// references, as written in the model (e.g. "/mt_c0101h0005_hir_a.mtrl"). Empty when there
    /// is no vanilla model. Cached per model path.
    /// </summary>
    internal IReadOnlyList<string> GetVanillaMaterialNames(PortSubject subject, RaceGender race)
    {
        var path = subject.ModelGamePath(race);
        lock (_vanillaMaterialNames)
        {
            if (_vanillaMaterialNames.TryGetValue(path, out var cached))
                return cached;
        }

        IReadOnlyList<string> names = Exists(path)
            ? GetVanillaModel(path, out _)?.MaterialNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>()
            : new List<string>();
        lock (_vanillaMaterialNames)
            _vanillaMaterialNames[path] = names;
        return names;
    }

    /// <summary>The race whose vanilla files describe a subject: a feature's own race, or gear's first native race.</summary>
    internal RaceGender? ReferenceRace(PortSubject subject)
        => subject.BaseRace ?? GetNativeRaces(subject).OrderBy(rg => rg.RaceCode, StringComparer.Ordinal).Cast<RaceGender?>().FirstOrDefault();

    /// <summary>Whether there is a vanilla material to clone for this subject when a shader has no bundled preset.</summary>
    internal bool HasVanillaMaterial(PortSubject subject)
    {
        if (subject is GearSubject gear)
            return HasVanillaMaterialTemplate(gear.Item.Slot, gear.Item.ModelId);
        var race = ReferenceRace(subject);
        return race != null && GetVanillaMaterialNames(subject, race.Value).Count > 0;
    }

    /// <summary>
    /// A vanilla material to clone for <paramref name="materialName"/>: the vanilla material of
    /// that exact name if there is one, else the first material the vanilla model uses, else
    /// (gear) any material in the item's folder.
    /// </summary>
    internal (string GamePath, MtrlInfo Material)? FindVanillaMaterial(PortSubject subject, string materialName, RaceGender? race)
    {
        // Memoised: the validator asks every few seconds, and vanilla files never change. Callers
        // must not edit the returned material (the patcher works on a clone).
        var key = $"{subject.Key}|{materialName}|{race?.RaceCode}";
        lock (_vanillaMaterials)
            if (_vanillaMaterials.TryGetValue(key, out var cached))
                return cached;

        var found = FindVanillaMaterialUncached(subject, materialName, race);
        lock (_vanillaMaterials)
            _vanillaMaterials[key] = found;
        return found;
    }

    private readonly Dictionary<string, (string GamePath, MtrlInfo Material)?> _vanillaMaterials = new();

    private (string GamePath, MtrlInfo Material)? FindVanillaMaterialUncached(PortSubject subject, string materialName, RaceGender? race)
    {
        var exact = TryReadMaterial(subject.MaterialGamePath(subject.MaterialNameFor(materialName, race), race));
        if (exact != null)
            return exact;

        var reference = race ?? ReferenceRace(subject);
        if (reference != null)
        {
            foreach (var name in GetVanillaMaterialNames(subject, reference.Value))
            {
                var found = TryReadMaterial(subject.VanillaMaterialPath(name, reference.Value));
                if (found != null)
                    return found;
            }
        }

        return subject is GearSubject gear ? FindVanillaMaterialTemplate(gear.Item.Slot, gear.Item.ModelId) : null;
    }

    /// <summary>
    /// The vanilla model's materials as editable set-ups: each material's name, its shader,
    /// and a texture slot per texture role it binds. Null when there is no vanilla model.
    /// </summary>
    internal List<MaterialSetup>? ReadVanillaMaterialLayout(PortSubject subject, RaceGender race)
    {
        var names = GetVanillaMaterialNames(subject, race);
        if (names.Count == 0)
            return null;

        var result = new List<MaterialSetup>();
        foreach (var referenced in names)
        {
            var name = MaterialNaming.SanitizeFileName(referenced.TrimStart('/'));
            var setup = new MaterialSetup { Name = name, ShaderType = subject.Kind == SubjectKind.Gear ? ShaderType.Character : ShaderType.Hair };

            var mtrl = TryReadMaterial(subject.VanillaMaterialPath(referenced, race));
            if (mtrl != null)
            {
                var info = mtrl.Value.Material;
                setup.ShaderType = ShaderInfo.FromShaderPack(info.ShaderPackageName) ?? setup.ShaderType;
                foreach (var type in MaterialPatcher.TextureTypes(info))
                    setup.Textures.Add(new TextureSlot { Type = type, Postfix = MaterialNaming.DefaultPostfix(type) });
            }

            result.Add(setup);
        }
        return result;
    }

    private (string GamePath, MtrlInfo Material)? TryReadMaterial(string path)
    {
        try
        {
            if (!_data.FileExists(path))
                return null;
            var raw = _data.GetFile(path)?.Data;
            return raw != null ? (path, MtrlReader.Read(raw)) : null;
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "[Studio] Failed to read vanilla material {0}", path);
            return null;
        }
    }

    internal bool Exists(string path)
    {
        try { return _data.FileExists(path); }
        catch (Exception ex)
        {
            _log.Debug(ex, "[Studio] FileExists check failed for {0}", path);
            return false;
        }
    }

    /// <summary>Reads the raw, unmodified bytes of a game file, or null if it doesn't exist.</summary>
    // -- Metadata tables (EQP, EST) ------------------------------------------

    private EqpReader? _eqp;
    private bool _eqpLoaded;
    private readonly Dictionary<(string RaceCode, bool Accessory), EqdpReader?> _eqdp = new();
    private readonly Dictionary<(SubjectKind Kind, ushort Id, string RaceCode), RaceGender> _materialRaces = new();
    private readonly Dictionary<string, EstReader?> _est = new();

    /// <summary>The game's equipment parameters for a set id: what a piece hides or shows. 0 when unknown.</summary>
    public ulong VanillaEqp(ushort setId)
    {
        lock (_est)
        {
            if (!_eqpLoaded)
            {
                _eqp = EqpReader.Load(this);
                _eqpLoaded = true;
            }
        }
        return _eqp?.Entry(setId) ?? 0;
    }

    /// <summary>
    /// The race's own equipment deformer bits for a set id: which of its slots the game has a
    /// material and a model of its own for. 0 when the game has no entry, which is exactly when a
    /// port writing that race's files has to add one.
    /// </summary>
    public ushort VanillaEqdp(RaceGender race, bool accessory, ushort setId)
    {
        var key = (race.RaceCode, accessory);
        lock (_eqdp)
        {
            if (!_eqdp.TryGetValue(key, out var reader))
                _eqdp[key] = reader = EqdpReader.Load(this, race, accessory);
            return reader?.Entry(setId) ?? 0;
        }
    }

    /// <summary>
    /// The race a model's materials are actually looked up under — which is not always the race
    /// wearing them. The game swaps the race code in a material name for the one it shares that
    /// material under: for gear the wearer's own race when its Eqdp material bit is set, otherwise
    /// the base race of its gender; for a character feature the race its vanilla model's own
    /// material names point at (a Miqo'te hair's materials live under c0201). Writing a material
    /// anywhere else leaves it where the game never looks.
    /// </summary>
    internal RaceGender MaterialRaceFor(PortSubject subject, RaceGender modelRace)
    {
        if (subject is GearSubject gear)
            return (VanillaEqdp(modelRace, SlotInfo.IsAccessory(gear.Item.Slot), gear.Item.ModelId) & EqdpInfo.Material(gear.Item.Slot)) != 0
                ? modelRace
                : RaceInfo.BaseFor(modelRace.Gender);

        var feature = (FeatureSubject)subject;
        var key = (feature.Kind, feature.Id, modelRace.RaceCode);
        lock (_materialRaces)
        {
            if (_materialRaces.TryGetValue(key, out var cached))
                return cached;
        }

        var race = ProbeMaterialRace(feature, modelRace);
        lock (_materialRaces)
            _materialRaces[key] = race;
        return race;
    }

    /// <summary>
    /// Reads the sharing out of the game's own files: the race's model for this id names the race it
    /// takes its materials from, and where the game has no model for that id, the race's first
    /// feature of the kind does — the sharing follows the race, not the id. Falls back to the base
    /// race of the gender, which is what everything else falls back to.
    /// </summary>
    private RaceGender ProbeMaterialRace(FeatureSubject feature, RaceGender modelRace)
    {
        foreach (var id in new[] { feature.Id, (ushort)1 })
        {
            var probe = new FeatureSubject(feature.Kind, modelRace, id);
            foreach (var name in GetVanillaMaterialNames(probe, modelRace))
            {
                if (!FeatureNaming.TryReadRaceCode(name, out var code))
                    continue;
                foreach (var rg in RaceInfo.AllRaces)
                {
                    if (rg.RaceCode == code)
                        return rg;
                }
            }
        }
        return RaceInfo.BaseFor(modelRace.Gender);
    }

    /// <summary>
    /// The races a port writes a copy of each material for: one per race its models resolve their
    /// materials to (see <see cref="MaterialRaceFor"/>), so every model finds the .mtrl the game
    /// will ask it for. With nothing set up, gear keeps the name it was given.
    /// </summary>
    internal IReadOnlyList<RaceGender?> MaterialRaces(PortSubject subject, IReadOnlyList<RaceModelEntry> models)
    {
        if (models.Count == 0)
            return subject.BaseRace is { } baseRace
                ? new RaceGender?[] { MaterialRaceFor(subject, baseRace) }
                : subject.MaterialRaces(models);

        return models.Select(m => (RaceGender?)MaterialRaceFor(subject, m.RaceGender))
            .Distinct()
            .OrderBy(r => r!.Value.RaceCode, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The skeleton the game ships for a hair id on a race, or null when that race has no entry for it.</summary>
    public ushort? VanillaHairSkeleton(RaceGender race, ushort hairId)
        => Est(EstReader.HairPath)?.Entry(race.RaceCode, hairId);

    private EstReader? Est(string gamePath)
    {
        lock (_est)
        {
            if (!_est.TryGetValue(gamePath, out var reader))
                _est[gamePath] = reader = EstReader.Load(this, gamePath);
            return reader;
        }
    }

    /// <summary>Every hair id on a race that has a skeleton entry, with the skeleton it uses.</summary>
    public IReadOnlyList<(ushort HairId, ushort SkeletonId)> HairSkeletonEntries(RaceGender race)
        => Est(EstReader.HairPath)?.EntriesFor(race.RaceCode) ?? Array.Empty<(ushort, ushort)>();

    /// <summary>The vanilla hair on a race that uses this skeleton, so a port can borrow its bones.</summary>
    public ushort? HairUsingSkeleton(RaceGender race, ushort skeletonId)
        => Est(EstReader.HairPath)?.SetUsing(race.RaceCode, skeletonId);

    /// <summary>Whether a race actually has the hair skeleton an EST entry would point at.</summary>
    public bool HasHairSkeleton(RaceGender race, ushort skeletonId)
        => skeletonId != 0 && Exists($"chara/human/c{race.RaceCode}/skeleton/hair/h{skeletonId:D4}/skl_c{race.RaceCode}h{skeletonId:D4}.sklb");

    public byte[]? GetVanillaFileBytes(string gamePath)
    {
        try
        {
            return _data.GetFile(gamePath)?.Data;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[Studio] Failed to read vanilla file {0}", gamePath);
            return null;
        }
    }

    /// <summary>
    /// Reads and parses the skeleton/bone metadata of the vanilla, unmodified model
    /// at a game path, or null if it doesn't exist or fails to parse. Reads raw bytes
    /// and parses them with <see cref="VanillaModelReader"/> rather than Lumina's own
    /// typed model reader, which throws on every retail model tested against the
    /// currently installed Dalamud/Lumina build — see VanillaModelReader's doc comment.
    /// </summary>
    public VanillaModelInfo? GetVanillaModel(string gamePath, out string? error)
    {
        error = null;
        try
        {
            var bytes = _data.GetFile(gamePath)?.Data;
            if (bytes == null)
            {
                error = "file not found";
                return null;
            }
            return VanillaModelReader.Read(bytes);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[Studio] Failed to read vanilla model {0}", gamePath);
            error = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// Finds a vanilla material to use as a clone target for a new .mtrl: tries every
    /// letter suffix ("_a".."_z") at the v0001 material folder (which virtually always has
    /// at least a "_a" material). Returns null if nothing usable is found. Fallback for
    /// shader types with no bundled preset — see <see cref="MaterialPresetLibrary"/>.
    /// </summary>
    public (string GamePath, MtrlInfo Material)? FindVanillaMaterialTemplate(EquipSlot slot, ushort modelId)
    {
        for (int letter = 1; letter <= 26; letter++)
        {
            var name = MaterialNaming.DefaultName(slot, modelId, letter);
            var path = MaterialNaming.MaterialGamePath(slot, modelId, name);
            try
            {
                if (!_data.FileExists(path)) continue;

                var raw = _data.GetFile(path)?.Data;
                if (raw != null)
                    return (path, MtrlReader.Read(raw));
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "[Studio] Failed to read candidate material template {0}", path);
            }
        }
        return null;
    }

    /// <summary>
    /// Reads the item's IMC file and builds one <see cref="ModImcOverride"/> per variant it
    /// has, each forcing that variant's MaterialId back to 1 while preserving every other
    /// field, so the mod's material shows regardless of which recolor/dye variant the
    /// equipped item instance actually uses. Returns null if the IMC file doesn't exist,
    /// the slot's part isn't present in it, or it fails to parse.
    /// </summary>
    public List<ModImcOverride>? GetImcOverridesForcingV1(EquipSlot slot, ushort modelId)
    {
        var path = MaterialNaming.ImcGamePath(slot, modelId);
        try
        {
            var imc = _data.GetFile<ImcFile>(path);
            if (imc == null) return null;

            int partIdx = SlotInfo.ImcPartIndex(slot);
            var parts = imc.GetParts();
            if (partIdx >= parts.Length || (imc.PartMask & (1 << partIdx)) == 0)
                return null;

            var part = parts[partIdx];
            var overrides = new List<ModImcOverride>(imc.Count);
            for (int v = 1; v <= imc.Count; v++)
            {
                var entry = part.Variants[v - 1];
                overrides.Add(new ModImcOverride
                {
                    Slot                = slot,
                    SetId               = modelId,
                    Variant             = (byte)v,
                    DecalId             = entry.DecalId,
                    VfxId               = entry.VfxId,
                    MaterialAnimationId = entry.MaterialAnimationId,
                    AttributeMask       = entry.AttributeMask,
                    // Lumina's ImcFile.ImageChangeData.SoundId returns the raw unshifted
                    // high bits (bug: missing ">> 10"); correct it here.
                    SoundId             = (byte)(entry.SoundId >> 10),
                });
            }
            return overrides;
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "[Studio] Failed to read IMC file {0}", path);
            return null;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Internal helpers
    // ─────────────────────────────────────────────────────────────────────────

    private Dictionary<EquipSlot, List<GameItem>> GetItemCache()
    {
        var ready = _itemsBySlot;
        if (ready != null) return ready;

        // The cache can be warmed on a worker while the window asks for it; build once.
        lock (_cacheLock)
            return _itemsBySlot ?? BuildItemCache();
    }

    private Dictionary<EquipSlot, List<GameItem>> BuildItemCache()
    {
        var bySlot   = new Dictionary<EquipSlot, List<GameItem>>();
        var byRowId  = new Dictionary<uint, GameItem>();

        try
        {
            var sheet = _data.GetExcelSheet<Item>();
            if (sheet == null) goto done;

            foreach (var row in sheet)
            {
                var name = row.Name.ToString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (row.ModelMain == 0) continue;

                var primaryId = (ushort)(row.ModelMain & 0xFFFF);
                if (primaryId == 0) continue;

                if (!TryGetEquipSlot(row, out var slot, out var isAcc)) continue;

                var variant = (ushort)((row.ModelMain >> 16) & 0xFFFF);

                var item = new GameItem
                {
                    RowId       = row.RowId,
                    Name        = name,
                    ModelId     = primaryId,
                    Variant     = variant,
                    Slot        = slot,
                    IsAccessory = isAcc,
                    Icon        = row.Icon,
                };

                if (!bySlot.TryGetValue(slot, out var list))
                {
                    list = new List<GameItem>();
                    bySlot[slot] = list;
                }
                list.Add(item);
                byRowId[item.RowId] = item;
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[Studio] Failed to build item cache");
        }

        done:
        foreach (var list in bySlot.Values)
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        var bySet = new Dictionary<(bool, ushort), List<(string, EquipSlot)>>();
        foreach (var item in byRowId.Values.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
        {
            var key = (item.IsAccessory, item.ModelId);
            if (!bySet.TryGetValue(key, out var names))
                bySet[key] = names = new List<(string, EquipSlot)>();
            names.Add((item.Name, item.Slot));
        }

        _itemsByRowId = byRowId;
        _itemsBySet   = bySet;
        _cacheCount   = byRowId.Count;
        _itemsBySlot  = bySlot;   // published last: readers treat non-null as "fully built"
        _log.Information("[Studio] Item cache built: {0} equipment/accessory items", _cacheCount);
        return bySlot;
    }

    /// <summary>
    /// Inspects the item's <c>EquipSlotCategory</c> sub-row to determine which
    /// equipment slot it occupies. Returns false for weapons, offhands and anything
    /// outside the ten slots we support.
    /// </summary>
    private static bool TryGetEquipSlot(Item row, out EquipSlot slot, out bool isAcc)
    {
        slot  = EquipSlot.Body;
        isAcc = false;

        try
        {
            var catRowId = row.EquipSlotCategory.RowId;
            if (catRowId == 0) return false;

            var cat = row.EquipSlotCategory.Value;

            // Equipment (prefix 'e')
            if (cat.Head   != 0) { slot = EquipSlot.Head;      return true; }
            if (cat.Body   != 0) { slot = EquipSlot.Body;      return true; }
            if (cat.Gloves != 0) { slot = EquipSlot.Hands;     return true; }
            if (cat.Legs   != 0) { slot = EquipSlot.Legs;      return true; }
            if (cat.Feet   != 0) { slot = EquipSlot.Feet;      return true; }

            // Accessories (prefix 'a')
            if (cat.Ears     != 0) { slot = EquipSlot.Earring;   isAcc = true; return true; }
            if (cat.Neck     != 0) { slot = EquipSlot.Neck;      isAcc = true; return true; }
            if (cat.Wrists   != 0) { slot = EquipSlot.Wrists;    isAcc = true; return true; }
            if (cat.FingerR  != 0) { slot = EquipSlot.RingRight; isAcc = true; return true; }
            if (cat.FingerL  != 0) { slot = EquipSlot.RingLeft;  isAcc = true; return true; }
        }
        catch
        {
            // Sub-row might not resolve for some items — skip silently
        }

        return false;
    }
}
