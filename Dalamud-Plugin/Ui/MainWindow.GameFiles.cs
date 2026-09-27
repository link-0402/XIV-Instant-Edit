using System.Collections.Immutable;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using InstantEdit.Services.GameFiles;

namespace InstantEdit.Ui;

public sealed partial class MainWindow
{
    private const string GameSelectionMenu = "##game-selection-menu";

    private static readonly (string Header, GameFileCategory[] Categories)[] GameFileGroups =
    [
        ("Character", [GameFileCategory.Hair, GameFileCategory.Face, GameFileCategory.Tail, GameFileCategory.Ears, GameFileCategory.Body]),
        ("Gear", [GameFileCategory.Equipment, GameFileCategory.Accessory]),
        ("Weapons", [GameFileCategory.Weapon]),
    ];

    private GameFileBrowserService? _gameFiles;
    private GameFileExportService? _gameExport;
    private readonly GameFileFilter _gameFilter = new();
    private readonly GameFileSelection _gameSelection = new();
    private readonly GameFileRowList _gameRows = new();
    private readonly Dictionary<string, ResourceView> _gameViews = new(StringComparer.OrdinalIgnoreCase);
    private GameFileCategory _gameCategory = GameFileCategory.Hair;
    private float _gameListWidth;
    private string _gameSearchText = string.Empty;
    private ActorView? _gameActor;
    private ImmutableArray<GameModelEntry> _gameSlotCountSource;
    private Dictionary<string, int> _gameSlotCounts = [];

    internal void AttachGameFiles(GameFileBrowserService browser, GameFileExportService export)
    {
        _gameFiles = browser;
        _gameExport = export;
        export.Finished += OnGameExportFinished;
    }

    private void DetachGameFiles()
    {
        if (_gameExport is not null)
            _gameExport.Finished -= OnGameExportFinished;
    }

    /// <summary> Master/detail like the Mod Browser: the categories on the left, a category's vanilla models on the right. </summary>
    private void DrawGameFilesTab()
    {
        ImGui.Spacing();
        if (_gameFiles is not { } browser)
        {
            Widgets.EmptyState(FontAwesomeIcon.Database, "Game Files are unavailable", "The plugin could not open the game's data.");
            return;
        }
        if (_gameListWidth <= 0)
            _gameListWidth = Theme.Scaled(190);
        var height = Math.Max(1, ImGui.GetContentRegionAvail().Y);
        var maxListWidth = Math.Max(Theme.Scaled(150), ImGui.GetContentRegionAvail().X - Theme.Scaled(440));
        _gameListWidth = Math.Clamp(_gameListWidth, Theme.Scaled(150), maxListWidth);

        using (var list = ImRaii.Child("##game-categories", new Vector2(_gameListWidth, height), true))
        {
            if (list.Success)
                DrawGameCategories(browser);
        }
        ImGui.SameLine(0, 0);
        Widgets.Splitter("##game-splitter", ref _gameListWidth, Theme.Scaled(150), maxListWidth, height);
        ImGui.SameLine(0, 0);
        using var detail = ImRaii.Child("##game-detail", new Vector2(0, height), false);
        if (detail.Success)
            DrawGameCategory(browser);
    }

    private void DrawGameCategories(GameFileBrowserService browser)
    {
        foreach (var (header, categories) in GameFileGroups)
        {
            ImGui.TextColored(Theme.Hint, header);
            foreach (var category in categories)
            {
                var state = browser.TryGetCategory(category);
                var count = state is { Loading: false, Error.Length: 0 } ? $"  {state.Entries.Length:N0}" : string.Empty;
                if (ImGui.Selectable($"{GameModelPaths.CategoryLabel(category)}{count}###game-category-{category}", _gameCategory == category))
                    SelectGameCategory(category);
            }
            ImGui.Spacing();
        }
        if (_gameSelection.Count == 0)
            return;
        ImGui.Separator();
        ImGui.TextColored(Theme.Muted, _gameSelection.Count == 1 ? "1 model ticked" : $"{_gameSelection.Count:N0} models ticked");
    }

