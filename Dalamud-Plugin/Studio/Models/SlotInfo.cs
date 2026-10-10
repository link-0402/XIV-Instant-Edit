using System;
using System.Collections.Generic;

namespace InstantEdit.Studio.Models;

/// <summary>Identifies an equipment slot using Penumbra/FFXIV naming conventions.</summary>
public enum EquipSlot
{
    Head,
    Body,
    Hands,
    Legs,
    Feet,
    Earring,
    Neck,
    Wrists,
    RingRight,
    RingLeft,
}

/// <summary>Maps between slot enums and the abbreviated game keys used in filenames.</summary>
public static class SlotInfo
{
    public static readonly IReadOnlyDictionary<EquipSlot, string> KeyMap =
        new Dictionary<EquipSlot, string>
        {
            { EquipSlot.Head,      "met" },
            { EquipSlot.Body,      "top" },
            { EquipSlot.Hands,     "glv" },
            { EquipSlot.Legs,      "dwn" },
            { EquipSlot.Feet,      "sho" },
            { EquipSlot.Earring,   "ear" },
            { EquipSlot.Neck,      "nek" },
            { EquipSlot.Wrists,    "wrs" },
            { EquipSlot.RingRight, "rir" },
            { EquipSlot.RingLeft,  "ril" },
        };

    public static readonly IReadOnlyDictionary<string, EquipSlot> ReverseMap;

    /// <summary>
    /// Maps slots to the EquipSlot string values used in Penumbra mod JSON
    /// (manipulation "Slot"/"EquipSlot" fields).  These must match Penumbra's
    /// EquipSlot enum spellings exactly, e.g. "Ears", "RFinger", "LFinger".
    /// </summary>
    public static readonly IReadOnlyDictionary<EquipSlot, string> LabelMap =
        new Dictionary<EquipSlot, string>
        {
            { EquipSlot.Head,      "Head"    },
            { EquipSlot.Body,      "Body"    },
            { EquipSlot.Hands,     "Hands"   },
            { EquipSlot.Legs,      "Legs"    },
            { EquipSlot.Feet,      "Feet"    },
            { EquipSlot.Earring,   "Ears"    },
            { EquipSlot.Neck,      "Neck"    },
            { EquipSlot.Wrists,    "Wrists"  },
            { EquipSlot.RingRight, "RFinger" },
            { EquipSlot.RingLeft,  "LFinger" },
        };

    /// <summary>Human-readable slot names for UI display.</summary>
    public static readonly IReadOnlyDictionary<EquipSlot, string> DisplayLabelMap =
        new Dictionary<EquipSlot, string>
        {
            { EquipSlot.Head,      "Head"       },
            { EquipSlot.Body,      "Body"       },
            { EquipSlot.Hands,     "Hands"      },
            { EquipSlot.Legs,      "Legs"       },
            { EquipSlot.Feet,      "Feet"       },
            { EquipSlot.Earring,   "Earring"    },
            { EquipSlot.Neck,      "Neck"       },
            { EquipSlot.Wrists,    "Wrists"     },
            { EquipSlot.RingRight, "Ring Right" },
            { EquipSlot.RingLeft,  "Ring Left"  },
        };

    private static readonly HashSet<EquipSlot> _accessories = new()
    {
        EquipSlot.Earring,
        EquipSlot.Neck,
        EquipSlot.Wrists,
        EquipSlot.RingRight,
        EquipSlot.RingLeft,
    };

    static SlotInfo()
    {
        var rev = new Dictionary<string, EquipSlot>(StringComparer.OrdinalIgnoreCase);
        foreach (var (slot, key) in KeyMap)
            rev[key] = slot;
        ReverseMap = rev;
    }

    /// <summary>Returns "a" for accessories, "e" for equipment.</summary>
    public static string ItemPrefix(EquipSlot slot) => _accessories.Contains(slot) ? "a" : "e";

    public static bool IsAccessory(EquipSlot slot) => _accessories.Contains(slot);

    public static List<EquipSlot> AllSlots { get; } = new List<EquipSlot>(KeyMap.Keys);

    /// <summary>The slot's part in an item's IMC file (mirrors Penumbra's ImcFile.PartIndex): head and earrings 0, body and neck 1, and so on.</summary>
    public static int ImcPartIndex(EquipSlot slot) => (int)slot % 5;

    /// <summary>Where the slot's two bits (material, model) sit in a 16-bit EQDP entry.</summary>
    public static int EqdpShift(EquipSlot slot) => (int)slot % 5 * 2;

    /// <summary>The prefix of the variant attributes (atr_?v_a to atr_?v_j) an IMC attribute mask turns on for the slot.</summary>
    public static string VariantAttributePrefix(EquipSlot slot) => slot switch
    {
        EquipSlot.Head      => "atr_mv_",
        EquipSlot.Body      => "atr_tv_",
        EquipSlot.Hands     => "atr_gv_",
        EquipSlot.Legs      => "atr_dv_",
        EquipSlot.Feet      => "atr_sv_",
        EquipSlot.Earring   => "atr_ev_",
        EquipSlot.Neck      => "atr_nv_",
        EquipSlot.Wrists    => "atr_wv_",
        EquipSlot.RingRight => "atr_rv_",
        _                   => "atr_lv_",
    };
}
