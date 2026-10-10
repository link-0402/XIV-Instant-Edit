using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using InstantEdit.Studio.Services;
using InstantEdit.Studio.Services.Sims;

namespace InstantEdit.Studio.Models;

/// <summary>A short-lived message for the status bar — the answer to an action the user just took.</summary>
internal sealed record StatusMessage(string Text, Severity Severity, DateTime At);

/// <summary>
/// What the editors both modes share (texture slots) need from the session they edit for: Build
/// mode's modpack (<see cref="PortSession"/>), or a mod open in Edit mode (<see cref="ModSession"/>).
/// </summary>
internal interface ISetUpSession
{
    /// <summary>Records that a set-up was edited in place.</summary>
    void MarkDirty();

    void Notify(string text, Severity severity);
}

/// <summary>
/// Build mode's modpack: every item in it with its race models and materials, plus which node of
/// the tree is selected. Panels read and edit this directly and call <see cref="MarkDirty"/>; the
/// session batches the writes into <see cref="Configuration"/>, so typing into a field does not
/// serialise the whole plugin config on every keystroke. A build turns the pack into a mod (see
/// <see cref="BuildController"/>); the mod knows nothing of the pack afterwards.
///
/// Items stay keyed by <see cref="PortSubject.Key"/> in <c>Configuration.*ByItem</c> (the gear
/// item row id, as always), so saved set-ups load unchanged. Mesh slots are no longer a layer of
/// their own — each material is one slot — and set-ups saved with shared or renamed slots are
/// folded into plain materials on load (see <see cref="MeshSlotFolding"/>).
/// </summary>
internal sealed partial class PortSession : ISetUpSession, IItemShelf
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan StatusLifetime = TimeSpan.FromSeconds(10);

    private readonly StudioHost _host;
    private readonly Stopwatch _sinceDirty = new();
    private bool _dirty;

    public PortSession(StudioHost host)
    {
        _host = host;
        var cfg = host.Configuration;
        ActiveStage    = (StageId)Math.Clamp(cfg.LastStage, 0, (int)StageId.Build);
        ModName        = cfg.ModName;
        ModDescription = cfg.ModDescription;
    }

    // ── Document ─────────────────────────────────────────────────────────────

    /// <summary>Every item in the modpack, in <c>Configuration.ModpackItems</c> order. Empty until <see cref="EnsureLoaded"/>.</summary>
    public List<PortItem> Items { get; } = new();

    /// <summary>False until the game's item list is ready and every modpack item has been read.</summary>
    public bool Loaded { get; private set; }

    public string ModName        { get; set; } = string.Empty;
    public string ModDescription { get; set; } = string.Empty;

    /// <summary>Bumped on every edit; the validator re-runs when it changes.</summary>
    public int Revision { get; private set; }

    /// <summary>Latest validation result, set by the main window's validation tick.</summary>
    public IReadOnlyList<PortIssue> Issues { get; set; } = Array.Empty<PortIssue>();

    // ── Focus ────────────────────────────────────────────────────────────────

    public StageId ActiveStage { get; private set; }

    /// <summary>The tree node the inspector shows.</summary>
    public Target? Selection { get; set; }

    private Target? _reveal;

    public StatusMessage? Status { get; private set; }

    // ─────────────────────────────────────────────────────────────────────────
    // Loading
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads every modpack item once the game's item list is available (gear subjects are
    /// resolved from it). Cheap after the first successful call. Returns whether items are loaded.
    /// </summary>
    public bool EnsureLoaded()
    {
        if (Loaded) return true;
        if (!_host.GameData.IsItemCacheReady) return false;

        var cfg = _host.Configuration;
        var folded = new List<string>();
        bool pruned = false;

        foreach (var key in cfg.ModpackItems.ToList())
        {
            var subject = PortSubject.FromKey(key, _host.GameData);
            if (subject == null)
            {
                cfg.ModpackItems.Remove(key);
                pruned = true;
                continue;
            }
            var item = LoadItem(subject, out var unused);
            Items.Add(item);
            if (unused.Count > 0)
                folded.Add($"{subject.DisplayName}: {string.Join(", ", unused)}");
        }

        Loaded = true;
        if (pruned) cfg.Save();
        SyncVariantGroups();   // set-ups from before groups were the user's get one per slot of variants

        // Restore the last selection, if it is still in the pack.
        var last = cfg.LastItemRowId;
        if (FindItem(last) != null)
            Selection = new Target(TargetKind.Item, last);

        if (folded.Count > 0)
        {
            MarkDirty();   // saves the folded form
            Notify("Mesh slots are now the materials themselves. Materials no slot used were kept and will now be written too — " +
                   string.Join("; ", folded), Severity.Warning);
        }
        Revision++;
        return true;
    }

    private PortItem LoadItem(PortSubject subject, out List<string> unused)
    {
        var cfg = _host.Configuration;
        var key = subject.Key;
        var item = new PortItem(subject)
        {
            Models = cfg.ModelsByItem.TryGetValue(key, out var models)
                ? models.Select(m => m.Clone()).ToList()
                : new List<RaceModelEntry>(),
        };

        var materials = cfg.MaterialsByItem.TryGetValue(key, out var mats)
            ? mats.Select(m => m.Clone()).ToList()
            : new List<MaterialSetup>();
        var slots = cfg.ModelMaterialSlotsByItem.TryGetValue(key, out var s)
            ? s.Select(x => x.Clone()).ToList()
            : new List<ModelMaterialSlot>();
        item.Materials = MeshSlotFolding.Fold(materials, slots, out unused);
        item.Meta = cfg.MetaByItem.TryGetValue(key, out var meta) ? meta.Clone() : new ItemMeta();

        EnsureFixedModel(item);
        SortModels(item);
        return item;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Items
    // ─────────────────────────────────────────────────────────────────────────

    public PortItem? FindItem(uint key) => key == 0 ? null : Items.Find(i => i.Key == key);

    /// <summary>Whether <paramref name="key"/> is one of the items in the current modpack.</summary>
    public bool IsInModpack(uint key) => _host.Configuration.ModpackItems.Contains(key);

    /// <summary>Whether a set-up is saved for <paramref name="key"/>, in the modpack or kept from before it was removed.</summary>
    public bool HasWork(uint key) => FindItem(key) is { } item ? item.HasWork : _host.Configuration.HasWork(key);

    /// <summary>
    /// Adds a subject to the modpack (loading any set-up saved for it earlier), or finds it if it
    /// is already there, and selects and reveals its node.
    /// </summary>
    public PortItem AddItem(PortSubject subject)
    {
        EnsureLoaded();
        var item = FindItem(subject.Key);
        if (item == null)
        {
            item = LoadItem(subject, out var unused);
            Items.Add(item);
            var cfg = _host.Configuration;
            if (!cfg.ModpackItems.Contains(subject.Key))
                cfg.ModpackItems.Add(subject.Key);
            MarkDirty();
            if (unused.Count > 0)
                Notify($"Kept {string.Join(", ", unused)} as materials of their own — mesh slots are now the materials themselves.", Severity.Warning);
        }

        Focus(new Target(TargetKind.Item, subject.Key));
        return item;
    }

    // The game's item browser adds to the modpack.
    string IItemShelf.ShelfName      => "modpack";
    int    IItemShelf.ShelfCount     => Items.Count;
    uint   IItemShelf.ShelfSelection => CurrentItem?.Key ?? 0;
    bool   IItemShelf.OnShelf(uint key) => IsInModpack(key);
    void   IItemShelf.AddToShelf(PortSubject subject) => AddItem(subject);

    /// <summary>Removes an item from the modpack. Its saved models/materials are kept, so adding it back restores them.</summary>
    public void RemoveItem(uint key)
    {
        var item = FindItem(key);
        if (item == null) return;

        Flush();   // keep its latest edits
        Items.Remove(item);
        var cfg = _host.Configuration;
        cfg.ModpackItems.Remove(key);
        cfg.Save();

        if (Selection is { } sel && sel.Item == key)
            Selection = null;
        Revision++;
        Notify($"Removed {item.Subject.DisplayName} from the modpack — its set-up is kept if you add it again.", Severity.Info);
    }

    /// <summary>
    /// Starts a new mod: every item is removed and its saved models and materials are deleted, so
    /// adding one again starts from scratch, and the name, description and id go too — the next
    /// build makes a mod of its own, and the one built before stays in Penumbra as it is.
    /// </summary>
    public void ResetModpack()
    {
        var cfg = _host.Configuration;
        int count = Items.Count;
        foreach (var key in Items.Select(i => i.Key).Concat(cfg.ModpackItems).Distinct().ToList())
        {
            cfg.ModelsByItem.Remove(key);
            cfg.MaterialsByItem.Remove(key);
            cfg.ModelMaterialSlotsByItem.Remove(key);
            cfg.MetaByItem.Remove(key);
        }
        Items.Clear();
        cfg.ModpackItems.Clear();
        cfg.PackGroups.Clear();
        _variantFiles.Clear();
        ModName        = string.Empty;
        ModDescription = string.Empty;
        cfg.ModName        = string.Empty;
        cfg.ModDescription = string.Empty;
        cfg.ModId          = null;
        Selection = null;
        _reveal = null;
        _dirty = false;
        cfg.Save();
        Revision++;
        Notify($"Started a new mod — {count} item(s) removed from the modpack.", Severity.Info);
    }

    /// <summary>
    /// A single-race feature always has exactly one model entry, for its own race; the tree
    /// shows it as a fixed row instead of offering races to add.
    /// </summary>
    private static void EnsureFixedModel(PortItem item)
    {
        if (item.Subject.BaseRace is not { } race)
            return;   // gear belongs to no race: every model is added by hand

        // A character feature was picked for one race in the browser, so that race always has a
        // model row. Hair may add more races on top; the others belong to their race alone.
        if (!item.Subject.MultiRace)
            item.Models.RemoveAll(m => m.RaceGender != race);
        if (!item.Models.Any(m => m.RaceGender == race))
            item.Models.Insert(0, new RaceModelEntry { Race = race.Race, Gender = race.Gender });
    }

    /// <summary>
    /// The races a model may still be added for: gear can claim any race (with an Eqdp override),
    /// while a feature is limited to races the game actually has this id for - the others could
    /// never equip it.
    /// </summary>
    public List<RaceGender> AddableRaces(PortItem item)
    {
        if (!item.Subject.MultiRace)
            return new List<RaceGender>();

        var configured = item.Models.Select(m => m.RaceGender).ToHashSet();
        IEnumerable<RaceGender> candidates = item.Subject is GearSubject
            ? RaceInfo.AllRaces
            : _host.GameData.GetNativeRaces(item.Subject);
        return candidates.Where(rg => !configured.Contains(rg))
            .OrderBy(rg => rg.RaceCode, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The skeleton this hair uses on a race: the entry the user set, else the one the game ships
    /// for this id. Null when neither exists (the hair has no extra bones).
    /// </summary>
    public ushort? EffectiveHairSkeleton(PortItem item, RaceGender race)
        => EffectiveHairSkeleton(item, race, _host.GameData);

    /// <summary>
    /// The skeleton this race's hair loads: what the user set, else the game's own entry for that
    /// race, else the entry of the race the port was opened for — a race the game has no entry for
    /// still needs one, and the port's own skeleton is the one its model fits. Null when there is
    /// nothing sensible to write, which is when the fallback skeleton does not exist for the race.
    /// </summary>
    public static ushort? EffectiveHairSkeleton(PortItem item, RaceGender race, GameDataService gameData)
    {
        if (item.Subject is not FeatureSubject { Kind: SubjectKind.Hair } hair)
            return null;

        if (item.Meta.EstByRace.TryGetValue(race.RaceCode, out var set))
            return set == 0 ? null : set;

        if (gameData.VanillaHairSkeleton(race, hair.Id) is { } vanilla and not 0)
            return vanilla;

        if (race != hair.Race
            && gameData.VanillaHairSkeleton(hair.Race, hair.Id) is { } baseline and not 0
            && gameData.HasHairSkeleton(race, baseline))
            return baseline;

        return null;
    }

    /// <summary>
    /// Which vanilla model a generated dummy takes its skeleton from. Normally the port's own id,
    /// but a hair pointed at another skeleton borrows the bones of the vanilla hair that uses it,
    /// so the generated model already fits the skeleton the mod asks the game to load.
    /// </summary>
    public (string GamePath, ushort? FromHairId) DummyBase(PortItem item, RaceGender race)
    {
        var own = item.Subject.ModelGamePath(race);
        if (item.Subject is not FeatureSubject { Kind: SubjectKind.Hair } hair)
            return (own, null);

        if (EffectiveHairSkeleton(item, race) is not { } skeleton)
            return (own, null);

        var source = _host.GameData.HairUsingSkeleton(race, skeleton);
        if (source == null || source == hair.Id)
            return (own, null);

        var path = FeatureNaming.ModelGamePath(SubjectKind.Hair, race.RaceCode, source.Value);
        return _host.GameData.Exists(path) ? (path, source) : (own, null);
    }

    /// <summary>The race a feature port was opened for, which cannot be removed: it is what the port replaces.</summary>
    public static bool IsBaseRace(PortItem item, RaceGender race) => item.Subject.BaseRace == race;

    /// <summary>
    /// Replaces an item's materials with the vanilla model's own — their names, shaders and
    /// texture roles — so the port's model and materials line up with what the game expects.
    /// </summary>
    public void MatchVanillaMaterials(PortItem item)
    {
        var subject = item.Subject;
        var race = _host.GameData.ReferenceRace(subject);
        var layout = race != null ? _host.GameData.ReadVanillaMaterialLayout(subject, race.Value) : null;
        if (layout == null || layout.Count == 0)
        {
            Notify($"No vanilla model found for {subject.DisplayName}.", Severity.Warning);
            return;
        }

        ApplyTextureDefaults(layout.SelectMany(m => m.Textures));
        item.Materials.Clear();
        item.Materials.AddRange(layout);
        Focus(new Target(TargetKind.Material, item.Key, 0));
        MarkDirty();
        Notify($"Matched {layout.Count} vanilla material(s): {string.Join(", ", layout.Select(m => m.Name))}.", Severity.Info);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Stage / focus
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Switches the visible stage. Remembered across sessions with the next save.</summary>
    public void GoToStage(StageId stage)
    {
        ActiveStage = stage;
        _host.Configuration.LastStage = (int)stage;
    }

    /// <summary>Selects <paramref name="target"/>, opens its stage, and asks the tree to open its parents and scroll to it.</summary>
    public void Focus(Target target)
    {
        if (target.Kind != TargetKind.Build)
        {
            Selection = target;
            if (target.Item != 0 && _host.Configuration.LastItemRowId != target.Item)
                _host.Configuration.LastItemRowId = target.Item;   // any subject key; saved with the next flush
        }
        GoToStage(target.Stage);
        _reveal = target;
    }

    /// <summary>Whether a pending reveal lies under <paramref name="node"/>, so the tree should open it.</summary>
    public bool WantsOpen(Target node) => _reveal is { } r && r != node && r.IsWithin(node);

    /// <summary>True once for the row that <see cref="Focus"/> asked to bring into view.</summary>
    public bool TakeScrollRequest(Target row)
    {
        if (_reveal != row) return false;
        _reveal = null;
        return true;
    }

    public bool IsSelected(Target t) => Selection == t;

    /// <summary>The item the selection lies in.</summary>
    public PortItem? CurrentItem => Selection is { } s ? FindItem(s.Item) : null;

    /// <summary>The selected material, or the material of the selected texture.</summary>
    public MaterialSetup? CurrentMaterial
    {
        get
        {
            if (Selection is not { Kind: TargetKind.Material or TargetKind.Texture } s) return null;
            var item = FindItem(s.Item);
            return item != null && s.Index >= 0 && s.Index < item.Materials.Count ? item.Materials[s.Index] : null;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Status messages
    // ─────────────────────────────────────────────────────────────────────────

    public void Notify(string text, Severity severity) => Status = new StatusMessage(text, severity, DateTime.UtcNow);

    /// <summary>The current status message, or null once it has aged out.</summary>
    public StatusMessage? LiveStatus
        => Status != null && DateTime.UtcNow - Status.At < StatusLifetime ? Status : null;

    // ─────────────────────────────────────────────────────────────────────────
    // Models
    // ─────────────────────────────────────────────────────────────────────────

    public void AddModel(PortItem item, RaceGender rg)
    {
        if (!AddableRaces(item).Contains(rg)) return;
        var entry = new RaceModelEntry { Race = rg.Race, Gender = rg.Gender };
        item.Models.Add(entry);
        SortModels(item);
        Focus(new Target(TargetKind.Model, item.Key, item.Models.IndexOf(entry)));
        MarkDirty();
    }

    public void RemoveModel(PortItem item, int index)
    {
        if (index < 0 || index >= item.Models.Count || !item.Subject.MultiRace) return;
        if (IsBaseRace(item, item.Models[index].RaceGender)) return;   // the race the port was opened for
        item.Models.RemoveAt(index);
        Selection = new Target(TargetKind.Item, item.Key);
        MarkDirty();
    }

    /// <summary>
    /// Writes a copy of the model the build would generate for this race — the vanilla
    /// skeleton with one empty mesh part per material — to a temp folder, so it can be
    /// opened and modelled in Blender via InstantEdit. The entry itself is left alone.
    /// </summary>
    public void ExportDummyModel(PortItem item, RaceModelEntry entry)
    {
        var subject = item.Subject;
        if (item.Materials.Count == 0)
        {
            Notify("Add at least one material first — each becomes an empty mesh part in the dummy model.", Severity.Warning);
            return;
        }

        var (gamePath, fromHair) = DummyBase(item, entry.RaceGender);
        var vanilla = _host.GameData.GetVanillaModel(gamePath, out var readError);
        if (vanilla == null)
        {
            Notify($"Could not read vanilla model at {gamePath}.{(readError != null ? $" ({readError})" : "")}", Severity.Error);
            return;
        }

        var materialNames = item.Materials
            .Select(m => $"{MaterialNaming.SanitizeFileName(subject.MaterialNameFor(m.Name, entry.RaceGender))}.mtrl")
            .ToList();

        byte[] bytes;
        try
        {
            bytes = DummyModelBuilder.Build(vanilla, materialNames);
        }
        catch (Exception ex)
        {
            StudioServices.Log.Warning(ex, "[Studio] Failed to build dummy model for {0}", gamePath);
            Notify($"Failed to build dummy model: {ex.Message}", Severity.Error);
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), "InstantEdit.Studio", "vanilla-models");
        Directory.CreateDirectory(dir);
        var fileName = subject is GearSubject gear
            ? $"{SanitizeFolderName(gear.Item.Name)}_{entry.RaceGender.RaceCode}_{SlotInfo.KeyMap[gear.Item.Slot]}.mdl"
            : Path.GetFileName(gamePath);
        var path = Path.Combine(dir, fileName);
        File.WriteAllBytes(path, bytes);

        ImGuiSetClipboard(path);
        var borrowed = fromHair != null ? $", on hair {fromHair}'s skeleton" : string.Empty;
        Notify($"Dummy model for {entry.RaceGender.DisplayName} written to {path} " +
               $"({materialNames.Count} empty mesh part(s){borrowed}); the path is on your clipboard.", Severity.Info);
    }

    /// <summary>Copies text to the clipboard through ImGui, which owns it while the game runs.</summary>
    private static void ImGuiSetClipboard(string text) => Dalamud.Bindings.ImGui.ImGui.SetClipboardText(text);

    /// <summary>Keeps the model list ordered by race code (0101, 0201, 0301, …) rather than insertion order.</summary>
    private static void SortModels(PortItem item)
        => item.Models.Sort((a, b) => string.CompareOrdinal(a.RaceGender.RaceCode, b.RaceGender.RaceCode));

    // ─────────────────────────────────────────────────────────────────────────
    // Materials
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Adds a material from a preset, or an empty one (shader only, no textures) when <paramref name="preset"/> is null.</summary>
    public void AddMaterial(PortItem item, MaterialPreset? preset, ShaderType emptyShader = ShaderType.Character)
    {
        string name = item.Subject.DefaultMaterialName(item.Materials.Count + 1, NamingRace(item));
        var material = preset != null
            ? MaterialSetup.FromPreset(preset, name)
            : new MaterialSetup { Name = name, ShaderType = emptyShader };
        ApplyTextureDefaults(material.Textures);
        item.Materials.Add(material);
        Focus(new Target(TargetKind.Material, item.Key, item.Materials.Count - 1));
        MarkDirty();
    }

    /// <summary>Re-applies a preset to a material, replacing its shader and textures.</summary>
    public void ApplyPreset(MaterialSetup mat, MaterialPreset preset)
    {
        mat.ShaderType = preset.Shader;
        mat.Textures.Clear();
        foreach (var type in preset.Textures)
            mat.Textures.Add(new TextureSlot { Type = type, Postfix = MaterialNaming.DefaultPostfix(type) });
        ApplyTextureDefaults(mat.Textures);
        MarkDirty();
    }

    // ── Material clipboard ───────────────────────────────────────────────────

    /// <summary>The material last copied, as it was at the time, and the subject it was copied from.</summary>
    public MaterialSetup? CopiedMaterial { get; private set; }
    public PortSubject?   CopiedFrom     { get; private set; }

    public void CopyMaterial(PortItem item, int index)
    {
        if (index < 0 || index >= item.Materials.Count) return;
        CopiedMaterial = item.Materials[index].Clone();
        CopiedFrom     = item.Subject;
        Notify($"Copied \"{CopiedMaterial.Name}\" — paste it onto any model from its right-click menu or + Material.", Severity.Info);
    }

    /// <summary>
    /// Pastes the copied material onto <paramref name="item"/>, after <paramref name="afterIndex"/>
    /// (or at the end). With <paramref name="keepTexturePaths"/>, every texture points at the game
    /// path the original writes, so both models share one set of texture files; otherwise the
    /// textures are written again under this model's own folder, from the same source files.
    /// The name is renamed to this model (mt_c0201h0005_… → mt_c0801h0005_…) and kept unique.
    /// </summary>
    public void PasteMaterial(PortItem item, bool keepTexturePaths, int afterIndex = -1)
    {
        if (CopiedMaterial is not { } source || CopiedFrom is not { } from) return;

        var copy = source.Clone();
        copy.Name = UniqueMaterialName(item, RetargetMaterialName(source.Name, from, item.Subject));
        foreach (var tex in copy.Textures)
        {
            tex.SharedGamePath = !keepTexturePaths ? string.Empty
                : tex.IsShared ? tex.SharedGamePath   // already shared: keep pointing at the original
                : from.TextureGamePath(MaterialNaming.ComposeTextureName(source.Name, tex.Postfix));
        }

        int at = afterIndex >= 0 && afterIndex < item.Materials.Count ? afterIndex + 1 : item.Materials.Count;
        item.Materials.Insert(at, copy);
        Focus(new Target(TargetKind.Material, item.Key, at));
        MarkDirty();
        Notify(keepTexturePaths
            ? $"Pasted \"{copy.Name}\" onto {item.Subject.DisplayName}, sharing {from.DisplayName}'s textures."
            : $"Pasted \"{copy.Name}\" onto {item.Subject.DisplayName} with its own textures.", Severity.Info);
    }

    /// <summary>
    /// The race a new material's name follows: the one the game looks the item's materials up under,
    /// which for gear follows the gender of its models and for a feature is whichever race that
    /// feature's materials are shared under (see <see cref="GameDataService.MaterialRaceFor"/>).
    /// </summary>
    public RaceGender? NamingRace(PortItem item)
    {
        var race = item.Subject.BaseRace ?? (item.Models.Count > 0 ? item.Models[0].RaceGender : (RaceGender?)null);
        return race is { } rg ? _host.GameData.MaterialRaceFor(item.Subject, rg) : null;
    }

    /// <summary>Swaps the source subject's naming stem for the target's, when the name follows it.</summary>
    private static string RetargetMaterialName(string name, PortSubject from, PortSubject to)
    {
        static string Stem(PortSubject s, RaceGender? race)
        {
            var n = s.DefaultMaterialName(1, race);   // "mt_c0201h0005_hir_a"
            int cut = n.LastIndexOf('_');
            return cut > 0 ? n[..cut] : n;            // "mt_c0201h0005_hir"
        }

        // Gear is named per gender, so a copied name can follow either of the two stems.
        foreach (var race in new RaceGender?[] { null, RaceInfo.BaseFor(PlayerGender.Female) })
        {
            var fromStem = Stem(from, race);
            if (name.StartsWith(fromStem, StringComparison.OrdinalIgnoreCase))
                return Stem(to, race) + name[fromStem.Length..];
        }
        return name;
    }

    /// <summary>Makes a name unique within the item, by bumping a trailing "_a" letter (or adding one).</summary>
    private static string UniqueMaterialName(PortItem item, string name)
    {
        bool Taken(string n) => item.Materials.Any(m => string.Equals(m.Name, n, StringComparison.OrdinalIgnoreCase));
        if (!Taken(name)) return name;

        bool lettered = name.Length > 2 && name[^2] == '_' && char.IsAsciiLetterLower(name[^1]);
        var stem = lettered ? name[..^2] : name;
        for (char c = lettered ? (char)(name[^1] + 1) : 'b'; c <= 'z'; c++)
            if (!Taken($"{stem}_{c}"))
                return $"{stem}_{c}";
        for (int i = 2; ; i++)
            if (!Taken($"{name}_{i}"))
                return $"{name}_{i}";
    }

    public void RemoveMaterial(PortItem item, int index)
    {
        if (index < 0 || index >= item.Materials.Count) return;
        item.Materials.RemoveAt(index);
        Selection = item.Materials.Count > 0
            ? new Target(TargetKind.Material, item.Key, Math.Min(index, item.Materials.Count - 1))
            : new Target(TargetKind.Item, item.Key);
        MarkDirty();
    }

    /// <summary>Moves a material one place up or down — its position is the mesh slot it fills.</summary>
    public void MoveMaterial(PortItem item, int index, int delta)
    {
        int to = index + delta;
        if (index < 0 || index >= item.Materials.Count || to < 0 || to >= item.Materials.Count) return;
        (item.Materials[index], item.Materials[to]) = (item.Materials[to], item.Materials[index]);
        Selection = new Target(TargetKind.Material, item.Key, to);
        MarkDirty();
    }

    public void AddTexture(PortItem item, int materialIndex, TextureType type)
    {
        if (materialIndex < 0 || materialIndex >= item.Materials.Count) return;
        var mat = item.Materials[materialIndex];
        mat.Textures.Add(new TextureSlot
        {
            Type        = type,
            Postfix     = MaterialNaming.DefaultPostfix(type),
            Compression = _host.Configuration.DefaultCompression,
        });
        Focus(new Target(TargetKind.Texture, item.Key, materialIndex, mat.Textures.Count - 1));
        MarkDirty();
    }

    public void RemoveTexture(PortItem item, int materialIndex, int index)
    {
        if (materialIndex < 0 || materialIndex >= item.Materials.Count) return;
        var mat = item.Materials[materialIndex];
        if (index < 0 || index >= mat.Textures.Count) return;
        mat.Textures.RemoveAt(index);
        Selection = new Target(TargetKind.Material, item.Key, materialIndex);
        MarkDirty();
    }

    private void ApplyTextureDefaults(IEnumerable<TextureSlot> slots)
    {
        var compression = _host.Configuration.DefaultCompression;
        if (compression == TextureCompression.None) return;
        foreach (var slot in slots)
            slot.Compression = compression;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Sims 4 importer hand-off
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Points the selected material at the files the Sims 4 importer just extracted: the
    /// diffuse folder becomes a variant slot (so every colour swatch in it turns into one
    /// option of a switch group when the mod is created), and the normal and specular
    /// images fill their slots as single textures. Slots that do not exist yet are added.
    /// Returns a message for the importer window to show.
    /// </summary>
    public string ApplySimsExtraction(SimsPackageInspector.ExtractionResult extraction)
    {
        var mat = CurrentMaterial;
        if (mat == null)
            return "Select a material in the main window first.";

        var applied = new List<string>();

        if (extraction.DiffuseCount > 0 && Directory.Exists(extraction.DiffuseFolder))
        {
            var slot = GetOrAddTextureSlot(mat, TextureType.Diffuse);
            slot.SourcePath    = extraction.DiffuseFolder;
            slot.UseVariants   = true;
            slot.UseWhiteDummy = false;
            applied.Add($"diffuse ({extraction.DiffuseCount} variant(s))");
        }

        if (extraction.NormalFile != null && File.Exists(extraction.NormalFile))
        {
            var slot = GetOrAddTextureSlot(mat, TextureType.Normal);
            slot.SourcePath    = extraction.NormalFile;
            slot.UseVariants   = false;
            slot.UseWhiteDummy = false;
            applied.Add("normal");
        }

        if (extraction.SpecularFile != null && File.Exists(extraction.SpecularFile))
        {
            var slot = GetOrAddTextureSlot(mat, TextureType.Specular);
            slot.SourcePath    = extraction.SpecularFile;
            slot.UseVariants   = false;
            slot.UseWhiteDummy = false;
            applied.Add("specular");
        }

        if (applied.Count == 0)
            return "Nothing to apply — the extraction produced no diffuse, normal or specular textures.";

        MarkDirty();
        var message = $"Applied {string.Join(", ", applied)} to material \"{mat.Name}\".";
        Notify(message, Severity.Info);
        return message;
    }

    /// <summary>Finds the material's slot for a texture role, adding one when it has none.</summary>
    private static TextureSlot GetOrAddTextureSlot(MaterialSetup mat, TextureType type)
    {
        var existing = mat.Textures.FirstOrDefault(t => t.Type == type);
        if (existing != null)
            return existing;

        var slot = new TextureSlot { Type = type, Postfix = MaterialNaming.DefaultPostfix(type) };
        mat.Textures.Add(slot);
        return slot;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Build hand-off
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A deep copy of everything the build needs for every item in the modpack, taken on the
    /// render thread so the worker never reads lists the UI is editing.
    /// </summary>
    public BuildRequest CreateBuildRequest(string modRoot)
    {
        EnsureLoaded();
        SyncVariantGroups();
        var cfg = _host.Configuration;
        var gameData = _host.GameData;

        var items = new List<ItemBuildRequest>();
        foreach (var source in Items)
        {
            var subject   = source.Subject;
            var models    = source.Models.Select(x => x.Clone()).ToList();
            var materials = source.Materials.Select(x => x.Clone()).ToList();

            // Where each file goes, worked out here: the build only reads the place each one says.
            for (int i = 0; i < models.Count; i++)
                models[i].Option = PlaceOf(source, source.Models[i]);
            for (int m = 0; m < materials.Count; m++)
            {
                materials[m].Option = PlaceOf(source, source.Materials[m]);
                for (int t = 0; t < materials[m].Textures.Count; t++)
                    materials[m].Textures[t].Option = PlaceOf(source, source.Materials[m], source.Materials[m].Textures[t]);
            }

            // Which races each material is written for, and so which names exist: gear follows the
            // gender of its models, hair every configured race (see GameDataService.MaterialRaces).
            var materialRaces = gameData.MaterialRaces(subject, models);

            // Vanilla materials to clone, only for materials whose shader has no bundled preset —
            // looked up here, on the render thread, per material and per race copy.
            var templates = new Dictionary<string, (string GamePath, MtrlInfo Material)?>(StringComparer.OrdinalIgnoreCase);
            foreach (var mat in materials)
            {
                if (MaterialPresetLibrary.FindPresetPath(mat.ShaderType, mat.Textures.Select(t => t.Type).ToHashSet()) != null)
                    continue;

                foreach (var race in materialRaces)
                {
                    var tkey = BuildRequest.TemplateKey(mat.Name, race);
                    if (!templates.ContainsKey(tkey))
                        templates[tkey] = gameData.FindVanillaMaterial(subject, mat.Name, race);
                }
            }

            List<ModImcOverride>? imc = null;
            if (subject is GearSubject gear && materials.Count > 0)
                imc = gameData.GetImcOverridesForcingV1(gear.Item.Slot, gear.Item.ModelId);

            // Metadata the user set on this item, as manipulations. Both are overrides of what
            // the game ships, so only what differs is carried into the build.
            ModEqpOverride? eqp = null;
            if (subject is GearSubject eqpGear && source.Meta.EqpEntry is { } entry
                && entry != gameData.VanillaEqp(eqpGear.Item.ModelId))
                eqp = new ModEqpOverride { Slot = eqpGear.Item.Slot, SetId = eqpGear.Item.ModelId, Entry = entry };

            // Every race of a hair port carries its skeleton entry, whether or not it differs from
            // the game's: the mod then says outright which skeleton its hair loads on that race,
            // rather than depending on an entry the game may not have.
            List<ModEstOverride>? est = null;
            if (subject is FeatureSubject { Kind: SubjectKind.Hair } hair)
            {
                foreach (var race in models.Select(m => m.RaceGender).Distinct())
                {
                    if (EffectiveHairSkeleton(source, race) is not { } skeleton)
                        continue;
                    (est ??= new List<ModEstOverride>()).Add(new ModEstOverride
                    {
                        RaceGender = race,
                        SetId      = hair.Id,
                        SkeletonId = skeleton,
                    });
                }
            }

            // A race set to "dummy" builds its model from the game's own, so the vanilla model is
            // read here, on the render thread.
            var dummyBases = new Dictionary<string, VanillaModelInfo>();
            foreach (var model in models.Where(m => m.UseDummy))
            {
                var vanilla = gameData.GetVanillaModel(DummyBase(source, model.RaceGender).GamePath, out _);
                if (vanilla != null)
                    dummyBases[model.RaceGender.RaceCode] = vanilla;
            }

            // Which race each model's materials resolve to, so the models this build writes point at
            // the .mtrl the game will actually ask them for.
            var materialRaceByModel = new Dictionary<string, RaceGender>();
            foreach (var race in models.Select(m => m.RaceGender).Distinct())
                materialRaceByModel[race.RaceCode] = gameData.MaterialRaceFor(subject, race);

            // What the game already grants each race for this item, so the build only adds what is
            // missing: every configured race, plus the base races its materials are named after.
            var vanillaEqdp = new Dictionary<string, ushort>();
            if (subject is GearSubject eqdpGear)
            {
                bool accessory = SlotInfo.IsAccessory(eqdpGear.Item.Slot);
                var races = models.Select(m => m.RaceGender)
                    .Concat(materialRaces.OfType<RaceGender>())
                    .Distinct();
                foreach (var race in races)
                    vanillaEqdp[race.RaceCode] = gameData.VanillaEqdp(race, accessory, eqdpGear.Item.ModelId);
            }

            items.Add(new ItemBuildRequest
            {
                Option             = PlaceOf(source),
                Subject            = subject,
                Models             = models,
                Materials          = materials,
                ModelMaterialSlots = source.IdentitySlots(),
                NativeRaces        = gameData.GetNativeRaces(subject),
                MaterialRaces      = materialRaces,
                MaterialRaceByModel = materialRaceByModel,
                VanillaEqdp        = vanillaEqdp,
                VanillaTemplates   = templates,
                ImcOverrides       = imc,
                EqpOverride        = eqp,
                EstOverrides       = est,
                DummyBases         = dummyBases,
            });
        }

        return new BuildRequest
        {
            ModRoot         = modRoot,
            ModName         = SanitizeFolderName(string.IsNullOrWhiteSpace(ModName) ? DefaultModName() : ModName.Trim()),
            PluginDirectory = _host.PluginDirectory,
            CacheDirectory  = _host.TextureCacheDirectory,
            ConvertTexture  = _host.PenumbraTextures.Available ? _host.PenumbraTextures.Convert : null,
            Meta = new ModMetaInfo
            {
                Identifier  = GetOrCreateModId(),
                Author      = cfg.ModAuthor,
                Description = ModDescription,
                Version     = cfg.ModVersion,
                Website     = cfg.ModWebsite,
            },
            Items  = items,
            Groups = Groups.Select(g => g.Clone()).ToList(),
        };
    }

    private Guid GetOrCreateModId()
    {
        var cfg = _host.Configuration;
        if (cfg.ModId is not { } id)
        {
            id = Guid.NewGuid();
            cfg.ModId = id;
            cfg.Save();
        }
        return id;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Persistence
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Records that the document changed.</summary>
    public void MarkDirty()
    {
        Revision++;
        _dirty = true;
        _sinceDirty.Restart();
    }

    /// <summary>Called once per frame: writes pending edits once they have settled.</summary>
    public void FlushIfDue()
    {
        if (_dirty && _sinceDirty.Elapsed >= SaveDelay)
            Flush();
    }

    /// <summary>Writes pending edits now — before a build, on window close and plugin unload.</summary>
    public void Flush()
    {
        if (!_dirty) return;
        _dirty = false;

        var cfg = _host.Configuration;
        foreach (var item in Items)
        {
            cfg.ModelsByItem[item.Key]             = item.Models.Select(m => m.Clone()).ToList();
            cfg.MaterialsByItem[item.Key]          = item.Materials.Select(m => m.Clone()).ToList();
            cfg.ModelMaterialSlotsByItem[item.Key] = item.IdentitySlots();
            if (item.Meta.IsEmpty) cfg.MetaByItem.Remove(item.Key);
            else                   cfg.MetaByItem[item.Key] = item.Meta.Clone();
        }
        cfg.ModName        = ModName;
        cfg.ModDescription = ModDescription;
        cfg.Save();
    }

    /// <summary>Auto-generated mod name used until the user overrides it, based on what's in the modpack.</summary>
    public string DefaultModName() => Items.Count switch
    {
        0 => "XIV Instant Edit - Mod",
        1 => SubjectModName(Items[0].Subject),
        _ => $"XIV Instant Edit - Modpack ({Items.Count} items)",
    };

    private static string SubjectModName(PortSubject subject) => subject switch
    {
        GearSubject gear       => $"XIV Instant Edit - {SanitizeFolderName(gear.Item.Name)}",
        FeatureSubject feature => $"XIV Instant Edit - {FeatureNaming.KindLabel(feature.Kind)} {feature.Id} ({SanitizeFolderName(feature.Race.DisplayName)})",
        _                      => "XIV Instant Edit - Mod",
    };

    /// <summary>Replaces path-invalid characters so item names can be used as folder names.</summary>
    public static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars).Trim();
    }
}
