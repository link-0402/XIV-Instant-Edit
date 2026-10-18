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
/// The inspector for one of the mod's own files (or a game file it references): where it is, what it
/// is, a preview, what replaces it, what it uses and what uses it. A model's material slots can be
/// pointed at other materials here, and a material edited, made anew, or given new texture slots.
/// </summary>
internal sealed class ResourceInspector
{
    private const string ImageFilter = "Image files{.png,.jpg,.jpeg,.tga,.dds,.tex}";

    private readonly StudioHost _host;
    private readonly ModEditor _editor;
    private readonly ModSession _session;
    private readonly TextureThumbnailCache _thumbs;
    private readonly ResourceActions _actions;
    private readonly TextureSlotEditor _textures;
    private readonly HandoffActions _handoffs;

    private string _typedSource = string.Empty;
    private string? _typedFor;
    private int _preset;

    /// <summary>The texture slot whose path is being typed, and the text so far, until it is taken.</summary>
    private TextureSlot? _pathSlot;
    private string _pathText = string.Empty;

    /// <summary>What is being typed into a model's material slot, by node and slot, until it is taken.</summary>
    private readonly Dictionary<string, string> _slotText = new(StringComparer.Ordinal);

    public ResourceInspector(ModEditor editor, ModSession session, TextureThumbnailCache thumbs, ResourceActions actions, TextureSlotEditor textures, HandoffActions handoffs)
    {
        _host   = editor.Host;
        _editor = editor;
        _session  = session;
        _thumbs   = thumbs;
        _actions  = actions;
        _textures = textures;
        _handoffs = handoffs;
    }

