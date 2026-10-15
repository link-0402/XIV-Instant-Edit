using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using InstantEdit.Models;
using InstantEdit.Services.GameFiles;

namespace InstantEdit.Services;

public sealed partial class ExportServer
{
    public const string RaceExportCapability = "instant-edit.export-race.v1";

    /// <summary>
    /// A Quick Export of meshes scaled for another race (<see cref="Race"/>, <c>c0801</c>) as that race's
    /// model: a new file next to the imported one, mapped at the race's game path wherever the mod maps
    /// the imported model, with the EQDP entry that makes the race load it. For a game model the first
    /// export creates the mod (<see cref="NewModName"/>) with just that file.
    /// </summary>
    internal sealed class RaceExportRequest
    {
        [JsonPropertyName("schema")]
        public string? Schema { get; set; }

        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("pluginInstanceId")]
        public string? PluginInstanceId { get; set; }

        [JsonPropertyName("contextId")]
        public string? ContextId { get; set; }

        [JsonPropertyName("exportId")]
        public string? ExportId { get; set; }

        [JsonPropertyName("capability")]
        public string? Capability { get; set; }

        [JsonPropertyName("race")]
        public string? Race { get; set; }

        [JsonPropertyName("filePath")]
        public string? FilePath { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("sha256")]
        public string? Sha256 { get; set; }

        [JsonPropertyName("backupExisting")]
        public bool BackupExisting { get; set; }

        [JsonPropertyName("newModName")]
        public string? NewModName { get; set; }

        [JsonPropertyName("createAttributeGroups")]
        public bool CreateAttributeGroups { get; set; }

        [JsonPropertyName("attributeTags")]
        public List<string>? AttributeTags { get; set; }

        [JsonPropertyName("attributeMasks")]
        public Dictionary<string, int>? AttributeMasks { get; set; }

