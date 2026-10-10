using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace InstantEdit.Studio.Services;

/// <summary>
/// Thin wrapper around Penumbra's IPC channel.
/// All calls gracefully return null/false when Penumbra is unavailable.
/// </summary>
public sealed class PenumbraIpcService : IDisposable
{
    private readonly IDalamudPluginInterface _pi;
    private readonly IPluginLog              _log;

    // ── IPC subscribers (cached to avoid allocating on every call) ────────────

    private readonly ICallGateSubscriber<(int Breaking, int Features)>                  _apiVersion;
    private readonly ICallGateSubscriber<string>                                         _getModDirectory;
    private readonly ICallGateSubscriber<Dictionary<string, string>>                     _getModList;
    private readonly ICallGateSubscriber<string, string, (int, string, bool, bool)>      _getModPath;
    private readonly ICallGateSubscriber<Dictionary<Guid, string>>                       _getCollections;
    private readonly ICallGateSubscriber<string, int>                                    _addMod;
    private readonly ICallGateSubscriber<string, string, int>                            _reloadMod;
    private readonly ICallGateSubscriber<string, int>                                    _installMod;
    private readonly ICallGateSubscriber<string, string, int>                            _deleteMod;
    private readonly ICallGateSubscriber<string, string, Dictionary<string, object?>>    _getChangedItems;
    private readonly ICallGateSubscriber<int, object>                                    _redrawAll;
    private readonly ICallGateSubscriber<string, string, int, bool, Task>                _convertTexture;
    private readonly ICallGateSubscriber<byte, (Guid, string)?>                          _getCollection;
    private readonly ICallGateSubscriber<int, (bool, bool, (Guid, string))>              _getCollectionForObject;
    private readonly ICallGateSubscriber<Guid, string, string, bool, (int, (bool, int, Dictionary<string, List<string>>, bool)?)> _getCurrentModSettings;

    // ── Events ────────────────────────────────────────────────────────────────

    /// <summary>Raised when Penumbra signals it has fully initialised.</summary>
    public event Action? PenumbraInitialized;
    /// <summary>Raised when Penumbra is disposing.</summary>
    public event Action? PenumbraDisposed;

    /// <summary>A mod folder was added to Penumbra (an import finished, or a mod was created). May be raised off the framework thread.</summary>
    public event Action<string>? ModAdded;
    /// <summary>A mod was deleted from Penumbra. May be raised off the framework thread.</summary>
    public event Action<string>? ModDeleted;
    /// <summary>A mod folder was renamed (old name, new name). May be raised off the framework thread.</summary>
    public event Action<string, string>? ModMoved;
    /// <summary>Penumbra's mod directory changed (new directory, whether it is valid).</summary>
    public event Action<string, bool>? ModDirectoryChanged;
    /// <summary>A mod's settings changed in a collection (mod folder name). May be raised off the framework thread.</summary>
    public event Action<string>? ModSettingChanged;

    private ICallGateSubscriber<object>? _initializedSub;
    private ICallGateSubscriber<object>? _disposedSub;
    private ICallGateSubscriber<string, object>? _modAddedSub;
    private ICallGateSubscriber<string, object>? _modDeletedSub;
    private ICallGateSubscriber<string, string, object>? _modMovedSub;
    private ICallGateSubscriber<string, bool, object>? _modDirectorySub;
    private IDisposable? _modSettingSub;

