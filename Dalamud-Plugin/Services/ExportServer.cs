using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Plugin.Services;
using InstantEdit.Models;
using InstantEdit.Services.GameFiles;
using InstantEdit.Services.PenumbraMods;
using InstantEdit.Services.Skeletons;

namespace InstantEdit.Services;

/// <summary>
/// Minimal HTTP server that receives export results from Blender's Quick Export button
/// and applies them to Penumbra as the persistent XIV Instant Edit mod.
/// </summary>
public sealed partial class ExportServer : IDisposable
{
    internal sealed class ExportRequest
    {
        [JsonPropertyName("schema")]
        public string? Schema { get; set; }

        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("pluginInstanceId")]
        public string? PluginInstanceId { get; set; }

        [JsonPropertyName("contextId")]
        public string? ContextId { get; set; }

        [JsonPropertyName("exportId")]
        public string? ExportId { get; set; }

        [JsonPropertyName("capability")]
        public string? Capability { get; set; }

        [JsonPropertyName("filePath")]
        public string? FilePath { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("sha256")]
        public string? Sha256 { get; set; }

        [JsonPropertyName("variantName")]
        public string? VariantName { get; set; }

        [JsonPropertyName("setupInPenumbra")]
        public bool SetupInPenumbra { get; set; }

        [JsonPropertyName("variantGroupName")]
        public string? VariantGroupName { get; set; }

        [JsonPropertyName("variantTarget")]
        public string? VariantTarget { get; set; }

        [JsonPropertyName("variantTargetId")]
        public string? VariantTargetId { get; set; }

        [JsonPropertyName("backupExisting")]
        public bool BackupExisting { get; set; }

        [JsonPropertyName("newModName")]
        public string? NewModName { get; set; }

        [JsonPropertyName("createAttributeGroups")]
        public bool CreateAttributeGroups { get; set; }

        [JsonPropertyName("attributeTags")]
        public List<string>? AttributeTags { get; set; }

        [JsonPropertyName("attributeMasks")]
        public Dictionary<string, int>? AttributeMasks { get; set; }

