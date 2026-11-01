using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Models.Resources;
using InstantEdit.Studio.Services;
using InstantEdit.Studio.Services.Project;
using InstantEdit.Studio.Windows.UI;

namespace InstantEdit.Studio.Windows.Studio;

/// <summary>
/// Copy and paste between mod windows: files, an option (or the default files) or a whole group is
/// copied in one mod window and pasted into any open mod, this one included. Pasting asks for a
/// name and where the files go in the game: at their game paths, or moved to an item of this mod.
/// The files are copied in on a worker before meta.json is pointed at them.
/// </summary>
internal sealed class PasteActions
{
    private const string PastePopupId = "Paste##StudioPaste";

    private readonly StudioHost _host;
    private readonly ModEditor _editor;
    private readonly ModSession _session;

    // The paste being set up in its dialog.
    private ModClip? _clip;
    private PasteTarget _target;
    private string _name = string.Empty;
    private bool _move;
    private int _model;
    private List<string> _models = new();
    private (string Folder, string? Model)? _source;
    private bool _open;

    // What the dialog shows, worked out again when one of its inputs changes.
    private PastePlan? _plan;
    private string? _planError;
    private (string Name, bool Move, int Model, int Meta, ResourceGraph Graph)? _plannedFor;

    public PasteActions(ModEditor editor, ModSession session)
    {
        _host    = editor.Host;
        _editor  = editor;
        _session = session;
    }

    /// <summary>What was last copied in any mod window.</summary>
    public ModClip? Clip => _host.Clipboard;

    // ─────────────────────────────────────────────────────────────────────────
    // Copying
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>The Copy entries of a file of the mod: the file, and with the mod's files it uses when it uses any.</summary>
    public void CopyFileItems(ResourceNode node)
    {
        if (node.Origin != OriginKind.ModFile)
            return;
        var why = _editor.CanCopy(out var reason) ? null : reason;
        if (ImGui.MenuItem("Copy the file", string.Empty, false, why == null))
            CopyFiles(node, withUses: false);
        Gui.Tooltip(why);
        if (node.Children.Any(c => c.Node.Origin == OriginKind.ModFile))
        {
            if (ImGui.MenuItem("Copy with what it uses", string.Empty, false, why == null))
                CopyFiles(node, withUses: true);
            Gui.Tooltip(why ?? "The file and the mod's files under it, like a model's materials and textures.");
        }
    }

    /// <summary>The Copy entry of an option (or the default files, or a combination).</summary>
    public void CopyOptionItem(OptionRef option)
    {
        var meta = _session.Project?.Meta;
        var why = !_editor.CanCopy(out var reason) ? reason
                : meta != null && !option.IsDefault ? ModPaste.WhyNoCopy(meta, option.Group)
                : null;
        if (ImGui.MenuItem("Copy", string.Empty, false, why == null))
            CopyOption(option);
        Gui.Tooltip(why ?? "Its files, file swaps and manipulations.");
    }

    /// <summary>The Copy entry of a group.</summary>
    public void CopyGroupItem(ModGroupView group)
    {
        var meta = _session.Project?.Meta;
        var why = !_editor.CanCopy(out var reason) ? reason : meta != null ? ModPaste.WhyNoCopy(meta, group.Id) : null;
        if (ImGui.MenuItem("Copy the group", string.Empty, false, why == null))
            CopyGroup(group);
        Gui.Tooltip(why ?? "Its options and everything they carry.");
    }

    private void CopyFiles(ResourceNode node, bool withUses)
    {
        if (_session.Project is not { } project || !_editor.CanCopy(out _))
            return;
        var files = new List<(string GamePath, string RelPath)>();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<ResourceNode>();
        var pending = new Stack<ResourceNode>();
        pending.Push(node);
        while (pending.Count > 0 && seen.Count < 4096)
        {
            var current = pending.Pop();
            if (!seen.Add(current))
                continue;
            if (current.Origin == OriginKind.ModFile && current.RelPath.Length > 0 && paths.Add(PathNormalizer.GamePath(current.GamePath)))
                files.Add((current.GamePath, current.RelPath));
            if (withUses)
                foreach (var child in Enumerable.Reverse(current.Children))
                    pending.Push(child.Node);
        }

        _host.Clipboard = ModPaste.CopyFiles(project.Meta, project.FolderPath, _editor.Name, node.Option, files);
        _session.Notify(files.Count == 1
            ? $"Copied {node.FileName}. Right-click an option to paste it."
            : $"Copied {node.FileName} and {files.Count - 1} file(s) it uses. Right-click an option to paste them.", Severity.Info);
    }

