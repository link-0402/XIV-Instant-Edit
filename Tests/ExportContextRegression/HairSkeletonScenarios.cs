using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using InstantEdit.Models;
using InstantEdit.Services;
using InstantEdit.Services.PenumbraMods;
using InstantEdit.Services.Skeletons;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// Custom hair skeletons: free skeleton IDs, vanilla templates, the IDs the mod library uses, and the
/// rig request with the bone layout made from it, and a mod's hair skeleton files written, mapped and removed.
/// </summary>
internal static class HairSkeletonScenarios
{
    public static void Run(string testRoot)
    {
        IdsAvoidVanillaAndLibrary();
        TemplateIsLowestExisting();
        LibraryScan(Path.Combine(testRoot, "HairIds"));
        RequestValidation();
        LayoutKeepsModelSpace();
        PrepareAndApplyMapsEveryRace(Path.Combine(testRoot, "HairApply"));
        MappingsFollowEachOption(Path.Combine(testRoot, "HairOptions"));
        RaceWithoutTemplateIsSkipped(Path.Combine(testRoot, "HairNoTemplate"));
        DroppedRaceIsUnmapped(Path.Combine(testRoot, "HairDroppedRace"));
        ExportWithoutRigRemovesOnlyOurs(Path.Combine(testRoot, "HairRemove"));
        RefusesHairNotUsedAsHair(Path.Combine(testRoot, "HairNotHair"));
    }

