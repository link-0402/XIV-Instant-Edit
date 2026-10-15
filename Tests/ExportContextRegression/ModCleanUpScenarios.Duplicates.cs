using InstantEdit.Studio;
using InstantEdit.Studio.Models.Project;
using InstantEdit.Studio.Services.CleanUp;
using InstantEdit.Studio.Services.Project;
using Newtonsoft.Json.Linq;
using static InstantEdit.TestSupport.Assertions;

/// <summary>Files a mod carries twice: found by content, mapped to one copy, the others deleted. Ported from Mod Optimizer's duplicate tests.</summary>
internal static partial class ModCleanUpScenarios
{
    public static void RunDuplicates(string testRoot)
    {
        DuplicateScanSkipsUiAndRemovalRewritesMappings(testRoot);
        DuplicateRemovalStopsIfAFileBecomesUiReferencedAfterTheScan(testRoot);
        ALedgerFileIsAlwaysTheCopyKept(testRoot);
        RestoringAfterRemovalBringsTheCopiesBack(testRoot);
        RestoreAfterAPendingDeleteBringsTheFileBack(testRoot);
        AFailedReloadAfterDeduplicatingStillLeavesABackup(testRoot);
    }

    private static TestModFolder DuplicateMod(JObject files, params (string Rel, string Text)[] contents)
    {
        var mod = new TestModFolder();
        foreach (var (rel, text) in contents)
            mod.File(rel, System.Text.Encoding.UTF8.GetBytes(text));
        mod.Meta(new JObject { ["FileVersion"] = 4, ["Name"] = "Duplicates", ["DefaultData"] = new JObject { ["Files"] = files } }.ToString());
        return mod;
    }

