using System.Reflection;
using System.Text.Json.Nodes;
using Dalamud.Plugin.Services;
using InstantEdit.Studio;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Models.Project;
using InstantEdit.Studio.Services;
using InstantEdit.Studio.Services.Project;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// The Mod Browser's writer, builder and folder watcher, which log through Dalamud's log: a replacement
/// whose game path leads outside the mod folder, two options whose folders come out the same, a
/// meta.json merge that fails after the files were written, a build names the mod as typed, and a
/// stopped folder watcher is noticed and started again. Each runs against a temp mod.
/// </summary>
internal static class ModWriterScenarios
{
    private const string GroupId = "8d0c4b52-31a5-4a52-8f8e-2a7d1e0f0001";
    private const string UpperId = "8d0c4b52-31a5-4a52-8f8e-2a7d1e0f0002";
    private const string LowerId = "8d0c4b52-31a5-4a52-8f8e-2a7d1e0f0003";
    private const string Model   = "chara/equipment/e0001/model/c0101e0001_top.mdl";

    public static async Task RunAsync()
    {
        EnsureLog();
        var root = Path.Combine(Path.GetTempPath(), "instant-edit-mod-writer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await AnUnsafeReplacementPathFailsOnItsOwn(root);         // writers-5
            await OptionsWhoseFoldersCollideGetPlacesOfTheirOwn(root);  // writers-4
            await AFailedMergeStillRecordsTheWrittenFiles(root);        // writers-6
            await ABuildNamesTheModAsTyped(root);                       // writers-7
            await ASwitchedOffVanillaModelLosesItsEqdpBits(root);
            await ABuiltModelsPartTagsBecomeAToggleGroup(root);
            await MeshesOfOneMaterialCanBePointedApart(root);
            await FittedMaterialsWireTheirDiffuse(root);
            AStoppedWatcherIsNoticed(root);                             // glue-6
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (Exception) { /* temp */ }
        }
    }

