using System.Text.Json;
using System.Text.RegularExpressions;
using InstantEdit.Models;
using InstantEdit.Services;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// Result codes go over the wire as fixed strings that the Blender add-on and the Substance Painter
/// plugin compare. Each code is checked against its expected string, so renaming a code or editing its
/// wire string fails here instead of silently breaking the other side. The codes the Python side
/// compares against must also exist on the C# side.
/// </summary>
internal static class ResultCodeScenarios
{
    // One line per code. A new code adds a line; an existing line never changes.
    private static readonly IReadOnlyDictionary<ResultCode, string> Expected = new Dictionary<ResultCode, string>
    {
        [ResultCode.RequestHeadersInvalid] = "request_headers_invalid",
        [ResultCode.RequestLineInvalid] = "request_line_invalid",
        [ResultCode.ContentLengthInvalid] = "content_length_invalid",
        [ResultCode.RequestBodyTooLarge] = "request_body_too_large",
        [ResultCode.RequestTooLarge] = "request_too_large",
        [ResultCode.RequestBodyIncomplete] = "request_body_incomplete",
        [ResultCode.RequestForbidden] = "request_forbidden",
        [ResultCode.MalformedHttpRequest] = "malformed_http_request",
        [ResultCode.EndpointNotFound] = "endpoint_not_found",
        [ResultCode.RequestRejected] = "request_rejected",
        [ResultCode.InvalidErrorResponse] = "invalid_error_response",
        [ResultCode.InternalError] = "internal_error",

        [ResultCode.InvalidUtf8] = "invalid_utf8",
        [ResultCode.InvalidJson] = "invalid_json",
        [ResultCode.RequestNotObject] = "request_not_object",
        [ResultCode.UnsupportedSchema] = "unsupported_schema",
        [ResultCode.UnsupportedVersion] = "unsupported_version",
        [ResultCode.UnsupportedField] = "unsupported_field",
        [ResultCode.MissingField] = "missing_field",
        [ResultCode.InvalidSize] = "invalid_size",
        [ResultCode.InvalidSha256] = "invalid_sha256",
        [ResultCode.InvalidModName] = "invalid_mod_name",
        [ResultCode.InvalidVariantName] = "invalid_variant_name",
        [ResultCode.InvalidVariantTarget] = "invalid_variant_target",
        [ResultCode.MissingVariantTarget] = "missing_variant_target",
        [ResultCode.PenumbraSetupRequiresVariant] = "penumbra_setup_requires_variant",
        [ResultCode.PenumbraSetupRequiresGroupName] = "penumbra_setup_requires_group_name",
        [ResultCode.InvalidStatus] = "invalid_status",
        [ResultCode.InvalidComponent] = "invalid_component",
        [ResultCode.InvalidOperation] = "invalid_operation",
        [ResultCode.InvalidStage] = "invalid_stage",
        [ResultCode.InvalidErrorCode] = "invalid_error_code",
        [ResultCode.InvalidCause] = "invalid_cause",
        [ResultCode.InvalidRemedy] = "invalid_remedy",
        [ResultCode.InvalidDiagnosticId] = "invalid_diagnostic_id",
        [ResultCode.InvalidExportFile] = "invalid_export_file",
        [ResultCode.InvalidContributors] = "invalid_contributors",
        [ResultCode.InvalidAttributeGroups] = "invalid_attribute_groups",
        [ResultCode.InvalidEstEntries] = "invalid_est_entries",
        [ResultCode.InvalidBackupName] = "invalid_backup_name",
        [ResultCode.InvalidBackupClear] = "invalid_backup_clear",
        [ResultCode.DuplicateContext] = "duplicate_context",
        [ResultCode.InvalidRace] = "invalid_race",
        [ResultCode.InvalidSizes] = "invalid_sizes",
        [ResultCode.InvalidSizeName] = "invalid_size_name",
        [ResultCode.InvalidDefaultSize] = "invalid_default_size",
        [ResultCode.InvalidModelPath] = "invalid_model_path",

        [ResultCode.Accepted] = "accepted",
        [ResultCode.StaleContext] = "stale_context",
        [ResultCode.InvalidContext] = "invalid_context",
        [ResultCode.InvalidCapability] = "invalid_capability",
        [ResultCode.InvalidCallbackPort] = "invalid_callback_port",
        [ResultCode.PluginInstanceMismatch] = "plugin_instance_mismatch",
        [ResultCode.ImportIdMismatch] = "import_id_mismatch",
        [ResultCode.ServerStopped] = "server_stopped",
        [ResultCode.ContextReattached] = "context_reattached",
        [ResultCode.ContextRevoked] = "context_revoked",
        [ResultCode.ContextRemoved] = "context_removed",
        [ResultCode.InvalidExportId] = "invalid_export_id",
        [ResultCode.DuplicateExport] = "duplicate_export",
        [ResultCode.DuplicateExportId] = "duplicate_export_id",
        [ResultCode.ExportNotFound] = "export_not_found",
        [ResultCode.ExportPending] = "export_pending",
        [ResultCode.ExportComplete] = "export_complete",
        [ResultCode.ImportFailureRecorded] = "import_failure_recorded",

        [ResultCode.UnsafeFilePath] = "unsafe_file_path",
        [ResultCode.FileNotFound] = "file_not_found",
        [ResultCode.FileNotReadable] = "file_not_readable",
        [ResultCode.SizeMismatch] = "size_mismatch",
        [ResultCode.HashMismatch] = "hash_mismatch",

        [ResultCode.ExportApplied] = "export_applied",
        [ResultCode.ExportAppliedWithWarnings] = "export_applied_with_warnings",
        [ResultCode.ApplyFailed] = "apply_failed",
        [ResultCode.DestinationNotReady] = "destination_not_ready",
        [ResultCode.DestinationUnsafe] = "destination_unsafe",
        [ResultCode.MissingNewModName] = "missing_new_mod_name",
        [ResultCode.UnexpectedNewModName] = "unexpected_new_mod_name",
        [ResultCode.InvalidPendingExport] = "invalid_pending_export",
        [ResultCode.InvalidDestinationState] = "invalid_destination_state",
        [ResultCode.MissingSourceTarget] = "missing_source_target",
        [ResultCode.InvalidVariant] = "invalid_variant",
        [ResultCode.InvalidVariantGroup] = "invalid_variant_group",
        [ResultCode.StaleVariantTarget] = "stale_variant_target",
        [ResultCode.AmbiguousSourceOption] = "ambiguous_source_option",
        [ResultCode.SourceOptionUnavailable] = "source_option_unavailable",
        [ResultCode.WriteFailed] = "write_failed",
        [ResultCode.PenumbraRootMissing] = "penumbra_root_missing",
        [ResultCode.PenumbraRootUnsafe] = "penumbra_root_unsafe",
        [ResultCode.PenumbraUnavailable] = "penumbra_unavailable",
        [ResultCode.VanillaModExists] = "vanilla_mod_exists",
        [ResultCode.VanillaModCreated] = "vanilla_mod_created",
        [ResultCode.VanillaModCreatedWithWarnings] = "vanilla_mod_created_with_warnings",
        [ResultCode.VanillaModCreateFailed] = "vanilla_mod_create_failed",
        [ResultCode.RacialScalingMismatch] = "racial_scaling_mismatch",
        [ResultCode.RaceModelUnavailable] = "race_model_unavailable",
        [ResultCode.RaceModelExported] = "race_model_exported",
        [ResultCode.RaceModelExportedWithWarnings] = "race_model_exported_with_warnings",
        [ResultCode.SizesNeedMod] = "sizes_need_mod",
        [ResultCode.SizesExported] = "sizes_exported",
        [ResultCode.SizesExportedWithWarnings] = "sizes_exported_with_warnings",
        [ResultCode.AttributeGroupConflict] = "attribute_group_conflict",
        [ResultCode.AttributeGroupWriteFailed] = "attribute_group_write_failed",

        [ResultCode.DestinationUnavailable] = "destination_unavailable",
        [ResultCode.DestinationUnresolvable] = "destination_unresolvable",
        [ResultCode.DestinationAmbiguous] = "destination_ambiguous",
        [ResultCode.DestinationMissing] = "destination_missing",
        [ResultCode.SourceModMissing] = "source_mod_missing",
        [ResultCode.SourceModIdentityUnavailable] = "source_mod_identity_unavailable",
        [ResultCode.SourceModChanged] = "source_mod_changed",
        [ResultCode.SourceModAmbiguous] = "source_mod_ambiguous",
        [ResultCode.VariantTargetUnavailable] = "variant_target_unavailable",
        [ResultCode.VariantTargetsLoaded] = "variant_targets_loaded",
        [ResultCode.VariantTargetsUnavailable] = "variant_targets_unavailable",

        [ResultCode.InvalidRestore] = "invalid_restore",
        [ResultCode.InvalidBackupTarget] = "invalid_backup_target",
        [ResultCode.BackupUnavailable] = "backup_unavailable",
        [ResultCode.BackupMissing] = "backup_missing",
        [ResultCode.RestoreWriteFailed] = "restore_write_failed",
        [ResultCode.RestoreFailed] = "restore_failed",
        [ResultCode.BackupRestored] = "backup_restored",
        [ResultCode.BackupRestoredWithWarnings] = "backup_restored_with_warnings",
        [ResultCode.BackupsCleared] = "backups_cleared",
        [ResultCode.BackupClearFailed] = "backup_clear_failed",

        [ResultCode.InvalidMashup] = "invalid_mashup",
        [ResultCode.MashupReimportRequired] = "mashup_reimport_required",
        [ResultCode.MashupMaterialMissing] = "mashup_material_missing",
        [ResultCode.MashupMaterialPathUnsafe] = "mashup_material_path_unsafe",
        [ResultCode.MashupMaterialAliasUnsafe] = "mashup_material_alias_unsafe",
        [ResultCode.MashupMaterialAliasConflict] = "mashup_material_alias_conflict",
        [ResultCode.MashupMaterialPathConflict] = "mashup_material_path_conflict",
        [ResultCode.MashupTargetMaterialDirectoryAmbiguous] = "mashup_target_material_directory_ambiguous",
        [ResultCode.MashupTargetMaterialDirectoryInvalid] = "mashup_target_material_directory_invalid",
        [ResultCode.MashupTargetMaterialFamilyAmbiguous] = "mashup_target_material_family_ambiguous",
        [ResultCode.MashupMaterialSlotsExhausted] = "mashup_material_slots_exhausted",
        [ResultCode.MashupPlanReady] = "mashup_plan_ready",
        [ResultCode.MashupPlanMismatch] = "mashup_plan_mismatch",
        [ResultCode.MashupModelMismatch] = "mashup_model_mismatch",
        [ResultCode.MashupTextureConflict] = "mashup_texture_conflict",
        [ResultCode.MashupSourceChanged] = "mashup_source_changed",
        [ResultCode.MashupMaterialRewriteFailed] = "mashup_material_rewrite_failed",
        [ResultCode.MashupMaterialHashConflict] = "mashup_material_hash_conflict",
        [ResultCode.MashupMappingConflict] = "mashup_mapping_conflict",
        [ResultCode.MashupFailed] = "mashup_failed",
        [ResultCode.MashupDestinationExists] = "mashup_destination_exists",
        [ResultCode.MashupApplied] = "mashup_applied",
        [ResultCode.MashupAppliedWithWarnings] = "mashup_applied_with_warnings",
        [ResultCode.MashupWriteFailed] = "mashup_write_failed",
        [ResultCode.MashupModExists] = "mashup_mod_exists",
        [ResultCode.MashupModCreated] = "mashup_mod_created",
        [ResultCode.MashupModCreatedWithWarnings] = "mashup_mod_created_with_warnings",
        [ResultCode.MashupModCreateFailed] = "mashup_mod_create_failed",

        [ResultCode.MaterialCoverageComplete] = "material_coverage_complete",
        [ResultCode.MaterialCoverageMissing] = "material_coverage_missing",
        [ResultCode.MaterialCoverageUnavailable] = "material_coverage_unavailable",

        [ResultCode.SkeletonsUnavailable] = "skeletons_unavailable",
        [ResultCode.SkeletonTimeout] = "skeleton_timeout",
        [ResultCode.SkeletonNotFound] = "skeleton_not_found",
        [ResultCode.RacialScalingUnavailable] = "racial_scaling_unavailable",
        [ResultCode.RacialDeformerTimeout] = "racial_deformer_timeout",

        [ResultCode.InvalidRequest] = "invalid_request",
        [ResultCode.UnknownProject] = "unknown_project",
        [ResultCode.RequestFailed] = "request_failed",
        [ResultCode.InvalidSend] = "invalid_send",
        [ResultCode.DuplicateSend] = "duplicate_send",
        [ResultCode.UnknownSend] = "unknown_send",

        [ResultCode.BlenderConnectionFailed] = "blender_connection_failed",
        [ResultCode.BlenderRequestTimeout] = "blender_request_timeout",
        [ResultCode.InvalidSuccessResponse] = "invalid_success_response",
        [ResultCode.LegacyHttpError] = "legacy_http_error",
    };

