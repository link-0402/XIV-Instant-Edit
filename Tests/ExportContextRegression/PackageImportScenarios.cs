using InstantEdit.Studio.Models;
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
        HostilePathsAreRefused(root);
        ExtractKeepsLayout(root);
        UnwantedAssetBytesAreNotKept(root);
        UnityWorkStopsWhenCancelled(root);

        PurposeFromUnityProperties();
        PurposeFromFileNames();
        PropertyBeatsFileName();
        UnityManifestGroupsTexturesByMaterial(root);
        SimsManifestUsesCasRoles(root);
        ManifestRoundTrips(root);
        LegacyFolderIsScanned(root);
        ReExtractReplacesManifest(root);

        StashApplyMapsPurposes(root);
        ExtractionsAreListedNewestFirst(root);
        RemoveStaysInsideTheOutputFolder(root);
    }

    private static void ExtractionsAreListedNewestFirst(string root)
    {
        var output = Path.Combine(root, "listing");
        var older = Directory.CreateDirectory(Path.Combine(output, "older")).FullName;
        var newer = Directory.CreateDirectory(Path.Combine(output, "newer")).FullName;
        Directory.CreateDirectory(Path.Combine(output, "empty"));
        File.WriteAllBytes(Path.Combine(older, "a.png"), PngBytes);
        File.WriteAllBytes(Path.Combine(newer, "b.png"), PngBytes);
        File.WriteAllBytes(Path.Combine(newer, "c.png"), PngBytes);
        File.WriteAllText(Path.Combine(newer, "notes.txt"), "x");
        Directory.SetLastWriteTimeUtc(older, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Directory.SetLastWriteTimeUtc(newer, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        var listed = StashFolders.ListExtractions(output);
        Require(listed.Select(Path.GetFileName).SequenceEqual(new[] { "newer", "older" }), "extractions are listed newest first; empty folders are left out");
        Require(StashFolders.CountImages(newer) == 2, "a folder's images are counted, other files not");
        Require(StashFolders.ListExtractions(Path.Combine(root, "missing")).Count == 0, "a missing output folder lists nothing");
    }

    private static void RemoveStaysInsideTheOutputFolder(string root)
    {
        var output = Path.Combine(root, "removal");
        var inside = Directory.CreateDirectory(Path.Combine(output, "pkg")).FullName;
        File.WriteAllBytes(Path.Combine(inside, "a.png"), PngBytes);
        var outside = Directory.CreateDirectory(Path.Combine(root, "removal-sibling")).FullName;

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
        };
        foreach (var (name, expected) in cases)
            Require(TexturePurposes.FromFileName(name) == expected, $"{name} is read as {expected} (got {TexturePurposes.FromFileName(name)})");
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
        var scan = UnityPackageReader.Scan(BasicPackage(root, "bytes"), _ => { }, CancellationToken.None);
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
