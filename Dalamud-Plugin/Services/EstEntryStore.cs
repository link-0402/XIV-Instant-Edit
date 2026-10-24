using System.Text.Json;
using System.Text.Json.Serialization;

namespace InstantEdit.Services;

/// <summary>
/// One EST entry XIV Instant Edit wrote into a Penumbra mod: where (mod, then <c>default</c>,
/// <c>option:group:option</c> or <c>container:group:index</c>), for which skeleton lookup, the
/// value it wrote, and the value it replaced (null: there was none). <paramref name="Folder"/> is the
/// mod folder's name, which tells a copied folder (same identifier, so the same EST key) from the mod
/// it was copied from. Records from before it load without it.
/// </summary>
public sealed record ManagedEstEntry(
    [property: JsonPropertyName("mod")] string Mod,
    [property: JsonPropertyName("container")] string Container,
    [property: JsonPropertyName("slot")] string Slot,
    [property: JsonPropertyName("setId")] int SetId,
    [property: JsonPropertyName("genderRace")] int GenderRace,
    [property: JsonPropertyName("entry")] int Entry,
    [property: JsonPropertyName("previous")] int? Previous,
    [property: JsonPropertyName("folder"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Folder = null);

/// <summary>
/// The EST entries exports wrote, so a later export without a hair skeleton takes back
/// only those and never an entry the modder set. Durable state: it lives in the plugin's
/// config folder, not the cache.
/// </summary>
public sealed class EstEntryStore
{
    private const int MaximumEntries = 4096;
    private readonly string _path;
    private readonly object _sync = new();

    public EstEntryStore(string configDirectory)
        => _path = Path.Combine(Path.GetFullPath(configDirectory), "est-entries.json");

    /// <summary>
    /// The records. A file that can't be read (held by another program) throws, so no export saves over
    /// the records it holds; a damaged one is kept aside under another name and reads as no records.
    /// </summary>
    public IReadOnlyList<ManagedEstEntry> Load()
    {
        lock (_sync)
        {
            if (!File.Exists(_path))
                return [];
            List<ManagedEstEntry> entries;
            try
            {
                entries = JsonSerializer.Deserialize<List<ManagedEstEntry>>(File.ReadAllText(_path)) ?? [];
            }
            catch (JsonException)
            {
                // Without the records a later export only leaves entries in place, never removes one, so the
                // damaged file is kept for whoever can mend it.
                File.Move(_path, $"{_path}.unreadable-{DateTime.UtcNow:yyyyMMdd-HHmmss}", true);
                return [];
            }
            return entries.Where(entry => entry is { Mod.Length: > 0, Container.Length: > 0, Slot.Length: > 0 }).ToArray();
        }
    }

    public void Save(IEnumerable<ManagedEstEntry> entries)
    {
        lock (_sync)
        {
            // The newest records are the ones exports can still act on.
            var kept = entries.TakeLast(MaximumEntries).ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(kept, JsonOptions));
                File.Move(temporary, _path, true);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
}
