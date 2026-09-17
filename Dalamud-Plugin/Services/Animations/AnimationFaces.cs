using System.Collections.Immutable;

namespace InstantEdit.Services.Animations;

/// <summary>
/// A body animation names the facial motion it plays alongside itself. A standing
/// pose's PAP references its own body motion plus an external one such as
/// <c>cfxf_bad</c>, which the game resolves through the facial ActionTimeline to a
/// clip in the character's own <c>nonresident/</c> pack. Changing which face plays
/// is therefore a rename of that reference, not a new file.
/// </summary>
internal static class AnimationFaces
{
    /// <summary>
    /// The motions a PAP's timeline plays but does not itself contain. A body clip
    /// references its own entry by name too, so anything left after removing the
    /// PAP's own entries is played from elsewhere - in practice the facial motion.
    /// </summary>
    public static ImmutableArray<string> ExternalMotions(byte[] papBytes)
    {
        var pap = new AnimationPap(papBytes);
        var own = pap.Entries.Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal);
        return AnimationDependencies.Read("face.pap", papBytes).References
            .Where(reference => reference.Kind == "animation" && !own.Contains(reference.Path))
            .Select(reference => reference.Path)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();
    }

    /// <summary>
    /// Point a body animation at a different facial motion. The body motion itself
    /// is untouched, so this never reaches the baker or the live skeleton.
    /// </summary>
    public static byte[] Retarget(byte[] papBytes, string from, string to)
    {
        if (!AnimationTimelineNames.IsSafeMotionName(to))
            throw new InvalidDataException($"'{to}' is not a valid facial motion name.");
        var external = ExternalMotions(papBytes);
        if (!external.Contains(from, StringComparer.Ordinal))
            throw new InvalidDataException($"This animation does not play the facial motion '{from}'.");
        // Renaming a PAP's own body motion here would repoint the clip at itself.
        if (new AnimationPap(papBytes).Entries.Any(entry => entry.Name == to))
            throw new InvalidDataException($"'{to}' is this animation's own motion, not a facial one.");
        if (from == to) return papBytes;
        return AnimationTimelineNames.Rename(papBytes, new Dictionary<string, string> { [from] = to });
    }
}
