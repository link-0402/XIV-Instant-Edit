using InstantEdit.Studio.Models;
using InstantEdit.Studio.Models.Project;
using InstantEdit.Studio.Services.Project;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// Edit mode after the review (group Editing): deleting a removed option's files keeps what the
/// ledger still needs, removed options' ledger entries go, a meta.json that can't be written changes
/// nothing, typed game paths, and local source paths kept out of the mod folder.
/// </summary>
internal static class ReviewFixEditingScenarios
{
    private const string Tex = "chara/equipment/e0001/texture/v01_c0201e0001_top_d.tex";

    public static void Run(string testRoot)
    {
        RemovingAnOptionKeepsAFileTheLedgerGoesBackTo();
        RemovingOneEntryKeepsAFileTheLedgerGoesBackTo();
        RemovingAnOptionDropsItsLedgerEntries();
        AMetaJsonThatCantBeWrittenChangesNothing();
        AnEditThatThrowsChangesNothing();
        TypedGamePathsRejectDotFoldersAndBadCharacters();
        LocalSourcePathsStayOutOfTheModFolder(testRoot);
    }

    // ── resui-1 ─────────────────────────────────────────────────────────────

    /// <summary>Red was repointed here at its own copy (its original is textures\shared_d.tex); Blue still uses that original.</summary>
    private static TestModFolder SharedOriginalMod()
    {
        var mod = new TestModFolder()
            .File("textures/shared_d.tex", [1])
            .File("Looks/Red/tex_d.tex", [2])
            .Meta($$"""
            {
              "FileVersion": 3,
              "Name": "Looks",
              "Groups": [
                { "Name": "Looks", "Type": "Single", "Options": [
                  { "Name": "Red",  "Files": { "{{Tex}}": "Looks\\Red\\tex_d.tex" } },
                  { "Name": "Blue", "Files": { "{{Tex}}": "textures\\shared_d.tex" } }
                ] }
              ]
            }
            """);
        var sidecar = new StudioProject();
        sidecar.Ledger.Files.Add(new LedgerFile { Option = "#0/#0", GamePath = Tex, RelPath = @"Looks\Red\tex_d.tex", OriginalRelPath = @"textures\shared_d.tex" });
        StudioProjectStore.Save(mod.Path, sidecar);
        return mod;
    }

    private static void RemovingAnOptionKeepsAFileTheLedgerGoesBackTo()
    {
        using var mod = SharedOriginalMod();
        var project = ModProject.Load(mod.Path);
        var blue = new OptionRef("#0", "#1");
        var files = new List<string>();
        var moved = new Dictionary<OptionRef, OptionRef>();
        project.EditStructure(meta =>
        {
            files = meta.FilesOf(blue).Select(f => f.RelPath).ToList();
            meta.RemoveOption(blue, out moved);
        });
        var result = project.ForgetOptions(new[] { blue }, moved, files);
        Require(mod.Exists("textures/shared_d.tex") && result.Deleted == 0,
            "removing an option with its files keeps a file the ledger points an entry back at (resui-1)");
    }

    private static void RemovingOneEntryKeepsAFileTheLedgerGoesBackTo()
    {
        using var mod = SharedOriginalMod();
        var project = ModProject.Load(mod.Path);
        var blue = new OptionRef("#0", "#1");
        Require(project.RelKeysInUse(blue, Tex).Contains(PathNormalizer.RelKey(@"textures\shared_d.tex")),
            "the remove dialog counts a file the ledger goes back to as used by something else");

        project.EditStructure(meta => meta.RemoveFile(blue, Tex));
        Require(project.DeleteUnused(new[] { @"textures\shared_d.tex" }) == 0 && mod.Exists("textures/shared_d.tex"),
            "taking out one entry with its file keeps a file the ledger goes back to");

        mod.File("unused/loose.tex", [3]);
        Require(project.DeleteUnused(new[] { @"unused\loose.tex" }) == 1 && !mod.Exists("unused/loose.tex"),
            "a file nothing needs is still deleted");
    }

    // ── resui-2 ─────────────────────────────────────────────────────────────

