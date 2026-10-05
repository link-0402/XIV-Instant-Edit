using System.Text.Json.Serialization;
using InstantEdit.Services.GameFiles;
using InstantEdit.Services.Skeletons;

namespace InstantEdit.Services;

public sealed partial class ExportServer
{
    public const string RacialDeformerCapability = "instant-edit.racial-deformer.v1";

    private sealed class RacialDeformerRequest
    {
        [JsonPropertyName("schema")]
        public string? Schema { get; set; }

        [JsonPropertyName("version")]
        public int Version { get; set; }

        /// <summary> The race the model is made for, like 201. </summary>
        [JsonPropertyName("modelRace")]
        public int ModelRace { get; set; }

        /// <summary> The race to reshape it for; 0 for the local player's. </summary>
        [JsonPropertyName("race")]
        public int Race { get; set; }
    }

    /// <summary>
    /// The racial deformer matrices for the Blender add-on's Apply Racial Scaling tool: each bone
    /// that moves a model of one race onto another's shape, in game space (3×4 rows, translation
    /// last), from the local player's collection. Read-only: it needs no import context, and what
    /// Blender does with the shape is its own (unlike scaled imports, nothing here refuses exports).
    /// </summary>
    private async Task<(int Status, string Body)> HandleRacialDeformerAsync(HttpRequest request)
    {
        var parsed = DeserializeRequest<RacialDeformerRequest>(request.Body, "racial deformer", out var parseError);
        if (parseError is not null)
            return parseError.Value;
        if (parsed!.Schema != "instant-edit.racial-deformer-request" || parsed.Version != 1)
            return Error(400, "unsupported_schema", "unsupported racial deformer request schema or version");
        if (parsed.ModelRace is < 1 or > ushort.MaxValue || GameRaces.Find((ushort)parsed.ModelRace) is null)
            return Error(400, "invalid_race", "the model's race is not a known race");
        if (parsed.Race != 0 && (parsed.Race is < 1 or > ushort.MaxValue || GameRaces.Find((ushort)parsed.Race) is null))
            return Error(400, "invalid_race", "the target race is not a known race");
        var resolver = Skeletons;
        if (resolver is null)
            return Error(503, "skeletons_unavailable", "the plugin cannot read game files");

        // Blender stops waiting after 8 seconds.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(7));
        try
        {
            var (deformer, race, problem) = await resolver.RacialDeformerForPlayerAsync(
                (ushort)parsed.ModelRace, parsed.Race == 0 ? null : (ushort)parsed.Race, timeout.Token).ConfigureAwait(false);
            if (deformer is null)
                return Error(422, "racial_scaling_unavailable", problem ?? "the racial deformer could not be read");
            return (200, RacialDeformerJson(parsed.ModelRace, race, deformer));
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return Error(503, "racial_deformer_timeout", "the game did not read the racial deformer in time");
        }
    }

    /// <summary> The endpoint's answer: each bone's matrix as 12 numbers, rows X, Y, Z with the translation last in each. </summary>
    internal static string RacialDeformerJson(int modelRace, ushort race, RacialDeformer deformer)
    {
        var bones = deformer.Matrices
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new
            {
                name = pair.Key,
                matrix = new[]
                {
                    pair.Value.X.X, pair.Value.X.Y, pair.Value.X.Z, pair.Value.X.W,
                    pair.Value.Y.X, pair.Value.Y.Y, pair.Value.Y.Z, pair.Value.Y.W,
                    pair.Value.Z.X, pair.Value.Z.Y, pair.Value.Z.Z, pair.Value.Z.W,
                },
            })
            .ToArray();
        return Json(new { ok = true, modelRace, race = (int)race, raceLabel = GameRaces.Label(race), bones });
    }
}
