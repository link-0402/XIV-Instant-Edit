using System;
using System.Collections.Generic;
using System.Linq;

namespace InstantEdit.Studio.Models;

/// <summary>
/// The Penumbra metadata a port sets beside its files, per item. Everything here is an override
/// of what the game ships: a null or missing value means "leave the vanilla value alone", so no
/// manipulation is written for it.
///
/// Eqdp is not here: it follows the configured race models and is derived at build time.
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

    public bool IsEmpty => EqpEntry == null && EstByRace.Count == 0 && string.IsNullOrEmpty(Option);

    public ItemMeta Clone() => new()
    {
        EqpEntry  = EqpEntry,
        EstByRace = EstByRace.ToDictionary(kv => kv.Key, kv => kv.Value),
        Option    = Option,
    };
}
