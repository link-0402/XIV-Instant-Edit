using System.Text.Json;
using System.Text.Json.Nodes;
using InstantEdit.Models;
using InstantEdit.Services;
using InstantEdit.Services.PenumbraMods;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// The context record the plugin sends Blender matches Tests/TestSupport/context-protocol.json, which
/// the add-on's bridge suite checks its field table against too.
/// </summary>
internal static class ContextProtocolScenarios
{
    public static void Run()
    {
        var protocol = Protocol();
        ContextCarriesEveryField(protocol);
        VersionsAgree(protocol);
        RepliesNameTheirStatus();
        MashupPassesOnlyBodyMaterialsThrough();
    }

    internal static JsonObject Protocol()
        => JsonNode.Parse(File.ReadAllText(Path.Combine(
            ComponentVersionScenarios.RepositoryRoot(), "Tests", "TestSupport", "context-protocol.json")))!.AsObject();

    internal static string[] Fields(JsonObject protocol)
        => protocol["fields"]!.AsArray().Select(field => field!.GetValue<string>()).ToArray();

    private static void ContextCarriesEveryField(JsonObject protocol)
    {
        using var registry = new ExportContextRegistry("context-protocol-regression");
        var context = registry.CreateGameContext(
            "chara/equipment/e0001/model/c0101e0001_top.mdl", "chara/equipment/e0001/model/c0101e0001_top.mdl", 0, 42428);
        // A reattach answer and a promoted context go out as the context serialized like this.
        var json = JsonNode.Parse(JsonSerializer.Serialize(context))!.AsObject();
        var missing = Fields(protocol).Where(field => !json.ContainsKey(field)).ToArray();
        Require(missing.Length == 0, $"a context sent to Blender carries every protocol field: {string.Join(", ", missing)}");
        Require(json["schema"]?.GetValue<string>() == protocol["schema"]!.GetValue<string>() &&
                json["version"]?.GetValue<int>() == protocol["currentVersion"]!.GetValue<int>(),
            "a context sent to Blender names the protocol's schema and current version");
    }

    private static void VersionsAgree(JsonObject protocol)
    {
        var versions = protocol["supportedVersions"]!.AsArray().Select(version => version!.GetValue<int>()).ToArray();
        Require(InstantEditImportContext.CurrentVersion == protocol["currentVersion"]!.GetValue<int>() &&
                InstantEditImportContext.SupportedVersions.SequenceEqual(versions),
            "the plugin supports the protocol's context versions");
        foreach (var version in versions.Prepend(0).Append(versions.Max() + 1))
        {
            var request = new ExportServer.ExportRequest
            {
                Schema = "instant-edit.export", Version = version, PluginInstanceId = "p", ContextId = "c", ExportId = "e",
                Capability = "k", FilePath = "C:/Temp/export.mdl", Size = 1, Sha256 = new string('a', 64),
            };
            var accepted = versions.Contains(version);
            Require(ExportServer.ValidateEnvelope(request) == (accepted ? null : ResultCode.UnsupportedVersion),
                $"an export envelope of version {version} is {(accepted ? "accepted" : "refused")}");
        }
    }

    private static void RepliesNameTheirStatus()
        => Require(ExportServer.ReasonPhrase(403) == "Forbidden" && ExportServer.ReasonPhrase(404) == "Not Found" &&
                   ExportServer.ReasonPhrase(202) == "Accepted" && ExportServer.ReasonPhrase(503) == "Service Unavailable" &&
                   ExportServer.ReasonPhrase(418) == "Request Failed",
            "the plugin's HTTP replies name their own status instead of calling the unlisted ones Not Found");

    private static void MashupPassesOnlyBodyMaterialsThrough()
    {
        const string top = "chara/equipment/e6106/material/v0001/mt_c0201e6106_top_a.mtrl";
        using var registry = new ExportContextRegistry("mashup-pass-through-regression");
        var context = registry.CreateGameContext("chara/equipment/e6106/model/c0201e6106_top.mdl",
            "chara/equipment/e6106/model/c0201e6106_top.mdl", 0, 42428, resourceManifest: new ResourceDependencyManifest
            {
                Materials =
                [
                    new MaterialDependency
                    {
                        ModelMaterial = "/mt_c0201e6106_top_a.mtrl", GamePath = top, Textures = [],
                        Resource = new SourceResourceLocator { Kind = "game", GamePath = top, Sha256 = new string('a', 64) },
                    },
                ],
            });
        MashupPlanResult Plan(string missing)
            => MashupPlanning.BuildMashupPlan(context, [new MashupContributor(context, ["/mt_c0201e6106_top_a.mtrl", missing])]);
        Require(Plan("/mt_c0201b0001_bibopube.mtrl") is { Success: true } body &&
                body.Assignments.Single(item => item.PassThrough).Alias == "/mt_c0201b0001_bibopube.mtrl",
            "a mashup passes a shared body material missing from the Context through");
        Require(Plan("/mt_c0201e5555_piercing.mtrl") is { Success: false, Code: ResultCode.MashupReimportRequired } gear &&
                gear.Message.Contains("absent from the captured dependency manifest", StringComparison.Ordinal),
            "a gear material named piercing missing from the Context still asks to re-import it");
    }
}
