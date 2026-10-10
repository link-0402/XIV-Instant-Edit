using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using InstantEdit.Models;
using Penumbra.Api.Enums;
using Penumbra.Api.Helpers;
using Penumbra.Api.IpcSubscribers;
using Lumina.Data;
using Lumina.Data.Files;

namespace InstantEdit.Services;

public sealed partial class PenumbraService
{
    private static string? ValidateAttributeGroupRequest(
        string resolvedGamePath,
        IReadOnlyList<string> tags,
        IReadOnlyDictionary<string, int>? masks)
    {
        return NormalizeAttributeGroupInput(
            resolvedGamePath, tags, masks, out _, out _, out _);
    }

    private static string? ValidateAttributeGroups(
        string modFolder,
        string resolvedGamePath,
        IReadOnlyList<string> tags,
        IReadOnlyDictionary<string, int>? masks)
    {
        var inputError = NormalizeAttributeGroupInput(
            resolvedGamePath, tags, masks, out _, out _, out var customTags);
        if (inputError is not null)
            return inputError;

        if (customTags.Count == 0)
            return null;

        var identity = ParseAttributeModelIdentity(resolvedGamePath);
        if (identity is null)
            return "attribute_group_invalid: The resolved model path is not a supported gear, accessory, hair, or face model path.";
        var meta = LoadV4ModMetadata(modFolder);
        var marker = AttributeGroupMarker("atr", NormalizeGamePath(resolvedGamePath));
        var groups = meta["Groups"] as JsonArray ?? new JsonArray();
        var previousManagedAtrTags = ManagedAtrTags(groups, marker);
        var compatibleDefaultTags = new HashSet<string>(previousManagedAtrTags, StringComparer.Ordinal);
        compatibleDefaultTags.UnionWith(customTags);
        return HasUnmanagedAtrConflict(meta, marker, identity, compatibleDefaultTags)
            ? "attribute_group_conflict: An existing Penumbra option already contains an unmanaged atrx_ toggle overlapping this model ID and slot. Remove the existing atrx_ toggles first, then retry the export."
            : null;
    }

    private string? WriteAttributeGroups(
        string modFolder,
        string resolvedGamePath,
        IReadOnlyList<string> tags,
        IReadOnlyDictionary<string, int>? masks,
        JsonArray? sourceManipulations)
        => WriteAttributeGroupsCore(
            modFolder, resolvedGamePath, tags, masks,
            identity => PlanImcGroupEntry(identity, sourceManipulations, GameImcEntries(resolvedGamePath, identity)));

    /// <summary> The entry a generated IMC group starts from; with <see cref="OnlyAttributes"/> Penumbra takes everything but the mask from the game's file. </summary>
    private sealed record ImcGroupEntry(JsonObject? Entry, bool OnlyAttributes, string? Error = null);

    private static string? WriteAttributeGroupsCore(
        string modFolder,
        string resolvedGamePath,
        IReadOnlyList<string> tags,
        IReadOnlyDictionary<string, int>? masks,
        Func<AttributeModelIdentity, ImcGroupEntry> resolveImcEntry)
    {
        var inputError = NormalizeAttributeGroupInput(
            resolvedGamePath, tags, masks, out var identity, out var suffixMasks, out _);
        if (inputError is not null)
            return inputError;
        ImcGroupEntry? imcEntry = null;
        string? refusal = null;
        if (identity is not null && suffixMasks.Count > 0)
        {
            imcEntry = resolveImcEntry(identity);
            // Part toggles the checks refuse get no IMC group, and one an earlier export made goes too, or it
            // would keep undoing the mod's variants; the atrx_ toggles are still written. The refusal is the warning.
            if (imcEntry.Error is not null)
                (refusal, imcEntry) = (imcEntry.Error, null);
            else if (imcEntry.Entry is null)
                return "attribute_group_imc_unavailable: Could not resolve the current IMC entry for this model. Re-import it with game data available, then retry the export.";
        }

        var error = PrepareAttributeGroups(
            modFolder, resolvedGamePath, tags, masks, imcEntry, out var prepared, withoutImc: refusal is not null);
        if (error is not null)
            return error;
        return (prepared is null ? null : CommitAttributeGroups(prepared)) ?? refusal;
    }

