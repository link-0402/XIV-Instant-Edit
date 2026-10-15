using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using InstantEdit.Models;
using InstantEdit.Services.PenumbraMods;
using InstantEdit.Services.PreviewMods;
using Penumbra.Api.Enums;
using Penumbra.Api.Helpers;
using Penumbra.Api.IpcSubscribers;

namespace InstantEdit.Services;

/// <summary> Result of applying an export to Penumbra. </summary>
public sealed record ExportResult(
    bool Success,
    string Code,
    string Message,
    IReadOnlyList<string>? Warnings = null,
    string? TargetFilePath = null,
    string? DestinationName = null,
    ModPathRemap? PathRemap = null,
    IReadOnlyList<string>? RequiredExternalMods = null,
    string? OutputModDirectory = null,
    string? OutputModRootPath = null,
    string? OutputTargetRelativePath = null,
    ResourceDependencyManifest? OutputResourceManifest = null,
    Guid? OutputModStableId = null)
{
    public ExportResult(bool success, string message)
        : this(success, success ? "export_applied" : "apply_failed", message)
    {
    }

    public IReadOnlyList<string> WarningList => Warnings ?? Array.Empty<string>();
}

/// <summary>Physical paths changed by a successful in-place mod cleanup.</summary>
public sealed record ModPathRemap(
    string ModDirectory,
    string ModRoot,
    IReadOnlyDictionary<string, string> RelativePaths);

public sealed record VariantOptionTarget(
    string Id, string Name, string ModelPath, string? BackupTargetId = null, string? BackupDirectory = null);
public sealed record VariantGroupTarget(string Id, string Name, IReadOnlyList<VariantOptionTarget> Options);
public sealed record VariantTargetsResult(bool Success, string Code, string Message, IReadOnlyList<VariantGroupTarget> Groups);
public sealed record PenumbraCollectionTarget(Guid Id, string Name);
public sealed record NewModelModResult(
    ExportResult Result,
    string? ModRoot = null,
    string? TargetRelativePath = null,
    Guid? ModStableId = null);
public sealed record MashupContributor(InstantEditImportContext Context, IReadOnlyList<string> Materials);
public sealed record MashupMaterialAssignment(
    string ContextId,
    string ModelMaterial,
    string Alias,
    string GamePath,
    string? Slot);
public sealed record MashupPlanResult(
    bool Success,
    string Code,
    string Message,
    IReadOnlyList<MashupMaterialAssignment> Assignments,
    string? Fingerprint = null);
public sealed record MaterialCoverageMissing(
    string ContextId,
    string SourceModName,
    string ModelMaterial,
    string GamePath,
    string ResourceType = "material");
public sealed record MaterialCoverageResult(
    bool Available,
    bool Covered,
    string Code,
    string Message,
    IReadOnlyList<MaterialCoverageMissing> Missing);
public sealed record SourceOptionCapture(SourceOptionLocator? Locator, string Status, string? Warning = null);

/// <summary>A size's exported model: its option name and the staged file.</summary>
public sealed record SizeExportFile(string Name, string ExportedFile);

public sealed record PenumbraMod(string Directory, string Name, Guid? StableId = null);

public sealed record PenumbraModResource(
    string GamePath,
    string ActualPath,
    string RelativePath,
    string OptionMapping,
    IReadOnlyList<string> OptionMemberships);

public sealed record PenumbraModSnapshot(
    string Directory,
    string Name,
    string RootPath,
    IReadOnlyList<PenumbraModResource> Resources,
    Guid? StableId = null);

/// <summary>
/// An EST entry the exported meshes need, from the <c>xiv_est_hair</c>/<c>xiv_est_race</c> tags
/// Magic Fit's Hair Weights puts on hair it weights: the hair skeleton (<paramref name="Entry"/>,
/// <c>skl_c0801h0160.sklb</c> for 160) of race <see cref="Race"/> (<c>c0801</c>).
/// </summary>
public sealed class EstEntryRequest
{
    [JsonPropertyName("slot")]
    public string? Slot { get; set; }

    [JsonPropertyName("entry")]
    public int Entry { get; set; }

    [JsonPropertyName("race")]
    public string? Race { get; set; }
}

