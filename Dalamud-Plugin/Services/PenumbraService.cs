using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using InstantEdit.Models;
using Penumbra.Api.Enums;
using Penumbra.Api.Helpers;
using Penumbra.Api.IpcSubscribers;
using Lumina.Data;
using Lumina.Data.Files;
using InstantEdit.Services.PenumbraMods;
using static InstantEdit.Services.PenumbraMods.FrameworkThread;
using static InstantEdit.Services.PenumbraMods.ModCleanup;
using static InstantEdit.Services.PenumbraMods.ModJson;
using static InstantEdit.Services.PenumbraMods.ModMetadata;
using static InstantEdit.Services.PenumbraMods.ModOptionGroups;
using static InstantEdit.Services.PenumbraMods.ModPaths;
using System.Text.Json.Serialization;

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
/// Wraps all Penumbra IPC used by XIV Instant Edit:
/// reading the resolved model files of on-screen game objects and
/// writing/updating a persistent mod so the edited model applies in-game.
/// </summary>
public sealed partial class PenumbraService : IDisposable
{
    private readonly IDalamudPluginInterface  _pi;
    private readonly PenumbraIpc                _ipc;
    private readonly IFramework                  _framework;
    private readonly IPluginLog                 _log;
    private readonly IObjectTable               _objects;
    private readonly IDataManager               _data;
    private readonly ModelBackupStore           _backups;
    private readonly SemaphoreSlim              _exportGate = new(1, 1);
    private readonly ModActivation              _activation;
    private readonly SourceMods                 _sources;
    private readonly ModelFileWriter            _writer;
    private readonly AttributeGroupService      _attributes;
    private readonly EstEntryService            _est;

    public PenumbraService(
        IDalamudPluginInterface pi,
        IFramework framework,
        IPluginLog log,
        IObjectTable objects,
        IDataManager data,
        ModelBackupStore backups,
        EstEntryStore estEntries)
    {
        _pi               = pi;
        _log             = log;
        _framework       = framework;
        _objects         = objects;
        _data            = data;
        _backups         = backups;
        _ipc             = new PenumbraIpc(pi, framework, log);
        _activation      = new ModActivation(_ipc, framework, log, objects);
        _sources         = new SourceMods(_ipc, framework, log);
        _writer          = new ModelFileWriter(backups);
        _attributes      = new AttributeGroupService(data, log);
        _est             = new EstEntryService(estEntries, log);
    }

    private GetModDirectory _getModDirectory => _ipc.ModDirectorySubscriber;
    private GetModList _getModList => _ipc.ModListSubscriber;
    private GetModPath _getModPath => _ipc.GetModPath;
    private AddMod _addMod => _ipc.AddMod;
    private ReloadMod _reloadMod => _ipc.ReloadMod;
    private GetCollectionForObject _getCollectionForObject => _ipc.GetCollectionForObject;
    private GetCurrentModSettings _getCurrentModSettings => _ipc.GetCurrentModSettings;
    private TrySetMod _trySetMod => _ipc.TrySetMod;
    private TrySetModPriority _trySetModPriority => _ipc.TrySetModPriority;
    private TrySetModSetting _trySetModSetting => _ipc.TrySetModSetting;
    private RedrawObject _redrawObject => _ipc.RedrawObject;

    /// <inheritdoc cref="PenumbraIpc.Available"/>
    public bool Available => _ipc.Available;

    /// <inheritdoc cref="PenumbraIpc.GetResourcePaths(ushort)"/>
    public Dictionary<string, HashSet<string>>? GetResourcePaths(ushort gameObjectIndex) => _ipc.GetResourcePaths(gameObjectIndex);

    /// <inheritdoc cref="PenumbraIpc.GetResourcePaths(ushort[])"/>
    public Dictionary<string, HashSet<string>>?[] GetResourcePaths(ushort[] gameObjectIndices) => _ipc.GetResourcePaths(gameObjectIndices);

    /// <inheritdoc cref="PenumbraIpc.GetResourcePathsAsync"/>
    public Task<Dictionary<string, HashSet<string>>?> GetResourcePathsAsync(ushort gameObjectIndex)
        => _ipc.GetResourcePathsAsync(gameObjectIndex);

    public Task<PenumbraCollectionTarget?> GetCollectionTargetAsync(int gameObjectIndex) => _ipc.GetCollectionTargetAsync(gameObjectIndex);

    /// <inheritdoc cref="PenumbraIpc.GetPlayerResourceTrees"/>
    public IReadOnlyDictionary<ushort, ResourceTreeDto> GetPlayerResourceTrees() => _ipc.GetPlayerResourceTrees();

    /// <inheritdoc cref="PenumbraIpc.GetPlayerResourcePaths"/>
    public IReadOnlyDictionary<ushort, Dictionary<string, HashSet<string>>> GetPlayerResourcePaths() => _ipc.GetPlayerResourcePaths();