    public PenumbraIpcService(IDalamudPluginInterface pi, IPluginLog log)
    {
        _pi  = pi;
        _log = log;

        _apiVersion      = pi.GetIpcSubscriber<(int, int)>                       ("Penumbra.ApiVersion.V5");
        _getModDirectory = pi.GetIpcSubscriber<string>                           ("Penumbra.GetModDirectory");
        _getModList      = pi.GetIpcSubscriber<Dictionary<string, string>>       ("Penumbra.GetModList");
        _getModPath      = pi.GetIpcSubscriber<string, string, (int, string, bool, bool)>("Penumbra.GetModPath.V5");
        _getCollections  = pi.GetIpcSubscriber<Dictionary<Guid, string>>         ("Penumbra.GetCollections.V5");
        _addMod          = pi.GetIpcSubscriber<string, int>                      ("Penumbra.AddMod.V5");
        _reloadMod       = pi.GetIpcSubscriber<string, string, int>              ("Penumbra.ReloadMod.V5");
        _installMod      = pi.GetIpcSubscriber<string, int>                      ("Penumbra.InstallMod.V5");
        _deleteMod       = pi.GetIpcSubscriber<string, string, int>              ("Penumbra.DeleteMod.V5");
        _getChangedItems = pi.GetIpcSubscriber<string, string, Dictionary<string, object?>>("Penumbra.GetChangedItems.V5");
        _redrawAll       = pi.GetIpcSubscriber<int, object>                      ("Penumbra.RedrawAll.V5");
        _convertTexture  = pi.GetIpcSubscriber<string, string, int, bool, Task>  ("Penumbra.ConvertTextureFile");
        _getCollection   = pi.GetIpcSubscriber<byte, (Guid, string)?>            ("Penumbra.GetCollection");
        _getCollectionForObject = pi.GetIpcSubscriber<int, (bool, bool, (Guid, string))>("Penumbra.GetCollectionForObject.V5");
        _getCurrentModSettings  = pi.GetIpcSubscriber<Guid, string, string, bool, (int, (bool, int, Dictionary<string, List<string>>, bool)?)>("Penumbra.GetCurrentModSettings.V5");

        try
        {
            _initializedSub = pi.GetIpcSubscriber<object>("Penumbra.Initialized");
            _initializedSub.Subscribe(OnPenumbraInitialized);
        }
        catch (Exception ex) { _log.Debug(ex, "[Studio] Could not subscribe to Penumbra.Initialized"); }

        try
        {
            _disposedSub = pi.GetIpcSubscriber<object>("Penumbra.Disposed");
            _disposedSub.Subscribe(OnPenumbraDisposed);
        }
        catch (Exception ex) { _log.Debug(ex, "[Studio] Could not subscribe to Penumbra.Disposed"); }

        try
        {
            _modAddedSub = pi.GetIpcSubscriber<string, object>("Penumbra.ModAdded");
            _modAddedSub.Subscribe(OnModAdded);
            _modDeletedSub = pi.GetIpcSubscriber<string, object>("Penumbra.ModDeleted");
            _modDeletedSub.Subscribe(OnModDeleted);
            _modMovedSub = pi.GetIpcSubscriber<string, string, object>("Penumbra.ModMoved");
            _modMovedSub.Subscribe(OnModMoved);
            _modDirectorySub = pi.GetIpcSubscriber<string, bool, object>("Penumbra.ModDirectoryChanged");
            _modDirectorySub.Subscribe(OnModDirectoryChanged);
        }
        catch (Exception ex) { _log.Debug(ex, "[Studio] Could not subscribe to Penumbra's mod events"); }

        try
        {
            _modSettingSub = Penumbra.Api.IpcSubscribers.ModSettingChanged.Subscriber(pi,
                (Penumbra.Api.Enums.ModSettingChange _, Guid _, string mod, bool _) => ModSettingChanged?.Invoke(mod));
        }
        catch (Exception ex) { _log.Debug(ex, "[Studio] Could not subscribe to Penumbra.ModSettingChanged"); }
    }

    private void OnPenumbraInitialized() => PenumbraInitialized?.Invoke();
    private void OnPenumbraDisposed()    => PenumbraDisposed?.Invoke();
    private void OnModAdded(string dir)                     => ModAdded?.Invoke(dir);
    private void OnModDeleted(string dir)                   => ModDeleted?.Invoke(dir);
    private void OnModMoved(string from, string to)         => ModMoved?.Invoke(from, to);
    private void OnModDirectoryChanged(string dir, bool ok) => ModDirectoryChanged?.Invoke(dir, ok);

    public void Dispose()
    {
        try { _initializedSub?.Unsubscribe(OnPenumbraInitialized); } catch { /* ignore */ }
        try { _disposedSub?.Unsubscribe(OnPenumbraDisposed); }       catch { /* ignore */ }
        try { _modAddedSub?.Unsubscribe(OnModAdded); }               catch { /* ignore */ }
        try { _modDeletedSub?.Unsubscribe(OnModDeleted); }           catch { /* ignore */ }
        try { _modMovedSub?.Unsubscribe(OnModMoved); }               catch { /* ignore */ }
        try { _modDirectorySub?.Unsubscribe(OnModDirectoryChanged); } catch { /* ignore */ }
        try { _modSettingSub?.Dispose(); }                            catch { /* ignore */ }
    }

