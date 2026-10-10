using System;
using System.Collections.Generic;
using System.Linq;

namespace InstantEdit.Studio.Models;

/// <summary>The eight playable character races (model bases; Highlander is its own base, distinct from Midlander).</summary>
public enum PlayerRace
{
    Midlander,
    Highlander,
    Elezen,
    Lalafell,
    Miqote,
    Roegadyn,
    AuRa,
    Hrothgar,
    Viera,
}

public enum PlayerGender
{
    Male,
    Female,
}

/// <summary>
/// One playable race/gender combination. <see cref="RaceCode"/> is the 4-digit
/// code used in model file paths (e.g. "0101"); <see cref="PenumbraRace"/> and
/// <see cref="PenumbraGender"/> are the exact enum-member spellings Penumbra's
/// meta manipulation JSON expects for its "Race"/"Gender" fields.
/// </summary>
public readonly record struct RaceGender(PlayerRace Race, PlayerGender Gender)
{
    public string DisplayName => $"{RaceInfo.RaceDisplayName[Race]} ({(Gender == PlayerGender.Male ? "Male" : "Female")})";

    public string RaceCode => RaceInfo.RaceCodeMap[(Race, Gender)];

    /// <summary>Spelling Penumbra's ModelRace enum uses in meta.json (e.g. "Midlander", "AuRa").</summary>
    public string PenumbraRace => Race.ToString();

    /// <summary>Spelling Penumbra's Gender enum uses in meta.json ("Male"/"Female").</summary>
    public string PenumbraGender => Gender.ToString();
}

/// <summary>Metadata and lookups for the 16 playable race/gender combinations.</summary>
public static class RaceInfo
{
    public static readonly IReadOnlyDictionary<PlayerRace, string> RaceDisplayName = new Dictionary<PlayerRace, string>
    {
        { PlayerRace.Midlander,  "Midlander"  },
        { PlayerRace.Highlander, "Highlander" },
        { PlayerRace.Elezen,     "Elezen"     },
        { PlayerRace.Lalafell,   "Lalafell"   },
        { PlayerRace.Miqote,     "Miqo'te"    },
        { PlayerRace.Roegadyn,   "Roegadyn"   },
        { PlayerRace.AuRa,       "Au Ra"      },
        { PlayerRace.Hrothgar,   "Hrothgar"   },
        { PlayerRace.Viera,      "Viera"      },
    };

    /// <summary>Padded race code used in model file paths, e.g. "chara/.../c0101....mdl".</summary>
    public static readonly IReadOnlyDictionary<(PlayerRace Race, PlayerGender Gender), string> RaceCodeMap =
        new Dictionary<(PlayerRace, PlayerGender), string>
        {
            { (PlayerRace.Midlander,  PlayerGender.Male),   "0101" },
            { (PlayerRace.Midlander,  PlayerGender.Female), "0201" },
            { (PlayerRace.Highlander, PlayerGender.Male),   "0301" },
            { (PlayerRace.Highlander, PlayerGender.Female), "0401" },
            { (PlayerRace.Elezen,     PlayerGender.Male),   "0501" },
            { (PlayerRace.Elezen,     PlayerGender.Female), "0601" },
            { (PlayerRace.Miqote,     PlayerGender.Male),   "0701" },
            { (PlayerRace.Miqote,     PlayerGender.Female), "0801" },
            { (PlayerRace.Roegadyn,   PlayerGender.Male),   "0901" },
            { (PlayerRace.Roegadyn,   PlayerGender.Female), "1001" },
            { (PlayerRace.Lalafell,   PlayerGender.Male),   "1101" },
            { (PlayerRace.Lalafell,   PlayerGender.Female), "1201" },
            { (PlayerRace.AuRa,       PlayerGender.Male),   "1301" },
            { (PlayerRace.AuRa,       PlayerGender.Female), "1401" },
            { (PlayerRace.Hrothgar,   PlayerGender.Male),   "1501" },
            { (PlayerRace.Hrothgar,   PlayerGender.Female), "1601" },
            { (PlayerRace.Viera,      PlayerGender.Male),   "1701" },
            { (PlayerRace.Viera,      PlayerGender.Female), "1801" },
        };

    /// <summary>
    /// The race a gender's shared files belong to. Midlander is what everything falls back to, so
    /// gear materials are named after its codes: c0101 for male, c0201 for female.
    /// </summary>
    public static RaceGender BaseFor(PlayerGender gender) => new(PlayerRace.Midlander, gender);

