using InstantEdit.Studio;
using InstantEdit.Studio.Models.Project;
using InstantEdit.Studio.Services.Backups;
using InstantEdit.Studio.Services.CleanUp;
using InstantEdit.Studio.Services.Project;
using Newtonsoft.Json.Linq;
using static InstantEdit.TestSupport.Assertions;

/// <summary>Moving a mod's files to Penumbra-style locations. Ported from Mod Optimizer's normalization tests.</summary>
internal static partial class ModCleanUpScenarios
{
    public static void RunLocations(string testRoot)
    {
        ALockedOldFileBecomesPendingCleanup(testRoot);
        AFailedReloadPutsTheMappingsBack(testRoot);
        GroupFoldersGetStableUniqueSafeNames(testRoot);
        ImagesAndThisPluginsFilesStayWhereTheyAre(testRoot);
        ATargetWithOtherContentStopsTheMove(testRoot);
        PendingCleanupKeepsAFileMetaJsonUsesAgain(testRoot);
        PendingCleanupKeepsAFileThatChanged(testRoot);
        ATopLevelFileIsRemovedAfterItsMove(testRoot);
        AFileUsedAgainSinceThePlanIsKept(testRoot);
        AFailedReloadAfterMovingStillLeavesABackup(testRoot);
        AFailedCopyLeavesNoTempFile(testRoot);
    }

    private static TestModFolder SimpleMod(string relPath, string contents)
    {
        var mod = new TestModFolder().File(relPath, System.Text.Encoding.UTF8.GetBytes(contents));
        mod.Meta(new JObject
        {
            ["FileVersion"] = 4,
            ["Name"] = "Simple",
            ["DefaultData"] = new JObject { ["Files"] = new JObject { ["chara/a.bin"] = relPath } },
        }.ToString());
        return mod;
    }

    private static CleanUpResult Tidy(TestModFolder mod, string testRoot, FakeReloader? reloader = null, StudioSettings? settings = null,
        IReadOnlySet<string>? ledger = null)
    {
        var (store, cache) = TextureBackups(testRoot);
        var plan = FileLocations.Plan(PenumbraModMeta.Load(mod.Path), mod.Path, ledger ?? new HashSet<string>());
        return FileLocations.ApplyAsync(ModProject.Load(mod.Path), plan, store, cache, reloader ?? new FakeReloader(), settings ?? new StudioSettings(),
            CancellationToken.None).GetAwaiter().GetResult();
    }

