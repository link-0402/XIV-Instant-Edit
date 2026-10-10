using System;
using System.Collections.Generic;
using System.Linq;
using InstantEdit.Studio.Models;

namespace InstantEdit.Studio.Services.Check;

/// <summary>Race coverage from EQDP: which model each race loads for the gear the mod ships, and the skin materials gear hands to its wearer.</summary>
internal sealed partial class ModCheck
{
    private readonly List<RaceData> _races = new();
    private readonly Dictionary<(ushort Race, bool Accessory), EqdpReader?> _eqdpFiles = new();

    /// <summary>The mod in the player's collection that provides a game path this mod lacks (filled by the collection check).</summary>
    private readonly Dictionary<string, string?> _external = new(StringComparer.Ordinal);

    private sealed class RaceData
    {
        public required string Label { get; init; }
        public required GameModelKind Kind { get; init; }
        public required ushort SetId { get; init; }
        public required EquipSlot Slot { get; init; }
        public required IReadOnlyList<ModelWalk> Models { get; init; }
        public required IReadOnlyList<ushort> Shipped { get; init; }
        public required Dictionary<ushort, int> Bits { get; init; }
        public Dictionary<ushort, ManipulationRef> ModEntries { get; init; } = new();
        public List<RaceRowData> Rows { get; } = new();
        public List<ushort> Dead { get; } = new();
        public bool Accessory => Kind == GameModelKind.Accessory;
    }

    private sealed record RaceRowData(ushort Race, ushort Resolved, bool UsesMod, bool Reroutable);

    private void BuildRaceTables()
    {
        var gear = _walks.Where(w => w.Info is { IsGear: true, Slot: not null } && RaceInfo.IsPlayableCode(w.Info.Race))
            .GroupBy(w => (w.Info!.Kind, w.Info.PrimaryId, Slot: w.Info.Slot!.Value));
        foreach (var group in gear)
        {
            var (kind, setId, slot) = group.Key;
            var accessory = kind == GameModelKind.Accessory;
            var containers = group.Select(w => w.Container).Append(_default).Distinct().ToArray();
            var modEntries = new Dictionary<ushort, ManipulationRef>();
            foreach (var container in containers)
            foreach (var manipulation in _model.Manipulations.Where(m => ReferenceEquals(m.Container, container) && m.Parsed.Eqdp is { } e && e.SetId == setId && e.Slot == slot))
                modEntries.TryAdd(manipulation.Parsed.Eqdp!.Race, manipulation);

            var bits = new Dictionary<ushort, int>();
            var available = true;
            foreach (var race in RaceInfo.PlayableCodes)
            {
                if (modEntries.TryGetValue(race, out var manipulation))
                {
                    bits[race] = manipulation.Parsed.Eqdp!.SlotBits;
                    continue;
                }
                if (Eqdp(race, accessory) is not { } file)
                {
                    available = false;
                    break;
                }
                bits[race] = (file.Entry(setId) >> SlotInfo.EqdpShift(slot)) & 3;
            }
            var label = $"{(accessory ? 'a' : 'e')}{setId:D4} {SlotInfo.DisplayLabelMap[slot]}";
            if (!available)
            {
                _notes.Add($"There is no EQDP data, so which races load {label} was not checked.");
                continue;
            }

            var shipped = group.Select(w => w.Info!.Race).Distinct().Order().ToArray();
            var data = new RaceData
            {
                Label = label, Kind = kind, SetId = setId, Slot = slot, Models = group.ToArray(), Shipped = shipped, Bits = bits, ModEntries = modEntries,
            };
            foreach (var race in RaceInfo.PlayableCodes)
            {
                var resolved = RaceInfo.ResolveModelRace(race, accessory, r => (bits[r] & 2) != 0);
                var uses = shipped.Contains(resolved);
                var reroutable = !uses && (bits[race] & 2) != 0
                              && shipped.Contains(RaceInfo.ResolveModelRace(race, accessory, r => r != race && (bits[r] & 2) != 0));
                data.Rows.Add(new RaceRowData(race, resolved, uses, reroutable));
            }
            data.Dead.AddRange(shipped.Where(race => data.Rows.All(row => row.Resolved != race)));
            _races.Add(data);
        }
    }

    private EqdpReader? Eqdp(ushort race, bool accessory)
    {
        if (!_eqdpFiles.TryGetValue((race, accessory), out var file))
            _eqdpFiles[(race, accessory)] = file = _game.TryReadBytes(ModelNaming.EqdpPath(race, accessory)) is { } bytes ? EqdpReader.Parse(bytes) : null;
        return file;
    }