    private void SelectGameCategory(GameFileCategory category)
    {
        if (_gameCategory == category)
            return;
        _gameCategory = category;
        // Slots differ between categories; the other filters carry over.
        _gameFilter.Slot = string.Empty;
    }

    private void DrawGameCategory(GameFileBrowserService browser)
    {
        var state = browser.Category(_gameCategory);
        var names = browser.Names;
        _gameFilter.Text = _gameSearchText;
        var visible = _gameFilter.Apply(state.Entries, names);
        DrawGameHeader(browser, state, visible);
        DrawGameFilterBar(state, names);
        DrawGameExportStatus();
        ImGui.Spacing();

        if (state.Entries.IsEmpty)
        {
            if (state.Loading)
                Widgets.EmptyState(FontAwesomeIcon.Sync, "Scanning the game's files…", "Checking every race and ID once; this takes a moment.");
            else if (state.Error.Length > 0)
                Widgets.EmptyState(FontAwesomeIcon.ExclamationTriangle, "Could not list these models", state.Error);
            else
                Widgets.EmptyState(FontAwesomeIcon.Database, "No models", "The game has no files in this category.");
            return;
        }
        if (visible.Count == 0)
        {
            Widgets.EmptyState(FontAwesomeIcon.Search, "No matching models", "Change the search, the race or the slot filter.");
            return;
        }
        var rows = _gameRows.Build(visible, browser.DependencyVersion, browser.Dependencies, entry => browser.SkeletonFiles.For(entry.GamePath));
        DrawGameTable(names, visible, rows);
    }

    private void DrawGameHeader(GameFileBrowserService browser, GameFileCategoryState state, IReadOnlyList<GameModelEntry> visible)
    {
        var style = ImGui.GetStyle();
        var frame = ImGui.GetFrameHeight();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Label, GameModelPaths.CategoryLabel(_gameCategory));
        ImGui.SameLine(0, Theme.Gap);
        ImGui.AlignTextToFramePadding();
        var total = state.Entries.Length;
        ImGui.TextColored(Theme.Muted, state.Loading ? "scanning…"
            : visible.Count == total ? (total == 1 ? "1 model" : $"{total:N0} models")
            : $"{visible.Count:N0} of {total:N0} models");

        var ticked = _gameSelection.Count;
        var exportLabel = ticked == 0 ? "Export selected…" : $"Export selected ({ticked:N0})…";
        var right = ImGui.CalcTextSize(exportLabel).X + style.FramePadding.X * 2 + (frame + style.ItemSpacing.X) * 2;
        ImGui.SameLine(Math.Max(ImGui.GetCursorPosX(), ImGui.GetContentRegionMax().X - right));
        if (Widgets.IconButton("##game-rescan", FontAwesomeIcon.Sync, "Scan the game's files for this category again", !state.Loading))
            browser.Rescan(_gameCategory);
        ImGui.SameLine();
        if (Widgets.IconButton("##game-selection", FontAwesomeIcon.CheckSquare, "Tick or untick the shown models"))
            ImGui.OpenPopup(GameSelectionMenu);
        DrawGameSelectionMenu(visible);
        ImGui.SameLine();
        using (ImRaii.Disabled(ticked == 0 || _gameExport is not { Busy: false }))
        {
            if (ImGui.Button(exportLabel))
                OpenGameExport(_gameSelection.Entries);
        }
        if (ticked == 0 && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Tick models to export their files to a folder. Shift-click ticks a range.");
        ImGui.Separator();
    }

