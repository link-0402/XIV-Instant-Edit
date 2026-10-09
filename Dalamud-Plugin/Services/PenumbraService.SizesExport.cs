using System.Text.Json.Nodes;
using InstantEdit.Models;

namespace InstantEdit.Services;

public sealed partial class PenumbraService
{
    /// <summary>A size's exported model: its option name and the staged file.</summary>
    public sealed record SizeExportFile(string Name, string ExportedFile);

    /// <summary>
    /// Where a size export writes: the group update, the group's name, each size's option file, and the
    /// files of those that the group already maps (<paramref name="Replaced"/>), which the export writes over.
    /// </summary>
    internal sealed record SizeGroupPlan(VariantGroupWrite Group, string GroupName, IReadOnlyList<GroupOptionFile> Files,
        IReadOnlySet<string> Replaced);

    /// <summary>A size's option name: as a group name, and with something left to name its file after.</summary>
    internal static bool IsSafeSizeName(string? value)
        => IsSafeVariantGroupName(value) && value == value!.Trim() && SizeFileStem(value).Length > 0;

    /// <summary>A size's part of its model's file name: its name, characters a file name can't have replaced.</summary>
    internal static string SizeFileStem(string sizeName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(sizeName.Select(c => invalid.Contains(c) || char.IsControl(c) ? '-' : c).ToArray());
        return cleaned.Trim().TrimEnd('.').Trim();
    }

    /// <summary>
    /// Plan a size export into a Single group, one option per size named after it. A new group gets no
    /// None option and has the option named <paramref name="defaultSize"/> on by default; an existing one
    /// keeps its default. A size the group already has keeps the file it maps the model to, so exporting
    /// again replaces it, unless the mod maps that file elsewhere too; the others get a new file next to
    /// the imported model, named after the model and the size, that the mod doesn't use or hold.
    /// </summary>
    internal static string? PlanSizeGroup(
        string modFolder,
        string sourceGamePath,
        string modelFile,
        IReadOnlyList<string> sizeNames,
        string groupName,
        string? targetId,
        string defaultSize,
        JsonObject? sourceOption,
        out SizeGroupPlan? plan)
    {
        plan = null;
        try
        {
            if (!IsSafeGamePath(sourceGamePath) || sizeNames.Count == 0 || sizeNames.Any(name => !IsSafeSizeName(name)) ||
                sizeNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != sizeNames.Count ||
                !sizeNames.Contains(defaultSize, StringComparer.OrdinalIgnoreCase))
                return "invalid_penumbra_variant";
            var meta = LoadV4ModMetadata(modFolder);
            var lookupError = FindOptionGroup(meta["Groups"] as JsonArray ?? [], sourceGamePath, groupName, targetId,
                out var existingGroup, out var existingIndex, out _);
            if (lookupError is not null)
                return lookupError;
            var root = Path.GetFullPath(modFolder);
            var directory = Path.GetDirectoryName(Path.GetFullPath(modelFile));
            if (directory is null || !IsPathWithin(directory, root))
                return "The imported model is outside its mod.";

            var files = new string?[sizeNames.Count];
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // A size keeps its option's file only when no other option or group maps that file too.
            if (existingGroup?["Options"] is JsonArray options)
                for (var index = 0; index < sizeNames.Count; index++)
                    if (FindOption(options, sizeNames[index])?["Files"] is JsonObject mappings &&
                        TryNormalizeRelativeModPath(
                            JsonString(mappings.FirstOrDefault(pair => SameGamePath(pair.Key, sourceGamePath)).Value),
                            out var mapped) &&
                        !IsMappedOutsideOption(meta, root, Path.Combine(root, mapped), sizeNames[index]) &&
                        taken.Add(mapped))
                        files[index] = mapped;
            var replaced = files.OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

            var used = MappedModFiles(meta);
            var stem = Path.GetFileNameWithoutExtension(sourceGamePath.Replace('\\', '/').Split('/')[^1]);
            for (var index = 0; index < sizeNames.Count; index++)
            {
                var name = $"{stem} {SizeFileStem(sizeNames[index])}";
                for (var count = 1; count <= 100 && files[index] is null; count++)
                {
                    var candidate = Path.Combine(directory, (count == 1 ? name : $"{name} {count}") + ".mdl");
                    var relative = Path.GetRelativePath(root, candidate).Replace('\\', '/');
                    // An unmapped file already there (an author's alternative or a leftover) is never taken either.
                    if (IsSafeRelativeModPath(relative) && !used.Contains(relative) && !File.Exists(candidate) &&
                        taken.Add(relative))
                        files[index] = relative;
                }
                if (files[index] is null)
                    return $"No free file name for the size {sizeNames[index]}.";
            }

            var optionFiles = sizeNames.Select((name, index) => new GroupOptionFile(name, files[index]!)).ToArray();
            var groupError = PrepareOptionGroup(modFolder, sourceGamePath, optionFiles, groupName, defaultSize, false,
                out var prepared, sourceOption, targetId);
            if (groupError is not null)
                return groupError;
            // An existing group keeps the default its author chose (BuildOptionGroup); only a new group starts on the shown size.
            plan = new SizeGroupPlan(prepared!,
                existingGroup is null ? groupName : JsonString(existingGroup["Name"]) ?? groupName, optionFiles, replaced);
            return null;
        }
        catch (Exception e)
        {
            return $"penumbra_group_write_failed: {e.Message}";
        }
    }