    private static CleanUpResult Deduplicate(TestModFolder mod, IReadOnlyList<DuplicateSet> sets, string testRoot,
        InstantEdit.Studio.Services.Backups.ModBackupStore? store = null, string? cache = null)
    {
        if (store is null)
            (store, cache) = TextureBackups(testRoot);
        return DuplicateFiles.ApplyAsync(ModProject.Load(mod.Path), sets, store, cache!, new FakeReloader(), new StudioSettings(), CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    private static void DuplicateScanSkipsUiAndRemovalRewritesMappings(string testRoot)
    {
        using var mod = DuplicateMod(new JObject
        {
            ["chara/a.bin"] = "files/a.bin", ["chara/b.bin"] = @"files\bb.bin", ["ui/a.tex"] = "files/ui-a.bin", ["ui/b.tex"] = "files/ui-b.bin",
        }, ("files/a.bin", "same"), ("files/bb.bin", "same"), ("files/ui-a.bin", "ui-same"), ("files/ui-b.bin", "ui-same"));

        var sets = DuplicateFiles.Scan(PenumbraModMeta.Load(mod.Path), mod.Path, new HashSet<string>(), CancellationToken.None);
        var set = sets.Single();
        Require(set.Keeper == "files/a.bin" && set.Others.SequenceEqual(["files/bb.bin"]) && set.BytesFreed == 4,
            "two equal files are one set, the shorter path kept; files a ui/ path uses are left out");

        var result = Deduplicate(mod, sets, testRoot);
        var files = (JObject)JObject.Parse(mod.ReadMeta())["DefaultData"]!["Files"]!;
        Require(result.Changed == 1 && !mod.Exists("files/bb.bin") && files["chara/b.bin"]!.Value<string>() == @"files\a.bin",
            "the duplicate's mapping points at the kept copy, in the mapping's own path style, and the duplicate is deleted");
    }

    private static void DuplicateRemovalStopsIfAFileBecomesUiReferencedAfterTheScan(string testRoot)
    {
        using var mod = DuplicateMod(new JObject { ["chara/a.bin"] = "files/a.bin", ["chara/b.bin"] = "files/b.bin" }, ("files/a.bin", "same"), ("files/b.bin", "same"));
        var sets = DuplicateFiles.Scan(PenumbraModMeta.Load(mod.Path), mod.Path, new HashSet<string>(), CancellationToken.None);
        var changed = JObject.Parse(mod.ReadMeta());
        ((JObject)changed["DefaultData"]!["Files"]!)["ui/new.tex"] = "files/b.bin";
        mod.Meta(changed.ToString());

        var result = Deduplicate(mod, sets, testRoot);
        Require(result.Changed == 0 && result.Problems.Count == 1 && mod.Exists("files/a.bin") && mod.Exists("files/b.bin"),
            "duplicates that changed since the scan are left alone");
    }

    private static void ALedgerFileIsAlwaysTheCopyKept(string testRoot)
    {
        using var mod = DuplicateMod(new JObject { ["chara/a.bin"] = "a.bin", ["chara/b.bin"] = "instant_edit/longer/b.bin" },
            ("a.bin", "same"), ("instant_edit/longer/b.bin", "same"));
        var set = DuplicateFiles.Scan(PenumbraModMeta.Load(mod.Path), mod.Path, new HashSet<string> { "instant_edit/longer/b.bin" }, CancellationToken.None).Single();
        Require(set.Keeper == "instant_edit/longer/b.bin" && set.Others.SequenceEqual(["a.bin"]),
            "a file Instant Edit wrote is the copy kept, however long its path");
    }

    private static void RestoringAfterRemovalBringsTheCopiesBack(string testRoot)
    {
        using var mod = DuplicateMod(new JObject { ["chara/a.bin"] = "files/a.bin", ["chara/b.bin"] = "files/b.bin" }, ("files/a.bin", "same"), ("files/b.bin", "same"));
        var before = mod.ReadMeta();
        var (store, cache) = TextureBackups(testRoot);
        Deduplicate(mod, DuplicateFiles.Scan(PenumbraModMeta.Load(mod.Path), mod.Path, new HashSet<string>(), CancellationToken.None), testRoot, store, cache);

        var restored = store.Restore(store.For(mod.Path).Single().Id, mod.Path, cache, new FakeReloader());
        Require(restored.Skipped.Count == 0 && mod.ReadMeta() == before && mod.Exists("files/b.bin"),
            "restoring brings the removed copy and the old mapping back");
    }

    private static void RestoreAfterAPendingDeleteBringsTheFileBack(string testRoot)
    {
        using var mod = DuplicateMod(new JObject { ["chara/a.bin"] = "files/a.bin", ["chara/b.bin"] = "files/b.bin" }, ("files/a.bin", "same"), ("files/b.bin", "same"));
        var before = mod.ReadMeta();
        var settings = new StudioSettings();
        var (store, cache) = TextureBackups(testRoot);
        var sets = DuplicateFiles.Scan(PenumbraModMeta.Load(mod.Path), mod.Path, new HashSet<string>(), CancellationToken.None);
        CleanUpResult result;
        using (new FileStream(Path.Combine(mod.Path, "files", "b.bin"), FileMode.Open, FileAccess.Read, FileShare.Read))
            result = DuplicateFiles.ApplyAsync(ModProject.Load(mod.Path), sets, store, cache, new FakeReloader(), settings, CancellationToken.None).GetAwaiter().GetResult();
        Require(result.Pending == 1, "the held copy waits");
        PendingCleanup.RetryAsync(settings, _ => false, CancellationToken.None).GetAwaiter().GetResult();
        Require(!mod.Exists("files/b.bin"), "and goes once it is free");

        var restored = store.Restore(store.For(mod.Path).Single().Id, mod.Path, cache, new FakeReloader());
        Require(restored.Skipped.Count == 0 && mod.Exists("files/b.bin") && mod.ReadMeta() == before,
            "restoring brings back the copy pending clean-up deleted, with its mapping");
    }

    private static void AFailedReloadAfterDeduplicatingStillLeavesABackup(string testRoot)
    {
        using var mod = DuplicateMod(new JObject { ["chara/a.bin"] = "files/a.bin", ["chara/b.bin"] = "files/b.bin" }, ("files/a.bin", "same"), ("files/b.bin", "same"));
        var (store, cache) = TextureBackups(testRoot);
        var sets = DuplicateFiles.Scan(PenumbraModMeta.Load(mod.Path), mod.Path, new HashSet<string>(), CancellationToken.None);
        try
        {
            DuplicateFiles.ApplyAsync(ModProject.Load(mod.Path), sets, store, cache, new FakeReloader { Throws = new InvalidOperationException("gone") },
                new StudioSettings(), CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (InvalidOperationException) { }
        Require(store.For(mod.Path).Count == 1, "deduplicated mappings whose reload failed are still in the backups");
    }
}
