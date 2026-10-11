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

/// <summary>
/// One mod in Penumbra's mod directory, as the library lists it. <see cref="Added"/> is when Penumbra
/// imported it; <see cref="Folder"/> is where Penumbra files it in its own list ("Gear/Shoes", empty
/// at the top), not where it lives on disk. <see cref="ImagePath"/> is the full path of its preview
/// image, empty when it has none (see <see cref="ModLibrary.TileImage"/>).
/// </summary>
internal sealed record InstalledMod(
    string FolderName,
    string FullPath,
    string Name,
    string Author,
    IReadOnlyList<string> Tags,
    string ImagePath,
    DateTime LastWrite,
    bool HasProject,
    DateTime Added = default,
    string Folder = "");

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
    private readonly Dictionary<string, string> _changedItems = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Packages handed to Penumbra to import, waiting for the mod each one becomes.</summary>
    private readonly List<PendingImport> _imports = new();

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
        Scans = new LibraryScans(() => ipc.GetCollections(), ipc.GetAllModSettings, ChangedItemKinds,
            cacheFile: () => Path.Combine(_host.CacheRoot, LibraryScanCache.FileName));
        _onSetting = _ => Scans.InvalidateUsage();
        ipc.ModSettingChanged += _onSetting;
    }

    /// <summary>The library filters' data: mods enabled nowhere or in the player's collection, duplicates, what each mod changes, sizes.</summary>
    public LibraryScans Scans { get; }

    private static readonly TimeSpan CollectionEvery = TimeSpan.FromSeconds(2);
    private DateTime _nextCollection;
    private (Guid Id, string Name)? _playerCollection;

    /// <summary>The collection the player's character is drawn with, asked of Penumbra every couple of seconds. Framework thread.</summary>
    public (Guid Id, string Name)? PlayerCollection()
    {
        var now = DateTime.UtcNow;
        if (now >= _nextCollection)
        {
            _nextCollection = now + CollectionEvery;
            _playerCollection = _host.PenumbraIpc.PlayerCollection();
        }
        return _playerCollection;
    }

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
        Scans.Flush();
    }

    /// <summary>Waiting for Penumbra to finish importing a package, which then opens.</summary>
    public bool IsInstalling
    {
        get
        {
            var now = DateTime.UtcNow;
            _imports.RemoveAll(i => now - i.Requested >= InstallWait);
            return _imports.Count > 0;
        }
    }

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

        // Penumbra only queued it: the import may still fail, and other mods can be added meanwhile.
        // So the mod that opens is the one that matches this package, not just the next one added.
        _imports.Add(new PendingImport(Path.GetFileNameWithoutExtension(packagePath), PackageModName(packagePath), DateTime.UtcNow));
        return true;
    }

    /// <summary>A package handed to Penumbra: its file name, the mod name it carries (when it could be read), and when.</summary>
    internal sealed record PendingImport(string FileStem, string? ModName, DateTime Requested);

    /// <summary>
    /// The mod name inside a package (a .pmp's meta.json, a .ttmp2's TTMPL.mpl), which Penumbra names
    /// the imported mod and its folder after. Null when the package has none or can't be read.
    /// </summary>
    internal static string? PackageModName(string packagePath)
    {
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(packagePath);
            foreach (var entryName in new[] { PenumbraModMeta.FileName, "TTMPL.mpl" })
            {
                var entry = zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, entryName, StringComparison.OrdinalIgnoreCase));
                if (entry == null || entry.Length > 4 * 1024 * 1024)
                    continue;
                using var reader = new JsonTextReader(new StreamReader(entry.Open())) { DateParseHandling = DateParseHandling.None };
                if (!reader.Read() || reader.TokenType != JsonToken.StartObject)
                    continue;
                while (reader.Read() && reader.TokenType == JsonToken.PropertyName)
                {
                    var key = (string?)reader.Value;
                    if (!reader.Read())
                        break;
                    if (key == "Name" && reader.TokenType == JsonToken.String && reader.Value is string name && !string.IsNullOrWhiteSpace(name))
                        return name;
                    reader.Skip();
                }
            }
        }
        catch (Exception)
        {
            // Not a zip (an old .ttmp, say), or damaged: Penumbra says so itself; the file name is matched instead.
        }
        return null;
    }

    /// <summary>
    /// Whether a mod Penumbra just added (<paramref name="folderName"/>, named <paramref name="modName"/>
    /// in its meta.json) is the one <paramref name="import"/> asked for. Names are compared on their
    /// letters and digits only, as Penumbra drops characters a folder can't have and numbers repeats ("Name (2)").
    /// </summary>
    internal static bool MatchesImport(PendingImport import, string folderName, string? modName)
    {
        var folder = Letters(folderName);
        var name   = Letters(modName);
        foreach (var expected in new[] { Letters(import.ModName), Letters(import.FileStem) })
        {
            if (expected.Length == 0)
                continue;
            if (expected == name || expected == folder)
                return true;
            // A folder Penumbra numbered because the name was taken: "Name (2)".
            if (folder.StartsWith(expected, StringComparison.Ordinal) && folder.Length > expected.Length
                && folder.AsSpan(expected.Length).IndexOfAnyExceptInRange('0', '9') < 0)
                return true;
        }
        return false;

        static string Letters(string? text)
            => text == null ? string.Empty : new string(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
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

        var root = _host.PenumbraIpc.GetModDirectory();
        if (string.IsNullOrEmpty(root))
            return;
        var folder = Path.Combine(root, folderName);
        var name = Peek(root, folderName, folderName, default).Name;
        // A mod added by anything else meanwhile (a copy, a build, an export) is not opened.
        var import = _imports.FirstOrDefault(i => MatchesImport(i, folderName, name));
        if (import == null)
            return;
        _imports.Remove(import);
        _host.OpenMod(folder, out _);
    }

    private void OnModDeleted(string folderName)
    {
        _listingStale = true;
        Changed(folderName);
        _host.OnModDeleted(_host.PenumbraIpc.GetModDirectory(), folderName);
    }

    private void OnModMoved(string from, string to)
    {
        _listingStale = true;
        Changed(from);
        Changed(to);
        _host.OnModMoved(_host.PenumbraIpc.GetModDirectory(), from, to);
    }

    /// <summary>
    /// Whether <paramref name="folderPath"/> is the mod <paramref name="folderName"/> of Penumbra's mod
    /// directory <paramref name="modRoot"/>. Penumbra's events name a mod by its folder name only, and a
    /// folder of the same name opened from elsewhere (a backup, an extracted pack) is not that mod.
    /// </summary>
    internal static bool IsModFolder(string folderPath, string? modRoot, string folderName)
    {
        if (string.IsNullOrWhiteSpace(modRoot) || string.IsNullOrWhiteSpace(folderName) || string.IsNullOrWhiteSpace(folderPath))
            return false;
        try
        {
            static string Full(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(Full(folderPath), Full(Path.Combine(modRoot, folderName)), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
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
        _changedItems.Clear();
        lock (_kindsGate)
            _kinds = null;
        var snapshot = mods.ToList();
        var facts = ListFacts(snapshot.Select(kv => kv.Key));
        _listing = Task.Run(() => snapshot
            .Select(kv => Peek(root, kv.Key, kv.Value, facts.TryGetValue(kv.Key, out var fact) ? fact : default))
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList());
    }

    /// <summary>
    /// Each mod's import date and Penumbra folder, asked of Penumbra on this (the framework) thread.
    /// When Penumbra's mod list can't be read, the folders are asked mod by mod and the dates stay
    /// unknown (the listing then uses the folder's creation time).
    /// </summary>
    private IReadOnlyDictionary<string, (DateTime Added, string Folder)> ListFacts(IEnumerable<string> folderNames)
    {
        var ipc = _host.PenumbraIpc;
        if (ipc.ModListFacts() is { } facts)
            return facts;
        var folders = new Dictionary<string, (DateTime, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (var folderName in folderNames)
        {
            // Penumbra files a mod under its own name when it has no folder.
            var path = ipc.GetModSortPath(folderName);
            int slash = path.LastIndexOf('/');
            folders[folderName] = (default, slash > 0 ? path[..slash] : string.Empty);
        }
        return folders;
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

    /// <summary>
    /// The library's view of one mod: the descriptive fields at the top of its meta.json, read as a
    /// stream and abandoned at the file lists (which Penumbra writes last), so thousands of mods list
    /// in moments. <paramref name="facts"/> is what Penumbra said of it.
    /// </summary>
    private static InstalledMod Peek(string root, string folderName, string penumbraName, (DateTime Added, string? Folder) facts)
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
        var added = facts.Added;
        if (added == default)
        {
            try { added = Directory.GetCreationTimeUtc(full); }
            catch (Exception) { /* leave unknown */ }
        }

        return new InstalledMod(folderName, full, string.IsNullOrWhiteSpace(name) ? folderName : name, author, tags,
            TileImage(full, image), lastWrite, StudioProjectStore.Exists(full), added, facts.Folder ?? string.Empty);
    }

    /// <summary>
    /// The file a meta.json "Image" value points at, checked once here on the listing worker rather
    /// than by the tiles every frame. Only a file inside the mod folder counts: an absolute or network
    /// path in a downloaded mod's meta.json would otherwise be looked up from the render thread.
    /// Empty when there is no such file.
    /// </summary>
    internal static string TileImage(string modFolder, string image)
    {
        if (!PathNormalizer.IsSafeRel(image) || image.Contains(':'))
            return string.Empty;
        try
        {
            var path = PathNormalizer.ToDiskPath(modFolder, image);
            return File.Exists(path) ? path : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
