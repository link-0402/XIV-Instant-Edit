using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using InstantEdit.Services.Skeletons;

namespace InstantEdit.Services.Physics;

/// <summary> What a file the character's collection loads is, for display. </summary>
internal sealed record PhysicsLoadedFile(string GamePath, string ResolvedPath, string Label, bool Mine, bool Preview);

/// <summary> The character the builder works for, and the files its collection loads for it. </summary>
internal sealed record PhysicsTarget(PhysicsCharacter Character, Guid CollectionId, string CollectionName, PhysicsLoadedFile Physics,
    PhysicsLoadedFile Skeleton);

/// <summary>
/// Your body as the game draws it, standing in the measuring pose, to fit physics to; what to know
/// about the measuring; and a line saying what shaped it.
/// </summary>
internal sealed record PhysicsBodyMeasure(PhysicsBody Body, PhysicsSkeleton Skeleton, IReadOnlyList<string> Warnings, string Note = "");

/// <summary>
/// The physics builder's game side: finds your character, its skeleton and the body physics its
/// collection loads; reads a starting file (the game's, the loaded one, your physics mod's or one
/// you pick); previews a document through a temporary mod; and saves it into your physics mod, a
/// mod of its own enabled above every other in your collection, so it can be changed again any time.
/// In GPose it reads and measures your posed copy, the character you see; previews and saves still
/// redraw your own character, which Penumbra turns into a redraw of the posed copy (and of your
/// character once GPose ends).
/// </summary>
internal sealed class PhysicsBuilderService : IDisposable
{
    public const string ModName = "IE Physics";
    private const string ModDescription =
        "Body physics made with XIV Instant Edit's physics builder (Quick Actions > Body physics). Open the builder to change it.";

    private const long MaxModelBytes = 256L * 1024 * 1024;

    private readonly PenumbraService penumbra;
    private readonly IDataManager data;
    private readonly IFramework framework;
    private readonly IObjectTable objects;
    private readonly IClientState clientState;
    private readonly ModelSkeletonResolver skeletons;
    private readonly PhysicsCustomizePlus customizePlus;
    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly Action saveConfig;

    public PhysicsBuilderService(IDalamudPluginInterface pi, PenumbraService penumbra, IDataManager data, IFramework framework, IObjectTable objects,
        IClientState clientState, ModelSkeletonResolver skeletons, IPluginLog log, Configuration config, Action saveConfig,
        string configDirectory, Func<string> cacheRoot)
    {
        this.penumbra = penumbra;
        this.data = data;
        this.framework = framework;
        this.objects = objects;
        this.clientState = clientState;
        this.skeletons = skeletons;
        customizePlus = new PhysicsCustomizePlus(pi, log);
        this.log = log;
        this.config = config;
        this.saveConfig = saveConfig;
        Preview = new PhysicsPreviewService(penumbra, framework, log, configDirectory, cacheRoot);
    }

    public PhysicsPreviewService Preview { get; }

    /// <summary> The physics mod was saved before (it may since have been deleted in Penumbra). </summary>
    public bool HasMod => config.PhysicsModDirectory.Length > 0 && config.PhysicsModIdentifier != Guid.Empty;

    public string ModDirectory => config.PhysicsModDirectory;

    /// <summary> You're in GPose, where only posed characters can be made to walk or run. </summary>
    public bool Posing => clientState.IsGPosing;

    /// <summary> The posed copy of your character in GPose, or null outside GPose and when there's none. Framework thread only. </summary>
    public ushort? PosedActor()
    {
        if (!clientState.IsGPosing || objects.LocalPlayer is not ICharacter player || player.Address == 0)
            return null;
        var shown = PhysicsLiveReader.Shown(objects, player, posing: true);
        return shown.ObjectIndex >= PhysicsLiveReader.FirstPosed ? shown.ObjectIndex : null;
    }

    /// <summary> Reads your character, its skeleton and what its collection loads for its body. </summary>
    public async Task<PhysicsTarget> FindAsync()
    {
        var (character, problem) = await framework.RunOnFrameworkThread(() => PhysicsLiveReader.ReadLocalPlayer(objects, clientState.IsGPosing))
            .ConfigureAwait(false);
        if (character is null)
            throw new IOException(problem ?? "Your character can't be read.");
        var collection = await penumbra.GetCollectionTargetAsync(character.ObjectIndex).ConfigureAwait(false)
            ?? throw new IOException("Your character's Penumbra collection is unavailable.");
        var physicsPath = PhysicsPaths.BodyPhysics(character.Race);
        var skeletonPath = PhysicsPaths.BodySkeleton(character.Race);
        var resolved = await penumbra.ResolveCollectionPathsAsync(collection.Id, [physicsPath, skeletonPath]).ConfigureAwait(false);
        await FollowModAsync(lookAgain: false).ConfigureAwait(false);
        return new PhysicsTarget(character, collection.Id, collection.Name,
            await DescribeAsync(physicsPath, resolved[0], PhysicsPaths.BodyPhysics).ConfigureAwait(false),
            await DescribeAsync(skeletonPath, resolved[1], PhysicsPaths.BodySkeleton).ConfigureAwait(false));
    }

