using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace InstantEdit.Studio.Services.Library;

/// <summary>
/// What the library scans found out about one mod, valid while its meta.json's LastWrite and its
/// folder's write time stay the same. Each part is null (or not read) until a scan needs it.
/// </summary>
internal sealed class ModScanEntry
{
    public DateTime LastWrite;
    public DateTime DirWrite;
    public long? Size;
    public IReadOnlyList<ChangeTag>? Tags;
    public bool PrintRead;
    public ModFingerprint? Print;
}

/// <summary>
/// The library scans' results kept in the cache folder (<see cref="FileName"/>), so a new session
/// reads only mods that are new or changed. Losing the file costs one full scan: a missing,
/// unreadable or older-version file loads as empty. Written whole to a temporary file and moved
/// over the old one. Dalamud-free.
/// </summary>
internal static class LibraryScanCache
{
    public const string FileName = "library-scans.json";
    public const string TempFileName = FileName + ".tmp";

    // Raised when what a scan finds changes (a new tag kind, a new rule), so old results are read again.
    private const int Version = 1;

    private static readonly JsonSerializerSettings Settings = new()
    {
        Converters = { new StringEnumConverter() },
        NullValueHandling = NullValueHandling.Ignore,
    };

    /// <summary>The entries saved in <paramref name="path"/> by mod folder name; empty when there are none to use.</summary>
    public static Dictionary<string, ModScanEntry> Load(string path)
    {
        var entries = new Dictionary<string, ModScanEntry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(path))
                return entries;
            var file = JsonConvert.DeserializeObject<CacheFile>(File.ReadAllText(path), Settings);
            if (file?.Version != Version || file.Mods == null)
                return entries;
            foreach (var (folder, saved) in file.Mods)
                if (saved != null)
                    entries[folder] = saved.ToEntry();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidCastException)
        {
            entries.Clear();
        }
        return entries;
    }

    /// <summary>Saves the entries; a cache that can't be written is skipped (the next session scans again).</summary>
    public static void Save(string path, IEnumerable<KeyValuePair<string, ModScanEntry>> entries)
    {
        var file = new CacheFile { Version = Version };
        foreach (var (folder, entry) in entries)
            file.Mods![folder] = SavedEntry.From(entry);
        var temp = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".tmp");
        try
        {
            File.WriteAllText(temp, JsonConvert.SerializeObject(file, Formatting.None, Settings));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temp); } catch (Exception) { /* left for the next save */ }
        }
    }

    private sealed class CacheFile
    {
        public int Version;
        public Dictionary<string, SavedEntry?>? Mods = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class SavedEntry
    {
        public long LastWrite;
        public long DirWrite;
        public long? Size;
        public List<SavedTag>? Tags;
        public bool PrintRead;
        public SavedPrint? Print;

        public static SavedEntry From(ModScanEntry entry) => new()
        {
            LastWrite = entry.LastWrite.Ticks,
            DirWrite  = entry.DirWrite.Ticks,
            Size      = entry.Size,
            Tags      = entry.Tags?.Select(tag => new SavedTag { Kind = tag.Kind, Label = tag.Label, Search = tag.Search }).ToList(),
            PrintRead = entry.PrintRead,
            Print     = entry.Print is { } print
                ? new SavedPrint { Name = print.Name, Structure = print.Structure, Files = print.Files.Select(f => new SavedFile { Rel = f.Rel, Size = f.Size, Sha256 = f.Sha256 }).ToList() }
                : null,
        };

        public ModScanEntry ToEntry() => new()
        {
            LastWrite = new DateTime(LastWrite),
            DirWrite  = new DateTime(DirWrite),
            Size      = Size,
            Tags      = Tags?.Select(tag => new ChangeTag(tag.Kind, tag.Label ?? string.Empty, tag.Search ?? string.Empty)).ToArray(),
            PrintRead = PrintRead,
            Print     = Print is { } print
                ? new ModFingerprint(print.Name ?? string.Empty, (print.Files ?? new()).Select(f => (f.Rel ?? string.Empty, f.Size, f.Sha256 ?? string.Empty)).ToArray(), print.Structure ?? string.Empty)
                : null,
        };
    }

    private sealed class SavedTag
    {
        public ChangeKind Kind;
        public string? Label;
        public string? Search;
    }

    private sealed class SavedPrint
    {
        public string? Name;
        public string? Structure;
        public List<SavedFile>? Files;
    }

    private sealed class SavedFile
    {
        public string? Rel;
        public long Size;
        public string? Sha256;
    }
}
