using InstantEdit.Studio.Models;
using InstantEdit.Studio.Services;
using InstantEdit.Studio.Services.Check;
using InstantEdit.Studio.Services.Project;
using Newtonsoft.Json.Linq;
using static InstantEdit.TestSupport.Assertions;

/// <summary>Which repairs the check offers, and which it ticks. Ported from Mod Optimizer's ModAnalyzerTests.</summary>
internal static partial class ModCheckScenarios
{
    private static void RunRepairs()
    {
        MissingFilesCanBeRetargeted();
        DeadRaceModelsGetTheirEqdpBit();
        RacesWithTheirOwnModelCanOptIntoTheModModel();
        TextureAndSkinPacksAreNeverTreatedAsLeftovers();
        JunkKeysAreRemovedWhenTheRealPathExists();
        DisabledModsAndOverriddenFilesAreReported();
    }

    private static void MissingFilesCanBeRetargeted()
    {
        using var mod = GearMod();
        mod.File("moved/base.tex", ModCheckFixtures.Tex());
        File.Delete(Path.Combine(mod.Path, "files", "base.tex"));
        var report = Check(mod);

        var finding = report.Findings.Single(f => f.Code == "S006");
        var fix = report.Find(finding.RepairIds.Single())!;
        Require(fix.Preselected && fix.EditsFor(null).Single() == new RetargetFileEdit(OptionRef.Default, GearTexture, "files/base.tex", "moved/base.tex"),
            "a redirection to a missing file is pointed at the one file of that name elsewhere in the mod");
    }

    private static void DeadRaceModelsGetTheirEqdpBit()
    {
        using var mod = GearMod();
        var meta = JObject.Parse(mod.ReadMeta());
        meta["DefaultData"]!["Files"]!["chara/equipment/e0005/model/c1401e0005_top.mdl"] = "files/model.mdl";
        mod.Meta(meta.ToString());

        var report = Check(mod);
        var fix = report.Find(report.Findings.Single(f => f.Code == "R001").RepairIds.Single())!;
        var edit = (AddManipulationEdit)fix.Edits.Single();
        Require(fix.Preselected && edit.Option.IsDefault &&
                JToken.DeepEquals(edit.Manipulation["Manipulation"],
                    new JObject { ["Gender"] = "Female", ["Race"] = "AuRa", ["SetId"] = 5, ["Slot"] = "Body", ["Entry"] = 8 }),
            "a race model no race loads gets the EQDP entry that turns it on, ticked");
    }

    private static void RacesWithTheirOwnModelCanOptIntoTheModModel()
    {
        using var mod = GearMod();
        var game = GearGame().WithEqdp(5, new Dictionary<ushort, ushort> { [101] = 0x08, [201] = 0x08, [1401] = 0x0C });

        var report = Check(mod, game, player: 1401);

        Require(report.Findings.Count(f => f.Code == "R003") == 1, "the player's race not wearing the mod's model is a warning");
        var rows = report.Races.Single().Rows;
        var row = rows.Single(r => r.Race == 1401);
        var reroute = report.Find(row.RepairId!)!;
        var edit = (AddManipulationEdit)reroute.Edits.Single();
        Require(!row.UsesModModel && !reroute.Preselected && edit.Manipulation["Manipulation"]!["Entry"]!.Value<int>() == 4,
            "a race with its own model can be pointed at the mod's model, unticked");
        Require(rows.Single(r => r.Race == 1201).RepairId is null, "a race that never had its own model needs no rerouting");
    }