        /// <summary>EST entries the meshes need; null leaves the mod's alone, empty takes back the plugin's own.</summary>
        [JsonPropertyName("estEntries")]
        public List<EstEntryRequest>? EstEntries { get; set; }
    }

    private sealed class ReattachRequest
    {
        [JsonPropertyName("schema")]
        public string? Schema { get; set; }

        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("contextId")]
        public string? ContextId { get; set; }

        [JsonPropertyName("importId")]
        public string? ImportId { get; set; }

        [JsonPropertyName("capability")]
        public string? Capability { get; set; }
    }

    private sealed class MashupContributorRequest
    {
        [JsonPropertyName("contextId")]
        public string? ContextId { get; set; }

        [JsonPropertyName("capability")]
        public string? Capability { get; set; }

        [JsonPropertyName("materials")]
        public List<string>? Materials { get; set; }
    }

    private sealed class MashupExportRequest
    {
        [JsonPropertyName("schema")]
        public string? Schema { get; set; }
        [JsonPropertyName("version")]
        public int Version { get; set; }
        [JsonPropertyName("pluginInstanceId")]
        public string? PluginInstanceId { get; set; }
        [JsonPropertyName("contextId")]
        public string? ContextId { get; set; }
        [JsonPropertyName("exportId")]
        public string? ExportId { get; set; }
        [JsonPropertyName("capability")]
        public string? Capability { get; set; }
        [JsonPropertyName("filePath")]
        public string? FilePath { get; set; }
        [JsonPropertyName("size")]
        public long Size { get; set; }
        [JsonPropertyName("sha256")]
        public string? Sha256 { get; set; }
        [JsonPropertyName("destination")]
        public string? Destination { get; set; }
        [JsonPropertyName("bundleExternalDependencies")]
        public bool BundleExternalDependencies { get; set; }
        [JsonPropertyName("name")]
        public string? Name { get; set; }
        [JsonPropertyName("contributors")]
        public List<MashupContributorRequest>? Contributors { get; set; }

        [JsonPropertyName("planFingerprint")]
        public string? PlanFingerprint { get; set; }

        [JsonPropertyName("createAttributeGroups")]
        public bool CreateAttributeGroups { get; set; }

        [JsonPropertyName("attributeTags")]
        public List<string>? AttributeTags { get; set; }

        [JsonPropertyName("attributeMasks")]
        public Dictionary<string, int>? AttributeMasks { get; set; }

        [JsonPropertyName("estEntries")]
        public List<EstEntryRequest>? EstEntries { get; set; }
    }

    private sealed class MashupPlanRequest
    {
        [JsonPropertyName("schema")]
        public string? Schema { get; set; }
        [JsonPropertyName("version")]
        public int Version { get; set; }
        [JsonPropertyName("pluginInstanceId")]
        public string? PluginInstanceId { get; set; }
        [JsonPropertyName("contextId")]
        public string? ContextId { get; set; }
        [JsonPropertyName("capability")]
        public string? Capability { get; set; }
        [JsonPropertyName("destination")]
        public string? Destination { get; set; }
        [JsonPropertyName("bundleExternalDependencies")]
        public bool BundleExternalDependencies { get; set; }
        [JsonPropertyName("contributors")]
        public List<MashupContributorRequest>? Contributors { get; set; }
    }

    private sealed class RevokeRequest
    {
        [JsonPropertyName("schema")]
        public string? Schema { get; set; }

        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("contextId")]
        public string? ContextId { get; set; }

        [JsonPropertyName("importId")]
        public string? ImportId { get; set; }

        [JsonPropertyName("capability")]
        public string? Capability { get; set; }
    }

    private sealed class ExportStatusRequest
    {
        [JsonPropertyName("schema")]
        public string? Schema { get; set; }

        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("pluginInstanceId")]
        public string? PluginInstanceId { get; set; }

        [JsonPropertyName("contextId")]
        public string? ContextId { get; set; }

        [JsonPropertyName("exportId")]
        public string? ExportId { get; set; }

        [JsonPropertyName("capability")]
        public string? Capability { get; set; }
    }

    private sealed class VariantTargetsRequest
    {
        [JsonPropertyName("schema")]
        public string? Schema { get; set; }

        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("pluginInstanceId")]
        public string? PluginInstanceId { get; set; }

        [JsonPropertyName("contextId")]
        public string? ContextId { get; set; }

        [JsonPropertyName("capability")]
        public string? Capability { get; set; }
    }

    private sealed class MaterialCoverageRequest
    {
        [JsonPropertyName("schema")]
        public string? Schema { get; set; }

        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("pluginInstanceId")]
        public string? PluginInstanceId { get; set; }

        [JsonPropertyName("contextId")]
        public string? ContextId { get; set; }

        [JsonPropertyName("capability")]
        public string? Capability { get; set; }

        [JsonPropertyName("contributors")]
        public List<MashupContributorRequest>? Contributors { get; set; }
    }

    private sealed class BackupRestoreRequest
    {
        [JsonPropertyName("schema")]
        public string? Schema { get; set; }

        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("pluginInstanceId")]
        public string? PluginInstanceId { get; set; }

        [JsonPropertyName("contextId")]
        public string? ContextId { get; set; }

        [JsonPropertyName("capability")]
        public string? Capability { get; set; }

        [JsonPropertyName("backupName")]
        public string? BackupName { get; set; }

        [JsonPropertyName("backupTargetId")]
        public string? BackupTargetId { get; set; }

    }

    private sealed class ImportStatusRequest
    {
        [JsonPropertyName("schema")]
        public string? Schema { get; set; }
        [JsonPropertyName("version")]
        public int Version { get; set; }
        [JsonPropertyName("pluginInstanceId")]
        public string? PluginInstanceId { get; set; }
        [JsonPropertyName("contextId")]
        public string? ContextId { get; set; }
        [JsonPropertyName("importId")]
        public string? ImportId { get; set; }
        [JsonPropertyName("capability")]
        public string? Capability { get; set; }
        [JsonPropertyName("status")]
        public string? Status { get; set; }
        [JsonPropertyName("component")]
        public string? Component { get; set; }
        [JsonPropertyName("operation")]
        public string? Operation { get; set; }
        [JsonPropertyName("stage")]
        public string? Stage { get; set; }
        [JsonPropertyName("code")]
        public string? Code { get; set; }
        [JsonPropertyName("cause")]
        public string? Cause { get; set; }
        [JsonPropertyName("remedy")]
        public string? Remedy { get; set; }
        [JsonPropertyName("diagnosticId")]
        public string? DiagnosticId { get; set; }
    }

    private sealed record HttpRequest(string Method, string Path, byte[] Body);
    internal sealed record StagedExport(string FilePath, string DirectoryPath);

    private readonly Configuration    _config;
    private readonly ModelExportService _modelExports;
    private readonly RaceExportService _raceExports;
    private readonly SizesExportService _sizesExports;
    private readonly ModelBackupService _modelBackups;
    private readonly MashupService _mashups;
    private readonly ModResourceScanner _scanner;
    private readonly ExportContextRegistry _contexts;
    private readonly bool _ownsContexts;
    private readonly IPluginLog       _log;
    private readonly CancellationTokenSource _cts = new();
    private const int MaxRequestBytes = 256 * 1024;
    private const int MaxHeaderBytes = 64 * 1024;
    private const long MaxExportBytes = 512L * 1024 * 1024;

    private readonly object _listenerLock = new();
    private readonly SemaphoreSlim _clientGate = new(8, 8);
    private readonly HashSet<TcpClient> _clients = new();
    private readonly HashSet<Task> _clientTasks = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private bool _disposed;

    public event Action<BridgeFailure>? ImportFailureReceived;

    /// <summary> Serves the /painter/* requests from the Substance Painter plugin. </summary>
    private Painter.PainterJobService? _painter;

    internal void AttachPainter(Painter.PainterJobService painter) => _painter = painter;

    internal ExportServer(
        Configuration config,
        ModelExportService modelExports,
        RaceExportService raceExports,
        SizesExportService sizesExports,
        ModelBackupService modelBackups,
        MashupService mashups,
        ModResourceScanner scanner,
        ExportContextRegistry contexts,
        IPluginLog log)
    {
        _config   = config;
        _modelExports = modelExports;
        _raceExports = raceExports;
        _sizesExports = sizesExports;
        _modelBackups = modelBackups;
        _mashups = mashups;
        _scanner = scanner;
        _contexts = contexts;
        _ownsContexts = false;
        _log      = log;
    }

    /// <summary> Compatibility constructor for hosts that do not provide a shared registry. </summary>
    internal ExportServer(Configuration config, ModelExportService modelExports, RaceExportService raceExports,
        SizesExportService sizesExports, ModelBackupService modelBackups, MashupService mashups, ModResourceScanner scanner,
        IPluginLog log)
        : this(config, modelExports, raceExports, sizesExports, modelBackups, mashups, scanner,
            new ExportContextRegistry(Guid.NewGuid().ToString("N")), log)
        => _ownsContexts = true;

    public void Start()
    {
        CleanupStaleStagedExports();
        lock (_listenerLock)
        {
            if (_listener is not null || _cts.IsCancellationRequested)
                return;

            try
            {
                var listener = new TcpListener(IPAddress.Loopback, _config.ListenPort);
                listener.Start();
                _listener = listener;
                var runCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                _runCts = runCts;
                _runTask = Task.Run(() => RunAsync(listener, runCts.Token));
            }
            catch (Exception e)
            {
                _log.Error(e, $"Could not start export receiver on port {_config.ListenPort}.");
                _listener = null;
                _runCts?.Dispose();
                _runCts = null;
                _runTask = null;
                return;
            }
        }

        _log.Information($"XIV Instant Edit export receiver listening on port {_config.ListenPort}.");
    }

    /// <summary> Stop and start the listener using the current configuration. </summary>
    public void Restart()
    {
        StopListener();
        Start();
    }

    private async Task RunAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await _clientGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    client.Dispose();
                    throw;
                }

                Task task;
                lock (_listenerLock)
                {
                    if (cancellationToken.IsCancellationRequested || !ReferenceEquals(_listener, listener))
                    {
                        client.Dispose();
                        _clientGate.Release();
                        break;
                    }
                    _clients.Add(client);
                    task = Task.Run(() => HandleTrackedClientAsync(client, cancellationToken), CancellationToken.None);
                    _clientTasks.Add(task);
                }
                _ = task.ContinueWith(
                    completed =>
                    {
                        lock (_listenerLock)
                            _clientTasks.Remove(completed);
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;
                _log.Error(e, "Export receiver accept failed.");
                try
                {
                    await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task HandleTrackedClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var cancellation = cancellationToken.Register(static state =>
        {
            try { ((TcpClient)state!).Close(); } catch { }
        }, client);
        try
        {
            await HandleClient(client, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_listenerLock)
                _clients.Remove(client);
            _clientGate.Release();
        }
    }

    private async Task HandleClient(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        using (var stream = client.GetStream())
        {
            stream.ReadTimeout = 10_000;
            stream.WriteTimeout = 10_000;
            try
            {
                var (request, error) = ReadRequest(stream);
                if (error is not null || request is null)
                {
                    var browser = error == BrowserRequestRefused;
                    var failure = StructuredError(
                        browser ? 403 : 400,
                        "http_request",
                        "request_receipt",
                        error?.Code ?? ResultCode.MalformedHttpRequest,
                        browser ? "The plugin only accepts requests from local applications." : error?.Message ?? "The plugin could not read the bridge request.",
                        browser ? "Send requests from Blender or Substance Painter, not a web page." : "Update both XIV Instant Edit components and retry.");
                    WriteResponse(stream, failure.Status, failure.Body);
                    return;
                }

                cancellationToken.ThrowIfCancellationRequested();
                var (status, body) = await ProcessRequestAsync(request).ConfigureAwait(false);
                if (status >= 400)
                    body = EnrichErrorResponse(request.Path, status, body);
                cancellationToken.ThrowIfCancellationRequested();
                WriteResponse(stream, status, body);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Listener shutdown closes active sockets so blocked reads terminate promptly.
            }
            catch (Exception e)
            {
                try
                {
                    var failure = StructuredError(
                        500,
                        "http_request",
                        "processing",
                        ResultCode.InternalError,
                        "The Dalamud bridge encountered an unexpected server error.",
                        "Retry the operation and review the Dalamud plugin log if it fails again.",
                        exception: e);
                    WriteResponse(stream, failure.Status, failure.Body);
                }
                catch
                {
                    // The peer may already have disconnected.
                }
            }
        }
    }

    private async Task<(int Status, string Body)> ProcessRequestAsync(HttpRequest request)
    {
        var method = request.Method;
        var path   = request.Path;

        if (method == "GET" && path.TrimEnd('/') == "/status")
            return HandleStatus();

        if (method == "POST" && path.TrimEnd('/') == "/import/status")
            return HandleImportStatus(request);

        if (method == "POST" && path.TrimEnd('/') == "/context/reattach")
            return HandleContextReattach(request);

        if (method == "POST" && path.TrimEnd('/') == "/context/revoke")
            return HandleContextRevoke(request);

        if (method == "POST" && path.TrimEnd('/') == "/export/status")
            return await HandleExportStatusAsync(request).ConfigureAwait(false);

        if (method == "POST" && path.TrimEnd('/') == "/variant-targets")
            return await HandleVariantTargetsAsync(request).ConfigureAwait(false);

        if (method == "POST" && path.TrimEnd('/') == "/material-coverage")
            return await HandleMaterialCoverageAsync(request).ConfigureAwait(false);

        if (method == "POST" && path.TrimEnd('/') == "/backup/restore")
            return await HandleBackupRestoreAsync(request).ConfigureAwait(false);

        if (method == "POST" && path.TrimEnd('/') == "/backup/clear")
            return await HandleBackupClearAsync(request).ConfigureAwait(false);

        if (method == "POST" && path.TrimEnd('/') == "/mashup/plan")
            return HandleMashupPlan(request);

        if (method == "POST" && path.TrimEnd('/') == "/mashup/export")
            return await HandleMashupExportAsync(request).ConfigureAwait(false);

        if (method == "POST" && path.TrimEnd('/') == "/export")
            return await HandleExportAsync(request).ConfigureAwait(false);

        if (method == "POST" && path.TrimEnd('/') == "/export/sizes")
            return await HandleSizesExportAsync(request).ConfigureAwait(false);

        if (method == "POST" && path.TrimEnd('/') == "/export/race")
            return await HandleRaceExportAsync(request).ConfigureAwait(false);

        if (method == "POST" && path.StartsWith("/painter/", StringComparison.Ordinal) && _painter is { } painter)
            return painter.Handle(path.TrimEnd('/'), request.Body);

        if (method == "POST" && path.TrimEnd('/') == "/skeleton")
            return await HandleSkeletonAsync(request).ConfigureAwait(false);

        if (method == "POST" && path.TrimEnd('/') == "/racial-deformer")
            return await HandleRacialDeformerAsync(request).ConfigureAwait(false);

        return Error(404, ResultCode.EndpointNotFound, "the requested bridge endpoint was not found");
    }

    private (int Status, string Body) HandleStatus()
        => (200, Json(new
        {
            ok = true,
            running = true,
            target = "original_source_mod",
            capabilities = new[]
            {
                "instant-edit.context-reattach.v1",
                "instant-edit.context-revoke.v1",
                "instant-edit.export-status.v1",
                "instant-edit.variant-targets.v1",
                "instant-edit.material-coverage.v1",
                "instant-edit.backup-restore.v1",
                "instant-edit.structured-errors.v1",
                "instant-edit.import-status.v1",
                SkeletonCapability,
                RacialDeformerCapability,
                SizesExportCapability,
                RaceExportCapability,
            },
        }));

    private (int Status, string Body) HandleImportStatus(HttpRequest request)
    {
        var parsed = DeserializeRequest<ImportStatusRequest>(request.Body, "import status", out var parseError);
        if (parseError is not null)
            return parseError.Value;
        var status = parsed!;
        if (ValidateImportStatusEnvelope(status) is { } envelopeError)
            return Error(400, envelopeError, "unsupported or malformed import status envelope");

        if (!_contexts.TryAuthorizeOperation(
                status.PluginInstanceId!, status.ContextId!, status.Capability!,
                out var context, out var registryCode) || context is null)
            return Error(StatusForCode(registryCode), registryCode, "import status context was rejected");
        if (!string.Equals(context.ImportId, status.ImportId, StringComparison.Ordinal))
            return Error(401, ResultCode.ImportIdMismatch, "import status identifier was rejected");

        var failure = BridgeFailure.Create(
            "blender_addon",
            "import",
            status.Stage!,
            status.Code!,
            status.Cause!,
            status.Remedy!,
            diagnosticId: status.DiagnosticId);
        _log.Error(
            $"Blender bridge failure {failure.DiagnosticId}: " +
            $"{failure.Operation}/{failure.Stage}/{failure.Code}: {failure.Cause} Remedy: {failure.Remedy}");
        try
        {
            ImportFailureReceived?.Invoke(failure);
        }
        catch (Exception e)
        {
            _log.Error(e, $"Import failure notification handler failed for {failure.DiagnosticId}.");
        }
        return (200, Json(new { ok = true, code = ResultCode.ImportFailureRecorded }));
    }

    private (int Status, string Body) HandleContextReattach(HttpRequest request)
    {
        var parsed = DeserializeRequest<ReattachRequest>(request.Body, "context reattach", out var parseError);
        if (parseError is not null)
            return parseError.Value;
        var reattach = parsed!;

        if (ValidateReattachEnvelope(reattach) is { } reattachError)
            return Error(StatusForCode(reattachError), reattachError, "unsupported or malformed reattach envelope");

        if (!_contexts.TryReattach(
                reattach.ContextId!,
                reattach.ImportId!,
                reattach.Capability!,
                _config.ListenPort,
                out var context,
                out var registryCode))
            return Error(StatusForCode(registryCode), registryCode, "export context was rejected");

        return (200, Json(new { ok = true, code = registryCode, context }));
    }

    private (int Status, string Body) HandleContextRevoke(HttpRequest request)
    {
        var parsed = DeserializeRequest<RevokeRequest>(request.Body, "context revoke", out var parseError);
        if (parseError is not null)
            return parseError.Value;
        var revoke = parsed!;
        if (ValidateRevokeEnvelope(revoke) is { } envelopeError)
            return Error(400, envelopeError, "unsupported or malformed context revoke envelope");
        if (!_contexts.TryRevoke(revoke.ContextId!, revoke.ImportId!, revoke.Capability!, out var registryCode))
            return Error(StatusForCode(registryCode), registryCode, "export context revocation was rejected");
        return (200, Json(new { ok = true, code = registryCode }));
    }

    private async Task<(int Status, string Body)> HandleExportStatusAsync(HttpRequest request)
    {
        var parsed = DeserializeRequest<ExportStatusRequest>(request.Body, "export status", out var parseError);
        if (parseError is not null)
            return parseError.Value;
        var status = parsed!;
        if (ValidateExportStatusEnvelope(status) is { } envelopeError)
            return Error(400, envelopeError, "unsupported or malformed export status envelope");
        if (!_contexts.TryGetExportStatus(
                status.PluginInstanceId!,
                status.ContextId!,
                status.ExportId!,
                status.Capability!,
                out var completion,
                out var registryCode) || completion is null)
            return Error(StatusForCode(registryCode), registryCode, "export receipt was not found");
        if (!completion.IsCompleted)
            return (202, Json(new { ok = true, code = ResultCode.ExportPending, complete = false }));
        return ResultResponse(await completion.ConfigureAwait(false));
    }

    private async Task<(int Status, string Body)> HandleVariantTargetsAsync(HttpRequest request)
    {
        var parsed = DeserializeRequest<VariantTargetsRequest>(request.Body, "variant targets", out var parseError);
        if (parseError is not null)
            return parseError.Value;
        var targetsRequest = parsed!;
        if (ValidateVariantTargetsEnvelope(targetsRequest) is { } envelopeError)
            return Error(400, envelopeError, "unsupported or malformed variant-targets envelope");
        if (!_contexts.TryAuthorizeOperation(
                targetsRequest.PluginInstanceId!, targetsRequest.ContextId!, targetsRequest.Capability!,
                out var target, out var registryCode) || target is null)
            return Error(StatusForCode(registryCode), registryCode, "export context was rejected");
        if (target.DestinationState != InstantEditImportContext.ReadyDestination)
            return Error(409, ResultCode.DestinationNotReady, "create the Penumbra mod before loading variant targets");

        var result = await _modelExports.GetVariantTargetsAsync(
            target.SourceModDirectory!, target.TargetFilePath!, target.SourceModRootPath,
            target.TargetRelativePath, target.GamePath, target.SourceModStableId).ConfigureAwait(false);
        if (!result.Success)
            return Error(400, result.Code, result.Message);
        return (200, Json(new
        {
            ok = true,
            groups = result.Groups.Select(group => new
            {
                id = group.Id,
                name = group.Name,
                options = group.Options.Select(option => new
                {
                    id = option.Id,
                    name = option.Name,
                    modelPath = option.ModelPath,
                    backupTargetId = option.BackupTargetId,
                    backupDirectory = option.BackupDirectory,
                }),
            }),
        }));
    }

    private async Task<(int Status, string Body)> HandleMaterialCoverageAsync(HttpRequest request)
    {
        var parsed = DeserializeRequest<MaterialCoverageRequest>(request.Body, "material coverage", out var parseError);
        if (parseError is not null)
            return parseError.Value;
        var coverageRequest = parsed!;
        if (ValidateMaterialCoverageEnvelope(coverageRequest) is { } envelopeError)
            return Error(StatusForCode(envelopeError), envelopeError, "unsupported or malformed material-coverage envelope");
        if (!_contexts.TryAuthorizeOperation(
                coverageRequest.PluginInstanceId!, coverageRequest.ContextId!, coverageRequest.Capability!,
                out var activeContext, out var activeCode) || activeContext is null)
            return Error(StatusForCode(activeCode), activeCode, "active export context was rejected");
        if (activeContext.DestinationState != InstantEditImportContext.ReadyDestination)
            return Error(409, ResultCode.DestinationNotReady, "create the Penumbra mod before checking material coverage");

        var contributors = new List<MashupContributor>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var contributor in coverageRequest.Contributors!)
        {
            if (!seen.Add(contributor.ContextId!))
                return Error(400, ResultCode.DuplicateContext, "a material-coverage context was supplied more than once");
            if (!_contexts.TryAuthorizeOperation(
                    coverageRequest.PluginInstanceId!, contributor.ContextId!, contributor.Capability!,
                    out var contributorContext, out var contributorCode) || contributorContext is null)
                return Error(StatusForCode(contributorCode), contributorCode, "a material-coverage context was rejected");
            if (!IsMashupContributorState(contributorContext))
                return Error(409, ResultCode.DestinationNotReady, "a contributing Context is not ready for material coverage");
            contributors.Add(new MashupContributor(contributorContext, contributor.Materials!));
        }

        var coverage = await _scanner.GetMaterialCoverageAsync(activeContext, contributors).ConfigureAwait(false);
        return (200, Json(new
        {
            ok = true,
            code = coverage.Code,
            available = coverage.Available,
            covered = coverage.Covered,
            message = coverage.Message,
            missing = coverage.Missing.Select(item => new
            {
                contextId = item.ContextId,
                sourceModName = item.SourceModName,
                modelMaterial = item.ModelMaterial,
                gamePath = item.GamePath,
                resourceType = item.ResourceType,
            }),
        }));
    }

    private async Task<(int Status, string Body)> HandleBackupRestoreAsync(HttpRequest request)
    {
        var parsed = DeserializeRequest<BackupRestoreRequest>(request.Body, "backup restore", out var parseError);
        if (parseError is not null)
            return parseError.Value;
        var restore = parsed!;
        if (ValidateBackupRestoreEnvelope(restore) is { } restoreError)
            return Error(StatusForCode(restoreError), restoreError, "unsupported or malformed backup restore envelope");
        if (!_contexts.TryAuthorizeOperation(
                restore.PluginInstanceId!, restore.ContextId!, restore.Capability!,
                out var target, out var registryCode) || target is null)
            return Error(StatusForCode(registryCode), registryCode, "export context was rejected");
        if (target.DestinationState != InstantEditImportContext.ReadyDestination)
            return Error(409, ResultCode.DestinationNotReady, "the import has no Penumbra mod backup destination yet");

        var result = await _modelBackups.RestoreSourceBackupAsync(
            target.SourceModDirectory!,
            target.TargetFilePath!,
            target.SourceModRootPath,
            target.TargetRelativePath,
            target.GamePath,
            restore.BackupName!,
            restore.BackupTargetId!,
            target.SourceModStableId).ConfigureAwait(false);
        return ResultResponse(new ExportReceipt(
            result.Success,
            result.Code,
            result.Message,
            result.WarningList,
            result.TargetFilePath));
    }

    private async Task<(int Status, string Body)> HandleBackupClearAsync(HttpRequest request)
    {
        var parsed = DeserializeRequest<BackupRestoreRequest>(request.Body, "backup clear", out var parseError);
        if (parseError is not null)
            return parseError.Value;
        var clear = parsed!;
        if (!string.Equals(clear.Schema, "instant-edit.backup-clear", StringComparison.Ordinal) || clear.Version != 1 ||
            string.IsNullOrWhiteSpace(clear.BackupTargetId))
            return Error(400, ResultCode.InvalidBackupClear, "unsupported or malformed backup clear envelope");
        if (!_contexts.TryAuthorizeOperation(clear.PluginInstanceId!, clear.ContextId!, clear.Capability!,
                out var target, out var registryCode) || target is null)
            return Error(StatusForCode(registryCode), registryCode, "export context was rejected");
        if (target.DestinationState != InstantEditImportContext.ReadyDestination)
            return Error(409, ResultCode.DestinationNotReady, "the import has no Penumbra mod backup destination yet");
        var result = await _modelBackups.ClearManagedBackupsAsync(
            target.SourceModDirectory!, target.TargetFilePath!, target.SourceModRootPath,
            target.TargetRelativePath, target.GamePath, clear.BackupTargetId!, target.SourceModStableId).ConfigureAwait(false);
        return ResultResponse(new ExportReceipt(result.Success, result.Code, result.Message));
    }

    private (int Status, string Body) HandleMashupPlan(HttpRequest request)
    {
        var parsed = DeserializeRequest<MashupPlanRequest>(request.Body, "mashup plan", out var parseError);
        if (parseError is not null)
            return parseError.Value;
        var requestPlan = parsed!;
        if (ValidateMashupPlanEnvelope(requestPlan) is { } envelopeError)
            return Error(StatusForCode(envelopeError), envelopeError, "unsupported or malformed mashup plan envelope");
        if (!_contexts.TryAuthorizeOperation(
                requestPlan.PluginInstanceId!, requestPlan.ContextId!, requestPlan.Capability!,
                out var activeContext, out var activeCode) || activeContext is null)
            return Error(StatusForCode(activeCode), activeCode, "active export context was rejected");
        var planDestination = requestPlan.Destination ?? "active_mod";
        if (!IsMashupActiveState(activeContext, planDestination))
            return Error(409, ResultCode.DestinationNotReady, "the active Context is not ready for this mashup destination");

        var contributors = new List<MashupContributor>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var contributor in requestPlan.Contributors!)
        {
            if (!seen.Add(contributor.ContextId!))
                return Error(400, ResultCode.DuplicateContext, "a mashup context was supplied more than once");
            if (!_contexts.TryAuthorizeOperation(
                    requestPlan.PluginInstanceId!, contributor.ContextId!, contributor.Capability!,
                    out var contributorContext, out var contributorCode) || contributorContext is null)
                return Error(StatusForCode(contributorCode), contributorCode, "a contributor context was rejected");
            if (!IsMashupContributorState(contributorContext))
                return Error(409, ResultCode.DestinationNotReady, "a contributor Context is not ready for mashup export");
            contributors.Add(new MashupContributor(contributorContext, contributor.Materials!));
        }
        if (RacialScalingRefusal(contributors.Select(item => item.Context).Prepend(activeContext), GamePathRace(activeContext)) is { } planScaling)
            return Error(StatusForCode(planScaling.Code), planScaling.Code, planScaling.Message);

        var plan = MashupPlanning.BuildMashupPlan(
            activeContext, contributors, requestPlan.BundleExternalDependencies);
        if (!plan.Success)
            return Error(StatusForCode(plan.Code), plan.Code, plan.Message);
        return (200, Json(new
        {
            ok = true,
            code = plan.Code,
            message = plan.Message,
            planFingerprint = plan.Fingerprint,
            assignments = plan.Assignments.Select(item => new
            {
                contextId = item.ContextId,
                modelMaterial = item.ModelMaterial,
                alias = item.Alias,
                gamePath = item.GamePath,
                slot = item.Slot,
            }),
        }));
    }

    private async Task<(int Status, string Body)> HandleMashupExportAsync(HttpRequest request)
    {
        var parsed = DeserializeRequest<MashupExportRequest>(request.Body, "mashup export", out var parseError);
        if (parseError is not null)
            return parseError.Value;
        var mashup = parsed!;
        if (ValidateMashupEnvelope(mashup) is { } envelopeError)
            return Error(StatusForCode(envelopeError), envelopeError, "unsupported or malformed mashup envelope");

        if (!_contexts.TryAuthorizeOperation(
                mashup.PluginInstanceId!, mashup.ContextId!, mashup.Capability!,
                out var activeContext, out var activeCode) || activeContext is null)
            return Error(StatusForCode(activeCode), activeCode, "active export context was rejected");
        if (!IsMashupActiveState(activeContext, mashup.Destination!))
            return Error(409, ResultCode.DestinationNotReady, "the active Context is not ready for this mashup destination");

        var contributors = new List<MashupContributor>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var contributor in mashup.Contributors!)
        {
            if (!seen.Add(contributor.ContextId!))
                return Error(400, ResultCode.DuplicateContext, "a mashup context was supplied more than once");
            if (!_contexts.TryAuthorizeOperation(
                    mashup.PluginInstanceId!, contributor.ContextId!, contributor.Capability!,
                    out var contributorContext, out var contributorCode) || contributorContext is null)
                return Error(StatusForCode(contributorCode), contributorCode, "a contributor context was rejected");
            if (!IsMashupContributorState(contributorContext))
                return Error(409, ResultCode.DestinationNotReady, "a contributor Context is not ready for mashup export");
            contributors.Add(new MashupContributor(contributorContext, contributor.Materials!));
        }
        if (RacialScalingRefusal(contributors.Select(item => item.Context).Prepend(activeContext), GamePathRace(activeContext)) is { } exportScaling)
            return Error(StatusForCode(exportScaling.Code), exportScaling.Code, exportScaling.Message);

        var plan = MashupPlanning.BuildMashupPlan(
            activeContext, contributors, mashup.BundleExternalDependencies);
        if (!plan.Success)
            return Error(StatusForCode(plan.Code), plan.Code, plan.Message);
        if (!string.Equals(plan.Fingerprint, mashup.PlanFingerprint, StringComparison.OrdinalIgnoreCase))
            return Error(409, ResultCode.MashupPlanMismatch, "The mashup material plan changed; retry the export.");

        var fingerprintSource = JsonSerializer.Serialize(new
        {
            mashup.Destination,
            mashup.BundleExternalDependencies,
            mashup.Name,
            mashup.PlanFingerprint,
            contributors = mashup.Contributors!.Select(item => new
                {
                    item.ContextId,
                    materials = item.Materials,
                }),
            mashup.CreateAttributeGroups,
            mashup.AttributeTags,
            mashup.AttributeMasks,
            mashup.EstEntries,
        }, JsonOpts);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintSource)));
        if (!_contexts.TryBeginExport(
                mashup.PluginInstanceId!, mashup.ContextId!, mashup.ExportId!, mashup.Capability!,
                mashup.FilePath!, mashup.Size, mashup.Sha256!, out var reservation, out var registryCode,
                fingerprint))
            return Error(StatusForCode(registryCode), registryCode, "mashup export was rejected");
        if (reservation is null)
            return Error(500, ResultCode.InternalError, "mashup reservation was not created");
        if (!reservation.IsOwner)
            return ResultResponse(await reservation.Completion.ConfigureAwait(false));

        ExportReceipt receipt;
        StagedExport? staged = null;
        try
        {
            var stageResult = await StageExportFileAsync(
                mashup.FilePath!, mashup.Size, mashup.Sha256!).ConfigureAwait(false);
            staged = stageResult.Export;
            if (stageResult.Error is not null)
                receipt = new ExportReceipt(false, stageResult.Error.Value.Code, stageResult.Error.Value.Message);
            else
            {
                var result = await _mashups.ApplyMashupAsync(
                    activeContext,
                    contributors,
                    plan,
                    staged!.FilePath,
                    mashup.ExportId!,
                    mashup.Destination!,
                    mashup.Name!,
                    mashup.BundleExternalDependencies,
                    mashup.CreateAttributeGroups,
                    mashup.AttributeTags,
                    mashup.AttributeMasks,
                    mashup.EstEntries).ConfigureAwait(false);
                if (result.Success && result.PathRemap is { } pathRemap)
                    _contexts.RemapModPaths(
                        pathRemap.ModDirectory,
                        pathRemap.ModRoot,
                        pathRemap.RelativePaths);

                var warnings = result.WarningList.ToList();
                InstantEditImportContext? outputContext = null;
                if (result.Success && string.Equals(mashup.Destination, "new_mod", StringComparison.Ordinal))
                {
                    if (result.OutputModDirectory is null ||
                        result.OutputModRootPath is null ||
                        result.OutputTargetRelativePath is null ||
                        result.TargetFilePath is null)
                    {
                        warnings.Add(
                            "The mashup mod was created, but its Blender export context could not be registered: " +
                            "the Penumbra output paths were incomplete.");
                    }
                    else
                    {
                        try
                        {
                            outputContext = _contexts.CreateContext(
                                activeContext.GamePath,
                                activeContext.ObjectIndex,
                                result.OutputModDirectory,
                                result.TargetFilePath,
                                result.DestinationName ?? result.OutputModDirectory,
                                activeContext.CallbackPort,
                                result.OutputModRootPath,
                                result.OutputTargetRelativePath,
                                resourceManifest: result.OutputResourceManifest,
                                targetCollectionId: activeContext.TargetCollectionId,
                                targetCollectionName: activeContext.TargetCollectionName,
                                sourceModStableId: result.OutputModStableId,
                                resolvedGamePath: activeContext.ResolvedGamePath);
                        }
                        catch (Exception error)
                        {
                            _log.Error(error, "Mashup mod was created, but its Blender export context could not be registered.");
                            warnings.Add(
                                "The mashup mod was created, but its Blender export context could not be registered.");
                        }
                    }
                }
                receipt = new ExportReceipt(
                    result.Success,
                    result.Code,
                    result.Message,
                    warnings,
                    result.TargetFilePath,
                    result.DestinationName,
                    Context: outputContext,
                    RequiredExternalMods: result.RequiredExternalMods);
            }
        }
        catch (Exception e)
        {
            _log.Error(e, "Mashup processing failed.");
            receipt = new ExportReceipt(false, ResultCode.InternalError, "mashup processing failed");
        }
        finally
        {
            CleanupStagedExport(staged);
        }

        _contexts.CompleteExport(mashup.ContextId!, mashup.ExportId!, receipt);
        return ResultResponse(receipt);
    }

    private async Task<(int Status, string Body)> HandleExportAsync(HttpRequest request)
    {
            var parsed = DeserializeRequest<ExportRequest>(request.Body, "export", out var parseError);
            if (parseError is not null)
                return parseError.Value;
            var export = parsed!;

            if (ValidateEnvelope(export) is { } envelopeError)
                return Error(StatusForCode(envelopeError), envelopeError, "unsupported or malformed export envelope");

            var requestFingerprint = ExportRequestFingerprint(export);
            if (!_contexts.TryBeginExport(
                    export.PluginInstanceId!,
                    export.ContextId!,
                    export.ExportId!,
                    export.Capability!,
                    export.FilePath!,
                    export.Size,
                    export.Sha256!,
                    out var reservation,
                    out var registryCode,
                    requestFingerprint))
                return Error(StatusForCode(registryCode), registryCode, "export context was rejected");

            if (reservation is null)
                return Error(500, ResultCode.InternalError, "export reservation was not created");

            if (!reservation.IsOwner)
            {
                var duplicate = await reservation.Completion.ConfigureAwait(false);
                return ResultResponse(duplicate);
            }

            ExportReceipt receipt;
            StagedExport? staged = null;
            try
            {
                var stageResult = await StageExportFileAsync(
                    export.FilePath!,
                    export.Size,
                    export.Sha256!).ConfigureAwait(false);
                staged = stageResult.Export;
                if (stageResult.Error is not null)
                {
                    receipt = new ExportReceipt(
                        false,
                        stageResult.Error.Value.Code,
                        stageResult.Error.Value.Message);
                }
                else if (RacialScalingRefusal([reservation.Context], GamePathRace(reservation.Context)) is { } scalingRefusal)
                {
                    receipt = scalingRefusal;
                }
                else
                {
                    var target = reservation.Context;
                    receipt = await ApplyExport(
                        target,
                        staged!.FilePath,
                        export.VariantName,
                        export.VariantGroupName,
                        export.VariantTarget,
                        export.VariantTargetId,
                        export.SetupInPenumbra,
                        export.BackupExisting,
                        export.NewModName,
                        export.CreateAttributeGroups,
                        export.AttributeTags,
                        export.AttributeMasks,
                        export.EstEntries).ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                _log.Error(e, "Export processing failed.");
                receipt = new ExportReceipt(false, ResultCode.InternalError, "export processing failed");
            }
            finally
            {
                CleanupStagedExport(staged);
            }

            _contexts.CompleteExport(export.ContextId!, export.ExportId!, receipt);
            _log.Information($"Export {receipt.Code.Wire()}: {receipt.Message}");
            return ResultResponse(receipt);

        async Task<ExportReceipt> ApplyExport(
            InstantEditImportContext target,
            string filePath,
            string? variantName,
            string? variantGroupName,
            string? variantTarget,
            string? variantTargetId,
            bool setupVariantInPenumbra,
            bool backupExisting,
            string? newModName,
            bool createAttributeGroups,
            IReadOnlyList<string>? attributeTags,
            IReadOnlyDictionary<string, int>? attributeMasks,
            IReadOnlyList<EstEntryRequest>? estEntries)
        {
            if (target.DestinationState == InstantEditImportContext.NewModRequiredDestination)
            {
                if (string.IsNullOrWhiteSpace(newModName))
                    return new ExportReceipt(false, ResultCode.MissingNewModName, "enter a name for the new Penumbra mod");
                if (setupVariantInPenumbra || variantName is not null || variantTarget is not null || backupExisting)
                    return new ExportReceipt(false, ResultCode.InvalidPendingExport, "a first vanilla export cannot target variants or backups");
                return await CreateGameModelModAsync(
                    target, filePath, newModName, createAttributeGroups, attributeTags, attributeMasks, estEntries)
                    .ConfigureAwait(false);
            }
            if (newModName is not null)
                return new ExportReceipt(false, ResultCode.UnexpectedNewModName, "this import already has a Penumbra destination");
            if (string.IsNullOrWhiteSpace(target.TargetFilePath) ||
                string.IsNullOrWhiteSpace(target.SourceModDirectory))
                return new ExportReceipt(false, ResultCode.MissingSourceTarget, "the import has no original Penumbra mod destination");

            var result = await _modelExports.ApplySourceExportAsync(
                target.SourceModDirectory!,
                target.TargetFilePath!,
                target.SourceModRootPath,
                target.TargetRelativePath,
                target.GamePath,
                filePath,
                variantName,
                variantGroupName,
                variantTarget,
                variantTargetId,
                setupVariantInPenumbra,
                backupExisting,
                target.SourceOption,
                target.SourceOptionStatus,
                target.SourceModStableId,
                createAttributeGroups,
                attributeTags,
                attributeMasks,
                target.ResolvedGamePath,
                AttributeGroupService.KnownManipulations(target),
                estEntries).ConfigureAwait(false);
            return new ExportReceipt(
                result.Success,
                result.Code,
                result.Message,
                result.WarningList,
                result.TargetFilePath);
        }
    }

    /// <summary>
    /// A game model's first export: a new mod holding the exported model (as <paramref name="race"/>'s own
    /// model when given), with the import's context promoted to it.
    /// </summary>
    private async Task<ExportReceipt> CreateGameModelModAsync(
        InstantEditImportContext target,
        string filePath,
        string newModName,
        bool createAttributeGroups,
        IReadOnlyList<string>? attributeTags,
        IReadOnlyDictionary<string, int>? attributeMasks,
        IReadOnlyList<EstEntryRequest>? estEntries,
        int? race = null)
    {
        var created = await _modelExports.CreateGameModelModAsync(
                target, filePath, newModName, createAttributeGroups, attributeTags, attributeMasks, estEntries, race)
            .ConfigureAwait(false);
        if (!created.Result.Success)
            return new ExportReceipt(false, created.Result.Code, created.Result.Message, created.Result.WarningList);
        if (created.ModRoot is null || created.TargetRelativePath is null ||
            !_contexts.PromoteGameContext(
                target.ContextId,
                newModName,
                newModName,
                created.ModRoot,
                created.TargetRelativePath,
                out var promoted,
                created.ModStableId) || promoted is null)
            return new ExportReceipt(
                true,
                ResultCode.VanillaModCreatedWithWarnings,
                created.Result.Message,
                created.Result.WarningList.Concat(["The mod was created, but the Blender export context could not be promoted."]).ToArray(),
                created.Result.TargetFilePath,
                created.Result.DestinationName);
        return new ExportReceipt(
            true,
            created.Result.Code,
            created.Result.Message,
            created.Result.WarningList,
            created.Result.TargetFilePath,
            created.Result.DestinationName,
            promoted);
    }

    internal static string ExportRequestFingerprint(ExportRequest request)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            request.Schema,
            request.Version,
            request.VariantName,
            request.VariantGroupName,
            request.VariantTarget,
            request.VariantTargetId,
            request.SetupInPenumbra,
            request.BackupExisting,
            request.NewModName,
            request.CreateAttributeGroups,
            request.AttributeTags,
            request.AttributeMasks,
            request.EstEntries,
        })));

    private T? DeserializeRequest<T>(
        byte[] bodyBytes,
        string operation,
        out (int Status, string Body)? error)
        where T : class
    {
        error = null;
        string body;
        try
        {
            body = new UTF8Encoding(false, true).GetString(bodyBytes);
        }
        catch (DecoderFallbackException)
        {
            error = Error(400, ResultCode.InvalidUtf8, "request body is not valid UTF-8");
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = Error(400, ResultCode.RequestNotObject, "request body must be a JSON object");
                return null;
            }
            var value = JsonSerializer.Deserialize<T>(body, JsonOpts);
            if (value is null)
                error = Error(400, ResultCode.RequestNotObject, "request body must be a JSON object");
            return value;
        }
        catch (Exception e)
        {
            _log.Error(e, $"Failed to parse {operation} request.");
            error = Error(400, ResultCode.InvalidJson, "request body is not valid JSON");
            return null;
        }
    }

    internal static ResultCode? ValidateEnvelope(ExportRequest request)
    {
        if (!string.Equals(request.Schema, "instant-edit.export", StringComparison.Ordinal))
            return request.Version is 1 or 2 or 3 ? ResultCode.UnsupportedSchema : ResultCode.UnsupportedVersion;
        if (request.Version is not (1 or 2 or 3))
            return ResultCode.UnsupportedVersion;
        if (string.IsNullOrWhiteSpace(request.PluginInstanceId) ||
            string.IsNullOrWhiteSpace(request.ContextId) ||
            string.IsNullOrWhiteSpace(request.ExportId) ||
            string.IsNullOrWhiteSpace(request.Capability) ||
            string.IsNullOrWhiteSpace(request.FilePath) ||
            string.IsNullOrWhiteSpace(request.Sha256))
            return ResultCode.MissingField;
        if (request.Size <= 0 || request.Size > MaxExportBytes)
            return ResultCode.InvalidSize;
        if (request.Sha256.Length != 64 || request.Sha256.Any(c => !Uri.IsHexDigit(c)))
            return ResultCode.InvalidSha256;
        if (request.Version == 1 && request.NewModName is not null)
            return ResultCode.UnsupportedField;
        if (request.NewModName is not null && !ModPaths.IsSafeNewModName(request.NewModName))
            return ResultCode.InvalidModName;
        if (request.VariantName is not null && !IsSafeVariantName(request.VariantName))
            return ResultCode.InvalidVariantName;
        if (request.SetupInPenumbra && request.VariantName is null)
            if (!string.Equals(request.VariantTarget, "option", StringComparison.Ordinal))
                return ResultCode.PenumbraSetupRequiresVariant;
        if (request.SetupInPenumbra && !string.Equals(request.VariantTarget, "option", StringComparison.Ordinal) &&
            !ModPaths.IsSafeVariantGroupName(request.VariantGroupName))
            return ResultCode.PenumbraSetupRequiresGroupName;
        if (request.SetupInPenumbra && request.VariantTarget is not ("new_group" or "group" or "option"))
            return ResultCode.InvalidVariantTarget;
        if (request.SetupInPenumbra && request.VariantTarget is not "new_group" &&
            string.IsNullOrWhiteSpace(request.VariantTargetId))
            return ResultCode.MissingVariantTarget;
        return ValidateAttributeGroupEnvelope(
                   request.CreateAttributeGroups, request.AttributeTags, request.AttributeMasks) ??
               EstEntryService.ValidateEstEntryRequests(request.EstEntries);
    }

    /// <summary>Shared preamble for envelopes with exactly one supported schema/version pair.</summary>
    private static ResultCode? ValidateSchemaVersion(string? schema, string expectedSchema, int version, int expectedVersion)
    {
        if (!string.Equals(schema, expectedSchema, StringComparison.Ordinal))
            return version == expectedVersion ? ResultCode.UnsupportedSchema : ResultCode.UnsupportedVersion;
        return version != expectedVersion ? ResultCode.UnsupportedVersion : null;
    }

    private static ResultCode? ValidateImportStatusEnvelope(ImportStatusRequest request)
    {
        if (ValidateSchemaVersion(request.Schema, "instant-edit.import-status", request.Version, 1) is { } schemaError)
            return schemaError;
        if (!string.Equals(request.Status, "failed", StringComparison.Ordinal))
            return ResultCode.InvalidStatus;
        if (!string.Equals(request.Component, "blender_addon", StringComparison.Ordinal))
            return ResultCode.InvalidComponent;
        if (!string.Equals(request.Operation, "import", StringComparison.Ordinal))
            return ResultCode.InvalidOperation;
        if (string.IsNullOrWhiteSpace(request.PluginInstanceId) || !IsSafeId(request.ContextId) ||
            !IsSafeId(request.ImportId) || string.IsNullOrWhiteSpace(request.Capability))
            return ResultCode.MissingField;
        if (!IsSafeDiagnosticValue(request.Stage, 64))
            return ResultCode.InvalidStage;
        if (!IsSafeDiagnosticValue(request.Code, 128))
            return ResultCode.InvalidErrorCode;
        if (string.IsNullOrWhiteSpace(request.Cause) || request.Cause.Length > 2048)
            return ResultCode.InvalidCause;
        if (string.IsNullOrWhiteSpace(request.Remedy) || request.Remedy.Length > 2048)
            return ResultCode.InvalidRemedy;
        return BridgeFailure.IsDiagnosticId(request.DiagnosticId) ? null : ResultCode.InvalidDiagnosticId;
    }

    private static ResultCode? ValidateRevokeEnvelope(RevokeRequest request)
    {
        if (ValidateSchemaVersion(request.Schema, "instant-edit.context-revoke", request.Version, 1) is { } schemaError)
            return schemaError;
        return !IsSafeId(request.ContextId) || !IsSafeId(request.ImportId) ||
               string.IsNullOrWhiteSpace(request.Capability)
            ? ResultCode.MissingField
            : null;
    }

    private static ResultCode? ValidateExportStatusEnvelope(ExportStatusRequest request)
    {
        if (ValidateSchemaVersion(request.Schema, "instant-edit.export-status", request.Version, 1) is { } schemaError)
            return schemaError;
        return string.IsNullOrWhiteSpace(request.PluginInstanceId) || !IsSafeId(request.ContextId) ||
               !IsSafeId(request.ExportId) || string.IsNullOrWhiteSpace(request.Capability)
            ? ResultCode.MissingField
            : null;
    }

    private static ResultCode? ValidateVariantTargetsEnvelope(VariantTargetsRequest request)
    {
        if (ValidateSchemaVersion(request.Schema, "instant-edit.variant-targets", request.Version, 1) is { } schemaError)
            return schemaError;
        return string.IsNullOrWhiteSpace(request.PluginInstanceId) || !IsSafeId(request.ContextId) ||
               string.IsNullOrWhiteSpace(request.Capability)
            ? ResultCode.MissingField
            : null;
    }

    private static ResultCode? ValidateMashupEnvelope(MashupExportRequest request)
    {
        if (ValidateSchemaVersion(request.Schema, "instant-edit.mashup-export", request.Version, 2) is { } schemaError)
            return schemaError;
        if (string.IsNullOrWhiteSpace(request.PluginInstanceId) || !IsSafeId(request.ContextId) ||
            !IsSafeId(request.ExportId) || string.IsNullOrWhiteSpace(request.Capability) ||
            string.IsNullOrWhiteSpace(request.FilePath) || string.IsNullOrWhiteSpace(request.Sha256) ||
            string.IsNullOrWhiteSpace(request.PlanFingerprint) ||
            (request.Destination is not null && request.Destination is not ("active_mod" or "new_mod")) ||
            !ModPaths.IsSafeVariantGroupName(request.Name))
            return ResultCode.MissingField;
        if (request.Size <= 0 || request.Size > MaxExportBytes || request.Sha256.Length != 64 ||
            request.Sha256.Any(c => !Uri.IsHexDigit(c)) || request.PlanFingerprint.Length != 64 ||
            request.PlanFingerprint.Any(c => !Uri.IsHexDigit(c)))
            return ResultCode.InvalidExportFile;
        if (request.Contributors is not { Count: >= 1 and <= 16 })
            return ResultCode.InvalidContributors;
        foreach (var contributor in request.Contributors)
        {
            if (!IsSafeId(contributor.ContextId) || string.IsNullOrWhiteSpace(contributor.Capability) ||
                contributor.Materials is not { Count: >= 1 and <= 256 } ||
                contributor.Materials.Any(material => string.IsNullOrWhiteSpace(material) || material.Length > 512))
                return ResultCode.InvalidContributors;
        }
        return ValidateAttributeGroupEnvelope(
                   request.CreateAttributeGroups, request.AttributeTags, request.AttributeMasks) ??
               EstEntryService.ValidateEstEntryRequests(request.EstEntries);
    }

    private static ResultCode? ValidateAttributeGroupEnvelope(
        bool createAttributeGroups,
        IReadOnlyList<string>? tags,
        IReadOnlyDictionary<string, int>? masks)
    {
        if (!createAttributeGroups && (tags is not null || masks is not null))
            return ResultCode.UnsupportedField;
        if (tags is { Count: > 512 } || masks is { Count: > 512 } ||
            (tags is not null && tags.Any(tag => string.IsNullOrWhiteSpace(tag) || tag.Length > 128)) ||
            (masks is not null && masks.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 128)))
            return ResultCode.InvalidAttributeGroups;
        return null;
    }

    private static ResultCode? ValidateMashupPlanEnvelope(MashupPlanRequest request)
    {
        if (ValidateSchemaVersion(request.Schema, "instant-edit.mashup-plan", request.Version, 1) is { } schemaError)
            return schemaError;
        if (string.IsNullOrWhiteSpace(request.PluginInstanceId) || !IsSafeId(request.ContextId) ||
            string.IsNullOrWhiteSpace(request.Capability) ||
            request.Destination is not ("active_mod" or "new_mod") ||
            request.Contributors is not { Count: >= 1 and <= 16 })
            return ResultCode.InvalidContributors;
        foreach (var contributor in request.Contributors)
        {
            if (!IsSafeId(contributor.ContextId) || string.IsNullOrWhiteSpace(contributor.Capability) ||
                contributor.Materials is not { Count: >= 1 and <= 256 } ||
                contributor.Materials.Any(material => string.IsNullOrWhiteSpace(material) || material.Length > 512))
                return ResultCode.InvalidContributors;
        }
        return null;
    }

    private static bool IsMashupActiveState(InstantEditImportContext context, string destination)
        => destination == "active_mod"
            ? context.DestinationState == InstantEditImportContext.ReadyDestination
            : destination == "new_mod" && IsMashupContributorState(context);

    private static bool IsMashupContributorState(InstantEditImportContext context)
        => context.DestinationState is InstantEditImportContext.ReadyDestination or
            InstantEditImportContext.NewModRequiredDestination;

    private static ResultCode? ValidateMaterialCoverageEnvelope(MaterialCoverageRequest request)
    {
        if (ValidateSchemaVersion(request.Schema, "instant-edit.material-coverage", request.Version, 1) is { } schemaError)
            return schemaError;
        if (string.IsNullOrWhiteSpace(request.PluginInstanceId) || request.PluginInstanceId.Length > 128 ||
            !IsSafeId(request.ContextId) || string.IsNullOrWhiteSpace(request.Capability) ||
            request.Capability.Length > 512 ||
            request.Contributors is not { Count: >= 1 and <= 16 })
            return ResultCode.InvalidContributors;
        var materialCount = 0;
        foreach (var contributor in request.Contributors)
        {
            if (!IsSafeId(contributor.ContextId) || string.IsNullOrWhiteSpace(contributor.Capability) ||
                contributor.Capability.Length > 512 ||
                contributor.Materials is not { Count: >= 1 and <= 256 } ||
                contributor.Materials.Any(material => string.IsNullOrWhiteSpace(material) || material.Length > 512))
                return ResultCode.InvalidContributors;
            materialCount += contributor.Materials.Count;
        }
        return materialCount <= 4096 ? null : ResultCode.InvalidContributors;
    }

    private static ResultCode? ValidateBackupRestoreEnvelope(BackupRestoreRequest request)
    {
        if (!string.Equals(request.Schema, "instant-edit.backup-restore", StringComparison.Ordinal))
            return request.Version == 1 ? ResultCode.UnsupportedSchema : ResultCode.UnsupportedVersion;
        if (request.Version != 2)
            return ResultCode.UnsupportedVersion;
        if (string.IsNullOrWhiteSpace(request.PluginInstanceId) ||
            string.IsNullOrWhiteSpace(request.ContextId) ||
            string.IsNullOrWhiteSpace(request.Capability) ||
            string.IsNullOrWhiteSpace(request.BackupName) ||
            string.IsNullOrWhiteSpace(request.BackupTargetId))
            return ResultCode.MissingField;
        if (request.BackupName.Length > 512 ||
            request.BackupName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            request.BackupName.Contains('/') || request.BackupName.Contains('\\'))
            return ResultCode.InvalidBackupName;
        return null;
    }

    private static ResultCode? ValidateReattachEnvelope(ReattachRequest request)
    {
        if (!string.Equals(request.Schema, "instant-edit.reattach", StringComparison.Ordinal))
            return request.Version == 1 ? ResultCode.UnsupportedSchema : ResultCode.UnsupportedVersion;
        if (request.Version != 1)
            return ResultCode.UnsupportedVersion;
        if (!IsSafeId(request.ContextId) || !IsSafeId(request.ImportId) ||
            string.IsNullOrWhiteSpace(request.Capability))
            return ResultCode.MissingField;
        return null;
    }

    private static bool IsSafeVariantName(string value)
        => PathRules.IsSafeVariantName(value);

    private static bool IsSafeId(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
           value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    internal static async Task<(StagedExport? Export, (ResultCode Code, string Message)? Error)> StageExportFileAsync(
        string filePath,
        long expectedSize,
        string expectedSha256)
    {
        string fullPath;
        StagedExport? staged = null;
        var retainStaged = false;
        try
        {
            if (string.IsNullOrWhiteSpace(filePath) || filePath.Length > 4096 || filePath.Contains('\0') ||
                !Path.IsPathRooted(filePath) || filePath.StartsWith("\\\\", StringComparison.Ordinal) ||
                !string.Equals(Path.GetExtension(filePath), ".mdl", StringComparison.OrdinalIgnoreCase) ||
                filePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or ".."))
                return (null, (ResultCode.UnsafeFilePath, "filePath must be a local absolute .mdl path"));

            fullPath = Path.GetFullPath(filePath);
            var parent = Path.GetDirectoryName(fullPath);
            if (!File.Exists(fullPath))
                return (null, (ResultCode.FileNotFound, "export file was not found"));
            if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0 ||
                (parent is not null && HasReparsePointInPath(parent)))
                return (null, (ResultCode.UnsafeFilePath, "filePath must not be a reparse point"));

            var info = new FileInfo(fullPath);
            if (info.Length != expectedSize)
                return (null, (ResultCode.SizeMismatch, "export file size did not match the request"));
            if (info.Length > MaxExportBytes)
                return (null, (ResultCode.InvalidSize, "export file is too large"));

            var stagingRoot = Path.Combine(Path.GetTempPath(), "InstantEdit", "plugin-exports");
            Directory.CreateDirectory(stagingRoot);
            if ((File.GetAttributes(stagingRoot) & FileAttributes.ReparsePoint) != 0 ||
                HasReparsePointInPath(Path.GetDirectoryName(stagingRoot)!))
                return (null, (ResultCode.InternalError, "plugin export staging directory is unsafe"));
            var stagingDirectory = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingDirectory);
            staged = new StagedExport(Path.Combine(stagingDirectory, "model.mdl"), stagingDirectory);

            await using var source = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan);
            await using var destination = new FileStream(
                staged.FilePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.SequentialScan);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long copied = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0)
                    break;
                copied += read;
                if (copied > MaxExportBytes)
                {
                    return (null, (ResultCode.InvalidSize, "export file is too large"));
                }
                sha.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            }
            await destination.FlushAsync().ConfigureAwait(false);

            if (copied != expectedSize)
            {
                return (null, (ResultCode.SizeMismatch, "export file size changed during staging"));
            }
            var actual = Convert.ToHexString(sha.GetHashAndReset());
            if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                return (null, (ResultCode.HashMismatch, "export file hash did not match the request"));
            }
            retainStaged = true;
            return (staged, null);
        }
        catch (UnauthorizedAccessException)
        {
            return (null, (ResultCode.UnsafeFilePath, "export file is not readable"));
        }
        catch (IOException)
        {
            return (null, (ResultCode.FileNotReadable, "export file could not be staged"));
        }
        catch (ArgumentException)
        {
            return (null, (ResultCode.UnsafeFilePath, "filePath is invalid"));
        }
        finally
        {
            if (!retainStaged)
                CleanupStagedExport(staged);
        }
    }

    /// <summary>
    /// A racially scaled import is shaped for the character's race, so it can only be written as a model
    /// of that race: an export, or a mashup with one, into a model of <paramref name="targetRace"/> (the
    /// race in the written game path, null for a path without one) is refused before anything is written
    /// when an import was scaled for another race. Null when that's fine.
    /// </summary>
    internal static ExportReceipt? RacialScalingRefusal(IEnumerable<InstantEditImportContext> contexts, ushort? targetRace)
    {
        if (contexts.Select(context => context.RacialScaling)
                .FirstOrDefault(scaling => scaling is not null && scaling.CharacterRace != targetRace) is not { } record)
            return null;
        var target = targetRace is { } race ? $"a c{race:D4} model" : "this model";
        return new ExportReceipt(false, ResultCode.RacialScalingMismatch,
            $"This model was sent to Blender racially scaled (c{record.ModelRace:D4} to {GameRaces.Label(record.CharacterRace)}), " +
            $"so it can't go into {target}. Pick Save as c{record.CharacterRace:D4} Model as the target.");
    }

    /// <summary>The race of a context's model game path (201 for c0201e6000_top.mdl), or null.</summary>
    internal static ushort? GamePathRace(InstantEditImportContext context)
        => ModelSkeletonPaths.Parse(context.GamePath) is { Human: true } key ? key.GenderRace : null;

    internal static void CleanupStagedExport(StagedExport? staged)
    {
        if (staged is null)
            return;
        try
        {
            if (Directory.Exists(staged.DirectoryPath))
                Directory.Delete(staged.DirectoryPath, true);
        }
        catch
        {
            // Crash/stale staging cleanup is best-effort; it must never change
            // an already recorded export result.
        }
    }

    private static void CleanupStaleStagedExports()
    {
        var root = Path.Combine(Path.GetTempPath(), "InstantEdit", "plugin-exports");
        try
        {
            if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0 ||
                HasReparsePointInPath(Path.GetDirectoryName(root)!))
                return;
            var cutoff = DateTime.UtcNow - TimeSpan.FromDays(1);
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                var info = new DirectoryInfo(directory);
                if (!Guid.TryParseExact(info.Name, "N", out _) ||
                    (info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LastWriteTimeUtc >= cutoff)
                    continue;
                info.Delete(true);
            }
        }
        catch
        {
            // Cleanup is best-effort and never prevents the listener starting.
        }
    }

    private static bool HasReparsePointInPath(string path)
    {
        var current = new DirectoryInfo(path);
        while (current is not null)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                return true;
            current = current.Parent;
        }

        return false;
    }

    private (int Status, string Body) StructuredError(
        int status,
        string operation,
        string stage,
        ResultCode code,
        string cause,
        string remedy,
        string? diagnosticId = null,
        Exception? exception = null)
    {
        var failure = BridgeFailure.Create(
            "dalamud_plugin", operation, stage, code.Wire(), cause, remedy, status, diagnosticId);
        var message =
            $"Bridge failure {failure.DiagnosticId}: HTTP {status}; " +
            $"{failure.Operation}/{failure.Stage}/{failure.Code}: {failure.Cause} Remedy: {failure.Remedy}";
        if (exception is null)
            _log.Error(message);
        else
            _log.Error(exception, message);
        return (status, Json(new
        {
            ok = false,
            error = failure.Cause,
            component = failure.Component,
            operation = failure.Operation,
            stage = failure.Stage,
            code = failure.Code,
            cause = failure.Cause,
            remedy = failure.Remedy,
            diagnosticId = failure.DiagnosticId,
            componentVersion = BlenderClient.CurrentPluginVersion,
        }));
    }

    private string EnrichErrorResponse(string path, int status, string body)
    {
        var code = ResultCode.RequestRejected;
        var cause = "The Dalamud plugin rejected the bridge request.";
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                // Every error body comes from Error(), so its code is always one of ours.
                if (document.RootElement.TryGetProperty("code", out var codeValue) &&
                    codeValue.ValueKind == JsonValueKind.String && ResultCodes.TryParse(codeValue.GetString(), out var parsed))
                    code = parsed;
                if (document.RootElement.TryGetProperty("error", out var errorValue) &&
                    errorValue.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(errorValue.GetString()))
                    cause = errorValue.GetString()!;
            }
        }

        catch (JsonException)
        {
            code = ResultCode.InvalidErrorResponse;
            cause = "The Dalamud plugin produced an invalid error response.";
        }

        return StructuredError(
            status,
            OperationForPath(path),
            StageForCode(code),
            code,
            cause,
            RemedyForCode(code)).Body;
    }

    private static string OperationForPath(string path)
        => path.TrimEnd('/') switch
        {
            "/context/reattach" => "context_reattach",
            "/context/revoke" => "context_revoke",
            "/import/status" => "import_status",
            "/export/status" => "export_status",
            "/variant-targets" => "variant_targets",
            "/material-coverage" => "material_coverage",
            "/backup/restore" => "backup_restore",
            "/backup/clear" => "backup_clear",
            "/mashup/plan" => "mashup_plan",
            "/mashup/export" => "mashup_export",
            "/export" => "export",
            "/export/sizes" => "export_sizes",
            "/export/race" => "export_race",
            "/skeleton" => "skeleton",
            "/racial-deformer" => "racial_deformer",
            _ => "http_request",
        };

    // Stage and remedy say where a failed request stopped and what to do about it. Both switches list
    // every code, so a new code warns (CS8509) until it has one. Success codes never reach the error
    // path; they sit in the last arm with the other codes that have no specific stage or remedy.
