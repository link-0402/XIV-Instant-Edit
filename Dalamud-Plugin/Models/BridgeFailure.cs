using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace InstantEdit.Models;

/// <summary>Structured failure exchanged across the local Blender/Dalamud bridge.</summary>
public sealed record BridgeFailure
{
    [JsonPropertyName("component")]
    public required string Component { get; init; }

    [JsonPropertyName("operation")]
    public required string Operation { get; init; }

    [JsonPropertyName("stage")]
    public required string Stage { get; init; }

    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("cause")]
    public required string Cause { get; init; }

    [JsonPropertyName("remedy")]
    public required string Remedy { get; init; }

    [JsonPropertyName("diagnosticId")]
    public required string DiagnosticId { get; init; }

    [JsonIgnore]
    public int? HttpStatus { get; init; }

    [JsonIgnore]
    public string ShortDiagnosticId => DiagnosticId[..Math.Min(8, DiagnosticId.Length)];

    [JsonIgnore]
    public string UserMessage
    {
        get
        {
            var component = Component == "blender_addon" ? "Blender" : "XIV Instant Edit";
            var operation = Operation.Replace('_', ' ');
            var stage = Stage.Replace('_', ' ');
            if (Component == "blender_addon" && Operation == "import" &&
                Stage is "import_processing" or "queueing")
                return $"Blender finished receiving the model, but the import failed during {stage}: " +
                       $"{Cause} {Remedy} Diagnostic ID: {ShortDiagnosticId}.";
            return $"{component} {operation} failed during {stage}: {Cause} {Remedy} " +
                   $"Diagnostic ID: {ShortDiagnosticId}.";
        }
    }

    public static BridgeFailure Create(
        string component,
        string operation,
        string stage,
        string code,
        string cause,
        string remedy,
        int? httpStatus = null,
        string? diagnosticId = null)
        => new()
        {
            Component = Safe(component, 64),
            Operation = Safe(operation, 64),
            Stage = Safe(stage, 64),
            Code = Safe(code, 128),
            Cause = Safe(cause, 2048),
            Remedy = Safe(remedy, 2048),
            DiagnosticId = Guid.TryParse(diagnosticId, out var parsedDiagnosticId)
                ? parsedDiagnosticId.ToString("N")
                : Guid.NewGuid().ToString("N"),
            HttpStatus = httpStatus,
        };

    public static BridgeFailure FromResponse(HttpStatusCode status, string body, string operation)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                var component = Text(root, "component");
                var responseOperation = Text(root, "operation");
                var stage = Text(root, "stage");
                var code = Text(root, "code");
                var cause = Text(root, "cause") ?? Text(root, "error") ?? Text(root, "message");
                var remedy = Text(root, "remedy");
                var diagnosticId = Text(root, "diagnosticId");
                if (component is not null && stage is not null && code is not null && cause is not null &&
                    remedy is not null)
                    return Create(
                        component,
                        responseOperation ?? operation,
                        stage,
                        code,
                        cause,
                        remedy,
                        (int)status,
                        diagnosticId);

                if (cause is not null)
                    return Create(
                        "blender_addon",
                        operation,
                        "response_handling",
                        code ?? "legacy_http_error",
                        cause,
                        "Update and restart the XIV Instant Edit Blender add-on, then retry.",
                        (int)status,
                        diagnosticId);
            }
        }
        catch (JsonException)
        {
            // Older add-ons may return plain text or malformed JSON.
        }

        return Create(
            "blender_addon",
            operation,
            "response_handling",
            "legacy_http_error",
            "Blender rejected the request without providing diagnostic details.",
            "Update and restart the XIV Instant Edit Blender add-on, then retry.",
            (int)status);
    }

    public static string Safe(string? value, int maxLength = 4096)
    {
        var text = value ?? string.Empty;
        text = Regex.Replace(text, @"(?i)(?:[a-z]:[\\/]|\\\\)[^\r\n\""'<>|]*", "<local-path>");
        text = Regex.Replace(text, @"(?<![A-Za-z0-9_.-])/(?:[^\s\""'<>|/]+/)+[^\s\""'<>|]*", "<local-path>");
        text = Regex.Replace(
            text,
            "(?i)(\\\"(?:capability|token|secret|pluginInstanceId)\\\"\\s*:\\s*\\\")[^\\\"]*(\\\")",
            "$1<redacted>$2");
        return text[..Math.Min(text.Length, maxLength)];
    }

    private static string? Text(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
           !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;
}

public sealed class BlenderBridgeException : Exception
{
    public BlenderBridgeException(BridgeFailure failure, string? rawResponse = null, Exception? inner = null)
        : base(failure.UserMessage, inner)
    {
        Failure = failure;
        RawResponse = BridgeFailure.Safe(rawResponse, 8192);
    }

    public BridgeFailure Failure { get; }
    public string RawResponse { get; }
    public int? HttpStatus => Failure.HttpStatus;
}
