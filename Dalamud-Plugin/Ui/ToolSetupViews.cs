using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using InstantEdit.Services;
using InstantEdit.Services.Painter;
using InstantEdit.Services.Setup;

namespace InstantEdit.Ui;

/// <summary>
/// Finds Blender, texture editors and Substance Painter on this PC and sets up Instant Edit's
/// add-on, save scripts and plugin in them. First-time setup and Settings draw the same panels.
/// </summary>
internal sealed class ToolSetupViews
{
    private sealed record Detection(
        IReadOnlyList<BlenderInstall> Blenders,
        IReadOnlyDictionary<string, IReadOnlyList<BlenderAddonCopy>> AddonCopies,
        IReadOnlySet<string> RunningBlenders,
        IReadOnlyList<TextureEditor> Editors,
        IReadOnlyDictionary<string, SaveScriptState> Scripts,
        string PainterExecutable,
        (string Executable, string Version) PainterVersion);

    private sealed record ActionState(bool Busy, FeedbackSeverity Severity, string Message, bool OfferReplace = false);

    private static readonly TimeSpan DetectionInterval = TimeSpan.FromSeconds(5);

    private readonly Configuration _config;
    private readonly Action _saveConfig;
    private readonly BlenderClient _blender;
    private readonly IPluginLog _log;
    private readonly FileDialogManager _fileDialog = new();
    private readonly object _lock = new();
    private readonly Dictionary<string, ActionState> _actions = new(StringComparer.OrdinalIgnoreCase);
    private Detection? _detection;
    private DateTime _detectedAt = DateTime.MinValue;
    private bool _detecting;
    private BlenderStatus? _blenderStatus;
    private DateTime _blenderStatusAt = DateTime.MinValue;
    private bool _blenderStatusBusy;
    private string _painterMessage = "";
    private PainterPluginState? _painterPlugin;
    private DateTime _painterPluginChecked = DateTime.MinValue;
    private bool _painterPluginChecking;
    private int? _pendingPainterPort;

    /// <summary> The installed Painter plugin: its version, whether its files are this build's, and its folder. </summary>
    private sealed record PainterPluginState(string Installed, bool FilesMatch, string Folder);

    public ToolSetupViews(Configuration config, Action saveConfig, BlenderClient blender, IPluginLog log)
    {
        _config = config;
        _saveConfig = saveConfig;
        _blender = blender;
        _log = log;
    }

    public void DrawFileDialog() => _fileDialog.Draw();

    /// <summary> Searches again on the next draw, for example when a window opens. </summary>
    public void Refresh()
    {
        lock (_lock)
        {
            _detectedAt = DateTime.MinValue;
            _painterPluginChecked = DateTime.MinValue;
        }
        _blenderStatusAt = DateTime.MinValue;
        PainterInstallation.Refresh();
    }

    /// <summary>
    /// The installed Painter plugin as last read, or null before the first read. It is read again in
    /// the background every few seconds: reading it touches files and Painter's log.
    /// </summary>
    private PainterPluginState? PainterPlugin()
    {
        lock (_lock)
        {
            if (_painterPluginChecking || DateTime.UtcNow - _painterPluginChecked < DetectionInterval)
                return _painterPlugin;
            _painterPluginChecking = true;
        }
        _ = Task.Run(() =>
        {
            PainterPluginState? found = null;
            try
            {
                var installed = PainterInstallation.InstalledVersion();
                found = new PainterPluginState(installed, installed.Length > 0 && PainterInstallation.FilesMatch(),
                    PainterInstallation.PluginsDirectory);
            }
            catch (Exception error)
            {
                _log.Warning(error, "Could not check the Substance Painter plugin.");
            }
            finally
            {
                lock (_lock)
                {
                    if (found is not null)
                        _painterPlugin = found;
                    _painterPluginChecked = DateTime.UtcNow;
                    _painterPluginChecking = false;
                }
            }
        });
        lock (_lock)
            return _painterPlugin;
    }

    // ---- Detection -------------------------------------------------------------------------

