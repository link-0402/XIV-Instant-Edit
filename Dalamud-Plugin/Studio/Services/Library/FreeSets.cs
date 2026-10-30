using System;
using System.Collections.Generic;
using System.Linq;
using InstantEdit.Studio.Models;
using GameItem = InstantEdit.Studio.Services.GameDataService.GameItem;

namespace InstantEdit.Studio.Services.Library;

/// <summary>One slot of an item set: its items, and whether a mod changes any of them.</summary>
internal sealed record SetSlot(EquipSlot Slot, IReadOnlyList<GameItem> Items, bool Taken);

/// <summary>An item set (gear or accessories of one model id) with every slot it has.</summary>
internal sealed record ItemSet(bool Accessory, ushort SetId, IReadOnlyList<SetSlot> Slots)
{
    public string IdDisplay => $"{(Accessory ? 'a' : 'e')}{SetId:D4}";
}

/// <summary>
/// The item browser's free set finder: item sets whose chosen slots no mod changes. Gear and
/// accessories are separate (their ids are counted apart), and both rings are one slot. Dalamud-free.
/// </summary>
internal static class FreeSets
{
    public static readonly EquipSlot[] GearSlots      = { EquipSlot.Head, EquipSlot.Body, EquipSlot.Hands, EquipSlot.Legs, EquipSlot.Feet };
    public static readonly EquipSlot[] AccessorySlots = { EquipSlot.Earring, EquipSlot.Neck, EquipSlot.Wrists, EquipSlot.RingRight };

    /// <summary>The finder's slot for an item's: the left ring counts as the right one, as both share their models.</summary>
    public static EquipSlot SlotOf(EquipSlot slot) => slot == EquipSlot.RingLeft ? EquipSlot.RingRight : slot;

    /// <summary>
    /// The sets of one kind (<paramref name="accessory"/>) in which every slot of <paramref name="wanted"/>
    /// has an item and no item of it is <paramref name="taken"/>. With <paramref name="onlyThese"/>, every other
    /// slot the set has must be taken, and it must have one. Sorted by set id.
    /// </summary>
    public static IReadOnlyList<ItemSet> Find(IEnumerable<GameItem> items, bool accessory, IReadOnlyCollection<EquipSlot> wanted, bool onlyThese,
        Func<GameItem, bool> taken)
    {
        var slots = wanted.Select(SlotOf).ToHashSet();
        if (slots.Count == 0)
            return Array.Empty<ItemSet>();

        var found = new List<ItemSet>();
        foreach (var set in items.Where(i => i.IsAccessory == accessory && i.ModelId != 0).GroupBy(i => i.ModelId))
        {
            var setSlots = set.GroupBy(i => SlotOf(i.Slot))
                .Select(g =>
                {
                    var list = g.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToArray();
                    return new SetSlot(g.Key, list, list.Any(taken));
                })
                .OrderBy(s => s.Slot)
                .ToArray();
            if (!slots.All(slot => setSlots.Any(s => s.Slot == slot && !s.Taken)))
                continue;
            if (onlyThese)
            {
                var others = setSlots.Where(s => !slots.Contains(s.Slot)).ToArray();
                if (others.Length == 0 || others.Any(s => !s.Taken))
                    continue;
            }
            found.Add(new ItemSet(accessory, set.Key, setSlots));
        }
        found.Sort((a, b) => a.SetId.CompareTo(b.SetId));
        return found;
    }
}
