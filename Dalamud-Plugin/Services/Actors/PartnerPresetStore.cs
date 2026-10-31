using System.Collections.Immutable;
using System.Text.Json;
using InstantEdit.Services.Animations;

namespace InstantEdit.Services.Actors;

/// <summary>
/// The partner presets, and the names whose Penumbra collection assignment Instant Edit made and may remove, kept in
/// the config folder. The UI and the spawner may read them at once, so every member takes the lock.
/// </summary>
internal sealed class PartnerPresetStore
{
    public const string FileName = "partner-presets.json";

    private readonly string? path;
    private readonly Action<string>? warn;
    private readonly object gate = new();
    private ImmutableArray<PartnerPreset> presets = [];
    private readonly List<string> owned = [];

    private sealed record SavedFile(int Version, List<JsonElement>? Presets, List<JsonElement>? OwnedAssignments);
    private sealed record WrittenFile(int Version, ImmutableArray<PartnerPreset> Presets, List<string> OwnedAssignments);

    /// <param name="configDirectory">Where the presets are kept; null keeps them in memory only.</param>
    public PartnerPresetStore(string? configDirectory, Action<string>? warn = null)
    {
        this.warn = warn;
        if (configDirectory == null)
            return;
        path = Path.Combine(configDirectory, FileName);
        try
        {
            if (!File.Exists(path) || JsonSerializer.Deserialize<SavedFile>(File.ReadAllBytes(path)) is not { } saved)
                return;
            var skipped = 0;
            var read = ImmutableArray.CreateBuilder<PartnerPreset>();
            foreach (var value in saved.Presets ?? [])
            {
                if (Read(value) is { } preset && !read.Any(item => item.Id == preset.Id) && PartnerNames.Problem(preset.Name, read, preset.Id, null) == null)
                    read.Add(preset with { Name = preset.Name.Trim() });
                else
                    skipped++;
            }
            presets = read.ToImmutable();
            foreach (var value in saved.OwnedAssignments ?? [])
            {
                if (value.ValueKind == JsonValueKind.String && value.GetString()!.Trim() is { Length: > 0 } name && !Owns(name))
                    owned.Add(name);
                else
                    skipped++;
            }
            if (skipped > 0)
            {
                Backup();
                warn?.Invoke($"Some saved partners could not be read and were left out ({skipped}); the file is kept as {FileName}.bak.");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            Backup();
            warn?.Invoke("The saved partners could not be read: " + e.Message);
        }
    }

    /// <summary>Copies a file that didn't read whole beside it, so the next save, which writes only what was read, loses nothing.</summary>
    private void Backup()
    {
        try { File.Copy(path!, path + ".bak", true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            warn?.Invoke("The saved partners could not be backed up: " + e.Message);
        }
    }

    /// <summary>The presets in the order they were first saved.</summary>
    public ImmutableArray<PartnerPreset> Presets
    {
        get { lock (gate) return presets; }
    }

    /// <summary>
    /// Keeps <paramref name="preset"/> with its name trimmed, replacing the preset of the same Id. False, with a message
    /// for the user, when the name can't be used (see <see cref="PartnerNames.Problem"/>).
    /// </summary>
    public bool Save(PartnerPreset preset, string? playerName, out string? error)
    {
        lock (gate)
        {
            error = PartnerNames.Problem(preset.Name, presets, preset.Id, playerName);
            if (error != null)
                return false;
            var kept = preset with { Name = preset.Name.Trim() };
            var at = IndexOf(preset.Id);
            presets = at >= 0 ? presets.SetItem(at, kept) : presets.Add(kept);
            Write();
            return true;
        }
    }

    /// <summary>Removes the preset with this Id; false when there was none.</summary>
    public bool Delete(Guid id)
    {
        lock (gate)
        {
            var at = IndexOf(id);
            if (at < 0)
                return false;
            presets = presets.RemoveAt(at);
            Write();
            return true;
        }
    }

    /// <summary>Whether Instant Edit made the collection assignment for the name <paramref name="name"/> (case ignored).</summary>
    public bool OwnsAssignment(string name)
    {
        lock (gate) return Owns(name.Trim());
    }

    /// <summary>Records whether Instant Edit made the collection assignment for <paramref name="name"/> (case ignored).</summary>
    public void SetOwnsAssignment(string name, bool owns)
    {
        var trimmed = name.Trim();
        lock (gate)
        {
            if (owns == Owns(trimmed) || trimmed.Length == 0)
                return;
            if (owns)
                owned.Add(trimmed);
            else
                owned.RemoveAll(item => string.Equals(item, trimmed, StringComparison.OrdinalIgnoreCase));
            Write();
        }
    }

    /// <summary>Where the preset with this Id is, or -1; the caller holds the lock.</summary>
    private int IndexOf(Guid id)
    {
        for (var i = 0; i < presets.Length; i++)
            if (presets[i].Id == id)
                return i;
        return -1;
    }

    /// <summary>The caller holds the lock, or is the constructor.</summary>
    private bool Owns(string name) => owned.Any(item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Saves the file; the caller holds the lock. A failed save leaves the presets in memory for this session.</summary>
    private void Write()
    {
        if (path == null)
            return;
        try { AnimationJournalStore.WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(new WrittenFile(1, presets, owned))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            warn?.Invoke("The saved partners could not be written: " + e.Message);
        }
    }

    /// <summary>A saved preset, or null when it has no Id or name, or a link without an Id or name.</summary>
    private static PartnerPreset? Read(JsonElement value)
    {
        try
        {
            return value.Deserialize<PartnerPreset>() is { Name: not null } preset && preset.Id != Guid.Empty &&
                   Linked(preset.Collection) && Linked(preset.Design) && Linked(preset.Profile)
                ? preset
                : null;
        }
        catch (JsonException)
        {
            return null;
        }

        static bool Linked(PartnerLink? link) => link == null || link.Id != Guid.Empty && link.Name != null;
    }
}
