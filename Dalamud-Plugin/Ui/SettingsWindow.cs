using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using InstantEdit.Services;
using InstantEdit.Services.Animations;

namespace InstantEdit.Ui;

/// <summary>Dedicated Dalamud configuration surface; kept separate from model selection.</summary>
public sealed class SettingsWindow : Window
{
    private enum SettingsTab { Editors, Cache, Interface, Advanced }

    // Most important first: the editors files are handed to, where the files live, display toggles, then ports.
    private static readonly (SettingsTab Tab, FontAwesomeIcon Icon, string Label)[] Tabs =
    [
        (SettingsTab.Editors, FontAwesomeIcon.Tools, "Editors"),
        (SettingsTab.Cache, FontAwesomeIcon.Hdd, "Cache"),
        (SettingsTab.Interface, FontAwesomeIcon.Desktop, "Interface"),
        (SettingsTab.Advanced, FontAwesomeIcon.Plug, "Advanced"),
    ];

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
    private SettingsTab _tab;

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
        // A cache that failed to open blocks most features, so Settings opens where it is fixed.
        _tab = cacheStartupError.Length > 0 ? SettingsTab.Cache : SettingsTab.Editors;

        Size = new Vector2(640, 520);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(560, 380),
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
        ImGui.TextColored(Theme.Muted, "Changes are saved as you make them.");
        ImGui.Spacing();
        if (DrawTabs())
            return;
        ImGui.Separator();
        ImGui.Spacing();

