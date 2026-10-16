using InstantEdit.Services;
using InstantEdit.Services.PenumbraMods;
using Penumbra.Api.Enums;
using Penumbra.Api.Helpers;
using static InstantEdit.TestSupport.Assertions;

/// <summary>
/// On Screen asks Penumbra for trees without UI names (a few milliseconds) and names them from the
/// same tree seen before; only a new tree is asked for again with names (about 150 ms on the
/// framework thread). A tree that changed in any path, type or place must not take old names, since
/// an item's name follows its variant, which only the materials' paths tell apart.
/// </summary>
internal static class ResourceUiNamesScenarios
{
    public static void Run()
    {
        var names = new ResourceUiNames();
        Check(names.Named(Tree("v0001", withNames: false)) is null, "a tree not seen before has no names");

        names.Learn(Tree("v0001", withNames: true));
        var filled = names.Named(Tree("v0001", withNames: false));
        Check(filled is not null, "the same tree without names is named");
        Check(filled!.Name == "Player" && filled.RaceCode == 101, "the tree's own fields are kept");
        var model = filled.Nodes[0];
        Check(model.Name == "Shirt (v0001)" && model.Icon == ChangedItemIcon.Body, "a model takes its name and icon");
        Check(model.Children[0].Name == "Material #0" && model.Children[0].Children[0].Name == "g_SamplerNormal",
            "children take their names in order");
        Check(model.ObjectAddress == 0x1234 && model.Children[0].Children[0].ActualPath == "C:/mods/shirt_n.tex",
            "paths and addresses come from the tree asked for, not the one remembered");

        Check(names.Named(Tree("v0002", withNames: false)) is null, "another variant's materials are a new tree");
        var moved = Tree("v0001", withNames: false);
        moved.Nodes[0].Children.Add(Node(ResourceType.Mtrl, "extra.mtrl", "extra.mtrl", null, []));
        Check(names.Named(moved) is null, "a tree with another node is a new tree");

        // Only the most recent trees are kept.
        for (var i = 0; i < 20; i++)
            names.Learn(Tree($"v{i + 10:D4}", withNames: true));
        Check(names.Named(Tree("v0001", withNames: false)) is null, "old trees are forgotten past the capacity");
        Check(names.Named(Tree("v0029", withNames: false)) is not null, "recent trees are kept");

        Check(ModPaths.IsSafeGamePath("chara/equipment/e0001/model/c0101e0001_top.mdl"), "a game model path is safe");
        Check(!ModPaths.IsSafeGamePath("chara/equipment//c0101e0001_top.mdl"), "an empty segment is not safe");
        Check(!ModPaths.IsSafeGamePath("chara/../c0101e0001_top.mdl"), "a parent segment is not safe");
        Check(!ModPaths.IsSafeGamePath("chara/e0001/c0101e0001_top.tex"), "another extension is not safe");
        Check(!ModPaths.IsSafeGamePath("chara/e0001/c0101e0001:top.mdl"), "an invalid file name character is not safe");
        Check(!ModPaths.IsSafeGamePath("chara\\e0001\\c0101e0001_top.mdl"), "a backslash is not safe");
    }

    private static ResourceTreeDto Tree(string variant, bool withNames)
        => new()
        {
            Name = "Player",
            RaceCode = 101,
            Nodes =
            [
                Node(ResourceType.Mdl, "chara/equipment/e0001/model/c0101e0001_top.mdl", "C:/mods/shirt.mdl",
                    withNames ? $"Shirt ({variant})" : null,
                    [
                        Node(ResourceType.Mtrl, $"chara/equipment/e0001/material/{variant}/mt_c0101e0001_top_a.mtrl", "C:/mods/shirt.mtrl",
                            withNames ? "Material #0" : null,
                            [Node(ResourceType.Tex, "chara/equipment/e0001/texture/c0101e0001_top_n.tex", "C:/mods/shirt_n.tex",
                                withNames ? "g_SamplerNormal" : null, [])]),
                    ],
                    withNames ? ChangedItemIcon.Body : default, 0x1234),
            ],
        };

    private static ResourceNodeDto Node(ResourceType type, string gamePath, string actualPath, string? name, List<ResourceNodeDto> children,
        ChangedItemIcon icon = default, nint address = 0)
        => new()
        {
            Type = type,
            Icon = icon,
            Name = name,
            GamePath = gamePath,
            ActualPath = actualPath,
            ObjectAddress = address,
            ResourceHandle = 0,
            Children = children,
        };
}
