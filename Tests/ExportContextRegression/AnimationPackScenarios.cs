using InstantEdit.Studio;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Services.Animation;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// Animation items of the Build window: where an emote's files go for each race (through the game's
/// PAP load table), what is kept in the settings, and the timelines an emote offers.
/// </summary>
internal static class AnimationPackScenarios
{
    public static void Run(string testRoot)
    {
        var root = Path.Combine(testRoot, "AnimationPack");
        Directory.CreateDirectory(root);

        PapAndTmbPaths();
        EveryTickedRaceGetsItsPath();
        BorrowedRacesShareOneFile();
        EmptyRacesMeansAll();
        TimelineRacesOverrideItemRaces();
        TmbIsWrittenOnce();
        NoFilesNoPlans();
        OldSettingsHaveNoAnimations();
        AnimationSubjectKeys();
        TimelineRefsSayWhatCanBeReplaced();
        AnimationFileCheckReadsMagic(root);
        AnimationItemIsCopiedToEveryPath(root);
        UnsafeKeysPlanNothing();
        PathsTheGameLacksAreFound();
        UntickedRacesSharingAFileAreNamed();
        EmptyRowsSayWhatPlays();
        AnimationKeysWaitForTheEmoteList();
    }

    private static void EmptyRowsSayWhatPlays()
    {
        var setup = Setup("emote/sit_start", @"C:\a\x.pap");
        var empty = new AnimationTimelineSetup { Key = "emote/sit_loop", Label = "Loop" };
        var timingOnly = new AnimationTimelineSetup { Key = "emote/sit_end", Label = "End", TmbSource = @"C:\a\x.tmb" };
        setup.Timelines.Add(empty);
        setup.Timelines.Add(timingOnly);
        Require(AnimationSetup.RowHint(setup, setup.Timelines[0]) == null, "a row with a .pap needs no hint");
        var hint = AnimationSetup.RowHint(setup, empty) ?? "";
        Require(hint.Contains("game's own", StringComparison.Ordinal), "an empty row of an item with files says the game's own animation plays there");
        var timing = AnimationSetup.RowHint(setup, timingOnly) ?? "";
        Require(timing.Contains("motion", StringComparison.Ordinal), "a row with only a .tmb says the game's own motion plays with it");
        var nothing = Setup("emote/joy", "");
        Require(AnimationSetup.RowHint(nothing, nothing.Timelines[0]) == null, "an item with no files at all leaves the hint to its own warning");
        Require(!(hint + timing).Contains('…'), "the hints have no ellipsis character");
    }

    private static void AnimationKeysWaitForTheEmoteList()
    {
        uint gear = 4321, emote = 0x2000_0005, timeline = 0x2100_0007, feature = 0x8000_1234;
        Require(ModpackLoading.IsAnimationKey(emote) && ModpackLoading.IsAnimationKey(timeline) && !ModpackLoading.IsAnimationKey(gear)
                && !ModpackLoading.IsAnimationKey(feature), "emote and timeline keys are animation keys, gear and feature keys are not");
        Require(!ModpackLoading.CanResolve(new[] { gear, emote }, AnimationScanState.Pending),
            "a pack holding an animation item waits while the emote list is still being read, rather than blocking for it");
        Require(ModpackLoading.CanResolve(new[] { gear, feature }, AnimationScanState.Pending), "a pack without animation items never waits for it");
        Require(ModpackLoading.CanResolve(new[] { emote }, AnimationScanState.Ready) && ModpackLoading.CanResolve(new[] { emote }, AnimationScanState.Failed),
            "once the emote list is read, or failed to read, the pack loads");
        Require(!ModpackLoading.IsGone(emote, AnimationScanState.Failed),
            "an animation item is kept in the pack when the emote list could not be read");
        Require(ModpackLoading.IsGone(emote, AnimationScanState.Ready), "an animation the read emote list doesn't have is gone");
        Require(ModpackLoading.IsGone(gear, AnimationScanState.Failed), "a gear item that resolves to nothing is gone whatever the emote list did");
    }

