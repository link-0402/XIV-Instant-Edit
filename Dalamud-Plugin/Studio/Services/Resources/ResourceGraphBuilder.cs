using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Models.Project;
using InstantEdit.Studio.Models.Resources;
using InstantEdit.Studio.Services.Project;

namespace InstantEdit.Studio.Services.Resources;

/// <summary>
/// Everything the graph is built from, copied on the render thread: the mod's files and swaps as
/// meta.json lists them, and the pending replacements.
/// </summary>
internal sealed class GraphInput
{
    public required string Folder { get; init; }
    public required IReadOnlyList<ModFileEntry> Files { get; init; }
    public required IReadOnlyList<ModSwapEntry> Swaps { get; init; }
    public IReadOnlyList<(OptionRef Option, string Type, int Count)> Manipulations { get; init; } = Array.Empty<(OptionRef, string, int)>();
    public IReadOnlyList<ResourceReplacement> Replacements { get; init; } = Array.Empty<ResourceReplacement>();

    /// <summary>The models of items added from the game (see <see cref="AddedItem"/>), with the option each goes into.</summary>
    public IReadOnlyList<(OptionRef Option, string GamePath)> Pinned { get; init; } = Array.Empty<(OptionRef, string)>();

    /// <summary>Every place a file can go — the default files, then each option in order — with its label, each listed as an entry of its own.</summary>
    public IReadOnlyList<(OptionRef Option, string Label)> Carriers { get; init; } = Array.Empty<(OptionRef, string)>();

    public IItemNames? ItemNames { get; init; }
    public int Revision { get; init; }

    /// <summary>What this plugin last wrote into the mod (the sidecar's ledger), by mod-relative path key.</summary>
    public IReadOnlyDictionary<string, LedgerFile> Written { get; init; } = new Dictionary<string, LedgerFile>();
}

/// <summary>
/// Links a mod's files into the trees the editor shows. Every file meta.json lists becomes a node
/// (one per option it is in); models are linked to the materials they name, materials to their
/// textures, and animations, timelines, effects and skeletons to the files they name inside. A
/// reference the mod does not serve resolves to the game's own file, or shows as missing. What
/// nothing references is a top-level resource, grouped into the contents pane's entries: every file
/// of one model set together, the same animation for every race together, each effect and sound on
/// its own, loose textures by folder. A model is a model of the mod however it was made: a mod
/// built in Build mode is read like any other.
///
/// Runs on a worker. Reads mod files through <see cref="ModFileFactsCache"/> and the game's through
/// <see cref="IVanillaFiles"/>, and touches nothing else that is shared.
/// </summary>
internal static class ResourceGraphBuilder
{
    private const string MetadataRootId = "meta";

    public static ResourceGraph Build(GraphInput input, ModFileFactsCache facts, IVanillaFiles vanilla, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var context = new Context(input, facts, vanilla, ct);
        context.Run();
        return new ResourceGraph(context.Roots, context.OptionRoots, context.Nodes, input.Revision, clock.Elapsed);
    }

    private sealed class Context
    {
        private readonly GraphInput _input;
        private readonly ModFileFactsCache _facts;
        private readonly IVanillaFiles _vanilla;
        private readonly CancellationToken _ct;

        public readonly Dictionary<string, ResourceNode> Nodes = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<ResourceRoot> Roots = new();
        public readonly List<ResourceRoot> OptionRoots = new();

        private readonly Dictionary<string, List<ResourceNode>> _modByPath = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<ResourceNode>> _modByFileName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, bool> _vanillaExists = new(StringComparer.Ordinal);
        private readonly HashSet<ResourceNode> _linked = new();

        public Context(GraphInput input, ModFileFactsCache facts, IVanillaFiles vanilla, CancellationToken ct)
        {
            _input   = input;
            _facts   = facts;
            _vanilla = vanilla;
            _ct      = ct;
        }

