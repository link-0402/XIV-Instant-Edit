using System.Text.Json.Nodes;
using InstantEdit.Models;
using InstantEdit.Services;
using static InstantEdit.TestSupport.Assertions;

internal static class SelectorStabilityScenarios
{
    public static void Run(string testRoot)
    {
        const string clonedGamePath = "chara/equipment/e0001/model/c0101e0001_top.mdl";
        var sourceOption = new JsonObject
        {
            ["Name"] = "Original",
            ["Files"] = new JsonObject
            {
                [clonedGamePath] = "Files/models/original.mdl",
                ["chara/equipment/e0001/material/v0001/mt_c0101e0001_top_a.mtrl"] = "Files/materials/top.mtrl",
                ["chara/equipment/e0001/texture/top_d.tex"] = "Files/textures/top_d.tex",
            },
            ["FileSwaps"] = new JsonObject { ["chara/common/texture/a.tex"] = "chara/common/texture/b.tex" },
            ["Manipulations"] = new JsonArray(new JsonObject { ["Type"] = "Eqp", ["Entry"] = 1 }),
        };
        var originalOptionJson = sourceOption.ToJsonString();
        var clonedOption = PenumbraService.BuildVariantOptionForRegression(
            sourceOption, clonedGamePath, "Files/models/variant.mdl");
        Require(clonedOption["Files"]?[clonedGamePath]?.GetValue<string>() == "Files/models/variant.mdl" &&
                clonedOption["Files"]?["chara/equipment/e0001/material/v0001/mt_c0101e0001_top_a.mtrl"] is not null &&
                clonedOption["Files"]?["chara/equipment/e0001/texture/top_d.tex"] is not null &&
                clonedOption["FileSwaps"] is JsonObject && clonedOption["Manipulations"] is JsonArray,
            "variant options clone materials, textures, file swaps, and manipulations while replacing only the model");
        Require(sourceOption.ToJsonString() == originalOptionJson,
            "variant creation leaves the source option unchanged");

        var selectorRoot = Path.Combine(testRoot, "StableSelectors");
        Directory.CreateDirectory(selectorRoot);
        var selectorGroupId = Guid.NewGuid();
        var selectorOptionId = Guid.NewGuid();
        File.WriteAllText(Path.Combine(selectorRoot, "meta.json"), new JsonObject
        {
            ["FileVersion"] = 4,
            ["Identifier"] = Guid.NewGuid(),
            ["LastWrite"] = DateTime.UtcNow,
            ["DefaultData"] = null,
            ["Groups"] = new JsonArray(new JsonObject
            {
                ["Type"] = "Single",
                ["Id"] = selectorGroupId,
                ["Name"] = "Variants",
                ["Options"] = new JsonArray(new JsonObject
                {
                    ["Id"] = selectorOptionId,
                    ["Name"] = "Variant",
                    ["Files"] = new JsonObject { [clonedGamePath] = "Files/models/variant.mdl" },
                }),
            }),
        }.ToJsonString());
        var stableTargets = PenumbraService.ReadVariantTargetsForRegression(selectorRoot, clonedGamePath);
        Require(stableTargets.Single().Id == $"group:{selectorGroupId:D}" &&
                stableTargets.Single().Options.Single().Id == $"option:{selectorGroupId:D}:{selectorOptionId:D}",
            "v4 groups expose stable GUID selectors");
        var stableSelector = stableTargets.Single().Options.Single().Id;
        var firstResolvedOption = PenumbraService.ResolveVariantOptionPathForRegression(
            selectorRoot, clonedGamePath, stableSelector);
        var secondResolvedOption = PenumbraService.ResolveVariantOptionPathForRegression(
            selectorRoot, clonedGamePath, stableSelector);
        Require(firstResolvedOption is not null && firstResolvedOption == secondResolvedOption,
            "the refreshed v4 option selector resolves for two consecutive saves");
        var selectorMetaPath = Path.Combine(selectorRoot, "meta.json");
        var selectorMeta = JsonNode.Parse(File.ReadAllText(selectorMetaPath))!.AsObject();
        selectorMeta["Groups"]![0]!["Name"] = "Renamed Group";
        selectorMeta["Groups"]![0]!["Options"]![0]!["Name"] = "Renamed Option";
        File.WriteAllText(selectorMetaPath, selectorMeta.ToJsonString());
        var stableSource = PenumbraService.ResolveSourceOptionForRegression(
            selectorRoot, clonedGamePath, "Files/models/variant.mdl", new SourceOptionLocator
            {
                Membership = stableSelector,
                GroupName = "Old Group Name",
                OptionName = "Old Option Name",
            });
        Require(stableSource.Error is null && stableSource.Membership == stableSelector,
            "persisted v4 source identity follows GUIDs across renames");
        var legacySource = PenumbraService.ResolveSourceOptionForRegression(
            selectorRoot, clonedGamePath, "Files/models/variant.mdl", new SourceOptionLocator
            {
                Membership = "meta:group:0:option:0",
                GroupName = "Renamed Group",
                OptionName = "Renamed Option",
            });
        Require(legacySource.Error is null && legacySource.Membership == stableSelector,
            "a legacy index locator upgrades through a unique group and option name match");
        var staleStableSource = PenumbraService.ResolveSourceOptionForRegression(
            selectorRoot, clonedGamePath, "Files/models/variant.mdl", new SourceOptionLocator
            {
                Membership = $"option:{selectorGroupId:D}:{Guid.NewGuid():D}",
                GroupName = "Renamed Group",
                OptionName = "Renamed Option",
            });
        Require(staleStableSource.Membership is null && staleStableSource.Error is not null,
            "a stale v4 GUID locator requires re-import instead of falling back to names");
        selectorMeta["Groups"]![0]!["Options"]!.AsArray().Add(new JsonObject
        {
            ["Id"] = Guid.NewGuid(),
            ["Name"] = "Renamed Option",
            ["Files"] = new JsonObject { [clonedGamePath] = "Files/models/variant.mdl" },
        });
        File.WriteAllText(selectorMetaPath, selectorMeta.ToJsonString());
        var ambiguousLegacySource = PenumbraService.ResolveSourceOptionForRegression(
            selectorRoot, clonedGamePath, "Files/models/variant.mdl", new SourceOptionLocator
            {
                Membership = "meta:group:0:option:0",
                GroupName = "Renamed Group",
                OptionName = "Renamed Option",
            });
        Require(ambiguousLegacySource.Membership is null && ambiguousLegacySource.Error is not null,
            "legacy index locators require re-import when names are not unique");

        var combiningRoot = Path.Combine(testRoot, "CombiningMemberships");
        Directory.CreateDirectory(combiningRoot);
        var combiningGroupId = Guid.NewGuid();
        var combiningFirstId = Guid.NewGuid();
        var combiningSecondId = Guid.NewGuid();
        const string combiningRelative = "Files/models/combined.mdl";
        File.WriteAllText(Path.Combine(combiningRoot, "meta.json"), new JsonObject
        {
            ["FileVersion"] = 4,
            ["Identifier"] = Guid.NewGuid(),
            ["LastWrite"] = DateTime.UtcNow,
            ["DefaultData"] = null,
            ["Groups"] = new JsonArray(new JsonObject
            {
                ["Type"] = "Combining",
                ["Id"] = combiningGroupId,
                ["Name"] = "Features",
                ["Options"] = new JsonArray(
                    new JsonObject { ["Id"] = combiningFirstId, ["Name"] = "First" },
                    new JsonObject { ["Id"] = combiningSecondId, ["Name"] = "Second" }),
                ["Containers"] = new JsonArray(
                    new JsonObject(), new JsonObject(), new JsonObject(),
                    new JsonObject { ["Files"] = new JsonObject { [clonedGamePath] = combiningRelative } }),
            }),
        }.ToJsonString());
        var combiningMemberships = PenumbraService.ReadOptionMembershipsForRegression(combiningRoot, combiningRelative);
        Require(combiningMemberships.Order().SequenceEqual(new[]
            {
                $"option:{combiningGroupId:D}:{combiningFirstId:D}",
                $"option:{combiningGroupId:D}:{combiningSecondId:D}",
            }.Order()),
            "Combining containers expose stable option GUID memberships instead of array indexes");
    }
}
