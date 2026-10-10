using InstantEdit.Studio.Services.Library;
using InstantEdit.Studio.Services.Project;
using Newtonsoft.Json.Linq;
using static InstantEdit.TestSupport.Assertions;

/// <summary>The library's filters: what a mod changes, mods enabled nowhere, and duplicate installs. Ported from Mod Optimizer's feature tests.</summary>
internal static class LibraryFilterScenarios
{
    public static void Run(string testRoot)
    {
        ClassifierRecognizesFacesIdlesAndGear();
        ChangedItemsJoinThePathTags();
        FilterCombinesUnusedDuplicateAndReplacementCriteria();
        UsageCountsEveryCollectionAndSkipsDisabledMods();
        DuplicateFinderMatchesIdenticalInstallsDespiteDifferentGuids();
        OnlyModsSharingANameAreRead();
        InvalidateRescansOnlyThatMod();
        SameNamedModsWithOtherFilesAreNotHashed();
        CaseTwinRedirectionsDontBreakTheFingerprint();
        AFailingModDoesntStopTheScan();
    }

    private static void SameNamedModsWithOtherFilesAreNotHashed()
    {
        using var small = CurrentMod("Same", Guid.NewGuid().ToString(), "A");
        using var large = CurrentMod("Same", Guid.NewGuid().ToString(), "A much larger file");
        var mods = new[] { Installed(small, "Same"), Installed(large, "Same") };
        var read = new List<string>();
        var sets = DuplicateModScan.Find(mods, folder => { read.Add(folder); return DuplicateModScan.Fingerprint(folder); });
        Require(sets.Count == 0 && read.Count == 0, "same-named mods whose files differ in size are told apart without hashing");
    }

    private static void CaseTwinRedirectionsDontBreakTheFingerprint()
    {
        using var mod = CurrentMod("Twins", Guid.NewGuid().ToString(), "A");
        var meta = JObject.Parse(File.ReadAllText(Path.Combine(mod.Path, "meta.json")));
        var files = (JObject)meta["DefaultData"]!["Files"]!;
        files["chara/Twin.bin"] = "files/a.bin";
        files["chara/twin.bin"] = "files/default.bin";
        mod.Meta(meta.ToString());
        ModFingerprint? print = null;
        try { print = DuplicateModScan.Fingerprint(mod.Path); }
        catch (ArgumentException) { }
        Require(print is not null, "game paths that differ only in case still give a fingerprint");
    }

    private static void AFailingModDoesntStopTheScan()
    {
        using var copyA = CurrentMod("Copy", Guid.NewGuid().ToString(), "A");
        using var copyB = CurrentMod("Copy", Guid.NewGuid().ToString(), "A");
        var mods = new[] { Installed(copyA, "Copy"), Installed(copyB, "Copy") };
        var calls = 0;
        var scans = new LibraryScans(() => new Dictionary<Guid, string>(), _ => null, _ => Array.Empty<(string, ChangeKind)>(),
            folder => { Interlocked.Increment(ref calls); throw new InvalidOperationException("unexpected"); });
        scans.Need(usage: false, duplicates: true, changes: false, mods);
        scans.Idle.Wait(TimeSpan.FromSeconds(30));
        Require(scans.DuplicatesOf is { Count: 0 }, "a mod that can't be read is no duplicate, and the scan finishes");
        scans.Need(usage: false, duplicates: true, changes: false, mods);
        scans.Idle.Wait(TimeSpan.FromSeconds(30));
        Require(calls == 2, "and the finished scan isn't started again");
    }

    private static ChangeTag Single(string gamePath)
    {
        var tags = ChangesIndex.Classify(new[] { gamePath }, Array.Empty<(string, ChangeKind)>());
        Require(tags.Count == 1, $"{gamePath} gives one tag (got {tags.Count})");
        return tags[0];
    }

