using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using InstantEdit.Services.Skeletons;

namespace InstantEdit.Services.GameFiles;

/// <summary>What an export writes besides the models.</summary>
internal sealed record GameExportOptions(bool Materials, bool Textures, bool AllVariants, bool SkeletonFiles, bool SkeletonJson);

/// <summary>An export's progress: its phase ("Files" or "Skeletons"), the items done and the current file.</summary>
internal sealed record GameExportProgress(string Phase, int Done, int Total, string Current);

internal sealed record GameExportFailure(string GamePath, string Error);

/// <summary>How an export ended: "completed", "cancelled" or "failed" (<paramref name="Error"/> says why).</summary>
internal sealed record GameExportResult(string Status, string Root, string? ManifestPath, int Models, int Files, long Bytes,
    ImmutableArray<GameExportFailure> Failures, string? Error)
{
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
    public const string Failed = "failed";
}

/// <summary>
/// What the exporter reads from the game. <paramref name="DecodeSkeleton"/> turns a model's skeleton
/// files into one merged skeleton (Havok runs on the game's framework thread); without it no
/// decoded skeletons are written.
/// </summary>
internal sealed record GameExportSource(
    Func<string, byte[]?> Read,
    Func<string, bool> Exists,
    Func<string, SkeletonFileSet?> SkeletonFiles,
    Func<string, CancellationToken, Task<(ModelSkeletonPayload? Payload, string? Problem)>>? DecodeSkeleton,
    Func<GameModelId, ImmutableArray<string>> Names,
    Func<GameModelId, bool?> PlayerSelectable,
    string PluginVersion,
    string GameVersion);

internal static class GameExportPaths
{
    /// <summary>
    /// The folder in Instant Edit's cache that exports go to by default, and the only one there that
    /// takes them. Keep it in the cache's owned-entry lists (TextureFiles.cs and cache.py).
    /// </summary>
    public const string CacheFolder = "game-exports";

    /// <summary>How long automatic cache cleanup keeps a file in <see cref="CacheFolder"/> that no export has written again.</summary>
    public static readonly TimeSpan CacheRetention = TimeSpan.FromDays(7);

    /// <summary>The default export folder inside the cache folder <paramref name="cacheRoot"/>.</summary>
    public static string CacheExportFolder(string cacheRoot) => Path.Combine(cacheRoot, CacheFolder);

    /// <summary>The manifest file an export started at <paramref name="startedUtc"/> writes.</summary>
    public static string ManifestName(DateTime startedUtc) => $"instant-edit-export-{startedUtc:yyyyMMdd-HHmmss}.json";

