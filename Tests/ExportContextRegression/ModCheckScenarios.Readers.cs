using InstantEdit.Studio.Models;
using InstantEdit.Studio.Models.Resources;
using InstantEdit.Studio.Services;
using InstantEdit.Studio.Services.Resources;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// The game knowledge a mod check stands on: which model a game path names, which material path
/// the game loads for a model's material name, where a game path belongs, and which race model a
/// character loads. Ported from Mod Optimizer's GameKnowledgeTests.
/// </summary>
internal static partial class ModCheckScenarios
{
    public static void RunReaders(string testRoot)
    {
        ParsesModelPaths();
        ResolvesMaterialPathsLikeTheGame();
        RecognisesGameRootsAndMangledKeys();
        ResolvesRaceModelsLikePenumbra();
        foreach (var (version, extraLods) in new[] { (ModCheckFixtures.ModelVersion5, false), (ModCheckFixtures.ModelVersion6, false), (ModCheckFixtures.ModelVersion6, true) })
            ReadsModelMaterialsAttributesAndSubmeshes(version, extraLods);
        FlagsMeshesWithMissingMaterialSlots();
        TruncatedModelFallsBackToTheStringTable();
        ReadsMaterialTexturesFlagsAndSamplers();
        ReadsImcRowsAndEqdpEntries();
        SniffsFileKinds();
    }

    private static void ReadsModelMaterialsAttributesAndSubmeshes(uint version, bool extraLods)
    {
        var bytes = ModCheckFixtures.Mdl(["/mt_c0201e0001_top_a.mtrl", "/mt_c0201b0001_a.mtrl"],
        [
            new ModCheckFixtures.Mesh(0, new ModCheckFixtures.Submesh("atr_tv_a"), new ModCheckFixtures.Submesh()),
            new ModCheckFixtures.Mesh(1, new ModCheckFixtures.Submesh("atr_ude")),
        ], version: version, extraLods: extraLods);

        var info = ModelLayoutReader.Read(bytes);

        Require(info.Structured && info.Version == version &&
                info.Materials.SequenceEqual(["/mt_c0201e0001_top_a.mtrl", "/mt_c0201b0001_a.mtrl"]) &&
                info.Attributes.SequenceEqual(["atr_tv_a", "atr_ude"]) && info.Bones.SequenceEqual(["j_kosi", "j_sebo_a"]) &&
                info.Lod0Meshes.Select(mesh => mesh.MaterialIndex).SequenceEqual([0, 1]) &&
                info.SubmeshAttributeMasks.SequenceEqual([1u, 0u, 2u]) && info.MeshesWithInvalidMaterial.Count == 0,
            $"a v{version & 0xF} model{(extraLods ? " with extra LODs" : "")} gives its materials, attributes, bones, meshes and submesh masks");

        var facts = ModFileFactsCache.FromBytes(bytes, ResourceKind.Model);
        Require(facts.MaterialSlots.SequenceEqual(["/mt_c0201e0001_top_a.mtrl", "/mt_c0201b0001_a.mtrl"]),
            "the resource tree's file facts carry the model's material slots");
    }

    private static void FlagsMeshesWithMissingMaterialSlots()
    {
        var info = ModelLayoutReader.Read(ModCheckFixtures.Mdl(["/mt_a.mtrl"], [new ModCheckFixtures.Mesh(3, new ModCheckFixtures.Submesh())]));
        Require(info.Structured && info.MeshesWithInvalidMaterial.SequenceEqual([0]),
            "a mesh pointing past the material list is flagged");
    }

