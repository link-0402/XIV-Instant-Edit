using InstantEdit.Studio.Models;
using InstantEdit.Studio.Services;
using InstantEdit.Studio.Services.Check;
using InstantEdit.Studio.Services.Project;
using Newtonsoft.Json.Linq;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// The mod check on synthetic mods: how it reads a mod's options, what it finds, and which repairs
/// it offers. Ported from Mod Optimizer's ModDocumentModelTests and ModAnalyzerTests.
/// </summary>
internal static partial class ModCheckScenarios
{
    private const string GearModel = "chara/equipment/e0005/model/c0201e0005_top.mdl";
    private const string GearMaterial = "chara/equipment/e0005/material/v0001/mt_c0201e0005_top_a.mtrl";
    private const string GearTexture = "chara/equipment/e0005/texture/mt_c0201e0005_top_a_base.tex";
    private const string GroupGuid = "11111111-1111-1111-1111-111111111111";

    public static void RunAnalysis(string testRoot)
    {
        ReadsWhatPenumbraSkips();
        BuildsContainersForEveryGroupType();
        BuildsContainersForLegacyMods();
        EditsKeepGuidsAndUnknownFields();
        MaterialOnlyInASiblingOptionIsBroken();
        MaterialInEveryOptionOfASingleGroupIsAlwaysProvided();
        MaterialOnlyInAMultiOptionIsConditional();
        RunOptionMaterials();
        HealthyModHasNoFindings();
        Dx11TexturesAreRequestedWithTheirPrefix();
        ReportsBrokenMaterialsAndFiles();
        ImcGroupsDecideMaterialSets();
        AttributeMasksThatHideEverythingAreReported();
        RaceModelsNoRaceLoadsAreErrors();
        RunRepairs();
        RunStellarBlade();
    }

    // ── Reading a mod's options ──────────────────────────────────────────────

    private static void ReadsWhatPenumbraSkips()
    {
        using var mod = new TestModFolder().File("files/b.tex", ModCheckFixtures.Tex()).File("files/c.tex", ModCheckFixtures.Tex());
        mod.Meta(new JObject
        {
            ["FileVersion"] = 4,
            ["DefaultData"] = new JObject
            {
                ["Files"] = new JObject
                {
                    ["chara/a.tex"] = 5,
                    ["chara/b.tex"] = "files/b.tex",
                    ["chara//c.tex"] = "files/c.tex",
                    ["chara/d.tex"] = "../outside.tex",
                },
                ["Manipulations"] = new JArray("not a manipulation"),
            },
            ["Groups"] = new JArray("not a group"),
        }.ToString());

        var model = CheckModel.Build(PenumbraModMeta.Load(mod.Path), mod.Path);

        Require(model.Mappings.Single().Key == "chara/b.tex", "only well-formed redirections are mappings");
        Require(model.Issues.Select(issue => issue.Kind).Order().SequenceEqual(new[]
                {
                    MetaIssueKind.NonStringMapping, MetaIssueKind.EscapingPath, MetaIssueKind.InvalidGamePath,
                    MetaIssueKind.BadGroup, MetaIssueKind.MalformedManipulation,
                }.Order()),
            "redirections, groups and manipulations Penumbra skips are recorded as issues");
        Require(model.SkippedMappings.Single().RelPath == "files/c.tex", "a file behind a skipped redirection still counts as used");
    }

    private static JObject CurrentMeta() => new()
    {
        ["FileVersion"] = 4,
        ["Name"] = "Test",
        ["DefaultData"] = new JObject
        {
            ["Files"] = new JObject(),
            ["Manipulations"] = new JArray(MetaManipulations.Eqdp(201, 1, EquipSlot.Body, 3)),
        },
        ["Groups"] = new JArray(
            new JObject
            {
                ["Type"] = "Single",
                ["Id"] = GroupGuid,
                ["Name"] = "Colors",
                ["Options"] = new JArray(
                    new JObject { ["Name"] = "Red", ["CustomFlag"] = true, ["Files"] = new JObject { ["chara/red.tex"] = @"chara\a.tex" } },
                    new JObject { ["Name"] = "Blue", ["Files"] = new JObject { ["chara/blue.tex"] = "chara/a.tex" } }),
            },
            new JObject
            {
                ["Type"] = "Multi",
                ["Name"] = "Extras",
                ["Options"] = new JArray(new JObject { ["Name"] = "Glow", ["Priority"] = 5 }),
            },
            new JObject
            {
                ["Type"] = "Combining",
                ["Name"] = "Parts",
                ["Options"] = new JArray(new JObject { ["Name"] = "Top" }, new JObject { ["Name"] = "Legs" }),
                ["Containers"] = new JArray(new JObject(), new JObject(), new JObject(), new JObject()),
            },
            new JObject
            {
                ["Type"] = "Imc",
                ["Name"] = "Toggles",
                ["Identifier"] = new JObject { ["ObjectType"] = "Equipment", ["PrimaryId"] = 878, ["Variant"] = 1, ["EquipSlot"] = "Legs" },
                ["DefaultEntry"] = new JObject { ["MaterialId"] = 1, ["AttributeMask"] = 1023 },
                ["AllVariants"] = true,
                ["OnlyAttributes"] = true,
                ["DefaultSettings"] = 2,
                ["Options"] = new JArray(new JObject { ["Name"] = "A", ["AttributeMask"] = 1 }, new JObject { ["Name"] = "B", ["AttributeMask"] = 2 }),
            }),
    };

