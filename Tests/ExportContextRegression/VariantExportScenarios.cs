using System.Text.Json;
using System.Text.Json.Nodes;
using InstantEdit.Models;
using InstantEdit.Services;
using static InstantEdit.TestSupport.Assertions;

internal static class VariantExportScenarios
{
    private const string GamePath = "chara/equipment/e0001/model/c0101e0001_top.mdl";

    public static void Run(string testRoot)
    {
        SelectedGroupKeepsIdentity(Path.Combine(testRoot, "SelectedGroupV4"));
        NewGroupUsesName(Path.Combine(testRoot, "NewGroupV4"));
        OptionTargetKeepsIdentity(Path.Combine(testRoot, "OptionTargetV4"));
        UnsupportedMetadataIsRejected(Path.Combine(testRoot, "UnsupportedMetadata"));
        AttributeGroupsAreGeneratedAndUpdated(Path.Combine(testRoot, "AttributeGroups"));
        ExportReceiptsMatchOperation();
    }

    private static JsonObject Metadata(params JsonObject[] groups) => new()
    {
        ["FileVersion"] = 4,
        ["Identifier"] = Guid.NewGuid(),
        ["LastWrite"] = DateTime.UtcNow.AddMinutes(-1),
        ["Name"] = "User mod",
        ["DefaultData"] = null,
        ["Groups"] = groups.Length == 0 ? null : new JsonArray(groups),
    };

    private static void NewGroupUsesName(string root)
    {
        Directory.CreateDirectory(root);
        var metaPath = Path.Combine(root, "meta.json");
        File.WriteAllText(metaPath, Metadata().ToJsonString());
        for (var option = 1; option <= 2; option++)
        {
            Require(PenumbraService.PrepareVariantGroup(root, GamePath, $"Files/new{option}.mdl",
                    $"New {option}", "New Group", out var prepared) is null &&
                    PenumbraService.CommitVariantGroup(prepared!) is null,
                "v4: New Group creates or reuses a compatible name");
        }
        var meta = JsonNode.Parse(File.ReadAllText(metaPath))!;
        var group = meta["Groups"]!.AsArray().Single()!;
        Require(group["Options"]!.AsArray().Count == 3 && group["DefaultSettings"]!.GetValue<int>() == 2,
            "v4: name-based reuse retains both new options and None");
        Require(Guid.TryParse(group["Id"]!.GetValue<string>(), out _) &&
                group["Options"]!.AsArray().All(option => Guid.TryParse(option!["Id"]!.GetValue<string>(), out _)) &&
                DateTimeOffset.Parse(meta["LastWrite"]!.GetValue<string>()) > DateTimeOffset.UtcNow.AddMinutes(-1) &&
                !Directory.EnumerateFiles(root).Any(path => Path.GetFileName(path).StartsWith("group_", StringComparison.OrdinalIgnoreCase)),
            "v4: new groups and options receive GUIDs and update embedded metadata only");
    }

    private static JsonObject Group(Guid id, string name) => new()
    {
        ["Id"] = id,
        ["Name"] = name,
        ["Type"] = "Single",
        ["Description"] = "User description",
        ["Priority"] = 42,
        ["CustomMetadata"] = new JsonObject { ["Keep"] = true },
        ["Options"] = new JsonArray(new JsonObject
        {
            ["Id"] = Guid.NewGuid(), ["Name"] = "Original",
            ["Files"] = new JsonObject { [GamePath] = "Files/original.mdl" },
            ["FileSwaps"] = new JsonObject { ["a"] = "b" },
        }),
    };

