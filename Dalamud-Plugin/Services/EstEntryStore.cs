using System.Text.Json;
using System.Text.Json.Serialization;

namespace InstantEdit.Services;

/// <summary>
/// One EST entry XIV Instant Edit wrote into a Penumbra mod: where (mod, then <c>default</c>,
/// <c>option:group:option</c> or <c>container:group:index</c>), for which skeleton lookup, the
/// value it wrote, and the value it replaced (null: there was none).
/// </summary>
public sealed record ManagedEstEntry(
    [property: JsonPropertyName("mod")] string Mod,
    [property: JsonPropertyName("container")] string Container,
    [property: JsonPropertyName("slot")] string Slot,
    [property: JsonPropertyName("setId")] int SetId,
    [property: JsonPropertyName("genderRace")] int GenderRace,
    [property: JsonPropertyName("entry")] int Entry,
    [property: JsonPropertyName("previous")] int? Previous);

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

    public IReadOnlyList<ManagedEstEntry> Load()
    {
        lock (_sync)
        {
            try
            {
                if (!File.Exists(_path))
                    return [];
                var entries = JsonSerializer.Deserialize<List<ManagedEstEntry>>(File.ReadAllText(_path)) ?? [];
                return entries.Where(entry => entry is { Mod.Length: > 0, Container.Length: > 0, Slot.Length: > 0 }).ToArray();
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                // Without the records a later export only leaves entries in place, never removes one.
                return [];
            }
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
