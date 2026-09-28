using System.Text.Json;
using InstantEdit.Models;

namespace InstantEdit.Services.Animations;

internal sealed class AnimationJournalStore
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, IncludeFields = true };
    private readonly string root;
    private readonly Action<string>? warn;
    private string OffsetBackupPath => Path.Combine(root, "livepose-offset-backup.json");
    public AnimationJournalStore(string configDirectory, Action<string>? warn = null)
    {
        root = Path.Combine(Path.GetFullPath(configDirectory), "AnimationEdits");
        this.warn = warn;
        TextureFiles.EnsureLocalPath(root);
        Directory.CreateDirectory(root);
        Cleanup();
    }
    /// <summary>Moves recovery records an older version kept under <paramref name="legacyDirectory"/>.</summary>
    public static void ImportLegacy(string legacyDirectory, string configDirectory)
    {
        var legacy = Path.Combine(Path.GetFullPath(legacyDirectory), "AnimationEdits");
        var root = Path.Combine(Path.GetFullPath(configDirectory), "AnimationEdits");
        if (!Directory.Exists(legacy) || PathRules.SamePhysicalPath(legacy, root)) return;
        TextureFiles.EnsureLocalPath(root);
        Directory.CreateDirectory(root);
        foreach (var source in Directory.EnumerateFileSystemEntries(legacy).ToArray())
        {
            var target = Path.Combine(root, Path.GetFileName(source));
            if (File.Exists(target) || Directory.Exists(target)) continue;
            if (!Directory.Exists(source)) { File.Move(source, target); continue; }
            try { Directory.Move(source, target); }
            catch (IOException)
            {
                // Directory.Move cannot cross volumes; job folders hold only files. They are copied
                // beside the target first: a half-copied target would be skipped on every later start.
                var partial = target + ".importing";
                if (Directory.Exists(partial)) Directory.Delete(partial, recursive: true);
                Directory.CreateDirectory(partial);
                try
                {
                    foreach (var file in Directory.EnumerateFiles(source))
                        File.Copy(file, Path.Combine(partial, Path.GetFileName(file)));
                    Directory.Move(partial, target);
                }
                catch
                {
                    try { Directory.Delete(partial, recursive: true); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                    throw;
                }
                Directory.Delete(source, recursive: true);
            }
        }
    }
    public void Cleanup(DateTime? now = null)
    {
        var cutoff = (now ?? DateTime.UtcNow) - Retention;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            if (!Guid.TryParseExact(Path.GetFileName(dir), "N", out _)) continue;
            var path = Path.Combine(dir, "journal.json");
            try
            {
                if (!File.Exists(path)) continue;
                var record = JsonSerializer.Deserialize<AnimationEditJournal>(File.ReadAllText(path), Json);
                if (record is null || record.CreatedUtc > cutoff) continue;
            }
            catch (Exception e) when (e is IOException or JsonException or InvalidDataException)
            {
                continue;
            }
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { warn?.Invoke($"Could not delete the expired animation record {Path.GetFileName(dir)}: {e.Message}"); }
        }
    }
    public string DirectoryFor(Guid id)
    {
        if (id == Guid.Empty) throw new InvalidDataException("The animation job has no identity.");
        var path = Path.Combine(root, id.ToString("N")); TextureFiles.EnsureLocalPath(path);
        return path;
    }
    public IReadOnlyList<AnimationEditJournal> Load()
    {
        var result = new List<AnimationEditJournal>();
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            if (!Guid.TryParseExact(Path.GetFileName(dir), "N", out var id)) continue;
            var path = Path.Combine(DirectoryFor(id), "journal.json");
            if (!File.Exists(path)) continue;
            try
            {
                TextureFiles.EnsureLocalPath(path);
                if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("An animation recovery record exceeds 16 MiB.");
                var record = JsonSerializer.Deserialize<AnimationEditJournal>(File.ReadAllText(path), Json)
                    ?? throw new InvalidDataException("Invalid animation recovery record.");
                if (record.Version is not (1 or 2 or 3) || record.Id != id || record.Request?.Id != id) throw new InvalidDataException("Unsupported animation recovery record.");
                // Staged outputs live in the record's own folder. Records moved out of an older
                // version's cache still name the folder they were written in.
                for (var i = 0; i < record.Files.Count; i++)
                    if (record.Files[i].Staged is { Length: > 0 } staged)
                        record.Files[i] = record.Files[i] with { Staged = Path.Combine(DirectoryFor(id), Path.GetFileName(staged)) };
                result.Add(record);
            }
            catch (Exception e) when (e is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
            {
                // One unreadable record must not take the others, and the Animations panel, down with it.
                warn?.Invoke($"Skipped the animation recovery record {id:N}: {e.Message}");
            }
        }
        return result.OrderByDescending(r => r.CreatedUtc).ToArray();
    }
    public void Save(AnimationEditJournal record)
    {
        var dir = DirectoryFor(record.Id); Directory.CreateDirectory(dir);
        WriteAtomic(Path.Combine(dir, "journal.json"), JsonSerializer.SerializeToUtf8Bytes(record, Json));
    }
    public LivePoseOffsetBackup? LoadOffsetBackup()
    {
        var path = OffsetBackupPath;
        if (!File.Exists(path)) return null;
        TextureFiles.EnsureLocalPath(path);
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("The LivePose offset backup exceeds 16 MiB.");
        return JsonSerializer.Deserialize<LivePoseOffsetBackup>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException("Invalid LivePose offset backup.");
    }
    public void SaveOffsetBackup(LivePoseOffsetBackup backup)
        => WriteAtomic(OffsetBackupPath, JsonSerializer.SerializeToUtf8Bytes(backup, Json));
    public void ClearOffsetBackup()
    {
        var path = OffsetBackupPath;
        if (!File.Exists(path)) return;
        TextureFiles.EnsureLocalPath(path);
        File.Delete(path);
    }
    internal static void WriteAtomic(string path, byte[] data)
    {
        TextureFiles.EnsureLocalPath(path);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(data); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