    private void DrawGameSelectionMenu(IReadOnlyList<GameModelEntry> visible)
    {
        using var popup = ImRaii.Popup(GameSelectionMenu);
        if (!popup.Success)
            return;
        if (ImGui.MenuItem(visible.Count == 1 ? "Tick the shown model" : $"Tick all {visible.Count:N0} shown models"))
            _gameSelection.SetAll(visible, true);
        if (ImGui.MenuItem("Untick the shown models"))
            _gameSelection.SetAll(visible, false);
        if (ImGui.MenuItem("Invert the shown models"))
            _gameSelection.Invert(visible);
        ImGui.Separator();
        using (ImRaii.Disabled(_gameSelection.Count == 0))
        {
            if (ImGui.MenuItem($"Clear the whole selection ({_gameSelection.Count:N0})"))
                _gameSelection.Clear();
            if (ImGui.MenuItem("Copy the ticked game paths"))
                ImGui.SetClipboardText(string.Join('\n', _gameSelection.Entries.Select(entry => entry.GamePath)));
        }
    }

    private void DrawGameFilterBar(GameFileCategoryState state, GameFileNames names)
    {
        ImGui.Spacing();
        Widgets.SearchBox("##game-search", ref _gameSearchText, "Search item names, IDs, races and paths");
        if (_gameCategory != GameFileCategory.Weapon)
        {
            ImGui.SetNextItemWidth(Theme.Scaled(200));
            using (var combo = ImRaii.Combo("##game-races", GameRaceFilterLabel()))
            {
                if (combo.Success)
                {
                    if (ImGui.Selectable("Player races", _gameFilter.Races == GameRaceMode.Player))
                        _gameFilter.Races = GameRaceMode.Player;
                    if (ImGui.Selectable("NPC races", _gameFilter.Races == GameRaceMode.Npc))
                        _gameFilter.Races = GameRaceMode.Npc;
                    if (ImGui.Selectable("All races", _gameFilter.Races == GameRaceMode.All))
                        _gameFilter.Races = GameRaceMode.All;
                    ImGui.Separator();
                    foreach (var race in GameRaces.All)
                    {
                        if (!ImGui.Selectable($"{race.Label}  ({race.Id})", _gameFilter.Races == GameRaceMode.One && _gameFilter.Race == race.Code))
                            continue;
                        _gameFilter.Races = GameRaceMode.One;
                        _gameFilter.Race = race.Code;
                    }
                }
            }
            ImGui.SameLine(0, Theme.Gap);
        }

        var slots = GameModelPaths.Slots(_gameCategory);
        if (slots.Length > 1)
        {
            var counts = GameSlotCounts(state.Entries);
            if (Widgets.Chip("All slots", state.Entries.Length, _gameFilter.Slot.Length == 0))
                _gameFilter.Slot = string.Empty;
            foreach (var slot in slots)
            {
                ImGui.SameLine(0, Theme.Gap);
                if (Widgets.Chip(GameModelPaths.SlotLabel(slot), counts.GetValueOrDefault(slot), _gameFilter.Slot == slot))
                    _gameFilter.Slot = _gameFilter.Slot == slot ? string.Empty : slot;
            }
            ImGui.SameLine(0, Theme.Scaled(12));
        }

        if (_gameCategory == GameFileCategory.Hair)
        {
            var selectable = _gameFilter.SelectableOnly;
            using (ImRaii.Disabled(!names.HasHairstyles))
            {
                if (ImGui.Checkbox("Player-selectable only", ref selectable))
                    _gameFilter.SelectableOnly = selectable;
            }
            ImGui.SameLine(0, Theme.Gap);
            Widgets.HelpTip("Only hairstyles players of the model's race can pick in character creation or unlock with an item. " +
                            "The player race folders also hold hairstyles only NPCs wear.");
        }
        else if (_gameCategory is GameFileCategory.Equipment or GameFileCategory.Accessory or GameFileCategory.Weapon)
        {
            var named = _gameFilter.NamedOnly;
            if (ImGui.Checkbox("Items only", ref named))
                _gameFilter.NamedOnly = named;
            ImGui.SameLine(0, Theme.Gap);
            Widgets.HelpTip("Only models an item uses. The others are worn by NPCs or unused.");
        }
        else if (_gameCategory == GameFileCategory.Face)
        {
            Widgets.HelpTip("Player races show the faces players can pick in character creation. " +
                            "The player race folders also hold faces only NPCs wear, which NPC races show.");
        }
        else
        {
            ImGui.NewLine();
        }
    }