    /// <summary>A replacement whose game path leads outside the mod folder fails on its own; the rest of the update is still written.</summary>
    private static async Task AnUnsafeReplacementPathFailsOnItsOwn(string root)
    {
        root = Path.Combine(root, "unsafe-path");
        var mod = Path.Combine(root, "Mods", "Writer Mod");
        Directory.CreateDirectory(mod);
        File.WriteAllText(Path.Combine(mod, "meta.json"), new JsonObject
        {
            ["FileVersion"] = 4,
            ["Identifier"] = Guid.NewGuid(),
            ["Name"] = "Writer Mod",
            ["DefaultData"] = new JsonObject { ["Files"] = new JsonObject() },
        }.ToJsonString());
        var source = Path.Combine(root, "top.mdl");
        File.WriteAllBytes(source, new byte[] { 1, 2, 3, 4 });

        const string good = "chara/equipment/e0001/model/c0101e0001_top.mdl";
        const string bad  = "chara/../../../escape.mdl";
        var request = new WriteRequest
        {
            ModPath         = mod,
            ModFolderName   = "Writer Mod",
            Meta            = PenumbraModMeta.Load(mod),
            PreviousLedger  = new WriteLedger(),
            Sidecar         = new StudioProject(),
            CreatedHere     = true,
            PluginDirectory = root,
            Replacements    =
            {
                new ResourceReplacement { GamePath = bad,  SourcePath = source, IsNew = true },
                new ResourceReplacement { GamePath = good, SourcePath = source, IsNew = true },
            },
        };

        var report = await new WriteJob(request).Task;
        Require(report.FatalError == null, "an unsafe replacement path does not stop the whole update");
        Require(report.Entries.Any(e => e.Outcome == WriteOutcome.Failed && e.GamePath == bad),
            "the replacement with the unsafe path is reported as failed");
        Require(File.Exists(Path.Combine(mod, "chara", "equipment", "e0001", "model", "c0101e0001_top.mdl")),
            "the other replacement is still written");
        Require(!File.Exists(Path.Combine(root, "escape.mdl")) && !File.Exists(Path.Combine(Path.GetDirectoryName(root)!, "escape.mdl")),
            "nothing is written outside the mod folder");
        var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(mod, "meta.json")))!;
        Require(meta["DefaultData"]?["Files"]?[good] != null && meta["DefaultData"]?["Files"]?[bad] == null,
            "meta.json points at the written file only");
    }

    private static async Task OptionsWhoseFoldersCollideGetPlacesOfTheirOwn(string root)
    {
        var mod = Path.Combine(root, "Mods", "Collide");
        Directory.CreateDirectory(mod);
        WriteMeta(mod, new JsonArray
        {
            new JsonObject
            {
                ["Id"] = GroupId, ["Name"] = "Look", ["Type"] = "Single", ["DefaultSettings"] = 0,
                ["Options"] = new JsonArray
                {
                    new JsonObject { ["Id"] = UpperId, ["Name"] = "Red", ["Files"] = new JsonObject() },
                    new JsonObject { ["Id"] = LowerId, ["Name"] = "red", ["Files"] = new JsonObject() },
                },
            },
        });
        var upper = Source(root, "upper.mdl", 1);
        var lower = Source(root, "lower.mdl", 2);
        var upperRef = new OptionRef(GroupId, UpperId).Serialize();
        var lowerRef = new OptionRef(GroupId, LowerId).Serialize();

        for (int run = 1; run <= 2; run++)
        {
            var report = await Update(mod, "Collide",
                new ResourceReplacement { GamePath = Model, Option = upperRef, SourcePath = upper, IsNew = true },
                new ResourceReplacement { GamePath = Model, Option = lowerRef, SourcePath = lower, IsNew = true });
            Require(report.FatalError == null && !report.Entries.Any(e => e.Outcome == WriteOutcome.Failed),
                $"update {run}: two options whose folders differ only in case both write ({string.Join("; ", report.Entries.Select(e => $"{e.Outcome} {e.Detail}"))})");
        }

        var meta = PenumbraModMeta.Load(mod);
        bool both = meta.TryGetFile(OptionRef.Parse(upperRef), Model, out var upperRel) & meta.TryGetFile(OptionRef.Parse(lowerRef), Model, out var lowerRel);
        Require(both && !PathNormalizer.SameRel(upperRel, lowerRel), "each option points at a file of its own");
        Require(File.ReadAllBytes(PathNormalizer.ToDiskPath(mod, upperRel))[0] == 1 && File.ReadAllBytes(PathNormalizer.ToDiskPath(mod, lowerRel))[0] == 2,
            "and each file holds that option's model");
    }

    private static async Task AFailedMergeStillRecordsTheWrittenFiles(string root)
    {
        var mod = Path.Combine(root, "Mods", "Locked Meta");
        Directory.CreateDirectory(mod);
        WriteMeta(mod, new JsonArray());
        var good = File.ReadAllText(Path.Combine(mod, "meta.json"));
        var source = Source(root, "top.mdl", 7);
        var replacement = new ResourceReplacement { GamePath = Model, SourcePath = source, IsNew = true };

        // meta.json can't be read once the files are written (the request's copy was taken before).
        var request = Request(mod, "Locked Meta", replacement);
        File.WriteAllText(Path.Combine(mod, "meta.json"), "{ not json");
        var failed = await new WriteJob(request).Task;
        var natural = Path.Combine(mod, "chara", "equipment", "e0001", "model", "c0101e0001_top.mdl");
        Require(failed.FatalError != null && File.Exists(natural), "the merge fails after the file was written");
        var ledger = StudioProjectStore.LoadLedger(mod);
        var entry = ledger?.Files.SingleOrDefault(f => f.GamePath == Model);
        Require(entry is { EntryNotPointed: true } && PathNormalizer.SameRel(entry.RelPath, @"chara\equipment\e0001\model\c0101e0001_top.mdl"),
            "the ledger still records the written file, as not pointed at yet");

        File.WriteAllText(Path.Combine(mod, "meta.json"), good);
        var report = await Update(mod, "Locked Meta", replacement);
        Require(report.FatalError == null && !report.Entries.Any(e => e.Outcome == WriteOutcome.Failed),
            "the next update goes through without taking the file for someone else's");
        var meta = PenumbraModMeta.Load(mod);
        Require(meta.TryGetFile(OptionRef.Default, Model, out var rel) && PathNormalizer.SameRel(rel, entry!.RelPath),
            "and points meta.json at the file written before, in its natural place");
        Require(StudioProjectStore.LoadLedger(mod)?.Files.Single(f => f.GamePath == Model).EntryNotPointed == false,
            "the ledger then has it as pointed");
        Require(!Directory.Exists(Path.Combine(mod, "instant_edit")), "no second copy was written beside it");
    }

    private static async Task ABuildNamesTheModAsTyped(string root)
    {
        var modRoot = Path.Combine(root, "BuildRoot");
        Directory.CreateDirectory(modRoot);
        const string typed = "Top: Red/Blue";
        var request = new BuildRequest
        {
            ModRoot         = modRoot,
            ModName         = InstantEdit.Studio.Models.PortSession.SanitizeFolderName(typed),
            DisplayName     = typed,
            PluginDirectory = root,
            Meta            = new ModMetaInfo(),
            Items           = new List<ItemBuildRequest>(),
        };
        var report = await new BuildJob(request).Task;
        var meta = Path.Combine(modRoot, "Top_ Red_Blue", "meta.json");
        Require(report.FatalError == null && File.Exists(meta), $"the build makes the mod's folder from the safe name ({report.FatalError})");
        Require((string?)JsonNode.Parse(File.ReadAllText(meta))?["Name"] == typed, "and names the mod as typed in meta.json");
    }

    /// <summary>
    /// A gear race whose own vanilla model is switched off (Miqo'te female) gets an Eqdp entry with the
    /// slot's bits cleared, so it falls back to the port's model; the other slots' bits are not its to write.
    /// </summary>
    private static async Task ASwitchedOffVanillaModelLosesItsEqdpBits(string root)
    {
        var miqoF = new RaceGender(PlayerRace.Miqote, PlayerGender.Female);
        var midF  = new RaceGender(PlayerRace.Midlander, PlayerGender.Female);
        var mask  = EqdpInfo.Mask(EquipSlot.Body);
        var modRoot = Path.Combine(root, "VanillaOff");
        Directory.CreateDirectory(modRoot);
        var request = new BuildRequest
        {
            ModRoot         = modRoot,
            ModName         = "Vanilla Off",
            DisplayName     = "Vanilla Off",
            PluginDirectory = root,
            Meta            = new ModMetaInfo(),
            Items           = new List<ItemBuildRequest>
            {
                new()
                {
                    Subject            = new GearSubject(new GameDataService.GameItem { RowId = 1, Name = "Top", ModelId = 6000, Slot = EquipSlot.Body }),
                    Models             = new List<RaceModelEntry>(),
                    Materials          = new List<MaterialSetup>(),
                    ModelMaterialSlots = new List<ModelMaterialSlot>(),
                    NativeRaces        = new HashSet<RaceGender> { midF, miqoF },
                    VanillaModelsOff   = new[] { miqoF },
                    VanillaEqdp        = new Dictionary<string, ushort> { [miqoF.RaceCode] = (ushort)(mask | 0x3), [midF.RaceCode] = mask },
                },
            },
        };
        var report = await new BuildJob(request).Task;
        Require(report.FatalError == null, $"a build with a vanilla model switched off runs ({report.FatalError})");
        var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(modRoot, "Vanilla Off", "meta.json")))!;
        var eqdp = (meta["DefaultData"]?["Manipulations"] as JsonArray ?? new JsonArray())
            .Where(m => (string?)m?["Type"] == "Eqdp").Select(m => m!["Manipulation"]!).ToList();
        Require(eqdp.Count == 1 && (string?)eqdp[0]["Race"] == "Miqote" && (string?)eqdp[0]["Gender"] == "Female"
                && (int?)eqdp[0]["Entry"] == 0 && (int?)eqdp[0]["SetId"] == 6000 && (string?)eqdp[0]["Slot"] == "Body",
            $"one Eqdp entry clears Miqo'te female's body bits, and nothing else is written ({eqdp.Count} entries)");
    }

    /// <summary>A body model the build copies, its parts tagged atr_tv_a and atr_tv_b, gets an IMC group with both parts in it.</summary>
    private static async Task ABuiltModelsPartTagsBecomeAToggleGroup(string root)
    {
        var midF = new RaceGender(PlayerRace.Midlander, PlayerGender.Female);
        var modRoot = Path.Combine(root, "PartToggles");
        Directory.CreateDirectory(modRoot);
        var source = Path.Combine(root, "tagged_top.mdl");
        File.WriteAllBytes(source, ModCheckFixtures.Mdl(["/mt_c0201e6000_top_a.mtrl"], [
            new ModCheckFixtures.Mesh(0, new ModCheckFixtures.Submesh("atr_tv_a"), new ModCheckFixtures.Submesh("atr_tv_b", "atr_top")),
        ]));
        ModImcOverride Variant(byte variant) => new()
        {
            Slot = EquipSlot.Body, SetId = 6000, Variant = variant, DecalId = 0, VfxId = 0, MaterialAnimationId = 0, AttributeMask = 0x3FF, SoundId = 0,
        };
        var request = new BuildRequest
        {
            ModRoot         = modRoot,
            ModName         = "Part Toggles",
            DisplayName     = "Part Toggles",
            PluginDirectory = root,
            Meta            = new ModMetaInfo(),
            Items           = new List<ItemBuildRequest>
            {
                new()
                {
                    Subject            = new GearSubject(new GameDataService.GameItem { RowId = 1, Name = "Top", ModelId = 6000, Slot = EquipSlot.Body }),
                    Models             = new List<RaceModelEntry> { new() { Race = midF.Race, Gender = midF.Gender, SourcePath = source } },
                    Materials          = new List<MaterialSetup>(),
                    ModelMaterialSlots = new List<ModelMaterialSlot>(),
                    NativeRaces        = new HashSet<RaceGender> { midF },
                    ImcOverrides       = new List<ModImcOverride> { Variant(1), Variant(2) },
                },
            },
        };
        var report = await new BuildJob(request).Task;
        Require(report.FatalError == null, $"a build of a tagged model runs ({report.FatalError})");
        var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(modRoot, "Part Toggles", "meta.json")))!;
        var group = (meta["Groups"] as JsonArray)?.SingleOrDefault();
        Require((string?)group?["Type"] == "Imc" && (string?)group?["Name"] == "Top Parts" && (bool?)group?["OnlyAttributes"] == true,
            "the model's part tags make an IMC group, which changes only the mask when the item writes no materials");
        Require((group?["Options"] as JsonArray)?.Select(o => ((string?)o?["Name"], (int?)o?["AttributeMask"])).SequenceEqual(new (string?, int?)[] { ("A", 1), ("B", 2) }) == true
                && (int?)group?["DefaultSettings"] == 3,
            "with one option per tag, both on");
        Require(meta["DefaultData"]?["Manipulations"] == null, "and no variant overrides are written for an item without materials");
    }

    /// <summary>
    /// Materials fitted to a model, built for real: the one with the stash's diffuse is written in the
    /// diffuse texture mode, the one with only placeholders keeps the colour table, and each mesh points
    /// at the material fitted to its own.
    /// </summary>
    private static async Task FittedMaterialsWireTheirDiffuse(string root)
    {
        var midF = new RaceGender(PlayerRace.Midlander, PlayerGender.Female);
        var modRoot = Path.Combine(root, "Fitted");
        Directory.CreateDirectory(modRoot);
        var source = Path.Combine(root, "fitted_top.mdl");
        File.WriteAllBytes(source, ModCheckFixtures.Mdl(["/Top.mtrl", "/Gloves.mtrl"]));
        var diffuse = Path.Combine(root, "top_d.png");
        using (var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(8, 8, new SixLabors.ImageSharp.PixelFormats.Rgba32(200, 40, 40, 255)))
            SixLabors.ImageSharp.ImageExtensions.SaveAsPng(image, diffuse);

        var subject = new GearSubject(new GameDataService.GameItem { RowId = 1, Name = "Top", ModelId = 6000, Slot = EquipSlot.Body });
        var none = new Dictionary<InstantEdit.Studio.Services.Stash.TexturePurpose, string>();
        var withDiffuse = new Dictionary<InstantEdit.Studio.Services.Stash.TexturePurpose, string> { [InstantEdit.Studio.Services.Stash.TexturePurpose.Diffuse] = diffuse };
        var fit = InstantEdit.Studio.Services.Stash.MaterialFit.Fit(ModelMaterialRewriter.ReadMeshMaterials(File.ReadAllBytes(source)),
            name => name == "/Top.mtrl" ? withDiffuse : none, subject, midF, otherPreset: null);
        Require(fit.Materials.Count == 2 && fit.WithDiffuse == 1, $"the model's two materials are fitted, one with a diffuse ({fit.Materials.Count}, {fit.WithDiffuse})");

        var request = new BuildRequest
        {
            ModRoot         = modRoot,
            ModName         = "Fitted",
            DisplayName     = "Fitted",
            PluginDirectory = Path.Combine(AppContext.BaseDirectory, "Studio"),
            Meta            = new ModMetaInfo(),
            Items           = new List<ItemBuildRequest>
            {
                new()
                {
                    Subject            = subject,
                    Models             = new List<RaceModelEntry> { new() { Race = midF.Race, Gender = midF.Gender, SourcePath = source } },
                    Materials          = fit.Materials.ToList(),
                    ModelMaterialSlots = Enumerable.Range(0, fit.Materials.Count).Select(i => new ModelMaterialSlot { MaterialIndex = i }).ToList(),
                    NativeRaces        = new HashSet<RaceGender> { midF },
                },
            },
        };
        var report = await new BuildJob(request).Task;
        Require(report.FatalError == null && report.Failed == 0,
            $"a build of fitted materials runs ({report.FatalError}{string.Join("; ", report.Entries.Where(e => e.Outcome == BuildOutcome.Failed).Select(e => $"{e.Label}: {e.Detail}"))})");

        var mod = Path.Combine(modRoot, "Fitted");
        uint? TextureMode(string material)
        {
            var file = Directory.EnumerateFiles(mod, material + ".mtrl", SearchOption.AllDirectories).SingleOrDefault();
            return file == null ? null : MtrlReader.Read(File.ReadAllBytes(file)).ShaderKeys.FirstOrDefault(k => k.Id == 0xB616DC5A)?.Value;
        }
        Require(TextureMode("mt_c0201e6000_top_a") == 0x600EF9DF, "the material with the stash's diffuse is written in the diffuse texture mode");
        var placeholders = TextureMode("mt_c0201e6000_top_b");
        Require(Directory.EnumerateFiles(mod, "mt_c0201e6000_top_b.mtrl", SearchOption.AllDirectories).Any() && placeholders != 0x600EF9DF,
            $"the one with only placeholders is written too, and keeps the colour table ({placeholders?.ToString("X8") ?? "the preset's default"})");
        Require(report.Entries.Count(e => e.Category == BuildCategory.Texture && e.Detail == "flat (no bump) 16x16 dummy") == 2
                && report.Entries.Count(e => e.Category == BuildCategory.Texture && e.Detail == "white 16x16 dummy") == 4,
            "every empty slot gets its role's placeholder: a flat normal, a white mask and a white index");

        var model = File.ReadAllBytes(Directory.EnumerateFiles(mod, "*.mdl", SearchOption.AllDirectories).Single());
        Require(ModelMaterialRewriter.ReadMeshMaterialsStrict(model).SequenceEqual(new[] { "/mt_c0201e6000_top_a.mtrl", "/mt_c0201e6000_top_b.mtrl" }),
            "each mesh points at the material fitted to its own");
    }

    private static void AStoppedWatcherIsNoticed(string root)
    {
        var folder = Path.Combine(root, "watched");
        Directory.CreateDirectory(folder);
        using var watcher = new ModFolderWatcher();
        watcher.Watch(folder);
        Require(!watcher.Failed, "a folder that can be watched is watched");

        watcher.NoteError(new InternalBufferOverflowException());
        Require(!watcher.Failed && watcher.TakeChanges(TimeSpan.Zero, out var meta, out var files) && meta && files,
            "after a buffer overflow the watcher keeps running, and everything counts as changed");

        watcher.NoteError(new IOException("the network name is no longer available"));
        Require(watcher.Failed, "any other error means the watcher stopped: the caller looks on a timer instead");
        watcher.Watch(folder);
        Require(!watcher.Failed, "and starting it again clears that");

        watcher.Watch(Path.Combine(root, "missing"));
        Require(watcher.Failed, "a folder that can't be watched stays on the timer");
    }

    private static void WriteMeta(string mod, JsonArray groups)
        => File.WriteAllText(Path.Combine(mod, "meta.json"), new JsonObject
        {
            ["FileVersion"] = 4,
            ["Identifier"]  = Guid.NewGuid(),
            ["Name"]        = Path.GetFileName(mod),
            ["DefaultData"] = new JsonObject { ["Files"] = new JsonObject() },
            ["Groups"]      = groups,
        }.ToJsonString());

    /// <summary>
    /// The mod editor points meshes, not material slots: two meshes on one slot given different
    /// materials come out with that slot split. A set-up saved by slot still writes as it did.
    /// </summary>
    private static async Task MeshesOfOneMaterialCanBePointedApart(string root)
    {
        var mod = Path.Combine(root, "Mods", "Meshes Apart");
        Directory.CreateDirectory(mod);
        WriteMeta(mod, new JsonArray());
        var source = Path.Combine(root, "two-mesh.mdl");
        File.WriteAllBytes(source, InstantEdit.TestSupport.TestModels.TwoMeshModel());

        var perMesh = new ResourceReplacement
        {
            GamePath = Model, SourcePath = source, IsNew = true,
            MaterialNames = new List<string> { string.Empty, "/mt_c0101e0001_top_b.mtrl" }, MaterialNamesPerMesh = true,
        };
        var report = await Update(mod, "Meshes Apart", perMesh);
        Require(report.FatalError == null && !report.Entries.Any(e => e.Outcome == WriteOutcome.Failed),
            $"a model with its meshes pointed apart writes ({string.Join("; ", report.Entries.Select(e => $"{e.Outcome} {e.Detail}"))})");
        var written = File.ReadAllBytes(Path.Combine(mod, "chara", "equipment", "e0001", "model", "c0101e0001_top.mdl"));
        Require(ModelMaterialRewriter.ReadMeshMaterialsStrict(written).SequenceEqual(new[] { "/old.mtrl", "/mt_c0101e0001_top_b.mtrl" })
                && ModelMaterialRewriter.ReadMaterialsStrict(written).Count == 2,
            "mesh 0 keeps its material and mesh 1 loads the new one, from a slot of its own");

        var bySlot = new ResourceReplacement
        {
            GamePath = Model, SourcePath = source, IsNew = true,
            MaterialNames = new List<string> { "/mt_c0101e0001_top_c.mtrl" },
        };
        await Update(mod, "Meshes Apart", bySlot);
        written = File.ReadAllBytes(Path.Combine(mod, "chara", "equipment", "e0001", "model", "c0101e0001_top.mdl"));
        Require(ModelMaterialRewriter.ReadMeshMaterialsStrict(written).SequenceEqual(new[] { "/mt_c0101e0001_top_c.mtrl", "/mt_c0101e0001_top_c.mtrl" })
                && ModelMaterialRewriter.ReadMaterialsStrict(written).Count == 1,
            "a set-up saved by slot still renames that slot for every mesh on it");
    }

    private static string Source(string root, string name, byte marker)
    {
        var path = Path.Combine(root, name);
        File.WriteAllBytes(path, new byte[] { marker, 2, 3, 4 });
        return path;
    }

    private static WriteRequest Request(string mod, string folderName, params ResourceReplacement[] replacements)
    {
        var request = new WriteRequest
        {
            ModPath         = mod,
            ModFolderName   = folderName,
            Meta            = PenumbraModMeta.Load(mod),
            PreviousLedger  = StudioProjectStore.LoadLedger(mod) ?? new WriteLedger(),
            Sidecar         = new StudioProject(),
            CreatedHere     = true,
            PluginDirectory = Path.GetDirectoryName(mod)!,
        };
        request.Replacements.AddRange(replacements.Select(r => r.Clone()));
        return request;
    }

    private static Task<WriteReport> Update(string mod, string folderName, params ResourceReplacement[] replacements)
        => new WriteJob(Request(mod, folderName, replacements)).Task;

    /// <summary>The writer logs through Dalamud's log; a quiet one stands in for it here.</summary>
    private static void EnsureLog()
    {
        if (typeof(StudioServices).GetProperty(nameof(StudioServices.Log))!.GetValue(null) != null)
            return;
        StudioServices.Initialize(null!, DispatchProxy.Create<IPluginLog, QuietLog>(), null!, null!, null!, null!);
    }
}

/// <summary>An <see cref="IPluginLog"/> that drops everything.</summary>
public class QuietLog : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        => targetMethod?.ReturnType is { IsValueType: true } type && type != typeof(void) ? Activator.CreateInstance(type) : null;
}