    private static void TruncatedModelFallsBackToTheStringTable()
    {
        var bytes = ModCheckFixtures.Mdl(["/mt_c0201e0001_top_a.mtrl", "/mt_c0201e0001_top_b.mtrl"]);
        var stringTableSize = BitConverter.ToUInt32(bytes, 0x44 + 4);
        var truncated = bytes[..(0x44 + 8 + (int)stringTableSize + 10)];

        var info = ModelLayoutReader.Read(truncated);
        Require(!info.Structured && info.Materials.SequenceEqual(["/mt_c0201e0001_top_a.mtrl", "/mt_c0201e0001_top_b.mtrl"]),
            "a truncated model still names the materials in its string table");
        Reject(() => ModelLayoutReader.Read(ModCheckFixtures.Tex()), "a texture is not read as a model");
    }

    private static void ReadsMaterialTexturesFlagsAndSamplers()
    {
        var bytes = ModCheckFixtures.Mtrl(["chara/equipment/e0001/texture/a_n.tex", "chara/equipment/e0001/texture/a_m.tex"], dx11: [true, false],
            samplers: [new ModCheckFixtures.Sampler(ModCheckFixtures.SamplerNormal, 0), new ModCheckFixtures.Sampler(ModCheckFixtures.SamplerMask, 5)]);

        var info = MtrlReader.Read(bytes);

        Require(MtrlReader.LooksLikeMtrl(bytes) && info.ShaderPackageName == "character.shpk" &&
                info.TextureOffsets.Select(t => t.Path).SequenceEqual(["chara/equipment/e0001/texture/a_n.tex", "chara/equipment/e0001/texture/a_m.tex"]) &&
                info.TextureOffsets[0].Dx11 && !info.TextureOffsets[1].Dx11,
            "a material gives its shader, its textures and which are DX11 textures");
        Require(info.ShaderDataParsed && info.Samplers.Select(s => (int)s.TextureIndex).SequenceEqual([0, 5]) && !info.LegacyColorTable,
            "a material gives its samplers and the textures they point at");
        Require(MtrlReader.Read(ModCheckFixtures.Mtrl(["chara/a/b.tex"], colorTableSize: 544)).LegacyColorTable,
            "an Endwalker-sized colour table is a legacy one");
    }

    private static void ReadsImcRowsAndEqdpEntries()
    {
        var imc = ImcReader.Parse(ModCheckFixtures.Imc([[(1, 0x3FF), (1, 0x3FF)], [(1, 0x3FF), (2, 0x3FE)]]))!;
        Require(imc.VariantCount == 1 && imc.Entry(1, 1) is { MaterialId: 2, AttributeMask: 0x3FE } &&
                imc.Entry(1, 2) is null && imc.Entry(5, 0) is null,
            "an IMC file gives each part's material set and attribute mask per variant");

        var eqdp = EqdpReader.Parse(ModCheckFixtures.Eqdp(new Dictionary<ushort, ushort> { [128] = 0x008A, [600] = 0x0200 }))!;
        Require(eqdp.Entry(128) == 0x008A && eqdp.Entry(600) == 0x0200 && eqdp.Entry(300) == 0 && eqdp.Entry(9000) == 0,
            "an EQDP file gives each set's entry, zero for empty blocks and sets beyond the table");
        Require(((0x008A >> SlotInfo.EqdpShift(EquipSlot.Body)) & 2) != 0 && ((0x0002 >> SlotInfo.EqdpShift(EquipSlot.Body)) & 2) == 0,
            "the second bit of a slot's pair says the race has its own model");
    }

    private static void SniffsFileKinds()
    {
        Require(FileSniffer.Sniff(ModCheckFixtures.Tex(), ModCheckFixtures.Tex().Length) == FileKind.Tex &&
                FileSniffer.Sniff(ModCheckFixtures.Mdl(["/mt_a.mtrl"]), 1000) == FileKind.Mdl &&
                FileSniffer.Sniff(ModCheckFixtures.Mtrl(["chara/a/b.tex"]), 1000) == FileKind.Mtrl &&
                FileSniffer.Sniff(ModCheckFixtures.Png(), 12) == FileKind.Png &&
                FileSniffer.Sniff([], 0) == FileKind.Empty &&
                FileSniffer.Sniff("hello world, not a texture at all............................................................"u8, 90) == FileKind.Unknown,
            "files are recognised by their content");
        Require(FileSniffer.ExpectedKind("chara/a/b.tex") == FileKind.Tex && FileSniffer.ExpectedKind("chara/a/b.avfx") is null,
            "only textures, models and materials are checked against their extension");
    }

