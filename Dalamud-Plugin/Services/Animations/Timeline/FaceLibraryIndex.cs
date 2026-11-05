using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dalamud.Plugin.Services;

namespace InstantEdit.Services.Animations.Timeline;

/// <summary>A face or lip shape an installed expression library holds, by motion name.</summary>
internal sealed record FaceLibraryMotion(string Name, bool Lip);

/// <summary>
/// An expression library (TMPP) a mod adds for one race's face: the game path it maps
/// (chara/human/{Race}/animation/{Face}/nonresident/{Pack}.pap), the file on disk, the mod and the
/// motions in it.
/// </summary>
internal sealed record FaceLibrary(string Pack, string Race, string Face, string GamePath, string File,
    string ModDirectory, string ModName, ImmutableArray<FaceLibraryMotion> Motions);

internal enum FaceLibraryState : byte { NotLoaded, Loading, Building, Ready, Failed }

/// <summary>
/// Where the expression set list stands, for Settings and the picker. Missing and changed count the
/// list's files that were removed or rewritten since it was built; new mods are only found by a rebuild.
/// </summary>
internal sealed record FaceLibraryStatus(FaceLibraryState State, int Libraries = 0, int Mods = 0, int Missing = 0, int Changed = 0,
    DateTime? BuiltUtc = null, int Progress = 0, int Total = 0, string? Error = null);