    /// <summary>
    /// Plans a generated IMC group. Penumbra writes it to every variant of the game's file, before the
    /// mod's own IMC entries, so those would be lost. A mod without IMC changes of its own keeps the
    /// game's entries (OnlyAttributes). A mod that changes them, such as a swap pointing every variant
    /// at its one material, gets its entries written into the group when they're all the same; when
    /// they differ, no single group can keep them and none is made. <paramref name="gameEntries"/> holds
    /// the game's entries by variant, 0 to the file's count; without it, only the mod's variant 1 is known.
    /// Null <paramref name="sourceManipulations"/> means the import couldn't read the mod's entries: then a model
    /// with variants gets no group, since it could undo entries nobody knows.
    /// </summary>
    private static ImcGroupEntry PlanImcGroupEntry(
        AttributeModelIdentity identity,
        JsonArray? sourceManipulations,
        IReadOnlyList<JsonObject>? gameEntries)
    {
        var own = SourceImcEntries(identity, sourceManipulations);
        if (gameEntries is not { Count: > 1 })
            return Usable(new ImcGroupEntry(own.GetValueOrDefault(1) ?? gameEntries?.FirstOrDefault(), true));
        if (sourceManipulations is null)
            return new ImcGroupEntry(null, false,
                "attribute_group_imc_unknown: The import didn't read this mod's variant settings, so a part toggle group could undo them. " +
                "Import the model again, then export.");

        var count = gameEntries.Count - 1;
        if (own.All(pair => pair.Key < 1 || pair.Key > count || SameImcEntry(pair.Value, gameEntries[pair.Key])))
            return Usable(new ImcGroupEntry(gameEntries[1], true));
        var effective = Enumerable.Range(1, count)
            .Select(variant => own.GetValueOrDefault(variant) ?? gameEntries[variant])
            .ToList();
        if (effective.All(entry => SameImcEntry(entry, effective[0])))
            return Usable(new ImcGroupEntry(effective[0], false));
        return new ImcGroupEntry(null, false,
            "attribute_group_imc_mixed: This mod changes the materials or effects of some of this model's variants, and a part toggle group would undo that. " +
            "Set up the part toggles in Penumbra instead.");

        static ImcGroupEntry Usable(ImcGroupEntry plan)
            => plan.Entry is not null && JsonIntFlexible(plan.Entry["MaterialId"]) is >= 1 and <= byte.MaxValue ? plan : plan with { Entry = null };
    }

    private static bool SameImcEntry(JsonObject first, JsonObject second)
        => new[] { "MaterialId", "DecalId", "VfxId", "MaterialAnimationId", "SoundId" }
            .All(key => JsonIntFlexible(first[key]) == JsonIntFlexible(second[key]));

    /// <summary> The game's IMC entries of the model's part, by variant from 0 to the file's count; null when there's no file. </summary>
    private IReadOnlyList<JsonObject>? GameImcEntries(string resolvedGamePath, AttributeModelIdentity identity)
    {
        if (_data is null)
            return null;

        try
        {
            var path = NormalizeGamePath(resolvedGamePath);
            var modelIndex = path.LastIndexOf("/model/", StringComparison.OrdinalIgnoreCase);
            if (modelIndex <= 0)
                return null;
            var folder = path[..modelIndex];
            var folderName = folder[(folder.LastIndexOf('/') + 1)..];
            var imc = _data.GetFile<ImcFile>($"{folder}/{folderName}.imc");
            if (imc is null)
                return null;

            var logicalPart = identity.AtrSlot switch
            {
                "Head" or "Ears" => 0,
                "Body" or "Neck" => 1,
                "Hands" or "Wrists" => 2,
                "Legs" or "RFinger" => 3,
                "Feet" or "LFinger" => 4,
                _ => -1,
            };
            if (logicalPart < 0 || (imc.PartMask & (1 << logicalPart)) == 0)
                return null;
            var partIndex = 0;
            for (var bit = 0; bit < logicalPart; ++bit)
                if ((imc.PartMask & (1 << bit)) != 0)
                    ++partIndex;

            var entries = new List<JsonObject> { Entry(imc.GetDefaultVariant(partIndex)) };
            for (var variant = 0; variant < imc.Count; ++variant)
                entries.Add(Entry(imc.GetVariant(partIndex, variant)));
            return entries;
        }
        catch (Exception error)
        {
            _log.Debug(error, "Could not resolve the game IMC entry for {GamePath}.", resolvedGamePath);
            return null;
        }

        static JsonObject Entry(ImcFile.ImageChangeData entry)
            => ImcEntry(
                entry.MaterialId,
                entry.DecalId,
                entry.VfxId,
                entry.MaterialAnimationId,
                0,
                entry.SoundId > 0x3F ? entry.SoundId >> 10 : entry.SoundId);
    }

