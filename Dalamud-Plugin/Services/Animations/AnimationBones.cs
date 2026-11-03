using System.Collections.Immutable;
using System.Numerics;
using FFXIVClientStructs.Havok.Common.Base.Math.QsTransform;
using InstantEdit.Models;
using InstantEdit.Studio.Models;

namespace InstantEdit.Services.Animations;

internal sealed record RepairPlan(StandardSkeleton Target, ImmutableArray<string> Kept, ImmutableArray<string> Dropped, int HighestIndex);

/// <summary>
/// Which bones an animation drives, and leaving chosen ones out of a rebake. A bone
/// with no transform track is not driven by the animation at all: it samples at its
/// reference pose, and whatever else moves it in game - hair, cloth or tail physics,
/// for instance - takes over again.
/// </summary>
internal static class AnimationBones
{
    // How far off its rest pose a bone must be somewhere in a clip to count as moved. Measured 2026-10-30 on nine c0801 emotes:
    // vanilla clips hold the chest bones up to 0.17 deg off rest and others under 0.002 mm and 0.004 %; the smallest real moves
    // were 0.75 deg and 1.46 mm.
    private const float MovedDegrees = .5f, MovedDistance = .0005f, MovedScale = .005f;

    /// <summary>
    /// The bones <paramref name="clip"/> takes off their rest pose somewhere: turned, moved or scaled. A track that only holds
    /// its bone at rest doesn't count, as vanilla clips hold many of the body's bones.
    /// </summary>
    public static HashSet<string> Moved(Motion.MotionClip clip)
    {
        var cosine = MathF.Cos(MovedDegrees * MathF.PI / 360);
        var moved = new HashSet<string>(StringComparer.Ordinal);
        for (var b = 0; b < clip.Bones; b++)
        {
            var rest = clip.Skeleton.Bones[b].Reference;
            for (var s = 0; s < clip.Samples; s++)
            {
                var at = clip.Get(s, b);
                if (MathF.Abs(Quaternion.Dot(Quaternion.Normalize(at.Rotation), Quaternion.Normalize(rest.Rotation))) < cosine ||
                    Vector3.DistanceSquared(at.Position, rest.Position) > MovedDistance * MovedDistance ||
                    MathF.Abs(at.Scale.X - rest.Scale.X) > MovedScale || MathF.Abs(at.Scale.Y - rest.Scale.Y) > MovedScale ||
                    MathF.Abs(at.Scale.Z - rest.Scale.Z) > MovedScale)
                {
                    moved.Add(clip.Skeleton.Bones[b].Name);
                    break;
                }
            }
        }
        return moved;
    }

    /// <summary>The bones a clip's transform tracks drive, in skeleton order.</summary>
    public static ImmutableArray<string> Animated(AnimationClip clip)
    {
        if (clip.Resolution is not { Selected: { } selected } resolution) return [];
        var bones = selected.Skeleton.Bones;
        return [.. resolution.TrackBones.Where(i => i >= 0 && i < bones.Length).Distinct().Order().Select(i => bones[i].Name)];
    }

