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
         where Plays(timeline, candidate, entry, motions.GetValueOrDefault(timeline.Id))
         select new AnimationMatch(candidate.Resource, entry, timeline)).Distinct().ToImmutableArray();

    private static bool Plays(AnimationCatalog.Timeline timeline, AnimationPapCandidate candidate, AnimationPap.Entry entry,
        ImmutableHashSet<string>? references)
    {
        if (references != null && (references.Contains(entry.Name) || references.Contains(candidate.Resource.GamePath))) return true;
        if (!AnimationCatalog.Matches(timeline, candidate.Resource.GamePath, entry.Name)) return false;
        // move_a and move_b hold every walk, run, sprint and jump clip. A movement row plays only the
        // clip its TMB names; the whole file counts only when the TMB names none of its clips.
        return !AnimationCatalog.IsMovement(timeline) || references == null ||
            !candidate.Pap.Entries.Any(other => references.Contains(other.Name));
    }
}
