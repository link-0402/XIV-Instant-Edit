using System;
using System.Collections.Generic;

namespace InstantEdit.Studio.Models;

/// <summary>
/// The Equipment Deformer Parameter bits: per equipment slot and set id, every race has two — one
/// saying it has a material of its own for the item, one saying it has a model of its own. A race
/// whose model bit is unset wears another race's model; a race whose material bit is unset looks
/// the material up under the base race of its gender, which is why gear materials are named
/// c0101 (male) and c0201 (female).
///
/// A port therefore has to claim what it writes: the model bit for every race it gives a model, and
/// the material bit for the base race whose name its materials carry — the game does not ship one
/// for every item. Bit values and slot offsets mirror Penumbra's <c>Eqdp</c>/<c>EqdpEntry</c>, where
/// the first bit of a slot's pair is the material and the second the model.
/// </summary>
public static class EqdpInfo
{
    /// <summary>
    /// Bit offset of a slot's (material, model) pair within an entry. Equipment and accessories have
    /// separate files, so the two ranges do not collide.
    /// </summary>
    public static int Offset(EquipSlot slot) => slot switch
    {
        EquipSlot.Head      => 0,
        EquipSlot.Body      => 2,
        EquipSlot.Hands     => 4,
        EquipSlot.Legs      => 6,
        EquipSlot.Feet      => 8,
        EquipSlot.Earring   => 0,
        EquipSlot.Neck      => 2,
        EquipSlot.Wrists    => 4,
        EquipSlot.RingRight => 6,
        EquipSlot.RingLeft  => 8,
        _                   => throw new ArgumentOutOfRangeException(nameof(slot)),
    };

    /// <summary>"This race has its own material for the item."</summary>
    public static ushort Material(EquipSlot slot) => (ushort)(1 << Offset(slot));

    /// <summary>"This race has its own model for the item."</summary>
    public static ushort Model(EquipSlot slot) => (ushort)(1 << (Offset(slot) + 1));

    /// <summary>Both bits of the slot — everything of an entry that belongs to it.</summary>
    public static ushort Mask(EquipSlot slot) => (ushort)(Material(slot) | Model(slot));

    /// <summary>What an entry grants this slot, for the report and the pre-flight list.</summary>
    public static string Describe(EquipSlot slot, ushort entry)
    {
        var parts = new List<string>(2);
        if ((entry & Material(slot)) != 0) parts.Add("material");
        if ((entry & Model(slot)) != 0) parts.Add("model");
        return parts.Count == 0 ? "nothing" : string.Join(" + ", parts);
    }
}
