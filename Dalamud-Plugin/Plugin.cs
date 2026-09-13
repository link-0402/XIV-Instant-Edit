using Dalamud.Game.Command;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using InstantEdit.Services;
using InstantEdit.Services.Animations;
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
    private readonly ExportContextRegistry   _contexts;
    private readonly BlenderClient           _blender;
    private readonly TextureEditService      _textures;
    private readonly AnimationEditService?   _animations;
    private readonly ExportServer            _exportServer;
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
        ISigScanner sigScanner)
    {
        _pi       = pi;
        _commands = commands;
        _log      = log;

        _config    = pi.GetPluginConfig() as Configuration ?? new Configuration();
        var cacheConfigurationMigrated = MigrateCacheConfiguration(_config);
        var pluginInstanceId = Guid.NewGuid().ToString("N");
        IReadOnlyList<Models.PersistedExportContext> persistedContexts = _config.ExportContexts;
        try
        {
            _contextStore = new ExportContextSessionStore(
                pi.ConfigDirectory.FullName,
                pluginInstanceId,
                (message, error) =>
                {
                    if (error is null) _log.Warning(message);
                    else _log.Warning(error, message);
                });
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
        _backups   = new ModelBackupStore(pi.ConfigDirectory.FullName);
        _penumbra  = new PenumbraService(pi, framework, log, objects, data, _backups);
        _onScreen  = new OnScreenService(objects, clientState, framework, _penumbra, log);
        _contexts  = new ExportContextRegistry(
            pluginInstanceId,
            persistedContexts,
            contexts =>
            {
                if (_contextStore is not null)
                {
                    _contextStore.Persist(contexts);
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
        _textures = new TextureEditService(_penumbra, _config, pi.ConfigDirectory.FullName, _backups,
            (error, message) => log.Warning(error, message));
        string? animationError = null;
        try { _animations = new AnimationEditService(pi, framework, objects, data, sigScanner, _penumbra, _backups, _config, log); }
        catch (Exception error)
        {
            animationError = "Animation integration is unavailable: " + error.Message;
            log.Warning(error, "Could not initialize animation editing; other features remain available.");
        }
        if (_config.AutomaticCacheCleanup)
            _textures.RequestCacheCleanup();
        _exportServer = new ExportServer(_config, _penumbra, _contexts, log);
        _changelogWindow = new ChangelogWindow(_config, BlenderClient.CurrentPluginVersion, SaveConfiguration);
        _window    = new MainWindow(
            _config,
            _penumbra,
            _onScreen,
            _blender,
            data,
            chat,
            log,
            SaveConfiguration,
            () => _exportServer.Restart(),
            _pi.UiBuilder,
            textureProvider,
            _textures,
            _changelogWindow.Open);
        _window.AttachAnimations(_animations, animationError);
        _exportServer.ImportFailureReceived += _window.ReportImportFailure;
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
            OpenSetupFromSettings);

        _windowSystem = new WindowSystem();
        _windowSystem.AddWindow(_window);
        _windowSystem.AddWindow(_changelogWindow);
        _windowSystem.AddWindow(_setupWindow);

        _pi.UiBuilder.Draw += _windowSystem.Draw;
        _pi.UiBuilder.Draw += _settingsWindow.Draw;
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
        _pi.UiBuilder.Draw -= _settingsWindow.Draw;
        _pi.UiBuilder.Draw -= _setupWindow.DrawFileDialog;
        _pi.UiBuilder.OpenMainUi -= OpenMainUi;
        _pi.UiBuilder.OpenConfigUi -= _settingsWindow.Open;
        _windowSystem.RemoveWindow(_window);
        _windowSystem.RemoveWindow(_changelogWindow);
        _windowSystem.RemoveWindow(_setupWindow);
        _window.Dispose();
        _animations?.Dispose();
        _textures.Dispose();
        _exportServer.ImportFailureReceived -= _window.ReportImportFailure;
        _exportServer.Dispose();
        _contexts.Dispose();
        _blender.Dispose();
        SaveConfiguration();
    }
}
