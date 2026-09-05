using Dalamud.Game.Command;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using InstantEdit.Services;
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
    private readonly ExportServer            _exportServer;
    private readonly WindowSystem            _windowSystem;
    private readonly MainWindow              _window;
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
        IFramework framework)
    {
        _pi       = pi;
        _commands = commands;
        _log      = log;

        _config    = pi.GetPluginConfig() as Configuration ?? new Configuration();
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
        _exportServer = new ExportServer(_config, _penumbra, _contexts, log);
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
            textureProvider);
        _exportServer.ImportFailureReceived += _window.ReportImportFailure;
        _settingsWindow = new SettingsWindow(
            _config,
            SaveConfiguration,
            () => _exportServer.Restart(),
            _log);

        _windowSystem = new WindowSystem();
        _windowSystem.AddWindow(_window);

        _pi.UiBuilder.Draw += _windowSystem.Draw;
        _pi.UiBuilder.Draw += _settingsWindow.Draw;
        _pi.UiBuilder.OpenMainUi += _window.Open;
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

        _log.Information("XIV Instant Edit loaded.");
    }

    private void OnCommand(string command, string args)
    {
        if (args.Trim().Equals("refresh", StringComparison.OrdinalIgnoreCase))
        {
            _onScreen.RequestRefresh();
            return;
        }

        _window.Toggle();
    }

    private void SaveConfiguration()
    {
        lock (_configLock)
            _pi.SavePluginConfig(_config);
    }

    public void Dispose()
    {
        _commands.RemoveHandler("/ie");
        _pi.UiBuilder.Draw -= _windowSystem.Draw;
        _pi.UiBuilder.Draw -= _settingsWindow.Draw;
        _pi.UiBuilder.OpenMainUi -= _window.Open;
        _pi.UiBuilder.OpenConfigUi -= _settingsWindow.Open;
        _windowSystem.RemoveWindow(_window);
        _window.Dispose();
        _exportServer.ImportFailureReceived -= _window.ReportImportFailure;
        _exportServer.Dispose();
        _contexts.Dispose();
        _blender.Dispose();
        SaveConfiguration();
    }
}