    private static void SelectedGroupKeepsIdentity(string root)
    {
        Directory.CreateDirectory(root);
        var selectedId = Guid.NewGuid();
        var selected = Group(selectedId, "Renamed");
        var sibling = Group(Guid.NewGuid(), "Renamed");
        var oldName = Group(Guid.NewGuid(), "Cached Name");
        var metaPath = Path.Combine(root, "meta.json");
        selected["Page"] = 2;
        selected["Layout"] = new JsonArray("Hide", "DefaultClosed");
        selected["Condition"] = new JsonObject { ["Type"] = "True" };
        selected["Options"]![0]!["Layout"] = new JsonArray("Separator");
        selected["Options"]![0]!["Color"] = 4;
        var meta = Metadata(selected, sibling, oldName);
        meta["PageNames"] = new JsonObject { ["2"] = "Second" };
        File.WriteAllText(metaPath, meta.ToJsonString());
        var target = $"group:{selectedId:D}";
        var error = PenumbraService.PrepareVariantGroup(root, GamePath, "Files/new.mdl", "New",
            "Cached Name", out var prepared, targetId: target);
        Require(error is null && prepared is not null, "v4: renamed group resolves by selected identity");
        Require(PenumbraService.CommitVariantGroup(prepared!) is null, "v4: selected group commits");
        var writtenMeta = JsonNode.Parse(File.ReadAllText(metaPath))!;
        var written = writtenMeta["Groups"]![0]!;
        Require(written["Name"]!.GetValue<string>() == "Renamed" &&
                written["Description"]!.GetValue<string>() == "User description" &&
                written["Priority"]!.GetValue<int>() == 42 &&
                written["CustomMetadata"]!["Keep"]!.GetValue<bool>() &&
                written["Page"]!.GetValue<int>() == 2 &&
                written["Layout"]!.AsArray().Count == 2 &&
                written["Condition"]!["Type"]!.GetValue<string>() == "True" &&
                written["Options"]![0]!["Layout"]!.AsArray().Count == 1 &&
                written["Options"]![0]!["Color"]!.GetValue<int>() == 4 &&
                writtenMeta["PageNames"]!["2"]!.GetValue<string>() == "Second" &&
                written["Options"]!.AsArray().Count == 2 &&
                written["Options"]!.AsArray().All(option =>
                    !string.Equals(option?["Name"]?.GetValue<string>(), "None", StringComparison.OrdinalIgnoreCase)) &&
                written["Options"]![1]!["Files"]![GamePath]!.GetValue<string>() == "Files/new.mdl" &&
                written["DefaultSettings"]!.GetValue<int>() == 1,
            "v4: optional and extension metadata survives adding and selecting the new option");
        Require(JsonNode.DeepEquals(writtenMeta["Groups"]![1], sibling) &&
                JsonNode.DeepEquals(writtenMeta["Groups"]![2], oldName) && writtenMeta["Groups"]!.AsArray().Count == 3,
            "v4: duplicate and cached names cannot redirect the selected write");

        foreach (var stale in new[] { "missing", "incompatible" })
        {
            if (stale == "incompatible")
            {
                written["Type"] = "Multi";
                File.WriteAllText(metaPath, writtenMeta.ToJsonString());
            }
            var before = Directory.GetFiles(root).ToDictionary(path => path, File.ReadAllText);
            var staleId = stale == "missing"
                ? $"group:{Guid.NewGuid():D}"
                : target;
            Require(PenumbraService.PrepareVariantGroup(root, GamePath, "Files/new.mdl", "New",
                    "Cached Name", out var rejected, targetId: staleId) is not null && rejected is null &&
                    before.All(pair => File.ReadAllText(pair.Key) == pair.Value),
                $"v4: {stale} group is rejected without filesystem changes");
        }
    }

    private static void OptionTargetKeepsIdentity(string root)
    {
        Directory.CreateDirectory(root);
        var groupId = Guid.NewGuid();
        var firstId = Guid.NewGuid();
        var selectedId = Guid.NewGuid();
        var group = Group(groupId, "Duplicate");
        group["Options"] = new JsonArray(
            new JsonObject { ["Id"] = firstId, ["Name"] = "Same", ["Files"] = new JsonObject { [GamePath] = "Files/first.mdl" } },
            new JsonObject { ["Id"] = selectedId, ["Name"] = "Same", ["Files"] = new JsonObject { [GamePath] = "Files/selected.mdl" } });
        var metaPath = Path.Combine(root, "meta.json");
        var meta = Metadata(group);
        File.WriteAllText(metaPath, meta.ToJsonString());
        var optionTarget = $"option:{groupId:D}:{selectedId:D}";
        var exposed = PenumbraService.ReadVariantTargetsForRegression(root, GamePath);
        Require(exposed.Single().Id == $"group:{groupId:D}" && exposed.Single().Options[1].Id == optionTarget,
            "v4: variant target responses expose GUID-only group and option IDs");

        group["Name"] = "Renamed";
        group["Options"] = new JsonArray(group["Options"]![1]!.DeepClone(), group["Options"]![0]!.DeepClone());
        File.WriteAllText(metaPath, meta.ToJsonString());
        Require(PenumbraService.ResolveVariantOptionPathForRegression(root, GamePath, optionTarget)?
                    .EndsWith(Path.Combine("Files", "selected.mdl"), StringComparison.OrdinalIgnoreCase) == true,
            "v4: option target survives group and option renaming/reordering with duplicate names");
        Require(PenumbraService.ResolveVariantOptionPathForRegression(root, GamePath,
                    $"option:{groupId:D}:{Guid.NewGuid():D}") is null,
            "v4: stale option GUIDs are rejected");
    }

