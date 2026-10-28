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
using ComboWheel = InstantEdit.Ui.ComboWheel;

namespace InstantEdit.Studio.Windows;

/// <summary>
/// Imports a Sims 4 .package or a Unity .unitypackage, then serves as the file stash. Folders of
/// loose files (a VRChat avatar's resources, say) can be opened in the stash too, without importing.
///
/// A scan lists what the package holds; the user unticks what they don't want and extracts the
/// rest into a folder of its own under the output folder (the cache's package-imports by
/// default). Several packages of one kind can be listed together; they then share that folder, so a
/// model and textures shipped apart can be matched. A Sims package comes out in the layout the material editor consumes (a folder of
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

    // Several packages of one kind can be listed at once and are extracted together, into one folder:
    // _scans/_unityScans hold each package's scan, _scan/_unityScan all of them as one list for the table.

    // ── Sims ─────────────────────────────────────────────────────────────────
    private readonly List<SimsScanResult> _scans = new();
    private SimsScanResult? _scan;
    private SimsPackageInspector.ExtractionResult? _extraction;

    private Task<List<SimsScanResult>>? _scanTask;
    private Task<SimsPackageInspector.ExtractionResult>? _extractTask;

    // ── Unity ────────────────────────────────────────────────────────────────
    private readonly List<UnityScanResult> _unityScans = new();
    private UnityScanResult? _unityScan;
    private Task<List<UnityScanResult>>? _unityScanTask;
    private Task<UnityExtraction>? _unityExtractTask;

    /// <summary>The package file each listed asset came from, when more than one package is listed.</summary>
    private readonly Dictionary<object, string> _packageOf = new(ReferenceEqualityComparer.Instance);

    /// <summary>Stops the scan or extraction under way: the Stop button, closing the window, or the plugin going away.</summary>
    private CancellationTokenSource? _cts;

    /// <summary>The one field the worker writes: its latest progress line.</summary>
    private volatile string _progress = string.Empty;

    private string _status       = string.Empty;
    private string _errorMessage = string.Empty;
    private string _applyMessage = string.Empty;

    /// <summary>The last extraction's result, shown above the stash it opened; a problem when files failed or were refused.</summary>
    private string _extractNote = string.Empty;
    private bool _extractNoteIsProblem;

    private static readonly string[] RoleLabels =
    {
        "Unknown", "Diffuse", "Normal", "Specular", "Shadow", "Emission", "Region map",
    };

    private static readonly string[] UnityTickOptions = { "Textures and models", "Textures only", "Models only", "Nothing" };
    private int _unityTick;

    /// <summary>Rows picked in the list (by index), so a tick on one of them ticks them all; cleared by every scan.</summary>
    private readonly HashSet<int> _picked = new();

    /// <summary>The row a Shift-click picks from: the last one clicked without Shift.</summary>
    private int _pickAnchor = -1;

    private static float RowHeight => Theme.S(60);

    internal PackageImportWindow(StudioHost host, TextureThumbnailCache thumbnails) : base("Import Package###StudioSimsImport")
    {
        _host       = host;
        _thumbnails = thumbnails;
        Size          = new Vector2(960, 700);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(680, 440) };

        var cfg = host.Configuration;
        _packagePath = cfg.LastSimsPackagePath;
        // A cache that can't be set up (a non-empty folder it doesn't own) leaves the old default.
        var cache = Services.Backups.BackupFolder.TryResolve(() => host.CacheRoot, out _);
        var cacheRoot = cache ?? Path.Combine(Path.GetTempPath(), "InstantEdit");
        // Blender saves into the cache's package-imports, so the stash lists its folders there whatever output folder is picked.
        _stash       = new StashView(host, thumbnails, cache != null ? PackageImportPaths.DefaultOutput(cache) : null);
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
            _showStash = _stash.HasFolders(_outputDir);
    }

    /// <summary>Closed: what runs is stopped, and whatever it would still have found is dropped.</summary>
    public override void OnClose() => StopWork();

    private void StopWork()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        if (_extractTask is { IsCompleted: false } || _unityExtractTask is { IsCompleted: false })
            _status = "Stopped: nothing was kept, and an earlier extraction of this package stays as it was.";
        else if (Busy)
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

    private FileDropRule? _packageDropRule;

    /// <summary>
    /// What the package field, the list below it and the stash's import button take from a drag:
    /// packages of one kind, which are scanned at once and extracted together, or folders, which open in the stash.
    /// </summary>
    private FileDropRule PackageDrop => _packageDropRule ??= new FileDropRule(
        p => p.Files.Count > 0 && p.Folders.Count == 0 && p.Files.All(IsPackage)
               ? (p.Files.All(IsUnity) || !p.Files.Any(IsUnity) ? null : "Drop packages of one kind: all Sims packages or all Unity packages.")
           : p.Folders.Count > 0 && p.Files.Count == 0 ? null
           : "Drop .package or .unitypackage files, or folders to open in the stash.",
        dropped =>
        {
            if (dropped.Folders.Count > 0)
            {
                foreach (var folder in dropped.Folders)
                    if (_stash.OpenFolder(folder) == null)
                        _showStash = true;
            }
            else
            {
                StartScan(dropped.Files, add: false);
            }
        });

    /// <summary>A drop target on the last drawn item, taking packages and folders as the package field does.</summary>
    private void PackageDropTarget()
    {
        if (!Busy && FileDrop.Target(PackageDrop.Reject, out var dropped))
            PackageDrop.OnDrop(dropped);
    }

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
            if (!busy)
                PickAllOnShortcut(_unityScan?.Assets.Count ?? _scan?.Assets.Count ?? 0);
        }
        ImGui.EndChild();
        PackageDropTarget();

        DrawActions(busy);
    }

    /// <summary>The stash's top line: back to importing, and the Sims shortcut for the extraction just made.</summary>
    private void DrawStashBar()
    {
        if (Gui.IconTextButton(FontAwesomeIcon.FileImport, "Import a package", "Scan and extract another .package or .unitypackage. A package can also be dropped here."))
            _showStash = false;
        PackageDropTarget();

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
        if (!string.IsNullOrEmpty(_extractNote) && string.Equals(_stash.Folder, _extractNoteFolder, StringComparison.OrdinalIgnoreCase))
        {
            Gui.Icon(_extractNoteIsProblem ? FontAwesomeIcon.ExclamationTriangle : FontAwesomeIcon.CheckCircle, _extractNoteIsProblem ? Theme.Warn : Theme.Good);
            ImGui.SameLine();
            ImGui.TextColored(_extractNoteIsProblem ? Theme.Warn : Theme.Muted, _extractNote);
            if (_extractNoteIsProblem)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Open the report##ExtractReport"))
                    StashView.Reveal(Path.Combine(_extractNoteFolder, "extract-report.txt"));
            }
        }
        ImGui.Spacing();
    }

    private string _extractNoteFolder = string.Empty;

    /// <summary>Notes what an extraction did, for the stash bar.</summary>
    private void NoteExtraction(string folder, int written, int failed, int refused, int skipped = 0)
    {
        _extractNoteFolder = folder;
        _extractNoteIsProblem = failed + refused + skipped > 0;
        _extractNote = refused > 0
            ? $"{written} file(s) written, {failed} failed, {refused} refused: their paths lead outside the output folder."
            : $"{written} file(s) written, {failed} failed.";
        if (skipped > 0)
            _extractNote += $" {skipped} left out: an earlier package writes the same path.";
    }

    private void DrawSource(bool busy)
    {
        Gui.SectionHeader("1  Package", "Pick a Sims 4 .package or a Unity .unitypackage and scan it. Nothing is written until you extract. " +
            "Packages dropped together, or added to the list, are extracted into one folder.");

        using (Gui.Disabled(busy))
        {
            // Typed paths are only kept in memory; they are persisted when a scan starts,
            // so an in-progress path does not write the config file on every keystroke.
            Gui.Label("Package");
            float scanW = Theme.S(110);
            Gui.PathPicker("PackagePath", ref _packagePath, "path to a .package or .unitypackage file", PathKind.File,
                "Packages{.package,.unitypackage}",
                picked => { _packagePath = picked; SaveSettings(); }, reserveRight: scanW + ImGui.GetStyle().ItemSpacing.X,
                drop: PackageDrop);

            ImGui.SameLine();
            if (Gui.PrimaryButton(FontAwesomeIcon.Search, "Scan", scanW, "Reads the package and lists what it holds."))
                StartScan();

            int listed = _scans.Count + _unityScans.Count;
            if (listed > 1)
            {
                Gui.Hint($"{listed} packages listed, extracted together into one folder named after the first");
                Gui.Tooltip(string.Join("\n", _scans.Select(s => s.PackagePath).Concat(_unityScans.Select(s => s.PackagePath))));
            }

            Gui.Label("Output", "A subfolder named after the package is created here.");
            Gui.PathPicker("PackageOutput", ref _outputDir, "folder the extracted files go into", PathKind.Folder, "",
                picked => { _outputDir = picked; SaveSettings(); },
                "A subfolder named after the package is created here.");

            if (_stash.HasFolders(_outputDir))
            {
                if (Gui.IconTextButton(FontAwesomeIcon.BoxOpen, "Open the stash", "Every package extracted into the output folder, and every folder opened, ready to drag from."))
                    _showStash = true;
                ImGui.SameLine();
            }
            if (Gui.IconTextButton(FontAwesomeIcon.FolderPlus, "Open a folder",
                    "Show a folder of loose files in the stash, such as a VRChat avatar's textures, models and materials. Nothing is written into it."))
                _stash.BrowseFolder(() => _showStash = true);

            if (_scan != null || _unityScan != null)
            {
                ImGui.SameLine();
                if (Gui.IconTextButton(FontAwesomeIcon.Plus, "Add packages",
                        "Scan more packages of the same kind into this list. They are extracted together into one folder, so a model and its textures shipped in separate packages end up side by side."))
                    BrowseMorePackages();
            }
        }
    }

    private void BrowseMorePackages()
    {
        var filter = _unityScan != null ? "Unity packages{.unitypackage}" : "Sims packages{.package}";
        var start = Path.GetDirectoryName(_packagePath) is { } dir && Directory.Exists(dir) ? dir : string.Empty;
        Gui.Dialogs.OpenFileDialog("Add packages", filter, (ok, picked) =>
        {
            if (ok && picked.Count > 0 && !Busy)
                StartScan(picked, add: true);
        }, 64, start, false);
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
    /// Sims: the detail-level picker. The LOD dropdown only re-ticks the mesh
    /// rows, so an individual tick changed afterwards is still respected. Unity: a bulk tick.
    /// </summary>
    private void DrawOptions(bool busy)
    {
        Gui.SectionHeader("2  Choose what to extract",
            "Click rows to pick them, Ctrl+click or Shift+click to pick more, Ctrl+A to pick all. Ticking a picked row ticks every picked row.");
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

        if (_scan.IsHair)
            Gui.Hint("A hair: its hat versions and all diffuses but the lightest start unticked.");
        if (!_scan.HasCasPartData)
        {
            Gui.Icon(FontAwesomeIcon.ExclamationTriangle, Theme.Warn);
            ImGui.SameLine();
            ImGui.TextColored(Theme.Warn, "No CAS part could be read, so texture roles are guesses from the resource type. Check them.");
        }
        ImGui.Spacing();
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
                DrawAssetRow(_scan, _scan.Assets[i], i);

            ImGui.EndTable();
        }
    }

    private void DrawAssetRow(SimsScanResult scan, SimsAsset asset, int index)
    {
        ImGui.TableNextRow(ImGuiTableRowFlags.None, RowHeight);
        ImGui.PushID(index);

        // Tick
        ImGui.TableNextColumn();
        DrawRowPicker(index, scan.Assets.Count);
        bool selected = asset.Selected;
        using (Gui.Disabled(asset.Error != null))
        {
            if (ImGui.Checkbox("##Sel", ref selected))
                TickPicked(index, selected, scan.Assets.Count, i => scan.Assets[i].Error == null, (i, v) => scan.Assets[i].Selected = v);
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
        Gui.Tooltip(_packageOf.TryGetValue(asset, out var from) ? $"{asset.Entry.KeyString}\nFrom {from}" : asset.Entry.KeyString);

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
            if (ComboWheel.Combo("##Role", ref roleIndex, RoleLabels))
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
                        DrawUnityRow(scan, scan.Assets[i], i);
                clipper.End();
            }
            finally
            {
                clipper.Destroy();
            }
            ImGui.EndTable();
        }
    }

    private void DrawUnityRow(UnityScanResult scan, UnityAsset asset, int index)
    {
        ImGui.TableNextRow(ImGuiTableRowFlags.None, RowHeight);
        ImGui.PushID(index);

        ImGui.TableNextColumn();
        DrawRowPicker(index, scan.Assets.Count);
        using (Gui.Disabled(!asset.Extractable))
        {
            bool selected = asset.Selected && asset.Extractable;
            if (ImGui.Checkbox("##Sel", ref selected))
                TickPicked(index, selected, scan.Assets.Count, i => scan.Assets[i].Extractable, (i, v) => scan.Assets[i].Selected = v);
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
        if (_packageOf.TryGetValue(asset, out var from))
            Gui.Tooltip($"From {from}");

        ImGui.PopID();
    }

    /// <summary>
    /// The row's pick highlight, behind the whole row: a click picks the row alone, Ctrl+click adds or
    /// drops it, Shift+click picks the run from the last clicked row. Leaves the cursor where it was, so
    /// the tick box is drawn over it.
    /// </summary>
    private void DrawRowPicker(int index, int count)
    {
        var start = ImGui.GetCursorScreenPos();
        var height = RowHeight - ImGui.GetStyle().CellPadding.Y * 2;
        if (ImGui.Selectable("##Pick", _picked.Contains(index), ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowItemOverlap,
                new Vector2(0, height)))
        {
            var io = ImGui.GetIO();
            if (io.KeyShift && _pickAnchor >= 0 && _pickAnchor < count)
            {
                if (!io.KeyCtrl)
                    _picked.Clear();
                for (int i = Math.Min(_pickAnchor, index); i <= Math.Max(_pickAnchor, index); i++)
                    _picked.Add(i);
            }
            else
            {
                if (io.KeyCtrl)
                {
                    if (!_picked.Remove(index))
                        _picked.Add(index);
                }
                else
                {
                    _picked.Clear();
                    _picked.Add(index);
                }
                _pickAnchor = index;
            }
        }
        ImGui.SetCursorScreenPos(start);
    }

    /// <summary>A tick on row <paramref name="index"/>: when it is one of several picked rows, every picked row that can be ticked follows it.</summary>
    private void TickPicked(int index, bool value, int count, Func<int, bool> canTick, Action<int, bool> set)
    {
        if (_picked.Count > 1 && _picked.Contains(index))
        {
            foreach (var i in _picked)
                if (i < count && canTick(i))
                    set(i, value);
        }
        else
        {
            set(index, value);
        }
    }

    /// <summary>Ctrl+A over the list picks every row.</summary>
    private void PickAllOnShortcut(int count)
    {
        var io = ImGui.GetIO();
        if (count == 0 || io.WantTextInput || !io.KeyCtrl || !ImGui.IsKeyPressed(ImGuiKey.A)
            || !ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
            return;
        _picked.Clear();
        for (int i = 0; i < count; i++)
            _picked.Add(i);
    }

    private void ClearPicks()
    {
        _picked.Clear();
        _pickAnchor = -1;
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
            using (Gui.Disabled(busy || selected == 0))
            {
                if (Gui.IconTextButton(FontAwesomeIcon.Square, "Untick all", "Unticks every row."))
                {
                    if (_unityScan != null)
                        foreach (var asset in _unityScan.Assets) asset.Selected = false;
                    else if (_scan != null)
                        foreach (var asset in _scan.Assets) asset.Selected = false;
                }
            }

            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            Gui.Hint(_picked.Count > 1
                ? $"{selected} of {total} selected, {_picked.Count} rows picked: a tick sets them all"
                : $"{selected} of {total} selected");
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

    private void StartScan() => StartScan(new[] { _packagePath }, add: false);

    /// <summary>Scans <paramref name="paths"/>, one after the other; <paramref name="add"/> lists them below the packages already listed.</summary>
    private void StartScan(IReadOnlyList<string> paths, bool add)
    {
        if (paths.Count == 0 || paths.Any(p => string.IsNullOrWhiteSpace(p) || !File.Exists(p) || !IsPackage(p)))
        {
            _errorMessage = "Pick a .package or .unitypackage file first.";
            return;
        }
        bool unity = IsUnity(paths[0]);
        if (paths.Any(p => IsUnity(p) != unity) || add && (_unityScan != null) != unity)
        {
            _errorMessage = "Packages extracted together must all be Sims packages or all Unity packages.";
            return;
        }
        var listed = _scans.Select(s => s.PackagePath).Concat(_unityScans.Select(s => s.PackagePath)).Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var list = paths.Where(p => !add || !listed.Contains(Path.GetFullPath(p))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0)
        {
            _status = "Those packages are listed already.";
            return;
        }

        if (!add)
        {
            _packagePath = list[0];
            _thumbnails.ForgetMemoryEntries();
            _scans.Clear();
            _unityScans.Clear();
            ShowScans();
            ClearPicks();
        }
        SaveSettings();

        _extraction   = null;
        _errorMessage = string.Empty;
        _applyMessage = string.Empty;
        _status       = string.Empty;
        _progress     = "Opening package";
        _showStash    = false;

        var cancel = NewWork();
        if (unity)
            _unityScanTask = Task.Run(() => ScanAll(list, UnityPackageReader.Scan, cancel), cancel);
        else
            _scanTask = Task.Run(() => ScanAll(list, SimsPackageInspector.Scan, cancel), cancel);
    }

    /// <summary>Scans each package in turn, on the worker; the progress line names the package when there are several.</summary>
    private List<T> ScanAll<T>(List<string> paths, Func<string, Action<string>, CancellationToken, T> scan, CancellationToken cancel)
    {
        var scans = new List<T>();
        for (int i = 0; i < paths.Count; i++)
        {
            var prefix = paths.Count > 1 ? $"{Path.GetFileName(paths[i])} ({i + 1} of {paths.Count}): " : string.Empty;
            scans.Add(scan(paths[i], s => _progress = prefix + s, cancel));
        }
        return scans;
    }

    /// <summary>Lists every scanned package's assets as one table, the first package's first.</summary>
    private void ShowScans()
    {
        _scan = _scans.Count switch
        {
            0 => null,
            1 => _scans[0],
            _ => new SimsScanResult
            {
                PackagePath    = _scans[0].PackagePath,
                Assets         = _scans.SelectMany(s => s.Assets).ToList(),
                Warnings       = _scans.SelectMany(s => s.Warnings).ToList(),
                HasCasPartData = _scans.All(s => s.HasCasPartData),
                IsHair         = _scans.Any(s => s.IsHair),
                AvailableLods  = _scans.SelectMany(s => s.AvailableLods).Distinct().Order().ToList(),
            },
        };

        _unityScan = null;
        if (_unityScans.Count == 1)
            _unityScan = _unityScans[0];
        else if (_unityScans.Count > 1)
        {
            _unityScan = new UnityScanResult { PackagePath = _unityScans[0].PackagePath };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var scan in _unityScans)
            {
                foreach (var asset in scan.Assets)
                {
                    if (seen.Add(asset.Guid))
                        _unityScan.Assets.Add(asset);
                    else
                        asset.Selected = false;   // the same asset shipped again: the first package's row stands for it
                }
                _unityScan.Materials.AddRange(scan.Materials.Where(m => _unityScan.Materials.All(o => o.Guid != m.Guid)));
                _unityScan.Prefabs.AddRange(scan.Prefabs.Where(p => _unityScan.Prefabs.All(o => o.Guid != p.Guid)));
            }
        }

        _packageOf.Clear();
        if (_scans.Count > 1)
            foreach (var scan in _scans)
                foreach (var asset in scan.Assets)
                    _packageOf[asset] = Path.GetFileName(scan.PackagePath);
        if (_unityScans.Count > 1)
            foreach (var scan in _unityScans)
                foreach (var asset in scan.Assets)
                    _packageOf[asset] = Path.GetFileName(scan.PackagePath);
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
        // Copies: a scan started after Stop must not change the lists under a run still winding down.
        if (_unityScans.Count > 0)
        {
            var unity = _unityScans.ToList();
            _unityExtractTask = Task.Run(() => UnityPackageReader.Extract(unity, outputDir, s => _progress = s, cancel), cancel);
        }
        else if (_scans.Count > 0)
        {
            var scans = _scans.ToList();
            _extractTask = Task.Run(() => SimsPackageInspector.Extract(scans, outputDir, s => _progress = s, cancel), cancel);
        }
    }

    private string InPackages()
    {
        int listed = _scans.Count + _unityScans.Count;
        return listed > 1 ? $" in {listed} packages" : string.Empty;
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
                _scans.AddRange(scanTask.Result);
                ShowScans();
                var scan = _scan!;
                _status = $"Found {scan.Meshes.Count()} mesh(es) and {scan.Textures.Count()} texture(s){InPackages()}.";
                foreach (var added in scanTask.Result.Where(s => s.Warnings.Count > 0))
                    StudioServices.Log.Information("[Studio] Sims scan warnings for {0}:\n  {1}", added.PackagePath, string.Join("\n  ", added.Warnings));
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
                _unityScans.AddRange(unityScanTask.Result);
                ShowScans();
                var scan = _unityScan!;
                _unityTick = 0;
                _status = $"Found {scan.Assets.Count(a => a.Kind == UnityAssetKind.Texture)} texture(s), " +
                          $"{scan.Assets.Count(a => a.Kind == UnityAssetKind.Model)} model(s) and {scan.Materials.Count} material(s){InPackages()}.";
                foreach (var added in unityScanTask.Result.Where(s => s.Warnings.Count > 0))
                    StudioServices.Log.Information("[Studio] Unity scan warnings for {0}:\n  {1}", added.PackagePath, string.Join("\n  ", added.Warnings));
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
                NoteExtraction(result.RootFolder, result.Extracted, result.Failed, 0);
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
                NoteExtraction(result.RootFolder, result.WrittenFiles.Count, result.Failed, result.Refused.Count, result.Skipped);
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
