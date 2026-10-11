using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using InstantEdit.Studio.Models.Project;
using InstantEdit.Studio.Models.Resources;
using InstantEdit.Studio.Services;
using InstantEdit.Studio.Services.Project;
using InstantEdit.Studio.Services.Resources;

namespace InstantEdit.Studio.Models;

/// <summary>
/// A mod open in Edit mode, as the editor works on it: the mod's own files (its resource graph),
/// the files of the user's own replacing or adding some of them, and which node is selected.
/// Panels read and edit this directly and call <see cref="MarkDirty"/>; the session batches the
/// writes into the mod's sidecar (see <see cref="ModProject"/>), so typing into a field does not
/// rewrite a file on every keystroke, and <see cref="ModSync"/> writes the change into the mod
/// itself once edits settle.
///
/// Every file the mod has is just a file of the mod here, however it was made: Build mode's ports
/// leave nothing behind for this to read.
/// </summary>
internal sealed class ModSession : ISetUpSession, IItemShelf
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan StatusLifetime = TimeSpan.FromSeconds(10);

    private readonly StudioHost _host;
    private readonly ModEditor _editor;
    private readonly Stopwatch _sinceDirty = new();
    private readonly Stopwatch _sinceSetUpChange = Stopwatch.StartNew();
    private bool _dirty;

    public ModSession(ModEditor editor)
    {
        _host   = editor.Host;
        _editor = editor;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // The open mod
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>The mod being edited, or null while none is open.</summary>
    public ModProject? Project { get; private set; }

    /// <summary>Starts editing <paramref name="project"/>.</summary>
    public void Attach(ModProject project)
    {
        Project   = project;
        Selection = null;
        _reveal   = null;
        _dirty    = false;
        AddInto   = OptionRef.Default;
        Revision++;
        SetUpRevision++;
    }

    /// <summary>
    /// Stops editing the open mod, writing what is pending first unless <paramref name="flush"/> is
    /// off (the folder is gone). Changes not yet in the mod are handed to the sync, which writes them
    /// into it after the mod is closed here.
    /// </summary>
    public void Detach(bool flush = true)
    {
        if (Project == null)
            return;
        if (flush)
        {
            try
            {
                Flush();
                _editor.Sync.Closing(Project);
            }
            catch (Exception ex)
            {
                StudioServices.Log.Warning(ex, "[Studio] Could not save {0} while closing it", Project.FolderPath);
            }
        }
        Project   = null;
        Selection = null;
        _reveal   = null;
        _dirty    = false;
        Revision++;
    }

    /// <summary>Edits top-level fields of the open mod's meta.json; written with the next flush.</summary>
    public void EditMeta(Action<PenumbraModMeta> edit, params string[] keys)
    {
        if (Project == null)
            return;
        int before = Project.Revision;
        Project.EditMeta(edit, keys);
        if (Project.Revision == before)
            return;
        Revision++;
        _sinceDirty.Restart();
    }

    /// <summary>Bumped on every edit; the validator re-runs when it changes.</summary>
    public int Revision { get; private set; }

    /// <summary>Bumped on every change of what goes into the mod (the replacements, not the mod's name or the view); the sync writes the mod when it changes.</summary>
    public int SetUpRevision { get; private set; }

    /// <summary>How long ago the replacements last changed, so a burst of edits is written once.</summary>
    public TimeSpan SinceSetUpChange => _sinceSetUpChange.Elapsed;

    /// <summary>Latest validation result, set by the main window's validation tick.</summary>
    public IReadOnlyList<PortIssue> Issues { get; set; } = Array.Empty<PortIssue>();

    // ─────────────────────────────────────────────────────────────────────────
    // View and focus
    // ─────────────────────────────────────────────────────────────────────────

    public StudioView ActiveView { get; private set; } = StudioView.Overview;

    /// <summary>The tree node the inspector shows.</summary>
    public Target? Selection { get; set; }

    private Target? _reveal;

    public StatusMessage? Status { get; private set; }

    public void GoToView(StudioView view) => ActiveView = view;

    /// <summary>Selects <paramref name="target"/>, opens its view, and asks the tree to open its parents and scroll to it.</summary>
    public void Focus(Target target)
    {
        target = Resolve(target);
        Selection = target;
        GoToView(target.View);
        _reveal = target;
    }

    /// <summary>
    /// A resource target that names a file but not the entry it is listed under (a row of an update's
    /// report, written before the tree knew it) gets its entry from the current graph.
    /// </summary>
    private Target Resolve(Target target)
    {
        if (target.Kind != TargetKind.Resource || string.IsNullOrEmpty(target.Path) || !string.IsNullOrEmpty(target.Root))
            return target;
        var node = Graph.Find(target.Path, OptionRef.Parse(target.Option)) ?? Graph.FindAll(target.Path).FirstOrDefault();
        return node != null ? TargetFor(node) : target;
    }

    /// <summary>Whether the contents pane lists the mod by option rather than by kind of file.</summary>
    public bool ByOption => _host.Configuration.ContentsByOption;

    /// <summary>
    /// Where a file is shown: in the entry of the option it belongs to while the pane lists the mod by
    /// option (when it is listed there), else in the entry of its kind.
    /// </summary>
    public Target TargetFor(ResourceNode node)
    {
        if (ByOption && Graph.OptionRoot(node.IsModContent ? node.Option : OptionFor(node)) is { } root && ResourceGraph.Reaches(root, node))
            return Target.ForResource(root.Id, node.GamePath, node.Option.Serialize());
        return node.Target;
    }

    /// <summary>
    /// Called once per frame: a file picked before the graph listed it (one just added) gets its entry
    /// once a graph that has it is in.
    /// </summary>
    public void SettleSelection()
    {
        if (Selection is not { Kind: TargetKind.Resource, Root.Length: 0, Path.Length: > 0 } pending)
            return;
        var resolved = Resolve(pending);
        if (string.IsNullOrEmpty(resolved.Root))
            return;
        Selection = resolved;
        if (_reveal == pending)
            _reveal = resolved;
    }

    /// <summary>The resource graph of the open mod, as last built (kept current every frame).</summary>
    public ResourceGraph Graph { get; set; } = ResourceGraph.Empty;

    /// <summary>The target a focus request asked to bring into view, until a view takes it.</summary>
    public Target? PendingReveal => _reveal;

    /// <summary>True once for the row that <see cref="Focus"/> asked to bring into view.</summary>
    public bool TakeScrollRequest(Target row)
    {
        if (_reveal != row) return false;
        _reveal = null;
        return true;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Status messages
    // ─────────────────────────────────────────────────────────────────────────

    public void Notify(string text, Severity severity) => Status = new StatusMessage(text, severity, DateTime.UtcNow);

    /// <summary>The current status message, or null once it has aged out.</summary>
    public StatusMessage? LiveStatus
        => Status != null && DateTime.UtcNow - Status.At < StatusLifetime ? Status : null;

    // ─────────────────────────────────────────────────────────────────────────
    // Replacing and adding the mod's own resources
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>The replacement pending for a game path in an option, if any.</summary>
    public ResourceReplacement? FindReplacement(string gamePath, OptionRef option)
    {
        if (Project == null)
            return null;
        var key = PathNormalizer.GamePath(gamePath);
        return Project.Sidecar.Replacements.FirstOrDefault(r => OptionRef.Parse(r.Option) == option && PathNormalizer.GamePath(r.GamePath) == key);
    }

    /// <summary>
    /// Puts <paramref name="sourcePath"/> at <paramref name="gamePath"/> in <paramref name="option"/>:
    /// replacing the file the option has there, or adding one. Written into the mod by the sync, and
    /// again whenever the file changes.
    /// </summary>
    public ResourceReplacement? SetReplacement(string gamePath, OptionRef option, string sourcePath, bool isNew)
    {
        if (Project == null)
            return null;

        var replacement = FindReplacement(gamePath, option);
        if (replacement == null)
        {
            replacement = new ResourceReplacement
            {
                GamePath    = PathNormalizer.GamePath(gamePath),
                Option      = option.Serialize(),
                Compression = _host.Configuration.DefaultCompression,
                IsNew       = isNew,
            };
            Project.Sidecar.Replacements.Add(replacement);
        }
        replacement.SourcePath        = sourcePath;
        replacement.AddedAt           = DateTime.UtcNow;
        replacement.Authored          = null;   // a file of one's own, not a material made here
        replacement.CloneGameMaterial = false;
        Project.MarkSidecarDirty();
        MarkDirty();
        return replacement;
    }

    /// <summary>
    /// Makes a material here for <paramref name="gamePath"/>: from a bundled preset, or starting from
    /// the game's own material at that path when <paramref name="preset"/> is null — its shader,
    /// colour table and textures, each kept until an image of the user's own takes its place.
    /// </summary>
    public ResourceReplacement? AuthorMaterial(string gamePath, OptionRef option, MaterialPreset? preset, bool isNew)
    {
        if (Project == null)
            return null;

        if (preset != null)
        {
            var material = MaterialSetup.FromPreset(preset, Path.GetFileNameWithoutExtension(gamePath));
            ApplyTextureDefaults(material.Textures);
            var made = PutMaterial(gamePath, option, material, isNew, MaterialStart.Preset, string.Empty);
            Notify($"{Path.GetFileName(gamePath)} is made here now — give its texture slots images of your own.", Severity.Info);
            return made;
        }

        var bytes = _host.GameData.GetVanillaFileBytes(PathNormalizer.GamePath(gamePath));
        if (bytes == null)
        {
            Notify("The game has no material at this path to start from — pick a preset instead.", Severity.Warning);
            return null;
        }
        return CloneMaterial(gamePath, option, bytes, isNew, MaterialStart.Game, string.Empty, "the game's own");
    }

    /// <summary>
    /// Edits a material of the mod here: it starts from the mod's own file (or the game's, for one the
    /// mod only references) with its shader, settings and every texture kept, and only what is changed
    /// here — a slot's image, a slot added or taken out, the shader — makes it differ.
    /// </summary>
    public ResourceReplacement? EditMaterial(ResourceNode node)
    {
        if (Project == null || node.Kind != ResourceKind.Material)
            return null;

        var option = OptionFor(node);
        var own = node.IsModContent ? ReadOwnFile(option, node.GamePath) : null;
        var bytes = own ?? _host.GameData.GetVanillaFileBytes(node.GamePath);
        if (bytes == null)
        {
            Notify("Neither the mod nor the game has this material to start from — make one from a preset instead.", Severity.Warning);
            return null;
        }
        return CloneMaterial(node.GamePath, option, bytes, isNew: !node.IsModContent || node.Replacement?.IsNew == true,
            own != null ? MaterialStart.Mod : MaterialStart.Game, string.Empty, own != null ? "the mod's own" : "the game's own");
    }

    /// <summary>
    /// A material of the mod's own that did not exist before, at <paramref name="gamePath"/>: from a
    /// bundled preset, or as a copy of another material (<paramref name="copyOf"/>, the mod's or the
    /// game's) that shares its textures until they are swapped here.
    /// </summary>
    public ResourceReplacement? NewMaterial(string gamePath, OptionRef option, MaterialPreset? preset, string? copyOf)
    {
        if (Project == null)
            return null;
        gamePath = PathNormalizer.GamePath(gamePath);

        if (copyOf == null)
        {
            var material = MaterialSetup.FromPreset(preset ?? MaterialPresets.All[0], Path.GetFileNameWithoutExtension(gamePath));
            ApplyTextureDefaults(material.Textures);
            return PutMaterial(gamePath, option, material, isNew: true, MaterialStart.Preset, string.Empty);
        }

        copyOf = PathNormalizer.GamePath(copyOf);
        var own = CurrentFile(option, copyOf);
        var bytes = own ?? _host.GameData.GetVanillaFileBytes(copyOf);
        if (bytes == null)
        {
            Notify($"Neither the mod nor the game has {Path.GetFileName(copyOf)} to copy.", Severity.Warning);
            return null;
        }
        return CloneMaterial(gamePath, option, bytes, isNew: true, own != null ? MaterialStart.Mod : MaterialStart.Game, copyOf,
            $"a copy of {Path.GetFileName(copyOf)}");
    }

    private enum MaterialStart { Preset, Game, Mod }

    /// <summary>A material made here from another one's bytes: its shader, and each of its textures kept as a slot.</summary>
    private ResourceReplacement? CloneMaterial(string gamePath, OptionRef option, byte[] bytes, bool isNew, MaterialStart start, string templatePath, string what)
    {
        MtrlInfo info;
        try
        {
            info = MtrlReader.Read(bytes);
        }
        catch (Exception ex)
        {
            Notify($"The material could not be read: {ex.Message}", Severity.Warning);
            return null;
        }

        var material = new MaterialSetup
        {
            Name       = Path.GetFileNameWithoutExtension(gamePath),
            ShaderType = ShaderInfo.FromShaderPack(info.ShaderPackageName) ?? ShaderType.Character,
        };
        for (int i = 0; i < info.TextureOffsets.Length; i++)
        {
            if (MaterialPatcher.TypeOf(info, i) is not { } type || material.Textures.Any(t => t.Type == type))
                continue;
            material.Textures.Add(new TextureSlot
            {
                Type           = type,
                Postfix        = MaterialNaming.DefaultPostfix(type),
                SharedGamePath = PathNormalizer.GamePath(info.TextureOffsets[i].Path),
            });
        }
        ApplyTextureDefaults(material.Textures);

        var made = PutMaterial(gamePath, option, material, isNew, start, templatePath);
        Notify($"{Path.GetFileName(gamePath)} is edited here now, starting from {what} — swap any of its textures for images of your own.", Severity.Info);
        return made;
    }

    private ResourceReplacement? PutMaterial(string gamePath, OptionRef option, MaterialSetup material, bool isNew, MaterialStart start, string templatePath)
    {
        if (Project == null)
            return null;

        var replacement = FindReplacement(gamePath, option);
        if (replacement == null)
        {
            replacement = new ResourceReplacement { GamePath = PathNormalizer.GamePath(gamePath), Option = option.Serialize() };
            Project.Sidecar.Replacements.Add(replacement);
        }
        replacement.SourcePath        = string.Empty;
        replacement.IsNew             = isNew;
        replacement.Authored          = material;
        replacement.CloneGameMaterial = start == MaterialStart.Game;
        replacement.CloneModMaterial  = start == MaterialStart.Mod;
        replacement.TemplatePath      = start == MaterialStart.Preset ? string.Empty : PathNormalizer.GamePath(templatePath);
        replacement.MaterialNames.Clear();
        replacement.AddedAt           = DateTime.UtcNow;
        Project.MarkSidecarDirty();
        MarkDirty();
        if (!option.IsDefault)
            Focus(Target.ForResource(string.Empty, replacement.GamePath, replacement.Option));
        return replacement;
    }

    /// <summary>
    /// A postfix for a texture slot of a material made here whose texture path nothing else uses — not
    /// the mod, not the game, not another slot — so swapping one texture of an edited material never
    /// changes it for whatever else uses the texture the material had.
    /// </summary>
    public string FreePostfix(string materialGamePath, TextureType type, MaterialSetup material, TextureSlot? self = null)
    {
        var first = MaterialNaming.DefaultPostfix(type);
        for (int n = 1; n < 100; n++)
        {
            var postfix = n == 1 ? first : $"{first}{n}";
            var path = ResourceNaming.AuthoredTexturePath(materialGamePath, postfix);
            bool taken = material.Textures.Any(t => t != self && (t.IsShared ? PathNormalizer.SameGamePath(t.SharedGamePath, path) : t.Postfix == postfix))
                      || Graph.FindAll(path).Any(node => node.AuthoredSlot != self)
                      || _host.GameData.VanillaFiles.Exists(path);
            if (!taken)
                return postfix;
        }
        return $"{first}_ie";
    }

    /// <summary>A slot of a material made here that kept its texture takes an image of the user's own instead, at a texture path of its own.</summary>
    public void SwapKeptTexture(string materialGamePath, MaterialSetup material, TextureSlot slot, string image)
    {
        if (slot.IsShared)
            slot.Postfix = FreePostfix(materialGamePath, slot.Type, material, slot);
        slot.SourcePath     = image;
        slot.SharedGamePath = string.Empty;
        slot.UseVariants    = slot.UseWhiteDummy = false;
        TouchReplacements();
    }

    /// <summary>Adds a texture slot of a role to a material made here, at a texture path of its own.</summary>
    public void AddTextureSlot(string materialGamePath, MaterialSetup material, TextureType type)
    {
        var slot = new TextureSlot { Type = type };
        slot.Postfix = FreePostfix(materialGamePath, type, material, slot);
        if (_host.Configuration.DefaultCompression != TextureCompression.None)
            slot.Compression = _host.Configuration.DefaultCompression;
        material.Textures.Add(slot);
        TouchReplacements();
    }

    /// <summary>
    /// Switches a material made here to a bundled preset of another shader. Its slots stay (kept
    /// textures stay kept), but the colour table and settings of what it started from are not carried
    /// over — a preset has its own.
    /// </summary>
    public void ChangeShader(ResourceReplacement replacement, ShaderType shader)
    {
        if (replacement.Authored is not { } material || material.ShaderType == shader)
            return;
        material.ShaderType            = shader;
        replacement.CloneGameMaterial  = false;
        replacement.CloneModMaterial   = false;
        replacement.TemplatePath       = string.Empty;
        TouchReplacements();
    }

    /// <summary>
    /// Points material slot <paramref name="slot"/> of a model at <paramref name="material"/> (named
    /// the way a model names one: "/mt_c0201e6114_top_a.mtrl"); an empty name gives the slot back
    /// what its file has. The model is written into the mod with only those names changed.
    /// </summary>
    public void ReassignMaterial(ResourceNode model, int slot, string material)
    {
        if (Project == null || model.Kind != ResourceKind.Model || slot < 0)
            return;

        var option = OptionFor(model);
        var name = MaterialReference(material);
        if (slot < model.MaterialSlots.Count && string.Equals(model.MaterialSlots[slot], name, StringComparison.Ordinal))
            name = string.Empty;   // what the file has anyway

        var replacement = FindReplacement(model.GamePath, option);
        if (replacement == null)
        {
            if (name.Length == 0)
                return;
            replacement = new ResourceReplacement
            {
                GamePath    = PathNormalizer.GamePath(model.GamePath),
                Option      = option.Serialize(),
                Compression = _host.Configuration.DefaultCompression,
                IsNew       = model.Origin is OriginKind.Vanilla or OriginKind.Missing,
            };
            Project.Sidecar.Replacements.Add(replacement);
        }

        var names = replacement.MaterialNames;
        while (names.Count <= slot)
            names.Add(string.Empty);
        names[slot] = name;
        while (names.Count > 0 && string.IsNullOrWhiteSpace(names[^1]))
            names.RemoveAt(names.Count - 1);

        if (replacement.Authored == null && string.IsNullOrWhiteSpace(replacement.SourcePath) && !replacement.ReassignsMaterials)
            Project.Sidecar.Replacements.Remove(replacement);
        Project.MarkSidecarDirty();
        MarkDirty();
        if (!option.IsDefault && Project.Sidecar.Replacements.Contains(replacement))
            Focus(Target.ForResource(string.Empty, replacement.GamePath, replacement.Option));
    }

    /// <summary>Gives every material slot of a model back what its file has.</summary>
    public void ResetMaterials(ResourceNode model)
    {
        if (Project == null || FindReplacement(model.GamePath, OptionFor(model)) is not { } replacement)
            return;
        replacement.MaterialNames.Clear();
        if (replacement.Authored == null && string.IsNullOrWhiteSpace(replacement.SourcePath))
            Project.Sidecar.Replacements.Remove(replacement);
        Project.MarkSidecarDirty();
        MarkDirty();
    }

    /// <summary>A material name the way a model names one: forward slashes, a leading slash when it is just a file name, ".mtrl" at the end.</summary>
    public static string MaterialReference(string name)
    {
        var text = name.Trim().Replace('\\', '/');
        if (text.Length == 0)
            return string.Empty;
        if (!text.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase))
            text += ".mtrl";
        return text.TrimStart('/').Contains('/') ? text : "/" + text.TrimStart('/');
    }

    /// <summary>
    /// The option a file goes into when it is put where the mod only references the game's file: the
    /// option of the item added from the game it belongs to, else the mod's default files.
    /// </summary>
    public OptionRef ProvideOption(string gamePath)
    {
        if (Project == null || ResourceNaming.SetKey(PathNormalizer.GamePath(gamePath)) is not { } set)
            return OptionRef.Default;
        foreach (var item in Project.Sidecar.AddedItems)
        {
            if (!item.Models.Any(m => ResourceNaming.SetKey(m) == set))
                continue;
            var option = OptionRef.Parse(item.Option);
            return Project.Meta.OptionExists(option) ? option : OptionRef.Default;
        }
        return OptionRef.Default;
    }

    /// <summary>
    /// The option a replacement of <paramref name="node"/> goes into: a file of the mod's own stays in
    /// its option; a game file goes into the option of the added item it is listed under (found up the
    /// tree), and into the default files when it is the mod's own files that use it.
    /// </summary>
    public OptionRef OptionFor(ResourceNode node)
    {
        if (node.IsModContent)
            return node.Option;

        var seen = new HashSet<ResourceNode>();
        var pending = new Stack<ResourceNode>();
        pending.Push(node);
        while (pending.Count > 0 && seen.Count < 256)
        {
            var current = pending.Pop();
            if (!seen.Add(current))
                continue;
            if (current.Pinned)
                return ProvideOption(current.GamePath);
            if (current.IsModContent)
            {
                // An added item's model once a file of the user's own took its place.
                if (!current.Option.IsDefault && ProvideOption(current.GamePath) == current.Option)
                    return current.Option;
                continue;
            }
            foreach (var parent in Graph.ParentsOf(current))
                pending.Push(parent);
        }
        return OptionRef.Default;
    }

    /// <summary>The bytes of the mod's own file at a game path — never what this plugin wrote over it.</summary>
    private byte[]? ReadOwnFile(OptionRef option, string gamePath)
    {
        if (Project is not { } project)
            return null;
        var key = WriteConflict.KeyFor(option, gamePath);
        var written = project.Sidecar.Ledger.Files.FirstOrDefault(f => WriteConflict.KeyFor(OptionRef.Parse(f.Option), f.GamePath) == key);
        string? rel = written != null ? written.OriginalRelPath : project.Meta.TryGetFile(option, gamePath, out var current) ? current : null;
        return ReadRel(rel);
    }

    /// <summary>The bytes of the file the mod serves at a game path now, in an option or else its default files.</summary>
    private byte[]? CurrentFile(OptionRef option, string gamePath)
    {
        if (Project is not { } project)
            return null;
        foreach (var where in option.IsDefault ? new[] { option } : new[] { option, OptionRef.Default })
            if (project.Meta.TryGetFile(where, gamePath, out var rel) && ReadRel(rel) is { } bytes)
                return bytes;
        return null;
    }

    private byte[]? ReadRel(string? rel)
    {
        if (Project is not { } project || string.IsNullOrEmpty(rel) || !PathNormalizer.IsSafeRel(rel))
            return null;
        try
        {
            var full = PathNormalizer.ToDiskPath(project.FolderPath, rel);
            return File.Exists(full) ? File.ReadAllBytes(full) : null;
        }
        catch (Exception ex)
        {
            StudioServices.Log.Debug(ex, "[Studio] Could not read {0}", rel);
            return null;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // The mod's groups and options
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Opens what an option (or the default files) carries.</summary>
    public void FocusOption(OptionRef option) => Focus(Target.ForRoot(ResourceGraph.OptionRootId(option)));

    /// <summary>
    /// Set when the last change of meta.json's structure could not be written (nothing changed then,
    /// and the user was told). Dialogs clear it before a change and stay open while it is set.
    /// </summary>
    public bool StructureFailed { get; set; }

    /// <summary>
    /// Runs a structural edit of meta.json (see <see cref="ModProject.EditStructure"/>). False when
    /// it could not be written, or the edit itself failed: the open mod is left as it was, the user
    /// is told, and nothing is thrown at the window drawing it.
    /// </summary>
    private bool TryEditStructure(ModProject project, Action<PenumbraModMeta> edit)
    {
        try
        {
            project.EditStructure(edit);
            return true;
        }
        catch (Exception ex)
        {
            StudioServices.Log.Warning(ex, "[Studio] Could not change meta.json of {0}", project.FolderPath);
            Notify(ex is IOException or UnauthorizedAccessException
                ? $"meta.json could not be written, so nothing changed: {ex.Message}"
                : $"Nothing changed: {ex.Message}", Severity.Error);
            StructureFailed = true;
            return false;
        }
    }

    /// <summary>Opens every option of a group, one after the other.</summary>
    public void FocusGroup(string groupId) => Focus(Target.ForRoot(ResourceGraph.GroupRootId(groupId)));

    /// <summary>
    /// Makes options in one write of meta.json: in the group <paramref name="groupId"/>, or in a new
    /// group (<paramref name="newGroup"/>) made first. Returns them in the order of
    /// <paramref name="names"/>, or null when the group cannot take them all (nothing is written then).
    /// </summary>
    public List<OptionRef>? MakeOptions(string? groupId, (string Name, ModGroupType Type)? newGroup, IReadOnlyList<string> names)
    {
        if (Project is not { } project)
            return null;

        List<OptionRef>? made = null;
        string? problem = null;
        bool written = TryEditStructure(project, meta =>
        {
            var into = groupId;
            if (newGroup is { } fresh)
            {
                if (fresh.Type == ModGroupType.Multi && names.Count > PenumbraModMeta.MaxOptions(ModGroupType.Multi))
                {
                    problem = $"A multi-select group can have at most {PenumbraModMeta.MaxOptions(ModGroupType.Multi)} options.";
                    return;
                }
                into = meta.AddGroup(fresh.Name, fresh.Type);
            }
            if (into == null || meta.FindGroupView(into) is not { } group)
            {
                problem = "That group is no longer in the mod.";
                return;
            }
            if (group.Options.Count + names.Count > PenumbraModMeta.MaxOptions(group.Type))
            {
                problem = PenumbraModMeta.WhyNoNewOption(group) ?? $"{group.Name} has no room for {names.Count} more options.";
                return;
            }

            var list = new List<OptionRef>();
            foreach (var name in names)
                if (meta.AddOption(into, name) is { } option)
                    list.Add(option);
            made = list;
        });
        if (!written)
            return null;

        if (problem != null)
            Notify(problem, Severity.Warning);
        Revision++;
        return made;
    }

    /// <summary>
    /// Points <paramref name="option"/> at one of the mod's files for its game path (replacing what the
    /// option had there), or takes that entry out of the option again. The file stays in the mod folder.
    /// </summary>
    public void LinkFile(ResourceNode node, OptionRef option, bool link)
    {
        if (Project is not { } project || node.Origin != OriginKind.ModFile)
            return;
        if (!TryEditStructure(project, meta =>
            {
                if (link)
                    meta.SetFile(option, node.GamePath, node.RelPath);
                else if (meta.TryGetFile(option, node.GamePath, out var rel) && PathNormalizer.SameRel(rel, node.RelPath))
                    meta.RemoveFile(option, node.GamePath);
            }))
            return;
        Revision++;

        var label = project.Meta.OptionLabel(option);
        Notify(link ? $"{node.FileName} is used in {label} too." : $"{node.FileName} is no longer used in {label}.", Severity.Info);

        // Unlinked from the option it was picked in: show it where it still is.
        if (link || option != node.Option)
            return;
        foreach (var still in project.Meta.FileCarriers())
        {
            if (project.Meta.TryGetFile(still, node.GamePath, out var rel) && PathNormalizer.SameRel(rel, node.RelPath))
            {
                Focus(Target.ForResource(string.Empty, node.GamePath, still.Serialize()));
                break;
            }
        }
    }

    /// <summary>Makes an empty group of options; returns its id.</summary>
    public string? AddGroup(string name, ModGroupType type)
    {
        if (Project is not { } project)
            return null;
        string? id = null;
        if (!TryEditStructure(project, meta => id = meta.AddGroup(name, type)))
            return null;
        Revision++;
        return id;
    }

    public void RenameGroup(string groupId, string name)
    {
        if (Project is not { } project || name.Trim().Length == 0)
            return;
        if (TryEditStructure(project, meta => meta.RenameGroup(groupId, name.Trim())))
            Revision++;
    }

    /// <summary>
    /// Renames an option. Files written here for it before keep their folder in the mod (named after
    /// the option as it was); only what is written from now on goes under the new name.
    /// </summary>
    public void RenameOption(OptionRef option, string name)
    {
        if (Project is not { } project || name.Trim().Length == 0)
            return;
        if (TryEditStructure(project, meta => meta.RenameOption(option, name.Trim())))
            Revision++;
    }

    public void SetOptionDescription(OptionRef option, string description)
    {
        if (Project is not { } project)
            return;
        if (TryEditStructure(project, meta => meta.SetOptionDescription(option, description)))
            Revision++;
    }

    /// <summary>
    /// Takes an option out of the mod, with the replacements and items set up for it here; with
    /// <paramref name="deleteFiles"/>, its files leave the mod folder too (those nothing else uses).
    /// </summary>
    public void RemoveOption(OptionRef option, bool deleteFiles)
    {
        if (Project is not { } project || option.IsDefault)
            return;
        var label = project.Meta.OptionLabel(option);
        var moved = new Dictionary<OptionRef, OptionRef>();
        var files = new List<string>();
        bool removed = false;
        if (!TryEditStructure(project, meta =>
            {
                files = meta.FilesOf(option).Select(f => f.RelPath).ToList();
                removed = meta.RemoveOption(option, out moved);
            }) || !removed)
            return;
        Forget(project, new[] { option }, moved, deleteFiles ? files : new List<string>(), label);
    }

    /// <summary>Takes a whole group out of the mod; see <see cref="RemoveOption"/>.</summary>
    public void RemoveGroup(string groupId, bool deleteFiles)
    {
        if (Project is not { } project || project.Meta.FindGroupView(groupId) is not { } view)
            return;
        var moved = new Dictionary<OptionRef, OptionRef>();
        var files = new List<string>();
        var gone = view.FileSets.Select(o => o.Ref).ToList();
        bool removed = false;
        if (!TryEditStructure(project, meta =>
            {
                files = gone.SelectMany(o => meta.FilesOf(o)).Select(f => f.RelPath).ToList();
                removed = meta.RemoveGroup(groupId, out moved);
            }) || !removed)
            return;
        Forget(project, gone, moved, deleteFiles ? files : new List<string>(), view.Name);
    }

    /// <summary>
    /// After options left the mod (meta.json is written by now): what was set up here for them goes
    /// too, and whatever was addressed by position follows the options that moved up (see
    /// <see cref="ModProject.ForgetOptions"/>). <paramref name="deleteFiles"/> are the files they
    /// carried, deleted from the mod folder unless something still needs them.
    /// </summary>
    private void Forget(ModProject project, IReadOnlyCollection<OptionRef> gone, Dictionary<OptionRef, OptionRef> moved, List<string> deleteFiles, string label)
    {
        var result = project.ForgetOptions(gone, moved, deleteFiles,
            (rel, ex) => StudioServices.Log.Debug(ex, "[Studio] Could not delete {0}", rel));

        if (Selection is { Kind: TargetKind.Resource } selected
            && (ResourceGraph.OptionOf(selected.Root) is { } option && gone.Contains(option)
                || !string.IsNullOrEmpty(selected.Path) && gone.Contains(OptionRef.Parse(selected.Option))
                || ResourceGraph.GroupOf(selected.Root) is { } group && gone.Any(o => o.Group == group)))
            Selection = new Target(TargetKind.Project);

        if (result.DroppedReplacements > 0)
            MarkDirty();
        else
            Revision++;

        if (result.SaveError is { } error)
            Notify($"{label} is no longer in the mod. No file was deleted: {StudioProject.FileName} could not be saved ({error}).", Severity.Warning);
        else
            Notify($"{label} is no longer in the mod.", Severity.Info);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Items added from the game
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>The item added from the game with this subject key, if any.</summary>
    public AddedItem? FindAddedItem(uint key) => Project?.Sidecar.AddedItems.FirstOrDefault(i => i.Key == key);

    /// <summary>The items added from the game whose files are listed under a contents entry.</summary>
    public List<AddedItem> AddedItemsIn(ResourceRoot root)
    {
        if (Project == null || !root.Id.StartsWith("set:", StringComparison.Ordinal))
            return new List<AddedItem>();
        var set = root.Id[4..];
        return Project.Sidecar.AddedItems.Where(i => i.Models.Any(m => ResourceNaming.SetKey(m) == set)).ToList();
    }

    /// <summary>
    /// Adds a piece of gear or a character feature to the mod from the game: its models (one per race
    /// with its own) are listed in the tree with the game's materials and textures under them, ready to
    /// be replaced, edited or reassigned. What is put there goes into <paramref name="option"/>.
    /// </summary>
    public AddedItem? AddItem(PortSubject subject, OptionRef option)
    {
        if (Project == null)
            return null;
        if (FindAddedItem(subject.Key) is { } existing)
        {
            FocusItem(existing);
            return existing;
        }

        IEnumerable<RaceGender> races = subject.MultiRace
            ? _host.GameData.GetNativeRaces(subject).OrderBy(r => r.RaceCode, StringComparer.Ordinal)
            : subject.BaseRace is { } race ? new[] { race } : Array.Empty<RaceGender>();
        var models = races.Select(subject.ModelGamePath).Select(PathNormalizer.GamePath).Distinct(StringComparer.Ordinal).ToList();
        if (models.Count == 0 && subject is GearSubject)
            models = new[] { PlayerGender.Male, PlayerGender.Female }.Select(g => PathNormalizer.GamePath(subject.ModelGamePath(RaceInfo.BaseFor(g)))).ToList();
        if (models.Count == 0)
        {
            Notify($"The game has no model for {subject.DisplayName}.", Severity.Warning);
            return null;
        }

        var item = new AddedItem
        {
            Key    = subject.Key,
            Name   = subject.DisplayName,
            Option = (Project.Meta.OptionExists(option) ? option : OptionRef.Default).Serialize(),
            Models = models,
        };
        Project.Sidecar.AddedItems.Add(item);
        Project.MarkSidecarDirty();
        Revision++;
        Notify($"{subject.DisplayName} is in the mod now — drop your files on its models, materials and textures.", Severity.Info);
        FocusItem(item);
        return item;
    }

    /// <summary>The option the item browser adds items into.</summary>
    public OptionRef AddInto { get; set; }

    // The game's item browser adds to the open mod.
    string IItemShelf.ShelfName      => "mod";
    int    IItemShelf.ShelfCount     => Project?.Sidecar.AddedItems.Count ?? 0;
    uint   IItemShelf.ShelfSelection => 0;
    bool   IItemShelf.OnShelf(uint key) => FindAddedItem(key) != null;
    bool   IItemShelf.HasWork(uint key) => FindAddedItem(key) != null;
    void   IItemShelf.AddToShelf(PortSubject subject) => AddItem(subject, AddInto);

    /// <summary>Opens the entry an added item's files are listed under.</summary>
    public void FocusItem(AddedItem item)
    {
        if (item.Models.Count > 0 && ResourceNaming.SetKey(item.Models[0]) is { } set)
            Focus(Target.ForRoot($"set:{set}"));
    }

    /// <summary>
    /// The files put into the mod for an added item: what was added (not replaced) in its option, in its
    /// model set — counted only while no other added item shares that set and option.
    /// </summary>
    public List<ResourceReplacement> FilesAddedFor(AddedItem item)
    {
        if (Project == null || item.Models.Count == 0 || ResourceNaming.SetKey(item.Models[0]) is not { } set)
            return new List<ResourceReplacement>();
        var option = OptionRef.Parse(item.Option);
        bool shared = Project.Sidecar.AddedItems.Any(i => i != item && OptionRef.Parse(i.Option) == option
                                                         && i.Models.Any(m => ResourceNaming.SetKey(m) == set));
        if (shared)
            return new List<ResourceReplacement>();
        return Project.Sidecar.Replacements
            .Where(r => r.IsNew && OptionRef.Parse(r.Option) == option && ResourceNaming.SetKey(PathNormalizer.GamePath(r.GamePath)) == set)
            .ToList();
    }

    /// <summary>Takes an added item back out of the tree, and with <paramref name="withFiles"/> what was added for it (see <see cref="FilesAddedFor"/>).</summary>
    public void RemoveAddedItem(AddedItem item, bool withFiles)
    {
        if (Project == null)
            return;
        var files = withFiles ? FilesAddedFor(item) : new List<ResourceReplacement>();
        Project.Sidecar.AddedItems.Remove(item);
        foreach (var replacement in files)
            Project.Sidecar.Replacements.Remove(replacement);
        Project.MarkSidecarDirty();
        if (files.Count > 0)
            MarkDirty();
        else
            Revision++;
        Notify(files.Count > 0 ? $"{item.Name} and the {files.Count} file(s) added for it are taken back out." : $"{item.Name} is no longer listed.", Severity.Info);
    }

    private void ApplyTextureDefaults(IEnumerable<TextureSlot> slots)
    {
        var compression = _host.Configuration.DefaultCompression;
        if (compression == TextureCompression.None) return;
        foreach (var slot in slots)
            slot.Compression = compression;
    }

    /// <summary>Records that a replacement's settings were edited in place (its compression), so the sidecar is written.</summary>
    public void TouchReplacements()
    {
        Project?.MarkSidecarDirty();
        MarkDirty();
    }

    /// <summary>
    /// Drops a replacement or addition; the mod gets back what it had there. A model that also has
    /// its materials reassigned only loses the file of the user's own, and keeps the reassignment.
    /// </summary>
    public bool ClearReplacement(string gamePath, OptionRef option)
    {
        if (Project == null)
            return false;
        var replacement = FindReplacement(gamePath, option);
        if (replacement == null)
            return false;
        if (replacement.Authored == null && replacement.ReassignsMaterials && !string.IsNullOrWhiteSpace(replacement.SourcePath))
        {
            replacement.SourcePath = string.Empty;
            Project.MarkSidecarDirty();
            MarkDirty();
            return true;
        }
        Project.Sidecar.Replacements.Remove(replacement);
        Project.MarkSidecarDirty();
        MarkDirty();
        return true;
    }

    /// <summary>
    /// Takes a file out of an option of the mod right away (its meta.json entry; the file itself stays
    /// in the folder unless <paramref name="deleteFile"/> and nothing else uses it). Penumbra reads the
    /// mod again afterwards. <paramref name="expectedRel"/> is the file the user was asked about: meta.json
    /// is read again first, and when the entry points somewhere else by then (an option moved up in
    /// Penumbra, say), nothing is removed or deleted.
    /// </summary>
    public bool RemoveModFile(string gamePath, OptionRef option, bool deleteFile, string expectedRel)
    {
        if (Project is not { } project)
            return false;

        string? removedRel = null;
        bool changed = false;
        if (!TryEditStructure(project, meta => removedRel = RemoveFileIfStill(meta, option, gamePath, expectedRel, out changed)))
            return false;
        if (changed)
        {
            Notify($"{Path.GetFileName(gamePath)} was changed in the mod since you asked, so nothing was removed. Look again and remove it then.", Severity.Warning);
            return false;
        }
        if (removedRel == null)
            return false;

        ClearReplacement(gamePath, option);
        // Kept while anything else needs it — another entry, or an entry repointed here that goes back to it.
        if (deleteFile)
            project.DeleteUnused(new[] { removedRel },
                (_, ex) => Notify($"The entry was removed, but the file could not be deleted: {ex.Message}", Severity.Warning));
        Revision++;
        return true;
    }

    /// <summary>
    /// Takes the entry <paramref name="option"/> / <paramref name="gamePath"/> out of <paramref name="meta"/>
    /// when it still points at <paramref name="expectedRel"/>; returns the file it pointed at, or null.
    /// <paramref name="changed"/> says it points at another file now (left as it is).
    /// </summary>
    internal static string? RemoveFileIfStill(PenumbraModMeta meta, OptionRef option, string gamePath, string expectedRel, out bool changed)
    {
        changed = false;
        if (!meta.TryGetFile(option, gamePath, out var rel))
            return null;
        if (!PathNormalizer.SameRel(rel, expectedRel))
        {
            changed = true;
            return null;
        }
        return meta.RemoveFile(option, gamePath) ? rel : null;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Writing the mod
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Every local file the replacements are made from, including the images of materials made here.</summary>
    public List<string> SourcePaths()
    {
        var paths = new List<string>();
        if (Project != null)
        {
            foreach (var replacement in Project.Sidecar.Replacements)
            {
                if (replacement.Authored is { } material)
                    paths.AddRange(material.Textures.Where(t => !t.UseWhiteDummy).Select(t => t.SourcePath));
                else
                    paths.Add(replacement.SourcePath);
            }
        }
        paths.RemoveAll(string.IsNullOrWhiteSpace);
        return paths;
    }

    /// <summary>
    /// A deep copy of everything an update of the mod needs — every replaced resource, the mod's
    /// meta.json — taken on the render thread so the worker never reads what the UI is editing.
    /// </summary>
    public WriteRequest CreateWriteRequest(IReadOnlyDictionary<string, ConflictChoice> resolutions)
    {
        var project = Project ?? throw new InvalidOperationException("No mod is open.");
        project.RefreshFromDisk();   // lay it out on what is on disk now, if Penumbra changed it

        return new WriteRequest
        {
            ModPath           = project.FolderPath,
            ModFolderName     = project.FolderName,
            Meta              = project.Meta.Clone(),
            PreviousLedger    = project.Sidecar.Ledger.Clone(),
            Sidecar           = StudioProjectStore.DeepClone(project.Sidecar),
            CreatedHere      = project.CreatedHere,
            Replacements      = project.Sidecar.Replacements.Select(r => r.Clone()).ToList(),
            Vanilla           = _host.GameData.VanillaFiles,
            PluginDirectory   = _host.PluginDirectory,
            CacheDirectory    = _host.TextureCacheDirectory,
            ConvertTexture    = _host.PenumbraTextures.Available ? _host.PenumbraTextures.Convert : null,
            Resolutions       = new Dictionary<string, ConflictChoice>(resolutions),
            Revision          = SetUpRevision,
            SourcePaths       = SourcePaths(),
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Persistence
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Records that the replacements changed: saved shortly, and written into the mod once edits settle.</summary>
    public void MarkDirty()
    {
        Revision++;
        SetUpRevision++;
        _sinceSetUpChange.Restart();
        _dirty = true;
        _sinceDirty.Restart();
    }

    /// <summary>Called once per frame: writes pending edits once they have settled.</summary>
    public void FlushIfDue()
    {
        bool metaPending = Project is { MetaDirty: true };
        if ((_dirty || metaPending || Project is { SidecarDirty: true }) && _sinceDirty.Elapsed >= SaveDelay)
            Flush();
        else if ((metaPending || Project is { SidecarDirty: true }) && !_sinceDirty.IsRunning)
            _sinceDirty.Restart();
    }

    /// <summary>Writes pending edits now — before the mod is updated, on window close and plugin unload.</summary>
    public void Flush()
    {
        var project = Project;
        if (project == null)
        {
            _dirty = false;
            return;
        }

        if (_dirty)
        {
            _dirty = false;
            project.MarkSidecarDirty();
        }

        try
        {
            project.Flush();
        }
        catch (Exception ex)
        {
            StudioServices.Log.Warning(ex, "[Studio] Could not save {0}", project.FolderPath);
            Notify($"Could not save the mod's files: {ex.Message}", Severity.Error);
        }
        _sinceDirty.Reset();
    }
}
