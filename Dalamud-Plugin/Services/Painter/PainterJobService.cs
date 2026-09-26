using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InstantEdit.Models;
using InstantEdit.Services.Previews;

namespace InstantEdit.Services.Painter;

public sealed record PainterCreateResult(Guid? JobId, string Message, IReadOnlyList<string> Warnings);

/// <summary>
/// Opens On Screen models in Substance Painter and applies what Painter sends back. Textures go
/// through ordinary texture sessions, so saves keep their backups, conflict checks and
/// recompression; a Painter project only adds the batching and the channel packing.
/// </summary>
internal sealed class PainterJobService : IDisposable
{
    private static readonly TimeSpan LaunchTimeout = TimeSpan.FromMinutes(2);

    private readonly Configuration _config;
    private readonly TextureEditService _textures;
    private readonly PainterProjectBuilder _builder;
    private readonly PainterClient _client;
    private readonly PainterJobStore _store;
    private readonly Func<string, CancellationToken, Task<byte[]?>> _readGameFile;
    private readonly Action<Exception, string> _log;
    private readonly ConcurrentDictionary<string, SendState> _sends = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _life = new();

    private sealed class SendState(Guid jobId)
    {
        public Guid JobId { get; } = jobId;
        public Task? Work { get; set; }
        public string Message { get; set; } = "";
        public bool Failed { get; set; }
        public List<(string Key, string Label, string Outcome, string Message)> Results { get; } = [];
        public DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;
    }

    internal PainterJobService(Configuration config, TextureEditService textures, PainterProjectBuilder builder,
        PainterClient client, PainterJobStore store, Func<string, CancellationToken, Task<byte[]?>> readGameFile,
        Action<Exception, string> log)
    {
        _config = config;
        _textures = textures;
        _builder = builder;
        _client = client;
        _store = store;
        _readGameFile = readGameFile;
        _log = log;
        _textures.KeepSession = _store.LinksSession;
    }

    /// <summary> Raised with a message for the status strip, and whether it reports a problem. </summary>
    public event Action<string, bool>? Message;

    public IReadOnlyList<PainterJob> Jobs => _store.Jobs;

    public string StoreError => _store.LoadError;

    // ---- Preparing the dialog --------------------------------------------------------------

    public Task<PainterDraft> PrepareAsync(PainterRequest request, CancellationToken token = default)
        => _builder.PrepareAsync(request, token);

    // ---- Creating a job --------------------------------------------------------------------