    public void Draw(ResourceNode node)
    {
        var meta = _session.Project?.Meta;
        bool texture = node.Kind is ResourceKind.Texture or ResourceKind.VfxTexture or ResourceKind.Interface;

        using (Fonts.Title())
            ImGui.TextUnformatted(node.FileName);
        ResourceStyle.BadgeItem(new Badge(ResourceKinds.Label(node.Kind), Theme.Accent));
        ImGui.SameLine();
        ResourceStyle.BadgeItem(new Badge(ResourceStyle.OriginLabel(node.Origin), ResourceStyle.OriginColor(node.Origin), ResourceStyle.OriginHelp(node.Origin)));
        if (node.IsModContent && meta != null)
        {
            ImGui.SameLine();
            ResourceStyle.BadgeItem(new Badge(meta.OptionLabel(node.Option), Theme.Muted));
        }
        if (node.Pinned)
        {
            ImGui.SameLine();
            ResourceStyle.BadgeItem(new Badge("from the game", Theme.Accent, "Listed because its item was added to the mod from the game. Drop a file on it to put one of your own here."));
        }
        ImGui.Separator();
        ImGui.Spacing();

        if (texture && _host.Configuration.ShowThumbnails)
        {
            float side = Math.Min(Theme.S(200), ImGui.GetContentRegionAvail().X);
            if (node.Replacement is { } r && File.Exists(r.SourcePath))
                _thumbs.DrawFile(r.SourcePath, side);
            else if (node.Origin == OriginKind.ModFile)
                _thumbs.DrawFile(node.DiskPath, side);
            else if (node.Origin == OriginKind.Vanilla)
                _thumbs.DrawGame(node.GamePath, side);
            ImGui.Spacing();
        }

        // Instant Edit's hand-off: a model or animation to Blender, a texture to the image editor.
        if (_handoffs.For(node) is { } handoff)
        {
            using (Gui.Disabled(handoff.Block != null))
            {
                if (Gui.IconTextButton(handoff.Icon, handoff.Label, handoff.Block))
                    handoff.Run();
            }
            ImGui.Spacing();
        }

        Gui.LabeledValue("Game path", node.GamePath, "Where the game loads it from.");
        if (node.Origin == OriginKind.ModFile)
        {
            Gui.LabeledValue("In the mod", node.RelPath, "The file inside the mod folder that meta.json points this path at.");
            if (File.Exists(node.DiskPath))
            {
                Gui.Label(string.Empty);
                if (ImGui.SmallButton("Show in Explorer"))
                    ResourceTreeView.ShowInExplorer(node.DiskPath);
            }
        }
        if (node.Texture is { } tex)
            Gui.LabeledValue("Format", $"{tex.FormatName} · {tex.Size} · {tex.MipCount} mip(s)");
        if (node.Size > 0)
            Gui.LabeledValue("Size", ResourceTreeView.FormatSize(node.Size));
        if (node.Origin == OriginKind.Swap)
            Gui.LabeledValue("Served by", node.SwapTarget, "A file swap: the game loads this other game file here.");
        if (node.Note != null)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, node.Origin == OriginKind.Missing ? Theme.Bad : Theme.Muted);
            ImGui.TextWrapped(node.Note);
            ImGui.PopStyleColor();
        }
        if (node.Error != null)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Warn);
            ImGui.TextWrapped($"Could not be read, so what it uses is not known: {node.Error}");
            ImGui.PopStyleColor();
        }

        DrawChangedElsewhere(node);

        var authored = _session.FindReplacement(node.GamePath, _session.OptionFor(node))?.Authored;
        if (node.AuthoredSlot is { } slot)
        {
            ImGui.Spacing();
            ImGui.Spacing();
            Gui.SectionHeader("Made from", "This texture belongs to a material made here; its image is converted into the mod.");
            if (_textures.DrawCompact(slot, 0))
                _session.TouchReplacements();
        }
        else if (ResourceActions.CanReplace(node) && authored == null)
        {
            DrawReplace(node, texture);
        }
        if (node.Kind == ResourceKind.Model && node.MaterialSlots.Count > 0)
            DrawModelMaterials(node);
        if (node.Kind == ResourceKind.Material && node.Origin is OriginKind.ModFile or OriginKind.Vanilla or OriginKind.Missing or OriginKind.Shared or OriginKind.Added)
            DrawAuthoring(node);
        if (node.Origin == OriginKind.ModFile && meta != null)
            DrawLinkedOptions(node, meta);

        DrawLinks("Uses", node.Children.Select(c => (c.Node, c.Role)).ToList());
        var parents = _session.Graph.ParentsOf(node)
            .Select(n => (n, (string?)null))
            .ToList();
        DrawLinks("Used by", parents);

        if (node.Origin == OriginKind.ModFile)
        {
            ImGui.Spacing();
            ImGui.Spacing();
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Bad);
            if (Gui.IconTextButton(FontAwesomeIcon.Trash, "Remove from the mod", "Takes this file out of its option (meta.json is edited right away)."))
                _actions.AskRemove(node);
            ImGui.PopStyleColor();
        }
    }

    /// <summary>
    /// Which parts of the mod carry this very file: a box per place a file can go, ticked where meta.json
    /// points this game path at it. Ticking one points that option at the same file (replacing what it had
    /// there); unticking takes the entry out of that option. The file itself stays where it is.
    /// </summary>
    private void DrawLinkedOptions(ResourceNode node, PenumbraModMeta meta)
    {
        ImGui.Spacing();
        ImGui.Spacing();
        Gui.SectionHeader("In options", "The parts of the mod that use this very file at this game path. Tick another option to use it there too; untick one to stop using it there. meta.json is edited right away.");

        bool Has(OptionRef option, out bool other)
        {
            other = false;
            if (!meta.TryGetFile(option, node.GamePath, out var rel))
                return false;
            if (PathNormalizer.SameRel(rel, node.RelPath))
                return true;
            other = true;
            return false;
        }

        int linked = meta.FileCarriers().Count(o => Has(o, out _));
        void Box(OptionRef option, string label)
        {
            bool on = Has(option, out bool other);
            bool yours = _session.FindReplacement(node.GamePath, option) != null;
            bool last = on && linked <= 1;
            ImGui.PushID(option.Serialize());
            using (Gui.Disabled(yours || last))
            {
                if (ImGui.Checkbox(label, ref on))
                    _session.LinkFile(node, option, on);
            }
            if (yours)
                Gui.Tooltip("A file of your own is set up here at this game path; take it out first.");
            else if (last)
                Gui.Tooltip("The only place that uses this file. To take it out of the mod, use Remove below.");
            else if (other)
                Gui.Tooltip("This option has another file at this game path: ticking points it at this one instead.");
            if (other)
            {
                ImGui.SameLine();
                ImGui.TextColored(Theme.Faint, "(has its own)");
            }
            ImGui.PopID();
        }

        Box(OptionRef.Default, "Default files");
        foreach (var group in meta.Groups)
        {
            if (group.FileSets.Count == 0)
                continue;
            int here = group.FileSets.Count(o => Has(o.Ref, out _));
            ImGui.PushID(group.Id);
            ImGui.SetNextItemOpen(here > 0 && group.FileSets.Count <= 12, ImGuiCond.Appearing);
            bool open = ImGui.TreeNodeEx($"{group.Name}##linkgroup", ImGuiTreeNodeFlags.SpanAvailWidth);
            ImGui.SameLine();
            ImGui.TextColored(here > 0 ? Theme.Muted : Theme.Faint, $"{here} of {group.FileSets.Count}");
            if (open)
            {
                foreach (var set in group.FileSets.Take(500))
                    Box(set.Ref, set.Name);
                if (group.FileSets.Count > 500)
                    ImGui.TextColored(Theme.Faint, $"and {group.FileSets.Count - 500} more");
                ImGui.TreePop();
            }
            ImGui.PopID();
        }
    }

    /// <summary>
    /// A file written here that was changed elsewhere since: what that means, and — when its
    /// replacement here changed too — the choice of which to keep.
    /// </summary>
    private void DrawChangedElsewhere(ResourceNode node)
    {
        var sync = _editor.Sync;
        var conflict = node.IsModContent ? sync.ConflictFor(node.Option, node.GamePath) : null;
        if (conflict == null && !node.EditedElsewhere)
            return;

        ImGui.Spacing();
        ImGui.Spacing();
        if (conflict == null)
        {
            Gui.SectionHeader("Edited elsewhere");
            Gui.HintWrapped(ResourceTreeView.EditedElsewhereHelp(node));
            return;
        }

        Gui.SectionHeader("Changed elsewhere", "Changed outside XIV Instant Edit after it was written here, while its replacement here changed too.");
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Warn);
        ImGui.TextWrapped($"{conflict.Reason}.");
        ImGui.PopStyleColor();
        if (Gui.IconTextButton(FontAwesomeIcon.Check, "Use mine", "Write what is set up here over the change made elsewhere."))
            sync.Resolve(conflict, ConflictChoice.UseMine);
        ImGui.SameLine();
        if (Gui.IconTextButton(FontAwesomeIcon.Undo, "Keep theirs", "Keep the file as it was changed elsewhere; the replacement here leaves it alone until you change it again."))
            sync.Resolve(conflict, ConflictChoice.KeepTheirs);
    }

    private void DrawReplace(ResourceNode node, bool texture)
    {
        bool provides = node.Origin is OriginKind.Vanilla or OriginKind.Missing or OriginKind.Shared;
        ImGui.Spacing();
        ImGui.Spacing();
        Gui.SectionHeader(node.Origin == OriginKind.Added ? "Added from" : provides ? "Provide it" : "Replace it",
            provides
                ? "Put a file of your own at this game path: it is added to the mod."
                : "Put a file of your own in its place: it is written beside the mod's own file and the option is pointed at it, so the original is kept.");

        var option = _session.OptionFor(node);
        var replacement = _session.FindReplacement(node.GamePath, option);
        var source = replacement?.SourcePath ?? string.Empty;

        if (_typedFor != node.Id)
        {
            _typedFor = node.Id;
            _typedSource = source;
        }
        else if (replacement != null && !string.Equals(_typedSource, source, StringComparison.Ordinal) && !ImGui.IsAnyItemActive())
        {
            _typedSource = source;
        }

        Gui.Label("File");
        if (Gui.PathPicker("replace", ref _typedSource, "a file of your own — or drop one here", PathKind.File,
                ResourceKinds.DialogFilter(node.GamePath), picked => { _typedSource = picked; _actions.Replace(node, picked); },
                drop: _actions.DropRuleFor(node)))
        {
            // Typed: taken once it names a file that can stand in for this one.
            if (File.Exists(_typedSource) && ResourceKinds.Accepts(node.GamePath, _typedSource))
                _actions.Replace(node, _typedSource);
        }

        if (replacement == null || source.Length == 0)
        {
            Gui.HintWrapped(texture
                ? "png, jpeg, tga and dds images are converted to the game's format; a .tex is used as it is."
                : node.Kind == ResourceKind.Model
                    ? "A .mdl is copied in as it is — its material slots can be pointed at other materials below."
                    : $"A {Path.GetExtension(node.GamePath)} file is copied in as it is.");
            return;
        }

        if (!File.Exists(source))
            ImGui.TextColored(Theme.Bad, "That file does not exist.");

        bool image = texture && Path.GetExtension(source).ToLowerInvariant() is not (".tex" or ".atex");
        if (image)
        {
            Gui.Label("Format", "What the image is compressed to in the mod.");
            int index = TextureCompressionInfo.IndexOf(replacement.Compression);
            ImGui.SetNextItemWidth(Theme.S(140));
            if (ComboWheel.Combo("##replcompression", ref index, TextureCompressionInfo.Labels))
            {
                replacement.Compression = TextureCompressionInfo.Order[index];
                _session.TouchReplacements();
            }
            Gui.Tooltip("Auto — compressed, unless that would visibly change it. Then it stays uncompressed.\nBC7 — the game's own choice, best quality, slow to compress.\nBC3 — far quicker, a little softer.\n" +
                       "BC5 — red and green only (colour set / index textures).\nNone — uncompressed, four times the size in memory.");
        }

        ImGui.Spacing();
        if (Gui.IconTextButton(FontAwesomeIcon.Undo, node.Origin == OriginKind.Added ? "Don't add it" : provides ? "Don't provide it" : "Keep the mod's own file"))
        {
            _actions.ClearReplacement(node);
            _typedSource = string.Empty;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // A model's materials
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Each material slot of a model, and the material it loads: the file's own, or another material
    /// of the mod or the game, or a new one made here. The model is written into the mod with only
    /// those names changed.
    /// </summary>
    private void DrawModelMaterials(ResourceNode node)
    {
        ImGui.Spacing();
        ImGui.Spacing();
        Gui.SectionHeader("Materials",
            "Which material each of the model's material slots loads. Point a slot at another material of the mod or the game, or at a new one — " +
            "the model is written into the mod with only those names changed, and the mod's own file is kept.");

        var replacement = _session.FindReplacement(node.GamePath, _session.OptionFor(node));
        var choices = MaterialChoices(node);
        float buttonW = ImGui.GetFrameHeight();
        float spacing = ImGui.GetStyle().ItemInnerSpacing.X;

        for (int slot = 0; slot < node.MaterialSlots.Count; slot++)
        {
            ImGui.PushID(slot);
            var own = node.MaterialSlots[slot];
            var current = node.EffectiveMaterial(slot);
            bool changed = !string.Equals(current, own, StringComparison.Ordinal);

            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(changed ? Theme.Good : Theme.Muted, $"Slot {slot + 1}");
            Gui.Tooltip(changed ? $"Reassigned here. The file has {own}." : "As the file has it.");
            ImGui.SameLine(Theme.S(56));

            // The name, typed: taken once the field is left.
            var key = $"{node.Id}#{slot}";
            var text = _slotText.TryGetValue(key, out var typing) ? typing : current;
            int buttons = changed ? 3 : 2;
            ImGui.SetNextItemWidth(-(buttonW * buttons + spacing * buttons));
            ImGui.InputTextWithHint("##name", own.Length > 0 ? own : "/mt_name.mtrl", ref text, 256);
            if (ImGui.IsItemActive())
                _slotText[key] = text;
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                _slotText.Remove(key);
                _session.ReassignMaterial(node, slot, text);
            }
            else if (!ImGui.IsItemActive())
            {
                _slotText.Remove(key);
            }

            // Picked from the materials the model's set has.
            ImGui.SameLine(0, spacing);
            if (ImGui.BeginCombo("##pick", string.Empty, ImGuiComboFlags.NoPreview | ImGuiComboFlags.HeightLarge))
            {
                if (own.Length > 0 && ImGui.Selectable($"The file's own: {Path.GetFileName(own)}", !changed))
                    _session.ReassignMaterial(node, slot, string.Empty);
                ImGui.Separator();
                foreach (var (name, label, origin) in choices)
                {
                    var reference = own.TrimStart('/').Contains('/') ? name : $"/{Path.GetFileName(name)}";
                    if (ImGui.Selectable($"{label}##{name}", string.Equals(reference, current, StringComparison.OrdinalIgnoreCase)))
                        _session.ReassignMaterial(node, slot, reference);
                    Gui.Tooltip($"{name}\n{origin}");
                }
                if (choices.Count == 0)
                    Gui.Hint("No other material in this model's set.");
                ImGui.EndCombo();
            }
            Gui.Tooltip("Pick a material of the model's set");

            ImGui.SameLine(0, spacing);
            if (Gui.IconButton(FontAwesomeIcon.Plus, "newmtrl", "Make a new material for this slot"))
                _actions.BeginNewMaterial(node, slot);

            if (changed)
            {
                ImGui.SameLine(0, spacing);
                if (Gui.IconButton(FontAwesomeIcon.Undo, "own", $"Back to the file's own: {own}"))
                    _session.ReassignMaterial(node, slot, string.Empty);
            }

            // Where the name leads.
            var resolved = ResourceActions.ResolvedMaterial(node, current);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Theme.S(56));
            if (resolved != null)
            {
                Gui.Icon(ResourceStyle.Icon(resolved.Kind), ResourceStyle.OriginColor(resolved.Origin));
                ImGui.SameLine();
                if (ImGui.Selectable($"{ResourceStyle.OriginLabel(resolved.Origin)} · {resolved.GamePath}##go", false))
                    _session.Focus(resolved.Target);
            }
            else if (current.Length > 0)
            {
                ImGui.TextColored(Theme.Faint, changed && replacement != null ? "read again in a moment" : "not found");
            }
            ImGui.PopID();
        }

        ImGui.Spacing();
        if (Gui.IconTextButton(FontAwesomeIcon.Plus, "New material", "Make a material of the mod's own for this model, from a preset or as a copy of one it uses."))
            _actions.BeginNewMaterial(node, -1);
        if (replacement is { ReassignsMaterials: true })
        {
            ImGui.SameLine();
            if (Gui.IconTextButton(FontAwesomeIcon.Undo, "Every slot as the file has it"))
                _session.ResetMaterials(node);
        }
    }

    /// <summary>
    /// The materials a slot of this model could load: what the mod and the game have in the model's
    /// set (any variant folder), and what the model already uses — by game path, with a label.
    /// </summary>
    private List<(string Path, string Label, string Origin)> MaterialChoices(ResourceNode model)
    {
        var stem = ResourceNaming.MaterialFolderStem(ResourceNaming.FallbackMaterialFolder(model.GamePath));
        var graph = _session.Graph;
        var found = new Dictionary<string, ResourceNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var link in model.Children)
            if (link.Node.Kind == ResourceKind.Material && link.Node.Origin is not (OriginKind.Missing or OriginKind.Shared))
                found.TryAdd(link.Node.GamePath, link.Node);
        foreach (var node in graph.Nodes.Values)
        {
            if (found.Count >= 200)
                break;
            if (node.Kind == ResourceKind.Material && node.Origin is not (OriginKind.Missing or OriginKind.Shared or OriginKind.Swap)
                && node.GamePath.StartsWith(stem, StringComparison.Ordinal))
                found.TryAdd(node.GamePath, node);
        }

        return found.Values
            .OrderBy(n => n.FileName, StringComparer.OrdinalIgnoreCase)
            .Select(n =>
            {
                var where = n.Replacement?.Authored != null ? "made here" : ResourceStyle.OriginLabel(n.Origin);
                return (n.GamePath, $"{n.FileName}  ·  {where}", $"{ResourceStyle.OriginHelp(n.Origin)}");
            })
            .ToList();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Materials made or edited here
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A material can be edited here — starting from the mod's own (or the game's), every texture
    /// kept until swapped — or made anew from a bundled preset.
    /// </summary>
    private void DrawAuthoring(ResourceNode node)
    {
        var option = _session.OptionFor(node);
        var replacement = _session.FindReplacement(node.GamePath, option);
        ImGui.Spacing();
        ImGui.Spacing();

        if (replacement?.Authored is not { } material)
        {
            if (replacement != null)
                return;   // replaced by a file; one or the other

            if (node.Origin is OriginKind.ModFile or OriginKind.Vanilla)
            {
                Gui.SectionHeader("Edit it here",
                    "Change this material without leaving the game: its shader, colour table and textures are kept, and you swap the textures you want " +
                    "for images of your own, add texture slots or take them out. Written beside the original, which is kept.");
                if (Gui.IconTextButton(FontAwesomeIcon.PencilAlt, "Edit this material",
                        node.Origin == OriginKind.ModFile ? "Starts from the mod's own file." : "Starts from the game's own material at this path."))
                    _session.EditMaterial(node);
                ImGui.Spacing();
            }

            Gui.SectionHeader(node.Origin is OriginKind.ModFile or OriginKind.Vanilla ? "Or make a new one here" : "Make it here",
                "Make this material yourself: a shader and texture slots filled with images of your own. The material and its textures are written into the mod.");
            Gui.Label("From a preset");
            float buttonW = Dalamud.Interface.Components.ImGuiComponents.GetIconButtonWithTextWidth(FontAwesomeIcon.Magic, "Make");
            ImGui.SetNextItemWidth(-(buttonW + ImGui.GetStyle().ItemSpacing.X));
            ComboWheel.Combo("##authorpreset", ref _preset, MaterialPresets.Names);
            ImGui.SameLine();
            if (Gui.IconTextButton(FontAwesomeIcon.Magic, "Make", "Start from this preset's shader and texture slots."))
                _session.AuthorMaterial(node.GamePath, option, MaterialPresets.All[Math.Clamp(_preset, 0, MaterialPresets.All.Count - 1)], isNew: !node.IsModContent);
            return;
        }

        bool clones = replacement.CloneModMaterial || replacement.CloneGameMaterial;
        Gui.SectionHeader("Made here", "Written into the mod as you set it up: this material, and each slot's image as its texture. Slots that keep a texture point at it again.");
        Gui.LabeledValue("Starts from", StartsFrom(replacement));

        // The shader: another one starts the material from a bundled preset.
        Gui.Label("Shader", "Which shader the game draws it with.");
        int shader = Math.Max(0, ShaderInfo.All.ToList().FindIndex(d => d.Type == material.ShaderType));
        ImGui.SetNextItemWidth(-1);
        if (ComboWheel.Combo("##shader", ref shader, ShaderInfo.Labels))
        {
            _session.ChangeShader(replacement, ShaderInfo.All[shader].Type);
            if (clones)
                _session.Notify("It starts from a bundled preset of that shader now — the colour table and settings of the original are not kept.", Severity.Warning);
        }
        if (clones)
            Gui.HintWrapped("Another shader starts it from a bundled preset instead: the colour table and settings of the original are not kept.");

        // A preset: its shader and any slots it has that this material lacks; the textures stay.
        Gui.Label("Preset", "Load a preset's shader and texture slots. Its textures and their paths are kept.");
        float loadW = Dalamud.Interface.Components.ImGuiComponents.GetIconButtonWithTextWidth(FontAwesomeIcon.FileImport, "Load");
        ImGui.SetNextItemWidth(-(loadW + ImGui.GetStyle().ItemSpacing.X));
        ComboWheel.Combo("##loadpreset", ref _preset, MaterialPresets.Names);
        ImGui.SameLine();
        if (Gui.IconTextButton(FontAwesomeIcon.FileImport, "Load", "Switch to this preset. Texture slots and their paths are kept; missing slots are added."))
        {
            _session.LoadPreset(replacement, MaterialPresets.All[Math.Clamp(_preset, 0, MaterialPresets.All.Count - 1)]);
            if (clones)
                _session.Notify("The colour table and settings of the original are not kept.", Severity.Warning);
        }

        bool edited = false;
        float removeW = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.X;
        int remove = -1;
        for (int i = 0; i < material.Textures.Count; i++)
        {
            var tex = material.Textures[i];
            ImGui.PushID(i);
            ImGui.Spacing();
            if (tex.IsShared)
            {
                DrawKeptThumb(tex.SharedGamePath, option);
                ImGui.SameLine();
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(TextureTypeInfo.DisplayName(tex.Type));
                ImGui.SameLine();
                ImGui.TextColored(Theme.Faint, $"keeps {Path.GetFileName(tex.SharedGamePath)}");
                Gui.Tooltip(tex.SharedGamePath);
                DrawKeptSlot(node.GamePath, material, tex, removeW);
            }
            else
            {
                _textures.DrawThumb(tex, _host.Configuration.ShowThumbnails);
                ImGui.SameLine();
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(TextureTypeInfo.DisplayName(tex.Type));
                ImGui.SameLine();
                ImGui.TextColored(Theme.Faint, Path.GetFileName(ResourceNaming.SlotTexturePath(node.GamePath, tex)));
                edited |= _textures.DrawCompact(tex, removeW);
            }
            ImGui.SameLine();
            if (Gui.IconButton(FontAwesomeIcon.Trash, "removeslot", "Take this texture slot out of the material", danger: true))
                remove = i;
            DrawTexturePath(node.GamePath, tex);
            ImGui.PopID();
        }
        if (remove >= 0)
        {
            material.Textures.RemoveAt(remove);
            edited = true;
        }
        if (edited)
            _session.TouchReplacements();

        ImGui.Spacing();
        DrawAddSlot(node.GamePath, material);

        ImGui.Spacing();
        if (Gui.IconTextButton(FontAwesomeIcon.Undo, node.Origin == OriginKind.ModFile ? "Keep the mod's own material" : "Don't make it here"))
            _actions.ClearReplacement(node);
    }

    /// <summary>
    /// The game path a slot's texture is at, editable: a kept slot reads the texture there, a slot with
    /// an image of its own is written there. Taken when the field is left or Enter is pressed.
    /// </summary>
    private void DrawTexturePath(string materialGamePath, TextureSlot tex)
    {
        var path = _pathSlot == tex ? _pathText : tex.IsShared ? tex.SharedGamePath : ResourceNaming.SlotTexturePath(materialGamePath, tex);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##texpath", ref path, 512);
        bool active = ImGui.IsItemActive();
        bool done = ImGui.IsItemDeactivatedAfterEdit();
        Gui.Tooltip(tex.IsShared
            ? "The texture this slot reads. Type another game path to point it there."
            : "Where this slot's image is written in the mod. Leave it empty for the usual path.");
        if (active)
        {
            _pathSlot = tex;
            _pathText = path;
        }
        else if (_pathSlot == tex)
        {
            _pathSlot = null;
            if (done)
                _session.SetTexturePath(materialGamePath, tex, path);
        }
    }

    private static string StartsFrom(ResourceReplacement replacement)
    {
        bool own = PathNormalizer.SameGamePath(replacement.TemplateGamePath, replacement.GamePath);
        var other = Path.GetFileName(replacement.TemplateGamePath);
        if (replacement.CloneModMaterial)
            return own ? "the mod's own material" : $"a copy of {other}";
        if (replacement.CloneGameMaterial)
            return own ? "the game's own material at this path" : $"a copy of the game's {other}";
        return "a bundled preset";
    }

    /// <summary>
    /// A slot that keeps the texture the material had: an image picked or dropped takes its place, at
    /// a texture path of its own (whatever else uses the kept texture keeps it).
    /// </summary>
    private void DrawKeptSlot(string materialPath, MaterialSetup material, TextureSlot tex, float reserveRight)
    {
        string typed = string.Empty;
        void Take(string path) => _session.SwapKeptTexture(materialPath, material, tex, path);

        var drop = new FileDropRule(FileDrop.RejectForTexture, paths =>
        {
            if (paths.Files.Count > 0)
                Take(paths.Files[0]);
        });
        if (Gui.PathPicker("keep", ref typed, "kept — pick or drop an image to swap it", PathKind.File, ImageFilter, Take,
                reserveRight: reserveRight, drop: drop)
            && File.Exists(typed) && ResourceKinds.Accepts("x/x.tex", typed))
        {
            Take(typed);
        }
    }

    private void DrawKeptThumb(string gamePath, OptionRef option)
    {
        if (!_host.Configuration.ShowThumbnails)
        {
            Gui.Icon(FontAwesomeIcon.Link, Theme.Accent);
            return;
        }
        float side = ImGui.GetFrameHeight();
        var node = _session.Graph.Find(gamePath, option);
        if (node is { Origin: OriginKind.ModFile })
            _thumbs.DrawFile(node.DiskPath, side);
        else if (node is { Origin: OriginKind.Added } && File.Exists(node.DiskPath))
            _thumbs.DrawFile(node.DiskPath, side);
        else
            _thumbs.DrawGame(PathNormalizer.GamePath(gamePath), side);
    }

    /// <summary>Adds a texture slot of a role the material has none of yet — the shader's usual ones first.</summary>
    private void DrawAddSlot(string materialPath, MaterialSetup material)
    {
        var used = material.Textures.Select(t => t.Type).ToHashSet();
        var usual = ShaderInfo.SupportedTextures(material.ShaderType).Where(t => !used.Contains(t)).ToList();
        var unusual = Enum.GetValues<TextureType>().Where(t => !used.Contains(t) && !usual.Contains(t)).ToList();
        if (usual.Count + unusual.Count == 0)
            return;

        ImGui.SetNextItemWidth(Theme.S(200));
        if (!ImGui.BeginCombo("##addslot", "+ Texture slot"))
            return;
        foreach (var type in usual)
            if (ImGui.Selectable(TextureTypeInfo.DisplayName(type)))
                _session.AddTextureSlot(materialPath, material, type);
        if (unusual.Count > 0)
        {
            if (usual.Count > 0)
                ImGui.Separator();
            ImGui.TextColored(Theme.Muted, $"Not normally used by {ShaderInfo.DisplayName(material.ShaderType)}");
            foreach (var type in unusual)
            {
                if (ImGui.Selectable($"{TextureTypeInfo.DisplayName(type)}##unusual"))
                    _session.AddTextureSlot(materialPath, material, type);
                Gui.Tooltip("Added to the material with its own sampler — the shader may ignore it unless it is set up to read it.");
            }
        }
        ImGui.EndCombo();
    }


    private void DrawLinks(string title, List<(ResourceNode Node, string? Role)> links)
    {
        if (links.Count == 0)
            return;

        ImGui.Spacing();
        ImGui.Spacing();
        Gui.SectionHeader($"{title} ({links.Count})");
        var meta = _session.Project?.Meta;
        for (int i = 0; i < links.Count && i < 200; i++)
        {
            var (node, role) = links[i];
            ImGui.PushID(i);
            Gui.Icon(ResourceStyle.Icon(node.Kind), ResourceStyle.OriginColor(node.Origin));
            ImGui.SameLine();
            var label = role != null ? $"{role}: {node.FileName}" : node.FileName;
            if (ImGui.Selectable(label, false))
                _session.Focus(node.Target);
            var where = node.IsModContent && meta != null ? meta.OptionLabel(node.Option) : ResourceStyle.OriginLabel(node.Origin);
            Gui.Tooltip($"{node.GamePath}\n{where}");
            ImGui.PopID();
        }
    }
}
