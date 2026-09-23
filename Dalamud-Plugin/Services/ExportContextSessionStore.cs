using System.Text.Json;
using InstantEdit.Models;

namespace InstantEdit.Services;

/// <summary>
/// Append-by-session durable context storage.  Each plugin run writes only its
/// own event file; newer upserts and tombstones override older session files.
/// </summary>
public sealed class ExportContextSessionStore
{
    private const string Schema = "instant-edit.context-session";
    private const int Version = 1;
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    private sealed record StoredUpsert(
        DateTimeOffset UpdatedAtUtc,
        PersistedExportContext Context);

    private sealed record StoredTombstone(
        DateTimeOffset UpdatedAtUtc,
        string ContextId);

    private sealed class SessionDocument
    {
        public string Schema { get; set; } = ExportContextSessionStore.Schema;
        public int Version { get; set; } = ExportContextSessionStore.Version;
        public required string SessionId { get; set; }
        public DateTimeOffset CreatedAtUtc { get; set; }
        public DateTimeOffset UpdatedAtUtc { get; set; }
        public List<StoredUpsert> Upserts { get; set; } = [];
        public List<StoredTombstone> Tombstones { get; set; } = [];
    }

    private readonly object _lock = new();
    private readonly string _directory;
    private readonly string _sessionId;
    private readonly string _sessionPath;
    private readonly Action<string, Exception?>? _log;
    private readonly Dictionary<string, PersistedExportContext> _snapshot = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StoredUpsert> _sessionUpserts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StoredTombstone> _sessionTombstones = new(StringComparer.Ordinal);
    private readonly DateTimeOffset _createdAtUtc;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public ExportContextSessionStore(
        string configDirectory,
        string sessionId,
        Action<string, Exception?>? log = null,
        DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(configDirectory))
            throw new ArgumentException("A plugin configuration directory is required.", nameof(configDirectory));
        if (!Guid.TryParseExact(sessionId, "N", out _))
            throw new ArgumentException("The context session id must be a compact GUID.", nameof(sessionId));

        _directory = Path.Combine(Path.GetFullPath(configDirectory), "Contexts");
        _sessionId = sessionId;
        _sessionPath = Path.Combine(_directory, sessionId + ".json");
        _log = log;
        _createdAtUtc = now ?? DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Moves session files left in <paramref name="legacyDirectory"/>'s Contexts folder
    /// (1.2.x kept them in the managed cache) into <paramref name="configDirectory"/>.
    /// Temp cleaners can remove the cache's ownership marker while these recently
    /// written files survive, which leaves the cache non-empty and unowned; moving
    /// them out lets the cache re-establish its marker.  Returns the number moved.
    /// </summary>
    public static int ImportLegacy(string legacyDirectory, string configDirectory, Action<string, Exception?>? log = null)
    {
        var source = Path.Combine(Path.GetFullPath(legacyDirectory), "Contexts");
        var destination = Path.Combine(Path.GetFullPath(configDirectory), "Contexts");
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(source) ||
            (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            return 0;

        Directory.CreateDirectory(destination);
        var moved = 0;
        foreach (var path in Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            var isSession = name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
                            Guid.TryParseExact(Path.GetFileNameWithoutExtension(name), "N", out _);
            var isInterruptedWrite = name.StartsWith('.') && name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    continue;
                if (isInterruptedWrite)
                {
                    File.Delete(path);
                    continue;
                }
                if (!isSession)
                    continue;
                var target = Path.Combine(destination, name);
                // Session ids are unique per plugin run, so an existing target is
                // this same file from an earlier, interrupted cross-volume move.
                if (File.Exists(target))
                    File.Delete(path);
                else
                    File.Move(path, target);
                moved++;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                log?.Invoke($"Could not move context session {name} out of the cache.", error);
            }
        }

        try
        {
            if (!Directory.EnumerateFileSystemEntries(source).Any())
                Directory.Delete(source);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            log?.Invoke("Could not remove the legacy context folder from the cache.", error);
        }
        return moved;
    }

    public IReadOnlyList<PersistedExportContext> Load(DateTimeOffset? now = null)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(_directory);
            var current = now ?? DateTimeOffset.UtcNow;
            var cutoff = current - Retention;
            var events = new List<(DateTimeOffset Timestamp, int Kind, PersistedExportContext? Context, string Id)>();

