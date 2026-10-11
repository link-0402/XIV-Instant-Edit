using InstantEdit.Studio.Models;
using InstantEdit.Studio.Services;
using InstantEdit.Studio.Services.Project;
using InstantEdit.Studio.Services.Resources;
using Newtonsoft.Json.Linq;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// Review fixes in the Library group: tile images, legacy group keys, face and ear material folders,
/// DDS sizes and texture cache pruning.
/// </summary>
internal static class ReviewFixLibraryScenarios
{
    public static void Run(string testRoot)
    {
        var root = Path.Combine(testRoot, "review-fix-library");
        Directory.CreateDirectory(root);
        TileImagesStayInsideTheModFolder(root);
        LegacyGroupsKeepEveryKey(root);
        FacesAndEarsHaveNoVariantFolder();
        DdsSizesComeFromTheHeader(root);
        PruneGetsUnderTheLimit(root);
    }

    private static void TileImagesStayInsideTheModFolder(string root)
    {
        var mod = Path.Combine(root, "tile-mod");
        Directory.CreateDirectory(Path.Combine(mod, "images"));
        var inside = Path.Combine(mod, "images", "a.png");
        File.WriteAllBytes(inside, [1]);
        var outside = Path.Combine(root, "outside.png");
        File.WriteAllBytes(outside, [1]);

        Require(ModLibrary.TileImage(mod, "images/a.png") == inside && ModLibrary.TileImage(mod, @"images\a.png") == inside,
            "a tile image inside the mod folder is found once, as a full path");
        Require(ModLibrary.TileImage(mod, outside) == string.Empty && ModLibrary.TileImage(mod, @"\\some.host\share\a.png") == string.Empty
                && ModLibrary.TileImage(mod, "../outside.png") == string.Empty && ModLibrary.TileImage(mod, "C:outside.png") == string.Empty,
            "absolute, network and escaping image paths are never looked up");
        Require(ModLibrary.TileImage(mod, "images/missing.png") == string.Empty && ModLibrary.TileImage(mod, string.Empty) == string.Empty,
            "a missing or empty image gives no tile image");
    }

    private static void LegacyGroupsKeepEveryKey(string root)
    {
        var mod = Path.Combine(root, "legacy-mod");
        Directory.CreateDirectory(mod);
        File.WriteAllText(Path.Combine(mod, "meta.json"), new JObject { ["FileVersion"] = 3, ["Name"] = "Legacy" }.ToString());
        File.WriteAllText(Path.Combine(mod, "group_001_combo.json"), new JObject
        {
            ["Type"] = "Combining", ["Name"] = "Combo", ["Priority"] = 2,
            ["Options"] = new JArray { new JObject { ["Name"] = "A", ["Description"] = "first" }, new JObject { ["Name"] = "B" } },
            ["Containers"] = new JArray
            {
                new JObject { ["Files"] = new JObject() },
                new JObject { ["Name"] = "A only", ["Files"] = new JObject { ["chara/a.tex"] = @"files\a.tex" } },
            },
        }.ToString());
        File.WriteAllText(Path.Combine(mod, "group_002_imc.json"), new JObject
        {
            ["Type"] = "Imc", ["Name"] = "Variants", ["AllVariants"] = true, ["OnlyAttributes"] = false,
            ["Identifier"] = new JObject { ["Type"] = "Equipment", ["PrimaryId"] = 6114, ["EquipSlot"] = "Body" },
            ["DefaultEntry"] = new JObject { ["MaterialId"] = 1, ["AttributeMask"] = 0 },
            ["Options"] = new JArray
            {
                new JObject { ["Name"] = "Off", ["IsDisableSubMod"] = true },
                new JObject { ["Name"] = "Mask", ["AttributeMask"] = 3, ["Future"] = "kept" },
            },
        }.ToString());

        var meta = (JObject)JToken.Parse(File.ReadAllText(Path.Combine(mod, "meta.json")));
        var merged = LegacyMetaReader.Merge(mod, meta, out var legacyFiles);
        var groups = (JArray)merged["Groups"]!;
        var combo = (JObject)groups[0];
        var imc = (JObject)groups[1];
        Require(legacyFiles.Count == 2 && groups.Count == 2, "both split group files are merged");
        Require(combo["Containers"] is JArray { Count: 2 } containers && (string?)containers[1]["Files"]!["chara/a.tex"] == @"files\a.tex"
                && (int?)combo["Priority"] == 2 && (string?)combo["Options"]![0]!["Description"] == "first",
            "a combining group keeps its containers, where all of its files are");
        Require(imc["Identifier"] is JObject { Count: 3 } && imc["DefaultEntry"] is JObject && (bool?)imc["AllVariants"] == true
                && imc["OnlyAttributes"] != null,
            "an IMC group keeps its identifier, default entry and variant flags");
        Require((bool?)imc["Options"]![0]!["IsDisableSubMod"] == true && (int?)imc["Options"]![1]!["AttributeMask"] == 3
                && (string?)imc["Options"]![1]!["Future"] == "kept",
            "options keep their attribute masks, the disable option and keys a later format added");
        Require(groups.All(g => Guid.TryParse((string?)g["Id"], out _)) && groups.SelectMany(g => (JArray)g["Options"]!).All(o => Guid.TryParse((string?)o["Id"], out _))
                && (string?)combo["Id"] == MetaIds.Derive(Guid.Empty, "legacy/legacy-mod/Combo").ToString(),
            "groups and options get their derived ids");
        var again = LegacyMetaReader.Merge(mod, meta, out _);
        Require(JToken.DeepEquals(merged, again), "the derived ids are the same on every read");
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
