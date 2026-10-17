using InstantEdit.Studio.Services;
using InstantEdit.Studio.Services.Check;
using InstantEdit.Studio.Services.Project;
using Newtonsoft.Json.Linq;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// Material sets whose materials only use 16×16 placeholder textures while another set has the real ones
/// (D`VIL's Death Grip on a0134: only v0004 had the corset's textures, the other bracelets showed the placeholders).
/// </summary>
internal static partial class ModCheckScenarios
{
    private static string SetMaterial(int set) => $"chara/equipment/e0005/material/v{set:D4}/mt_c0201e0005_top_a.mtrl";
    private static string SetTexture(int set) => $"chara/equipment/e0005/texture/v{set:D2}_c0201e0005_top_a_norm.tex";

    private static void RunPlaceholderSets()
    {
        FindsSetsWithOnlyPlaceholderTextures();
        MissingSetsCopyTheDetailedSet();
        SetsThatAreAllPlaceholdersAreFine();
    }

    /// <summary>Variants 1-3 use body sets 1-3; set 1 ships 16×16 textures, set 2 <paramref name="set2Side"/>, set 3 nothing.</summary>
    private static TestModFolder PlaceholderSetMod(int set2Side)
    {
        var mod = new TestModFolder()
            .File("files/model.mdl", ModCheckFixtures.Mdl(["/mt_c0201e0005_top_a.mtrl"]))
            .File("files/v1.mtrl", ModCheckFixtures.Mtrl([SetTexture(1)]))
            .File("files/v2.mtrl", ModCheckFixtures.Mtrl([SetTexture(2)]))
            .File("files/stub.tex", ModCheckFixtures.Tex(16, 16))
            .File("files/real.tex", ModCheckFixtures.Tex(set2Side, set2Side));
        mod.Meta(new JObject
        {
            ["FileVersion"] = 4,
            ["Name"] = "Placeholder sets",
            ["DefaultData"] = new JObject
            {
                ["Files"] = new JObject
                {
                    [GearModel] = "files/model.mdl",
                    [SetMaterial(1)] = "files/v1.mtrl",
                    [SetMaterial(2)] = "files/v2.mtrl",
                    [SetTexture(1)] = "files/stub.tex",
                    [SetTexture(2)] = "files/real.tex",
                },
            },
        }.ToString());
        return mod;
    }

    private static FakeVanillaFiles PlaceholderSetGame()
        => new FakeVanillaFiles()
            .Add("shader/sm5/shpk/character.shpk")
            .Add("chara/equipment/e0005/e0005.imc", ModCheckFixtures.Imc([GearRow(1, 0x3FF), GearRow(1, 0x3FF), GearRow(2, 0x3FF), GearRow(3, 0x3FF)]))
            .WithEqdp(5, new Dictionary<ushort, ushort> { [101] = 0x08, [201] = 0x08 });

    private static void FindsSetsWithOnlyPlaceholderTextures()
    {
        using var mod = PlaceholderSetMod(set2Side: 512);
        var report = Check(mod, PlaceholderSetGame());
        var finding = report.Findings.Single(f => f.Code == "V004");
        var fix = report.Find(finding.RepairIds.Single())!;
        Require(finding.Severity == Severity.Warning && finding.GamePath == "chara/equipment/e0005/material/v0001/" && finding.Detail.StartsWith("Variant 0, 1:", StringComparison.Ordinal),
            "set 1, whose only texture is 16×16 while set 2 has a 512 one, is reported for the variants that use it");
        Require(!fix.Preselected && fix.EditsFor(null).Single() == new RetargetFileEdit(OptionRef.Default, SetMaterial(1), "files/v1.mtrl", "files/v2.mtrl"),
            "the offered fix points set 1's material at set 2's file, and is not ticked");
        var rows = report.Variants.Single().Rows;
        Require(rows.Single(row => row.Variant == 1) is { Status: Severity.Warning, Summary: "Placeholder textures" } && rows.Single(row => row.Variant == 2).Status is null,
            "the variant table marks the placeholder set");
    }

    private static void MissingSetsCopyTheDetailedSet()
    {
        using var mod = PlaceholderSetMod(set2Side: 512);
        var report = Check(mod, PlaceholderSetGame());
        var finding = report.Findings.Single(f => f.Code == "V001");
        Require(finding.GamePath == "chara/equipment/e0005/material/v0003/"
                && report.Find(finding.RepairIds.Single())!.EditsFor(null).Single() == new AddFileEdit(OptionRef.Default, SetMaterial(3), "files/v2.mtrl"),
            "a missing set gets the materials of the set with real textures, not the placeholder set the first variant uses");
    }

    private static void SetsThatAreAllPlaceholdersAreFine()
    {
        using var mod = PlaceholderSetMod(set2Side: 16);
        var report = Check(mod, PlaceholderSetGame());
        Require(!report.Findings.Any(f => f.Code == "V004")
                && report.Find(report.Findings.Single(f => f.Code == "V001").RepairIds.Single())!.EditsFor(null).Single() == new AddFileEdit(OptionRef.Default, SetMaterial(3), "files/v1.mtrl"),
            "with no set that has real textures nothing is a placeholder set, and a missing set still copies the first variant's");
    }
}
