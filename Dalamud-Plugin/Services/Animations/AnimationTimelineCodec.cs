using System.Buffers.Binary;

namespace InstantEdit.Services.Animations;

/// <summary>
/// Retime a PAP's embedded timelines. Every event a timeline fires - footsteps,
/// sounds, effects - carries a frame time, and the animation entries carry a
/// duration and a frame range besides. Change a clip's length without these and
/// the events drift out of step with the motion.
/// <para>
/// Scaling only rewrites numeric fields, so no entry changes size and nothing has
/// to be repacked. Layouts are confirmed against two independent sources: a
/// timeline entry is magic(4) + size(4) + id(2) + time(2) before its own fields,
/// which is what puts C009's path at +20 and C010's at +32 - exactly the offsets
/// dependency discovery already reads.
/// </para>
/// </summary>
internal static class AnimationTimelineCodec
{
    /// <summary>Frame times are 16-bit, so a clip cannot be stretched past this.</summary>
    private const int MaxFrame = short.MaxValue;
    private const int TimeField = 10;

    /// <summary>
    /// Scale every event time and duration in a PAP's timelines by the same factor
    /// the clip's length changed by. Refuses outright rather than producing a
    /// partially retimed timeline: anything the reader reports a problem with, an
    /// entry whose size disagrees with its layout, an unknown entry, or a construct
    /// whose resources are chosen dynamically, blocks the whole edit.
    /// </summary>
    public static byte[] Scale(byte[] papBytes, double factor)
    {
        if (!double.IsFinite(factor) || factor <= 0 || factor > 1000)
            throw new InvalidDataException("The retime factor is invalid or out of range.");
        var before = AnimationDependencies.Read("timeline.pap", papBytes);
        if (!before.Problems.IsEmpty)
            throw new InvalidDataException(
                "This animation's timeline cannot be retimed safely: " + string.Join(" ", before.Problems));
        var pap = new AnimationPap(papBytes);
        var result = papBytes.ToArray();
        var offset = pap.TimelineOffset;
        for (var i = 0; i < pap.Entries.Length; i++)
        {
            var entries = new List<TimelineEntry>();
            var problems = new List<string>();
            var length = AnimationDependencies.CollectTimelineEntries(result, offset, entries, problems);
            if (problems.Count > 0)
                throw new InvalidDataException("This animation's timeline cannot be retimed safely: " + string.Join(" ", problems));
            foreach (var entry in entries) ScaleEntry(result, entry, factor);
            offset = checked(offset + length);
            // Inter-timeline padding aligns relative to the footer, not to zero.
            if (i + 1 < pap.Entries.Length) offset += (pap.TimelineOffset - offset) & 3;
        }
        // Sizes and strings are untouched, so the rewrite must still read identically
        // apart from the numbers it changed.
        var after = AnimationDependencies.Read("timeline.pap", result);
        if (!after.References.SequenceEqual(before.References) || !after.Problems.IsEmpty)
            throw new InvalidDataException("Retiming the animation's timeline did not round trip. The file was not changed.");
        return result;
    }

    private static void ScaleEntry(byte[] bytes, TimelineEntry entry, double factor)
    {
        switch (entry.Magic)
        {
            // The timeline header's own length is a duration - the longest time any
            // entry uses - not a byte count, so it scales with everything else.
            case "TMDH":
                if (entry.Size >= 16) ScaleShort(bytes, entry.Position + 12, factor, entry.Magic);
                return;
            // Structural entries carry byte counts and indices, never frame times.
            case "TMPP" or "TMAL" or "TMAC" or "TMTR" or "TMFC":
                return;
        }
        if (entry.Size >= TimeField + 2) ScaleShort(bytes, entry.Position + TimeField, factor, entry.Magic);
        switch (entry.Magic)
        {
            case "C009" when entry.Size >= 16:
                ScaleInt(bytes, entry.Position + 12, factor, entry.Magic);
                return;
            case "C010" when entry.Size >= 32:
                ScaleInt(bytes, entry.Position + 12, factor, entry.Magic);
                // The start and end frames are only honoured when time control is on,
                // but scaling them regardless keeps the entry self-consistent.
                ScaleFloat(bytes, entry.Position + 24, factor, entry.Magic);
                ScaleFloat(bytes, entry.Position + 28, factor, entry.Magic);
                return;
        }
    }

    private static void ScaleShort(byte[] bytes, int at, double factor, string magic)
    {
        var value = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(at));
        if (value == 0) return;
        var scaled = Math.Round(value * factor, MidpointRounding.AwayFromZero);
        if (scaled is < short.MinValue or > MaxFrame)
            throw new InvalidDataException($"Retiming pushes a {magic} frame time past what the timeline can store.");
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(at), (short)scaled);
    }

    private static void ScaleInt(byte[] bytes, int at, double factor, string magic)
    {
        var value = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at));
        if (value == 0) return;
        var scaled = Math.Round(value * factor, MidpointRounding.AwayFromZero);
        if (scaled is < int.MinValue or > int.MaxValue)
            throw new InvalidDataException($"Retiming pushes a {magic} duration past what the timeline can store.");
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at), (int)scaled);
    }

    private static void ScaleFloat(byte[] bytes, int at, double factor, string magic)
    {
        var value = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(at));
        if (!float.IsFinite(value))
            throw new InvalidDataException($"A {magic} frame bound is not a finite number.");
        if (value == 0) return;
        var scaled = (float)(value * factor);
        if (!float.IsFinite(scaled))
            throw new InvalidDataException($"Retiming pushes a {magic} frame bound past what the timeline can store.");
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at), scaled);
    }
}