    /// <summary>Every skin material gear asks for, per race that wears this mod's model.</summary>
    private IEnumerable<(ModelWalk Walk, SkinUse Skin, ushort Race, string Path)> SkinRequests()
    {
        foreach (var race in _races)
        foreach (var walk in race.Models)
        foreach (var skin in walk.Skins)
        foreach (var row in race.Rows.Where(row => row.UsesMod && row.Resolved == walk.Info!.Race))
            yield return (walk, skin, row.Race, MaterialNaming.SkinPath(row.Race, skin.Body, skin.Suffix));
    }

    private void RaceFindings()
    {
        var player = _input.PlayerRace;

        // Races that keep their own model, merged across sets and slots so a body mod doesn't give one note per piece.
        var uncoveredKeys = new Dictionary<RaceData, string>();
        foreach (var group in _races.Select(race => (Race: race, Names: string.Join(", ", race.Rows
                         .Where(row => !row.UsesMod && row.Race != player
                                    // Models are shaped per gender, so only races of a gender the mod ships for are worth listing.
                                    && race.Shipped.Any(shipped => RaceInfo.IsFemaleCode(shipped) == RaceInfo.IsFemaleCode(row.Race)))
                         .Select(row => RaceInfo.NameOf(row.Race)))))
                     .Where(entry => entry.Names.Length > 0)
                     .GroupBy(entry => entry.Names))
        {
            var key = $"R002:{group.Key}";
            var count = group.Key.Split(", ").Length;
            var reroutable = group.Any(entry => entry.Race.Rows.Any(row => row.Reroutable && row.Race != player));
            AddFinding(key, "R002", CheckCategory.Races, Severity.Info, count == 1 ? "1 race keeps its own model" : $"{count} races keep their own model",
                $"{group.Key} load{(count == 1 ? "s" : string.Empty)} a model this mod doesn't replace for {string.Join(", ", group.Select(entry => entry.Race.Label))}."
              + (reroutable ? " Races with a tick box in Races can be pointed at this mod's model." : string.Empty),
                container: group.First().Race.Models[0].Container);
            foreach (var entry in group)
                uncoveredKeys[entry.Race] = key;
        }

        foreach (var race in _races)
        {
            foreach (var dead in race.Dead)
            {
                var walk = race.Models.First(w => w.Info!.Race == dead);
                var users = race.Rows.Where(row => row.Race == dead).Select(row => RaceInfo.NameOf(row.Resolved)).FirstOrDefault();
                var key = $"R001:{race.Label}:{dead}";
                AddFinding(key, "R001", CheckCategory.Races, Severity.Error, $"The {RaceInfo.NameOf(dead)} model is never loaded",
                    $"The mod ships {walk.Path}, but the EQDP entry of {RaceInfo.NameOf(dead)} (c{dead:D4}) for {race.Label} says this race has no model of its own, so {RaceInfo.NameOf(dead)} characters load the {users} model instead.",
                    walk.Path, walk.Container);
                AddRepair($"eqdp:{race.Label}:{dead}", key, CheckCategory.Races, $"Use the {RaceInfo.NameOf(dead)} model for {RaceInfo.NameOf(dead)}",
                    "Add an EQDP entry that turns on this race's own model.",
                    Array.Empty<string>(), new RepairEdit[] { EqdpEdit(race, walk.Container, dead, race.Bits[dead] | 2) }, preselected: true);
            }

            foreach (var row in race.Rows.Where(row => row.Reroutable))
            {
                var target = RaceInfo.ResolveModelRace(row.Race, race.Accessory, r => r != row.Race && (race.Bits[r] & 2) != 0);
                var walk = race.Models.First(w => w.Info!.Race == target);
                var key = row.Race == player ? $"R003:{race.Label}" : uncoveredKeys.GetValueOrDefault(race, $"R002:{race.Label}");
                AddRepair($"reroute:{race.Label}:{row.Race}", key, CheckCategory.Races,
                    $"Let {RaceInfo.NameOf(row.Race)} use the {RaceInfo.NameOf(target)} model",
                    $"Add an EQDP entry that turns off the {RaceInfo.NameOf(row.Race)} model of {race.Label}, so the race falls back to this mod's {RaceInfo.NameOf(target)} model. The fit may need adjusting.",
                    Array.Empty<string>(), new RepairEdit[] { EqdpEdit(race, walk.Container, row.Race, race.Bits[row.Race] & ~2) }, forceOptIn: true);
            }

            if (player is { } me && race.Rows.FirstOrDefault(row => row.Race == me) is { UsesMod: false } playerRow)
                AddFinding($"R003:{race.Label}", "R003", CheckCategory.Races, Severity.Warning, "Your character doesn't use this mod's model",
                    $"Your character ({RaceInfo.NameOf(me)}) loads the {RaceInfo.NameOf(playerRow.Resolved)} model for {race.Label}, which this mod doesn't replace.",
                    container: race.Models[0].Container);
        }

        foreach (var group in SkinRequests().GroupBy(s => (s.Skin.Suffix, s.Skin.Body)))
        {
            var statuses = group.GroupBy(s => s.Path).Select(g =>
            {
                var request = g.First();
                var resolution = Resolve(request.Path, request.Walk.Container);
                var provider = resolution.IsModFile ? "this mod" : resolution.Source == ResolutionSource.Vanilla ? "the game" : _external.GetValueOrDefault(request.Path);
                return (Path: g.Key, Races: g.Select(s => s.Race).Distinct().ToArray(), Provider: provider);
            }).ToArray();
            var missing = statuses.Where(s => s.Provider is null).ToArray();
            var missingRaces = string.Join(", ", missing.SelectMany(s => s.Races).Order().Select(RaceInfo.NameOf));
            // Without the collection, "missing" only means the game lacks it; a body mod may well provide it.
            var playerMissing = _input.Collection is not null && player is { } me && missing.Any(s => s.Races.Contains(me));
            var name = $"_{group.Key.Suffix} (b{group.Key.Body:D4})";
            var providers = string.Join(", ", statuses.Where(s => s.Provider is not null).Select(s => s.Provider).Distinct());
            var severity = missing.Length == 0 ? Severity.Info : playerMissing ? Severity.Error : Severity.Warning;
            var providedRaces = string.Join(", ", statuses.Where(s => s.Provider is not null).SelectMany(s => s.Races).Order().Select(RaceInfo.NameOf));
            var detail = missing.Length == 0
                ? $"The {name} skin material is there for every race that wears this model ({providers})."
                : _input.Collection is null
                    ? $"The model shows skin with the {name} material, which the game doesn't have for {missingRaces}. It needs a body mod that provides it; your collection was not checked."
                    : (providedRaces.Length > 0 ? $"{providers} provide{(providers.Contains(',') ? string.Empty : "s")} the {name} skin material for {providedRaces}, but it" : $"The {name} skin material")
                    + $" is missing for {missingRaces}{(playerMissing ? ", your character included" : string.Empty)}. Skin parts of the model won't show without a body mod that provides it.";
            AddFinding($"X001:{name}", "X001", CheckCategory.Dependencies, severity, "Skin material dependency", detail, statuses[0].Path,
                manual: missing.Length == 0 ? null : "Install or turn on the body mod this gear was made for (one that provides _bibo skin materials, for example).");
        }
    }

