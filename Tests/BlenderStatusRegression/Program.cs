using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using InstantEdit;
using InstantEdit.Models;
using InstantEdit.Services;
using InstantEdit.TestSupport;
using Newtonsoft.Json;
using static InstantEdit.TestSupport.Assertions;

static async Task<BlenderStatus> ProbeAsync(
    Func<HttpResponseMessage>? responseFactory = null,
    Exception? exception = null,
    CancellationToken cancellationToken = default)
{
    using var http = new HttpClient(new StubHandler(responseFactory, exception));
    using var contexts = new ExportContextRegistry("blender-status-regression");
    using var client = new BlenderClient(null!, contexts, http);
    return await client.GetStatusAsync(42424, cancellationToken).ConfigureAwait(false);
}

static async Task CheckConnectionStatesAsync()
{
    var pluginVersion = BlenderClient.CurrentPluginVersion;
    Require(
        BlenderClient.NormalizeVersion(pluginVersion) == pluginVersion,
        "the plugin release version is normalized to major.minor.patch");


    var matching = await ProbeAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent($"{{\"addonVersion\":\"{pluginVersion}\"}}"),
    });
    Require(matching.Reachable && matching.AddonVersion == pluginVersion &&
            matching.Classify(pluginVersion) == BlenderConnectionState.Online,
        "matching add-on and plugin versions produce the online state");

    var mismatched = await ProbeAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"addonVersion\":\"1.1.3\"}"),
    });
    Require(mismatched.Reachable && mismatched.Classify(pluginVersion) == BlenderConnectionState.VersionMismatch,
        "a different add-on version produces a reachable mismatch state");

    foreach (var (name, body) in new[]
    {
        ("missing version", "{\"ok\":true,\"ready\":true}"),
        ("invalid version type", "{\"addonVersion\":123}"),
        ("malformed document", "{"),
    })
    {
        var status = await ProbeAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body),
        });
        Require(status.Reachable && status.AddonVersion is null &&
                status.Classify(pluginVersion) == BlenderConnectionState.VersionMismatch,
            $"{name} stays reachable but cannot report a matching version");
    }

    var nonSuccess = await ProbeAsync(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    Require(!nonSuccess.Reachable && nonSuccess.Classify(pluginVersion) == BlenderConnectionState.Offline,
        "a non-success status response produces offline state");

    var failedRequest = await ProbeAsync(exception: new HttpRequestException("connection refused"));
    Require(!failedRequest.Reachable && failedRequest.Classify(pluginVersion) == BlenderConnectionState.Offline,
        "a failed status request produces offline state");

    var canceledRequest = await ProbeAsync(cancellationToken: new CancellationToken(true));
    Require(!canceledRequest.Reachable && canceledRequest.Classify(pluginVersion) == BlenderConnectionState.Offline,
        "a canceled status request produces offline state");

}