        public void Run()
        {
            AddModFiles();
            AddSwaps();
            AddReplacements();
            AttachReplacements();   // to the mod's own files first, so they link through what replaces them
            AddPinned();
            LinkAll();
            AttachReplacements();   // then to the game files the linking brought in
            AddAuthoredTextures();
            MarkWritten();
            MarkReferenced();
            BuildRoots();
            BuildOptionRoots();
        }

        // ── Nodes ────────────────────────────────────────────────────────────

        private void AddModFiles()
        {
            foreach (var file in _input.Files)
            {
                var path = PathNormalizer.GamePath(file.GamePath);
                var id = NodeId(file.Option, path);
                if (Nodes.ContainsKey(id))
                    continue;   // the same path twice in one option, differing only in case

                var node = new ResourceNode
                {
                    Id       = id,
                    GamePath = path,
                    Kind     = ResourceKinds.FromGamePath(path),
                    Origin   = OriginKind.ModFile,
                    Option   = file.Option,
                    RelPath  = file.RelPath,
                    DiskPath = PathNormalizer.ToDiskPath(_input.Folder, file.RelPath),
                };
                Add(node);
            }
        }

        private void AddSwaps()
        {
            foreach (var swap in _input.Swaps)
            {
                var path = PathNormalizer.GamePath(swap.GamePath);
                var id = $"swap:{swap.Option.Serialize()}|{path}";
                if (Nodes.ContainsKey(id))
                    continue;
                Nodes[id] = new ResourceNode
                {
                    Id         = id,
                    GamePath   = path,
                    Kind       = ResourceKinds.FromGamePath(path),
                    Origin     = OriginKind.Swap,
                    Option     = swap.Option,
                    SwapTarget = PathNormalizer.GamePath(swap.TargetGamePath),
                };
            }
        }

        /// <summary>Files being added (a game path the option did not serve) become nodes of their own.</summary>
        private void AddReplacements()
        {
            foreach (var replacement in _input.Replacements)
            {
                var option = OptionRef.Parse(replacement.Option);
                var path = PathNormalizer.GamePath(replacement.GamePath);
                var id = NodeId(option, path);
                if (Nodes.ContainsKey(id))
                    continue;   // replaces a file the option has; attached to that node later

                // Indexed like the mod's own files, so whatever references this path links to it —
                // including a game file the mod only referenced until now.
                var node = new ResourceNode
                {
                    Id          = id,
                    GamePath    = path,
                    Kind        = ResourceKinds.FromGamePath(path),
                    Origin      = OriginKind.Added,
                    Option      = option,
                    DiskPath    = replacement.SourcePath,
                    Replacement = replacement,
                };
                Add(node);
            }
        }

        /// <summary>
        /// The models of items added from the game, as the game's own files — each until the mod has a
        /// file of its own at that path (in the item's option or the default files), which is listed instead.
        /// </summary>
        private void AddPinned()
        {
            var none = new List<ResourceNode>();
            foreach (var (option, gamePath) in _input.Pinned)
            {
                var path = PathNormalizer.GamePath(gamePath);
                if (path.Length == 0)
                    continue;
                if (_modByPath.TryGetValue(path, out var mod) && mod.Any(n => n.Option == option || n.Option.IsDefault))
                    continue;
                var node = GameFile(path, missingAllowed: true, none)!;
                node.Pinned = true;
                if (node.Origin == OriginKind.Missing)
                    node.Note = "the game has no model here for this race — drop a .mdl on it to add one";
            }
        }

        /// <summary>
        /// Which of the mod's files this plugin wrote, and which of those were changed elsewhere since
        /// — told apart by size and timestamp against the ledger, so nothing has to be read again.
        /// </summary>
        private void MarkWritten()
        {
            foreach (var node in Nodes.Values)
            {
                _ct.ThrowIfCancellationRequested();
                if (node.Origin != OriginKind.ModFile || !_input.Written.TryGetValue(PathNormalizer.RelKey(node.RelPath), out var wrote))
                    continue;

                node.WrittenHere = true;
                if (wrote.EditedElsewhere)
                {
                    node.EditedElsewhere = true;
                    continue;
                }
                if (wrote.Length < 0 || wrote.WrittenTicks == 0)
                    continue;   // written by a version that kept no size: cannot tell

                var facts = _facts.ForFile(node.DiskPath, node.Kind);
                node.EditedElsewhere = facts.Size >= 0 && (facts.Size != wrote.Length || facts.StampTicks != wrote.WrittenTicks);
            }
        }

