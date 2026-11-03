using System.Collections.Immutable;
using System.Numerics;
using InstantEdit.Models;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Studio.Models;

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
    /// This reads the sources the capture saw, for the save footer's note; the save asks the collection again
    /// (<see cref="CompanionCandidate"/>, <see cref="CompanionFrom"/>).
    /// </summary>
    public static string? CompanionClip(AnimationCapture capture, bool startup, AnimationResource edited, AnimationDestination destination) =>
        CompanionCandidate(capture, startup, edited, destination) is { } other
            ? CompanionFrom(other, capture.Sources.FirstOrDefault(resource => string.Equals(resource.GamePath, other, StringComparison.OrdinalIgnoreCase)), edited)
            : null;

    /// <summary>
    /// The other half's game path a save to a new mod may carry along (see <see cref="CompanionClip"/>), before
    /// knowing where the collection gives it from. Null when nothing can come along.
    /// </summary>
    public static string? CompanionCandidate(AnimationCapture capture, bool startup, AnimationResource edited, AnimationDestination destination)
    {
        if (destination != AnimationDestination.NewMod || capture.Startup is null || edited.ModDirectory is null)
            return null;
        var other = startup ? capture.Clip.GamePath : capture.Startup.GamePath;
        return string.Equals(other, edited.GamePath, StringComparison.OrdinalIgnoreCase) ? null : other;
    }

    /// <summary><paramref name="candidate"/> when <paramref name="current"/>, where it comes from, is <paramref name="edited"/>'s mod; else null.</summary>
    public static string? CompanionFrom(string? candidate, AnimationResource? current, AnimationResource edited) =>
        candidate != null && edited.ModDirectory is { } mod && SameMod(current, mod) ? candidate : null;

    /// <summary>The save footer's line when <see cref="CompanionClip"/> brings the other clip; <paramref name="startup"/> is whether the startup is edited.</summary>
    public static string CompanionNote(bool startup) => startup ? "Its loop goes into the new mod too." : "Its startup goes into the new mod too.";

    /// <summary>
    /// The save footer's line for a rebuild: the other races that play the file (<paramref name="playedBy"/>)
    /// and now get it converted from <paramref name="actor"/>'s race. Null when no other race plays it.
    /// </summary>
    public static string? BorrowNote(IReadOnlyList<ushort> playedBy, ushort actor)
    {
        var others = playedBy.Where(race => race != actor).Select(RaceInfo.NameOf).ToArray();
        if (others.Length == 0) return null;
        var names = others.Length == 1 ? others[0] : string.Join(", ", others[..^1]) + " and " + others[^1];
        return $"{names} {(others.Length == 1 ? "plays" : "play")} it converted from {RaceInfo.NameOf(actor)}.";
    }

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
    public static MotionClip LayerClip(SkeletonDescription skeleton, float duration, int frames, bool loops, IEnumerable<short> tracks, bool hipsOnHara = false) =>
        new(skeleton, duration, frames) { Loops = loops, SourceTracks = [.. tracks], HipsOnHara = hipsOnHara };

    /// <summary>
    /// Whether the bake retargets onto a standard skeleton: a skeleton repair, or a timeline save
    /// that names one to repair onto.
    /// </summary>
    public static bool Repairs(AnimationBakeRequest request) =>
        request.Operation == AnimationOperation.RepairSkeleton ||
        request.Operation == AnimationOperation.EditTimeline && (request.RepairTarget != null || request.RepairSkeleton != null);

    /// <summary>
    /// The race a repair's output must name in its PAP header: the one whose standard skeleton it was
    /// repaired onto, the clip's live skeleton's. The game plays a PAP naming another race through that
    /// race's skeleton mapper, so a clip repaired onto c0801's standard that still names c0101 is converted
    /// a second time: every bone shortened by the races' ratio, a warped body and head (measured in game
    /// 2026-10-19). Null when the header stays: no repair, a clip naming no human race, or the same race.
    /// The header names the race of every animation in the file, so a file holding others the repair
    /// leaves on the old race is refused: one of the two would play warped. A rebuild for another race
    /// (<see cref="AnimationBakeRequest.RepairModel"/>) names that race.
    /// </summary>
    public static ushort? RepairedModel(AnimationBakeRequest request, AnimationClip clip, AnimationPap pap)
    {
        if (!Repairs(request) || pap.ModelType != 0 || (request.RepairModel ?? AnimationSkeletonIndex.ModelFromPath(clip.SkeletonPath)) is not { } race ||
            !ushort.TryParse(race.AsSpan(1), out var model) || model == pap.ModelId)
            return null;
        var startup = request.IncludeStartup && request.Capture.Startup is { } start && start.GamePath == clip.GamePath ? start.BindingIndex : -1;
        if (pap.Entries.Any(entry => entry.Binding != clip.BindingIndex && entry.Binding != startup))
            throw new InvalidDataException($"This file also holds animations for {AnimationSkeletonIndex.ModelCode(pap.ModelId) ?? "another race"}. " +
                $"Repairing it onto {race} would warp them.");
        return model;
    }

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
    /// Whether a save that rebuilds a borrowed clip for another race (<see cref="AnimationBakeRequest.RepairModel"/>) keeps
    /// its edits open: with no bones left out, the motion from before rebuilt for that race takes its cuts, length and
    /// layers back (Edit again), on the skeleton the saved file is for.
    /// </summary>
    public static bool RebuildKeepsEdits(AnimationBakeRequest request, TimelineDocument document) =>
        request is { RepairModel: not null, RepairTarget: not null, ExcludedBones.IsEmpty: true } &&
        (document.MotionChanged || document.Cut || document.Stretched);

    /// <summary>
    /// Whether the Timeline window goes on editing <paramref name="document"/>'s bone layers on the motion
    /// from before once it is saved or the save undone: not when the save bakes them in for good, and not
    /// when it cuts or stretches the motion, or gives a face a new length, whose saved timeline is then in other frames
    /// than that motion. The saved file is read back as it is, and Edit again brings the cuts, length and layers back.
    /// </summary>
    public static bool KeepsLayersOverSave(TimelineDocument document, SkeletonStandard? repair = null) =>
        !BakesLayersIn(document, repair) && !document.Cut && !document.Stretched && document.Face is not { LengthChanged: true };

    /// <summary>
    /// The save footer's line while saving <paramref name="document"/> would bake its bone layers in for good
    /// (<see cref="BakesLayersIn"/>), and whether Undo last save brings them back (<see cref="LayersBackAfterUndo"/>). Null otherwise.
    /// </summary>
    public static string? BakeInNote(TimelineDocument document, SkeletonStandard? repair) =>
        !document.MotionChanged || !BakesLayersIn(document, repair) ? null
        : document.Cut || document.Stretched ? "Saving bakes the layers in."
        : "Saving bakes the layers in. Undo last save brings them back as unsaved edits.";

    /// <summary>
    /// The bone layers undoing <paramref name="request"/>'s save brings back as unsaved edits, when it baked them in
    /// for good (bones left out or a repair): its layers, by bone name. None for a save that cut or stretched the
    /// motion, whose layers are keyed in other frames than the file the undo brings back.
    /// </summary>
    public static ImmutableArray<MotionLayer> LayersBackAfterUndo(AnimationBakeRequest request) =>
        request is { Timeline: { Motion.IsEmpty: false, Cuts: null } edit } && (request.RepairTarget != null || !request.ExcludedBones.IsEmpty)
            ? edit.Motion : [];

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

    /// <summary>
    /// Whether a bone layer changed a bone's value, by more than a micrometre or about 0.0001 degrees: what a save writes
    /// and Live gives a track. A turn is measured by the vector part of the turn between the two, which floats keep precise
    /// (as amplitude layers measure it); one minus their dot product read turns under 0.16 degrees as none, so saves dropped
    /// a pin's small hip turns and a planted foot jumped 2.5 mm in game where Live held it.
    /// </summary>
    public static bool Changed(MotionTransform before, MotionTransform after)
    {
        var turn = Quaternion.Conjugate(Quaternion.Normalize(before.Rotation)) * Quaternion.Normalize(after.Rotation);
        return Vector3.DistanceSquared(before.Position, after.Position) > 1e-12f || Vector3.DistanceSquared(before.Scale, after.Scale) > 1e-12f ||
               new Vector3(turn.X, turn.Y, turn.Z).LengthSquared() > 1e-12f;
    }

    /// <summary>As the bake's own comparison (AnimationNative.Near) at its 1e-6 tolerance.</summary>
    private static bool Near(MotionTransform a, MotionTransform b, float tolerance = 0.000001f) =>
        Vector3.Distance(a.Position, b.Position) <= tolerance && Vector3.Distance(a.Scale, b.Scale) <= tolerance &&
        1 - Math.Abs(Quaternion.Dot(Quaternion.Normalize(a.Rotation), Quaternion.Normalize(b.Rotation))) <= tolerance;
}
