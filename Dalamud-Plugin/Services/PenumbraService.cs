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
/// Wraps all Penumbra IPC used by XIV Instant Edit:
/// reading the resolved model files of on-screen game objects and
/// writing/updating a persistent mod so the edited model applies in-game.
/// </summary>
public sealed partial class PenumbraService
{
    internal sealed record SourceModTarget(string Directory, string Folder, string FilePath, string RelativePath);
    internal sealed record SourceTargetResolution(SourceModTarget? Target, string Code, string? Error);
    private sealed record AttributeModelIdentity(
        int Id, int RaceCode, string AtrSlot, string ObjectType, string EquipSlot, string BodySlot);
    private sealed record AttributeGroupWrite(string Path, JsonObject Document);

    private const string OwnershipMarkerFile = ".instant-edit-owner.json";
    private const string VariantGroupDescriptionPrefix = "Managed by XIV Instant Edit variant group: ";
    private const string AttributeGroupDescriptionPrefix = "Managed by XIV Instant Edit attribute group v1: ";
    private static readonly string[] AttributeGroupFamilies =
        ["mv", "tv", "gv", "dv", "sv", "ev", "nv", "wv", "rv"];
    // Every other attribute the game's own gear, accessory, body, hair, face, tail
    // and ear models use, typos included (surveyed from the game data 2026-09-28).
    // The game drives them itself, so they never get a generated group. Keep in
    // sync with GAME_ATTRIBUTE_NAMES in the Blender add-on's materials.py.
    private static readonly HashSet<string> GameAttributeNames = new(StringComparer.Ordinal)
    {
        "atr_ar", "atr_arm", "atr_arm1", "atr_arm2", "atr_arm3", "atr_armc", "atr_armhh",
        "atr_bak", "atr_bak1", "atr_bak3", "atr_bak4", "atr_bakh", "atr_blt", "atr_bv_a",
        "atr_cn_ankle", "atr_cn_neck", "atr_cn_waist", "atr_cn_wrist", "atr_del",
        "atr_hair", "atr_head", "atr_hig", "atr_hij", "atr_hij1", "atr_hiz", "atr_hiz1", "atr_hrn",
        "atr_inr", "atr_kam", "atr_kam1", "atr_kam4", "atr_kam23", "atr_kam24",
        "atr_kao", "atr_kao1", "atr_kao2", "atr_kao3", "atr_kod", "atr_kod1", "atr_kod2",
        "atr_leg", "atr_leg1", "atr_lod", "atr_lod1", "atr_lod2", "atr_lod12",
        "atr_lod117", "atr_lod118", "atr_lod119", "atr_lod120", "atr_lpd", "atr_mim", "atr_mv_a1",
        "atr_nek", "atr_nek2", "atr_sne", "atr_sne1", "atr_st", "atr_sta", "atr_star",
        "atr_tlh", "atr_tls", "atr_top", "atr_top1", "atr_top3", "atr_tv_h1", "atr_ude", "atr_ude1", "atr_vsr",
    };

    private readonly IDalamudPluginInterface  _pi;
    private readonly GetGameObjectResourcePaths _getPaths;
    private readonly GetGameObjectResourceTrees _getObjectTrees;
    private readonly GetPlayerResourceTrees _getPlayerTrees;
    private readonly GetPlayerResourcePaths _getPlayerPaths;
    private readonly GetModDirectory           _getModDirectory;
    private readonly GetModList                 _getModList;
    private readonly GetModPath                 _getModPath;
    private readonly AddMod                     _addMod;
    private readonly ReloadMod                  _reloadMod;
    private readonly GetCollectionForObject      _getCollectionForObject;
    private readonly GetCurrentModSettings       _getCurrentModSettings;
    private readonly TrySetMod                   _trySetMod;
    private readonly TrySetModPriority            _trySetModPriority;
    private readonly TrySetModSetting             _trySetModSetting;
    private readonly RedrawObject               _redrawObject;
    private readonly IFramework                  _framework;
    private readonly IPluginLog                 _log;
    private readonly IObjectTable?              _objects;
    private readonly IDataManager?              _data;
    private readonly ModelBackupStore?          _backups;
    private readonly EstEntryStore?             _estEntries;
    private readonly SemaphoreSlim              _exportGate = new(1, 1);

