using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using InstantEdit.Services.Animations;

namespace InstantEdit.Services;

/// <summary>
/// An EST entry the exported meshes need, from the <c>xiv_est_hair</c>/<c>xiv_est_race</c> tags
/// Magic Fit's Hair Weights puts on hair it weights: the hair skeleton (<paramref name="Entry"/>,
/// <c>skl_c0801h0160.sklb</c> for 160) of race <see cref="Race"/> (<c>c0801</c>).
/// </summary>
public sealed class EstEntryRequest
{
    [JsonPropertyName("slot")]
    public string? Slot { get; set; }

    [JsonPropertyName("entry")]
    public int Entry { get; set; }

    [JsonPropertyName("race")]
    public string? Race { get; set; }
}

public sealed partial class PenumbraService
{
    private const string EstStateSchema = "instant-edit.est-state";

    // Penumbra reads Gender, ModelRace and EstType by name or by number.
    private static readonly string[] EstGenderNames = ["Unknown", "Male", "Female", "MaleNpc", "FemaleNpc"];
    private static readonly string[] EstModelRaceNames =
        ["Unknown", "Midlander", "Highlander", "Elezen", "Lalafell", "Miqote", "Roegadyn", "AuRa", "Hrothgar", "Viera"];

    /// <summary>A hair EST lookup: the hair's set ID and the gender-race code of its path (801 for c0801).</summary>
    private sealed record EstIdentity(int SetId, int GenderRace)
    {
        public const string Slot = "Hair";
    }

    /// <summary>A mod's Default data, group option or combining container, keyed as the EST store keys it.</summary>
    private sealed record EstContainer(string Key, string Label, JsonObject Data);

    /// <summary>A container that maps the written model, with the hair lookups its game paths make.</summary>
    private sealed record EstTarget(EstContainer Container, IReadOnlyList<EstIdentity> Hair);

    /// <summary>Envelope check for <c>estEntries</c>: hair only, entries 1-9999, player race codes.</summary>
    internal static string? ValidateEstEntryRequests(IReadOnlyList<EstEntryRequest>? entries)
    {
        if (entries is null)
            return null;
        if (entries.Count > 4 || entries.Any(entry => entry is null) ||
            entries.Select(entry => entry.Slot).Distinct(StringComparer.Ordinal).Count() != entries.Count)
            return "invalid_est_entries";
        foreach (var entry in entries)
            if (!string.Equals(entry.Slot, EstIdentity.Slot, StringComparison.Ordinal) ||
                entry.Entry is < 1 or > 9999 || EstRaceCode(entry.Race) is null)
                return "invalid_est_entries";
        return null;
    }

    /// <summary>The gender-race code of a player race such as <c>c0801</c> (801), or null.</summary>
    private static int? EstRaceCode(string? race)
    {
        if (race is null || !EstRaceRegex().IsMatch(race) || !int.TryParse(race.AsSpan(1), out var code) ||
            code % 100 != 1 || code / 100 is < 1 or > 18)
            return null;
        return code;
    }

    private static string EstRaceLabel(int genderRace) => $"c{genderRace:D4}";

    /// <summary>Penumbra keeps EST ownership records per mod: its stable identifier, else its directory key.</summary>
    private static string EstModKey(string modFolder, string modDirectory)
        => ReadModStableIdentifier(modFolder) is { } identifier
            ? $"id:{identifier:D}"
            : $"dir:{modDirectory.Trim().ToLowerInvariant()}";

    private static IEnumerable<EstContainer> EstContainers(JsonObject meta)
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

    private static bool SameEstName(JsonNode? node, string expected, string[] names)
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

    private static bool SameEstRecord(ManagedEstEntry record, string mod, string container, EstIdentity identity)
        => string.Equals(record.Mod, mod, StringComparison.Ordinal) &&
           string.Equals(record.Container, container, StringComparison.Ordinal) &&
           string.Equals(record.Slot, EstIdentity.Slot, StringComparison.Ordinal) &&
           record.SetId == identity.SetId && record.GenderRace == identity.GenderRace;