    private void CopyOption(OptionRef option)
    {
        if (_session.Project is not { } project || !_editor.CanCopy(out _))
            return;
        if (ModPaste.CopyOption(project.Meta, project.FolderPath, _editor.Name, option) is not { } clip)
            return;
        _host.Clipboard = clip;
        _session.Notify($"Copied {project.Meta.OptionLabel(option)}. Right-click a group or an option to paste it.", Severity.Info);
    }

    private void CopyGroup(ModGroupView group)
    {
        if (_session.Project is not { } project || !_editor.CanCopy(out _))
            return;
        if (ModPaste.CopyGroup(project.Meta, project.FolderPath, _editor.Name, group.Id) is not { } clip)
            return;
        _host.Clipboard = clip;
        _session.Notify($"Copied the group {group.Name}. Right-click a mod's tile to paste it.", Severity.Info);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Pasting
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>The Paste entry of a menu, greyed out with the reason when what was copied can't go there.</summary>
    public void PasteItem(PasteTarget target)
    {
        var clip = Clip;
        var project = _session.Project;
        string? why = clip == null ? "Copy files, an option or a group first."
                    : project == null ? "The mod is closed."
                    : ModPaste.WhyNot(clip, target, project.Meta, project.FolderPath);
        if (why == null && !_editor.CanPaste(out var reason))
            why = reason;
        if (ImGui.MenuItem(clip == null ? "Paste" : Label(clip, target), string.Empty, false, why == null))
            BeginPaste(target);
        Gui.Tooltip(why ?? $"From {clip!.SourceName}.");
    }

    /// <summary>What pasting does, as a menu entry.</summary>
    private static string Label(ModClip clip, PasteTarget target) => (clip.Kind, target.Kind) switch
    {
        (ModClipKind.Group, PasteKind.NewGroup)   => $"Paste the group {clip.Name}",
        (ModClipKind.Option, PasteKind.NewOption) => $"Paste {clip.Name} as an option",
        (ModClipKind.Files, _) when clip.FileCount > 1 => $"Paste {clip.FileCount} files into it",
        (_, PasteKind.IntoOption)                 => $"Paste {clip.Name} into it",
        _                                         => "Paste",
    };

    /// <summary>Opens the paste dialog for what was copied, into <paramref name="target"/>.</summary>
    public void BeginPaste(PasteTarget target)
    {
        if (Clip is not { } clip || _session.Project is not { } project)
            return;
        var meta = project.Meta;
        if (ModPaste.WhyNot(clip, target, meta, project.FolderPath) is { } why)
        {
            _session.Notify(why, Severity.Warning);
            return;
        }

        _clip   = clip;
        _target = target;
        _name   = target.Kind switch
        {
            PasteKind.NewGroup  => ModPaste.UniqueName(meta.Groups.Select(g => g.Name), clip.Name),
            PasteKind.NewOption => ModPaste.UniqueName(meta.FindGroupView(target.GroupId)?.Options.Select(o => o.Name) ?? [], clip.Name),
            _                   => clip.Name,
        };
        _models = meta.AllFiles().Select(f => PathNormalizer.GamePath(f.GamePath))
            .Where(p => p.EndsWith(".mdl", StringComparison.Ordinal) && ModRetarget.ItemFolder(p) != null)
            .Distinct().Order(StringComparer.Ordinal).ToList();
        _source = ModRetarget.SourceOf(clip);
        _move   = false;
        // Another item's model first: that's where a move would go.
        _model  = Math.Max(0, _models.FindIndex(m => ModRetarget.ItemFolder(m) != _source?.Folder));
        _plannedFor = null;
        _open   = true;
    }

    /// <summary>Draws the paste dialog; called once per frame at the window root.</summary>
    public void DrawPopups()
    {
        if (_open)
        {
            ImGui.OpenPopup(PastePopupId);
            _open = false;
        }
        DrawPastePopup();
    }

    private void DrawPastePopup()
    {
        Gui.CenterNextPopup();
        ImGui.SetNextWindowSizeConstraints(new Vector2(Theme.S(480), 0), new Vector2(Theme.S(900), Theme.S(700)));
        if (!ImGui.BeginPopupModal(PastePopupId, ImGuiWindowFlags.AlwaysAutoResize))
            return;
        var project = _session.Project;
        var clip = _clip;
        if (project == null || clip == null || ModPaste.WhyNot(clip, _target, project.Meta, project.FolderPath) != null)
        {
            _clip = null;
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }
        var meta = project.Meta;
        var group = _target.Kind == PasteKind.NewOption ? meta.FindGroupView(_target.GroupId) : null;

        ImGui.TextUnformatted(_target.Kind switch
        {
            PasteKind.NewGroup  => $"Paste the group {clip.Name}",
            PasteKind.NewOption => $"Paste {clip.Name} into {group?.Name}",
            _                   => $"Paste {(clip.Kind == ModClipKind.Files && clip.FileCount > 1 ? $"{clip.FileCount} files" : clip.Name)} into {meta.OptionLabel(_target.Option)}",
        });
        ImGui.TextColored(Theme.Muted, $"From {clip.SourceName}");
        ImGui.Spacing();

        string? problem = null;
        if (_target.Kind != PasteKind.IntoOption)
        {
            Gui.Label("Name");
            ImGui.SetNextItemWidth(Theme.S(320));
            if (ImGui.IsWindowAppearing())
                ImGui.SetKeyboardFocusHere();
            ImGui.InputText("##pastename", ref _name, 128);
            var name = _name.Trim();
            problem = name.Length == 0 ? "Give it a name."
                    : _target.Kind == PasteKind.NewGroup && meta.Groups.Any(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase))
                        ? "The mod already has a group with that name."
                    : group != null && group.Options.Any(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase))
                        ? $"{group.Name} already has an option with that name."
                    : null;
        }

