using System.Reflection;
using System.Text.Json.Nodes;
using Dalamud.Plugin.Services;
using InstantEdit.Studio;
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
