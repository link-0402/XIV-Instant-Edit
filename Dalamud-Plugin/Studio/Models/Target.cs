using System;

namespace InstantEdit.Studio.Models;

/// <summary>Build mode's workflow stages, shown as tabs in the main window, in order.</summary>
internal enum StageId { Browse, Details, ModInfo, Build }

/// <summary>What Edit mode's centre shows: the mod at a glance, the entry picked on the left as a tree, or the game's items to add.</summary>
internal enum StudioView { Overview, Resources, Browse, Check }

/// <summary>What kind of object a <see cref="Target"/> points at.</summary>
internal enum TargetKind { Item, Model, Material, Texture, Build, Project, Resource, Option, Group }

/// <summary>
/// A reference to one object: the node a tree has selected, and the thing a validation issue or a
/// report row points at.
///
/// In Build mode, objects of the modpack are addressed by position: <see cref="Item"/> is the owning
/// item's key; <see cref="Index"/> is the model or material within it; <see cref="SubIndex"/> is only
/// used by textures, where <see cref="Index"/> is the owning material. <see cref="TargetKind.Build"/>
/// is the build itself.
///
/// In Edit mode, the mod's files are <see cref="TargetKind.Resource"/> targets: <see cref="Root"/>
/// names the left-pane entry they are listed under, <see cref="Path"/> the game path and
/// <see cref="Option"/> the option that carries it (empty for the default files). A resource target
/// with no path is the whole entry; one with no root (a row of an update's report) is found by its
/// path. <see cref="TargetKind.Project"/> is the mod itself (its name, description, tags).
///
/// Build mode's groups and options are <see cref="TargetKind.Group"/> (<see cref="Root"/> is the
/// group's id) and <see cref="TargetKind.Option"/> (<see cref="Option"/> is the place, see
/// <see cref="PackPlace"/>; empty for the default files).
/// </summary>
internal readonly record struct Target(TargetKind Kind, uint Item = 0, int Index = -1, int SubIndex = -1,
    string Root = "", string Path = "", string Option = "")
{
    /// <summary>The Build mode stage that shows it.</summary>
    public StageId Stage => Kind == TargetKind.Build ? StageId.Build : StageId.Details;

    /// <summary>The Edit mode view that shows it.</summary>
    public StudioView View => Kind == TargetKind.Project ? StudioView.Overview : StudioView.Resources;

    /// <summary>A resource target naming a whole left-pane entry rather than one file in it.</summary>
    public bool IsResourceRoot => Kind == TargetKind.Resource && string.IsNullOrEmpty(Path);

    /// <summary>Whether this target is <paramref name="node"/> itself or lies underneath it in the tree.</summary>
    public bool IsWithin(Target node) => node.Kind switch
    {
        TargetKind.Item     => Kind is TargetKind.Item or TargetKind.Model or TargetKind.Material or TargetKind.Texture && Item == node.Item,
        TargetKind.Material => Item == node.Item && Index == node.Index && Kind is TargetKind.Material or TargetKind.Texture,
        TargetKind.Resource when string.IsNullOrEmpty(node.Path)
                            => Kind == TargetKind.Resource && string.Equals(Root, node.Root, StringComparison.OrdinalIgnoreCase),
        _                   => this == node,
    };

    public static Target ForRoot(string rootId) => new(TargetKind.Resource, Root: rootId);

    public static Target ForResource(string rootId, string gamePath, string option)
        => new(TargetKind.Resource, Root: rootId, Path: gamePath, Option: option);

    /// <summary>Build mode: one option of the modpack's mod (or its default files, for an empty place).</summary>
    public static Target ForPackOption(string place) => new(TargetKind.Option, Option: place);

    /// <summary>Build mode: one group of the modpack's mod.</summary>
    public static Target ForPackGroup(Guid group) => new(TargetKind.Group, Root: group.ToString("D"));
}
