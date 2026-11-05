using System.Collections.Immutable;
using InstantEdit.Models;
using InstantEdit.Services.Animations.Motion;
using InstantEdit.Studio.Models;

namespace InstantEdit.Services.Animations;

/// <summary>What an All races save does with one file the races load.</summary>
internal enum RaceFileAction
{
    /// <summary>The file being edited.</summary>
    Edited,
    /// <summary>Gets a copy of the edit, built for its race.</summary>
    Rebuild,
    /// <summary>The mod's own other file: left as it is.</summary>
    Kept,
    /// <summary>Its race has no skeleton to build on.</summary>
    Skipped,
}

/// <summary>Where the collection gives a path from (null: nowhere), and whether the edited mod maps the path itself.</summary>
internal readonly record struct RaceFileSource(AnimationResource? Collection, bool EditedModMaps = false);

/// <summary>
/// One file of an All races save: its path, the race its path names, the races that load it and what the save does.
/// <paramref name="Target"/> is the standard it is built on, null when it stays on its own skeleton;
/// <paramref name="BonesLeftOut"/> counts the moved bones that standard lacks; <paramref name="InEditedMod"/> says the collection gives the path from the edited mod.
/// </summary>
internal sealed record RaceFile(string GamePath, ushort Race, ImmutableArray<ushort> PlayedBy, RaceFileAction Action,
    StandardSkeleton? Target = null, int BonesLeftOut = 0, bool InEditedMod = false);

/// <summary>The files of an All races save, the edited one first.</summary>
internal sealed record RaceCopyPlan(ImmutableArray<RaceFile> Files);

/// <summary>
/// Saving an edit for every race: one copy per distinct file the races load, each built for the race in its path,
/// as the game's own files are. No Dalamud dependencies.
/// </summary>
internal static class RaceCopies
{
    /// <summary>Where the copies live in a mod. A later save tells a copy from the mod's own file by it.</summary>
    public const string Folder = AnimationCommitService.AddedFolder + "races/";

    /// <summary>
    /// Why this clip can't be saved for every race, or null when it can: the plain reason for the greyed switch.
    /// Only <c>bt_common</c> files: in weapon folders a race's request can move to another folder, so a copy could change another animation.
    /// </summary>
    public static string? Unavailable(string gamePath, bool face, bool holdsOthers, bool newMotion, PapLoadTable? table, IEnumerable<ushort> races)
    {
        if (face) return "A face saves for its own race.";
        if (!InCommonFolder(gamePath)) return "Only unarmed emotes and idles can.";
        if (holdsOthers) return "This file holds other animations.";
        if (newMotion) return "A motion from Blender can't be rebuilt yet.";
        if (table == null) return "The game's load table couldn't be read.";
        return table.Files(gamePath, races).Length <= 1 ? "Every race plays this one file." : null;
    }

    /// <summary>The save row's count beside the switch.</summary>
    public static string CountLabel(RaceCopyPlan plan) => plan.Files.Length == 1 ? "1 file" : $"{plan.Files.Length} files";

    /// <summary>The switch's tooltip: what it does, then one line per file, named by its path's race.</summary>
    public static IEnumerable<string> TooltipLines(RaceCopyPlan? plan)
    {
        yield return "Saves a copy for each file the races play.";
        foreach (var file in plan?.Files ?? [])
            yield return $"{RaceInfo.NameOf(file.Race)}: {FileLine(file)}";
    }

    private static string FileLine(RaceFile file)
    {
        var also = file.PlayedBy.Where(race => race != file.Race).Select(RaceInfo.NameOf).ToArray();
        var others = also.Length == 0 ? "" : also.Length <= 3 ? $" Also {string.Join(", ", also)}." : $" Also {string.Join(", ", also[..3])} and {also.Length - 3} more.";
        var left = file.BonesLeftOut switch { 0 => "", 1 => " 1 bone left out.", var count => $" {count} bones left out." };
        return file.Action switch
        {
            RaceFileAction.Edited => "Your edit." + others + left,
            RaceFileAction.Rebuild => "Rebuilt." + others + left,
            RaceFileAction.Kept => "The mod's own file, kept.",
            _ => "Its skeleton wasn't found, skipped.",
        };
    }

    /// <summary>Whether <paramref name="gamePath"/> is a human PAP in a set's <c>bt_common</c> folder.</summary>
    public static bool InCommonFolder(string gamePath) =>
        gamePath.Split('/') is ["chara", "human", ['c', ..], "animation", ['a', ..], "bt_common", _, ..] &&
        gamePath.EndsWith(".pap", StringComparison.Ordinal);

