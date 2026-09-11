using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using InstantEdit.Models;
using Penumbra.Api.Enums;

namespace InstantEdit.Services;

/// <summary>Owns persistent working images. A single queue serializes saves, restores and destination changes.</summary>
public sealed class TextureEditService : IDisposable
{
    private static readonly TimeSpan StaleSessionAge = TimeSpan.FromDays(1);
    private static readonly TimeSpan CacheCleanupInterval = TimeSpan.FromHours(1);

    private sealed class Runtime(TextureEditSession session) : IDisposable
    {
        public TextureEditSession Session = session;
        public FileSystemWatcher? Watcher;
        public long ChangedAt;
        public long Generation;
        public long LastScan;
        public long LastHashCheck;
        public long ObservedGeneration = -1;
        public long ObservedLength = -1;
        public long ObservedWrite;
        public string FailedHash = "";
        public int Failures;
        public long RetryAt;
        public volatile bool Enabled = !session.Paused;
        public void Changed() { Interlocked.Exchange(ref ChangedAt, Environment.TickCount64); Interlocked.Increment(ref Generation); }
        public void Dispose() { Enabled = false; Interlocked.Increment(ref Generation); Watcher?.Dispose(); }
    }

    private readonly ITextureEditBackend _backend;
    private readonly Configuration _config;
    private readonly ModelBackupStore _backups;
    private readonly Action<Exception, string> _log;
    private readonly string _catalog;
    private readonly ConcurrentDictionary<Guid, Runtime> _sessions = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _life = new();
    private readonly Task _worker;
    private readonly bool _watch;
    private readonly TimeSpan _cacheCleanupInterval;
    private TextureEditSession[] _snapshot = [];
    public IReadOnlyList<TextureEditSession> Sessions => Volatile.Read(ref _snapshot);
    public string StartupError { get; private set; } = "";
    internal Task Completion => _worker;

    internal TextureEditService(ITextureEditBackend backend, Configuration config, string configDirectory,
        ModelBackupStore backups, Action<Exception, string> log, bool watch = true, TimeSpan? cacheCleanupInterval = null)
    {
        _backend = backend; _config = config; _backups = backups; _log = log;
        _watch = watch;
        _cacheCleanupInterval = cacheCleanupInterval ?? CacheCleanupInterval;
        if (_cacheCleanupInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(cacheCleanupInterval));
        _catalog = Path.Combine(configDirectory, "TextureSessions.json");
        try
        {
            if (File.Exists(_catalog))
            {
                var restored = JsonSerializer.Deserialize<TextureEditSession[]>(TextureFiles.Read(_catalog))
                    ?? throw new IOException("Invalid texture session catalog.");
                foreach (var restoredSession in restored)
                {
                    // Sessions written before resolved vanilla paths were
                    // persisted used GamePath for both values. Preserve those
                    // sessions while allowing new captures to distinguish a
                    // file-swapped source.
                    var s = restoredSession.NeedsMod && restoredSession.ResolvedGamePath.Length == 0
                        ? restoredSession with { ResolvedGamePath = restoredSession.GamePath }
                        : restoredSession;
                    if (s.Id == Guid.Empty || !PenumbraService.IsSafeGameResourcePath(s.GamePath, ".tex") ||
                        !PenumbraService.IsSafeGameResourcePath(s.RelativePath, ".tex") ||
                        (s.NeedsMod && !PenumbraService.IsSafeGameResourcePath(s.ResolvedGamePath, ".tex")))
                        throw new IOException("Invalid texture session identity.");
                    TextureFiles.EnsureLocalPath(s.Directory);
                    MigrateLegacyWorkingFile(s);
                    TextureFiles.EnsureLocalPath(s.TargetFile);
                    _ = TextureFiles.OutputType(s.Format);
                    s.Paused = true;
                    s.Status = s.Conflict ? s.Status : "Paused after restart. Resume to apply saved changes.";
                    if (!_sessions.TryAdd(s.Id, new Runtime(s))) throw new IOException("Duplicate texture session identity.");
                }
            }
        }
        catch (Exception error)
        {
            StartupError = "Texture sessions could not be loaded; the catalog has been retained. " + error.Message;
            _log(error, StartupError);
            _sessions.Clear();
        }
        Publish();
        _worker = watch ? Task.Run(WatchLoopAsync) : Task.CompletedTask;
    }

