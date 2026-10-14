using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InstantEdit.Studio.Services.Check;
using InstantEdit.Studio.Services.Project;

namespace InstantEdit.Studio.Services.Library;

/// <summary>
/// The library filters' data, scanned on a worker the first time a filter or sort needs it: which
/// mods are enabled anywhere, which are on in the player's collection, which are installed twice,
/// what each one changes and how big it is. What is read from a mod's files is kept per mod folder,
/// meta.json last-write time and a stamp of its files (<see cref="Stamp"/>), in memory and in the cache folder
/// (<see cref="LibraryScanCache"/>), so a later scan, also in a later session, reads only mods that
/// are new or changed (or were <see cref="Invalidate"/>d). Dalamud-free; the Penumbra calls come in
/// as delegates and run on the worker.
/// </summary>
internal sealed class LibraryScans
{
    private readonly Func<IReadOnlyDictionary<Guid, string>?> _collections;
    private readonly Func<Guid, IReadOnlyDictionary<string, bool>?> _enabledByMod;
    private readonly Func<string, IReadOnlyList<(string, ChangeKind)>?> _changedItems;
    private readonly Func<string, CancellationToken, ModFingerprint?> _fingerprint;
    private readonly Func<string?>? _cacheFile;

    private readonly object _gate = new();
    // Held while the cache file is replaced or deleted; ForgetAll bumps _forgotten under it, so a save in flight drops its snapshot.
    private readonly object _fileGate = new();
    private long _forgotten;
    private readonly Dictionary<string, ModScanEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    // Mods dropped since the cache file was last written, so a session that never loads it still forgets them there.
    private readonly HashSet<string> _dropped = new(StringComparer.OrdinalIgnoreCase);
    private string? _cachePath;
    private bool _loaded;
    private bool _dirty;

    private Task _running = Task.CompletedTask;
    private CancellationTokenSource? _cancel;
    private long _generation;
    private long _usageGeneration;
    private long? _usageReadFor;
    private (Guid Collection, long Generation)? _activeReadFor;
    private (long Signature, long Generation)? _duplicatesFor;
    private (long Signature, long Generation)? _changesFor;
    private (long Signature, long Generation)? _sizesFor;
    private IReadOnlyList<InstalledMod>? _lastList;
    private long _lastSignature;

    private volatile ModUsage? _usage;
    private volatile IReadOnlySet<string>? _active;
    private volatile bool _activeUnknown;
    private volatile IReadOnlyDictionary<string, IReadOnlyList<string>>? _duplicatesOf;
    private volatile IReadOnlyDictionary<string, IReadOnlyList<ChangeTag>>? _changes;
    private volatile IReadOnlyDictionary<string, long>? _sizes;
    private volatile CheckProgress? _progress;
    private int _revision;

    /// <param name="collections">Penumbra's collections by id; null when Penumbra can't be asked.</param>
    /// <param name="enabledByMod">A collection's mods by folder name and whether each is enabled, inheritance on, temporary settings off.</param>
    /// <param name="changedItems">Penumbra's changed items of a mod folder; null when Penumbra said nothing (tags made without them aren't kept).</param>
    /// <param name="fingerprint">Reads a mod for the duplicate check (tests count the calls); <see cref="DuplicateModScan.Fingerprint(string, CancellationToken)"/> when null.</param>
    /// <param name="cacheFile">Where the results are kept between sessions, asked once on the calling thread; null (or a throw) keeps them in memory only.</param>
    public LibraryScans(Func<IReadOnlyDictionary<Guid, string>?> collections, Func<Guid, IReadOnlyDictionary<string, bool>?> enabledByMod,
        Func<string, IReadOnlyList<(string, ChangeKind)>?> changedItems, Func<string, ModFingerprint?>? fingerprint = null, Func<string?>? cacheFile = null)
    {
        _collections  = collections;
        _enabledByMod = enabledByMod;
        _changedItems = changedItems;
        _fingerprint  = fingerprint is null ? DuplicateModScan.Fingerprint : (folder, _) => fingerprint(folder);
        _cacheFile    = cacheFile;
    }

