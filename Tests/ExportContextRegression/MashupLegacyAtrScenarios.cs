using System.Text.Json.Nodes;
using InstantEdit.Services.PenumbraMods;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// A new-mod mashup stages its mod, writes the attribute groups into it and then checks the staged
/// Meta Manipulations against what it expects to have written. A source mod exported before 2.0.0
/// carries its hair toggles on the Unknown slot, which the mashup moves to the Hair slot.
///
/// This follows MashupService's new-mod sequence step by step: stage the default data from the
/// manipulations it will report (converted, defaults added), write the attribute groups, validate against
/// that same list. Staging the source's raw manipulations instead made the validation fail for such a
/// source ("The mashup Meta Manipulations are incomplete"), so no mashup of it could be made.
/// </summary>
internal static class MashupLegacyAtrScenarios
{
    private const string HairPath = "chara/human/c0801/obj/hair/h0154/model/c0801h0154_hir.mdl";

    public static void Run(string root)
    {
        var staging = Path.Combine(root, "MashupLegacyAtr");
        var modelRelative = "Files/xiv-instant-edit/mashups/test/model.mdl";
        Directory.CreateDirectory(Path.Combine(staging, "Files", "xiv-instant-edit", "mashups", "test"));
        File.WriteAllBytes(Path.Combine(staging, modelRelative.Replace('/', Path.DirectorySeparatorChar)), [1, 2, 3]);
        var mappings = new Dictionary<string, string> { [HairPath] = modelRelative };

        // What the source mod's resolved manipulations hold: its toggle's default, on the invalid slot.
        var source = new JsonArray(LegacyHairAtr("atrx_bangs", false));
        string[] tags = ["atrx_bangs"];
        var expected = AttributeGroupService.ManipulationsWithAtrDefaults(source, HairPath, tags);

        File.WriteAllText(Path.Combine(staging, "meta.json"),
            ModMetadata.CreateV4ModMetadata("Legacy ATR Mashup", "XIV Instant Edit", "", "",
                MashupPlanning.CreateMashupDefaultData(mappings, expected)).ToJsonString());
        Require(AttributeGroupService.WriteAttributeGroupsForRegression(
                    staging, HairPath, tags, new Dictionary<string, int>(), source) is null,
            "the attribute groups of a mashup of a pre-2.0.0 hair export are written");

        string? problem = null;
        try
        {
            MashupPlanning.ValidateStagedMashupMod(staging, "Legacy ATR Mashup", mappings, expected);
        }
        catch (InvalidDataException error)
        {
            problem = error.Message;
        }
        Require(problem is null,
            $"the staged mashup of a pre-2.0.0 hair export passes its own validation ({problem})");

        var staged = JsonNode.Parse(File.ReadAllText(Path.Combine(staging, "meta.json")))!["DefaultData"]!["Manipulations"]!.AsArray()
            .Select(item => item!["Manipulation"]!).ToArray();
        Require(staged.Length > 0 && staged.All(atr => atr["Slot"]!.GetValue<string>() == "Hair"),
            "and none of its toggles is left on the Unknown slot, which Penumbra drops");
    }

    private static JsonObject LegacyHairAtr(string attribute, bool entry) => new()
    {
        ["Type"] = "Atr",
        ["Manipulation"] = new JsonObject
        {
            ["Entry"] = entry, ["Attribute"] = attribute,
            ["Slot"] = "Unknown", ["Id"] = 154, ["GenderRaceCondition"] = 0,
        },
    };
}