    public string EnsureConfiguredCache()
    {
        if (!string.IsNullOrWhiteSpace(_config.TextureCacheDirectory))
            return TextureFiles.EnsureCacheRoot(_config.TextureCacheDirectory);

        // Keep sessions created by the pre-centralized implementation usable if
        // the plugin has not yet run its startup migration (notably in tests).
        if (!string.IsNullOrWhiteSpace(_config.TextureCacheRoot))
            return TextureFiles.ValidateCache(_config.TextureCacheRoot);

        return TextureFiles.EnsureCacheRoot(Path.GetTempPath());
    }

    public void RequestCacheCleanup()
    {
        if (!_config.AutomaticCacheCleanup) return;
        _ = Task.Run(async () =>
        {
            try { await CleanupStaleSessionsAsync().ConfigureAwait(false); }
            catch (OperationCanceledException) when (_life.IsCancellationRequested) { }
            catch (Exception error) { _log(error, "Texture cache cleanup failed; existing sessions were retained."); }
        });
    }

    internal async Task<int> CleanupStaleSessionsAsync()
    {
        if (!_config.AutomaticCacheCleanup) return 0;
        await _gate.WaitAsync(_life.Token).ConfigureAwait(false);
        try
        {
            EnsureReady();
            var cutoff = DateTime.UtcNow - StaleSessionAge;
            var removed = 0;
            foreach (var runtime in _sessions.Values.ToArray())
            {
                var session = runtime.Session;
                if (runtime.Enabled || !session.Paused || !IsStaleSession(session, cutoff)) continue;
                runtime.Dispose();
                try
                {
                    if (!DeleteOwnedWorkingDirectory(session)) continue;
                    if (_sessions.TryRemove(session.Id, out _)) removed++;
                }
                catch (Exception error)
                {
                    _log(error, "Could not remove a stale texture session; it was retained.");
                }
            }
            if (removed > 0) Persist();
            return removed;
        }
        finally { _gate.Release(); }
    }