    private string GameRaceFilterLabel() => _gameFilter.Races switch
    {
        GameRaceMode.Player => "Player races",
        GameRaceMode.Npc => "NPC races",
        GameRaceMode.One => GameRaces.Label(_gameFilter.Race),
        _ => "All races",
    };

    private IReadOnlyDictionary<string, int> GameSlotCounts(ImmutableArray<GameModelEntry> entries)
    {
        if (entries == _gameSlotCountSource)
            return _gameSlotCounts;
        _gameSlotCountSource = entries;
        _gameSlotCounts = entries.GroupBy(entry => entry.Id.Slot).ToDictionary(group => group.Key, group => group.Count());
        return _gameSlotCounts;
    }

    /// <summary> Every Game Files row imports and edits for the first on-screen character, as the Mod Browser does. </summary>
    private ActorView GameFilesActor()
    {
        var index = _onScreen.Items.FirstOrDefault()?.ObjectIndex ?? 0;
        if (_gameActor is null || _gameActor.ImportObjectIndex != index)
            _gameActor = new ActorView(null, "Game Files", string.Empty, [], index);
        return _gameActor;
    }

    private ResourceView GameFileView(string gamePath)
    {
        if (_gameViews.TryGetValue(gamePath, out var view))
            return view;
        if (_gameViews.Count >= 4096)
            _gameViews.Clear();
        return _gameViews[gamePath] = ResourceViews.GameFile(gamePath, gamePath[(gamePath.LastIndexOf('/') + 1)..]);
    }

    private void DrawGameTable(GameFileNames names, IReadOnlyList<GameModelEntry> visible, IReadOnlyList<GameFileRow> rows)
    {
        var actor = GameFilesActor();
        using var table = ImRaii.Table("##game-files", 6, ResourceTableFlags | ImGuiTableFlags.ScrollY,
            new Vector2(0, Math.Max(1, ImGui.GetContentRegionAvail().Y)));
        if (!table.Success)
            return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("##tick", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoHide | ImGuiTableColumnFlags.NoResize,
            ImGui.GetFrameHeight());
        ImGui.TableSetupColumn("Model", ImGuiTableColumnFlags.WidthStretch, .36f);
        ImGui.TableSetupColumn("Race", ImGuiTableColumnFlags.WidthStretch, .15f);
        ImGui.TableSetupColumn("ID", ImGuiTableColumnFlags.WidthStretch, .11f);
        ImGui.TableSetupColumn("Path", ImGuiTableColumnFlags.WidthStretch, .38f);
        ImGui.TableSetupColumn("##actions", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoHide, Theme.Scaled(58));
        ImGui.TableHeadersRow();

        // Thousands of rows: only the visible ones are drawn. The clipper measures the first row.
        var clipper = ImGui.ImGuiListClipper();
        try
        {
            clipper.Begin(rows.Count, -1);
            while (clipper.Step())
                for (var index = clipper.DisplayStart; index < clipper.DisplayEnd; index++)
                    DrawGameRow(actor, names, visible, rows[index]);
            clipper.End();
        }
        finally
        {
            clipper.Destroy();
        }
    }

    private void DrawGameRow(ActorView actor, GameFileNames names, IReadOnlyList<GameModelEntry> visible, GameFileRow row)
    {
        using var id = ImRaii.PushId(SafeId($"{row.Entry.GamePath}|{row.Kind}|{row.GamePath}|{row.Label}"));
        ImGui.TableNextRow();
        if (row.Kind == GameFileRowKind.Model)
            DrawGameModelRow(actor, names, visible, row.Entry);
        else
            DrawGameDetailRow(actor, row);
    }

