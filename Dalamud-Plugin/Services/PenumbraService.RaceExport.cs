using System.Text.Json.Nodes;
using InstantEdit.Services.Animations;
using InstantEdit.Services.Painter;
using InstantEdit.Services.Skeletons;

namespace InstantEdit.Services;

public sealed partial class PenumbraService
{
    // EQDP files keep two bits per slot, in this order: the material flag, then the model flag.
    private static readonly string[] EquipmentEqdpSlots = ["Head", "Body", "Hands", "Legs", "Feet"];
    private static readonly string[] AccessoryEqdpSlots = ["Ears", "Neck", "Wrists", "RFinger", "LFinger"];

    /// <summary>
    /// Where a race export writes: the race's game path, the file the mod maps there (new or the one it
    /// already mapped), the updated metadata and the options it went into.
    /// </summary>
    internal sealed record RaceModelPlan(string GamePath, string RelativePath, JsonObject Meta, IReadOnlyList<string> Containers);

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
    /// already map there, else a new one next to the imported model named after the race's game file
    /// (<c>c0801e6000_top.mdl</c>) that the mod doesn't use yet.
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
        if (relative is null)
        {
            var used = MappedModFiles(meta);
            var directory = model.Contains('/') ? model[..(model.LastIndexOf('/') + 1)] : "";
            var stem = Path.GetFileNameWithoutExtension(racePath[(racePath.LastIndexOf('/') + 1)..]);
            for (var count = 1; count <= 100 && relative is null; count++)
            {
                var candidate = $"{directory}{(count == 1 ? stem : $"{stem} {count}")}.mdl";
                if (IsSafeRelativeModPath(candidate) && !used.Contains(candidate) &&
                    !string.Equals(candidate, model, StringComparison.OrdinalIgnoreCase))
                    relative = candidate;
            }
            if (relative is null)
                return $"No free file name for the c{race:D4} model.";
        }

        foreach (var container in containers)
        {
            var files = (JsonObject)container.Data["Files"]!;
            foreach (var key in files.Select(pair => pair.Key).Where(key => SameGamePath(key, racePath)).ToArray())
                files.Remove(key);
            files[racePath] = relative;
            SetEqdpModel(container.Data, identity, race, gameEqdpBits);
        }
        TouchV4ModMetadata(meta);
        plan = new RaceModelPlan(racePath, relative, meta, containers.Select(container => container.Label).ToArray());
        return null;
    }

    /// <summary>The model's slot bits in the game's EQDP file for the race, or null when it can't be read.</summary>
    private int? GameEqdpBits(string racePath, int race)
    {
        if (_data is null || ParseAttributeModelIdentity(racePath) is not { } identity)
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
                () => ResolveSourceModTargetOnFramework(
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
                ? CaptureEstState(resolved.Target.Folder, resolved.Target.Directory, plan.RelativePath, _estEntries)
                : null;
            var writeError = WriteModelToOriginalLocation(
                resolved.Target.Folder, targetFile, exportedFile, backupExisting,
                resolved.Target.Directory, plan.RelativePath, estState);
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
                var attributeError = WriteAttributeGroups(
                    resolved.Target.Folder, resolvedGamePath, attributeTags, attributeMasks,
                    sourceManipulations);
                if (attributeError is not null)
                    warnings.Add($"Penumbra attribute group setup failed: {AttributeGroupErrorMessage(attributeError)}");
            }

            warnings.AddRange(TryUpdateEstEntries(
                resolved.Target.Folder, resolved.Target.Directory, plan.RelativePath, estEntries));

            var reloadError = await _framework.RunOnFrameworkThread(
                () => ReloadModOnFramework(resolved.Target.Directory)).ConfigureAwait(false);
            if (reloadError is not null)
                warnings.Add(reloadError.Message);
            else
            {
                var redrawWarning = await _framework.RunOnFrameworkThread(
                    RedrawPlayerOwnedEntitiesOnFramework).ConfigureAwait(false);
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

    /// <summary>A game model's first race export: the race's model in a new mod's default data, with the EQDP entry.</summary>
    private void AddRaceModelEqdp(string staging, string racePath, int race)
    {
        var meta = LoadV4ModMetadata(staging);
        if (ParseAttributeModelIdentity(racePath) is not { } identity || meta["DefaultData"] is not JsonObject defaultData)
            throw new InvalidDataException("The new mod has no default data for the race's model.");
        SetEqdpModel(defaultData, identity, race, GameEqdpBits(racePath, race));
        TouchV4ModMetadata(meta);
        WriteJsonAtomic(Path.Combine(staging, "meta.json"), meta);
    }
}