            foreach (var path in Directory.EnumerateFiles(_directory, "*.json", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (!Guid.TryParseExact(name, "N", out _))
                    continue;
                try
                {
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                        continue;
                    var document = JsonSerializer.Deserialize<SessionDocument>(File.ReadAllText(path), JsonOptions);
                    if (document is null || document.Schema != Schema || document.Version != Version ||
                        !string.Equals(document.SessionId, name, StringComparison.Ordinal))
                        throw new InvalidDataException("The context session document has an invalid envelope.");

                    foreach (var upsert in document.Upserts)
                    {
                        if (upsert.UpdatedAtUtc >= cutoff && upsert.Context is not null &&
                            !string.IsNullOrWhiteSpace(upsert.Context.ContextId))
                            events.Add((upsert.UpdatedAtUtc, 0, upsert.Context, upsert.Context.ContextId));
                    }
                    foreach (var tombstone in document.Tombstones)
                    {
                        if (tombstone.UpdatedAtUtc >= cutoff && !string.IsNullOrWhiteSpace(tombstone.ContextId))
                            events.Add((tombstone.UpdatedAtUtc, 1, null, tombstone.ContextId));
                    }

                    if (document.UpdatedAtUtc < cutoff)
                        TryDelete(path);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
                {
                    _log?.Invoke($"Ignoring unreadable context session {Path.GetFileName(path)}.", error);
                }
            }

            _snapshot.Clear();
            foreach (var item in events.OrderBy(item => item.Timestamp).ThenBy(item => item.Kind))
            {
                if (item.Context is null)
                    _snapshot.Remove(item.Id);
                else if (item.Context.LastTouchedAtUtc >= cutoff)
                    _snapshot[item.Id] = item.Context;
            }
            return _snapshot.Values.ToArray();
        }
    }

    public void Persist(IReadOnlyList<PersistedExportContext> contexts, DateTimeOffset? now = null)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(_directory);
            var current = now ?? DateTimeOffset.UtcNow;
            var cutoff = current - Retention;
            var next = contexts.ToDictionary(item => item.ContextId, StringComparer.Ordinal);

            foreach (var expired in next.Where(pair => pair.Value.LastTouchedAtUtc < cutoff)
                         .Select(pair => pair.Key).ToArray())
                next.Remove(expired);

            foreach (var removed in _snapshot.Keys.Except(next.Keys, StringComparer.Ordinal).ToArray())
            {
                _sessionUpserts.Remove(removed);
                _sessionTombstones[removed] = new StoredTombstone(current, removed);
            }

            foreach (var pair in next)
            {
                if (!_snapshot.TryGetValue(pair.Key, out var previous) || !Equivalent(previous, pair.Value))
                {
                    _sessionTombstones.Remove(pair.Key);
                    _sessionUpserts[pair.Key] = new StoredUpsert(current, pair.Value);
                }
            }

            foreach (var expired in _sessionTombstones
                         .Where(pair => pair.Value.UpdatedAtUtc < cutoff)
                         .Select(pair => pair.Key).ToArray())
                _sessionTombstones.Remove(expired);

            if (_sessionUpserts.Count > 0 || _sessionTombstones.Count > 0)
                WriteCurrent(current);

            _snapshot.Clear();
            foreach (var pair in next)
                _snapshot[pair.Key] = pair.Value;
            Cleanup(current);
        }
    }

    private void WriteCurrent(DateTimeOffset now)
    {
        var document = new SessionDocument
        {
            SessionId = _sessionId,
            CreatedAtUtc = _createdAtUtc,
            UpdatedAtUtc = now,
            Upserts = _sessionUpserts.Values.OrderBy(item => item.Context.ContextId, StringComparer.Ordinal).ToList(),
            Tombstones = _sessionTombstones.Values.OrderBy(item => item.ContextId, StringComparer.Ordinal).ToList(),
        };
        var temporary = Path.Combine(_directory, $".{_sessionId}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, document, JsonOptions);
                stream.Flush(true);
            }
            File.Move(temporary, _sessionPath, true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private void Cleanup(DateTimeOffset now)
    {
        var cutoff = now - Retention;
        foreach (var path in Directory.EnumerateFiles(_directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            if (string.Equals(path, _sessionPath, StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0 &&
                    File.GetLastWriteTimeUtc(path) < cutoff.UtcDateTime)
                    TryDelete(path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                _log?.Invoke($"Could not clean context session {Path.GetFileName(path)}.", error);
            }
        }
    }

    private static bool Equivalent(PersistedExportContext left, PersistedExportContext right)
        => JsonSerializer.Serialize(left, JsonOptions) == JsonSerializer.Serialize(right, JsonOptions);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Cleanup is best effort; persistence failures still surface from
            // the atomic write itself.
        }
    }
}
