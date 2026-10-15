using System.Text.Json.Nodes;
using InstantEdit.Services.Physics;

namespace InstantEdit.Ui;

internal sealed partial class PhysicsWindow
{
    /// <summary>
    /// What the builder shows and does right now, for the XIV Bridge test tools: the window, the
    /// character it found, the document, the message line, the automatic preview and the preview
    /// itself. <paramref name="detail"/> adds the document's simulators ("areas"), or those and
    /// their chains, connectors and collision bits ("full"). Framework thread only, like drawing.
    /// </summary>
    internal JsonObject TestState(string detail)
    {
        var state = new JsonObject
        {
            ["open"] = IsOpen,
            ["busy"] = Busy,
            ["finding"] = finding != null,
            ["findProblem"] = findProblem,
            ["loading"] = loading != null,
            ["working"] = working != null,
            ["fitting"] = fitting != null || refitting != null,
            ["fitTipAt"] = TipAt.ToString(),
            ["message"] = message,
            ["messageSeverity"] = messageSeverity.ToString(),
            ["autoPreview"] = config.PhysicsAutoPreview,
            ["autoPreviewPendingMs"] = autoPreviewDue == 0 ? null : Math.Max(0, autoPreviewDue - Environment.TickCount64),
            ["preview"] = new JsonObject
            {
                ["active"] = service.Preview.Active,
                ["busy"] = service.Preview.Busy,
                ["shared"] = service.Preview.Shared,
                ["collection"] = service.Preview.CollectionName,
            },
            ["mods"] = new JsonArray(config.PhysicsMods.Select(mod => (JsonNode)new JsonObject
            {
                ["directory"] = mod.Directory,
                ["identifier"] = mod.Identifier.ToString(),
            }).ToArray()),
            ["saveName"] = saveName,
            ["moving"] = Moving ? playback.Timeline.ToString() : null,
            ["overlay"] = OverlayWanted,
            ["overlayShown"] = config.PhysicsShowOverlay,
            ["overlayParts"] = ((PhysicsOverlayShown)config.PhysicsOverlayShown).ToString(),
            ["overlayHover"] = overlayLit.Part?.ToString(),
            ["overlayHoverShape"] = overlayLit.Shape,
        };
        if (target is { } found)
        {
            state["target"] = new JsonObject
            {
                ["objectIndex"] = found.Character.ObjectIndex,
                ["name"] = found.Character.Name,
                ["race"] = found.Character.Race,
                ["skeletonFamily"] = found.Character.Skeleton.Family,
                ["bones"] = found.Character.Skeleton.Count,
                ["collection"] = found.CollectionName,
                ["physics"] = Loaded(found.Physics),
                ["skeleton"] = Loaded(found.Skeleton),
            };
        }
        if (document is { } current)
        {
            var errors = Issues(current);
            state["document"] = new JsonObject
            {
                ["source"] = current.Source.Kind.ToString(),
                ["sourceLabel"] = current.Source.Label,
                ["sourcePath"] = current.Source.Path,
                ["sourceMod"] = current.Source.Mod,
                ["race"] = current.Race,
                ["dirty"] = current.Dirty,
                ["revision"] = current.Revision,
                ["canUndo"] = current.CanUndo,
                ["canRedo"] = current.CanRedo,
                ["selected"] = selected,
                ["simulators"] = current.File.Simulators.Count,
                ["collisionShapes"] = new JsonArray(current.File.Collision.Names.Select(name => (JsonNode)name.Text).ToArray()),
                ["issues"] = new JsonArray(errors.Select(issue => (JsonNode)new JsonObject
                {
                    ["level"] = issue.Level.ToString(),
                    ["simulator"] = issue.Simulator,
                    ["text"] = issue.Text,
                }).ToArray()),
            };
            if (detail is "areas" or "full")
                state["document"]!["areas"] = Areas(current.File, detail == "full");
        }
        return state;
    }