    private static void ParsesModelPaths()
    {
        (string Path, GameModelKind Kind, ushort Race, ushort Id, EquipSlot? Slot)[] cases =
        [
            ("chara/equipment/e0128/model/c0201e0128_top.mdl", GameModelKind.Equipment, 201, 128, EquipSlot.Body),
            ("chara/accessory/a0012/model/c1401a0012_rir.mdl", GameModelKind.Accessory, 1401, 12, EquipSlot.RingRight),
            ("chara/human/c0801/obj/hair/h0127/model/c0801h0127_hir.mdl", GameModelKind.Hair, 801, 127, null),
            ("chara/human/c1401/obj/face/f0001/model/c1401f0001_fac.mdl", GameModelKind.Face, 1401, 1, null),
            ("chara/human/c1301/obj/tail/t0002/model/c1301t0002_til.mdl", GameModelKind.Tail, 1301, 2, null),
            ("chara/human/c1801/obj/zear/z0003/model/c1801z0003_zer.mdl", GameModelKind.Ear, 1801, 3, null),
            ("chara/human/c0201/obj/body/b0001/model/c0201b0001_top.mdl", GameModelKind.Body, 201, 1, null),
        ];
        foreach (var (path, kind, race, id, slot) in cases)
        {
            var info = ModelNaming.Parse(path);
            Require(info is not null && info.Kind == kind && info.Race == race && info.PrimaryId == id && info.Slot == slot,
                $"{path} parses as {kind}, race {race}, id {id}");
        }
        Require(ModelNaming.Parse("chara/equipment/e0128/model/c0201e0128_top_old.mdl") is null,
            "a renamed copy of a model is not a model path the game loads");
        var weapon = ModelNaming.Parse("chara/weapon/w0201/obj/body/b0010/model/w0201b0010.mdl");
        Require(weapon is { Kind: GameModelKind.Weapon, PrimaryId: 201, SecondaryId: 10, Race: 0 },
            "weapon models give their weapon and body ids and no race");
        Require(weapon!.Directory == "chara/weapon/w0201/obj/body/b0010" && weapon.UsesImc &&
                ModelNaming.ImcPath(weapon) == "chara/weapon/w0201/obj/body/b0010/b0010.imc",
            "a weapon's materials live in IMC variant folders of its body");
        var top = ModelNaming.Parse("chara/equipment/e0128/model/c0201e0128_top.mdl")!;
        Require(ModelNaming.ImcPath(top) == "chara/equipment/e0128/e0128.imc" && ModelNaming.ImcPart(top) == 1 &&
                ModelNaming.EqdpPath(801, accessory: false) == "chara/xls/charadb/equipmentdeformerparameter/c0801.eqdp",
            "gear reads its slot's part of the set's IMC file, and EQDP files are per race");
    }

