using System.Text.Json.Nodes;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Services;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// The Mod Browser's Build mode: a build only makes new mods, variant files of different items never
/// share a file, explicit mesh-part material picks follow their material when the materials list
/// changes, two items may write one path into different options, and mod folder names Windows can store.
/// </summary>
internal static class ModBuildScenarios
{
    public static void Run(string testRoot)
    {
        var root = Path.Combine(testRoot, "ModBuild");
        Directory.CreateDirectory(root);

        ExistingFolderIsRefused(root);              // writers-1/-2
        LeftoversAreRemoved(root);
        MetaIsReplacedWhole(root);
        EmptyDescriptionStaysEmpty(root);
        WeaponPaths();
        WeaponKeys();
        WeaponImcIsWrittenAsWeapon(root);
        UnknownKeyTagsResolveToNothing();
        VariantNamesAreBuildWide();              // writers-3
        MaterialLinksFollowTheirMaterials();        // models-1
        SamePathInAnotherOptionIsNoConflict();      // models-3
        FolderNamesWindowsCanStore();               // writers-7
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

    private static void EmptyDescriptionStaysEmpty(string root)
    {
        var mod = Path.Combine(root, "No Description");
        Directory.CreateDirectory(mod);
        ModMetaWriter.Write(mod, "No Description", new ModCarrier(), Array.Empty<ModGroupOut>(), new ModMetaInfo { Description = "" });
        var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(mod, "meta.json")))!;
        Require((string?)meta["Description"] == "", "an empty pack description is written empty, not as \"Port of ...\"");
    }

    private static void WeaponPaths()
    {
        Require(WeaponNaming.ModelGamePath(201, 1) == "chara/weapon/w0201/obj/body/b0001/model/w0201b0001.mdl", "a weapon's model path");
        Require(WeaponNaming.MaterialGamePath(201, 1, "mt_w0201b0001_a") == "chara/weapon/w0201/obj/body/b0001/material/v0001/mt_w0201b0001_a.mtrl",
            "a weapon's material path, in the v0001 folder the build always writes");
        Require(WeaponNaming.ImcGamePath(201, 1) == "chara/weapon/w0201/obj/body/b0001/b0001.imc", "a weapon's IMC path");
        Require(WeaponNaming.DefaultMaterialName(201, 1, 2) == "mt_w0201b0001_b", "a weapon's second material is named _b");
        Require(WeaponNaming.TextureGamePath(201, 1, "mt_w0201b0001_a_n") == "chara/weapon/w0201/obj/body/b0001/texture/mt_w0201b0001_a_n.tex",
            "a weapon's texture path");

        var subject = new WeaponSubject(new WeaponModel(4321, "Bronze Gladius", 0, "Gladiator's Arm", 201, 1, 3, false));
        Require(subject.ModelGamePath(RaceInfo.BaseFor(PlayerGender.Female)) == WeaponNaming.ModelGamePath(201, 1),
            "a weapon has one model whatever the race");
        Require(subject is { Kind: SubjectKind.Weapon, UsesEqdp: false, UsesImc: true, MultiRace: false, HasRaces: false, IdDisplay: "w0201b0001" },
            "a weapon uses IMC but no EQDP and no races");
        Require(subject.MaterialRaces(new List<RaceModelEntry>()).SequenceEqual(new RaceGender?[] { null }), "a weapon writes one race-shared material set");
    }

    private static void WeaponKeys()
    {
        var main = new WeaponSubject(new WeaponModel(4321, "Bronze Gladius", 0, "Gladiator's Arm", 201, 1, 3, false));
        var off  = new WeaponSubject(new WeaponModel(4321, "Bronze Gladius", 0, "Gladiator's Arm", 201, 1, 3, true));
        Require(main.Key == 4321u, "a main-hand weapon is keyed by its item row id, like gear");
        Require(off.Key == (0x4000_0000u | 4321u), "an off-hand model sets the off-hand bit");
        Require((main.Key & 0x8000_0000u) == 0 && (off.Key & 0x8000_0000u) == 0, "weapon keys never look like feature keys");
        Require(off.DisplayName == "Bronze Gladius (off hand)", "an off-hand model says so in its name");
    }

    private static void WeaponImcIsWrittenAsWeapon(string root)
    {
        var mod = Path.Combine(root, "Weapon Imc");
        Directory.CreateDirectory(mod);
        var defaults = new ModCarrier();
        defaults.Imc.Add(new ModImcOverride
        {
            Object = ImcObject.OffHand, SetId = 201, SecondaryId = 1, Variant = 3,
            DecalId = 0, VfxId = 2, MaterialAnimationId = 0, AttributeMask = 0x3FF, SoundId = 0,
        });
        ModMetaWriter.Write(mod, "Weapon Imc", defaults, Array.Empty<ModGroupOut>());
        var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(mod, "meta.json")))!;
        var m = meta["DefaultData"]?["Manipulations"]?[0]?["Manipulation"];
        Require(m != null && (string?)m["ObjectType"] == "Weapon" && (string?)m["EquipSlot"] == "OffHand" &&
                (int?)m["PrimaryId"] == 201 && (int?)m["SecondaryId"] == 1 && (int?)m["Variant"] == 3 &&
                (int?)m["Entry"]?["MaterialId"] == 1 && (int?)m["Entry"]?["VfxId"] == 2,
            "a weapon's IMC entry is written with the weapon's object type, hand, body and variant");
    }

    private static void UnknownKeyTagsResolveToNothing()
    {
        Require(PortSubject.KeyTag(12345) == KeyTagKind.Gear, "a plain row id is gear or a main-hand weapon");
        Require(PortSubject.KeyTag(0x4000_3039) == KeyTagKind.OffHand, "the off-hand bit marks an off-hand weapon");
        Require(PortSubject.KeyTag(0x2000_0010) == KeyTagKind.Emote, "0x20 marks an emote");
        Require(PortSubject.KeyTag(0x2100_0010) == KeyTagKind.Timeline, "0x21 marks another animation timeline");
        Require(PortSubject.KeyTag(0x8000_0000 | 0x1001_0005) == KeyTagKind.Feature, "the top bit marks a character feature");
        Require(PortSubject.KeyTag(0x1000_0001) == KeyTagKind.Unknown, "any other tag is unknown, not an item");
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

    private static void SamePathInAnotherOptionIsNoConflict()
    {
        var race = new RaceGender(PlayerRace.Midlander, PlayerGender.Male);
        var first  = new PortItem(new FeatureSubject(SubjectKind.Hair, race, 5));
        var second = new PortItem(new FeatureSubject(SubjectKind.Hair, race, 6));
        const string path = "chara/human/c0101/obj/hair/h0005/material/v0001/mt_c0101h0005_hir_a.mtrl";
        var target = new Target(TargetKind.Material, second.Key, 0);

        var writtenBy = new Dictionary<string, PortItem>(StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var issues = new List<PortIssue>();
        PortValidator.ValidateAcrossItems(first, [("group/red", path, new Target(TargetKind.Material, first.Key, 0))], writtenBy, paths, issues);
        PortValidator.ValidateAcrossItems(second, [("group/blue", path, target)], writtenBy, paths, issues);
        Require(issues.Count == 0 && paths.Contains(path), "two items writing one path into different options don't overwrite each other");

        PortValidator.ValidateAcrossItems(second, [("group/red", path, target)], writtenBy, paths, issues);
        Require(issues.Count == 1 && issues[0].Severity == Severity.Error, "the same path in the same option is still an error");
    }

    private static void FolderNamesWindowsCanStore()
    {
        Require(PortSession.SanitizeFolderName("Top: Red/Blue") == "Top_ Red_Blue", "invalid characters still become '_'");
        Require(PortSession.SanitizeFolderName("Bunny Suit Ver. ") == "Bunny Suit Ver" && PortSession.SanitizeFolderName("Name...") == "Name",
            "trailing dots and spaces go, as Windows would drop them");
        Require(PortSession.SanitizeFolderName("CON") == "_CON" && PortSession.SanitizeFolderName("nul.txt") == "_nul.txt" && PortSession.SanitizeFolderName("COM1") == "_COM1",
            "device names get a '_' in front");
        Require(PortSession.SanitizeFolderName("Console") == "Console" && PortSession.SanitizeFolderName("...") == "Mod",
            "other names are left alone; a name of only dots falls back");
    }
}
