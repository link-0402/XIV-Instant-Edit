using System.Text.Json.Nodes;
using InstantEdit.Services.Materials;
using InstantEdit.Services.PenumbraMods;
using InstantEdit.Studio.Services;
using InstantEdit.Studio.Services.Project;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// The Material window's "Save as variant": the group a material's variants go into (made once,
/// found again by its marker), the options it gets, where their files go, and that the original
/// file, the mod's other groups and every collection's default stay as they were.
/// </summary>
internal static class MaterialVariantScenarios
{
    public static void Run(string testRoot)
    {
        const string material = "chara/equipment/e0001/material/v0001/mt_c0101e0001_top_a.mtrl";
        const string other = "chara/equipment/e0001/material/v0001/mt_c0101e0001_top_b.mtrl";
        const string texture = "chara/equipment/e0001/texture/v01_c0101e0001_top_fx.tex";
        const string shared = @"Files\materials\top_a.mtrl";
        const string groupName = "mt_c0101e0001_top_a variants";

        var root = Path.Combine(testRoot, "VariantMod");
        Directory.CreateDirectory(Path.Combine(root, "Files", "materials"));
        File.WriteAllText(Path.Combine(root, shared), "original");
        File.WriteAllText(Path.Combine(root, "meta.json"), new JsonObject
        {
            ["FileVersion"] = 4,
            ["Identifier"] = Guid.NewGuid(),
            ["Name"] = "Variant Mod",
            ["DefaultData"] = new JsonObject { ["Files"] = new JsonObject { [material] = shared } },
            ["Groups"] = new JsonArray(
                new JsonObject
                {
                    ["Type"] = "Single",
                    ["Id"] = Guid.NewGuid(),
                    ["Name"] = "Style",
                    ["Priority"] = 3,
                    ["DefaultSettings"] = 1,
                    ["Options"] = new JsonArray(
                        new JsonObject { ["Id"] = Guid.NewGuid(), ["Name"] = "Plain" },
                        new JsonObject { ["Id"] = Guid.NewGuid(), ["Name"] = "Fancy", ["Files"] = new JsonObject { [other] = @"Files\materials\top_b.mtrl" } }),
                },
                // A group of the user's own that only shares the name a new variant group would get.
                new JsonObject
                {
                    ["Type"] = "Multi",
                    ["Id"] = Guid.NewGuid(),
                    ["Name"] = groupName,
                    ["Priority"] = 7,
                    ["Options"] = new JsonArray(new JsonObject { ["Id"] = Guid.NewGuid(), ["Name"] = "Extra" }),
                }),
        }.ToJsonString());

        var meta = PenumbraModMeta.Load(root);
        Require(MaterialVariants.FindGroup(meta, material) == null && MaterialVariants.SuggestName(meta, material) == "Variant"
                && MaterialVariants.NewGroupName(meta, material) == groupName + " 2",
            "a mod without variants of the material has no group for them; the new one is named after the material, numbered past a group of that name");
        Require(MaterialVariants.NameProblem(meta, material, "  ") != null && MaterialVariants.NameProblem(meta, material, "original") != null
                && MaterialVariants.NameProblem(meta, material, new string('x', 121)) != null && MaterialVariants.NameProblem(meta, material, "Red") == null,
            "a variant needs a name, and not the one the mod's own material has");

        // The first variant makes the group: Original first and picked by default, above every other group.
        var red = MaterialVariants.WriteInto(root, "Red", [(material, "red"u8.ToArray()), (texture, "pattern"u8.ToArray())]);
        var after = PenumbraModMeta.Load(root);
        var group = after.FindGroupView(red.GroupId);
        var json = Group(root, red.GroupId);
        Require(red.NewGroup && red.GroupName == groupName + " 2" && group is { Type: ModGroupType.Single }
                && group.Options.Select(o => o.Name).SequenceEqual([MaterialVariants.OriginalOption, "Red"]),
            "the first variant makes a single-select group with Original and the variant");
        Require((int?)json["DefaultSettings"] == 0 && (int?)json["Priority"] == 8
                && ((string?)json["Description"])?.StartsWith(ModOptionGroups.VariantGroupDescriptionPrefix, StringComparison.Ordinal) == true
                && ((string?)json["Description"])?.EndsWith(" -> " + material, StringComparison.Ordinal) == true,
            "collections that never chose keep the mod's own material, and the group wins over the mod's other groups");
        var original = group!.Options[0].Ref;
        Require(!after.FilesOf(original).Any(), "Original carries no file, so the mod's own material shows with it");
        after.TryGetFile(red.Option, material, out var redRel);
        Require(red.Files.Count == 2 && PathNormalizer.SameRel(redRel, red.Files[0].RelPath)
                && PathNormalizer.SameRel(redRel, ModWriter.ReplacementRel(after, material, red.Option))
                && after.TryGetFile(red.Option, texture, out var redTex) && PathNormalizer.SameRel(redTex, red.Files[1].RelPath)
                && File.ReadAllText(red.Files[0].DiskPath) == "red" && File.ReadAllText(red.Files[1].DiskPath) == "pattern",
            "the variant's material and its new texture are written into the option's folder, and the option points at them");
        Require(File.ReadAllText(Path.Combine(root, shared)) == "original" && after.TryGetFile(OptionRef.Default, material, out var defaultRel) && defaultRel == shared
                && after.Groups.Count == 3 && after.FindGroupView(after.Groups[1].Id)!.Options.Count == 1,
            "the original file, the default files and the mod's other groups stay as they were");
        Require(File.Exists(Path.Combine(root, "meta.json" + PenumbraModMeta.BackupSuffix)), "meta.json as it was is kept beside it");
        Require(MaterialVariants.NameProblem(after, material, "RED") != null && MaterialVariants.SuggestName(after, material) == "Variant",
            "a name the group has already is refused");

        // The next variant joins the same group, also once the group is renamed in Penumbra.
        var renamed = PenumbraModMeta.Load(root);
        renamed.RenameGroup(red.GroupId, "Colours");
        renamed.Save(root, backupFirst: false);
        var blue = MaterialVariants.WriteInto(root, "Blue", [(material, "blue"u8.ToArray())]);
        after = PenumbraModMeta.Load(root);
        Require(!blue.NewGroup && blue.GroupId == red.GroupId && blue.GroupName == "Colours"
                && after.FindGroupView(red.GroupId)!.Options.Select(o => o.Name).SequenceEqual([MaterialVariants.OriginalOption, "Red", "Blue"])
                && (int?)Group(root, red.GroupId)["DefaultSettings"] == 0,
            "another variant joins the group, found by its marker, and the group's default stays Original");
        Require(after.TryGetFile(blue.Option, material, out var blueRel) && !PathNormalizer.SameRel(blueRel, redRel)
                && File.ReadAllText(red.Files[0].DiskPath) == "red" && File.ReadAllText(blue.Files[0].DiskPath) == "blue",
            "each variant has its own file");
        Require(MaterialVariants.FindGroup(after, other) == null, "another material of the mod has no variant group yet");

        // A variant that can't be written leaves no file behind and meta.json as it was.
        var before = File.ReadAllText(Path.Combine(root, "meta.json"));
        var greenTexture = Path.Combine(root, "Colours", "Green", texture.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(greenTexture);   // a folder where the texture would go: it can't be written
        Require(Throws<IOException>(() => MaterialVariants.WriteInto(root, "Green", [(material, "x"u8.ToArray()), (texture, "y"u8.ToArray())])),
            "a variant whose file can't be written fails");
        Require(File.ReadAllText(Path.Combine(root, "meta.json")) == before
                && !File.Exists(Path.Combine(root, "Colours", "Green", material.Replace('/', Path.DirectorySeparatorChar))),
            "the files written are deleted again and meta.json is not touched");
        Require(Throws<InvalidOperationException>(() => MaterialVariants.WriteInto(root, "Original", [(material, "x"u8.ToArray())]))
                && File.ReadAllText(Path.Combine(root, "meta.json")) == before,
            "a name that can't be used writes nothing");
        Require(!Directory.EnumerateFiles(root, "*" + PenumbraModMeta.TempSuffix, SearchOption.AllDirectories).Any(), "no half-written file is left behind");
    }

    private static JsonObject Group(string root, string id)
        => (JsonNode.Parse(File.ReadAllText(Path.Combine(root, "meta.json")))!["Groups"] as JsonArray)!
            .OfType<JsonObject>().Single(group => string.Equals((string?)group["Id"], id, StringComparison.OrdinalIgnoreCase));

    private static bool Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (T)
        {
            return true;
        }
    }
}