    // The Python files that read the plugin's responses. Their tests are left out: they also compare
    // codes the Python side makes itself.
    private static readonly string[] PythonSources =
    [
        Path.Combine("Blender-Addon", "instant_edit"),
        Path.Combine("Tools", "SubstancePainter", "xiv_instant_edit"),
    ];

    // code == "x", code != "x", .get("code") == "x" and code in {"x", "y"}.
    private static readonly Regex Comparison = new(
        """\bcode\b["'\])]*\s*(?:==|!=)\s*["']([a-z0-9_]+)["']""", RegexOptions.Compiled);
    private static readonly Regex Membership = new(
        """\bcode\b["'\])]*\s+(?:not\s+)?in\s*[(\[{]([^)\]}]*)[)\]}]""", RegexOptions.Compiled);
    private static readonly Regex Quoted = new("""["']([a-z0-9_]+)["']""", RegexOptions.Compiled);

    public static void Run()
    {
        CodesKeepTheirWireStrings();
        PythonComparesOnlyPluginCodes();
    }

    private static void CodesKeepTheirWireStrings()
    {
        var missing = ResultCodes.All.Where(code => !Expected.ContainsKey(code)).ToList();
        Require(missing.Count == 0,
            "result codes: every code has an expected wire string" + Listed(missing.Select(code => code.ToString())));
        Require(Expected.Values.Distinct(StringComparer.Ordinal).Count() == Expected.Count,
            "result codes: no two codes share a wire string");

        var wrong = Expected.Where(pair => pair.Key.Wire() != pair.Value).Select(pair => $"{pair.Key} is {pair.Key.Wire()}");
        Require(!wrong.Any(), "result codes: each code's wire string is the expected one" + Listed(wrong));

        var serialized = Expected.Where(pair =>
                JsonSerializer.Serialize(pair.Key) != $"\"{pair.Value}\"" ||
                JsonSerializer.Serialize(new { code = pair.Key }) != $"{{\"code\":\"{pair.Value}\"}}")
            .Select(pair => pair.Value);
        Require(!serialized.Any(), "result codes: JSON writes each code as its wire string" + Listed(serialized));

        var parsed = Expected.Where(pair =>
                !ResultCodes.TryParse(pair.Value, out var code) || code != pair.Key ||
                JsonSerializer.Deserialize<ResultCode>($"\"{pair.Value}\"") != pair.Key)
            .Select(pair => pair.Value);
        Require(!parsed.Any(), "result codes: each wire string reads back as its code" + Listed(parsed));
        Require(!ResultCodes.TryParse("ExportApplied", out _) && !ResultCodes.TryParse(null, out _),
            "result codes: member names and null are not wire strings");

        var receipt = JsonSerializer.Serialize(new ExportReceipt(false, ResultCode.StaleContext, "gone"));
        var result = JsonSerializer.Serialize(new ExportResult(true, "written"));
        Require(receipt.Contains("\"Code\":\"stale_context\"", StringComparison.Ordinal) &&
                result.Contains("\"Code\":\"export_applied\"", StringComparison.Ordinal),
            "result codes: records holding a code serialize it as its wire string");
    }

