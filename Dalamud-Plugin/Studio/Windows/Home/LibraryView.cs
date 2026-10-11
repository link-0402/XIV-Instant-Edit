using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using InstantEdit.Studio.Services.Library;
using InstantEdit.Studio.Services.Project;
using InstantEdit.Studio.Windows.Studio;
using InstantEdit.Studio.Windows.UI;

namespace InstantEdit.Studio.Windows.Home;

/// <summary>
/// The Mod Browser tab: build a new mod (the build window), import a mod package, open a folder,
/// pick up a recent mod, or open any mod in Penumbra's library (every installed mod as a tile) in
/// a window of its own.
/// </summary>
internal sealed class LibraryView
{
    private static readonly string[] SortLabels = { "Name", "Recently changed", "Author", "Size", "Date added" };
    private const int SortChanged = 1, SortAuthor = 2, SortSize = 3, SortAdded = 4;

    private readonly StudioHost _host;
    private readonly ModLibrary _library;
    private readonly TextureThumbnailCache _thumbs;

    // In SearchIn's order.
    private static readonly string[] SearchInLabels =
    {
        "Name", "Author", "Tag", "Folder", "Anything it changes", "Item", "Emote", "Face", "Hair", "Standing idle", "Groundsit", "Chair sit",
    };
    private static readonly string[] SearchHints =
    {
        "Search mod names", "Search authors", "Search tags", "Folder in Penumbra (Gear/Shoes)",
        "Item, emote, face, hair or pose", "Item name or model id (e6016)", "Emote name",
        "Race, gender and face number", "Race, gender and hair number", "Race or pose number", "Race or pose number", "Race or pose number",
    };
    private static readonly string[] ShowLabels = { "All mods", "Active", "Disabled", "Not enabled anywhere", "Duplicates" };
    private const int ShowActive = 1, ShowDisabled = 2, ShowUnused = 3, ShowDuplicates = 4;

    private string _search = string.Empty;
    private int _sort;
    private int _searchIn;
    private int _show;
    private string? _error;

    // The filtered, sorted list, kept until the listing, the search, the sort, a filter or the scans' results change.
    private IReadOnlyList<InstalledMod>? _source;
    private string? _shownSearch;
    private int _shownSort = -1;
    private int _shownSearchIn = -1;
    private int _shownShow = -1;
    private int _shownRevision = -1;
    private List<InstalledMod> _shown = new();

    // The tile whose right-click menu is open.
    private InstalledMod? _menuFor;
    private bool _openMenu;

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

