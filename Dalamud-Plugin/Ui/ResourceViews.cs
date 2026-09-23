using InstantEdit.Models;
using InstantEdit.Services;

namespace InstantEdit.Ui;

/// <summary> An actor (or a browsed mod) and the resource roots the browser shows for it. </summary>
internal sealed record ActorView(
    OnScreenObject? Entity,
    string Category,
    string Name,
    List<ResourceView> Roots,
    int ImportObjectIndex)
{
    private Dictionary<ResourceSection, ResourceView[]>? _sectionRoots;

    /// <summary> The roots of one section in display order, computed once per view. </summary>
    public IReadOnlyList<ResourceView> RootsInSection(ResourceSection section)
    {
        _sectionRoots ??= Roots
            .GroupBy(root => root.SectionValue)
            .ToDictionary(group => group.Key, group => group.OrderBy(root => root.Order).ToArray());
        return _sectionRoots.TryGetValue(section, out var roots) ? roots : Array.Empty<ResourceView>();
    }
}

/// <summary> One resource row: a plugin-owned, immutable copy of a Penumbra resource node. </summary>
internal sealed record ResourceView(
    string Type,
    string Icon,
    string Name,
    string GamePath,
    string ActualPath,
    string SourceLabel,
    string SourceModName,
    string SourceModDirectory,
    string SourceModRootPath,
    string SourceRelativePath,
    Guid? SourceModStableId,
    ResourceSourceState SourceState,
    string Section,
    string Slot,
    int Order,
    string OptionMapping,
    IReadOnlyList<string> OptionMemberships,
    List<ResourceView> Children)
{
    // Classified once: the type filters and tree drawing query these every frame.
    // Children are complete when a view is constructed and are never mutated.
    public ResourceKinds Kinds { get; } = ResourceKindClassifier.Classify(Type, GamePath, ActualPath);
    public ResourceKinds DescendantKinds { get; } =
        Children.Aggregate(ResourceKinds.None, (kinds, child) => kinds | child.SubtreeKinds);
    public ResourceKinds SubtreeKinds => Kinds | DescendantKinds;

    /// <summary> The section this row belongs to; unknown labels fall into Other. </summary>
    public ResourceSection SectionValue { get; } =
        Enum.TryParse<ResourceSection>(Section, true, out var section) ? section : ResourceSection.Other;

    private ResourceView[]? _flattened;
    private string? _displayName;

    /// <summary> This view followed by all of its descendants, depth first. </summary>
    public IReadOnlyList<ResourceView> Flattened => _flattened ??= ResourceViews.Flatten(this).ToArray();

    /// <summary> The name shown in the browser: the resource name or, failing that, its file name. </summary>
    public string DisplayName => _displayName ??= ResourceViews.DisplayName(Name, ActualPath);

    public bool IsModel => ResourceViews.IsModel(this);
}

/// <summary> Pure helpers that build and classify resource views. No ImGui or Dalamud dependencies. </summary>
internal static class ResourceViews
{
    /// <summary> Converts a snapshot node (and its children) into browser rows. </summary>
    public static ResourceView FromNode(ResourceNode node)
    {
        var children = node.Children
            .Select(FromNode)
            .ToList();
        return new ResourceView(
            node.Type,
            node.Icon,
            node.Name,
            node.GamePath,
            node.ActualPath,
            node.SourceLabel,
            node.SourceModName ?? string.Empty,
            node.SourceModDirectory ?? string.Empty,
            node.SourceModRootPath ?? string.Empty,
            node.SourceRelativePath ?? string.Empty,
            node.SourceModStableId,
            node.SourceState,
            node.ResourceSection.ToString(),
            node.SlotLabel,
            node.SortOrder,
            string.Empty,
            Array.Empty<string>(),
            children);
    }

    /// <summary> The Mod Browser view of a scanned mod: one flat row per resource, sorted by game path. </summary>
    public static ActorView BuildModView(PenumbraModSnapshot snapshot, int importObjectIndex)
    {
        var roots = snapshot.Resources
            .Select(resource => new ResourceView(
                ResourceType(resource.GamePath),
                string.Empty,
                Path.GetFileName(resource.GamePath),
                resource.GamePath,
                resource.ActualPath,
                $"Loaded from: {snapshot.Name}",
                snapshot.Name,
                snapshot.Directory,
                snapshot.RootPath,
                resource.RelativePath,
                snapshot.StableId,
                ResourceSourceState.LoadedMod,
                ResourceSection.Other.ToString(),
                ResourceType(resource.GamePath),
                int.MaxValue,
                resource.OptionMapping,
                resource.OptionMemberships,
                new List<ResourceView>()))
            .OrderBy(resource => resource.GamePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ActorView(
            null,
            "Mod",
            snapshot.Name,
            roots,
            importObjectIndex);
    }

    public static string ResourceType(string gamePath)
        => Path.GetExtension(gamePath).ToLowerInvariant() switch
        {
            ".mdl" => "Model",
            ".tex" or ".atex" => "Texture",
            ".mtrl" => "Material",
            _ => "Resource",
        };

    /// <summary> The root followed by its descendants, depth first. </summary>
    public static IEnumerable<ResourceView> Flatten(ResourceView root)
    {
        var pending = new Stack<ResourceView>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            yield return node;
            for (var i = node.Children.Count - 1; i >= 0; i--)
                pending.Push(node.Children[i]);
        }
    }

    public static string KindLabel(string type) => string.IsNullOrWhiteSpace(type) ? "Resource" : type;

    public static string DisplayName(string name, string actualPath)
    {
        if (!string.IsNullOrWhiteSpace(name)) return name;
        return UiText.Safe(Path.GetFileName(actualPath), "Unnamed resource");
    }

    public static bool IsModel(ResourceView node)
        => node.Type.Contains("model", StringComparison.OrdinalIgnoreCase)
        || node.GamePath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)
        || node.ActualPath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase);

    /// <summary> Whether the Edit action may send this model to Blender. </summary>
    public static bool IsSafeModel(ResourceView node)
    {
        if (!IsModel(node) || !PenumbraService.IsSafeGamePath(node.GamePath) ||
            !node.GamePath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase) ||
            !node.ActualPath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
            return false;
        return node.SourceState switch
        {
            ResourceSourceState.LoadedMod => Path.IsPathRooted(node.ActualPath) &&
                                             !string.IsNullOrWhiteSpace(node.SourceModDirectory),
            ResourceSourceState.GameData => !Path.IsPathRooted(node.ActualPath) &&
                                            PenumbraService.IsSafeGamePath(node.ActualPath),
            _ => false,
        };
    }

    /// <summary> Maps a slot label and Penumbra icon name onto one of the armoury board icon keys. </summary>
    public static string NormalizeSlotIcon(string slot, string resourceIcon)
    {
        var value = $"{slot} {resourceIcon}".ToLowerInvariant();
        if (value.Contains("mainhand") || value.Contains("weapon")) return "Mainhand";
        if (value.Contains("offhand")) return "Offhand";
        if (value.Contains("head")) return "Head";
        if (value.Contains("body")) return "Body";
        if (value.Contains("hand")) return "Hands";
        if (value.Contains("leg")) return "Legs";
        if (value.Contains("feet") || value.Contains("foot")) return "Feet";
        if (value.Contains("earring") || value.Contains("ears")) return "Ears";
        if (value.Contains("neck")) return "Neck";
        if (value.Contains("bracelet") || value.Contains("wrist")) return "Wrists";
        if (value.Contains("ring") || value.Contains("finger")) return "Finger";
        return string.Empty;
    }
}
