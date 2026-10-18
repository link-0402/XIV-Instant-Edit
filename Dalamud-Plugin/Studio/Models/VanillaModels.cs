using System;
using System.Collections.Generic;
using System.Linq;

namespace InstantEdit.Studio.Models;

/// <summary>
/// The vanilla race models of a gear item that the user switched off (<see cref="ItemMeta.VanillaModelsOff"/>):
/// such a race loses its Eqdp bits for the slot, so it falls back to another race's model, the one
/// the port writes, like a race the game has no model for.
/// </summary>
internal static class VanillaModels
{
    /// <summary>
    /// Whether a race's own vanilla model of this gear can be switched off: a race the port writes
    /// no model for, other than Midlander male, which every race ends up at and which never falls back.
    /// </summary>
    public static bool CanTurnOff(PortItem item, RaceGender race)
        => item.Subject is GearSubject && race.RaceCode != "0101" && !item.Models.Exists(m => m.RaceGender == race);

    /// <summary>Whether a race's own vanilla model is switched off for this item, so it falls back to another race's.</summary>
    public static bool IsOff(PortItem item, RaceGender race)
        => CanTurnOff(item, race) && item.Meta.VanillaModelsOff.Contains(race.RaceCode);

    /// <summary>The races of <paramref name="native"/> whose vanilla model this item switches off.</summary>
    public static List<RaceGender> Off(PortItem item, IEnumerable<RaceGender> native)
        => native.Where(rg => IsOff(item, rg)).OrderBy(rg => rg.RaceCode, StringComparer.Ordinal).ToList();

    /// <summary>The race a character of <paramref name="race"/> loads this gear's model from once its own is switched off.</summary>
    public static RaceGender FallbackRace(PortItem item, RaceGender race, HashSet<RaceGender> native)
    {
        bool accessory = item.Subject is GearSubject gear && SlotInfo.IsAccessory(gear.Item.Slot);
        var own = RaceInfo.Code(race);
        bool HasModel(ushort code) => code != own && RaceInfo.FromCode(code) is { } rg
            && (item.Models.Exists(m => m.RaceGender == rg) || native.Contains(rg) && !IsOff(item, rg));
        return RaceInfo.FromCode(RaceInfo.ResolveModelRace(own, accessory, HasModel)) ?? RaceInfo.BaseFor(PlayerGender.Male);
    }
}
