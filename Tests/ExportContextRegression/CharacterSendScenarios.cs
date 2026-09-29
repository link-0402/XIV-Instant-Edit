using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using InstantEdit.Models;
using InstantEdit.Services.CharacterSend;
using InstantEdit.Ui;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// Send my character to Blender without the game: which of a character's models a send takes and in
/// what order, the names and import entries it sends with them, which bone holds a weapon and where
/// the weapon sits on it, and the send's summary.
/// </summary>
internal static class CharacterSendScenarios
{
    private const string FacePath = "chara/human/c0801/obj/face/f0001/model/c0801f0001_fac.mdl";
    private const string HairPath = "chara/human/c0801/obj/hair/h0101/model/c0801h0101_hir.mdl";
    private const string TailPath = "chara/human/c0801/obj/tail/t0001/model/c0801t0001_til.mdl";
    private const string TopPath = "chara/equipment/e6001/model/c0201e6001_top.mdl";
    private const string FeetPath = "chara/equipment/e6001/model/c0201e6001_sho.mdl";
    private const string RingPath = "chara/accessory/a0001/model/c0201a0001_rir.mdl";
    private const string BodyPath = "chara/human/c0801/obj/body/b0001/model/c0801b0001_top.mdl";
    private const string WeaponPath = "chara/weapon/w0101/obj/body/b0001/model/w0101b0001.mdl";

    public static void Run()
    {
        CheckModels();
        CheckNames();
        CheckEntry();
        CheckWeaponPlacement();
        CheckSummary();
    }

    private static ResourceNode Node(string gamePath, string actualPath, ResourceSourceState state = ResourceSourceState.LoadedMod,
        ResourceSection section = ResourceSection.Gear, int order = 0, string type = "Mdl", ResourceNode[]? children = null)
        => new()
        {
            Type = type,
            Icon = "",
            Name = Path.GetFileName(gamePath),
            GamePath = gamePath,
            ActualPath = actualPath,
            Children = children ?? [],
            SourceState = state,
            SourceLabel = state == ResourceSourceState.GameData ? "Game data" : "Loaded from: Mod",
            SourceModName = state == ResourceSourceState.LoadedMod ? "Mod" : null,
            SourceModDirectory = state == ResourceSourceState.LoadedMod ? "ModDir" : null,
            SlotLabel = "Body",
            ResourceSection = section,
            SortOrder = order,
        };

    private static ResourceNode Game(string gamePath, ResourceSection section, int order)
        => Node(gamePath, gamePath, ResourceSourceState.GameData, section, order);

    private static void CheckModels()
    {
        var face = Game(FacePath, ResourceSection.CharacterFeatures, 0);
        var hair = Node(HairPath, @"C:\mods\Hair\hair.mdl", section: ResourceSection.CharacterFeatures, order: 1);
        var tail = Game(TailPath, ResourceSection.CharacterFeatures, 3);
        var material = Node("chara/equipment/e6001/material/v0001/mt_c0201e6001_top_a.mtrl", @"C:\mods\Top\top.mtrl", type: "Mtrl");
        var top = Node(TopPath, @"C:\mods\Top\top.mdl", order: 1, children: [material]);
        var feet = Game(FeetPath, ResourceSection.Gear, 4);
        var ring = Game(RingPath, ResourceSection.Gear, 8);
        var body = Game(BodyPath, ResourceSection.Other, int.MaxValue);
        var weapon = Game(WeaponPath, ResourceSection.Gear, 10);
        var effect = Game("vfx/common/eff/cmdc_body_a.mdl", ResourceSection.Other, int.MaxValue);
        var unattributed = Node(TopPath.Replace("_top", "_glv"), @"C:\elsewhere\glv.mdl", ResourceSourceState.ExternalResolvedFile);
        var duplicate = Node(TopPath, @"C:\mods\Top\top.mdl", order: 1);
        ResourceNode[] roots = [weapon, top, effect, hair, face, duplicate, ring, feet, tail, unattributed, body];

        var models = CharacterSendPlan.Models(roots, includeWeapons: false);
        Require(models.Select(model => model.Node.GamePath).SequenceEqual([FacePath, HairPath, TailPath, TopPath, FeetPath, RingPath, BodyPath]),
            "a character send takes every body, face, hair, tail and gear model once, in On Screen's order");
        Require(models.All(model => model.Role == CharacterModelRole.Body), "without weapons every model moves with the body");
        var armed = CharacterSendPlan.Models(roots, includeWeapons: true);
        Require(armed.Count == models.Count + 1 && armed[^1].Node.GamePath == WeaponPath && armed[^1].Role == CharacterModelRole.Weapon,
            "weapons, when asked for, come last with their own role");
        Require(roots.All(node => CharacterSendPlan.IsSendable(node) == ResourceViews.IsSafeModel(ResourceViews.FromNode(node))),
            "a send takes exactly the models On Screen's edit action accepts");
        Require(CharacterSendPlan.RoleOf(material.GamePath) is null && CharacterSendPlan.RoleOf(effect.GamePath) is null &&
                CharacterSendPlan.RoleOf(@"chara\weapon\w0101\obj\body\b0001\model\w0101b0001.mdl") == CharacterModelRole.Weapon,
            "only character models have a role");
        Require(armed[3].FileName == "c0201e6001_top.mdl", "a model's file name comes from its game path");
    }

