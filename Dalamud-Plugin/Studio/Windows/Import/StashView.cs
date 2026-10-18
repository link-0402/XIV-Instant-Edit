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
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Services.Stash;
using InstantEdit.Studio.Windows.UI;

namespace InstantEdit.Studio.Windows.Import;

/// <summary>
/// The import window's file stash: every extraction under the output folder, and every folder of
/// loose files the user opened, on the left; the picked one's files on the right, by kind and, for
/// textures, by purpose. Every row drags onto a
/// texture or model tile of the build window or a mod window; a material's textures can also be
/// put into the build window's selected material in one go.
/// </summary>
internal sealed class StashView
{
    private const string ConfirmRemoveId = "Remove this extraction?##StashRemove";

    private static readonly (StashKind Kind, string Label, FontAwesomeIcon Icon)[] Tabs =
    {
        (StashKind.Model,     "Models",     FontAwesomeIcon.Cube),
        (StashKind.Texture,   "Textures",   FontAwesomeIcon.Image),
        (StashKind.Material,  "Materials",  FontAwesomeIcon.Palette),
        (StashKind.Animation, "Animations", FontAwesomeIcon.Running),
        (StashKind.Other,     "Other",      FontAwesomeIcon.File),
    };

    private static readonly TexturePurpose[] PurposeOrder =
        { TexturePurpose.Diffuse, TexturePurpose.Normal, TexturePurpose.Mask, TexturePurpose.Occlusion, TexturePurpose.Emission, TexturePurpose.Matcap, TexturePurpose.Other };

    private readonly StudioHost _host;
    private readonly TextureThumbnailCache _thumbnails;

    private IReadOnlyList<string> _folders = Array.Empty<string>();
    private IReadOnlyList<string> _opened = Array.Empty<string>();

    /// <summary>Lists the output folder once its path has stopped changing, not on every keystroke in its field.</summary>
    private readonly SettledInput _listing = new(TimeSpan.FromMilliseconds(400));
    private string? _folder;
    private StashManifest? _manifest;

    /// <summary>A folder without stash.json being scanned off the render thread; an opened folder can hold thousands of files.</summary>
    private Task<StashManifest?>? _scan;
    private StashKind _tab = StashKind.Texture;
    private string _search = string.Empty;
    private string? _pendingRemove;
    private bool _openRemove;
    private string _message = string.Empty;

    public StashView(StudioHost host, TextureThumbnailCache thumbnails)
    {
        _host = host;
        _thumbnails = thumbnails;
    }

    /// <summary>The extraction shown, or null.</summary>
    public string? Folder => _folder;

    /// <summary>Whether the stash has anything to show: an extraction in the output folder, or an opened folder.</summary>
    public bool HasFolders(string outputDir)
    {
        EnsureListed(outputDir);
        return _folders.Count > 0 || _opened.Count > 0;
    }

    /// <summary>Shows <paramref name="folder"/>, read again from disk.</summary>
    public void Show(string folder)
    {
        _folder = folder;
        _manifest = StashManifest.Read(folder);
        _scan = _manifest == null ? Task.Run(() => SafeScan(folder)) : null;
        _listing.Invalidate();   // the list may have gained it
        _message = string.Empty;
        _exists.Clear();
        _imageCounts.Clear();
        _thumbnails.ForgetMemoryEntries();
        PickTab();
    }

