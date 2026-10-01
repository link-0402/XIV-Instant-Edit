using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using InstantEdit.Models;

namespace InstantEdit.Services.PreviewMods;

/// <summary> Where one file a preview mod replaces came from, with its hash when it was read. </summary>
internal record PreviewSource
{
    /// <summary> The path the game requested. </summary>
    public required string GamePath { get; init; }
    /// <summary> The resolved file: a full path for mod files, a game path for game data. </summary>
    public required string ActualPath { get; init; }
    public required ResourceSourceState State { get; init; }
    public string ModName { get; init; } = string.Empty;
    public string ModDirectory { get; init; } = string.Empty;
    public string ModRootPath { get; init; } = string.Empty;
    public string RelativePath { get; init; } = string.Empty;
    public Guid? ModStableId { get; init; }
    public required string Sha256 { get; init; }

    /// <summary> A file inside a loaded Penumbra mod, which a preview can be written back to. </summary>
    [JsonIgnore]
    public bool IsModFile => State == ResourceSourceState.LoadedMod && ModDirectory.Length > 0 && RelativePath.Length > 0 && Path.IsPathRooted(ActualPath);

    /// <summary> "Mod name: relative path", or the game path for game data. </summary>
    [JsonIgnore]
    public string Label => IsModFile ? $"{ModName}: {RelativePath}" : $"Game data: {GamePath}";

    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    /// <summary> The source of a resource tree node whose file hashed as <paramref name="sha256"/>. </summary>
    public static PreviewSource Of(ResourceNode node, string sha256) => new()
    {
        GamePath = PathRules.NormalizeGamePath(node.GamePath),
        ActualPath = node.ActualPath,
        State = node.SourceState,
        ModName = node.SourceModName ?? string.Empty,
        ModDirectory = node.SourceModDirectory ?? string.Empty,
        ModRootPath = node.SourceModRootPath ?? string.Empty,
        RelativePath = node.SourceState == ResourceSourceState.LoadedMod ? (node.SourceRelativePath ?? string.Empty).Replace('\\', '/') : string.Empty,
        ModStableId = node.SourceModStableId,
        Sha256 = sha256,
    };
}

/// <summary> One file of a preview mod: what it replaces, where it sits in the mod, and where it came from. </summary>
internal sealed record PreviewModFile
{
    public required string Kind { get; init; }
    /// <summary> The path the game requests for the original. </summary>
    public required string GamePath { get; init; }
    /// <summary> Every game path a new mod maps to the file when its source is game data; just <see cref="GamePath"/> when empty. </summary>
    public List<string> GamePaths { get; init; } = [];
    public required string PreviewRelativePath { get; init; }
    public required string PreviewSha256 { get; init; }
    public required PreviewSource Source { get; init; }
    /// <summary> For a material: the preview's texture paths and the stored paths they replace. </summary>
    public Dictionary<string, string> TextureRewrites { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary> What else uses the source file, shown before applying (options of its mod that map it). </summary>
    public string Note { get; init; } = string.Empty;
    /// <summary>
    /// A new file rather than a changed one: <see cref="Source"/> is the material that reads it at
    /// <see cref="GamePath"/>. Applying adds it to that material's mod, mapped wherever the material
    /// is, or puts it in the new mod with the game files when the material is game data.
    /// </summary>
    public bool NewFile { get; init; }

    [JsonIgnore]
    public IReadOnlyList<string> FixGamePaths => GamePaths.Count > 0 ? GamePaths : [GamePath];
}

/// <summary> What a preview store needs to know about the previews it keeps. </summary>
internal interface IPreviewModRecord
{
    Guid Id { get; }
    string ModDirectory { get; }
    string ActorName { get; }
}

/// <summary> A preview mod the plugin created and has not applied or discarded yet. </summary>
internal sealed record PreviewMod : IPreviewModRecord
{
    public required Guid Id { get; init; }
    public required string ModDirectory { get; init; }
    public required Guid ModIdentifier { get; init; }
    public required Guid CollectionId { get; init; }
    public required string CollectionName { get; init; }
    public required string ActorName { get; init; }
    public required ushort ObjectIndex { get; init; }
    public required DateTimeOffset Created { get; init; }
    public required List<PreviewModFile> Files { get; init; }
    public List<string> Changes { get; init; } = [];
}

/// <summary> A file for a new mod: its bytes at a relative path, mapped from one or more game paths. </summary>
internal sealed record PreviewModEntry(string RelativePath, byte[] Bytes, IReadOnlyList<string> GamePaths)
{
    /// <summary> A file mapped from one game path, stored at Files/&lt;game path&gt;. </summary>
    public static PreviewModEntry At(string gamePath, byte[] bytes)
    {
        var normalized = PathRules.NormalizeGamePath(gamePath);
        return new PreviewModEntry("Files/" + normalized, bytes, [normalized]);
    }
}

/// <summary> A mod a preview created: where it is and the collection it was enabled in. </summary>
internal sealed record PreviewModResult(string ModDirectory, Guid Identifier, Guid CollectionId, string CollectionName, IReadOnlyList<string> Warnings);

/// <summary> Knows which mod folders hold its feature's previews, so other features leave their files alone. </summary>
internal interface IPreviewModRegistry
{
    bool HoldsMod(string modDirectory);
}

/// <summary> Previews persisted in the plugin's config folder, so a reload can still apply or discard them. </summary>
internal class PreviewModStore<TPreview> : IPreviewModRegistry where TPreview : class, IPreviewModRecord
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private readonly string _path;
    private readonly string _description;
    private readonly object _lock = new();
    private List<TPreview> _previews = [];

    /// <param name="description">What the store holds, for the load error ("neck seam previews").</param>
    public PreviewModStore(string configDirectory, string fileName, string description)
    {
        _path = Path.Combine(configDirectory, fileName);
        _description = description;
    }

    public string LoadError { get; private set; } = string.Empty;

    public IReadOnlyList<TPreview> Previews
    {
        get { lock (_lock) return _previews.ToArray(); }
    }

    public TPreview? PreviewFor(string actorName)
        => Previews.LastOrDefault(p => string.Equals(p.ActorName, actorName, StringComparison.Ordinal));

    public bool HoldsMod(string modDirectory)
        => Previews.Any(p => string.Equals(p.ModDirectory, modDirectory, StringComparison.OrdinalIgnoreCase));

    /// <summary> Reads the saved previews. An unreadable file is set aside, so the next save doesn't write over the previews it lists. </summary>
    public void Load()
    {
        lock (_lock)
        {
            LoadError = string.Empty;
            try
            {
                _previews = File.Exists(_path) ? JsonSerializer.Deserialize<List<TPreview>>(File.ReadAllText(_path), Json) ?? [] : [];
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            {
                var aside = $"{_path}.unreadable-{DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss", System.Globalization.CultureInfo.InvariantCulture)}";
                try { File.Move(_path, aside); }
                catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException) { aside = _path; }
                LoadError = $"Could not read the {_description} ({e.Message}); the file was kept as {Path.GetFileName(aside)}. " +
                            "Their preview mods are still in Penumbra; apply or delete them there.";
                _previews = [];
            }
        }
    }

    public void Add(TPreview preview)
    {
        lock (_lock)
        {
            _previews.RemoveAll(p => p.Id == preview.Id);
            _previews.Add(preview);
            Save();
        }
    }

    public void Remove(Guid id)
    {
        lock (_lock)
        {
            _previews.RemoveAll(p => p.Id == id);
            Save();
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_previews, Json));
        File.Move(temporary, _path, true);
    }
}
