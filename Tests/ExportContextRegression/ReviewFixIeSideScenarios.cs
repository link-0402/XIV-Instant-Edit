using System.Text.Json.Nodes;
using InstantEdit.Models;
using InstantEdit.Services;
using InstantEdit.Services.PenumbraMods;
using static InstantEdit.TestSupport.Assertions;

/// <summary>Review fixes (IeSide group): attribute groups of game-model exports whose resource capture failed.</summary>
internal static class ReviewFixIeSideScenarios
{
    public static void Run(string root)
    {
        root = Path.Combine(root, "review-fix-ieside");
        Directory.CreateDirectory(root);
        const string necklace = "chara/accessory/a0060/model/c0201a0060_nek.mdl";
        string[] tags = ["atr_nv_a", "atr_nv_b"];
        var masks = new Dictionary<string, int> { ["atr_nv_a"] = 1, ["atr_nv_b"] = 2 };
        // a0060 has four variants with materials 1 to 4.
        var game = new[] { 1, 1, 2, 3, 4 }.Select(material => new JsonObject
        {
            ["MaterialId"] = material, ["DecalId"] = 0, ["VfxId"] = 0, ["MaterialAnimationId"] = 0, ["AttributeMask"] = 1023, ["SoundId"] = 0,
        }).ToArray();

        // fixes-1: a game model's context with a failed capture has no mod entries to keep.
        var gameContext = Context(InstantEditImportContext.GameSource, necklace);
        var gameEntries = AttributeGroupService.KnownManipulations(gameContext);
        Require(gameContext.ResourceManifest is null && gameEntries is { Count: 0 },
            "review fix ieside: a game model's context without a capture has no unknown IMC entries");
        var gameMod = Folder(root, "GameNecklace");
        Require(AttributeGroupService.WriteAttributeGroupsForRegression(gameMod, necklace, tags, masks, gameEntries, game) is null &&
                ImcGroup(gameMod) is { } group && group["OnlyAttributes"]!.GetValue<bool>(),
            "review fix ieside: so its part toggles get their IMC group on every export path, as the vanilla Quick Export does");

        // A promoted game context (its mod made by the first export) still counts as a game model.
        var promoted = gameContext with
        {
            DestinationState = InstantEditImportContext.ReadyDestination, SourceModDirectory = "GameNecklace",
        };
        Require(AttributeGroupService.KnownManipulations(promoted) is { Count: 0 },
            "review fix ieside: the same holds for Sizes and Race exports into the mod the first export made");

        // A mod's context whose capture failed keeps the refusal: its own entries are unknown.
        var modContext = Context(InstantEditImportContext.ModSource, necklace);
        Require(AttributeGroupService.KnownManipulations(modContext) is null &&
                AttributeGroupService.WriteAttributeGroupsForRegression(Folder(root, "ModNecklace"), necklace, tags, masks,
                        AttributeGroupService.KnownManipulations(modContext), game)?
                    .StartsWith("attribute_group_imc_unknown:", StringComparison.Ordinal) == true,
            "review fix ieside: a mod whose import didn't read its IMC entries still gets no IMC group");

        // A capture that worked is used as it is, for either kind.
        var captured = new JsonArray(new JsonObject { ["Type"] = "Atr" });
        var manifest = new ResourceDependencyManifest { Materials = [], Manipulations = captured };
        Require(ReferenceEquals(AttributeGroupService.KnownManipulations(modContext with { ResourceManifest = manifest }), captured) &&
                ReferenceEquals(AttributeGroupService.KnownManipulations(gameContext with { ResourceManifest = manifest }), captured),
            "review fix ieside: a captured manifest's entries are used as captured");
    }

    private static InstantEditImportContext Context(string kind, string gamePath) => new()
    {
        PluginInstanceId = "test", ContextId = Guid.NewGuid().ToString("N"), ImportId = Guid.NewGuid().ToString("N"),
        Capability = "test", GamePath = gamePath, ResolvedGamePath = gamePath, ObjectIndex = 0, CallbackPort = 1,
        SourceKind = kind,
        DestinationState = kind == InstantEditImportContext.GameSource
            ? InstantEditImportContext.NewModRequiredDestination
            : InstantEditImportContext.ReadyDestination,
    };

    private static string Folder(string root, string name)
    {
        var folder = Path.Combine(root, name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "meta.json"), new JsonObject
        {
            ["FileVersion"] = 4, ["Identifier"] = Guid.NewGuid(), ["LastWrite"] = DateTime.UtcNow.AddMinutes(-1),
            ["Name"] = name, ["DefaultData"] = null, ["Groups"] = null,
        }.ToJsonString());
        return folder;
    }

    private static JsonObject? ImcGroup(string folder)
        => JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "meta.json")))!["Groups"]?.AsArray()
            .OfType<JsonObject>().SingleOrDefault(group => group["Type"]?.GetValue<string>() == "Imc");
}
