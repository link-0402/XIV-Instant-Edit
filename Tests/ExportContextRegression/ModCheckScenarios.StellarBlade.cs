using InstantEdit.Studio.Models;
using InstantEdit.Studio.Services;
using InstantEdit.Studio.Services.Check;
using InstantEdit.Studio.Services.Project;
using Newtonsoft.Json.Linq;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// A synthetic copy of "Stellar Blade 7th Diving Suit (2)" as Mod Optimizer found it: a gear port on
/// e0128 whose normal maps are redirected under "_norm" names, whose materials only exist for
/// material set 1, and which carries leftovers of a hair mod.
/// </summary>
internal static partial class ModCheckScenarios
{
    private const string SbTextures = "chara/equipment/e0128/texture/mt_c0201e0128_top_";
    private const string SbMaterials = "chara/equipment/e0128/material/v0001/mt_c0201e0128_top_";
    private const string SbModel = "chara/equipment/e0128/model/c0201e0128_top.mdl";
    private const string SbHairKey = "chara/human/c0801/obj/hair/h0127/texture/mt_c0201h0127_hir_b_normal.tex";

    private static readonly string[] SbHairFiles =
    [
        "chara/human/c0801/obj/hair/h0127/model/c0801h0127_hir.mdl",
        "chara/human/c0201/obj/hair/h0127/material/v0001/mt_c0201h0127_hir_a.mtrl",
        "chara/human/c0201/obj/hair/h0127/material/v0001/mt_c0201h0127_hir_b.mtrl",
        "chara/human/c0801/obj/hair/h0127/texture/mt_c0201h0127_hir_a_mask.tex",
        "chara/human/c0801/obj/hair/h0127/texture/mt_c0201h0127_hir_a_normal.tex",
        "chara/human/c0801/obj/hair/h0127/texture/mt_c0201h0127_hir_b_mask.tex",
    ];

    private static void RunStellarBlade()
    {
        FindsTheMisnamedNormalMaps();
        FindsMaterialSetsTheItemsNeed();
        FindsTheHairModLeftovers();
        ReportsRaceCoverageAndTheSkinDependency();
    }

    internal static TestModFolder StellarBladeMod()
    {
        var mod = new TestModFolder();
        foreach (var letter in "abcde")
            mod.File($"{SbMaterials}{letter}.mtrl", ModCheckFixtures.Mtrl(
            [
                $"{SbTextures}{letter}_normal.tex", $"{SbTextures}{letter}_mask.tex", $"{SbTextures}{letter}_id.tex", $"{SbTextures}{letter}_base.tex",
            ]));
        mod.File(SbModel, ModCheckFixtures.Mdl(
            ["/mt_c0201b0001_bibo.mtrl", "/mt_c0201e0128_top_a.mtrl", "/mt_c0201e0128_top_b.mtrl", "/mt_c0201e0128_top_c.mtrl", "/mt_c0201e0128_top_d.mtrl"],
            [
                new ModCheckFixtures.Mesh(0, new ModCheckFixtures.Submesh("atr_nek"), new ModCheckFixtures.Submesh("atr_ude", "atr_hij")),
                new ModCheckFixtures.Mesh(1, new ModCheckFixtures.Submesh()), new ModCheckFixtures.Mesh(2, new ModCheckFixtures.Submesh()),
                new ModCheckFixtures.Mesh(3, new ModCheckFixtures.Submesh()), new ModCheckFixtures.Mesh(4, new ModCheckFixtures.Submesh()),
            ]));
        foreach (var texture in new[] { "a_base", "a_id", "a_normal", "b_base", "b_normal", "c_base", "c_normal", "d_base", "d_normal", "e_base", "e_normal" })
            mod.File($"{SbTextures}{texture}.tex", ModCheckFixtures.Tex());
        foreach (var file in SbHairFiles)
            mod.File(file, file.EndsWith(".mdl", StringComparison.Ordinal) ? ModCheckFixtures.Mdl(["/mt_c0201h0127_hir_a.mtrl", "/mt_c0201h0127_hir_b.mtrl"])
                : file.EndsWith(".mtrl", StringComparison.Ordinal) ? ModCheckFixtures.Mtrl([file.Replace("/material/v0001/", "/texture/")]) : ModCheckFixtures.Tex());

        var files = new JObject();
        void Map(string gamePath, string relative) => files[gamePath] = relative.Replace('/', '\\');
        foreach (var letter in "abcde") Map($"{SbMaterials}{letter}.mtrl", $"{SbMaterials}{letter}.mtrl");
        Map(SbModel, SbModel);
        Map($"{SbTextures}a_base.tex", $"{SbTextures}a_base.tex");
        Map($"{SbTextures}a_id.tex", $"{SbTextures}a_id.tex");
        foreach (var placeholder in new[] { "b_id", "b_mask", "c_id", "c_mask", "d_id", "d_mask", "a_mask" })
            Map($"{SbTextures}{placeholder}.tex", $"{SbTextures}a_id.tex");
        Map($"{SbTextures}a_normal.tex", $"{SbTextures}a_normal.tex");
        foreach (var letter in "bcd")
        {
            Map($"{SbTextures}{letter}_base.tex", $"{SbTextures}{letter}_base.tex");
            Map($"{SbTextures}{letter}_norm.tex", $"{SbTextures}{letter}_normal.tex");
        }
        Map($"{SbTextures}e_base.tex", $"{SbTextures}e_base.tex");
        Map(SbHairKey, $"{SbTextures}e_normal.tex");

        mod.Meta(new JObject
        {
            ["FileVersion"] = 4,
            ["Identifier"] = "325be350-5384-4241-93c8-f30c66f8f217",
            ["LastWrite"] = "2026-09-26T17:47:46.0106303Z",
            ["Name"] = "Stellar Blade 7th Diving Suit",
            ["Author"] = "Luci",
            ["DefaultData"] = new JObject
            {
                ["Files"] = files,
                ["Manipulations"] = new JArray(new JObject
                {
                    ["Type"] = "Est",
                    ["Manipulation"] = new JObject { ["Gender"] = "Female", ["Race"] = "Miqote", ["SetId"] = 127, ["Slot"] = "Hair", ["Entry"] = 175 },
                }),
            },
        }.ToString());
        return mod;
    }

