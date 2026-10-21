using System.Text.Json;
using System.Text.Json.Serialization;

namespace InstantEdit.Services;

/// <summary>
/// The custom hair skeleton XIV Instant Edit made for a model file of a Penumbra mod: the mod (its
/// EST key), the model's mod-relative path with <c>/</c> separators, and the skeleton ID.
/// </summary>
public sealed record ManagedHairSkeleton(
    [property: JsonPropertyName("mod")] string Mod,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("skeletonId")] int SkeletonId);

/// <summary>
/// The hair skeletons exports made, so a later export of the same model reuses its ID and an export
/// without a rig takes back only those files. Durable state: it lives in the plugin's config folder,
/// not the cache.
/// </summary>
public sealed class HairSkeletonStore
{
    private const int MaximumEntries = 4096;
    private readonly string _path;
    private readonly object _sync = new();

    public HairSkeletonStore(string configDirectory)
        => _path = Path.Combine(Path.GetFullPath(configDirectory), "hair-skeletons.json");

    public IReadOnlyList<ManagedHairSkeleton> Load()
    {
        lock (_sync)
        {
            try
            {
                if (!File.Exists(_path))
                    return [];
                var entries = JsonSerializer.Deserialize<List<ManagedHairSkeleton>>(File.ReadAllText(_path)) ?? [];
                return entries.Where(entry => entry is { Mod.Length: > 0, Model.Length: > 0, SkeletonId: >= 1000 and <= 9999 })
                    .ToArray();
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                // Without the records a later export picks a new ID and never removes a skeleton.
                return [];
            }
        }
    }

    public void Save(IEnumerable<ManagedHairSkeleton> entries)
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
