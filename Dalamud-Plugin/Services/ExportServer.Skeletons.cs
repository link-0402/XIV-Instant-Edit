using System.Text.Json.Serialization;
using InstantEdit.Services.Skeletons;

namespace InstantEdit.Services;

public sealed partial class ExportServer
{
    public const string SkeletonCapability = "instant-edit.skeleton.v1";

    private sealed class SkeletonRequest
    {
        [JsonPropertyName("schema")]
        public string? Schema { get; set; }

        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("modelPath")]
        public string? ModelPath { get; set; }
    }

    /// <summary>Game skeletons for the Blender add-on's imports of model files from disk.</summary>
    internal ModelSkeletonResolver? Skeletons { get; set; }

    /// <summary>
    /// The game skeleton of a model named like the game's (a game path or its file name), as the
    /// local player's collection resolves it. Read-only: it needs no import context.
    /// </summary>
    private async Task<(int Status, string Body)> HandleSkeletonAsync(HttpRequest request)
    {
        var parsed = DeserializeRequest<SkeletonRequest>(request.Body, "skeleton", out var parseError);
        if (parseError is not null)
            return parseError.Value;
        if (parsed!.Schema != "instant-edit.skeleton-request" || parsed.Version != 1)
            return Error(400, "unsupported_schema", "unsupported skeleton request schema or version");
        if (ModelSkeletonPaths.Parse(parsed.ModelPath) is null)
            return Error(400, "invalid_model_path", "the file isn't named like a game model");
        var resolver = Skeletons;
        if (resolver is null)
            return Error(503, "skeletons_unavailable", "the plugin cannot read game skeletons");

        // Blender stops waiting after 8 seconds.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(7));
        ModelSkeletonResult result;
        try
        {
            result = await resolver.ResolveForPlayerAsync(parsed.ModelPath!, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return Error(503, "skeleton_timeout", "the game did not read the skeleton in time");
        }
        if (result.Skeleton is null)
            return Error(404, "skeleton_not_found", result.Problem ?? "no game skeleton was found for this model");
        return (200, Json(new { ok = true, skeleton = result.Skeleton.ToPayload() }));
    }
}
