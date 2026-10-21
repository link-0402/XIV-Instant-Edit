using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json.Nodes;
using InstantEdit.Models;
using InstantEdit.Services.Skeletons;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// Custom hair skeletons: free skeleton IDs, vanilla templates, the IDs the mod library uses, and the
/// rig request with the bone layout made from it.
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
}
