using Dalamud.Plugin.Services;

namespace InstantEdit.Services.Physics;

/// <summary> What a file the character's collection loads is, for display. </summary>
internal sealed record PhysicsLoadedFile(string GamePath, string ResolvedPath, string Label, bool Mine, bool Preview);

/// <summary> The character the builder works for, and the files its collection loads for it. </summary>
internal sealed record PhysicsTarget(PhysicsCharacter Character, Guid CollectionId, string CollectionName, PhysicsLoadedFile Physics,
    PhysicsLoadedFile Skeleton);

/// <summary>
/// The physics builder's game side: finds your character, its skeleton and the body physics its
/// collection loads; reads a starting file (the game's, the loaded one, your physics mod's or one
/// you pick); previews a document through a temporary mod; and saves it into your physics mod, a
/// mod of its own enabled above every other in your collection, so it can be changed again any time.
/// </summary>
internal sealed class PhysicsBuilderService : IDisposable
{
    public const string ModName = "IE Physics";
    private const string ModDescription =
        "Body physics made with XIV Instant Edit's physics builder (Quick Actions > Body physics). Open the builder to change it.";

    private readonly PenumbraService penumbra;
    private readonly IDataManager data;
    private readonly IFramework framework;
    private readonly IObjectTable objects;
    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly Action saveConfig;

    public PhysicsBuilderService(PenumbraService penumbra, IDataManager data, IFramework framework, IObjectTable objects, IPluginLog log,
        Configuration config, Action saveConfig, string configDirectory, Func<string> cacheRoot)
    {
        this.penumbra = penumbra;
        this.data = data;
        this.framework = framework;
        this.objects = objects;
        this.log = log;
        this.config = config;
        this.saveConfig = saveConfig;
        Preview = new PhysicsPreviewService(penumbra, framework, log, configDirectory, cacheRoot);
    }

    public PhysicsPreviewService Preview { get; }

    /// <summary> The physics mod was saved before (it may since have been deleted in Penumbra). </summary>
    public bool HasMod => config.PhysicsModDirectory.Length > 0 && config.PhysicsModIdentifier != Guid.Empty;

    public string ModDirectory => config.PhysicsModDirectory;

    /// <summary> Reads your character, its skeleton and what its collection loads for its body. </summary>
    public async Task<PhysicsTarget> FindAsync()
    {
        var (character, problem) = await framework.RunOnFrameworkThread(() => PhysicsLiveReader.ReadLocalPlayer(objects)).ConfigureAwait(false);
        if (character is null)
            throw new IOException(problem ?? "Your character can't be read.");
        var collection = await penumbra.GetCollectionTargetAsync(character.ObjectIndex).ConfigureAwait(false)
            ?? throw new IOException("Your character's Penumbra collection is unavailable.");
        var physicsPath = PhysicsPaths.BodyPhysics(character.Race);
        var skeletonPath = PhysicsPaths.BodySkeleton(character.Race);
        var resolved = await penumbra.ResolveCollectionPathsAsync(collection.Id, [physicsPath, skeletonPath]).ConfigureAwait(false);
        return new PhysicsTarget(character, collection.Id, collection.Name,
            await DescribeAsync(physicsPath, resolved[0]).ConfigureAwait(false),
            await DescribeAsync(skeletonPath, resolved[1]).ConfigureAwait(false));
    }

    private async Task<PhysicsLoadedFile> DescribeAsync(string gamePath, string? resolved)
    {
        if (string.IsNullOrWhiteSpace(resolved) || !Path.IsPathFullyQualified(resolved))
            return new PhysicsLoadedFile(gamePath, resolved ?? gamePath, "the game's own file", false, false);
        if (IsPreviewFile(resolved))
            return new PhysicsLoadedFile(gamePath, resolved, "the preview", false, true);
        var mod = await penumbra.ModOfFileAsync(resolved).ConfigureAwait(false);
        if (mod is { } found && HasMod && string.Equals(found.Directory, config.PhysicsModDirectory, StringComparison.OrdinalIgnoreCase))
            return new PhysicsLoadedFile(gamePath, resolved, "your physics mod", true, false);
        return new PhysicsLoadedFile(gamePath, resolved, mod?.Name ?? Path.GetFileName(resolved), false, false);
    }

    private static bool IsPreviewFile(string path)
        => path.Replace('\\', '/').Contains($"/{PhysicsPreviewService.CacheFolder}/", StringComparison.OrdinalIgnoreCase);