    private static void BuildsContainersForEveryGroupType()
    {
        using var mod = new TestModFolder();
        mod.Meta(CurrentMeta().ToString());

        var model = CheckModel.Build(PenumbraModMeta.Load(mod.Path), mod.Path);
        var containers = model.Containers;

        Require(containers.Select(c => c.DisplayName).SequenceEqual(
                ["Default files", "Colors › Red", "Colors › Blue", "Extras › Glow", "Parts › (none)", "Parts › Top", "Parts › Legs", "Parts › Top + Legs"]),
            "every option and every combining container is a place files can go, after the default files");
        Require(containers[0].Option.IsDefault && containers[2].Option == new OptionRef(GroupGuid, "#1"),
            "containers are named by the option ids the rest of the Mod Browser uses");
        Require(containers[1].IsExclusiveWith(containers[2]) && !containers[3].IsExclusiveWith(containers[3]) &&
                !containers[0].IsExclusiveWith(containers[1]) && containers[5].IsExclusiveWith(containers[7]),
            "options of one single-choice group and containers of one combining group are never on together");
        Require(containers[3].OptionPriority == 5, "option priorities are read");

        var imc = model.ImcGroups.Single();
        Require(imc.Identifier.PrimaryId == 878 && imc.DefaultMask() == (0x3FF ^ 0x002),
            "an IMC group's default mask is its entry's mask with the default options toggled");
        Require(model.Manipulations.Single().Parsed.Type == MetaType.Eqdp, "manipulations are read with their type");
    }

    private static void BuildsContainersForLegacyMods()
    {
        using var mod = new TestModFolder();
        mod.Meta("{\"FileVersion\": 3, \"Name\": \"Old\"}");
        File.WriteAllText(Path.Combine(mod.Path, "default_mod.json"), "{\"Files\": {}, \"FileSwaps\": {}, \"Manipulations\": []}");
        File.WriteAllText(Path.Combine(mod.Path, "group_001_colors.json"),
            "{\"Name\": \"Colors\", \"Type\": \"Single\", \"Options\": [{\"Name\": \"Red\", \"Files\": {}}, {\"Name\": \"Blue\"}]}");

        var containers = CheckModel.Build(PenumbraModMeta.Load(mod.Path), mod.Path).Containers;

        Require(containers.Select(c => c.DisplayName).SequenceEqual(["Default files", "Colors › Red", "Colors › Blue"]),
            "a mod in the old split layout has the same containers");
    }

    private static void EditsKeepGuidsAndUnknownFields()
    {
        using var mod = new TestModFolder();
        mod.Meta(CurrentMeta().ToString());
        var meta = PenumbraModMeta.Load(mod.Path);
        var red = new OptionRef(GroupGuid, "#0");

        meta.SetFile(red, "chara/new/path.tex", @"chara\b.tex");
        meta.RemoveFile(new OptionRef(GroupGuid, "#1"), "chara/blue.tex");
        meta.UpsertManipulation(red, MetaManipulations.Eqdp(1401, 128, EquipSlot.Body, 3));
        meta.RemoveManipulation(OptionRef.Default, PenumbraModMeta.IdentityOf(MetaManipulations.Eqdp(201, 1, EquipSlot.Body, 3)));
        meta.Save(mod.Path, backupFirst: false);

        var saved = JObject.Parse(mod.ReadMeta());
        var redOption = (JObject)saved["Groups"]![0]!["Options"]![0]!;
        Require(saved["Groups"]![0]!["Id"]!.Value<string>() == GroupGuid && redOption.Value<bool>("CustomFlag"),
            "edits keep group ids and fields nothing here knows about");
        Require(redOption["Files"]!["chara/new/path.tex"]!.Value<string>() == @"chara\b.tex" &&
                redOption["Manipulations"]![0]!["Manipulation"]!["Entry"]!.Value<int>() == 12 &&
                ((JArray)saved["DefaultData"]!["Manipulations"]!).Count == 0 &&
                saved["Groups"]![0]!["Options"]![1]!["Files"]!["chara/blue.tex"] is null,
            "files and manipulations are added and removed in the option they belong to");
    }

