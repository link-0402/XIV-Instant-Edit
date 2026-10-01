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
        public long VariantGeneration;
        /// <summary>Save tracking per variant TGA name; only touched while holding the queue gate.</summary>
        public readonly Dictionary<string, VariantState> Variants = new(StringComparer.OrdinalIgnoreCase);
        public void Changed() { Interlocked.Exchange(ref ChangedAt, Environment.TickCount64); Interlocked.Increment(ref Generation); }
        public void VariantChanged() { Interlocked.Exchange(ref ChangedAt, Environment.TickCount64); Interlocked.Increment(ref VariantGeneration); }
        public VariantState StateFor(string name)
        {
            if (!Variants.TryGetValue(name, out var state)) Variants[name] = state = new VariantState();
            return state;
        }
        public void Dispose() { Enabled = false; Interlocked.Increment(ref Generation); Watcher?.Dispose(); }
    }

    private sealed class VariantState
    {
        public long ObservedGeneration = -1;
        public long ObservedLength = -1;
        public long ObservedWrite;
        public long LastHashCheck;
        public string FailedHash = "";
        public int Failures;
        public long RetryAt;
    }

    /// <summary>TGAs in the working folder that the service writes itself, so they are never variants.</summary>
    private static readonly HashSet<string> ReservedTgaNames = new(StringComparer.OrdinalIgnoreCase) { "snapshot.tga", "texture.tga" };
    /// <summary>Scratch folder for variant conversions, kept out of the watched top level.</summary>
    private const string VariantWorkFolder = "variant-work";

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

    /// <summary> Raised (on the service's worker) with the destination path after a save or restore replaced it. </summary>
    public event Action<string>? FileChanged;

    /// <summary> Sessions this returns true for are never removed as stale (a Painter project still links them). </summary>
    internal Func<Guid, bool>? KeepSession { get; set; }

    private void RaiseFileChanged(string path)
    {
        try
        {
            FileChanged?.Invoke(path);
        }
        catch (Exception error)
        {
            _log(error, "A texture file-changed handler failed.");
        }
    }
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
                    s.Variants ??= [];
                    if (s.Variants.Any(v => !PathRules.IsSafeVariantName(v.Name) ||
                            (v.RelativePath.Length > 0 && !PenumbraService.IsSafeGameResourcePath(v.RelativePath, ".tex"))))
                        throw new IOException("Invalid texture variant.");
                    if (s.Id == Guid.Empty || !PenumbraService.IsSafeGameResourcePath(s.GamePath, ".tex") ||
                        !PenumbraService.IsSafeGameResourcePath(s.RelativePath, ".tex") ||
                        (s.NeedsMod && !PenumbraService.IsSafeGameResourcePath(s.ResolvedGamePath, ".tex")))
                        throw new IOException("Invalid texture session identity.");
                    TextureFiles.EnsureLocalPath(s.Directory);
                    MigrateLegacyWorkingFile(s);
                    TextureFiles.EnsureLocalPath(s.TargetFile);
                    _ = TextureFiles.OutputType(s.Format);
                    // Sessions from before optional recompression always saved in their captured format.
                    if (s.SavedFormat == 0) s.SavedFormat = s.Format;
                    if (!TextureFiles.IsSessionFormat(s.SavedFormat, s)) throw new IOException("Invalid texture session format.");
                    s.Paused = true;
                    s.Status = s.Conflict ? s.Status : "Paused after restart. Open the texture again or Resume to apply saved changes.";
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
            catch (OperationCanceledException) when (_life.IsCancellationRequested) { return; }
            catch (Exception error) { _log(error, "Texture cache cleanup failed; existing sessions were retained."); }
            RunAdditionalCacheCleanup();
        });
    }

    /// <summary>
    /// More automatic cache cleanup, such as old Game Files exports. It runs on a background thread
    /// after the stale texture sessions', whenever automatic cleanup is on: at startup, hourly and
    /// when the cache settings change.
    /// </summary>
    internal Action? AdditionalCacheCleanup { get; set; }

    private void RunAdditionalCacheCleanup()
    {
        if (!_config.AutomaticCacheCleanup || AdditionalCacheCleanup is not { } cleanup) return;
        try { cleanup(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { _log(error, "Cache cleanup failed; the remaining files were kept."); }
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
                // A Painter project can send to this texture again whenever it is reopened.
                if (KeepSession?.Invoke(session.Id) == true) continue;
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
            var blocked = SharedTextures.EditBlock(request.GamePath, string.IsNullOrEmpty(request.ModDirectory));
            if (blocked.Length > 0) throw new IOException(blocked);
            if (launchEditor) ValidateEditor();
            var existing = _sessions.Values.FirstOrDefault(r => IsReusableFor(r.Session, request));
            if (existing is not null)
            {
                if (launchEditor) Launch(EditorFileFor(existing.Session, request));
                ResumeIfPaused(existing);
                return existing.Session.Id;
            }
            var root = EnsureConfiguredCache();
            var source = await _backend.CaptureAsync(request, _life.Token).ConfigureAwait(false);
            _life.Token.ThrowIfCancellationRequested();
            var session = source.Session with
            {
                CacheRoot = root, SavedFormat = source.Session.Format, Paused = true, Status = "Preparing working image",
            };
            TextureFiles.EnsureLocalPath(session.Directory);
            Directory.CreateDirectory(session.Directory);
            var runtime = new Runtime(session);
            _sessions[session.Id] = runtime;
            Persist();
            try
            {
                var original = Path.Combine(session.Directory, "original.tex");
                // Kept with its mip offsets normalized, so Penumbra can convert it and restoring it
                // writes a header that describes its data.
                File.WriteAllBytes(original, TextureFiles.NormalizeMipOffsets(source.Bytes));
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

    /// <summary>
    /// A session that finished opening and has no source conflict continues when its texture is opened again.
    /// A vanilla session belongs to its destination mod, so it only continues within the same Painter project.
    /// </summary>
    internal static bool IsReusableFor(TextureEditSession session, TextureEditRequest request)
        => !session.Conflict && session.PixelHash.Length > 0 &&
           (string.IsNullOrEmpty(request.ModDirectory)
               ? session.NewModName.Length > 0 && session.JobId == request.JobId && session.GamePath == request.GamePath && session.CollectionId.HasValue &&
                 session.ObjectIndex == request.ObjectIndex && session.ActorAddress == request.ActorAddress
               : string.Equals(session.TargetFile, request.ActualPath, StringComparison.OrdinalIgnoreCase) ||
                 VariantShowing(session, request) is not null);

    /// <summary>The variant whose TEX the request shows, when the actor has that option selected.</summary>
    private static TextureVariant? VariantShowing(TextureEditSession session, TextureEditRequest request)
        => session.Variants.FirstOrDefault(v => v.RelativePath.Length > 0 &&
            string.Equals(session.VariantTargetFile(v), request.ActualPath, StringComparison.OrdinalIgnoreCase));

    /// <summary>Reopening a texture that shows a variant opens that variant's TGA.</summary>
    private static string EditorFileFor(TextureEditSession session, TextureEditRequest request)
        => VariantShowing(session, request) is { } variant && File.Exists(session.VariantWorkingFile(variant))
            ? session.VariantWorkingFile(variant)
            : session.WorkingFile;

    /// <summary>A top-level TGA in the working folder other than the working image and the service's own files.</summary>
    internal static bool IsVariantFileName(TextureEditSession session, string? fileName)
        => fileName is not null && fileName == Path.GetFileName(fileName) &&
           Path.GetExtension(fileName).Equals(".tga", StringComparison.OrdinalIgnoreCase) &&
           !fileName.Equals(Path.GetFileName(session.WorkingFile), StringComparison.OrdinalIgnoreCase) &&
           !ReservedTgaNames.Contains(fileName);

    public TextureEditSession? FindReusable(TextureEditRequest request) => Sessions.FirstOrDefault(s => IsReusableFor(s, request));

    /// <summary>Opening the working image means editing continues, so a paused session resumes.</summary>
    public async Task OpenEditorAsync(Guid id)
    {
        Launch(Get(id).Session.WorkingFile);
        await _gate.WaitAsync(_life.Token).ConfigureAwait(false);
        try
        {
            EnsureReady();
            ResumeIfPaused(Get(id));
        }
        finally { _gate.Release(); }
    }

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
        try { ApplyPaused(Get(id), paused); }
        finally { _gate.Release(); }
    }

    // Callers hold the queue gate.
    private void ApplyPaused(Runtime runtime, bool paused)
    {
        var s = runtime.Session;
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

    private void ResumeIfPaused(Runtime runtime)
    {
        var s = runtime.Session;
        if (s.Paused && !s.Conflict && s.PixelHash.Length > 0) ApplyPaused(runtime, false);
    }

    public async Task RetryAsync(Guid id)
    {
        await SetPausedAsync(id, false).ConfigureAwait(false);
        await _gate.WaitAsync(_life.Token).ConfigureAwait(false);
        try
        {
            var s = Get(id).Session;
            if (!s.NeedsMod) s.Status = await RefreshOneAsync(s, null).ConfigureAwait(false);
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
            // Earlier saves may have been resized or stored uncompressed, so the backup's size can differ.
            // A backup of the captured file is restored with its mip offsets normalized.
            var (original, h) = TextureFiles.ReadOriginal(TextureFiles.Read(source));
            if (!TextureFiles.IsSessionFormat(h.Format, s)) throw new IOException("The backup does not match this session.");
            _life.Token.ThrowIfCancellationRequested();
            var result = await _backend.CommitAsync(s, original, () => !_life.IsCancellationRequested, _life.Token).ConfigureAwait(false);
            s.LastCommittedHash = result.Hash;
            s.LastBackup = result.Backup;
            s.LastSaved = DateTimeOffset.UtcNow;
            s.SavedFormat = h.Format;
            s.Width = h.Width;
            s.Height = h.Height;
            RaiseFileChanged(s.TargetFile);
            // Intentionally keep the artist's working image and pixel baseline unchanged.
            s.Status = "Backup restored. Session paused; working TGA retained.";
            Persist();
            var refresh = await RefreshOneAsync(s, s.OriginalOptionId).ConfigureAwait(false);
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
        var wasEnabled = runtime.Enabled;
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
        catch when (_sessions.ContainsKey(id))
        {
            // The session is still listed, so keep it working as before instead of ignoring saves.
            runtime.Enabled = wasEnabled;
            if (wasEnabled) ArmWatcher(runtime);
            runtime.Changed();
            throw;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Discards every session as <see cref="DiscardAsync"/> does, writing the catalog once.
    /// Returns the sessions that could not be removed, each as its texture and why.
    /// </summary>
    public async Task<IReadOnlyList<string>> DiscardAllAsync()
    {
        var runtimes = _sessions.Values.Select(runtime => (Runtime: runtime, WasEnabled: runtime.Enabled)).ToArray();
        // Stop in-flight conversions before waiting for the queue.
        foreach (var (runtime, _) in runtimes)
        {
            runtime.Enabled = false;
            runtime.Changed();
        }
        await _gate.WaitAsync(_life.Token).ConfigureAwait(false);
        var removed = false;
        var kept = new List<string>();
        try
        {
            foreach (var (runtime, wasEnabled) in runtimes)
            {
                var s = runtime.Session;
                if (!_sessions.ContainsKey(s.Id)) continue;
                runtime.Dispose();
                try
                {
                    DeleteOwnedWorkingDirectory(s);
                    removed |= _sessions.TryRemove(s.Id, out _);
                }
                catch (Exception error)
                {
                    // The session is still listed, so keep it working as before instead of ignoring saves.
                    runtime.Enabled = wasEnabled;
                    if (wasEnabled) ArmWatcher(runtime);
                    runtime.Changed();
                    kept.Add($"{Path.GetFileName(s.GamePath)}: {error.Message}");
                    _log(error, "Could not discard a texture session; it was retained.");
                }
            }
            if (removed) Persist();
        }
        finally { _gate.Release(); }
        return kept;
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
                Path.Combine(VariantWorkFolder, "snapshot.tga"), Path.Combine(VariantWorkFolder, "pixels.tex"),
                Path.Combine(VariantWorkFolder, "converted.tex"),
            }.Concat(session.Variants.Select(v => v.Name + ".tga"))
                .Select(name => Path.GetFullPath(Path.Combine(session.Directory, name))), StringComparer.OrdinalIgnoreCase);
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
            foreach (var variant in session.Variants)
            {
                var file = session.VariantWorkingFile(variant);
                if (File.Exists(file) && TextureFiles.Hash(TextureFiles.Read(file)) != variant.WorkingHash) return false;
            }
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
                else if (IsVariantFileName(runtime.Session, name)) runtime.VariantChanged();
            }
            watcher.Changed += (_, e) => Changed(e.Name);
            watcher.Created += (_, e) => Changed(e.Name);
            watcher.Deleted += (_, e) => Changed(e.Name);
            watcher.Renamed += (_, e) => { Changed(e.Name); Changed(e.OldName); };
            watcher.Error += (_, _) => { runtime.Changed(); runtime.VariantChanged(); };
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
                // Off the loop, so a large cleanup doesn't hold up texture saves.
                _ = Task.Run(RunAdditionalCacheCleanup);
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
                catch (Exception error) when (error is not OperationCanceledException) { ReportSaveFailure(runtime, error); }
                if (runtime.Enabled) await ApplyVariantSavesAsync(runtime, force).ConfigureAwait(false);
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>Records a failed save and backs off; a conflict pauses the whole session. Callers hold the queue gate.</summary>
    private void ReportSaveFailure(Runtime runtime, Exception error, string? variantName = null)
    {
        var s = runtime.Session;
        var message = (variantName is null ? "Save not applied: " : $"Variant \"{variantName}\" not applied: ") + error.Message;
        var changed = s.Status != message;
        s.Status = message;
        if (variantName is null)
        {
            runtime.Failures = Math.Min(runtime.Failures + 1, 5);
            runtime.RetryAt = Environment.TickCount64 + Math.Min(30000, 1000 * (1 << runtime.Failures));
        }
        else
        {
            var state = runtime.StateFor(variantName);
            state.Failures = Math.Min(state.Failures + 1, 5);
            state.RetryAt = Environment.TickCount64 + Math.Min(30000, 1000 * (1 << state.Failures));
            if (FindVariant(s, variantName) is { } variant) variant.Status = "Save not applied: " + error.Message;
        }
        if (error is TextureConflictException)
        {
            s.Conflict = true;
            s.Paused = true;
            runtime.Enabled = false;
        }
        if (changed) _log(error, "Texture save failed; the previous destination remains available.");
        try { Persist(); }
        catch (Exception storageError)
        {
            runtime.Enabled = false;
            s.Paused = true;
            s.Status += " Session storage failed: " + storageError.Message;
            Publish();
        }
    }

    private static TextureVariant? FindVariant(TextureEditSession session, string name)
        => session.Variants.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));

    private async Task ApplyVariantSavesAsync(Runtime runtime, bool force)
    {
        string[] files;
        try
        {
            files = Directory.EnumerateFiles(runtime.Session.Directory, "*.tga", SearchOption.TopDirectoryOnly)
                .Where(path => IsVariantFileName(runtime.Session, Path.GetFileName(path)))
                .ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return; }
        foreach (var file in files)
        {
            if (!runtime.Enabled) return;
            var name = Path.GetFileNameWithoutExtension(file);
            try { await ApplyVariantSaveAsync(runtime, name, force).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!_life.IsCancellationRequested) { /* superseded by save or pause */ }
            catch (Exception error) when (error is not OperationCanceledException) { ReportSaveFailure(runtime, error, name); }
        }
    }

    /// <summary>
    /// Mirrors <see cref="ApplySaveAsync"/> for one variant TGA: the first changed save creates
    /// the variant's TEX and Penumbra option, later saves replace that TEX.
    /// </summary>
    private async Task ApplyVariantSaveAsync(Runtime runtime, string name, bool force)
    {
        var s = runtime.Session;
        var file = Path.Combine(s.Directory, name + ".tga");
        var state = runtime.StateFor(name);
        var existing = FindVariant(s, name);
        var generation = Interlocked.Read(ref runtime.VariantGeneration);
        var info = new FileInfo(file);
        if (!info.Exists) return;
        if (force) state.RetryAt = 0;
        if (!force && generation == state.ObservedGeneration && info.Length == state.ObservedLength &&
            info.LastWriteTimeUtc.Ticks == state.ObservedWrite && Environment.TickCount64 - state.LastHashCheck < 30000 &&
            state.Failures == 0) return;
        var tga = TextureFiles.Read(file);
        state.LastHashCheck = Environment.TickCount64;
        state.ObservedLength = info.Length;
        state.ObservedWrite = info.LastWriteTimeUtc.Ticks;
        state.ObservedGeneration = generation;
        var hash = TextureFiles.Hash(tga);
        if (hash == existing?.WorkingHash) return;
        EnsureWritable(s);
        if (hash != state.FailedHash)
        {
            state.FailedHash = hash;
            state.Failures = 0;
            state.RetryAt = 0;
        }
        if (Environment.TickCount64 < state.RetryAt) return;
        if (!PathRules.IsSafeVariantName(name))
            throw new IOException($"\"{name}\" cannot be used as a Penumbra option name. Rename the TGA.");
        if (string.Equals(name, PenumbraService.OriginalTextureOptionName, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"\"{name}\" is reserved for the edited texture. Rename the TGA.");
        var (width, height) = TextureFiles.ValidateTga(tga);
        var format = TextureFiles.SaveFormat(s, _config.RecompressTextures);
        TextureFiles.ValidateEncodable(format, width, height);
        await Task.Delay(150, _life.Token).ConfigureAwait(false);
        bool Current() => runtime.Enabled && !_life.IsCancellationRequested &&
            generation == Interlocked.Read(ref runtime.VariantGeneration) && TextureFiles.Hash(TextureFiles.Read(file)) == hash;
        if (!Current()) return;
        s.Status = $"Converting variant \"{name}\"";
        Publish();
        var work = Path.Combine(s.Directory, VariantWorkFolder);
        TextureFiles.EnsureLocalPath(work);
        Directory.CreateDirectory(work);
        var snapshot = Path.Combine(work, "snapshot.tga");
        var pixels = Path.Combine(work, "pixels.tex");
        var output = Path.Combine(work, "converted.tex");
        File.WriteAllBytes(snapshot, tga);
        await _backend.ConvertAsync(snapshot, pixels, TextureType.RgbaTex, false).ConfigureAwait(false);
        _life.Token.ThrowIfCancellationRequested();
        var rawPixels = TextureFiles.Read(pixels);
        var pixelHeader = TextureFiles.ReadTex(rawPixels);
        if (pixelHeader.Width != width || pixelHeader.Height != height) throw new IOException("Decoded dimensions do not match the variant image.");
        var pixelHash = TextureFiles.PixelHash(rawPixels);
        if (existing is not null && pixelHash == existing.PixelHash && format == existing.SavedFormat)
        {
            if (!Current()) return;
            existing.WorkingHash = hash;
            s.Status = $"Watching; variant \"{name}\" pixels are unchanged";
            Persist();
            return;
        }
        await _backend.ConvertAsync(snapshot, output, TextureFiles.OutputType(format), s.MipMaps).ConfigureAwait(false);
        _life.Token.ThrowIfCancellationRequested();
        if (!Current()) return;
        var converted = TextureFiles.Read(output);
        TextureFiles.ValidateOutput(converted, format, width, height, s.MipMaps);
        if (s.NeedsMod) await CreateModFromOriginalAsync(s, Current).ConfigureAwait(false);

        var variant = existing ?? new TextureVariant { Name = name };
        var result = await _backend.CommitVariantAsync(s, variant, converted, Current, _life.Token).ConfigureAwait(false);
        if (existing is null) s.Variants.Add(variant);
        variant.RelativePath = result.RelativePath;
        variant.OptionId = result.OptionId;
        variant.LastCommittedHash = result.Hash;
        variant.PixelHash = pixelHash;
        variant.WorkingHash = hash;
        variant.SavedFormat = format;
        variant.LastSaved = DateTimeOffset.UtcNow;
        variant.Status = "Saved";
        s.VariantGroupId = result.GroupId;
        s.OriginalOptionId = result.OriginalOptionId;
        s.MappingFingerprint = result.MappingFingerprint;
        s.Status = $"Variant \"{name}\" saved";
        RaiseFileChanged(s.VariantTargetFile(variant));
        if (!PersistCommit(runtime)) return;
        _life.Token.ThrowIfCancellationRequested();
        var refresh = await RefreshOneAsync(s, variant.OptionId).ConfigureAwait(false);
        variant.Status = refresh;
        s.Status = $"Variant \"{name}\": {refresh}";
        Persist();
    }

    /// <summary>
    /// A vanilla session has no mod until its first save. A variant needs one to live in, so the
    /// captured texture is published unchanged first; the group's Original option then shows it.
    /// </summary>
    private async Task CreateModFromOriginalAsync(TextureEditSession s, Func<bool> current)
    {
        var (original, header) = TextureFiles.ReadOriginal(TextureFiles.Read(Path.Combine(s.Directory, "original.tex")));
        var result = await _backend.CommitAsync(s, original, current, _life.Token).ConfigureAwait(false);
        s.LastCommittedHash = result.Hash;
        s.LastBackup = result.Backup;
        s.SavedFormat = header.Format;
        s.Width = header.Width;
        s.Height = header.Height;
        Persist();
        // Registers the mod with Penumbra, which the variant commit's destination check requires.
        s.Status = await RefreshOneAsync(s, null).ConfigureAwait(false);
        Persist();
    }

    /// <summary>
    /// A session opened before shared game textures such as white.tex were refused never writes one;
    /// its save conflicts instead. A vanilla session's mod replaces the game path, so it stays vanilla after its first save.
    /// </summary>
    private static void EnsureWritable(TextureEditSession s)
    {
        var blocked = SharedTextures.EditBlock(s.GamePath, s.NeedsMod || s.ResolvedGamePath.Length > 0);
        if (blocked.Length > 0) throw new TextureConflictException(blocked);
    }

    /// <summary>Saves commit identity before any refresh that may fail; false when session storage failed and the session paused.</summary>
    private bool PersistCommit(Runtime runtime)
    {
        try
        {
            Persist();
            return true;
        }
        catch (Exception error)
        {
            runtime.Enabled = false;
            runtime.Session.Paused = true;
            runtime.Session.Status = "Texture saved, but session storage failed. Paused: " + error.Message;
            _log(error, runtime.Session.Status);
            Publish();
            return false;
        }
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
        EnsureWritable(s);
        if (hash != runtime.FailedHash)
        {
            runtime.FailedHash = hash;
            runtime.Failures = 0;
            runtime.RetryAt = 0;
        }
        if (Environment.TickCount64 < runtime.RetryAt) return;
        var (width, height) = TextureFiles.ValidateTga(tga);
        var format = TextureFiles.SaveFormat(s, _config.RecompressTextures);
        TextureFiles.ValidateEncodable(format, width, height);
        await Task.Delay(150, _life.Token).ConfigureAwait(false);
        bool Current() => runtime.Enabled && !_life.IsCancellationRequested && generation == Interlocked.Read(ref runtime.Generation) &&
            TextureFiles.Hash(TextureFiles.Read(s.WorkingFile)) == hash;
        if (!Current()) return;
        s.Status = "Converting saved texture";
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
        if (pixelHeader.Width != width || pixelHeader.Height != height) throw new IOException("Decoded dimensions do not match the working image.");
        var pixelHash = TextureFiles.PixelHash(rawPixels);
        // A recompression setting change is applied by the next save even when its pixels are unchanged.
        if (pixelHash == s.PixelHash && format == s.SavedFormat)
        {
            if (!Current()) return;
            s.WorkingHash = hash;
            s.Status = "Watching; saved pixels are unchanged";
            Persist();
            return;
        }
        await _backend.ConvertAsync(snapshot, output, TextureFiles.OutputType(format), s.MipMaps).ConfigureAwait(false);
        _life.Token.ThrowIfCancellationRequested();
        if (!Current()) return;
        var converted = TextureFiles.Read(output);
        TextureFiles.ValidateOutput(converted, format, width, height, s.MipMaps);
        var result = await _backend.CommitAsync(s, converted, Current, _life.Token).ConfigureAwait(false);
        s.LastCommittedHash = result.Hash;
        s.LastBackup = result.Backup;
        s.PixelHash = pixelHash;
        s.WorkingHash = hash;
        s.SavedFormat = format;
        s.Width = width;
        s.Height = height;
        s.LastSaved = DateTimeOffset.UtcNow;
        s.Status = result.Message;
        PropagateFingerprint(s, result.MappingFingerprint);
        RaiseFileChanged(s.TargetFile);
        if (!PersistCommit(runtime)) return;
        _life.Token.ThrowIfCancellationRequested();
        // With variants, the edited texture shows only while the group's Original option is selected.
        s.Status = await RefreshOneAsync(s, s.OriginalOptionId).ConfigureAwait(false);
        Persist();
    }

    private Task<string> RefreshOneAsync(TextureEditSession session, Guid? showOption)
        => _backend.RefreshAsync([new TextureRefresh(session, showOption)], _life.Token);

    /// <summary>
    /// Adding a file to a Painter project's shared mod rewrites its metadata; the project's other
    /// sessions in that mod take the new fingerprint instead of reporting a mapping conflict.
    /// </summary>
    private void PropagateFingerprint(TextureEditSession source, string fingerprint)
    {
        if (fingerprint.Length == 0 || source.JobId is null)
            return;
        foreach (var other in _sessions.Values.Select(r => r.Session))
            if (!ReferenceEquals(other, source) && other.JobId == source.JobId && !other.NeedsMod &&
                string.Equals(other.ModRoot, source.ModRoot, StringComparison.OrdinalIgnoreCase))
                other.MappingFingerprint = fingerprint;
    }

    /// <summary>
    /// Applies textures exported by Substance Painter as one batch: each goes through its session's
    /// usual convert, backup and commit, then every affected mod reloads once and actors redraw once.
    /// </summary>
    internal async Task<IReadOnlyList<ExternalTextureResult>> ApplyExternalAsync(IReadOnlyList<ExternalTextureSave> saves, CancellationToken token)
    {
        var results = new List<ExternalTextureResult>();
        var refresh = new List<TextureEditSession>();
        await _gate.WaitAsync(_life.Token).ConfigureAwait(false);
        try
        {
            EnsureReady();
            foreach (var save in saves)
            {
                token.ThrowIfCancellationRequested();
                if (!_sessions.TryGetValue(save.SessionId, out var runtime))
                {
                    results.Add(new ExternalTextureResult(save.SessionId, ExternalTextureOutcome.Failed, "The texture session no longer exists."));
                    continue;
                }
                var s = runtime.Session;
                try
                {
                    if (s.Conflict)
                        throw new TextureConflictException(s.Status);
                    ResumeIfPaused(runtime);
                    var outcome = save.Tga is null
                        ? await RestoreOriginalAsync(runtime).ConfigureAwait(false)
                        : await ApplyExternalSaveAsync(runtime, save.Tga).ConfigureAwait(false);
                    results.Add(new ExternalTextureResult(save.SessionId, outcome, s.Status));
                    if (outcome is ExternalTextureOutcome.Applied or ExternalTextureOutcome.Restored)
                        refresh.Add(s);
                }
                catch (Exception error) when (error is not OperationCanceledException || !_life.IsCancellationRequested)
                {
                    if (error is not OperationCanceledException)
                        ReportSaveFailure(runtime, error);
                    results.Add(new ExternalTextureResult(save.SessionId, ExternalTextureOutcome.Failed,
                        error is OperationCanceledException ? "The save was interrupted." : error.Message));
                }
            }
            if (refresh.Count > 0)
            {
                var status = await _backend.RefreshAsync(refresh.Select(s => new TextureRefresh(s, s.OriginalOptionId)).ToList(), _life.Token).ConfigureAwait(false);
                foreach (var s in refresh)
                    s.Status = status;
                Persist();
            }
        }
        finally { _gate.Release(); }
        return results;
    }

    // Callers hold the queue gate.
    private async Task<ExternalTextureOutcome> ApplyExternalSaveAsync(Runtime runtime, byte[] tga)
    {
        var s = runtime.Session;
        EnsureWritable(s);
        var (width, height) = TextureFiles.ValidateTga(tga);
        var format = TextureFiles.SaveFormat(s, _config.RecompressTextures);
        TextureFiles.ValidateEncodable(format, width, height);
        bool Current() => runtime.Enabled && !_life.IsCancellationRequested;
        var hash = TextureFiles.Hash(tga);
        var snapshot = Path.Combine(s.Directory, "snapshot.tga");
        var pixels = Path.Combine(s.Directory, "pixels.tex");
        var output = Path.Combine(s.Directory, "converted.tex");
        TextureFiles.EnsureLocalPath(snapshot);
        File.WriteAllBytes(snapshot, tga);
        await _backend.ConvertAsync(snapshot, pixels, TextureType.RgbaTex, false).ConfigureAwait(false);
        _life.Token.ThrowIfCancellationRequested();
        var rawPixels = TextureFiles.Read(pixels);
        var pixelHeader = TextureFiles.ReadTex(rawPixels);
        if (pixelHeader.Width != width || pixelHeader.Height != height) throw new IOException("Decoded dimensions do not match the exported image.");
        var pixelHash = TextureFiles.PixelHash(rawPixels);
        if (pixelHash == s.PixelHash && format == s.SavedFormat)
        {
            MirrorWorkingFile(runtime, tga, hash);
            s.Status = "Painter texture unchanged";
            Persist();
            return ExternalTextureOutcome.Unchanged;
        }
        s.Status = "Converting Painter texture";
        Publish();
        await _backend.ConvertAsync(snapshot, output, TextureFiles.OutputType(format), s.MipMaps).ConfigureAwait(false);
        _life.Token.ThrowIfCancellationRequested();
        if (!Current()) throw new OperationCanceledException("The session was paused.");
        var converted = TextureFiles.Read(output);
        TextureFiles.ValidateOutput(converted, format, width, height, s.MipMaps);
        var result = await _backend.CommitAsync(s, converted, Current, _life.Token).ConfigureAwait(false);
        s.LastCommittedHash = result.Hash;
        s.LastBackup = result.Backup;
        s.PixelHash = pixelHash;
        s.SavedFormat = format;
        s.Width = width;
        s.Height = height;
        s.LastSaved = DateTimeOffset.UtcNow;
        s.Status = "Saved from Substance Painter";
        PropagateFingerprint(s, result.MappingFingerprint);
        RaiseFileChanged(s.TargetFile);
        MirrorWorkingFile(runtime, tga, hash);
        if (!PersistCommit(runtime)) throw new IOException(s.Status);
        return ExternalTextureOutcome.Applied;
    }

    /// <summary>
    /// Painter's export matched its first, untouched export: the texture should be the captured
    /// original again. Nothing happens while the destination still holds it.
    /// </summary>
    private async Task<ExternalTextureOutcome> RestoreOriginalAsync(Runtime runtime)
    {
        var s = runtime.Session;
        var originalPath = Path.Combine(s.Directory, "original.tex");
        var original = TextureFiles.NormalizeMipOffsets(TextureFiles.Read(originalPath));
        // The destination can still hold the captured file with the mip offsets its mod wrote.
        if (s.NeedsMod || (File.Exists(s.TargetFile) &&
                TextureFiles.Hash(TextureFiles.NormalizeMipOffsets(TextureFiles.Read(s.TargetFile))) == TextureFiles.Hash(original)))
            return ExternalTextureOutcome.Unchanged;
        var header = TextureFiles.ReadTex(original);
        var result = await _backend.CommitAsync(s, original, () => runtime.Enabled && !_life.IsCancellationRequested, _life.Token).ConfigureAwait(false);
        s.LastCommittedHash = result.Hash;
        s.LastBackup = result.Backup;
        s.SavedFormat = header.Format;
        s.Width = header.Width;
        s.Height = header.Height;
        s.LastSaved = DateTimeOffset.UtcNow;
        s.Status = "Original restored from Substance Painter";
        PropagateFingerprint(s, result.MappingFingerprint);
        RaiseFileChanged(s.TargetFile);
        // The working image follows, so a later editor save starts from what the game shows.
        var snapshot = Path.Combine(s.Directory, "snapshot.tga");
        var pixels = Path.Combine(s.Directory, "pixels.tex");
        await _backend.ConvertAsync(originalPath, snapshot, TextureType.Targa, false).ConfigureAwait(false);
        await _backend.ConvertAsync(snapshot, pixels, TextureType.RgbaTex, false).ConfigureAwait(false);
        var tga = TextureFiles.Read(snapshot);
        s.PixelHash = TextureFiles.PixelHash(TextureFiles.Read(pixels));
        MirrorWorkingFile(runtime, tga, TextureFiles.Hash(tga));
        if (!PersistCommit(runtime)) throw new IOException(s.Status);
        return ExternalTextureOutcome.Restored;
    }

    /// <summary>
    /// Writes an applied image over the working TGA and records it, so the watcher treats it as
    /// already saved. If the file is locked the old image and hash stay, which the watcher ignores too.
    /// </summary>
    private void MirrorWorkingFile(Runtime runtime, byte[] tga, string hash)
    {
        var s = runtime.Session;
        if (s.WorkingHash == hash)
            return;
        try
        {
            TextureFiles.EnsureLocalPath(s.WorkingFile);
            File.WriteAllBytes(s.WorkingFile, tga);
            s.WorkingHash = hash;
            runtime.FailedHash = "";
            runtime.Failures = 0;
            runtime.RetryAt = 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _log(error, "The working TGA could not be updated after a Painter save; the game already has the new texture.");
        }
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
    private void Publish() => Volatile.Write(ref _snapshot, _sessions.Values
        .Select(r => r.Session with { Variants = r.Session.Variants.Select(v => v with { }).ToList() })
        .OrderBy(s => s.GamePath).ToArray());
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
