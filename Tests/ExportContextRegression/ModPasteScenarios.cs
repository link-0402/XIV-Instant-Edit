using InstantEdit.Studio.Services;
using InstantEdit.Studio.Services.Project;
using Newtonsoft.Json.Linq;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// Copy and paste between mods in the Mod Browser, on a ring mod's groups carried over to a leg mod
/// made from it by a mashup: kept at their game paths, or moved to the leg item where the mashup
/// put the ring's files; new ids and names; one copy of a file several options share; a race's own
/// texture keeping its race; materials whose texture paths move with them; attribute groups left
/// out; and a paste that fails leaving nothing behind.
/// </summary>
internal static class ModPasteScenarios
{
    private const string Ring          = "chara/accessory/a0053";
    private const string RingModel     = Ring + "/model/c0201a0053_rir.mdl";
    private const string RingMaterial  = Ring + "/material/v0001/mt_c0201a0053_rir_a.mtrl";
    private const string ToneBase      = Ring + "/texture/c0201a0053_rir_tone_base.tex";
    private const string Norm          = Ring + "/texture/v01_c0201a0053_rir_norm.tex";
    private const string NormOtherRace = Ring + "/texture/v01_c0101a0053_rir_norm.tex";
    private const string Skin          = "chara/human/c0201/obj/body/b0001/texture/c0201b0001_b_n.tex";

    private const string Legs          = "chara/equipment/e0279";
    private const string LegModel      = Legs + "/model/c0201e0279_dwn.mdl";
    // Where the mashup that made the leg mod put the ring's textures.
    private const string LegToneBase   = Legs + "/texture/c0201e0279_rir_tone_base.tex";
    private const string LegNorm       = Legs + "/texture/v01_c0201e0279_rir_norm.tex";
    private const string LegMaterial   = Legs + "/material/v0001/mt_c0201e0279_rir_a.mtrl";

    private const string TextureMatchingId = "8f3c2a10-5d4e-4b6a-9c71-0a1b2c3d4e01";
    private const string VeinsId           = "8f3c2a10-5d4e-4b6a-9c71-0a1b2c3d4e02";
    private const string ModelSwapId       = "8f3c2a10-5d4e-4b6a-9c71-0a1b2c3d4e03";
    private const string RaenId            = "8f3c2a10-5d4e-4b6a-9c71-0a1b2c3d4e12";

    public static void Run(string testRoot)
    {
        TheMoveIsTheOneTheMashupMade();
        using var ring = RingMod();
        var ringMeta = PenumbraModMeta.Load(ring.Path);
        AttributeGroupsAreNotCopied(ringMeta);

        using var legs = LegMod();
        var ringBefore = ring.ReadMeta();
        var matching = ModPaste.CopyGroup(ringMeta, ring.Path, "Ring Mod", TextureMatchingId)!;
        AGroupMovesToTheLegItem(matching, ring, legs);
        AGroupKeepsItsGamePaths(matching, legs);
        AnEmptyOptionAndATakenPlaceSurvive(ringMeta, ring, legs);
        AMaterialsTexturesMoveWithIt(ringMeta, ring, legs);
        FilesGoIntoAnOption(ringMeta, ring, legs);
        AnOptionBecomesAnOptionOfAGroup(ringMeta, ring, legs);
        APasteThatFailsLeavesNothing(matching, legs);
        Require(ring.ReadMeta() == ringBefore && ring.Exists("Texture Matching/Raen/base.tex"),
            "the mod copied from stays exactly as it was");
    }

