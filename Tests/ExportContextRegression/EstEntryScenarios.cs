using System.Text.Json.Nodes;
using InstantEdit.Services;
using static InstantEdit.TestSupport.Assertions;

/// <summary>Hair EST entries set from Magic Fit's hair skeleton tags on export, taken back, and restored with backups.</summary>
internal static class EstEntryScenarios
{
    private const string MiqoteHair = "chara/human/c0801/obj/hair/h0108/model/c0801h0108_hir.mdl";
    private const string AuRaHair = "chara/human/c1401/obj/hair/h0108/model/c1401h0108_hir.mdl";
    private const string HairFile = "Files/hair/c0801h0108_hir.mdl";
    private const string OtherFile = "Files/hair/other_hir.mdl";
    private static readonly Guid GroupId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OptionA = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OptionB = Guid.Parse("33333333-3333-3333-3333-333333333333");

    public static void Run(string testRoot)
    {
        EnvelopeRejectsUnsupportedEntries();
        EntriesFollowTheTags(Path.Combine(testRoot, "EstEntries"));
        MismatchesOnlyWarn(Path.Combine(testRoot, "EstWarnings"));
        BackupsCarryTheEntries(Path.Combine(testRoot, "EstBackups"));
        NewVanillaModGetsTheEntry(Path.Combine(testRoot, "EstVanilla"));
    }

    private static EstEntryRequest Hair(int entry, string race = "c0801") => new() { Slot = "Hair", Entry = entry, Race = race };

    private static JsonObject Est(string gender, string race, int set, int entry) => new()
    {
        ["Type"] = "Est",
        ["Manipulation"] = new JsonObject
        {
            ["Entry"] = entry, ["Gender"] = gender, ["Race"] = race, ["SetId"] = set, ["Slot"] = "Hair",
        },
    };

    private static JsonObject HairMod(JsonArray? optionAManipulations = null) => new()
    {
        ["FileVersion"] = 4,
        ["Identifier"] = Guid.NewGuid().ToString("D"),
        ["Name"] = "Hair mod",
        ["DefaultData"] = new JsonObject
        {
            ["Files"] = new JsonObject { [MiqoteHair] = HairFile.Replace('/', '\\') },
            ["FileSwaps"] = new JsonObject(),
            ["Manipulations"] = new JsonArray(),
        },
        ["Groups"] = new JsonArray(new JsonObject
        {
            ["Id"] = GroupId.ToString("D"),
            ["Name"] = "Style",
            ["Type"] = "Single",
            ["Options"] = new JsonArray(
                new JsonObject
                {
                    ["Id"] = OptionA.ToString("D"),
                    ["Name"] = "Both races",
                    ["Files"] = new JsonObject { [MiqoteHair] = HairFile, [AuRaHair] = HairFile },
                    ["Manipulations"] = optionAManipulations ?? new JsonArray(),
                },
                new JsonObject
                {
                    ["Id"] = OptionB.ToString("D"),
                    ["Name"] = "Other",
                    ["Files"] = new JsonObject { [MiqoteHair] = OtherFile },
                    ["Manipulations"] = new JsonArray(),
                }),
        }),
    };

