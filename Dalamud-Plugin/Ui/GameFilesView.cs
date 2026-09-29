using System.Collections.Immutable;
using InstantEdit.Services.GameFiles;
using InstantEdit.Services.Skeletons;

namespace InstantEdit.Ui;

/// <summary>
/// Which race codes the Game Files table shows. Player leaves out the faces only NPCs wear, which NPC
/// then includes. Weapons have no race and always show.
/// </summary>
internal enum GameRaceMode { Player, All, Npc, One }

/// <summary>
/// The Game Files table's filter: search words over paths, IDs, item names and race labels, the
/// race codes, a slot, player-selectable hairstyles and named models. It is recomputed only when
/// the model list, the names or a setting changes.
/// </summary>
internal sealed class GameFileFilter
{
    private ImmutableArray<GameModelEntry> source;
    private GameFileNames? sourceNames;
    private string[] searchText = [];
    private (string Text, GameRaceMode Races, ushort Race, string Slot, bool Selectable, bool Named)? settings;
    private IReadOnlyList<GameModelEntry> result = [];

    public string Text { get; set; } = "";
    public GameRaceMode Races { get; set; } = GameRaceMode.Player;
    public ushort Race { get; set; }

    /// <summary>A slot suffix such as <c>top</c>, or empty for every slot.</summary>
    public string Slot { get; set; } = "";
    public bool SelectableOnly { get; set; }
    public bool NamedOnly { get; set; }

    /// <summary>Changes whenever the filtered list does.</summary>
    public int Version { get; private set; }

    public IReadOnlyList<GameModelEntry> Apply(ImmutableArray<GameModelEntry> entries, GameFileNames names)
    {
        var current = (UiText.Safe(Text).Trim(), Races, Race, Slot, SelectableOnly, NamedOnly);
        var sourceChanged = entries != source || !ReferenceEquals(names, sourceNames);
        if (!sourceChanged && settings == current)
            return result;
        if (sourceChanged)
        {
            source = entries;
            sourceNames = names;
            searchText = entries.IsDefault ? [] : [.. entries.Select(entry => SearchText(entry, names))];
        }
        settings = current;
        var words = current.Item1.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var filtered = new List<GameModelEntry>();
        for (var index = 0; index < searchText.Length; index++)
        {
            var entry = entries[index];
            if (Admits(entry, names) && words.All(word => searchText[index].Contains(word, StringComparison.OrdinalIgnoreCase)))
                filtered.Add(entry);
        }
        result = filtered;
        Version++;
        return result;
    }

    private bool Admits(GameModelEntry entry, GameFileNames names)
    {
        var id = entry.Id;
        if (id.Human)
        {
            // NPC-only faces in the player race folders count as NPC models.
            var player = names.PlayerModel(id);
            var admitted = Races switch
            {
                GameRaceMode.Player => player,
                GameRaceMode.Npc => !player,
                GameRaceMode.One => id.Race == Race,
                _ => true,
            };
            if (!admitted)
                return false;
        }
        if (Slot.Length > 0 && id.Slot != Slot)
            return false;
        if (SelectableOnly && id.Category == GameFileCategory.Hair && names.PlayerSelectable(id) != true)
            return false;
        return !NamedOnly || names.For(id).Length > 0;
    }

    /// <summary>What the search looks through for a model: its path, ID, item names and race label.</summary>
    public static string SearchText(GameModelEntry entry, GameFileNames names)
    {
        var id = entry.Id;
        var race = id.Human ? $"c{id.Race:D4} {GameRaces.Label(id.Race)}" : "";
        return $"{entry.GamePath}\n{id.IdLabel} {GameModelPaths.SlotLabel(id.Slot)} {race}\n{string.Join('\n', names.For(id))}";
    }
}

/// <summary>The models ticked for export. It spans categories and survives filtering; shift-clicks tick ranges of the visible rows.</summary>
internal sealed class GameFileSelection
{
    private readonly Dictionary<string, GameModelEntry> selected = new(StringComparer.OrdinalIgnoreCase);
    private string? anchor;

    public int Count => selected.Count;

    public bool Contains(GameModelEntry entry) => selected.ContainsKey(entry.GamePath);

    public void Set(GameModelEntry entry, bool value)
    {
        Apply(entry, value);
        anchor = entry.GamePath;
    }

    /// <summary>Sets every visible model from the last one clicked to this one; just this one without a usable anchor.</summary>
    public void SetRange(IReadOnlyList<GameModelEntry> visible, GameModelEntry entry, bool value)
    {
        var end = IndexOf(visible, entry.GamePath);
        var start = anchor is null ? -1 : IndexOf(visible, anchor);
        if (start < 0 || end < 0)
        {
            Set(entry, value);
            return;
        }
        for (var index = Math.Min(start, end); index <= Math.Max(start, end); index++)
            Apply(visible[index], value);
        anchor = entry.GamePath;
    }

    public void SetAll(IEnumerable<GameModelEntry> entries, bool value)
    {
        foreach (var entry in entries)
            Apply(entry, value);
    }

    public void Invert(IEnumerable<GameModelEntry> entries)
    {
        foreach (var entry in entries)
            Apply(entry, !Contains(entry));
    }

    public void Clear()
    {
        selected.Clear();
        anchor = null;
    }

