using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using InstantEdit.Models;
using InstantEdit.Services;
using InstantEdit.Services.PenumbraMods;
using InstantEdit.Services.Physics;
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
        UsersNamesFolders(Path.Combine(testRoot, "HairIdUsers"));
        RequestValidation();
        EnvelopeChecksHairSkeleton();
        FingerprintIncludesHairSkeleton();
        LayoutKeepsModelSpace();
        PrepareAndApplyMapsEveryRace(Path.Combine(testRoot, "HairApply"));
        MappingsFollowEachOption(Path.Combine(testRoot, "HairOptions"));
        RaceWithoutTemplateIsSkipped(Path.Combine(testRoot, "HairNoTemplate"));
        DroppedRaceIsUnmapped(Path.Combine(testRoot, "HairDroppedRace"));
        ExportWithoutRigRemovesOnlyOurs(Path.Combine(testRoot, "HairRemove"));
        RefusesHairNotUsedAsHair(Path.Combine(testRoot, "HairNotHair"));
        UnmappedVariantIsSkipped(Path.Combine(testRoot, "HairUnmappedVariant"));
        RefusesWithoutAnyTemplate(Path.Combine(testRoot, "HairNoTemplates"));
        FailedApplyKeepsTheRecord(Path.Combine(testRoot, "HairFailedApply"));
        CaptureRestoreRoundTrip(Path.Combine(testRoot, "HairBackupRoundTrip"));
        RestoreBeforeFirstRig(Path.Combine(testRoot, "HairBackupFirstRig"));
        FailedDeleteKeepsTheRecord(Path.Combine(testRoot, "HairBackupLockedFile"));
        OtherKeysAreLeftAlone(Path.Combine(testRoot, "HairBackupOtherKeys"));
        OrphanSidecarIsCleaned(Path.Combine(testRoot, "HairBackupSidecar"));
        NoChainsWritesPhysicsOff(Path.Combine(testRoot, "HairPhysicsOff"));
        OldBackupDropsPhysics(Path.Combine(testRoot, "HairPhysicsOldBackup"));
        UnreadablePreviousPhysics(Path.Combine(testRoot, "HairPhysicsUnreadable"));
        LockedPreviousPhysics(Path.Combine(testRoot, "HairPhysicsLocked"));
        TuningSurvivesReexport(Path.Combine(testRoot, "HairPhysicsTuning"));
        ClashMovesTheId(Path.Combine(testRoot, "HairClash"));
        OwnFolderIsNotAClash(Path.Combine(testRoot, "HairOwnFolder"));
        FailedMoveFinishesNextTime(Path.Combine(testRoot, "HairFailedMove"));
        KindChangeTakesThePreset(Path.Combine(testRoot, "HairKindChange"));
        OldRecordsLoad(Path.Combine(testRoot, "HairOldRecords"));
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
        // Duplicate property names parse but throw when read; Arabic-Indic digits are not an ID.
        WriteJson(Path.Combine(root, "Duplicate keys mod"), "meta.json",
            """{"Name": "a", "Name": "b", "DefaultData": {"Files": {"chara/human/c0801/skeleton/hair/h7001/skl_c0801h7001.sklb": "x"}}}""");
        WriteJson(Path.Combine(root, "Odd digits mod"), "meta.json",
            """{"DefaultData": {"Files": {"chara/human/c0801/skeleton/hair/h١٢٣٤/skl_c0801h1234.sklb": "x"}}}""");
        WriteJson(Path.Combine(root, "Odd digits mod"), "default_mod.json", new JsonObject
        {
            ["Files"] = new JsonObject { ["chara/human/c0801/skeleton/hair/h4822/skl_c0801h4822.sklb"] = "x" },
        }.ToJsonString());
        // Invalid JSON that names neither marker is skipped unread; one that names a marker is still parsed.
        WriteJson(Path.Combine(root, "Plain broken mod"), "meta.json", """{"Name": "Hair pack", "Files": {""");
        WriteJson(Path.Combine(root, "Lower case mod"), "meta.json",
            """{"DefaultData": {"Manipulations": [{"Type": "est", "Manipulation": {"Slot": "hair", "Entry": 5001}}]}}""");
        // The scan reads the raw text: an Est entry whose type is escaped names no marker, so the file isn't parsed.
        WriteJson(Path.Combine(root, "Escaped mod"), "meta.json",
            """{"DefaultData": {"Manipulations": [{"Type": "TYPE", "Manipulation": {"Slot": "Hair", "Entry": 5002}}]}}"""
                .Replace("TYPE", @"\" + "u0045st"));
        Require(HairSkeletonIds.Library(root).SetEquals([4821, 4822, 5000, 5001]),
            "Hair IDs: library IDs come from file keys and hair Est entries; odd files and files without a marker are skipped");
    }

    private static void UsersNamesFolders(string root)
    {
        const string Key = "chara/human/c0801/skeleton/hair/h1500/skl_c0801h1500.sklb";
        WriteJson(Path.Combine(root, "Mod A"), "meta.json",
            new JsonObject { ["DefaultData"] = new JsonObject { ["Files"] = new JsonObject { [Key] = "x" } } }.ToJsonString());
        WriteJson(Path.Combine(root, "Mod A"), "default_mod.json",
            new JsonObject { ["Files"] = new JsonObject { [Key] = "x" } }.ToJsonString());
        WriteJson(Path.Combine(root, "Mod B"), "group_001_x.json", new JsonObject
        {
            ["Options"] = new JsonArray(new JsonObject { ["Manipulations"] = new JsonArray(Est("Hair", 1500)) }),
        }.ToJsonString());
        WriteJson(Path.Combine(root, "Mod B"), "meta.json", "{");
        Require(HairSkeletonIds.Users(root, null) is { Count: 1 } all && all[1500].Order().SequenceEqual(["Mod A", "Mod B"]),
            "Hair ID users: each ID names its folders once, past a broken file");
        Require(HairSkeletonIds.Users(root, "mod a") is { Count: 1 } others && others[1500].SequenceEqual(["Mod B"]),
            "Hair ID users: the excepted folder is left out, whatever its case");
        // Reject also takes an IOException, which a missing folder would throw unasked.
        var threw = false;
        try { HairSkeletonIds.Users(Path.Combine(root, "missing"), null); }
        catch (InvalidDataException e) { threw = e.Message == "The Penumbra mod folder can't be read."; }
        Require(threw, "Hair ID users: a missing mod folder throws");

        // The mod's folder name and its directory name may differ; neither counts as another mod.
        WriteJson(Path.Combine(root, "Mod C"), "default_mod.json", new JsonObject
        {
            ["Files"] = new JsonObject { ["chara/human/c0801/skeleton/hair/h1600/skl_c0801h1600.sklb"] = "x" },
        }.ToJsonString());
        Require(HairSkeletonIds.TakenByOthers(root, Path.Combine(root, "Mod C") + Path.DirectorySeparatorChar, "Mod D")
                    .SetEquals([1500]),
            "Hair ID users: the mod's folder name is left out when its directory name differs");
        Require(HairSkeletonIds.TakenByOthers(root, Path.Combine(root, "Mod A"), "Mod C").SetEquals([1500]),
            "Hair ID users: both names are left out, and an ID another mod shares stays taken");
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
        Chains =
        [
            new()
            {
                Kind = "back", Bones = ["j_ex_hair_01_a", "j_ex_hair_01_b"], Tip = [PositionB.X, PositionB.Y - 0.06f, PositionB.Z],
            },
        ],
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
        full.Bones[27].Parent = "j_ex_hair_01_a";
        full.Bones[27].Model = Model(PositionB, RotationB);
        Require(HairSkeletonRequest.Validate(full) is null, "Hair rig: 64 bones pass");
        var partial = Rig();
        partial.Chains![0].Bones = ["j_ex_hair_01_a"];
        partial.Chains[0].Tip = [PositionA.X, PositionA.Y - 0.06f, PositionA.Z];
        Require(HairSkeletonRequest.Validate(partial) is null, "Hair rig: a bone in no chain passes");
        partial.Bones![1].Model = Model(PositionA, RotationB);
        Require(HairSkeletonRequest.Validate(partial) is null, "Hair rig: a bone in no chain may sit on its parent");

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
        Invalid(rig => rig.Chains!.Add(new() { Kind = "side", Bones = ["j_ex_hair_01_b"], Tip = rig.Chains[0].Tip }),
            "one bone in two chains");
        Invalid(rig => rig.Chains![0].Bones = [], "an empty chain");
        Invalid(rig => rig.Chains![0].Tip = null, "a chain without a tip");
        Invalid(rig => rig.Chains![0].Tip = rig.Chains[0].Tip![..2], "a tip of 2 floats");
        Invalid(rig => rig.Chains![0].Tip![1] = float.NaN, "a NaN in a tip");
        Invalid(rig => rig.Chains![0].Tip = [PositionB.X, PositionB.Y, PositionB.Z], "a tip on the last bone");
        Invalid(rig => rig.Chains![0].Bones = ["j_ex_hair_01_b", "j_ex_hair_01_a"], "a chain listed tip to root");
        Invalid(rig => rig.Bones![1].Model = Model(PositionA + new Vector3(0.0005f, 0, 0), RotationB),
            "a chain bone under 1 mm from the one before");
        Invalid(rig =>
        {
            rig.Bones!.Add(new() { Name = "j_ex_hair_02_a", Parent = "j_kao", Model = Model(PositionB, RotationB) });
            rig.Chains![0].Bones = ["j_ex_hair_01_a", "j_ex_hair_02_a"];
        }, "a chain bone whose parent is not the previous bone");
    }

    private static ExportServer.ExportRequest Export(HairSkeletonRequest? rig) => new()
    {
        Schema = "instant-edit.export", Version = 3, PluginInstanceId = "p", ContextId = "c", ExportId = "e",
        Capability = "k", FilePath = "C:/Temp/export.mdl", Size = 1, Sha256 = new string('a', 64),
        HairSkeleton = rig,
    };

    private static void EnvelopeChecksHairSkeleton()
    {
        Require(ExportServer.ValidateEnvelope(Export(Rig())) is null, "Hair envelope: a valid rig passes");
        var invalid = Rig();
        invalid.Bones![0].Parent = "j_kosi";
        Require(ExportServer.ValidateEnvelope(Export(invalid)) == ResultCode.InvalidHairSkeleton,
            "Hair envelope: an invalid rig is refused");
        var withEst = Export(Rig());
        withEst.EstEntries = [];
        Require(ExportServer.ValidateEnvelope(withEst) == ResultCode.InvalidHairSkeleton,
            "Hair envelope: a rig with EST entries is refused");
    }

    private static void FingerprintIncludesHairSkeleton()
    {
        var moved = Rig();
        moved.Bones![1].Model![0] += 0.01f;
        Require(ExportServer.ExportRequestFingerprint(Export(Rig())) == ExportServer.ExportRequestFingerprint(Export(Rig())),
            "Hair fingerprint: equal rigs give equal fingerprints");
        Require(ExportServer.ExportRequestFingerprint(Export(Rig())) != ExportServer.ExportRequestFingerprint(Export(moved)),
            "Hair fingerprint: a moved rig bone changes the fingerprint");
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

    /// <summary>The eight shapes vanilla hair collides with, as the race's h0182 physics holds them.</summary>
    private static byte[] DonorBytes()
    {
        var donor = new PhybFile();
        foreach (var name in new[]
                 {
                     "backhair_nplane", "hair_capsule_sako_l", "hair_capsule_sako_r", "sidehair_nplane",
                     "hair_sphere_kao_l", "sidehair_capsule_sako_l", "hair_sphere_kao_r", "sidehair_capsule_sako_r",
                 })
        {
            if (name.Contains("nplane"))
                donor.Collision.NormalPlanes.Add(new() { Name = PhybName.From(name), Bone = PhybName.From("j_kao"), Normal = Vector3.UnitZ, Thickness = 0.02f });
            else if (name.Contains("capsule"))
                donor.Collision.Capsules.Add(new()
                {
                    Name = PhybName.From(name), StartBone = PhybName.From("j_sebo_c"), EndBone = PhybName.From("j_kubi"),
                    EndOffset = Vector3.UnitY, Radius = 0.03f,
                });
            else
                donor.Collision.Spheres.Add(new() { Name = PhybName.From(name), Bone = PhybName.From("j_kao"), Thickness = 0.04f });
        }
        return donor.ToBytes();
    }

    /// <summary>
    /// Fake game data: <see cref="Table"/>'s hair EST table, any skeleton file, c0801's h0182 physics (c1401
    /// has none), a writer that names its bone count, and no other mod using any ID unless <paramref name="taken"/> says so.
    /// </summary>
    private static HairSkeletonSources Sources(int seed, Func<string, bool>? exists = null, Func<IReadOnlySet<int>>? taken = null)
        => new(
            path => path == EstTable.GamePath(EstSlot.Hair)
                ? EstBytes([(801, 1, 2), (801, 2, 3), (1401, 1, 160)])
                : path == HairPhysicsPresets.DonorPath(801) ? DonorBytes()
                : path.EndsWith(".sklb", StringComparison.Ordinal) ? Encoding.UTF8.GetBytes(path) : null,
            exists ?? (_ => true),
            (_, bones) => Encoding.UTF8.GetBytes($"sklb {bones.Length}"),
            _ => Kao,
            taken ?? (() => new HashSet<int>()),
            new Random(seed));

    private static string Sklb(int genderRace, int id)
        => ModelSkeletonPaths.PartialPath($"c{genderRace:D4}", EstSlot.Hair, id);

    private static string Phyb(int genderRace, int id) => HairPhysicsBuilder.PhysicsPath(genderRace, id);

    /// <summary>The mod's physics file at a game path, parsed.</summary>
    private static PhybFile ReadPhyb(HairModFixture mod, string gamePath) => PhybFile.Parse(File.ReadAllBytes(mod.FilePath(gamePath)));

    private static JsonObject Option(JsonObject meta, Guid option)
        => meta["Groups"]![0]!["Options"]!.AsArray()
            .Single(item => item!["Id"]!.GetValue<string>() == option.ToString("D"))!.AsObject();

    private static HashSet<string> SklbKeys(JsonObject container) => Keys(container, ".sklb");

    private static HashSet<string> PhybKeys(JsonObject container) => Keys(container, ".phyb");

    private static HashSet<string> Keys(JsonObject container, string extension)
        => container["Files"]!.AsObject().Select(pair => pair.Key)
            .Where(key => key.EndsWith(extension, StringComparison.Ordinal)).ToHashSet();

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

    /// <summary>A variant saved without Penumbra setup is mapped by no option yet: the export skips its skeleton with a note.</summary>
    private static void UnmappedVariantIsSkipped(string root)
    {
        var mod = HairMod(Path.Combine(root, "hair"), [MiqoteHair], []);
        Require(HairSkeletonFiles.UnmappedNote(mod.Meta(), HairModel) is null,
            "Hair skip: a model an option maps as hair is prepared");
        Require(HairSkeletonFiles.UnmappedNote(mod.Meta(), "Files/hair/Long.mdl") ==
                "No Penumbra option uses Long.mdl yet, so no hair skeleton was built.",
            "Hair skip: a variant file no option maps is skipped with a note");
        var gear = HairMod(Path.Combine(root, "gear"), ["chara/equipment/e0001/model/c0101e0001_top.mdl"], []);
        Require(HairSkeletonFiles.UnmappedNote(gear.Meta(), HairModel) is null,
            "Hair skip: a model mapped as anything else still goes to Prepare, which refuses it");
    }

    private static HairSkeletonPlan PrepareAndApply(HairModFixture mod, HairSkeletonSources sources, HairSkeletonRequest? rig = null)
    {
        var plan = HairSkeletonFiles.Prepare(rig ?? Rig(), mod.ModFolder, ModDirectory, HairModel, sources, mod.Store);
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

        // Physics: c0801 has the h0182 donor, c1401 doesn't.
        Require(plan.Files.All(file => file.PhysicsPath == Phyb(file.GenderRace, id)) &&
                PhybKeys(optionA).SetEquals([Phyb(801, id), Phyb(1401, id)]) &&
                optionA["Files"]![Phyb(801, id)]?.GetValue<string>() == Phyb(801, id).Replace('/', '\\'),
            "Hair physics files: the option maps both races' physics");
        var miqote = ReadPhyb(mod, Phyb(801, id));
        var auRa = ReadPhyb(mod, Phyb(1401, id));
        Require(miqote is { DataType: 3, Simulators.Count: 1 } && !miqote.Collision.IsEmpty &&
                auRa is { DataType: 2, Simulators.Count: 1 } && auRa.Collision.IsEmpty,
            "Hair physics files: c0801's physics has shapes, c1401's (no donor) has none");
        Require(plan.Notes.Contains("The game's hair physics h0182 for c1401 could not be read, so that race's hair collides with nothing.") &&
                !plan.Notes.Any(note => note.Contains("h0182 for c0801", StringComparison.Ordinal)),
            "Hair physics files: the missing donor is noted for c1401 only");

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
                PhybKeys(optionA).SetEquals([Phyb(801, id)]) && !File.Exists(mod.FilePath(Phyb(1401, id))) &&
                EstEntry(optionA, "AuRa") is null,
            "Hair skeleton files: the dropped race's keys, files and EST entry are gone");
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
        Require(PhybKeys(optionA).Count == 0 &&
                !File.Exists(mod.FilePath(Phyb(801, id))) && !File.Exists(mod.FilePath(Phyb(1401, id))),
            "Hair skeleton removal: our physics keys and files are gone too");
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

    private static void RefusesWithoutAnyTemplate(string root)
    {
        var mod = HairMod(root, [MiqoteHair, AuRaHair], []);
        var threw = false;
        try { HairSkeletonFiles.Prepare(Rig(), mod.ModFolder, ModDirectory, HairModel, Sources(7, _ => false), mod.Store); }
        catch (InvalidDataException) { threw = true; }
        Require(threw, "Hair skeleton files: no race with a vanilla template is refused");
    }

    private static void FailedApplyKeepsTheRecord(string root)
    {
        var mod = HairMod(root, [MiqoteHair, AuRaHair], []);
        // A folder where the EST records file goes makes the EST step fail.
        var brokenConfig = Path.Combine(root, "broken config");
        Directory.CreateDirectory(Path.Combine(brokenConfig, "est-entries.json"));
        var plan = HairSkeletonFiles.Prepare(Rig(), mod.ModFolder, ModDirectory, HairModel, Sources(7), mod.Store);
        var threw = false;
        try { HairSkeletonFiles.Apply(plan, mod.ModFolder, ModDirectory, HairModel, mod.Store, new EstEntryStore(brokenConfig)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { threw = true; }
        Require(threw && mod.Store.Load() is [{ SkeletonId: var stored }] && stored == plan.SkeletonId,
            "Hair skeleton files: a failed EST step leaves the ID recorded");
        Require(HairSkeletonFiles.Prepare(Rig(), mod.ModFolder, ModDirectory, HairModel, Sources(99), mod.Store).SkeletonId == plan.SkeletonId,
            "Hair skeleton files: the export after a failed one reuses the ID");
    }

    /// <summary>A container's <c>.sklb</c> and <c>.phyb</c> mappings as <c>key=value</c>, sorted.</summary>
    private static string[] SkeletonPairs(JsonObject container)
        => container["Files"]!.AsObject()
            .Where(pair => pair.Key.EndsWith(".sklb", StringComparison.Ordinal) || pair.Key.EndsWith(".phyb", StringComparison.Ordinal))
            .Select(pair => $"{pair.Key}={pair.Value!.GetValue<string>()}").Order(StringComparer.Ordinal).ToArray();

    /// <summary>A mod with the hair skeleton applied under one ID, its state captured, then exported again under another ID.</summary>
    private static (HairModFixture Mod, JsonObject State, int Id, int Other) ExportedTwice(string root)
    {
        var mod = HairMod(root, [MiqoteHair, AuRaHair], [MiqoteHair]);
        var id = PrepareAndApply(mod, Sources(7)).SkeletonId;
        var state = HairSkeletonFiles.CaptureState(mod.ModFolder, ModDirectory, HairModel, mod.Store)!;
        var other = id == 4321 ? 4322 : 4321;
        mod.Store.Save([mod.Store.Load().Single() with { SkeletonId = other }]);
        Require(PrepareAndApply(mod, Sources(7)).SkeletonId == other, "Hair skeleton backups: the second export uses the other ID");
        return (mod, state, id, other);
    }

    private static void CaptureRestoreRoundTrip(string root)
    {
        var (mod, state, id, other) = ExportedTwice(root);
        static string Pair(string gamePath) => $"{gamePath}={gamePath.Replace('/', '\\')}";
        var pairsA = new[] { Pair(Sklb(801, id)), Pair(Sklb(1401, id)), Pair(Phyb(801, id)), Pair(Phyb(1401, id)) }
            .Order(StringComparer.Ordinal).ToArray();
        var pairsB = new[] { Pair(Sklb(801, id)), Pair(Phyb(801, id)) }.Order(StringComparer.Ordinal).ToArray();
        Require(HairSkeletonFiles.CaptureState(mod.ModFolder, ModDirectory, "Files/unused.mdl", mod.Store) is null,
            "Hair skeleton backups: a model used as no hair keeps no state");
        var entries = state["entries"]!.AsArray();
        Require(state["schema"]?.GetValue<string>() == "instant-edit.hair-skeleton-state" &&
                state["skeletonId"]?.GetValue<int>() == id && entries.Count == 6 &&
                entries.Count(entry => entry!["container"]!.GetValue<string>() == entries[0]!["container"]!.GetValue<string>()) == 4 &&
                entries.Count(entry => entry!["gamePath"]!.GetValue<string>().EndsWith(".phyb", StringComparison.Ordinal)) == 3,
            "Hair skeleton backups: the state keeps the ID and every option's keys of it, 4 for option A's two races");
        var physics = new[] { Phyb(801, id), Phyb(1401, id) }.ToDictionary(path => path, path => File.ReadAllBytes(mod.FilePath(path)));
        // The first ID's files change since.
        File.WriteAllText(mod.FilePath(Sklb(801, id)), "changed");
        foreach (var path in physics.Keys)
            File.WriteAllText(mod.FilePath(path), "changed");

        var unchanged = File.ReadAllText(Path.Combine(mod.ModFolder, "meta.json"));
        var warnings = HairSkeletonFiles.RestoreState(mod.ModFolder, ModDirectory, HairModel,
            new JsonObject { ["schema"] = "other" }, mod.Store);
        Require(warnings.Count == 1 && File.ReadAllText(Path.Combine(mod.ModFolder, "meta.json")) == unchanged &&
                mod.Store.Load() is [{ SkeletonId: var kept }] && kept == other,
            "Hair skeleton backups: an unreadable state is one warning and changes nothing");

        warnings = HairSkeletonFiles.RestoreState(mod.ModFolder, ModDirectory, HairModel, state, mod.Store);
        var meta = mod.Meta();
        Require(warnings.Count == 0 && SkeletonPairs(Option(meta, OptionA)).SequenceEqual(pairsA) &&
                SkeletonPairs(Option(meta, OptionB)).SequenceEqual(pairsB),
            "Hair skeleton backups: restoring puts back every option's keys exactly");
        Require(HasBytes(mod, Sklb(801, id), "sklb 3") && HasBytes(mod, Sklb(1401, id), "sklb 3"),
            "Hair skeleton backups: restoring puts back the files' bytes");
        Require(physics.All(pair => File.ReadAllBytes(mod.FilePath(pair.Key)).AsSpan().SequenceEqual(pair.Value)),
            "Hair skeleton backups: restoring puts back both physics files byte for byte");
        Require(!File.Exists(mod.FilePath(Sklb(801, other))) && !File.Exists(mod.FilePath(Sklb(1401, other))) &&
                !File.Exists(mod.FilePath(Phyb(801, other))) && !File.Exists(mod.FilePath(Phyb(1401, other))),
            "Hair skeleton backups: the other ID's files are deleted");
        Require(mod.Store.Load() is [{ SkeletonId: var stored }] && stored == id,
            "Hair skeleton backups: the record names the restored ID");
    }

    private static void RestoreBeforeFirstRig(string root)
    {
        var mod = HairMod(root, [MiqoteHair, AuRaHair], []);
        var est = EstEntryService.CaptureEstStateForRegression(mod.ModFolder, ModDirectory, HairModel, mod.EstStore)!;
        var state = HairSkeletonFiles.CaptureState(mod.ModFolder, ModDirectory, HairModel, mod.Store)!;
        Require(state["skeletonId"] is null && state["entries"]!.AsArray().Count == 0,
            "Hair skeleton backups: before the first rig the state has no ID and no keys");
        var id = PrepareAndApply(mod, Sources(7)).SkeletonId;

        EstEntryService.RestoreEstStateForRegression(mod.ModFolder, ModDirectory, est, mod.EstStore);
        var warnings = HairSkeletonFiles.RestoreState(mod.ModFolder, ModDirectory, HairModel, state, mod.Store);
        var optionA = Option(mod.Meta(), OptionA);
        Require(warnings.Count == 0 && SklbKeys(optionA).Count == 0 &&
                !File.Exists(mod.FilePath(Sklb(801, id))) && !File.Exists(mod.FilePath(Sklb(1401, id))) &&
                mod.Store.Load().Count == 0,
            "Hair skeleton backups: restoring a backup from before the first rig removes our keys, files and record");
        Require(EstEntry(optionA, "Miqote") is null && EstEntry(optionA, "AuRa") is null && mod.EstStore.Load().Count == 0,
            "Hair skeleton backups: the EST state restored with it takes back the EST entries");
    }

    private static void FailedDeleteKeepsTheRecord(string root)
    {
        var (mod, state, id, other) = ExportedTwice(root);
        // An open handle without sharing makes the previous ID's file undeletable during the restore.
        using (new FileStream(mod.FilePath(Sklb(801, other)), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            try { HairSkeletonFiles.RestoreState(mod.ModFolder, ModDirectory, HairModel, state, mod.Store); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        Require(File.Exists(mod.FilePath(Sklb(801, other))) && mod.Store.Load() is [{ SkeletonId: var stored }] && stored == id &&
                SklbKeys(Option(mod.Meta(), OptionA)).SetEquals([Sklb(801, id), Sklb(1401, id)]),
            "Hair skeleton backups: a previous ID's file that can't be deleted leaves the record naming the restored ID");
    }

    private static void OtherKeysAreLeftAlone(string root)
    {
        const string HairY = "chara/human/c0801/obj/hair/h0109/model/c0801h0109_hir.mdl";
        const string ModelY = "Files/hair/c0801h0109_hir.mdl";
        const string ModderKey = "chara/human/c0801/skeleton/hair/h0160/skl_c0801h0160.sklb";
        const string ModderFile = "Files/skl_c0801h0160.sklb";
        var mod = HairMod(root, [MiqoteHair], []);
        var meta = mod.Meta();
        Option(meta, OptionA)["Files"]![HairY] = ModelY.Replace('/', '\\');
        Option(meta, OptionA)["Files"]![ModderKey] = ModderFile.Replace('/', '\\');
        mod.Save(meta);
        Directory.CreateDirectory(Path.GetDirectoryName(mod.FilePath(ModderFile))!);
        File.WriteAllText(mod.FilePath(ModderFile), "modder");
        var idX = PrepareAndApply(mod, Sources(7)).SkeletonId;
        var planY = HairSkeletonFiles.Prepare(Rig(), mod.ModFolder, ModDirectory, ModelY, Sources(8), mod.Store);
        HairSkeletonFiles.Apply(planY, mod.ModFolder, ModDirectory, ModelY, mod.Store, mod.EstStore);
        var idY = planY.SkeletonId;
        Require(idX != idY, "Hair skeleton backups: two hair models in one option get two IDs");

        var state = HairSkeletonFiles.CaptureState(mod.ModFolder, ModDirectory, HairModel, mod.Store)!;
        Require(state["entries"]!.AsArray().Select(entry => entry!["gamePath"]!.GetValue<string>()).ToHashSet()
                .SetEquals([Sklb(801, idX), Phyb(801, idX)]),
            "Hair skeleton backups: the state keeps only the model's own ID, not another model's or the modder's");
        var other = Enumerable.Range(4321, 3).First(candidate => candidate != idX && candidate != idY);
        var records = mod.Store.Load().ToList();
        var index = records.FindIndex(record => record.SkeletonId == idX);
        records[index] = records[index] with { SkeletonId = other };
        mod.Store.Save(records);
        PrepareAndApply(mod, Sources(7));

        var warnings = HairSkeletonFiles.RestoreState(mod.ModFolder, ModDirectory, HairModel, state, mod.Store);
        var optionA = Option(mod.Meta(), OptionA);
        Require(warnings.Count == 0 && SklbKeys(optionA).SetEquals([Sklb(801, idX), Sklb(801, idY), ModderKey]) &&
                optionA["Files"]![ModderKey]!.GetValue<string>() == ModderFile.Replace('/', '\\'),
            "Hair skeleton backups: restoring one model leaves another model's and the modder's keys alone");
        Require(HasBytes(mod, Sklb(801, idY), "sklb 3") && HasBytes(mod, ModderFile, "modder") &&
                !File.Exists(mod.FilePath(Sklb(801, other))),
            "Hair skeleton backups: restoring one model leaves another model's and the modder's files alone");
        Require(mod.Store.Load().Select(record => (record.Model, record.SkeletonId)).ToHashSet()
                .SetEquals([(HairModel, idX), (ModelY, idY)]),
            "Hair skeleton backups: restoring one model leaves another model's record alone");
    }

    private static void OrphanSidecarIsCleaned(string root)
    {
        var mod = HairMod(root, [MiqoteHair], []);
        PrepareAndApply(mod, Sources(7));
        var state = HairSkeletonFiles.CaptureState(mod.ModFolder, ModDirectory, HairModel, mod.Store)!;
        var model = mod.FilePath(HairModel);
        Directory.CreateDirectory(Path.GetDirectoryName(model)!);
        File.WriteAllText(model, "model");
        var exported = Path.Combine(root, "exported.mdl");
        File.WriteAllText(exported, "exported");
        var backups = new ModelBackupStore(Path.Combine(root, "cache"));
        var writer = new ModelFileWriter(backups);

        Require(writer.WriteModelToOriginalLocation(mod.ModFolder, model, exported, true, ModDirectory, HairModel,
                    hairSkeletonState: state) is null,
            "Hair skeleton backups: the export writes the model");
        var first = backups.List(ModDirectory, HairModel).Single();
        var firstPath = backups.Resolve(first.TargetId, first.Name);
        Require(JsonNode.DeepEquals(backups.ReadHairSkeletonState(firstPath), state) && backups.ReadEstState(firstPath) is null &&
                File.Exists(firstPath + ".hairskel.json"),
            "Hair skeleton backups: the export keeps the state beside its backup, apart from the EST entries");
        Require(writer.RestoreModelBackup(model, firstPath, ModDirectory, HairModel, hairSkeletonState: state) is null,
            "Hair skeleton backups: the restore writes the model");
        var second = backups.List(ModDirectory, HairModel).Single(backup => backup.Name != first.Name);
        var secondPath = backups.Resolve(second.TargetId, second.Name);
        Require(JsonNode.DeepEquals(backups.ReadHairSkeletonState(secondPath), state),
            "Hair skeleton backups: the pre-restore backup keeps today's state");

        backups.Cleanup(DateTimeOffset.UtcNow + TimeSpan.FromDays(10));
        Require(!File.Exists(firstPath) && !File.Exists(firstPath + ".hairskel.json") &&
                !File.Exists(secondPath + ".hairskel.json"),
            "Hair skeleton backups: cleanup of an expired backup removes its hair skeleton state");
    }

    private static void NoChainsWritesPhysicsOff(string root)
    {
        var mod = HairMod(root, [MiqoteHair, AuRaHair], []);
        var rig = Rig();
        rig.Chains = null;
        var plan = PrepareAndApply(mod, Sources(7), rig);
        var id = plan.SkeletonId;
        Require(PhybKeys(Option(mod.Meta(), OptionA)).SetEquals([Phyb(801, id), Phyb(1401, id)]) &&
                ReadPhyb(mod, Phyb(801, id)) is { Version: 0 } && ReadPhyb(mod, Phyb(1401, id)) is { Version: 0 },
            "Hair physics files: a rig without chains maps physics that turn it off");
        Require(!plan.Notes.Any(note => note.Contains("physics", StringComparison.Ordinal)),
            "Hair physics files: a rig without chains notes no missing donor");
    }

    private static void OldBackupDropsPhysics(string root)
    {
        var mod = HairMod(root, [MiqoteHair, AuRaHair], [MiqoteHair]);
        var id = PrepareAndApply(mod, Sources(7)).SkeletonId;
        var state = HairSkeletonFiles.CaptureState(mod.ModFolder, ModDirectory, HairModel, mod.Store)!;
        // A phase 1 state: the same keys without the physics.
        var entries = state["entries"]!.AsArray();
        foreach (var entry in entries.Where(entry => entry!["gamePath"]!.GetValue<string>().Contains("/phy_", StringComparison.Ordinal)).ToList())
            entries.Remove(entry);
        PrepareAndApply(mod, Sources(7));

        var warnings = HairSkeletonFiles.RestoreState(mod.ModFolder, ModDirectory, HairModel, state, mod.Store);
        var meta = mod.Meta();
        var containers = new[] { meta["DefaultData"]!.AsObject(), Option(meta, OptionA), Option(meta, OptionB) };
        Require(warnings.Count == 0 && containers.All(container => PhybKeys(container).Count == 0) &&
                !File.Exists(mod.FilePath(Phyb(801, id))) && !File.Exists(mod.FilePath(Phyb(1401, id))),
            "Hair physics backups: restoring a backup without physics unmaps and deletes the physics, without warnings");
        Require(SklbKeys(Option(meta, OptionA)).SetEquals([Sklb(801, id), Sklb(1401, id)]) && HasBytes(mod, Sklb(801, id), "sklb 3"),
            "Hair physics backups: the backup's skeletons stay");
    }

    private static void UnreadablePreviousPhysics(string root)
    {
        var mod = HairMod(root, [MiqoteHair, AuRaHair], []);
        var id = PrepareAndApply(mod, Sources(7)).SkeletonId;
        File.WriteAllBytes(mod.FilePath(Phyb(801, id)), [1, 2, 3]);
        HairSkeletonPlan? plan = null;
        try { plan = PrepareAndApply(mod, Sources(7)); }
        catch (InvalidDataException) { }
        Require(plan is not null && ReadPhyb(mod, Phyb(801, id)) is { DataType: 3, Simulators.Count: 1 } &&
                plan.Notes.Contains("The hair physics in the mod for c0801 could not be read, so every chain gets its preset."),
            "Hair physics files: unreadable physics in the mod is rebuilt from the presets, with a note");
    }

    private static void TuningSurvivesReexport(string root)
    {
        var mod = HairMod(root, [MiqoteHair, AuRaHair], []);
        var id = PrepareAndApply(mod, Sources(7)).SkeletonId;
        var tuned = ReadPhyb(mod, Phyb(801, id));
        tuned.Simulators[0].Chains[0].Dampening = 0.3f;
        File.WriteAllBytes(mod.FilePath(Phyb(801, id)), tuned.ToBytes());
        var plan = PrepareAndApply(mod, Sources(7));
        Require(ReadPhyb(mod, Phyb(801, id)).Simulators[0].Chains[0].Dampening == 0.3f &&
                ReadPhyb(mod, Phyb(1401, id)).Simulators[0].Chains[0].Dampening == 0.05f,
            "Hair physics files: a re-export keeps the back chain's tuned dampening");
        Require(plan.Notes.Count(note => note.Contains("Kept", StringComparison.Ordinal)) == 1 &&
                plan.Notes.Contains("c0801, c1401: Kept your physics settings for 1 of 1 chains."),
            "Hair physics files: a builder note the same for several races is one note naming them all");
    }

    private static void LockedPreviousPhysics(string root)
    {
        var mod = HairMod(root, [MiqoteHair], []);
        var id = PrepareAndApply(mod, Sources(7)).SkeletonId;
        TuneDampening(mod, Phyb(801, id), 0.3f);
        // An open handle without sharing makes the previous physics unreadable during Prepare.
        HairSkeletonPlan plan;
        using (new FileStream(mod.FilePath(Phyb(801, id)), FileMode.Open, FileAccess.Read, FileShare.None))
            plan = HairSkeletonFiles.Prepare(Rig(), mod.ModFolder, ModDirectory, HairModel, Sources(7), mod.Store);
        Require(plan.Notes.Contains("The hair physics in the mod for c0801 could not be read, so every chain gets its preset.") &&
                PhybFile.Parse(plan.Files[0].Physics).Simulators[0].Chains[0].Dampening == 0.05f,
            "Hair physics files: physics in the mod that exists but can't be opened gets the presets, with a note");
    }

    private static void ClashMovesTheId(string root)
    {
        var mod = HairMod(root, [MiqoteHair, AuRaHair], [MiqoteHair]);
        var id = PrepareAndApply(mod, Sources(7)).SkeletonId;
        var tuned = ReadPhyb(mod, Phyb(801, id));
        tuned.Simulators[0].Chains[0].Dampening = 0.3f;
        File.WriteAllBytes(mod.FilePath(Phyb(801, id)), tuned.ToBytes());

        // Another mod now uses the recorded ID.
        var plan = PrepareAndApply(mod, Sources(8, taken: () => new HashSet<int> { id }));
        var moved = plan.SkeletonId;
        Require(moved != id && plan.PreviousId == id, "Hair ID clash: the export moves to a new ID and names the old one");
        var meta = mod.Meta();
        var containers = new[] { meta["DefaultData"]!.AsObject(), Option(meta, OptionA), Option(meta, OptionB) };
        Require(containers.All(container => !container["Files"]!.AsObject()
                    .Any(pair => pair.Key.Contains($"/h{id:D4}/", StringComparison.Ordinal))) &&
                SklbKeys(Option(meta, OptionA)).SetEquals([Sklb(801, moved), Sklb(1401, moved)]) &&
                PhybKeys(Option(meta, OptionB)).SetEquals([Phyb(801, moved)]),
            "Hair ID clash: no option keeps a key of the old ID; the new ID is mapped instead");
        Require(new[] { Sklb(801, id), Sklb(1401, id), Phyb(801, id), Phyb(1401, id) }.All(path => !File.Exists(mod.FilePath(path))) &&
                HasBytes(mod, Sklb(801, moved), "sklb 3") && HasBytes(mod, Sklb(1401, moved), "sklb 3"),
            "Hair ID clash: the old ID's files are deleted and the new ones written");
        Require(EstEntry(Option(meta, OptionA), "Miqote") == moved && EstEntry(Option(meta, OptionA), "AuRa") == moved &&
                EstEntry(Option(meta, OptionB), "Miqote") == moved &&
                mod.EstStore.Load() is { Count: > 0 } estRecords && estRecords.All(record => record.Entry == moved),
            "Hair ID clash: the EST entries and their records name the new ID");
        Require(mod.Store.Load() is [{ SkeletonId: var stored }] && stored == moved,
            "Hair ID clash: the store holds one record, with the new ID");
        Require(plan.Notes.Contains($"Hair skeleton h{id:D4} is also used by another mod, so this export moved it to h{moved:D4}."),
            "Hair ID clash: the notes say the ID moved");
        Require(ReadPhyb(mod, Phyb(801, moved)).Simulators[0].Chains[0].Dampening == 0.3f &&
                ReadPhyb(mod, Phyb(1401, moved)).Simulators[0].Chains[0].Dampening == 0.05f,
            "Hair ID clash: the moved physics keeps the tuned dampening");

        var again = PrepareAndApply(mod, Sources(9, taken: () => new HashSet<int> { id }));
        Require(again.SkeletonId == moved && again.PreviousId is null && !again.Notes.Any(note => note.Contains("moved it")),
            "Hair ID clash: the next export keeps the new ID");
    }

    private static void OwnFolderIsNotAClash(string root)
    {
        const string HairY = "chara/human/c0801/obj/hair/h0109/model/c0801h0109_hir.mdl";
        const string ModelY = "Files/hair/c0801h0109_hir.mdl";
        var mod = HairMod(root, [MiqoteHair, AuRaHair], []);
        Func<IReadOnlySet<int>> library = () => HairSkeletonIds.Users(root, ModDirectory).Keys.ToHashSet();
        var id = PrepareAndApply(mod, Sources(7, taken: library)).SkeletonId;
        var plan = PrepareAndApply(mod, Sources(8, taken: library));
        Require(HairSkeletonIds.Users(root, null).ContainsKey(id) && plan.SkeletonId == id && plan.PreviousId is null &&
                !plan.Notes.Any(note => note.Contains("moved it")),
            "Hair ID clash: the mod's own folder is no clash, so the ID stays");

        // The library scan leaves this mod out, so another model's ID in it must still count as taken.
        var meta = mod.Meta();
        Option(meta, OptionA)["Files"]![HairY] = ModelY.Replace('/', '\\');
        mod.Save(meta);
        var free = id == 1000 ? 1001 : 1000;
        Func<IReadOnlySet<int>> allButTwo = () => Enumerable.Range(1000, 9000).Where(other => other != id && other != free).ToHashSet();
        Require(Enumerable.Range(0, 8).All(seed => HairSkeletonFiles.Prepare(
                    Rig(), mod.ModFolder, ModDirectory, ModelY, Sources(seed, taken: allButTwo), mod.Store).SkeletonId == free),
            "Hair ID clash: a new pick avoids the IDs the mod's own options use");
    }

    /// <summary>The mod's physics file at a game path with the first chain's dampening set.</summary>
    private static void TuneDampening(HairModFixture mod, string gamePath, float dampening)
    {
        var tuned = ReadPhyb(mod, gamePath);
        tuned.Simulators[0].Chains[0].Dampening = dampening;
        File.WriteAllBytes(mod.FilePath(gamePath), tuned.ToBytes());
    }

    private static void FailedMoveFinishesNextTime(string root)
    {
        var mod = HairMod(root, [MiqoteHair, AuRaHair], [MiqoteHair]);
        var id = PrepareAndApply(mod, Sources(7)).SkeletonId;
        TuneDampening(mod, Phyb(801, id), 0.3f);
        Func<IReadOnlySet<int>> taken = () => new HashSet<int> { id };
        var plan = HairSkeletonFiles.Prepare(Rig(), mod.ModFolder, ModDirectory, HairModel, Sources(8, taken: taken), mod.Store);
        var moved = plan.SkeletonId;

        // A read-only meta.json makes the move fail after the record and the new files are written.
        var metaPath = Path.Combine(mod.ModFolder, "meta.json");
        File.SetAttributes(metaPath, File.GetAttributes(metaPath) | FileAttributes.ReadOnly);
        var threw = false;
        try { HairSkeletonFiles.Apply(plan, mod.ModFolder, ModDirectory, HairModel, mod.Store, mod.EstStore); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { threw = true; }
        finally { File.SetAttributes(metaPath, File.GetAttributes(metaPath) & ~FileAttributes.ReadOnly); }
        Require(threw && mod.Store.Load() is [{ SkeletonId: var stored, MovingFrom: var from }] && stored == moved && from == id,
            "Hair ID move: a failed move leaves a record of the new ID that names the old one");

        // The next export no longer clashes with anything, but still finishes the move.
        var again = PrepareAndApply(mod, Sources(9, taken: taken));
        Require(again.SkeletonId == moved && again.PreviousId == id, "Hair ID move: the next export moves from the recorded old ID");
        var meta = mod.Meta();
        var containers = new[] { meta["DefaultData"]!.AsObject(), Option(meta, OptionA), Option(meta, OptionB) };
        Require(containers.All(container => !container["Files"]!.AsObject()
                    .Any(pair => pair.Key.Contains($"/h{id:D4}/", StringComparison.Ordinal))) &&
                SklbKeys(Option(meta, OptionA)).SetEquals([Sklb(801, moved), Sklb(1401, moved)]) &&
                EstEntry(Option(meta, OptionA), "Miqote") == moved,
            "Hair ID move: no key or EST entry of the old ID remains");
        Require(new[] { Sklb(801, id), Sklb(1401, id), Phyb(801, id), Phyb(1401, id) }.All(path => !File.Exists(mod.FilePath(path))),
            "Hair ID move: the old ID's files are gone");
        Require(mod.Store.Load() is [{ SkeletonId: var final, MovingFrom: null }] && final == moved,
            "Hair ID move: the record names only the new ID");
        Require(ReadPhyb(mod, Phyb(801, moved)).Simulators[0].Chains[0].Dampening == 0.3f,
            "Hair ID move: the finished move keeps the tuned dampening");
    }

    private static void KindChangeTakesThePreset(string root)
    {
        var mod = HairMod(root, [MiqoteHair], []);
        var id = PrepareAndApply(mod, Sources(7)).SkeletonId;
        Require(mod.Store.Load() is [{ ChainKinds: { } kinds }] && kinds.Count == 1 && kinds["j_ex_hair_01_a"] == "back",
            "Hair physics kinds: the record keeps each chain's kind by its first bone");
        TuneDampening(mod, Phyb(801, id), 0.3f);
        var back = PrepareAndApply(mod, Sources(7));
        Require(ReadPhyb(mod, Phyb(801, id)).Simulators[0].Chains[0].Dampening == 0.3f &&
                back.Notes.Any(note => note.Contains("Kept your physics settings", StringComparison.Ordinal)),
            "Hair physics kinds: a back chain exported as back again keeps its tuning");

        var rig = Rig();
        rig.Chains![0].Kind = "ponytail";
        var ponytail = PrepareAndApply(mod, Sources(7), rig);
        Require(ReadPhyb(mod, Phyb(801, id)).Simulators[0].Chains[0].Dampening == 0.04f &&
                !ponytail.Notes.Any(note => note.Contains("Kept", StringComparison.Ordinal)),
            "Hair physics kinds: a back chain exported as a ponytail gets the ponytail preset, without a note");
        Require(mod.Store.Load() is [{ ChainKinds: { } now }] && now["j_ex_hair_01_a"] == "ponytail",
            "Hair physics kinds: the record keeps the new kind");
    }

    private static void OldRecordsLoad(string root)
    {
        var config = Path.Combine(root, "config");
        WriteJson(config, "hair-skeletons.json",
            """[{"mod": "m", "model": "Files/a.mdl", "skeletonId": 4321}, {"mod": "m", "model": "Files/b.mdl", "skeletonId": 4322, "movingFrom": 7}]""");
        var store = new HairSkeletonStore(config);
        Require(store.Load() is [{ SkeletonId: 4321, MovingFrom: null, ChainKinds: null }, { SkeletonId: 4322, MovingFrom: null }],
            "Hair skeleton store: records without the new fields load, and a move from outside the custom range is dropped");
        store.Save(store.Load());
        Require(!File.ReadAllText(Path.Combine(config, "hair-skeletons.json")).Contains("movingFrom", StringComparison.Ordinal),
            "Hair skeleton store: empty fields are not written");
    }
}