    public async Task<Guid> StartAsync(TextureEditRequest request, bool launchEditor = true)
    {
        await _gate.WaitAsync(_life.Token).ConfigureAwait(false);
        try
        {
            EnsureReady();
            if (launchEditor) ValidateEditor();
            var existing = _sessions.Values.FirstOrDefault(r => !r.Session.Conflict &&
                (string.IsNullOrEmpty(request.ModDirectory)
                    ? r.Session.NewModName.Length > 0 && r.Session.GamePath == request.GamePath && r.Session.CollectionId.HasValue &&
                      r.Session.ObjectIndex == request.ObjectIndex && r.Session.ActorAddress == request.ActorAddress
                    : string.Equals(r.Session.TargetFile, request.ActualPath, StringComparison.OrdinalIgnoreCase)));
            if (existing is not null)
            {
                if (launchEditor) Launch(existing.Session.WorkingFile);
                return existing.Session.Id;
            }
            var root = EnsureConfiguredCache();
            var source = await _backend.CaptureAsync(request, _life.Token).ConfigureAwait(false);
            _life.Token.ThrowIfCancellationRequested();
            var session = source.Session with { CacheRoot = root, Paused = true, Status = "Preparing working image" };
            TextureFiles.EnsureLocalPath(session.Directory);
            Directory.CreateDirectory(session.Directory);
            var runtime = new Runtime(session);
            _sessions[session.Id] = runtime;
            Persist();
            try
            {
                var original = Path.Combine(session.Directory, "original.tex");
                File.WriteAllBytes(original, source.Bytes);
                await _backend.ConvertAsync(original, session.WorkingFile, TextureType.Targa, false).ConfigureAwait(false);
                _life.Token.ThrowIfCancellationRequested();
                var tga = TextureFiles.Read(session.WorkingFile);
                TextureFiles.ValidateTga(tga, session.Width, session.Height);
                var pixels = Path.Combine(session.Directory, "pixels.tex");
                await _backend.ConvertAsync(session.WorkingFile, pixels, TextureType.RgbaTex, false).ConfigureAwait(false);
                _life.Token.ThrowIfCancellationRequested();
                session.PixelHash = TextureFiles.PixelHash(TextureFiles.Read(pixels));
                session.WorkingHash = TextureFiles.Hash(tga);
                session.Paused = false;
                session.Status = "Watching for saves";
                runtime.Enabled = true;
                ArmWatcher(runtime);
                Persist();
                if (launchEditor) Launch(session.WorkingFile);
                return session.Id;
            }
            catch (Exception error)
            {
                runtime.Enabled = false;
                session.Paused = true;
                session.Status = "Could not open texture: " + error.Message;
                Persist();
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    public void OpenEditor(Guid id) => Launch(Get(id).Session.WorkingFile);
    public void OpenFolder(Guid id)
    {
        var path = Get(id).Session.Directory;
        TextureFiles.EnsureLocalPath(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
    private void ValidateEditor()
    {
        if (!TryValidateEditorPath(_config.TextureEditorPath, out _))
            throw new IOException("Set the texture editor executable in XIV Instant Edit Settings first.");
    }

    internal static bool TryValidateEditorPath(string path, out string error)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            error = "Choose a full path to the texture editor executable.";
            return false;
        }

        if (!File.Exists(path))
        {
            error = "The selected texture editor executable does not exist.";
            return false;
        }

        if (!string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            error = "Choose a Windows .exe file for the texture editor.";
            return false;
        }

        error = string.Empty;
        return true;
    }
    private void Launch(string file)
    {
        ValidateEditor();
        TextureFiles.EnsureLocalPath(file);
        if (!File.Exists(file)) throw new IOException("The working TGA is missing. Discard this session and reopen the texture.");
        var start = new ProcessStartInfo(_config.TextureEditorPath) { UseShellExecute = false };
        start.ArgumentList.Add(file);
        Process.Start(start);
    }

    public async Task SetPausedAsync(Guid id, bool paused)
    {
        var runtime = Get(id);
        // Stop an in-flight conversion before waiting for the queue.
        runtime.Enabled = false;
        runtime.Changed();
        await _gate.WaitAsync(_life.Token).ConfigureAwait(false);
        try
        {
            var s = Get(id).Session;
            if (!paused && s.Conflict) throw new IOException("This session has a source conflict. Its TGA is retained; reopen the texture from the browser.");
            if (!paused && s.PixelHash.Length == 0) throw new IOException("This session did not finish opening. Discard it and reopen the texture.");
            TextureFiles.EnsureLocalPath(s.Directory);
            s.Paused = paused;
            runtime.RetryAt = 0;
            runtime.Failures = 0;
            s.Status = paused ? "Paused" : "Watching for saves";
            runtime.Enabled = !paused;
            if (!paused) ArmWatcher(runtime);
            Persist();
        }
        finally { _gate.Release(); }
    }

    public async Task RetryAsync(Guid id)
    {
        await SetPausedAsync(id, false).ConfigureAwait(false);
        await _gate.WaitAsync(_life.Token).ConfigureAwait(false);
        try
        {
            var s = Get(id).Session;
            if (!s.NeedsMod) s.Status = await _backend.RefreshAsync(s, _life.Token).ConfigureAwait(false);
            Persist();
        }
        finally { _gate.Release(); }
    }

    public async Task RestoreAsync(Guid id)
    {
        await SetPausedAsync(id, true).ConfigureAwait(false);
        await _gate.WaitAsync(_life.Token).ConfigureAwait(false);
        try
        {
            var s = Get(id).Session;
            if (s.NeedsMod || s.Conflict) throw new IOException("There is no safe destination to restore.");
            var target = _backups.Describe(s.ModDirectory, s.RelativePath);
            var source = string.IsNullOrEmpty(s.LastBackup) ? Path.Combine(s.Directory, "original.tex")
                : _backups.Resolve(target.Id, Path.GetFileName(s.LastBackup));
            var original = TextureFiles.Read(source);
            var h = TextureFiles.ReadTex(original);
            if (h.Format != s.Format || h.Width != s.Width || h.Height != s.Height) throw new IOException("The backup does not match this session.");
            _life.Token.ThrowIfCancellationRequested();
            var result = await _backend.CommitAsync(s, original, () => !_life.IsCancellationRequested, _life.Token, restoring: true).ConfigureAwait(false);
            s.LastCommittedHash = result.Hash;
            s.LastBackup = result.Backup;
            s.LastSaved = DateTimeOffset.UtcNow;
            // Intentionally keep the artist's working image and pixel baseline unchanged.
            s.Status = "Backup restored. Session paused; working TGA retained.";
            Persist();
            var refresh = await _backend.RefreshAsync(s, _life.Token).ConfigureAwait(false);
            if (refresh.Contains("attention", StringComparison.Ordinal)) s.Status += " " + refresh;
            Persist();
        }
        catch (TextureConflictException error)
        {
            Get(id).Session.Conflict = true;
            Get(id).Session.Status = error.Message;
            Persist();
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task DiscardAsync(Guid id)
    {
        var runtime = Get(id);
        runtime.Enabled = false;
        runtime.Changed();
        await _gate.WaitAsync(_life.Token).ConfigureAwait(false);
        try
        {
            runtime.Dispose();
            var s = runtime.Session;
            var expected = Path.GetFullPath(Path.Combine(s.CacheRoot, "texture-edits", id.ToString("N")));
            if (!string.Equals(expected, Path.GetFullPath(s.Directory), StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid working directory.");
            TextureFiles.ValidateCache(s.CacheRoot);
            TextureFiles.EnsureLocalPath(expected);
            if (Directory.Exists(expected))
            {
                foreach (var item in Directory.EnumerateFileSystemEntries(expected, "*", SearchOption.AllDirectories)) TextureFiles.EnsureLocalPath(item);
                Directory.Delete(expected, true);
            }
            _sessions.TryRemove(id, out _);
            Persist();
        }
        finally { _gate.Release(); }
    }

    private static bool IsStaleSession(TextureEditSession session, DateTime cutoff)
    {
        try
        {
            if (!Directory.Exists(session.Directory) || session.WorkingHash.Length == 0 || !File.Exists(session.WorkingFile))
                return false;

            // The working directory is also the editor handoff directory. Only
            // remove files that this service creates; an artist may keep a PSD,
            // layered source, or editor backup beside the working TGA.
            var generated = new HashSet<string>(new[]
            {
                "original.tex", "pixels.tex", "snapshot.tga", "converted.tex", "session.json",
                Path.GetFileName(session.WorkingFile), "texture.tga",
            }.Select(name => Path.GetFullPath(Path.Combine(session.Directory, name))), StringComparer.OrdinalIgnoreCase);
            if (Directory.EnumerateFiles(session.Directory, "*", SearchOption.AllDirectories)
                    .Any(path => !generated.Contains(Path.GetFullPath(path))))
                return false;

            var latest = Directory.GetLastWriteTimeUtc(session.Directory);
            foreach (var path in Directory.EnumerateFiles(session.Directory, "*", SearchOption.AllDirectories))
            {
                TextureFiles.EnsureLocalPath(path);
                var modified = File.GetLastWriteTimeUtc(path);
                latest = latest > modified ? latest : modified;
            }
            if (latest >= cutoff) return false;
            return TextureFiles.Hash(TextureFiles.Read(session.WorkingFile)) == session.WorkingHash;
        }
        catch { return false; }
    }

    private static bool DeleteOwnedWorkingDirectory(TextureEditSession session)
    {
        var expected = Path.GetFullPath(Path.Combine(session.CacheRoot, "texture-edits", session.Id.ToString("N")));
        if (!string.Equals(expected, Path.GetFullPath(session.Directory), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Invalid working directory.");
        TextureFiles.ValidateCache(session.CacheRoot);
        TextureFiles.EnsureLocalPath(expected);
        if (!Directory.Exists(expected)) return true;
        foreach (var item in Directory.EnumerateFileSystemEntries(expected, "*", SearchOption.AllDirectories))
            TextureFiles.EnsureLocalPath(item);
        Directory.Delete(expected, true);
        return true;
    }

    private void ArmWatcher(Runtime runtime)
    {
        if (!_watch) return;
        runtime.Watcher?.Dispose();
        runtime.Watcher = null;
        try
        {
            var watcher = new FileSystemWatcher(runtime.Session.Directory)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
            };
            void Changed(string? name)
            {
                if (string.Equals(name, Path.GetFileName(runtime.Session.WorkingFile), StringComparison.OrdinalIgnoreCase)) runtime.Changed();
            }
            watcher.Changed += (_, e) => Changed(e.Name);
            watcher.Created += (_, e) => Changed(e.Name);
            watcher.Deleted += (_, e) => Changed(e.Name);
            watcher.Renamed += (_, e) => { Changed(e.Name); Changed(e.OldName); };
            watcher.Error += (_, _) => runtime.Changed();
            watcher.EnableRaisingEvents = true;
            runtime.Watcher = watcher;
        }
        catch (IOException error) { _log(error, "Texture notifications unavailable; periodic reconciliation remains active."); }
    }

    private async Task WatchLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        var nextCacheCleanup = DateTime.UtcNow + _cacheCleanupInterval;
        try
        {
            while (await timer.WaitForNextTickAsync(_life.Token).ConfigureAwait(false))
            {
                await ProcessPendingAsync().ConfigureAwait(false);
                if (!_config.AutomaticCacheCleanup || DateTime.UtcNow < nextCacheCleanup) continue;
                nextCacheCleanup = DateTime.UtcNow + _cacheCleanupInterval;
                try { await CleanupStaleSessionsAsync().ConfigureAwait(false); }
                catch (OperationCanceledException) when (_life.IsCancellationRequested) { throw; }
                catch (Exception error) { _log(error, "Texture cache cleanup failed; existing sessions were retained."); }
            }
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested) { }
        finally { foreach (var r in _sessions.Values) r.Dispose(); }
    }

    internal async Task ProcessPendingAsync(bool force = false)
    {
        await _gate.WaitAsync(_life.Token).ConfigureAwait(false);
        try
        {
            foreach (var runtime in _sessions.Values)
            {
                if (!runtime.Enabled) continue;
                var now = Environment.TickCount64;
                if (!force && (now - Interlocked.Read(ref runtime.ChangedAt) < 750 || now - runtime.LastScan < 2000)) continue;
                runtime.LastScan = now;
                if (force) runtime.RetryAt = 0;
                try { await ApplySaveAsync(runtime, force).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!_life.IsCancellationRequested) { /* superseded by save or pause */ }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    var message = "Save not applied: " + error.Message;
                    var changed = runtime.Session.Status != message;
                    runtime.Session.Status = message;
                    runtime.Failures = Math.Min(runtime.Failures + 1, 5);
                    runtime.RetryAt = Environment.TickCount64 + Math.Min(30000, 1000 * (1 << runtime.Failures));
                    if (error is TextureConflictException)
                    {
                        runtime.Session.Conflict = true;
                        runtime.Session.Paused = true;
                        runtime.Enabled = false;
                    }
                    if (changed) _log(error, "Texture save failed; the previous destination remains available.");
                    try { Persist(); }
                    catch (Exception storageError)
                    {
                        runtime.Enabled = false;
                        runtime.Session.Paused = true;
                        runtime.Session.Status += " Session storage failed: " + storageError.Message;
                        Publish();
                    }
                }
            }
        }
        finally { _gate.Release(); }
    }

    private async Task ApplySaveAsync(Runtime runtime, bool force)
    {
        var s = runtime.Session;
        var generation = Interlocked.Read(ref runtime.Generation);
        var info = new FileInfo(s.WorkingFile);
        if (!force && info.Exists && generation == runtime.ObservedGeneration &&
            info.Length == runtime.ObservedLength && info.LastWriteTimeUtc.Ticks == runtime.ObservedWrite &&
            Environment.TickCount64 - runtime.LastHashCheck < 30000 && runtime.Failures == 0) return;
        var tga = TextureFiles.Read(s.WorkingFile);
        runtime.LastHashCheck = Environment.TickCount64;
        runtime.ObservedLength = info.Length;
        runtime.ObservedWrite = info.LastWriteTimeUtc.Ticks;
        runtime.ObservedGeneration = generation;
        var hash = TextureFiles.Hash(tga);
        if (hash == s.WorkingHash) return;
        if (hash != runtime.FailedHash)
        {
            runtime.FailedHash = hash;
            runtime.Failures = 0;
            runtime.RetryAt = 0;
        }
        if (Environment.TickCount64 < runtime.RetryAt) return;
        TextureFiles.ValidateTga(tga, s.Width, s.Height);
        await Task.Delay(150, _life.Token).ConfigureAwait(false);
        bool Current() => runtime.Enabled && !_life.IsCancellationRequested && generation == Interlocked.Read(ref runtime.Generation) &&
            TextureFiles.Hash(TextureFiles.Read(s.WorkingFile)) == hash;
        if (!Current()) return;
        s.Status = "Converting saved texture…";
        Publish();
        var snapshot = Path.Combine(s.Directory, "snapshot.tga");
        var pixels = Path.Combine(s.Directory, "pixels.tex");
        var output = Path.Combine(s.Directory, "converted.tex");
        TextureFiles.EnsureLocalPath(snapshot);
        File.WriteAllBytes(snapshot, tga);
        await _backend.ConvertAsync(snapshot, pixels, TextureType.RgbaTex, false).ConfigureAwait(false);
        _life.Token.ThrowIfCancellationRequested();
        var rawPixels = TextureFiles.Read(pixels);
        var pixelHeader = TextureFiles.ReadTex(rawPixels);
        if (pixelHeader.Width != s.Width || pixelHeader.Height != s.Height) throw new IOException("Decoded dimensions do not match the working image.");
        var pixelHash = TextureFiles.PixelHash(rawPixels);
        if (pixelHash == s.PixelHash)
        {
            if (!Current()) return;
            s.WorkingHash = hash;
            s.Status = "Watching; saved pixels are unchanged";
            Persist();
            return;
        }
        await _backend.ConvertAsync(snapshot, output, TextureFiles.OutputType(s.Format), s.MipMaps).ConfigureAwait(false);
        _life.Token.ThrowIfCancellationRequested();
        if (!Current()) return;
        var converted = TextureFiles.Read(output);
        TextureFiles.ValidateOutput(converted, s);
        var result = await _backend.CommitAsync(s, converted, Current, _life.Token).ConfigureAwait(false);
        s.LastCommittedHash = result.Hash;
        s.LastBackup = result.Backup;
        s.PixelHash = pixelHash;
        s.WorkingHash = hash;
        s.LastSaved = DateTimeOffset.UtcNow;
        s.Status = result.Message;
        // Save commit identity before any refresh that may fail.
        try { Persist(); }
        catch (Exception error)
        {
            runtime.Enabled = false;
            s.Paused = true;
            s.Status = "Texture saved, but session storage failed. Paused: " + error.Message;
            _log(error, s.Status);
            Publish();
            return;
        }
        _life.Token.ThrowIfCancellationRequested();
        s.Status = await _backend.RefreshAsync(s, _life.Token).ConfigureAwait(false);
        Persist();
    }

    private Runtime Get(Guid id) => _sessions.TryGetValue(id, out var runtime) ? runtime : throw new IOException("Texture session no longer exists.");
    private static void MigrateLegacyWorkingFile(TextureEditSession session)
    {
        var legacy = Path.Combine(session.Directory, "texture.tga");
        if (string.Equals(legacy, session.WorkingFile, StringComparison.OrdinalIgnoreCase) ||
            File.Exists(session.WorkingFile) || !File.Exists(legacy)) return;

        TextureFiles.EnsureLocalPath(legacy);
        TextureFiles.EnsureLocalPath(session.WorkingFile);
        File.Move(legacy, session.WorkingFile);
    }

    private void EnsureReady()
    {
        _life.Token.ThrowIfCancellationRequested();
        if (StartupError.Length > 0) throw new IOException(StartupError);
    }
    private void Publish() => Volatile.Write(ref _snapshot, _sessions.Values.Select(r => r.Session with { }).OrderBy(s => s.GamePath).ToArray());
    private void Persist()
    {
        EnsureReady();
        foreach (var r in _sessions.Values)
            if (Directory.Exists(r.Session.Directory)) TextureFiles.WriteJson(Path.Combine(r.Session.Directory, "session.json"), r.Session);
        TextureFiles.WriteJson(_catalog, _sessions.Values.Select(r => r.Session).ToArray());
        Publish();
    }

    public void Dispose()
    {
        _life.Cancel();
        foreach (var r in _sessions.Values) r.Dispose();
        // Never block the game thread: a converter or refresh can be waiting on it.
    }
}