    /// <summary>How many of <paramref name="entries"/> are ticked.</summary>
    public int CountIn(IEnumerable<GameModelEntry> entries) => entries.Count(Contains);

    /// <summary>The ticked models, by category, ID, slot and race.</summary>
    public IReadOnlyList<GameModelEntry> Entries
        => [.. selected.Values.OrderBy(entry => entry.Id.Category).ThenBy(entry => entry.Id.Id).ThenBy(entry => entry.Id.SecondaryId)
            .ThenBy(entry => GameModelPaths.Slots(entry.Id.Category).IndexOf(entry.Id.Slot)).ThenBy(entry => entry.Id.Race)];

    private void Apply(GameModelEntry entry, bool value)
    {
        if (value)
            selected[entry.GamePath] = entry;
        else
            selected.Remove(entry.GamePath);
    }

    private static int IndexOf(IReadOnlyList<GameModelEntry> entries, string path)
    {
        for (var index = 0; index < entries.Count; index++)
            if (string.Equals(entries[index].GamePath, path, StringComparison.OrdinalIgnoreCase))
                return index;
        return -1;
    }
}

internal enum GameFileRowKind { Model, Material, Texture, Skeleton, Note }

/// <summary>
/// One line of the Game Files table: a model, or below an opened model one of its materials, a
/// material's texture, its skeleton files or a note. <paramref name="GamePath"/> is empty for
/// lines that aren't files; <paramref name="Found"/> says whether the game has the file.
/// </summary>
internal sealed record GameFileRow(GameFileRowKind Kind, GameModelEntry Entry, string GamePath, string Label, string Detail, bool Found);

/// <summary>
/// The Game Files table's lines: the filtered models, each opened one followed by its materials
/// (each followed by its textures), its skeleton files and notes. Rebuilt only when the models,
/// the opened set or a model's dependencies change.
/// </summary>
internal sealed class GameFileRowList
{
    private IReadOnlyList<GameModelEntry>? models;
    private int openedVersion = -1;
    private int dependencyVersion = -1;
    private IReadOnlyList<GameFileRow> rows = [];
    private readonly HashSet<string> opened = new(StringComparer.OrdinalIgnoreCase);
    private int version;

    public bool IsOpen(GameModelEntry entry) => opened.Contains(entry.GamePath);

    public void Toggle(GameModelEntry entry)
    {
        if (!opened.Remove(entry.GamePath))
            opened.Add(entry.GamePath);
        version++;
    }

    public IReadOnlyList<GameFileRow> Build(IReadOnlyList<GameModelEntry> filtered, int dependencies,
        Func<GameModelEntry, GameFileDependencyState> dependenciesOf, Func<GameModelEntry, SkeletonFileSet?> skeletonOf)
    {
        if (ReferenceEquals(filtered, models) && openedVersion == version && (opened.Count == 0 || dependencyVersion == dependencies))
            return rows;
        models = filtered;
        openedVersion = version;
        dependencyVersion = dependencies;
        var lines = new List<GameFileRow>(filtered.Count + opened.Count * 8);
        foreach (var entry in filtered)
        {
            lines.Add(new GameFileRow(GameFileRowKind.Model, entry, entry.GamePath, FileName(entry.GamePath), "", true));
            if (opened.Contains(entry.GamePath))
                AddDetails(lines, entry, dependenciesOf(entry), skeletonOf(entry));
        }
        rows = lines;
        return rows;
    }

    private static void AddDetails(List<GameFileRow> lines, GameModelEntry entry, GameFileDependencyState state, SkeletonFileSet? skeleton)
    {
        if (state.Loading)
            lines.Add(new GameFileRow(GameFileRowKind.Note, entry, "", "Reading the model's materials", "", true));
        else if (state.Dependencies is not { } dependencies)
            lines.Add(new GameFileRow(GameFileRowKind.Note, entry, "", "The materials could not be read", state.Error, false));
        else
        {
            foreach (var material in dependencies.Materials)
            {
                lines.Add(new GameFileRow(GameFileRowKind.Material, entry, material.GamePath, FileName(material.GamePath),
                    material.Found ? material.Problem : "Not in the game data", material.Found && material.Problem.Length == 0));
                foreach (var texture in material.Textures)
                    lines.Add(new GameFileRow(GameFileRowKind.Texture, entry, texture.GamePath, FileName(texture.GamePath),
                        texture.Found ? texture.Usage : "Not in the game data", texture.Found));
            }
            foreach (var warning in dependencies.Warnings)
                lines.Add(new GameFileRow(GameFileRowKind.Note, entry, "", warning, "", false));
        }
        if (skeleton is not null)
        {
            var files = string.Join(" + ", skeleton.Files.Select(FileName));
            var est = skeleton.Extra is { } extra && skeleton.Entry > 0 ? $" (EST {extra.Slot.ToString().ToLowerInvariant()} {extra.Set} → {skeleton.Entry})" : "";
            lines.Add(new GameFileRow(GameFileRowKind.Skeleton, entry, skeleton.Files[^1], "Skeleton: " + files + est,
                string.Join(" ", skeleton.Warnings), true));
        }
    }

    private static string FileName(string path) => path[(path.LastIndexOf('/') + 1)..];
}
