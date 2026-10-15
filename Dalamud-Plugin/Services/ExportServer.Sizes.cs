using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using InstantEdit.Models;

namespace InstantEdit.Services;

public sealed partial class ExportServer
{
    public const string SizesExportCapability = "instant-edit.export-sizes.v1";

    // Every combination of the values picked in Magic Fit is a size.
    private const int MaxSizes = 64;

    internal sealed class SizeFileRequest
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("filePath")]
        public string? FilePath { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("sha256")]
        public string? Sha256 { get; set; }
    }

    /// <summary>
    /// A Quick Export at several sizes (Magic Fit's), each size an option of one group: a new one
    /// (<c>new_group</c>, <see cref="VariantGroupName"/>) or one of the mod's (<c>group</c>,
    /// <see cref="VariantTargetId"/>).
    /// </summary>
    internal sealed class SizesExportRequest
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

        [JsonPropertyName("variantTarget")]
        public string? VariantTarget { get; set; }

        [JsonPropertyName("variantGroupName")]
        public string? VariantGroupName { get; set; }

        [JsonPropertyName("variantTargetId")]
        public string? VariantTargetId { get; set; }

        /// <summary>The size the group turns on by default; null for the first.</summary>
        [JsonPropertyName("defaultSize")]
        public string? DefaultSize { get; set; }

        [JsonPropertyName("sizes")]
        public List<SizeFileRequest>? Sizes { get; set; }

        [JsonPropertyName("backupExisting")]
        public bool BackupExisting { get; set; }

        [JsonPropertyName("createAttributeGroups")]
        public bool CreateAttributeGroups { get; set; }

        [JsonPropertyName("attributeTags")]
        public List<string>? AttributeTags { get; set; }

        [JsonPropertyName("attributeMasks")]
        public Dictionary<string, int>? AttributeMasks { get; set; }

        [JsonPropertyName("estEntries")]
        public List<EstEntryRequest>? EstEntries { get; set; }
    }

    private async Task<(int Status, string Body)> HandleSizesExportAsync(HttpRequest request)
    {
        var parsed = DeserializeRequest<SizesExportRequest>(request.Body, "sizes export", out var parseError);
        if (parseError is not null)
            return parseError.Value;
        var export = parsed!;

        var envelopeError = ValidateSizesEnvelope(export);
        if (envelopeError is not null)
            return Error(StatusForCode(envelopeError), envelopeError, "unsupported or malformed sizes export envelope");

        var sizes = export.Sizes!;
        if (!_contexts.TryBeginExport(
                export.PluginInstanceId!,
                export.ContextId!,
                export.ExportId!,
                export.Capability!,
                sizes[0].FilePath!,
                sizes.Sum(size => size.Size),
                SizesManifestHash(sizes),
                out var reservation,
                out var registryCode,
                SizesExportRequestFingerprint(export)))
            return Error(StatusForCode(registryCode), registryCode, "export context was rejected");
        if (reservation is null)
            return Error(500, "internal_error", "export reservation was not created");
        if (!reservation.IsOwner)
            return ResultResponse(await reservation.Completion.ConfigureAwait(false));

        ExportReceipt receipt;
        var staged = new List<StagedExport>();
        try
        {
            (string Code, string Message)? stageError = null;
            foreach (var size in sizes)
            {
                var stageResult = await StageExportFileAsync(size.FilePath!, size.Size, size.Sha256!).ConfigureAwait(false);
                if (stageResult.Export is not null)
                    staged.Add(stageResult.Export);
                if (stageResult.Error is not null)
                {
                    stageError = stageResult.Error;
                    break;
                }
            }
            var target = reservation.Context;
            if (stageError is not null)
                receipt = new ExportReceipt(false, stageError.Value.Code, stageError.Value.Message);
            else if (RacialScalingRefusal([target], GamePathRace(target)) is { } scalingRefusal)
                receipt = scalingRefusal;
            else if (target.DestinationState == InstantEditImportContext.NewModRequiredDestination)
                receipt = new ExportReceipt(false, "sizes_need_mod",
                    "export once without sizes to create the Penumbra mod first");
            else if (string.IsNullOrWhiteSpace(target.TargetFilePath) ||
                     string.IsNullOrWhiteSpace(target.SourceModDirectory))
                receipt = new ExportReceipt(false, "missing_source_target",
                    "the import has no original Penumbra mod destination");
            else
            {
                var result = await _penumbra.ApplySizesExportAsync(
                    target.SourceModDirectory!,
                    target.TargetFilePath!,
                    target.SourceModRootPath,
                    target.TargetRelativePath,
                    target.GamePath,
                    sizes.Select((size, index) => new SizeExportFile(size.Name!, staged[index].FilePath))
                        .ToArray(),
                    export.DefaultSize ?? sizes[0].Name!,
                    export.VariantTarget!,
                    export.VariantGroupName,
                    export.VariantTargetId,
                    export.BackupExisting,
                    target.SourceOption,
                    target.SourceOptionStatus,
                    target.SourceModStableId,
                    export.CreateAttributeGroups,
                    export.AttributeTags,
                    export.AttributeMasks,
                    target.ResolvedGamePath,
                    PenumbraService.KnownManipulations(target),
                    export.EstEntries).ConfigureAwait(false);
                receipt = new ExportReceipt(
                    result.Success, result.Code, result.Message, result.WarningList, result.TargetFilePath,
                    result.DestinationName);
            }
        }
        catch (Exception e)
        {
            _log.Error(e, "Sizes export processing failed.");
            receipt = new ExportReceipt(false, "internal_error", "export processing failed");
        }
        finally
        {
            foreach (var item in staged)
                CleanupStagedExport(item);
        }

        _contexts.CompleteExport(export.ContextId!, export.ExportId!, receipt);
        _log.Information($"Sizes export {receipt.Code}: {receipt.Message}");
        return ResultResponse(receipt);
    }

    internal static string? ValidateSizesEnvelope(SizesExportRequest request)
    {
        if (ValidateSchemaVersion(request.Schema, "instant-edit.export-sizes", request.Version, 1) is { } schemaError)
            return schemaError;
        if (string.IsNullOrWhiteSpace(request.PluginInstanceId) ||
            string.IsNullOrWhiteSpace(request.ContextId) ||
            string.IsNullOrWhiteSpace(request.ExportId) ||
            string.IsNullOrWhiteSpace(request.Capability))
            return "missing_field";
        if (request.VariantTarget is not ("new_group" or "group"))
            return "invalid_variant_target";
        if (request.VariantTarget == "new_group" && !PenumbraService.IsSafeVariantGroupName(request.VariantGroupName))
            return "penumbra_setup_requires_group_name";
        if (request.VariantTarget == "group" && string.IsNullOrWhiteSpace(request.VariantTargetId))
            return "missing_variant_target";
        if (request.Sizes is not { Count: >= 1 and <= MaxSizes })
            return "invalid_sizes";
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var size in request.Sizes)
        {
            if (size is null || !PenumbraService.IsSafeSizeName(size.Name) || !names.Add(size.Name!))
                return "invalid_size_name";
            if (string.IsNullOrWhiteSpace(size.FilePath) || string.IsNullOrWhiteSpace(size.Sha256))
                return "missing_field";
            if (size.Size <= 0 || size.Size > MaxExportBytes)
                return "invalid_size";
            if (size.Sha256.Length != 64 || size.Sha256.Any(c => !Uri.IsHexDigit(c)))
                return "invalid_sha256";
        }
        if (request.DefaultSize is not null && !names.Contains(request.DefaultSize))
            return "invalid_default_size";
        return ValidateAttributeGroupEnvelope(
                   request.CreateAttributeGroups, request.AttributeTags, request.AttributeMasks) ??
               PenumbraService.ValidateEstEntryRequests(request.EstEntries);
    }

    internal static string SizesExportRequestFingerprint(SizesExportRequest request)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            request.Schema,
            request.Version,
            request.VariantTarget,
            request.VariantGroupName,
            request.VariantTargetId,
            request.DefaultSize,
            Sizes = request.Sizes?.Select(size => new { size.Name, size.FilePath, size.Size, size.Sha256 }).ToArray(),
            request.BackupExisting,
            request.CreateAttributeGroups,
            request.AttributeTags,
            request.AttributeMasks,
            request.EstEntries,
        })));

    /// <summary>One hash over every size's file, for the export reservation, which records one file.</summary>
    internal static string SizesManifestHash(IReadOnlyList<SizeFileRequest> sizes)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n",
            sizes.Select(size => $"{size.Name}\t{size.FilePath}\t{size.Size}\t{size.Sha256}")))));
}
