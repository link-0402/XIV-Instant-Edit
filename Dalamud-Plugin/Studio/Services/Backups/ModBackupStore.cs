using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using InstantEdit.Studio.Services.Check;

namespace InstantEdit.Studio.Services.Backups;

/// <summary>One file an operation changed: its contents before (null when it didn't exist) and after (null when it was deleted).</summary>
internal sealed class BackupFile
{
    public string RelPath { get; set; } = string.Empty;
    public string? ShaBefore { get; set; }
    public string? ShaAfter { get; set; }

    /// <summary>Where the operation moved the file, when it moved it.</summary>
    public string? MovedTo { get; set; }
}

/// <summary>One operation on one mod ("Applied 4 fixes", "Compressed 12 textures") and the files it changed.</summary>
internal sealed class BackupEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ModFolder { get; set; } = string.Empty;
    public Guid ModId { get; set; }
    public DateTime At { get; set; }
    public string Label { get; set; } = string.Empty;
    public List<BackupFile> Files { get; set; } = new();

    /// <summary>When it was restored, if it was.</summary>
    public DateTime? RestoredAt { get; set; }
}

internal sealed class BackupJournal
{
    public int Version { get; set; } = 1;
    public List<BackupEntry> Entries { get; set; } = new();
}

/// <summary>What a restore put back, and the files it left alone because they changed since.</summary>
internal sealed record RestoreResult(int Restored, IReadOnlyList<string> Skipped);

/// <summary>
/// Backups of the files clean-up and repairs change, rewrite, move or delete. Each file's contents
/// before the change are copied into the cache folder's mod-backups folder under their SHA-256,
/// so the same contents are kept once; the journal of operations lives in the config folder, since
/// restoring needs it. A restore only puts a file back while it still holds what the operation left,
/// so a later change is never overwritten. Entries are forgotten after <see cref="Retention"/>.
/// Dalamud-free.
/// </summary>
internal sealed partial class ModBackupStore
{
    public const string FolderName = "mod-backups";
    public const string JournalName = "ModBackups.json";

    /// <summary>How long the automatic cache clean-up keeps an operation's backups.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly Lock _lock = new();
    private readonly string _journalPath;
    private BackupJournal _journal = new();

    public ModBackupStore(string configDirectory) => _journalPath = Path.Combine(configDirectory, JournalName);

    /// <summary>Why the journal couldn't be read, or empty. The unreadable file is set aside, not overwritten.</summary>
    public string LoadError { get; private set; } = string.Empty;

