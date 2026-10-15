using System.IO.Compression;
using InstantEdit.Studio.Services.Project;
using Newtonsoft.Json.Linq;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// The Mod Browser's library: tile images, mods in Penumbra's legacy split layout, Penumbra's
/// delete and rename events, and which mod an import opens.
/// </summary>
internal static class ModLibraryScenarios
{
    public static void Run(string testRoot)
    {
        var root = Path.Combine(testRoot, "ModLibrary");
        Directory.CreateDirectory(root);
        TileImagesStayInsideTheModFolder(root);
        LegacyGroupsKeepEveryKey(root);
        PenumbraEventsMatchTheFullPath(root);       // glue-3
        ImportsOpenOnlyTheModTheyAskedFor(root);    // glue-7
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

    private static void PenumbraEventsMatchTheFullPath(string root)
    {
        var modRoot = Path.Combine(root, "Penumbra");
        var backup  = Path.Combine(root, "Backups", "MyMod");
        Require(ModLibrary.IsModFolder(Path.Combine(modRoot, "MyMod"), modRoot, "MyMod")
                && ModLibrary.IsModFolder(Path.Combine(modRoot, "mymod") + Path.DirectorySeparatorChar, modRoot + Path.DirectorySeparatorChar, "MyMod"),
            "a mod folder in Penumbra's mod directory matches its event, whatever the case or trailing separator");
        Require(!ModLibrary.IsModFolder(backup, modRoot, "MyMod"),
            "a folder of the same name outside Penumbra's mod directory is not the mod Penumbra deleted or renamed");
        Require(!ModLibrary.IsModFolder(Path.Combine(modRoot, "MyMod"), null, "MyMod") && !ModLibrary.IsModFolder(Path.Combine(modRoot, "MyMod"), modRoot, ""),
            "without Penumbra's mod directory (or a name) nothing matches");
    }

    private static void ImportsOpenOnlyTheModTheyAskedFor(string root)
    {
        var package = Path.Combine(root, "cool_top_v2.pmp");
        using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("meta.json").Open()))
            writer.Write(new JObject { ["FileVersion"] = 3, ["Name"] = "Cool Top: Red" }.ToString());
        Require(ModLibrary.PackageModName(package) == "Cool Top: Red", "the mod name is read from the package's meta.json");
        var notZip = Path.Combine(root, "old.ttmp");
        File.WriteAllText(notZip, "{\"Name\":\"x\"}");
        Require(ModLibrary.PackageModName(notZip) == null, "a package that isn't a zip gives no name");

        var import = new ModLibrary.PendingImport("cool_top_v2", "Cool Top: Red", DateTime.UtcNow);
        Require(ModLibrary.MatchesImport(import, "Cool Top_ Red", "Cool Top: Red"), "the imported mod matches by its name");
        Require(ModLibrary.MatchesImport(import, "Cool Top_ Red (2)", "Cool Top: Red"), "a numbered folder of the same mod matches");
        Require(!ModLibrary.MatchesImport(import, "Other Mod", "Other Mod") && !ModLibrary.MatchesImport(import, "Cool Top_ Red (copy)", "Cool Top: Red (copy)"),
            "an unrelated mod, or a copy made meanwhile, is not taken for the import");
        var byFile = new ModLibrary.PendingImport("Cool Hair", null, DateTime.UtcNow);
        Require(ModLibrary.MatchesImport(byFile, "Cool Hair", null) && !ModLibrary.MatchesImport(byFile, "Hat", "Hat"),
            "without a name in the package the file name is matched");
    }
}
