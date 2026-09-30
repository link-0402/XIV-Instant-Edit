using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using InstantEdit.Models;
using InstantEdit.Services;
using InstantEdit.Services.CharacterSend;
using InstantEdit.Services.Skeletons;
using Lumina.Data;

namespace InstantEdit.Ui;

public sealed partial class MainWindow
{
    private ModelSkeletonResolver? _skeletons;

    internal void AttachSkeletons(ModelSkeletonResolver? value) => _skeletons = value;

    /// <summary>
    /// The model's game skeleton for Blender's generated armature, and a warning for the status
    /// line. Nothing for an existing armature, or when the add-on can't take a skeleton; Blender
    /// then gives the model's bones no rest pose, as before. A racially scaled model gets the
    /// skeleton of the race it was scaled for.
    /// </summary>
    private async Task<(ModelSkeletonPayload? Skeleton, string? Warning)> ResolveSkeletonAsync(
        ActorView actor, MdlFile model, int blenderPort, BlenderImportOptions importOptions,
        RacialScaling? scaling, CancellationToken cancellationToken)
    {
        var resolver = _skeletons;
        var modelPath = scaling is null ? model.GamePath : ModelSkeletonPaths.WithRace(model.GamePath, scaling.CharacterRace);
        if (resolver is null || importOptions.ArmatureMode == BlenderImportOptions.ExistingMode ||
            ModelSkeletonPaths.Parse(modelPath) is null ||
            !await _blender.SupportsImportSkeletonAsync(blenderPort, cancellationToken).ConfigureAwait(false))
            return (null, null);
        try
        {
            // An on-screen character lends its live skeleton; a browsed mod uses the files.
            var result = actor.Entity is { } entity
                ? await resolver.ResolveAsync(modelPath, entity.ObjectIndex, entity.Address, cancellationToken).ConfigureAwait(false)
                : await resolver.ResolveAsync(modelPath, actor.ImportObjectIndex, 0, cancellationToken).ConfigureAwait(false);
            return (result.Skeleton?.ToPayload(), result.Problem ?? result.Skeleton?.Warnings.FirstOrDefault());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            _log.Warning(e, "Could not resolve the model's game skeleton.");
            return (null, "the game skeleton could not be read, so its bones have no rest pose.");
        }
    }

    /// <summary>
    /// With racial scaling on, how a model of another race is reshaped for the character it is sent
    /// for: the on-screen actor, or for browsed mods and game files the character they import for.
    /// Null for models of the character's own race, and with a note when it can't be scaled.
    /// </summary>
    private async Task<(RacialScaling? Scaling, string? Warning)> ResolveRacialScalingAsync(
        ActorView actor, string modelPath, CancellationToken cancellationToken)
    {
        if (!_config.ApplyRacialScaling || _skeletons is not { } resolver || ModelSkeletonPaths.Parse(modelPath) is not { Human: true })
            return (null, null);
        var (source, problem) = actor.Entity is { } entity
            ? await resolver.RacialScalingAsync(entity.ObjectIndex, entity.Address, cancellationToken).ConfigureAwait(false)
            : await resolver.RacialScalingAsync(actor.ImportObjectIndex, 0, cancellationToken).ConfigureAwait(false);
        return RacialScalingFor(source, problem, modelPath);
    }

    /// <summary>
    /// With racial scaling on, how a model is reshaped by a character's racial scaling
    /// <paramref name="source"/>, or <paramref name="problem"/> when the character has none.
    /// </summary>
    private (RacialScaling? Scaling, string? Warning) RacialScalingFor(RacialScalingSource? source, string? problem, string modelPath)
    {
        if (!_config.ApplyRacialScaling || ModelSkeletonPaths.Parse(modelPath) is not { Human: true })
            return (null, null);
        if (source is null)
            return (null, problem);
        try
        {
            return (source.For(modelPath), null);
        }
        catch (InvalidDataException e)
        {
            return (null, e.Message);
        }
    }

