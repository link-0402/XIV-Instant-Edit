using System.Reflection;
using System.Text.Json.Nodes;
using Dalamud.Plugin.Services;
using InstantEdit.Studio;
using InstantEdit.Studio.Models.Project;
using InstantEdit.Studio.Services;
using InstantEdit.Studio.Services.Project;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// writers-5: a replacement whose game path leads outside the mod folder fails on its own; the rest
/// of the update is still written. Runs a whole Edit-mode update (ModWriter) against a temp mod.
/// </summary>
internal static class ReviewFixBuildScenarios
{
    public static async Task RunAsync()
    {
        EnsureLog();
        var root = Path.Combine(Path.GetTempPath(), "instant-edit-review-build-" + Guid.NewGuid().ToString("N"));
        var mod = Path.Combine(root, "Mods", "Writer Mod");
        Directory.CreateDirectory(mod);
        try
        {
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
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (Exception) { /* temp */ }
        }
    }

    /// <summary>The writer logs through Dalamud's log; a quiet one stands in for it here.</summary>
    private static void EnsureLog()
    {
        if (typeof(StudioServices).GetProperty(nameof(StudioServices.Log))!.GetValue(null) != null)
            return;
        StudioServices.Initialize(null!, DispatchProxy.Create<IPluginLog, ReviewFixQuietLog>(), null!, null!, null!, null!);
    }
}

/// <summary>An <see cref="IPluginLog"/> that drops everything.</summary>
public class ReviewFixQuietLog : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        => targetMethod?.ReturnType is { IsValueType: true } type && type != typeof(void) ? Activator.CreateInstance(type) : null;
}
