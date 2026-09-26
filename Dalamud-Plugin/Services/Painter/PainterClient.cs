using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace InstantEdit.Services.Painter;

public enum PainterConnectionState
{
    Offline,
    Online,
    VersionMismatch,
}

public sealed record PainterStatus(bool Reachable, string AddonVersion, string PainterVersion, bool ProjectOpen, string JobId, bool SettingUp)
{
    public static readonly PainterStatus Offline = new(false, "", "", false, "", false);

    public PainterConnectionState Classify(string expectedPluginVersion)
    {
        if (!Reachable)
            return PainterConnectionState.Offline;
        var expected = BlenderClient.NormalizeVersion(expectedPluginVersion);
        return expected.Length > 0 && BlenderClient.NormalizeVersion(AddonVersion) == expected
            ? PainterConnectionState.Online
            : PainterConnectionState.VersionMismatch;
    }
}

/// <summary> Talks to the XIV Instant Edit plugin's listener inside Substance Painter. </summary>
public sealed class PainterClient : IDisposable
{
    public const string OpenSchema = "instant-edit.painter-open";
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public async Task<PainterStatus> GetStatusAsync(int port, TimeSpan timeout, CancellationToken token = default)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        cancel.CancelAfter(timeout);
        try
        {
            using var response = await _http.GetAsync($"http://127.0.0.1:{port}/status", cancel.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return PainterStatus.Offline;
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancel.Token).ConfigureAwait(false)) as JsonObject;
            if (body is null || body["ok"]?.GetValue<bool>() != true)
                return PainterStatus.Offline;
            return new PainterStatus(true, Text(body, "addonVersion"), Text(body, "painterVersion"),
                Flag(body, "projectOpen"), Text(body, "jobId"), Flag(body, "settingUp"));
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException)
        {
            return PainterStatus.Offline;
        }
    }

    /// <summary> Asks Painter to open a job; returns an empty string when it was queued, otherwise Painter's reason. </summary>
    public async Task<string> OpenAsync(int port, Guid jobId, string manifestPath, CancellationToken token = default)
    {
        var payload = JsonSerializer.Serialize(new
        {
            schema = OpenSchema,
            version = 1,
            jobId = jobId.ToString("N"),
            manifestPath,
        });
        try
        {
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync($"http://127.0.0.1:{port}/open", content, token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
                return "";
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false)) as JsonObject;
            var message = body?["message"]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(message) ? $"Painter refused the project ({(int)response.StatusCode})." : message;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException)
        {
            return "Substance Painter is not reachable. Start it with the XIV Instant Edit plugin enabled.";
        }
    }

    private static string Text(JsonObject body, string name)
        => body[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    private static bool Flag(JsonObject body, string name)
        => body[name] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    public void Dispose() => _http.Dispose();
}