/// <summary>
/// XIV Instant Edit's way into Penumbra. It builds the Penumbra IPC client and the mod services of
/// InstantEdit.Services.PenumbraMods around one export gate, and keeps the API its callers use by
/// forwarding to them: the work happens in the services.
/// </summary>
public sealed class PenumbraService : IDisposable, ITextureEditBackend
{
    private readonly PenumbraIpc _ipc;
    private readonly IFramework _framework;
    private readonly IPluginLog _log;
    /// <summary> One mod write at a time, across every service that writes into Penumbra's mods. </summary>
    private readonly SemaphoreSlim _exportGate = new(1, 1);
    private readonly ModActivation _activation;
    private readonly SourceMods _sources;
    private readonly ModelExportService _exports;
    private readonly RaceExportService _race;
    private readonly SizesExportService _sizes;
    private readonly ModelBackupService _modelBackups;
    private readonly MashupService _mashups;
    private readonly ModResourceScanner _scanner;
    private readonly TextureModBackend _textures;
    private readonly ModFileService _modFiles;
    private readonly PreviewModWriter _previews;
    private readonly AnimationModService _animations;

    public PenumbraService(
        IDalamudPluginInterface pi,
        IFramework framework,
        IPluginLog log,
        IObjectTable objects,
        IDataManager data,
        ModelBackupStore backups,
        EstEntryStore estEntries)
    {
        _framework = framework;
        _log = log;
        _ipc = new PenumbraIpc(pi, framework, log);
        _activation = new ModActivation(_ipc, framework, log, objects);
        _sources = new SourceMods(_ipc, framework, log);
        var writer = new ModelFileWriter(backups);
        var attributes = new AttributeGroupService(data, log);
        var est = new EstEntryService(estEntries, log);
        _race = new RaceExportService(framework, log, _exportGate, data, backups, _sources, _activation, writer, attributes, est);
        _sizes = new SizesExportService(framework, log, _exportGate, _sources, _activation, writer, attributes, est);
        _exports = new ModelExportService(_ipc, framework, log, _exportGate, backups, _sources, _activation, writer, attributes,
            est, _race);
        _modelBackups = new ModelBackupService(framework, log, _exportGate, backups, _sources, _activation, writer, est, _race);
        _mashups = new MashupService(_ipc, framework, log, _exportGate, data, _sources, _activation, attributes, est);
        _scanner = new ModResourceScanner(framework, log, _sources);
        _textures = new TextureModBackend(_ipc, framework, objects, data, backups, _exportGate, _sources, _activation);
        _modFiles = new ModFileService(framework, _exportGate, _sources, _activation);
        _previews = new PreviewModWriter(_ipc, framework, log, objects, backups, _exportGate, _sources, _activation);
        _animations = new AnimationModService(_ipc, framework, objects, _exportGate, _activation);
        Physics = new PhysicsModService(_ipc, framework, log, _exportGate, _activation);
    }

    internal PenumbraIpc Ipc => _ipc;
    internal PreviewModWriter Previews => _previews;
    internal PhysicsModService Physics { get; }

    public void Dispose() => _ipc.Dispose();

    // Penumbra's state and events (PenumbraIpc).

    /// <inheritdoc cref="PenumbraIpc.Available"/>
    public bool Available => _ipc.Available;

    /// <inheritdoc cref="PenumbraIpc.ResourcesChanged"/>
    public event Action? ResourcesChanged
    {
        add => _ipc.ResourcesChanged += value;
        remove => _ipc.ResourcesChanged -= value;
    }

    /// <inheritdoc cref="PenumbraIpc.ModsChanged"/>
    public event Action? ModsChanged
    {
        add => _ipc.ModsChanged += value;
        remove => _ipc.ModsChanged -= value;
    }

    /// <inheritdoc cref="PenumbraIpc.ObjectRedrawn"/>
    internal event Action<int>? ObjectRedrawn
    {
        add => _ipc.ObjectRedrawn += value;
        remove => _ipc.ObjectRedrawn -= value;
    }

    /// <inheritdoc cref="PenumbraIpc.GetResourcePaths(ushort)"/>
    public Dictionary<string, HashSet<string>>? GetResourcePaths(ushort gameObjectIndex) => _ipc.GetResourcePaths(gameObjectIndex);

    /// <inheritdoc cref="PenumbraIpc.GetResourcePaths(ushort[])"/>
    public Dictionary<string, HashSet<string>>?[] GetResourcePaths(ushort[] gameObjectIndices) => _ipc.GetResourcePaths(gameObjectIndices);