    /// <summary>Returns true when Penumbra is loaded and its IPC is reachable.</summary>
    public bool IsAvailable
    {
        get
        {
            try
            {
                var (breaking, _) = _apiVersion.InvokeFunc();
                return breaking == 5;
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// Whether Penumbra can convert textures for us. Its converter is the one behind its own texture
    /// editor — native DirectXTex through OtterTex — and is worth using over the managed encoder
    /// bundled here, which is the same job in plain C# and minutes slower on a large image.
    /// </summary>
    public bool CanConvertTextures
    {
        get
        {
            try   { return _convertTexture.HasFunction; }
            catch { return false; }
        }
    }

    /// <summary>
    /// Asks Penumbra to convert an image into a .tex, returning the task it runs it on, or null when
    /// it cannot. <paramref name="textureType"/> is Penumbra's own TextureType (7 = BC7, 5 = BC3,
    /// 3 = RGBA, all as .tex). Call on the framework thread.
    /// </summary>
    public Task? ConvertTextureFile(string inputFile, string outputFile, int textureType, bool mipMaps = true)
    {
        try   { return _convertTexture.InvokeFunc(inputFile, outputFile, textureType, mipMaps); }
        catch (Exception ex)
        {
            _log.Debug(ex, "[Studio] Penumbra could not convert {0}", inputFile);
            return null;
        }
    }

    /// <summary>Returns Penumbra's configured mod root directory, or null on failure.</summary>
    public string? GetModDirectory()
    {
        try   { return _getModDirectory.InvokeFunc(); }
        catch (Exception ex) { _log.Warning(ex, "[Studio] GetModDirectory failed"); return null; }
    }

    /// <summary>Returns a dict of modDirectory → modName for all known mods.</summary>
    public Dictionary<string, string>? GetModList()
    {
        try   { return _getModList.InvokeFunc(); }
        catch (Exception ex) { _log.Warning(ex, "[Studio] GetModList failed"); return null; }
    }

    /// <summary>
    /// The path a mod is filed under in Penumbra's own mod list (its virtual folders, e.g.
    /// "Gear/Shoes/My mod") — not where it lives on disk, which is always the mod directory plus
    /// <paramref name="modDirectory"/>. Empty when Penumbra cannot say.
    /// </summary>
    public string GetModSortPath(string modDirectory, string modName = "")
    {
        try
        {
            var (ret, sortPath, _, _) = _getModPath.InvokeFunc(modDirectory, modName);
            return (PenumbraApiEc)ret == PenumbraApiEc.Success ? sortPath : string.Empty;
        }
        catch (Exception ex) { _log.Debug(ex, "[Studio] GetModPath failed"); return string.Empty; }
    }

    /// <summary>
    /// Asks Penumbra to read a mod it already knows from disk again — files, options and meta —
    /// after this plugin wrote into it. <see cref="PenumbraApiEc.ModMissing"/> means Penumbra does not
    /// know the folder yet; <see cref="AddMod"/> registers it then.
    /// </summary>
    public PenumbraApiEc ReloadMod(string modDirectory, string modName = "")
    {
        try   { return (PenumbraApiEc)_reloadMod.InvokeFunc(modDirectory, modName); }
        catch (Exception ex) { _log.Warning(ex, "[Studio] ReloadMod failed"); return PenumbraApiEc.UnknownError; }
    }

    /// <summary>
    /// Queues a mod package (.pmp, .ttmp, .ttmp2) for import. Penumbra imports it in the background;
    /// the new folder is announced through <see cref="ModAdded"/> once it is done.
    /// </summary>
    public PenumbraApiEc InstallMod(string packagePath)
    {
        try   { return (PenumbraApiEc)_installMod.InvokeFunc(packagePath); }
        catch (Exception ex) { _log.Warning(ex, "[Studio] InstallMod failed"); return PenumbraApiEc.UnknownError; }
    }

    /// <summary>Deletes a mod (its folder included) through Penumbra.</summary>
    public PenumbraApiEc DeleteMod(string modDirectory, string modName = "")
    {
        try   { return (PenumbraApiEc)_deleteMod.InvokeFunc(modDirectory, modName); }
        catch (Exception ex) { _log.Warning(ex, "[Studio] DeleteMod failed"); return PenumbraApiEc.UnknownError; }
    }

    /// <summary>The names of the game items a mod changes, as Penumbra identifies them, or null.</summary>
    public IReadOnlyCollection<string>? GetChangedItems(string modDirectory, string modName = "")
    {
        try   { return _getChangedItems.InvokeFunc(modDirectory, modName)?.Keys; }
        catch (Exception ex) { _log.Debug(ex, "[Studio] GetChangedItems failed"); return null; }
    }

    /// <summary>
    /// The collection the player character uses (their own assignment, or whatever it inherits),
    /// else Penumbra's "current" collection. Null when Penumbra cannot say.
    /// </summary>
    public (Guid Id, string Name)? PlayerCollection()
    {
        try
        {
            var (valid, _, collection) = _getCollectionForObject.InvokeFunc(0);
            if (valid && collection.Item1 != Guid.Empty)
                return collection;
        }
        catch (Exception ex) { _log.Debug(ex, "[Studio] GetCollectionForObject failed"); }

        try   { return _getCollection.InvokeFunc(226); }   // ApiCollectionType.Current
        catch (Exception ex) { _log.Debug(ex, "[Studio] GetCollection failed"); return null; }
    }

    /// <summary>
    /// How a mod is set in a collection: whether it is enabled, and the options picked in each of its
    /// groups (by group and option name). Null when Penumbra cannot say.
    /// </summary>
    public (bool Enabled, Dictionary<string, List<string>> Options)? ModSettings(Guid collection, string modDirectory)
    {
        try
        {
            var (ec, settings) = _getCurrentModSettings.InvokeFunc(collection, modDirectory, string.Empty, false);
            if ((PenumbraApiEc)ec != PenumbraApiEc.Success || settings is not { } s)
                return null;
            return (s.Item1, s.Item3 ?? new Dictionary<string, List<string>>());
        }
        catch (Exception ex) { _log.Debug(ex, "[Studio] GetCurrentModSettings failed"); return null; }
    }

    /// <summary>Whether a mod is on in a collection (inherited settings included) and at what priority. Null when it has no settings there.</summary>
    public (bool Enabled, int Priority)? ModState(Guid collection, string modDirectory)
    {
        try
        {
            var (ec, settings) = _getCurrentModSettings.InvokeFunc(collection, modDirectory, string.Empty, false);
            if ((PenumbraApiEc)ec != PenumbraApiEc.Success || settings is not { } s)
                return null;
            return (s.Item1, s.Item2);
        }
        catch (Exception ex) { _log.Debug(ex, "[Studio] GetCurrentModSettings failed"); return null; }
    }

    private Penumbra.Api.IpcSubscribers.ResolvePlayerPathsAsync? _resolvePlayerPaths;

    /// <summary>
    /// What the player's collection resolves game paths to: a file on disk or another game path, the
    /// path itself where nothing redirects it. May be called off the framework thread.
    /// </summary>
    public async Task<string[]?> ResolvePlayerPaths(string[] gamePaths)
    {
        try
        {
            _resolvePlayerPaths ??= new Penumbra.Api.IpcSubscribers.ResolvePlayerPathsAsync(_pi);
            var (forward, _) = await _resolvePlayerPaths.Invoke(gamePaths, Array.Empty<string>()).ConfigureAwait(false);
            return forward;
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "[Studio] ResolvePlayerPathsAsync failed");
            return null;
        }
    }

    /// <summary>Redraws every actor, so changed files show without re-equipping anything.</summary>
    public void RedrawAll()
    {
        try   { _redrawAll.InvokeAction(0); }   // RedrawType.Redraw
        catch (Exception ex) { _log.Debug(ex, "[Studio] RedrawAll failed"); }
    }

    private Penumbra.Api.IpcSubscribers.GetAllModSettings? _getAllModSettings;
    private Penumbra.Api.IpcSubscribers.GetChangedItemAdapterList? _getChangedItemList;
    private Penumbra.Api.IpcSubscribers.OpenMainWindow? _openMainWindow;

    /// <summary>
    /// Whether each mod is enabled in a collection, by mod folder name: inherited settings count,
    /// temporary settings from other plugins don't. Null when Penumbra cannot say. May be called off
    /// the framework thread.
    /// </summary>
    public IReadOnlyDictionary<string, bool>? GetAllModSettings(Guid collection)
    {
        try
        {
            _getAllModSettings ??= new Penumbra.Api.IpcSubscribers.GetAllModSettings(_pi);
            var (ec, settings) = _getAllModSettings.Invoke(collection, false, true, 0);
            if (ec != Penumbra.Api.Enums.PenumbraApiEc.Success || settings is null)
                return null;
            var enabled = new Dictionary<string, bool>(settings.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var (mod, setting) in settings)
                enabled[mod] = setting.Item1;
            return enabled;
        }
        catch (Exception ex) { _log.Debug(ex, "[Studio] GetAllModSettings failed"); return null; }
    }

    /// <summary>
    /// Penumbra's identified changed items of every mod, by mod folder name, with what kind of thing
    /// each is. Null when Penumbra cannot say. May be called off the framework thread.
    /// </summary>
    internal IReadOnlyDictionary<string, IReadOnlyList<(string Name, Library.ChangeKind Kind)>>? ChangedItemKinds()
    {
        try
        {
            _getChangedItemList ??= new Penumbra.Api.IpcSubscribers.GetChangedItemAdapterList(_pi);
            var result = new Dictionary<string, IReadOnlyList<(string, Library.ChangeKind)>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (mod, items) in _getChangedItemList.Invoke())
                result[mod] = items.Select(item => (item.Key, KindOf(item.Value))).ToArray();
            return result;
        }
        catch (Exception ex) { _log.Debug(ex, "[Studio] GetChangedItemAdapterList failed"); return null; }
    }

    /// <summary>What a changed item is, from the type of Penumbra's object for it (Mod Optimizer's rules).</summary>
    private static Library.ChangeKind KindOf(object? value) => value?.GetType().Name switch
    {
        "EquipItem"                                              => Library.ChangeKind.Item,
        "Emote"                                                  => Library.ChangeKind.Emote,
        { } name when name.StartsWith("ValueTuple", StringComparison.Ordinal) => Library.ChangeKind.Face,
        _                                                        => Library.ChangeKind.Other,
    };

    /// <summary>Opens Penumbra's main window on the mods tab with a mod selected; null on success, else what went wrong.</summary>
    public string? OpenModPage(string folderName)
    {
        try
        {
            _openMainWindow ??= new Penumbra.Api.IpcSubscribers.OpenMainWindow(_pi);
            return _openMainWindow.Invoke(Penumbra.Api.Enums.TabType.Mods, folderName, string.Empty) switch
            {
                Penumbra.Api.Enums.PenumbraApiEc.Success    => null,
                Penumbra.Api.Enums.PenumbraApiEc.ModMissing => "Penumbra could not find this mod. Refresh and try again.",
                var ec                                      => $"Penumbra could not select the mod ({ec}).",
            };
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "[Studio] OpenMainWindow failed");
            return "Penumbra's main window could not be opened.";
        }
    }