    // ── Option semantics ─────────────────────────────────────────────────────

    private static FakeVanillaFiles GearGame(byte bodyMaterialSet = 1, ushort mask = 0x3FF)
        => new FakeVanillaFiles()
            .Add("shader/sm5/shpk/character.shpk")
            .Add("chara/equipment/e0005/e0005.imc", ModCheckFixtures.Imc([GearRow(1, 0x3FF), GearRow(bodyMaterialSet, mask)]))
            .WithEqdp(5, new Dictionary<ushort, ushort> { [101] = 0x08, [201] = 0x08 });

    private static (byte, ushort)[] GearRow(byte set, ushort mask) => [(1, 0x3FF), (set, mask), (1, 0x3FF), (1, 0x3FF), (1, 0x3FF)];

    private static ModCheckReport Check(TestModFolder mod, FakeVanillaFiles? game = null, ushort? player = 201,
        IItemVariants? items = null, ICollectionView? collection = null)
        => ModCheck.Run(CheckInput.ForFolder(mod.Path, game ?? GearGame(), items, player, collection), null, default);

    /// <summary>A gear mod: model, one material, one texture, each in the containers its argument names.</summary>
    private static TestModFolder GearMod(string modelIn = "default", string materialIn = "default", string textureIn = "default",
        string groupType = "Single", byte[]? model = null, byte[]? material = null)
    {
        var mod = new TestModFolder()
            .File("files/model.mdl", model ?? ModCheckFixtures.Mdl(["/mt_c0201e0005_top_a.mtrl"]))
            .File("files/material.mtrl", material ?? ModCheckFixtures.Mtrl([GearTexture]))
            .File("files/base.tex", ModCheckFixtures.Tex());
        var containers = new Dictionary<string, JObject> { ["default"] = new(), ["a"] = new(), ["b"] = new() };
        void Map(string where, string gamePath, string relative)
        {
            foreach (var target in where.Split(','))
                ((JObject)(containers[target]["Files"] ??= new JObject()))[gamePath] = relative;
        }
        Map(modelIn, GearModel, "files/model.mdl");
        Map(materialIn, GearMaterial, "files/material.mtrl");
        Map(textureIn, GearTexture, "files/base.tex");
        mod.Meta(new JObject
        {
            ["FileVersion"] = 4,
            ["Name"] = "Gear",
            ["DefaultData"] = containers["default"],
            ["Groups"] = new JArray(new JObject
            {
                ["Type"] = groupType,
                ["Name"] = "Style",
                ["Options"] = new JArray(new JObject(containers["a"]) { ["Name"] = "A" }, new JObject(containers["b"]) { ["Name"] = "B" }),
            }),
        }.ToString());
        return mod;
    }

    private static readonly OptionRef StyleA = new("#0", "#0");

    private static void MaterialOnlyInASiblingOptionIsBroken()
    {
        using var mod = GearMod(modelIn: "a", materialIn: "b");
        var report = Check(mod);

        var finding = report.Findings.Single(f => f.Code == "C001");
        Require(finding.Severity == Severity.Error && finding.Detail.Contains("never active together", StringComparison.Ordinal),
            "a material only a sibling single-choice option provides is an error for the option that needs it");
        var fix = report.Find(finding.RepairIds.Single())!;
        Require(fix.Preselected && fix.EditsFor(null).Single() == new AddFileEdit(StyleA, GearMaterial, "files/material.mtrl"),
            "the fix redirects the material in the option that needs it, and is ticked");
    }

    private static void MaterialInEveryOptionOfASingleGroupIsAlwaysProvided()
    {
        using var mod = GearMod(materialIn: "a,b");
        Require(!Check(mod).Findings.Any(f => f.Code is "C001" or "C006"),
            "a material every option of a single-choice group provides is always there");
    }

    private static void MaterialOnlyInAMultiOptionIsConditional()
    {
        using var mod = GearMod(materialIn: "a", groupType: "Multi");
        var report = Check(mod);

        var finding = report.Findings.Single(f => f.Code == "C006");
        Require(finding.Severity == Severity.Warning && finding.RepairIds.All(id => !report.Find(id)!.Preselected),
            "a material only a multi-choice option provides is a warning, and copying it is left unticked");
    }

    // ── Chains, variants and races ───────────────────────────────────────────

