using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using InstantEdit.Studio.Models.Project;
using InstantEdit.Studio.Services.Project;
using InstantEdit.Studio.Services.Resources;

namespace InstantEdit.Studio.Models.Resources;

/// <summary>Where the file a node stands for comes from.</summary>
internal enum OriginKind
{
    /// <summary>A file inside the mod folder, carried by one of its options.</summary>
    ModFile,

    /// <summary>A file swap: the game path is served by another game path.</summary>
    Swap,

    /// <summary>The game's own file: the mod references it and does not replace it.</summary>
    Vanilla,

    /// <summary>A file being added to the mod from a local file (nothing served this path before).</summary>
    Added,

    /// <summary>Referenced, but neither the mod nor the game has it.</summary>
    Missing,

    /// <summary>A common material (skin, pubes, piercings) neither the mod nor the game has: the player's body mod provides it.</summary>
    Shared,
}

/// <summary>A reference from one file to another, with what the referencing file uses it for ("Normal", "VFX" …).</summary>
internal sealed record ResourceLink(ResourceNode Node, string? Role);

/// <summary>
/// One file of the open mod, as the resource tree shows it. A file referenced from several places
/// (a texture two materials share) is one node, listed under each of them.
/// </summary>
internal sealed class ResourceNode
{
    /// <summary>Unique within a graph, and stable across rebuilds: the option and the game path.</summary>
    public required string Id { get; init; }

    public required string GamePath { get; init; }
    public required ResourceKind Kind { get; init; }
    public required OriginKind Origin { get; init; }

    /// <summary>The option carrying the file (ModFile, Swap, Added).</summary>
    public OptionRef Option { get; init; }

    /// <summary>ModFile: the path inside the mod folder, as meta.json has it.</summary>
    public string RelPath { get; init; } = string.Empty;

    /// <summary>The file on disk this node shows: the mod's own file, or the local file being added.</summary>
    public string DiskPath { get; init; } = string.Empty;

    /// <summary>Swap: the game path that serves this one.</summary>
    public string SwapTarget { get; init; } = string.Empty;

    /// <summary>The replacement or addition set up for this file, if any.</summary>
    public ResourceReplacement? Replacement { get; set; }

    /// <summary>A mod file this plugin wrote for a replacement (it is in the sidecar's ledger).</summary>
    public bool WrittenHere { get; set; }

    /// <summary>
    /// A file this plugin wrote that was changed elsewhere since (in Penumbra's editor, say). It is
    /// kept as it is: only changed again here when its replacement changes, and then for a material
    /// made here by carrying the change over.
    /// </summary>
    public bool EditedElsewhere { get; set; }

    /// <summary>For a texture of a material made here: the slot it is written from (the live set-up, edited in place).</summary>
    public TextureSlot? AuthoredSlot { get; set; }

    /// <summary>
    /// For a model: the material each of its slots names in the file it is made from (the mod's own,
    /// the game's, or the one replacing it), before any reassignment here. Empty when unknown.
    /// </summary>
    public IReadOnlyList<string> MaterialSlots { get; set; } = Array.Empty<string>();

    /// <summary>A game model listed because an item was added to the mod from the game (see <see cref="Project.AddedItem"/>), in this option.</summary>
    public bool Pinned { get; set; }

    /// <summary>Size of the mod's file in bytes, or -1.</summary>
    public long Size { get; set; } = -1;

    /// <summary>Header facts for a texture the mod carries.</summary>
    public TexHeaderInfo? Texture { get; set; }

    /// <summary>Something worth saying about it ("not found in the mod or the game", "copies the hair material" …).</summary>
    public string? Note { get; set; }

    /// <summary>The file could not be read, so what it references is unknown.</summary>
    public string? Error { get; set; }

    public List<ResourceLink> Children { get; } = new();

    /// <summary>The entry of the contents pane this node is listed under (its first, when shared).</summary>
    public string RootId { get; set; } = string.Empty;

    /// <summary>Referenced by at least one other node of the graph.</summary>
    public bool IsReferenced { get; set; }

    public string FileName => Path.GetFileName(GamePath);

    public bool IsModContent => Origin is OriginKind.ModFile or OriginKind.Swap or OriginKind.Added;

    /// <summary>The material slot <paramref name="slot"/> loads: reassigned here, else the file's own.</summary>
    public string EffectiveMaterial(int slot)
    {
        var names = Replacement?.MaterialNames;
        if (names != null && slot < names.Count && !string.IsNullOrWhiteSpace(names[slot]))
            return names[slot];
        return slot < MaterialSlots.Count ? MaterialSlots[slot] : string.Empty;
    }