    public PenumbraService(
        IDalamudPluginInterface pi,
        IFramework framework,
        IPluginLog log,
        IObjectTable? objects = null,
        IDataManager? data = null,
        ModelBackupStore? backups = null,
        EstEntryStore? estEntries = null)
    {
        _pi               = pi;
        _log             = log;
        _framework       = framework;
        _objects         = objects;
        _data            = data;
        _backups         = backups;
        _estEntries      = estEntries;
        _getPaths        = new GetGameObjectResourcePaths(pi);
        _getObjectTrees  = new GetGameObjectResourceTrees(pi);
        _getPlayerTrees  = new GetPlayerResourceTrees(pi);
        _getPlayerPaths  = new GetPlayerResourcePaths(pi);
        _getModDirectory = new GetModDirectory(pi);
        _getModList      = new GetModList(pi);
        _getModPath      = new GetModPath(pi);
        _addMod          = new AddMod(pi);
        _reloadMod       = new ReloadMod(pi);
        _getCollectionForObject = new GetCollectionForObject(pi);
        _getCurrentModSettings = new GetCurrentModSettings(pi);
        _trySetMod       = new TrySetMod(pi);
        _trySetModPriority = new TrySetModPriority(pi);
        _trySetModSetting = new TrySetModSetting(pi);
        _redrawObject    = new RedrawObject(pi);
        SubscribeToResourceChanges(pi);
    }