    /// <inheritdoc cref="PenumbraIpc.GetResourcePathsAsync"/>
    public Task<Dictionary<string, HashSet<string>>?> GetResourcePathsAsync(ushort gameObjectIndex)
        => _ipc.GetResourcePathsAsync(gameObjectIndex);

    /// <inheritdoc cref="PenumbraIpc.GetPlayerResourceTrees"/>
    public IReadOnlyDictionary<ushort, ResourceTreeDto> GetPlayerResourceTrees() => _ipc.GetPlayerResourceTrees();

    /// <inheritdoc cref="PenumbraIpc.GetPlayerResourcePaths"/>
    public IReadOnlyDictionary<ushort, Dictionary<string, HashSet<string>>> GetPlayerResourcePaths() => _ipc.GetPlayerResourcePaths();

    /// <inheritdoc cref="PenumbraIpc.GetResourceTrees"/>
    public ResourceTreeDto?[] GetResourceTrees(ushort[] gameObjectIndices) => _ipc.GetResourceTrees(gameObjectIndices);

    public Task<PenumbraCollectionTarget?> GetCollectionTargetAsync(int gameObjectIndex) => _ipc.GetCollectionTargetAsync(gameObjectIndex);

    public string? GetModDirectory() => _ipc.GetModDirectory();

    public Dictionary<string, string> GetModList() => _ipc.GetModList();

    /// <inheritdoc cref="PenumbraIpc.TryGetModList"/>
    internal bool TryGetModList(out Dictionary<string, string> modList) => _ipc.TryGetModList(out modList);

    public IReadOnlyList<PenumbraMod> GetMods() => _ipc.GetMods();

    /// <inheritdoc cref="PenumbraIpc.GetRegisteredModPath"/>
    public string? GetRegisteredModPath(string modDirectory) => _ipc.GetRegisteredModPath(modDirectory);

    /// <inheritdoc cref="PenumbraIpc.OpenModInPenumbra"/>
    public void OpenModInPenumbra(string modDirectory, string modName) => _ipc.OpenModInPenumbra(modDirectory, modName);

    /// <inheritdoc cref="PenumbraIpc.ResolveCollectionPathsAsync"/>
    internal Task<string?[]> ResolveCollectionPathsAsync(Guid collection, IReadOnlyList<string> gamePaths)
        => _ipc.ResolveCollectionPathsAsync(collection, gamePaths);

    /// <inheritdoc cref="PenumbraIpc.MetaManipulationsAsync"/>
    internal Task<string?> MetaManipulationsAsync(int objectIndex) => _ipc.MetaManipulationsAsync(objectIndex);

    // Temporary mods (PenumbraIpc).

    /// <inheritdoc cref="PenumbraIpc.TemporaryModCollectionOnFramework"/>
    internal TemporaryModCollection? TemporaryModCollectionOnFramework(int objectIndex)
        => _ipc.TemporaryModCollectionOnFramework(objectIndex);

    internal Task<TemporaryModCollection?> TemporaryModCollectionAsync(int objectIndex) => _ipc.TemporaryModCollectionAsync(objectIndex);

    /// <inheritdoc cref="PenumbraIpc.AddTemporaryModOnFramework"/>
    internal PenumbraApiEc AddTemporaryModOnFramework(string tag, Guid collection, IReadOnlyDictionary<string, string> paths, int priority)
        => _ipc.AddTemporaryModOnFramework(tag, collection, paths, priority);

    /// <inheritdoc cref="PenumbraIpc.AddTemporaryModAllOnFramework"/>
    internal PenumbraApiEc AddTemporaryModAllOnFramework(string tag, IReadOnlyDictionary<string, string> paths, int priority)
        => _ipc.AddTemporaryModAllOnFramework(tag, paths, priority);

    /// <inheritdoc cref="PenumbraIpc.ResolveInterfacePathOnFramework"/>
    internal string ResolveInterfacePathOnFramework(string gamePath) => _ipc.ResolveInterfacePathOnFramework(gamePath);

    /// <inheritdoc cref="PenumbraIpc.RemoveTemporaryModAllOnFramework"/>
    internal (bool Gone, string? Error) RemoveTemporaryModAllOnFramework(string tag, int priority)
        => _ipc.RemoveTemporaryModAllOnFramework(tag, priority);

    /// <inheritdoc cref="PenumbraIpc.RemoveTemporaryModOnFramework"/>
    internal (bool Gone, string? Error) RemoveTemporaryModOnFramework(string tag, Guid collection, int priority)
        => _ipc.RemoveTemporaryModOnFramework(tag, collection, priority);

