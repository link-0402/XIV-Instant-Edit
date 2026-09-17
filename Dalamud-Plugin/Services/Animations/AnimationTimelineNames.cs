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
    /// Rewrite motion names inside a PAP's concatenated timelines, in place.
    /// Slot names differ only by their digits, so the replacement is always the
    /// same length and no string table or entry offset has to move. A different
    /// length is refused rather than repacked, which is the full codec's job.
    /// </summary>
    public static byte[] Rename(byte[] papBytes, IReadOnlyDictionary<string, string> renames)
    {
        if (renames.Count == 0) throw new InvalidDataException("No timeline motion rename was supplied.");
        foreach (var (from, to) in renames)
        {
            if (from.Length == 0 || to.Length == 0)
                throw new InvalidDataException("A timeline motion name cannot be empty.");
            if (from.Length != to.Length)
                throw new InvalidDataException($"Renaming motion '{from}' to '{to}' would change the timeline's string length.");
            if (!IsSafeMotionName(to))
                throw new InvalidDataException($"'{to}' is not a valid timeline motion name.");
        }
        var pap = new AnimationPap(papBytes);
        var result = papBytes.ToArray();
        var sites = new List<TimelineString>();
        var offset = pap.TimelineOffset;
        for (var i = 0; i < pap.Entries.Length; i++)
        {
            var length = AnimationDependencies.CollectTimelineStrings(result, offset, sites);
            offset = checked(offset + length);
            // Inter-timeline padding aligns relative to the footer, not to zero.
            if (i + 1 < pap.Entries.Length) offset += (pap.TimelineOffset - offset) & 3;
        }
        var replaced = 0;
        foreach (var site in sites)
        {
            if (site.Magic is not ("C009" or "C010")) continue;
            if (!renames.TryGetValue(site.Value, out var name)) continue;
            // ASCII by construction: SafeGamePath admits no wider characters, so the
            // byte length matches the string length and the terminator stays put.
            for (var i = 0; i < name.Length; i++) result[site.Position + i] = (byte)name[i];
            replaced++;
        }
        if (replaced == 0)
            throw new InvalidDataException("The animation's timeline does not reference the motion being renamed.");
        _ = new AnimationPap(result);
        return result;
    }
}
