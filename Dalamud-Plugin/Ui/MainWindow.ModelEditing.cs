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
using Lumina.Data;

namespace InstantEdit.Ui;

public sealed partial class MainWindow
{
    private void TryEditNode(ResourceView node, ActorView actor)
    {
        if (!IsModel(node) || !IsSafeModel(node))
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
        string? handoffDirectory = null;
        var handoffCached = false;
        try
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
            if (source.SourceState == ResourceSourceState.GameData &&
                !await _blender.SupportsVanillaContextAsync(blenderPort, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("The XIV Instant Edit add-on is too old for vanilla model contexts. Update the add-on and restart Blender.");

            var bytes = model.IsFilePath
                ? await File.ReadAllBytesAsync(model.LocalPath, cancellationToken).ConfigureAwait(false)
                : (await _data.GetFileAsync<FileResource>(model.LocalPath, cancellationToken).ConfigureAwait(false))?.Data
                    ?? throw new InvalidOperationException($"Game file not found: {model.LocalPath}");
            CleanupStaleHandoffs();
            var handoffRoot = Path.Combine(Path.GetTempPath(), "InstantEdit", "handoff");
            Directory.CreateDirectory(handoffRoot);
            var dir = Path.Combine(handoffRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            handoffDirectory = dir;
            var file = Path.Combine(dir, $"{Sanitize(actor.Name)}-{actor.ImportObjectIndex}-{model.FileName}");
            await File.WriteAllBytesAsync(file, bytes).ConfigureAwait(false);
            var resources = source.SourceState == ResourceSourceState.GameData
                ? Array.Empty<MaterialResourceCandidate>()
                : await ResolvePreviewResourcesAsync(actor).ConfigureAwait(false);
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
            var collection = await _penumbra.GetCollectionTargetAsync(
                actor.ImportObjectIndex).ConfigureAwait(false);
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
            if (source.SourceState == ResourceSourceState.GameData)
            {
                handoffCached = await _blender.SendGameImportAsync(
                    blenderPort,
                    file,
                    model.GamePath,
                    model.LocalPath,
                    actor.ImportObjectIndex,
                    $"{actor.Name} {model.FileName}",
                    listenPort,
                    collection?.Id,
                    collection?.Name,
                    importOptions: importOptions,
                    previewManifestPath: preview?.ManifestPath,
                    resourceManifest: resourceManifest,
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
                    $"{actor.Name} {model.FileName}",
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
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            var hasPreviewWarning = preview is { Warnings.Count: > 0 };
            var hasMashupWarning = resourceManifest is null;
            var warning = hasPreviewWarning ? $" Preview warning: {preview!.WarningSummary}" : string.Empty;
            var mashupWarning = hasMashupWarning
                ? $" Mashup warning: {(dependencyWarnings.Count > 0
                    ? string.Join("; ", dependencyWarnings.Take(3))
                    : "exact material/texture sources could not be captured; re-import after resolving the missing resources.")}"
                : string.Empty;
            var hasWarning = hasPreviewWarning || hasMashupWarning;
            var status = actor.Entity is null && hasWarning
                ? $"Sent {model.FileName} to Blender. {ModBrowserAmbiguityWarning}"
                : $"Sent {model.FileName} to Blender.{warning}{mashupWarning}";
            SetStatus(status, hasWarning ? FeedbackSeverity.Warning : FeedbackSeverity.Success);
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
            if (handoffCached && handoffDirectory is not null)
                TryDeleteOwnedHandoff(handoffDirectory);
            Volatile.Write(ref _editing, 0);
        }
    }

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
            .SelectMany(Flatten)
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
