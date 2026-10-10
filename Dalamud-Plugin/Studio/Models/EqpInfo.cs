using System;
using System.Collections.Generic;

namespace InstantEdit.Studio.Models;

/// <summary>One flag of an EQP entry: which bit it is, and what it does to the character.</summary>
public readonly record struct EqpFlag(ulong Bit, string Label, string Help);

/// <summary>
/// The Equipment Parameter entry of a gear set: the flags that decide what a piece hides or shows
/// on the rest of the character (a long coat hiding the thighs, a helmet hiding the hair, …).
/// Each slot owns its own byte range of the 64-bit entry; accessories have none.
/// Bits and names follow Penumbra.GameData's <c>EqpEntry</c> / <c>Eqp</c>.
/// </summary>
public static class EqpInfo
{
    /// <summary>Bit mask of the bytes belonging to a slot; 0 for slots with no EQP entry.</summary>
    public static ulong Mask(EquipSlot slot) => slot switch
    {
        EquipSlot.Body  => 0xFFFFul,
        EquipSlot.Legs  => 0xFFul << 16,
        EquipSlot.Hands => 0xFFul << 24,
        EquipSlot.Feet  => 0xFFul << 32,
        EquipSlot.Head  => 0xFFFFFFul << 40,
        _               => 0,
    };

    public static bool HasEntry(EquipSlot slot) => Mask(slot) != 0;

    /// <summary>
    /// The editable flags of a slot, in display order. Bits the game has no known use for are
    /// left out of the list but kept as they are when the entry is written.
    /// </summary>
    public static IReadOnlyList<EqpFlag> Flags(EquipSlot slot) => slot switch
    {
        EquipSlot.Body => new EqpFlag[]
        {
            new(0x0001, "Enabled",                "Off: the game ignores this entry entirely."),
            new(0x0002, "Hide waist",             "Hides the waist area of leg gear, for a top that covers it."),
            new(0x0004, "Hide thighs",            "Hides the upper part of leg gear, for a long coat or skirt."),
            new(0x0008, "Hide gloves (short)",    "Hides short glove models."),
            new(0x0010, "Hide glove cuffs",       "Hides the cuffs of gloves."),
            new(0x0020, "Hide gloves (medium)",   "Hides medium-length glove models."),
            new(0x0040, "Hide gloves (long)",     "Hides long glove models."),
            new(0x0080, "Hide gorget",            "Hides the neck piece some bodies draw."),
            new(0x0100, "Show legs",              "Keeps leg gear visible under this piece."),
            new(0x0200, "Show hands",             "Keeps hand gear visible."),
            new(0x0400, "Show head",              "Keeps head gear visible."),
            new(0x0800, "Show necklace",          "Keeps a necklace visible."),
            new(0x1000, "Show bracelet",          "Keeps bracelets visible."),
            new(0x2000, "Show tail",              "Keeps the tail visible, for Miqo'te, Au Ra and Hrothgar."),
            new(0x4000, "Disable breast physics", "Stops chest physics while this piece is worn."),
            new(0x8000, "Uses EVP table",         "The piece has its own deformer entry; best left as the game set it."),
        },
        EquipSlot.Legs => new EqpFlag[]
        {
            new(0x01ul << 16, "Enabled",             "Off: the game ignores this entry entirely."),
            new(0x02ul << 16, "Hide knee pads",      "Hides the knee part of boots."),
            new(0x04ul << 16, "Hide boots (short)",  "Hides short boot models."),
            new(0x08ul << 16, "Hide boots (medium)", "Hides medium-length boot models."),
            new(0x20ul << 16, "Show feet",           "Keeps foot gear visible under this piece."),
            new(0x40ul << 16, "Show tail",           "Keeps the tail visible."),
        },
        EquipSlot.Hands => new EqpFlag[]
        {
            new(0x01ul << 24, "Enabled",            "Off: the game ignores this entry entirely."),
            new(0x02ul << 24, "Hide elbow",         "Hides the elbow area of the body."),
            new(0x04ul << 24, "Hide forearm",       "Hides the forearm of the body."),
            new(0x10ul << 24, "Show bracelet",      "Keeps bracelets visible."),
            new(0x20ul << 24, "Show ring (left)",   "Keeps the left ring visible."),
            new(0x40ul << 24, "Show ring (right)",  "Keeps the right ring visible."),
        },
        EquipSlot.Feet => new EqpFlag[]
        {
            new(0x01ul << 32, "Enabled",    "Off: the game ignores this entry entirely."),
            new(0x02ul << 32, "Hide knee",  "Hides the knee area of leg gear."),
            new(0x04ul << 32, "Hide calf",  "Hides the calf of leg gear."),
            new(0x08ul << 32, "Hide ankle", "Hides the ankle of leg gear."),
        },
        EquipSlot.Head => new EqpFlag[]
        {
            new(0x000001ul << 40, "Enabled",                   "Off: the game ignores this entry entirely."),
            new(0x000002ul << 40, "Hide scalp",                "Hides the scalp under the hat."),
            new(0x000004ul << 40, "Hide hair",                 "Hides the hairstyle."),
            new(0x000008ul << 40, "Show hair anyway",          "Overrides hiding the hair."),
            new(0x000010ul << 40, "Hide neck",                 "Hides the neck area of the body."),
            new(0x000020ul << 40, "Show necklace",             "Keeps a necklace visible."),
            new(0x000040ul << 40, "Show earrings (Hyur, Roe)", "Keeps earrings visible for those races."),
            new(0x000080ul << 40, "Show earrings (Lala, Elezen)", "Keeps earrings visible for those races."),
            new(0x000100ul << 40, "Show earrings (Miqo, Hroth, Viera)", "Keeps earrings visible for those races."),
            new(0x000200ul << 40, "Show earrings (Au Ra)",     "Keeps earrings visible for Au Ra."),
            new(0x000400ul << 40, "Show ears (human)",         "Keeps the human ear visible."),
            new(0x000800ul << 40, "Show ears (Miqo'te)",       "Keeps Miqo'te ears visible."),
            new(0x001000ul << 40, "Show horns (Au Ra)",        "Keeps Au Ra horns visible."),
            new(0x002000ul << 40, "Show ears (Viera)",         "Keeps Viera ears visible."),
            new(0x004000ul << 40, "Disable bangs physics",     "Stops the physics of the hair's front strands."),
            new(0x008000ul << 40, "Disable hair physics",      "Stops hair physics entirely."),
            new(0x010000ul << 40, "Show Hrothgar hat",         "Draws the hat for Hrothgar."),
            new(0x020000ul << 40, "Show Viera hat",            "Draws the hat for Viera."),
            new(0x040000ul << 40, "Uses EVP table",            "The piece has its own deformer entry; best left as the game set it."),
        },
        _ => Array.Empty<EqpFlag>(),
    };

    /// <summary>Names of the flags that differ between two entries, for messages.</summary>
    public static List<string> Differences(EquipSlot slot, ulong a, ulong b)
    {
        var names = new List<string>();
        foreach (var flag in Flags(slot))
            if ((a & flag.Bit) != (b & flag.Bit))
                names.Add($"{flag.Label} {(((b & flag.Bit) != 0) ? "on" : "off")}");
        return names;
    }
}
