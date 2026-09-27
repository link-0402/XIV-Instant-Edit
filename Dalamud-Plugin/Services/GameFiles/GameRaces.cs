using System.Collections.Immutable;

namespace InstantEdit.Services.GameFiles;

/// <summary>
/// A character model race code such as <c>c0101</c>: the race and gender a model is made for, and
/// whether players can be it. The <c>xx04</c> codes and 9104/9204 are NPC bodies.
/// </summary>
internal sealed record GameRace(ushort Code, string Label, bool Player)
{
    /// <summary>The code as game paths spell it, e.g. <c>c0101</c>.</summary>
    public string Id => $"c{Code:D4}";
}

internal static class GameRaces
{
    // In race-code order: 0101/0201 are Midlanders, 0301/0401 Highlanders, and so on.
    private static readonly string[] Names =
        ["Midlander", "Highlander", "Elezen", "Miqo'te", "Roegadyn", "Lalafell", "Au Ra", "Hrothgar", "Viera"];

    /// <summary>
    /// Every race code character folders use, in code order. Not every NPC code has files: in the
    /// 2026 game data 27 of these 38 have a base skeleton.
    /// </summary>
    public static readonly ImmutableArray<GameRace> All = Build();

    private static readonly Dictionary<ushort, GameRace> ByCode = All.ToDictionary(race => race.Code);

    private static ImmutableArray<GameRace> Build()
    {
        var races = new List<GameRace>();
        for (var index = 0; index < Names.Length; index++)
        {
            foreach (var female in new[] { false, true })
            {
                var code = (ushort)((index * 2 + (female ? 2 : 1)) * 100);
                var gender = female ? "Female" : "Male";
                races.Add(new GameRace((ushort)(code + 1), $"{gender} {Names[index]}", true));
                races.Add(new GameRace((ushort)(code + 4), $"{gender} {Names[index]} NPC", false));
            }
        }
        races.Add(new GameRace(9104, "Other Male NPC", false));
        races.Add(new GameRace(9204, "Other Female NPC", false));
        return [.. races.OrderBy(race => race.Code)];
    }

    public static GameRace? Find(ushort code) => ByCode.GetValueOrDefault(code);

    public static string Label(ushort code) => Find(code)?.Label ?? $"c{code:D4}";

    /// <summary>
    /// The player race code for a character-creation row: the game's Race sheet (1 Hyur, 2 Elezen,
    /// 3 Lalafell, 4 Miqo'te, 5 Roegadyn, 6 Au Ra, 7 Hrothgar, 8 Viera), its Tribe (Hyur tribe 2 is
    /// Highlander) and gender (0 male, 1 female). Null for other values.
    /// </summary>
    public static ushort? PlayerCode(uint race, uint tribe, int gender)
    {
        int? first = race switch
        {
            1 => tribe == 2 ? 3 : 1,
            2 => 5,
            3 => 11,
            4 => 7,
            5 => 9,
            6 => 13,
            7 => 15,
            8 => 17,
            _ => null,
        };
        if (first is not { } value || gender is not (0 or 1)) return null;
        return (ushort)((value + gender) * 100 + 1);
    }

    /// <summary>
    /// The highest face model ID players can wear. Their race folders also hold NPC-only faces,
    /// numbered 91, 92, 191, 192 and from 200 up.
    /// </summary>
    public const int MaxPlayerFace = 104;

    /// <summary>
    /// The face model a character-creation face number picks for a Tribe row. Highlanders (tribe 2),
    /// Duskwight, Dunesfolk, Keepers of the Moon and Hellsguard wear the models 100 above the number;
    /// the other clans, Au Ra, Hrothgar and Viera included, wear the number itself.
    /// </summary>
    public static ushort FaceModel(uint tribe, int face) => (ushort)(tribe is 2 or 4 or 6 or 8 or 10 ? face + 100 : face);
}
