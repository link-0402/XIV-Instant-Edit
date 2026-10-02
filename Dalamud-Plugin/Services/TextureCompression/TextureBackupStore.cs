using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace InstantEdit.Services.TextureCompression;

/// <summary> A mod texture automatic optimization replaced, and where its original is kept. </summary>
internal sealed record CompressedTexture
{
    /// <summary> The mod file, as a full path. </summary>
    public required string File { get; init; }
    public required string ModDirectory { get; init; }
    public string ModName { get; init; } = string.Empty;
    public Guid? ModStableId { get; init; }
    /// <summary> The file's path inside its mod folder, with forward slashes. </summary>
    public required string RelativePath { get; init; }
    /// <summary> The copy of the original in the cache folder, named after its hash. </summary>
    public required string Backup { get; init; }
    public required string OriginalSha256 { get; init; }
    public long OriginalLength { get; init; }
    public uint OriginalFormat { get; init; }
    /// <summary> What was written over the file; restoring only replaces a file that still holds it. </summary>
    public required string CompressedSha256 { get; init; }
    public long CompressedLength { get; init; }
    public uint CompressedFormat { get; init; }
    /// <summary> The original's size. </summary>
    public int Width { get; init; }
    public int Height { get; init; }
    /// <summary> It held one color and was shrunk to <see cref="SingleColor.Size"/> square before it was compressed. </summary>
    public bool Shrunk { get; init; }
    public DateTimeOffset Compressed { get; init; }

    [JsonIgnore]
    public string Label => $"{ModName}: {RelativePath}";
}

/// <summary> A mod texture the check kept as it is, remembered by its content so it isn't encoded again. </summary>
internal sealed record KeptTexture
{
    public required string File { get; init; }
    public required string Sha256 { get; init; }
    public required string Reason { get; init; }
    /// <summary> The check that kept it; a stricter or looser check looks at the texture again. </summary>
    public int CheckVersion { get; init; }
}

/// <summary> One file of a hair refit, and where its original is kept. </summary>
internal sealed record RefitFile
{
    /// <summary> The mod file, as a full path. </summary>
    public required string File { get; init; }
    /// <summary> The file's path inside its mod folder, with forward slashes. </summary>
    public required string RelativePath { get; init; }
    public required string Backup { get; init; }
    public required string OriginalSha256 { get; init; }
    public long OriginalLength { get; init; }
    /// <summary> What was written over the file; restoring only replaces a file that still holds it. </summary>
    public required string NewSha256 { get; init; }
    public long NewLength { get; init; }
}

/// <summary> Hair whose models and textures were refit together, to be put back together. </summary>
internal sealed record RefitGroup
{
    public required string ModDirectory { get; init; }
    public string ModName { get; init; } = string.Empty;
    public Guid? ModStableId { get; init; }
    /// <summary> The part of the texture kept, for the log and the card. </summary>
    public string Window { get; init; } = string.Empty;
    public List<RefitFile> Files { get; init; } = [];
    public DateTimeOffset Refit { get; init; }

    [JsonIgnore]
    public long BytesSaved => Files.Sum(file => file.OriginalLength - file.NewLength);
}

internal sealed class TextureBackupJournal
{
    public int Version { get; set; } = 1;
    public List<CompressedTexture> Compressed { get; set; } = [];
    public List<KeptTexture> Kept { get; set; } = [];
    public List<RefitGroup> Refits { get; set; } = [];
    /// <summary> Files whose originals were restored, as full paths; optimization leaves them alone until Optimize now. </summary>
    public List<string> Restored { get; set; } = [];
}

