using System.Text.Json;
using System.Text.Json.Serialization;

namespace InstantEdit.Services;

/// <summary>
/// The custom hair skeleton XIV Instant Edit made for a model file of a Penumbra mod: the mod (its
/// EST key), the model's mod-relative path with <c>/</c> separators, and the skeleton ID.
/// <paramref name="MovingFrom"/> holds the IDs exports are moving away from until their keys and files are
/// gone. <paramref name="ChainKinds"/> maps each physics chain's first bone to its kind, as the last
/// export wrote them. <paramref name="Folder"/> is the mod folder's name, which tells a copied folder
/// (same identifier, so the same EST key) from the mod it was copied from. Records from before these
/// fields load without them; an old single <c>movingFrom</c> number loads as a list of one.
/// </summary>
public sealed record ManagedHairSkeleton(
    [property: JsonPropertyName("mod")] string Mod,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("skeletonId")] int SkeletonId,
    [property: JsonPropertyName("movingFrom"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull),
               JsonConverter(typeof(SkeletonIdListConverter))]
    IReadOnlyList<int>? MovingFrom = null,
    [property: JsonPropertyName("chainKinds"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, string>? ChainKinds = null,
    [property: JsonPropertyName("folder"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Folder = null);

/// <summary>A list of skeleton IDs, read from a list or from the one number older records wrote.</summary>
internal sealed class SkeletonIdListConverter : JsonConverter<IReadOnlyList<int>?>
{
    public override IReadOnlyList<int>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Number
            ? [reader.GetInt32()]
            : JsonSerializer.Deserialize<List<int>>(ref reader, options);

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<int>? value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value?.ToArray(), options);
}

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

    /// <summary>
    /// The records. A file that can't be read (held by another program) throws, so no export saves over
    /// the records it holds; a damaged one is kept aside under another name and reads as no records.
    /// </summary>
    public IReadOnlyList<ManagedHairSkeleton> Load()
    {
        lock (_sync)
        {
            if (!File.Exists(_path))
                return [];
            List<ManagedHairSkeleton> entries;
            try
            {
                entries = JsonSerializer.Deserialize<List<ManagedHairSkeleton>>(File.ReadAllText(_path)) ?? [];
            }
            catch (JsonException)
            {
                // Without the records a later export picks a new ID and never removes a skeleton, so the
                // damaged file is kept for whoever can mend it.
                File.Move(_path, $"{_path}.unreadable-{DateTime.UtcNow:yyyyMMdd-HHmmss}", true);
                return [];
            }
            // A move from an ID outside the custom range would take back a vanilla or modder's skeleton.
            return entries.Where(entry => entry is { Mod.Length: > 0, Model.Length: > 0, SkeletonId: >= 1000 and <= 9999 })
                .Select(entry => entry with { MovingFrom = CustomIds(entry.MovingFrom, entry.SkeletonId) })
                .ToArray();
        }
    }

    /// <summary>The distinct IDs in the custom range other than <paramref name="skeletonId"/>, or null when none is left.</summary>
    private static IReadOnlyList<int>? CustomIds(IReadOnlyList<int>? ids, int skeletonId)
        => ids?.Where(id => id is >= 1000 and <= 9999 && id != skeletonId).Distinct().ToArray() is { Length: > 0 } kept
            ? kept
            : null;

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
                DurableFile.WriteAllText(temporary, JsonSerializer.Serialize(kept, JsonOptions));
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