        private void Add(ResourceNode node)
        {
            Nodes[node.Id] = node;
            AddTo(_modByPath, node.GamePath, node);
            AddTo(_modByFileName, Path.GetFileName(node.GamePath), node);
        }

        private static void AddTo(Dictionary<string, List<ResourceNode>> index, string key, ResourceNode node)
        {
            if (!index.TryGetValue(key, out var list))
                index[key] = list = new List<ResourceNode>();
            list.Add(node);
        }

        private static string NodeId(OptionRef option, string gamePath) => $"{option.Serialize()}|{gamePath}";

        // ── Links ────────────────────────────────────────────────────────────

        private void LinkAll()
        {
            var pending = new Queue<ResourceNode>(Nodes.Values.Where(n => n.Origin is OriginKind.ModFile or OriginKind.Added || n.Pinned).ToList());
            while (pending.Count > 0)
            {
                _ct.ThrowIfCancellationRequested();
                var node = pending.Dequeue();
                if (!_linked.Add(node))
                    continue;
                foreach (var created in Link(node))
                    pending.Enqueue(created);
            }
        }

        /// <summary>Links one node to what it references; returns the game files it brought in, which are linked in turn.</summary>
        private List<ResourceNode> Link(ResourceNode node)
        {
            var created = new List<ResourceNode>();
            if (node.Replacement?.Authored != null)
                return created;   // its children are the textures it is made with (AddAuthoredTextures)

            // A file being replaced by one of the same kind (a .mdl by a .mdl) is shown as what
            // replaces it: its materials, its textures — including any that nothing provides yet.
            var replacedBy = node.Replacement is { } r && File.Exists(r.SourcePath)
                             && string.Equals(Path.GetExtension(r.SourcePath), Path.GetExtension(node.GamePath), StringComparison.OrdinalIgnoreCase)
                ? r.SourcePath
                : null;

            FileFacts? facts = node.Origin switch
            {
                _ when replacedBy != null && FileFactsWanted(node.Kind) => _facts.ForFile(replacedBy, node.Kind),
                OriginKind.ModFile when node.Replacement is { OnlyReassigns: true } => ReassignedBase(node),
                OriginKind.ModFile => _facts.ForFile(node.DiskPath, node.Kind),
                OriginKind.Added when ResourceKinds.Accepts(node.GamePath, node.DiskPath) && SameKind(node) => _facts.ForFile(node.DiskPath, node.Kind),
                OriginKind.Added when node.Replacement is { OnlyReassigns: true } => _facts.ForVanilla(node.GamePath, node.Kind, _vanilla),
                OriginKind.Vanilla when node.Kind == ResourceKind.Material || node.Pinned => _facts.ForVanilla(node.GamePath, node.Kind, _vanilla),
                _ => null,
            };
            if (facts == null)
                return created;

            if (node.Origin is OriginKind.ModFile or OriginKind.Added)
            {
                // A mod file still says what it is while a replacement waits (the replacement only
                // decides what it links to); a file being added is its source.
                var own = node.Origin == OriginKind.ModFile && replacedBy != null ? _facts.ForFile(node.DiskPath, node.Kind) : facts;
                node.Size    = own.Size;
                node.Texture = own.Texture;
                node.Error   = own.Error;
            }

            // A model links to what its slots load once reassigned here.
            var references = facts.References;
            if (node.Kind == ResourceKind.Model)
            {
                node.MaterialSlots = facts.MaterialSlots;
                if (node.Replacement is { ReassignsMaterials: true })
                    references = Enumerable.Range(0, facts.MaterialSlots.Count)
                        .Select(node.EffectiveMaterial)
                        .Where(n => !string.IsNullOrWhiteSpace(n))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
            }

            for (int i = 0; i < references.Count; i++)
            {
                var reference = references[i];
                var role = i < facts.Roles.Count ? facts.Roles[i] : null;
                IEnumerable<ResourceNode> targets = node.Kind switch
                {
                    ResourceKind.Model    => ResolveMaterial(node, reference, created),
                    ResourceKind.Material => ResolvePath(node, reference, created, missingAllowed: true),
                    _                     => ResolvePath(node, reference, created, missingAllowed: false),
                };
                foreach (var target in targets)
                    AddChild(node, target, role ?? RoleFor(node.Kind, target.Kind));
            }
            return created;
        }

