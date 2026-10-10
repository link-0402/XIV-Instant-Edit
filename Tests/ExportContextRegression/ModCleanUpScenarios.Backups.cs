using InstantEdit.Studio.Services.Backups;
using static InstantEdit.TestSupport.Assertions;

/// <summary>Backups of the files an operation changes: kept by content, restored only where nothing changed since, forgotten after a week.</summary>
internal static partial class ModCleanUpScenarios
{
    public static void RunBackups(string testRoot)
    {
        ModPathsStayInsideTheMod(testRoot);
        RestorePutsBackOnlyUnchangedFiles(testRoot);
        BackupsExpireAfterAWeek(testRoot);
        AnUnreadableJournalIsSetAside(testRoot);
    }

    private static void ModPathsStayInsideTheMod(string testRoot)
    {
        using var mod = new TestModFolder().File("a/b.tex", ModCheckFixtures.Tex());
        Require(ModPaths.Resolve(mod.Path, "a/b.tex") == Path.Combine(mod.Path, "a", "b.tex"), "a path inside the mod resolves");
        Reject(() => ModPaths.Resolve(mod.Path, "../escape.bin"), "a path out of the mod folder is refused");
        Reject(() => ModPaths.Resolve(mod.Path, @"C:\Windows\win.ini"), "a rooted path is refused");
    }

    private static (ModBackupStore Store, string Config, string Cache) Store(string testRoot)
    {
        var config = Path.Combine(testRoot, "BackupConfig", Guid.NewGuid().ToString("N"));
        var cache = Path.Combine(testRoot, "BackupCache", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(config);
        Directory.CreateDirectory(cache);
        var store = new ModBackupStore(config);
        store.Load();
        return (store, config, cache);
    }

    private static void RestorePutsBackOnlyUnchangedFiles(string testRoot)
    {
        using var mod = new TestModFolder().File("x/one.tex", [1, 1, 1]).File("x/two.tex", [2, 2, 2]).File("x/gone.tex", [3, 3, 3]);
        var (store, _, cache) = Store(testRoot);

        using (var operation = store.Begin(mod.Path, Guid.NewGuid(), "Compressed 3 textures", cache))
        {
            operation.Keep("x/one.tex");
            operation.Keep("x/two.tex");
            operation.Keep("x/gone.tex");
            mod.File("x/one.tex", [9]).File("x/two.tex", [8]);
            File.Delete(Path.Combine(mod.Path, "x", "gone.tex"));
            operation.Commit();
        }
        var entry = store.For(mod.Path).Single();
        Require(entry.Label == "Compressed 3 textures" && entry.Files.Count == 3 &&
                Directory.EnumerateFiles(Path.Combine(cache, ModBackupStore.FolderName)).Count() == 3,
            "an operation's journal entry lists its files, and their old contents are kept in the cache");

        mod.File("x/two.tex", [7]);   // changed again after the operation
        var restored = store.Restore(entry.Id, mod.Path, cache, new FakeReloader());
        Require(restored.Restored == 2 && restored.Skipped.SequenceEqual(["x/two.tex"]) &&
                File.ReadAllBytes(Path.Combine(mod.Path, "x", "one.tex")).SequenceEqual(new byte[] { 1, 1, 1 }) &&
                File.ReadAllBytes(Path.Combine(mod.Path, "x", "gone.tex")).SequenceEqual(new byte[] { 3, 3, 3 }) &&
                File.ReadAllBytes(Path.Combine(mod.Path, "x", "two.tex")).SequenceEqual(new byte[] { 7 }),
            "restore puts back what the operation changed or deleted, and skips a file changed since, naming it");
    }

    private static void BackupsExpireAfterAWeek(string testRoot)
    {
        using var mod = new TestModFolder().File("x/one.tex", [1]);
        var (store, config, cache) = Store(testRoot);
        using (var operation = store.Begin(mod.Path, Guid.NewGuid(), "Applied 1 fix", cache))
        {
            operation.Keep("x/one.tex");
            operation.Commit();
        }

        store.Expire(DateTime.UtcNow + ModBackupStore.Retention + TimeSpan.FromHours(1), cache);
        var reloaded = new ModBackupStore(config);
        reloaded.Load();
        Require(store.For(mod.Path).Count == 0 && reloaded.For(mod.Path).Count == 0,
            "backups older than a week are forgotten, also after the journal is read again");
    }

    private static void AnUnreadableJournalIsSetAside(string testRoot)
    {
        var (_, config, cache) = Store(testRoot);
        File.WriteAllText(Path.Combine(config, ModBackupStore.JournalName), "{ not json");
        Directory.CreateDirectory(Path.Combine(cache, ModBackupStore.FolderName));
        var copy = Path.Combine(cache, ModBackupStore.FolderName, new string('A', 64));
        File.WriteAllText(copy, "original");
        File.SetLastWriteTimeUtc(copy, DateTime.UtcNow - TimeSpan.FromDays(30));

        var store = new ModBackupStore(config);
        store.Load();
        store.Expire(DateTime.UtcNow, cache);
        Require(store.LoadError.Length > 0 && File.Exists(copy) &&
                Directory.EnumerateFiles(config, ModBackupStore.JournalName + ".unreadable-*").Any(),
            "an unreadable journal is kept aside, and no backup is swept while it can't be read");
    }
}
