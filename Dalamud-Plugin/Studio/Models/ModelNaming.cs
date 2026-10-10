using System;
using System.Text.RegularExpressions;
using InstantEdit.Studio.Services.Project;
using InstantEdit.Studio.Services.Resources;

namespace InstantEdit.Studio.Models;

/// <summary>What a model game path is a model of.</summary>
public enum GameModelKind
{
    Equipment,
    Accessory,
    Hair,
    Face,
    Tail,
    Ear,
    Body,
    Weapon,
    Monster,
    Demihuman,
}

/// <summary>
/// A model game path, taken apart. <see cref="Race"/> is the model's race code as Penumbra numbers
/// it (101 for c0101), 0 for models that are not race specific (weapons, monsters, demihumans).
/// </summary>
public sealed record ModelPathInfo(string Path, GameModelKind Kind, ushort Race, ushort PrimaryId, ushort SecondaryId, EquipSlot? Slot)
{
    /// <summary>The model's folder: everything before "/model/".</summary>
    public string Directory => Path[..Path.LastIndexOf("/model/", StringComparison.Ordinal)];

    public bool IsGear => Kind is GameModelKind.Equipment or GameModelKind.Accessory;

    /// <summary>Models whose materials live in the IMC-chosen variant folders (material/vNNNN).</summary>
    public bool UsesImc => Kind is GameModelKind.Equipment or GameModelKind.Accessory or GameModelKind.Weapon
        or GameModelKind.Monster or GameModelKind.Demihuman;

    /// <summary>The item the model belongs to.</summary>
    internal GameRoot Root => ResourceNaming.RootOf(Path);
}

/// <summary>Model (.mdl) file naming and game paths, per race.</summary>
public static class ModelNaming
{
    private static readonly Regex EquipmentModel = new(
        @"^chara/equipment/e(?<set>\d{4})/model/c(?<race>\d{4})e\d{4}_(?<slot>met|top|glv|dwn|sho)\.mdl$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AccessoryModel = new(
        @"^chara/accessory/a(?<set>\d{4})/model/c(?<race>\d{4})a\d{4}_(?<slot>ear|nek|wrs|rir|ril)\.mdl$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex HumanModel = new(
        @"^chara/human/c(?<race>\d{4})/obj/(?<dir>hair|face|tail|zear|body)/[hftzb](?<id>\d{4})/model/c\d{4}[hftzb]\d{4}_\w+\.mdl$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex WeaponModel = new(
        @"^chara/weapon/w(?<id>\d{4})/obj/body/b(?<body>\d{4})/model/w\d{4}b\d{4}\.mdl$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MonsterModel = new(
        @"^chara/monster/m(?<id>\d{4})/obj/body/b(?<body>\d{4})/model/m\d{4}b\d{4}\.mdl$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DemihumanModel = new(
        @"^chara/demihuman/d(?<id>\d{4})/obj/equipment/e(?<eq>\d{4})/model/d\d{4}e\d{4}_(?<slot>met|top|glv|dwn|sho)\.mdl$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Full game path of a race's model, e.g.
    /// "chara/equipment/e0164/model/c0101e0164_top.mdl".
    /// </summary>
    public static string ModelGamePath(EquipSlot slot, ushort modelId, string raceCode)
        => $"{MaterialNaming.ItemFolder(slot, modelId)}/model/c{raceCode}{SlotInfo.ItemPrefix(slot)}{modelId:D4}_{SlotInfo.KeyMap[slot]}.mdl";

    /// <summary>The model a game path names, or null when it is not a model path the game loads.</summary>
    public static ModelPathInfo? Parse(string gamePath)
    {
        var path = PathNormalizer.GamePath(gamePath);
        Match match;
        if ((match = EquipmentModel.Match(path)).Success || (match = AccessoryModel.Match(path)).Success)
        {
            var slot = SlotInfo.ReverseMap[match.Groups["slot"].Value];
            return new ModelPathInfo(path, SlotInfo.IsAccessory(slot) ? GameModelKind.Accessory : GameModelKind.Equipment,
                Id(match, "race"), Id(match, "set"), 0, slot);
        }
        if ((match = HumanModel.Match(path)).Success)
        {
            var kind = match.Groups["dir"].Value switch
            {
                "hair" => GameModelKind.Hair,
                "face" => GameModelKind.Face,
                "tail" => GameModelKind.Tail,
                "zear" => GameModelKind.Ear,
                _      => GameModelKind.Body,
            };
            return new ModelPathInfo(path, kind, Id(match, "race"), Id(match, "id"), 0, null);
        }
        if ((match = WeaponModel.Match(path)).Success)
            return new ModelPathInfo(path, GameModelKind.Weapon, 0, Id(match, "id"), Id(match, "body"), null);
        if ((match = MonsterModel.Match(path)).Success)
            return new ModelPathInfo(path, GameModelKind.Monster, 0, Id(match, "id"), Id(match, "body"), null);
        if ((match = DemihumanModel.Match(path)).Success)
            return new ModelPathInfo(path, GameModelKind.Demihuman, 0, Id(match, "id"), Id(match, "eq"),
                SlotInfo.ReverseMap[match.Groups["slot"].Value]);
        return null;
    }

    /// <summary>The IMC file that picks a model's material sets, or null for models without one (hair, faces, bodies).</summary>
    public static string? ImcPath(ModelPathInfo model) => model.Kind switch
    {
        GameModelKind.Equipment => $"chara/equipment/e{model.PrimaryId:D4}/e{model.PrimaryId:D4}.imc",
        GameModelKind.Accessory => $"chara/accessory/a{model.PrimaryId:D4}/a{model.PrimaryId:D4}.imc",
        GameModelKind.Weapon    => $"chara/weapon/w{model.PrimaryId:D4}/obj/body/b{model.SecondaryId:D4}/b{model.SecondaryId:D4}.imc",
        GameModelKind.Monster   => $"chara/monster/m{model.PrimaryId:D4}/obj/body/b{model.SecondaryId:D4}/b{model.SecondaryId:D4}.imc",
        GameModelKind.Demihuman => $"chara/demihuman/d{model.PrimaryId:D4}/obj/equipment/e{model.SecondaryId:D4}/e{model.SecondaryId:D4}.imc",
        _                       => null,
    };

    /// <summary>The IMC part a model reads: its gear slot's, or the only part of weapons and monsters.</summary>
    public static int ImcPart(ModelPathInfo model) => model.Slot is { } slot ? SlotInfo.ImcPartIndex(slot) : 0;

    /// <summary>The EQDP file of a race (code as Penumbra numbers it), for gear or accessories.</summary>
    public static string EqdpPath(ushort race, bool accessory)
        => $"chara/xls/charadb/{(accessory ? "accessory" : "equipment")}deformerparameter/c{race:D4}.eqdp";

    /// <summary>The game path of a shader package a material names.</summary>
    public static string ShaderPath(string shaderPackage) => $"shader/sm5/shpk/{shaderPackage}";

    private static ushort Id(Match match, string group) => ushort.Parse(match.Groups[group].Value);
}