    private static void UnsupportedMetadataIsRejected(string root)
    {
        foreach (var version in new[] { 3, 5 })
        {
            var folder = Path.Combine(root, $"v{version}");
            Directory.CreateDirectory(folder);
            var metaPath = Path.Combine(folder, "meta.json");
            File.WriteAllText(metaPath, new JsonObject { ["FileVersion"] = version, ["Groups"] = new JsonArray() }.ToJsonString());
            var before = File.ReadAllText(metaPath);
            var error = PenumbraService.PrepareVariantGroup(folder, GamePath, "Files/new.mdl", "New", "Group", out var prepared);
            Require(prepared is null && error?.Contains("Penumbra 1.7.1.0", StringComparison.Ordinal) == true &&
                    File.ReadAllText(metaPath) == before,
                $"v{version}: unsupported metadata fails actionably before mutation");
        }
    }

    private static void AttributeGroupsAreGeneratedAndUpdated(string root)
    {
        Directory.CreateDirectory(root);
        var metaPath = Path.Combine(root, "meta.json");
        var requestedName = "XIV Instant Edit c0101e0001_top Parts (IMC)";
        var userGroup = Group(Guid.NewGuid(), requestedName);
        userGroup["CustomMetadata"] = new JsonObject { ["Keep"] = "user" };
        var meta = Metadata(userGroup);
        var unrelatedDefaultManipulation = new JsonObject
        {
            ["Type"] = "Eqp",
            ["Manipulation"] = new JsonObject { ["SetId"] = 1, ["Slot"] = "Body", ["Entry"] = 7 },
        };
        meta["DefaultData"] = new JsonObject
        {
            ["Files"] = new JsonObject(),
            ["FileSwaps"] = new JsonObject(),
            ["Manipulations"] = new JsonArray(unrelatedDefaultManipulation),
        };
        meta["CustomRootMetadata"] = new JsonObject { ["Keep"] = true };
        File.WriteAllText(metaPath, meta.ToJsonString());

        var path = "chara/equipment/e0001/model/c0101e0001_top.mdl";
        var masks = new Dictionary<string, int>
        {
            // These reproduce MDL-table-position masks from an older add-on.
            // The plugin must canonicalize them from the suffix instead.
            ["atr_tv_a"] = 16,
            ["atr_gv_a"] = 64,
            ["atr_tv_c"] = 32,
            ["atr_tv_b"] = 8,
        };
        var tags = new[] { "atr_tv_a", "atr_gv_a", "atr_tv_c", "atr_tv_b", "atrx_cape" };
        var sourceImc = new JsonArray(new JsonObject
        {
            ["Type"] = "Imc",
            ["Manipulation"] = new JsonObject
            {
                ["ObjectType"] = "Equipment",
                ["PrimaryId"] = 1,
                ["Variant"] = 1,
                ["EquipSlot"] = "Body",
                ["Entry"] = new JsonObject
                {
                    ["MaterialId"] = 3,
                    ["DecalId"] = 2,
                    ["VfxId"] = 5,
                    ["MaterialAnimationId"] = 4,
                    ["AttributeMask"] = 511,
                    ["SoundId"] = 2,
                },
            },
        });
        var beforeMissingImc = File.ReadAllText(metaPath);
        var missingImc = PenumbraService.WriteAttributeGroupsForRegression(root, path, tags, masks);
        Require(missingImc?.Contains("attribute_group_imc_unavailable", StringComparison.Ordinal) == true &&
                File.ReadAllText(metaPath) == beforeMissingImc,
            "attribute groups: a missing source IMC entry is rejected instead of guessing a material ID");
        Require(PenumbraService.WriteAttributeGroupsForRegression(root, path, tags, masks, sourceImc) is null,
            "attribute groups: standard and custom groups are generated together");

        var written = JsonNode.Parse(File.ReadAllText(metaPath))!.AsObject();
        var groups = written["Groups"]!.AsArray().OfType<JsonObject>().ToArray();
        var imc = groups.Single(group => group["Type"]?.GetValue<string>() == "Imc");
        var atr = groups.Single(group => group["Type"]?.GetValue<string>() == "Multi");
        Require(imc["Name"]!.GetValue<string>() == requestedName + " (2)" &&
                imc["AllVariants"]!.GetValue<bool>() && imc["OnlyAttributes"]!.GetValue<bool>() &&
                imc["Identifier"]!["PrimaryId"]!.GetValue<int>() == 1 &&
                imc["Identifier"]!["ObjectType"]!.GetValue<string>() == "Equipment" &&
                imc["Identifier"]!["EquipSlot"]!.GetValue<string>() == "Body" &&
                imc["Identifier"]!["BodySlot"]!.GetValue<string>() == "Unknown" &&
                imc["Options"]!.AsArray().Count == 3 &&
                imc["Options"]!.AsArray().Single(option => option!["Name"]!.GetValue<string>() == "A")!["AttributeMask"]!.GetValue<int>() == 1 &&
                imc["Options"]!.AsArray().Single(option => option!["Name"]!.GetValue<string>() == "B")!["AttributeMask"]!.GetValue<int>() == 2 &&
                imc["Options"]!.AsArray().Single(option => option!["Name"]!.GetValue<string>() == "C")!["AttributeMask"]!.GetValue<int>() == 4 &&
                imc["DefaultEntry"]!["MaterialId"]!.GetValue<int>() == 3 &&
                imc["DefaultEntry"]!["DecalId"]!.GetValue<int>() == 2 &&
                imc["DefaultEntry"]!["VfxId"]!.GetValue<int>() == 5 &&
                imc["DefaultEntry"]!["MaterialAnimationId"]!.GetValue<int>() == 4 &&
                imc["DefaultEntry"]!["AttributeMask"]!.GetValue<int>() == 0 &&
                imc["DefaultEntry"]!["SoundId"]!.GetValue<int>() == 2 &&
                imc["DefaultSettings"]!.GetValue<int>() == 7,
            "attribute groups: IMC identity, captured entry, flags, and canonical suffix masks are correct");
        var manipulation = atr["Options"]!.AsArray().Single()!["Manipulations"]!.AsArray().Single()!;
        var defaultManipulations = written["DefaultData"]!["Manipulations"]!.AsArray();
        var defaultAtr = defaultManipulations.OfType<JsonObject>().Single(item =>
            item["Type"]?.GetValue<string>() == "Atr");
        Require(atr["Options"]!.AsArray().Count == 1 &&
                manipulation["Type"]!.GetValue<string>() == "Atr" &&
                manipulation["Manipulation"]!["Entry"]!.GetValue<bool>() &&
                manipulation["Manipulation"]!["Attribute"]!.GetValue<string>() == "atrx_cape" &&
                manipulation["Manipulation"]!["Slot"]!.GetValue<string>() == "Body" &&
                manipulation["Manipulation"]!["Id"]!.GetValue<int>() == 1 &&
                manipulation["Manipulation"]!["GenderRaceCondition"]!.GetValue<int>() == 0 &&
                defaultManipulations.Count == 2 &&
                !defaultAtr["Manipulation"]!["Entry"]!.GetValue<bool>() &&
                defaultAtr["Manipulation"]!["Attribute"]!.GetValue<string>() == "atrx_cape" &&
                defaultAtr["Manipulation"]!["GenderRaceCondition"]!.GetValue<int>() == 0,
            "attribute groups: default data disables atrx while the user-facing option enables it");
        Require(written["CustomRootMetadata"]!["Keep"]!.GetValue<bool>() &&
                groups.Single(group => group["Type"]?.GetValue<string>() == "Single")["CustomMetadata"]!["Keep"]!.GetValue<string>() == "user",
            "attribute groups: unrelated metadata and user groups survive generation");

        var imcId = imc["Id"]!.GetValue<string>();
        Require(PenumbraService.WriteAttributeGroupsForRegression(
                    root,
                    path,
                    ["atr_tv_a", "atr_tv_b", "atrx_cape"],
                    new Dictionary<string, int> { ["atr_tv_a"] = 1, ["atr_tv_b"] = 8 },
                    sourceImc) is null,
            "attribute groups: rerunning updates managed groups");
        var rerun = JsonNode.Parse(File.ReadAllText(metaPath))!["Groups"]!.AsArray().OfType<JsonObject>().ToArray();
        Require(rerun.Count(group => group["Type"]?.GetValue<string>() is "Imc" or "Multi") == 2 &&
                rerun.Single(group => group["Type"]?.GetValue<string>() == "Imc")["Id"]!.GetValue<string>() == imcId &&
                rerun.Single(group => group["Type"]?.GetValue<string>() == "Imc")["Options"]!.AsArray().Count == 2 &&
                JsonNode.Parse(File.ReadAllText(metaPath))!["DefaultData"]!["Manipulations"]!.AsArray()
                    .OfType<JsonObject>().Count(item => item["Type"]?.GetValue<string>() == "Atr") == 1,
            "attribute groups: managed group identity is stable across reruns");
        Require(PenumbraService.WriteAttributeGroupsForRegression(
                    root,
                    path,
                    ["atr_tv_a"],
                    new Dictionary<string, int> { ["atr_tv_a"] = 1 },
                    sourceImc) is null &&
                JsonNode.Parse(File.ReadAllText(metaPath))!["Groups"]!.AsArray()
                    .OfType<JsonObject>().All(group => group["Type"]?.GetValue<string>() != "Multi") &&
                JsonNode.Parse(File.ReadAllText(metaPath))!["DefaultData"]!["Manipulations"]!.AsArray()
                    .OfType<JsonObject>().All(item => item["Type"]?.GetValue<string>() != "Atr"),
            "attribute groups: removed custom tags remove the managed ATR group and defaults");

        var conflictRoot = Path.Combine(root, "Conflict");
        Directory.CreateDirectory(conflictRoot);
        var conflictMetaPath = Path.Combine(conflictRoot, "meta.json");
        File.WriteAllText(conflictMetaPath, Metadata(UserAtrGroup("Body", 1)).ToJsonString());
        var before = File.ReadAllText(conflictMetaPath);
        var conflict = PenumbraService.WriteAttributeGroupsForRegression(
            conflictRoot, path, ["atrx_new"], new Dictionary<string, int>());
        Require(conflict?.Contains("conflict", StringComparison.OrdinalIgnoreCase) == true &&
                File.ReadAllText(conflictMetaPath) == before,
            "attribute groups: unmanaged exact-identity ATR conflicts are rejected without mutation");

        // Penumbra cannot load an IMC group for hair or faces (the game has no IMC
        // file for them), so part variants on them never ask for an IMC entry.
        const string hairPath = "chara/human/c0801/obj/hair/h0154/model/c0801h0154_hir.mdl";
        var hairRoot = Path.Combine(root, "Hair");
        Directory.CreateDirectory(hairRoot);
        File.WriteAllText(Path.Combine(hairRoot, "meta.json"), Metadata(new JsonObject
        {
            ["Id"] = Guid.NewGuid(), ["Name"] = "XIV Instant Edit c0801h0154_hir Parts (IMC)", ["Type"] = "Imc",
            ["Description"] = $"Managed by XIV Instant Edit attribute group v1: imc|{hairPath}",
            ["Identifier"] = new JsonObject
            {
                ["PrimaryId"] = 154, ["SecondaryId"] = 0, ["Variant"] = 1,
                ["ObjectType"] = "Character", ["EquipSlot"] = "Nothing", ["BodySlot"] = "Hair",
            },
            ["DefaultEntry"] = new JsonObject { ["MaterialId"] = 6 },
            ["Options"] = new JsonArray(),
        }).ToJsonString());
        var hair = PenumbraService.WriteAttributeGroupsForRegression(
            hairRoot,
            hairPath,
            ["atr_hv_a", "atr_tv_b", "atr_kam", "atr_bak", "atr_fv_e", "atrx_bangs"],
            new Dictionary<string, int> { ["atr_hv_a"] = 1, ["atr_tv_b"] = 2 });
        var hairMeta = JsonNode.Parse(File.ReadAllText(Path.Combine(hairRoot, "meta.json")))!;
        Require(hair is null &&
                hairMeta["Groups"]!.AsArray().OfType<JsonObject>()
                    .Select(group => group["Type"]?.GetValue<string>()).SequenceEqual(["Multi"]),
            "attribute groups: hair variants and vanilla hair attributes need no IMC entry, and an old managed IMC group is removed");
        Require(AtrManipulations(hairMeta).Count() == 2 &&
                AtrManipulations(hairMeta).All(atr =>
                    atr["Slot"]!.GetValue<string>() == "Hair" && atr["Id"]!.GetValue<int>() == 154 &&
                    atr["Attribute"]!.GetValue<string>() == "atrx_bangs"),
            "attribute groups: hair atrx toggles target Penumbra's Hair slot, not the invalid Unknown slot");

        var faceRoot = Path.Combine(root, "Face");
        Directory.CreateDirectory(faceRoot);
        File.WriteAllText(Path.Combine(faceRoot, "meta.json"), Metadata().ToJsonString());
        Require(PenumbraService.WriteAttributeGroupsForRegression(
                    faceRoot,
                    "chara/human/c0101/obj/face/f0001/model/c0101f0001_fac.mdl",
                    ["atr_fv_a", "atr_fv_g", "atr_mim", "atr_kao", "atrx_scar"],
                    new Dictionary<string, int>()) is null,
            "attribute groups: face toggles and vanilla face attributes are accepted next to atrx toggles");
        var faceMeta = JsonNode.Parse(File.ReadAllText(Path.Combine(faceRoot, "meta.json")))!;
        Require(AtrManipulations(faceMeta).Count() == 2 &&
                AtrManipulations(faceMeta).All(atr =>
                    atr["Slot"]!.GetValue<string>() == "Face" && atr["Id"]!.GetValue<int>() == 1),
            "attribute groups: face atrx toggles target Penumbra's Face slot");

        var hairConflictRoot = Path.Combine(root, "HairConflict");
        Directory.CreateDirectory(hairConflictRoot);
        var hairConflictMetaPath = Path.Combine(hairConflictRoot, "meta.json");
        File.WriteAllText(hairConflictMetaPath, Metadata(UserAtrGroup("Hair", 154)).ToJsonString());
        var hairConflictBefore = File.ReadAllText(hairConflictMetaPath);
        Require(PenumbraService.WriteAttributeGroupsForRegression(
                    hairConflictRoot, hairPath, ["atrx_bangs"], new Dictionary<string, int>())?
                    .Contains("conflict", StringComparison.OrdinalIgnoreCase) == true &&
                File.ReadAllText(hairConflictMetaPath) == hairConflictBefore,
            "attribute groups: unmanaged Hair-slot ATR toggles for the same hair ID are conflicts");

        // Exports before 2.0.0 wrote Slot "Unknown" for hair and face; Penumbra dropped those as invalid.
        var legacyRoot = Path.Combine(root, "LegacyHair");
        Directory.CreateDirectory(legacyRoot);
        var legacyMetaPath = Path.Combine(legacyRoot, "meta.json");
        var legacyMeta = Metadata(new JsonObject
        {
            ["Id"] = Guid.NewGuid(), ["Name"] = "XIV Instant Edit c0801h0154_hir Parts (ATR)", ["Type"] = "Multi",
            ["Description"] = $"Managed by XIV Instant Edit attribute group v1: atr|{hairPath}",
            ["Priority"] = 1,
            ["Options"] = new JsonArray(new JsonObject
            {
                ["Id"] = Guid.NewGuid(), ["Name"] = "atrx_bangs",
                ["Manipulations"] = new JsonArray(LegacyHairAtr("atrx_bangs", true)),
            }),
        });
        legacyMeta["DefaultData"] = new JsonObject
        {
            ["Files"] = new JsonObject(),
            ["FileSwaps"] = new JsonObject(),
            ["Manipulations"] = new JsonArray(LegacyHairAtr("atrx_bangs", false)),
        };
        File.WriteAllText(legacyMetaPath, legacyMeta.ToJsonString());
        Require(PenumbraService.WriteAttributeGroupsForRegression(
                    legacyRoot, hairPath, ["atrx_bangs"], new Dictionary<string, int>()) is null,
            "attribute groups: a legacy Unknown-slot hair export is not treated as a conflict");
        var migrated = JsonNode.Parse(File.ReadAllText(legacyMetaPath))!;
        Require(migrated["Groups"]!.AsArray().Count == 1 &&
                AtrManipulations(migrated).Count() == 2 &&
                AtrManipulations(migrated).All(atr => atr["Slot"]!.GetValue<string>() == "Hair") &&
                !migrated["DefaultData"]!["Manipulations"]![0]!["Manipulation"]!["Entry"]!.GetValue<bool>(),
            "attribute groups: re-exporting replaces legacy Unknown-slot hair toggles and defaults");

        var captured = PenumbraService.ManipulationsWithAtrDefaults(
            new JsonArray(LegacyHairAtr("atrx_bangs", true), LegacyHairAtr("atrx_other", false)),
            hairPath,
            ["atrx_bangs"]);
        var capturedAtr = captured.OfType<JsonObject>().Select(item => item["Manipulation"]!).ToArray();
        Require(capturedAtr.Length == 3 &&
                capturedAtr[0]["Slot"]!.GetValue<string>() == "Hair" && capturedAtr[0]["Entry"]!.GetValue<bool>() &&
                capturedAtr[1]["Slot"]!.GetValue<string>() == "Unknown" &&
                capturedAtr[2]["Slot"]!.GetValue<string>() == "Hair" && !capturedAtr[2]["Entry"]!.GetValue<bool>(),
            "attribute groups: mashups carry legacy hair toggles for exported tags over to the Hair slot");

        var gameAttributesRoot = Path.Combine(root, "GameAttributesOnGear");
        Directory.CreateDirectory(gameAttributesRoot);
        File.WriteAllText(Path.Combine(gameAttributesRoot, "meta.json"), Metadata().ToJsonString());
        Require(PenumbraService.WriteAttributeGroupsForRegression(
                    gameAttributesRoot, path, ["atr_hv_a", "atr_fv_b", "atr_nek", "atr_vsr", "atr_lod117"],
                    new Dictionary<string, int> { ["atr_hv_a"] = 1 }) is null &&
                (JsonNode.Parse(File.ReadAllText(Path.Combine(gameAttributesRoot, "meta.json")))!["Groups"]
                    as JsonArray)?.Count is null or 0,
            "attribute groups: the game's own attributes, sent by older add-ons too, are accepted without a group");

        var unknownRoot = Path.Combine(root, "UnknownAttribute");
        Directory.CreateDirectory(unknownRoot);
        File.WriteAllText(Path.Combine(unknownRoot, "meta.json"), Metadata().ToJsonString());
        var unknownBefore = File.ReadAllText(Path.Combine(unknownRoot, "meta.json"));
        Require(PenumbraService.WriteAttributeGroupsForRegression(
                    unknownRoot, path, ["atr_cape"], new Dictionary<string, int>())?
                    .Contains("must use the atrx_ prefix", StringComparison.Ordinal) == true &&
                File.ReadAllText(Path.Combine(unknownRoot, "meta.json")) == unknownBefore,
            "attribute groups: a custom attribute without the atrx_ prefix is still refused");

        var tenSuffixRoot = Path.Combine(root, "TenSuffixes");
        Directory.CreateDirectory(tenSuffixRoot);
        File.WriteAllText(Path.Combine(tenSuffixRoot, "meta.json"), Metadata().ToJsonString());
        Require(PenumbraService.WriteAttributeGroupsForRegression(
                    tenSuffixRoot, path, ["atr_tv_a", "atr_tv_i", "atr_tv_j"],
                    new Dictionary<string, int> { ["atr_tv_a"] = 1, ["atr_tv_i"] = 256, ["atr_tv_j"] = 512 },
                    sourceImc) is null,
            "attribute groups: _i and _j part variants are accepted");
        var tenSuffixGroup = JsonNode.Parse(File.ReadAllText(Path.Combine(tenSuffixRoot, "meta.json")))!["Groups"]!
            .AsArray().OfType<JsonObject>().Single(group => group["Type"]?.GetValue<string>() == "Imc");
        Require(tenSuffixGroup["Options"]!.AsArray()
                    .Select(option => (option!["Name"]!.GetValue<string>(), option["AttributeMask"]!.GetValue<int>()))
                    .SequenceEqual([("A", 1), ("I", 256), ("J", 512)]) &&
                tenSuffixGroup["DefaultSettings"]!.GetValue<int>() == 7,
            "attribute groups: _i and _j use IMC attribute bits 8 and 9");
    }