    private static void TheMoveIsTheOneTheMashupMade()
    {
        var move = ModRetarget.Between(Ring, RingModel, LegModel)!;
        Require(move.Path(ToneBase) == LegToneBase && move.Path(Norm) == LegNorm,
            "a ring's textures move to the leg item where the mashup put them");
        Require(move.Path(NormOtherRace) == Legs + "/texture/v01_c0101e0279_rir_norm.tex",
            "another race's texture keeps its race, so the two don't land on one path");
        Require(move.Path(RingMaterial) == LegMaterial, "a material moves with its folder and name");
        Require(!move.Moves(RingModel) && move.Path(RingModel) == RingModel && !move.Covers(Skin) && move.Path(Skin) == Skin,
            "models and files of other items keep their paths");
        Require(ModRetarget.Between(Ring, null, LegModel)!.Path(ToneBase) == LegToneBase,
            "without a model of the ring, the ring's folder says which id moves");
        Require(ModRetarget.ItemFolder(ToneBase) == Ring && ModRetarget.ItemFolder("chara/xls/charamake/x.tex") == null
                && ModRetarget.ItemFolder(Skin) == "chara/human/c0201/obj/body/b0001",
            "an item's folder is the part of the path before model, material or texture");
    }

    private static void AttributeGroupsAreNotCopied(PenumbraModMeta meta)
    {
        string Group(string name) => meta.Groups.Single(g => g.Name == name).Id;
        Require(ModPaste.WhyNoCopy(meta, Group("Options")) is { Length: > 0 }
                && ModPaste.WhyNoCopy(meta, Group("Toggles")) is { Length: > 0 }
                && ModPaste.WhyNoCopy(meta, Group("Parts (ATR)")) is { Length: > 0 },
            "groups of Atr manipulations only, IMC groups and Instant Edit's attribute groups aren't copied");
        Require(ModPaste.WhyNoCopy(meta, TextureMatchingId) == null && ModPaste.WhyNoCopy(meta, ModelSwapId) == null,
            "texture toggles, and a group with files and manipulations, are copied");
    }

    private static void AGroupMovesToTheLegItem(ModClip clip, TestModFolder ring, TestModFolder legs)
    {
        var source = ModRetarget.SourceOf(clip)!.Value;
        Require(source.Folder == Ring && source.Model == RingModel, "the copy's item is the ring, with the ring mod's model");
        var move = ModRetarget.Between(source.Folder, source.Model, LegModel);
        var (plan, after, result) = Paste(clip, PasteTarget.NewGroup, "Texture Matching", move, legs.Path);

        Require(plan.Entries == 5 && plan.Files.Count == 4 && plan.Used == 4 && plan.Moved.Count == 5 && plan.Kept == 0
                && plan.Missing.SequenceEqual([ToneBase]),
            "five entries move, four files are copied (two options share one), four land on paths the leg mod uses, and a missing file is left out");
        var group = after.CopyGroup(result.GroupId!)!;
        var options = ((JArray)group["Options"]!).Cast<JObject>().ToList();
        var ids = options.Select(o => (string)o["Id"]!).ToList();
        Require((string?)group["Name"] == "Texture Matching" && result.GroupId != TextureMatchingId
                && ids.Distinct().Count() == 4 && !ids.Contains(RaenId),
            "the pasted group and each of its options have new ids");
        Require((int)group["Priority"]! == 5 && (int)group["DefaultSettings"]! == 1 && (string?)group["Description"] == "Match your skin."
                && after.Groups.Count == 2,
            "its priority, description and default option come along, and the leg mod's other group stays");

        string Rel(int option, string gamePath) => (string)options[option]["Files"]![gamePath]!;
        Require(Rel(1, LegToneBase) == @"Texture Matching\Raen\chara\equipment\e0279\texture\c0201e0279_rir_tone_base.tex"
                && Rel(2, LegToneBase) == Rel(1, LegToneBase)
                && File.ReadAllBytes(Path.Combine(legs.Path, Rel(1, LegToneBase))).SequenceEqual(File.ReadAllBytes(Path.Combine(ring.Path, "Texture Matching", "Raen", "base.tex"))),
            "a file two options share is copied once, into the first option's folder, as it is");
        var hrothgar = (JObject)options[3]["Files"]!;
        Require(hrothgar.Properties().Select(p => p.Name).Order().SequenceEqual(new[] { Legs + "/texture/v01_c0101e0279_rir_norm.tex", LegNorm }.Order()),
            "both races' textures are there, at paths of their own, and the missing one is left out");
        Require(!Directory.EnumerateFiles(legs.Path, "*" + PenumbraModMeta.TempSuffix, SearchOption.AllDirectories).Any(), "no half-written file is left behind");
    }