static void CheckFirstTimeSetupConfiguration()
{
    var fresh = JsonConvert.DeserializeObject<Configuration>("{}");
    Require(fresh is not null && !fresh.FirstTimeSetupCompleted,
        "missing setup state defaults to incomplete for existing configurations");

    var completed = new Configuration { FirstTimeSetupCompleted = true };
    var roundTrip = JsonConvert.DeserializeObject<Configuration>(JsonConvert.SerializeObject(completed));
    Require(roundTrip is not null && roundTrip.FirstTimeSetupCompleted,
        "completed setup state survives configuration serialization");

    var root = Path.Combine(Path.GetTempPath(), "XIV-Instant-Edit-tests", $"setup-{Guid.NewGuid():N}");
    try
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "existing-file.txt"), "existing content");
        var managed = TextureFiles.EnsureCacheRoot(root);
        Require(TextureFiles.CacheFolder == "XIV Instant Edit" &&
                Path.GetFileName(managed) == "XIV Instant Edit" &&
                File.Exists(Path.Combine(managed, ".instant-edit-cache.json")) &&
                Directory.Exists(Path.Combine(managed, "imports")) &&
                Directory.Exists(Path.Combine(managed, "exports")) &&
                Directory.Exists(Path.Combine(managed, "backups")),
            "setup creates the managed cache inside a non-empty base directory");

        var conflictBase = Path.Combine(root, "conflict-base");
        var conflict = Path.Combine(conflictBase, TextureFiles.CacheFolder);
        Directory.CreateDirectory(conflict);
        File.WriteAllText(Path.Combine(conflict, "unrelated-file.txt"), "not a cache marker");
        try
        {
            TextureFiles.EnsureCacheRoot(conflictBase);
            throw new InvalidOperationException("a conflicting managed cache unexpectedly succeeded");
        }
        catch (IOException)
        {
            Require(true, "setup rejects a conflicting managed cache folder");
        }

        // Temp cleaners delete the old marker (and empty folders) while newer cache
        // files survive; a cache holding only its own entries re-adopts itself.
        var cleanedBase = Path.Combine(root, "cleaned-base");
        var cleaned = Path.Combine(cleanedBase, TextureFiles.CacheFolder);
        Directory.CreateDirectory(Path.Combine(cleaned, "Contexts"));
        Directory.CreateDirectory(Path.Combine(cleaned, "Backups"));
        Directory.CreateDirectory(Path.Combine(cleaned, "skeleton-library"));
        Directory.CreateDirectory(Path.Combine(cleaned, "game-exports", "chara"));
        File.WriteAllText(Path.Combine(cleaned, "Contexts", $"{Guid.NewGuid():N}.json"), "{}");
        File.WriteAllText(Path.Combine(cleaned, "TextureSessions.json"), "[]");
        File.WriteAllText(Path.Combine(cleaned, $".pending-context-revocations.json.{Guid.NewGuid():N}.tmp"), "{");
        Require(TextureFiles.EnsureCacheRoot(cleanedBase) == cleaned &&
                File.Exists(Path.Combine(cleaned, ".instant-edit-cache.json")),
            "a cache whose marker was removed re-adopts itself when it holds only cache entries");

        File.Delete(Path.Combine(cleaned, ".instant-edit-cache.json"));
        File.WriteAllText(Path.Combine(cleaned, "notes.txt"), "user file");
        try
        {
            TextureFiles.EnsureCacheRoot(cleanedBase);
            throw new InvalidOperationException("an unmarked cache with a foreign entry unexpectedly succeeded");
        }
        catch (IOException)
        {
            Require(!File.Exists(Path.Combine(cleaned, ".instant-edit-cache.json")),
                "an unmarked cache is still refused when it holds any foreign entry");
        }

        var editor = Path.Combine(root, "editor.exe");
        File.WriteAllText(editor, "test executable");
        Require(TextureEditService.TryValidateEditorPath(editor, out _),
            "setup accepts an existing absolute .exe texture editor path");
        Require(!TextureEditService.TryValidateEditorPath(Path.Combine(root, "missing.exe"), out _),
            "setup rejects a missing texture editor executable");
        Require(!TextureEditService.TryValidateEditorPath(Path.Combine(root, "editor.txt"), out _),
            "setup rejects a non-executable texture editor path");
    }
    finally
    {
        if (Directory.Exists(root))
            Directory.Delete(root, true);
    }
}

await CheckConnectionStatesAsync();
CheckFirstTimeSetupConfiguration();

