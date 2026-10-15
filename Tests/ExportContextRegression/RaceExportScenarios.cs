using System.Text.Json.Nodes;
using InstantEdit.Services;
using InstantEdit.Services.PenumbraMods;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// Quick Export as another race's model: the race's game path, the file it goes into next to the
/// imported model, the options that map it and the EQDP entry that makes the race load it.
/// </summary>
internal static class RaceExportScenarios
{
    private const string TopPath = "chara/equipment/e6001/model/c0201e6001_top.mdl";
    private const string RacePath = "chara/equipment/e6001/model/c0801e6001_top.mdl";

    public static void Run(string testRoot)
    {
        GamePaths();
        OptionsThatUseTheModelGetTheRaceModel(Path.Combine(testRoot, "RaceOptions"));
        ExportingAgainKeepsTheFileAndEntry(Path.Combine(testRoot, "RaceAgain"));
        FilesTheModUsesAreNotTaken(Path.Combine(testRoot, "RaceUsedFiles"));
        SharedRaceFilesAreNotWrittenOver(Path.Combine(testRoot, "RaceShared"));
        RestoreUndoesTheOptions(Path.Combine(testRoot, "RaceRestore"));
        AccessoriesAndNewMods(Path.Combine(testRoot, "RaceAccessory"));
        UnusableModsAreRefused(Path.Combine(testRoot, "RaceRefused"));
        RequestsAreValidated();
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

    private static JsonObject Files(params (string GamePath, string File)[] files)
    {
        var mappings = new JsonObject();
        foreach (var (gamePath, file) in files)
            mappings[gamePath] = file;
        return new JsonObject { ["Files"] = mappings, ["FileSwaps"] = new JsonObject(), ["Manipulations"] = new JsonArray() };
    }

    private static JsonObject Group(string name, params (string Name, JsonObject Data)[] options)
    {
        var list = new JsonArray();
        foreach (var (optionName, data) in options)
        {
            data["Name"] = optionName;
            data["Id"] = Guid.NewGuid().ToString();
            list.Add(data);
        }
        return new JsonObject { ["Name"] = name, ["Id"] = Guid.NewGuid().ToString(), ["Type"] = "Single", ["Options"] = list };
    }

    private static void Write(string root, JsonObject meta)
    {
        Directory.CreateDirectory(Path.Combine(root, "Files"));
        File.WriteAllText(Path.Combine(root, "meta.json"), meta.ToJsonString());
    }

    private static RaceExportService.RaceModelPlan Plan(string root, string model, int race, int? gameBits, string what,
        string gamePath = TopPath)
    {
        var error = RaceExportService.PlanRaceModel(root, gamePath, model, race, gameBits, out var plan);
        Require(error is null && plan is not null, $"race export: {what} is planned ({error})");
        File.WriteAllText(Path.Combine(root, "meta.json"), plan!.Meta.ToJsonString());
        return plan;
    }

    private static JsonObject[] Eqdp(JsonNode? container)
        => (container?["Manipulations"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(item => item["Type"]?.GetValue<string>() == "Eqdp").ToArray();

    private static JsonNode Option(JsonNode meta, string group, string option)
        => meta["Groups"]!.AsArray().Single(item => item!["Name"]!.GetValue<string>() == group)!["Options"]!.AsArray()
            .Single(item => item!["Name"]!.GetValue<string>() == option)!;

    private static void GamePaths()
    {
        Require(RaceExportService.RaceModelGamePath(TopPath, 801) == RacePath &&
                RaceExportService.RaceModelGamePath("chara/accessory/a0001/model/c0201a0001_ear.mdl", 1401) ==
                "chara/accessory/a0001/model/c1401a0001_ear.mdl",
            "race export: a race's gear and accessory models sit at the same path with its race code");
        Require(RaceExportService.RaceModelGamePath(TopPath, 201) is null &&
                RaceExportService.RaceModelGamePath(TopPath, 804) is null &&
                RaceExportService.RaceModelGamePath(TopPath, 1901) is null &&
                RaceExportService.RaceModelGamePath("chara/human/c0201/obj/hair/h0101/model/c0201h0101_hir.mdl", 801) is null &&
                RaceExportService.RaceModelGamePath("chara/human/c0101/obj/body/b0001/model/c0101b0001_top.mdl", 801) is null,
            "race export: no race model for the model's own race, NPC races, hair or bodies");
        Require(RaceExportService.PlayerRaceCode("c0801") == 801 && RaceExportService.PlayerRaceCode("c0804") is null &&
                RaceExportService.PlayerRaceCode("0801") is null && RaceExportService.PlayerRaceCode(null) is null,
            "race export: requests name a player race such as c0801");
    }

    private static void OptionsThatUseTheModelGetTheRaceModel(string root)
    {
        Write(root, Metadata(
            Files((TopPath, "Files\\top.mdl"), ("chara/equipment/e6001/material/v0001/mt_c0201e6001_top_a.mtrl", "Files/top_a.mtrl")),
            Group("Style", ("Plain", Files((TopPath, "Files/top.mdl"))), ("Alt", Files((TopPath, "Files/alt.mdl"))))));
        var plan = Plan(root, "Files/top.mdl", 801, 1, "a model the default data and an option use");
        var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "meta.json")))!;
        Require(plan.GamePath == RacePath && plan.RelativePath == "Files/c0801e6001_top.mdl" &&
                plan.Containers.SequenceEqual(["Default", "Style: Plain"]),
            "race export: the race's model sits next to the imported one, named after the race's game file");
        Require(meta["DefaultData"]!["Files"]![RacePath]!.GetValue<string>() == "Files/c0801e6001_top.mdl" &&
                meta["DefaultData"]!["Files"]![TopPath]!.GetValue<string>() == "Files\\top.mdl" &&
                Option(meta, "Style", "Plain")["Files"]![RacePath]!.GetValue<string>() == "Files/c0801e6001_top.mdl" &&
                Option(meta, "Style", "Alt")["Files"]![RacePath] is null && Eqdp(Option(meta, "Style", "Alt")).Length == 0,
            "race export: it is mapped beside the original wherever the mod uses the imported model, and nowhere else");
        var entry = Eqdp(meta["DefaultData"]).Single()["Manipulation"]!;
        Require(entry["Entry"]!.GetValue<int>() == 0b1100 && entry["SetId"]!.GetValue<int>() == 6001 &&
                entry["Slot"]!.GetValue<string>() == "Body" && entry["Race"]!.GetValue<string>() == "Miqote" &&
                entry["Gender"]!.GetValue<string>() == "Female" && Eqdp(Option(meta, "Style", "Plain")).Length == 1,
            "race export: each of those turns on the race's EQDP model flag for the set's body, keeping the game's material flag");
    }

