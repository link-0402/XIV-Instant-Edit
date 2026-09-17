using System.Collections.Immutable;
using System.Text.RegularExpressions;
using InstantEdit.Models;

namespace InstantEdit.Services.Animations;

/// <summary>
/// Animation families the player can switch between. Each has a numbered set under
/// <c>emote/</c> and one unnumbered base member elsewhere: the standing idles are
/// joined by <c>idle.pap</c>, chair sitting by <c>sit.pap</c>, ground sitting by
/// <c>jmn.pap</c>. The base member is slot 0 of its family and swaps like any other.
/// The catalog models emote families and has no notion of a numbered slot at all.
/// </summary>
internal static partial class AnimationSlots
{
    private sealed record Family(string Key, string Label, string Prefix, string BaseName);

    private static readonly ImmutableArray<Family> Families =
    [
        new("standing", "Standing Idle", "pose", "idle"),
        new("chair", "Chair Sitting Idle", "s_pose", "sit"),
        new("ground", "Ground Sitting Idle", "j_pose", "jmn"),
    ];

    [GeneratedRegex(@"^(?<root>.+)/(?<dir>[^/]+)/(?<prefix>[A-Za-z_]*pose)(?<slot>\d{2})_(?<state>loop|start)\.pap$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex NumberedPath();

    [GeneratedRegex(@"^(?<root>.+)/(?<dir>[^/]+)/(?<name>[A-Za-z0-9_-]+)\.pap$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex BasePath();

    public const int MaxIndex = 99;
    /// <summary>Numbered slots are probed from 1 up to here; slot 0 is the base member.</summary>
    public const int MaxProbe = 15;

    /// <summary>
    /// The family a slot belongs to. A prefix the table does not know still has to
    /// be named, and there the timeline disambiguates the sitting rows that report
    /// their transition instead of their loop.
    /// </summary>
    public static string Resolve(string family, ushort timeline) =>
        Families.Any(f => f.Key == family) ? family
        : timeline is 642 or 643 ? "chair" : timeline is 653 or 654 ? "ground" : "standing";

    public static string Label(string family, ushort timeline) =>
        Families.First(f => f.Key == Resolve(family, timeline)).Label;

    public static AnimationSlot? Describe(string papPath)
    {
        if (!AnimationDependencies.SafeGamePath(papPath)) return null;
        if (NumberedPath().Match(papPath) is { Success: true } numbered)
        {
            var index = int.Parse(numbered.Groups["slot"].Value);
            if (index is < 0 or > MaxIndex) return null;
            var prefix = numbered.Groups["prefix"].Value.ToLowerInvariant();
            // An unrecognised prefix keeps its own identity rather than being folded
            // into a family it may have nothing to do with.
            var family = Families.FirstOrDefault(f => f.Prefix == prefix)?.Key ?? prefix;
            return new AnimationSlot(numbered.Groups["root"].Value, family, index,
                numbered.Groups["state"].Value.Equals("start", StringComparison.OrdinalIgnoreCase), papPath);
        }
        if (BasePath().Match(papPath) is { Success: true } baseMatch &&
            Families.FirstOrDefault(f => f.BaseName.Equals(baseMatch.Groups["name"].Value, StringComparison.OrdinalIgnoreCase)) is { } owner)
            return new AnimationSlot(baseMatch.Groups["root"].Value, owner.Key, 0, false, papPath);
        return null;
    }

    /// <summary>
    /// Every path worth probing for a family. The base member's directory is not
    /// assumed: both the resident and emote directories are offered, and discovery
    /// keeps whichever actually resolves.
    /// </summary>
    public static ImmutableArray<AnimationSlot> Candidates(AnimationSlot member)
    {
        var family = Families.FirstOrDefault(f => f.Key == member.Family);
        var result = ImmutableArray.CreateBuilder<AnimationSlot>();
        if (family != null)
            foreach (var directory in new[] { "resident", "emote" })
                result.Add(new AnimationSlot(member.Root, member.Family, 0, false,
                    $"{member.Root}/{directory}/{family.BaseName}.pap"));
        var prefix = family?.Prefix ?? member.Family;
        for (var index = 1; index <= MaxProbe; index++)
            foreach (var startup in new[] { false, true })
                result.Add(new AnimationSlot(member.Root, member.Family, index, startup,
                    $"{member.Root}/emote/{prefix}{index:D2}_{(startup ? "start" : "loop")}.pap"));
        return result.ToImmutable();
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
        if (available.Select(slot => slot.Group).Distinct(StringComparer.Ordinal).Count() != 1)
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
            // Only pair the startup when both sides have one. The base member has no
            // numbered startup, and a family whose slots ship loop-only must not gain
            // an invented startup path.
            if (byKey.TryGetValue((destination, true), out var toStart) && byKey.TryGetValue((source, true), out var fromStart))
                result.Add(new AnimationSlotSwap(toStart, fromStart, name));
        }
        if (result.Count == 0) throw new InvalidDataException("Every slot is mapped to itself, so there is nothing to swap.");
        return result.ToImmutable();
    }
}