    private static void ALockedOldFileBecomesPendingCleanup(string testRoot)
    {
        using var mod = SimpleMod("old/a.bin", "payload");
        var settings = new StudioSettings();
        var old = Path.Combine(mod.Path, "old", "a.bin");
        using (new FileStream(old, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = Tidy(mod, testRoot, settings: settings);
            var files = (JObject)JObject.Parse(mod.ReadMeta())["DefaultData"]!["Files"]!;
            Require(result.Pending == 1 && settings.PendingCleanup.Count == 1 && File.Exists(old) && mod.Exists("chara/a.bin") &&
                    files["chara/a.bin"]!.Value<string>() == "chara/a.bin",
                "the mapping moves to the game path while the held old file waits for clean-up");
        }

        var cleaned = PendingCleanup.RetryAsync(settings, _ => false, CancellationToken.None).GetAwaiter().GetResult();
        Require(cleaned == 1 && settings.PendingCleanup.Count == 0 && !File.Exists(old), "the old file goes once nothing holds it");
    }

    private static void AFailedReloadPutsTheMappingsBack(string testRoot)
    {
        using var mod = SimpleMod("old/a.bin", "payload");
        var reloader = new FakeReloader { ReloadSucceeds = false };
        var result = Tidy(mod, testRoot, reloader);
        var files = (JObject)JObject.Parse(mod.ReadMeta())["DefaultData"]!["Files"]!;
        Require(result.Problems.Count == 1 && files["chara/a.bin"]!.Value<string>() == "old/a.bin" && !mod.Exists("chara/a.bin") &&
                mod.Exists("old/a.bin") && reloader.ReloadCalls >= 2,
            "when Penumbra won't take the moved mappings, meta.json and the files go back as they were");
    }

    private static void GroupFoldersGetStableUniqueSafeNames(string testRoot)
    {
        using var mod = new TestModFolder().File("files/a.bin", [1]).File("files/b.bin", [2]);
        mod.Meta(new JObject
        {
            ["FileVersion"] = 4,
            ["Name"] = "Groups",
            ["Groups"] = new JArray(
                new JObject { ["Type"] = "Single", ["Name"] = "Same/Name", ["Options"] = new JArray(new JObject { ["Name"] = "Option", ["Files"] = new JObject { ["chara/a.bin"] = "files/a.bin" } }) },
                new JObject { ["Type"] = "Single", ["Name"] = "Same\\Name", ["Options"] = new JArray(new JObject { ["Name"] = "Option", ["Files"] = new JObject { ["chara/b.bin"] = "files/b.bin" } }) }),
        }.ToString());

        Tidy(mod, testRoot);
        var paths = PenumbraModMeta.Load(mod.Path).AllFiles().Select(f => f.RelPath.Replace('\\', '/')).ToArray();
        Require(paths.Contains("Same_Name/Option/chara/a.bin") && paths.Contains("Same_Name_2/Option/chara/b.bin") &&
                mod.Exists("Same_Name/Option/chara/a.bin") && !mod.Exists("files/a.bin"),
            "option files move under safe, unique group and option folders, and the old ones go");
    }

    private static void ImagesAndThisPluginsFilesStayWhereTheyAre(string testRoot)
    {
        using var mod = new TestModFolder()
            .File("images/cover.png", [1]).File("images/option.png", [2]).File("old/a.tex", [3])
            .File("instant_edit/made.tex", [4]).File("instant_edit/unused.tex", [5]).File("tracked/ledger.tex", [6]);
        mod.Meta(new JObject
        {
            ["FileVersion"] = 4,
            ["Name"] = "Images",
            ["Image"] = "images/cover.png",
            ["DefaultData"] = new JObject
            {
                ["Files"] = new JObject
                {
                    ["chara/a.tex"] = "old/a.tex",
                    ["chara/made.tex"] = @"instant_edit\made.tex",
                    ["chara/ledger.tex"] = "tracked/ledger.tex",
                },
            },
            ["Groups"] = new JArray(new JObject
            {
                ["Type"] = "Multi",
                ["Name"] = "Extras",
                ["Options"] = new JArray(new JObject { ["Name"] = "Glow", ["Image"] = "images/option.png" }),
            }),
        }.ToString());

        Tidy(mod, testRoot, ledger: new HashSet<string> { "tracked/ledger.tex" });
        var files = (JObject)JObject.Parse(mod.ReadMeta())["DefaultData"]!["Files"]!;
        Require(mod.Exists("images/cover.png") && mod.Exists("images/option.png") && mod.Exists("chara/a.tex") && !mod.Exists("old/a.tex"),
            "images stay, while an ordinary file moves to its game path");
        Require(files["chara/made.tex"]!.Value<string>() == @"instant_edit\made.tex" && mod.Exists("instant_edit/unused.tex") &&
                files["chara/ledger.tex"]!.Value<string>() == "tracked/ledger.tex" && mod.Exists("tracked/ledger.tex"),
            "files this plugin wrote and everything under instant_edit stay where they are");
    }

    private static void ATargetWithOtherContentStopsTheMove(string testRoot)
    {
        using var mod = SimpleMod("old/a.bin", "payload");
        mod.File("chara/a.bin", System.Text.Encoding.UTF8.GetBytes("something else"));
        var before = mod.ReadMeta();
        var result = Tidy(mod, testRoot);
        Require(result.Changed == 0 && result.Problems.Count == 1 && mod.ReadMeta() == before && mod.Exists("old/a.bin"),
            "a canonical path already holding other content stops the move before anything changes");
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

    private static void AFailedCopyLeavesNoTempFile(string testRoot)
    {
        using var mod = SimpleMod("old/a.bin", "payload");
        Directory.CreateDirectory(Path.Combine(mod.Path, "chara", "a.bin"));
        var result = Tidy(mod, testRoot);
        Require(result.Changed == 0 && result.Problems.Count == 1 && !mod.Exists("chara/a.bin.instant-edit-tmp") && mod.Exists("old/a.bin"),
            "a copy that can't be finished leaves no half-written file");
    }
}
