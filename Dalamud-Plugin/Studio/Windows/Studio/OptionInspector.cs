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
/// The inspector for the mod's structure: one option (or the default files) — its name and
/// description as Penumbra shows them, whether it is in use, what it carries — or one group with
/// its options. Names and descriptions are written into meta.json when the field is left.
/// </summary>
internal sealed class OptionInspector
{
    private readonly StudioHost _host;
    private readonly ModEditor _editor;
    private readonly ModSession _session;
    private readonly ResourceActions _actions;

    // The text being edited, and what it belongs to; taken from meta.json again whenever that changes.
    private string _editingKey = string.Empty;
    private int _editingRevision = -1;
    private string _name = string.Empty;
    private string _description = string.Empty;

    public OptionInspector(ModEditor editor, ModSession session, ResourceActions actions)
    {
        _host  = editor.Host;
        _editor = editor;
        _session = session;
        _actions = actions;
    }

    /// <summary>Takes the fields from meta.json when another thing is picked, or meta.json changed while they are not being typed in.</summary>
    private void Sync(PenumbraModMeta meta, string key, string name, string description)
    {
        if (_editingKey == key && (_editingRevision == meta.Revision || ImGui.IsAnyItemActive()))
            return;
        _editingKey      = key;
        _editingRevision = meta.Revision;
        _name            = name;
        _description     = description;
    }

