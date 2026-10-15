using System.Text.Json;
using System.Text.Json.Nodes;
using InstantEdit.Models;
using InstantEdit.Services;
using static InstantEdit.TestSupport.Assertions;
using InstantEdit.Services.PenumbraMods;

/// <summary>Quick Export at Magic Fit's sizes: a group with an option per size, and the request.</summary>
internal static class SizesExportScenarios
{
    private const string GamePath = "chara/equipment/e0001/model/c0101e0001_top.mdl";

    public static void Run(string testRoot)
    {
        NewGroupGetsAnOptionPerSize(Path.Combine(testRoot, "SizesNewGroup"));
        SelectedGroupKeepsItsFiles(Path.Combine(testRoot, "SizesSelectedGroup"));
        FilesTheModUsesAreNotTaken(Path.Combine(testRoot, "SizesUsedFiles"));
        SharedOptionFilesAreNotReused(Path.Combine(testRoot, "SizesSharedFiles"));
        UnusableGroupsAndNamesAreRefused(Path.Combine(testRoot, "SizesRefused"));
        RequestsAreValidated();
        ReceiptsMatchTheWholeRequest();
    }

    private static JsonObject Metadata(JsonObject? defaultData = null, params JsonObject[] groups) => new()
    {
        ["FileVersion"] = 4,
        ["Identifier"] = Guid.NewGuid(),
        ["LastWrite"] = DateTime.UtcNow.AddMinutes(-1),
        ["Name"] = "User mod",
        ["DefaultData"] = defaultData,
        ["Groups"] = groups.Length == 0 ? null : new JsonArray(groups),
    };

    private static string ModelFile(string root) => Path.Combine(root, "Files", "top.mdl");

    private static JsonNode Plan(string root, string[] sizes, string groupName, string defaultSize,
        out SizesExportService.SizeGroupPlan plan, string? targetId = null, JsonObject? sourceOption = null)
    {
        var error = SizesExportService.PlanSizeGroup(root, GamePath, ModelFile(root), sizes, groupName, targetId,
            defaultSize, sourceOption, out var planned);
        Require(error is null && planned is not null, $"sizes: {string.Join(", ", sizes)} planned ({error})");
        Require(ModOptionGroups.CommitVariantGroup(planned!.Group) is null, "sizes: the group commits");
        plan = planned;
        return JsonNode.Parse(File.ReadAllText(Path.Combine(root, "meta.json")))!;
    }

    private static string[] OptionNames(JsonNode group)
        => group["Options"]!.AsArray().Select(option => option!["Name"]!.GetValue<string>()).ToArray();

    private static string Mapped(JsonNode group, string option)
        => group["Options"]!.AsArray().Single(item => item!["Name"]!.GetValue<string>() == option)!
            ["Files"]![GamePath]!.GetValue<string>();

