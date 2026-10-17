using System.Collections.Immutable;
using System.Numerics;
using InstantEdit.Models;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;

namespace InstantEdit.Services.Animations;

/// <summary>What a bake request changes. No Dalamud dependencies.</summary>
internal static class AnimationBakeRules
{
    /// <summary>
    /// The other clip of a loop and its startup that a timeline save to a new mod carries along unchanged:
    /// the startup when <paramref name="edited"/> is the loop, the loop when it is the startup (<paramref name="startup"/>),
    /// as long as the collection gives both from the same mod and they are separate files. Without it the new
    /// mod would pair the edited clip with the game's own other half once the original mod is off. Null when
    /// nothing comes along: saving in place, a clip without a startup, or one half from the game or another mod.
    /// </summary>
    public static string? CompanionClip(AnimationCapture capture, bool startup, AnimationResource edited, AnimationDestination destination)
    {
        if (destination != AnimationDestination.NewMod || capture.Startup is null || edited.ModDirectory is not { } mod)
            return null;
        var other = startup ? capture.Clip.GamePath : capture.Startup.GamePath;
        if (string.Equals(other, edited.GamePath, StringComparison.OrdinalIgnoreCase))
            return null;
        var source = capture.Sources.FirstOrDefault(resource => string.Equals(resource.GamePath, other, StringComparison.OrdinalIgnoreCase));
        return SameMod(source, mod) ? other : null;
    }

    /// <summary>The save footer's line when <see cref="CompanionClip"/> brings the other clip; <paramref name="startup"/> is whether the startup is edited.</summary>
    public static string CompanionNote(bool startup) => startup ? "Its loop goes into the new mod too." : "Its startup goes into the new mod too.";

    /// <summary>
    /// <paramref name="outputs"/> with <paramref name="companion"/>'s file as the save read it (in
    /// <paramref name="manifest"/>), unchanged. Throws when it no longer comes from <paramref name="edited"/>'s mod,
    /// as the edited clip's own save refuses a changed source.
    /// </summary>
    public static ImmutableDictionary<string, byte[]> WithCompanion(ImmutableDictionary<string, byte[]> outputs, AnimationDependencyManifest manifest,
        AnimationResource edited, string? companion)
    {
        if (companion == null)
            return outputs;
        var source = manifest.Resources.FirstOrDefault(resource => string.Equals(resource.GamePath, companion, StringComparison.OrdinalIgnoreCase));
        if (edited.ModDirectory is not { } mod || !SameMod(source, mod) || !manifest.Files.TryGetValue(companion, out var bytes))
            throw new IOException("The loop and its startup no longer come from the same mod. Open the timeline again.");
        return outputs.SetItem(companion, bytes);
    }

    private static bool SameMod(AnimationResource? resource, string mod) =>
        resource?.ModDirectory is { } other && string.Equals(other, mod, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The clip a save applies bone layers to, empty for its samples: on the output skeleton, carrying
    /// the bones the clip has tracks for (<paramref name="tracks"/>, on that skeleton) as Live's decoded
    /// clip does, so symmetry layers leave the same trackless bones to physics in both.
    /// </summary>
    public static MotionClip LayerClip(SkeletonDescription skeleton, float duration, int frames, bool loops, IEnumerable<short> tracks) =>
        new(skeleton, duration, frames) { Loops = loops, SourceTracks = [.. tracks] };

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
    /// Whether saving <paramref name="document"/> writes another motion than the file's: a bake (see
    /// <see cref="ChangesMotion"/>), or a motion from Blender, which is already built and never baked again.
    /// </summary>
    public static bool NewMotion(TimelineDocument document, SkeletonStandard? repair = null) =>
        ChangesMotion(document, repair) || document.Replacement != null;

    /// <summary>
    /// Whether saving bakes its edits in for good: bones left out or a repair. The saved file is then
    /// no longer the motion from before plus the edits (cuts, a new length, layers), so they can't be
    /// opened again on it.
    /// </summary>
    public static bool BakesLayersIn(TimelineDocument document, SkeletonStandard? repair = null) =>
        !document.ExcludedBones.IsEmpty || repair != null;

    /// <summary>
    /// Whether the Timeline window goes on editing <paramref name="document"/>'s bone layers on the motion
    /// from before once it is saved or the save undone: not when the save bakes them in for good, and not
    /// when it cuts or stretches the motion, whose saved timeline is then in other frames than that motion.
    /// The saved file is read back as it is, and Edit again brings the cuts, length and layers back.
    /// </summary>
    public static bool KeepsLayersOverSave(TimelineDocument document, SkeletonStandard? repair = null) =>
        !BakesLayersIn(document, repair) && !document.Cut && !document.Stretched;

    /// <summary>
    /// The length a motion from Blender is written with: the clip's own when the motion's is within a
    /// microsecond of it (float noise from the round trip), so the length stays exactly the clip's; else the motion's.
    /// </summary>
    public static float ReplacementDuration(float motion, float source) => Math.Abs(motion - source) <= 1e-6f ? source : motion;

    /// <summary>
    /// Whether a clip's motion can't come from Blender yet: an additive clip (blend hint 1 or 2) with float
    /// tracks. How the game's sampler turns such a float track's values into the slot's value hasn't been
    /// measured, so the build couldn't verify what it wrote.
    /// </summary>
    public static bool AdditiveWithFloats(sbyte blendHint, int floatTracks) => blendHint != 0 && floatTracks > 0;

    /// <summary>
    /// The transform tracks a motion from Blender is written with: the clip's own, in their order, then
    /// every other bone the motion moves away from its reference pose at some sample. A bone that stays at
    /// its reference gets none, so the game's physics keeps moving it.
    /// </summary>
    public static short[] ReplacementTracks(ReadOnlySpan<short> templateTracks, MotionClip motion)
    {
        var tracks = templateTracks.ToArray().ToList();
        for (var b = 0; b < motion.Bones; b++)
        {
            if (tracks.Contains((short)b))
                continue;
            var reference = MotionTransform.From(motion.Skeleton.Bones[b].Reference);
            for (var t = 0; t < motion.Samples; t++)
            {
                if (Near(motion.Get(t, b), reference))
                    continue;
                tracks.Add((short)b);
                break;
            }
        }
        return [.. tracks];
    }

    /// <summary>As the bake's own comparison (AnimationNative.Near) at its 1e-6 tolerance.</summary>
    private static bool Near(MotionTransform a, MotionTransform b, float tolerance = 0.000001f) =>
        Vector3.Distance(a.Position, b.Position) <= tolerance && Vector3.Distance(a.Scale, b.Scale) <= tolerance &&
        1 - Math.Abs(Quaternion.Dot(Quaternion.Normalize(a.Rotation), Quaternion.Normalize(b.Rotation))) <= tolerance;
}
