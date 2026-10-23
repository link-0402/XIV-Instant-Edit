using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dalamud.Plugin.Services;
using InstantEdit.Models;
using InstantEdit.Services.Animations;
using static InstantEdit.Services.PenumbraMods.AttributeGroupService;
using static InstantEdit.Services.PenumbraMods.ModJson;
using static InstantEdit.Services.PenumbraMods.ModMetadata;

namespace InstantEdit.Services.PenumbraMods;

/// <summary>
/// The hair EST entries an export sets in the options that use its model, and the records of the
/// entries XIV Instant Edit owns, so a later export or a restored backup can take them back.
/// </summary>
internal sealed partial class EstEntryService
{
    private readonly EstEntryStore _store;
    private readonly IPluginLog _log;

    public EstEntryService(EstEntryStore store, IPluginLog log)
    {
        _store = store;
        _log = log;
    }

    private const string EstStateSchema = "instant-edit.est-state";

    // Penumbra reads Gender, ModelRace and EstType by name or by number.
    internal static readonly string[] EstGenderNames = ["Unknown", "Male", "Female", "MaleNpc", "FemaleNpc"];
    internal static readonly string[] EstModelRaceNames =
        ["Unknown", "Midlander", "Highlander", "Elezen", "Lalafell", "Miqote", "Roegadyn", "AuRa", "Hrothgar", "Viera"];

    /// <summary>A hair EST lookup: the hair's set ID and the gender-race code of its path (801 for c0801).</summary>
    private sealed record EstIdentity(int SetId, int GenderRace)
    {
        public const string Slot = "Hair";
    }

    /// <summary>A mod's Default data, group option or combining container, keyed as the EST store keys it.</summary>
    internal sealed record EstContainer(string Key, string Label, JsonObject Data);

    /// <summary>A container that maps the written model, with the hair lookups its game paths make.</summary>
    private sealed record EstTarget(EstContainer Container, IReadOnlyList<EstIdentity> Hair);

    /// <summary>Envelope check for <c>estEntries</c>: hair only, entries 1-9999, player race codes.</summary>
    internal static ResultCode? ValidateEstEntryRequests(IReadOnlyList<EstEntryRequest>? entries)
    {
        if (entries is null)
            return null;
        if (entries.Count > 4 || entries.Any(entry => entry is null) ||
            entries.Select(entry => entry.Slot).Distinct(StringComparer.Ordinal).Count() != entries.Count)
            return ResultCode.InvalidEstEntries;
        foreach (var entry in entries)
            if (!string.Equals(entry.Slot, EstIdentity.Slot, StringComparison.Ordinal) ||
                entry.Entry is < 1 or > 9999 || EstRaceCode(entry.Race) is null)
                return ResultCode.InvalidEstEntries;
        return null;
    }

    /// <summary>The gender-race code of a player race such as <c>c0801</c> (801), or null.</summary>
    internal static int? EstRaceCode(string? race)
    {
        if (race is null || !EstRaceRegex().IsMatch(race) || !int.TryParse(race.AsSpan(1), out var code) ||
            code % 100 != 1 || code / 100 is < 1 or > 18)
            return null;
        return code;
    }

    private static string EstRaceLabel(int genderRace) => $"c{genderRace:D4}";

    /// <summary>Penumbra keeps EST ownership records per mod: its stable identifier, else its directory key.</summary>
    internal static string EstModKey(string modFolder, string modDirectory)
        => ReadModStableIdentifier(modFolder) is { } identifier
            ? $"id:{identifier:D}"
            : $"dir:{modDirectory.Trim().ToLowerInvariant()}";

