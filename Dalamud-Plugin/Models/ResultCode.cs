using System.Text.Json;
using System.Text.Json.Serialization;

namespace InstantEdit.Models;

/// <summary>
/// The result codes the plugin puts in its results and bridge responses. The Blender add-on and the
/// Substance Painter plugin compare the wire strings (<see cref="ResultCodes.Wire"/>), so those never
/// change; JSON writes a code as its wire string. Codes other components send, such as a Blender
/// failure's code, stay plain strings.
/// </summary>
[JsonConverter(typeof(ResultCodeJsonConverter))]
public enum ResultCode
{
    // Reading the HTTP request (ExportServer).
    RequestHeadersInvalid,
    RequestLineInvalid,
    ContentLengthInvalid,
    RequestBodyTooLarge,
    RequestTooLarge,
    RequestBodyIncomplete,
    RequestForbidden,
    MalformedHttpRequest,
    EndpointNotFound,
    RequestRejected,
    InvalidErrorResponse,
    InternalError,

    // Request bodies and envelopes (ExportServer and its Sizes, Race, Skeletons and RacialDeformer parts).
    InvalidUtf8,
    InvalidJson,
    RequestNotObject,
    UnsupportedSchema,
    UnsupportedVersion,
    UnsupportedField,
    MissingField,
    InvalidSize,
    InvalidSha256,
    InvalidModName,
    InvalidVariantName,
    InvalidVariantTarget,
    MissingVariantTarget,
    PenumbraSetupRequiresVariant,
    PenumbraSetupRequiresGroupName,
    InvalidStatus,
    InvalidComponent,
    InvalidOperation,
    InvalidStage,
    InvalidErrorCode,
    InvalidCause,
    InvalidRemedy,
    InvalidDiagnosticId,
    InvalidExportFile,
    InvalidContributors,
    InvalidAttributeGroups,
    InvalidEstEntries,
    InvalidBackupName,
    InvalidBackupClear,
    DuplicateContext,
    InvalidRace,
    InvalidSizes,
    InvalidSizeName,
    InvalidDefaultSize,
    InvalidModelPath,

    // Contexts and export reservations (ExportContextRegistry).
    Accepted,
    StaleContext,
    InvalidContext,
    InvalidCapability,
    InvalidCallbackPort,
    PluginInstanceMismatch,
    ImportIdMismatch,
    ServerStopped,
    ContextReattached,
    ContextRevoked,
    ContextRemoved,
    InvalidExportId,
    DuplicateExport,
    DuplicateExportId,
    ExportNotFound,
    ExportPending,
    ExportComplete,
    ImportFailureRecorded,

    // Staging the exported file (ExportServer).
    UnsafeFilePath,
    FileNotFound,
    FileNotReadable,
    SizeMismatch,
    HashMismatch,

    // Model exports into Penumbra (ExportServer, ModelExportService, RaceExportService, SizesExportService).
    ExportApplied,
    ExportAppliedWithWarnings,
    ApplyFailed,
    DestinationNotReady,
    DestinationUnsafe,
    MissingNewModName,
    UnexpectedNewModName,
    InvalidPendingExport,
    InvalidDestinationState,
    MissingSourceTarget,
    InvalidVariant,
    InvalidVariantGroup,
    StaleVariantTarget,
    AmbiguousSourceOption,
    SourceOptionUnavailable,
    WriteFailed,
    PenumbraRootMissing,
    PenumbraRootUnsafe,
    PenumbraUnavailable,
    VanillaModExists,
    VanillaModCreated,
    VanillaModCreatedWithWarnings,
    VanillaModCreateFailed,
    RacialScalingMismatch,
    RaceModelUnavailable,
    RaceModelExported,
    RaceModelExportedWithWarnings,
    SizesNeedMod,
    SizesExported,
    SizesExportedWithWarnings,
    AttributeGroupConflict,
    AttributeGroupWriteFailed,

