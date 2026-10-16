using InstantEdit.Models;
using InstantEdit.Services.PreviewMods;
using InstantEdit.Ui;

namespace InstantEdit.Services.Materials;

/// <summary>
/// Where an open material came from: the path the game asks for, the file that answers it (a mod
/// file or game data), the object it is drawn on, and the hash of the bytes the editor read.
/// </summary>
internal sealed record MaterialSource(
    string GamePath,
    string ActualPath,
    ResourceSourceState State,
    string ModName,
    string ModDirectory,
    string ModRootPath,
    string RelativePath,
    int ObjectIndex,
    string Sha256)
{
    public Guid? ModStableId { get; init; }

    /// <summary>The actor's name, for the names of new mods.</summary>
    public string ActorName { get; init; } = string.Empty;

    public bool IsGameData => State == ResourceSourceState.GameData;

    public string FileName => GamePath[(GamePath.LastIndexOf('/') + 1)..];

    /// <summary>A row of the resource browser and the actor it belongs to (Game Files rows use the import object).</summary>
    public static MaterialSource FromRow(ResourceView row, ActorView actor, byte[] bytes) => new(
        PathRules.NormalizeGamePath(row.GamePath),
        row.ActualPath,
        row.SourceState,
        row.SourceState == ResourceSourceState.GameData ? string.Empty : row.SourceModName,
        row.SourceState == ResourceSourceState.GameData ? string.Empty : row.SourceModDirectory,
        row.SourceState == ResourceSourceState.GameData ? string.Empty : row.SourceModRootPath,
        row.SourceState == ResourceSourceState.LoadedMod ? row.SourceRelativePath.Replace('\\', '/') : string.Empty,
        actor.Entity?.ObjectIndex ?? actor.ImportObjectIndex,
        PreviewSource.Hash(bytes))
    {
        ModStableId = row.SourceModStableId,
        ActorName = actor.Entity?.Name ?? actor.Name,
    };

    /// <summary>The same material as the file a save put into the new mod <paramref name="modDirectory"/> (at Files/&lt;game path&gt;).</summary>
    public MaterialSource SavedInto(string modRoot, string modDirectory, string sha256)
    {
        var folder = Path.Combine(modRoot, modDirectory);
        var relative = "Files/" + GamePath;
        return this with
        {
            ActualPath = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar)),
            State = ResourceSourceState.LoadedMod,
            ModName = modDirectory,
            ModDirectory = modDirectory,
            ModRootPath = folder,
            RelativePath = relative,
            ModStableId = null,
            Sha256 = sha256,
        };
    }

    public PreviewSource ToPreviewSource() => new()
    {
        GamePath = GamePath,
        ActualPath = ActualPath,
        State = State,
        ModName = ModName,
        ModDirectory = ModDirectory,
        ModRootPath = ModRootPath,
        RelativePath = RelativePath,
        ModStableId = ModStableId,
        Sha256 = Sha256,
    };
}