    /// <summary>
    /// A record made in <paramref name="recordFolder"/> is this mod folder's: made here, or in a folder that
    /// no longer exists (Penumbra renamed the mod). A folder copied with the mod's identifier shares its EST
    /// key, but the mod it was copied from keeps its records. Records from before the folder was kept belong
    /// to every folder with the key.
    /// </summary>
    internal static bool OwnsFolder(string? recordFolder, string modFolder)
    {
        if (recordFolder is not { Length: > 0 } folder || string.Equals(folder, FolderName(modFolder), StringComparison.OrdinalIgnoreCase))
            return true;
        var library = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(modFolder)));
        return library is null || !Directory.Exists(Path.Combine(library, folder));
    }

    internal static string FolderName(string modFolder) => Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(modFolder)));

    internal static IEnumerable<EstContainer> EstContainers(JsonObject meta)
    {
        if (meta["DefaultData"] is JsonObject defaultData)
            yield return new EstContainer("default", "Default", defaultData);
        foreach (var group in (meta["Groups"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (ReadGuid(group["Id"]) is not { } groupId)
                continue;
            var groupName = JsonString(group["Name"]) is { Length: > 0 } name ? name : "Unnamed group";
            var options = group["Options"] as JsonArray ?? [];
            foreach (var option in options.OfType<JsonObject>())
                if (ReadGuid(option["Id"]) is { } optionId)
                    yield return new EstContainer($"option:{groupId:D}:{optionId:D}",
                        $"{groupName}: {JsonString(option["Name"]) ?? "Unnamed option"}", option);
            if (group["Containers"] is JsonArray containers)
                for (var index = 0; index < containers.Count; ++index)
                    if (containers[index] is JsonObject container)
                        yield return new EstContainer($"container:{groupId:D}:{index}", $"{groupName}: combination {index}", container);
        }
    }

    /// <summary>Every container whose files map <paramref name="modelRelativePath"/>, with the hair paths it maps it at.</summary>
    private static List<EstTarget> EstTargets(JsonObject meta, string modelRelativePath)
    {
        var model = modelRelativePath.Replace('\\', '/').Trim().TrimStart('/');
        var result = new List<EstTarget>();
        foreach (var container in EstContainers(meta))
        {
            if (container.Data["Files"] is not JsonObject files)
                continue;
            var mapped = false;
            var hair = new List<EstIdentity>();
            foreach (var (gamePath, value) in files)
            {
                if (!string.Equals(JsonString(value)?.Replace('\\', '/').Trim().TrimStart('/'), model,
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                mapped = true;
                if (ParseAttributeModelIdentity(gamePath) is { ObjectType: "Character", BodySlot: "Hair" } identity &&
                    !hair.Contains(new EstIdentity(identity.Id, identity.RaceCode)))
                    hair.Add(new EstIdentity(identity.Id, identity.RaceCode));
            }
            if (mapped)
                result.Add(new EstTarget(container, hair));
        }
        return result;
    }

    /// <summary>True when any container's files map the model, at any game path.</summary>
    internal static bool MapsModel(JsonObject meta, string modelRelativePath) => EstTargets(meta, modelRelativePath).Count > 0;

    /// <summary>The hair lookups of every container that maps the model, flattened: container key, set ID, gender-race code.</summary>
    internal static IReadOnlyList<(string Container, int SetId, int GenderRace)> HairUses(JsonObject meta, string modelRelativePath)
        => EstTargets(meta, modelRelativePath)
            .SelectMany(target => target.Hair.Select(identity => (target.Container.Key, identity.SetId, identity.GenderRace)))
            .ToList();

    internal static bool SameEstName(JsonNode? node, string expected, string[] names)
    {
        if (node is not JsonValue value)
            return false;
        if (value.TryGetValue<string>(out var text))
            return string.Equals(text, expected, StringComparison.OrdinalIgnoreCase);
        return value.TryGetValue<int>(out var number) && number >= 0 && number < names.Length &&
               string.Equals(names[number], expected, StringComparison.Ordinal);
    }

    private static bool MatchesEst(JsonObject item, EstIdentity identity)
    {
        if (!string.Equals(JsonString(item["Type"]), "Est", StringComparison.OrdinalIgnoreCase) ||
            item["Manipulation"] is not JsonObject manipulation)
            return false;
        var (gender, race) = AnimationMetadata.GenderRaceNames((ushort)identity.GenderRace);
        // EstType is a MetaIndex: 73 is the hair table.
        var slot = manipulation["Slot"] is JsonValue slotValue && slotValue.TryGetValue<int>(out var slotNumber)
            ? slotNumber == 73
            : SameEstName(manipulation["Slot"], EstIdentity.Slot, []);
        return slot && JsonIntFlexible(manipulation["SetId"]) == identity.SetId &&
               SameEstName(manipulation["Gender"], gender, EstGenderNames) &&
               SameEstName(manipulation["Race"], race, EstModelRaceNames);
    }

    /// <summary>The entry a container sets for a lookup, or null. Penumbra keeps the first valid duplicate.</summary>
    private static int? CurrentEst(JsonObject container, EstIdentity identity)
    {
        foreach (var item in (container["Manipulations"] as JsonArray ?? []).OfType<JsonObject>())
            if (MatchesEst(item, identity) && item["Manipulation"]!["Entry"] is JsonValue value &&
                value.TryGetValue<int>(out var entry) && entry is >= 0 and <= ushort.MaxValue)
                return entry;
        return null;
    }

    /// <summary>Set a container's entry for a lookup (null removes it), collapsing duplicates into one.</summary>
    private static void SetEst(JsonObject container, EstIdentity identity, int? entry)
    {
        var manipulations = container["Manipulations"] as JsonArray;
        if (manipulations is null)
        {
            if (entry is null)
                return;
            container["Manipulations"] = manipulations = [];
        }
        var first = -1;
        for (var index = manipulations.Count - 1; index >= 0; --index)
        {
            if (manipulations[index] is not JsonObject item || !MatchesEst(item, identity))
                continue;
            manipulations.RemoveAt(index);
            first = index;
        }
        if (entry is null)
            return;
        var (gender, race) = AnimationMetadata.GenderRaceNames((ushort)identity.GenderRace);
        var manipulation = new JsonObject
        {
            ["Type"] = "Est",
            ["Manipulation"] = new JsonObject
            {
                ["Gender"] = gender,
                ["Race"] = race,
                ["SetId"] = identity.SetId,
                ["Slot"] = EstIdentity.Slot,
                ["Entry"] = entry.Value,
            },
        };
        if (first >= 0)
            manipulations.Insert(first, manipulation);
        else
            manipulations.Add(manipulation);
    }

    private static bool SameEstRecord(ManagedEstEntry record, string mod, string modFolder, string container, EstIdentity identity)
        => string.Equals(record.Mod, mod, StringComparison.Ordinal) &&
           string.Equals(record.Container, container, StringComparison.Ordinal) &&
           string.Equals(record.Slot, EstIdentity.Slot, StringComparison.Ordinal) &&
           record.SetId == identity.SetId && record.GenderRace == identity.GenderRace &&
           OwnsFolder(record.Folder, modFolder);

    /// <summary>
    /// Bring a mod's hair EST entries in line with the exported meshes. <paramref name="requests"/> null
    /// leaves them alone (the tags were missing from an older add-on or disagreed); empty takes back
    /// the entries this plugin set for the model; each hair request (at most one per race) sets its
    /// entry in every option that uses the model for the request's race, recording the value it replaced.
    /// </summary>
    internal static List<string> UpdateEstEntries(
        string modFolder,
        string modDirectory,
        string modelRelativePath,
        IReadOnlyList<EstEntryRequest>? requests,
        EstEntryStore? store)
    {
        var warnings = new List<string>();
        if (requests is null)
            return warnings;
        // One request per race; a second one for the same race is ignored (the first wins, as in Penumbra).
        var hairRequests = requests
            .Where(item => string.Equals(item.Slot, EstIdentity.Slot, StringComparison.Ordinal))
            .DistinctBy(item => item.Race, StringComparer.Ordinal)
            .ToList();
        if (store is null)
        {
            foreach (var request in hairRequests)
                warnings.Add(hairRequests.Count == 1
                    ? $"The hair's EST entry could not be set: XIV Instant Edit's EST records are unavailable. Set Hair EST entry {request.Entry} in Penumbra by hand."
                    : $"The hair's EST entry could not be set: XIV Instant Edit's EST records are unavailable. Set Hair EST entry {request.Entry} for {request.Race} in Penumbra by hand.");
            return warnings;
        }

        var meta = LoadV4ModMetadata(modFolder);
        var targets = EstTargets(meta, modelRelativePath);
        var modKey = EstModKey(modFolder, modDirectory);
        var modelName = Path.GetFileName(modelRelativePath.Replace('\\', '/'));
        var records = store.Load().ToList();
        var metaChanged = false;
        var recordsChanged = false;

        if (hairRequests.Count > 0)
        {
            var raced = hairRequests
                .Select(request => (Request: request, RaceCode: EstRaceCode(request.Race)
                    ?? throw new InvalidDataException("The EST entry names an invalid race.")))
                .ToList();
            var hairRaces = targets.SelectMany(target => target.Hair).Select(identity => identity.GenderRace)
                .Distinct().Order().ToArray();
            foreach (var (request, raceCode) in raced)
            {
                var otherRaces = string.Join(", ", hairRaces.Where(code => code != raceCode).Select(EstRaceLabel));
                if (targets.Count == 0)
                    warnings.Add($"No Penumbra option uses {modelName}, so its EST entry was not set. Set Hair EST entry {request.Entry} for {request.Race} by hand.");
                else if (hairRaces.Length == 0)
                    warnings.Add($"The meshes are weighted to hair skeleton {request.Entry} ({request.Race}), but {modelName} is not used as a hair model, so no EST entry was set.");
                else if (!hairRaces.Contains(raceCode))
                    warnings.Add($"The meshes are weighted to {request.Race}'s hair skeleton {request.Entry}, but {modelName} is used for {otherRaces}. Skeleton IDs differ per race, so no EST entry was set; weight the hair to that race's skeleton.");
            }
            // The races the model is used for that no request covers keep their entries; name them once.
            var served = raced.Where(item => hairRaces.Contains(item.RaceCode)).ToList();
            var unrequested = string.Join(", ", hairRaces
                .Where(code => raced.All(item => item.RaceCode != code)).Select(EstRaceLabel));
            if (served.Count == 1 && unrequested.Length > 0)
                warnings.Add($"{modelName} is also used for {unrequested}. Hair skeleton {served[0].Request.Entry} is {served[0].Request.Race}'s, so only {served[0].Request.Race} got the EST entry; set the others by hand.");
            else if (served.Count > 1 && unrequested.Length > 0)
                warnings.Add($"{modelName} is also used for {unrequested}. Only {string.Join(", ", served.Select(item => item.Request.Race))} got EST entries for their hair skeletons; set the others by hand.");

            foreach (var (request, raceCode) in raced)
            foreach (var target in targets)
            foreach (var identity in target.Hair.Where(identity => identity.GenderRace == raceCode))
            {
                var current = CurrentEst(target.Container.Data, identity);
                if (current == request.Entry)
                    continue;
                var index = records.FindIndex(record => SameEstRecord(record, modKey, modFolder, target.Container.Key, identity));
                // Keep the modder's value from before the first export while the entry is still ours.
                var previous = index >= 0 && current == records[index].Entry ? records[index].Previous : current;
                SetEst(target.Container.Data, identity, request.Entry);
                var updated = new ManagedEstEntry(modKey, target.Container.Key, EstIdentity.Slot,
                    identity.SetId, identity.GenderRace, request.Entry, previous, FolderName(modFolder));
                if (index >= 0)
                    records[index] = updated;
                else
                    records.Add(updated);
                metaChanged = recordsChanged = true;
            }
        }
        else
        {
            foreach (var target in targets)
            foreach (var identity in target.Hair)
            {
                var index = records.FindIndex(record => SameEstRecord(record, modKey, modFolder, target.Container.Key, identity));
                if (index < 0)
                    continue;
                var record = records[index];
                records.RemoveAt(index);
                recordsChanged = true;
                // An entry changed since the export wrote it is the modder's now.
                if (CurrentEst(target.Container.Data, identity) != record.Entry)
                    continue;
                SetEst(target.Container.Data, identity, record.Previous);
                metaChanged = true;
            }
        }

        if (metaChanged)
        {
            TouchV4ModMetadata(meta);
            WriteJsonAtomic(Path.Combine(modFolder, "meta.json"), meta);
        }
        if (recordsChanged)
            store.Save(records);
        return warnings;
    }

    /// <summary>
    /// Take back the hair EST entries this plugin set to <paramref name="entry"/> in a mod wherever the
    /// model no longer serves them: a container that stopped mapping the model for the lookup, or a race
    /// outside <paramref name="races"/>. Changes <paramref name="meta"/> and <paramref name="records"/> in
    /// memory for the caller to write; true when meta changed.
    /// </summary>
    internal static bool ReleaseEstEntries(
        JsonObject meta,
        string modFolder,
        string modKey,
        string modelRelativePath,
        int entry,
        IReadOnlyCollection<int> races,
        List<ManagedEstEntry> records)
    {
        var served = EstTargets(meta, modelRelativePath)
            .SelectMany(target => target.Hair.Where(identity => races.Contains(identity.GenderRace))
                .Select(identity => (target.Container.Key, identity)))
            .ToHashSet();
        var containers = EstContainers(meta).ToDictionary(container => container.Key, StringComparer.Ordinal);
        var metaChanged = false;
        for (var index = records.Count - 1; index >= 0; --index)
        {
            var record = records[index];
            var identity = new EstIdentity(record.SetId, record.GenderRace);
            if (!string.Equals(record.Mod, modKey, StringComparison.Ordinal) || !OwnsFolder(record.Folder, modFolder) ||
                !string.Equals(record.Slot, EstIdentity.Slot, StringComparison.Ordinal) ||
                record.Entry != entry || served.Contains((record.Container, identity)))
                continue;
            records.RemoveAt(index);
            // An entry changed since the export wrote it is the modder's now.
            if (!containers.TryGetValue(record.Container, out var container) ||
                CurrentEst(container.Data, identity) != record.Entry)
                continue;
            SetEst(container.Data, identity, record.Previous);
            metaChanged = true;
        }
        return metaChanged;
    }

    /// <summary>EST setup follows a committed model write, so its failure is a warning, not a failed export.</summary>
    internal List<string> TryUpdateEstEntries(
        string modFolder, string modDirectory, string modelRelativePath, IReadOnlyList<EstEntryRequest>? requests)
    {
        try
        {
            return UpdateEstEntries(modFolder, modDirectory, modelRelativePath, requests, _store);
        }
        catch (Exception e)
        {
            _log.Error(e, "Could not update the hair's EST entries.");
            return [$"The hair's EST entry could not be updated: {e.Message}"];
        }
    }

    /// <summary><see cref="CaptureEstState(string, string, string, EstEntryStore?)"/> with the plugin's EST records.</summary>
    internal JsonObject? CaptureEstState(string modFolder, string modDirectory, string modelRelativePath)
        => CaptureEstState(modFolder, modDirectory, modelRelativePath, _store);

    /// <summary><see cref="RestoreEstState(string, string, JsonObject, EstEntryStore?)"/> with the plugin's EST records.</summary>
    internal List<string> RestoreEstState(string modFolder, string modDirectory, JsonObject state)
        => RestoreEstState(modFolder, modDirectory, state, _store);

    /// <summary>
    /// The hair EST entries of every option that uses the model, and whether the plugin owns them,
    /// as a model backup carries them; null when the model is used as no hair.
    /// </summary>
    internal static JsonObject? CaptureEstState(
        string modFolder, string modDirectory, string modelRelativePath, EstEntryStore? store)
    {
        var meta = LoadV4ModMetadata(modFolder);
        var modKey = EstModKey(modFolder, modDirectory);
        var records = store?.Load() ?? [];
        var entries = new JsonArray();
        foreach (var target in EstTargets(meta, modelRelativePath))
        foreach (var identity in target.Hair)
        {
            var record = records.FirstOrDefault(item => SameEstRecord(item, modKey, modFolder, target.Container.Key, identity));
            entries.Add(new JsonObject
            {
                ["container"] = target.Container.Key,
                ["label"] = target.Container.Label,
                ["slot"] = EstIdentity.Slot,
                ["setId"] = identity.SetId,
                ["genderRace"] = identity.GenderRace,
                ["entry"] = CurrentEst(target.Container.Data, identity),
                ["managed"] = record is null ? null : new JsonObject
                {
                    ["entry"] = record.Entry,
                    ["previous"] = record.Previous,
                },
            });
        }
        return entries.Count == 0
            ? null
            : new JsonObject { ["schema"] = EstStateSchema, ["version"] = 1, ["entries"] = entries };
    }

    /// <summary>Put back the EST entries a model backup carries, with the plugin's ownership of them.</summary>
    internal static List<string> RestoreEstState(
        string modFolder, string modDirectory, JsonObject state, EstEntryStore? store)
    {
        var warnings = new List<string>();
        if (!string.Equals(JsonString(state["schema"]), EstStateSchema, StringComparison.Ordinal) ||
            JsonInt(state["version"]) != 1 || state["entries"] is not JsonArray entries)
            return ["The backup's EST entries are unreadable, so they were not restored."];
        var meta = LoadV4ModMetadata(modFolder);
        var containers = EstContainers(meta).ToDictionary(container => container.Key, StringComparer.Ordinal);
        var modKey = EstModKey(modFolder, modDirectory);
        var records = store?.Load().ToList() ?? [];
        var metaChanged = false;
        var recordsChanged = false;
        foreach (var item in entries.OfType<JsonObject>())
        {
            var key = JsonString(item["container"]) ?? "";
            var identity = new EstIdentity(JsonInt(item["setId"]), JsonInt(item["genderRace"]));
            if (!string.Equals(JsonString(item["slot"]), EstIdentity.Slot, StringComparison.Ordinal) ||
                identity.SetId is < 1 or > 9999 || EstRaceCode(EstRaceLabel(identity.GenderRace)) is null)
                continue;
            if (!containers.TryGetValue(key, out var container))
            {
                var missing = $"The Penumbra option \"{JsonString(item["label"]) ?? key}\" no longer exists, so its EST entries were not restored.";
                if (!warnings.Contains(missing))
                    warnings.Add(missing);
                continue;
            }
            int? entry = item["entry"] is JsonValue value && value.TryGetValue<int>(out var number) &&
                         number is >= 0 and <= ushort.MaxValue ? number : null;
            if (CurrentEst(container.Data, identity) != entry)
            {
                SetEst(container.Data, identity, entry);
                metaChanged = true;
            }
            if (store is null)
                continue;
            var removed = records.RemoveAll(record => SameEstRecord(record, modKey, modFolder, key, identity)) > 0;
            if (item["managed"] is JsonObject managed && JsonIntFlexible(managed["entry"]) is var managedEntry and >= 1)
            {
                int? previous = managed["previous"] is JsonValue previousValue &&
                                previousValue.TryGetValue<int>(out var previousNumber) ? previousNumber : null;
                records.Add(new ManagedEstEntry(modKey, key, EstIdentity.Slot, identity.SetId, identity.GenderRace,
                    managedEntry, previous, FolderName(modFolder)));
                recordsChanged = true;
            }
            else
            {
                recordsChanged |= removed;
            }
        }
        if (metaChanged)
        {
            TouchV4ModMetadata(meta);
            WriteJsonAtomic(Path.Combine(modFolder, "meta.json"), meta);
        }
        if (recordsChanged)
            store!.Save(records);
        return warnings;
    }

    [GeneratedRegex("^c[0-9]{4}$", RegexOptions.CultureInvariant)]
    private static partial Regex EstRaceRegex();

    internal static IReadOnlyList<string> UpdateEstEntriesForRegression(
        string modFolder, string modDirectory, string modelRelativePath,
        IReadOnlyList<EstEntryRequest>? requests, EstEntryStore? store)
        => UpdateEstEntries(modFolder, modDirectory, modelRelativePath, requests, store);

    internal static JsonObject? CaptureEstStateForRegression(
        string modFolder, string modDirectory, string modelRelativePath, EstEntryStore? store)
        => CaptureEstState(modFolder, modDirectory, modelRelativePath, store);

    internal static IReadOnlyList<string> RestoreEstStateForRegression(
        string modFolder, string modDirectory, JsonObject state, EstEntryStore? store)
        => RestoreEstState(modFolder, modDirectory, state, store);
}
