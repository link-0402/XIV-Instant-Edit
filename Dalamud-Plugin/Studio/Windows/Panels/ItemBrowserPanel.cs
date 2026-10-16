using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Services;
using InstantEdit.Studio.Windows.UI;

namespace InstantEdit.Studio.Windows.Panels;

/// <summary>
/// The "Add from game" view of both modes: browse the game's items and features and add them to the
/// modpack (Build mode) or the open mod (Edit mode) with the + button, or by dragging a tile onto the
/// list on the left.
/// Gear is browsed by slot and item name; character features (hair, face, tail, ears) by
/// race and id.
///
/// Lists draw only the tiles in view (a slot can hold thousands of items), and the
/// search waits for typing to pause before re-filtering.
/// </summary>
internal sealed class ItemBrowserPanel
{
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>Weapons are offered in Build mode only: Edit mode's mods are opened from their game files, which it lists by gear and feature.</summary>
    private readonly SubjectKind[] _kinds;
    private readonly string[] _kindLabels;

    private readonly StudioHost _host;
    private readonly IItemShelf _session;

    private SubjectKind _kind;

    // ── Weapons ──────────────────────────────────────────────────────────────
    private string[] _weaponCategories = { "All weapons" };
    private int _weaponCategory;
    private List<WeaponModel> _filteredWeapons = new();

    // ── Animations ───────────────────────────────────────────────────────────
    private static readonly string[] AnimationLists = { "Emotes", "Other animations" };
    private int _animationList;
    private List<AnimationTarget> _filteredAnimations = new();
    private object? _animationsFrom;

    // ── Gear ─────────────────────────────────────────────────────────────────
    private readonly EquipSlot[] _allSlots = SlotInfo.AllSlots.ToArray();
    private readonly string[]    _slotLabels;
    private int _slotIndex;
    private List<GameDataService.GameItem> _allItems = new();
    private List<GameDataService.GameItem> _filtered = new();

    // ── Features ─────────────────────────────────────────────────────────────
    private string _featureRace;
    private List<ushort> _featureIds = new();
    private object? _filteredFrom;

    // ── Shared ───────────────────────────────────────────────────────────────
    private string _search;
    private bool _configuredOnly;
    private readonly Stopwatch _sinceTyped = new();
    private bool _filterPending;
    private bool _loaded;
    private bool _scrollToSelection;

    public ItemBrowserPanel(StudioHost host, IItemShelf session)
    {
        _host  = host;
        _session = session;
        _slotLabels = _allSlots.Select(s => SlotInfo.DisplayLabelMap[s]).ToArray();

        bool build = session is PortSession;
        _kinds = build
            ? new[] { SubjectKind.Gear, SubjectKind.Weapon, SubjectKind.Hair, SubjectKind.Face, SubjectKind.Tail, SubjectKind.Ear, SubjectKind.Animation }
            : new[] { SubjectKind.Gear, SubjectKind.Hair, SubjectKind.Face, SubjectKind.Tail, SubjectKind.Ear };
        _kindLabels = _kinds.Select(KindTab).ToArray();

        var cfg = host.Configuration;
        // Saved as the kind itself; older settings saved the tab index, which matched the kinds then.
        _kind        = Array.IndexOf(_kinds, (SubjectKind)cfg.LastBrowserKind) >= 0 ? (SubjectKind)cfg.LastBrowserKind : SubjectKind.Gear;
        _slotIndex   = Math.Clamp(cfg.LastSlotIndex, 0, _allSlots.Length - 1);
        _search      = cfg.LastItemSearch;
        _featureRace = cfg.LastFeatureRace;
    }

