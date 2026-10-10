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
        Require(RepairApplier.WhyNot(null, report, false, true) == "The mod is closed." &&
                RepairApplier.WhyNot(project, report, true, true) == "Wait a moment, until every change is written into the mod." &&
                RepairApplier.WhyNot(project, report, false, false) == "Penumbra is not available." &&
                RepairApplier.WhyNot(project, report, false, true) is null,
            "repairs say why they can't run: closed, still writing, no Penumbra");
    }
}