    /// <inheritdoc cref="PenumbraIpc.GetResourceTrees"/>
    public ResourceTreeDto?[] GetResourceTrees(ushort[] gameObjectIndices) => _ipc.GetResourceTrees(gameObjectIndices);

    public string? GetModDirectory() => _ipc.GetModDirectory();

    public Dictionary<string, string> GetModList() => _ipc.GetModList();

    /// <inheritdoc cref="PenumbraIpc.TryGetModList"/>
    internal bool TryGetModList(out Dictionary<string, string> modList) => _ipc.TryGetModList(out modList);

    /// <inheritdoc cref="PenumbraIpc.GetRegisteredModPath"/>
    public string? GetRegisteredModPath(string modDirectory) => _ipc.GetRegisteredModPath(modDirectory);

    public IReadOnlyList<PenumbraMod> GetMods() => _ipc.GetMods();

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

    /// <inheritdoc cref="PenumbraIpc.OpenModInPenumbra"/>
    public void OpenModInPenumbra(string modDirectory, string modName) => _ipc.OpenModInPenumbra(modDirectory, modName);

    public void Dispose() => _ipc.Dispose();

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

    /// <inheritdoc cref="PenumbraIpc.ResolveCollectionPathsAsync"/>
    internal Task<string?[]> ResolveCollectionPathsAsync(Guid collection, IReadOnlyList<string> gamePaths)
        => _ipc.ResolveCollectionPathsAsync(collection, gamePaths);

    /// <inheritdoc cref="PenumbraIpc.MetaManipulationsAsync"/>
    internal Task<string?> MetaManipulationsAsync(int objectIndex) => _ipc.MetaManipulationsAsync(objectIndex);

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

    // The export server and the windows still call these helpers here.
    internal static bool IsSafeGamePath(string? gamePath) => ModPaths.IsSafeGamePath(gamePath);
    internal static bool IsSafeNewModName(string? modName) => ModPaths.IsSafeNewModName(modName);
    internal static bool IsSafeVariantGroupName(string? value) => ModPaths.IsSafeVariantGroupName(value);
    internal static IReadOnlyList<string> AnimationMappedFiles(string modRoot, string gamePath)
        => ModMetadata.AnimationMappedFiles(modRoot, gamePath);
    internal static JsonArray? KnownManipulations(InstantEditImportContext context)
        => AttributeGroupService.KnownManipulations(context);
    internal static string? ValidateEstEntryRequests(IReadOnlyList<EstEntryRequest>? entries)
        => EstEntryService.ValidateEstEntryRequests(entries);

    // PenumbraService.Physics.cs still calls these by their old names.
    private Task<ExportResult?> AddNewModAsync(string modName) => _activation.AddNewModAsync(modName);
    private ExportResult? ReloadModOnFramework(string modName) => _activation.ReloadModOnFramework(modName);
    private ExportResult ConfigureModForCollectionOnFramework(string modName, Guid collectionId, string collectionName,
        bool setPriority = true, int priority = int.MaxValue, bool redraw = true)
        => _activation.ConfigureModForCollectionOnFramework(modName, collectionId, collectionName, setPriority, priority, redraw);
    private static string NormalizeGamePath(string value) => ModPaths.NormalizeGamePath(value);
    private static bool IsSafeGameResourcePath(string? gamePath, params string[] extensions)
        => ModPaths.IsSafeGameResourcePath(gamePath, extensions);
    private static bool IsSafeModName(string? modName) => ModPaths.IsSafeModName(modName);
    private static bool IsPathWithin(string path, string root) => ModPaths.IsPathWithin(path, root);
    private static bool HasReparsePointInPath(string root, string path) => ModPaths.HasReparsePointInPath(root, path);
    private static bool SameGamePath(string left, string right) => ModPaths.SameGamePath(left, right);
    private static string? JsonString(JsonNode? node) => ModJson.JsonString(node);
    private static JsonObject LoadV4ModMetadata(string modRoot) => ModMetadata.LoadV4ModMetadata(modRoot);
    private static void TouchV4ModMetadata(JsonObject meta) => ModMetadata.TouchV4ModMetadata(meta);
    private static void WriteJsonAtomic(string path, JsonObject value) => ModMetadata.WriteJsonAtomic(path, value);
    private static Guid? ReadModStableIdentifier(string modRoot) => ModMetadata.ReadModStableIdentifier(modRoot);
    private static JsonObject CreateV4ModMetadata(string name, string author, string description, string version,
        JsonObject defaultData)
        => ModMetadata.CreateV4ModMetadata(name, author, description, version, defaultData);
    private static FrameworkThread.ThreadPoolHop LeaveFrameworkThread() => FrameworkThread.LeaveFrameworkThread();
}