    public void Draw()
    {
        if (!_host.GameData.IsItemCacheReady)
        {
            Gui.SectionHeader("Replace");
            Waiting("Reading the game's item list");
            return;
        }

        if (!_loaded)
            FirstLoad();

        Gui.SectionHeader("Browse the game", _session is PortSession
            ? "Press + on anything, or drag it onto the modpack list on the left, to add it. Set-ups are saved per item, so removing and re-adding one keeps its work."
            : $"Press + on gear or a feature, or drag it onto the list on the left, to add it to the {_session.ShelfName}: its game files are listed there, ready for files of your own.");
        DrawKindSwitch();
        ImGui.Spacing();

        bool listReady = _kind switch
        {
            SubjectKind.Gear      => DrawGearFilters(),
            SubjectKind.Weapon    => DrawWeaponFilters(),
            SubjectKind.Animation => DrawAnimationFilters(),
            _                     => DrawFeatureFilters(),
        };
        if (!listReady)
            return;

        DrawSearchRow();
        ImGui.Spacing();

        float footerH = ImGui.GetTextLineHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y;

        if (ImGui.BeginChild("##SubjectList", new Vector2(-1, -footerH), true))
        {
            switch (_kind)
            {
                case SubjectKind.Gear:      DrawGearList();      break;
                case SubjectKind.Weapon:    DrawWeaponList();    break;
                case SubjectKind.Animation: DrawAnimationList(); break;
                default:                    DrawFeatureList();   break;
            }
        }
        ImGui.EndChild();

        var (shown, total) = _kind switch
        {
            SubjectKind.Gear      => (_filtered.Count, _allItems.Count),
            SubjectKind.Weapon    => (_filteredWeapons.Count, WeaponsInCategory().Count()),
            SubjectKind.Animation => (_filteredAnimations.Count, _host.GameData.AnimationTargets(_animationList == 0)?.Count ?? 0),
            _                     => (_featureIds.Count, AllIdsForRace().Count),
        };
        Gui.Hint(shown == total ? $"{total} {Noun(total)}" : $"{shown} of {total} {Noun(total)}");
        ImGui.SameLine();
        var inPack = $"{_session.ShelfCount} in the {_session.ShelfName}";
        Gui.AlignRight(ImGui.CalcTextSize(inPack).X);
        ImGui.TextColored(Theme.Accent, inPack);
    }

    private uint SelectedKey => _session.ShelfSelection;

    /// <summary>The row last clicked in this browser (highlighted even if it is not in the pack).</summary>
    private uint _picked;

    /// <summary>The subject of the last clicked tile, for the browser's inspector.</summary>
    public PortSubject? Highlighted { get; private set; }

    private string Noun(int n) => _kind switch
    {
        SubjectKind.Gear      => "items",
        SubjectKind.Weapon    => "weapon models",
        SubjectKind.Animation => _animationList == 0 ? "emotes" : "animations",
        _                     => $"{FeatureNaming.KindLabel(_kind).ToLowerInvariant()} ids",
    };

    private static string KindTab(SubjectKind kind) => kind switch
    {
        SubjectKind.Gear      => "Gear",
        SubjectKind.Weapon    => "Weapons",
        SubjectKind.Ear       => "Ears",
        SubjectKind.Animation => "Animations",
        _                     => FeatureNaming.KindLabel(kind),
    };

    private static void Waiting(string text)
    {
        Gui.Icon(FontAwesomeIcon.Hourglass, Theme.Muted);
        ImGui.SameLine();
        Gui.Hint(text);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Kind switch and filters
    // ─────────────────────────────────────────────────────────────────────────

    private void DrawKindSwitch()
    {
        float spacing = ImGui.GetStyle().ItemSpacing.X * 0.5f;
        float w = (ImGui.GetContentRegionAvail().X - spacing * (_kinds.Length - 1)) / _kinds.Length;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(spacing, ImGui.GetStyle().ItemSpacing.Y));
        for (int i = 0; i < _kinds.Length; i++)
        {
            if (i > 0) ImGui.SameLine();
            bool active = _kind == _kinds[i];
            ImGui.PushStyleColor(ImGuiCol.Button, active ? Theme.Accent with { W = 0.45f } : Theme.Band);
            ImGui.PushStyleColor(ImGuiCol.Text,   active ? new Vector4(1, 1, 1, 1) : Theme.Muted);
            if (ImGui.Button($"{_kindLabels[i]}##kind", new Vector2(w, 0)) && !active)
            {
                _kind = _kinds[i];
                Reload();
                SaveBrowserState();
            }
            ImGui.PopStyleColor(2);
        }
        ImGui.PopStyleVar();
    }

