using System.Collections.Generic;

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
}
