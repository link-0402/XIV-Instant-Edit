using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using InstantEdit.Services.Animations.Motion;

namespace InstantEdit.Services.Animations;

/// <summary>
/// The hand shapes the user captured from a posed hand, by name, kept in the config folder. A shape is each finger
/// bone's name and its turn from rest, as <see cref="HandShapes.Capture"/> gives them. The Timeline reads the list
/// on the UI thread while a bake may read a shape, so every member takes the lock.
/// </summary>
internal sealed class HandShapeLibrary
{
    private const string FileName = "HandShapes.json";
    // NaN has to read back so one bad shape is skipped, not the whole file.
    private static readonly JsonSerializerOptions Json = new() { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

    private readonly string? path;
    private readonly Action<string>? warn;
    private readonly object gate = new();
    private readonly Dictionary<string, ImmutableDictionary<string, Quaternion>> shapes = new(StringComparer.OrdinalIgnoreCase);

    private readonly record struct Turn(float X, float Y, float Z, float W);

    /// <param name="configDirectory">Where the shapes are kept; null keeps them in memory only.</param>
    public HandShapeLibrary(string? configDirectory, Action<string>? warn = null)
    {
        this.warn = warn;
        if (configDirectory == null)
            return;
        path = Path.Combine(configDirectory, FileName);
        try
        {
            if (!File.Exists(path) || JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllBytes(path), Json) is not { } saved)
                return;
            var skipped = 0;
            foreach (var (name, value) in saved)
            {
                if (Read(value) is { } turns && Check(name, turns, out var trimmed) == null && !shapes.ContainsKey(trimmed))
                    shapes[trimmed] = turns;
                else
                    skipped++;
            }
            if (skipped > 0)
            {
                Backup();
                warn?.Invoke($"{skipped} saved hand shape(s) could not be read and were left out; the file is kept as {FileName}.bak.");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            Backup();
            warn?.Invoke("The saved hand shapes could not be read: " + e.Message);
        }
    }

    /// <summary>Copies a file that didn't read whole beside it, so the next save, which writes only what was read, loses nothing.</summary>
    private void Backup()
    {
        try { File.Copy(path!, path + ".bak", true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            warn?.Invoke("The saved hand shapes could not be backed up: " + e.Message);
        }
    }

    /// <summary>The saved shapes' names, sorted ignoring case.</summary>
    public IReadOnlyList<string> Names
    {
        get { lock (gate) return shapes.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray(); }
    }

    /// <summary>The turns of the shape called <paramref name="name"/> (case ignored), or null when there is none.</summary>
    public ImmutableDictionary<string, Quaternion>? Get(string name)
    {
        lock (gate) return shapes.GetValueOrDefault(name.Trim());
    }

    /// <summary>
    /// Keeps <paramref name="turns"/> as <paramref name="name"/>, replacing a shape of the same name (case ignored).
    /// False, with a message for the user, when the name or the turns can't be kept.
    /// </summary>
    public bool Save(string name, ImmutableDictionary<string, Quaternion> turns, out string? error)
    {
        error = Check(name, turns, out var trimmed);
        if (error != null)
            return false;
        lock (gate)
        {
            shapes.Remove(trimmed);
            shapes[trimmed] = turns;
            Write();
        }
        return true;
    }

    /// <summary>Removes the shape called <paramref name="name"/> (case ignored); false when there was none.</summary>
    public bool Delete(string name)
    {
        lock (gate)
        {
            if (!shapes.Remove(name.Trim()))
                return false;
            Write();
            return true;
        }
    }

    /// <summary>Saves the file; the caller holds the lock. A failed save leaves the shapes in memory for this session.</summary>
    private void Write()
    {
        if (path == null)
            return;
        var file = new SortedDictionary<string, Dictionary<string, Turn>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, turns) in shapes)
            file[name] = turns.ToDictionary(pair => pair.Key, pair => new Turn(pair.Value.X, pair.Value.Y, pair.Value.Z, pair.Value.W), StringComparer.Ordinal);
        try { AnimationJournalStore.WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(file, Json)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            warn?.Invoke("The saved hand shapes could not be written: " + e.Message);
        }
    }

    private static ImmutableDictionary<string, Quaternion>? Read(JsonElement value)
    {
        try
        {
            return value.Deserialize<Dictionary<string, Turn>>(Json)?.ToImmutableDictionary(pair => pair.Key, pair => new Quaternion(pair.Value.X, pair.Value.Y, pair.Value.Z, pair.Value.W));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Why a shape of this name and these turns can't be kept, or null; <paramref name="trimmed"/> is the name as kept.</summary>
    private static string? Check(string name, ImmutableDictionary<string, Quaternion> turns, out string trimmed)
    {
        var kept = trimmed = name.Trim();
        if (kept.Length == 0)
            return "Give the shape a name.";
        if (kept.Length > MotionEvaluator.MaxHandShapeName)
            return $"A shape name has at most {MotionEvaluator.MaxHandShapeName} characters.";
        if (HandShapes.Order.Any(id => string.Equals(id, kept, StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(HandShapes.BuiltIn[id].Name, kept, StringComparison.OrdinalIgnoreCase)))
            return "A built-in shape already has that name.";
        if (turns.Count is 0 or > MotionEvaluator.MaxHandTurns || turns.Any(pair => string.IsNullOrEmpty(pair.Key) ||
                !float.IsFinite(pair.Value.LengthSquared()) || pair.Value.LengthSquared() < 1e-6f))
            return "The hand has no finger turns that can be saved.";
        return null;
    }
}