    /// <summary>The running scan's step, or null when none runs.</summary>
    public CheckProgress? Progress => _progress;

    /// <summary>Folder names of the mods enabled in any collection; null until read.</summary>
    public ModUsage? Usage => _usage;

    /// <summary>
    /// Folder names of the mods enabled in the collection last asked for (<c>activeIn</c>); null until
    /// read, while no collection is asked for, and when Penumbra didn't answer for it (<see cref="ActiveUnknown"/>).
    /// </summary>
    public IReadOnlySet<string>? Active => _active;

    /// <summary>The last read of the collection asked for got no answer from Penumbra.</summary>
    public bool ActiveUnknown => _activeUnknown;

    /// <summary>Tests: runs after a save wrote its snapshot, before the snapshot replaces the cache file.</summary>
    internal Action? SavingForRegression { get; set; }

    /// <summary>For each mod installed more than once, the folder names of its other copies; null until scanned.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? DuplicatesOf => _duplicatesOf;

    /// <summary>What each mod changes, by folder name; null until scanned.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ChangeTag>>? Changes => _changes;

    /// <summary>The total size in bytes of every file in each mod's folder, by folder name; null until scanned.</summary>
    public IReadOnlyDictionary<string, long>? Sizes => _sizes;

    /// <summary>Goes up each time a scan publishes results, so a view knows to filter again.</summary>
    public int Revision => Volatile.Read(ref _revision);

    /// <summary>Why the last scan stopped early, or null. What it was asked for isn't scanned again until a mod changes.</summary>
    public string? Error { get; private set; }

    /// <summary>The running scan, or a finished task (tests wait on it).</summary>
    internal Task Idle => _running;

    /// <summary>
    /// Starts a scan of what is asked for and missing or out of date for this list of mods; does
    /// nothing while a scan runs (the next call after it picks up what changed meanwhile). Called
    /// every frame the library shows, from the thread that draws it.
    /// </summary>
    /// <param name="activeIn">The collection whose enabled mods are <see cref="Active"/>; null reads none.</param>
    public void Need(bool usage, bool duplicates, bool changes, IReadOnlyList<InstalledMod> mods, bool sizes = false, Guid? activeIn = null)
    {
        if (!_running.IsCompleted)
            return;
        if (activeIn is not { } asked)
        {
            // No collection to go by: no Active set, and the next one asked for is read.
            _active = null;
            _activeUnknown = false;
            _activeReadFor = null;
        }
        else if (_activeReadFor?.Collection != asked)
        {
            // Another collection's set isn't shown while this one is read.
            _active = null;
            _activeUnknown = false;
        }
        var stamp = (Signature(mods), Interlocked.Read(ref _generation));
        var usageGeneration = Interlocked.Read(ref _usageGeneration);
        var readUsage   = usage && _usageReadFor != usageGeneration;
        var readActive  = activeIn is { } collection && _activeReadFor != (collection, usageGeneration);
        var readCopies  = duplicates && _duplicatesFor != stamp;
        var readChanges = changes && _changesFor != stamp;
        var readSizes   = sizes && _sizesFor != stamp;
        if (!readUsage && !readActive && !readCopies && !readChanges && !readSizes)
            return;

        var readFiles = readCopies || readChanges || readSizes;
        if (readFiles)
            CachePath();

        var cancel = new CancellationTokenSource();
        _cancel?.Dispose();
        _cancel = cancel;
        var list = mods.ToArray();
        // Reading one collection takes a moment: no progress line flickers for it at every settings change.
        _progress = readUsage || readFiles ? new CheckProgress("Starting", 0, 1) : null;
        var active = readActive ? (activeIn!.Value, usageGeneration) : ((Guid, long)?)null;
        _running = Task.Run(() => Scan(list, stamp, readUsage ? usageGeneration : null, active, readCopies, readChanges, readSizes, cancel.Token));
    }