    /// <summary> Whether Penumbra is loaded and the resource tree IPC is available. </summary>
    public bool Available
    {
        get
        {
            try
            {
                return _getPlayerTrees.Valid;
            }
            catch (Exception e)
            {
                _log.Debug($"Could not query Penumbra availability: {e.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// Get the resolved resource paths for one game object.
    /// The dictionary maps the resolved local path (mod file on disk, or the game path if unmodded)
    /// to the set of game paths it serves.
    /// </summary>
    public Dictionary<string, HashSet<string>>? GetResourcePaths(ushort gameObjectIndex)
    {
        try
        {
            if (!Available)
                return null;

            var result = _getPaths.Invoke(gameObjectIndex);
            return result is { Length: > 0 } ? result[0] : null;
        }
        catch (Exception e)
        {
            _log.Debug($"Could not retrieve Penumbra resource paths: {e.Message}");
            return null;
        }
    }

    /// <summary>Resolve one object's effective resources on Dalamud's framework thread.</summary>
    public async Task<Dictionary<string, HashSet<string>>?> GetResourcePathsAsync(ushort gameObjectIndex)
        => await _framework.RunOnFrameworkThread(() => GetResourcePaths(gameObjectIndex)).ConfigureAwait(false);

    public async Task<PenumbraCollectionTarget?> GetCollectionTargetAsync(int gameObjectIndex)
        => await _framework.RunOnFrameworkThread(() =>
        {
            try
            {
                var collection = _getCollectionForObject.Invoke(gameObjectIndex);
                return collection.ObjectValid && collection.EffectiveCollection.Id != Guid.Empty &&
                       !string.IsNullOrWhiteSpace(collection.EffectiveCollection.Name)
                    ? new PenumbraCollectionTarget(collection.EffectiveCollection.Id, collection.EffectiveCollection.Name)
                    : null;
            }
            catch (Exception e)
            {
                _log.Debug(e, "Could not capture the Penumbra collection for object {ObjectIndex}.", gameObjectIndex);
                return null;
            }
        }).ConfigureAwait(false);

    /// <summary>
    /// Resumes on the thread pool. An await on RunOnFrameworkThread continues inline on the game
    /// thread that ran the work, so the file work that usually follows would stall the frame.
    /// </summary>
    private static ThreadPoolHop LeaveFrameworkThread() => default;

    private readonly struct ThreadPoolHop : System.Runtime.CompilerServices.ICriticalNotifyCompletion
    {
        public ThreadPoolHop GetAwaiter() => this;
        public bool IsCompleted => false;
        public void GetResult() { }
        public void OnCompleted(Action continuation)
            => ThreadPool.QueueUserWorkItem(static run => run(), continuation, preferLocal: false);
        public void UnsafeOnCompleted(Action continuation)
            => ThreadPool.UnsafeQueueUserWorkItem(static run => run(), continuation, preferLocal: false);
    }

    /// <summary>Capture the effective source-mod manipulations for one collection.</summary>
    public async Task<JsonArray?> CaptureEffectiveManipulationsAsync(
        Guid collectionId,
        string modDirectory,
        string modRoot)
    {
        if (collectionId == Guid.Empty || !IsSafeModName(modDirectory) ||
            string.IsNullOrWhiteSpace(modRoot) || !Directory.Exists(modRoot))
            return null;
        try
        {
            var selections = await _framework.RunOnFrameworkThread(() =>
            {
                var result = _getCurrentModSettings.Invoke(
                    collectionId, modDirectory, string.Empty, false);
                if (result.Item1 is not PenumbraApiEc.Success || result.Item2 is null)
                    return null;
                return result.Item2.Value.Item3.ToDictionary(
                    pair => pair.Key,
                    pair => (IReadOnlyList<string>)pair.Value.ToArray(),
                    StringComparer.OrdinalIgnoreCase);
            }).ConfigureAwait(false);
            await LeaveFrameworkThread();
            return selections is null
                ? null
                : CaptureEffectiveManipulations(modRoot, selections);
        }
        catch (Exception e)
        {
            _log.Warning(e, "Could not snapshot Meta Manipulations for source mod {ModDirectory}.", modDirectory);
            return null;
        }
    }

    internal static JsonArray CaptureEffectiveManipulations(
        string modFolder,
        IReadOnlyDictionary<string, IReadOnlyList<string>> enabledOptions)
    {
        var meta = LoadV4ModMetadata(modFolder);
        var groups = ReadAllVariantGroups(modFolder)
            .Select((group, index) => (Group: group, Index: index))
            .OrderByDescending(item => JsonInt(item.Group["Priority"]))
            .ThenBy(item => item.Index)
            .ToArray();
        var result = new JsonArray();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var serializedBytes = 2;

        static string CanonicalJson(JsonNode? node)
            => node switch
            {
                null => "null",
                JsonObject value => "{" + string.Join(",", value
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => JsonSerializer.Serialize(pair.Key) + ":" + CanonicalJson(pair.Value))) + "}",
                JsonArray value => "[" + string.Join(",", value.Select(CanonicalJson)) + "]",
                _ => node.ToJsonString(),
            };

        static string Identity(JsonObject manipulation)
        {
            var identity = manipulation.DeepClone().AsObject();
            if (identity["Manipulation"] is JsonObject payload)
                payload.Remove("Entry");
            else
                identity.Remove("Entry");
            return CanonicalJson(identity);
        }

        void Append(JsonNode? container)
        {
            if (container?["Manipulations"] is not JsonArray manipulations)
                return;
            foreach (var manipulation in manipulations)
            {
                if (manipulation is not JsonObject entry)
                    throw new InvalidDataException("A source manipulation entry is invalid.");
                var type = JsonString(entry["Type"]);
                if (type is not ("Eqp" or "Eqdp" or "Imc" or "Est" or "Atr"))
                    continue;
                if (!identities.Add(Identity(entry)))
                    continue;
                var clone = entry.DeepClone();
                serializedBytes += Encoding.UTF8.GetByteCount(clone.ToJsonString()) + 1;
                if (result.Count >= 4096 || serializedBytes > 4 * 1024 * 1024)
                    throw new InvalidDataException("The source manipulation snapshot is too large.");
                result.Add(clone);
            }
        }

        foreach (var (group, _index) in groups)
        {
            var groupName = JsonString(group["Name"]);
            var selection = enabledOptions.FirstOrDefault(pair =>
                string.Equals(pair.Key, groupName, StringComparison.OrdinalIgnoreCase));
            var selected = selection.Value;
            if (selected is null)
                continue;

            if (string.Equals(JsonString(group["Type"]), "Combining", StringComparison.OrdinalIgnoreCase))
            {
                if (group["Options"] is not JsonArray combinationOptions ||
                    group["Containers"] is not JsonArray combinationContainers)
                    continue;
                var containerIndex = 0;
                for (var optionIndex = 0; optionIndex < combinationOptions.Count && optionIndex < 31; ++optionIndex)
                    if (combinationOptions[optionIndex] is JsonObject option &&
                        selected.Contains(JsonString(option["Name"]) ?? "", StringComparer.OrdinalIgnoreCase))
                        containerIndex |= 1 << optionIndex;
                if (containerIndex < combinationContainers.Count)
                    Append(combinationContainers[containerIndex]);
                continue;
            }

            if (selected.Count == 0 || group["Options"] is not JsonArray options)
                continue;
            foreach (var selectedName in selected)
            {
                var container = options.OfType<JsonObject>().FirstOrDefault(option =>
                    string.Equals(JsonString(option["Name"]), selectedName, StringComparison.OrdinalIgnoreCase));
                if (container is not null)
                    Append(container);
            }
        }

        Append(meta["DefaultData"]);
        return result;
    }

    /// <summary>
    /// Get the resolved resource paths for several game objects in one IPC call. Penumbra
    /// looks these indices up in Dalamud's object table, which asserts the main thread.
    /// </summary>
    public Dictionary<string, HashSet<string>>?[] GetResourcePaths(ushort[] gameObjectIndices)
    {
        if (gameObjectIndices.Length == 0)
            return Array.Empty<Dictionary<string, HashSet<string>>?>();

        try
        {
            if (!Available)
                return EmptyResourceResults(gameObjectIndices.Length);

            var result = _getPaths.Invoke(gameObjectIndices);
            if (result is null || result.Length != gameObjectIndices.Length)
                return EmptyResourceResults(gameObjectIndices.Length);
            return result;
        }
        catch (Exception e)
        {
            _log.Debug($"Could not retrieve Penumbra resource paths: {e.Message}");
            return EmptyResourceResults(gameObjectIndices.Length);
        }
    }

    /// <summary>
    /// Gets Penumbra's local-player-owned resource trees, including the public UI
    /// labels and icons. The returned index is the current object-table index.
    /// Call it on the framework thread: Penumbra builds the trees on the calling thread and
    /// reads each character's ObjectIndex there, which Dalamud rejects on other threads.
    /// </summary>
    public IReadOnlyDictionary<ushort, ResourceTreeDto> GetPlayerResourceTrees()
    {
        try
        {
            if (!Available)
                return new Dictionary<ushort, ResourceTreeDto>();

            return _getPlayerTrees.Invoke(withUiData: true);
        }
        catch (Exception e)
        {
            _log.Warning($"Could not retrieve Penumbra player resource trees: {e.Message}");
            return new Dictionary<ushort, ResourceTreeDto>();
        }
    }

    /// <summary>
    /// Gets the resolved resource paths of the local player and their owned objects, keyed
    /// by object-table index. Like <see cref="GetPlayerResourceTrees"/>, call it on the
    /// framework thread.
    /// </summary>
    public IReadOnlyDictionary<ushort, Dictionary<string, HashSet<string>>> GetPlayerResourcePaths()
    {
        try
        {
            if (!Available)
                return new Dictionary<ushort, Dictionary<string, HashSet<string>>>();

            return _getPlayerPaths.Invoke() ?? new Dictionary<ushort, Dictionary<string, HashSet<string>>>();
        }
        catch (Exception e)
        {
            _log.Warning($"Could not retrieve Penumbra player resource paths: {e.Message}");
            return new Dictionary<ushort, Dictionary<string, HashSet<string>>>();
        }
    }

    public string? GetModDirectory()
    {
        try
        {
            var dir = _getModDirectory.Invoke();
            return string.IsNullOrWhiteSpace(dir) ? null : dir;
        }
        catch (Exception e)
        {
            _log.Debug($"Could not retrieve Penumbra mod directory: {e.Message}");
            return null;
        }
    }

    public Dictionary<string, string> GetModList()
    {
        try
        {
            return _getModList.Invoke() ?? new Dictionary<string, string>();
        }
        catch (Exception e)
        {
            _log.Debug($"Could not retrieve Penumbra mod list: {e.Message}");
            return new Dictionary<string, string>();
        }
    }
}
