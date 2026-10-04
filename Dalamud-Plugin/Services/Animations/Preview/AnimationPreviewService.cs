using System.Collections.Immutable;
using Dalamud.Plugin.Services;
using Penumbra.Api.Enums;

namespace InstantEdit.Services.Animations.Preview;

/// <summary>
/// Shows edited animation files in game through a Penumbra temporary mod at int.MaxValue in the
/// character's effective collection. Nothing goes into Penumbra's mod folder or mod list: the files
/// live in the cache (outside Penumbra's folder, so sync plugins don't upload them) and the mod only
/// in Penumbra's memory. Every revision gets a new folder, because the game and Penumbra never
/// re-read a file path they already loaded; the clip still has to be replayed (or the character
/// redrawn, for idle and movement) before the game loads it. A record in the config folder is saved
/// before Penumbra is called, so a crash or a failed unload is cleaned up on the next load.
/// </summary>
internal sealed class AnimationPreviewService : IDisposable
{
    public const string Tag = "XIV Instant Edit animation preview";
    public const int Priority = int.MaxValue;
    private const int VerifyAttempts = 10;
    private static readonly TimeSpan VerifyDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan DisposeWait = TimeSpan.FromSeconds(2);

    private readonly PenumbraService _penumbra;
    private readonly IFramework _framework;
    private readonly IPluginLog _log;
    private readonly Func<string> _cacheRoot;
    private readonly AnimationPreviewStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _life = new();
    private readonly object _sync = new();
    private readonly IDisposable? _lifecycle;
    private readonly IClientState? _clientState;
    private volatile bool _ending;
    // After a failed stop on leaving GPose, the next try waits until this tick count.
    private long _retryStopAfter;
    private readonly bool _recordLost;
    private readonly Task _startup;
    private volatile bool _disposed;
    private string? _sessionFolder;
    private int _revision;
    private Guid _collection;
    private string _collectionName = string.Empty;
    private bool _shared;
    private string _status = string.Empty;
    private string _error = string.Empty;

    /// <param name="cacheRoot">The managed cache root (TextureFiles.EnsureCacheRoot); may throw while the cache is broken.</param>
    /// <param name="clientState">When given, leaving GPose ends the preview.</param>
    public AnimationPreviewService(PenumbraService penumbra, IFramework framework, IPluginLog log, string configDirectory, Func<string> cacheRoot,
        IClientState? clientState = null)
    {
        _penumbra = penumbra;
        _framework = framework;
        _log = log;
        _cacheRoot = cacheRoot;
        _store = new AnimationPreviewStore(configDirectory);
        _store.Load();
        if (_store.LoadError.Length > 0)
        {
            log.Warning(_store.LoadError);
            _recordLost = true;
        }
        try { _lifecycle = penumbra.SubscribePenumbraLifecycle(OnPenumbraRestarted); }
        catch (Exception e) { log.Warning(e, "Could not watch Penumbra restarts for the animation preview."); }
        _startup = Task.Run(CleanUpLeftoversAsync);
        if (clientState != null)
        {
            _clientState = clientState;
            framework.Update += OnUpdate;
        }
    }

    /// <summary>
    /// A preview is for GPose only: once it ends, so does the preview, so your character in the
    /// world never keeps playing an edit that was not saved.
    /// </summary>
    private void OnUpdate(IFramework framework)
    {
        if (_disposed || _ending || _clientState is not { IsGPosing: false } || !Active ||
            Environment.TickCount64 < Interlocked.Read(ref _retryStopAfter))
            return;
        _ending = true;
        _ = Task.Run(async () =>
        {
            try
            {
                await _startup.ConfigureAwait(false);
                await _gate.WaitAsync(_life.Token).ConfigureAwait(false);
                try { await StopLockedAsync("GPose ended. The preview ended.").ConfigureAwait(false); }
                finally { _gate.Release(); }
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                _log.Warning(e, "Could not end the animation preview after GPose ended.");
                Interlocked.Exchange(ref _retryStopAfter, Environment.TickCount64 + 5000);
            }
            finally { _ending = false; }
        });
    }

