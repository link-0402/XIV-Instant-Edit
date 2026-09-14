using System.Collections.Immutable;
using InstantEdit.Models;

namespace InstantEdit.Services.Animations;

internal sealed record AnimationPapCandidate(AnimationResource Resource, AnimationPap Pap, ImmutableDictionary<int, string> Prints,
    ImmutableDictionary<int, float>? Durations = null);
internal sealed record AnimationMatch(AnimationResource Resource, AnimationPap.Entry Entry, AnimationCatalog.Timeline Timeline);

internal static class AnimationMatching
{
    public static ImmutableArray<AnimationMatch> Find(string fingerprint, IEnumerable<AnimationPapCandidate> candidates,
        IReadOnlyList<AnimationCatalog.Timeline> active, IReadOnlyDictionary<ushort, ImmutableHashSet<string>> motions) =>
        (from candidate in candidates
         from entry in candidate.Pap.Entries
         where candidate.Prints.GetValueOrDefault(entry.Binding) == fingerprint
         from timeline in active
         where AnimationCatalog.Matches(timeline, candidate.Resource.GamePath, entry.Name) ||
             motions.TryGetValue(timeline.Id, out var references) &&
             (references.Contains(entry.Name) || references.Contains(candidate.Resource.GamePath))
         select new AnimationMatch(candidate.Resource, entry, timeline)).Distinct().ToImmutableArray();
}