    private static void CheckNames()
    {
        Require(CharacterSendPlan.Key("Firstname Lastname", 73) == "Firstname Lastname@73" &&
                CharacterSendPlan.Key(" Firstname Lastname ", 0) == "Firstname Lastname",
            "Blender tells a character's sends apart by its name and home world");
        Require(CharacterSendPlan.ArmatureName(null) == "Skeleton" && CharacterSendPlan.ArmatureName("  Rig  ") == "Rig" &&
                CharacterSendPlan.ArmatureName(new string('x', 70)).Length == CharacterSendPlan.MaximumArmatureName,
            "the send's armature takes the animation armature's name, cut to what Blender keeps");
        Require(CharacterSendPlan.WeaponModelPath(101, 1) == WeaponPath && CharacterSendPlan.WeaponOf(WeaponPath) == ((ushort)101, (ushort)1) &&
                CharacterSendPlan.WeaponOf(FacePath) is null,
            "a weapon's model path and its model set and body name each other");
    }

    private static void CheckEntry()
    {
        float[] offset = [0.01f, 0.12f, -0.03f, 0, 0, 0, 1, 1, 1, 1];
        var weapon = JsonNode.Parse(JsonSerializer.Serialize(new CharacterImportEntry(new string('a', 32), "Name@73", "Name",
            CharacterImportEntry.WeaponRole, "Skeleton", new CharacterAttach("n_buki_r", offset))))!.AsObject();
        Require(weapon["sendId"]?.GetValue<string>() == new string('a', 32) && weapon["key"]?.GetValue<string>() == "Name@73" &&
                weapon["name"]?.GetValue<string>() == "Name" && weapon["role"]?.GetValue<string>() == "weapon" &&
                weapon["armatureName"]?.GetValue<string>() == "Skeleton" &&
                weapon["attach"]?["bone"]?.GetValue<string>() == "n_buki_r" && weapon["attach"]?["offset"]?.AsArray().Count == 10,
            "an import's character entry has the fields the add-on reads");
        var body = JsonNode.Parse(JsonSerializer.Serialize(new CharacterImportEntry(new string('a', 32), "Name@73", "Name",
            CharacterImportEntry.BodyRole, "Skeleton")))!.AsObject();
        Require(body["role"]?.GetValue<string>() == "body" && body.ContainsKey("attach") && body["attach"] is null,
            "a body model hangs from no bone");
    }

    private static Matrix4x4 Full(BoneTransform transform) => CharacterWeaponPlacer.Matrix(transform, rigid: false);

    private static bool Near(Matrix4x4 a, Matrix4x4 b, float tolerance = 1e-5f)
    {
        for (var row = 0; row < 4; row++)
            for (var column = 0; column < 4; column++)
                if (MathF.Abs(a[row, column] - b[row, column]) > tolerance)
                    return false;
        return true;
    }

    private static BoneTransform T(float x, float y, float z, Quaternion rotation, float scale = 1)
        => new(new Vector3(x, y, z), rotation, new Vector3(scale));