    private void TryEditNode(ResourceView node, ActorView actor)
    {
        if (!node.IsModel || !ResourceViews.IsSafeModel(node))
        {
            SetStatus("Only safe .mdl resources can be imported.", FeedbackSeverity.Warning);
            return;
        }

        var model = new MdlFile { GamePath = node.GamePath, LocalPath = node.ActualPath };
        EditModel(actor, model, node);
    }

    private void RequestRefresh() { try { SetStatus(string.Empty, FeedbackSeverity.Success); _onScreen.RequestRefresh(); } catch (Exception e) { _log.Debug(e.Message); SetStatus("Refresh unavailable. Is Penumbra running?", FeedbackSeverity.Warning); } }
    private void EditModel(ActorView actor, MdlFile model, ResourceView source)
    {
        if (Interlocked.CompareExchange(ref _editing, 1, 0) != 0)
        {
            SetStatus("Another model is already being sent.", FeedbackSeverity.Warning);
            return;
        }

        var importOptions = CurrentImportOptions();
        var cancellationToken = _lifetimeCts.Token;
        _ = Task.Run(() => EditModelAsync(
            actor,
            model,
            source,
            _config.BlenderPort,
            _config.ListenPort,
            importOptions,
            cancellationToken));
    }

    public void ReportImportFailure(BridgeFailure failure)
    {
        var message = failure.UserMessage;
        SetStatus($"Failed: {message}", FeedbackSeverity.Error);
        _chat.PrintError($"XIV Instant Edit: {message}");
    }

    private BlenderImportOptions CurrentImportOptions()
        => _config.UseExistingSkeleton
            ? BlenderImportOptions.Existing(
                _config.SkeletonObjectName,
                _config.ApplyTexturesAndMaterials,
                _config.ExcludeBodyAndGeneralMaterials)
            : BlenderImportOptions.GeneratedWithPreview(
                _config.ApplyTexturesAndMaterials,
                _config.ExcludeBodyAndGeneralMaterials);

