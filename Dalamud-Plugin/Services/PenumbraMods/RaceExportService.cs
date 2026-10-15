using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Dalamud.Plugin.Services;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Painter;
using InstantEdit.Services.Skeletons;
using static InstantEdit.Services.PenumbraMods.AttributeGroupService;
using static InstantEdit.Services.PenumbraMods.EstEntryService;
using static InstantEdit.Services.PenumbraMods.FrameworkThread;
using static InstantEdit.Services.PenumbraMods.ModJson;
using static InstantEdit.Services.PenumbraMods.ModMetadata;
using static InstantEdit.Services.PenumbraMods.ModPaths;

namespace InstantEdit.Services.PenumbraMods;

/// <summary>
/// Saves an export as another race's own copy of a gear or accessory model: the model next to the
/// imported one, mapped at the race's game path in the options that use it, with the race's EQDP
/// model flag, and a restore point that undoes all of it.
/// </summary>
internal sealed class RaceExportService
{
    private readonly IFramework _framework;
    private readonly IPluginLog _log;
    private readonly SemaphoreSlim _exportGate;
    private readonly IDataManager _data;
    private readonly ModelBackupStore _backups;
    private readonly SourceMods _sources;
    private readonly ModActivation _activation;
    private readonly ModelFileWriter _writer;
    private readonly AttributeGroupService _attributes;
    private readonly EstEntryService _est;

    public RaceExportService(IFramework framework, IPluginLog log, SemaphoreSlim exportGate, IDataManager data,
        ModelBackupStore backups, SourceMods sources, ModActivation activation, ModelFileWriter writer,
        AttributeGroupService attributes, EstEntryService est)
    {
        _framework = framework;
        _log = log;
        _exportGate = exportGate;
        _data = data;
        _backups = backups;
        _sources = sources;
        _activation = activation;
        _writer = writer;
        _attributes = attributes;
        _est = est;
    }

    // EQDP files keep two bits per slot, in this order: the material flag, then the model flag.
    private static readonly string[] EquipmentEqdpSlots = ["Head", "Body", "Hands", "Legs", "Feet"];
    private static readonly string[] AccessoryEqdpSlots = ["Ears", "Neck", "Wrists", "RFinger", "LFinger"];

    /// <summary>
    /// Where a race export writes: the race's game path, the file the mod maps there (new or the one it
    /// already mapped), the updated metadata and the options it went into. <paramref name="Undo"/> holds
    /// what each of those options had before (see <see cref="RestoreRaceExportMeta"/>).
    /// </summary>
    internal sealed record RaceModelPlan(string GamePath, string RelativePath, JsonObject Meta, IReadOnlyList<string> Containers,
        JsonArray Undo);

    private const string RaceStateSchema = "instant-edit.race-export";

    /// <summary>The gender-race code of a player race such as <c>c0801</c> (801), or null.</summary>
    internal static int? PlayerRaceCode(string? race) => EstRaceCode(race);

    /// <summary>
    /// The game path a race loads its own copy of a gear or accessory model from
    /// (<c>chara/equipment/e6000/model/c0801e6000_top.mdl</c> for c0201e6000_top.mdl and 801), or null for
    /// other models, other races than players' and the model's own race. Only gear and accessories pick
    /// a race's own model through EQDP; the rest are loaded by their race's path.
    /// </summary>
    internal static string? RaceModelGamePath(string gamePath, int race)
    {
        if (PlayerRaceCode($"c{race:D4}") is null || ParseAttributeModelIdentity(gamePath) is not
                { ObjectType: "Equipment" or "Accessory" } identity || identity.RaceCode == race)
            return null;
        return ModelSkeletonPaths.WithRace(NormalizeGamePath(gamePath), (ushort)race);
    }

    private static int EqdpSlotIndex(AttributeModelIdentity identity)
        => Array.IndexOf(identity.ObjectType == "Accessory" ? AccessoryEqdpSlots : EquipmentEqdpSlots, identity.EquipSlot);

