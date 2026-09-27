using System.Collections.Immutable;
using Lumina.Data.Structs.Excel;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace InstantEdit.Services.GameFiles;

/// <summary>
/// Names from the game's sheets for vanilla models: the items that use a gear set's slot or a weapon
/// body, the hairstyles players can pick in character creation, with the item that unlocks each, and
/// the faces players can pick.
/// </summary>
internal sealed class GameFileNames(
    IReadOnlyDictionary<(GameFileCategory Category, ushort Id, ushort Secondary, string Slot), ImmutableArray<string>> items,
    IReadOnlyDictionary<ushort, ImmutableHashSet<ushort>> hairstyles,
    IReadOnlyDictionary<ushort, string> hairItems,
    IReadOnlyDictionary<ushort, ImmutableHashSet<ushort>>? faces = null)
{
    private readonly IReadOnlyDictionary<ushort, ImmutableHashSet<ushort>> faces = faces ?? new Dictionary<ushort, ImmutableHashSet<ushort>>();

    public static readonly GameFileNames Empty = new(
        new Dictionary<(GameFileCategory, ushort, ushort, string), ImmutableArray<string>>(),
        new Dictionary<ushort, ImmutableHashSet<ushort>>(), new Dictionary<ushort, string>());

    /// <summary>Whether the character-creation hairstyles were read.</summary>
    public bool HasHairstyles => hairstyles.Count > 0;

    /// <summary>Whether the character-creation faces were read.</summary>
    public bool HasFaces => faces.Count > 0;

    /// <summary>
    /// The model's item names: every item that uses a gear set's slot or a weapon body, in variant
    /// order, or the item that unlocks a hairstyle.
    /// </summary>
    public ImmutableArray<string> For(GameModelId id) => id.Category switch
    {
        GameFileCategory.Equipment or GameFileCategory.Accessory => items.GetValueOrDefault((id.Category, id.Id, (ushort)0, id.Slot), []),
        GameFileCategory.Weapon => items.GetValueOrDefault((id.Category, id.Id, id.SecondaryId, ""), []),
        GameFileCategory.Hair => hairItems.TryGetValue(id.Id, out var name) ? [name] : [],
        _ => [],
    };

    /// <summary>
    /// Whether players of the model's race can pick this hairstyle in character creation or with an
    /// unlock item, or this face in character creation. Null for other models, and while the sheets
    /// haven't been read.
    /// </summary>
    public bool? PlayerSelectable(GameModelId id) => id.Category switch
    {
        GameFileCategory.Hair when HasHairstyles => hairstyles.TryGetValue(id.Race, out var styles) && styles.Contains(id.Id),
        GameFileCategory.Face when HasFaces => faces.TryGetValue(id.Race, out var ids) && ids.Contains(id.Id),
        _ => null,
    };

    /// <summary>
    /// Whether the model is one a player character can wear: its race code is a player race's and,
    /// for faces, players can pick it (the face number limit stands in until the sheets are read).
    /// Other models of player race codes, NPC-only hairstyles included, count as the race's.
    /// </summary>
    public bool PlayerModel(GameModelId id)
        => GameRaces.Find(id.Race) is { Player: true } &&
           (id.Category != GameFileCategory.Face || (PlayerSelectable(id) ?? id.Id <= GameRaces.MaxPlayerFace));
}

/// <summary>Reads <see cref="GameFileNames"/> from the game's Item and character-creation sheets.</summary>
internal static class GameFileNameSource
{
    // The customize bytes of the hairstyle menu in HairMakeType and the face menu in CharaMakeType.
    private const uint HairstyleCustomize = 6;
    private const uint FaceCustomize = 5;

    public static GameFileNames Build(ExcelSheet<Item> itemSheet, ExcelSheet<HairMakeType> hairMakeTypes,
        ExcelSheet<RawRow> rawHairMakeTypes, ExcelSheet<CharaMakeCustomize> customize, ExcelSheet<CharaMakeType> charaMakeTypes)
    {
        var items = new Dictionary<(GameFileCategory, ushort, ushort, string), SortedSet<(int Variant, string Name)>>();
        foreach (var item in itemSheet)
        {
            if (item.EquipSlotCategory.RowId == 0 || !item.EquipSlotCategory.IsValid)
                continue;
            var name = item.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(name))
                continue;
            var slots = item.EquipSlotCategory.Value;
            if (slots.MainHand == 1 || slots.OffHand == 1)
            {
                foreach (var model in new[] { item.ModelMain, item.ModelSub })
                    if ((ushort)model != 0)
                        Add(GameFileCategory.Weapon, (ushort)model, (ushort)(model >> 16), "", (ushort)(model >> 32), name);
                continue;
            }
            if (item.ModelMain == 0)
                continue;
            var set = (ushort)item.ModelMain;
            var variant = (byte)(item.ModelMain >> 16);
            if (slots.Head == 1) Add(GameFileCategory.Equipment, set, 0, "met", variant, name);
            if (slots.Body == 1) Add(GameFileCategory.Equipment, set, 0, "top", variant, name);
            if (slots.Gloves == 1) Add(GameFileCategory.Equipment, set, 0, "glv", variant, name);
            if (slots.Legs == 1) Add(GameFileCategory.Equipment, set, 0, "dwn", variant, name);
            if (slots.Feet == 1) Add(GameFileCategory.Equipment, set, 0, "sho", variant, name);
            if (slots.Ears == 1) Add(GameFileCategory.Accessory, set, 0, "ear", variant, name);
            if (slots.Neck == 1) Add(GameFileCategory.Accessory, set, 0, "nek", variant, name);
            if (slots.Wrists == 1) Add(GameFileCategory.Accessory, set, 0, "wrs", variant, name);
            if (slots.FingerR == 1) Add(GameFileCategory.Accessory, set, 0, "rir", variant, name);
            if (slots.FingerL == 1) Add(GameFileCategory.Accessory, set, 0, "ril", variant, name);
        }

