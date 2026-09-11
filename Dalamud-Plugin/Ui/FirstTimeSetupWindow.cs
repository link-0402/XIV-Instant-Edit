using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using InstantEdit.Services;

namespace InstantEdit.Ui;

/// <summary>Guided setup for the external tools and shared cache used by XIV Instant Edit.</summary>
public sealed class FirstTimeSetupWindow : Window
{
    private const int WelcomeStep = 0;
    private const int CacheStep = 1;
    private const int EditorStep = 2;
    private const int StepCount = 3;

    private readonly Configuration _config;
    private readonly Action _saveConfig;
    private readonly Action _requestCacheSynchronization;
    private readonly Action _openMainWindow;
    private readonly IPluginLog _log;
    private readonly FileDialogManager _fileDialog = new();
    private int _step;
    private string _cacheDirectory = string.Empty;
    private string _textureEditorPath = string.Empty;
    private string _error = string.Empty;

    public FirstTimeSetupWindow(
        Configuration config,
        Action saveConfig,
        Action requestCacheSynchronization,
        Action openMainWindow,
        IPluginLog log)
        : base("XIV Instant Edit Setup##FirstTimeSetup")
    {
        _config = config;
        _saveConfig = saveConfig;
        _requestCacheSynchronization = requestCacheSynchronization;
        _openMainWindow = openMainWindow;
        _log = log;

        Flags = ImGuiWindowFlags.NoCollapse;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(520, 360),
            MaximumSize = new Vector2(760, 720),
        };
    }

    public void Open()
    {
        _cacheDirectory = _config.TextureCacheDirectory;
        _textureEditorPath = _config.TextureEditorPath;
        _step = WelcomeStep;
        _error = string.Empty;
        IsOpen = true;
    }

    public override void Draw()
    {
        ImGui.TextColored(new Vector4(.95f, .78f, .35f, 1), "XIV INSTANT EDIT");
        ImGui.TextColored(new Vector4(.58f, .6f, .67f, 1), "First-time setup");
        ImGui.Spacing();
        DrawProgress();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        switch (_step)
        {
            case WelcomeStep:
                DrawWelcomeStep();
                break;
            case CacheStep:
                DrawCacheStep();
                break;
            default:
                DrawEditorStep();
                break;
        }

        if (_error.Length > 0)
        {
            ImGui.Spacing();
            ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(.24f, .055f, .055f, 1));
            ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(.9f, .3f, .3f, 1));
            ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 4);
            ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 1);
            if (ImGui.BeginChild("##setup-error", new Vector2(0, CalculateErrorHeight()), true))
                ImGui.TextWrapped(_error);
            ImGui.EndChild();
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor(2);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawFooter();
    }

    public void DrawFileDialog() => _fileDialog.Draw();

    private void DrawProgress()
    {
        ImGui.TextColored(new Vector4(.76f, .78f, .84f, 1), $"Step {_step + 1} of {StepCount}");
        ImGui.SameLine();
        for (var i = 0; i < StepCount; i++)
        {
            var color = i <= _step
                ? new Vector4(.95f, .78f, .35f, 1)
                : new Vector4(.35f, .37f, .43f, 1);
            ImGui.TextColored(color, i == _step ? "●" : "○");
            if (i < StepCount - 1)
                ImGui.SameLine(0, 4);
        }
    }

    private static void DrawHeading(string heading, string description)
    {
        ImGui.TextColored(new Vector4(.95f, .78f, .35f, 1), heading);
        ImGui.TextWrapped(description);
        ImGui.Spacing();
    }

    private void DrawWelcomeStep()
    {
        DrawHeading(
            "Welcome to XIV Instant Edit",
            "This plugin connects Penumbra, Blender, and the XIV Instant Edit Blender add-on so you can edit models in Blender and send them back to the game.");

        ImGui.BulletText("Penumbra is required to read modded resources and write exports back to mods.");
        ImGui.BulletText("Blender and the XIV Instant Edit add-on are required for model editing.");
        ImGui.BulletText("The next step creates a shared cache used by the plugin and the Blender add-on.");
        ImGui.BulletText("Texture editing is optional and can be enabled by choosing a TGA editor on the final step.");
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(.55f, .57f, .64f, 1), "You can revisit this setup wizard from XIV Instant Edit Settings at any time.");
    }

    private void DrawCacheStep()
    {
        DrawHeading(
            "Choose a shared cache location",
            "The plugin and Blender add-on use this location to exchange model files and store texture-editing work. The managed XIV Instant Edit folder is created automatically inside the location you choose.");

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
        ImGui.TextColored(new Vector4(.55f, .57f, .64f, 1), "The base directory may already contain other files.");
        ImGui.Spacing();
        var managedCachePath = ManagedCachePathFor(_cacheDirectory);
        if (managedCachePath is not null)
        {
            ImGui.TextColored(new Vector4(.76f, .78f, .84f, 1), "Managed cache folder");
            ImGui.TextWrapped(managedCachePath);
            ImGui.Spacing();
        }
        ImGui.TextColored(new Vector4(.55f, .57f, .64f, 1), "The cache is required. Setup will verify that the managed folder can be created before it finishes.");
    }

    private void DrawEditorStep()
    {
        DrawHeading(
            "Configure texture editing (optional)",
            "Choose the executable for Photoshop or another editor that can open and save 32-bit TGA files. Leave this blank if you only plan to edit models.");

        ImGui.Text("Texture editor executable");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##setup-texture-editor", "Full path to Photoshop.exe or another TGA editor", ref _textureEditorPath, 2048))
            _textureEditorPath = _textureEditorPath.Trim().Trim('"');
        ImGui.TextColored(new Vector4(.55f, .57f, .64f, 1), "You can configure or change this later in XIV Instant Edit Settings.");
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
        if (_step == EditorStep)
        {
            if (ImGui.Button("Finish", new Vector2(92, 0)))
                Finish();
        }
        else if (ImGui.Button("Next", new Vector2(72, 0)))
        {
            _error = string.Empty;
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
            _step = EditorStep;
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

    private float CalculateErrorHeight()
    {
        var width = Math.Max(1, ImGui.GetContentRegionAvail().X - ImGui.GetStyle().WindowPadding.X * 2);
        return Math.Max(ImGui.GetFrameHeightWithSpacing() * 2, ImGui.CalcTextSize(_error, false, width).Y + ImGui.GetStyle().WindowPadding.Y * 2 + 4);
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
