using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;

namespace InstantEdit.Services.Animations.Timeline;

/// <summary>An actor (TMAC) and the ids of the tracks it plays.</summary>
internal sealed record TmbActor(short Id, ImmutableArray<short> Tracks);

/// <summary>A track (TMTR), the ids of the entries it plays, and whether a Lua condition gates it.</summary>
internal sealed record TmbTrack(short Id, ImmutableArray<short> Entries, bool Conditional);

/// <summary>
/// One timeline (TMLB) taken apart for editing. A timeline is a run of fixed-size items
/// (TMDH header, optional TMPP face pack, TMAL actor list, actors, tracks, entries) followed
/// by three areas the items point into: extra data, id lists and strings, each pointer
/// relative to its own item. Ported from the item converter's TmbTimeline and extended into
/// an editor model. Layouts follow VFXEditor's TmbFormat.
/// <para>
/// Unedited, <see cref="ToArray()"/> hands back the original bytes. Edited, the timeline is
/// written in VFXEditor's canonical layout (byte-identical to vanilla files): items grouped
/// as header, actors, tracks, entries; extra data, id lists, then strings stored once each;
/// ids renumbered from 2 with every list and every raw F-curve (TMFC) id remapped, which
/// VFXEditor itself never does.
/// </para>
/// Entries are addressed by the id they had when parsed; added ones get a free id until written.
/// </summary>
internal sealed class TmbFile
{
    private const int HeaderSize = 12;
    private const int MaxString = 512;

    /// <summary>Kinds holding a TMFC's raw entry id as an int32 at <see cref="CurveIdOffset"/>.</summary>
    public static readonly ImmutableHashSet<string> CurveUsers = ["C013", "C117", "C168", "C176", "C177", "C178"];
    public const int CurveIdOffset = 20;

    private enum Kind
    {
        /// <summary>A NUL-terminated string.</summary>
        String,
        /// <summary>Item ids (int16), counted by the int after the pointer.</summary>
        List,
        /// <summary>Floats, counted by the int after the pointer.</summary>
        Vector,
        /// <summary>A track's Lua condition: 8 bytes, then 12 per term counted at +4.</summary>
        Condition,
        /// <summary>C094's filter block, 0x14 bytes.</summary>
        Filter,
        /// <summary>TMFC's curves, from this pointer to the end pointer at +24, both relative to the item + 12.</summary>
        Curves,
    }

    private readonly record struct Layout(int Offset, Kind Kind, int Base = 8)
    {
        /// <summary>Bytes of the item the pointer occupies: the pointer, plus its count for lists and vectors.</summary>
        public int Width => Kind is Kind.List or Kind.Vector ? 8 : 4;
    }

    /// <summary>The pointers of every item that has any; every other known kind holds none.</summary>
    private static readonly Dictionary<string, Layout[]> Pointers = new(StringComparer.Ordinal)
    {
        ["TMPP"] = [new(8, Kind.String)],
        ["TMAL"] = [new(8, Kind.List)],
        ["TMAC"] = [new(20, Kind.List)],
        ["TMTR"] = [new(12, Kind.List), new(20, Kind.Condition)],
        ["TMFC"] = [new(12, Kind.Curves, 12)],
        ["C002"] = [new(24, Kind.String)],
        ["C009"] = [new(20, Kind.String)],
        ["C010"] = [new(32, Kind.String)],
        ["C012"] = [new(20, Kind.String), new(32, Kind.Vector), new(40, Kind.Vector), new(48, Kind.Vector), new(56, Kind.Vector)],
        ["C063"] = [new(20, Kind.String)],
        ["C068"] = [new(20, Kind.Vector), new(28, Kind.Vector)],
        ["C075"] = [new(24, Kind.Vector), new(32, Kind.Vector), new(40, Kind.Vector), new(48, Kind.Vector)],
        ["C093"] = [new(20, Kind.Vector), new(28, Kind.Vector)],
        ["C094"] = [new(28, Kind.Filter)],
        ["C173"] = [new(20, Kind.String)],
    };

    private static readonly Dictionary<string, int> StructuralSizes = new(StringComparer.Ordinal)
    {
        ["TMDH"] = 0x10, ["TMPP"] = 0x0C, ["TMAL"] = 0x10, ["TMAC"] = 0x1C, ["TMTR"] = 0x18, ["TMFC"] = 0x20,
    };

    /// <summary>The fixed size of an item kind, or null for a kind no reader knows.</summary>
    public static int? FixedSize(string magic) =>
        StructuralSizes.TryGetValue(magic, out var size) ? size : TmbEntrySchema.Find(magic)?.Size;

