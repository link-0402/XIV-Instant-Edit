using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using InstantEdit.Services;

namespace InstantEdit.Ui;

/// <summary>Guided setup for the shared cache and the external tools used by XIV Instant Edit.</summary>
public sealed class FirstTimeSetupWindow : Window
{
    private const int WelcomeStep = 0;
    private const int CacheStep = 1;
    private const int BlenderStep = 2;
    private const int TextureStep = 3;
    private const int PainterStep = 4;
    private static readonly string[] StepNames = ["Welcome", "Cache", "Blender", "Textures", "Painter"];

    private sealed record PluginCheck(string Name, bool Required, bool Ready, string State, string Purpose);

    private readonly Configuration _config;
    private readonly Action _saveConfig;
    private readonly Action _requestCacheSynchronization;
    private readonly Action _openMainWindow;
    private readonly IDalamudPluginInterface _pi;
    private readonly IPluginLog _log;
    private readonly ToolSetupViews _tools;
    private readonly FileDialogManager _fileDialog = new();
    private int _step;
    private string _cacheDirectory = string.Empty;
    private string _textureEditorPath = string.Empty;
    private string _error = string.Empty;
    private IReadOnlyList<PluginCheck> _plugins = [];
    private DateTime _pluginsChecked = DateTime.MinValue;

    internal FirstTimeSetupWindow(
        Configuration config,
        Action saveConfig,
        Action requestCacheSynchronization,
        Action openMainWindow,
        IDalamudPluginInterface pi,
        ToolSetupViews tools,
        IPluginLog log)
        : base("XIV Instant Edit Setup##FirstTimeSetup")
    {
        _config = config;
        _saveConfig = saveConfig;
        _requestCacheSynchronization = requestCacheSynchronization;
        _openMainWindow = openMainWindow;
        _pi = pi;
        _tools = tools;
        _log = log;

        Flags = ImGuiWindowFlags.NoCollapse;
        Size = new Vector2(640, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(560, 420),
            MaximumSize = new Vector2(1000, 900),
        };
    }

    public void Open()
    {
        _cacheDirectory = _config.TextureCacheDirectory;
        _textureEditorPath = _config.TextureEditorPath;
        _step = WelcomeStep;
        _error = string.Empty;
        _pluginsChecked = DateTime.MinValue;
        _tools.Refresh();
        IsOpen = true;
    }

    public override void Draw()
    {
        ImGui.TextColored(Theme.Accent, "XIV INSTANT EDIT");
        ImGui.TextColored(Theme.Muted, "First-time setup");
        ImGui.Spacing();
        DrawProgress();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // The footer stays put; long steps scroll above it.
        var footer = ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y * 3 +
                     (_error.Length > 0 ? Widgets.BannerHeight(_error) + ImGui.GetStyle().ItemSpacing.Y : 0);
        using (var body = ImRaii.Child("##setup-step", new Vector2(0, -footer)))
        {
            if (body.Success)
            {
                switch (_step)
                {
                    case WelcomeStep: DrawWelcomeStep(); break;
                    case CacheStep: DrawCacheStep(); break;
                    case BlenderStep: DrawBlenderStep(); break;
                    case TextureStep: DrawTextureStep(); break;
                    default: DrawPainterStep(); break;
                }
            }
        }

        if (_error.Length > 0)
            Widgets.Banner("##setup-error", FeedbackSeverity.Error, _error);
        ImGui.Separator();
        ImGui.Spacing();
        DrawFooter();
    }

    public void DrawFileDialog() => _fileDialog.Draw();

    private void DrawProgress()
    {
        for (var i = 0; i < StepNames.Length; i++)
        {
            if (i > 0)
                ImGui.SameLine(0, Theme.Scaled(14));
            var colour = i == _step ? Theme.Accent : i < _step ? Theme.Label : Theme.Inactive;
            ImGui.TextColored(colour, $"{(i <= _step ? "●" : "○")} {StepNames[i]}");
        }
    }

    private static void DrawHeading(string heading, string description)
    {
        ImGui.TextColored(Theme.Accent, heading);
        ImGui.TextWrapped(description);
        ImGui.Spacing();
    }