    private static IEnumerable<JsonObject> AtrManipulations(JsonNode meta)
        => meta["Groups"]!.AsArray()
            .SelectMany(group => group!["Options"]?.AsArray() ?? [])
            .SelectMany(option => option!["Manipulations"]?.AsArray() ?? [])
            .Concat(meta["DefaultData"]?["Manipulations"]?.AsArray() ?? [])
            .OfType<JsonObject>()
            .Where(item => item["Type"]?.GetValue<string>() == "Atr")
            .Select(item => item["Manipulation"]!.AsObject());

    private static JsonObject UserAtrGroup(string slot, int id) => new()
    {
        ["Id"] = Guid.NewGuid(), ["Name"] = "User ATR", ["Type"] = "Multi",
        ["Options"] = new JsonArray(new JsonObject
        {
            ["Name"] = "Existing",
            ["Manipulations"] = new JsonArray(new JsonObject
            {
                ["Type"] = "Atr",
                ["Manipulation"] = new JsonObject
                {
                    ["Attribute"] = "atrx_existing", ["Entry"] = true,
                    ["Slot"] = slot, ["Id"] = id, ["GenderRaceCondition"] = 801,
                },
            }),
        }),
    };

    private static JsonObject LegacyHairAtr(string attribute, bool entry) => new()
    {
        ["Type"] = "Atr",
        ["Manipulation"] = new JsonObject
        {
            ["Entry"] = entry, ["Attribute"] = attribute,
            ["Slot"] = "Unknown", ["Id"] = 154, ["GenderRaceCondition"] = 0,
        },
    };

