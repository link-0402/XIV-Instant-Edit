using System.Text.Json;
using System.Text.Json.Nodes;
using InstantEdit.Models;
using InstantEdit.Services;

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

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
        Console.WriteLine($"[PASS] {message}");
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
        meta["CustomRootMetadata"] = new JsonObject { ["Keep"] = true };
        File.WriteAllText(metaPath, meta.ToJsonString());

        var path = "chara/equipment/e0001/model/c0101e0001_top.mdl";
        var masks = new Dictionary<string, int>
        {
            ["atr_tv_a"] = 1,
            ["atr_gv_a"] = 2,
            ["atr_tv_c"] = 4,
        };
        var tags = new[] { "atr_tv_a", "atr_gv_a", "atr_tv_c", "atrx_cape" };
        Require(PenumbraService.WriteAttributeGroupsForRegression(root, path, tags, masks) is null,
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
                imc["Options"]!.AsArray().Count == 2 &&
                imc["Options"]!.AsArray().Single(option => option!["Name"]!.GetValue<string>() == "A")!["AttributeMask"]!.GetValue<int>() == 3 &&
                imc["DefaultEntry"]!["AttributeMask"]!.GetValue<int>() == 7,
            "attribute groups: IMC identity and suffix masks match the exported model");
        var manipulation = atr["Options"]!.AsArray().Single()!["Manipulations"]!.AsArray().Single()!;
        Require(atr["Options"]!.AsArray().Count == 1 &&
                manipulation["Type"]!.GetValue<string>() == "Atr" &&
                manipulation["Manipulation"]!["Entry"]!.GetValue<bool>() &&
                manipulation["Manipulation"]!["Attribute"]!.GetValue<string>() == "atrx_cape" &&
                manipulation["Manipulation"]!["Slot"]!.GetValue<string>() == "Body" &&
                manipulation["Manipulation"]!["Id"]!.GetValue<int>() == 1 &&
                manipulation["Manipulation"]!["GenderRaceCondition"]!.GetValue<int>() == 101,
            "attribute groups: custom ATR uses the exact model, slot, and race identity");
        Require(written["CustomRootMetadata"]!["Keep"]!.GetValue<bool>() &&
                groups.Single(group => group["Type"]?.GetValue<string>() == "Single")["CustomMetadata"]!["Keep"]!.GetValue<string>() == "user",
            "attribute groups: unrelated metadata and user groups survive generation");

        var imcId = imc["Id"]!.GetValue<string>();
        Require(PenumbraService.WriteAttributeGroupsForRegression(
                    root,
                    path,
                    ["atr_tv_a", "atr_tv_b", "atrx_cape"],
                    new Dictionary<string, int> { ["atr_tv_a"] = 1, ["atr_tv_b"] = 8 }) is null,
            "attribute groups: rerunning updates managed groups");
        var rerun = JsonNode.Parse(File.ReadAllText(metaPath))!["Groups"]!.AsArray().OfType<JsonObject>().ToArray();
        Require(rerun.Count(group => group["Type"]?.GetValue<string>() is "Imc" or "Multi") == 2 &&
                rerun.Single(group => group["Type"]?.GetValue<string>() == "Imc")["Id"]!.GetValue<string>() == imcId &&
                rerun.Single(group => group["Type"]?.GetValue<string>() == "Imc")["Options"]!.AsArray().Count == 2,
            "attribute groups: managed group identity is stable across reruns");
        Require(PenumbraService.WriteAttributeGroupsForRegression(
                    root,
                    path,
                    ["atr_tv_a"],
                    new Dictionary<string, int> { ["atr_tv_a"] = 1 }) is null &&
                JsonNode.Parse(File.ReadAllText(metaPath))!["Groups"]!.AsArray()
                    .OfType<JsonObject>().All(group => group["Type"]?.GetValue<string>() != "Multi"),
            "attribute groups: removed custom tags remove only the managed ATR group");

        var conflictRoot = Path.Combine(root, "Conflict");
        Directory.CreateDirectory(conflictRoot);
        var conflictMetaPath = Path.Combine(conflictRoot, "meta.json");
        var conflictMeta = Metadata(new JsonObject
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
                        ["Slot"] = "Body", ["Id"] = 1, ["GenderRaceCondition"] = 101,
                    },
                }),
            }),
        });
        File.WriteAllText(conflictMetaPath, conflictMeta.ToJsonString());
        var before = File.ReadAllText(conflictMetaPath);
        var conflict = PenumbraService.WriteAttributeGroupsForRegression(
            conflictRoot, path, ["atrx_new"], new Dictionary<string, int>());
        Require(conflict?.Contains("conflict", StringComparison.OrdinalIgnoreCase) == true &&
                File.ReadAllText(conflictMetaPath) == before,
            "attribute groups: unmanaged exact-identity ATR conflicts are rejected without mutation");

        var hairRoot = Path.Combine(root, "Hair");
        Directory.CreateDirectory(hairRoot);
        File.WriteAllText(Path.Combine(hairRoot, "meta.json"), Metadata().ToJsonString());
        var hair = PenumbraService.WriteAttributeGroupsForRegression(
            hairRoot,
            "chara/human/c0801/obj/hair/h0154/model/c0801h0154_hir.mdl",
            ["atr_hv_a"],
            new Dictionary<string, int> { ["atr_hv_a"] = 1 });
        var hairGroup = JsonNode.Parse(File.ReadAllText(Path.Combine(hairRoot, "meta.json")))!["Groups"]!.AsArray()[0]!;
        Require(hair is null && hairGroup!["Identifier"]!["ObjectType"]!.GetValue<string>() == "Character" &&
                hairGroup["Identifier"]!["BodySlot"]!.GetValue<string>() == "Hair" &&
                hairGroup["Identifier"]!["EquipSlot"]!.GetValue<string>() == "Nothing" &&
                hairGroup["Identifier"]!["PrimaryId"]!.GetValue<int>() == 154,
            "attribute groups: hair paths use character/hair IMC identity");
    }

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