    /// <summary>Adds a folder of loose files to the stash and shows it. Returns why it wasn't added, or null.</summary>
    public string? OpenFolder(string folder)
    {
        var cfg = _host.Configuration;
        var problem = StashFolders.AddOpened(cfg.StashOpenedFolders, folder);
        if (problem != null)
        {
            _message = problem;
            return problem;
        }
        cfg.Save();
        Show(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)));
        return null;
    }

    /// <summary>Asks for a folder of loose files to open in the stash; <paramref name="opened"/> runs once it is shown.</summary>
    public void BrowseFolder(Action? opened = null)
    {
        Gui.Dialogs.OpenFolderDialog("Open a folder in the stash", (ok, path) =>
        {
            if (ok && OpenFolder(path) == null)
                opened?.Invoke();
        });
    }

    /// <summary>The tab to land on: the current one if this folder has anything for it, else textures or models.</summary>
    private void PickTab()
    {
        if (_manifest != null && _manifest.Entries.All(e => e.Kind != _tab) && !(_tab == StashKind.Material && _manifest.Materials.Count > 0))
            _tab = _manifest.Entries.Any(e => e.Kind == StashKind.Texture) ? StashKind.Texture : StashKind.Model;
    }

    public void Draw(string outputDir)
    {
        EnsureListed(outputDir);
        if (_folder == null && (_folders.Count > 0 || _opened.Count > 0))
            Show(_folders.Count > 0 ? _folders[0] : _opened[0]);
        if (_scan is { IsCompleted: true } scan)
        {
            _scan = null;
            _manifest = scan.IsCompletedSuccessfully ? scan.Result : null;
            if (_manifest == null)
                _message = "This folder could not be read.";
            PickTab();
        }

        float listW = Theme.S(230);
        if (ImGui.BeginChild("##StashFolders", new Vector2(listW, -1), true))
            DrawFolders(outputDir);
        ImGui.EndChild();
        ImGui.SameLine();
        if (ImGui.BeginChild("##StashFiles", new Vector2(-1, -1), true))
            DrawFiles();
        ImGui.EndChild();

        DrawRemovePopup(outputDir);
    }

    private void EnsureListed(string outputDir)
    {
        if (!_listing.Due(outputDir, DateTime.UtcNow))
            return;
        _folders = StashFolders.ListExtractions(outputDir);
        _opened = StashFolders.Opened(_host.Configuration.StashOpenedFolders)
            .Where(f => !_folders.Contains(f, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (_folder != null && !Directory.Exists(_folder))
        {
            _folder = null;
            _manifest = null;
            _scan = null;
        }
    }

    private bool IsOpened(string folder) => _opened.Contains(folder, StringComparer.OrdinalIgnoreCase);

    // ─────────────────────────────────────────────────────────────────────────
    // Extractions
    // ─────────────────────────────────────────────────────────────────────────

    private void DrawFolders(string outputDir)
    {
        Gui.Hint($"{_folders.Count} extraction(s)");
        ImGui.SameLine();
        float button = ImGui.GetFrameHeight();
        Gui.AlignRight(button * 2 + ImGui.GetStyle().ItemSpacing.X);
        if (Gui.IconButton(FontAwesomeIcon.FolderPlus, "StashOpenFolder", "Open a folder of loose files (textures, models, materials) in the stash. Nothing is written into it."))
            BrowseFolder();
        ImGui.SameLine();
        if (Gui.IconButton(FontAwesomeIcon.Sync, "StashRefresh", "List the folders again and re-read the one shown"))
        {
            _listing.Invalidate();
            if (_folder != null)
                Show(_folder);
        }
        ImGui.Separator();

        if (_folders.Count == 0 && _opened.Count == 0)
            Gui.HintWrapped("Nothing here yet. Import a package, or open or drop a folder of files.");

        foreach (var folder in _folders)
            DrawFolderRow(folder, opened: false);

        if (_opened.Count > 0)
        {
            ImGui.Spacing();
            Gui.Hint($"{_opened.Count} opened folder(s)");
            ImGui.Separator();
            foreach (var folder in _opened)
                DrawFolderRow(folder, opened: true);
        }

        // Folders dropped from Explorer anywhere on the list are opened in the stash.
        ImGui.Dummy(new Vector2(ImGui.GetContentRegionAvail().X, Math.Max(ImGui.GetContentRegionAvail().Y, button)));
        if (FileDrop.Target(p => p.Folders.Count > 0 && p.Files.Count == 0 ? null : "Drop folders to open them in the stash.", out var dropped))
            foreach (var folder in dropped.Folders)
                OpenFolder(folder);
    }

    private void DrawFolderRow(string folder, bool opened)
    {
        ImGui.PushID(folder);
        bool selected = string.Equals(folder, _folder, StringComparison.OrdinalIgnoreCase);
        float trash = ImGui.GetFrameHeight();
        if (ImGui.Selectable(Path.GetFileName(folder), selected, ImGuiSelectableFlags.None,
                new Vector2(ImGui.GetContentRegionAvail().X - trash - ImGui.GetStyle().ItemSpacing.X, 0)) && !selected)
            Show(folder);
        Gui.Tooltip(folder);
        ImGui.SameLine();
        if (opened)
        {
            if (Gui.IconButton(FontAwesomeIcon.Times, "forget", "Take out of the stash. The folder and its files are not touched."))
            {
                StashFolders.Forget(_host.Configuration.StashOpenedFolders, folder);
                _host.Configuration.Save();
                if (selected)
                {
                    _folder = null;
                    _manifest = null;
                    _scan = null;
                }
                _listing.Invalidate();
            }
        }
        else if (Gui.IconButton(FontAwesomeIcon.Trash, "remove", "Remove this extraction's folder", danger: true))
        {
            _pendingRemove = folder;
            _openRemove = true;
        }
        ImGui.PopID();
    }

    private void DrawRemovePopup(string outputDir)
    {
        if (_openRemove)
        {
            _openRemove = false;
            ImGui.OpenPopup(ConfirmRemoveId);
        }
        Confirm.Modal(ConfirmRemoveId, () =>
        {
            ImGui.TextUnformatted($"Delete {Path.GetFileName(_pendingRemove)} and every file in it?");
            ImGui.TextColored(Theme.Muted, "Set-ups that point at these files lose them. The package itself is not touched.");
        }, "Delete", () =>
        {
            if (_pendingRemove == null) return;
            var problem = StashFolders.Remove(outputDir, _pendingRemove);
            _message = problem ?? $"Removed {Path.GetFileName(_pendingRemove)}.";
            if (problem == null && string.Equals(_pendingRemove, _folder, StringComparison.OrdinalIgnoreCase))
            {
                _folder = null;
                _manifest = null;
            }
            _pendingRemove = null;
            _listing.Invalidate();
        }, danger: true);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Files
    // ─────────────────────────────────────────────────────────────────────────

    private void DrawFiles()
    {
        if (_folder != null && _scan != null)
        {
            Gui.Icon(FontAwesomeIcon.Spinner, Theme.Accent);
            ImGui.SameLine();
            ImGui.TextColored(Theme.Accent, $"Reading {Path.GetFileName(_folder)}");
            return;
        }
        if (_folder == null || _manifest == null)
        {
            Gui.HintWrapped(string.IsNullOrEmpty(_message) ? "Pick a folder on the left." : _message);
            return;
        }

        DrawHeader(_folder, _manifest);
        DrawTabs(_manifest);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##StashSearch", "Search names and materials", ref _search, 256);
        ImGui.Spacing();

        if (ImGui.BeginChild("##StashRows", new Vector2(-1, -1), false))
        {
            if (_tab == StashKind.Material) DrawMaterials(_folder, _manifest);
            else if (_tab == StashKind.Texture) DrawTextures(_folder, _manifest);
            else DrawRows(_folder, Visible(_manifest).Where(e => e.Kind == _tab).ToList());
        }
        ImGui.EndChild();
    }

    private void DrawHeader(string folder, StashManifest manifest)
    {
        using (Fonts.Title())
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(Path.GetFileName(folder));
        }
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        Gui.Hint(manifest.SourceKind switch
        {
            "unity" => "from a Unity package",
            "sims"  => "from a Sims 4 package",
            _       => IsOpened(folder) ? "an opened folder; nothing is written into it" : "files in this folder",
        });
        Gui.AlignRight(ImGui.GetFrameHeight());
        if (Gui.IconButton(FontAwesomeIcon.FolderOpen, "StashReveal", "Show in Explorer"))
            Reveal(folder);

        ImGui.TextColored(Theme.Accent, "Drag any row onto a texture or model tile in the build window, or in a mod window.");
        if (manifest.Truncated)
            ImGui.TextColored(Theme.Warn, $"This folder holds more than {StashManifest.MaxScanFiles} files; only the first {StashManifest.MaxScanFiles} are listed.");
        if (!string.IsNullOrEmpty(_message))
            Gui.Hint(_message);
    }

    private void DrawTabs(StashManifest manifest)
    {
        float spacing = ImGui.GetStyle().ItemSpacing.X * 0.5f;
        float w = (ImGui.GetContentRegionAvail().X - spacing * (Tabs.Length - 1)) / Tabs.Length;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(spacing, ImGui.GetStyle().ItemSpacing.Y));
        for (int i = 0; i < Tabs.Length; i++)
        {
            var (kind, label, icon) = Tabs[i];
            int count = kind == StashKind.Material ? manifest.Materials.Count : manifest.Entries.Count(e => e.Kind == kind);
            bool active = _tab == kind;
            if (i > 0) ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Button, active ? Theme.Accent with { W = 0.45f } : Theme.Band);
            ImGui.PushStyleColor(ImGuiCol.Text,   active ? new Vector4(1, 1, 1, 1) : count == 0 ? Theme.Faint : Theme.Muted);
            if (Dalamud.Interface.Components.ImGuiComponents.IconButtonWithText(icon, $"{label} ({count})##stashtab{i}", new Vector2(w, 0)))
                _tab = kind;
            ImGui.PopStyleColor(2);
        }
        ImGui.PopStyleVar();
    }

    private IEnumerable<StashEntry> Visible(StashManifest manifest)
    {
        var q = _search.Trim();
        return q.Length == 0
            ? manifest.Entries
            : manifest.Entries.Where(e => e.Path.Contains(q, StringComparison.OrdinalIgnoreCase)
                                       || e.Groups.Any(g => g.Contains(q, StringComparison.OrdinalIgnoreCase)));
    }

    private void DrawTextures(string folder, StashManifest manifest)
    {
        var textures = Visible(manifest).Where(e => e.Kind == StashKind.Texture).ToList();
        if (textures.Count == 0)
        {
            Gui.Hint(_search.Length > 0 ? "(no textures match)" : "(no textures)");
            return;
        }

        var items = ItemsOf(folder, manifest);
        if (items == null)
        {
            Gui.Icon(FontAwesomeIcon.Spinner, Theme.Accent);
            ImGui.SameLine();
            Gui.Hint("Sorting textures into items");
            DrawTexturesByPurpose(folder, manifest, textures);
            return;
        }

        // One block per item, its textures by purpose; then matcaps; then what belongs to no item.
        var shown = textures.ToHashSet();
        for (int i = 0; i < items.Items.Count; i++)
        {
            var item = items.Items[i];
            var rows = item.Textures.Where(shown.Contains).ToList();
            if (rows.Count == 0 || !ImGui.CollapsingHeader($"{item.Name} ({rows.Count})##item{i}", ImGuiTreeNodeFlags.DefaultOpen))
                continue;
            if (item.Models.Count > 0)
                Gui.Hint($"On {string.Join(", ", item.Models)}");
            DrawTextureRows(folder, rows, item.ByLayout);
        }

        var matcaps = items.Matcaps.Where(shown.Contains).ToList();
        if (matcaps.Count > 0 && ImGui.CollapsingHeader($"Matcaps ({matcaps.Count})##items-matcaps", ImGuiTreeNodeFlags.DefaultOpen))
            DrawTextureRows(folder, matcaps, null);

        var loose = items.Loose.Where(shown.Contains).ToList();
        if (loose.Count > 0 && ImGui.CollapsingHeader($"{(items.Items.Count > 0 ? "Not part of an item" : "Textures")} ({loose.Count})##items-loose",
                ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawSwatchFolders(folder, manifest, loose.Where(t => t.Purpose == TexturePurpose.Diffuse));
            DrawTextureRows(folder, loose, null);
        }
    }

    /// <summary>The plain list, by purpose, while the textures are being sorted into items.</summary>
    private void DrawTexturesByPurpose(string folder, StashManifest manifest, List<StashEntry> textures)
    {
        foreach (var purpose in PurposeOrder)
        {
            var group = textures.Where(t => t.Purpose == purpose).ToList();
            if (group.Count == 0)
                continue;
            if (!ImGui.CollapsingHeader($"{PurposeLabel(purpose)} ({group.Count})##purpose{purpose}", ImGuiTreeNodeFlags.DefaultOpen))
                continue;
            if (purpose == TexturePurpose.Diffuse)
                DrawSwatchFolders(folder, manifest, group);
            DrawRows(folder, group);
        }
    }

    /// <summary>
    /// Diffuse swatches that share a folder can go in together, as one variant group; a folder that
    /// also holds normals, masks or matcaps is a texture set, and would drop them all as swatches.
    /// </summary>
    private void DrawSwatchFolders(string folder, StashManifest manifest, IEnumerable<StashEntry> diffuses)
    {
        foreach (var shared in diffuses.GroupBy(t => StashManifest.FolderOf(t.Path), StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1 && manifest.HoldsOnlyDiffuses(g.Key)))
        {
            var full = Full(folder, shared.Key);
            int images = Images(full);
            if (images < 2) continue;
            DrawRow($"{Path.GetFileName(full)} (folder)", $"{images} images, drops as a variant group",
                DroppedPaths.Folder(full), full, true, FontAwesomeIcon.Folder, false);
        }
    }

    /// <summary>Texture rows that say what each texture is for.</summary>
    private void DrawTextureRows(string folder, IReadOnlyList<StashEntry> entries, ISet<StashEntry>? byLayout)
    {
        foreach (var entry in entries)
        {
            var full = Full(folder, entry.Path);
            var detail = string.Join(" · ", new[]
            {
                PurposeLabel(entry.Purpose), byLayout?.Contains(entry) == true ? "matched by layout" : string.Empty,
                entry.Detail, string.Join(", ", entry.Groups),
            }.Where(s => s.Length > 0));
            DrawRow(Path.GetFileName(entry.Path), detail, DroppedPaths.File(full), full, false, FontAwesomeIcon.File, entry.NeedsConversion, entry.Path);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Items, worked out off the render thread: reading images and materials takes a moment.
    // ─────────────────────────────────────────────────────────────────────────

    private StashManifest? _itemsFor;
    private Task<TextureItemList?>? _items;
    private CancellationTokenSource? _itemsCancel;

    /// <summary>The shown manifest's textures by item, or null while they are being sorted.</summary>
    private TextureItemList? ItemsOf(string folder, StashManifest manifest)
    {
        if (!ReferenceEquals(_itemsFor, manifest))
        {
            _itemsCancel?.Cancel();
            _itemsCancel?.Dispose();
            _itemsCancel = new CancellationTokenSource();
            var cancel = _itemsCancel.Token;
            _itemsFor = manifest;
            _items = Task.Run(() => SafeItems(manifest, folder, cancel), cancel);
        }
        return _items is { IsCompletedSuccessfully: true } done ? done.Result : null;
    }

    /// <summary>Everything loose, for a sort that failed: the rows still show, by purpose.</summary>
    private static TextureItemList Fallback(StashManifest manifest)
    {
        var list = new TextureItemList();
        list.Loose.AddRange(manifest.Entries.Where(e => e.Kind == StashKind.Texture).OrderBy(e => e.Purpose));
        return list;
    }

    private static TextureItemList? SafeItems(StashManifest manifest, string folder, CancellationToken cancel)
    {
        try
        {
            return TextureItems.Build(manifest, folder, layout: true, cancel);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            StudioServices.Log.Warning(ex, "[Studio] Could not sort the stash's textures into items in {0}", folder);
            return Fallback(manifest);
        }
    }

    private void DrawRows(string folder, IReadOnlyList<StashEntry> entries)
    {
        if (entries.Count == 0)
        {
            Gui.Hint(_search.Length > 0 ? "(nothing matches)" : "(nothing of this kind)");
            return;
        }
        foreach (var entry in entries)
        {
            var full = Full(folder, entry.Path);
            var detail = string.Join(" · ", new[] { entry.Detail, string.Join(", ", entry.Groups) }.Where(s => s.Length > 0));
            var icon = entry.Kind switch
            {
                StashKind.Model     => FontAwesomeIcon.Cube,
                StashKind.Animation => FontAwesomeIcon.Running,
                StashKind.Material  => FontAwesomeIcon.Palette,
                _                   => FontAwesomeIcon.File,
            };
            DrawRow(Path.GetFileName(entry.Path), detail, DroppedPaths.File(full), entry.Kind == StashKind.Texture ? full : null,
                false, icon, entry.NeedsConversion, entry.Path);
        }
    }

    /// <summary>One draggable row: thumbnail or icon, name, detail, and a reveal button.</summary>
    private void DrawRow(string label, string detail, DroppedPaths paths, string? preview, bool previewIsFolder, FontAwesomeIcon icon,
        bool warn, string? id = null)
    {
        var target = paths.Folders.Concat(paths.Files).First();
        bool exists = Exists(target, previewIsFolder);
        ImGui.PushID(id ?? target);

        float thumb = ImGui.GetFrameHeight() * 1.6f;
        if (preview != null && exists)
            _thumbnails.Draw(new TextureSlot { SourcePath = preview, UseVariants = previewIsFolder }, thumb);
        else
            Gui.Icon(icon, warn ? Theme.Warn : Theme.Muted);
        ImGui.SameLine();

        ImGui.BeginGroup();
        ImGui.Selectable($"{label}##row", false, ImGuiSelectableFlags.None,
            new Vector2(ImGui.GetContentRegionAvail().X - ImGui.GetFrameHeight() - ImGui.GetStyle().ItemSpacing.X, 0));
        if (exists)
            FileDrop.Source(paths, label);
        if (ImGui.IsItemHovered() && !ImGui.IsMouseDragging(ImGuiMouseButton.Left))
            ImGui.SetTooltip(exists ? $"{target}\n\nDrag onto a tile in the build window or a mod window." : $"{target}\n\nThis file is gone.");
        ImGui.SameLine();
        if (Gui.IconButton(FontAwesomeIcon.FolderOpen, "reveal", "Show in Explorer"))
            Reveal(target);
        if (!exists)
            ImGui.TextColored(Theme.Bad, "missing");
        else if (detail.Length > 0)
            ImGui.TextColored(warn ? Theme.Warn : Theme.Faint, ResourceStyle.Fit(detail, ImGui.GetContentRegionAvail().X));
        ImGui.EndGroup();

        ImGui.PopID();
    }

    private void DrawMaterials(string folder, StashManifest manifest)
    {
        var q = _search.Trim();
        var materials = manifest.Materials.Where(m => q.Length == 0 || m.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        if (materials.Count == 0)
        {
            Gui.HintWrapped(manifest.Materials.Count == 0
                ? "This extraction lists no materials. Its textures are still on the Textures tab."
                : "(no materials match)");
            return;
        }

        var target = _host.Pack.CurrentMaterial;
        for (int i = 0; i < materials.Count; i++)
        {
            var material = materials[i];
            ImGui.PushID(i);
            if (ImGui.CollapsingHeader($"{material.Name}##material", ImGuiTreeNodeFlags.DefaultOpen))
            {
                var full = material.Textures.ToDictionary(t => t.Key, t => Full(folder, t.Value));
                foreach (var purpose in PurposeOrder)
                {
                    if (!full.TryGetValue(purpose, out var path)) continue;
                    DrawRow(Path.GetFileName(path), PurposeLabel(purpose), DroppedPaths.File(path), path, false, FontAwesomeIcon.Image, false, $"{i}/{purpose}");
                }

                using (Gui.Disabled(target == null))
                {
                    var label = target != null ? $"Apply to \"{target.Name}\"" : "Apply to the selected material";
                    if (Gui.IconTextButton(FontAwesomeIcon.ArrowRight, label,
                            target == null
                                ? "Select a material in the build window's Set-up view first."
                                : "Puts these textures into that material's diffuse, normal and mask slots."))
                    {
                        var plan = StashApply.Plan(full, target!.Textures.Select(t => t.Type).ToList());
                        _message = _host.Pack.ApplyTextures(plan, material.Name);
                        _host.OpenBuild();
                        _host.Pack.GoToStage(StageId.Details);
                    }
                }
                ImGui.Spacing();
            }
            ImGui.PopID();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────

    // What the rows show of the disk, read again every couple of seconds rather than every frame:
    // an extraction can hold hundreds of files.
    private static readonly TimeSpan RecheckEvery = TimeSpan.FromSeconds(2);
    private readonly Dictionary<(string Path, bool Folder), (DateTime At, bool Exists)> _exists = new();
    private readonly Dictionary<string, (DateTime At, int Count)> _imageCounts = new(StringComparer.OrdinalIgnoreCase);

    private bool Exists(string path, bool folder)
    {
        var now = DateTime.UtcNow;
        if (_exists.TryGetValue((path, folder), out var known) && now - known.At < RecheckEvery)
            return known.Exists;
        bool exists = folder ? Directory.Exists(path) : File.Exists(path);
        _exists[(path, folder)] = (now, exists);
        return exists;
    }

    private int Images(string folder)
    {
        var now = DateTime.UtcNow;
        if (_imageCounts.TryGetValue(folder, out var known) && now - known.At < RecheckEvery)
            return known.Count;
        int count = StashFolders.CountImages(folder);
        _imageCounts[folder] = (now, count);
        return count;
    }

    private static string Full(string folder, string relative) => Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));

    private static string PurposeLabel(TexturePurpose purpose) => purpose switch
    {
        TexturePurpose.Diffuse   => "Diffuse",
        TexturePurpose.Normal    => "Normal",
        TexturePurpose.Mask      => "Mask / specular",
        TexturePurpose.Occlusion => "Ambient occlusion",
        TexturePurpose.Emission  => "Emission",
        TexturePurpose.Matcap    => "Matcap",
        _                        => "Other",
    };

    private static StashManifest? SafeScan(string folder)
    {
        try { return StashManifest.Scan(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static void Reveal(string path)
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
}
