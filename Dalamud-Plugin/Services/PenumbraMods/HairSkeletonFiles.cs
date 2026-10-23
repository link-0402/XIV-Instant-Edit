using System.Collections.Immutable;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using InstantEdit.Models;
using InstantEdit.Services.Physics;
using InstantEdit.Services.Skeletons;
using static InstantEdit.Services.PenumbraMods.ModJson;
using static InstantEdit.Services.PenumbraMods.ModMetadata;
using static InstantEdit.Services.PenumbraMods.ModPaths;

namespace InstantEdit.Services.PenumbraMods;

/// <summary>
/// Where a hair skeleton's inputs come from: game files, the skeleton writer, the <c>j_kao</c> of a
/// template, and the hair skeleton IDs the library's other mods use.
/// </summary>
internal sealed record HairSkeletonSources(
    Func<string, byte[]?> GameFile,
    Func<string, bool> GameFileExists,
    Func<byte[], ImmutableArray<SkeletonBone>, byte[]> Write,
    Func<byte[], SkeletonBone> TemplateKao,
    Func<IReadOnlySet<int>> TakenIds,
    Random Random);

/// <summary>One race's skeleton and physics files; their game paths are also their mod-relative paths.</summary>
internal sealed record HairSkeletonFile(int GenderRace, string GamePath, byte[] Bytes, string PhysicsPath, byte[] Physics);

/// <summary>
/// The skeleton files of a hair model (mod-relative, <c>/</c> separators) for every race it is used for.
/// <paramref name="PreviousIds"/> are the IDs the plan moves away from: the recorded one when another mod
/// uses it, and those of an earlier move that didn't finish.
/// <paramref name="ChainKinds"/> maps each physics chain's first bone to its kind, for the record.
/// </summary>
internal sealed record HairSkeletonPlan(
    string Model, int SkeletonId, IReadOnlyList<HairSkeletonFile> Files, IReadOnlyList<string> Notes,
    IReadOnlyList<int>? PreviousIds = null, IReadOnlyDictionary<string, string>? ChainKinds = null);

/// <summary>
/// A mod's custom hair skeleton for one model file: one <c>.sklb</c> and one <c>.phyb</c> per race the
/// model is used as hair for, mapped in the options that use it for that race, with the hair EST entries
/// naming it. Every skeleton ID belongs to one (mod, model), so its keys and files are this plugin's to
/// change; keys of other skeleton IDs are never touched.
/// </summary>
internal static partial class HairSkeletonFiles
{
    // Player gender-race codes: 101 (c0101) to 1801 (c1801).
    private static readonly int[] PlayerRaces = Enumerable.Range(1, 18).Select(code => code * 100 + 1).ToArray();