    private static void TextureAndSkinPacksAreNeverTreatedAsLeftovers()
    {
        using var textures = new TestModFolder().File("x/a.tex", ModCheckFixtures.Tex());
        textures.Meta("{\"FileVersion\": 4, \"DefaultData\": {\"Files\": {\"chara/equipment/e0005/texture/custom_name.tex\": \"x/a.tex\"}}}");
        Require(!Check(textures).Findings.Any(f => f.Category == CheckCategory.Leftovers),
            "a texture pack's textures are never leftovers");

        const string skin = "chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_bibo.mtrl";
        const string skinTexture = "chara/human/c0201/obj/body/b0001/texture/c0201b0001_bibo_d.tex";
        using var skins = new TestModFolder()
            .File("m/skin.mtrl", ModCheckFixtures.Mtrl([skinTexture, "chara/human/c0201/obj/body/b0001/texture/missing_n.tex"]))
            .File("t/d.tex", ModCheckFixtures.Tex());
        skins.Meta(new JObject
        {
            ["FileVersion"] = 4,
            ["DefaultData"] = new JObject { ["Files"] = new JObject { [skin] = "m/skin.mtrl", [skinTexture] = "t/d.tex" } },
        }.ToString());
        var report = Check(skins);
        Require(!report.Findings.Any(f => f.Category == CheckCategory.Leftovers) &&
                report.Findings.Single(f => f.Code == "C002").GamePath == "chara/human/c0201/obj/body/b0001/texture/missing_n.tex",
            "a skin mod's materials are still checked, but nothing in it is a leftover");
    }

    private static void JunkKeysAreRemovedWhenTheRealPathExists()
    {
        const string real = "chara/human/c0201/obj/body/b0001/texture/c0201b0001_b_d.tex";
        using var mod = new TestModFolder().File("Skin/Detailed/d.tex", ModCheckFixtures.Tex()).File("Skin/Other/x.tex", ModCheckFixtures.Tex());
        mod.Meta(new JObject
        {
            ["FileVersion"] = 4,
            ["DefaultData"] = new JObject
            {
                ["Files"] = new JObject
                {
                    ["Core Files_ Skin/Detailed/" + real] = "Skin/Detailed/d.tex",
                    ["Core Files_ Skin/Other/chara/human/c0201/obj/body/b0001/texture/unique.tex"] = "Skin/Other/x.tex",
                },
            },
            ["Groups"] = new JArray(new JObject
            {
                ["Type"] = "Single",
                ["Name"] = "Skin",
                ["Options"] = new JArray(new JObject { ["Name"] = "Detailed", ["Files"] = new JObject { [real] = "Skin/Detailed/d.tex" } }),
            }),
        }.ToString());

        var report = Check(mod);
        var junk = report.Findings.Where(f => f.Code == "S005").ToArray();
        var duplicated = junk.Single(f => f.GamePath!.Contains("detailed", StringComparison.OrdinalIgnoreCase));
        var unique = junk.Single(f => f.GamePath!.Contains("unique", StringComparison.Ordinal));
        Require(junk.Length == 2 && report.Find(duplicated.RepairIds.Single())!.Preselected,
            "a game path with folders glued in front is removed when the real path is redirected elsewhere");
        Require(unique.RepairIds.Count == 2 && unique.RepairIds.All(id => !report.Find(id)!.Preselected) &&
                unique.RepairIds.Select(id => report.Find(id)!.ExclusiveGroup).Distinct().Count() == 1,
            "otherwise fixing its key and removing it are offered as a choice, unticked");
    }

    private static void DisabledModsAndOverriddenFilesAreReported()
    {
        using var mod = GearMod();
        Require(Check(mod, collection: new FakeCollectionView(new Dictionary<string, string>(), enabled: false))
                    .Findings.Single(f => f.Code == "X003").Severity == Severity.Warning,
            "a mod that is off in the player's collection is a warning");

        var overridden = new FakeCollectionView(new Dictionary<string, string> { [GearTexture] = @"X:\Penumbra\Better Textures\base.tex" });
        Require(Check(mod, collection: overridden).Findings.Single(f => f.Code == "X004").Detail.Contains("Better Textures", StringComparison.Ordinal),
            "a file another mod replaces in the collection names that mod");
    }
}