/// <summary>
/// The originals of the textures automatic optimization replaced, and of the models and textures of
/// refit hair. Each original is copied into the cache folder's texture-backups folder under its
/// SHA-256, so identical files share one copy; the list of replaced files and of the textures the
/// check kept lives in the config folder, since it is what restoring needs. Backups are kept until
/// they are restored, or until the automatic cache cleanup finds them older than
/// <see cref="Retention"/>. Dalamud-free.
/// </summary>
internal sealed partial class TextureBackupStore
{
    public const string FolderName = "texture-backups";
    public const string JournalName = "CompressedTextures.json";
    /// <summary> How long the automatic cache cleanup keeps an original. </summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly Lock _lock = new();
    private readonly string _journalPath;
    private TextureBackupJournal _journal = new();
    private IReadOnlyList<CompressedTexture> _compressed = [];
    private IReadOnlyList<KeptTexture> _kept = [];
    private IReadOnlyList<RefitGroup> _refits = [];
    private IReadOnlySet<string> _restored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public TextureBackupStore(string configDirectory) => _journalPath = Path.Combine(configDirectory, JournalName);

    /// <summary> Why the saved list couldn't be read, or empty. The unreadable file is set aside, not overwritten. </summary>
    public string LoadError { get; private set; } = string.Empty;

    public IReadOnlyList<CompressedTexture> Compressed => Volatile.Read(ref _compressed);
    public IReadOnlyList<KeptTexture> Kept => Volatile.Read(ref _kept);
    public IReadOnlyList<RefitGroup> Refits => Volatile.Read(ref _refits);