    private static JsonObject WriteMod(string root, JsonObject meta)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "meta.json"), meta.ToJsonString());
        return meta;
    }

    private static JsonObject ReadMod(string root) => JsonNode.Parse(File.ReadAllText(Path.Combine(root, "meta.json")))!.AsObject();

    private static JsonObject Container(JsonObject meta, Guid? option)
        => option is null
            ? meta["DefaultData"]!.AsObject()
            : meta["Groups"]![0]!["Options"]!.AsArray().Single(item => item!["Id"]!.GetValue<string>() == option.Value.ToString("D"))!.AsObject();

    /// <summary>The (race, entry) pairs of a container's hair EST entries for set 108.</summary>
    private static string[] Entries(JsonObject meta, Guid? option)
        => (Container(meta, option)["Manipulations"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Where(item => item["Type"]?.GetValue<string>() == "Est")
            .Select(item => item["Manipulation"]!)
            .Select(item => $"{item["Gender"]} {item["Race"]} {item["SetId"]} {item["Slot"]}={item["Entry"]}")
            .ToArray();

    private static void EnvelopeRejectsUnsupportedEntries()
    {
        Require(PenumbraService.ValidateEstEntryRequests(null) is null &&
                PenumbraService.ValidateEstEntryRequests([]) is null &&
                PenumbraService.ValidateEstEntryRequests([Hair(160)]) is null &&
                PenumbraService.ValidateEstEntryRequests([Hair(9999, "c1801")]) is null,
            "EST envelope: absent, empty and a player race's hair entry are accepted");
        var rejected = new (string Name, List<EstEntryRequest> Entries)[]
        {
            ("face slot", [new EstEntryRequest { Slot = "Face", Entry = 1, Race = "c0801" }]),
            ("entry 0", [Hair(0)]),
            ("entry 10000", [Hair(10000)]),
            ("NPC race", [Hair(160, "c0804")]),
            ("race without c", [Hair(160, "0801")]),
            ("unknown race", [Hair(160, "c1901")]),
            ("two hair entries", [Hair(160), Hair(161)]),
        };
        foreach (var (name, entries) in rejected)
            Require(PenumbraService.ValidateEstEntryRequests(entries) == "invalid_est_entries",
                $"EST envelope: {name} is refused");
        var request = new ExportServer.ExportRequest
        {
            Schema = "instant-edit.export", Version = 3, PluginInstanceId = "p", ContextId = "c", ExportId = "e",
            Capability = "k", FilePath = "C:/Temp/export.mdl", Size = 1, Sha256 = new string('a', 64),
            EstEntries = [Hair(160, "c0101x")],
        };
        Require(ExportServer.ValidateEnvelope(request) == "invalid_est_entries",
            "EST envelope: the export envelope refuses an invalid entry");
        request.EstEntries = [Hair(160)];
        Require(ExportServer.ValidateEnvelope(request) is null, "EST envelope: the export envelope takes a valid entry");
    }

    private static void EntriesFollowTheTags(string root)
    {
        var store = new EstEntryStore(Path.Combine(root, "config"));
        var modFolder = Path.Combine(root, "Hair mod");
        // The modder set Miqo'te's entry in option A by hand, twice (Penumbra keeps the first).
        WriteMod(modFolder, HairMod(new JsonArray(Est("Female", "Miqote", 108, 150), Est("Female", "Miqote", 108, 151))));

        var warnings = PenumbraService.UpdateEstEntriesForRegression(modFolder, "Hair mod", HairFile, [Hair(160)], store);
        var meta = ReadMod(modFolder);
        Require(Entries(meta, null).SequenceEqual(["Female Miqote 108 Hair=160"]),
            "EST: Default, which maps the hair, gets the tagged entry for the tag's race");
        Require(Entries(meta, OptionA).SequenceEqual(["Female Miqote 108 Hair=160"]),
            "EST: an option's hand-set entry is replaced once, without duplicates");
        Require(Entries(meta, OptionB).Length == 0, "EST: an option using another model file is left alone");
        Require(warnings.Count == 1 && warnings[0].Contains("c1401", StringComparison.Ordinal),
            "EST: the other race using the same file is only warned about");
        var records = store.Load();
        Require(records.Count == 2 &&
                records.Single(record => record.Container == "default").Previous is null &&
                records.Single(record => record.Container == $"option:{GroupId:D}:{OptionA:D}").Previous == 150 &&
                records.All(record => record is { Entry: 160, SetId: 108, GenderRace: 801, Slot: "Hair" }),
            "EST: the plugin records what it wrote and the value it replaced");

        var unchanged = File.ReadAllText(Path.Combine(modFolder, "meta.json"));
        PenumbraService.UpdateEstEntriesForRegression(modFolder, "Hair mod", HairFile, [Hair(160)], store);
        Require(File.ReadAllText(Path.Combine(modFolder, "meta.json")) == unchanged && store.Load().Count == 2,
            "EST: exporting the same tag again changes nothing");

        PenumbraService.UpdateEstEntriesForRegression(modFolder, "Hair mod", HairFile, [Hair(170)], store);
        meta = ReadMod(modFolder);
        Require(Entries(meta, OptionA).SequenceEqual(["Female Miqote 108 Hair=170"]) &&
                store.Load().Single(record => record.Container.StartsWith("option:", StringComparison.Ordinal)).Previous == 150,
            "EST: a new tag value updates the entry and keeps the modder's original value");

        PenumbraService.UpdateEstEntriesForRegression(modFolder, "Hair mod", HairFile, null, store);
        Require(Entries(ReadMod(modFolder), OptionA).SequenceEqual(["Female Miqote 108 Hair=170"]) && store.Load().Count == 2,
            "EST: without tag information (older add-on, disagreeing meshes) nothing changes");

        // The modder then edits Default's entry by hand.
        meta = ReadMod(modFolder);
        Container(meta, null)["Manipulations"] = new JsonArray(Est("Female", "Miqote", 108, 175));
        WriteMod(modFolder, meta);
        warnings = PenumbraService.UpdateEstEntriesForRegression(modFolder, "Hair mod", HairFile, [], store);
        meta = ReadMod(modFolder);
        Require(Entries(meta, null).SequenceEqual(["Female Miqote 108 Hair=175"]),
            "EST: an untagged export never removes an entry the modder changed");
        Require(Entries(meta, OptionA).SequenceEqual(["Female Miqote 108 Hair=150"]),
            "EST: an untagged export puts back the value the plugin replaced");
        Require(warnings.Count == 0 && store.Load().Count == 0, "EST: taking entries back forgets them");

        // Numeric enum values name the same lookup; an entry the plugin never wrote stays.
        meta = ReadMod(modFolder);
        Container(meta, null)["Manipulations"] = new JsonArray(new JsonObject
        {
            ["Type"] = "Est",
            ["Manipulation"] = new JsonObject { ["Entry"] = 90, ["Gender"] = 2, ["Race"] = 5, ["SetId"] = "108", ["Slot"] = 73 },
        });
        WriteMod(modFolder, meta);
        PenumbraService.UpdateEstEntriesForRegression(modFolder, "Hair mod", HairFile, [], store);
        Require(Entries(ReadMod(modFolder), null).SequenceEqual(["2 5 108 73=90"]),
            "EST: an untagged export leaves entries it did not write");
        PenumbraService.UpdateEstEntriesForRegression(modFolder, "Hair mod", HairFile, [Hair(160)], store);
        Require(Entries(ReadMod(modFolder), null).SequenceEqual(["Female Miqote 108 Hair=160"]) &&
                store.Load().Single(record => record.Container == "default").Previous == 90,
            "EST: Penumbra's numeric gender, race and slot values match the same lookup");
    }

    private static void MismatchesOnlyWarn(string root)
    {
        var store = new EstEntryStore(Path.Combine(root, "config"));
        var modFolder = Path.Combine(root, "Hair mod");
        WriteMod(modFolder, HairMod());
        var before = File.ReadAllText(Path.Combine(modFolder, "meta.json"));

        var otherRace = PenumbraService.UpdateEstEntriesForRegression(modFolder, "Hair mod", OtherFile, [Hair(160, "c1401")], store);
        Require(otherRace.Count == 1 && otherRace[0].Contains("c1401", StringComparison.Ordinal) &&
                otherRace[0].Contains("c0801", StringComparison.Ordinal),
            "EST: a tag for another race than the model's path is refused with a warning");
        var unused = PenumbraService.UpdateEstEntriesForRegression(modFolder, "Hair mod", "Files/unused.mdl", [Hair(160)], store);
        Require(unused.Count == 1 && unused[0].Contains("No Penumbra option", StringComparison.Ordinal),
            "EST: a model no option uses gets a warning");
        Require(File.ReadAllText(Path.Combine(modFolder, "meta.json")) == before && store.Load().Count == 0,
            "EST: warnings leave the mod and the records unchanged");

        var bodyFolder = Path.Combine(root, "Body mod");
        var body = HairMod();
        Container(body, null)["Files"] = new JsonObject { ["chara/equipment/e0001/model/c0801e0001_top.mdl"] = "Files/top.mdl" };
        WriteMod(bodyFolder, body);
        var notHair = PenumbraService.UpdateEstEntriesForRegression(bodyFolder, "Body mod", "Files/top.mdl", [Hair(160)], store);
        Require(notHair.Count == 1 && notHair[0].Contains("not used as a hair model", StringComparison.Ordinal) &&
                Entries(ReadMod(bodyFolder), null).Length == 0,
            "EST: a hair tag on a model that is not hair is refused with a warning");
        Require(PenumbraService.UpdateEstEntriesForRegression(bodyFolder, "Body mod", "Files/top.mdl", [], store).Count == 0,
            "EST: untagged non-hair exports stay silent");
        Require(PenumbraService.UpdateEstEntriesForRegression(bodyFolder, "Body mod", "Files/top.mdl", [Hair(160)], null)
                    .Single().Contains("by hand", StringComparison.Ordinal),
            "EST: without the record store the entry is not written and the modder is told to set it");
    }

    private static void NewVanillaModGetsTheEntry(string root)
    {
        var store = new EstEntryStore(Path.Combine(root, "config"));
        var staging = Path.Combine(root, "staging");
        Directory.CreateDirectory(staging);
        PenumbraService.StageGameModelMod(staging, "Vanilla Maeve", MiqoteHair, [1, 2, 3]);
        var warnings = PenumbraService.UpdateEstEntriesForRegression(
            staging, "Vanilla Maeve", "Files/" + MiqoteHair, [Hair(160)], store);
        var meta = ReadMod(staging);
        Require(warnings.Count == 0 && Entries(meta, null).SequenceEqual(["Female Miqote 108 Hair=160"]) &&
                store.Load().Single() is { Previous: null, Container: "default" } record &&
                record.Mod == $"id:{meta["Identifier"]!.GetValue<string>()}",
            "EST: a new mod from a vanilla hair gets the entry, recorded under the mod's identifier");
    }

    private static void BackupsCarryTheEntries(string root)
    {
        var store = new EstEntryStore(Path.Combine(root, "config"));
        var backups = new ModelBackupStore(Path.Combine(root, "cache"));
        var modFolder = Path.Combine(root, "Hair mod");
        WriteMod(modFolder, HairMod(new JsonArray(Est("Female", "Miqote", 108, 150))));
        var model = Path.Combine(modFolder, "Files", "hair", "c0801h0108_hir.mdl");
        Directory.CreateDirectory(Path.GetDirectoryName(model)!);
        File.WriteAllBytes(model, [1, 2, 3]);

        // Export: the backup keeps the state before the export changed it.
        var before = PenumbraService.CaptureEstStateForRegression(modFolder, "Hair mod", HairFile, store)!;
        var backup = backups.Create(model, "Hair mod", HairFile);
        backups.WriteEstState(backup, before);
        PenumbraService.UpdateEstEntriesForRegression(modFolder, "Hair mod", HairFile, [Hair(160)], store);
        Require(before["entries"]!.AsArray().Count == 3,
            "EST backups: every option using the model keeps its entry for every hair race it maps");

        // Restore: first a backup of today's state, then the backup's own.
        var current = PenumbraService.CaptureEstStateForRegression(modFolder, "Hair mod", HairFile, store)!;
        var restored = backups.ReadEstState(backup)!;
        var warnings = PenumbraService.RestoreEstStateForRegression(modFolder, "Hair mod", restored, store);
        var meta = ReadMod(modFolder);
        Require(warnings.Count == 0 && Entries(meta, null).Length == 0 &&
                Entries(meta, OptionA).SequenceEqual(["Female Miqote 108 Hair=150"]) && store.Load().Count == 0,
            "EST backups: restoring puts back the entries and the plugin's ownership from before the export");
        PenumbraService.RestoreEstStateForRegression(modFolder, "Hair mod", current, store);
        meta = ReadMod(modFolder);
        Require(Entries(meta, null).SequenceEqual(["Female Miqote 108 Hair=160"]) &&
                Entries(meta, OptionA).SequenceEqual(["Female Miqote 108 Hair=160"]) &&
                store.Load().Count == 2 && store.Load().Single(record => record.Container != "default").Previous == 150,
            "EST backups: restoring the pre-restore backup redoes the export's entries");

        Require(PenumbraService.CaptureEstStateForRegression(modFolder, "Hair mod", "Files/unused.mdl", store) is null,
            "EST backups: a model used as no hair keeps no EST state");
        meta = ReadMod(modFolder);
        meta["Groups"]![0]!["Options"]!.AsArray().RemoveAt(0);
        WriteMod(modFolder, meta);
        warnings = PenumbraService.RestoreEstStateForRegression(modFolder, "Hair mod", restored, store);
        Require(warnings.Count == 1 && warnings[0].Contains("Both races", StringComparison.Ordinal),
            "EST backups: an option deleted since is reported, not recreated");

        var sidecar = backup + ".est.json";
        Require(File.Exists(sidecar) && !Path.GetFileName(sidecar).EndsWith(".bak", StringComparison.Ordinal),
            "EST backups: the state sits beside the backup under a name backup listings skip");
        File.Delete(backup);
        backups.Cleanup();
        Require(!File.Exists(sidecar), "EST backups: cleanup removes the state of a backup that is gone");
        var second = backups.Create(model, "Hair mod", HairFile);
        backups.WriteEstState(second, before);
        backups.Clear(backups.Describe("Hair mod", HairFile).Id);
        Require(!File.Exists(second + ".est.json") && !Directory.Exists(Path.GetDirectoryName(second)),
            "EST backups: clearing a target's backups removes their states too");
    }
}
