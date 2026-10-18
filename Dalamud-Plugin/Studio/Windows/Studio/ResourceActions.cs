using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Models.Project;
using InstantEdit.Studio.Models.Resources;
using InstantEdit.Studio.Services;
using InstantEdit.Studio.Services.Project;
using InstantEdit.Studio.Services.Resources;
using InstantEdit.Studio.Windows.UI;
using ComboWheel = InstantEdit.Ui.ComboWheel;

namespace InstantEdit.Studio.Windows.Studio;

/// <summary>
/// Replacing and adding the mod's own resources, and shaping its groups and options: a file dropped on
/// (or picked for) a tile replaces that resource; files dropped on an entry, an option, a group or the
/// contents pane are added, after asking where each one goes in the game and which option carries it —
/// one option for all of them, a new option of a group for each, or each its own. Each change is
/// written into the mod a moment later, and again whenever the file itself changes.
/// </summary>
internal sealed class ResourceActions
{
    private const string AddPopupId          = "Add files to the mod##StudioAddFiles";
    private const string RemovePopupId       = "Remove from the mod?##StudioRemoveFile";
    private const string NewMaterialPopupId  = "New material##StudioNewMaterial";
    private const string RemoveItemPopupId   = "Take it back out?##StudioRemoveItem";
    private const string NewGroupPopupId     = "New group##StudioNewGroup";
    private const string NewOptionPopupId    = "New option##StudioNewOption";
    private const string RenamePopupId       = "Rename##StudioRename";
    private const string RemoveOptionPopupId = "Remove the option?##StudioRemoveOption";
    private const string RemoveGroupPopupId  = "Remove the group?##StudioRemoveGroup";
    private const string ClearPopupId        = "Throw away this material?##StudioClearMaterial";

    private readonly StudioHost _host;
    private readonly ModSession _session;

    /// <summary>Where the files being added go: all into one place, each into a new option of a group, or each where its row says.</summary>
    private enum AddMode { Together, OwnOptions, PerFile }

    private sealed class PendingAdd
    {
        public required string Source;
        public string GamePath = string.Empty;
        public OptionRef Option;
        public string OptionName = string.Empty;
    }

    private readonly List<PendingAdd> _adds = new();
    private AddMode _addMode;
    private OptionRef _addOption;
    private string? _addGroup;               // OwnOptions: the group the new options go into; null for a new group
    private string _newGroupName = string.Empty;
    private bool _newGroupMulti;
    private bool _samePath;
    private string _sharedPath = string.Empty;
    private ResourceRoot? _addContext;
    private bool _openAdd;

    /// <summary>A group being made, or an option being added to one.</summary>
    private string _newName = string.Empty;
    private bool _newMulti;
    private string _newOptionGroup = string.Empty;
    private bool _openNewGroup, _openNewOption;

    /// <summary>A group (by id) or an option being renamed.</summary>
    private string? _renameGroup;
    private OptionRef? _renameOption;
    private string _renameText = string.Empty;
    private bool _openRename;

    private OptionRef? _removeOption;
    private string? _removeGroup;
    private bool _removeOptionFiles;
    private bool _openRemoveOption, _openRemoveGroup;

    private (string GamePath, OptionRef Option, string RelPath)? _remove;
    private bool _removeDeletes;
    private bool _openRemove;

    /// <summary>A material made here about to be thrown away, once confirmed.</summary>
    private (string GamePath, OptionRef Option, string FileName, bool Added)? _clear;
    private bool _openClear;

    /// <summary>Set when a dialog's change of meta.json could not be written; the dialog stays open and says so.</summary>
    private bool _writeFailed;

    /// <summary>A material being made, and the model slot it is for (if any).</summary>
    private sealed class PendingMaterial
    {
        public string Folder = string.Empty;
        public string Name = string.Empty;
        public OptionRef Option;
        public string? CopyOf;
        public int Start;          // the copy first (when there is one), then each preset
        public string ModelPath = string.Empty;
        public OptionRef ModelOption;
        public int Slot = -1;
        public bool Assign = true;
    }

    private PendingMaterial? _material;
    private bool _openMaterial;

    private AddedItem? _removeItem;
    private bool _removeItemFiles = true;
    private bool _openRemoveItem;

