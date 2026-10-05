using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace InstantEdit.Services.Animations;

/// <summary>
/// The game's PAP load table: for every race and animation set, whose copy of each body PAP the game
/// loads. A race without its own copy borrows another race's, and not along the model chart: c0801
/// plays c0101's emote/dance12_loop, c0201 plays c0801's copy of most female emotes. Penumbra only
/// redirects the path the game picks, so a mod for c0801 dancing replaces c0101's file.
/// </summary>
internal sealed class PapLoadTable
{
    public const string GamePath = "chara/xls/animation/papLoadTable.plt";
    // Read from the game's loader (ffxiv_dx11.exe, 2026.09). Header: u16 entry count, u16 record
    // count, u32 (unused here), u32 folder and name string offsets. Entries (12 bytes): u32 flags, u32
    // folder, u32 name. Records (0xA8): u32 owner (gender-race | kind << 16, kind 0 human), u16 set
    // (the aNNNN folder), u16 first remap group, then one bit per group of four entries. A set bit
    // sends the group's entries to remaps (8 bytes: u32 owner, u16 set, u16 folder), in bit order.
    private const int HeaderSize = 16, EntrySize = 12, RecordSize = 0xA8, RemapSize = 8, BitsOffset = 8;
    private const int Groups = (RecordSize - BitsOffset) * 8;
    private readonly byte[] file;
    private readonly int remapStart, remapCount, folderStart;
    private readonly Dictionary<(string Folder, string Name), int> entries = [];
    private readonly Dictionary<(uint Owner, ushort Set), int> records = [];

    public PapLoadTable(byte[] file)
    {
        this.file = file;
        if (file.Length < HeaderSize) throw new InvalidDataException("The PAP load table is truncated.");
        var entryCount = BinaryPrimitives.ReadUInt16LittleEndian(file);
        var recordCount = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(2));
        var folders = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(8));
        var names = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(12));
        var recordStart = HeaderSize + entryCount * EntrySize;
        var start = recordStart + (long)recordCount * RecordSize;
        if (start > folders || folders >= names || names > file.Length || file[names - 1] != 0)
            throw new InvalidDataException("The PAP load table's sections overlap or run past its end.");
        // The remaps fill the space up to the folder names. The header's u32 at 4 is smaller (176019
        // of 179788 in 2026.09) and the monster and demihuman records use the rest.
        (remapStart, remapCount, folderStart) = ((int)start, (int)((folders - start) / RemapSize), (int)folders);
        for (var i = 0; i < entryCount; i++)
        {
            var at = HeaderSize + i * EntrySize;
            // The table lists one mount PAP twice; the first is the one a lookup by name finds.
            entries.TryAdd((Text(folderStart, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at + 4))),
                Text((int)names, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at + 8)))), i);
        }
        // Checked once here, so a lookup never reads past a section. Slots past the last entry
        // (the game pads the final group of four) name no folder (0xFFFF) and are never looked up.
        for (var i = 0; i < recordCount; i++)
        {
            var at = recordStart + i * RecordSize;
            var remap = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(at + 6)) * 4L;
            for (var group = 0; group < Groups; group++)
            {
                if ((file[at + BitsOffset + group / 8] >> (group % 8) & 1) == 0) continue;
                if (remap + 4 > remapCount) throw new InvalidDataException("A PAP load table record runs past its remaps.");
                for (var slot = 0; slot < 4 && group * 4 + slot < entryCount; slot++)
                    if (BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(remapStart + (int)(remap + slot) * RemapSize + 6)) >= names - folders)
                        throw new InvalidDataException("A PAP load table remap names a folder past its folder list.");
                remap += 4;
            }
            records.TryAdd((BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at)), BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(at + 4))), at);
        }
    }

    /// <summary>
    /// The PAP the game loads when a human requests <paramref name="path"/>, such as
    /// <c>chara/human/c0801/animation/a0001/bt_common/emote/dance12_loop.pap</c>: another race's copy
    /// when the table borrows one, else <paramref name="path"/> itself.
    /// </summary>
    public string Resolve(string path)
    {
        var parts = path.Split('/', 6);
        if (parts.Length != 6 || parts[0] != "chara" || parts[1] != "human" || parts[3] != "animation" ||
            !Code(parts[2], 'c', out var race) || !Code(parts[4], 'a', out var set) ||
            !parts[5].EndsWith(".pap", StringComparison.Ordinal))
            return path;
        var slash = parts[5].IndexOf('/');
        if (slash <= 0) return path;
        var name = parts[5][(slash + 1)..^4];
        if (!entries.TryGetValue((parts[5][..slash], name), out var entry) || !records.TryGetValue((race, set), out var record))
            return path;
        var group = entry >> 2;
        if (group >= Groups) return path;
        var bits = file.AsSpan(record + BitsOffset, Groups / 8);
        var word = BinaryPrimitives.ReadUInt64LittleEndian(bits[((group >> 6) * 8)..]);
        if ((word >> (group & 63) & 1) == 0) return path;
        var rank = BitOperations.PopCount(word & ((1UL << (group & 63)) - 1));
        for (var i = 0; i < group >> 6; i++) rank += BitOperations.PopCount(BinaryPrimitives.ReadUInt64LittleEndian(bits[(i * 8)..]));
        var at = remapStart + ((rank + BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(record + 6))) * 4 + (entry & 3)) * RemapSize;
        var owner = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at));
        // Humans only borrow from humans; any other kind would need its own path layout.
        if (owner >> 16 != 0 || (owner & 0xFFFF) > 9999) return path;
        return $"chara/human/c{owner:D4}/animation/a{BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(at + 4)):D4}/" +
            $"{Text(folderStart, BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(at + 6)))}/{name}.pap";
    }

    private static bool Code(string text, char prefix, out ushort value)
    {
        value = 0;
        return text.Length == 5 && text[0] == prefix && text.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0 && ushort.TryParse(text.AsSpan(1), out value);
    }

    private string Text(int table, uint offset)
    {
        var start = table + (long)offset;
        if (start >= file.Length) throw new InvalidDataException("A PAP load table name runs past the file.");
        var length = file.AsSpan((int)start).IndexOf((byte)0);
        if (length < 0) throw new InvalidDataException("A PAP load table name has no terminator.");
        return Encoding.ASCII.GetString(file, (int)start, length);
    }
}
