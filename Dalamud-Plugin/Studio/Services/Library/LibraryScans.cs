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
/// The library filters' data, scanned on a worker the first time a filter needs it: which mods are
/// enabled anywhere, which are installed twice, and what each one changes. Results are kept in
/// memory per mod folder and last-write time, so a later scan reads only mods that are new or
/// changed (or were <see cref="Invalidate"/>d). Nothing is saved to disk. Dalamud-free; the
/// Penumbra calls come in as delegates and run on the worker.
/// </summary>
internal sealed class LibraryScans
{
    private readonly Func<IReadOnlyDictionary<Guid, string>?> _collections;
    private readonly Func<Guid, IReadOnlyDictionary<string, bool>?> _enabledByMod;
    private readonly Func<string, IReadOnlyList<(string, ChangeKind)>> _changedItems;
    private readonly Func<string, CancellationToken, ModFingerprint?> _fingerprint;

    private readonly object _gate = new();
    private readonly Dictionary<string, (DateTime LastWrite, ModFingerprint? Print)> _prints = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (DateTime LastWrite, IReadOnlyList<ChangeTag> Tags)> _tags = new(StringComparer.OrdinalIgnoreCase);

    private Task _running = Task.CompletedTask;
    private CancellationTokenSource? _cancel;
    private long _generation;
    private long _usageGeneration;
    private long? _usageReadFor;
    private (long Signature, long Generation)? _duplicatesFor;
    private (long Signature, long Generation)? _changesFor;
    private IReadOnlyList<InstalledMod>? _lastList;
    private long _lastSignature;

    private volatile ModUsage? _usage;
    private volatile IReadOnlyDictionary<string, IReadOnlyList<string>>? _duplicatesOf;
    private volatile IReadOnlyDictionary<string, IReadOnlyList<ChangeTag>>? _changes;
    private volatile CheckProgress? _progress;
    private int _revision;

    /// <param name="collections">Penumbra's collections by id; null when Penumbra can't be asked.</param>
    /// <param name="enabledByMod">A collection's mods by folder name and whether each is enabled, inheritance on, temporary settings off.</param>
    /// <param name="changedItems">Penumbra's changed items of a mod folder.</param>
    /// <param name="fingerprint">Reads a mod for the duplicate check (tests count the calls); <see cref="DuplicateModScan.Fingerprint(string, CancellationToken)"/> when null.</param>
    public LibraryScans(Func<IReadOnlyDictionary<Guid, string>?> collections, Func<Guid, IReadOnlyDictionary<string, bool>?> enabledByMod,
        Func<string, IReadOnlyList<(string, ChangeKind)>> changedItems, Func<string, ModFingerprint?>? fingerprint = null)
    {
        _collections  = collections;
        _enabledByMod = enabledByMod;
        _changedItems = changedItems;
        _fingerprint  = fingerprint is null ? DuplicateModScan.Fingerprint : (folder, _) => fingerprint(folder);
    }

    /// <summary>The running scan's step, or null when none runs.</summary>
    public CheckProgress? Progress => _progress;

    /// <summary>Folder names of the mods enabled in any collection; null until read.</summary>
    public ModUsage? Usage => _usage;

    /// <summary>For each mod installed more than once, the folder names of its other copies; null until scanned.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? DuplicatesOf => _duplicatesOf;

    /// <summary>What each mod changes, by folder name; null until scanned.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ChangeTag>>? Changes => _changes;

    /// <summary>Goes up each time a scan publishes results, so a view knows to filter again.</summary>
    public int Revision => Volatile.Read(ref _revision);

    /// <summary>Why the last scan stopped early, or null. What it was asked for isn't scanned again until a mod changes.</summary>
    public string? Error { get; private set; }

    /// <summary>The running scan, or a finished task (tests wait on it).</summary>
    internal Task Idle => _running;

