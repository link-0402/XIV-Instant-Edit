using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace InstantEdit.Services.GameFiles;

/// <summary>The kinds of vanilla model the Game Files browser lists.</summary>
internal enum GameFileCategory { Hair, Face, Tail, Ears, Body, Equipment, Accessory, Weapon }

/// <summary>
/// A vanilla model file by the IDs its game path is built from. <paramref name="Race"/> is the
/// model's race code (0 for weapons), <paramref name="Id"/> the part, set or weapon ID,
/// <paramref name="SecondaryId"/> a weapon's body (0 otherwise) and <paramref name="Slot"/> the
/// file's suffix (<c>hir</c>, <c>top</c>, <c>ear</c>, …; empty for weapons).
/// </summary>
internal sealed record GameModelId(GameFileCategory Category, ushort Race, ushort Id, ushort SecondaryId, string Slot)
{
    public bool Human => Category is not GameFileCategory.Weapon;

    /// <summary>The ID as game paths spell it: <c>h0113</c>, <c>e6016</c>, <c>w0101b0001</c>.</summary>
    public string IdLabel => Category == GameFileCategory.Weapon
        ? $"w{Id:D4}b{SecondaryId:D4}"
        : $"{GameModelPaths.Letter(Category)}{Id:D4}";
}

internal static partial class GameModelPaths
{
    public static readonly ImmutableArray<string> EquipmentSlots = ["met", "top", "glv", "dwn", "sho"];
    public static readonly ImmutableArray<string> AccessorySlots = ["ear", "nek", "wrs", "rir", "ril"];

    /// <summary>The suffixes body models use; the game has no body headgear.</summary>
    public static readonly ImmutableArray<string> BodySlots = ["top", "glv", "dwn", "sho"];

    public static readonly ImmutableArray<GameFileCategory> Categories = [.. Enum.GetValues<GameFileCategory>()];

    [GeneratedRegex(@"^chara/human/c(?<race>\d{4})/obj/(?<folder>hair|face|tail|zear|body)/(?<letter>[hftzb])(?<id>\d{4})/model/c\k<race>\k<letter>\k<id>_(?<slot>[a-z]{3})\.mdl$",
        RegexOptions.CultureInvariant)]
    private static partial Regex HumanModel();

    [GeneratedRegex(@"^chara/(?<folder>equipment|accessory)/(?<letter>[ea])(?<id>\d{4})/model/c(?<race>\d{4})\k<letter>\k<id>_(?<slot>[a-z]{3})\.mdl$",
        RegexOptions.CultureInvariant)]
    private static partial Regex GearModel();

    [GeneratedRegex(@"^chara/weapon/w(?<id>\d{4})/obj/body/b(?<body>\d{4})/model/w\k<id>b\k<body>\.mdl$",
        RegexOptions.CultureInvariant)]
    private static partial Regex WeaponModel();

    public static char Letter(GameFileCategory category) => category switch
    {
        GameFileCategory.Hair => 'h',
        GameFileCategory.Face => 'f',
        GameFileCategory.Tail => 't',
        GameFileCategory.Ears => 'z',
        GameFileCategory.Body => 'b',
        GameFileCategory.Equipment => 'e',
        GameFileCategory.Accessory => 'a',
        _ => 'w',
    };

    private static string HumanFolder(GameFileCategory category) => category switch
    {
        GameFileCategory.Hair => "hair",
        GameFileCategory.Face => "face",
        GameFileCategory.Tail => "tail",
        GameFileCategory.Ears => "zear",
        _ => "body",
    };

    /// <summary>The one suffix of a character part's model files; body models have four.</summary>
    public static string? PartSlot(GameFileCategory category) => category switch
    {
        GameFileCategory.Hair => "hir",
        GameFileCategory.Face => "fac",
        GameFileCategory.Tail => "til",
        GameFileCategory.Ears => "zer",
        _ => null,
    };

    /// <summary>The slots a category's model files can have.</summary>
    public static ImmutableArray<string> Slots(GameFileCategory category) => category switch
    {
        GameFileCategory.Body => BodySlots,
        GameFileCategory.Equipment => EquipmentSlots,
        GameFileCategory.Accessory => AccessorySlots,
        GameFileCategory.Weapon => [""],
        _ => [PartSlot(category)!],
    };

