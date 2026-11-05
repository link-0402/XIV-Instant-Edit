using System.Collections.Immutable;
using System.Numerics;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Animations.Motion;

/// <summary>
/// Saving an edit for every race: which files the races load, what a save does with each, the standard
/// each copy is built on and the layers it keeps.
/// </summary>
internal static class RaceCopiesFixture
{
    private const string ModRoot = @"C:\mods\Test Mod";

    private static string Path(int race, string name = "emote/lent") => $"chara/human/c{race:D4}/animation/a0001/bt_common/{name}.pap";

    private static AnimationResource Resource(string gamePath, string relative, string mod = "Test Mod") =>
        new(gamePath, $@"C:\mods\{mod}\{relative.Replace('/', '\\')}", "hash", mod, $@"C:\mods\{mod}", relative);

    private static SkeletonDescription Rig(string fingerprint, params string[] names) => new("skeleton", fingerprint,
        [.. names.Select((name, i) => new SkeletonBone(name, (short)(i == 0 ? -1 : 0), 0, new(Vector3.Zero, Quaternion.Identity, Vector3.One)))], [], [], []);

    private static readonly BoneTransform Rest = new(Vector3.Zero, Quaternion.Identity, Vector3.One);

    private static MotionLayer Layer(MotionLayerKind kind, ImmutableArray<string> bones = default, ImmutableArray<MotionKey> keys = default, ImmutableArray<MotionPin> pins = default) =>
        new("m1", kind, "Test", 0, 10, 0, 0, 1, PoseComponents.All, MotionInterpolation.Linear, bones, keys, Pins: pins);

    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Files(check);
        Classification(check);
        Plans(check, reject);
        Standards(check);
        Layers(check);
        Reasons(check);
        Requests(check);
    }

    private static void Requests(Action<bool, string> check)
    {
        var rig = Rig("target", "n_root", "n_hara", "j_kosi");
        var target = new StandardSkeleton(SkeletonStandard.Ivcs, rig, "ivcs");
        var clip = new AnimationClip(Path(101), "lent", 0, 0, 42, "chara/test.sklb");
        var capture = new AnimationCapture("a", 7, 1, Guid.NewGuid(), "Test", "Lent", clip, null, [Path(101)], [], DateTime.UtcNow, true);
        var layer = Layer(MotionLayerKind.Offset, ["j_kosi", "iv_a"], [new MotionKey(0, ImmutableDictionary<string, BoneTransform>.Empty.Add("j_kosi", Rest).Add("iv_a", Rest))]);
        var request = new AnimationBakeRequest(Guid.NewGuid(), capture, AnimationDestination.InPlace, "", false, AnimationOperation.EditTimeline,
            RepairTarget: SkeletonStandard.Vanilla, Timeline: new AnimationTimelineEdit(false, 0, null, [layer]));

        var other = RaceCopies.CopyRequest(request, new(Path(1101), 1101, [], RaceFileAction.Rebuild, target));
        check(other is { RepairTarget: SkeletonStandard.Ivcs, RepairModel: "c1101" } && other.RepairSkeleton == rig && AnimationBakeRules.Repairs(other),
            "a copy for another race repairs onto that race's standard and names its race");
        check(other.Timeline!.Motion.Single().Bones.SequenceEqual(["j_kosi"]), "a copy keeps only the layers' bones its skeleton has");
        check(request.Timeline!.Motion.Single().Bones.Length == 2, "the edited file's own layers stay as they are");
        var repair = RaceCopies.CopyRequest(request, new(Path(101), 101, [], RaceFileAction.Rebuild, target));
        check(repair is { RepairTarget: SkeletonStandard.Ivcs, RepairModel: "c0101" } && repair.RepairSkeleton == rig,
            "a repaired copy of the header's race still names its race, or its header would take the character's");
        var own = RaceCopies.CopyRequest(request, new(Path(801), 801, [], RaceFileAction.Rebuild));
        check(own is { RepairTarget: null, RepairModel: null, RepairSkeleton: null } && !AnimationBakeRules.Repairs(own) && own.Timeline!.Motion.Single().Bones.Length == 2,
            "a file staying on its own skeleton has nothing to rebuild and keeps every layer");

        check(RaceCopies.HeaderDiffers(Path(801), "c0101") && !RaceCopies.HeaderDiffers(Path(801), "c0801") && !RaceCopies.HeaderDiffers(Path(801), null),
            "a file's header differs from its path only when it names another human race");
        var editedRow = new RaceFile(Path(101), 101, [], RaceFileAction.Edited, target);
        check(RaceCopies.EditedRebuilds(new(Path(801), 801, [], RaceFileAction.Edited, target), null, "c0101", "c0801"), "a header naming another race rebuilds the edited file");
        check(RaceCopies.EditedRebuilds(editedRow, SkeletonStandard.Ivcs, "c0101", "c0801"), "a repair on another race's character rebuilds it for its path's race");
        check(!RaceCopies.EditedRebuilds(editedRow, SkeletonStandard.Ivcs, "c0101", "c0101"), "a repair on its own race's character stays as the Skeleton choice says");
        check(!RaceCopies.EditedRebuilds(editedRow with { Target = null }, null, "c0101", "c0801"), "a file with nothing to build on isn't rebuilt");
        check(!RaceCopies.EditedRebuilds(editedRow, null, null, "c0801"), "a header naming no human race isn't rebuilt");

        var offLayer = Layer(MotionLayerKind.Offset, ["iv_off"]) with { Enabled = false };
        var moved = RaceCopies.MovedBones(["j_kosi", "gone"], [layer, offLayer, Layer(MotionLayerKind.Pin, pins: [new MotionPin("j_te_r")]),
            Layer(MotionLayerKind.Link) with { Link = new MotionLink("j_te_l", null, "j_te_l", "clip", 0) }], ["gone"]);
        check(moved.SequenceEqual(["iv_a", "j_kosi", "j_te_l", "j_te_r"]),
            "a copy's standard has to hold the bones the turned-on layers name too, less the bones left out");
        var ivcsTarget = new StandardSkeleton(SkeletonStandard.Ivcs, Rig("i", "n_root", "j_kosi", "iv_a"), "ivcs");
        var vanillaTarget = new StandardSkeleton(SkeletonStandard.Vanilla, Rig("v", "n_root", "j_kosi"), "game");
        check(RaceCopies.PickStandard([vanillaTarget, ivcsTarget], RaceCopies.MovedBones(["j_kosi"], [layer], []), null) == (ivcsTarget, 0),
            "a layer on an IVCS bone picks the IVCS standard, though the clip itself moves only game bones");

        RaceFile File(int race, RaceFileAction action, bool inMod) => new(Path(race), (ushort)race, [], action, InEditedMod: inMod);
        var plan = new RaceCopyPlan([File(101, RaceFileAction.Edited, true), File(1101, RaceFileAction.Rebuild, true), File(801, RaceFileAction.Rebuild, false),
            File(901, RaceFileAction.Kept, true), File(1501, RaceFileAction.Kept, false), File(1701, RaceFileAction.Skipped, false)]);
        check(RaceCopies.CopyPaths(plan).SequenceEqual([Path(1101), Path(801)]), "the save lists only the files it writes a copy to");
        check(RaceCopies.ManifestPaths(plan, AnimationDestination.InPlace).SequenceEqual([Path(1101)]), "in place, only a copy taking over a path the mod maps is read and rechecked");
        check(RaceCopies.ManifestPaths(plan, AnimationDestination.NewMod).SequenceEqual([Path(901)]), "a new mod carries only the edited mod's own other files");

        var rows = new RaceCopyPlan([new(Path(101), 101, [101, 301, 501, 701, 801], RaceFileAction.Edited), new(Path(1101), 1101, [1101, 1201], RaceFileAction.Rebuild, target, 2),
            new(Path(801), 801, [801], RaceFileAction.Rebuild, target, 1), new(Path(901), 901, [901], RaceFileAction.Kept), new(Path(1701), 1701, [1701], RaceFileAction.Skipped)]);
        var lines = RaceCopies.TooltipLines(rows).ToArray();
        check(lines.Length == 6 && RaceCopies.TooltipLines(null).Count() == 1, "the tooltip has a line for what it does and one per file");
        check(lines[1].Contains("Midlander (Male)", StringComparison.Ordinal) && lines[1].Contains("1 more", StringComparison.Ordinal) && !lines[1].Contains("Midlander (Male).", StringComparison.Ordinal),
            "a file's line names its race, shortens a long list of others and doesn't repeat itself");
        check(lines[2].Contains("Lalafell (Female)", StringComparison.Ordinal) && lines[2].Contains('2') && lines[3].Contains('1') && !lines[3].Contains("bones", StringComparison.Ordinal),
            "a copy's line names who shares it and counts the bones left out");
        check(lines.All(line => !line.Contains('…') && !line.Contains("...", StringComparison.Ordinal)), "no line has an ellipsis the game font can't draw");
        check(RaceCopies.CountLabel(rows) == "5 files" && RaceCopies.CountLabel(new([rows.Files[0]])) == "1 file", "the count beside the switch");

        var link = Layer(MotionLayerKind.Link) with { Link = new MotionLink("n_root", null, "n_root", "clip", 0) };
        check(RaceCopies.LayersFor([link], rig).IsEmpty, "a layer whose bones all exist but which the skeleton can't hold goes");
    }

    /// <summary>c0801 plays c0101's file, c0201 plays c0801's; c1101 has its own.</summary>
    private static PapLoadTable Table()
    {
        PapLoadTableFixture.Remap Own(uint race) => new(race, 1, 0);
        return new(PapLoadTableFixture.Table(["bt_common"], [(0, "emote/lent"), (0, "emote/own"), (0, "emote/c"), (0, "emote/d")],
            [new(801, 1, 0, [0]), new(201, 1, 1, [0]), new(101, 1, 2, [])],
            [new(101, 1, 0), Own(801), Own(801), Own(801), new(801, 1, 0), Own(201), Own(201), Own(201)]));
    }

    private static void Files(Action<bool, string> check)
    {
        var table = Table();
        ushort[] races = [101, 201, 801, 1101];
        var files = table.Files(Path(101), races);
        check(files.Length == 3 && files[0].Path == Path(101) && files[0].PlayedBy.SequenceEqual<ushort>([101, 801]) &&
              files[1].Path == Path(801) && files[1].PlayedBy.SequenceEqual<ushort>([201]) &&
              files[2].Path == Path(1101) && files[2].PlayedBy.SequenceEqual<ushort>([1101]),
            "each race's file, once, with the races that load it");
        check(table.Files(Path(1101), races).Select(f => f.Path).SequenceEqual(files.Select(f => f.Path)),
            "the files are the same from any race's path");
        check(table.Files(Path(101, "emote/own"), races).Length == 4, "a race the table leaves alone loads its own path");
        check(table.Files("chara/monster/m0001/animation/a0001/bt_common/emote/lent.pap", races).IsEmpty, "a path outside the human layout has no files");
    }

    private static void Classification(Action<bool, string> check)
    {
        var edited = Resource(Path(101), "files/edit.pap");
        RaceFileAction? At(AnimationResource? given, bool maps = false) => RaceCopies.Classify(new(given, maps), edited);
        check(At(edited with { GamePath = Path(1101) }) == RaceFileAction.Rebuild, "the mod's same file at another race's path is this edit");
        check(At(Resource(Path(1101), "instant_edit/races/chara/human/c1101/lent_x.pap")) == RaceFileAction.Rebuild, "an earlier copy is rebuilt");
        check(At(Resource(Path(1101), @"instant_edit\races\chara\human\c1101\lent_x.pap")) == RaceFileAction.Rebuild, "an earlier copy is found whichever separators the mod used");
        check(At(Resource(Path(1101), "files/lala.pap")) == RaceFileAction.Kept, "the mod's own other file stays");
        check(At(Resource(Path(1101), "instant_edit/sound.pap")) == RaceFileAction.Kept, "only the copies' folder counts as copies");
        check(At(new(Path(1101), @"C:\game\lent.pap", "hash")) == RaceFileAction.Rebuild, "a path the game gives gets a copy");
        check(At(Resource(Path(1101), "files/other.pap", "Other Mod")) == RaceFileAction.Rebuild, "a path another mod gives gets a copy, and priority decides");
        check(At(Resource(Path(1101), "files/other.pap", "Other Mod"), true) == RaceFileAction.Kept, "a path another mod outranks this one at stays");
        check(At(new(Path(1101), @"C:\game\lent.pap", "hash"), true) == RaceFileAction.Kept, "a path the mod maps in an option that's off stays");
        check(At(null) == null, "a path nothing gives is left out");
        check(At(null, true) == RaceFileAction.Kept, "a path only the mod's other option gives stays");
    }

    private static void Plans(Action<bool, string> check, Action<Action, string> reject)
    {
        var edited = Resource(Path(101), "files/edit.pap");
        var vanilla = new StandardSkeleton(SkeletonStandard.Vanilla, Rig("v", "n_root", "j_kosi"), "game");
        ImmutableArray<ushort> Who(params ushort[] races) => [.. races];
        (string, ImmutableArray<ushort>)[] files =
        [
            (Path(1101), Who(1101, 1201)), (Path(101), Who(101, 801)), (Path(801), Who(201)), (Path(901), Who(901)), (Path(1501), Who(1501)), (Path(1701), Who(1701)),
        ];
        RaceFileSource Source(string path) => path == Path(901) ? new(Resource(path, "files/own.pap"))
            : path == Path(1701) ? new(null) : new(new(path, @"C:\game\lent.pap", "hash"));
        ImmutableArray<StandardSkeleton> Standards(ushort race) => race == 1501 ? [] : [vanilla];
        var plan = RaceCopies.Plan(files, edited, "c0101", Source, Standards, ["j_kosi", "iv_ko_c_l"], null);

        check(plan.Files[0] is { Action: RaceFileAction.Edited, Race: 101, Target: null } first && first.PlayedBy.SequenceEqual<ushort>([101, 801]),
            "the edited file comes first and, of its header's race, stays on its own skeleton");
        var rows = plan.Files.ToDictionary(file => file.GamePath);
        check(rows[Path(1101)] is { Action: RaceFileAction.Rebuild, Race: 1101, BonesLeftOut: 1 } lala && lala.Target == vanilla && lala.PlayedBy.Length == 2,
            "a copy is built for the race in its path, counting the bones its standard lacks");
        check(rows[Path(801)] is { Action: RaceFileAction.Rebuild, Race: 801 } borrowed && borrowed.PlayedBy.SequenceEqual<ushort>([201]),
            "a file no player of which is its own race is still built for its path's race");
        check(rows[Path(901)] is { Action: RaceFileAction.Kept, Target: null }, "the mod's own file is kept");
        check(rows[Path(1501)].Action == RaceFileAction.Skipped, "a race with no skeleton is skipped");
        check(!rows.ContainsKey(Path(1701)), "a path nothing gives isn't in the plan");

        var other = RaceCopies.Plan(files, edited, "c0801", Source, Standards, ["j_kosi"], null);
        check(other.Files[0] is { Action: RaceFileAction.Edited, Target: not null, BonesLeftOut: 0 }, "an edited file whose header names another race than its path is built for its path's race");
        check(other.Files.Single(file => file.GamePath == Path(801)) is { Action: RaceFileAction.Rebuild, Target: null }, "a copy of the header's race stays on its own skeleton");
        var repaired = RaceCopies.Plan(files, edited, "c0101", Source, Standards, ["j_kosi"], SkeletonStandard.Vanilla);
        check(repaired.Files[0].Target == vanilla, "a repair applies to the edited file too");
        var noFile = RaceCopies.Plan([(Path(1101), Who(1101))], edited, "c0101", Source, Standards, [], null);
        check(noFile.Files[0] is { Action: RaceFileAction.Edited, PlayedBy.IsEmpty: true } && noFile.Files.Length == 2, "the edited file stays in the plan even when no race's request reaches it");
        reject(() => RaceCopies.Plan([], edited with { GamePath = "chara/test.pap" }, null, Source, Standards, [], null), "a file that names no race is refused");
    }

    private static void Standards(Action<bool, string> check)
    {
        var vanilla = new StandardSkeleton(SkeletonStandard.Vanilla, Rig("v", "n_root", "j_kosi"), "game");
        var ivcs = new StandardSkeleton(SkeletonStandard.Ivcs, Rig("i", "n_root", "j_kosi", "iv_a"), "ivcs");
        var yas = new StandardSkeleton(SkeletonStandard.IvcsYas, Rig("y", "n_root", "j_kosi", "iv_a", "ya_a"), "yas");
        ImmutableArray<StandardSkeleton> all = [yas, vanilla, ivcs];
        check(RaceCopies.PickStandard(all, ["j_kosi"], null) == (vanilla, 0), "keep: the smallest holding every bone");
        check(RaceCopies.PickStandard(all, ["iv_a"], null) == (ivcs, 0), "keep: a larger one when the smaller lacks a bone");
        check(RaceCopies.PickStandard(all, ["ya_a", "other", "another"], null) == (yas, 2), "keep: the largest there is when none holds them all, counting what it lacks");
        check(RaceCopies.PickStandard(all, ["iv_a"], SkeletonStandard.Vanilla) == (vanilla, 1), "repair: the chosen standard, even when it lacks bones");
        check(RaceCopies.PickStandard([vanilla, ivcs], [], SkeletonStandard.IvcsYas) == (ivcs, 0), "repair: the largest below it when the race hasn't got it");
        check(RaceCopies.PickStandard([], ["j_kosi"], null) == (null, 0), "no standard at all");
    }

    private static void Layers(Action<bool, string> check)
    {
        var rig = Rig("r", "n_root", "n_hara", "j_kosi");
        MotionKey Key(params string[] bones) => new(0, bones.ToImmutableDictionary(bone => bone, _ => Rest), [.. bones]);
        var plain = Layer(MotionLayerKind.Offset, ["j_kosi"], [Key("j_kosi")]);
        check(RaceCopies.LayersFor([plain], rig).Single() is { Key: "m1", Kind: MotionLayerKind.Offset } same && same.Bones.SequenceEqual(["j_kosi"]) &&
              same.Keys[0].Bones.ContainsKey("j_kosi"), "a layer on bones the skeleton has stays");
        var mixed = RaceCopies.LayersFor([Layer(MotionLayerKind.Offset, ["j_kosi", "iv_a"], [Key("j_kosi", "iv_a")])], rig).Single();
        check(mixed.Bones.SequenceEqual(["j_kosi"]) && mixed.Keys[0].Bones.Keys.SequenceEqual(["j_kosi"]) && mixed.Keys[0].Moved.SequenceEqual(["j_kosi"]),
            "a missing bone leaves the layer and its keys");
        check(RaceCopies.LayersFor([Layer(MotionLayerKind.Amplitude, ["iv_a"])], rig).IsEmpty, "a layer left with no bones goes, not onto every bone");
        check(RaceCopies.LayersFor([Layer(MotionLayerKind.Amplitude)], rig).Length == 1, "a layer on every bone stays");
        check(RaceCopies.LayersFor([Layer(MotionLayerKind.Offset, keys: [Key("iv_a")])], rig).IsEmpty, "a layer left with no keys goes");
        var keys = RaceCopies.LayersFor([Layer(MotionLayerKind.Offset, keys: [Key("iv_a"), Key("j_kosi")])], rig).Single();
        check(keys.Keys.Length == 1 && keys.Keys[0].Bones.ContainsKey("j_kosi"), "a key left with no bones goes");
        check(RaceCopies.LayersFor([Layer(MotionLayerKind.Pin, pins: [new MotionPin("iv_a")])], rig).IsEmpty, "a pin on a missing bone goes with its layer");
        check(RaceCopies.LayersFor([Layer(MotionLayerKind.Pin, pins: [new MotionPin("j_kosi", RelativeTo: "iv_a")])], rig).IsEmpty, "a pin relative to a missing bone goes");
    }

    private static void Reasons(Action<bool, string> check)
    {
        var table = Table();
        ushort[] races = [101, 201, 801, 1101];
        string? Why(string path, bool face = false, bool others = false, bool motion = false, PapLoadTable? with = null, ushort[]? who = null) =>
            RaceCopies.Unavailable(path, face, others, motion, with ?? table, who ?? races);
        check(Why(Path(101)) == null, "an emote many races load can be saved for every race");
        check(Why(Path(101), face: true) != null, "a face can't");
        check(Why("chara/human/c0101/animation/a0001/bt_2ax_emp/emote/lent.pap") != null, "a weapon folder's file can't");
        check(Why("chara/human/c0101/animation/f0002/nonresident/smile.pap") != null, "a face folder's file can't");
        check(Why("chara/monster/m0001/animation/a0001/bt_common/emote/lent.pap") != null, "a monster's file can't");
        check(Why(Path(101), others: true) != null, "a file holding other animations can't");
        check(Why(Path(101), motion: true) != null, "a motion from Blender can't");
        check(RaceCopies.Unavailable(Path(101), false, false, false, null, races) != null, "without the load table it can't");
        check(Why(Path(101), who: [101, 801]) != null, "a file every race plays can't");
        check(RaceCopies.InCommonFolder(Path(101, "resident/idle")) && RaceCopies.InCommonFolder(Path(101, "emote_sp/sp10_loop")), "idles and special emotes are in the common folder");
        check(RaceCopies.IsCopy("instant_edit/races/x.pap") && RaceCopies.IsCopy(@"Instant_Edit\Races\x.pap") && !RaceCopies.IsCopy("files/x.pap") && !RaceCopies.IsCopy(null), "copies are found by their folder");
    }
}