        /// <summary>
        /// What a model of the mod whose materials are reassigned here is made from: the mod's own file
        /// (or the game's), not the copy written with the reassignment, whose slots already say what was
        /// picked — so the inspector can still tell what each slot had.
        /// </summary>
        private FileFacts ReassignedBase(ResourceNode node)
        {
            if (!_input.Written.TryGetValue(PathNormalizer.RelKey(node.RelPath), out var wrote))
                return _facts.ForFile(node.DiskPath, node.Kind);
            if (wrote.OriginalRelPath is { } original && PathNormalizer.IsSafeRel(original))
                return _facts.ForFile(PathNormalizer.ToDiskPath(_input.Folder, original), node.Kind);
            return _facts.ForVanilla(node.GamePath, node.Kind, _vanilla) ?? _facts.ForFile(node.DiskPath, node.Kind);
        }

        /// <summary>Kinds whose files say what else they load.</summary>
        private static bool FileFactsWanted(ResourceKind kind) => ModFileFactsCache.References(kind);

        /// <summary>An added file is only looked into when its source is the same kind of file as its target (not an image becoming a .tex).</summary>
        private static bool SameKind(ResourceNode node)
            => string.Equals(Path.GetExtension(node.DiskPath), Path.GetExtension(node.GamePath), StringComparison.OrdinalIgnoreCase);

        private static string? RoleFor(ResourceKind parent, ResourceKind child) => (parent, child) switch
        {
            (_, ResourceKind.Vfx)           => "VFX",
            (_, ResourceKind.Sound)         => "Sound",
            (_, ResourceKind.Timeline)      => "Timeline",
            (_, ResourceKind.VfxTexture)    => "Texture",
            (_, ResourceKind.Animation)     => "Animation",
            (_, ResourceKind.Skeleton)      => "Skeleton",
            (ResourceKind.Model, ResourceKind.Material) => null,
            _                               => null,
        };

        /// <summary>
        /// The material a model names. A relative name ("/mt_c0201e6114_sho_a.mtrl") lives where its
        /// name says; the mod's own copies are found by name in that set's material folders (any
        /// variant), else the game's, else it is missing.
        /// </summary>
        private IEnumerable<ResourceNode> ResolveMaterial(ResourceNode model, string reference, List<ResourceNode> created)
        {
            var name = reference.Replace('\\', '/').Trim();
            if (name.Length == 0)
                return Array.Empty<ResourceNode>();

            var trimmed = name.TrimStart('/');
            if (trimmed.Contains('/'))
                return ResolvePath(model, trimmed, created, missingAllowed: true);   // absolute path

            var fileName = trimmed.ToLowerInvariant();
            var folder = ResourceNaming.MaterialFolder(fileName) ?? ResourceNaming.FallbackMaterialFolder(model.GamePath);
            var stem = ResourceNaming.MaterialFolderStem(folder);

            if (_modByFileName.TryGetValue(fileName, out var byName))
            {
                var inSet = byName.Where(n => n.GamePath.StartsWith(stem, StringComparison.Ordinal)).ToList();
                var chosen = Prefer(model.Option, inSet.Count > 0 ? inSet : byName);
                if (chosen.Count > 0)
                    return chosen;
            }

            return new[] { GameFile($"{folder}/{fileName}", missingAllowed: true, created)! };
        }

