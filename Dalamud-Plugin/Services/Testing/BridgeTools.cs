using System.Text.Json;
using System.Text.Json.Nodes;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;
using InstantEdit.Ui;

namespace InstantEdit.Services.Testing;

/// <summary>
/// Read-only test tools for XIV Bridge, a separate dev plugin that lets Claude Code look inside the
/// running game: state dumps that answer the in-game acceptance checklists in Tests/. Registered over
/// Dalamud IPC only when Instant Edit runs as a dev plugin; without the bridge nothing happens. The
/// bridge calls <c>InstantEdit.XivBridge.Invoke</c> on a worker thread, and tools that read UI state
/// hop to the framework thread, where the windows draw.
/// </summary>
internal sealed class BridgeTools : IDisposable
{
    private const string Owner = "InstantEdit";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly IDalamudPluginInterface pi;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly ExportContextRegistry contexts;
    private readonly ModelBackupStore backups;
    private readonly EstEntryStore estEntries;
    private readonly PhysicsWindow physics;
    private readonly ICallGateProvider<string, string, string> invoke;
    private readonly ICallGateSubscriber<object> ready;
    private readonly Dictionary<string, Func<JsonObject, JsonNode?>> tools;

    public BridgeTools(IDalamudPluginInterface pi, IFramework framework, IPluginLog log, Configuration config, ExportContextRegistry contexts,
        ModelBackupStore backups, EstEntryStore estEntries, PhysicsWindow physics)
    {
        this.pi = pi;
        this.framework = framework;
        this.log = log;
        this.config = config;
        this.contexts = contexts;
        this.backups = backups;
        this.estEntries = estEntries;
        this.physics = physics;
        tools = new Dictionary<string, Func<JsonObject, JsonNode?>>(StringComparer.Ordinal)
        {
            ["state"] = State,
            ["contexts"] = Contexts,
            ["est_entries"] = EstEntries,
            ["physics"] = Physics,
            ["live_short_test"] = LiveShortTest,
        };

        invoke = pi.GetIpcProvider<string, string, string>($"{Owner}.XivBridge.Invoke");
        invoke.RegisterFunc(Invoke);
        ready = pi.GetIpcSubscriber<object>("XivBridge.Ready");
        ready.Subscribe(Register);
        Register();
    }

    private const string Tools = """
        [
          { "name": "state",
            "description": "Instant Edit's version, plugin instance, ports and the settings exports and previews depend on.",
            "inputSchema": { "type": "object", "properties": {} } },
          { "name": "contexts",
            "description": "Export contexts (Blender imports) this instance holds, newest first: source mod, target file, collection, racial scaling, source option, export receipts and the model backups kept for the target. Capabilities are left out.",
            "inputSchema": { "type": "object", "properties": {
              "filter": { "type": "string", "description": "Keep contexts whose mod, mod name, game path or target path contains this text." },
              "limit": { "type": "integer", "description": "Contexts to return. Default 20." },
              "manifest": { "type": "boolean", "description": "Include the resource manifest (materials, textures, meta snapshot). Default false." },
              "all_backups": { "type": "boolean", "description": "List every backup of each target, not only the newest 3. Default false." } } } },
          { "name": "est_entries",
            "description": "The EST entries exports wrote into mods (mod as id:<Penumbra stable id>, container, slot, set, gender/race, value written, value replaced).",
            "inputSchema": { "type": "object", "properties": {
              "mod": { "type": "string", "description": "Keep entries whose mod key contains this text, such as part of the mod's stable id." } } } },
          { "name": "physics",
            "description": "The Body Physics builder: window, character and loaded physics file, document, message line, automatic preview, the preview itself and the IE Physics mod.",
            "inputSchema": { "type": "object", "properties": {
              "detail": { "type": "string", "enum": ["summary", "areas", "full"], "description": "summary (default); areas adds each simulator; full adds chains, connectors, springs and collision bits." } } } },
          { "name": "live_short_test",
            "description": "Timeline Live test: while on, Live shows the clip's first two thirds at that shorter length, to see whether the game loops a swapped copy at its own length. Returns the setting.",
            "inputSchema": { "type": "object", "properties": {
              "on": { "type": "boolean", "description": "Turn the short copy on or off. Omit to read the setting." } } } }
        ]
        """;

    private void Register()
    {
        try
        {
            pi.GetIpcSubscriber<string, string, object>("XivBridge.RegisterTools").InvokeAction(Owner, Tools);
            log.Debug("Registered the XIV Bridge test tools.");
        }
        catch (IpcNotReadyError)
        {
            // The bridge isn't loaded; XivBridge.Ready calls Register when it is.
        }
        catch (Exception error)
        {
            log.Warning(error, "Could not register the XIV Bridge test tools.");
        }
    }

    private string Invoke(string tool, string argumentsJson)
    {
        if (!tools.TryGetValue(tool, out var run))
            throw new ArgumentException($"Instant Edit has no test tool '{tool}'.");
        var arguments = (string.IsNullOrWhiteSpace(argumentsJson) ? null : JsonNode.Parse(argumentsJson)) as JsonObject ?? new JsonObject();
        return run(arguments)?.ToJsonString(JsonOptions) ?? "null";
    }

    // ---- Tools ----