    private static bool MatchesEqdp(JsonObject item, AttributeModelIdentity identity, int genderRace)
    {
        if (!string.Equals(JsonString(item["Type"]), "Eqdp", StringComparison.OrdinalIgnoreCase) ||
            item["Manipulation"] is not JsonObject manipulation)
            return false;
        // Penumbra writes the slot by name and reads its EquipSlot number too.
        var slot = manipulation["Slot"] is JsonValue slotValue && slotValue.TryGetValue<int>(out var slotNumber)
            ? slotNumber is > 0 and <= byte.MaxValue && AnimationMetadata.EquipSlotName((byte)slotNumber) == identity.EquipSlot
            : string.Equals(JsonString(manipulation["Slot"]), identity.EquipSlot, StringComparison.OrdinalIgnoreCase);
        var (gender, race) = AnimationMetadata.GenderRaceNames((ushort)genderRace);
        return slot && JsonIntFlexible(manipulation["SetId"]) == identity.Id &&
               SameEstName(manipulation["Gender"], gender, EstGenderNames) &&
               SameEstName(manipulation["Race"], race, EstModelRaceNames);
    }

    /// <summary>
    /// Turn on the model flag of a container's EQDP entry for the race's set and slot, so the race loads
    /// its own model; duplicates collapse into one. The material flag stays as the container had it, else
    /// as the game's file has it (<paramref name="gameBits"/>, the slot's two bits), else on.
    /// </summary>
    private static void SetEqdpModel(JsonObject container, AttributeModelIdentity identity, int genderRace, int? gameBits)
    {
        var index = EqdpSlotIndex(identity);
        var manipulations = container["Manipulations"] as JsonArray;
        if (manipulations is null)
            container["Manipulations"] = manipulations = [];
        int? current = null;
        var first = -1;
        for (var at = manipulations.Count - 1; at >= 0; --at)
        {
            if (manipulations[at] is not JsonObject item || !MatchesEqdp(item, identity, genderRace))
                continue;
            if (item["Manipulation"]!["Entry"] is JsonValue &&
                JsonIntFlexible(item["Manipulation"]!["Entry"]) is >= 0 and <= ushort.MaxValue and var entry)
                current = entry >> (2 * index) & 3;
            manipulations.RemoveAt(at);
            first = at;
        }
        var material = (current ?? gameBits ?? 1) & 1;
        var (gender, race) = AnimationMetadata.GenderRaceNames((ushort)genderRace);
        var manipulation = new JsonObject
        {
            ["Type"] = "Eqdp",
            ["Manipulation"] = new JsonObject
            {
                // Penumbra keeps the slot's bits of the whole entry.
                ["Entry"] = (material | 2) << (2 * index),
                ["Gender"] = gender,
                ["Race"] = race,
                ["SetId"] = identity.Id,
                ["Slot"] = identity.EquipSlot,
            },
        };
        if (first >= 0)
            manipulations.Insert(first, manipulation);
        else
            manipulations.Add(manipulation);
    }

    /// <summary>
    /// Plan a race export: every container (default data, option or combination) that maps the imported
    /// model at <paramref name="sourceGamePath"/>, or already maps it at the race's path, maps the race's
    /// path to one file and turns on the race's EQDP model flag. The file is the one those containers
    /// already map there when the mod maps it nowhere else, else a new one next to the imported model
    /// named after the race's game file (<c>c0801e6000_top.mdl</c>) that the mod doesn't use or hold yet.
    /// </summary>
    internal static string? PlanRaceModel(
        string modFolder,
        string sourceGamePath,
        string modelRelativePath,
        int race,
        int? gameEqdpBits,
        out RaceModelPlan? plan)
    {
        plan = null;
        if (RaceModelGamePath(sourceGamePath, race) is not { } racePath ||
            ParseAttributeModelIdentity(racePath) is not { } identity || EqdpSlotIndex(identity) < 0)
            return "Only gear and accessory models can be saved for another race.";
        if (!TryNormalizeRelativeModPath(modelRelativePath.TrimStart('/', '\\'), out var model))
            return "The imported model is outside its mod.";
        var meta = LoadV4ModMetadata(modFolder);
        var containers = new List<EstContainer>();
        var mapped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var container in EstContainers(meta))
        {
            if (container.Data["Files"] is not JsonObject files)
                continue;
            var usesModel = false;
            string? raceFile = null;
            foreach (var (gamePath, value) in files)
            {
                if (!TryNormalizeRelativeModPath(JsonString(value)?.TrimStart('/', '\\'), out var file))
                    continue;
                if (SameGamePath(gamePath, racePath))
                    raceFile = file;
                else if (SameGamePath(gamePath, sourceGamePath) && string.Equals(file, model, StringComparison.OrdinalIgnoreCase))
                    usesModel = true;
            }
            if (!usesModel && !string.Equals(raceFile, model, StringComparison.OrdinalIgnoreCase))
                continue;
            containers.Add(container);
            if (raceFile is not null)
                mapped.Add(raceFile);
        }
        var modelName = model[(model.LastIndexOf('/') + 1)..];
        if (containers.Count == 0)
            return $"The mod doesn't use {modelName} for {NormalizeGamePath(sourceGamePath)}.";
        if (mapped.Count > 1)
            return $"The mod's options map {racePath} to different files.";