    /// <summary>Where a kind keeps its string's pointer, or -1 when it has none.</summary>
    public static int StringOffset(string magic) =>
        Pointers.TryGetValue(magic, out var layouts) && layouts.Where(l => l.Kind == Kind.String).Select(l => l.Offset).FirstOrDefault(-1) is var offset
            ? offset : -1;

    /// <summary>Where a kind keeps its vector pointers (offset, then count).</summary>
    public static ImmutableArray<int> VectorOffsets(string magic) =>
        Pointers.TryGetValue(magic, out var layouts) ? [.. layouts.Where(l => l.Kind == Kind.Vector).Select(l => l.Offset)] : [];

    private sealed class Field(Layout layout)
    {
        public Layout Layout { get; } = layout;
        /// <summary>False for a pointer the file left at zero, which stays zero.</summary>
        public bool Present { get; set; }
        public string Text { get; set; } = string.Empty;
        public List<short> Ids { get; } = [];
        public byte[] Data { get; set; } = [];
    }

    private sealed class Item(string magic, byte[] bytes)
    {
        public string Magic { get; } = magic;
        public byte[] Bytes { get; } = bytes;
        public List<Field> Fields { get; } = [];
        public short Id => BinaryPrimitives.ReadInt16LittleEndian(Bytes.AsSpan(8));
        public bool IsEntry => Magic == "TMFC" || Magic[0] == 'C';
        public Field? At(int offset) => Fields.FirstOrDefault(f => f.Layout.Offset == offset);
        public Field? String => Fields.FirstOrDefault(f => f.Layout.Kind == Kind.String);
        public Field List => Fields.First(f => f.Layout.Kind == Kind.List);
        public Field? Condition => Fields.FirstOrDefault(f => f.Layout.Kind == Kind.Condition);
    }

    /// <summary>Read access to one entry (a C-entry or TMFC). It reflects later edits.</summary>
    internal sealed class Entry
    {
        private readonly Item item;
        // Item is private to the file; the parameter type keeps the constructor's accessibility consistent.
        internal Entry(object item) => this.item = (Item)item;
        public short Id => item.Id;
        public string Magic => item.Magic;
        public int Size => item.Bytes.Length;
        /// <summary>The entry's start, in frames.</summary>
        public short Time => BinaryPrimitives.ReadInt16LittleEndian(item.Bytes.AsSpan(10));
        /// <summary>The entry's string (a path or motion name); null when its kind has none or the pointer is unset.</summary>
        public string? Text => item.String is { Present: true } s ? s.Text : null;
        public int Int32(int offset) => BinaryPrimitives.ReadInt32LittleEndian(Span(offset, 4));
        public short Int16(int offset) => BinaryPrimitives.ReadInt16LittleEndian(Span(offset, 2));
        public ushort UInt16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(Span(offset, 2));
        public byte Byte(int offset) => Span(offset, 1)[0];
        public sbyte SByte(int offset) => (sbyte)Span(offset, 1)[0];
        public float Single(int offset) => BinaryPrimitives.ReadSingleLittleEndian(Span(offset, 4));
        /// <summary>The floats a vector pointer at <paramref name="offset"/> holds; empty when unset.</summary>
        public ImmutableArray<float> Vector(int offset) => item.At(offset) is { Layout.Kind: Kind.Vector, Present: true } f
            ? [.. Enumerable.Range(0, f.Data.Length / 4).Select(i => BinaryPrimitives.ReadSingleLittleEndian(f.Data.AsSpan(i * 4)))]
            : [];
        /// <summary>The block a filter pointer at <paramref name="offset"/> holds; null when unset.</summary>
        public byte[]? Block(int offset) => item.At(offset) is { Layout.Kind: Kind.Filter, Present: true } f ? f.Data.ToArray() : null;
        private ReadOnlySpan<byte> Span(int offset, int length) =>
            offset >= 0 && offset <= item.Bytes.Length - length ? item.Bytes.AsSpan(offset, length)
                : throw new InvalidDataException($"Offset {offset} is outside the {Magic} entry.");
    }

    private readonly byte[] original;
    private readonly Item header;
    private Item? pack;
    private readonly Item actorList;
    private readonly List<Item> actors;
    private readonly List<Item> tracks;
    private readonly List<Item> entries;

    /// <summary>Whether any edit changed a byte; until then <see cref="ToArray()"/> returns the original bytes.</summary>
    public bool Edited { get; private set; }

    private TmbFile(byte[] original, Item header, Item? pack, Item actorList, List<Item> actors, List<Item> tracks, List<Item> entries)
    {
        this.original = original;
        this.header = header;
        this.pack = pack;
        this.actorList = actorList;
        this.actors = actors;
        this.tracks = tracks;
        this.entries = entries;
    }

