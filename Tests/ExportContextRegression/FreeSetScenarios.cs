using InstantEdit.Studio.Models;
using InstantEdit.Studio.Services;
using InstantEdit.Studio.Services.Library;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// The item browser's modded items and free set finder: which mods of the player's collection change
/// an item, reading again after changes, and which item sets have the slots asked for free.
/// </summary>
internal static class FreeSetScenarios
{
    public static void Run()
    {
        ChangedItemsComeFromEnabledModsAndActiveOptions();
        ReadsSettleAndRetry();
        FreeSetsHaveEverySlotAskedFor();
    }

    private static IReadOnlyList<(string, ChangeKind)> Items(params string[] names) => names.Select(n => (n, ChangeKind.Item)).ToArray();

    private static void ChangedItemsComeFromEnabledModsAndActiveOptions()
    {
        var enabled = new Dictionary<string, bool> { ["a"] = true, ["b"] = true, ["off"] = false };
        var modItems = new Dictionary<string, IReadOnlyList<(string Name, ChangeKind Kind)>>
        {
            ["a"]   = Items("Cotton Shirt", "Hempen Hat"),
            ["b"]   = new[] { ("Cotton Shirt", ChangeKind.Item), ("Customization: Hyur Male Face 1", ChangeKind.Face), ("Silk­en Gloves", ChangeKind.Item) },
            ["off"] = Items("Leather Boots"),
        };
        var names = new Dictionary<string, string> { ["a"] = "Zeta Mod", ["b"] = "Alpha Mod" };

        var all = OccupiedItems.Build(null, enabled, modItems, names);
        Require(all.ContainsKey("Cotton Shirt") && all["Cotton Shirt"].Select(m => m.Name).SequenceEqual(new[] { "Alpha Mod", "Zeta Mod" }),
            "an item lists every enabled mod that changes it, sorted by name");
        Require(!all.ContainsKey("Leather Boots"), "a mod that is off changes nothing");
        Require(!all.ContainsKey("Customization: Hyur Male Face 1"), "only items count, not faces or hair");
        Require(all.ContainsKey("Silken Gloves"), "soft hyphens in a name are ignored");

        var active = OccupiedItems.Build(new[] { "Cotton Shirt", "Silken Gloves" }, enabled, modItems, names);
        Require(!active.ContainsKey("Hempen Hat") && active.ContainsKey("Cotton Shirt"),
            "with the collection's own list, an item only an option that is off touches stays free");

        var unnamed = OccupiedItems.Build(null, enabled, modItems, null);
        Require(unnamed["Hempen Hat"].Single().Name == "a", "a mod without a name from Penumbra is shown by its folder");
    }

    private static void ReadsSettleAndRetry()
    {
        var now = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
        var collection = (Guid.NewGuid(), "Main");
        IReadOnlyDictionary<string, IReadOnlyList<(string Name, ChangeKind Kind)>>? modItems =
            new Dictionary<string, IReadOnlyList<(string Name, ChangeKind Kind)>> { ["mine"] = Items("Cotton Shirt"), ["other"] = Items("Hempen Hat") };
        IReadOnlyDictionary<string, bool>? enabled = new Dictionary<string, bool> { ["mine"] = true, ["other"] = true };
        int reads = 0;
        var occupied = new OccupiedItems(_ => null, _ => { reads++; return enabled; }, () => modItems,
            () => new Dictionary<string, string> { ["mine"] = "My Mod", ["other"] = "Other Mod" }, () => now);

        Require(!occupied.Ready, "nothing is known before the first read");
        occupied.Need(collection);
        occupied.Idle.Wait();
        Require(occupied.Ready && occupied.IsChanged("Cotton Shirt") && occupied.CollectionName == "Main", "the first read is done right away");
        Require(!occupied.IsChanged("Cotton Shirt", except: "mine") && occupied.IsChanged("Hempen Hat", except: "mine"),
            "the mod open in Edit mode doesn't count against its own items");

        occupied.Need(collection);
        occupied.Idle.Wait();
        Require(reads == 1, "nothing is read again while nothing changed");

        enabled = new Dictionary<string, bool> { ["mine"] = false, ["other"] = true };
        occupied.Invalidate();
        occupied.Need(collection);
        occupied.Idle.Wait();
        Require(reads == 1 && occupied.IsChanged("Cotton Shirt"), "settings are read again only once they settle");
        now += OccupiedItems.Settle;
        occupied.Need(collection);
        occupied.Idle.Wait();
        Require(reads == 2 && !occupied.IsChanged("Cotton Shirt"), "after they settle the new settings show");

        modItems = null;
        occupied.Invalidate();
        now += OccupiedItems.Settle;
        occupied.Need(collection);
        occupied.Idle.Wait();
        Require(occupied.Unknown && occupied.Ready && occupied.IsChanged("Hempen Hat"), "without an answer from Penumbra the last result stays");
        int before = reads;
        occupied.Need(collection);
        occupied.Idle.Wait();
        Require(reads == before, "Penumbra isn't asked again at once");
        modItems = new Dictionary<string, IReadOnlyList<(string Name, ChangeKind Kind)>> { ["other"] = Items("Hempen Hat", "Cotton Shirt") };
        now += OccupiedItems.Retry;
        occupied.Need(collection);
        occupied.Idle.Wait();
        Require(!occupied.Unknown && occupied.IsChanged("Cotton Shirt"), "it is asked again after a while");

        var other = (Guid.NewGuid(), "Other");
        occupied.Need(other);
        Require(!occupied.Ready || occupied.CollectionName == "Other", "another collection's items aren't shown while it is read");
        occupied.Idle.Wait();
        Require(occupied.CollectionName == "Other", "a new collection is read right away");
    }