    private static void RemovingAnOptionDropsItsLedgerEntries()
    {
        using var mod = new TestModFolder()
            .File("textures/g_orig.tex", [1])
            .File("textures/h_orig.tex", [2])
            .File("Looks/G/tex_d.tex", [3])
            .File("Looks/H/tex_d.tex", [4])
            .Meta($$"""
            {
              "FileVersion": 3,
              "Name": "Looks",
              "Groups": [
                { "Name": "Looks", "Type": "Single", "Options": [
                  { "Name": "A" },
                  { "Name": "G", "Files": { "{{Tex}}": "Looks\\G\\tex_d.tex" } },
                  { "Name": "H", "Files": { "{{Tex}}": "Looks\\H\\tex_d.tex" } }
                ] }
              ]
            }
            """);
        var sidecar = new StudioProject();
        // H's replacement came first, G's later: G's entry follows H's in the ledger.
        sidecar.Ledger.Files.Add(new LedgerFile { Option = "#0/#2", GamePath = Tex, RelPath = @"Looks\H\tex_d.tex", OriginalRelPath = @"textures\h_orig.tex" });
        sidecar.Ledger.Files.Add(new LedgerFile { Option = "#0/#1", GamePath = Tex, RelPath = @"Looks\G\tex_d.tex", OriginalRelPath = @"textures\g_orig.tex" });
        StudioProjectStore.Save(mod.Path, sidecar);

        var project = ModProject.Load(mod.Path);
        var g = new OptionRef("#0", "#1");
        var files = new List<string>();
        var moved = new Dictionary<OptionRef, OptionRef>();
        project.EditStructure(meta =>
        {
            files = meta.FilesOf(g).Select(f => f.RelPath).ToList();
            meta.RemoveOption(g, out moved);
        });
        var result = project.ForgetOptions(new[] { g }, moved, files);

        static bool OnlyH(WriteLedger? ledger)
            => ledger != null && ledger.Files.Count == 1 && ledger.Files[0].Option == "#0/#1" && ledger.Files[0].OriginalRelPath == @"textures\h_orig.tex";
        Require(OnlyH(project.Sidecar.Ledger), "the removed option's ledger entry goes, and the moved option keeps its own original (resui-2)");
        Require(OnlyH(StudioProjectStore.LoadLedger(mod.Path)) && result.SaveError == null, "and the ledger on disk says so at once");
        Require(!mod.Exists("Looks/G/tex_d.tex") && mod.Exists("Looks/H/tex_d.tex") && mod.Exists("textures/h_orig.tex"),
            "the removed option's written file is deleted, the moved option's files stay");
    }

    // ── models-2 / resui-4 ─────────────────────────────────────────────────

    private static void AMetaJsonThatCantBeWrittenChangesNothing()
    {
        using var mod = SharedOriginalMod();
        var project = ModProject.Load(mod.Path);
        var red = new OptionRef("#0", "#0");
        var metaPath = Path.Combine(mod.Path, PenumbraModMeta.FileName);
        var before = mod.ReadMeta();
        int revision = project.Revision;

        Exception? error = null;
        using (new FileStream(metaPath, FileMode.Open, FileAccess.Read, FileShare.Read))   // held the way Penumbra or a scanner holds it
        {
            try
            {
                project.EditStructure(meta => meta.RenameOption(red, "Crimson"));
            }
            catch (Exception ex)
            {
                error = ex;
            }
        }
        Require(error is IOException or UnauthorizedAccessException, "a meta.json that can't be written is reported to the caller (models-2)");
        Require(project.Meta.OptionName(red) == "Red" && project.Revision == revision, "the open mod keeps meta.json as it was, not the unsaved edit");
        Require(mod.ReadMeta() == before && !File.Exists(metaPath + PenumbraModMeta.TempSuffix), "and no half-written copy is left in the mod folder");

        project.EditStructure(meta => meta.RenameOption(red, "Crimson"));
        Require(project.Meta.OptionName(red) == "Crimson" && PenumbraModMeta.Load(mod.Path).OptionName(red) == "Crimson",
            "once it can be written, the same edit goes through");
    }

    private static void AnEditThatThrowsChangesNothing()
    {
        using var mod = SharedOriginalMod();
        var project = ModProject.Load(mod.Path);
        var red = new OptionRef("#0", "#0");
        try
        {
            project.EditStructure(meta =>
            {
                meta.RenameOption(red, "Crimson");
                throw new InvalidOperationException("stale");
            });
        }
        catch (InvalidOperationException) { }
        Require(project.Meta.OptionName(red) == "Red", "an edit that fails halfway leaves the open mod's meta.json as it was");
    }

    // ── resui-6 ─────────────────────────────────────────────────────────────

