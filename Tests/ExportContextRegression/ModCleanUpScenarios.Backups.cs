using InstantEdit.Studio;
using InstantEdit.Studio.Models.Project;
using InstantEdit.Studio.Services.Backups;
using InstantEdit.Studio.Services.CleanUp;
using InstantEdit.Studio.Services.Project;
using Newtonsoft.Json.Linq;
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
        SplitLayoutModsRestoreWhole(testRoot);
        SplitLayoutRollbackPutsTheFilesBack(testRoot);
        BackupsFollowAModRenamedInPenumbra(testRoot);
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

    /// <summary>A mod in Penumbra's old layout: meta.json for its name, default_mod.json for its files.</summary>
    private static TestModFolder SplitLayoutMod()
    {
        var mod = new TestModFolder();
        mod.File("files/a.bin", "same"u8.ToArray());
        mod.File("files/b.bin", "same"u8.ToArray());
        mod.Meta(new JObject { ["FileVersion"] = 3, ["Name"] = "Old layout", ["Author"] = "Someone" }.ToString());
        mod.File(LegacyMetaReader.DefaultModFile, System.Text.Encoding.UTF8.GetBytes(new JObject
        {
            ["Name"] = "", ["Priority"] = 0,
            ["Files"] = new JObject { ["chara/a.bin"] = "files/a.bin", ["chara/b.bin"] = "files/b.bin" },
            ["FileSwaps"] = new JObject(), ["Manipulations"] = new JArray(),
        }.ToString()));
        return mod;
    }

    private static string Text(TestModFolder mod, string relPath) => File.ReadAllText(Path.Combine(mod.Path, relPath));

    private static void SplitLayoutModsRestoreWhole(string testRoot)
    {
        using var mod = SplitLayoutMod();
        var meta = mod.ReadMeta();
        var defaults = Text(mod, LegacyMetaReader.DefaultModFile);
        var (store, cache) = TextureBackups(testRoot);
        var sets = DuplicateFiles.Scan(PenumbraModMeta.Load(mod.Path), mod.Path, new HashSet<string>(), CancellationToken.None);
        var result = DuplicateFiles.ApplyAsync(ModProject.Load(mod.Path), sets, store, cache, new FakeReloader(), new StudioSettings(), CancellationToken.None)
            .GetAwaiter().GetResult();
        Require(result.Changed == 1 && !mod.Exists(LegacyMetaReader.DefaultModFile), "the old layout is merged into meta.json when it is written");

        var restored = store.Restore(store.For(mod.Path).Single().Id, mod.Path, cache, new FakeReloader());
        Require(restored.Skipped.Count == 0 && mod.ReadMeta() == meta && mod.Exists(LegacyMetaReader.DefaultModFile)
                && Text(mod, LegacyMetaReader.DefaultModFile) == defaults && !mod.Exists(LegacyMetaReader.DefaultModFile + PenumbraModMeta.BackupSuffix)
                && mod.Exists("files/b.bin"),
            "restoring puts the old layout back as it was, without the set-aside copy");
    }

    private static void SplitLayoutRollbackPutsTheFilesBack(string testRoot)
    {
        using var mod = SplitLayoutMod();
        var meta = mod.ReadMeta();
        var defaults = Text(mod, LegacyMetaReader.DefaultModFile);
        var (store, cache) = TextureBackups(testRoot);
        var sets = DuplicateFiles.Scan(PenumbraModMeta.Load(mod.Path), mod.Path, new HashSet<string>(), CancellationToken.None);
        DuplicateFiles.ApplyAsync(ModProject.Load(mod.Path), sets, store, cache, new FakeReloader { ReloadSucceeds = false }, new StudioSettings(), CancellationToken.None)
            .GetAwaiter().GetResult();
        Require(mod.ReadMeta() == meta && mod.Exists(LegacyMetaReader.DefaultModFile) && Text(mod, LegacyMetaReader.DefaultModFile) == defaults
                && !mod.Exists(LegacyMetaReader.DefaultModFile + PenumbraModMeta.BackupSuffix),
            "a rejected reload puts the old layout back too");
    }

    private static void BackupsFollowAModRenamedInPenumbra(string testRoot)
    {
        var id = Guid.NewGuid();
        var mod = new TestModFolder();
        mod.File("files/a.bin", "same"u8.ToArray()).File("files/b.bin", "same"u8.ToArray());
        mod.Meta(new JObject
        {
            ["FileVersion"] = 4, ["Identifier"] = id.ToString(), ["Name"] = "Renamed",
            ["DefaultData"] = new JObject { ["Files"] = new JObject { ["chara/a.bin"] = "files/a.bin", ["chara/b.bin"] = "files/b.bin" } },
        }.ToString());
        var before = mod.ReadMeta();
        var (store, cache) = TextureBackups(testRoot);
        var sets = DuplicateFiles.Scan(PenumbraModMeta.Load(mod.Path), mod.Path, new HashSet<string>(), CancellationToken.None);
        DuplicateFiles.ApplyAsync(ModProject.Load(mod.Path), sets, store, cache, new FakeReloader(), new StudioSettings(), CancellationToken.None).GetAwaiter().GetResult();

        var renamed = mod.Path + " renamed";
        Directory.Move(mod.Path, renamed);
        try
        {
            var entries = store.For(renamed, id);
            Require(entries.Count == 1, "a mod renamed in Penumbra keeps its backups, found by its id");
            var restored = store.Restore(entries[0].Id, renamed, cache, new FakeReloader());
            Require(restored.Skipped.Count == 0 && File.ReadAllText(Path.Combine(renamed, "meta.json")) == before && File.Exists(Path.Combine(renamed, "files", "b.bin")),
                "and they restore into its new folder");
        }
        finally
        {
            Directory.Delete(renamed, recursive: true);
        }
    }
}