    private static void NewGroupGetsAnOptionPerSize(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "Files"));
        File.WriteAllText(Path.Combine(root, "meta.json"), Metadata().ToJsonString());
        var source = new JsonObject
        {
            ["Files"] = new JsonObject
            {
                [GamePath] = "Files/top.mdl",
                ["chara/equipment/e0001/material/v0001/mt_c0101e0001_top_a.mtrl"] = "Files/top_a.mtrl",
            },
            ["FileSwaps"] = new JsonObject { ["a"] = "b" },
        };
        var meta = Plan(root, ["Large", "Medium Rue", "Rue/Buff"], "Size", "Medium Rue", out var plan,
            sourceOption: source);
        var group = meta["Groups"]!.AsArray().Single()!;
        Require(OptionNames(group).SequenceEqual(["Large", "Medium Rue", "Rue/Buff"]) &&
                group["DefaultSettings"]!.GetValue<int>() == 1 && group["Type"]!.GetValue<string>() == "Single" &&
                group["Name"]!.GetValue<string>() == "Size" && plan.GroupName == "Size",
            "sizes: a new group gets an option per size and no None, the size given on by default");
        Require(Mapped(group, "Large") == "Files/c0101e0001_top Large.mdl" &&
                Mapped(group, "Medium Rue") == "Files/c0101e0001_top Medium Rue.mdl" &&
                Mapped(group, "Rue/Buff") == "Files/c0101e0001_top Rue-Buff.mdl" &&
                plan.Files.Select(file => file.RelativePath).SequenceEqual(
                    ["Files/c0101e0001_top Large.mdl", "Files/c0101e0001_top Medium Rue.mdl", "Files/c0101e0001_top Rue-Buff.mdl"]),
            "sizes: each size's file sits next to the model, named after the game file and the size");
        var large = group["Options"]![0]!;
        Require(large["Files"]!["chara/equipment/e0001/material/v0001/mt_c0101e0001_top_a.mtrl"]!.GetValue<string>() ==
                "Files/top_a.mtrl" && large["FileSwaps"]!["a"]!.GetValue<string>() == "b",
            "sizes: new options keep the rest of the option the model was imported from");

        // Exporting again, a size more: the same files, a new one for the new size.
        meta = Plan(root, ["Small", "Large", "Medium Rue", "Rue/Buff"], "size", "Large", out plan);
        group = meta["Groups"]!.AsArray().Single()!;
        Require(OptionNames(group).SequenceEqual(["Large", "Medium Rue", "Rue/Buff", "Small"]) &&
                group["DefaultSettings"]!.GetValue<int>() == 1 &&
                Mapped(group, "Large") == "Files/c0101e0001_top Large.mdl" &&
                Mapped(group, "Small") == "Files/c0101e0001_top Small.mdl" &&
                plan.Files[0].RelativePath == "Files/c0101e0001_top Small.mdl" &&
                plan.Replaced.SetEquals(["Files/c0101e0001_top Large.mdl", "Files/c0101e0001_top Medium Rue.mdl",
                    "Files/c0101e0001_top Rue-Buff.mdl"]),
            "sizes: exporting again reuses the group by name, keeps its default and each size's file, and adds the new size");
    }

    private static void SelectedGroupKeepsItsFiles(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "Files"));
        var groupId = Guid.NewGuid();
        var group = new JsonObject
        {
            ["Id"] = groupId,
            ["Name"] = "Body",
            ["Type"] = "Single",
            ["Priority"] = 7,
            ["DefaultSettings"] = 0,
            ["Options"] = new JsonArray(
                new JsonObject { ["Id"] = Guid.NewGuid(), ["Name"] = "None" },
                new JsonObject
                {
                    ["Id"] = Guid.NewGuid(), ["Name"] = "large",
                    ["Files"] = new JsonObject { [GamePath] = "Files/custom/large.mdl" },
                }),
        };
        File.WriteAllText(Path.Combine(root, "meta.json"), Metadata(null, group).ToJsonString());
        var meta = Plan(root, ["Large", "Small"], "Ignored", "Small", out var plan, $"group:{groupId:D}");
        var written = meta["Groups"]!.AsArray().Single()!;
        Require(written["Name"]!.GetValue<string>() == "Body" && plan.GroupName == "Body" &&
                written["Priority"]!.GetValue<int>() == 7 &&
                OptionNames(written).SequenceEqual(["None", "large", "Small"]) &&
                written["DefaultSettings"]!.GetValue<int>() == 0,
            "sizes: a selected group keeps its name, priority, default and other options");
        Require(Mapped(written, "large") == "Files/custom/large.mdl" &&
                Mapped(written, "Small") == "Files/c0101e0001_top Small.mdl",
            "sizes: a size the group has keeps its file; a new size gets one next to the model");
    }

    private static void FilesTheModUsesAreNotTaken(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "Files"));
        var used = Path.Combine(root, "Files", "c0101e0001_top Medium.mdl");
        var orphan = Path.Combine(root, "Files", "c0101e0001_top Small.mdl");
        File.WriteAllText(used, "used");
        File.WriteAllText(orphan, "left over");
        var defaultData = new JsonObject
        {
            ["Files"] = new JsonObject
            {
                ["chara/equipment/e0001/model/c0101e0001_dwn.mdl"] = "Files/c0101e0001_top Medium.mdl",
                ["chara/equipment/e0001/model/c0101e0001_glv.mdl"] = "Files/c0101e0001_top Large.mdl",
            },
        };
        File.WriteAllText(Path.Combine(root, "meta.json"), Metadata(defaultData).ToJsonString());
        var meta = Plan(root, ["Medium", "Small", "Large"], "Size", "Medium", out _);
        var group = meta["Groups"]!.AsArray().Single()!;
        Require(Mapped(group, "Medium") == "Files/c0101e0001_top Medium 2.mdl" &&
                Mapped(group, "Large") == "Files/c0101e0001_top Large 2.mdl",
            "sizes: files the mod maps elsewhere, there or not, are never taken");
        Require(Mapped(group, "Small") == "Files/c0101e0001_top Small 2.mdl" && File.ReadAllText(orphan) == "left over",
            "sizes: a file nothing maps but that is there (an alternative or a leftover) is never taken");
    }

    private static void SharedOptionFilesAreNotReused(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "Files"));
        // The size option's file is also another group's model: exporting the size must not change that one.
        var size = new JsonObject
        {
            ["Id"] = Guid.NewGuid(), ["Name"] = "Size", ["Type"] = "Single", ["DefaultSettings"] = 1,
            ["Options"] = new JsonArray(
                new JsonObject { ["Id"] = Guid.NewGuid(), ["Name"] = "Large", ["Files"] = new JsonObject { [GamePath] = "Files/big.mdl" } },
                new JsonObject { ["Id"] = Guid.NewGuid(), ["Name"] = "Small", ["Files"] = new JsonObject { [GamePath] = "Files/small.mdl" } }),
        };
        var other = new JsonObject
        {
            ["Id"] = Guid.NewGuid(), ["Name"] = "Outfit", ["Type"] = "Single",
            ["Options"] = new JsonArray(new JsonObject
            {
                ["Id"] = Guid.NewGuid(), ["Name"] = "Bulky", ["Files"] = new JsonObject { [GamePath] = "Files/big.mdl" },
            }),
        };
        File.WriteAllText(Path.Combine(root, "meta.json"), Metadata(null, size, other).ToJsonString());
        var meta = Plan(root, ["Large", "Small"], "Size", "Large", out var plan);
        var group = meta["Groups"]![0]!;
        Require(Mapped(group, "Large") == "Files/c0101e0001_top Large.mdl" && Mapped(group, "Small") == "Files/small.mdl" &&
                Mapped(meta["Groups"]![1]!, "Bulky") == "Files/big.mdl" &&
                plan.Replaced.SetEquals(["Files/small.mdl"]) && group["DefaultSettings"]!.GetValue<int>() == 1,
            "sizes: a size whose file another group maps gets a new file; the group's default stays the author's");
    }

    private static void UnusableGroupsAndNamesAreRefused(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "Files"));
        var metaPath = Path.Combine(root, "meta.json");
        var multi = new JsonObject
        {
            ["Id"] = Guid.NewGuid(), ["Name"] = "Size", ["Type"] = "Multi",
            ["Options"] = new JsonArray(new JsonObject
            {
                ["Id"] = Guid.NewGuid(), ["Name"] = "Large",
                ["Files"] = new JsonObject { [GamePath] = "Files/large.mdl" },
            }),
        };
        File.WriteAllText(metaPath, Metadata(null, multi).ToJsonString());
        var before = File.ReadAllText(metaPath);
        foreach (var (sizes, groupName, defaultSize, targetId, name) in new (string[], string, string, string?, string)[]
                 {
                     (["Large"], "Size", "Large", null, "a group of that name that can't take the model"),
                     (["Large"], "Other", "Large", $"group:{Guid.NewGuid():D}", "a group that's gone"),
                     (["Large", "large"], "Other", "Large", null, "two sizes of one name"),
                     (["Large", "Small"], "Other", "Medium", null, "a default that isn't a size"),
                     (["..."], "Other", "...", null, "a name with nothing to name a file after"),
                     ([" Large"], "Other", " Large", null, "a name with spaces around it"),
                     ([], "Other", "Large", null, "no sizes"),
                 })
        {
            var error = SizesExportService.PlanSizeGroup(root, GamePath, ModelFile(root), sizes, groupName, targetId,
                defaultSize, null, out var plan);
            Require(error is not null && plan is null && File.ReadAllText(metaPath) == before,
                $"sizes: {name} is refused before anything changes");
        }
        Require(SizesExportService.PlanSizeGroup(root, GamePath, Path.Combine(Path.GetTempPath(), "elsewhere.mdl"),
                ["Large"], "Other", null, "Large", null, out _) is not null,
            "sizes: a model outside its mod is refused");
    }

    private static ExportServer.SizesExportRequest Request() => new()
    {
        Schema = "instant-edit.export-sizes",
        Version = 1,
        PluginInstanceId = "instance",
        ContextId = "context",
        ExportId = "export",
        Capability = "capability",
        VariantTarget = "new_group",
        VariantGroupName = "Size",
        DefaultSize = "Medium",
        Sizes =
        [
            new() { Name = "Large", FilePath = "C:/Temp/size_0.mdl", Size = 3, Sha256 = new string('a', 64) },
            new() { Name = "Medium", FilePath = "C:/Temp/size_1.mdl", Size = 4, Sha256 = new string('b', 64) },
        ],
    };

    private static void RequestsAreValidated()
    {
        Require(ExportServer.ValidateSizesEnvelope(Request()) is null, "sizes request: a valid request passes");
        var group = Request();
        group.VariantTarget = "group";
        group.VariantGroupName = null;
        group.VariantTargetId = "group:00000000-0000-0000-0000-000000000001";
        group.DefaultSize = null;
        Require(ExportServer.ValidateSizesEnvelope(group) is null, "sizes request: an existing group needs no name");
        var cases = new Dictionary<string, (Action<ExportServer.SizesExportRequest> Change, string Code)>
        {
            ["schema"] = (request => request.Schema = "instant-edit.export", "unsupported_schema"),
            ["version"] = (request => request.Version = 2, "unsupported_version"),
            ["in place"] = (request => request.VariantTarget = "option", "invalid_variant_target"),
            ["no group name"] = (request => request.VariantGroupName = " ", "penumbra_setup_requires_group_name"),
            ["no group"] = (request => request.VariantTarget = "group", "missing_variant_target"),
            ["no sizes"] = (request => request.Sizes = [], "invalid_sizes"),
            ["too many sizes"] = (request => request.Sizes = Enumerable.Range(0, 65).Select(index =>
                new ExportServer.SizeFileRequest
                {
                    Name = $"Size {index}", FilePath = $"C:/Temp/size_{index}.mdl", Size = 1,
                    Sha256 = new string('a', 64),
                }).ToList(), "invalid_sizes"),
            ["one name twice"] = (request => request.Sizes![1].Name = "LARGE", "invalid_size_name"),
            ["a control character"] = (request => request.Sizes![1].Name = "Me\tdium", "invalid_size_name"),
            ["no file"] = (request => request.Sizes![0].FilePath = "", "missing_field"),
            ["an empty file"] = (request => request.Sizes![0].Size = 0, "invalid_size"),
            ["a bad hash"] = (request => request.Sizes![0].Sha256 = new string('g', 64), "invalid_sha256"),
            ["a default that isn't a size"] = (request => request.DefaultSize = "Small", "invalid_default_size"),
            ["attribute tags without groups"] = (request => request.AttributeTags = ["atr_tv_a"], "unsupported_field"),
        };
        foreach (var (name, (change, code)) in cases)
        {
            var request = Request();
            change(request);
            var error = ExportServer.ValidateSizesEnvelope(request);
            Require(error == code, $"sizes request: {name} is refused as {code} ({error})");
        }
    }

    private static void ReceiptsMatchTheWholeRequest()
    {
        using var registry = new ExportContextRegistry("sizes-regression");
        var context = registry.CreateGameContext(GamePath, GamePath, 0, 42428);
        var original = Request();
        const string exportId = "sizes-export";
        bool Begin(ExportServer.SizesExportRequest request, out ExportContextRegistry.ExportReservation? reservation,
            out string code)
            => registry.TryBeginExport(registry.PluginInstanceId, context.ContextId, exportId, context.Capability,
                request.Sizes![0].FilePath!, request.Sizes.Sum(size => size.Size),
                ExportServer.SizesManifestHash(request.Sizes), out reservation, out code,
                ExportServer.SizesExportRequestFingerprint(request));
        Require(Begin(original, out var owner, out _) && owner!.IsOwner, "sizes receipt: the export is reserved");
        var receipt = new ExportReceipt(true, "sizes_exported", "done");
        registry.CompleteExport(context.ContextId, exportId, receipt);
        Require(Begin(original, out var duplicate, out _) && !duplicate!.IsOwner &&
                duplicate.Completion.Result == receipt, "sizes receipt: an exact retry gets the receipt back");
        var changes = new Dictionary<string, Action<ExportServer.SizesExportRequest>>
        {
            ["the group name"] = request => request.VariantGroupName = "Other",
            ["the default size"] = request => request.DefaultSize = "Large",
            ["a size's name"] = request => request.Sizes![1].Name = "Small",
            ["a size's file"] = request => request.Sizes![1].Sha256 = new string('c', 64),
            ["the sizes' order"] = request => request.Sizes!.Reverse(),
            ["backups"] = request => request.BackupExisting = true,
        };
        foreach (var (name, change) in changes)
        {
            var changed = JsonSerializer.Deserialize<ExportServer.SizesExportRequest>(JsonSerializer.Serialize(original))!;
            change(changed);
            Require(!Begin(changed, out _, out var code) && code == "duplicate_export_id",
                $"sizes receipt: another request can't reuse the receipt ({name} changed)");
        }
    }
}
