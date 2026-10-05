using System.Buffers.Binary;
using System.Text;
using InstantEdit.Services.Animations;

/// <summary>
/// The game's PAP load table: which race's copy of a body PAP the game loads, and the listener's
/// candidate, family and startup paths for a clip borrowed from another race.
/// </summary>
internal static class PapLoadTableFixture
{
    internal sealed record Record(uint Owner, ushort Set, ushort First, int[] Groups);
    internal sealed record Remap(uint Owner, ushort Set, int Folder);

    private const string Skeleton = "chara/human/c0801/skeleton/base/b0001/skl_c0801b0001.sklb";
    private static string Path(int race, string name, int set = 1) => $"chara/human/c{race:D4}/animation/a{set:D4}/bt_common/{name}.pap";

    /// <summary>A table in the game's layout. Each record's set group bits take four remaps, from First on.</summary>
    internal static byte[] Table(string[] folders, (int Folder, string Name)[] entries, Record[] records, Remap[] remaps)
    {
        static (byte[] Bytes, int[] Offsets) Strings(IEnumerable<string> values)
        {
            var bytes = new List<byte>(); var offsets = new List<int>();
            foreach (var value in values) { offsets.Add(bytes.Count); bytes.AddRange(Encoding.ASCII.GetBytes(value)); bytes.Add(0); }
            return (bytes.ToArray(), offsets.ToArray());
        }
        var folderText = Strings(folders);
        var nameText = Strings(entries.Select(e => e.Name));
        var recordStart = 16 + entries.Length * 12;
        var remapStart = recordStart + records.Length * 0xA8;
        var folderStart = remapStart + remaps.Length * 8;
        var nameStart = folderStart + folderText.Bytes.Length;
        var file = new byte[nameStart + nameText.Bytes.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(file, (ushort)entries.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(2), (ushort)records.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), (uint)remaps.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(8), (uint)folderStart);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(12), (uint)nameStart);
        for (var i = 0; i < entries.Length; i++)
        {
            var at = 16 + i * 12;
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(at + 4), (uint)folderText.Offsets[entries[i].Folder]);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(at + 8), (uint)nameText.Offsets[i]);
        }
        for (var i = 0; i < records.Length; i++)
        {
            var at = recordStart + i * 0xA8;
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(at), records[i].Owner);
            BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(at + 4), records[i].Set);
            BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(at + 6), records[i].First);
            foreach (var group in records[i].Groups) file[at + 8 + group / 8] |= (byte)(1 << (group % 8));
        }
        for (var i = 0; i < remaps.Length; i++)
        {
            var at = remapStart + i * 8;
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(at), remaps[i].Owner);
            BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(at + 4), remaps[i].Set);
            BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(at + 6), (ushort)folderText.Offsets[remaps[i].Folder]);
        }
        folderText.Bytes.CopyTo(file, folderStart);
        nameText.Bytes.CopyTo(file, nameStart);
        return file;
    }

    /// <summary>
    /// c0801 borrows c0101's dance and pose loops (group 0, entry 2 its own startup), and c1101's far
    /// loop (entry 300: group 75, past the first 64-bit word); c0201 borrows c0801's pose startup.
    /// </summary>
    private static byte[] Sample(uint borrowedOwner = 101)
    {
        var entries = new List<(int, string)> { (0, "resident/idle"), (0, "emote/dance12_loop"), (0, "emote/dance12_start"), (0, "emote/pose01_loop"), (0, "emote/pose01_start") };
        while (entries.Count < 300) entries.Add((0, $"filler/f{entries.Count}"));
        entries.Add((0, "emote/far_loop"));
        Remap Own(uint owner) => new(owner, 1, 0);
        Remap[] remaps =
        [
            Own(801), new(borrowedOwner, 1, 0), Own(801), new(101, 1, 0),   // c0801 group 0: entries 0-3
            Own(801), Own(801), Own(801), Own(801),                          // c0801 group 70
            new(1101, 1, 0), Own(801), Own(801), Own(801),                   // c0801 group 75: entry 300 first
            new(801, 1, 0), Own(201), Own(201), Own(201),                    // c0201 group 1: entry 4 first
        ];
        return Table(["bt_common"], entries.ToArray(),
            [new(801, 1, 0, [0, 70, 75]), new(201, 1, 3, [1]), new(101, 1, 4, [])], remaps);
    }

    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        var table = new PapLoadTable(Sample());
        check(table.Resolve(Path(801, "emote/dance12_loop")) == Path(101, "emote/dance12_loop") &&
              table.Resolve(Path(801, "emote/pose01_loop")) == Path(101, "emote/pose01_loop") &&
              table.Resolve(Path(801, "emote/far_loop")) == Path(1101, "emote/far_loop"),
            "a race without its own copy loads the race the table names, counting set bits across words");
        check(table.Resolve(Path(801, "resident/idle")) == Path(801, "resident/idle") &&
              table.Resolve(Path(801, "emote/pose01_start")) == Path(801, "emote/pose01_start") &&
              table.Resolve(Path(101, "emote/dance12_loop")) == Path(101, "emote/dance12_loop"),
            "a race keeps its own copy when the table sends it to itself or leaves its group unset");
        check(table.Resolve(Path(201, "emote/pose01_start")) == Path(801, "emote/pose01_start"),
            "a race can borrow from a race below it in the model chart");
        check(table.Resolve(Path(301, "emote/dance12_loop")) == Path(301, "emote/dance12_loop") &&
              table.Resolve(Path(801, "emote/dance12_loop", 2)) == Path(801, "emote/dance12_loop", 2) &&
              table.Resolve(Path(801, "emote/unknown")) == Path(801, "emote/unknown") &&
              table.Resolve("chara/human/c0801/animation/f0002/nonresident/smile.pap") == "chara/human/c0801/animation/f0002/nonresident/smile.pap" &&
              table.Resolve("chara/monster/m0001/animation/a0001/bt_common/emote/dance12_loop.pap") == "chara/monster/m0001/animation/a0001/bt_common/emote/dance12_loop.pap" &&
              table.Resolve("chara/human/c0801/animation/a0001/emote.pap") == "chara/human/c0801/animation/a0001/emote.pap",
            "paths without a record, an entry or the human layout stay as they are");
        check(new PapLoadTable(Sample(borrowedOwner: 0x10000 | 101)).Resolve(Path(801, "emote/dance12_loop")) == Path(801, "emote/dance12_loop"),
            "a remap to another kind of model is not turned into a human path");

        var sample = Sample();
        reject(() => _ = new PapLoadTable(sample[..15]), "a truncated table is refused");
        var overrun = sample.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(overrun.AsSpan(16 + 301 * 12 + 0xA8 + 6), 4);
        reject(() => _ = new PapLoadTable(overrun), "a record whose groups run past the remaps is refused");
        var remapStart = 16 + 301 * 12 + 3 * 0xA8;
        var folder = sample.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(folder.AsSpan(remapStart + 6), 0x7FFF);
        reject(() => _ = new PapLoadTable(folder), "a remap naming a folder past the folder list is refused");
        // Entry 300 is the last: its group's other three slots are padding, as at the end of the game's table.
        var padded = sample.ToArray();
        for (var slot = 9; slot < 12; slot++) BinaryPrimitives.WriteUInt16LittleEndian(padded.AsSpan(remapStart + slot * 8 + 6), 0xFFFF);
        check(new PapLoadTable(padded).Resolve(Path(801, "emote/far_loop")) == Path(1101, "emote/far_loop"),
            "padding slots past the last entry name no folder, as in the game's table, and are accepted");

        // The listener: a c0801 player dancing plays c0101's loop, and its family is still the player's.
        string[] tree = [Skeleton, "chara/equipment/e0001/animation/a0001/material.pap"];
        var dance = new AnimationCatalog.Timeline(100, "emote/dance12_loop", "Popotostep", 2, true, [100, 101], [101]);
        var start = dance with { Id = 101, Key = "emote/dance12_start", Loop = false };
        check(AnimationCatalog.CandidatePapPaths(dance, tree, table).SequenceEqual([Path(101, "emote/dance12_loop")]) &&
              AnimationCatalog.CandidatePapPaths(dance, tree).SequenceEqual([Path(801, "emote/dance12_loop")]),
            "the listener looks for the copy the game loads, and the player's own path without a table");
        check(AnimationCatalog.PapPath(start, Path(101, "emote/dance12_loop"), tree, table) == Path(801, "emote/dance12_start") &&
              AnimationCatalog.PapPath(dance, Path(101, "emote/dance12_loop"), tree, table) == Path(101, "emote/dance12_loop"),
            "a borrowed loop's startup is the player's race's request, not the lending race's");
        check(AnimationCatalog.SiblingStartupPath(Path(101, "emote/dance12_loop"), tree, table) == Path(801, "emote/dance12_start") &&
              AnimationCatalog.SiblingStartupPath(Path(101, "emote/pose01_loop"), [Skeleton.Replace("c0801", "c0201")], table) == Path(801, "emote/pose01_start") &&
              AnimationCatalog.SiblingStartupPath(Path(101, "emote/dance12_loop")) == Path(101, "emote/dance12_start"),
            "a borrowed idle loop's sibling startup is requested by the player's race and can come from a third race");
    }
}
