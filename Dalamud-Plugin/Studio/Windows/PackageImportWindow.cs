using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using InstantEdit.Studio.Services;
using InstantEdit.Studio.Services.Sims;
using InstantEdit.Studio.Services.Stash;
using InstantEdit.Studio.Services.Unity;
using InstantEdit.Studio.Windows.Import;
using InstantEdit.Studio.Windows.UI;

namespace InstantEdit.Studio.Windows;

/// <summary>
/// Imports a Sims 4 .package or a Unity .unitypackage, then serves as the file stash.
///
/// A scan lists what the package holds; the user unticks what they don't want and extracts the
/// rest into a folder of its own under the output folder (the cache's package-imports by
/// default). A Sims package comes out in the layout the material editor consumes (a folder of
/// diffuse swatches, single files for the other roles); a Unity package keeps its project layout.
/// Either way the window then shows the stash: every extraction, its files by kind and purpose,
/// each one draggable onto a texture or model tile of the build window or a mod window.
///
/// Scanning and extraction run off the render thread: a package can run to hundreds of
/// megabytes. The worker only returns its result and publishes a progress line; everything else
/// is applied here on the render thread once the task has finished.
/// </summary>
public sealed class PackageImportWindow : Window, IDisposable
{
    private readonly StudioHost _host;
    private readonly TextureThumbnailCache _thumbnails;
    private readonly StashView _stash;

    private string _packagePath = string.Empty;
    private string _outputDir   = string.Empty;

    /// <summary>Whether the stash is shown rather than the import steps.</summary>
    private bool _showStash;

    // ── Sims ─────────────────────────────────────────────────────────────────
    private SimsScanResult? _scan;
    private SimsPackageInspector.ExtractionResult? _extraction;

    /// <summary>Whether to halve textures whose used content sits in one half.</summary>
    private bool _cropHalf = true;

    /// <summary>Index into <see cref="_lodOptions"/>; 0 is "All LODs".</summary>
    private int _lodChoice;
    private string[] _lodOptions = { AllLodsLabel };
    private const string AllLodsLabel = "All LODs";

    private Task<SimsScanResult>? _scanTask;
    private Task<SimsPackageInspector.ExtractionResult>? _extractTask;

    // ── Unity ────────────────────────────────────────────────────────────────
    private UnityScanResult? _unityScan;
    private Task<UnityScanResult>? _unityScanTask;
    private Task<UnityExtraction>? _unityExtractTask;

    /// <summary>Stops the scan or extraction under way: the Stop button, closing the window, or the plugin going away.</summary>
    private CancellationTokenSource? _cts;

    /// <summary>The one field the worker writes: its latest progress line.</summary>
    private volatile string _progress = string.Empty;

    private string _status       = string.Empty;
    private string _errorMessage = string.Empty;
    private string _applyMessage = string.Empty;

    private static readonly string[] RoleLabels =
    {
        "Unknown", "Diffuse", "Normal", "Specular", "Shadow", "Emission", "Region map",
    };

    private static readonly string[] UnityTickOptions = { "Textures and models", "Textures only", "Models only", "Nothing" };
    private int _unityTick;

    internal PackageImportWindow(StudioHost host, TextureThumbnailCache thumbnails) : base("Import Package###StudioSimsImport")
    {
        _host       = host;
        _thumbnails = thumbnails;
        _stash      = new StashView(host, thumbnails);
        Size          = new Vector2(960, 700);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(680, 440) };