    /// <summary>
    /// The bone in <paramref name="skeleton"/> each of the clip's transform tracks drives, in track order: its own
    /// track bones on its source skeleton, else found by name, tracks whose bone it lacks left out. With some
    /// left out, the result is no longer indexed by track: use it as a set of bones.
    /// </summary>
    public static ImmutableArray<short> TracksOn(AnimationClip clip, SkeletonDescription skeleton)
    {
        if (clip.Resolution is not { Selected: { } selected } resolution) return [];
        if (selected.Skeleton.Fingerprint == skeleton.Fingerprint) return resolution.TrackBones;
        var bones = selected.Skeleton.Bones;
        var index = new Dictionary<string, short>(StringComparer.Ordinal);
        for (var i = 0; i < skeleton.Bones.Length; i++) index.TryAdd(skeleton.Bones[i].Name, (short)i);
        return [.. resolution.TrackBones.Where(i => i >= 0 && i < bones.Length && index.ContainsKey(bones[i].Name)).Select(i => index[bones[i].Name])];
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

    /// <summary>The highest bone index a rebake that leaves out these bones writes, or -1 when none is left.</summary>
    public static int HighestIndex(AnimationClip clip, IReadOnlyCollection<string> excluded)
    {
        if (clip.Resolution is not { Selected: { } selected } resolution) return -1;
        var set = excluded.ToHashSet(StringComparer.Ordinal);
        var bones = selected.Skeleton.Bones;
        return resolution.TrackBones.Where(i => i >= 0 && i < bones.Length && !set.Contains(bones[i].Name))
            .Select(i => (int)i).DefaultIfEmpty(-1).Max();
    }

    public static string StandardName(SkeletonStandard standard) => standard switch
    {
        SkeletonStandard.Ivcs => "IVCS",
        SkeletonStandard.IvcsYas => "IVCS + YAS",
        _ => "Vanilla",
    };

    /// <summary>
    /// The standard's skeleton for the clip's own race, or with <paramref name="model"/> for that
    /// race: only the character's race (see <see cref="SkeletonResolution.ActorModel"/>) has any.
    /// </summary>
    public static StandardSkeleton? Standard(AnimationClip clip, SkeletonStandard standard, string? model = null)
        => (model == null ? clip.Resolution?.Standards : model == clip.Resolution?.ActorModel ? clip.Resolution?.ActorStandards : null)
            ?.FirstOrDefault(s => s.Standard == standard);

    /// <summary>A race code for people: "Miqo'te (Female)" for "c0801".</summary>
    public static string RaceName(string model) => ushort.TryParse(model.AsSpan(1), out var code) ? RaceInfo.NameOf(code) : model;

    /// <summary>
    /// The race a save can rebuild the clip for: the character's, when the clip is another race's
    /// and its file (<paramref name="fileModel"/>, the race its PAP header names) wasn't saved for it already.
    /// None when the file <paramref name="holdsOthers"/>: its header names their race too, so a rebuild would warp them.
    /// </summary>
    public static string? RebuildRace(AnimationClip clip, string? fileModel, bool holdsOthers = false)
        => holdsOthers ? null : ActorRace(clip, fileModel);

    private static string? ActorRace(AnimationClip clip, string? fileModel)
        => clip.Resolution?.ActorModel is { } actor && actor != fileModel ? actor : null;

    /// <summary>
    /// The bones <paramref name="editing"/> lacks that a standard of the clip's race (or <paramref name="model"/>'s, for a rebuild)
    /// has, each with the skeleton choice that edits on the smallest standard holding it: "Repair onto IVCS + YAS".
    /// </summary>
    public static Dictionary<string, string> WiderChoices(AnimationClip clip, SkeletonDescription editing, string? model)
    {
        var have = editing.Bones.Select(bone => bone.Name).ToHashSet(StringComparer.Ordinal);
        var choices = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var standard in Enum.GetValues<SkeletonStandard>())
        {
            if (Standard(clip, standard, model) is not { } found)
                continue;
            var name = model == null ? "Repair onto " + StandardName(standard) : RebuildName(model, standard);
            foreach (var bone in found.Skeleton.Bones)
                if (!have.Contains(bone.Name))
                    choices.TryAdd(bone.Name, name);
        }
        return choices;
    }

    /// <summary>The skeleton choice's entry for a rebuild: "Rebuild for Miqo'te (Female), IVCS".</summary>
    public static string RebuildName(string model, SkeletonStandard standard) => $"Rebuild for {RaceName(model)}, {StandardName(standard)}";

    /// <summary>The line under the save footer when a rebuild picked for you went back to Keep its own: its motion couldn't be read on the target.</summary>
    public static string RebuildFailedNote(string model, string error) => $"Can't rebuild for {RaceName(model)}: {error}";

    /// <summary>
    /// The rebuild the save footer picks for you once the clip's match is in: onto the <see cref="DefaultRebuild"/>
    /// standard of the character's race. Null while it is matched, or without one.
    /// </summary>
    public static RaceRebuild? AutomaticRebuild(AnimationClip clip, string? fileModel, IReadOnlyCollection<string> excluded, bool holdsOthers = false)
        => clip.Resolution is { State: SkeletonResolutionState.Matched, ActorModel: { } actor } &&
           DefaultRebuild(clip, fileModel, excluded, holdsOthers) is { } standard && Standard(clip, standard, actor) is { } target
            ? new(actor, target) : null;

    /// <summary>
    /// <paramref name="rebuild"/> as the clip's match has it now (a skeleton chosen under Source, another character):
    /// the same, onto the standard found now, or null when its race or standard is gone. A repair (no race) needs only
    /// its standard for the clip's own race.
    /// </summary>
    public static RaceRebuild? CurrentRebuild(AnimationClip clip, string? fileModel, RaceRebuild rebuild, bool holdsOthers = false)
    {
        var race = rebuild.Model == null ? null : RebuildRace(clip, fileModel, holdsOthers);
        if (rebuild.Model != null && race != rebuild.Model || Standard(clip, rebuild.Target.Standard, race) is not { } now)
            return null;
        return now == rebuild.Target ? rebuild : rebuild with { Target = now };
    }

