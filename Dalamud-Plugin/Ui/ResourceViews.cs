using InstantEdit.Models;
using InstantEdit.Services;
using InstantEdit.Services.PenumbraMods;

namespace InstantEdit.Ui;

/// <summary> An actor (or a browsed mod) and the resource roots the browser shows for it. </summary>
/// <param name="ModelRoots">The roots with each material still under the models that use it, when <paramref name="Roots"/> regroups them (<see cref="ResourceViews.GroupBodyFeatures"/>).</param>
internal sealed record ActorView(
    OnScreenObject? Entity,
    string Category,
    string Name,
    List<ResourceView> Roots,
    int ImportObjectIndex,
    List<ResourceView>? ModelRoots = null)
{
    /// <summary> The roots with every material under the models that use it. </summary>
    public List<ResourceView> RootsByModel => ModelRoots ?? Roots;

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

    /// <summary> The name shown in the browser: the resource name (plain for a sampler name) or, failing that, its file name. </summary>
    public string DisplayName => _displayName ??= ResourceViews.DisplayName(Name, ActualPath);

    private string? _labelId;

    /// <summary> The row's selectable label and id, "name##label", made once rather than every frame. </summary>
    public string LabelId => _labelId ??= $"{UiText.Safe(DisplayName, "Unnamed resource")}##label";

    public bool IsModel => ResourceViews.IsModel(this);

    private bool? _safeModel;

    /// <summary> Whether the Edit action may send this model to Blender; worked out once, as rows ask every frame. </summary>
    public bool SafeModel => _safeModel ??= ResourceViews.ComputeSafeModel(this);
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

    /// <summary> The type of a row that only groups other rows, such as <see cref="GroupBodyFeatures"/>'s. </summary>
    public const string GroupType = "Group";

    public const string BodyFeaturesLabel = "Body Features";

    // After Face, Hair, Ears and Tail (OnScreenService's ResourcePresentation).
    private const int BodyFeaturesOrder = 4;

    public static bool IsGroup(ResourceView node) => node.Type == GroupType;

    /// <summary> The material row the texture row sits under in the actor's tree, or null. </summary>
    public static ResourceView? ParentMaterial(ActorView actor, ResourceView texture)
    {
        foreach (var roots in new[] { actor.Roots, actor.RootsByModel })
            foreach (var root in roots)
                foreach (var row in root.Flattened)
                    if ((row.Kinds & ResourceKinds.Material) != 0 && row.Children.Any(child => ReferenceEquals(child, texture)))
                        return row;
        return null;
    }

    /// <summary>
    /// Whether the row is a material of the body (<c>chara/human/cNNNN/obj/body/bNNNN/material/…</c>): the
    /// skin, and body-mod materials such as pubes or piercings that gear names with a leading slash.
    /// </summary>
    public static bool IsBodyMaterial(ResourceView node)
        => (node.Kinds & ResourceKinds.Material) != 0 && BodyMaterialPath.IsMatch(node.GamePath.Replace('\\', '/'));

    private static readonly System.Text.RegularExpressions.Regex BodyMaterialPath = new(
        @"^chara/human/c\d{4}/obj/body/b\d{4}/material/",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Takes the body materials (<see cref="IsBodyMaterial"/>) out of every model and section and lists each
    /// once (by game path and file) under a "Body Features" row in Character features. Returns the roots
    /// unchanged when there are none.
    /// </summary>
    public static List<ResourceView> GroupBodyFeatures(List<ResourceView> roots)
    {
        var body = new List<ResourceView>();
        var seen = new HashSet<(string, string)>();
        var kept = new List<ResourceView>(roots.Count);
        foreach (var root in roots)
            if (WithoutBodyMaterials(root, body, seen) is { } rest)
                kept.Add(rest);
        if (body.Count == 0)
            return roots;

        kept.Add(new ResourceView(
            GroupType,
            string.Empty,
            BodyFeaturesLabel,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            null,
            ResourceSourceState.LoadedMod,
            ResourceSection.CharacterFeatures.ToString(),
            BodyFeaturesLabel,
            BodyFeaturesOrder,
            string.Empty,
            Array.Empty<string>(),
            body));
        return kept;
    }

    /// <summary> The row without its body materials, which go to <paramref name="body"/> once each; null when it is one. </summary>
    private static ResourceView? WithoutBodyMaterials(ResourceView node, List<ResourceView> body, HashSet<(string, string)> seen)
    {
        if (IsBodyMaterial(node))
        {
            if (seen.Add((node.GamePath.ToLowerInvariant(), node.ActualPath.ToLowerInvariant())))
                body.Add(node);
            return null;
        }
        if (!node.Children.Any(child => child.Flattened.Any(IsBodyMaterial)))
            return node;

        var children = new List<ResourceView>(node.Children.Count);
        foreach (var child in node.Children)
            if (WithoutBodyMaterials(child, body, seen) is { } rest)
                children.Add(rest);
        // Built anew rather than with `with`, which would keep the old children's kinds.
        return new ResourceView(node.Type, node.Icon, node.Name, node.GamePath, node.ActualPath, node.SourceLabel,
            node.SourceModName, node.SourceModDirectory, node.SourceModRootPath, node.SourceRelativePath, node.SourceModStableId,
            node.SourceState, node.Section, node.Slot, node.Order, node.OptionMapping, node.OptionMemberships, children);
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
        // Penumbra names a material's textures after their shader samplers (g_SamplerNormal); shown as "Normal".
        if (!string.IsNullOrWhiteSpace(name)) return TextureRoleClassifier.SamplerLabel(name) ?? name;
        return UiText.Safe(Path.GetFileName(actualPath), "Unnamed resource");
    }

    public static bool IsModel(ResourceView node)
        => node.Type.Contains("model", StringComparison.OrdinalIgnoreCase)
        || node.GamePath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)
        || node.ActualPath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase);

    /// <summary> Whether the Edit action may send this model to Blender. </summary>
    public static bool IsSafeModel(ResourceView node) => node.SafeModel;

    internal static bool ComputeSafeModel(ResourceView node)
    {
        if (!IsModel(node) || !ModPaths.IsSafeGamePath(node.GamePath) ||
            !node.GamePath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase) ||
            !node.ActualPath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
            return false;
        return node.SourceState switch
        {
            ResourceSourceState.LoadedMod => Path.IsPathRooted(node.ActualPath) &&
                                             !string.IsNullOrWhiteSpace(node.SourceModDirectory),
            ResourceSourceState.GameData => !Path.IsPathRooted(node.ActualPath) &&
                                            ModPaths.IsSafeGamePath(node.ActualPath),
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