    /// <summary> The last search's findings; starts a new search in the background when they're stale. </summary>
    private Detection? CurrentDetection()
    {
        lock (_lock)
        {
            if (_detecting || DateTime.UtcNow - _detectedAt < DetectionInterval)
                return _detection;
            _detecting = true;
        }
        var addedBlenders = _config.BlenderExecutables.ToArray();
        var configuredPainter = _config.PainterExecutablePath;
        _ = Task.Run(() =>
        {
            Detection? found = null;
            try
            {
                var blenders = BlenderSetup.Detect(addedBlenders);
                var editors = TextureEditorSetup.Detect();
                PainterInstallation.Refresh();
                var painter = PainterInstallation.DetectExecutable();
                var effectivePainter = configuredPainter.Length > 0 ? configuredPainter : painter;
                found = new Detection(
                    blenders,
                    blenders.ToDictionary(install => install.Executable, install => BlenderSetup.InstalledCopies(install), StringComparer.OrdinalIgnoreCase),
                    blenders.Where(BlenderSetup.IsRunning).Select(install => install.Executable).ToHashSet(StringComparer.OrdinalIgnoreCase),
                    editors,
                    editors.ToDictionary(editor => editor.Executable, editor => TextureEditorSetup.ScriptState(editor), StringComparer.OrdinalIgnoreCase),
                    painter,
                    (effectivePainter, File.Exists(effectivePainter) ? InstalledSoftware.FileProductVersion(effectivePainter) : ""));
            }
            catch (Exception error)
            {
                _log.Warning(error, "Could not search for Blender, texture editors and Substance Painter.");
            }
            finally
            {
                lock (_lock)
                {
                    if (found is not null)
                        _detection = found;
                    _detectedAt = DateTime.UtcNow;
                    _detecting = false;
                }
            }
        });
        lock (_lock)
            return _detection;
    }

    private ActionState? Action(string key)
    {
        lock (_lock)
            return _actions.GetValueOrDefault(key);
    }

    private bool Busy(string key) => Action(key)?.Busy == true;

    /// <summary> Runs an install in the background and keeps its outcome to show under the item. </summary>
    private void Run(string key, Func<Task<ActionState>> work)
    {
        lock (_lock)
        {
            if (_actions.TryGetValue(key, out var running) && running.Busy)
                return;
            _actions[key] = new ActionState(true, FeedbackSeverity.Info, "");
        }
        _ = Task.Run(async () =>
        {
            ActionState outcome;
            try
            {
                outcome = await work().ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException
                                              or System.ComponentModel.Win32Exception or OperationCanceledException)
            {
                outcome = new ActionState(false, FeedbackSeverity.Error, error.Message);
            }
            catch (Exception error)
            {
                _log.Error(error, "Tool setup failed.");
                outcome = new ActionState(false, FeedbackSeverity.Error, error.Message);
            }
            lock (_lock)
            {
                _actions[key] = outcome;
                _detectedAt = DateTime.MinValue;
            }
            _blenderStatusAt = DateTime.MinValue;
        });
    }

    private void DrawActionMessage(string key)
    {
        var action = Action(key);
        if (action is null || action.Busy || action.Message.Length == 0)
            return;
        Widgets.Banner("##tool-action-" + key, action.Severity, action.Message);
    }

    private static void Searching(string what)
    {
        Widgets.Spinner();
        ImGui.SameLine(0, Theme.Gap);
        ImGui.AlignTextToFramePadding();
        Widgets.Hint($"Looking for {what}…");
    }

    // ---- Blender ---------------------------------------------------------------------------

