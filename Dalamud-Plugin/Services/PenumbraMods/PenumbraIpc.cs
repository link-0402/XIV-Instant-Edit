using Dalamud.Plugin;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;
using Penumbra.Api.Enums;
using Penumbra.Api.Helpers;
using Penumbra.Api.IpcSubscribers;
using static InstantEdit.Services.PenumbraMods.ModPaths;

namespace InstantEdit.Services.PenumbraMods;

/// <summary> The collection an object uses, and whether it was assigned to that character alone. </summary>
internal sealed record TemporaryModCollection(Guid Id, string Name, bool IndividualSet);

/// <summary>
/// Every Penumbra IPC call and event XIV Instant Edit uses. The reads of Penumbra's state return
/// nothing instead of throwing while Penumbra is unavailable; the subscribers the mod services
/// call themselves are exposed as they are.
/// </summary>
internal sealed class PenumbraIpc : IDisposable
{
    private readonly IDalamudPluginInterface _pi;
    private readonly IFramework _framework;
    private readonly IPluginLog _log;
    private readonly GetGameObjectResourcePaths _getPaths;
    private readonly GetGameObjectResourceTrees _getObjectTrees;
    private readonly GetPlayerResourceTrees _getPlayerTrees;
    private readonly GetPlayerResourcePaths _getPlayerPaths;
    private readonly GetModDirectory _getModDirectory;
    private readonly GetModList _getModList;
    private readonly GetModPath _getModPath;
    private readonly GetCollectionForObject _getCollectionForObject;

    public PenumbraIpc(IDalamudPluginInterface pi, IFramework framework, IPluginLog log)
    {
        _pi = pi;
        _framework = framework;
        _log = log;
        _getPaths = new GetGameObjectResourcePaths(pi);
        _getObjectTrees = new GetGameObjectResourceTrees(pi);
        _getPlayerTrees = new GetPlayerResourceTrees(pi);
        _getPlayerPaths = new GetPlayerResourcePaths(pi);
        _getModDirectory = new GetModDirectory(pi);
        _getModList = new GetModList(pi);
        _getModPath = new GetModPath(pi);
        AddMod = new AddMod(pi);
        ReloadMod = new ReloadMod(pi);
        _getCollectionForObject = new GetCollectionForObject(pi);
        GetCurrentModSettings = new GetCurrentModSettings(pi);
        TrySetMod = new TrySetMod(pi);
        TrySetModPriority = new TrySetModPriority(pi);
        TrySetModSetting = new TrySetModSetting(pi);
        RedrawObject = new RedrawObject(pi);
        SubscribeToResourceChanges(pi);
    }

    internal AddMod AddMod { get; }
    internal ReloadMod ReloadMod { get; }
    internal GetModPath GetModPath => _getModPath;
    internal GetCollectionForObject GetCollectionForObject => _getCollectionForObject;
    internal GetCurrentModSettings GetCurrentModSettings { get; }
    internal TrySetMod TrySetMod { get; }
    internal TrySetModPriority TrySetModPriority { get; }
    internal TrySetModSetting TrySetModSetting { get; }
    internal RedrawObject RedrawObject { get; }

    /// <summary> Penumbra's mod directory as Penumbra answers, throwing when it can't; see <see cref="GetModDirectory"/>. </summary>
    internal GetModDirectory ModDirectorySubscriber => _getModDirectory;

    /// <summary> Penumbra's mod list as Penumbra answers, throwing when it can't; see <see cref="GetModList"/>. </summary>
    internal GetModList ModListSubscriber => _getModList;

    internal PenumbraApiEc ResolvePath(Guid collection, string gamePath, out string? resolved)
        => new ResolvePath(_pi).Invoke(collection, gamePath, out resolved);

    internal string GetMetaManipulations(int objectIndex) => new GetMetaManipulations(_pi).Invoke(objectIndex);

    internal ModListWrapper GetModListAdapter() => new GetModListAdapter(_pi).Invoke();

    internal PenumbraApiEc DeleteMod(string modDirectory) => new DeleteMod(_pi).Invoke(modDirectory);

    internal Task ConvertTextureFile(string input, string output, TextureType format, bool mipMaps)
        => new ConvertTextureFile(_pi).Invoke(input, output, format, mipMaps);

    /// <summary> A new subscription to Penumbra's mod-added event; the caller enables and disposes it. </summary>
    internal EventSubscriber<string> SubscribeModAdded(Action<string> added) => ModAdded.Subscriber(_pi, added);

