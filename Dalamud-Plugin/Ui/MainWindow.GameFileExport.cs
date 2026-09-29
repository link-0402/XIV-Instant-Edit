using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Services;
using InstantEdit.Services.GameFiles;

namespace InstantEdit.Ui;

public sealed partial class MainWindow
{
    private const string GameExportDialog = "Export game files";
    private const int ManyTextureModels = 500;
    private readonly FileDialogManager _gameExportFolderDialog = new();
    private IReadOnlyList<GameModelEntry> _gameExportModels = [];
    private (string Path, string Label)[] _gameExportForbidden = [];
    private string _gameExportCacheRoot = string.Empty;
    private string _gameExportRoot = string.Empty;
    private string _gameExportSummary = string.Empty;
    private bool _openGameExportDialog;
    private GameExportResult? _dismissedGameExport;

    /// <summary> Draws the export folder picker; the plugin hooks it to the UI builder like the setup window's. </summary>
    public void DrawFileDialog() => _gameExportFolderDialog.Draw();

    private void OpenGameExport(IReadOnlyList<GameModelEntry> models)
    {
        if (models.Count == 0 || _gameExport is null)
            return;
        _gameExportModels = models;
        _gameExportCacheRoot = GameExportCacheRoot();
        // An empty setting means the export folder in the cache, so it follows the cache folder.
        _gameExportRoot = _config.GameExportDirectory.Length > 0 ? _config.GameExportDirectory : DefaultGameExportFolder();
        _gameExportSummary = string.Join(" · ", models.GroupBy(model => model.Id.Category).OrderBy(group => group.Key)
            .Select(group => $"{group.Count():N0} {GameModelPaths.CategoryLabel(group.Key).ToLowerInvariant()}"));
        _gameExportForbidden = ForbiddenExportFolders();
        _openGameExportDialog = true;
    }

    private string GameExportCacheRoot()
    {
        try { return TextureFiles.CacheRootFor(_config.TextureCacheDirectory); }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
        {
            _log.Debug(e, "The cache folder could not be resolved for the export folder.");
            return string.Empty;
        }
    }

    /// <summary> The export folder in Instant Edit's cache, where automatic cleanup removes old exports; empty if the cache folder is invalid. </summary>
    private string DefaultGameExportFolder()
        => _gameExportCacheRoot.Length > 0 ? GameExportPaths.CacheExportFolder(_gameExportCacheRoot) : string.Empty;