        var cfg = host.Configuration;
        _packagePath = cfg.LastSimsPackagePath;
        // A cache that can't be set up (a non-empty folder it doesn't own) leaves the old default.
        var cacheRoot = Services.Backups.BackupFolder.TryResolve(() => host.CacheRoot, out _)
                        ?? Path.Combine(Path.GetTempPath(), "InstantEdit");
        _outputDir   = PackageImportPaths.ChooseOutput(cfg.LastSimsOutputDir, cacheRoot, Path.GetTempPath());
        if (_outputDir != cfg.LastSimsOutputDir && !string.IsNullOrWhiteSpace(cfg.LastSimsOutputDir))
        {
            cfg.LastSimsOutputDir = _outputDir;
            cfg.Save();
        }
    }

    public void Dispose()
    {
        StopWork();
        _thumbnails.ForgetMemoryEntries();
    }

    /// <summary>Opened: the stash when something was extracted before, so its files are a click away.</summary>
    public override void OnOpen()
    {
        if (!Busy && _scan == null && _unityScan == null)
            _showStash = _stash.HasExtractions(_outputDir);
    }

    /// <summary>Closed: what runs is stopped, and whatever it would still have found is dropped.</summary>
    public override void OnClose() => StopWork();

    private void StopWork()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        if (Busy)
            _status = "Stopped.";
        _scanTask         = null;
        _extractTask      = null;
        _unityScanTask    = null;
        _unityExtractTask = null;
    }

    /// <summary>A token for new work; whatever ran before is stopped first.</summary>
    private CancellationToken NewWork()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        return _cts.Token;
    }

    private bool Busy => _scanTask is { IsCompleted: false } || _extractTask is { IsCompleted: false }
                      || _unityScanTask is { IsCompleted: false } || _unityExtractTask is { IsCompleted: false };

    private static bool IsUnity(string path) => Path.GetExtension(path).Equals(".unitypackage", StringComparison.OrdinalIgnoreCase);
    private static bool IsPackage(string path) => IsUnity(path) || Path.GetExtension(path).Equals(".package", StringComparison.OrdinalIgnoreCase);

    // ─────────────────────────────────────────────────────────────────────────
    // Draw
    // ─────────────────────────────────────────────────────────────────────────

    public override void Draw()
    {
        FileDrop.BeginExternalSource();
        CollectFinishedWork();
        bool busy = Busy;

        if (_showStash && !busy)
        {
            DrawStashBar();
            _stash.Draw(_outputDir);
            return;
        }

        DrawSource(busy);
        DrawStatus(busy);
        ImGui.Spacing();

        DrawOptions(busy);

        float footerH = ImGui.GetFrameHeightWithSpacing() * 1.5f;
        if (ImGui.BeginChild("##PackageAssets", new Vector2(-1, -footerH), true))
        {
            if (_unityScan != null) DrawUnityAssets(busy, _unityScan);
            else                    DrawAssetList(busy);
        }
        ImGui.EndChild();

        DrawActions(busy);
    }

    /// <summary>The stash's top line: back to importing, and the Sims shortcut for the extraction just made.</summary>
    private void DrawStashBar()
    {
        if (Gui.IconTextButton(FontAwesomeIcon.FileImport, "Import a package", "Scan and extract another .package or .unitypackage."))
            _showStash = false;

        if (_extraction != null && string.Equals(_stash.Folder, _extraction.RootFolder, StringComparison.OrdinalIgnoreCase))
        {
            ImGui.SameLine();
            bool canApply = _extraction.DiffuseCount > 0 || _extraction.NormalFile != null || _extraction.SpecularFile != null;
            var target = _host.Pack.CurrentMaterial;
            using (Gui.Disabled(!canApply || target == null))
            {
                var label = target != null ? $"Apply all to \"{target.Name}\"" : "Apply all to the selected material";
                if (Gui.IconTextButton(FontAwesomeIcon.ArrowRight, label,
                        target == null
                            ? "Select a material in the build window's Set-up view first."
                            : "Points that material at this extraction: the diffuse folder as a variant group, and the normal and specular images as single textures."))
                {
                    _applyMessage = _host.Pack.ApplySimsExtraction(_extraction);
                    _host.OpenBuild();
                    _host.Pack.GoToStage(Models.StageId.Details);
                }
            }
        }
        if (!string.IsNullOrEmpty(_applyMessage))
        {
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            Gui.Hint(_applyMessage);
        }
        ImGui.Spacing();
    }

    private void DrawSource(bool busy)
    {
        Gui.SectionHeader("1  Package", "Pick a Sims 4 .package or a Unity .unitypackage and scan it. Nothing is written until you extract.");

        using (Gui.Disabled(busy))
        {
            // Typed paths are only kept in memory; they are persisted when a scan starts,
            // so an in-progress path does not write the config file on every keystroke.
            Gui.Label("Package");
            float scanW = Theme.S(110);
            Gui.PathPicker("PackagePath", ref _packagePath, "path to a .package or .unitypackage file", PathKind.File,
                "Packages{.package,.unitypackage}",
                picked => { _packagePath = picked; SaveSettings(); }, reserveRight: scanW + ImGui.GetStyle().ItemSpacing.X);
            if (FileDrop.Target(p => p.Files.Count == 1 && p.Folders.Count == 0 && IsPackage(p.Files[0]) ? null : "Drop one .package or .unitypackage file.",
                    out var dropped))
            {
                _packagePath = dropped.Files[0];
                StartScan();
            }

            ImGui.SameLine();
            if (Gui.PrimaryButton(FontAwesomeIcon.Search, "Scan", scanW, "Reads the package and lists what it holds."))
                StartScan();

            Gui.Label("Output", "A subfolder named after the package is created here.");
            Gui.PathPicker("PackageOutput", ref _outputDir, "folder the extracted files go into", PathKind.Folder, "",
                picked => { _outputDir = picked; SaveSettings(); },
                "A subfolder named after the package is created here.");

            if (_stash.HasExtractions(_outputDir))
            {
                if (Gui.IconTextButton(FontAwesomeIcon.BoxOpen, "Open the stash", "Every package extracted into the output folder, ready to drag from."))
                    _showStash = true;
            }
        }
    }

    private void DrawStatus(bool busy)
    {
        if (busy)
        {
            Gui.Icon(FontAwesomeIcon.Spinner, Theme.Accent);
            ImGui.SameLine();
            ImGui.TextColored(Theme.Accent, _progress);
            ImGui.SameLine();
            if (ImGui.SmallButton("Stop##PackageStop"))
                StopWork();
        }
        else if (!string.IsNullOrEmpty(_errorMessage))
        {
            Gui.Icon(FontAwesomeIcon.TimesCircle, Theme.Bad);
            ImGui.SameLine();
            ImGui.TextColored(Theme.Bad, _errorMessage);
        }
        else if (!string.IsNullOrEmpty(_status))
        {
            Gui.Hint(_status);
        }
    }

    /// <summary>
    /// Sims: the detail-level picker and half-crop toggle. The LOD dropdown only re-ticks the mesh
    /// rows, so an individual tick changed afterwards is still respected. Unity: a bulk tick.
    /// </summary>
    private void DrawOptions(bool busy)
    {
        Gui.SectionHeader("2  Choose what to extract");
        if (_unityScan != null)
        {
            using (Gui.Disabled(busy))
            {
                if (Gui.LabeledCombo("Tick", "##UnityTick", ref _unityTick, UnityTickOptions,
                        "Ticks rows in bulk; single rows can still be changed below. Materials and prefabs are read to sort the textures, never written.", Theme.S(220)))
                    ApplyUnityTick(_unityScan);
            }
            ImGui.Spacing();
            return;
        }
        if (_scan == null)
            return;

        using (Gui.Disabled(busy))
        {
            if (_scan.AvailableLods.Count > 0)
            {
                if (Gui.LabeledCombo("Detail", "##SimsLod", ref _lodChoice, _lodOptions,
                        "Which detail levels to export. LOD 0 is the full-quality mesh and the only one a mod needs, so it is " +
                        "selected by default; the lower levels are the game's reduced-detail copies. Individual meshes can still be " +
                        "ticked or unticked below.", Theme.S(220)))
                    ApplyLodChoice(_scan);
                ImGui.SameLine();
                Gui.Hint($"{_scan.Meshes.Count()} mesh(es) in this package");
            }

            DrawCropRow(_scan);
        }

        if (!_scan.HasCasPartData)
        {
            Gui.Icon(FontAwesomeIcon.ExclamationTriangle, Theme.Warn);
            ImGui.SameLine();
            ImGui.TextColored(Theme.Warn, "No CAS part could be read, so texture roles are guesses from the resource type. Check them.");
        }
        ImGui.Spacing();
    }

    /// <summary>
    /// The half-crop toggle. It is only offered when the meshes actually fit in one half
    /// of the sheet, since that is what makes rescaling UV0 safe; otherwise the reason is
    /// shown instead of a control that could not do anything.
    /// </summary>
    private void DrawCropRow(SimsScanResult scan)
    {
        var crop = scan.SuggestedCrop;
        Gui.Label("Half crop");
        if (!crop.Crops)
        {
            Gui.Hint("not available: the meshes use both halves of the texture sheet");
            Gui.Tooltip("Sims sheets are often twice as tall as they need to be, with one half empty. " +
                       "That can only be cropped away when every mesh sits inside a single half.");
            return;
        }

        ImGui.Checkbox($"Crop textures to the {crop.Describe()}##SimsCrop", ref _cropHalf);
        Gui.Tooltip($"The meshes only use the {crop.Describe()} of the sheet, so the rest is dead space. " +
                   "Halving it turns a 2048x4096 diffuse into a square 2048x2048, and UV0 on the " +
                   "exported model is rescaled by the same amount so the two still line up.");
        ImGui.SameLine();
        Gui.Hint("(UV0 is rescaled to match)");
    }

    /// <summary>
    /// Rebuilds the picker for whatever levels the scan actually found and points it at
    /// the selection the scan already made: LOD 0 where there is one, everything
    /// otherwise. The scan owns that decision; this only reflects it, so the dropdown
    /// and the ticks cannot disagree on the first frame.
    /// </summary>
    private void BuildLodOptions(SimsScanResult scan)
    {
        var options = new List<string> { AllLodsLabel };
        foreach (var level in scan.AvailableLods)
        {
            options.Add(level switch
            {
                SimsAsset.UnknownLod => "Unknown level",
                0                    => "LOD 0 (highest detail)",
                _                    => $"LOD {level}",
            });
        }

        _lodOptions = options.ToArray();

        int lod0 = scan.AvailableLods.IndexOf(0);
        _lodChoice = lod0 >= 0 ? lod0 + 1 : 0;      // +1 to step over the "All LODs" entry
    }

    private void ApplyLodChoice(SimsScanResult scan)
    {
        // Choice 0 is "All"; the rest line up with AvailableLods, offset by that entry.
        int? wanted = _lodChoice > 0 && _lodChoice - 1 < scan.AvailableLods.Count
            ? scan.AvailableLods[_lodChoice - 1]
            : null;

        foreach (var asset in scan.Meshes)
            asset.Selected = wanted == null || asset.LodLevel == wanted;
    }

    private void ApplyUnityTick(UnityScanResult scan)
    {
        foreach (var asset in scan.Assets.Where(a => a.Extractable))
            asset.Selected = _unityTick switch
            {
                0 => true,
                1 => asset.Kind == UnityAssetKind.Texture,
                2 => asset.Kind == UnityAssetKind.Model,
                _ => false,
            };
    }

    private void DrawAssetList(bool busy)
    {
        if (_scan == null)
        {
            ImGui.Spacing();
            Gui.Icon(FontAwesomeIcon.Box, Theme.Muted);
            ImGui.SameLine();
            Gui.Hint("Pick or drop a .package or .unitypackage file and hit Scan.");
            return;
        }

        if (_scan.Assets.Count == 0)
        {
            Gui.Hint("(nothing extractable was found in this package)");
            return;
        }

        using (Gui.Disabled(busy))
        {
            if (!ImGui.BeginTable("##SimsAssetTable", 6,
                    ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.PadOuterX,
                    new Vector2(-1, -1)))
                return;

            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("",         ImGuiTableColumnFlags.WidthFixed, Theme.S(26));
            ImGui.TableSetupColumn("Preview",  ImGuiTableColumnFlags.WidthFixed, Theme.S(64));
            ImGui.TableSetupColumn("Resource", ImGuiTableColumnFlags.WidthFixed, Theme.S(120));
            ImGui.TableSetupColumn("Role",     ImGuiTableColumnFlags.WidthFixed, Theme.S(130));
            ImGui.TableSetupColumn("Size",     ImGuiTableColumnFlags.WidthFixed, Theme.S(140));
            ImGui.TableSetupColumn("CAS part", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();

            for (int i = 0; i < _scan.Assets.Count; i++)
                DrawAssetRow(_scan.Assets[i], i);

            ImGui.EndTable();
        }
    }

    private void DrawAssetRow(SimsAsset asset, int index)
    {
        ImGui.TableNextRow();
        ImGui.PushID(index);

        // Tick
        ImGui.TableNextColumn();
        bool selected = asset.Selected;
        using (Gui.Disabled(asset.Error != null))
        {
            if (ImGui.Checkbox("##Sel", ref selected))
                asset.Selected = selected;
        }

        // Preview
        ImGui.TableNextColumn();
        if (asset.ThumbnailPng != null)
            _thumbnails.DrawPng(asset.Entry.KeyString, asset.ThumbnailPng, Theme.S(56));
        else if (asset.Kind == SimsAssetKind.Mesh)
            Gui.Icon(FontAwesomeIcon.Cube, Theme.Muted);
        else
            Gui.Hint("—");

        // Resource type
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(asset.Kind == SimsAssetKind.Mesh
            ? $"GEOM ({asset.LodLabel})"
            : SimsResourceType.Name(asset.Entry.Type));
        Gui.Tooltip(asset.Entry.KeyString);

        // Role
        ImGui.TableNextColumn();
        if (asset.Kind == SimsAssetKind.Mesh)
        {
            Gui.Hint("model");
            if (asset.LodLevel == SimsAsset.UnknownLod)
                Gui.Tooltip("No CAS part lists this mesh, so its detail level could not be established.");
        }
        else
        {
            ImGui.SetNextItemWidth(-1);
            int roleIndex = (int)asset.Role;
            if (ImGui.Combo("##Role", ref roleIndex, RoleLabels, RoleLabels.Length))
                asset.Role = (SimsTextureRole)roleIndex;
            Gui.Tooltip(asset.RoleFromCasPart
                ? "Role taken from the CAS part that references this texture."
                : "No CAS part referenced this texture, so the role was guessed from its resource type. Change it if the preview says otherwise.");
        }

        // Size / counts
        ImGui.TableNextColumn();
        if (asset.Error != null)
        {
            ImGui.TextColored(Theme.Bad, "failed");
            Gui.Tooltip(asset.Error);
        }
        else
        {
            Gui.Hint(asset.Describe());
            if (asset.Kind == SimsAssetKind.Texture)
                Gui.Tooltip(asset.SourceFormat);
        }

        // CAS part
        ImGui.TableNextColumn();
        Gui.Hint(string.IsNullOrWhiteSpace(asset.PartName) ? "—" : asset.PartName);

        ImGui.PopID();
    }

    private void DrawUnityAssets(bool busy, UnityScanResult scan)
    {
        if (scan.Assets.Count == 0)
        {
            Gui.Hint("(this package holds no assets)");
            return;
        }

        using (Gui.Disabled(busy))
        {
            if (!ImGui.BeginTable("##UnityAssetTable", 5,
                    ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.PadOuterX,
                    new Vector2(-1, -1)))
                return;

            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("",        ImGuiTableColumnFlags.WidthFixed, Theme.S(26));
            ImGui.TableSetupColumn("Preview", ImGuiTableColumnFlags.WidthFixed, Theme.S(64));
            ImGui.TableSetupColumn("Kind",    ImGuiTableColumnFlags.WidthFixed, Theme.S(90));
            ImGui.TableSetupColumn("Size",    ImGuiTableColumnFlags.WidthFixed, Theme.S(90));
            ImGui.TableSetupColumn("Path",    ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();

            var clipper = ImGui.ImGuiListClipper();
            try
            {
                clipper.Begin(scan.Assets.Count, Theme.S(60));
                while (clipper.Step())
                    for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                        DrawUnityRow(scan.Assets[i], i);
                clipper.End();
            }
            finally
            {
                clipper.Destroy();
            }
            ImGui.EndTable();
        }
    }

    private void DrawUnityRow(UnityAsset asset, int index)
    {
        ImGui.TableNextRow(ImGuiTableRowFlags.None, Theme.S(60));
        ImGui.PushID(index);

        ImGui.TableNextColumn();
        using (Gui.Disabled(!asset.Extractable))
        {
            bool selected = asset.Selected && asset.Extractable;
            if (ImGui.Checkbox("##Sel", ref selected))
                asset.Selected = selected;
        }
        if (!asset.Extractable)
            Gui.Tooltip(asset.Kind is UnityAssetKind.Material or UnityAssetKind.Prefab
                ? "Read to sort the textures, not written."
                : "Not something the game or the build window can use.");

        ImGui.TableNextColumn();
        if (asset.PreviewPng != null)
            _thumbnails.DrawPng($"unity:{asset.Guid}", asset.PreviewPng, Theme.S(56));
        else
            Gui.Icon(asset.Kind == UnityAssetKind.Model ? FontAwesomeIcon.Cube : FontAwesomeIcon.File, Theme.Muted);

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(asset.Kind switch
        {
            UnityAssetKind.Texture   => asset.TextureType == 1 ? "normal map" : "texture",
            UnityAssetKind.Model     => "model",
            UnityAssetKind.Material  => "material",
            UnityAssetKind.Prefab    => "prefab",
            UnityAssetKind.Animation => "animation",
            _                        => "other",
        });

        ImGui.TableNextColumn();
        Gui.Hint(FormatSize(asset.Size));

        ImGui.TableNextColumn();
        ImGui.TextColored(asset.Extractable ? Theme.Muted : Theme.Faint, asset.PackagePath);

        ImGui.PopID();
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.0} MB",
        >= 1024        => $"{bytes / 1024.0:0} KB",
        _              => $"{bytes} B",
    };

    private void DrawActions(bool busy)
    {
        int selected = _unityScan != null
            ? _unityScan.Assets.Count(a => a.Selected && a.Extractable)
            : _scan?.Assets.Count(a => a.Selected) ?? 0;
        int total = _unityScan?.Assets.Count(a => a.Extractable) ?? _scan?.Assets.Count ?? 0;

        using (Gui.Disabled(busy || selected == 0))
        {
            if (Gui.PrimaryButton(FontAwesomeIcon.FileExport, "Extract", Theme.S(140), "Writes the ticked files into the output folder, then opens the stash."))
                StartExtract();
        }

        if (_scan != null || _unityScan != null)
        {
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            Gui.Hint($"{selected} of {total} selected");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Work
    // ─────────────────────────────────────────────────────────────────────────

    private void SaveSettings()
    {
        var cfg = _host.Configuration;
        cfg.LastSimsPackagePath = _packagePath;
        cfg.LastSimsOutputDir   = _outputDir;
        cfg.Save();
    }

    private void StartScan()
    {
        if (string.IsNullOrWhiteSpace(_packagePath) || !File.Exists(_packagePath) || !IsPackage(_packagePath))
        {
            _errorMessage = "Pick a .package or .unitypackage file first.";
            return;
        }

        SaveSettings();

        _thumbnails.ForgetMemoryEntries();
        _scan         = null;
        _unityScan    = null;
        _extraction   = null;
        _errorMessage = string.Empty;
        _applyMessage = string.Empty;
        _status       = string.Empty;
        _progress     = "Opening package";
        _showStash    = false;

        var path = _packagePath;
        var cancel = NewWork();
        if (IsUnity(path))
            _unityScanTask = Task.Run(() => UnityPackageReader.Scan(path, s => _progress = s, cancel), cancel);
        else
            _scanTask = Task.Run(() => SimsPackageInspector.Scan(path, s => _progress = s, cancel), cancel);
    }

    private void StartExtract()
    {
        if (string.IsNullOrWhiteSpace(_outputDir))
        {
            _errorMessage = "Pick an output folder first.";
            return;
        }

        SaveSettings();

        _extraction   = null;
        _errorMessage = string.Empty;
        _applyMessage = string.Empty;
        _status       = string.Empty;
        _progress     = "Extracting";

        var outputDir = _outputDir;
        var cancel = NewWork();
        if (_unityScan is { } unity)
        {
            _unityExtractTask = Task.Run(() => UnityPackageReader.Extract(unity, outputDir, s => _progress = s, cancel), cancel);
        }
        else if (_scan is { } scan)
        {
            var crop = _cropHalf ? scan.SuggestedCrop : SimsHalfCrop.None;
            _extractTask = Task.Run(() => SimsPackageInspector.Extract(scan, outputDir, crop, s => _progress = s, cancel), cancel);
        }
    }

    private static bool Stopped(Task task)
        => task.IsCanceled || task.Exception?.GetBaseException() is OperationCanceledException;

    /// <summary>Applies a finished scan or extraction on the render thread.</summary>
    private void CollectFinishedWork()
    {
        if (_scanTask is { IsCompleted: true } scanTask)
        {
            _scanTask = null;
            if (Stopped(scanTask))
                _status = "Stopped.";
            else if (scanTask.IsCompletedSuccessfully)
            {
                var scan = scanTask.Result;
                BuildLodOptions(scan);
                _scan   = scan;
                _status = $"Found {scan.Meshes.Count()} mesh(es) and {scan.Textures.Count()} texture(s).";
                if (scan.Warnings.Count > 0)
                    StudioServices.Log.Information("[Studio] Sims scan warnings for {0}:\n  {1}", scan.PackagePath, string.Join("\n  ", scan.Warnings));
            }
            else
            {
                var ex = scanTask.Exception?.GetBaseException();
                StudioServices.Log.Warning(ex, "[Studio] Failed to scan Sims package {0}", _packagePath);
                _errorMessage = $"Could not read this package: {ex?.Message}";
            }
        }

        if (_unityScanTask is { IsCompleted: true } unityScanTask)
        {
            _unityScanTask = null;
            if (Stopped(unityScanTask))
                _status = "Stopped.";
            else if (unityScanTask.IsCompletedSuccessfully)
            {
                var scan = unityScanTask.Result;
                _unityScan = scan;
                _unityTick = 0;
                _status = $"Found {scan.Assets.Count(a => a.Kind == UnityAssetKind.Texture)} texture(s), " +
                          $"{scan.Assets.Count(a => a.Kind == UnityAssetKind.Model)} model(s) and {scan.Materials.Count} material(s).";
                if (scan.Warnings.Count > 0)
                    StudioServices.Log.Information("[Studio] Unity scan warnings for {0}:\n  {1}", scan.PackagePath, string.Join("\n  ", scan.Warnings));
            }
            else
            {
                var ex = unityScanTask.Exception?.GetBaseException();
                StudioServices.Log.Warning(ex, "[Studio] Failed to scan Unity package {0}", _packagePath);
                _errorMessage = $"Could not read this package: {ex?.Message}";
            }
        }

        if (_extractTask is { IsCompleted: true } extractTask)
        {
            _extractTask = null;
            if (Stopped(extractTask))
                _status = "Stopped.";
            else if (extractTask.IsCompletedSuccessfully)
            {
                var result = extractTask.Result;
                _extraction = result;
                _status     = $"Done: {result.Extracted} file(s) written, {result.Failed} failed.";
                StudioServices.Log.Information("[Studio] Sims extraction: {0} written, {1} failed, into {2}",
                    result.Extracted, result.Failed, result.RootFolder);
                OpenStash(result.RootFolder);
            }
            else
            {
                var ex = extractTask.Exception?.GetBaseException();
                StudioServices.Log.Warning(ex, "[Studio] Failed to extract Sims package into {0}", _outputDir);
                _errorMessage = $"Extraction failed: {ex?.Message}";
            }
        }

        if (_unityExtractTask is { IsCompleted: true } unityExtractTask)
        {
            _unityExtractTask = null;
            if (Stopped(unityExtractTask))
                _status = "Stopped.";
            else if (unityExtractTask.IsCompletedSuccessfully)
            {
                var result = unityExtractTask.Result;
                _status = $"Done: {result.WrittenFiles.Count} file(s) written, {result.Failed} failed, {result.Refused.Count} refused.";
                StudioServices.Log.Information("[Studio] Unity extraction: {0} written, {1} failed, {2} refused, into {3}",
                    result.WrittenFiles.Count, result.Failed, result.Refused.Count, result.RootFolder);
                OpenStash(result.RootFolder);
            }
            else
            {
                var ex = unityExtractTask.Exception?.GetBaseException();
                StudioServices.Log.Warning(ex, "[Studio] Failed to extract Unity package into {0}", _outputDir);
                _errorMessage = $"Extraction failed: {ex?.Message}";
            }
        }
    }

    private void OpenStash(string folder)
    {
        _stash.Show(folder);
        _showStash = true;
    }
}