    private static void ExportingAgainKeepsTheFileAndEntry(string root)
    {
        var defaultData = Files((TopPath, "Files/top.mdl"), (RacePath, "Files/miqo.mdl"));
        defaultData["Manipulations"] = new JsonArray(
            new JsonObject
            {
                ["Type"] = "Eqdp",
                ["Manipulation"] = new JsonObject
                {
                    ["Entry"] = 0, ["Gender"] = "Female", ["Race"] = "Miqote", ["SetId"] = 6001, ["Slot"] = 4,
                },
            },
            new JsonObject { ["Type"] = "Imc", ["Manipulation"] = new JsonObject { ["PrimaryId"] = 6001 } },
            new JsonObject
            {
                ["Type"] = "Eqdp",
                ["Manipulation"] = new JsonObject
                {
                    ["Entry"] = 0b1100, ["Gender"] = 2, ["Race"] = 5, ["SetId"] = "6001", ["Slot"] = "Body",
                },
            },
            new JsonObject
            {
                ["Type"] = "Eqdp",
                ["Manipulation"] = new JsonObject
                {
                    ["Entry"] = 0b1100, ["Gender"] = "Female", ["Race"] = "Miqote", ["SetId"] = 6001, ["Slot"] = "Legs",
                },
            });
        Write(root, Metadata(defaultData));
        var plan = Plan(root, "Files/top.mdl", 801, 1, "a model already mapped for the race");
        var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "meta.json")))!;
        var entries = Eqdp(meta["DefaultData"]);
        Require(plan.RelativePath == "Files/miqo.mdl" && meta["DefaultData"]!["Files"]![RacePath]!.GetValue<string>() == "Files/miqo.mdl",
            "race export: a race path the options map already keeps its file, so exporting again replaces it");
        Require(entries.Length == 2 && entries[0]["Manipulation"]!["Entry"]!.GetValue<int>() == 0b1000 &&
                entries[0]["Manipulation"]!["Slot"]!.GetValue<string>() == "Body" &&
                entries[1]["Manipulation"]!["Slot"]!.GetValue<string>() == "Legs" &&
                meta["DefaultData"]!["Manipulations"]![1]!["Type"]!.GetValue<string>() == "Imc",
            "race export: the race's EQDP entries for the slot, by name or number, become one in place with the model flag on and the option's material flag; other entries stay");

        var again = Plan(root, "Files/top.mdl", 801, 1, "the same export again");
        Require(again.RelativePath == "Files/miqo.mdl" && Eqdp(again.Meta["DefaultData"]).Length == 2,
            "race export: exporting again changes nothing more");

        // A game model's first race export maps only the race's model; later ones find it there.
        Write(root, Metadata(Files((RacePath, "Files/" + RacePath))));
        var promoted = Plan(root, "Files/" + RacePath, 801, 0, "a game model's race mod");
        Require(promoted.RelativePath == "Files/" + RacePath &&
                Eqdp(promoted.Meta["DefaultData"]).Single()["Manipulation"]!["Entry"]!.GetValue<int>() == 0b1000,
            "race export: a mod holding only the race's model gets it written again, with the game's material flag off");
    }

    private static void FilesTheModUsesAreNotTaken(string root)
    {
        Write(root, Metadata(
            Files((TopPath, "Files/top.mdl")),
            Group("Other", ("Legs", Files(("chara/equipment/e6001/model/c0201e6001_dwn.mdl", "Files/c0801e6001_top.mdl"))))));
        var plan = Plan(root, "Files/top.mdl", 801, null, "a model whose race file name the mod uses");
        Require(plan.RelativePath == "Files/c0801e6001_top 2.mdl" &&
                Eqdp(plan.Meta["DefaultData"]).Single()["Manipulation"]!["Entry"]!.GetValue<int>() == 0b1100,
            "race export: a file the mod uses elsewhere is never taken, and without the game's file the material flag is on");
    }

    private static void SharedRaceFilesAreNotWrittenOver(string root)
    {
        // An "all races" mod: one file for every race, so the c0801 path already maps the imported model.
        Write(root, Metadata(Files((TopPath, "Files/top.mdl"), (RacePath, "Files/top.mdl"))));
        File.WriteAllText(Path.Combine(root, "Files", "c0801e6001_top.mdl"), "an author's alternative");
        var plan = Plan(root, "Files/top.mdl", 801, 1, "a file mapped at both races' paths");
        Require(plan.RelativePath == "Files/c0801e6001_top 2.mdl" &&
                plan.Meta["DefaultData"]!["Files"]![TopPath]!.GetValue<string>() == "Files/top.mdl" &&
                plan.Meta["DefaultData"]!["Files"]![RacePath]!.GetValue<string>() == "Files/c0801e6001_top 2.mdl",
            "race export: a file mapped at another race's path keeps it; the race gets a new file, past an unmapped one already there");

        // The race's file is also another option's c0801 model, for a model this export doesn't touch.
        Write(root, Metadata(
            Files((TopPath, "Files/top.mdl"), (RacePath, "Files/miqo.mdl")),
            Group("Style", ("Alt", Files((TopPath, "Files/alt.mdl"), (RacePath, "Files/miqo.mdl"))))));
        plan = Plan(root, "Files/top.mdl", 801, 1, "a race file another option maps");
        var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "meta.json")))!;
        Require(plan.RelativePath != "Files/miqo.mdl" &&
                meta["DefaultData"]!["Files"]![RacePath]!.GetValue<string>() == plan.RelativePath &&
                Option(meta, "Style", "Alt")["Files"]![RacePath]!.GetValue<string>() == "Files/miqo.mdl",
            "race export: a race file another option maps is never written over; that option keeps it");
    }

    private static void RestoreUndoesTheOptions(string root)
    {
        var defaultData = Files((TopPath, "Files/top.mdl"), (RacePath, "Files/miqo.mdl"));
        defaultData["Manipulations"] = new JsonArray(
            new JsonObject { ["Type"] = "Imc", ["Manipulation"] = new JsonObject { ["PrimaryId"] = 6001 } },
            new JsonObject
            {
                ["Type"] = "Eqdp",
                ["Manipulation"] = new JsonObject
                {
                    ["Entry"] = 0, ["Gender"] = "Female", ["Race"] = "Miqote", ["SetId"] = 6001, ["Slot"] = "Body",
                },
            });
        Write(root, Metadata(defaultData, Group("Style", ("Plain", Files((TopPath, "Files/top.mdl"))))));
        var metaPath = Path.Combine(root, "meta.json");
        var before = JsonNode.Parse(File.ReadAllText(metaPath))!;
        var plan = Plan(root, "Files/top.mdl", 801, 1, "a race export to undo");
        var state = new JsonObject
        {
            ["schema"] = "instant-edit.race-export", ["version"] = 1, ["racePath"] = RacePath,
            ["raceFile"] = plan.RelativePath, ["containers"] = plan.Undo.DeepClone(),
        };
        Require(plan.RelativePath == "Files/miqo.mdl", "race export restore: the race's own file is reused");
        var warnings = RaceExportService.RestoreRaceExportMeta(root, state);
        var after = JsonNode.Parse(File.ReadAllText(metaPath))!;
        Require(warnings.Count == 0 &&
                JsonNode.DeepEquals(after["DefaultData"], before["DefaultData"]) &&
                JsonNode.DeepEquals(Option(after, "Style", "Plain"), Option(before, "Style", "Plain")),
            "race export restore: every option's race mapping and EQDP entries are back as they were, the new ones gone");

        state["containers"]![1]!["container"] = "option:gone";
        Require(RaceExportService.RestoreRaceExportMeta(root, state).Single().Contains("no longer exists") &&
                RaceExportService.RestoreRaceExportMeta(root, new JsonObject { ["schema"] = "other" }).Single().Contains("unreadable"),
            "race export restore: an option that's gone or an unreadable record is a warning");

        // The record rides beside the imported model's backup and goes with it.
        var store = new ModelBackupStore(Path.Combine(root, "Config"));
        var model = Path.Combine(root, "Files", "top.mdl");
        File.WriteAllText(model, "model");
        var backup = store.Create(model, "RaceRestore", "Files/top.mdl");
        store.WriteRaceState(backup, state);
        Require(store.ReadEstState(backup) is null && JsonNode.DeepEquals(store.ReadRaceState(backup), state),
            "race export restore: the record is kept beside the backup, apart from its EST entries");
        File.Delete(backup);
        store.Cleanup();
        Require(!File.Exists(backup + ".race.json"), "race export restore: a backup's record goes with it");
    }

    private static void AccessoriesAndNewMods(string root)
    {
        const string ear = "chara/accessory/a0001/model/c0201a0001_ear.mdl";
        Write(root, Metadata(Files((ear, "Files/earring.mdl"))));
        var plan = Plan(root, "/Files/earring.mdl", 1401, 3, "an accessory", ear);
        var entry = Eqdp(plan.Meta["DefaultData"]).Single()["Manipulation"]!;
        Require(plan.RelativePath == "Files/c1401a0001_ear.mdl" && entry["Entry"]!.GetValue<int>() == 0b11 &&
                entry["Slot"]!.GetValue<string>() == "Ears" && entry["Race"]!.GetValue<string>() == "AuRa" &&
                entry["SetId"]!.GetValue<int>() == 1,
            "race export: accessories use their own slot's EQDP bits");
    }

    private static void UnusableModsAreRefused(string root)
    {
        Write(root, Metadata(
            Files((TopPath, "Files/top.mdl"), (RacePath, "Files/a.mdl")),
            Group("Style", ("Plain", Files((TopPath, "Files/top.mdl"), (RacePath, "Files/b.mdl"))))));
        var metaPath = Path.Combine(root, "meta.json");
        var before = File.ReadAllText(metaPath);
        foreach (var (name, model, race, gamePath) in new[]
                 {
                     ("options mapping the race path to different files", "Files/top.mdl", 801, TopPath),
                     ("a model the mod doesn't use", "Files/other.mdl", 801, TopPath),
                     ("the model's own race", "Files/top.mdl", 201, TopPath),
                     ("hair", "Files/top.mdl", 801, "chara/human/c0201/obj/hair/h0101/model/c0201h0101_hir.mdl"),
                     ("a path outside the mod", "../top.mdl", 801, TopPath),
                 })
        {
            var error = RaceExportService.PlanRaceModel(root, gamePath, model, race, 1, out var plan);
            Require(error is not null && plan is null && File.ReadAllText(metaPath) == before,
                $"race export: {name} is refused before anything changes ({error})");
        }
    }

    private static ExportServer.RaceExportRequest Request() => new()
    {
        Schema = "instant-edit.export-race",
        Version = 1,
        PluginInstanceId = "instance",
        ContextId = "context",
        ExportId = "export",
        Capability = "capability",
        Race = "c0801",
        FilePath = "C:/Temp/race.mdl",
        Size = 3,
        Sha256 = new string('a', 64),
    };

    private static void RequestsAreValidated()
    {
        Require(ExportServer.ValidateRaceEnvelope(Request()) is null, "race request: a valid request passes");
        var cases = new Dictionary<string, (Action<ExportServer.RaceExportRequest> Change, string Code)>
        {
            ["schema"] = (request => request.Schema = "instant-edit.export", "unsupported_schema"),
            ["version"] = (request => request.Version = 2, "unsupported_version"),
            ["no race"] = (request => request.Race = null, "missing_field"),
            ["an NPC race"] = (request => request.Race = "c0804", "invalid_race"),
            ["a bare number"] = (request => request.Race = "0801", "invalid_race"),
            ["no file"] = (request => request.FilePath = "", "missing_field"),
            ["an empty file"] = (request => request.Size = 0, "invalid_size"),
            ["a bad hash"] = (request => request.Sha256 = new string('g', 64), "invalid_sha256"),
            ["a bad mod name"] = (request => request.NewModName = "a/b", "invalid_mod_name"),
            ["attribute tags without groups"] = (request => request.AttributeTags = ["atr_tv_a"], "unsupported_field"),
        };
        foreach (var (name, (change, code)) in cases)
        {
            var request = Request();
            change(request);
            var error = ExportServer.ValidateRaceEnvelope(request);
            Require(error == code, $"race request: {name} is refused as {code} ({error})");
        }
        var other = Request();
        other.Race = "c1401";
        Require(ExportServer.RaceExportRequestFingerprint(Request()) == ExportServer.RaceExportRequestFingerprint(Request()) &&
                ExportServer.RaceExportRequestFingerprint(other) != ExportServer.RaceExportRequestFingerprint(Request()),
            "race request: a retried export matches its receipt only for the same race");
    }
}