    private static void ResolvesMaterialPathsLikeTheGame()
    {
        var gear = ModelNaming.Parse("chara/equipment/e0128/model/c0201e0128_top.mdl")!;
        Require(MaterialNaming.Resolve(gear, "/mt_c0201e0128_top_b.mtrl", 3).Path == "chara/equipment/e0128/material/v0003/mt_c0201e0128_top_b.mtrl",
            "a gear material resolves into the material set's variant folder");
        Require(MaterialNaming.Resolve(gear, "/mt_c0101e0005_top_a.mtrl", 1).Path == "chara/equipment/e0128/material/v0001/mt_c0101e0005_top_a.mtrl",
            "a material named for another item still loads from the model's own folder");
        var skin = MaterialNaming.Resolve(gear, "/mt_c0201b0001_bibo.mtrl", 1);
        Require(skin is { Kind: MaterialRequestKind.Skin, SkinSuffix: "bibo", SkinBody: 1 },
            "a skin material on gear depends on who wears it");
        Require(MaterialNaming.SkinPath(801, 1, "bibo") == "chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_bibo.mtrl" &&
                MaterialNaming.SkinPath(1001, 1, "bibo") == "chara/human/c0401/obj/body/b0001/material/v0001/mt_c0401b0001_bibo.mtrl",
            "skin materials resolve through the wearer's skin race");
        Require(MaterialNaming.Resolve(gear, "bg/ffxiv/wil_w1/mat/a.mtrl", 1).Path == "bg/ffxiv/wil_w1/mat/a.mtrl",
            "a full game path is requested as it is");
        Require(MaterialNaming.Resolve(gear, "notamaterial", 1).Kind == MaterialRequestKind.Invalid,
            "a name that isn't a material can't be requested");

        var sharedHair = ModelNaming.Parse("chara/human/c0801/obj/hair/h0127/model/c0801h0127_hir.mdl")!;
        Require(MaterialNaming.Resolve(sharedHair, "/mt_c0801h0127_hir_a.mtrl", 1).Path == "chara/human/c0201/obj/hair/h0127/material/v0001/mt_c0201h0127_hir_a.mtrl",
            "shared hairstyles load their materials from the Midlander root");
        Require(MaterialNaming.HairMaterialRace(801, 105) == 801 && MaterialNaming.HairMaterialRace(1401, 105) == 201 &&
                MaterialNaming.HairMaterialRace(1601, 150) == 1601 && MaterialNaming.HairMaterialRace(1301, 180) == 101 &&
                MaterialNaming.HairMaterialRace(1401, 50) == 1401,
            "Miqo'te keep their own 101-115, Hrothgar never share, 116-200 always do");

        var face = ModelNaming.Parse("chara/human/c1401/obj/face/f0001/model/c1401f0001_fac.mdl")!;
        Require(MaterialNaming.Resolve(face, "/mt_c1401f0001_fac_a.mtrl", 1).Path == "chara/human/c1401/obj/face/f0001/material/mt_c1401f0001_fac_a.mtrl",
            "faces have no variant folder");

        Require(MaterialNaming.TexturePath("chara/equipment/e0001/texture/c0101e0001_top_d.tex", dx11: true) == "chara/equipment/e0001/texture/--c0101e0001_top_d.tex" &&
                MaterialNaming.TexturePath("chara/equipment/e0001/texture/c0101e0001_top_d.tex", dx11: false) == "chara/equipment/e0001/texture/c0101e0001_top_d.tex",
            "DX11 textures are requested with the -- prefix");
        Require(ResourceNaming.Dx11Twin("chara/equipment/e0001/texture/--c0101e0001_top_d.tex") == "chara/equipment/e0001/texture/c0101e0001_top_d.tex" &&
                ResourceNaming.Dx11Twin("chara/equipment/e0001/texture/c0101e0001_top_d.tex") == "chara/equipment/e0001/texture/--c0101e0001_top_d.tex",
            "a texture's DX11 twin adds or drops the -- prefix");
    }