    private void DrawWelcomeStep()
    {
        DrawHeading(
            "Welcome to XIV Instant Edit",
            "Instant Edit sends the models, textures and animations you see in game to your editing software, and puts your " +
            "changes straight back into Penumbra mods. No manual importing, exporting or file juggling.");

        ImGui.BulletText("Models: edit what your character wears, or any game model, in Blender and export it back in one click.");
        ImGui.BulletText("Textures: edit them in any image editor that saves TGA files, or paint them on the model in Substance Painter.");
        ImGui.BulletText("Animations: bake LivePose adjustments into them, repair their skeletons, or send them to Blender.");
        ImGui.BulletText("Game Files: browse the game's own models and export their files.");
        ImGui.Spacing();

        Widgets.SectionHeader("Plugins");
        foreach (var plugin in PluginChecks())
        {
            var colour = plugin.Ready ? Theme.Online : plugin.Required ? Theme.Offline : Theme.Inactive;
            Widgets.StatusDot(plugin.Name, colour, plugin.State);
            ImGui.SameLine(0, Theme.Gap);
            Widgets.Hint((plugin.Required ? "Required. " : "Optional. ") + plugin.Purpose);
        }
        ImGui.Spacing();
        Widgets.HintWrapped("The next steps choose a cache folder, then find Blender, your image editor and Substance Painter on this PC " +
                            "and install Instant Edit's add-on, save scripts and plugin into them. Everything but the cache can be skipped " +
                            "and set up later in Settings.");
    }

