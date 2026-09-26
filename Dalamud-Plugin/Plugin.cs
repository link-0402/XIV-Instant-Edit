using Dalamud.Game.Command;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using InstantEdit.Services;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Previews;
using InstantEdit.Services.Skeletons;
using InstantEdit.Ui;

namespace InstantEdit;

public sealed class Plugin : IDalamudPlugin
{
    private readonly object                    _configLock = new();
    private readonly IDalamudPluginInterface _pi;
    private readonly ICommandManager         _commands;
    private readonly IPluginLog              _log;
    private readonly Configuration           _config;
    private readonly ExportContextSessionStore? _contextStore;
    private readonly ModelBackupStore          _backups;
    private readonly PenumbraService         _penumbra;
    private readonly OnScreenService         _onScreen;
    private readonly GlamourerService        _glamourer;
    private readonly ExportContextRegistry   _contexts;
    private readonly BlenderClient           _blender;
    private readonly TextureEditService      _textures;
    private readonly PreviewService          _previews;
    private readonly AnimationEditService?   _animations;
    private readonly AnimationRecorder       _recorder;
    private readonly ExportServer            _exportServer;
    private readonly Services.Painter.PainterJobService _painterJobs;
    private readonly WindowSystem            _windowSystem;
    private readonly MainWindow              _window;
    private readonly ChangelogWindow         _changelogWindow;
    private readonly FirstTimeSetupWindow   _setupWindow;
    private readonly SettingsWindow          _settingsWindow;

    public string Name => "XIV Instant Edit";