    private AddManipulationEdit EqdpEdit(RaceData race, CheckContainer container, ushort code, int bits)
    {
        var manipulation = MetaManipulations.Eqdp(code, race.SetId, race.Slot, bits);
        var identity = Project.PenumbraModMeta.IdentityOf(manipulation);
        var replaces = _model.Manipulations.Any(m => ReferenceEquals(m.Container, container) && m.Parsed.Identity == identity) ? identity : null;
        return new AddManipulationEdit(container.Option, manipulation,
            $"EQDP {RaceInfo.NameOf(code)} {race.Label}: {((bits & 2) != 0 ? "own model" : "no own model")}", replaces);
    }

    private RaceTable[] RaceTableViews()
    {
        var skins = SkinRequests().GroupBy(s => s.Race).ToDictionary(g => g.Key, g => g.ToArray());
        return _races.Select(race => new RaceTable(race.Label, race.Shipped, race.Rows.Select(row =>
        {
            string? skinNote = null;
            if (skins.TryGetValue(row.Race, out var requests))
                skinNote = string.Join("; ", requests.DistinctBy(s => s.Path).Select(s =>
                {
                    var resolution = Resolve(s.Path, s.Walk.Container);
                    var provider = resolution.IsModFile ? "this mod" : resolution.Source == ResolutionSource.Vanilla ? "game" : _external.GetValueOrDefault(s.Path);
                    return $"_{s.Skin.Suffix}: {provider ?? (_input.Collection is null ? "not checked" : "missing")}";
                }));
            var repair = row.Reroutable ? $"reroute:{race.Label}:{row.Race}" : null;
            return new RaceRow(row.Race, row.Resolved, row.UsesMod, row.Resolved != row.Race, row.Race == _input.PlayerRace, skinNote, repair);
        }).ToArray())).ToArray();
    }
}