foreach (var (body, expectedCache) in new[]
{
    ("""{"addonVersion":"1.1.7","capabilities":["instant-edit.texture-cache.v1"],"cacheRoot":"C:/Cache/XIV Instant Edit"}""", "C:/Cache/XIV Instant Edit"),
    ("""{"addonVersion":"1.1.7","cacheRoot":"C:/untrusted"}""", (string?)null),
    ("""{"addonVersion":"1.1.7","capabilities":["instant-edit.texture-cache.v1"],"cacheRoot":123}""", (string?)null),
})
{
    var cacheStatus = await ProbeAsync(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    Require(cacheStatus.CacheRoot == expectedCache, "cache synchronization requires the capability and a string root");
}
var settingsStatus = await ProbeAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
{
    Content = new StringContent($"{{\"addonVersion\":\"{BlenderClient.CurrentPluginVersion}\",\"capabilities\":[\"{BlenderClient.CacheSettingsCapability}\"]}}"),
});
Require(settingsStatus.CacheSettingsSupported, "the add-on advertises the central cache-settings endpoint");

var cacheHandler = new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
{
    Content = new StringContent("{\"ok\":true,\"cacheRoot\":\"C:/Cache/XIV Instant Edit\"}"),
}, null);
using (var cacheHttp = new HttpClient(cacheHandler))
using (var cacheContexts = new ExportContextRegistry("blender-cache-settings-regression"))
using (var cacheClient = new BlenderClient(null!, cacheContexts, cacheHttp))
{
    var configured = await cacheClient.ConfigureCacheAsync(42424, "C:/Cache", true);
    Require(configured == "C:/Cache/XIV Instant Edit" && cacheHandler.LastMethod == "POST" &&
            JsonNode.Parse(cacheHandler.LastBody!)!["schema"]?.GetValue<string>() == "instant-edit.cache-settings" &&
            JsonNode.Parse(cacheHandler.LastBody!)!["cacheDirectory"]?.GetValue<string>() == "C:/Cache" &&
            JsonNode.Parse(cacheHandler.LastBody!)!["automaticCleanup"]?.GetValue<bool>() == true,
        "the plugin sends its central cache directory and cleanup preference to Blender");
}

var structured = BridgeFailure.FromResponse(
    HttpStatusCode.BadRequest,
    """{"ok":false,"error":"Blender could not access the temporary model file.","component":"blender_addon","operation":"import","stage":"file_staging","code":"model_file_unavailable","cause":"Blender could not access the temporary model file.","remedy":"Run both applications as the same user.","diagnosticId":"01234567"}""",
    "import");
Require(
    structured.HttpStatus == 400 && structured.Stage == "file_staging" &&
    structured.Code == "model_file_unavailable" && structured.ShortDiagnosticId == "01234567" &&
    structured.UserMessage.Contains("Run both applications as the same user.", StringComparison.Ordinal),
    "structured Blender failures preserve stage, code, cause, remedy, status, and diagnostic ID");
var typedException = new BlenderBridgeException(structured, "structured response body");
Require(
    typedException.HttpStatus == 400 && typedException.Failure.Code == "model_file_unavailable" &&
    typedException.Message.Contains("Diagnostic ID: 01234567", StringComparison.Ordinal),
    "typed Blender bridge exceptions expose structured failure details to the UI");
var asynchronous = BridgeFailure.Create(
    "blender_addon", "import", "import_processing", "import_processing_failed",
    "The model operator failed.", "Review the diagnostic report.");
Require(
    asynchronous.UserMessage.StartsWith(
        "Blender finished receiving the model, but the import failed", StringComparison.Ordinal),
    "asynchronous import failures explain that request receipt already succeeded");
Require(
    BridgeFailure.IsDiagnosticId(asynchronous.DiagnosticId),
    "generated diagnostic IDs use the compact eight-character format");

Require(
    BlenderClient.ParseImportResponse(HttpStatusCode.OK, "{\"ok\":true,\"cached\":true}"),
    "BlenderClient accepts a valid successful import response");