    /// <summary>All 16 playable race/gender combinations, in ascending race-code order (0101, 0201, 0301, …).</summary>
    public static readonly IReadOnlyList<RaceGender> AllRaces = new List<RaceGender>
    {
        new(PlayerRace.Midlander,  PlayerGender.Male),   // 0101
        new(PlayerRace.Midlander,  PlayerGender.Female), // 0201
        new(PlayerRace.Highlander, PlayerGender.Male),   // 0301
        new(PlayerRace.Highlander, PlayerGender.Female), // 0401
        new(PlayerRace.Elezen,     PlayerGender.Male),   // 0501
        new(PlayerRace.Elezen,     PlayerGender.Female), // 0601
        new(PlayerRace.Miqote,     PlayerGender.Male),   // 0701
        new(PlayerRace.Miqote,     PlayerGender.Female), // 0801
        new(PlayerRace.Roegadyn,   PlayerGender.Male),   // 0901
        new(PlayerRace.Roegadyn,   PlayerGender.Female), // 1001
        new(PlayerRace.Lalafell,   PlayerGender.Male),   // 1101
        new(PlayerRace.Lalafell,   PlayerGender.Female), // 1201
        new(PlayerRace.AuRa,       PlayerGender.Male),   // 1301
        new(PlayerRace.AuRa,       PlayerGender.Female), // 1401
        new(PlayerRace.Hrothgar,   PlayerGender.Male),   // 1501
        new(PlayerRace.Hrothgar,   PlayerGender.Female), // 1601
        new(PlayerRace.Viera,      PlayerGender.Male),   // 1701
        new(PlayerRace.Viera,      PlayerGender.Female), // 1801
    };

    // ── Race codes as Penumbra numbers them (101 for c0101) ──────────────────

    /// <summary>The 18 playable race codes, 101 to 1801.</summary>
    public static readonly IReadOnlyList<ushort> PlayableCodes = AllRaces.Select(Code).ToArray();

    public static ushort Code(RaceGender race) => ushort.Parse(race.RaceCode);

    /// <summary>The playable race of a code, or null for NPC and unknown codes.</summary>
    public static RaceGender? FromCode(ushort code)
    {
        foreach (var race in AllRaces)
            if (Code(race) == code)
                return race;
        return null;
    }

    public static bool IsPlayableCode(ushort code) => code % 100 == 1 && code / 100 is >= 1 and <= 18;

    /// <summary>The race code of Penumbra's "Race"/"Gender" spellings in meta.json ("AuRa", "Female").</summary>
    public static bool TryFromPenumbra(string? race, string? gender, out ushort code)
    {
        foreach (var candidate in AllRaces)
        {
            if (string.Equals(candidate.PenumbraRace, race, StringComparison.OrdinalIgnoreCase)
             && string.Equals(candidate.PenumbraGender, gender, StringComparison.OrdinalIgnoreCase))
            {
                code = Code(candidate);
                return true;
            }
        }
        code = 0;
        return false;
    }

    /// <summary>A race code for people: "Miqo'te (Female)", or "c0104" for NPC and unknown codes.</summary>
    public static string NameOf(ushort code) => FromCode(code)?.DisplayName ?? $"c{code:D4}";

    public static bool IsFemaleCode(ushort code) => code / 100 % 2 == 0;

    /// <summary>The race a race falls back to for a model it has none of (Penumbra's GenderRace.Fallback).</summary>
    public static ushort Fallback(ushort code) => code switch
    {
        201  => 101,
        1501 => 901,   // Hrothgar male to Roegadyn male
        1201 => 1101,  // Lalafell female to Lalafell male
        _ when IsFemaleCode(code) => 201,
        _ => 101,
    };

    /// <summary>The race whose body skin a race wears: Elezen and Miqo'te the Midlander one, Roegadyn female the Highlander female one.</summary>
    public static ushort SkinRace(ushort code) => code switch
    {
        501 or 701 => 101,
        601 or 801 => 201,
        1001 => 401,
        1201 => 1101,
        _ => code,
    };

    /// <summary>The model race of the Race, Gender and Tribe customize bytes; Hyur clans have codes of their own.</summary>
    public static ushort? FromCustomize(byte race, byte gender, byte tribe)
    {
        int? baseCode = (race, tribe) switch
        {
            (1, 1) => 1, (1, 2) => 3, (2, _) => 5, (3, _) => 11, (4, _) => 7,
            (5, _) => 9, (6, _) => 13, (7, _) => 15, (8, _) => 17, _ => null,
        };
        if (baseCode is not { } value || gender > 1) return null;
        return (ushort)((value + gender) * 100 + 1);
    }

    /// <summary>
    /// The race whose gear model a character of <paramref name="race"/> loads (Penumbra's
    /// ResolveEqdpRaceCode): its own when its EQDP model bit is set, else its fallback's when that
    /// one's is, else Midlander male. Only one fallback level is tried.
    /// </summary>
    public static ushort ResolveModelRace(ushort race, bool accessory, Func<ushort, bool> hasModel)
    {
        if (race == 101) return 101;
        if (accessory && race % 10 != 1) return 101;
        if (hasModel(race)) return race;
        var fallback = Fallback(race);
        if (fallback == 101) return 101;
        return hasModel(fallback) ? fallback : (ushort)101;
    }
}