    private static JsonObject Loaded(PhysicsLoadedFile file) => new()
    {
        ["gamePath"] = file.GamePath,
        ["resolvedPath"] = file.ResolvedPath,
        ["label"] = file.Label,
        ["mine"] = file.Mine,
        ["mod"] = file.Mod,
        ["preview"] = file.Preview,
    };

    private static JsonArray Areas(PhybFile file, bool full)
    {
        var labels = PhysicsAreas.Labels(file);
        var areas = new JsonArray();
        for (var index = 0; index < file.Simulators.Count; index++)
        {
            var simulator = file.Simulators[index];
            var shapes = simulator.Collisions.Select(reference => reference.Name.Text).ToArray();
            var area = new JsonObject
            {
                ["index"] = index,
                ["label"] = index < labels.Count ? labels[index] : null,
                ["group"] = simulator.Group,
                ["excluded"] = simulator.Excluded,
                ["flags"] = simulator.Flags.ToString(),
                ["rootBones"] = new JsonArray(PhysicsAreas.RootBones(simulator).Select(bone => (JsonNode)bone).ToArray()),
                ["collisions"] = new JsonArray(simulator.Collisions.Select(reference => (JsonNode)$"{reference.Name.Text} ({reference.Type})").ToArray()),
                ["chains"] = simulator.Chains.Count,
                ["connectors"] = simulator.Connectors.Count,
                ["springs"] = simulator.Springs.Count,
                ["attracts"] = simulator.Attracts.Count,
                ["pins"] = simulator.Pins.Count,
            };
            if (full)
            {
                area["chainList"] = new JsonArray(simulator.Chains.Select((chain, number) => (JsonNode)new JsonObject
                {
                    ["index"] = number,
                    ["bones"] = new JsonArray(chain.Nodes.Select(node => (JsonNode)node.Bone.Text).ToArray()),
                    ["type"] = chain.Type,
                    ["dampening"] = chain.Dampening,
                    ["collisions"] = new JsonArray(chain.Collisions.Select(reference => (JsonNode)reference.Name.Text).ToArray()),
                    // A node's bits name the simulator's shapes, then the chain's own.
                    ["nodeCollisions"] = new JsonArray(chain.Nodes.Select(node => (JsonNode)Bits(node.CollisionFlags,
                        PhysicsCollisions.BitShapes(simulator, chain).Select(reference => reference.Name.Text).ToArray())).ToArray()),
                }).ToArray());
                area["connectorList"] = new JsonArray(simulator.Connectors.Select(connector => (JsonNode)new JsonObject
                {
                    ["chain1"] = connector.Chain1,
                    ["node1"] = connector.Node1,
                    ["chain2"] = connector.Chain2,
                    ["node2"] = connector.Node2,
                    ["collisionFlags"] = $"0x{connector.CollisionFlags:X}",
                    ["collidesWith"] = Bits(connector.CollisionFlags, shapes),
                    ["continuousFlags"] = $"0x{connector.ContinuousCollisionFlags:X}",
                }).ToArray());
                area["springList"] = new JsonArray(simulator.Springs.Select(spring => (JsonNode)new JsonObject
                {
                    ["chain1"] = spring.Chain1,
                    ["node1"] = spring.Node1,
                    ["chain2"] = spring.Chain2,
                    ["node2"] = spring.Node2,
                    ["stretch"] = spring.StretchStiffness,
                    ["compress"] = spring.CompressStiffness,
                }).ToArray());
            }
            areas.Add(area);
        }
        return areas;
    }

    /// <summary> The shapes a collision bit field names, bit n naming <paramref name="shapes"/>[n]; bits past them name nothing and show as #n. </summary>
    private static string Bits(uint flags, IReadOnlyList<string> shapes)
        => string.Join(", ", Enumerable.Range(0, 32).Where(bit => (flags & (1u << bit)) != 0)
            .Select(bit => bit < shapes.Count ? shapes[bit] : $"#{bit}"));
}