    public void DrawOption(OptionRef option)
    {
        if (_session.Project is not { } project)
            return;
        var meta = project.Meta;
        if (!meta.OptionExists(option))
        {
            Gui.Hint("This option is no longer in the mod.");
            return;
        }
        var group = option.IsDefault ? null : meta.FindGroupView(option.Group);
        var root = _session.Graph.OptionRoot(option);
        bool named = group is { Type: ModGroupType.Single or ModGroupType.Multi };
        Sync(meta, $"opt:{option.Serialize()}", meta.OptionName(option), meta.OptionDescription(option));

        using (Fonts.Title())
            ImGui.TextUnformatted(option.IsDefault ? "Default files" : meta.OptionName(option));
        ImGui.TextColored(Theme.Muted, option.IsDefault ? "Always in use while the mod is enabled." : $"An option of {group?.Name}");
        ImGui.Separator();
        ImGui.Spacing();

        if (named)
        {
            Gui.SectionHeader("Option", "Written into the mod's meta.json when you leave the field; Penumbra reads it again then.");
            Gui.Label("Name");
            ImGui.SetNextItemWidth(-1);
            ImGui.InputText("##optname", ref _name, 128);
            if (ImGui.IsItemDeactivatedAfterEdit() && _name.Trim().Length > 0 && _name.Trim() != meta.OptionName(option))
            {
                if (group!.Options.Any(o => o.Ref != option && string.Equals(o.Name, _name.Trim(), System.StringComparison.OrdinalIgnoreCase)))
                    _session.Notify($"{group.Name} already has an option called {_name.Trim()}.", Severity.Warning);
                else
                    _session.RenameOption(option, _name);
            }

            Gui.Label("Description", "What Penumbra shows for the option when it is hovered.");
            ImGui.InputTextMultiline("##optdescription", ref _description, 4096,
                new Vector2(-1, ImGui.GetTextLineHeight() * 4 + ImGui.GetStyle().FramePadding.Y * 2));
            if (ImGui.IsItemDeactivatedAfterEdit())
                _session.SetOptionDescription(option, _description);

            Gui.Label("Group");
            if (ImGui.Selectable($"{group!.Name}##opengroup", false, ImGuiSelectableFlags.None, new Vector2(0, 0)))
                _session.FocusGroup(group.Id);
            Gui.Tooltip($"{ResourceStyle.GroupTypeHelp(group.Type)}\nClick to see all of its options.");
            ImGui.Spacing();
        }
        else if (group != null)
        {
            Gui.LabeledValue("Group", group.Name, ResourceStyle.GroupTypeHelp(group.Type));
            ImGui.TextColored(Theme.Faint, "Options of this kind of group are named and arranged in Penumbra.");
            ImGui.Spacing();
        }

        var settings = _editor.Settings;
        if (settings.IsOn(option) is { } on)
        {
            Gui.Label("For you");
            ImGui.TextColored(on ? Theme.Good : Theme.Muted, on
                ? $"in use, in {settings.Collection}"
                : settings.Enabled == false ? $"not in use — the mod is disabled in {settings.Collection}" : $"not picked in {settings.Collection}");
        }

        ImGui.Spacing();
        Gui.SectionHeader("In it");
        Row(FontAwesomeIcon.File, $"{root?.FileCount ?? 0} file(s)");
        if (root is { SwapCount: > 0 })
            Row(FontAwesomeIcon.ExchangeAlt, $"{root.SwapCount} file swap(s)");
        if (root is { ManipulationCount: > 0 })
            Row(FontAwesomeIcon.SlidersH, $"{root.ManipulationCount} manipulation(s)");
        int yours = project.Sidecar.Replacements.Count(r => OptionRef.Parse(r.Option) == option);
        if (yours > 0)
            Row(FontAwesomeIcon.CheckCircle, $"{yours} file(s) of your own replacing or added");
        foreach (var item in project.Sidecar.AddedItems.Where(i => OptionRef.Parse(i.Option) == option))
            Row(FontAwesomeIcon.Tshirt, $"{item.Name}, added from the game");
        if (root is { MissingCount: > 0 })
        {
            Gui.Icon(FontAwesomeIcon.ExclamationTriangle, Theme.Bad);
            ImGui.SameLine(Theme.S(28));
            ImGui.TextColored(Theme.Bad, $"{root.MissingCount} referenced file(s) neither the mod nor the game has");
        }

        ImGui.Spacing();
        ImGui.Spacing();
        var zone = DropZone.Draw("optionadd", "Drop files to add them here, or click to pick", FontAwesomeIcon.Plus,
            new Vector2(ImGui.GetContentRegionAvail().X, Theme.T(56)), new FileDropRule(UI.FileDrop.RejectForAdding, _ => { }));
        if (zone.Dropped is { } dropped)
            _actions.BeginAdd(dropped.Files, root, option);
        else if (zone.Clicked)
            _actions.BrowseAdd(root, option);

        if (ImGui.Button("Add gear from the game here", new Vector2(-1, 0)))
        {
            _session.AddInto = option;
            _session.GoToView(StudioView.Browse);
        }
        Gui.Tooltip("Opens the game's items: what you add is listed here, ready for files of your own.");

        if (named)
        {
            ImGui.Spacing();
            ImGui.Spacing();
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Bad);
            if (ImGui.Button("Take this option out of the mod"))
                _actions.AskRemoveOption(option);
            ImGui.PopStyleColor();
        }
    }

    public void DrawGroup(ModGroupView group)
    {
        if (_session.Project is not { } project)
            return;
        var meta = project.Meta;
        Sync(meta, $"grp:{group.Id}", group.Name, string.Empty);

        using (Fonts.Title())
            ImGui.TextUnformatted(group.Name);
        ImGui.TextColored(Theme.Muted, ResourceStyle.GroupTypeHelp(group.Type));
        ImGui.Separator();
        ImGui.Spacing();

        Gui.SectionHeader("Group", "Written into the mod's meta.json when you leave the field; Penumbra reads it again then.");
        Gui.Label("Name");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##groupname", ref _name, 128);
        if (ImGui.IsItemDeactivatedAfterEdit() && _name.Trim().Length > 0 && _name.Trim() != group.Name)
        {
            if (meta.Groups.Any(g => g.Id != group.Id && string.Equals(g.Name, _name.Trim(), System.StringComparison.OrdinalIgnoreCase)))
                _session.Notify("The mod already has a group with that name.", Severity.Warning);
            else
                _session.RenameGroup(group.Id, _name);
        }
        Gui.Tooltip("Collections remember what is picked in a group by its name: after a rename it may need picking again.");
        Gui.LabeledValue("Players pick", ResourceStyle.GroupTypeLabel(group.Type), ResourceStyle.GroupTypeHelp(group.Type));

        ImGui.Spacing();
        Gui.SectionHeader(group.Type == ModGroupType.Combining ? $"Combinations ({group.FileSets.Count})" : $"Options ({group.Options.Count})");
        var settings = _editor.Settings;
        var sets = group.Type == ModGroupType.Imc ? group.Options : group.FileSets;
        foreach (var set in sets.Take(300))
        {
            ImGui.PushID(set.Ref.Serialize());
            bool? on = settings.IsOn(set.Ref);
            Gui.Icon(ResourceStyle.OptionIcon(group.Type), on == true ? Theme.Good : Theme.Faint);
            ImGui.SameLine(Theme.S(28));
            if (ImGui.Selectable(set.Name, false) && group.Type != ModGroupType.Imc)
                _session.FocusOption(set.Ref);
            if (group.Type != ModGroupType.Imc && _session.Graph.OptionRoot(set.Ref) is { } root)
            {
                ImGui.SameLine();
                ImGui.TextColored(Theme.Faint, $"{root.FileCount} file(s)");
            }
            ImGui.PopID();
        }
        if (sets.Count > 300)
            ImGui.TextColored(Theme.Faint, $"and {sets.Count - 300} more");

        ImGui.Spacing();
        var why = PenumbraModMeta.WhyNoNewOption(group);
        using (Gui.Disabled(why != null))
        {
            if (Gui.IconTextButton(FontAwesomeIcon.Plus, "Add an option", why))
                _actions.BeginNewOption(group.Id);
        }
        if (why != null)
            ImGui.TextColored(Theme.Faint, why);
        else
        {
            ImGui.Spacing();
            var zone = DropZone.Draw("groupadd", "Drop files to make one new option of each, or click to pick", FontAwesomeIcon.Plus,
                new Vector2(ImGui.GetContentRegionAvail().X, Theme.T(56)), new FileDropRule(UI.FileDrop.RejectForAdding, _ => { }));
            if (zone.Dropped is { } dropped)
                _actions.BeginAddToGroup(dropped.Files, group.Id);
            else if (zone.Clicked)
                _actions.BrowseAddToGroup(group.Id);
        }

        ImGui.Spacing();
        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Bad);
        if (ImGui.Button("Take this group out of the mod"))
            _actions.AskRemoveGroup(group.Id);
        ImGui.PopStyleColor();
    }

    private static void Row(FontAwesomeIcon icon, string text)
    {
        Gui.Icon(icon, Theme.Muted);
        ImGui.SameLine(Theme.S(28));
        ImGui.TextUnformatted(text);
    }
}