    [GeneratedRegex(@"^chara/human/c([0-9]{4})/skeleton/hair/h([0-9]{4})/(skl_c\1h\2\.sklb|phy_c\1h\2\.phyb)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SkeletonKey();

    private static string RaceLabel(int genderRace) => $"c{genderRace:D4}";

    /// <summary>
    /// The warning for a model no option maps yet (a variant saved without Penumbra setup), which gets
    /// no hair skeleton; null when <see cref="Prepare"/> should run.
    /// </summary>
    public static string? UnmappedNote(JsonObject meta, string modelRelativePath)
        => EstEntryService.MapsModel(meta, modelRelativePath)
            ? null
            : $"No Penumbra option uses {Path.GetFileName(PathRules.NormalizeGamePath(modelRelativePath))} yet, so no hair skeleton was built.";

    /// <summary>
    /// Build the skeleton and physics files for every race the model is used as hair for, under the ID the
    /// store holds for the model, or a new free one when there is none or another mod now uses it. Throws
    /// <see cref="InvalidDataException"/> when the model is used as no hair, or not for the rig's race.
    /// </summary>
    public static HairSkeletonPlan Prepare(
        HairSkeletonRequest rig,
        string modFolder,
        string modDirectory,
        string modelRelativePath,
        HairSkeletonSources sources,
        HairSkeletonStore store)
    {
        var model = PathRules.NormalizeGamePath(modelRelativePath);
        var modelName = Path.GetFileName(model);
        var meta = LoadV4ModMetadata(modFolder);
        var races = EstEntryService.HairUses(meta, model).Select(use => use.GenderRace).Distinct().Order().ToList();
        if (races.Count == 0)
            throw new InvalidDataException($"No Penumbra option uses {modelName} as a hair model, so it can't get a hair skeleton.");
        if (EstEntryService.EstRaceCode(rig.Race) is not { } rigRace || !races.Contains(rigRace))
            throw new InvalidDataException(
                $"The hair rig is made on {rig.Race}, but {modelName} is used for {string.Join(", ", races.Select(RaceLabel))}.");

        var table = EstTable.Parse(sources.GameFile(EstTable.GamePath(EstSlot.Hair))
            ?? throw new InvalidDataException("The game's hair EST table could not be read."));
        // The library scan leaves this mod out, so a recorded ID it names is another mod's too. A new ID
        // also avoids the IDs this mod's own options use.
        var record = Record(store.Load(), EstEntryService.EstModKey(modFolder, modDirectory), model, modFolder);
        var recorded = record?.SkeletonId;
        var taken = sources.TakenIds();
        var notes = new List<string>();
        // A move an earlier export didn't finish still takes back its old IDs; the first one's physics is the one tuned.
        var previousIds = new List<int>(record?.MovingFrom ?? []);
        var tuningId = record?.MovingFrom is [var first, ..] ? first : recorded;
        int id;
        if (recorded is { } kept && !taken.Contains(kept))
            id = kept;
        else
        {
            id = HairSkeletonIds.Pick(HairSkeletonIds.Vanilla(table).Union(taken).Union(HairSkeletonIds.InModFile(meta)).ToHashSet(),
                sources.Random);
            if (recorded is { } clashed)
            {
                previousIds.Add(clashed);
                notes.Add($"Hair skeleton h{clashed:D4} is also used by another mod, so this export moved it to h{id:D4}.");
            }
        }
        previousIds = previousIds.Where(previous => previous != id).Distinct().ToList();

        var files = new List<HairSkeletonFile>();
        // The physics builder's notes, each with the races it was made for.
        var raceNotes = new OrderedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var race in races)
        {
            var label = RaceLabel(race);
            if (HairSkeletonIds.TemplateId(table, (ushort)race, sources.GameFileExists) is not { } templateId)
            {
                notes.Add($"No vanilla hair skeleton for {label}, so it keeps its own.");
                continue;
            }
            var templatePath = ModelSkeletonPaths.PartialPath(label, EstSlot.Hair, templateId);
            var template = sources.GameFile(templatePath)
                ?? throw new InvalidDataException($"The game's hair skeleton {templatePath} could not be read.");
            var bones = HairSkeletonLayout.Bones(rig, sources.TemplateKao(template));
            var physicsPath = HairPhysicsBuilder.PhysicsPath(race, id);
            // Only the recorded ID's physics is the user's to keep, also when the ID moves; a first ID starts from the presets.
            var previous = tuningId is { } own ? ReadPhysicsFile(modFolder, HairPhysicsBuilder.PhysicsPath(race, own)) : default;
            var physics = Physics(rig, bones, race, sources, previous, record?.ChainKinds, notes, raceNotes);
            files.Add(new HairSkeletonFile(race, ModelSkeletonPaths.PartialPath(label, EstSlot.Hair, id),
                sources.Write(template, bones), physicsPath, physics));
        }
        // An empty plan would only take back the model's EST entries.
        if (files.Count == 0)
            throw new InvalidDataException("No race the model is used for has a vanilla hair skeleton.");
        // A note the same for several races is said once, naming them all.
        notes.AddRange(raceNotes.Select(pair => $"{string.Join(", ", pair.Value)}: {pair.Key}"));
        var kinds = (rig.Chains ?? []).ToDictionary(chain => chain.Bones![0], chain => chain.Kind!, StringComparer.Ordinal);
        return new HairSkeletonPlan(model, id, files, notes, previousIds.Count > 0 ? previousIds : null, kinds);
    }

    /// <summary>
    /// A race's hair physics: shapes from its h0182 physics, and the settings of chains the mod's file
    /// (<paramref name="previous"/>, written with <paramref name="previousKinds"/>) already has. The
    /// builder's notes go to <paramref name="raceNotes"/> with the race.
    /// </summary>
    private static byte[] Physics(HairSkeletonRequest rig, ImmutableArray<SkeletonBone> bones, int race,
        HairSkeletonSources sources, (byte[]? Bytes, bool Unreadable) previous, IReadOnlyDictionary<string, string>? previousKinds,
        List<string> notes, OrderedDictionary<string, List<string>> raceNotes)
    {
        var label = RaceLabel(race);
        PhybFile? donor = null, tuned = null;
        // Without chains the file turns physics off, so neither file is read.
        if (rig.Chains is { Count: > 0 })
        {
            donor = ParsePhysics(sources.GameFile(HairPhysicsPresets.DonorPath(race)));
            if (donor is null)
                notes.Add($"The game's hair physics h0182 for {label} could not be read, so that race's hair collides with nothing.");
            tuned = ParsePhysics(previous.Bytes);
            if (previous.Unreadable || (previous.Bytes is not null && tuned is null))
                notes.Add($"The hair physics in the mod for {label} could not be read, so every chain gets its preset.");
        }
        var built = new List<string>();
        var file = HairPhysicsBuilder.Build(rig, bones, donor, tuned, built, previousKinds);
        foreach (var note in built)
        {
            if (!raceNotes.TryGetValue(note, out var labels))
                raceNotes.Add(note, labels = []);
            labels.Add(label);
        }
        return file.ToBytes();
    }

    /// <summary>
    /// A mod physics file's bytes, or null when it is missing, outside the mod or behind a reparse point;
    /// <c>Unreadable</c> when it exists but can't be read, so the export says it starts from the presets.
    /// </summary>
    private static (byte[]? Bytes, bool Unreadable) ReadPhysicsFile(string modFolder, string relativePath)
    {
        string path;
        try { path = Destination(modFolder, relativePath); }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException) { return (null, false); }
        try { return (File.Exists(path) ? File.ReadAllBytes(path) : null, false); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (null, true); }
    }