    /// <summary>
    /// Why <paramref name="root"/> can't take an export, or null. It must be a full path, and it may
    /// not lie inside a <paramref name="forbidden"/> folder: files there would unsettle Penumbra's mod
    /// folder or the game. Inside Instant Edit's cache folder <paramref name="cacheRoot"/> only its
    /// <see cref="CacheFolder"/> takes exports, where automatic cleanup looks after them.
    /// </summary>
    public static string? RootProblem(string? root, IEnumerable<(string Path, string Label)> forbidden, string? cacheRoot = null)
    {
        if (string.IsNullOrWhiteSpace(root))
            return "Choose a folder to export to.";
        string full;
        try
        {
            if (!Path.IsPathFullyQualified(root))
                return @"Enter a full folder path, such as D:\XIV exports.";
            full = Path.GetFullPath(root);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "That folder path is not valid.";
        }
        if (File.Exists(full))
            return "That path is a file, not a folder.";
        if (!string.IsNullOrWhiteSpace(cacheRoot) && Path.IsPathFullyQualified(cacheRoot) && PathRules.IsPathWithin(full, cacheRoot) &&
            !PathRules.IsPathWithin(full, CacheExportFolder(cacheRoot)))
            return $"In Instant Edit's cache folder, export to {CacheExportFolder(cacheRoot)}.";
        foreach (var (path, label) in forbidden)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
                continue;
            if (PathRules.IsPathWithin(full, path))
                return $"Choose a folder outside {label}.";
        }
        return null;
    }

    /// <summary>Where a game file goes under the export folder: at its game path. False if it would leave the folder.</summary>
    public static bool TryOutputPath(string root, string gamePath, out string fullPath)
    {
        fullPath = "";
        if (!PathRules.TryNormalizeRelativePath(gamePath, out var relative) || relative.Contains(':') ||
            relative.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            return false;
        try
        {
            var candidate = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!PathRules.IsPathWithin(candidate, root) || PathRules.SamePhysicalPath(candidate, root))
                return false;
            fullPath = candidate;
            return true;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>The decoded skeleton written next to a skeleton file: <c>skl_c0101h0114.skeleton.json</c>.</summary>
    public static string SkeletonJsonPath(string skeletonFile)
        => (skeletonFile.EndsWith(".sklb", StringComparison.OrdinalIgnoreCase) ? skeletonFile[..^5] : skeletonFile) + ".skeleton.json";

    /// <summary>
    /// Automatic cleanup of an export folder: removes the files last written before
    /// <paramref name="cutoffUtc"/>, then the folders that leaves empty, but not the folder itself.
    /// Exports rewrite every file they include, so a file's age is the time since the last export that
    /// included it. Links are neither followed nor removed, and files that can't be removed are kept.
    /// </summary>
    public static (int Files, long Bytes) Clean(string folder, DateTime cutoffUtc, CancellationToken token = default)
    {
        var root = new DirectoryInfo(folder);
        if (!root.Exists || (root.Attributes & FileAttributes.ReparsePoint) != 0)
            return (0, 0);
        var files = 0;
        long bytes = 0;
        CleanFolder(root);
        return (files, bytes);

        // True when the folder is left empty.
        bool CleanFolder(DirectoryInfo folderInfo)
        {
            var empty = true;
            foreach (var entry in folderInfo.GetFileSystemInfos())
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                        empty = false;
                    else if (entry is DirectoryInfo child)
                    {
                        if (CleanFolder(child))
                            child.Delete();
                        else
                            empty = false;
                    }
                    else if (entry.LastWriteTimeUtc >= cutoffUtc)
                        empty = false;
                    else
                    {
                        var length = ((FileInfo)entry).Length;
                        entry.Delete();
                        files++;
                        bytes += length;
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    empty = false;
                }
            }
            return empty;
        }
    }
}

/// <summary>
/// Writes vanilla game files to a folder, each at its game path, so files models share are written
/// once: the models, and as the options say their materials (and gear IMC files), textures, skeleton
/// files (and the EST tables that pick them) and decoded skeletons. A manifest records every model,
/// what it uses and what failed; it is written even when the export is cancelled or fails.
/// </summary>
internal sealed class GameFileExporter(GameExportSource source)
{
    public const string ManifestSchema = "instant-edit.game-export";
    public const int ManifestVersion = 1;

    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private sealed class ModelState(GameModelEntry entry)
    {
        public GameModelEntry Entry { get; } = entry;
        public GameModelDependencies? Dependencies { get; set; }
        public SkeletonFileSet? Skeleton { get; set; }
        public string? SkeletonJson { get; set; }
        public string? SkeletonProblem { get; set; }
        public List<string> Warnings { get; } = [];
    }

    private sealed class Writer(string root)
    {
        private readonly HashSet<string> written = new(StringComparer.OrdinalIgnoreCase);
        public int Files { get; private set; }
        public long Bytes { get; private set; }

        /// <summary>Writes a game file once. Unsafe paths throw InvalidDataException; disk errors are left to the caller.</summary>
        public void Write(string gamePath, byte[] bytes)
        {
            if (!written.Add(gamePath))
                return;
            if (!GameExportPaths.TryOutputPath(root, gamePath, out var full))
                throw new InvalidDataException($"{gamePath} would be written outside the export folder.");
            WriteFile(full, bytes);
            Files++;
            Bytes += bytes.Length;
        }