    /// <summary>
    /// What a save does with each of <paramref name="files"/> (<see cref="PapLoadTable.Files"/>). <paramref name="fileModel"/> is the race the
    /// edited file's header names, such as "c0101"; a file of that race stays on its own skeleton unless <paramref name="repair"/> moves it.
    /// <paramref name="kept"/> are the bones the edit moves. A path nothing provides and the mod doesn't map is left out: no race plays it.
    /// </summary>
    public static RaceCopyPlan Plan(IEnumerable<(string Path, ImmutableArray<ushort> PlayedBy)> files, AnimationResource edited, string? fileModel,
        Func<string, RaceFileSource> source, Func<ushort, ImmutableArray<StandardSkeleton>> standards, IReadOnlyCollection<string> kept, SkeletonStandard? repair)
    {
        var all = files.ToArray();
        var editedFile = all.FirstOrDefault(file => file.Path == edited.GamePath);
        var rows = new List<RaceFile> { Row(edited.GamePath, editedFile.PlayedBy.IsDefault ? [] : editedFile.PlayedBy, RaceFileAction.Edited, edited.ModDirectory != null) };
        foreach (var (path, playedBy) in all)
        {
            if (path == edited.GamePath)
                continue;
            var given = source(path);
            if (Classify(given, edited) is { } action)
                rows.Add(Row(path, playedBy, action, FromMod(given, edited)));
        }
        return new([.. rows]);

        RaceFile Row(string path, ImmutableArray<ushort> playedBy, RaceFileAction action, bool inMod)
        {
            var race = RaceOf(path);
            if (action == RaceFileAction.Kept || repair == null && Model(race) == fileModel)
                return new(path, race, playedBy, action, InEditedMod: inMod);
            var (target, leftOut) = PickStandard(standards(race), kept, repair);
            return target == null && action == RaceFileAction.Rebuild
                ? new(path, race, playedBy, RaceFileAction.Skipped, InEditedMod: inMod)
                : new(path, race, playedBy, action, target, leftOut, inMod);
        }
    }

    private static string Model(ushort race) => $"c{race:D4}";

    private static bool FromMod(RaceFileSource source, AnimationResource edited) =>
        source.Collection?.ModDirectory is { } mod && mod == edited.ModDirectory;

    /// <summary>The paths the save writes a copy of the edit to.</summary>
    public static ImmutableArray<string> CopyPaths(RaceCopyPlan plan) =>
        [.. plan.Files.Where(file => file.Action == RaceFileAction.Rebuild).Select(file => file.GamePath)];

    /// <summary>
    /// The paths besides the edited one a save must read and recheck: in place the copies that take over a path the mod maps,
    /// which go through its rename and undo; in a new mod the mod's own files that come along unchanged.
    /// </summary>
    public static ImmutableArray<string> ManifestPaths(RaceCopyPlan plan, AnimationDestination destination) =>
        [.. plan.Files.Where(file => file.InEditedMod && (destination == AnimationDestination.NewMod
            ? file.Action == RaceFileAction.Kept : file.Action == RaceFileAction.Rebuild)).Select(file => file.GamePath)];

    /// <summary>
    /// <paramref name="request"/> as it bakes <paramref name="file"/>: for its race on its standard, with the layers that standard can hold.
    /// A file on its own skeleton is baked as the request says, with nothing to rebuild. A copy always names its race, also the header's own:
    /// without one the header would take the race of the skeleton the clip plays on, the character's (see <see cref="AnimationBakeRules.RepairedModel"/>).
    /// </summary>
    public static AnimationBakeRequest CopyRequest(AnimationBakeRequest request, RaceFile file)
    {
        if (file.Target is not { } target)
            return request with { RepairTarget = null, RepairModel = null, RepairSkeleton = null };
        return request with
        {
            RepairTarget = target.Standard, RepairModel = Model(file.Race), RepairSkeleton = target.Skeleton,
            Timeline = request.Timeline! with { Motion = LayersFor(request.Timeline.Motion, target.Skeleton) },
        };
    }

    /// <summary>Whether the edited file at <paramref name="gamePath"/> names another human race in its header than its path: an All races save builds it for its path's.</summary>
    public static bool HeaderDiffers(string gamePath, string? fileModel) => fileModel != null && Model(RaceOf(gamePath)) != fileModel;

    /// <summary>
    /// Whether an All races save builds the edited file as a copy for its path's race, instead of as the Skeleton choice says: its header names
    /// another race, or a repair would go onto the standard of the race of <paramref name="skeletonModel"/>, the character's, rather than its path's.
    /// </summary>
    public static bool EditedRebuilds(RaceFile edited, SkeletonStandard? repair, string? fileModel, string? skeletonModel) =>
        edited.Target != null && (repair != null ? skeletonModel != Model(edited.Race) : HeaderDiffers(edited.GamePath, fileModel));

