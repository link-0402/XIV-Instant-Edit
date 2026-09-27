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
    private string? _header;
    private string? _summary;

    /// <summary> "Player  ·  Name" (or just the category when the actor has no name). </summary>
    public string Header => _header ??= string.IsNullOrWhiteSpace(Name) ? Category : $"{Category}  ·  {Name}";

    /// <summary> "3 models · 12 textures · 5 materials" over every row under this actor; empty when there are none. </summary>
    public string Summary => _summary ??= BuildSummary();

    private string BuildSummary()
    {
        int models = 0, textures = 0, materials = 0, animations = 0;
        foreach (var root in Roots)
            foreach (var node in root.Flattened)
            {
                if ((node.Kinds & ResourceKinds.Model) != 0) models++;
                if ((node.Kinds & ResourceKinds.Texture) != 0) textures++;
                if ((node.Kinds & ResourceKinds.Material) != 0) materials++;
                if ((node.Kinds & ResourceKinds.Animation) != 0) animations++;
            }

        var parts = new List<string>(4);
        if (models > 0) parts.Add(models == 1 ? "1 model" : $"{models} models");
        if (textures > 0) parts.Add(textures == 1 ? "1 texture" : $"{textures} textures");
        if (materials > 0) parts.Add(materials == 1 ? "1 material" : $"{materials} materials");
        if (animations > 0) parts.Add(animations == 1 ? "1 animation" : $"{animations} animations");
        return string.Join(" · ", parts);
    }

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
    private TextureRole? _textureRole;

    /// <summary> What a texture row is used for; <see cref="TextureRole.None"/> for other rows. </summary>
    public TextureRole TextureRole => _textureRole ??= TextureRoleClassifier.Classify(Kinds, Name, GamePath, ActualPath);

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

    /// <summary> A Game Files row: a vanilla file read from the game's own data, with no actor or mod behind it. </summary>
    public static ResourceView GameFile(string gamePath, string name)
        => new(
            ResourceType(gamePath),
            string.Empty,
            name,
            gamePath,
            gamePath,
            "Game Data",
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            null,
            ResourceSourceState.GameData,
            ResourceSection.Other.ToString(),
            ResourceType(gamePath),
            0,
            string.Empty,
            Array.Empty<string>(),
            new List<ResourceView>());

    public static string ResourceType(string gamePath)
        => Path.GetExtension(gamePath).ToLowerInvariant() switch
        {
            ".mdl" => "Model",
            ".tex" or ".atex" => "Texture",
            ".mtrl" => "Material",
            ".pap" => "Animation",
            _ => "Resource",
        };

    /// <summary> Whether the row is a character animation pack that can be sent to Blender. </summary>
    public static bool IsAnimation(ResourceView node) => (node.Kinds & ResourceKinds.Animation) != 0;

    /// <summary>
    /// An Animations tab row for an animation the listener detected: its file and where it was
    /// loaded from. Penumbra's resource tree does not report character animations.
    /// </summary>
    public static ResourceView FromAnimation(AnimationCapture capture, bool startup, int order)
    {
        var clip = startup && capture.Startup is { } linked ? linked : capture.Clip;
        var source = capture.Sources.FirstOrDefault(s => s.GamePath == clip.GamePath);
        var mod = source?.ModName ?? source?.ModDirectory;
        return new ResourceView(
            "Animation",
            string.Empty,
            Services.Animations.AnimationPresentation.AnimationName(capture, startup),
            clip.GamePath,
            source?.ResolvedPath ?? clip.GamePath,
            mod is null ? "Game Data" : $"Loaded from: {mod}",
            mod ?? string.Empty,
            source?.ModDirectory ?? string.Empty,
            source?.ModRoot ?? string.Empty,
            source?.RelativePath ?? string.Empty,
            null,
            string.IsNullOrEmpty(source?.ModDirectory) ? ResourceSourceState.GameData : ResourceSourceState.LoadedMod,
            ResourceSection.Other.ToString(),
            "Animation",
            order,
            string.Empty,
            Array.Empty<string>(),
            new List<ResourceView>());
    }

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
