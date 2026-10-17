using InstantEdit.Studio.Models;
using InstantEdit.Studio.Models.Project;
using InstantEdit.Studio.Services;
using InstantEdit.Studio.Services.Resources;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// The Mod Browser's resources: where face, ear and hair materials live, model string blocks, the
/// resource graph's snapshot of the replacements, source image sizes and conversion, and the texture cache.
/// </summary>
internal static class ModResourceScenarios
{
    public static void Run(string testRoot)
    {
        var root = Path.Combine(testRoot, "ModResources");
        Directory.CreateDirectory(root);
        FacesAndEarsHaveNoVariantFolder();
        ModelStringBlockIsCheckedBeforeAllocating();            // gamedata-3
        GraphBuildsFromACopyOfTheReplacements(root);            // resources-5
        DdsSizesComeFromTheHeader(root);
        Bc7DdsRecompresses(root);                               // resources-6
        OversizedImagesAreRefused(root);                        // resources-7
        PlaceholdersAreGenerated();
        PruneGetsUnderTheLimit(root);
    }

    private static void PlaceholdersAreGenerated()
    {
        var metal = DummyTextureLibrary.Resolve(TextureType.Mask, DummyTextureLibrary.MetalKey);
        var flat = DummyTextureLibrary.Resolve(TextureType.Normal, null);
        Require(metal is { R: 185, G: 5, B: 185, A: 255 } && flat is { R: 128, G: 128, B: 255, A: 255 } &&
                DummyTextureLibrary.Resolve(TextureType.Diffuse, DummyTextureLibrary.MetalKey) is { R: 255, G: 255, B: 255 },
            "placeholders: metal and flat normal have their colors, and a role without metal falls back to white");

        Require(TextureConverter.CreateDummy(metal, TextureCompression.None, out var tex, out var error) &&
                BitConverter.ToUInt16(tex, 8) == 16 && BitConverter.ToUInt16(tex, 10) == 16 && tex.Length == 80 + 16 * 16 * 4 &&
                tex[80] == 185 && tex[81] == 5 && tex[82] == 185 && tex[83] == 255 && tex[^4] == 185 && tex[^1] == 255,
            $"placeholders: an uncompressed one is a 16 x 16 BGRA square of its color ({error})");
        Require(TextureConverter.CreateDummy(metal, TextureCompression.Bc7, out tex, out error) &&
                BitConverter.ToUInt16(tex, 8) == 16 && BitConverter.ToUInt16(tex, 10) == 16,
            $"placeholders: a BC7 one is 16 x 16 too ({error})");
    }

    private static void FacesAndEarsHaveNoVariantFolder()
    {
        // Checked against the game and real mods: faces and Viera ears keep their materials in
        // material/, hair, tails and bodies in material/v0001.
        Require(FeatureNaming.MaterialFolder(SubjectKind.Face, "0801", 1) == "chara/human/c0801/obj/face/f0001/material"
                && FeatureNaming.ResolveMaterialPath(SubjectKind.Face, "/mt_c0801f0001_fac_a.mtrl", "0801", 1)
                    == "chara/human/c0801/obj/face/f0001/material/mt_c0801f0001_fac_a.mtrl",
            "a ported face reads and writes its materials where the game looks");
        Require(FeatureNaming.ResolveMaterialPath(SubjectKind.Ear, "/mt_c1801z0001_a.mtrl", "1801", 1)
                    == "chara/human/c1801/obj/zear/z0001/material/mt_c1801z0001_a.mtrl"
                && FeatureNaming.MaterialFolder(SubjectKind.Ear, "1801", 1) == "chara/human/c1801/obj/zear/z0001/material",
            "Viera ears have no variant folder either");
        Require(FeatureNaming.MaterialFolder(SubjectKind.Hair, "0101", 5) == "chara/human/c0101/obj/hair/h0005/material/v0001"
                && FeatureNaming.MaterialFolder(SubjectKind.Tail, "0701", 1) == "chara/human/c0701/obj/tail/t0001/material/v0001",
            "hair and tails keep v0001");

        Require(ResourceNaming.MaterialFolder("mt_c0101f0001_fac_a.mtrl") == "chara/human/c0101/obj/face/f0001/material"
                && ResourceNaming.MaterialFolder("/mt_c1801z0001_fac_a.mtrl") == "chara/human/c1801/obj/zear/z0001/material"
                && ResourceNaming.MaterialFolder("mt_c0101h0005_hir_a.mtrl") == "chara/human/c0101/obj/hair/h0005/material/v0001",
            "the resource tree looks a face or ear material up without a variant folder");
        Require(ResourceNaming.FallbackMaterialFolder("chara/human/c0101/obj/face/f0001/model/c0101f0001_fac.mdl") == "chara/human/c0101/obj/face/f0001/material"
                && ResourceNaming.FallbackMaterialFolder("chara/equipment/e6114/model/c0201e6114_sho.mdl") == "chara/equipment/e6114/material/v0001",
            "a face model's own materials fall back to material/, gear to v0001");
        Require(ResourceNaming.MaterialFolderStem(ResourceNaming.MaterialFolder("mt_c0101f0001_fac_a.mtrl")!) == "chara/human/c0101/obj/face/f0001/material/",
            "a face material folder's stem is its material folder");

        const string sharedHair = "chara/human/c0801/obj/hair/h0127/model/c0801h0127_hir.mdl";
        Require(ResourceNaming.RelativeMaterialPath(sharedHair, "/mt_c0801h0127_hir_a.mtrl") == "chara/human/c0201/obj/hair/h0127/material/v0001/mt_c0201h0127_hir_a.mtrl"
                && ResourceNaming.RelativeMaterialPath(sharedHair, "/mt_c0201h0127_hir_a.mtrl") == "chara/human/c0201/obj/hair/h0127/material/v0001/mt_c0201h0127_hir_a.mtrl",
            "a shared hairstyle's material is looked up under the race it is shared from");
        Require(ResourceNaming.RelativeMaterialPath("chara/human/c0801/obj/hair/h0105/model/c0801h0105_hir.mdl", "/mt_c0801h0105_hir_a.mtrl")
                    == "chara/human/c0801/obj/hair/h0105/material/v0001/mt_c0801h0105_hir_a.mtrl"
                && ResourceNaming.RelativeMaterialPath("chara/human/c0801/obj/face/f0001/model/c0801f0001_fac.mdl", "/mt_c0801f0001_iri_a.mtrl")
                    == "chara/human/c0801/obj/face/f0001/material/mt_c0801f0001_iri_a.mtrl"
                && ResourceNaming.RelativeMaterialPath("chara/equipment/e6114/model/c0201e6114_sho.mdl", "/mt_c0201e6114_sho_a.mtrl")
                    == "chara/equipment/e6114/material/v0001/mt_c0201e6114_sho_a.mtrl",
            "Miqo'te keep their own hair, faces have no variant, gear is unchanged");
    }