    public static string ModelPath(GameModelId id)
    {
        var race = $"c{id.Race:D4}";
        return id.Category switch
        {
            GameFileCategory.Equipment => $"chara/equipment/e{id.Id:D4}/model/{race}e{id.Id:D4}_{id.Slot}.mdl",
            GameFileCategory.Accessory => $"chara/accessory/a{id.Id:D4}/model/{race}a{id.Id:D4}_{id.Slot}.mdl",
            GameFileCategory.Weapon => $"chara/weapon/w{id.Id:D4}/obj/body/b{id.SecondaryId:D4}/model/w{id.Id:D4}b{id.SecondaryId:D4}.mdl",
            _ => $"chara/human/{race}/obj/{HumanFolder(id.Category)}/{Letter(id.Category)}{id.Id:D4}/model/{race}{Letter(id.Category)}{id.Id:D4}_{id.Slot}.mdl",
        };
    }

    /// <summary>The IMC file that picks a gear set's or weapon body's material variants.</summary>
    public static string? ImcPath(GameModelId id) => id.Category switch
    {
        GameFileCategory.Equipment => $"chara/equipment/e{id.Id:D4}/e{id.Id:D4}.imc",
        GameFileCategory.Accessory => $"chara/accessory/a{id.Id:D4}/a{id.Id:D4}.imc",
        GameFileCategory.Weapon => $"chara/weapon/w{id.Id:D4}/obj/body/b{id.SecondaryId:D4}/b{id.SecondaryId:D4}.imc",
        _ => null,
    };

    /// <summary>A gear set's IMC file before its slots are known.</summary>
    public static string SetImcPath(GameFileCategory category, ushort set)
        => category == GameFileCategory.Accessory ? $"chara/accessory/a{set:D4}/a{set:D4}.imc" : $"chara/equipment/e{set:D4}/e{set:D4}.imc";

    /// <summary>
    /// The IMC part of a gear slot: its position in the set's slot list. An IMC file stores only the
    /// parts in its part mask, in this order. Weapons have one part.
    /// </summary>
    public static int ImcPart(GameModelId id) => id.Category switch
    {
        GameFileCategory.Equipment => EquipmentSlots.IndexOf(id.Slot),
        GameFileCategory.Accessory => AccessorySlots.IndexOf(id.Slot),
        GameFileCategory.Weapon => 0,
        _ => -1,
    };

    /// <summary>The model a vanilla game path names, or null for other paths.</summary>
    public static GameModelId? Parse(string? gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath) || gamePath.Length > 256) return null;
        var path = PathRules.NormalizeGamePath(gamePath).ToLowerInvariant();
        if (HumanModel().Match(path) is { Success: true } human)
        {
            var category = human.Groups["folder"].Value switch
            {
                "hair" => GameFileCategory.Hair,
                "face" => GameFileCategory.Face,
                "tail" => GameFileCategory.Tail,
                "zear" => GameFileCategory.Ears,
                _ => GameFileCategory.Body,
            };
            var slot = human.Groups["slot"].Value;
            if (human.Groups["letter"].Value[0] != Letter(category) || !Slots(category).Contains(slot)) return null;
            return new GameModelId(category, ushort.Parse(human.Groups["race"].Value), ushort.Parse(human.Groups["id"].Value), 0, slot);
        }
        if (GearModel().Match(path) is { Success: true } gear)
        {
            var category = gear.Groups["folder"].Value == "equipment" ? GameFileCategory.Equipment : GameFileCategory.Accessory;
            var slot = gear.Groups["slot"].Value;
            if (gear.Groups["letter"].Value[0] != Letter(category) || !Slots(category).Contains(slot)) return null;
            return new GameModelId(category, ushort.Parse(gear.Groups["race"].Value), ushort.Parse(gear.Groups["id"].Value), 0, slot);
        }
        if (WeaponModel().Match(path) is { Success: true } weapon)
            return new GameModelId(GameFileCategory.Weapon, 0, ushort.Parse(weapon.Groups["id"].Value), ushort.Parse(weapon.Groups["body"].Value), "");
        return null;
    }

    public static string CategoryLabel(GameFileCategory category) => category switch
    {
        GameFileCategory.Hair => "Hair",
        GameFileCategory.Face => "Faces",
        GameFileCategory.Tail => "Tails",
        GameFileCategory.Ears => "Viera ears",
        GameFileCategory.Body => "Bodies",
        GameFileCategory.Equipment => "Equipment",
        GameFileCategory.Accessory => "Accessories",
        _ => "Weapons",
    };

    public static string SlotLabel(string slot) => slot switch
    {
        "met" => "Head",
        "top" => "Body",
        "glv" => "Hands",
        "dwn" => "Legs",
        "sho" => "Feet",
        "ear" => "Ears",
        "nek" => "Neck",
        "wrs" => "Wrists",
        "rir" => "Right ring",
        "ril" => "Left ring",
        "hir" => "Hair",
        "fac" => "Face",
        "til" => "Tail",
        "zer" => "Ears",
        _ => slot,
    };
}
