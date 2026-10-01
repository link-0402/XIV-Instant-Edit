using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using InstantEdit.Models;
using InstantEdit.Services.CharacterSend;
using InstantEdit.Services.Painter;
using InstantEdit.Ui;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// Send my character to Blender without the game: which of a character's models a send takes and in
/// what order, which of them the game draws now and with what parts and shapes, the names and import
/// entries it sends with them, which bone holds a weapon and where the weapon sits on it, and the
/// send's summary.
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
        CheckDrawState();
        CheckPartMasks();
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

        var connector = Node(ConnectorPath, @"C:\mods\Body\connectors.mdl", section: ResourceSection.Other, order: int.MaxValue);
        var alias = Node(ConnectorPath.Replace("b0002", "b0006"), @"C:\MODS\Body\connectors.mdl", section: ResourceSection.Other,
            order: int.MaxValue);
        Require(CharacterSendPlan.Models([connector, alias], includeWeapons: false) is [{ Node.GamePath: ConnectorPath }],
            "a file the character loads under two game paths goes once, since it draws the same under each");
        var mainHand = Node(WeaponPath, @"C:\mods\Sword\sword.mdl", section: ResourceSection.Gear, order: 10);
        var offHand = Node(WeaponPath.Replace("w0101", "w0151"), @"C:\mods\Sword\sword.mdl", section: ResourceSection.Gear, order: 11);
        Require(CharacterSendPlan.Models([mainHand, offHand, mainHand], includeWeapons: true) is [{ Node.GamePath: WeaponPath }, { Node: var second }] &&
                second == offHand,
            "a weapon file both hands use goes once per hand, so each hangs its own copy");
    }

    private const string SmallclothesTopPath = "chara/equipment/e0000/model/c0201e0000_top.mdl";
    private const string SmallclothesLegsPath = "chara/equipment/e0000/model/c0201e0000_dwn.mdl";
    private const string LowPolyPath = "chara/human/c0801/obj/body/b0003/model/c0801b0003_top.mdl";
    private const string ConnectorPath = "chara/human/c0201/obj/body/b0002/model/c0201b0002_top.mdl";

    private static void CheckDrawState()
    {
        var face = Game(FacePath, ResourceSection.CharacterFeatures, 0);
        var top = Node(TopPath, @"C:\mods\Robe\top.mdl", order: 1);
        var underTop = Node(SmallclothesTopPath, @"C:\mods\Body\top.mdl", order: 1);
        var legs = Node(SmallclothesLegsPath, @"C:\mods\Body\dwn.mdl", order: 3);
        var stale = Node(FeetPath, @"C:\mods\Old\sho.mdl", order: 4);
        var lowPoly = Game(LowPolyPath, ResourceSection.Other, int.MaxValue);
        var connector = Node(ConnectorPath, @"C:\mods\Body\connectors.mdl", section: ResourceSection.Other, order: int.MaxValue);
        var weapon = Game(WeaponPath, ResourceSection.Gear, 10);
        ResourceNode[] roots = [face, top, underTop, legs, stale, lowPoly, connector, weapon];

        static string Drawn(string file) => PainterVisibility.NormalizePath(file);
        var live = new PainterLiveCharacter(
        [
            new PainterLiveModel(Drawn(FacePath), 0xFFFFFFEF, 0, 11),
            new PainterLiveModel(Drawn(@"C:\mods\Robe\top.mdl"), 0xFFFFFFD7, 0b1, 1),
            // Smallclothes loaded next to the gear of their slot.
            new PainterLiveModel(Drawn(@"C:\mods\Body\top.mdl"), 0xFFFFFFFF, 0, 17),
            new PainterLiveModel(Drawn(@"C:\mods\Body\dwn.mdl"), 0xFFFFFFFF, 0b10, 3),
            new PainterLiveModel(Drawn(LowPolyPath), 0xFFFFFFFF, 0, 15),
            // One connector file in two slots, each with other bands on.
            new PainterLiveModel(Drawn(@"C:\mods\Body\connectors.mdl"), 0xFFFFFFF2, 0b1, 13),
            new PainterLiveModel(Drawn(@"C:\mods\Body\connectors.mdl"), 0xFFFFFFF8, 0b100, 14),
            // Drawn since the list was made.
            new PainterLiveModel(Drawn(@"C:\mods\New\glv.mdl"), 0xFFFFFFFF, 0, 2),
            new PainterLiveModel(Drawn(WeaponPath), 0xFFFFFFFE, 0),
            // Held by another object attached to the character, such as a parasol.
            new PainterLiveModel(Drawn(@"C:\mods\Parasol\parasol.mdl"), 0xFFFFFFFF, 0),
        ], null);

        var plan = CharacterDrawState.Plan(CharacterSendPlan.Models(roots, includeWeapons: false), roots, live);
        Require(plan.Known && plan.Models.Select(model => model.Model.Node.GamePath).SequenceEqual([FacePath, TopPath, SmallclothesLegsPath, ConnectorPath]),
            "a send takes the models the character draws: its face, its gear, smallclothes where no gear is, and seam connectors");
        var reasons = plan.LeftOut.ToDictionary(model => model.FileName, model => model.Reason);
        Require(reasons.Count == 3 && reasons["c0801b0003_top.mdl"] == CharacterDrawState.LowPolyReason &&
                reasons["c0201e0000_top.mdl"] == CharacterDrawState.SmallclothesReason &&
                reasons["c0201e6001_sho.mdl"] == CharacterDrawState.NotDrawnReason,
            "the low-poly body, smallclothes loaded next to gear of their slot, and models no longer drawn are left out");
        var drawnConnector = plan.Models[3];
        Require(drawnConnector.AttributeMasks.SequenceEqual([0xFFFFFFF2u, 0xFFFFFFF8u]) && drawnConnector.Shapes == 0b101u &&
                plan.Models[1] is { AttributeMasks: [0xFFFFFFD7u], Shapes: 0b1u },
            "each model carries the attributes of every copy the character draws, and every shape key the game has on");
        var armed = CharacterDrawState.Plan(CharacterSendPlan.Models(roots, includeWeapons: true), roots, live);
        Require(plan.Missing.SequenceEqual(["glv.mdl"]) && armed.Missing.SequenceEqual(["glv.mdl"]),
            "a model the character draws in its own slots that the On Screen list lacks is named; attached objects aren't listed, so they don't count");
        var outside = CharacterDrawState.Plan(CharacterSendPlan.Models(roots, includeWeapons: false), roots, live, path => path.EndsWith("glv.mdl", StringComparison.Ordinal));
        Require(outside.Missing.Count == 0 && outside.External.SequenceEqual(["glv.mdl"]),
            "a drawn model from outside the installed mods isn't missing from the list, which can't hold it, but can't be sent");
        Require(armed.Models[^1] is { Model.Role: CharacterModelRole.Weapon, AttributeMasks: [0xFFFFFFFEu] },
            "a weapon takes the draw state of the weapon's own model");
        var weaponAsBody = new PainterLiveCharacter([new PainterLiveModel(Drawn(FacePath), 0, 0)], null);
        Require(CharacterDrawState.Plan([new CharacterSendModel(face, CharacterModelRole.Body)], [face], weaponAsBody).Models.Count == 0,
            "a body model matches only the character's own slots, not a weapon's");

        var unknown = CharacterDrawState.Plan(CharacterSendPlan.Models(roots, includeWeapons: false), roots, null);
        Require(!unknown.Known && unknown.Models.Count == 7 && unknown.Models.All(model => model.AttributeMasks.Count == 0 && model.Shapes is null) &&
                unknown.LeftOut.Count == 0 && unknown.Missing.Count == 0,
            "without the draw state every model of the list goes, with every part");

        uint[] parts = [0, 0b01, 0b10, 0b11];
        Require(CharacterDrawState.Count(parts, [0b01]) == (2, 2) && CharacterDrawState.Count(parts, [0b01, 0b10]) == (3, 1) &&
                CharacterDrawState.Count(parts, []) == (4, 0),
            "a part is drawn when one drawn copy enables all of its attributes; without masks every part is");
        Require(CharacterDrawState.IsLowPolyBody(@"chara\human\c1401\obj\body\b0003\model\c1401b0003_top.mdl") &&
                !CharacterDrawState.IsLowPolyBody(ConnectorPath),
            "the low-poly body is the human body model b0003 of any race");
    }

    private static void CheckPartMasks()
    {
        var model = PainterScenarios.SyntheticModel();
        Require(CharacterDrawState.PartMasks(model) is [0b01u, 0b10u],
            "a model's parts are its LOD-0 submeshes with their attribute masks, as the add-on imports them");
        Require(CharacterDrawState.PartMasks(new byte[16]) is null && CharacterDrawState.PartMasks(model[..200]) is null,
            "a file that isn't a whole model has no known parts");
    }

    private static void CheckNames()
    {
        Require(CharacterSendPlan.Key("Firstname Lastname", 73) == "Firstname Lastname@73" &&
                CharacterSendPlan.Key(" Firstname Lastname ", 0) == "Firstname Lastname",
            "Blender tells a character's sends apart by its name and home world");
        Require(CharacterSendPlan.ArmatureName(null) == "Skeleton" && CharacterSendPlan.ArmatureName("  Rig  ") == "Rig" &&
                CharacterSendPlan.ArmatureName(new string('x', 70)).Length == CharacterSendPlan.MaximumArmatureName &&
                CharacterSendPlan.ArmatureName(new string('骨', 30)) == new string('骨', 21) &&
                CharacterSendPlan.ArmatureName(new string('x', 62) + "😀") == new string('x', 62),
            "the send's armature takes the animation armature's name, cut at a whole character to the bytes Blender keeps");
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
        Require(body["enabledAttributes"] is null && body["enabledShapes"] is null,
            "a model whose draw state isn't known sends none, so every part is drawn");
        var drawn = JsonNode.Parse(JsonSerializer.Serialize(new CharacterImportEntry(new string('a', 32), "Name@73", "Name",
            CharacterImportEntry.BodyRole, "Skeleton", EnabledAttributes: [0xFFFFFFF2u, 7u], EnabledShapes: 5u)))!.AsObject();
        Require(drawn["enabledAttributes"]?.AsArray().Select(mask => mask!.GetValue<uint>()).SequenceEqual([0xFFFFFFF2u, 7u]) == true &&
                drawn["enabledShapes"]?.GetValue<uint>() == 5u,
            "a model's draw state goes as the masks the add-on reads");
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

        var live = CharacterSendPlan.Summary(Outcome([Sent("a.mdl")]) with
        {
            HiddenParts = 3,
            LeftOut = [new("c0801b0003_top.mdl", CharacterDrawState.LowPolyReason), new("c0201e0000_top.mdl", CharacterDrawState.SmallclothesReason)],
        });
        Require(!live.Warned && live.Text.Contains("3 parts your character doesn't show now came over hidden", StringComparison.Ordinal) &&
                live.Text.Contains("Left out 2 models", StringComparison.Ordinal) &&
                live.Text.Contains($"c0801b0003_top.mdl ({CharacterDrawState.LowPolyReason})", StringComparison.Ordinal),
            "the summary counts the hidden parts and names what it left out, without a warning");
        Require(CharacterSendPlan.Summary(Outcome([Sent("a.mdl")]) with { DrawStateKnown = false }).Warned,
            "a send that couldn't read what the character draws warns");
        var missing = CharacterSendPlan.Summary(Outcome([Sent("a.mdl")]) with { Missing = ["glv.mdl"] });
        Require(missing.Warned && missing.Text.Contains("glv.mdl", StringComparison.Ordinal) && missing.Text.Contains("Refresh the list", StringComparison.Ordinal),
            "a model the character draws that the list lacks makes the summary a warning with what to do");
    }
}
