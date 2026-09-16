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
    private static string UniqueMashupGroupName(string modFolder, string requested)
    {
        var names = ReadAllVariantGroups(modFolder)
            .Select(group => JsonString(group["Name"]))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(requested))
            return requested;
        for (var suffix = 2; suffix < 10000; ++suffix)
        {
            var suffixText = $" ({suffix})";
            var prefix = requested[..Math.Min(requested.Length, 120 - suffixText.Length)].TrimEnd();
            var value = prefix + suffixText;
            if (!names.Contains(value))
                return value;
        }
        throw new InvalidDataException("Could not allocate a unique Penumbra group name.");
    }

    internal static string? WriteMashupGroup(
        string modFolder,
        string name,
        IReadOnlyDictionary<string, string> mappings,
        string? description = null,
        JsonArray? manipulations = null,
        IReadOnlyDictionary<string, string>? fileSwaps = null)
    {
        try
        {
            fileSwaps ??= new Dictionary<string, string>();
            ValidateMashupMappingsAndSwaps(mappings, fileSwaps);
            var metaPath = Path.Combine(modFolder, "meta.json");
            var meta = LoadV4ModMetadata(modFolder);
            var allGroups = ReadAllVariantGroups(modFolder);
            var priority = allGroups.Select(group => JsonInt(group["Priority"])).DefaultIfEmpty(0).Max();
            if (priority == int.MaxValue)
                return "penumbra_group_priority_exhausted";
            var group = new JsonObject
            {
                ["Type"] = "Single",
                ["Id"] = Guid.NewGuid(),
                ["Name"] = name,
                ["Description"] = description ?? $"Managed by XIV Instant Edit mashup: {name}",
                ["Priority"] = priority + 1,
                ["DefaultSettings"] = 1,
                ["Options"] = new JsonArray
                {
                    new JsonObject { ["Id"] = Guid.NewGuid(), ["Name"] = "None" },
                    new JsonObject
                    {
                        ["Id"] = Guid.NewGuid(),
                        ["Name"] = name,
                        ["Files"] = new JsonObject(mappings.Select(pair =>
                            KeyValuePair.Create<string, JsonNode?>(pair.Key, pair.Value))),
                        ["FileSwaps"] = new JsonObject(fileSwaps.Select(pair =>
                            KeyValuePair.Create<string, JsonNode?>(pair.Key, pair.Value))),
                        ["Manipulations"] = CloneManipulations(manipulations),
                    },
                },
            };

            var groups = meta["Groups"] as JsonArray ?? new JsonArray();
            meta["Groups"] = groups;
            groups.Add(group);
            TouchV4ModMetadata(meta);
            WriteJsonAtomic(metaPath, meta);
            return null;
        }
        catch (Exception e)
        {
            return $"penumbra_group_write_failed: {e.Message}";
        }
    }

    private static void WriteBytesAtomic(string root, string relativePath, byte[] bytes)
    {
        if (!IsSafeRelativeResourcePath(relativePath))
            throw new InvalidDataException("A generated mashup path is unsafe.");
        var target = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var parent = Path.GetDirectoryName(target) ?? throw new InvalidDataException("Mashup file has no parent.");
        if (!IsPathWithin(target, root) || HasReparsePointInPath(root, parent) ||
            (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0))
            throw new InvalidDataException("A generated mashup destination is unsafe.");
        Directory.CreateDirectory(parent);
        var temporary = Path.Combine(parent, $".instant-edit-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, target, false);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static void TryDeleteMashupNamespace(string root, string folder)
    {
        try
        {
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullFolder = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Directory.Exists(fullFolder) || !IsPathWithin(fullFolder, fullRoot) ||
                string.Equals(fullFolder, fullRoot, StringComparison.OrdinalIgnoreCase) ||
                (File.GetAttributes(fullFolder) & FileAttributes.ReparsePoint) != 0)
                return;
            Directory.Delete(fullFolder, true);
        }
        catch
        {
            // Cleanup is best-effort; the error returned to Blender remains authoritative.
        }
    }

    internal static void ValidateStagedMashupMod(
        string staging,
        string modName,
        IReadOnlyDictionary<string, string> mappings,
        JsonArray? expectedManipulations = null,
        IReadOnlyDictionary<string, string>? expectedFileSwaps = null)
    {
        expectedFileSwaps ??= new Dictionary<string, string>();
        ValidateMashupMappingsAndSwaps(mappings, expectedFileSwaps);
        if ((File.GetAttributes(staging) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The mashup staging directory is unsafe.");
        var meta = LoadV4ModMetadata(staging);
        if (!string.Equals(JsonString(meta["Name"]), modName, StringComparison.Ordinal) ||
            ReadGuid(meta["Identifier"]) is null ||
            !DateTimeOffset.TryParse(JsonString(meta["LastWrite"]), out _))
            throw new InvalidDataException("The mashup mod metadata is invalid.");
        if (meta["DefaultData"] is not JsonObject defaultData ||
            defaultData["Files"] is not JsonObject files || files.Count != mappings.Count)
            throw new InvalidDataException("The mashup default mappings are incomplete.");
        if (defaultData["FileSwaps"] is not JsonObject fileSwaps || fileSwaps.Count != expectedFileSwaps.Count)
            throw new InvalidDataException("The mashup default FileSwaps are incomplete.");
        var actualManipulations = defaultData["Manipulations"] as JsonArray;
        var expected = CloneManipulations(expectedManipulations);
        if ((expectedManipulations is not null && actualManipulations is null) ||
            !JsonNode.DeepEquals(actualManipulations ?? new JsonArray(), expected))
            throw new InvalidDataException("The mashup Meta Manipulations are incomplete.");
        foreach (var mapping in mappings)
        {
            if (!string.Equals(JsonString(files[mapping.Key]), mapping.Value, StringComparison.Ordinal) ||
                !IsSafeRelativeResourcePath(mapping.Value))
                throw new InvalidDataException("A mashup default mapping is invalid.");
            var physical = Path.GetFullPath(Path.Combine(
                staging, mapping.Value.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsPathWithin(physical, staging) || !File.Exists(physical) ||
                HasReparsePointInPath(staging, physical))
                throw new InvalidDataException("A staged mashup resource is missing or unsafe.");
        }
        foreach (var swap in expectedFileSwaps)
            if (!string.Equals(JsonString(fileSwaps[swap.Key]), swap.Value, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A mashup default FileSwap is invalid.");
    }

    internal static JsonObject CreateMashupDefaultData(
        IReadOnlyDictionary<string, string> mappings,
        JsonArray? manipulations,
        IReadOnlyDictionary<string, string>? fileSwaps = null)
    {
        fileSwaps ??= new Dictionary<string, string>();
        ValidateMashupMappingsAndSwaps(mappings, fileSwaps);
        return new JsonObject
        {
            ["Files"] = new JsonObject(mappings.Select(pair =>
                KeyValuePair.Create<string, JsonNode?>(pair.Key, pair.Value))),
            ["FileSwaps"] = new JsonObject(fileSwaps.Select(pair =>
                KeyValuePair.Create<string, JsonNode?>(pair.Key, pair.Value))),
            ["Manipulations"] = CloneManipulations(manipulations),
        };
    }

    private static void ValidateMashupMappingsAndSwaps(
        IReadOnlyDictionary<string, string> mappings,
        IReadOnlyDictionary<string, string> fileSwaps)
    {
        foreach (var mapping in mappings)
            if (!IsSafeGameResourcePath(NormalizeGamePath(mapping.Key), ".mdl", ".mtrl", ".tex") ||
                !IsSafeRelativeResourcePath(mapping.Value))
                throw new InvalidDataException("A generated mashup file mapping is unsafe.");
        foreach (var swap in fileSwaps)
            if (!IsSafeGameResourcePath(NormalizeGamePath(swap.Key), ".mtrl") ||
                !IsSafeGameResourcePath(NormalizeGamePath(swap.Value), ".mtrl") ||
                mappings.ContainsKey(swap.Key))
                throw new InvalidDataException("A generated mashup FileSwap is unsafe or conflicts with a file mapping.");
    }

    private static JsonArray CloneManipulations(JsonArray? manipulations)
        => manipulations?.DeepClone() as JsonArray ?? new JsonArray();

    internal static string StageGameModelMod(
        string staging,
        string modName,
        string consumerGamePath,
        byte[] modelBytes)
    {
        if (!Directory.Exists(staging) || !IsSafeNewModName(modName) ||
            !IsSafeGamePath(consumerGamePath) ||
            modelBytes.Length == 0)
            throw new InvalidDataException("The vanilla model staging request is invalid.");

        var gamePath = NormalizeGamePath(consumerGamePath);
        var relativeModel = "Files/" + gamePath;
        var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [gamePath] = relativeModel,
        };
        WriteBytesAtomic(staging, relativeModel, modelBytes);
        WriteJsonAtomic(Path.Combine(staging, "meta.json"), CreateV4ModMetadata(
            modName,
            "XIV Instant Edit",
            $"Vanilla model edit for {gamePath}",
            "",
            new JsonObject
            {
                ["Files"] = new JsonObject { [gamePath] = relativeModel },
                ["FileSwaps"] = new JsonObject(),
                ["Manipulations"] = new JsonArray(),
            }));
        ValidateStagedMashupMod(staging, modName, mappings);
        return relativeModel;
    }

    internal static MashupPlanResult BuildMashupPlan(
        InstantEditImportContext activeContext,
        IReadOnlyList<MashupContributor> contributors,
        bool bundleExternalDependencies = false)
    {
        if (contributors.Count is < 1 or > 16 ||
            contributors.All(item => !string.Equals(
                item.Context.ContextId, activeContext.ContextId, StringComparison.Ordinal)))
            return MashupPlanFailure("invalid_mashup", "The active Context and at least one contributor are required.");
        if (contributors.Any(item => item.Context.ResourceManifest?.Version != ResourceDependencyManifest.CurrentVersion))
            return MashupPlanFailure("mashup_reimport_required", "Re-import every contributing Context before creating a mashup.");

        var ordered = contributors
            .OrderBy(item => string.Equals(item.Context.ContextId, activeContext.ContextId, StringComparison.Ordinal) ? 0 : 1)
            .ToArray();
        var resolved = new List<(MashupContributor Contributor, string ModelMaterial, MaterialDependency Dependency)>();
        foreach (var contributor in ordered)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var requested in contributor.Materials)
            {
                var normalized = NormalizeModelMaterial(requested);
                if (!seen.Add(normalized))
                    continue;
                var dependency = contributor.Context.ResourceManifest!.Materials.FirstOrDefault(material =>
                    string.Equals(NormalizeModelMaterial(material.ModelMaterial), normalized,
                        StringComparison.OrdinalIgnoreCase));
                if (dependency is null)
                    return MashupPlanFailure("mashup_reimport_required",
                        $"Material {normalized} is absent from the captured dependency manifest for {contributor.Context.SourceModName ?? contributor.Context.ContextId}; re-import that Context.");
                if (!IsValidMashupLocator(dependency.Resource, ".mtrl") ||
                    !string.Equals(NormalizeGamePath(dependency.GamePath),
                        NormalizeGamePath(dependency.Resource.GamePath), StringComparison.OrdinalIgnoreCase))
                    return MashupPlanFailure("mashup_reimport_required",
                        $"Material {normalized} has invalid captured source metadata; re-import that Context.");
                resolved.Add((contributor, normalized, dependency));
            }
        }

        var activeMaterials = resolved.Where(item => string.Equals(
            item.Contributor.Context.ContextId, activeContext.ContextId, StringComparison.Ordinal)).ToArray();
        if (activeMaterials.Length == 0)
            return MashupPlanFailure("mashup_material_missing", "The active Context contributes no captured material.");
        var assignments = new List<MashupMaterialAssignment>(resolved.Count);
        var usedAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in activeMaterials)
        {
            var gamePath = NormalizeGamePath(item.Dependency.GamePath);
            if (!IsSafeGameResourcePath(gamePath, ".mtrl"))
                return MashupPlanFailure("mashup_material_path_unsafe",
                    $"Active material path is unsafe: {item.Dependency.GamePath}.");
            var alias = item.ModelMaterial;
            if (!IsSafeModelMaterialAlias(alias))
                return MashupPlanFailure("mashup_material_alias_unsafe",
                    $"Active material alias is unsafe: {alias}.");
            var parsed = ParseCanonicalMaterialFamily(Path.GetFileName(gamePath));
            var slot = parsed?.Slot.ToString();
            if (!usedAliases.Add(alias))
                return MashupPlanFailure("mashup_material_alias_conflict",
                    $"Active materials conflict at alias {alias}.");
            if (!usedPaths.Add(gamePath))
                return MashupPlanFailure("mashup_material_path_conflict",
                    $"Active materials conflict at {gamePath}.");
            assignments.Add(new MashupMaterialAssignment(
                item.Contributor.Context.ContextId, item.ModelMaterial, alias, gamePath, slot));
        }

        var incomingMaterials = resolved.Where(item => !string.Equals(
            item.Contributor.Context.ContextId, activeContext.ContextId, StringComparison.Ordinal)).ToArray();
        if (incomingMaterials.Length > 0)
        {
            var target = SelectIncomingMashupMaterialDirectory(
                activeContext.GamePath, activeMaterials.Select(item => item.Dependency));
            if (target.Ambiguous)
                return MashupPlanFailure("mashup_target_material_directory_ambiguous",
                    "Multiple equally likely model-local material directories are active.");
            if (target.Directory is null || !IsSafeGameResourcePath(
                    $"{target.Directory}/placeholder.mtrl", ".mtrl"))
                return MashupPlanFailure("mashup_target_material_directory_invalid",
                    "A safe model-local material directory could not be derived from the active model.");
            var targetDirectory = target.Directory;
            var modelStem = CanonicalTargetModelStem(activeContext.GamePath, targetDirectory);
            if (!Regex.IsMatch(modelStem, @"^[a-z]\d{4}[a-z]\d{4}.*$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return MashupPlanFailure("mashup_target_material_family_ambiguous",
                    "A canonical incoming material family could not be derived from the active model.");
            var prefix = $"mt_{modelStem}";
            var suffix = CanonicalTargetRaceSuffix(activeContext.GamePath, targetDirectory);
            var usedSlots = new HashSet<char> { 'a' };
            foreach (var assignment in assignments.Where(item => string.Equals(
                         GamePathDirectory(item.GamePath), targetDirectory, StringComparison.OrdinalIgnoreCase)))
            {
                var parsed = ParseCanonicalMaterialFamily(Path.GetFileName(assignment.GamePath));
                if (parsed is not null &&
                    string.Equals(parsed.Value.Prefix, prefix, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(parsed.Value.Suffix, suffix, StringComparison.OrdinalIgnoreCase))
                    usedSlots.Add(parsed.Value.Slot);
            }

            foreach (var item in incomingMaterials)
            {
                char? allocated = null;
                string? gamePath = null;
                string? alias = null;
                for (var candidate = 'b'; candidate <= 'z'; candidate++)
                {
                    if (usedSlots.Contains(candidate))
                        continue;
                    var fileName = $"{prefix}_{candidate}{suffix}.mtrl";
                    var candidateAlias = "/" + fileName;
                    var candidatePath = $"{targetDirectory}/{fileName}";
                    if (usedAliases.Contains(candidateAlias) || usedPaths.Contains(candidatePath))
                        continue;
                    allocated = candidate;
                    alias = candidateAlias;
                    gamePath = candidatePath;
                    break;
                }
                if (allocated is null || gamePath is null || alias is null)
                    return MashupPlanFailure("mashup_material_slots_exhausted",
                        "No canonical material slots remain between b and z.");
                usedSlots.Add(allocated.Value);
                usedAliases.Add(alias);
                usedPaths.Add(gamePath);
                assignments.Add(new MashupMaterialAssignment(
                    item.Contributor.Context.ContextId,
                    item.ModelMaterial,
                    alias,
                    gamePath,
                    allocated.Value.ToString()));
            }
        }

        var fingerprintSource = $"bundleExternalDependencies={bundleExternalDependencies}\n" +
            string.Join("\n", assignments.Select(item =>
                $"{item.ContextId}\0{item.ModelMaterial.ToLowerInvariant()}\0{item.Alias.ToLowerInvariant()}\0{item.GamePath.ToLowerInvariant()}"));
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintSource)))
            .ToLowerInvariant();
        return new MashupPlanResult(true, "mashup_plan_ready", "Mashup material plan is ready.", assignments, fingerprint);
    }

    private static MashupPlanResult MashupPlanFailure(string code, string message)
        => new(false, code, message, Array.Empty<MashupMaterialAssignment>());

    internal static bool IsValidMashupContributorCount(string destination, int count)
        => count is >= 1 and <= 16 &&
           (!string.Equals(destination, "active_mod", StringComparison.Ordinal) || count >= 2);

    private static (string Prefix, string Suffix, char Slot)? ParseCanonicalMaterialFamily(string fileName)
    {
        var match = Regex.Match(fileName,
            @"^(?<prefix>mt_.+)_(?<slot>[a-z])(?<suffix>_c\d{4})?\.mtrl$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success
            ? (match.Groups["prefix"].Value, match.Groups["suffix"].Value,
                char.ToLowerInvariant(match.Groups["slot"].Value[0]))
            : null;
    }

    private static string GamePathDirectory(string value)
    {
        var normalized = NormalizeGamePath(value);
        var separator = normalized.LastIndexOf('/');
        return separator > 0 ? normalized[..separator] : string.Empty;
    }

    private static (string? Directory, bool Ambiguous) SelectIncomingMashupMaterialDirectory(
        string modelGamePath,
        IEnumerable<MaterialDependency> activeMaterials)
    {
        var modelAssetDirectory = ModelAssetDirectory(modelGamePath);
        if (modelAssetDirectory is null)
            return (null, false);
        var modelFamily = AssetFamilyKey(modelAssetDirectory);
        var candidates = activeMaterials
            .Select(material => GamePathDirectory(material.GamePath))
            .Where(directory => IsSafeGameResourcePath($"{directory}/placeholder.mtrl", ".mtrl"))
            .Where(directory => string.Equals(
                AssetFamilyKey(MaterialAssetDirectory(directory)), modelFamily,
                StringComparison.OrdinalIgnoreCase))
            .GroupBy(directory => directory, StringComparer.OrdinalIgnoreCase)
            .Select(group => (Directory: group.Key, Count: group.Count()))
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Directory, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (candidates.Length > 1 && candidates[0].Count == candidates[1].Count)
            return (null, true);
        if (candidates.Length > 0)
            return (candidates[0].Directory, false);

        var normalizedModelPath = NormalizeGamePath(modelGamePath);
        var fallback = normalizedModelPath.Contains("/face/f", StringComparison.OrdinalIgnoreCase) ||
                       normalizedModelPath.Contains("/zear/z", StringComparison.OrdinalIgnoreCase)
            ? $"{modelAssetDirectory}/material"
            : $"{modelAssetDirectory}/material/v0001";
        return IsSafeGameResourcePath($"{fallback}/placeholder.mtrl", ".mtrl")
            ? (fallback, false)
            : (null, false);
    }

    private static string? ModelAssetDirectory(string modelGamePath)
    {
        var normalized = NormalizeGamePath(modelGamePath);
        var marker = normalized.LastIndexOf("/model/", StringComparison.OrdinalIgnoreCase);
        return marker > 0 ? normalized[..marker] : null;
    }

    private static string? MaterialAssetDirectory(string materialDirectory)
    {
        var normalized = NormalizeGamePath(materialDirectory);
        var marker = normalized.LastIndexOf("/material", StringComparison.OrdinalIgnoreCase);
        if (marker <= 0)
            return null;
        var suffix = normalized[(marker + "/material".Length)..];
        if (suffix.Length > 0 && !Regex.IsMatch(suffix, @"^/v\d{4}$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return null;
        return normalized[..marker];
    }

    private static string? AssetFamilyKey(string? assetDirectory)
    {
        if (string.IsNullOrWhiteSpace(assetDirectory))
            return null;
        return string.Join("/", NormalizeGamePath(assetDirectory)
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => !Regex.IsMatch(segment, @"^c\d{4}$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)));
    }

    private static bool IsSafeModelMaterialAlias(string alias)
        => alias.Length is > 1 and <= 260 &&
           alias[0] == '/' &&
           alias.IndexOf('/', 1) < 0 &&
           !alias.Contains('\\') &&
           !alias.Contains('\0') &&
           alias.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase) &&
           alias.AsSpan(1) is not "." and not "..";

    private static string CanonicalTargetModelStem(string modelGamePath, string targetMaterialDirectory)
    {
        var stem = Path.GetFileNameWithoutExtension(NormalizeGamePath(modelGamePath));
        var componentMatches = Regex.Matches(
            NormalizeGamePath(targetMaterialDirectory),
            @"(?:^|/)(?<component>[a-uw-z]\d{4})(?=/|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        foreach (Match match in componentMatches)
        {
            var component = match.Groups["component"].Value;
            stem = Regex.Replace(
                stem,
                $@"{Regex.Escape(component[..1])}\d{{4}}",
                component,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100));
        }
        return stem;
    }

    private static string CanonicalTargetRaceSuffix(string modelGamePath, string targetMaterialDirectory)
    {
        var modelStem = Path.GetFileNameWithoutExtension(NormalizeGamePath(modelGamePath));
        var modelRace = Regex.Match(modelStem, @"^c(?<id>\d{4})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var targetRace = Regex.Match(NormalizeGamePath(targetMaterialDirectory), @"(?:^|/)c(?<id>\d{4})(?=/|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return modelRace.Success && targetRace.Success && !string.Equals(
            modelRace.Groups["id"].Value, targetRace.Groups["id"].Value, StringComparison.OrdinalIgnoreCase)
            ? $"_c{modelRace.Groups["id"].Value}"
            : string.Empty;
    }

    internal static string AllocateMashupTexturePath(
        string activeModelGamePath,
        char slot,
        string usage,
        int textureIndex,
        IReadOnlyDictionary<string, string> mappings)
    {
        var modelPath = NormalizeGamePath(activeModelGamePath);
        var marker = modelPath.LastIndexOf("/model/", StringComparison.OrdinalIgnoreCase);
        if (marker <= 0)
            throw new InvalidDataException("The active model has no canonical texture directory.");
        var directory = modelPath[..marker] + "/texture";
        var stem = Path.GetFileNameWithoutExtension(modelPath);
        var role = usage switch
        {
            "normal" => "n",
            "diffuse" => "d",
            "mask" or "specular" => "s",
            "index" => "id",
            "occlusion" => "o",
            "flow" => "f",
            "decal" => "decal",
            _ => $"t{textureIndex + 1:D2}",
        };
        var baseName = $"{stem}_{slot}_{role}";
        for (var suffix = 1; suffix < 10_000; suffix++)
        {
            var discriminator = suffix == 1 ? string.Empty : $"_{suffix}";
            var candidate = $"{directory}/{baseName}{discriminator}.tex";
            var dx11Candidate = Dx11TexturePath(candidate, 0x8000);
            if (!mappings.ContainsKey(candidate) && !mappings.ContainsKey(dx11Candidate))
                return candidate;
        }
        throw new InvalidDataException("No canonical texture collision name is available.");
    }

    internal static string RetargetMashupTexturePath(
        string activeModelGamePath,
        string sourceModelGamePath,
        string storedTexturePath)
    {
        var targetModelPath = NormalizeGamePath(activeModelGamePath);
        var sourceModelPath = NormalizeGamePath(sourceModelGamePath);
        var texturePath = NormalizeGamePath(storedTexturePath);
        var modelMarker = targetModelPath.LastIndexOf("/model/", StringComparison.OrdinalIgnoreCase);
        if (modelMarker <= 0)
            throw new InvalidDataException("The active model has no canonical texture directory.");
        if (!IsSafeGameResourcePath(texturePath, ".tex"))
            throw new InvalidDataException("The captured texture path is unsafe.");

        var separator = texturePath.LastIndexOf('/');
        var fileName = separator >= 0 ? texturePath[(separator + 1)..] : texturePath;
        var sourceIdentities = ModelPathIdentities(sourceModelPath);
        var targetIdentities = ModelPathIdentities(targetModelPath);
        var replacements = MatchModelIdentities(sourceIdentities, targetIdentities);

        // Retarget any same-kind identity present in the actual texture name as
        // well. This covers packs that borrow a texture from another item while
        // still keeping the dependency inside the active model's namespace.
        foreach (var targetIdentity in targetIdentities)
            replacements.TryAdd(targetIdentity.Kind, targetIdentity.Value);

        foreach (var source in sourceIdentities)
        {
            if (!replacements.TryGetValue(source.Kind, out var replacement) ||
                string.Equals(source.Value, replacement, StringComparison.OrdinalIgnoreCase))
                continue;
            fileName = ReplaceBoundedModelIdentity(fileName, source.Value, replacement);
        }

        foreach (Match match in Regex.Matches(
                     fileName,
                     @"(?<![a-z])(?<identity>[a-z]\d{4})(?!\d)",
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            var identity = match.Groups["identity"].Value;
            if (replacements.TryGetValue(char.ToLowerInvariant(identity[0]), out var replacement))
                fileName = ReplaceBoundedModelIdentity(fileName, identity, replacement);
        }

        var target = $"{targetModelPath[..modelMarker]}/texture/{fileName}";
        if (!IsSafeGameResourcePath(target, ".tex"))
            throw new InvalidDataException("The retargeted texture path is unsafe.");
        return target;
    }

    internal static string PlanMashupTexturePath(
        string activeModelGamePath,
        string sourceModelGamePath,
        string storedTexturePath,
        string? materialSlot,
        string usage,
        int textureIndex,
        IReadOnlyList<(ushort Flags, string Hash)> textures,
        IReadOnlyDictionary<string, string> textureHashByGamePath,
        IReadOnlyDictionary<string, string> mappings)
    {
        if (textures.Count == 0)
            throw new InvalidDataException("The captured texture group is empty.");

        var target = RetargetMashupTexturePath(activeModelGamePath, sourceModelGamePath, storedTexturePath);
        if (MashupTexturePathConflicts(target, textures, textureHashByGamePath))
        {
            var slot = !string.IsNullOrWhiteSpace(materialSlot) &&
                       materialSlot!.Length == 1 && char.IsAsciiLetterLower(materialSlot[0])
                ? materialSlot[0]
                : 'a';
            target = AllocateMashupTexturePath(
                activeModelGamePath, slot, usage, textureIndex, mappings);
        }

        if (MashupTexturePathConflicts(target, textures, textureHashByGamePath))
            throw new InvalidDataException("Generated texture path still conflicts.");
        return target;
    }

    internal static bool MashupTexturePathConflicts(
        string storedTexturePath,
        IReadOnlyList<(ushort Flags, string Hash)> textures,
        IReadOnlyDictionary<string, string> textureHashByGamePath)
    {
        var proposed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var texture in textures)
        {
            var effective = Dx11TexturePath(storedTexturePath, texture.Flags);
            if ((textureHashByGamePath.TryGetValue(effective, out var existingHash) &&
                 !string.Equals(existingHash, texture.Hash, StringComparison.OrdinalIgnoreCase)) ||
                (proposed.TryGetValue(effective, out var proposedHash) &&
                 !string.Equals(proposedHash, texture.Hash, StringComparison.OrdinalIgnoreCase)))
                return true;
            proposed[effective] = texture.Hash;
        }
        return false;
    }

    private static IReadOnlyList<(char Kind, string Value)> ModelPathIdentities(string modelGamePath)
    {
        var marker = modelGamePath.LastIndexOf("/model/", StringComparison.OrdinalIgnoreCase);
        if (marker <= 0)
            return [];
        return Regex.Matches(
                modelGamePath,
                @"(?<![a-z])(?<identity>[a-z]\d{4})(?!\d)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Select(match => match.Groups["identity"].Value.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(identity => (identity[0], identity))
            .ToArray();
    }

    private static Dictionary<char, string> MatchModelIdentities(
        IReadOnlyList<(char Kind, string Value)> source,
        IReadOnlyList<(char Kind, string Value)> target)
    {
        var result = new Dictionary<char, string>();
        foreach (var sourceIdentity in source)
        {
            var sameKind = target.FirstOrDefault(item => item.Kind == sourceIdentity.Kind);
            if (sameKind.Value is not null)
                result[sourceIdentity.Kind] = sameKind.Value;
        }

        var unmatchedSource = source.Where(item => !result.ContainsKey(item.Kind)).ToArray();
        var usedTargetKinds = result.Values
            .Select(value => char.ToLowerInvariant(value[0]))
            .ToHashSet();
        var unmatchedTarget = target.Where(item => !usedTargetKinds.Contains(item.Kind)).ToArray();
        var count = Math.Min(unmatchedSource.Length, unmatchedTarget.Length);
        for (var index = 1; index <= count; ++index)
            result[unmatchedSource[^index].Kind] = unmatchedTarget[^index].Value;
        return result;
    }

    private static string ReplaceBoundedModelIdentity(string value, string source, string target)
        => Regex.Replace(
            value,
            $@"(?<![a-z]){Regex.Escape(source)}(?!\d)",
            target,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));

    private static string NormalizeModelMaterial(string value)
    {
        var normalized = Regex.Replace((value ?? string.Empty).Trim(), @"\.\d{3}$", string.Empty);
        if (!normalized.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase))
            normalized += ".mtrl";
        if (!normalized.StartsWith('/'))
            normalized = "/" + normalized;
        return normalized;
    }
}