        var relative = mapped.SingleOrDefault();
        // The race's file is written over only when nothing else uses it: an "all races" mod mapping one
        // file at several races' paths, or another option mapping it, gets a new file instead.
        if (relative is not null && !OnlyMappedAt(meta, relative, racePath, containers.Select(container => container.Data)))
            relative = null;
        if (relative is null)
        {
            var used = MappedModFiles(meta);
            var directory = model.Contains('/') ? model[..(model.LastIndexOf('/') + 1)] : "";
            var stem = Path.GetFileNameWithoutExtension(racePath[(racePath.LastIndexOf('/') + 1)..]);
            for (var count = 1; count <= 100 && relative is null; count++)
            {
                var candidate = $"{directory}{(count == 1 ? stem : $"{stem} {count}")}.mdl";
                // An unmapped file already there (an author's alternative or a leftover) is never taken either.
                if (IsSafeRelativeModPath(candidate) && !used.Contains(candidate) &&
                    !string.Equals(candidate, model, StringComparison.OrdinalIgnoreCase) &&
                    !File.Exists(Path.Combine(modFolder, candidate.Replace('/', Path.DirectorySeparatorChar))))
                    relative = candidate;
            }
            if (relative is null)
                return $"No free file name for the c{race:D4} model.";
        }

        var undo = new JsonArray();
        foreach (var container in containers)
        {
            var files = (JsonObject)container.Data["Files"]!;
            var before = new JsonObject();
            foreach (var key in files.Select(pair => pair.Key).Where(key => SameGamePath(key, racePath)).ToArray())
            {
                before[key] = files[key]?.DeepClone();
                files.Remove(key);
            }
            var eqdp = new JsonArray();
            if (container.Data["Manipulations"] is JsonArray manipulations)
                for (var at = 0; at < manipulations.Count; at++)
                    if (manipulations[at] is JsonObject item && MatchesEqdp(item, identity, race))
                        eqdp.Add(new JsonObject { ["index"] = at, ["item"] = item.DeepClone() });
            undo.Add(new JsonObject { ["container"] = container.Key, ["label"] = container.Label, ["files"] = before, ["eqdp"] = eqdp });
            files[racePath] = relative;
            SetEqdpModel(container.Data, identity, race, gameEqdpBits);
        }
        TouchV4ModMetadata(meta);
        plan = new RaceModelPlan(racePath, relative, meta, containers.Select(container => container.Label).ToArray(), undo);
        return null;
    }

    /// <summary>
    /// Undo a race export's changes to the mod's options, as <paramref name="state"/> recorded them
    /// (see <see cref="RaceModelPlan.Undo"/>): each option's race path mapping and the race's EQDP entries
    /// for the set and slot go back to what they were. Returns the warnings, such as an option that's gone.
    /// </summary>
    internal static List<string> RestoreRaceExportMeta(string modFolder, JsonObject state)
    {
        if (!string.Equals(JsonString(state["schema"]), RaceStateSchema, StringComparison.Ordinal) || JsonInt(state["version"]) != 1 ||
            JsonString(state["racePath"]) is not { } racePath || !IsSafeGamePath(racePath) ||
            ParseAttributeModelIdentity(racePath) is not { } identity || EqdpSlotIndex(identity) < 0 ||
            state["containers"] is not JsonArray recorded)
            return ["The backup's race model record is unreadable, so the mod's options were not restored."];
        var warnings = new List<string>();
        var meta = LoadV4ModMetadata(modFolder);
        var containers = EstContainers(meta).ToDictionary(container => container.Key, StringComparer.Ordinal);
        foreach (var item in recorded.OfType<JsonObject>())
        {
            if (!containers.TryGetValue(JsonString(item["container"]) ?? "", out var container))
            {
                warnings.Add($"The Penumbra option \"{JsonString(item["label"])}\" no longer exists, so its race model wasn't restored.");
                continue;
            }
            if (container.Data["Files"] is not JsonObject files)
                container.Data["Files"] = files = new JsonObject();
            foreach (var key in files.Select(pair => pair.Key).Where(key => SameGamePath(key, racePath)).ToArray())
                files.Remove(key);
            foreach (var (key, value) in item["files"] as JsonObject ?? new JsonObject())
                if (SameGamePath(key, racePath))
                    files[key] = value?.DeepClone();

            if (container.Data["Manipulations"] is not JsonArray manipulations)
                container.Data["Manipulations"] = manipulations = [];
            for (var at = manipulations.Count - 1; at >= 0; --at)
                if (manipulations[at] is JsonObject current && MatchesEqdp(current, identity, identity.RaceCode))
                    manipulations.RemoveAt(at);
            foreach (var entry in (item["eqdp"] as JsonArray ?? []).OfType<JsonObject>().OrderBy(entry => JsonInt(entry["index"])))
                if (entry["item"] is JsonObject previous && MatchesEqdp(previous, identity, identity.RaceCode))
                    manipulations.Insert(Math.Clamp(JsonInt(entry["index"]), 0, manipulations.Count), previous.DeepClone());
        }
        TouchV4ModMetadata(meta);
        WriteJsonAtomic(Path.Combine(modFolder, "meta.json"), meta);
        return warnings;
    }

    /// <summary>
    /// Whether every mapping of <paramref name="file"/> in the mod (default data, options and combining
    /// containers) is <paramref name="gamePath"/> in one of <paramref name="containers"/>.
    /// </summary>
    private static bool OnlyMappedAt(JsonObject meta, string file, string gamePath, IEnumerable<JsonObject> containers)
    {
        var allowed = containers.ToHashSet(ReferenceEqualityComparer.Instance);
        bool MapsElsewhere(JsonNode? node) => node is JsonObject container && container["Files"] is JsonObject files &&
            files.Any(pair => TryNormalizeRelativeModPath(JsonString(pair.Value)?.TrimStart('/', '\\'), out var mapped) &&
                              string.Equals(mapped, file, StringComparison.OrdinalIgnoreCase) &&
                              (!SameGamePath(pair.Key, gamePath) || !allowed.Contains(container)));
        if (MapsElsewhere(meta["DefaultData"]))
            return false;
        foreach (var group in (meta["Groups"] as JsonArray ?? []).OfType<JsonObject>())
            foreach (var key in new[] { "Options", "Containers" })
                if (group[key] is JsonArray entries && entries.Any(MapsElsewhere))
                    return false;
        return true;
    }

    /// <summary>The model's slot bits in the game's EQDP file for the race, or null when it can't be read.</summary>
    private int? GameEqdpBits(string racePath, int race)
    {
        if (ParseAttributeModelIdentity(racePath) is not { } identity)
            return null;
        var index = EqdpSlotIndex(identity);
        if (index < 0)
            return null;
        try
        {
            var folder = identity.ObjectType == "Accessory" ? "accessorydeformerparameter" : "equipmentdeformerparameter";
            var file = _data.GetFile($"chara/xls/charadb/{folder}/c{race:D4}.eqdp");
            return file is null ? null : PainterSmallclothes.EqdpEntry(file.Data, identity.Id) >> (2 * index) & 3;
        }
        catch (Exception e)
        {
            _log.Debug($"Could not read the game's EQDP entry for {racePath}: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Write meshes scaled for <paramref name="race"/> as that race's model into the original Penumbra mod
    /// (see PlanRaceModel): the model next to the imported one, then the mod's options, then one reload
    /// and redraw.
    /// </summary>
    public async Task<ExportResult> ApplyRaceExportAsync(
        string sourceModDirectory,
        string sourceFilePath,
        string? sourceModRootPath,
        string? targetRelativePath,
        string sourceGamePath,
        int race,
        string exportedFile,
        bool backupExisting = false,
        Guid? sourceModStableId = null,
        bool createAttributeGroups = false,
        IReadOnlyList<string>? attributeTags = null,
        IReadOnlyDictionary<string, int>? attributeMasks = null,
        string? resolvedGamePath = null,
        JsonArray? sourceManipulations = null,
        IReadOnlyList<EstEntryRequest>? estEntries = null)
    {
        resolvedGamePath ??= sourceGamePath;
        if (!IsSafeLocalModelPath(sourceFilePath))
            return new ExportResult(false, "destination_unsafe", "The original Penumbra model destination is invalid.");
        var validationError = ValidateExportRequest(sourceModDirectory, sourceGamePath, exportedFile);
        if (validationError is not null)
            return new ExportResult(false, "invalid_export_file", validationError);
        if (RaceModelGamePath(sourceGamePath, race) is not { } racePath)
            return new ExportResult(false, "race_model_unavailable", "Only gear and accessory models can be saved for another race.");

        await _exportGate.WaitAsync().ConfigureAwait(false);
        string? committedTarget = null;
        try
        {
            var resolved = await _framework.RunOnFrameworkThread(
                () => _sources.ResolveSourceModTargetOnFramework(
                    sourceModDirectory,
                    sourceFilePath,
                    sourceModRootPath,
                    targetRelativePath,
                    sourceModStableId)).ConfigureAwait(false);
            if (resolved.Target is null)
                return new ExportResult(
                    false,
                    resolved.Code,
                    resolved.Error ?? "The original Penumbra mod is no longer available.");
            await LeaveFrameworkThread();
            if (createAttributeGroups && attributeTags is { Count: > 0 })
            {
                var attributeError = ValidateAttributeGroups(
                    resolved.Target.Folder, resolvedGamePath, attributeTags, attributeMasks);
                if (attributeError is not null)
                    return new ExportResult(false, AttributeGroupErrorCode(attributeError),
                        AttributeGroupErrorMessage(attributeError));
            }

            var planError = PlanRaceModel(resolved.Target.Folder, sourceGamePath, resolved.Target.RelativePath, race,
                GameEqdpBits(racePath, race), out var plan);
            if (planError is not null)
                return new ExportResult(false, "race_model_unavailable", planError);

            var targetFile = Path.Combine(resolved.Target.Folder, plan!.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            // The backup keeps the model's EST entries too, so restoring it undoes this export's EST change.
            var estState = backupExisting && File.Exists(targetFile)
                ? _est.CaptureEstState(resolved.Target.Folder, resolved.Target.Directory, plan.RelativePath)
                : null;
            if (backupExisting)
            {
                var backupError = CreateRaceRestorePoint(resolved.Target.Folder, resolved.Target.Directory,
                    resolved.Target.FilePath, plan, targetFile, exportedFile, estState);
                if (backupError is not null)
                    return new ExportResult(false, "write_failed", backupError);
            }
            // The restore point already backed up the race's file.
            var writeError = _writer.WriteModelToOriginalLocation(
                resolved.Target.Folder, targetFile, exportedFile, false,
                resolved.Target.Directory, plan.RelativePath);
            if (writeError is not null)
                return new ExportResult(false, "write_failed", writeError);
            committedTarget = targetFile;

            var warnings = new List<string>();
            try
            {
                WriteJsonAtomic(Path.Combine(resolved.Target.Folder, "meta.json"), plan.Meta);
            }
            catch (Exception e)
            {
                warnings.Add($"The c{race:D4} model was written, but the mod's options weren't updated: {e.Message}");
            }

            if (createAttributeGroups && attributeTags is { Count: > 0 })
            {
                var attributeError = _attributes.WriteAttributeGroups(
                    resolved.Target.Folder, resolvedGamePath, attributeTags, attributeMasks,
                    sourceManipulations);
                if (attributeError is not null)
                    warnings.Add($"Penumbra attribute group setup failed: {AttributeGroupErrorMessage(attributeError)}");
            }

            warnings.AddRange(_est.TryUpdateEstEntries(
                resolved.Target.Folder, resolved.Target.Directory, plan.RelativePath, estEntries));

            var reloadError = await _framework.RunOnFrameworkThread(
                () => _activation.ReloadModOnFramework(resolved.Target.Directory)).ConfigureAwait(false);
            if (reloadError is not null)
                warnings.Add(reloadError.Message);
            else
            {
                var redrawWarning = await _framework.RunOnFrameworkThread(
                    _activation.RedrawPlayerOwnedEntitiesOnFramework).ConfigureAwait(false);
                if (redrawWarning is not null)
                    warnings.Add(redrawWarning);
            }

            var code = warnings.Count == 0 ? "race_model_exported" : "race_model_exported_with_warnings";
            var message = warnings.Count == 0
                ? $"Exported to {targetFile} as the c{race:D4} model ({string.Join(", ", plan.Containers)}) and reloaded {resolved.Target.Directory}."
                : $"Exported to {targetFile} as the c{race:D4} model; {warnings.Count} follow-up warning(s).";
            return new ExportResult(true, code, message, warnings, targetFile);
        }
        catch (Exception e)
        {
            _log.Error(e, "Failed to export a race model to the original Penumbra mod.");
            if (committedTarget is not null)
                return new ExportResult(
                    true,
                    "race_model_exported_with_warnings",
                    $"Exported to {committedTarget}; follow-up processing failed.",
                    [$"Follow-up processing failed: {e.Message}"],
                    committedTarget);
            return new ExportResult(false, "write_failed", $"Failed before the model write could be committed: {e.Message}");
        }
        finally
        {
            _exportGate.Release();
        }
    }

    /// <summary>
    /// A race export's restore point: a backup of the race's file when it has one, then a backup of the
    /// imported model, the one its import lists, carrying what the export changes (the race's file, and
    /// each option's mapping and EQDP entries before it). Restoring that backup undoes the whole export.
    /// </summary>
    private string? CreateRaceRestorePoint(string modFolder, string modDirectory, string importedFile, RaceModelPlan plan,
        string raceFile, string exportedFile, JsonObject? estState)
    {
        if (!File.Exists(importedFile))
            return "The imported model is missing, so no backup could be made.";
        try
        {
            var state = new JsonObject
            {
                ["schema"] = RaceStateSchema,
                ["version"] = 1,
                ["racePath"] = plan.GamePath,
                ["raceFile"] = plan.RelativePath,
                ["created"] = !File.Exists(raceFile),
                ["writtenHash"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(exportedFile))),
                ["containers"] = plan.Undo.DeepClone(),
            };
            var importedRelative = Path.GetRelativePath(modFolder, importedFile).Replace('\\', '/');
            var sameFile = PathRules.SamePhysicalPath(Path.GetFullPath(raceFile), Path.GetFullPath(importedFile));
            if (File.Exists(raceFile) && !sameFile)
            {
                var raceBackup = _backups.Create(raceFile, modDirectory, plan.RelativePath);
                if (estState is not null)
                    _backups.WriteEstState(raceBackup, estState);
                state["raceBackup"] = Path.GetFileName(raceBackup);
            }
            var restorePoint = _backups.Create(importedFile, modDirectory, importedRelative);
            if (sameFile && estState is not null)
                _backups.WriteEstState(restorePoint, estState);
            _backups.WriteRaceState(restorePoint, state);
            return null;
        }
        catch (Exception e)
        {
            return $"Could not back up the models before the export: {e.Message}";
        }
    }

    /// <summary>
    /// Restoring a race export's restore point (see <see cref="CreateRaceRestorePoint"/>), after the imported
    /// model is back: the options as they were, then the race's file from its backup, or, when the export
    /// made it, gone, unless the mod maps it again or it changed since. Returns the warnings.
    /// </summary>
    internal List<string> UndoRaceExport(string modFolder, string modDirectory, JsonObject state)
    {
        var warnings = RestoreRaceExportMeta(modFolder, state);
        if (JsonString(state["raceFile"]) is not { } relative || !IsSafeRelativeModPath(relative))
            return warnings;
        var raceFile = Path.GetFullPath(Path.Combine(modFolder, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsPathWithin(raceFile, modFolder) || HasReparsePointInPath(modFolder, Path.GetDirectoryName(raceFile)!))
            return warnings;
        if (JsonString(state["raceBackup"]) is { } backupName)
        {
            string backup;
            try
            {
                backup = _backups.Resolve(_backups.Describe(modDirectory, relative).Id, backupName);
            }
            catch (Exception e) when (e is FileNotFoundException or ArgumentException)
            {
                warnings.Add($"The backup of {relative} is gone, so it wasn't restored.");
                return warnings;
            }
            if (_writer.RestoreModelBackup(raceFile, backup, modDirectory, relative) is { } error)
                warnings.Add(error);
        }
        else if (state["created"] is JsonValue created && created.TryGetValue<bool>(out var made) && made && File.Exists(raceFile) &&
                 !MappedModFiles(LoadV4ModMetadata(modFolder)).Contains(relative))
        {
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(raceFile))), JsonString(state["writtenHash"]),
                    StringComparison.OrdinalIgnoreCase))
                warnings.Add($"{relative} changed after the export, so it was kept.");
            else
                File.Delete(raceFile);
        }
        return warnings;
    }

    /// <summary>A game model's first race export: the race's model in a new mod's default data, with the EQDP entry.</summary>
    internal void AddRaceModelEqdp(string staging, string racePath, int race)
    {
        var meta = LoadV4ModMetadata(staging);
        if (ParseAttributeModelIdentity(racePath) is not { } identity || meta["DefaultData"] is not JsonObject defaultData)
            throw new InvalidDataException("The new mod has no default data for the race's model.");
        SetEqdpModel(defaultData, identity, race, GameEqdpBits(racePath, race));
        TouchV4ModMetadata(meta);
        WriteJsonAtomic(Path.Combine(staging, "meta.json"), meta);
    }
}