    private static void ClassifierRecognizesFacesIdlesAndGear()
    {
        (string Path, ChangeKind Kind, string Label)[] cases =
        [
            ("chara/human/c1401/obj/face/f0101/model/c1401f0101_fac.mdl", ChangeKind.Face, "Au Ra (Female) face 101 (f0101)"),
            ("chara/human/c0101/animation/a0001/bt_common/resident/idle.pap", ChangeKind.StandingIdle, "Midlander (Male) base idle (idle.pap)"),
            ("chara/human/c0801/animation/a0001/bt_common/emote/pose03_loop.pap", ChangeKind.StandingIdle, "Miqo'te (Female) standing idle pose 03"),
            ("chara/human/c1801/animation/a0001/bt_common/emote/j_pose02_loop.pap", ChangeKind.GroundSit, "Viera (Female) groundsit pose 02"),
            ("chara/human/c0201/animation/a0001/bt_common/emote/s_pose01_loop.pap", ChangeKind.ChairSit, "Midlander (Female) chair sit pose 01"),
            ("chara/human/c0201/animation/a0001/bt_common/emote/dance.pap", ChangeKind.Emote, "Midlander (Female) dance animation"),
            ("chara/equipment/e6016/model/c0201e6016_top.mdl", ChangeKind.Item, "equipment model e6016"),
        ];
        foreach (var (path, kind, label) in cases)
        {
            var tag = Single(path);
            Require(tag.Kind == kind && tag.Label == label, $"{path} is {kind} '{label}' (got {tag.Kind} '{tag.Label}')");
        }
        Require(ChangesIndex.Classify(new[] { @"Chara\Human\C1401\obj\face\f0101\model\c1401f0101_fac.mdl" }, Array.Empty<(string, ChangeKind)>()).Count == 1,
            "backslashes and upper case are recognised too");
        Require(ChangesIndex.Classify(new[] { "chara/xls/charamake/human.cmp", "ui/icon/000000/000001.tex" }, Array.Empty<(string, ChangeKind)>()).Count == 0,
            "paths that are none of these give no tag");
        Require(ChangesIndex.Matches(new[] { Single("chara/human/c0801/animation/a0001/bt_common/emote/pose03_loop.pap") }, SearchIn.StandingIdle, "miqote 03"),
            "a search finds Miqo'te without the apostrophe");
    }

    private static void ChangedItemsJoinThePathTags()
    {
        var tags = ChangesIndex.Classify(new[] { "chara/equipment/e6016/model/c0201e6016_top.mdl", "chara/equipment/e6016/model/c0101e6016_top.mdl" },
            new[] { ("Augmented Shire Custodian's Jacket", ChangeKind.Item), ("Dance", ChangeKind.Emote) });
        Require(tags.Count == 3, "a model id is one tag however many races it has, plus one per changed item");
        Require(ChangesIndex.Matches(tags, SearchIn.Item, "e6016") && ChangesIndex.Matches(tags, SearchIn.Item, "custodian jacket"),
            "an item is found by model id or by its name");
        Require(ChangesIndex.Matches(tags, SearchIn.Emote, "dance") && !ChangesIndex.Matches(tags, SearchIn.Face, "dance"),
            "a kind only searches its own tags");
        Require(ChangesIndex.Matches(tags, SearchIn.Anything, "dance") && ChangesIndex.Matches(tags, SearchIn.Anything, "e6016"),
            "Anything searches every tag");
    }