    /// <summary>A physics file, or null when there are no bytes or they don't parse.</summary>
    private static PhybFile? ParsePhysics(byte[]? bytes)
    {
        if (bytes is null)
            return null;
        try { return PhybFile.Parse(bytes); }
        catch (InvalidDataException) { return null; }
    }

    /// <summary>
    /// Record the ID, write the plan's files into the mod, map each one in the options that use the
    /// model for its race (and nowhere else), delete the ID's files no option maps any more, and set
    /// the hair EST entries. A plan that moves off previous IDs first takes back those IDs' keys, files
    /// and EST entries; the record names those IDs until they are gone, so a failed move is finished by the
    /// next export. Returns the plan's notes, then the EST warnings.
    /// </summary>
    public static List<string> Apply(
        HairSkeletonPlan plan,
        string modFolder,
        string modDirectory,
        string modelRelativePath,
        HairSkeletonStore store,
        EstEntryStore estStore)
    {
        var model = PathRules.NormalizeGamePath(modelRelativePath);
        if (!string.Equals(plan.Model, model, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The hair skeleton plan was made for another model.", nameof(plan));
        // Check every destination before the first write.
        var targets = plan.Files
            .SelectMany(file => new[]
            {
                (file.Bytes, Path: Destination(modFolder, file.GamePath)),
                (Bytes: file.Physics, Path: Destination(modFolder, file.PhysicsPath)),
            })
            .ToList();
        // Record the ID before the first write, so a step that fails later never orphans its keys and files.
        // Until the move is done, the record also names the previous IDs, and keeps the chain kinds of their physics.
        var modKey = EstEntryService.EstModKey(modFolder, modDirectory);
        var folder = FolderName(modFolder);
        var previousIds = (plan.PreviousIds ?? []).Where(moved => moved != plan.SkeletonId).Distinct().ToList();
        var stored = store.Load();
        var records = stored.Where(record => !Owns(record, modKey, model, modFolder)).ToList();
        records.Add(new ManagedHairSkeleton(modKey, model, plan.SkeletonId, previousIds.Count > 0 ? previousIds : null,
            Record(stored, modKey, model, modFolder)?.ChainKinds, folder));
        store.Save(records);
        foreach (var (bytes, path) in targets)
            WriteAtomic(path, bytes);

        var meta = LoadV4ModMetadata(modFolder);
        var estRecords = estStore.Load().ToList();
        var estCount = estRecords.Count;
        var metaChanged = false;
        // The previous IDs belong to another mod now, or to a move that is being finished: none of their keys or EST entries stay.
        foreach (var previous in previousIds)
        {
            metaChanged |= Remap(meta, model, previous, new Dictionary<int, IReadOnlyList<string>>());
            metaChanged |= EstEntryService.ReleaseEstEntries(meta, modKey, model, previous, [], estRecords);
        }
        var built = plan.Files.ToDictionary(file => file.GenderRace, IReadOnlyList<string> (file) => [file.GamePath, file.PhysicsPath]);
        metaChanged |= Remap(meta, model, plan.SkeletonId, built);
        // Races and options that dropped the model keep no EST entry naming the skeleton.
        metaChanged |= EstEntryService.ReleaseEstEntries(meta, modKey, model, plan.SkeletonId, built.Keys, estRecords);
        // meta.json goes first, so a failed write never leaves an option mapping a deleted file.
        if (metaChanged)
        {
            TouchV4ModMetadata(meta);
            WriteJsonAtomic(Path.Combine(modFolder, "meta.json"), meta);
        }
        if (estRecords.Count != estCount)
            estStore.Save(estRecords);
        foreach (var old in previousIds)
            DeleteUnmapped(meta, modFolder, old);
        // The previous IDs are gone and the new physics written: the record names only the new ID, with its chain kinds.
        store.Save(store.Load().Where(record => !Owns(record, modKey, model, modFolder))
            .Append(new ManagedHairSkeleton(modKey, model, plan.SkeletonId, ChainKinds: plan.ChainKinds, Folder: folder)));
        DeleteUnmapped(meta, modFolder, plan.SkeletonId);

        var requests = plan.Files
            .Select(file => new EstEntryRequest { Slot = "Hair", Entry = plan.SkeletonId, Race = RaceLabel(file.GenderRace) })
            .ToList();
        var warnings = EstEntryService.UpdateEstEntries(modFolder, modDirectory, model, requests, estStore);
        return [.. plan.Notes, .. warnings];
    }

    /// <summary>
    /// Take back the model's hair skeleton: unmap and delete its files, take back its EST entries, and
    /// drop its record; the same for the old IDs of a move that didn't finish. Without a record nothing
    /// changes. Returns the EST warnings.
    /// </summary>
    public static List<string> Remove(
        string modFolder,
        string modDirectory,
        string modelRelativePath,
        HairSkeletonStore store,
        EstEntryStore estStore)
    {
        var model = PathRules.NormalizeGamePath(modelRelativePath);
        var modKey = EstEntryService.EstModKey(modFolder, modDirectory);
        var records = store.Load().ToList();
        if (Record(records, modKey, model, modFolder) is not { } record)
            return [];

        var ids = (record.MovingFrom ?? []).Prepend(record.SkeletonId).Distinct().ToList();
        var meta = LoadV4ModMetadata(modFolder);
        var estRecords = estStore.Load().ToList();
        var estCount = estRecords.Count;
        var metaChanged = false;
        foreach (var id in ids)
        {
            metaChanged |= Remap(meta, model, id, new Dictionary<int, IReadOnlyList<string>>());
            metaChanged |= EstEntryService.ReleaseEstEntries(meta, modKey, model, id, [], estRecords);
        }
        if (metaChanged)
        {
            TouchV4ModMetadata(meta);
            WriteJsonAtomic(Path.Combine(modFolder, "meta.json"), meta);
        }
        if (estRecords.Count != estCount)
            estStore.Save(estRecords);
        foreach (var id in ids)
            DeleteUnmapped(meta, modFolder, id);

        var warnings = EstEntryService.UpdateEstEntries(modFolder, modDirectory, model, [], estStore);
        records.Remove(record);
        store.Save(records);
        return warnings;
    }

    private const string StateSchema = "instant-edit.hair-skeleton-state";

    /// <summary>The skeleton ID of a hair skeleton or physics file key, or null for any other key.</summary>
    private static int? SkeletonKeyId(string key)
        => SkeletonKey().Match(PathRules.NormalizeGamePath(key)) is { Success: true } match
            ? int.Parse(match.Groups[2].Value)
            : null;

    /// <summary>
    /// The keys of the model's recorded skeleton ID in every option that uses the model, with the files
    /// they map and the ID, as a model backup carries them; no keys without a record, and null when the
    /// model is used as no hair. Keys of other IDs (a modder's own, another model's) are never part of it.
    /// A file mapped in several options carries its bytes once.
    /// </summary>
    public static JsonObject? CaptureState(string modFolder, string modDirectory, string modelRelativePath, HairSkeletonStore store)
    {
        var model = PathRules.NormalizeGamePath(modelRelativePath);
        var meta = LoadV4ModMetadata(modFolder);
        var users = UsingContainers(meta, model);
        if (users.Count == 0)
            return null;
        var skeletonId = Record(store.Load(), EstEntryService.EstModKey(modFolder, modDirectory), model, modFolder)?.SkeletonId;
        var entries = new JsonArray();
        var captured = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var container in EstEntryService.EstContainers(meta).Where(container => users.Contains(container.Key)))
        {
            if (skeletonId is null || container.Data["Files"] is not JsonObject files)
                continue;
            foreach (var (key, value) in files)
            {
                if (SkeletonKeyId(key) != skeletonId || JsonString(value) is not { } relative)
                    continue;
                var data = captured.Add(PathRules.NormalizeGamePath(relative)) ? ReadModFile(modFolder, relative) : null;
                entries.Add(new JsonObject
                {
                    ["container"] = container.Key,
                    ["gamePath"] = key,
                    ["relativePath"] = relative,
                    ["data"] = data is null ? null : Convert.ToBase64String(data),
                });
            }
        }
        return new JsonObject
        {
            ["schema"] = StateSchema,
            ["version"] = 1,
            ["skeletonId"] = skeletonId,
            ["entries"] = entries,
        };
    }

    /// <summary>
    /// Put back the hair skeleton a model backup carries: in the options that use the model now, the
    /// keys of the recorded ID and of the state's ID make way for the state's keys; the state's files are
    /// written, the record follows the state's ID, and the previous ID's files no option maps any more
    /// are deleted. The old IDs of a move that didn't finish lose their keys in every option and their
    /// files (the backup's EST state puts back the EST entries). Keys of other IDs are never touched. An
    /// unreadable state changes nothing. Returns the warnings.
    /// </summary>
    public static List<string> RestoreState(
        string modFolder,
        string modDirectory,
        string modelRelativePath,
        JsonObject state,
        HairSkeletonStore store)
    {
        if (ReadState(modFolder, state) is not { } read)
            return ["The backup's hair skeleton is unreadable, so it was not restored."];
        var (skeletonId, entries) = read;
        var model = PathRules.NormalizeGamePath(modelRelativePath);
        var meta = LoadV4ModMetadata(modFolder);
        var targets = UsingContainers(meta, model);
        var containers = EstEntryService.EstContainers(meta).Where(container => targets.Contains(container.Key))
            .ToDictionary(container => container.Key, StringComparer.Ordinal);
        var warnings = new List<string>();
        var kept = entries.Where(entry => targets.Contains(entry.Container)).ToList();
        if (kept.Count < entries.Count)
            warnings.Add("Some Penumbra options the backup's hair skeleton was mapped in no longer use this model, so it was not restored there.");
        var modKey = EstEntryService.EstModKey(modFolder, modDirectory);
        var records = store.Load().ToList();
        var record = Record(records, modKey, model, modFolder);
        var previous = record?.SkeletonId;
        var moving = (record?.MovingFrom ?? []).Where(id => id != skeletonId).ToList();
        int?[] ours = [previous, skeletonId, .. moving.Select(id => (int?)id)];

        // The files go first, so meta.json never maps one that isn't written yet.
        var data = entries.Where(entry => entry.Data is not null)
            .DistinctBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(entry => entry.Path, entry => entry.Data!, StringComparer.OrdinalIgnoreCase);
        foreach (var path in kept.Select(entry => entry.Path).Distinct(StringComparer.OrdinalIgnoreCase))
            if (data.TryGetValue(path, out var bytes))
                WriteAtomic(path, bytes);

        var metaChanged = false;
        foreach (var (key, target) in containers)
        {
            var container = target.Data;
            var wanted = kept.Where(entry => entry.Container == key).Select(entry => (entry.GamePath, entry.RelativePath))
                .ToHashSet();
            var files = container["Files"] as JsonObject;
            var current = (files ?? new JsonObject())
                .Where(pair => SkeletonKeyId(pair.Key) is { } id && ours.Contains(id))
                .Select(pair => (GamePath: pair.Key, RelativePath: JsonString(pair.Value))).ToList();
            if (current.Count == wanted.Count && current.All(pair => pair.RelativePath is not null &&
                                                                     wanted.Contains((pair.GamePath, pair.RelativePath))))
                continue;
            if (files is null)
                container["Files"] = files = new JsonObject();
            foreach (var (gamePath, _) in current)
                files.Remove(gamePath);
            foreach (var (gamePath, relative) in wanted)
                files[gamePath] = relative;
            metaChanged = true;
        }
        // An unfinished move's old IDs keep no key in any option, also one that no longer uses the model.
        foreach (var id in moving)
            metaChanged |= Remap(meta, model, id, new Dictionary<int, IReadOnlyList<string>>());
        if (metaChanged)
        {
            TouchV4ModMetadata(meta);
            WriteJsonAtomic(Path.Combine(modFolder, "meta.json"), meta);
        }
        // The record follows meta.json before any delete, so a file that can't be deleted never leaves it stale.
        if (previous != skeletonId || record?.MovingFrom is not null)
        {
            records.RemoveAll(item => Owns(item, modKey, model, modFolder));
            if (skeletonId is { } id)
                records.Add(new ManagedHairSkeleton(modKey, model, id,
                    ChainKinds: previous == skeletonId ? record?.ChainKinds : null, Folder: FolderName(modFolder)));
            store.Save(records);
        }
        foreach (var id in previous is { } recorded ? moving.Prepend(recorded).Distinct() : moving)
            DeleteUnmapped(meta, modFolder, id);
        return warnings;
    }

    /// <summary>One key of a hair skeleton state, with the full path of its file and the bytes it carries.</summary>
    private sealed record StateEntry(string Container, string GamePath, string RelativePath, string Path, byte[]? Data);

    /// <summary>A hair skeleton state's ID and keys, or null when anything in it is unreadable or unsafe.</summary>
    private static (int? SkeletonId, List<StateEntry> Entries)? ReadState(string modFolder, JsonObject state)
    {
        if (!string.Equals(JsonString(state["schema"]), StateSchema, StringComparison.Ordinal) ||
            JsonInt(state["version"]) != 1 || state["entries"] is not JsonArray items)
            return null;
        int? skeletonId = null;
        if (state["skeletonId"] is { } idNode)
        {
            if (idNode is not JsonValue idValue || !idValue.TryGetValue<int>(out var id) || id is < 1000 or > 9999)
                return null;
            skeletonId = id;
        }
        var entries = new List<StateEntry>();
        foreach (var node in items)
        {
            // Every key is one of the state's own ID, and its file is only ever written with the key's type:
            // a .sklb for a skl_ key, a .phyb for a phy_ key.
            if (node is not JsonObject item || JsonString(item["container"]) is not { Length: > 0 } container ||
                JsonString(item["gamePath"]) is not { } gamePath || SkeletonKeyId(gamePath) is not { } keyId ||
                keyId != skeletonId || JsonString(item["relativePath"]) is not { Length: > 0 } relative ||
                !string.Equals(Path.GetExtension(PathRules.NormalizeGamePath(relative)),
                    Path.GetExtension(PathRules.NormalizeGamePath(gamePath)), StringComparison.OrdinalIgnoreCase))
                return null;
            byte[]? data = null;
            if (item["data"] is { } dataNode)
            {
                if (JsonString(dataNode) is not { } text)
                    return null;
                try { data = Convert.FromBase64String(text); }
                catch (FormatException) { return null; }
            }
            string path;
            try { path = Destination(modFolder, relative); }
            catch (InvalidDataException) { return null; }
            entries.Add(new StateEntry(container, gamePath, relative, path, data));
        }
        return (skeletonId, entries);
    }

    /// <summary>The keys of the containers whose files map the model as hair.</summary>
    private static HashSet<string> UsingContainers(JsonObject meta, string model)
        => EstEntryService.HairUses(meta, model).Select(use => use.Container).ToHashSet(StringComparer.Ordinal);

    /// <summary>A mod file's bytes, or null when it is missing, unreadable, outside the mod or behind a reparse point.</summary>
    private static byte[]? ReadModFile(string modFolder, string relativePath)
    {
        try
        {
            var path = Destination(modFolder, relativePath);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The record is this mod folder's for the model: same EST key and model, and made in this folder, or
    /// in one that no longer exists (Penumbra renamed the mod). A folder copied with the mod's identifier
    /// shares its EST key, but the mod it was copied from keeps its records. Records from before the folder
    /// was kept belong to every folder with the key.
    /// </summary>
    private static bool Owns(ManagedHairSkeleton record, string modKey, string model, string modFolder)
    {
        if (!string.Equals(record.Mod, modKey, StringComparison.Ordinal) ||
            !string.Equals(record.Model, model, StringComparison.OrdinalIgnoreCase))
            return false;
        if (record.Folder is not { Length: > 0 } folder || string.Equals(folder, FolderName(modFolder), StringComparison.OrdinalIgnoreCase))
            return true;
        var library = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(modFolder)));
        return library is null || !Directory.Exists(Path.Combine(library, folder));
    }

    private static string FolderName(string modFolder) => Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(modFolder)));

    private static ManagedHairSkeleton? Record(IEnumerable<ManagedHairSkeleton> records, string modKey, string model, string modFolder)
        => records.LastOrDefault(record => Owns(record, modKey, model, modFolder));

    /// <summary>
    /// Give every container exactly the keys of skeleton <paramref name="id"/> for the races it uses the
    /// model for that <paramref name="built"/> has files for (gender-race to game paths). True when a key changed.
    /// </summary>
    private static bool Remap(JsonObject meta, string model, int id, IReadOnlyDictionary<int, IReadOnlyList<string>> built)
    {
        var uses = EstEntryService.HairUses(meta, model);
        var changed = false;
        foreach (var container in EstEntryService.EstContainers(meta))
        {
            if (container.Data["Files"] is not JsonObject files)
                continue;
            var races = uses.Where(use => use.Container == container.Key && built.ContainsKey(use.GenderRace))
                .Select(use => use.GenderRace).ToHashSet();
            foreach (var (key, value) in files.ToList())
            {
                if (SkeletonKey().Match(PathRules.NormalizeGamePath(key)) is not { Success: true } match ||
                    int.Parse(match.Groups[2].Value) != id)
                    continue;
                var race = int.Parse(match.Groups[1].Value);
                // Keep a key only in the exact form Apply writes; anything else is rewritten below.
                if (races.Contains(race) && built[race].Contains(key, StringComparer.Ordinal) &&
                    string.Equals(JsonString(value), ModRelative(key), StringComparison.Ordinal))
                    continue;
                files.Remove(key);
                changed = true;
            }
            foreach (var gamePath in races.SelectMany(race => built[race]))
            {
                if (files.ContainsKey(gamePath))
                    continue;
                files[gamePath] = ModRelative(gamePath);
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>Penumbra writes mod-relative paths with backslashes.</summary>
    private static string ModRelative(string gamePath) => gamePath.Replace('/', '\\');

    /// <summary>Delete the skeleton and physics files, at any player race's paths, that no container maps.</summary>
    private static void DeleteUnmapped(JsonObject meta, string modFolder, int id)
    {
        var mapped = EstEntryService.EstContainers(meta)
            .SelectMany(container => container.Data["Files"] as JsonObject ?? new JsonObject())
            .Select(pair => PathRules.NormalizeGamePath(JsonString(pair.Value)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var root = Path.GetFullPath(modFolder);
        var paths = PlayerRaces.SelectMany(race => new[]
        {
            ModelSkeletonPaths.PartialPath(RaceLabel(race), EstSlot.Hair, id), HairPhysicsBuilder.PhysicsPath(race, id),
        });
        foreach (var relative in paths)
        {
            if (mapped.Contains(relative))
                continue;
            var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            var folder = Path.GetDirectoryName(path)!;
            if (!File.Exists(path) || !IsPathWithin(path, root) || HasReparsePointInPath(root, folder) ||
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                continue;
            File.Delete(path);
            RemoveEmptyFolders(root, folder);
        }
    }

    /// <summary>Remove <paramref name="folder"/> and its parents while they are empty, up to the mod folder.</summary>
    private static void RemoveEmptyFolders(string root, string folder)
    {
        var current = folder;
        while (IsPathWithin(current, root) && !string.Equals(Path.TrimEndingDirectorySeparator(current),
                   Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (Directory.EnumerateFileSystemEntries(current).Any())
                    return;
                Directory.Delete(current);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return;
            }
            current = Path.GetDirectoryName(current)!;
        }
    }

    /// <summary>The full path of a mod-relative file; refuses one outside the mod folder or behind a reparse point.</summary>
    private static string Destination(string modFolder, string relativePath)
    {
        var root = Path.GetFullPath(modFolder);
        var path = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsPathWithin(path, root) || HasReparsePointInPath(root, Path.GetDirectoryName(path)!) ||
            (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
            throw new InvalidDataException("The hair skeleton's destination in the mod is unsafe.");
        return path;
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        var folder = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(folder);
        var temporary = Path.Combine(folder, $".instant-edit-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