    /// <summary> A preview is registered in Penumbra. </summary>
    public bool Active { get { lock (_sync) return _sessionFolder != null; } }

    /// <summary> The registered revision, counted from 1 per preview session; 0 when none is. </summary>
    public int Revision { get { lock (_sync) return _revision; } }

    /// <summary> The preview's collection is not assigned to the character alone, so others using it see the preview too. </summary>
    public bool CollectionShared { get { lock (_sync) return _shared; } }

    public string CollectionName { get { lock (_sync) return _collectionName; } }

    public string Status { get { lock (_sync) return _status; } }

    public string Error { get { lock (_sync) return _error; } }

    /// <summary> A show or stop is running. </summary>
    public bool Busy => _gate.CurrentCount == 0;

    /// <summary>
    /// Shows <paramref name="files"/> (game path to bytes) on the collection of the object at
    /// <paramref name="objectIndex"/>: writes them to a new revision folder, saves the record,
    /// registers or updates the temporary mod, checks Penumbra resolves every game path to its new
    /// file, then deletes the older revisions. Any failure after Penumbra was called ends the preview.
    /// </summary>
    public Task<AnimationPreviewRevision> ShowAsync(ushort objectIndex, IReadOnlyDictionary<string, byte[]> files, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(files);
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Off the caller's thread: from ImGui draw, framework calls would otherwise run inline with the file work.
        return Task.Run(() => ShowCoreAsync(objectIndex, files, token), CancellationToken.None);
    }

