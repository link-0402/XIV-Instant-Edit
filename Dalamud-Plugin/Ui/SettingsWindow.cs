using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using InstantEdit.Services;
using InstantEdit.Services.Animations;

namespace InstantEdit.Ui;

/// <summary>Dedicated Dalamud configuration surface; kept separate from model selection.</summary>
public sealed class SettingsWindow : Window
{
    private readonly Configuration _config;
    private readonly Action _saveConfig;
    private readonly Action _restartExportListener;
    private readonly IPluginLog _log;
    private readonly Action _requestCacheSynchronization;
    private readonly Action _openSetup;
    private readonly ToolSetupViews _tools;
    private readonly string _cacheStartupError;
    private AnimationEditService? _animations;
    private string? _animationError;
    private DateTime? _savedSkeletonLibrary;
    private int? _pendingListenPort;

    internal SettingsWindow(Configuration config, Action saveConfig, Action restartExportListener, IPluginLog log,
        Action requestCacheSynchronization, Action openSetup, ToolSetupViews tools, string cacheStartupError = "")
        : base("XIV Instant Edit Settings##Settings")
    {
        _config = config;
        _saveConfig = saveConfig;
        _restartExportListener = restartExportListener;
        _log = log;
        _requestCacheSynchronization = requestCacheSynchronization;
        _openSetup = openSetup;
        _tools = tools;
        _cacheStartupError = cacheStartupError;

        Size = new Vector2(600, 480);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(520, 360),
            MaximumSize = new Vector2(1000, 900),
        };
        AllowPinning = true;
        AllowBackgroundBlur = true;
        RespectCloseHotkey = true;
    }

    public void Open() => IsOpen = true;
    public void Close() => IsOpen = false;

    internal void AttachAnimations(AnimationEditService? service, string? error)
    {
        _animations = service;
        _animationError = error;
    }

    // Before the library is loaded this session, Settings shows when the saved one was built.
    public override void OnOpen()
    {
        _savedSkeletonLibrary = _animations?.SavedSkeletonLibraryUtc();
        _tools.Refresh();
    }

    public override void Draw()
    {
        ImGui.TextColored(Theme.Accent, "XIV INSTANT EDIT SETTINGS");
        ImGui.TextColored(Theme.Muted, "Connection and export preferences");
        ImGui.Spacing();
        if (ImGui.Button("Run first-time setup again"))
        {
            IsOpen = false;
            _openSetup();
            return;
        }
        ImGui.SameLine();
        Widgets.Hint("Review the cache, Blender, texture editor and Substance Painter setup.");
        ImGui.Spacing();
        ImGui.Separator(); ImGui.Text("Connections");
        var blenderPort = _config.BlenderPort; if (ImGui.InputInt("Blender port", ref blenderPort)) { _config.BlenderPort = blenderPort; Save(); }
        ImGui.TextWrapped("Model editing requires Blender. Texture editing works after its cache has synchronized once.");
        var listenPort = _pendingListenPort ?? _config.ListenPort;
        if (ImGui.InputInt("Listener port", ref listenPort, 0, 0)) _pendingListenPort = listenPort;
        // Restart once the field is left, not for every digit typed on the way to the new port.
        if (ImGui.IsItemDeactivatedAfterEdit() && _pendingListenPort is { } port)
        {
            _pendingListenPort = null;
            if (port != _config.ListenPort)
            {
                _config.ListenPort = port;
                Save();
                RestartListener();
                try { Services.Painter.PainterInstallation.UpdatePorts(_config.PainterPort, _config.ListenPort); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _log.Debug(e.Message); }
            }
        }
        Widgets.Hint("Quick Export writes back to the model's original Penumbra mod.");
        ImGui.Spacing(); ImGui.Separator(); ImGui.Text("Blender add-on");
        _tools.DrawBlender();
        ImGui.Spacing(); ImGui.Separator(); ImGui.Text("Texture editing");
        _tools.DrawTextureEditors(_config.TextureEditorPath, path => { _config.TextureEditorPath = path; Save(); });
        ImGui.TextWrapped("Open a texture, edit it, then save your changes to the same file (in-place as a 32-bit TGA with alpha).");
        ImGui.Spacing(); ImGui.Separator(); ImGui.Text("Substance Painter");
        _tools.DrawPainter(advanced: true);
        ImGui.Spacing(); ImGui.Separator(); ImGui.Text("On Screen");
        var autoRefresh = _config.AutoRefreshOnScreen;
        if (ImGui.Checkbox("Refresh automatically when Penumbra or Glamourer changes", ref autoRefresh)) { _config.AutoRefreshOnScreen = autoRefresh; Save(); }
        Widgets.Hint("Reloads the on-screen list a second after a mod setting changes or a character is redrawn.");
        ImGui.Spacing(); ImGui.Separator(); ImGui.Text("Previews");
        var thumbnails = _config.RenderModelThumbnails;
        if (ImGui.Checkbox("Render model thumbnails on hover", ref thumbnails)) { _config.RenderModelThumbnails = thumbnails; Save(); }
        Widgets.Hint("Draws a small shaded view of Dawntrail (V6) models in the hover card. Textures and materials always preview.");
        ImGui.Spacing(); ImGui.Separator(); ImGui.Text("Notifications");
        var showNotifications = _config.ShowNotifications;
        if (ImGui.Checkbox("Show notifications", ref showNotifications)) { _config.ShowNotifications = showNotifications; Save(); }
        Widgets.Hint("Warnings, errors and model handoff results also appear as Dalamud notifications, even while the window is closed.");
        ImGui.Spacing(); ImGui.Separator(); ImGui.Text("Cache");
        var cacheDirectory = _config.TextureCacheDirectory;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("Cache directory", "Base folder shared by the in-game plugin and Blender add-on", ref cacheDirectory, 4096))
        {
            _config.TextureCacheDirectory = cacheDirectory.Trim().Trim('"');
            Save();
            try { _requestCacheSynchronization(); }
            catch (Exception e) { _log.Debug(e.Message); }
        }
        var automaticCleanup = _config.AutomaticCacheCleanup;
        if (ImGui.Checkbox("Automatic cache cleanup", ref automaticCleanup))
        {
            _config.AutomaticCacheCleanup = automaticCleanup;
            Save();
            try { _requestCacheSynchronization(); }
            catch (Exception e) { _log.Debug(e.Message); }
        }
        ImGui.TextWrapped("When enabled, completed model cache jobs and inactive texture-edit sessions older than 24 hours are removed, " +
                          "and Game Files exports in the cache folder after 7 days. Active sessions and unsaved texture edits are kept.");
        try { ImGui.TextWrapped($"Managed cache: {TextureFiles.CacheRootFor(_config.TextureCacheDirectory)}"); }
        catch (Exception e) { ImGui.TextWrapped($"Cache directory is invalid: {e.Message}"); }
        if (_cacheStartupError.Length > 0)
        {
            ImGui.Spacing();
            Widgets.Banner("##settings-cache-error", FeedbackSeverity.Error,
                $"The cache could not be opened when the plugin loaded: {_cacheStartupError} " +
                "Choose another cache directory (or empty this one), then reload the plugin. " +
                "Model backups are kept in the plugin's config folder until then.");
        }
        ImGui.Spacing(); ImGui.Separator(); ImGui.Text("Skeleton library");
        DrawSkeletonLibrary();
    }

    /// <summary>The library of every skeleton in the installed mods that animations are matched against.</summary>
    private void DrawSkeletonLibrary()
    {
        ImGui.TextWrapped("Animations are matched to the skeleton they were made for from a library of every skeleton in your " +
                          "Penumbra mods. It is built once, saved in the cache folder, and only built again with this button.");
        var service = _animations;
        if (service is null)
        {
            Widgets.Hint(_animationError ?? "Animation integration is unavailable.");
            return;
        }
        var library = service.SkeletonLibrary;
        var working = library.State is SkeletonLibraryState.Loading or SkeletonLibraryState.Building;
        using (ImRaii.Disabled(working))
        {
            if (ImGui.Button("Rebuild skeleton library"))
                service.RebuildSkeletonLibrary();
        }
        ImGui.SameLine();
        Widgets.Hint("After installing, updating or removing skeleton mods.");
        switch (library.State)
        {
            case SkeletonLibraryState.NotLoaded:
                Widgets.HintWrapped(_savedSkeletonLibrary is { } saved
                    ? $"Saved {saved.ToLocalTime():g}. It loads the first time an animation is matched."
                    : "Not built yet. It is built the first time an animation is matched, or now with the button.");
                break;
            case SkeletonLibraryState.Loading:
                Widgets.HintWrapped("Loading the saved library…");
                break;
            case SkeletonLibraryState.Building:
                Widgets.HintWrapped(library.Total == 0
                    ? "Building: looking for skeletons in your mods…"
                    : $"Building: {library.Progress} of {library.Total} skeleton files read. Animations are matched once it is done.");
                break;
            case SkeletonLibraryState.Ready:
                Widgets.HintWrapped($"{library.Skeletons} skeletons from {library.Files} files, built {library.BuiltUtc?.ToLocalTime():g}.");
                if (library.Missing > 0 || library.Changed > 0)
                    Widgets.Banner("##skeleton-library-stale", FeedbackSeverity.Warning,
                        $"Since then, {Files(library.Missing)} of it were removed and {Files(library.Changed)} changed. Rebuild to pick those changes up.");
                if (library.Error is { } saveError)
                    Widgets.Banner("##skeleton-library-save", FeedbackSeverity.Warning, saveError);
                break;
            case SkeletonLibraryState.Failed:
                Widgets.Banner("##skeleton-library-failed", FeedbackSeverity.Error,
                    $"The last build failed: {library.Error}" +
                    (library.Skeletons > 0 ? " The previous library stays in use." : ""));
                break;
        }

        static string Files(int count) => count == 1 ? "1 file" : $"{count} files";
    }

    private void Save()
    {
        _config.BlenderPort = Math.Clamp(_config.BlenderPort, 1, 65535);
        _config.ListenPort = Math.Clamp(_config.ListenPort, 1, 65535);
        _saveConfig();
    }
    private void RestartListener() { try { _restartExportListener(); } catch (Exception e) { _log.Error(e, "Failed to restart the export listener."); } }
}