    private static void AGroupKeepsItsGamePaths(ModClip clip, TestModFolder legs)
    {
        var (plan, after, result) = Paste(clip, PasteTarget.NewGroup, "Texture Matching", null, legs.Path);
        var group = after.CopyGroup(result.GroupId!)!;
        var raen = (JObject)((JArray)group["Options"]!)[1]["Files"]!;
        Require(result.Name == "Texture Matching (copy)" && (string?)group["Name"] == result.Name,
            "a name the mod has already gets (copy)");
        Require(plan.Used == 0 && raen.Properties().Single().Name == ToneBase
                && (string)raen[ToneBase]! == @"Texture Matching\Raen\chara\accessory\a0053\texture\c0201a0053_rir_tone_base.tex",
            "kept at the ring's paths, the files are pasted where the leg mod doesn't use them");
    }

    private static void AnEmptyOptionAndATakenPlaceSurvive(PenumbraModMeta ringMeta, TestModFolder ring, TestModFolder legs)
    {
        var clip = ModPaste.CopyGroup(ringMeta, ring.Path, "Ring Mod", VeinsId)!;
        var (_, after, result) = Paste(clip, PasteTarget.NewGroup, "Veins", ModRetarget.Between(Ring, RingModel, LegModel), legs.Path);
        var options = ((JArray)after.CopyGroup(result.GroupId!)!["Options"]!).Cast<JObject>().ToList();
        Require((string?)options[0]["Name"] == "- OMIT -" && options[0]["Files"] == null,
            "an option without files, a single-select group's way to pick none, comes along");
        var rel = (string)options[1]["Files"]![LegNorm]!;
        Require(rel.EndsWith("v01_c0201e0279_rir_norm_copy.tex", StringComparison.Ordinal)
                && File.ReadAllText(Path.Combine(legs.Path, @"Veins\Strong\chara\equipment\e0279\texture\v01_c0201e0279_rir_norm.tex")) == "in the way",
            "a file already where a pasted one would go is never written over");
    }

    private static void AMaterialsTexturesMoveWithIt(PenumbraModMeta ringMeta, TestModFolder ring, TestModFolder legs)
    {
        var clip = ModPaste.CopyGroup(ringMeta, ring.Path, "Ring Mod", ModelSwapId)!;
        var (plan, after, result) = Paste(clip, PasteTarget.NewGroup, "Model swap", ModRetarget.Between(Ring, RingModel, LegModel), legs.Path);
        var option = (JObject)((JArray)after.CopyGroup(result.GroupId!)!["Options"]!)[0];
        var files = (JObject)option["Files"]!;
        Require(plan.Kept == 1 && plan.HasExtras && files[RingModel] != null && files[LegMaterial] != null,
            "the material moves, the model stays where it was");
        var textures = MtrlReader.Read(File.ReadAllBytes(Path.Combine(legs.Path, (string)files[LegMaterial]!))).TextureOffsets.Select(t => t.Path).ToList();
        Require(textures.SequenceEqual([LegToneBase, LegNorm, Skin]),
            "the moved material reads the moved textures, and the body's texture as before");
        var manipulation = (JObject)((JArray)option["Manipulations"]!).Single();
        Require((int)manipulation["Manipulation"]!["SetId"]! == 53 && (string?)manipulation["Manipulation"]!["Slot"] == "RFinger",
            "manipulations are copied as they are");
    }