    public async Task<PainterCreateResult> CreateAsync(PainterDraft draft, CancellationToken token = default)
    {
        var warnings = new List<string>();
        var selected = draft.SelectedTextures.ToList();
        if (selected.Count == 0)
            return new PainterCreateResult(null, "Select at least one texture to send back.", warnings);
        if (draft.NeedsModName && !PenumbraService.IsSafeNewModName(draft.NewModName))
            return new PainterCreateResult(null, "Enter a valid name for the new mod that holds the edited vanilla textures.", warnings);
        if (_store.LoadError.Length > 0)
            return new PainterCreateResult(null, _store.LoadError, warnings);

        var jobId = Guid.NewGuid();
        var request = draft.Request;
        var cacheRoot = _textures.EnsureConfiguredCache();
        var jobDir = JobDirectory(cacheRoot, jobId);

        // Sessions first: a texture whose session can't open stays in Painter for reference only.
        var sessions = new Dictionary<PainterDraftTexture, Guid>();
        foreach (var texture in selected)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var locator = texture.Texture.Locator!;
                var vanilla = texture.Texture.IsVanilla;
                var textureRequest = new TextureEditRequest(
                    texture.Texture.GamePath,
                    vanilla ? texture.Texture.SourcePath : Path.Combine(locator.SourceModRootPath ?? "", (locator.SourceRelativePath ?? "").Replace('/', Path.DirectorySeparatorChar)),
                    vanilla ? "" : locator.SourceModDirectory ?? "",
                    vanilla ? "" : locator.SourceModRootPath ?? "",
                    vanilla ? "" : locator.SourceRelativePath ?? "",
                    request.ObjectIndex, request.ActorAddress,
                    vanilla ? draft.NewModName : "",
                    jobId);
                sessions[texture] = await _textures.StartAsync(textureRequest, launchEditor: false).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                warnings.Add($"{Path.GetFileName(texture.Texture.GamePath)}: {error.Message}");
            }
        }
        if (sessions.Count == 0)
            return new PainterCreateResult(null, "None of the selected textures could be opened. " + string.Join(" ", warnings), warnings);

        var capability = PainterJobStore.NewCapability();
        var displayName = $"{request.ActorName} – {Path.GetFileName(request.Model.GamePath)}";
        var files = await _builder.WriteJobAsync(draft, sessions, jobDir, jobId, capability, _config.ListenPort,
            BlenderClient.CurrentPluginVersion, displayName, token).ConfigureAwait(false);
        warnings.AddRange(files.Warnings);
        if (files.Targets.Count == 0)
            return new PainterCreateResult(null, "No texture could be prepared for Painter. " + string.Join(" ", warnings), warnings);

        var job = new PainterJob
        {
            Id = jobId,
            Capability = capability,
            DisplayName = displayName,
            ActorName = request.ActorName,
            ModelGamePath = request.Model.GamePath,
            NewModName = draft.NeedsModName ? draft.NewModName : "",
            Created = DateTimeOffset.UtcNow,
            Targets = files.Targets,
        };
        _store.Save(job);

        var opened = await OpenInPainterAsync(job, token).ConfigureAwait(false);
        return new PainterCreateResult(jobId, opened.Length == 0 ? $"Sent {files.Targets.Count} textures to Substance Painter." : opened, warnings);
    }

    internal static string JobDirectory(string cacheRoot, Guid jobId) => Path.Combine(cacheRoot, "painter", jobId.ToString("N"));

    /// <summary> Asks Painter to open the job's project, starting Painter first when it is configured and not running. </summary>
    public async Task<string> OpenInPainterAsync(PainterJob job, CancellationToken token = default)
    {
        var manifest = Path.Combine(JobDirectory(_textures.EnsureConfiguredCache(), job.Id), "job.json");
        if (!File.Exists(manifest))
            return Fail(job, "The project's files were removed from the cache. Open the model from On Screen again.");
        var status = await _client.GetStatusAsync(_config.PainterPort, TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
        if (!status.Reachable)
        {
            var launched = await LaunchPainterAsync(token).ConfigureAwait(false);
            if (launched.Length > 0)
                return Fail(job, launched);
        }
        var refused = await _client.OpenAsync(_config.PainterPort, job.Id, manifest, token).ConfigureAwait(false);
        if (refused.Length > 0)
            return Fail(job, refused);
        _store.Update(() =>
        {
            job.State = PainterJobState.Sent;
            job.Message = "Opening in Substance Painter…";
        });
        return "";
    }

    private string Fail(PainterJob job, string message)
    {
        try
        {
            _store.Update(() =>
            {
                job.State = PainterJobState.Failed;
                job.Message = message;
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _log(error, "Could not save the Painter project list.");
        }
        return message;
    }

    /// <summary> Starts Painter from Settings' executable and waits for the plugin to answer; returns an error or empty. </summary>
    public async Task<string> LaunchPainterAsync(CancellationToken token = default)
    {
        var exe = string.IsNullOrWhiteSpace(_config.PainterExecutablePath) ? PainterInstallation.DetectExecutable() : _config.PainterExecutablePath;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            return "Substance Painter isn't running. Start it (with the XIV Instant Edit plugin enabled) or set its executable in Settings.";
        try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false }); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return "Substance Painter could not be started: " + error.Message;
        }
        var deadline = DateTime.UtcNow + LaunchTimeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(2000, token).ConfigureAwait(false);
            if ((await _client.GetStatusAsync(_config.PainterPort, TimeSpan.FromSeconds(1), token).ConfigureAwait(false)).Reachable)
                return "";
        }
        return "Substance Painter started, but the XIV Instant Edit plugin didn't answer. Enable it under Python in Painter.";
    }

    public Task<PainterStatus> GetStatusAsync(CancellationToken token = default)
        => _client.GetStatusAsync(_config.PainterPort, TimeSpan.FromSeconds(1), token);

    /// <summary> Forgets a project: its texture sessions and cache files go; the mods and their backups stay. </summary>
    public async Task DiscardAsync(Guid jobId)
    {
        var job = _store.Find(jobId) ?? throw new IOException("The Painter project no longer exists.");
        _store.Remove(jobId);
        foreach (var target in job.Targets)
        {
            if (_store.LinksSession(target.SessionId))
                continue;
            try { await _textures.DiscardAsync(target.SessionId).ConfigureAwait(false); }
            catch (IOException) { /* already gone */ }
        }
        try
        {
            var directory = JobDirectory(_textures.EnsureConfiguredCache(), jobId);
            TextureFiles.EnsureLocalPath(directory);
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _log(error, "Could not remove a Painter project's cache folder.");
        }
    }

    // ---- Requests from Painter ---------------------------------------------------------------

    /// <summary> Handles /painter/* requests from Painter's plugin; returns the HTTP status and JSON body. </summary>
    internal (int Status, string Body) Handle(string path, byte[] body)
    {
        JsonObject request;
        try
        {
            request = JsonNode.Parse(new UTF8Encoding(false, true).GetString(body)) as JsonObject
                ?? throw new JsonException("The request must be a JSON object.");
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or ArgumentException)
        {
            return Error(400, "invalid_request", error.Message);
        }
        var job = _store.Authorize(Text(request, "jobId"), Text(request, "capability"));
        if (job is null)
            return Error(404, "unknown_project", "Instant Edit doesn't know this Painter project. Open the model from On Screen again.");
        try
        {
            return path switch
            {
                "/painter/attach" => Attach(job, request),
                "/painter/baseline" => Baseline(job, request),
                "/painter/send" => Send(job, request),
                "/painter/send/status" => SendStatus(job, request),
                _ => Error(404, "endpoint_not_found", "Unknown Painter endpoint."),
            };
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return Error(400, "request_failed", error.Message);
        }
    }

    private (int, string) Attach(PainterJob job, JsonObject request)
    {
        var projectPath = Text(request, "projectPath");
        var jobDir = JobDirectory(_textures.EnsureConfiguredCache(), job.Id);
        _store.Update(() =>
        {
            if (projectPath.Length > 0 && projectPath.Length < 1024)
                job.ProjectPath = projectPath;
        });
        return (200, Json(new { ok = true, jobDir, displayName = job.DisplayName }));
    }

    private (int, string) Baseline(PainterJob job, JsonObject request)
    {
        if (request["ok"] is not JsonValue okValue || !okValue.TryGetValue<bool>(out var ok) || !ok)
        {
            var message = Text(request, "message");
            Fail(job, message.Length > 0 ? message : "Painter could not set up the project.");
            Message?.Invoke($"Substance Painter: {job.Message}", true);
            return (200, Json(new { ok = true }));
        }
        var jobDir = JobDirectory(_textures.EnsureConfiguredCache(), job.Id);
        var files = ReadFiles(request, Path.Combine(jobDir, "baseline"), job);
        _ = Task.Run(() =>
        {
            try
            {
                var hashes = files.ToDictionary(file => file.Key, file => TgaImage.Read(TextureFiles.Read(file.Path)).PixelHash(), StringComparer.OrdinalIgnoreCase);
                _store.Update(() =>
                {
                    foreach (var target in job.Targets)
                        if (hashes.TryGetValue(target.Key, out var hash))
                            target.BaselineHash = hash;
                    job.State = PainterJobState.Ready;
                    job.Message = "Ready in Substance Painter";
                });
                // Seeds stay until the project is discarded: Painter may read them again before its first save.
                DeleteQuietly(Path.Combine(jobDir, "baseline"));
                Message?.Invoke($"{job.DisplayName} is ready in Substance Painter.", false);
            }
            catch (Exception error)
            {
                _log(error, "Could not record the Painter project's first export.");
                Fail(job, "Could not read Painter's first export: " + error.Message);
                Message?.Invoke($"Substance Painter: {job.Message}", true);
            }
        });
        return (202, Json(new { ok = true }));
    }

    private (int, string) Send(PainterJob job, JsonObject request)
    {
        var sendId = Text(request, "sendId");
        if (sendId.Length != 32 || !sendId.All(Uri.IsHexDigit))
            return Error(400, "invalid_send", "The send id is invalid.");
        var jobDir = JobDirectory(_textures.EnsureConfiguredCache(), job.Id);
        var folder = Path.Combine(jobDir, "export", sendId);
        var files = ReadFiles(request, folder, job);
        var state = new SendState(job.Id);
        if (!_sends.TryAdd(sendId, state))
            return Error(409, "duplicate_send", "This send is already being applied.");
        foreach (var stale in _sends.Where(pair => pair.Value.Work?.IsCompleted == true && DateTimeOffset.UtcNow - pair.Value.Started > TimeSpan.FromHours(1)).ToList())
            _sends.TryRemove(stale.Key, out _);
        state.Work = Task.Run(() => ApplySendAsync(job, files, folder, state));
        return (202, Json(new { ok = true, sendId }));
    }

    private (int, string) SendStatus(PainterJob job, JsonObject request)
    {
        if (!_sends.TryGetValue(Text(request, "sendId"), out var state) || state.JobId != job.Id)
            return Error(404, "unknown_send", "Instant Edit has no record of this send.");
        if (state.Work is { IsCompleted: false })
            return (200, Json(new { ok = true, state = "pending" }));
        return (200, Json(new
        {
            ok = true,
            state = state.Failed ? "failed" : "done",
            message = state.Message,
            results = state.Results.Select(r => new { key = r.Key, label = r.Label, outcome = r.Outcome, message = r.Message }),
        }));
    }

    private async Task ApplySendAsync(PainterJob job, IReadOnlyList<(string Key, string Path)> files, string folder, SendState state)
    {
        try
        {
            var saves = new List<ExternalTextureSave>();
            var targets = new Dictionary<Guid, PainterTarget>();
            var meshCache = new Dictionary<string, ModelMesh>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, path) in files)
            {
                var target = job.Targets.First(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));
                targets[target.SessionId] = target;
                var image = TgaImage.Read(TextureFiles.Read(path));
                if (target.BaselineHash.Length > 0 && image.PixelHash() == target.BaselineHash)
                {
                    saves.Add(new ExternalTextureSave(target.SessionId, null));
                    continue;
                }
                if (target.Protect)
                    image = await MergeWithCoverageAsync(target, image, meshCache, state).ConfigureAwait(false);
                saves.Add(new ExternalTextureSave(target.SessionId, TgaImage.WriteBgra32(image)));
            }
            var results = await _textures.ApplyExternalAsync(saves, _life.Token).ConfigureAwait(false);
            foreach (var result in results)
            {
                var target = targets[result.SessionId];
                state.Results.Add((target.Key, Path.GetFileName(target.GamePath), result.Outcome switch
                {
                    ExternalTextureOutcome.Applied => "applied",
                    ExternalTextureOutcome.Restored => "restored",
                    ExternalTextureOutcome.Unchanged => "unchanged",
                    _ => "failed",
                }, result.Outcome == ExternalTextureOutcome.Failed ? result.Message : ""));
            }
            var applied = results.Count(r => r.Outcome is ExternalTextureOutcome.Applied or ExternalTextureOutcome.Restored);
            var failed = results.Count(r => r.Outcome == ExternalTextureOutcome.Failed);
            state.Message = failed > 0
                ? $"{applied} applied, {failed} failed."
                : applied == 0 ? "Nothing changed since the last send." : $"{applied} texture{(applied == 1 ? "" : "s")} applied in game.";
            state.Failed = failed > 0 && applied == 0;
            _store.Update(() =>
            {
                job.LastSent = DateTimeOffset.UtcNow;
                job.Message = state.Message;
            });
            Message?.Invoke($"Substance Painter: {state.Message}", failed > 0);
        }
        catch (Exception error)
        {
            _log(error, "A Painter send failed.");
            state.Failed = true;
            state.Message = error.Message;
            Message?.Invoke($"Substance Painter send failed: {error.Message}", true);
        }
        finally
        {
            DeleteQuietly(folder);
        }
    }

    /// <summary>
    /// For a texture other meshes also use: Painter's pixels inside this project's UV islands (plus a
    /// seam margin), and the texture's current pixels everywhere else.
    /// </summary>
    private async Task<RgbaImage> MergeWithCoverageAsync(PainterTarget target, RgbaImage painter, Dictionary<string, ModelMesh> meshCache, SendState state)
    {
        var session = _textures.Sessions.FirstOrDefault(s => s.Id == target.SessionId);
        if (session is null || !File.Exists(session.WorkingFile))
            return painter;
        var current = TgaImage.Read(TextureFiles.Read(session.WorkingFile));
        if (current.Width != painter.Width || current.Height != painter.Height)
        {
            state.Results.Add((target.Key, Path.GetFileName(target.GamePath), "note", "Its size changed, so the whole texture was taken from Painter."));
            return painter;
        }
        var triangles = new List<(Vector2, Vector2, Vector2)>();
        foreach (var model in target.Coverage)
        {
            if (!meshCache.TryGetValue(model.Source, out var mesh))
            {
                var bytes = model.Vanilla
                    ? await _readGameFile(model.Source, _life.Token).ConfigureAwait(false)
                    : File.Exists(model.Source) ? await File.ReadAllBytesAsync(model.Source, _life.Token).ConfigureAwait(false) : null;
                if (bytes is null)
                    continue;
                meshCache[model.Source] = mesh = ModelMeshReader.Read(bytes);
            }
            foreach (var part in mesh.Meshes.Where(part => model.Meshes.Contains(part.MeshIndex)))
                foreach (var submesh in part.Submeshes)
                    for (var i = 0; i + 2 < submesh.Indices.Length; i += 3)
                        triangles.Add((part.Uvs[submesh.Indices[i]], part.Uvs[submesh.Indices[i + 1]], part.Uvs[submesh.Indices[i + 2]]));
        }
        if (triangles.Count == 0)
            return painter;
        var coverage = UvCoverage.Rasterize(painter.Width, painter.Height, triangles, UvCoverage.MarginFor(painter.Width, painter.Height));
        return coverage.Merge(painter, current);
    }

    /// <summary> The request's files, each checked to be one of the job's targets inside the expected folder. </summary>
    private static IReadOnlyList<(string Key, string Path)> ReadFiles(JsonObject request, string folder, PainterJob job)
    {
        if (request["files"] is not JsonArray array || array.Count == 0 || array.Count > 256)
            throw new InvalidDataException("The request lists no files.");
        var files = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in array)
        {
            if (item is not JsonObject entry)
                throw new InvalidDataException("A file entry is invalid.");
            var key = Text(entry, "key");
            var path = Text(entry, "path");
            if (!job.Targets.Any(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase)) || !seen.Add(key))
                throw new InvalidDataException($"{key} is not one of this project's textures.");
            if (path.Length == 0 || !Path.IsPathFullyQualified(path) || !PathRules.IsPathWithin(path, folder) ||
                !string.Equals(Path.GetFileName(path), key + ".tga", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                throw new InvalidDataException($"The exported file for {key} is not where Instant Edit expects it.");
            TextureFiles.EnsureLocalPath(path);
            files.Add((key, Path.GetFullPath(path)));
        }
        return files;
    }

    private void DeleteQuietly(string directory)
    {
        try
        {
            TextureFiles.EnsureLocalPath(directory);
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _log(error, "Could not remove a Painter export folder.");
        }
    }

    private static string Text(JsonObject node, string name)
        => node[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    private static string Json(object value) => JsonSerializer.Serialize(value);

    private static (int, string) Error(int status, string code, string message)
        => (status, Json(new { ok = false, code, message }));

    public void Dispose()
    {
        _life.Cancel();
        _client.Dispose();
    }
}
