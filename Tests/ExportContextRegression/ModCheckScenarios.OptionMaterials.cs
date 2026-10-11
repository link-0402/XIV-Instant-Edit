using InstantEdit.Studio.Services;
using InstantEdit.Studio.Services.Check;
using InstantEdit.Studio.Services.Project;
using Newtonsoft.Json.Linq;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// A material that every option of a group ships in its own version, with its textures in a later group
/// (Pocky's Corset Blouse: the Leather material lost its mask whenever a Metal texture was picked).
/// </summary>
internal static partial class ModCheckScenarios
{
    private const string OptionNorm = "chara/equipment/e0005/texture/v01_c0201e0005_top_a_norm.tex";
    private const string OptionMask = "chara/equipment/e0005/texture/v01_c0201e0005_top_a_mask.tex";

    private static void RunOptionMaterials()
    {
        EveryVersionOfAMaterialIsChecked();
        AFallbackTextureGoesWhereTheOtherOptionsStillWin();
    }

    /// <summary>Model in Sizes; Material › Metal and Leather ship different files; Texture › Metal - 1 and Leather - 1 ship the textures.</summary>
    private static TestModFolder OptionMaterialMod(bool texturesFirst = false)
    {
        var mod = new TestModFolder()
            .File("files/model.mdl", ModCheckFixtures.Mdl(["/mt_c0201e0005_top_a.mtrl"]))
            .File("files/metal.mtrl", ModCheckFixtures.Mtrl([OptionNorm, OptionMask]))
            .File("files/leather.mtrl", ModCheckFixtures.Mtrl([OptionNorm, OptionMask, GearTexture]))
            .File("files/base.tex", ModCheckFixtures.Tex())
            .File("files/metal_mask.tex", ModCheckFixtures.Tex())
            .File("files/metal_norm.tex", ModCheckFixtures.Tex())
            .File("files/metal_norm2.tex", ModCheckFixtures.Tex())
            .File("files/leather_norm.tex", ModCheckFixtures.Tex())
            .File("files/leather_mask.tex", ModCheckFixtures.Tex());
        static JObject Option(string name, JObject files) => new() { ["Name"] = name, ["Files"] = files };
        var materials = new JObject
        {
            ["Type"] = "Single",
            ["Name"] = "Material",
            ["Options"] = new JArray(
                Option("Metal", new JObject { [GearMaterial] = "files/metal.mtrl", [OptionMask] = "files/metal_mask.tex" }),
                Option("Leather", new JObject { [GearMaterial] = "files/leather.mtrl" })),
        };
        var textures = new JObject
        {
            ["Type"] = "Single",
            ["Name"] = "Texture",
            ["Options"] = new JArray(
                Option("Metal - 1", new JObject { [OptionNorm] = "files/metal_norm.tex" }),
                Option("Metal - 2", new JObject { [OptionNorm] = "files/metal_norm2.tex" }),
                new JObject { ["Name"] = "--------" },
                Option("Leather - 1", new JObject { [OptionNorm] = "files/leather_norm.tex", [OptionMask] = "files/leather_mask.tex" })),
        };
        mod.Meta(new JObject
        {
            ["FileVersion"] = 4,
            ["Name"] = "Corset",
            ["DefaultData"] = new JObject { ["Files"] = new JObject { [GearTexture] = "files/base.tex" } },
            ["Groups"] = new JArray(
                new JObject { ["Type"] = "Single", ["Name"] = "Sizes", ["Options"] = new JArray(Option("S", new JObject { [GearModel] = "files/model.mdl" })) },
                texturesFirst ? textures : materials,
                texturesFirst ? materials : textures),
        }.ToString());
        return mod;
    }

    private static CheckFinding LeatherMaskFinding(ModCheckReport report)
        => report.Findings.Single(f => f.Code == "C006" && f.GamePath == OptionMask && f.Detail.Contains("in Material › Leather", StringComparison.Ordinal));

    private static void EveryVersionOfAMaterialIsChecked()
    {
        using var mod = OptionMaterialMod();
        var report = Check(mod);

        var finding = LeatherMaskFinding(report);
        Require(finding.Severity == Severity.Warning && finding.Detail.Contains("Texture › Leather - 1", StringComparison.Ordinal)
                && !finding.Detail.Contains("Material › Metal", StringComparison.Ordinal),
            "the Leather version of a material every option ships is checked too, and its missing mask names only options that can be on with it");
        Require(!report.Findings.Any(f => f.GamePath == OptionMask && f.Detail.Contains("in Material › Metal", StringComparison.Ordinal)),
            "the Metal version brings its own mask, so it has no finding for it");

        var norm = report.Findings.Single(f => f.Code == "C006" && f.GamePath == OptionNorm && f.Detail.Contains("in Material › Metal", StringComparison.Ordinal));
        Require(norm.Detail.Contains("every Texture option provides except \"--------\"", StringComparison.Ordinal),
            "a texture every option of a group but a separator provides names the separator instead of listing the others");
        Require(report.Find(norm.RepairIds.Single())!.EditsFor(null).Single() == new AddFileEdit(new OptionRef("#1", "#0"), OptionNorm, "files/metal_norm.tex"),
            "the fallback for the Metal material is a Metal texture, not the alphabetically first one");
    }

    private static void AFallbackTextureGoesWhereTheOtherOptionsStillWin()
    {
        using (var mod = OptionMaterialMod())
        {
            var report = Check(mod);
            var fix = report.Find(LeatherMaskFinding(report).RepairIds.Single())!;
            Require(!fix.Preselected && fix.EditsFor(null).Single() == new AddFileEdit(new OptionRef("#1", "#1"), OptionMask, "files/leather_mask.tex"),
                "the fix copies the leather mask into Material › Leather, unticked; the later Texture group still wins over it");
        }
        using (var mod = OptionMaterialMod(texturesFirst: true))
        {
            var report = Check(mod);
            var fix = report.Find(LeatherMaskFinding(report).RepairIds.Single())!;
            Require(fix.EditsFor(null).Single() == new AddFileEdit(OptionRef.Default, OptionMask, "files/leather_mask.tex"),
                "when the material's group would win over the texture options, the fallback goes into the default files instead");
        }
    }
}
