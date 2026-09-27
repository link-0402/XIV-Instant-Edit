using System.Collections.Immutable;
using Dalamud.Plugin.Services;
using InstantEdit.Services.Skeletons;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace InstantEdit.Services.GameFiles;

/// <summary>A category's model files: still being found, found, or why they couldn't be.</summary>
internal sealed record GameFileCategoryState(GameFileCategory Category, bool Loading, ImmutableArray<GameModelEntry> Entries, string Error);

/// <summary>A model's materials and textures: still being read, read, or why they couldn't be.</summary>
internal sealed record GameFileDependencyState(bool Loading, GameModelDependencies? Dependencies, string Error);

/// <summary>
/// The Game Files browser's data: each category's vanilla model files, found in the background the
/// first time it is shown and kept for the session; item and hairstyle names from the game's sheets;
/// and the materials and textures of the models opened in the browser.
/// </summary>
internal sealed class GameFileBrowserService : IDisposable
{
    private const int MaxCachedDependencies = 512;
    private readonly IDataManager data;
    private readonly IPluginLog log;
    private readonly CancellationTokenSource life = new();
    private readonly object sync = new();
    private readonly Dictionary<GameFileCategory, GameFileCategoryState> categories = [];
    private readonly Dictionary<GameFileCategory, CancellationTokenSource> scans = [];
    private readonly Dictionary<string, GameFileDependencyState> dependencies = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lazy<(GameFileNames Names, IReadOnlyCollection<(ushort, ushort)> WeaponModels)> sheets;
    private int sheetsStarted;
    private int dependencyVersion;

    public GameFileBrowserService(IDataManager data, IPluginLog log)
    {
        this.data = data;
        this.log = log;
        sheets = new(ReadSheets, LazyThreadSafetyMode.ExecutionAndPublication);
        SkeletonFiles = new VanillaSkeletonFiles(Read);
    }

    /// <summary>The skeleton files vanilla models use, from the game's own EST tables.</summary>
    public VanillaSkeletonFiles SkeletonFiles { get; }

    /// <summary>Changes whenever a model's materials finish reading, so views know to rebuild.</summary>
    public int DependencyVersion => Volatile.Read(ref dependencyVersion);

    /// <summary>Item and hairstyle names and player faces; empty until the sheets have been read in the background.</summary>
    public GameFileNames Names
    {
        get
        {
            if (sheets.IsValueCreated)
                return sheets.Value.Names;
            if (Interlocked.Exchange(ref sheetsStarted, 1) == 0)
                _ = Task.Run(() => sheets.Value, life.Token);
            return GameFileNames.Empty;
        }
    }

    /// <summary>The game data's version, as the export manifest records it.</summary>
    public string GameVersion
    {
        get
        {
            try { return data.GameData.Repositories.TryGetValue("ffxiv", out var repository) ? repository.Version : ""; }
            catch (Exception e) when (e is not OutOfMemoryException) { return ""; }
        }
    }

    /// <summary>The game's install folder (the one holding <c>sqpack</c>), which exports stay out of.</summary>
    public string GameFolder
    {
        get
        {
            try { return data.GameData.DataPath.Parent?.FullName ?? data.GameData.DataPath.FullName; }
            catch (Exception e) when (e is not OutOfMemoryException) { return ""; }
        }
    }

    public bool Exists(string gamePath)
    {
        try { return data.FileExists(gamePath); }
        catch (Exception e) when (e is not OutOfMemoryException) { return false; }
    }

