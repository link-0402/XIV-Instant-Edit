using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using InstantEdit.Studio.Windows.UI;

namespace InstantEdit.Studio.Windows;

/// <summary>
/// Imports a Sims 4 .package: lists the meshes and textures it carries, lets the user
/// choose which detail levels to take and untick anything unwanted, then extracts the
/// rest into the folder layout the material editor consumes — a folder of diffuse
/// swatches to point a variant slot at, and single files for the other roles. Every
/// extracted file and folder can then be dragged onto a texture or model input of Build
/// mode, or onto a tile in Edit mode.
///
/// Scanning and extraction both run off the render thread. A package can run to
/// hundreds of megabytes, and decoding its textures on the draw call would stall the
/// game. The worker only returns its result and publishes a progress line; everything
/// else is applied here on the render thread once the task has finished.
/// </summary>
public sealed class SimsImportWindow : Window, IDisposable
{
    private readonly StudioHost _host;
    private readonly TextureThumbnailCache _thumbnails;

    private string _packagePath = string.Empty;
    private string _outputDir   = string.Empty;

    private SimsScanResult? _scan;
    private SimsPackageInspector.ExtractionResult? _extraction;

    /// <summary>One draggable entry of the extraction result list.</summary>
    private sealed record ResultRow(string Label, string Detail, DroppedPaths Paths, string? Preview, bool PreviewIsFolder,
        bool IsModel, bool NeedsConversion, bool Indented);

    private List<ResultRow> _results = new();

    /// <summary>Whether to halve textures whose used content sits in one half.</summary>
    private bool _cropHalf = true;

    /// <summary>Index into <see cref="_lodOptions"/>; 0 is "All LODs".</summary>
    private int _lodChoice;
    private string[] _lodOptions = { AllLodsLabel };
    private const string AllLodsLabel = "All LODs";

    private Task<SimsScanResult>? _scanTask;
    private Task<SimsPackageInspector.ExtractionResult>? _extractTask;

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

    internal SimsImportWindow(StudioHost host, TextureThumbnailCache thumbnails) : base("Import Sims 4 Package###StudioSimsImport")
    {
        _host     = host;
        _thumbnails = thumbnails;
        Size          = new Vector2(900, 660);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(620, 420) };

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

    /// <summary>Closed: what runs is stopped, and whatever it would still have found is dropped.</summary>
    public override void OnClose() => StopWork();

