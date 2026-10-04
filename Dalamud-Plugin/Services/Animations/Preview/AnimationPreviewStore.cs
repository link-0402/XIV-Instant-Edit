using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace InstantEdit.Services.Animations.Preview;

/// <summary>
/// The temporary mod a preview registered. It is saved before Penumbra is called, so a crash or a
/// failed unload can still remove the mod and the files it points at on the next load.
/// </summary>
internal sealed record AnimationPreviewRecord
{
    public required string Tag { get; init; }
    public required Guid CollectionId { get; init; }
    public required int Priority { get; init; }
    /// <summary> The session folder: &lt;cache&gt;/animation-preview/&lt;session&gt;. </summary>
    public required string SessionFolder { get; init; }
    public required DateTime CreatedUtc { get; init; }
}

/// <summary> A registered preview revision: its folder and the file each game path resolves to. </summary>
internal sealed record AnimationPreviewRevision(
    int Revision,
    string Folder,
    ImmutableDictionary<string, string> Files,
    Guid CollectionId,
    string CollectionName,
    bool CollectionShared);

/// <summary>
/// Where preview files go and which leftovers may be deleted. Every revision gets its own folder,
/// because the game and Penumbra never re-read a file path they already loaded.
/// </summary>
internal static class AnimationPreviewRules
{
    /// <summary> The top-level cache folder. Keep in the cache's owned-entry lists (TextureFiles, cache.py). </summary>
    public const string CacheFolder = "animation-preview";
    public const int MaxFiles = 64;
    public const int MaxFileBytes = 256 * 1024 * 1024;
    public const long MaxTotalBytes = 512L * 1024 * 1024;
    public const int MaxRevision = 999_999;
    /// <summary> Session folders the record doesn't name are deleted once this old (the cache's stale age). </summary>
    public static readonly TimeSpan StaleSessionAge = TimeSpan.FromHours(24);
    // Penumbra refuses redirect targets of 2048 characters or more.
    private const int MaxFullPath = 2047;
    private static readonly string[] Extensions = [".pap", ".tmb", ".scd", ".avfx", ".atex"];
    private static readonly Regex SessionName = new("^[0-9a-f]{16}$", RegexOptions.CultureInvariant);
    private static readonly Regex RevisionPattern = new("^[1-9][0-9]{0,5}$", RegexOptions.CultureInvariant);

    /// <summary> A lowercase game path with forward slashes, or an <see cref="InvalidDataException"/>. </summary>
    public static string GamePath(string? gamePath)
    {
        var normalized = (gamePath ?? string.Empty).Replace('\\', '/').Trim().TrimStart('/').ToLowerInvariant();
        if (!IsSafeGamePath(normalized))
            throw new InvalidDataException($"The preview file {gamePath} is not a supported game path.");
        return normalized;
    }