    /// <summary> Redraws one object, so it loads its skeleton, physics and animations again. </summary>
    internal Task<string?> RedrawObjectAsync(int objectIndex)
        => _framework.RunOnFrameworkThread(() =>
        {
            try
            {
                RedrawObject.Invoke(objectIndex);
                return (string?)null;
            }
            catch (Exception e)
            {
                _log.Warning(e, "Could not redraw object {ObjectIndex}.", objectIndex);
                return "Your character could not be redrawn; redraw it in Penumbra.";
            }
        });

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

    private static Dictionary<string, HashSet<string>>?[] EmptyResourceResults(int count)
        => Enumerable.Repeat<Dictionary<string, HashSet<string>>?>(null, count).ToArray();

    /// <summary> Reads the mod list, distinguishing an IPC failure from an empty list. </summary>
    internal bool TryGetModList(out Dictionary<string, string> modList)
    {
        try
        {
            modList = _getModList.Invoke() ?? new Dictionary<string, string>();
            return true;
        }
        catch (Exception e)
        {
            _log.Error(e, "Could not retrieve the Penumbra mod list.");
            modList = new Dictionary<string, string>();
            return false;
        }
    }

    /// <summary>Resolve the root currently registered for a Penumbra directory key.</summary>
    public string? GetRegisteredModPath(string modDirectory)
    {
        if (!IsSafeModName(modDirectory))
            return null;
        try
        {
            var result = _getModPath.Invoke(modDirectory, string.Empty);
            if (result.Item1 is not PenumbraApiEc.Success)
                return null;
            var path = NormalizePhysicalPath(result.Item2);
            if (path is null)
                return null;
            return string.Equals(Path.GetFileName(path), "Files", StringComparison.OrdinalIgnoreCase)
                ? Directory.GetParent(path)?.FullName ?? path
                : path;
        }
        catch (Exception e)
        {
            _log.Debug($"Could not retrieve the registered path for {modDirectory}: {e.Message}");
            return null;
        }
    }