    private static GameDataService.GameItem Item(uint row, ushort set, EquipSlot slot, string name)
        => new() { RowId = row, Name = name, ModelId = set, Variant = 1, Slot = slot, IsAccessory = SlotInfo.IsAccessory(slot) };

    private static void FreeSetsHaveEverySlotAskedFor()
    {
        var items = new[]
        {
            Item(1, 1, EquipSlot.Head, "Set1 Hat"), Item(2, 1, EquipSlot.Body, "Set1 Shirt"), Item(3, 1, EquipSlot.Hands, "Set1 Gloves"),
            Item(4, 2, EquipSlot.Head, "Set2 Hat"), Item(5, 2, EquipSlot.Body, "Set2 Shirt"), Item(6, 2, EquipSlot.Body, "Set2 Shirt B"),
            Item(7, 3, EquipSlot.Body, "Set3 Shirt"),
            Item(8, 1, EquipSlot.RingRight, "Ring R"), Item(9, 1, EquipSlot.RingLeft, "Ring L"), Item(10, 1, EquipSlot.Neck, "Necklace"),
        };
        var taken = new HashSet<string> { "Set2 Shirt B", "Necklace" };
        bool Taken(GameDataService.GameItem i) => taken.Contains(i.Name);
        IEnumerable<ushort> Ids(IReadOnlyList<ItemSet> sets) => sets.Select(s => s.SetId);

        Require(Ids(FreeSets.Find(items, false, new[] { EquipSlot.Head }, false, Taken)).SequenceEqual(new ushort[] { 1, 2 }),
            "every set with a free item in the slot is found, by set id");
        Require(Ids(FreeSets.Find(items, false, new[] { EquipSlot.Head, EquipSlot.Body }, false, Taken)).SequenceEqual(new ushort[] { 1 }),
            "a slot with one modded item isn't free");
        Require(!Ids(FreeSets.Find(items, false, new[] { EquipSlot.Head }, false, Taken)).Contains((ushort)3),
            "a set without the slot isn't found");
        Require(Ids(FreeSets.Find(items, false, new[] { EquipSlot.Head }, true, Taken)).SequenceEqual(new ushort[] { 2 }),
            "only these free: every other slot of the set is modded");
        Require(FreeSets.Find(items, false, new[] { EquipSlot.Body }, true, _ => false).Count == 0,
            "only these free: a set with no other slot isn't found");
        Require(FreeSets.Find(items, false, Array.Empty<EquipSlot>(), false, Taken).Count == 0, "with no slot ticked nothing is found");

        var rings = FreeSets.Find(items, true, new[] { EquipSlot.RingLeft }, true, Taken);
        Require(rings.Count == 1 && rings[0].IdDisplay == "a0001" && rings[0].Slots.Count(s => s.Slot == EquipSlot.RingRight) == 1
                && rings[0].Slots.Single(s => s.Slot == EquipSlot.RingRight).Items.Count == 2,
            "both rings are one slot, and accessories are counted apart from gear of the same id");
    }
}
