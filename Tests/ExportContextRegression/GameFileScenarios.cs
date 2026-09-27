using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Text;
using System.Text.Json;
using InstantEdit.Services;
using InstantEdit.Services.GameFiles;
using InstantEdit.Services.Skeletons;
using InstantEdit.Ui;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// The Game Files browser's pure parts: race codes, vanilla model paths, finding models by probing
/// the game's file index, where their materials live, their dependencies and names, and the export
/// that writes them to a folder with a manifest. None of these touch Dalamud.
/// </summary>
internal static class GameFileScenarios
{
    private const GameFileCategory Hair = GameFileCategory.Hair;
    private const GameFileCategory Equipment = GameFileCategory.Equipment;
    private const GameFileCategory Weapon = GameFileCategory.Weapon;

    // A V6 MDL whose string table holds only material names (see MinimalModel in Program.cs).
    private static byte[] Model(params string[] materials)
    {
        var strings = materials.SelectMany(value => Encoding.UTF8.GetBytes(value + "\0")).ToArray();
        var bytes = new byte[68 + 8 + strings.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x01000006u);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(14), (ushort)materials.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(68), (ushort)materials.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(72), (uint)strings.Length);
        strings.CopyTo(bytes, 76);
        return bytes;
    }

    // An MTRL with one texture and an empty shader section (see MinimalMaterial in Program.cs).
    private static byte[] Material(string texturePath, ushort flags)
    {
        var shader = Encoding.UTF8.GetBytes("character.shpk\0");
        var texture = Encoding.UTF8.GetBytes(texturePath + "\0");
        var strings = shader.Concat(texture).ToArray();
        var bytes = new byte[16 + 4 + strings.Length + 12];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x0103u);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), (ushort)bytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), (ushort)strings.Length);
        bytes[12] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(16), (ushort)shader.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18), flags);
        strings.CopyTo(bytes, 20);
        return bytes;
    }

    // An IMC file: the variant count, the part mask, then six bytes per stored part for the default
    // variant and each other variant. Rows hold one material ID per stored part.
    private static byte[] Imc(ushort mask, byte[] defaults, params byte[][] variants)
    {
        var parts = BitOperations.PopCount(mask);
        var bytes = new byte[4 + 6 * parts * (1 + variants.Length)];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)variants.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), mask);
        var offset = 4;
        foreach (var row in variants.Prepend(defaults))
            for (var part = 0; part < parts; part++, offset += 6)
                bytes[offset] = row[part];
        return bytes;
    }

    private static GameModelId Id(string path) => GameModelPaths.Parse(path) ?? throw new InvalidOperationException(path);

    public static void Run(string testRoot)
    {
        Races();
        Paths();
        Materials();
        Catalog();
        Dependencies();
        Names();
        ExportPaths(testRoot);
        Export(testRoot).GetAwaiter().GetResult();
        Views();
    }

    private static void Views()
    {
        ImmutableArray<GameModelEntry> hair =
        [
            GameModelEntry.For(new(Hair, 101, 1, 0, "hir")), GameModelEntry.For(new(Hair, 104, 1, 0, "hir")),
            GameModelEntry.For(new(Hair, 201, 113, 0, "hir")), GameModelEntry.For(new(Hair, 101, 113, 0, "hir")),
            GameModelEntry.For(new(Hair, 101, 200, 0, "hir")),
        ];
        var names = new GameFileNames(new Dictionary<(GameFileCategory, ushort, ushort, string), ImmutableArray<string>>(),
            new Dictionary<ushort, ImmutableHashSet<ushort>> { [101] = [1, 113], [201] = [113] },
            new Dictionary<ushort, string> { [113] = "Modern Aesthetics - Strife" });
        var filter = new GameFileFilter();
        var players = filter.Apply(hair, names);
        Require(players.Count == 4 && players.All(entry => entry.Id.Race != 104) && ReferenceEquals(filter.Apply(hair, names), players),
            "the Game Files filter shows player races by default and keeps its result until something changes");
        filter.Races = GameRaceMode.Npc;
        Require(filter.Apply(hair, names).Single().Id.Race == 104, "the NPC filter shows only NPC race codes");
        filter.Races = GameRaceMode.One;
        filter.Race = 201;
        Require(filter.Apply(hair, names).Single().Id.Race == 201, "one race can be picked");
        filter.Races = GameRaceMode.All;
        filter.SelectableOnly = true;
        Require(filter.Apply(hair, names).Select(entry => entry.GamePath).SequenceEqual(
                [hair[0].GamePath, hair[2].GamePath, hair[3].GamePath]),
            "player-selectable hairstyles leave out NPC races and styles players can't pick");
        filter.SelectableOnly = false;

        const GameFileCategory Face = GameFileCategory.Face;
        ImmutableArray<GameModelEntry> faceModels =
        [
            GameModelEntry.For(new(Face, 101, 1, 0, "fac")), GameModelEntry.For(new(Face, 101, 91, 0, "fac")),
            GameModelEntry.For(new(Face, 101, 201, 0, "fac")), GameModelEntry.For(new(Face, 104, 1, 0, "fac")),
            GameModelEntry.For(new(Face, 301, 101, 0, "fac")),
        ];
        var faceNames = new GameFileNames(new Dictionary<(GameFileCategory, ushort, ushort, string), ImmutableArray<string>>(),
            new Dictionary<ushort, ImmutableHashSet<ushort>>(), new Dictionary<ushort, string>(),
            new Dictionary<ushort, ImmutableHashSet<ushort>> { [101] = [1, 2, 3, 4, 5, 6, 7], [301] = [101, 102, 103, 104] });
        var faceFilter = new GameFileFilter();
        Require(faceFilter.Apply(faceModels, faceNames).Select(entry => entry.GamePath).SequenceEqual([faceModels[0].GamePath, faceModels[4].GamePath]),
            "player races leave out the faces only NPCs wear");
        faceFilter.Races = GameRaceMode.Npc;
        Require(faceFilter.Apply(faceModels, faceNames).Select(entry => entry.GamePath).SequenceEqual(
                [faceModels[1].GamePath, faceModels[2].GamePath, faceModels[3].GamePath]),
            "NPC races show NPC race codes and the player races' NPC-only faces");
        faceFilter.Races = GameRaceMode.One;
        faceFilter.Race = 101;
        Require(faceFilter.Apply(faceModels, faceNames).Count == 3, "one race shows all of its faces");
        faceFilter.Races = GameRaceMode.Player;
        Require(faceFilter.Apply(faceModels, GameFileNames.Empty).Select(entry => entry.GamePath).SequenceEqual(
                [faceModels[0].GamePath, faceModels[1].GamePath, faceModels[4].GamePath]),
            "until the sheets are read, player races show faces up to 104");

        filter.Text = "strife c0201";
        Require(filter.Apply(hair, names).Single().GamePath == hair[2].GamePath, "search words must all match a model's names, IDs or race");
        filter.Text = "female midlander";
        var version = filter.Version;
        Require(filter.Apply(hair, names).Single().Id.Race == 201 && filter.Version == version + 1, "race labels are searchable");
        filter.Text = "";

        var selection = new GameFileSelection();
        var all = filter.Apply(hair, names);
        selection.Set(all[1], true);
        selection.SetRange(all, all[3], true);
        Require(selection.Count == 3 && !selection.Contains(all[0]) && selection.Contains(all[2]) && !selection.Contains(all[4]),
            "shift-ticking ticks every shown model from the last ticked one");
        selection.Invert(all);
        Require(selection.Count == 2 && selection.Contains(all[0]) && selection.Contains(all[4]), "inverting flips the shown models");
        selection.SetAll(all, true);
        filter.Races = GameRaceMode.Player;
        Require(selection.Count == 5 && selection.CountIn(filter.Apply(hair, names)) == 4 &&
                selection.Entries.Select(entry => entry.GamePath).SequenceEqual(
                    [hair[0].GamePath, hair[1].GamePath, hair[3].GamePath, hair[2].GamePath, hair[4].GamePath]),
            "the selection survives filtering and lists models by ID, then race");
        selection.Clear();
        selection.SetRange(all, all[2], true);
        Require(selection.Count == 1 && selection.Contains(all[2]), "a range without an anchor ticks just the clicked model");

        var rows = new GameFileRowList();
        var visible = filter.Apply(hair, names);
        var states = new Dictionary<string, GameFileDependencyState>
        {
            [hair[0].GamePath] = new(false, new GameModelDependencies(hair[0].GamePath, [1],
            [
                new GameFileMaterial("/mt_c0101h0001_hir_a.mtrl", "chara/human/c0101/obj/hair/h0001/material/v0001/mt_c0101h0001_hir_a.mtrl", 1, true,
                    [new GameFileTexture("chara/human/c0101/obj/hair/h0001/texture/c0101h0001_hir_norm.tex", "normal", true)], ""),
                new GameFileMaterial("/mt_c0101h0001_hir_b.mtrl", "chara/human/c0101/obj/hair/h0001/material/v0001/mt_c0101h0001_hir_b.mtrl", 1, false, [], "gone"),
            ], ["a note"]), ""),
        };
        var skeleton = new SkeletonFileSet(["chara/human/c0101/skeleton/base/b0001/skl_c0101b0001.sklb",
            "chara/human/c0101/skeleton/hair/h0003/skl_c0101h0003.sklb"], new EstRequest(EstSlot.Hair, 1), 3, []);
        GameFileDependencyState StateOf(GameModelEntry entry) => states.GetValueOrDefault(entry.GamePath, new GameFileDependencyState(true, null, ""));
        var closed = rows.Build(visible, 0, StateOf, _ => skeleton);
        Require(closed.Count == visible.Count && closed.All(row => row.Kind == GameFileRowKind.Model) &&
                ReferenceEquals(rows.Build(visible, 5, StateOf, _ => skeleton), closed),
            "closed models are one line each, and nothing rebuilds while none is open");
        rows.Toggle(visible[0]);
        rows.Toggle(visible[1]);
        var opened = rows.Build(visible, 0, StateOf, _ => skeleton);
        Require(opened.Select(row => row.Kind).SequenceEqual([
                    GameFileRowKind.Model, GameFileRowKind.Material, GameFileRowKind.Texture, GameFileRowKind.Material, GameFileRowKind.Note,
                    GameFileRowKind.Skeleton, GameFileRowKind.Model, GameFileRowKind.Note, GameFileRowKind.Skeleton,
                    GameFileRowKind.Model, GameFileRowKind.Model]) &&
                opened[3] is { Found: false, Detail: "Not in the game data" } &&
                opened[5].Label == "Skeleton: skl_c0101b0001.sklb + skl_c0101h0003.sklb (EST hair 1 → 3)" &&
                opened[7].Label.StartsWith("Reading", StringComparison.Ordinal),
            "an opened model lists its materials, their textures, notes and its skeleton; one still reading says so");
        Require(!ReferenceEquals(rows.Build(visible, 1, StateOf, _ => skeleton), opened),
            "open models rebuild when their dependencies arrive");
    }

    private static void Races()
    {
        Require(GameRaces.All.Length == 38 && GameRaces.All.Select(race => race.Code).Distinct().Count() == 38 &&
                GameRaces.All.Count(race => race.Player) == 18 && GameRaces.All.All(race => race.Player == (race.Code % 100 == 1)),
            "the race table has the 18 player codes, their NPC codes and the two other NPC bodies");
        Require(GameRaces.Label(101) == "Male Midlander" && GameRaces.Label(401) == "Female Highlander" &&
                GameRaces.Label(1804) == "Female Viera NPC" && GameRaces.Label(9204) == "Other Female NPC" &&
                GameRaces.Label(4242) == "c4242" && GameRaces.Find(1101)!.Id == "c1101",
            "race codes read as race and gender, and unknown codes as themselves");
        Require(GameRaces.PlayerCode(1, 1, 0) == 101 && GameRaces.PlayerCode(1, 2, 1) == 401 && GameRaces.PlayerCode(2, 4, 0) == 501 &&
                GameRaces.PlayerCode(3, 5, 1) == 1201 && GameRaces.PlayerCode(4, 8, 1) == 801 && GameRaces.PlayerCode(8, 15, 0) == 1701 &&
                GameRaces.PlayerCode(9, 1, 0) is null && GameRaces.PlayerCode(1, 1, 2) is null,
            "character-creation races, tribes and genders map to player race codes");
        Require(GameRaces.FaceModel(1, 7) == 7 && GameRaces.FaceModel(2, 1) == 101 && GameRaces.FaceModel(10, 4) == 104 &&
                GameRaces.FaceModel(12, 1) == 1 && GameRaces.FaceModel(13, 5) == 5 && GameRaces.FaceModel(16, 4) == 4,
            "the second clans of the first five races wear the face models 100 up; Au Ra, Hrothgar and Viera clans share theirs");
    }

    private static void Paths()
    {
        var samples = new (GameModelId Id, string Path)[]
        {
            (new(Hair, 101, 113, 0, "hir"), "chara/human/c0101/obj/hair/h0113/model/c0101h0113_hir.mdl"),
            (new(GameFileCategory.Face, 104, 202, 0, "fac"), "chara/human/c0104/obj/face/f0202/model/c0104f0202_fac.mdl"),
            (new(GameFileCategory.Tail, 1401, 5, 0, "til"), "chara/human/c1401/obj/tail/t0005/model/c1401t0005_til.mdl"),
            (new(GameFileCategory.Ears, 1801, 4, 0, "zer"), "chara/human/c1801/obj/zear/z0004/model/c1801z0004_zer.mdl"),
            (new(GameFileCategory.Body, 9104, 1, 0, "glv"), "chara/human/c9104/obj/body/b0001/model/c9104b0001_glv.mdl"),
            (new(Equipment, 201, 6016, 0, "top"), "chara/equipment/e6016/model/c0201e6016_top.mdl"),
            (new(GameFileCategory.Accessory, 101, 1, 0, "ril"), "chara/accessory/a0001/model/c0101a0001_ril.mdl"),
            (new(Weapon, 0, 101, 1, ""), "chara/weapon/w0101/obj/body/b0001/model/w0101b0001.mdl"),
        };
        Require(samples.All(sample => GameModelPaths.ModelPath(sample.Id) == sample.Path && GameModelPaths.Parse(sample.Path) == sample.Id),
            "every category's model path is built from its IDs and parsed back to them, NPC races included");
        Require(GameModelPaths.Parse(@"CHARA\human\c0101\obj\hair\h0113\model\c0101h0113_hir.mdl") == samples[0].Id &&
                GameModelPaths.Parse("/chara/weapon/w0101/obj/body/b0001/model/w0101b0001.mdl") == samples[^1].Id,
            "model paths parse regardless of slashes and case");
        string?[] rejected =
        [
            "chara/human/c0101/obj/hair/h0113/model/c0101h0114_hir.mdl",
            "chara/human/c0101/obj/hair/h0113/model/c0101h0113_fac.mdl",
            "chara/human/c0101/obj/face/h0113/model/c0101h0113_hir.mdl",
            "chara/human/c0101/obj/body/b0001/model/c0101b0001_met.mdl",
            "chara/equipment/e0001/model/c0101e0001_hir.mdl",
            "chara/accessory/a0001/model/c0101a0001_top.mdl",
            "chara/weapon/w0101/obj/body/b0001/model/w0101b0002.mdl",
            "chara/human/c0101/obj/hair/h0113/model/../model/c0101h0113_hir.mdl",
            "../chara/human/c0101/obj/hair/h0113/model/c0101h0113_hir.mdl",
            "chara/monster/m0001/obj/body/b0001/model/m0001b0001.mdl",
            "chara/human/c0101/obj/hair/h0113/material/v0001/mt_c0101h0113_hir_a.mtrl",
            "",
            null,
        ];
        Require(rejected.All(path => GameModelPaths.Parse(path) is null),
            "paths whose folders, IDs or slots disagree, climb folders or aren't v1 models are not models");
        Require(GameModelPaths.ImcPath(samples[5].Id) == "chara/equipment/e6016/e6016.imc" &&
                GameModelPaths.ImcPath(samples[6].Id) == "chara/accessory/a0001/a0001.imc" &&
                GameModelPaths.ImcPath(samples[^1].Id) == "chara/weapon/w0101/obj/body/b0001/b0001.imc" &&
                GameModelPaths.ImcPath(samples[0].Id) is null &&
                GameModelPaths.ImcPart(samples[5].Id) == 1 && GameModelPaths.ImcPart(samples[6].Id) == 4 &&
                GameModelPaths.ImcPart(samples[^1].Id) == 0 && GameModelPaths.ImcPart(samples[0].Id) == -1,
            "gear and weapons name their IMC file and part; character parts have none");
        Require(samples[^1].Id.IdLabel == "w0101b0001" && samples[0].Id.IdLabel == "h0113" && samples[3].Id.IdLabel == "z0004" &&
                GameModelPaths.SlotLabel("ril") == "Left ring" && GameModelPaths.CategoryLabel(GameFileCategory.Ears) == "Viera ears",
            "model IDs and slots have display labels");
    }

    private static void Materials()
    {
        Require(VanillaMaterialPaths.Resolve("chara/human/c0101/obj/hair/h0103/model/c0101h0103_hir.mdl", "/mt_c0101h0001_hir_a.mtrl") ==
                "chara/human/c0101/obj/hair/h0001/material/v0001/mt_c0101h0001_hir_a.mtrl" &&
                VanillaMaterialPaths.Resolve("chara/human/c0104/obj/hair/h0010/model/c0104h0010_hir.mdl", "/mt_c0101h0010_hir_a.mtrl", 4) ==
                "chara/human/c0101/obj/hair/h0010/material/v0001/mt_c0101h0010_hir_a.mtrl" &&
                VanillaMaterialPaths.Resolve("chara/human/c0701/obj/tail/t0008/model/c0701t0008_til.mdl", "/mt_c0701t0004_a.mtrl") ==
                "chara/human/c0701/obj/tail/t0004/material/v0001/mt_c0701t0004_a.mtrl",
            "character-part materials live in the folder their name encodes, whichever style uses them");
        Require(VanillaMaterialPaths.Resolve("chara/human/c0101/obj/face/f0001/model/c0101f0001_fac.mdl", "/mt_c0101f0001_iri_a.mtrl") ==
                "chara/human/c0101/obj/face/f0001/material/mt_c0101f0001_iri_a.mtrl" &&
                VanillaMaterialPaths.Resolve("chara/human/c1701/obj/zear/z0001/model/c1701z0001_zer.mdl", "/mt_c1701z0001_fac_a.mtrl") ==
                "chara/human/c1701/obj/zear/z0001/material/mt_c1701z0001_fac_a.mtrl",
            "face and ear materials have no variant folder");
        Require(VanillaMaterialPaths.Resolve("chara/equipment/e0007/model/c0101e0007_glv.mdl", "/mt_c0101b0001_a.mtrl", 3) ==
                "chara/human/c0101/obj/body/b0001/material/v0001/mt_c0101b0001_a.mtrl" &&
                VanillaMaterialPaths.Resolve("chara/equipment/e6016/model/c0101e6016_top.mdl", "/mt_c0201e6016_top_a.mtrl", 3) ==
                "chara/equipment/e6016/material/v0003/mt_c0201e6016_top_a.mtrl" &&
                VanillaMaterialPaths.Resolve("chara/accessory/a0041/model/c0101a0041_nek.mdl", "mt_c0101a0041_nek_a.mtrl", 2) ==
                "chara/accessory/a0041/material/v0002/mt_c0101a0041_nek_a.mtrl",
            "skin materials live in the body folder, gear materials in the set's IMC variant");
        Require(VanillaMaterialPaths.Resolve("chara/weapon/w0351/obj/body/b0030/model/w0351b0030.mdl", "/mt_w0301b0030_a.mtrl", 2) ==
                "chara/weapon/w0301/obj/body/b0030/material/v0002/mt_w0301b0030_a.mtrl" &&
                VanillaMaterialPaths.MainHandMaterial("chara/weapon/w3051/obj/body/b0001/material/v0001/mt_w3051b0001_a.mtrl") ==
                "chara/weapon/w3001/obj/body/b0001/material/v0001/mt_w3001b0001_a.mtrl" &&
                VanillaMaterialPaths.MainHandMaterial("chara/weapon/w0301/obj/body/b0030/material/v0001/mt_w0301b0030_a.mtrl") is null &&
                VanillaMaterialPaths.MainHandMaterial("chara/equipment/e0001/material/v0001/mt_c0101e0001_top_a.mtrl") is null,
            "off-hands use the main hand's material their name gives, or the one 50 IDs below");
        Require(VanillaMaterialPaths.Resolve("chara/equipment/e0001/model/c0101e0001_top.mdl", "/readme.txt") is null &&
                VanillaMaterialPaths.Resolve("chara/equipment/e0001/model/c0101e0001_top.mdl", "/mt_a.mtrl", -1) is null,
            "names that aren't materials resolve to nothing");
        var warnings = new List<string>();
        Require(MaterialPreviewBundleBuilder.ResolveMaterialPath("chara/human/c0101/obj/hair/h0103/model/c0101h0103_hir.mdl",
                    "/mt_c0101h0001_hir_a.mtrl", new Dictionary<string, List<string>>(), warnings) ==
                "chara/human/c0101/obj/hair/h0001/material/v0001/mt_c0101h0001_hir_a.mtrl" && warnings.Count == 0,
            "vanilla model previews find a hairstyle's shared material where the game keeps it");
    }

    private static void Catalog()
    {
        var hairFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "chara/human/c0101/obj/hair/h0999/model/c0101h0999_hir.mdl",
            "chara/human/c0101/obj/hair/h0001/model/c0101h0001_hir.mdl",
            "chara/human/c0104/obj/hair/h0010/model/c0104h0010_hir.mdl",
            "chara/human/c9204/obj/hair/h0002/model/c9204h0002_hir.mdl",
            "chara/human/c0101/obj/hair/h1000/model/c0101h1000_hir.mdl",
            "chara/human/c0101/obj/face/f0001/model/c0101f0001_fac.mdl",
        };
        var builder = new GameFileCatalogBuilder(hairFiles.Contains, _ => null);
        var hair = builder.Enumerate(Hair);
        Require(hair.Select(entry => entry.GamePath).SequenceEqual([
                    "chara/human/c0101/obj/hair/h0001/model/c0101h0001_hir.mdl",
                    "chara/human/c9204/obj/hair/h0002/model/c9204h0002_hir.mdl",
                    "chara/human/c0104/obj/hair/h0010/model/c0104h0010_hir.mdl",
                    "chara/human/c0101/obj/hair/h0999/model/c0101h0999_hir.mdl"]) &&
                hair.All(entry => GameModelPaths.ModelPath(entry.Id) == entry.GamePath),
            "hair models are found for every race code up to ID 999, in ID then race order");
        Require(builder.Probes == 38 * 999, "character parts cost one probe per race code and ID");

        var gearFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "chara/equipment/e0001/e0001.imc",
            "chara/equipment/e0001/model/c0101e0001_top.mdl",
            "chara/equipment/e0001/model/c0101e0001_met.mdl",
            "chara/equipment/e0002/model/c0201e0002_top.mdl",
            "chara/equipment/e0003/e0003.imc",
            "chara/equipment/e0003/model/c0301e0003_sho.mdl",
        };
        var gearBytes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["chara/equipment/e0001/e0001.imc"] = Imc(0b00010, [2], [2], [4]),
            ["chara/equipment/e0003/e0003.imc"] = [1, 2, 3],
        };
        var gearBuilder = new GameFileCatalogBuilder(gearFiles.Contains, path => gearBytes.GetValueOrDefault(path));
        var gear = gearBuilder.Enumerate(Equipment);
        Require(gear.Select(entry => entry.GamePath).SequenceEqual([
                    "chara/equipment/e0001/model/c0101e0001_top.mdl",
                    "chara/equipment/e0003/model/c0301e0003_sho.mdl"]) &&
                gearBuilder.Probes == 10_000 + 38 + 5 * 38,
            "gear sets are found through their IMC file, which names their slots (all of them when unreadable)");

        var weaponFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "chara/weapon/w0101/skeleton/base/b0001/skl_w0101b0001.sklb",
            "chara/weapon/w0101/obj/body/b0001/model/w0101b0001.mdl",
            "chara/weapon/w0101/obj/body/b0999/model/w0101b0999.mdl",
            "chara/weapon/w3302/obj/body/b0002/model/w3302b0002.mdl",
            "chara/weapon/w0201/obj/body/b1500/model/w0201b1500.mdl",
            "chara/weapon/w0301/obj/body/b1500/model/w0301b1500.mdl",
            "chara/weapon/w0401/obj/body/b0050/model/w0401b0050.mdl",
        };
        var weapons = new GameFileCatalogBuilder(weaponFiles.Contains, _ => null).Enumerate(Weapon, [(201, 1500)]);
        Require(weapons.Select(entry => entry.Id.IdLabel).SequenceEqual(["w0101b0001", "w0101b0999", "w0201b1500", "w3302b0002"]),
            "weapons are found from their skeleton, their first bodies and the Item sheet's pairs");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var stopped = false;
        try { builder.Enumerate(GameFileCategory.Face, token: cancelled.Token); }
        catch (OperationCanceledException) { stopped = true; }
        Require(stopped, "a cancelled enumeration stops");
    }

    private static void Dependencies()
    {
        const string gearModel = "chara/equipment/e0001/model/c0101e0001_top.mdl";
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["chara/equipment/e0001/e0001.imc"] = Imc(0b00010, [3], [3], [5], [3]),
            [gearModel] = Model("/mt_c0101e0001_top_a.mtrl", "/mt_c0101b0001_a.mtrl", "/mt_c0101e0001_top_b.mtrl"),
            ["chara/equipment/e0001/material/v0003/mt_c0101e0001_top_a.mtrl"] =
                Material("chara/equipment/e0001/texture/v03_c0101e0001_top_norm.tex", 0x8000),
            ["chara/equipment/e0001/material/v0005/mt_c0101e0001_top_a.mtrl"] =
                Material("chara/equipment/e0001/texture/v05_c0101e0001_top_norm.tex", 0x8000),
            ["chara/equipment/e0001/texture/--v03_c0101e0001_top_norm.tex"] = [1],
            ["chara/equipment/e0001/texture/v05_c0101e0001_top_norm.tex"] = [1],
            ["chara/human/c0101/obj/body/b0001/material/v0001/mt_c0101b0001_a.mtrl"] = Material("chara/common/texture/skin_m.tex", 0),
        };
        byte[]? Read(string path) => files.GetValueOrDefault(path);
        var id = Id(gearModel);
        var variants = GameFileDependencies.MaterialVariants(id, Read);
        Require(variants.SequenceEqual([3, 5]) &&
                GameFileDependencies.MaterialVariants(Id("chara/equipment/e0009/model/c0101e0009_top.mdl"), Read).SequenceEqual([1]) &&
                GameFileDependencies.MaterialVariants(Id("chara/human/c0101/obj/hair/h0001/model/c0101h0001_hir.mdl"), Read).SequenceEqual([1]),
            "a gear slot's material variants are its IMC default first, then the others; without an IMC, 1");

        var dependencies = GameFileDependencies.Resolve(gearModel, files[gearModel], variants, files.ContainsKey, Read);
        Require(dependencies.Materials is
                [
                    { GamePath: "chara/equipment/e0001/material/v0003/mt_c0101e0001_top_a.mtrl", Variant: 3, Found: true,
                        Textures: [{ GamePath: "chara/equipment/e0001/texture/--v03_c0101e0001_top_norm.tex", Found: true }] },
                    { GamePath: "chara/equipment/e0001/material/v0005/mt_c0101e0001_top_a.mtrl", Variant: 5, Found: true,
                        Textures: [{ GamePath: "chara/equipment/e0001/texture/v05_c0101e0001_top_norm.tex", Found: true }] },
                    { GamePath: "chara/human/c0101/obj/body/b0001/material/v0001/mt_c0101b0001_a.mtrl", Found: true,
                        Textures: [{ GamePath: "chara/common/texture/skin_m.tex", Found: false }] },
                    { GamePath: "chara/equipment/e0001/material/v0003/mt_c0101e0001_top_b.mtrl", Found: false, Problem.Length: > 0 },
                    { GamePath: "chara/equipment/e0001/material/v0005/mt_c0101e0001_top_b.mtrl", Found: false },
                ],
            "a model's materials resolve in each variant, the shared skin once, with DX11 textures when the game has them");
        Reject(() => GameFileDependencies.Resolve(gearModel, [1, 2, 3], variants, files.ContainsKey, Read),
            "a model file that isn't an MDL has no dependencies");
    }

    private static void Names()
    {
        var names = new GameFileNames(
            new Dictionary<(GameFileCategory, ushort, ushort, string), ImmutableArray<string>>
            {
                [(Equipment, 6016, 0, "top")] = ["Housemaid's Dress", "Loyal Housemaid's Dress"],
                [(Weapon, 101, 1, "")] = ["Goatskin Targe"],
            },
            new Dictionary<ushort, ImmutableHashSet<ushort>> { [101] = [1, 113] },
            new Dictionary<ushort, string> { [113] = "Modern Aesthetics - Strife" });
        Require(names.For(new(Equipment, 201, 6016, 0, "top")).SequenceEqual(["Housemaid's Dress", "Loyal Housemaid's Dress"]) &&
                names.For(new(Equipment, 201, 6016, 0, "met")).IsEmpty &&
                names.For(new(Weapon, 0, 101, 1, "")).SequenceEqual(["Goatskin Targe"]) &&
                names.For(new(Weapon, 0, 101, 2, "")).IsEmpty &&
                names.For(new(Hair, 1801, 113, 0, "hir")).SequenceEqual(["Modern Aesthetics - Strife"]),
            "gear and weapon models are named after their items, hairstyles after their unlock item");
        Require(names.PlayerSelectable(new(Hair, 101, 113, 0, "hir")) == true && names.PlayerSelectable(new(Hair, 101, 200, 0, "hir")) == false &&
                names.PlayerSelectable(new(Hair, 104, 113, 0, "hir")) == false &&
                names.PlayerSelectable(new(GameFileCategory.Face, 101, 1, 0, "fac")) is null &&
                GameFileNames.Empty.PlayerSelectable(new(Hair, 101, 113, 0, "hir")) is null,
            "a hairstyle is player-selectable per race; it is unknown for other models and before the sheets load");

        const GameFileCategory Face = GameFileCategory.Face;
        var faces = new GameFileNames(new Dictionary<(GameFileCategory, ushort, ushort, string), ImmutableArray<string>>(),
            new Dictionary<ushort, ImmutableHashSet<ushort>>(), new Dictionary<ushort, string>(),
            new Dictionary<ushort, ImmutableHashSet<ushort>> { [101] = [1, 2, 3, 4, 5, 6, 7], [1501] = [5, 6, 7, 8] });
        Require(faces.HasFaces && faces.PlayerSelectable(new(Face, 101, 7, 0, "fac")) == true &&
                faces.PlayerSelectable(new(Face, 101, 91, 0, "fac")) == false && faces.PlayerSelectable(new(Face, 1501, 1, 0, "fac")) == false &&
                faces.PlayerSelectable(new(Face, 301, 101, 0, "fac")) == false && faces.PlayerSelectable(new(Hair, 101, 1, 0, "hir")) is null,
            "a face is player-selectable per race");
        Require(faces.PlayerModel(new(Face, 101, 7, 0, "fac")) && !faces.PlayerModel(new(Face, 101, 91, 0, "fac")) &&
                !faces.PlayerModel(new(Face, 1501, 2, 0, "fac")) && !faces.PlayerModel(new(Face, 104, 1, 0, "fac")) &&
                faces.PlayerModel(new(Hair, 101, 200, 0, "hir")) && !faces.PlayerModel(new(Hair, 104, 1, 0, "hir")),
            "player models are the player races' models, without the faces only NPCs wear");
        Require(GameFileNames.Empty.PlayerModel(new(Face, 301, 104, 0, "fac")) && !GameFileNames.Empty.PlayerModel(new(Face, 301, 105, 0, "fac")) &&
                !GameFileNames.Empty.PlayerModel(new(Face, 101, 201, 0, "fac")),
            "until the sheets are read, faces above 104 count as NPC faces");
    }

    private static void ExportPaths(string testRoot)
    {
        var root = Path.Combine(testRoot, "GameExportPaths");
        var cache = Path.Combine(testRoot, "Cache");
        var file = Path.Combine(testRoot, "not-a-folder.txt");
        File.WriteAllText(file, "x");
        var mods = Path.Combine(testRoot, "Mods");
        (string, string)[] forbidden = [(mods, "Penumbra's mod folder"), ("", "nothing"), ("relative", "a relative folder")];
        var cacheExports = GameExportPaths.CacheExportFolder(cache);
        Require(GameExportPaths.RootProblem(root, forbidden, cache) is null &&
                GameExportPaths.RootProblem(Path.Combine(mods, "exports"), forbidden, cache) == "Choose a folder outside Penumbra's mod folder." &&
                GameExportPaths.RootProblem(@"relative\exports", forbidden, cache) is not null &&
                GameExportPaths.RootProblem(" ", forbidden, cache) is not null && GameExportPaths.RootProblem(file, forbidden, cache) is not null,
            "exports need a full folder path outside Penumbra's mods and the game");
        Require(cacheExports == Path.Combine(cache, "game-exports") &&
                GameExportPaths.RootProblem(cacheExports, forbidden, cache) is null &&
                GameExportPaths.RootProblem(Path.Combine(cacheExports, "hair"), forbidden, cache) is null &&
                GameExportPaths.RootProblem(Path.Combine(cache, "exports"), forbidden, cache) == $"In Instant Edit's cache folder, export to {cacheExports}." &&
                GameExportPaths.RootProblem(cache, forbidden, cache) is not null &&
                GameExportPaths.RootProblem(Path.Combine(cache, "game-exports-old"), forbidden, cache) is not null,
            "in the cache folder, only its export folder takes exports");
        Require(GameExportPaths.TryOutputPath(root, "chara/a/b.mdl", out var full) && full == Path.Combine(root, "chara", "a", "b.mdl") &&
                !GameExportPaths.TryOutputPath(root, "../escape.mdl", out _) && !GameExportPaths.TryOutputPath(root, "chara/../../escape.mdl", out _) &&
                !GameExportPaths.TryOutputPath(root, "chara/file.mdl:stream", out _) && !GameExportPaths.TryOutputPath(root, @"C:\escape.mdl", out _) &&
                !GameExportPaths.TryOutputPath(root, "", out _),
            "game files are written at their game path and never outside the export folder");
        Require(GameExportPaths.SkeletonJsonPath("chara/human/c0101/skeleton/hair/h0114/skl_c0101h0114.sklb") ==
                "chara/human/c0101/skeleton/hair/h0114/skl_c0101h0114.skeleton.json" &&
                GameExportPaths.ManifestName(new DateTime(2026, 9, 26, 18, 5, 3, DateTimeKind.Utc)) == "instant-edit-export-20260926-180503.json",
            "decoded skeletons sit next to their skeleton file, manifests are named by their start time");

        // Cleanup: files older than the cutoff go, then the folders that leaves empty; the export folder stays.
        var now = DateTime.UtcNow;
        var cutoff = now - GameExportPaths.CacheRetention;
        string Put(string relative, DateTime written)
        {
            var path = Path.Combine(cacheExports, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[10]);
            File.SetLastWriteTimeUtc(path, written);
            return path;
        }
        var oldModel = Put(@"chara\human\c0101\obj\hair\h0001\model\c0101h0001_hir.mdl", now.AddDays(-8));
        var oldMaterial = Put(@"chara\human\c0101\obj\hair\h0001\material\v0001\mt_c0101h0001_hir_a.mtrl", now.AddDays(-8));
        var sharedOld = Put(@"chara\human\c0101\skeleton\base\b0001\skl_c0101b0001.sklb", now.AddDays(-9));
        var fresh = Put(@"chara\human\c0101\skeleton\base\b0001\eid_c0101b0001.eid", now.AddHours(-1));
        var oldManifest = Put("instant-edit-export-20260918-120000.json", now.AddDays(-9));
        var newManifest = Put("instant-edit-export-20260927-120000.json", now.AddHours(-1));
        Directory.CreateDirectory(Path.Combine(cacheExports, "chara", "empty"));
        var (removed, removedBytes) = GameExportPaths.Clean(cacheExports, cutoff);
        Require(removed == 4 && removedBytes == 40 && !File.Exists(oldModel) && !File.Exists(oldMaterial) && !File.Exists(sharedOld) &&
                !File.Exists(oldManifest) && File.Exists(fresh) && File.Exists(newManifest) &&
                !Directory.Exists(Path.Combine(cacheExports, "chara", "human", "c0101", "obj")) &&
                !Directory.Exists(Path.Combine(cacheExports, "chara", "empty")) &&
                Directory.Exists(Path.GetDirectoryName(fresh)),
            "cache cleanup removes export files no export wrote since the cutoff, and the folders left empty");
        File.SetLastWriteTimeUtc(fresh, now.AddDays(-30));
        File.SetLastWriteTimeUtc(newManifest, now.AddDays(-30));
        Require(GameExportPaths.Clean(cacheExports, cutoff) == (2, 20) && Directory.Exists(cacheExports) &&
                !Directory.EnumerateFileSystemEntries(cacheExports).Any() &&
                GameExportPaths.Clean(Path.Combine(cache, "missing"), cutoff) == (0, 0),
            "the export folder itself stays when it is emptied, and a missing folder is left alone");
    }

    private static async Task Export(string testRoot)
    {
        const string hairA = "chara/human/c0101/obj/hair/h0103/model/c0101h0103_hir.mdl";
        const string hairB = "chara/human/c0101/obj/hair/h0104/model/c0101h0104_hir.mdl";
        const string missing = "chara/human/c0101/obj/hair/h0105/model/c0101h0105_hir.mdl";
        const string material = "chara/human/c0101/obj/hair/h0001/material/v0001/mt_c0101h0001_hir_a.mtrl";
        const string texture = "chara/human/c0101/obj/hair/h0001/texture/c0101h0001_hir_norm.tex";
        const string body = "chara/human/c0101/skeleton/base/b0001/skl_c0101b0001.sklb";
        const string hairSkeleton = "chara/human/c0101/skeleton/hair/h0114/skl_c0101h0114.sklb";
        var game = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            [hairA] = Model("/mt_c0101h0001_hir_a.mtrl"),
            [hairB] = Model("/mt_c0101h0001_hir_a.mtrl"),
            [material] = Material(texture, 0),
            [texture] = [7, 7, 7],
            [body] = [1],
            [hairSkeleton] = [2],
            [EstTable.GamePath(EstSlot.Hair)] = [3],
            ["../escape.mdl"] = [4],
        };
        SkeletonFileSet? Skeletons(string path) => path is hairA or hairB
            ? new SkeletonFileSet([body, hairSkeleton], new EstRequest(EstSlot.Hair, Id(path).Id), 114, [])
            : null;
        var decodes = new List<string>();
        var cancel = new CancellationTokenSource();
        var cancelOnDecode = false;
        var source = new GameExportSource(path => game.GetValueOrDefault(path), game.ContainsKey, Skeletons,
            (path, token) =>
            {
                decodes.Add(path);
                if (cancelOnDecode)
                {
                    cancel.Cancel();
                    token.ThrowIfCancellationRequested();
                }
                return Task.FromResult<(ModelSkeletonPayload?, string?)>((new ModelSkeletonPayload(ModelSkeleton.Schema, ModelSkeleton.Version,
                    ModelSkeleton.FilesSource, "c0101", [body, hairSkeleton], [], [new ModelSkeletonPayloadBone("n_root", -1, new float[10])]), null));
            },
            id => id.Id == 103 ? ["Hairstyle 103"] : [], id => id.Id == 103 ? true : null, "1.2.3", "2026.09.26");
        GameModelEntry[] models =
        [
            GameModelEntry.For(Id(hairA)), GameModelEntry.For(Id(hairB)), GameModelEntry.For(Id(missing)),
            new GameModelEntry(Id(hairA) with { Id = 9 }, "../escape.mdl"),
        ];
        var root = Path.Combine(testRoot, "GameExport");
        var options = new GameExportOptions(true, true, false, true, true);
        var result = await new GameFileExporter(source).RunAsync(models, root, options, null, CancellationToken.None);
        Require(result is { Status: GameExportResult.Completed, Models: 2, Files: 8, Error: null } &&
                result.Failures.Select(failure => failure.GamePath).SequenceEqual([missing, "../escape.mdl"]) &&
                !File.Exists(Path.Combine(testRoot, "escape.mdl")),
            "an export writes each file once, records models it can't read or place, and keeps going");
        Require(new[] { hairA, hairB, material, texture, body, hairSkeleton, EstTable.GamePath(EstSlot.Hair),
                    "chara/human/c0101/skeleton/hair/h0114/skl_c0101h0114.skeleton.json" }
                    .All(path => File.Exists(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)))) &&
                File.ReadAllBytes(Path.Combine(root, texture)).SequenceEqual(game[texture]) &&
                decodes.Count == 1 && !Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).Any(),
            "models, their shared material and texture, skeleton files, the EST table and one decoded skeleton land at their game paths");
        var manifest = JsonDocument.Parse(File.ReadAllBytes(result.ManifestPath!)).RootElement;
        var first = manifest.GetProperty("models")[0];
        var skeleton = first.GetProperty("skeleton");
        Require(manifest.GetProperty("schema").GetString() == GameFileExporter.ManifestSchema &&
                manifest.GetProperty("status").GetString() == "completed" && manifest.GetProperty("plugin").GetString() == "1.2.3" &&
                manifest.GetProperty("models").GetArrayLength() == 2 && manifest.GetProperty("failures").GetArrayLength() == 2 &&
                manifest.GetProperty("totals").GetProperty("files").GetInt32() == 8 &&
                manifest.GetProperty("options").GetProperty("skeletonJson").GetBoolean() &&
                first.GetProperty("gamePath").GetString() == hairA && first.GetProperty("race").GetString() == "c0101" &&
                first.GetProperty("raceLabel").GetString() == "Male Midlander" && !first.GetProperty("npc").GetBoolean() &&
                first.GetProperty("names")[0].GetString() == "Hairstyle 103" && first.GetProperty("playerSelectable").GetBoolean() &&
                !first.TryGetProperty("secondaryId", out _) &&
                first.GetProperty("materials")[0].GetProperty("gamePath").GetString() == material &&
                first.GetProperty("materials")[0].GetProperty("textures")[0].GetProperty("found").GetBoolean() &&
                skeleton.GetProperty("files").GetArrayLength() == 2 &&
                skeleton.GetProperty("est").GetProperty("set").GetInt32() == 103 && skeleton.GetProperty("est").GetProperty("skeleton").GetInt32() == 114 &&
                skeleton.GetProperty("json").GetString() == "chara/human/c0101/skeleton/hair/h0114/skl_c0101h0114.skeleton.json" &&
                !manifest.GetProperty("models")[1].TryGetProperty("playerSelectable", out _),
            "the manifest records each model, its materials, its skeleton files with their EST entry and decoded skeleton, and the failures");

        cancelOnDecode = true;
        var cancelledRoot = Path.Combine(testRoot, "GameExportCancelled");
        var cancelled = await new GameFileExporter(source).RunAsync(models[..2], cancelledRoot, options, null, cancel.Token);
        Require(cancelled is { Status: GameExportResult.Cancelled, ManifestPath: not null } &&
                JsonDocument.Parse(File.ReadAllBytes(cancelled.ManifestPath!)).RootElement.GetProperty("status").GetString() == "cancelled",
            "a cancelled export still writes its manifest, marked cancelled");

        var blockedRoot = Path.Combine(testRoot, "GameExportBlocked");
        Directory.CreateDirectory(blockedRoot);
        File.WriteAllText(Path.Combine(blockedRoot, "chara"), "a file where the chara folder goes");
        var blocked = await new GameFileExporter(source with { DecodeSkeleton = null }).RunAsync(models[..1], blockedRoot, options, null, CancellationToken.None);
        Require(blocked is { Status: GameExportResult.Failed, Error.Length: > 0, ManifestPath: not null },
            "an export that can't write stops as failed and says why");
    }
}