    private bool DrawGearFilters()
    {
        ImGui.SetNextItemWidth(-1);
        if (ImGui.Combo("##Slot", ref _slotIndex, _slotLabels, _slotLabels.Length))
        {
            Reload();
            SaveBrowserState();
        }
        Gui.Tooltip("Equipment slot");
        return true;
    }

    private bool DrawWeaponFilters()
    {
        ImGui.SetNextItemWidth(-1);
        if (ImGui.Combo("##WeaponCategory", ref _weaponCategory, _weaponCategories, _weaponCategories.Length))
            ApplyFilter();
        Gui.Tooltip("Weapon class");
        return true;
    }

    /// <summary>Emotes or every other animation. Returns false while the game's sheets are still being read.</summary>
    private bool DrawAnimationFilters()
    {
        ImGui.SetNextItemWidth(-1);
        if (ImGui.Combo("##AnimationList", ref _animationList, AnimationLists, AnimationLists.Length))
            ApplyFilter();
        Gui.Tooltip("Emotes come with their start and end. Other animations are every other action timeline the game has, by name.");

        var targets = _host.GameData.AnimationTargets(_animationList == 0);
        if (targets == null)
        {
            Waiting("Reading the game's emotes and animations");
            return false;
        }
        if (!ReferenceEquals(targets, _animationsFrom))
        {
            _animationsFrom = targets;
            ApplyFilter();
        }
        return true;
    }

    /// <summary>Race picker for features. Returns false while the game's files are still being scanned.</summary>
    private bool DrawFeatureFilters()
    {
        var ids = _host.GameData.GetFeatureIds(_kind);
        if (ids == null)
        {
            Waiting($"Finding every {FeatureNaming.KindLabel(_kind).ToLowerInvariant()} in the game");
            return false;
        }
        if (!ReferenceEquals(ids, _filteredFrom))
        {
            // The scan finished (or the kind changed) since the list was last filtered.
            _filteredFrom = ids;
            ApplyFilter();
        }
        if (ids.Count == 0)
        {
            Gui.HintWrapped($"No {FeatureNaming.KindLabel(_kind).ToLowerInvariant()} models were found in the game files.");
            return false;
        }

        var races = ids.Keys.OrderBy(rg => rg.RaceCode, StringComparer.Ordinal).ToList();
        int raceIdx = races.FindIndex(rg => rg.RaceCode == _featureRace);
        if (raceIdx < 0)
        {
            raceIdx = 0;
            _featureRace = races[0].RaceCode;
            ApplyFilter();
        }

        var labels = races.Select(rg => rg.DisplayName).ToArray();
        ImGui.SetNextItemWidth(-1);
        if (ImGui.Combo("##FeatureRace", ref raceIdx, labels, labels.Length))
        {
            _featureRace = races[raceIdx].RaceCode;
            ApplyFilter();
            SaveBrowserState();
        }
        Gui.Tooltip(_kind == SubjectKind.Hair
            ? "Race the hairstyle is opened for. More races can be added in the item's set-up."
            : "Race and gender this feature belongs to.");
        return true;
    }

    private void DrawSearchRow()
    {
        var hint = _kind switch
        {
            SubjectKind.Gear or SubjectKind.Weapon => "Search name or model id",
            SubjectKind.Animation                  => "Search name, command or animation key",
            _                                      => "Search id",
        };
        ImGui.SetNextItemWidth(-(ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.X));
        if (ImGui.InputTextWithHint("##Search", hint, ref _search, 256))
        {
            _filterPending = true;
            _sinceTyped.Restart();
        }
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, _configuredOnly ? Theme.Accent : Theme.Muted);
        if (Gui.IconButton(FontAwesomeIcon.Star, "ConfiguredOnly",
                _configuredOnly ? "Showing only what you have set up — click to show all" : "Show only what you have set up"))
        {
            _configuredOnly = !_configuredOnly;
            ApplyFilter();
        }
        ImGui.PopStyleColor();