    internal Task<(bool Gone, string? Error)> RemoveTemporaryModAsync(string tag, Guid collection, int priority)
        => _ipc.RemoveTemporaryModAsync(tag, collection, priority);

    /// <inheritdoc cref="PenumbraIpc.RemoveTemporaryModEverywhereOnFramework"/>
    internal (bool Gone, string? Error) RemoveTemporaryModEverywhereOnFramework(string tag, int priority)
        => _ipc.RemoveTemporaryModEverywhereOnFramework(tag, priority);

    internal Task<(bool Gone, string? Error)> RemoveTemporaryModEverywhereAsync(string tag, int priority)
        => _ipc.RemoveTemporaryModEverywhereAsync(tag, priority);

    /// <inheritdoc cref="PenumbraIpc.SubscribePenumbraLifecycle"/>
    internal IDisposable SubscribePenumbraLifecycle(Action restarted) => _ipc.SubscribePenumbraLifecycle(restarted);

    // Imports (SourceMods, ModResourceScanner).

    /// <inheritdoc cref="SourceMods.CaptureEffectiveManipulationsAsync"/>
    public Task<JsonArray?> CaptureEffectiveManipulationsAsync(Guid collectionId, string modDirectory, string modRoot)
        => _sources.CaptureEffectiveManipulationsAsync(collectionId, modDirectory, modRoot);

    /// <inheritdoc cref="SourceMods.CaptureSourceOptionAsync"/>
    public Task<SourceOptionCapture> CaptureSourceOptionAsync(
        string sourceModDirectory, string sourceFilePath, string? sourceModRootPath,
        string? targetRelativePath, string sourceGamePath,
        IReadOnlyCollection<string>? preferredMemberships = null,
        Guid? collectionId = null,
        Guid? sourceModStableId = null)
        => _sources.CaptureSourceOptionAsync(sourceModDirectory, sourceFilePath, sourceModRootPath, targetRelativePath,
            sourceGamePath, preferredMemberships, collectionId, sourceModStableId);

    /// <inheritdoc cref="ModResourceScanner.GetModResourcesAsync"/>
    public Task<PenumbraModSnapshot?> GetModResourcesAsync(
        string modDirectory,
        CancellationToken cancellationToken = default,
        Guid? stableId = null)
        => _scanner.GetModResourcesAsync(modDirectory, cancellationToken, stableId);

    /// <inheritdoc cref="ModResourceScanner.GetMaterialCoverageAsync"/>
    public Task<MaterialCoverageResult> GetMaterialCoverageAsync(
        InstantEditImportContext activeContext,
        IReadOnlyList<MashupContributor> contributors,
        CancellationToken cancellationToken = default)
        => _scanner.GetMaterialCoverageAsync(activeContext, contributors, cancellationToken);

    // Model exports and their backups.

    /// <inheritdoc cref="ModelExportService.ApplySourceExportAsync"/>
    public Task<ExportResult> ApplySourceExportAsync(
        string sourceModDirectory,
        string sourceFilePath,
        string? sourceModRootPath,
        string? targetRelativePath,
        string sourceGamePath,
        string exportedFile,
        string? variantName,
        string? variantGroupName,
        string? variantTarget,
        string? variantTargetId,
        bool setupVariantInPenumbra,
        bool backupExisting = false,
        SourceOptionLocator? sourceOption = null,
        string sourceOptionStatus = "unknown",
        Guid? sourceModStableId = null,
        bool createAttributeGroups = false,
        IReadOnlyList<string>? attributeTags = null,
        IReadOnlyDictionary<string, int>? attributeMasks = null,
        string? resolvedGamePath = null,
        JsonArray? sourceManipulations = null,
        IReadOnlyList<EstEntryRequest>? estEntries = null)
        => _exports.ApplySourceExportAsync(sourceModDirectory, sourceFilePath, sourceModRootPath, targetRelativePath,
            sourceGamePath, exportedFile, variantName, variantGroupName, variantTarget, variantTargetId,
            setupVariantInPenumbra, backupExisting, sourceOption, sourceOptionStatus, sourceModStableId,
            createAttributeGroups, attributeTags, attributeMasks, resolvedGamePath, sourceManipulations, estEntries);