    private static void UnsafeKeysPlanNothing()
    {
        Require(AnimationPathPlanner.Plan(Setup("../../evil", @"C:\a\x.pap", @"C:\a\x.tmb"), new[] { "0101" }, Identity).Count == 0,
            "a timeline key that is no safe game path plans nothing");
    }

    private static void PathsTheGameLacksAreFound()
    {
        var plans = AnimationPathPlanner.Plan(Setup("emote/joy", @"C:\a\x.pap", @"C:\a\x.tmb", "0101", "0201"), new[] { "0101", "0201" }, Identity);
        var missing = AnimationPathPlanner.MissingFromGame(plans, path => path.Contains("/c0101/", StringComparison.Ordinal));
        Require(missing.Select(p => p.GamePath).SequenceEqual(new[] { "chara/human/c0201/animation/a0001/bt_common/emote/joy.pap" }),
            "a PAP path the game has no file at is found; TMBs and existing paths are not");
    }

    private static void UntickedRacesSharingAFileAreNamed()
    {
        string Resolve(string path) => path.Replace("/c0801/", "/c0101/", StringComparison.Ordinal);
        var plans = AnimationPathPlanner.Plan(Setup("emote/joy", @"C:\a\x.pap", "", "0801"), new[] { "0101", "0201", "0801" }, Resolve);
        Require(plans.Single() is { GamePath: "chara/human/c0101/animation/a0001/bt_common/emote/joy.pap" } plan &&
                plan.Races.SequenceEqual(new[] { "0801" }) && plan.AlsoServes.SequenceEqual(new[] { "0101" }),
            "a file written for a ticked race that borrows another's copy names the unticked races that play it too");
        var all = AnimationPathPlanner.Plan(Setup("emote/joy", @"C:\a\x.pap"), new[] { "0101", "0801" }, Resolve);
        Require(all.Single().AlsoServes.Count == 0, "with every race ticked no other race is affected");
    }

    private static void AnimationFileCheckReadsMagic(string root)
    {
        var pap = Path.Combine(root, "good.pap");
        var tmb = Path.Combine(root, "good.tmb");
        var junk = Path.Combine(root, "junk.pap");
        File.WriteAllBytes(pap, "pap \u0001\u0002"u8.ToArray());
        File.WriteAllBytes(tmb, "TMLB\u0001\u0002"u8.ToArray());
        File.WriteAllBytes(junk, "RIFF\u0001\u0002"u8.ToArray());
        Require(AnimationFileCheck.Problem(pap, tmb: false) == null, "a file starting with \"pap \" is a PAP");
        Require(AnimationFileCheck.Problem(tmb, tmb: true) == null, "a file starting with TMLB is a TMB");
        Require(AnimationFileCheck.Problem(junk, tmb: false) == "Not a .pap file.", "anything else is not a PAP");
        Require(AnimationFileCheck.Problem(pap, tmb: true) == "Not a .tmb file.", "a PAP is not a TMB");
        Require(AnimationFileCheck.Problem(Path.Combine(root, "missing.pap"), tmb: false) == "File not found.", "a missing file says so");
    }

