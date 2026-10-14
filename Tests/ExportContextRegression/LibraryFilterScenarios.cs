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
        HairIsHairAndNoFace();
        CustomizationItemsAreSortedByWhatTheyChange();
        NameAuthorTagAndFolderAreSearchedApart();
        SizeIsEveryFileInTheFolder();
        ActiveIsReadForTheCollectionAsked();
        ScansAreKeptAcrossRestarts(testRoot);
        ABrokenCacheFileMeansAFullScan(testRoot);
        FilesRewrittenInSubfoldersAreReadAgain(testRoot);
        TagsWithoutPenumbrasItemsArentKept(testRoot);
        ForgetAllWinsOverASaveInFlight(testRoot);
        ActiveIsDroppedWhenPenumbraDoesntAnswer();
    }

    private static void FilesRewrittenInSubfoldersAreReadAgain(string testRoot)
    {
        var cacheFile = Path.Combine(testRoot, $"library-scans-{Guid.NewGuid():N}.json");
        using var copyA = CurrentMod("Copy", Guid.NewGuid().ToString(), "A");
        using var copyB = CurrentMod("Copy", Guid.NewGuid().ToString(), "A");
        var mods = new[] { Installed(copyA, "Copy"), Installed(copyB, "Copy") };

        (LibraryScans Scans, List<string> Prints) Settle()
        {
            var prints = new List<string>();
            var scans = new LibraryScans(() => new Dictionary<Guid, string>(), _ => null, _ => Array.Empty<(string, ChangeKind)>(),
                folder => { lock (prints) prints.Add(folder); return DuplicateModScan.Fingerprint(folder); }, () => cacheFile);
            scans.Need(usage: false, duplicates: true, changes: false, mods, sizes: true);
            scans.Idle.Wait(TimeSpan.FromSeconds(30));
            return (scans, prints);
        }

        // Rewritten in place under files/, meta.json untouched: the mod folder's own write time stays as it was.
        void Rewrite(string content, int minutes)
        {
            var folderWrite = Directory.GetLastWriteTimeUtc(copyB.Path);
            var file = Path.Combine(copyB.Path, "files", "a.bin");
            File.WriteAllText(file, content);
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(minutes));
            Directory.SetLastWriteTimeUtc(copyB.Path, folderWrite);
        }

        var first = Settle();
        Require(first.Prints.Count == 2 && first.Scans.DuplicatesOf is { Count: 2 }, "the first session finds the two copies");
        var size = first.Scans.Sizes![mods[1].FolderName];

        Rewrite("B", 1);
        var second = Settle();
        Require(second.Prints.SequenceEqual([copyB.Path]) && second.Scans.DuplicatesOf is { Count: 0 },
            "a mod whose file in a subfolder was rewritten is read again, and only it; it is no copy any more");

        Rewrite("BBB", 2);
        var third = Settle();
        Require(third.Scans.Sizes is { } sizes && sizes[mods[1].FolderName] == size + 2, "and its size is measured again");
    }

    private static void TagsWithoutPenumbrasItemsArentKept(string testRoot)
    {
        var cacheFile = Path.Combine(testRoot, $"library-scans-{Guid.NewGuid():N}.json");
        using var mod = CurrentMod("Items", Guid.NewGuid().ToString(), "A");
        var mods = new[] { Installed(mod, "Items") };

        (LibraryScans Scans, int Calls) Settle(Func<IReadOnlyList<(string, ChangeKind)>?> items)
        {
            var calls = 0;
            var scans = new LibraryScans(() => new Dictionary<Guid, string>(), _ => null,
                _ => { Interlocked.Increment(ref calls); return items(); }, null, () => cacheFile);
            scans.Need(usage: false, duplicates: false, changes: true, mods);
            scans.Idle.Wait(TimeSpan.FromSeconds(30));
            return (scans, calls);
        }

        bool Dances(LibraryScans scans) => scans.Changes is { } changes && ChangesIndex.Matches(changes[mods[0].FolderName], SearchIn.Emote, "dance");

        var silent = Settle(() => null);
        Require(silent.Calls == 1 && silent.Scans.Changes is { Count: 1 } && silent.Scans.Error is null,
            "when Penumbra says nothing the mod still gets its path tags");
        var thrown = Settle(() => throw new InvalidOperationException("gone"));
        Require(thrown.Calls == 1, "tags made without Penumbra's items aren't kept: the next session asks again");
        var answered = Settle(() => new[] { ("Dance", ChangeKind.Emote) });
        Require(answered.Calls == 1 && Dances(answered.Scans), "also after Penumbra failed outright");
        var kept = Settle(() => null);
        Require(kept.Calls == 0 && Dances(kept.Scans), "tags made with Penumbra's items are kept");
    }

    private static void ForgetAllWinsOverASaveInFlight(string testRoot)
    {
        var cacheFile = Path.Combine(testRoot, $"library-scans-{Guid.NewGuid():N}.json");
        using var mod = CurrentMod("Forget", Guid.NewGuid().ToString(), "A");
        var mods = new[] { Installed(mod, "Forget") };
        var scans = new LibraryScans(() => new Dictionary<Guid, string>(), _ => null, _ => Array.Empty<(string, ChangeKind)>(), null, () => cacheFile);

        // Ctrl+click Refresh lands after the save took its snapshot and before the file is replaced.
        scans.SavingForRegression = () => scans.ForgetAll();
        scans.Need(usage: false, duplicates: false, changes: true, mods);
        scans.Idle.Wait(TimeSpan.FromSeconds(30));
        Require(!File.Exists(cacheFile) && !File.Exists(cacheFile + ".tmp"), "a save in flight doesn't put back the file ForgetAll deleted");

        scans.SavingForRegression = null;
        scans.Need(usage: false, duplicates: false, changes: true, mods);
        scans.Idle.Wait(TimeSpan.FromSeconds(30));
        Require(File.Exists(cacheFile), "the next scan reads the mod again and saves");
    }

    private static void ActiveIsDroppedWhenPenumbraDoesntAnswer()
    {
        var mine = Guid.NewGuid();
        var silent = Guid.NewGuid();
        var slow = Guid.NewGuid();
        using var release = new ManualResetEventSlim(false);
        var scans = new LibraryScans(() => new Dictionary<Guid, string>(), id =>
        {
            if (id == slow)
                release.Wait(TimeSpan.FromSeconds(30));
            return id == silent ? null : new Dictionary<string, bool> { ["A"] = true };
        }, _ => Array.Empty<(string, ChangeKind)>());
        var mods = Array.Empty<InstalledMod>();

        void Settle(Guid? collection)
        {
            scans.Need(usage: false, duplicates: false, changes: false, mods, activeIn: collection);
            scans.Idle.Wait(TimeSpan.FromSeconds(30));
        }

        Settle(mine);
        Require(scans.Active is { Count: 1 } && !scans.ActiveUnknown, "the collection's enabled mods are active");
        Settle(silent);
        Require(scans.Active is null && scans.ActiveUnknown, "a collection Penumbra doesn't answer for has no Active set, not the last one's");
        Settle(mine);
        Require(scans.Active is { Count: 1 } && !scans.ActiveUnknown, "an answer brings it back");
        Settle(null);
        Require(scans.Active is null && !scans.ActiveUnknown, "no collection known: no Active set");
        Settle(mine);
        Require(scans.Active is { Count: 1 }, "the collection is read again once Penumbra names it");

        scans.Need(usage: false, duplicates: false, changes: false, mods, activeIn: slow);
        Require(scans.Active is null, "while another collection is read, the last one's set isn't shown");
        release.Set();
        scans.Idle.Wait(TimeSpan.FromSeconds(30));
        Require(scans.Active is { Count: 1 }, "the new collection's set comes in");
    }

    private static void HairIsHairAndNoFace()
    {
        var hair = Single("chara/human/c0101/obj/hair/h0005/model/c0101h0005_hir.mdl");
        Require(hair.Kind == ChangeKind.Hair && hair.Label == "Midlander (Male) hair 5 (h0005)", $"a hair path is hair (got {hair.Kind} '{hair.Label}')");
        Require(ChangesIndex.Matches(new[] { hair }, SearchIn.Hair, "midlander 5") && !ChangesIndex.Matches(new[] { hair }, SearchIn.Face, ""),
            "Hair finds it and Face doesn't");
    }

    // Penumbra's customization changed items are (race, gender, customize index, value) tuples; only the index's name matters.
    private enum CustomizeIndex : byte { Face = 5, Hairstyle = 6, TailShape = 18, FacePaint = 19 }

    private static void CustomizationItemsAreSortedByWhatTheyChange()
    {
        object Customization(CustomizeIndex index) => ((byte)1, (byte)1, index, (byte)5);
        Require(ChangesIndex.KindOfChangedItem(Customization(CustomizeIndex.Face)) == ChangeKind.Face, "a face customization is a face");
        Require(ChangesIndex.KindOfChangedItem(Customization(CustomizeIndex.Hairstyle)) == ChangeKind.Hair, "a hairstyle is hair");
        Require(ChangesIndex.KindOfChangedItem(Customization(CustomizeIndex.TailShape)) == ChangeKind.Other
             && ChangesIndex.KindOfChangedItem(Customization(CustomizeIndex.FacePaint)) == ChangeKind.Other, "tails, ears and face paint are neither");
        Require(ChangesIndex.KindOfChangedItem(null) == ChangeKind.Other && ChangesIndex.KindOfChangedItem(3) == ChangeKind.Other, "anything else is other");
    }

    private static void NameAuthorTagAndFolderAreSearchedApart()
    {
        var mod = new InstalledMod("disk_folder", @"C:\mods\disk_folder", "Bunny Ears", "Kitsune", new[] { "Accessory" }, string.Empty,
            new DateTime(2026, 9, 1), false, Folder: "Gear/Head");
        Require(ChangesIndex.MatchesListing(mod, SearchIn.Name, "bunny") && !ChangesIndex.MatchesListing(mod, SearchIn.Name, "kitsune"), "Name searches the name only");
        Require(ChangesIndex.MatchesListing(mod, SearchIn.Author, "kitsune") && !ChangesIndex.MatchesListing(mod, SearchIn.Author, "bunny"), "Author searches the author only");
        Require(ChangesIndex.MatchesListing(mod, SearchIn.Tag, "access") && !ChangesIndex.MatchesListing(mod, SearchIn.Tag, "bunny"), "Tag searches the tags only");
        Require(ChangesIndex.MatchesListing(mod, SearchIn.Folder, "gear/head") && ChangesIndex.MatchesListing(mod, SearchIn.Folder, "head")
             && !ChangesIndex.MatchesListing(mod, SearchIn.Folder, "disk_folder"), "Folder searches Penumbra's folders, not the folder on disk");
        Require(ChangesIndex.MatchesListing(mod, SearchIn.Tag, "") && !ChangesIndex.MatchesListing(mod, SearchIn.Item, "bunny"),
            "an empty search matches; a changes search is no listing search");
        Require(!ChangesIndex.IsChangesSearch(SearchIn.Folder) && ChangesIndex.IsChangesSearch(SearchIn.Hair), "Folder needs no scan; Hair does");
    }

    private static void SizeIsEveryFileInTheFolder()
    {
        using var mod = CurrentMod("Sized", Guid.NewGuid().ToString(), "ABC");
        var expected = Directory.EnumerateFiles(mod.Path, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        var scans = new LibraryScans(() => new Dictionary<Guid, string>(), _ => null, _ => Array.Empty<(string, ChangeKind)>());
        var mods = new[] { Installed(mod, "Sized") };
        scans.Need(usage: false, duplicates: false, changes: false, mods, sizes: true);
        scans.Idle.Wait(TimeSpan.FromSeconds(30));
        Require(scans.Sizes is { } sizes && sizes[mods[0].FolderName] == expected, $"the size is every file in the folder ({expected} bytes)");
    }

    private static void ActiveIsReadForTheCollectionAsked()
    {
        var mine = Guid.NewGuid();
        var other = Guid.NewGuid();
        var asked = new List<Guid>();
        var scans = new LibraryScans(() => new Dictionary<Guid, string>(), id =>
        {
            lock (asked) asked.Add(id);
            return id == mine ? new Dictionary<string, bool> { ["A"] = true, ["B"] = false } : new Dictionary<string, bool> { ["B"] = true };
        }, _ => Array.Empty<(string, ChangeKind)>());
        var mods = Array.Empty<InstalledMod>();

        void Settle(Guid collection)
        {
            scans.Need(usage: false, duplicates: false, changes: false, mods, activeIn: collection);
            scans.Idle.Wait(TimeSpan.FromSeconds(30));
        }

        Settle(mine);
        Require(scans.Active is { } active && active.SetEquals(["a"]), "the mods enabled in the collection are active, by folder name ignoring case");
        Settle(mine);
        Require(asked.Count == 1, "asking again for the same collection reads nothing");
        Settle(other);
        Require(asked.Count == 2 && scans.Active!.SetEquals(["B"]), "another collection is read again");
        scans.InvalidateUsage();
        Settle(other);
        Require(asked.Count == 3, "a settings change reads it again");
    }

    private static void ScansAreKeptAcrossRestarts(string testRoot)
    {
        var cacheFile = Path.Combine(testRoot, $"library-scans-{Guid.NewGuid():N}.json");
        using var copyA = CurrentMod("Copy", Guid.NewGuid().ToString(), "A");
        using var copyB = CurrentMod("Copy", Guid.NewGuid().ToString(), "A");
        copyA.File("chara/human/c0101/obj/hair/h0005/model/c0101h0005_hir.mdl", "M"u8.ToArray());
        var meta = JObject.Parse(File.ReadAllText(Path.Combine(copyA.Path, "meta.json")));
        ((JObject)meta["DefaultData"]!["Files"]!)["chara/human/c0101/obj/hair/h0005/model/c0101h0005_hir.mdl"] = "chara/human/c0101/obj/hair/h0005/model/c0101h0005_hir.mdl";
        copyA.Meta(meta.ToString());
        copyB.File("chara/human/c0101/obj/hair/h0005/model/c0101h0005_hir.mdl", "M"u8.ToArray());
        copyB.Meta(meta.ToString());
        var mods = new[] { Installed(copyA, "Copy"), Installed(copyB, "Copy") };

        (LibraryScans Scans, List<string> Prints, List<string> Items) Start()
        {
            var prints = new List<string>();
            var items = new List<string>();
            var scans = new LibraryScans(() => new Dictionary<Guid, string>(), _ => null,
                folder => { lock (items) items.Add(folder); return new[] { ("Dance", ChangeKind.Emote) }; },
                folder => { lock (prints) prints.Add(folder); return DuplicateModScan.Fingerprint(folder); },
                () => cacheFile);
            return (scans, prints, items);
        }

        void Settle(LibraryScans scans, IReadOnlyList<InstalledMod> list)
        {
            scans.Need(usage: false, duplicates: true, changes: true, list, sizes: true);
            scans.Idle.Wait(TimeSpan.FromSeconds(30));
        }

        var first = Start();
        Settle(first.Scans, mods);
        Require(first.Prints.Count == 2 && first.Items.Count == 2 && File.Exists(cacheFile), "the first session reads every mod and saves what it found");
        var sizes = first.Scans.Sizes!;

        var second = Start();
        Settle(second.Scans, mods);
        Require(second.Prints.Count == 0 && second.Items.Count == 0, "a new session reads nothing for mods that didn't change");
        Require(second.Scans.DuplicatesOf is { } dups && dups[mods[0].FolderName].SequenceEqual([mods[1].FolderName]),
            "the duplicates come back from the cache");
        Require(second.Scans.Changes is { } changes && ChangesIndex.Matches(changes[mods[0].FolderName], SearchIn.Hair, "midlander")
             && ChangesIndex.Matches(changes[mods[0].FolderName], SearchIn.Emote, "dance"), "what each mod changes comes back from the cache");
        Require(second.Scans.Sizes is { } kept && kept[mods[0].FolderName] == sizes[mods[0].FolderName] && kept[mods[1].FolderName] == sizes[mods[1].FolderName],
            "the sizes come back from the cache");

        var moved = new[] { mods[0], mods[1] with { LastWrite = mods[1].LastWrite.AddMinutes(1) } };
        var third = Start();
        Settle(third.Scans, moved);
        Require(third.Prints.SequenceEqual([copyB.Path]) && third.Items.SequenceEqual([mods[1].FolderName]),
            "a mod with a new last-write time is read again, and only it");
    }

    private static void ABrokenCacheFileMeansAFullScan(string testRoot)
    {
        var cacheFile = Path.Combine(testRoot, $"library-scans-{Guid.NewGuid():N}.json");
        File.WriteAllText(cacheFile, "{ not json");
        using var mod = CurrentMod("Broken", Guid.NewGuid().ToString(), "A");
        var items = 0;
        var scans = new LibraryScans(() => new Dictionary<Guid, string>(), _ => null,
            _ => { Interlocked.Increment(ref items); return Array.Empty<(string, ChangeKind)>(); }, null, () => cacheFile);
        scans.Need(usage: false, duplicates: false, changes: true, new[] { Installed(mod, "Broken") });
        scans.Idle.Wait(TimeSpan.FromSeconds(30));
        Require(items == 1 && scans.Error is null && scans.Changes is { Count: 1 }, "an unreadable cache file is ignored and the mods are read");
        Require(JObject.Parse(File.ReadAllText(cacheFile))["Version"] is not null, "and it is written again");
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