    /// <inheritdoc cref="ModelExportService.GetVariantTargetsAsync"/>
    public Task<VariantTargetsResult> GetVariantTargetsAsync(
        string sourceModDirectory,
        string sourceFilePath,
        string? sourceModRootPath,
        string? targetRelativePath,
        string sourceGamePath,
        Guid? sourceModStableId = null)
        => _exports.GetVariantTargetsAsync(sourceModDirectory, sourceFilePath, sourceModRootPath, targetRelativePath,
            sourceGamePath, sourceModStableId);

    /// <inheritdoc cref="ModelExportService.CreateGameModelModAsync"/>
    public Task<NewModelModResult> CreateGameModelModAsync(
        InstantEditImportContext context,
        string exportedFile,
        string modName,
        bool createAttributeGroups = false,
        IReadOnlyList<string>? attributeTags = null,
        IReadOnlyDictionary<string, int>? attributeMasks = null,
        IReadOnlyList<EstEntryRequest>? estEntries = null,
        int? race = null)
        => _exports.CreateGameModelModAsync(context, exportedFile, modName, createAttributeGroups, attributeTags,
            attributeMasks, estEntries, race);

    /// <inheritdoc cref="RaceExportService.ApplyRaceExportAsync"/>
    public Task<ExportResult> ApplyRaceExportAsync(
        string sourceModDirectory,
        string sourceFilePath,
        string? sourceModRootPath,
        string? targetRelativePath,
        string sourceGamePath,
        int race,
        string exportedFile,
        bool backupExisting = false,
        Guid? sourceModStableId = null,
        bool createAttributeGroups = false,
        IReadOnlyList<string>? attributeTags = null,
        IReadOnlyDictionary<string, int>? attributeMasks = null,
        string? resolvedGamePath = null,
        JsonArray? sourceManipulations = null,
        IReadOnlyList<EstEntryRequest>? estEntries = null)
        => _race.ApplyRaceExportAsync(sourceModDirectory, sourceFilePath, sourceModRootPath, targetRelativePath,
            sourceGamePath, race, exportedFile, backupExisting, sourceModStableId, createAttributeGroups, attributeTags,
            attributeMasks, resolvedGamePath, sourceManipulations, estEntries);

    /// <inheritdoc cref="SizesExportService.ApplySizesExportAsync"/>
    public Task<ExportResult> ApplySizesExportAsync(
        string sourceModDirectory,
        string sourceFilePath,
        string? sourceModRootPath,
        string? targetRelativePath,
        string sourceGamePath,
        IReadOnlyList<SizeExportFile> sizes,
        string defaultSize,
        string variantTarget,
        string? variantGroupName,
        string? variantTargetId,
        bool backupExisting = false,
        SourceOptionLocator? sourceOption = null,
        string sourceOptionStatus = "unknown",
        Guid? sourceModStableId = null,
        bool createAttributeGroups = false,
        IReadOnlyList<string>? attributeTags = null,
        IReadOnlyDictionary<string, int>? attributeMasks = null,
        string? resolvedGamePath = null,
        JsonArray? sourceManipulations = null,
        IReadOnlyList<EstEntryRequest>? estEntries = null)
        => _sizes.ApplySizesExportAsync(sourceModDirectory, sourceFilePath, sourceModRootPath, targetRelativePath,
            sourceGamePath, sizes, defaultSize, variantTarget, variantGroupName, variantTargetId, backupExisting,
            sourceOption, sourceOptionStatus, sourceModStableId, createAttributeGroups, attributeTags, attributeMasks,
            resolvedGamePath, sourceManipulations, estEntries);

    /// <inheritdoc cref="MashupService.ApplyMashupAsync"/>
    public Task<ExportResult> ApplyMashupAsync(
        InstantEditImportContext activeContext,
        IReadOnlyList<MashupContributor> contributors,
        MashupPlanResult plan,
        string exportedFile,
        string exportId,
        string destination,
        string name,
        bool bundleExternalDependencies = false,
        bool createAttributeGroups = false,
        IReadOnlyList<string>? attributeTags = null,
        IReadOnlyDictionary<string, int>? attributeMasks = null,
        IReadOnlyList<EstEntryRequest>? estEntries = null)
        => _mashups.ApplyMashupAsync(activeContext, contributors, plan, exportedFile, exportId, destination, name,
            bundleExternalDependencies, createAttributeGroups, attributeTags, attributeMasks, estEntries);

