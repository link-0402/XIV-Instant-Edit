using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using InstantEdit.Services;

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
    private readonly string _cacheStartupError;

    public SettingsWindow(Configuration config, Action saveConfig, Action restartExportListener, IPluginLog log,
        Action requestCacheSynchronization, Action openSetup, string cacheStartupError = "")
        : base("XIV Instant Edit Settings##Settings")
    {
        _config = config;
        _saveConfig = saveConfig;
        _restartExportListener = restartExportListener;
        _log = log;
        _requestCacheSynchronization = requestCacheSynchronization;
        _openSetup = openSetup;
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
        Widgets.Hint("Review Penumbra, Blender, cache, and texture-editor setup.");
        ImGui.Spacing();
        ImGui.Separator(); ImGui.Text("Connections");
        var blenderPort = _config.BlenderPort; if (ImGui.InputInt("Blender port", ref blenderPort)) { _config.BlenderPort = blenderPort; Save(); }
        ImGui.TextWrapped("Model editing requires Blender. Texture editing works after its cache has synchronized once.");
        var listenPort = _config.ListenPort; if (ImGui.InputInt("Listener port", ref listenPort)) { var changed = listenPort != _config.ListenPort; _config.ListenPort = listenPort; Save(); if (changed) RestartListener(); }
        Widgets.Hint("Quick Export writes back to the model's original Penumbra mod.");
        ImGui.Spacing(); ImGui.Separator(); ImGui.Text("Texture editing");
        var editor = _config.TextureEditorPath;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##texture-editor", "Full path to Photoshop.exe or another TGA editor", ref editor, 2048))
        { _config.TextureEditorPath = editor.Trim().Trim('"'); Save(); }
        ImGui.TextWrapped("Open a texture, edit it, then save your changes to the same file (in-place as a 32-bit TGA with alpha).");
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
        ImGui.TextWrapped("When enabled, completed model cache jobs and inactive texture-edit sessions older than 24 hours are removed. Active sessions and unsaved texture edits are kept.");
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
    }

    private void Save()
    {
        _config.BlenderPort = Math.Clamp(_config.BlenderPort, 1, 65535);
        _config.ListenPort = Math.Clamp(_config.ListenPort, 1, 65535);
        _saveConfig();
    }
    private void RestartListener() { try { _restartExportListener(); } catch (Exception e) { _log.Error(e, "Failed to restart the export listener."); } }
}
