using InstantEdit.Studio.Models;
using InstantEdit.Studio.Services.Sims;
using InstantEdit.Studio.Services.Stash;
using InstantEdit.Studio.Services.Unity;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// The package importer: where extractions go by default, the Unity .unitypackage reader against
/// hostile input, texture purposes, and the stash manifest each extraction leaves behind.
/// </summary>
internal static class PackageImportScenarios
{
    public static void Run(string testRoot)
    {
        var root = Path.Combine(testRoot, "PackageImport");
        Directory.CreateDirectory(root);

        OutputDefaultsToTheCache();
        OldTempDefaultMovesToTheCache();
        PickedFolderIsKept();

        ScanListsAssetsByPath(root);
        MaterialTexturesAreRead();
        PrefabMaterialsAreRead();
        NormalMapFlagIsRead(root);
        MaterialRemapIsRead();
        HostilePathsAreRefused(root);
        ExtractKeepsLayout(root);
        UnwantedAssetBytesAreNotKept(root);
        UnityWorkStopsWhenCancelled(root);

        PurposeFromUnityProperties();
        PurposeFromFileNames();
        PropertyBeatsFileName();
        SetNamesFollowTextureSets();
        TexturesAreSortedIntoItems(root);
        LayoutComparesLikeWithLike(root);
        ItemNamesKeepTheirDigits();
        UnityMaterialsPullLooseTexturesIntoTheirSet(root);
        UnityManifestGroupsTexturesByMaterial(root);
        SimsManifestUsesCasRoles(root);
        ManifestRoundTrips(root);
        LegacyFolderIsScanned(root);
        ReExtractReplacesManifest(root);
        PackagesExtractedTogetherShareAFolder(root);
        UnityManifestNamesModelMaterials(root);
        MaterialNamesRoundTrip(root);

        StashApplyMapsPurposes(root);
        StashApplyFillsSlots(root);
        ItemsAreOnTheModelsTheirNamesTell(root);
        ExtractionsAreListedNewestFirst(root);
        RemoveStaysInsideTheOutputFolder(root);

        StoppedReExtractKeepsTheEarlierOne(root);
        StoppedFirstExtractLeavesNothing(root);
        TypedFolderIsListedOnceSettled();
        OpenedFolderReadsUnityMetadata(root);
        OpenedFolderNamesModelMaterials(root);
        FolderScanStopsAtItsCap(root);
        OpenedFoldersAreRememberedNotDeleted(root);

        BlenderSidecarIsRead(root);
        BlenderExportsAreMerged(root);
        SidecarMakesAStashFolder(root);
        FinishCarriesBlenderExportsOver(root);
        FailedCarryOverIsTakenUpByTheNextRun(root);
        ReExtractKeepsBlenderExports(root);
        LeftoverEarlierRunGivesUpItsBlenderExports(root);
        SimsReExtractKeepsBlenderExports(root);
        BlenderFoldersAreListedBesideAPickedOutput(root);
        SidecarOnlyFolderIsFromBlender(root);
        BlenderWritesAreNoticed(root);

        ModelMaterialNamesAreNormalized();
        LookupFindsTheTextureFolder(root);
        LookupMatchesMaterials(root);
        LookupByWordsPrefersADiffuse(root);
        LookupByLongerNames(root);
        LookupByUvs(root);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Models saved from Blender
    // ─────────────────────────────────────────────────────────────────────────

    private static string Sidecar(string models, int version = 1) =>
        $$"""{ "schema": "instant-edit.blender-exports", "version": {{version}}, "models": [{{models}}] }""";

    private static void WriteBlenderExport(string folder)
    {
        Directory.CreateDirectory(Path.Combine(folder, "Blender"));
        File.WriteAllBytes(Path.Combine(folder, "Blender", "Top.mdl"), new byte[] { 7 });
        File.WriteAllText(Path.Combine(folder, BlenderExports.FileName), Sidecar("""{ "file": "Blender/Top.mdl", "sources": ["G:/stash/Kaihua/Models/Kaihua.fbx"] }"""));
    }

    private static void BlenderSidecarIsRead(string root)
    {
        var folder = Directory.CreateDirectory(Path.Combine(root, "sidecar-read")).FullName;
        var sidecar = Path.Combine(folder, BlenderExports.FileName);
        Require(BlenderExports.Read(folder).Count == 0, "a folder without a sidecar lists no Blender models");

        File.WriteAllText(sidecar, Sidecar("""
            { "file": "Blender/Top.mdl", "exportedUtc": "2026-10-27T12:00:00Z", "sources": ["G:/stash/Kaihua/Models/Kaihua.fbx", "G:/stash/Kaihua/Models/Extra.fbx"] },
            { "file": "Blender\\Legs.mdl", "exportedUtc": "2026-10-27T12:01:00Z" },
            { "exportedUtc": "2026-10-27T12:02:00Z", "sources": ["G:/a.fbx"] },
            { "file": 5 },
            { "file": "../outside.mdl" },
            { "file": "C:/elsewhere/x.mdl" },
            "not a model"
            """));
        var read = BlenderExports.Read(folder);
        Require(read.Select(m => m.File).SequenceEqual(new[] { "Blender/Top.mdl", "Blender/Legs.mdl" }),
            $"the sidecar's models are read with '/' paths; entries without a file, or with one outside the folder, are skipped ({string.Join(", ", read.Select(m => m.File))})");
        Require(read.Count == 2 && read[0].Sources.SequenceEqual(new[] { "G:/stash/Kaihua/Models/Kaihua.fbx", "G:/stash/Kaihua/Models/Extra.fbx" }) && read[1].Sources.Count == 0,
            "each model keeps its source files in order; one without sources has none");

        File.WriteAllText(sidecar, Sidecar("""{ "file": "Blender/Top.mdl" }""", version: 2));
        Require(BlenderExports.Read(folder).Count == 0, "a sidecar of an unknown version is ignored");
        File.WriteAllText(sidecar, """{ "schema": "something-else", "version": 1, "models": [{ "file": "Blender/Top.mdl" }] }""");
        Require(BlenderExports.Read(folder).Count == 0, "a file of another schema is ignored");
        File.WriteAllText(sidecar, """{ "models": [ not json""");
        Require(BlenderExports.Read(folder).Count == 0, "a damaged sidecar reads as none");
    }

    private static void BlenderExportsAreMerged(string root)
    {
        var folder = Path.Combine(root, "sidecar-merge");
        Directory.CreateDirectory(Path.Combine(folder, "Blender"));
        File.WriteAllBytes(Path.Combine(folder, "Blender", "Top.mdl"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(folder, "Blender", "Listed.mdl"), new byte[] { 1 });
        File.WriteAllText(Path.Combine(folder, BlenderExports.FileName), Sidecar("""
            { "file": "Blender/Top.mdl", "sources": ["G:/stash/Kaihua/Models/Kaihua.fbx"] },
            { "file": "blender/listed.mdl" },
            { "file": "Blender/Gone.mdl" }
            """));
        var manifest = new StashManifest { Source = folder, SourceKind = "unity" };
        manifest.Entries.Add(new StashEntry { Path = "Blender/Listed.mdl", Kind = StashKind.Model, Detail = "mine" });

        BlenderExports.Merge(manifest, folder);
        BlenderExports.Merge(manifest, folder);
        var added = manifest.Entries.Where(e => e.Path == "Blender/Top.mdl").ToList();
        Require(added.Count == 1 && added[0] is { Kind: StashKind.Model, Detail: "from Blender", NeedsConversion: false },
            "a model Blender saved is listed once, as a model from Blender, however often it is merged");
        Require(manifest.Entries.Count == 2 && manifest.Entries[0].Detail == "mine",
            $"a file already listed is left as it is, and one that isn't there is skipped ({string.Join(", ", manifest.Entries.Select(e => e.Path))})");
    }

    private static void SidecarMakesAStashFolder(string root)
    {
        var output = Path.Combine(root, "sidecar-listing");
        var folder = Path.Combine(output, "Kaihua");
        WriteBlenderExport(folder);
        File.WriteAllText(Path.Combine(folder, BlenderExports.FileName + ".tmp"), "{");
        Require(StashFolders.IsExtraction(folder) && StashFolders.ListExtractions(output).Select(Path.GetFileName).SequenceEqual(new[] { "Kaihua" }),
            "a folder holding only Blender's sidecar is listed in the stash");
        Require(StashManifest.IsBookkeeping(BlenderExports.FileName) && StashManifest.IsBookkeeping("Blender-Exports.json.tmp"),
            "the sidecar and its .tmp are bookkeeping");
        Require(StashManifest.Scan(folder).Entries.Select(e => e.Path).SequenceEqual(new[] { "Blender/Top.mdl" }),
            "a scan lists the model, not the sidecar or its .tmp");
    }

    private static void FinishCarriesBlenderExportsOver(string root)
    {
        var output = Path.Combine(root, "carry-out");
        var earlier = Path.Combine(output, "pkg");
        WriteBlenderExport(earlier);
        Directory.CreateDirectory(Path.Combine(earlier, "Blender", "Sizes"));
        File.WriteAllBytes(Path.Combine(earlier, "Blender", "Sizes", "Top_L.mdl"), new byte[] { 2 });
        File.WriteAllBytes(Path.Combine(earlier, "Blender", "Shared.blend"), new byte[] { 3 });
        File.WriteAllBytes(Path.Combine(earlier, "old.png"), PngBytes);

        var claim = ExtractionFolder.Claim(output, "pkg", _ => true);
        Require(claim.Path == earlier && Directory.Exists(earlier + ".previous"), "the earlier run is moved aside");
        // This run's package has a Blender folder of its own.
        Directory.CreateDirectory(Path.Combine(claim.Path, "Blender"));
        File.WriteAllBytes(Path.Combine(claim.Path, "Blender", "Shared.blend"), new byte[] { 9 });
        File.WriteAllBytes(Path.Combine(claim.Path, "new.png"), PngBytes);
        claim.Finish();

        Require(File.Exists(Path.Combine(earlier, "Blender", "Top.mdl")) && File.Exists(Path.Combine(earlier, "Blender", "Sizes", "Top_L.mdl"))
                && File.Exists(Path.Combine(earlier, BlenderExports.FileName)),
            "the models Blender saved into the earlier run, and its sidecar, move into the new run");
        Require(File.ReadAllBytes(Path.Combine(earlier, "Blender", "Shared.blend")).SequenceEqual(new byte[] { 9 }) && File.Exists(Path.Combine(earlier, "new.png")),
            "a file the new run wrote under the same name is kept");
        Require(!File.Exists(Path.Combine(earlier, "old.png")) && !Directory.Exists(earlier + ".previous"), "the rest of the earlier run goes");
    }

    private static void FailedCarryOverIsTakenUpByTheNextRun(string root)
    {
        var output = Path.Combine(root, "carry-locked-out");
        var folder = Path.Combine(output, "pkg");
        var aside = folder + ".previous";
        WriteBlenderExport(folder);

        var claim = ExtractionFolder.Claim(output, "pkg", _ => true);
        Directory.CreateDirectory(claim.Path);
        // A model another program holds open can't be moved.
        using (File.Open(Path.Combine(aside, "Blender", "Top.mdl"), FileMode.Open, FileAccess.Read, FileShare.None))
            claim.Finish();
        Require(File.Exists(Path.Combine(aside, "Blender", "Top.mdl")) && File.Exists(Path.Combine(aside, BlenderExports.FileName))
                && !File.Exists(Path.Combine(folder, BlenderExports.FileName)),
            "a carry-over that fails keeps the earlier run, sidecar included, so the new run lists no model it lacks");

        var next = ExtractionFolder.Claim(output, "pkg", _ => true);
        Directory.CreateDirectory(next.Path);
        next.Finish();
        Require(File.Exists(Path.Combine(folder, "Blender", "Top.mdl")) && File.Exists(Path.Combine(folder, BlenderExports.FileName)) && !Directory.Exists(aside),
            "the next run takes the models left in the earlier run before clearing it");
    }

    private static void ReExtractKeepsBlenderExports(string root)
    {
        var output = Path.Combine(root, "blender-reextract-out");
        var scan = UnityPackageReader.Scan(BasicPackage(root, "exported"), _ => { }, CancellationToken.None);
        var first = UnityPackageReader.Extract(scan, output, _ => { }, CancellationToken.None);
        WriteBlenderExport(first.RootFolder);

        var again = UnityPackageReader.Extract(scan, output, _ => { }, CancellationToken.None);
        Require(again.RootFolder == first.RootFolder && Directory.GetDirectories(output).Length == 1,
            "re-extracting a package Blender saved models into reuses its folder");
        Require(File.Exists(Path.Combine(again.RootFolder, "Blender", "Top.mdl")) && File.Exists(Path.Combine(again.RootFolder, BlenderExports.FileName)),
            "the models and the sidecar survive the re-extraction");
        var manifest = StashManifest.Read(again.RootFolder)!;
        BlenderExports.Merge(manifest, again.RootFolder);
        Require(manifest.Entries.Any(e => e is { Path: "Blender/Top.mdl", Detail: "from Blender" }), "and the stash lists them from Blender");

        // A folder Blender made on its own holds no package's earlier run.
        var solo = Path.Combine(output, "solo");
        WriteBlenderExport(solo);
        var claimed = UnityPackageReader.Extract(UnityPackageReader.Scan(BasicPackage(root, "solo"), _ => { }, CancellationToken.None), output, _ => { }, CancellationToken.None);
        Require(claimed.RootFolder == solo + "_2" && File.Exists(Path.Combine(solo, "Blender", "Top.mdl")),
            "a package of the same name leaves a folder of Blender exports alone and uses a numbered one");
    }

    private static void LeftoverEarlierRunGivesUpItsBlenderExports(string root)
    {
        // An earlier run of the package left as .previous beside the live one, holding the models Blender saved into it.
        var output = Path.Combine(root, "leftover-out");
        var scan = UnityPackageReader.Scan(BasicPackage(root, "leftover"), _ => { }, CancellationToken.None);
        var live = UnityPackageReader.Extract(scan, output, _ => { }, CancellationToken.None);
        var stale = UnityPackageReader.Extract(scan, Path.Combine(root, "leftover-stale"), _ => { }, CancellationToken.None);
        var aside = live.RootFolder + ".previous";
        Directory.Move(stale.RootFolder, aside);
        WriteBlenderExport(aside);

        var again = UnityPackageReader.Extract(scan, output, _ => { }, CancellationToken.None);
        Require(again.RootFolder == live.RootFolder && File.Exists(Path.Combine(again.RootFolder, "Blender", "Top.mdl"))
                && File.Exists(Path.Combine(again.RootFolder, BlenderExports.FileName)),
            "re-extracting takes the Blender models out of an earlier run left beside the folder");
        Require(!Directory.Exists(aside) && Directory.GetDirectories(output).Length == 1, "and that earlier run is cleared");
    }

    private static void SimsReExtractKeepsBlenderExports(string root)
    {
        var source = Directory.CreateDirectory(Path.Combine(root, "sims-blender")).FullName;
        var output = Path.Combine(root, "sims-blender-out");
        string Package(string name)
        {
            var path = Path.Combine(source, name + ".package");
            SimsImportScenarios.WritePackage(path, new SimsImportScenarios.Res(SimsResourceType.ImgOverlay, 1, SimsImportScenarios.UncompressedDds(4, 4, pixels: 16)));
            return path;
        }

        var scan = SimsPackageInspector.Scan(Package("dress"));
        var first = SimsPackageInspector.Extract(scan, output);
        WriteBlenderExport(first.RootFolder);
        var again = SimsPackageInspector.Extract(scan, output);
        Require(again.RootFolder == first.RootFolder && File.Exists(Path.Combine(again.RootFolder, "Blender", "Top.mdl"))
                && File.Exists(Path.Combine(again.RootFolder, BlenderExports.FileName)),
            "re-extracting a Sims package reuses its folder and keeps the models Blender saved into it");

        var solo = Path.Combine(output, "lone");
        WriteBlenderExport(solo);
        var claimed = SimsPackageInspector.Extract(SimsPackageInspector.Scan(Package("lone")), output);
        Require(claimed.RootFolder == solo + "_2" && File.Exists(Path.Combine(solo, "Blender", "Top.mdl")),
            "a Sims package of the same name leaves a folder of Blender exports alone");
    }

    private static void BlenderFoldersAreListedBesideAPickedOutput(string root)
    {
        // The user picked an output folder of their own; Blender still saves into the cache's package-imports.
        var output = Path.Combine(root, "picked-out");
        var imports = Path.Combine(root, "picked-imports");
        var mine = Directory.CreateDirectory(Path.Combine(output, "mine")).FullName;
        File.WriteAllText(Path.Combine(mine, StashManifest.FileName), "{}");
        var kaihua = Path.Combine(imports, "Kaihua");
        WriteBlenderExport(kaihua);
        var saved = Path.Combine(imports, "saved");
        WriteBlenderExport(saved);
        File.WriteAllText(Path.Combine(saved, StashManifest.FileName), "{}");
        var plain = Directory.CreateDirectory(Path.Combine(imports, "plain")).FullName;
        File.WriteAllText(Path.Combine(plain, StashManifest.FileName), "{}");
        Directory.SetLastWriteTimeUtc(saved, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Directory.SetLastWriteTimeUtc(mine, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        Directory.SetLastWriteTimeUtc(kaihua, new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));
        Directory.SetLastWriteTimeUtc(plain, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        var listed = StashFolders.ListStash(output, imports);
        Require(listed.SequenceEqual(new[] { kaihua, mine, saved }),
            $"beside the output folder's extractions, the package-imports folders Blender saved into are listed, newest first; one it never saved into stays out ({string.Join(", ", listed.Select(Path.GetFileName))})");
        var same = StashFolders.ListStash(imports, imports.ToUpperInvariant() + Path.DirectorySeparatorChar);
        Require(same.Select(Path.GetFileName).SequenceEqual(new[] { "plain", "Kaihua", "saved" }),
            $"with package-imports as the output folder, each of its extractions is listed once ({string.Join(", ", same.Select(Path.GetFileName))})");
        Require(StashFolders.ListStash(output, null).SequenceEqual(new[] { mine }) && StashFolders.ListStash(output, Path.Combine(root, "no-imports")).SequenceEqual(new[] { mine }),
            "without a package-imports folder, only the output folder is listed");

        Require(StashFolders.RootOf(kaihua, output, imports) == imports && StashFolders.RootOf(mine, output, imports) == output
                && StashFolders.RootOf(mine, output, null) == output,
            "a folder belongs to the folder it was listed from");
        Require(StashFolders.Remove(StashFolders.RootOf(kaihua, output, imports), kaihua) == null && !Directory.Exists(kaihua),
            "a Blender folder listed beside a picked output folder can be removed");
    }

    private static void SidecarOnlyFolderIsFromBlender(string root)
    {
        var folder = Path.Combine(root, "from-blender");
        WriteBlenderExport(folder);
        Require(StashFolders.IsFromBlender(folder), "a folder whose only bookkeeping is Blender's sidecar is from Blender");
        foreach (var marker in new[] { StashManifest.FileName, "extract-report.txt", SimsPackageInspector.ManifestName })
        {
            var imported = Path.Combine(root, "imported-" + Path.GetFileNameWithoutExtension(marker));
            WriteBlenderExport(imported);
            File.WriteAllText(Path.Combine(imported, marker), "{}");
            Require(!StashFolders.IsFromBlender(imported), $"a folder an import made ({marker}) isn't from Blender, though Blender saved into it");
        }
        var plain = Directory.CreateDirectory(Path.Combine(root, "no-sidecar")).FullName;
        File.WriteAllBytes(Path.Combine(plain, "a.png"), PngBytes);
        Require(!StashFolders.IsFromBlender(plain) && !StashFolders.IsFromBlender(Path.Combine(root, "not-there")),
            "a folder without the sidecar isn't from Blender");
    }

    private static void BlenderWritesAreNoticed(string root)
    {
        // Blender saves into the cache's package-imports; the user picked another output folder.
        var output = Directory.CreateDirectory(Path.Combine(root, "watch-out")).FullName;
        var imports = Path.Combine(root, "watch-imports");
        var shown = Path.Combine(imports, "Kaihua");
        var sidecar = Path.Combine(shown, BlenderExports.FileName);
        WriteBlenderExport(shown);
        var other = Path.Combine(imports, "Other");
        WriteBlenderExport(other);
        var imported = Directory.CreateDirectory(Path.Combine(imports, "imported")).FullName;
        File.WriteAllText(Path.Combine(imported, StashManifest.FileName), "{}");

        // Write times only tick every few milliseconds: everything is dated well back first, so each write shows.
        var old = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var watch = new StashWatch();
        void Settle(string folder)
        {
            foreach (var file in Directory.GetFiles(imports, BlenderExports.FileName, SearchOption.AllDirectories))
                File.SetLastWriteTimeUtc(file, old);
            foreach (var dir in new[] { output, imports }.Concat(Directory.GetDirectories(output)).Concat(Directory.GetDirectories(imports)))
                Directory.SetLastWriteTimeUtc(dir, old);
            watch.Listing(output, imports);
            watch.Showing(folder);
        }
        void Rewrite(string path)
        {
            File.WriteAllText(path + ".tmp", Sidecar("""{ "file": "Blender/Top.mdl" }, { "file": "Blender/Legs.mdl" }"""));
            File.Move(path + ".tmp", path, overwrite: true);
        }

        Settle(shown);
        Require(!watch.FoldersChanged() && !watch.ShownChanged(), "with nothing written, nothing is read again");

        // Another model into the shown folder: the model first, then the sidecar (a .tmp, then replaced).
        File.WriteAllBytes(Path.Combine(shown, "Blender", "Legs.mdl"), new byte[] { 1 });
        Require(!watch.ShownChanged(), "a model written before the sidecar lists it doesn't re-show the folder yet");
        Rewrite(sidecar);
        Require(watch.ShownChanged(), "the sidecar written again re-shows the folder shown");
        watch.Showing(shown);
        Require(!watch.ShownChanged(), "once shown again, it isn't re-shown until the next write");

        Settle(shown);
        Rewrite(Path.Combine(other, BlenderExports.FileName));
        Require(!watch.ShownChanged(), "a sidecar written in another folder doesn't re-show the one shown");
        Require(watch.FoldersChanged(), "it re-lists the folders, so the one saved into moves up");

        // An import Blender saves into for the first time.
        Settle(imported);
        Rewrite(Path.Combine(imported, BlenderExports.FileName));
        Require(watch.ShownChanged(), "a first sidecar in the folder shown re-shows it");

        // A new folder: Blender makes it and writes the model, and only then the sidecar.
        Settle(shown);
        var fresh = Path.Combine(imports, "Fresh");
        Directory.CreateDirectory(Path.Combine(fresh, "Blender"));
        File.WriteAllBytes(Path.Combine(fresh, "Blender", "Fresh.mdl"), new byte[] { 1 });
        Require(watch.FoldersChanged(), "a new folder in package-imports re-lists, though the user picked another output folder");
        Settle(shown);
        Require(!StashFolders.ListStash(output, imports).Contains(fresh), "listed before its sidecar landed, the new folder isn't in the list yet");
        Rewrite(Path.Combine(fresh, BlenderExports.FileName));
        Require(watch.FoldersChanged() && StashFolders.ListStash(output, imports).Contains(fresh),
            "its sidecar landing afterwards re-lists, and then it is listed");
        Require(!watch.ShownChanged(), "a new folder never re-shows the one shown");

        Settle(shown);
        Directory.CreateDirectory(Path.Combine(output, "new"));
        Require(watch.FoldersChanged(), "a folder made in the output folder re-lists");
        Settle(shown);
        Directory.Delete(other, recursive: true);
        Require(watch.FoldersChanged(), "a folder removed re-lists");

        var none = new StashWatch();
        Require(!none.FoldersChanged() && !none.ShownChanged(), "before anything is listed or shown, nothing is read again");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Textures for a model's materials
    // ─────────────────────────────────────────────────────────────────────────

    private static string Touch(string folder, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar)));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, PngBytes);
        return path;
    }

    /// <summary>A model Blender saved into <paramref name="folder"/>, listed in its sidecar as made from <paramref name="sources"/>.</summary>
    private static string SaveFromBlender(string folder, string file, params string[] sources)
    {
        var model = Touch(folder, file);
        var listed = string.Join(", ", sources.Select(s => $"\"{s.Replace('\\', '/')}\""));
        File.WriteAllText(Path.Combine(folder, BlenderExports.FileName), Sidecar($$"""{ "file": "{{file}}", "sources": [{{listed}}] }"""));
        return model;
    }

    /// <summary>
    /// A VRChat outfit's extraction: its model's meta maps "Body" to the Unity material Mat_Skin, three
    /// materials list their textures, and two texture sets (a shirt, gloves with a cow variant) no material lists.
    /// </summary>
    private static string OutfitExtraction(string stash)
    {
        var outfit = Path.GetFullPath(Path.Combine(stash, "Outfit"));
        if (Directory.Exists(outfit))
            Directory.Delete(outfit, true);
        var manifest = new StashManifest { Source = @"C:\p\Outfit.unitypackage", SourceKind = "unity", ExtractedUtc = DateTime.UtcNow };
        Touch(outfit, "Models/Outfit.fbx");
        manifest.Entries.Add(new StashEntry
        {
            Path = "Models/Outfit.fbx", Kind = StashKind.Model, NeedsConversion = true, MaterialNames = new() { ["Body"] = "Mat_Skin" },
        });
        foreach (var (path, purpose) in new[]
                 {
                     ("Textures/skin_d.png", TexturePurpose.Diffuse), ("Textures/skin_n.png", TexturePurpose.Normal),
                     ("Textures/body.png", TexturePurpose.Diffuse), ("Textures/hair.png", TexturePurpose.Diffuse),
                     ("Textures/Outfit_Shirt.png", TexturePurpose.Diffuse), ("Textures/Outfit_Shirt_Normal.png", TexturePurpose.Normal),
                     ("Textures/Outfit_Gloves.png", TexturePurpose.Diffuse), ("Textures/Outfit_Gloves_Cow.png", TexturePurpose.Diffuse),
                     ("Textures/Outfit_Gloves_Normal.png", TexturePurpose.Normal),
                 })
        {
            Touch(outfit, path);
            manifest.Entries.Add(new StashEntry { Path = path, Kind = StashKind.Texture, Purpose = purpose });
        }
        manifest.Materials.Add(new StashMaterial { Name = "Mat_Skin", Textures = { [TexturePurpose.Diffuse] = "Textures/skin_d.png", [TexturePurpose.Normal] = "Textures/skin_n.png" } });
        manifest.Materials.Add(new StashMaterial { Name = "Body", Textures = { [TexturePurpose.Diffuse] = "Textures/body.png" } });
        manifest.Materials.Add(new StashMaterial { Name = "hair", Textures = { [TexturePurpose.Diffuse] = "Textures/hair.png" } });
        manifest.Write(outfit);
        return outfit;
    }

    private static void ModelMaterialNamesAreNormalized()
    {
        Require(ModelTextureLookup.NameOf("/Body.mtrl") == "Body" && ModelTextureLookup.NameOf(@"\Body.MTRL") == "Body",
            "a model's material is looked up by its name, without the slash and .mtrl");
        Require(ModelTextureLookup.NameOf("chara/equipment/e6000/material/v0001/mt_c0201e6000_top_a.mtrl") == "mt_c0201e6000_top_a",
            "a full material path is looked up by its file name");
        Require(ModelTextureLookup.NameOf("Mat.Skin") == "Mat.Skin" && ModelTextureLookup.NameOf("/Body.001.mtrl") == "Body.001",
            "a dot in the name is kept: only .mtrl is dropped");
    }

    private static void LookupFindsTheTextureFolder(string root)
    {
        var stash = Path.Combine(root, "lookup-stash");
        var outfit = OutfitExtraction(stash);
        var none = Array.Empty<string>();

        // Blender saved into a folder of its own, from the outfit's model.
        var saves = Path.GetFullPath(Path.Combine(stash, "Saves"));
        var top = SaveFromBlender(saves, "Blender/Top.mdl", Path.Combine(outfit, "Models", "Outfit.fbx"), Path.Combine(saves, "Other.fbx"));
        Require(ModelTextureLookup.Open(top, new[] { stash }, none).Folder == outfit,
            "a Blender export's textures come from the stash folder its first source is in");
        Require(string.Equals(ModelTextureLookup.Open(top.ToLowerInvariant(), new[] { stash.ToUpperInvariant() }, none).Folder, outfit, StringComparison.OrdinalIgnoreCase),
            "its sidecar entry and the stash folders are matched whatever their case");

        // Its source in a folder of loose files the user opened (the deepest one that holds it).
        var avatars = Path.GetFullPath(Path.Combine(root, "lookup-opened"));
        var avatar = Path.Combine(avatars, "Avatar");
        Touch(avatar, "FBX/Avatar.fbx");
        Touch(avatar, "Textures/Avatar_Body.png");
        Touch(avatar, "Textures/Avatar_Body_Normal.png");
        var fromAvatar = Path.GetFullPath(Path.Combine(stash, "Avatar"));
        var avatarModel = SaveFromBlender(fromAvatar, "Blender/Avatar.mdl", Path.Combine(avatar, "FBX", "Avatar.fbx"));
        var opened = ModelTextureLookup.Open(avatarModel, new[] { stash }, new[] { avatars, avatar });
        Require(opened.Folder == avatar && opened.Manifest?.SourceKind == "folder",
            $"an export whose source is in an opened folder takes that folder's textures, scanned ({opened.Folder})");
        Require(opened.TexturesFor("/Body.mtrl").GetValueOrDefault(TexturePurpose.Normal) == Path.Combine(avatar, "Textures", "Avatar_Body_Normal.png"),
            "and finds them there");
        Require(ModelTextureLookup.Open(avatarModel, new[] { stash }, none).Folder == fromAvatar,
            "an export whose source is in no stash folder takes the textures of the folder it was saved into");

        Require(ModelTextureLookup.Open(Touch(outfit, "Models/Plain.mdl"), new[] { stash }, none).Folder == outfit,
            "a model of an extraction, not from Blender, takes its own folder's textures");
        var loose = ModelTextureLookup.Open(Touch(root, "lookup-loose/Top.mdl"), new[] { stash }, none);
        Require(loose.Folder == null && loose.Manifest == null && loose.TexturesFor("/Body.mtrl").Count == 0,
            "a model in no stash folder finds no textures");
        Require(ModelTextureLookup.Open(Touch(stash, "Mine/Top.mdl"), new[] { stash }, none).Folder == null,
            "a folder in the stash's root that no extraction made is not a stash folder");
    }

    private static void LookupMatchesMaterials(string root)
    {
        var stash = Path.Combine(root, "match-stash");
        var outfit = OutfitExtraction(stash);
        var top = SaveFromBlender(Path.Combine(stash, "Saves"), "Blender/Top.mdl", Path.Combine(outfit, "Models", "Outfit.fbx"));
        var lookup = ModelTextureLookup.Open(top, new[] { stash }, Array.Empty<string>());
        string Texture(string name) => Path.Combine(outfit, "Textures", name);
        string Show(IReadOnlyDictionary<TexturePurpose, string> found) => string.Join(", ", found.Select(p => $"{p.Key}={Path.GetFileName(p.Value)}"));

        var body = lookup.TexturesFor("/Body.mtrl");
        Require(body.Count == 2 && body.GetValueOrDefault(TexturePurpose.Diffuse) == Texture("skin_d.png") && body.GetValueOrDefault(TexturePurpose.Normal) == Texture("skin_n.png"),
            $"the source model's remap names the Unity material first, before one named the same; paths are full ({Show(body)})");
        Require(lookup.TexturesFor("body.001").GetValueOrDefault(TexturePurpose.Diffuse) == Texture("skin_d.png"),
            "the remap is read whatever the case, and without Blender's .001");
        var hair = lookup.TexturesFor("/HAIR.001.mtrl");
        Require(hair.GetValueOrDefault(TexturePurpose.Diffuse) == Texture("hair.png"), $"next, the material named the same, whatever the case and without .001 ({Show(hair)})");

        var shirt = lookup.TexturesFor("Shirt");
        Require(shirt.Count == 2 && shirt.GetValueOrDefault(TexturePurpose.Diffuse) == Texture("Outfit_Shirt.png")
                && shirt.GetValueOrDefault(TexturePurpose.Normal) == Texture("Outfit_Shirt_Normal.png"),
            $"next, the texture set whose names hold the material's telling words ({Show(shirt)})");
        var cow = lookup.TexturesFor("/Cow.mtrl");
        Require(cow.GetValueOrDefault(TexturePurpose.Diffuse) == Texture("Outfit_Gloves_Cow.png") && cow.GetValueOrDefault(TexturePurpose.Normal) == Texture("Outfit_Gloves_Normal.png"),
            $"of that set, the texture its words name comes first ({Show(cow)})");
        Require(lookup.TexturesFor("Gloves").GetValueOrDefault(TexturePurpose.Diffuse) == Texture("Outfit_Gloves.png"), "else the set's own");
        Require(lookup.TexturesFor("Unrelated").Count == 0 && lookup.TexturesFor("Outfit_Logo").Count == 0, "a name nothing tells finds nothing");

        var plain = ModelTextureLookup.Open(Touch(outfit, "Models/Plain.mdl"), new[] { stash }, Array.Empty<string>());
        Require(plain.TexturesFor("/Body.mtrl").GetValueOrDefault(TexturePurpose.Diffuse) == Texture("body.png"),
            "a model without a remap finds the material named the same");

        // Saved beside its source, in the extraction's own Blender folder, as Blender does by default.
        var beside = ModelTextureLookup.Open(SaveFromBlender(outfit, "Blender/Top.mdl", Path.Combine(outfit, "Models", "Outfit.fbx")),
            new[] { stash }, Array.Empty<string>());
        Require(beside.Folder == outfit && beside.TexturesFor("/Body.mtrl").GetValueOrDefault(TexturePurpose.Diffuse) == Texture("skin_d.png"),
            "an export saved into its package's own folder takes that folder's textures, through its source's remap");
    }

    /// <summary>
    /// Of two texture sets the material's words name, the one with a diffuse wins, though the other's names are
    /// shorter (an "Alternative" set of a normal and a mask); of its diffuses, the one named in the fewest words.
    /// </summary>
    private static void LookupByWordsPrefersADiffuse(string root)
    {
        var stash = Path.Combine(root, "words-stash");
        var coat = Path.GetFullPath(Path.Combine(stash, "Coat"));
        if (Directory.Exists(coat))
            Directory.Delete(coat, true);
        var manifest = new StashManifest { Source = @"C:\p\Coat.unitypackage", SourceKind = "unity", ExtractedUtc = DateTime.UtcNow };
        foreach (var (path, purpose) in new[]
                 {
                     ("Alt/Coat_Normal.png", TexturePurpose.Normal), ("Alt/Coat_Mask.png", TexturePurpose.Mask),
                     ("Main/Coat_Main_Body.png", TexturePurpose.Diffuse), ("Main/Coat_Main_Body-Zebra.png", TexturePurpose.Diffuse),
                     ("Main/Coat_Main_Body_Normal.png", TexturePurpose.Normal),
                     ("Gloves/Gloves.png", TexturePurpose.Diffuse), ("Gloves/Gloves_Normal.png", TexturePurpose.Normal),
                 })
        {
            Touch(coat, path);
            manifest.Entries.Add(new StashEntry { Path = path, Kind = StashKind.Texture, Purpose = purpose });
        }
        manifest.Write(coat);
        var lookup = ModelTextureLookup.Open(Touch(coat, "Models/Coat.mdl"), new[] { stash }, Array.Empty<string>());
        var found = lookup.TexturesFor("/Coat.mtrl");
        var shown = string.Join(", ", found.Select(p => $"{p.Key}={Path.GetFileName(p.Value)}"));
        Require(found.GetValueOrDefault(TexturePurpose.Diffuse) == Path.Combine(coat, "Main", "Coat_Main_Body.png")
                && found.GetValueOrDefault(TexturePurpose.Normal) == Path.Combine(coat, "Main", "Coat_Main_Body_Normal.png"),
            $"the set with a diffuse is picked, and of its diffuses the one in the fewest words ({shown})");
    }

    /// <summary>
    /// A Unity package whose materials are named after the model's and more ("Thigh Highs", "UV1 Black Gold"),
    /// as VRChat creators name colour variants, with Substance Painter's texture names.
    /// </summary>
    private static void LookupByLongerNames(string root)
    {
        var stash = Path.Combine(root, "longer-stash");
        var outfit = Path.GetFullPath(Path.Combine(stash, "Yokai"));
        if (Directory.Exists(outfit))
            Directory.Delete(outfit, true);
        var manifest = new StashManifest { Source = @"C:\p\Yokai.unitypackage", SourceKind = "unity", ExtractedUtc = DateTime.UtcNow };
        foreach (var (path, purpose) in new[]
                 {
                     ("Thigh/Thigh_Base_color.png", TexturePurpose.Diffuse), ("Thigh/Thigh_Normal_OpenGL.png", TexturePurpose.Normal),
                     ("Thigh/Thigh_Metallic.png", TexturePurpose.Mask),
                     ("UV1/UV1_White.png", TexturePurpose.Diffuse), ("UV1/UV1_Black.png", TexturePurpose.Diffuse),
                     ("UV1/UV1_Normal2.png", TexturePurpose.Normal), ("UV1/UV1_AO.png", TexturePurpose.Occlusion),
                     ("UV10/UV10_Red.png", TexturePurpose.Diffuse), ("UV10/UV10_Normal.png", TexturePurpose.Normal), ("UV10/UV10_Mask.png", TexturePurpose.Mask),
                     ("b_Base_color.png", TexturePurpose.Diffuse), ("b_Normal_OpenGL.png", TexturePurpose.Normal),
                     ("Bikini/Bikini_Base_color.png", TexturePurpose.Diffuse),
                     ("Horns/Horns_Base_color.png", TexturePurpose.Diffuse), ("Horns/Horns_Alt_Base_color.png", TexturePurpose.Diffuse),
                     ("Horns/Horns_Alt_Normal.png", TexturePurpose.Normal),
                     ("Matcaps/Metal 1.png", TexturePurpose.Matcap), ("Metal/Metal_Base_color.png", TexturePurpose.Diffuse),
                 })
        {
            Touch(outfit, path);
            manifest.Entries.Add(new StashEntry { Path = path, Kind = StashKind.Texture, Purpose = purpose });
        }
        void Material(string name, params (TexturePurpose Purpose, string Path)[] textures)
            => manifest.Materials.Add(new StashMaterial { Name = name, Textures = textures.ToDictionary(t => t.Purpose, t => t.Path) });
        Material("Thigh Highs", (TexturePurpose.Diffuse, "Thigh/Thigh_Base_color.png"), (TexturePurpose.Normal, "Thigh/Thigh_Normal_OpenGL.png"));
        Material("UV10 Red", (TexturePurpose.Diffuse, "UV10/UV10_Red.png"), (TexturePurpose.Normal, "UV10/UV10_Normal.png"), (TexturePurpose.Mask, "UV10/UV10_Mask.png"));
        Material("UV1 1", (TexturePurpose.Diffuse, "UV1/UV1_White.png"), (TexturePurpose.Occlusion, "UV1/UV1_AO.png"));
        Material("UV1 Black Gold", (TexturePurpose.Diffuse, "UV1/UV1_Black.png"), (TexturePurpose.Normal, "UV1/UV1_Normal2.png"), (TexturePurpose.Occlusion, "UV1/UV1_AO.png"));
        Material("BIKINI", (TexturePurpose.Diffuse, "Bikini/Bikini_Base_color.png"));
        Material("Horns", (TexturePurpose.Diffuse, "Horns/Horns_Base_color.png"));
        Material("Horns Alt", (TexturePurpose.Diffuse, "Horns/Horns_Alt_Base_color.png"), (TexturePurpose.Normal, "Horns/Horns_Alt_Normal.png"));
        Material("Metal", (TexturePurpose.Matcap, "Matcaps/Metal 1.png"));
        Material("Metal Shiny", (TexturePurpose.Diffuse, "Metal/Metal_Base_color.png"));
        manifest.Write(outfit);

        var lookup = ModelTextureLookup.Open(SaveFromBlender(outfit, "Blender/Yokai.mdl", @"C:\elsewhere\Yokai.fbx"), new[] { stash }, Array.Empty<string>());
        string Texture(string relative) => Path.Combine(outfit, relative.Replace('/', Path.DirectorySeparatorChar));
        string Show(IReadOnlyDictionary<TexturePurpose, string> found) => string.Join(", ", found.Select(p => $"{p.Key}={Path.GetFileName(p.Value)}"));

        var thigh = lookup.TexturesFor("/Thigh.mtrl");
        Require(thigh.GetValueOrDefault(TexturePurpose.Diffuse) == Texture("Thigh/Thigh_Base_color.png")
                && thigh.GetValueOrDefault(TexturePurpose.Normal) == Texture("Thigh/Thigh_Normal_OpenGL.png"),
            $"a material named the model's and more is taken when none is named the same ({Show(thigh)})");
        Require(thigh.GetValueOrDefault(TexturePurpose.Mask) == Texture("Thigh/Thigh_Metallic.png"),
            $"and what it leaves out comes from the texture set its textures are in ({Show(thigh)})");

        var uv1 = lookup.TexturesFor("/UV1.mtrl");
        Require(uv1.GetValueOrDefault(TexturePurpose.Diffuse) == Texture("UV1/UV1_Black.png") && uv1.GetValueOrDefault(TexturePurpose.Normal) == Texture("UV1/UV1_Normal2.png"),
            $"of several, the one that fills the most slots, though another comes first; \"UV10 Red\" is not \"UV1\" and more ({Show(uv1)})");
        var b = lookup.TexturesFor("/b.mtrl");
        Require(b.GetValueOrDefault(TexturePurpose.Diffuse) == Texture("b_Base_color.png") && b.GetValueOrDefault(TexturePurpose.Normal) == Texture("b_Normal_OpenGL.png"),
            $"\"BIKINI\" doesn't start with the word \"b\": its texture set is found by words, Substance's suffixes and all ({Show(b)})");
        var horns = lookup.TexturesFor("/Horns.001.mtrl");
        Require(horns.Count == 1 && horns.GetValueOrDefault(TexturePurpose.Diffuse) == Texture("Horns/Horns_Base_color.png"),
            $"the material named the same comes before one named longer ({Show(horns)})");
        var metal = lookup.TexturesFor("/Metal.mtrl");
        Require(metal.GetValueOrDefault(TexturePurpose.Diffuse) == Texture("Metal/Metal_Base_color.png") && !metal.ContainsKey(TexturePurpose.Matcap),
            $"a material whose textures fill no slot (a matcap alone) is passed over ({Show(metal)})");
    }

    /// <summary>
    /// A model made in Blender whose materials are named for nothing in the folder ("4.0 Ribbon" on the Sin &amp; Tonic
    /// dress, whose textures are "ASD_…"), beside Substance Painter sets: the set whose ambient occlusion islands
    /// its UVs lie on is its set. Colour maps are painted everywhere, as Substance dilates them, and say nothing.
    /// </summary>
    private static void LookupByUvs(string root)
    {
        var folder = Path.GetFullPath(Path.Combine(root, "uv-opened"));
        if (Directory.Exists(folder))
            Directory.Delete(folder, true);
        PaintedImage(folder, "PBR/Dress_Base_color.png", 1, 0, 1, 2, 3);
        PaintedImage(folder, "PBR/Dress_Mixed_AO.png", 2, 0);
        PaintedImage(folder, "PBR/Dress_Opacity.png", 3, 0, 1, 2, 3);
        PaintedImage(folder, "PBR/Shoes_Base_color.png", 4, 0, 1, 2, 3);
        PaintedImage(folder, "PBR/Shoes_Mixed_AO.png", 5, 3);
        PaintedImage(folder, "PBR/Veil_Base_color.png", 6, 0, 1, 2, 3);
        PaintedImage(folder, "PBR/Veil_Roughness.png", 7, 1, 2);
        PaintedImage(folder, "PBR/Cape_Base_color.png", 8, 0, 1, 2, 3);
        PaintedImage(folder, "PBR/Cape_Mixed_AO.png", 9, 2);
        var model = Path.Combine(folder, "Outfit.mdl");
        File.WriteAllBytes(model, PainterScenarios.UvSquaresModel(
            ("/4.0 Ribbon.mtrl", 0.05f, 0.05f, 0.45f, 0.45f), ("/Material.mtrl", 0.55f, 1.55f, 0.95f, 1.95f),
            ("/Strap.mtrl", 0.55f, 0.05f, 0.95f, 0.45f), ("/Pin.mtrl", 0.05f, 0.55f, 0.45f, 0.95f), ("/Shoes.mtrl", 0.05f, 0.05f, 0.45f, 0.45f)));

        var lookup = ModelTextureLookup.Open(model, Array.Empty<string>(), new[] { folder });
        string Texture(string relative) => Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
        string Show(IReadOnlyDictionary<TexturePurpose, string> found) => string.Join(", ", found.Select(p => $"{p.Key}={Path.GetFileName(p.Value)}"));

        var ribbon = lookup.TexturesFor("/4.0 Ribbon.mtrl");
        Require(ribbon.GetValueOrDefault(TexturePurpose.Diffuse) == Texture("PBR/Dress_Base_color.png")
                && ribbon.GetValueOrDefault(TexturePurpose.Occlusion) == Texture("PBR/Dress_Mixed_AO.png"),
            $"a material whose name tells nothing gets the set whose islands its UVs lie on, though every colour map is painted there ({Show(ribbon)})");
        var material = lookup.TexturesFor("/Material.mtrl");
        Require(material.GetValueOrDefault(TexturePurpose.Diffuse) == Texture("PBR/Shoes_Base_color.png"),
            $"UVs outside the 0-1 square count where they wrap to ({Show(material)})");
        var strap = lookup.TexturesFor("/Strap.mtrl");
        Require(strap.GetValueOrDefault(TexturePurpose.Diffuse) == Texture("PBR/Veil_Base_color.png"),
            $"a roughness map shows the islands as well as an occlusion map ({Show(strap)})");
        Require(lookup.TexturesFor("/Pin.mtrl").Count == 0, "UVs that lie on two sets' islands alike get neither");
        Require(lookup.TexturesFor("/Shoes.mtrl").GetValueOrDefault(TexturePurpose.Diffuse) == Texture("PBR/Shoes_Base_color.png"),
            "a material's name comes before where its UVs lie");
        Require(lookup.TexturesFor("/Unknown.mtrl").Count == 0, "a material the model doesn't have finds nothing by UVs");
    }

    private static void StoppedReExtractKeepsTheEarlierOne(string root)
    {
        var output = Path.Combine(root, "stop-again-out");
        var package = BasicPackage(root, "keep");
        var scan = UnityPackageReader.Scan(package, _ => { }, CancellationToken.None);
        var first = UnityPackageReader.Extract(scan, output, _ => { }, CancellationToken.None);
        var before = Directory.GetFiles(first.RootFolder, "*", SearchOption.AllDirectories).Select(Path.GetFileName).Order().ToList();

        using var cts = new CancellationTokenSource();
        Exception? error = null;
        try { UnityPackageReader.Extract(scan, output, _ => cts.Cancel(), cts.Token); }
        catch (Exception ex) { error = ex; }
        var after = Directory.GetFiles(first.RootFolder, "*", SearchOption.AllDirectories).Select(Path.GetFileName).Order().ToList();
        Require(error is OperationCanceledException, $"the second run stops ({error?.GetType().Name})");
        Require(after.SequenceEqual(before), $"stopping a re-extraction keeps every file of the earlier one ({string.Join(", ", after)})");
        Require(Directory.GetDirectories(output).Select(Path.GetFileName).SequenceEqual(new[] { "keep" }),
            "no half-written or set-aside folder is left beside it");

        var again = UnityPackageReader.Extract(scan, output, _ => { }, CancellationToken.None);
        Require(again.RootFolder == first.RootFolder && Directory.GetDirectories(output).Length == 1,
            "a finished re-extraction still replaces the earlier one in its folder");
    }

    private static void StoppedFirstExtractLeavesNothing(string root)
    {
        var output = Path.Combine(root, "stop-first-out");
        var scan = UnityPackageReader.Scan(BasicPackage(root, "never"), _ => { }, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        try { UnityPackageReader.Extract(scan, output, _ => cts.Cancel(), cts.Token); }
        catch (OperationCanceledException) { }
        Require(!Directory.Exists(Path.Combine(output, "never")), "a stopped first extraction leaves no half-written folder");
    }

    private static void TypedFolderIsListedOnceSettled()
    {
        var settle = new SettledInput(TimeSpan.FromMilliseconds(400));
        var t0 = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
        Require(settle.Due(@"C:\a", t0), "the first value is acted on at once");
        Require(!settle.Due(@"C:\a", t0.AddSeconds(5)), "an unchanged value isn't acted on again");
        Require(!settle.Due(@"C:\ab", t0.AddSeconds(6)) && !settle.Due(@"C:\abc", t0.AddSeconds(6.1)) && !settle.Due(@"C:\abc", t0.AddSeconds(6.3)),
            "a folder being typed isn't listed on every keystroke");
        Require(settle.Due(@"C:\abc", t0.AddSeconds(6.6)) && !settle.Due(@"C:\abc", t0.AddSeconds(7)),
            "once typing has stopped for the delay it is listed, once");
        settle.Invalidate();
        Require(settle.Due(@"C:\abc", t0.AddSeconds(7.1)), "asked to list again, it does so at once");
    }

    private static void OpenedFolderReadsUnityMetadata(string root)
    {
        // A VRChat avatar's raw resources: textures and materials with Unity's .meta files beside them.
        var folder = Path.Combine(root, "raw-avatar");
        Directory.CreateDirectory(Path.Combine(folder, "Textures"));
        Directory.CreateDirectory(Path.Combine(folder, "Materials"));
        Directory.CreateDirectory(Path.Combine(folder, "FBX"));
        Directory.CreateDirectory(Path.Combine(folder, ".git"));
        File.WriteAllBytes(Path.Combine(folder, "Textures", "body_d.png"), PngBytes);
        File.WriteAllText(Path.Combine(folder, "Textures", "body_d.png.meta"), TextureMeta(GuidTex, 0));
        File.WriteAllBytes(Path.Combine(folder, "Textures", "tex2.png"), PngBytes);
        File.WriteAllText(Path.Combine(folder, "Textures", "tex2.png.meta"), TextureMeta(GuidNormal, 1));
        File.WriteAllText(Path.Combine(folder, "Materials", "skin.mat"), MaterialText("Skin", GuidTex, GuidNormal));
        File.WriteAllText(Path.Combine(folder, "Materials", "skin.mat.meta"), $"fileFormatVersion: 2\nguid: {GuidMat}\nNativeFormatImporter:\n");
        File.WriteAllText(Path.Combine(folder, "Outfit.prefab"), $"      objectReference: {{fileID: 2100000, guid: {GuidMat}, type: 2}}\n");
        File.WriteAllBytes(Path.Combine(folder, "FBX", "m.fbx"), new byte[] { 9 });
        File.WriteAllBytes(Path.Combine(folder, ".git", "x.png"), PngBytes);

        var manifest = StashManifest.Scan(folder);
        Require(manifest.Entries.All(e => !e.Path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)), "Unity's .meta files are read, not listed");
        Require(manifest.Entries.All(e => !e.Path.StartsWith(".git", StringComparison.Ordinal)), "hidden folders are left out");
        var normal = manifest.Entries.FirstOrDefault(e => e.Path == "Textures/tex2.png");
        Require(normal is { Purpose: TexturePurpose.Normal }, $"a texture a material uses as its bump map is a normal map, whatever its name ({normal?.Purpose})");
        Require(normal!.Groups.Contains("Skin") && normal.Groups.Contains("Outfit"), "a texture is grouped under its material and the prefab using it");
        var skin = manifest.Materials.SingleOrDefault(m => m.Name == "Skin");
        Require(skin != null && skin.Textures[TexturePurpose.Diffuse] == "Textures/body_d.png" && skin.Textures[TexturePurpose.Normal] == "Textures/tex2.png",
            "a folder's Unity materials are listed with their textures by purpose");
        Require(manifest.Entries.Any(e => e.Path == "FBX/m.fbx" && e.NeedsConversion), "its models are listed too");
    }

    private static void OpenedFolderNamesModelMaterials(string root)
    {
        var folder = Path.Combine(root, "raw-remap");
        Directory.CreateDirectory(Path.Combine(folder, "FBX"));
        Directory.CreateDirectory(Path.Combine(folder, "Materials"));
        // An avatar's .fbx meta runs long: its rig follows the remap.
        var meta = Path.Combine(folder, "FBX", "Model.fbx.meta");
        File.WriteAllBytes(Path.Combine(folder, "FBX", "Model.fbx"), new byte[] { 9 });
        File.WriteAllText(meta, ModelMeta(GuidModel, ("Body", GuidMat), ("Hair", "66666666666666666666666666666666")) + Rig(600));
        File.WriteAllBytes(Path.Combine(folder, "FBX", "Plain.fbx"), new byte[] { 8 });
        File.WriteAllText(Path.Combine(folder, "Materials", "skin.mat"), MaterialText("Skin", GuidTex, GuidNormal));
        File.WriteAllText(Path.Combine(folder, "Materials", "skin.mat.meta"), $"fileFormatVersion: 2\nguid: {GuidMat}\nNativeFormatImporter:\n");

        var manifest = StashManifest.Scan(folder);
        var model = manifest.Entries.FirstOrDefault(e => e.Path == "FBX/Model.fbx");
        Require(new FileInfo(meta).Length > 64 * 1024 && model?.MaterialNames is { Count: 1 } names && names.GetValueOrDefault("Body") == "Skin",
            $"an opened folder's model names the Unity material each of its materials maps to, found through the .mat's meta, however long its own meta runs; a GUID the folder lacks is dropped ({Show(model?.MaterialNames)})");
        Require(manifest.Entries.Where(e => e != model).All(e => e.MaterialNames == null), "a model without a meta, and every other file, names none");
    }

    private static void FolderScanStopsAtItsCap(string root)
    {
        var folder = Path.Combine(root, "big-folder");
        Directory.CreateDirectory(folder);
        for (int i = 0; i < 5; i++)
            File.WriteAllBytes(Path.Combine(folder, $"t{i}.png"), PngBytes);
        var capped = StashManifest.Scan(folder, maxFiles: 3);
        Require(capped.Entries.Count == 3 && capped.Truncated, "a folder scan stops at its cap and says so");
        Require(!StashManifest.Scan(folder).Truncated, "a folder under the cap is listed whole");

        // The folders above an opened folder say nothing about its files.
        var above = Path.Combine(root, "Screenshots", "Matcap_Test");
        Directory.CreateDirectory(above);
        File.WriteAllBytes(Path.Combine(above, "Body.png"), PngBytes);
        File.WriteAllBytes(Path.Combine(above, "Body_Normal.png"), PngBytes);
        var opened = StashManifest.Scan(above);
        Require(opened.Entries.FirstOrDefault(e => e.Path == "Body.png")?.Purpose == TexturePurpose.Diffuse,
            "a texture is classified by the names from the opened folder down, not by the folders it sits under (\"Matcap_Test\", \"Screenshots\")");
    }

    private static void OpenedFoldersAreRememberedNotDeleted(string root)
    {
        var folder = Directory.CreateDirectory(Path.Combine(root, "opened", "Avatar Files")).FullName;
        File.WriteAllBytes(Path.Combine(folder, "a.png"), PngBytes);
        var saved = new List<string>();
        Require(StashFolders.AddOpened(saved, folder + Path.DirectorySeparatorChar) == null && saved.SequenceEqual(new[] { folder }),
            "an opened folder is remembered by its full path");
        Require(StashFolders.AddOpened(saved, folder.ToUpperInvariant()) == null && saved.Count == 1, "opening it again keeps one entry");
        Require(StashFolders.AddOpened(saved, Path.Combine(root, "opened", "missing")) != null && saved.Count == 1, "a folder that isn't there is refused");
        Require(StashFolders.AddOpened(saved, Path.GetPathRoot(folder)!) != null, "a whole drive is refused");

        var gone = Path.Combine(root, "opened", "gone");
        saved.Add(gone);
        Require(StashFolders.Opened(saved).SequenceEqual(new[] { folder }), "opened folders that are gone aren't listed");
        StashFolders.Forget(saved, folder);
        Require(!saved.Contains(folder, StringComparer.OrdinalIgnoreCase) && File.Exists(Path.Combine(folder, "a.png")),
            "taking a folder out of the stash forgets it and never deletes its files");
    }

    private static void ExtractionsAreListedNewestFirst(string root)
    {
        var output = Path.Combine(root, "listing");
        var older = Directory.CreateDirectory(Path.Combine(output, "older")).FullName;
        var newer = Directory.CreateDirectory(Path.Combine(output, "newer")).FullName;
        Directory.CreateDirectory(Path.Combine(output, "empty"));
        var user = Directory.CreateDirectory(Path.Combine(output, "my project")).FullName;
        File.WriteAllBytes(Path.Combine(older, "a.png"), PngBytes);
        File.WriteAllText(Path.Combine(older, "extract-report.txt"), "report");
        File.WriteAllBytes(Path.Combine(newer, "b.png"), PngBytes);
        File.WriteAllBytes(Path.Combine(newer, "c.png"), PngBytes);
        File.WriteAllText(Path.Combine(newer, "notes.txt"), "x");
        File.WriteAllText(Path.Combine(newer, "stash.json"), "{}");
        File.WriteAllBytes(Path.Combine(user, "texture.png"), PngBytes);
        var aside = Directory.CreateDirectory(Path.Combine(output, "newer.previous")).FullName;
        File.WriteAllText(Path.Combine(aside, "stash.json"), "{}");
        Directory.SetLastWriteTimeUtc(older, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Directory.SetLastWriteTimeUtc(newer, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        var listed = StashFolders.ListExtractions(output);
        Require(listed.Select(Path.GetFileName).SequenceEqual(new[] { "newer", "older" }),
            $"extractions are listed newest first; empty folders, folders no extraction made and one set aside during a re-extraction are left out ({string.Join(", ", listed.Select(Path.GetFileName))})");
        Require(StashFolders.CountImages(newer) == 2, "a folder's images are counted, other files not");
        Require(StashFolders.ListExtractions(Path.Combine(root, "missing")).Count == 0, "a missing output folder lists nothing");
    }

    private static void RemoveStaysInsideTheOutputFolder(string root)
    {
        var output = Path.Combine(root, "removal");
        var inside = Directory.CreateDirectory(Path.Combine(output, "pkg")).FullName;
        File.WriteAllBytes(Path.Combine(inside, "a.png"), PngBytes);
        File.WriteAllText(Path.Combine(inside, "stash.json"), "{}");
        var outside = Directory.CreateDirectory(Path.Combine(root, "removal-sibling")).FullName;
        File.WriteAllText(Path.Combine(outside, "stash.json"), "{}");
        var foreign = Directory.CreateDirectory(Path.Combine(output, "my project")).FullName;
        File.WriteAllBytes(Path.Combine(foreign, "a.png"), PngBytes);

        Require(StashFolders.Remove(output, foreign) != null && Directory.Exists(foreign),
            "a folder inside the output folder that no extraction made is never removed");
        Require(StashFolders.Remove(output, outside) != null && Directory.Exists(outside), "a folder outside the output folder is never removed");
        Require(StashFolders.Remove(output, output) != null && Directory.Exists(output), "the output folder itself is never removed");
        Require(StashFolders.Remove(output, inside) == null && !Directory.Exists(inside), "an extraction inside the output folder is removed");
    }

    private static void StashApplyMapsPurposes(string root)
    {
        var folder = Path.Combine(root, "apply");
        Directory.CreateDirectory(Path.Combine(folder, "swatches"));
        var textures = new Dictionary<TexturePurpose, string>
        {
            [TexturePurpose.Diffuse] = Path.Combine(folder, "d.png"), [TexturePurpose.Normal] = Path.Combine(folder, "n.png"),
            [TexturePurpose.Mask] = Path.Combine(folder, "m.png"), [TexturePurpose.Occlusion] = Path.Combine(folder, "ao.png"),
        };
        var withMask = StashApply.Plan(textures, new[] { TextureType.Diffuse, TextureType.Normal, TextureType.Mask });
        Require(withMask.Select(r => r.Type).SequenceEqual(new[] { TextureType.Diffuse, TextureType.Normal, TextureType.Mask }),
            "diffuse, normal and mask go to their own slots; occlusion has none");
        Require(withMask.All(r => !r.Folder) && withMask[1].Path == textures[TexturePurpose.Normal], "single files go in as single textures");

        var noMask = StashApply.Plan(textures, new[] { TextureType.Diffuse, TextureType.Specular });
        Require(noMask.Any(r => r.Type == TextureType.Specular && r.Path == textures[TexturePurpose.Mask]) && noMask.All(r => r.Type != TextureType.Mask),
            "a mask goes to the specular slot of a material without a mask slot");

        var folderDiffuse = StashApply.Plan(new Dictionary<TexturePurpose, string> { [TexturePurpose.Diffuse] = Path.Combine(folder, "swatches") },
            new[] { TextureType.Diffuse });
        Require(folderDiffuse.Single() is { Type: TextureType.Diffuse, Folder: true }, "a folder of diffuse swatches goes in as variants");
    }

    private static void StashApplyFillsSlots(string root)
    {
        var folder = Path.Combine(root, "apply-fill");
        var diffuse = Touch(folder, "d.png");
        Touch(folder, "swatches/red.png");
        Touch(folder, "swatches/blue.png");
        var material = MaterialSetup.FromPreset(MaterialPresets.All.First(p => p.Name == "Gear"), "mt_test_a");
        var mask = material.Textures.Single(t => t.Type == TextureType.Mask);
        mask.UseWhiteDummy = true;

        var applied = StashApply.Fill(material, new[]
        {
            (TextureType.Diffuse, diffuse, false), (TextureType.Normal, Path.Combine(folder, "missing.png"), false),
            (TextureType.Mask, Path.Combine(folder, "swatches"), true),
        });
        Require(applied.SequenceEqual(new[] { "diffuse", "mask / multiply (2 variant(s))" }),
            $"what went in is listed for the message; a file that isn't there is skipped ({string.Join(", ", applied)})");
        var added = material.Textures.Last();
        Require(added is { Type: TextureType.Diffuse, Compression: TextureCompression.Auto, UseVariants: false } && added.SourcePath == diffuse
                && added.Postfix == MaterialNaming.DefaultPostfix(TextureType.Diffuse),
            "a slot the material lacks is added, on Auto with the role's postfix");
        Require(material.Textures.Single(t => t.Type == TextureType.Normal).SourcePath.Length == 0, "the slot of a skipped file is left as it was");
        Require(mask is { UseVariants: true, UseWhiteDummy: false } && mask.SourcePath == Path.Combine(folder, "swatches"),
            "a folder goes in as variants, and a placeholder gives way to the file");
    }

    /// <summary>Without .mtrl files, a model is on the item its materials' telling words name.</summary>
    private static void ItemsAreOnTheModelsTheirNamesTell(string root)
    {
        var folder = Path.Combine(root, "items-models");
        if (Directory.Exists(folder))
            Directory.Delete(folder, true);
        foreach (var name in new[] { "Outfit_Shirt.png", "Outfit_Shirt_Normal.png", "Outfit_Gloves.png", "Outfit_Gloves_Normal.png" })
            Touch(folder, name);
        void Model(string name, params string[] materials)
        {
            Directory.CreateDirectory(Path.Combine(folder, "Models"));
            File.WriteAllBytes(Path.Combine(folder, "Models", name), ModCheckFixtures.Mdl(materials));
        }
        Model("Top.mdl", "/Outfit_Shirt.mtrl", "/bibo.mtrl");
        Model("Hands.mdl", "/mt_gloves.mtrl");
        Model("Other.mdl", "/Outfit.mtrl");

        var items = TextureItems.Build(StashManifest.Scan(folder), folder, layout: false);
        string Models(string item) => string.Join(", ", items.Items.Single(i => i.Name == item).Models);
        Require(Models("Shirt") == "Top.mdl" && Models("Gloves") == "Hands.mdl",
            $"a model is on the item whose texture names hold its material's telling words, \"mt\" and the outfit's shared words aside (shirt: {Models("Shirt")}; gloves: {Models("Gloves")})");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Purposes and the stash manifest
    // ─────────────────────────────────────────────────────────────────────────

    private static void PurposeFromUnityProperties()
    {
        Require(TexturePurposes.FromUnityProperty("_MainTex") == TexturePurpose.Diffuse, "_MainTex is a diffuse");
        Require(TexturePurposes.FromUnityProperty("_BaseMap") == TexturePurpose.Diffuse, "_BaseMap is a diffuse");
        Require(TexturePurposes.FromUnityProperty("_BumpMap") == TexturePurpose.Normal, "_BumpMap is a normal map");
        Require(TexturePurposes.FromUnityProperty("_MetallicGlossMap") == TexturePurpose.Mask, "_MetallicGlossMap is a mask");
        Require(TexturePurposes.FromUnityProperty("_MaskMap") == TexturePurpose.Mask, "_MaskMap is a mask");
        Require(TexturePurposes.FromUnityProperty("_OcclusionMap") == TexturePurpose.Occlusion, "_OcclusionMap is occlusion");
        Require(TexturePurposes.FromUnityProperty("_EmissionMap") == TexturePurpose.Emission, "_EmissionMap is emission");
        Require(TexturePurposes.FromUnityProperty("_Foo") == null, "an unknown property says nothing");
        Require(TexturePurposes.FromUnityProperty("_MatCapTex") == TexturePurpose.Matcap && TexturePurposes.FromUnityProperty("_MatCap2ndTex") == TexturePurpose.Matcap
                && TexturePurposes.FromUnityProperty("_Matcap") == TexturePurpose.Matcap, "lilToon's and Poiyomi's matcap slots hold matcaps");
        Require(TexturePurposes.FromUnityProperty("_ShadowBorderMask") == TexturePurpose.Occlusion, "lilToon's AO map is occlusion");
        Require(TexturePurposes.FromUnityProperty("_Main2ndBlendMask") == null, "a layer's blend mask is not the material's mask");
        Require(TexturePurposes.MostCommon(new TexturePurpose?[] { null, TexturePurpose.Matcap, null, null }) == TexturePurpose.Matcap,
            "a texture's one telling use speaks for it");
        Require(TexturePurposes.MostCommon(new TexturePurpose?[] { TexturePurpose.Matcap, TexturePurpose.Diffuse, TexturePurpose.Diffuse }) == TexturePurpose.Diffuse,
            "the purpose most of its uses give it wins, whatever comes first");
        Require(TexturePurposes.MostCommon(new TexturePurpose?[] { null }) == null, "uses that say nothing give nothing");
    }

    private static void PurposeFromFileNames()
    {
        (string Name, TexturePurpose Expected)[] cases =
        {
            ("UV1_Normal.png", TexturePurpose.Normal), ("body_n.png", TexturePurpose.Normal), ("UV1_Normal2.png", TexturePurpose.Normal),
            ("UV1_AO.png", TexturePurpose.Occlusion), ("Glow_Emissive.png", TexturePurpose.Emission),
            ("skin_mask.png", TexturePurpose.Mask), ("Z.UV1_Pattern mask.png", TexturePurpose.Mask),
            ("skin_d.png", TexturePurpose.Diffuse), ("Albedo.png", TexturePurpose.Diffuse),
            ("UV1_Black.png", TexturePurpose.Other),
            ("0a51e805-77cf-4516-bc7f-e8b980857650_base_resized.jpg", TexturePurpose.Other), ("Outfit_Preview.png", TexturePurpose.Other),
            (Path.Combine("Thumbnails", "body_d.png"), TexturePurpose.Other),
            ("Hair_MatCap.png", TexturePurpose.Matcap), ("matcap2.png", TexturePurpose.Matcap),
            (Path.Combine("09. Matcap", "LKVK_Metal 1.png"), TexturePurpose.Matcap), (Path.Combine("09. Matcap", "LKVK_Metal_Normal.png"), TexturePurpose.Normal),
        };
        foreach (var (name, expected) in cases)
            Require(TexturePurposes.FromFileName(name) == expected, $"{name} is read as {expected} (got {TexturePurposes.FromFileName(name)})");

        PurposeFromSiblings();
    }

    /// <summary>A VRChat outfit's loose texture folder (Sera Yura by LookVook): diffuses only named by their siblings, matcaps only by their looks.</summary>
    private static void PurposeFromSiblings()
    {
        var folder = Path.Combine(Path.GetTempPath(), "sera");
        (string Name, TexturePurpose Expected)[] cases =
        {
            ("0a51e805-77cf-4516-bc7f-e8b980857650_base_resized.jpg", TexturePurpose.Other),
            ("Sera_Yura_Bikini.png", TexturePurpose.Diffuse), ("Sera_Yura_Bikini_AO.png", TexturePurpose.Occlusion),
            ("Sera_Yura_Bikini_Mask.png", TexturePurpose.Mask), ("Sera_Yura_Bikini_Normal.png", TexturePurpose.Normal),
            ("Sera_Yura_Skirt.png", TexturePurpose.Diffuse), ("Sera_Yura_Skirt_Mixed_AO.png", TexturePurpose.Occlusion),
            ("Sera_Yura_Skirt_Normal.png", TexturePurpose.Normal),
            ("Sera_Yura_Shirt.png", TexturePurpose.Diffuse), ("Sera_Yura_Shirt_Alpha.png", TexturePurpose.Diffuse),
            ("Sera_Yura_Shirt_AO.png", TexturePurpose.Occlusion), ("Sera_Yura_Shirt_Normal.png", TexturePurpose.Normal),
            ("Sera_Yura_Elbow_Glove&Stocking.png", TexturePurpose.Diffuse), ("Sera_Yura_Elbow_Glove&Stocking_Latex.png", TexturePurpose.Diffuse),
            ("Sera_Yura_Elbow_Glove&Stocking_Cow.png", TexturePurpose.Diffuse), ("Sera_Yura_Elbow_Glove&Stocking_Mask2.png", TexturePurpose.Mask),
            ("Sera_Yura_Elbow_Glove&Stocking_Latex_Normal.png", TexturePurpose.Normal), ("Sera_Yura_Elbow_Glove&Stocking_Normal.png", TexturePurpose.Normal),
            ("Sera_Yura_Bell_Choker.png", TexturePurpose.Other), ("Sera_Yura_Logo.png", TexturePurpose.Other),
            ("LKVK_Gloss 1.png", TexturePurpose.Matcap), ("LKVK_Gloss 2.png", TexturePurpose.Matcap), ("LKVK_Glow 1.png", TexturePurpose.Matcap),
            ("LKVK_Metal 1.png", TexturePurpose.Matcap), ("LKVK_Metal_Normal.png", TexturePurpose.Normal),
            ("LKVK_Hologram 1.png", TexturePurpose.Matcap), ("LKVK_Latex.png", TexturePurpose.Matcap), ("LKVK_Light.png", TexturePurpose.Matcap),
            ("LKVK_Can_Gold.png", TexturePurpose.Matcap),
            ("LKVK_Check.png", TexturePurpose.Other), ("LKVK_Cow.png", TexturePurpose.Other), ("LV-017.png", TexturePurpose.Other),
            // A gloss map beside its diffuse keeps its role.
            ("Hair_Diffuse.png", TexturePurpose.Diffuse), ("Hair_Gloss.png", TexturePurpose.Mask), ("Hair_Glow.png", TexturePurpose.Emission),
        };
        var read = TexturePurposes.FromFileNames(cases.Select(c => Path.Combine(folder, c.Name)));
        foreach (var (name, expected) in cases)
        {
            var got = read[Path.Combine(folder, name)];
            Require(got == expected, $"beside its folder, {name} is read as {expected} (got {got})");
        }

        var alone = TexturePurposes.FromFileNames(new[] { Path.Combine(folder, "Body.png"), Path.Combine(folder, "Other", "Body_Normal.png") });
        Require(alone[Path.Combine(folder, "Body.png")] == TexturePurpose.Other, "only files in the same folder make a set");

        var manifest = new StashManifest();
        foreach (var (path, purpose) in new[]
                 {
                     ("Body.png", TexturePurpose.Diffuse), ("Body_Normal.png", TexturePurpose.Normal), ("Shirt.png", TexturePurpose.Diffuse),
                     ("swatches/red.png", TexturePurpose.Diffuse), ("swatches/blue.png", TexturePurpose.Diffuse),
                 })
            manifest.Entries.Add(new StashEntry { Path = path, Kind = StashKind.Texture, Purpose = purpose });
        manifest.Entries.Add(new StashEntry { Path = "swatches/readme.txt", Kind = StashKind.Other });
        Require(!manifest.HoldsOnlyDiffuses(""), "a folder with a normal map beside its diffuses is a texture set, not a swatch folder");
        Require(manifest.HoldsOnlyDiffuses("swatches") && manifest.HoldsOnlyDiffuses("SWATCHES"),
            "a folder of only diffuses is a swatch folder, whatever else that is not a texture sits in it");
    }

    private static void SetNamesFollowTextureSets()
    {
        var folder = Path.Combine(Path.GetTempPath(), "sets");
        (string Name, string Set)[] cases =
        {
            ("Sera_Yura_Bikini.png", "Sera_Yura_Bikini"), ("Sera_Yura_Bikini_AO.png", "Sera_Yura_Bikini"),
            ("Sera_Yura_Elbow_Glove&Stocking.png", "Sera_Yura_Elbow_Glove&Stocking"),
            ("Sera_Yura_Elbow_Glove&Stocking_Latex_Normal.png", "Sera_Yura_Elbow_Glove&Stocking"),
            ("Sera_Yura_Elbow_Glove&Stocking_Mask2.png", "Sera_Yura_Elbow_Glove&Stocking"),
            ("Sera_Yura_Bell_L.png", "Sera_Yura_Bell_L"), ("Sera_Yura_Bell_R.png", "Sera_Yura_Bell_R"),
            ("baroque_top_id.tex", "baroque_top"), ("baroque_top_m.tex", "baroque_top"), ("baroque_top_n.tex", "baroque_top"),
            ("baroque_glove_n.tex", "baroque_glove"), ("Hair_Diffuse.png", "Hair"), ("Hair_Gloss.png", "Hair"),
            ("LKVK_Metal 1.png", "LKVK_Metal 1"),
            // Substance Painter's export names.
            ("ASA_Base_color.png", "ASA"), ("ASA_Normal_OpenGL.png", "ASA"), ("ASA_Normal_DirectX.png", "ASA"), ("ASA_Mixed_AO.png", "ASA"),
            ("ASA_Metallic.png", "ASA"), ("ASA_Height.png", "ASA"),
        };
        var sets = TexturePurposes.SetNames(cases.Select(c => Path.Combine(folder, c.Name)));
        foreach (var (name, set) in cases)
        {
            var got = sets[Path.Combine(folder, name)];
            Require(got == set, $"{name} is in the set {set} (got {got})");
        }
    }

    /// <summary>A loose folder: two items by name, a texture without a telling name that shares one's layout, a matcap, a preview and a logo.</summary>
    private static void TexturesAreSortedIntoItems(string root)
    {
        var folder = Path.Combine(root, "items");
        if (Directory.Exists(folder))
            Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        // Painted where an item's UV islands are, empty elsewhere.
        void Image(string name, int x0, int y0, int seed)
        {
            var random = new Random(seed);
            using var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(256, 256);
            for (int y = y0; y < y0 + 128; y++)
                for (int x = x0; x < x0 + 128; x++)
                    image[x, y] = new SixLabors.ImageSharp.PixelFormats.Rgba32((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), 255);
            SixLabors.ImageSharp.ImageExtensions.SaveAsPng(image, Path.Combine(folder, name));
        }
        Image("Outfit_Shirt.png", 0, 0, 1);
        Image("Outfit_Shirt_Normal.png", 0, 0, 2);
        Image("Outfit_Shirt_AO.png", 0, 0, 3);
        Image("Outfit_Gloves.png", 128, 128, 4);
        Image("Outfit_Gloves_Normal.png", 128, 128, 5);
        Image("tex9.png", 0, 0, 6);
        Image("Logo.png", 0, 128, 7);
        Image("LKVK_Gloss 1.png", 64, 64, 8);
        Image("0a51e805-77cf-4516-bc7f-e8b980857650_base_resized.png", 0, 0, 9);

        var manifest = StashManifest.Scan(folder);
        var items = TextureItems.Build(manifest, folder);
        Require(items.Items.Select(i => i.Name).SequenceEqual(new[] { "Gloves", "Shirt" }),
            $"texture sets are items, named without the start they all share (got {string.Join(", ", items.Items.Select(i => i.Name))})");
        var shirt = items.Items.Last();
        Require(shirt.Textures.Select(t => t.Path).SequenceEqual(new[] { "Outfit_Shirt.png", "Outfit_Shirt_Normal.png", "Outfit_Shirt_AO.png", "tex9.png" }),
            $"an item lists its textures by purpose; a texture painted where the item is joins it (got {string.Join(", ", shirt.Textures.Select(t => t.Path))})");
        Require(shirt.ByLayout.Select(t => t.Path).SequenceEqual(new[] { "tex9.png" }), "a texture that joined by its layout says so");
        Require(items.Matcaps.Select(t => t.Path).SequenceEqual(new[] { "LKVK_Gloss 1.png" }), "matcaps are listed apart from the items");
        Require(items.Loose.Select(t => t.Path).Order().SequenceEqual(new[] { "0a51e805-77cf-4516-bc7f-e8b980857650_base_resized.png", "Logo.png" }),
            "a texture painted elsewhere, and a store picture whatever its layout, belong to no item");

        var byName = TextureItems.Build(manifest, folder, layout: false);
        Require(byName.Loose.Any(t => t.Path == "tex9.png") && byName.Items.All(i => i.ByLayout.Count == 0), "without the layout pass, only names group");
    }

    /// <summary>An image painted with noise in the given quadrants of a 256 by 256 square (0 top left, 1 top right, 2 bottom left, 3 bottom right).</summary>
    private static void PaintedImage(string folder, string name, int seed, params int[] quadrants)
    {
        var random = new Random(seed);
        var path = Path.Combine(folder, name.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(256, 256);
        foreach (var quadrant in quadrants)
            for (int y = quadrant / 2 * 128; y < quadrant / 2 * 128 + 128; y++)
                for (int x = quadrant % 2 * 128; x < quadrant % 2 * 128 + 128; x++)
                    image[x, y] = new SixLabors.ImageSharp.PixelFormats.Rgba32((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), 255);
        SixLabors.ImageSharp.ImageExtensions.SaveAsPng(image, path);
    }

    /// <summary>
    /// Substance Painter's sets: normal maps painted nearly all over, alike in every set, so a colour variant in
    /// another folder is held against the sets' diffuses. The same set in two folders is alike in both.
    /// </summary>
    private static void LayoutComparesLikeWithLike(string root)
    {
        var folder = Path.Combine(root, "items-substance");
        if (Directory.Exists(folder))
            Directory.Delete(folder, true);
        PaintedImage(folder, "PBR/Shirt_Base_color.png", 1, 0);
        PaintedImage(folder, "PBR/Shirt_Normal_OpenGL.png", 2, 0, 1, 3);
        PaintedImage(folder, "PBR/Shirt_Mixed_AO.png", 3, 0);
        PaintedImage(folder, "PBR/Gloves_Base_color.png", 4, 3);
        PaintedImage(folder, "PBR/Gloves_Normal_OpenGL.png", 5, 0, 1, 3);
        PaintedImage(folder, "PBR/Alt/Shirt_Base_color.png", 6, 0);
        PaintedImage(folder, "PBR/Alt/Shirt_Normal_OpenGL.png", 7, 0, 1, 3);
        PaintedImage(folder, "PBR/Red/Shirt_Base_color.png", 8, 0);
        PaintedImage(folder, "PBR/Red/Gloves_Base_color.png", 9, 3);

        var items = TextureItems.Build(StashManifest.Scan(folder), folder);
        string Show() => string.Join("; ", items.Items.Select(i => $"{i.Name}: {string.Join(", ", i.Textures.Select(t => t.Path))}"));
        var shirt = items.Items.FirstOrDefault(i => i.Textures.Any(t => t.Path == "PBR/Shirt_Base_color.png"));
        Require(shirt != null && shirt.Textures.Any(t => t.Path == "PBR/Shirt_Normal_OpenGL.png") && shirt.Textures.Any(t => t.Path == "PBR/Shirt_Mixed_AO.png")
                && shirt.ByLayout.Count(t => !t.Path.StartsWith("PBR/Red/", StringComparison.Ordinal)) == 0,
            $"Substance's _Normal_OpenGL and _Mixed_AO are in their set by name ({Show()})");
        var red = items.Items.Where(i => i.ByLayout.Any(t => t.Path.StartsWith("PBR/Red/", StringComparison.Ordinal))).ToList();
        Require(red.Count == 2 && red.Single(i => i.ByLayout.Any(t => t.Path == "PBR/Red/Shirt_Base_color.png")).Name == "Shirt"
                && red.Single(i => i.ByLayout.Any(t => t.Path == "PBR/Red/Gloves_Base_color.png")).Name == "Gloves" && items.Loose.Count == 0,
            $"a colour variant joins the set whose diffuse it is painted like, though every normal map looks alike, and though its set is in two folders ({Show()})");
    }

    /// <summary>The words of material and texture names keep their digits where some item's names have them.</summary>
    private static void ItemNamesKeepTheirDigits()
    {
        Require(TexturePurposes.Words("UV1_Normal2", keepDigits: true).SequenceEqual(new[] { "uv1", "normal2" })
                && TexturePurposes.Words("Dress.001", keepDigits: true).SequenceEqual(new[] { "dress" })
                && TexturePurposes.Words("UV1_Normal2").SequenceEqual(new[] { "uv", "normal" }),
            "kept, a word's digits stay after its letters; a word of digits alone is still dropped");

        TextureItem Item(string name, params string[] textures)
        {
            var item = new TextureItem { Name = name };
            item.Textures.AddRange(textures.Select(t => new StashEntry { Path = t, Kind = StashKind.Texture }));
            return item;
        }
        var uv1 = Item("UV1", "UV 1/UV1_Normal.png", "UV 1/UV1_AO.png");
        var uv2 = Item("UV2", "UV 2/UV2_Black.png", "UV 2/UV2_Normal.png");
        var uv3 = Item("UV3", "UV 3/UV3_Mask.png", "UV 3/UV3_AO.png");
        var top = Item("Top", "Top_Base_color.png", "Top_Normal.png");
        var stray = new StashEntry { Path = "UV 3/UV3_Normal_2 2.png", Kind = StashKind.Texture };
        uv2.Textures.Add(stray);
        uv2.ByLayout.Add(stray);
        var names = new TextureItemNames(new[] { uv1, uv2, uv3, top });
        Require(names.Names("UV1", uv1) && !names.Names("UV1", uv2) && names.Names("UV2.001", uv2) && !names.Names("UV2", uv1),
            "\"UV1\" names the UV1 set only, not every set with \"uv\" in its names");
        Require(names.Names("UV3", uv3) && !names.Names("UV3", uv2), "a texture that joined a set by its layout doesn't name it");
        Require(names.Names("Top2", top), "a word whose digits no set's names have is read without them");
    }

    /// <summary>
    /// A Unity package's colour variants: each material pulls the loose diffuse it uses into the texture set it
    /// shares, and two loose textures one material uses don't make a set of their own.
    /// </summary>
    private static void UnityMaterialsPullLooseTexturesIntoTheirSet(string root)
    {
        var folder = Path.GetFullPath(Path.Combine(root, "items-unity"));
        if (Directory.Exists(folder))
            Directory.Delete(folder, true);
        var manifest = new StashManifest { Source = @"C:\p\Kaihua.unitypackage", SourceKind = "unity", ExtractedUtc = DateTime.UtcNow };
        foreach (var (path, purpose) in new[]
                 {
                     ("UV 1/AO/UV1_Normal2.png", TexturePurpose.Normal), ("UV 1/AO/UV1_AO.png", TexturePurpose.Occlusion),
                     ("UV 1/UV1_Black.png", TexturePurpose.Diffuse), ("UV 1/UV2_Pink.png", TexturePurpose.Diffuse),
                     ("Shoes/Shoes_W.png", TexturePurpose.Diffuse), ("Shoes/Normal_3.png", TexturePurpose.Normal),
                 })
        {
            Touch(folder, path);
            manifest.Entries.Add(new StashEntry { Path = path, Kind = StashKind.Texture, Purpose = purpose });
        }
        manifest.Materials.Add(new StashMaterial
        {
            Name = "UV1 Black Gold",
            Textures = { [TexturePurpose.Diffuse] = "UV 1/UV1_Black.png", [TexturePurpose.Normal] = "UV 1/AO/UV1_Normal2.png", [TexturePurpose.Occlusion] = "UV 1/AO/UV1_AO.png" },
        });
        manifest.Materials.Add(new StashMaterial { Name = "UV1 Pink", Textures = { [TexturePurpose.Diffuse] = "UV 1/UV2_Pink.png", [TexturePurpose.Normal] = "UV 1/AO/UV1_Normal2.png" } });
        manifest.Materials.Add(new StashMaterial { Name = "Shoes", Textures = { [TexturePurpose.Diffuse] = "Shoes/Shoes_W.png", [TexturePurpose.Normal] = "Shoes/Normal_3.png" } });

        var items = TextureItems.Build(manifest, folder, layout: false);
        string Show() => $"{string.Join("; ", items.Items.Select(i => $"{i.Name}: {string.Join(", ", i.Textures.Select(t => t.Path))}"))} / loose: {string.Join(", ", items.Loose.Select(t => t.Path))}";
        Require(items.Items.Count == 1 && items.Items[0].Textures.Select(t => t.Path).Order(StringComparer.Ordinal)
                    .SequenceEqual(new[] { "UV 1/AO/UV1_AO.png", "UV 1/AO/UV1_Normal2.png", "UV 1/UV1_Black.png", "UV 1/UV2_Pink.png" }),
            $"each material's diffuse joins the set of the normal map it uses ({Show()})");
        Require(items.Loose.Select(t => t.Path).Order(StringComparer.Ordinal).SequenceEqual(new[] { "Shoes/Normal_3.png", "Shoes/Shoes_W.png" }),
            $"a material using only loose textures makes no set of them ({Show()})");
    }

    private static void PropertyBeatsFileName()
    {
        Require(TexturePurposes.Classify(null, TexturePurpose.Diffuse, -1, "x_normal.png") == TexturePurpose.Diffuse,
            "the material property a texture is used by beats its file name");
        Require(TexturePurposes.Classify(null, null, 1, "x.png") == TexturePurpose.Normal, "Unity's normal-map flag beats the file name");
        Require(TexturePurposes.Classify(TexturePurpose.Mask, TexturePurpose.Diffuse, 1, "x_n.png") == TexturePurpose.Mask,
            "the source's own role beats everything");
        Require(TexturePurposes.FromSimsRole(InstantEdit.Studio.Services.Sims.SimsTextureRole.Specular) == TexturePurpose.Mask &&
                TexturePurposes.FromSimsRole(InstantEdit.Studio.Services.Sims.SimsTextureRole.Unknown) == null,
            "Sims roles map onto purposes; an unknown role says nothing");
    }

    private static void UnityManifestGroupsTexturesByMaterial(string root)
    {
        var scan = UnityPackageReader.Scan(BasicPackage(root, "manifest"), _ => { }, CancellationToken.None);
        scan.Prefabs.Add(new UnityPrefab("77777777777777777777777777777777", "Black Gold", new[] { GuidMat }));
        var output = Path.Combine(root, "manifest-out");
        var extraction = UnityPackageReader.Extract(scan, output, _ => { }, CancellationToken.None);
        var manifest = StashManifest.ForUnity(scan, extraction);

        Require(manifest.SourceKind == "unity" && manifest.Materials.Count == 1 && manifest.Materials[0].Name == "Skin",
            "each material with a written texture is listed");
        var skin = manifest.Materials[0];
        Require(skin.Textures.GetValueOrDefault(TexturePurpose.Diffuse) == "A/Tex/body_d.png" &&
                skin.Textures.GetValueOrDefault(TexturePurpose.Normal) == "A/Tex/body_n.png",
            "a material's textures are listed by purpose, as paths relative to the extraction");
        var diffuse = manifest.Entries.First(e => e.Path == "A/Tex/body_d.png");
        Require(diffuse.Kind == StashKind.Texture && diffuse.Purpose == TexturePurpose.Diffuse, "a texture entry carries its purpose");
        Require(diffuse.Groups.Contains("Skin") && diffuse.Groups.Contains("Black Gold"),
            "a texture is grouped under its material and the prefabs that use that material");
        Require(manifest.Entries.First(e => e.Path == "A/FBX/m.fbx") is { Kind: StashKind.Model, NeedsConversion: true },
            "an .fbx is a model that needs converting");
        Require(StashManifest.Read(extraction.RootFolder) != null, "the extraction writes stash.json");
    }

    private static void SimsManifestUsesCasRoles(string root)
    {
        var folder = Path.Combine(root, "sims-manifest");
        Directory.CreateDirectory(Path.Combine(folder, "textures", "other"));
        var file = Path.Combine(folder, "textures", "other", "Dress_other.png");
        File.WriteAllBytes(file, PngBytes);
        var extraction = new InstantEdit.Studio.Services.Sims.SimsPackageInspector.ExtractionResult { RootFolder = folder };
        extraction.WrittenFiles.Add(file);
        extraction.Textures.Add((file, InstantEdit.Studio.Services.Sims.SimsTextureRole.Normal, "Dress"));
        var manifest = StashManifest.ForSims(@"C:\p\dress.package", extraction);
        var entry = manifest.Entries.Single();
        Require(entry.Purpose == TexturePurpose.Normal && entry.Groups.SequenceEqual(new[] { "Dress" }),
            "a Sims texture takes its purpose from the CAS role, not its folder or name, and is grouped under its CAS part");
        Require(manifest.Materials.Single().Name == "Dress" && manifest.Materials[0].Textures[TexturePurpose.Normal] == "textures/other/Dress_other.png",
            "each CAS part is listed as a material with its textures");
    }

    private static void ManifestRoundTrips(string root)
    {
        var folder = Path.Combine(root, "roundtrip");
        Directory.CreateDirectory(folder);
        var manifest = new StashManifest { Source = @"C:\p\x.unitypackage", SourceKind = "unity", ExtractedUtc = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc) };
        manifest.Entries.Add(new StashEntry { Path = "a/b.png", Kind = StashKind.Texture, Purpose = TexturePurpose.Normal, Groups = { "Skin" }, Detail = "normal map" });
        manifest.Materials.Add(new StashMaterial { Name = "Skin", Textures = { [TexturePurpose.Normal] = "a/b.png" } });
        manifest.Write(folder);
        var read = StashManifest.Read(folder);
        Require(read != null && read.Source == manifest.Source && read.ExtractedUtc == manifest.ExtractedUtc &&
                read.Entries.Single() is { Path: "a/b.png", Kind: StashKind.Texture, Purpose: TexturePurpose.Normal, Detail: "normal map" } e &&
                e.Groups.SequenceEqual(new[] { "Skin" }) && read.Materials.Single().Textures[TexturePurpose.Normal] == "a/b.png",
            "stash.json reads back what was written");
        File.WriteAllText(Path.Combine(folder, StashManifest.FileName), "{ not json");
        Require(StashManifest.Read(folder) == null, "a damaged stash.json reads as none");
    }

    private static void LegacyFolderIsScanned(string root)
    {
        var folder = Path.Combine(root, "legacy");
        Directory.CreateDirectory(Path.Combine(folder, "textures", "normal"));
        Directory.CreateDirectory(Path.Combine(folder, "model"));
        File.WriteAllBytes(Path.Combine(folder, "textures", "normal", "x.png"), PngBytes);
        File.WriteAllBytes(Path.Combine(folder, "model", "a.glb"), new byte[] { 1 });
        File.WriteAllText(Path.Combine(folder, "extract-report.txt"), "report");
        var manifest = StashManifest.Scan(folder);
        Require(manifest.Entries.Count == 2, $"a folder without stash.json is scanned, its report left out (got {manifest.Entries.Count})");
        Require(manifest.Entries.Any(e => e.Path == "textures/normal/x.png" && e.Purpose == TexturePurpose.Normal),
            "a texture in a Sims role folder takes that role");
        Require(manifest.Entries.Any(e => e.Path == "model/a.glb" && e is { Kind: StashKind.Model, NeedsConversion: true }),
            "a .glb is a model that needs converting");
    }

    private static void ReExtractReplacesManifest(string root)
    {
        var output = Path.Combine(root, "reextract-out");
        var package = BasicPackage(root, "again");
        var first = UnityPackageReader.Scan(package, _ => { }, CancellationToken.None);
        UnityPackageReader.Extract(first, output, _ => { }, CancellationToken.None);

        var second = UnityPackageReader.Scan(package, _ => { }, CancellationToken.None);
        second.Assets.First(a => a.Guid == GuidModel).Selected = false;
        var result = UnityPackageReader.Extract(second, output, _ => { }, CancellationToken.None);

        Require(result.RootFolder == Path.Combine(output, "again"), "extracting the same package again reuses its folder");
        Require(!File.Exists(Path.Combine(output, "again", "A", "FBX", "m.fbx")), "files of the earlier run that this run left out are gone");
        var manifest = StashManifest.Read(result.RootFolder);
        Require(manifest != null && manifest.Entries.All(e => e.Path != "A/FBX/m.fbx") && manifest.Entries.Count == 2,
            "stash.json describes only the latest run");

        File.WriteAllText(Path.Combine(output, "again", "mine.txt"), "user file");
        var third = UnityPackageReader.Extract(second, output, _ => { }, CancellationToken.None);
        Require(third.RootFolder == Path.Combine(output, "again_2") && File.Exists(Path.Combine(output, "again", "mine.txt")),
            "a folder holding someone else's files is left alone and a numbered folder is used");
    }

    private static void PackagesExtractedTogetherShareAFolder(string root)
    {
        // The model and its material in one package, the textures the material names in another.
        var modelPackage = Path.Combine(root, "outfit.unitypackage");
        WriteUnityPackage(modelPackage,
            (GuidModel, "Assets/A/FBX/m.fbx", new byte[] { 9, 9, 9 }, null),
            (GuidMat, "Assets/A/M/skin.mat", System.Text.Encoding.UTF8.GetBytes(MaterialText("Skin", GuidTex, GuidNormal)), null));
        var texturePackage = Path.Combine(root, "outfit-textures.unitypackage");
        WriteUnityPackage(texturePackage,
            (GuidTex, "Assets/A/Tex/body_d.png", PngBytes, TextureMeta(GuidTex, 0)),
            (GuidNormal, "Assets/A/Tex/body_n.png", PngBytes, TextureMeta(GuidNormal, 1)),
            ("66666666666666666666666666666666", "Assets/A/FBX/m.fbx", new byte[] { 1 }, null));
        var output = Path.Combine(root, "together-out");
        List<UnityScanResult> Scans() => new[] { modelPackage, texturePackage }.Select(p => UnityPackageReader.Scan(p, _ => { }, CancellationToken.None)).ToList();

        var result = UnityPackageReader.Extract(Scans(), output, _ => { }, CancellationToken.None);
        Require(result.RootFolder == Path.Combine(output, "outfit"), "packages extracted together go into one folder, named after the first");
        Require(File.Exists(Path.Combine(result.RootFolder, "A", "Tex", "body_d.png")) && File.ReadAllBytes(Path.Combine(result.RootFolder, "A", "FBX", "m.fbx")).SequenceEqual(new byte[] { 9, 9, 9 }),
            "both packages' files are written, and where two write the same path the first package's file is kept");
        Require(result.Skipped == 1, $"the file left out for it is counted (got {result.Skipped})");
        var manifest = StashManifest.Read(result.RootFolder);
        Require(manifest != null && manifest.Sources.Count == 2 && manifest.Materials.SingleOrDefault() is { Name: "Skin" } skin
                && skin.Textures.GetValueOrDefault(TexturePurpose.Diffuse) == "A/Tex/body_d.png" && skin.Textures.GetValueOrDefault(TexturePurpose.Normal) == "A/Tex/body_n.png",
            "a material in one package is matched with its textures from the other");

        var again = UnityPackageReader.Extract(Scans(), output, _ => { }, CancellationToken.None);
        Require(again.RootFolder == result.RootFolder, "extracting the same packages together again reuses their folder");
        var alone = UnityPackageReader.Extract(UnityPackageReader.Scan(modelPackage, _ => { }, CancellationToken.None), output, _ => { }, CancellationToken.None);
        Require(alone.RootFolder == Path.Combine(output, "outfit_2") && File.Exists(Path.Combine(result.RootFolder, "A", "Tex", "body_d.png")),
            "one of them extracted alone leaves the shared folder be and uses a numbered one");
    }

    private static void UnityManifestNamesModelMaterials(string root)
    {
        // The models in one package, the materials the remap names (with their texture) in another.
        // The model is an avatar's: its meta, rig and all, runs past what a scan reads of a meta.
        const string GuidPlain = "99999999999999999999999999999999";
        const string GuidHair = "cccccccccccccccccccccccccccccccc";
        var meta = ModelMeta(GuidModel, ("Body", GuidMat), ("\"\\u9AEA\"", GuidHair), ("Hair", "66666666666666666666666666666666")) + Rig(2000);
        var models = Path.Combine(root, "remap-models.unitypackage");
        WriteUnityPackage(models,
            (GuidModel, "Assets/A/FBX/m.fbx", new byte[] { 9, 9, 9 }, meta),
            (GuidPlain, "Assets/A/FBX/plain.fbx", new byte[] { 8 }, null));
        var materials = Path.Combine(root, "remap-materials.unitypackage");
        WriteUnityPackage(materials,
            (GuidTex, "Assets/A/Tex/body_d.png", PngBytes, TextureMeta(GuidTex, 0)),
            (GuidMat, "Assets/A/M/skin.mat", System.Text.Encoding.UTF8.GetBytes(MaterialText("Skin", GuidTex, GuidNormal)), null),
            (GuidHair, "Assets/A/M/hair.mat", System.Text.Encoding.UTF8.GetBytes(MaterialText("\"\\u9AEA\"", GuidTex, GuidNormal)), null));

        var modelScan = UnityPackageReader.Scan(models, _ => { }, CancellationToken.None);
        var remap = modelScan.Assets.First(a => a.Guid == GuidModel).MaterialRemap;
        Require(System.Text.Encoding.UTF8.GetByteCount(meta) > 256 * 1024 && remap.Count == 3 && remap.GetValueOrDefault("Body") == GuidMat,
            $"a package scan carries a model's material remap from its meta, however long the meta runs ({Show(remap)})");
        Require(modelScan.Assets.First(a => a.Guid == GuidPlain).MaterialRemap.Count == 0, "a model without a meta has no remap");

        var extraction = UnityPackageReader.Extract(new[] { modelScan, UnityPackageReader.Scan(materials, _ => { }, CancellationToken.None) },
            Path.Combine(root, "remap-out"), _ => { }, CancellationToken.None);
        var manifest = StashManifest.Read(extraction.RootFolder);
        var model = manifest?.Entries.FirstOrDefault(e => e.Path == "A/FBX/m.fbx");
        Require(model?.MaterialNames is { Count: 2 } names && names.GetValueOrDefault("Body") == "Skin" && names.GetValueOrDefault("\u9AEA") == "\u9AEA",
            $"a model's entry names the Unity material each of its materials maps to, from the package holding it, names Unity escaped included; a GUID no package ships is dropped ({Show(model?.MaterialNames)})");
        Require(manifest!.Materials.Any(m => m.Name == "\u9AEA") && manifest.Entries.First(e => e.Path == "A/Tex/body_d.png").Groups.Contains("\u9AEA"),
            "a material Unity wrote an escaped name for is listed, and groups its textures, under the name itself");
        Require(manifest.Entries.Where(e => e != model).All(e => e.MaterialNames == null), "a model without a remap, and every texture, names none");
        Require(StashManifest.ForUnity(modelScan, extraction).Entries.First(e => e.Path == "A/FBX/m.fbx").MaterialNames == null,
            "without the package holding its materials, a remap names none");
    }

    private static void MaterialNamesRoundTrip(string root)
    {
        var folder = Directory.CreateDirectory(Path.Combine(root, "roundtrip-remap")).FullName;
        var manifest = new StashManifest { Source = @"C:\p\x.unitypackage", SourceKind = "unity" };
        manifest.Entries.Add(new StashEntry { Path = "FBX/m.fbx", Kind = StashKind.Model, MaterialNames = new() { ["Body"] = "Skin", ["\u9AEA"] = "Hair" } });
        manifest.Entries.Add(new StashEntry { Path = "FBX/plain.fbx", Kind = StashKind.Model });
        manifest.Write(folder);
        var json = File.ReadAllText(Path.Combine(folder, StashManifest.FileName));
        var read = StashManifest.Read(folder);
        Require(read != null && read.Entries.Count == 2 && read.Entries[0].MaterialNames is { Count: 2 } names
                && names.GetValueOrDefault("Body") == "Skin" && names.GetValueOrDefault("\u9AEA") == "Hair",
            "a model's material names read back from stash.json");
        Require(read is { Entries.Count: 2 } && read.Entries[1].MaterialNames == null && json.Split("\"MaterialNames\"").Length == 2,
            "an entry without material names writes none and reads back with none");

        File.WriteAllText(Path.Combine(folder, StashManifest.FileName),
            """{ "Version": 1, "Source": "C:\\p\\x.unitypackage", "SourceKind": "unity", "Entries": [ { "Path": "FBX/m.fbx", "Kind": "Model", "NeedsConversion": true } ] }""");
        Require(StashManifest.Read(folder)?.Entries.SingleOrDefault() is { Path: "FBX/m.fbx", NeedsConversion: true, MaterialNames: null },
            "an older stash.json without material names still reads");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Unity packages
    // ─────────────────────────────────────────────────────────────────────────

    private const string GuidTex   = "11111111111111111111111111111111";
    private const string GuidModel = "22222222222222222222222222222222";
    private const string GuidFolder = "33333333333333333333333333333333";
    private const string GuidMat   = "44444444444444444444444444444444";
    private const string GuidNormal = "55555555555555555555555555555555";

    private static readonly byte[] PngBytes = { 0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3, 4 };

    private static string MaterialText(string name, string mainTex, string bump) =>
        "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n--- !u!21 &2100000\nMaterial:\n" +
        $"  m_Name: {name}\n  m_Shader: {{fileID: 4800000, guid: df12117ecd77c31469c224178886498e, type: 3}}\n" +
        "  m_SavedProperties:\n    serializedVersion: 3\n    m_TexEnvs:\n" +
        "    - _AlphaMask:\n        m_Texture: {fileID: 0}\n        m_Scale: {x: 1, y: 1}\n" +
        $"    - _BumpMap:\n        m_Texture: {{fileID: 2800000, guid: {bump}, type: 3}}\n        m_Scale: {{x: 1, y: 1}}\n" +
        "    - _Empty:\n        m_Texture: {fileID: 2800000, guid: 0000000000000000f000000000000000, type: 0}\n" +
        $"    - _MainTex:\n        m_Texture: {{fileID: 2800000, guid: {mainTex}, type: 3}}\n        m_Scale: {{x: 1, y: 1}}\n" +
        "    m_Floats:\n    - _Cutoff: 0.5\n";

    private static string TextureMeta(string guid, int type) =>
        $"fileFormatVersion: 2\nguid: {guid}\nTextureImporter:\n  serializedVersion: 12\n  spriteMode: 0\n  textureType: {type}\n";

    /// <summary>An .fbx's meta as Unity writes it: the ID table, then the remap of each named material to a .mat, then the rest of the importer.</summary>
    private static string ModelMeta(string guid, params (string Name, string Material)[] materials)
    {
        var text = new System.Text.StringBuilder($"fileFormatVersion: 2\nguid: {guid}\nModelImporter:\n  serializedVersion: 22200\n")
            .Append("  internalIDToNameTable:\n  - first:\n      74: -2034478483433414484\n    second: Walk\n")
            .Append(materials.Length == 0 ? "  externalObjects: {}\n" : "  externalObjects:\n");
        foreach (var (name, material) in materials)
            text.Append($"  - first:\n      type: UnityEngine:Material\n      assembly: UnityEngine.CoreModule\n      name: {name}\n")
                .Append($"    second: {{fileID: 2100000, guid: {material}, type: 2}}\n");
        return text.Append("  materials:\n    materialImportMode: 2\n    materialName: 0\n").ToString();
    }

    /// <summary>The skeleton of an avatar's rig, as it follows the remap in its .fbx meta: about 160 bytes a bone.</summary>
    private static string Rig(int bones) => "  humanDescription:\n    skeleton:\n" + string.Concat(Enumerable.Range(0, bones).Select(i =>
        $"    - name: Bone{i}\n      parentName: Armature\n      position: {{x: 0, y: 0.95, z: 0.01}}\n      rotation: {{x: 0, y: 0, z: 0, w: 1}}\n      scale: {{x: 1, y: 1, z: 1}}\n"));

    private static string Show(IReadOnlyDictionary<string, string>? map) => map == null ? "none" : string.Join(", ", map.Select(p => $"{p.Key}={p.Value}"));

    /// <summary>Writes a gzip'd tar laid out like Unity's exporter: one folder per GUID.</summary>
    private static void WriteUnityPackage(string path, params (string Guid, string Pathname, byte[]? Asset, string? Meta)[] entries)
    {
        using var file = File.Create(path);
        using var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionLevel.Fastest);
        using var tar = new System.Formats.Tar.TarWriter(gzip, System.Formats.Tar.TarEntryFormat.Ustar);
        void Add(string name, byte[] data)
        {
            var entry = new System.Formats.Tar.UstarTarEntry(System.Formats.Tar.TarEntryType.RegularFile, name) { DataStream = new MemoryStream(data) };
            tar.WriteEntry(entry);
        }
        foreach (var (guid, pathname, asset, meta) in entries)
        {
            // The asset comes before its pathname for some entries, as real packages do.
            if (asset != null) Add($"{guid}/asset", asset);
            Add($"{guid}/pathname", System.Text.Encoding.UTF8.GetBytes(pathname + "\n00"));
            if (meta != null) Add($"{guid}/asset.meta", System.Text.Encoding.UTF8.GetBytes(meta));
        }
    }

    private static string BasicPackage(string root, string name)
    {
        var path = Path.Combine(root, name + ".unitypackage");
        WriteUnityPackage(path,
            (GuidTex, "Assets/A/Tex/body_d.png", PngBytes, TextureMeta(GuidTex, 0)),
            (GuidModel, "Assets/A/FBX/m.fbx", new byte[] { 9, 9, 9 }, null),
            (GuidFolder, "Assets/A", null, "folderAsset: yes\n"),
            (GuidMat, "Assets/A/M/skin.mat", System.Text.Encoding.UTF8.GetBytes(MaterialText("Skin", GuidTex, GuidNormal)), null),
            (GuidNormal, "Assets/A/Tex/body_n.png", PngBytes, TextureMeta(GuidNormal, 1)));
        return path;
    }

    private static void ScanListsAssetsByPath(string root)
    {
        var scan = UnityPackageReader.Scan(BasicPackage(root, "scan"), _ => { }, CancellationToken.None);
        Require(scan.Assets.Count == 4, $"folder entries are dropped, 4 assets remain (got {scan.Assets.Count})");
        var tex = scan.Assets.FirstOrDefault(a => a.Guid == GuidTex);
        Require(tex is { Kind: UnityAssetKind.Texture, PackagePath: "Assets/A/Tex/body_d.png" }, "a texture keeps its package path and kind");
        Require(scan.Assets.Any(a => a.Guid == GuidModel && a.Kind == UnityAssetKind.Model), "an .fbx is a model");
        Require(scan.Assets.Any(a => a.Guid == GuidMat && a.Kind == UnityAssetKind.Material), "a .mat is a material");
        Require(tex!.Size == PngBytes.Length, "an asset's size is its file's");
    }

    private static void MaterialTexturesAreRead()
    {
        var mat = UnityYaml.ReadMaterial(GuidMat, MaterialText("Skin", GuidTex, GuidNormal));
        Require(mat != null && mat.Name == "Skin", "a material's name is read");
        Require(mat!.Textures.TryGetValue("_MainTex", out var main) && main == GuidTex, "_MainTex names its texture GUID");
        Require(mat.Textures.TryGetValue("_BumpMap", out var bump) && bump == GuidNormal, "_BumpMap names its texture GUID");
        Require(!mat.Textures.ContainsKey("_AlphaMask") && !mat.Textures.ContainsKey("_Empty"),
            "properties without a texture, or with Unity's null GUID, are left out");
        Require(mat.Shader == "df12117ecd77c31469c224178886498e", "the shader reference is read");
        Require(UnityYaml.ReadMaterial(GuidMat, MaterialText("\"\\u9AEA\"", GuidTex, GuidNormal))?.Name == "\u9AEA"
                && UnityYaml.ReadMaterial(GuidMat, MaterialText("'Skin #2'", GuidTex, GuidNormal))?.Name == "Skin #2",
            "a material's quoted or escaped name is read as the name itself");
    }

    private static void PrefabMaterialsAreRead()
    {
        var text = "--- !u!1001 &1\nPrefabInstance:\n  m_Modification:\n    m_Modifications:\n" +
                   "    - target: {fileID: -9082840853109227449, guid: 01b57011262d1114d9932044e8f2d030,\n        type: 3}\n" +
                   "      propertyPath: m_Materials.Array.data[0]\n      value: \n" +
                   $"      objectReference: {{fileID: 2100000, guid: {GuidMat}, type: 2}}\n" +
                   "      objectReference: {fileID: 2100000, guid: 66666666666666666666666666666666, type: 2}\n" +
                   $"      objectReference: {{fileID: 2100000, guid: {GuidMat}, type: 2}}\n";
        var materials = UnityYaml.ReadPrefabMaterials(text);
        Require(materials.SequenceEqual(new[] { GuidMat, "66666666666666666666666666666666" }),
            "a prefab lists each material it references once, in order; other references are ignored");
    }

    private static void NormalMapFlagIsRead(string root)
    {
        var meta = UnityYaml.ReadMeta(TextureMeta(GuidNormal, 1));
        Require(meta.Guid == GuidNormal && meta.TextureType == 1, "a meta file's GUID and normal-map texture type are read");
        Require(UnityYaml.ReadMeta("fileFormatVersion: 2\nguid: abc\nDefaultImporter:\n").TextureType == -1,
            "a meta file without a texture importer has no texture type");

        var scan = UnityPackageReader.Scan(BasicPackage(root, "meta"), _ => { }, CancellationToken.None);
        Require(scan.Assets.First(a => a.Guid == GuidNormal).TextureType == 1, "the scan carries the normal-map flag");
        Require(scan.Materials.Count == 1 && scan.Materials[0].Textures["_MainTex"] == GuidTex, "the scan reads the package's materials");
    }

    private static void MaterialRemapIsRead()
    {
        // An outfit's .fbx meta: an ID table shaped like the remap, the remap with other kinds of object in it, then the rest of the importer.
        var text =
            $"fileFormatVersion: 2\nguid: {GuidModel}\nModelImporter:\n  serializedVersion: 22200\n" +
            "  internalIDToNameTable:\n  - first:\n      21: 2100000\n    second: Decoy\n" +
            "  externalObjects:\n" +
            "  - first:\n      type: UnityEngine:Material\n      assembly: UnityEngine.CoreModule\n      name: Body\n" +
            $"    second: {{fileID: 2100000, guid: {GuidMat}, type: 2}}\n" +
            "  - first:\n      type: UnityEngine:AnimationClip\n      assembly: UnityEngine.AnimationModule\n      name: Walk\n" +
            "    second: {fileID: 7400000, guid: 66666666666666666666666666666666, type: 2}\n" +
            // Keys in another order, no assembly, extra spaces, a quoted name and a reference Unity wrapped.
            "  - first:\n      name:    'Hair #2'  \n      type:   UnityEngine:Material\n" +
            "    second:   {fileID: 2100000, guid: AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA,\n      type: 2}\n" +
            // Unity writes a name outside ASCII as escapes.
            "  - first:\n      type: UnityEngine:Material\n      name: \"\\u9AEA\"\n" +
            "    second: {fileID: 2100000, guid: bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb, type: 2}\n" +
            "  - first:\n      type: UnityEngine:Material\n      name: Missing\n    second: {fileID: 0}\n" +
            "  - first:\n      type: UnityEngine:Material\n      name: Builtin\n    second: {fileID: 10303, guid: 0000000000000000f000000000000000, type: 0}\n" +
            "  - first:\n      type: UnityEngine:Avatar\n      name: BodyAvatar\n    second: {fileID: 9000000, guid: 77777777777777777777777777777777, type: 2}\n" +
            "  - first:\n      type: UnityEngine:Material\n      name: Body\n    second: {fileID: 2100000, guid: 88888888888888888888888888888888, type: 2}\n" +
            "  materials:\n    materialImportMode: 2\n" +
            // A block of the same shape under another key.
            "  humanDescription:\n    human:\n    - first:\n        type: UnityEngine:Material\n        name: Late\n" +
            "      second: {fileID: 2100000, guid: 99999999999999999999999999999999, type: 2}\n";

        var remap = UnityYaml.ReadMaterialRemap(text);
        Require(remap.GetValueOrDefault("Body") == GuidMat, $"a model's remap maps each of its material names to a .mat GUID; a name listed twice keeps the first ({Show(remap)})");
        Require(remap.GetValueOrDefault("Hair #2") == "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "an entry is read whatever the order of its keys, with or without an assembly, with odd spacing, a quoted name and a wrapped reference");
        Require(remap.GetValueOrDefault("\u9AEA") == "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "a name Unity escaped is read as the model has it");
        Require(remap.Count == 3,
            $"other kinds of object, empty or built-in references, the ID table and blocks under other keys are left out ({Show(remap)})");
        Require(UnityYaml.ReadMaterialRemap(text.Replace("\n", "\r\n")).OrderBy(p => p.Key, StringComparer.Ordinal).SequenceEqual(remap.OrderBy(p => p.Key, StringComparer.Ordinal)),
            "a meta with Windows line ends reads the same");
        var deeper = "ModelImporter:\n  externalObjects:\n    - first:\n        type: UnityEngine:Material\n        name: Body\n" +
                     $"      second: {{fileID: 2100000, guid: {GuidMat}, type: 2}}\n  materials:\n    materialImportMode: 2\n";
        Require(UnityYaml.ReadMaterialRemap(deeper) is { Count: 1 } indented && indented.GetValueOrDefault("Body") == GuidMat, "entries indented under the key are read too");

        Require(UnityYaml.ReadMaterialRemap(ModelMeta(GuidModel)).Count == 0, "an empty remap ({}) maps nothing");
        Require(UnityYaml.ReadMaterialRemap($"fileFormatVersion: 2\nguid: {GuidModel}\nModelImporter:\n  materials:\n    materialImportMode: 2\n").Count == 0
                && UnityYaml.ReadMaterialRemap(TextureMeta(GuidTex, 0)).Count == 0 && UnityYaml.ReadMaterialRemap("").Count == 0,
            "a meta without a remap maps nothing");
    }

    private static void HostilePathsAreRefused(string root)
    {
        var path = Path.Combine(root, "hostile.unitypackage");
        WriteUnityPackage(path,
            ("a0000000000000000000000000000001", "../evil.png", PngBytes, null),
            ("a0000000000000000000000000000002", "C:/evil.png", PngBytes, null),
            ("a0000000000000000000000000000003", "/abs.png", PngBytes, null),
            ("a0000000000000000000000000000004", "Assets/../../x.png", PngBytes, null),
            ("a0000000000000000000000000000005", "Assets/ok/good.png", PngBytes, null));
        var output = Path.Combine(root, "hostile-out");
        var scan = UnityPackageReader.Scan(path, _ => { }, CancellationToken.None);
        var result = UnityPackageReader.Extract(scan, output, _ => { }, CancellationToken.None);

        var outside = Directory.EnumerateFiles(root, "*.png", SearchOption.AllDirectories)
            .Where(f => !Path.GetFullPath(f).StartsWith(Path.GetFullPath(output) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .ToList();
        Require(outside.Count == 0 && !File.Exists(@"C:\evil.png"), "no file is written outside the output folder");
        Require(result.Refused.Count == 4, $"each hostile path is refused and reported (got {result.Refused.Count})");
        Require(File.Exists(Path.Combine(output, "hostile", "ok", "good.png")), "a good asset beside them is still written");
        Require(UnityPackageReader.SafeRelative("Assets/a/b.png") == "a/b.png" && UnityPackageReader.SafeRelative("x/../y.png") == null,
            "paths lose their leading Assets/ and may not climb");
        Require(UnityPackageReader.SafeRelative("Assets/Blender/m.fbx") == "Assets/Blender/m.fbx" &&
                UnityPackageReader.SafeRelative("blender/m.fbx") == "Assets/blender/m.fbx" &&
                UnityPackageReader.SafeRelative("Assets/blender-exports.json") == "Assets/blender-exports.json" &&
                UnityPackageReader.SafeRelative("Assets/Blenders/m.fbx") == "Blenders/m.fbx",
            "a path into the Blender/ folder or sidecar Blender saves into keeps Assets/, so a re-extraction never replaces Blender's models");
    }

    private static void ExtractKeepsLayout(string root)
    {
        var output = Path.Combine(root, "layout-out");
        var scan = UnityPackageReader.Scan(BasicPackage(root, "layout"), _ => { }, CancellationToken.None);
        var result = UnityPackageReader.Extract(scan, output, _ => { }, CancellationToken.None);
        var tex = Path.Combine(output, "layout", "A", "Tex", "body_d.png");
        Require(File.Exists(tex) && File.ReadAllBytes(tex).SequenceEqual(PngBytes), "a texture lands at its package path, bytes intact");
        Require(result.FileByGuid.TryGetValue(GuidTex, out var mapped) && Path.GetFullPath(mapped) == Path.GetFullPath(tex),
            "the extraction maps each GUID to the file written for it");
        Require(!File.Exists(Path.Combine(output, "layout", "A", "M", "skin.mat")), "materials are read, not written");
        Require(File.Exists(Path.Combine(output, "layout", "A", "FBX", "m.fbx")), "models are written");
        Require(result.RootFolder == Path.Combine(output, "layout"), "the extraction's root is a folder named after the package");
    }

    private static void UnwantedAssetBytesAreNotKept(string root)
    {
        // An animation clip is Unity YAML too, and its asset comes before its pathname here: it must not stay buffered.
        var path = Path.Combine(root, "bytes.unitypackage");
        var clip = System.Text.Encoding.UTF8.GetBytes("%YAML 1.1\n--- !u!74 &7400000\nAnimationClip:\n  m_Name: Wave\n" + new string('x', 4096));
        WriteUnityPackage(path,
            (GuidTex, "Assets/A/Tex/body_d.png", PngBytes, TextureMeta(GuidTex, 0)),
            (GuidMat, "Assets/A/M/skin.mat", System.Text.Encoding.UTF8.GetBytes(MaterialText("Skin", GuidTex, GuidNormal)), null),
            ("66666666666666666666666666666666", "Assets/A/Anim/wave.anim", clip, null));
        var scan = UnityPackageReader.Scan(path, _ => { }, CancellationToken.None);
        var material = System.Text.Encoding.UTF8.GetByteCount(MaterialText("Skin", GuidTex, GuidNormal));
        Require(scan.BufferedAssetBytes == material,
            $"a scan reads only material and prefab text into memory, never images or models (read {scan.BufferedAssetBytes}, material is {material})");
    }

    private static void UnityWorkStopsWhenCancelled(string root)
    {
        var path = BasicPackage(root, "cancel");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        try
        {
            UnityPackageReader.Scan(path, _ => { }, cts.Token);
            Require(false, "a cancelled scan stops");
        }
        catch (OperationCanceledException)
        {
            Require(true, "a cancelled scan stops");
        }
    }

    private const string CacheRoot = @"C:\c\XIV Instant Edit";

    private static void OutputDefaultsToTheCache()
    {
        Require(PackageImportPaths.DefaultOutput(CacheRoot) == @"C:\c\XIV Instant Edit\package-imports",
            "extractions default to package-imports inside the cache folder");
        Require(PackageImportPaths.ChooseOutput("", CacheRoot, @"C:\t\") == PackageImportPaths.DefaultOutput(CacheRoot),
            "no saved folder means the cache default");
    }

    private static void OldTempDefaultMovesToTheCache()
    {
        var expected = PackageImportPaths.DefaultOutput(CacheRoot);
        Require(PackageImportPaths.ChooseOutput(@"C:\t\InstantEdit\sims-import", CacheRoot, @"C:\t\") == expected,
            "the old %TEMP% default moves to the cache");
        Require(PackageImportPaths.ChooseOutput(@"c:\T\instantedit\SIMS-IMPORT\", CacheRoot, @"C:\t\") == expected,
            "the old default is recognised whatever its case and with a trailing slash");
    }

    private static void PickedFolderIsKept()
    {
        Require(PackageImportPaths.ChooseOutput(@"D:\mine", CacheRoot, @"C:\t\") == @"D:\mine",
            "a folder the user picked is kept");
    }
}