    /// <summary> The mod's own IMC entries for the model's part, by variant; the first one captured for a variant wins, as in Penumbra. </summary>
    private static Dictionary<int, JsonObject> SourceImcEntries(
        AttributeModelIdentity identity,
        JsonArray? sourceManipulations)
    {
        var result = new Dictionary<int, JsonObject>();
        if (sourceManipulations is null)
            return result;
        foreach (var item in sourceManipulations.OfType<JsonObject>())
        {
            if (!string.Equals(JsonString(item["Type"]), "Imc", StringComparison.OrdinalIgnoreCase) ||
                item["Manipulation"] is not JsonObject manipulation ||
                manipulation["Entry"] is not JsonObject entry ||
                !string.Equals(JsonString(manipulation["ObjectType"]), identity.ObjectType,
                    StringComparison.OrdinalIgnoreCase) ||
                JsonIntFlexible(manipulation["PrimaryId"]) != identity.Id ||
                result.ContainsKey(JsonIntFlexible(manipulation["Variant"])))
                continue;
            if (identity.ObjectType is "Equipment" or "Accessory")
            {
                if (!string.Equals(JsonString(manipulation["EquipSlot"]), identity.EquipSlot,
                        StringComparison.OrdinalIgnoreCase))
                    continue;
            }
            else if (!string.Equals(JsonString(manipulation["BodySlot"]), identity.BodySlot,
                         StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var materialId = JsonIntFlexible(entry["MaterialId"]);
            if (materialId is < 1 or > byte.MaxValue)
                continue;
            result[JsonIntFlexible(manipulation["Variant"])] = ImcEntry(
                materialId,
                JsonByteOrZero(entry["DecalId"]),
                JsonByteOrZero(entry["VfxId"]),
                JsonByteOrZero(entry["MaterialAnimationId"]),
                0,
                Math.Clamp(JsonIntFlexible(entry["SoundId"]), 0, 0x3F));
        }
        return result;
    }

    private static int JsonByteOrZero(JsonNode? node)
    {
        var value = JsonIntFlexible(node);
        return value is >= byte.MinValue and <= byte.MaxValue ? value : 0;
    }

    private static string? PrepareAttributeGroups(
        string modFolder,
        string resolvedGamePath,
        IReadOnlyList<string> tags,
        IReadOnlyDictionary<string, int>? masks,
        ImcGroupEntry? imcEntry,
        out AttributeGroupWrite? prepared,
        bool withoutImc = false)
    {
        prepared = null;
        var inputError = NormalizeAttributeGroupInput(
            resolvedGamePath, tags, masks, out var identity, out var suffixMasks, out var customTags);
        if (inputError is not null)
            return inputError;
        if (identity is null)
            return "attribute_group_invalid: The resolved model path is not a supported gear, accessory, hair, or face model path.";

        var metaPath = Path.Combine(modFolder, "meta.json");
        var meta = LoadV4ModMetadata(modFolder);
        var groups = meta["Groups"] as JsonArray ?? new JsonArray();
        meta["Groups"] = groups;
        var markerPath = NormalizeGamePath(resolvedGamePath);
        var atrMarker = AttributeGroupMarker("atr", markerPath);
        var previousManagedAtrTags = ManagedAtrTags(groups, atrMarker);
        var compatibleDefaultTags = new HashSet<string>(previousManagedAtrTags, StringComparer.Ordinal);
        compatibleDefaultTags.UnionWith(customTags);
        if (customTags.Count > 0 && HasUnmanagedAtrConflict(
                meta, atrMarker, identity, compatibleDefaultTags))
            return "attribute_group_conflict: An existing Penumbra option already contains an unmanaged atrx_ toggle overlapping this model ID and slot. Remove the existing atrx_ toggles first, then retry the export.";

        UpdateManagedAtrDefaults(meta, identity, previousManagedAtrTags, customTags);

        if (suffixMasks.Count > 0 && !withoutImc)
        {
            if (imcEntry?.Entry is not { } defaultImcEntry)
                return "attribute_group_imc_unavailable: No current IMC entry was supplied for the generated group.";
            UpsertAttributeGroup(
                groups,
                AttributeGroupMarker("imc", markerPath),
                GeneratedAttributeGroupName(identity, markerPath, "IMC"),
                existing => BuildImcAttributeGroup(existing, identity, suffixMasks, defaultImcEntry, imcEntry.OnlyAttributes));
        }
        else
        {
            RemoveManagedAttributeGroup(groups, AttributeGroupMarker("imc", markerPath));
        }
        if (customTags.Count > 0)
        {
            UpsertAttributeGroup(
                groups,
                atrMarker,
                GeneratedAttributeGroupName(identity, markerPath, "ATR"),
                existing => BuildAtrAttributeGroup(existing, identity, customTags));
        }
        else
        {
            RemoveManagedAttributeGroup(groups, atrMarker);
        }

        TouchV4ModMetadata(meta);
        prepared = new AttributeGroupWrite(metaPath, meta);
        return null;
    }

    private static string? CommitAttributeGroups(AttributeGroupWrite prepared)
    {
        try
        {
            WriteJsonAtomic(prepared.Path, prepared.Document);
            return null;
        }
        catch (Exception error)
        {
            return $"attribute_group_write_failed: {error.Message}";
        }
    }

    internal static string? WriteAttributeGroupsForRegression(
        string modFolder,
        string resolvedGamePath,
        IReadOnlyList<string> tags,
        IReadOnlyDictionary<string, int>? masks,
        JsonArray? sourceManipulations = null,
        IReadOnlyList<JsonObject>? gameImcEntries = null)
        => WriteAttributeGroupsCore(
            modFolder, resolvedGamePath, tags, masks,
            identity => PlanImcGroupEntry(identity, sourceManipulations, gameImcEntries));

    private static string? NormalizeAttributeGroupInput(
        string resolvedGamePath,
        IReadOnlyList<string> tags,
        IReadOnlyDictionary<string, int>? masks,
        out AttributeModelIdentity? identity,
        out Dictionary<string, int> suffixMasks,
        out List<string> customTags)
    {
        identity = null;
        suffixMasks = new Dictionary<string, int>(StringComparer.Ordinal);
        customTags = [];
        if (tags is null || tags.Count == 0)
            return null;
        if (!IsSafeGamePath(resolvedGamePath))
            return "attribute_group_invalid: The resolved model path is invalid.";

        identity = ParseAttributeModelIdentity(resolvedGamePath);
        if (identity is null)
            return "attribute_group_invalid: The resolved model path is not a supported gear, accessory, hair, or face model path.";
        if (tags.Count > 512)
            return "attribute_group_invalid: Too many model attributes were supplied.";

        var (family, slotName) = VariantFamily(identity.AtrSlot);
        var otherFamily = new List<string>();
        foreach (var tag in tags.Distinct(StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(tag) || tag.Length > 128 ||
                !Regex.IsMatch(tag, "^[a-z0-9_]+$", RegexOptions.CultureInvariant))
                return "attribute_group_invalid: Model attribute names must contain only lowercase letters, numbers, and underscores.";

            if (TryGetStandardAttributeSuffix(tag, out var suffix))
            {
                // The game has no IMC file for hair or faces, and Penumbra
                // cannot load an IMC group for them.
                if (identity.ObjectType == "Character")
                    continue;
                // A model's IMC entry only toggles its own slot's tags, such as
                // atr_nv_ on a necklace; another slot's tags would never hide.
                if (!tag.StartsWith($"atr_{family}_", StringComparison.Ordinal))
                {
                    otherFamily.Add(tag);
                    continue;
                }
                // IMC columns are semantic suffix bits, not positions in the
                // model's attribute table. Canonicalize this here as well as
                // in Blender so older payloads cannot shift A/B/C to D/E/F.
                suffixMasks[suffix] = 1 << (suffix[0] - 'a');
                continue;
            }

            if (tag.StartsWith("atrx_", StringComparison.Ordinal))
            {
                if (tag.Length is < 5 or > 30)
                    return $"attribute_group_invalid: Custom attribute {tag} must be between 5 and 30 characters.";
                customTags.Add(tag);
                continue;
            }

            // The game's own attributes, including the hair variants and the face
            // toggles that Glamourer and character creation drive, are valid model
            // attributes but are deliberately not part of the generated Penumbra
            // groups. Add-ons before 2.0.0 still send atr_hv_.
            if (GameAttributeNames.Contains(tag) ||
                Regex.IsMatch(tag, "^atr_(?:hv_[a-h]|fv_[a-g])$", RegexOptions.CultureInvariant))
                continue;

            return $"attribute_group_invalid: Custom attribute {tag} must use the atrx_ prefix; it was not emitted as a Penumbra toggle.";
        }

        if (otherFamily.Count > 0)
        {
            otherFamily.Sort(StringComparer.Ordinal);
            return $"attribute_group_invalid: {(slotName[0] == 'e' ? "An" : "A")} {slotName} model only toggles atr_{family}_ tags, so {string.Join(", ", otherFamily)} " +
                   $"would never hide. Rename {(otherFamily.Count == 1 ? "it" : "them")} to " +
                   $"{string.Join(", ", otherFamily.Select(tag => $"atr_{family}_{tag[^1]}"))}.";
        }

        customTags = customTags.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToList();
        return null;
    }

    /// <summary> The variant tag family a gear or accessory slot's IMC entry toggles, and the slot's name for messages. </summary>
    private static (string Family, string SlotName) VariantFamily(string atrSlot) => atrSlot switch
    {
        "Head" => ("mv", "head"),
        "Body" => ("tv", "body"),
        "Hands" => ("gv", "glove"),
        "Legs" => ("dv", "leg"),
        "Feet" => ("sv", "shoe"),
        "Ears" => ("ev", "earring"),
        "Neck" => ("nv", "necklace"),
        "Wrists" => ("wv", "bracelet"),
        "RFinger" or "LFinger" => ("rv", "ring"),
        _ => ("", atrSlot),
    };

    private static bool TryGetStandardAttributeSuffix(string tag, out string suffix)
    {
        suffix = "";
        // IMC attribute masks have ten bits, _a to _j.
        var match = Regex.Match(
            tag,
            "^atr_(?:" + string.Join("|", AttributeGroupFamilies) + ")_(?<suffix>[a-j])$",
            RegexOptions.CultureInvariant);
        if (!match.Success)
            return false;
        suffix = match.Groups["suffix"].Value;
        return true;
    }

    private static AttributeModelIdentity? ParseAttributeModelIdentity(string gamePath)
    {
        var path = NormalizeGamePath(gamePath);
        Match match;
        string slot;
        string objectType;
        string equipSlot;
        string bodySlot;
        if (path.StartsWith("chara/equipment/", StringComparison.OrdinalIgnoreCase))
        {
            match = Regex.Match(path,
                @"^chara/equipment/e(?<id>\d{4})/model/c(?<race>\d{4})e\k<id>_(?<slot>met|top|glv|dwn|sho)\.mdl$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            objectType = "Equipment";
            bodySlot = "Unknown";
            equipSlot = slot = match.Success ? match.Groups["slot"].Value.ToLowerInvariant() : "";
        }
        else if (path.StartsWith("chara/accessory/", StringComparison.OrdinalIgnoreCase))
        {
            match = Regex.Match(path,
                @"^chara/accessory/a(?<id>\d{4})/model/c(?<race>\d{4})a\k<id>_(?<slot>ear|nek|wrs|rir|ril)\.mdl$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            objectType = "Accessory";
            bodySlot = "Unknown";
            equipSlot = slot = match.Success ? match.Groups["slot"].Value.ToLowerInvariant() : "";
        }
        else if (path.StartsWith("chara/human/", StringComparison.OrdinalIgnoreCase))
        {
            match = Regex.Match(path,
                @"^chara/human/c(?<race>\d{4})/obj/(?<kind>hair|face)/(?<kindId>[hf])(?<id>\d{4})/model/c\k<race>\k<kindId>\k<id>_(?<slot>hir|fac)\.mdl$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            objectType = "Character";
            equipSlot = "Nothing";
            slot = match.Success && match.Groups["kind"].Value.Equals("face", StringComparison.OrdinalIgnoreCase)
                ? "Face" : "Hair";
            bodySlot = slot;
        }
        else
        {
            return null;
        }

        if (!match.Success || !int.TryParse(match.Groups["race"].Value, out var raceCode) ||
            !int.TryParse(match.Groups["id"].Value, out var id) ||
            raceCode is < 101 or > 1801 || raceCode % 100 != 1)
            return null;

        var atrSlot = slot switch
        {
            "met" => "Head",
            "top" => "Body",
            "glv" => "Hands",
            "dwn" => "Legs",
            "sho" => "Feet",
            "ear" => "Ears",
            "nek" => "Neck",
            "wrs" => "Wrists",
            "rir" => "RFinger",
            "ril" => "LFinger",
            // The human branch already names its slot after Penumbra's HumanSlot.
            "Hair" or "Face" => slot,
            _ => "Unknown",
        };
        equipSlot = equipSlot switch
        {
            "met" => "Head",
            "top" => "Body",
            "glv" => "Hands",
            "dwn" => "Legs",
            "sho" => "Feet",
            "ear" => "Ears",
            "nek" => "Neck",
            "wrs" => "Wrists",
            "rir" => "RFinger",
            "ril" => "LFinger",
            _ => equipSlot,
        };
        return new AttributeModelIdentity(id, raceCode, atrSlot, objectType, equipSlot, bodySlot);
    }

    private static string AttributeGroupMarker(string kind, string path)
        => $"{AttributeGroupDescriptionPrefix}{kind}|{path}";

    private static string GeneratedAttributeGroupName(
        AttributeModelIdentity identity, string path, string kind)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        var baseName = $"XIV Instant Edit {stem} Parts ({kind})";
        return baseName.Length <= 120 ? baseName : baseName[..120].TrimEnd();
    }

    private static void UpsertAttributeGroup(
        JsonArray groups,
        string marker,
        string requestedName,
        Func<JsonObject?, JsonObject> builder)
    {
        var existingIndex = -1;
        JsonObject? existing = null;
        for (var index = 0; index < groups.Count; ++index)
        {
            if (groups[index] is not JsonObject group ||
                !string.Equals(JsonString(group["Description"]), marker, StringComparison.Ordinal))
                continue;
            existing = group;
            existingIndex = index;
            break;
        }

        var name = existing is null
            ? UniqueAttributeGroupName(groups, requestedName)
            : JsonString(existing["Name"]) ?? requestedName;
        var priority = existing is null
            ? NextAttributeGroupPriority(groups)
            : JsonInt(existing["Priority"]);
        var result = builder(existing);
        result["Id"] = ReadGuid(existing?["Id"])?.ToString("D") ?? Guid.NewGuid().ToString("D");
        result["Name"] = name;
        result["Description"] = marker;
        result["Priority"] = priority;
        if (existingIndex >= 0)
            groups[existingIndex] = result;
        else
            groups.Add(result);
    }

    private static void RemoveManagedAttributeGroup(JsonArray groups, string marker)
    {
        for (var index = groups.Count - 1; index >= 0; --index)
        {
            if (groups[index] is JsonObject group &&
                string.Equals(JsonString(group["Description"]), marker, StringComparison.Ordinal))
                groups.RemoveAt(index);
        }
    }

    private static int NextAttributeGroupPriority(JsonArray groups)
    {
        var highest = groups.OfType<JsonObject>().Select(group => JsonInt(group["Priority"]))
            .DefaultIfEmpty(0).Max();
        if (highest == int.MaxValue)
            throw new InvalidDataException("attribute_group_priority_exhausted");
        return highest + 1;
    }

    private static string UniqueAttributeGroupName(JsonArray groups, string requested)
    {
        var names = groups.OfType<JsonObject>()
            .Select(group => JsonString(group["Name"]))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(requested))
            return requested;
        for (var suffix = 2; suffix < 10000; ++suffix)
        {
            var suffixText = $" ({suffix})";
            var prefix = requested[..Math.Min(requested.Length, 120 - suffixText.Length)].TrimEnd();
            var candidate = prefix + suffixText;
            if (!names.Contains(candidate))
                return candidate;
        }
        throw new InvalidDataException("Could not allocate a unique Penumbra attribute group name.");
    }

    private static JsonObject BuildImcAttributeGroup(
        JsonObject? existing,
        AttributeModelIdentity identity,
        IReadOnlyDictionary<string, int> suffixMasks,
        JsonObject defaultImcEntry,
        bool onlyAttributes)
    {
        var result = existing?.DeepClone() as JsonObject ?? new JsonObject();
        result["Type"] = "Imc";
        result["AllVariants"] = true;
        // With OnlyAttributes, only the attribute mask changes and every variant keeps the game's materials,
        // decals and VFX. Without it, every variant gets the default entry: the one the mod gives them all.
        result["OnlyAttributes"] = onlyAttributes;
        result["Identifier"] = new JsonObject
        {
            ["PrimaryId"] = identity.Id,
            ["SecondaryId"] = 0,
            ["Variant"] = 1,
            ["ObjectType"] = identity.ObjectType,
            ["EquipSlot"] = identity.EquipSlot,
            ["BodySlot"] = identity.BodySlot,
        };
        var groupEntry = defaultImcEntry.DeepClone().AsObject();
        groupEntry["AttributeMask"] = 0;
        result["DefaultEntry"] = groupEntry;
        var options = suffixMasks
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => (JsonNode)new JsonObject
            {
                ["Id"] = Guid.NewGuid().ToString("D"),
                ["Name"] = pair.Key.ToUpperInvariant(),
                ["Description"] = $"Enable model parts tagged with suffix _{pair.Key}.",
                ["AttributeMask"] = pair.Value,
            }).ToArray();
        result["Options"] = new JsonArray(options);
        result["DefaultSettings"] = (1 << options.Length) - 1;
        return result;
    }

    private static JsonObject ImcEntry(
        int materialId,
        int decalId,
        int vfxId,
        int materialAnimationId,
        int attributeMask,
        int soundId)
        => new()
        {
            ["MaterialId"] = materialId,
            ["DecalId"] = decalId,
            ["VfxId"] = vfxId,
            ["MaterialAnimationId"] = materialAnimationId,
            ["AttributeMask"] = attributeMask,
            ["SoundId"] = soundId,
        };

    private static JsonObject BuildAtrAttributeGroup(
        JsonObject? existing,
        AttributeModelIdentity identity,
        IReadOnlyList<string> customTags)
    {
        var result = existing?.DeepClone() as JsonObject ?? new JsonObject();
        result["Type"] = "Multi";
        result["Options"] = new JsonArray(customTags.Select(tag => (JsonNode)new JsonObject
        {
            ["Id"] = Guid.NewGuid().ToString("D"),
            ["Name"] = tag,
            ["Description"] = $"Enable {tag}.",
            ["Manipulations"] = new JsonArray(BuildAtrManipulation(identity, tag, true)),
        }).ToArray());
        foreach (var property in new[] { "AllVariants", "OnlyAttributes", "Identifier", "DefaultEntry", "DefaultSettings" })
            result.Remove(property);
        return result;
    }

    private static JsonObject BuildAtrManipulation(
        AttributeModelIdentity identity,
        string tag,
        bool enabled)
        => new()
        {
            ["Type"] = "Atr",
            ["Manipulation"] = new JsonObject
            {
                ["Entry"] = enabled,
                ["Attribute"] = tag,
                ["Slot"] = identity.AtrSlot,
                ["Id"] = identity.Id,
                // Penumbra serializes Any Gender & Race as GenderRace.Unknown.
                ["GenderRaceCondition"] = 0,
            },
        };

    private static HashSet<string> ManagedAtrTags(JsonArray groups, string managedMarker)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var managedGroup = groups.OfType<JsonObject>().FirstOrDefault(group =>
            string.Equals(JsonString(group["Description"]), managedMarker, StringComparison.Ordinal));
        if (managedGroup is null)
            return result;
        foreach (var item in DescendantObjects(managedGroup))
            if (string.Equals(JsonString(item["Type"]), "Atr", StringComparison.OrdinalIgnoreCase) &&
                item["Manipulation"] is JsonObject manipulation &&
                JsonString(manipulation["Attribute"]) is { } attribute &&
                attribute.StartsWith("atrx_", StringComparison.Ordinal))
                result.Add(attribute);
        return result;
    }

    private static void UpdateManagedAtrDefaults(
        JsonObject meta,
        AttributeModelIdentity identity,
        IReadOnlySet<string> previousTags,
        IReadOnlyList<string> currentTags)
    {
        if (previousTags.Count == 0 && currentTags.Count == 0)
            return;
        var defaultData = meta["DefaultData"] as JsonObject;
        if (defaultData is null)
        {
            defaultData = new JsonObject
            {
                ["Files"] = new JsonObject(),
                ["FileSwaps"] = new JsonObject(),
                ["Manipulations"] = new JsonArray(),
            };
            meta["DefaultData"] = defaultData;
        }
        var manipulations = defaultData["Manipulations"] as JsonArray ?? new JsonArray();
        defaultData["Manipulations"] = manipulations;

        for (var index = manipulations.Count - 1; index >= 0; --index)
        {
            if (manipulations[index] is not JsonObject item ||
                !TryGetAtrManipulation(item, out var manipulation, out var attribute) ||
                !previousTags.Contains(attribute) ||
                !(AtrManipulationMatchesIdentity(manipulation, identity) ||
                  IsLegacyHumanAtrManipulation(manipulation, identity)) ||
                !IsFalse(manipulation["Entry"]))
                continue;
            manipulations.RemoveAt(index);
        }
        foreach (var tag in currentTags)
            if (!manipulations.OfType<JsonObject>().Any(item =>
                    TryGetAtrManipulation(item, out var manipulation, out var attribute) &&
                    string.Equals(attribute, tag, StringComparison.Ordinal) &&
                    AtrManipulationMatchesIdentity(manipulation, identity) &&
                    IsFalse(manipulation["Entry"])))
                manipulations.Add(BuildAtrManipulation(identity, tag, false));
    }

    internal static JsonArray ManipulationsWithAtrDefaults(
        JsonArray? sourceManipulations,
        string resolvedGamePath,
        IReadOnlyList<string>? tags)
    {
        var result = CloneManipulations(sourceManipulations);
        var identity = ParseAttributeModelIdentity(resolvedGamePath);
        if (identity is null || tags is null)
            return result;
        // Source mods exported by older versions carry this model's toggles on
        // the invalid Unknown slot; move them to the real one so they apply.
        foreach (var item in result.OfType<JsonObject>())
            if (TryGetAtrManipulation(item, out var manipulation, out var attribute) &&
                tags.Contains(attribute, StringComparer.Ordinal) &&
                IsLegacyHumanAtrManipulation(manipulation, identity))
                manipulation["Slot"] = identity.AtrSlot;
        foreach (var tag in tags.Where(tag => tag.StartsWith("atrx_", StringComparison.Ordinal))
                     .Distinct(StringComparer.Ordinal).OrderBy(tag => tag, StringComparer.Ordinal))
            if (!result.OfType<JsonObject>().Any(item =>
                    TryGetAtrManipulation(item, out var manipulation, out var attribute) &&
                    string.Equals(attribute, tag, StringComparison.Ordinal) &&
                    AtrManipulationMatchesIdentity(manipulation, identity) &&
                    IsFalse(manipulation["Entry"])))
                result.Add(BuildAtrManipulation(identity, tag, false));
        return result;
    }

    private static bool TryGetAtrManipulation(
        JsonObject item,
        out JsonObject manipulation,
        out string attribute)
    {
        manipulation = null!;
        attribute = "";
        if (!string.Equals(JsonString(item["Type"]), "Atr", StringComparison.OrdinalIgnoreCase) ||
            item["Manipulation"] is not JsonObject value ||
            JsonString(value["Attribute"]) is not { } name ||
            !name.StartsWith("atrx_", StringComparison.Ordinal))
            return false;
        manipulation = value;
        attribute = name;
        return true;
    }

    private static bool AtrManipulationMatchesIdentity(
        JsonObject manipulation,
        AttributeModelIdentity identity)
        => string.Equals(JsonString(manipulation["Slot"]), identity.AtrSlot, StringComparison.Ordinal) &&
           JsonIntFlexible(manipulation["Id"]) == identity.Id &&
           JsonIntFlexible(manipulation["GenderRaceCondition"]) == 0;

    // Before 2.0.0, hair and face exports wrote Slot "Unknown" with the model ID.
    // Penumbra drops that combination as invalid, so these entries never did anything.
    private static bool IsLegacyHumanAtrManipulation(
        JsonObject manipulation,
        AttributeModelIdentity identity)
        => identity.ObjectType == "Character" &&
           string.Equals(JsonString(manipulation["Slot"]), "Unknown", StringComparison.Ordinal) &&
           JsonIntFlexible(manipulation["Id"]) == identity.Id &&
           JsonIntFlexible(manipulation["GenderRaceCondition"]) == 0;

    private static bool IsFalse(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<bool>(out var result) && !result;

    private static bool HasUnmanagedAtrConflict(
        JsonObject meta,
        string managedMarker,
        AttributeModelIdentity identity,
        IReadOnlySet<string>? managedDefaultTags = null)
    {
        foreach (var group in (meta["Groups"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            if (string.Equals(JsonString(group["Description"]), managedMarker, StringComparison.Ordinal))
                continue;
            foreach (var item in DescendantObjects(group))
            {
                var attribute = item["Manipulation"] is JsonObject value
                    ? JsonString(value["Attribute"])
                    : null;
                if (!string.Equals(JsonString(item["Type"]), "Atr", StringComparison.OrdinalIgnoreCase) ||
                    item["Manipulation"] is not JsonObject manipulation ||
                    attribute is null || !attribute.StartsWith("atrx_", StringComparison.Ordinal))
                    continue;
                if (string.Equals(JsonString(manipulation["Slot"]), identity.AtrSlot, StringComparison.Ordinal) &&
                    JsonIntFlexible(manipulation["Id"]) == identity.Id)
                    return true;
            }
        }
        if (meta["DefaultData"]?["Manipulations"] is not JsonArray defaultManipulations)
            return false;
        foreach (var item in defaultManipulations.OfType<JsonObject>())
        {
            if (!TryGetAtrManipulation(item, out var manipulation, out var attribute) ||
                !string.Equals(JsonString(manipulation["Slot"]), identity.AtrSlot, StringComparison.Ordinal) ||
                JsonIntFlexible(manipulation["Id"]) != identity.Id)
                continue;
            if (managedDefaultTags?.Contains(attribute) == true &&
                AtrManipulationMatchesIdentity(manipulation, identity) &&
                IsFalse(manipulation["Entry"]))
                continue;
            return true;
        }
        return false;
    }

    private static IEnumerable<JsonObject> DescendantObjects(JsonNode? node)
    {
        if (node is JsonObject objectNode)
        {
            yield return objectNode;
            foreach (var property in objectNode)
                foreach (var child in DescendantObjects(property.Value))
                    yield return child;
        }
        else if (node is JsonArray arrayNode)
        {
            foreach (var item in arrayNode)
                foreach (var child in DescendantObjects(item))
                    yield return child;
        }
    }

    private static int JsonIntFlexible(JsonNode? node)
    {
        if (node is not JsonValue value)
            return 0;
        if (value.TryGetValue<int>(out var number))
            return number;
        return value.TryGetValue<string>(out var text) && int.TryParse(text, out number) ? number : 0;
    }

    private static string AttributeGroupErrorCode(string error)
        => error.StartsWith("attribute_group_conflict:", StringComparison.Ordinal)
            ? "attribute_group_conflict"
            : error.StartsWith("attribute_group_write_failed:", StringComparison.Ordinal)
                ? "attribute_group_write_failed"
                : "invalid_attribute_groups";

    private static string AttributeGroupErrorMessage(string error)
    {
        var separator = error.IndexOf(": ", StringComparison.Ordinal);
        return separator >= 0 ? error[(separator + 2)..] : error;
    }

}
