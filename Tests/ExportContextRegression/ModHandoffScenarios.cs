using System.Text.Json.Nodes;
using InstantEdit.Models;
using InstantEdit.Services;
using InstantEdit.Services.PenumbraMods;
using InstantEdit.Studio;
using InstantEdit.Studio.Services.Project;
using InstantEdit.Ui;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// The Mod Browser's hand-offs: a file of a mod open in a mod window becomes a row of the one
/// option it is listed in, with the memberships Instant Edit's own meta.json reader gives that
/// option, so an import exports back into that option even when several carry the same file.
/// </summary>
internal static class ModHandoffScenarios
{
    public static void Run(string testRoot)
    {
        const string model = "chara/equipment/e0001/model/c0101e0001_top.mdl";
        const string texture = "chara/equipment/e0001/texture/v01_c0101e0001_top_d.tex";
        const string shared = "Files/models/top.mdl";
        const string combined = "Files/models/combined.mdl";

        var root = Path.Combine(testRoot, "HandoffMod");
        Directory.CreateDirectory(root);
        var modId = Guid.NewGuid();
        var styleId = Guid.NewGuid();
        var redId = Guid.NewGuid();
        var blueId = Guid.NewGuid();
        var featuresId = Guid.NewGuid();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        File.WriteAllText(Path.Combine(root, "meta.json"), new JsonObject
        {
            ["FileVersion"] = 4,
            ["Identifier"] = modId,
            ["Name"] = "Handoff Mod",
            ["DefaultData"] = new JsonObject { ["Files"] = new JsonObject { [texture] = @"Files\textures\top_d.tex" } },
            ["Groups"] = new JsonArray(
                new JsonObject
                {
                    ["Type"] = "Single",
                    ["Id"] = styleId,
                    ["Name"] = "Style",
                    ["Options"] = new JsonArray(
                        new JsonObject { ["Id"] = redId, ["Name"] = "Red", ["Files"] = new JsonObject { [model] = shared } },
                        new JsonObject { ["Id"] = blueId, ["Name"] = "Blue", ["Files"] = new JsonObject { [model] = shared } }),
                },
                new JsonObject
                {
                    ["Type"] = "Combining",
                    ["Id"] = featuresId,
                    ["Name"] = "Features",
                    ["Options"] = new JsonArray(
                        new JsonObject { ["Id"] = firstId, ["Name"] = "First" },
                        new JsonObject { ["Id"] = secondId, ["Name"] = "Second" }),
                    ["Containers"] = new JsonArray(
                        new JsonObject(), new JsonObject(), new JsonObject(),
                        new JsonObject { ["Files"] = new JsonObject { [model] = combined } }),
                }),
        }.ToJsonString());

        // The files as a mod window lists them: one per option and game path.
        var meta = PenumbraModMeta.Load(root);
        var mod = new ModHandoffMod("Handoff Mod", "HandoffMod", root, ModMetadata.ReadModStableIdentifier(root));
        var files = meta.AllFiles()
            .Select(f => ModHandoffBridge.File(meta, f.GamePath, Path.Combine(root, f.RelPath), f.RelPath, f.Option))
            .ToList();
        var view = ModHandoffBridge.Mod(mod, files, 0);
        Require(view.Entity is null && view.Category == "Mod" && view.ImportObjectIndex == 0 && view.Roots.Count == 4,
            "a mod window's hand-off view lists every file of every option once, with no character of its own");
        Require(mod.StableId == modId, "the mod's stable identifier is its v4 meta.json identifier");

        var defaultRow = view.Roots.Single(r => r.GamePath == texture);
        Require(defaultRow.OptionMemberships.SequenceEqual([ModHandoffBridge.DefaultMembership]) &&
                defaultRow.OptionMapping == "Default" &&
                defaultRow.SourceRelativePath == "Files/textures/top_d.tex" &&
                defaultRow.SourceModRootPath == root && defaultRow.SourceModDirectory == "HandoffMod" &&
                defaultRow.SourceState == ResourceSourceState.LoadedMod && defaultRow.SourceModStableId == modId,
            "a default file is a row of the default files, with its path in the mod in forward slashes");
        Require(ModOptionGroups.ReadOptionMembershipsForRegression(root, "Files/textures/top_d.tex").SequenceEqual(defaultRow.OptionMemberships),
            "the default files' membership is the one Instant Edit reads from meta.json");

        // The same file in two options: each row names its own option only.
        var red = $"option:{styleId:D}:{redId:D}";
        var blue = $"option:{styleId:D}:{blueId:D}";
        var sharedRows = view.Roots.Where(r => r.SourceRelativePath == shared).ToList();
        Require(sharedRows.Count == 2 &&
                sharedRows.Select(r => r.OptionMemberships.Single()).Order().SequenceEqual(new[] { red, blue }.Order()) &&
                sharedRows.Select(r => r.OptionMapping).Order().SequenceEqual(new[] { "Style: Blue", "Style: Red" }),
            "a file two options carry is two rows, each with its own single option");
        Require(ModOptionGroups.ReadOptionMembershipsForRegression(root, shared).Order()
                    .SequenceEqual(sharedRows.SelectMany(r => r.OptionMemberships).Order()),
            "together the rows have exactly the memberships Instant Edit reads for the file");
        foreach (var row in sharedRows)
        {
            var resolved = ModOptionGroups.ResolveSourceOptionForRegression(root, model, shared,
                new SourceOptionLocator { Membership = row.OptionMemberships.Single(), GroupName = "", OptionName = "" });
            Require(resolved.Error is null && resolved.Membership == row.OptionMemberships.Single(),
                "each row's option resolves on its own, so an export goes back into that very option");
        }
        Require(ResourceViews.IsSafeModel(sharedRows[0]), "a mod window's model rows can be sent to Blender");

        // A combining group's container belongs to each option it combines.
        var combinedRow = view.Roots.Single(r => r.SourceRelativePath == combined);
        Require(combinedRow.OptionMemberships.Order().SequenceEqual(new[]
                {
                    $"option:{featuresId:D}:{firstId:D}",
                    $"option:{featuresId:D}:{secondId:D}",
                }.Order()) &&
                ModOptionGroups.ReadOptionMembershipsForRegression(root, combined).Order().SequenceEqual(combinedRow.OptionMemberships.Order()),
            "a combining container's row names the options it combines, as Instant Edit reads them");

        // Groups and options without ids (meta.json before version 4) can't be named to the hand-offs.
        Require(ModHandoffBridge.Memberships("#0", "#1", []).Count == 0 &&
                ModHandoffBridge.Memberships(styleId.ToString(), "#1", []).Count == 0 &&
                ModHandoffBridge.Memberships(featuresId.ToString(), "c#0", [firstId.ToString(), secondId.ToString()]).Count == 0,
            "positional ids and the empty combination give no membership");
    }
}
