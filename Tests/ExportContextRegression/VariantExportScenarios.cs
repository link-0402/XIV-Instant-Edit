using System.Text.Json;
using System.Text.Json.Nodes;
using InstantEdit.Models;
using InstantEdit.Services;

internal static class VariantExportScenarios
{
    private const string GamePath = "chara/equipment/e0001/model/c0101e0001_top.mdl";

    public static void Run(string testRoot)
    {
        foreach (var version in new[] { 3, 4 })
        {
            SelectedGroupKeepsIdentity(Path.Combine(testRoot, $"SelectedGroupV{version}"), version);
            NewGroupUsesName(Path.Combine(testRoot, $"NewGroupV{version}"), version);
        }
        ExportReceiptsMatchOperation();
    }

    private static void NewGroupUsesName(string root, int version)
    {
        Directory.CreateDirectory(root);
        var metaPath = Path.Combine(root, "meta.json");
        File.WriteAllText(metaPath, new JsonObject { ["FileVersion"] = version }.ToJsonString());
        for (var option = 1; option <= 2; option++)
        {
            Require(PenumbraService.PrepareVariantGroup(root, GamePath, $"Files/new{option}.mdl",
                    $"New {option}", "New Group", out var prepared) is null &&
                    PenumbraService.CommitVariantGroup(prepared!) is null,
                $"v{version}: New Group creates or reuses a compatible name");
        }
        var meta = JsonNode.Parse(File.ReadAllText(metaPath))!;
        var group = version == 4 ? meta["Groups"]!.AsArray().Single()!
            : JsonNode.Parse(File.ReadAllText(Directory.GetFiles(root, "group_*.json").Single()))!;
        Require(group["Options"]!.AsArray().Count == 3 && group["DefaultSettings"]!.GetValue<int>() == 2,
            $"v{version}: name-based reuse retains both new options and None");
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

    private static void SelectedGroupKeepsIdentity(string root, int version)
    {
        Directory.CreateDirectory(root);
        var selectedId = Guid.NewGuid();
        var selected = Group(selectedId, "Renamed");
        var sibling = Group(Guid.NewGuid(), "Renamed");
        var oldName = Group(Guid.NewGuid(), "Cached Name");
        var metaPath = Path.Combine(root, "meta.json");
        var selectedPath = Path.Combine(root, "group_001_selected.json");
        var meta = new JsonObject { ["FileVersion"] = version, ["Name"] = "User mod" };
        if (version == 4)
            meta["Groups"] = new JsonArray(selected, sibling, oldName);
        else
        {
            File.WriteAllText(selectedPath, selected.ToJsonString());
            File.WriteAllText(Path.Combine(root, "group_002_sibling.json"), sibling.ToJsonString());
            File.WriteAllText(Path.Combine(root, "group_003_cached.json"), oldName.ToJsonString());
        }
        File.WriteAllText(metaPath, meta.ToJsonString());
        var target = version == 4 ? $"group:{selectedId:D}" : "legacy-group:group_001_selected.json";
        var error = PenumbraService.PrepareVariantGroup(root, GamePath, "Files/new.mdl", "New",
            "Cached Name", out var prepared, targetId: target);
        Require(error is null && prepared is not null, $"v{version}: renamed group resolves by selected identity");
        Require(PenumbraService.CommitVariantGroup(prepared!) is null, $"v{version}: selected group commits");
        var writtenMeta = JsonNode.Parse(File.ReadAllText(metaPath))!;
        var written = version == 4 ? writtenMeta["Groups"]![0]! : JsonNode.Parse(File.ReadAllText(selectedPath))!;
        Require(written["Name"]!.GetValue<string>() == "Renamed" &&
                written["Description"]!.GetValue<string>() == "User description" &&
                written["Priority"]!.GetValue<int>() == 42 &&
                written["CustomMetadata"]!["Keep"]!.GetValue<bool>() &&
                written["Options"]!.AsArray().Count == 2 &&
                written["Options"]![1]!["Files"]![GamePath]!.GetValue<string>() == "Files/new.mdl" &&
                written["DefaultSettings"]!.GetValue<int>() == 1,
            $"v{version}: group metadata survives adding and selecting the new option");
        Require(version == 4
                ? JsonNode.DeepEquals(writtenMeta["Groups"]![1], sibling) &&
                  JsonNode.DeepEquals(writtenMeta["Groups"]![2], oldName) && writtenMeta["Groups"]!.AsArray().Count == 3
                : Directory.GetFiles(root, "group_*.json").Length == 3 &&
                  JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(Path.Combine(root, "group_002_sibling.json"))), sibling) &&
                  JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(Path.Combine(root, "group_003_cached.json"))), oldName),
            $"v{version}: duplicate and cached names cannot redirect the selected write");

        foreach (var stale in new[] { "missing", "incompatible" })
        {
            if (stale == "incompatible")
            {
                written["Type"] = "Multi";
                File.WriteAllText(version == 4 ? metaPath : selectedPath,
                    (version == 4 ? writtenMeta : written).ToJsonString());
            }
            var before = Directory.GetFiles(root).ToDictionary(path => path, File.ReadAllText);
            var staleId = stale == "missing"
                ? version == 4 ? $"group:{Guid.NewGuid():D}" : "legacy-group:group_999_missing.json"
                : target;
            Require(PenumbraService.PrepareVariantGroup(root, GamePath, "Files/new.mdl", "New",
                    "Cached Name", out var rejected, targetId: staleId) is not null && rejected is null &&
                    before.All(pair => File.ReadAllText(pair.Key) == pair.Value),
                $"v{version}: {stale} group is rejected without filesystem changes");
        }
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