    private async Task<PhysicsLoadedFile> DescribeAsync(string gamePath, string? resolved, Func<ushort, string> racePath)
    {
        if (string.IsNullOrWhiteSpace(resolved) || !Path.IsPathFullyQualified(resolved))
            return new PhysicsLoadedFile(gamePath, string.IsNullOrWhiteSpace(resolved) ? gamePath : resolved,
                PhysicsPaths.GameFileLabel(gamePath, resolved, racePath), false, false);
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
                    // A game file: the race's own, or the one a file swap names.
                    var swapped = PathRules.NormalizeGamePath(loaded.ResolvedPath);
                    var bytes = await Task.Run(() => data.GetFile(swapped)?.Data).ConfigureAwait(false)
                        ?? throw new FileNotFoundException($"The game has no body physics file at {swapped}.");
                    var label = PhysicsPaths.GameFileLabel(gamePath, swapped, PhysicsPaths.BodyPhysics);
                    return new PhysicsDocument(PhybFile.Parse(bytes), new PhysicsSource(kind, char.ToUpperInvariant(label[0]) + label[1..], swapped), race);
                }
                return new PhysicsDocument(await ReadFileAsync(loaded.ResolvedPath).ConfigureAwait(false),
                    new PhysicsSource(loaded.Mine ? PhysicsSourceKind.Mine : kind, loaded.Mine ? "Your physics mod" : loaded.Label, loaded.ResolvedPath), race);
            }
            case PhysicsSourceKind.Mine:
            {
                if (!HasMod)
                    throw new InvalidOperationException("You haven't saved a physics mod yet.");
                await FollowModAsync(lookAgain: true).ConfigureAwait(false);
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

    /// <summary>
    /// Measures your body as the game draws it: the models of your body, hands, legs and feet, read
    /// from the files your collection loads and reshaped for your race, standing in the skeleton's rest
    /// pose with your Customize+ profile and bust size (<see cref="PhysicsPose"/>). Your animation,
    /// physics and posing tools don't count, so measuring again gives the same body. Gear covering a
    /// slot is measured as it is, with a warning, since the fit is for bare skin.
    /// </summary>
    public async Task<PhysicsBodyMeasure> MeasureBodyAsync(CancellationToken token)
    {
        var (capture, resolved, plus, plusInstalled, plusProblem) = await framework.RunOnFrameworkThread(() =>
        {
            var (body, problem) = PhysicsLiveReader.ReadBody(objects, clientState.IsGPosing);
            if (body is null)
                throw new IOException(problem ?? "Your character can't be read.");
            // Penumbra's resource tree and Customize+ read the character, so they have to run here.
            var paths = penumbra.GetResourcePaths((ushort)body.ObjectIndex);
            var profile = customizePlus.Read((ushort)body.Character.ObjectIndex, out var installed, out var why);
            return (body, paths, profile, installed, why);
        }).ConfigureAwait(false);
        if (capture.Models.Count == 0)
            throw new IOException("Your character draws no body right now.");

        var warnings = new List<string>();
        if (plusProblem is not null)
            warnings.Add($"Measured without Customize+: {plusProblem}.");
        else if (plus is null && plusInstalled)
            warnings.Add("Customize+ shares no profile of yours (temporary ones, like a sync plugin's, can't be read), so your body is measured without it.");
        var (scaling, problem) = await skeletons.RacialScalingAsync(capture.ObjectIndex, capture.Address, token).ConfigureAwait(false);
        if (scaling is null && problem is not null)
            warnings.Add($"Measured without racial scaling: {problem}.");
        var models = new List<PhysicsBodyModel>();
        foreach (var drawn in capture.Models)
        {
            var slot = PhysicsPaths.SlotName(drawn.Slot);
            if (PhysicsPaths.ResolveDrawn(resolved, drawn.File, drawn.Slot) is not var (actual, gamePath))
            {
                warnings.Add($"Penumbra doesn't list your {slot} model, so it wasn't measured. Redraw your character and try again.");
                continue;
            }
            if (!PhysicsPaths.IsBareBody(gamePath))
                warnings.Add($"You're wearing {slot} gear, so the fit measured the gear, not your skin.");
            var bytes = await Task.Run(() => ReadModel(actual), token).ConfigureAwait(false);
            if (bytes is null)
            {
                warnings.Add($"Your {slot} model couldn't be read, so it wasn't measured.");
                continue;
            }
            RacialDeformer? deformer = null;
            try { deformer = scaling?.For(gamePath.Length > 0 ? gamePath : Path.GetFileName(actual))?.Deformer; }
            catch (InvalidDataException e) { warnings.Add($"Your {slot} model was measured without racial scaling: {e.Message}"); }
            models.Add(new PhysicsBodyModel(slot, bytes, deformer, drawn.Attributes, drawn.Shapes));
        }
        if (models.Count == 0)
            throw new IOException("None of your body's models could be read.");

        var skeleton = capture.Character.Skeleton;
        var rest = PhysicsBody.RestPose(skeleton);
        var look = capture.Customize;
        // The game's own bust ranges: a mod that changes racial bust scaling isn't read, so the window asks to remove those.
        var bust = await Task.Run(() => data.GetFile(PhysicsPose.CharaMakeFile)?.Data is { } cmp
            ? PhysicsPose.Bust(cmp, look.Clan, look.Female, look.BodyType, look.BustSize)
            : System.Numerics.Vector3.One, token).ConfigureAwait(false);
        var pose = PhysicsPose.Reference(skeleton, plus, bust);
        var measured = await Task.Run(() => PhysicsBody.Measure(models, rest, pose), token).ConfigureAwait(false);
        if (measured.MissingBones > 0)
            warnings.Add($"Your skeleton lacks {measured.MissingBones} bones your body models use; their skin was measured unposed.");
        var shaped = plus is null ? "your body mod" : "your body mod and Customize+ profile";
        var note = look.Female ? $"Measured standing still with {shaped}, bust size {look.BustSize}." : $"Measured standing still with {shaped}.";
        return new PhysicsBodyMeasure(measured, skeleton, warnings, note);
    }

    private byte[]? ReadModel(string actual)
    {
        try
        {
            if (Path.IsPathFullyQualified(actual))
            {
                var info = new FileInfo(actual);
                return info.Exists && info.Length is > 0 and <= MaxModelBytes ? File.ReadAllBytes(info.FullName) : null;
            }
            // The file Penumbra redirects to, which a file swap makes another game file than the slot's own path.
            return data.GetFile(PathRules.NormalizeGamePath(actual))?.Data;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            log.Debug(e, "Could not read {Path} to measure the body.", actual);
            return null;
        }
    }

    /// <summary>
    /// Turns your physics mod on or off in your collection and redraws your character. Turning it off
    /// stops the preview first, which sits above every mod and would still show.
    /// </summary>
    public async Task<string?> SetModEnabledAsync(PhysicsTarget target, bool enabled)
    {
        if (!HasMod)
            return "You haven't saved a physics mod yet.";
        await FollowModAsync(lookAgain: true).ConfigureAwait(false);
        string? stopProblem = null;
        if (!enabled)
        {
            try { await Preview.StopAsync(redrawObject: null).ConfigureAwait(false); }
            catch (Exception e) when (e is IOException or InvalidOperationException) { stopProblem = e.Message; }
        }
        if (await penumbra.SetPhysicsModEnabledAsync(config.PhysicsModDirectory, target.Character.ObjectIndex, enabled).ConfigureAwait(false) is { } error)
            return stopProblem is null ? error : $"{error} The preview is still on: {stopProblem}";
        var redraw = await penumbra.RedrawObjectAsync(target.Character.ObjectIndex).ConfigureAwait(false);
        if (stopProblem is null)
            return redraw;
        var still = $"{config.PhysicsModDirectory} is off, but the preview is still on: {stopProblem}";
        return redraw is null ? still : $"{still} {redraw}";
    }

    // The folder and identifier a look for your renamed mod last found nothing for.
    private (string Directory, Guid Identifier)? lostMod;

    /// <summary>
    /// Follows your physics mod when it was renamed in Penumbra: finds the folder with its identifier and
    /// remembers it, so saves keep going into it. Unless <paramref name="lookAgain"/>, a mod that's gone
    /// is only looked for once, so reading your character again stays quick.
    /// </summary>
    private async Task FollowModAsync(bool lookAgain)
    {
        if (!HasMod)
            return;
        var known = (Directory: config.PhysicsModDirectory, Identifier: config.PhysicsModIdentifier);
        if (!lookAgain && lostMod == known)
            return;
        var found = await penumbra.FindPhysicsModAsync(known.Directory, known.Identifier).ConfigureAwait(false);
        lostMod = found is null ? known : null;
        if (found is null || found == known.Directory)
            return;
        await framework.RunOnFrameworkThread(() =>
        {
            // A save in the meantime already chose the folder.
            if (config.PhysicsModDirectory != known.Directory)
                return;
            config.PhysicsModDirectory = found;
            saveConfig();
        }).ConfigureAwait(false);
        log.Information($"Your physics mod was renamed from {known.Directory} to {found}.");
    }

    public void Dispose() => Preview.Dispose();
}