    private static void FilterCombinesUnusedDuplicateAndReplacementCriteria()
    {
        var used = new ModUsage(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "a" }, 1);
        var replacements = new Dictionary<string, IReadOnlyList<ChangeTag>>
        {
            ["a"] = ChangesIndex.Classify(new[] { "chara/human/c1401/obj/face/f0101/model/c1401f0101_fac.mdl" }, Array.Empty<(string, ChangeKind)>()),
            ["b"] = ChangesIndex.Classify(new[] { "chara/human/c1801/animation/a0001/bt_common/emote/j_pose02_loop.pap" }, Array.Empty<(string, ChangeKind)>()),
            ["c"] = ChangesIndex.Classify(Array.Empty<string>(), new[] { ("Augmented Shire Custodian's Jacket", ChangeKind.Item) }),
        };
        string[] mods = ["a", "b", "c"];
        Require(mods.Where(m => !used.Used.Contains(m)).SequenceEqual(["b", "c"]), "mods enabled nowhere are b and c");
        Require(mods.Where(m => !used.Used.Contains(m) && ChangesIndex.Matches(replacements[m], SearchIn.GroundSit, "")).SequenceEqual(["b"]),
            "an empty search of a kind finds every mod that changes one");
        Require(mods.Where(m => ChangesIndex.Matches(replacements[m], SearchIn.Anything, "custodian jacket")).SequenceEqual(["c"]),
            "all words must match one tag");
        Require(!mods.Any(m => ChangesIndex.Matches(replacements[m], SearchIn.Face, "Viera")), "a groundsit is no face");
    }

    private static void UsageCountsEveryCollectionAndSkipsDisabledMods()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var broken = Guid.NewGuid();
        var usage = ModUsageScan.From(new Dictionary<Guid, string> { [first] = "Default", [second] = "Alt", [broken] = "Gone" }, id =>
            id == first ? new Dictionary<string, bool> { ["A"] = true, ["B"] = false }
          : id == second ? new Dictionary<string, bool> { ["C"] = true }
          : null);
        Require(usage.Collections == 3 && usage.Used.SetEquals(["A", "C"]) && usage.Used.Contains("a"),
            "a mod enabled in any collection is used; a disabled one is not; folder names ignore case");
    }

    private static TestModFolder CurrentMod(string name, string groupId, string aContent)
    {
        var mod = new TestModFolder();
        mod.File("files/a.bin", System.Text.Encoding.UTF8.GetBytes(aContent));
        mod.File("files/default.bin", "D"u8.ToArray());
        mod.File("images/preview.png", "png"u8.ToArray());
        mod.File(".instant-edit-project.json", System.Text.Encoding.UTF8.GetBytes(Guid.NewGuid().ToString()));
        mod.Meta(new JObject
        {
            ["FileVersion"] = 4,
            ["Identifier"] = Guid.NewGuid().ToString(),
            ["LastWrite"] = DateTime.UtcNow.Ticks,
            ["Name"] = name,
            ["Author"] = "Author",
            ["DefaultData"] = new JObject { ["Files"] = new JObject { ["chara/default.bin"] = "files/default.bin" } },
            ["Groups"] = new JArray(new JObject
            {
                ["Type"] = "Single",
                ["Id"] = groupId,
                ["Name"] = "Styles",
                ["Options"] = new JArray(new JObject
                {
                    ["Id"] = Guid.NewGuid().ToString(),
                    ["Name"] = "Style A",
                    ["Files"] = new JObject { ["chara/a.bin"] = "files/a.bin" },
                }),
            }),
        }.ToString());
        return mod;
    }

    private static InstalledMod Installed(TestModFolder mod, string name, DateTime? lastWrite = null)
        => new(Path.GetFileName(mod.Path), mod.Path, name, "Author", Array.Empty<string>(), string.Empty, lastWrite ?? new DateTime(2026, 9, 1), false);

    private static void DuplicateFinderMatchesIdenticalInstallsDespiteDifferentGuids()
    {
        using var first = CurrentMod("Same Name", Guid.NewGuid().ToString(), "A");
        using var second = CurrentMod("Same Name", Guid.NewGuid().ToString(), "A");
        using var different = CurrentMod("Same Name", Guid.NewGuid().ToString(), "A!");
        var mods = new[] { Installed(first, "Same Name"), Installed(second, " same name "), Installed(different, "Same Name") };

        var sets = DuplicateModScan.Find(mods, DuplicateModScan.Fingerprint);
        Require(sets.Count == 1 && sets[0].Order().SequenceEqual(new[] { mods[0].FolderName, mods[1].FolderName }.Order()),
            "identical installs are one set despite their ids, write times and ledgers; a changed file is not a copy");
    }

    private static void OnlyModsSharingANameAreRead()
    {
        using var copyA = CurrentMod("Copy", Guid.NewGuid().ToString(), "A");
        using var copyB = CurrentMod("Copy", Guid.NewGuid().ToString(), "A");
        using var other = CurrentMod("Other", Guid.NewGuid().ToString(), "A");
        var mods = new[] { Installed(copyA, "Copy"), Installed(copyB, "Copy"), Installed(other, "Other") };

        var read = new List<string>();
        DuplicateModScan.Find(mods, folder => { read.Add(folder); return DuplicateModScan.Fingerprint(folder); });
        Require(read.Order().SequenceEqual(new[] { copyA.Path, copyB.Path }.Order()), "only mods that share a name are read");
    }

    private static void InvalidateRescansOnlyThatMod()
    {
        using var copyA = CurrentMod("Copy", Guid.NewGuid().ToString(), "A");
        using var copyB = CurrentMod("Copy", Guid.NewGuid().ToString(), "A");
        var mods = new[] { Installed(copyA, "Copy"), Installed(copyB, "Copy") };
        var reads = new List<string>();
        var scans = new LibraryScans(() => new Dictionary<Guid, string>(), _ => null, _ => Array.Empty<(string, ChangeKind)>(),
            folder => { lock (reads) reads.Add(folder); return DuplicateModScan.Fingerprint(folder); });

        void Settle(IReadOnlyList<InstalledMod> list)
        {
            scans.Need(usage: false, duplicates: true, changes: false, list);
            scans.Idle.Wait(TimeSpan.FromSeconds(30));
        }

        Settle(mods);
        Require(reads.Count == 2 && scans.DuplicatesOf is { } dups && dups[mods[0].FolderName].SequenceEqual([mods[1].FolderName]),
            "the first scan reads both copies and names each one's other copy");
        Settle(mods);
        Require(reads.Count == 2, "asking again reads nothing");

        scans.Invalidate(mods[0].FolderName);
        Settle(mods);
        Require(reads.Count == 3 && reads[2] == copyA.Path, "after a mod changed only that mod is read again");

        var moved = new[] { mods[0], mods[1] with { LastWrite = mods[1].LastWrite.AddMinutes(1) } };
        Settle(moved);
        Require(reads.Count == 4 && reads[3] == copyB.Path, "a new last-write time reads that mod again");
    }
}
