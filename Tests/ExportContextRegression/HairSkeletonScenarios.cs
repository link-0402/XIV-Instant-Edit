using System.Buffers.Binary;
using System.Text.Json.Nodes;
using InstantEdit.Services.Skeletons;
using static InstantEdit.TestSupport.Assertions;

/// <summary>Custom hair skeletons: free skeleton IDs, vanilla templates and the IDs the mod library uses.</summary>
internal static class HairSkeletonScenarios
{
    public static void Run(string testRoot)
    {
        IdsAvoidVanillaAndLibrary();
        TemplateIsLowestExisting();
        LibraryScan(Path.Combine(testRoot, "HairIds"));
    }

    /// <summary>An EST table's bytes: a count, the (set, gender-race) pairs, then one entry per pair.</summary>
    private static byte[] EstBytes((ushort GenderRace, ushort Set, ushort Entry)[] rows)
    {
        var bytes = new byte[4 + rows.Length * 6];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, rows.Length);
        for (var i = 0; i < rows.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4 + i * 4), rows[i].Set);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6 + i * 4), rows[i].GenderRace);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4 + rows.Length * 4 + i * 2), rows[i].Entry);
        }
        return bytes;
    }

    private static EstTable Table() => EstTable.Parse(EstBytes([(801, 1, 2), (801, 2, 3), (1401, 1, 160)]));

    private static void IdsAvoidVanillaAndLibrary()
    {
        var table = Table();
        Require(HairSkeletonIds.Vanilla(table).SetEquals([2, 3, 160]), "Hair IDs: vanilla IDs are every entry");
        var taken = new HashSet<int>(Enumerable.Range(1000, 9000).Where(id => id != 4321));
        Require(HairSkeletonIds.Pick(taken, new Random(1)) == 4321, "Hair IDs: the only free ID is picked");
        // Reject only counts data, IO and argument errors; a full range is an InvalidOperationException.
        var threw = false;
        try { HairSkeletonIds.Pick(new HashSet<int>(Enumerable.Range(1000, 9000)), new Random(1)); }
        catch (InvalidOperationException) { threw = true; }
        Require(threw, "Hair IDs: no free ID throws");
    }

    private static void TemplateIsLowestExisting()
    {
        var table = Table();
        Require(HairSkeletonIds.TemplateId(table, 801, path => path.Contains("h0003")) == 3,
            "Hair templates: missing h0002 falls through to h0003");
        Require(HairSkeletonIds.TemplateId(table, 1801, _ => true) is null,
            "Hair templates: a race without entries has no template");
    }

    private static JsonObject Est(string slot, int entry) => new()
    {
        ["Type"] = "Est",
        ["Manipulation"] = new JsonObject
        {
            ["Entry"] = entry, ["Gender"] = "Female", ["Race"] = "Miqote", ["SetId"] = 108, ["Slot"] = slot,
        },
    };

    private static void WriteJson(string folder, string name, string text)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, name), text);
    }

    private static void LibraryScan(string root)
    {
        WriteJson(Path.Combine(root, "A broken mod"), "meta.json", "{");
        WriteJson(Path.Combine(root, "Skeleton file mod"), "meta.json", new JsonObject
        {
            ["Name"] = "Skeleton file mod",
            ["DefaultData"] = new JsonObject
            {
                ["Files"] = new JsonObject
                {
                    ["chara/human/c0801/skeleton/hair/h4821/skl_c0801h4821.sklb"] = "Files\\skl_c0801h4821.sklb",
                    ["chara/human/c0801/obj/hair/h0108/model/c0801h0108_hir.mdl"] = "Files\\c0801h0108_hir.mdl",
                },
            },
        }.ToJsonString());
        WriteJson(Path.Combine(root, "Est mod"), "group_001_x.json", new JsonObject
        {
            ["Name"] = "Style",
            ["Options"] = new JsonArray(new JsonObject
            {
                ["Name"] = "Long",
                ["Manipulations"] = new JsonArray(Est("Hair", 5000), Est("Face", 6000)),
            }),
        }.ToJsonString());
        Require(HairSkeletonIds.Library(root).SetEquals([4821, 5000]),
            "Hair IDs: library IDs come from file keys and hair Est entries");
    }
}
