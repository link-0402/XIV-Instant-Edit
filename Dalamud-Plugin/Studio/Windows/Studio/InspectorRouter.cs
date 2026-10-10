using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Models.Resources;
using InstantEdit.Studio.Windows.Panels;
using InstantEdit.Studio.Windows.UI;

namespace InstantEdit.Studio.Windows.Studio;

/// <summary>
/// Edit mode's right-hand pane: whatever is selected, in full. The mod itself, an entry of the
/// contents pane, or one of the mod's files.
/// </summary>
internal sealed class InspectorRouter
{
    private readonly ModSession _session;
    private readonly ResourceInspector _resources;
    private readonly ProjectInspector _project;
    private readonly OptionInspector _options;
    private readonly ResourceActions _actions;

    public InspectorRouter(ModSession session, ResourceInspector resources, ProjectInspector project, OptionInspector options, ResourceActions actions)
    {
        _session   = session;
        _resources = resources;
        _project   = project;
        _options   = options;
        _actions   = actions;
    }

    public void Draw()
    {
        switch (_session.Selection)
        {
            case null:
                Gui.HintWrapped("Pick the mod, an entry on the left, or a file in the tree to see it here.");
                break;

            case { Kind: TargetKind.Project }:
                _project.Draw();
                break;

            // An option picked in a group's list, or a group, or an option's entry.
            case { IsResourceRoot: true } picked when ResourceGraph.GroupOf(picked.Root) is { } groupId:
                if (!string.IsNullOrEmpty(picked.Option))
                    _options.DrawOption(Services.Project.OptionRef.Parse(picked.Option));
                else if (_session.Project?.Meta.FindGroupView(groupId) is { } group)
                    _options.DrawGroup(group);
                else
                    Gui.Hint("This group is no longer in the mod.");
                break;

            case { IsResourceRoot: true } picked when ResourceGraph.OptionOf(picked.Root) is { } option:
                _options.DrawOption(option);
                break;

            case { IsResourceRoot: true } root:
                if (_session.Graph.FindRoot(root.Root) is { } entry)
                    DrawRoot(entry);
                else
                    Gui.Hint("This is no longer in the mod.");
                break;

            case { Kind: TargetKind.Resource } target:
                if (_session.Graph.Find(target) is { } node)
                    _resources.Draw(node);
                else
                    Gui.Hint(_session.Graph.Roots.Count == 0 ? "Reading the mod's files" : "This file is no longer in the mod.");
                break;

            default:
                Gui.Hint("Nothing to show.");
                break;
        }
    }

    /// <summary>An entry of the contents pane: what it is, how much is in it, and where its files come from.</summary>
    private void DrawRoot(ResourceRoot root)
    {
        using (Fonts.Title())
            ImGui.TextUnformatted(root.Title);
        if (root.Subtitle.Length > 0)
            ImGui.TextColored(Theme.Muted, root.Subtitle);
        ImGui.Separator();
        ImGui.Spacing();

        Gui.LabeledValue("Kind", ResourceKinds.SectionLabel(root.Section));
        Gui.LabeledValue("Files", root.FileCount.ToString());
        if (root.ReplacedCount > 0)
            Gui.LabeledValue("Replaced", $"{root.ReplacedCount}, with files of your own");
        if (root.MissingCount > 0)
        {
            Gui.Label("Missing");
            ImGui.TextColored(Theme.Bad, $"{root.MissingCount} referenced file(s) neither the mod nor the game has");
        }

        var meta = _session.Project?.Meta;
        if (meta != null && root.Options.Count > 0)
        {
            ImGui.Spacing();
            Gui.SectionHeader("Comes from", "The parts of the mod its files are in. A file in an option is only used while that option is picked in Penumbra.");
            foreach (var option in root.Options)
            {
                Gui.Icon(option.IsDefault ? FontAwesomeIcon.CheckCircle : FontAwesomeIcon.ListUl, option.IsDefault ? Theme.Good : Theme.Muted);
                ImGui.SameLine();
                ImGui.TextUnformatted(meta.OptionLabel(option));
            }
        }

        if (root.Section == ResourceSection.Metadata && meta != null)
        {
            ImGui.Spacing();
            Gui.SectionHeader("Manipulations", "Penumbra's metadata edits: what gear hides, which races use which files, skeletons, variants");
            foreach (var group in meta.ManipulationCounts().GroupBy(m => m.Type).OrderBy(g => g.Key))
                ImGui.BulletText($"{group.Sum(m => m.Count)} × {group.Key}");
        }

        var added = _session.AddedItemsIn(root);
        if (added.Count > 0 && meta != null)
        {
            ImGui.Spacing();
            Gui.SectionHeader("Added from the game",
                "Gear or a character feature added to the mod from the game: its models are listed with the game's materials and textures, " +
                "ready for files of your own. What you put there goes into the option shown.");
            foreach (var item in added)
            {
                ImGui.PushID((int)item.Key);
                Gui.Icon(FontAwesomeIcon.Tshirt, Theme.Accent);
                ImGui.SameLine();
                ImGui.TextUnformatted(item.Name);
                ImGui.SameLine();
                ImGui.TextColored(Theme.Faint, meta.OptionLabel(Services.Project.OptionRef.Parse(item.Option)));
                ImGui.SameLine();
                if (Gui.IconButton(FontAwesomeIcon.Times, "removeitem", $"Take {item.Name} back out of the mod", danger: true))
                    _actions.AskRemoveItem(item);
                ImGui.PopID();
            }
        }

        if (root.Section == ResourceSection.Models && root.Nodes.FirstOrDefault(n => n.Kind == ResourceKind.Model && n.MaterialSlots.Count > 0) is { } model)
        {
            ImGui.Spacing();
            if (Gui.IconTextButton(FontAwesomeIcon.Palette, "New material",
                    "Make a material of the mod's own for this set, from a preset or as a copy of one its models use. Point a model's slot at it from the model's inspector."))
                _actions.BeginNewMaterial(model, -1);
        }

        if (root.Section != ResourceSection.Metadata)
        {
            ImGui.Spacing();
            ImGui.Spacing();
            var zone = DropZone.Draw("rootadd", "Drop files to add them here, or click to pick", FontAwesomeIcon.Plus,
                new Vector2(ImGui.GetContentRegionAvail().X, Theme.T(56)),
                new FileDropRule(UI.FileDrop.RejectForAdding, _ => { }));
            if (zone.Dropped is { } dropped)
                _actions.BeginAdd(dropped.Files, root);
            else if (zone.Clicked)
                _actions.BrowseAdd(root);
        }
    }
}
