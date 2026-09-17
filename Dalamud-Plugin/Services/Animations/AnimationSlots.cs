using System.Collections.Immutable;
using System.Text.RegularExpressions;
using InstantEdit.Models;

namespace InstantEdit.Services.Animations;

/// <summary>
/// Slot identity is derived from the PAP path rather than an enumerated table, so a
/// prefix the catalog does not know about still groups correctly. The catalog models
/// emote families and has no notion of a numbered slot at all.
/// </summary>
internal static partial class AnimationSlots
{
    [GeneratedRegex(@"^(?<dir>.+)/(?<prefix>[A-Za-z_]*pose)(?<slot>\d{2})_(?<state>loop|start)\.pap$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SlotPath();

    public const int MaxIndex = 99;

    public static AnimationSlot? Describe(string papPath)
    {
        if (!AnimationDependencies.SafeGamePath(papPath)) return null;
        var match = SlotPath().Match(papPath);
        if (!match.Success) return null;
        var index = int.Parse(match.Groups["slot"].Value);
        if (index is < 0 or > MaxIndex) return null;
        return new AnimationSlot(match.Groups["dir"].Value, match.Groups["prefix"].Value.ToLowerInvariant(), index,
            match.Groups["state"].Value.Equals("start", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Renumber a motion name from one slot to another, e.g. <c>cbem_pose03_2lp</c>
    /// to <c>cbem_pose06_2lp</c>. Returns null when the source number does not appear
    /// exactly once, because guessing which digits to rewrite would silently produce
    /// a name the destination timeline cannot resolve.
    /// </summary>
    public static string? Renumber(string motionName, int from, int to)
    {
        if (motionName.Length == 0 || from is < 0 or > MaxIndex || to is < 0 or > MaxIndex) return null;
        var source = from.ToString("D2");
        var first = motionName.IndexOf(source, StringComparison.Ordinal);
        if (first < 0 || motionName.IndexOf(source, first + 1, StringComparison.Ordinal) >= 0) return null;
        var renamed = string.Concat(motionName.AsSpan(0, first), to.ToString("D2"), motionName.AsSpan(first + source.Length));
        return AnimationTimelineNames.IsSafeMotionName(renamed) ? renamed : null;
    }

    /// <summary>
    /// Turn a destination-to-source slot mapping into the swaps to perform. Identity
    /// mappings are dropped: rewriting a slot with itself would package a file that
    /// changes nothing. A loop carries its paired startup so the two never disagree.
    /// </summary>
    public static ImmutableArray<AnimationSlotSwap> Plan(IReadOnlyCollection<AnimationSlot> available,
        IEnumerable<(int Destination, int Source)> mapping, Func<AnimationSlot, string> option)
    {
        if (available.Count == 0) throw new InvalidDataException("No animation slots were discovered to swap between.");
        var groups = available.Select(slot => slot.Group).Distinct(StringComparer.Ordinal).ToArray();
        if (groups.Length != 1)
            throw new InvalidDataException("A slot swap must stay inside one animation family.");
        var byKey = available.ToDictionary(slot => (slot.Index, slot.Startup));
        var seen = new HashSet<int>();
        var result = ImmutableArray.CreateBuilder<AnimationSlotSwap>();
        foreach (var (destination, source) in mapping)
        {
            if (!seen.Add(destination))
                throw new InvalidDataException($"Slot {destination} is mapped more than once.");
            if (destination == source) continue;
            if (!byKey.TryGetValue((destination, false), out var to))
                throw new InvalidDataException($"Slot {destination} is not part of this animation family.");
            if (!byKey.TryGetValue((source, false), out var from))
                throw new InvalidDataException($"Slot {source} is not part of this animation family.");
            var name = option(from);
            result.Add(new AnimationSlotSwap(to, from, name));
            // Only pair the startup when both sides have one; a family whose slots
            // ship loop-only must not gain an invented startup path.
            if (byKey.TryGetValue((destination, true), out var toStart) && byKey.TryGetValue((source, true), out var fromStart))
                result.Add(new AnimationSlotSwap(toStart, fromStart, name));
        }
        if (result.Count == 0) throw new InvalidDataException("Every slot is mapped to itself, so there is nothing to swap.");
        return result.ToImmutable();
    }
}