        /// <summary>A full game path: the mod's own (same option first), else the game's, else missing (when allowed).</summary>
        private IEnumerable<ResourceNode> ResolvePath(ResourceNode from, string reference, List<ResourceNode> created, bool missingAllowed)
        {
            var path = PathNormalizer.GamePath(reference);
            if (path.Length == 0 || path == from.GamePath)
                return Array.Empty<ResourceNode>();

            if (_modByPath.TryGetValue(path, out var mod))
            {
                var chosen = Prefer(from.Option, mod);
                if (chosen.Count > 0)
                    return chosen;
            }

            var game = GameFile(path, missingAllowed, created);
            return game != null ? new[] { game } : Array.Empty<ResourceNode>();
        }

        /// <summary>The copies in the referencing file's own option, else in the default files, else all of them.</summary>
        private static List<ResourceNode> Prefer(OptionRef option, IReadOnlyList<ResourceNode> candidates)
        {
            var same = candidates.Where(n => n.Option == option && n.Origin != OriginKind.Swap).ToList();
            if (same.Count > 0)
                return same;
            var fallback = candidates.Where(n => n.Option.IsDefault && n.Origin != OriginKind.Swap).ToList();
            return fallback.Count > 0 ? fallback : candidates.Where(n => n.Origin != OriginKind.Swap).ToList();
        }

        /// <summary>The game's own file at a path (one node per path), or a missing one when allowed.</summary>
        private ResourceNode? GameFile(string path, bool missingAllowed, List<ResourceNode> created)
        {
            if (!_vanillaExists.TryGetValue(path, out var exists))
            {
                exists = _vanilla.Exists(path);
                _vanillaExists[path] = exists;
            }

            if (!exists && !missingAllowed)
                return null;

            var id = exists ? $"vanilla|{path}" : $"missing|{path}";
            if (Nodes.TryGetValue(id, out var node))
                return node;

            node = new ResourceNode
            {
                Id       = id,
                GamePath = path,
                Kind     = ResourceKinds.FromGamePath(path),
                Origin   = exists ? OriginKind.Vanilla : OriginKind.Missing,
                Note     = exists ? null : "neither the mod nor the game has this file",
            };
            Nodes[id] = node;
            if (exists && node.Kind == ResourceKind.Material)
                created.Add(node);
            return node;
        }

        private static void AddChild(ResourceNode parent, ResourceNode child, string? role)
        {
            if (child == parent || parent.Children.Any(c => c.Node == child))
                return;
            parent.Children.Add(new ResourceLink(child, role));
        }

        // ── Replacements of existing files ───────────────────────────────────

        private void AttachReplacements()
        {
            foreach (var replacement in _input.Replacements)
            {
                var option = OptionRef.Parse(replacement.Option);
                var path = PathNormalizer.GamePath(replacement.GamePath);
                if (Nodes.TryGetValue(NodeId(option, path), out var node))
                {
                    node.Replacement ??= replacement;
                    continue;
                }

                // Replacing a game file the mod only references: it becomes a file of the mod's default files.
                if (option.IsDefault && (Nodes.TryGetValue($"vanilla|{path}", out node) || Nodes.TryGetValue($"missing|{path}", out node)))
                    node.Replacement ??= replacement;
            }
        }

        /// <summary>
        /// A material made here lists the textures it will be built with, in place of whatever the
        /// file it replaces referenced: the mod's own file where one is at that path already, else a
        /// file to be added.
        /// </summary>
        private void AddAuthoredTextures()
        {
            foreach (var node in Nodes.Values.ToList())
            {
                if (node.Replacement?.Authored is not { } material)
                    continue;

                node.Children.Clear();
                var option = node.IsModContent ? node.Option : OptionRef.Default;
                foreach (var slot in material.Textures)
                {
                    if (slot.IsShared)
                    {
                        // Kept as the material has it: the texture it already uses, wherever that is.
                        foreach (var kept in ResolvePath(node, slot.SharedGamePath, new List<ResourceNode>(), missingAllowed: true))
                            AddChild(node, kept, TextureTypeInfo.DisplayName(slot.Type));
                        continue;
                    }

                    var path = ResourceNaming.AuthoredTexturePath(node.GamePath, slot.Postfix);
                    var id = NodeId(option, path);
                    if (!Nodes.TryGetValue(id, out var child))
                    {
                        child = new ResourceNode
                        {
                            Id       = id,
                            GamePath = path,
                            Kind     = ResourceKind.Texture,
                            Origin   = OriginKind.Added,
                            Option   = option,
                            DiskPath = slot.UseWhiteDummy ? string.Empty : slot.SourcePath,
                            Note     = slot.UseWhiteDummy ? "placeholder" : string.IsNullOrWhiteSpace(slot.SourcePath) ? "no image set yet" : null,
                        };
                        Nodes[id] = child;
                    }
                    child.AuthoredSlot = slot;
                    AddChild(node, child, TextureTypeInfo.DisplayName(slot.Type));
                }
            }
        }