    // Finding the source mod and its option (SourceMods, ModOptionGroups, ModelExportService).
    DestinationUnavailable,
    DestinationUnresolvable,
    DestinationAmbiguous,
    DestinationMissing,
    SourceModMissing,
    SourceModIdentityUnavailable,
    SourceModChanged,
    SourceModAmbiguous,
    VariantTargetUnavailable,
    VariantTargetsLoaded,
    VariantTargetsUnavailable,

    // Managed backups (ModelBackupService).
    InvalidRestore,
    InvalidBackupTarget,
    BackupUnavailable,
    BackupMissing,
    RestoreWriteFailed,
    RestoreFailed,
    BackupRestored,
    BackupRestoredWithWarnings,
    BackupsCleared,
    BackupClearFailed,

    // Mashups (MashupPlanning, MashupService, ExportServer).
    InvalidMashup,
    MashupReimportRequired,
    MashupMaterialMissing,
    MashupMaterialPathUnsafe,
    MashupMaterialAliasUnsafe,
    MashupMaterialAliasConflict,
    MashupMaterialPathConflict,
    MashupTargetMaterialDirectoryAmbiguous,
    MashupTargetMaterialDirectoryInvalid,
    MashupTargetMaterialFamilyAmbiguous,
    MashupMaterialSlotsExhausted,
    MashupPlanReady,
    MashupPlanMismatch,
    MashupModelMismatch,
    MashupTextureConflict,
    MashupSourceChanged,
    MashupMaterialRewriteFailed,
    MashupMaterialHashConflict,
    MashupMappingConflict,
    MashupFailed,
    MashupDestinationExists,
    MashupApplied,
    MashupAppliedWithWarnings,
    MashupWriteFailed,
    MashupModExists,
    MashupModCreated,
    MashupModCreatedWithWarnings,
    MashupModCreateFailed,

    // Material coverage (ModResourceScanner).
    MaterialCoverageComplete,
    MaterialCoverageMissing,
    MaterialCoverageUnavailable,

    // Skeletons and racial scaling (ExportServer.Skeletons, ExportServer.RacialDeformer).
    SkeletonsUnavailable,
    SkeletonTimeout,
    SkeletonNotFound,
    RacialScalingUnavailable,
    RacialDeformerTimeout,

    // Substance Painter requests (PainterJobService).
    InvalidRequest,
    UnknownProject,
    RequestFailed,
    InvalidSend,
    DuplicateSend,
    UnknownSend,

    // Failures the plugin reports about Blender's side of the bridge (BlenderClient, BridgeFailure).
    BlenderConnectionFailed,
    BlenderRequestTimeout,
    InvalidSuccessResponse,
    LegacyHttpError,
}

public static class ResultCodes
{
    private static readonly IReadOnlyDictionary<string, ResultCode> ByWire =
        Enum.GetValues<ResultCode>().ToDictionary(code => code.Wire(), StringComparer.Ordinal);

    /// <summary>Every code, in declaration order.</summary>
    public static IReadOnlyList<ResultCode> All { get; } = Enum.GetValues<ResultCode>();

    /// <summary>The code from its wire string; false for a string that isn't one of the plugin's codes.</summary>
    public static bool TryParse(string? wire, out ResultCode code)
    {
        if (wire is not null && ByWire.TryGetValue(wire, out code))
            return true;
        code = default;
        return false;
    }