    /// <summary>
    /// Plain ASCII game paths of the file kinds an animation edit can replace, nothing that leaves the
    /// folder. Spaces are allowed inside a name (mods add paths such as "emote/pose 1.pap"), but not at
    /// either end of one, nor a trailing dot, which Windows would drop from the file name.
    /// </summary>
    public static bool IsSafeGamePath(string gamePath)
        => gamePath.Length is > 0 and < 512 && !Path.IsPathRooted(gamePath) &&
           gamePath.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '_' or '-' or '.' or ' ') &&
           gamePath.Split('/').All(segment => segment is not ("" or "." or "..") && segment[0] != ' ' && segment[^1] is not (' ' or '.')) &&
           Extensions.Any(extension => gamePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    public static string NewSessionName() => Guid.NewGuid().ToString("N")[..16];

    public static bool IsSessionName(string name) => SessionName.IsMatch(name);

    public static string RevisionName(int revision)
        => revision is >= 1 and <= MaxRevision
            ? revision.ToString(CultureInfo.InvariantCulture)
            : throw new InvalidOperationException("The preview revision is out of range.");

    public static bool TryParseRevision(string name, out int revision)
    {
        revision = 0;
        return RevisionPattern.IsMatch(name) && int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out revision);
    }

    public static string PreviewRoot(string cacheRoot) => Path.Combine(Path.GetFullPath(cacheRoot), CacheFolder);

    public static string SessionFolder(string previewRoot, string session)
        => IsSessionName(session)
            ? Path.Combine(previewRoot, session)
            : throw new InvalidDataException("The preview session name is invalid.");

    public static string RevisionFolder(string sessionFolder, int revision) => Path.Combine(sessionFolder, RevisionName(revision));

    /// <summary> True for a session folder this feature made: a session name directly inside an animation-preview folder. </summary>
    public static bool IsSessionFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return false;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return IsSessionName(Path.GetFileName(full)) &&
               string.Equals(Path.GetFileName(Path.GetDirectoryName(full)), CacheFolder, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The file each game path is written to inside <paramref name="revisionFolder"/>. Game paths
    /// are normalized; duplicates, unsafe paths and paths too long for Penumbra are refused.
    /// </summary>
    public static ImmutableDictionary<string, string> PlanFiles(string revisionFolder, IEnumerable<string> gamePaths)
    {
        var root = Path.GetFullPath(revisionFolder);
        var files = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var raw in gamePaths)
        {
            var gamePath = GamePath(raw);
            var file = Path.GetFullPath(Path.Combine(root, gamePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!PathRules.IsPathWithin(file, root) || PathRules.SamePhysicalPath(file, root))
                throw new InvalidDataException($"The preview file {raw} is outside its folder.");
            if (file.Length > MaxFullPath)
                throw new InvalidDataException("The preview folder path is too long. Choose a shorter cache folder in Settings.");
            if (!files.TryAdd(gamePath, file))
                throw new InvalidDataException($"The preview has {gamePath} twice.");
        }
        if (files.Count is 0 or > MaxFiles)
            throw new InvalidDataException($"A preview needs 1 to {MaxFiles} files.");
        return files.ToImmutable();
    }

    /// <summary> True when one folder is inside the other, such as a cache folder inside Penumbra's mod folder. </summary>
    public static bool Overlaps(string first, string second)
        => PathRules.IsPathWithin(first, second) || PathRules.IsPathWithin(second, first);

    /// <summary> Revision folders of a session other than <paramref name="current"/>; other names are left alone. </summary>
    public static ImmutableArray<string> OlderRevisions(IEnumerable<string> names, int current)
        => names.Where(name => TryParseRevision(name, out var revision) && revision != current)
            .OrderBy(name => name, StringComparer.Ordinal).ToImmutableArray();

    /// <summary>
    /// Session folders other than <paramref name="keep"/> last changed before <paramref name="cutoffUtc"/>;
    /// other names are left alone. The age keeps a second game client's live preview in a shared cache.
    /// </summary>
    public static ImmutableArray<string> StaleSessions(IEnumerable<(string Name, DateTime LastWriteUtc)> sessions, string? keep, DateTime cutoffUtc)
        => sessions.Where(session => IsSessionName(session.Name) && session.LastWriteUtc < cutoffUtc &&
                                     !string.Equals(session.Name, keep, StringComparison.OrdinalIgnoreCase))
            .Select(session => session.Name).OrderBy(name => name, StringComparer.Ordinal).ToImmutableArray();

    /// <summary> A seek time inside the animation: NaN and negatives become 0, later times the duration. </summary>
    public static float ClampTime(float seconds, float duration)
    {
        if (!float.IsFinite(duration) || duration <= 0)
            return 0;
        return float.IsNaN(seconds) ? 0 : Math.Clamp(seconds, 0, duration);
    }

    /// <summary> A playback speed: NaN becomes 1, otherwise 0 (paused) to 4. </summary>
    public static float ClampSpeed(float speed) => float.IsNaN(speed) ? 1 : Math.Clamp(speed, 0, 4);
}

/// <summary>
/// The animation preview's temporary mod, persisted in the plugin's config folder (mirrors
/// PreviewModStore): saved atomically, and an unreadable file is set aside instead of overwritten.
/// </summary>
internal sealed class AnimationPreviewStore
{
    public const string FileName = "AnimationPreview.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path;
    private readonly object _lock = new();
    private AnimationPreviewRecord? _record;

    public AnimationPreviewStore(string configDirectory, string fileName = FileName)
        => _path = Path.Combine(configDirectory, fileName);

    public string LoadError { get; private set; } = string.Empty;

    public AnimationPreviewRecord? Record
    {
        get { lock (_lock) return _record; }
    }

    public void Load()
    {
        lock (_lock)
        {
            LoadError = string.Empty;
            try
            {
                _record = File.Exists(_path) ? Validate(JsonSerializer.Deserialize<AnimationPreviewRecord>(File.ReadAllText(_path), Json)) : null;
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or InvalidDataException)
            {
                var aside = $"{_path}.unreadable-{DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture)}";
                try { File.Move(_path, aside); }
                catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException) { aside = _path; }
                LoadError = $"Could not read the animation preview record ({e.Message}); it was kept as {Path.GetFileName(aside)}.";
                _record = null;
            }
        }
    }

    public void Save(AnimationPreviewRecord record)
    {
        lock (_lock)
        {
            Validate(record);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(record, Json));
            File.Move(temporary, _path, true);
            _record = record;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            if (File.Exists(_path))
                File.Delete(_path);
            _record = null;
        }
    }

    private static AnimationPreviewRecord Validate(AnimationPreviewRecord? record)
        => record is { Tag.Length: > 0, CollectionId: var id } && id != Guid.Empty && AnimationPreviewRules.IsSessionFolder(record.SessionFolder)
            ? record
            : throw new InvalidDataException("The animation preview record is incomplete.");
}