    private static void FilesGoIntoAnOption(PenumbraModMeta ringMeta, TestModFolder ring, TestModFolder legs)
    {
        var clip = ModPaste.CopyFiles(ringMeta, ring.Path, "Ring Mod", OptionRef.Default, [(ToneBase, @"chara\accessory\a0053\texture\v01_c0201a0053_rir_base.tex")]);
        var meta = PenumbraModMeta.Load(legs.Path);
        Require(ModPaste.WhyNot(clip, PasteTarget.Into(OptionRef.Default), ringMeta, ring.Path) is { Length: > 0 }
                && ModPaste.WhyNot(clip, PasteTarget.NewGroup, meta, legs.Path) is { Length: > 0 },
            "files don't go back where they came from, and only into an option");
        var blocked = ModPaste.Plan(clip, PasteTarget.Into(OptionRef.Default), clip.Name, ModRetarget.Between(Ring, RingModel, LegModel), meta, legs.Path,
            new HashSet<string>(), _ => true, (_, _) => true);
        Require(blocked.Blocked.SequenceEqual([LegToneBase]), "an option where a file of your own is set up for one of the paths can't take them");

        var (plan, after, _) = Paste(clip, PasteTarget.Into(OptionRef.Default), clip.Name, ModRetarget.Between(Ring, RingModel, LegModel), legs.Path);
        Require(plan.Replaced.SequenceEqual([LegToneBase]) && after.TryGetFile(OptionRef.Default, LegToneBase, out var rel)
                && PathNormalizer.SameRel(rel, @"chara\equipment\e0279\texture\c0201e0279_rir_tone_base.tex")
                && legs.Exists("Files/chara/equipment/e0279/texture/c0201e0279_rir_tone_base.tex")
                && after.TryGetFile(OptionRef.Default, LegModel, out _),
            "pasted into the default files, the file they had there is replaced by the copy and stays in the folder");
    }

    private static void AnOptionBecomesAnOptionOfAGroup(PenumbraModMeta ringMeta, TestModFolder ring, TestModFolder legs)
    {
        var raen = new OptionRef(TextureMatchingId, RaenId);
        var clip = ModPaste.CopyOption(ringMeta, ring.Path, "Ring Mod", raen)!;
        var meta = PenumbraModMeta.Load(legs.Path);
        var full = meta.Groups.Single(g => g.Name == "Full");
        Require(ModPaste.WhyNot(clip, PasteTarget.NewOptionIn(full.Id), meta, legs.Path) is { Length: > 0 },
            "a multi-select group with 32 options takes no more");
        var into = meta.Groups.Single(g => g.Name == "Texture Matching");
        var unwritten = ModPaste.Plan(clip, PasteTarget.NewOptionIn(into.Id), "Raen", null, meta, legs.Path, new HashSet<string>(), _ => false, (_, _) => false);
        var gone = PenumbraModMeta.Load(legs.Path);
        gone.RemoveGroup(into.Id);
        Require(Throws(() => ModPaste.Apply(gone, unwritten)), "a paste whose group went meanwhile is refused");

        var (_, after, result) = Paste(clip, PasteTarget.NewOptionIn(into.Id), "Raen", null, legs.Path);
        var view = after.FindGroupView(into.Id)!;
        Require(result.Name == "Raen (copy)" && view.Options.Count == 5 && view.Options[^1].Name == "Raen (copy)"
                && view.Options[^1].Ref == result.Option && Guid.TryParse(view.Options[^1].Ref.Option, out _)
                && after.TryGetFile(result.Option!.Value, ToneBase, out _),
            "a copied option goes at the end of the group, with an id and a name of its own");
    }

    private static void APasteThatFailsLeavesNothing(ModClip clip, TestModFolder legs)
    {
        var meta = PenumbraModMeta.Load(legs.Path);
        var before = legs.ReadMeta();
        var plan = ModPaste.Plan(clip, PasteTarget.NewGroup, "Broken", null, meta, legs.Path, new HashSet<string>(), _ => false, (_, _) => false);
        Directory.CreateDirectory(PathNormalizer.ToDiskPath(legs.Path, plan.Files[^1].RelPath));   // a folder where the last file goes
        Require(Throws(() => ModPaste.Write(legs.Path, plan))
                && plan.Files.SkipLast(1).All(f => !File.Exists(PathNormalizer.ToDiskPath(legs.Path, f.RelPath)))
                && !Directory.EnumerateFiles(legs.Path, "*" + PenumbraModMeta.TempSuffix, SearchOption.AllDirectories).Any()
                && legs.ReadMeta() == before,
            "when one file can't be written, those written are deleted again and meta.json is untouched");
    }