    /// <summary>The selection target: this file, in the entry it is listed under.</summary>
    public Target Target => Target.ForResource(RootId, GamePath, Option.Serialize());
}

/// <summary>
/// One entry of the contents pane: a top-level resource (a model set, an animation, a sound …) and
/// everything listed under it — or, when the pane lists the mod by option, one option (or the default
/// files) and the files it carries, as trees of what they load.
/// </summary>
internal sealed class ResourceRoot
{
    public required string Id { get; init; }
    public required ResourceSection Section { get; init; }
    public required ResourceKind Kind { get; init; }
    public string Title    { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;

    /// <summary>For an option's entry: the option (or the default files) it lists; null for an entry by kind.</summary>
    public OptionRef? Option { get; init; }

    /// <summary>For an option's entry: its file swaps and manipulations (which no tree shows).</summary>
    public int SwapCount { get; set; }
    public int ManipulationCount { get; set; }

    /// <summary>The nodes listed directly under it, in display order.</summary>
    public List<ResourceNode> Nodes { get; } = new();

    /// <summary>The options its files belong to (default files included when it has any).</summary>
    public List<OptionRef> Options { get; } = new();

    public int FileCount     { get; set; }
    public int ReplacedCount { get; set; }
    public int MissingCount  { get; set; }

    /// <summary>A texture to show on its tile: the mod's file on disk, or a game path to preview from the game.</summary>
    public string? ThumbDiskPath { get; set; }
    public string? ThumbGamePath { get; set; }

    public Target Target => Target.ForRoot(Id);
}

/// <summary>
/// Every file of the open mod, linked into trees under their top-level resources: models to their
/// materials to their textures, animations to the timelines, effects and sounds they play, effects
/// to their textures. Built on a worker and swapped in whole; never edited after.
/// </summary>
internal sealed class ResourceGraph
{
    public static readonly ResourceGraph Empty = new(Array.Empty<ResourceRoot>(), Array.Empty<ResourceRoot>(), new Dictionary<string, ResourceNode>(), -1, TimeSpan.Zero);

    private const string OptionPrefix = "opt:";
    private const string GroupPrefix  = "grp:";

    private readonly Dictionary<string, ResourceRoot> _rootsById;
    private readonly Dictionary<OptionRef, ResourceRoot> _optionRoots;
    private readonly Dictionary<string, List<ResourceNode>> _byGamePath;

    public ResourceGraph(IReadOnlyList<ResourceRoot> roots, IReadOnlyList<ResourceRoot> optionRoots,
        IReadOnlyDictionary<string, ResourceNode> nodes, int revision, TimeSpan buildTime)
    {
        Roots       = roots;
        OptionRoots = optionRoots;
        Nodes       = nodes;
        Revision    = revision;
        BuildTime   = buildTime;
        // Ids are unique by construction; should two still match (hand-edited group ids), the first
        // one is kept rather than the whole graph failing.
        _rootsById = new Dictionary<string, ResourceRoot>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots.Concat(optionRoots))
            _rootsById.TryAdd(root.Id, root);
        _optionRoots = new Dictionary<OptionRef, ResourceRoot>();
        foreach (var root in optionRoots)
            if (root.Option is { } option)
                _optionRoots.TryAdd(option, root);
        _byGamePath = nodes.Values
            .GroupBy(n => PathNormalizer.GamePath(n.GamePath))
            .ToDictionary(g => g.Key, g => g.ToList());
    }

    /// <summary>The entries by kind: what nothing references, grouped by what it is.</summary>
    public IReadOnlyList<ResourceRoot> Roots { get; }

    /// <summary>One entry per place a file can go — the default files, then each option — listing the files it carries.</summary>
    public IReadOnlyList<ResourceRoot> OptionRoots { get; }

    /// <summary>The entry listing what an option (or the default files) carries, once the graph knows the option.</summary>
    public ResourceRoot? OptionRoot(OptionRef option) => _optionRoots.GetValueOrDefault(option);

    /// <summary>The id of an option's entry ("opt:" for the default files).</summary>
    public static string OptionRootId(OptionRef option) => OptionPrefix + option.Serialize();

    /// <summary>The id the centre shows a whole group under: every option of it, one after the other.</summary>
    public static string GroupRootId(string groupId) => GroupPrefix + groupId;

    /// <summary>The option an entry id names, if it names one.</summary>
    public static OptionRef? OptionOf(string rootId)
        => rootId.StartsWith(OptionPrefix, StringComparison.Ordinal) ? OptionRef.Parse(rootId[OptionPrefix.Length..]) : null;