    private static void ExportReceiptsMatchOperation()
    {
        using var registry = new ExportContextRegistry("operation-regression");
        var context = registry.CreateGameContext(GamePath, GamePath, 0, 42428);
        var original = new ExportServer.ExportRequest
        {
            Schema = "instant-edit.export", Version = 3, VariantName = "Variant",
            VariantGroupName = "Variants", VariantTarget = "group", VariantTargetId = "group:original",
            SetupInPenumbra = true,
            CreateAttributeGroups = true,
            AttributeTags = ["atr_tv_a"],
            AttributeMasks = new Dictionary<string, int> { ["atr_tv_a"] = 1 },
        };
        var fingerprint = ExportServer.ExportRequestFingerprint(original);
        const string exportId = "operation-export";
        const string file = "C:/Temp/export.mdl";
        var hash = new string('a', 64);
        Require(registry.TryBeginExport(registry.PluginInstanceId, context.ContextId, exportId, context.Capability,
            file, 3, hash, out var owner, out _, fingerprint) && owner!.IsOwner, "normal export reserves its complete operation");
        var receipt = new ExportReceipt(true, "complete", "complete");
        registry.CompleteExport(context.ContextId, exportId, receipt);
        Require(registry.TryBeginExport(registry.PluginInstanceId, context.ContextId, exportId, context.Capability,
            file, 3, hash, out var duplicate, out _, fingerprint) && !duplicate!.IsOwner &&
            duplicate.Completion.Result == receipt, "exact normal export retry returns the original receipt");
        var changes = new Dictionary<string, Action<ExportServer.ExportRequest>>
        {
            ["schema"] = request => request.Schema = "different",
            ["version"] = request => request.Version = 2,
            ["variant name"] = request => request.VariantName = "Other",
            ["group name"] = request => request.VariantGroupName = "Other",
            ["target kind"] = request => request.VariantTarget = "new_group",
            ["target ID"] = request => request.VariantTargetId = "group:other",
            ["setup"] = request => request.SetupInPenumbra = false,
            ["backup"] = request => request.BackupExisting = true,
            ["new mod"] = request => request.NewModName = "New mod",
            ["attribute toggle"] = request => request.CreateAttributeGroups = false,
            ["attribute tags"] = request => request.AttributeTags = ["atr_tv_b"],
            ["attribute masks"] = request => request.AttributeMasks = new Dictionary<string, int> { ["atr_tv_a"] = 2 },
            ["EST entries"] = request => request.EstEntries = [new EstEntryRequest { Slot = "Hair", Entry = 160, Race = "c0801" }],
        };
        foreach (var (name, change) in changes)
        {
            var changed = JsonSerializer.Deserialize<ExportServer.ExportRequest>(JsonSerializer.Serialize(original))!;
            change(changed);
            Require(!registry.TryBeginExport(registry.PluginInstanceId, context.ContextId, exportId, context.Capability,
                file, 3, hash, out _, out var code, ExportServer.ExportRequestFingerprint(changed)) && code == "duplicate_export_id",
                $"changed {name} cannot reuse an export receipt");
        }
    }
}