    private static (PastePlan Plan, PenumbraModMeta After, PasteResult Result) Paste(ModClip clip, PasteTarget target, string name, ModRetarget? retarget, string folder)
    {
        var meta = PenumbraModMeta.Load(folder);
        var used = meta.AllFiles().Select(f => PathNormalizer.GamePath(f.GamePath)).ToHashSet(StringComparer.Ordinal);
        var plan = ModPaste.Plan(clip, target, name, retarget, meta, folder, new HashSet<string>(), p => used.Contains(PathNormalizer.GamePath(p)), (_, _) => false);
        ModPaste.Write(folder, plan);
        var fresh = PenumbraModMeta.Load(folder);
        var result = ModPaste.Apply(fresh, plan);
        fresh.Save(folder, backupFirst: false);
        return (plan, PenumbraModMeta.Load(folder), result);
    }

    private static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>A ring mod: texture toggles, a model group and attribute groups.</summary>
    private static TestModFolder RingMod()
    {
        var material = ModCheckFixtures.Mtrl([ToneBase, Norm, Skin]);
        return new TestModFolder()
            .File("chara/accessory/a0053/material/v0001/mt_c0201a0053_rir_a.mtrl", material)
            .File("chara/accessory/a0053/texture/v01_c0201a0053_rir_base.tex", [1])
            .File("chara/accessory/a0053/texture/v01_c0201a0053_rir_norm.tex", [2])
            .File("Texture Matching/Raen/base.tex", [3])
            .File("Texture Matching/Hrothgar/c0101_norm.tex", [4])
            .File("Texture Matching/Hrothgar/c0201_norm.tex", [5])
            .File("Veins/Strong/norm.tex", [6])
            .File("Model swap/Model/rir.mdl", [7])
            .File("Model swap/Model/a.mtrl", material)
            .Meta($$"""
            {
              "FileVersion": 4,
              "Identifier": "8f3c2a10-5d4e-4b6a-9c71-0a1b2c3d4e00",
              "Name": "Ring Mod",
              "DefaultData": {
                "Files": {
                  "{{RingMaterial}}": "chara\\accessory\\a0053\\material\\v0001\\mt_c0201a0053_rir_a.mtrl",
                  "{{ToneBase}}": "chara\\accessory\\a0053\\texture\\v01_c0201a0053_rir_base.tex",
                  "{{Norm}}": "chara\\accessory\\a0053\\texture\\v01_c0201a0053_rir_norm.tex"
                }
              },
              "Groups": [
                { "Type": "Single", "Id": "{{TextureMatchingId}}", "Name": "Texture Matching", "Description": "Match your skin.", "Priority": 5, "DefaultSettings": 1, "Options": [
                  { "Id": "8f3c2a10-5d4e-4b6a-9c71-0a1b2c3d4e11", "Name": "Default (Most Races)", "Files": { "{{ToneBase}}": "chara\\accessory\\a0053\\texture\\v01_c0201a0053_rir_base.tex" } },
                  { "Id": "{{RaenId}}", "Name": "Raen", "Files": { "{{ToneBase}}": "Texture Matching\\Raen\\base.tex" } },
                  { "Id": "8f3c2a10-5d4e-4b6a-9c71-0a1b2c3d4e13", "Name": "Raen again", "Files": { "{{ToneBase}}": "Texture Matching\\Raen\\base.tex" } },
                  { "Id": "8f3c2a10-5d4e-4b6a-9c71-0a1b2c3d4e14", "Name": "Hrothgar", "Files": {
                    "{{NormOtherRace}}": "Texture Matching\\Hrothgar\\c0101_norm.tex",
                    "{{Norm}}": "Texture Matching\\Hrothgar\\c0201_norm.tex",
                    "{{ToneBase}}": "Texture Matching\\Hrothgar\\gone.tex" } }
                ] },
                { "Type": "Single", "Id": "{{VeinsId}}", "Name": "Veins", "Priority": 3, "Options": [
                  { "Id": "8f3c2a10-5d4e-4b6a-9c71-0a1b2c3d4e21", "Name": "- OMIT -" },
                  { "Id": "8f3c2a10-5d4e-4b6a-9c71-0a1b2c3d4e22", "Name": "Strong", "Files": { "{{Norm}}": "Veins\\Strong\\norm.tex" } }
                ] },
                { "Type": "Single", "Id": "{{ModelSwapId}}", "Name": "Model swap", "Options": [
                  { "Id": "8f3c2a10-5d4e-4b6a-9c71-0a1b2c3d4e31", "Name": "Model",
                    "Files": { "{{RingModel}}": "Model swap\\Model\\rir.mdl", "{{RingMaterial}}": "Model swap\\Model\\a.mtrl" },
                    "Manipulations": [ { "Type": "Eqdp", "Manipulation": { "Gender": "Female", "Race": "Midlander", "SetId": 53, "Slot": "RFinger", "Entry": 128 } } ] }
                ] },
                { "Type": "Multi", "Id": "8f3c2a10-5d4e-4b6a-9c71-0a1b2c3d4e04", "Name": "Options", "Options": [
                  { "Id": "8f3c2a10-5d4e-4b6a-9c71-0a1b2c3d4e41", "Name": "Glow", "Manipulations": [ { "Type": "Atr", "Manipulation": { "Attribute": "atrx_ring_glow", "Entry": true } } ] }
                ] },
                { "Type": "Imc", "Id": "8f3c2a10-5d4e-4b6a-9c71-0a1b2c3d4e05", "Name": "Toggles", "Options": [ { "Name": "Hide" } ] },
                { "Type": "Multi", "Id": "8f3c2a10-5d4e-4b6a-9c71-0a1b2c3d4e06", "Name": "Parts (ATR)",
                  "Description": "Managed by XIV Instant Edit attribute group v1: atr|{{RingModel}}", "Options": [] }
              ]
            }
            """);
    }