    /// <summary> Blender installs with the add-on's state in each, and buttons that set it up from GitHub. </summary>
    public void DrawBlender()
    {
        DrawBlenderConnection();
        ImGui.Spacing();
        var detection = CurrentDetection();
        if (detection is null)
        {
            Searching("Blender");
            return;
        }

        if (detection.Blenders.Count == 0)
            Widgets.Banner("##blender-none", FeedbackSeverity.Warning,
                "No Blender was found. Install Blender 4.5 or newer from blender.org or Steam; it shows up here once installed.");
        foreach (var install in detection.Blenders)
        {
            using var id = ImRaii.PushId(install.Executable);
            DrawBlenderInstall(install, detection.AddonCopies.GetValueOrDefault(install.Executable) ?? [],
                detection.RunningBlenders.Contains(install.Executable));
            ImGui.Spacing();
        }

        if (ImGui.Button("Add a Blender that isn't listed…"))
        {
            var start = detection.Blenders.Count > 0
                ? Path.GetDirectoryName(Path.GetDirectoryName(detection.Blenders[0].Executable)!)!
                : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            _fileDialog.OpenFileDialog("Choose blender.exe", "Blender{.exe}", (success, files) =>
            {
                if (!success || files.Count == 0)
                    return;
                var (install, error) = BlenderSetup.FromExecutable(files[0], true);
                lock (_lock)
                    _actions["blender:add"] = new ActionState(false, install is null ? FeedbackSeverity.Error : FeedbackSeverity.Success,
                        install is null ? error : $"Added Blender {install.Version}.");
                if (install is not null && !_config.BlenderExecutables.Any(path => InstalledSoftware.SamePath(path, install.Executable)))
                {
                    _config.BlenderExecutables.Add(install.Executable);
                    _saveConfig();
                }
                Refresh();
            }, 1, start, true);
        }
        ImGui.SameLine();
        Widgets.Hint("For example a Blender unpacked from a ZIP.");
        DrawActionMessage("blender:add");
        ImGui.Spacing();

        Widgets.HintWrapped("Installing adds the XIV Instant Edit repository to Blender's Get Extensions, with Check for Updates on " +
                            "Startup turned on, and turns on Blender's Allow Online Access so that check can run. Blender then offers " +
                            "each new version of the add-on when it starts.");
        if (ImGui.SmallButton("Copy repository URL"))
            ImGui.SetClipboardText(BlenderSetup.RepositoryUrl);
        ImGui.SameLine();
        Widgets.Hint("To add it by hand: Edit > Preferences > Get Extensions > Repositories > + > Add Remote Repository.");
    }