/// <summary>
/// The expression libraries the installed Penumbra mods add, like the skeleton library: built once
/// from every mod, saved in the cache folder, and only built again with <see cref="RebuildAsync"/>.
/// Libraries the game has itself are left out: the catalog lists those, and a mod replacing one plays
/// in its place anyway. Which ones the player can use depends on the collection, so that is asked
/// each time (<see cref="Active"/>).
/// </summary>
internal sealed class FaceLibraryIndex(Func<Task<(string Directory, string Name, string Root)[]>> mods, Func<string, bool> gameHas,
    Func<string> cacheRoot, IPluginLog log)
{
    private const int Version = 1;
    private const int MaxLibraries = 8192;
    private const int MaxMotions = 1024;
    private const long MaxPapBytes = 64L * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true };

    private sealed record CachedLibrary(FaceLibrary Library, long Length, DateTime Write);
    private sealed record CachedList(int Version, DateTime BuiltUtc, ImmutableArray<CachedLibrary> Libraries);

    internal static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);

    private readonly object sync = new();
    private Task? work;
    // The failed build Start last saw, and when it may be tried again.
    private Task? seenFailure;
    private DateTime retryUtc;
    private ImmutableArray<FaceLibrary> libraries = [];
    private int revision;
    private volatile FaceLibraryStatus status = new(FaceLibraryState.NotLoaded);

    public FaceLibraryStatus Status => status;

    /// <summary>Moves on whenever <see cref="Libraries"/> changes.</summary>
    public int Revision => Volatile.Read(ref revision);

    public ImmutableArray<FaceLibrary> Libraries { get { lock (sync) return libraries; } }

    private string ListPath() => Path.Combine(cacheRoot(), "face-libraries", "face-libraries.json");

    /// <summary>When the saved list was written, or null when none is saved in the cache folder.</summary>
    public DateTime? SavedUtc()
    {
        try
        {
            var path = ListPath();
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Loads the saved list, or builds and saves it when none is saved or it can't be read. Cheap to
    /// call again: a list loaded or being loaded is kept. The picker calls it every frame, so a build
    /// that failed is tried again only after <see cref="RetryDelay"/> (Settings' Rebuild doesn't wait).
    /// </summary>
    public void Start(CancellationToken token, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        lock (sync)
        {
            if (work is { IsFaulted: true } failed)
            {
                if (!ReferenceEquals(failed, seenFailure)) (seenFailure, retryUtc) = (failed, now + RetryDelay);
                if (now < retryUtc) return;
                work = null;
            }
            if (work is { IsCanceled: true }) work = null;
            work ??= LoadOrBuildAsync(token);
        }
    }

    /// <summary>
    /// Builds the list again from every installed mod and saves it, after installing, updating or
    /// removing expression mods. The current list stays in use until the new one is done. While a load
    /// or build runs, returns that instead.
    /// </summary>
    public Task RebuildAsync(CancellationToken token)
    {
        lock (sync)
        {
            if (work is { IsCompleted: false } running) return running;
            return work = BuildAsync(token);
        }
    }

    private async Task LoadOrBuildAsync(CancellationToken token)
    {
        status = new(FaceLibraryState.Loading);
        try
        {
            var path = ListPath();
            if (File.Exists(path) && new FileInfo(path).Length <= 64 * 1024 * 1024)
            {
                var json = await File.ReadAllTextAsync(path, token);
                if (TryLoad(json, FileState, out var loaded, out var missing, out var changed, out var built))
                {
                    Publish(loaded);
                    status = new(FaceLibraryState.Ready, loaded.Length, ModCount(loaded), missing, changed, built);
                    return;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            log.Debug(e, "Could not load the saved expression sets.");
        }
        // Nothing saved yet, or the saved file is unreadable: build it this once.
        await BuildAsync(token);
    }

    private async Task BuildAsync(CancellationToken token)
    {
        var previous = status;
        try
        {
            status = new(FaceLibraryState.Building);
            var roots = await mods();
            var progress = 0;
            status = new(FaceLibraryState.Building, Total: roots.Length);
            var found = await Task.Run(() => Scan(roots, gameHas, ReadPap, token, () => status = status with { Progress = ++progress }), token);
            var built = DateTime.UtcNow;
            var saveError = Save(found, built);
            Publish(found);
            status = new(FaceLibraryState.Ready, found.Length, ModCount(found), BuiltUtc: built, Error: saveError);
            log.Debug($"Built the expression set list with {found.Length} sets from {roots.Length} mods.");
        }
        catch (OperationCanceledException)
        {
            status = previous;
            throw;
        }
        catch (Exception e)
        {
            log.Warning(e, "Could not build the expression set list.");
            status = previous with { State = FaceLibraryState.Failed, Error = e.Message, Progress = 0, Total = 0 };
            throw;
        }
    }

    private void Publish(ImmutableArray<FaceLibrary> next)
    {
        lock (sync) libraries = next;
        Interlocked.Increment(ref revision);
    }

    /// <summary>
    /// After a save wrote or put back an Instant Edit library's file: the list and its saved copy take what
    /// <paramref name="modRoot"/> maps at <paramref name="gamePath"/> now, as each save writes a fresh path and
    /// <see cref="Active"/> admits a library only when the collection resolves to that file. No other mod is read. Waits
    /// for a load or build that is running; changes nothing while no list is loaded (a build reads the library anyway).
    /// </summary>
    public async Task UpdateAsync(string modDirectory, string modName, string modRoot, string gamePath)
    {
        Task? running;
        lock (sync) running = work;
        if (running != null)
            try { await running.ConfigureAwait(false); }
            catch (Exception e) when (e is not OutOfMemoryException) { }
        if (!TryParse(gamePath, out var race, out var face, out var pack)) throw new InvalidDataException($"{gamePath} is not an expression library.");
        var path = gamePath.Replace('\\', '/').ToLowerInvariant();
        FaceLibrary? library = null;
        foreach (var (mapped, file) in Mappings(modRoot, CancellationToken.None))
            if (string.Equals(mapped.Replace('\\', '/'), path, StringComparison.OrdinalIgnoreCase) && Motions(ReadPap, file) is { IsEmpty: false } motions)
            {
                library = new(pack, race, face, path, file, modDirectory, modName.Length > 0 ? modName : modDirectory, motions);
                break;
            }
        ImmutableArray<FaceLibrary> next;
        lock (sync)
        {
            if (status.State != FaceLibraryState.Ready) return;
            libraries = next = Upserted(libraries, modDirectory, path, library);
        }
        Interlocked.Increment(ref revision);
        var saveError = Save(next, status.BuiltUtc ?? DateTime.UtcNow);
        status = status with { Libraries = next.Length, Mods = ModCount(next), Error = saveError };
    }

    /// <summary>
    /// <paramref name="list"/> with the library <paramref name="modDirectory"/> maps at <paramref name="gamePath"/> replaced by
    /// <paramref name="library"/>, or taken out when it is null (the mod maps no faces there now).
    /// </summary>
    internal static ImmutableArray<FaceLibrary> Upserted(ImmutableArray<FaceLibrary> list, string modDirectory, string gamePath, FaceLibrary? library)
    {
        var kept = list.Where(l => !(string.Equals(l.ModDirectory, modDirectory, StringComparison.OrdinalIgnoreCase) &&
                                     string.Equals(l.GamePath, gamePath, StringComparison.OrdinalIgnoreCase)));
        return library == null ? [.. kept] : [.. kept, library];
    }

    private static int ModCount(ImmutableArray<FaceLibrary> list) => list.Select(l => l.ModDirectory).Distinct(StringComparer.OrdinalIgnoreCase).Count();

    /// <summary>Saves the list to the cache folder. Returns why it could not be saved, or null.</summary>
    private string? Save(ImmutableArray<FaceLibrary> list, DateTime built)
    {
        try
        {
            var path = ListPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, Serialize(list, built, FileState));
            File.Move(temporary, path, true);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            log.Warning(e, "Could not save the expression set list to the cache folder.");
            return "It could not be saved to the cache folder, so it is built again next session: " + e.Message;
        }
    }

    private static byte[]? ReadPap(string file)
    {
        var info = new FileInfo(file);
        return info.Exists && info.Length <= MaxPapBytes ? File.ReadAllBytes(file) : null;
    }

    private static (bool Exists, long Length, DateTime Write) FileState(string file)
    {
        var info = new FileInfo(file);
        return info.Exists ? (true, info.Length, info.LastWriteTimeUtc) : (false, 0, DateTime.MinValue);
    }

    /// <summary>
    /// The race code, face folder and library name of a mod's game path when it is an expression
    /// library (chara/human/c0801/animation/f0002/nonresident/emot/laugh.pap); false for anything else.
    /// </summary>
    internal static bool TryParse(string gamePath, out string race, out string face, out string pack)
    {
        race = face = pack = "";
        var parts = gamePath.Replace('\\', '/').ToLowerInvariant().Split('/');
        if (parts.Length < 7 || parts[0] != "chara" || parts[1] != "human" || parts[3] != "animation" || parts[5] != "nonresident" ||
            !IsCode(parts[2], 'c') || !IsCode(parts[4], 'f') || !parts[^1].EndsWith(".pap", StringComparison.Ordinal))
            return false;
        var name = string.Join('/', parts[6..])[..^4];
        if (!TmbFile.IsSafePackName(name)) return false;
        (race, face, pack) = (parts[2], parts[4], name);
        return true;

        static bool IsCode(string part, char letter) => part.Length == 5 && part[0] == letter && part[1..].All(char.IsAsciiDigit);
    }

    /// <summary>
    /// The expression libraries each mod maps: every option's, once per game path and file. A library the
    /// game has itself (<paramref name="gameHas"/>) or without faces is left out. Pure apart from reading
    /// the mods' settings and <paramref name="readPap"/>; <paramref name="done"/> is called after each mod.
    /// </summary>
    internal static ImmutableArray<FaceLibrary> Scan(IEnumerable<(string Directory, string Name, string Root)> mods, Func<string, bool> gameHas,
        Func<string, byte[]?> readPap, CancellationToken token, Action? done = null)
    {
        var result = ImmutableArray.CreateBuilder<FaceLibrary>();
        foreach (var (directory, name, root) in mods)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var seen = new HashSet<(string GamePath, string File)>();
                foreach (var (gamePath, file) in Mappings(root, token))
                {
                    if (result.Count >= MaxLibraries) break;
                    if (!TryParse(gamePath, out var race, out var face, out var pack) || !seen.Add((gamePath.ToLowerInvariant(), file.ToLowerInvariant())) ||
                        gameHas(gamePath.ToLowerInvariant()))
                        continue;
                    if (Motions(readPap, file) is { IsEmpty: false } motions)
                        result.Add(new(pack, race, face, gamePath.ToLowerInvariant(), file, directory, name.Length > 0 ? name : directory, motions));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
            done?.Invoke();
        }
        return result.ToImmutable();
    }

    private static ImmutableArray<FaceLibraryMotion>? Motions(Func<string, byte[]?> readPap, string file)
    {
        try
        {
            if (readPap(file) is not { } bytes) return null;
            // Faces, blinks and lip shapes (PAP clip types 17, 18 and 19), each once.
            return new AnimationPap(bytes).Entries.Where(e => e.Type is 17 or 18 or 19 && e.Name.Length > 0)
                .DistinctBy(e => e.Name, StringComparer.Ordinal).Take(MaxMotions).Select(e => new FaceLibraryMotion(e.Name, e.Type == 19)).ToImmutableArray();
        }
        // A mod's file can be anything; one that doesn't read is skipped.
        catch (Exception e) when (e is not OutOfMemoryException and not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// The own length in whole frames of each face, blink and lip shape in a library's <paramref name="entries"/>, by motion
    /// name, from <paramref name="seconds"/> by binding index (the game's Havok reads them); one without a known length is left out.
    /// </summary>
    internal static ImmutableDictionary<string, int> Frames(IEnumerable<AnimationPap.Entry> entries, IReadOnlyDictionary<int, float> seconds) =>
        entries.Where(e => e.Type is 17 or 18 or 19 && e.Name.Length > 0).DistinctBy(e => e.Name, StringComparer.Ordinal)
            .Select(e => (e.Name, Frames: seconds.TryGetValue(e.Binding, out var length) ? TimelineDocument.MotionFrames(length) : null))
            .Where(e => e.Frames != null).ToImmutableDictionary(e => e.Name, e => e.Frames!.Value, StringComparer.Ordinal);

    /// <summary>
    /// The PAP game paths a mod maps to files inside it: Files in meta.json, default_mod.json and
    /// group_*.json, at any depth (options, containers). Read only.
    /// </summary>
    internal static IEnumerable<(string GamePath, string File)> Mappings(string root, CancellationToken token)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var found = new List<(string, string)>();
        void Visit(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                if (obj["Files"] is JsonObject files)
                    foreach (var (gamePath, value) in files)
                    {
                        if (!gamePath.EndsWith(".pap", StringComparison.OrdinalIgnoreCase) || !gamePath.Contains("/nonresident/", StringComparison.OrdinalIgnoreCase) ||
                            !AnimationDependencies.SafeGamePath(gamePath) || value is not JsonValue text || !text.TryGetValue<string>(out var relative) ||
                            string.IsNullOrWhiteSpace(relative))
                            continue;
                        string full;
                        try { full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))); }
                        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
                        if (PathRules.IsPathWithin(full, root)) found.Add((gamePath, full));
                    }
                foreach (var property in obj)
                    if (property.Key != "Files") Visit(property.Value);
            }
            else if (node is JsonArray array)
                foreach (var item in array) Visit(item);
        }
        foreach (var json in Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly)
                     .Where(p => Path.GetFileName(p) is var n && (n.Equals("meta.json", StringComparison.OrdinalIgnoreCase) ||
                         n.Equals("default_mod.json", StringComparison.OrdinalIgnoreCase) || n.StartsWith("group_", StringComparison.OrdinalIgnoreCase)))
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (new FileInfo(json).Length <= 16 * 1024 * 1024)
                    Visit(JsonNode.Parse(File.ReadAllText(json), documentOptions: new JsonDocumentOptions
                        { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }));
            }
            catch (Exception e) when (e is IOException or JsonException or ArgumentException or UnauthorizedAccessException or InvalidOperationException) { }
        }
        return found;
    }

    internal static string Serialize(ImmutableArray<FaceLibrary> list, DateTime built, Func<string, (bool Exists, long Length, DateTime Write)> fileState) =>
        JsonSerializer.Serialize(new CachedList(Version, built, [.. list.Select(library =>
        {
            var state = fileState(library.File);
            return new CachedLibrary(library, state.Exists ? state.Length : -1, state.Exists ? state.Write : DateTime.MinValue);
        })]), Json);

    /// <summary>
    /// Reads a saved list. It stays in use until it is rebuilt: libraries whose file was removed since
    /// the build are left out and counted as missing, rewritten ones are kept and counted as changed.
    /// </summary>
    internal static bool TryLoad(string json, Func<string, (bool Exists, long Length, DateTime Write)> fileState,
        out ImmutableArray<FaceLibrary> list, out int missing, out int changed, out DateTime builtUtc)
    {
        list = []; missing = 0; changed = 0; builtUtc = DateTime.MinValue;
        try
        {
            var cached = JsonSerializer.Deserialize<CachedList>(json, Json);
            if (cached is not { Version: Version } || cached.Libraries.IsDefault || cached.Libraries.Length > MaxLibraries) return false;
            var result = ImmutableArray.CreateBuilder<FaceLibrary>(cached.Libraries.Length);
            foreach (var item in cached.Libraries)
            {
                if (item?.Library is not { } library || library.Motions.IsDefault || library.Motions.Length > MaxMotions ||
                    library.Motions.Any(m => m is null || string.IsNullOrEmpty(m.Name)) || string.IsNullOrEmpty(library.File) ||
                    library.ModDirectory is null || library.ModName is null ||
                    !TryParse(library.GamePath ?? "", out var race, out var face, out var pack) ||
                    race != library.Race || face != library.Face || pack != library.Pack)
                    return false;
                var state = fileState(library.File);
                if (!state.Exists) { missing++; continue; }
                if (state.Length != item.Length || state.Write != item.Write) changed++;
                result.Add(library);
            }
            list = result.ToImmutable();
            builtUtc = cached.BuiltUtc;
            return true;
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            missing = 0; changed = 0;
            return false;
        }
    }

    /// <summary>
    /// Of the libraries for <paramref name="race"/>'s <paramref name="face"/>, the ones the collection
    /// plays: those whose game path it resolves to their own file (<paramref name="resolved"/>, null when
    /// it can't tell), one per library name.
    /// </summary>
    internal static ImmutableArray<FaceLibrary> Active(IEnumerable<FaceLibrary> list, string race, string face, Func<string, string?> resolved)
    {
        var result = ImmutableArray.CreateBuilder<FaceLibrary>();
        var packs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in list)
        {
            if (library.Race != race || library.Face != face || resolved(library.GamePath) is not { } file || !SameFile(file, library.File) ||
                !packs.Add(library.Pack))
                continue;
            result.Add(library);
        }
        return result.ToImmutable();
    }

    private static bool SameFile(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    /// <summary>
    /// The picker rows for the faces (or lip shapes, <paramref name="lips"/>) of <paramref name="active"/>
    /// libraries, each from its own library and named after its mod; motions the face always has
    /// loaded (<paramref name="resident"/>) need no library, so they are left out. Each row has its own length when
    /// <paramref name="frames"/> knows its library's lengths (null while unknown).
    /// </summary>
    internal static ImmutableArray<TimelineCatalogItem> Items(IEnumerable<FaceLibrary> active, bool lips, IReadOnlySet<string>? resident,
        Func<string, IReadOnlyDictionary<string, int>?>? frames = null) =>
        [.. active.SelectMany(library =>
        {
            var lengths = frames?.Invoke(library.Pack);
            return library.Motions
                .Where(motion => motion.Lip == lips && resident?.Contains(motion.Name) != true)
                .Select(motion => new TimelineCatalogItem(TimelineCatalogBuilder.Readable(motion.Name), motion.Name, TimelineCatalogOrigin.Installed,
                    library.ModName, library.Pack, Frames: lengths != null && lengths.TryGetValue(motion.Name, out var length) ? length : null));
        })];
}