        float spacing0 = ImGui.GetStyle().ItemSpacing.X;
        ImGui.SetNextItemWidth(Theme.S(170));
        ImGui.Combo("##searchin", ref _searchIn, SearchInLabels, SearchInLabels.Length);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Math.Max(Theme.S(160), ImGui.GetContentRegionAvail().X - Theme.S(170 + 150 + 40) - spacing0 * 3));
        ImGui.InputTextWithHint("##search", SearchHints[_searchIn], ref _search, 256);
        ImGui.SameLine();
        var collection = _library.PlayerCollection();
        ImGui.SetNextItemWidth(Theme.S(170));
        ImGui.Combo("##show", ref _show, ShowLabels, ShowLabels.Length);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(collection is { } shownIn
                ? $"Active and Disabled go by {shownIn.Name}, the collection your character uses."
                : "Active and Disabled go by the collection your character uses.");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Theme.S(150));
        ImGui.Combo("##sort", ref _sort, SortLabels, SortLabels.Length);
        ImGui.SameLine();
        using (Gui.Disabled(_library.IsListing))
        {
            if (Gui.IconButton(FontAwesomeIcon.SyncAlt, "refresh", "List Penumbra's mods again.\nCtrl+click to also read every mod's files again."))
            {
                if (ImGui.GetIO().KeyCtrl)
                    _library.Scans.ForgetAll();
                _library.RefreshInstalled();
            }
        }
        ImGui.Spacing();

        if (installed == null)
        {
            Gui.Icon(FontAwesomeIcon.Hourglass, Theme.Muted);
            ImGui.SameLine();
            Gui.Hint("Listing Penumbra's mods");
            return;
        }

        // A filter that needs a scan of the library starts one, and shows its progress until the results are in.
        // The player's collection is always read: every tile shows whether its mod is active there.
        var scans = _library.Scans;
        var kind = (SearchIn)_searchIn;
        bool needChanges = ChangesIndex.IsChangesSearch(kind);
        bool needActive = _show is ShowActive or ShowDisabled;
        bool needSizes = _sort == SortSize;
        scans.Need(usage: _show == ShowUnused, duplicates: _show == ShowDuplicates, changes: needChanges, installed,
            sizes: needSizes, activeIn: collection?.Id);
        var usage = scans.Usage;
        var active = scans.Active;
        var duplicates = scans.DuplicatesOf;
        var changes = scans.Changes;
        var sizes = scans.Sizes;
        bool waiting = (_show == ShowUnused && usage == null) || (_show == ShowDuplicates && duplicates == null) || (needChanges && changes == null)
                    || (needActive && active == null) || (needSizes && sizes == null);
        if (scans.Progress is { } progress)
        {
            Gui.Icon(FontAwesomeIcon.Hourglass, Theme.Accent);
            ImGui.SameLine();
            ImGui.TextColored(Theme.Accent, $"Checking {installed.Count} mods");
            ImGui.SameLine();
            Gui.Hint(progress.Total > 1 ? $"{progress.Phase} ({progress.Current} of {progress.Total})" : progress.Phase);
            ImGui.SameLine();
            if (ImGui.SmallButton("Cancel##scan"))
            {
                // The filters go back to what needs no scan, or the next frame would start it again.
                scans.Cancel();
                _show = 0;
                _searchIn = 0;
                if (_sort == SortSize)
                    _sort = 0;
            }
            ImGui.Spacing();
        }
        if (waiting)
        {
            if (scans.Progress == null)
            {
                Gui.Hint(scans.Error
                    ?? (_show == ShowUnused && usage == null ? "Penumbra did not say which mods are enabled."
                      : needActive && active == null && collection == null ? "Penumbra did not say which collection your character uses."
                      : "Waiting for the scan."));
            }
            return;
        }

        var search = _search.Trim();
        if (!ReferenceEquals(_source, installed) || _shownSearch != search || _shownSort != _sort
            || _shownSearchIn != _searchIn || _shownShow != _show || _shownRevision != scans.Revision)
        {
            IEnumerable<InstalledMod> mods = installed;
            if (!needChanges)
            {
                if (search.Length > 0)
                    mods = mods.Where(m => ChangesIndex.MatchesListing(m, kind, search));
            }
            else if (changes != null)
            {
                mods = mods.Where(m => changes.TryGetValue(m.FolderName, out var tags) && ChangesIndex.Matches(tags, kind, search));
            }
            if (_show == ShowActive && active != null)
                mods = mods.Where(m => active.Contains(m.FolderName));
            if (_show == ShowDisabled && active != null)
                mods = mods.Where(m => !active.Contains(m.FolderName));
            if (_show == ShowUnused && usage != null)
                mods = mods.Where(m => !usage.Used.Contains(m.FolderName));
            if (_show == ShowDuplicates && duplicates != null)
                mods = mods.Where(m => duplicates.ContainsKey(m.FolderName));
            mods = _sort switch
            {
                SortChanged => mods.OrderByDescending(m => m.LastWrite),
                SortAuthor  => mods.OrderBy(m => m.Author, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase),
                SortSize    => mods.OrderByDescending(m => sizes != null && sizes.TryGetValue(m.FolderName, out var size) ? size : -1)
                                   .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase),
                SortAdded   => mods.OrderByDescending(m => m.Added).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase),
                _           => mods.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase),
            };
            // Copies of one mod sit next to each other, where the first of them sorts.
            if (_show == ShowDuplicates)
                mods = mods.GroupBy(m => m.Name.Trim(), StringComparer.OrdinalIgnoreCase).SelectMany(g => g);
            _shown = mods.ToList();
            _source = installed;
            _shownSearch = search;
            _shownSort = _sort;
            _shownSearchIn = _searchIn;
            _shownShow = _show;
            _shownRevision = scans.Revision;
        }

        if (_shown.Count == 0)
        {
            bool unsearched = search.Length == 0 && !needChanges;
            Gui.Hint(installed.Count == 0 ? "Penumbra has no mods yet."
                : _show == ShowDuplicates && unsearched ? "No mod is installed twice."
                : _show == ShowUnused && unsearched ? "Every mod is enabled somewhere."
                : _show == ShowActive && unsearched ? $"No mod is active in {collection?.Name ?? "your collection"}."
                : _show == ShowDisabled && unsearched ? $"Every mod is active in {collection?.Name ?? "your collection"}."
                : "No mod matches the search.");
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
                DrawMod(shown[index], new Vector2(tileW, tileH), usage, active, collection?.Name, duplicates, needChanges ? changes : null, sizes, kind, search);
            }
        });
        DrawMenu();
        ImGui.EndChild();
    }

    /// <summary>The right-click menu of a library tile.</summary>
    private void DrawMenu()
    {
        if (_openMenu)
        {
            ImGui.OpenPopup("##modmenu");
            _openMenu = false;
        }
        if (_menuFor is not { } mod || !ImGui.BeginPopup("##modmenu"))
            return;
        if (ImGui.MenuItem("Open"))
            Open(mod.FullPath);
        if (ImGui.MenuItem("Open the mod's folder"))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{mod.FullPath}\"") { UseShellExecute = true });
                _error = null;
            }
            catch (Exception ex)
            {
                _error = $"The folder could not be opened: {ex.Message}";
            }
        }
        if (ImGui.MenuItem("Select in Penumbra"))
            _error = _host.PenumbraIpc.OpenModPage(mod.FolderName);
        ImGui.EndPopup();
    }

    /// <summary>The tile hovered last frame: its mod's changed items are asked of Penumbra (once).</summary>
    private string? _hovered;

    private void DrawMod(InstalledMod mod, Vector2 size, ModUsage? usage, IReadOnlySet<string>? active, string? collection,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? duplicates, IReadOnlyDictionary<string, IReadOnlyList<ChangeTag>>? changes,
        IReadOnlyDictionary<string, long>? sizes, SearchIn kind, string search)
    {
        var folder = mod.Folder;
        var badges = new List<Badge>();
        if (active != null && active.Contains(mod.FolderName))
            badges.Add(new Badge("Active", Theme.Good, $"Enabled in {collection ?? "your collection"}."));
        if (_host.IsModOpen(mod.FullPath))
            badges.Add(new Badge("Open", Theme.Good, "Open in a window."));
        if (mod.HasProject)
            badges.Add(new Badge("Your files", Theme.Accent, "Has files of your own that are kept in step with their sources."));
        if (usage != null && !usage.Used.Contains(mod.FolderName))
            badges.Add(new Badge("Unused", Theme.Warn, "Not enabled in any collection."));
        if (duplicates != null && duplicates.TryGetValue(mod.FolderName, out var copies))
            badges.Add(new Badge("Duplicate", Theme.Accent, $"Same files as: {string.Join(", ", copies)}"));

        // With a changes search, the tooltip lists what the mod changes and marks what matched.
        var changed = changes != null && changes.TryGetValue(mod.FolderName, out var tags)
            ? ChangesText(tags, kind, search)
            : _library.ChangedItems(mod, fetch: mod.FolderName == _hovered);
        var detail = mod.Tags.Count > 0 ? string.Join(", ", mod.Tags) : folder;
        long? bytes = sizes != null && sizes.TryGetValue(mod.FolderName, out var known) ? known : null;
        // The sort's own value leads the detail line, so the order can be read off the tiles.
        if (_sort == SortSize && bytes is { } sorted)
            detail = detail.Length > 0 ? $"{ResourceTreeView.FormatSize(sorted)} · {detail}" : ResourceTreeView.FormatSize(sorted);
        else if (_sort == SortAdded && mod.Added != default)
            detail = $"Added {mod.Added.ToLocalTime():yyyy-MM-dd}{(detail.Length > 0 ? $" · {detail}" : string.Empty)}";
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
                      + (bytes is { } total ? $"\nSize: {ResourceTreeView.FormatSize(total)}" : string.Empty)
                      + (mod.Added != default ? $"\nAdded: {mod.Added.ToLocalTime():yyyy-MM-dd HH:mm}" : string.Empty)
                      + (!string.IsNullOrEmpty(changed) ? $"\n\nChanges:\n{changed}" : string.Empty)
                      + "\n\nClick to open it in a window.",
        }, size);

        if (result.Hovered)
            _hovered = mod.FolderName;
        if (result.Clicked)
            Open(mod.FullPath);
        if (result.RightClicked)
        {
            _menuFor = mod;
            _openMenu = true;
        }
    }

    /// <summary>A mod's changes one per line, the ones the search matched first and marked "> ", the first ten.</summary>
    private static string ChangesText(IReadOnlyList<ChangeTag> tags, SearchIn kind, string search)
    {
        var lines = tags.Select(tag => (Tag: tag, Hit: ChangesIndex.Matches(new[] { tag }, kind, search)))
            .OrderBy(line => line.Hit ? 0 : 1)
            .Select(line => line.Hit ? $"> {line.Tag.Label}" : $"  {line.Tag.Label}")
            .ToList();
        return string.Join("\n", lines.Take(10)) + (lines.Count > 10 ? $"\nand {lines.Count - 10} more" : string.Empty);
    }

    private Action<float>? Image(InstalledMod? mod)
    {
        if (mod == null || mod.ImagePath.Length == 0 || !_host.Configuration.ShowThumbnails)
            return null;
        var path = PathNormalizer.ToDiskPath(mod.FullPath, mod.ImagePath);
        return File.Exists(path) ? side => _thumbs.DrawFile(path, side) : null;
    }
}
