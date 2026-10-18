using System;
using System.Collections.Generic;
using System.Linq;

namespace InstantEdit.Studio.Models;

/// <summary>
/// The Penumbra metadata a port sets beside its files, per item. Everything here is an override
/// of what the game ships: a null or missing value means "leave the vanilla value alone", so no
/// manipulation is written for it.
///
/// Eqdp is not here: it follows the configured race models (and <see cref="VanillaModelsOff"/>)
/// and is derived at build time.
/// </summary>
[Serializable]
public class ItemMeta
{
    /// <summary>
    /// Equipment parameters for the item's slot (gear): the full 64-bit entry the port wants.
    /// Null while it matches the game's own entry.
    /// </summary>
    public ulong? EqpEntry { get; set; }

    /// <summary>
    /// Extra skeleton ids for hair, by race code ("0101"). A race listed here gets an Est
    /// manipulation; races not listed keep whatever the game has.
    /// </summary>
    public Dictionary<string, ushort> EstByRace { get; set; } = new();

    /// <summary>
    /// Where the item goes in the built mod (see <see cref="PackPlace"/>): its files, unless one of them
    /// is put elsewhere, and its metadata. Null or empty for the mod's default files.
    /// </summary>
    public string? Option { get; set; }

    /// <summary>
    /// Races (by race code, "0801") whose own vanilla model of this gear is switched off, so they
    /// fall back to another race's model - the one this port writes. Ignored for a race the port
    /// writes a model for itself.
    /// </summary>
    public HashSet<string> VanillaModelsOff { get; set; } = new();

    public bool IsEmpty => EqpEntry == null && EstByRace.Count == 0 && string.IsNullOrEmpty(Option) && VanillaModelsOff.Count == 0;

    public ItemMeta Clone() => new()
    {
        EqpEntry         = EqpEntry,
        EstByRace        = EstByRace.ToDictionary(kv => kv.Key, kv => kv.Value),
        Option           = Option,
        VanillaModelsOff = new HashSet<string>(VanillaModelsOff),
    };
}