    /// <summary>
    /// Bring a mod's hair EST entries in line with the exported meshes. <paramref name="requests"/> null
    /// leaves them alone (the tags were missing from an older add-on or disagreed); empty takes back
    /// the entries this plugin set for the model; a hair request sets its entry in every option that
    /// uses the model for the request's race, recording the value it replaced.
    /// </summary>
    private static List<string> UpdateEstEntries(
        string modFolder,
        string modDirectory,
        string modelRelativePath,
        IReadOnlyList<EstEntryRequest>? requests,
        EstEntryStore? store)
    {
        var warnings = new List<string>();
        if (requests is null)
            return warnings;
        var request = requests.FirstOrDefault(item => string.Equals(item.Slot, EstIdentity.Slot, StringComparison.Ordinal));
        if (store is null)
        {
            if (request is not null)
                warnings.Add($"The hair's EST entry could not be set: XIV Instant Edit's EST records are unavailable. Set Hair EST entry {request.Entry} in Penumbra by hand.");
            return warnings;
        }

        var meta = LoadV4ModMetadata(modFolder);
        var targets = EstTargets(meta, modelRelativePath);
        var modKey = EstModKey(modFolder, modDirectory);
        var modelName = Path.GetFileName(modelRelativePath.Replace('\\', '/'));
        var records = store.Load().ToList();
        var metaChanged = false;
        var recordsChanged = false;

        if (request is not null)
        {
            var raceCode = EstRaceCode(request.Race)
                ?? throw new InvalidDataException("The EST entry names an invalid race.");
            var hairRaces = targets.SelectMany(target => target.Hair).Select(identity => identity.GenderRace)
                .Distinct().Order().ToArray();
            var otherRaces = string.Join(", ", hairRaces.Where(code => code != raceCode).Select(EstRaceLabel));
            if (targets.Count == 0)
                warnings.Add($"No Penumbra option uses {modelName}, so its EST entry was not set. Set Hair EST entry {request.Entry} for {request.Race} by hand.");
            else if (hairRaces.Length == 0)
                warnings.Add($"The meshes are weighted to hair skeleton {request.Entry} ({request.Race}), but {modelName} is not used as a hair model, so no EST entry was set.");
            else if (!hairRaces.Contains(raceCode))
                warnings.Add($"The meshes are weighted to {request.Race}'s hair skeleton {request.Entry}, but {modelName} is used for {otherRaces}. Skeleton IDs differ per race, so no EST entry was set; weight the hair to that race's skeleton.");
            else if (otherRaces.Length > 0)
                warnings.Add($"{modelName} is also used for {otherRaces}. Hair skeleton {request.Entry} is {request.Race}'s, so only {request.Race} got the EST entry; set the others by hand.");

            foreach (var target in targets)
            foreach (var identity in target.Hair.Where(identity => identity.GenderRace == raceCode))
            {
                var current = CurrentEst(target.Container.Data, identity);
                if (current == request.Entry)
                    continue;
                var index = records.FindIndex(record => SameEstRecord(record, modKey, target.Container.Key, identity));
                // Keep the modder's value from before the first export while the entry is still ours.
                var previous = index >= 0 && current == records[index].Entry ? records[index].Previous : current;
                SetEst(target.Container.Data, identity, request.Entry);
                var updated = new ManagedEstEntry(modKey, target.Container.Key, EstIdentity.Slot,
                    identity.SetId, identity.GenderRace, request.Entry, previous);
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
                var index = records.FindIndex(record => SameEstRecord(record, modKey, target.Container.Key, identity));
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

    /// <summary>EST setup follows a committed model write, so its failure is a warning, not a failed export.</summary>
    private List<string> TryUpdateEstEntries(
        string modFolder, string modDirectory, string modelRelativePath, IReadOnlyList<EstEntryRequest>? requests)
    {
        try
        {
            return UpdateEstEntries(modFolder, modDirectory, modelRelativePath, requests, _estEntries);
        }
        catch (Exception e)
        {
            _log.Error(e, "Could not update the hair's EST entries.");
            return [$"The hair's EST entry could not be updated: {e.Message}"];
        }
    }

    /// <summary>
    /// The hair EST entries of every option that uses the model, and whether the plugin owns them,
    /// as a model backup carries them; null when the model is used as no hair.
    /// </summary>
    private static JsonObject? CaptureEstState(
        string modFolder, string modDirectory, string modelRelativePath, EstEntryStore? store)
    {
        var meta = LoadV4ModMetadata(modFolder);
        var modKey = EstModKey(modFolder, modDirectory);
        var records = store?.Load() ?? [];
        var entries = new JsonArray();
        foreach (var target in EstTargets(meta, modelRelativePath))
        foreach (var identity in target.Hair)
        {
            var record = records.FirstOrDefault(item => SameEstRecord(item, modKey, target.Container.Key, identity));
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
    private static List<string> RestoreEstState(
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
            var removed = records.RemoveAll(record => SameEstRecord(record, modKey, key, identity)) > 0;
            if (item["managed"] is JsonObject managed && JsonIntFlexible(managed["entry"]) is var managedEntry and >= 1)
            {
                int? previous = managed["previous"] is JsonValue previousValue &&
                                previousValue.TryGetValue<int>(out var previousNumber) ? previousNumber : null;
                records.Add(new ManagedEstEntry(modKey, key, EstIdentity.Slot, identity.SetId, identity.GenderRace,
                    managedEntry, previous));
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