        using var body = ImRaii.Child("##settings-tab", Vector2.Zero, false);
        if (!body.Success)
            return;
        switch (_tab)
        {
            case SettingsTab.Cache: DrawCacheTab(); break;
            case SettingsTab.Interface: DrawInterfaceTab(); break;
            case SettingsTab.Advanced: DrawAdvancedTab(); break;
            default: DrawEditorsTab(); break;
        }
    }

    /// <summary> The tab row, with the setup wizard's button at its right edge. Returns true when the wizard was opened instead. </summary>
    private bool DrawTabs()
    {
        for (var i = 0; i < Tabs.Length; i++)
        {
            if (i > 0)
                ImGui.SameLine(0, Theme.Gap);
            if (Widgets.TabButton(Tabs[i].Icon, Tabs[i].Label, _tab == Tabs[i].Tab))
                _tab = Tabs[i].Tab;
        }

        const string setup = "Run setup again";
        ImGui.SameLine();
        var start = ImGui.GetWindowContentRegionMax().X - ImGui.CalcTextSize(setup).X - ImGui.GetStyle().FramePadding.X * 2;
        if (start > ImGui.GetCursorPosX())
            ImGui.SetCursorPosX(start);
        var open = ImGui.Button(setup);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Review the cache, Blender, texture editor and Substance Painter setup.");
        if (!open)
            return false;
        IsOpen = false;
        _openSetup();
        return true;
    }

    // ---- Editors ---------------------------------------------------------------------------

    private void DrawEditorsTab()
    {
        Widgets.SectionHeader("Blender", "model editing");
        Widgets.MutedWrapped("Model editing requires Blender with the XIV Instant Edit add-on. Quick Export writes back to the model's original Penumbra mod.");
        ImGui.Spacing();
        _tools.DrawBlender();

        Divider();
        Widgets.SectionHeader("Texture editor", "texture editing");
        Widgets.MutedWrapped("Open a texture, edit it, then save your changes to the same file (in place, as a 32-bit TGA with alpha). " +
                             "Texture editing works after the cache has synchronized once.");
        ImGui.Spacing();
        _tools.DrawTextureEditors(_config.TextureEditorPath, path => { _config.TextureEditorPath = path; Save(); });

        Divider();
        Widgets.SectionHeader("Substance Painter", "optional");
        _tools.DrawPainter(advanced: true);
    }

    // ---- Cache -----------------------------------------------------------------------------

    private void DrawCacheTab()
    {
        if (_cacheStartupError.Length > 0)
        {
            Widgets.Banner("##settings-cache-error", FeedbackSeverity.Error,
                $"The cache could not be opened when the plugin loaded: {_cacheStartupError} " +
                "Choose another cache directory (or empty this one), then reload the plugin. " +
                "Model backups are kept in the plugin's config folder until then.");
            ImGui.Spacing();
        }

        Widgets.SectionHeader("Cache folder");
        Widgets.MutedWrapped("The plugin and the Blender add-on exchange model files here, and texture edits keep their working files here.");
        ImGui.Spacing();
        var cacheDirectory = _config.TextureCacheDirectory;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##cache-directory", "Base folder shared by the in-game plugin and Blender add-on", ref cacheDirectory, 4096))
        {
            _config.TextureCacheDirectory = cacheDirectory.Trim().Trim('"');
            Save();
            SynchronizeCache();
        }
        try { Widgets.HintWrapped($"Managed cache: {TextureFiles.CacheRootFor(_config.TextureCacheDirectory)}"); }
        catch (Exception e)
        {
            using var wrap = ImRaii.TextWrapPos(0f);
            ImGui.TextColored(Theme.Error, $"Cache directory is invalid: {e.Message}");
        }
        ImGui.Spacing();
        var automaticCleanup = _config.AutomaticCacheCleanup;
        if (Toggle("Automatic cache cleanup", ref automaticCleanup,
                "Completed model cache jobs and inactive texture-edit sessions older than 24 hours are removed, and Game Files " +
                "exports in the cache folder after 7 days. Active sessions and unsaved texture edits are kept."))
        {
            _config.AutomaticCacheCleanup = automaticCleanup;
            Save();
            SynchronizeCache();
        }

        Divider();
        Widgets.SectionHeader("Skeleton library", "animations");
        DrawSkeletonLibrary();
    }

    /// <summary>The library of the installed mods' full skeletons, each once, that animations are matched against.</summary>
    private void DrawSkeletonLibrary()
    {
        Widgets.MutedWrapped("Animations are matched to the skeleton they were made for from a library of the full skeletons in your " +
                             "Penumbra mods, each kept once. It is built once, saved in the cache folder, and only built again with this button.");
        ImGui.Spacing();
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
                Widgets.HintWrapped("Loading the saved library");
                break;
            case SkeletonLibraryState.Building:
                Widgets.HintWrapped(library.Total == 0
                    ? "Building: looking for skeletons in your mods"
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

    // ---- Interface -------------------------------------------------------------------------

    private void DrawInterfaceTab()
    {
        Widgets.SectionHeader("Lists");
        var autoRefresh = _config.AutoRefreshOnScreen;
        if (Toggle("Refresh On Screen when Penumbra or Glamourer changes", ref autoRefresh,
                "Reloads the on-screen list a second after a mod setting changes or a character is redrawn."))
        {
            _config.AutoRefreshOnScreen = autoRefresh;
            Save();
        }
        ImGui.Spacing();
        var thumbnails = _config.RenderModelThumbnails;
        if (Toggle("Render model thumbnails on hover", ref thumbnails,
                "Draws a small shaded view of Dawntrail (V6) models in the hover card. Textures and materials always preview."))
        {
            _config.RenderModelThumbnails = thumbnails;
            Save();
        }

        Divider();
        Widgets.SectionHeader("Notifications");
        var showNotifications = _config.ShowNotifications;
        if (Toggle("Show notifications", ref showNotifications,
                "Warnings, errors and model handoff results also appear as Dalamud notifications, even while the window is closed."))
        {
            _config.ShowNotifications = showNotifications;
            Save();
        }
    }

    // ---- Advanced --------------------------------------------------------------------------

    private void DrawAdvancedTab()
    {
        Widgets.SectionHeader("Ports");
        Widgets.MutedWrapped("The plugin, Blender and Substance Painter talk to each other over these local ports. " +
                             "Change one only when another program already uses it.");
        ImGui.Spacing();

        var portWidth = Theme.Scaled(110);
        var blenderPort = _config.BlenderPort;
        ImGui.SetNextItemWidth(portWidth);
        if (ImGui.InputInt("Blender port", ref blenderPort, 0, 0)) { _config.BlenderPort = blenderPort; Save(); }
        Widgets.Hint("Must match Blender Listen Port in the add-on's preferences.");
        ImGui.Spacing();

        var listenPort = _pendingListenPort ?? _config.ListenPort;
        ImGui.SetNextItemWidth(portWidth);
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
        Widgets.Hint("Blender and Substance Painter send their exports back to this port.");
        ImGui.Spacing();

        _tools.DrawPainterPort(portWidth);
        Widgets.Hint("The XIV Instant Edit plugin inside Substance Painter listens on this port.");
    }

    // ---- Shared ----------------------------------------------------------------------------

    /// <summary> A checkbox with its explanation wrapped under the label. Returns true when it was toggled. </summary>
    private static bool Toggle(string label, ref bool value, string hint)
    {
        var changed = ImGui.Checkbox(label, ref value);
        using (ImRaii.PushIndent(ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X, false))
            Widgets.HintWrapped(hint);
        return changed;
    }

    private static void Divider()
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
    }

    private void SynchronizeCache()
    {
        try { _requestCacheSynchronization(); }
        catch (Exception e) { _log.Debug(e.Message); }
    }

    private void Save()
    {
        _config.BlenderPort = Math.Clamp(_config.BlenderPort, 1, 65535);
        _config.ListenPort = Math.Clamp(_config.ListenPort, 1, 65535);
        _saveConfig();
    }
    private void RestartListener() { try { _restartExportListener(); } catch (Exception e) { _log.Error(e, "Failed to restart the export listener."); } }
}