    internal static FakeVanillaFiles StellarBladeGame()
    {
        (byte, ushort)[] Row(byte bodySet, ushort bodyMask) => [(1, 0x3FF), (bodySet, bodyMask), (1, 0x3FF), (1, 0x3FF), (1, 0x3FF)];
        var game = new FakeVanillaFiles()
            .Add("chara/equipment/e0128/e0128.imc", ModCheckFixtures.Imc([Row(1, 0x3FF), Row(2, 0x3FE), Row(1, 0x3FF), Row(3, 0x3FE), Row(4, 0x3FF)]))
            .Add("shader/sm5/shpk/character.shpk")
            .Add("chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_a.mtrl")
            .WithEqdp(128, new Dictionary<ushort, ushort> { [101] = 0x008A, [201] = 0x008A, [901] = 0x008A, [1101] = 0x008A, [801] = 0x0002 });
        for (var set = 1; set <= 4; ++set)
            game.Add($"chara/equipment/e0128/material/v{set:D4}/mt_c0101e0128_top_a.mtrl");
        return game;
    }

    internal static FakeItemVariants StellarBladeItems()
        => new FakeItemVariants()
            .Add(GameModelKind.Equipment, 128, EquipSlot.Body, 1, "Birdsong Coat")
            .Add(GameModelKind.Equipment, 128, EquipSlot.Body, 2, "Birdliege Coat")
            .Add(GameModelKind.Equipment, 128, EquipSlot.Body, 3, "Parrotbeak Coat")
            .Add(GameModelKind.Equipment, 128, EquipSlot.Body, 4, "Parrotliege Coat");

    internal static ModCheckReport CheckStellarBlade(TestModFolder mod, ICollectionView? collection = null)
        => ModCheck.Run(CheckInput.ForFolder(mod.Path, StellarBladeGame(), StellarBladeItems(), 801, collection), null, default);

    private static void FindsTheMisnamedNormalMaps()
    {
        using var mod = StellarBladeMod();
        var report = CheckStellarBlade(mod);

        foreach (var letter in "bcd")
        {
            var path = $"{SbTextures}{letter}_normal.tex";
            var finding = report.Findings.Single(f => f.Code == "C002" && f.GamePath == path);
            var fix = report.Find(finding.RepairIds.Single())!;
            var superseded = report.Findings.Single(f => f.Code == "L001" && f.GamePath == $"{SbTextures}{letter}_norm.tex");
            var removal = report.Find(superseded.RepairIds.Single())!;
            Require(finding.Severity == Severity.Error && fix.Preselected && fix.Confidence == RepairConfidence.High &&
                    fix.EditsFor(null).Single() == new AddFileEdit(OptionRef.Default, path, $"{SbTextures}{letter}_normal.tex") &&
                    removal.Preselected && removal.DependsOn.SequenceEqual([fix.Id]),
                $"the {letter} normal map redirected under a _norm name is redirected under its real name, and the wrong one removed after");
        }
        Require(!report.Findings.Any(f => f.Code == "C002" && (f.GamePath!.Contains("_mask") || f.GamePath.Contains("_id"))),
            "placeholder masks and index maps are fine");
    }