    private static void TypedGamePathsRejectDotFoldersAndBadCharacters()
    {
        Require(PathNormalizer.HasStorableSegments(Tex), "an ordinary game path can be stored");
        Require(!PathNormalizer.HasStorableSegments("chara/equipment/../texture/x.tex")
             && !PathNormalizer.HasStorableSegments("chara/./texture/x.tex")
             && !PathNormalizer.HasStorableSegments("../x.tex"),
            "a game path with \".\" or \"..\" folders is refused (resui-6)");
        Require(!PathNormalizer.HasStorableSegments("chara/texture/a<b.tex")
             && !PathNormalizer.HasStorableSegments("chara/te|xture/x.tex")
             && !PathNormalizer.HasStorableSegments("chara/texture/what?.tex"),
            "a game path with characters Windows can't store is refused");
    }

    // ── models-5 ────────────────────────────────────────────────────────────

    private const string RedSource = @"C:\Users\SomeoneElse\Desktop\My mods\red_d.png";
    private const string NormSource = @"C:\Users\SomeoneElse\Desktop\My mods\norm.png";
    private const string Mtrl = "chara/equipment/e0001/material/v0001/mt_c0201e0001_top_a.mtrl";

    private static void LocalSourcePathsStayOutOfTheModFolder(string testRoot)
    {
        var root = Path.Combine(testRoot, "LocalSources", Guid.NewGuid().ToString("N"));
        var previous = LocalSources.Root;
        LocalSources.Root = root;
        try
        {
            RoundTrip(root);
            OldSidecarMigrates(root);
        }
        finally
        {
            LocalSources.Root = previous;
        }
    }

    private static TestModFolder PlainMod() => new TestModFolder().Meta("""{ "FileVersion": 3, "Name": "Plain" }""");

    private static void AddReplacements(StudioProject sidecar)
    {
        sidecar.Replacements.Add(new ResourceReplacement { GamePath = Tex, SourcePath = RedSource });
        var material = new MaterialSetup();
        material.Textures.Add(new TextureSlot { Type = TextureType.Normal, Postfix = "n", SourcePath = NormSource });
        sidecar.Replacements.Add(new ResourceReplacement { GamePath = Mtrl, Authored = material });
    }

    private static string SidecarText(TestModFolder mod) => File.ReadAllText(StudioProjectStore.PathIn(mod.Path));

    private static bool Restored(ModProject project)
        => project.Sidecar.Replacements[0].SourcePath == RedSource
        && project.Sidecar.Replacements[1].Authored!.Textures[0].SourcePath == NormSource;

    private static void RoundTrip(string root)
    {
        using var mod = PlainMod();
        var project = ModProject.Load(mod.Path);
        AddReplacements(project.Sidecar);
        project.MarkSidecarDirty();
        project.Flush();

        var text = SidecarText(mod);
        Require(!text.Contains("SomeoneElse") && !text.Contains(@"C:\\") && text.Contains("red_d.png") && text.Contains("norm.png"),
            "the sidecar in the mod folder keeps file names only, no local paths (models-5)");
        Require(Restored(project), "the open mod still has the full paths");
        Require(project.Sidecar.LocalId is { } id && File.Exists(LocalSources.FileFor(root, id))
             && File.ReadAllText(LocalSources.FileFor(root, id)).Contains("SomeoneElse"),
            "the full paths are kept in Instant Edit's config folder");

        var reopened = ModProject.Load(mod.Path);
        Require(Restored(reopened) && !reopened.SidecarDirty, "opening the mod again puts the full paths back");

        File.Delete(LocalSources.FileFor(root, project.Sidecar.LocalId!.Value));
        var received = ModProject.Load(mod.Path);
        Require(received.SidecarError == null
             && received.Sidecar.Replacements[0].SourcePath == "red_d.png"
             && received.Sidecar.Replacements[1].Authored!.Textures[0].SourcePath == "norm.png",
            "without the local file (a mod from someone else) the replacements still load, by file name only");
    }

    private static void OldSidecarMigrates(string root)
    {
        using var mod = PlainMod();
        var old = new StudioProject();
        AddReplacements(old);
        var previous = LocalSources.Root;
        LocalSources.Root = null;   // written the way older versions wrote it: paths in the sidecar
        try
        {
            StudioProjectStore.Save(mod.Path, old);
        }
        finally
        {
            LocalSources.Root = previous;
        }
        Require(SidecarText(mod).Contains("SomeoneElse"), "an older sidecar holds local paths");

        var project = ModProject.Load(mod.Path);
        Require(Restored(project) && project.SidecarDirty, "it is read as it is, and marked to be written again");
        project.Flush();
        Require(!SidecarText(mod).Contains("SomeoneElse"), "the next save takes the local paths out of the mod folder");
        Require(Restored(ModProject.Load(mod.Path)), "and they come back from the config folder");
    }
}
