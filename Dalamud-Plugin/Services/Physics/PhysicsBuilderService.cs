using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using InstantEdit.Services.PenumbraMods;
using InstantEdit.Services.Skeletons;

namespace InstantEdit.Services.Physics;

/// <summary>
/// What a file the character's collection loads is, for display. <paramref name="Mod"/> is the folder of
/// the builder's mod it comes from, when <paramref name="Mine"/>.
/// </summary>
internal sealed record PhysicsLoadedFile(string GamePath, string ResolvedPath, string Label, bool Mine, bool Preview, string Mod = "");

/// <summary> A mod the physics builder saved: its folder in Penumbra and its identifier. </summary>
public sealed class PhysicsSavedMod
{
    public string Directory { get; set; } = "";
    public Guid Identifier { get; set; }
}

/// <summary> A finished save: the mod it went into, anything to report and your other builder mods it turned off. </summary>
internal sealed record PhysicsSaveResult(string Directory, IReadOnlyList<string> Warnings, IReadOnlyList<string> TurnedOff);

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
/// collection loads; reads a starting file (the game's, the loaded one or one you pick); previews a
/// document through a temporary mod; and saves it into a mod named as you like, enabled above every
/// other in your collection, so it can be changed again any time. Each name is a mod of its own, so
/// you can keep several and switch between them in Penumbra.
/// In GPose it reads and measures your posed copy, the character you see; previews and saves still
/// redraw your own character, which Penumbra turns into a redraw of the posed copy (and of your
/// character once GPose ends).
/// </summary>
internal sealed class PhysicsBuilderService : IDisposable
{
    /// <summary> The name a save suggests first; later ones get a number. </summary>
    public const string ModName = "IE Physics";
    private const string ModDescription =
        "Body physics made with XIV Instant Edit's physics builder (Quick Actions > Body physics builder). Open the builder to change it.";

    private const long MaxModelBytes = 256L * 1024 * 1024;

    private readonly PenumbraIpc penumbra;
    private readonly PhysicsModService physicsMods;
    private readonly IDataManager data;
    private readonly IFramework framework;
    private readonly IObjectTable objects;
    private readonly IClientState clientState;
    private readonly ModelSkeletonResolver skeletons;
    private readonly PhysicsCustomizePlus customizePlus;
    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly Action saveConfig;

    public PhysicsBuilderService(IDalamudPluginInterface pi, PenumbraIpc penumbra, PhysicsModService physicsMods, PreviewModWriter previewMods,
        IDataManager data, IFramework framework, IObjectTable objects, IClientState clientState, ModelSkeletonResolver skeletons, IPluginLog log,
        Configuration config, Action saveConfig, string configDirectory, Func<string> cacheRoot)
    {
        this.penumbra = penumbra;
        this.physicsMods = physicsMods;
        this.data = data;
        this.framework = framework;
        this.objects = objects;
        this.clientState = clientState;
        this.skeletons = skeletons;
        customizePlus = new PhysicsCustomizePlus(pi, log);
        this.log = log;
        this.config = config;
        this.saveConfig = saveConfig;
        Preview = new PhysicsPreviewService(penumbra, previewMods, framework, log, configDirectory, cacheRoot);
        if (config.PhysicsModDirectory.Length > 0 && config.PhysicsModIdentifier != Guid.Empty)
        {
            // The one mod from before saves were named.
            if (!config.PhysicsMods.Any(mod => mod.Identifier == config.PhysicsModIdentifier))
                config.PhysicsMods = [..config.PhysicsMods, new PhysicsSavedMod { Directory = config.PhysicsModDirectory, Identifier = config.PhysicsModIdentifier }];
            config.PhysicsModDirectory = string.Empty;
            config.PhysicsModIdentifier = Guid.Empty;
            saveConfig();
        }
    }

    public PhysicsPreviewService Preview { get; }

    /// <summary> A physics mod was saved before (it may since have been deleted in Penumbra). </summary>
    public bool HasMod => config.PhysicsMods.Count > 0;

    /// <summary> The folders of the mods the builder saved. </summary>
    public IReadOnlyList<string> ModDirectories => config.PhysicsMods.Select(mod => mod.Directory).ToList();

    /// <summary> The builder saved a mod in this folder. </summary>
    public bool IsMine(string directory) => config.PhysicsMods.Any(mod => string.Equals(mod.Directory, directory, StringComparison.OrdinalIgnoreCase));

    /// <summary> "IE Physics", or with the first number none of your mods has yet: a name for a new mod. </summary>
    public string FreeModName()
    {
        for (var number = 1; ; number++)
        {
            var name = number == 1 ? ModName : $"{ModName} ({number})";
            if (!IsMine(name))
                return name;
        }
    }

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

