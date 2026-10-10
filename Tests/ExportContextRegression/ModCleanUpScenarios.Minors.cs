using InstantEdit.Services.TextureCompression;
using InstantEdit.Studio;
using InstantEdit.Studio.Models.Project;
using InstantEdit.Studio.Services.CleanUp;
using InstantEdit.Studio.Services.Project;
using Newtonsoft.Json.Linq;
using static InstantEdit.TestSupport.Assertions;

/// <summary>Clean-up after the review's minor findings: split-layout mods, stale texture lists, formats, renamed mods, half-written files.</summary>
internal static partial class ModCleanUpScenarios
{
    public static void RunMinors(string testRoot)
    {
        SplitLayoutModsRestoreWhole(testRoot);
        SplitLayoutRollbackPutsTheFilesBack(testRoot);
        ATextureChangedSinceTheListIsLeftAlone(testRoot);
        KeepFormatIsRefusedForFormatsThatCantBeWritten();
        BackupsFollowAModRenamedInPenumbra(testRoot);
        AFailedCopyLeavesNoTempFile(testRoot);
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

    private static void ATextureChangedSinceTheListIsLeftAlone(string testRoot)
    {
        using var mod = TextureMod("t/base.tex");
        var candidate = Candidate(mod, "t/base.tex", [BaseUse]);
        mod.File("t/base.tex", Pattern(128, 128, 5));
        var result = Clean(mod, [new TextureJob(candidate, TextureFormatChoice.Bc7, 4096)], new FakeConverter(), testRoot);
        Require(result.Changed == 0 && result.Problems.Count == 1 && result.Problems[0].Contains("changed since") && FormatOf(mod, "t/base.tex") == TextureCost.Bgra8,
            "a texture that changed since the list was made is left alone, with the reason");
    }

    private static void KeepFormatIsRefusedForFormatsThatCantBeWritten()
    {
        var bc2 = TextureCost.Read(Header(TextureCost.Bc2, 256, 256))!.Value;
        var bc7 = TextureCost.Read(Header(TextureCost.Bc7, 256, 256))!.Value;
        Require(TextureCleanUp.WhyNotFormat(bc2, TextureFormatChoice.Keep) is { Length: > 0 }, "a format the encoder can't write can't be kept");
        Require(TextureCleanUp.WhyNotFormat(bc2, TextureFormatChoice.Bc7) is null && TextureCleanUp.WhyNotFormat(bc7, TextureFormatChoice.Keep) is null,
            "it can be encoded into another format, and a writable format can be kept");
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

    private static void AFailedCopyLeavesNoTempFile(string testRoot)
    {
        using var mod = SimpleMod("old/a.bin", "payload");
        Directory.CreateDirectory(Path.Combine(mod.Path, "chara", "a.bin"));
        var result = Tidy(mod, testRoot);
        Require(result.Changed == 0 && result.Problems.Count == 1 && !mod.Exists("chara/a.bin.instant-edit-tmp") && mod.Exists("old/a.bin"),
            "a copy that can't be finished leaves no half-written file");
    }
}
