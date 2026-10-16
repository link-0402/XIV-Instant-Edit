using System;
using System.Collections.Generic;
using System.Linq;
using InstantEdit.Studio.Services;

namespace InstantEdit.Studio.Models;

/// <summary>What an item of the modpack replaces.</summary>
public enum SubjectKind { Gear, Hair, Face, Tail, Ear, Weapon, Animation }

/// <summary>What a saved item key names, by its top bits (see <see cref="PortSubject.KeyTag"/>).</summary>
public enum KeyTagKind { Gear, OffHand, Emote, Timeline, Feature, Unknown }

/// <summary>
/// The thing a port replaces — a piece of gear, or a character feature — and the answer to
/// every "where does this file go?" question for it. Everything that builds paths asks the
/// subject, so the session, build and validation do not care which kind it is.
///
/// Saved set-ups are keyed by <see cref="Key"/>. Gear keys are the item's row id (as they
/// always were); feature keys set the top bit, see <see cref="FeatureSubject"/>.
/// </summary>
internal abstract class PortSubject
{
    public abstract uint        Key         { get; }
    public abstract SubjectKind Kind        { get; }
    public abstract string      DisplayName { get; }
    public abstract string      IdDisplay   { get; }

    /// <summary>Whether models can be added for several races (gear, hair).</summary>
    public abstract bool MultiRace { get; }

    /// <summary>The race a feature port was opened for; null for gear.</summary>
    public abstract RaceGender? BaseRace { get; }

    public abstract string ModelGamePath(RaceGender rg);

    /// <summary>Game path of a material. <paramref name="race"/> is ignored where materials are race-shared (gear).</summary>
    public abstract string MaterialGamePath(string materialName, RaceGender? race);

    public abstract string TextureGamePath(string textureName);

    public abstract string MaterialFolder(RaceGender? race);
    public abstract string TextureFolder { get; }

    /// <summary>
    /// The name a new material starts with. Gear names follow the gender of the model the material
    /// belongs to, since the game keeps one gear material per gender; a feature uses its own race.
    /// </summary>
    public abstract string DefaultMaterialName(int letter, RaceGender? race = null);

    /// <summary>
    /// The races a copy of each material is written for. Gear yields a single null (one
    /// race-shared set); hair yields every configured race; other features their one race.
    /// </summary>
    public abstract IReadOnlyList<RaceGender?> MaterialRaces(IReadOnlyList<RaceModelEntry> models);

    /// <summary>The material's name in <paramref name="race"/>'s copy (hair rewrites the base race code).</summary>
    public virtual string MaterialNameFor(string name, RaceGender? race) => name;

    /// <summary>Where the vanilla game keeps the material a vanilla model references by this name.</summary>
    public abstract string VanillaMaterialPath(string referencedName, RaceGender race);

    /// <summary>Every place the vanilla material may be, best first; the first that exists is used.</summary>
    public virtual IEnumerable<string> VanillaMaterialPaths(string referencedName, RaceGender race)
    {
        yield return VanillaMaterialPath(referencedName, race);
    }

    /// <summary>Why a model without a vanilla counterpart may never be loaded, for its badge.</summary>
    public virtual string NoVanillaModelHint
        => $"This race does not ship this {KindLabel.ToLowerInvariant()}, so the game may never ask for the file.";

    public abstract bool UsesEqdp { get; }
    public abstract bool UsesImc  { get; }

    /// <summary>Whether models are set up per race (false for weapons, which have one model for everyone).</summary>
    public virtual bool HasRaces => true;

    public virtual string KindLabel => Kind == SubjectKind.Gear ? "Item" : FeatureNaming.KindLabel(Kind);

    /// <summary>
    /// What a saved key names. Feature keys set the top bit; below it, the top byte tags the rest:
    /// 0 for an item row (gear, or a main-hand weapon), 0x40 an off-hand weapon, 0x20 an emote,
    /// 0x21 another animation timeline. Any other tag names nothing this version knows.
    /// </summary>
    public static KeyTagKind KeyTag(uint key)
    {
        if ((key & 0x8000_0000) != 0)
            return KeyTagKind.Feature;
        return (key >> 24) switch
        {
            0x00 => KeyTagKind.Gear,
            0x40 => KeyTagKind.OffHand,
            0x20 => KeyTagKind.Emote,
            0x21 => KeyTagKind.Timeline,
            _    => KeyTagKind.Unknown,
        };
    }