    private void DrawGameModelRow(ActorView actor, GameFileNames names, IReadOnlyList<GameModelEntry> visible, GameModelEntry entry)
    {
        var view = GameFileView(entry.GamePath);
        var frame = ImGui.GetFrameHeight();
        TintRow(view);
        ImGui.TableSetColumnIndex(0);
        var ticked = _gameSelection.Contains(entry);
        if (ImGui.Checkbox("##tick", ref ticked))
        {
            if (ImGui.GetIO().KeyShift)
                _gameSelection.SetRange(visible, entry, ticked);
            else
                _gameSelection.Set(entry, ticked);
        }

        ImGui.TableSetColumnIndex(1);
        var open = _gameRows.IsOpen(entry);
        if (Widgets.GhostIconButton("##open", open ? FontAwesomeIcon.CaretDown : FontAwesomeIcon.CaretRight,
                open ? "Hide its materials, textures and skeleton" : "Show its materials, textures and skeleton", new Vector2(frame)))
            _gameRows.Toggle(entry);
        ImGui.SameLine(0, Theme.Gap);
        DrawRowGlyph(view);
        ImGui.SameLine(0, Theme.Gap);
        var itemNames = names.For(entry.Id);
        var title = GameModelTitle(entry.Id, itemNames);
        var labelWidth = itemNames.Length > 1 ? ImGui.CalcTextSize(title).X : 0;
        if (ImGui.Selectable($"{title}##label", false, ImGuiSelectableFlags.None, new Vector2(labelWidth, frame)))
            _gameRows.Toggle(entry);
        var hovered = ImGui.IsItemHovered();
        ImGui.OpenPopupOnItemClick(RowMenuPopup, ImGuiPopupFlags.MouseButtonRight);
        if (hovered)
            DrawRowHover(view, title, "game:" + entry.GamePath);
        if (itemNames.Length > 1)
        {
            ImGui.SameLine(0, Theme.Gap);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Hint, $"+{itemNames.Length - 1}");
            if (ImGui.IsItemHovered())
            {
                using var tooltip = ImRaii.Tooltip();
                foreach (var name in itemNames.Take(24))
                    ImGui.TextUnformatted(name);
                if (itemNames.Length > 24)
                    ImGui.TextColored(Theme.Hint, $"and {itemNames.Length - 24} more");
            }
        }

