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