    /// <summary>Stops the running scan; what it found so far is dropped. The caller turns its filter off, or the next <see cref="Need"/> starts again.</summary>
    public void Cancel() => _cancel?.Cancel();

    /// <summary>A mod was added, changed or removed in Penumbra: the next scan reads it again, and only it.</summary>
    public void Invalidate(string folderName)
    {
        lock (_gate)
        {
            _entries.Remove(folderName);
            _dropped.Add(folderName);
            _dirty = true;
        }
        Interlocked.Increment(ref _generation);
    }

    /// <summary>Forgets everything read from the mods' files, here and in the cache folder: the next scans read every mod again.</summary>
    public void ForgetAll()
    {
        var path = CachePath();
        lock (_fileGate)
        {
            lock (_gate)
            {
                _entries.Clear();
                _dropped.Clear();
                _loaded = true;   // what the file holds is not loaded back
                _dirty = false;
                _forgotten++;     // a save that took its snapshot before this doesn't write it
            }
            if (path != null)
                try { File.Delete(path); } catch (Exception) { /* overwritten by the next save */ }
        }
        Interlocked.Increment(ref _generation);
    }

    /// <summary>A mod's settings changed: the next scan reads the collections again. The old result stays until then.</summary>
    public void InvalidateUsage() => Interlocked.Increment(ref _usageGeneration);

    /// <summary>Writes what changed since the last save to the cache folder (the plugin is closing). Waits briefly for a cancelled scan.</summary>
    public void Flush()
    {
        try { _running.Wait(TimeSpan.FromSeconds(2)); }
        catch (Exception) { /* a failed scan saves nothing more */ }
        if (!_running.IsCompleted)
            return;
        lock (_gate)
            if (!_dirty)
                return;
        if (CachePath() != null)
            Save();
    }

    private void Scan(InstalledMod[] mods, (long, long) stamp, long? readUsage, (Guid Collection, long Generation)? readActive,
        bool readCopies, bool readChanges, bool readSizes, CancellationToken token)
    {
        Error = null;
        var readFiles = readCopies || readChanges || readSizes;
        try
        {
            if (readUsage is { } generation)
            {
                _progress = new CheckProgress("Reading collections", 0, 1);
                if (_collections() is { } collections)
                    _usage = ModUsageScan.From(collections, _enabledByMod);
                // Read once, also when Penumbra didn't answer, so a missing Penumbra isn't asked every frame.
                _usageReadFor = generation;
                Interlocked.Increment(ref _revision);
            }
            if (readActive is { } active)
            {
                IReadOnlyDictionary<string, bool>? settings;
                try { settings = _enabledByMod(active.Collection); }
                catch (Exception ex) when (ex is not OperationCanceledException) { settings = null; }
                // No answer drops the old set too: it may be another collection's, or out of date.
                _active = settings?.Where(s => s.Value).Select(s => s.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                _activeUnknown = settings is null;
                _activeReadFor = active;
                Interlocked.Increment(ref _revision);
            }
            if (readFiles)
                LoadOnce();
            // Each mod's files are stamped once per scan, however many of the reads below need it.
            var stamps = new Dictionary<string, (long Content, long Size)>(StringComparer.OrdinalIgnoreCase);
            if (readCopies)
            {
                var byPath = mods.GroupBy(m => m.FullPath, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                var progress = new ProgressSink(this);
                var sets = DuplicateModScan.Find(mods, path => Print(byPath[path], stamps, token), progress, token);
                var others = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var set in sets)
                    foreach (var folder in set)
                        others[folder] = set.Where(f => !string.Equals(f, folder, StringComparison.OrdinalIgnoreCase)).ToArray();
                _duplicatesOf = others;
                _duplicatesFor = stamp;
                Interlocked.Increment(ref _revision);
            }
            if (readChanges)
            {
                var changes = new Dictionary<string, IReadOnlyList<ChangeTag>>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < mods.Length; i++)
                {
                    token.ThrowIfCancellationRequested();
                    _progress = new CheckProgress("Reading what mods change", i + 1, mods.Length);
                    changes[mods[i].FolderName] = Tags(mods[i], stamps);
                }
                _changes = changes;
                _changesFor = stamp;
                Interlocked.Increment(ref _revision);
            }
            if (readSizes)
            {
                var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < mods.Length; i++)
                {
                    token.ThrowIfCancellationRequested();
                    _progress = new CheckProgress("Measuring mods", i + 1, mods.Length);
                    sizes[mods[i].FolderName] = Entry(mods[i], stamps).Size;
                }
                _sizes = sizes;
                _sizesFor = stamp;
                Interlocked.Increment(ref _revision);
            }
            if (readFiles)
            {
                Prune(mods);
                Save();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Not asked for again every frame: the next change to a mod (or the collections) starts it again.
            Error = $"The library couldn't be checked: {ex.Message}";
            if (readCopies) _duplicatesFor = stamp;
            if (readChanges) _changesFor = stamp;
            if (readSizes) _sizesFor = stamp;
            if (readUsage is { } failed) _usageReadFor = failed;
            if (readActive is { } failedActive) _activeReadFor = failedActive;
            Interlocked.Increment(ref _revision);
        }
        finally
        {
            _progress = null;
        }
    }

