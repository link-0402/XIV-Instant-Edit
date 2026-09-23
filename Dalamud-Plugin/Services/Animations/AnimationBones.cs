using System.Collections.Immutable;
using FFXIVClientStructs.Havok.Common.Base.Math.QsTransform;
using InstantEdit.Models;

namespace InstantEdit.Services.Animations;

/// <summary>
/// Which bones an animation drives, and leaving chosen ones out of a rebake. A bone
/// with no transform track is not driven by the animation at all: it samples at its
/// reference pose, and whatever else moves it in game - hair, cloth or tail physics,
/// for instance - takes over again.
/// </summary>
internal static class AnimationBones
{
    /// <summary>The bones a clip's transform tracks drive, in skeleton order.</summary>
    public static ImmutableArray<string> Animated(AnimationClip clip)
    {
        if (clip.Resolution is not { Selected: { } selected } resolution) return [];
        var bones = selected.Skeleton.Bones;
        return [.. resolution.TrackBones.Where(i => i >= 0 && i < bones.Length).Distinct().Order().Select(i => bones[i].Name)];
    }

    /// <summary>Why a clip cannot be rebaked without these bones, or null when it can.</summary>
    public static string? Problem(AnimationClip clip, IReadOnlyCollection<string> excluded)
    {
        if (excluded.Count == 0) return null;
        var animated = Animated(clip);
        if (animated.IsEmpty) return "This animation's bones are unknown until its source skeleton is identified.";
        var names = animated.ToHashSet(StringComparer.Ordinal);
        var stale = excluded.Where(name => !names.Contains(name)).Order(StringComparer.Ordinal).ToArray();
        if (stale.Length > 0)
            return $"This animation does not move {string.Join(", ", stale.Select(AnimationPresentation.BoneName))}. Select the animation again.";
        var set = excluded.ToHashSet(StringComparer.Ordinal);
        return animated.All(set.Contains) ? "Keep at least one bone animated." : null;
    }

    /// <summary>
    /// An offset baked into a bone that the rebake then leaves out would be lost, and
    /// a successful bake clears every offset it baked, so the two cannot overlap.
    /// </summary>
    public static string? OffsetConflict(IReadOnlyCollection<string> excluded, IEnumerable<string> offsetBones)
    {
        if (excluded.Count == 0) return null;
        var set = excluded.ToHashSet(StringComparer.Ordinal);
        var both = offsetBones.Where(set.Contains).Distinct().Order(StringComparer.Ordinal).ToArray();
        return both.Length == 0 ? null :
            "Selected for LivePose baking but unticked under Animated Bones: " +
            $"{string.Join(", ", both.Select(AnimationPresentation.BoneName))}. Tick the bone again, or deselect its offset.";
    }

    /// <summary>Indices of the named bones in a skeleton. Names it does not have are skipped.</summary>
    public static HashSet<int> Indices(SkeletonDescription skeleton, IEnumerable<string> names)
    {
        var wanted = names.ToHashSet(StringComparer.Ordinal);
        return [.. Enumerable.Range(0, skeleton.Bones.Length).Where(i => wanted.Contains(skeleton.Bones[i].Name))];
    }

    /// <summary>
    /// Drop the excluded bones' tracks, and hold validation to what the output then
    /// samples for them: with no track, a bone rests at its reference pose.
    /// </summary>
    public static void Exclude(List<short> tracks, IReadOnlyList<hkQsTransformf[]> expected,
        ReadOnlySpan<hkQsTransformf> reference, IReadOnlySet<int> excluded)
    {
        if (excluded.Count == 0) return;
        tracks.RemoveAll(bone => excluded.Contains(bone));
        foreach (var frame in expected)
            foreach (var bone in excluded) frame[bone] = reference[bone];
    }
}