        private void MarkReferenced()
        {
            foreach (var node in Nodes.Values)
                foreach (var child in node.Children)
                    child.Node.IsReferenced = true;
        }

        // ── Roots ────────────────────────────────────────────────────────────

        private void BuildRoots()
        {
            // What nothing references is top-level, grouped into entries.
            var groups = new Dictionary<string, (ResourceSection Section, ResourceKind Kind, List<ResourceNode> Nodes)>(StringComparer.Ordinal);
            foreach (var node in Nodes.Values)
            {
                if (node.IsReferenced || node.Origin is OriginKind.Vanilla or OriginKind.Missing && !node.Pinned)
                    continue;

                string key;
                ResourceSection section;
                if (node.Origin == OriginKind.Swap)
                {
                    key = MetadataRootId;
                    section = ResourceSection.Metadata;
                }
                else
                {
                    section = ResourceKinds.SectionFor(node.Kind, node.GamePath);
                    key = GroupKey(node, section);
                }

                if (!groups.TryGetValue(key, out var group))
                    groups[key] = group = (section, node.Kind, new List<ResourceNode>());
                group.Nodes.Add(node);
            }

            if (!groups.ContainsKey(MetadataRootId) && _input.Manipulations.Count > 0)
                groups[MetadataRootId] = (ResourceSection.Metadata, ResourceKind.Other, new List<ResourceNode>());

            foreach (var (key, group) in groups)
            {
                var ordered = group.Nodes
                    .OrderBy(n => KindOrder(n.Kind))
                    .ThenBy(n => n.GamePath, StringComparer.Ordinal)
                    .ThenBy(n => n.Option.IsDefault ? 0 : 1)
                    .ThenBy(n => n.Option.Serialize(), StringComparer.Ordinal)
                    .ToList();
                var root = new ResourceRoot
                {
                    Id      = key,
                    Section = group.Section,
                    Kind    = ordered.Count > 0 ? ordered[0].Kind : group.Kind,
                };
                root.Nodes.AddRange(ordered);
                (root.Title, root.Subtitle) = TitleFor(key, root, ordered);
                Roots.Add(root);
            }

            foreach (var root in Roots)
                Summarise(root);

            Roots.Sort((a, b) =>
            {
                int bySection = a.Section.CompareTo(b.Section);
                if (bySection != 0) return bySection;
                return string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);
            });
        }

