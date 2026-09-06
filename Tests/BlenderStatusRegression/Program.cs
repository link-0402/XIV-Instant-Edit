using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using InstantEdit.Models;
using InstantEdit.Services;

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
    Console.WriteLine($"[PASS] {message}");
}

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

await CheckConnectionStatesAsync();

var structured = BridgeFailure.FromResponse(
    HttpStatusCode.BadRequest,
    """{"ok":false,"error":"Blender could not access the temporary model file.","component":"blender_addon","operation":"import","stage":"file_staging","code":"model_file_unavailable","cause":"Blender could not access the temporary model file.","remedy":"Run both applications as the same user.","diagnosticId":"0123456789abcdef0123456789abcdef"}""",
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
        """{"ok":false,"component":"blender_addon","operation":"import","stage":"file_staging","code":"model_file_unavailable","cause":"model unavailable","remedy":"retry as the same user","diagnosticId":"0123456789abcdef0123456789abcdef"}""");
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
}
finally
{
    if (Directory.Exists(importRoot))
        Directory.Delete(importRoot, true);
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

Console.WriteLine("All Blender status regressions passed.");

sealed class StubHandler(
    Func<HttpResponseMessage>? responseFactory,
    Exception? exception) : HttpMessageHandler
{
    public string? LastBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        LastBody = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (exception is not null)
            throw exception;
        return responseFactory?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.OK);
    }
}