    private static Quaternion Q(float x, float y, float z, float degrees)
        => Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(x, y, z)), degrees * MathF.PI / 180);

    private static void CheckWeaponPlacement()
    {
        // The hand's weapon bone carries a small scale, as a posed bone may; Blender's bones don't.
        var buki = T(0.32f, 0.95f, 0.12f, Q(0.2f, 1, 0.1f, 70), 1.02f);
        List<(string, BoneTransform)> bones =
        [
            ("n_root", T(0, 0, 0, Quaternion.Identity)),
            ("j_te_r", T(0.3f, 1.0f, 0.1f, Q(0, 0, 1, -40))),
            ("n_buki_r", buki),
            ("n_buki_l", T(-0.32f, 0.95f, 0.12f, Q(0, 1, 0, -70))),
        ];
        var child = T(0.01f, 0.05f, 0, Q(1, 0.3f, 0, 25));
        var weapon = Full(child) * Full(buki);
        var attached = CharacterWeaponPlacer.Place(WeaponPath, bones, weapon, child);
        Require(attached is { Bone: "n_buki_r", Method: CharacterWeaponPlacement.AttachMethod, Offset.Length: 10 } &&
                Near(CharacterWeaponPlacer.Matrix(AsTransform(attached.Offset!), rigid: false) *
                     CharacterWeaponPlacer.Matrix(buki, rigid: true), weapon),
            "the attach's bone is the one its child transform puts the weapon on, and the weapon sits on it where the game draws it");

        var misread = CharacterWeaponPlacer.Place(WeaponPath, bones, weapon, T(0.2f, 0, 0, Q(0, 1, 0, 90)));
        Require(misread is { Bone: "n_buki_r", Method: CharacterWeaponPlacement.NearestMethod } &&
                Near(CharacterWeaponPlacer.Matrix(AsTransform(misread.Offset!), rigid: false) *
                     CharacterWeaponPlacer.Matrix(buki, rigid: true), weapon),
            "an attach that doesn't put the weapon where it is drawn falls back to the nearest weapon bone");

        Require(CharacterWeaponPlacer.IsWeaponBone("n_buki_r") && CharacterWeaponPlacer.IsWeaponBone("j_buki_kosi_l") &&
                CharacterWeaponPlacer.IsWeaponBone("j_buki2_kosi_r") && CharacterWeaponPlacer.IsWeaponBone("n_buki_tate_l") &&
                !CharacterWeaponPlacer.IsWeaponBone("j_te_r"),
            "weapons hang from the hand, hip, back and shield bones the game names buki");
        var hip = T(-0.15f, 0.95f, -0.08f, Q(0, 0, 1, 80));
        List<(string, BoneTransform)> sheathed = [.. bones, ("j_buki_kosi_l", hip)];
        var onHip = Full(T(0, 0.02f, 0, Quaternion.Identity)) * Full(hip);
        Require(CharacterWeaponPlacer.Place(WeaponPath, sheathed, onHip, null) is { Bone: "j_buki_kosi_l", Method: CharacterWeaponPlacement.NearestMethod },
            "a sheathed weapon without an attach hangs from the hip bone it sits on");

        var far = Matrix4x4.CreateTranslation(3, 0, 0);
        Require(CharacterWeaponPlacer.Place(WeaponPath, bones, far, null) is { Bone: null, Offset: null, Method: CharacterWeaponPlacement.NoneMethod },
            "a weapon far from every weapon bone hangs from none");
        Require(CharacterWeaponPlacer.Offset(new Matrix4x4()) is null, "a degenerate place is no offset");
    }

    private static BoneTransform AsTransform(float[] offset)
        => new(new Vector3(offset[0], offset[1], offset[2]), new Quaternion(offset[3], offset[4], offset[5], offset[6]),
            new Vector3(offset[7], offset[8], offset[9]));

    private static void CheckSummary()
    {
        CharacterSentModel Sent(string file, bool scaled = false, params string[] notes) => new(file, notes, scaled);
        CharacterSendOutcome Outcome(IReadOnlyList<CharacterSentModel> sent, CharacterPose pose = CharacterPose.Rest,
            IReadOnlyList<string>? failed = null, string? poseResult = null, string? poseError = null, IReadOnlyList<string>? weapons = null)
            => new("Name", pose, sent, failed ?? [], poseResult, poseError, null, weapons ?? [], []);

        var clean = CharacterSendPlan.Summary(Outcome([Sent("a.mdl"), Sent("b.mdl"), Sent("c.mdl")]));
        Require(!clean.Warned && clean.Text.Contains("3 models", StringComparison.Ordinal) && clean.Text.Contains("Name", StringComparison.Ordinal),
            "a clean send reports its models without a warning");
        Require(!CharacterSendPlan.Summary(Outcome([Sent("a.mdl", scaled: true)])).Warned,
            "racially scaled models are mentioned without a warning, as for single sends");
        Require(CharacterSendPlan.Summary(Outcome([Sent("a.mdl")], failed: ["b.mdl: missing"])).Warned,
            "models that could not be sent make the summary a warning");
        Require(CharacterSendPlan.Summary(Outcome([Sent("a.mdl")], CharacterPose.Current, poseError: "refused")).Warned,
            "a pose Blender didn't key makes the summary a warning");
        Require(CharacterSendPlan.Summary(Outcome([Sent("a.mdl")], weapons: ["w.mdl stays at the origin"])).Warned,
            "a weapon left at the origin makes the summary a warning");
        var noted = CharacterSendPlan.Summary(Outcome([Sent("a.mdl", false, "one", "two"), Sent("b.mdl", false, "three", "four", "five")]));
        Require(noted.Warned && noted.Text.Contains("and 2 more", StringComparison.Ordinal) && !noted.Text.Contains('\u2026'),
            "the summary lists a few model warnings and counts the rest, without an ellipsis");
        var posed = CharacterSendPlan.Summary(Outcome([Sent("a.mdl")], CharacterPose.Animation, poseResult: "Keyed it."));
        Require(!posed.Warned && posed.Text.Contains("Keyed it.", StringComparison.Ordinal), "the summary says what Blender did with the pose");
    }
}