    private static void PythonComparesOnlyPluginCodes()
    {
        var root = RepositoryRoot();
        var compared = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var folder in PythonSources)
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, folder), "*.py", SearchOption.AllDirectories))
            {
                var lines = File.ReadAllLines(file);
                for (var index = 0; index < lines.Length; index++)
                {
                    var codes = Comparison.Matches(lines[index]).Select(match => match.Groups[1].Value)
                        .Concat(Membership.Matches(lines[index])
                            .SelectMany(match => Quoted.Matches(match.Groups[1].Value).Select(quoted => quoted.Groups[1].Value)));
                    foreach (var code in codes)
                    {
                        if (!compared.TryGetValue(code, out var places))
                            compared[code] = places = [];
                        places.Add($"{Path.GetRelativePath(root, file)}:{index + 1}");
                    }
                }
            }

        // The comparisons known today; finding them shows the scan still reads the files.
        string[] known = ["endpoint_not_found", "export_pending", "plugin_instance_mismatch", "unknown_send"];
        Require(known.All(compared.ContainsKey),
            "result codes: the Python scan finds the known code comparisons" + Listed(known.Where(code => !compared.ContainsKey(code))));
        var unknown = compared.Where(pair => !ResultCodes.TryParse(pair.Key, out _))
            .Select(pair => $"{pair.Key} ({string.Join(", ", pair.Value)})");
        Require(!unknown.Any(), "result codes: every code the Python side compares against is a plugin code" + Listed(unknown));
    }

    private static string Listed(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count == 0 ? "" : ": " + string.Join("; ", list);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Dalamud-Plugin", "InstantEdit.csproj")))
            directory = directory.Parent;
        if (directory is null)
            throw new DirectoryNotFoundException("The repository was not found above the test output.");
        return directory.FullName;
    }
}
