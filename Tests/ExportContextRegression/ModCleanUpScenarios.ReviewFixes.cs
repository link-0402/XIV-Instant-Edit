using InstantEdit.Studio;
using InstantEdit.Studio.Models.Project;
using InstantEdit.Studio.Services.CleanUp;
using InstantEdit.Studio.Services.Project;
using Newtonsoft.Json.Linq;
using static InstantEdit.TestSupport.Assertions;

/// <summary>Clean-up after the whole-branch review: pending files checked before deleting, top-level moves, stale plans, failures after meta.json changed.</summary>
internal static partial class ModCleanUpScenarios
{
    public static void RunReviewFixes(string testRoot)
    {
        PendingCleanupKeepsAFileMetaJsonUsesAgain(testRoot);
        PendingCleanupKeepsAFileThatChanged(testRoot);
        RestoreAfterAPendingDeleteBringsTheFileBack(testRoot);
        ATopLevelFileIsRemovedAfterItsMove(testRoot);
        AFileUsedAgainSinceThePlanIsKept(testRoot);
        AFailedReloadAfterMovingStillLeavesABackup(testRoot);
        AFailedReloadAfterDeduplicatingStillLeavesABackup(testRoot);
    }

    private static void PendingCleanupKeepsAFileMetaJsonUsesAgain(string testRoot)
    {
        using var mod = SimpleMod("old/a.bin", "payload");
        var settings = new StudioSettings();
        PendingCleanup.Add(settings, mod.Path, "old/a.bin", "test");
        var cleaned = PendingCleanup.RetryAsync(settings, _ => false, CancellationToken.None).GetAwaiter().GetResult();
        Require(cleaned == 0 && mod.Exists("old/a.bin") && settings.PendingCleanup.Count == 0,
            "a pending file meta.json points at again is kept, and dropped from the list");
    }

    private static void PendingCleanupKeepsAFileThatChanged(string testRoot)
    {
        using var mod = SimpleMod("chara/a.bin", "payload");
        mod.File("old/stale.bin", "before"u8.ToArray());
        var settings = new StudioSettings();
        PendingCleanup.Add(settings, mod.Path, "old/stale.bin", "test");
        mod.File("old/stale.bin", "a new file from a reinstall"u8.ToArray());
        PendingCleanup.RetryAsync(settings, _ => false, CancellationToken.None).GetAwaiter().GetResult();
        Require(mod.Exists("old/stale.bin") && settings.PendingCleanup.Count == 0, "a pending file that changed since is kept, and dropped from the list");
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

    private static void ATopLevelFileIsRemovedAfterItsMove(string testRoot)
    {
        using var mod = SimpleMod("a.bin", "payload");
        var result = Tidy(mod, testRoot);
        Require(result.Changed == 1 && mod.Exists("chara/a.bin") && !mod.Exists("a.bin"), "a moved file at the mod's top level is removed too");
    }

    private static void AFileUsedAgainSinceThePlanIsKept(string testRoot)
    {
        using var mod = SimpleMod("old/a.bin", "payload");
        mod.File("old/unused.bin", "spare"u8.ToArray());
        var (store, cache) = TextureBackups(testRoot);
        var plan = FileLocations.Plan(PenumbraModMeta.Load(mod.Path), mod.Path, new HashSet<string>());
        Require(plan.Obsolete.Contains("old/unused.bin"), "the spare file is listed as unused");

        var meta = JObject.Parse(mod.ReadMeta());
        meta["DefaultData"]!["Files"]!["chara/spare.bin"] = "old/unused.bin";
        mod.Meta(meta.ToString());
        var result = FileLocations.ApplyAsync(ModProject.Load(mod.Path), plan, store, cache, new FakeReloader(), new StudioSettings(), CancellationToken.None)
            .GetAwaiter().GetResult();
        Require(result.Changed == 1 && mod.Exists("old/unused.bin") && mod.Exists("chara/a.bin"),
            "a file an option uses again since the list was made is kept");
    }

    private static void AFailedReloadAfterMovingStillLeavesABackup(string testRoot)
    {
        using var mod = SimpleMod("old/a.bin", "payload");
        var (store, cache) = TextureBackups(testRoot);
        var plan = FileLocations.Plan(PenumbraModMeta.Load(mod.Path), mod.Path, new HashSet<string>());
        try
        {
            FileLocations.ApplyAsync(ModProject.Load(mod.Path), plan, store, cache, new FakeReloader { Throws = new InvalidOperationException("gone") },
                new StudioSettings(), CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (InvalidOperationException) { }
        Require(store.For(mod.Path).Count == 1, "moved files whose reload failed are still in the backups");
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