    /// <inheritdoc cref="ModelBackupService.RestoreSourceBackupAsync"/>
    public Task<ExportResult> RestoreSourceBackupAsync(
        string sourceModDirectory,
        string sourceFilePath,
        string? sourceModRootPath,
        string? targetRelativePath,
        string sourceGamePath,
        string backupName,
        string backupTargetId,
        Guid? sourceModStableId = null)
        => _modelBackups.RestoreSourceBackupAsync(sourceModDirectory, sourceFilePath, sourceModRootPath,
            targetRelativePath, sourceGamePath, backupName, backupTargetId, sourceModStableId);

    /// <inheritdoc cref="ModelBackupService.ClearManagedBackupsAsync"/>
    public Task<ExportResult> ClearManagedBackupsAsync(
        string sourceModDirectory, string sourceFilePath, string? sourceModRootPath,
        string? targetRelativePath, string sourceGamePath, string backupTargetId,
        Guid? sourceModStableId = null)
        => _modelBackups.ClearManagedBackupsAsync(sourceModDirectory, sourceFilePath, sourceModRootPath,
            targetRelativePath, sourceGamePath, backupTargetId, sourceModStableId);

    // Texture edits (TextureModBackend) and texture compression (ModFileService).

    Task<TextureSource> ITextureEditBackend.CaptureAsync(TextureEditRequest request, CancellationToken token)
        => ((ITextureEditBackend)_textures).CaptureAsync(request, token);

    Task ITextureEditBackend.CheckAsync(TextureEditSession session, CancellationToken token)
        => ((ITextureEditBackend)_textures).CheckAsync(session, token);

    Task<TextureRebase> ITextureEditBackend.RebaseAsync(TextureEditSession session, CancellationToken token)
        => ((ITextureEditBackend)_textures).RebaseAsync(session, token);

    Task ITextureEditBackend.ConvertAsync(string input, string output, TextureType format, bool mipMaps)
        => ((ITextureEditBackend)_textures).ConvertAsync(input, output, format, mipMaps);

    Task<TextureCommit> ITextureEditBackend.CommitAsync(TextureEditSession session, byte[] tex, Func<bool> stillCurrent,
        CancellationToken token)
        => ((ITextureEditBackend)_textures).CommitAsync(session, tex, stillCurrent, token);

    Task<TextureVariantCommit> ITextureEditBackend.CommitVariantAsync(TextureEditSession session, TextureVariant variant,
        byte[] tex, Func<bool> stillCurrent, CancellationToken token)
        => ((ITextureEditBackend)_textures).CommitVariantAsync(session, variant, tex, stillCurrent, token);

    Task<string> ITextureEditBackend.RefreshAsync(IReadOnlyList<TextureRefresh> items, CancellationToken token)
        => ((ITextureEditBackend)_textures).RefreshAsync(items, token);

    /// <inheritdoc cref="ModFileService.ReplaceModFileAsync"/>
    internal Task ReplaceModFileAsync(PreviewSource source, string expectedSha256, byte[] bytes, string recheck)
        => _modFiles.ReplaceModFileAsync(source, expectedSha256, bytes, recheck);

    /// <inheritdoc cref="ModFileService.ModRootAsync"/>
    internal Task<string?> ModRootAsync(string modDirectory, Guid? stableId) => _modFiles.ModRootAsync(modDirectory, stableId);

    /// <inheritdoc cref="ModFileService.ReloadModsAndRedrawPlayerAsync"/>
    internal Task<IReadOnlyList<string>> ReloadModsAndRedrawPlayerAsync(IReadOnlyCollection<string> modDirectories)
        => _modFiles.ReloadModsAndRedrawPlayerAsync(modDirectories);

    // Preview mods (PreviewModWriter).

    /// <inheritdoc cref="PreviewModWriter.CreatePreviewModAsync"/>
    internal Task<PreviewModResult> CreatePreviewModAsync(string name, string description,
        IReadOnlyList<PreviewModEntry> files, int objectIndex, string fallbackName, CancellationToken token)
        => _previews.CreatePreviewModAsync(name, description, files, objectIndex, fallbackName, token);

    internal Task<string?> PreviewModRootAsync() => _previews.PreviewModRootAsync();

    /// <inheritdoc cref="PreviewModWriter.DeletePreviewModAsync"/>
    internal Task<(bool Removed, string? Warning)> DeletePreviewModAsync(string modDirectory, Guid identifier, int objectIndex)
        => _previews.DeletePreviewModAsync(modDirectory, identifier, objectIndex);