    private static void AnimationItemIsCopiedToEveryPath(string root)
    {
        var source = Path.Combine(root, "joy.pap");
        var bytes = "pap \u0001\u0002\u0003"u8.ToArray();
        File.WriteAllBytes(source, bytes);
        var modRoot = Path.Combine(root, "mods");
        Directory.CreateDirectory(modRoot);

        var target = new AnimationTarget(0x2000_0010, "Joy", 0, "/joy", new[] { new AnimationTimelineRef(3, "emote/joy", "joy", true, null) });
        var plans = new[]
        {
            new AnimationFilePlan(source, "chara/human/c0101/animation/a0001/bt_common/emote/joy.pap", new[] { "0101" }),
            new AnimationFilePlan(source, "chara/human/c0201/animation/a0001/bt_common/emote/joy.pap", new[] { "0201" }),
        };
        var mod = Path.Combine(modRoot, "Joy Mod");
        var report = new InstantEdit.Studio.Services.BuildReport { ModName = "Joy Mod", ModPath = mod, ItemsSummary = target.Name };
        var withMissing = plans.Append(new AnimationFilePlan(Path.Combine(root, "gone.pap"), "chara/human/c0301/animation/a0001/bt_common/emote/joy.pap", new[] { "0301" })).ToList();
        var steps = 0;
        var written = AnimationFileWriter.Write(withMissing, mod, gamePath => $"Option/{gamePath}", report,
            new Target(TargetKind.Item, target.Key), _ => steps++, CancellationToken.None);

        foreach (var plan in plans)
        {
            var file = Path.Combine(mod, "Option", plan.GamePath.Replace('/', Path.DirectorySeparatorChar));
            Require(File.Exists(file) && File.ReadAllBytes(file).SequenceEqual(bytes), $"{plan.GamePath} holds the dropped file, in the item's place");
        }
        Require(written.Select(w => w.GamePath).SequenceEqual(plans.Select(p => p.GamePath)) && written.All(w => w.Rel.StartsWith("Option/", StringComparison.Ordinal)),
            "every copied file is returned with its path in the mod, for meta.json");
        Require(report.Failed == 1 && report.Entries.Single(e => e.Outcome == InstantEdit.Studio.Services.BuildOutcome.Failed).Detail == "file not found" && steps == 3,
            "a source that is gone is reported as failed, and every plan is a progress step");
    }

    private static void TimelineRefsSayWhatCanBeReplaced()
    {
        var body = EmoteCatalog.Ref(1, "emote/joy", 0);
        Require(body is { Editable: true, Reason: null, Label: "joy" }, "a body timeline under bt_common can be replaced");
        Require(EmoteCatalog.Ref(4, "emote/x_ground", 2).Editable, "load type 2 timelines read bt_common too");
        var face = EmoteCatalog.Ref(2, "facial/pose/smile", 0);
        Require(!face.Editable && face.Reason == "Face animations live per face; not supported yet.", "an expression's face is not replaceable here, and says why");
        var weapon = EmoteCatalog.Ref(3, "emote/x", 1);
        Require(!weapon.Editable && weapon.Reason == "Weapon-specific animation; not supported yet.", "a weapon-stance timeline is not replaceable here, and says why");
        Require(EmoteCatalog.LabelFor("emote/sit_start") == "Start" && EmoteCatalog.LabelFor("emote/dance12_loop") == "Loop" &&
                EmoteCatalog.LabelFor("emote/sit_end") == "End" && EmoteCatalog.LabelFor("emote/joy") == "joy",
            "rows are labelled Start, Loop and End, or by the key's last part");
        Require(EmoteCatalog.Ref(5, "", 0) is { Editable: false }, "a timeline without a key can't be replaced");
    }

    private static readonly Func<string, string> Identity = p => p;

    private static AnimationSetup Setup(string key, string pap, string tmb = "", params string[] races)
    {
        var setup = new AnimationSetup();
        setup.Races.AddRange(races);
        setup.Timelines.Add(new AnimationTimelineSetup { Key = key, Label = "Loop", PapSource = pap, TmbSource = tmb });
        return setup;
    }

    private static void PapAndTmbPaths()
    {
        Require(AnimationPathPlanner.PapPath("0101", "emote/joy") == "chara/human/c0101/animation/a0001/bt_common/emote/joy.pap",
            "a body emote's PAP sits in the race's bt_common folder");
        Require(AnimationPathPlanner.TmbPath("emote/joy") == "chara/action/emote/joy.tmb", "an emote's TMB sits under chara/action");
    }

    private static void EveryTickedRaceGetsItsPath()
    {
        var plans = AnimationPathPlanner.Plan(Setup("emote/joy", @"C:\a\x.pap", "", "0101", "0201"), new[] { "0101", "0201", "0801" }, Identity);
        Require(plans.Select(p => p.GamePath).SequenceEqual(new[]
            {
                "chara/human/c0101/animation/a0001/bt_common/emote/joy.pap",
                "chara/human/c0201/animation/a0001/bt_common/emote/joy.pap",
            }), "each ticked race gets the file at its own path, in path order");
        Require(plans.All(p => p.Source == @"C:\a\x.pap"), "every path gets the dropped file");
    }