    /// <summary> Penumbra, Glamourer and LivePose, looked up at most every two seconds. </summary>
    private IReadOnlyList<PluginCheck> PluginChecks()
    {
        if (DateTime.UtcNow - _pluginsChecked < TimeSpan.FromSeconds(2))
            return _plugins;
        _pluginsChecked = DateTime.UtcNow;
        var installed = new Dictionary<string, (bool Loaded, string Version)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var plugin in _pi.InstalledPlugins)
                installed[plugin.InternalName] = (plugin.IsLoaded, plugin.Version.ToString());
        }
        catch (Exception error)
        {
            _log.Debug($"Could not list installed plugins: {error.Message}");
        }
        bool livePose;
        try { livePose = _pi.GetIpcSubscriber<(int, int)>("LivePose.ApiVersion").HasFunction; }
        catch (Exception error) { _log.Debug(error.Message); livePose = false; }

        _plugins =
        [
            Check("Penumbra", true, "Reads modded files and writes your edits back into mods."),
            Check("Glamourer", false, "Refreshes On Screen when your appearance changes."),
            new PluginCheck("LivePose", false, livePose, livePose ? "available" : "not found", "Required to bake pose offsets into animations."),
        ];
        return _plugins;

        PluginCheck Check(string name, bool required, string purpose)
            => installed.TryGetValue(name, out var found)
                ? new PluginCheck(name, required, found.Loaded, found.Loaded ? $"loaded ({found.Version})" : "installed but not enabled", purpose)
                : new PluginCheck(name, required, false, "not installed", purpose);
    }

    private void DrawCacheStep()
    {
        DrawHeading(
            "Choose a cache location",
            "The plugin and the Blender add-on exchange model files here, and texture edits keep their working files here. " +
            "A managed XIV Instant Edit folder is created inside the folder you choose.");

        ImGui.Text("Cache base directory");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##setup-cache-directory", "For example: D:\\XIV\\InstantEdit", ref _cacheDirectory, 4096))
            _cacheDirectory = _cacheDirectory.Trim().Trim('"');

        if (ImGui.Button("Browse..."))
        {
            _fileDialog.OpenFolderDialog(
                "Choose cache base directory",
                (success, path) =>
                {
                    if (success)
                        _cacheDirectory = path;
                },
                ExistingDirectoryOrCurrent(_cacheDirectory),
                true);
        }
        ImGui.SameLine();
        if (ImGui.Button("Use the default"))
            _cacheDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Your local app data folder, on the Windows drive.");
        ImGui.SameLine();
        Widgets.Hint("The base directory may already contain other files.");
        ImGui.Spacing();
        var managedCachePath = ManagedCachePathFor(_cacheDirectory);
        if (managedCachePath is not null)
        {
            ImGui.TextColored(Theme.Label, "Managed cache folder");
            ImGui.TextWrapped(managedCachePath);
            ImGui.Spacing();
        }
        Widgets.HintWrapped("Pick a drive with a few GB free: model exports, texture sessions and the skeleton library live here. " +
                            "Setup checks that the folder can be written to before it finishes.");
    }

    private void DrawBlenderStep()
    {
        DrawHeading(
            "Set up Blender for model editing",
            "Models are edited in Blender 4.5 or newer with the XIV Instant Edit add-on. Install it from GitHub below, so Blender " +
            "keeps it up to date. You can skip this if you only edit textures or animations.");
        _tools.DrawBlender();
    }

    private void DrawTextureStep()
    {
        DrawHeading(
            "Choose a texture editor (optional)",
            "Textures open as TGA files in the editor you choose here. Save them in place as a flattened 32-bit TGA, and the game " +
            "updates right away. Leave this empty if you only edit models.");
        _tools.DrawTextureEditors(_textureEditorPath, path => _textureEditorPath = path);
    }

    private void DrawPainterStep()
    {
        DrawHeading(
            "Paint in Substance Painter (optional)",
            "With Adobe Substance 3D Painter 10.0.1 or newer, On Screen models open in Painter with the current texture configuration " +
            "and Painter's Send to game button saves the painted textures back to the game.");
        _tools.DrawPainter(advanced: false);
    }

    private void DrawFooter()
    {
        if (ImGui.Button("Close"))
            IsOpen = false;

        if (_step > WelcomeStep)
        {
            ImGui.SameLine();
            if (ImGui.Button("Back"))
            {
                _step--;
                _error = string.Empty;
            }
        }

        ImGui.SameLine();
        if (_step == PainterStep)
        {
            if (ImGui.Button("Finish", new Vector2(Theme.Scaled(92), 0)))
                Finish();
        }
        else if (ImGui.Button(_step is BlenderStep or TextureStep ? "Next (or skip)" : "Next", new Vector2(Theme.Scaled(_step is BlenderStep or TextureStep ? 120 : 72), 0)))
        {
            _error = _step == CacheStep && _cacheDirectory.Trim().Trim('"').Length == 0
                ? "Choose a cache base directory before continuing."
                : string.Empty;
            if (_error.Length == 0)
                _step++;
        }
    }

    private void Finish()
    {
        _error = string.Empty;
        var cacheDirectory = _cacheDirectory.Trim().Trim('"');
        if (cacheDirectory.Length == 0)
        {
            _step = CacheStep;
            _error = "Choose a cache base directory before finishing setup.";
            return;
        }

        var editorPath = _textureEditorPath.Trim().Trim('"');
        if (editorPath.Length > 0 && !TextureEditService.TryValidateEditorPath(editorPath, out var editorError))
        {
            _step = TextureStep;
            _error = editorError;
            return;
        }

        string normalizedCacheDirectory;
        try
        {
            normalizedCacheDirectory = Path.GetFullPath(cacheDirectory);
            _ = TextureFiles.EnsureCacheRoot(normalizedCacheDirectory);
        }
        catch (Exception error)
        {
            _log.Warning(error, "First-time setup could not create the XIV Instant Edit cache.");
            _step = CacheStep;
            _error = $"The cache could not be created: {error.Message}";
            return;
        }

        _config.TextureCacheDirectory = normalizedCacheDirectory;
        _config.TextureEditorPath = editorPath;
        _config.FirstTimeSetupCompleted = true;
        _saveConfig();
        try { _requestCacheSynchronization(); }
        catch (Exception error) { _log.Debug(error.Message); }
        IsOpen = false;
        _openMainWindow();
    }

    private static string ExistingDirectoryOrCurrent(string path)
    {
        try
        {
            if (Directory.Exists(path))
                return path;

            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
                return parent;
        }
        catch (ArgumentException) { }
        catch (NotSupportedException) { }

        return Environment.CurrentDirectory;
    }

    private static string? ManagedCachePathFor(string path)
    {
        var trimmed = path.Trim().Trim('"');
        if (trimmed.Length == 0)
            return null;

        try
        {
            return Path.GetFullPath(Path.Combine(trimmed, TextureFiles.CacheFolder));
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