    /// <summary>Returns all Penumbra collections as (Guid Id, string Name) pairs, or null on failure.</summary>
    public Dictionary<Guid, string>? GetCollections()
    {
        try   { return _getCollections.InvokeFunc(); }
        catch (Exception ex) { _log.Debug(ex, "[Studio] GetCollections failed"); return null; }
    }

    /// <summary>
    /// Registers a mod folder that already exists on disk with Penumbra, without
    /// requiring a full mod-directory rediscovery. <paramref name="modDirectory"/>
    /// is the folder name under the Penumbra root (not a full path). Returns
    /// Success both when newly added and when it was already loaded.
    /// </summary>
    public PenumbraApiEc AddMod(string modDirectory)
    {
        try   { return (PenumbraApiEc)_addMod.InvokeFunc(modDirectory); }
        catch (Exception ex) { _log.Warning(ex, "[Studio] AddMod failed"); return PenumbraApiEc.UnknownError; }
    }
}

/// <summary>Mirrors Penumbra.Api's PenumbraApiEc enum (int values, as of API 5 / Penumbra 1.7).</summary>
public enum PenumbraApiEc : int
{
    Success                      = 0,
    NothingChanged               = 1,
    CollectionMissing            = 2,
    ModMissing                   = 3,
    OptionGroupMissing           = 4,
    OptionMissing                = 5,
    CharacterCollectionExists    = 6,
    LowerPriority                = 7,
    InvalidGamePath              = 8,
    FileMissing                  = 9,
    InvalidManipulation          = 10,
    InvalidArgument              = 11,
    PathRenameFailed             = 12,
    CollectionExists             = 13,
    AssignmentCreationDisallowed = 14,
    AssignmentDeletionDisallowed = 15,
    InvalidIdentifier            = 16,
    SystemDisposed               = 17,
    AssignmentDeletionFailed     = 18,
    TemporarySettingDisallowed   = 19,
    TemporarySettingImpossible   = 20,
    InvalidCredentials           = 21,
    CollectionInactive           = 22,
    UnknownError                 = 255,
}