    private static void BorrowedRacesShareOneFile()
    {
        string Resolve(string path) => path.Replace("/c0801/", "/c0101/", StringComparison.Ordinal);
        var plans = AnimationPathPlanner.Plan(Setup("emote/joy", @"C:\a\x.pap", "", "0101", "0801"), new[] { "0101", "0801" }, Resolve);
        Require(plans.Count == 1 && plans[0].GamePath == "chara/human/c0101/animation/a0001/bt_common/emote/joy.pap",
            "a race the game sends to another race's copy is served by that one file, written once");
        Require(plans[0].Races.SequenceEqual(new[] { "0101", "0801" }), "the plan lists every ticked race it serves");
    }

    private static void EmptyRacesMeansAll()
    {
        var plans = AnimationPathPlanner.Plan(Setup("emote/joy", @"C:\a\x.pap"), new[] { "0101", "0201", "0301" }, Identity);
        Require(plans.Count == 3, "no races ticked by hand means every race");
    }

    private static void TimelineRacesOverrideItemRaces()
    {
        var setup = Setup("emote/joy", @"C:\a\x.pap", "", "0101", "0201");
        setup.Timelines[0].Races = new List<string> { "0301" };
        var plans = AnimationPathPlanner.Plan(setup, new[] { "0101", "0201", "0301" }, Identity);
        Require(plans.Single().GamePath.Contains("/c0301/", StringComparison.Ordinal), "a timeline with races of its own uses those");
    }

    private static void TmbIsWrittenOnce()
    {
        var plans = AnimationPathPlanner.Plan(Setup("emote/joy", @"C:\a\x.pap", @"C:\a\x.tmb", "0101", "0201"), new[] { "0101", "0201" }, Identity);
        var tmb = plans.Where(p => p.GamePath.EndsWith(".tmb", StringComparison.Ordinal)).ToList();
        Require(tmb.Count == 1 && tmb[0].GamePath == "chara/action/emote/joy.tmb" && tmb[0].Source == @"C:\a\x.tmb",
            "the TMB is one file for every race");
        Require(AnimationPathPlanner.Plan(Setup("emote/joy", "", @"C:\a\x.tmb"), new[] { "0101" }, Identity).Single().GamePath == "chara/action/emote/joy.tmb",
            "a TMB alone is planned without a PAP");
    }

    private static void NoFilesNoPlans()
    {
        Require(AnimationPathPlanner.Plan(Setup("emote/joy", ""), new[] { "0101" }, Identity).Count == 0, "a timeline without files plans nothing");
    }

    private static void OldSettingsHaveNoAnimations()
    {
        var settings = Newtonsoft.Json.JsonConvert.DeserializeObject<StudioSettings>("{\"ModpackItems\":[5],\"ModName\":\"Old\"}")!;
        Require(settings.AnimationsByItem.Count == 0 && settings.ModName == "Old", "settings from before animation items load with none");
        Require(!settings.HasWork(5), "an item without a set-up has no work");
        settings.AnimationsByItem[0x2000_0005] = Setup("emote/joy", @"C:\a\x.pap");
        Require(settings.HasWork(0x2000_0005), "an animation item with a file has work");
    }

    private static void AnimationSubjectKeys()
    {
        var target = new AnimationTarget(0x2000_0010, "Joy", 64001, "/joy",
            new[] { new AnimationTimelineRef(3, "emote/joy", "joy", true, null) });
        var subject = new AnimationSubject(target);
        Require(subject is { Kind: SubjectKind.Animation, Key: 0x2000_0010u, HasRaces: false, UsesEqdp: false, UsesImc: false, IdDisplay: "/joy" },
            "an animation subject is keyed by its target and has no races, EQDP or IMC of its own");
        Require(PortSubject.KeyTag(subject.Key) == KeyTagKind.Emote, "an emote key carries the emote tag");
        var fresh = AnimationSetup.For(target);
        Require(fresh.Timelines.Count == 1 && fresh.Timelines[0].Key == "emote/joy" && fresh.Races.Count == 0,
            "a new animation set-up has one row per editable timeline and every race");
    }
}