    /// <summary>Recreates a subject from a saved key, or null if it no longer resolves.</summary>
    public static PortSubject? FromKey(uint key, GameDataService gameData)
    {
        if (key == 0)
            return null;
        switch (KeyTag(key))
        {
            case KeyTagKind.Feature:
                return FeatureSubject.TryDecode(key, out var feature) ? feature : null;
            case KeyTagKind.Gear:
                var item = gameData.GetItemByRowId(key);
                if (item != null)
                    return new GearSubject(item);
                return gameData.GetWeapon(key, offHand: false) is { } main ? new WeaponSubject(main) : null;
            case KeyTagKind.OffHand:
                return gameData.GetWeapon(key & ~WeaponSubject.OffHandBit, offHand: true) is { } off ? new WeaponSubject(off) : null;
            case KeyTagKind.Emote or KeyTagKind.Timeline:
                return gameData.GetAnimationTarget(key) is { } target ? new AnimationSubject(target) : null;
            default:
                return null;
        }
    }
}

/// <summary>
/// An emote or another animation timeline. It has no models or materials: its item carries an
/// <see cref="AnimationSetup"/> instead, whose files go to each race's path (see
/// <see cref="Services.Animation.AnimationPathPlanner"/>). The path members below are never asked.
/// </summary>
internal sealed class AnimationSubject : PortSubject
{
    public const uint EmoteTag    = 0x2000_0000;
    public const uint TimelineTag = 0x2100_0000;

    public AnimationSubject(AnimationTarget target) => Target = target;

    public AnimationTarget Target { get; }

    public override uint        Key         => Target.Key;
    public override SubjectKind Kind        => SubjectKind.Animation;
    public override string      DisplayName => Target.Name;
    public override string      IdDisplay   => Target.Command.Length > 0 ? Target.Command : Target.Timelines.FirstOrDefault()?.Key ?? string.Empty;
    public override bool        MultiRace   => false;
    public override RaceGender? BaseRace    => null;
    public override bool        UsesEqdp    => false;
    public override bool        UsesImc     => false;
    public override bool        HasRaces    => false;
    public override string      KindLabel   => "Animation";

    private static readonly IReadOnlyList<RaceGender?> None = Array.Empty<RaceGender?>();

    public override string ModelGamePath(RaceGender rg) => string.Empty;
    public override string MaterialGamePath(string materialName, RaceGender? race) => string.Empty;
    public override string TextureGamePath(string textureName) => string.Empty;
    public override string MaterialFolder(RaceGender? race) => string.Empty;
    public override string TextureFolder => string.Empty;
    public override string DefaultMaterialName(int letter, RaceGender? race = null) => string.Empty;
    public override IReadOnlyList<RaceGender?> MaterialRaces(IReadOnlyList<RaceModelEntry> models) => None;
    public override string VanillaMaterialPath(string referencedName, RaceGender race) => string.Empty;
}

/// <summary>
/// A weapon model: the main or off hand of a weapon item. One model for every race, its materials
/// and textures in the weapon's own folder, and IMC overrides pointing every variant at them.
/// </summary>
internal sealed class WeaponSubject : PortSubject
{
    public const uint OffHandBit = 0x4000_0000;

    public WeaponSubject(WeaponModel model) => Model = model;

    public WeaponModel Model { get; }

    public override uint        Key         => Model.OffHand ? OffHandBit | Model.RowId : Model.RowId;
    public override SubjectKind Kind        => SubjectKind.Weapon;
    public override string      DisplayName => Model.OffHand ? $"{Model.Name} (off hand)" : Model.Name;
    public override string      IdDisplay   => Model.IdDisplay;
    public override bool        MultiRace   => false;
    public override RaceGender? BaseRace    => null;
    public override bool        UsesEqdp    => false;
    public override bool        UsesImc     => true;
    public override bool        HasRaces    => false;
    public override string      KindLabel   => "Weapon";

    public override string ModelGamePath(RaceGender rg) => WeaponNaming.ModelGamePath(Model.Id, Model.Body);

    public override string MaterialGamePath(string materialName, RaceGender? race)
        => WeaponNaming.MaterialGamePath(Model.Id, Model.Body, materialName);