    /// <summary>
    /// Reads the timeline at <paramref name="start"/>, which runs for the length its header gives.
    /// Throws <see cref="InvalidDataException"/> for a damaged timeline, an unknown entry kind or a
    /// known kind of an unexpected size: moving such an entry could break a pointer it holds.
    /// </summary>
    public static TmbFile Parse(byte[] bytes, int start = 0)
    {
        if (start < 0 || start > bytes.Length - HeaderSize || !bytes.AsSpan(start, 4).SequenceEqual("TMLB"u8))
            throw new InvalidDataException("Invalid animation timeline header.");
        var length = ReadInt(bytes, start + 4);
        var count = ReadInt(bytes, start + 8);
        if (length < HeaderSize || length > bytes.Length - start || count < 0 || count > (length - HeaderSize) / 8)
            throw new InvalidDataException("Invalid animation timeline size.");
        var end = start + length;

        var items = new List<(Item Item, int Position)>(count);
        var cursor = start + HeaderSize;
        for (var i = 0; i < count; i++)
        {
            if (cursor > end - 8) throw new InvalidDataException("Truncated animation timeline entry.");
            var magic = Magic(bytes, cursor);
            var size = ReadInt(bytes, cursor + 4);
            var expected = FixedSize(magic) ?? throw new InvalidDataException($"The timeline has an entry of unknown kind {magic}.");
            if (size != expected) throw new InvalidDataException($"The timeline's {magic} entry has size {size} instead of {expected}.");
            if (size > end - cursor) throw new InvalidDataException("Truncated animation timeline entry.");
            items.Add((new Item(magic, bytes[cursor..(cursor + size)]), cursor));
            cursor += size;
        }

        // Every pointer leads past the items, into the data areas.
        var data = cursor;
        long copied = 0;
        foreach (var (item, position) in items)
        {
            if (!Pointers.TryGetValue(item.Magic, out var layouts)) continue;
            foreach (var layout in layouts)
            {
                var field = new Field(layout);
                item.Fields.Add(field);
                var counted = layout.Kind is Kind.List or Kind.Vector;
                var pointer = BinaryPrimitives.ReadInt32LittleEndian(item.Bytes.AsSpan(layout.Offset));
                var number = counted ? BinaryPrimitives.ReadInt32LittleEndian(item.Bytes.AsSpan(layout.Offset + 4)) : 0;
                if (pointer == 0 && (layout.Kind != Kind.List || number == 0)) continue;
                var target = (long)position + layout.Base + pointer;
                if (target < data || target > end) throw new InvalidDataException($"Invalid {item.Magic} timeline pointer.");
                var at = (int)target;
                field.Present = true;
                switch (layout.Kind)
                {
                    case Kind.String:
                        field.Text = ReadString(bytes, at, end);
                        break;
                    case Kind.List:
                        if (number is < 0 or > 4096 || at > end - number * 2)
                            throw new InvalidDataException($"Invalid {item.Magic} timeline id list.");
                        for (var n = 0; n < number; n++) field.Ids.Add(BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(at + n * 2)));
                        break;
                    case Kind.Vector:
                        if (number is < 1 or > 16) throw new InvalidDataException($"Invalid {item.Magic} timeline vector.");
                        field.Data = Slice(bytes, at, number * 4, end);
                        break;
                    case Kind.Condition:
                        var terms = at > end - 8 ? -1 : ReadInt(bytes, at + 4);
                        if (terms is < 0 or > 1024) throw new InvalidDataException("Invalid timeline track condition.");
                        field.Data = Slice(bytes, at, 8 + 12 * terms, end);
                        break;
                    case Kind.Filter:
                        field.Data = Slice(bytes, at, 0x14, end);
                        break;
                    case Kind.Curves:
                        var last = (long)position + layout.Base + BinaryPrimitives.ReadInt32LittleEndian(item.Bytes.AsSpan(24));
                        if (last < at || last > end) throw new InvalidDataException("Invalid TMFC timeline curves.");
                        field.Data = bytes[at..(int)last];
                        break;
                }
            }
            // Every entry copies the data it points at, and a crafted timeline can point many entries at
            // one large range; real ones never share data between entries.
            copied += item.Fields.Sum(f => (long)f.Data.Length);
            if (copied > length) throw new InvalidDataException("The animation's timeline entries share data.");
        }

