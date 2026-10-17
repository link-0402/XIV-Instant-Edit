using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dalamud.Plugin.Services;
using InstantEdit.Services.PenumbraMods;
using Penumbra.Api.Enums;

namespace InstantEdit.Services.Materials;

/// <summary> The temporary mod a material preview registered, saved before Penumbra is called so a crash can't leave it behind. </summary>
internal sealed record MaterialPreviewRecord
{
    public required string Tag { get; init; }
    public required Guid CollectionId { get; init; }
    public required int Priority { get; init; }
    /// <summary> &lt;cache&gt;/material-preview/&lt;session&gt;. </summary>
    public required string SessionFolder { get; init; }
    public required DateTime CreatedUtc { get; init; }
    /// <summary> The object shown with the preview, redrawn when the preview goes without a Stop (unload, crash). </summary>
    public int? ObjectIndex { get; init; }
}

/// <summary>
/// Shows an edited material (and any new texture it reads) on a character before it is saved: a
/// Penumbra temporary mod at the highest priority in the character's collection points the game
/// paths at copies in the cache, then the character is redrawn so the game loads them. Nothing
/// goes into Penumbra's mod folder, so sync plugins don't send it. Each revision gets a new folder,
/// because the game never reads a path it already loaded again. The record in the config folder
/// lets the next start remove a preview a crash left behind. Each Material window has its own slot:
/// its own temporary mod (slot 0 keeps the first name) and record.
/// </summary>
internal sealed class MaterialPreviewService : IMaterialPreview, IDisposable
{
    public const string BaseTag = "XIV Instant Edit material preview";
    public const string CacheFolder = "material-preview";
    public const int Priority = int.MaxValue;
    private const string RecordName = "MaterialPreview";
    private static readonly Regex RecordSlot = new($"^{RecordName}-([0-9]{{1,3}})\\.json$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private const int VerifyAttempts = 10;
    private static readonly TimeSpan VerifyDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan StaleAge = TimeSpan.FromHours(24);
    private static readonly Regex SessionName = new("^[0-9a-f]{16}$", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly PenumbraIpc penumbra;
    private readonly PreviewModWriter previewMods;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly Func<string> cacheRoot;
    private readonly string recordPath;
    private readonly string tag;
    private readonly int slot;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource life = new();
    private readonly object sync = new();
    private readonly IDisposable? lifecycle;
    private readonly Task startup;
    private MaterialPreviewRecord? record;
    private string? session;
    private int revision;
    private bool shared;
    private string collectionName = string.Empty;
    private volatile bool disposed;

    public MaterialPreviewService(PenumbraIpc penumbra, PreviewModWriter previewMods, IFramework framework, IPluginLog log, string configDirectory, Func<string> cacheRoot,
        int slot = 0)
    {
        this.penumbra = penumbra;
        this.previewMods = previewMods;
        this.framework = framework;
        this.log = log;
        this.cacheRoot = cacheRoot;
        this.slot = slot;
        tag = slot == 0 ? BaseTag : $"{BaseTag} {slot + 1}";
        recordPath = Path.Combine(configDirectory, slot == 0 ? $"{RecordName}.json" : $"{RecordName}-{slot + 1}.json");
        record = LoadRecord();
        try { lifecycle = penumbra.SubscribePenumbraLifecycle(OnPenumbraRestarted); }
        catch (Exception e) { log.Warning(e, "Could not watch Penumbra restarts for the material preview."); }
        startup = Task.Run(CleanUpLeftoversAsync);
    }

    /// <summary> The slots past 0 whose record a crash may have left in <paramref name="configDirectory"/>. </summary>
    public static IEnumerable<int> RecordedSlots(string configDirectory)
    {
        try
        {
            return Directory.Exists(configDirectory)
                ? Directory.EnumerateFiles(configDirectory, $"{RecordName}-*.json")
                    .Select(file => RecordSlot.Match(Path.GetFileName(file)))
                    .Where(match => match.Success)
                    .Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) - 1)
                    .Where(found => found > 0)
                    .ToArray()
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary> A preview is registered in Penumbra. </summary>
    public bool Active { get { lock (sync) return session != null; } }

    public bool Busy => gate.CurrentCount == 0;

    /// <summary> Others using the collection see the preview too. </summary>
    public bool Shared { get { lock (sync) return shared; } }

    public string CollectionName { get { lock (sync) return collectionName; } }

    /// <summary> The file the shown preview redirects <paramref name="gamePath"/> to, or null without a preview. </summary>
    public string? FileFor(string gamePath)
    {
        lock (sync)
            return session == null || revision == 0
                ? null
                : Path.Combine(session, revision.ToString(CultureInfo.InvariantCulture),
                    PathRules.NormalizeGamePath(gamePath).ToLowerInvariant().Replace('/', Path.DirectorySeparatorChar));
    }

    public Task<string?> ShowAsync(int objectIndex, IReadOnlyDictionary<string, byte[]> files, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return Task.Run(() => ShowCoreAsync(objectIndex, files, token), CancellationToken.None);
    }

    private async Task<string?> ShowCoreAsync(int objectIndex, IReadOnlyDictionary<string, byte[]> files, CancellationToken token)
    {
        await startup.ConfigureAwait(false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, life.Token);
        await gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            var normalized = files.ToDictionary(file => PathRules.NormalizeGamePath(file.Key).ToLowerInvariant(), file => file.Value);
            if (normalized.Count == 0 || normalized.Any(file => !ModPaths.IsSafeGameResourcePath(file.Key, ".mtrl", ".tex", ".mdl") || file.Value.Length == 0))
                throw new InvalidDataException("The preview files are invalid.");
            if (!penumbra.Available)
                throw new IOException("Penumbra is not available.");
            var collection = await penumbra.TemporaryModCollectionAsync(objectIndex).ConfigureAwait(false)
                ?? throw new IOException("The character's Penumbra collection is unavailable.");
            var root = Path.Combine(Path.GetFullPath(cacheRoot()), CacheFolder);
            var modRoot = await previewMods.PreviewModRootAsync().ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(modRoot) && (PathRules.IsPathWithin(root, modRoot) || PathRules.IsPathWithin(modRoot, root)))
                throw new IOException("The cache folder is inside Penumbra's mod folder. Choose another cache folder in Settings.");
            TextureFiles.EnsureLocalPath(root);

            string? oldSession;
            int oldRevision;
            MaterialPreviewRecord? oldRecord;
            lock (sync) (oldSession, oldRevision, oldRecord) = (session, revision, record);
            var sameSession = oldSession != null && oldRecord?.CollectionId == collection.Id &&
                              PathRules.SamePhysicalPath(Path.GetDirectoryName(oldSession), root);
            var sessionFolder = sameSession ? oldSession! : Path.Combine(root, Guid.NewGuid().ToString("N")[..16]);
            var next = sameSession ? oldRevision + 1 : 1;
            var revisionFolder = Path.Combine(sessionFolder, next.ToString(CultureInfo.InvariantCulture));
            var redirects = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (gamePath, bytes) in normalized)
            {
                var file = Path.GetFullPath(Path.Combine(revisionFolder, gamePath.Replace('/', Path.DirectorySeparatorChar)));
                if (!PathRules.IsPathWithin(file, revisionFolder) || file.Length > 2047)
                    throw new InvalidDataException("The preview folder path is too long. Choose a shorter cache folder in Settings.");
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                await File.WriteAllBytesAsync(file, bytes, linked.Token).ConfigureAwait(false);
                redirects[gamePath] = file;
            }

            // A preview in another collection (the character's changed) goes first; its record is replaced below.
            if (oldRecord != null && !sameSession)
            {
                var removed = await RemoveAsync(oldRecord).ConfigureAwait(false);
                if (!removed.Gone)
                    throw new IOException(removed.Error ?? "Penumbra could not remove the last preview.");
            }
            var current = new MaterialPreviewRecord
            {
                Tag = tag, CollectionId = collection.Id, Priority = Priority, SessionFolder = sessionFolder, CreatedUtc = DateTime.UtcNow,
                ObjectIndex = objectIndex,
            };
            SaveRecord(current);
            lock (sync) (record, session, revision) = (current, sessionFolder, next);

            try
            {
                var added = await framework.RunOnFrameworkThread(() => penumbra.AddTemporaryModOnFramework(tag, collection.Id, redirects, Priority))
                    .ConfigureAwait(false);
                if (added != PenumbraApiEc.Success)
                    throw new IOException($"Penumbra could not add the preview ({added}).");
                await VerifyAsync(collection.Id, redirects).ConfigureAwait(false);
            }
            catch
            {
                try { await StopLockedAsync(redrawObject: null).ConfigureAwait(false); }
                catch (Exception stop) { log.Warning(stop, "Could not end the material preview after a failed update."); }
                throw;
            }

            lock (sync)
            {
                collectionName = collection.Name;
                shared = !collection.IndividualSet;
            }
            foreach (var older in Subfolders(sessionFolder).Where(name => name != next.ToString(CultureInfo.InvariantCulture)))
                TryDelete(Path.Combine(sessionFolder, older));
            if (oldSession != null && !sameSession)
                TryDelete(oldSession);
            log.Information($"Material preview {next} registered in {collection.Name} for {string.Join(", ", redirects.Keys)}.");
            return await penumbra.RedrawObjectAsync(objectIndex).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary> Removes the preview; with an object index, redraws it so it loads its files without the preview. </summary>
    public Task StopAsync(int? redrawObject)
        => disposed ? Task.CompletedTask : Task.Run(async () =>
        {
            await startup.ConfigureAwait(false);
            await gate.WaitAsync(life.Token).ConfigureAwait(false);
            try { await StopLockedAsync(redrawObject).ConfigureAwait(false); }
            finally { gate.Release(); }
        }, CancellationToken.None);

    private async Task StopLockedAsync(int? redrawObject)
    {
        MaterialPreviewRecord? current;
        string? folder;
        lock (sync) (current, folder) = (record, session);
        if (current != null)
        {
            var removed = await RemoveAsync(current).ConfigureAwait(false);
            if (!removed.Gone)
                throw new IOException(removed.Error ?? "Penumbra could not remove the preview.");
            TryDelete(current.SessionFolder);
            ClearRecord();
        }
        if (folder != null && (current == null || !PathRules.SamePhysicalPath(folder, current.SessionFolder)))
            TryDelete(folder);
        lock (sync)
        {
            record = null;
            session = null;
            revision = 0;
            collectionName = string.Empty;
            shared = false;
        }
        if (redrawObject is { } index && (current != null || folder != null))
            await penumbra.RedrawObjectAsync(index).ConfigureAwait(false);
    }

    private async Task VerifyAsync(Guid collection, IReadOnlyDictionary<string, string> redirects)
    {
        var gamePaths = redirects.Keys.ToArray();
        for (var attempt = 1; ; attempt++)
        {
            var resolved = await penumbra.ResolveCollectionPathsAsync(collection, gamePaths).ConfigureAwait(false);
            if (gamePaths.Select((path, i) => PathRules.SamePhysicalPath(resolved[i], redirects[path])).All(same => same))
                return;
            if (attempt >= VerifyAttempts)
                throw new IOException("Penumbra did not use the preview. Check that mods are enabled.");
            await Task.Delay(VerifyDelay, life.Token).ConfigureAwait(false);
        }
    }

    private Task<(bool Gone, string? Error)> RemoveAsync(MaterialPreviewRecord current)
        => framework.RunOnFrameworkThread(() => penumbra.RemoveTemporaryModOnFramework(current.Tag, current.CollectionId, current.Priority));

    /// <summary> Removes a preview a crash left behind and session folders a day old. </summary>
    private async Task CleanUpLeftoversAsync()
    {
        try
        {
            MaterialPreviewRecord? leftover;
            lock (sync) leftover = record;
            if (leftover != null)
            {
                var removed = await penumbra.RemoveTemporaryModAsync(leftover.Tag, leftover.CollectionId, leftover.Priority).ConfigureAwait(false);
                if (!removed.Gone)
                {
                    log.Warning($"Could not remove the last material preview from Penumbra: {removed.Error}");
                    return;
                }
                TryDelete(leftover.SessionFolder);
                ClearRecord();
                lock (sync) record = null;
                // The character may still wear the preview's files, which are gone now.
                if (leftover.ObjectIndex is { } index)
                    await penumbra.RedrawObjectAsync(index).ConfigureAwait(false);
            }
            // Slot 0 starts with the plugin; a later slot could find another window's long-shown preview stale.
            if (slot != 0)
                return;
            string root;
            try { root = Path.Combine(Path.GetFullPath(cacheRoot()), CacheFolder); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return; }
            foreach (var name in Subfolders(root).Where(name => SessionName.IsMatch(name)))
            {
                var folder = Path.Combine(root, name);
                try
                {
                    if (Directory.GetLastWriteTimeUtc(folder) < DateTime.UtcNow - StaleAge)
                        TryDelete(folder);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception e)
        {
            log.Warning(e, "Could not clean up the last material preview.");
        }
    }

    /// <summary> Penumbra keeps temporary mods in memory only: once it restarts the preview is gone. </summary>
    private void OnPenumbraRestarted()
    {
        if (disposed)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                await startup.ConfigureAwait(false);
                await gate.WaitAsync(life.Token).ConfigureAwait(false);
                try { await StopLockedAsync(redrawObject: null).ConfigureAwait(false); }
                finally { gate.Release(); }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { log.Warning(e, "Could not end the material preview after Penumbra restarted."); }
        });
    }

    private MaterialPreviewRecord? LoadRecord()
    {
        try
        {
            if (!File.Exists(recordPath))
                return null;
            var loaded = JsonSerializer.Deserialize<MaterialPreviewRecord>(File.ReadAllText(recordPath), Json);
            return loaded is { Tag.Length: > 0 } && loaded.CollectionId != Guid.Empty && IsSessionFolder(loaded.SessionFolder) ? loaded : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            log.Warning(e, "Could not read the material preview record.");
            return null;
        }
    }

    private void SaveRecord(MaterialPreviewRecord value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(recordPath)!);
        var temporary = recordPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json));
        File.Move(temporary, recordPath, true);
    }

    private void ClearRecord()
    {
        try
        {
            if (File.Exists(recordPath))
                File.Delete(recordPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Warning(e, "Could not delete the material preview record.");
        }
    }

    /// <summary> A session folder this feature made: a session name directly inside a material-preview folder. </summary>
    private static bool IsSessionFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return false;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return SessionName.IsMatch(Path.GetFileName(full)) &&
               string.Equals(Path.GetFileName(Path.GetDirectoryName(full)), CacheFolder, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> Subfolders(string folder)
    {
        try
        {
            return Directory.Exists(folder) ? Directory.EnumerateDirectories(folder).Select(path => Path.GetFileName(path)).ToArray() : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary> Deletes a folder inside a preview session, best effort; a file the game still reads stays for the next cleanup. </summary>
    private void TryDelete(string folder)
    {
        try
        {
            var full = Path.GetFullPath(folder);
            var inSession = IsSessionFolder(full) || IsSessionFolder(Path.GetDirectoryName(full));
            if (!inSession || !Directory.Exists(full) || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                return;
            Directory.Delete(full, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            log.Debug(e, "Could not delete the material preview folder {Folder}.", folder);
        }
    }

    /// <summary>
    /// Removes the temporary mod and redraws the character, so it doesn't keep wearing preview files
    /// that are deleted next (Penumbra then shows the material as unavailable). Waits briefly when
    /// called off the framework thread.
    /// </summary>
    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        life.Cancel();
        lifecycle?.Dispose();
        try
        {
            MaterialPreviewRecord? current;
            lock (sync) current = record;
            if (current == null)
                return;
            (bool Gone, string? Error) Remove()
            {
                var removed = penumbra.RemoveTemporaryModOnFramework(current.Tag, current.CollectionId, current.Priority);
                if (removed.Gone && current.ObjectIndex is { } index)
                    penumbra.RedrawObjectOnFramework(index);
                return removed;
            }
            (bool Gone, string? Error) removed;
            if (framework.IsInFrameworkUpdateThread)
                removed = Remove();
            else
            {
                var task = framework.RunOnFrameworkThread(Remove);
                removed = task.Wait(TimeSpan.FromSeconds(2)) ? task.Result : (false, "Timed out.");
            }
            if (!removed.Gone)
            {
                log.Warning($"Could not remove the material preview from Penumbra while unloading: {removed.Error}");
                return;
            }
            TryDelete(current.SessionFolder);
            ClearRecord();
        }
        catch (Exception e)
        {
            log.Warning(e, "Could not remove the material preview while unloading; the next load removes it.");
        }
    }
}