    public void Load()
    {
        lock (_lock)
        {
            LoadError = string.Empty;
            _journal = new TextureBackupJournal();
            if (File.Exists(_journalPath))
            {
                try
                {
                    _journal = JsonSerializer.Deserialize<TextureBackupJournal>(File.ReadAllText(_journalPath), JsonOptions) ?? new TextureBackupJournal();
                    _journal.Compressed.RemoveAll(entry => entry is null);
                    _journal.Kept.RemoveAll(entry => entry is null);
                    _journal.Refits ??= [];
                    _journal.Refits.RemoveAll(group => group?.Files is not { Count: > 0 });
                    _journal.Restored ??= [];
                    _journal.Restored.RemoveAll(string.IsNullOrWhiteSpace);
                }
                catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    var aside = $"{_journalPath}.unreadable-{DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture)}";
                    try { File.Move(_journalPath, aside); }
                    catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException) { aside = _journalPath; }
                    LoadError = $"The list of compressed textures couldn't be read ({e.Message}); it was kept as {Path.GetFileName(aside)}. " +
                                "Their backups are still in the cache folder's texture-backups folder.";
                    _journal = new TextureBackupJournal();
                }
            }
            Publish();
        }
    }

    /// <summary>
    /// Copies an original into the cache folder's backup folder under its hash and returns the copy's
    /// path. A copy that is already there with the same content is reused.
    /// </summary>
    /// <param name="extension">".tex", or ".mdl" for a refit model.</param>
    public static string StoreBackup(string cacheRoot, byte[] original, string sha256, string extension = ".tex")
    {
        if (!Sha256Regex().IsMatch(sha256))
            throw new ArgumentException("The backup's hash is invalid.", nameof(sha256));
        if (extension is not (".tex" or ".mdl"))
            throw new ArgumentException("Only textures and models are backed up.", nameof(extension));
        var folder = Path.Combine(cacheRoot, FolderName);
        TextureFiles.EnsureLocalPath(folder);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, sha256.ToUpperInvariant() + extension);
        if (File.Exists(path) && new FileInfo(path).Length == original.LongLength && Hash(File.ReadAllBytes(path)) == sha256.ToUpperInvariant())
        {
            // Dated now, so the cleanup's sweep for copies no entry names doesn't take it before it is recorded.
            try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return path;
        }
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporary, original);
            if (Hash(File.ReadAllBytes(temporary)) != sha256.ToUpperInvariant())
                throw new IOException("The backup copy didn't read back as written.");
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
        return path;
    }

    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    /// <summary>
    /// Remembers a replaced file. An earlier entry for the same file is replaced, and its backup deleted
    /// when nothing else uses it; an entry whose original is what that earlier entry wrote is refused,
    /// since replacing it would delete the only copy of the real original.
    /// </summary>
    public void Record(CompressedTexture entry)
    {
        lock (_lock)
        {
            var replaced = _journal.Compressed.Where(existing => SameFile(existing.File, entry.File)).ToList();
            if (replaced.Any(existing => string.Equals(existing.CompressedSha256, entry.OriginalSha256, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"{entry.RelativePath} still holds what automatic optimization wrote; its original is kept as it was.");
            _journal.Compressed.RemoveAll(existing => SameFile(existing.File, entry.File));
            _journal.Kept.RemoveAll(kept => SameFile(kept.File, entry.File));
            _journal.Compressed.Add(entry);
            Save();
            DeleteUnreferenced(replaced.Select(existing => existing.Backup));
            Publish();
        }
    }

    /// <summary>
    /// Remembers refit hair. A file an earlier group also lists stays in that group when this one's
    /// original is what the earlier group wrote, as when two hair materials of one model are refit one
    /// after the other: restoring the newer group first, then the older, puts back the real originals.
    /// Otherwise the earlier group's entry for that file is replaced, the group dropped when no file is
    /// left, and the backups deleted when nothing else uses them.
    /// </summary>
    public void RecordRefit(RefitGroup group)
    {
        lock (_lock)
        {
            bool Replaced(RefitFile old) => group.Files.Any(file => SameFile(old.File, file.File) &&
                                                                    !string.Equals(old.NewSha256, file.OriginalSha256, StringComparison.OrdinalIgnoreCase));
            var dropped = new List<RefitFile>();
            for (var i = _journal.Refits.Count - 1; i >= 0; i--)
            {
                var existing = _journal.Refits[i];
                var stale = existing.Files.Where(Replaced).ToList();
                if (stale.Count == 0)
                    continue;
                dropped.AddRange(stale);
                if (stale.Count == existing.Files.Count)
                    _journal.Refits.RemoveAt(i);
                else
                    _journal.Refits[i] = existing with { Files = existing.Files.Except(stale).ToList() };
            }
            _journal.Refits.Add(group);
            Save();
            DeleteUnreferenced(dropped.Select(file => file.Backup));
            Publish();
        }
    }

    /// <summary> Drops refit groups (to keep the refit files) and deletes backups nothing else uses. </summary>
    public void ForgetRefits(IReadOnlyCollection<RefitGroup> groups)
    {
        if (groups.Count == 0)
            return;
        lock (_lock)
        {
            _journal.Refits.RemoveAll(existing => groups.Any(group => ReferenceEquals(group, existing)));
            Save();
            DeleteUnreferenced(groups.SelectMany(group => group.Files).Select(file => file.Backup));
            Publish();
        }
    }

    /// <summary> Replaces a refit group with the files of it still to restore; drops it when none are left. </summary>
    public void UpdateRefit(RefitGroup group, IReadOnlyCollection<RefitFile> remaining)
    {
        lock (_lock)
        {
            var index = _journal.Refits.FindIndex(existing => ReferenceEquals(existing, group));
            if (index < 0)
                return;
            if (remaining.Count == 0)
                _journal.Refits.RemoveAt(index);
            else
                _journal.Refits[index] = group with { Files = remaining.ToList() };
            Save();
            DeleteUnreferenced(group.Files.Except(remaining).Select(file => file.Backup));
            Publish();
        }
    }

    /// <summary> Remembers a texture the check kept, replacing an earlier verdict for the same file. </summary>
    public void RecordKept(KeptTexture entry)
    {
        lock (_lock)
        {
            _journal.Kept.RemoveAll(kept => SameFile(kept.File, entry.File));
            _journal.Kept.Add(entry);
            Save();
            Publish();
        }
    }

    /// <summary> The verdict for a file holding this content under this check, or null when it wasn't checked. </summary>
    public KeptTexture? KeptFor(string file, string sha256, int checkVersion)
        => Kept.FirstOrDefault(kept => SameFile(kept.File, file) && kept.CheckVersion == checkVersion &&
                                       string.Equals(kept.Sha256, sha256, StringComparison.OrdinalIgnoreCase));

    public CompressedTexture? CompressedFor(string file) => Compressed.FirstOrDefault(entry => SameFile(entry.File, file));

    /// <summary> Drops entries (after restoring them, or to keep the compressed files) and deletes backups nothing else uses. </summary>
    public void Forget(IReadOnlyCollection<CompressedTexture> entries)
    {
        if (entries.Count == 0)
            return;
        lock (_lock)
        {
            _journal.Compressed.RemoveAll(existing => entries.Any(entry => ReferenceEquals(entry, existing) || entry == existing));
            Save();
            DeleteUnreferenced(entries.Select(entry => entry.Backup));
            Publish();
        }
    }

    /// <summary> How many originals are kept and the bytes they take, each shared copy once. </summary>
    public (int Files, long Bytes) BackupTotals()
    {
        var distinct = Compressed.Select(entry => (entry.Backup, entry.OriginalLength))
            .Concat(Refits.SelectMany(group => group.Files).Select(file => (file.Backup, file.OriginalLength)))
            .GroupBy(entry => entry.Backup, StringComparer.OrdinalIgnoreCase).ToList();
        return (distinct.Count, distinct.Sum(group => group.First().OriginalLength));
    }

    /// <summary> Whether the file's original was restored, so optimization leaves it alone. </summary>
    public bool IsRestored(string file) => Volatile.Read(ref _restored).Contains(Full(file));

    /// <summary> Remembers files whose originals were restored. </summary>
    public void MarkRestored(IEnumerable<string> files)
    {
        lock (_lock)
        {
            var known = _journal.Restored.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var added = files.Select(Full).Where(known.Add).ToList();
            if (added.Count == 0)
                return;
            _journal.Restored.AddRange(added);
            Save();
            Publish();
        }
    }

    /// <summary> Lets optimization change these files again; returns how many were marked restored. </summary>
    public int ClearRestored(IEnumerable<string> files)
    {
        lock (_lock)
        {
            var cleared = files.Select(Full).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var removed = _journal.Restored.RemoveAll(cleared.Contains);
            if (removed == 0)
                return 0;
            Save();
            Publish();
            return removed;
        }
    }

    /// <summary>
    /// The compressed textures and refit groups to restore for these files (what a character has
    /// loaded). A refit group goes back whole when any of its files is among them, together with every
    /// group sharing a file with it and the compressed entries of all their files, since a texture cut
    /// by a refit may have been compressed after. Groups keep the store's order, oldest first.
    /// </summary>
    public static (IReadOnlyList<CompressedTexture> Compressed, IReadOnlyList<RefitGroup> Refits) Covering(
        IReadOnlyList<CompressedTexture> compressed, IReadOnlyList<RefitGroup> refits, IEnumerable<string> files)
    {
        var wanted = files.Select(Full).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var groups = new HashSet<RefitGroup>(ReferenceEqualityComparer.Instance);
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var group in refits)
            {
                if (groups.Contains(group) || !group.Files.Any(file => wanted.Contains(Full(file.File))))
                    continue;
                groups.Add(group);
                wanted.UnionWith(group.Files.Select(file => Full(file.File)));
                grew = true;
            }
        }
        return (compressed.Where(entry => wanted.Contains(Full(entry.File))).ToList(), refits.Where(groups.Contains).ToList());
    }

    /// <summary>
    /// The automatic cache cleanup: forgets textures optimized and hair refit before <paramref name="cutoff"/>
    /// and deletes their originals, then deletes copies in <paramref name="cacheRoot"/>'s backup folder that
    /// no entry names and that are older than the cutoff too, such as ones a failed delete left. That sweep is
    /// skipped when the list couldn't be read, since the set-aside list may still name them. Returns the
    /// originals forgotten and the stray copies deleted.
    /// </summary>
    public (int Forgotten, int Strays) Expire(string? cacheRoot, DateTimeOffset cutoff)
    {
        lock (_lock)
        {
            var entries = _journal.Compressed.Where(entry => entry.Compressed < cutoff).ToList();
            var groups = _journal.Refits.Where(group => group.Refit < cutoff).ToList();
            if (entries.Count > 0 || groups.Count > 0)
            {
                _journal.Compressed.RemoveAll(entries.Contains);
                _journal.Refits.RemoveAll(groups.Contains);
                Save();
                DeleteUnreferenced(entries.Select(entry => entry.Backup).Concat(groups.SelectMany(group => group.Files).Select(file => file.Backup)));
                Publish();
            }
            var strays = LoadError.Length == 0 && cacheRoot is not null ? DeleteStrays(Path.Combine(cacheRoot, FolderName), cutoff) : 0;
            return (entries.Count + groups.Sum(group => group.Files.Count), strays);
        }
    }

    private int DeleteStrays(string folder, DateTimeOffset cutoff)
    {
        var deleted = 0;
        try
        {
            if (!Directory.Exists(folder) || (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
                return 0;
            var named = _journal.Compressed.Select(entry => entry.Backup).Concat(_journal.Refits.SelectMany(group => group.Files).Select(file => file.Backup))
                .Select(Full).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.EnumerateFiles(folder))
            {
                try
                {
                    var name = Path.GetFileName(path);
                    if ((Sha256FileRegex().IsMatch(name) || TemporaryFileRegex().IsMatch(name)) && !named.Contains(Full(path)) &&
                        (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0 && File.GetLastWriteTimeUtc(path) < cutoff.UtcDateTime)
                    {
                        File.Delete(path);
                        deleted++;
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Left for the next cleanup.
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // The folder can't be read now; the next cleanup tries again.
        }
        return deleted;
    }

    private void DeleteUnreferenced(IEnumerable<string> backups)
    {
        foreach (var backup in backups.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (_journal.Compressed.Any(entry => string.Equals(entry.Backup, backup, StringComparison.OrdinalIgnoreCase)) ||
                _journal.Refits.Any(group => group.Files.Any(file => string.Equals(file.Backup, backup, StringComparison.OrdinalIgnoreCase))))
                continue;
            try
            {
                // Only a file this store named: <sha256>.tex or .mdl inside a texture-backups folder.
                if (Sha256FileRegex().IsMatch(Path.GetFileName(backup)) &&
                    string.Equals(Path.GetFileName(Path.GetDirectoryName(backup)), FolderName, StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(backup) && (File.GetAttributes(backup) & FileAttributes.ReparsePoint) == 0)
                    File.Delete(backup);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // A copy that can't be deleted now only takes space; it is no longer listed.
            }
        }
    }

    private void Save()
    {
        var directory = Path.GetDirectoryName(_journalPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var temporary = _journalPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_journal, JsonOptions));
        File.Move(temporary, _journalPath, true);
    }

    private void Publish()
    {
        Volatile.Write(ref _compressed, _journal.Compressed.ToArray());
        Volatile.Write(ref _kept, _journal.Kept.ToArray());
        Volatile.Write(ref _refits, _journal.Refits.ToArray());
        Volatile.Write(ref _restored, _journal.Restored.Select(Full).ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    private static bool SameFile(string a, string b) => string.Equals(Full(a), Full(b), StringComparison.OrdinalIgnoreCase);

    private static string Full(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }

    [GeneratedRegex("^[0-9A-Fa-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();

    [GeneratedRegex("^[0-9A-F]{64}\\.(tex|mdl)$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256FileRegex();

    /// <summary> What <see cref="StoreBackup"/> writes before moving a copy into place. </summary>
    [GeneratedRegex("^[0-9A-F]{64}\\.(tex|mdl)\\.[0-9a-f]{32}\\.tmp$", RegexOptions.CultureInvariant)]
    private static partial Regex TemporaryFileRegex();
}