        // VFXEditor and the game expect the header, an optional face pack, then the actor list.
        var list = items.Select(i => i.Item).ToList();
        var packed = list.Count > 1 && list[1].Magic == "TMPP";
        var head = packed ? 3 : 2;
        if (list.Count < head || list[0].Magic != "TMDH" || list[head - 1].Magic != "TMAL" ||
            list.Skip(head).Any(i => i.Magic is "TMDH" or "TMPP" or "TMAL"))
            throw new InvalidDataException("The timeline's header items are missing or out of order.");
        var body = list.Skip(head).ToList();
        var file = new TmbFile(bytes[start..end], list[0], packed ? list[1] : null, list[head - 1],
            body.Where(i => i.Magic == "TMAC").ToList(), body.Where(i => i.Magic == "TMTR").ToList(), body.Where(i => i.IsEntry).ToList());
        file.CheckReferences();
        return file;
    }

    /// <summary>Ids are unique and every list names an item of the right kind, so renumbering can remap them all.</summary>
    private void CheckReferences()
    {
        var ids = new Dictionary<short, Item>();
        foreach (var item in actors.Concat(tracks).Concat(entries))
            if (!ids.TryAdd(item.Id, item)) throw new InvalidDataException($"The timeline has two entries with id {item.Id}.");
        bool Names(Item owner, Func<Item, bool> kind) =>
            owner.List.Ids.All(id => ids.TryGetValue(id, out var target) && kind(target));
        if (!Names(actorList, i => i.Magic == "TMAC") || actors.Any(a => !Names(a, i => i.Magic == "TMTR")) ||
            tracks.Any(t => !Names(t, i => i.IsEntry)))
            throw new InvalidDataException("The timeline's lists name a missing entry.");
    }

    // ---- Read access ----

    /// <summary>The timeline's length in frames (TMDH +12).</summary>
    public int Length => BinaryPrimitives.ReadInt16LittleEndian(header.Bytes.AsSpan(12));

    public ImmutableArray<TmbActor> Actors => [.. actors.Select(a => new TmbActor(a.Id, [.. a.List.Ids]))];

    /// <summary>Every track in item order, those no actor plays included.</summary>
    public ImmutableArray<TmbTrack> Tracks =>
        [.. tracks.Select(t => new TmbTrack(t.Id, [.. t.List.Ids], t.Condition is { Present: true }))];

    /// <summary>The C-entries and TMFCs in item order.</summary>
    public ImmutableArray<Entry> Entries => [.. entries.Select(e => new Entry(e))];

    public Entry? Find(short id) => entries.FirstOrDefault(e => e.Id == id) is { } item ? new Entry(item) : null;

    /// <summary>The first track playing the entry, or null when none does.</summary>
    public TmbTrack? TrackOf(short entryId) => Tracks.FirstOrDefault(t => t.Entries.Contains(entryId));

    // ---- Edits ----

    /// <summary>The face pack (TMPP) the timeline loads, such as "emot/laugh"; null for none. Setting null removes it.</summary>
    public string? FacePack
    {
        get => pack?.String is { Present: true, Text.Length: > 0 } s ? s.Text : null;
        set
        {
            if (value == FacePack) return;
            if (value == null)
            {
                pack = null;
                Edited = true;
                return;
            }
            if (!IsSafePackName(value)) throw new InvalidDataException($"'{value}' is not a valid face pack name.");
            if (pack == null)
            {
                pack = new Item("TMPP", Header("TMPP", 0x0C));
                pack.Fields.Add(new Field(Pointers["TMPP"][0]));
            }
            pack.String!.Present = true;
            pack.String.Text = value;
            Edited = true;
        }
    }

    /// <summary>Sets the timeline's length in frames (TMDH +12).</summary>
    public void SetLength(int frames)
    {
        if (frames is < 1 or > short.MaxValue) throw new InvalidDataException($"A timeline length of {frames} frames is out of range.");
        Write(header.Bytes.AsSpan(12, 2), b => BinaryPrimitives.WriteInt16LittleEndian(b, (short)frames));
    }

    /// <summary>Sets an entry's start, in frames.</summary>
    public void SetTime(short id, int time)
    {
        if (time is < short.MinValue or > short.MaxValue) throw new InvalidDataException($"An entry time of {time} frames is out of range.");
        Write(Get(id).Bytes.AsSpan(10, 2), b => BinaryPrimitives.WriteInt16LittleEndian(b, (short)time));
    }

    public void SetInt32(short id, int offset, int value) => Write(Plain(id, offset, 4), b => BinaryPrimitives.WriteInt32LittleEndian(b, value));
    public void SetInt16(short id, int offset, short value) => Write(Plain(id, offset, 2), b => BinaryPrimitives.WriteInt16LittleEndian(b, value));
    public void SetUInt16(short id, int offset, ushort value) => Write(Plain(id, offset, 2), b => BinaryPrimitives.WriteUInt16LittleEndian(b, value));
    public void SetByte(short id, int offset, byte value) => Write(Plain(id, offset, 1), b => b[0] = value);
    public void SetSByte(short id, int offset, sbyte value) => Write(Plain(id, offset, 1), b => b[0] = (byte)value);
    public void SetSingle(short id, int offset, float value) => Write(Plain(id, offset, 4), b => BinaryPrimitives.WriteSingleLittleEndian(b, value));

    /// <summary>Sets the entry's string: a game path or motion name in printable ASCII.</summary>
    public void SetString(short id, string text)
    {
        var item = Get(id);
        var field = item.String ?? throw new InvalidDataException($"A {item.Magic} entry has no path.");
        if (text.Length is 0 or >= 256 || text.Any(c => c is < ' ' or > '~'))
            throw new InvalidDataException($"'{text}' is not a valid timeline path.");
        if (field.Present && field.Text == text) return;
        field.Present = true;
        field.Text = text;
        Edited = true;
    }

    /// <summary>Sets the floats of the vector pointer at <paramref name="offset"/>, with its count.</summary>
    public void SetVector(short id, int offset, ReadOnlySpan<float> values)
    {
        var item = Get(id);
        var field = item.At(offset) is { Layout.Kind: Kind.Vector } f ? f : throw new InvalidDataException($"A {item.Magic} entry has no vector at {offset}.");
        if (values.Length is < 1 or > 16) throw new InvalidDataException("A timeline vector holds 1 to 16 values.");
        var data = new byte[values.Length * 4];
        for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(i * 4), values[i]);
        if (field.Present && field.Data.AsSpan().SequenceEqual(data)) return;
        field.Present = true;
        field.Data = data;
        BinaryPrimitives.WriteInt32LittleEndian(item.Bytes.AsSpan(offset + 4), values.Length);
        Edited = true;
    }

    /// <summary>Sets (or with null, unsets) the 0x14-byte block a filter pointer at <paramref name="offset"/> holds (C094).</summary>
    public void SetBlock(short id, int offset, byte[]? block)
    {
        var item = Get(id);
        var field = item.At(offset) is { Layout.Kind: Kind.Filter } f ? f : throw new InvalidDataException($"A {item.Magic} entry has no block at {offset}.");
        if (block != null && block.Length != 0x14) throw new InvalidDataException("A timeline filter block holds 20 bytes.");
        if (block == null ? !field.Present : field.Present && field.Data.AsSpan().SequenceEqual(block)) return;
        field.Present = block != null;
        field.Data = block?.ToArray() ?? [];
        Edited = true;
    }

    /// <summary>
    /// Adds a C-entry from its fixed bytes (pointers unset) on a new track after the first actor's
    /// last track, the one-entry-per-track pattern of vanilla timelines. The entry goes after the
    /// entries of the tracks before it, as VFXEditor groups them, and before any trailing TMFC.
    /// Returns the entry's id until the timeline is written.
    /// </summary>
    public short AddEntry(string magic, ReadOnlySpan<byte> fixedBytes) => AddEntry(magic, fixedBytes, null);

    /// <summary>
    /// Adds a copy of entry <paramref name="sourceId"/>: its fixed bytes and everything its pointers
    /// hold, on a new track of the actor that plays the source, so the copy plays exactly as the
    /// original does. Entries that use a curve are refused, since a copy would share it. Returns
    /// the copy's id, temporary like every model id until the file is written.
    /// </summary>
    public short CloneEntry(short sourceId)
    {
        var source = entries.FirstOrDefault(e => e.Id == sourceId && e.Magic != "TMFC")
                     ?? throw new InvalidDataException("The entry to copy is no longer in the timeline.");
        if (CurveUsers.Contains(source.Magic)) throw new InvalidDataException($"A {source.Magic} entry uses a curve and cannot be copied.");
        var track = tracks.FirstOrDefault(t => t.List.Ids.Contains(sourceId));
        var owner = track == null ? null : actors.FirstOrDefault(a => a.List.Ids.Contains(track.Id));
        var id = AddEntry(source.Magic, source.Bytes, owner);
        var copy = entries.First(e => e.Id == id);
        foreach (var field in source.Fields)
        {
            var target = copy.At(field.Layout.Offset)!;
            target.Present = field.Present;
            target.Text = field.Text;
            target.Data = field.Data.ToArray();
        }
        return id;
    }

    /// <summary>
    /// Adds a copy of entry <paramref name="sourceId"/> to the track that plays it, after that
    /// track's last entry: a run of faces on one track, as mods lay them out (19 faces on one
    /// track in NFLB's Cow Taming). Entries that use a curve, or are on no track, are refused.
    /// Returns the copy's id, temporary like every model id until the file is written.
    /// </summary>
    public short CloneOnTrack(short sourceId)
    {
        var source = entries.FirstOrDefault(e => e.Id == sourceId && e.Magic != "TMFC")
                     ?? throw new InvalidDataException("The entry to copy is no longer in the timeline.");
        if (CurveUsers.Contains(source.Magic)) throw new InvalidDataException($"A {source.Magic} entry uses a curve and cannot be copied.");
        var track = tracks.FirstOrDefault(t => t.List.Ids.Contains(sourceId))
                    ?? throw new InvalidDataException("The entry to copy is on no track.");
        var copy = new Item(source.Magic, source.Bytes.ToArray());
        foreach (var field in source.Fields)
            copy.Fields.Add(new Field(field.Layout) { Present = field.Present, Text = field.Text, Data = field.Data.ToArray() });
        BinaryPrimitives.WriteInt16LittleEndian(copy.Bytes.AsSpan(8), FreeId());
        var mates = track.List.Ids.ToHashSet();
        entries.Insert(entries.FindLastIndex(e => mates.Contains(e.Id)) + 1, copy);
        track.List.Ids.Add(copy.Id);
        Edited = true;
        return copy.Id;
    }

    private short AddEntry(string magic, ReadOnlySpan<byte> fixedBytes, Item? player)
    {
        var size = TmbEntrySchema.Find(magic)?.Size ?? throw new InvalidDataException($"Cannot add an entry of kind {magic}.");
        if (fixedBytes.Length != size) throw new InvalidDataException($"A {magic} entry is {size} bytes, not {fixedBytes.Length}.");
        var bytes = fixedBytes.ToArray();
        Encoding.ASCII.GetBytes(magic).CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), size);
        var entry = new Item(magic, bytes);
        if (Pointers.TryGetValue(magic, out var layouts))
            foreach (var layout in layouts)
            {
                entry.Fields.Add(new Field(layout));
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(layout.Offset), 0);
            }

        if (actors.Count == 0)
        {
            // A timeline without actors gets one, listed by the actor list like every other.
            var actor = new Item("TMAC", Header("TMAC", 0x1C));
            actor.Fields.Add(new Field(Pointers["TMAC"][0]));
            BinaryPrimitives.WriteInt16LittleEndian(actor.Bytes.AsSpan(8), FreeId());
            actors.Add(actor);
            actorList.List.Ids.Add(actor.Id);
        }
        var owner = player ?? actors[0];
        var trackIndex = owner.List.Ids.Count == 0 ? 0 : owner.List.Ids.Max(id => tracks.FindIndex(t => t.Id == id)) + 1;
        var track = new Item("TMTR", Header("TMTR", 0x18));
        track.Fields.AddRange(Pointers["TMTR"].Select(l => new Field(l)));
        BinaryPrimitives.WriteInt16LittleEndian(track.Bytes.AsSpan(8), FreeId());
        tracks.Insert(trackIndex, track);
        owner.List.Ids.Add(track.Id);

        // Ids are taken one at a time, each once the item holding the previous one is listed.
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(8), FreeId());
        var before = tracks.Take(trackIndex).SelectMany(t => t.List.Ids).ToHashSet();
        var index = entries.FindLastIndex(e => before.Contains(e.Id)) + 1;
        while (index > 0 && entries[index - 1].Magic == "TMFC") index--;
        entries.Insert(index, entry);
        track.List.Ids.Add(entry.Id);
        Edited = true;
        return entry.Id;
    }

    /// <summary>Removes an entry, and any track it leaves empty that has no condition.</summary>
    public void RemoveEntry(short id)
    {
        var item = Get(id);
        entries.Remove(item);
        foreach (var track in tracks.Where(t => t.List.Ids.Contains(id)).ToList())
        {
            track.List.Ids.RemoveAll(i => i == id);
            if (track.List.Ids.Count > 0 || track.Condition is { Present: true }) continue;
            tracks.Remove(track);
            foreach (var actor in actors) actor.List.Ids.RemoveAll(i => i == track.Id);
        }
        Edited = true;
    }

    /// <summary>The row times of a TMFC's curves, in frames, in data order.</summary>
    public ImmutableArray<float> CurveTimes(short tmfcId)
    {
        var (data, rows) = Curves(tmfcId);
        return [.. rows.Select(r => BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(r + 4)))];
    }

    /// <summary>Multiplies every row time of a TMFC's curves, for an entry whose duration was scaled.</summary>
    public void ScaleCurve(short tmfcId, double factor)
    {
        if (!double.IsFinite(factor) || factor <= 0) throw new InvalidDataException("A curve scale must be positive.");
        var (data, rows) = Curves(tmfcId);
        var scaled = data.ToArray();
        foreach (var row in rows)
            BinaryPrimitives.WriteSingleLittleEndian(scaled.AsSpan(row + 4),
                (float)(BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(row + 4)) * factor));
        if (scaled.AsSpan().SequenceEqual(data)) return;
        Get(tmfcId).Fields[0].Data = scaled;
        Edited = true;
    }

    /// <summary>The TMFC data and the start of each 0x18-byte row: dataCount 0x10-byte headers (row count at +12), then the rows.</summary>
    private (byte[] Data, List<int> Rows) Curves(short tmfcId)
    {
        var item = Get(tmfcId);
        if (item.Magic != "TMFC") throw new InvalidDataException($"Entry {tmfcId} is not a curve.");
        var data = item.Fields[0].Data;
        var groups = BinaryPrimitives.ReadInt32LittleEndian(item.Bytes.AsSpan(16));
        if (groups < 0 || (long)groups * 0x10 > data.Length) throw new InvalidDataException("Invalid TMFC curve data.");
        long total = 0;
        for (var g = 0; g < groups; g++) total += BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(g * 0x10 + 12));
        if (groups * 0x10 + total * 0x18 != data.Length) throw new InvalidDataException("Invalid TMFC curve data.");
        return (data, [.. Enumerable.Range(0, (int)total).Select(r => groups * 0x10 + r * 0x18)]);
    }

    private Item Get(short id) => entries.FirstOrDefault(e => e.Id == id) ?? throw new InvalidDataException($"The timeline has no entry {id}.");

    /// <summary>A plain field of an entry: past the id and time, not a pointer, not a raw F-curve id.</summary>
    private Span<byte> Plain(short id, int offset, int width)
    {
        var item = Get(id);
        if (item.Magic == "TMFC" || offset < 12 || offset > item.Bytes.Length - width ||
            item.Fields.Any(f => offset < f.Layout.Offset + f.Layout.Width && f.Layout.Offset < offset + width) ||
            CurveUsers.Contains(item.Magic) && offset < CurveIdOffset + 4 && CurveIdOffset < offset + width)
            throw new InvalidDataException($"Offset {offset} of a {item.Magic} entry cannot be set.");
        return item.Bytes.AsSpan(offset, width);
    }

    private delegate void Writer(Span<byte> bytes);

    private void Write(Span<byte> target, Writer write)
    {
        Span<byte> value = stackalloc byte[target.Length];
        target.CopyTo(value);
        write(value);
        if (value.SequenceEqual(target)) return;
        value.CopyTo(target);
        Edited = true;
    }

    private short FreeId()
    {
        var used = actors.Concat(tracks).Concat(entries).Select(i => i.Id).ToHashSet();
        for (var id = (short)(used.DefaultIfEmpty((short)1).Max() + 1); id is > 1 and < short.MaxValue; id++)
            if (!used.Contains(id)) return id;
        for (short id = 2; id < short.MaxValue; id++)
            if (!used.Contains(id)) return id;
        throw new InvalidDataException("The timeline has too many entries.");
    }

    // ---- Writing ----

    /// <summary>The original bytes when nothing was edited, else the canonical layout.</summary>
    public byte[] ToArray() => ToArray(out _);

    /// <param name="ids">Each entry's id in the output, by the id it has in this model.</param>
    public byte[] ToArray(out IReadOnlyDictionary<short, short> ids)
    {
        if (Edited) return ToCanonicalArray(out ids);
        ids = actors.Concat(tracks).Concat(entries).ToDictionary(i => i.Id, i => i.Id);
        return original.ToArray();
    }

    /// <summary>The timeline in VFXEditor's canonical layout, edited or not.</summary>
    public byte[] ToCanonicalArray() => ToCanonicalArray(out _);

    public byte[] ToCanonicalArray(out IReadOnlyDictionary<short, short> ids)
    {
        var items = new List<Item> { header };
        if (pack != null) items.Add(pack);
        items.Add(actorList);
        items.AddRange(actors);
        items.AddRange(tracks);
        items.AddRange(entries);
        var numbered = actors.Concat(tracks).Concat(entries).ToList();
        if (numbered.Count > short.MaxValue - 2) throw new InvalidDataException("The timeline has too many entries.");
        var map = new Dictionary<short, short>();
        for (var i = 0; i < numbered.Count; i++) map[numbered[i].Id] = (short)(i + 2);
        var curves = entries.Where(e => e.Magic == "TMFC").Select(e => e.Id).ToHashSet();

        var itemsLength = items.Sum(i => i.Bytes.Length);
        var extraLength = items.SelectMany(i => i.Fields).Where(f => f.Present && IsExtra(f.Layout.Kind)).Sum(f => f.Data.Length);
        var listLength = items.SelectMany(i => i.Fields).Where(f => f.Layout.Kind == Kind.List).Sum(f => f.Ids.Count * 2);
        var extraStart = HeaderSize + itemsLength;
        var listStart = extraStart + extraLength;
        var stringStart = listStart + listLength;

        var body = new MemoryStream();
        var extra = new MemoryStream();
        var lists = new MemoryStream();
        var strings = new MemoryStream();
        var written = new Dictionary<string, int>(StringComparer.Ordinal);
        var position = HeaderSize;
        foreach (var item in items)
        {
            var bytes = item.Bytes.ToArray();
            if (map.TryGetValue(item.Id, out var renumbered) && item != header && item != pack && item != actorList)
                BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(8), renumbered);
            if (CurveUsers.Contains(item.Magic))
            {
                // The game finds the curve by this raw id; it must follow the TMFC's renumbering.
                var curve = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(CurveIdOffset));
                if (curve is >= short.MinValue and <= short.MaxValue && curves.Contains((short)curve))
                    BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(CurveIdOffset), map[(short)curve]);
            }
            foreach (var field in item.Fields)
            {
                var from = position + field.Layout.Base;
                var at = field.Layout.Offset;
                if (!field.Present && field.Layout.Kind != Kind.List)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at), 0);
                    continue;
                }
                switch (field.Layout.Kind)
                {
                    case Kind.String:
                        if (!written.TryGetValue(field.Text, out var offset))
                        {
                            offset = (int)strings.Length;
                            written[field.Text] = offset;
                            strings.Write(Encoding.Latin1.GetBytes(field.Text));
                            strings.WriteByte(0);
                        }
                        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at), stringStart + offset - from);
                        break;
                    case Kind.List:
                        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at), listStart + (int)lists.Length - from);
                        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at + 4), field.Ids.Count);
                        foreach (var id in field.Ids)
                        {
                            var target = map[id];
                            lists.WriteByte((byte)target);
                            lists.WriteByte((byte)(target >> 8));
                        }
                        break;
                    default:
                        var pointer = extraStart + (int)extra.Length - from;
                        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at), pointer);
                        if (field.Layout.Kind == Kind.Curves)
                            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), pointer + field.Data.Length);
                        extra.Write(field.Data);
                        break;
                }
            }
            body.Write(bytes);
            position += bytes.Length;
        }

        var result = new MemoryStream();
        result.Write("TMLB"u8);
        result.Write(new byte[8]);
        body.WriteTo(result);
        extra.WriteTo(result);
        lists.WriteTo(result);
        strings.WriteTo(result);
        var output = result.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(4), output.Length);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(8), items.Count);
        ids = map;
        return output;
    }

    private static bool IsExtra(Kind kind) => kind is Kind.Vector or Kind.Condition or Kind.Filter or Kind.Curves;

    private static byte[] Header(string magic, int size)
    {
        var bytes = new byte[size];
        Encoding.ASCII.GetBytes(magic).CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), size);
        return bytes;
    }

    /// <summary>A face pack's name: motion-name parts per folder level ("emot/laugh"), none empty or a dot.</summary>
    public static bool IsSafePackName(string name) => name.Length is > 0 and < 256 &&
        name.Split('/').All(part => part.Length > 0 && part is not ("." or "..") &&
            part.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'));

    private static string Magic(byte[] bytes, int at) =>
        bytes.AsSpan(at, 4).ToArray().All(b => b is >= (byte)'0' and <= (byte)'9' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z')
            ? Encoding.ASCII.GetString(bytes, at, 4)
            : "0x" + Convert.ToHexString(bytes, at, 4);

    private static int ReadInt(byte[] bytes, int offset)
    {
        if (offset < 0 || offset > bytes.Length - 4) throw new InvalidDataException("Timeline offset is out of bounds.");
        return BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
    }

    private static byte[] Slice(byte[] bytes, int start, int length, int end)
    {
        if (length < 0 || start > end - length) throw new InvalidDataException("A timeline pointer runs past the timeline.");
        return bytes[start..(start + length)];
    }

    /// <summary>Strings are kept as Latin-1 so every byte survives a rewrite unchanged.</summary>
    private static string ReadString(byte[] bytes, int start, int end)
    {
        var span = bytes.AsSpan(start, Math.Min(MaxString, end - start));
        var zero = span.IndexOf((byte)0);
        if (zero < 0) throw new InvalidDataException("Unterminated animation timeline string.");
        return Encoding.Latin1.GetString(span[..zero]);
    }
}
