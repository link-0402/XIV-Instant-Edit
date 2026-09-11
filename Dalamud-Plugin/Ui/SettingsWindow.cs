using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using InstantEdit.Services;

namespace InstantEdit.Ui;

/// <summary>Dedicated Dalamud configuration surface; kept separate from model selection.</summary>
public sealed class SettingsWindow
{
    private readonly Configuration _config;
    private readonly Action _saveConfig;
    private readonly Action _restartExportListener;
    private readonly IPluginLog _log;
    private readonly Action _requestCacheSynchronization;
    private readonly Action _openSetup;
    private bool _open;

    public SettingsWindow(Configuration config, Action saveConfig, Action restartExportListener, IPluginLog log,
        Action requestCacheSynchronization, Action openSetup)
    {
        _config = config;
        _saveConfig = saveConfig;
        _restartExportListener = restartExportListener;
        _log = log;
        _requestCacheSynchronization = requestCacheSynchronization;
        _openSetup = openSetup;
    }

    public bool IsOpen { get => _open; set => _open = value; }
    public void Open() => _open = true;
    public void Close() => _open = false;
    public void Toggle() => _open = !_open;

    public void Draw()
    {
        if (!_open) return;
        if (!ImGui.Begin("XIV Instant Edit Settings##Settings", ref _open)) { ImGui.End(); return; }
        ImGui.TextColored(new Vector4(.95f, .78f, .35f, 1), "XIV INSTANT EDIT SETTINGS");
        ImGui.TextColored(new Vector4(.58f, .6f, .67f, 1), "Connection and export preferences");
        ImGui.Spacing();
        if (ImGui.Button("Run first-time setup again"))
        {
            _open = false;
            _openSetup();
            ImGui.End();
            return;
        }
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(.55f, .57f, .64f, 1), "Review Penumbra, Blender, cache, and texture-editor setup.");
        ImGui.Spacing();
        ImGui.Separator(); ImGui.Text("Connections");
        var blenderPort = _config.BlenderPort; if (ImGui.InputInt("Blender port", ref blenderPort)) { _config.BlenderPort = blenderPort; Save(); }
        ImGui.TextWrapped("Model editing requires Blender. Texture editing works after its cache has synchronized once.");
        var listenPort = _config.ListenPort; if (ImGui.InputInt("Listener port", ref listenPort)) { var changed = listenPort != _config.ListenPort; _config.ListenPort = listenPort; Save(); if (changed) RestartListener(); }
        ImGui.TextColored(new Vector4(.55f, .57f, .64f, 1), "Quick Export writes back to the model's original Penumbra mod.");
        ImGui.Spacing(); ImGui.Separator(); ImGui.Text("Texture editing");
        var editor = _config.TextureEditorPath;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##texture-editor", "Full path to Photoshop.exe or another TGA editor", ref editor, 2048))
        { _config.TextureEditorPath = editor.Trim().Trim('"'); Save(); }
        ImGui.TextWrapped("Open a texture, edit it, then save your changes to the same file (in-place as a 32-bit TGA with alpha).");
        ImGui.Spacing(); ImGui.Separator(); ImGui.Text("Cache");
        var cacheDirectory = _config.TextureCacheDirectory;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("Cache directory", "Base folder shared by the plugin and Blender", ref cacheDirectory, 4096))
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
        ImGui.End();
    }

    private void Save()
    {
        _config.BlenderPort = Math.Clamp(_config.BlenderPort, 1, 65535);
        _config.ListenPort = Math.Clamp(_config.ListenPort, 1, 65535);
        _saveConfig();
    }
    private void RestartListener() { try { _restartExportListener(); } catch (Exception e) { _log.Error(e, "Failed to restart the export listener."); } }
}
