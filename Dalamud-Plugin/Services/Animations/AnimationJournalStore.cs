using System.Text.Json;
using InstantEdit.Models;

namespace InstantEdit.Services.Animations;

internal sealed class AnimationJournalStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, IncludeFields = true };
    private readonly string root;
    public AnimationJournalStore(string configDirectory)
    {
        root = Path.Combine(Path.GetFullPath(configDirectory), "AnimationEdits");
        TextureFiles.EnsureLocalPath(root);
        Directory.CreateDirectory(root);
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
            TextureFiles.EnsureLocalPath(path);
            if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("An animation recovery record exceeds 16 MiB.");
            var record = JsonSerializer.Deserialize<AnimationEditJournal>(File.ReadAllText(path), Json)
                ?? throw new InvalidDataException("Invalid animation recovery record.");
            if (record.Version != 1 || record.Id != id || record.Request?.Id != id) throw new InvalidDataException("Unsupported animation recovery record.");
            result.Add(record);
        }
        return result.OrderByDescending(r => r.CreatedUtc).ToArray();
    }
    public void Save(AnimationEditJournal record)
    {
        var dir = DirectoryFor(record.Id); Directory.CreateDirectory(dir);
        WriteAtomic(Path.Combine(dir, "journal.json"), JsonSerializer.SerializeToUtf8Bytes(record, Json));
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