    /// <summary>
    /// Reads the pose of the character the builder shows (your posed copy in GPose, else your
    /// character) into <paramref name="pose"/>; false when it can't be read. Framework thread only.
    /// </summary>
    public bool ReadShownPose(Animations.Preview.ActorPose pose)
    {
        if (objects.LocalPlayer is not ICharacter player || player.Address == 0)
            return false;
        var shown = PhysicsLiveReader.Shown(objects, player, clientState.IsGPosing);
        return shown.Address != 0 && pose.Read(shown.Address);
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
        await FollowModsAsync(lookAgain: false).ConfigureAwait(false);
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
        var mod = await physicsMods.ModOfFileAsync(resolved).ConfigureAwait(false);
        if (mod is { } found && IsMine(found.Directory))
            return new PhysicsLoadedFile(gamePath, resolved, found.Name, true, false, found.Directory);
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
                // One of your own mods: saving suggests its name, so the changes go back into it.
                return new PhysicsDocument(await ReadFileAsync(loaded.ResolvedPath).ConfigureAwait(false),
                    new PhysicsSource(loaded.Mine ? PhysicsSourceKind.Mine : kind, loaded.Label, loaded.ResolvedPath, loaded.Mod), race);
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
    /// Saves a prepared file into the mod called <paramref name="name"/>: one the builder saved before
    /// gets the new file, any other name makes a new mod (a name another mod has is refused). Turns
    /// your other builder mods off in your collection, since they replace the same file at the same
    /// priority, ends the preview and redraws your character.
    /// </summary>
    public async Task<PhysicsSaveResult> SaveAsync(PhysicsTarget target, byte[] bytes, string name, CancellationToken token)
    {
        name = name.Trim();
        if (!ModPaths.IsSafeNewModName(name))
            throw new InvalidDataException($"\"{name}\" can't be a mod name.");
        await FollowModsAsync(lookAgain: true).ConfigureAwait(false);
        var race = target.Character.Race;
        var known = config.PhysicsMods.FirstOrDefault(mod => string.Equals(mod.Directory, name, StringComparison.OrdinalIgnoreCase));
        var saved = await physicsMods.SavePhysicsModAsync(known?.Directory, known?.Identifier ?? Guid.Empty, name, ModDescription,
            PhysicsPaths.BodyPhysics(race), bytes, target.Character.ObjectIndex, token).ConfigureAwait(false);
        var others = await framework.RunOnFrameworkThread(() =>
        {
            var mods = config.PhysicsMods.Where(mod => !string.Equals(mod.Directory, saved.Directory, StringComparison.OrdinalIgnoreCase) &&
                                                       mod.Identifier != saved.Identifier).ToList();
            var rest = mods.Select(mod => mod.Directory).ToList();
            mods.Add(new PhysicsSavedMod { Directory = saved.Directory, Identifier = saved.Identifier });
            config.PhysicsMods = mods;
            saveConfig();
            return rest;
        }).ConfigureAwait(false);
        var warnings = saved.Warnings.ToList();
        var turnedOff = await physicsMods.TurnOffPhysicsModsAsync(others, target.Character.ObjectIndex).ConfigureAwait(false);
        try { await Preview.StopAsync(redrawObject: null).ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            warnings.Add($"The preview is still on: {e.Message}");
        }
        if (await penumbra.RedrawObjectAsync(target.Character.ObjectIndex).ConfigureAwait(false) is { } redraw)
            warnings.Add(redraw);
        log.Information($"Saved body physics for c{race:D4} into {saved.Directory} ({saved.RelativePath}).");
        return new PhysicsSaveResult(saved.Directory, warnings, turnedOff);
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
    /// Turns every mod the builder saved off in your collection, so your other physics mods apply
    /// again, and redraws your character. Stops the preview first, which sits above every mod and
    /// would still show. Returns the folders turned off and a warning, or null.
    /// </summary>
    public async Task<(IReadOnlyList<string> TurnedOff, string? Warning)> TurnOffModsAsync(PhysicsTarget target)
    {
        await FollowModsAsync(lookAgain: true).ConfigureAwait(false);
        string? stopProblem = null;
        try { await Preview.StopAsync(redrawObject: null).ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or InvalidOperationException) { stopProblem = e.Message; }
        var turnedOff = await physicsMods.TurnOffPhysicsModsAsync(ModDirectories, target.Character.ObjectIndex).ConfigureAwait(false);
        var redraw = await penumbra.RedrawObjectAsync(target.Character.ObjectIndex).ConfigureAwait(false);
        if (stopProblem is null)
            return (turnedOff, redraw);
        var still = $"The preview is still on: {stopProblem}";
        return (turnedOff, redraw is null ? still : $"{still} {redraw}");
    }

    // The mods a look for their renamed folder last found nothing for.
    private readonly HashSet<(string Directory, Guid Identifier)> lostMods = [];

    /// <summary>
    /// Follows your physics mods when they were renamed in Penumbra: finds the folder with each one's
    /// identifier and remembers it, so saves keep going into it. Unless <paramref name="lookAgain"/>, a
    /// mod that's gone is only looked for once, so reading your character again stays quick.
    /// </summary>
    private async Task FollowModsAsync(bool lookAgain)
    {
        var moved = new List<(PhysicsSavedMod Mod, string Folder)>();
        foreach (var mod in config.PhysicsMods)
        {
            var known = (mod.Directory, mod.Identifier);
            lock (lostMods)
                if (!lookAgain && lostMods.Contains(known))
                    continue;
            var found = await physicsMods.FindPhysicsModAsync(known.Directory, known.Identifier).ConfigureAwait(false);
            lock (lostMods)
                if (found is null)
                    lostMods.Add(known);
                else
                    lostMods.Remove(known);
            if (found is not null && found != known.Directory)
                moved.Add((mod, found));
        }
        if (moved.Count == 0)
            return;
        await framework.RunOnFrameworkThread(() =>
        {
            // A save in the meantime may have replaced the list; only mods still in it move.
            var mods = config.PhysicsMods.Select(mod => moved.FirstOrDefault(pair => ReferenceEquals(pair.Mod, mod)) is { Mod: not null } pair
                ? new PhysicsSavedMod { Directory = pair.Folder, Identifier = mod.Identifier }
                : mod).ToList();
            config.PhysicsMods = mods;
            saveConfig();
        }).ConfigureAwait(false);
        foreach (var (mod, folder) in moved)
            log.Information($"Your physics mod was renamed from {mod.Directory} to {folder}.");
    }

    public void Dispose() => Preview.Dispose();
}