    public IReadOnlyList<PenumbraMod> GetMods()
        => GetModList()
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => new PenumbraMod(pair.Key, pair.Value))
            .OrderBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(mod => mod.Directory, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// Get resource trees for explicit object-table indices. Penumbra looks these indices
    /// up in Dalamud's object table, which asserts the main thread.
    /// </summary>
    public ResourceTreeDto?[] GetResourceTrees(ushort[] gameObjectIndices)
    {
        if (gameObjectIndices.Length == 0)
            return Array.Empty<ResourceTreeDto?>();

        try
        {
            if (!Available)
                return Array.Empty<ResourceTreeDto?>();

            return _getObjectTrees.Invoke(withUiData: true, gameObjectIndices) ?? Array.Empty<ResourceTreeDto?>();
        }
        catch (Exception e)
        {
            _log.Debug($"Could not retrieve explicit resource trees: {e.Message}");
            return Array.Empty<ResourceTreeDto?>();
        }
    }

    /// <summary>
    /// The files a collection loads for game paths: a mod's file as a full path, or the game path
    /// (or the one a file swap names) when no mod replaces it; null where Penumbra couldn't resolve.
    /// </summary>
    internal Task<string?[]> ResolveCollectionPathsAsync(Guid collection, IReadOnlyList<string> gamePaths) => _framework.RunOnFrameworkThread(() =>
    {
        var resolve = new ResolvePath(_pi);
        return gamePaths.Select(gamePath =>
        {
            try
            {
                return resolve.Invoke(collection, gamePath, out var resolved) == PenumbraApiEc.Success && !string.IsNullOrWhiteSpace(resolved)
                    ? resolved
                    : null;
            }
            catch (Exception e)
            {
                _log.Debug(e, "Could not resolve {GamePath} for collection {Collection}.", gamePath, collection);
                return null;
            }
        }).ToArray();
    });

    /// <summary>
    /// The encoded meta manipulations Penumbra applies to a game object (see
    /// <c>AnimationMetadata.Decode</c>), or null when Penumbra can't give them.
    /// </summary>
    internal Task<string?> MetaManipulationsAsync(int objectIndex) => _framework.RunOnFrameworkThread(() =>
    {
        try
        {
            var encoded = new GetMetaManipulations(_pi).Invoke(objectIndex);
            return string.IsNullOrEmpty(encoded) ? null : encoded;
        }
        catch (Exception e)
        {
            _log.Debug(e, "Could not read the meta manipulations of object {ObjectIndex}.", objectIndex);
            return null;
        }
    });

    private OpenMainWindow? _openMainWindow;

    private EventSubscriber<ModSettingChange, Guid, string, bool>? _modSettingChanged;

    private EventSubscriber<nint, int>? _gameObjectRedrawn;

    private EventSubscriber<string>? _modAdded;

    private EventSubscriber<string>? _modDeleted;

    private EventSubscriber<string, string>? _modMoved;

    /// <summary>
    /// Raised, on Penumbra's thread, when a mod setting changed in any collection or a game
    /// object was redrawn. Handlers must be cheap and must not call back into Penumbra.
    /// </summary>
    public event Action? ResourcesChanged;

    /// <summary>
    /// Raised, on Penumbra's thread, when a mod was added, deleted or moved. Handlers must be
    /// cheap and must not call back into Penumbra.
    /// </summary>
    public event Action? ModsChanged;

    /// <summary>
    /// Raised, on Penumbra's thread, with the object index once Penumbra drew a redrawn object again.
    /// Handlers must be cheap and must not call back into Penumbra.
    /// </summary>
    internal event Action<int>? ObjectRedrawn;

    private void SubscribeToResourceChanges(IDalamudPluginInterface pi)
    {
        _openMainWindow = new OpenMainWindow(pi);
        _modSettingChanged = ModSettingChanged.Subscriber(pi, (_, _, _, _) => RaiseResourcesChanged());
        _gameObjectRedrawn = GameObjectRedrawn.Subscriber(pi, (_, index) =>
        {
            ObjectRedrawn?.Invoke(index);
            RaiseResourcesChanged();
        });
        _modAdded = ModAdded.Subscriber(pi, _ => RaiseModsChanged());
        _modDeleted = ModDeleted.Subscriber(pi, _ => RaiseModsChanged());
        _modMoved = ModMoved.Subscriber(pi, (_, _) => RaiseModsChanged());
    }

    private void RaiseResourcesChanged()
    {
        try
        {
            ResourcesChanged?.Invoke();
        }
        catch (Exception e)
        {
            _log.Debug($"A resource change handler failed: {e.Message}");
        }
    }

    private void RaiseModsChanged()
    {
        try
        {
            ModsChanged?.Invoke();
        }
        catch (Exception e)
        {
            _log.Debug($"A mod list change handler failed: {e.Message}");
        }
    }

    /// <summary> Opens Penumbra's main window on the Mods tab with the given mod selected. </summary>
    public void OpenModInPenumbra(string modDirectory, string modName)
        => _openMainWindow?.Invoke(TabType.Mods, modDirectory, modName);

    public void Dispose()
    {
        _modSettingChanged?.Dispose();
        _gameObjectRedrawn?.Dispose();
        _modAdded?.Dispose();
        _modDeleted?.Dispose();
        _modMoved?.Dispose();
        _modSettingChanged = null;
        _gameObjectRedrawn = null;
        _modAdded = null;
        _modDeleted = null;
        _modMoved = null;
    }

    private AddTemporaryMod? _addTemporaryMod;

    private RemoveTemporaryMod? _removeTemporaryMod;

    private AddTemporaryModAll? _addTemporaryModAll;

    private RemoveTemporaryModAll? _removeTemporaryModAll;

    private ResolveInterfacePath? _resolveInterfacePath;

    private GetCollections? _getCollections;

    /// <summary> The effective collection of an object, with IndividualSet; null when the object has none. </summary>
    internal TemporaryModCollection? TemporaryModCollectionOnFramework(int objectIndex)
    {
        var (valid, individual, (id, name)) = _getCollectionForObject.Invoke(objectIndex);
        return valid && id != Guid.Empty ? new TemporaryModCollection(id, name ?? string.Empty, individual) : null;
    }

    internal Task<TemporaryModCollection?> TemporaryModCollectionAsync(int objectIndex)
        => _framework.RunOnFrameworkThread(() => TemporaryModCollectionOnFramework(objectIndex));

    /// <summary>
    /// Registers, or with the same tag and priority updates in place, a temporary mod in one
    /// collection. Rooted values are file redirects; no meta manipulations.
    /// </summary>
    internal PenumbraApiEc AddTemporaryModOnFramework(string tag, Guid collection, IReadOnlyDictionary<string, string> paths, int priority)
    {
        if (collection == Guid.Empty)
            throw new InvalidDataException("The temporary mod is invalid.");
        return (_addTemporaryMod ??= new AddTemporaryMod(_pi)).Invoke(tag, collection, Redirects(tag, paths), string.Empty, priority);
    }

    /// <summary>
    /// Registers, or with the same tag and priority updates in place, a temporary mod in every
    /// collection, for a file whose game path nothing else uses (such as a sound the Timeline plays
    /// to listen to). Rooted values are file redirects; no meta manipulations.
    /// </summary>
    internal PenumbraApiEc AddTemporaryModAllOnFramework(string tag, IReadOnlyDictionary<string, string> paths, int priority)
        => (_addTemporaryModAll ??= new AddTemporaryModAll(_pi)).Invoke(tag, Redirects(tag, paths), string.Empty, priority);

    /// <summary> What the Interface collection resolves <paramref name="gamePath"/> to: the game path itself when nothing changes it. </summary>
    internal string ResolveInterfacePathOnFramework(string gamePath)
        => (_resolveInterfacePath ??= new ResolveInterfacePath(_pi)).Invoke(gamePath);

    /// <summary> Removes a temporary mod from every collection; Gone as for <see cref="RemoveTemporaryModOnFramework"/>. </summary>
    internal (bool Gone, string? Error) RemoveTemporaryModAllOnFramework(string tag, int priority)
    {
        try
        {
            var result = (_removeTemporaryModAll ??= new RemoveTemporaryModAll(_pi)).Invoke(tag, priority);
            return result is PenumbraApiEc.Success or PenumbraApiEc.NothingChanged
                ? (true, null)
                : (false, $"Penumbra could not remove the temporary mod ({result}).");
        }
        catch (IpcNotReadyError)
        {
            return (true, null);
        }
    }

    private static Dictionary<string, string> Redirects(string tag, IReadOnlyDictionary<string, string> paths)
    {
        if (string.IsNullOrWhiteSpace(tag) || paths.Count == 0)
            throw new InvalidDataException("The temporary mod is invalid.");
        var redirects = new Dictionary<string, string>(paths.Count, StringComparer.Ordinal);
        foreach (var (gamePath, file) in paths)
        {
            if (!Path.IsPathFullyQualified(file))
                throw new InvalidDataException($"The temporary mod file for {gamePath} is not a full path.");
            redirects.Add(gamePath, file);
        }
        return redirects;
    }

    /// <summary>
    /// Removes a temporary mod. Gone is true when Penumbra no longer holds it: removed, never
    /// registered, its collection deleted, or Penumbra not loaded (it keeps temporary mods in memory only).
    /// </summary>
    internal (bool Gone, string? Error) RemoveTemporaryModOnFramework(string tag, Guid collection, int priority)
    {
        try
        {
            var result = (_removeTemporaryMod ??= new RemoveTemporaryMod(_pi)).Invoke(tag, collection, priority);
            return result is PenumbraApiEc.Success or PenumbraApiEc.NothingChanged or PenumbraApiEc.CollectionMissing
                ? (true, null)
                : (false, $"Penumbra could not remove the preview ({result}).");
        }
        catch (IpcNotReadyError)
        {
            return (true, null);
        }
    }

    internal Task<(bool Gone, string? Error)> RemoveTemporaryModAsync(string tag, Guid collection, int priority)
        => _framework.RunOnFrameworkThread(() => RemoveTemporaryModOnFramework(tag, collection, priority));

    /// <summary>
    /// Removes a temporary mod from every collection, for when the record of where it was
    /// registered is lost. Gone is true only when no collection may still hold it.
    /// </summary>
    internal (bool Gone, string? Error) RemoveTemporaryModEverywhereOnFramework(string tag, int priority)
    {
        Dictionary<Guid, string> collections;
        try
        {
            collections = (_getCollections ??= new GetCollections(_pi)).Invoke();
        }
        catch (IpcNotReadyError)
        {
            return (true, null);
        }
        foreach (var collection in collections.Keys)
            if (RemoveTemporaryModOnFramework(tag, collection, priority) is { Gone: false } failed)
                return failed;
        return (true, null);
    }

    internal Task<(bool Gone, string? Error)> RemoveTemporaryModEverywhereAsync(string tag, int priority)
        => _framework.RunOnFrameworkThread(() => RemoveTemporaryModEverywhereOnFramework(tag, priority));

    /// <summary>
    /// Calls <paramref name="restarted"/> when Penumbra is disposed or initialized; either way it no
    /// longer holds any temporary mod. The handler runs on Penumbra's thread and must be cheap.
    /// The caller owns and disposes the subscription.
    /// </summary>
    internal IDisposable SubscribePenumbraLifecycle(Action restarted)
        => new LifecycleSubscription(Initialized.Subscriber(_pi, restarted), Disposed.Subscriber(_pi, restarted));

    private sealed class LifecycleSubscription(EventSubscriber initialized, EventSubscriber disposed) : IDisposable
    {
        public void Dispose()
        {
            initialized.Dispose();
            disposed.Dispose();
        }
    }
}