    /// <summary>The mod-relative files the default data and every option of the mod map.</summary>
    private static HashSet<string> MappedModFiles(JsonObject meta)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(JsonNode? container)
        {
            if (container is JsonObject value && value["Files"] is JsonObject files)
                foreach (var pair in files)
                    if (JsonString(pair.Value) is { } path)
                        used.Add(path.Replace('\\', '/').TrimStart('/'));
        }
        Add(meta["DefaultData"]);
        if (meta["Groups"] is JsonArray groups)
            foreach (var group in groups.OfType<JsonObject>())
                foreach (var key in new[] { "Options", "Containers" })
                    if (group[key] is JsonArray options)
                        foreach (var option in options)
                            Add(option);
        return used;
    }

    /// <summary>
    /// Write an export at several sizes into a Single group of the original Penumbra mod, one option per
    /// size (see PlanSizeGroup): one meta.json write, one reload and one redraw for all of them.
    /// </summary>
    public async Task<ExportResult> ApplySizesExportAsync(
        string sourceModDirectory,
        string sourceFilePath,
        string? sourceModRootPath,
        string? targetRelativePath,
        string sourceGamePath,
        IReadOnlyList<SizeExportFile> sizes,
        string defaultSize,
        string variantTarget,
        string? variantGroupName,
        string? variantTargetId,
        bool backupExisting = false,
        SourceOptionLocator? sourceOption = null,
        string sourceOptionStatus = "unknown",
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
        if (sizes.Count == 0)
            return new ExportResult(false, "invalid_sizes", "No sizes to export.");
        foreach (var size in sizes)
        {
            var validationError = ValidateExportRequest(sourceModDirectory, sourceGamePath, size.ExportedFile);
            if (validationError is not null)
                return new ExportResult(false, "invalid_export_file", validationError);
        }
        if (variantTarget is not ("new_group" or "group"))
            return new ExportResult(false, "invalid_variant_target", "Sizes go into an option group.");
        if (variantTarget == "new_group" && !IsSafeVariantGroupName(variantGroupName))
            return new ExportResult(false, "invalid_variant_group", "Penumbra variant setup requires an option group name.");
        if (variantTarget == "group" && string.IsNullOrWhiteSpace(variantTargetId))
            return new ExportResult(false, "stale_variant_target", "The selected Penumbra group is invalid.");

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

            if (sourceOptionStatus == "ambiguous")
                return new ExportResult(false, "ambiguous_source_option",
                    "Multiple Penumbra options provide the imported model. Re-import it from the intended option, then export again.");
            JsonObject? sourceOptionTemplate = null;
            if (sourceOptionStatus != "default")
            {
                var source = ResolveSourceOption(resolved.Target.Folder, sourceGamePath,
                    resolved.Target.RelativePath, sourceOption);
                if (source.Error is not null)
                    return new ExportResult(false, "source_option_unavailable", source.Error);
                sourceOptionTemplate = source.Option;
            }
            var planError = PlanSizeGroup(
                resolved.Target.Folder, sourceGamePath, resolved.Target.FilePath,
                sizes.Select(size => size.Name).ToArray(), variantGroupName ?? "",
                variantTarget == "group" ? variantTargetId : null, defaultSize, sourceOptionTemplate, out var plan);
            if (planError is not null)
                return new ExportResult(false,
                    variantTarget == "group" ? "stale_variant_target" : "invalid_variant_group", planError);

            var targets = plan!.Files.Select(file => (file.RelativePath, Path: Path.Combine(
                resolved.Target.Folder, file.RelativePath.Replace('/', Path.DirectorySeparatorChar)))).ToArray();
            // New files first: the files the group already maps are live, so they're only written over once
            // every new one is in place.
            var order = Enumerable.Range(0, sizes.Count)
                .OrderBy(index => plan.Replaced.Contains(targets[index].RelativePath)).ToArray();
            for (var step = 0; step < order.Length; step++)
            {
                var index = order[step];
                var (relative, targetFile) = targets[index];
                // The backup keeps the model's EST entries too, so restoring it undoes this export's EST change.
                var estState = backupExisting && File.Exists(targetFile)
                    ? CaptureEstState(resolved.Target.Folder, resolved.Target.Directory, relative, _estEntries)
                    : null;
                var writeError = WriteModelToOriginalLocation(
                    resolved.Target.Folder, targetFile, sizes[index].ExportedFile, backupExisting,
                    resolved.Target.Directory, relative, estState);
                if (writeError is null)
                    continue;
                var replacedSizes = order.Take(step).Where(done => plan.Replaced.Contains(targets[done].RelativePath))
                    .Select(done => sizes[done].Name).ToArray();
                return new ExportResult(false, "write_failed", step == 0
                    ? writeError
                    : replacedSizes.Length == 0
                        ? $"{writeError} {step} of {sizes.Count} sizes were written as new files; the group wasn't changed."
                        : $"{writeError} The models of {string.Join(", ", replacedSizes)} were already replaced; the group wasn't changed.");
            }
            var defaultIndex = Math.Max(0, sizes.ToList().FindIndex(size =>
                string.Equals(size.Name, defaultSize, StringComparison.OrdinalIgnoreCase)));
            committedTarget = targets[defaultIndex].Path;

            var warnings = new List<string>();
            var groupError = CommitVariantGroup(plan.Group);
            if (groupError is not null)
                warnings.Add($"Penumbra size group setup failed: {groupError}");

            if (createAttributeGroups && attributeTags is { Count: > 0 })
            {
                var attributeError = WriteAttributeGroups(
                    resolved.Target.Folder, resolvedGamePath, attributeTags, attributeMasks,
                    sourceManipulations);
                if (attributeError is not null)
                    warnings.Add($"Penumbra attribute group setup failed: {AttributeGroupErrorMessage(attributeError)}");
            }

            foreach (var (relative, _) in targets)
                foreach (var warning in TryUpdateEstEntries(
                             resolved.Target.Folder, resolved.Target.Directory, relative, estEntries))
                    if (!warnings.Contains(warning))
                        warnings.Add(warning);

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

            var code = warnings.Count == 0 ? "sizes_exported" : "sizes_exported_with_warnings";
            var message = warnings.Count == 0
                ? $"Exported {sizes.Count} sizes to {plan.GroupName} and reloaded {resolved.Target.Directory}."
                : $"Exported {sizes.Count} sizes to {plan.GroupName}; {warnings.Count} follow-up warning(s).";
            return new ExportResult(true, code, message, warnings, committedTarget, plan.GroupName);
        }
        catch (Exception e)
        {
            _log.Error(e, "Failed to export sizes to the original Penumbra mod.");
            if (committedTarget is not null)
                return new ExportResult(
                    true,
                    "sizes_exported_with_warnings",
                    $"Exported {sizes.Count} sizes; follow-up processing failed.",
                    [$"Follow-up processing failed: {e.Message}"],
                    committedTarget);
            return new ExportResult(false, "write_failed", $"Failed before the sizes could be written: {e.Message}");
        }
        finally
        {
            _exportGate.Release();
        }
    }
}
