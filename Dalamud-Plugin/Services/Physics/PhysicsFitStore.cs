using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InstantEdit.Services.Physics;

/// <summary>
/// A skirt shape "Fit to my body" moved: the race whose physics it was in, the shape (capsule or sphere) by
/// name, its offsets from its bones before the fit (<c>original</c>) and the ones the fit wrote, each as
/// start then end x, y, z (a sphere's end is its start).
/// </summary>
internal sealed record PhysicsFitRecord(
    [property: JsonPropertyName("race")] int Race,
    [property: JsonPropertyName("target")] PhysicsFitTarget Target,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("original")] float[] Original,
    [property: JsonPropertyName("written")] float[] Written);

/// <summary>
/// The skirt shape offsets fits wrote and the ones they replaced. A physics file doesn't say whether an
/// offset holds the Customize+ move already, so a second fit measures from the original, but only while
/// the shape still sits exactly where the fit put it: a file someone changed since fits as new. The written
/// offsets tell files apart, so a fitted file keeps its record when it is saved into another mod.
/// Durable state: it lives in the plugin's config folder, not the cache.
/// </summary>
internal sealed class PhysicsFitStore
{
    private const int MaximumRecords = 1024;

    /// <summary> A tenth of a millimetre: the file's floats round-trip well within it. </summary>
    private const float Same = 1e-4f;

    private readonly string path;
    private readonly object sync = new();

    public PhysicsFitStore(string configDirectory)
        => path = Path.Combine(Path.GetFullPath(configDirectory), "physics-fit.json");

    public IReadOnlyList<PhysicsFitRecord> Load()
    {
        lock (sync)
        {
            try
            {
                if (!File.Exists(path))
                    return [];
                var records = JsonSerializer.Deserialize<List<PhysicsFitRecord>>(File.ReadAllText(path), JsonOptions) ?? [];
                return records.Where(record => record is { Name.Length: > 0, Original.Length: 6, Written.Length: 6, Target: PhysicsFitTarget.Capsule or PhysicsFitTarget.Sphere })
                    .ToArray();
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                // Without the records a fit measures every shape as a first fit does.
                return [];
            }
        }
    }

    public void Save(IEnumerable<PhysicsFitRecord> records)
    {
        lock (sync)
        {
            // The newest records are the ones fits can still use.
            var kept = records.TakeLast(MaximumRecords).ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(kept, JsonOptions));
                File.Move(temporary, path, true);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }
    }

    /// <summary> The offsets each skirt shape had before a fit, for the shapes in <paramref name="file"/> still exactly where a fit put them. </summary>
    public Dictionary<(PhysicsFitTarget Target, string Name), (Vector3 Start, Vector3 End)> Originals(PhybFile file, ushort race)
    {
        var originals = new Dictionary<(PhysicsFitTarget, string), (Vector3, Vector3)>();
        // Later records win.
        foreach (var record in Load())
            if (record.Race == race && Offsets(file, record.Target, record.Name) is { } now && Near(now, Read(record.Written)))
                originals[(record.Target, record.Name)] = Read(record.Original);
        return originals;
    }

    /// <summary>
    /// Records the skirt shapes <paramref name="applied"/> moved in a file that was <paramref name="before"/>: each one's
    /// original is the one <paramref name="originals"/> measured it from, else its offsets before.
    /// </summary>
    public void Record(PhybFile before, ushort race, IEnumerable<PhysicsFitItem> applied,
        IReadOnlyDictionary<(PhysicsFitTarget Target, string Name), (Vector3 Start, Vector3 End)>? originals)
    {
        var added = new List<PhysicsFitRecord>();
        foreach (var item in applied)
        {
            // The butt balls the fit adds are measured on the skin each time and need none.
            if (item is not { Target: PhysicsFitTarget.Capsule or PhysicsFitTarget.Sphere, Shape: { NewOn: null } move })
                continue;
            (Vector3, Vector3) from;
            if (originals != null && originals.TryGetValue((item.Target, item.Name), out var known))
                from = known;
            else if (Offsets(before, item.Target, item.Name) is { } was)
                from = was;
            else
                continue;
            var written = item.Target == PhysicsFitTarget.Sphere ? (move.Start, move.Start) : (move.Start, move.End);
            added.Add(new PhysicsFitRecord(race, item.Target, item.Name, Write(from), Write(written)));
        }
        if (added.Count == 0)
            return;
        // A record for the same offsets again is replaced, so the newest original wins.
        Save(Load().Where(record => !added.Any(other => other.Race == record.Race && other.Target == record.Target && other.Name == record.Name &&
                                                        Near(Read(other.Written), Read(record.Written))))
            .Concat(added));
    }

    /// <summary> A shape's offsets from its bones in the file: a capsule's start and end, a sphere's offset twice. </summary>
    private static (Vector3 Start, Vector3 End)? Offsets(PhybFile file, PhysicsFitTarget target, string name)
    {
        if (target == PhysicsFitTarget.Capsule)
            return file.Collision.Capsules.FirstOrDefault(shape => shape.Name.Text == name) is { } capsule ? (capsule.StartOffset, capsule.EndOffset) : null;
        return file.Collision.Spheres.FirstOrDefault(shape => shape.Name.Text == name) is { } sphere ? (sphere.Offset, sphere.Offset) : null;
    }

    private static bool Near((Vector3 Start, Vector3 End) a, (Vector3 Start, Vector3 End) b)
        => Vector3.Distance(a.Start, b.Start) < Same && Vector3.Distance(a.End, b.End) < Same;

    private static (Vector3, Vector3) Read(float[] v) => (new Vector3(v[0], v[1], v[2]), new Vector3(v[3], v[4], v[5]));

    private static float[] Write((Vector3 Start, Vector3 End) offsets)
        => [offsets.Start.X, offsets.Start.Y, offsets.Start.Z, offsets.End.X, offsets.End.Y, offsets.End.Z];

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
}
