using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using InstantEdit.Studio.Services.Project;
using InstantEdit.Studio.Windows.UI;

namespace InstantEdit.Studio.Windows.Home;

/// <summary>
/// The Mod Browser tab: build a new mod (the build window), import a mod package, open a folder,
/// pick up a recent mod, or open any mod in Penumbra's library (every installed mod as a tile) in
/// a window of its own.
/// </summary>
internal sealed class LibraryView
{
    private static readonly string[] SortLabels = { "Name", "Recently changed", "Author" };

    private readonly StudioHost _host;
    private readonly ModLibrary _library;
    private readonly TextureThumbnailCache _thumbs;

    private string _search = string.Empty;
    private int _sort;
    private string? _error;

    // The filtered, sorted list, kept until the listing, the search or the sort changes.
    private IReadOnlyList<InstalledMod>? _source;
    private string? _shownSearch;
    private int _shownSort = -1;
    private List<InstalledMod> _shown = new();

    public LibraryView(StudioHost host)
    {
        _host    = host;
        _library = host.Library;
        _thumbs  = host.Thumbnails;
    }

    public void Draw()
    {
        Theme.TileScale = Math.Clamp(_host.Configuration.TileScale, 0.75f, 1.6f);
        FileDrop.BeginExternalSource();

        bool penumbra = _host.PenumbraIpc.IsAvailable;
        if (penumbra && _library.InstalledStale && !_library.IsListing)
            _library.RefreshInstalled();

        ImGui.Spacing();
        ImGui.TextColored(Theme.Muted, "Build a new mod by porting gear from another game, or open any mod from your Penumbra library to see what it is made of and change any of it.");
        ImGui.Spacing();

        DrawActions(penumbra);
        ImGui.Spacing();

        if (_error != null)
        {
            ImGui.TextColored(Theme.Bad, _error);
            ImGui.Spacing();
        }
        if (_library.IsInstalling)
        {
            Gui.Icon(FontAwesomeIcon.Hourglass, Theme.Accent);
            ImGui.SameLine();
            ImGui.TextColored(Theme.Accent, "Penumbra is importing the package. It opens in a window when it is done.");
            ImGui.Spacing();
        }
        DrawRecent();

        if (!penumbra)
        {
            ImGui.Spacing();
            Gui.Icon(FontAwesomeIcon.ExclamationTriangle, Theme.Warn);
            ImGui.SameLine();
            ImGui.TextColored(Theme.Warn, "Penumbra is not available, so its mod library cannot be listed. You can still open a mod folder.");
        }
        else
        {
            DrawLibrary();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Actions
    // ─────────────────────────────────────────────────────────────────────────

    private void DrawActions(bool penumbra)
    {
        float spacing = ImGui.GetStyle().ItemSpacing.X;
        float width = (ImGui.GetContentRegionAvail().X - spacing * 2) / 3;
        var size = new Vector2(width, Tile.Height(detail: true));

        // The build window keeps one modpack at a time, built into a new mod once it is set up.
        var cfg = _host.Configuration;
        int packed = cfg.ModpackItems.Count;
        var build = Tile.Draw(new TileSpec
        {
            Id        = "build",
            Title     = packed > 0 ? "Continue building" : "Build a new mod",
            Subtitle  = packed > 0
                ? $"{(string.IsNullOrWhiteSpace(cfg.ModName) ? "Your modpack" : cfg.ModName)} · {packed} item(s)"
                : "Port gear or a character feature from another game",
            Detail    = "Set it up from your own files, then build it into Penumbra",
            Icon      = FontAwesomeIcon.Hammer,
            IconColor = Theme.Accent,
        }, size);
        if (build.Clicked)
            _host.OpenBuild();

        using (Gui.Disabled(!penumbra))
        {
            ImGui.SameLine(0, spacing);
            var import = Tile.Draw(new TileSpec
            {
                Id = "import", Title = "Import a mod package", Subtitle = ".pmp, .ttmp or .ttmp2",
                Detail = "Penumbra installs it, then it opens in a window",
                Icon = FontAwesomeIcon.FileImport, IconColor = Theme.Accent,
                Drop = new FileDropRule(p => p.Files.Count == 1 && IsPackage(p.Files[0]) ? null : "Drop one .pmp, .ttmp or .ttmp2 file.", _ => { }),
            }, size);
            if (import.Clicked)
                Gui.Dialogs.OpenFileDialog("Import a mod package", "Mod packages{.pmp,.ttmp,.ttmp2}", (ok, path) => { if (ok) Install(path); });
            if (import.Dropped is { } dropped)
                Install(dropped.Files[0]);
        }

        ImGui.SameLine(0, spacing);
        var open = Tile.Draw(new TileSpec
        {
            Id = "open", Title = "Open a mod folder", Subtitle = "Any folder with a Penumbra meta.json",
            Detail = "Or drop the folder here",
            Icon = FontAwesomeIcon.FolderOpen, IconColor = Theme.Accent,
            Drop = new FileDropRule(p => p.Folders.Count == 1 ? null : "Drop one mod folder.", _ => { }),
        }, size);
        if (open.Clicked)
            Gui.Dialogs.OpenFolderDialog("Open a mod folder", (ok, path) => { if (ok) Open(path); });
        if (open.Dropped is { } folder)
            Open(folder.Folders[0]);
    }

    private static bool IsPackage(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".pmp" or ".ttmp" or ".ttmp2";

    private void Install(string path)
    {
        _error = _library.TryInstall(path, out var error) ? null : error;
    }

    private void Open(string folder)
    {
        _error = _host.OpenMod(folder, out var error) ? null : error;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Recent and library
    // ─────────────────────────────────────────────────────────────────────────

    private void DrawRecent()
    {
        var recent = _host.Configuration.RecentProjects.Where(Directory.Exists).Take(4).ToList();
        if (recent.Count == 0)
            return;

        Gui.SectionHeader("Recent");
        float spacing = ImGui.GetStyle().ItemSpacing.X;
        float width = (ImGui.GetContentRegionAvail().X - spacing * 3) / 4;
        for (int i = 0; i < recent.Count; i++)
        {
            if (i > 0) ImGui.SameLine(0, spacing);
            var folder = recent[i];
            var installed = _library.Installed?.FirstOrDefault(m => string.Equals(m.FullPath, folder, StringComparison.OrdinalIgnoreCase));
            var result = Tile.Draw(new TileSpec
            {
                Id        = $"recent{i}",
                Title     = installed?.Name ?? Path.GetFileName(folder),
                Subtitle  = installed?.Author is { Length: > 0 } author ? author : Path.GetFileName(folder),
                Icon      = FontAwesomeIcon.History,
                IconColor = Theme.Muted,
                DrawThumb = Image(installed),
                Tooltip   = folder,
            }, new Vector2(width, Tile.Height()));
            if (result.Clicked)
                Open(folder);
        }
        ImGui.Spacing();
    }

    private void DrawLibrary()
    {
        var installed = _library.Installed;
        ImGui.Spacing();
        Gui.SectionHeader(installed == null ? "Your mods" : $"Your mods ({installed.Count})");

        ImGui.SetNextItemWidth(Math.Max(Theme.S(200), ImGui.GetContentRegionAvail().X - Theme.S(220)));
        ImGui.InputTextWithHint("##search", "Search name, author or tag", ref _search, 256);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Theme.S(150));
        ImGui.Combo("##sort", ref _sort, SortLabels, SortLabels.Length);
        ImGui.SameLine();
        using (Gui.Disabled(_library.IsListing))
        {
            if (Gui.IconButton(FontAwesomeIcon.SyncAlt, "refresh", "List Penumbra's mods again"))
                _library.RefreshInstalled();
        }
        ImGui.Spacing();

        if (installed == null)
        {
            Gui.Icon(FontAwesomeIcon.Hourglass, Theme.Muted);
            ImGui.SameLine();
            Gui.Hint("Listing Penumbra's mods");
            return;
        }

        var search = _search.Trim();
        if (!ReferenceEquals(_source, installed) || _shownSearch != search || _shownSort != _sort)
        {
            IEnumerable<InstalledMod> mods = installed;
            if (search.Length > 0)
            {
                mods = mods.Where(m => m.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                                    || m.Author.Contains(search, StringComparison.OrdinalIgnoreCase)
                                    || m.FolderName.Contains(search, StringComparison.OrdinalIgnoreCase)
                                    || m.Tags.Any(t => t.Contains(search, StringComparison.OrdinalIgnoreCase)));
            }
            mods = _sort switch
            {
                1 => mods.OrderByDescending(m => m.LastWrite),
                2 => mods.OrderBy(m => m.Author, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase),
                _ => mods.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase),
            };
            _shown = mods.ToList();
            _source = installed;
            _shownSearch = search;
            _shownSort = _sort;
        }

        if (_shown.Count == 0)
        {
            Gui.Hint(installed.Count == 0 ? "Penumbra has no mods yet." : "No mod matches the search.");
            return;
        }

        if (!ImGui.BeginChild("##library", new Vector2(-1, -1), false))
        {
            ImGui.EndChild();
            return;
        }

        float width = ImGui.GetContentRegionAvail().X;
        float spacing = ImGui.GetStyle().ItemSpacing.X;
        int columns = Math.Max(1, (int)((width + spacing) / (Theme.T(300) + spacing)));
        float tileW = (width - spacing * (columns - 1)) / columns;
        float tileH = Tile.Height(detail: true);
        int rowCount = (_shown.Count + columns - 1) / columns;
        var heights = Enumerable.Repeat(tileH, rowCount).ToList();
        var shown = _shown;

        VirtualRows.Draw(heights, row =>
        {
            for (int c = 0; c < columns; c++)
            {
                int index = row * columns + c;
                if (index >= shown.Count)
                    break;
                if (c > 0) ImGui.SameLine(0, spacing);
                DrawMod(shown[index], new Vector2(tileW, tileH));
            }
        });
        ImGui.EndChild();
    }

    /// <summary>The tile hovered last frame: its mod's changed items are asked of Penumbra (once).</summary>
    private string? _hovered;

    private void DrawMod(InstalledMod mod, Vector2 size)
    {
        var folder = _library.SortPath(mod);
        var badges = new List<Badge>();
        if (_host.IsModOpen(mod.FullPath))
            badges.Add(new Badge("Open", Theme.Good, "Open in a window."));
        if (mod.HasProject)
            badges.Add(new Badge("Your files", Theme.Accent, "Has files of your own that are kept in step with their sources."));

        var changed = _library.ChangedItems(mod, fetch: mod.FolderName == _hovered);
        var detail = mod.Tags.Count > 0 ? string.Join(", ", mod.Tags) : folder;
        var result = Tile.Draw(new TileSpec
        {
            Id        = mod.FolderName,
            Title     = mod.Name,
            Subtitle  = mod.Author.Length > 0 ? mod.Author : "—",
            Detail    = detail,
            Icon      = FontAwesomeIcon.BoxOpen,
            IconColor = Theme.Muted,
            DrawThumb = Image(mod),
            Badges    = badges,
            Tooltip   = $"{mod.Name}\n{mod.FullPath}{(folder.Length > 0 ? $"\nIn Penumbra: {folder}" : string.Empty)}"
                      + (!string.IsNullOrEmpty(changed) ? $"\n\nChanges:\n{changed}" : string.Empty)
                      + "\n\nClick to open it in a window.",
        }, size);

        if (result.Hovered)
            _hovered = mod.FolderName;
        if (result.Clicked)
            Open(mod.FullPath);
    }

    private Action<float>? Image(InstalledMod? mod)
    {
        if (mod == null || mod.ImagePath.Length == 0 || !_host.Configuration.ShowThumbnails)
            return null;
        var path = PathNormalizer.ToDiskPath(mod.FullPath, mod.ImagePath);
        return File.Exists(path) ? side => _thumbs.DrawFile(path, side) : null;
    }
}