    private async Task<AnimationPreviewRevision> ShowCoreAsync(ushort objectIndex, IReadOnlyDictionary<string, byte[]> files, CancellationToken token)
    {
        await _startup.ConfigureAwait(false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _life.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            var shown = await ShowLockedAsync(objectIndex, files, linked.Token).ConfigureAwait(false);
            SetMessages(shown.CollectionShared ? "Preview on. Others using this collection see it too." : "Preview on.", string.Empty);
            return shown;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            SetMessages(Active ? Status : string.Empty, e.Message);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary> Removes the temporary mod and deletes the preview's files. The character keeps the loaded preview until the clip replays or it is redrawn. </summary>
    public Task StopAsync() => _disposed ? Task.CompletedTask : Task.Run(StopCoreAsync, CancellationToken.None);

    private async Task StopCoreAsync()
    {
        await _startup.ConfigureAwait(false);
        await _gate.WaitAsync(_life.Token).ConfigureAwait(false);
        try
        {
            await StopLockedAsync("Preview off.").ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            SetMessages(Status, e.Message);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<AnimationPreviewRevision> ShowLockedAsync(ushort objectIndex, IReadOnlyDictionary<string, byte[]> files, CancellationToken token)
    {
        var bytes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        long total = 0;
        foreach (var (raw, data) in files)
        {
            var gamePath = AnimationPreviewRules.GamePath(raw);
            if (data is not { Length: > 0 and <= AnimationPreviewRules.MaxFileBytes })
                throw new InvalidDataException($"The preview file {gamePath} is empty or too large.");
            if (!bytes.TryAdd(gamePath, data))
                throw new InvalidDataException($"The preview has {gamePath} twice.");
            total += data.Length;
        }
        if (total > AnimationPreviewRules.MaxTotalBytes)
            throw new InvalidDataException("The preview files are too large.");
        if (!_penumbra.Available)
            throw new IOException("Penumbra is not available.");

        var collection = await _penumbra.TemporaryModCollectionAsync(objectIndex).ConfigureAwait(false)
            ?? throw new IOException("The character's Penumbra collection is unavailable.");
        var previewRoot = AnimationPreviewRules.PreviewRoot(_cacheRoot());
        var modRoot = await _penumbra.PreviewModRootAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(modRoot))
            throw new IOException("Penumbra's mod folder is unavailable.");
        if (AnimationPreviewRules.Overlaps(previewRoot, modRoot))
            throw new IOException("The cache folder is inside Penumbra's mod folder. Choose another cache folder in Settings.");
        TextureFiles.EnsureLocalPath(previewRoot);
        token.ThrowIfCancellationRequested();

        string? oldSession;
        int oldRevision;
        Guid oldCollection;
        lock (_sync) (oldSession, oldRevision, oldCollection) = (_sessionFolder, _revision, _collection);
        // A changed cache folder starts a new session there; the old one goes once the new revision is live.
        var sameSession = oldSession != null && PathRules.SamePhysicalPath(Path.GetDirectoryName(oldSession), previewRoot);
        var sessionFolder = sameSession ? oldSession! : AnimationPreviewRules.SessionFolder(previewRoot, AnimationPreviewRules.NewSessionName());
        var revision = sameSession ? oldRevision + 1 : 1;
        var revisionFolder = AnimationPreviewRules.RevisionFolder(sessionFolder, revision);
        var plan = AnimationPreviewRules.PlanFiles(revisionFolder, bytes.Keys);

        try
        {
            // Left by an attempt that failed before Penumbra was called; nothing points at it.
            if (Directory.Exists(revisionFolder))
                Directory.Delete(revisionFolder, true);
            foreach (var (gamePath, file) in plan)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                await File.WriteAllBytesAsync(file, bytes[gamePath], token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
        }
        catch
        {
            TryDelete(sameSession ? revisionFolder : sessionFolder, sameSession ? null : sessionFolder);
            throw;
        }

        try
        {
            // A preview the last start could not take down is removed before a new one replaces its record.
            if (oldSession == null && _store.Record is { } leftover)
            {
                var cleared = await RemoveOnFrameworkAsync(leftover.Tag, leftover.CollectionId, leftover.Priority).ConfigureAwait(false);
                if (!cleared.Gone)
                    throw new IOException(cleared.Error ?? "Penumbra could not remove the last preview.");
                TryDelete(leftover.SessionFolder, leftover.SessionFolder);
                _store.Clear();
            }
            // A preview moves with the character's collection: take it out of the old one first.
            if (oldSession != null && oldCollection != collection.Id)
            {
                var moved = await RemoveOnFrameworkAsync(Tag, oldCollection, Priority).ConfigureAwait(false);
                if (!moved.Gone)
                    throw new IOException(moved.Error ?? "Penumbra could not remove the preview.");
            }
            var record = _store.Record is { } current && current.CollectionId == collection.Id &&
                         PathRules.SamePhysicalPath(current.SessionFolder, sessionFolder)
                ? current
                : new AnimationPreviewRecord
                {
                    Tag = Tag,
                    CollectionId = collection.Id,
                    Priority = Priority,
                    SessionFolder = sessionFolder,
                    CreatedUtc = DateTime.UtcNow,
                };
            if (!ReferenceEquals(record, _store.Record))
                _store.Save(record);
            // The revision counts from here: if this show fails and so does taking it down, the
            // next one must not reuse a folder the still-registered mod points at.
            lock (_sync) (_sessionFolder, _collection, _revision) = (sessionFolder, collection.Id, revision);
        }
        catch
        {
            TryDelete(sameSession ? revisionFolder : sessionFolder, sameSession ? null : sessionFolder);
            throw;
        }

        // From here the preview may be registered: a failure ends it rather than leaving a half state.
        try
        {
            var added = await _framework.RunOnFrameworkThread(() =>
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _penumbra.AddTemporaryModOnFramework(Tag, collection.Id, plan, Priority);
            }).ConfigureAwait(false);
            if (added != PenumbraApiEc.Success)
                throw new IOException($"Penumbra could not add the preview ({added}).");
            await VerifyAsync(collection.Id, plan).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            try
            {
                await StopLockedAsync(string.Empty).ConfigureAwait(false);
                // Nothing points at the old cache folder's session any more.
                if (oldSession != null && !sameSession)
                    TryDelete(oldSession, oldSession);
            }
            catch (Exception stop) { _log.Warning(stop, "Could not end the animation preview after a failed update."); }
            if (error is ObjectDisposedException)
                throw new OperationCanceledException("The plugin is unloading.", error);
            throw;
        }

        lock (_sync)
        {
            _revision = revision;
            _collectionName = collection.Name;
            _shared = !collection.IndividualSet;
        }
        foreach (var older in AnimationPreviewRules.OlderRevisions(SubfolderNames(sessionFolder), revision))
            TryDelete(Path.Combine(sessionFolder, older), null);
        if (oldSession != null && !sameSession)
            TryDelete(oldSession, oldSession);
        return new AnimationPreviewRevision(revision, revisionFolder, plan, collection.Id, collection.Name, !collection.IndividualSet);
    }

    /// <summary> Waits until Penumbra resolves every game path to its new file; a full recalculation can delay the change. </summary>
    private async Task VerifyAsync(Guid collection, ImmutableDictionary<string, string> plan)
    {
        var gamePaths = plan.Keys.ToArray();
        for (var attempt = 1; ; attempt++)
        {
            var resolved = await _penumbra.ResolveCollectionPathsAsync(collection, gamePaths).ConfigureAwait(false);
            var missing = gamePaths.Where((gamePath, i) => !PathRules.SamePhysicalPath(resolved[i], plan[gamePath])).ToArray();
            if (missing.Length == 0)
                return;
            if (attempt >= VerifyAttempts)
                throw new IOException($"Penumbra did not use the preview for {missing[0]}. Check that mods are enabled.");
            await Task.Delay(VerifyDelay, _life.Token).ConfigureAwait(false);
        }
    }

    /// <summary> Removes the registered mod, then deletes its files and the record. Files stay while Penumbra may still use them. </summary>
    private async Task StopLockedAsync(string status)
    {
        var record = _store.Record;
        string? session;
        lock (_sync) session = _sessionFolder;
        if (record != null)
        {
            var removed = await RemoveOnFrameworkAsync(record.Tag, record.CollectionId, record.Priority).ConfigureAwait(false);
            if (!removed.Gone)
                throw new IOException(removed.Error ?? "Penumbra could not remove the preview.");
            TryDelete(record.SessionFolder, record.SessionFolder);
            _store.Clear();
        }
        if (session != null && (record == null || !PathRules.SamePhysicalPath(session, record.SessionFolder)))
            TryDelete(session, session);
        lock (_sync)
        {
            _sessionFolder = null;
            _revision = 0;
            _collection = Guid.Empty;
            _collectionName = string.Empty;
            _shared = false;
            _status = status;
            _error = string.Empty;
        }
    }

    private Task<(bool Gone, string? Error)> RemoveOnFrameworkAsync(string tag, Guid collection, int priority)
        => _framework.RunOnFrameworkThread(() => _penumbra.RemoveTemporaryModOnFramework(tag, collection, priority));

    /// <summary>
    /// A leftover record means the last session ended without removing its mod: remove it and delete
    /// its folder, then any other session folder a day old. With the record unreadable, every
    /// collection is cleared first.
    /// </summary>
    private async Task CleanUpLeftoversAsync()
    {
        try
        {
            if (_store.Record is { } record)
            {
                var removed = await _penumbra.RemoveTemporaryModAsync(record.Tag, record.CollectionId, record.Priority).ConfigureAwait(false);
                if (!removed.Gone)
                {
                    _log.Warning($"Could not remove the last animation preview from Penumbra: {removed.Error}");
                    SetMessages(string.Empty, "The last preview is still in Penumbra. Restart Penumbra to remove it.");
                    return;
                }
                TryDelete(record.SessionFolder, record.SessionFolder);
                _store.Clear();
            }
            else if (_recordLost)
            {
                var removed = await _penumbra.RemoveTemporaryModEverywhereAsync(Tag, Priority).ConfigureAwait(false);
                if (!removed.Gone)
                {
                    _log.Warning($"Could not remove a lost animation preview from Penumbra: {removed.Error}");
                    return;
                }
            }
            string previewRoot;
            try { previewRoot = AnimationPreviewRules.PreviewRoot(_cacheRoot()); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return;
            }
            var sessions = SubfolderNames(previewRoot).Select(name => (name, LastWriteUtc(Path.Combine(previewRoot, name))));
            foreach (var stale in AnimationPreviewRules.StaleSessions(sessions, null, DateTime.UtcNow - AnimationPreviewRules.StaleSessionAge))
                TryDelete(Path.Combine(previewRoot, stale), Path.Combine(previewRoot, stale));
        }
        catch (Exception e)
        {
            _log.Warning(e, "Could not clean up the last animation preview.");
        }
    }

    /// <summary> Penumbra keeps temporary mods in memory only: once it restarts the preview is gone. </summary>
    private void OnPenumbraRestarted()
    {
        if (_disposed || !Active && _store.Record == null)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                await _startup.ConfigureAwait(false);
                await _gate.WaitAsync(_life.Token).ConfigureAwait(false);
                try { await StopLockedAsync("Penumbra restarted. The preview ended.").ConfigureAwait(false); }
                finally { _gate.Release(); }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { _log.Warning(e, "Could not end the animation preview after Penumbra restarted."); }
        });
    }

    private void SetMessages(string status, string error)
    {
        lock (_sync)
        {
            _status = status;
            _error = error;
        }
    }

    /// <summary> When a folder last changed; unreadable folders count as new, so they are kept. </summary>
    private static DateTime LastWriteUtc(string folder)
    {
        try { return Directory.GetLastWriteTimeUtc(folder); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return DateTime.MaxValue; }
    }

    private static IEnumerable<string> SubfolderNames(string folder)
    {
        try
        {
            return Directory.Exists(folder)
                ? Directory.EnumerateDirectories(folder).Select(path => Path.GetFileName(path)).ToArray()
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Deletes a preview folder, best effort; a file the game still reads stays for the next cleanup.
    /// With <paramref name="session"/> set, the folder must be that session folder.
    /// </summary>
    private void TryDelete(string folder, string? session)
    {
        try
        {
            if (session != null && !AnimationPreviewRules.IsSessionFolder(session))
                return;
            if (!Directory.Exists(folder) || (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
                return;
            Directory.Delete(folder, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Debug(e, "Could not delete the animation preview folder {Folder}.", folder);
        }
    }

    /// <summary> Removes the temporary mod on the framework thread, waiting briefly when called from elsewhere. </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_clientState != null)
            _framework.Update -= OnUpdate;
        _life.Cancel();
        _lifecycle?.Dispose();
        try
        {
            // Read the record on the framework thread too, so a show queued there either finished or refuses.
            (bool Gone, string? Error, AnimationPreviewRecord? Record) Remove()
            {
                var record = _store.Record;
                if (record == null)
                    return (true, null, null);
                var (gone, error) = _penumbra.RemoveTemporaryModOnFramework(record.Tag, record.CollectionId, record.Priority);
                return (gone, error, record);
            }
            (bool Gone, string? Error, AnimationPreviewRecord? Record) removed;
            if (_framework.IsInFrameworkUpdateThread)
                removed = Remove();
            else
            {
                var task = _framework.RunOnFrameworkThread(Remove);
                removed = task.Wait(DisposeWait) ? task.Result : (false, "Timed out.", null);
            }
            if (removed.Record == null)
                return;
            if (!removed.Gone)
            {
                _log.Warning($"Could not remove the animation preview from Penumbra while unloading: {removed.Error}");
                return;
            }
            TryDelete(removed.Record.SessionFolder, removed.Record.SessionFolder);
            _store.Clear();
        }
        catch (Exception e)
        {
            _log.Warning(e, "Could not remove the animation preview while unloading; the next load removes it.");
        }
    }
}