#pragma warning disable CS8524 // Only the named codes exist; no other value reaches these switches.
    private static string StageForCode(ResultCode code) => code switch
    {
        ResultCode.EndpointNotFound => "routing",
        ResultCode.InvalidUtf8 or ResultCode.InvalidJson => "request_parsing",
        ResultCode.MalformedHttpRequest or ResultCode.InvalidErrorResponse or ResultCode.RequestNotObject or
            ResultCode.UnsupportedSchema or ResultCode.UnsupportedVersion or ResultCode.UnsupportedField or
            ResultCode.MissingField or ResultCode.InvalidSize or ResultCode.InvalidSha256 or
            ResultCode.InvalidModName or ResultCode.InvalidVariantName or ResultCode.InvalidVariantTarget or
            ResultCode.InvalidStatus or ResultCode.InvalidComponent or ResultCode.InvalidOperation or
            ResultCode.InvalidStage or ResultCode.InvalidErrorCode or ResultCode.InvalidCause or
            ResultCode.InvalidRemedy or ResultCode.InvalidDiagnosticId or ResultCode.InvalidExportFile or
            ResultCode.InvalidContributors or ResultCode.InvalidAttributeGroups or ResultCode.InvalidEstEntries or
            ResultCode.InvalidBackupName or ResultCode.InvalidBackupClear or ResultCode.InvalidRace or
            ResultCode.InvalidSizes or ResultCode.InvalidSizeName or ResultCode.InvalidDefaultSize or
            ResultCode.InvalidModelPath or ResultCode.InvalidContext or ResultCode.InvalidCapability or
            ResultCode.InvalidCallbackPort or ResultCode.InvalidExportId or ResultCode.InvalidPendingExport or
            ResultCode.InvalidDestinationState or ResultCode.InvalidVariant or ResultCode.InvalidVariantGroup or
            ResultCode.InvalidRestore or ResultCode.InvalidBackupTarget or ResultCode.InvalidMashup or
            ResultCode.InvalidRequest or ResultCode.InvalidSend or ResultCode.InvalidSuccessResponse => "request_validation",
        ResultCode.StaleContext or ResultCode.PluginInstanceMismatch or ResultCode.ImportIdMismatch => "authorization",
        ResultCode.UnsafeFilePath or ResultCode.FileNotFound or ResultCode.FileNotReadable or
            ResultCode.SizeMismatch or ResultCode.HashMismatch => "file_staging",
        ResultCode.DuplicateExportId => "reservation",
        ResultCode.ExportNotFound => "receipt_lookup",
        ResultCode.DestinationNotReady or ResultCode.VanillaModExists or ResultCode.SizesNeedMod or
            ResultCode.MashupPlanMismatch => "destination_validation",
        ResultCode.RacialScalingMismatch or ResultCode.RaceModelUnavailable => "racial_scaling",
        ResultCode.InternalError => "processing",
        ResultCode.RequestHeadersInvalid or ResultCode.RequestLineInvalid or ResultCode.ContentLengthInvalid or
            ResultCode.RequestBodyTooLarge or ResultCode.RequestTooLarge or ResultCode.RequestBodyIncomplete or
            ResultCode.RequestForbidden or ResultCode.RequestRejected or ResultCode.MissingVariantTarget or
            ResultCode.PenumbraSetupRequiresVariant or ResultCode.PenumbraSetupRequiresGroupName or
            ResultCode.DuplicateContext or ResultCode.Accepted or ResultCode.ServerStopped or
            ResultCode.ContextReattached or ResultCode.ContextRevoked or ResultCode.ContextRemoved or
            ResultCode.DuplicateExport or ResultCode.ExportPending or ResultCode.ExportComplete or
            ResultCode.ImportFailureRecorded or ResultCode.ExportApplied or ResultCode.ExportAppliedWithWarnings or
            ResultCode.ApplyFailed or ResultCode.DestinationUnsafe or ResultCode.MissingNewModName or
            ResultCode.UnexpectedNewModName or ResultCode.MissingSourceTarget or ResultCode.StaleVariantTarget or
            ResultCode.AmbiguousSourceOption or ResultCode.SourceOptionUnavailable or ResultCode.WriteFailed or
            ResultCode.PenumbraRootMissing or ResultCode.PenumbraRootUnsafe or ResultCode.PenumbraUnavailable or
            ResultCode.VanillaModCreated or ResultCode.VanillaModCreatedWithWarnings or
            ResultCode.VanillaModCreateFailed or ResultCode.RaceModelExported or
            ResultCode.RaceModelExportedWithWarnings or ResultCode.SizesExported or
            ResultCode.SizesExportedWithWarnings or ResultCode.AttributeGroupConflict or
            ResultCode.AttributeGroupWriteFailed or ResultCode.DestinationUnavailable or
            ResultCode.DestinationUnresolvable or ResultCode.DestinationAmbiguous or ResultCode.DestinationMissing or
            ResultCode.SourceModMissing or ResultCode.SourceModIdentityUnavailable or ResultCode.SourceModChanged or
            ResultCode.SourceModAmbiguous or ResultCode.VariantTargetUnavailable or ResultCode.VariantTargetsLoaded or
            ResultCode.VariantTargetsUnavailable or ResultCode.BackupUnavailable or ResultCode.BackupMissing or
            ResultCode.RestoreWriteFailed or ResultCode.RestoreFailed or ResultCode.BackupRestored or
            ResultCode.BackupRestoredWithWarnings or ResultCode.BackupsCleared or ResultCode.BackupClearFailed or
            ResultCode.MashupReimportRequired or ResultCode.MashupMaterialMissing or
            ResultCode.MashupMaterialPathUnsafe or ResultCode.MashupMaterialAliasUnsafe or
            ResultCode.MashupMaterialAliasConflict or ResultCode.MashupMaterialPathConflict or
            ResultCode.MashupTargetMaterialDirectoryAmbiguous or ResultCode.MashupTargetMaterialDirectoryInvalid or
            ResultCode.MashupTargetMaterialFamilyAmbiguous or ResultCode.MashupMaterialSlotsExhausted or
            ResultCode.MashupPlanReady or ResultCode.MashupModelMismatch or ResultCode.MashupTextureConflict or
            ResultCode.MashupSourceChanged or ResultCode.MashupMaterialRewriteFailed or
            ResultCode.MashupMaterialHashConflict or ResultCode.MashupMappingConflict or ResultCode.MashupFailed or
            ResultCode.MashupDestinationExists or ResultCode.MashupApplied or ResultCode.MashupAppliedWithWarnings or
            ResultCode.MashupWriteFailed or ResultCode.MashupModExists or ResultCode.MashupModCreated or
            ResultCode.MashupModCreatedWithWarnings or ResultCode.MashupModCreateFailed or
            ResultCode.MaterialCoverageComplete or ResultCode.MaterialCoverageMissing or
            ResultCode.MaterialCoverageUnavailable or ResultCode.SkeletonsUnavailable or ResultCode.SkeletonTimeout or
            ResultCode.SkeletonNotFound or ResultCode.RacialScalingUnavailable or ResultCode.RacialDeformerTimeout or
            ResultCode.UnknownProject or ResultCode.RequestFailed or ResultCode.DuplicateSend or
            ResultCode.UnknownSend or ResultCode.BlenderConnectionFailed or ResultCode.BlenderRequestTimeout or
            ResultCode.LegacyHttpError => "external_service",
    };

    private const string RemedyEndpoint = "Update both XIV Instant Edit components and verify the configured plugin port.";
    private const string RemedyUpdate = "Update and restart both XIV Instant Edit components, then retry.";
    private const string RemedyReimport = "Re-import the model from the current plugin session and retry.";
    private const string RemedyCache = "Retry the export and verify the Blender cache directory is accessible.";
    private const string RemedyExportAgain = "Export the model again; the staged file changed before the plugin could read it.";
    private const string RemedyDestination = "Select or create a valid Penumbra destination, then retry.";
    private const string RemedyMashupPlan = "Refresh the mashup plan and retry the export.";
    private const string RemedySizesNeedMod = "Export once with Magic Fit Sizes off to create the mod, then export the sizes.";
    private const string RemedyRacialScaling = "Export it as a model of the race it was scaled for, or re-import it with racial scaling off.";
    private const string RemedyInternal = "Retry the operation and review the Dalamud plugin log if it fails again.";
    private const string RemedyReview = "Review the reported Penumbra or request details, correct them, and retry.";

    private static string RemedyForCode(ResultCode code) => code switch
    {
        ResultCode.EndpointNotFound => RemedyEndpoint,
        ResultCode.MalformedHttpRequest or ResultCode.RequestNotObject or ResultCode.UnsupportedSchema or
            ResultCode.UnsupportedVersion or ResultCode.UnsupportedField or ResultCode.MissingField => RemedyUpdate,
        ResultCode.StaleContext or ResultCode.InvalidCapability or ResultCode.PluginInstanceMismatch or
            ResultCode.ImportIdMismatch => RemedyReimport,
        ResultCode.UnsafeFilePath or ResultCode.FileNotFound or ResultCode.FileNotReadable => RemedyCache,
        ResultCode.SizeMismatch or ResultCode.HashMismatch => RemedyExportAgain,
        ResultCode.DestinationNotReady or ResultCode.VanillaModExists => RemedyDestination,
        ResultCode.MashupPlanMismatch => RemedyMashupPlan,
        ResultCode.SizesNeedMod => RemedySizesNeedMod,
        ResultCode.RacialScalingMismatch => RemedyRacialScaling,
        ResultCode.InternalError => RemedyInternal,
        ResultCode.RequestHeadersInvalid or ResultCode.RequestLineInvalid or ResultCode.ContentLengthInvalid or
            ResultCode.RequestBodyTooLarge or ResultCode.RequestTooLarge or ResultCode.RequestBodyIncomplete or
            ResultCode.RequestForbidden or ResultCode.RequestRejected or ResultCode.InvalidErrorResponse or
            ResultCode.InvalidUtf8 or ResultCode.InvalidJson or ResultCode.InvalidSize or ResultCode.InvalidSha256 or
            ResultCode.InvalidModName or ResultCode.InvalidVariantName or ResultCode.InvalidVariantTarget or
            ResultCode.MissingVariantTarget or ResultCode.PenumbraSetupRequiresVariant or
            ResultCode.PenumbraSetupRequiresGroupName or ResultCode.InvalidStatus or ResultCode.InvalidComponent or
            ResultCode.InvalidOperation or ResultCode.InvalidStage or ResultCode.InvalidErrorCode or
            ResultCode.InvalidCause or ResultCode.InvalidRemedy or ResultCode.InvalidDiagnosticId or
            ResultCode.InvalidExportFile or ResultCode.InvalidContributors or ResultCode.InvalidAttributeGroups or
            ResultCode.InvalidEstEntries or ResultCode.InvalidBackupName or ResultCode.InvalidBackupClear or
            ResultCode.DuplicateContext or ResultCode.InvalidRace or ResultCode.InvalidSizes or
            ResultCode.InvalidSizeName or ResultCode.InvalidDefaultSize or ResultCode.InvalidModelPath or
            ResultCode.Accepted or ResultCode.InvalidContext or ResultCode.InvalidCallbackPort or
            ResultCode.ServerStopped or ResultCode.ContextReattached or ResultCode.ContextRevoked or
            ResultCode.ContextRemoved or ResultCode.InvalidExportId or ResultCode.DuplicateExport or
            ResultCode.DuplicateExportId or ResultCode.ExportNotFound or ResultCode.ExportPending or
            ResultCode.ExportComplete or ResultCode.ImportFailureRecorded or ResultCode.ExportApplied or
            ResultCode.ExportAppliedWithWarnings or ResultCode.ApplyFailed or ResultCode.DestinationUnsafe or
            ResultCode.MissingNewModName or ResultCode.UnexpectedNewModName or ResultCode.InvalidPendingExport or
            ResultCode.InvalidDestinationState or ResultCode.MissingSourceTarget or ResultCode.InvalidVariant or
            ResultCode.InvalidVariantGroup or ResultCode.StaleVariantTarget or ResultCode.AmbiguousSourceOption or
            ResultCode.SourceOptionUnavailable or ResultCode.WriteFailed or ResultCode.PenumbraRootMissing or
            ResultCode.PenumbraRootUnsafe or ResultCode.PenumbraUnavailable or ResultCode.VanillaModCreated or
            ResultCode.VanillaModCreatedWithWarnings or ResultCode.VanillaModCreateFailed or
            ResultCode.RaceModelUnavailable or ResultCode.RaceModelExported or
            ResultCode.RaceModelExportedWithWarnings or ResultCode.SizesExported or
            ResultCode.SizesExportedWithWarnings or ResultCode.AttributeGroupConflict or
            ResultCode.AttributeGroupWriteFailed or ResultCode.DestinationUnavailable or
            ResultCode.DestinationUnresolvable or ResultCode.DestinationAmbiguous or ResultCode.DestinationMissing or
            ResultCode.SourceModMissing or ResultCode.SourceModIdentityUnavailable or ResultCode.SourceModChanged or
            ResultCode.SourceModAmbiguous or ResultCode.VariantTargetUnavailable or ResultCode.VariantTargetsLoaded or
            ResultCode.VariantTargetsUnavailable or ResultCode.InvalidRestore or ResultCode.InvalidBackupTarget or
            ResultCode.BackupUnavailable or ResultCode.BackupMissing or ResultCode.RestoreWriteFailed or
            ResultCode.RestoreFailed or ResultCode.BackupRestored or ResultCode.BackupRestoredWithWarnings or
            ResultCode.BackupsCleared or ResultCode.BackupClearFailed or ResultCode.InvalidMashup or
            ResultCode.MashupReimportRequired or ResultCode.MashupMaterialMissing or
            ResultCode.MashupMaterialPathUnsafe or ResultCode.MashupMaterialAliasUnsafe or
            ResultCode.MashupMaterialAliasConflict or ResultCode.MashupMaterialPathConflict or
            ResultCode.MashupTargetMaterialDirectoryAmbiguous or ResultCode.MashupTargetMaterialDirectoryInvalid or
            ResultCode.MashupTargetMaterialFamilyAmbiguous or ResultCode.MashupMaterialSlotsExhausted or
            ResultCode.MashupPlanReady or ResultCode.MashupModelMismatch or ResultCode.MashupTextureConflict or
            ResultCode.MashupSourceChanged or ResultCode.MashupMaterialRewriteFailed or
            ResultCode.MashupMaterialHashConflict or ResultCode.MashupMappingConflict or ResultCode.MashupFailed or
            ResultCode.MashupDestinationExists or ResultCode.MashupApplied or ResultCode.MashupAppliedWithWarnings or
            ResultCode.MashupWriteFailed or ResultCode.MashupModExists or ResultCode.MashupModCreated or
            ResultCode.MashupModCreatedWithWarnings or ResultCode.MashupModCreateFailed or
            ResultCode.MaterialCoverageComplete or ResultCode.MaterialCoverageMissing or
            ResultCode.MaterialCoverageUnavailable or ResultCode.SkeletonsUnavailable or ResultCode.SkeletonTimeout or
            ResultCode.SkeletonNotFound or ResultCode.RacialScalingUnavailable or ResultCode.RacialDeformerTimeout or
            ResultCode.InvalidRequest or ResultCode.UnknownProject or ResultCode.RequestFailed or
            ResultCode.InvalidSend or ResultCode.DuplicateSend or ResultCode.UnknownSend or
            ResultCode.BlenderConnectionFailed or ResultCode.BlenderRequestTimeout or
            ResultCode.InvalidSuccessResponse or ResultCode.LegacyHttpError => RemedyReview,
    };
