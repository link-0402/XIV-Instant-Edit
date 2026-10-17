using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using InstantEdit.Models;
using InstantEdit.Services.Painter;
using InstantEdit.Services.PenumbraMods;
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
        public long ObservedGeneration = -1;
        public long ObservedLength = -1;
        public long ObservedWrite;
        public string FailedHash = "";
        public int Failures;
        public long RetryAt;
        /// <summary>The working image's last failed save only had to wait.</summary>
        public bool Waiting;
        /// <summary>When saves started waiting for Penumbra to list the mod again (tick count), or 0.</summary>
        public long UnlistedSince;
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
        /// <summary>Forgets a failed save of the working image, so nothing waits for its next attempt.</summary>
        public void Succeeded()
        {
            FailedHash = "";
            Failures = 0;
            RetryAt = 0;
            Waiting = false;
            UnlistedSince = 0;
            Session.Waiting = false;
        }
        public void Dispose() { Enabled = false; Interlocked.Increment(ref Generation); Watcher?.Dispose(); }
    }

    private sealed class VariantState
    {
        public long ObservedGeneration = -1;
        public long ObservedLength = -1;
        public long ObservedWrite;
        public string FailedHash = "";
        public int Failures;
        public long RetryAt;
        public bool Waiting;
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

    /// <summary> How long saves wait for Penumbra to list a session's mod again before the session pauses. </summary>
    internal TimeSpan UnlistedModPause { get; set; } = TimeSpan.FromMinutes(10);

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
        var idleSince = DateTime.UtcNow - StaleSessionAge;
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
                            (v.RelativePath.Length > 0 && !ModPaths.IsSafeGameResourcePath(v.RelativePath, ".tex"))))
                        throw new IOException("Invalid texture variant.");
                    if (s.Id == Guid.Empty || !ModPaths.IsSafeGameResourcePath(s.GamePath, ".tex") ||
                        !ModPaths.IsSafeGameResourcePath(s.RelativePath, ".tex") ||
                        (s.NeedsMod && !ModPaths.IsSafeGameResourcePath(s.ResolvedGamePath, ".tex")))
                        throw new IOException("Invalid texture session identity.");
                    TextureFiles.EnsureLocalPath(s.Directory);
                    MigrateLegacyWorkingFile(s);
                    TextureFiles.EnsureLocalPath(s.TargetFile);
                    _ = TextureFiles.OutputType(s.Format);
                    // Sessions from before optional recompression always saved in their captured format.
                    if (s.SavedFormat == 0) s.SavedFormat = s.Format;
                    if (!TextureFiles.IsSessionFormat(s.SavedFormat, s)) throw new IOException("Invalid texture session format.");
                    // Sessions carry on as they were across plugin reloads and game restarts. One left
                    // alone for a day is paused, so automatic cache cleanup can remove it.
                    if (s.PixelHash.Length == 0)
                    {
                        s.Paused = true;
                        s.Status = "This session didn't finish opening. Discard it and open the texture again.";
                    }
                    else if (!s.Paused && !s.Conflict)
                    {
                        var latest = LastActivity(s);
                        s.Paused = latest is null || latest < idleSince;
                        s.Status = latest is null ? "The working folder can't be read. Discard the session and open the texture again."
                            : s.Paused ? "Paused after a day without saves. Open the texture or Resume to continue."
                            : "Watching for saves";
                    }
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
        foreach (var runtime in _sessions.Values.Where(runtime => runtime.Enabled))
            ArmWatcher(runtime);
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
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
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
        Process.Start(start)?.Dispose();
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
        runtime.Succeeded();
        s.Status = paused ? "Paused" : "Watching for saves";
        runtime.Enabled = !paused;
        if (!paused)
        {
            ArmWatcher(runtime);
            // The working image is read again, so a save that failed before the pause is tried again.
            runtime.Changed();
        }
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

    /// <summary>
    /// Takes a conflicted session back: the destination as it is now becomes the session's last save,
    /// and watching resumes with the working TGA as it is, so its next save goes over that file.
    /// </summary>
    public async Task ReconnectAsync(Guid id)
    {
        var runtime = Get(id);
        await _gate.WaitAsync(_life.Token).ConfigureAwait(false);
        try
        {
            EnsureReady();
            var s = runtime.Session;
            if (!s.Conflict) return;
            if (s.PixelHash.Length == 0) throw new IOException("This session didn't finish opening. Discard it and open the texture again.");
            var blocked = SharedTextures.EditBlock(s.GamePath, s.NeedsMod || s.ResolvedGamePath.Length > 0);
            if (blocked.Length > 0) throw new IOException(blocked);
            var rebase = await _backend.RebaseAsync(s, _life.Token).ConfigureAwait(false);
            s.LastCommittedHash = rebase.Hash;
            s.MappingFingerprint = rebase.MappingFingerprint;
            s.Width = rebase.Width;
            s.Height = rebase.Height;
            // Another program may have stored it in a format saves don't write; the next save writes it again anyway.
            if (TextureFiles.IsSessionFormat(rebase.Format, s)) s.SavedFormat = rebase.Format;
            foreach (var variant in s.Variants)
                if (rebase.VariantHashes.TryGetValue(variant.Name, out var hash))
                    variant.LastCommittedHash = hash;
            runtime.Variants.Clear();
            s.Conflict = false;
            ApplyPaused(runtime, false);
            // A save that the conflict stopped goes through now.
            runtime.Changed();
            runtime.VariantChanged();
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
                // A drop removes these once it is converted; one that stopped halfway can leave them.
                Path.Combine(DropWorkFolder, "dropped.dds"), Path.Combine(DropWorkFolder, "dropped.tex"),
                Path.Combine(DropWorkFolder, "dropped.tga"), Path.Combine(DropWorkFolder, "restored.tex"),
            }.Concat(session.Variants.Select(v => v.Name + ".tga"))
                .Select(name => Path.GetFullPath(Path.Combine(session.Directory, name))), StringComparer.OrdinalIgnoreCase);
            if (Directory.EnumerateFiles(session.Directory, "*", SearchOption.AllDirectories)
                    .Any(path => !generated.Contains(Path.GetFullPath(path))))
                return false;

            if (LastActivity(session) is not { } latest || latest >= cutoff) return false;
            foreach (var variant in session.Variants)
            {
                var file = session.VariantWorkingFile(variant);
                if (File.Exists(file) && TextureFiles.Hash(TextureFiles.Read(file)) != variant.WorkingHash) return false;
            }
            return TextureFiles.Hash(TextureFiles.Read(session.WorkingFile)) == session.WorkingHash;
        }
        catch { return false; }
    }

    /// <summary>
    /// When the session's files last changed: its images, variants, conversions and anything an editor
    /// keeps there. Its session.json doesn't count, since saving any session rewrites all of them.
    /// Null when the folder is gone or can't be read.
    /// </summary>
    private static DateTime? LastActivity(TextureEditSession session)
    {
        try
        {
            if (!Directory.Exists(session.Directory))
                return null;
            DateTime? latest = null;
            foreach (var path in Directory.EnumerateFiles(session.Directory, "*", SearchOption.AllDirectories))
            {
                TextureFiles.EnsureLocalPath(path);
                if (Path.GetFileName(path).StartsWith("session.json", StringComparison.OrdinalIgnoreCase))
                    continue;
                var modified = File.GetLastWriteTimeUtc(path);
                latest = latest > modified ? latest : modified;
            }
            return latest ?? Directory.GetLastWriteTimeUtc(session.Directory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
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
        // A missing working folder is an ArgumentException.
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException)
        {
            _log(error, "Texture notifications unavailable; periodic reconciliation remains active.");
        }
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

    /// <summary>
    /// Records a failed save. A conflict pauses the whole session. A failed save of a watched TGA is tried
    /// again after a growing delay, and one that only has to wait, for Penumbra or for the character to
    /// show the file again, says so. A save from outside, named by <paramref name="external"/> (a Painter
    /// texture or a dropped image), isn't tried again; it is sent anew. Callers hold the queue gate.
    /// </summary>
    private void ReportSaveFailure(Runtime runtime, Exception error, string? variantName = null, string? external = null)
    {
        var s = runtime.Session;
        var fromOutside = external is not null;
        var waiting = error is TextureWaitException && !fromOutside;
        var subject = variantName is not null ? $"Variant \"{variantName}\"" : external ?? "Save";
        var message = $"{subject} {(waiting ? "waiting" : "not applied")}: {error.Message}";
        var changed = s.Status != message;
        s.Status = message;
        if (variantName is not null)
        {
            var state = runtime.StateFor(variantName);
            state.Failures = Math.Min(state.Failures + 1, 5);
            state.RetryAt = Environment.TickCount64 + Math.Min(30000, 1000 * (1 << state.Failures));
            state.Waiting = waiting;
            if (FindVariant(s, variantName) is { } variant) variant.Status = (waiting ? "Waiting: " : "Save not applied: ") + error.Message;
        }
        else if (!fromOutside)
        {
            runtime.Failures = Math.Min(runtime.Failures + 1, 5);
            runtime.RetryAt = Environment.TickCount64 + Math.Min(30000, 1000 * (1 << runtime.Failures));
            runtime.Waiting = waiting;
        }
        // Only the working image's own outcome ends a wait for it.
        if (variantName is null && !fromOutside)
            s.Waiting = waiting;
        else if (waiting)
            s.Waiting = true;
        if (error is TextureConflictException)
        {
            changed |= !s.Conflict;
            s.Conflict = true;
            s.Paused = true;
            s.Waiting = false;
            runtime.Enabled = false;
        }
        // Penumbra running without listing the mod can last, as when its mod folder moved and the old
        // one was kept. After a while of that, the session pauses instead of trying again forever.
        var unlisted = waiting && error is TextureWaitException { ModUnlisted: true };
        if (unlisted && runtime.UnlistedSince == 0)
            runtime.UnlistedSince = Environment.TickCount64;
        else if (!unlisted && variantName is null && !fromOutside)
            runtime.UnlistedSince = 0;
        var pause = unlisted && Environment.TickCount64 - runtime.UnlistedSince >= UnlistedModPause.TotalMilliseconds;
        if (pause)
        {
            changed = true;
            s.Status = "Paused: Penumbra doesn't list the texture's mod. Resume once it does, or open the texture again.";
            s.Paused = true;
            s.Waiting = false;
            runtime.Enabled = false;
        }
        // A save that keeps waiting only needs the session list to show it.
        if (!changed)
        {
            Publish();
            return;
        }
        _log(error, pause ? "A texture session paused: Penumbra hasn't listed its mod for a while."
            : waiting ? "A texture save is waiting and will be tried again." : "Texture save failed; the previous destination remains available.");
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

    /// <summary>Forgets a variant's failed save; the session stops waiting unless its working image still is.</summary>
    private static void VariantSucceeded(Runtime runtime, VariantState state)
    {
        state.FailedHash = "";
        state.Failures = 0;
        state.RetryAt = 0;
        state.Waiting = false;
        if (runtime.Failures == 0)
        {
            runtime.Session.Waiting = false;
            runtime.UnlistedSince = 0;
        }
    }

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
        var unchanged = generation == state.ObservedGeneration && info.Length == state.ObservedLength &&
            info.LastWriteTimeUtc.Ticks == state.ObservedWrite;
        // A file with the same size and write time isn't read again: a save the watcher missed still
        // changes them. After a failed save it's read again once it may be tried again.
        if (!force && unchanged && (state.Failures == 0 || Environment.TickCount64 < state.RetryAt)) return;
        // A waiting save is checked first: its file is only read and converted again once it can go through.
        if (unchanged && state.Waiting) await _backend.CheckAsync(s, _life.Token).ConfigureAwait(false);
        var tga = TextureFiles.Read(file);
        state.ObservedLength = info.Length;
        state.ObservedWrite = info.LastWriteTimeUtc.Ticks;
        state.ObservedGeneration = generation;
        var hash = TextureFiles.Hash(tga);
        if (hash == existing?.WorkingHash)
        {
            VariantSucceeded(runtime, state);
            return;
        }
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
        if (string.Equals(name, TextureModBackend.OriginalTextureOptionName, StringComparison.OrdinalIgnoreCase))
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
            VariantSucceeded(runtime, state);
            s.Status = $"Watching; variant \"{name}\" pixels are unchanged";
            Persist();
            return;
        }
        // Nothing is encoded for a save that can't go through now.
        await _backend.CheckAsync(s, _life.Token).ConfigureAwait(false);
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
        VariantSucceeded(runtime, state);
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
        var unchanged = info.Exists && generation == runtime.ObservedGeneration &&
            info.Length == runtime.ObservedLength && info.LastWriteTimeUtc.Ticks == runtime.ObservedWrite;
        // A file with the same size and write time isn't read again: a save the watcher missed still
        // changes them. After a failed save it's read again once it may be tried again.
        if (!force && unchanged && (runtime.Failures == 0 || Environment.TickCount64 < runtime.RetryAt)) return;
        // A waiting save is checked first: its file is only read and converted again once it can go through.
        if (unchanged && runtime.Waiting) await _backend.CheckAsync(s, _life.Token).ConfigureAwait(false);
        var tga = TextureFiles.Read(s.WorkingFile);
        runtime.ObservedLength = info.Length;
        runtime.ObservedWrite = info.LastWriteTimeUtc.Ticks;
        runtime.ObservedGeneration = generation;
        var hash = TextureFiles.Hash(tga);
        if (hash == s.WorkingHash)
        {
            // Back to what the game has: nothing is left to save.
            if (runtime.Failures > 0)
            {
                runtime.Succeeded();
                s.Status = "Watching for saves";
                Publish();
            }
            return;
        }
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
            runtime.Succeeded();
            s.Status = "Watching; saved pixels are unchanged";
            Persist();
            return;
        }
        // Nothing is encoded for a save that can't go through now.
        await _backend.CheckAsync(s, _life.Token).ConfigureAwait(false);
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
        runtime.Succeeded();
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
                // Its status already says why, and the session card can reconnect it.
                if (s.Conflict)
                {
                    results.Add(new ExternalTextureResult(save.SessionId, ExternalTextureOutcome.Failed, s.Status));
                    continue;
                }
                try
                {
                    ResumeIfPaused(runtime);
                    var outcome = save.Tga is null
                        ? await RestoreOriginalAsync(runtime).ConfigureAwait(false)
                        : await ApplyExternalSaveAsync(runtime, save.Tga, save.DroppedFile).ConfigureAwait(false);
                    results.Add(new ExternalTextureResult(save.SessionId, outcome, s.Status));
                    if (outcome is ExternalTextureOutcome.Applied or ExternalTextureOutcome.Restored)
                        refresh.Add(s);
                }
                catch (Exception error) when (error is not OperationCanceledException || !_life.IsCancellationRequested)
                {
                    if (error is not OperationCanceledException)
                        ReportSaveFailure(runtime, error, external: save.DroppedFile is null ? "Painter texture" : "Dropped image");
                    results.Add(new ExternalTextureResult(save.SessionId, ExternalTextureOutcome.Failed,
                        error is OperationCanceledException ? "The save was interrupted." : error.Message));
                }
            }
            if (refresh.Count > 0)
            {
                var status = await _backend.RefreshAsync(refresh.Select(s => new TextureRefresh(s, s.OriginalOptionId)).ToList(), _life.Token).ConfigureAwait(false);
                // A session paused to keep its working TGA goes on saying so, with any refresh warning.
                foreach (var s in refresh)
                    s.Status = !s.Paused ? status
                        : status.Contains("attention", StringComparison.Ordinal) ? s.Status + ". " + status : s.Status;
                Persist();
            }
        }
        finally { _gate.Release(); }
        return results;
    }

    /// <summary>
    /// Applies a Painter export, or an image dropped over the texture when <paramref name="dropped"/>
    /// names its file; a drop is recorded so <see cref="UndoReplaceAsync"/> can put back what it replaced.
    /// Callers hold the queue gate.
    /// </summary>
    private async Task<ExternalTextureOutcome> ApplyExternalSaveAsync(Runtime runtime, byte[] tga, string? dropped = null)
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
            var mirrored = MirrorWorkingFile(runtime, tga, hash);
            s.Status = (dropped is null ? "Painter texture unchanged" : $"{dropped} is the same as the texture") +
                (mirrored ? "" : KeptWorkingImage);
            Persist();
            return ExternalTextureOutcome.Unchanged;
        }
        // Nothing is encoded for a texture that can't be applied now.
        await _backend.CheckAsync(s, _life.Token).ConfigureAwait(false);
        s.Status = dropped is null ? "Converting Painter texture" : $"Converting {dropped}";
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
        s.Status = dropped is null ? "Saved from Substance Painter" : $"Replaced with {dropped}";
        if (dropped is not null)
            s.Replacement = new TextureReplacement { File = dropped, Backup = result.Backup, Hash = result.Hash, At = DateTimeOffset.UtcNow };
        RaiseFileChanged(s.TargetFile);
        if (!MirrorWorkingFile(runtime, tga, hash)) s.Status += KeptWorkingImage;
        if (!PersistCommit(runtime)) throw new IOException(s.Status);
        return ExternalTextureOutcome.Applied;
    }

    /// <summary>Scratch folder for dropped images and undone drops, kept out of the watched top level.</summary>
    private const string DropWorkFolder = "drop-work";

    /// <summary>
    /// Puts an image file in place of a texture, as dropping one on its row in On Screen does. The
    /// texture's session opens (or carries on) without the editor, and the image is converted, backed up
    /// and committed as a Painter export is. The session keeps what the drop replaced for <see cref="UndoReplaceAsync"/>.
    /// </summary>
    internal Task<ExternalTextureResult> ReplaceAsync(TextureEditRequest request, string image)
        => ReplaceAsync(request, Path.GetFileName(image), token => ReadImageFileAsync(image, token));

    /// <summary>
    /// Puts an image in place of a texture as <see cref="ReplaceAsync(TextureEditRequest, string)"/> does, with
    /// <paramref name="read"/> giving its bytes: another texture copied onto this one, read from its mod or the
    /// game's data. <paramref name="name"/> is the image's file name, whose extension says how to decode it.
    /// </summary>
    internal async Task<ExternalTextureResult> ReplaceAsync(TextureEditRequest request, string name, Func<CancellationToken, Task<byte[]>> read)
    {
        var id = await StartAsync(request, launchEditor: false).ConfigureAwait(false);
        var session = Sessions.First(s => s.Id == id);
        // The drop would land on the texture under the variant, where nobody sees it.
        if (VariantShowing(session, request) is { } variant)
            throw new IOException($"Your character shows this texture's variant \"{variant.Name}\". " +
                $"Select \"{TextureModBackend.OriginalTextureOptionName}\" in its Penumbra group to replace the texture itself.");
        CheckImageName(name);
        var bytes = await read(_life.Token).ConfigureAwait(false);
        if (bytes.Length is <= 0 or > TextureFiles.MaxBytes) throw new IOException($"{name} is empty or too large.");
        var tga = await ReadImageAsTgaAsync(session, name, bytes).ConfigureAwait(false);
        var results = await ApplyExternalAsync([new ExternalTextureSave(id, tga, name)], _life.Token).ConfigureAwait(false);
        return results.Single();
    }

    private static void CheckImageName(string name)
    {
        if (Path.GetExtension(name).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg" or ".tga" or ".dds" or ".tex"))
            throw new IOException($"{name} is not a png, jpeg, tga, dds or .tex image.");
    }

    private static async Task<byte[]> ReadImageFileAsync(string image, CancellationToken token)
    {
        var name = Path.GetFileName(image);
        CheckImageName(name);
        var info = new FileInfo(image);
        if (!info.Exists) throw new IOException($"{name} no longer exists.");
        if (info.Length is <= 0 or > TextureFiles.MaxBytes) throw new IOException($"{name} is empty or too large.");
        return await File.ReadAllBytesAsync(image, token).ConfigureAwait(false);
    }

    /// <summary>
    /// The image as the 32-bit TGA saves take. PNG, JPEG and TGA are decoded here; DDS and TEX go
    /// through the converter, which reads the game's formats (a TEX with its mip offsets normalized first).
    /// </summary>
    private async Task<byte[]> ReadImageAsTgaAsync(TextureEditSession s, string name, byte[] bytes)
    {
        var extension = Path.GetExtension(name).ToLowerInvariant();
        RgbaImage pixels;
        if (extension is ".dds" or ".tex")
        {
            var work = Path.Combine(s.Directory, DropWorkFolder);
            TextureFiles.EnsureLocalPath(work);
            Directory.CreateDirectory(work);
            var input = Path.Combine(work, "dropped" + extension);
            var output = Path.Combine(work, "dropped.tga");
            try
            {
                File.WriteAllBytes(input, extension == ".tex" ? TextureFiles.NormalizeMipOffsets(bytes) : bytes);
                await _backend.ConvertAsync(input, output, TextureType.Targa, false).ConfigureAwait(false);
                pixels = TgaImage.Read(TextureFiles.Read(output));
            }
            finally { DeleteDropWork(input, output); }
        }
        else
        {
            using var decoded = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(Studio.Services.StudioImages.Decoder, bytes);
            pixels = new RgbaImage(decoded.Width, decoded.Height);
            decoded.CopyPixelDataTo(pixels.Pixels);
        }
        return TgaImage.WriteBgra32(pixels);
    }

    /// <summary>Removes a drop's scratch files once they're used, so automatic cleanup can remove the session again.</summary>
    private void DeleteDropWork(params string[] files)
    {
        foreach (var file in files)
        {
            try
            {
                TextureFiles.EnsureLocalPath(file);
                File.Delete(file);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                _log(error, "A dropped image's scratch file could not be removed; cache cleanup removes it with its session.");
            }
        }
    }

    /// <summary>Whether the texture still holds the image dropped over it, so Undo can put back what it replaced.</summary>
    public static bool CanUndoReplace(TextureEditSession session)
        => session.Replacement is { } drop && drop.Hash == session.LastCommittedHash && !session.NeedsMod && !session.Conflict;

    /// <summary>The session whose drop over the request's texture can still be undone, if any.</summary>
    public TextureEditSession? FindUndoableReplace(TextureEditRequest request)
        => Sessions.FirstOrDefault(s => CanUndoReplace(s) && IsReusableFor(s, request) && VariantShowing(s, request) is null);

    /// <summary>
    /// Puts back the texture an image was dropped over: the backup the drop made, or the captured original
    /// when the drop created the texture's mod. The working image follows, so a later editor save starts
    /// from what the game shows.
    /// </summary>
    public async Task UndoReplaceAsync(Guid id)
    {
        await _gate.WaitAsync(_life.Token).ConfigureAwait(false);
        try
        {
            EnsureReady();
            var runtime = Get(id);
            var s = runtime.Session;
            if (!CanUndoReplace(s) || s.Replacement is not { } drop)
                throw new IOException("The texture changed after the image was dropped on it, so there is nothing to undo.");
            var source = drop.Backup.Length == 0 ? Path.Combine(s.Directory, "original.tex")
                : _backups.Resolve(_backups.Describe(s.ModDirectory, s.RelativePath).Id, Path.GetFileName(drop.Backup));
            // A backup of the captured file is written back with its mip offsets normalized, as a restore does.
            var (original, header) = TextureFiles.ReadOriginal(TextureFiles.Read(source));
            if (!TextureFiles.IsSessionFormat(header.Format, s)) throw new IOException("The backup does not match this session.");
            var result = await _backend.CommitAsync(s, original, () => !_life.IsCancellationRequested, _life.Token).ConfigureAwait(false);
            s.LastCommittedHash = result.Hash;
            s.LastBackup = result.Backup;
            s.SavedFormat = header.Format;
            s.Width = header.Width;
            s.Height = header.Height;
            s.LastSaved = DateTimeOffset.UtcNow;
            s.Replacement = null;
            s.Status = $"Undid the drop of {drop.File}";
            RaiseFileChanged(s.TargetFile);
            var work = Path.Combine(s.Directory, DropWorkFolder);
            TextureFiles.EnsureLocalPath(work);
            Directory.CreateDirectory(work);
            var restored = Path.Combine(work, "restored.tex");
            try
            {
                File.WriteAllBytes(restored, original);
                if (!await FollowWorkingImageAsync(runtime, restored).ConfigureAwait(false)) s.Status += KeptWorkingImage;
            }
            finally { DeleteDropWork(restored); }
            if (!PersistCommit(runtime)) throw new IOException(s.Status);
            _life.Token.ThrowIfCancellationRequested();
            var refresh = await RefreshOneAsync(s, s.OriginalOptionId).ConfigureAwait(false);
            if (refresh.Contains("attention", StringComparison.Ordinal)) s.Status += ". " + refresh;
            Persist();
        }
        catch (TextureConflictException error) when (_sessions.TryGetValue(id, out var conflicted))
        {
            // As a failed save: the file changed outside the session, which stops until it is reconnected.
            conflicted.Session.Conflict = true;
            conflicted.Session.Paused = true;
            conflicted.Session.Waiting = false;
            conflicted.Session.Status = error.Message;
            conflicted.Enabled = false;
            Persist();
            throw;
        }
        finally { _gate.Release(); }
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
        RaiseFileChanged(s.TargetFile);
        if (!await FollowWorkingImageAsync(runtime, originalPath).ConfigureAwait(false)) s.Status += KeptWorkingImage;
        if (!PersistCommit(runtime)) throw new IOException(s.Status);
        return ExternalTextureOutcome.Restored;
    }

    /// <summary>
    /// After a texture went back to an earlier file, the working image follows, so a later editor save
    /// starts from what the game shows. <paramref name="tex"/> is that file, with its mip offsets normalized.
    /// False when the working image held unsaved edits and was kept (see <see cref="MirrorWorkingFile"/>).
    /// </summary>
    private async Task<bool> FollowWorkingImageAsync(Runtime runtime, string tex)
    {
        var s = runtime.Session;
        var snapshot = Path.Combine(s.Directory, "snapshot.tga");
        var pixels = Path.Combine(s.Directory, "pixels.tex");
        await _backend.ConvertAsync(tex, snapshot, TextureType.Targa, false).ConfigureAwait(false);
        await _backend.ConvertAsync(snapshot, pixels, TextureType.RgbaTex, false).ConfigureAwait(false);
        var tga = TextureFiles.Read(snapshot);
        s.PixelHash = TextureFiles.PixelHash(TextureFiles.Read(pixels));
        return MirrorWorkingFile(runtime, tga, TextureFiles.Hash(tga));
    }

    /// <summary>Added to the status of a texture applied while the working TGA was kept.</summary>
    private const string KeptWorkingImage = ". Paused to keep your unsaved TGA edits";

    /// <summary>
    /// Writes an applied image over the working TGA and records it, so the watcher treats it as
    /// already saved. If the file is locked the old image and hash stay, which the watcher ignores too.
    /// A working TGA with edits the game doesn't have yet (a paused session, a save that waits or failed,
    /// one still settling) is kept, as Restore keeps it, and the session pauses so those edits don't go
    /// over the applied texture; Resume saves them. Returns false then.
    /// </summary>
    private bool MirrorWorkingFile(Runtime runtime, byte[] tga, string hash)
    {
        var s = runtime.Session;
        try
        {
            TextureFiles.EnsureLocalPath(s.WorkingFile);
            if (File.Exists(s.WorkingFile) && TextureFiles.Hash(TextureFiles.Read(s.WorkingFile)) != s.WorkingHash)
            {
                s.Paused = true;
                runtime.Enabled = false;
                runtime.Succeeded();
                return false;
            }
            if (s.WorkingHash == hash)
                return true;
            File.WriteAllBytes(s.WorkingFile, tga);
            s.WorkingHash = hash;
            runtime.Succeeded();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _log(error, "The working TGA could not be updated after a Painter save, a drop or its undo; the game already has the new texture.");
        }
        return true;
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