        ImGui.TableSetColumnIndex(2);
        ImGui.AlignTextToFramePadding();
        if (entry.Id.Human)
            ImGui.TextColored(names.PlayerModel(entry.Id) ? Theme.Text : Theme.Muted, GameRaces.Label(entry.Id.Race));
        ImGui.TableSetColumnIndex(3);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Muted, entry.Id.Category is GameFileCategory.Equipment or GameFileCategory.Accessory or GameFileCategory.Body
            ? $"{entry.Id.IdLabel} {entry.Id.Slot}"
            : entry.Id.IdLabel);
        ImGui.TableSetColumnIndex(4);
        Widgets.PathText(entry.GamePath, entry.GamePath);
        ImGui.TableSetColumnIndex(5);
        DrawRowActions(actor, view);
        DrawRowMenu(actor, view, () =>
        {
            using (ImRaii.Disabled(_gameExport is not { Busy: false }))
            {
                if (ImGui.MenuItem("Export this model's files…"))
                    OpenGameExport([entry]);
            }
            if (ImGui.MenuItem(ticked ? "Untick for export" : "Tick for export"))
                _gameSelection.Set(entry, !ticked);
        });
    }

    private void DrawGameDetailRow(ActorView actor, GameFileRow row)
    {
        var frame = ImGui.GetFrameHeight();
        ImGui.TableSetColumnIndex(1);
        // Materials sit under the model's glyph, textures one step further in. The spacer is a frame
        // tall, so every row has the height the clipper measured on the first.
        var depth = row.Kind == GameFileRowKind.Texture ? 2 : 1;
        ImGui.Dummy(new Vector2(depth * (frame + Theme.Gap), frame));
        ImGui.SameLine(0, 0);
        ImGui.AlignTextToFramePadding();
        var file = row.Found && row.GamePath.Length > 0 && row.Kind is GameFileRowKind.Material or GameFileRowKind.Texture;
        if (file)
        {
            var view = GameFileView(row.GamePath);
            TintRow(view);
            DrawRowGlyph(view);
            ImGui.SameLine(0, Theme.Gap);
            ImGui.Selectable($"{row.Label}##label", false, ImGuiSelectableFlags.None, new Vector2(0, frame));
            var hovered = ImGui.IsItemHovered();
            ImGui.OpenPopupOnItemClick(RowMenuPopup, ImGuiPopupFlags.MouseButtonRight);
            if (hovered)
                DrawRowHover(view, row.Kind == GameFileRowKind.Texture ? GameTextureLabel(row.Detail) : "Material", "game:" + row.GamePath);
            ImGui.TableSetColumnIndex(3);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Hint, row.Kind == GameFileRowKind.Texture ? GameTextureLabel(row.Detail) : row.Detail);
            ImGui.TableSetColumnIndex(4);
            Widgets.PathText(row.GamePath, row.GamePath);
            ImGui.TableSetColumnIndex(5);
            DrawRowActions(actor, view);
            DrawRowMenu(actor, view);
            return;
        }

        var icon = row.Kind switch
        {
            GameFileRowKind.Skeleton => FontAwesomeIcon.Bone,
            GameFileRowKind.Note => row.Found ? FontAwesomeIcon.Sync : FontAwesomeIcon.ExclamationTriangle,
            _ => FontAwesomeIcon.QuestionCircle,
        };
        Widgets.Icon(icon, row.Found ? Theme.Muted : Theme.Warning);
        ImGui.SameLine(0, Theme.Gap);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(row.Found ? Theme.Muted : Theme.Warning, row.Label);
        if (row.Detail.Length > 0 && ImGui.IsItemHovered())
            ImGui.SetTooltip(row.Detail);
        if (row.Kind != GameFileRowKind.Note && row.Detail.Length > 0)
        {
            ImGui.TableSetColumnIndex(3);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Warning, row.Detail);
        }
        if (row.GamePath.Length > 0)
        {
            ImGui.TableSetColumnIndex(4);
            Widgets.PathText(row.GamePath, row.GamePath);
        }
    }

    /// <summary> A model's name: its first item, unlock item or what it is. </summary>
    private static string GameModelTitle(GameModelId id, ImmutableArray<string> itemNames)
    {
        if (itemNames.Length > 0)
            return itemNames[0];
        return id.Category switch
        {
            GameFileCategory.Hair => $"Hairstyle {id.Id}",
            GameFileCategory.Face => $"Face {id.Id}",
            GameFileCategory.Tail => $"Tail {id.Id}",
            GameFileCategory.Ears => $"Ears {id.Id}",
            GameFileCategory.Body => $"Body {id.Id} ({GameModelPaths.SlotLabel(id.Slot).ToLowerInvariant()})",
            GameFileCategory.Weapon => $"Weapon {id.Id}, body {id.SecondaryId}",
            _ => $"{GameModelPaths.SlotLabel(id.Slot)} {id.Id}",
        };
    }

    private static string GameTextureLabel(string usage) => usage switch
    {
        "normal" => "Normal map",
        "mask" => "Mask",
        "index" => "Index map",
        "diffuse" => "Diffuse",
        "specular" => "Specular",
        "occlusion" => "Occlusion",
        "flow" => "Flow map",
        "decal" => "Decal",
        _ => "Texture",
    };
}
