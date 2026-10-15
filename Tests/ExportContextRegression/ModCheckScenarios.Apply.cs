using InstantEdit.Studio;
using InstantEdit.Studio.Models.Project;
using InstantEdit.Studio.Services;
using InstantEdit.Studio.Services.Backups;
using InstantEdit.Studio.Services.Check;
using Newtonsoft.Json.Linq;
using static InstantEdit.TestSupport.Assertions;

/// <summary>Applying a check's repairs: meta.json edits, backups, Penumbra's reload, deleting files nothing uses. Ported from Mod Optimizer's StellarBladeScenarioTests.</summary>
internal static partial class ModCheckScenarios
{
    public static void RunApply(string testRoot)
    {
        RecommendedRepairsLeaveNoErrors(testRoot);
        RejectedReloadRestoresTheMod(testRoot);
        ChangedModIsNotRepaired(testRoot);
        EmptySelectionChangesNothing(testRoot);
        DependentRepairsAreDroppedWithTheirPrerequisite();
        AddedEqdpEntryLoadsTheRaceModel(testRoot);
        RepairsStopWhenTheBackupCantBeStored(testRoot);
        RepairsSayWhyTheyCantRun(testRoot);
        RepairsKeepPenumbrasPathStyle(testRoot);
        AModMovedAfterItsCheckIsNotRepaired(testRoot);
        RepairsAndRestoreWorkInNonAsciiFolders(testRoot);
        ACancelBeforeApplyingChangesNothing(testRoot);
        AFailureAfterMetaChangedStillLeavesABackup(testRoot);
        ABackupFolderThatCantBeUsedSaysWhy();
        WhyNotTrustsTheSessionsStaleness(testRoot);
        FilesChangedAfterTheCheckAreNeverDeleted(testRoot);
    }

    private static void RepairsKeepPenumbrasPathStyle(string testRoot)
    {
        using var mod = new TestModFolder()
            .File("files/model.mdl", ModCheckFixtures.Mdl(["/mt_c0201e0005_top_a.mtrl"]))
            .File("files/material.mtrl", ModCheckFixtures.Mtrl([GearTexture]))
            .File("moved/base.tex", ModCheckFixtures.Tex());
        mod.Meta(new JObject
        {
            ["FileVersion"] = 4,
            ["Name"] = "Penumbra style",
            ["DefaultData"] = new JObject
            {
                ["Files"] = new JObject
                {
                    [GearModel] = @"files\model.mdl",
                    [GearMaterial.ToUpperInvariant()] = @"FILES\material.mtrl",
                    [GearTexture] = @"files\base.tex",
                },
            },
        }.ToString());

        var report = Check(mod);
        Require(!report.Findings.Any(f => f.Code is "C001" or "S004"), "upper-case keys and paths are matched without case");
        var result = ApplyRecommended(mod, report, new FakeReloader(), testRoot);
        var files = (JObject)JObject.Parse(mod.ReadMeta())["DefaultData"]!["Files"]!;
        Require(result.Ok && files[GearTexture]!.Value<string>() == @"moved\base.tex" &&
                files.Properties().Any(p => p.Name == GearMaterial.ToUpperInvariant()),
            "a retarget into a backslash meta.json writes a backslash path, and other keys keep their spelling");
    }

    private static void AModMovedAfterItsCheckIsNotRepaired(string testRoot)
    {
        var mod = StellarBladeMod();
        var report = CheckStellarBlade(mod);
        var project = ModProject.Load(mod.Path);
        var moved = mod.Path + " renamed";
        Directory.Move(mod.Path, moved);
        try
        {
            Require(RepairApplier.WhyNot(project, report, false, true, stale: MetaFingerprint.Of(project.FolderPath) != report.Fingerprint) == RepairApplier.ChangedReason,
                "a mod moved after its check is not repaired");
            var (store, cache) = NewBackups(testRoot);
            var result = RepairApplier.ApplyAsync(project, report, new RepairSelection(report), store, cache, new FakeReloader(), new StudioSettings(),
                CancellationToken.None).GetAwaiter().GetResult();
            Require(!result.Ok && result.Message == RepairApplier.ChangedReason && !Directory.Exists(mod.Path),
                "applying anyway stops before writing anything");
        }
        finally
        {
            Directory.Delete(moved, recursive: true);
        }
    }