    private JsonNode State(JsonObject arguments) => new JsonObject
    {
        ["version"] = BlenderClient.CurrentPluginVersion,
        ["dev"] = pi.IsDev,
        ["pluginInstanceId"] = contexts.PluginInstanceId,
        ["configDirectory"] = pi.ConfigDirectory.FullName,
        ["cacheDirectory"] = config.TextureCacheDirectory,
        ["listenPort"] = config.ListenPort,
        ["blenderPort"] = config.BlenderPort,
        ["settings"] = new JsonObject
        {
            ["applyRacialScaling"] = config.ApplyRacialScaling,
            ["applyTexturesAndMaterials"] = config.ApplyTexturesAndMaterials,
            ["excludeBodyAndGeneralMaterials"] = config.ExcludeBodyAndGeneralMaterials,
            ["includeVanillaResources"] = config.IncludeVanillaResources,
            ["recompressTextures"] = config.RecompressTextures,
            ["autoCompressTextures"] = config.AutoCompressTextures,
            ["autoFixHeels"] = config.AutoFixHeels,
            ["refitHairUvs"] = config.RefitHairUvs,
            ["useExistingSkeleton"] = config.UseExistingSkeleton,
            ["physicsAutoPreview"] = config.PhysicsAutoPreview,
        },
    };

    private JsonNode Contexts(JsonObject arguments)
    {
        var filter = String(arguments, "filter");
        var limit = Math.Clamp(Int(arguments, "limit") ?? 20, 1, 500);
        var manifest = Bool(arguments, "manifest") ?? false;
        var allBackups = Bool(arguments, "all_backups") ?? false;
        var all = contexts.Summaries();
        var matching = all.Where(summary => filter is null || new[]
            {
                summary.Context.SourceModDirectory, summary.Context.SourceModName, summary.Context.GamePath,
                summary.Context.ResolvedGamePath, summary.Context.TargetRelativePath, summary.Context.TargetFilePath,
            }.Any(text => text?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true)).ToArray();

        var list = new JsonArray();
        foreach (var summary in matching.Take(limit))
        {
            var context = JsonSerializer.SerializeToNode(summary.Context) as JsonObject ?? new JsonObject();
            // The capability is the add-on's secret for this context; it isn't test data.
            context.Remove("capability");
            if (!manifest)
                context.Remove("resourceManifest");
            context["pendingExports"] = summary.PendingExports;
            context["exports"] = new JsonArray(summary.Exports.Select(export => (JsonNode)new JsonObject
            {
                ["exportId"] = export.ExportId,
                ["file"] = export.FilePath,
                ["created"] = export.CreatedAt.ToString("O"),
                ["completed"] = export.CompletedAt?.ToString("O"),
                ["success"] = export.Receipt?.Success,
                ["code"] = export.Receipt?.Code,
                ["message"] = export.Receipt?.Message,
                ["warnings"] = export.Receipt?.Warnings is { } warnings ? new JsonArray(warnings.Select(text => (JsonNode)text).ToArray()) : null,
                ["targetFilePath"] = export.Receipt?.TargetFilePath,
                ["destinationName"] = export.Receipt?.DestinationName,
            }).ToArray());
            var kept = Backups(summary.Context.SourceModDirectory, summary.Context.TargetRelativePath);
            context["backupCount"] = kept.Count;
            context["backups"] = new JsonArray(kept.Take(allBackups ? kept.Count : 3).Select(backup => backup.DeepClone()).ToArray());
            list.Add(context);
        }
        return new JsonObject
        {
            ["total"] = all.Count,
            ["matching"] = matching.Length,
            ["contexts"] = list,
        };
    }

    /// <summary> The backups kept of a context's target, newest first. </summary>
    private IReadOnlyList<JsonNode> Backups(string? modDirectory, string? targetRelativePath)
    {
        if (string.IsNullOrEmpty(modDirectory) || string.IsNullOrEmpty(targetRelativePath))
            return [];
        var rows = new List<JsonNode>();
        foreach (var backup in backups.List(modDirectory, targetRelativePath))
        {
            // A backup cleaned up between listing and reading it is left out, not the whole call.
            string path;
            long size;
            try
            {
                path = backups.Resolve(backup.TargetId, backup.Name);
                size = new FileInfo(path).Length;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                continue;
            }
            rows.Add(new JsonObject
            {
                ["targetId"] = backup.TargetId,
                ["name"] = backup.Name,
                ["created"] = backup.Created.ToString("O"),
                ["size"] = size,
                ["estState"] = File.Exists(path + ".est.json"),
                ["raceState"] = File.Exists(path + ".race.json"),
            });
        }
        return rows;
    }

    private JsonNode EstEntries(JsonObject arguments)
    {
        var mod = String(arguments, "mod");
        var entries = estEntries.Load().Where(entry => mod is null || entry.Mod.Contains(mod, StringComparison.OrdinalIgnoreCase)).ToArray();
        return new JsonObject
        {
            ["count"] = entries.Length,
            ["entries"] = JsonSerializer.SerializeToNode(entries),
        };
    }

    private static JsonNode LiveShortTest(JsonObject arguments)
    {
        if (arguments["on"] is JsonValue on && on.TryGetValue<bool>(out var value))
            Services.Animations.Preview.MotionLiveDisplay.ShortTest = value;
        return new JsonObject { ["shortTest"] = Services.Animations.Preview.MotionLiveDisplay.ShortTest };
    }

    private JsonNode Physics(JsonObject arguments)
    {
        var detail = String(arguments, "detail") ?? "summary";
        return framework.RunOnFrameworkThread(() => physics.TestState(detail)).GetAwaiter().GetResult();
    }

    // ---- Arguments ----

    private static string? String(JsonObject arguments, string name)
        => arguments[name] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;

    private static int? Int(JsonObject arguments, string name)
        => arguments[name] is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

    private static bool? Bool(JsonObject arguments, string name)
        => arguments[name] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

    public void Dispose()
    {
        try { pi.GetIpcSubscriber<string, object>("XivBridge.UnregisterTools").InvokeAction(Owner); }
        catch (IpcNotReadyError) { }
        catch (Exception error) { log.Warning(error, "Could not unregister the XIV Bridge test tools."); }
        ready.Unsubscribe(Register);
        invoke.UnregisterFunc();
    }
}