        public bool Has(string gamePath) => written.Contains(gamePath);

        public static void WriteFile(string full, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var temporary = full + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, full, true);
        }
    }

    public async Task<GameExportResult> RunAsync(IReadOnlyList<GameModelEntry> models, string root, GameExportOptions options,
        Action<GameExportProgress>? progress, CancellationToken token)
    {
        var started = DateTime.UtcNow;
        try
        {
            root = Path.GetFullPath(root);
            Directory.CreateDirectory(root);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new GameExportResult(GameExportResult.Failed, root, null, 0, 0, 0, [], $"The export folder could not be created: {e.Message}");
        }
        var writer = new Writer(root);
        var states = new List<ModelState>();
        var failures = new List<GameExportFailure>();
        var status = GameExportResult.Completed;
        string? error = null;
        try
        {
            for (var index = 0; index < models.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var entry = models[index];
                progress?.Invoke(new GameExportProgress("Files", index, models.Count, entry.GamePath));
                var state = new ModelState(entry);
                try
                {
                    ExportModel(state, options, writer);
                    states.Add(state);
                }
                catch (Exception e) when (e is InvalidDataException or FileNotFoundException)
                {
                    failures.Add(new GameExportFailure(entry.GamePath, e.Message));
                }
            }
            if (options.SkeletonJson && source.DecodeSkeleton is { } decode)
                await DecodeSkeletonsAsync(states, decode, writer, progress, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            status = GameExportResult.Cancelled;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            status = GameExportResult.Failed;
            error = e.Message;
        }

        string? manifestPath = Path.Combine(root, GameExportPaths.ManifestName(started));
        try
        {
            var manifest = Manifest(states, failures, options, status, started, writer);
            Writer.WriteFile(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, ManifestJson));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            manifestPath = null;
            status = GameExportResult.Failed;
            error ??= $"The manifest could not be written: {e.Message}";
        }
        return new GameExportResult(status, root, manifestPath, states.Count, writer.Files, writer.Bytes, [.. failures], error);
    }

    private void ExportModel(ModelState state, GameExportOptions options, Writer writer)
    {
        var path = state.Entry.GamePath;
        var model = source.Read(path) ?? throw new FileNotFoundException($"The game has no file at {path}.");
        writer.Write(path, model);

        if (options.Materials || options.Textures)
        {
            var variants = GameFileDependencies.MaterialVariants(state.Entry.Id, source.Read);
            try
            {
                state.Dependencies = GameFileDependencies.Resolve(path, model, options.AllVariants ? variants : [variants[0]],
                    source.Exists, source.Read);
            }
            catch (InvalidDataException e)
            {
                state.Warnings.Add($"Its materials could not be read: {e.Message}");
            }
            if (state.Dependencies is { } dependencies)
            {
                state.Warnings.AddRange(dependencies.Warnings);
                if (options.Materials && GameModelPaths.ImcPath(state.Entry.Id) is { } imc)
                    WriteIfPresent(imc);
                foreach (var material in dependencies.Materials.Where(material => material.Found))
                {
                    if (options.Materials)
                        WriteIfPresent(material.GamePath);
                    if (options.Textures)
                        foreach (var texture in material.Textures.Where(texture => texture.Found))
                            WriteIfPresent(texture.GamePath);
                }
            }
        }

        if (options.SkeletonFiles || options.SkeletonJson)
        {
            var skeleton = source.SkeletonFiles(path);
            state.Skeleton = skeleton;
            if (skeleton == null)
                state.SkeletonProblem = "No game skeleton is known for this kind of model.";
            else if (options.SkeletonFiles)
            {
                if (skeleton.Extra is { } extra)
                    WriteIfPresent(EstTable.GamePath(extra.Slot));
                foreach (var file in skeleton.Files)
                    if (!WriteIfPresent(file))
                        state.Warnings.Add($"The game has no skeleton file at {file}.");
            }
        }

        bool WriteIfPresent(string gamePath)
        {
            if (writer.Has(gamePath))
                return true;
            if (source.Read(gamePath) is not { } bytes)
                return false;
            writer.Write(gamePath, bytes);
            return true;
        }
    }

    private static async Task DecodeSkeletonsAsync(List<ModelState> states,
        Func<string, CancellationToken, Task<(ModelSkeletonPayload? Payload, string? Problem)>> decode, Writer writer,
        Action<GameExportProgress>? progress, CancellationToken token)
    {
        // Models with the same skeleton files share one decoded skeleton.
        var groups = states.Where(state => state.Skeleton is { Files.Length: > 0 })
            .GroupBy(state => string.Join('|', state.Skeleton!.Files), StringComparer.OrdinalIgnoreCase).ToArray();
        for (var index = 0; index < groups.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var group = groups[index];
            var first = group.First();
            var last = first.Skeleton!.Files[^1];
            progress?.Invoke(new GameExportProgress("Skeletons", index, groups.Length, last));
            var (payload, problem) = await decode(first.Entry.GamePath, token).ConfigureAwait(false);
            if (payload == null)
            {
                foreach (var state in group)
                    state.SkeletonProblem = problem ?? "The game skeleton could not be read.";
                continue;
            }
            var json = GameExportPaths.SkeletonJsonPath(last);
            writer.Write(json, JsonSerializer.SerializeToUtf8Bytes(payload));
            foreach (var state in group)
                state.SkeletonJson = json;
        }
    }

    private object Manifest(List<ModelState> states, List<GameExportFailure> failures, GameExportOptions options, string status,
        DateTime started, Writer writer)
        => new
        {
            schema = ManifestSchema,
            version = ManifestVersion,
            status,
            created = started,
            plugin = source.PluginVersion,
            game = source.GameVersion,
            options,
            models = states.Select(ManifestModel).ToArray(),
            failures,
            totals = new { models = states.Count, files = writer.Files, bytes = writer.Bytes },
        };

    private object ManifestModel(ModelState state)
    {
        var id = state.Entry.Id;
        var race = id.Human ? GameRaces.Find(id.Race) : null;
        return new
        {
            gamePath = state.Entry.GamePath,
            category = id.Category.ToString().ToLowerInvariant(),
            race = id.Human ? $"c{id.Race:D4}" : null,
            raceLabel = id.Human ? GameRaces.Label(id.Race) : null,
            npc = id.Human ? race is not { Player: true } : (bool?)null,
            id = (int)id.Id,
            secondaryId = id.Category == GameFileCategory.Weapon ? (int?)id.SecondaryId : null,
            slot = id.Slot.Length > 0 ? id.Slot : null,
            names = source.Names(id),
            playerSelectable = source.PlayerSelectable(id),
            materialVariants = state.Dependencies?.Variants,
            materials = state.Dependencies?.Materials.Select(material => new
            {
                name = material.Name,
                gamePath = material.GamePath,
                variant = material.Variant,
                found = material.Found,
                textures = material.Textures.Select(texture => new { gamePath = texture.GamePath, usage = texture.Usage, found = texture.Found }),
                problem = material.Problem.Length > 0 ? material.Problem : null,
            }),
            skeleton = state.Skeleton == null && state.SkeletonProblem == null ? null : new
            {
                files = state.Skeleton?.Files,
                est = state.Skeleton?.Extra is { } extra
                    ? new { table = EstTable.GamePath(extra.Slot), set = extra.Set, skeleton = (int)state.Skeleton.Entry }
                    : null,
                json = state.SkeletonJson,
                problem = state.SkeletonProblem,
                warnings = state.Skeleton?.Warnings,
            },
            warnings = state.Warnings,
        };
    }
}
