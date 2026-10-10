using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using InstantEdit.Studio.Models;
using InstantEdit.Studio.Windows.Panels;
using InstantEdit.Studio.Windows.UI;

namespace InstantEdit.Studio.Windows.Studio;

/// <summary>
/// Edit mode's "Add from game" view: the game's item browser, adding gear or a character feature to
/// the open mod. An added item's models are listed in the mod's tree with the game's materials and
/// textures under them, and files dropped on them go into the option picked here.
/// </summary>
internal sealed class ModBrowseView
{
    private readonly StudioHost _host;
    private readonly ModSession _session;
    private readonly ItemBrowserPanel _browser;

    public ModBrowseView(StudioHost host, ModSession session)
    {
        _host  = host;
        _session = session;
        _browser = new ItemBrowserPanel(host, session);
    }

    public void DrawCanvas() => _browser.Draw();

    public void DrawInspector()
    {
        var project = _session.Project;
        if (project == null)
            return;

        var meta = project.Meta;
        Gui.SectionHeader("Into", "Which part of the mod the files you put on an added item go into: its default files (always on), or one option of a group.");
        var option = _session.AddInto;
        if (!meta.OptionExists(option))
            option = _session.AddInto = Services.Project.OptionRef.Default;
        if (ResourceActions.DrawOptionCombo("##addinto", meta, ref option))
            _session.AddInto = option;

        ImGui.Spacing();
        ImGui.Spacing();
        var subject = _browser.Highlighted;
        if (subject == null)
        {
            Gui.HintWrapped("Click a row to see what the game ships for it. Press + on it, or drag it onto the list on the left, to add it to the mod.");
            return;
        }

        var added = _session.FindAddedItem(subject.Key);
        Gui.SectionHeader(subject.DisplayName, added != null ? "Already added to the mod." : null);
        if (subject is GearSubject gear)
        {
            Gui.LabeledValue("Slot", SlotInfo.DisplayLabelMap[gear.Item.Slot]);
            Gui.LabeledValue("Model", gear.Item.ModelIdDisplay, "Model set id and variant.");
        }
        else if (subject is FeatureSubject feature)
        {
            Gui.LabeledValue("Race", feature.Race.DisplayName);
            Gui.LabeledValue("Id", feature.IdDisplay);
        }
        Gui.LabeledValue("Materials", subject.MaterialFolder(subject.BaseRace));
        Gui.LabeledValue("Textures", subject.TextureFolder);

        if (subject.MultiRace)
        {
            ImGui.Spacing();
            var native = _host.GameData.GetNativeRaces(subject);
            ImGui.TextColored(Theme.Muted, subject is GearSubject ? "Races with their own model (each is listed)" : "Races that have this id (each is listed)");
            if (ImGui.BeginTable("##AddRaces", 2, ImGuiTableFlags.SizingStretchSame))
            {
                foreach (var rg in RaceInfo.AllRaces)
                {
                    ImGui.TableNextColumn();
                    bool has = native.Contains(rg);
                    Gui.Icon(has ? FontAwesomeIcon.Check : FontAwesomeIcon.Minus, has ? Theme.Good : Theme.Faint);
                    ImGui.SameLine();
                    ImGui.TextColored(has ? Theme.Muted : Theme.Faint, rg.DisplayName);
                }
                ImGui.EndTable();
            }
        }

        ImGui.Spacing();
        ImGui.Spacing();
        if (added != null)
        {
            if (Gui.IconTextButton(FontAwesomeIcon.ArrowRight, "Open it", "Show its files in the mod's tree."))
                _session.FocusItem(added);
        }
        else if (Gui.PrimaryButton(FontAwesomeIcon.Plus, "Add to the mod", Theme.S(170),
                     $"Lists its game files in the mod, ready for files of your own — they go into {meta.OptionLabel(_session.AddInto)}."))
        {
            _session.AddItem(subject, _session.AddInto);
        }
    }
}
