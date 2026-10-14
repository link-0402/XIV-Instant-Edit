using InstantEdit.Models;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Services.Animations;

/// <summary>What a bake request changes. No Dalamud dependencies.</summary>
internal static class AnimationBakeRules
{
    /// <summary>
    /// Whether the bake retargets onto a standard skeleton: a skeleton repair, or a timeline save
    /// that names one to repair onto.
    /// </summary>
    public static bool Repairs(AnimationBakeRequest request) =>
        request.Operation == AnimationOperation.RepairSkeleton ||
        request.Operation == AnimationOperation.EditTimeline && request.RepairTarget != null;

    /// <summary>
    /// Whether saving <paramref name="document"/> bakes the motion: a stretch, cuts, bone layers that
    /// change it, bones left out, or a repair onto <paramref name="repair"/>.
    /// </summary>
    public static bool ChangesMotion(TimelineDocument document, SkeletonStandard? repair = null) =>
        document.Stretched || document.Cut || document.MotionChanged || !document.ExcludedBones.IsEmpty || repair != null;

    /// <summary>
    /// Whether saving bakes the layers in for good: a new length, bones left out or a repair. The
    /// saved file is then no longer the motion the layers apply to plus the layers, so they can't
    /// be opened again on the motion from before.
    /// </summary>
    public static bool BakesLayersIn(TimelineDocument document, SkeletonStandard? repair = null) =>
        document.LengthChanged || !document.ExcludedBones.IsEmpty || repair != null;
}