    /// <summary>A leg mod a mashup made from the ring mod: the ring's textures in the leg item's folder, and a full multi-select group.</summary>
    private static TestModFolder LegMod()
    {
        var full = string.Join(",", Enumerable.Range(1, 32).Select(i => $$"""{ "Id": "{{Guid.NewGuid()}}", "Name": "Part {{i}}" }"""));
        return new TestModFolder()
            .File("Files/chara/equipment/e0279/model/c0201e0279_dwn.mdl", [10])
            .File("Files/chara/equipment/e0279/texture/c0201e0279_rir_tone_base.tex", [11])
            .File("Files/chara/equipment/e0279/texture/v01_c0201e0279_rir_norm.tex", [12])
            .File("Veins/Strong/chara/equipment/e0279/texture/v01_c0201e0279_rir_norm.tex", "in the way"u8.ToArray())
            .Meta($$"""
            {
              "FileVersion": 4,
              "Identifier": "8f3c2a10-5d4e-4b6a-9c71-0a1b2c3d4f00",
              "Name": "Leg Mod",
              "DefaultData": {
                "Files": {
                  "{{LegModel}}": "Files/chara/equipment/e0279/model/c0201e0279_dwn.mdl",
                  "{{LegToneBase}}": "Files/chara/equipment/e0279/texture/c0201e0279_rir_tone_base.tex",
                  "{{LegNorm}}": "Files/chara/equipment/e0279/texture/v01_c0201e0279_rir_norm.tex"
                }
              },
              "Groups": [ { "Type": "Multi", "Id": "{{Guid.NewGuid()}}", "Name": "Full", "Options": [ {{full}} ] } ]
            }
            """);
    }
}
