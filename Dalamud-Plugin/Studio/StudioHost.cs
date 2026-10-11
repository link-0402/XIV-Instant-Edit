using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Services;
using InstantEdit.Studio.Services.Project;
using InstantEdit.Studio.Windows;
using InstantEdit.Studio.Windows.UI;

namespace InstantEdit.Studio;

/// <summary>
/// The Mod Browser's shared state and windows: Penumbra's mod library, the modpack being built and
/// its build window, the Sims 4 importer, and one window per open mod. Drawn and updated once per
/// frame (<see cref="Draw"/>), so builds and writes into mods carry on while every window is closed.
/// </summary>
internal sealed class StudioHost : IDisposable
{
    private readonly Action _openSettings;
    private readonly WindowSystem _windows = new("InstantEditStudio");
    private readonly BuildWindow _buildWindow;
    private readonly SimsImportWindow _simsWindow;

    /// <summary>Open mod windows by mod folder path.</summary>
    private readonly Dictionary<string, ModWindow> _open = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Editors whose windows closed, still writing changes into their mods.</summary>
    private readonly List<ModEditor> _closing = new();

    /// <summary>Windows opened while drawing, added to the window system before the next frame.</summary>
    private readonly List<ModWindow> _toAdd = new();

    private int _opened;

    public StudioSettings Configuration { get; }
    public PenumbraIpcService PenumbraIpc { get; }

    /// <summary>Texture conversion handed to Penumbra, which does it in native code.</summary>
    public PenumbraTextureConverter PenumbraTextures { get; }
    public GameDataService GameData { get; }
    public MaterialTemplates MaterialTemplates { get; }

    /// <summary>Material lists read out of the local .mdl files the build window's set-ups point at.</summary>
    public ModelFileCache ModelFiles { get; } = new();

    /// <summary>Sizes of the source images the set-ups and replacements point at, read from their headers.</summary>
    public ImageFileCache ImageFiles { get; } = new();

    /// <summary>
    /// Where converted textures are kept between builds and updates. Compressing an image is the
    /// slowest part of either, and a port is built many times over while it is worked out, so the
    /// result is kept here and reused until the source file changes.
    /// </summary>
    public string TextureCacheDirectory { get; }

    /// <summary>The folder holding the bundled material presets and placeholder textures (Resources/).</summary>
    public string PluginDirectory { get; }

    /// <summary>The modpack being built: what the build window reads and edits, and what a build turns into a new mod.</summary>
    public PortSession Pack { get; }

    /// <summary>The build window's builds.</summary>
    public BuildController Builds { get; }

    /// <summary>Penumbra's mods for the library, and its mod events.</summary>
    public ModLibrary Library { get; }

    public TextureThumbnailCache Thumbnails { get; }

    /// <summary>Instant Edit's hand-offs (Blender, the texture editor) for the files of an open mod; set by the main window.</summary>
    public IModHandoff? Handoff { get; set; }

    /// <summary>Backups of the files clean-up and repairs change, kept in the cache folder for a week.</summary>
    public Services.Backups.ModBackupStore Backups { get; }

    /// <summary>Penumbra reading a mod again, for repairs and clean-up.</summary>
    public Services.Check.IModReloader Reloader { get; }

    private readonly Func<string> _cacheRoot;
    private readonly Func<ushort?> _playerRace;

    /// <summary>Instant Edit's cache folder, made sure of on every call.</summary>
    public string CacheRoot => _cacheRoot();

    /// <summary>The local player's race code (101 for c0101), or null. Framework thread only.</summary>
    public ushort? PlayerRace() => _playerRace();