    /// <summary> Whether <paramref name="path"/> is a full path inside <paramref name="folder"/> or the folder itself. </summary>
    private static bool InFolder(string path, string folder)
    {
        try { return Path.IsPathFullyQualified(path) && PathRules.IsPathWithin(path, folder); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    /// <summary> Folders an export may not go into: files there would unsettle Penumbra or the game. The cache folder is checked apart. </summary>
    private (string, string)[] ForbiddenExportFolders()
    {
        var folders = new List<(string, string)>();
        try
        {
            if (_penumbra.GetModDirectory() is { Length: > 0 } mods)
                folders.Add((mods, "Penumbra's mod folder"));
        }
        catch (Exception e) { _log.Debug(e, "Penumbra's mod folder could not be read for the export check."); }
        if (_gameFiles?.GameFolder is { Length: > 0 } game)
            folders.Add((game, "the game's folder"));
        return [.. folders];
    }

    private void DrawGameFileExportDialog()
    {
        if (_openGameExportDialog)
        {
            _openGameExportDialog = false;
            ImGui.OpenPopup(GameExportDialog);
        }
        ImGui.SetNextWindowSizeConstraints(Theme.Scaled(560, 0), Theme.Scaled(820, 720));
        if (!ImGui.BeginPopupModal(GameExportDialog, ImGuiWindowFlags.AlwaysAutoResize))
            return;
        var models = _gameExportModels;
        ImGui.TextColored(Theme.Text, models.Count == 1 ? models[0].GamePath : $"{models.Count:N0} models");
        ImGui.SameLine(0, Theme.Gap);
        ImGui.TextColored(Theme.Muted, _gameExportSummary);
        using (ImRaii.TextWrapPos(Theme.Scaled(620)))
            Widgets.HintWrapped("Each file is written at its game path under the folder, so files several models use are written once. " +
                                "A manifest lists every model with its materials, textures and skeleton files, and whatever could not be exported.");
        ImGui.Spacing();

        ImGui.TextColored(Theme.Label, "Export folder");
        ImGui.SetNextItemWidth(Theme.Scaled(440));
        if (ImGui.InputTextWithHint("##game-export-folder", @"For example: D:\XIV exports", ref _gameExportRoot, 4096))
            _gameExportRoot = _gameExportRoot.Trim().Trim('"');
        ImGui.SameLine();
        if (ImGui.Button("Browse##game-export"))
        {
            // The picker is its own window, which this modal would block: step aside until it closes.
            ImGui.CloseCurrentPopup();
            _gameExportFolderDialog.OpenFolderDialog("Choose the export folder",
                (success, path) =>
                {
                    if (success)
                        _gameExportRoot = path;
                    _openGameExportDialog = true;
                },
                Directory.Exists(_gameExportRoot) ? _gameExportRoot : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                true);
        }
        var defaultFolder = DefaultGameExportFolder();
        ImGui.SameLine();
        using (ImRaii.Disabled(defaultFolder.Length == 0 || PathRules.SamePhysicalPath(_gameExportRoot, defaultFolder)))
        {
            if (ImGui.Button("Default##game-export"))
                _gameExportRoot = defaultFolder;
        }
        if (defaultFolder.Length > 0 && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip($"Export to the folder in Instant Edit's cache: {defaultFolder}");
        if (defaultFolder.Length > 0 && InFolder(_gameExportRoot, defaultFolder))
            Widgets.Hint(_config.AutomaticCacheCleanup
                ? $"In Instant Edit's cache: automatic cache cleanup removes files here that no export has written for {GameExportPaths.CacheRetention.TotalDays:0} days."
                : "In Instant Edit's cache. Automatic cache cleanup is off in Settings, so files here are kept.");

        ImGui.Spacing();
        Widgets.SectionHeader("Include", "models are always exported");
        var changed = false;
        var materials = _config.GameExportMaterials;
        if (ImGui.Checkbox("Materials", ref materials))
        {
            _config.GameExportMaterials = materials;
            changed = true;
        }
        ImGui.SameLine(0, Theme.Gap);
        Widgets.HelpTip("Each model's .mtrl files, and for gear and weapons the IMC file that picks their material variants.");
        var textures = _config.GameExportTextures;
        if (ImGui.Checkbox("Textures", ref textures))
        {
            _config.GameExportTextures = textures;
            changed = true;
        }
        ImGui.SameLine(0, Theme.Gap);
        Widgets.HelpTip("The .tex files the materials use, as the game stores them.");
        using (ImRaii.Disabled(!materials && !textures))
        {
            var variants = _config.GameExportAllVariants;
            if (ImGui.Checkbox("Every material variant", ref variants))
            {
                _config.GameExportAllVariants = variants;
                changed = true;
            }
        }
        ImGui.SameLine(0, Theme.Gap);
        Widgets.HelpTip("Gear and weapons: the materials of every variant (dye and colour versions) instead of the default one only.");
        var skeletonFiles = _config.GameExportSkeletonFiles;
        if (ImGui.Checkbox("Skeleton files", ref skeletonFiles))
        {
            _config.GameExportSkeletonFiles = skeletonFiles;
            changed = true;
        }
        ImGui.SameLine(0, Theme.Gap);
        Widgets.HelpTip("The .sklb files the game loads for each model: the race's body and the hair, face, headgear or top skeleton its EST table picks, " +
                        "and the EST tables themselves. A hairstyle's skeleton number can differ from its own.");
        var canDecode = _gameExport?.CanDecodeSkeletons ?? false;
        using (ImRaii.Disabled(!canDecode))
        {
            var skeletonJson = _config.GameExportSkeletonJson && canDecode;
            if (ImGui.Checkbox("Decoded skeletons", ref skeletonJson))
            {
                _config.GameExportSkeletonJson = skeletonJson;
                changed = true;
            }
        }
        ImGui.SameLine(0, Theme.Gap);
        Widgets.HelpTip("One .skeleton.json next to each skeleton file set: the body and its partial skeleton merged, with every bone's name, parent " +
                        "and rest transform, in the format Instant Edit's Blender add-on reads (instant_edit/skeleton.py). The game decodes one skeleton per frame, " +
                        "so large exports take a minute.");
        if (changed)
            _saveConfig();

        var problem = GameExportPaths.RootProblem(_gameExportRoot, _gameExportForbidden, _gameExportCacheRoot);
        if (problem is not null && _gameExportRoot.Length > 0)
        {
            ImGui.Spacing();
            Widgets.Banner("##game-export-problem", FeedbackSeverity.Error, problem);
        }
        if (textures && models.Count > ManyTextureModels)
        {
            ImGui.Spacing();
            Widgets.Banner("##game-export-size", FeedbackSeverity.Warning,
                $"Textures for {models.Count:N0} models can take many gigabytes of disk space.");
        }

        ImGui.Spacing();
        var busy = _gameExport is not { Busy: false };
        using (ImRaii.Disabled(problem is not null || busy))
        {
            if (ImGui.Button(models.Count == 1 ? "Export 1 model" : $"Export {models.Count:N0} models"))
            {
                StartGameExport();
                ImGui.CloseCurrentPopup();
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel##game-export-dialog"))
            ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void StartGameExport()
    {
        if (_gameExport is not { } export)
            return;
        var root = Path.GetFullPath(_gameExportRoot);
        if (_gameExportCacheRoot.Length > 0 && InFolder(root, _gameExportCacheRoot))
        {
            // The cache folder must be Instant Edit's before files go into it; this creates it if needed.
            try { TextureFiles.EnsureCacheRoot(_config.TextureCacheDirectory); }
            catch (Exception e)
            {
                SetStatus($"The export can't go into Instant Edit's cache folder: {e.Message}", FeedbackSeverity.Error);
                return;
            }
        }
        _config.GameExportDirectory = PathRules.SamePhysicalPath(root, DefaultGameExportFolder()) ? string.Empty : root;
        _saveConfig();
        var options = new GameExportOptions(_config.GameExportMaterials, _config.GameExportTextures, _config.GameExportAllVariants,
            _config.GameExportSkeletonFiles, _config.GameExportSkeletonJson && export.CanDecodeSkeletons);
        var count = _gameExportModels.Count;
        if (export.Start(_gameExportModels, root, options))
            SetStatus(count == 1 ? $"Exporting 1 model to {root}" : $"Exporting {count:N0} models to {root}", FeedbackSeverity.Info);
        else
            SetStatus("Another export is still running.", FeedbackSeverity.Warning);
    }

    // Raised on the export's background thread; the status feed is thread-safe.
    private void OnGameExportFinished(GameExportResult result)
    {
        var (text, severity) = GameExportSummary(result);
        SetStatus(text, severity);
    }

    private static (string Text, FeedbackSeverity Severity) GameExportSummary(GameExportResult result)
    {
        var models = result.Models == 1 ? "1 model" : $"{result.Models:N0} models";
        var files = result.Files == 1 ? "1 file" : $"{result.Files:N0} files";
        var size = result.Bytes >= 1L << 30 ? $"{result.Bytes / (double)(1L << 30):0.0} GB" : $"{result.Bytes / (double)(1L << 20):0.0} MB";
        var failed = result.Failures.Length == 0 ? ""
            : result.Failures.Length == 1 ? " One model could not be exported; the manifest says why."
            : $" {result.Failures.Length:N0} models could not be exported; the manifest says why.";
        return result.Status switch
        {
            GameExportResult.Completed => ($"Exported {models} ({files}, {size}) to {result.Root}.{failed}",
                result.Failures.Length == 0 ? FeedbackSeverity.Success : FeedbackSeverity.Warning),
            GameExportResult.Cancelled => ($"Export cancelled after {models} ({files}). The manifest lists what was written.", FeedbackSeverity.Warning),
            _ => ($"Export failed: {result.Error}" + (result.ManifestPath is null ? "" : " The manifest lists what was written."), FeedbackSeverity.Error),
        };
    }

    /// <summary> The running export's progress bar, or the last export's result until it is dismissed. </summary>
    private void DrawGameExportStatus()
    {
        if (_gameExport is not { } export)
            return;
        if (export.Progress is { } progress)
        {
            ImGui.Spacing();
            var fraction = progress.Total == 0 ? 0 : Math.Clamp((float)progress.Done / progress.Total, 0, 1);
            var phase = progress.Phase == "Skeletons" ? "Decoding skeletons" : "Writing files";
            var width = Math.Max(Theme.Scaled(160), Math.Min(Theme.Scaled(420), ImGui.GetContentRegionAvail().X * .55f));
            ImGui.ProgressBar(fraction, new Vector2(width, 0), $"{phase}: {progress.Done:N0} / {progress.Total:N0}");
            ImGui.SameLine();
            if (ImGui.Button("Cancel##game-export"))
                export.Cancel();
            if (progress.Current.Length > 0)
            {
                ImGui.SameLine(0, Theme.Gap);
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(Theme.Hint, progress.Current[(progress.Current.LastIndexOf('/') + 1)..]);
            }
            return;
        }
        if (export.LastResult is not { } result || ReferenceEquals(result, _dismissedGameExport))
            return;
        ImGui.Spacing();
        var (text, severity) = GameExportSummary(result);
        Widgets.Banner("##game-export-result", severity, text);
        using (ImRaii.Disabled(!Directory.Exists(result.Root)))
        {
            if (ImGui.SmallButton("Open folder"))
                OpenFolder(result.Root);
        }
        if (result.ManifestPath is { } manifest && File.Exists(manifest))
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Open manifest"))
                OpenFolder(manifest);
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Dismiss##game-export"))
            _dismissedGameExport = result;
    }
}