    private static string[] Kept(AnimationClip clip, IReadOnlyCollection<string> excluded)
    {
        var set = excluded.ToHashSet(StringComparer.Ordinal);
        return [.. Animated(clip).Where(name => !set.Contains(name))];
    }

    private static bool Holds(StandardSkeleton standard, IEnumerable<string> bones)
        => standard.Skeleton.Bones.Select(b => b.Name).ToHashSet(StringComparer.Ordinal).IsSupersetOf(bones);

    /// <summary>
    /// The standard a rebuild picks unless you choose: the smallest of the character's race whose
    /// skeleton has every bone kept. Null without a race to rebuild for or when none has them.
    /// </summary>
    public static SkeletonStandard? DefaultRebuild(AnimationClip clip, string? fileModel, IReadOnlyCollection<string> excluded, bool holdsOthers = false)
    {
        if (RebuildRace(clip, fileModel, holdsOthers) == null) return null;
        var kept = Kept(clip, excluded);
        return clip.Resolution!.ActorStandards.OrderBy(s => s.Standard).FirstOrDefault(s => Holds(s, kept))?.Standard;
    }

    /// <summary>
    /// The line under the save footer: what the save rebuilds for, or why it can't. Null without a
    /// race to rebuild for, or when it can and isn't <paramref name="rebuilding"/>. A file that
    /// <paramref name="holdsOthers"/> can't be rebuilt; the line says so when nothing else stops it.
    /// </summary>
    public static string? RebuildNote(AnimationClip clip, string? fileModel, IReadOnlyCollection<string> excluded, bool rebuilding, bool holdsOthers = false)
    {
        if (ActorRace(clip, fileModel) is not { } model) return null;
        var race = RaceName(model);
        if (rebuilding) return $"Rebuilt for {race}: your character's skeleton.";
        var standards = clip.Resolution!.ActorStandards;
        if (standards.IsEmpty) return $"Can't rebuild for {race}: its skeleton wasn't found.";
        if (DefaultRebuild(clip, fileModel, excluded) != null) return holdsOthers ? $"Can't rebuild for {race}: this file holds other animations." : null;
        // Each standard extends the one before it, so the largest lacks what they all lack.
        var largest = standards.MaxBy(s => s.Standard)!.Skeleton.Bones.Select(b => b.Name).ToHashSet(StringComparer.Ordinal);
        var lacking = Kept(clip, excluded).Where(name => !largest.Contains(name)).ToArray();
        return $"Can't rebuild for {race}: its skeletons lack {string.Join(", ", lacking.Take(3).Select(AnimationPresentation.BoneName))}" +
               (lacking.Length > 3 ? $" and {lacking.Length - 3} more" : "") + ".";
    }

    /// <summary>
    /// Whether a bone belongs to a standard's groups: the game's own bones, then IVCS's
    /// iv_ bones, then YAS's ya_ bones.
    /// </summary>
    public static bool InStandard(string name, SkeletonStandard standard, IReadOnlySet<string> vanilla)
        => vanilla.Contains(name) ||
           standard >= SkeletonStandard.Ivcs && name.StartsWith("iv_", StringComparison.Ordinal) ||
           standard == SkeletonStandard.IvcsYas && name.StartsWith("ya_", StringComparison.Ordinal);

    /// <summary>
    /// The animated bones a preset unticks: every one outside the standard's groups. Null
    /// until the game's own skeleton for the clip's model is known.
    /// </summary>
    public static ImmutableArray<string>? Outside(AnimationClip clip, SkeletonStandard standard)
    {
        if (Standard(clip, SkeletonStandard.Vanilla) is not { } game) return null;
        var vanilla = game.Skeleton.Bones.Select(b => b.Name).ToHashSet(StringComparer.Ordinal);
        return [.. Animated(clip).Where(name => !InStandard(name, standard, vanilla))];
    }

    // How many of the game's last bones a mod layout may lack at the game's indices. The game
    // appended n_hara_noanim_trans after IVCS and YAS fixed their layouts; a small allowance
    // covers such patch additions without accepting a rig that reshuffles the game's bones.
    private const int LateGameBones = 4;

