using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Dalamud.Plugin.Services;
using InstantEdit.Models;
using InstantEdit.Services.CharacterSend;
using InstantEdit.Services.Skeletons;

namespace InstantEdit.Services;

public enum BlenderConnectionState
{
    Offline,
    Online,
    VersionMismatch,
}

public sealed record BlenderStatus(bool Reachable, string? AddonVersion)
{
    public string? CacheRoot { get; init; }
    public bool CacheSettingsSupported { get; init; }
    /// <summary> Imports Blender has queued or is running; -1 when the add-on doesn't say. </summary>
    public int PendingImports { get; init; } = -1;
    /// <summary> Online when the add-on is at or above <paramref name="minimumAddonVersion"/> (<see cref="ComponentVersions.BlenderAddonMinimum"/>); newer add-ons are accepted. </summary>
    public BlenderConnectionState Classify(string minimumAddonVersion)
    {
        if (!Reachable)
            return BlenderConnectionState.Offline;

        return ComponentVersions.Satisfies(AddonVersion, minimumAddonVersion)
            ? BlenderConnectionState.Online
            : BlenderConnectionState.VersionMismatch;
    }
}

/// <summary> Talks to the HTTP listener hosted by XIV Instant Edit in Blender. </summary>
public sealed class BlenderClient : IDisposable
{
    public const string ImportOptionsCapability = "instant-edit.import-options.v1";
    public const string MaterialPreviewCapability = "instant-edit.material-preview.v1";
    public const string CacheHandoffCapability = "instant-edit.cache-handoff.v1";
    public const string TextureCacheCapability = "instant-edit.texture-cache.v1";
    public const string CacheSettingsCapability = "instant-edit.cache-settings.v1";
    public const string VanillaContextCapability = "instant-edit.vanilla-context.v1";
    public const string AnimationImportCapability = "instant-edit.animation-import.v1";
    public const string ImportSkeletonCapability = "instant-edit.import-skeleton.v1";
    /// <summary> Imports of a whole-character send share one armature, animations can name it, and /status counts pending imports. </summary>
    public const string CharacterImportCapability = "instant-edit.character-import.v1";
    /// <summary> The Timeline can read an armature's motion back (Motion from Blender). </summary>
    public const string AnimationReadCapability = "instant-edit.animation-read.v1";

    private readonly HttpClient _http;
    private readonly IPluginLog _log;
    private readonly ExportContextRegistry _contexts;
    private readonly bool _disposeHttp;

    public BlenderClient(IPluginLog log, ExportContextRegistry contexts)
        : this(log, contexts, new HttpClient
        {
            // Import handoff can include a bounded material-preview bundle. The
            // caller supplies short cancellation tokens for status probes, while
            // an actual local handoff is allowed enough time to copy large files.
            Timeout = TimeSpan.FromMinutes(2),
        }, true)
    {
    }

    internal BlenderClient(IPluginLog log, ExportContextRegistry contexts, HttpClient http)
        : this(log, contexts, http, false)
    {
    }

    private BlenderClient(IPluginLog log, ExportContextRegistry contexts, HttpClient http, bool disposeHttp)
    {
        _log         = log;
        _contexts    = contexts;
        _http        = http;
        _disposeHttp = disposeHttp;
    }