    /// <summary>The mod's entry when its stamps still match, else a new empty one in its place. The files are stamped once per scan (<paramref name="stamps"/>).</summary>
    private ModScanEntry Entry(InstalledMod mod, Dictionary<string, (long Content, long Size)> stamps)
    {
        if (!stamps.TryGetValue(mod.FolderName, out var stamp))
            stamps[mod.FolderName] = stamp = Stamp(mod.FullPath);
        lock (_gate)
        {
            if (_entries.TryGetValue(mod.FolderName, out var entry) && entry.LastWrite == mod.LastWrite
                && entry.Content == stamp.Content && entry.Size == stamp.Size)
                return entry;
            entry = new ModScanEntry { LastWrite = mod.LastWrite, Content = stamp.Content, Size = stamp.Size };
            _entries[mod.FolderName] = entry;
            _dirty = true;
            return entry;
        }
    }

    /// <summary>
    /// A mod folder's files, cheaply: their total size, and one number from every file's path, size and
    /// write time (no file is opened). A file rewritten in a subfolder changes it, which the folder's own
    /// write time doesn't. What can't be listed counts as nothing.
    /// </summary>
    internal static (long Content, long Size) Stamp(string folder)
    {
        long size = 0, content = 0, count = 0;
        try
        {
            var root = new DirectoryInfo(folder);
            var skip = root.FullName.Length;
            foreach (var file in root.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                var length = file.Length;
                size += length;
                count++;
                // Added up, so the order the files are listed in doesn't matter.
                content = unchecked(content + Mix(file.FullName.AsSpan(Math.Min(skip, file.FullName.Length)), length, file.LastWriteTimeUtc.Ticks));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }
        return (unchecked(content * 31 + count), size);
    }

    /// <summary>One file's path (ignoring case), size and write time as a number that is the same in every session.</summary>
    private static long Mix(ReadOnlySpan<char> path, long length, long ticks)
    {
        unchecked
        {
            var hash = 14695981039346656037UL;
            foreach (var c in path)
                hash = (hash ^ char.ToLowerInvariant(c)) * 1099511628211UL;
            hash = (hash ^ (ulong)length) * 0x9E3779B97F4A7C15UL;
            hash = (hash ^ (ulong)ticks) * 0xBF58476D1CE4E5B9UL;
            return (long)(hash ^ (hash >> 31));
        }
    }

    private ModFingerprint? Print(InstalledMod mod, Dictionary<string, (long Content, long Size)> stamps, CancellationToken token)
    {
        var entry = Entry(mod, stamps);
        lock (_gate)
            if (entry.PrintRead)
                return entry.Print;
        ModFingerprint? print;
        try
        {
            print = _fingerprint(mod.FullPath, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            print = null;   // a mod that can't be read is no duplicate
        }
        lock (_gate)
        {
            entry.Print = print;
            entry.PrintRead = true;
            _dirty = true;
        }
        return print;
    }

    private IReadOnlyList<ChangeTag> Tags(InstalledMod mod, Dictionary<string, (long Content, long Size)> stamps)
    {
        var entry = Entry(mod, stamps);
        lock (_gate)
            if (entry.Tags is { } cached)
                return cached;

        IEnumerable<string> paths;
        try
        {
            paths = ChangesIndex.GamePathsOf(PenumbraModMeta.Load(mod.FullPath)).ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            paths = Array.Empty<string>();
        }
        IReadOnlyList<(string, ChangeKind)>? items;
        try
        {
            items = _changedItems(mod.FolderName);
        }
        catch (Exception)
        {
            items = null;
        }
        // Penumbra went away or said nothing: the paths still tell faces, hair, idles and gear, but
        // these tags lack its items, so they aren't kept and the mod is read again next time.
        var tags = ChangesIndex.Classify(paths, items ?? Array.Empty<(string, ChangeKind)>());
        if (items != null)
        {
            lock (_gate)
            {
                entry.Tags = tags;
                _dirty = true;
            }
        }
        return tags;
    }

    /// <summary>Brings back what an earlier session saved, once, for mods this session hasn't read yet.</summary>
    private void LoadOnce()
    {
        string? path;
        lock (_gate)
        {
            if (_loaded || _cachePath == null)
                return;
            path = _cachePath;
        }
        var saved = LibraryScanCache.Load(path);
        lock (_gate)
        {
            _loaded = true;
            foreach (var (folder, entry) in saved)
                if (!_dropped.Contains(folder))
                    _entries.TryAdd(folder, entry);
        }
    }

    /// <summary>Writes the entries to the cache file when anything changed since the last save.</summary>
    private void Save()
    {
        lock (_gate)
            if (_cachePath == null || !_dirty)
                return;
        // A session that never loaded the file still owes it the mods it dropped, and keeps the rest.
        LoadOnce();
        KeyValuePair<string, ModScanEntry>[] snapshot;
        string path;
        long forgotten;
        lock (_gate)
        {
            path = _cachePath!;
            snapshot = _entries.ToArray();
            forgotten = _forgotten;
            _dirty = false;
            _dropped.Clear();
        }
        SavingForRegression?.Invoke();
        // A ForgetAll after the snapshot wins: the file it deleted isn't put back.
        LibraryScanCache.Save(path, snapshot, _fileGate, () => { lock (_gate) return _forgotten == forgotten; });
    }

    /// <summary>The cache file, asked of the host the first time it is needed (on the calling thread); null while it can't be had.</summary>
    private string? CachePath()
    {
        if (_cachePath == null && _cacheFile != null)
        {
            try { _cachePath = _cacheFile(); }
            catch (Exception) { /* kept in memory only; asked again next time */ }
        }
        return _cachePath;
    }

    /// <summary>Forgets mods no longer installed.</summary>
    private void Prune(InstalledMod[] mods)
    {
        var present = new HashSet<string>(mods.Select(m => m.FolderName), StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            foreach (var gone in _entries.Keys.Where(k => !present.Contains(k)).ToArray())
            {
                _entries.Remove(gone);
                _dirty = true;
            }
        }
    }

    /// <summary>The mods and their last-write times as one number; the same list object is not hashed twice in a row.</summary>
    private long Signature(IReadOnlyList<InstalledMod> mods)
    {
        if (ReferenceEquals(mods, _lastList))
            return _lastSignature;
        var hash = new HashCode();
        hash.Add(mods.Count);
        foreach (var mod in mods)
        {
            hash.Add(mod.FolderName, StringComparer.OrdinalIgnoreCase);
            hash.Add(mod.LastWrite);
        }
        _lastList = mods;
        _lastSignature = hash.ToHashCode();
        return _lastSignature;
    }

    private sealed class ProgressSink(LibraryScans owner) : IProgress<CheckProgress>
    {
        public void Report(CheckProgress value) => owner._progress = value;
    }
}