    /// <summary>
    /// The bones a copy has to hold: the clip's own (<paramref name="kept"/>) and those the turned-on <paramref name="layers"/> name, so a standard is
    /// picked that holds an edit on a bone the clip doesn't move, less the bones left out (<paramref name="excluded"/>).
    /// </summary>
    public static ImmutableArray<string> MovedBones(IEnumerable<string> kept, IEnumerable<MotionLayer> layers, IEnumerable<string> excluded)
    {
        var bones = new HashSet<string>(kept, StringComparer.Ordinal);
        foreach (var layer in layers.Where(layer => layer.Enabled))
        {
            bones.UnionWith(layer.Bones);
            bones.UnionWith(layer.Keys.SelectMany(key => key.Bones.Keys));
            bones.UnionWith(layer.Pins.Select(pin => pin.Bone));
            if (layer.Link is { } link)
                bones.Add(link.Bone);
        }
        bones.ExceptWith(excluded);
        return [.. bones.Order(StringComparer.Ordinal)];
    }

    /// <summary>What a save does with a path other than the edited one; null leaves it out.</summary>
    internal static RaceFileAction? Classify(RaceFileSource source, AnimationResource edited)
    {
        if (source.Collection is not { } given)
            return source.EditedModMaps ? RaceFileAction.Kept : null;
        if (given.ModDirectory != null && given.ModDirectory == edited.ModDirectory)
            return PathRules.SamePhysicalPath(given.ResolvedPath, edited.ResolvedPath) || IsCopy(given.RelativePath)
                ? RaceFileAction.Rebuild : RaceFileAction.Kept;
        return source.EditedModMaps ? RaceFileAction.Kept : RaceFileAction.Rebuild;
    }

    /// <summary>Whether a mod-relative file is a copy an earlier All races save made.</summary>
    public static bool IsCopy(string? relativePath) =>
        relativePath?.Replace('\\', '/').StartsWith(Folder, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// The standard a race's copy is built on: <paramref name="repair"/> or the largest it has below that; without a repair the
    /// smallest holding every bone in <paramref name="kept"/>, else its largest. With how many of those bones it lacks. Null without any standard.
    /// </summary>
    public static (StandardSkeleton? Target, int BonesLeftOut) PickStandard(ImmutableArray<StandardSkeleton> standards, IReadOnlyCollection<string> kept, SkeletonStandard? repair)
    {
        var ordered = standards.OrderBy(standard => standard.Standard).ToArray();
        if (ordered.Length == 0)
            return (null, 0);
        var target = repair is { } wanted
            ? ordered.LastOrDefault(standard => standard.Standard <= wanted) ?? ordered[0]
            : ordered.FirstOrDefault(standard => Lacking(standard, kept) == 0) ?? ordered[^1];
        return (target, Lacking(target, kept));
    }

    private static int Lacking(StandardSkeleton standard, IReadOnlyCollection<string> bones)
    {
        var have = standard.Skeleton.Bones.Select(bone => bone.Name).ToHashSet(StringComparer.Ordinal);
        return bones.Count(bone => !have.Contains(bone));
    }

    /// <summary>
    /// <paramref name="layers"/> as a copy on <paramref name="skeleton"/> applies them: without the bones it lacks, and without a layer
    /// left with none of the bones it named (an empty list would mean every bone) or one the skeleton still can't hold.
    /// </summary>
    public static ImmutableArray<MotionLayer> LayersFor(IEnumerable<MotionLayer> layers, SkeletonDescription skeleton)
    {
        var have = skeleton.Bones.Select(bone => bone.Name).ToHashSet(StringComparer.Ordinal);
        var result = ImmutableArray.CreateBuilder<MotionLayer>();
        foreach (var layer in layers)
        {
            var bones = layer.Bones.Where(have.Contains).ToImmutableArray();
            var pins = layer.Pins.Where(pin => have.Contains(pin.Bone) && (pin.RelativeTo == null || have.Contains(pin.RelativeTo)) &&
                (pin.Top == null || have.Contains(pin.Top))).ToImmutableArray();
            var keys = layer.Keys.Where(key => key.Bones.IsEmpty || key.Bones.Keys.Any(have.Contains)).Select(key => key with
            {
                Bones = key.Bones.RemoveRange(key.Bones.Keys.Where(name => !have.Contains(name))),
                Moved = key.Moved.Where(have.Contains).ToImmutableArray(),
            }).ToImmutableArray();
            if (bones.IsEmpty && !layer.Bones.IsEmpty || pins.IsEmpty && !layer.Pins.IsEmpty || keys.IsEmpty && !layer.Keys.IsEmpty)
                continue;
            var kept = layer with { Bones = bones, Pins = pins, Keys = keys };
            try { MotionEvaluator.CheckBones([kept], skeleton); }
            catch (InvalidDataException) { continue; }
            result.Add(kept);
        }
        return result.ToImmutable();
    }

    /// <summary>The race a human path names.</summary>
    public static ushort RaceOf(string path) =>
        path.Split('/') is [_, _, ['c', .. var code], ..] && ushort.TryParse(code, out var race)
            ? race : throw new InvalidDataException($"{path} names no race.");
}