    public StudioHost(StudioSettings settings, string configDirectory, Action openSettings, Func<string> cacheRoot, Func<ushort?> playerRace)
    {
        Configuration = settings;
        _openSettings = openSettings;
        _cacheRoot    = cacheRoot;
        _playerRace   = playerRace;
        Backups       = new Services.Backups.ModBackupStore(configDirectory);
        Backups.Load();
        // Files clean-up couldn't delete last time, because something held them; no mod is open yet.
        if (settings.PendingCleanup.Count > 0)
            _ = System.Threading.Tasks.Task.Run(() => Services.CleanUp.PendingCleanup.RetryAsync(settings, _ => false, System.Threading.CancellationToken.None));
        var pi = StudioServices.PluginInterface;

        PenumbraIpc      = new PenumbraIpcService(pi, StudioServices.Log);
        PenumbraTextures = new PenumbraTextureConverter(PenumbraIpc);
        Reloader         = new Services.Check.PenumbraModReloader(PenumbraIpc);
        GameData         = new GameDataService(StudioServices.DataManager, StudioServices.Log);
        // Reading the whole Item sheet takes a moment; do it off the render thread so loading the
        // plugin does not hitch the game. The item browser shows a wait state.
        _ = GameData.WarmUpAsync();
        PluginDirectory       = Path.Combine(pi.AssemblyLocation.DirectoryName ?? string.Empty, "Studio");
        MaterialTemplates     = new MaterialTemplates(GameData, PluginDirectory);
        TextureCacheDirectory = Path.Combine(configDirectory, "studio-texcache");

        Pack       = new PortSession(this);
        Builds     = new BuildController(this, Pack);
        Library    = new ModLibrary(this);
        Thumbnails = new TextureThumbnailCache();
        FileDrop.Initialize(StudioServices.DragDrop);
        Fonts.Initialize(pi.UiBuilder);

        _buildWindow = new BuildWindow(this, Thumbnails);
        _simsWindow  = new SimsImportWindow(this, Thumbnails);
        _windows.AddWindow(_buildWindow);
        _windows.AddWindow(_simsWindow);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Opening windows
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Opens a mod folder in a window of its own, or brings its window forward when it is open already.</summary>
    public bool OpenMod(string folder, out string error)
    {
        error = string.Empty;
        if (Key(folder) is not { } key)
        {
            error = "That is not a folder path.";
            return false;
        }
        folder = key;

        if (_open.TryGetValue(folder, out var shown))
        {
            shown.IsOpen = true;
            shown.BringToFront();
            return true;
        }
        // Two editors must never write one mod: a mod just closed is opened again once its last changes are in.
        if (_closing.Any(e => !e.Finished && string.Equals(Key(e.Project.FolderPath), folder, StringComparison.OrdinalIgnoreCase)))
        {
            error = "Your last changes are still being written into it. Try again in a moment.";
            return false;
        }
        // A build makes this folder from scratch; it is opened once the build is done.
        if (Builds.IsWriting(folder))
        {
            error = "It is still being built. Try again when the build is done.";
            return false;
        }

        if (ModEditor.TryOpen(this, folder, out error) is not { } editor)
            return false;

        Configuration.TouchRecent(editor.Project.FolderPath);
        Configuration.Save();
        var window = new ModWindow(this, editor, Thumbnails, CascadePosition());
        _open[folder] = window;
        _toAdd.Add(window);
        return true;
    }

    /// <summary>Whether a mod folder is open in a window.</summary>
    public bool IsModOpen(string folder)
        => _open.Count > 0 && Key(folder) is { } key && _open.ContainsKey(key);

    /// <summary>A folder's full path without a trailing separator, as open windows are keyed; null for a path that isn't one.</summary>
    private static string? Key(string folder)
    {
        try
        {
            return Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Shows the build window.</summary>
    public void OpenBuild()
    {
        _buildWindow.IsOpen = true;
        _buildWindow.BringToFront();
    }

    public void ToggleSimsImport() => _simsWindow.Toggle();

    public void OpenSettings() => _openSettings();

    /// <summary>Whether any mod is being written right now; a build waits for it.</summary>
    public bool AnySyncRunning
        => _open.Values.Any(w => w.Editor.Sync.Running != null) || _closing.Any(e => e.Sync.Running != null);

    /// <summary>Where a new mod window opens the first time: a little below and right of the last one.</summary>
    private Vector2 CascadePosition()
    {
        var step = 28 * (_opened++ % 8);
        return ImGui.GetMainViewport().WorkPos + new Vector2(120 + step, 90 + step);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Penumbra's events (from the library)
    // ─────────────────────────────────────────────────────────────────────────

    public void OnModDeleted(string folderName)
    {
        foreach (var window in _open.Values.Where(w => SameFolderName(w.Editor.Project.FolderName, folderName)).ToList())
        {
            window.Editor.OnDeleted();
            window.IsOpen = false;
        }
    }

    public void OnModMoved(string from, string to)
    {
        foreach (var (key, window) in _open.Where(p => SameFolderName(p.Value.Editor.Project.FolderName, from)).ToList())
        {
            window.Editor.OnMoved(to);
            _open.Remove(key);
            _open[Key(window.Editor.Project.FolderPath) ?? window.Editor.Project.FolderPath] = window;
        }
    }

    private static bool SameFolderName(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // ─────────────────────────────────────────────────────────────────────────
    // Frame
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Called once per frame: updates the library, the build and every open mod, then draws the windows.</summary>
    public void Draw()
    {
        Thumbnails.NewFrame();
        if (Configuration.TakeSaveRequest())
            Configuration.Save();   // asked for by a worker
        Builds.Update();   // finishes a build even while the build window is closed
        Library.Update();  // Penumbra's mod events and the library listing

        foreach (var window in _toAdd)
            _windows.AddWindow(window);
        _toAdd.Clear();

        foreach (var (key, window) in _open.ToList())
        {
            if (!window.IsOpen || !window.Editor.IsOpen)
            {
                // Closed: the editor lets go of the mod, and finishes writing it after the window is gone.
                _open.Remove(key);
                _windows.RemoveWindow(window);
                window.Editor.Close();
                _closing.Add(window.Editor);
                // What was written into the mod shows in the library's filters from here on.
                Library.Changed(window.Editor.Project.FolderName);
                continue;
            }
            window.Editor.Update();
        }

        for (int i = _closing.Count - 1; i >= 0; i--)
        {
            var editor = _closing[i];
            editor.Update();
            if (!editor.Finished)
                continue;
            editor.Dispose();
            _closing.RemoveAt(i);
        }

        Pack.FlushIfDue();
        _windows.Draw();

        // One shared file dialog, drawn once after every window.
        Gui.Dialogs.Draw();
    }

    public void Dispose()
    {
        // Edits are saved in batches; write whatever is still pending before anything goes away. An
        // update of a mod under way is given a moment to finish (it saves its own ledger); changes
        // not yet written into their mod are written the next time it is opened.
        Pack.Flush();
        Builds.Cancel();
        foreach (var window in _open.Values)
        {
            window.Editor.Flush();
            window.Editor.Dispose();
        }
        foreach (var editor in _closing)
            editor.Dispose();
        _open.Clear();
        _closing.Clear();
        Library.Dispose();
        if (Configuration.TakeSaveRequest())
            Configuration.Save();

        _windows.RemoveAllWindows();
        _buildWindow.Dispose();
        _simsWindow.Dispose();
        PenumbraIpc.Dispose();
        Thumbnails.Dispose();
        Fonts.Dispose();
    }
}