    private static void RecognisesGameRootsAndMangledKeys()
    {
        Require(ResourceNaming.HasValidRoot("chara/equipment/e0001/texture/a.tex") && ResourceNaming.HasValidRoot("/Chara\\Equipment/e0001/a.tex"),
            "game paths start with one of the game's top-level folders");
        const string mangled = "Core Files_ Skin Materials/Bibo Skin MTRLs/chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_bibo.mtrl";
        Require(!ResourceNaming.HasValidRoot(mangled) && ResourceNaming.IsMangledKey(mangled, out var real) &&
                real == "chara/human/c0201/obj/body/b0001/material/v0001/mt_c0201b0001_bibo.mtrl",
            "option folders glued in front of a game path are recognised, with the real path");
        Require(!ResourceNaming.IsMangledKey("images/preview.png", out _) && !ResourceNaming.IsMangledKey("chara/equipment/e0001/texture/a.tex", out _),
            "neither an image nor a valid game path is a mangled key");
        Require(ResourceNaming.RootOf("chara/human/c0801/obj/hair/h0127/texture/x.tex") == new GameRoot(GameRootKind.Hair, 127, 0, string.Empty) &&
                ResourceNaming.RootOf("chara/human/c0201/obj/hair/h0127/material/v0001/x.mtrl") == new GameRoot(GameRootKind.Hair, 127, 0, string.Empty),
            "a hairstyle's files share one root whatever race folder they are in");
        Require(ResourceNaming.RootOf("chara/human/c0201/obj/body/b0001/texture/x.tex").IsShared &&
                ResourceNaming.RootOf("chara/equipment/e0128/material/v0001/x.mtrl").Kind == GameRootKind.Equipment,
            "body files are shared; gear files belong to their set");
    }

    private static void ResolvesRaceModelsLikePenumbra()
    {
        Require(RaceInfo.Fallback(801) == 201 && RaceInfo.Fallback(201) == 101 && RaceInfo.Fallback(1501) == 901 &&
                RaceInfo.Fallback(1201) == 1101 && RaceInfo.Fallback(1601) == 201,
            "race fallbacks follow Penumbra");
        Require(RaceInfo.SkinRace(801) == 201 && RaceInfo.SkinRace(1001) == 401 && RaceInfo.SkinRace(1401) == 1401,
            "skin races follow the game");
        Require(RaceInfo.FromCustomize(4, 1, 7) == 801 && RaceInfo.FromCustomize(1, 0, 2) == 301 &&
                RaceInfo.FromCustomize(3, 1, 5) == 1201 && RaceInfo.FromCustomize(9, 0, 0) is null,
            "customize bytes give the model race");
        Require(RaceInfo.FromCode(801) is { } miqote && miqote.PenumbraRace == "Miqote" && miqote.PenumbraGender == "Female" &&
                RaceInfo.Code(new RaceGender(PlayerRace.AuRa, PlayerGender.Female)) == 1401 && RaceInfo.FromCode(104) is null,
            "race codes and Penumbra's spellings convert both ways; NPC codes have no playable race");
        Require(RaceInfo.PlayableCodes.Count == 18 && RaceInfo.PlayableCodes[0] == 101 && RaceInfo.PlayableCodes[^1] == 1801,
            "every playable race code, in order");

        static bool Only(ushort race, params ushort[] withModel) => withModel.Contains(race);
        Require(RaceInfo.ResolveModelRace(801, false, r => Only(r, 801, 201)) == 801 &&
                RaceInfo.ResolveModelRace(801, false, r => Only(r, 201)) == 201 &&
                RaceInfo.ResolveModelRace(801, false, r => Only(r, 101)) == 101 &&
                RaceInfo.ResolveModelRace(101, false, _ => false) == 101,
            "a race loads its own model, else its fallback's, else Midlander male");
        Require(RaceInfo.ResolveModelRace(1201, false, r => Only(r, 201)) == 101,
            "Lalafell female falls back to Lalafell male, never to Midlander female");

        Require(SlotInfo.ImcPartIndex(EquipSlot.Head) == 0 && SlotInfo.ImcPartIndex(EquipSlot.Earring) == 0 &&
                SlotInfo.ImcPartIndex(EquipSlot.RingLeft) == 4 && SlotInfo.EqdpShift(EquipSlot.Legs) == 6 &&
                SlotInfo.VariantAttributePrefix(EquipSlot.Body) == "atr_tv_",
            "slot helpers match the game's IMC parts, EQDP bits and variant attributes");
    }
}