    public Plugin(
        IDalamudPluginInterface pi,
        ICommandManager commands,
        IChatGui chat,
        IClientState clientState,
        IPluginLog log,
        IDataManager data,
        ITextureProvider textureProvider,
        IObjectTable objects,
        IFramework framework,
        ISigScanner sigScanner,
        INotificationManager notifications,
        ITargetManager targets)
    {
        _pi       = pi;
        _commands = commands;
        _log      = log;

        _config    = pi.GetPluginConfig() as Configuration ?? new Configuration();
        var cacheConfigurationMigrated = MigrateCacheConfiguration(_config);
        var configDirectory = pi.ConfigDirectory.FullName;
        Action<string, Exception?> logStoreWarning = (message, error) =>
        {
            if (error is null) _log.Warning(message);
            else _log.Warning(error, message);
        };
        // Contexts are durable plugin state and live in the config folder; 1.2.x
        // kept them in the managed cache. Move them out before the ownership check,
        // since they are what keeps an unmarked cache non-empty.
        try { ExportContextSessionStore.ImportLegacy(TextureFiles.CacheRootFor(_config.TextureCacheDirectory), configDirectory, logStoreWarning); }
        catch (Exception error) { _log.Warning(error, "Could not move export contexts out of the cache."); }
        // A cache problem must not stop the plugin from loading: the fix is choosing
        // another cache directory in Settings, which needs the plugin running.
        string? cacheRoot = null;
        var cacheStartupError = "";
        try { cacheRoot = TextureFiles.EnsureCacheRoot(_config.TextureCacheDirectory); }
        catch (Exception error)
        {
            cacheStartupError = error.Message;
            _log.Error(error, "Could not open the XIV Instant Edit cache; cache-backed features are unavailable until it is fixed.");
        }
        // Backups and the texture session catalog normally live in the cache, which
        // Blender shares; the config folder keeps them working until the cache is fixed.
        var storageRoot = cacheRoot ?? configDirectory;
        var pluginInstanceId = Guid.NewGuid().ToString("N");
        IReadOnlyList<Models.PersistedExportContext> persistedContexts = _config.ExportContexts;
        try
        {
            _contextStore = new ExportContextSessionStore(configDirectory, pluginInstanceId, logStoreWarning);
            var stored = _contextStore.Load();
            persistedContexts = stored
                .Concat(_config.ExportContexts)
                .GroupBy(item => item.ContextId, StringComparer.Ordinal)
                .Select(group => group.Last())
                .ToArray();
            if (_config.ExportContexts.Count > 0)
                _contextStore.Persist(persistedContexts);
            _config.ExportContexts = [];
            _config.Version = 10;
            pi.SavePluginConfig(_config);
        }
        catch (Exception error)
        {
            _contextStore = null;
            _log.Error(error, "Could not initialize per-session context storage; retaining contexts in plugin settings.");
        }
        pi.UiBuilder.DisableUserUiHide = _config.KeepVisibleWhenUiHidden;
        _backups   = new ModelBackupStore(storageRoot);
        _penumbra  = new PenumbraService(pi, framework, log, objects, data, _backups);
        // One attributor serves the On Screen snapshot and the Edit/material-preview flows,
        // so both share its mod index and cached stable identifiers.
        var resourceSources = new ResourceSourceAttributor(_penumbra, log);
        // A new, deleted or renamed mod must show up in the next attribution, not up to 5 s later.
        _penumbra.ModsChanged += resourceSources.Invalidate;
        _onScreen  = new OnScreenService(objects, clientState, framework, _penumbra, resourceSources, log);
        _glamourer = new GlamourerService(pi, log);
        _contexts  = new ExportContextRegistry(
            pluginInstanceId,
            persistedContexts,
            contexts =>
            {
                if (_contextStore is not null)
                {
                    // A failed write must not fail the request that caused it (an export may already be
                    // applied). The store keeps the pending change, and the next save writes it again.
                    try { _contextStore.Persist(contexts); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    { _log.Warning(error, "Could not save export contexts; the next change retries."); }
                    return;
                }
                lock (_configLock)
                {
                    _config.ExportContexts = contexts.ToList();
                    _pi.SavePluginConfig(_config);
                }
            },
            _backups);
        _blender   = new BlenderClient(log, _contexts);
        _textures = new TextureEditService(_penumbra, _config, storageRoot, _backups,
            (error, message) => log.Warning(error, message));
        string? animationError = null;
        try { _animations = new AnimationEditService(pi, framework, objects, data, sigScanner, _penumbra, _backups, _config, log); }
        catch (Exception error)
        {
            animationError = "Animation integration is unavailable: " + error.Message;
            log.Warning(error, "Could not initialize animation editing; other features remain available.");
        }
        _recorder = new AnimationRecorder(framework, objects, clientState, targets);
        if (_config.AutomaticCacheCleanup)
            _textures.RequestCacheCleanup();
        _exportServer = new ExportServer(_config, _penumbra, _contexts, log);
        var painterStore = new Services.Painter.PainterJobStore(configDirectory);
        painterStore.Load();
        if (painterStore.LoadError.Length > 0)
            log.Warning(painterStore.LoadError);
        Func<string, CancellationToken, Task<byte[]?>> readGameFile =
            async (gamePath, token) => (await data.GetFileAsync<Lumina.Data.FileResource>(gamePath, token).ConfigureAwait(false))?.Data;
        Action<Exception, string> logPainter = (error, message) => log.Warning(error, message);
        _painterJobs = new Services.Painter.PainterJobService(_config, _textures,
            new Services.Painter.PainterProjectBuilder(new MaterialPreviewBundleBuilder(data, log, resourceSources), readGameFile, logPainter),
            new Services.Painter.PainterClient(), painterStore, readGameFile, logPainter);
        _exportServer.AttachPainter(_painterJobs);
        _previews = new PreviewService(textureProvider, data, log, () => _config.RenderModelThumbnails);
        _textures.FileChanged += _previews.Invalidate;
        _changelogWindow = new ChangelogWindow(_config, BlenderClient.CurrentPluginVersion, SaveConfiguration);
        _window    = new MainWindow(
            _config,
            _penumbra,
            _onScreen,
            resourceSources,
            _blender,
            data,
            chat,
            log,
            SaveConfiguration,
            () => _exportServer.Restart(),
            _pi.UiBuilder,
            textureProvider,
            _textures,
            notifications,
            _previews,
            _changelogWindow.Open,
            () => _settingsWindow!.Open());
        _window.AttachAnimations(_animations, animationError);
        _window.AttachRecorder(_recorder);
        _window.AttachPainter(_painterJobs);
        // Game skeletons for Blender's armatures: sent with imports, and asked for by the add-on.
        var skeletons = new ModelSkeletonResolver(_penumbra, data, framework, objects, log);
        _window.AttachSkeletons(skeletons);
        _exportServer.Skeletons = skeletons;
        _exportServer.ImportFailureReceived += _window.ReportImportFailure;
        _glamourer.AppearanceChanged += _window.OnGlamourerAppearanceChanged;
        _setupWindow = new FirstTimeSetupWindow(
            _config,
            SaveConfiguration,
            _window.RequestCacheSynchronization,
            _window.Open,
            _log);
        _settingsWindow = new SettingsWindow(
            _config,
            SaveConfiguration,
            () => _exportServer.Restart(),
            _log,
            _window.RequestCacheSynchronization,
            OpenSetupFromSettings,
            cacheStartupError);
        _settingsWindow.AttachAnimations(_animations, animationError);

        _windowSystem = new WindowSystem();
        _windowSystem.AddWindow(_window);
        _windowSystem.AddWindow(_changelogWindow);
        _windowSystem.AddWindow(_setupWindow);
        _windowSystem.AddWindow(_settingsWindow);

        _pi.UiBuilder.Draw += _windowSystem.Draw;
        _pi.UiBuilder.Draw += _setupWindow.DrawFileDialog;
        _pi.UiBuilder.OpenMainUi += OpenMainUi;
        _pi.UiBuilder.OpenConfigUi += _settingsWindow.Open;

        _commands.AddHandler("/ie", new CommandInfo(OnCommand)
        {
            HelpMessage = "Opens the XIV Instant Edit window. Use /ie refresh to refresh the on-screen list.",
        });

        try
        {
            _exportServer.Start();
        }
        catch (Exception e)
        {
            // A listener conflict must not prevent the rest of the plugin from loading.
            _log.Error(e, "XIV Instant Edit export receiver could not start.");
        }

        if (cacheConfigurationMigrated)
            SaveConfiguration();
        _log.Information("XIV Instant Edit loaded.");
    }

    private void OnCommand(string command, string args)
    {
        if (args.Trim().Equals("refresh", StringComparison.OrdinalIgnoreCase))
        {
            _onScreen.RequestRefresh();
            return;
        }

        ToggleMainUi();
    }

    private void OpenMainUi()
    {
        if (!_config.FirstTimeSetupCompleted)
        {
            _setupWindow.Open();
            return;
        }

        _window.Open();
    }

    private void ToggleMainUi()
    {
        if (!_config.FirstTimeSetupCompleted)
        {
            _setupWindow.Open();
            return;
        }

        _window.Toggle();
    }

    private void OpenSetupFromSettings()
    {
        _window.Close();
        _setupWindow.Open();
    }

    private void SaveConfiguration()
    {
        lock (_configLock)
            _pi.SavePluginConfig(_config);
    }

    private static bool MigrateCacheConfiguration(Configuration config)
    {
        if (!string.IsNullOrWhiteSpace(config.TextureCacheDirectory))
            return false;

        if (!string.IsNullOrWhiteSpace(config.TextureCacheRoot))
        {
            try
            {
                var legacyRoot = Path.GetFullPath(config.TextureCacheRoot.Trim().Trim('"'));
                var legacyFolder = Path.GetFileName(legacyRoot);
                config.TextureCacheDirectory =
                    (string.Equals(legacyFolder, TextureFiles.CacheFolder, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(legacyFolder, TextureFiles.LegacyCacheFolder, StringComparison.OrdinalIgnoreCase))
                    ? Path.GetDirectoryName(legacyRoot) ?? Path.GetTempPath()
                    : legacyRoot;
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                config.TextureCacheDirectory = Path.GetTempPath();
            }
        }
        else
        {
            config.TextureCacheDirectory = Path.GetTempPath();
        }

        config.TextureCacheRoot = "";
        return true;
    }

    public void Dispose()
    {
        _commands.RemoveHandler("/ie");
        _pi.UiBuilder.Draw -= _windowSystem.Draw;
        _pi.UiBuilder.Draw -= _setupWindow.DrawFileDialog;
        _pi.UiBuilder.OpenMainUi -= OpenMainUi;
        _pi.UiBuilder.OpenConfigUi -= _settingsWindow.Open;
        _windowSystem.RemoveWindow(_window);
        _windowSystem.RemoveWindow(_changelogWindow);
        _windowSystem.RemoveWindow(_setupWindow);
        _windowSystem.RemoveWindow(_settingsWindow);
        _glamourer.AppearanceChanged -= _window.OnGlamourerAppearanceChanged;
        _glamourer.Dispose();
        _recorder.Dispose();
        _window.Dispose();
        _textures.FileChanged -= _previews.Invalidate;
        _previews.Dispose();
        _onScreen.Dispose();
        _animations?.Dispose();
        _painterJobs.Dispose();
        _textures.Dispose();
        _exportServer.ImportFailureReceived -= _window.ReportImportFailure;
        _exportServer.Dispose();
        _contexts.Dispose();
        _blender.Dispose();
        _penumbra.Dispose();
        SaveConfiguration();
    }
}
