using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace InstantEdit.Services.TextureCompression;

/// <summary> A mod texture automatic compression replaced, and where its original is kept. </summary>
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
    public int Width { get; init; }
    public int Height { get; init; }
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

internal sealed class TextureBackupJournal
{
    public int Version { get; set; } = 1;
    public List<CompressedTexture> Compressed { get; set; } = [];
    public List<KeptTexture> Kept { get; set; } = [];
}

/// <summary>
/// The originals of the textures automatic compression replaced. Each original is copied into the
/// cache folder's texture-backups folder under its SHA-256, so identical files share one copy; the
/// list of replaced files and of the textures the check kept lives in the config folder, since it
/// is what restoring needs. Backups are kept until they are restored or deleted. Dalamud-free.
/// </summary>
internal sealed partial class TextureBackupStore
{
    public const string FolderName = "texture-backups";
    public const string JournalName = "CompressedTextures.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly Lock _lock = new();
    private readonly string _journalPath;
    private TextureBackupJournal _journal = new();
    private IReadOnlyList<CompressedTexture> _compressed = [];
    private IReadOnlyList<KeptTexture> _kept = [];

    public TextureBackupStore(string configDirectory) => _journalPath = Path.Combine(configDirectory, JournalName);

    /// <summary> Why the saved list couldn't be read, or empty. The unreadable file is set aside, not overwritten. </summary>
    public string LoadError { get; private set; } = string.Empty;

    public IReadOnlyList<CompressedTexture> Compressed => Volatile.Read(ref _compressed);
    public IReadOnlyList<KeptTexture> Kept => Volatile.Read(ref _kept);

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
    public static string StoreBackup(string cacheRoot, byte[] original, string sha256)
    {
        if (!Sha256Regex().IsMatch(sha256))
            throw new ArgumentException("The backup's hash is invalid.", nameof(sha256));
        var folder = Path.Combine(cacheRoot, FolderName);
        TextureFiles.EnsureLocalPath(folder);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, sha256.ToUpperInvariant() + ".tex");
        if (File.Exists(path) && new FileInfo(path).Length == original.LongLength && Hash(File.ReadAllBytes(path)) == sha256.ToUpperInvariant())
            return path;
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

    /// <summary> Remembers a replaced file. An earlier entry for the same file is replaced, and its backup deleted when nothing else uses it. </summary>
    public void Record(CompressedTexture entry)
    {
        lock (_lock)
        {
            var replaced = _journal.Compressed.Where(existing => SameFile(existing.File, entry.File)).ToList();
            _journal.Compressed.RemoveAll(existing => SameFile(existing.File, entry.File));
            _journal.Kept.RemoveAll(kept => SameFile(kept.File, entry.File));
            _journal.Compressed.Add(entry);
            Save();
            DeleteUnreferenced(replaced);
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
            DeleteUnreferenced(entries);
            Publish();
        }
    }

    /// <summary> How many originals are kept and the bytes they take, each shared copy once. </summary>
    public (int Files, long Bytes) BackupTotals()
    {
        var distinct = Compressed.GroupBy(entry => entry.Backup, StringComparer.OrdinalIgnoreCase).ToList();
        return (distinct.Count, distinct.Sum(group => group.First().OriginalLength));
    }

    private void DeleteUnreferenced(IEnumerable<CompressedTexture> entries)
    {
        foreach (var backup in entries.Select(entry => entry.Backup).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (_journal.Compressed.Any(entry => string.Equals(entry.Backup, backup, StringComparison.OrdinalIgnoreCase)))
                continue;
            try
            {
                // Only a file this store named: <sha256>.tex inside a texture-backups folder.
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
    }

    private static bool SameFile(string a, string b) => string.Equals(Full(a), Full(b), StringComparison.OrdinalIgnoreCase);

    private static string Full(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }

    [GeneratedRegex("^[0-9A-Fa-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();

    [GeneratedRegex("^[0-9A-F]{64}\\.tex$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256FileRegex();
}