    public ResourceActions(StudioHost host, ModSession session)
    {
        _host  = host;
        _session = session;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Replacing
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Whether a node stands for a file the mod (or the game) has that a local file may replace.</summary>
    public static bool CanReplace(ResourceNode node)
        => node.Origin is OriginKind.ModFile or OriginKind.Vanilla or OriginKind.Missing or OriginKind.Shared or OriginKind.Added
        && ResourceKinds.AcceptedSources(node.GamePath).Count > 0;

    /// <summary>Puts <paramref name="source"/> where <paramref name="node"/> is.</summary>
    public void Replace(ResourceNode node, string source)
    {
        if (!ResourceKinds.Accepts(node.GamePath, source))
        {
            _session.Notify($"{Path.GetFileName(source)} cannot stand in for {node.FileName}.", Severity.Warning);
            return;
        }

        // A game file the mod only references (or a missing one) is provided through the default files
        // — or the option of the item added from the game it belongs to.
        var option = _session.OptionFor(node);
        bool adds = node.Origin is OriginKind.Vanilla or OriginKind.Missing or OriginKind.Shared
                 || node.Origin == OriginKind.Added && node.Replacement?.IsNew == true;
        _session.SetReplacement(node.GamePath, option, source, adds);
        _session.Notify($"{node.FileName} ← {Path.GetFileName(source)}", Severity.Info);
        if (!option.IsDefault && !node.IsModContent)
            _session.Focus(Target.ForResource(string.Empty, PathNormalizer.GamePath(node.GamePath), option.Serialize()));
    }

    /// <summary>One file dropped on a tile replaces it; several become options at its game path, one each.</summary>
    public FileDropRule DropRuleFor(ResourceNode node)
        => new(p => p.Files.Count > 1 && p.Folders.Count == 0
                ? p.Files.All(f => ResourceKinds.Accepts(node.GamePath, f)) ? null
                  : $"Each file must be able to stand in for {node.FileName} — drop {string.Join(" / ", ResourceKinds.AcceptedSources(node.GamePath))} files."
                : FileDrop.RejectForResource(p, node.GamePath),
            p =>
            {
                if (p.Files.Count > 1)
                    BeginAddAsOptions(p.Files, node);
                else
                    Replace(node, p.Files[0]);
            });

    public void BrowseReplacement(ResourceNode node)
    {
        var start = StartFolder(node.Replacement?.SourcePath ?? node.DiskPath);
        void Done(bool ok, List<string> paths)
        {
            if (ok && paths.Count > 0)
                Replace(node, paths[0]);
        }

        if (start != null)
            Gui.Dialogs.OpenFileDialog($"Replace {node.FileName}", ResourceKinds.DialogFilter(node.GamePath), Done, 1, start, false);
        else
            Gui.Dialogs.OpenFileDialog($"Replace {node.FileName}", ResourceKinds.DialogFilter(node.GamePath), Done, 1);
    }

    /// <summary>
    /// Drops what is set up here for a resource. A material made here (its shader and every slot's
    /// image) is only thrown away after asking; anything else goes at once.
    /// </summary>
    public void ClearReplacement(ResourceNode node)
    {
        var option = _session.OptionFor(node);
        if (_session.FindReplacement(node.GamePath, option) is { Authored: not null })
        {
            _clear = (node.GamePath, option, node.FileName, node.Origin == OriginKind.Added);
            _openClear = true;
            return;
        }
        Clear(node.GamePath, option, node.FileName, node.Origin == OriginKind.Added);
    }

    private void Clear(string gamePath, OptionRef option, string fileName, bool added)
    {
        if (_session.ClearReplacement(gamePath, option))
            _session.Notify(added ? $"{fileName} is taken back out of the mod." : $"{fileName}: back to the mod's own file.", Severity.Info);
    }

    /// <summary>Asks before taking a file out of its option.</summary>
    public void AskRemove(ResourceNode node)
    {
        if (node.Origin != OriginKind.ModFile)
            return;
        _remove = (node.GamePath, node.Option, node.RelPath);
        _removeDeletes = false;
        _openRemove = true;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // New materials and added items
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts making a new material for a model: in the folder its slot's material lives in (or the
    /// model set's), named like that material with the next free letter, from a preset or as a copy
    /// of it — and, for a slot, loaded by that slot once made.
    /// </summary>
    public void BeginNewMaterial(ResourceNode model, int slot)
    {
        if (_session.Project == null)
            return;

        var current = slot >= 0 ? model.EffectiveMaterial(slot) : model.MaterialSlots.FirstOrDefault(n => n.Length > 0) ?? string.Empty;
        var resolved = ResolvedMaterial(model, current);
        var folder = resolved != null
            ? Path.GetDirectoryName(resolved.GamePath)?.Replace('\\', '/') ?? string.Empty
            : ResourceNaming.MaterialFolder(Path.GetFileName(current)) ?? ResourceNaming.FallbackMaterialFolder(model.GamePath);

        var option = _session.OptionFor(model);
        _material = new PendingMaterial
        {
            Folder      = folder,
            Name        = NextName(folder, Path.GetFileNameWithoutExtension(current), model),
            Option      = option,
            CopyOf      = resolved?.GamePath,
            ModelPath   = model.GamePath,
            ModelOption = option,
            Slot        = slot,
            Assign      = slot >= 0,
        };
        _openMaterial = true;
    }

    /// <summary>The material node a model's slot name resolves to in the graph, if any.</summary>
    public static ResourceNode? ResolvedMaterial(ResourceNode model, string name)
    {
        if (name.Length == 0)
            return null;
        var file = Path.GetFileName(name.Replace('\\', '/'));
        return model.Children.Select(c => c.Node)
            .FirstOrDefault(n => n.Kind == ResourceKind.Material && string.Equals(n.FileName, file, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A name for a new material: <paramref name="like"/> with its letter moved on until neither the mod nor the game has it yet.</summary>
    private string NextName(string folder, string like, ResourceNode model)
    {
        if (like.Length == 0)
            like = $"mt_{Path.GetFileNameWithoutExtension(model.GamePath)}_a";
        var stem = like.Length > 2 && like[^2] == '_' && char.IsLetter(like[^1]) ? like[..^1] : like + "_";
        for (char letter = 'a'; letter <= 'z'; letter++)
        {
            var name = stem + letter;
            var path = $"{folder}/{name}.mtrl";
            if (!TakenByMod(path) && !InGame(path))
                return name;
        }
        return stem + "new";
    }

    /// <summary>Whether the mod (or a replacement) already has a file at this game path.</summary>
    private bool TakenByMod(string gamePath)
    {
        var path = PathNormalizer.GamePath(gamePath);
        return _session.Graph.FindAll(path).Any(n => n.IsModContent)
            || _session.Project?.Sidecar.Replacements.Any(r => PathNormalizer.SameGamePath(r.GamePath, path)) == true;
    }

    /// <summary>Whether the game has a file at this path: a new material there would stand in for it everywhere it is used.</summary>
    private bool InGame(string gamePath) => _host.GameData.VanillaFiles.Exists(PathNormalizer.GamePath(gamePath));

    /// <summary>Asks before taking an item added from the game back out of the mod.</summary>
    public void AskRemoveItem(AddedItem item)
    {
        _removeItem = item;
        _removeItemFiles = true;
        _openRemoveItem = true;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Adding
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts adding files; asks where each goes, prefilled from <paramref name="context"/> (the entry
    /// they were dropped on), all into <paramref name="option"/> (the default files when not given).
    /// </summary>
    public void BeginAdd(IEnumerable<string> files, ResourceRoot? context, OptionRef? option = null)
    {
        if (!StartAdd(files, context))
            return;
        _addMode   = AddMode.Together;
        _addOption = option ?? context?.Option ?? OptionRef.Default;
        _openAdd   = true;
    }

    /// <summary>Starts adding files as new options of a group, one each, at the game path the group's options share when they share one.</summary>
    public void BeginAddToGroup(IEnumerable<string> files, string groupId)
    {
        var meta = _session.Project?.Meta;
        var group = meta?.FindGroupView(groupId);
        var sample = group?.FileSets.Select(o => _session.Graph.OptionRoot(o.Ref)).FirstOrDefault(r => r is { Nodes.Count: > 0 });
        if (meta == null || group == null || !StartAdd(files, sample))
            return;
        _addMode  = AddMode.OwnOptions;
        _addGroup = PenumbraModMeta.TakesOptions(group) ? group.Id : null;
        SuggestSharedPath(meta, group);
        _openAdd  = true;
    }

    /// <summary>
    /// Several files dropped on one file of the mod: each becomes an option at its game path — of the
    /// group the file's option is in, or of a new group named after the file.
    /// </summary>
    public void BeginAddAsOptions(IEnumerable<string> files, ResourceNode node)
    {
        var meta = _session.Project?.Meta;
        if (meta == null || !StartAdd(files, _session.Graph.FindRoot(node.RootId)))
            return;
        var option = _session.OptionFor(node);
        var group = option.IsDefault ? null : meta.FindGroupView(option.Group);
        _addMode      = AddMode.OwnOptions;
        _addGroup     = group != null && PenumbraModMeta.TakesOptions(group) ? group.Id : null;
        _newGroupName = Path.GetFileNameWithoutExtension(node.GamePath);
        _newGroupMulti = false;
        _samePath     = true;
        _sharedPath   = node.GamePath;
        _openAdd      = true;
    }

    /// <summary>The files to add, with a guess at where each goes; false (and a message) when none is left.</summary>
    private bool StartAdd(IEnumerable<string> files, ResourceRoot? context)
    {
        if (_session.Project == null)
            return false;
        _adds.Clear();
        _addContext   = context;
        _addGroup     = null;
        _newGroupName = string.Empty;
        _newGroupMulti = false;
        _samePath     = false;
        _sharedPath   = string.Empty;
        foreach (var file in files.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            _adds.Add(new PendingAdd
            {
                Source     = file,
                GamePath   = GuessGamePath(file, context),
                Option     = context?.Option ?? OptionRef.Default,
                OptionName = Path.GetFileNameWithoutExtension(file),
            });
        if (_adds.Count == 0)
            _session.Notify("Nothing to add — drop files, not folders.", Severity.Warning);
        return _adds.Count > 0;
    }

    /// <summary>
    /// When most of a group's options carry a file at the same game path that every file being added
    /// can stand in for (a texture switched between looks), the new options go there too.
    /// </summary>
    private void SuggestSharedPath(PenumbraModMeta meta, ModGroupView group)
    {
        var counts = group.FileSets
            .SelectMany(o => meta.FilesOf(o.Ref).Select(f => PathNormalizer.GamePath(f.GamePath)).Distinct())
            .GroupBy(p => p)
            .Select(g => (Path: g.Key, Count: g.Count()))
            .Where(p => _adds.All(a => ResourceKinds.Accepts(p.Path, a.Source)))
            .OrderByDescending(p => p.Count)
            .ToList();
        if (counts.Count > 0 && counts[0].Count * 2 >= group.FileSets.Count)
        {
            _samePath   = true;
            _sharedPath = counts[0].Path;
        }
    }

    public void BrowseAdd(ResourceRoot? context, OptionRef? option = null)
    {
        void Done(bool ok, List<string> paths)
        {
            if (ok && paths.Count > 0)
                BeginAdd(paths, context, option);
        }
        Gui.Dialogs.OpenFileDialog("Add files to the mod", ".*", Done, 64);
    }

    public void BrowseAddToGroup(string groupId)
    {
        void Done(bool ok, List<string> paths)
        {
            if (ok && paths.Count > 0)
                BeginAddToGroup(paths, groupId);
        }
        Gui.Dialogs.OpenFileDialog("Add files, one option each", ".*", Done, 256);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Groups and options
    // ─────────────────────────────────────────────────────────────────────────

    public void BeginNewGroup()
    {
        if (_session.Project == null)
            return;
        _newName = string.Empty;
        _newMulti = false;
        _openNewGroup = true;
    }

    public void BeginNewOption(string groupId)
    {
        if (_session.Project?.Meta.FindGroupView(groupId) is not { } group)
            return;
        if (PenumbraModMeta.WhyNoNewOption(group) is { } why)
        {
            _session.Notify(why, Severity.Warning);
            return;
        }
        _newOptionGroup = groupId;
        _newName = string.Empty;
        _openNewOption = true;
    }

    public void BeginRenameGroup(string groupId)
    {
        if (_session.Project?.Meta.FindGroupView(groupId) is not { } group)
            return;
        _renameGroup  = groupId;
        _renameOption = null;
        _renameText   = group.Name;
        _openRename   = true;
    }

    public void BeginRenameOption(OptionRef option)
    {
        if (_session.Project?.Meta is not { } meta || option.IsDefault)
            return;
        _renameGroup  = null;
        _renameOption = option;
        _renameText   = meta.OptionName(option);
        _openRename   = true;
    }

    public void AskRemoveOption(OptionRef option)
    {
        if (option.IsDefault)
            return;
        _removeOption = option;
        _removeOptionFiles = false;
        _openRemoveOption = true;
    }

    public void AskRemoveGroup(string groupId)
    {
        _removeGroup = groupId;
        _removeOptionFiles = false;
        _openRemoveGroup = true;
    }

    /// <summary>
    /// Where a file probably goes: an image becomes a .tex, and lands in the entry's model set folder
    /// (textures under texture/, materials under material/v0001/ or material/, models under model/) or next to the
    /// entry's other files. Without an entry the user types the path.
    /// </summary>
    private static string GuessGamePath(string source, ResourceRoot? context)
    {
        var name = Path.GetFileName(source).ToLowerInvariant();
        var ext = Path.GetExtension(name);
        if (ext is ".png" or ".jpg" or ".jpeg" or ".tga" or ".dds")
            name = Path.ChangeExtension(name, ".tex");
        var target = Path.GetExtension(name);

        var sample = context?.Nodes.FirstOrDefault(n => n.IsModContent)?.GamePath
                  ?? context?.Nodes.FirstOrDefault()?.GamePath;
        if (sample == null)
            return string.Empty;

        if (ResourceNaming.SetKey(sample) != null)
        {
            // The set folder of a real path (with its race code), not the grouping key.
            var set = sample;
            foreach (var marker in new[] { "/model/", "/material/", "/texture/", "/vfx/", "/animation/" })
            {
                int at = set.IndexOf(marker, StringComparison.Ordinal);
                if (at > 0)
                {
                    set = set[..at];
                    break;
                }
            }
            return target switch
            {
                ".tex"  => $"{set}/texture/{name}",
                ".mtrl" => $"{ResourceNaming.FallbackMaterialFolder($"{set}/model/{name}")}/{name}",   // faces and ears have no v0001
                ".mdl"  => $"{set}/model/{name}",
                _       => $"{set}/{name}",
            };
        }

        int slash = sample.LastIndexOf('/');
        return slash > 0 ? $"{sample[..slash]}/{name}" : name;
    }

    private const string BadSegments = "A game path can't have \".\" or \"..\" folders, or : * ? \" < > | in it.";

    private static string? PathProblem(string gamePath, string source)
    {
        var path = PathNormalizer.GamePath(gamePath);
        if (path.Length == 0)
            return "Where in the game should this go? Type its game path.";
        if (!PathNormalizer.LooksLikeGamePath(path))
            return "That does not look like a game path (folder/name.ext, forward slashes).";
        if (!PathNormalizer.HasStorableSegments(path))
            return BadSegments;
        if (!ResourceKinds.Accepts(path, source))
        {
            var accepted = ResourceKinds.AcceptedSources(path);
            return accepted.Count == 0
                ? "Nothing can be added at that kind of path."
                : $"A {Path.GetExtension(source)} file cannot become a {Path.GetExtension(path)} — use {string.Join(" / ", accepted)}.";
        }
        return null;
    }

    /// <summary>The game path a row's file goes to: the one shared by every file, or its own.</summary>
    private string PathOf(PendingAdd add) => PathNormalizer.GamePath(_samePath ? _sharedPath : add.GamePath);

    // ─────────────────────────────────────────────────────────────────────────
    // Popups
    // ─────────────────────────────────────────────────────────────────────────

    public void DrawPopups()
    {
        Open(ref _openAdd, AddPopupId);
        Open(ref _openRemove, RemovePopupId);
        Open(ref _openMaterial, NewMaterialPopupId);
        Open(ref _openRemoveItem, RemoveItemPopupId);
        Open(ref _openNewGroup, NewGroupPopupId);
        Open(ref _openNewOption, NewOptionPopupId);
        Open(ref _openRename, RenamePopupId);
        Open(ref _openRemoveOption, RemoveOptionPopupId);
        Open(ref _openRemoveGroup, RemoveGroupPopupId);
        Open(ref _openClear, ClearPopupId);

        DrawAddPopup();
        DrawRemovePopup();
        DrawNewMaterialPopup();
        DrawRemoveItemPopup();
        DrawNewGroupPopup();
        DrawNewOptionPopup();
        DrawRenamePopup();
        DrawRemoveOptionPopup();
        DrawRemoveGroupPopup();
        DrawClearPopup();

        void Open(ref bool flag, string id)
        {
            if (!flag)
                return;
            ImGui.OpenPopup(id);
            flag = false;
            _writeFailed = false;
        }
    }

    /// <summary>
    /// Makes a dialog's change of meta.json; true when the dialog can close. When it could not be
    /// written nothing changed: the dialog stays open and says so (the status line says why).
    /// </summary>
    private bool Wrote(Action change)
    {
        _session.StructureFailed = false;
        change();
        _writeFailed = _session.StructureFailed;
        return !_writeFailed;
    }

    /// <summary>The line a dialog shows after its change could not be written.</summary>
    private void DrawWriteFailure()
    {
        if (_writeFailed)
            ImGui.TextColored(Theme.Bad, "meta.json could not be written, so nothing changed. Try again.");
    }

    private void DrawClearPopup()
    {
        if (!ImGui.BeginPopupModal(ClearPopupId, ImGuiWindowFlags.AlwaysAutoResize))
            return;
        if (_session.Project == null || _clear is not { } clear)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        ImGui.TextUnformatted($"Throw away the material set up here for {clear.FileName}?");
        ImGui.TextColored(Theme.Muted, "Its shader and every texture slot's image go. This can't be undone.");
        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Bad);
        bool confirmed = ImGui.Button("Throw it away", new Vector2(Theme.S(130), 0));
        ImGui.PopStyleColor();
        if (confirmed)
        {
            Clear(clear.GamePath, clear.Option, clear.FileName, clear.Added);
            _clear = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(Theme.S(110), 0)))
        {
            _clear = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    // ── Groups and options ───────────────────────────────────────────────────

    private void DrawNewGroupPopup()
    {
        if (!ImGui.BeginPopupModal(NewGroupPopupId, ImGuiWindowFlags.AlwaysAutoResize))
            return;
        if (_session.Project is not { } project)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        Gui.HintWrapped("A group of options the player picks between in Penumbra. Drop files on it afterwards to make an option of each.");
        ImGui.Spacing();
        Gui.Label("Name");
        ImGui.SetNextItemWidth(Theme.S(320));
        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();
        bool enter = ImGui.InputTextWithHint("##groupname", "Colour, Style, Texture Slot A", ref _newName, 128, ImGuiInputTextFlags.EnterReturnsTrue);
        DrawGroupTypeChoice(ref _newMulti);

        var name = _newName.Trim();
        string? problem = name.Length == 0 ? "Give it a name."
                        : project.Meta.Groups.Any(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)) ? "The mod already has a group with that name."
                        : null;
        if (problem != null)
        {
            Gui.Label(string.Empty);
            ImGui.TextColored(Theme.Warn, problem);
        }

        DrawWriteFailure();
        ImGui.Spacing();
        using (Gui.Disabled(problem != null))
        {
            if ((Gui.PrimaryButton(FontAwesomeIcon.FolderPlus, "Make it", Theme.S(140)) || enter) && problem == null)
            {
                string? id = null;
                if (Wrote(() => id = _session.AddGroup(name, _newMulti ? ModGroupType.Multi : ModGroupType.Single)))
                {
                    if (id != null)
                    {
                        _session.Notify($"{name} is in the mod now — drop files on it to make its options.", Severity.Info);
                        _session.FocusGroup(id);
                    }
                    ImGui.CloseCurrentPopup();
                }
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(Theme.S(100), 0)))
            ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    /// <summary>Pick one (Single) or pick any (Multi), as two radio buttons after a label.</summary>
    private static void DrawGroupTypeChoice(ref bool multi)
    {
        Gui.Label("Players pick", "Penumbra's group types: Single (exactly one option at a time) or Multi (any number, at most 32 options).");
        if (ImGui.RadioButton("one option##single", !multi))
            multi = false;
        Gui.Tooltip(ResourceStyle.GroupTypeHelp(ModGroupType.Single));
        ImGui.SameLine();
        if (ImGui.RadioButton("any number##multi", multi))
            multi = true;
        Gui.Tooltip(ResourceStyle.GroupTypeHelp(ModGroupType.Multi));
    }

    private void DrawNewOptionPopup()
    {
        if (!ImGui.BeginPopupModal(NewOptionPopupId, ImGuiWindowFlags.AlwaysAutoResize))
            return;
        if (_session.Project?.Meta.FindGroupView(_newOptionGroup) is not { } group)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        ImGui.TextUnformatted($"A new option of {group.Name}");
        ImGui.TextColored(Theme.Muted, "Empty to begin with: drop files on it to fill it.");
        ImGui.Spacing();
        Gui.Label("Name");
        ImGui.SetNextItemWidth(Theme.S(320));
        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();
        bool enter = ImGui.InputTextWithHint("##optionname", "Red, Short, Matte", ref _newName, 128, ImGuiInputTextFlags.EnterReturnsTrue);

        var name = _newName.Trim();
        string? problem = name.Length == 0 ? "Give it a name."
                        : group.Options.Any(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase)) ? $"{group.Name} already has an option with that name."
                        : PenumbraModMeta.WhyNoNewOption(group);
        if (problem != null)
        {
            Gui.Label(string.Empty);
            ImGui.TextColored(Theme.Warn, problem);
        }

        DrawWriteFailure();
        ImGui.Spacing();
        using (Gui.Disabled(problem != null))
        {
            if ((Gui.PrimaryButton(FontAwesomeIcon.Plus, "Add it", Theme.S(140)) || enter) && problem == null)
            {
                List<OptionRef>? made = null;
                if (Wrote(() => made = _session.MakeOptions(group.Id, null, new[] { name })))
                {
                    if (made is [var option])
                        _session.FocusOption(option);
                    ImGui.CloseCurrentPopup();
                }
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(Theme.S(100), 0)))
            ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void DrawRenamePopup()
    {
        if (!ImGui.BeginPopupModal(RenamePopupId, ImGuiWindowFlags.AlwaysAutoResize))
            return;
        var meta = _session.Project?.Meta;
        var group = _renameGroup != null ? meta?.FindGroupView(_renameGroup) : _renameOption is { } o ? meta?.FindGroupView(o.Group) : null;
        if (meta == null || group == null)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        ImGui.TextUnformatted(_renameOption is { } renaming ? $"Rename {meta.OptionLabel(renaming)}" : $"Rename the group {group.Name}");
        ImGui.Spacing();
        ImGui.SetNextItemWidth(Theme.S(360));
        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();
        bool enter = ImGui.InputText("##rename", ref _renameText, 128, ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);

        var name = _renameText.Trim();
        string? problem = name.Length == 0 ? "Give it a name."
                        : _renameOption is { } option
                            ? group.Options.Any(x => x.Ref != option && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)) ? $"{group.Name} already has an option with that name." : null
                            : meta.Groups.Any(g => g.Id != group.Id && string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)) ? "The mod already has a group with that name." : null;
        if (problem != null)
            ImGui.TextColored(Theme.Warn, problem);
        else if (_renameOption == null)
            ImGui.TextColored(Theme.Faint, "Collections remember what is picked in a group by its name: after a rename it may need picking again.");

        DrawWriteFailure();
        ImGui.Spacing();
        using (Gui.Disabled(problem != null))
        {
            if ((Gui.PrimaryButton(FontAwesomeIcon.Check, "Rename", Theme.S(120)) || enter) && problem == null)
            {
                var renamed = _renameOption;
                if (Wrote(() =>
                    {
                        if (renamed is { } option)
                            _session.RenameOption(option, name);
                        else
                            _session.RenameGroup(group.Id, name);
                    }))
                    ImGui.CloseCurrentPopup();
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(Theme.S(100), 0)))
            ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void DrawRemoveOptionPopup()
    {
        if (!ImGui.BeginPopupModal(RemoveOptionPopupId, ImGuiWindowFlags.AlwaysAutoResize))
            return;
        var project = _session.Project;
        if (project == null || _removeOption is not { } option || !project.Meta.OptionExists(option))
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        ImGui.TextUnformatted($"Take {project.Meta.OptionLabel(option)} out of the mod?");
        int files = project.Meta.FilesOf(option).Count();
        int yours = project.Sidecar.Replacements.Count(r => OptionRef.Parse(r.Option) == option);
        DrawRemoveDetails(files, yours);

        if (ConfirmRemove() && Wrote(() => _session.RemoveOption(option, _removeOptionFiles)))
        {
            _removeOption = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private void DrawRemoveGroupPopup()
    {
        if (!ImGui.BeginPopupModal(RemoveGroupPopupId, ImGuiWindowFlags.AlwaysAutoResize))
            return;
        var project = _session.Project;
        if (project == null || _removeGroup == null || project.Meta.FindGroupView(_removeGroup) is not { } group)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        ImGui.TextUnformatted($"Take the group {group.Name} and its {group.Options.Count} option(s) out of the mod?");
        var sets = group.FileSets.Select(o => o.Ref).ToHashSet();
        int files = sets.Sum(o => project.Meta.FilesOf(o).Count());
        int yours = project.Sidecar.Replacements.Count(r => sets.Contains(OptionRef.Parse(r.Option)));
        DrawRemoveDetails(files, yours);

        if (ConfirmRemove() && Wrote(() => _session.RemoveGroup(group.Id, _removeOptionFiles)))
        {
            _removeGroup = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private void DrawRemoveDetails(int files, int yours)
    {
        ImGui.TextColored(Theme.Muted, files == 0 ? "It carries no files." : $"It carries {files} file(s); they stop being used.");
        if (yours > 0)
            ImGui.TextColored(Theme.Muted, $"The {yours} file(s) of your own set up for it here go too.");
        ImGui.TextColored(Theme.Muted, "This edits the mod's meta.json now (a copy of the original is kept the first time).");
        if (files > 0)
            ImGui.Checkbox("Also delete its files from the mod folder (those nothing else uses)", ref _removeOptionFiles);
        DrawWriteFailure();
        ImGui.Spacing();
    }

    /// <summary>A red Remove button and Cancel; true when Remove was clicked (Cancel closes the popup itself).</summary>
    private bool ConfirmRemove()
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Bad);
        bool confirmed = ImGui.Button("Remove", new Vector2(Theme.S(110), 0));
        ImGui.PopStyleColor();
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(Theme.S(110), 0)))
        {
            _removeOption = null;
            _removeGroup = null;
            ImGui.CloseCurrentPopup();
        }
        return confirmed;
    }

    private void DrawNewMaterialPopup()
    {
        ImGui.SetNextWindowSizeConstraints(new Vector2(Theme.S(520), 0), new Vector2(Theme.S(900), Theme.S(700)));
        if (!ImGui.BeginPopupModal(NewMaterialPopupId, ImGuiWindowFlags.AlwaysAutoResize))
            return;

        var project = _session.Project;
        if (project == null || _material is not { } m)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        Gui.HintWrapped("A material of the mod's own, written into the mod as you set it up. A model's slot only loads it once pointed at it.");
        ImGui.Spacing();

        Gui.Label("Name", "The file name, without .mtrl. For gear and character features the game finds a material by its name, so keep the part that names the item (mt_c0201e6114_a).");
        ImGui.SetNextItemWidth(Theme.S(360));
        ImGui.InputText("##mtrlname", ref m.Name, 128);
        Gui.Label("Folder", "Where it goes in the game: the material folder of the model's set, in the variant the item uses.");
        ImGui.SetNextItemWidth(Theme.S(360));
        ImGui.InputText("##mtrlfolder", ref m.Folder, 256);

        var name = MaterialNaming.SanitizeFileName(m.Name.Trim());
        if (name.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase))
            name = name[..^5];
        var path = PathNormalizer.GamePath($"{m.Folder.Trim().TrimEnd('/')}/{name}.mtrl");
        string? problem = name.Length == 0 ? "Give it a name."
                        : !PathNormalizer.LooksLikeGamePath(path) ? "That folder does not look like a game path (forward slashes, no drive)."
                        : !PathNormalizer.HasStorableSegments(path) ? BadSegments
                        : TakenByMod(path) ? "The mod already has a material there — edit that one instead, or pick another name."
                        : InGame(path) ? "The game already has a material with that name. Pick another name."
                        : null;
        Gui.Label(string.Empty);
        ImGui.TextColored(problem != null ? Theme.Warn : Theme.Faint, problem ?? path);

        ImGui.Spacing();
        Gui.Label("Start from", "A copy keeps the other material's shader, colour table and textures until you swap them; a preset starts clean.");
        var starts = new List<string>();
        if (m.CopyOf != null)
            starts.Add($"A copy of {Path.GetFileName(m.CopyOf)}");
        starts.AddRange(MaterialPresets.Names.Select(n => $"Preset: {n}"));
        m.Start = Math.Clamp(m.Start, 0, starts.Count - 1);
        ImGui.SetNextItemWidth(Theme.S(360));
        ComboWheel.Combo("##mtrlstart", ref m.Start, starts.ToArray());

        Gui.Label("Into", "Which part of the mod carries it: its default files, or one option of a group.");
        DrawOptionCombo("##mtrloption", project.Meta, ref m.Option);

        if (m.Slot >= 0)
        {
            Gui.Label(string.Empty);
            ImGui.Checkbox($"Use it for slot {m.Slot + 1} of {Path.GetFileName(m.ModelPath)}", ref m.Assign);
        }

        ImGui.Spacing();
        using (Gui.Disabled(problem != null))
        {
            if (Gui.PrimaryButton(FontAwesomeIcon.Plus, "Make it", Theme.S(140)))
            {
                bool copy = m.CopyOf != null && m.Start == 0;
                var preset = copy ? null : MaterialPresets.All[Math.Clamp(m.Start - (m.CopyOf != null ? 1 : 0), 0, MaterialPresets.All.Count - 1)];
                if (_session.NewMaterial(path, m.Option, preset, copy ? m.CopyOf : null) != null)
                {
                    bool assigned = false;
                    if (m.Assign && m.Slot >= 0 && _session.Graph.Find(m.ModelPath, m.ModelOption) is { } model)
                    {
                        // Named the way the slot names its material now: by file name, or by full path.
                        var reference = model.EffectiveMaterial(m.Slot).TrimStart('/').Contains('/') ? path : $"/{name}.mtrl";
                        _session.ReassignMaterial(model, m.Slot, reference);
                        assigned = true;
                    }
                    _session.Notify($"{name}.mtrl is in the mod now{(assigned ? $", loaded by slot {m.Slot + 1}" : string.Empty)} — set up its textures.", Severity.Info);
                    _session.Focus(Target.ForResource(string.Empty, path, m.Option.Serialize()));
                }
                _material = null;
                ImGui.CloseCurrentPopup();
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(Theme.S(100), 0)))
        {
            _material = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private void DrawRemoveItemPopup()
    {
        if (!ImGui.BeginPopupModal(RemoveItemPopupId, ImGuiWindowFlags.AlwaysAutoResize))
            return;

        if (_session.Project == null || _removeItem is not { } item)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        ImGui.TextUnformatted($"Take {item.Name} back out of the mod?");
        var files = _session.FilesAddedFor(item);
        if (files.Count > 0)
        {
            ImGui.Checkbox($"Also take out the {files.Count} file(s) added for it", ref _removeItemFiles);
            Gui.Tooltip(string.Join("\n", files.Take(30).Select(r => r.GamePath)));
            if (!_removeItemFiles)
                ImGui.TextColored(Theme.Muted, "They stay in the mod; only the game's own files stop being listed.");
        }
        else
        {
            ImGui.TextColored(Theme.Muted, "Its game files stop being listed. Nothing in the mod changes.");
        }

        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Bad);
        bool confirmed = ImGui.Button("Take it out", new Vector2(Theme.S(120), 0));
        ImGui.PopStyleColor();
        if (confirmed)
        {
            _session.RemoveAddedItem(item, _removeItemFiles && files.Count > 0);
            _removeItem = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(Theme.S(110), 0)))
        {
            _removeItem = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private void DrawAddPopup()
    {
        ImGui.SetNextWindowSizeConstraints(new Vector2(Theme.S(640), 0), new Vector2(Theme.S(1200), Theme.S(760)));
        if (!ImGui.BeginPopupModal(AddPopupId, ImGuiWindowFlags.AlwaysAutoResize))
            return;

        var project = _session.Project;
        if (project == null || _adds.Count == 0)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }
        var meta = project.Meta;

        Gui.HintWrapped("Say where in the game each file goes, and which part of the mod carries it. Images are converted to the game's texture format; every other file is copied in as it is.");
        ImGui.Spacing();

        // ── Where they go in the mod ─────────────────────────────────────────
        Gui.Label("Put them in", "Which part of the mod carries the files: one place for all of them, a new option of a group for each, or a place per file.");
        if (ImGui.RadioButton("one place##together", _addMode == AddMode.Together))
            _addMode = AddMode.Together;
        ImGui.SameLine();
        if (ImGui.RadioButton("a new option each##own", _addMode == AddMode.OwnOptions))
        {
            _addMode = AddMode.OwnOptions;
            if (_addGroup != null && meta.FindGroupView(_addGroup) is { } current && !_samePath)
                SuggestSharedPath(meta, current);
        }
        Gui.Tooltip("Each file becomes an option of its own in a group, named after the file — the way a mod switches between looks.");
        ImGui.SameLine();
        if (ImGui.RadioButton("a place per file##perfile", _addMode == AddMode.PerFile))
            _addMode = AddMode.PerFile;

        ModGroupView? group = null;
        switch (_addMode)
        {
            case AddMode.Together:
                Gui.Label("Into", "Its default files (always on), or one option of a group.");
                DrawOptionCombo("##addoption", meta, ref _addOption);
                break;

            case AddMode.OwnOptions:
                group = DrawGroupPicker(meta);
                break;
        }

        // ── Where they go in the game ────────────────────────────────────────
        ImGui.Spacing();
        Gui.Label("Game path", "Where in the game the files go. Options switching between looks all put their file at the same game path.");
        ImGui.Checkbox("the same for every file", ref _samePath);
        if (_samePath)
        {
            Gui.Label(string.Empty);
            if (_sharedPath.Length == 0 && _adds.Count > 0)
                _sharedPath = _adds[0].GamePath;
            ImGui.SetNextItemWidth(Theme.S(460));
            ImGui.InputTextWithHint("##sharedpath", "chara/common/texture/name.tex", ref _sharedPath, 512);
            ImGui.SameLine();
            DrawPathSuggestions(meta, group);
        }

        // ── The files ────────────────────────────────────────────────────────
        ImGui.Spacing();
        var problems = new Dictionary<int, string>();
        var notes = new Dictionary<int, string>();
        Check(meta, group, problems, notes);
        string? general = GeneralProblem(meta, group);

        int remove = -1;
        int columns = 2 + (_samePath ? 0 : 1) + (_addMode == AddMode.Together ? 0 : 1);
        float tableH = Math.Min(Theme.S(420), (ImGui.GetFrameHeightWithSpacing() + ImGui.GetTextLineHeightWithSpacing()) * (_adds.Count + 1));
        if (ImGui.BeginTable("##adds", columns, ImGuiTableFlags.RowBg | ImGuiTableFlags.PadOuterX | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY,
                new Vector2(Math.Max(Theme.S(620), ImGui.GetContentRegionAvail().X), tableH)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("File", ImGuiTableColumnFlags.WidthFixed, Theme.S(200));
            if (!_samePath)
                ImGui.TableSetupColumn("Game path", ImGuiTableColumnFlags.WidthStretch, 1f);
            if (_addMode == AddMode.OwnOptions)
                ImGui.TableSetupColumn("Option", ImGuiTableColumnFlags.WidthStretch, _samePath ? 1f : 0.6f);
            else if (_addMode == AddMode.PerFile)
                ImGui.TableSetupColumn("Into", ImGuiTableColumnFlags.WidthStretch, _samePath ? 1f : 0.6f);
            ImGui.TableSetupColumn("##x", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFrameHeight());
            ImGui.TableHeadersRow();

            for (int i = 0; i < _adds.Count; i++)
            {
                var add = _adds[i];
                ImGui.PushID(i);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(ResourceStyle.Fit(Path.GetFileName(add.Source), Theme.S(196)));
                Gui.Tooltip(add.Source);

                if (!_samePath)
                {
                    ImGui.TableNextColumn();
                    ImGui.SetNextItemWidth(-1);
                    ImGui.InputTextWithHint("##path", "chara/equipment/e0000/texture/name.tex", ref add.GamePath, 512);
                }
                if (_addMode == AddMode.OwnOptions)
                {
                    ImGui.TableNextColumn();
                    ImGui.SetNextItemWidth(-1);
                    ImGui.InputTextWithHint("##optname", "option name", ref add.OptionName, 128);
                }
                else if (_addMode == AddMode.PerFile)
                {
                    ImGui.TableNextColumn();
                    DrawOptionCombo("##into", meta, ref add.Option, -1);
                }

                ImGui.TableSetColumnIndex(columns - 1);
                if (Gui.IconButton(FontAwesomeIcon.Times, "drop", "Leave this file out"))
                    remove = i;

                // What is wrong (or worth knowing) about the row, on a line of its own under it.
                if (problems.TryGetValue(i, out var message) || notes.TryGetValue(i, out message))
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(Math.Min(1, columns - 2));
                    ImGui.TextColored(problems.ContainsKey(i) ? Theme.Warn : Theme.Muted, message);
                }
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
        if (remove >= 0)
            _adds.RemoveAt(remove);

        if (general != null)
            ImGui.TextColored(Theme.Warn, general);
        DrawWriteFailure();

        ImGui.Spacing();
        bool allGood = problems.Count == 0 && general == null && _adds.Count > 0;
        using (Gui.Disabled(!allGood))
        {
            var label = _addMode == AddMode.OwnOptions ? $"Add {_adds.Count} option(s)" : $"Add {_adds.Count} file(s)";
            if (Gui.PrimaryButton(FontAwesomeIcon.Plus, label, Theme.S(170)) && allGood)
            {
                bool done = false;
                if (Wrote(() => done = Commit(meta, group)) && done)
                {
                    _adds.Clear();
                    ImGui.CloseCurrentPopup();
                }
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(Theme.S(100), 0)))
        {
            _adds.Clear();
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    /// <summary>The group new options go into: one of the mod's that can take them, or a new one (named and typed here).</summary>
    private ModGroupView? DrawGroupPicker(PenumbraModMeta meta)
    {
        var group = _addGroup != null ? meta.FindGroupView(_addGroup) : null;
        if (group == null)
            _addGroup = null;

        Gui.Label("Group", "The group the new options go into. Players pick between its options in Penumbra.");
        ImGui.SetNextItemWidth(Theme.S(320));
        if (ImGui.BeginCombo("##addgroup", group != null ? $"{group.Name}  ({group.Options.Count} options)" : "A new group"))
        {
            if (ImGui.Selectable("A new group", group == null))
                _addGroup = null;
            foreach (var g in meta.Groups)
            {
                var why = PenumbraModMeta.WhyNoNewOption(g);
                using (Gui.Disabled(why != null))
                {
                    if (ImGui.Selectable($"{g.Name}  ({g.Options.Count} options, {ResourceStyle.GroupTypeLabel(g.Type)})##{g.Id}", g == group) && why == null)
                    {
                        _addGroup = g.Id;
                        SuggestSharedPath(meta, g);
                    }
                }
                Gui.Tooltip(why);
            }
            ImGui.EndCombo();
        }
        else if (ComboWheel.Steps() is var steps and not 0)
        {
            // "A new group" (null), then the groups that can take new options.
            ModGroupView?[] choices = [null, .. meta.Groups.Where(g => PenumbraModMeta.WhyNoNewOption(g) == null)];
            if (ComboWheel.Pick(choices, g => g?.Id == group?.Id, steps) is var picked && picked?.Id != group?.Id)
            {
                _addGroup = picked?.Id;
                if (picked != null)
                    SuggestSharedPath(meta, picked);
            }
        }

        if (group == null)
        {
            Gui.Label("Its name");
            ImGui.SetNextItemWidth(Theme.S(320));
            ImGui.InputTextWithHint("##addgroupname", "Colour, Style, Texture Slot A", ref _newGroupName, 128);
            DrawGroupTypeChoice(ref _newGroupMulti);
        }
        else
        {
            Gui.Label(string.Empty);
            ImGui.TextColored(Theme.Faint, $"{ResourceStyle.GroupTypeHelp(group.Type)} Each file becomes one more option of it.");
        }
        return group;
    }

    /// <summary>A small menu of game paths that fit: those the group's options use, else those of the entry the files were dropped on.</summary>
    private void DrawPathSuggestions(PenumbraModMeta meta, ModGroupView? group)
    {
        if (Gui.IconButton(FontAwesomeIcon.CaretDown, "pathsuggest", "Game paths the mod already uses here"))
            ImGui.OpenPopup("##pathsuggestions");
        if (!ImGui.BeginPopup("##pathsuggestions"))
            return;

        IEnumerable<string> paths = group != null
            ? group.FileSets.SelectMany(o => meta.FilesOf(o.Ref)).Select(f => PathNormalizer.GamePath(f.GamePath))
            : _addContext != null ? ContextPaths(_addContext) : Enumerable.Empty<string>();
        var counted = paths.GroupBy(p => p)
            .Where(g => _adds.All(a => ResourceKinds.Accepts(g.Key, a.Source)))
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Take(40)
            .ToList();
        if (counted.Count == 0)
            ImGui.TextColored(Theme.Muted, "Nothing here the files could go to.");
        foreach (var path in counted)
        {
            if (ImGui.Selectable(path.Key))
                _sharedPath = path.Key;
            if (path.Count() > 1)
            {
                ImGui.SameLine();
                ImGui.TextColored(Theme.Faint, $"{path.Count()} options");
            }
        }
        ImGui.EndPopup();
    }

    /// <summary>The game paths of the mod's own files listed under an entry.</summary>
    private static IEnumerable<string> ContextPaths(ResourceRoot root)
    {
        var seen = new HashSet<ResourceNode>();
        var stack = new Stack<ResourceNode>(root.Nodes);
        while (stack.Count > 0 && seen.Count < 2000)
        {
            var node = stack.Pop();
            if (!seen.Add(node))
                continue;
            if (node.IsModContent)
                yield return node.GamePath;
            foreach (var child in node.Children)
                stack.Push(child.Node);
        }
    }

    /// <summary>What is wrong with each row (it cannot be added like that), and what is worth saying about it.</summary>
    private void Check(PenumbraModMeta meta, ModGroupView? group, Dictionary<int, string> problems, Dictionary<int, string> notes)
    {
        var places = new Dictionary<(OptionRef, string), int>();
        var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < _adds.Count; i++)
        {
            var add = _adds[i];
            var path = PathOf(add);
            if (PathProblem(path, add.Source) is { } problem)
            {
                problems[i] = problem;
                continue;
            }

            if (_addMode == AddMode.OwnOptions)
            {
                var name = add.OptionName.Trim();
                if (name.Length == 0)
                    problems[i] = "Give the option a name.";
                else if (group != null && group.Options.Any(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase)))
                    problems[i] = $"{group.Name} already has an option called {name}.";
                else if (names.TryGetValue(name, out var other))
                    problems[i] = $"{Path.GetFileName(_adds[other].Source)} gets an option of that name too.";
                else
                    names[name] = i;
                continue;
            }

            var option = _addMode == AddMode.Together ? _addOption : add.Option;
            if (places.TryGetValue((option, path), out var first))
                problems[i] = $"{Path.GetFileName(_adds[first].Source)} goes to the same place — one option has one file per game path.";
            else
            {
                places[(option, path)] = i;
                if (meta.TryGetFile(option, path, out _))
                    notes[i] = $"{meta.OptionLabel(option)} already has this file — it is replaced.";
            }
        }
    }

    /// <summary>What stops the whole set from being added, if anything.</summary>
    private string? GeneralProblem(PenumbraModMeta meta, ModGroupView? group)
    {
        if (_addMode == AddMode.Together && !meta.OptionExists(_addOption))
            return "The option picked is no longer in the mod.";
        if (_addMode == AddMode.PerFile && _adds.FirstOrDefault(a => !meta.OptionExists(a.Option)) is { } gone)
            return $"The option picked for {Path.GetFileName(gone.Source)} is no longer in the mod.";
        if (_addMode != AddMode.OwnOptions)
            return null;

        if (group == null)
        {
            var name = _newGroupName.Trim();
            if (name.Length == 0)
                return "Give the new group a name.";
            if (meta.Groups.Any(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)))
                return "The mod already has a group with that name — pick it above instead.";
            if (_newGroupMulti && _adds.Count > PenumbraModMeta.MaxOptions(ModGroupType.Multi))
                return $"A group that lets players pick any number can have at most {PenumbraModMeta.MaxOptions(ModGroupType.Multi)} options.";
            return null;
        }
        if (group.Options.Count + _adds.Count > PenumbraModMeta.MaxOptions(group.Type))
            return $"{group.Name} can have at most {PenumbraModMeta.MaxOptions(group.Type)} options; it has {group.Options.Count}.";
        return null;
    }

    /// <summary>Makes what the dialog says: new options first when it asks for them, then a replacement for each file. False when nothing was done.</summary>
    private bool Commit(PenumbraModMeta meta, ModGroupView? group)
    {
        if (_addMode == AddMode.OwnOptions)
        {
            var names = _adds.Select(a => a.OptionName.Trim()).ToList();
            var made = _session.MakeOptions(group?.Id,
                group == null ? (_newGroupName.Trim(), _newGroupMulti ? ModGroupType.Multi : ModGroupType.Single) : null, names);
            if (made == null || made.Count != _adds.Count)
                return false;
            for (int i = 0; i < _adds.Count; i++)
                _session.SetReplacement(PathOf(_adds[i]), made[i], _adds[i].Source, isNew: true);
            var groupName = group?.Name ?? _newGroupName.Trim();
            _session.Notify($"{made.Count} new option(s) in {groupName}, one per file.", Severity.Info);
            _session.FocusGroup(made[0].Group);
            return true;
        }

        foreach (var add in _adds)
        {
            var option = _addMode == AddMode.Together ? _addOption : add.Option;
            var path = PathOf(add);
            _session.SetReplacement(path, option, add.Source, isNew: !meta.TryGetFile(option, path, out _));
        }
        _session.Notify(_addMode == AddMode.Together
            ? $"Adding {_adds.Count} file(s) to {meta.OptionLabel(_addOption)}."
            : $"Adding {_adds.Count} file(s) to the mod.", Severity.Info);
        if (_adds.Count == 1)
            _session.Focus(Target.ForResource(string.Empty, PathOf(_adds[0]), (_addMode == AddMode.Together ? _addOption : _adds[0].Option).Serialize()));
        else if (_addMode == AddMode.Together && _session.ByOption)
            _session.FocusOption(_addOption);
        return true;
    }

    /// <summary>A combo of every place a file can go: the default files, then each group's options.</summary>
    public static bool DrawOptionCombo(string id, PenumbraModMeta meta, ref OptionRef option, float width = 0)
    {
        bool changed = false;
        ImGui.SetNextItemWidth(width != 0 ? width : Theme.S(320));
        if (ImGui.BeginCombo(id, meta.OptionLabel(option), ImGuiComboFlags.HeightLarge))
        {
            foreach (var carrier in meta.FileCarriers())
            {
                if (ImGui.Selectable(meta.OptionLabel(carrier), carrier == option))
                {
                    option = carrier;
                    changed = true;
                }
            }
            ImGui.EndCombo();
        }
        else if (ComboWheel.Steps() is var steps and not 0 && meta.FileCarriers().ToList() is { Count: > 0 } carriers)
        {
            var current = option;
            var picked = ComboWheel.Pick(carriers, carrier => carrier == current, steps);
            changed = picked != current;
            option = picked;
        }
        return changed;
    }

    private void DrawRemovePopup()
    {
        if (!ImGui.BeginPopupModal(RemovePopupId, ImGuiWindowFlags.AlwaysAutoResize))
            return;

        var project = _session.Project;
        if (project == null || _remove is not { } remove || !project.Meta.TryGetFile(remove.Option, remove.GamePath, out _))
        {
            _remove = null;
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        ImGui.TextUnformatted($"Take {Path.GetFileName(remove.GamePath)} out of {project.Meta.OptionLabel(remove.Option)}?");
        ImGui.TextColored(Theme.Muted, remove.GamePath);
        ImGui.Spacing();
        ImGui.TextColored(Theme.Muted, "This edits the mod's meta.json now (a copy of the original is kept the first time).");
        // Another entry may use the same file, or an entry repointed here goes back to it later.
        bool shared = project.RelKeysInUse(remove.Option, remove.GamePath).Contains(PathNormalizer.RelKey(remove.RelPath));
        using (Gui.Disabled(shared))
            ImGui.Checkbox("Also delete the file from the mod folder", ref _removeDeletes);
        if (shared)
            Gui.Tooltip("Something else in the mod still uses this file, so it stays.");
        DrawWriteFailure();

        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Bad);
        bool confirmed = ImGui.Button("Remove", new Vector2(Theme.S(110), 0));
        ImGui.PopStyleColor();
        bool removed = false;
        if (confirmed && Wrote(() => removed = _session.RemoveModFile(remove.GamePath, remove.Option, _removeDeletes && !shared, remove.RelPath)))
        {
            if (removed)
                _session.Notify($"Removed {Path.GetFileName(remove.GamePath)} from {project.Meta.OptionLabel(remove.Option)}.", Severity.Info);
            _remove = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(Theme.S(110), 0)))
        {
            _remove = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private static string? StartFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        try
        {
            if (Directory.Exists(path)) return path;
            var dir = Path.GetDirectoryName(path);
            return dir != null && Directory.Exists(dir) ? dir : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