    private static void HealthyModHasNoFindings()
    {
        using var mod = GearMod();
        var report = Check(mod);
        Require(report.Count(Severity.Error) == 0 && report.Count(Severity.Warning) == 0 && !report.Repairs.Any(r => r.Preselected),
            "a healthy gear mod has no errors, no warnings and nothing to repair");
        Require(report.Variants.Single().Rows.All(row => row.Status is null) && report.Races.Single().Rows.Count == 18,
            "its variant table is clean, and its race table lists every playable race");
    }

    private static void Dx11TexturesAreRequestedWithTheirPrefix()
    {
        using var mod = GearMod(material: ModCheckFixtures.Mtrl([GearTexture], dx11: [true]));
        var report = Check(mod);

        const string prefixed = "chara/equipment/e0005/texture/--mt_c0201e0005_top_a_base.tex";
        var finding = report.Findings.Single(f => f.Code == "C002");
        var fix = report.Find(finding.RepairIds.Single())!;
        Require(finding.GamePath == prefixed && fix.Preselected &&
                fix.EditsFor(null).Single() == new AddFileEdit(OptionRef.Default, prefixed, "files/base.tex"),
            "a DX11 texture is asked for with the -- prefix, and its twin without it is the certain fix");
    }

    private static void ReportsBrokenMaterialsAndFiles()
    {
        using var mod = GearMod(material: ModCheckFixtures.Mtrl([GearTexture], shader: "missing.shpk",
            samplers: [new ModCheckFixtures.Sampler(ModCheckFixtures.SamplerDiffuse, 3)]));
        mod.File("files/base.tex", ModCheckFixtures.Png());
        var report = Check(mod);

        Require(report.Findings.Count(f => f.Code == "C005") == 1, "a shader that doesn't exist is reported");
        Require(report.Findings.Count(f => f.Code == "C004") == 1, "a sampler pointing past the material's textures is reported");
        Require(report.Findings.Single(f => f.Code == "F001").Detail.Contains("PNG", StringComparison.Ordinal),
            "a PNG renamed to .tex is reported as a PNG");
    }

    private static void ImcGroupsDecideMaterialSets()
    {
        using var mod = GearMod();
        var meta = JObject.Parse(mod.ReadMeta());
        ((JArray)meta["Groups"]!).Add(new JObject
        {
            ["Type"] = "Imc",
            ["Name"] = "Sets",
            ["Identifier"] = new JObject { ["ObjectType"] = "Equipment", ["PrimaryId"] = 5, ["Variant"] = 1, ["EquipSlot"] = "Body" },
            ["DefaultEntry"] = new JObject { ["MaterialId"] = 1, ["AttributeMask"] = 1023 },
            ["AllVariants"] = true,
            ["OnlyAttributes"] = false,
            ["Options"] = new JArray(),
        });

        Require(Check(mod, GearGame(bodyMaterialSet: 2)).Findings.Count(f => f.Code is "V001" or "C001") == 1,
            "a variant whose material set the mod doesn't provide is reported");
        mod.Meta(meta.ToString());
        Require(!Check(mod, GearGame(bodyMaterialSet: 2)).Findings.Any(f => f.Code is "V001" or "C001"),
            "an IMC group that points every variant at a provided material set fixes it");
    }

    private static void AttributeMasksThatHideEverythingAreReported()
    {
        using var mod = GearMod(model: ModCheckFixtures.Mdl(["/mt_c0201e0005_top_a.mtrl"],
            [new ModCheckFixtures.Mesh(0, new ModCheckFixtures.Submesh("atr_tv_b"), new ModCheckFixtures.Submesh("atr_tv_b", "atr_ude"))]));
        Require(Check(mod, GearGame(mask: 0x3FD)).Findings.Count(f => f.Code == "V003") == 1,
            "an attribute mask that hides every part of the model is reported");
        Require(!Check(mod, GearGame(mask: 0x3FF)).Findings.Any(f => f.Code == "V003"),
            "a mask that leaves a part visible is fine");
    }

    private static void RaceModelsNoRaceLoadsAreErrors()
    {
        using var mod = GearMod();
        var meta = JObject.Parse(mod.ReadMeta());
        meta["DefaultData"]!["Files"]!["chara/equipment/e0005/model/c1401e0005_top.mdl"] = "files/model.mdl";
        mod.Meta(meta.ToString());

        var report = Check(mod, player: 1401);
        var finding = report.Findings.Single(f => f.Code == "R001");
        var table = report.Races.Single();
        Require(finding.Severity == Severity.Error && table.Shipped.Order().SequenceEqual(new ushort[] { 201, 1401 }) &&
                table.Rows.Single(row => row.Race == 1401) is { UsesModModel: true, Loaded: 201, ViaFallback: true, IsPlayer: true },
            "a race model the EQDP data never loads is an error; that race wears the model it falls back to");
    }
}