        /// <summary>
        /// One entry per place a file can go, listing the files it carries the way the game loads them:
        /// what nothing else in the same option loads is on top (a model, with its materials and their
        /// textures under it), its file swaps beside them, and the game models of items added into it.
        /// </summary>
        private void BuildOptionRoots()
        {
            var parents = new Dictionary<ResourceNode, List<ResourceNode>>();
            foreach (var node in Nodes.Values)
                foreach (var link in node.Children)
                {
                    if (!parents.TryGetValue(link.Node, out var list))
                        parents[link.Node] = list = new List<ResourceNode>();
                    list.Add(node);
                }

            var own = new Dictionary<OptionRef, List<ResourceNode>>();
            foreach (var (option, _) in _input.Carriers)
                own.TryAdd(option, new List<ResourceNode>());
            foreach (var node in Nodes.Values)
                if (node.IsModContent && own.TryGetValue(node.Option, out var list))
                    list.Add(node);

            var made = new HashSet<OptionRef>();
            foreach (var (option, label) in _input.Carriers)
            {
                _ct.ThrowIfCancellationRequested();
                if (!made.Add(option))
                    continue;
                var root = new ResourceRoot
                {
                    Id      = ResourceGraph.OptionRootId(option),
                    Section = ResourceSection.Other,
                    Kind    = ResourceKind.Other,
                    Option  = option,
                    Title   = label,
                };

                var files = own[option]
                    .OrderBy(n => KindOrder(n.Kind))
                    .ThenBy(n => n.GamePath, StringComparer.Ordinal)
                    .ToList();
                var top = files.Where(n => n.Origin == OriginKind.Swap
                                        || !(parents.TryGetValue(n, out var p) && p.Any(x => x.IsModContent && x.Option == option)))
                               .ToList();

                // Files that only load each other would never be reached from the top: list them too.
                var reached = new HashSet<ResourceNode>();
                void Walk(ResourceNode from)
                {
                    var stack = new Stack<ResourceNode>();
                    stack.Push(from);
                    while (stack.Count > 0)
                    {
                        var n = stack.Pop();
                        if (!reached.Add(n))
                            continue;
                        foreach (var child in n.Children)
                            stack.Push(child.Node);
                    }
                }
                top.ForEach(Walk);
                foreach (var file in files.Where(f => !reached.Contains(f)))
                {
                    top.Add(file);
                    Walk(file);
                }

                foreach (var (pinOption, gamePath) in _input.Pinned)
                {
                    var path = PathNormalizer.GamePath(gamePath);
                    if (pinOption == option
                        && (Nodes.TryGetValue($"vanilla|{path}", out var pinned) || Nodes.TryGetValue($"missing|{path}", out pinned))
                        && pinned.Pinned && !top.Contains(pinned))
                        top.Add(pinned);
                }

                root.Nodes.AddRange(top);
                root.SwapCount = files.Count(n => n.Origin == OriginKind.Swap);
                root.ManipulationCount = _input.Manipulations.Where(m => m.Option == option).Sum(m => m.Count);
                SummariseOption(root, option);
                OptionRoots.Add(root);
            }
        }

        /// <summary>Counts and a thumbnail for an option's entry: only its own files count, not what it loads from elsewhere.</summary>
        private static void SummariseOption(ResourceRoot root, OptionRef option)
        {
            var seen = new HashSet<ResourceNode>();
            var stack = new Stack<ResourceNode>(root.Nodes.AsEnumerable().Reverse());
            ResourceNode? thumb = null;
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                if (!seen.Add(node))
                    continue;
                bool mine = node.IsModContent && node.Option == option;
                if (mine && node.Origin != OriginKind.Swap)
                    root.FileCount++;
                if (node.Replacement != null && OptionRef.Parse(node.Replacement.Option) == option)
                    root.ReplacedCount++;
                if (node.Origin == OriginKind.Missing)
                    root.MissingCount++;
                if (node.Kind is ResourceKind.Texture or ResourceKind.VfxTexture or ResourceKind.Interface
                    && (thumb == null || mine && !(thumb.IsModContent && thumb.Option == option)))
                    thumb = node;
                for (int i = node.Children.Count - 1; i >= 0; i--)
                    stack.Push(node.Children[i].Node);
            }