    public void Load()
    {
        lock (_lock)
        {
            LoadError = string.Empty;
            _journal = new BackupJournal();
            if (!File.Exists(_journalPath))
                return;
            try
            {
                _journal = JsonSerializer.Deserialize<BackupJournal>(File.ReadAllText(_journalPath), JsonOptions) ?? new BackupJournal();
                _journal.Entries ??= new List<BackupEntry>();
                _journal.Entries.RemoveAll(entry => entry is null);
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                var aside = $"{_journalPath}.unreadable-{DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture)}";
                try { File.Move(_journalPath, aside); }
                catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException) { aside = _journalPath; }
                LoadError = $"The list of mod backups couldn't be read ({e.Message}); it was kept as {Path.GetFileName(aside)}. "
                          + "The backups are still in the cache folder's mod-backups folder.";
                _journal = new BackupJournal();
            }
        }
    }

    /// <summary>Starts an operation on a mod. Nothing is recorded until <see cref="BackupOperation.Commit"/>.</summary>
    public BackupOperation Begin(string modFolder, Guid modId, string label, string cacheRoot)
        => new(this, new BackupEntry { ModFolder = Key(modFolder), ModId = modId, At = DateTime.UtcNow, Label = label }, modFolder, cacheRoot);

    /// <summary>A mod's operations, newest first.</summary>
    public IReadOnlyList<BackupEntry> For(string modFolder)
    {
        var key = Key(modFolder);
        lock (_lock)
            return _journal.Entries.Where(entry => string.Equals(entry.ModFolder, key, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(entry => entry.At).ToArray();
    }

    /// <summary>
    /// Puts back every file of an operation that still holds what the operation left: rewritten files
    /// get their old contents, deleted ones come back, moved ones go back where they were. Files
    /// changed since are skipped and named. Penumbra reads the mod again when anything was put back.
    /// </summary>
    public RestoreResult Restore(Guid entryId, string modFolder, string cacheRoot, IModReloader penumbra)
    {
        BackupEntry? entry;
        lock (_lock)
            entry = _journal.Entries.FirstOrDefault(e => e.Id == entryId);
        if (entry is null)
            return new RestoreResult(0, Array.Empty<string>());

        var restored = 0;
        var skipped = new List<string>();
        foreach (var file in entry.Files)
        {
            try
            {
                var path = ModPaths.Resolve(modFolder, file.RelPath);
                var moved = file.MovedTo is { } to ? ModPaths.Resolve(modFolder, to) : null;
                var current = moved is null ? HashOf(path) : HashOf(moved);
                if (!string.Equals(current, file.ShaAfter, StringComparison.OrdinalIgnoreCase) || moved is not null && File.Exists(path))
                {
                    skipped.Add(file.RelPath);
                    continue;
                }
                if (file.ShaBefore is { } before)
                {
                    var copy = Path.Combine(cacheRoot, FolderName, before);
                    if (!File.Exists(copy))
                    {
                        skipped.Add(file.RelPath);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var temp = path + ".instant-edit-tmp";
                    File.Copy(copy, temp, overwrite: true);
                    File.Move(temp, path, overwrite: true);
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);   // the operation created it
                }
                if (moved is not null && File.Exists(moved))
                    File.Delete(moved);
                restored++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                skipped.Add(file.RelPath);
            }
        }

        if (restored > 0)
        {
            lock (_lock)
            {
                entry.RestoredAt = DateTime.UtcNow;
                Save();
            }
            penumbra.Reload(Path.GetFileName(modFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        }
        return new RestoreResult(restored, skipped);
    }

    /// <summary>
    /// Forgets operations older than <see cref="Retention"/> and deletes the copies nothing still
    /// listed needs, when they are older than that too. Nothing is deleted while the journal couldn't be read.
    /// </summary>
    public void Expire(DateTime now, string cacheRoot)
    {
        lock (_lock)
        {
            if (LoadError.Length > 0)
                return;
            var removed = _journal.Entries.RemoveAll(entry => now - entry.At > Retention);
            if (removed > 0)
                Save();

            var needed = _journal.Entries.SelectMany(entry => entry.Files).Select(file => file.ShaBefore).OfType<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var folder = Path.Combine(cacheRoot, FolderName);
            if (!Directory.Exists(folder))
                return;
            foreach (var copy in Directory.EnumerateFiles(folder))
            {
                var name = Path.GetFileName(copy);
                if (!Sha256Name().IsMatch(name) || needed.Contains(name))
                    continue;
                try
                {
                    if (now - File.GetLastWriteTimeUtc(copy) > Retention)
                        File.Delete(copy);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    internal void Record(BackupEntry entry)
    {
        lock (_lock)
        {
            _journal.Entries.Add(entry);
            Save();
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_journalPath)!);
        var temp = _journalPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_journal, JsonOptions));
        File.Move(temp, _journalPath, overwrite: true);
    }

    /// <summary>Copies a file's contents into the cache folder's backup folder under their hash; returns the hash.</summary>
    internal static string StoreCopy(string cacheRoot, byte[] content)
    {
        var sha = Convert.ToHexString(SHA256.HashData(content));
        var folder = Path.Combine(cacheRoot, FolderName);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, sha);
        if (File.Exists(path) && new FileInfo(path).Length == content.LongLength)
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            return sha;
        }
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, content);
        File.Move(temp, path, overwrite: true);
        return sha;
    }

    internal static string? HashOf(string path)
        => File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : null;

    private static string Key(string folder) => Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    [GeneratedRegex("^[0-9A-F]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Name();
}

/// <summary>
/// One operation's backups being taken: <see cref="Keep"/> each file before changing it, then
/// <see cref="Commit"/> once the change is done. Disposing it uncommitted records nothing.
/// </summary>
internal sealed class BackupOperation : IDisposable
{
    private readonly ModBackupStore _store;
    private readonly BackupEntry _entry;
    private readonly string _modFolder;
    private readonly string _cacheRoot;
    private bool _committed;

    internal BackupOperation(ModBackupStore store, BackupEntry entry, string modFolder, string cacheRoot)
    {
        _store     = store;
        _entry     = entry;
        _modFolder = modFolder;
        _cacheRoot = cacheRoot;
    }

    public BackupEntry Entry => _entry;

    /// <summary>Copies a file's current contents into the backups; throws <see cref="IOException"/> when they can't be stored.</summary>
    public void Keep(string relPath)
    {
        var normalized = relPath.Replace('\\', '/');
        if (_entry.Files.Any(file => string.Equals(file.RelPath, normalized, StringComparison.OrdinalIgnoreCase)))
            return;
        var path = ModPaths.Resolve(_modFolder, normalized);
        string? sha = null;
        if (File.Exists(path))
        {
            try
            {
                sha = ModBackupStore.StoreCopy(_cacheRoot, File.ReadAllBytes(path));
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new IOException(ex.Message, ex);
            }
        }
        _entry.Files.Add(new BackupFile { RelPath = normalized, ShaBefore = sha });
    }

    /// <summary>Notes that a kept file was moved, so a restore moves it back.</summary>
    public void Moved(string fromRel, string toRel)
    {
        Keep(fromRel);
        var file = _entry.Files.First(f => string.Equals(f.RelPath, fromRel.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
        file.MovedTo = toRel.Replace('\\', '/');
    }

    /// <summary>Records what each kept file holds now, and saves the operation in the journal.</summary>
    public void Commit()
    {
        if (_committed || _entry.Files.Count == 0)
            return;
        foreach (var file in _entry.Files)
            file.ShaAfter = ModBackupStore.HashOf(ModPaths.Resolve(_modFolder, file.MovedTo ?? file.RelPath));
        _store.Record(_entry);
        _committed = true;
    }

    public void Dispose() { }
}