try
{
    BlenderClient.ParseImportResponse(HttpStatusCode.OK, "{\"ok\":true}");
    throw new InvalidOperationException("unconfirmed success did not throw");
}
catch (BlenderBridgeException error)
{
    Require(
        error.HttpStatus == 200 && error.Failure.Code == "invalid_success_response",
        "BlenderClient rejects success responses that do not confirm the model was cached");
}
try
{
    BlenderClient.ParseImportResponse(
        HttpStatusCode.BadRequest,
        """{"ok":false,"component":"blender_addon","operation":"import","stage":"file_staging","code":"model_file_unavailable","cause":"model unavailable","remedy":"retry as the same user","diagnosticId":"01234567"}""");
    throw new InvalidOperationException("structured 400 did not throw");
}
catch (BlenderBridgeException error)
{
    Require(
        error.HttpStatus == 400 && error.Failure.Stage == "file_staging" &&
        error.Failure.Code == "model_file_unavailable" &&
        error.Failure.Remedy == "retry as the same user",
        "BlenderClient throws a populated typed exception for structured HTTP errors");
}

foreach (var malformedBody in new[] { "", "Bad Request", "{" })
{
    try
    {
        BlenderClient.ParseImportResponse(HttpStatusCode.BadRequest, malformedBody);
        throw new InvalidOperationException("legacy error did not throw");
    }
    catch (BlenderBridgeException error)
    {
        Require(
            error.Failure.Code == "legacy_http_error" &&
            error.Failure.Remedy.Contains("Update and restart", StringComparison.Ordinal),
            $"BlenderClient supplies compatibility guidance for {(
                malformedBody.Length == 0 ? "empty" : "malformed")} HTTP errors");
    }
}

try
{
    BlenderClient.ParseImportResponse(HttpStatusCode.OK, "not JSON");
    throw new InvalidOperationException("malformed success did not throw");
}
catch (BlenderBridgeException error)
{
    Require(
        error.Failure.Code == "invalid_success_response" && error.HttpStatus == 200,
        "BlenderClient reports malformed success responses explicitly");
}

var legacy = BridgeFailure.FromResponse(
    HttpStatusCode.BadRequest,
    "Bad Request",
    "import");
Require(
    legacy.Code == "legacy_http_error" && !legacy.UserMessage.Contains("400", StringComparison.Ordinal) &&
    legacy.UserMessage.Contains("Update and restart", StringComparison.Ordinal),
    "legacy Blender failures produce an actionable compatibility message");

var legacyJson = BridgeFailure.FromResponse(
    HttpStatusCode.BadRequest,
    "{\"ok\":false,\"error\":\"old add-on rejection\"}",
    "import");
Require(
    legacyJson.Code == "legacy_http_error" && legacyJson.Cause == "old add-on rejection",
    "legacy JSON error bodies retain their useful cause");

var importHandler = new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
{
    Content = new StringContent("{\"ok\":true,\"cached\":true}"),
}, null);
var importRoot = Path.Combine(Path.GetTempPath(), "XIV-Instant-Edit-tests", Guid.NewGuid().ToString("N"));
try
{
    var backupStore = new ModelBackupStore(importRoot);
    using var importHttp = new HttpClient(importHandler);
    using var importContexts = new ExportContextRegistry(
        "blender-import-regression", persist: null, backups: backupStore);
    using var importClient = new BlenderClient(null!, importContexts, importHttp);
    var targetRoot = Path.Combine(importRoot, "RegisteredMod");
    var targetFile = Path.Combine(targetRoot, "Files", "model.mdl");
    Require(
        await importClient.SendSourceImportAsync(
            42424, Path.Combine(importRoot, "handoff.mdl"),
            "chara/equipment/e0001/model/c0101e0001_top.mdl", 0, "Import", 42425,
            targetFile, "registered-mod", "Registered Mod",
            sourceModRootPath: targetRoot, targetRelativePath: "Files/model.mdl"),
        "BlenderClient accepts a source import with a managed backup target");
    var importEnvelope = JsonNode.Parse(importHandler.LastBody!)!.AsObject();
    Require(
        importEnvelope["backupTargetId"]?.GetValue<string>()?.Length == 64 &&
        importEnvelope["backupDirectory"]?.GetValue<string>() is { Length: > 0 } backupDirectory &&
        Path.GetFileName(backupDirectory) == importEnvelope["backupTargetId"]?.GetValue<string>(),
        "source import requests serialize the managed backup target metadata");
    Require(importEnvelope.ContainsKey("racialScaling") && importEnvelope["racialScaling"] is null,
        "an unscaled import tells Blender it has no racial scaling");
    await importClient.SendGameImportAsync(
        42424, Path.Combine(importRoot, "scaled.mdl"),
        "chara/equipment/e6001/model/c0201e6001_top.mdl", "chara/equipment/e6001/model/c0201e6001_top.mdl", 0, "Scaled", 42425,
        racialScaling: new RacialScalingRecord { ModelRace = 201, CharacterRace = 801 });
    var scaledEnvelope = JsonNode.Parse(importHandler.LastBody!)!.AsObject();
    Require(scaledEnvelope["racialScaling"]?["modelRace"]?.GetValue<string>() == "c0201" &&
            scaledEnvelope["racialScaling"]?["race"]?.GetValue<string>() == "c0801" &&
            scaledEnvelope["racialScaling"]!.AsObject().Count == 2,
        "a racially scaled import tells Blender both races, so the add-on can tag its meshes");
}
finally
{
    if (Directory.Exists(importRoot))
        Directory.Delete(importRoot, true);
}