    /// <inheritdoc cref="PreviewModWriter.WritePreviewSourcesAsync"/>
    internal Task<IReadOnlyList<string>> WritePreviewSourcesAsync(IReadOnlyList<(PreviewSource Source, byte[] Bytes)> writes,
        int objectIndex, string recheck, IReadOnlyList<(PreviewSource Material, string GamePath, byte[] Bytes)>? additions = null)
        => _previews.WritePreviewSourcesAsync(writes, objectIndex, recheck, additions);

    /// <inheritdoc cref="PreviewModWriter.ManagedBackupsFor"/>
    internal IReadOnlyList<ManagedBackup> ManagedBackupsFor(PreviewSource source) => _previews.ManagedBackupsFor(source);

    /// <inheritdoc cref="PreviewModWriter.RestorePreviewSourcesAsync"/>
    internal Task<IReadOnlyList<string>> RestorePreviewSourcesAsync(
        IReadOnlyList<(PreviewSource Source, ManagedBackup Backup)> restores, int objectIndex, string recheck)
        => _previews.RestorePreviewSourcesAsync(restores, objectIndex, recheck);

    // Animation edits (AnimationModService).

    internal Task<(string Directory, string Root)[]> AnimationSkeletonRootsAsync() => _animations.AnimationSkeletonRootsAsync();

    internal void CheckAnimationCollectionOnFramework(ushort objectIndex, Guid collection)
        => _animations.CheckAnimationCollectionOnFramework(objectIndex, collection);

    internal Task<string> ResolveAnimationPathAsync(Guid collection, string gamePath)
        => _animations.ResolveAnimationPathAsync(collection, gamePath);

    internal Task AnimationExportAsync(Func<Task> action, CancellationToken token) => _animations.AnimationExportAsync(action, token);

    internal Task<string> AnimationModRootAsync() => _animations.AnimationModRootAsync();

    internal Task<string> AnimationMetadataAsync(Guid collection) => _animations.AnimationMetadataAsync(collection);

    internal Task CheckAnimationModRootAsync(string mod, string root) => _animations.CheckAnimationModRootAsync(mod, root);

    internal Task ActivateAnimationAsync(AnimationEditJournal journal) => _animations.ActivateAnimationAsync(journal);

    internal Task RedrawAnimationAsync() => _animations.RedrawAnimationAsync();

    /// <inheritdoc cref="AnimationModService.RedrawObjectAndWaitAsync"/>
    internal Task<string?> RedrawObjectAndWaitAsync(int objectIndex, TimeSpan timeout, CancellationToken token)
        => _animations.RedrawObjectAndWaitAsync(objectIndex, timeout, token);

    internal Task DisableAnimationModAsync(AnimationEditJournal journal) => _animations.DisableAnimationModAsync(journal);

    internal Task ReloadAnimationSourcesAsync(IEnumerable<string> mods) => _animations.ReloadAnimationSourcesAsync(mods);

    // Helpers the export server and the windows still call here.

    internal static bool IsSafeGamePath(string? gamePath) => ModPaths.IsSafeGamePath(gamePath);
    internal static bool IsSafeNewModName(string? modName) => ModPaths.IsSafeNewModName(modName);
    internal static bool IsSafeVariantGroupName(string? value) => ModPaths.IsSafeVariantGroupName(value);
    internal static IReadOnlyList<string> AnimationMappedFiles(string modRoot, string gamePath)
        => ModMetadata.AnimationMappedFiles(modRoot, gamePath);
    internal static JsonArray? KnownManipulations(InstantEditImportContext context)
        => AttributeGroupService.KnownManipulations(context);
    internal static string? ValidateEstEntryRequests(IReadOnlyList<EstEntryRequest>? entries)
        => EstEntryService.ValidateEstEntryRequests(entries);
    internal static int? PlayerRaceCode(string? race) => RaceExportService.PlayerRaceCode(race);
    internal static string? RaceModelGamePath(string gamePath, int race) => RaceExportService.RaceModelGamePath(gamePath, race);
    internal static bool IsSafeSizeName(string? value) => SizesExportService.IsSafeSizeName(value);
    internal static MashupPlanResult BuildMashupPlan(InstantEditImportContext activeContext,
        IReadOnlyList<MashupContributor> contributors, bool bundleExternalDependencies = false)
        => MashupPlanning.BuildMashupPlan(activeContext, contributors, bundleExternalDependencies);
}