    private void StopWork()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        if (Busy)
            _status = "Stopped.";
        _scanTask    = null;
        _extractTask = null;
    }

    /// <summary>A token for new work; whatever ran before is stopped first.</summary>
    private CancellationToken NewWork()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        return _cts.Token;
    }

    private bool Busy => _scanTask is { IsCompleted: false } || _extractTask is { IsCompleted: false };

    // ─────────────────────────────────────────────────────────────────────────
    // Draw
    // ─────────────────────────────────────────────────────────────────────────

    public override void Draw()
    {
        CollectFinishedWork();
        bool busy = Busy;

        DrawSource(busy);
        DrawStatus(busy);
        ImGui.Spacing();

        DrawOptions(busy);

        float footerH = _extraction != null
            ? ImGui.GetFrameHeightWithSpacing() * 6 + ResultListHeight
            : ImGui.GetFrameHeightWithSpacing() * 1.5f;
        if (ImGui.BeginChild("##SimsAssets", new Vector2(-1, -footerH), true))
            DrawAssetList(busy);
        ImGui.EndChild();

        DrawActions(busy);
    }

    private void DrawSource(bool busy)
    {
        Gui.SectionHeader("1  Package", "Pick a .package and scan it. Nothing is written until you extract.");

        using (Gui.Disabled(busy))
        {
            // Typed paths are only kept in memory; they are persisted when a scan starts,
            // so an in-progress path does not write the config file on every keystroke.
            Gui.Label("Package");
            float scanW = Theme.S(110);
            Gui.PathPicker("SimsPackage", ref _packagePath, "path to a .package file", PathKind.File, "Sims 4 packages{.package}",
                picked => { _packagePath = picked; SaveSettings(); }, reserveRight: scanW + ImGui.GetStyle().ItemSpacing.X);

            ImGui.SameLine();
            if (Gui.PrimaryButton(FontAwesomeIcon.Search, "Scan", scanW, "Reads the package and lists its meshes and textures."))
                StartScan();

            Gui.Label("Output", "A subfolder named after the package is created here, holding model/ and textures/.");
            Gui.PathPicker("SimsOutput", ref _outputDir, "folder the extracted files go into", PathKind.Folder, "",
                picked => { _outputDir = picked; SaveSettings(); },
                "A subfolder named after the package is created here, holding model/ and textures/.");
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
            if (ImGui.SmallButton("Stop##SimsStop"))
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
    /// Detail-level picker and half-crop toggle. The LOD dropdown only re-ticks the mesh
    /// rows, so an individual tick changed afterwards is still respected — it is a bulk
    /// selection, not a filter that hides anything.
    /// </summary>
    private void DrawOptions(bool busy)
    {
        Gui.SectionHeader("2  Choose what to extract");
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
            ImGui.TextColored(Theme.Warn, "No CAS part could be read, so texture roles are guesses from the resource type — check them.");
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
            Gui.Hint("not available — the meshes use both halves of the texture sheet");
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
    /// the selection the scan already made — LOD 0 where there is one, everything
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

    private void DrawAssetList(bool busy)
    {
        if (_scan == null)
        {
            ImGui.Spacing();
            Gui.Icon(FontAwesomeIcon.Box, Theme.Muted);
            ImGui.SameLine();
            Gui.Hint("Pick a .package file and hit Scan.");
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
                : "No CAS part referenced this texture — the role was guessed from its resource type. Change it if the preview says otherwise.");
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

    private void DrawActions(bool busy)
    {
        int selected = _scan?.Assets.Count(a => a.Selected) ?? 0;

        using (Gui.Disabled(busy || _scan == null || selected == 0))
        {
            if (Gui.PrimaryButton(FontAwesomeIcon.FileExport, "Extract", Theme.S(140), "Writes the ticked assets into the output folder."))
                StartExtract();
        }

        if (_scan != null)
        {
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            Gui.Hint($"{selected} of {_scan.Assets.Count} selected");
        }

        if (_extraction == null)
            return;

        ImGui.Spacing();
        Gui.SectionHeader("3  Use the result");

        bool clean = _extraction.Failed == 0;
        Gui.Icon(clean ? FontAwesomeIcon.CheckCircle : FontAwesomeIcon.ExclamationTriangle, clean ? Theme.Good : Theme.Bad);
        ImGui.SameLine();
        ImGui.TextColored(clean ? Theme.Good : Theme.Bad, $"{_extraction.Extracted} file(s) written, {_extraction.Failed} failed.");
        if (_extraction.Crop.Crops)
        {
            ImGui.SameLine();
            Gui.Hint($"· cropped to the {_extraction.Crop.Describe()}, UV0 rescaled to match");
        }

        Gui.LabeledValue("Folder", _extraction.RootFolder, "Also holds extract-report.txt with the full log of this run.");

        ImGui.Spacing();
        Gui.Icon(FontAwesomeIcon.HandPointer, Theme.Accent);
        ImGui.SameLine();
        ImGui.TextColored(Theme.Accent, "Drag any row onto a texture or model tile in the build window's Set-up, or in a mod window.");
        DrawResultList();

        bool canApply = _extraction.DiffuseCount > 0 || _extraction.NormalFile != null || _extraction.SpecularFile != null;
        var target = _host.Pack.CurrentMaterial;
        using (Gui.Disabled(!canApply || target == null))
        {
            var label = target != null ? $"Apply all to \"{target.Name}\"" : "Apply all to selected material";
            if (Gui.IconTextButton(FontAwesomeIcon.ArrowRight, label,
                    target == null
                        ? "Select a material in the build window's Set-up view first."
                        : "Shortcut: points that material at these files — the diffuse folder as a variant group, and the normal / specular images as single textures."))
            {
                _applyMessage = _host.Pack.ApplySimsExtraction(_extraction);
                _host.OpenBuild();
                _host.Pack.GoToStage(Models.StageId.Details);
            }
        }

        if (!string.IsNullOrEmpty(_applyMessage))
        {
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            Gui.Hint(_applyMessage);
        }
    }

    private static float ResultListHeight => Theme.S(200);

    /// <summary>
    /// Everything the extraction wrote, one draggable row each: the diffuse folder (drops as a
    /// variant folder) and its swatches, the other textures, and the model files.
    /// </summary>
    private void DrawResultList()
    {
        if (!ImGui.BeginTable("##SimsResults", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY
                                                 | ImGuiTableFlags.PadOuterX, new Vector2(-1, ResultListHeight)))
            return;

        float thumb = ImGui.GetFrameHeight() * 1.4f;
        ImGui.TableSetupColumn("##thumb", ImGuiTableColumnFlags.WidthFixed, thumb);
        ImGui.TableSetupColumn("##name",  ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("##act",   ImGuiTableColumnFlags.WidthFixed, ImGui.GetFrameHeight());

        if (_results.Count == 0)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TableNextColumn();
            Gui.Hint("(nothing usable was written)");
        }

        for (int i = 0; i < _results.Count; i++)
        {
            var row = _results[i];
            ImGui.PushID(i);
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            if (row.Preview != null)
                _thumbnails.Draw(new Models.TextureSlot { SourcePath = row.Preview, UseVariants = row.PreviewIsFolder }, thumb);
            else
                Gui.Icon(row.IsModel ? FontAwesomeIcon.Cube : FontAwesomeIcon.File, row.NeedsConversion ? Theme.Warn : Theme.Muted);

            ImGui.TableNextColumn();
            if (row.Indented) ImGui.Indent(Theme.S(14));
            ImGui.Selectable($"{row.Label}##row", false, ImGuiSelectableFlags.None, new Vector2(0, 0));
            FileDrop.Source(row.Paths, row.Label);
            if (ImGui.IsItemHovered() && !ImGui.IsMouseDragging(ImGuiMouseButton.Left))
                ImGui.SetTooltip($"{string.Join("\n", row.Paths.Folders.Concat(row.Paths.Files))}\n\nDrag onto an input in the main window.");
            ImGui.SameLine();
            ImGui.TextColored(row.NeedsConversion ? Theme.Warn : Theme.Faint, row.Detail);
            if (row.Indented) ImGui.Unindent(Theme.S(14));

            ImGui.TableNextColumn();
            if (Gui.IconButton(FontAwesomeIcon.FolderOpen, "reveal", "Show in Explorer"))
                Reveal(row.Paths.Folders.Concat(row.Paths.Files).First());

            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private static List<ResultRow> BuildResultRows(SimsPackageInspector.ExtractionResult result)
    {
        var rows = new List<ResultRow>();
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Only what this run wrote; the folders are never listed, so nothing else in them shows up.
        var written = result.WrittenFiles.Where(File.Exists).ToList();
        IEnumerable<string> In(string folder) => written.Where(f =>
            string.Equals(Path.GetDirectoryName(f), folder, StringComparison.OrdinalIgnoreCase));

        var swatches = In(result.DiffuseFolder).Where(FileDrop.IsImage).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        if (swatches.Count > 0)
        {
            rows.Add(new ResultRow("Diffuse swatches", $"folder · {swatches.Count} image(s) → a variant group",
                DroppedPaths.Folder(result.DiffuseFolder), result.DiffuseFolder, true, false, false, false));
            foreach (var file in swatches)
            {
                listed.Add(file);
                rows.Add(new ResultRow(Path.GetFileName(file), "one swatch", DroppedPaths.File(file), file, false, false, false, true));
            }
        }

        void AddTexture(string? file, string role)
        {
            if (file == null || !File.Exists(file) || !listed.Add(file)) return;
            rows.Add(new ResultRow(Path.GetFileName(file), role, DroppedPaths.File(file), file, false, false, false, false));
        }

        AddTexture(result.NormalFile, "normal");
        AddTexture(result.SpecularFile, "specular");

        var texturesRoot = Path.Combine(result.RootFolder, "textures") + Path.DirectorySeparatorChar;
        foreach (var file in written.Where(f => f.StartsWith(texturesRoot, StringComparison.OrdinalIgnoreCase))
                     .Where(FileDrop.IsImage).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            AddTexture(file, Path.GetFileName(Path.GetDirectoryName(file)) ?? "texture");

        foreach (var file in In(result.ModelFolder).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            bool mdl = FileDrop.IsModel(file);
            bool mesh = mdl || Path.GetExtension(file).ToLowerInvariant() is ".glb" or ".gltf" or ".fbx" or ".obj";
            if (!mesh) continue;
            rows.Add(new ResultRow(Path.GetFileName(file),
                mdl ? "model" : "needs converting to .mdl in Blender first",
                DroppedPaths.File(file), null, false, true, !mdl, false));
        }

        return rows;
    }

    private static void Reveal(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", Directory.Exists(path) ? $"\"{path}\"" : $"/select,\"{path}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            StudioServices.Log.Warning(ex, "[Studio] Could not open Explorer at {0}", path);
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
        if (string.IsNullOrWhiteSpace(_packagePath) || !File.Exists(_packagePath))
        {
            _errorMessage = "Pick a .package file first.";
            return;
        }

        SaveSettings();

        _thumbnails.ForgetMemoryEntries();
        _scan         = null;
        _extraction   = null;
        _errorMessage = string.Empty;
        _applyMessage = string.Empty;
        _status       = string.Empty;
        _progress     = "Opening package";

        var path = _packagePath;
        var cancel = NewWork();
        _scanTask = Task.Run(() => SimsPackageInspector.Scan(path, s => _progress = s, cancel), cancel);
    }

    private void StartExtract()
    {
        var scan = _scan;
        if (scan == null)
            return;

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
        var crop = _cropHalf ? scan.SuggestedCrop : SimsHalfCrop.None;
        var cancel = NewWork();
        _extractTask = Task.Run(() => SimsPackageInspector.Extract(scan, outputDir, crop, s => _progress = s, cancel), cancel);
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

        if (_extractTask is { IsCompleted: true } extractTask)
        {
            _extractTask = null;
            if (Stopped(extractTask))
                _status = "Stopped.";
            else if (extractTask.IsCompletedSuccessfully)
            {
                var result = extractTask.Result;
                _extraction = result;
                _results    = BuildResultRows(result);
                _status     = $"Done — {result.Extracted} file(s) written to {result.RootFolder}";
                StudioServices.Log.Information("[Studio] Sims extraction: {0} written, {1} failed, into {2}",
                    result.Extracted, result.Failed, result.RootFolder);
            }
            else
            {
                var ex = extractTask.Exception?.GetBaseException();
                StudioServices.Log.Warning(ex, "[Studio] Failed to extract Sims package into {0}", _outputDir);
                _errorMessage = $"Extraction failed: {ex?.Message}";
            }
        }
    }
}