    /// <summary>The group an entry id names, if it names one.</summary>
    public static string? GroupOf(string rootId)
        => rootId.StartsWith(GroupPrefix, StringComparison.Ordinal) ? rootId[GroupPrefix.Length..] : null;

    /// <summary>Whether <paramref name="node"/> is listed somewhere under <paramref name="root"/>.</summary>
    public static bool Reaches(ResourceRoot root, ResourceNode node)
    {
        var seen = new HashSet<ResourceNode>();
        var stack = new Stack<ResourceNode>(root.Nodes);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current == node)
                return true;
            if (!seen.Add(current))
                continue;
            foreach (var child in current.Children)
                stack.Push(child.Node);
        }
        return false;
    }
    public IReadOnlyDictionary<string, ResourceNode> Nodes { get; }

    /// <summary>Which state of the mod and its replacements this was built from.</summary>
    public int Revision { get; }
    public TimeSpan BuildTime { get; }

    public int Unresolved => Nodes.Values.Count(n => n.Origin == OriginKind.Missing);

    // Answers the views ask every frame, worked out once per graph (on the render thread, which is
    // the only one that reads a finished graph).
    private int? _modFileCount;
    private Dictionary<OptionRef, int>? _perOption;
    private Dictionary<ResourceNode, List<ResourceNode>>? _parents;

    /// <summary>How many files the mod itself carries (every option counted).</summary>
    public int ModFileCount => _modFileCount ??= Nodes.Values.Count(n => n.Origin == OriginKind.ModFile);

    /// <summary>How many of the mod's files one option (or the default files) carries.</summary>
    public int FilesIn(OptionRef option)
    {
        _perOption ??= Nodes.Values
            .Where(n => n.Origin == OriginKind.ModFile)
            .GroupBy(n => n.Option)
            .ToDictionary(g => g.Key, g => g.Count());
        return _perOption.GetValueOrDefault(option);
    }

    /// <summary>The files that reference <paramref name="node"/>.</summary>
    public IReadOnlyList<ResourceNode> ParentsOf(ResourceNode node)
    {
        if (_parents == null)
        {
            _parents = new Dictionary<ResourceNode, List<ResourceNode>>();
            foreach (var parent in Nodes.Values)
            {
                foreach (var link in parent.Children)
                {
                    if (!_parents.TryGetValue(link.Node, out var list))
                        _parents[link.Node] = list = new List<ResourceNode>();
                    list.Add(parent);
                }
            }
        }
        return _parents.TryGetValue(node, out var parents) ? parents : Array.Empty<ResourceNode>();
    }

    public ResourceRoot? FindRoot(string id) => _rootsById.GetValueOrDefault(id);

    /// <summary>The nodes for a game path, in every option.</summary>
    public IReadOnlyList<ResourceNode> FindAll(string gamePath)
        => _byGamePath.TryGetValue(PathNormalizer.GamePath(gamePath), out var list) ? list : Array.Empty<ResourceNode>();

    /// <summary>The node of a game path in one option (vanilla and missing nodes answer any option).</summary>
    public ResourceNode? Find(string gamePath, OptionRef option)
    {
        var all = FindAll(gamePath);
        return all.FirstOrDefault(n => n.IsModContent && n.Option == option)
            ?? all.FirstOrDefault(n => n.Origin is OriginKind.Vanilla or OriginKind.Missing or OriginKind.Shared);
    }

    /// <summary>The node a resource target names, if it still exists.</summary>
    public ResourceNode? Find(Target target)
        => target.Kind == TargetKind.Resource && !string.IsNullOrEmpty(target.Path)
            ? Find(target.Path, OptionRef.Parse(target.Option))
            : null;

    /// <summary>
    /// The chain of nodes from a top-level node of <paramref name="root"/> (by default the entry the
    /// node is listed under) down to <paramref name="node"/>, inclusive, following the first path found.
    /// </summary>
    public List<ResourceNode> PathTo(ResourceNode node, ResourceRoot? root = null)
    {
        root ??= FindRoot(node.RootId);
        if (root == null)
            return new List<ResourceNode> { node };
        var path = new List<ResourceNode>();
        var seen = new HashSet<ResourceNode>();
        foreach (var top in root.Nodes)
            if (Search(top, node, path, seen))
                return path;
        return new List<ResourceNode> { node };
    }

    private static bool Search(ResourceNode current, ResourceNode wanted, List<ResourceNode> path, HashSet<ResourceNode> seen)
    {
        if (!seen.Add(current))
            return false;
        path.Add(current);
        if (current == wanted)
            return true;
        foreach (var child in current.Children)
            if (Search(child.Node, wanted, path, seen))
                return true;
        path.RemoveAt(path.Count - 1);
        return false;
    }
}