        DrawGamePaths();
        var retarget = Retarget(out var moveProblem);
        problem ??= _move ? moveProblem : null;

        var plan = PlanFor(project, retarget);
        ImGui.Spacing();
        DrawPlan(plan, retarget, meta);
        if (problem != null)
            ImGui.TextColored(Theme.Warn, problem);
        bool canPaste = _editor.CanPaste(out var busy);
        if (!canPaste)
            ImGui.TextColored(Theme.Warn, busy);

        ImGui.Spacing();
        bool ready = problem == null && canPaste && plan is { Blocked.Count: 0 } && _planError == null;
        using (Gui.Disabled(!ready))
        {
            if (Gui.PrimaryButton(FontAwesomeIcon.Paste, "Paste", Theme.S(120)) && ready)
            {
                // Worked out once more right before it goes: the mod may have changed since the dialog last looked.
                _plannedFor = null;
                if (PlanFor(project, retarget) is { Blocked.Count: 0 } fresh && _planError == null)
                {
                    _editor.Paste(fresh);
                    _clip = null;
                    ImGui.CloseCurrentPopup();
                }
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(Theme.S(100), 0)))
        {
            _clip = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    /// <summary>Keep the game paths, or move them to an item of this mod.</summary>
    private void DrawGamePaths()
    {
        var block = _source == null ? "None of its files belong to an item."
                  : _models.Count == 0 ? "This mod has no model to move them to."
                  : null;
        if (block != null)
            _move = false;

        Gui.Label("Game paths", "Where its files go in the game.");
        if (ImGui.RadioButton("Keep them##keeppaths", !_move))
            _move = false;
        Gui.Tooltip("Each file stays at its game path.");
        ImGui.SameLine();
        using (Gui.Disabled(block != null))
        {
            if (ImGui.RadioButton("Move to##movepaths", _move))
                _move = true;
        }
        Gui.Tooltip(block ?? $"Textures and materials of {ItemLabel(_source?.Folder)} move to the item you pick, as a mashup moves them.");
        if (block != null)
            return;

        ImGui.SameLine();
        ImGui.SetNextItemWidth(Theme.S(220));
        var labels = _models.Select(Path.GetFileName).Select(n => n ?? string.Empty).ToArray();
        using (Gui.Disabled(!_move))
            InstantEdit.Ui.ComboWheel.Combo("##pasteto", ref _model, labels);
        if (_model >= 0 && _model < _models.Count)
            Gui.Tooltip(_models[_model]);
    }

    /// <summary>The move the dialog is set to, or null for keeping the game paths.</summary>
    private ModRetarget? Retarget(out string? problem)
    {
        problem = null;
        if (!_move || _source is not { } source || _model < 0 || _model >= _models.Count)
            return null;
        var retarget = ModRetarget.Between(source.Folder, source.Model, _models[_model]);
        if (retarget == null)
            problem = "That model is in no item's folder.";
        else if (retarget.To == retarget.From)
            problem = "Its files are on that item already.";
        return problem == null ? retarget : null;
    }

    private PastePlan? PlanFor(Models.Project.ModProject project, ModRetarget? retarget)
    {
        var key = (_name.Trim(), _move, _model, project.Meta.Revision, _session.Graph);
        if (_plannedFor == key)
            return _plan;
        _plannedFor = key;

        var meta = project.Meta;
        var here = meta.AllFiles().Select(f => PathNormalizer.GamePath(f.GamePath)).ToHashSet(StringComparer.Ordinal);
        var graph = _session.Graph;
        try
        {
            _plan = ModPaste.Plan(_clip!, _target, _name.Trim().Length > 0 ? _name.Trim() : _clip!.Name, retarget, meta, project.FolderPath,
                project.RelKeysInUse(readDisk: true),
                path => here.Contains(PathNormalizer.GamePath(path)) || graph.FindAll(path).Any(),
                (option, path) => _session.FindReplacement(path, option) != null);
            _planError = null;
        }
        catch (Exception ex)
        {
            _plan = null;
            _planError = ex.Message;
        }
        return _plan;
    }

    private void DrawPlan(PastePlan? plan, ModRetarget? retarget, PenumbraModMeta meta)
    {
        if (plan == null)
        {
            if (_planError != null)
                ImGui.TextColored(Theme.Bad, _planError);
            return;
        }

        ImGui.TextUnformatted($"{plan.Entries} file(s), {plan.Used} at game paths this mod uses.");
        Gui.Tooltip("A file at a game path the mod has no use for may need moving to one of its items.");
        if (retarget != null)
        {
            ImGui.TextUnformatted($"{plan.Moved.Count} move from {ItemLabel(retarget.From)} to {ItemLabel(retarget.To)}.");
            if (plan.Moved.Count > 0)
                Gui.Tooltip(string.Join("\n", plan.Moved.Take(30).Select(m => $"{Path.GetFileName(m.From)} → {m.To}"))
                            + (plan.Moved.Count > 30 ? $"\nand {plan.Moved.Count - 30} more" : string.Empty));
            if (plan.Kept > 0)
                ImGui.TextColored(Theme.Muted, $"{plan.Kept} stay where they are: models and other files.");
            if (plan.HasExtras)
                ImGui.TextColored(Theme.Muted, "Its file swaps and manipulations stay as they are.");
        }
        if (plan.Missing.Count > 0)
        {
            ImGui.TextColored(Theme.Warn, $"{plan.Missing.Count} file(s) are missing in {plan.Clip.SourceName} and are left out.");
            Gui.Tooltip(string.Join("\n", plan.Missing.Take(30)));
        }
        if (plan.Replaced.Count > 0)
        {
            ImGui.TextColored(Theme.Warn, $"{meta.OptionLabel(plan.Target.Option)} has {plan.Replaced.Count} of these already. It will use the copies.");
            Gui.Tooltip(string.Join("\n", plan.Replaced.Take(30)));
        }
        if (plan.Blocked.Count > 0)
        {
            ImGui.TextColored(Theme.Bad, "A file of your own replaces one of these here. Undo that first.");
            Gui.Tooltip(string.Join("\n", plan.Blocked.Take(30)));
        }
    }

    /// <summary>An item's folder without "chara/" ("accessory/a0053").</summary>
    private static string ItemLabel(string? folder)
        => folder == null ? "its item" : folder.StartsWith("chara/", StringComparison.Ordinal) ? folder[6..] : folder;
}