    /// <summary>
    /// Starts a scan of what is asked for and missing or out of date for this list of mods; does
    /// nothing while a scan runs (the next call after it picks up what changed meanwhile). Called
    /// every frame a filter is on, from the thread that draws the library.
    /// </summary>
    public void Need(bool usage, bool duplicates, bool changes, IReadOnlyList<InstalledMod> mods)
    {
        if (!_running.IsCompleted)
            return;
        var stamp = (Signature(mods), Interlocked.Read(ref _generation));
        var usageGeneration = Interlocked.Read(ref _usageGeneration);
        var readUsage   = usage && _usageReadFor != usageGeneration;
        var readCopies  = duplicates && _duplicatesFor != stamp;
        var readChanges = changes && _changesFor != stamp;
        if (!readUsage && !readCopies && !readChanges)
            return;

        var cancel = new CancellationTokenSource();
        _cancel?.Dispose();
        _cancel = cancel;
        var list = mods.ToArray();
        _progress = new CheckProgress("Starting", 0, 1);
        _running = Task.Run(() => Scan(list, stamp, readUsage ? usageGeneration : null, readCopies, readChanges, cancel.Token));
    }

    /// <summary>Stops the running scan; what it found so far is dropped. The caller turns its filter off, or the next <see cref="Need"/> starts again.</summary>
    public void Cancel() => _cancel?.Cancel();

    /// <summary>A mod was added, changed or removed in Penumbra: the next scan reads it again, and only it.</summary>
    public void Invalidate(string folderName)
    {
        lock (_gate)
        {
            _prints.Remove(folderName);
            _tags.Remove(folderName);
        }
        Interlocked.Increment(ref _generation);
    }

    /// <summary>A mod's settings changed: the next scan reads the collections again. The old result stays until then.</summary>
    public void InvalidateUsage() => Interlocked.Increment(ref _usageGeneration);

    private void Scan(InstalledMod[] mods, (long, long) stamp, long? readUsage, bool readCopies, bool readChanges, CancellationToken token)
    {
        Error = null;
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
            if (readCopies)
            {
                var byPath = mods.GroupBy(m => m.FullPath, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                var progress = new ProgressSink(this);
                var sets = DuplicateModScan.Find(mods, path => Print(byPath[path], token), progress, token);
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
                    changes[mods[i].FolderName] = Tags(mods[i]);
                }
                _changes = changes;
                _changesFor = stamp;
                Interlocked.Increment(ref _revision);
            }
            Prune(mods);
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
            if (readUsage is { } failed) _usageReadFor = failed;
            Interlocked.Increment(ref _revision);
        }
        finally
        {
            _progress = null;
        }
    }

    private ModFingerprint? Print(InstalledMod mod, CancellationToken token)
    {
        lock (_gate)
            if (_prints.TryGetValue(mod.FolderName, out var cached) && cached.LastWrite == mod.LastWrite)
                return cached.Print;
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
            _prints[mod.FolderName] = (mod.LastWrite, print);
        return print;
    }

    private IReadOnlyList<ChangeTag> Tags(InstalledMod mod)
    {
        lock (_gate)
            if (_tags.TryGetValue(mod.FolderName, out var cached) && cached.LastWrite == mod.LastWrite)
                return cached.Tags;

        IEnumerable<string> paths;
        try
        {
            paths = ChangesIndex.GamePathsOf(PenumbraModMeta.Load(mod.FullPath)).ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            paths = Array.Empty<string>();
        }
        IReadOnlyList<(string, ChangeKind)> items;
        try
        {
            items = _changedItems(mod.FolderName);
        }
        catch (Exception)
        {
            // Penumbra went away or doesn't know the mod yet: the paths still tell faces, idles and gear.
            items = Array.Empty<(string, ChangeKind)>();
        }
        var tags = ChangesIndex.Classify(paths, items);
        lock (_gate)
            _tags[mod.FolderName] = (mod.LastWrite, tags);
        return tags;
    }

    /// <summary>Forgets mods no longer installed.</summary>
    private void Prune(InstalledMod[] mods)
    {
        var present = new HashSet<string>(mods.Select(m => m.FolderName), StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            foreach (var gone in _prints.Keys.Where(k => !present.Contains(k)).ToArray())
                _prints.Remove(gone);
            foreach (var gone in _tags.Keys.Where(k => !present.Contains(k)).ToArray())
                _tags.Remove(gone);
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
