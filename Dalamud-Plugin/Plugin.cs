using Dalamud.Game.Command;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.DragDrop;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using InstantEdit.Services;
using InstantEdit.Services.Animations;
using InstantEdit.Services.GameFiles;
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
    private readonly Services.Animations.Timeline.TimelineCatalog _timelineCatalog;
    private readonly Services.Animations.Preview.AnimationPreviewService _animationPreview;
    private readonly Services.Audio.GameSoundPlayer _soundPlayer;
    private readonly Services.Animations.Preview.AnimationPlayback _animationPlayback;
    private readonly Services.Animations.Preview.MotionLiveDisplay? _motionDisplay;
    private readonly TimelineWindow          _timelineWindow;
    private readonly ExportServer            _exportServer;
    private readonly Services.Painter.PainterJobService _painterJobs;
    private readonly GameFileBrowserService  _gameFiles;
    private readonly GameFileExportService   _gameExport;
    private readonly Services.Heels.HeelsOffsetService _heels;
    private readonly Services.Physics.PhysicsBuilderService _physics;
    private readonly PhysicsWindow           _physicsWindow;
    private readonly Services.Testing.BridgeTools? _bridgeTools;
    private readonly Services.TextureCompression.TextureCompressionService _compression;
    private readonly Studio.StudioHost       _studio;
    private readonly WindowSystem            _windowSystem;
    private readonly MainWindow              _window;
    private readonly ChangelogWindow         _changelogWindow;
    private readonly FirstTimeSetupWindow   _setupWindow;
    private readonly ToolSetupViews         _toolSetup;
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
        ITargetManager targets,
        ICondition condition,
        IDragDropManager dragDrop)
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
        // Dalamud hides plugin windows in GPose by default. The animation tools work there (the
        // timeline preview, live bone layers, recording the posed character), so the windows must
        // open and stay open in GPose; a window that isn't open shows nothing either way.
        pi.UiBuilder.DisableGposeUiHide = true;
        _backups   = new ModelBackupStore(storageRoot);
        var estEntries = new EstEntryStore(configDirectory);
        _penumbra  = new PenumbraService(pi, framework, log, objects, data, _backups, estEntries);
        // One attributor serves the On Screen snapshot and the Edit/material-preview flows,
        // so both share its mod index and cached stable identifiers.
        var resourceSources = new ResourceSourceAttributor(_penumbra.Ipc, log);
        // A new, deleted or renamed mod must show up in the next attribution, not up to 5 s later.
        _penumbra.Ipc.ModsChanged += resourceSources.Invalidate;
        _onScreen  = new OnScreenService(objects, clientState, framework, _penumbra.Ipc, resourceSources, log);
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
        _textures = new TextureEditService(_penumbra.Textures, _config, storageRoot, _backups,
            (error, message) => log.Warning(error, message));
        string? animationError = null;
        try { _animations = new AnimationEditService(pi, framework, objects, clientState, targets, data, sigScanner, _penumbra.Ipc, _penumbra.Animations, _backups, _config, log); }
        catch (Exception error)
        {
            animationError = "Animation integration is unavailable: " + error.Message;
            log.Warning(error, "Could not initialize animation editing; other features remain available.");
        }
        _recorder = new AnimationRecorder(framework, objects, clientState, targets, new CustomizePlusPause(pi, framework, log));
        // The timeline editor: what can be placed, the GPose preview through a temporary mod, and playback on the GPose actor.
        // Lip shape lengths come from the game's Havok, read on the framework thread like the observer's clip lengths.
        _timelineCatalog = new Services.Animations.Timeline.TimelineCatalog(data, log, bytes => Services.Audio.ScdFile.TrySeconds(bytes),
            _animations == null ? null : (bytes, token) => framework.RunOnTick(
                () => Services.Animations.AnimationRuntime.InspectPapDurations(bytes), cancellationToken: token).GetAwaiter().GetResult());
        _animationPreview = new Services.Animations.Preview.AnimationPreviewService(_penumbra.Ipc, _penumbra.Animations, _penumbra.Previews, framework, log, configDirectory,
            () => TextureFiles.EnsureCacheRoot(_config.TextureCacheDirectory), clientState);
        _animationPlayback =new Services.Animations.Preview.AnimationPlayback(framework, objects, clientState, targets, log);
        // The Timeline's sound inspector plays sounds through the game, served by a temporary mod.
        _soundPlayer = new Services.Audio.GameSoundPlayer(sigScanner, framework, _penumbra.Ipc, log,
            () => TextureFiles.EnsureCacheRoot(_config.TextureCacheDirectory));
        // Bone layers shown live on the GPose actor: an edited copy of the clip swapped into its controls.
        _motionDisplay = _animations == null ? null
            : new Services.Animations.Preview.MotionLiveDisplay(framework, objects, clientState, _animations.Native, log);
        _timelineWindow = new TimelineWindow(_animations, animationError, _timelineCatalog, _animationPreview, _animationPlayback,
            _motionDisplay, _recorder, data, framework, textureProvider, log, _soundPlayer);
        _exportServer = new ExportServer(_config, _penumbra.ModelExports, _penumbra.RaceExports, _penumbra.SizesExports,
            _penumbra.ModelBackups, _penumbra.Mashups, _penumbra.Scanner, _contexts, log);
        var painterStore = new Services.Painter.PainterJobStore(configDirectory);
        painterStore.Load();
        if (painterStore.LoadError.Length > 0)
            log.Warning(painterStore.LoadError);
        Func<string, CancellationToken, Task<byte[]?>> readGameFile =
            async (gamePath, token) => (await data.GetFileAsync<Lumina.Data.FileResource>(gamePath, token).ConfigureAwait(false))?.Data;
        Action<Exception, string> logPainter = (error, message) => log.Warning(error, message);
        _painterJobs = new Services.Painter.PainterJobService(_config, _textures,
            new Services.Painter.PainterProjectBuilder(new MaterialPreviewBundleBuilder(data, log, resourceSources), readGameFile, logPainter),
            new Services.Painter.PainterClient(), painterStore, readGameFile, logPainter,
            new Services.Painter.PainterLiveReader(framework, objects),
            new Services.Painter.PainterSkinResolver(new Services.Painter.PainterSkinPenumbra(
                async index => (await _penumbra.Ipc.GetCollectionTargetAsync(index).ConfigureAwait(false))?.Id,
                _penumbra.Ipc.MetaManipulationsAsync, _penumbra.Ipc.ResolveCollectionPathsAsync), readGameFile, logPainter));
        _exportServer.AttachPainter(_painterJobs);
        _previews = new PreviewService(textureProvider, data, log, () => _config.RenderModelThumbnails);
        _textures.FileChanged += _previews.Invalidate;
        _changelogWindow = new ChangelogWindow(_config, BlenderClient.CurrentPluginVersion, SaveConfiguration);
        _window    = new MainWindow(
            _config,
            _penumbra.Ipc,
            _penumbra.Sources,
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
        _window.AttachTimeline(_timelineWindow.Open, _timelineWindow.Show, () => _timelineWindow.Listens);
        _timelineWindow.AttachListening(() => _window.ListensForAnimations);
        _timelineWindow.AttachTools(() => _settingsWindow!.Open(), _window.DrawRecorderPanel);
        _timelineWindow.AttachBlender(_window.AnimationDelivery);
        _window.AttachPainter(_painterJobs);
        // Game skeletons for Blender's armatures: sent with imports, and asked for by the add-on.
        var skeletons = new ModelSkeletonResolver(_penumbra.Ipc, _penumbra.Animations, data, framework, objects, log);
        _window.AttachSkeletons(skeletons);
        _exportServer.Skeletons = skeletons;
        // What your character draws right now, for Send my character.
        _window.AttachCharacterDrawState(new Services.Painter.PainterLiveReader(framework, objects));
        // Vanilla models for the Game Files tab, and exports of their files to a folder.
        _gameFiles = new GameFileBrowserService(data, log);
        _gameExport = new GameFileExportService(_gameFiles, skeletons, log, BlenderClient.CurrentPluginVersion);
        _window.AttachGameFiles(_gameFiles, _gameExport);
        // Skin seam checks and preview mods; open previews are remembered in the config folder.
        var neckSeam = new Services.NeckSeam.NeckSeamService(_penumbra.Previews, _penumbra.Textures, data, log, configDirectory, () => _config.RecompressTextures,
            new Services.Painter.PainterLiveReader(framework, objects));
        _window.AttachNeckSeam(neckSeam);
        // Automatic texture compression: the character's uncompressed mod textures, checked and backed up in the cache folder.
        _compression = new Services.TextureCompression.TextureCompressionService(_config, SaveConfiguration, _penumbra.Ipc, _penumbra.ModFiles, _penumbra.Textures,
            _onScreen, _textures,
            framework, objects, clientState, condition, data, log, pi, configDirectory, [neckSeam.Store]);
        _window.AttachTextureCompression(_compression);
        // Simple Heels offsets, measured on the model your character's feet are in and written into it; preview mods' files are left alone.
        _heels = new Services.Heels.HeelsOffsetService(pi, framework, objects, clientState, condition, data, skeletons, _penumbra.Ipc, _penumbra.Previews, resourceSources,
            [neckSeam.Store], () => _config.AutoFixHeels, log);
        _window.AttachHeelsOffset(_heels);
        // The physics builder: body physics for your skeleton, previewed through a temporary mod and saved into a mod of its own.
        _physics = new Services.Physics.PhysicsBuilderService(pi, _penumbra.Ipc, _penumbra.Physics, _penumbra.Previews, data, framework, objects, clientState, skeletons, log, _config, SaveConfiguration,
            configDirectory, () => TextureFiles.EnsureCacheRoot(_config.TextureCacheDirectory));
        // In GPose your posed character walks or runs for the builder through the Timeline preview's playback.
        _physicsWindow = new PhysicsWindow(_physics, _animationPlayback, _config, SaveConfiguration, log);
        _window.AttachPhysics(_physicsWindow.Open);
        // Test tools for XIV Bridge (Tests/*.md checks), only when running as a dev plugin.
        if (pi.IsDev)
            _bridgeTools = new Services.Testing.BridgeTools(pi, framework, log, _config, _contexts, _backups, estEntries, _physicsWindow);
        // Automatic cache cleanup also removes old exports from the cache's export folder, and the
        // originals of textures optimized more than a week ago.
        _textures.AdditionalCacheCleanup = () =>
        {
            _compression.ExpireBackups();
            _studio?.Backups.Expire(DateTime.UtcNow, TextureFiles.CacheRootFor(_config.TextureCacheDirectory));
            var (files, bytes) = _gameExport.CleanCache(TextureFiles.CacheRootFor(_config.TextureCacheDirectory));
            if (files > 0)
                _log.Information($"Cache cleanup removed {files:N0} old Game Files export files ({bytes / (double)(1L << 20):0.0} MB).");
        };
        if (_config.AutomaticCacheCleanup)
            _textures.RequestCacheCleanup();
        _exportServer.ImportFailureReceived += _window.ReportImportFailure;
        _glamourer.AppearanceChanged += _window.OnGlamourerAppearanceChanged;
        // Blender, texture editor and Painter setup, shared by first-time setup and Settings.
        _toolSetup = new ToolSetupViews(_config, SaveConfiguration, _blender, _log);
        _setupWindow = new FirstTimeSetupWindow(
            _config,
            SaveConfiguration,
            _window.RequestCacheSynchronization,
            _window.Open,
            _pi,
            _toolSetup,
            _log);
        _settingsWindow = new SettingsWindow(
            _config,
            SaveConfiguration,
            () => _exportServer.Restart(),
            _log,
            _window.RequestCacheSynchronization,
            OpenSetupFromSettings,
            _toolSetup,
            cacheStartupError);
        _settingsWindow.AttachAnimations(_animations, animationError);
        // The Mod Browser: Penumbra's mods as a library, a window per open mod, and the modpack builder.
        Studio.StudioServices.Initialize(pi, log, data, textureProvider, dragDrop, framework);
        _config.Studio.AttachSave(SaveConfiguration);
        _studio = new Studio.StudioHost(_config.Studio, configDirectory, _settingsWindow.Open,
            () => TextureFiles.EnsureCacheRoot(_config.TextureCacheDirectory),
            () => objects.LocalPlayer is { } player && player.Customize.Length > 4
                ? Studio.Models.RaceInfo.FromCustomize(player.Customize[0], player.Customize[1], player.Customize[4])
                : null);
        _window.AttachStudio(_studio);

        _windowSystem = new WindowSystem();
        _windowSystem.AddWindow(_window);
        _windowSystem.AddWindow(_changelogWindow);
        _windowSystem.AddWindow(_setupWindow);
        _windowSystem.AddWindow(_settingsWindow);
        _windowSystem.AddWindow(_timelineWindow);
        _windowSystem.AddWindow(_timelineWindow.Bones);
        _windowSystem.AddWindow(_timelineWindow.Overlay);
        _windowSystem.AddWindow(_physicsWindow.Overlay);
        _windowSystem.AddWindow(_physicsWindow);

        _pi.UiBuilder.Draw += _windowSystem.Draw;
        _pi.UiBuilder.Draw += _studio.Draw;
        _pi.UiBuilder.Draw += _setupWindow.DrawFileDialog;
        _pi.UiBuilder.Draw += _toolSetup.DrawFileDialog;
        _pi.UiBuilder.Draw += _window.DrawFileDialog;
        _pi.UiBuilder.Draw += _timelineWindow.DrawFileDialog;
        _pi.UiBuilder.Draw += _physicsWindow.DrawFileDialog;
        _pi.UiBuilder.OpenMainUi += OpenMainUi;
        _pi.UiBuilder.OpenConfigUi += _settingsWindow.Open;

        _commands.AddHandler("/ie", new CommandInfo(OnCommand)
        {
            HelpMessage = "Opens the XIV Instant Edit window. Use /ie refresh to refresh the on-screen list, /ie physics for the physics builder.",
        });
        _commands.AddHandler("/iesettings", new CommandInfo((_, _) => _settingsWindow.Open())
        {
            HelpMessage = "Opens the XIV Instant Edit settings.",
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
        if (args.Trim().Equals("physics", StringComparison.OrdinalIgnoreCase))
        {
            _physicsWindow.Open();
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
        _bridgeTools?.Dispose();
        _commands.RemoveHandler("/ie");
        _commands.RemoveHandler("/iesettings");
        _pi.UiBuilder.Draw -= _windowSystem.Draw;
        _pi.UiBuilder.Draw -= _studio.Draw;
        _pi.UiBuilder.Draw -= _setupWindow.DrawFileDialog;
        _pi.UiBuilder.Draw -= _toolSetup.DrawFileDialog;
        _pi.UiBuilder.Draw -= _window.DrawFileDialog;
        _pi.UiBuilder.Draw -= _timelineWindow.DrawFileDialog;
        _pi.UiBuilder.Draw -= _physicsWindow.DrawFileDialog;
        _pi.UiBuilder.OpenMainUi -= OpenMainUi;
        _pi.UiBuilder.OpenConfigUi -= _settingsWindow.Open;
        _windowSystem.RemoveWindow(_window);
        _windowSystem.RemoveWindow(_changelogWindow);
        _windowSystem.RemoveWindow(_setupWindow);
        _windowSystem.RemoveWindow(_settingsWindow);
        _windowSystem.RemoveWindow(_timelineWindow);
        _windowSystem.RemoveWindow(_timelineWindow.Bones);
        _windowSystem.RemoveWindow(_timelineWindow.Overlay);
        _windowSystem.RemoveWindow(_physicsWindow.Overlay);
        _windowSystem.RemoveWindow(_physicsWindow);
        _glamourer.AppearanceChanged -= _window.OnGlamourerAppearanceChanged;
        _glamourer.Dispose();
        _recorder.Dispose();
        // The window first, then the live display (puts the clip's own motion back), playback (puts the
        // GPose actor back), the preview (removes its temporary mod) and the catalog; all of them before
        // Penumbra goes away.
        _timelineWindow.Dispose();
        _motionDisplay?.Dispose();
        _animationPlayback.Dispose();
        _soundPlayer.Dispose();
        _animationPreview.Dispose();
        _timelineCatalog.Dispose();
        _textures.AdditionalCacheCleanup = null;
        _gameExport.Dispose();
        _window.Dispose();
        // Writes what the open mods and the modpack still have pending.
        _studio.Dispose();
        _compression.Dispose();
        _heels.Dispose();
        // Removes the physics preview's temporary mod, so it goes before Penumbra.
        _physicsWindow.Dispose();
        _physics.Dispose();
        _gameFiles.Dispose();
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