    public static string CurrentPluginVersion
    {
        get
        {
            var version = typeof(Plugin).Assembly.GetName().Version;
            return version is null || version.Build < 0
                ? "unknown"
                : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    public static string NormalizeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version) || !Version.TryParse(version.Trim(), out var parsed) || parsed.Build < 0)
            return string.Empty;
        return $"{parsed.Major}.{parsed.Minor}.{parsed.Build}";
    }

    public static string VersionMismatchMessage(string? addonVersion, string minimum)
        => $"The Blender add-on is {(string.IsNullOrEmpty(addonVersion) ? "an older version" : addonVersion)}; " +
           $"this plugin needs {minimum} or newer. Update it in Settings.";

    /// <summary>Reads the Blender add-on status and its declared release version.</summary>
    public async Task<BlenderStatus> GetStatusAsync(int port, CancellationToken cancellationToken = default)
    {
        if (port is < 1 or > 65535)
            return new BlenderStatus(false, null);

        try
        {
            using var resp = await _http.GetAsync(
                $"http://127.0.0.1:{port}/status",
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return new BlenderStatus(false, null);

            var responseBody = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var document = JsonDocument.Parse(responseBody);
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("addonVersion", out var addonVersion) ||
                    addonVersion.ValueKind != JsonValueKind.String)
                    return new BlenderStatus(true, null);

                var version = addonVersion.GetString();
                var cacheSettingsSupported = document.RootElement.TryGetProperty("capabilities", out var capabilities) &&
                    capabilities.ValueKind == JsonValueKind.Array && capabilities.EnumerateArray().Any(c =>
                        c.ValueKind == JsonValueKind.String && c.GetString() == CacheSettingsCapability);
                var cacheRoot = document.RootElement.TryGetProperty("capabilities", out capabilities) &&
                    capabilities.ValueKind == JsonValueKind.Array && capabilities.EnumerateArray().Any(c =>
                        c.ValueKind == JsonValueKind.String && c.GetString() == TextureCacheCapability) &&
                    document.RootElement.TryGetProperty("cacheRoot", out var cache) && cache.ValueKind == JsonValueKind.String
                    ? cache.GetString() : null;
                var pendingImports = document.RootElement.TryGetProperty("pendingImports", out var pending) &&
                    pending.ValueKind == JsonValueKind.Number && pending.TryGetInt32(out var count) && count >= 0
                    ? count : -1;
                return new BlenderStatus(true, string.IsNullOrWhiteSpace(version) ? null : version.Trim())
                {
                    CacheRoot = cacheRoot,
                    CacheSettingsSupported = cacheSettingsSupported,
                    PendingImports = pendingImports,
                };
            }
            catch (JsonException)
            {
                return new BlenderStatus(true, null);
            }
        }
        catch (OperationCanceledException)
        {
            return new BlenderStatus(false, null);
        }
        catch (HttpRequestException)
        {
            return new BlenderStatus(false, null);
        }
        catch (InvalidOperationException)
        {
            return new BlenderStatus(false, null);
        }
    }

    /// <summary>
    /// Ping Blender's add-on server without blocking the caller's thread.
    /// A stopped add-on is a normal condition, so connection and timeout failures
    /// are reported as false rather than escaping to the UI thread.
    /// </summary>
    public async Task<bool> IsReachableAsync(int port, CancellationToken cancellationToken = default)
        => (await GetStatusAsync(port, cancellationToken).ConfigureAwait(false)).Reachable;

    /// <summary>Returns whether the connected add-on advertises import options support.</summary>
    public async Task<bool> SupportsImportOptionsAsync(int port, CancellationToken cancellationToken = default)
        => await SupportsCapabilityAsync(port, ImportOptionsCapability, cancellationToken).ConfigureAwait(false);

    /// <summary>Returns whether the connected add-on can consume material preview bundles.</summary>
    public async Task<bool> SupportsMaterialPreviewAsync(int port, CancellationToken cancellationToken = default)
        => await SupportsCapabilityAsync(port, MaterialPreviewCapability, cancellationToken).ConfigureAwait(false);

    public async Task<bool> SupportsCacheHandoffAsync(int port, CancellationToken cancellationToken = default)
        => await SupportsCapabilityAsync(port, CacheHandoffCapability, cancellationToken).ConfigureAwait(false);

    public async Task<string?> ConfigureCacheAsync(
        int port,
        string cacheDirectory,
        bool automaticCleanup,
        CancellationToken cancellationToken = default)
    {
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));
        if (string.IsNullOrWhiteSpace(cacheDirectory))
            throw new ArgumentException("A cache directory is required.", nameof(cacheDirectory));

        var payload = JsonSerializer.Serialize(new
        {
            schema = "instant-edit.cache-settings",
            version = 1,
            cacheDirectory,
            automaticCleanup,
        });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        try
        {
            using var response = await _http.PostAsync(
                $"http://127.0.0.1:{port}/settings/cache", content, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("cacheRoot", out var root) &&
                   root.ValueKind == JsonValueKind.String &&
                   !string.IsNullOrWhiteSpace(root.GetString())
                ? root.GetString()
                : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<bool> SupportsVanillaContextAsync(int port, CancellationToken cancellationToken = default)
        => await SupportsCapabilityAsync(port, VanillaContextCapability, cancellationToken).ConfigureAwait(false);

    public async Task<bool> SupportsAnimationImportAsync(int port, CancellationToken cancellationToken = default)
        => await SupportsCapabilityAsync(port, AnimationImportCapability, cancellationToken).ConfigureAwait(false);

    /// <summary>Returns whether the connected add-on builds armatures from an import's game skeleton.</summary>
    public async Task<bool> SupportsImportSkeletonAsync(int port, CancellationToken cancellationToken = default)
        => await SupportsCapabilityAsync(port, ImportSkeletonCapability, cancellationToken).ConfigureAwait(false);

    /// <summary>Returns whether the connected add-on binds a whole-character send to one armature.</summary>
    public async Task<bool> SupportsCharacterImportAsync(int port, CancellationToken cancellationToken = default)
        => await SupportsCapabilityAsync(port, CharacterImportCapability, cancellationToken).ConfigureAwait(false);

    /// <summary>Returns whether the connected add-on can read an armature's motion back.</summary>
    public async Task<bool> SupportsAnimationReadAsync(int port, CancellationToken cancellationToken = default)
        => await SupportsCapabilityAsync(port, AnimationReadCapability, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Asks Blender to read its armature's motion (see <c>AnimationReadFormat</c>): <paramref name="request"/>
    /// is the JSON body written from <paramref name="parsed"/>, which the reply is checked against.
    /// </summary>
    internal async Task<Animations.AnimationRead> ReadAnimationAsync(int port, byte[] request, Animations.AnimationReadRequest parsed,
        CancellationToken cancellationToken = default)
    {
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));
        using var content = new ByteArrayContent(request);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsync($"http://127.0.0.1:{port}/animation/read", content, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            var failure = BridgeFailure.Create(
                "blender_addon", "animation_read", "transport", ResultCode.BlenderConnectionFailed.Wire(),
                "The plugin could not reach Blender's XIV Instant Edit listener.",
                "Start Blender, verify the configured port, and read the motion again.");
            _log?.Error(e, $"Blender bridge failure {failure.DiagnosticId}: animation_read/transport/{failure.Code}.");
            throw new BlenderBridgeException(failure, inner: e);
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            var failure = BridgeFailure.Create(
                "blender_addon", "animation_read", "transport", ResultCode.BlenderRequestTimeout.Wire(),
                "Blender did not answer while reading the motion.",
                "Check whether Blender is busy, then read the motion again.");
            _log?.Error(e, $"Blender bridge failure {failure.DiagnosticId}: animation_read/transport/{failure.Code}.");
            throw new BlenderBridgeException(failure, inner: e);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var exception = new BlenderBridgeException(BridgeFailure.FromResponse(response.StatusCode, text, "animation_read"), text);
                _log?.Error(exception, $"Blender bridge failure {exception.Failure.DiagnosticId}: HTTP {(int)response.StatusCode}; " +
                    $"{exception.Failure.Operation}/{exception.Failure.Stage}/{exception.Failure.Code}; response={text}");
                throw exception;
            }
            if (response.Content.Headers.ContentLength is > Animations.AnimationReadFormat.MaximumBytes)
                throw Unparsable(response.StatusCode, "Blender's reply is larger than the plugin accepts.");
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return Animations.AnimationReadFormat.Parse(body, parsed);
            }
            catch (InvalidDataException e)
            {
                throw Unparsable(response.StatusCode, e.Message, e);
            }
        }

        BlenderBridgeException Unparsable(HttpStatusCode status, string cause, Exception? inner = null)
        {
            var failure = BridgeFailure.Create(
                "blender_addon", "animation_read", "response_parsing", ResultCode.InvalidSuccessResponse.Wire(), cause,
                "Update and restart the XIV Instant Edit Blender add-on, then read the motion again.", (int)status);
            var message = $"Blender bridge failure {failure.DiagnosticId}: animation_read/response_parsing/{failure.Code}.";
            if (inner != null)
                _log?.Error(inner, message);
            else
                _log?.Error(message);
            return new BlenderBridgeException(failure, inner: inner);
        }
    }

    /// <summary>
    /// Sends an encoded take (see <c>AnimationTakeFormat</c>) to Blender, which keys it onto a
    /// scene armature and reports the result once its main thread has done so.
    /// </summary>
    public async Task<BlenderAnimationResult> SendAnimationAsync(int port, byte[] take, CancellationToken cancellationToken = default)
    {
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));
        using var content = new ByteArrayContent(take);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsync($"http://127.0.0.1:{port}/animation", content, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            var failure = BridgeFailure.Create(
                "blender_addon", "animation_import", "transport", ResultCode.BlenderConnectionFailed.Wire(),
                "The animation could not reach Blender's XIV Instant Edit listener.",
                "Start Blender, verify the configured port, and send the animation again.");
            _log?.Error(e, $"Blender bridge failure {failure.DiagnosticId}: animation_import/transport/{failure.Code}.");
            throw new BlenderBridgeException(failure, inner: e);
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            var failure = BridgeFailure.Create(
                "blender_addon", "animation_import", "transport", ResultCode.BlenderRequestTimeout.Wire(),
                "Blender did not answer while receiving the animation.",
                "Check whether Blender is busy, then send the animation again.");
            _log?.Error(e, $"Blender bridge failure {failure.DiagnosticId}: animation_import/transport/{failure.Code}.");
            throw new BlenderBridgeException(failure, inner: e);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return ParseAnimationResponse(response.StatusCode, body);
            }
            catch (BlenderBridgeException exception)
            {
                var failure = exception.Failure;
                _log?.Error(exception,
                    $"Blender bridge failure {failure.DiagnosticId}: HTTP {(int)response.StatusCode}; " +
                    $"{failure.Operation}/{failure.Stage}/{failure.Code}; response={exception.RawResponse}");
                throw;
            }
        }
    }

    internal static BlenderAnimationResult ParseAnimationResponse(HttpStatusCode status, string responseBody)
    {
        if ((int)status is < 200 or >= 300)
            throw new BlenderBridgeException(BridgeFailure.FromResponse(status, responseBody, "animation_import"), responseBody);
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("applied", out var applied))
            {
                if (applied.ValueKind == JsonValueKind.True)
                {
                    static string Text(JsonElement e, string name) =>
                        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                    static int Number(JsonElement e, string name) =>
                        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;
                    var missing = root.TryGetProperty("missingBones", out var list) && list.ValueKind == JsonValueKind.Array
                        ? list.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToArray()
                        : [];
                    return new BlenderAnimationResult(true, Text(root, "action"), Text(root, "armature"), Number(root, "frames"),
                        root.TryGetProperty("frameRate", out var rate) && rate.ValueKind == JsonValueKind.Number ? rate.GetDouble() : 0,
                        Number(root, "frameStart"), Number(root, "frameEnd"), Number(root, "matchedBones"),
                        Number(root, "missingBoneCount"), missing, Number(root, "alignedBoneCount"));
                }
                if (applied.ValueKind == JsonValueKind.False && root.TryGetProperty("queued", out var queued) &&
                    queued.ValueKind == JsonValueKind.True)
                    return new BlenderAnimationResult(false);
            }
        }
        catch (JsonException e)
        {
            throw new BlenderBridgeException(BridgeFailure.Create(
                "blender_addon", "animation_import", "response_parsing", ResultCode.InvalidSuccessResponse.Wire(),
                "Blender accepted the animation but returned an invalid confirmation.",
                "Update and restart the XIV Instant Edit Blender add-on, then send the animation again.", (int)status), responseBody, e);
        }
        throw new BlenderBridgeException(BridgeFailure.Create(
            "blender_addon", "animation_import", "response_parsing", ResultCode.InvalidSuccessResponse.Wire(),
            "Blender accepted the animation but did not confirm what it did with it.",
            "Update and restart the XIV Instant Edit Blender add-on, then send the animation again.", (int)status), responseBody);
    }

    private async Task<bool> SupportsCapabilityAsync(
        int port,
        string capability,
        CancellationToken cancellationToken = default)
    {
        if (port is < 1 or > 65535)
            return false;

        try
        {
            using var resp = await _http.GetAsync(
                $"http://127.0.0.1:{port}/status",
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return false;

            await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return document.RootElement.TryGetProperty("capabilities", out var capabilities) &&
                   capabilities.ValueKind == JsonValueKind.Array &&
                   capabilities.EnumerateArray().Any(item =>
                       item.ValueKind == JsonValueKind.String &&
                       string.Equals(item.GetString(), capability, StringComparison.Ordinal));
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Send an import whose Quick Export authority is the resolved file inside
    /// the original Penumbra mod. The physical path is retained in the shared
    /// registry, so Blender can display it but cannot substitute another target.
    /// </summary>
    public async Task<bool> SendSourceImportAsync(
        int port,
        string importFilePath,
        string gamePath,
        int objectIndex,
        string name,
        int callbackPort,
        string targetFilePath,
        string sourceModDirectory,
        string sourceModName,
        CancellationToken cancellationToken = default,
        BlenderImportOptions? importOptions = null,
        string? previewManifestPath = null,
        string? sourceModRootPath = null,
        string? targetRelativePath = null,
        ResourceDependencyManifest? resourceManifest = null,
        Guid? targetCollectionId = null,
        string? targetCollectionName = null,
        SourceOptionLocator? sourceOption = null,
        string sourceOptionStatus = "unknown",
        Guid? sourceModStableId = null,
        ModelSkeletonPayload? skeleton = null,
        RacialScalingRecord? racialScaling = null,
        CharacterImportEntry? character = null)
    {
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));
        if (callbackPort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(callbackPort));
        if (string.IsNullOrWhiteSpace(sourceModDirectory))
            throw new ArgumentException("A source Penumbra mod directory is required.", nameof(sourceModDirectory));
        if (string.IsNullOrWhiteSpace(targetFilePath))
            throw new ArgumentException("An original model path is required.", nameof(targetFilePath));

        var context = _contexts.CreateContext(
            gamePath,
            objectIndex,
            sourceModDirectory,
            targetFilePath,
            sourceModName,
            callbackPort,
            sourceModRootPath,
            targetRelativePath,
            resourceManifest,
            targetCollectionId,
            targetCollectionName,
            sourceOption,
            sourceOptionStatus,
            sourceModStableId,
            racialScaling: racialScaling);

        return await SendImportAsync(
            port, importFilePath, name, context, cancellationToken,
            importOptions, previewManifestPath, skeleton, character).ConfigureAwait(false);
    }

    public async Task<bool> SendGameImportAsync(
        int port,
        string importFilePath,
        string gamePath,
        string resolvedGamePath,
        int objectIndex,
        string name,
        int callbackPort,
        Guid? targetCollectionId = null,
        string? targetCollectionName = null,
        CancellationToken cancellationToken = default,
        BlenderImportOptions? importOptions = null,
        string? previewManifestPath = null,
        ResourceDependencyManifest? resourceManifest = null,
        ModelSkeletonPayload? skeleton = null,
        RacialScalingRecord? racialScaling = null,
        CharacterImportEntry? character = null)
    {
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));
        if (callbackPort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(callbackPort));

        var context = _contexts.CreateGameContext(
            gamePath,
            resolvedGamePath,
            objectIndex,
            callbackPort,
            targetCollectionId,
            targetCollectionName,
            resourceManifest,
            racialScaling);

        return await SendImportAsync(
            port, importFilePath, name, context, cancellationToken,
            importOptions, previewManifestPath, skeleton, character).ConfigureAwait(false);
    }

    private async Task<bool> SendImportAsync(
        int port,
        string importFilePath,
        string name,
        InstantEditImportContext context,
        CancellationToken cancellationToken,
        BlenderImportOptions? importOptions,
        string? previewManifestPath,
        ModelSkeletonPayload? skeleton,
        CharacterImportEntry? character)
    {
        // The context is forgotten only when Blender can't have queued the import: it was never
        // reached, or it refused the request. After a timeout or a cancellation it may still import
        // the model, which then needs the context; an unused one expires with the retention setting.
        var refused = true;
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                schema = context.Schema,
                version = context.Version,
                pluginVersion = CurrentPluginVersion,
                pluginInstanceId = context.PluginInstanceId,
                contextId = context.ContextId,
                importId = context.ImportId,
                capability = context.Capability,
                filePath = importFilePath,
                sourceGamePath = context.GamePath,
                sourceKind = context.SourceKind,
                resolvedGamePath = context.ResolvedGamePath,
                destinationState = context.DestinationState,
                objectIndex = context.ObjectIndex,
                displayName = name,
                callbackPort = context.CallbackPort,
                managedDestination = context.TargetFolder,
                targetFilePath = context.TargetFilePath,
                sourceModDirectory = context.SourceModDirectory,
                sourceModStableId = context.SourceModStableId,
                sourceModName = context.SourceModName,
                sourceModRootPath = context.SourceModRootPath,
                targetRelativePath = context.TargetRelativePath,
                targetCollectionId = context.TargetCollectionId,
                targetCollectionName = context.TargetCollectionName,
                resourceManifestVersion = context.ResourceManifestVersion,
                resourceManifestStatus = context.ResourceManifestStatus,
                backupTargetId = context.BackupTargetId,
                backupDirectory = context.BackupDirectory,
                previewManifestPath,
                importOptions = importOptions ?? BlenderImportOptions.Generated,
                // The model's game skeleton, for the generated armature's rest pose; null without one.
                skeleton,
                // The races a racially scaled model was reshaped between; the add-on tags its meshes.
                racialScaling = context.RacialScaling is { } scaling
                    ? new { modelRace = $"c{scaling.ModelRace:D4}", race = $"c{scaling.CharacterRace:D4}" }
                    : null,
                // The whole-character send the model belongs to, whose armature it shares; null for single imports.
                character,
            });

            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            HttpResponseMessage resp;
            try
            {
                resp = await _http.PostAsync(
                    $"http://127.0.0.1:{port}/import",
                    content,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException e)
            {
                var transportFailure = BridgeFailure.Create(
                    "blender_addon",
                    "import",
                    "transport",
                    ResultCode.BlenderConnectionFailed.Wire(),
                    "The request could not reach Blender's XIV Instant Edit listener.",
                    "Start Blender, verify the configured port, and retry.");
                var exception = new BlenderBridgeException(transportFailure, inner: e);
                _log?.Error(exception,
                    $"Blender bridge failure {transportFailure.DiagnosticId}: " +
                    $"import/transport/{transportFailure.Code}.");
                throw exception;
            }

            using (resp)
            {
                // Blender answered; until its answer is read, whether it queued the model is unknown.
                refused = false;
                var responseBody = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                refused = true;
                try
                {
                    var accepted = ParseImportResponse(resp.StatusCode, responseBody);
                    refused = false;
                    return accepted;
                }
                catch (BlenderBridgeException exception)
                {
                    var failure = exception.Failure;
                    _log?.Error(exception,
                        $"Blender bridge failure {failure.DiagnosticId}: HTTP {(int)resp.StatusCode}; " +
                        $"{failure.Operation}/{failure.Stage}/{failure.Code}; response={exception.RawResponse}");
                    throw;
                }
            }
        }
        catch (HttpRequestException e)
        {
            var transportFailure = BridgeFailure.Create(
                "blender_addon",
                "import",
                "transport",
                ResultCode.BlenderConnectionFailed.Wire(),
                "The request could not be completed by Blender's XIV Instant Edit listener.",
                "Start Blender, verify the configured port, and retry.");
            var exception = new BlenderBridgeException(transportFailure, inner: e);
            _log?.Error(exception,
                $"Blender bridge failure {transportFailure.DiagnosticId}: " +
                $"import/transport/{transportFailure.Code}.");
            throw exception;
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            refused = false;
            var timeoutFailure = BridgeFailure.Create(
                "blender_addon",
                "import",
                "transport",
                ResultCode.BlenderRequestTimeout.Wire(),
                "The request to Blender's XIV Instant Edit listener timed out.",
                "Retry the import and check whether Blender is busy or blocked by local security software.");
            var exception = new BlenderBridgeException(timeoutFailure, inner: e);
            _log?.Error(exception,
                $"Blender bridge failure {timeoutFailure.DiagnosticId}: " +
                $"import/transport/{timeoutFailure.Code}.");
            throw exception;
        }
        catch (OperationCanceledException)
        {
            refused = false;
            throw;
        }
        finally
        {
            if (refused)
                _contexts.RemoveContext(context.ContextId);
        }
    }

    internal static bool ParseImportResponse(HttpStatusCode status, string responseBody)
    {
        if ((int)status is < 200 or >= 300)
        {
            var failure = BridgeFailure.FromResponse(status, responseBody, "import");
            throw new BlenderBridgeException(failure, responseBody);
        }

        try
        {
            using var response = JsonDocument.Parse(responseBody);
            if (response.RootElement.ValueKind == JsonValueKind.Object &&
                response.RootElement.TryGetProperty("cached", out var cached) &&
                cached.ValueKind == JsonValueKind.True)
                return true;

            var failure = BridgeFailure.Create(
                "blender_addon",
                "import",
                "response_parsing",
                ResultCode.InvalidSuccessResponse.Wire(),
                "Blender accepted the request but did not confirm that the model was cached.",
                "Update and restart the XIV Instant Edit Blender add-on, then retry.",
                (int)status);
            throw new BlenderBridgeException(failure, responseBody);
        }
        catch (JsonException e)
        {
            var failure = BridgeFailure.Create(
                "blender_addon",
                "import",
                "response_parsing",
                ResultCode.InvalidSuccessResponse.Wire(),
                "Blender accepted the request but returned an invalid confirmation.",
                "Update and restart the XIV Instant Edit Blender add-on, then retry.",
                (int)status);
            throw new BlenderBridgeException(failure, responseBody, e);
        }
    }

    public void Dispose()
    {
        if (_disposeHttp)
            _http.Dispose();
    }
}
