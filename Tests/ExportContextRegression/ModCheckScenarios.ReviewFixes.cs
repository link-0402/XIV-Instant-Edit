using InstantEdit.Studio;
using InstantEdit.Studio.Models.Project;
using InstantEdit.Studio.Services.Backups;
using InstantEdit.Studio.Services.Check;
using static InstantEdit.TestSupport.Assertions;

/// <summary>Repairs and the session after the whole-branch review: cancelling, failures after meta.json changed, the backup folder, staleness.</summary>
internal static partial class ModCheckScenarios
{
    public static void RunReviewFixes(string testRoot)
    {
        ACancelBeforeApplyingChangesNothing(testRoot);
        AFailureAfterMetaChangedStillLeavesABackup(testRoot);
        ABackupFolderThatCantBeUsedSaysWhy();
        WhyNotTrustsTheSessionsStaleness(testRoot);
        OperationsReportProgressAndCanBeCancelled();
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

    private static void OperationsReportProgressAndCanBeCancelled()
    {
        var session = new CheckSession();
        var progress = session.OperationProgress;
        session.StartOperation("Changing textures", async token =>
        {
            progress.Report(new CheckProgress("Encoding", 1, 3));
            await Task.Delay(Timeout.Infinite, token);
            return (true, "done");
        }, checkAfter: false);

        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (session.Progress is null && waited.Elapsed < TimeSpan.FromSeconds(10))
            Thread.Sleep(10);
        Require(session.Progress is { Phase: "Encoding", Current: 1, Total: 3 }, "an operation's progress shows");

        session.Cancel();
        while (session.Busy && waited.Elapsed < TimeSpan.FromSeconds(20))
        {
            Thread.Sleep(10);
            session.Update();
        }
        Require(!session.Busy && session.LastOperation is (false, "Cancelled.") && session.Progress is null, "Cancel stops it");
    }
}
