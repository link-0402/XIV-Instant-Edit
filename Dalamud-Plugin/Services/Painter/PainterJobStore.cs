using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using InstantEdit.Services.Previews;

namespace InstantEdit.Services.Painter;

/// <summary> A model whose meshes a texture set covers, re-read when a shared texture needs its UV coverage. </summary>
/// <param name="Source">Absolute file for modded models; the game path for vanilla ones.</param>
/// <param name="AttributeMasks">The character's enabled attributes for the model when the project was made; empty when unknown.</param>
internal sealed record PainterCoverageModel(string Source, bool Vanilla, IReadOnlyList<int> Meshes, IReadOnlyList<uint>? AttributeMasks = null)
{
    /// <summary> Whether the project drew this submesh: all of its attributes were enabled. </summary>
    public bool Draws(ModelSubmesh submesh) => PainterVisibility.Draws(AttributeMasks, submesh);
}

/// <summary> One texture a Painter project sends back, and the session that applies it. </summary>
internal sealed record PainterTarget
{
    public string Key { get; init; } = "";
    public string TextureSet { get; init; } = "";
    public string GamePath { get; init; } = "";
    public Guid SessionId { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    /// <summary> Size of Painter's file: the texture is its top-left corner, all of it unless the set was made square. 0 for older projects. </summary>
    public int ExportWidth { get; init; }
    public int ExportHeight { get; init; }
    /// <summary> Pixel hash of Painter's first, untouched export; a send that matches it means "the original". </summary>
    public string BaselineHash { get; set; } = "";
    /// <summary> Other meshes use this texture too, so only this project's UV area is taken from Painter. </summary>
    public bool Protect { get; init; }
    public List<PainterCoverageModel> Coverage { get; init; } = [];
}

/// <summary> A Substance Painter project opened from On Screen. Kept in the config folder, not the cache. </summary>
internal sealed record PainterJob
{
    public Guid Id { get; init; }
    public string Capability { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string ActorName { get; init; } = "";
    public string ModelGamePath { get; init; } = "";
    public string NewModName { get; init; } = "";
    public DateTimeOffset Created { get; init; }
    public List<PainterTarget> Targets { get; init; } = [];
    public string State { get; set; } = PainterJobState.Sent;
    public string Message { get; set; } = "";
    public string ProjectPath { get; set; } = "";
    public DateTimeOffset? LastSent { get; set; }
}

internal static class PainterJobState
{
    public const string Sent = "sent";
    public const string Ready = "ready";
    public const string Failed = "failed";
}

/// <summary> Durable list of Painter projects, rewritten atomically on each change. </summary>
internal sealed class PainterJobStore
{
    private const string Schema = "instant-edit.painter-jobs";
    private const int Version = 1;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;
    private readonly object _lock = new();
    private List<PainterJob> _jobs = [];

    private sealed record Document(
        [property: JsonPropertyName("schema")] string Schema,
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("jobs")] List<PainterJob> Jobs);

    public PainterJobStore(string configDirectory)
    {
        _path = Path.Combine(configDirectory, "PainterJobs.json");
    }

    public string LoadError { get; private set; } = "";

    public void Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_path))
                return;
            try
            {
                var document = JsonSerializer.Deserialize<Document>(File.ReadAllBytes(_path), Options);
                if (document is null || document.Schema != Schema || document.Version != Version)
                    throw new InvalidDataException("Unsupported Painter project list.");
                _jobs = document.Jobs.Where(job => job.Id != Guid.Empty && job.Capability.Length > 0).ToList();
            }
            catch (Exception error) when (error is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
            {
                LoadError = "Painter projects could not be loaded; the file was kept. " + error.Message;
                _jobs = [];
            }
        }
    }

    public IReadOnlyList<PainterJob> Jobs
    {
        get { lock (_lock) return _jobs.ToArray(); }
    }

    public PainterJob? Find(Guid id)
    {
        lock (_lock) return _jobs.FirstOrDefault(job => job.Id == id);
    }

    /// <summary> The job, when the caller holds its capability. </summary>
    public PainterJob? Authorize(string jobId, string capability)
    {
        if (!Guid.TryParseExact(jobId, "N", out var id) || string.IsNullOrEmpty(capability))
            return null;
        var job = Find(id);
        if (job is null)
            return null;
        var expected = System.Text.Encoding.UTF8.GetBytes(job.Capability);
        var actual = System.Text.Encoding.UTF8.GetBytes(capability);
        return CryptographicOperations.FixedTimeEquals(expected, actual) ? job : null;
    }

    public bool LinksSession(Guid sessionId)
    {
        lock (_lock) return _jobs.Any(job => job.Targets.Any(target => target.SessionId == sessionId));
    }

    public void Save(PainterJob job)
    {
        lock (_lock)
        {
            _jobs.RemoveAll(existing => existing.Id == job.Id);
            _jobs.Add(job);
            Write();
        }
    }

    /// <summary> Records a change made to a job returned by this store. </summary>
    public void Update(Action change)
    {
        lock (_lock)
        {
            change();
            Write();
        }
    }

    public bool Remove(Guid id)
    {
        lock (_lock)
        {
            var removed = _jobs.RemoveAll(job => job.Id == id) > 0;
            if (removed)
                Write();
            return removed;
        }
    }

    public static string NewCapability() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private void Write()
    {
        if (LoadError.Length > 0)
            throw new IOException(LoadError);
        TextureFiles.WriteJson(_path, new Document(Schema, Version, _jobs));
    }
}