        if (_filterPending && _sinceTyped.Elapsed >= SearchDelay)
        {
            _filterPending = false;
            ApplyFilter();
            SaveBrowserState();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Lists
    // ─────────────────────────────────────────────────────────────────────────

    private void DrawGearList()
    {
        if (_filtered.Count == 0)
        {
            Gui.Hint(_configuredOnly ? "(no set-up items in this slot)" : "(no items match)");
            return;
        }

        int selected = _scrollToSelection ? _filtered.FindIndex(i => i.RowId == SelectedKey) : -1;
        DrawTiles(_filtered.Count, selected, i =>
        {
            var item = _filtered[i];
            DrawTile(item.RowId, item.Name, $"{SlotInfo.DisplayLabelMap[item.Slot]} · {item.ModelIdDisplay}", _session.HasWork(item.RowId),
                () => new GearSubject(item), item.Icon,
                $"{item.Name}\nModel {item.ModelIdDisplay}");
        });
    }

    private void DrawWeaponList()
    {
        if (_filteredWeapons.Count == 0)
        {
            Gui.Hint(_configuredOnly ? "(no set-up weapons here)" : "(no weapons match)");
            return;
        }

        int selected = _scrollToSelection ? _filteredWeapons.FindIndex(w => new WeaponSubject(w).Key == SelectedKey) : -1;
        DrawTiles(_filteredWeapons.Count, selected, i =>
        {
            var weapon = _filteredWeapons[i];
            var subject = new WeaponSubject(weapon);
            DrawTile(subject.Key, subject.DisplayName,
                $"{(weapon.Category.Length > 0 ? weapon.Category : "Weapon")} · {weapon.IdDisplay}{(weapon.OffHand ? " · off hand" : "")}",
                _session.HasWork(subject.Key), () => subject, weapon.Icon,
                $"{subject.DisplayName}\n{subject.ModelGamePath(default)}");
        });
    }

    private void DrawAnimationList()
    {
        if (_filteredAnimations.Count == 0)
        {
            Gui.Hint(_configuredOnly ? "(nothing set up here)" : "(nothing matches)");
            return;
        }

        int selected = _scrollToSelection ? _filteredAnimations.FindIndex(t => t.Key == SelectedKey) : -1;
        DrawTiles(_filteredAnimations.Count, selected, i =>
        {
            var target = _filteredAnimations[i];
            int editable = target.Timelines.Count(t => t.Editable);
            var subtitle = target.Command.Length > 0
                ? $"{target.Command} · {editable} animation(s)"
                : editable > 0 ? target.Timelines[0].Key : target.Timelines[0].Reason ?? "can't be replaced";
            var lines = target.Timelines.Select(t => t.Editable ? t.Key : $"{t.Key}  ({t.Reason})");
            DrawTile(target.Key, target.Name, subtitle, _session.HasWork(target.Key), () => new AnimationSubject(target), target.Icon,
                $"{target.Name}\n{string.Join("\n", lines)}");
        });
    }

    private void DrawFeatureList()
    {
        if (_featureIds.Count == 0)
        {
            Gui.Hint(_configuredOnly ? "(nothing set up for this race)" : "(no ids match)");
            return;
        }

        var race = CurrentRace();
        if (race == null) return;

        var label = FeatureNaming.KindLabel(_kind);
        int selected = -1;
        if (_scrollToSelection)
            selected = _featureIds.FindIndex(id => new FeatureSubject(_kind, race.Value, id).Key == SelectedKey);

        DrawTiles(_featureIds.Count, selected, i =>
        {
            var subject = new FeatureSubject(_kind, race.Value, _featureIds[i]);
            DrawTile(subject.Key, $"{label} {subject.Id}", $"{race.Value.DisplayName} · {subject.IdDisplay}", _session.HasWork(subject.Key),
                () => subject, 0,
                $"{subject.DisplayName}\n{subject.ModelGamePath(race.Value)}");
        });
    }

    /// <summary>Submits only the tiles in view — a slot can hold thousands of items.</summary>
    private void DrawTiles(int count, int scrollTo, Action<int> drawTile)
    {
        int reveal = -1;
        if (_scrollToSelection)
        {
            _scrollToSelection = false;
            reveal = scrollTo;
        }

        var heights = Enumerable.Repeat(Tile.Height(), count).ToList();
        VirtualRows.Draw(heights, drawTile, reveal);
    }

    /// <summary>
    /// One browser tile. Clicking only highlights it (or, for something already in the pack, shows
    /// its set-up); adding takes the + button or a drag onto the modpack list, so a stray click never
    /// changes the modpack.
    /// </summary>
    private void DrawTile(uint key, string name, string subtitle, bool configured, Func<PortSubject> subject, uint icon, string tooltip)
    {
        bool inPack = _session.OnShelf(key);
        bool sel = key == _picked || key == SelectedKey;

        var badges = new List<Badge>();
        if (inPack)
            badges.Add(new Badge($"in the {_session.ShelfName}", Theme.Good));
        else if (configured)
            badges.Add(new Badge("set up before", Theme.Accent, "You have a set-up saved for this; adding it again brings it back."));

        var add = new TileAction(inPack ? FontAwesomeIcon.ArrowRight : FontAwesomeIcon.Plus,
            inPack ? $"In this {_session.ShelfName} — open it" : $"Add to this {_session.ShelfName}",
            () =>
            {
                _picked = key;
                Highlighted = subject();
                _session.AddToShelf(Highlighted);
            }, inPack ? Theme.Good : Theme.Accent);

        var result = Tile.Draw(new TileSpec
        {
            Id          = $"k{key}",
            Title       = name,
            TitleColor  = configured && !inPack ? Theme.Accent : null,
            Subtitle    = subtitle,
            Icon        = _kind switch
            {
                SubjectKind.Gear      => FontAwesomeIcon.Tshirt,
                SubjectKind.Weapon    => FontAwesomeIcon.ShieldAlt,
                SubjectKind.Animation => FontAwesomeIcon.Running,
                _                     => FontAwesomeIcon.User,
            },
            IconColor   = inPack ? Theme.Good : Theme.Muted,
            DrawThumb   = icon != 0 ? side => GameIcons.Draw(icon, side) : null,
            Badges      = badges,
            Actions     = new[] { add },
            Selected    = sel,
            AfterButton = () => SubjectDrop.Source(subject(), inPack, _session.ShelfName),
            Tooltip     = tooltip + (inPack ? $"\n\nAlready in the {_session.ShelfName}." : "\n\nAdd it with + or by dragging it onto the list on the left."),
        }, new Vector2(ImGui.GetContentRegionAvail().X, Tile.Height()));

        if (result.Clicked)
        {
            _picked = key;
            Highlighted = subject();
        }
        if (result.DoubleClicked)
            _session.AddToShelf(subject());
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Loading / filtering
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Restores the last subject once the item cache is ready, switching kind, slot or race to show it.</summary>
    private void FirstLoad()
    {
        _loaded = true;
        var subject = PortSubject.FromKey(_host.Configuration.LastItemRowId, _host.GameData);
        switch (subject)
        {
            case GearSubject gear:
                _kind = SubjectKind.Gear;
                int slotIdx = Array.IndexOf(_allSlots, gear.Item.Slot);
                if (slotIdx >= 0) _slotIndex = slotIdx;
                break;
            case FeatureSubject feature:
                _kind = feature.Kind;
                _featureRace = feature.Race.RaceCode;
                break;
            case WeaponSubject when Array.IndexOf(_kinds, SubjectKind.Weapon) >= 0:
                _kind = SubjectKind.Weapon;
                break;
            case AnimationSubject animation when Array.IndexOf(_kinds, SubjectKind.Animation) >= 0:
                _kind = SubjectKind.Animation;
                _animationList = PortSubject.KeyTag(animation.Key) == KeyTagKind.Emote ? 0 : 1;
                break;
        }

        Reload();
        _scrollToSelection = subject != null;
    }

    private void Reload()
    {
        if (_kind == SubjectKind.Gear)
            _allItems = _host.GameData.GetAllItemsForSlot(_allSlots[_slotIndex]);
        else if (_kind == SubjectKind.Weapon)
        {
            var categories = _host.GameData.GetWeapons().Select(w => w.Category).Where(c => c.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
            _weaponCategories = new[] { "All weapons" }.Concat(categories).ToArray();
            _weaponCategory = Math.Clamp(_weaponCategory, 0, _weaponCategories.Length - 1);
        }
        ApplyFilter();
    }

    private IEnumerable<WeaponModel> WeaponsInCategory()
    {
        var all = _host.GameData.GetWeapons();
        return _weaponCategory <= 0 || _weaponCategory >= _weaponCategories.Length
            ? all
            : all.Where(w => string.Equals(w.Category, _weaponCategories[_weaponCategory], StringComparison.OrdinalIgnoreCase));
    }

    private RaceGender? CurrentRace()
    {
        foreach (var rg in RaceInfo.AllRaces)
            if (rg.RaceCode == _featureRace)
                return rg;
        return null;
    }

    private IReadOnlyList<ushort> AllIdsForRace()
    {
        var ids = _host.GameData.GetFeatureIds(_kind);
        var race = CurrentRace();
        return ids != null && race != null && ids.TryGetValue(race.Value, out var list) ? list : Array.Empty<ushort>();
    }

    private void ApplyFilter()
    {
        var cfg = _host.Configuration;
        var q = _search.Trim();

        if (_kind == SubjectKind.Animation)
        {
            IEnumerable<AnimationTarget> targets = _host.GameData.AnimationTargets(_animationList == 0) ?? Array.Empty<AnimationTarget>();
            if (_configuredOnly)
                targets = targets.Where(t => _session.HasWork(t.Key));
            if (q.Length > 0)
                targets = targets.Where(t => t.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                                          || t.Command.Contains(q, StringComparison.OrdinalIgnoreCase)
                                          || t.Timelines.Any(l => l.Key.Contains(q, StringComparison.OrdinalIgnoreCase)))
                                 .OrderBy(t => t.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0 : 1);
            _filteredAnimations = targets.ToList();
            return;
        }

        if (_kind == SubjectKind.Weapon)
        {
            IEnumerable<WeaponModel> weapons = WeaponsInCategory();
            if (_configuredOnly)
                weapons = weapons.Where(w => _session.HasWork(new WeaponSubject(w).Key));
            if (q.Length > 0)
                weapons = weapons.Where(w => w.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                                          || w.IdDisplay.Contains(q, StringComparison.OrdinalIgnoreCase))
                                 .OrderBy(w => w.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0 : 1);
            _filteredWeapons = weapons.ToList();
            return;
        }

        if (_kind != SubjectKind.Gear)
        {
            var race = CurrentRace();
            IEnumerable<ushort> ids = AllIdsForRace();
            if (race != null && _configuredOnly)
                ids = ids.Where(id => _session.HasWork(new FeatureSubject(_kind, race.Value, id).Key));
            if (q.Length > 0)
                ids = ids.Where(id => id.ToString().Contains(q, StringComparison.Ordinal) || id.ToString("D4").Contains(q, StringComparison.Ordinal));
            _featureIds = ids.ToList();
            return;
        }

        IEnumerable<GameDataService.GameItem> source = _allItems;
        if (_configuredOnly)
            source = source.Where(i => _session.HasWork(i.RowId));

        if (q.Length == 0)
        {
            _filtered = source as List<GameDataService.GameItem> ?? source.ToList();
            return;
        }

        bool StartsWithQ(GameDataService.GameItem i) =>
            i.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ||
            i.ModelIdPadded.StartsWith(q, StringComparison.OrdinalIgnoreCase) ||
            i.ModelIdDisplay.StartsWith(q, StringComparison.OrdinalIgnoreCase);
        bool ContainsQ(GameDataService.GameItem i) =>
            i.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            i.ModelIdPadded.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            i.ModelIdDisplay.Contains(q, StringComparison.OrdinalIgnoreCase);

        var list = source.ToList();
        var starts   = list.Where(StartsWithQ).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase);
        var contains = list.Where(i => !StartsWithQ(i) && ContainsQ(i)).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase);
        _filtered = starts.Concat(contains).ToList();
    }

    private void SaveBrowserState()
    {
        var cfg = _host.Configuration;
        cfg.LastBrowserKind = (int)_kind;
        cfg.LastSlotIndex   = _slotIndex;
        cfg.LastItemSearch  = _search;
        cfg.LastFeatureRace = _featureRace;
        cfg.Save();
    }
}