    public override string TextureGamePath(string textureName) => WeaponNaming.TextureGamePath(Model.Id, Model.Body, textureName);

    public override string MaterialFolder(RaceGender? race) => WeaponNaming.MaterialFolder(Model.Id, Model.Body);
    public override string TextureFolder => WeaponNaming.TextureFolder(Model.Id, Model.Body);

    public override string DefaultMaterialName(int letter, RaceGender? race = null)
        => WeaponNaming.DefaultMaterialName(Model.Id, Model.Body, letter);

    private static readonly IReadOnlyList<RaceGender?> Shared = new RaceGender?[] { null };

    public override IReadOnlyList<RaceGender?> MaterialRaces(IReadOnlyList<RaceModelEntry> models) => Shared;

    public override string VanillaMaterialPath(string referencedName, RaceGender race)
        => VanillaMaterialPaths(referencedName, race).First();

    public override IEnumerable<string> VanillaMaterialPaths(string referencedName, RaceGender race)
        => WeaponNaming.VanillaMaterialCandidates(Model.Id, Model.Body, Model.Variant, referencedName.TrimStart('/'));

    public override string NoVanillaModelHint => "The game has no model for this weapon, so it may never ask for the file.";
}

/// <summary>A piece of gear: everything delegates to the existing equipment path helpers.</summary>
internal sealed class GearSubject : PortSubject
{
    public GearSubject(GameDataService.GameItem item) => Item = item;

    public GameDataService.GameItem Item { get; }

    public override uint        Key         => Item.RowId;
    public override SubjectKind Kind        => SubjectKind.Gear;
    public override string      DisplayName => Item.Name;
    public override string      IdDisplay   => Item.ModelIdDisplay;
    public override bool        MultiRace   => true;
    public override RaceGender? BaseRace    => null;
    public override bool        UsesEqdp    => true;
    public override bool        UsesImc     => true;

    public override string ModelGamePath(RaceGender rg)
        => ModelNaming.ModelGamePath(Item.Slot, Item.ModelId, rg.RaceCode);

    public override string MaterialGamePath(string materialName, RaceGender? race)
        => MaterialNaming.MaterialGamePath(Item.Slot, Item.ModelId, materialName);

    public override string TextureGamePath(string textureName)
        => MaterialNaming.TextureGamePath(Item.Slot, Item.ModelId, textureName);

    public override string MaterialFolder(RaceGender? race) => MaterialNaming.MaterialFolder(Item.Slot, Item.ModelId);
    public override string TextureFolder => MaterialNaming.TextureFolder(Item.Slot, Item.ModelId);

    public override string DefaultMaterialName(int letter, RaceGender? race = null)
        => MaterialNaming.DefaultName(Item.Slot, Item.ModelId, letter, (race ?? RaceInfo.BaseFor(PlayerGender.Male)).Gender);

    private static readonly IReadOnlyList<RaceGender?> Shared = new RaceGender?[] { null };

    /// <summary>
    /// One copy per gender the port has a model for: the game looks a gear material up under the
    /// base race of the wearer's gender, so a female model's material is written as c0201 and a
    /// male one's as c0101. With no models configured there is nothing to follow, so the material
    /// keeps the name it was given.
    /// </summary>
    public override IReadOnlyList<RaceGender?> MaterialRaces(IReadOnlyList<RaceModelEntry> models)
        => models.Count == 0
            ? Shared
            : models.Select(m => m.RaceGender.Gender).Distinct().OrderBy(g => g)
                    .Select(g => (RaceGender?)RaceInfo.BaseFor(g)).ToList();

    /// <summary>The material's name in that gender's copy — the base race code swapped in.</summary>
    public override string MaterialNameFor(string name, RaceGender? race)
        => race == null ? name : MaterialNaming.WithRaceCode(name, MaterialNaming.BaseRaceCode(race.Value.Gender));

    public override string VanillaMaterialPath(string referencedName, RaceGender race)
        => MaterialNaming.MaterialGamePath(Item.Slot, Item.ModelId, referencedName.TrimStart('/'));
}

