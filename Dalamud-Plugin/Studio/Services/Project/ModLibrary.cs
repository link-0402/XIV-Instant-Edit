using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using InstantEdit.Studio.Models.Project;
using InstantEdit.Studio.Services.Library;

namespace InstantEdit.Studio.Services.Project;

/// <summary>One mod in Penumbra's mod directory, as the library lists it.</summary>
internal sealed record InstalledMod(
    string FolderName,
    string FullPath,
    string Name,
    string Author,
    IReadOnlyList<string> Tags,
    string ImagePath,
    DateTime LastWrite,
    bool HasProject);

/// <summary>
/// Penumbra's installed mods for the Mod Browser's library, importing a package, and Penumbra's
/// mod events: a mod added by an import opens in a window of its own, and a mod renamed or deleted
/// in Penumbra moves or closes its window (see <see cref="StudioHost"/>).
///
/// Penumbra raises its mod events from whatever thread it is on; they are queued and handled in
/// <see cref="Update"/>, on the render thread, like everything else that touches an open mod.
/// </summary>
internal sealed class ModLibrary : IDisposable
{
    private static readonly TimeSpan InstallWait = TimeSpan.FromMinutes(5);

    private readonly StudioHost _host;
    private readonly ConcurrentQueue<Action> _events = new();

    private readonly Action<string>         _onAdded;
    private readonly Action<string>         _onDeleted;
    private readonly Action<string, string> _onMoved;
    private readonly Action<string, bool>   _onDirectory;
    private readonly Action<string>         _onSetting;

    // Penumbra's changed items of every mod, asked for once and kept until a mod changes or the library is listed again.
    private readonly object _kindsGate = new();
    private IReadOnlyDictionary<string, IReadOnlyList<(string Name, ChangeKind Kind)>>? _kinds;

    private Task<List<InstalledMod>>? _listing;
    private bool _listingStale = true;
    private readonly Dictionary<string, string> _sortPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _changedItems = new(StringComparer.OrdinalIgnoreCase);

    private DateTime? _installRequested;

    public ModLibrary(StudioHost host)
    {
        _host = host;
        var ipc = host.PenumbraIpc;
        _onAdded     = dir        => _events.Enqueue(() => OnModAdded(dir));
        _onDeleted   = dir        => _events.Enqueue(() => OnModDeleted(dir));
        _onMoved     = (from, to) => _events.Enqueue(() => OnModMoved(from, to));
        _onDirectory = (_, _)     => _events.Enqueue(() => _listingStale = true);
        ipc.ModAdded            += _onAdded;
        ipc.ModDeleted          += _onDeleted;
        ipc.ModMoved            += _onMoved;
        ipc.ModDirectoryChanged += _onDirectory;
        // Settings change often (every slider): the scans only count them, so this runs on Penumbra's thread.
        Scans = new LibraryScans(() => ipc.GetCollections(), ipc.GetAllModSettings, ChangedItemKinds);
        _onSetting = _ => Scans.InvalidateUsage();
        ipc.ModSettingChanged += _onSetting;
    }

    /// <summary>The library filters' data: mods enabled nowhere, duplicates, what each mod changes.</summary>
    public LibraryScans Scans { get; }

    private IReadOnlyList<(string, ChangeKind)> ChangedItemKinds(string folderName)
    {
        IReadOnlyDictionary<string, IReadOnlyList<(string Name, ChangeKind Kind)>>? kinds;
        lock (_kindsGate)
        {
            _kinds ??= _host.PenumbraIpc.ChangedItemKinds();
            kinds = _kinds;
        }
        return kinds is not null && kinds.TryGetValue(folderName, out var items) ? items : Array.Empty<(string, ChangeKind)>();
    }

    public void Dispose()
    {
        var ipc = _host.PenumbraIpc;
        ipc.ModAdded            -= _onAdded;
        ipc.ModDeleted          -= _onDeleted;
        ipc.ModMoved            -= _onMoved;
        ipc.ModDirectoryChanged -= _onDirectory;
        ipc.ModSettingChanged   -= _onSetting;
        Scans.Cancel();
    }

