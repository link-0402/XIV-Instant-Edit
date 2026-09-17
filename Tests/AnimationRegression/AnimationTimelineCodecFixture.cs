using System.Buffers.Binary;
using System.Text;
using InstantEdit.Services.Animations;

internal static class AnimationTimelineCodecFixture
{
    /// <summary>
    /// A PAP whose single timeline carries a header, a footstep and an animation
    /// entry, so retiming has a duration, a frame time and a frame range to move.
    /// Entry layout is magic(4) + size(4) + id(2) + time(2) before its own fields.
    /// </summary>
    private static byte[] Pap(short headerLength = 120, short eventTime = 30,
        int duration = 50, short animationTime = 10, float start = 4, float end = 44,
        string magic = "C063", int eventSize = 32)
    {
        static void Entry(BinaryWriter w, string magic, int size, short time, Action<BinaryWriter> body)
        {
            w.Write(Encoding.ASCII.GetBytes(magic)); w.Write(size);
            w.Write((short)1); w.Write(time); body(w);
        }
        using var timeline = new MemoryStream();
        using (var w = new BinaryWriter(timeline, Encoding.UTF8, true))
        {
            w.Write(Encoding.ASCII.GetBytes("TMLB")); w.Write(0); w.Write(3);
            Entry(w, "TMDH", 16, 0, x => { x.Write(headerLength); x.Write((short)3); });
            // Body is all zeroes, so the entry's own path field stays unset and adds
            // no reference of its own.
            Entry(w, magic, eventSize, eventTime, x => x.Write(new byte[eventSize - 12]));
            Entry(w, "C010", 40, animationTime, x =>
            {
                // The path displacement is relative to the entry's position + 8, and
                // the motion name follows the last entry: 40 - 8 = 32.
                x.Write(duration); x.Write(0); x.Write(1); x.Write(start); x.Write(end);
                x.Write(32); x.Write(0);
            });
            var text = Encoding.UTF8.GetBytes("cbem_pose01_2lp\0");
            w.Write(text);
        }
        var body = timeline.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4), body.Length);

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("pap "u8); writer.Write(0x20001); writer.Write((short)1);
        writer.Write((short)0x0801); writer.Write((byte)0); writer.Write((byte)0);
        var offsets = stream.Position;
        writer.Write(0); writer.Write(0); writer.Write(0);
        var info = (int)stream.Position;
        var name = Encoding.UTF8.GetBytes("cbem_pose01_2lp");
        writer.Write(name); writer.Write(new byte[32 - name.Length]);
        writer.Write((short)7); writer.Write((short)0); writer.Write(0);
        var havok = (int)stream.Position;
        writer.Write(new byte[] { 0x57, 0xE0, 0xE0, 0x57, 0x10, 0xC0, 0xC0, 0x10 });
        var timelineOffset = (int)stream.Position;
        writer.Write(body);
        stream.Position = offsets;
        writer.Write(info); writer.Write(havok); writer.Write(timelineOffset);
        return stream.ToArray();
    }

    private static (short Header, short Footstep, int Duration, short AnimationTime, float Start, float End) Read(byte[] pap)
    {
        var entries = new List<TimelineEntry>();
        var problems = new List<string>();
        AnimationDependencies.CollectTimelineEntries(pap, new AnimationPap(pap).TimelineOffset, entries, problems);
        var header = entries.Single(e => e.Magic == "TMDH");
        var footstep = entries.Single(e => e.Magic is "C042" or "C063");
        var animation = entries.Single(e => e.Magic == "C010");
        return (BinaryPrimitives.ReadInt16LittleEndian(pap.AsSpan(header.Position + 12)),
            BinaryPrimitives.ReadInt16LittleEndian(pap.AsSpan(footstep.Position + 10)),
            BinaryPrimitives.ReadInt32LittleEndian(pap.AsSpan(animation.Position + 12)),
            BinaryPrimitives.ReadInt16LittleEndian(pap.AsSpan(animation.Position + 10)),
            BinaryPrimitives.ReadSingleLittleEndian(pap.AsSpan(animation.Position + 24)),
            BinaryPrimitives.ReadSingleLittleEndian(pap.AsSpan(animation.Position + 28)));
    }

    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        // C063 (Sound) carries a time but no dynamic resource selection, so it is
        // retimeable; C042 (Footstep) is on the blocking list.
        var pap = Pap();
        var original = Read(pap);
        check(original == (120, 30, 50, 10, 4f, 44f),
            "the fixture timeline decodes its header length, event time, duration and frame range");

        // A no-op scale must be byte-identical, the same discipline the bake pipeline
        // applies before it trusts a Havok round trip.
        check(AnimationTimelineCodec.Scale(pap, 1).SequenceEqual(pap),
            "retiming by a factor of one is byte-identical");

        var doubled = AnimationTimelineCodec.Scale(pap, 2);
        check(Read(doubled) == (240, 60, 100, 20, 8f, 88f) && doubled.Length == pap.Length,
            "retiming scales the header length, every event time, the duration and the frame range together");
        var halved = AnimationTimelineCodec.Scale(pap, 0.5);
        check(Read(halved) == (60, 15, 25, 5, 2f, 22f),
            "retiming a clip shorter scales the same fields down");
        check(Read(AnimationTimelineCodec.Scale(pap, 1.0 / 3)) is { Footstep: 10, AnimationTime: 3 },
            "a non-integer factor rounds frame times rather than truncating them");

        check(AnimationDependencies.Read("x.pap", doubled).References
                .SequenceEqual(AnimationDependencies.Read("x.pap", pap).References),
            "retiming leaves every motion and resource reference exactly as it was");

        // Fail closed rather than retime a timeline that cannot be modelled whole.
        reject(() => AnimationTimelineCodec.Scale(Pap(magic: "C042", eventSize: 28), 2),
            "a timeline with a dynamically selected resource refuses to retime");
        reject(() => AnimationTimelineCodec.Scale(Pap(magic: "ZZZZ"), 2),
            "a timeline with an unknown entry refuses to retime");
        reject(() => AnimationTimelineCodec.Scale(pap, 0), "a zero retime factor is rejected");
        reject(() => AnimationTimelineCodec.Scale(pap, -1), "a negative retime factor is rejected");
        reject(() => AnimationTimelineCodec.Scale(pap, double.NaN), "a non-finite retime factor is rejected");
        reject(() => AnimationTimelineCodec.Scale(pap, 2000), "an absurd retime factor is rejected before it reaches the file");
        reject(() => AnimationTimelineCodec.Scale(Pap(eventTime: 30000), 4),
            "a retime that would push an event past the timeline's 16-bit frame limit is refused");

        // An entry size that disagrees with its layout means the entry is not the
        // shape the writer thinks it is.
        var malformed = pap.ToArray();
        var animationEntry = new List<TimelineEntry>();
        AnimationDependencies.CollectTimelineEntries(malformed, new AnimationPap(malformed).TimelineOffset, animationEntry, []);
        BinaryPrimitives.WriteInt32LittleEndian(
            malformed.AsSpan(animationEntry.Single(e => e.Magic == "C010").Position + 4), 36);
        reject(() => AnimationTimelineCodec.Scale(malformed, 2),
            "an entry whose size disagrees with its known layout refuses to retime");
    }
}
