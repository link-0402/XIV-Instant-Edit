using System.Text.Json.Nodes;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Services;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// Review fixes of Build mode: a build only makes new mods (writers-1/-2), variant files of different
/// items never share a file (writers-3), and explicit mesh-part material picks follow their material
/// when the materials list changes (models-1).
/// </summary>
internal static class ReviewFixBuildScenarios
{
    public static void Run(string testRoot)
    {
        var root = Path.Combine(testRoot, "ReviewFixBuild");
        Directory.CreateDirectory(root);

        ExistingFolderIsRefused(root);
        LeftoversAreRemoved(root);
        MetaIsReplacedWhole(root);
        VariantNamesAreBuildWide();
        MaterialLinksFollowTheirMaterials();
    }

    private static void ExistingFolderIsRefused(string root)
    {
        var fresh = Path.Combine(root, "Not Built Yet");
        Require(BuildTarget.Problem(fresh) == null, "a build may write into a mod folder that does not exist yet");

        var empty = Path.Combine(root, "Empty Folder");
        Directory.CreateDirectory(empty);
        Require(BuildTarget.Problem(empty) != null, "a build refuses an existing folder even when it is empty");

        var built = Path.Combine(root, "Built Mod");
        Directory.CreateDirectory(built);
        File.WriteAllText(Path.Combine(built, "meta.json"), "{\"FileVersion\":4,\"Name\":\"Built Mod\"}");
        var problem = BuildTarget.Problem(built);
        Require(problem != null && problem.Contains("Open it from the library", StringComparison.Ordinal),
            "a build refuses an existing mod (its own earlier build included) and points to editing it");
        Require(!problem!.Contains('…'), "the refusal has no ellipsis character");
    }

    private static void LeftoversAreRemoved(string root)
    {
        var half = Path.Combine(root, "Half Built");
        var nested = Path.Combine(half, "chara", "equipment", "e0001", "texture");
        Directory.CreateDirectory(nested);
        File.WriteAllBytes(Path.Combine(nested, "a.tex"), new byte[] { 1, 2, 3 });
        var readOnly = Path.Combine(half, "copied.mdl");
        File.WriteAllBytes(readOnly, new byte[] { 4 });
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);

        var error = ModBuilder.DeleteLeftovers(half);
        Require(error == null && !Directory.Exists(half), "a cancelled or failed build removes its half-built folder, read-only copies included");
        Require(ModBuilder.DeleteLeftovers(half) == null, "removing a folder that is already gone is not an error");
    }

    private static void MetaIsReplacedWhole(string root)
    {
        var mod = Path.Combine(root, "Meta Mod");
        Directory.CreateDirectory(mod);
        File.WriteAllText(Path.Combine(mod, "meta.json"), "old");

        var defaults = new ModCarrier();
        defaults.Files.Add(new KeyValuePair<string, string>("chara/x.tex", "chara\\x.tex"));
        ModMetaWriter.Write(mod, "Meta Mod", defaults, Array.Empty<ModGroupOut>());

        var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(mod, "meta.json")))!;
        Require((string?)meta["Name"] == "Meta Mod" && meta["DefaultData"]?["Files"]?["chara/x.tex"] != null,
            "meta.json is written whole");
        Require(!File.Exists(Path.Combine(mod, "meta.json.tmp")), "no temp file is left beside meta.json");
    }

    private static void VariantNamesAreBuildWide()
    {
        var names = new VariantFileNames();
        // Two items whose variant sources share a stem (red.mdl in two folders) for the same race.
        var first  = names.Claim("modelvariants", "c0101_red", ".mdl");
        var second = names.Claim("modelvariants", "c0101_red", ".mdl");
        Require(first == "modelvariants/c0101_red.mdl" && second == "modelvariants/c0101_red_2.mdl",
            "model variants of two items with the same file name get files of their own");

        // Two items sharing a model id share material and texture names; their texture variants must not.
        var a = names.Claim("variants", "mt_c0101e0001_top_a_d_Red", ".tex");
        var b = names.Claim("variants", "MT_C0101E0001_TOP_A_D_RED", ".tex");
        Require(!string.Equals(a, b, StringComparison.OrdinalIgnoreCase), "texture variant names are unique across the build, ignoring case");

        var other = names.Claim("variants", "c0101_red", ".mdl");
        Require(other == "variants/c0101_red.mdl", "the same name in another folder is a different file");
    }

    private static void MaterialLinksFollowTheirMaterials()
    {
        const int Auto = ModelMaterialLinks.Auto;
        const int Keep = ModelMaterialLinks.Keep;

        // Materials A, B, C. Part 0 is set to C, part 1 to B, part 2 keeps its own, part 3 is automatic.
        RaceModelEntry Model() => new()
        {
            MaterialLinks = new List<int> { 2, 1, Keep, Auto },
            VariantMaterialLinks = new List<List<int>> { new() { 0, 2 }, new() },
        };

        var removed = Model();
        ModelMaterialLinks.Remap(new[] { removed }, ModelMaterialLinks.Removed(0));   // A removed: B, C
        Require(removed.MaterialLinks.SequenceEqual(new[] { 1, 0, Keep, Auto }),
            "removing a material keeps the other parts on the materials they were set to");
        Require(removed.VariantMaterialLinks[0].SequenceEqual(new[] { Keep, 1 }),
            "a variant's part set to the removed material keeps its own name; the others follow theirs");

        var moved = Model();
        ModelMaterialLinks.Remap(new[] { moved }, ModelMaterialLinks.Swapped(1, 2));   // B and C swap: A, C, B
        Require(moved.MaterialLinks.SequenceEqual(new[] { 1, 2, Keep, Auto }) && moved.VariantMaterialLinks[0].SequenceEqual(new[] { 0, 1 }),
            "moving a material up or down keeps every part on the material it was set to");

        var pasted = Model();
        ModelMaterialLinks.Remap(new[] { pasted }, ModelMaterialLinks.Inserted(1));    // pasted after A: A, P, B, C
        Require(pasted.MaterialLinks.SequenceEqual(new[] { 3, 2, Keep, Auto }) && pasted.VariantMaterialLinks[0].SequenceEqual(new[] { 0, 3 }),
            "pasting a material in the middle keeps every part on the material it was set to");

        var matched = Model();
        ModelMaterialLinks.Remap(new[] { matched }, ModelMaterialLinks.Reset);        // replaced by the vanilla layout
        Require(matched.MaterialLinks.SequenceEqual(new[] { Auto, Auto, Keep, Auto }) && matched.VariantMaterialLinks[0].SequenceEqual(new[] { Auto, Auto }),
            "replacing the materials with the vanilla ones puts every pick back on automatic");

        // What the build resolves after the removal: part 1 (was B) still on B, not on C.
        var written = new List<string> { "/b.mtrl", "/c.mtrl" };
        var referenced = new List<string> { "/x.mtrl", "/y.mtrl", "/z.mtrl", "/w.mtrl" };
        var plan = ModelMaterialLinks.Plan(referenced, removed.MaterialLinks, written);
        Require(plan[0] == "/c.mtrl" && plan[1] == "/b.mtrl" && plan[2] == null,
            "the build writes the remapped picks onto the right .mtrl files");
    }
}