        [JsonPropertyName("estEntries")]
        public List<EstEntryRequest>? EstEntries { get; set; }
    }

    private async Task<(int Status, string Body)> HandleRaceExportAsync(HttpRequest request)
    {
        var parsed = DeserializeRequest<RaceExportRequest>(request.Body, "race export", out var parseError);
        if (parseError is not null)
            return parseError.Value;
        var export = parsed!;

        if (ValidateRaceEnvelope(export) is { } envelopeError)
            return Error(StatusForCode(envelopeError), envelopeError, "unsupported or malformed race export envelope");

        if (!_contexts.TryBeginExport(
                export.PluginInstanceId!,
                export.ContextId!,
                export.ExportId!,
                export.Capability!,
                export.FilePath!,
                export.Size,
                export.Sha256!,
                out var reservation,
                out var registryCode,
                RaceExportRequestFingerprint(export)))
            return Error(StatusForCode(registryCode), registryCode, "export context was rejected");
        if (reservation is null)
            return Error(500, ResultCode.InternalError, "export reservation was not created");
        if (!reservation.IsOwner)
            return ResultResponse(await reservation.Completion.ConfigureAwait(false));

        ExportReceipt receipt;
        StagedExport? staged = null;
        try
        {
            var stageResult = await StageExportFileAsync(export.FilePath!, export.Size, export.Sha256!).ConfigureAwait(false);
            staged = stageResult.Export;
            var target = reservation.Context;
            var race = (ushort)int.Parse(export.Race!.AsSpan(1));
            if (stageResult.Error is not null)
                receipt = new ExportReceipt(false, stageResult.Error.Value.Code, stageResult.Error.Value.Message);
            else if (RaceExportRefusal(target, race) is { } refusal)
                receipt = refusal;
            else if (target.DestinationState == InstantEditImportContext.NewModRequiredDestination)
                receipt = await CreateRaceModelModAsync(target, staged!.FilePath, race, export).ConfigureAwait(false);
            else if (export.NewModName is not null)
                receipt = new ExportReceipt(false, ResultCode.UnexpectedNewModName, "this import already has a Penumbra destination");
            else if (string.IsNullOrWhiteSpace(target.TargetFilePath) ||
                     string.IsNullOrWhiteSpace(target.SourceModDirectory))
                receipt = new ExportReceipt(false, ResultCode.MissingSourceTarget,
                    "the import has no original Penumbra mod destination");
            else
            {
                var result = await _penumbra.ApplyRaceExportAsync(
                    target.SourceModDirectory!,
                    target.TargetFilePath!,
                    target.SourceModRootPath,
                    target.TargetRelativePath,
                    target.GamePath,
                    race,
                    staged!.FilePath,
                    export.BackupExisting,
                    target.SourceModStableId,
                    export.CreateAttributeGroups,
                    export.AttributeTags,
                    export.AttributeMasks,
                    target.ResolvedGamePath,
                    PenumbraService.KnownManipulations(target),
                    export.EstEntries).ConfigureAwait(false);
                receipt = new ExportReceipt(
                    result.Success, result.Code, result.Message, result.WarningList, result.TargetFilePath);
            }
        }
        catch (Exception e)
        {
            _log.Error(e, "Race export processing failed.");
            receipt = new ExportReceipt(false, ResultCode.InternalError, "export processing failed");
        }
        finally
        {
            CleanupStagedExport(staged);
        }

        _contexts.CompleteExport(export.ContextId!, export.ExportId!, receipt);
        _log.Information($"Race export {receipt.Code.Wire()}: {receipt.Message}");
        return ResultResponse(receipt);
    }

    /// <summary>A game model's first race export: a new mod with only the race's model, its context promoted to it.</summary>
    private async Task<ExportReceipt> CreateRaceModelModAsync(
        InstantEditImportContext target, string filePath, ushort race, RaceExportRequest export)
    {
        if (string.IsNullOrWhiteSpace(export.NewModName))
            return new ExportReceipt(false, ResultCode.MissingNewModName, "enter a name for the new Penumbra mod");
        if (export.BackupExisting)
            return new ExportReceipt(false, ResultCode.InvalidPendingExport, "a first vanilla export cannot target backups");
        return await CreateGameModelModAsync(
                target, filePath, export.NewModName, export.CreateAttributeGroups, export.AttributeTags,
                export.AttributeMasks, export.EstEntries, race)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Why an import can't be written as a model of <paramref name="race"/>: its game path isn't a gear or
    /// accessory model (only those pick a race's own model by EQDP), it already is that race's, or it was
    /// sent scaled for another race. Null when it can.
    /// </summary>
    internal static ExportReceipt? RaceExportRefusal(InstantEditImportContext context, ushort race)
    {
        if (context.RacialScaling is { } scaling && scaling.CharacterRace != race)
            return new ExportReceipt(false, ResultCode.RacialScalingMismatch,
                $"This model was sent to Blender racially scaled (c{scaling.ModelRace:D4} to {GameRaces.Label(scaling.CharacterRace)}), " +
                $"so it can't go into a c{race:D4} model.");
        if (GamePathRace(context) == race)
            return new ExportReceipt(false, ResultCode.RaceModelUnavailable,
                $"The imported model already is a c{race:D4} model. Export it in place.");
        if (PenumbraService.RaceModelGamePath(context.GamePath, race) is null)
            return new ExportReceipt(false, ResultCode.RaceModelUnavailable,
                "Only gear and accessory models can be saved for another race.");
        return null;
    }

    internal static ResultCode? ValidateRaceEnvelope(RaceExportRequest request)
    {
        if (ValidateSchemaVersion(request.Schema, "instant-edit.export-race", request.Version, 1) is { } schemaError)
            return schemaError;
        if (string.IsNullOrWhiteSpace(request.PluginInstanceId) ||
            string.IsNullOrWhiteSpace(request.ContextId) ||
            string.IsNullOrWhiteSpace(request.ExportId) ||
            string.IsNullOrWhiteSpace(request.Capability) ||
            string.IsNullOrWhiteSpace(request.FilePath) ||
            string.IsNullOrWhiteSpace(request.Sha256) ||
            string.IsNullOrWhiteSpace(request.Race))
            return ResultCode.MissingField;
        if (PenumbraService.PlayerRaceCode(request.Race) is null)
            return ResultCode.InvalidRace;
        if (request.Size <= 0 || request.Size > MaxExportBytes)
            return ResultCode.InvalidSize;
        if (request.Sha256.Length != 64 || request.Sha256.Any(c => !Uri.IsHexDigit(c)))
            return ResultCode.InvalidSha256;
        if (request.NewModName is not null && !PenumbraService.IsSafeNewModName(request.NewModName))
            return ResultCode.InvalidModName;
        return ValidateAttributeGroupEnvelope(
                   request.CreateAttributeGroups, request.AttributeTags, request.AttributeMasks) ??
               PenumbraService.ValidateEstEntryRequests(request.EstEntries);
    }

    internal static string RaceExportRequestFingerprint(RaceExportRequest request)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            request.Schema,
            request.Version,
            request.Race,
            request.BackupExisting,
            request.NewModName,
            request.CreateAttributeGroups,
            request.AttributeTags,
            request.AttributeMasks,
            request.EstEntries,
        })));
}