    private static void ModelStringBlockIsCheckedBeforeAllocating()
    {
        var bytes = new byte[68 + 8 + 16];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), 0x01000006u);
        BitConverter.TryWriteBytes(bytes.AsSpan(72, 4), 0x40000000u);   // a 1 GB string block in a 92-byte file
        Exception? error = null;
        long before = GC.GetAllocatedBytesForCurrentThread();
        try { ModelMaterialRewriter.ReadMaterialsStrict(bytes); }
        catch (Exception ex) { error = ex; }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Require(error is InvalidDataException && allocated < 16 * 1024 * 1024,
            $"a model whose string block runs past the file is refused before allocating it ({allocated / 1024} KB, {error?.GetType().Name})");
    }

    private static void GraphBuildsFromACopyOfTheReplacements(string root)
    {
        var mod = Path.Combine(root, "graph-mod");
        Directory.CreateDirectory(mod);
        var slot = new TextureSlot { Type = TextureType.Diffuse, Postfix = "d", SourcePath = Path.Combine(root, "d.png") };
        var material = new MaterialSetup { Name = "mt_c0101e0001_top_a" };
        material.Textures.Add(slot);
        var live = new ResourceReplacement
        {
            GamePath = "chara/equipment/e0001/material/v0001/mt_c0101e0001_top_a.mtrl",
            Authored = material,
            IsNew    = true,
        };

        var snapshot = new ReplacementSnapshot([live]);
        var copy = snapshot.Copies.Single();
        Require(!ReferenceEquals(copy, live) && !ReferenceEquals(copy.Authored, live.Authored) && !ReferenceEquals(copy.Authored!.Textures, live.Authored!.Textures),
            "the worker gets its own copy of each replacement and its slot list");
        live.Authored.Textures.Add(new TextureSlot { Type = TextureType.Normal, Postfix = "n" });
        Require(copy.Authored!.Textures.Count == 1,"a slot added while the graph builds doesn't reach the worker's copy");

        var graph = ResourceGraphBuilder.Build(new GraphInput { Folder = mod, Files = [], Swaps = [], Replacements = snapshot.Copies },
            new ModFileFactsCache(), new NoGameFiles(), CancellationToken.None);
        snapshot.Relink(graph);
        var materialNode = graph.Nodes.Values.FirstOrDefault(n => n.GamePath == live.GamePath);
        var textureNode = graph.Nodes.Values.FirstOrDefault(n => n.AuthoredSlot != null);
        Require(materialNode != null && ReferenceEquals(materialNode.Replacement, live), "the built graph points at the live replacement, which the views edit");
        Require(textureNode != null && ReferenceEquals(textureNode.AuthoredSlot, slot), "and its texture at the live slot");
    }

    private sealed class NoGameFiles : IVanillaFiles
    {
        public bool Exists(string gamePath) => false;
        public byte[]? TryReadBytes(string gamePath) => null;
    }

    private static void DdsSizesComeFromTheHeader(string root)
    {
        var dds = new byte[128];
        BitConverter.TryWriteBytes(dds.AsSpan(0, 4), 0x20534444u);   // "DDS "
        BitConverter.TryWriteBytes(dds.AsSpan(4, 4), 124u);
        BitConverter.TryWriteBytes(dds.AsSpan(12, 4), 256u);          // height
        BitConverter.TryWriteBytes(dds.AsSpan(16, 4), 512u);          // width
        var path = Path.Combine(root, "source.dds");
        File.WriteAllBytes(path, dds);
        var facts = new ImageFileCache().Read(path);
        Require(facts.Ok && facts.Width == 512 && facts.Height == 256, $"a .dds source reads its size from its header (got {facts.Size}, {facts.Error})");

        var bad = Path.Combine(root, "bad.dds");
        File.WriteAllBytes(bad, new byte[8]);
        Require(!new ImageFileCache().Read(bad).Ok, "a file that isn't a DDS is still reported");
    }

    private static void Bc7DdsRecompresses(string root)
    {
        var bc7 = Path.Combine(root, "index.dds");
        File.WriteAllBytes(bc7, Dx10Dds(8, 8, dxgi: 98));
        Require(TextureConverter.Convert(bc7, TextureCompression.Bc5, out var tex, out var error) && tex.Length > 80,
            $"a BC7 .dds is decoded and recompressed to BC5 ({error})");
        Require(TextureConverter.Convert(bc7, TextureCompression.Bc3, out _, out error), $"and to BC3 ({error})");

        var bc6 = Path.Combine(root, "hdr.dds");
        File.WriteAllBytes(bc6, Dx10Dds(8, 8, dxgi: 95));
        Require(!TextureConverter.Convert(bc6, TextureCompression.Bc7, out _, out error) && error.Contains("BC6H", StringComparison.Ordinal),
            $"a BC6H .dds says plainly why it can't be recompressed ({error})");
    }

    /// <summary>A DX10 .dds with one mip of mode-6 BC7/BC6H blocks.</summary>
    private static byte[] Dx10Dds(int width, int height, uint dxgi)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(0x20534444u);
        bw.Write(124);
        bw.Write(0x1007u);
        bw.Write(height);
        bw.Write(width);
        bw.Write(0);
        bw.Write(0);
        bw.Write(1);
        bw.Write(new byte[44]);
        bw.Write(32);
        bw.Write(0x4u);
        bw.Write(0x30315844u);          // DX10
        bw.Write(new byte[20]);
        bw.Write(0x1000u);
        bw.Write(new byte[16]);
        bw.Write(dxgi);
        bw.Write(3u);
        bw.Write(0u);
        bw.Write(1u);
        bw.Write(0u);
        for (int i = 0; i < (width / 4) * (height / 4); i++)
        {
            var block = new byte[16];
            block[0] = 0x40;
            bw.Write(block);
        }
        return ms.ToArray();
    }

    private static void OversizedImagesAreRefused(string root)
    {
        var wide = Path.Combine(root, "wide.png");
        using (var image = new Image<Rgba32>(TextureConverter.MaxSide + 1, 1))
            image.SaveAsPng(wide);
        Require(!TextureConverter.Convert(wide, TextureCompression.None, out _, out var error) && error.Contains("16384", StringComparison.Ordinal),
            $"an image wider than the game takes is refused with a clear message ({error})");

        var dds = Dx10Dds(4, 4, dxgi: 98);
        BitConverter.TryWriteBytes(dds.AsSpan(12, 4), 70000);   // height past what the .tex header holds
        var big = Path.Combine(root, "big.dds");
        File.WriteAllBytes(big, dds);
        Require(!TextureConverter.Convert(big, TextureCompression.None, out _, out error) && error.Contains("16384", StringComparison.Ordinal),
            $"a .dds too large for the game is refused before its data is read ({error})");

        var fine = Path.Combine(root, "fine.png");
        using (var image = new Image<Rgba32>(8, 8))
            image.SaveAsPng(fine);
        Require(TextureConverter.Convert(fine, TextureCompression.None, out var tex, out error) && tex.Length == 80 + 8 * 8 * 4,
            $"an image of a normal size still converts ({error})");
    }

    private static void PruneGetsUnderTheLimit(string root)
    {
        var folder = Path.Combine(root, "texture-cache");
        Directory.CreateDirectory(folder);
        var start = DateTime.UtcNow.AddHours(-1);
        for (int i = 0; i < 5; i++)
        {
            var file = Path.Combine(folder, $"entry{i}.tex");
            File.WriteAllBytes(file, new byte[100]);
            File.SetLastWriteTimeUtc(file, start.AddMinutes(i));
        }

        new TextureCache(folder).Prune(250);
        var left = Directory.GetFiles(folder, "*.tex").Select(Path.GetFileName).OrderBy(n => n).ToList();
        Require(left.SequenceEqual(new[] { "entry3.tex", "entry4.tex" }), $"pruning deletes the oldest entries until the cache is under its limit (left: {string.Join(", ", left)})");
    }
}