/// <summary>
/// A character feature of one race: hair, face, tail or ears. Its files live in that race's
/// folder. Hair can additionally be ported to more races; each gets its own copy of the
/// materials, named for that race.
///
/// Key layout: <c>0x8000_0000 | kind &lt;&lt; 28 | raceCode &lt;&lt; 16 | id</c> — the top bit keeps
/// it clear of item row ids, so gear set-ups saved before features existed are untouched.
/// </summary>
internal sealed class FeatureSubject : PortSubject
{
    private const uint FeatureBit = 0x8000_0000;

    public FeatureSubject(SubjectKind kind, RaceGender race, ushort id)
    {
        Kind = kind;
        Race = race;
        Id   = id;
    }

    public override SubjectKind Kind { get; }
    public RaceGender Race { get; }
    public ushort     Id   { get; }

    public override uint Key => FeatureBit | ((uint)Kind << 28) | ((uint)int.Parse(Race.RaceCode) << 16) | Id;

    public override string DisplayName => $"{FeatureNaming.KindLabel(Kind)} {Id} · {Race.DisplayName}";
    public override string IdDisplay   => $"{FeatureNaming.Prefix(Kind)}{Id:D4}";
    public override bool   MultiRace   => Kind == SubjectKind.Hair;
    public override RaceGender? BaseRace => Race;
    public override bool   UsesEqdp    => false;
    public override bool   UsesImc     => false;

    public override string ModelGamePath(RaceGender rg) => FeatureNaming.ModelGamePath(Kind, rg.RaceCode, Id);

    /// <summary>
    /// A feature material lives where its name says: "mt_c1501h0005_hir_a" goes to Hrothgar's
    /// hair 5 folder whichever race is asking, since that is how the game resolves it — and the
    /// name carries the race the game shares this feature's materials under, not necessarily the
    /// race wearing them. Names that carry no race/id go to <paramref name="race"/>'s own folder.
    /// </summary>
    public override string MaterialGamePath(string materialName, RaceGender? race)
        => FeatureNaming.ResolveMaterialPath(Kind, materialName, (race ?? Race).RaceCode, Id);

    public override string TextureGamePath(string textureName)
        => $"{TextureFolder}/{MaterialNaming.SanitizeFileName(textureName)}.tex";

    public override string MaterialFolder(RaceGender? race) => FeatureNaming.MaterialFolder(Kind, (race ?? Race).RaceCode, Id);

    /// <summary>Textures are written once, under the base race, and every race's material points at them.</summary>
    public override string TextureFolder => FeatureNaming.TextureFolder(Kind, Race.RaceCode, Id);

    public override string DefaultMaterialName(int letter, RaceGender? race = null)
        => FeatureNaming.DefaultMaterialName(Kind, (race ?? Race).RaceCode, Id, letter);

    public override IReadOnlyList<RaceGender?> MaterialRaces(IReadOnlyList<RaceModelEntry> models)
    {
        if (!MultiRace || models.Count == 0)
            return new RaceGender?[] { Race };
        return models.Select(m => (RaceGender?)m.RaceGender).Distinct().ToList();
    }

    /// <summary>
    /// The material's name in one race's copy: its race code swapped for that race's. Each race a
    /// feature is written for looks its materials up under one race code (often a shared one, see
    /// <see cref="GameDataService.MaterialRaceFor"/>), and the name decides both the file name and,
    /// through <see cref="FeatureNaming.ResolveMaterialPath"/>, the folder it lands in.
    /// </summary>
    public override string MaterialNameFor(string name, RaceGender? race)
        => race == null ? name : MaterialNaming.WithRaceCode(name, race.Value.RaceCode);

    public override string VanillaMaterialPath(string referencedName, RaceGender race)
        => FeatureNaming.ResolveMaterialPath(Kind, referencedName, race.RaceCode, Id);

    public static bool TryDecode(uint key, out FeatureSubject? subject)
    {
        subject = null;
        if ((key & FeatureBit) == 0)
            return false;

        var kind = (SubjectKind)((key >> 28) & 0x7);
        var code = ((key >> 16) & 0xFFF).ToString("D4");
        var id   = (ushort)(key & 0xFFFF);
        if (kind is SubjectKind.Gear or > SubjectKind.Ear)
            return true;   // a feature key we cannot read: treat as "nothing", not as an item

        foreach (var rg in RaceInfo.AllRaces)
        {
            if (rg.RaceCode == code)
            {
                subject = new FeatureSubject(kind, rg, id);
                return true;
            }
        }
        return true;
    }
}