    private async Task EditModelAsync(
        ActorView actor,
        MdlFile model,
        ResourceView source,
        int blenderPort,
        int listenPort,
        BlenderImportOptions importOptions,
        CancellationToken cancellationToken)
    {
        try
        {
            await EnsureBlenderReadyForImportAsync(
                blenderPort, importOptions, source.SourceState == ResourceSourceState.GameData, cancellationToken).ConfigureAwait(false);
            var sent = await SendModelToBlenderAsync(
                actor, model, source, blenderPort, listenPort, importOptions, null, cancellationToken).ConfigureAwait(false);
            SetStatus(sent.Status, sent.HasWarning ? FeedbackSeverity.Warning : FeedbackSeverity.Success);
            _chat.Print($"XIV Instant Edit: {model.FileName} sent to Blender.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Plugin/window shutdown cancels outstanding handoffs without touching UI services.
        }
        catch (Exception e)
        {
            _log.Error(e, "Failed to send model to Blender.");
            SetStatus($"Failed: {e.Message}", FeedbackSeverity.Error);
            _chat.PrintError($"XIV Instant Edit: could not send model to Blender: {e.Message}");
        }
        finally
        {
            Volatile.Write(ref _editing, 0);
        }
    }

    /// <summary>
    /// Throws with a user-facing message unless Blender runs this version of the add-on, shares the
    /// plugin's cache, and takes imports with these options (and of game data, with <paramref name="gameData"/>).
    /// </summary>
    private async Task EnsureBlenderReadyForImportAsync(int blenderPort, BlenderImportOptions importOptions, bool gameData,
        CancellationToken cancellationToken)
    {
        var blenderStatus = await CheckBlenderStatusAsync(blenderPort, cancellationToken).ConfigureAwait(false);
        if (!blenderStatus.Reachable)
            throw new InvalidOperationException("Blender is offline. Start Blender and enable the XIV Instant Edit add-on before editing.");
        if (blenderStatus.Classify(_pluginVersion) != BlenderConnectionState.Online)
            throw new InvalidOperationException(BlenderClient.VersionMismatchMessage(_pluginVersion));
        if (!await SynchronizeBlenderCacheAsync(blenderStatus, blenderPort, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Blender's cache could not be synchronized. Update and restart the XIV Instant Edit add-on, then retry.");
        if (importOptions.ArmatureMode == BlenderImportOptions.ExistingMode &&
            !await _blender.SupportsImportOptionsAsync(blenderPort, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("The XIV Instant Edit add-on is too old for custom import options. Update the add-on and restart Blender.");
        if (importOptions.ApplyTexturesAndMaterials &&
            !await _blender.SupportsMaterialPreviewAsync(blenderPort, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("The XIV Instant Edit add-on is too old for texture and material previews. Update the add-on and restart Blender.");
        if (gameData && !await _blender.SupportsVanillaContextAsync(blenderPort, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("The XIV Instant Edit add-on is too old for vanilla model contexts. Update the add-on and restart Blender.");
    }

    /// <summary> What sending one model to Blender produced. </summary>
    /// <param name="Status">The status line a send of this model alone reports.</param>
    /// <param name="Notes">Its warnings, each on its own, for the summary of a send of several models.</param>
    private sealed record ModelSendResult(string FileName, string Status, bool HasWarning, IReadOnlyList<string> Notes, bool Scaled);

    /// <summary>
    /// A model of a whole-character send: its entry, the character's skeleton for body models
    /// (a weapon's own is looked up), what the send resolved once for all of its models, and the
    /// model file the send already read.
    /// </summary>
    private sealed record CharacterModelSend(
        CharacterImportEntry Entry,
        ModelSkeletonPayload? Skeleton,
        RacialScalingSource? Scaling,
        string? ScalingProblem,
        IReadOnlyCollection<MaterialResourceCandidate> PreviewResources,
        PenumbraCollectionTarget? Collection,
        byte[]? Bytes = null);

    /// <summary>
    /// Sends one model to Blender as an import with its own context. Throws with a user-facing
    /// message when it can't be sent. Blender's readiness is checked by the caller.
    /// </summary>
    private async Task<ModelSendResult> SendModelToBlenderAsync(
        ActorView actor,
        MdlFile model,
        ResourceView source,
        int blenderPort,
        int listenPort,
        BlenderImportOptions importOptions,
        CharacterModelSend? character,
        CancellationToken cancellationToken)
    {
        string? handoffDirectory = null;
        var handoffCached = false;
        try
        {
            var bytes = character?.Bytes ?? await ReadModelBytesAsync(model, cancellationToken).ConfigureAwait(false);
            // Blender gets the model as the character wears it, for preview: the import context
            // records the scaling, and the plugin refuses every export from it.
            var (scaling, scalingWarning) = character is null
                ? await ResolveRacialScalingAsync(actor, model.GamePath, cancellationToken).ConfigureAwait(false)
                : RacialScalingFor(character.Scaling, character.ScalingProblem, model.GamePath);
            if (scaling is not null)
            {
                try
                {
                    bytes = RacialScalingModel.Apply(bytes, scaling.Deformer);
                }
                catch (Exception e) when (e is InvalidDataException or NotSupportedException)
                {
                    scalingWarning = e.Message;
                    scaling = null;
                }
            }
            CleanupStaleHandoffs();
            var handoffRoot = Path.Combine(Path.GetTempPath(), "InstantEdit", "handoff");
            Directory.CreateDirectory(handoffRoot);
            var dir = Path.Combine(handoffRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            handoffDirectory = dir;
            var file = Path.Combine(dir, model.FileName);
            await File.WriteAllBytesAsync(file, bytes).ConfigureAwait(false);
            var resources = source.SourceState == ResourceSourceState.GameData
                ? Array.Empty<MaterialResourceCandidate>()
                : character?.PreviewResources ?? await ResolvePreviewResourcesAsync(actor).ConfigureAwait(false);
            var previewModelPath = source.SourceState == ResourceSourceState.GameData
                ? model.LocalPath
                : model.GamePath;
            var materialBundle = await _materialPreviews.BuildImportBundleAsync(
                bytes,
                previewModelPath,
                resources,
                Path.Combine(dir, "preview"),
                importOptions.ApplyTexturesAndMaterials,
                importOptions.ExcludeBodyAndGeneralMaterials,
                cancellationToken,
                actor.Entity is null ? source.OptionMemberships : null).ConfigureAwait(false);
            var preview = materialBundle.Preview;
            var resourceManifest = materialBundle.ResourceManifest;
            var dependencyWarnings = materialBundle.DependencyWarnings.ToList();
            var collection = character is not null
                ? character.Collection
                : await _penumbra.GetCollectionTargetAsync(actor.ImportObjectIndex).ConfigureAwait(false);
            if (source.SourceState != ResourceSourceState.GameData && resourceManifest is not null)
            {
                var manipulations = collection is not null &&
                                    !string.IsNullOrWhiteSpace(source.SourceModRootPath)
                    ? await _penumbra.CaptureEffectiveManipulationsAsync(
                        collection.Id,
                        source.SourceModDirectory,
                        source.SourceModRootPath).ConfigureAwait(false)
                    : null;
                if (manipulations is null)
                {
                    dependencyWarnings.Add(
                        "Could not snapshot the source mod's effective Meta Manipulations");
                    resourceManifest = null;
                }
                else
                {
                    resourceManifest = resourceManifest with
                    {
                        Manipulations = manipulations,
                    };
                }
            }
            // A character's body models share the armature built from the character's whole skeleton.
            var (skeleton, skeletonWarning) = character is { Entry.Role: CharacterImportEntry.BodyRole }
                ? (character.Skeleton, null)
                : await ResolveSkeletonAsync(
                    actor, model, blenderPort, importOptions, scaling, cancellationToken).ConfigureAwait(false);
            if (source.SourceState == ResourceSourceState.GameData)
            {
                handoffCached = await _blender.SendGameImportAsync(
                    blenderPort,
                    file,
                    model.GamePath,
                    model.LocalPath,
                    actor.ImportObjectIndex,
                    model.FileName,
                    listenPort,
                    collection?.Id,
                    collection?.Name,
                    importOptions: importOptions,
                    previewManifestPath: preview?.ManifestPath,
                    resourceManifest: resourceManifest,
                    skeleton: skeleton,
                    racialScaling: scaling?.ToRecord(),
                    character: character?.Entry,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var sourceOption = await _penumbra.CaptureSourceOptionAsync(
                    source.SourceModDirectory,
                    source.ActualPath,
                    source.SourceModRootPath,
                    source.SourceRelativePath,
                    model.GamePath,
                    source.OptionMemberships,
                    collection?.Id,
                    source.SourceModStableId).ConfigureAwait(false);
                if (sourceOption.Warning is not null)
                    dependencyWarnings.Add($"Source option: {sourceOption.Warning}");
                handoffCached = await _blender.SendSourceImportAsync(
                    blenderPort,
                    file,
                    model.GamePath,
                    actor.ImportObjectIndex,
                    model.FileName,
                    listenPort,
                    source.ActualPath,
                    source.SourceModDirectory,
                    source.SourceModName,
                    importOptions: importOptions,
                    previewManifestPath: preview?.ManifestPath,
                    sourceModRootPath: source.SourceModRootPath,
                    targetRelativePath: source.SourceRelativePath,
                    resourceManifest: resourceManifest,
                    targetCollectionId: collection?.Id,
                    targetCollectionName: collection?.Name,
                    sourceOption: sourceOption.Locator,
                    sourceOptionStatus: sourceOption.Status,
                    sourceModStableId: source.SourceModStableId,
                    skeleton: skeleton,
                    racialScaling: scaling?.ToRecord(),
                    character: character?.Entry,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            var notes = new List<string>();
            if (preview is { Warnings.Count: > 0 })
                notes.Add($"Preview warning: {preview.WarningSummary}");
            if (resourceManifest is null)
                notes.Add($"Mashup warning: {(dependencyWarnings.Count > 0
                    ? string.Join("; ", dependencyWarnings.Take(3))
                    : "exact material/texture sources could not be captured; re-import after resolving the missing resources.")}");
            if (skeletonWarning is not null)
                notes.Add($"Skeleton: {skeletonWarning}");
            if (scalingWarning is not null)
                notes.Add($"Not racially scaled: {scalingWarning.TrimEnd('.')}.");
            var scaled = scaling is null ? string.Empty : $", scaled from {scaling.Description} for preview (it can't be exported)";
            var status = $"Sent {model.FileName} to Blender{scaled}.{string.Concat(notes.Select(note => " " + note))}";
            return new ModelSendResult(model.FileName, status, notes.Count > 0, notes, scaling is not null);
        }
        finally
        {
            if (handoffCached && handoffDirectory is not null)
                TryDeleteOwnedHandoff(handoffDirectory);
        }
    }

    /// <summary> The model's file: a mod file from disk, or a game file from the game's data. </summary>
    private async Task<byte[]> ReadModelBytesAsync(MdlFile model, CancellationToken cancellationToken)
        => model.IsFilePath
            ? await File.ReadAllBytesAsync(model.LocalPath, cancellationToken).ConfigureAwait(false)
            : (await _data.GetFileAsync<FileResource>(model.LocalPath, cancellationToken).ConfigureAwait(false))?.Data
                ?? throw new InvalidOperationException($"Game file not found: {model.LocalPath}");

    private static void CleanupStaleHandoffs()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "InstantEdit", "handoff"));
        if (!Directory.Exists(root))
            return;
        var cutoff = DateTime.UtcNow - TimeSpan.FromHours(24);
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(directory);
            if (name.Length != 32 || !Guid.TryParseExact(name, "N", out _))
                continue;
            try
            {
                if (Directory.GetLastWriteTimeUtc(directory) < cutoff)
                    TryDeleteOwnedHandoff(directory);
            }
            catch
            {
                // A locked or concurrently consumed handoff is retried later.
            }
        }
    }

    private static void TryDeleteOwnedHandoff(string directory)
    {
        try
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "InstantEdit", "handoff"))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var full = Path.GetFullPath(directory);
            var name = Path.GetFileName(full);
            if (name.Length != 32 || !Guid.TryParseExact(name, "N", out _) ||
                !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                return;
            Directory.Delete(full, true);
        }
        catch
        {
            // Cleanup is best-effort and never changes the import result.
        }
    }

    private async Task<IReadOnlyCollection<MaterialResourceCandidate>> ResolvePreviewResourcesAsync(ActorView actor)
    {
        if (actor.Entity is not null && actor.ImportObjectIndex is >= 0 and <= ushort.MaxValue)
        {
            var resolved = await _penumbra.GetResourcePathsAsync((ushort)actor.ImportObjectIndex).ConfigureAwait(false);
            if (resolved is not null)
            {
                return resolved.SelectMany(pair =>
                {
                    var attribution = _resourceSources.AttributionFor(pair.Key);
                    return pair.Value
                        .Where(gamePath => gamePath.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase) ||
                                           gamePath.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
                        .Select(gamePath => new MaterialResourceCandidate(
                            gamePath,
                            pair.Key,
                            attribution.State == ResourceSourceState.LoadedMod ? attribution.ModDirectory : null,
                            attribution.State == ResourceSourceState.LoadedMod ? attribution.ModRootPath : null,
                            attribution.State == ResourceSourceState.LoadedMod ? attribution.RelativePath : null,
                            SourceModStableId: attribution.State == ResourceSourceState.LoadedMod ? attribution.ModStableId : null));
                }).ToArray();
            }
        }

        return actor.Roots
            .SelectMany(ResourceViews.Flatten)
            .Where(resource => resource.GamePath.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase) ||
                               resource.GamePath.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
            .Select(resource => new MaterialResourceCandidate(
                resource.GamePath,
                resource.ActualPath,
                resource.SourceModDirectory,
                resource.SourceModRootPath,
                resource.SourceRelativePath,
                resource.OptionMemberships,
                resource.OptionMapping,
                resource.SourceModStableId))
            .ToArray();
    }
}