    /// <summary> A new document from a starting point. <paramref name="file"/> is the file to open for <see cref="PhysicsSourceKind.File"/>. </summary>
    public async Task<PhysicsDocument> LoadAsync(PhysicsTarget target, PhysicsSourceKind kind, string? file = null)
    {
        var race = target.Character.Race;
        var gamePath = PhysicsPaths.BodyPhysics(race);
        switch (kind)
        {
            case PhysicsSourceKind.Vanilla:
            {
                var bytes = await Task.Run(() => data.GetFile(gamePath)?.Data).ConfigureAwait(false)
                    ?? throw new FileNotFoundException($"The game has no body physics file for c{race:D4}.");
                return new PhysicsDocument(PhybFile.Parse(bytes), new PhysicsSource(kind, "The game's own file", gamePath), race);
            }
            case PhysicsSourceKind.Loaded:
            {
                var loaded = target.Physics;
                if (loaded.Preview)
                    throw new InvalidOperationException("The preview is on. Stop it to start from what your mods load.");
                if (!Path.IsPathFullyQualified(loaded.ResolvedPath))
                {
                    var bytes = await Task.Run(() => data.GetFile(gamePath)?.Data).ConfigureAwait(false)
                        ?? throw new FileNotFoundException($"The game has no body physics file for c{race:D4}.");
                    return new PhysicsDocument(PhybFile.Parse(bytes), new PhysicsSource(kind, "The game's own file", gamePath), race);
                }
                return new PhysicsDocument(await ReadFileAsync(loaded.ResolvedPath).ConfigureAwait(false),
                    new PhysicsSource(loaded.Mine ? PhysicsSourceKind.Mine : kind, loaded.Mine ? "Your physics mod" : loaded.Label, loaded.ResolvedPath), race);
            }
            case PhysicsSourceKind.Mine:
            {
                if (!HasMod)
                    throw new InvalidOperationException("You haven't saved a physics mod yet.");
                var (bytes, path) = await penumbra.ReadPhysicsModAsync(config.PhysicsModDirectory, config.PhysicsModIdentifier, gamePath).ConfigureAwait(false);
                if (bytes is null)
                    throw new FileNotFoundException($"Your physics mod has no body physics for c{race:D4}, or it was deleted.");
                return new PhysicsDocument(PhybFile.Parse(bytes), new PhysicsSource(kind, "Your physics mod", path), race);
            }
            default:
            {
                if (string.IsNullOrWhiteSpace(file))
                    throw new ArgumentException("No file was picked.");
                return new PhysicsDocument(await ReadFileAsync(file).ConfigureAwait(false),
                    new PhysicsSource(PhysicsSourceKind.File, Path.GetFileName(file), file), race);
            }
        }
    }

    /// <summary> Reads and parses a physics file from disk. </summary>
    public static async Task<PhybFile> ReadFileAsync(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new FileNotFoundException($"{Path.GetFileName(path)} doesn't exist.");
        if (info.Length > PhybFile.MaxFileBytes)
            throw new InvalidDataException($"{Path.GetFileName(path)} is too large for a physics file.");
        var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        return PhybFile.Parse(bytes);
    }

    /// <summary>
    /// The file to preview or save, refused when the document has errors or was made for another race.
    /// Call it on the thread that edits the document; the tasks below only get the bytes.
    /// </summary>
    public static byte[] Prepare(PhysicsDocument document, PhysicsTarget target)
    {
        if (document.Race != target.Character.Race)
            throw new InvalidOperationException($"This physics is for c{document.Race:D4}, and your character is c{target.Character.Race:D4} now.");
        var errors = PhysicsValidation.Check(document.Output(), target.Character.Skeleton).Where(issue => issue.Level == PhysicsIssueLevel.Error).ToList();
        if (errors.Count > 0)
            throw new InvalidDataException(errors.Count == 1 ? $"Fix this first: {errors[0].Text}" : $"Fix {errors.Count} problems first, such as: {errors[0].Text}");
        return document.OutputBytes();
    }

    /// <summary> Shows a prepared file on your character. Returns a warning, or null. </summary>
    public Task<string?> PreviewAsync(PhysicsTarget target, byte[] bytes, CancellationToken token)
        => Preview.ShowAsync(target.Character.ObjectIndex, PhysicsPaths.BodyPhysics(target.Character.Race), bytes, token);

    public Task StopPreviewAsync(PhysicsTarget? target) => Preview.StopAsync(target?.Character.ObjectIndex);

    /// <summary>
    /// Saves a prepared file into your physics mod, making the mod the first time, ends the preview
    /// and redraws your character. Returns the warnings to show.
    /// </summary>
    public async Task<IReadOnlyList<string>> SaveAsync(PhysicsTarget target, byte[] bytes, CancellationToken token)
    {
        var race = target.Character.Race;
        var saved = await penumbra.SavePhysicsModAsync(config.PhysicsModDirectory.Length > 0 ? config.PhysicsModDirectory : null,
            config.PhysicsModIdentifier, ModName, ModDescription, PhysicsPaths.BodyPhysics(race), bytes, target.Character.ObjectIndex, token).ConfigureAwait(false);
        await framework.RunOnFrameworkThread(() =>
        {
            config.PhysicsModDirectory = saved.Directory;
            config.PhysicsModIdentifier = saved.Identifier;
            saveConfig();
        }).ConfigureAwait(false);
        var warnings = saved.Warnings.ToList();
        try { await Preview.StopAsync(redrawObject: null).ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            warnings.Add($"The preview is still on: {e.Message}");
        }
        if (await penumbra.RedrawObjectAsync(target.Character.ObjectIndex).ConfigureAwait(false) is { } redraw)
            warnings.Add(redraw);
        log.Information($"Saved body physics for c{race:D4} into {saved.Directory} ({saved.RelativePath}).");
        return warnings;
    }

    /// <summary> Turns your physics mod on or off in your collection and redraws your character. </summary>
    public async Task<string?> SetModEnabledAsync(PhysicsTarget target, bool enabled)
    {
        if (!HasMod)
            return "You haven't saved a physics mod yet.";
        if (await penumbra.SetPhysicsModEnabledAsync(config.PhysicsModDirectory, target.Character.ObjectIndex, enabled).ConfigureAwait(false) is { } error)
            return error;
        return await penumbra.RedrawObjectAsync(target.Character.ObjectIndex).ConfigureAwait(false);
    }

    public void Dispose() => Preview.Dispose();
}