    private void DrawBlenderConnection()
    {
        if (!_blenderStatusBusy && DateTime.UtcNow - _blenderStatusAt > TimeSpan.FromSeconds(3))
        {
            _blenderStatusBusy = true;
            _ = Task.Run(async () =>
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    _blenderStatus = await _blender.GetStatusAsync(_config.BlenderPort, timeout.Token).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    _log.Debug(error.Message);
                    _blenderStatus = new BlenderStatus(false, null);
                }
                finally
                {
                    _blenderStatusAt = DateTime.UtcNow;
                    _blenderStatusBusy = false;
                }
            });
        }

        var plugin = BlenderClient.CurrentPluginVersion;
        switch (_blenderStatus?.Classify(plugin))
        {
            case BlenderConnectionState.Online:
                Widgets.StatusDot("Blender", Theme.Online, $"connected, add-on {_blenderStatus!.AddonVersion}");
                break;
            case BlenderConnectionState.VersionMismatch:
                Widgets.StatusDot("Blender", Theme.Mismatch,
                    $"connected, but the add-on is {_blenderStatus!.AddonVersion ?? "an older version"} and this plugin is {plugin}. Update it below.");
                break;
            default:
                Widgets.StatusDot("Blender", Theme.Offline, "not connected. Start Blender once the add-on is installed.");
                break;
        }
    }

    private void DrawBlenderInstall(BlenderInstall install, IReadOnlyList<BlenderAddonCopy> copies, bool running)
    {
        ImGui.TextColored(Theme.Text, $"Blender {install.Version}");
        ImGui.SameLine(0, Theme.Gap);
        ImGui.TextColored(Theme.Hint, Path.GetDirectoryName(install.Executable) ?? install.Executable);
        if (install.AddedByHand)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Remove from list"))
            {
                _config.BlenderExecutables.RemoveAll(path => InstalledSoftware.SamePath(path, install.Executable));
                _saveConfig();
                Refresh();
            }
        }

        if (BlenderSetup.UnsupportedReason(install) is { Length: > 0 } unsupported)
        {
            ImGui.TextColored(Theme.Mismatch, "●");
            ImGui.SameLine(0, Theme.Scaled(4));
            ImGui.TextColored(Theme.Muted, unsupported);
            return;
        }

        var plugin = BlenderClient.NormalizeVersion(BlenderClient.CurrentPluginVersion);
        var online = copies.FirstOrDefault(copy => !copy.FromFile);
        var fromFile = copies.FirstOrDefault(copy => copy.FromFile);
        if (online is not null)
        {
            var current = BlenderClient.NormalizeVersion(online.Version) == plugin;
            ImGui.TextColored(current ? Theme.Success : Theme.Mismatch, current ? "✓" : "●");
            ImGui.SameLine(0, Theme.Scaled(4));
            ImGui.TextColored(Theme.Muted, current
                ? $"Add-on {online.Version}, updated from GitHub."
                : $"Add-on {online.Version} from GitHub; this plugin is {plugin}.");
        }
        else if (fromFile is not null)
        {
            ImGui.TextColored(Theme.Mismatch, "●");
            ImGui.SameLine(0, Theme.Scaled(4));
            ImGui.TextColored(Theme.Muted, $"Add-on {fromFile.Version} installed from a file, so it doesn't update itself.");
        }
        else
        {
            ImGui.TextColored(Theme.Inactive, "○");
            ImGui.SameLine(0, Theme.Scaled(4));
            ImGui.TextColored(Theme.Muted, "Add-on not installed.");
        }
        if (online is not null && fromFile is not null)
            Widgets.Hint($"A second copy ({fromFile.Version}, from a file) is also installed; only one can be enabled.");

        var key = "blender:" + install.Executable;
        var action = Action(key);
        var busy = action?.Busy == true;
        // A copy from a file clashes with the GitHub one, so switching replaces it.
        var replace = (online is null && fromFile is not null) || action?.OfferReplace == true;
        var label = online is null
            ? fromFile is null ? "Install add-on" : "Switch to the GitHub add-on"
            : BlenderClient.NormalizeVersion(online.Version) == plugin ? "Reinstall add-on" : "Update add-on";
        if (action?.OfferReplace == true)
            label = "Replace the other copy";
        using (ImRaii.Disabled(busy || running))
        {
            if (ImGui.Button(label))
                Run(key, async () =>
                {
                    var result = await BlenderSetup.InstallAsync(install, replace).ConfigureAwait(false);
                    if (!result.Ok)
                        return new ActionState(false, FeedbackSeverity.Error, result.Error,
                            OfferReplace: !replace && result.OtherCopies.Count > 0);
                    var message = $"Installed add-on {result.InstalledVersion} in Blender {install.Version}, with update checks at startup.";
                    if (result.InstalledVersion.Length > 0 && BlenderClient.NormalizeVersion(result.InstalledVersion) != plugin)
                        message += $" GitHub doesn't have {plugin} yet; Blender offers it once it's published.";
                    return new ActionState(false, FeedbackSeverity.Success, message + " Start Blender to use it.");
                });
        }
        if (replace && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Removes the add-on installed from a file, then installs it from GitHub.");
        ImGui.SameLine();
        using (ImRaii.Disabled(busy || running))
        {
            if (ImGui.Button("Start Blender"))
            {
                try { BlenderSetup.Start(install); }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    lock (_lock)
                        _actions[key] = new ActionState(false, FeedbackSeverity.Error, "Blender could not be started: " + error.Message);
                }
                Refresh();
            }
        }
        ImGui.SameLine();
        if (busy)
        {
            Widgets.Spinner();
            ImGui.SameLine(0, Theme.Gap);
            ImGui.AlignTextToFramePadding();
            Widgets.Hint("Installing from GitHub…");
        }
        else if (running)
        {
            ImGui.AlignTextToFramePadding();
            Widgets.Hint("Close this Blender to install or update the add-on.");
        }
        DrawActionMessage(key);
    }

    // ---- Texture editors -------------------------------------------------------------------

    /// <summary>
    /// The texture editor path, with the editors found on this PC to pick from and their save
    /// scripts to install. <paramref name="setPath"/> receives a trimmed path.
    /// </summary>
    public void DrawTextureEditors(string path, Action<string> setPath)
    {
        ImGui.Text("Texture editor executable");
        var edited = path;
        ImGui.SetNextItemWidth(-Theme.Scaled(80));
        if (ImGui.InputTextWithHint("##texture-editor-path", "Full path to Photoshop.exe or another TGA editor", ref edited, 2048))
            setPath(edited.Trim().Trim('"'));
        ImGui.SameLine();
        if (ImGui.Button("Browse…##texture-editor"))
        {
            var start = Directory.Exists(Path.GetDirectoryName(path) ?? "") ? Path.GetDirectoryName(path)! : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            _fileDialog.OpenFileDialog("Choose the texture editor", "Programs{.exe}", (success, files) =>
            {
                if (success && files.Count > 0)
                    setPath(files[0]);
            }, 1, start, true);
        }
        if (path.Length > 0 && !TextureEditService.TryValidateEditorPath(path, out var error))
            ImGui.TextColored(Theme.Error, error);

        ImGui.Spacing();
        var detection = CurrentDetection();
        if (detection is null)
        {
            Searching("texture editors");
            return;
        }
        if (detection.Editors.Count == 0)
        {
            Widgets.HintWrapped("No Photoshop, GIMP, Krita or Paint.NET was found. Any editor that opens and saves 32-bit TGA files works; choose its program above.");
            return;
        }

        Widgets.SectionHeader("Found on this PC");
        foreach (var editor in detection.Editors)
        {
            using var id = ImRaii.PushId(editor.Executable);
            var selected = path.Length > 0 && InstalledSoftware.SamePath(path, editor.Executable);
            if (ImGui.RadioButton(editor.Name, selected))
                setPath(editor.Executable);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(editor.Executable);
            ImGui.SameLine(Theme.Scaled(220));
            DrawSaveScripts(editor, detection.Scripts.GetValueOrDefault(editor.Executable));
            DrawActionMessage("editor:" + editor.Executable);
        }
        ImGui.Spacing();
        Widgets.HintWrapped("The save scripts add two actions: 'SaveFlattenedTGA' and 'SaveFlattenedTGAVariant', which save the current texture back as a flattened 32-bit TGA in one " +
                            "step while keeping layers in your editing copy. Saving as a variant automatically sets up a Penumbra mapping for this texture under the new name.");
    }

    private void DrawSaveScripts(TextureEditor editor, SaveScriptState state)
    {
        var key = "editor:" + editor.Executable;
        if (editor.Unsupported.Length > 0)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Mismatch, editor.Unsupported);
            return;
        }
        if (state == SaveScriptState.NotNeeded)
        {
            ImGui.AlignTextToFramePadding();
            Widgets.Hint("Saves flattened TGAs on its own; choose 32-bit when saving.");
            return;
        }
        if (state == SaveScriptState.Installed && !Busy(key))
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Success, "✓");
            ImGui.SameLine(0, Theme.Scaled(4));
            ImGui.TextColored(Theme.Muted, "Save scripts installed.");
            return;
        }
        using (ImRaii.Disabled(Busy(key)))
        {
            if (ImGui.Button(state == SaveScriptState.Outdated ? "Update save scripts" : "Install save scripts"))
                Run(key, async () => new ActionState(false, FeedbackSeverity.Success,
                    await TextureEditorSetup.InstallScriptsAsync(editor).ConfigureAwait(false)));
        }
        if (editor.Kind == TextureEditorKind.Photoshop && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip($"Photoshop reads scripts only from {editor.ScriptFolder}.\nWindows asks for administrator permission to copy them there.");
        if (Busy(key))
        {
            ImGui.SameLine();
            Widgets.Spinner();
        }
    }

    // ---- Substance Painter -----------------------------------------------------------------

    /// <summary> The Painter integration switch, where Painter was found, and the plugin install. <paramref name="advanced"/> adds an executable override; Settings draws the port with <see cref="DrawPainterPort"/>. </summary>
    public void DrawPainter(bool advanced)
    {
        var enabled = _config.PainterIntegrationEnabled;
        if (ImGui.Checkbox("Paint textures in Substance Painter", ref enabled)) { _config.PainterIntegrationEnabled = enabled; _saveConfig(); }
        Widgets.Hint("Adds a paint-roller action to On Screen models, a Painter status dot, and Painter projects under Sessions.");

        var detection = CurrentDetection();
        var configured = _config.PainterExecutablePath;
        if (configured.Length > 0)
            Widgets.StatusDot("Painter", File.Exists(configured) ? Theme.Online : Theme.Offline,
                File.Exists(configured) ? configured : "the executable set below doesn't exist");
        else if (detection is null)
            Searching("Substance Painter");
        else if (detection.PainterExecutable.Length > 0)
            Widgets.StatusDot("Painter", Theme.Online, "found at " + detection.PainterExecutable);
        else
            Widgets.StatusDot("Painter", Theme.Offline, $"not found. Painter {PainterInstallation.MinimumVersion} or newer from Adobe or Steam is needed.");

        // The version belongs to the Painter shown above once the search has caught up with the setting.
        var effective = configured.Length > 0 ? configured : detection?.PainterExecutable ?? "";
        var version = detection?.PainterVersion is { } known && InstalledSoftware.SamePath(known.Executable, effective) ? known.Version : "";
        var tooOld = PainterInstallation.TooOld(version);
        if (tooOld)
            Widgets.Banner("##painter-too-old", FeedbackSeverity.Warning,
                $"This Painter is version {version}, and the Instant Edit plugin needs {PainterInstallation.MinimumVersion} or newer. " +
                (advanced ? "Update Painter, or set a newer Painter's program below." : "Update Painter, or choose a newer one in Settings."));
        if (!_config.PainterIntegrationEnabled)
            return;

        var current = BlenderClient.CurrentPluginVersion;
        if (PainterPlugin() is not { } plugin)
            Searching("the Painter plugin");
        else
        {
            var installed = plugin.Installed;
            var sameVersion = installed.Length > 0 && BlenderClient.NormalizeVersion(installed) == BlenderClient.NormalizeVersion(current);
            var upToDate = sameVersion && plugin.FilesMatch;
            if (!tooOld && ImGui.Button(installed.Length == 0 ? "Install Painter plugin" : upToDate ? "Reinstall Painter plugin" : "Update Painter plugin"))
            {
                try
                {
                    var folder = PainterInstallation.Install(_config.PainterPort, _config.ListenPort, current);
                    _painterMessage = $"Installed to {folder}. In Painter, enable it once under Python > xiv_instant_edit (restart Painter after an update).";
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    _painterMessage = "Could not install the Painter plugin: " + e.Message;
                }
                lock (_lock)
                    _painterPluginChecked = DateTime.MinValue;
            }
            if (!tooOld)
                ImGui.SameLine();
            Widgets.Hint(installed.Length == 0 ? "Not installed yet."
                : upToDate ? $"Version {installed} is installed."
                : sameVersion ? $"Version {installed} is installed, but its files differ from this build's. Update it, then restart Painter."
                : $"Version {installed} is installed; this plugin is {current}.");
            Widgets.Hint("Plugin folder: " + plugin.Folder);
        }
        if (_painterMessage.Length > 0)
            ImGui.TextWrapped(_painterMessage);
        if (!advanced)
            return;

        var executable = _config.PainterExecutablePath;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##painter-exe", "Painter executable (empty: use the one found above)", ref executable, 2048))
        { _config.PainterExecutablePath = executable.Trim().Trim('"'); _saveConfig(); }
        Widgets.Hint("Used to start Painter when a model is sent while Painter is closed.");
    }

    /// <summary> The port the Painter plugin listens on; a new one is written to the installed plugin once the field is left. </summary>
    public void DrawPainterPort(float width)
    {
        var painterPort = _pendingPainterPort ?? _config.PainterPort;
        ImGui.SetNextItemWidth(width);
        if (ImGui.InputInt("Painter port", ref painterPort, 0, 0)) _pendingPainterPort = painterPort;
        if (ImGui.IsItemDeactivatedAfterEdit() && _pendingPainterPort is { } port)
        {
            _pendingPainterPort = null;
            if (port is > 0 and <= 65535 && port != _config.PainterPort)
            {
                _config.PainterPort = port;
                _saveConfig();
                try { PainterInstallation.UpdatePorts(_config.PainterPort, _config.ListenPort); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _log.Debug(e.Message); }
            }
        }
    }
}