    /// <summary>Waiting for Penumbra to finish importing a package, which then opens.</summary>
    public bool IsInstalling => _installRequested is { } at && DateTime.UtcNow - at < InstallWait;

    /// <summary>The listing is out of date: a mod was added (a copy, say) or Penumbra's folder changed.</summary>
    public void MarkStale() => _listingStale = true;

    /// <summary>Handles Penumbra's queued events and finishes the library listing.</summary>
    public void Update()
    {
        while (_events.TryDequeue(out var handle))
            handle();

        if (_listing is { IsCompleted: true } done)
        {
            if (done.IsCompletedSuccessfully)
                Installed = done.Result;
            else
                StudioServices.Log.Warning(done.Exception, "[Studio] Listing Penumbra's mods failed");
            _listing = null;
        }
    }

    /// <summary>Hands a mod package to Penumbra to import; the mod opens in a window once Penumbra has added it.</summary>
    public bool TryInstall(string packagePath, out string error)
    {
        error = string.Empty;
        if (!File.Exists(packagePath))
        {
            error = "That file does not exist.";
            return false;
        }

        var result = _host.PenumbraIpc.InstallMod(packagePath);
        if (result != PenumbraApiEc.Success)
        {
            error = $"Penumbra would not import it ({result}).";
            return false;
        }

        _installRequested = DateTime.UtcNow;
        return true;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Penumbra's events
    // ─────────────────────────────────────────────────────────────────────────

    private void OnModAdded(string folderName)
    {
        _listingStale = true;
        Changed(folderName);
        if (!IsInstalling)
            return;

        _installRequested = null;
        var root = _host.PenumbraIpc.GetModDirectory();
        if (!string.IsNullOrEmpty(root))
            _host.OpenMod(Path.Combine(root, folderName), out _);
    }

    private void OnModDeleted(string folderName)
    {
        _listingStale = true;
        Changed(folderName);
        _host.OnModDeleted(folderName);
    }

    private void OnModMoved(string from, string to)
    {
        _listingStale = true;
        Changed(from);
        Changed(to);
        _host.OnModMoved(from, to);
    }

    /// <summary>A mod was added, removed, renamed or written into: the filters read it again, and the collections.</summary>
    public void Changed(string folderName)
    {
        lock (_kindsGate)
            _kinds = null;
        _changedItems.Remove(folderName);
        Scans.Invalidate(folderName);
        Scans.InvalidateUsage();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // The library
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Penumbra's installed mods, or null until they have been listed (see <see cref="RefreshInstalled"/>).</summary>
    public IReadOnlyList<InstalledMod>? Installed { get; private set; }

    public bool IsListing => _listing != null;

    /// <summary>Whether the listing is out of date (a mod was added, removed or renamed since).</summary>
    public bool InstalledStale => _listingStale;

    /// <summary>Lists Penumbra's mods again: the names from Penumbra, the rest read from each meta.json on a worker.</summary>
    public void RefreshInstalled()
    {
        if (_listing != null || !_host.PenumbraIpc.IsAvailable)
            return;
        var root = _host.PenumbraIpc.GetModDirectory();
        var mods = _host.PenumbraIpc.GetModList();
        if (string.IsNullOrWhiteSpace(root) || mods == null)
            return;

        _listingStale = false;
        _sortPaths.Clear();
        _changedItems.Clear();
        lock (_kindsGate)
            _kinds = null;
        var snapshot = mods.ToList();
        _listing = Task.Run(() => snapshot
            .Select(kv => Peek(root, kv.Key, kv.Value))
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList());
    }

    /// <summary>
    /// The game items a mod changes, as Penumbra identifies them, one per line (the first few),
    /// asked for only when <paramref name="fetch"/> (the tile is hovered), and kept after that.
    /// </summary>
    public string? ChangedItems(InstalledMod mod, bool fetch)
    {
        if (_changedItems.TryGetValue(mod.FolderName, out var cached))
            return cached;
        if (!fetch || !_host.PenumbraIpc.IsAvailable)
            return null;

        var items = _host.PenumbraIpc.GetChangedItems(mod.FolderName)?.OrderBy(i => i, StringComparer.OrdinalIgnoreCase).ToList();
        var text = items == null || items.Count == 0
            ? string.Empty
            : string.Join("\n", items.Take(10)) + (items.Count > 10 ? $"\nand {items.Count - 10} more" : string.Empty);
        _changedItems[mod.FolderName] = text;
        return text;
    }

    /// <summary>Where Penumbra files a mod in its own list ("Gear/Shoes"), fetched once per mod as tiles come into view.</summary>
    public string SortPath(InstalledMod mod)
    {
        if (_sortPaths.TryGetValue(mod.FolderName, out var cached))
            return cached;
        var path = _host.PenumbraIpc.IsAvailable ? _host.PenumbraIpc.GetModSortPath(mod.FolderName) : string.Empty;
        // Penumbra files a mod under its own name when it has no folder; that adds nothing to show.
        int slash = path.LastIndexOf('/');
        cached = slash > 0 ? path[..slash] : string.Empty;
        _sortPaths[mod.FolderName] = cached;
        return cached;
    }

    /// <summary>
    /// The library's view of one mod: the descriptive fields at the top of its meta.json, read as a
    /// stream and abandoned at the file lists (which Penumbra writes last), so thousands of mods list
    /// in moments.
    /// </summary>
    private static InstalledMod Peek(string root, string folderName, string penumbraName)
    {
        var full = Path.Combine(root, folderName);
        string name = penumbraName, author = string.Empty, image = string.Empty;
        var tags = new List<string>();
        DateTime lastWrite = default;

        try
        {
            using var stream = new StreamReader(Path.Combine(full, PenumbraModMeta.FileName));
            using var reader = new JsonTextReader(stream) { DateParseHandling = DateParseHandling.None };
            if (reader.Read() && reader.TokenType == JsonToken.StartObject)
            {
                while (reader.Read() && reader.TokenType == JsonToken.PropertyName)
                {
                    var key = (string?)reader.Value ?? string.Empty;
                    if (key is "DefaultData" or "Groups")
                        break;
                    if (!reader.Read())
                        break;

                    switch (key)
                    {
                        case "Name" when reader.TokenType == JsonToken.String:
                            name = (string?)reader.Value ?? name;
                            break;
                        case "Author" when reader.TokenType == JsonToken.String:
                            author = (string?)reader.Value ?? string.Empty;
                            break;
                        case "Image" when reader.TokenType == JsonToken.String:
                            image = (string?)reader.Value ?? string.Empty;
                            break;
                        case "LastWrite" when reader.TokenType == JsonToken.String:
                            DateTime.TryParse((string?)reader.Value, System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.RoundtripKind, out lastWrite);
                            break;
                        case "ModTags" when reader.TokenType == JsonToken.StartArray:
                            while (reader.Read() && reader.TokenType != JsonToken.EndArray)
                                if (reader.TokenType == JsonToken.String && reader.Value is string tag && tag.Length > 0)
                                    tags.Add(tag);
                            break;
                        default:
                            reader.Skip();
                            break;
                    }
                }
            }
        }
        catch (Exception)
        {
            // A mod whose meta.json cannot be read still lists, under the name Penumbra knows it by.
        }

        if (lastWrite == default)
        {
            try { lastWrite = Directory.GetLastWriteTimeUtc(full); }
            catch (Exception) { /* leave unknown */ }
        }

        return new InstalledMod(folderName, full, string.IsNullOrWhiteSpace(name) ? folderName : name, author, tags,
            image, lastWrite, StudioProjectStore.Exists(full));
    }
}