    /// <summary>
    /// Whether a skeleton is laid out as the standard: only bones of the standard's groups,
    /// with IVCS's (and for IVCS + YAS, YAS's) among them, and the game's bones leading at
    /// the game's own indices, except that up to <see cref="LateGameBones"/> of the game's
    /// last bones may be missing there or follow later.
    /// </summary>
    public static bool IsStandardLayout(SkeletonDescription skeleton, SkeletonDescription game, SkeletonStandard standard)
    {
        var gameNames = game.Bones.Select(b => b.Name).ToArray();
        var names = skeleton.Bones.Select(b => b.Name).ToArray();
        if (standard == SkeletonStandard.Vanilla) return names.SequenceEqual(gameNames, StringComparer.Ordinal);
        var vanilla = gameNames.ToHashSet(StringComparer.Ordinal);
        if (!names.All(name => InStandard(name, standard, vanilla)) ||
            !names.Any(name => name.StartsWith("iv_", StringComparison.Ordinal)) ||
            standard == SkeletonStandard.IvcsYas && !names.Any(name => name.StartsWith("ya_", StringComparison.Ordinal)))
            return false;
        var leading = names.TakeWhile(vanilla.Contains).Count();
        return leading >= gameNames.Length - LateGameBones && leading <= gameNames.Length &&
               names.Take(leading).SequenceEqual(gameNames.Take(leading), StringComparer.Ordinal);
    }

    /// <summary>
    /// The standard a repair picks unless you choose: the smallest found whose skeleton has
    /// every bone kept, else the largest found. Null when none was found.
    /// </summary>
    public static SkeletonStandard? DefaultRepairTarget(AnimationClip clip, IReadOnlyCollection<string> excluded)
    {
        var standards = clip.Resolution?.Standards ?? [];
        if (standards.IsEmpty) return null;
        var set = excluded.ToHashSet(StringComparer.Ordinal);
        var kept = Animated(clip).Where(name => !set.Contains(name)).ToArray();
        return standards.OrderBy(s => s.Standard).FirstOrDefault(s =>
            s.Skeleton.Bones.Select(b => b.Name).ToHashSet(StringComparer.Ordinal).IsSupersetOf(kept))?.Standard
            ?? standards.Max(s => s.Standard);
    }

    /// <summary>
    /// What a repair onto the standard writes: the kept animated bones its skeleton has, the
    /// ones it lacks and leaves out, and the highest bone index the output binds. Null when
    /// the standard's skeleton was not found.
    /// </summary>
    public static RepairPlan? Plan(AnimationClip clip, SkeletonStandard standard, IReadOnlyCollection<string> excluded, string? model = null)
    {
        if (Standard(clip, standard, model) is not { } target) return null;
        var index = target.Skeleton.Bones.Select((bone, i) => (bone.Name, i)).ToDictionary(b => b.Name, b => b.i, StringComparer.Ordinal);
        var set = excluded.ToHashSet(StringComparer.Ordinal);
        var bones = Animated(clip).Where(name => !set.Contains(name)).ToArray();
        ImmutableArray<string> kept = [.. bones.Where(index.ContainsKey)];
        return new(target, kept, [.. bones.Where(name => !index.ContainsKey(name))],
            kept.Select(name => index[name]).DefaultIfEmpty(-1).Max());
    }

    /// <summary>Indices of the named bones in a skeleton. Names it does not have are skipped.</summary>
    public static HashSet<int> Indices(SkeletonDescription skeleton, IEnumerable<string> names)
    {
        var wanted = names.ToHashSet(StringComparer.Ordinal);
        return [.. Enumerable.Range(0, skeleton.Bones.Length).Where(i => wanted.Contains(skeleton.Bones[i].Name))];
    }

    /// <summary>
    /// The game bone no saved animation drives: no game file has a track for it, and its index is another bone on IVCS, YAS
    /// and skeleton-mod rigs (the game's 105 is IVCS's iv_ko_c_l), which a track on it would move there.
    /// </summary>
    public const string Unanimated = "n_hara_noanim_trans";

    /// <summary>The bones a save of <paramref name="skeleton"/> writes no track for: the unticked <paramref name="excluded"/> and <see cref="Unanimated"/>.</summary>
    public static HashSet<int> LeftOut(SkeletonDescription skeleton, IEnumerable<string> excluded) =>
        Indices(skeleton, excluded.Append(Unanimated));

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