            root.Options.Add(option);
            root.Subtitle = $"{root.FileCount} file(s)";
            if (thumb == null)
                return;
            if (thumb.Origin is OriginKind.ModFile or OriginKind.Added && thumb.DiskPath.Length > 0)
                root.ThumbDiskPath = thumb.DiskPath;
            else if (thumb.Origin == OriginKind.Vanilla)
                root.ThumbGamePath = thumb.GamePath;
        }

        /// <summary>Which entry a top-level file belongs to.</summary>
        private static string GroupKey(ResourceNode node, ResourceSection section)
        {
            switch (section)
            {
                case ResourceSection.Models or ResourceSection.Environment:
                    if (ResourceNaming.SetKey(node.GamePath) is { } set)
                        return $"set:{set}";
                    return $"dir:{Folder(node.GamePath)}";
                case ResourceSection.Animations or ResourceSection.Skeletons:
                    return $"file:{ResourceNaming.RaceAgnostic(node.GamePath)}";
                case ResourceSection.Vfx or ResourceSection.Sounds:
                    return $"file:{node.GamePath}";
                default:
                    return $"dir:{Folder(node.GamePath)}";
            }
        }

        private static string Folder(string path)
        {
            int slash = path.LastIndexOf('/');
            return slash > 0 ? path[..slash] : path;
        }

        private static int KindOrder(ResourceKind kind) => kind switch
        {
            ResourceKind.Model      => 0,
            ResourceKind.Animation  => 1,
            ResourceKind.Timeline   => 2,
            ResourceKind.Vfx        => 3,
            ResourceKind.Material   => 4,
            ResourceKind.Texture    => 5,
            ResourceKind.VfxTexture => 6,
            ResourceKind.Sound      => 7,
            _                       => 8,
        };

        private (string Title, string Subtitle) TitleFor(string key, ResourceRoot root, List<ResourceNode> nodes)
        {
            if (key == MetadataRootId)
            {
                int manipulations = _input.Manipulations.Sum(m => m.Count);
                int swaps = nodes.Count;
                var parts = new List<string>();
                if (manipulations > 0) parts.Add($"{manipulations} manipulation(s)");
                if (swaps > 0) parts.Add($"{swaps} file swap(s)");
                return ("Metadata", string.Join(" · ", parts));
            }

            if (key.StartsWith("set:", StringComparison.Ordinal))
                return ResourceNaming.SetTitle(key[4..], nodes.Select(n => n.GamePath), _input.ItemNames);

            if (key.StartsWith("file:", StringComparison.Ordinal))
            {
                var path = key[5..];
                var races = ResourceNaming.Races(nodes.Select(n => n.GamePath));
                var folder = Folder(path);
                var subtitle = races.Count > 1 ? $"{folder} · {races.Count} races" : folder;
                return (Path.GetFileName(path), subtitle);
            }

            var dir = key[4..];
            var last = dir.LastIndexOf('/');
            return ($"{(last >= 0 ? dir[(last + 1)..] : dir)}/", $"{dir} · {nodes.Count} file(s)");
        }

        /// <summary>Counts, options, and a thumbnail for an entry, from everything reachable under it; also tells each node its entry.</summary>
        private void Summarise(ResourceRoot root)
        {
            var seen = new HashSet<ResourceNode>();
            var options = new List<OptionRef>();
            var stack = new Stack<ResourceNode>(root.Nodes.AsEnumerable().Reverse());
            ResourceNode? thumb = null;
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                if (!seen.Add(node))
                    continue;
                if (node.RootId.Length == 0)
                    node.RootId = root.Id;

                if (node.IsModContent)
                {
                    root.FileCount++;
                    if (!options.Contains(node.Option))
                        options.Add(node.Option);
                }
                if (node.Replacement != null)
                    root.ReplacedCount++;
                if (node.Origin == OriginKind.Missing)
                    root.MissingCount++;

                if (thumb == null && node.Kind is ResourceKind.Texture or ResourceKind.VfxTexture or ResourceKind.Interface)
                    thumb = node;

                for (int i = node.Children.Count - 1; i >= 0; i--)
                    stack.Push(node.Children[i].Node);
            }

            root.Options.AddRange(options.OrderBy(o => o.IsDefault ? 0 : 1));
            if (thumb != null)
            {
                switch (thumb.Origin)
                {
                    case OriginKind.ModFile or OriginKind.Added when thumb.DiskPath.Length > 0:
                        root.ThumbDiskPath = thumb.DiskPath;
                        break;
                    case OriginKind.Vanilla:
                        root.ThumbGamePath = thumb.GamePath;
                        break;
                }
            }
        }
    }
}