    private static void FindsMaterialSetsTheItemsNeed()
    {
        using var mod = StellarBladeMod();
        var report = CheckStellarBlade(mod);

        var expected = new Dictionary<int, string> { [2] = "Birdsong Coat", [3] = "Parrotbeak Coat", [4] = "Parrotliege Coat" };
        foreach (var (set, item) in expected)
        {
            var finding = report.Findings.Single(f => f.Code == "V001" && f.GamePath == $"chara/equipment/e0128/material/v{set:D4}/");
            var repair = report.Find(finding.RepairIds.Single())!;
            Require(finding.Severity == Severity.Error && finding.Detail.Contains(item, StringComparison.Ordinal) && repair.Preselected &&
                    repair.Edits.Cast<AddFileEdit>().SequenceEqual("abcd".Select(letter => new AddFileEdit(OptionRef.Default,
                        $"chara/equipment/e0128/material/v{set:D4}/mt_c0201e0128_top_{letter}.mtrl", $"{SbMaterials}{letter}.mtrl"))),
                $"material set v{set:D4} ({item}) gets the materials of set 1");
        }
        var rows = report.Variants.Single().Rows;
        Require(!report.Findings.Any(f => f.Code == "V001" && f.GamePath!.Contains("v0001")) &&
                rows.Select(row => (int)row.MaterialSet).SequenceEqual([1, 2, 1, 3, 4]) && rows.Single(row => row.Variant == 2).Status is null,
            "the variant table lists each variant's material set");
    }

    private static void FindsTheHairModLeftovers()
    {
        using var mod = StellarBladeMod();
        var report = CheckStellarBlade(mod);

        var key = report.Findings.Single(f => f.Code == "L003");
        var est = report.Findings.Single(f => f.Code == "L004");
        Require(key.GamePath == SbHairKey && key.Cluster == "From another mod: Hair 127" && report.Find(key.RepairIds.Single())!.Preselected &&
                est.Cluster == "From another mod: Hair 127" && report.Find(est.RepairIds.Single())!.Preselected,
            "a hair mod's redirection and metadata in a gear mod are leftovers from another mod, ticked for removal");
        foreach (var file in SbHairFiles)
        {
            var orphan = report.Findings.Single(f => f.Code == "L005" && f.Key == $"L005:{file}");
            Require(orphan.Cluster == "From another mod: Hair 127" && report.Find(orphan.RepairIds.Single())!.Preselected,
                $"{Path.GetFileName(file)}, which nothing uses, is ticked for deletion");
        }
        Require(report.Findings.Where(f => f.Code == "L002").Select(f => f.GamePath!).Order()
                    .SequenceEqual(new[] { $"{SbMaterials}e.mtrl", $"{SbTextures}e_base.tex" }.Order()),
            "the unused fifth material of the suit and its texture are redirections nothing asks for");
    }

    private static void ReportsRaceCoverageAndTheSkinDependency()
    {
        using var mod = StellarBladeMod();
        var report = CheckStellarBlade(mod);

        var races = report.Races.Single();
        var player = races.Rows.Single(row => row.IsPlayer);
        Require(races.Shipped.SequenceEqual(new ushort[] { 201 }) && player is { Race: 801, UsesModModel: true, ViaFallback: true } &&
                !races.Rows.Single(row => row.Race == 1201).UsesModModel && !report.Findings.Any(f => f.Code is "R001" or "R003"),
            "the player's Miqo'te wears the Midlander model the mod ships, Lalafell don't");
        Require(report.Findings.Single(f => f.Code == "X001").Severity == Severity.Warning,
            "without the collection, a skin material the game lacks is a warning");

        var live = CheckStellarBlade(mod, new FakeCollectionView(new Dictionary<string, string>
        {
            ["chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_bibo.mtrl"] = @"X:\Penumbra\Skin Collection\bibo.mtrl",
        }));
        Require(live.Findings.Single(f => f.Code == "X001").Detail.Contains("Skin Collection", StringComparison.Ordinal),
            "with the collection, the skin material names the mod that provides it");
    }
}
