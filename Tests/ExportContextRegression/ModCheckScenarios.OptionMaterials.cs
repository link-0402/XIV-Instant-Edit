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
        EachModelOptionIsJudgedByItself();
        MaterialOptionsShareOneDefaultFallback();
        OptionsShippingOneMaterialShareAFinding();
        TheWinningOptionsRedirectionIsChecked();
    }

    private static void EachModelOptionIsJudgedByItself()
    {
        // Whichever size's model is walked first, the M model's missing mask is reported, and only for M.
        foreach (var order in new[] { "SM", "MS" })
        {
            using var mod = OptionMaterialMod(sizes: order);
            var report = Check(mod);
            var masks = report.Findings.Where(f => f.GamePath == OptionMask && f.Detail.Contains("in Material › Leather", StringComparison.Ordinal)).ToArray();
            Require(masks.Length == 1 && masks[0].Code == "C006" && masks[0].Detail.Contains("with the model from Sizes › M", StringComparison.Ordinal)
                    && !masks[0].Detail.Contains("Sizes › S", StringComparison.Ordinal),
                $"({order}) the Leather mask depends on another option only with the M model, and the finding names it; S brings its own mask");
            Require(report.Find(masks[0].RepairIds.Single())!.EditsFor(null).Single() == new AddFileEdit(OptionRef.Default, OptionMask, "files/leather_mask.tex"),
                $"({order}) the fallback goes into the default files, so S's own mask still wins over it");
        }
    }

    private static void MaterialOptionsShareOneDefaultFallback()
    {
        using var mod = OptionMaterialMod(texturesFirst: true, cloth: "files/cloth.mtrl");
        var report = Check(mod);
        var leather = LeatherMaskFinding(report);
        var cloth = report.Findings.Single(f => f.Code == "C006" && f.GamePath == OptionMask && f.Detail.Contains("in Material › Cloth", StringComparison.Ordinal));
        var fallbacks = report.Repairs.Where(r => r.EditsFor(null).Any(e => e is AddFileEdit { GamePath: OptionMask } add && add.Option == OptionRef.Default)).ToArray();
        Require(fallbacks.Length == 1 && leather.RepairIds.Single() == fallbacks[0].Id && cloth.RepairIds.Single() == fallbacks[0].Id,
            "two material options missing one texture share one fallback in the default files, not two that write the same redirection");
    }

    private static void OptionsShippingOneMaterialShareAFinding()
    {
        using var mod = OptionMaterialMod(cloth: "files/leather.mtrl");
        var report = Check(mod);
        var masks = report.Findings.Where(f => f.GamePath == OptionMask && f.Code == "C006").ToArray();
        Require(masks.Length == 1 && masks[0].Detail.Contains("in Material › Leather, Material › Cloth (mask)", StringComparison.Ordinal),
            "options shipping the same material file get one finding that names them together");
        Require(masks[0].RepairIds.Select(id => report.Find(id)!.EditsFor(null).Single().Option).ToHashSet().SetEquals([new OptionRef("#1", "#1"), new OptionRef("#1", "#2")]),
            "each of those options still gets its own fallback copy");
    }

    /// <summary>Material › Leather swaps the normal map to a file nobody has; the later Sizes › S redirects it to a file of its own.</summary>
    private static TestModFolder SwapAndRedirectMod(int materialPriority)
    {
        var mod = new TestModFolder()
            .File("files/model.mdl", ModCheckFixtures.Mdl(["/mt_c0201e0005_top_a.mtrl"]))
            .File("files/leather.mtrl", ModCheckFixtures.Mtrl([OptionNorm, OptionMask]))
            .File("files/leather_mask.tex", ModCheckFixtures.Tex())
            .File("files/s_norm.tex", ModCheckFixtures.Tex());
        mod.Meta(new JObject
        {
            ["FileVersion"] = 4,
            ["Name"] = "Corset",
            ["Groups"] = new JArray(
                new JObject
                {
                    ["Type"] = "Single", ["Name"] = "Material", ["Priority"] = materialPriority,
                    ["Options"] = new JArray(new JObject
                    {
                        ["Name"] = "Leather",
                        ["Files"] = new JObject { [GearMaterial] = "files/leather.mtrl", [OptionMask] = "files/leather_mask.tex" },
                        ["FileSwaps"] = new JObject { [OptionNorm] = "chara/equipment/e0005/texture/v01_c0201e0005_top_z_norm.tex" },
                    }),
                },
                new JObject
                {
                    ["Type"] = "Single", ["Name"] = "Sizes",
                    ["Options"] = new JArray(new JObject
                    {
                        ["Name"] = "S", ["Files"] = new JObject { [GearModel] = "files/model.mdl", [OptionNorm] = "files/s_norm.tex" },
                    }),
                }),
        }.ToString());
        return mod;
    }

    private static void TheWinningOptionsRedirectionIsChecked()
    {
        using (var mod = SwapAndRedirectMod(materialPriority: 0))
            Require(!Check(mod).Findings.Any(f => f.GamePath == OptionNorm && f.Severity == Severity.Error),
                "the model's later group wins the normal map, so the material's broken swap is not what the game loads");
        using (var mod = SwapAndRedirectMod(materialPriority: 1))
            Require(Check(mod).Findings.Any(f => f.Code == "C002" && f.GamePath == OptionNorm),
                "with a higher priority the material's group wins, and its swap to a file nobody has is reported");
    }

    /// <summary>
    /// Model in Sizes; Material › Metal and Leather ship different files; Texture › Metal - 1 and Leather - 1 ship the textures.
    /// <paramref name="sizes"/> "SM" or "MS" puts the model in Sizes › S and M too, in that order, with only S bringing a mask;
    /// <paramref name="cloth"/> adds Material › Cloth with that material file.
    /// </summary>
    private static TestModFolder OptionMaterialMod(bool texturesFirst = false, string? sizes = null, string? cloth = null)
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
        if (sizes is not null)
            mod.File("files/s_mask.tex", ModCheckFixtures.Tex());
        if (cloth == "files/cloth.mtrl")
            mod.File(cloth, ModCheckFixtures.Mtrl([OptionNorm, OptionMask]));
        static JObject Option(string name, JObject files) => new() { ["Name"] = name, ["Files"] = files };
        var materialOptions = new JArray(
            Option("Metal", new JObject { [GearMaterial] = "files/metal.mtrl", [OptionMask] = "files/metal_mask.tex" }),
            Option("Leather", new JObject { [GearMaterial] = "files/leather.mtrl" }));
        if (cloth is not null)
            materialOptions.Add(Option("Cloth", new JObject { [GearMaterial] = cloth }));
        var materials = new JObject
        {
            ["Type"] = "Single",
            ["Name"] = "Material",
            ["Options"] = materialOptions,
        };
        var small = Option("S", new JObject { [GearModel] = "files/model.mdl", [OptionMask] = "files/s_mask.tex" });
        var medium = Option("M", new JObject { [GearModel] = "files/model.mdl" });
        var sizeOptions = sizes switch
        {
            "SM" => new JArray(small, medium),
            "MS" => new JArray(medium, small),
            _    => new JArray(Option("S", new JObject { [GearModel] = "files/model.mdl" })),
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
                new JObject { ["Type"] = "Single", ["Name"] = "Sizes", ["Options"] = sizeOptions },
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