    /// <summary>A game file's bytes, or null when the game has none or it can't be read.</summary>
    public byte[]? Read(string gamePath)
    {
        try { return data.GetFile(gamePath)?.Data; }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            log.Debug(e, $"Could not read the game file {gamePath}.");
            return null;
        }
    }

    /// <summary>A category's model files if they have been asked for, without starting a scan.</summary>
    public GameFileCategoryState? TryGetCategory(GameFileCategory category)
    {
        lock (sync) return categories.GetValueOrDefault(category);
    }

    /// <summary>A category's model files; starts finding them the first time it is asked for.</summary>
    public GameFileCategoryState Category(GameFileCategory category)
    {
        lock (sync)
        {
            if (categories.TryGetValue(category, out var state))
                return state;
        }
        Scan(category);
        lock (sync) return categories[category];
    }

    /// <summary>Finds a category's model files again, for example after a game update.</summary>
    public void Rescan(GameFileCategory category) => Scan(category);

    private void Scan(GameFileCategory category)
    {
        var scan = CancellationTokenSource.CreateLinkedTokenSource(life.Token);
        lock (sync)
        {
            if (scans.Remove(category, out var previous))
                previous.Cancel();
            scans[category] = scan;
            var entries = categories.TryGetValue(category, out var current) ? current.Entries : [];
            categories[category] = new GameFileCategoryState(category, true, entries, "");
        }
        _ = Task.Run(() =>
        {
            GameFileCategoryState? result = null;
            try
            {
                var seeds = category == GameFileCategory.Weapon ? sheets.Value.WeaponModels : null;
                var entries = new GameFileCatalogBuilder(Exists, Read).Enumerate(category, seeds, scan.Token);
                result = new GameFileCategoryState(category, false, entries, "");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                log.Warning(e, $"Could not list the game's {category} models.");
                result = new GameFileCategoryState(category, false, [], e.Message);
            }
            // Only the latest scan of a category publishes; a replaced one was cancelled by its replacement.
            lock (sync)
            {
                if (scans.TryGetValue(category, out var owner) && ReferenceEquals(owner, scan))
                {
                    scans.Remove(category);
                    categories[category] = result ?? categories[category] with { Loading = false };
                }
            }
            scan.Dispose();
        });
    }

    /// <summary>A model's materials and textures (its first material variant); starts reading them on first use.</summary>
    public GameFileDependencyState Dependencies(GameModelEntry entry)
    {
        lock (sync)
        {
            if (dependencies.TryGetValue(entry.GamePath, out var known))
                return known;
            if (dependencies.Count >= MaxCachedDependencies)
                dependencies.Clear();
            dependencies[entry.GamePath] = new GameFileDependencyState(true, null, "");
        }
        var token = life.Token;
        _ = Task.Run(() =>
        {
            GameFileDependencyState result;
            try
            {
                var model = Read(entry.GamePath) ?? throw new FileNotFoundException("The game has no file at this path.");
                var variants = GameFileDependencies.MaterialVariants(entry.Id, Read);
                result = new GameFileDependencyState(false, GameFileDependencies.Resolve(entry.GamePath, model, [variants[0]], Exists, Read), "");
            }
            catch (Exception e)
            {
                result = new GameFileDependencyState(false, null, e.Message);
            }
            lock (sync) dependencies[entry.GamePath] = result;
            Interlocked.Increment(ref dependencyVersion);
        }, token);
        return new GameFileDependencyState(true, null, "");
    }

    private (GameFileNames, IReadOnlyCollection<(ushort, ushort)>) ReadSheets()
    {
        try
        {
            var items = data.GetExcelSheet<Item>();
            return (GameFileNameSource.Build(items, data.GetExcelSheet<HairMakeType>(),
                    data.GetExcelSheet<RawRow>(name: "HairMakeType"), data.GetExcelSheet<CharaMakeCustomize>(),
                    data.GetExcelSheet<CharaMakeType>()),
                GameFileNameSource.WeaponModels(items));
        }
        catch (Exception e)
        {
            log.Warning(e, "Could not read item names, hairstyles and faces for the Game Files browser.");
            return (GameFileNames.Empty, []);
        }
    }

    // Scans are linked to the lifetime token, so cancelling it stops them; they dispose themselves.
    public void Dispose() => life.Cancel();
}