    /// <summary>
    /// The string the code is sent as. Never change one: the Blender add-on and the Painter plugin compare
    /// them, and Tests/ExportContextRegression checks each against its expected value. The switch lists
    /// every code, so a new code warns (CS8509) until it has a wire string.
    /// </summary>
#pragma warning disable CS8524 // Only the named codes exist; no other value reaches this switch.
    public static string Wire(this ResultCode code) => code switch
    {
        ResultCode.RequestHeadersInvalid => "request_headers_invalid",
        ResultCode.RequestLineInvalid => "request_line_invalid",
        ResultCode.ContentLengthInvalid => "content_length_invalid",
        ResultCode.RequestBodyTooLarge => "request_body_too_large",
        ResultCode.RequestTooLarge => "request_too_large",
        ResultCode.RequestBodyIncomplete => "request_body_incomplete",
        ResultCode.RequestForbidden => "request_forbidden",
        ResultCode.MalformedHttpRequest => "malformed_http_request",
        ResultCode.EndpointNotFound => "endpoint_not_found",
        ResultCode.RequestRejected => "request_rejected",
        ResultCode.InvalidErrorResponse => "invalid_error_response",
        ResultCode.InternalError => "internal_error",

        ResultCode.InvalidUtf8 => "invalid_utf8",
        ResultCode.InvalidJson => "invalid_json",
        ResultCode.RequestNotObject => "request_not_object",
        ResultCode.UnsupportedSchema => "unsupported_schema",
        ResultCode.UnsupportedVersion => "unsupported_version",
        ResultCode.UnsupportedField => "unsupported_field",
        ResultCode.MissingField => "missing_field",
        ResultCode.InvalidSize => "invalid_size",
        ResultCode.InvalidSha256 => "invalid_sha256",
        ResultCode.InvalidModName => "invalid_mod_name",
        ResultCode.InvalidVariantName => "invalid_variant_name",
        ResultCode.InvalidVariantTarget => "invalid_variant_target",
        ResultCode.MissingVariantTarget => "missing_variant_target",
        ResultCode.PenumbraSetupRequiresVariant => "penumbra_setup_requires_variant",
        ResultCode.PenumbraSetupRequiresGroupName => "penumbra_setup_requires_group_name",
        ResultCode.InvalidStatus => "invalid_status",
        ResultCode.InvalidComponent => "invalid_component",
        ResultCode.InvalidOperation => "invalid_operation",
        ResultCode.InvalidStage => "invalid_stage",
        ResultCode.InvalidErrorCode => "invalid_error_code",
        ResultCode.InvalidCause => "invalid_cause",
        ResultCode.InvalidRemedy => "invalid_remedy",
        ResultCode.InvalidDiagnosticId => "invalid_diagnostic_id",
        ResultCode.InvalidExportFile => "invalid_export_file",
        ResultCode.InvalidContributors => "invalid_contributors",
        ResultCode.InvalidAttributeGroups => "invalid_attribute_groups",
        ResultCode.InvalidEstEntries => "invalid_est_entries",
        ResultCode.InvalidBackupName => "invalid_backup_name",
        ResultCode.InvalidBackupClear => "invalid_backup_clear",
        ResultCode.DuplicateContext => "duplicate_context",
        ResultCode.InvalidRace => "invalid_race",
        ResultCode.InvalidSizes => "invalid_sizes",
        ResultCode.InvalidSizeName => "invalid_size_name",
        ResultCode.InvalidDefaultSize => "invalid_default_size",
        ResultCode.InvalidModelPath => "invalid_model_path",

        ResultCode.Accepted => "accepted",
        ResultCode.StaleContext => "stale_context",
        ResultCode.InvalidContext => "invalid_context",
        ResultCode.InvalidCapability => "invalid_capability",
        ResultCode.InvalidCallbackPort => "invalid_callback_port",
        ResultCode.PluginInstanceMismatch => "plugin_instance_mismatch",
        ResultCode.ImportIdMismatch => "import_id_mismatch",
        ResultCode.ServerStopped => "server_stopped",
        ResultCode.ContextReattached => "context_reattached",
        ResultCode.ContextRevoked => "context_revoked",
        ResultCode.ContextRemoved => "context_removed",
        ResultCode.InvalidExportId => "invalid_export_id",
        ResultCode.DuplicateExport => "duplicate_export",
        ResultCode.DuplicateExportId => "duplicate_export_id",
        ResultCode.ExportNotFound => "export_not_found",
        ResultCode.ExportPending => "export_pending",
        ResultCode.ExportComplete => "export_complete",
        ResultCode.ImportFailureRecorded => "import_failure_recorded",

        ResultCode.UnsafeFilePath => "unsafe_file_path",
        ResultCode.FileNotFound => "file_not_found",
        ResultCode.FileNotReadable => "file_not_readable",
        ResultCode.SizeMismatch => "size_mismatch",
        ResultCode.HashMismatch => "hash_mismatch",

        ResultCode.ExportApplied => "export_applied",
        ResultCode.ExportAppliedWithWarnings => "export_applied_with_warnings",
        ResultCode.ApplyFailed => "apply_failed",
        ResultCode.DestinationNotReady => "destination_not_ready",
        ResultCode.DestinationUnsafe => "destination_unsafe",
        ResultCode.MissingNewModName => "missing_new_mod_name",
        ResultCode.UnexpectedNewModName => "unexpected_new_mod_name",
        ResultCode.InvalidPendingExport => "invalid_pending_export",
        ResultCode.InvalidDestinationState => "invalid_destination_state",
        ResultCode.MissingSourceTarget => "missing_source_target",
        ResultCode.InvalidVariant => "invalid_variant",
        ResultCode.InvalidVariantGroup => "invalid_variant_group",
        ResultCode.StaleVariantTarget => "stale_variant_target",
        ResultCode.AmbiguousSourceOption => "ambiguous_source_option",
        ResultCode.SourceOptionUnavailable => "source_option_unavailable",
        ResultCode.WriteFailed => "write_failed",
        ResultCode.PenumbraRootMissing => "penumbra_root_missing",
        ResultCode.PenumbraRootUnsafe => "penumbra_root_unsafe",
        ResultCode.PenumbraUnavailable => "penumbra_unavailable",
        ResultCode.VanillaModExists => "vanilla_mod_exists",
        ResultCode.VanillaModCreated => "vanilla_mod_created",
        ResultCode.VanillaModCreatedWithWarnings => "vanilla_mod_created_with_warnings",
        ResultCode.VanillaModCreateFailed => "vanilla_mod_create_failed",
        ResultCode.RacialScalingMismatch => "racial_scaling_mismatch",
        ResultCode.RaceModelUnavailable => "race_model_unavailable",
        ResultCode.RaceModelExported => "race_model_exported",
        ResultCode.RaceModelExportedWithWarnings => "race_model_exported_with_warnings",
        ResultCode.SizesNeedMod => "sizes_need_mod",
        ResultCode.SizesExported => "sizes_exported",
        ResultCode.SizesExportedWithWarnings => "sizes_exported_with_warnings",
        ResultCode.AttributeGroupConflict => "attribute_group_conflict",
        ResultCode.AttributeGroupWriteFailed => "attribute_group_write_failed",

        ResultCode.DestinationUnavailable => "destination_unavailable",
        ResultCode.DestinationUnresolvable => "destination_unresolvable",
        ResultCode.DestinationAmbiguous => "destination_ambiguous",
        ResultCode.DestinationMissing => "destination_missing",
        ResultCode.SourceModMissing => "source_mod_missing",
        ResultCode.SourceModIdentityUnavailable => "source_mod_identity_unavailable",
        ResultCode.SourceModChanged => "source_mod_changed",
        ResultCode.SourceModAmbiguous => "source_mod_ambiguous",
        ResultCode.VariantTargetUnavailable => "variant_target_unavailable",
        ResultCode.VariantTargetsLoaded => "variant_targets_loaded",
        ResultCode.VariantTargetsUnavailable => "variant_targets_unavailable",

        ResultCode.InvalidRestore => "invalid_restore",
        ResultCode.InvalidBackupTarget => "invalid_backup_target",
        ResultCode.BackupUnavailable => "backup_unavailable",
        ResultCode.BackupMissing => "backup_missing",
        ResultCode.RestoreWriteFailed => "restore_write_failed",
        ResultCode.RestoreFailed => "restore_failed",
        ResultCode.BackupRestored => "backup_restored",
        ResultCode.BackupRestoredWithWarnings => "backup_restored_with_warnings",
        ResultCode.BackupsCleared => "backups_cleared",
        ResultCode.BackupClearFailed => "backup_clear_failed",

        ResultCode.InvalidMashup => "invalid_mashup",
        ResultCode.MashupReimportRequired => "mashup_reimport_required",
        ResultCode.MashupMaterialMissing => "mashup_material_missing",
        ResultCode.MashupMaterialPathUnsafe => "mashup_material_path_unsafe",
        ResultCode.MashupMaterialAliasUnsafe => "mashup_material_alias_unsafe",
        ResultCode.MashupMaterialAliasConflict => "mashup_material_alias_conflict",
        ResultCode.MashupMaterialPathConflict => "mashup_material_path_conflict",
        ResultCode.MashupTargetMaterialDirectoryAmbiguous => "mashup_target_material_directory_ambiguous",
        ResultCode.MashupTargetMaterialDirectoryInvalid => "mashup_target_material_directory_invalid",
        ResultCode.MashupTargetMaterialFamilyAmbiguous => "mashup_target_material_family_ambiguous",
        ResultCode.MashupMaterialSlotsExhausted => "mashup_material_slots_exhausted",
        ResultCode.MashupPlanReady => "mashup_plan_ready",
        ResultCode.MashupPlanMismatch => "mashup_plan_mismatch",
        ResultCode.MashupModelMismatch => "mashup_model_mismatch",
        ResultCode.MashupTextureConflict => "mashup_texture_conflict",
        ResultCode.MashupSourceChanged => "mashup_source_changed",
        ResultCode.MashupMaterialRewriteFailed => "mashup_material_rewrite_failed",
        ResultCode.MashupMaterialHashConflict => "mashup_material_hash_conflict",
        ResultCode.MashupMappingConflict => "mashup_mapping_conflict",
        ResultCode.MashupFailed => "mashup_failed",
        ResultCode.MashupDestinationExists => "mashup_destination_exists",
        ResultCode.MashupApplied => "mashup_applied",
        ResultCode.MashupAppliedWithWarnings => "mashup_applied_with_warnings",
        ResultCode.MashupWriteFailed => "mashup_write_failed",
        ResultCode.MashupModExists => "mashup_mod_exists",
        ResultCode.MashupModCreated => "mashup_mod_created",
        ResultCode.MashupModCreatedWithWarnings => "mashup_mod_created_with_warnings",
        ResultCode.MashupModCreateFailed => "mashup_mod_create_failed",

        ResultCode.MaterialCoverageComplete => "material_coverage_complete",
        ResultCode.MaterialCoverageMissing => "material_coverage_missing",
        ResultCode.MaterialCoverageUnavailable => "material_coverage_unavailable",

        ResultCode.SkeletonsUnavailable => "skeletons_unavailable",
        ResultCode.SkeletonTimeout => "skeleton_timeout",
        ResultCode.SkeletonNotFound => "skeleton_not_found",
        ResultCode.RacialScalingUnavailable => "racial_scaling_unavailable",
        ResultCode.RacialDeformerTimeout => "racial_deformer_timeout",

        ResultCode.InvalidRequest => "invalid_request",
        ResultCode.UnknownProject => "unknown_project",
        ResultCode.RequestFailed => "request_failed",
        ResultCode.InvalidSend => "invalid_send",
        ResultCode.DuplicateSend => "duplicate_send",
        ResultCode.UnknownSend => "unknown_send",

        ResultCode.BlenderConnectionFailed => "blender_connection_failed",
        ResultCode.BlenderRequestTimeout => "blender_request_timeout",
        ResultCode.InvalidSuccessResponse => "invalid_success_response",
        ResultCode.LegacyHttpError => "legacy_http_error",
    };
#pragma warning restore CS8524
}

/// <summary>Writes a <see cref="ResultCode"/> as its wire string and reads it back.</summary>
public sealed class ResultCodeJsonConverter : JsonConverter<ResultCode>
{
    public override ResultCode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => ResultCodes.TryParse(reader.GetString(), out var code)
            ? code
            : throw new JsonException($"'{reader.GetString()}' is not a result code.");

    public override void Write(Utf8JsonWriter writer, ResultCode value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Wire());
}