        var hairstyles = new Dictionary<ushort, HashSet<ushort>>();
        var hairItems = new Dictionary<ushort, string>();
        var uintOffsets = rawHairMakeTypes.Columns.Where(column => column.Type == ExcelColumnDataType.UInt32)
            .Select(column => (int)column.Offset).ToHashSet();
        foreach (var row in hairMakeTypes)
        {
            if (GameRaces.PlayerCode(row.Race.RowId, row.Tribe.RowId, row.Gender) is not { } race)
                continue;
            if (!hairstyles.TryGetValue(race, out var styles))
                hairstyles[race] = styles = [];
            foreach (var menu in row.CharaMakeStruct)
            {
                if (menu.Customize != HairstyleCustomize)
                    continue;
                foreach (var param in MenuParams(menu.SubMenuParam, menu.SubMenuNum, rawHairMakeTypes.GetRow(row.RowId), uintOffsets))
                {
                    if (!customize.TryGetRow(param, out var style) || style.FeatureID == 0)
                        continue;
                    styles.Add(style.FeatureID);
                    if (style.HintItem.RowId != 0 && style.HintItem.IsValid &&
                        style.HintItem.Value.Name.ExtractText() is { Length: > 0 } unlock)
                        hairItems.TryAdd(style.FeatureID, unlock);
                }
            }
        }

        // The face menu's graphics are the face numbers the game stores; the Tribe turns them into models.
        var faces = new Dictionary<ushort, HashSet<ushort>>();
        foreach (var row in charaMakeTypes)
        {
            if (GameRaces.PlayerCode(row.Race.RowId, row.Tribe.RowId, row.Gender) is not { } race)
                continue;
            foreach (var menu in row.CharaMakeStruct)
            {
                if (menu.Customize != FaceCustomize)
                    continue;
                if (!faces.TryGetValue(race, out var ids))
                    faces[race] = ids = [];
                foreach (var face in menu.SubMenuGraphic.Take(menu.SubMenuNum))
                    if (face > 0)
                        ids.Add(GameRaces.FaceModel(row.Tribe.RowId, face));
            }
        }

        return new GameFileNames(
            items.ToDictionary(pair => pair.Key, pair => pair.Value.Select(entry => entry.Name).Distinct().ToImmutableArray()),
            hairstyles.ToDictionary(pair => pair.Key, pair => pair.Value.ToImmutableHashSet()),
            hairItems,
            faces.ToDictionary(pair => pair.Key, pair => pair.Value.ToImmutableHashSet()));

        void Add(GameFileCategory category, ushort id, ushort secondary, string slot, int variant, string name)
        {
            if (!items.TryGetValue((category, id, secondary, slot), out var names))
                items[(category, id, secondary, slot)] = names = [];
            names.Add((variant, name));
        }
    }

    /// <summary>
    /// A character-creation menu's first <paramref name="count"/> entries. The schema stops SubMenuParam
    /// at 100 entries, but hairstyle menus list more (the newest styles come last), and the rest follow
    /// the 100 in the raw row. Their start is found by matching the typed entries, not a fixed offset.
    /// </summary>
    private static IEnumerable<uint> MenuParams(Collection<uint> typed, int count, RawRow raw, IReadOnlySet<int> uintOffsets)
    {
        for (var index = 0; index < count && index < typed.Count; index++)
            yield return typed[index];
        if (count <= typed.Count || typed.Count == 0)
            yield break;
        var start = uintOffsets.FirstOrDefault(offset => Enumerable.Range(0, typed.Count).All(index =>
            uintOffsets.Contains(offset + 4 * index) && raw.ReadUInt32((nuint)(offset + 4 * index)) == typed[index]), -1);
        if (start < 0)
            yield break;
        for (var index = typed.Count; index < count && uintOffsets.Contains(start + 4 * index); index++)
            yield return raw.ReadUInt32((nuint)(start + 4 * index));
    }

    /// <summary>The (weapon, body) pairs the Item sheet's weapons use, so enumeration keeps bodies above its probe range.</summary>
    public static IReadOnlyCollection<(ushort Primary, ushort Body)> WeaponModels(ExcelSheet<Item> itemSheet)
    {
        var models = new HashSet<(ushort, ushort)>();
        foreach (var item in itemSheet)
        {
            if (item.EquipSlotCategory.RowId == 0 || !item.EquipSlotCategory.IsValid)
                continue;
            var slots = item.EquipSlotCategory.Value;
            if (slots.MainHand != 1 && slots.OffHand != 1)
                continue;
            foreach (var model in new[] { item.ModelMain, item.ModelSub })
                if ((ushort)model != 0)
                    models.Add(((ushort)model, (ushort)(model >> 16)));
        }
        return models;
    }
}
