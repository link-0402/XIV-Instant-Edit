using System.Text;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Timeline;

/// <summary>
/// A face library's PAP built from its faces and merged Havok data, read back as the game reads it, and the prefilter
/// that picks which faces are worth sampling as possible duplicates. Merging and sampling Havok need the game.
/// </summary>
internal static class FacePapFixture
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Layout(check);
        Refusals(reject);
        Sources(check, reject);
        Comparable(check);
    }

    /// <summary>
    /// What a library takes from each source PAP: the entry's type, face flag, timeline and binding, under its new name.
    /// The Havok merge itself (AnimationNative.MergeBindings) needs the game's Havok, so it is checked in game only.
    /// </summary>
    private static void Sources(Action<bool, string> check, Action<Action, string> reject)
    {
        var pack = FacePapLayout.Build(1401, 0, [new("cfxf_base", 17, 1, Tmlb(13)), new("cfxf_smile", 18, 0, Tmlb(22))],
            Enumerable.Repeat((byte)3, 9).ToArray());
        var library = FacePapLayout.Build(1401, 0, [new("cfxf_a", 17, 1, Tmlb(12)), new("cfxf_b", 19, 1, Tmlb(17))],
            Enumerable.Repeat((byte)5, 8).ToArray());
        // Other tools' PAPs may bind an entry to another index than its own: entry 1 here plays binding 0.
        library[26 + 40 + 34] = 0;
        var (entries, merge) = FacePapBuilder.Plan([(pack, 1, "cfxf_smile"), (library, 1, "cfxf_mine"), (pack, 0, "cfxf_base_2")]);
        check(entries.Select(e => (e.Name, e.Type, e.Face)).SequenceEqual([("cfxf_smile", (short)18, 0), ("cfxf_mine", (short)19, 1), ("cfxf_base_2", (short)17, 1)]),
            "each face keeps its source entry's type and face flag under its new name");
        check(entries[0].Timeline.SequenceEqual(new AnimationPap(pack).TimelineBytes(1)) &&
              entries[1].Timeline.SequenceEqual(new AnimationPap(library).TimelineBytes(1)) &&
              entries[2].Timeline.SequenceEqual(new AnimationPap(pack).TimelineBytes(0)),
            "each face carries its source entry's own timeline");
        check(merge.Select(m => m.Binding).SequenceEqual([1, 0, 0]), "each face merges the binding its entry plays");
        check(merge[0].Havok.SequenceEqual(new AnimationPap(pack).Havok) && merge[1].Havok.SequenceEqual(new AnimationPap(library).Havok) &&
              ReferenceEquals(merge[0].Havok, merge[2].Havok), "faces of one file merge from its Havok data, read once");

        reject(() => FacePapBuilder.Plan([(pack, 0, "cfxf_a"), (library, 0, "cfxf_a")]), "two faces given one name are refused");
        reject(() => FacePapBuilder.Plan([(pack, 2, "cfxf_a")]), "an entry past the file's last is refused");
        reject(() => FacePapBuilder.Plan([(pack, -1, "cfxf_a")]), "a negative entry is refused");
        reject(() => FacePapBuilder.Plan([]), "a library of no faces is refused");
        // No file is read before the names are checked: a null file would fail differently.
        var early = false;
        try { FacePapBuilder.Plan([(null!, 0, "cfxf_a"), (null!, 0, "cfxf_a")]); }
        catch (InvalidDataException) { early = true; }
        catch (Exception) { }
        check(early, "duplicate face names are refused before any file is read");
    }

    /// <summary>Entries, new bindings, timelines and the library's race come back as built, across odd timeline sizes.</summary>
    private static void Layout(Action<bool, string> check)
    {
        FacePapLayout.Entry[] entries =
        [
            new("cfxf_base", 17, 1, Tmlb(13)), new("cfxb_blink1", 18, 1, Tmlb(22)),
            new("cfxf_smile", 17, 0, Tmlb(12)), new("cfxf_mixed", 17, 1, Tmlb(31)),
        ];
        // An odd Havok length moves the footer off a 4-byte boundary unless the layout pads it.
        var havok = Enumerable.Range(0, 37).Select(i => (byte)(i * 7 + 1)).ToArray();
        var bytes = FacePapLayout.Build(1401, 0, entries, havok);
        var pap = new AnimationPap(bytes);
        check(pap.ModelId == 1401 && pap.ModelType == 0, "a face library names the race it is built for");
        check(pap.Entries.Select(e => (e.Name, e.Type, e.Face)).SequenceEqual(entries.Select(e => (e.Name, e.Type, e.Face))),
            "a face library keeps every face's name, type and face flag in order");
        check(pap.Entries.Select(e => (int)e.Binding).SequenceEqual(Enumerable.Range(0, entries.Length)),
            "a face library binds its faces to the merged bindings in order");
        check(pap.HavokOffset == 26 + 4 * 40 && pap.Havok.Length == 38 && pap.Havok[..37].SequenceEqual(havok) && pap.Havok[37..].All(b => b == 0),
            "a face library carries the merged Havok data unchanged, zero padded up to the footer");
        check(Enumerable.Range(0, entries.Length).All(i => pap.TimelineBytes(i).SequenceEqual(entries[i].Timeline)),
            "every face's timeline reads back whole, across odd sizes");
        check(pap.TimelineOffset % 4 == 0 && bytes.Length == pap.TimelineOffset + 13 + 3 + 22 + 2 + 12 + 31,
            "timelines are padded to 4 from the footer, the last unpadded");
        // The footer starts on a 4-byte boundary of the file: 26 + 40 + 8 = 74 takes two bytes of padding.
        var single = new AnimationPap(FacePapLayout.Build(801, 1, [new("cfxf_one", 17, 1, Tmlb(17))], Enumerable.Repeat((byte)9, 8).ToArray()));
        check(single is { ModelId: 801, ModelType: 1, TimelineOffset: 76, Entries: [{ Name: "cfxf_one", Binding: 0 }] } &&
              single.TimelineBytes(0).Length == 17 && single.Havok is [9, 9, 9, 9, 9, 9, 9, 9, 0, 0], "a library of one face builds");
        var longest = new string('a', 31);
        check(new AnimationPap(FacePapLayout.Build(801, 0, [new(longest, 17, 1, Tmlb(12))], new byte[8])).Entries[0].Name == longest,
            "a 31-byte face name fits");
    }

    private static void Refusals(Action<Action, string> reject)
    {
        var havok = new byte[8];
        reject(() => FacePapLayout.Build(801, 0, [], havok), "a library with no faces is refused");
        reject(() => FacePapLayout.Build(801, 0, [new("", 17, 1, Tmlb(12))], havok), "an empty face name is refused");
        reject(() => FacePapLayout.Build(801, 0, [new(new string('a', 32), 17, 1, Tmlb(12))], havok), "a face name over 31 bytes is refused");
        // Sixteen two-byte characters are 32 bytes in UTF-8: the limit is bytes, not characters.
        reject(() => FacePapLayout.Build(801, 0, [new(new string('é', 16), 17, 1, Tmlb(12))], havok),
            "a face name over 31 UTF-8 bytes is refused");
        reject(() => FacePapLayout.Build(801, 0, [new("cfxf_a", 17, 1, Tmlb(12)), new("cfxf_a", 17, 1, Tmlb(16))], havok),
            "two faces of the same name are refused");
        var truncated = Tmlb(16)[..12];
        reject(() => FacePapLayout.Build(801, 0, [new("cfxf_a", 17, 1, truncated)], havok), "a timeline shorter than its size is refused");
        var wrongMagic = Tmlb(12); wrongMagic[0] = (byte)'X';
        reject(() => FacePapLayout.Build(801, 0, [new("cfxf_a", 17, 1, wrongMagic)], havok), "a timeline that isn't a TMLB is refused");
        reject(() => FacePapLayout.Build(801, 0, [new("cfxf_a", 17, 1, new byte[8])], havok), "a timeline too short for its header is refused");
        reject(() => FacePapLayout.Build(801, 0, [new("cfxf_a", 17, 1, Tmlb(12))], new byte[4]), "Havok data too short is refused");
        reject(() => FacePapLayout.Build(801, 4, [new("cfxf_a", 17, 1, Tmlb(12))], havok), "an unknown skeleton type is refused");
        // Two timelines just under the limit each make a file over it.
        var half = Tmlb(AnimationPap.MaxFileSize / 2);
        reject(() => FacePapLayout.Build(801, 0, [new("cfxf_a", 17, 1, half), new("cfxf_b", 17, 1, half)], havok),
            "a library over the PAP size limit is refused");
    }

    private static void Comparable(Action<bool, string> check)
    {
        check(FaceMotionMatch.Comparable(2, 0, 2, 0), "faces of the same length and blend hint are compared");
        check(!FaceMotionMatch.Comparable(2, 0, 2 + 1 / 30f, 0), "faces a frame apart are not compared");
        check(!FaceMotionMatch.Comparable(2, 0, 2, 1), "an additive face is never compared with a non-additive one");
        check(FaceMotionMatch.Comparable(2, 1, 2.004f, 1), "lengths that round to the same frames are compared");
        check(!FaceMotionMatch.Comparable(float.NaN, 0, float.NaN, 0), "faces of unknown length are not compared");
    }

    /// <summary>A whole TMLB of <paramref name="size"/> bytes: magic, size, one entry, then filler that isn't zero.</summary>
    private static byte[] Tmlb(int size)
    {
        var bytes = new byte[size];
        for (var i = 12; i < size; i++) bytes[i] = (byte)(i * 3 + 5);
        Encoding.ASCII.GetBytes("TMLB").CopyTo(bytes, 0);
        BitConverter.GetBytes(size).CopyTo(bytes, 4);
        BitConverter.GetBytes(1).CopyTo(bytes, 8);
        return bytes;
    }
}