var keyed = BlenderClient.ParseAnimationResponse(HttpStatusCode.OK,
    """{"ok":true,"queued":false,"applied":true,"action":"Live pose 19:42:07","armature":"Skeleton","frames":301,"frameRate":60.0,"frameStart":1,"frameEnd":301,"matchedBones":438,"missingBoneCount":6,"missingBones":["n_hara","iv_ochinko_a","iv_ochinko_b","iv_ochinko_c","iv_ochinko_d","iv_ochinko_e"]}""");
Require(
    keyed is { Applied: true, Action: "Live pose 19:42:07", Armature: "Skeleton", Frames: 301, FrameStart: 1, FrameEnd: 301,
        MatchedBones: 438, MissingBoneCount: 6 } && keyed.MissingBones!.Count == 6 && keyed.FrameRate == 60,
    "an animation Blender keyed reports its action, armature, frames and bone match");
Require(
    keyed.Describe().Contains("\"Live pose 19:42:07\" on \"Skeleton\"", StringComparison.Ordinal) &&
    keyed.Describe().Contains("6 bones are not in the armature (n_hara, iv_ochinko_a, iv_ochinko_b, iv_ochinko_c, …)", StringComparison.Ordinal),
    "the animation summary names the action and the first bones the armature lacks");
Require(keyed.AlignedBoneCount == 0 && !keyed.Describe().Contains("Aligned", StringComparison.Ordinal),
    "an animation that changed no bone's Inherit Scale says nothing about it");
var aligned = BlenderClient.ParseAnimationResponse(HttpStatusCode.OK,
    """{"ok":true,"queued":false,"applied":true,"action":"Live pose 19:43:10","armature":"Skeleton","frames":301,"frameRate":60.0,"frameStart":1,"frameEnd":301,"matchedBones":444,"missingBoneCount":0,"missingBones":[],"alignedBoneCount":4,"alignedBones":["j_sebo_a","j_asi_a_l","j_asi_b_l","j_asi_c_l"]}""");
Require(aligned.AlignedBoneCount == 4 && aligned.Describe().EndsWith("4 bones now inherit scale Aligned, so they scale the game's way.", StringComparison.Ordinal),
    "the animation summary says how many bones Blender set to inherit scale Aligned");
var queuedAnimation = BlenderClient.ParseAnimationResponse(HttpStatusCode.Accepted, """{"ok":true,"queued":true,"applied":false}""");
Require(!queuedAnimation.Applied && queuedAnimation.Describe().Contains("idle", StringComparison.Ordinal),
    "an animation Blender could not key yet is reported as queued");