#pragma warning restore CS8524

    private static bool IsSafeDiagnosticValue(string? value, int maxLength)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength &&
           value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    private static (int Status, string Body) Error(int status, ResultCode code, string message)
        => (status, Json(new { ok = false, code, error = message }));

    private static (int Status, string Body) ResultResponse(ExportReceipt receipt)
        => receipt.Success
            ? (200, Json(new
            {
                ok = true,
                code = receipt.Code,
                message = receipt.Message,
                warnings = receipt.Warnings ?? Array.Empty<string>(),
                targetFilePath = receipt.TargetFilePath,
                destinationName = receipt.DestinationName,
                context = receipt.Context,
                requiredExternalMods = receipt.RequiredExternalMods ?? Array.Empty<string>(),
            }))
            : Error(StatusForCode(receipt.Code), receipt.Code, receipt.Message);

    private static int StatusForCode(ResultCode code)
        => code switch
        {
            ResultCode.StaleContext => 410,
            ResultCode.InvalidCapability or ResultCode.PluginInstanceMismatch => 401,
            ResultCode.DuplicateExportId => 409,
            ResultCode.MashupPlanMismatch => 409,
            ResultCode.RacialScalingMismatch => 409,
            ResultCode.VanillaModExists or ResultCode.DestinationNotReady or ResultCode.SizesNeedMod => 409,
            ResultCode.ExportNotFound => 404,
            ResultCode.ServerStopped or ResultCode.InternalError => 500,
            _ => 400,
        };

    /// <summary>Why the request reader gave up: the code clients match on, and the message shown to people.</summary>
    private sealed record RequestReadError(ResultCode Code, string Message);

    private static (HttpRequest? Request, RequestReadError? Error) ReadRequest(NetworkStream stream)
    {
        var bytes = new List<byte>(4096);
        var buffer = new byte[4096];
        var headerEnd = -1;

        while (headerEnd < 0 && bytes.Count <= MaxHeaderBytes)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0)
                break;

            bytes.AddRange(buffer.AsSpan(0, read).ToArray());
            headerEnd = FindHeaderEnd(bytes);
        }

        if (headerEnd < 0)
            return (null, new(ResultCode.RequestHeadersInvalid, "request headers are missing or too large"));

        var header = Encoding.ASCII.GetString(bytes.ToArray(), 0, headerEnd);
        var lines = header.Split("\r\n");
        var requestLine = lines.Length > 0
            ? lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries)
            : Array.Empty<string>();
        if (requestLine.Length < 2)
            return (null, new(ResultCode.RequestLineInvalid, "invalid request line"));

        var contentLength = 0;
        foreach (var line in lines.Skip(1))
        {
            if (IsBrowserRequestHeader(line))
                return (null, BrowserRequestRefused);
            if (!line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                continue;

            var value = line["Content-Length:".Length..].Trim();
            if (!int.TryParse(value, out contentLength) || contentLength < 0)
                return (null, new(ResultCode.ContentLengthInvalid, "invalid content length"));
            if (contentLength > MaxRequestBytes)
                return (null, new(ResultCode.RequestBodyTooLarge, "request body is too large"));
        }

        var bodyStart = headerEnd + 4;
        var requiredBytes = bodyStart + contentLength;
        if (requiredBytes > MaxHeaderBytes + MaxRequestBytes)
            return (null, new(ResultCode.RequestTooLarge, "request is too large"));

        while (bytes.Count < requiredBytes)
        {
            var read = stream.Read(buffer, 0, Math.Min(buffer.Length, requiredBytes - bytes.Count));
            if (read == 0)
                return (null, new(ResultCode.RequestBodyIncomplete, "request body ended early"));
            bytes.AddRange(buffer.AsSpan(0, read).ToArray());
        }

        return (new HttpRequest(
            requestLine[0],
            requestLine[1],
            bytes.GetRange(bodyStart, contentLength).ToArray()), null);
    }

    private static readonly RequestReadError BrowserRequestRefused = new(ResultCode.RequestForbidden, "browser requests are refused");

    /// <summary>
    /// Whether a header line marks what a web page could send to loopback (CSRF, DNS rebinding):
    /// an Origin or Sec-Fetch-Site header, or a Host other than 127.0.0.1 or localhost. Blender
    /// and Painter, the listener's clients, send none of these.
    /// </summary>
    private static bool IsBrowserRequestHeader(string line)
    {
        var colon = line.IndexOf(':');
        if (colon <= 0)
            return false;
        var name = line[..colon].Trim();
        if (name.Equals("Origin", StringComparison.OrdinalIgnoreCase) || name.Equals("Sec-Fetch-Site", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!name.Equals("Host", StringComparison.OrdinalIgnoreCase))
            return false;
        var host = line[(colon + 1)..].Trim().ToLowerInvariant();
        var hostname = host.Contains(':') ? host[..host.LastIndexOf(':')] : host;
        return host.Length > 0 && hostname is not ("127.0.0.1" or "localhost");
    }

    private static int FindHeaderEnd(List<byte> bytes)
    {
        for (var i = 3; i < bytes.Count; i++)
        {
            if (bytes[i - 3] == '\r' && bytes[i - 2] == '\n' &&
                bytes[i - 1] == '\r' && bytes[i] == '\n')
                return i - 3;
        }

        return -1;
    }

    private static void WriteResponse(NetworkStream stream, int status, string body)
    {
        var reason = status switch
        {
            200 => "OK",
            202 => "Accepted",
            400 => "Bad Request",
            401 => "Unauthorized",
            409 => "Conflict",
            410 => "Gone",
            500 => "Internal Server Error",
            _   => "Not Found",
        };

        var response = $"HTTP/1.1 {status} {reason}\r\n" +
                       "Content-Type: application/json\r\n" +
                       $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n" +
                       "Connection: close\r\n\r\n" +
                       body;

        var bytes = Encoding.UTF8.GetBytes(response);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();
    }

    private static string Json(object value)
        => JsonSerializer.Serialize(value);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = false,
    };

    public void Dispose()
    {
        lock (_listenerLock)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        _cts.Cancel();
        StopListener();
        _cts.Dispose();
        if (_ownsContexts)
            _contexts.Dispose();
    }

    private void StopListener()
    {
        CancellationTokenSource? runCts;
        TcpListener? listener;
        Task[] tasks;
        lock (_listenerLock)
        {
            runCts = _runCts;
            _runCts = null;
            listener = _listener;
            _listener = null;
            tasks = _clientTasks.Append(_runTask).Where(task => task is not null).Cast<Task>().ToArray();
            _runTask = null;
            runCts?.Cancel();
            listener?.Stop();
            foreach (var client in _clients.ToArray())
            {
                try { client.Close(); } catch { }
            }
        }

        try
        {
            Task.WhenAll(tasks).Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception e)
        {
            _log.Debug($"Export receiver shutdown completed with active work: {e.Message}");
        }
        runCts?.Dispose();
    }
}
