using System.Buffers.Binary;

namespace InstantEdit.Services.Animations;

/// <summary>
/// Retarget a PAP's motion names. Moving a clip into another slot takes three
/// coordinated changes: the file lands at the destination game path, the PAP entry
/// is renamed, and the embedded timeline's C009/C010 motion path is renamed to
/// match. Miss the third and the destination timeline resolves nothing.
/// </summary>
internal static class AnimationTimelineNames
{
    /// <summary>
    /// A motion name identifies a clip inside a PAP, so unlike a dependency path it
    /// carries no separators. SafeGamePath admits '/' and would let one through.
    /// </summary>
    public static bool IsSafeMotionName(string name) => name.Length is > 0 and < 256 &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');

    /// <summary>
    /// Rewrite motion names inside a PAP's concatenated timelines.
    /// <para>
    /// A name that fits where the old one lived is written in place. A longer one is
    /// appended to the end of its own timeline and the referencing entry is re-pointed
    /// at it, which leaves every existing byte and every other displacement where it
    /// was - displacements are relative to their own entry, so appending cannot
    /// invalidate one. Swapping a numbered slot with its family's base member needs
    /// this: <c>jmn</c> and <c>cbem_pose03_2lp</c> are nothing like the same length.
    /// </para>
    /// </summary>
    public static byte[] Rename(byte[] papBytes, IReadOnlyDictionary<string, string> renames)
    {
        if (renames.Count == 0) throw new InvalidDataException("No timeline motion rename was supplied.");
        foreach (var (from, to) in renames)
        {
            if (from.Length == 0) throw new InvalidDataException("A timeline motion name cannot be empty.");
            if (!IsSafeMotionName(to)) throw new InvalidDataException($"'{to}' is not a valid timeline motion name.");
        }
        var pap = new AnimationPap(papBytes);
        var footer = new MemoryStream();
        var replaced = 0;
        var offset = pap.TimelineOffset;
        for (var i = 0; i < pap.Entries.Length; i++)
        {
            var sites = new List<TimelineString>();
            var length = AnimationDependencies.CollectTimelineStrings(papBytes, offset, sites);
            var timeline = papBytes.AsSpan(offset, length).ToArray();
            var rewritten = new MemoryStream();
            rewritten.Write(timeline);
            foreach (var site in sites)
            {
                if (site.Magic is not ("C009" or "C010")) continue;
                if (!renames.TryGetValue(site.Value, out var name)) continue;
                replaced++;
                // Positions from the shared parser are absolute in the PAP; inside
                // this timeline they are relative to where it starts.
                var local = site.Position - offset;
                if (name.Length <= site.Value.Length)
                {
                    // ASCII by construction, so byte length matches string length.
                    for (var c = 0; c < name.Length; c++) rewritten.GetBuffer()[local + c] = (byte)name[c];
                    rewritten.GetBuffer()[local + name.Length] = 0;
                    continue;
                }
                var appended = (int)rewritten.Length;
                foreach (var c in name) rewritten.WriteByte((byte)c);
                rewritten.WriteByte(0);
                BinaryPrimitives.WriteInt32LittleEndian(
                    rewritten.GetBuffer().AsSpan(site.Field - offset), appended - (site.Anchor - offset));
            }
            // The timeline's own length header must cover anything appended to it.
            var bytes = rewritten.ToArray();
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), bytes.Length);
            offset = checked(offset + length);
            if (i + 1 < pap.Entries.Length)
            {
                // Inter-timeline padding aligns relative to the footer, not to zero.
                var padding = (pap.TimelineOffset - offset) & 3;
                offset += padding;
                footer.Write(bytes);
                footer.Write(new byte[(pap.TimelineOffset - (pap.TimelineOffset + (int)footer.Length)) & 3]);
            }
            else footer.Write(bytes);
        }
        if (replaced == 0)
            throw new InvalidDataException("The animation's timeline does not reference the motion being renamed.");
        var result = new byte[checked(pap.TimelineOffset + (int)footer.Length)];
        if (result.Length > AnimationPap.MaxFileSize) throw new InvalidDataException("The renamed PAP exceeds the size limit.");
        papBytes.AsSpan(0, pap.TimelineOffset).CopyTo(result);
        footer.ToArray().CopyTo(result, pap.TimelineOffset);
        _ = new AnimationPap(result);
        return result;
    }
}