try
{
    BlenderClient.ParseAnimationResponse((HttpStatusCode)422,
        """{"component":"blender_addon","operation":"animation_import","stage":"animation_processing","code":"animation_no_matching_bones","cause":"Armature \"Skeleton\" has none of the animation's 7 bones.","remedy":"Send the animation to the FFXIV skeleton armature your meshes are weighted to.","diagnosticId":"89abcdef"}""");
    throw new InvalidOperationException("animation failure did not throw");
}
catch (BlenderBridgeException error)
{
    Require(
        error.HttpStatus == 422 && error.Failure.Stage == "animation_processing" &&
        error.Failure.Code == "animation_no_matching_bones" &&
        error.Failure.UserMessage.StartsWith("Blender animation import failed during animation processing", StringComparison.Ordinal),
        "an animation Blender could not use reports Blender's reason");
}
foreach (var unconfirmed in new[] { "{}", """{"applied":false}""", "not JSON" })
{
    try
    {
        BlenderClient.ParseAnimationResponse(HttpStatusCode.OK, unconfirmed);
        throw new InvalidOperationException("unconfirmed animation success did not throw");
    }
    catch (BlenderBridgeException error)
    {
        Require(error.Failure.Code == "invalid_success_response" && error.Failure.Operation == "animation_import",
            $"an animation response that confirms nothing is refused ({unconfirmed})");
    }
}
var animationHandler = new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
{
    Content = new StringContent("""{"ok":true,"applied":true,"action":"Walk","armature":"Skeleton","frames":31,"frameRate":30,"frameStart":1,"frameEnd":31,"matchedBones":7,"missingBoneCount":0,"missingBones":[]}"""),
}, null);
using (var animationHttp = new HttpClient(animationHandler))
using (var animationContexts = new ExportContextRegistry("blender-animation-regression", persist: null))
using (var animationClient = new BlenderClient(null!, animationContexts, animationHttp))
{
    var sent = await animationClient.SendAnimationAsync(42424, "XIEA"u8.ToArray());
    Require(sent is { Applied: true, Action: "Walk", Frames: 31 } && animationHandler.LastMethod == "POST" && animationHandler.LastBody == "XIEA",
        "an animation is posted to Blender as its encoded take and the confirmation is parsed");
}
using (var offlineHttp = new HttpClient(new StubHandler(null, new HttpRequestException("refused"))))
using (var offlineContexts = new ExportContextRegistry("blender-animation-offline", persist: null))
using (var offlineClient = new BlenderClient(null!, offlineContexts, offlineHttp))
{
    try
    {
        await offlineClient.SendAnimationAsync(42424, [1]);
        throw new InvalidOperationException("offline animation send did not throw");
    }
    catch (BlenderBridgeException error)
    {
        Require(error.Failure.Stage == "transport" && error.Failure.Code == "blender_connection_failed" &&
                error.Failure.Operation == "animation_import",
            "an animation that cannot reach Blender reports a transport failure");
    }
}

var safeText = BridgeFailure.Safe(
    "Could not read C:\\Users\\Example\\AppData\\Local\\Temp\\model.mdl or /home/example/model.mdl " +
    "and received {\"capability\":\"private-value\"}");
Require(
    !safeText.Contains("C:\\Users", StringComparison.OrdinalIgnoreCase) &&
    !safeText.Contains("/home/", StringComparison.Ordinal) &&
    !safeText.Contains("private-value", StringComparison.Ordinal) &&
    safeText.Contains("<local-path>", StringComparison.Ordinal),
    "bridge diagnostics redact absolute paths and capability values");

await TextureEditScenarios.RunAsync();
await PreviewScenarios.RunAsync();
Console.WriteLine("All Blender status and texture regressions passed.");

sealed class StubHandler(
    Func<HttpResponseMessage>? responseFactory,
    Exception? exception) : HttpMessageHandler
{
    public string? LastBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        LastMethod = request.Method.Method;
        LastBody = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (exception is not null)
            throw exception;
        return responseFactory?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.OK);
    }

    public string? LastMethod { get; private set; }
}