    /// <summary>An EST table's bytes: a count, the (set, gender-race) pairs, then one entry per pair.</summary>
    private static byte[] EstBytes((ushort GenderRace, ushort Set, ushort Entry)[] rows)
    {
        var bytes = new byte[4 + rows.Length * 6];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, rows.Length);
        for (var i = 0; i < rows.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4 + i * 4), rows[i].Set);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6 + i * 4), rows[i].GenderRace);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4 + rows.Length * 4 + i * 2), rows[i].Entry);
        }
        return bytes;
    }

    private static EstTable Table() => EstTable.Parse(EstBytes([(801, 1, 2), (801, 2, 3), (1401, 1, 160)]));

    private static void IdsAvoidVanillaAndLibrary()
    {
        var table = Table();
        Require(HairSkeletonIds.Vanilla(table).SetEquals([2, 3, 160]), "Hair IDs: vanilla IDs are every entry");
        var taken = new HashSet<int>(Enumerable.Range(1000, 9000).Where(id => id != 4321));
        Require(HairSkeletonIds.Pick(taken, new Random(1)) == 4321, "Hair IDs: the only free ID is picked");
        // Reject only counts data, IO and argument errors; a full range is an InvalidOperationException.
        var threw = false;
        try { HairSkeletonIds.Pick(new HashSet<int>(Enumerable.Range(1000, 9000)), new Random(1)); }
        catch (InvalidOperationException) { threw = true; }
        Require(threw, "Hair IDs: no free ID throws");
    }

    private static void TemplateIsLowestExisting()
    {
        var table = Table();
        Require(HairSkeletonIds.TemplateId(table, 801, path => path.Contains("h0003")) == 3,
            "Hair templates: missing h0002 falls through to h0003");
        Require(HairSkeletonIds.TemplateId(table, 1801, _ => true) is null,
            "Hair templates: a race without entries has no template");
    }

    private static JsonObject Est(string slot, int entry) => new()
    {
        ["Type"] = "Est",
        ["Manipulation"] = new JsonObject
        {
            ["Entry"] = entry, ["Gender"] = "Female", ["Race"] = "Miqote", ["SetId"] = 108, ["Slot"] = slot,
        },
    };

    private static void WriteJson(string folder, string name, string text)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, name), text);
    }

    private static void LibraryScan(string root)
    {
        WriteJson(Path.Combine(root, "A broken mod"), "meta.json", "{");
        WriteJson(Path.Combine(root, "Skeleton file mod"), "meta.json", new JsonObject
        {
            ["Name"] = "Skeleton file mod",
            ["DefaultData"] = new JsonObject
            {
                ["Files"] = new JsonObject
                {
                    ["chara/human/c0801/skeleton/hair/h4821/skl_c0801h4821.sklb"] = "Files\\skl_c0801h4821.sklb",
                    ["chara/human/c0801/obj/hair/h0108/model/c0801h0108_hir.mdl"] = "Files\\c0801h0108_hir.mdl",
                },
            },
        }.ToJsonString());
        WriteJson(Path.Combine(root, "Est mod"), "group_001_x.json", new JsonObject
        {
            ["Name"] = "Style",
            ["Options"] = new JsonArray(new JsonObject
            {
                ["Name"] = "Long",
                ["Manipulations"] = new JsonArray(Est("Hair", 5000), Est("Face", 6000)),
            }),
        }.ToJsonString());
        Require(HairSkeletonIds.Library(root).SetEquals([4821, 5000]),
            "Hair IDs: library IDs come from file keys and hair Est entries");
    }

    // Two rig bones in game model space: translation, rotation x/y/z/w, scale.
    private static readonly Vector3 PositionA = new(0.04f, 1.58f, -0.06f), PositionB = new(0.05f, 1.46f, -0.09f);
    private static readonly Quaternion RotationA = Quaternion.CreateFromYawPitchRoll(0.3f, -0.4f, 0.2f);
    private static readonly Quaternion RotationB = Quaternion.CreateFromYawPitchRoll(-0.2f, 0.7f, 0.1f);

    private static float[] Model(Vector3 position, Quaternion rotation)
        => [position.X, position.Y, position.Z, rotation.X, rotation.Y, rotation.Z, rotation.W, 1, 1, 1];

    private static HairSkeletonRequest Rig() => new()
    {
        Race = "c0801",
        Bones =
        [
            new() { Name = "j_ex_hair_01_a", Parent = "j_kao", Model = Model(PositionA, RotationA) },
            new() { Name = "j_ex_hair_01_b", Parent = "j_ex_hair_01_a", Model = Model(PositionB, RotationB) },
        ],
        Chains = [new() { Kind = "back", Bones = ["j_ex_hair_01_a", "j_ex_hair_01_b"] }],
    };

    private static Vector3 RigPosition(string name)
        => name == "j_ex_hair_01_a" ? PositionA : PositionB;

    private static Quaternion RigRotation(string name)
        => name == "j_ex_hair_01_a" ? RotationA : RotationB;

    private static void Invalid(Action<HairSkeletonRequest> change, string label)
    {
        var rig = Rig();
        change(rig);
        Require(HairSkeletonRequest.Validate(rig) == ResultCode.InvalidHairSkeleton, $"Hair rig: {label} is refused");
    }

    // Names j_ex_hair_00_a onwards, all on j_kao; the 27th and 28th are the chain's bones.
    private static List<HairSkeletonBoneRequest> ManyBones(int count)
        => Enumerable.Range(0, count).Select(i => new HairSkeletonBoneRequest
        {
            Name = $"j_ex_hair_{i / 26:D2}_{(char)('a' + i % 26)}", Parent = "j_kao", Model = Model(PositionA, RotationA),
        }).ToList();

    private static void RequestValidation()
    {
        Require(HairSkeletonRequest.Validate(null) is null && HairSkeletonRequest.Validate(Rig()) is null,
            "Hair rig: absent and valid rigs pass");
        var full = Rig();
        full.Bones = ManyBones(64);
        Require(HairSkeletonRequest.Validate(full) is null, "Hair rig: 64 bones pass");

        Invalid(rig => rig.Race = "c0804", "a non-player race");
        Invalid(rig => rig.Bones = [], "zero bones");
        Invalid(rig => rig.Bones = ManyBones(65), "65 bones");
        Invalid(rig => rig.Bones![1].Name = "j_kami_a", "a non-rig bone name");
        Invalid(rig => rig.Bones![1].Name = "j_ex_hair_1_a", "a one-digit bone number");
        Invalid(rig => rig.Bones![1].Name = "j_ex_hair_01_a", "a duplicate name");
        Invalid(rig => rig.Bones!.Reverse(), "a parent declared after its child");
        Invalid(rig => rig.Bones![0].Parent = "j_kosi", "a parent outside the rig and j_kao");
        Invalid(rig => rig.Bones![0].Model = rig.Bones[0].Model![..9], "a model of 9 floats");
        Invalid(rig => rig.Bones![0].Model![1] = float.NaN, "a NaN in a model");
        Invalid(rig => { for (var i = 3; i < 7; i++) rig.Bones![0].Model![i] *= 0.5f; }, "a rotation of length 0.5");
        Invalid(rig => rig.Chains![0].Kind = "tail", "an unknown chain kind");
        Invalid(rig => rig.Chains![0].Bones!.Add("j_ex_hair_02_a"), "a chain naming an unknown bone");
        Invalid(rig => rig.Chains!.Add(new() { Kind = "side", Bones = ["j_ex_hair_01_b"] }), "one bone in two chains");
        Invalid(rig => rig.Chains![0].Bones = [], "an empty chain");
    }

    private static void LayoutKeepsModelSpace()
    {
        var kaoA = new SkeletonBone("j_kao", -1, 0,
            new BoneTransform(new(0, 1.50f, 0.02f), Quaternion.CreateFromYawPitchRoll(0, 0.1f, 0), Vector3.One));
        var kaoB = kaoA with { Reference = kaoA.Reference with { Position = new(0, 1.62f, 0.03f) } };
        foreach (var (kao, race) in new[] { (kaoA, "A"), (kaoB, "B") })
        {
            var bones = HairSkeletonLayout.Bones(Rig(), kao);
            var model = HairSkeletonLayout.ModelSpace(bones);
            Require(bones.Length == 3 && bones[0] == kao && bones[1].Parent == 0 && bones[2].Parent == 1 &&
                    bones[1].Name == "j_ex_hair_01_a" && bones[2].Reference.Scale == Vector3.One &&
                    bones[2].LockTranslation == 0,
                $"Hair layout: j_kao first, parents by index (race {race})");
            foreach (var index in new[] { 1, 2 })
            {
                var name = bones[index].Name;
                Require(Vector3.Distance(model[index].Position, RigPosition(name)) < 1e-5f &&
                        Quaternion.Dot(model[index].Rotation, RigRotation(name)) is > 0.99999f or < -0.99999f,
                    $"Hair layout: {name} keeps its model-space rest on race {race}'s j_kao");
            }
        }
    }

    private const string MiqoteHair = "chara/human/c0801/obj/hair/h0108/model/c0801h0108_hir.mdl";
    private const string AuRaHair = "chara/human/c1401/obj/hair/h0108/model/c1401h0108_hir.mdl";
    private const string HairModel = "Files/hair/c0801h0108_hir.mdl";
    private const string ModDirectory = "Hair mod";
    private static readonly Guid GroupId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OptionA = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OptionB = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static readonly SkeletonBone Kao = new("j_kao", -1, 0,
        new BoneTransform(new(0, 1.50f, 0.02f), Quaternion.Identity, Vector3.One));

    /// <summary>A mod, its stores, and the model whose hair skeleton the scenarios write.</summary>
    private sealed record HairModFixture(string ModFolder, HairSkeletonStore Store, EstEntryStore EstStore)
    {
        public JsonObject Meta() => JsonNode.Parse(File.ReadAllText(Path.Combine(ModFolder, "meta.json")))!.AsObject();

        public void Save(JsonObject meta) => File.WriteAllText(Path.Combine(ModFolder, "meta.json"), meta.ToJsonString());

        public string FilePath(string gamePath) => Path.Combine(ModFolder, gamePath.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>Default data maps nothing; option A and option B map the hair model at the game paths given.</summary>
    private static HairModFixture HairMod(string root, string[] optionA, string[] optionB, string? modelFile = null)
    {
        static JsonObject Files(string[] gamePaths, string file)
        {
            var files = new JsonObject();
            foreach (var gamePath in gamePaths)
                files[gamePath] = file;
            return files;
        }
        var file = (modelFile ?? HairModel).Replace('/', '\\');
        var meta = new JsonObject
        {
            ["FileVersion"] = 4,
            ["Identifier"] = Guid.NewGuid().ToString("D"),
            ["Name"] = ModDirectory,
            ["DefaultData"] = new JsonObject
            {
                ["Files"] = new JsonObject(), ["FileSwaps"] = new JsonObject(), ["Manipulations"] = new JsonArray(),
            },
            ["Groups"] = new JsonArray(new JsonObject
            {
                ["Id"] = GroupId.ToString("D"),
                ["Name"] = "Style",
                ["Type"] = "Single",
                ["Options"] = new JsonArray(
                    new JsonObject
                    {
                        ["Id"] = OptionA.ToString("D"), ["Name"] = "A", ["Files"] = Files(optionA, file),
                        ["Manipulations"] = new JsonArray(),
                    },
                    new JsonObject
                    {
                        ["Id"] = OptionB.ToString("D"), ["Name"] = "B", ["Files"] = Files(optionB, file),
                        ["Manipulations"] = new JsonArray(),
                    }),
            }),
        };
        var modFolder = Path.Combine(root, ModDirectory);
        Directory.CreateDirectory(modFolder);
        var fixture = new HairModFixture(modFolder, new HairSkeletonStore(Path.Combine(root, "config")),
            new EstEntryStore(Path.Combine(root, "config")));
        fixture.Save(meta);
        return fixture;
    }

    /// <summary>Fake game data: <see cref="Table"/>'s hair EST table, any skeleton file, and a writer that names its bone count.</summary>
    private static HairSkeletonSources Sources(int seed, Func<string, bool>? exists = null)
        => new(
            path => path == EstTable.GamePath(EstSlot.Hair)
                ? EstBytes([(801, 1, 2), (801, 2, 3), (1401, 1, 160)])
                : path.EndsWith(".sklb", StringComparison.Ordinal) ? Encoding.UTF8.GetBytes(path) : null,
            exists ?? (_ => true),
            (_, bones) => Encoding.UTF8.GetBytes($"sklb {bones.Length}"),
            _ => Kao,
            () => new HashSet<int>(),
            new Random(seed));

    private static string Sklb(int genderRace, int id)
        => ModelSkeletonPaths.PartialPath($"c{genderRace:D4}", EstSlot.Hair, id);

    private static JsonObject Option(JsonObject meta, Guid option)
        => meta["Groups"]![0]!["Options"]!.AsArray()
            .Single(item => item!["Id"]!.GetValue<string>() == option.ToString("D"))!.AsObject();

    private static HashSet<string> SklbKeys(JsonObject container)
        => container["Files"]!.AsObject().Select(pair => pair.Key)
            .Where(key => key.EndsWith(".sklb", StringComparison.Ordinal)).ToHashSet();

    /// <summary>The entry of a container's hair EST manipulation for set 108 and a model race name, or null.</summary>
    private static int? EstEntry(JsonObject container, string race)
        => (container["Manipulations"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(item => item["Type"]?.GetValue<string>() == "Est")
            .Select(item => item["Manipulation"]!.AsObject())
            .Where(item => item["Race"]?.GetValue<string>() == race && item["SetId"]?.GetValue<int>() == 108)
            .Select(item => (int?)item["Entry"]!.GetValue<int>())
            .FirstOrDefault();

    private static bool HasBytes(HairModFixture mod, string gamePath, string text)
        => File.Exists(mod.FilePath(gamePath)) && File.ReadAllText(mod.FilePath(gamePath)) == text;

    private static HairSkeletonPlan PrepareAndApply(HairModFixture mod, HairSkeletonSources sources)
    {
        var plan = HairSkeletonFiles.Prepare(Rig(), mod.ModFolder, ModDirectory, HairModel, sources, mod.Store);
        HairSkeletonFiles.Apply(plan, mod.ModFolder, ModDirectory, HairModel, mod.Store, mod.EstStore);
        return plan;
    }

    private static void PrepareAndApplyMapsEveryRace(string root)
    {
        var mod = HairMod(root, [MiqoteHair, AuRaHair], []);
        var plan = PrepareAndApply(mod, Sources(7));
        var id = HairSkeletonIds.Pick(new HashSet<int> { 2, 3, 160 }, new Random(7));
        Require(plan.SkeletonId == id && plan.Files.Count == 2 &&
                plan.Files.Select(file => file.GamePath).ToHashSet().SetEquals([Sklb(801, id), Sklb(1401, id)]),
            "Hair skeleton files: one file per race, with a free ID");
        Require(HasBytes(mod, Sklb(801, id), "sklb 3") && HasBytes(mod, Sklb(1401, id), "sklb 3"),
            "Hair skeleton files: both files are written");
        var optionA = Option(mod.Meta(), OptionA);
        Require(optionA["Files"]![Sklb(801, id)]?.GetValue<string>() == Sklb(801, id).Replace('/', '\\') &&
                optionA["Files"]![Sklb(1401, id)]?.GetValue<string>() == Sklb(1401, id).Replace('/', '\\'),
            "Hair skeleton files: the option maps both game paths");
        Require(EstEntry(optionA, "Miqote") == id && EstEntry(optionA, "AuRa") == id,
            "Hair skeleton files: both races' EST entries name the ID");
        Require(mod.Store.Load() is [{ SkeletonId: var stored, Model: HairModel }] && stored == id,
            "Hair skeleton files: the store keeps one record");
        Require(HairSkeletonFiles.Prepare(Rig(), mod.ModFolder, ModDirectory, HairModel, Sources(99), mod.Store).SkeletonId == id,
            "Hair skeleton files: a later export reuses the ID");
    }

    private static void MappingsFollowEachOption(string root)
    {
        var mod = HairMod(root, [MiqoteHair, AuRaHair], [MiqoteHair]);
        var id = PrepareAndApply(mod, Sources(7)).SkeletonId;
        var meta = mod.Meta();
        Require(SklbKeys(Option(meta, OptionA)).SetEquals([Sklb(801, id), Sklb(1401, id)]) &&
                SklbKeys(Option(meta, OptionB)).SetEquals([Sklb(801, id)]) &&
                SklbKeys(meta["DefaultData"]!.AsObject()).Count == 0,
            "Hair skeleton files: each option maps exactly its races' skeletons");
    }

    private static void RaceWithoutTemplateIsSkipped(string root)
    {
        var mod = HairMod(root, [MiqoteHair, AuRaHair], []);
        var sources = Sources(7, path => !path.Contains("c1401", StringComparison.Ordinal));
        var plan = HairSkeletonFiles.Prepare(Rig(), mod.ModFolder, ModDirectory, HairModel, sources, mod.Store);
        Require(plan.Files is [{ GenderRace: 801 }] && plan.Notes.Any(note => note.Contains("c1401", StringComparison.Ordinal)),
            "Hair skeleton files: a race without a vanilla template is skipped with a note");
        HairSkeletonFiles.Apply(plan, mod.ModFolder, ModDirectory, HairModel, mod.Store, mod.EstStore);
        var optionA = Option(mod.Meta(), OptionA);
        Require(EstEntry(optionA, "Miqote") == plan.SkeletonId && EstEntry(optionA, "AuRa") is null &&
                SklbKeys(optionA).SetEquals([Sklb(801, plan.SkeletonId)]),
            "Hair skeleton files: the skipped race gets no key and no EST entry");
    }

    private static void DroppedRaceIsUnmapped(string root)
    {
        var mod = HairMod(root, [MiqoteHair, AuRaHair], []);
        var id = PrepareAndApply(mod, Sources(7)).SkeletonId;
        var meta = mod.Meta();
        Option(meta, OptionA)["Files"]!.AsObject().Remove(AuRaHair);
        mod.Save(meta);
        Require(PrepareAndApply(mod, Sources(7)).SkeletonId == id, "Hair skeleton files: the dropped race's export keeps the ID");
        var optionA = Option(mod.Meta(), OptionA);
        Require(SklbKeys(optionA).SetEquals([Sklb(801, id)]) && !File.Exists(mod.FilePath(Sklb(1401, id))) &&
                EstEntry(optionA, "AuRa") is null,
            "Hair skeleton files: the dropped race's key, file and EST entry are gone");
        Require(HasBytes(mod, Sklb(801, id), "sklb 3") && EstEntry(optionA, "Miqote") == id,
            "Hair skeleton files: the kept race's key, file and EST entry stay");
    }

    private static void ExportWithoutRigRemovesOnlyOurs(string root)
    {
        const string ModderKey = "chara/human/c0801/skeleton/hair/h0160/skl_c0801h0160.sklb";
        var mod = HairMod(root, [MiqoteHair, AuRaHair], []);
        var id = PrepareAndApply(mod, Sources(7)).SkeletonId;
        var meta = mod.Meta();
        Option(meta, OptionA)["Files"]![ModderKey] = "Files\\skl_c0801h0160.sklb";
        mod.Save(meta);
        HairSkeletonFiles.Remove(mod.ModFolder, ModDirectory, HairModel, mod.Store, mod.EstStore);
        var optionA = Option(mod.Meta(), OptionA);
        Require(SklbKeys(optionA).SetEquals([ModderKey]) &&
                !File.Exists(mod.FilePath(Sklb(801, id))) && !File.Exists(mod.FilePath(Sklb(1401, id))),
            "Hair skeleton removal: our keys and files are gone, the modder's key stays");
        Require(EstEntry(optionA, "Miqote") is null && EstEntry(optionA, "AuRa") is null &&
                mod.Store.Load().Count == 0 && mod.EstStore.Load().Count == 0,
            "Hair skeleton removal: our EST entries and records are gone");
        var unchanged = File.ReadAllText(Path.Combine(mod.ModFolder, "meta.json"));
        HairSkeletonFiles.Remove(mod.ModFolder, ModDirectory, HairModel, mod.Store, mod.EstStore);
        Require(File.ReadAllText(Path.Combine(mod.ModFolder, "meta.json")) == unchanged,
            "Hair skeleton removal: without a record nothing changes");
    }

    private static void RefusesHairNotUsedAsHair(string root)
    {
        var mod = HairMod(root, ["chara/equipment/e6001/model/c0801e6001_top.mdl"], []);
        Reject(() => HairSkeletonFiles.Prepare(Rig(), mod.ModFolder, ModDirectory, HairModel, Sources(7), mod.Store),
            "Hair skeleton files: a model not used as hair is refused");
        var other = HairMod(Path.Combine(root, "OtherRace"), [AuRaHair], []);
        Reject(() => HairSkeletonFiles.Prepare(Rig(), other.ModFolder, ModDirectory, HairModel, Sources(7), other.Store),
            "Hair skeleton files: a rig for a race the model isn't used for is refused");
    }
}