    private static void RepairsAndRestoreWorkInNonAsciiFolders(string testRoot)
    {
        using var source = StellarBladeMod();
        using var mod = new TestModFolder($"Ünïcode Mod Ä {Guid.NewGuid():N}");
        foreach (var file in Directory.EnumerateFiles(source.Path, "*", SearchOption.AllDirectories))
            mod.File(Path.GetRelativePath(source.Path, file), File.ReadAllBytes(file));
        var before = mod.ReadMeta();
        var (store, cache) = NewBackups(testRoot);

        var result = ApplyRecommended(mod, CheckStellarBlade(mod), new FakeReloader(), testRoot, store: store, cache: cache);
        Require(result.Ok && SbHairFiles.All(file => !mod.Exists(file)), $"repairs work in a folder named with spaces and accents ({result.Message})");

        var restored = store.Restore(store.For(mod.Path).Single().Id, mod.Path, cache, new FakeReloader());
        Require(restored.Skipped.Count == 0 && mod.ReadMeta() == before && SbHairFiles.All(mod.Exists),
            "restoring the repairs puts meta.json and every deleted file back");
    }

    private static (ModBackupStore Store, string Cache) NewBackups(string testRoot)
    {
        var config = Path.Combine(testRoot, "ApplyConfig", Guid.NewGuid().ToString("N"));
        var cache = Path.Combine(testRoot, "ApplyCache", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(config);
        Directory.CreateDirectory(cache);
        var store = new ModBackupStore(config);
        store.Load();
        return (store, cache);
    }

    private static ApplyResult ApplyRecommended(TestModFolder mod, ModCheckReport report, FakeReloader reloader, string testRoot,
        bool deleteReleased = true, StudioSettings? settings = null, ModBackupStore? store = null, string? cache = null)
    {
        (ModBackupStore Store, string Cache) backups = store is null ? NewBackups(testRoot) : (store, cache!);
        var selection = new RepairSelection(report) { DeleteReleasedFiles = deleteReleased };
        return RepairApplier.ApplyAsync(ModProject.Load(mod.Path), report, selection, backups.Store, backups.Cache, reloader,
            settings ?? new StudioSettings(), CancellationToken.None).GetAwaiter().GetResult();
    }

    private static void RecommendedRepairsLeaveNoErrors(string testRoot)
    {
        using var mod = StellarBladeMod();
        var reloader = new FakeReloader();
        var result = ApplyRecommended(mod, CheckStellarBlade(mod), reloader, testRoot);

        Require(result.Ok && reloader.ReloadCalls == 1, $"the ticked repairs apply with one reload ({result.Message})");
        var meta = JObject.Parse(mod.ReadMeta());
        var files = (JObject)meta["DefaultData"]!["Files"]!;
        Require("bcd".All(letter => files[$"{SbTextures}{letter}_normal.tex"] is not null && files[$"{SbTextures}{letter}_norm.tex"] is null) &&
                Enumerable.Range(2, 3).All(set => files[$"chara/equipment/e0128/material/v{set:D4}/mt_c0201e0128_top_a.mtrl"] is not null) &&
                files[SbHairKey] is null && files[$"{SbMaterials}e.mtrl"] is null &&
                ((JArray)meta["DefaultData"]!["Manipulations"]!).Count == 0 &&
                meta.Value<string>("LastWrite") != "2026-09-26T17:47:46.0106303Z",
            "normal maps get their real names, every material set its materials, and the hair leftovers go");
        Require(SbHairFiles.All(file => !mod.Exists(file)) && !mod.Exists($"{SbTextures}e_normal.tex") && !mod.Exists($"{SbMaterials}e.mtrl") &&
                mod.Exists($"{SbTextures}b_normal.tex"),
            "files nothing uses any more are deleted, files still used stay");

        var after = CheckStellarBlade(mod);
        Require(after.Count(Severity.Error) == 0 && !after.Findings.Any(f => f.Category == CheckCategory.Leftovers),
            "checked again, the repaired mod has no errors and no leftovers");
    }

    private static void RejectedReloadRestoresTheMod(string testRoot)
    {
        using var mod = StellarBladeMod();
        var before = mod.ReadMeta();
        var result = ApplyRecommended(mod, CheckStellarBlade(mod), new FakeReloader { ReloadSucceeds = false }, testRoot);

        Require(!result.Ok && mod.ReadMeta() == before && SbHairFiles.All(mod.Exists),
            "when Penumbra won't take the repaired mod, meta.json is put back and nothing is deleted");
    }

    private static void ChangedModIsNotRepaired(string testRoot)
    {
        using var mod = StellarBladeMod();
        var report = CheckStellarBlade(mod);
        var changed = mod.ReadMeta().Replace("\"Author\": \"Luci\"", "\"Author\": \"Someone else\"");
        mod.Meta(changed);

        var result = ApplyRecommended(mod, report, new FakeReloader(), testRoot);
        Require(!result.Ok && result.Message == "The mod changed since it was checked. Check it again." && mod.ReadMeta() == changed,
            "a mod that changed after its check is not repaired");
    }

    private static void EmptySelectionChangesNothing(string testRoot)
    {
        using var mod = StellarBladeMod();
        var before = mod.ReadMeta();
        var report = CheckStellarBlade(mod);
        var selection = new RepairSelection(report);
        foreach (var repair in report.Repairs)
            selection.Set(repair.Id, false);
        var reloader = new FakeReloader();
        var (store, cache) = NewBackups(testRoot);

        var result = RepairApplier.ApplyAsync(ModProject.Load(mod.Path), report, selection, store, cache, reloader, new StudioSettings(),
            CancellationToken.None).GetAwaiter().GetResult();
        Require(result.Ok && reloader.ReloadCalls == 0 && mod.ReadMeta() == before && store.For(mod.Path).Count == 0,
            "with nothing ticked, nothing is written, backed up or reloaded");
    }

    private static void DependentRepairsAreDroppedWithTheirPrerequisite()
    {
        using var mod = StellarBladeMod();
        var report = CheckStellarBlade(mod);
        var removal = report.Repairs.First(r => r.DependsOn.Count > 0);
        var selection = new RepairSelection(report);
        foreach (var repair in report.Repairs)
            selection.Set(repair.Id, false);

        selection.Set(removal.Id, true);
        Require(!selection.Effective().Any(r => r.Id == removal.Id), "a repair whose prerequisite isn't ticked is left out");
        selection.Set(removal.DependsOn[0], true);
        Require(selection.Effective().Any(r => r.Id == removal.Id) && selection.Count == 2, "with its prerequisite it is applied");
    }

    private static void AddedEqdpEntryLoadsTheRaceModel(string testRoot)
    {
        using var mod = GearMod();
        var meta = JObject.Parse(mod.ReadMeta());
        meta["DefaultData"]!["Files"]!["chara/equipment/e0005/model/c1401e0005_top.mdl"] = "files/model.mdl";
        mod.Meta(meta.ToString());
        var game = GearGame();
        var report = Check(mod, game);
        var fix = report.Find(report.Findings.Single(f => f.Code == "R001").RepairIds.Single())!;
        var selection = new RepairSelection(report);
        foreach (var repair in report.Repairs)
            selection.Set(repair.Id, repair.Id == fix.Id);
        var (store, cache) = NewBackups(testRoot);

        var result = RepairApplier.ApplyAsync(ModProject.Load(mod.Path), report, selection, store, cache, new FakeReloader(), new StudioSettings(),
            CancellationToken.None).GetAwaiter().GetResult();
        var added = ((JArray)JObject.Parse(mod.ReadMeta())["DefaultData"]!["Manipulations"]!).Single();
        Require(result.Ok && JToken.DeepEquals(added["Manipulation"],
                    new JObject { ["Gender"] = "Female", ["Race"] = "AuRa", ["SetId"] = 5, ["Slot"] = "Body", ["Entry"] = 8 }) &&
                !Check(mod, game).Findings.Any(f => f.Code == "R001"),
            "the EQDP entry is added to the default files, and the race model is loaded after");
    }

    private static void RepairsStopWhenTheBackupCantBeStored(string testRoot)
    {
        using var mod = StellarBladeMod();
        var before = mod.ReadMeta();
        var report = CheckStellarBlade(mod);
        var (store, _) = NewBackups(testRoot);
        var blocked = Path.Combine(testRoot, $"cache-is-a-file-{Guid.NewGuid():N}");
        File.WriteAllText(blocked, "not a folder");

        var result = RepairApplier.ApplyAsync(ModProject.Load(mod.Path), report, new RepairSelection(report), store, blocked, new FakeReloader(),
            new StudioSettings(), CancellationToken.None).GetAwaiter().GetResult();
        Require(!result.Ok && mod.ReadMeta() == before && SbHairFiles.All(mod.Exists),
            "repairs whose backup can't be stored change nothing");
    }

    private static void RepairsSayWhyTheyCantRun(string testRoot)
    {
        using var mod = StellarBladeMod();
        var report = CheckStellarBlade(mod);
        var project = ModProject.Load(mod.Path);
        Require(RepairApplier.WhyNot(null, report, false, true, stale: false) == "The mod is closed." &&
                RepairApplier.WhyNot(project, report, true, true, stale: false) == "Wait a moment, until every change is written into the mod." &&
                RepairApplier.WhyNot(project, report, false, false, stale: false) == "Penumbra is not available." &&
                RepairApplier.WhyNot(project, report, false, true, stale: false) is null,
            "repairs say why they can't run: closed, still writing, no Penumbra");
    }

    private static void ACancelBeforeApplyingChangesNothing(string testRoot)
    {
        using var mod = StellarBladeMod();
        var before = mod.ReadMeta();
        var report = CheckStellarBlade(mod);
        var (store, cache) = NewBackups(testRoot);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        ApplyResult? result = null;
        try
        {
            result = RepairApplier.ApplyAsync(ModProject.Load(mod.Path), report, new RepairSelection(report), store, cache, new FakeReloader(),
                new StudioSettings(), cancelled.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) { }
        Require(mod.ReadMeta() == before && SbHairFiles.All(mod.Exists) && store.For(mod.Path).Count == 0 && result is not { Ok: true },
            "a cancel before the repairs start changes nothing");
    }

    private static void AFailureAfterMetaChangedStillLeavesABackup(string testRoot)
    {
        using var mod = StellarBladeMod();
        var before = mod.ReadMeta();
        var report = CheckStellarBlade(mod);
        var (store, cache) = NewBackups(testRoot);
        try
        {
            RepairApplier.ApplyAsync(ModProject.Load(mod.Path), report, new RepairSelection(report) { DeleteReleasedFiles = true }, store, cache,
                new FakeReloader { Throws = new InvalidOperationException("the plugin went away") }, new StudioSettings(), CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        catch (InvalidOperationException) { }
        Require(mod.ReadMeta() != before && store.For(mod.Path).Count == 1, "meta.json changed before the failure, so the change is in the backups");
        store.Restore(store.For(mod.Path).Single().Id, mod.Path, cache, new FakeReloader());
        Require(mod.ReadMeta() == before, "and Restore puts it back");
    }

    private static void ABackupFolderThatCantBeUsedSaysWhy()
    {
        var root = BackupFolder.TryResolve(() => throw new IOException("the drive is gone"), out var problem);
        Require(root is null && problem == "Nothing was changed: the backup folder couldn't be used (the drive is gone).",
            "a cache folder that can't be used stops the operation with the reason");
        Require(BackupFolder.TryResolve(() => @"C:\cache", out var none) == @"C:\cache" && none.Length == 0, "a usable one is passed on");
    }

    private static void WhyNotTrustsTheSessionsStaleness(string testRoot)
    {
        using var mod = StellarBladeMod();
        var report = CheckStellarBlade(mod);
        var project = ModProject.Load(mod.Path);
        mod.Meta(mod.ReadMeta().Replace("\"Author\": \"Luci\"", "\"Author\": \"Someone else\""));
        Require(RepairApplier.WhyNot(project, report, false, true, stale: false) is null,
            "the Apply button doesn't read meta.json itself; the session's watch says whether the report is stale");
        Require(RepairApplier.WhyNot(project, report, false, true, stale: true) == RepairApplier.ChangedReason, "a stale report can't be applied");
    }

    private static void FilesChangedAfterTheCheckAreNeverDeleted(string testRoot)
    {
        using var mod = StellarBladeMod();
        var report = CheckStellarBlade(mod);
        var files = Directory.EnumerateFiles(mod.Path, "*", SearchOption.AllDirectories).Where(f => Path.GetFileName(f) != "meta.json").ToList();
        var later = DateTime.UtcNow.AddMinutes(5);
        foreach (var file in files)
            File.SetLastWriteTimeUtc(file, later);

        var result = ApplyRecommended(mod, report, new FakeReloader(), testRoot, deleteReleased: true);
        Require(result.Ok && files.All(File.Exists), $"files changed after the check are never deleted ({result.Message})");
    }
}
